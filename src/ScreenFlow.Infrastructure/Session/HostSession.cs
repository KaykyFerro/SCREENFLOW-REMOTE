using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using ScreenFlow.Core;
using ScreenFlow.Core.Diagnostics;
using ScreenFlow.Core.Media;
using ScreenFlow.Core.Protocol;
using ScreenFlow.Core.Security;
using ScreenFlow.Core.Settings;
using ScreenFlow.Infrastructure.Capture;
using ScreenFlow.Infrastructure.Media;
using ScreenFlow.Infrastructure.Network;

namespace ScreenFlow.Infrastructure.Session;

public sealed record HostOptions(
    string DeviceName,
    IPAddress BindAddress,
    IPAddress BroadcastAddress,
    int MediaPort,
    int ControlPort,
    int DiscoveryPort,
    string? Pin,
    bool ApproveEachViewer,
    int MaxViewers,
    CaptureTarget Target,
    DisplayMonitor? Monitor,
    Resolution Resolution,
    int Fps,
    int BitrateKbps,
    VideoCodec? PreferredCodec,
    string? PreferredEncoder,
    IReadOnlyList<EncoderInfo> Encoders,
    bool AudioEnabled,
    int AudioBitrateKbps,
    bool Adaptive,
    int FecGroupSize,
    double UploadMbps);

public enum ViewerState { Authenticating, WaitingApproval, Streaming }

public sealed record ViewerInfo(Guid Id, string Name, string Address, ViewerState State, double RoundTripMs, double LossRate, double ReceivedMbps, double LatencyMs);

public sealed record HostStatistics(int Viewers, double EncodeFps, double BitrateMbps, double UploadMbps, string Encoder, string Capture, StreamTarget Target, string Reason);

public sealed record ApprovalRequest(Guid Id, string Name, string Address);

/// <summary>
/// Sessão de transmissão: aceita viewers no canal TCP, captura e codifica a tela uma vez só e envia o mesmo
/// vídeo (criptografado por viewer) para todos via UDP. Suporta dezenas de viewers; o limite prático é o upload.
/// </summary>
public sealed class HostSession : IAsyncDisposable
{
    private readonly HostOptions _options;
    private readonly ConcurrentDictionary<Guid, RemoteViewer> _viewers = new();
    private readonly ConcurrentDictionary<string, (DateTimeOffset Start, int Count)> _failures = new();
    private readonly CancellationTokenSource _cancellation = new();
    private readonly RetransmitCache _retransmit = new(96);
    private readonly AutoResetEvent _wake = new(false);
    private readonly RateMeter _encodedFrames = new();
    private readonly RateMeter _encodedBytes = new();
    private TcpListener? _listener;
    private Socket? _udp;
    private MediaSender? _sender;
    private DiscoveryAnnouncer? _announcer;
    private Thread? _pipeline;
    private Task? _acceptLoop;
    private Task? _udpDrain;
    private SystemAudioCapture? _audio;
    private uint _audioSequence;
    private volatile bool _keyframeRequested;
    private StreamTarget _profile;
    private volatile StreamTarget _target;
    private VideoCodec _codec;
    private string _encoderName = "";
    private AdaptiveBitrateController? _adaptive;
    private string _reason = "perfil escolhido";
    private string _captureBackend = "";
    private long _lastKeyframeUs;

    public HostSession(HostOptions options)
    {
        _options = options;
        var bitrate = options.BitrateKbps > 0 ? options.BitrateKbps : Quality.TargetBitrateKbps(options.Resolution, options.Fps, options.PreferredCodec ?? VideoCodec.Hevc);
        _profile = new StreamTarget(bitrate, options.Fps, options.Resolution);
        _target = _profile;
    }

    public event Action<string>? StatusChanged;
    public event Action? ViewersChanged;
    public event Action<HostStatistics>? StatisticsUpdated;
    public event Action<string, double>? BandwidthMeasured;
    public Func<ApprovalRequest, CancellationToken, Task<bool>>? ApprovalHandler { get; set; }

    public IReadOnlyList<ViewerInfo> Viewers => _viewers.Values.Select(v => v.Info).OrderBy(v => v.Name).ToArray();
    public HostOptions Options => _options;

    public void Start()
    {
        _udp = MediaSender.CreateUdp(_options.BindAddress, _options.MediaPort);
        _sender = new MediaSender(_udp);
        _listener = new TcpListener(_options.BindAddress, _options.ControlPort);
        _listener.Start(32);
        _announcer = new DiscoveryAnnouncer(_options.BindAddress, _options.BroadcastAddress, _options.DiscoveryPort, () =>
            new DiscoveryAnnouncement(AppInfo.Name, AppInfo.ProtocolVersion, _options.DeviceName, _options.BindAddress.ToString(), _options.ControlPort, AppInfo.DisplayVersion, !string.IsNullOrEmpty(_options.Pin), _viewers.Count));
        _announcer.Start();
        _acceptLoop = Task.Run(AcceptLoopAsync);
        _udpDrain = Task.Run(DrainUdpAsync);
        _pipeline = new Thread(PipelineLoop) { IsBackground = true, Name = "ScreenFlow pipeline", Priority = ThreadPriority.AboveNormal };
        _pipeline.Start();
        _ = Task.Run(StatsLoopAsync);
        Log.Info($"Host iniciado em {_options.BindAddress} (controle TCP {_options.ControlPort}, mídia UDP {_options.MediaPort}), perfil {_profile}");
        StatusChanged?.Invoke($"Transmitindo em {_options.BindAddress}. Aguardando viewers.");
    }

    // ───────────────────────── Canal de controle ─────────────────────────

    private async Task AcceptLoopAsync()
    {
        var token = _cancellation.Token;
        while (!token.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _listener!.AcceptTcpClientAsync(token); }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (SocketException exception) { Log.Warn($"accept: {exception.Message}"); continue; }
            _ = Task.Run(() => HandleViewerAsync(client, token));
        }
    }

    private async Task HandleViewerAsync(TcpClient client, CancellationToken token)
    {
        client.NoDelay = true;
        var remote = (IPEndPoint)client.Client.RemoteEndPoint!;
        var ip = remote.Address.ToString();
        RemoteViewer? viewer = null;
        try
        {
            if (IsThrottled(ip))
            {
                await ControlFraming.WriteAsync(client.GetStream(), new RejectedMessage(RejectedMessage.Throttled, "Muitas tentativas erradas. Aguarde um minuto."), token);
                return;
            }

            var stream = client.GetStream();
            using var handshakeTimeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            handshakeTimeout.CancelAfter(TimeSpan.FromSeconds(15));
            if (await ControlFraming.ReadAsync(stream, handshakeTimeout.Token) is not HelloMessage hello)
                return;
            if (hello.Protocol != AppInfo.ProtocolVersion)
            {
                await ControlFraming.WriteAsync(stream, new RejectedMessage(RejectedMessage.VersionMismatch, $"Versões diferentes do ScreenFlow (host {AppInfo.DisplayVersion}, viewer {hello.AppVersion}). Atualize os dois."), token);
                return;
            }
            if (_viewers.Count >= _options.MaxViewers)
            {
                await ControlFraming.WriteAsync(stream, new RejectedMessage(RejectedMessage.Busy, $"O host já tem {_options.MaxViewers} pessoas assistindo."), token);
                return;
            }

            using var exchange = new KeyExchange();
            var nonce = SessionCrypto.CreateNonce();
            var pinRequired = !string.IsNullOrEmpty(_options.Pin);
            await ControlFraming.WriteAsync(stream, new ChallengeMessage(_options.DeviceName, exchange.PublicKey, nonce, pinRequired), token);
            if (await ControlFraming.ReadAsync(stream, handshakeTimeout.Token) is not AuthMessage auth) return;

            var secret = exchange.DeriveSharedSecret(hello.PublicKey);
            var transcript = SessionCrypto.Transcript(hello.PublicKey, exchange.PublicKey, nonce);
            if (!SessionCrypto.VerifyProof(secret, _options.Pin, transcript, auth.Proof))
            {
                RegisterFailure(ip);
                Log.Warn($"PIN incorreto de {ip}");
                await ControlFraming.WriteAsync(stream, new RejectedMessage(RejectedMessage.WrongPin, "PIN incorreto."), token);
                return;
            }
            _failures.TryRemove(ip, out _);

            var key = SessionCrypto.DeriveMediaKey(secret, transcript);
            CryptographicOperations.ZeroMemory(secret);
            var salt = RandomNumberGenerator.GetBytes(4);
            var name = string.IsNullOrWhiteSpace(hello.ViewerName) ? ip : hello.ViewerName.Trim()[..Math.Min(40, hello.ViewerName.Trim().Length)];
            viewer = new RemoteViewer(Guid.NewGuid(), name, client, stream, new IPEndPoint(remote.Address, hello.UdpPort), key, salt, hello);
            CryptographicOperations.ZeroMemory(key);
            _viewers[viewer.Id] = viewer;
            ViewersChanged?.Invoke();

            if (_options.ApproveEachViewer)
            {
                viewer.State = ViewerState.WaitingApproval;
                ViewersChanged?.Invoke();
                await viewer.SendAsync(new PendingApprovalMessage("Aguardando o host aprovar a sua entrada."), token);
                var approved = false;
                if (ApprovalHandler is not null)
                {
                    using var approvalTimeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                    approvalTimeout.CancelAfter(TimeSpan.FromMinutes(2));
                    try { approved = await ApprovalHandler(new ApprovalRequest(viewer.Id, name, ip), approvalTimeout.Token); }
                    catch (OperationCanceledException) when (!token.IsCancellationRequested) { approved = false; }
                }
                if (!approved)
                {
                    await viewer.SendAsync(new RejectedMessage(RejectedMessage.Denied, "O host recusou a conexão."), token);
                    return;
                }
            }

            // Codec comum a todos que estão assistindo (inclui este).
            if (!Renegotiate(viewer))
            {
                await viewer.SendAsync(new RejectedMessage(RejectedMessage.NoCodec, "Este PC não decodifica nenhum codec que o host consegue gerar."), token);
                return;
            }

            await viewer.SendAsync(new WelcomeMessage(_options.DeviceName, Convert.ToBase64String(salt), _options.MediaPort, CurrentConfig("conexão")), token);
            viewer.State = ViewerState.Streaming;
            _keyframeRequested = true;
            _wake.Set();
            ViewersChanged?.Invoke();
            Log.Info($"Viewer {name} ({ip}) conectado. Total: {_viewers.Count}");
            StatusChanged?.Invoke($"{name} começou a assistir.");
            EnsureAudio();

            await ControlLoopAsync(viewer, token);
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) when (exception is IOException or SocketException or InvalidDataException or System.Text.Json.JsonException)
        {
            Log.Info($"Conexão com {ip} encerrada: {exception.Message}");
        }
        catch (Exception exception)
        {
            Log.Error($"Erro com viewer {ip}", exception);
        }
        finally
        {
            if (viewer is not null && _viewers.TryRemove(viewer.Id, out _))
            {
                viewer.Dispose();
                ViewersChanged?.Invoke();
                if (viewer.State == ViewerState.Streaming) StatusChanged?.Invoke($"{viewer.Name} saiu.");
                Renegotiate(null);
            }
            client.Dispose();
        }
    }

    private async Task ControlLoopAsync(RemoteViewer viewer, CancellationToken token)
    {
        while (!token.IsCancellationRequested && !viewer.Kicked.IsCancellationRequested)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, viewer.Kicked.Token);
            var message = await ControlFraming.ReadAsync(viewer.Stream, linked.Token);
            switch (message)
            {
                case PingMessage ping:
                    await viewer.SendAsync(new PongMessage(ping.ViewerTimeUs, MediaClock.NowUs), token);
                    break;
                case StatsMessage stats:
                    viewer.LastStats = stats;
                    break;
                case NackMessage nack:
                    foreach (var chunk in _retransmit.Lookup(nack.FrameId, nack.Missing.Take(256)))
                        _sender!.Send(viewer.Cipher, ref viewer.PacketCounter, chunk, viewer.MediaEndpoint, retransmit: true);
                    break;
                case KeyframeRequestMessage:
                    _keyframeRequested = true;
                    break;
                case BandwidthTestResultMessage result:
                    Log.Info($"Teste de banda até {viewer.Name}: {result.ReceivedMbps:0.0} Mbps, perda {result.LossRate:P1}");
                    BandwidthMeasured?.Invoke(viewer.Name, result.ReceivedMbps);
                    break;
                case ByeMessage:
                    return;
            }
        }
    }

    public void Kick(Guid id)
    {
        if (_viewers.TryGetValue(id, out var viewer))
        {
            _ = viewer.SendAsync(new ByeMessage("O host removeu você da transmissão."), CancellationToken.None);
            viewer.Kicked.Cancel();
        }
    }

    /// <summary>Envia tráfego de teste para um viewer por alguns segundos e espera o resultado.</summary>
    public async Task RunBandwidthTestAsync(Guid id, int seconds = 3, int targetMbps = 400)
    {
        if (!_viewers.TryGetValue(id, out var viewer) || _sender is null) return;
        await viewer.SendAsync(new BandwidthTestRequestMessage(seconds, targetMbps), CancellationToken.None);
        await Task.Run(() =>
        {
            var payload = RandomNumberGenerator.GetBytes(Packetizer.DefaultPayloadSize);
            var bytesPerMs = targetMbps * 1_000_000 / 8.0 / 1000.0;
            var watch = Stopwatch.StartNew();
            double sent = 0;
            uint index = 0;
            while (watch.Elapsed.TotalSeconds < seconds && !viewer.Kicked.IsCancellationRequested)
            {
                while (sent < watch.Elapsed.TotalMilliseconds * bytesPerMs)
                {
                    var chunk = new MediaChunk { Header = new MediaHeader { Type = MediaPacketType.Probe, FrameId = index++, Count = 1 }, Payload = payload };
                    _sender.Send(viewer.Cipher, ref viewer.PacketCounter, chunk, viewer.MediaEndpoint);
                    sent += payload.Length + MediaHeader.Size;
                }
                Thread.Sleep(1);
            }
        });
    }

    private async Task DrainUdpAsync()
    {
        // Lê os "keepalive" dos viewers (abrem o firewall deles para o nosso tráfego) e descarta.
        var buffer = new byte[2048];
        EndPoint any = new IPEndPoint(IPAddress.Any, 0);
        while (!_cancellation.IsCancellationRequested)
        {
            try { await _udp!.ReceiveFromAsync(buffer, SocketFlags.None, any, _cancellation.Token); }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (SocketException) { }
        }
    }

    private bool IsThrottled(string ip)
        => _failures.TryGetValue(ip, out var f) && f.Count >= 5 && DateTimeOffset.UtcNow - f.Start < TimeSpan.FromMinutes(1);

    private void RegisterFailure(string ip)
    {
        var now = DateTimeOffset.UtcNow;
        _failures.AddOrUpdate(ip, _ => (now, 1), (_, f) => now - f.Start > TimeSpan.FromMinutes(1) ? (now, 1) : (f.Start, f.Count + 1));
    }

    // ───────────────────────── Negociação e configuração ─────────────────────────

    private readonly object _configGate = new();

    /// <summary>Escolhe o codec comum a todos os viewers (AV1 &gt; HEVC &gt; H.264) e o melhor encoder para ele.</summary>
    private bool Renegotiate(RemoteViewer? joining)
    {
        lock (_configGate)
        {
            var audience = _viewers.Values.Where(v => v.State == ViewerState.Streaming || v == joining).ToList();
            if (audience.Count == 0) return true;
            IEnumerable<VideoCodec> common = [VideoCodec.Av1, VideoCodec.Hevc, VideoCodec.H264];
            foreach (var v in audience) common = common.Intersect(v.Hello.Decoders);
            var hostCodecs = _options.Encoders.Select(e => e.Codec).Distinct().ToList();
            var codec = CodecNegotiation.Choose(hostCodecs, common, _options.PreferredCodec);
            if (codec is null) return false;

            var encoder = _options.Encoders.FirstOrDefault(e => e.FfmpegName == _options.PreferredEncoder && e.Codec == codec)
                ?? _options.Encoders.Where(e => e.Codec == codec).OrderByDescending(e => e.Hardware).First();

            // Limites do viewer mais fraco.
            var maxFps = audience.Min(v => v.Hello.MaxFps);
            var maxW = audience.Min(v => v.Hello.MaxWidth);
            var maxH = audience.Min(v => v.Hello.MaxHeight);
            var resolution = _options.Resolution;
            while (resolution != Resolution.P720 && (resolution.Width > Math.Max(1280, maxW) || resolution.Height > Math.Max(720, maxH)))
                resolution = Resolution.All[Resolution.All.ToList().IndexOf(resolution) - 1];
            var fps = Math.Min(_options.Fps, Math.Max(30, maxFps));
            var bitrate = _options.BitrateKbps > 0 ? _options.BitrateKbps : Quality.TargetBitrateKbps(resolution, fps, codec.Value);
            var profile = new StreamTarget(bitrate, fps, resolution);

            var changed = codec != _codec || encoder.FfmpegName != _encoderName || profile != _profile;
            _codec = codec.Value;
            _encoderName = encoder.FfmpegName;
            if (changed || _adaptive is null)
            {
                _profile = profile;
                _adaptive = _options.Adaptive ? new AdaptiveBitrateController(profile, _codec) : null;
                SetTarget(ApplyUploadBudget(profile), "negociação com os viewers");
            }
            return true;
        }
    }

    private StreamTarget ApplyUploadBudget(StreamTarget target)
    {
        var watching = Math.Max(1, _viewers.Values.Count(v => v.State == ViewerState.Streaming));
        var limit = Quality.PerViewerBitrateKbps(target.BitrateKbps, _options.UploadMbps, watching);
        return limit < target.BitrateKbps ? target with { BitrateKbps = limit } : target;
    }

    private void SetTarget(StreamTarget target, string reason)
    {
        _target = target;
        _reason = reason;
        _wake.Set();
    }

    private StreamConfigMessage CurrentConfig(string reason)
    {
        var (w, h) = _target.Resolution.FitTo(_options.Target.Width, _options.Target.Height);
        return new StreamConfigMessage(_codec, w, h, _target.Fps, _target.BitrateKbps, _options.AudioEnabled, SystemAudioCapture.SampleRate, SystemAudioCapture.Channels, _encoderName, reason);
    }

    private async Task StatsLoopAsync()
    {
        var token = _cancellation.Token;
        while (!token.IsCancellationRequested)
        {
            try { await Task.Delay(1000, token); } catch (OperationCanceledException) { break; }
            var streaming = _viewers.Values.Where(v => v.State == ViewerState.Streaming).ToList();

            // Controle adaptativo pela pior rede entre os viewers.
            if (_adaptive is not null && streaming.Count > 0)
            {
                var samples = streaming.Where(v => v.LastStats is not null).Select(v => v.LastStats!).ToList();
                if (samples.Count > 0)
                {
                    var worst = new NetworkSample(samples.Max(s => s.LossRate), samples.Max(s => s.RoundTripMs), samples.Min(s => s.ReceivedKbps), samples.Min(s => s.DecodeFps));
                    if (_adaptive.Update(worst) is { } next) SetTarget(ApplyUploadBudget(next), _adaptive.LastReason);
                    else if (ApplyUploadBudget(_adaptive.Current) is var budget && budget != _target) SetTarget(budget, $"upload dividido entre {streaming.Count} viewers");
                }
            }

            var now = MediaClock.NowUs;
            var sentMbps = 0.0;
            if (_sender is not null)
            {
                var total = Interlocked.Exchange(ref _sender.BytesSent, 0);
                sentMbps = total * 8 / 1_000_000.0;
            }
            StatisticsUpdated?.Invoke(new HostStatistics(streaming.Count, _encodedFrames.PerSecond(now), _encodedBytes.PerSecond(now) * 8 / 1_000_000.0, sentMbps,
                string.IsNullOrEmpty(_encoderName) ? "—" : $"{Quality.CodecLabel(_codec)} · {_encoderName}", _captureBackend, _target, _reason));
            ViewersChanged?.Invoke();
        }
    }

    // ───────────────────────── Pipeline de mídia ─────────────────────────

    private void PipelineLoop()
    {
        GpuDevice? gpu = null;
        ICaptureSource? capture = null;
        GpuColorConverter? converter = null;
        VideoEncoder? encoder = null;
        StreamTarget? active = null;
        string? activeEncoder = null;
        uint frameId = 0;
        var packets = new List<EncodedPacket>(4);
        long lastEncodeUs = 0;

        void TearDown()
        {
            encoder?.Dispose(); encoder = null;
            converter?.Dispose(); converter = null;
            capture?.Dispose(); capture = null;
            gpu?.Dispose(); gpu = null;
            active = null;
        }

        try
        {
            while (!_cancellation.IsCancellationRequested)
            {
                var audience = _viewers.Values.Where(v => v.State == ViewerState.Streaming).ToArray();
                if (audience.Length == 0)
                {
                    if (gpu is not null) { TearDown(); Log.Info("Ninguém assistindo: captura e encoder pausados."); }
                    _wake.WaitOne(250);
                    continue;
                }

                try
                {
                    var target = _target;
                    if (gpu is null)
                    {
                        gpu = GpuDevice.Create(_options.Monitor?.DeviceName);
                        capture = CaptureFactory.Create(gpu, _options.Target, _options.Monitor, target.Fps);
                        _captureBackend = capture.Backend;
                    }
                    if (capture!.SizeChanged)
                    {
                        Log.Info("Tamanho da origem mudou: recriando captura.");
                        TearDown();
                        continue;
                    }

                    if (active != target || activeEncoder != _encoderName)
                    {
                        var onlyBitrate = active is not null && encoder is not null && activeEncoder == _encoderName
                                          && active.Fps == target.Fps && active.Resolution == target.Resolution
                                          && encoder.TryUpdateBitrate(target.BitrateKbps);
                        if (!onlyBitrate)
                        {
                            encoder?.Dispose();
                            converter?.Dispose();
                            var (w, h) = target.Resolution.FitTo(capture.Width, capture.Height);
                            converter = new GpuColorConverter(gpu, capture.Texture, capture.Width, capture.Height, w, h, target.Fps);
                            encoder = OpenEncoder(gpu, w, h, target);
                            _keyframeRequested = true;
                        }
                        active = target;
                        activeEncoder = _encoderName;
                        BroadcastConfig(onlyBitrate ? "bitrate" : _reason);
                    }

                    var interval = 1_000_000 / target.Fps;
                    var got = capture.WaitForFrame(TimeSpan.FromMilliseconds(Math.Max(1, interval / 1000)), out var timestamp);
                    var now = MediaClock.NowUs;
                    var since = now - lastEncodeUs;
                    // Tela parada: reenvia a cada 100 ms para manter o intra-refresh e os viewers vivos.
                    if (!got && since < 100_000 && !_keyframeRequested) continue;
                    if (got && since < interval * 0.85 && !_keyframeRequested) continue;
                    if (!got) timestamp = now;
                    lastEncodeUs = now;

                    if (_keyframeRequested && now - _lastKeyframeUs > 200_000)
                    {
                        encoder!.RequestKeyframe();
                        _lastKeyframeUs = now;
                        _keyframeRequested = false;
                    }

                    converter!.Convert();
                    packets.Clear();
                    encoder!.Encode(converter.Output, timestamp, packets);
                    foreach (var packet in packets)
                    {
                        var id = frameId++;
                        var chunks = Packetizer.Packetize(MediaPacketType.Video, id, packet.Data, packet.TimestampUs, packet.Keyframe, fecGroupSize: _options.FecGroupSize);
                        _retransmit.Store(id, chunks);
                        _encodedFrames.Add(1, now);
                        _encodedBytes.Add(packet.Data.Length, now);
                        foreach (var viewer in audience)
                            foreach (var chunk in chunks)
                                _sender!.Send(viewer.Cipher, ref viewer.PacketCounter, chunk, viewer.MediaEndpoint);
                    }
                }
                catch (Exception exception) when (!_cancellation.IsCancellationRequested)
                {
                    Log.Error("Falha no pipeline de vídeo", exception);
                    StatusChanged?.Invoke($"Erro na captura ou no encoder: {exception.Message}. Tentando de novo…");
                    TearDown();
                    Thread.Sleep(1000);
                }
            }
        }
        finally
        {
            TearDown();
        }
    }

    private VideoEncoder OpenEncoder(GpuDevice gpu, int width, int height, StreamTarget target)
    {
        var settings = new EncoderSettings(_encoderName, _codec, width, height, target.Fps, target.BitrateKbps);
        try
        {
            return new VideoEncoder(gpu, settings);
        }
        catch (Exception exception)
        {
            // Encoder escolhido falhou (driver, sessão NVENC esgotada...). Tenta os outros do mesmo codec e depois H.264.
            Log.Warn($"Encoder {_encoderName} falhou: {exception.Message}");
            foreach (var fallback in _options.Encoders.Where(e => e.FfmpegName != _encoderName && _viewers.Values.All(v => v.Hello.Decoders.Contains(e.Codec)))
                         .OrderByDescending(e => e.Codec == _codec).ThenByDescending(e => e.Hardware))
            {
                try
                {
                    var encoder = new VideoEncoder(gpu, settings with { EncoderName = fallback.FfmpegName, Codec = fallback.Codec });
                    _encoderName = fallback.FfmpegName;
                    _codec = fallback.Codec;
                    if (!fallback.Hardware) StatusChanged?.Invoke("Usando encoder por software (x264). 4K a 165 FPS não vai rodar assim.");
                    return encoder;
                }
                catch (Exception inner) { Log.Warn($"Encoder {fallback.FfmpegName} também falhou: {inner.Message}"); }
            }
            throw;
        }
    }

    private void BroadcastConfig(string reason)
    {
        var config = CurrentConfig(reason);
        foreach (var viewer in _viewers.Values.Where(v => v.State == ViewerState.Streaming))
            _ = viewer.SendAsync(config, CancellationToken.None);
    }

    private void EnsureAudio()
    {
        if (!_options.AudioEnabled || _audio is not null) return;
        try
        {
            _audio = new SystemAudioCapture(_options.AudioBitrateKbps);
            _audio.PacketReady += OnAudioPacket;
            _audio.Start();
        }
        catch (Exception exception)
        {
            Log.Error("Áudio do sistema indisponível", exception);
            StatusChanged?.Invoke($"Áudio do sistema indisponível: {exception.Message}");
        }
    }

    private void OnAudioPacket(AudioPacket packet)
    {
        if (_sender is null) return;
        var chunk = new MediaChunk
        {
            Header = new MediaHeader { Type = MediaPacketType.Audio, FrameId = _audioSequence++, Count = 1, TimestampUs = packet.TimestampUs },
            Payload = packet.Data,
        };
        foreach (var viewer in _viewers.Values)
            if (viewer.State == ViewerState.Streaming)
                _sender.Send(viewer.Cipher, ref viewer.PacketCounter, chunk, viewer.MediaEndpoint);
    }

    public async ValueTask DisposeAsync()
    {
        if (_cancellation.IsCancellationRequested) return;
        foreach (var viewer in _viewers.Values)
        {
            try { await viewer.SendAsync(new ByeMessage("O host encerrou a transmissão."), CancellationToken.None).WaitAsync(TimeSpan.FromMilliseconds(300)); } catch { }
        }
        _cancellation.Cancel();
        _wake.Set();
        _listener?.Stop();
        if (_announcer is not null) await _announcer.DisposeAsync();
        _audio?.Dispose();
        foreach (var viewer in _viewers.Values) viewer.Dispose();
        _viewers.Clear();
        _pipeline?.Join(TimeSpan.FromSeconds(3));
        _udp?.Dispose();
        if (_acceptLoop is not null) try { await _acceptLoop; } catch { }
        if (_udpDrain is not null) try { await _udpDrain; } catch { }
        Log.Info("Host encerrado e recursos liberados.");
        StatusChanged?.Invoke("Transmissão encerrada.");
    }

    private sealed class RemoteViewer : IDisposable
    {
        private readonly SemaphoreSlim _writeLock = new(1, 1);

        public RemoteViewer(Guid id, string name, TcpClient client, NetworkStream stream, IPEndPoint mediaEndpoint, byte[] key, byte[] salt, HelloMessage hello)
        {
            Id = id;
            Name = name;
            Client = client;
            Stream = stream;
            MediaEndpoint = mediaEndpoint;
            Cipher = new PacketCipher(key, salt);
            Hello = hello;
        }

        public Guid Id { get; }
        public string Name { get; }
        public TcpClient Client { get; }
        public NetworkStream Stream { get; }
        public IPEndPoint MediaEndpoint { get; }
        public PacketCipher Cipher { get; }
        public HelloMessage Hello { get; }
        public ulong PacketCounter;
        public volatile ViewerState State = ViewerState.Authenticating;
        public StatsMessage? LastStats { get; set; }
        public CancellationTokenSource Kicked { get; } = new();

        public ViewerInfo Info => new(Id, Name, MediaEndpoint.Address.ToString(), State,
            LastStats?.RoundTripMs ?? 0, LastStats?.LossRate ?? 0, (LastStats?.ReceivedKbps ?? 0) / 1000, LastStats?.LatencyMs ?? 0);

        public async Task SendAsync(ControlMessage message, CancellationToken token)
        {
            await _writeLock.WaitAsync(token);
            try { await ControlFraming.WriteAsync(Stream, message, token); }
            catch (Exception exception) when (exception is IOException or ObjectDisposedException) { }
            finally { _writeLock.Release(); }
        }

        public void Dispose()
        {
            Kicked.Cancel();
            Cipher.Dispose();
            Client.Dispose();
        }
    }
}
