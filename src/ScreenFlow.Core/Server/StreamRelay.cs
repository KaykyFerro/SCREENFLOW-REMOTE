using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using ScreenFlow.Core.Diagnostics;
using ScreenFlow.Core.Media;
using ScreenFlow.Core.Network;
using ScreenFlow.Core.Protocol;
using ScreenFlow.Core.Security;

namespace ScreenFlow.Core.Server;

/// <summary>Uma transmissão no servidor, para a tela do servidor e o console.</summary>
public sealed record ServerStreamInfo(Guid Id, string HostName, StreamMode Mode, bool Online, int Viewers, IReadOnlyList<string> ViewerNames,
    string Video, double InMbps, double OutMbps, double LossRate);

/// <summary>
/// O repasse das transmissões no servidor (1.4.1). Quem transmite manda uma cópia só para o servidor, e o servidor manda
/// para cada pessoa que assiste, cifrado para cada uma. O servidor não decodifica nada: abre cada pacote, guarda o vídeo
/// por alguns segundos (para atender os NACKs na hora) e cifra de novo para cada pessoa.
/// </summary>
/// <remarks>
/// Para quem transmite, o servidor é uma pessoa assistindo (com o pior caso de quem está assistindo por ele: o codec que
/// todos decodificam, a menor tela, o pior RTT e a pior perda). Para quem assiste, o servidor é o host: o handshake, as
/// mensagens e os pacotes são os mesmos de uma conexão direta.
/// </remarks>
public sealed class StreamRelay : IAsyncDisposable
{
    private static readonly VideoCodec[] AllCodecs = [VideoCodec.Av1, VideoCodec.Hevc, VideoCodec.H264];
    private readonly ServerOptions _options;
    private readonly ConcurrentDictionary<Guid, RelayedStream> _streams = new();
    private readonly ConcurrentDictionary<SocketAddress, RelayedStream> _sources = new();
    private readonly ConcurrentDictionary<string, (DateTimeOffset Start, int Count)> _failures = new();
    private readonly CancellationTokenSource _cancellation = new();
    private readonly object _gate = new();
    private TcpListener? _listener;
    private Socket? _media;
    private MediaSender? _sender;
    private Thread? _receiver;
    private Task? _acceptLoop;
    private Task? _timerLoop;

    public StreamRelay(ServerOptions options) => _options = options;

    /// <summary>A porta TCP de verdade (com a porta 0 nas opções, a que o sistema escolheu).</summary>
    public int ControlPort => (_listener?.LocalEndpoint as IPEndPoint)?.Port ?? _options.ControlPort;

    public int MediaPort => (_media?.LocalEndPoint as IPEndPoint)?.Port ?? _options.MediaPort;

    /// <summary>Uma transmissão entrou, saiu, caiu ou mudou quem assiste. Chamado fora de qualquer thread de interface.</summary>
    public event Action? Changed;

    public IReadOnlyList<ServerStreamInfo> Streams => _streams.Values.Select(s => s.Info()).OrderBy(s => s.HostName, StringComparer.CurrentCultureIgnoreCase).ToArray();

    /// <summary>Os anúncios das transmissões no ar, para a sala do servidor mandar para todo mundo.</summary>
    public IReadOnlyList<DiscoveryAnnouncement> Announcements()
        => _streams.Values.Where(s => s.Upstream is not null).Select(s => new DiscoveryAnnouncement(AppInfo.Name, AppInfo.ProtocolVersion, s.HostName, "0.0.0.0",
            ControlPort, s.HostVersion, _options.HasPassword, s.Viewers, s.Mode, s.CameraOverlay, s.Id.ToString("N"), _options.Name)).ToArray();

    public void Start()
    {
        _media = MediaSender.CreateUdp(_options.BindAddress, _options.MediaPort);
        _sender = new MediaSender(_media);
        _listener = new TcpListener(_options.BindAddress, _options.ControlPort);
        _listener.Start(64);
        _receiver = new Thread(ReceiveLoop) { IsBackground = true, Name = "ScreenFlow servidor: mídia", Priority = ThreadPriority.Highest };
        _receiver.Start();
        _acceptLoop = Task.Run(AcceptLoopAsync);
        _timerLoop = Task.Run(TimerLoopAsync);
        Log.Info($"Servidor: transmissões na porta TCP {ControlPort} e mídia na porta UDP {MediaPort}.");
    }

    public async ValueTask DisposeAsync()
    {
        if (_cancellation.IsCancellationRequested) return;
        foreach (var stream in _streams.Values)
            foreach (var subscriber in stream.Subscribers.Values)
                try { await subscriber.SendAsync(new ByeMessage("O servidor foi desligado."), CancellationToken.None).WaitAsync(TimeSpan.FromMilliseconds(300)); } catch { }
        _cancellation.Cancel();
        _listener?.Stop();
        foreach (var stream in _streams.Values) stream.Dispose();
        _streams.Clear();
        _media?.Dispose();
        _receiver?.Join(1000);
        if (_acceptLoop is not null) try { await _acceptLoop; } catch { }
        if (_timerLoop is not null) try { await _timerLoop; } catch { }
        _cancellation.Dispose();
    }

    // ───────────────────────── Conexões TCP ─────────────────────────

    private async Task AcceptLoopAsync()
    {
        var token = _cancellation.Token;
        while (!token.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _listener!.AcceptTcpClientAsync(token).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (SocketException exception) { Log.Warn($"Servidor: accept: {exception.Message}"); continue; }
            _ = Task.Run(() => HandleConnectionAsync(client, token));
        }
    }

    private async Task HandleConnectionAsync(TcpClient client, CancellationToken token)
    {
        client.NoDelay = true;
        var remote = (IPEndPoint)client.Client.RemoteEndPoint!;
        try
        {
            var stream = client.GetStream();
            using var handshake = CancellationTokenSource.CreateLinkedTokenSource(token);
            handshake.CancelAfter(TimeSpan.FromSeconds(15));
            var first = await ControlFraming.ReadAsync(stream, handshake.Token).ConfigureAwait(false);
            switch (first)
            {
                case PublishMessage publish:
                    await HandlePublisherAsync(client, stream, remote, publish, token).ConfigureAwait(false);
                    break;
                case HelloMessage hello:
                    await HandleSubscriberAsync(client, stream, remote, hello, token).ConfigureAwait(false);
                    break;
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) when (exception is IOException or SocketException or InvalidDataException or System.Text.Json.JsonException or ObjectDisposedException)
        {
            Log.Debug($"Servidor: conexão com {remote.Address} encerrada: {exception.Message}");
        }
        catch (Exception exception)
        {
            Log.Error($"Servidor: erro com {remote.Address}", exception);
        }
        finally
        {
            client.Dispose();
        }
    }

    /// <summary>Confere a senha do servidor (Challenge/Auth) e devolve o segredo ECDH, ou null se recusou.</summary>
    private async Task<(byte[] Secret, byte[] Transcript)?> AuthenticateAsync(NetworkStream stream, string ip, string peerPublicKey, string hostName, CancellationToken token)
    {
        if (IsThrottled(ip))
        {
            await ControlFraming.WriteAsync(stream, new RejectedMessage(RejectedMessage.Throttled, "Muitas tentativas com a senha errada. Aguarde um minuto."), token).ConfigureAwait(false);
            return null;
        }
        using var exchange = new KeyExchange();
        var nonce = SessionCrypto.CreateNonce();
        await ControlFraming.WriteAsync(stream, new ChallengeMessage(hostName, exchange.PublicKey, nonce, _options.HasPassword), token).ConfigureAwait(false);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(60)); // dá tempo de digitar a senha
        if (await ControlFraming.ReadAsync(stream, timeout.Token).ConfigureAwait(false) is not AuthMessage auth) return null;
        byte[] secret;
        try { secret = exchange.DeriveSharedSecret(peerPublicKey); }
        catch (Exception exception) when (exception is FormatException or CryptographicException) { return null; }
        var transcript = SessionCrypto.Transcript(peerPublicKey, exchange.PublicKey, nonce);
        if (!SessionCrypto.VerifyProof(secret, _options.Password, transcript, auth.Proof))
        {
            CryptographicOperations.ZeroMemory(secret);
            RegisterFailure(ip);
            Log.Warn($"Servidor: senha errada de {ip}.");
            await ControlFraming.WriteAsync(stream, new RejectedMessage(RejectedMessage.WrongPin, "Senha do servidor incorreta."), token).ConfigureAwait(false);
            return null;
        }
        _failures.TryRemove(ip, out _);
        return (secret, transcript);
    }

    // ───────────────────────── Quem transmite ─────────────────────────

    private async Task HandlePublisherAsync(TcpClient client, NetworkStream net, IPEndPoint remote, PublishMessage publish, CancellationToken token)
    {
        var ip = remote.Address.ToString();
        if (publish.Protocol != AppInfo.ProtocolVersion)
        {
            await ControlFraming.WriteAsync(net, new RejectedMessage(RejectedMessage.VersionMismatch,
                $"Versões diferentes do ScreenFlow (servidor {AppInfo.DisplayVersion}, você {publish.AppVersion}). Atualize os dois."), token).ConfigureAwait(false);
            return;
        }
        if (!Guid.TryParse(publish.StreamId, out var id) || id == Guid.Empty) return;
        if (!_streams.ContainsKey(id) && _streams.Count >= _options.MaxStreams)
        {
            await ControlFraming.WriteAsync(net, new RejectedMessage(RejectedMessage.Busy, $"O servidor já tem {_options.MaxStreams} transmissões."), token).ConfigureAwait(false);
            return;
        }
        // Enquanto a transmissão existe (no ar ou esperando voltar), só o mesmo PC pode transmitir com o mesmo id.
        if (_streams.TryGetValue(id, out var current) && current.LastAddress is { } last && !last.Equals(remote.Address))
        {
            await ControlFraming.WriteAsync(net, new RejectedMessage(RejectedMessage.Busy, "Outro PC já está transmitindo com este id no servidor."), token).ConfigureAwait(false);
            return;
        }
        var hostName = CleanName(publish.HostName, ip);
        if (await AuthenticateAsync(net, ip, publish.PublicKey, _options.Name, token).ConfigureAwait(false) is not { } auth) return;
        CryptographicOperations.ZeroMemory(auth.Secret);
        await ControlFraming.WriteAsync(net, new PublishAcceptedMessage(_options.Name, AppInfo.DisplayVersion), token).ConfigureAwait(false);

        // Agora o servidor faz o papel de quem assiste, com o que todo mundo que já espera por esta transmissão aguenta.
        var existing = _streams.GetValueOrDefault(id);
        var audience = existing?.Limits() ?? AudienceLimits.Nobody;
        using var exchange = new KeyExchange();
        await ControlFraming.WriteAsync(net, new HelloMessage(AppInfo.ProtocolVersion, AppInfo.DisplayVersion, _options.Name, audience.Decoders,
            audience.MaxWidth, audience.MaxHeight, audience.MaxFps, MediaPort, exchange.PublicKey, [.. StreamModes.ViewerFeatures]), token).ConfigureAwait(false);
        using var handshake = CancellationTokenSource.CreateLinkedTokenSource(token);
        handshake.CancelAfter(TimeSpan.FromSeconds(15));
        var reply = await ControlFraming.ReadAsync(net, handshake.Token).ConfigureAwait(false);
        if (reply is RejectedMessage refused)
        {
            Log.Warn($"Servidor: {hostName} recusou o servidor: {refused.Message}");
            return;
        }
        if (reply is not ChallengeMessage challenge) return;
        var secret = exchange.DeriveSharedSecret(challenge.PublicKey);
        var transcript = SessionCrypto.Transcript(exchange.PublicKey, challenge.PublicKey, challenge.Nonce);
        // Quem transmite trata o servidor como de confiança (já provou a senha): não há PIN aqui.
        await ControlFraming.WriteAsync(net, new AuthMessage(SessionCrypto.CreateProof(secret, null, transcript)), token).ConfigureAwait(false);
        WelcomeMessage welcome;
        while (true)
        {
            var message = await ControlFraming.ReadAsync(net, handshake.Token).ConfigureAwait(false);
            if (message is RejectedMessage rejected)
            {
                Log.Warn($"Servidor: {hostName} recusou o servidor: {rejected.Message}");
                CryptographicOperations.ZeroMemory(secret);
                return;
            }
            if (message is WelcomeMessage w) { welcome = w; break; }
        }
        var key = SessionCrypto.DeriveMediaKey(secret, transcript);
        CryptographicOperations.ZeroMemory(secret);
        var upstream = new Upstream(client, net, remote.Address, new IPEndPoint(remote.Address, welcome.MediaPort),
            new PacketCipher(key, Convert.FromBase64String(welcome.SessionSalt)), welcome.Features ?? []);
        CryptographicOperations.ZeroMemory(key);

        RelayedStream relayed;
        lock (_gate)
        {
            relayed = _streams.GetOrAdd(id, _ => new RelayedStream(id));
            relayed.HostName = CleanName(welcome.HostName, hostName);
            relayed.HostVersion = publish.AppVersion.Length <= 24 ? publish.AppVersion : publish.AppVersion[..24];
            relayed.Mode = welcome.Stream.Mode;
            relayed.CameraOverlay = welcome.Stream.CameraOverlay;
            relayed.Config = welcome.Stream;
            var previous = relayed.Attach(upstream);
            if (previous is not null)
            {
                _sources.TryRemove(previous.Source, out _);
                previous.Dispose();
            }
            _sources[upstream.Source] = relayed;
        }
        Log.Info($"Servidor: {relayed.HostName} ({ip}) está transmitindo pelo servidor ({welcome.Stream.Mode}, {welcome.Stream.Width}x{welcome.Stream.Height}).");
        _ = upstream.SendAsync(relayed.Limits().ToMessage(), token);
        relayed.RequestKeyframe(VideoTrack.Main, force: true);
        relayed.RequestKeyframe(VideoTrack.Camera, force: true);
        Changed?.Invoke();

        var ended = false;
        try
        {
            ended = await PublisherLoopAsync(relayed, upstream, token).ConfigureAwait(false);
        }
        finally
        {
            _sources.TryRemove(upstream.Source, out _);
            lock (_gate)
            {
                if (relayed.Upstream == upstream)
                {
                    // (1.5.0) Uma despedida com volta prevista (troca de modo, ou o host pode começar outra) deixa a
                    // transmissão esperando o host voltar, como numa queda: quem assiste entra sozinho na nova.
                    // Uma despedida final leva o texto do servidor (o mesmo de antes), não o que o host mandou.
                    if (ended && upstream.Farewell is not { CanResume: true }) End(relayed, "O host encerrou a transmissão.");
                    else relayed.GoOffline(DateTimeOffset.UtcNow, upstream.Farewell);
                }
            }
            upstream.Dispose();
            Log.Info(ended ? $"Servidor: {relayed.HostName} encerrou a transmissão." : $"Servidor: a conexão de {relayed.HostName} caiu; quem assiste espera ele voltar.");
            Changed?.Invoke();
        }
    }

    /// <summary>Lê o que quem transmite manda. Retorna true quando ele encerrou a transmissão (Bye).</summary>
    private async Task<bool> PublisherLoopAsync(RelayedStream relayed, Upstream upstream, CancellationToken token)
    {
        while (!token.IsCancellationRequested && !upstream.Closed.IsCancellationRequested)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, upstream.Closed.Token);
            var message = await ControlFraming.ReadAsync(upstream.Stream, linked.Token).ConfigureAwait(false);
            switch (message)
            {
                case PongMessage pong:
                    upstream.Clock.AddSample(pong.ViewerTimeUs, pong.HostTimeUs, MediaClock.NowUs);
                    break;
                case StreamConfigMessage config:
                    relayed.Config = config;
                    relayed.Mode = config.Mode;
                    relayed.CameraOverlay = config.CameraOverlay;
                    relayed.ConfigChanged();
                    relayed.Broadcast(config, remoteOnly: false);
                    break;
                case RemoteStateMessage remote:
                    relayed.Remote = remote;
                    relayed.Broadcast(remote, remoteOnly: true);
                    break;
                case MediaStateMessage media:
                    relayed.SetMedia(RemoteControl.Trim(media));
                    relayed.Broadcast(relayed.Media, remoteOnly: true);
                    break;
                case AudioAppsMessage apps:
                    relayed.Apps = apps.Apps is { Length: <= RemoteControl.MaxApps } ? apps : new AudioAppsMessage([.. (apps.Apps ?? []).Take(RemoteControl.MaxApps)]);
                    relayed.Broadcast(relayed.Apps, remoteOnly: true);
                    break;
                case MediaCommandResultMessage result:
                    relayed.RouteResult(result);
                    break;
                case BandwidthTestRequestMessage test:
                    _ = MeasureBandwidthAsync(upstream, test, token);
                    break;
                case ByeMessage bye:
                    upstream.Farewell = bye;
                    return true;
                case UnknownControlMessage unknown:
                    Log.Debug($"Servidor: mensagem que esta versão não conhece de {relayed.HostName}: {unknown.Type}");
                    break;
            }
        }
        return false;
    }

    /// <summary>Quem transmite mede a banda até o servidor: conta o que chega e responde.</summary>
    private static async Task MeasureBandwidthAsync(Upstream upstream, BandwidthTestRequestMessage test, CancellationToken token)
    {
        var seconds = Math.Clamp(test.Seconds, 1, 10);
        Interlocked.Exchange(ref upstream.ProbeBytes, 0);
        Interlocked.Exchange(ref upstream.ProbeCount, 0);
        await Task.Delay(TimeSpan.FromSeconds(seconds + 0.5), token).ConfigureAwait(false);
        var bytes = Interlocked.Read(ref upstream.ProbeBytes);
        var count = Interlocked.Read(ref upstream.ProbeCount);
        var expected = Math.Max(0, test.TargetMbps) * 1_000_000.0 / 8 * seconds / (Packetizer.DefaultPayloadSize + MediaHeader.Size);
        var loss = expected <= 0 ? 0 : Math.Max(0, 1 - count / expected);
        await upstream.SendAsync(new BandwidthTestResultMessage(bytes * 8 / 1_000_000.0 / seconds, loss, seconds), token).ConfigureAwait(false);
    }

    private void End(RelayedStream relayed, string reason)
    {
        if (!_streams.TryRemove(relayed.Id, out _)) return;
        foreach (var subscriber in relayed.Subscribers.Values)
        {
            _ = subscriber.SendAsync(new ByeMessage(reason), CancellationToken.None)
                .ContinueWith(_ => subscriber.Close(), TaskScheduler.Default);
        }
        relayed.Ended(reason);
    }

    // ───────────────────────── Quem assiste ─────────────────────────

    private async Task HandleSubscriberAsync(TcpClient client, NetworkStream net, IPEndPoint remote, HelloMessage hello, CancellationToken token)
    {
        var ip = remote.Address.ToString();
        if (hello.Protocol != AppInfo.ProtocolVersion)
        {
            await ControlFraming.WriteAsync(net, new RejectedMessage(RejectedMessage.VersionMismatch,
                $"Versões diferentes do ScreenFlow (servidor {AppInfo.DisplayVersion}, você {hello.AppVersion}). Atualize os dois."), token).ConfigureAwait(false);
            return;
        }
        RelayedStream? relayed = null;
        if (Guid.TryParse(hello.StreamId, out var id)) relayed = _streams.GetValueOrDefault(id);
        else if (hello.StreamId is null && _streams.Count == 1) relayed = _streams.Values.First();
        if (relayed is null)
        {
            var several = hello.StreamId is null && _streams.Count > 1;
            var message = several
                ? "Este servidor tem mais de uma transmissão: escolha uma na tela Assistir (precisa do ScreenFlow 1.4.1 ou mais novo)."
                : "Essa transmissão não está mais no servidor.";
            // (1.5.0) "Não está aqui" tem código próprio: quem espera o host voltar continua esperando.
            await ControlFraming.WriteAsync(net, new RejectedMessage(several ? RejectedMessage.Denied : RejectedMessage.NotFound, message), token).ConfigureAwait(false);
            return;
        }
        if (relayed.Mode == StreamMode.AudioOnly && !hello.Supports(StreamModes.FeatureAudioOnly))
        {
            await ControlFraming.WriteAsync(net, new RejectedMessage(RejectedMessage.VersionMismatch, $"{relayed.HostName} está numa festa de música. Atualize o ScreenFlow para ouvir."), token).ConfigureAwait(false);
            return;
        }
        if (relayed.Subscribers.Count >= _options.MaxViewersPerStream)
        {
            await ControlFraming.WriteAsync(net, new RejectedMessage(RejectedMessage.Busy, $"Já tem {_options.MaxViewersPerStream} pessoas assistindo {relayed.HostName} pelo servidor."), token).ConfigureAwait(false);
            return;
        }
        if (await AuthenticateAsync(net, ip, hello.PublicKey, relayed.HostName, token).ConfigureAwait(false) is not { } auth) return;
        var key = SessionCrypto.DeriveMediaKey(auth.Secret, auth.Transcript);
        CryptographicOperations.ZeroMemory(auth.Secret);
        var salt = RandomNumberGenerator.GetBytes(4);
        var subscriber = new Subscriber(Guid.NewGuid(), CleanName(hello.ViewerName, ip), client, net, new IPEndPoint(remote.Address, hello.UdpPort),
            new PacketCipher(key, salt), hello, ip);
        CryptographicOperations.ZeroMemory(key);
        relayed.Subscribers[subscriber.Id] = subscriber;
        try
        {
            if (!await WaitUntilReadyAsync(relayed, subscriber, token).ConfigureAwait(false)) return;
            await subscriber.SendAsync(new WelcomeMessage(relayed.HostName, Convert.ToBase64String(salt), MediaPort, relayed.Config,
                [.. StreamModes.HostFeatures]), token).ConfigureAwait(false);
            subscriber.Streaming = true;
            // Quem transmite pode ter saído entre a resposta e agora: a pessoa recebe o mesmo aviso dos outros.
            if (relayed.Upstream is null) relayed.LeftBehind(subscriber);
            relayed.AudienceChanged();
            relayed.SendSnapshot(subscriber);
            relayed.RequestKeyframe(VideoTrack.Main, force: true);
            relayed.RequestKeyframe(VideoTrack.Camera, force: true);
            Log.Info($"Servidor: {subscriber.Name} ({ip}) assistindo {relayed.HostName}. Total: {relayed.Viewers}.");
            Changed?.Invoke();
            await SubscriberLoopAsync(relayed, subscriber, token).ConfigureAwait(false);
        }
        finally
        {
            relayed.Subscribers.TryRemove(subscriber.Id, out _);
            var wasStreaming = subscriber.Streaming;
            subscriber.Streaming = false;
            relayed.AudienceChanged();
            subscriber.Dispose();
            if (wasStreaming) Log.Info($"Servidor: {subscriber.Name} parou de assistir {relayed.HostName}.");
            Changed?.Invoke();
        }
    }

    /// <summary>
    /// Espera a transmissão poder ir para esta pessoa: quem transmite está conectado e o codec é um que ela decodifica.
    /// Enquanto quem transmite está fora (a conexão dele caiu), a pessoa vê "esperando voltar" em vez de um erro.
    /// </summary>
    private async Task<bool> WaitUntilReadyAsync(RelayedStream relayed, Subscriber subscriber, CancellationToken token)
    {
        var told = false;
        var deadline = DateTimeOffset.UtcNow + _options.OfflineGrace;
        relayed.AudienceChanged(); // quem transmite já passa a negociar com esta pessoa junto
        var codecDeadline = DateTimeOffset.MinValue;
        while (!token.IsCancellationRequested)
        {
            if (relayed.IsEnded)
            {
                await subscriber.SendAsync(new RejectedMessage(RejectedMessage.Denied, relayed.EndReason ?? "A transmissão terminou."), token).ConfigureAwait(false);
                return false;
            }
            if (relayed.Upstream is null)
            {
                if (DateTimeOffset.UtcNow > deadline)
                {
                    await subscriber.SendAsync(new RejectedMessage(RejectedMessage.Denied, $"{relayed.HostName} não voltou para o servidor."), token).ConfigureAwait(false);
                    return false;
                }
                if (!told)
                {
                    told = true;
                    await subscriber.SendAsync(new PendingApprovalMessage($"Esperando {relayed.HostName} voltar para o servidor…"), token).ConfigureAwait(false);
                }
            }
            else if (!relayed.Config.VideoEnabled || subscriber.Hello.Decoders.Contains(relayed.Config.Codec))
            {
                return true;
            }
            else
            {
                // O codec de agora não serve para esta pessoa: quem transmite troca para um que todos decodificam.
                if (codecDeadline == DateTimeOffset.MinValue) codecDeadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(6);
                else if (DateTimeOffset.UtcNow > codecDeadline)
                {
                    await subscriber.SendAsync(new RejectedMessage(RejectedMessage.NoCodec, "Este PC não decodifica nenhum codec que quem transmite consegue gerar."), token).ConfigureAwait(false);
                    return false;
                }
            }
            await relayed.WaitForChangeAsync(TimeSpan.FromMilliseconds(500), token).ConfigureAwait(false);
        }
        return false;
    }

    private async Task SubscriberLoopAsync(RelayedStream relayed, Subscriber subscriber, CancellationToken token)
    {
        while (!token.IsCancellationRequested && !subscriber.Closed.IsCancellationRequested)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, subscriber.Closed.Token);
            var message = await ControlFraming.ReadAsync(subscriber.Stream, linked.Token).ConfigureAwait(false);
            switch (message)
            {
                case PingMessage ping:
                    // A hora do host (não a do servidor): quem assiste mede a latência de ponta a ponta.
                    var upstream = relayed.Upstream;
                    var now = MediaClock.NowUs;
                    await subscriber.SendAsync(new PongMessage(ping.ViewerTimeUs, upstream?.Clock.ViewerToHostUs(now) ?? now), token).ConfigureAwait(false);
                    break;
                case StatsMessage stats:
                    subscriber.LastStats = stats;
                    break;
                case NackMessage nack:
                    ServeNack(relayed, subscriber, nack);
                    break;
                case KeyframeRequestMessage request:
                    relayed.RequestKeyframe(request.Track);
                    break;
                case QualityRequestMessage or MediaCommandMessage or AudioAppsRequestMessage:
                    relayed.ForwardRequest(subscriber, message);
                    break;
                case ByeMessage:
                    return;
                case UnknownControlMessage unknown:
                    Log.Debug($"Servidor: mensagem que esta versão não conhece de {subscriber.Name}: {unknown.Type}");
                    break;
            }
        }
    }

    /// <summary>
    /// Atende um NACK com o que o servidor tem (e o que a paridade recupera). O que o servidor também não tem é pedido a
    /// quem transmite; quando chega, vai para todo mundo (se faltou aqui, faltou para todos).
    /// </summary>
    private void ServeNack(RelayedStream relayed, Subscriber subscriber, NackMessage nack)
    {
        if (_sender is not { } sender || !subscriber.Streaming) return;
        var cache = relayed.CacheFor(nack.Track);
        if (cache is null) return;
        var indices = (nack.Missing ?? []).Take(512).ToArray();
        var lookup = cache.Find(nack.FrameId, indices);
        foreach (var packet in lookup.Found.Take(512))
            sender.Send(subscriber.Cipher, ref subscriber.PacketCounter, packet.Header, packet.Payload, subscriber.MediaAddress, retransmit: true);
        // O quadro inteiro que o servidor nem viu, ou só os pedaços que faltaram aqui também.
        if (lookup.Unknown && indices.Length == 0) relayed.AskUpstream(nack.Track, nack.FrameId, []);
        else if (lookup.Missing.Count > 0) relayed.AskUpstream(nack.Track, nack.FrameId, lookup.Missing);
    }

    // ───────────────────────── Mídia (UDP) ─────────────────────────

    private void ReceiveLoop()
    {
        var socket = _media!;
        var buffer = new byte[16 * 1024];
        var plain = new byte[16 * 1024];
        var from = new SocketAddress(AddressFamily.InterNetwork);
        socket.ReceiveTimeout = 250;
        while (!_cancellation.IsCancellationRequested)
        {
            int length;
            try { length = socket.ReceiveFrom(buffer, SocketFlags.None, from); }
            catch (SocketException) { continue; }
            catch (ObjectDisposedException) { break; }
            if (!MediaHeader.TryRead(buffer.AsSpan(0, length), out var header)) continue;
            var body = length - MediaHeader.Size;
            if (body < PacketCipher.TagSize || body - PacketCipher.TagSize != header.PayloadLength) continue;
            if (!_sources.TryGetValue(from, out var relayed) && (relayed = Relearn(from, buffer.AsSpan(0, length), header)) is null) continue;
            if (relayed.Upstream is not { } upstream) continue;
            if (!upstream.Cipher.TryDecrypt(header.PacketNumber, buffer.AsSpan(0, MediaHeader.Size), buffer.AsSpan(MediaHeader.Size, body), plain)) continue;
            try { Forward(relayed, upstream, header, plain.AsSpan(0, header.PayloadLength), length); }
            catch (Exception exception) { Log.Debug($"Servidor: pacote de {relayed.HostName} descartado: {exception.Message}"); }
        }
    }

    /// <summary>
    /// Um pacote de um endereço que o servidor não conhece pode ser de quem transmite, atrás de um NAT que trocou a porta:
    /// se ele abre com a chave de alguém do mesmo IP, o servidor passa a usar o endereço novo.
    /// </summary>
    private RelayedStream? Relearn(SocketAddress from, ReadOnlySpan<byte> packet, MediaHeader header)
    {
        IPEndPoint endpoint;
        try { endpoint = (IPEndPoint)new IPEndPoint(IPAddress.Any, 0).Create(from); }
        catch { return null; }
        foreach (var relayed in _streams.Values)
        {
            if (relayed.Upstream is not { } upstream || !upstream.Address.Equals(endpoint.Address)) continue;
            var plain = new byte[header.PayloadLength];
            if (!upstream.Cipher.TryDecrypt(header.PacketNumber, packet[..MediaHeader.Size], packet[MediaHeader.Size..], plain)) continue;
            _sources.TryRemove(upstream.Source, out _);
            upstream.Rebind(endpoint);
            _sources[upstream.Source] = relayed;
            Log.Info($"Servidor: {relayed.HostName} passou a mandar a mídia de {endpoint}.");
            return relayed;
        }
        return null;
    }

    private void Forward(RelayedStream relayed, Upstream upstream, MediaHeader header, ReadOnlySpan<byte> payload, int wireLength)
    {
        upstream.Count(header.PacketNumber, wireLength);
        switch (header.Type)
        {
            case MediaPacketType.Video or MediaPacketType.VideoFec or MediaPacketType.CameraVideo or MediaPacketType.CameraFec:
                var cache = header.Type is MediaPacketType.Video or MediaPacketType.VideoFec ? relayed.Main : relayed.Camera;
                // Repetido (uma retransmissão de algo que já chegou): quem assiste já recebeu.
                if (!cache.Store(header, payload)) return;
                break;
            case MediaPacketType.Audio or MediaPacketType.AudioPair:
                break;
            case MediaPacketType.Probe:
                Interlocked.Add(ref upstream.ProbeBytes, wireLength);
                Interlocked.Increment(ref upstream.ProbeCount);
                return;
            default:
                return;
        }
        var sender = _sender!;
        var retransmit = header.Flags.HasFlag(MediaPacketFlags.Retransmit);
        foreach (var subscriber in relayed.Recipients())
        {
            if (!subscriber.Streaming) continue;
            sender.Send(subscriber.Cipher, ref subscriber.PacketCounter, header, payload, subscriber.MediaAddress, retransmit);
        }
        relayed.CountOut(payload.Length, relayed.AudienceCount);
    }

    // ───────────────────────── Relógio, estatísticas e limpeza ─────────────────────────

    private async Task TimerLoopAsync()
    {
        var token = _cancellation.Token;
        var punch = new byte[MediaHeader.Size];
        new MediaHeader { Type = MediaPacketType.Probe }.Write(punch);
        var ticks = 0;
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(250));
        try
        {
            while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
            {
                ticks++;
                var now = DateTimeOffset.UtcNow;
                foreach (var relayed in _streams.Values)
                {
                    try
                    {
                        if (relayed.Upstream is { } upstream)
                        {
                            if (ticks % 2 == 0) _ = upstream.SendAsync(new PingMessage(MediaClock.NowUs), token);
                            if (ticks % 4 == 0)
                            {
                                // Mantém o firewall do caminho aberto para a mídia de quem transmite.
                                try { _media?.SendTo(punch, upstream.MediaEndPoint); } catch { }
                                upstream.CloseWindow();
                                if (relayed.Stats(upstream) is { } stats) _ = upstream.SendAsync(stats, token);
                            }
                        }
                        else if (relayed.OfflineSince is { } since && now - since > _options.OfflineGrace)
                        {
                            lock (_gate)
                            {
                                if (relayed.Upstream is null) End(relayed, $"{relayed.HostName} não voltou para o servidor.");
                            }
                            Changed?.Invoke();
                        }
                        if (ticks % 8 == 0) relayed.Prune();
                    }
                    catch (Exception exception) when (!token.IsCancellationRequested)
                    {
                        Log.Debug($"Servidor: {relayed.HostName}: {exception.Message}");
                    }
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    // ───────────────────────── Auxiliares ─────────────────────────

    private static string CleanName(string? name, string fallback)
    {
        var clean = Room.RoomPacket.CleanName(name);
        return clean.Length > 0 ? clean : fallback;
    }

    private bool IsThrottled(string ip)
        => _failures.TryGetValue(ip, out var f) && f.Count >= 5 && DateTimeOffset.UtcNow - f.Start < TimeSpan.FromMinutes(1);

    private void RegisterFailure(string ip)
    {
        var now = DateTimeOffset.UtcNow;
        _failures.AddOrUpdate(ip, _ => (now, 1), (_, f) => now - f.Start > TimeSpan.FromMinutes(1) ? (now, 1) : (f.Start, f.Count + 1));
        if (_failures.Count > 1024)
            foreach (var stale in _failures.Where(f => now - f.Value.Start > TimeSpan.FromMinutes(1)).Select(f => f.Key).ToList()) _failures.TryRemove(stale, out _);
    }

    // ───────────────────────── Estado de cada transmissão ─────────────────────────

    /// <summary>O que todo mundo que assiste uma transmissão aguenta: o codec em comum, a menor tela e o menor FPS.</summary>
    internal sealed record AudienceLimits(int Viewers, string[] Names, VideoCodec[] Decoders, int MaxWidth, int MaxHeight, int MaxFps)
    {
        public static AudienceLimits Nobody { get; } = new(0, [], AllCodecs, 7680, 4320, 240);

        public AudienceMessage ToMessage() => new(Viewers, Names, Decoders, MaxWidth, MaxHeight, MaxFps);
    }

    /// <summary>A conexão de quem transmite com o servidor.</summary>
    internal sealed class Upstream : IDisposable
    {
        private readonly SemaphoreSlim _writeLock = new(1, 1);
        private ulong _highest;
        private ulong _windowStart;
        private long _windowReceived;
        private readonly RateMeter _bytes = new();

        public Upstream(TcpClient client, NetworkStream stream, IPAddress address, IPEndPoint media, PacketCipher cipher, string[] features)
        {
            Client = client;
            Stream = stream;
            Address = address;
            Cipher = cipher;
            Features = features;
            Rebind(media);
        }

        public TcpClient Client { get; }
        public NetworkStream Stream { get; }
        public IPAddress Address { get; }
        public PacketCipher Cipher { get; }
        public string[] Features { get; }
        public IPEndPoint MediaEndPoint { get; private set; } = null!;
        public SocketAddress Source { get; private set; } = null!;
        public ClockSync Clock { get; } = new();
        public CancellationTokenSource Closed { get; } = new();
        /// <summary>(1.5.0) A despedida de quem transmite (se mandou uma): diz se a transmissão pode voltar.</summary>
        public ByeMessage? Farewell { get; set; }
        public double LossRate { get; private set; }
        public long ProbeBytes;
        public long ProbeCount;

        public double InKbps => _bytes.PerSecond(MediaClock.NowUs) * 8 / 1000.0;

        public void Rebind(IPEndPoint media)
        {
            MediaEndPoint = media;
            Source = media.Serialize();
        }

        public void Count(ulong packetNumber, int length)
        {
            Interlocked.Increment(ref _windowReceived);
            if (packetNumber > _highest) _highest = packetNumber;
            if (_windowStart == 0) _windowStart = packetNumber;
            _bytes.Add(length, MediaClock.NowUs);
        }

        /// <summary>Fecha a janela de 1 s da perda entre quem transmite e o servidor.</summary>
        public void CloseWindow()
        {
            var received = Interlocked.Exchange(ref _windowReceived, 0);
            var expected = (double)(_highest - _windowStart);
            _windowStart = _highest;
            LossRate = expected <= 0 ? 0 : Math.Clamp(1 - received / expected, 0, 1);
        }

        public async Task SendAsync(ControlMessage message, CancellationToken token)
        {
            try
            {
                await _writeLock.WaitAsync(token).ConfigureAwait(false);
                try { await ControlFraming.WriteAsync(Stream, message, token).ConfigureAwait(false); }
                finally { _writeLock.Release(); }
            }
            catch (Exception exception) when (exception is IOException or ObjectDisposedException or OperationCanceledException or SocketException)
            {
                Closed.Cancel();
            }
        }

        public void Dispose()
        {
            Closed.Cancel();
            Cipher.Dispose();
            Client.Dispose();
        }
    }

    /// <summary>Uma pessoa assistindo pelo servidor.</summary>
    internal sealed class Subscriber : IDisposable
    {
        private readonly SemaphoreSlim _writeLock = new(1, 1);

        public Subscriber(Guid id, string name, TcpClient client, NetworkStream stream, IPEndPoint media, PacketCipher cipher, HelloMessage hello, string address)
        {
            Id = id;
            Name = name;
            Client = client;
            Stream = stream;
            MediaAddress = media.Serialize();
            Cipher = cipher;
            Hello = hello;
            Address = address;
            Remote = hello.Supports(StreamModes.FeatureRemoteControl);
            MediaSeek = Remote && hello.Supports(StreamModes.FeatureMediaSeek);
        }

        public Guid Id { get; }
        public string Name { get; }
        public TcpClient Client { get; }
        public NetworkStream Stream { get; }
        public SocketAddress MediaAddress { get; }
        public PacketCipher Cipher { get; }
        public HelloMessage Hello { get; }
        public string Address { get; }
        public bool Remote { get; }
        public bool MediaSeek { get; }
        public ulong PacketCounter;
        public volatile bool Streaming;
        private int _leftBehind;

        /// <summary>Marca que esta pessoa já foi avisada da saída de quem transmite (uma vez só).</summary>
        public bool TryMarkLeftBehind() => Interlocked.Exchange(ref _leftBehind, 1) == 0;
        public StatsMessage? LastStats { get; set; }
        public RequestLimiter Limiter { get; } = new();
        public RequestDeduper Requests { get; } = new();
        public CancellationTokenSource Closed { get; } = new();

        public async Task SendAsync(ControlMessage message, CancellationToken token)
        {
            try
            {
                await _writeLock.WaitAsync(token).ConfigureAwait(false);
                try { await ControlFraming.WriteAsync(Stream, message, token).ConfigureAwait(false); }
                finally { _writeLock.Release(); }
            }
            catch (Exception exception) when (exception is IOException or ObjectDisposedException or OperationCanceledException or SocketException)
            {
                Closed.Cancel();
            }
        }

        /// <summary>Fecha a conexão sem despedida: quem assiste reconecta sozinho.</summary>
        public void Close()
        {
            Closed.Cancel();
            try { Client.Client.Shutdown(SocketShutdown.Both); } catch { }
            Client.Dispose();
        }

        public void Dispose()
        {
            Closed.Cancel();
            Cipher.Dispose();
            Client.Dispose();
        }
    }

    /// <summary>Uma transmissão no servidor: a conexão de quem transmite (ou a espera por ela) e quem está assistindo.</summary>
    internal sealed class RelayedStream : IDisposable
    {
        private readonly object _gate = new();
        private readonly Dictionary<(int Track, uint Frame, int Index), long> _asked = new();
        private readonly ConcurrentDictionary<long, (Guid Subscriber, long Original)> _requests = new();
        private readonly RateMeter _out = new();
        private TaskCompletionSource _change = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private Subscriber[] _audience = [];
        private long _nextRequest;
        private long _mediaAt;
        private long _lastKeyframeMain;
        private long _lastKeyframeCamera;
        private volatile Upstream? _upstream;

        public RelayedStream(Guid id) => Id = id;

        public Guid Id { get; }
        public string HostName { get; set; } = string.Empty;
        public string HostVersion { get; set; } = string.Empty;
        public StreamMode Mode { get; set; }
        public bool CameraOverlay { get; set; }
        public volatile StreamConfigMessage Config = new(VideoCodec.H264, 0, 0, 0, 0, false, 48000, 2, string.Empty, string.Empty, VideoEnabled: false);
        public volatile RemoteStateMessage Remote = RemoteStateMessage.Disabled;
        public volatile MediaStateMessage Media = MediaStateMessage.None;
        public volatile AudioAppsMessage Apps = new([]);
        public RelayTrackCache Main { get; private set; } = new(256, MediaPacketType.Video);
        public RelayTrackCache Camera { get; private set; } = new(96, MediaPacketType.CameraVideo);
        public ConcurrentDictionary<Guid, Subscriber> Subscribers { get; } = new();
        public Upstream? Upstream => _upstream;

        /// <summary>De onde quem transmite conectou da última vez.</summary>
        public IPAddress? LastAddress { get; private set; }
        public DateTimeOffset? OfflineSince { get; private set; }
        public bool IsEnded { get; private set; }
        public string? EndReason { get; private set; }
        public int Viewers => Subscribers.Values.Count(s => s.Streaming);
        public int AudienceCount => _audience.Length;

        /// <summary>Quem está recebendo agora (cópia: a lista muda quando alguém entra ou sai).</summary>
        public Subscriber[] Recipients() => _audience;

        public RelayTrackCache? CacheFor(int track) => track == VideoTrack.Camera ? Camera : track == VideoTrack.Main ? Main : null;

        /// <summary>Quem transmite (re)conectou. Devolve a conexão anterior, que precisa ser fechada.</summary>
        public Upstream? Attach(Upstream upstream)
        {
            Upstream? previous;
            lock (_gate)
            {
                previous = _upstream;
                _upstream = upstream;
                LastAddress = upstream.Address;
                OfflineSince = null;
                // Pacotes e números de frame novos: o que estava guardado é de outra conexão.
                Main = new RelayTrackCache(256, MediaPacketType.Video);
                Camera = new RelayTrackCache(96, MediaPacketType.CameraVideo);
                _asked.Clear();
            }
            Signal();
            return previous;
        }

        /// <summary>
        /// A conexão de quem transmite caiu. Quem está assistindo é desconectado sem despedida: reconecta sozinho, espera
        /// quem transmite voltar e recomeça do zero (os números dos frames podem recomeçar com a conexão nova).
        /// </summary>
        public void GoOffline(DateTimeOffset now, ByeMessage? farewell = null)
        {
            lock (_gate)
            {
                _upstream = null;
                _farewell = farewell;
                OfflineSince = now;
            }
            foreach (var subscriber in Subscribers.Values.Where(s => s.Streaming)) LeftBehind(subscriber);
            Signal();
        }

        private ByeMessage? _farewell;

        /// <summary>
        /// Quem transmite saiu e esta pessoa estava assistindo: (1.5.0) quem entende a despedida com volta mostra
        /// "trocando para a festa…" e espera; os outros só reconectam (como sempre fizeram numa queda). Também é chamado
        /// para quem acabou de receber a resposta de boas-vindas no mesmo instante em que quem transmite saiu.
        /// </summary>
        public void LeftBehind(Subscriber subscriber)
        {
            if (!subscriber.TryMarkLeftBehind()) return;
            ByeMessage? farewell;
            lock (_gate) farewell = _farewell;
            if (farewell is { CanResume: true } && subscriber.Hello.Supports(StreamModes.FeatureFollowHost))
                _ = subscriber.SendAsync(farewell, CancellationToken.None).ContinueWith(_ => subscriber.Close(), TaskScheduler.Default);
            else subscriber.Close();
        }

        public void Ended(string reason)
        {
            IsEnded = true;
            EndReason = reason;
            Signal();
        }

        public void ConfigChanged() => Signal();

        public async Task WaitForChangeAsync(TimeSpan timeout, CancellationToken token)
        {
            Task change;
            lock (_gate) change = _change.Task;
            try { await change.WaitAsync(timeout, token).ConfigureAwait(false); }
            catch (TimeoutException) { }
        }

        private void Signal()
        {
            TaskCompletionSource previous;
            lock (_gate)
            {
                previous = _change;
                _change = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }
            previous.TrySetResult();
        }

        /// <summary>Alguém entrou ou saiu: a lista de quem recebe muda e quem transmite fica sabendo.</summary>
        public void AudienceChanged()
        {
            lock (_gate) _audience = Subscribers.Values.Where(s => s.Streaming).ToArray();
            if (_upstream is { } upstream) _ = upstream.SendAsync(Limits().ToMessage(), CancellationToken.None);
            Signal();
        }

        /// <summary>O que todo mundo (inclusive quem ainda está entrando) aguenta.</summary>
        public AudienceLimits Limits()
        {
            var people = Subscribers.Values.ToList();
            if (people.Count == 0) return AudienceLimits.Nobody;
            IEnumerable<VideoCodec> common = AllCodecs;
            foreach (var person in people) common = common.Intersect(person.Hello.Decoders ?? []);
            var decoders = common.ToArray();
            return new AudienceLimits(people.Count(p => p.Streaming), [.. people.Where(p => p.Streaming).Select(p => p.Name).Take(64)],
                decoders.Length > 0 ? decoders : AllCodecs,
                people.Min(p => p.Hello.MaxWidth), people.Min(p => p.Hello.MaxHeight), people.Min(p => p.Hello.MaxFps));
        }

        public void SetMedia(MediaStateMessage media)
        {
            Interlocked.Exchange(ref _mediaAt, Environment.TickCount64);
            Media = media;
        }

        /// <summary>Manda uma mensagem de quem transmite para todo mundo que assiste (só para quem entende, se for dos controles).</summary>
        public void Broadcast(ControlMessage message, bool remoteOnly)
        {
            foreach (var subscriber in Subscribers.Values)
            {
                if (!subscriber.Streaming || (remoteOnly && !subscriber.Remote)) continue;
                _ = subscriber.SendAsync(message, CancellationToken.None);
            }
        }

        /// <summary>Quem acabou de entrar recebe o estado atual dos controles (a posição da música é a de agora).</summary>
        public void SendSnapshot(Subscriber subscriber)
        {
            if (!subscriber.Remote) return;
            _ = subscriber.SendAsync(Remote, CancellationToken.None);
            var media = Media;
            var elapsed = (Environment.TickCount64 - Interlocked.Read(ref _mediaAt)) / 1000.0;
            _ = subscriber.SendAsync(media.Playing ? media with { PositionSeconds = RemoteControl.PositionAt(media, elapsed) } : media, CancellationToken.None);
            _ = subscriber.SendAsync(Apps, CancellationToken.None);
        }

        /// <summary>
        /// Passa o pedido de quem assiste para quem transmite, com o nome da pessoa e um número do servidor (os números de
        /// pessoas diferentes podem coincidir). A resposta volta para quem pediu, com o número dela.
        /// </summary>
        public void ForwardRequest(Subscriber subscriber, ControlMessage message)
        {
            if (!subscriber.Remote || _upstream is not { } upstream) return;
            if (!subscriber.Limiter.TryTake(Environment.TickCount64)) return;
            ControlMessage forwarded;
            switch (message)
            {
                case MediaCommandMessage command:
                    if (!subscriber.Requests.IsNew(command.RequestId)) return;
                    long id = 0;
                    if (command.RequestId != 0)
                    {
                        id = Interlocked.Increment(ref _nextRequest);
                        _requests[id] = (subscriber.Id, command.RequestId);
                        if (_requests.Count > 512)
                            foreach (var old in _requests.Keys.Where(k => k < id - 256).ToList()) _requests.TryRemove(old, out _);
                    }
                    forwarded = command with { RequestId = id, From = subscriber.Name };
                    break;
                case QualityRequestMessage quality:
                    forwarded = quality with { From = subscriber.Name };
                    break;
                case AudioAppsRequestMessage apps:
                    forwarded = apps with { From = subscriber.Name };
                    break;
                default:
                    return;
            }
            _ = upstream.SendAsync(forwarded, CancellationToken.None);
        }

        public void RouteResult(MediaCommandResultMessage result)
        {
            if (!_requests.TryRemove(result.RequestId, out var origin)) return;
            if (Subscribers.TryGetValue(origin.Subscriber, out var subscriber) && subscriber.Streaming && subscriber.MediaSeek)
                _ = subscriber.SendAsync(result with { RequestId = origin.Original }, CancellationToken.None);
        }

        /// <summary>Pede um keyframe a quem transmite, no máximo um a cada 250 ms por faixa (vários pedindo juntos viram um).</summary>
        public void RequestKeyframe(int track, bool force = false)
        {
            if (_upstream is not { } upstream) return;
            ref var last = ref track == VideoTrack.Camera ? ref _lastKeyframeCamera : ref _lastKeyframeMain;
            var now = Environment.TickCount64;
            if (!force && now - Interlocked.Read(ref last) < 250) return;
            Interlocked.Exchange(ref last, now);
            _ = upstream.SendAsync(new KeyframeRequestMessage("servidor", track), CancellationToken.None);
        }

        /// <summary>
        /// Pede a quem transmite o que o servidor não tem. Cada pacote é pedido uma vez por RTT, mesmo que várias pessoas
        /// peçam. <paramref name="indices"/> vazio pede o frame inteiro.
        /// </summary>
        public void AskUpstream(int track, uint frameId, IReadOnlyList<ushort> indices)
        {
            if (_upstream is not { } upstream) return;
            var now = Environment.TickCount64;
            var wait = (long)Math.Clamp(upstream.Clock.RoundTripMs * 1.2 + 10, 25, 250);
            var ask = new List<ushort>();
            lock (_gate)
            {
                if (indices.Count == 0)
                {
                    var key = (track, frameId, -1);
                    if (_asked.TryGetValue(key, out var at) && now - at < wait) return;
                    _asked[key] = now;
                }
                else
                {
                    foreach (var index in indices)
                    {
                        var key = (track, frameId, (int)index);
                        if (_asked.TryGetValue(key, out var at) && now - at < wait) continue;
                        _asked[key] = now;
                        ask.Add(index);
                    }
                    if (ask.Count == 0) return;
                }
            }
            _ = upstream.SendAsync(new NackMessage(frameId, [.. ask], track), CancellationToken.None);
        }

        public void Prune()
        {
            var now = Environment.TickCount64;
            lock (_gate)
            {
                if (_asked.Count < 256) return;
                foreach (var key in _asked.Where(a => now - a.Value > 2000).Select(a => a.Key).ToList()) _asked.Remove(key);
            }
        }

        public void CountOut(int payloadBytes, int copies)
        {
            if (copies > 0) _out.Add((long)(payloadBytes + MediaHeader.Size + PacketCipher.TagSize) * copies, MediaClock.NowUs);
        }

        /// <summary>
        /// As estatísticas que vão para quem transmite: a pior perda (entre ele e o servidor ou do servidor até alguém), o
        /// RTT dele até o servidor somado ao pior RTT até quem assiste, e o pior que alguém recebe e decodifica.
        /// </summary>
        public StatsMessage? Stats(Upstream upstream)
        {
            var samples = Subscribers.Values.Where(s => s.Streaming && s.LastStats is not null).Select(s => s.LastStats!).ToList();
            if (samples.Count == 0) return null;
            return new StatsMessage(
                Math.Max(upstream.LossRate, samples.Max(s => s.LossRate)),
                upstream.Clock.RoundTripMs + samples.Max(s => s.RoundTripMs),
                Math.Min(upstream.InKbps, samples.Min(s => s.ReceivedKbps)),
                samples.Min(s => s.DecodeFps),
                samples.Max(s => s.LatencyMs));
        }

        public ServerStreamInfo Info()
        {
            var config = Config;
            var video = !config.VideoEnabled ? "só áudio" : $"{Quality.CodecLabel(config.Codec)} {config.Width}x{config.Height} {config.Fps} FPS";
            var upstream = _upstream;
            var viewers = Subscribers.Values.Where(s => s.Streaming).Select(s => s.Name).OrderBy(n => n, StringComparer.CurrentCultureIgnoreCase).ToArray();
            return new ServerStreamInfo(Id, HostName, Mode, upstream is not null, viewers.Length, viewers, video,
                (upstream?.InKbps ?? 0) / 1000.0, _out.PerSecond(MediaClock.NowUs) * 8 / 1_000_000.0, upstream?.LossRate ?? 0);
        }

        public void Dispose()
        {
            _upstream?.Dispose();
            foreach (var subscriber in Subscribers.Values) subscriber.Dispose();
            Subscribers.Clear();
            Signal();
        }
    }
}
