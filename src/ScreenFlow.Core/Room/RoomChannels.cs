using System.Collections;
using System.Globalization;
using System.Text;
using System.Text.Json.Serialization;

namespace ScreenFlow.Core.Room;

/// <summary>Canal de texto (# geral) ou de voz (🔊 Geral) da sala da rede (1.3.0).</summary>
public enum RoomChannelKind : byte { Text = 1, Voice = 2 }

/// <summary>
/// Um canal da sala, como num servidor do Discord. Dois canais são o mesmo quando têm o mesmo tipo e o mesmo nome sem
/// acentos nem maiúsculas (<see cref="Key"/>): "Música" e "musica" são um canal só.
/// </summary>
public sealed record RoomChannel(RoomChannelKind Kind, string Name)
{
    [JsonIgnore]
    public string Key => RoomChannels.Key(Name);

    [JsonIgnore]
    public bool IsDefault => RoomChannels.IsDefault(Kind, Name);

    public bool Same(RoomChannel? other) => other is not null && other.Kind == Kind && other.Key == Key;
}

/// <summary>
/// Os canais da sala. Todo mundo tem os mesmos canais padrão; quem cria um canal anuncia na presença e ele aparece para
/// todo mundo enquanto essa pessoa está na sala (ou enquanto tem alguém na voz dele). As versões 1.1 e 1.2 só conhecem
/// o chat "geral" e a voz "Geral": continuam conversando com quem está neles.
/// </summary>
public static class RoomChannels
{
    public const int MaxNameLength = 24;

    /// <summary>Canais que cada PC pode criar (a presença tem espaço limitado).</summary>
    public const int MaxPerPerson = 6;

    /// <summary>Canais mostrados de cada tipo, no máximo (contra uma enxurrada de canais de alguém).</summary>
    public const int MaxListed = 40;

    public const string DefaultTextName = "geral";
    public const string DefaultVoiceName = "Geral";

    public static readonly RoomChannel DefaultText = new(RoomChannelKind.Text, DefaultTextName);
    public static readonly RoomChannel DefaultVoice = new(RoomChannelKind.Voice, DefaultVoiceName);

    /// <summary>Os canais que toda sala tem.</summary>
    public static readonly IReadOnlyList<RoomChannel> Defaults =
    [
        DefaultText,
        new(RoomChannelKind.Text, "música"),
        new(RoomChannelKind.Text, "prints-e-clipes"),
        DefaultVoice,
        new(RoomChannelKind.Voice, "Jogando"),
        new(RoomChannelKind.Voice, "Música"),
    ];

    /// <summary>
    /// Nome limpo: sem caracteres de controle, até <see cref="MaxNameLength"/> letras. Canais de texto ficam como no
    /// Discord, em minúsculas e com hífen no lugar dos espaços ("Pedidos de música" vira "pedidos-de-música").
    /// Vazio quando não sobra nada.
    /// </summary>
    public static string CleanName(RoomChannelKind kind, string? name)
    {
        var clean = RoomPacket.CleanName(name);
        if (kind == RoomChannelKind.Text)
        {
            var builder = new StringBuilder(clean.Length);
            foreach (var c in clean.ToLower(CultureInfo.GetCultureInfo("pt-BR")))
            {
                if (char.IsLetterOrDigit(c) || char.IsSurrogate(c) || c is '_' || char.GetUnicodeCategory(c) is UnicodeCategory.OtherSymbol)
                    builder.Append(c);
                else if (builder.Length > 0 && builder[^1] != '-') builder.Append('-');
            }
            clean = builder.ToString().Trim('-');
        }
        if (clean.Length > MaxNameLength)
        {
            var cut = MaxNameLength;
            if (char.IsHighSurrogate(clean[cut - 1])) cut--;
            clean = clean[..cut].TrimEnd(' ', '-');
        }
        return clean;
    }

    /// <summary>Como o canal é comparado: minúsculas, sem acentos, só letras e números ("Música!" e "musica" dão "musica").</summary>
    public static string Key(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return string.Empty;
        var decomposed = name.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        foreach (var c in decomposed)
        {
            var category = char.GetUnicodeCategory(c);
            if (category == UnicodeCategory.NonSpacingMark) continue;
            if (char.IsLetterOrDigit(c)) builder.Append(char.ToLowerInvariant(c));
            else if (char.IsSurrogate(c) || category == UnicodeCategory.OtherSymbol) builder.Append(c);
            else if (builder.Length > 0 && builder[^1] != '-') builder.Append('-');
        }
        return builder.ToString().Trim('-');
    }

    public static bool IsDefault(RoomChannelKind kind, string? name)
    {
        var key = Key(name);
        return Defaults.Any(d => d.Kind == kind && d.Key == key);
    }

    /// <summary>O chat sem canal (versões antigas) é o "geral".</summary>
    public static bool IsDefaultText(string? channel) => channel is null || Key(channel) == DefaultText.Key;

    /// <summary>A voz sem canal (versões antigas) é a "Geral".</summary>
    public static bool IsDefaultVoice(string? channel) => channel is null || Key(channel) == DefaultVoice.Key;

    /// <summary>Dois nomes de canal são o mesmo canal (do mesmo tipo).</summary>
    public static bool SameName(string? a, string? b) => Key(a) == Key(b);

    /// <summary>Um canal criado pela pessoa: o nome limpo, se for válido e não repetir um canal padrão.</summary>
    public static RoomChannel? Create(RoomChannelKind kind, string? name)
    {
        if (!Enum.IsDefined(kind)) return null;
        var clean = CleanName(kind, name);
        if (clean.Length == 0 || Key(clean).Length == 0) return null;
        return new RoomChannel(kind, clean);
    }

    /// <summary>Lista limpa: só canais válidos, sem repetidos e sem os padrão, até <paramref name="max"/>.</summary>
    public static List<RoomChannel> Normalize(IEnumerable<RoomChannel?>? channels, int max = MaxPerPerson)
    {
        var list = new List<RoomChannel>();
        foreach (var channel in channels ?? [])
        {
            if (channel is null || Create(channel.Kind, channel.Name) is not { } clean || clean.IsDefault) continue;
            if (list.Any(c => c.Same(clean))) continue;
            list.Add(clean);
            if (list.Count >= max) break;
        }
        return list;
    }

    /// <summary>
    /// Os canais de um tipo que aparecem na lista: os padrão primeiro, depois os criados (em ordem alfabética), cada um
    /// uma vez só.
    /// </summary>
    public static List<RoomChannel> Merge(RoomChannelKind kind, IEnumerable<RoomChannel> extra)
    {
        var list = Defaults.Where(c => c.Kind == kind).ToList();
        var added = new List<RoomChannel>();
        foreach (var channel in extra)
        {
            if (channel.Kind != kind || list.Any(c => c.Same(channel)) || added.Any(c => c.Same(channel))) continue;
            added.Add(channel);
        }
        list.AddRange(added.OrderBy(c => c.Name, StringComparer.Create(CultureInfo.GetCultureInfo("pt-BR"), ignoreCase: true))
            .Take(Math.Max(0, MaxListed - list.Count)));
        return list;
    }
}

/// <summary>Lista que se compara pelo conteúdo (a presença é um record e é comparada para saber se mudou).</summary>
public sealed class ValueList<T> : IReadOnlyList<T>, IEquatable<ValueList<T>>
{
    private readonly T[] _items;

    public ValueList(IEnumerable<T>? items) => _items = items?.ToArray() ?? [];

    public static ValueList<T> Empty { get; } = new([]);

    public int Count => _items.Length;
    public T this[int index] => _items[index];

    public IEnumerator<T> GetEnumerator() => ((IEnumerable<T>)_items).GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => _items.GetEnumerator();

    public bool Equals(ValueList<T>? other) => other is not null && _items.SequenceEqual(other._items);
    public override bool Equals(object? obj) => Equals(obj as ValueList<T>);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var item in _items) hash.Add(item);
        return hash.ToHashCode();
    }
}
