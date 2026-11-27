using System.Globalization;
using System.Text;

namespace ScreenFlow.Core.Files;

/// <summary>O que um arquivo do chat é, para mostrar a prévia certa.</summary>
public enum ChatFileKind : byte { File = 0, Image = 1, Video = 2, Audio = 3 }

/// <summary>
/// (1.5.0) Nomes de arquivos que chegam pelo chat. O nome vem de outro PC, então nunca é usado como caminho: fica só o
/// nome (sem pasta, sem "..", sem caracteres que o Windows não aceita, sem os nomes reservados como CON e sem os
/// caracteres invisíveis que escondem a extensão de verdade, como em "foto‮gpj.exe").
/// </summary>
public static class FileNames
{
    public const int MaxNameLength = 120;
    public const string Fallback = "arquivo";

    private static readonly HashSet<char> Invalid = ['<', '>', ':', '"', '/', '\\', '|', '?', '*'];

    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$",
        "COM0", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "COM¹", "COM²", "COM³",
        "LPT0", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9", "LPT¹", "LPT²", "LPT³",
    };

    /// <summary>
    /// Tipos que rodam programas ao abrir (ou têm macros): o cartão avisa, o ScreenFlow nunca abre sozinho e não oferece
    /// "Abrir" para eles (só mostrar na pasta).
    /// </summary>
    private static readonly HashSet<string> Dangerous = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".com", ".bat", ".cmd", ".scr", ".pif", ".msi", ".msp", ".mst", ".msc", ".cpl", ".dll", ".sys", ".drv", ".ocx",
        ".ps1", ".ps2", ".psm1", ".psd1", ".ps1xml", ".psc1", ".vbs", ".vbe", ".vb", ".js", ".jse", ".wsf", ".wsh", ".ws", ".wsc",
        ".hta", ".sct", ".jar", ".lnk", ".url", ".scf", ".reg", ".inf", ".chm", ".application", ".appref-ms", ".gadget",
        ".appx", ".appxbundle", ".msix", ".msixbundle", ".iso", ".img", ".vhd", ".vhdx",
        ".docm", ".dotm", ".xlsm", ".xltm", ".xlam", ".xll", ".pptm", ".potm", ".ppam", ".ppsm", ".sldm",
    };

    private static readonly HashSet<string> Images = new(StringComparer.OrdinalIgnoreCase) { ".png", ".jpg", ".jpeg", ".jfif", ".gif", ".bmp", ".webp", ".tif", ".tiff", ".ico" };
    private static readonly HashSet<string> Videos = new(StringComparer.OrdinalIgnoreCase) { ".mp4", ".m4v", ".mov", ".avi", ".wmv", ".mkv", ".webm", ".mpg", ".mpeg", ".3gp", ".ts", ".flv" };
    private static readonly HashSet<string> Audios = new(StringComparer.OrdinalIgnoreCase) { ".mp3", ".wav", ".ogg", ".opus", ".m4a", ".aac", ".flac", ".wma" };

    /// <summary>Só o nome do arquivo, seguro para gravar no Windows. Nunca vazio.</summary>
    public static string Clean(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return Fallback;
        // Só o último pedaço: "../../Windows/x.dll", "C:\x" e "\\servidor\x" viram "x.dll" e "x".
        var last = name.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? string.Empty;
        var builder = new StringBuilder(last.Length);
        foreach (var rune in last.EnumerateRunes())
        {
            if (Rune.IsControl(rune) || IsInvisible(rune.Value) || rune == Rune.ReplacementChar) continue;
            if (rune.IsAscii && Invalid.Contains((char)rune.Value)) builder.Append('_');
            else builder.Append(rune.ToString());
        }
        // O Windows tira pontos e espaços do fim ("x.exe." vira "x.exe"); aqui eles saem antes.
        var clean = builder.ToString().Trim().TrimEnd('.', ' ');
        if (clean.Length == 0 || clean.All(c => c == '.')) return Fallback;
        var stem = clean.Split('.')[0].TrimEnd(' ');
        if (Reserved.Contains(stem)) clean = "_" + clean;
        if (clean.Length > MaxNameLength)
        {
            var extension = Path.GetExtension(clean);
            if (extension.Length > 16) extension = string.Empty;
            clean = TruncateRunes(clean[..^extension.Length], MaxNameLength - extension.Length).TrimEnd('.', ' ') + extension;
        }
        return clean.Length == 0 ? Fallback : clean;
    }

    /// <summary>A extensão de verdade (a última), em minúsculas, com o ponto (".zip"). Vazia sem extensão.</summary>
    public static string Extension(string name) => Path.GetExtension(Clean(name)).ToLowerInvariant();

    /// <summary>Roda um programa (ou tem macros) ao abrir.</summary>
    public static bool IsDangerous(string name) => Dangerous.Contains(Extension(name));

    public static ChatFileKind KindOf(string name)
    {
        var extension = Extension(name);
        if (Images.Contains(extension)) return ChatFileKind.Image;
        if (Videos.Contains(extension)) return ChatFileKind.Video;
        return Audios.Contains(extension) ? ChatFileKind.Audio : ChatFileKind.File;
    }

    /// <summary>"820 bytes", "12 KB", "1,4 MB", "2,35 GB".</summary>
    public static string SizeText(long bytes)
    {
        var brazil = CultureInfo.GetCultureInfo("pt-BR");
        if (bytes < 1024) return bytes == 1 ? "1 byte" : $"{bytes} bytes";
        if (bytes < 1024 * 1024) return (bytes / 1024.0).ToString("0", brazil) + " KB";
        if (bytes < 1024L * 1024 * 1024) return (bytes / (1024.0 * 1024)).ToString(bytes < 100L * 1024 * 1024 ? "0.0" : "0", brazil) + " MB";
        return (bytes / (1024.0 * 1024 * 1024)).ToString("0.00", brazil) + " GB";
    }

    /// <summary>"3,2 MB/s".</summary>
    public static string SpeedText(double bytesPerSecond) => SizeText((long)Math.Max(0, bytesPerSecond)) + "/s";

    /// <summary>Um nome que ainda não existe na pasta: "foto.png", "foto (1).png", "foto (2).png"...</summary>
    public static string Unique(string folder, string name)
    {
        var clean = Clean(name);
        var path = Path.Combine(folder, clean);
        if (!File.Exists(path) && !Directory.Exists(path)) return path;
        var stem = Path.GetFileNameWithoutExtension(clean);
        var extension = Path.GetExtension(clean);
        for (var i = 1; i < 1000; i++)
        {
            path = Path.Combine(folder, $"{stem} ({i}){extension}");
            if (!File.Exists(path) && !Directory.Exists(path)) return path;
        }
        return Path.Combine(folder, $"{stem} ({Guid.NewGuid():N}){extension}");
    }

    // Marcas de direção (que invertem o texto e escondem a extensão), espaços de largura zero e o BOM.
    private static bool IsInvisible(int value) => value is (>= 0x200B and <= 0x200F) or (>= 0x202A and <= 0x202E)
        or (>= 0x2060 and <= 0x2064) or (>= 0x2066 and <= 0x2069) or 0xFEFF or 0x061C or 0x180E;

    private static string TruncateRunes(string text, int maxChars)
    {
        if (text.Length <= maxChars) return text;
        var builder = new StringBuilder(maxChars);
        foreach (var rune in text.EnumerateRunes())
        {
            if (builder.Length + rune.Utf16SequenceLength > maxChars) break;
            builder.Append(rune.ToString());
        }
        return builder.ToString();
    }
}
