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

namespace ScreenFlow.Infrastructure.Session;

public enum ConnectionState { Idle, Connecting, WaitingApproval, Connected, Reconnecting, Error, Closed }

public sealed record ViewerOptions(string Host, int ControlPort, string DeviceName, string? Pin, float Volume, VideoCodec[] Decoders, int MaxWidth, int MaxHeight, int MaxFps);

public sealed class ConnectionRejectedException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

/// <summary>
/// Conecta num host pelo IP, faz o handshake, recebe vídeo e áudio por UDP, decodifica e reconecta sozinho
/// com backoff se a conexão cair.
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

    public ViewerSession(ViewerOptions options)
    {
        _options = options;
        _pin = options.Pin;
        _volume = options.Volume;
    }

    public ConnectionState State { get; private set; } = ConnectionState.Idle;
    public string HostName { get; private set; } = "";
    public ViewerStatistics? LastStatistics => _lastStats;

    public event Action<ConnectionState, string>? StateChanged;
    public event Action<DecodedFrame>? FrameReady;
    public event Action<ViewerStatistics>? StatisticsUpdated;

    /// <summary>Chamado quando o host exige PIN e nenhum foi informado (ou o informado estava errado).</summary>
    public Func<bool, Task<string?>>? PinProvider { get; set; }

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
        SocketException { SocketErrorCode: SocketError.ConnectionRefused } => $"O ScreenFlow não está transmitindo em {_options.Host}. Tentando de novo…",
        SocketException { SocketErrorCode: SocketError.TimedOut or SocketError.HostUnreachable or SocketError.NetworkUnreachable } or TimeoutException
            => $"Não deu para alcançar {_options.Host}. Confira se os dois estão na mesma rede do Radmin VPN. Tentando de novo…",
        _ => $"Conexão perdida ({exception.Message}). Tentando de novo…",
    };

    private async Task ConnectOnceAsync(CancellationToken token, bool askPinAgain)
    {
        if (!RadminAddress.TryParseHost(_options.Host, out var hostAddress))
            throw new ConnectionRejectedException("address", "Digite um IP válido, por exemplo 26.12.34.56.");

        using var client = new TcpClient(AddressFamily.InterNetwork) { NoDelay = true };
        using (var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(token))
        {
            connectTimeout.CancelAfter(TimeSpan.FromSeconds(5));
            try { await client.ConnectAsync(hostAddress, _options.ControlPort, connectTimeout.Token); }
            catch (OperationCanceledException) when (!token.IsCancellationRequested) { throw new TimeoutException(); }
        }
        var stream = client.GetStream();
        var local = (client.Client.LocalEndPoint as IPEndPoint)?.Address ?? IPAddress.Any;
        using var udp = MediaSender.CreateUdp(local, 0);
        var udpPort = ((IPEndPoint)udp.LocalEndPoint!).Port;

        using var exchange = new KeyExchange();
        await ControlFraming.WriteAsync(stream, new HelloMessage(AppInfo.ProtocolVersion, AppInfo.DisplayVersion, _options.DeviceName,
            _options.Decoders, _options.MaxWidth, _options.MaxHeight, _options.MaxFps, udpPort, exchange.PublicKey), token);

        if (await ControlFraming.ReadAsync(stream, token) is var first && first is RejectedMessage early)
            throw new ConnectionRejectedException(early.Code, early.Message);
        if (first is not ChallengeMessage challenge) throw new InvalidDataException("Resposta inesperada do host.");
        HostName = challenge.HostName;

        if (challenge.PinRequired && (string.IsNullOrEmpty(_pin) || askPinAgain))
        {
            _pin = PinProvider is null ? null : await PinProvider(askPinAgain);
            if (string.IsNullOrEmpty(_pin)) throw new ConnectionRejectedException(RejectedMessage.WrongPin + "-missing", "Este host pede PIN.");
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
        using var connection = new ActiveConnection(this, stream, udp, new IPEndPoint(hostAddress, welcome.MediaPort), cipher, welcome.Stream);
        _backoff.Reset();
        SetState(ConnectionState.Connected, $"Conectado a {welcome.HostName}.");
        await connection.RunAsync(token);
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
        private readonly SemaphoreSlim _writeLock = new(1, 1);
        private readonly ClockSync _clock = new();
        private readonly RateMeter _bytes = new();
        private readonly RateMeter _decoded = new();
        private readonly CancellationTokenSource _stop = new();
        private StreamConfigMessage _config;
        private VideoDecoder? _decoder;
        private string _decoderLabel = "—";
        private long _lastVideoTimestampViewerUs;
        private long _lastKeyframeRequestUs;
        private ulong _highestPacket;
        private ulong _windowStartPacket;
        private long _windowReceived;
        private double _lossRate;
        private double _latencyMs;
        private volatile bool _codecChanged;

        // Teste de banda
        private long _probeBytes;
        private long _probeCount;

        public ActiveConnection(ViewerSession owner, NetworkStream stream, Socket udp, IPEndPoint hostMedia, PacketCipher cipher, StreamConfigMessage config)
        {
            _owner = owner;
            _stream = stream;
            _udp = udp;
            _hostMedia = hostMedia;
            _cipher = cipher;
            _config = config;
        }

        public async Task RunAsync(CancellationToken token)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, _stop.Token);
            var t = linked.Token;
            var receive = new Thread(() => ReceiveLoop(t)) { IsBackground = true, Name = "ScreenFlow UDP", Priority = ThreadPriority.Highest };
            var decode = new Thread(() => DecodeLoop(t)) { IsBackground = true, Name = "ScreenFlow decoder", Priority = ThreadPriority.AboveNormal };
            receive.Start();
            decode.Start();
            if (_config.AudioEnabled) StartAudio();

            var tick = Task.Run(() => TickLoopAsync(t));
            var timers = Task.Run(() => TimerLoopAsync(t));
            try
            {
                await SendAsync(new KeyframeRequestMessage("entrada"), t);
                await ControlLoopAsync(t);
            }
            finally
            {
                _stop.Cancel();
                _decodeQueue.CompleteAdding();
                receive.Join(1000);
                decode.Join(1000);
                try { await Task.WhenAll(tick, timers); } catch { }
            }
        }

        private void StartAudio()
        {
            try
            {
                _owner._audio ??= new AudioPlayer(_owner._volume);
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
                        if (config.AudioEnabled && !_config.AudioEnabled) StartAudio();
                        _config = config;
                        Log.Info($"Novo perfil do host: {config.Width}x{config.Height} {config.Fps} FPS {config.BitrateKbps} kbps ({config.Reason})");
                        break;
                    case BandwidthTestRequestMessage test:
                        _ = RunBandwidthTestAsync(test, token);
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
            EndPoint from = new IPEndPoint(IPAddress.Any, 0);
            _udp.ReceiveTimeout = 250;
            while (!token.IsCancellationRequested)
            {
                int length;
                try { length = _udp.ReceiveFrom(buffer, ref from); }
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
                    case MediaPacketType.Audio:
                        var audio = _owner._audio;
                        if (audio is not null)
                        {
                            var audioViewerUs = _clock.HostToViewerUs(header.TimestampUs);
                            var lagMs = _lastVideoTimestampViewerUs == 0 ? 0 : (_lastVideoTimestampViewerUs - audioViewerUs) / 1000.0;
                            audio.Play(payload.ToArray(), lagMs);
                        }
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
            if (number > _highestPacket) _highestPacket = number;
            if (_windowStartPacket == 0) _windowStartPacket = number;
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
                    RequestKeyframe("decoder atrasado");
                }
            }
        }

        private void RequestKeyframe(string reason)
        {
            var now = MediaClock.NowUs;
            if (now - Interlocked.Read(ref _lastKeyframeRequestUs) < 300_000) return;
            Interlocked.Exchange(ref _lastKeyframeRequestUs, now);
            _ = SendAsync(new KeyframeRequestMessage(reason), CancellationToken.None);
        }

        private void DecodeLoop(CancellationToken token)
        {
            try
            {
                foreach (var frame in _decodeQueue.GetConsumingEnumerable(token))
                {
                    if (_decoder is null || _codecChanged)
                    {
                        _decoder?.Dispose();
                        _decoder = new VideoDecoder(_config.Codec);
                        _decoderLabel = _decoder.Description;
                        _codecChanged = false;
                    }
                    var image = _decoder.Decode(frame.Data, frame.TimestampUs);
                    if (image is null) continue;
                    var now = MediaClock.NowUs;
                    _decoded.Add(1, now);
                    var viewerTs = _clock.HostToViewerUs(frame.TimestampUs);
                    _lastVideoTimestampViewerUs = viewerTs;
                    if (_clock.HasEstimate) _latencyMs = _latencyMs * 0.9 + (now - viewerTs) / 1000.0 * 0.1;
                    _owner.FrameReady?.Invoke(image);
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
            }
        }

        private async Task TickLoopAsync(CancellationToken token)
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(4));
            while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
            {
                List<NackRequest> nacks;
                List<AssembledFrame> ready;
                int dropped;
                lock (_reassemblyGate) (nacks, ready, dropped) = _reassembler.Tick(MediaClock.Now);
                Enqueue(ready);
                if (dropped > 0) RequestKeyframe("frames perdidos");
                foreach (var nack in nacks) await SendAsync(new NackMessage(nack.FrameId, nack.Missing), token);
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
                var kbps = _bytes.PerSecond(now) * 8 / 1000.0;
                var fps = _decoded.PerSecond(now);
                await SendAsync(new StatsMessage(_lossRate, _clock.RoundTripMs, kbps, fps, _latencyMs), token);

                var stats = new ViewerStatistics(fps, kbps / 1000, _latencyMs, _clock.RoundTripMs, _lossRate, Quality.CodecLabel(_config.Codec),
                    _config.Width, _config.Height, _decoderLabel, _reassembler.FramesDropped, _reassembler.PacketsRecoveredByFec, _config.Reason);
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

        public void Dispose()
        {
            _stop.Cancel();
            _decodeQueue.Dispose();
            _stop.Dispose();
        }
    }
}
