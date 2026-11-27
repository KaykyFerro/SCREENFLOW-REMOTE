using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using ScreenFlow.Core.Diagnostics;
using ScreenFlow.Core.Protocol;
using ScreenFlow.Core.Room;

namespace ScreenFlow.Core.Server;

/// <summary>Uma pessoa na sala do servidor, para a tela do servidor e o console.</summary>
public sealed record ServerMemberInfo(Guid Id, string Name, string Address, string? VoiceChannel, bool Live);

/// <summary>
/// A sala do servidor (1.4.1): presença, chat, voz, arquivos e canais de todo mundo passam por aqui. Cada PC manda um
/// envelope (<see cref="RoomPacketType.Relay"/>) e o servidor entrega o pacote de dentro para quem deve receber. Assim
/// funciona no Tailscale (que não tem broadcast) e cada PC manda uma cópia só da voz, em vez de uma para cada pessoa.
/// </summary>
/// <remarks>
/// O servidor guarda o chat aberto (para quem chegar depois ver a conversa, mesmo com todo mundo desligado) e os canais.
/// Na sala privada tudo vai lacrado com a chave do convite: o servidor só repassa e não consegue ler nem guardar.
/// </remarks>
public sealed class RoomRelay : IAsyncDisposable
{
    private static readonly TimeSpan PresenceInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan MemberTimeout = TimeSpan.FromSeconds(15);

    /// <summary>Quem ainda não provou a senha ocupa pouco lugar: no máximo tantos ao todo e tantos por IP.</summary>
    public const int MaxPending = 32, MaxPendingPerAddress = 4;

    /// <summary>Senhas erradas por IP num minuto: passando disso, as provas desse IP são ignoradas até o minuto acabar.</summary>
    public const int MaxWrongPasswordsPerMinute = 10;

    private readonly ConcurrentDictionary<IPAddress, (DateTimeOffset Start, int Count)> _wrongPasswords = new();
    private readonly ServerOptions _options;
    private readonly Func<IReadOnlyList<DiscoveryAnnouncement>> _announcements;
    private readonly Func<int> _controlPort;
    private readonly ConcurrentDictionary<Guid, Member> _members = new();
    private readonly ChatHistory _history = new();
    private readonly ChannelStore _channels;
    private readonly ChatArchive? _archive;
    private readonly CancellationTokenSource _cancellation = new();
    private readonly string _nonce = Convert.ToBase64String(RandomNumberGenerator.GetBytes(24));
    private Socket? _socket;
    private Thread? _receiver;
    private Task? _ticker;
    private uint _sequence;
    private volatile bool _announceSoon;

    public RoomRelay(ServerOptions options, Guid serverId, Func<IReadOnlyList<DiscoveryAnnouncement>> announcements, Func<int> controlPort)
    {
        _options = options;
        ServerId = serverId;
        _announcements = announcements;
        _controlPort = controlPort;
        var folder = options.DataDirectory;
        _channels = new ChannelStore(folder is null ? null : Path.Combine(folder, "canais.json"));
        if (folder is not null)
        {
            try
            {
                Directory.CreateDirectory(folder);
                _archive = new ChatArchive(folder);
                foreach (var message in _archive.LoadRecent(ChatHistory.Capacity)) _history.Add(message with { History = false });
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                Log.Warn($"Servidor: o chat não vai ser guardado ({exception.Message}).");
            }
        }
    }

    public Guid ServerId { get; }

    public int Port => (_socket?.LocalEndPoint as IPEndPoint)?.Port ?? _options.RoomPort;

    public IReadOnlyList<RoomChannel> Channels => _channels.All;

    public int MessageCount => _history.Count;

    public IReadOnlyList<ServerMemberInfo> Members => _members.Values.Where(m => m.Authorized)
        .Select(m => new ServerMemberInfo(m.Id, m.Name.Length > 0 ? m.Name : "…", m.EndPoint.Address.ToString(), m.Presence?.CurrentVoiceChannel, m.Presence?.Live ?? false))
        .OrderBy(m => m.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();

    /// <summary>Alguém entrou ou saiu da sala, ou os canais mudaram.</summary>
    public event Action? Changed;

    public void Start()
    {
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp)
        {
            ReceiveBufferSize = 4 * 1024 * 1024,
            SendBufferSize = 4 * 1024 * 1024,
        };
        if (OperatingSystem.IsWindows())
            try { socket.IOControl(-1744830452, [0, 0, 0, 0], null); } catch { }
        socket.Bind(new IPEndPoint(_options.BindAddress, _options.RoomPort));
        _socket = socket;
        _receiver = new Thread(ReceiveLoop) { IsBackground = true, Name = "ScreenFlow servidor: sala", Priority = ThreadPriority.AboveNormal };
        _receiver.Start();
        _ticker = Task.Run(TickLoopAsync);
        Log.Info($"Servidor: sala na porta UDP {Port} ({_channels.All.Count} canais, {_history.Count} mensagens guardadas).");
    }

    public async ValueTask DisposeAsync()
    {
        if (_cancellation.IsCancellationRequested) return;
        _cancellation.Cancel();
        if (_ticker is not null) try { await _ticker; } catch { }
        _socket?.Dispose();
        _receiver?.Join(1000);
        _archive?.Flush(TimeSpan.FromSeconds(2));
        _archive?.Dispose();
        _cancellation.Dispose();
    }

    // ───────────────────────── Recepção ─────────────────────────

    private void ReceiveLoop()
    {
        var socket = _socket!;
        var buffer = new byte[4096];
        socket.ReceiveTimeout = 500;
        while (!_cancellation.IsCancellationRequested)
        {
            EndPoint from = new IPEndPoint(IPAddress.Any, 0);
            int length;
            try { length = socket.ReceiveFrom(buffer, ref from); }
            catch (SocketException) { continue; }
            catch (ObjectDisposedException) { break; }
            if (length <= 0 || from is not IPEndPoint source) continue;
            try { Handle(buffer.AsSpan(0, length), source, DateTimeOffset.UtcNow); }
            catch (Exception exception) { Log.Debug($"Servidor: pacote da sala descartado ({source}): {exception.Message}"); }
        }
    }

    private void Handle(ReadOnlySpan<byte> data, IPEndPoint from, DateTimeOffset now)
    {
        if (!RoomPacket.TryReadHeader(data, out var type, out var sender) || sender == ServerId) return;
        var body = RoomPacket.Body(data);
        switch (type)
        {
            case RoomPacketType.ServerInfo:
                OnProbe(sender, body, from, now);
                return;
            case RoomPacketType.Relay:
                OnRelay(sender, body, from, now);
                return;
            case RoomPacketType.ChannelOp:
                if (Authorized(sender, from, now) is { } editor) OnChannelOp(editor, body);
                return;
            case RoomPacketType.Presence or RoomPacketType.HistoryRequest or RoomPacketType.Leave:
                if (Authorized(sender, from, now) is { } member) HandleLocal(member, type, data, now);
                return;
        }
    }

    /// <summary>A sondagem de um PC: registra quem é, confere a senha e responde com o estado do servidor.</summary>
    private void OnProbe(Guid sender, ReadOnlySpan<byte> body, IPEndPoint from, DateTimeOffset now)
    {
        if (!RoomPacket.TryReadServerProbe(body, out _, out var proof)) return;
        if (Bind(sender, from, now, create: true) is not { } member) return;
        var justAuthorized = false;
        if (!member.Authorized && proof is not null && _options.HasPassword && !TooManyWrongPasswords(from.Address, now))
        {
            if (RoomPacket.VerifyServerProof(_options.Password!, _nonce, sender, proof))
            {
                member.Authorized = justAuthorized = true;
                _wrongPasswords.TryRemove(from.Address, out _);
                Log.Info($"Servidor: {from.Address} entrou na sala com a senha.");
                _announceSoon = true;
                Changed?.Invoke();
            }
            else WrongPassword(from.Address, now);
        }
        // Uma resposta a cada 150 ms por PC (a sondagem não vira amplificador), mas quem acabou de entrar sempre ouve.
        if (!justAuthorized && now - member.LastInfo < TimeSpan.FromMilliseconds(150)) return;
        member.LastInfo = now;
        Send(RoomPacket.ServerInfo(ServerId, Info(member.Authorized)), member);
        if (member.Authorized && !member.Welcomed)
        {
            member.Welcomed = true;
            Send(PresencePacket(), member);
            foreach (var announcement in _announcements()) Send(announcement.ToBytes(), member);
        }
    }

    private RoomServerInfo Info(bool authorized)
        => new(_options.Name, AppInfo.DisplayVersion, _nonce, _options.HasPassword, authorized, _members.Values.Count(m => m.Authorized),
            _announcements().Count, _controlPort());

    private void OnRelay(Guid sender, ReadOnlySpan<byte> body, IPEndPoint from, DateTimeOffset now)
    {
        if (Authorized(sender, from, now) is not { } member)
        {
            // Quem não entrou (ou o servidor reiniciou e esqueceu) fica sabendo e manda a sondagem de novo.
            if (Bind(sender, from, now, create: true) is { } stranger && now - stranger.LastInfo > TimeSpan.FromSeconds(1))
            {
                stranger.LastInfo = now;
                Send(RoomPacket.ServerInfo(ServerId, Info(stranger.Authorized)), stranger);
            }
            return;
        }
        if (!RoomPacket.TryReadRelay(body, sender, out var targets, out var inner)) return;
        var toServer = targets.Length == 1 && targets[0] == ServerId;
        var cost = toServer ? 1 : targets.Length == 0 ? Math.Max(1, _members.Count - 1) : targets.Length;
        if (!member.TryTake(cost, now)) return;
        if (!RoomPacket.TryReadHeader(inner, out var innerType, out _)) return;
        if (toServer)
        {
            HandleLocal(member, innerType, inner, now);
            return;
        }
        Inspect(member, innerType, inner, now);
        if (targets.Length == 0)
        {
            foreach (var other in _members.Values)
                if (other.Authorized && other.Id != sender) Send(inner, other);
        }
        else
        {
            foreach (var target in targets)
                if (target != sender && _members.TryGetValue(target, out var other) && other.Authorized) Send(inner, other);
        }
        if (innerType == RoomPacketType.Leave) Remove(member, "saiu");
    }

    /// <summary>Pacotes para o próprio servidor: a presença (para a lista), o pedido do histórico e a saída.</summary>
    private void HandleLocal(Member member, RoomPacketType type, ReadOnlySpan<byte> packet, DateTimeOffset now)
    {
        switch (type)
        {
            case RoomPacketType.Presence:
                Inspect(member, type, packet, now);
                break;
            case RoomPacketType.HistoryRequest:
                if (now - member.LastHistory < TimeSpan.FromSeconds(10)) return;
                member.LastHistory = now;
                SendHistory(member);
                break;
            case RoomPacketType.Leave:
                Remove(member, "saiu");
                break;
        }
    }

    /// <summary>O servidor lê o que vai aberto: a presença (nome e voz, para a lista) e o chat (para guardar).</summary>
    private void Inspect(Member member, RoomPacketType type, ReadOnlySpan<byte> packet, DateTimeOffset now)
    {
        var body = RoomPacket.Body(packet);
        switch (type)
        {
            case RoomPacketType.Presence when RoomPacket.TryReadPresence(body, out var presence):
                var changed = member.Presence is not { } previous || previous.Name != presence.Name || previous.CurrentVoiceChannel != presence.CurrentVoiceChannel
                              || previous.Live != presence.Live;
                if (member.Name.Length == 0) Log.Info($"Servidor: {presence.Name} ({member.EndPoint.Address}) entrou na sala.");
                member.Presence = presence;
                member.Name = presence.Name;
                if (changed) Changed?.Invoke();
                break;
            case RoomPacketType.Chat when RoomPacket.TryReadChat(body, out var message) && !message.History:
                var clean = message with
                {
                    AuthorId = member.Id,
                    AuthorName = member.Name.Length > 0 ? member.Name : message.AuthorName,
                    Time = (message.Time - now).Duration() > TimeSpan.FromMinutes(2) ? now : message.Time,
                    // Quem ainda não viu que o canal mudou de nome manda no nome antigo: guarda no nome de agora.
                    Channel = _channels.CurrentTextName(message.Channel),
                };
                if (_history.Add(clean) >= 0) _archive?.Add(clean);
                break;
        }
    }

    private void OnChannelOp(Member member, ReadOnlySpan<byte> body)
    {
        if (!member.TryTake(50, DateTimeOffset.UtcNow)) return;
        if (!RoomPacket.TryReadChannelOp(body, out var operation, out var channel, out var renamed)) return;
        var problem = _channels.Apply(operation, channel, renamed);
        var who = member.Name.Length > 0 ? member.Name : member.EndPoint.Address.ToString();
        if (problem is null)
        {
            Log.Info(operation switch
            {
                RoomChannelOperation.Create => $"Servidor: {who} criou o canal {channel.Name}.",
                RoomChannelOperation.Rename => $"Servidor: {who} renomeou o canal {channel.Name} para {renamed!.Name}.",
                _ => $"Servidor: {who} apagou o canal {channel.Name}.",
            });
            _announceSoon = true;
            Changed?.Invoke();
        }
        else Log.Debug($"Servidor: canal de {who} não mudou: {problem}");
    }

    private void SendHistory(Member member)
    {
        foreach (var message in _history.Recent(80))
            Send(RoomPacket.Chat(ServerId, message with { History = true, Channel = _channels.CurrentTextName(message.Channel) }), member);
    }

    // ───────────────────────── Quem está na sala ─────────────────────────

    /// <summary>
    /// O PC com este id, no endereço de sempre. Um id que aparece de outro endereço só troca de endereço depois de alguns
    /// segundos sem sinal do antigo (ninguém toma o lugar de quem está conectado).
    /// </summary>
    private Member? Bind(Guid id, IPEndPoint from, DateTimeOffset now, bool create)
    {
        if (_members.TryGetValue(id, out var member))
        {
            if (member.EndPoint.Equals(from))
            {
                member.LastSeen = now;
                return member;
            }
            if (now - member.LastSeen < TimeSpan.FromSeconds(6)) return null;
            member.Rebind(from, now, authorized: !_options.HasPassword);
            return member;
        }
        if (!create || !HasRoomFor(from.Address)) return null;
        member = new Member(id, from, now) { Authorized = !_options.HasPassword };
        return _members.TryAdd(id, member) ? member : _members.GetValueOrDefault(id);
    }

    /// <summary>
    /// Cabe mais um PC? Quem já entrou conta até o máximo de pessoas. Com senha, quem ainda não provou a senha fica num
    /// espaço à parte e pequeno (por IP e ao todo), então ids inventados não tomam o lugar de quem tem a senha.
    /// </summary>
    private bool HasRoomFor(IPAddress address)
    {
        int authorized = 0, pending = 0, pendingHere = 0;
        foreach (var member in _members.Values)
        {
            if (member.Authorized) authorized++;
            else
            {
                pending++;
                if (member.EndPoint.Address.Equals(address)) pendingHere++;
            }
        }
        if (authorized >= _options.MaxMembers) return false;
        return !_options.HasPassword || (pending < MaxPending && pendingHere < MaxPendingPerAddress);
    }

    private bool TooManyWrongPasswords(IPAddress address, DateTimeOffset now)
        => _wrongPasswords.TryGetValue(address, out var wrong) && wrong.Count >= MaxWrongPasswordsPerMinute && now - wrong.Start < TimeSpan.FromMinutes(1);

    private void WrongPassword(IPAddress address, DateTimeOffset now)
    {
        var wrong = _wrongPasswords.AddOrUpdate(address, _ => (now, 1), (_, w) => now - w.Start > TimeSpan.FromMinutes(1) ? (now, 1) : (w.Start, w.Count + 1));
        if (wrong.Count == MaxWrongPasswordsPerMinute) Log.Warn($"Servidor: senhas erradas demais de {address}; as próximas tentativas esperam um minuto.");
        if (_wrongPasswords.Count > 1024)
            foreach (var stale in _wrongPasswords.Where(w => now - w.Value.Start > TimeSpan.FromMinutes(1)).Select(w => w.Key).ToList())
                _wrongPasswords.TryRemove(stale, out _);
    }

    private Member? Authorized(Guid id, IPEndPoint from, DateTimeOffset now)
        => _members.TryGetValue(id, out var member) && member.EndPoint.Equals(from) && member.Authorized ? Touch(member, now) : null;

    private static Member Touch(Member member, DateTimeOffset now)
    {
        member.LastSeen = now;
        return member;
    }

    private void Remove(Member member, string reason)
    {
        if (!_members.TryRemove(member.Id, out _)) return;
        if (member.Name.Length > 0) Log.Info($"Servidor: {member.Name} {reason}.");
        Changed?.Invoke();
    }

    // ───────────────────────── Envio ─────────────────────────

    private RoomPresence ServerPresence()
        => new(_options.Name, AppInfo.DisplayVersion, PresenceFlags.Channels | PresenceFlags.Server, Sequence: Interlocked.Increment(ref _sequence),
            Channels: new ValueList<RoomChannel>(_channels.All));

    private byte[] PresencePacket() => RoomPacket.Presence(ServerId, ServerPresence());

    private void Send(ReadOnlySpan<byte> packet, Member member)
    {
        try { _socket?.SendTo(packet, SocketFlags.None, member.Address); }
        catch (SocketException) { }
        catch (ObjectDisposedException) { }
    }

    private async Task TickLoopAsync()
    {
        var token = _cancellation.Token;
        var lastPresence = DateTimeOffset.MinValue;
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(200));
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
                        var presence = PresencePacket();
                        var announcements = _announcements().Select(a => a.ToBytes()).ToList();
                        foreach (var member in _members.Values)
                        {
                            if (now - member.LastSeen > MemberTimeout)
                            {
                                Remove(member, "sem sinal");
                                continue;
                            }
                            if (!member.Authorized) continue;
                            Send(presence, member);
                            foreach (var announcement in announcements) Send(announcement, member);
                        }
                    }
                }
                catch (Exception exception) when (!token.IsCancellationRequested)
                {
                    Log.Warn($"Servidor: sala: {exception.Message}");
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    private sealed class Member
    {
        private const double Burst = 40_000;
        private const double PerSecond = 20_000;
        private double _tokens = Burst;
        private DateTimeOffset _tokensAt;

        public Member(Guid id, IPEndPoint endPoint, DateTimeOffset now)
        {
            Id = id;
            EndPoint = endPoint;
            Address = endPoint.Serialize();
            LastSeen = now;
            _tokensAt = now;
        }

        public Guid Id { get; }
        public IPEndPoint EndPoint { get; private set; }
        public SocketAddress Address { get; private set; }
        public DateTimeOffset LastSeen { get; set; }
        public DateTimeOffset LastInfo { get; set; }
        public DateTimeOffset LastHistory { get; set; }
        public volatile bool Authorized;
        public bool Welcomed { get; set; }
        public string Name { get; set; } = string.Empty;
        public RoomPresence? Presence { get; set; }

        public void Rebind(IPEndPoint endPoint, DateTimeOffset now, bool authorized)
        {
            EndPoint = endPoint;
            Address = endPoint.Serialize();
            LastSeen = now;
            Authorized = authorized;
            Welcomed = false;
        }

        /// <summary>Limite de entregas por segundo (um pacote para todo mundo conta uma vez para cada pessoa).</summary>
        public bool TryTake(double cost, DateTimeOffset now)
        {
            lock (this)
            {
                _tokens = Math.Min(Burst, _tokens + (now - _tokensAt).TotalSeconds * PerSecond);
                _tokensAt = now;
                if (_tokens < cost) return false;
                _tokens -= cost;
                return true;
            }
        }
    }
}
