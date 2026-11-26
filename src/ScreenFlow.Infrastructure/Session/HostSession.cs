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
    double UploadMbps,
    StreamMode Mode = StreamMode.Screen,
    int MusicBitrateKbps = 256,
    Func<AudioProfile, IAudioSource>? AudioSourceFactory = null,
    CameraOverlay? OverlayCamera = null,
    SystemAudioOptions? AudioSeparation = null);

/// <summary>Câmera junto com a tela: vai numa segunda faixa de vídeo e aparece num quadrinho por cima da tela.</summary>
public sealed record CameraOverlay(string DeviceId, string Name, int Width, int Height, int Fps);

public enum ViewerState { Authenticating, WaitingApproval, Streaming }

public sealed record ViewerInfo(Guid Id, string Name, string Address, ViewerState State, double RoundTripMs, double LossRate, double ReceivedMbps, double LatencyMs);

/// <param name="Camera">A câmera que vai junto com a tela ("1280×720 · 30 FPS · 2,1 Mbps"), o problema dela, ou vazio.</param>
public sealed record HostStatistics(int Viewers, double EncodeFps, double BitrateMbps, double UploadMbps, string Encoder, string Capture, StreamTarget Target, string Reason,
    StreamMode Mode = StreamMode.Screen, double AudioKbps = 0, string Camera = "");

public sealed record ApprovalRequest(Guid Id, string Name, string Address);

/// <summary>
/// Sessão de transmissão: aceita viewers no canal TCP, captura e codifica a tela (ou a câmera) uma vez só e envia o
/// mesmo vídeo (criptografado por viewer) para todos via UDP. Suporta dezenas de viewers; o limite prático é o upload.
/// No modo festa de música não há vídeo: só o áudio do PC em qualidade de música, com redundância.
/// Com "câmera junto", a câmera vai numa segunda faixa de vídeo (pacotes próprios, encoder próprio) e quem assiste vê
/// num quadrinho por cima da tela.
/// </summary>
public sealed partial class HostSession : IAsyncDisposable
{
    // Trocado inteiro (é imutável) quando quem transmite muda a qualidade ao vivo.
    private volatile HostOptions _options;
    private readonly ConcurrentDictionary<Guid, RemoteViewer> _viewers = new();
    private readonly ConcurrentDictionary<string, (DateTimeOffset Start, int Count)> _failures = new();
    private readonly CancellationTokenSource _cancellation = new();
    // ~1,5 s a 165 FPS e mais de 4 s a 60 FPS: cobre o prazo de remontagem do viewer com folga.
    private readonly RetransmitCache _retransmit = new(256);
    private readonly RetransmitCache _cameraRetransmit = new(96, MediaPacketType.CameraVideo);
    private readonly AutoResetEvent _wake = new(false);
    private readonly AutoResetEvent _cameraWake = new(false);
    private readonly RateMeter _cameraFrames = new();
    private readonly RateMeter _cameraBytes = new();
    private readonly RateMeter _encodedFrames = new();
    private readonly RateMeter _encodedBytes = new();
    private readonly RateMeter _audioBytes = new();
    private readonly AudioProfile _audioProfile;
    private TcpListener? _listener;
    private Socket? _udp;
    private MediaSender? _sender;
    private Pacer? _pacer;
    private IDisposable? _timerResolution;
    private DiscoveryAnnouncer? _announcer;
    private Thread? _pipeline;
    private Thread? _cameraPipeline;
    private Task? _acceptLoop;
    private Task? _udpDrain;
    private IAudioSource? _audio;
    private uint _audioSequence;
    private byte[] _previousAudio = [];
    private volatile bool _keyframeRequested;
    private volatile bool _cameraKeyframeRequested;
    private volatile string? _cameraProblem;
    private volatile string _cameraFormat = "";
    private int _cameraKbps;
    private StreamTarget _profile;
    private volatile StreamTarget _target;
    private VideoCodec _codec;
    private string _encoderName = "";
    private AdaptiveBitrateController? _adaptive;
    private string _reason = "perfil escolhido";
    private string _captureBackend = "";
    private long _lastKeyframeUs;
    private (int Width, int Height) _sourceSize;

    public HostSession(HostOptions options)
    {
        _options = options;
        var bitrate = options.BitrateKbps > 0 ? options.BitrateKbps : Quality.TargetBitrateKbps(options.Resolution, options.Fps, options.PreferredCodec ?? VideoCodec.Hevc);
        _profile = new StreamTarget(bitrate, options.Fps, options.Resolution);
        _target = _profile;
        _audioProfile = AudioProfile.ForMode(options.Mode, options.AudioBitrateKbps, options.MusicBitrateKbps);
        _sourceSize = (options.Target.Width, options.Target.Height);
        if (HasCameraOverlay)
        {
            var camera = options.OverlayCamera!;
            _cameraKbps = Quality.CameraOverlayBitrateKbps(camera.Width, camera.Height, camera.Fps, options.PreferredCodec ?? VideoCodec.Hevc);
        }
    }

    public StreamMode Mode => _options.Mode;
    public bool HasVideo => StreamModes.HasVideo(_options.Mode);
    public bool AudioEnabled => _options.AudioEnabled || _options.Mode == StreamMode.AudioOnly;

    /// <summary>A câmera vai junto com a tela (modo tela com "câmera junto" ligado).</summary>
    public bool HasCameraOverlay => _options.Mode == StreamMode.Screen && _options.OverlayCamera is not null;

    /// <summary>O que vai para quem assiste, em poucas palavras: "tela, câmera e áudio".</summary>
    public string Summary => StreamModes.Summary(_options.Mode, AudioEnabled, HasCameraOverlay);
    public AudioProfile AudioProfile => _audioProfile;

    /// <summary>Nível e espectro do áudio capturado (null sem áudio).</summary>
    public AudioMeter? AudioMeter => _audio?.Meter;
    public string? AudioDescription => _audio?.Description;

    /// <summary>O que vai no som: "todo o som do PC", "sem a voz da rede" ou "sem Spotify e a voz da rede". Null sem som.</summary>
    public string? AudioSeparation => (_audio as SystemAudioCapture)?.Separation;

    /// <summary>O som do PC está separado por app (a voz da rede fica fora). Null sem som do PC.</summary>
    public bool? AudioSeparated => (_audio as SystemAudioCapture)?.Separated;

    /// <summary>O app que vai sozinho na festa de música ("só o Spotify"). Null: vai o som do PC.</summary>
    public string? AudioOnlyApp => (_audio as SystemAudioCapture)?.OnlyApp;

    /// <summary>Troca os apps que ficam fora do som (vale na hora, durante a transmissão).</summary>
    public void ConfigureAudio(SystemAudioOptions options) => (_audio as SystemAudioCapture)?.Configure(options);

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
        _timerResolution = TimerResolution.Request();
        _pacer = new Pacer(_sender, PacingRate());
        _listener = new TcpListener(_options.BindAddress, _options.ControlPort);
        _listener.Start(32);
        _announcer = new DiscoveryAnnouncer(_options.BindAddress, _options.BroadcastAddress, _options.DiscoveryPort, () =>
            new DiscoveryAnnouncement(AppInfo.Name, AppInfo.ProtocolVersion, _options.DeviceName, _options.BindAddress.ToString(), _options.ControlPort, AppInfo.DisplayVersion,
                !string.IsNullOrEmpty(_options.Pin), _viewers.Values.Count(v => v.State == ViewerState.Streaming), _options.Mode, HasCameraOverlay));
        _announcer.Start();
        _acceptLoop = Task.Run(AcceptLoopAsync);
        _udpDrain = Task.Run(DrainUdpAsync);
        if (HasVideo)
        {
            _pipeline = new Thread(PipelineLoop) { IsBackground = true, Name = "ScreenFlow pipeline", Priority = ThreadPriority.AboveNormal };
            _pipeline.Start();
        }
        if (HasCameraOverlay)
        {
            _cameraPipeline = new Thread(CameraLoop) { IsBackground = true, Name = "ScreenFlow câmera junto", Priority = ThreadPriority.AboveNormal };
            _cameraPipeline.Start();
        }
        StartAudio();
        _ = Task.Run(StatsLoopAsync);
        Log.Info($"Host iniciado em {_options.BindAddress} (controle TCP {_options.ControlPort}, mídia UDP {_options.MediaPort}), modo {_options.Mode}, "
                 + (HasVideo ? $"perfil {_profile}" : $"áudio {_audioProfile.BitrateKbps} kbps")
                 + (HasCameraOverlay ? $", câmera junto ({_options.OverlayCamera!.Name})" : string.Empty));
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
            if (_options.Mode == StreamMode.AudioOnly && !hello.Supports(StreamModes.FeatureAudioOnly))
            {
                await ControlFraming.WriteAsync(stream, new RejectedMessage(RejectedMessage.VersionMismatch,
                    $"{_options.DeviceName} está numa festa de música (só áudio). Atualize o ScreenFlow para a versão {AppInfo.DisplayVersion} para ouvir."), token);
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
            viewer = new RemoteViewer(Guid.NewGuid(), name, client, stream, new IPEndPoint(remote.Address, hello.UdpPort), key, salt, hello, ip);
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

            await viewer.SendAsync(new WelcomeMessage(_options.DeviceName, Convert.ToBase64String(salt), _options.MediaPort, CurrentConfig("conexão"), [.. StreamModes.HostFeatures]), token);
            viewer.State = ViewerState.Streaming;
            _keyframeRequested = true;
            _cameraKeyframeRequested = true;
            _wake.Set();
            _cameraWake.Set();
            ViewersChanged?.Invoke();
            Log.Info($"Viewer {name} ({ip}) conectado. Total: {_viewers.Count}");
            StatusChanged?.Invoke(_options.Mode == StreamMode.AudioOnly ? $"{name} entrou na festa." : $"{name} começou a assistir.");
            UpdateAudioPause();

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
                UpdateAudioPause();
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
                    // Sem índices: o viewer não recebeu nada do frame e pede ele inteiro (1.1.1).
                    var cache = nack.Track == VideoTrack.Camera ? _cameraRetransmit : _retransmit;
                    var resend = cache.Lookup(nack.FrameId, (nack.Missing ?? []).Take(512));
                    if (resend.Count > 512) resend = resend[..512];
                    if (resend.Count > 0) _pacer?.EnqueueRetransmission(viewer, resend);
                    break;
                case KeyframeRequestMessage request:
                    if (request.Track == VideoTrack.Camera) _cameraKeyframeRequested = true;
                    else _keyframeRequested = true;
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
                    _sender.Send(viewer.Cipher, ref viewer.PacketCounter, chunk, viewer.MediaAddress);
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
            if (audience.Count == 0 || !HasVideo) return true;
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
        // Qualidade fixa: nada muda sozinho, nem a divisão do upload entre quem assiste.
        if (!_options.Adaptive) return target;
        var watching = Math.Max(1, _viewers.Values.Count(v => v.State == ViewerState.Streaming));
        // A câmera junto usa um pedaço do upload de cada pessoa: a tela fica com o resto.
        var camera = HasCameraOverlay ? Volatile.Read(ref _cameraKbps) : 0;
        var limit = Math.Max(1_000, Quality.PerViewerBitrateKbps(target.BitrateKbps + camera, _options.UploadMbps, watching) - camera);
        return limit < target.BitrateKbps ? target with { BitrateKbps = limit } : target;
    }

    /// <summary>
    /// Troca a qualidade durante a transmissão: resolução, FPS e se ela se adapta à rede (ou fica fixa). Vale na hora:
    /// o encoder é refeito com um keyframe e quem assiste recebe a configuração nova. O limite do viewer mais fraco
    /// (resolução e FPS que ele aguenta) continua valendo. No modo câmera, a câmera passa para o formato novo.
    /// </summary>
    public bool ChangeQuality(Resolution resolution, int fps, bool adaptive)
    {
        if (!HasVideo || fps <= 0) return false;
        lock (_configGate)
        {
            var options = _options;
            if (options.Resolution == resolution && options.Fps == fps && options.Adaptive == adaptive) return false;
            var target = options.Target.Kind == CaptureKind.Camera
                ? options.Target with { Width = resolution.Width, Height = resolution.Height, RefreshRateHz = fps }
                : options.Target;
            _options = options with { Resolution = resolution, Fps = fps, Adaptive = adaptive, Target = target };
            _adaptive = null;
            var reason = adaptive ? "qualidade escolhida por quem transmite" : "qualidade fixa escolhida por quem transmite";
            if (_viewers.Values.Any(v => v.State == ViewerState.Streaming)) Renegotiate(null);
            else
            {
                var bitrate = options.BitrateKbps > 0 ? options.BitrateKbps : Quality.TargetBitrateKbps(resolution, fps, _encoderName.Length > 0 ? _codec : options.PreferredCodec ?? VideoCodec.Hevc);
                _profile = new StreamTarget(bitrate, fps, resolution);
            }
            SetTarget(ApplyUploadBudget(_profile), reason);
            Log.Info($"Qualidade trocada ao vivo: {_profile} ({(adaptive ? "adaptativa" : "fixa")}).");
            return true;
        }
    }

    /// <summary>A qualidade se adapta à rede (false: fixa, do jeito que quem transmite escolheu).</summary>
    public bool AdaptiveQuality => _options.Adaptive;

    private void SetTarget(StreamTarget target, string reason)
    {
        _target = target;
        _reason = reason;
        _wake.Set();
    }

    private StreamConfigMessage CurrentConfig(string reason)
    {
        if (!HasVideo)
            return new StreamConfigMessage(VideoCodec.H264, 0, 0, 0, 0, true, AudioProfile.SampleRate, AudioProfile.Channels, string.Empty, reason,
                VideoEnabled: false, Mode: _options.Mode);
        var (w, h) = _target.Resolution.FitTo(_sourceSize.Width, _sourceSize.Height);
        return new StreamConfigMessage(_codec, w, h, _target.Fps, _target.BitrateKbps, AudioEnabled, AudioProfile.SampleRate, AudioProfile.Channels, _encoderName, reason,
            VideoEnabled: true, Mode: _options.Mode, CameraOverlay: HasCameraOverlay);
    }

    private async Task StatsLoopAsync()
    {
        var token = _cancellation.Token;
        while (!token.IsCancellationRequested)
        {
            try { await Task.Delay(1000, token); } catch (OperationCanceledException) { break; }
            var streaming = _viewers.Values.Where(v => v.State == ViewerState.Streaming).ToList();
            var now = MediaClock.NowUs;

            // Controle adaptativo pela pior rede entre os viewers. O FPS que o viewer decodifica é comparado com o que
            // saiu daqui: com a tela parada ou um vídeo de 30 FPS saem menos frames, e isso não é o viewer lento.
            if (_adaptive is not null && streaming.Count > 0)
            {
                var samples = streaming.Where(v => v.LastStats is not null).Select(v => v.LastStats!).ToList();
                if (samples.Count > 0)
                {
                    var worst = new NetworkSample(samples.Max(s => s.LossRate), samples.Max(s => s.RoundTripMs), samples.Min(s => s.ReceivedKbps), samples.Min(s => s.DecodeFps),
                        _encodedFrames.PerSecond(now));
                    if (_adaptive.Update(worst) is { } next) SetTarget(ApplyUploadBudget(next), _adaptive.LastReason);
                    else if (ApplyUploadBudget(_adaptive.Current) is var budget && budget != _target) SetTarget(budget, $"upload dividido entre {streaming.Count} viewers");
                }
            }
            if (_pacer is { } pacer) pacer.BytesPerSecond = PacingRate();

            var sentMbps = 0.0;
            if (_sender is not null)
            {
                var total = Interlocked.Exchange(ref _sender.BytesSent, 0);
                sentMbps = total * 8 / 1_000_000.0;
            }
            var encoder = !HasVideo ? $"Opus {_audioProfile.BitrateKbps} kbps" : string.IsNullOrEmpty(_encoderName) ? "—" : $"{Quality.CodecLabel(_codec)} · {_encoderName}";
            StatisticsUpdated?.Invoke(new HostStatistics(streaming.Count, _encodedFrames.PerSecond(now), _encodedBytes.PerSecond(now) * 8 / 1_000_000.0, sentMbps,
                encoder, HasVideo ? _captureBackend : "WASAPI loopback", _target, HasVideo ? _reason : "festa de música", _options.Mode, _audioBytes.PerSecond(now) * 8 / 1000.0,
                CameraSummary(streaming, now)));
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
        CaptureTarget? captureTarget = null;
        var captureFps = 0;
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
                        captureTarget = _options.Target;
                        captureFps = target.Fps;
                        gpu = GpuDevice.Create(_options.Monitor?.DeviceName);
                        capture = CaptureFactory.Create(gpu, captureTarget, _options.Monitor, captureFps);
                        _captureBackend = capture.Backend;
                        _sourceSize = (capture.Width, capture.Height);
                    }
                    if (capture!.SizeChanged)
                    {
                        Log.Info("Tamanho da origem mudou: recriando captura.");
                        TearDown();
                        continue;
                    }
                    // Qualidade trocada ao vivo: a câmera muda de formato, e a captura da tela foi aberta com um limite
                    // de FPS (ela entrega no máximo isso), então um FPS maior precisa de uma captura nova.
                    if (_options.Target != captureTarget || target.Fps > captureFps)
                    {
                        Log.Info("Qualidade nova pede outra captura: recriando.");
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
                        _encodedFrames.Add(1, now);
                        _encodedBytes.Add(packet.Data.Length, now);
                        SendFrame(MediaPacketType.Video, frameId++, packet, audience, _retransmit);
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

    // ───────────────────────── Câmera junto com a tela ─────────────────────────

    /// <summary>Quem recebe a câmera: está assistindo e tem uma versão que mostra o quadrinho (1.1.0 ou mais nova).</summary>
    private RemoteViewer[] CameraAudience()
        => _viewers.Values.Where(v => v.State == ViewerState.Streaming && v.Hello.Supports(StreamModes.FeatureCameraOverlay)).ToArray();

    /// <summary>
    /// A segunda faixa de vídeo: a câmera, no mesmo codec da tela e num tamanho de quadrinho (até 720p). Tem device,
    /// captura e encoder próprios, então a tela não espera a câmera. Se a câmera falhar (em uso, desligada, bloqueada
    /// pela privacidade do Windows), a tela continua e a câmera tenta de novo com espera crescente.
    /// </summary>
    private void CameraLoop()
    {
        var camera = _options.OverlayCamera!;
        var fps = Math.Clamp(camera.Fps, 5, 60);
        var interval = 1_000_000 / fps;
        GpuDevice? gpu = null;
        CameraCapture? capture = null;
        GpuColorConverter? converter = null;
        VideoEncoder? encoder = null;
        string? activeEncoder = null;
        uint frameId = 0;
        var packets = new List<EncodedPacket>(4);
        long lastEncodeUs = 0;
        long lastKeyframeUs = 0;
        var failures = 0;

        void TearDown()
        {
            encoder?.Dispose(); encoder = null;
            converter?.Dispose(); converter = null;
            capture?.Dispose(); capture = null;
            gpu?.Dispose(); gpu = null;
            activeEncoder = null;
            _cameraFormat = string.Empty;
        }

        try
        {
            while (!_cancellation.IsCancellationRequested)
            {
                var audience = CameraAudience();
                string encoderName;
                VideoCodec codec;
                lock (_configGate) (encoderName, codec) = (_encoderName, _codec);
                if (audience.Length == 0 || encoderName.Length == 0)
                {
                    if (gpu is not null) { TearDown(); Log.Info("Ninguém recebendo a câmera: câmera desligada."); }
                    _cameraWake.WaitOne(250);
                    continue;
                }

                try
                {
                    if (gpu is null)
                    {
                        gpu = GpuDevice.Create(_options.Monitor?.DeviceName);
                        capture = new CameraCapture(gpu, camera.DeviceId, camera.Width, camera.Height, fps);
                    }
                    if (capture!.SizeChanged)
                    {
                        Log.Info("A câmera mudou de formato: recriando.");
                        TearDown();
                        continue;
                    }

                    // Mesmo codec da tela: quando a tela troca de encoder (alguém entrou ou saiu), a câmera acompanha.
                    if (encoder is null || activeEncoder != encoderName)
                    {
                        encoder?.Dispose();
                        converter?.Dispose();
                        var (w, h) = Quality.FitWithin(capture.Width, capture.Height, camera.Width, camera.Height);
                        var kbps = Quality.CameraOverlayBitrateKbps(w, h, fps, codec);
                        converter = new GpuColorConverter(gpu, capture.Texture, capture.Width, capture.Height, w, h, fps);
                        encoder = OpenCameraEncoder(gpu, new EncoderSettings(encoderName, codec, w, h, fps, kbps));
                        activeEncoder = encoderName;
                        _cameraFormat = $"{w}×{h}";
                        if (Interlocked.Exchange(ref _cameraKbps, kbps) != kbps && _options.UploadMbps > 0)
                            SetTarget(ApplyUploadBudget(_adaptive?.Current ?? _profile), _reason);
                        _cameraKeyframeRequested = true;
                        if (_cameraProblem is not null) StatusChanged?.Invoke("A câmera voltou e está indo junto com a tela.");
                        _cameraProblem = null;
                        failures = 0;
                    }

                    var got = capture.WaitForFrame(TimeSpan.FromMilliseconds(Math.Max(1, interval / 1000)), out var timestamp);
                    var now = MediaClock.NowUs;
                    var since = now - lastEncodeUs;
                    // Sem frame novo (câmera travou ou foi tampada): reenvia de vez em quando para quem acabou de entrar.
                    if (!got && since < 500_000 && !_cameraKeyframeRequested) continue;
                    if (got && since < interval * 0.85 && !_cameraKeyframeRequested) continue;
                    if (!got) timestamp = now;
                    lastEncodeUs = now;

                    if (_cameraKeyframeRequested && now - lastKeyframeUs > 200_000)
                    {
                        encoder!.RequestKeyframe();
                        lastKeyframeUs = now;
                        _cameraKeyframeRequested = false;
                    }

                    converter!.Convert();
                    packets.Clear();
                    encoder!.Encode(converter.Output, timestamp, packets);
                    foreach (var packet in packets)
                    {
                        _cameraFrames.Add(1, now);
                        _cameraBytes.Add(packet.Data.Length, now);
                        SendFrame(MediaPacketType.CameraVideo, frameId++, packet, audience, _cameraRetransmit);
                    }
                }
                catch (Exception exception) when (!_cancellation.IsCancellationRequested)
                {
                    failures++;
                    TearDown();
                    var problem = exception.Message;
                    if (_cameraProblem != problem)
                    {
                        Log.Error("A câmera junto com a tela falhou", exception);
                        StatusChanged?.Invoke($"A câmera não foi junto: {problem} A tela continua normal; a câmera tenta de novo sozinha.");
                    }
                    _cameraProblem = problem;
                    // 2 s, 4 s, 8 s, 16 s e depois a cada 30 s; alguém entrando tenta na hora.
                    var wait = TimeSpan.FromSeconds(Math.Min(30, 2 << Math.Min(failures - 1, 4)));
                    WaitHandle.WaitAny([_cancellation.Token.WaitHandle, _cameraWake], wait);
                }
            }
        }
        finally
        {
            TearDown();
        }
    }

    /// <summary>Abre o encoder da câmera: o mesmo da tela e, se ele recusar uma segunda sessão, outro do mesmo codec.</summary>
    private VideoEncoder OpenCameraEncoder(GpuDevice gpu, EncoderSettings settings)
    {
        try
        {
            return new VideoEncoder(gpu, settings);
        }
        catch (Exception exception)
        {
            Log.Warn($"Encoder {settings.EncoderName} recusou a câmera: {exception.Message}");
            foreach (var fallback in _options.Encoders.Where(e => e.Codec == settings.Codec && e.FfmpegName != settings.EncoderName).OrderByDescending(e => e.Hardware))
            {
                try { return new VideoEncoder(gpu, settings with { EncoderName = fallback.FfmpegName }); }
                catch (Exception inner) { Log.Warn($"Encoder {fallback.FfmpegName} também recusou a câmera: {inner.Message}"); }
            }
            throw new InvalidOperationException($"Nenhum encoder {Quality.CodecLabel(settings.Codec)} aceitou a câmera junto com a tela.", exception);
        }
    }

    /// <summary>Uma linha sobre a câmera junto para a tela Transmissão (vazio sem câmera).</summary>
    private string CameraSummary(List<RemoteViewer> streaming, long now)
    {
        if (!HasCameraOverlay) return string.Empty;
        if (_cameraProblem is { } problem) return $"Câmera com problema: {problem}";
        if (streaming.Count == 0) return "Câmera junto: liga quando alguém entrar.";
        if (!streaming.Any(v => v.Hello.Supports(StreamModes.FeatureCameraOverlay)))
            return "Câmera junto: quem está assistindo usa uma versão antiga do ScreenFlow, que não mostra a câmera.";
        var frames = _cameraFrames.PerSecond(now);
        if (frames == 0) return "Câmera junto: ligando…";
        return $"Câmera junto: {_cameraFormat} · {frames:0} FPS · {_cameraBytes.PerSecond(now) * 8 / 1_000_000.0:0.0} Mbps";
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
                    if (!fallback.Hardware) StatusChanged?.Invoke($"Usando encoder por software ({fallback.FfmpegName}). 4K a 165 FPS não vai rodar assim.");
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

    /// <summary>
    /// Liga a captura de áudio já no início (pausada até alguém entrar): o medidor do host funciona antes de
    /// ter viewers e não há atraso para começar quando a primeira pessoa chega.
    /// </summary>
    private void StartAudio()
    {
        if (!AudioEnabled || _audio is not null) return;
        try
        {
            _audio = _options.AudioSourceFactory?.Invoke(_audioProfile) ?? new SystemAudioCapture(_audioProfile, _options.AudioSeparation);
            if (_audio is SystemAudioCapture system)
            {
                system.Notice += message => StatusChanged?.Invoke(message);
                if (system.LastNotice is { } notice) StatusChanged?.Invoke(notice);
            }
            _audio.Paused = true;
            _audio.PacketReady += OnAudioPacket;
            _audio.Start();
        }
        catch (Exception exception)
        {
            Log.Error("Áudio do sistema indisponível", exception);
            StatusChanged?.Invoke($"Áudio do sistema indisponível: {exception.Message}");
        }
    }

    private void UpdateAudioPause()
    {
        if (_audio is not null) _audio.Paused = !_viewers.Values.Any(v => v.State == ViewerState.Streaming);
    }

    private void OnAudioPacket(AudioPacket packet)
    {
        if (_sender is null) return;
        MediaChunk chunk;
        if (_audioProfile.Redundant)
        {
            // Festa de música: cada datagrama leva o quadro atual e o anterior.
            chunk = new MediaChunk
            {
                Header = new MediaHeader { Type = MediaPacketType.AudioPair, FrameId = _audioSequence++, Count = 1, TimestampUs = packet.TimestampUs },
                Payload = AudioPair.Pack(packet.Data, _previousAudio),
            };
            _previousAudio = packet.Data;
        }
        else
        {
            chunk = new MediaChunk
            {
                Header = new MediaHeader { Type = MediaPacketType.Audio, FrameId = _audioSequence++, Count = 1, TimestampUs = packet.TimestampUs },
                Payload = packet.Data,
            };
        }
        var now = MediaClock.NowUs;
        foreach (var viewer in _viewers.Values)
        {
            if (viewer.State != ViewerState.Streaming) continue;
            _sender.Send(viewer.Cipher, ref viewer.PacketCounter, chunk, viewer.MediaAddress);
            _audioBytes.Add(chunk.Payload.Length + MediaHeader.Size + PacketCipher.TagSize, now);
        }
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
        _cameraWake.Set();
        _listener?.Stop();
        if (_announcer is not null) await _announcer.DisposeAsync();
        _audio?.Dispose();
        foreach (var viewer in _viewers.Values) viewer.Dispose();
        _viewers.Clear();
        _pipeline?.Join(TimeSpan.FromSeconds(3));
        _cameraPipeline?.Join(TimeSpan.FromSeconds(3));
        _pacer?.Dispose();
        _udp?.Dispose();
        _timerResolution?.Dispose();
        if (_acceptLoop is not null) try { await _acceptLoop; } catch { }
        if (_udpDrain is not null) try { await _udpDrain; } catch { }
        Log.Info("Host encerrado e recursos liberados.");
        StatusChanged?.Invoke("Transmissão encerrada.");
    }

    private sealed class RemoteViewer : IDisposable
    {
        private readonly SemaphoreSlim _writeLock = new(1, 1);

        public RemoteViewer(Guid id, string name, TcpClient client, NetworkStream stream, IPEndPoint mediaEndpoint, byte[] key, byte[] salt, HelloMessage hello, string address)
        {
            Id = id;
            Name = name;
            Client = client;
            Stream = stream;
            MediaEndpoint = mediaEndpoint;
            MediaAddress = mediaEndpoint.Serialize();
            Address = address;
            Cipher = new PacketCipher(key, salt);
            Hello = hello;
            SpreadFec = hello.Supports(StreamModes.FeatureSpreadFec);
        }

        public Guid Id { get; }
        public string Name { get; }
        public TcpClient Client { get; }
        public NetworkStream Stream { get; }
        public IPEndPoint MediaEndpoint { get; }
        public SocketAddress MediaAddress { get; }
        public string Address { get; }
        public PacketCipher Cipher { get; }
        public HelloMessage Hello { get; }

        /// <summary>Entende a paridade espalhada (1.1.1). Versões antigas recebem a paridade de pacotes seguidos.</summary>
        public bool SpreadFec { get; }
        public ulong PacketCounter;
        public volatile ViewerState State = ViewerState.Authenticating;
        public StatsMessage? LastStats { get; set; }
        public CancellationTokenSource Kicked { get; } = new();

        public ViewerInfo Info => new(Id, Name, Address, State,
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
