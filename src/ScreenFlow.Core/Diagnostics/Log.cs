using System.Collections.Concurrent;
using System.Text;

namespace ScreenFlow.Core.Diagnostics;

public enum LogLevel { Debug, Info, Warning, Error }

/// <summary>
/// Log em arquivo, thread-safe, com rotação diária e limite de arquivos.
/// Arquivos ficam em %LOCALAPPDATA%\ScreenFlow\logs.
/// </summary>
public static class Log
{
    private static readonly BlockingCollection<string> Queue = new(new ConcurrentQueue<string>(), 10_000);
    private static readonly object StartLock = new();
    private static Thread? _writer;
    private static string? _directory;

    public static LogLevel MinimumLevel { get; set; } = LogLevel.Info;
    public static string Directory => _directory ??= Path.Combine(AppInfo.DataDirectory, "logs");
    public static string CurrentFile => Path.Combine(Directory, $"screenflow-{DateTime.Now:yyyy-MM-dd}.log");

    public static event Action<LogLevel, string>? Written;

    public static void Configure(string directory) => _directory = directory;

    public static void Debug(string message) => Write(LogLevel.Debug, message);
    public static void Info(string message) => Write(LogLevel.Info, message);
    public static void Warn(string message) => Write(LogLevel.Warning, message);
    public static void Error(string message, Exception? exception = null)
        => Write(LogLevel.Error, exception is null ? message : $"{message}: {exception}");

    public static void Write(LogLevel level, string message)
    {
        if (level < MinimumLevel) return;
        EnsureWriter();
        var line = $"{DateTime.Now:HH:mm:ss.fff} [{level.ToString().ToUpperInvariant()[..4]}] [{Environment.CurrentManagedThreadId:D3}] {message}";
        Queue.TryAdd(line);
        Written?.Invoke(level, message);
    }

    public static void Flush()
    {
        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (Queue.Count > 0 && DateTime.UtcNow < deadline) Thread.Sleep(10);
    }

    private static void EnsureWriter()
    {
        if (_writer is not null) return;
        lock (StartLock)
        {
            if (_writer is not null) return;
            _writer = new Thread(WriterLoop) { IsBackground = true, Name = "ScreenFlow log", Priority = ThreadPriority.BelowNormal };
            _writer.Start();
        }
    }

    private static void WriterLoop()
    {
        try
        {
            System.IO.Directory.CreateDirectory(Directory);
            PruneOldFiles();
        }
        catch { /* sem diretório de log: segue sem arquivo */ }

        var builder = new StringBuilder();
        foreach (var line in Queue.GetConsumingEnumerable())
        {
            builder.Clear().AppendLine(line);
            while (builder.Length < 64 * 1024 && Queue.TryTake(out var next)) builder.AppendLine(next);
            try { File.AppendAllText(CurrentFile, builder.ToString(), Encoding.UTF8); }
            catch { /* disco cheio ou bloqueado: descarta */ }
        }
    }

    private static void PruneOldFiles()
    {
        foreach (var file in new DirectoryInfo(Directory).GetFiles("screenflow-*.log")
                     .OrderByDescending(f => f.Name).Skip(14))
        {
            try { file.Delete(); } catch { }
        }
    }
}
