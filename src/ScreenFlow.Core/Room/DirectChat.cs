using System.Security.Cryptography;

namespace ScreenFlow.Core.Room;

/// <summary>Por onde o chat privado chega até as pessoas: as salas e os servidores abertos neste PC.</summary>
public interface IDirectTransport
{
    /// <summary>A pessoa está agora numa sala ou servidor aberto aqui, anunciando esta chave do chat privado.</summary>
    bool CanReach(Guid peer, string identityKey);

    /// <summary>Manda o pacote para a pessoa, pela sala em que ela anuncia esta chave. false se ela não está em nenhuma.</summary>
    bool Send(Guid peer, string identityKey, byte[] packet);
}

/// <summary>O que aconteceu com um pacote do chat privado que chegou.</summary>
public enum DirectReceive
{
    /// <summary>Não era para este PC, não abriu (foi mexido ou não é da conversa) ou não tem como responder.</summary>
    Ignored,
    /// <summary>Mensagem nova: guardada e confirmada.</summary>
    Message,
    /// <summary>Mensagem que já tinha chegado (a confirmação se perdeu): só confirma de novo.</summary>
    Duplicate,
    /// <summary>A outra pessoa confirmou que recebeu uma mensagem sua.</summary>
    Delivered,
    /// <summary>A chave da pessoa não é a guardada: nada dela entra até você aceitar a chave nova.</summary>
    KeyChanged,
}

/// <summary>
/// (1.5.0) As conversas privadas deste PC: manda (com fila e reenvio até a outra pessoa confirmar), recebe (confere quem
/// mandou, abre, guarda e confirma) e cuida da chave de cada pessoa. A primeira chave que a pessoa mostra fica guardada
/// (como no WhatsApp e no Signal); se aparecer outra com o mesmo id, nada entra até você aceitar, e o código de
/// segurança (<see cref="SafetyCode"/>) serve para conferir com ela. Não é thread-safe: use sempre da mesma thread.
/// </summary>
public sealed class DirectChat
{
    /// <summary>Mensagens suas esperando para ir (as mais antigas continuam guardadas e voltam para a fila ao abrir).</summary>
    public const int MaxOutbox = 400;
    /// <summary>Na volta de alguém, quantas mensagens vão por pessoa a cada <see cref="Tick"/>.</summary>
    public const int SendsPerTick = 6;
    public const int FastAttempts = 8;
    public const int PreviewLength = 90;
    public static readonly TimeSpan FastRetry = TimeSpan.FromSeconds(3);
    public static readonly TimeSpan SlowRetry = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan MaxFuture = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan MaxPast = TimeSpan.FromDays(30);

    private readonly DirectIdentity _identity;
    private readonly IDirectTransport _transport;
    private readonly Dictionary<(Guid Peer, ulong Id), Outgoing> _outbox = [];
    private readonly Dictionary<Guid, HashSet<ulong>> _seen = [];

    public DirectChat(Guid self, DirectIdentity identity, DirectStore store, IDirectTransport transport)
    {
        if (self == Guid.Empty) throw new ArgumentException("Id vazio.", nameof(self));
        Self = self;
        _identity = identity;
        Store = store;
        _transport = transport;
    }

    public Guid Self { get; }
    public DirectStore Store { get; }

    /// <summary>A chave pública deste PC no chat privado (vai na presença).</summary>
    public string PublicKey => _identity.PublicKey;

    /// <summary>Mensagens suas que ainda não chegaram.</summary>
    public int PendingCount => _outbox.Count;

    /// <summary>Uma mensagem entrou na conversa com a pessoa, ou uma sua mudou de estado (saiu, chegou).</summary>
    public event Action<Guid, DirectRecord>? MessageChanged;

    /// <summary>A pessoa mudou na lista: nome, não lidas, última mensagem ou a chave.</summary>
    public event Action<Guid>? ContactChanged;

    /// <summary>
    /// O código de segurança da conversa (20 números): o mesmo nos dois PCs quando cada um tem a chave verdadeira do
    /// outro. Null sem a chave da pessoa.
    /// </summary>
    public string? SafetyCode(Guid peer) => Store.Find(peer)?.IdentityKey is { } key ? DirectIdentity.SafetyCode(PublicKey, key) : null;

    /// <summary>Ao abrir: as mensagens suas que não tinham chegado voltam para a fila (vão quando a pessoa aparecer).</summary>
    public void RestoreOutbox()
    {
        var changed = false;
        foreach (var contact in Store.Contacts.Where(c => c.HasPending))
        {
            var waiting = Store.Load(contact.Id).Where(r => r.Author == Self && r.State is DirectState.Pending or DirectState.Sent).ToList();
            foreach (var record in waiting.TakeLast(50)) Queue(contact.Id, record, DateTimeOffset.MinValue);
            if (waiting.Count > 0) continue;
            contact.HasPending = false;
            changed = true;
        }
        if (changed) Store.SaveContacts();
    }

    /// <summary>
    /// Abre (ou continua) a conversa com alguém que você vê numa sala. Na primeira vez, a chave que a pessoa anuncia
    /// fica guardada.
    /// </summary>
    public DirectContact Open(Guid peer, string name, string? announcedKey)
    {
        if (peer == Guid.Empty || peer == Self) throw new ArgumentException("Conversa com você mesmo.", nameof(peer));
        var contact = Store.Upsert(peer, RoomPacket.CleanName(name))!;
        if (announcedKey is not null) Notice(contact, announcedKey);
        Store.SaveContacts();
        ContactChanged?.Invoke(peer);
        return contact;
    }

    /// <summary>
    /// A chave que alguém com quem você conversa anuncia agora (numa sala ou servidor). Sem chave guardada, guarda; com
    /// outra guardada, marca a troca (a conversa mostra o aviso). Também atualiza o nome.
    /// </summary>
    public void NoticeKey(Guid peer, string name, string announcedKey)
    {
        if (Store.Find(peer) is not { } contact) return;
        var changed = Rename(contact, name);
        changed |= Notice(contact, announcedKey);
        if (!changed) return;
        Store.SaveContacts();
        ContactChanged?.Invoke(peer);
    }

    /// <summary>A pessoa apareceu (numa sala ou servidor): as mensagens esperando por ela vão já.</summary>
    public void PeerAppeared(Guid peer, DateTimeOffset now)
    {
        foreach (var ((owner, _), item) in _outbox)
        {
            if (owner != peer) continue;
            item.Attempts = 0;
            item.Next = now;
        }
    }

    /// <summary>Aceita a chave nova da pessoa (ela reinstalou o ScreenFlow ou trocou de PC, e você conferiu).</summary>
    public void AcceptKey(Guid peer, DateTimeOffset now)
    {
        if (Store.Find(peer) is not { ChangedKey: { } next } contact) return;
        contact.IdentityKey = next;
        contact.ChangedKey = null;
        Store.SaveContacts();
        PeerAppeared(peer, now);
        ContactChanged?.Invoke(peer);
    }

    /// <summary>Continua com a chave guardada (a nova some do aviso; se ela aparecer de novo, o aviso volta).</summary>
    public void KeepKey(Guid peer)
    {
        if (Store.Find(peer) is not { ChangedKey: not null } contact) return;
        contact.ChangedKey = null;
        Store.SaveContacts();
        ContactChanged?.Invoke(peer);
    }

    public void MarkRead(Guid peer)
    {
        if (Store.Find(peer) is not { Unread: > 0 } contact) return;
        contact.Unread = 0;
        Store.SaveContacts();
        ContactChanged?.Invoke(peer);
    }

    /// <summary>Apaga a conversa deste PC (as mensagens suas que não tinham chegado não vão mais).</summary>
    public void Remove(Guid peer)
    {
        foreach (var key in _outbox.Keys.Where(k => k.Peer == peer).ToList()) _outbox.Remove(key);
        _seen.Remove(peer);
        Store.Remove(peer);
        ContactChanged?.Invoke(peer);
    }

    /// <summary>Manda uma mensagem (vai já se a pessoa estiver numa sala; senão, quando ela aparecer). Null: nada para mandar.</summary>
    public DirectRecord? Send(Guid peer, DirectPayload payload, DateTimeOffset now)
    {
        if (Store.Find(peer) is not { } contact) return null;
        var text = RoomPacket.TrimToBytes(RoomPacket.CleanText(payload.Text), DirectCrypto.MaxTextBytes);
        if (text.Length == 0 && payload.Image is null) return null;
        var record = new DirectRecord(NewId(), Self, now, text, payload.Image);
        Store.Append(peer, record);
        contact.LastActivity = now;
        contact.LastPreview = Preview(record);
        contact.HasPending = true;
        Store.SaveContacts();
        MessageChanged?.Invoke(peer, record);
        ContactChanged?.Invoke(peer);
        var item = Queue(peer, record, now);
        TrySend(peer, item, now);
        return record;
    }

    /// <summary>A cada segundo: manda de novo o que ainda não foi confirmado (rápido no começo, depois a cada 30 s).</summary>
    public void Tick(DateTimeOffset now)
    {
        if (_outbox.Count == 0) return;
        var sends = new Dictionary<Guid, int>();
        foreach (var ((peer, _), item) in _outbox.OrderBy(entry => entry.Value.Record.Time).ToList())
        {
            if (item.Next > now) continue;
            var count = sends.GetValueOrDefault(peer);
            if (count >= SendsPerTick) continue;
            if (TrySend(peer, item, now)) sends[peer] = count + 1;
        }
    }

    /// <summary>
    /// Um pacote do chat privado que chegou de <paramref name="sender"/>, com a chave que ele anuncia na sala por onde
    /// veio. <paramref name="reply"/> manda a confirmação de volta pela mesma sala.
    /// </summary>
    public DirectReceive Receive(RoomPacketType type, Guid sender, string senderName, string? announcedKey, ReadOnlySpan<byte> body,
        DateTimeOffset now, Func<byte[], bool> reply)
    {
        if (sender == Guid.Empty || sender == Self || !DirectIdentity.IsValidPublicKey(announcedKey)) return DirectReceive.Ignored;
        if (!DirectCrypto.TryReadRecipient(body, out var recipient) || recipient != Self) return DirectReceive.Ignored;
        var contact = Store.Find(sender);
        if (contact?.IdentityKey is { } pinned && pinned != announcedKey)
        {
            // Outra chave com o mesmo id: pode ser a pessoa num PC novo, ou alguém se passando por ela. Não entra nada.
            if (contact.ChangedKey != announcedKey)
            {
                contact.ChangedKey = announcedKey;
                Store.SaveContacts();
                ContactChanged?.Invoke(sender);
            }
            return DirectReceive.KeyChanged;
        }
        var key = contact?.IdentityKey ?? announcedKey!;
        if (_identity.PairKey(key, Self, sender) is not { } pair) return DirectReceive.Ignored;
        return type switch
        {
            RoomPacketType.Direct => OnMessage(sender, senderName, key, pair, contact, body, now, reply),
            RoomPacketType.DirectAck => OnAck(sender, contact, pair, body, now),
            _ => DirectReceive.Ignored,
        };
    }

    private DirectReceive OnMessage(Guid sender, string senderName, string key, byte[] pair, DirectContact? contact, ReadOnlySpan<byte> body,
        DateTimeOffset now, Func<byte[], bool> reply)
    {
        if (!DirectCrypto.TryOpenMessage(pair, sender, Self, body, out var id, out var time, out var payload)) return DirectReceive.Ignored;
        if (contact is null)
        {
            // Primeira mensagem desta pessoa: a chave com que ela abriu fica guardada.
            contact = Store.Upsert(sender, RoomPacket.CleanName(senderName), evict: false);
            if (contact is null) return DirectReceive.Ignored; // conversas demais: não apaga nenhuma sua por causa de alguém novo
            contact.IdentityKey = key;
        }
        reply(RoomPacket.DirectAck(Self, DirectCrypto.SealAck(pair, Self, sender, id)));
        if (!Seen(sender).Add(id)) return DirectReceive.Duplicate;
        if (time > now + MaxFuture || time < now - MaxPast) time = now;
        var record = new DirectRecord(id, sender, time, payload.Text, payload.Image, DirectState.Received);
        Store.Append(sender, record);
        Rename(contact, senderName);
        if (record.Time >= contact.LastActivity || contact.LastPreview is null)
        {
            contact.LastActivity = record.Time;
            contact.LastPreview = Preview(record);
        }
        contact.Unread = Math.Min(contact.Unread + 1, 9999);
        Store.SaveContacts();
        MessageChanged?.Invoke(sender, record);
        ContactChanged?.Invoke(sender);
        return DirectReceive.Message;
    }

    private DirectReceive OnAck(Guid sender, DirectContact? contact, byte[] pair, ReadOnlySpan<byte> body, DateTimeOffset now)
    {
        if (contact is null || !DirectCrypto.TryOpenAck(pair, sender, Self, body, out var id)) return DirectReceive.Ignored;
        if (!_outbox.Remove((sender, id), out var item)) return DirectReceive.Duplicate;
        var delivered = item.Record with { State = DirectState.Delivered, DeliveredAt = now };
        Store.Append(sender, delivered);
        MessageChanged?.Invoke(sender, delivered);
        if (!_outbox.Keys.Any(k => k.Peer == sender))
        {
            contact.HasPending = false;
            Store.SaveContacts();
        }
        return DirectReceive.Delivered;
    }

    /// <summary>"📷 Imagem" ou o começo do texto (para a lista de conversas e os avisos).</summary>
    public static string Preview(DirectRecord record)
    {
        var text = record.Text.ReplaceLineEndings(" ");
        if (text.Length > PreviewLength) text = text[..PreviewLength].TrimEnd() + "…";
        return record.Image is null ? text : text.Length == 0 ? "📷 Imagem" : $"📷 {text}";
    }

    private bool TrySend(Guid peer, Outgoing item, DateTimeOffset now)
    {
        if (Store.Find(peer) is not { IdentityKey: { } key } || !_transport.CanReach(peer, key))
        {
            // A pessoa não está em nenhuma sala agora (ou só aparece com outra chave): espera ela voltar.
            item.Next = now + FastRetry;
            return false;
        }
        if (_identity.PairKey(key, Self, peer) is not { } pair)
        {
            item.Next = now + SlowRetry;
            return false;
        }
        var record = item.Record;
        var body = DirectCrypto.SealMessage(pair, Self, peer, record.Id, record.Time, new DirectPayload(record.Text, record.Image));
        var sent = _transport.Send(peer, key, RoomPacket.Direct(Self, body));
        item.Attempts++;
        item.Next = now + (item.Attempts < FastAttempts ? FastRetry : SlowRetry);
        // A confirmação pode ter chegado durante o envio: aí a mensagem já está entregue e não volta para "enviada".
        if (sent && record.State == DirectState.Pending && _outbox.TryGetValue((peer, record.Id), out var current) && current == item)
        {
            item.Record = record with { State = DirectState.Sent };
            Store.Append(peer, item.Record);
            MessageChanged?.Invoke(peer, item.Record);
        }
        return sent;
    }

    private Outgoing Queue(Guid peer, DirectRecord record, DateTimeOffset next)
    {
        if (_outbox.TryGetValue((peer, record.Id), out var existing)) return existing;
        if (_outbox.Count >= MaxOutbox)
        {
            // A mais antiga sai da fila (continua guardada como não enviada e volta da próxima vez que o ScreenFlow abrir).
            var oldest = _outbox.MinBy(entry => entry.Value.Record.Time).Key;
            _outbox.Remove(oldest);
        }
        var item = new Outgoing(record) { Next = next };
        _outbox[(peer, record.Id)] = item;
        return item;
    }

    private HashSet<ulong> Seen(Guid peer)
    {
        if (_seen.TryGetValue(peer, out var seen)) return seen;
        seen = Store.Load(peer).Where(r => r.Author == peer).Select(r => r.Id).ToHashSet();
        _seen[peer] = seen;
        return seen;
    }

    private static bool Rename(DirectContact contact, string name)
    {
        var clean = RoomPacket.CleanName(name);
        if (clean.Length == 0 || clean == contact.Name) return false;
        contact.Name = clean;
        return true;
    }

    private static bool Notice(DirectContact contact, string announcedKey)
    {
        if (!DirectIdentity.IsValidPublicKey(announcedKey)) return false;
        if (contact.IdentityKey is null)
        {
            contact.IdentityKey = announcedKey;
            contact.ChangedKey = null;
            return true;
        }
        if (contact.IdentityKey == announcedKey || contact.ChangedKey == announcedKey) return false;
        contact.ChangedKey = announcedKey;
        return true;
    }

    private static ulong NewId()
    {
        Span<byte> bytes = stackalloc byte[8];
        RandomNumberGenerator.Fill(bytes);
        return BitConverter.ToUInt64(bytes) | 1; // nunca 0
    }

    private sealed class Outgoing(DirectRecord record)
    {
        public DirectRecord Record { get; set; } = record;
        public int Attempts { get; set; }
        public DateTimeOffset Next { get; set; }
    }
}
