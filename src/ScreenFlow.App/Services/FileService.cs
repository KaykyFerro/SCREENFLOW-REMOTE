using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using ScreenFlow.Core;
using ScreenFlow.Core.Diagnostics;
using ScreenFlow.Core.Files;
using ScreenFlow.Core.Network;
using ScreenFlow.Core.Room;

namespace ScreenFlow.App.Services;

/// <summary>Um arquivo escolhido para mandar, em cima da caixa de escrever (o SHA-256 e a prévia ficam prontos antes de mandar).</summary>
public sealed class PendingFile : RoomRowBase
{
    private double _progress;
    private bool _ready;
    private string? _error;
    private ImageSource? _thumbnail;

    internal PendingFile(FileInfo info)
    {
        Path = info.FullName;
        Name = FileNames.Clean(info.Name);
        Size = info.Length;
        LastWriteUtc = info.LastWriteTimeUtc;
        Kind = FileNames.KindOf(Name);
    }

    public string Path { get; }
    public string Name { get; }
    public long Size { get; }
    public ChatFileKind Kind { get; }
    internal DateTime LastWriteUtc { get; }
    internal CancellationTokenSource Cancellation { get; } = new();
    internal byte[]? Hash { get; set; }
    internal PreparedImage? Preview { get; set; }
    internal int Duration { get; set; }

    public string Glyph => FileTransferItem.GlyphFor(Kind, Name);
    public bool IsDangerous => FileNames.IsDangerous(Name);

    public double Progress
    {
        get => _progress;
        set => Set(ref _progress, value, nameof(Progress), nameof(StatusText));
    }

    public bool IsReady
    {
        get => _ready;
        set => Set(ref _ready, value, nameof(IsReady), nameof(IsPreparing), nameof(StatusText));
    }

    public bool IsPreparing => !_ready && _error is null;

    public string? Error
    {
        get => _error;
        set => Set(ref _error, value, nameof(Error), nameof(HasError), nameof(IsPreparing), nameof(StatusText));
    }

    public bool HasError => _error is not null;

    public ImageSource? Thumbnail
    {
        get => _thumbnail;
        set => Set(ref _thumbnail, value, nameof(Thumbnail), nameof(HasThumbnail));
    }

    public bool HasThumbnail => _thumbnail is not null;

    /// <summary>"12,4 MB · pronto" / "Preparando 40%" / o problema.</summary>
    public string StatusText => _error ?? (_ready
        ? FileNames.SizeText(Size) + (IsDangerous ? " · pode rodar programas" : string.Empty)
        : $"{FileNames.SizeText(Size)} · conferindo {_progress:P0}");
}

/// <summary>
/// O cartão de um arquivo do chat (o mesmo para todas as mensagens com o mesmo arquivo): o que ele é e, para quem
/// recebeu, como está o download; para quem mandou, quem está baixando.
/// </summary>
public sealed class FileTransferItem : RoomRowBase
{
    private FileDownloadState? _state;
    private double _progress;
    private long _received;
    private double _speed;
    private string? _error;
    private string? _savedPath;
    private string? _sharedPath;
    private string? _uploadText;
    private bool _sharingStopped;

    internal FileTransferItem(ChatFile file, Guid author, string authorName, bool mine)
    {
        File = file;
        Author = author;
        AuthorName = authorName;
        IsMine = mine;
    }

    public ChatFile File { get; }
    public Guid Author { get; }
    public string AuthorName { get; internal set; }
    public bool IsMine { get; }
    public string Name => File.Name;
    public string Glyph => GlyphFor(File.Kind, File.Name);
    public bool IsDangerous => File.IsDangerous;
    public bool IsVideo => File.Kind == ChatFileKind.Video;

    public string WarningText => "Este tipo de arquivo pode rodar programas no seu PC. O ScreenFlow nunca abre sozinho: só abra se confia em quem mandou.";

    /// <summary>"Vídeo · 1,2 GB · 3:25".</summary>
    public string Summary
    {
        get
        {
            var kind = File.Kind switch
            {
                ChatFileKind.Image => "Imagem",
                ChatFileKind.Video => "Vídeo",
                ChatFileKind.Audio => "Áudio",
                _ => FileNames.Extension(File.Name) is { Length: > 1 } extension ? extension[1..].ToUpperInvariant() : "Arquivo",
            };
            var duration = File.Duration > 0 ? " · " + DurationText(File.Duration) : string.Empty;
            return $"{kind} · {File.SizeText}{duration}";
        }
    }

    internal FileDownload? Download { get; set; }

    public FileDownloadState? State
    {
        get => _state;
        set => Set(ref _state, value, nameof(State), nameof(StateText), nameof(ShowProgress), nameof(CanDownload), nameof(CanPause),
            nameof(CanResume), nameof(CanCancel), nameof(CanOpen), nameof(CanShowFolder), nameof(IsVerified), nameof(HasStateText), nameof(IsWarningState));
    }

    /// <summary>De 0 a 100.</summary>
    public double Progress
    {
        get => _progress;
        private set => Set(ref _progress, value, nameof(Progress));
    }

    public string? Error
    {
        get => _error;
        set => Set(ref _error, value, nameof(Error), nameof(StateText), nameof(HasStateText), nameof(IsWarningState));
    }

    public string? SavedPath
    {
        get => _savedPath;
        set => Set(ref _savedPath, value, nameof(SavedPath), nameof(CanOpen), nameof(CanShowFolder), nameof(StateText), nameof(CanDownload));
    }

    /// <summary>Onde o arquivo está neste PC (de quem mandou).</summary>
    public string? SharedPath
    {
        get => _sharedPath;
        set => Set(ref _sharedPath, value, nameof(SharedPath), nameof(CanShowFolder), nameof(CanStopSharing));
    }

    public string? UploadText
    {
        get => _uploadText;
        set => Set(ref _uploadText, value, nameof(UploadText), nameof(StateText), nameof(HasStateText));
    }

    public bool SharingStopped
    {
        get => _sharingStopped;
        set => Set(ref _sharingStopped, value, nameof(SharingStopped), nameof(CanStopSharing), nameof(StateText), nameof(HasStateText));
    }

    private bool SavedExists => _savedPath is not null && System.IO.File.Exists(_savedPath);

    public bool CanDownload => !IsMine && _state is null or FileDownloadState.Failed or FileDownloadState.Canceled
        || (!IsMine && _state == FileDownloadState.Completed && !SavedExists);
    public bool CanPause => _state is FileDownloadState.Connecting or FileDownloadState.Downloading or FileDownloadState.Waiting;
    public bool CanResume => _state == FileDownloadState.Paused;
    public bool CanCancel => _state is FileDownloadState.Connecting or FileDownloadState.Downloading or FileDownloadState.Paused or FileDownloadState.Waiting;
    public bool CanOpen => _state == FileDownloadState.Completed && SavedExists && !IsDangerous;
    public bool CanShowFolder => (_state == FileDownloadState.Completed && SavedExists) || (IsMine && _sharedPath is not null && System.IO.File.Exists(_sharedPath));
    public bool CanStopSharing => IsMine && !_sharingStopped && _sharedPath is not null;
    public bool ShowProgress => _state is FileDownloadState.Connecting or FileDownloadState.Downloading or FileDownloadState.Verifying or FileDownloadState.Paused;
    public bool IsVerified => _state == FileDownloadState.Completed;
    public bool IsWarningState => _state is FileDownloadState.Failed || (_state == FileDownloadState.Paused && _error is not null);
    public bool HasStateText => StateText.Length > 0;

    public string StateText
    {
        get
        {
            if (IsMine)
            {
                if (_sharingStopped) return "Você parou de compartilhar este arquivo.";
                return _uploadText ?? "Fica no seu PC: quem está na conversa baixa direto daqui enquanto o ScreenFlow estiver aberto.";
            }
            return _state switch
            {
                null => $"Fica no PC de {AuthorName}: baixe enquanto ele estiver com o ScreenFlow aberto.",
                FileDownloadState.Waiting => "Na fila (até 3 downloads ao mesmo tempo).",
                FileDownloadState.Connecting => $"Conectando ao PC de {AuthorName}…",
                FileDownloadState.Downloading => ProgressLine(),
                FileDownloadState.Verifying => "Conferindo o arquivo (SHA-256)…",
                FileDownloadState.Completed => SavedExists ? $"Baixado e conferido · {System.IO.Path.GetDirectoryName(_savedPath)}" : "Baixado, mas o arquivo não está mais na pasta.",
                FileDownloadState.Paused => _error is { } why ? why : $"Pausado em {_progress:0}% · clique em continuar",
                FileDownloadState.Failed => _error ?? "O download parou.",
                FileDownloadState.Canceled => "Download cancelado.",
                _ => string.Empty,
            };
        }
    }

    private string ProgressLine()
    {
        var line = $"{FileNames.SizeText(_received)} de {File.SizeText}";
        if (_speed > 1) line += $" · {FileNames.SpeedText(_speed)} · falta {DurationText((int)Math.Ceiling((File.Size - _received) / _speed))}";
        return line;
    }

    internal void Update(FileDownload download)
    {
        _received = download.Received;
        _speed = download.BytesPerSecond;
        Progress = File.Size == 0 ? 0 : Math.Round(download.Received * 100.0 / File.Size, 1);
        Error = download.Error;
        State = download.State;
        Notify(nameof(StateText));
    }

    /// <summary>Um estado de exemplo (só nas capturas de tela).</summary>
    internal void ShowDemo(FileDownloadState state, double percent, double speed, string? savedPath)
    {
        _received = (long)(File.Size * percent / 100);
        _speed = speed;
        Progress = percent;
        if (savedPath is not null) SavedPath = savedPath;
        State = state;
        Notify(nameof(StateText));
    }

    internal static string DurationText(int seconds)
    {
        if (seconds < 60) return $"{Math.Max(1, seconds)} s";
        if (seconds < 3600) return $"{seconds / 60}:{seconds % 60:00}";
        return $"{seconds / 3600} h {seconds % 3600 / 60:00} min";
    }

    internal static string GlyphFor(ChatFileKind kind, string name) => kind switch
    {
        ChatFileKind.Image => "",
        ChatFileKind.Video => "",
        ChatFileKind.Audio => "",
        _ when FileNames.IsDangerous(name) => "",
        _ when FileNames.Extension(name) is ".zip" or ".rar" or ".7z" or ".tar" or ".gz" => "",
        _ => "",
    };
}

/// <summary>
/// (1.5.0) Os arquivos do chat neste PC: prepara o que você anexa (SHA-256 e prévia), compartilha (o
/// <see cref="FileTransferServer"/> entrega para quem tem a mensagem) e baixa o que os outros mandaram, com a pasta
/// escolhida por você, até 3 ao mesmo tempo, retomando de onde parou. Nada que chega é aberto sozinho.
/// </summary>
public sealed class FileService
{
    public const int MaxFilesPerSend = 10;
    public const int MaxParallelDownloads = 3;
    private static readonly TimeSpan PartialLifetime = TimeSpan.FromDays(14);
    private readonly Dispatcher _dispatcher;
    private readonly Dictionary<string, FileTransferItem> _items = new(StringComparer.Ordinal);
    private readonly Dictionary<string, FileDownload> _downloads = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Dictionary<IPAddress, FileUploadProgress>> _uploads = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _uploadCounts = new(StringComparer.Ordinal);
    private FileShareRegistry? _shares;
    private FileReceipts? _receipts;
    private FileTransferServer? _server;
    private byte[]? _key;
    private string _folder = string.Empty;
    private bool _started;
    private bool _demo;

    public FileService(Dispatcher dispatcher) => _dispatcher = dispatcher;

    /// <summary>Por que os arquivos não funcionam neste PC agora (null quando está tudo certo).</summary>
    public string? Problem { get; private set; }

    /// <summary>Um download terminou (conferido) ou parou de vez.</summary>
    public event Action<FileTransferItem>? Finished;

    private string StatesFolder => Path.Combine(_folder, "baixando");

    public void Start()
    {
        if (_started) return;
        _started = true;
        try
        {
            var settings = AppServices.Settings.Current;
            if (settings.FilesKey is not { Length: 32 })
            {
                settings.FilesKey = RandomNumberGenerator.GetBytes(32);
                AppServices.Settings.Save();
            }
            _key = settings.FilesKey;
            _folder = Path.Combine(AppInfo.DataDirectory, "arquivos");
            Directory.CreateDirectory(StatesFolder);
            _shares = new FileShareRegistry(Path.Combine(_folder, "mandados.dat"), _key);
            _receipts = new FileReceipts(Path.Combine(_folder, "baixados.dat"), _key);
            StartServer(settings.FilePort);
            RestoreDownloads();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Problem = $"Os arquivos do chat não funcionam neste PC: {exception.Message}";
            Log.Warn(Problem);
        }
    }

    /// <summary>A porta mudou nas configurações: escuta na nova.</summary>
    public void Restart()
    {
        if (!_started || _shares is null) return;
        StartServer(AppServices.Settings.Current.FilePort);
    }

    public void Stop()
    {
        foreach (var download in _downloads.Values) download.Pause();
        _server?.Dispose();
        _server = null;
    }

    private void StartServer(int port)
    {
        _server?.Dispose();
        _server = new FileTransferServer(_shares!);
        _server.Progress += progress => _dispatcher.BeginInvoke(() => OnUpload(progress));
        try
        {
            _server.Start(port);
            Problem = null;
        }
        catch (SocketException exception)
        {
            Problem = $"A porta TCP {port} dos arquivos está em uso por outro programa: ninguém consegue baixar o que você manda. Troque a porta em Configurações › Rede e firewall.";
            Log.Warn($"Arquivos do chat: porta {port} em uso ({exception.Message}).");
            _server.Dispose();
            _server = null;
        }
    }

    /// <summary>Os downloads que estavam no meio quando o ScreenFlow fechou voltam pausados (os muito velhos são apagados).</summary>
    private void RestoreDownloads()
    {
        foreach (var state in Directory.EnumerateFiles(StatesFolder, "*.state"))
        {
            var old = DateTime.UtcNow - File.GetLastWriteTimeUtc(state) > PartialLifetime;
            var download = FileDownload.Restore(state, _key!);
            if (download is null)
            {
                if (old) TryDelete(state);
                continue;
            }
            if (old)
            {
                download.Cancel();
                continue;
            }
            _downloads[download.File.Share] = download;
            Watch(download);
        }
    }

    // ───────────────────────── Mandar ─────────────────────────

    /// <summary>Começa a preparar um arquivo para mandar (SHA-256 e prévia). Null e o motivo quando não dá.</summary>
    public PendingFile? Prepare(string path, out string? problem)
    {
        problem = null;
        try
        {
            if (Directory.Exists(path))
            {
                problem = "Pastas não vão pelo chat: compacte numa .zip e mande o arquivo.";
                return null;
            }
            var info = new FileInfo(path);
            if (!info.Exists) problem = "O arquivo não existe mais.";
            else if (info.Length == 0) problem = $"\"{info.Name}\" está vazio.";
            else if (info.Length > FileTransferProtocol.MaxFileBytes) problem = $"\"{info.Name}\" passa de 4 GB, o limite do chat.";
            if (problem is not null) return null;
            var pending = new PendingFile(info);
            _ = PrepareAsync(pending);
            return pending;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            problem = $"Não deu para abrir o arquivo: {exception.Message}";
            return null;
        }
    }

    private static async Task PrepareAsync(PendingFile pending)
    {
        try
        {
            var hash = await FileHasher.HashAsync(pending.Path, new Progress<double>(p => pending.Progress = p), pending.Cancellation.Token);
            var after = new FileInfo(pending.Path);
            if (!after.Exists || after.Length != pending.Size || after.LastWriteTimeUtc != pending.LastWriteUtc)
            {
                pending.Error = "O arquivo mudou enquanto era preparado. Tire e anexe de novo.";
                return;
            }
            if (pending.Kind == ChatFileKind.Image && pending.Size <= ImageTools.MaxFileBytes) pending.Preview = ImagePreview(pending.Path);
            else if (pending.Kind == ChatFileKind.Video)
            {
                (pending.Preview, pending.Duration) = await VideoPreviewAsync(pending.Path);
            }
            pending.Thumbnail = pending.Preview?.Preview;
            pending.Hash = hash;
            pending.IsReady = true;
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            pending.Error = $"Não deu para ler o arquivo: {exception.Message}";
        }
    }

    /// <summary>A prévia de uma imagem (a mesma das imagens do chat). Null se o Windows não abre a imagem.</summary>
    private static PreparedImage? ImagePreview(string path)
    {
        try { return ImageTools.MakeChatImage(ImageTools.LoadFile(path, ImageTools.ChatMaxSide)); }
        catch (Exception exception) when (exception is InvalidDataException or IOException or NotSupportedException or ArgumentException or OutOfMemoryException)
        {
            return null;
        }
    }

    /// <summary>
    /// Um quadro do vídeo (no começo) e a duração, pelo próprio Windows (os formatos que o Windows toca: MP4, MOV, WMV,
    /// AVI...). Sem prévia quando o Windows não abre o vídeo: o arquivo vai do mesmo jeito.
    /// </summary>
    private static async Task<(PreparedImage? Preview, int Duration)> VideoPreviewAsync(string path)
    {
        var player = new MediaPlayer { ScrubbingEnabled = true, IsMuted = true, Volume = 0 };
        var opened = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        player.MediaOpened += (_, _) => opened.TrySetResult(true);
        player.MediaFailed += (_, _) => opened.TrySetResult(false);
        try
        {
            player.Open(new Uri(path, UriKind.Absolute));
            var finished = await Task.WhenAny(opened.Task, Task.Delay(TimeSpan.FromSeconds(6)));
            if (finished != opened.Task || !opened.Task.Result) return (null, 0);
            var duration = player.NaturalDuration.HasTimeSpan ? (int)Math.Round(player.NaturalDuration.TimeSpan.TotalSeconds) : 0;
            var (width, height) = (player.NaturalVideoWidth, player.NaturalVideoHeight);
            if (width <= 0 || height <= 0) return (null, duration);
            player.Pause();
            player.Position = TimeSpan.FromSeconds(Math.Min(1.0, Math.Max(0, duration) / 10.0));
            await Task.Delay(700);
            var scale = Math.Min(1.0, 1280.0 / Math.Max(width, height));
            var w = Math.Max(1, (int)(width * scale));
            var h = Math.Max(1, (int)(height * scale));
            var visual = new DrawingVisual();
            using (var context = visual.RenderOpen()) context.DrawVideo(player, new Rect(0, 0, w, h));
            var bitmap = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(visual);
            bitmap.Freeze();
            // Um quadro todo preto (o Windows não desenhou a tempo) é pior que nenhum.
            return (IsBlank(bitmap) ? null : ImageTools.MakeChatImage(bitmap), duration);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            Log.Warn($"Prévia do vídeo não saiu: {exception.Message}");
            return (null, 0);
        }
        finally
        {
            player.Close();
        }
    }

    private static bool IsBlank(BitmapSource bitmap)
    {
        var stride = bitmap.PixelWidth * 4;
        var pixels = new byte[stride * bitmap.PixelHeight];
        bitmap.CopyPixels(pixels, stride, 0);
        var step = Math.Max(4, pixels.Length / 4000 / 4 * 4);
        for (var i = 0; i + 2 < pixels.Length; i += step)
        {
            if (pixels[i] > 20 || pixels[i + 1] > 20 || pixels[i + 2] > 20) return false;
        }
        return true;
    }

    /// <summary>
    /// Passa a compartilhar o arquivo e devolve o anexo para a mensagem (null e o motivo quando não dá). Numa conversa
    /// privada, <paramref name="peer"/> é a pessoa (só ela recebe a chave, dentro da mensagem cifrada).
    /// </summary>
    public ChatFile? Share(PendingFile pending, Guid? peer, out string? problem)
    {
        problem = null;
        if (_demo)
        {
            var demo = new ChatFile(Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant(), pending.Name, pending.Size,
                Convert.ToHexString(pending.Hash ?? new byte[32]).ToLowerInvariant(), Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
                AppInfo.DefaultFilePort, pending.Kind, pending.Duration);
            ItemFor(demo, AppServices.Room.SelfId, AppServices.Room.SelfName, mine: true).SharedPath = pending.Path;
            return demo;
        }
        if (_shares is null || _server is null)
        {
            problem = Problem ?? "Os arquivos do chat não estão funcionando neste PC.";
            return null;
        }
        if (!pending.IsReady || pending.Hash is not { } hash)
        {
            problem = pending.Error ?? "O arquivo ainda está sendo preparado.";
            return null;
        }
        var info = new FileInfo(pending.Path);
        if (!info.Exists || info.Length != pending.Size || info.LastWriteTimeUtc != pending.LastWriteUtc)
        {
            problem = $"\"{pending.Name}\" mudou depois de anexado. Tire e anexe de novo.";
            return null;
        }
        var entry = _shares.Add(pending.Path, hash, pending.Size, pending.LastWriteUtc, peer, pending.Name);
        var file = entry.ToChatFile(_server.Port, pending.Kind, pending.Duration, LocalAddresses());
        ItemFor(file, AppServices.Room.SelfId, AppServices.Room.SelfName, mine: true).SharedPath = pending.Path;
        return file;
    }

    /// <summary>Os IPs deste PC nas redes que importam (Radmin, Tailscale, rede local), para quem recebe pelo servidor.</summary>
    private static IEnumerable<string> LocalAddresses()
        => LocalNetworks.List().Where(a => a.IsUp && a.Kind != NetworkKind.Other).Select(a => a.Address.ToString()).Distinct().Take(ChatFile.MaxAddresses);

    // ───────────────────────── Cartões ─────────────────────────

    /// <summary>O cartão do arquivo (um por arquivo, mesmo que a mensagem apareça de novo).</summary>
    public FileTransferItem ItemFor(ChatFile file, Guid author, string authorName, bool mine)
    {
        if (_items.TryGetValue(file.Share, out var item))
        {
            if (authorName.Length > 0) item.AuthorName = authorName;
            return item;
        }
        item = new FileTransferItem(file, author, authorName, mine);
        _items[file.Share] = item;
        if (mine)
        {
            if (_shares?.Find(file.Share) is { } entry) item.SharedPath = entry.Path;
            else if (!_demo) item.SharingStopped = true;
        }
        else if (_downloads.TryGetValue(file.Share, out var download) && download.File.Hash == file.Hash)
        {
            item.Download = download;
            item.Update(download);
        }
        else if (_receipts?.Find(file.Share) is { } receipt)
        {
            item.SavedPath = receipt.Path;
            item.State = FileDownloadState.Completed;
        }
        return item;
    }

    /// <summary>Baixa: pergunta onde salvar (começando na última pasta usada) e começa (ou entra na fila).</summary>
    public void Download(FileTransferItem item, Window? owner)
    {
        if (item.IsMine) return;
        if (item.Download is { State: FileDownloadState.Paused })
        {
            Resume(item);
            return;
        }
        if (item.Download is { IsRunning: true } or { State: FileDownloadState.Waiting }) return;
        var settings = AppServices.Settings.Current;
        var extension = Path.GetExtension(item.File.Name);
        var dialog = new SaveFileDialog
        {
            Title = $"Salvar \"{item.File.Name}\"",
            FileName = item.File.Name,
            InitialDirectory = settings.FileSaveFolder is { } folder && Directory.Exists(folder) ? folder : DefaultFolder(),
            Filter = extension.Length > 1 ? $"{extension[1..].ToUpperInvariant()}|*{extension}|Todos os arquivos|*.*" : "Todos os arquivos|*.*",
            OverwritePrompt = true,
            AddExtension = extension.Length > 1,
            DefaultExt = extension,
        };
        if (dialog.ShowDialog(owner) != true) return;
        settings.FileSaveFolder = Path.GetDirectoryName(dialog.FileName);
        AppServices.Settings.Save();
        if (_demo || _key is null)
        {
            item.State = FileDownloadState.Failed;
            item.Error = Problem ?? "Os arquivos do chat não estão funcionando neste PC.";
            return;
        }
        var download = new FileDownload(item.File, dialog.FileName, StatesFolder, _key);
        _downloads[item.File.Share] = download;
        _receipts?.Remove(item.File.Share);
        item.SavedPath = null;
        item.Download = download;
        Watch(download);
        item.Update(download);
        StartNext();
    }

    private static string DefaultFolder()
    {
        var downloads = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        return Directory.Exists(downloads) ? downloads : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
    }

    public void Pause(FileTransferItem item) => item.Download?.Pause();

    public void Resume(FileTransferItem item)
    {
        if (item.Download is not { State: FileDownloadState.Paused or FileDownloadState.Failed } download) return;
        item.Error = null;
        Start(download);
    }

    public void Cancel(FileTransferItem item)
    {
        if (item.Download is not { } download) return;
        download.Cancel();
        _downloads.Remove(item.File.Share);
        item.Update(download);
        item.Download = null;
    }

    /// <summary>Abre um arquivo baixado com o programa do Windows (nunca um programa ou script: esses só aparecem na pasta).</summary>
    public string? Open(FileTransferItem item)
    {
        if (!item.CanOpen || item.SavedPath is not { } path) return "Este arquivo não abre pelo ScreenFlow: use Mostrar na pasta.";
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            return null;
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
        {
            return $"O Windows não abriu o arquivo: {exception.Message}";
        }
    }

    public void ShowInFolder(FileTransferItem item)
    {
        var path = item.IsMine ? item.SharedPath : item.SavedPath;
        if (path is null || !File.Exists(path)) return;
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true }); }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or IOException)
        {
            Log.Warn($"Explorer não abriu: {exception.Message}");
        }
    }

    /// <summary>Para de compartilhar um arquivo seu: quem tentar baixar depois ouve que você parou.</summary>
    public void StopSharing(FileTransferItem item)
    {
        if (!item.IsMine) return;
        _shares?.Remove(item.File.Share);
        item.SharingStopped = true;
        item.UploadText = null;
    }

    private void Watch(FileDownload download)
    {
        download.Changed += changed => _dispatcher.BeginInvoke(() => OnDownloadChanged(changed));
    }

    private void OnDownloadChanged(FileDownload download)
    {
        if (_items.TryGetValue(download.File.Share, out var item) && item.Download == download) item.Update(download);
        switch (download.State)
        {
            case FileDownloadState.Completed:
                _receipts?.Add(download.File.Share, download.Destination, DateTimeOffset.UtcNow);
                if (item is not null)
                {
                    item.SavedPath = download.Destination;
                    Finished?.Invoke(item);
                }
                _downloads.Remove(download.File.Share);
                StartNext();
                break;
            case FileDownloadState.Failed or FileDownloadState.Paused or FileDownloadState.Canceled:
                if (download.State == FileDownloadState.Failed && item is not null) Finished?.Invoke(item);
                StartNext();
                break;
        }
    }

    /// <summary>Começa os downloads da fila até ter 3 acontecendo.</summary>
    private void StartNext()
    {
        var running = _downloads.Values.Count(d => d.IsRunning);
        foreach (var waiting in _downloads.Values.Where(d => d.State == FileDownloadState.Waiting).ToList())
        {
            if (running >= MaxParallelDownloads) break;
            Start(waiting);
            running++;
        }
    }

    private void Start(FileDownload download)
    {
        if (_downloads.Values.Count(d => d.IsRunning) >= MaxParallelDownloads)
        {
            // Já tem 3 baixando: este espera a vez ("Na fila").
            download.Queue();
            return;
        }
        var author = _items.TryGetValue(download.File.Share, out var item) ? item.Author : Guid.Empty;
        var file = download.File;
        _ = download.StartAsync(() => SourcesFor(author, file));
    }

    /// <summary>Onde achar quem mandou: o IP de onde ele fala com as salas abertas e, depois, os IPs que vieram na mensagem.</summary>
    private IReadOnlyList<IPEndPoint> SourcesFor(Guid author, ChatFile file)
    {
        IReadOnlyList<IPAddress> live = [];
        try { live = _dispatcher.Invoke(() => AppServices.Room.AddressesOf(author), DispatcherPriority.Normal, CancellationToken.None, TimeSpan.FromSeconds(3)); }
        catch (Exception exception) when (exception is TimeoutException or TaskCanceledException or OperationCanceledException) { }
        return live.Concat(file.AddressList).Distinct().Select(address => new IPEndPoint(address, file.Port)).ToList();
    }

    private void OnUpload(FileUploadProgress progress)
    {
        if (!_items.TryGetValue(progress.Share, out var item)) return;
        if (!_uploads.TryGetValue(progress.Share, out var active)) _uploads[progress.Share] = active = [];
        if (progress.Finished || progress.Failed)
        {
            active.Remove(progress.Remote);
            if (progress.Finished) _uploadCounts[progress.Share] = _uploadCounts.GetValueOrDefault(progress.Share) + 1;
        }
        else active[progress.Remote] = progress;
        var done = _uploadCounts.GetValueOrDefault(progress.Share);
        if (active.Count > 0)
        {
            var first = active.Values.First();
            var who = AppServices.Room.NameAt(first.Remote) ?? first.Remote.ToString();
            var percent = first.Total == 0 ? 0 : first.Sent * 100.0 / first.Total;
            item.UploadText = active.Count == 1 ? $"{who} está baixando · {percent:0}%" : $"{active.Count} pessoas baixando agora";
        }
        else item.UploadText = done > 0 ? (done == 1 ? "Baixado 1 vez direto do seu PC." : $"Baixado {done} vezes direto do seu PC.") : null;
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
    }

    // ───────────────────────── Demonstração (capturas de tela) ─────────────────────────

    /// <summary>Cartões de exemplo para as capturas de tela (nada vai para a rede).</summary>
    internal (ChatFile Zip, ChatFile Video, ChatFile Installer, ChatFile Mine) LoadDemo()
    {
        _demo = true;
        ChatFile Make(string name, long size, ChatFileKind kind, int duration = 0) => new(
            Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant(), name, size,
            Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant(), Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
            AppInfo.DefaultFilePort, kind, duration);
        return (Make("fotos do encontro.zip", 734_003_200, ChatFileKind.File), Make("gameplay ranked.mp4", 1_288_490_189, ChatFileKind.Video, 205),
            Make("setup-mod.exe", 18_874_368, ChatFileKind.File), Make("regulamento do campeonato.pdf", 2_411_724, ChatFileKind.File));
    }

    /// <summary>Põe um cartão de exemplo num estado (só nas capturas de tela).</summary>
    internal void DemoState(FileTransferItem item, FileDownloadState state, double percent, double speed = 0, string? savedPath = null)
    {
        if (_demo) item.ShowDemo(state, percent, speed, savedPath);
    }
}
