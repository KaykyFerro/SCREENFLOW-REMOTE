using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ScreenFlow.Core.Diagnostics;
using ScreenFlow.Core.Security;

namespace ScreenFlow.Core.Room;

/// <summary>(1.5.0) De onde saiu a chave de um servidor: do nome e da senha da rede do Radmin, ou de um convite.</summary>
public enum SpaceKeySource { Network = 0, Invite = 1 }

/// <summary>
/// (1.5.0) As chaves dos servidores por rede do Radmin. Quem digita o mesmo nome e a mesma senha da rede do Radmin chega
/// na mesma chave (PBKDF2-SHA256), sem ninguém precisar mandar nada: todo mundo daquela rede cai no mesmo servidor, e
/// quem está em outra rede (mesmo vendo os mesmos PCs pelo adaptador do Radmin) não consegue abrir nada dele.
/// </summary>
public static class SpaceKeys
{
    /// <summary>Rodadas do PBKDF2 (a chave sai em alguns décimos de segundo, uma vez só, ao criar o servidor).</summary>
    public const int Iterations = 200_000;
    public const int MaxNetworkNameLength = 64;
    private const string SaltPrefix = "ScreenFlow servidor por rede v1|";

    /// <summary>
    /// O nome da rede como entra na chave: sem espaços sobrando, sem caracteres invisíveis e em minúsculas ("Amigos  CS"
    /// e "amigos cs" são a mesma rede). Vazio quando não sobra nada.
    /// </summary>
    public static string NormalizeNetworkName(string? name) => CleanNetworkName(name).ToLowerInvariant();

    /// <summary>O nome da rede como aparece (do jeito que foi digitado, só sem espaços sobrando e caracteres invisíveis).</summary>
    public static string CleanNetworkName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return string.Empty;
        var builder = new StringBuilder(name.Length);
        foreach (var c in name.Normalize(NormalizationForm.FormC))
        {
            if (char.IsWhiteSpace(c)) { builder.Append(' '); continue; }
            if (char.IsControl(c) || char.GetUnicodeCategory(c) is UnicodeCategory.Format) continue;
            builder.Append(c);
        }
        var clean = string.Join(' ', builder.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
        if (clean.Length <= MaxNetworkNameLength) return clean;
        var cut = MaxNetworkNameLength;
        if (char.IsHighSurrogate(clean[cut - 1])) cut--;
        return clean[..cut].TrimEnd();
    }

    /// <summary>
    /// A chave (32 bytes) do servidor de uma rede do Radmin. Os espaços no começo e no fim da senha não contam (é fácil
    /// copiar um a mais). Lança se o nome da rede estiver vazio.
    /// </summary>
    public static byte[] FromNetwork(string? networkName, string? password)
    {
        var name = NormalizeNetworkName(networkName);
        if (name.Length == 0) throw new ArgumentException("Digite o nome da rede do Radmin.", nameof(networkName));
        var salt = Encoding.UTF8.GetBytes(SaltPrefix + name);
        return Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes((password ?? string.Empty).Trim()), salt, Iterations, HashAlgorithmName.SHA256,
            RoomKeys.SecretBytes);
    }

    /// <summary>A etiqueta pública da chave em hexa: dois servidores com a mesma etiqueta são o mesmo servidor.</summary>
    public static string TagText(ReadOnlySpan<byte> secret) => Convert.ToHexString(RoomKeys.Tag(secret)).ToLowerInvariant();
}

/// <summary>
/// (1.5.0) Convite de servidor: o segredo do servidor e o nome sugerido, num código "SF2-" para copiar e colar (com
/// verificador, como o da sala privada). Serve para servidores que não dependem do nome e da senha da rede.
/// </summary>
public sealed record SpaceInvite(byte[] Secret, string Name)
{
    public const string Prefix = "SF2-";
    private const int MaxNameBytes = 60;

    public static SpaceInvite Create(string name) => new(RandomNumberGenerator.GetBytes(RoomKeys.SecretBytes), RoomSpace.CleanName(name));

    public string ToCode()
    {
        var name = Encoding.UTF8.GetBytes(TrimToBytes(RoomSpace.CleanName(Name), MaxNameBytes));
        var raw = new byte[RoomKeys.SecretBytes + 1 + name.Length + 2];
        Secret.AsSpan(0, RoomKeys.SecretBytes).CopyTo(raw);
        raw[RoomKeys.SecretBytes] = (byte)name.Length;
        name.CopyTo(raw, RoomKeys.SecretBytes + 1);
        return RoomInvite.EncodeCode(Prefix, raw);
    }

    public static bool TryParse(string? code, out SpaceInvite invite)
    {
        invite = null!;
        var raw = RoomInvite.DecodeCode(Prefix, code);
        if (raw is null || raw.Length < RoomKeys.SecretBytes + 3) return false;
        var length = raw[RoomKeys.SecretBytes];
        if (length > MaxNameBytes || raw.Length != RoomKeys.SecretBytes + 1 + length + 2) return false;
        string name;
        try { name = new UTF8Encoding(false, true).GetString(raw, RoomKeys.SecretBytes + 1, length); }
        catch (DecoderFallbackException) { return false; }
        invite = new SpaceInvite(raw[..RoomKeys.SecretBytes], RoomSpace.CleanName(name));
        return true;
    }

    private static string TrimToBytes(string text, int maxBytes)
    {
        while (text.Length > 0 && Encoding.UTF8.GetByteCount(text) > maxBytes)
            text = text[..(char.IsLowSurrogate(text[^1]) && text.Length > 1 ? text.Length - 2 : text.Length - 1)];
        return text;
    }
}

/// <summary>
/// (1.5.0) Um canal de um servidor. O <see cref="Id"/> é o que vai nos pacotes (no chat e na voz), então mudar o nome não
/// mexe em nada além do nome que aparece. <see cref="Version"/> é a hora da última mudança (em ms): entre duas versões do
/// mesmo canal vale a mais nova. Um canal apagado fica como <see cref="Deleted"/> por um tempo, para não voltar.
/// </summary>
public sealed record SpaceChannel(string Id, RoomChannelKind Kind, string Name, long Version, Guid Author, bool Deleted = false)
{
    [JsonIgnore]
    public bool IsDefault => Id == SpaceChannelTable.DefaultId;

    /// <summary>O canal como os pacotes conhecem: o id no lugar do nome.</summary>
    [JsonIgnore]
    public RoomChannel Channel => new(Kind, Id);
}

/// <summary>
/// (1.5.0) Os canais de um servidor, iguais em todos os PCs: cada PC guarda a lista, manda para quem chega e junta o que
/// recebe (vale a mudança mais nova de cada canal). Assim os canais continuam lá mesmo com quem criou desligado, e
/// qualquer pessoa do servidor cria, renomeia e apaga (como no servidor ScreenFlow da 1.4.1). O chat "geral" e a voz
/// "Geral" existem sempre (id <see cref="DefaultId"/>): só o nome deles muda.
/// </summary>
public sealed class SpaceChannelTable
{
    public const string DefaultId = "geral";
    public const int IdLength = 12;
    public const int MaxPerKind = 50;
    public const int MaxEntries = 160;
    public static readonly TimeSpan TombstoneLife = TimeSpan.FromDays(30);

    /// <summary>Mudanças com hora no futuro além disto são recusadas (senão ninguém conseguiria mudar o canal depois).</summary>
    public static readonly TimeSpan MaxClockSkew = TimeSpan.FromMinutes(10);

    private static readonly StringComparer Names = StringComparer.Create(CultureInfo.GetCultureInfo("pt-BR"), ignoreCase: true);
    private readonly object _gate = new();
    private readonly Dictionary<(RoomChannelKind, string), SpaceChannel> _entries = [];

    public SpaceChannelTable(IEnumerable<SpaceChannel>? entries = null, DateTimeOffset? now = null)
    {
        var at = now ?? DateTimeOffset.UtcNow;
        foreach (var entry in entries ?? []) Merge(entry, at);
    }

    public static bool IsValidId(string? id)
        => id == DefaultId || (id is { Length: IdLength } && id.All(c => c is (>= '0' and <= '9') or (>= 'a' and <= 'f')));

    public static string NewId() => Convert.ToHexString(RandomNumberGenerator.GetBytes(IdLength / 2)).ToLowerInvariant();

    /// <summary>O nome dos canais padrão quando ninguém mudou: "geral" e "Geral".</summary>
    public static string DefaultName(RoomChannelKind kind) => kind == RoomChannelKind.Text ? RoomChannels.DefaultTextName : RoomChannels.DefaultVoiceName;

    /// <summary>O id do canal de um pacote: null (versões e mensagens sem canal) e o "geral" viram <see cref="DefaultId"/>.</summary>
    public static string IdOf(RoomChannelKind kind, string? name)
    {
        if (name is null) return DefaultId;
        if (kind == RoomChannelKind.Text ? RoomChannels.IsDefaultText(name) : RoomChannels.IsDefaultVoice(name)) return DefaultId;
        return name;
    }

    /// <summary>O nome que aparece de um canal (null se o canal não existe ou foi apagado).</summary>
    public string? NameOf(RoomChannelKind kind, string? id)
    {
        id = IdOf(kind, id);
        lock (_gate)
        {
            if (_entries.TryGetValue((kind, id), out var entry)) return entry.Deleted ? null : entry.Name;
        }
        return id == DefaultId ? DefaultName(kind) : null;
    }

    public SpaceChannel? Find(RoomChannelKind kind, string? id)
    {
        lock (_gate) return _entries.GetValueOrDefault((kind, IdOf(kind, id)));
    }

    /// <summary>Os canais de um tipo que aparecem: o padrão primeiro, depois os outros em ordem alfabética.</summary>
    public IReadOnlyList<SpaceChannel> Visible(RoomChannelKind kind)
    {
        lock (_gate)
        {
            var list = _entries.Values.Where(e => e.Kind == kind && !e.Deleted && !e.IsDefault).OrderBy(e => e.Name, Names).ThenBy(e => e.Id, StringComparer.Ordinal).ToList();
            var fallback = new SpaceChannel(DefaultId, kind, DefaultName(kind), 0, Guid.Empty);
            list.Insert(0, _entries.GetValueOrDefault((kind, DefaultId)) ?? fallback);
            return list;
        }
    }

    /// <summary>Tudo, inclusive os apagados (para guardar e mandar para os outros).</summary>
    public IReadOnlyList<SpaceChannel> Snapshot()
    {
        lock (_gate) return _entries.Values.OrderBy(e => e.Kind).ThenBy(e => e.Id, StringComparer.Ordinal).ToList();
    }

    public int Count
    {
        get { lock (_gate) return _entries.Count; }
    }

    /// <summary>Cria um canal. Null e o motivo quando não deu (nome vazio, repetido ou canais demais).</summary>
    public SpaceChannel? Create(RoomChannelKind kind, string? name, Guid author, DateTimeOffset now, out string? problem)
    {
        problem = null;
        if (!Enum.IsDefined(kind)) { problem = "Tipo de canal inválido."; return null; }
        var clean = RoomChannels.CleanName(kind, name);
        if (clean.Length == 0 || RoomChannels.Key(clean).Length == 0) { problem = "Escolha um nome com letras ou números."; return null; }
        lock (_gate)
        {
            if (Visible(kind).Any(c => RoomChannels.SameName(c.Name, clean)))
            {
                problem = "Já existe um canal com esse nome.";
                return null;
            }
            if (_entries.Values.Count(e => e.Kind == kind && !e.Deleted) >= MaxPerKind)
            {
                problem = $"O servidor já tem {MaxPerKind} canais desse tipo. Apague um para criar outro.";
                return null;
            }
            MakeRoom(now);
            if (_entries.Count >= MaxEntries)
            {
                problem = "O servidor tem canais demais. Apague alguns para criar outro.";
                return null;
            }
            string id;
            do id = NewId(); while (_entries.ContainsKey((kind, id)));
            var entry = new SpaceChannel(id, kind, clean, now.ToUnixTimeMilliseconds(), author);
            _entries[(kind, id)] = entry;
            return entry;
        }
    }

    /// <summary>Muda o nome que aparece (o id continua o mesmo). Null e o motivo quando não deu.</summary>
    public SpaceChannel? Rename(RoomChannelKind kind, string id, string? name, Guid author, DateTimeOffset now, out string? problem)
    {
        problem = null;
        var clean = RoomChannels.CleanName(kind, name);
        if (clean.Length == 0 || RoomChannels.Key(clean).Length == 0) { problem = "Escolha um nome com letras ou números."; return null; }
        id = IdOf(kind, id);
        lock (_gate)
        {
            var current = _entries.GetValueOrDefault((kind, id));
            if ((current is null && id != DefaultId) || current is { Deleted: true })
            {
                problem = "Esse canal não existe mais.";
                return null;
            }
            if (Visible(kind).Any(c => c.Id != id && RoomChannels.SameName(c.Name, clean)))
            {
                problem = "Já existe um canal com esse nome.";
                return null;
            }
            var version = Math.Max(now.ToUnixTimeMilliseconds(), (current?.Version ?? 0) + 1);
            var entry = new SpaceChannel(id, kind, clean, version, author);
            _entries[(kind, id)] = entry;
            return entry;
        }
    }

    /// <summary>Apaga um canal (o "geral" e a "Geral" não). Null e o motivo quando não deu.</summary>
    public SpaceChannel? Delete(RoomChannelKind kind, string id, Guid author, DateTimeOffset now, out string? problem)
    {
        problem = null;
        id = IdOf(kind, id);
        if (id == DefaultId) { problem = "O #geral e a voz Geral não podem ser apagados."; return null; }
        lock (_gate)
        {
            if (_entries.GetValueOrDefault((kind, id)) is not { Deleted: false } current)
            {
                problem = "Esse canal não existe mais.";
                return null;
            }
            var entry = current with { Version = Math.Max(now.ToUnixTimeMilliseconds(), current.Version + 1), Author = author, Deleted = true };
            _entries[(kind, id)] = entry;
            return entry;
        }
    }

    /// <summary>
    /// Junta a versão de outro PC: fica a mais nova (no empate, a do autor com o maior id, igual em todo PC). Recusa
    /// o que não é válido. true quando algo mudou aqui.
    /// </summary>
    public bool Merge(SpaceChannel incoming, DateTimeOffset now)
    {
        if (!IsValidId(incoming.Id) || !Enum.IsDefined(incoming.Kind) || incoming.Version <= 0) return false;
        if (incoming.Version > (now + MaxClockSkew).ToUnixTimeMilliseconds()) return false;
        if (incoming.IsDefault && incoming.Deleted) return false;
        var name = RoomChannels.CleanName(incoming.Kind, incoming.Name);
        if (name.Length == 0) return false;
        var clean = incoming with { Name = name };
        if (now - DateTimeOffset.FromUnixTimeMilliseconds(clean.Version) > TombstoneLife && clean.Deleted) return false;
        lock (_gate)
        {
            var key = (clean.Kind, clean.Id);
            if (_entries.TryGetValue(key, out var current))
            {
                if (current == clean) return false;
                var newer = clean.Version > current.Version || (clean.Version == current.Version && clean.Author.CompareTo(current.Author) > 0);
                if (!newer) return false;
            }
            else
            {
                if (!clean.Deleted && _entries.Values.Count(e => e.Kind == clean.Kind && !e.Deleted) >= MaxPerKind) return false;
                MakeRoom(now);
                if (_entries.Count >= MaxEntries) return false;
            }
            _entries[key] = clean;
            return true;
        }
    }

    /// <summary>Tira os canais apagados há mais de <see cref="TombstoneLife"/>. Devolve quantos saíram.</summary>
    public int Expire(DateTimeOffset now)
    {
        lock (_gate)
        {
            var old = _entries.Where(e => e.Value.Deleted && now - DateTimeOffset.FromUnixTimeMilliseconds(e.Value.Version) > TombstoneLife).Select(e => e.Key).ToList();
            foreach (var key in old) _entries.Remove(key);
            return old.Count;
        }
    }

    /// <summary>Com a lista cheia, os apagados mais antigos saem primeiro.</summary>
    private void MakeRoom(DateTimeOffset now)
    {
        Expire(now);
        while (_entries.Count >= MaxEntries)
        {
            var oldest = _entries.Values.Where(e => e.Deleted).OrderBy(e => e.Version).FirstOrDefault();
            if (oldest is null) return;
            _entries.Remove((oldest.Kind, oldest.Id));
        }
    }
}

/// <summary>
/// (1.5.0) Um servidor, como no Discord, ligado a uma rede do Radmin: o nome que aparece (só para você), a rede, a chave
/// (guardada cifrada pelo Windows), por qual adaptador falar, IPs para chamar direto, os canais e onde você parou.
/// </summary>
public sealed class RoomSpace
{
    public const int MaxNameLength = 40;
    public const int MaxManualPeers = 32;

    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>O nome que aparece na lista de servidores (renomear muda só aqui, para você).</summary>
    public string Name { get; set; } = "Servidor";

    /// <summary>O nome da rede do Radmin (quando a chave saiu do nome e da senha dela).</summary>
    public string? NetworkName { get; set; }

    public SpaceKeySource KeySource { get; set; }

    /// <summary>A chave do servidor (32 bytes). Null quando não deu para abrir a guardada (outro PC ou outra conta).</summary>
    [JsonIgnore]
    public byte[]? Secret { get; set; }

    /// <summary>A chave do histórico guardado neste PC (32 bytes, criada na primeira vez).</summary>
    [JsonIgnore]
    public byte[]? HistoryKey { get; set; }

    /// <summary>Por qual adaptador falar: null = os do Radmin VPN; "*" = Radmin e rede local; senão o nome do adaptador.</summary>
    public string? Adapter { get; set; }

    /// <summary>IPs (com porta, se quiser) que recebem a presença direto, quando o broadcast não chega até eles.</summary>
    public List<string> ManualPeers { get; set; } = [];

    /// <summary>Conectado (volta conectado quando o ScreenFlow abre). Desconectado, o servidor fica na lista parado.</summary>
    public bool Connected { get; set; } = true;

    public DateTimeOffset Created { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>O canal de texto e o de voz em que você estava (ids).</summary>
    public string? LastTextChannel { get; set; }
    public string? LastVoiceChannel { get; set; }

    /// <summary>Os canais como estavam da última vez (inclusive os apagados recentemente).</summary>
    public List<SpaceChannel> Channels { get; set; } = [];

    /// <summary>A cor do ícone na lista de servidores (0 a 5).</summary>
    public int Color { get; set; }

    [JsonInclude, JsonPropertyName("SecretProtected")]
    private string? SecretProtected
    {
        get => Secret is null ? null : SecretProtection.Protect(Convert.ToBase64String(Secret));
        set => Secret = Decode(SecretProtection.Unprotect(value)) ?? Secret;
    }

    [JsonInclude, JsonPropertyName("HistoryKeyProtected")]
    private string? HistoryKeyProtected
    {
        get => HistoryKey is null ? null : SecretProtection.Protect(Convert.ToBase64String(HistoryKey));
        set => HistoryKey = Decode(SecretProtection.Unprotect(value)) ?? HistoryKey;
    }

    private static byte[]? Decode(string? base64)
    {
        if (base64 is null) return null;
        try
        {
            var bytes = Convert.FromBase64String(base64);
            return bytes.Length == RoomKeys.SecretBytes ? bytes : null;
        }
        catch (FormatException) { return null; }
    }

    /// <summary>A etiqueta da chave (igual para todo mundo do mesmo servidor). Vazia sem chave.</summary>
    [JsonIgnore]
    public string Tag => Secret is null ? string.Empty : SpaceKeys.TagText(Secret);

    /// <summary>O resumo da chave ("7F3A-91C2"), para as pessoas conferirem que estão no mesmo servidor.</summary>
    [JsonIgnore]
    public string? Fingerprint => Secret is null ? null : RoomKeys.Fingerprint(Secret, 0);

    /// <summary>O convite deste servidor (qualquer um com ele entra, mesmo fora da rede do Radmin).</summary>
    public string? InviteCode() => Secret is null ? null : new SpaceInvite(Secret, Name).ToCode();

    /// <summary>Nome do servidor: sem caracteres de controle, sem espaços sobrando, até 40 letras. "Servidor" se vazio.</summary>
    public static string CleanName(string? name)
    {
        var clean = RoomPacket.CleanName(name);
        if (clean.Length > MaxNameLength) clean = clean[..MaxNameLength].TrimEnd();
        return clean.Length == 0 ? "Servidor" : clean;
    }

    /// <summary>Um IP para chamar direto ("26.1.2.3" ou "26.1.2.3:47802"), limpo. Null se não for um IPv4 válido.</summary>
    public static string? CleanPeer(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var value = text.Trim();
        var colon = value.LastIndexOf(':');
        var host = colon > 0 ? value[..colon] : value;
        if (!System.Net.IPAddress.TryParse(host, out var address) || address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) return null;
        if (colon <= 0) return address.ToString();
        return int.TryParse(value[(colon + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var port) && port is > 0 and <= 65535
            ? $"{address}:{port}"
            : null;
    }

    public void Normalize()
    {
        if (Id == Guid.Empty) Id = Guid.NewGuid();
        Name = CleanName(Name);
        NetworkName = SpaceKeys.CleanNetworkName(NetworkName) is { Length: > 0 } network ? network : null;
        if (!Enum.IsDefined(KeySource)) KeySource = SpaceKeySource.Invite;
        Adapter = string.IsNullOrWhiteSpace(Adapter) ? null : Adapter.Trim();
        ManualPeers = (ManualPeers ?? []).Select(CleanPeer).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).Take(MaxManualPeers).ToList();
        Color = Math.Abs(Color) % 6;
        if (LastTextChannel is not null && !SpaceChannelTable.IsValidId(LastTextChannel)) LastTextChannel = null;
        if (LastVoiceChannel is not null && !SpaceChannelTable.IsValidId(LastVoiceChannel)) LastVoiceChannel = null;
        Channels = new SpaceChannelTable(Channels?.Where(c => c is not null)).Snapshot().ToList();
    }
}

/// <summary>
/// (1.5.0) A lista de servidores deste PC, em "servers.json" ao lado das configurações. As chaves vão cifradas pelo
/// Windows (DPAPI): o arquivo copiado para outro PC não abre os servidores.
/// </summary>
public sealed class SpaceStore
{
    public const int MaxSpaces = 20;
    private const int CurrentVersion = 1;

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _path;
    private readonly object _gate = new();
    private List<RoomSpace> _spaces = [];

    public SpaceStore(string? path = null)
    {
        _path = path ?? Path.Combine(AppInfo.DataDirectory, "servers.json");
        Load();
    }

    public string FilePath => _path;

    public IReadOnlyList<RoomSpace> Spaces
    {
        get { lock (_gate) return _spaces.ToList(); }
    }

    public RoomSpace? Find(Guid id)
    {
        lock (_gate) return _spaces.FirstOrDefault(s => s.Id == id);
    }

    /// <summary>Um servidor com a mesma chave (o mesmo servidor, mesmo que com outro nome).</summary>
    public RoomSpace? FindByTag(string tag)
    {
        if (tag.Length == 0) return null;
        lock (_gate) return _spaces.FirstOrDefault(s => s.Tag == tag);
    }

    /// <summary>Adiciona (e guarda). O motivo quando não deu: servidor repetido, sem chave ou servidores demais.</summary>
    public string? Add(RoomSpace space)
    {
        space.Normalize();
        if (space.Secret is null) return "O servidor ficou sem chave.";
        lock (_gate)
        {
            if (_spaces.Count >= MaxSpaces) return $"Dá para ter até {MaxSpaces} servidores. Remova um para adicionar outro.";
            if (_spaces.FirstOrDefault(s => s.Tag == space.Tag) is { } same) return $"Você já tem esse servidor (\"{same.Name}\").";
            _spaces.Add(space);
        }
        Save();
        return null;
    }

    public bool Remove(Guid id)
    {
        bool removed;
        lock (_gate) removed = _spaces.RemoveAll(s => s.Id == id) > 0;
        if (removed) Save();
        return removed;
    }

    /// <summary>Muda a posição de um servidor na lista.</summary>
    public void Move(Guid id, int index)
    {
        lock (_gate)
        {
            var at = _spaces.FindIndex(s => s.Id == id);
            if (at < 0) return;
            var space = _spaces[at];
            _spaces.RemoveAt(at);
            _spaces.Insert(Math.Clamp(index, 0, _spaces.Count), space);
        }
        Save();
    }

    public void Save()
    {
        lock (_gate)
        {
            foreach (var space in _spaces) space.Normalize();
            var file = new SpaceFile { Version = CurrentVersion, Servers = _spaces };
            var temp = _path + ".tmp";
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(temp, JsonSerializer.Serialize(file, Options));
            File.Move(temp, _path, overwrite: true);
        }
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_path)) return;
            var file = JsonSerializer.Deserialize<SpaceFile>(File.ReadAllText(_path), Options);
            var list = new List<RoomSpace>();
            foreach (var space in file?.Servers ?? [])
            {
                if (space is null) continue;
                space.Normalize();
                if (list.Any(s => s.Id == space.Id) || (space.Tag.Length > 0 && list.Any(s => s.Tag == space.Tag))) continue;
                list.Add(space);
                if (list.Count >= MaxSpaces) break;
            }
            _spaces = list;
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // Não perde o arquivo: guarda uma cópia antes de começar com a lista vazia.
            try { File.Copy(_path, $"{_path}.invalido-{DateTime.Now:yyyyMMdd-HHmmss}", overwrite: true); } catch { }
            Log.Warn($"A lista de servidores não abriu e começou vazia: {exception.Message}");
            _spaces = [];
        }
    }

    private sealed class SpaceFile
    {
        public int Version { get; set; }
        public List<RoomSpace> Servers { get; set; } = [];
    }
}
