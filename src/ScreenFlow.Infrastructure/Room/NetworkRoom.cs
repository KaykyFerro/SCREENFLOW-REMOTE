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

/// <summary>A sala privada em uso: quem é o dono e se ela é só para quem ele aprovou.</summary>
public sealed record PrivateRoomPolicy(Guid Owner, bool ApprovedOnly);

/// <summary>
/// O que este PC mostra para a sala: nome, foto e se está ao vivo (e o quê). <see cref="LiveViaServer"/> (1.4.1): a
/// transmissão já está no servidor ScreenFlow, e quem está nele assiste por lá (com a senha do servidor, sem o PIN);
/// <see cref="LiveAddress"/> e <see cref="ControlPort"/> continuam os deste PC, para quem entra direto.
/// </summary>
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
    int ControlPort = AppInfo.DefaultControlPort,
    RoomBlobRef? Avatar = null,
    bool LiveViaServer = false);

/// <summary>
/// Sala da rede: todo mundo com o ScreenFlow aberto na mesma rede do Radmin se vê, conversa no chat e pode entrar na
/// voz. Tudo passa pela porta de descoberta (UDP 47802, já liberada no firewall pelo instalador):
/// presença por broadcast a cada 2 s (e direto para quem já é conhecido), chat direto para cada PC com confirmação e
/// reenvio, e voz direto para quem está no mesmo canal de voz. Os anúncios em JSON das transmissões (versões 1.0.x)
/// também chegam aqui e alimentam a lista da tela Assistir.
/// </summary>
/// <remarks>
/// Canais (1.3.0), como num servidor do Discord: cada mensagem é de um canal de texto e cada pessoa na voz está num
/// canal de voz; só quem está no mesmo canal de voz se ouve. As versões antigas ficam no "geral" e na voz "Geral":
/// mensagens dos outros canais nem vão para elas.
/// </remarks>
public sealed partial class NetworkRoom : IAsyncDisposable
{
    private static readonly TimeSpan PresenceInterval = TimeSpan.FromSeconds(2);
    private readonly int _port;
    private readonly RoomPeerTable _peers = new();
    private readonly ChatHistory _history = new();
    private readonly ChatOutbox _outbox = new();
    private readonly RateLimiter<Guid> _chatLimit = new(burst: 8, perSecond: 1);
    private readonly RateLimiter<Guid> _replyLimit = new(burst: 2, perSecond: 0.5);
    private readonly RateLimiter<Guid> _historyLimit = new(burst: 1, perSecond: 0.1);
    private readonly RateLimiter<Guid> _blobLimit = new(burst: 12, perSecond: 4);
    private readonly RoomBlobDownloads _downloads = new();
    private readonly ConcurrentDictionary<(Guid Peer, string Id), byte> _uploading = new();
    private readonly ConcurrentDictionary<string, DateTimeOffset> _blobTried = new();
    private readonly SemaphoreSlim _uploadSlots = new(3);
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
    private volatile string _voiceChannel = RoomChannels.DefaultVoiceName;
    private volatile ValueList<RoomChannel> _createdChannels = ValueList<RoomChannel>.Empty;
    private readonly ConcurrentDictionary<Guid, float> _peerVolumes = new();
    // Sala privada e moderação (1.4.0).
    private readonly RoomKeyExchange _exchange = new();
    private readonly ConcurrentDictionary<Guid, byte> _blocked = new();
    private readonly ConcurrentDictionary<Guid, string> _pending = new();
    private readonly ConcurrentDictionary<Guid, (byte[] Packet, DateTimeOffset Until)> _rekeys = new();
    private volatile PrivateRoomPolicy? _private;
    private volatile IReadOnlyList<Guid>? _roster;
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

    // ───────────────────────── Sala privada e moderação (1.4.0) ─────────────────────────

    /// <summary>As chaves da sala privada. Sem sala privada, tudo vai aberto como nas versões antigas.</summary>
    public RoomKeyring Keys { get; } = new();

    /// <summary>A sala privada em uso (dono e se é só para quem o dono aprovou), ou null.</summary>
    public PrivateRoomPolicy? Private => _private;

    /// <summary>
    /// Entra numa sala privada (ou sai, com null). A partir daqui tudo da sala (presença, chat, voz e arquivos) vai
    /// lacrado com a chave dela, e o que chega aberto ou de outra sala é ignorado. Quem está fora não vê nem ouve nada.
    /// </summary>
    public void UsePrivateRoom(RoomInvite? invite, bool approvedOnly = false, IReadOnlyList<Guid>? roster = null)
    {
        Keys.Use(invite?.Secret, invite?.Epoch ?? 0, DateTimeOffset.UtcNow);
        _private = invite is null ? null : new PrivateRoomPolicy(invite.Owner, approvedOnly);
        _roster = invite is not null && invite.Owner == SelfId ? roster ?? [] : null;
        _pending.Clear();
        _rekeys.Clear();
        _announceSoon = true;
        PeersChanged?.Invoke();
        PendingChanged?.Invoke();
    }

    /// <summary>O dono muda quem foi aprovado e se a sala é só para eles (vai para todos na próxima presença).</summary>
    public void SetRoster(bool approvedOnly, IReadOnlyList<Guid> roster)
    {
        if (_private is not { } policy || policy.Owner != SelfId) return;
        _private = policy with { ApprovedOnly = approvedOnly };
        _roster = roster;
        foreach (var id in roster) _pending.TryRemove(id, out _);
        _announceSoon = true;
        PendingChanged?.Invoke();
        PeersChanged?.Invoke();
    }

    /// <summary>Quem tem o convite mas ainda não foi aprovado pelo dono (só o dono vê).</summary>
    public IReadOnlyDictionary<Guid, string> Pending => new Dictionary<Guid, string>(_pending);

    /// <summary>Alguém pediu para entrar (ou foi aprovado). Chamado fora da thread da interface.</summary>
    public event Action? PendingChanged;

    /// <summary>A chave da sala mudou (o dono trocou). Chamado fora da thread da interface, com o convite novo.</summary>
    public event Action<RoomInvite>? KeyChanged;

    /// <summary>Bloqueia (ou desbloqueia) alguém só para você: nada dele chega aqui, nem chat, nem voz, nem arquivos.</summary>
    public void SetBlocked(Guid id, bool blocked)
    {
        if (id == SelfId) return;
        if (blocked)
        {
            _blocked[id] = 0;
            if (_peers.Remove(id, out var gone) && gone is not null) OnPeerGone(gone, "bloqueado");
        }
        else _blocked.TryRemove(id, out _);
        PeersChanged?.Invoke();
    }

    public bool IsBlocked(Guid id) => _blocked.ContainsKey(id);

    /// <summary>
    /// O dono troca a chave da sala: um segredo novo vai para cada pessoa que está na sala agora, cifrado só para ela.
    /// Quem está em <paramref name="exclude"/> (removido) não recebe e fica de fora. Quem estiver offline precisa de um
    /// convite novo. Devolve o convite novo, ou null se este PC não é o dono.
    /// </summary>
    public RoomInvite? RotateKey(IReadOnlyCollection<Guid> exclude)
    {
        if (_private is not { } policy || policy.Owner != SelfId) return null;
        var now = DateTimeOffset.UtcNow;
        var next = new RoomInvite(System.Security.Cryptography.RandomNumberGenerator.GetBytes(RoomKeys.SecretBytes), SelfId, Keys.Epoch + 1);
        var packets = new List<(Guid Id, byte[] Packet)>();
        foreach (var peer in _peers.Snapshot())
        {
            if (exclude.Contains(peer.Id) || peer.Presence.ExchangeKey is not { } key) continue;
            if (policy.ApprovedOnly && _roster is { } roster && !roster.Contains(peer.Id)) continue;
            try
            {
                var wrapped = _exchange.Wrap(key, next.Secret, next.Epoch, peer.Id);
                packets.Add((peer.Id, RoomPacket.Rekey(SelfId, peer.Id, next.Epoch, _exchange.PublicKey, wrapped)));
            }
            catch (Exception exception) when (exception is FormatException or System.Security.Cryptography.CryptographicException)
            {
                Log.Debug($"Sala privada: chave de troca inválida de {peer.Presence.Name}: {exception.Message}");
            }
        }
        // Manda com a chave antiga (todos ainda estão nela) e repete até cada um aparecer na chave nova.
        foreach (var (id, packet) in packets)
        {
            if (_peers.Find(id) is { } peer) SendTo(packet, peer);
            _rekeys[id] = (Keys.Seal(packet, SelfId) ?? packet, now + TimeSpan.FromSeconds(20));
        }
        foreach (var id in exclude) SetBlocked(id, true);
        Keys.Use(next.Secret, next.Epoch, now, keepPrevious: true);
        _announceSoon = true;
        Log.Info($"Sala privada: chave trocada (geração {next.Epoch}) para {packets.Count} pessoas.");
        return next;
    }

    /// <summary>Endereços de broadcast para a presença. Padrão: os adaptadores do Radmin (e a rede local, se pedido).</summary>
    public Func<IReadOnlyList<IPAddress>> BroadcastTargets { get; init; } = () => RadminBroadcasts(includeLan: false);

    /// <summary>Sem saída de som (autoteste): a voz é decodificada mas não toca.</summary>
    public bool OutputEnabled { get; init; } = true;

    /// <summary>Microfone escolhido nas configurações (null = padrão do Windows).</summary>
    public string? MicrophoneId { get; set; }

    /// <summary>Saída de som da voz (null = padrão do Windows). Vale na próxima vez que entrar na voz.</summary>
    public string? SpeakerId { get; set; }

    /// <summary>Apertar para falar (1.4.0): ligado, a voz só vai enquanto <see cref="SetTalkKey"/> diz que a tecla está apertada.</summary>
    public bool PushToTalk
    {
        get => _pushToTalk;
        set
        {
            _pushToTalk = value;
            if (_voice is { } voice) voice.PushToTalk.Enabled = value;
        }
    }

    private volatile bool _pushToTalk;

    /// <summary>A tecla de apertar para falar está (ou não) apertada agora.</summary>
    public void SetTalkKey(bool held) => _voice?.PushToTalk.SetHeld(held, Environment.TickCount64);

    /// <summary>Sons de entrar, sair e mudo na voz (como no Discord).</summary>
    public bool SoundsEnabled { get; set; } = true;

    /// <summary>Arquivos que este PC tem para mandar (a própria foto e as imagens do chat). Null: não tem o arquivo.</summary>
    public Func<string, byte[]?>? BlobSource { get; init; }

    /// <summary>Este PC já tem o arquivo (não precisa baixar).</summary>
    public Func<string, bool>? HasBlob { get; init; }

    /// <summary>Um arquivo chegou inteiro e conferido (chamado fora da thread da interface).</summary>
    public event Action<string, byte[]>? BlobReceived;

    /// <summary>Ninguém mandou o arquivo.</summary>
    public event Action<string>? BlobFailed;

    /// <summary>Por que a sala não abriu (porta ocupada, por exemplo). Null quando está funcionando.</summary>
    public string? Error { get; private set; }

    public bool IsRunning => _socket is not null && !_cancellation.IsCancellationRequested;
    public bool InVoice => _inVoice;

    /// <summary>O canal de voz em que você está (null fora da voz).</summary>
    public string? VoiceChannel => _inVoice ? _voiceChannel : null;

    /// <summary>Os canais que você criou (aparecem para todo mundo da sala enquanto você estiver nela).</summary>
    public IReadOnlyList<RoomChannel> CreatedChannels
    {
        get => _createdChannels;
        set
        {
            var list = new ValueList<RoomChannel>(RoomChannels.Normalize(value));
            if (list.Equals(_createdChannels)) return;
            _createdChannels = list;
            _announceSoon = true;
        }
    }
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
            if (_server is { } server) SendRelay(leave, null, server);
            else foreach (var target in PresenceTargets()) Send(leave, target);
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
        if (_server is null) Send(PresencePacket(wantsReply: true), endpoint);
    }

    public void RemoveManualPeer(IPAddress address) => _manual.TryRemove(new IPEndPoint(address, _port), out _);

    /// <summary>
    /// (1.4.1) Os endereços de quem está na sala direta (vazio pelo servidor): a transmissão também é anunciada direto
    /// para eles, porque o broadcast não chega em todo lugar (no Tailscale, por exemplo).
    /// </summary>
    public IReadOnlyList<IPAddress> PeerAddresses => _server is not null ? [] : _peers.Snapshot().Select(p => p.EndPoint.Address).Distinct().ToArray();

    // ───────────────────────── Chat ─────────────────────────

    /// <summary>
    /// Manda uma mensagem para todo mundo da sala num canal de texto (null é o "geral"), com uma imagem se quiser (o
    /// arquivo dela precisa estar em <see cref="BlobSource"/>: quem recebe pede os pedaços). As mensagens dos outros
    /// canais só vão para quem entende canais (1.3.0 ou mais nova). Retorna a mensagem (já no histórico) ou null se vazia.
    /// </summary>
    public RoomChatMessage? SendChat(string text, ChatImage? image = null, string? channel = null)
    {
        text = RoomPacket.CleanText(text);
        if (text.Length == 0 && image is null) return null;
        if (text.Length == 0) text = RoomPacket.ImagePlaceholder;
        string? channelName = null;
        if (!RoomChannels.IsDefaultText(channel))
        {
            channelName = RoomChannels.CleanName(RoomChannelKind.Text, channel);
            if (channelName.Length == 0) return null;
        }
        var message = new RoomChatMessage(SelfId, _local.Name, BitConverter.ToUInt64(RandomNumberGenerator.GetBytes(8)), DateTimeOffset.UtcNow, text,
            Image: image, Channel: channelName);
        _history.Add(message);
        var packet = RoomPacket.Chat(SelfId, message);
        var peers = _peers.Snapshot().Where(p => message.InDefaultChannel || p.Presence.SupportsChannels).ToList();
        SendToMany(packet, peers);
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

    /// <summary>
    /// Entra na voz da rede, no canal <paramref name="channel"/> (null é a "Geral"), com o microfone (ou outra fonte, no
    /// autoteste). Já na voz, só troca de canal. Lança se o microfone não abrir.
    /// </summary>
    public void JoinVoice(IVoiceInput? input = null, string? channel = null)
    {
        var name = CleanVoiceChannel(channel);
        VoiceEngine engine;
        bool alreadyIn;
        lock (_voiceGate)
        {
            if (_cancellation.IsCancellationRequested) return;
            alreadyIn = _voice is not null;
        }
        if (alreadyIn)
        {
            input?.Dispose();
            MoveVoice(name);
            return;
        }
        lock (_voiceGate)
        {
            if (_voice is not null || _cancellation.IsCancellationRequested)
            {
                input?.Dispose();
                return;
            }
            input ??= new MicrophoneInput(MicrophoneId);
            try
            {
                engine = new VoiceEngine(input, _volume, _sensitivity, OutputEnabled, SpeakerId) { Muted = _muted, Deafened = _deafened };
                engine.PushToTalk.Enabled = _pushToTalk;
            }
            catch
            {
                input.Dispose();
                throw;
            }
            foreach (var (peer, volume) in _peerVolumes) engine.SetPeerVolume(peer, volume);
            engine.FrameEncoded += SendVoice;
            _voiceChannel = name;
            engine.Start();
            _voice = engine;
            _inVoice = true;
        }
        if (SoundsEnabled) engine.Play(VoiceCue.Join);
        Log.Info($"Entrou na voz da rede, canal {name} ({engine.InputDescription}).");
        _announceSoon = true;
        VoiceChanged?.Invoke();
        PeersChanged?.Invoke();
    }

    /// <summary>Troca de canal de voz sem desligar o microfone (como clicar em outro canal no Discord).</summary>
    public void MoveVoice(string? channel)
    {
        var name = CleanVoiceChannel(channel);
        if (RoomChannels.SameName(name, _voiceChannel)) return;
        _voiceChannel = name;
        if (_voice is { } voice)
        {
            // Quem ficou no canal antigo para de tocar aqui; quem está no novo começa no próximo quadro.
            foreach (var peer in _peers.Snapshot()) voice.RemovePeer(peer.Id);
            PlayCue(VoiceCue.Join);
            Log.Info($"Voz da rede: mudou para o canal {name}.");
        }
        _announceSoon = true;
        VoiceChanged?.Invoke();
        PeersChanged?.Invoke();
    }

    private static string CleanVoiceChannel(string? channel)
        => RoomChannels.CleanName(RoomChannelKind.Voice, channel) is { Length: > 0 } clean && !RoomChannels.IsDefaultVoice(clean)
            ? clean
            : RoomChannels.DefaultVoiceName;

    /// <summary>Volume de uma pessoa só, para você (0 = silenciada; 1 = normal; até 2). Vale também depois de sair e voltar.</summary>
    public void SetPeerVolume(Guid peer, float volume)
    {
        volume = Math.Clamp(volume, 0f, 2f);
        if (Math.Abs(volume - 1f) < 0.001f) _peerVolumes.TryRemove(peer, out _);
        else _peerVolumes[peer] = volume;
        _voice?.SetPeerVolume(peer, volume);
    }

    public float PeerVolume(Guid peer) => _peerVolumes.GetValueOrDefault(peer, 1f);

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
        var channel = _voiceChannel;
        Span<byte> packet = stackalloc byte[RoomPacket.HeaderSize + 7 + current.Length + previous.Length];
        var length = RoomPacket.WriteVoice(packet, SelfId, sequence, level, current, previous);
        var listeners = _peers.Snapshot().Where(peer => peer.Presence.InVoiceChannel(channel) && !peer.Presence.Deafened).ToList();
        // Pelo servidor, um pacote só (com a lista de quem ouve) e o servidor manda para cada um.
        if (listeners.Count > 0) SendToMany(packet[..length], listeners);
    }

    // ───────────────────────── Envio ─────────────────────────

    private RoomPresence LocalPresence(bool wantsReply)
    {
        var local = _local;
        var flags = PresenceFlags.Channels;
        var channel = _voiceChannel;
        var defaultVoice = RoomChannels.IsDefaultVoice(channel);
        // Na "Geral" as versões antigas também ouvem; nos outros canais, só quem entende canais.
        if (_inVoice) flags |= defaultVoice ? PresenceFlags.InVoice : PresenceFlags.InVoiceChannel;
        if (_muted || _deafened) flags |= PresenceFlags.Muted;
        if (_deafened) flags |= PresenceFlags.Deafened;
        if (local.Live) flags |= PresenceFlags.Live;
        if (local.Live && local.LiveAudio) flags |= PresenceFlags.LiveAudio;
        if (local.Live && local.LiveCamera) flags |= PresenceFlags.LiveCamera;
        if (local.Live && local.PinRequired) flags |= PresenceFlags.PinRequired;
        if (local.Live && local.LiveViaServer) flags |= PresenceFlags.ViaServer;
        if (local.Watching) flags |= PresenceFlags.Watching;
        if (wantsReply) flags |= PresenceFlags.WantsReply;
        return new RoomPresence(local.Name, AppInfo.DisplayVersion, flags, local.LiveMode, local.Viewers, local.ControlPort,
            local.Live ? local.LiveAddress : null, Interlocked.Increment(ref _presenceSequence), local.Avatar,
            _inVoice && !defaultVoice ? channel : null, _createdChannels,
            Keys.Active ? _exchange.PublicKey : null);
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
        // O dono manda junto quem aprovou (e se a sala é só para eles); quem ainda não recebeu a chave nova recebe de novo.
        var roster = _private is { } policy && policy.Owner == SelfId && _roster is { } list ? RoomPacket.Roster(SelfId, policy.ApprovedOnly, list) : null;
        if (_server is { } server)
        {
            // Pelo servidor: um pacote para todo mundo (o servidor repassa), só depois de entrar.
            if (server.Authorized)
            {
                SendRelay(packet, null, server);
                if (roster is not null) SendRelay(roster, null, server);
            }
        }
        else
        {
            foreach (var target in PresenceTargets())
            {
                Send(packet, target);
                if (roster is not null) Send(roster, target);
            }
        }
        var now = DateTimeOffset.UtcNow;
        foreach (var (id, (rekey, until)) in _rekeys)
        {
            if (now > until) _rekeys.TryRemove(id, out _);
            else if (_peers.Find(id) is { } peer) SendTo(rekey, peer);
        }
    }

    private void Send(ReadOnlySpan<byte> packet, IPEndPoint target)
    {
        var socket = _socket;
        if (socket is null) return;
        // Na sala privada, tudo vai lacrado com a chave dela (os pacotes de troca de chave já vêm lacrados com a antiga).
        var sealedPacket = packet.Length > RoomPacket.HeaderSize && packet[3] != (byte)RoomPacketType.Sealed ? Keys.Seal(packet, SelfId) : null;
        try { socket.SendTo(sealedPacket is null ? packet : sealedPacket, SocketFlags.None, target); }
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
                        if (_peers.Find(peerId) is { } peer) SendTo(packet, peer);
                    var (asks, failed) = _downloads.Due(now);
                    foreach (var ask in asks)
                        if (_peers.Find(ask.Source) is { } source) SendTo(RoomPacket.BlobRequest(SelfId, ask.Id, ask.Missing), source);
                    if (_server is { } link) ServerTick(link, now);
                    foreach (var id in failed)
                    {
                        Log.Info($"Sala da rede: ninguém mandou o arquivo {id}.");
                        BlobFailed?.Invoke(id);
                    }
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
        var server = _server;
        // Pela sala do servidor, só vale o que vem do servidor (o resto é a sala direta da rede, que fica de lado).
        if (server is not null && !server.EndPoint.Equals(from)) return;
        if (data[0] == (byte)'{')
        {
            // Anúncio de transmissão (JSON), de qualquer versão.
            if (!DiscoveryAnnouncement.TryParse(data, out var announcement) || announcement is null) return;
            if (server is not null)
            {
                // As transmissões do servidor vêm sem endereço: o endereço é o do próprio servidor.
                if (announcement.Stream is not { Length: > 0 } || announcement.Address != "0.0.0.0") return;
                announcement = announcement with { Address = server.EndPoint.Address.ToString() };
            }
            else if (announcement.Address == "0.0.0.0") return;
            var isNew = !_hosts.ContainsKey(announcement.Key);
            _hosts[announcement.Key] = new DiscoveredHost(announcement, from.Address, DateTimeOffset.UtcNow);
            if (isNew) HostsChanged?.Invoke();
            return;
        }
        if (!RoomPacket.TryReadHeader(data, out var type, out var sender) || sender == SelfId) return;
        var now = DateTimeOffset.UtcNow;
        if (server is not null && (type == RoomPacketType.ServerInfo || sender == server.Id))
        {
            // Do próprio servidor (nunca lacrado: o servidor não tem a chave da sala privada).
            OnServerPacket(server, type, sender, data, now);
            return;
        }
        if (_blocked.ContainsKey(sender)) return;
        var sealedNow = false;
        byte[]? opened = null;
        if (type == RoomPacketType.Sealed)
        {
            // De uma sala privada: só abre com a chave desta sala (outra sala, ou chave velha demais, é ignorada).
            opened = Keys.Active ? Keys.Open(data, sender, now, out sealedNow) : null;
            if (opened is null || !RoomPacket.TryReadHeader(opened, out type, out _)) return;
            data = opened;
            // Esta pessoa já está na chave nova: não precisa mais receber a troca.
            if (sealedNow) _rekeys.TryRemove(sender, out _);
        }
        else if (Keys.Active || type is RoomPacketType.Rekey or RoomPacketType.Roster)
        {
            // Na sala privada, nada aberto entra; troca de chave e lista do dono nunca valem abertas.
            return;
        }
        var body = RoomPacket.Body(data);
        if (_private is { } policy)
        {
            if (type == RoomPacketType.Rekey)
            {
                OnRekey(sender, policy, body, now);
                return;
            }
            if (type == RoomPacketType.Roster)
            {
                if (sender == policy.Owner && RoomPacket.TryReadRoster(body, out var approvedOnly, out var members))
                {
                    _private = policy with { ApprovedOnly = approvedOnly };
                    _roster = members;
                }
                return;
            }
            if (!RoomAccess.Allows(sender, policy.Owner, policy.ApprovedOnly, _roster, Array.Empty<Guid>()))
            {
                // O dono vê quem tem o convite e está esperando a aprovação; os outros só ignoram.
                if (policy.Owner == SelfId && type == RoomPacketType.Presence && RoomPacket.TryReadPresence(body, out var asking)
                    && _pending.Count < 32 && _pending.TryAdd(sender, asking.Name)) PendingChanged?.Invoke();
                return;
            }
        }
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
            case RoomPacketType.BlobRequest:
                OnBlobRequest(sender, body, from, now);
                break;
            case RoomPacketType.BlobChunk:
                OnBlobChunk(sender, body, from, now);
                break;
        }
    }

    /// <summary>O dono mandou a chave nova, cifrada só para este PC: passa a usar (a antiga ainda vale por uns minutos).</summary>
    private void OnRekey(Guid sender, PrivateRoomPolicy policy, ReadOnlySpan<byte> body, DateTimeOffset now)
    {
        if (sender != policy.Owner || !RoomPacket.TryReadRekey(body, out var recipient, out var epoch, out var ownerKey, out var wrapped)) return;
        if (recipient != SelfId || epoch <= Keys.Epoch) return;
        // A chave de troca precisa ser a que o dono anuncia na presença dele.
        if (_peers.Find(sender) is { Presence.ExchangeKey: { } announced } && announced != ownerKey) return;
        if (_exchange.Unwrap(ownerKey, wrapped, epoch, SelfId) is not { } secret) return;
        Keys.Use(secret, epoch, now, keepPrevious: true);
        _announceSoon = true;
        Log.Info($"Sala privada: chave nova do dono (geração {epoch}).");
        KeyChanged?.Invoke(new RoomInvite(secret, policy.Owner, epoch));
    }

    private void OnPresence(Guid sender, RoomPresence presence, IPEndPoint from, DateTimeOffset now)
    {
        // O servidor não é uma pessoa da sala (a presença dele só vale vinda do servidor em uso, em OnServerPacket).
        if (presence.IsServer) return;
        var update = _peers.Update(sender, presence, from, now);
        if (update.Peer is not { } peer) return;
        var previous = update.Previous;
        if (update.IsNew)
        {
            Log.Info($"Sala da rede: {presence.Name} ({from.Address}) apareceu.");
            SendTo(PresencePacket(wantsReply: false), sender, from);
            // Acabou de abrir: pede as mensagens recentes para os primeiros que aparecerem (pelo servidor, o histórico
            // vem do próprio servidor, que guarda o chat).
            if (_server is null && now - _started < TimeSpan.FromSeconds(45) && Interlocked.Increment(ref _historyRequests) <= 3)
            {
                _historyAsked[sender] = now;
                Send(RoomPacket.HistoryRequest(SelfId), from);
            }
            PeerJoined?.Invoke(peer);
        }
        else if (presence.Flags.HasFlag(PresenceFlags.WantsReply) && _replyLimit.TryTake(sender, now))
        {
            SendTo(PresencePacket(wantsReply: false), sender, from);
        }

        if (presence.InAnyVoice != (previous?.InAnyVoice ?? false))
        {
            if (presence.InAnyVoice) PeerJoinedVoice?.Invoke(peer);
            else if (previous is not null) PeerLeftVoice?.Invoke(peer);
        }
        // Os sons de entrar e sair são só de quem entra ou sai do seu canal de voz.
        var mine = VoiceChannel;
        var wasHere = previous?.InVoiceChannel(mine) ?? false;
        var isHere = presence.InVoiceChannel(mine);
        if (wasHere != isHere)
        {
            if (!isHere) _voice?.RemovePeer(sender);
            if (_inVoice) PlayCue(isHere ? VoiceCue.Join : VoiceCue.Leave);
        }
        if (presence.Live && previous is { Live: false }) PeerWentLive?.Invoke(peer);
        if (presence.Avatar is { } avatar) MaybeFetch(avatar, now, sender);

        if (update.IsNew || previous is null || (previous with { Sequence = 0 }) != (presence with { Sequence = 0 }))
            PeersChanged?.Invoke();
    }

    private void OnPeerGone(RoomPeer peer, string reason)
    {
        Log.Info($"Sala da rede: {peer.Presence.Name} {reason}.");
        _outbox.Forget(peer.Id);
        _downloads.Forget(peer.Id, DateTimeOffset.UtcNow);
        if (peer.Presence.InVoiceChannel(VoiceChannel))
        {
            _voice?.RemovePeer(peer.Id);
            PlayCue(VoiceCue.Leave);
        }
        if (peer.Presence.InAnyVoice) PeerLeftVoice?.Invoke(peer);
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
            SendTo(RoomPacket.ChatAck(SelfId, message.AuthorId, message.MessageId), sender, from);
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
        // A imagem vem de quem escreveu ou, se ele já saiu (histórico), de quem repassou a mensagem.
        if (message.Image is { } image) MaybeFetch(image.Blob, now, message.AuthorId, sender);
        MessageReceived?.Invoke(message);
    }

    /// <summary>Relógio do outro PC adiantado ou atrasado demais: usa a hora de chegada.</summary>
    private static DateTimeOffset Clamp(DateTimeOffset time, DateTimeOffset now) => (time - now).Duration() > TimeSpan.FromMinutes(2) ? now : time;

    /// <summary>Quem acabou de abrir pede as mensagens recentes (de todos os canais; só do "geral" para as versões antigas).</summary>
    private void OnHistoryRequest(Guid sender, IPEndPoint from, DateTimeOffset now)
    {
        // Pelo servidor, quem guarda e manda o histórico é o servidor.
        if (_server is not null) return;
        if (_peers.Find(sender) is not { } peer || !_historyLimit.TryTake(sender, now)) return;
        var channels = peer.Presence.SupportsChannels;
        var messages = _history.All().Where(m => channels || m.InDefaultChannel).TakeLast(channels ? 80 : 40);
        foreach (var message in messages)
            Send(RoomPacket.Chat(SelfId, message with { History = true }), from);
    }

    private void OnVoice(Guid sender, ReadOnlySpan<byte> body, IPEndPoint from, DateTimeOffset now)
    {
        if (!RoomPacket.TryReadVoice(body, out var sequence, out var level, out var current, out var previous)) return;
        var peer = _peers.Touch(sender, from, now);
        // Só toca a voz de quem está no mesmo canal (uma versão antiga na "Geral" manda para todo mundo na voz dela).
        if (peer is null || !peer.Presence.InVoiceChannel(VoiceChannel)) return;
        peer.RecordVoice(level, now);
        if (!_deafened) _voice?.Receive(sender, sequence, current, previous);
    }

    // ───────────────────────── Arquivos (foto do perfil e imagens do chat) ─────────────────────────

    /// <summary>Baixa um arquivo de quem tem (na ordem de <paramref name="sources"/>), se este PC ainda não tiver.</summary>
    public void FetchBlob(RoomBlobRef blob, params Guid[] sources)
    {
        if (HasBlob?.Invoke(blob.Id) != false || _cancellation.IsCancellationRequested) return;
        if (_downloads.Start(blob.Id, blob.Size, sources.Where(s => s != SelfId), DateTimeOffset.UtcNow))
            Log.Debug($"Sala da rede: baixando o arquivo {blob.Id} ({blob.Size / 1024.0:0.#} KB).");
    }

    /// <summary>Baixa se ainda não tem e não tentou há pouco (uma foto que não veio é tentada de novo a cada 30 s).</summary>
    private void MaybeFetch(RoomBlobRef blob, DateTimeOffset now, params Guid[] sources)
    {
        if (_downloads.IsActive(blob.Id)) return;
        if (_blobTried.TryGetValue(blob.Id, out var tried) && now - tried < TimeSpan.FromSeconds(30)) return;
        if (HasBlob?.Invoke(blob.Id) != false) return;
        if (_blobTried.Count > 512) _blobTried.Clear();
        _blobTried[blob.Id] = now;
        FetchBlob(blob, sources);
    }

    /// <summary>
    /// Alguém pediu um arquivo: manda os pedaços pedidos, em lotes (sem encher a fila da rede de uma vez). Só responde a
    /// quem está na sala, pelo endereço em que ele está, e com limite: um pedido pequeno não vira uma enxurrada para
    /// outro endereço.
    /// </summary>
    private void OnBlobRequest(Guid sender, ReadOnlySpan<byte> body, IPEndPoint from, DateTimeOffset now)
    {
        if (!RoomPacket.TryReadBlobRequest(body, out var id, out var missing)) return;
        var peer = _peers.Find(sender);
        if (peer is null || !peer.EndPoint.Address.Equals(from.Address) || !_blobLimit.TryTake(sender, now)) return;
        if (!RoomBlob.TryParseId(id, out var hash) || BlobSource?.Invoke(id) is not { Length: > 0 and <= RoomBlob.MaxImageBytes } data) return;
        if (!_uploading.TryAdd((sender, id), 0)) return;
        var count = RoomBlob.ChunkCount(data.Length);
        var indexes = missing.Length == 0 ? Enumerable.Range(0, count).ToArray() : missing.Where(i => i < count).Distinct().ToArray();
        var token = _cancellation.Token;
        _ = Task.Run(async () =>
        {
            var slot = false;
            try
            {
                await _uploadSlots.WaitAsync(token).ConfigureAwait(false);
                slot = true;
                var buffer = new byte[RoomPacket.MaximumSize];
                for (var i = 0; i < indexes.Length && !token.IsCancellationRequested; i++)
                {
                    var length = RoomPacket.WriteBlobChunk(buffer, SelfId, hash, data, indexes[i]);
                    SendTo(buffer.AsSpan(0, length), sender, from);
                    if (i % 24 == 23) await Task.Delay(2, token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception exception) { Log.Debug($"Envio do arquivo {id}: {exception.Message}"); }
            finally
            {
                if (slot) _uploadSlots.Release();
                _uploading.TryRemove((sender, id), out _);
            }
        }, token);
    }

    private void OnBlobChunk(Guid sender, ReadOnlySpan<byte> body, IPEndPoint from, DateTimeOffset now)
    {
        if (!RoomPacket.TryReadBlobChunk(body, out var id, out var size, out var index, out var data)) return;
        _peers.Touch(sender, from, now);
        if (_downloads.Accept(id, sender, size, index, data, now) is not { } blob) return;
        Log.Debug($"Sala da rede: arquivo {id} chegou ({blob.Length / 1024.0:0.#} KB).");
        BlobReceived?.Invoke(id, blob);
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
