using System.Text.Json;
using ScreenFlow.Core.Diagnostics;
using ScreenFlow.Core.Security;

namespace ScreenFlow.Core.Room;

/// <summary>Uma pessoa com quem você tem (ou teve) conversa privada, como fica guardada neste PC.</summary>
public sealed class DirectContact
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    /// <summary>A última foto que a pessoa mostrou (id do arquivo).</summary>
    public string? Avatar { get; set; }
    /// <summary>A chave do chat privado da pessoa, a primeira vista (as próximas precisam ser iguais).</summary>
    public string? IdentityKey { get; set; }
    /// <summary>Uma chave diferente que apareceu com o mesmo id: só vale depois que você aceitar.</summary>
    public string? ChangedKey { get; set; }
    public DateTimeOffset LastActivity { get; set; }
    public int Unread { get; set; }
    /// <summary>O começo da última mensagem (para a lista de conversas).</summary>
    public string? LastPreview { get; set; }
    /// <summary>Tem mensagem sua que ainda não chegou (ao abrir, a fila de envio é refeita só com estas conversas).</summary>
    public bool HasPending { get; set; }
}

/// <summary>
/// (1.5.0) As conversas privadas guardadas neste PC: a lista de pessoas (contacts.dat) e um histórico por pessoa
/// ({id}.history), tudo cifrado (AES-256-GCM) com uma chave que fica nas configurações, protegida pelo Windows. As
/// mudanças de estado de uma mensagem (enviada, entregue) são acrescentadas no fim; ao abrir, vale a última.
/// </summary>
public sealed class DirectStore
{
    public const int MaxContacts = 500;
    public const int Keep = 1000;
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = false };
    private readonly string _folder;
    private readonly byte[] _key;
    private readonly EncryptedLog _contactsLog;
    private readonly Dictionary<Guid, DirectContact> _contacts = [];
    private readonly Dictionary<Guid, EncryptedHistory<DirectRecord>> _histories = [];
    private readonly object _gate = new();

    public DirectStore(string folder, byte[] key)
    {
        if (key.Length != 32) throw new ArgumentException("A chave das conversas precisa ter 32 bytes.", nameof(key));
        _folder = folder;
        _key = key.ToArray();
        _contactsLog = new EncryptedLog(Path.Combine(folder, "contacts.dat"), _key);
        LoadContacts();
    }

    public string Folder => _folder;

    /// <summary>As pessoas, da conversa mais recente para a mais antiga.</summary>
    public IReadOnlyList<DirectContact> Contacts
    {
        get { lock (_gate) return _contacts.Values.OrderByDescending(c => c.LastActivity).ToList(); }
    }

    public DirectContact? Find(Guid id)
    {
        lock (_gate) return _contacts.GetValueOrDefault(id);
    }

    /// <summary>Mensagens não lidas, somando todas as conversas.</summary>
    public int Unread
    {
        get { lock (_gate) return _contacts.Values.Sum(c => c.Unread); }
    }

    /// <summary>
    /// A pessoa (cria se ainda não tem conversa com ela). Com <see cref="MaxContacts"/> conversas, só abre uma nova com
    /// <paramref name="evict"/> (você abriu a conversa: sai a mais antiga); uma mensagem de alguém novo não apaga nenhuma
    /// conversa sua (null).
    /// </summary>
    public DirectContact? Upsert(Guid id, string name, bool evict = true)
    {
        lock (_gate)
        {
            if (!_contacts.TryGetValue(id, out var contact))
            {
                if (_contacts.Count >= MaxContacts)
                {
                    if (!evict) return null;
                    var oldest = _contacts.Values.OrderBy(c => c.LastActivity).First();
                    RemoveLocked(oldest.Id);
                }
                contact = new DirectContact { Id = id, Name = name, LastActivity = DateTimeOffset.UtcNow };
                _contacts[id] = contact;
            }
            if (name.Length > 0) contact.Name = name;
            return contact;
        }
    }

    /// <summary>As últimas <paramref name="max"/> mensagens da conversa, cada uma no estado mais novo.</summary>
    public List<DirectRecord> Load(Guid peer, int max = Keep)
    {
        var history = History(peer);
        var latest = new Dictionary<(Guid, ulong), DirectRecord>();
        var order = new List<(Guid, ulong)>();
        foreach (var record in history.Load(int.MaxValue))
        {
            var key = (record.Author, record.Id);
            if (!latest.ContainsKey(key)) order.Add(key);
            latest[key] = record;
        }
        return order.Select(k => latest[k]).OrderBy(r => r.Time).TakeLast(max).ToList();
    }

    /// <summary>Guarda uma mensagem (ou o estado novo dela).</summary>
    public void Append(Guid peer, DirectRecord record) => History(peer).Append(record);

    /// <summary>Apaga a conversa deste PC (a pessoa continua com a dela).</summary>
    public void Remove(Guid peer)
    {
        lock (_gate) RemoveLocked(peer);
        SaveContacts();
    }

    public void SaveContacts()
    {
        List<DirectContact> list;
        lock (_gate) list = _contacts.Values.ToList();
        try { _contactsLog.Rewrite([JsonSerializer.SerializeToUtf8Bytes(list, Options)]); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"A lista de conversas não foi gravada: {exception.Message}");
        }
    }

    private void RemoveLocked(Guid peer)
    {
        _contacts.Remove(peer);
        if (_histories.Remove(peer, out var history)) history.Delete();
        else
        {
            try { File.Delete(HistoryPath(peer)); }
            catch (IOException) { }
        }
    }

    private EncryptedHistory<DirectRecord> History(Guid peer)
    {
        lock (_gate)
        {
            if (!_histories.TryGetValue(peer, out var history))
            {
                history = new EncryptedHistory<DirectRecord>(HistoryPath(peer), _key, keep: Keep * 2, maxBytes: 4 * 1024 * 1024);
                _histories[peer] = history;
            }
            return history;
        }
    }

    private string HistoryPath(Guid peer) => Path.Combine(_folder, $"{peer:N}.history");

    private void LoadContacts()
    {
        try
        {
            var records = _contactsLog.ReadAll(1);
            if (records.Count == 0) return;
            var list = JsonSerializer.Deserialize<List<DirectContact>>(records[0], Options) ?? [];
            foreach (var contact in list)
            {
                if (contact.Id == Guid.Empty || _contacts.ContainsKey(contact.Id)) continue;
                contact.Name = RoomPacket.CleanName(contact.Name);
                if (contact.IdentityKey is not null && !DirectIdentity.IsValidPublicKey(contact.IdentityKey)) contact.IdentityKey = null;
                if (contact.ChangedKey is not null && !DirectIdentity.IsValidPublicKey(contact.ChangedKey)) contact.ChangedKey = null;
                contact.Unread = Math.Max(0, contact.Unread);
                _contacts[contact.Id] = contact;
                if (_contacts.Count >= MaxContacts) break;
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            Log.Warn($"A lista de conversas não abriu: {exception.Message}");
        }
    }
}
