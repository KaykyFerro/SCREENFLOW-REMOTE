using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ScreenFlow.Core.Diagnostics;

namespace ScreenFlow.Core.Room;

/// <summary>
/// Guarda o chat da sala numa pasta (1.3.0), por exemplo a do Google Drive para computador, que manda tudo para a
/// nuvem sozinho. Fica assim:
/// <code>
/// ScreenFlow Chat/
///   geral/2026-11-27.txt        ← a conversa do dia, para ler em qualquer lugar
///   imagens/&lt;id&gt;.jpg           ← as imagens do chat
///   historico/2026-11.jsonl     ← as mesmas mensagens, para o ScreenFlow mostrar de novo quando abrir
/// </code>
/// As gravações vão numa fila (nunca na thread da interface). Uma mensagem só é gravada uma vez, mesmo que chegue de
/// novo (histórico repassado por outra pessoa).
/// </summary>
public sealed class ChatArchive : IDisposable
{
    public const string FolderName = "ScreenFlow Chat";
    public const string ImagesFolder = "imagens";
    public const string HistoryFolder = "historico";

    private static readonly JsonSerializerOptions Json = new() { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };
    private static readonly CultureInfo Brazil = CultureInfo.GetCultureInfo("pt-BR");
    private readonly BlockingCollection<Action> _queue = new(new ConcurrentQueue<Action>(), 4096);
    private readonly HashSet<(Guid, ulong)> _saved = [];
    private readonly object _gate = new();
    private readonly Thread _writer;
    private readonly Func<DateTimeOffset, DateTime> _localTime;
    private volatile string? _error;
    private int _failures;

    /// <param name="root">A pasta escolhida (a do Google Drive, por exemplo). O chat fica em "ScreenFlow Chat" dentro dela.</param>
    /// <param name="localTime">Converte para a hora local (os testes passam um fuso fixo).</param>
    public ChatArchive(string root, Func<DateTimeOffset, DateTime>? localTime = null)
    {
        if (string.IsNullOrWhiteSpace(root)) throw new ArgumentException("Pasta vazia.", nameof(root));
        Root = root;
        Folder = Path.Combine(root, FolderName);
        _localTime = localTime ?? (time => time.LocalDateTime);
        _writer = new Thread(WriteLoop) { IsBackground = true, Name = "ScreenFlow chat guardado", Priority = ThreadPriority.BelowNormal };
        _writer.Start();
    }

    public string Root { get; }

    /// <summary>
    /// A pasta escolhida no "Escolher outra pasta" vira a raiz do chat. Quem escolhe a própria "ScreenFlow Chat" guarda
    /// nela (e não numa "ScreenFlow Chat" dentro dela).
    /// </summary>
    public static string RootFor(string chosen)
    {
        var trimmed = chosen.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return string.Equals(Path.GetFileName(trimmed), FolderName, StringComparison.OrdinalIgnoreCase)
               && Path.GetDirectoryName(trimmed) is { Length: > 0 } parent
            ? parent
            : chosen;
    }

    /// <summary>A pasta "ScreenFlow Chat" onde tudo fica.</summary>
    public string Folder { get; }

    /// <summary>Por que a última gravação falhou (null quando está tudo certo).</summary>
    public string? Error => _error;

    /// <summary>Uma gravação falhou (a pasta sumiu, o Drive está fechado...). Chamado fora da thread da interface.</summary>
    public event Action<string>? Failed;

    /// <summary>Lê as últimas mensagens guardadas (para mostrar o chat de novo quando o ScreenFlow abre) e lembra quais já estão lá.</summary>
    public IReadOnlyList<RoomChatMessage> LoadRecent(int max = 200)
    {
        var messages = new List<RoomChatMessage>();
        try
        {
            var folder = Path.Combine(Folder, HistoryFolder);
            if (!Directory.Exists(folder)) return [];
            // Os dois últimos meses bastam para o chat de agora (e para não gravar de novo o que já está lá).
            foreach (var file in Directory.EnumerateFiles(folder, "*.jsonl").Order(StringComparer.Ordinal).TakeLast(2))
            foreach (var line in File.ReadLines(file, Encoding.UTF8))
            {
                if (Parse(line) is not { } message) continue;
                lock (_gate)
                {
                    if (!_saved.Add((message.AuthorId, message.MessageId))) continue;
                }
                messages.Add(message);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            Log.Warn($"Chat guardado não foi lido: {exception.Message}");
        }
        return messages.OrderBy(m => m.Time).TakeLast(Math.Max(0, max)).Select(m => m with { History = true }).ToArray();
    }

    /// <summary>Grava a mensagem (se ainda não estiver guardada). A imagem, se já estiver neste PC, vai junto.</summary>
    public void Add(RoomChatMessage message, byte[]? image = null)
    {
        lock (_gate)
        {
            if (!_saved.Add((message.AuthorId, message.MessageId))) return;
        }
        Enqueue(() =>
        {
            var local = _localTime(message.Time);
            var channel = SafeName(message.ChannelName);
            var line = new StringBuilder();
            line.Append('[').Append(local.ToString("HH:mm", Brazil)).Append("] ").Append(message.AuthorName).Append(": ");
            if (message.Caption.Length > 0) line.Append(message.Caption.ReplaceLineEndings("\n    "));
            if (message.Image is { } picture)
            {
                if (message.Caption.Length > 0) line.Append(' ');
                line.Append("[imagem: ../").Append(ImagesFolder).Append('/').Append(ImageFileName(picture.Id, image)).Append(']');
            }
            if (message.File is { } attachedFile)
            {
                line.Append(' ').Append("[arquivo: ").Append(attachedFile.Name).Append(" · ").Append(attachedFile.SizeText).Append(']');
            }
            line.AppendLine();
            var day = Path.Combine(Folder, channel, $"{local:yyyy-MM-dd}.txt");
            Directory.CreateDirectory(Path.GetDirectoryName(day)!);
            if (!File.Exists(day)) File.AppendAllText(day, $"# {message.ChannelName} · {local.ToString("dddd, dd/MM/yyyy", Brazil)}{Environment.NewLine}{Environment.NewLine}", Encoding.UTF8);
            File.AppendAllText(day, line.ToString(), Encoding.UTF8);

            var history = Path.Combine(Folder, HistoryFolder, $"{message.Time.UtcDateTime:yyyy-MM}.jsonl");
            Directory.CreateDirectory(Path.GetDirectoryName(history)!);
            File.AppendAllText(history, JsonSerializer.Serialize(Entry.From(message), Json) + "\n", Encoding.UTF8);
            if (image is not null && message.Image is { } attached) WriteImage(attached.Id, image);
        });
    }

    /// <summary>A imagem de uma mensagem chegou depois dela: grava na pasta das imagens.</summary>
    public void AddImage(string id, byte[] data)
    {
        if (!RoomBlob.IsValidId(id)) return;
        Enqueue(() => WriteImage(id, data));
    }

    /// <summary>
    /// Uma imagem guardada na pasta (para as mensagens antigas cuja imagem já saiu deste PC). Null quando não tem. Pode
    /// demorar (o Google Drive baixa o arquivo na hora): chame fora da thread da interface.
    /// </summary>
    public byte[]? ReadImage(string id)
    {
        if (!RoomBlob.IsValidId(id)) return null;
        foreach (var name in (string[])[id + ".jpg", id + ".png"])
        {
            try
            {
                var file = new FileInfo(Path.Combine(Folder, ImagesFolder, name));
                if (!file.Exists || file.Length is 0 or > RoomBlob.MaxImageBytes) continue;
                var data = File.ReadAllBytes(file.FullName);
                if (RoomBlob.IdOf(data) == id) return data;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        }
        return null;
    }

    /// <summary>Espera a fila de gravação esvaziar (ao fechar o app, e nos testes).</summary>
    public bool Flush(TimeSpan timeout)
    {
        using var done = new ManualResetEventSlim();
        if (!_queue.TryAdd(() => done.Set())) return false;
        return done.Wait(timeout);
    }

    public void Dispose()
    {
        _queue.CompleteAdding();
        _writer.Join(TimeSpan.FromSeconds(3));
    }

    private void Enqueue(Action write)
    {
        if (_queue.IsAddingCompleted) return;
        if (!_queue.TryAdd(write)) Log.Warn("Fila do chat guardado cheia: uma mensagem não foi gravada.");
    }

    private void WriteLoop()
    {
        foreach (var write in _queue.GetConsumingEnumerable())
        {
            try
            {
                write();
                if (_error is not null)
                {
                    _error = null;
                    _failures = 0;
                    Log.Info("Chat guardado voltou a gravar.");
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                var first = _error is null;
                _error = exception.Message;
                // Avisa uma vez (e de novo a cada 50 falhas), sem uma enxurrada de avisos com o Drive fechado.
                if (first || ++_failures % 50 == 0)
                {
                    Log.Warn($"Chat guardado: {exception.Message}");
                    Failed?.Invoke(exception.Message);
                }
            }
        }
    }

    private void WriteImage(string id, byte[] data)
    {
        var path = Path.Combine(Folder, ImagesFolder, ImageFileName(id, data));
        if (File.Exists(path)) return;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        File.WriteAllBytes(temp, data);
        File.Move(temp, path, overwrite: true);
    }

    /// <summary>O nome do arquivo da imagem: o id e a extensão pelo conteúdo (o chat manda JPEG; PNG fica PNG).</summary>
    public static string ImageFileName(string id, byte[]? data)
        => id + (data is [0x89, 0x50, 0x4E, 0x47, ..] ? ".png" : ".jpg");

    /// <summary>Nome do canal que serve de pasta em qualquer sistema (sem \ / : * ? " &lt; &gt; |).</summary>
    public static string SafeName(string channel)
    {
        var invalid = Path.GetInvalidFileNameChars().Concat(['\\', '/', ':', '*', '?', '"', '<', '>', '|']).ToHashSet();
        var clean = new string(channel.Select(c => invalid.Contains(c) || char.IsControl(c) ? '-' : c).ToArray()).Trim(' ', '.');
        return clean.Length == 0 ? RoomChannels.DefaultTextName : clean;
    }

    private static RoomChatMessage? Parse(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return null;
        try { return JsonSerializer.Deserialize<Entry>(line, Json)?.ToMessage(); }
        catch (JsonException) { return null; }
    }

    /// <summary>Uma mensagem no arquivo de histórico (nomes curtos: o arquivo cresce todo dia).</summary>
    private sealed record Entry(
        [property: JsonPropertyName("a")] Guid Author,
        [property: JsonPropertyName("n")] string Name,
        [property: JsonPropertyName("i")] ulong Id,
        [property: JsonPropertyName("t")] long Time,
        [property: JsonPropertyName("x")] string Text,
        [property: JsonPropertyName("c")] string? Channel = null,
        [property: JsonPropertyName("img")] string? ImageId = null,
        [property: JsonPropertyName("s")] int ImageSize = 0,
        [property: JsonPropertyName("w")] int ImageWidth = 0,
        [property: JsonPropertyName("h")] int ImageHeight = 0,
        [property: JsonPropertyName("f")] ChatFile? File = null)
    {
        public static Entry From(RoomChatMessage message) => new(message.AuthorId, message.AuthorName, message.MessageId,
            message.Time.ToUnixTimeMilliseconds(), message.Text, message.InDefaultChannel ? null : message.ChannelName,
            message.Image?.Id, message.Image?.Size ?? 0, message.Image?.Width ?? 0, message.Image?.Height ?? 0,
            message.File is { IsValid: true } file ? file : null);

        public RoomChatMessage? ToMessage()
        {
            var name = RoomPacket.CleanName(Name);
            var text = RoomPacket.CleanText(Text);
            if (Author == Guid.Empty || name.Length == 0 || text.Length == 0) return null;
            DateTimeOffset time;
            try { time = DateTimeOffset.FromUnixTimeMilliseconds(Time); }
            catch (ArgumentOutOfRangeException) { return null; }
            var channel = Channel is null ? null : RoomChannels.CleanName(RoomChannelKind.Text, Channel);
            if (channel is { Length: 0 } || RoomChannels.IsDefaultText(channel)) channel = null;
            ChatImage? image = RoomBlob.IsValidId(ImageId) && ImageSize > 0 && ImageWidth > 0 && ImageHeight > 0
                ? new ChatImage(ImageId!, ImageSize, ImageWidth, ImageHeight)
                : null;
            return new RoomChatMessage(Author, name, Id, time, text, Image: image, Channel: channel, File: File is { IsValid: true } file ? file : null);
        }
    }
}

/// <summary>Acha a pasta do Google Drive para computador neste PC (a unidade "Google Drive" ou a pasta espelhada).</summary>
public static class CloudFolders
{
    /// <summary>Nomes da pasta "Meu Drive" nos idiomas mais comuns.</summary>
    public static readonly string[] MyDriveNames = ["Meu Drive", "My Drive", "Mi unidad", "Mon Drive", "Il mio Drive", "Meine Ablage"];

    private static readonly string[] NotMyDrive =
        [".shortcut-targets-by-id", "Drives compartilhados", "Shared drives", "Computadores", "Computers", "$RECYCLE.BIN", "System Volume Information"];

    /// <summary>
    /// A pasta "Meu Drive": primeiro numa unidade chamada "Google Drive" (o modo padrão, G:), depois no modo espelhado
    /// (a pasta dentro do usuário). Null quando o Google Drive para computador não está neste PC.
    /// </summary>
    /// <param name="drives">As unidades do PC (raiz e nome do volume).</param>
    /// <param name="userProfile">A pasta do usuário (C:\Users\nome).</param>
    public static string? FindGoogleDrive(IEnumerable<(string Root, string? Label)> drives, string? userProfile)
    {
        foreach (var (root, label) in drives)
        {
            if (label is null || !label.Contains("Google Drive", StringComparison.OrdinalIgnoreCase)) continue;
            try
            {
                foreach (var name in MyDriveNames)
                {
                    var path = Path.Combine(root, name);
                    if (Directory.Exists(path)) return path;
                }
                var other = Directory.EnumerateDirectories(root)
                    .FirstOrDefault(d => !NotMyDrive.Contains(Path.GetFileName(d), StringComparer.OrdinalIgnoreCase));
                if (other is not null) return other;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        }
        if (string.IsNullOrEmpty(userProfile)) return null;
        foreach (var name in MyDriveNames.Append("Google Drive"))
        {
            var path = Path.Combine(userProfile, name);
            if (Directory.Exists(path)) return path;
        }
        return null;
    }
}
