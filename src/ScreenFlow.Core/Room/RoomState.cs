using System.Net;

namespace ScreenFlow.Core.Room;

/// <summary>Um PC visto na sala da rede.</summary>
public sealed class RoomPeer
{
    internal RoomPeer(Guid id, IPEndPoint endPoint, RoomPresence presence, DateTimeOffset now)
    {
        Id = id;
        EndPoint = endPoint;
        Presence = presence;
        FirstSeen = LastSeen = EndPointSeen = now;
    }

    public Guid Id { get; }
    public IPEndPoint EndPoint { get; internal set; }
    public RoomPresence Presence { get; internal set; }
    public DateTimeOffset FirstSeen { get; }
    public DateTimeOffset LastSeen { get; internal set; }
    internal DateTimeOffset EndPointSeen { get; set; }

    /// <summary>Último quadro de voz recebido e o volume dele (para o anel de quem está falando).</summary>
    public DateTimeOffset LastVoice { get; private set; }
    public byte VoiceLevel { get; private set; }

    /// <summary>Chegou um quadro de voz desta pessoa (com o volume da fala).</summary>
    public void RecordVoice(byte level, DateTimeOffset now)
    {
        LastVoice = now;
        VoiceLevel = level;
    }

    /// <summary>Endereço para assistir a transmissão: o IP que o host anuncia ou, sem ele, o de onde a presença veio.</summary>
    public IPAddress WatchAddress => Presence.LiveAddress ?? EndPoint.Address;

    public bool IsSpeaking(DateTimeOffset now) => Presence.InAnyVoice && !Presence.Muted && now - LastVoice < TimeSpan.FromMilliseconds(320) && VoiceLevel > 24;
}

public readonly record struct PeerUpdate(RoomPeer? Peer, bool IsNew, RoomPresence? Previous)
{
    public static PeerUpdate Ignored => default;
}

/// <summary>
/// Quem está na sala. Um PC some depois de alguns segundos sem presença. Se o mesmo PC aparece por dois caminhos (rede
/// local e Radmin, por exemplo), fica no primeiro enquanto ele continuar respondendo, para a voz não ficar trocando.
/// </summary>
public sealed class RoomPeerTable
{
    /// <summary>
    /// Quantas pessoas a sala guarda. É também o máximo de um servidor ScreenFlow (1.4.1), para que todo mundo que entrou
    /// no servidor apareça para todo mundo.
    /// </summary>
    public const int MaxPeers = 512;
    private readonly Dictionary<Guid, RoomPeer> _peers = [];
    private readonly object _gate = new();

    public RoomPeerTable(TimeSpan? timeout = null) => Timeout = timeout ?? TimeSpan.FromSeconds(7);

    public TimeSpan Timeout { get; }

    public int Count
    {
        get { lock (_gate) return _peers.Count; }
    }

    public PeerUpdate Update(Guid id, RoomPresence presence, IPEndPoint from, DateTimeOffset now)
    {
        lock (_gate)
        {
            if (!_peers.TryGetValue(id, out var peer))
            {
                if (_peers.Count >= MaxPeers) return PeerUpdate.Ignored;
                peer = new RoomPeer(id, from, presence, now);
                _peers[id] = peer;
                return new PeerUpdate(peer, true, null);
            }
            var previous = peer.Presence;
            peer.Presence = presence;
            peer.LastSeen = now;
            Touch(peer, from, now);
            return new PeerUpdate(peer, false, previous);
        }
    }

    /// <summary>Qualquer pacote de um PC conhecido conta como sinal de vida e atualiza o caminho até ele.</summary>
    public RoomPeer? Touch(Guid id, IPEndPoint from, DateTimeOffset now)
    {
        lock (_gate)
        {
            if (!_peers.TryGetValue(id, out var peer)) return null;
            Touch(peer, from, now);
            return peer;
        }
    }

    private static void Touch(RoomPeer peer, IPEndPoint from, DateTimeOffset now)
    {
        if (peer.EndPoint.Equals(from)) peer.EndPointSeen = now;
        else if (now - peer.EndPointSeen > TimeSpan.FromSeconds(5))
        {
            peer.EndPoint = from;
            peer.EndPointSeen = now;
        }
    }

    public RoomPeer? Find(Guid id)
    {
        lock (_gate) return _peers.GetValueOrDefault(id);
    }

    public bool Remove(Guid id, out RoomPeer? peer)
    {
        lock (_gate) return _peers.Remove(id, out peer);
    }

    /// <summary>Tira quem não deu sinal dentro do prazo e devolve quem saiu.</summary>
    public List<RoomPeer> Expire(DateTimeOffset now)
    {
        var gone = new List<RoomPeer>();
        lock (_gate)
        {
            foreach (var peer in _peers.Values)
                if (now - peer.LastSeen > Timeout) gone.Add(peer);
            foreach (var peer in gone) _peers.Remove(peer.Id);
        }
        return gone;
    }

    /// <summary>Quem está na sala, em ordem de nome (para a tela).</summary>
    public IReadOnlyList<RoomPeer> Snapshot()
    {
        lock (_gate) return _peers.Values.OrderBy(p => p.Presence.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
    }

    /// <summary>Quem está na sala, sem ordem: para enviar (a voz pede a cada 20 ms e não precisa ordenar).</summary>
    public RoomPeer[] All()
    {
        lock (_gate) return [.. _peers.Values];
    }
}

/// <summary>
/// Entrega do chat: cada mensagem vai direto para cada PC da sala e é reenviada até ele confirmar (ou desistir depois de
/// algumas tentativas). Quem recebe confirma sempre, até as repetidas, e descarta as que já mostrou.
/// </summary>
public sealed class ChatOutbox
{
    public const int MaxAttempts = 6;
    public static readonly TimeSpan RetryInterval = TimeSpan.FromMilliseconds(450);
    private readonly List<Pending> _pending = [];
    private readonly object _gate = new();

    public int Count
    {
        get { lock (_gate) return _pending.Count; }
    }

    /// <summary>Registra a mensagem já enviada uma vez para <paramref name="targets"/>.</summary>
    public void Add(RoomChatMessage message, byte[] packet, IEnumerable<Guid> targets, DateTimeOffset now)
    {
        var pending = new Pending(message, packet, [.. targets], now + RetryInterval);
        if (pending.Targets.Count == 0) return;
        lock (_gate)
        {
            _pending.Add(pending);
            if (_pending.Count > 200) _pending.RemoveAt(0);
        }
    }

    public void Acknowledge(Guid peer, Guid author, ulong messageId)
    {
        lock (_gate)
        {
            foreach (var pending in _pending)
                if (pending.Message.MessageId == messageId && pending.Message.AuthorId == author) pending.Targets.Remove(peer);
            _pending.RemoveAll(p => p.Targets.Count == 0);
        }
    }

    /// <summary>Quem saiu da sala não vai confirmar: para de tentar.</summary>
    public void Forget(Guid peer)
    {
        lock (_gate)
        {
            foreach (var pending in _pending) pending.Targets.Remove(peer);
            _pending.RemoveAll(p => p.Targets.Count == 0);
        }
    }

    /// <summary>Reenvios que vencem agora. Mensagens sem resposta depois de <see cref="MaxAttempts"/> tentativas são largadas.</summary>
    public List<(Guid Peer, byte[] Packet)> Due(DateTimeOffset now)
    {
        var sends = new List<(Guid, byte[])>();
        lock (_gate)
        {
            foreach (var pending in _pending)
            {
                if (now < pending.NextAttempt) continue;
                pending.Attempts++;
                pending.NextAttempt = now + RetryInterval;
                if (pending.Attempts >= MaxAttempts)
                {
                    pending.Targets.Clear();
                    continue;
                }
                foreach (var peer in pending.Targets) sends.Add((peer, pending.Packet));
            }
            _pending.RemoveAll(p => p.Targets.Count == 0);
        }
        return sends;
    }

    private sealed class Pending(RoomChatMessage message, byte[] packet, HashSet<Guid> targets, DateTimeOffset next)
    {
        public RoomChatMessage Message { get; } = message;
        public byte[] Packet { get; } = packet;
        public HashSet<Guid> Targets { get; } = targets;
        public int Attempts { get; set; }
        public DateTimeOffset NextAttempt { get; set; } = next;
    }
}

/// <summary>Lembra os últimos N ids vistos (descarta repetidos sem crescer para sempre).</summary>
public sealed class RecentIds<T>(int capacity = 1024) where T : notnull
{
    private readonly HashSet<T> _set = [];
    private readonly Queue<T> _order = new();
    private readonly object _gate = new();

    /// <summary>true se o id é novo.</summary>
    public bool Add(T id)
    {
        lock (_gate)
        {
            if (!_set.Add(id)) return false;
            _order.Enqueue(id);
            while (_order.Count > capacity) _set.Remove(_order.Dequeue());
            return true;
        }
    }

    public bool Contains(T id)
    {
        lock (_gate) return _set.Contains(id);
    }
}

/// <summary>Limite de mensagens por PC (balde de fichas): uma rajada curta passa, uma enxurrada não.</summary>
public sealed class RateLimiter<TKey>(int burst, double perSecond) where TKey : notnull
{
    private readonly Dictionary<TKey, (double Tokens, DateTimeOffset At)> _buckets = [];
    private readonly object _gate = new();

    public bool TryTake(TKey key, DateTimeOffset now)
    {
        lock (_gate)
        {
            var (tokens, at) = _buckets.TryGetValue(key, out var bucket) ? bucket : ((double)burst, now);
            tokens = Math.Min(burst, tokens + (now - at).TotalSeconds * perSecond);
            if (tokens < 1)
            {
                _buckets[key] = (tokens, now);
                return false;
            }
            _buckets[key] = (tokens - 1, now);
            if (_buckets.Count > 256) _buckets.Remove(_buckets.MinBy(b => b.Value.At).Key);
            return true;
        }
    }
}

/// <summary>Histórico do chat desta sessão (todos os canais), em ordem de horário, sem repetidas. Guarda as últimas 600 mensagens.</summary>
public sealed class ChatHistory
{
    public const int Capacity = 600;
    private readonly List<RoomChatMessage> _messages = [];
    private readonly HashSet<(Guid, ulong)> _ids = [];
    private readonly object _gate = new();

    public int Count
    {
        get { lock (_gate) return _messages.Count; }
    }

    /// <summary>Adiciona em ordem de horário. Retorna a posição, ou -1 se a mensagem já estava lá.</summary>
    public int Add(RoomChatMessage message)
    {
        lock (_gate)
        {
            if (!_ids.Add((message.AuthorId, message.MessageId))) return -1;
            var index = _messages.Count;
            while (index > 0 && _messages[index - 1].Time > message.Time) index--;
            _messages.Insert(index, message);
            if (_messages.Count > Capacity)
            {
                var oldest = _messages[0];
                _messages.RemoveAt(0);
                _ids.Remove((oldest.AuthorId, oldest.MessageId));
                index--;
            }
            return Math.Max(0, index);
        }
    }

    public bool Contains(Guid author, ulong id)
    {
        lock (_gate) return _ids.Contains((author, id));
    }

    public IReadOnlyList<RoomChatMessage> Recent(int count)
    {
        lock (_gate) return _messages.Skip(Math.Max(0, _messages.Count - count)).ToArray();
    }

    public IReadOnlyList<RoomChatMessage> All()
    {
        lock (_gate) return _messages.ToArray();
    }
}
