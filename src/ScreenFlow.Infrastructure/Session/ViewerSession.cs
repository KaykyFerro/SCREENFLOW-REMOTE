using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using ScreenFlow.Core;
using ScreenFlow.Core.Diagnostics;
using ScreenFlow.Core.Media;
using ScreenFlow.Core.Network;
using ScreenFlow.Core.Protocol;
using ScreenFlow.Core.Security;
using ScreenFlow.Infrastructure.Media;
using ScreenFlow.Infrastructure.Network;
using ScreenFlow.Infrastructure.Rendering;

namespace ScreenFlow.Infrastructure.Session;

public enum ConnectionState { Idle, Connecting, WaitingApproval, Connected, Reconnecting, Error, Closed }

/// <param name="StreamId">Pelo servidor ScreenFlow (1.4.1): qual transmissão do servidor assistir (o id de quem transmite).</param>
/// <param name="ViaServer">Está assistindo pelo servidor: <paramref name="Pin"/> é a senha do servidor.</param>
public sealed record ViewerOptions(string Host, int ControlPort, string DeviceName, string? Pin, float Volume, VideoCodec[] Decoders, int MaxWidth, int MaxHeight, int MaxFps,
    bool AudioOutput = true, string? StreamId = null, bool ViaServer = false);

public sealed class ConnectionRejectedException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

/// <summary>
/// Conecta num host pelo IP, faz o handshake, recebe vídeo e áudio por UDP, decodifica e reconecta sozinho
/// com backoff se a conexão cair. Com um <see cref="Presenter"/>, o vídeo é desenhado direto na GPU.
/// Quando o host manda a câmera junto com a tela, ela chega numa segunda faixa e sai em <see cref="CameraFrameReady"/>.
/// </summary>
public sealed class ViewerSession : IAsyncDisposable
{
    private readonly ViewerOptions _options;
    private readonly CancellationTokenSource _cancellation = new();
    private readonly Backoff _backoff = new();
    private Task? _runner;
    private string? _pin;
    private float _volume;
    private AudioPlayer? _audio;
    private ViewerStatistics? _lastStats;
    private volatile StreamConfigMessage? _config;
    private volatile D3D11VideoPresenter? _presenter;
    private bool? _gpuRendering;
    private volatile ActiveConnection? _connection;
    private volatile bool _remoteAvailable;
    private volatile RemoteStateMessage _remote = RemoteStateMessage.Disabled;
    private volatile MediaStateMessage _media = MediaStateMessage.None;
    private long _mediaReceivedUs;
    private volatile AudioAppsMessage _apps = new([]);
    private volatile bool _hostMediaSeek;

    public ViewerSession(ViewerOptions options)
    {
        _options = options;
        _pin = options.Pin;
        _volume = options.Volume;
    }

    public ConnectionState State { get; private set; } = ConnectionState.Idle;
    public string HostName { get; private set; } = "";
    public ViewerStatistics? LastStatistics => _lastStats;

    /// <summary>Configuração atual do host (modo, codec, resolução). Null antes de conectar.</summary>
    public StreamConfigMessage? Config => _config;

    /// <summary>Nível e espectro do áudio que está tocando (para o visualizador da festa de música).</summary>
    public AudioMeter? AudioMeter => _audio?.Meter;

    /// <summary>Player na GPU. Definido pela janela do player; null usa a CPU (<see cref="FrameReady"/>).</summary>
    public D3D11VideoPresenter? Presenter
    {
        get => _presenter;
        set => _presenter = value;
    }

    public event Action<ConnectionState, string>? StateChanged;
    public event Action<DecodedFrame>? FrameReady;

    /// <summary>Um frame da câmera que vai junto com a tela (BGRA, pela CPU), para o quadrinho do player.</summary>
    public event Action<DecodedFrame>? CameraFrameReady;
    public event Action<ViewerStatistics>? StatisticsUpdated;
    public event Action<StreamConfigMessage>? ConfigChanged;

    /// <summary>true quando os frames passam a ser desenhados na GPU; false quando vêm pela CPU.</summary>
    public event Action<bool>? RenderPathChanged;

    /// <summary>Chamado quando o host exige PIN e nenhum foi informado (ou o informado estava errado).</summary>
    public Func<bool, Task<string?>>? PinProvider { get; set; }

    // ───────────────────────── Controles de quem assiste (1.2.0) ─────────────────────────

    /// <summary>Conectado a um host que aceita os pedidos de quem assiste (1.2.0 ou mais nova). Hosts antigos não têm os controles.</summary>
    public bool RemoteAvailable => _remoteAvailable && _connection is not null;

    /// <summary>O que o host deixa fazer e as qualidades que oferece.</summary>
    public RemoteStateMessage RemoteState => _remote;

    /// <summary>O que está tocando no host, como chegou (veja <see cref="MediaPosition"/> para a posição de agora).</summary>
    public MediaStateMessage Media => _media;

    /// <summary>A posição da música agora, contando o tempo desde que o estado chegou.</summary>
    public double MediaPosition => RemoteControl.PositionAt(_media, (MediaClock.NowUs - Interlocked.Read(ref _mediaReceivedUs)) / 1_000_000.0);

    /// <summary>Os apps com som no host e quais estão fora da transmissão.</summary>
    public AudioAppsMessage AudioApps => _apps;

    /// <summary>Mudou o que o host deixa fazer, a música ou a lista de apps (ou a conexão caiu). Chamado fora da thread da interface.</summary>
    public event Action? RemoteChanged;

    /// <summary>Pede outra qualidade ao host. false se não está conectado ou o host não oferece.</summary>
    public Task<bool> RequestQualityAsync(string resolution, int fps, bool adaptive)
        => SendRemoteAsync(new QualityRequestMessage(resolution, fps, adaptive), RemoteState.CanChangeQuality);

    /// <summary>
    /// Pausa, volta, pula ou muda o ponto da música ou do vídeo que está tocando no host. Vai com a chave da sessão de mídia
    /// que está na tela, para o host recusar se o app mudou no meio do caminho.
    /// </summary>
    public Task<bool> SendMediaCommandAsync(string command, double positionSeconds = 0, long requestId = 0, double deltaSeconds = 0)
    {
        if (command == MediaCommandMessage.SkipBy && !_hostMediaSeek) return Task.FromResult(false);
        return SendRemoteAsync(new MediaCommandMessage(command, positionSeconds, _hostMediaSeek ? requestId : 0, _media.SessionKey, deltaSeconds), RemoteState.CanControlMedia);
    }

    /// <summary>O host é da 1.4.0 ou mais nova: avança e volta alguns segundos e responde a cada busca.</summary>
    public bool HostConfirmsSeek => _hostMediaSeek && RemoteAvailable;

    /// <summary>A resposta do host a um comando de mídia (1.4.0). Chamado fora da thread da interface.</summary>
    public event Action<MediaCommandResultMessage>? MediaResult;

    /// <summary>Os apps da lista que devem ficar fora do som da transmissão.</summary>
    public Task<bool> RequestAudioAppsAsync(IEnumerable<string> excluded)
        => SendRemoteAsync(new AudioAppsRequestMessage([.. excluded.Distinct(StringComparer.OrdinalIgnoreCase).Take(RemoteControl.MaxApps)]), RemoteState.CanChooseApps);

    private async Task<bool> SendRemoteAsync(ControlMessage message, bool allowed)
    {
        if (!allowed || !_remoteAvailable || _connection is not { } connection) return false;
        return await connection.TrySendAsync(message).ConfigureAwait(false);
    }

    private void SetRemote(RemoteStateMessage? remote = null, MediaStateMessage? media = null, AudioAppsMessage? apps = null)
    {
        if (remote is not null) _remote = remote;
        if (media is not null)
        {
            Interlocked.Exchange(ref _mediaReceivedUs, MediaClock.NowUs);
            _media = RemoteControl.Trim(media);
        }
        if (apps is not null) _apps = apps.Apps is { Length: <= RemoteControl.MaxApps } ? apps : new AudioAppsMessage([.. (apps.Apps ?? []).Take(RemoteControl.MaxApps)]);
        RemoteChanged?.Invoke();
    }

    private void ResetRemote(bool available)
    {
        _remoteAvailable = available;
        _remote = RemoteStateMessage.Disabled;
        _media = MediaStateMessage.None;
        _apps = new AudioAppsMessage([]);
        RemoteChanged?.Invoke();
    }

    public float Volume
    {
        get => _volume;
        set
        {
            _volume = Math.Clamp(value, 0, 1);
            if (_audio is not null) _audio.Volume = _volume;
        }
    }

    public void Start() => _runner = Task.Run(RunAsync);

    private void SetConfig(StreamConfigMessage config)
    {
        _config = config;
        ConfigChanged?.Invoke(config);
    }

    private void SetRenderPath(bool gpu)
    {
        if (_gpuRendering == gpu) return;
        _gpuRendering = gpu;
        Log.Info(gpu ? "Vídeo desenhado direto na GPU." : "Vídeo desenhado pela CPU.");
        RenderPathChanged?.Invoke(gpu);
    }

    private void SetState(ConnectionState state, string message)
    {
        State = state;
        Log.Info($"Viewer: {state} — {message}");
        StateChanged?.Invoke(state, message);
    }

    private async Task RunAsync()
    {
        var token = _cancellation.Token;
        var wrongPin = false;
        while (!token.IsCancellationRequested)
        {
            try
            {
                SetState(_backoff.Attempt == 0 ? ConnectionState.Connecting : ConnectionState.Reconnecting,
                    _backoff.Attempt == 0 ? $"Conectando em {_options.Host}…" : $"Reconectando em {_options.Host} (tentativa {_backoff.Attempt})…");
                await ConnectOnceAsync(token, wrongPin);
                wrongPin = false;
                if (token.IsCancellationRequested) break;
                SetState(ConnectionState.Reconnecting, "A conexão caiu. Reconectando…");
            }
            catch (ConnectionRejectedException rejected) when (rejected.Code == RejectedMessage.WrongPin && PinProvider is not null)
            {
                _pin = null;
                wrongPin = true;
                continue; // pede o PIN de novo
            }
            catch (ConnectionRejectedException rejected)
            {
                SetState(ConnectionState.Error, rejected.Message);
                return;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
            catch (Exception exception)
            {
                Log.Warn($"Falha na conexão: {exception.Message}");
                SetState(ConnectionState.Reconnecting, FriendlyError(exception));
            }

            try { await Task.Delay(_backoff.Next(), token); }
            catch (OperationCanceledException) { break; }
        }
        SetState(ConnectionState.Closed, "Desconectado.");
    }

    private string FriendlyError(Exception exception) => exception switch
    {
        SocketException { SocketErrorCode: SocketError.ConnectionRefused } => _options.ViaServer
            ? $"O servidor {_options.Host} não respondeu (o programa do servidor está aberto?). Tentando de novo…"
            : $"O ScreenFlow não está transmitindo em {_options.Host}. Tentando de novo…",
        SocketException { SocketErrorCode: SocketError.HostNotFound or SocketError.NoData } => $"Não achei {_options.Host}. Confira o endereço. Tentando de novo…",
        SocketException { SocketErrorCode: SocketError.TimedOut or SocketError.HostUnreachable or SocketError.NetworkUnreachable } or TimeoutException
            => $"Não deu para alcançar {_options.Host}. Confira se os dois estão na mesma rede (Tailscale, Radmin VPN ou a rede local). Tentando de novo…",
        _ => $"Conexão perdida ({exception.Message}). Tentando de novo…",
    };

    private async Task ConnectOnceAsync(CancellationToken token, bool askPinAgain)
    {
        // Um IP (26.x do Radmin, 100.x do Tailscale, da rede local) ou, para o servidor, também o nome dele (MagicDNS).
        if (!LocalNetworks.TrySplitHostPort(_options.Host, _options.ControlPort, out var hostText, out var controlPort))
            throw new ConnectionRejectedException("address", "Digite um IP válido, por exemplo 26.12.34.56 ou 100.101.102.103.");
        var hostAddress = await LocalNetworks.ResolveAsync(hostText, token)
                          ?? throw new SocketException((int)SocketError.HostNotFound);

        using var client = new TcpClient(AddressFamily.InterNetwork) { NoDelay = true };
        using (var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(token))
        {
            connectTimeout.CancelAfter(TimeSpan.FromSeconds(5));
            try { await client.ConnectAsync(hostAddress, controlPort, connectTimeout.Token); }
            catch (OperationCanceledException) when (!token.IsCancellationRequested) { throw new TimeoutException(); }
        }
        var stream = client.GetStream();
        var local = (client.Client.LocalEndPoint as IPEndPoint)?.Address ?? IPAddress.Any;
        using var udp = MediaSender.CreateUdp(local, 0);
        var udpPort = ((IPEndPoint)udp.LocalEndPoint!).Port;

        using var exchange = new KeyExchange();
        await ControlFraming.WriteAsync(stream, new HelloMessage(AppInfo.ProtocolVersion, AppInfo.DisplayVersion, _options.DeviceName,
            _options.Decoders, _options.MaxWidth, _options.MaxHeight, _options.MaxFps, udpPort, exchange.PublicKey, [.. StreamModes.ViewerFeatures], _options.StreamId), token);

        if (await ControlFraming.ReadAsync(stream, token) is var first && first is RejectedMessage early)
            throw new ConnectionRejectedException(early.Code, early.Message);
        if (first is not ChallengeMessage challenge) throw new InvalidDataException("Resposta inesperada do host.");
        HostName = challenge.HostName;

        if (challenge.PinRequired && (string.IsNullOrEmpty(_pin) || askPinAgain))
        {
            _pin = PinProvider is null ? null : await PinProvider(askPinAgain);
            if (string.IsNullOrEmpty(_pin))
                throw new ConnectionRejectedException(RejectedMessage.WrongPin + "-missing", _options.ViaServer ? "Este servidor pede senha." : "Este host pede PIN.");
        }

        var secret = exchange.DeriveSharedSecret(challenge.PublicKey);
        var transcript = SessionCrypto.Transcript(exchange.PublicKey, challenge.PublicKey, challenge.Nonce);
        await ControlFraming.WriteAsync(stream, new AuthMessage(SessionCrypto.CreateProof(secret, challenge.PinRequired ? _pin : null, transcript)), token);
        var key = SessionCrypto.DeriveMediaKey(secret, transcript);

        WelcomeMessage welcome;
        while (true)
        {
            var message = await ControlFraming.ReadAsync(stream, token);
            if (message is PendingApprovalMessage pending) { SetState(ConnectionState.WaitingApproval, $"{challenge.HostName}: {pending.Message}"); continue; }
            if (message is RejectedMessage rejected) throw new ConnectionRejectedException(rejected.Code, rejected.Message);
            if (message is WelcomeMessage w) { welcome = w; break; }
        }

        using var cipher = new PacketCipher(key, Convert.FromBase64String(welcome.SessionSalt));
        using var connection = new ActiveConnection(this, stream, udp, new IPEndPoint(hostAddress, welcome.MediaPort), cipher, welcome.Stream,
            welcome.Supports(StreamModes.HostFeatureFrameNack));
        _backoff.Reset();
        SetConfig(welcome.Stream);
        _connection = connection;
        _hostMediaSeek = welcome.Supports(StreamModes.HostFeatureMediaSeek);
        ResetRemote(welcome.Supports(StreamModes.HostFeatureRemoteControl));
        SetState(ConnectionState.Connected, welcome.Stream.Mode == StreamMode.AudioOnly ? $"Na festa de música de {welcome.HostName}." : $"Conectado a {welcome.HostName}.");
        try { await connection.RunAsync(token); }
        finally
        {
            _connection = null;
            ResetRemote(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cancellation.Cancel();
        if (_runner is not null) try { await _runner.WaitAsync(TimeSpan.FromSeconds(5)); } catch { }
        _audio?.Dispose();
        _audio = null;
        _cancellation.Dispose();
    }

    /// <summary>Uma conexão viva: threads de recepção UDP, remontagem, decodificação e controle.</summary>
    private sealed class ActiveConnection : IDisposable
    {
        private readonly ViewerSession _owner;
        private readonly NetworkStream _stream;
        private readonly Socket _udp;
        private readonly IPEndPoint _hostMedia;
        private readonly PacketCipher _cipher;
        private readonly FrameReassembler _reassembler = new();
        private readonly object _reassemblyGate = new();
        private readonly BlockingCollection<AssembledFrame> _decodeQueue = new(8);
        private readonly FrameReassembler _cameraReassembler = new(dataType: MediaPacketType.CameraVideo);
        private readonly object _cameraGate = new();
        private readonly BlockingCollection<AssembledFrame> _cameraQueue = new(4);
        private readonly RateMeter _cameraDecoded = new();
        private readonly SemaphoreSlim _writeLock = new(1, 1);
        private readonly ClockSync _clock = new();
        private readonly RateMeter _bytes = new();
        private readonly RateMeter _decoded = new();
        private readonly CancellationTokenSource _stop = new();
        private StreamConfigMessage _config;
        private VideoDecoder? _decoder;
        private D3D11VideoPresenter? _decoderPresenter;
        private GpuFrameHandler? _gpuHandler;
        private string _decoderLabel = "—";
        private long _lastVideoTimestampViewerUs;
        private long _lastKeyframeRequestUs;
        private long _lastCameraKeyframeRequestUs;
        private volatile bool _cameraSeen;
        private int _cameraWidth;
        private int _cameraHeight;
        private ulong _highestPacket;
        private ulong _windowStartPacket;
        private long _windowReceived;
        private ulong _firstPacket;
        private long _totalReceived;
        private long _decoderLate;
        private double _lossRate;
        private double _latencyMs;
        private volatile bool _codecChanged;

        // Teste de banda
        private long _probeBytes;
        private long _probeCount;

        public ActiveConnection(ViewerSession owner, NetworkStream stream, Socket udp, IPEndPoint hostMedia, PacketCipher cipher, StreamConfigMessage config, bool wholeFrameNack)
        {
            _owner = owner;
            _stream = stream;
            _udp = udp;
            _hostMedia = hostMedia;
            _cipher = cipher;
            _config = config;
            // Hosts 1.1.1 reenviam um frame que sumiu inteiro: ele é pedido de novo em vez de pulado.
            _reassembler.WholeFrameNack = _cameraReassembler.WholeFrameNack = wholeFrameNack;
        }

        public async Task RunAsync(CancellationToken token)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, _stop.Token);
            var t = linked.Token;
            var receive = new Thread(() => ReceiveLoop(t)) { IsBackground = true, Name = "ScreenFlow UDP", Priority = ThreadPriority.Highest };
            var decode = new Thread(() => DecodeLoop(t)) { IsBackground = true, Name = "ScreenFlow decoder", Priority = ThreadPriority.AboveNormal };
            var camera = new Thread(() => CameraDecodeLoop(t)) { IsBackground = true, Name = "ScreenFlow decoder da câmera" };
            receive.Start();
            if (_config.VideoEnabled)
            {
                decode.Start();
                camera.Start();
            }
            if (_config.AudioEnabled) StartAudio();

            var tick = Task.Run(() => TickLoopAsync(t));
            var timers = Task.Run(() => TimerLoopAsync(t));
            using var performance = SessionPerformance.Request();
            try
            {
                await SendAsync(new KeyframeRequestMessage("entrada"), t);
                await ControlLoopAsync(t);
            }
            finally
            {
                _stop.Cancel();
                _decodeQueue.CompleteAdding();
                _cameraQueue.CompleteAdding();
                receive.Join(1000);
                if (decode.IsAlive) decode.Join(1000);
                if (camera.IsAlive) camera.Join(1000);
                try { await Task.WhenAll(tick, timers); } catch { }
            }
        }

        private void StartAudio()
        {
            try
            {
                var profile = AudioProfile.ForMode(_config.Mode, AudioProfile.MaxVoiceKbps, AudioProfile.MaxMusicKbps);
                if (_owner._audio is { } current && current.Profile.Music != profile.Music)
                {
                    _owner._audio = null;
                    current.Dispose();
                }
                _owner._audio ??= new AudioPlayer(_owner._volume, profile, _owner._options.AudioOutput);
            }
            catch (Exception exception) { Log.Warn($"Saída de áudio indisponível: {exception.Message}"); }
        }

        private async Task ControlLoopAsync(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                var message = await ControlFraming.ReadAsync(_stream, token);
                switch (message)
                {
                    case PongMessage pong:
                        _clock.AddSample(pong.ViewerTimeUs, pong.HostTimeUs, MediaClock.NowUs);
                        break;
                    case StreamConfigMessage config:
                        if (config.Codec != _config.Codec) _codecChanged = true;
                        var startAudio = config.AudioEnabled && !_config.AudioEnabled;
                        _config = config;
                        if (startAudio) StartAudio();
                        _owner.SetConfig(config);
                        Log.Info($"Novo perfil do host: {config.Width}x{config.Height} {config.Fps} FPS {config.BitrateKbps} kbps ({config.Reason})");
                        break;
                    case BandwidthTestRequestMessage test:
                        _ = RunBandwidthTestAsync(test, token);
                        break;
                    case RemoteStateMessage remote:
                        _owner.SetRemote(remote: remote);
                        break;
                    case MediaStateMessage media:
                        _owner.SetRemote(media: media);
                        break;
                    case AudioAppsMessage apps:
                        _owner.SetRemote(apps: apps);
                        break;
                    case MediaCommandResultMessage result:
                        _owner.MediaResult?.Invoke(result);
                        break;
                    case UnknownControlMessage unknown:
                        Log.Debug($"Mensagem do host que esta versão não conhece: {unknown.Type}");
                        break;
                    case ByeMessage bye:
                        throw new ConnectionRejectedException("bye", bye.Reason);
                }
            }
        }

        private async Task RunBandwidthTestAsync(BandwidthTestRequestMessage test, CancellationToken token)
        {
            Interlocked.Exchange(ref _probeBytes, 0);
            Interlocked.Exchange(ref _probeCount, 0);
            await Task.Delay(TimeSpan.FromSeconds(test.Seconds + 0.5), token);
            var bytes = Interlocked.Read(ref _probeBytes);
            var count = Interlocked.Read(ref _probeCount);
            var mbps = bytes * 8 / 1_000_000.0 / test.Seconds;
            var expected = test.TargetMbps * 1_000_000.0 / 8 * test.Seconds / (Packetizer.DefaultPayloadSize + MediaHeader.Size);
            var loss = expected <= 0 ? 0 : Math.Max(0, 1 - count / expected);
            await SendAsync(new BandwidthTestResultMessage(mbps, loss, test.Seconds), token);
        }

        private void ReceiveLoop(CancellationToken token)
        {
            var buffer = new byte[16 * 1024];
            var plain = new byte[16 * 1024];
            var from = new SocketAddress(AddressFamily.InterNetwork); // reaproveitado: nenhuma alocação por pacote
            _udp.ReceiveTimeout = 250;
            while (!token.IsCancellationRequested)
            {
                int length;
                try { length = _udp.ReceiveFrom(buffer, SocketFlags.None, from); }
                catch (SocketException) { continue; }
                catch (ObjectDisposedException) { break; }
                if (!MediaHeader.TryRead(buffer.AsSpan(0, length), out var header)) continue;
                var body = length - MediaHeader.Size;
                if (body < PacketCipher.TagSize || body - PacketCipher.TagSize != header.PayloadLength) continue;
                if (!_cipher.TryDecrypt(header.PacketNumber, buffer.AsSpan(0, MediaHeader.Size), buffer.AsSpan(MediaHeader.Size, body), plain)) continue;

                var now = MediaClock.NowUs;
                _bytes.Add(length, now);
                CountPacket(header.PacketNumber);
                var payload = plain.AsSpan(0, header.PayloadLength);
                switch (header.Type)
                {
                    case MediaPacketType.Video:
                    case MediaPacketType.VideoFec:
                        List<AssembledFrame> ready;
                        lock (_reassemblyGate) ready = _reassembler.Add(header, payload, MediaClock.Now);
                        Enqueue(ready);
                        break;
                    case MediaPacketType.CameraVideo:
                    case MediaPacketType.CameraFec:
                        List<AssembledFrame> cameraReady;
                        lock (_cameraGate) cameraReady = _cameraReassembler.Add(header, payload, MediaClock.Now);
                        _cameraSeen = true;
                        EnqueueCamera(cameraReady);
                        break;
                    case MediaPacketType.Audio:
                        var audio = _owner._audio;
                        if (audio is not null)
                        {
                            var audioViewerUs = _clock.HostToViewerUs(header.TimestampUs);
                            var lagMs = _lastVideoTimestampViewerUs == 0 ? 0 : (_lastVideoTimestampViewerUs - audioViewerUs) / 1000.0;
                            audio.Play(payload, lagMs);
                        }
                        break;
                    case MediaPacketType.AudioPair:
                        _owner._audio?.PlayPair(header.FrameId, payload);
                        break;
                    case MediaPacketType.Probe:
                        Interlocked.Add(ref _probeBytes, length);
                        Interlocked.Increment(ref _probeCount);
                        break;
                }
            }
        }

        private void CountPacket(ulong number)
        {
            Interlocked.Increment(ref _windowReceived);
            Interlocked.Increment(ref _totalReceived);
            if (number > _highestPacket) _highestPacket = number;
            if (_windowStartPacket == 0) _windowStartPacket = number;
            if (_firstPacket == 0) _firstPacket = number;
        }

        private void Enqueue(List<AssembledFrame> frames)
        {
            foreach (var frame in frames)
            {
                if (frame.AfterGap && !frame.Keyframe) RequestKeyframe("perda de frame");
                if (!_decodeQueue.TryAdd(frame))
                {
                    // Decoder atrasado: descarta o mais antigo para não acumular latência.
                    _decodeQueue.TryTake(out _);
                    _decodeQueue.TryAdd(frame);
                    Interlocked.Increment(ref _decoderLate);
                    RequestKeyframe("decoder atrasado");
                }
            }
        }

        /// <summary>Frames da câmera: fila curta, porque no quadrinho vale mais o frame novo que todos os frames.</summary>
        private void EnqueueCamera(List<AssembledFrame> frames)
        {
            foreach (var frame in frames)
            {
                if (frame.AfterGap && !frame.Keyframe) RequestKeyframe("perda de frame da câmera", VideoTrack.Camera);
                if (!_cameraQueue.TryAdd(frame))
                {
                    _cameraQueue.TryTake(out _);
                    _cameraQueue.TryAdd(frame);
                    RequestKeyframe("decoder da câmera atrasado", VideoTrack.Camera);
                }
            }
        }

        private void RequestKeyframe(string reason, int track = VideoTrack.Main)
        {
            var now = MediaClock.NowUs;
            ref var last = ref track == VideoTrack.Camera ? ref _lastCameraKeyframeRequestUs : ref _lastKeyframeRequestUs;
            if (now - Interlocked.Read(ref last) < 300_000) return;
            Interlocked.Exchange(ref last, now);
            _ = SendAsync(new KeyframeRequestMessage(reason, track), CancellationToken.None);
        }

        private void DecodeLoop(CancellationToken token)
        {
            try
            {
                foreach (var frame in _decodeQueue.GetConsumingEnumerable(token))
                {
                    var presenter = _owner._presenter is { Failed: false } p ? p : null;
                    if (_decoder is null || _codecChanged || presenter != _decoderPresenter)
                    {
                        // Novo codec, ou o player na GPU apareceu/falhou: o decoder precisa do device certo.
                        _decoder?.Dispose();
                        _decoderPresenter?.ResetInputs();
                        _decoder = CreateDecoder(presenter);
                        _decoderPresenter = presenter;
                        _gpuHandler = _decoder.SharesPresenterDevice ? presenter!.Present : null;
                        _decoderLabel = _decoder.Description + (_gpuHandler is null ? string.Empty : " · desenho na GPU");
                        _codecChanged = false;
                        if (!frame.Keyframe) RequestKeyframe("decoder novo");
                    }
                    var outcome = _decoder.Decode(frame.Data, frame.TimestampUs, _gpuHandler, out var image);
                    if (outcome == DecodeOutcome.None) continue;
                    var now = MediaClock.NowUs;
                    _decoded.Add(1, now);
                    var viewerTs = _clock.HostToViewerUs(frame.TimestampUs);
                    _lastVideoTimestampViewerUs = viewerTs;
                    if (_clock.HasEstimate) _latencyMs = _latencyMs * 0.9 + (now - viewerTs) / 1000.0 * 0.1;
                    _owner.SetRenderPath(outcome == DecodeOutcome.Gpu);
                    if (image is not null) _owner.FrameReady?.Invoke(image);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception exception)
            {
                Log.Error("Falha no decoder", exception);
                _owner.SetState(ConnectionState.Error, $"Falha ao decodificar o vídeo: {exception.Message}");
                _stop.Cancel();
            }
            finally
            {
                _decoder?.Dispose();
                _decoder = null;
                _decoderPresenter?.ResetInputs();
            }
        }

        /// <summary>
        /// Decodifica a câmera que vai junto com a tela, sempre para a CPU (BGRA): o quadrinho é pequeno e é desenhado
        /// pelo WPF. Um problema aqui não derruba a tela: o decoder é recriado depois de alguns segundos.
        /// </summary>
        private void CameraDecodeLoop(CancellationToken token)
        {
            VideoDecoder? decoder = null;
            long retryAfterUs = 0;
            try
            {
                foreach (var frame in _cameraQueue.GetConsumingEnumerable(token))
                {
                    if (MediaClock.NowUs < retryAfterUs) continue;
                    try
                    {
                        var codec = _config.Codec;
                        if (decoder is null || decoder.Codec != codec)
                        {
                            decoder?.Dispose();
                            decoder = null;
                            decoder = new VideoDecoder(codec);
                            if (!frame.Keyframe) RequestKeyframe("decoder da câmera novo", VideoTrack.Camera);
                        }
                        var image = decoder.Decode(frame.Data, frame.TimestampUs);
                        if (image is null) continue;
                        _cameraDecoded.Add(1, MediaClock.NowUs);
                        _cameraWidth = image.Width;
                        _cameraHeight = image.Height;
                        _owner.CameraFrameReady?.Invoke(image);
                    }
                    catch (Exception exception) when (exception is not OperationCanceledException)
                    {
                        Log.Warn($"Câmera junto com a tela: {exception.Message}. Tentando de novo em 3 segundos.");
                        decoder?.Dispose();
                        decoder = null;
                        retryAfterUs = MediaClock.NowUs + 3_000_000;
                    }
                }
            }
            catch (OperationCanceledException) { }
            finally
            {
                decoder?.Dispose();
            }
        }

        private VideoDecoder CreateDecoder(D3D11VideoPresenter? presenter)
        {
            if (presenter is not null)
            {
                try { return new VideoDecoder(_config.Codec, presenter.DevicePointer); }
                catch (Exception exception) { Log.Warn($"Decoder no device do player falhou ({exception.Message}). Usando a CPU."); }
            }
            return new VideoDecoder(_config.Codec);
        }

        private async Task TickLoopAsync(CancellationToken token)
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(4));
            while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
            {
                List<NackRequest> nacks;
                List<AssembledFrame> ready;
                int dropped;
                // Pacotes ainda esperando no socket (o PC ficou ocupado): os que faltam podem estar ali, o prazo dobra.
                var pending = InputPending();
                lock (_reassemblyGate) (nacks, ready, dropped) = _reassembler.Tick(MediaClock.Now, pending);
                Enqueue(ready);
                if (dropped > 0) RequestKeyframe("frames perdidos");
                foreach (var nack in nacks) await SendAsync(new NackMessage(nack.FrameId, nack.Missing), token);

                if (!_cameraSeen) continue;
                lock (_cameraGate) (nacks, ready, dropped) = _cameraReassembler.Tick(MediaClock.Now, pending);
                EnqueueCamera(ready);
                if (dropped > 0) RequestKeyframe("frames da câmera perdidos", VideoTrack.Camera);
                foreach (var nack in nacks) await SendAsync(new NackMessage(nack.FrameId, nack.Missing, VideoTrack.Camera), token);
            }
        }

        private bool InputPending()
        {
            try { return _udp.Available > 0; }
            catch (Exception exception) when (exception is SocketException or ObjectDisposedException) { return false; }
        }

        /// <summary>
        /// Prazo e espera entre NACKs pelo RTT: com o Radmin passando por um relay o RTT chega a 150 ms, e 120 ms de prazo
        /// não davam nem para uma retransmissão.
        /// </summary>
        private void TuneReassembly(double rttMs)
        {
            if (rttMs <= 0) return;
            var deadline = TimeSpan.FromMilliseconds(Math.Clamp(rttMs * 3 + 50, 120, 500));
            var retry = TimeSpan.FromMilliseconds(Math.Clamp(rttMs * 1.25 + 5, 10, 150));
            lock (_reassemblyGate)
            {
                _reassembler.Deadline = deadline;
                _reassembler.NackRetryInterval = retry;
            }
            lock (_cameraGate)
            {
                _cameraReassembler.Deadline = deadline;
                _cameraReassembler.NackRetryInterval = retry;
            }
        }

        private async Task TimerLoopAsync(CancellationToken token)
        {
            var punch = new byte[MediaHeader.Size];
            new MediaHeader { Type = MediaPacketType.Probe }.Write(punch);
            var ticks = 0;
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(250));
            while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
            {
                ticks++;
                await SendAsync(new PingMessage(MediaClock.NowUs), token);
                if (ticks % 4 != 0) continue;

                // Mantém o firewall/NAT aberto para o tráfego UDP do host.
                try { _udp.SendTo(punch, _hostMedia); } catch { }

                var now = MediaClock.NowUs;
                var received = Interlocked.Exchange(ref _windowReceived, 0);
                var expected = (double)(_highestPacket - _windowStartPacket);
                _windowStartPacket = _highestPacket;
                _lossRate = expected <= 0 ? 0 : Math.Clamp(1 - received / expected, 0, 1);
                var totalExpected = _firstPacket == 0 ? 0 : (double)(_highestPacket - _firstPacket + 1);
                var totalLoss = totalExpected <= 0 ? 0 : Math.Clamp(1 - Interlocked.Read(ref _totalReceived) / totalExpected, 0, 1);
                var kbps = _bytes.PerSecond(now) * 8 / 1000.0;
                var fps = _decoded.PerSecond(now);
                TuneReassembly(_clock.RoundTripMs);
                await SendAsync(new StatsMessage(_lossRate, _clock.RoundTripMs, kbps, fps, _latencyMs), token);

                var audio = _owner._audio;
                long expired, missing, superseded, fec, nack;
                lock (_reassemblyGate)
                    (expired, missing, superseded, fec, nack) = (_reassembler.FramesExpired, _reassembler.FramesMissing, _reassembler.FramesSuperseded,
                        _reassembler.PacketsRecoveredByFec, _reassembler.PacketsRecoveredByNack);
                var stats = new ViewerStatistics(fps, kbps / 1000, _latencyMs, _clock.RoundTripMs, _lossRate,
                    _config.VideoEnabled ? Quality.CodecLabel(_config.Codec) : "Opus", _config.Width, _config.Height,
                    _config.VideoEnabled ? _decoderLabel : "Opus · só áudio", expired + missing + superseded, fec, _config.Reason,
                    _config.Mode, audio?.Buffered.TotalMilliseconds ?? 0, audio?.PacketsRecovered ?? 0,
                    _config.VideoEnabled ? 0 : kbps, _owner._gpuRendering == true,
                    _cameraSeen ? _cameraDecoded.PerSecond(now) : 0, _cameraWidth, _cameraHeight)
                {
                    TotalLossRate = totalLoss,
                    FramesExpired = expired,
                    FramesMissing = missing,
                    FramesSuperseded = superseded,
                    NackRecovered = nack,
                    DecoderLate = Interlocked.Read(ref _decoderLate),
                };
                _owner._lastStats = stats;
                _owner.StatisticsUpdated?.Invoke(stats);
            }
        }

        private async Task SendAsync(ControlMessage message, CancellationToken token)
        {
            await _writeLock.WaitAsync(token).ConfigureAwait(false);
            try { await ControlFraming.WriteAsync(_stream, message, token).ConfigureAwait(false); }
            catch (Exception exception) when (exception is IOException or ObjectDisposedException) { _stop.Cancel(); }
            finally { _writeLock.Release(); }
        }

        /// <summary>Manda um pedido de quem assiste fora dos laços da conexão. false se a conexão já caiu.</summary>
        public async Task<bool> TrySendAsync(ControlMessage message)
        {
            try
            {
                var token = _stop.Token;
                await SendAsync(message, token).ConfigureAwait(false);
                return !token.IsCancellationRequested;
            }
            catch (Exception exception) when (exception is OperationCanceledException or ObjectDisposedException) { return false; }
        }

        public void Dispose()
        {
            _stop.Cancel();
            _decodeQueue.Dispose();
            _cameraQueue.Dispose();
            _stop.Dispose();
        }
    }
}
