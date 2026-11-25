using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using ScreenFlow.Core;
using ScreenFlow.Core.Diagnostics;
using ScreenFlow.Core.Media;
using ScreenFlow.Core.Protocol;
using ScreenFlow.Core.Room;
using ScreenFlow.Infrastructure.Network;

namespace ScreenFlow.Infrastructure.Room;

/// <summary>O que este PC mostra para a sala: nome e se está ao vivo (e o quê).</summary>
public sealed record RoomLocalState(
    string Name,
    bool Live = false,
    StreamMode LiveMode = StreamMode.Screen,
    bool LiveAudio = false,
    bool LiveCamera = false,
    int Viewers = 0,
    bool PinRequired = false,
    IPAddress? LiveAddress = null,
    bool Watching = false,
    int ControlPort = AppInfo.DefaultControlPort);

/// <summary>
/// Sala da rede: todo mundo com o ScreenFlow aberto na mesma rede do Radmin se vê, conversa no chat e pode entrar na
/// voz. Tudo passa pela porta de descoberta (UDP 47802, já liberada no firewall pelo instalador):
/// presença por broadcast a cada 2 s (e direto para quem já é conhecido), chat direto para cada PC com confirmação e
/// reenvio, e voz direto para quem está na voz. Os anúncios em JSON das transmissões (versões 1.0.x) também chegam
/// aqui e alimentam a lista da tela Assistir.
/// </summary>
public sealed class NetworkRoom : IAsyncDisposable
{
    private static readonly TimeSpan PresenceInterval = TimeSpan.FromSeconds(2);
    private readonly int _port;
    private readonly RoomPeerTable _peers = new();
    private readonly ChatHistory _history = new();
    private readonly ChatOutbox _outbox = new();
    private readonly RateLimiter<Guid> _chatLimit = new(burst: 8, perSecond: 1);
    private readonly RateLimiter<Guid> _replyLimit = new(burst: 2, perSecond: 0.5);
    private readonly RateLimiter<Guid> _historyLimit = new(burst: 1, perSecond: 0.1);
    private readonly ConcurrentDictionary<Guid, DateTimeOffset> _historyAsked = new();
    private readonly ConcurrentDictionary<string, DiscoveredHost> _hosts = new();
    private readonly ConcurrentDictionary<IPEndPoint, byte> _manual = new();
    private readonly CancellationTokenSource _cancellation = new();
    private readonly object _voiceGate = new();
    private readonly DateTimeOffset _started = DateTimeOffset.UtcNow;
    private Socket? _socket;
    private Thread? _receiver;
    private Task? _ticker;
    private volatile RoomLocalState _local;
    private VoiceEngine? _voice;
    private uint _presenceSequence;
    private volatile bool _inVoice;
    private volatile bool _muted;
    private volatile bool _deafened;
    private volatile bool _announceSoon;
    private int _wantsReplyTicks = 2;
    private int _historyRequests;
    private IReadOnlyList<IPAddress> _broadcasts = [];
    private DateTimeOffset _broadcastsAt;
    private float _volume = 1f;
    private double _sensitivity = 0.6;

    public NetworkRoom(Guid self, int port, RoomLocalState local)
    {
        if (self == Guid.Empty) throw new ArgumentException("Id da sala vazio.", nameof(self));
        SelfId = self;
        _port = port;
        _local = local;
    }

    public Guid SelfId { get; }
    public int Port => _port;

    /// <summary>Endereços de broadcast para a presença. Padrão: os adaptadores do Radmin (e a rede local, se pedido).</summary>
    public Func<IReadOnlyList<IPAddress>> BroadcastTargets { get; init; } = () => RadminBroadcasts(includeLan: false);

    /// <summary>Sem saída de som (autoteste): a voz é decodificada mas não toca.</summary>
    public bool OutputEnabled { get; init; } = true;

    /// <summary>Microfone escolhido nas configurações (null = padrão do Windows).</summary>
    public string? MicrophoneId { get; set; }

    /// <summary>Sons de entrar, sair e mudo na voz (como no Discord).</summary>
    public bool SoundsEnabled { get; set; } = true;

    /// <summary>Por que a sala não abriu (porta ocupada, por exemplo). Null quando está funcionando.</summary>
    public string? Error { get; private set; }

    public bool IsRunning => _socket is not null && !_cancellation.IsCancellationRequested;
    public bool InVoice => _inVoice;
    public bool Muted => _muted;
    public bool Deafened => _deafened;
    public VoiceEngine? Voice => _voice;
    public RoomLocalState Local => _local;

    public IReadOnlyList<RoomPeer> Peers => _peers.Snapshot();
    public IReadOnlyList<RoomChatMessage> Messages => _history.All();

    /// <summary>Transmissões anunciadas na rede (inclusive de versões antigas), vistas nos últimos 5 segundos.</summary>
    public IReadOnlyList<DiscoveredHost> Hosts
    {
        get
        {
            var cutoff = DateTimeOffset.UtcNow.AddSeconds(-5);
            foreach (var stale in _hosts.Where(h => h.Value.LastSeen < cutoff).Select(h => h.Key).ToArray()) _hosts.TryRemove(stale, out _);
            return _hosts.Values.OrderBy(h => h.Announcement.HostName).ToArray();
        }
    }

    /// <summary>Alguém entrou, saiu ou mudou (voz, mudo, ao vivo).</summary>
    public event Action? PeersChanged;
    public event Action<RoomPeer>? PeerJoined;
    public event Action<RoomPeer>? PeerLeft;
    public event Action<RoomPeer>? PeerJoinedVoice;
    public event Action<RoomPeer>? PeerLeftVoice;
    public event Action<RoomPeer>? PeerWentLive;

    /// <summary>Mensagem nova no chat (inclusive as suas e as antigas que chegaram para quem acabou de entrar).</summary>
    public event Action<RoomChatMessage>? MessageReceived;

    /// <summary>Você entrou ou saiu da voz, ou mudou mudo/ensurdecer.</summary>
    public event Action? VoiceChanged;

    public event Action? HostsChanged;

    // ───────────────────────── Ciclo de vida ─────────────────────────

    public void Start()
    {
        try { _socket = Bind(exclusive: true); }
        catch (SocketException first)
        {
            try
            {
                _socket = Bind(exclusive: false);
                Log.Warn($"Porta {_port} da sala já estava em uso ({first.SocketErrorCode}); abrindo compartilhada.");
            }
            catch (SocketException exception)
            {
                Error = $"A porta {_port} (UDP) está em uso por outro programa: a sala da rede ficou desligada.";
                Log.Warn($"{Error} ({exception.Message})");
                return;
            }
        }
        _receiver = new Thread(ReceiveLoop) { IsBackground = true, Name = "ScreenFlow sala da rede", Priority = ThreadPriority.AboveNormal };
        _receiver.Start();
        _ticker = Task.Run(TickLoopAsync);
        Log.Info($"Sala da rede aberta na porta UDP {_port}.");
    }

    private Socket Bind(bool exclusive)
    {
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp)
        {
            EnableBroadcast = true,
            ReceiveBufferSize = 1024 * 1024,
            SendBufferSize = 1024 * 1024,
        };
        try
        {
            // Um "porta inacessível" (ICMP) de quem fechou o app não pode derrubar o socket no Windows.
            socket.IOControl(-1744830452, [0, 0, 0, 0], null);
        }
        catch { }
        if (exclusive) socket.ExclusiveAddressUse = true;
        else
        {
            socket.ExclusiveAddressUse = false;
            socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        }
        try { socket.Bind(new IPEndPoint(IPAddress.Any, _port)); }
        catch
        {
            socket.Dispose();
            throw;
        }
        return socket;
    }

    public async ValueTask DisposeAsync()
    {
        if (_cancellation.IsCancellationRequested) return;
        try
        {
            var leave = RoomPacket.Leave(SelfId);
            foreach (var target in PresenceTargets()) Send(leave, target);
        }
        catch { }
        _cancellation.Cancel();
        lock (_voiceGate)
        {
            _voice?.Dispose();
            _voice = null;
            _inVoice = false;
        }
        if (_ticker is not null) try { await _ticker; } catch { }
        _socket?.Dispose();
        _receiver?.Join(1000);
        Log.Info("Sala da rede fechada.");
    }

    // ───────────────────────── Estado local ─────────────────────────

    /// <summary>Nome, transmissão e afins mudaram: avisa a sala na hora.</summary>
    public void UpdateLocal(RoomLocalState state)
    {
        if (state == _local) return;
        _local = state;
        _announceSoon = true;
    }

    public void AnnounceNow() => _announceSoon = true;

    /// <summary>Adiciona um PC pelo IP (quando o broadcast não chega até ele) e pede a presença dele.</summary>
    public void AddManualPeer(IPAddress address, int? port = null)
    {
        var endpoint = new IPEndPoint(address, port ?? _port);
        _manual[endpoint] = 0;
        Send(PresencePacket(wantsReply: true), endpoint);
    }

    public void RemoveManualPeer(IPAddress address) => _manual.TryRemove(new IPEndPoint(address, _port), out _);

    // ───────────────────────── Chat ─────────────────────────

    /// <summary>Manda uma mensagem para todo mundo da sala. Retorna a mensagem (já no histórico) ou null se vazia.</summary>
    public RoomChatMessage? SendChat(string text)
    {
        text = RoomPacket.CleanText(text);
        if (text.Length == 0) return null;
        var message = new RoomChatMessage(SelfId, _local.Name, BitConverter.ToUInt64(RandomNumberGenerator.GetBytes(8)), DateTimeOffset.UtcNow, text);
        _history.Add(message);
        var packet = RoomPacket.Chat(SelfId, message);
        var peers = _peers.Snapshot();
        foreach (var peer in peers) Send(packet, peer.EndPoint);
        _outbox.Add(message, packet, peers.Select(p => p.Id), DateTimeOffset.UtcNow);
        MessageReceived?.Invoke(message);
        return message;
    }

    // ───────────────────────── Voz ─────────────────────────

    public float Volume
    {
        get => _volume;
        set
        {
            _volume = value;
            if (_voice is { } voice) voice.Volume = value;
        }
    }

    public double Sensitivity
    {
        get => _sensitivity;
        set
        {
            _sensitivity = value;
            if (_voice is { } voice) voice.Sensitivity = value;
        }
    }

    /// <summary>Entra na voz da rede com o microfone (ou outra fonte, no autoteste). Lança se o microfone não abrir.</summary>
    public void JoinVoice(IVoiceInput? input = null)
    {
        VoiceEngine engine;
        lock (_voiceGate)
        {
            if (_voice is not null || _cancellation.IsCancellationRequested) return;
            input ??= new MicrophoneInput(MicrophoneId);
            try
            {
                engine = new VoiceEngine(input, _volume, _sensitivity, OutputEnabled) { Muted = _muted, Deafened = _deafened };
            }
            catch
            {
                input.Dispose();
                throw;
            }
            engine.FrameEncoded += SendVoice;
            engine.Start();
            _voice = engine;
            _inVoice = true;
        }
        if (SoundsEnabled) engine.Play(VoiceCue.Join);
        Log.Info($"Entrou na voz da rede ({engine.InputDescription}).");
        _announceSoon = true;
        VoiceChanged?.Invoke();
        PeersChanged?.Invoke();
    }

    public async Task LeaveVoiceAsync()
    {
        VoiceEngine? engine;
        lock (_voiceGate)
        {
            engine = _voice;
            _voice = null;
            _inVoice = false;
        }
        if (engine is null) return;
        engine.Muted = true;
        if (SoundsEnabled) engine.Play(VoiceCue.Leave);
        _announceSoon = true;
        VoiceChanged?.Invoke();
        PeersChanged?.Invoke();
        await Task.Delay(350); // deixa o som de saída tocar
        engine.Dispose();
        Log.Info("Saiu da voz da rede.");
    }

    public void SetMuted(bool muted) => SetVoiceState(muted, _deafened);

    /// <summary>Ensurdecer: não ouve ninguém e também fica mudo (como no Discord).</summary>
    public void SetDeafened(bool deafened) => SetVoiceState(_muted, deafened);

    /// <summary>Muda o mudo e o ensurdecer de uma vez, com um som só.</summary>
    public void SetVoiceState(bool muted, bool deafened)
    {
        if (_muted == muted && _deafened == deafened) return;
        _muted = muted;
        _deafened = deafened;
        if (_voice is { } voice)
        {
            voice.Deafened = deafened;
            voice.Muted = muted || deafened;
            PlayCue(muted || deafened ? VoiceCue.Mute : VoiceCue.Unmute);
        }
        _announceSoon = true;
        VoiceChanged?.Invoke();
        PeersChanged?.Invoke();
    }

    private void PlayCue(VoiceCue cue)
    {
        if (SoundsEnabled) _voice?.Play(cue);
    }

    private void SendVoice(uint sequence, byte level, byte[] current, byte[] previous)
    {
        if (!_inVoice || _deafened) return;
        Span<byte> packet = stackalloc byte[RoomPacket.HeaderSize + 7 + current.Length + previous.Length];
        var length = RoomPacket.WriteVoice(packet, SelfId, sequence, level, current, previous);
        foreach (var peer in _peers.Snapshot())
        {
            if (!peer.Presence.InVoice || peer.Presence.Deafened) continue;
            Send(packet[..length], peer.EndPoint);
        }
    }

    // ───────────────────────── Envio ─────────────────────────

    private RoomPresence LocalPresence(bool wantsReply)
    {
        var local = _local;
        var flags = PresenceFlags.None;
        if (_inVoice) flags |= PresenceFlags.InVoice;
        if (_muted || _deafened) flags |= PresenceFlags.Muted;
        if (_deafened) flags |= PresenceFlags.Deafened;
        if (local.Live) flags |= PresenceFlags.Live;
        if (local.Live && local.LiveAudio) flags |= PresenceFlags.LiveAudio;
        if (local.Live && local.LiveCamera) flags |= PresenceFlags.LiveCamera;
        if (local.Live && local.PinRequired) flags |= PresenceFlags.PinRequired;
        if (local.Watching) flags |= PresenceFlags.Watching;
        if (wantsReply) flags |= PresenceFlags.WantsReply;
        return new RoomPresence(local.Name, AppInfo.DisplayVersion, flags, local.LiveMode, local.Viewers, local.ControlPort,
            local.Live ? local.LiveAddress : null, Interlocked.Increment(ref _presenceSequence));
    }

    private byte[] PresencePacket(bool wantsReply) => RoomPacket.Presence(SelfId, LocalPresence(wantsReply));

    private IEnumerable<IPEndPoint> PresenceTargets()
    {
        var now = DateTimeOffset.UtcNow;
        if (now - _broadcastsAt > TimeSpan.FromSeconds(5))
        {
            try { _broadcasts = BroadcastTargets(); }
            catch (Exception exception) { Log.Debug($"Rede da sala: {exception.Message}"); _broadcasts = []; }
            _broadcastsAt = now;
        }
        var sent = new HashSet<IPEndPoint>();
        foreach (var address in _broadcasts)
            if (sent.Add(new IPEndPoint(address, _port))) yield return new IPEndPoint(address, _port);
        // Direto também para quem já é conhecido ou foi adicionado pelo IP: funciona mesmo se o broadcast não chegar.
        foreach (var peer in _peers.Snapshot())
            if (sent.Add(peer.EndPoint)) yield return peer.EndPoint;
        foreach (var manual in _manual.Keys)
            if (sent.Add(manual)) yield return manual;
    }

    private void SendPresence()
    {
        var wantsReply = _wantsReplyTicks > 0;
        if (wantsReply) _wantsReplyTicks--;
        var packet = PresencePacket(wantsReply);
        foreach (var target in PresenceTargets()) Send(packet, target);
    }

    private void Send(ReadOnlySpan<byte> packet, IPEndPoint target)
    {
        var socket = _socket;
        if (socket is null) return;
        try { socket.SendTo(packet, SocketFlags.None, target); }
        catch (SocketException) { }
        catch (ObjectDisposedException) { }
    }

    private async Task TickLoopAsync()
    {
        var token = _cancellation.Token;
        var lastPresence = DateTimeOffset.MinValue;
        var lastExpire = DateTimeOffset.UtcNow;
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(100));
        try
        {
            while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
            {
                var now = DateTimeOffset.UtcNow;
                try
                {
                    if (_announceSoon || now - lastPresence >= PresenceInterval)
                    {
                        _announceSoon = false;
                        lastPresence = now;
                        SendPresence();
                    }
                    foreach (var (peerId, packet) in _outbox.Due(now))
                        if (_peers.Find(peerId) is { } peer) Send(packet, peer.EndPoint);
                    if (now - lastExpire >= TimeSpan.FromSeconds(1))
                    {
                        lastExpire = now;
                        foreach (var gone in _peers.Expire(now)) OnPeerGone(gone, "sem sinal");
                        var before = _hosts.Count;
                        _ = Hosts;
                        if (_hosts.Count != before) HostsChanged?.Invoke();
                    }
                }
                catch (Exception exception) when (!token.IsCancellationRequested)
                {
                    Log.Warn($"Sala da rede: {exception.Message}");
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    // ───────────────────────── Recepção ─────────────────────────

    private void ReceiveLoop()
    {
        var buffer = new byte[2048];
        EndPoint any = new IPEndPoint(IPAddress.Any, 0);
        var socket = _socket!;
        socket.ReceiveTimeout = 500;
        while (!_cancellation.IsCancellationRequested)
        {
            int length;
            EndPoint from = any;
            try { length = socket.ReceiveFrom(buffer, ref from); }
            catch (SocketException) { continue; }
            catch (ObjectDisposedException) { break; }
            if (length <= 0 || from is not IPEndPoint source) continue;
            try { Handle(buffer.AsSpan(0, length), source); }
            catch (Exception exception) { Log.Debug($"Pacote da sala descartado ({source}): {exception.Message}"); }
        }
    }

    private void Handle(ReadOnlySpan<byte> data, IPEndPoint from)
    {
        if (data[0] == (byte)'{')
        {
            // Anúncio de transmissão (JSON), de qualquer versão.
            if (!DiscoveryAnnouncement.TryParse(data, out var announcement) || announcement is null) return;
            var isNew = !_hosts.ContainsKey(announcement.Address);
            _hosts[announcement.Address] = new DiscoveredHost(announcement, from.Address, DateTimeOffset.UtcNow);
            if (isNew) HostsChanged?.Invoke();
            return;
        }
        if (!RoomPacket.TryReadHeader(data, out var type, out var sender) || sender == SelfId) return;
        var body = RoomPacket.Body(data);
        var now = DateTimeOffset.UtcNow;
        switch (type)
        {
            case RoomPacketType.Presence:
                if (RoomPacket.TryReadPresence(body, out var presence)) OnPresence(sender, presence, from, now);
                break;
            case RoomPacketType.Leave:
                if (_peers.Remove(sender, out var leaving) && leaving is not null) OnPeerGone(leaving, "saiu");
                break;
            case RoomPacketType.Chat:
                if (RoomPacket.TryReadChat(body, out var message)) OnChat(sender, message, from, now);
                break;
            case RoomPacketType.ChatAck:
                if (RoomPacket.TryReadChatAck(body, out var author, out var id)) _outbox.Acknowledge(sender, author, id);
                break;
            case RoomPacketType.Voice:
                OnVoice(sender, body, from, now);
                break;
            case RoomPacketType.HistoryRequest:
                OnHistoryRequest(sender, from, now);
                break;
        }
    }

    private void OnPresence(Guid sender, RoomPresence presence, IPEndPoint from, DateTimeOffset now)
    {
        var update = _peers.Update(sender, presence, from, now);
        if (update.Peer is not { } peer) return;
        var previous = update.Previous;
        if (update.IsNew)
        {
            Log.Info($"Sala da rede: {presence.Name} ({from.Address}) apareceu.");
            Send(PresencePacket(wantsReply: false), from);
            // Acabou de abrir: pede as mensagens recentes para os primeiros que aparecerem.
            if (now - _started < TimeSpan.FromSeconds(45) && Interlocked.Increment(ref _historyRequests) <= 3)
            {
                _historyAsked[sender] = now;
                Send(RoomPacket.HistoryRequest(SelfId), from);
            }
            PeerJoined?.Invoke(peer);
        }
        else if (presence.Flags.HasFlag(PresenceFlags.WantsReply) && _replyLimit.TryTake(sender, now))
        {
            Send(PresencePacket(wantsReply: false), from);
        }

        if (presence.InVoice != (previous?.InVoice ?? false))
        {
            if (presence.InVoice) PeerJoinedVoice?.Invoke(peer);
            else
            {
                _voice?.RemovePeer(sender);
                if (previous is not null) PeerLeftVoice?.Invoke(peer);
            }
            if (_inVoice && previous is not null) PlayCue(presence.InVoice ? VoiceCue.Join : VoiceCue.Leave);
            else if (_inVoice && update.IsNew && presence.InVoice) PlayCue(VoiceCue.Join);
        }
        if (presence.Live && previous is { Live: false }) PeerWentLive?.Invoke(peer);

        if (update.IsNew || previous is null || (previous with { Sequence = 0 }) != (presence with { Sequence = 0 }))
            PeersChanged?.Invoke();
    }

    private void OnPeerGone(RoomPeer peer, string reason)
    {
        Log.Info($"Sala da rede: {peer.Presence.Name} {reason}.");
        _outbox.Forget(peer.Id);
        if (peer.Presence.InVoice)
        {
            _voice?.RemovePeer(peer.Id);
            if (_inVoice) PlayCue(VoiceCue.Leave);
            PeerLeftVoice?.Invoke(peer);
        }
        PeerLeft?.Invoke(peer);
        PeersChanged?.Invoke();
    }

    private void OnChat(Guid sender, RoomChatMessage message, IPEndPoint from, DateTimeOffset now)
    {
        if (message.History)
        {
            // Só aceita histórico de quem foi perguntado agora há pouco.
            if (!_historyAsked.TryGetValue(sender, out var asked) || now - asked > TimeSpan.FromSeconds(15)) return;
        }
        else
        {
            Send(RoomPacket.ChatAck(SelfId, message.AuthorId, message.MessageId), from);
            if (_history.Contains(message.AuthorId, message.MessageId)) return;
            if (!_chatLimit.TryTake(sender, now)) return;
            // A mensagem ao vivo é de quem mandou: o nome e o autor vêm da presença, não do pacote.
            var peer = _peers.Touch(sender, from, now);
            message = message with
            {
                AuthorId = sender,
                AuthorName = peer?.Presence.Name ?? message.AuthorName,
                Time = Clamp(message.Time, now),
            };
        }
        if (_history.Add(message) < 0) return;
        MessageReceived?.Invoke(message);
    }

    /// <summary>Relógio do outro PC adiantado ou atrasado demais: usa a hora de chegada.</summary>
    private static DateTimeOffset Clamp(DateTimeOffset time, DateTimeOffset now) => (time - now).Duration() > TimeSpan.FromMinutes(2) ? now : time;

    private void OnHistoryRequest(Guid sender, IPEndPoint from, DateTimeOffset now)
    {
        if (_peers.Find(sender) is null || !_historyLimit.TryTake(sender, now)) return;
        foreach (var message in _history.Recent(40))
            Send(RoomPacket.Chat(SelfId, message with { History = true }), from);
    }

    private void OnVoice(Guid sender, ReadOnlySpan<byte> body, IPEndPoint from, DateTimeOffset now)
    {
        if (!RoomPacket.TryReadVoice(body, out var sequence, out var level, out var current, out var previous)) return;
        var peer = _peers.Touch(sender, from, now);
        if (peer is null || !peer.Presence.InVoice) return;
        peer.RecordVoice(level, now);
        if (_inVoice && !_deafened) _voice?.Receive(sender, sequence, current, previous);
    }

    // ───────────────────────── Rede ─────────────────────────

    /// <summary>Broadcast de cada adaptador ligado do Radmin (e, se pedido, das redes locais).</summary>
    public static IReadOnlyList<IPAddress> RadminBroadcasts(bool includeLan)
        => RadminNetwork.GetIPv4Adapters()
            .Where(a => a.IsUp && (a.IsRadmin || includeLan) && !IPAddress.IsLoopback(a.Address) && !a.Address.ToString().StartsWith("169.254.", StringComparison.Ordinal))
            .Select(a => a.Broadcast)
            .Distinct()
            .ToArray();
}
