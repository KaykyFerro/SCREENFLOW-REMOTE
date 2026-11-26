using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using FFmpeg.AutoGen;

namespace ScreenFlow.Infrastructure.Media;

/// <summary>
/// Guarda as mensagens de erro e de aviso do FFmpeg enquanto um encoder é testado, para dizer por que ele não abriu
/// ("o driver da NVIDIA é antigo para o NVENC"). Fora desses testes o FFmpeg volta para o log padrão dele, e nada roda
/// no caminho do vídeo.
/// </summary>
public static unsafe class FFmpegLog
{
    private const int MaxLines = 24;
    private static readonly object Gate = new();
    private static List<string>? _lines;
    private static nint _defaultCallback = -1;

    /// <summary>Roda <paramref name="action"/> guardando as mensagens do FFmpeg. Se não der para trocar o log, roda sem guardar.</summary>
    public static T Capture<T>(Func<T> action, out IReadOnlyList<string> lines)
    {
        var captured = new List<string>();
        T result;
        lock (Gate)
        {
            var installed = Install(captured);
            try { result = action(); }
            finally
            {
                Volatile.Write(ref _lines, null);
                if (installed) Restore();
            }
        }
        lock (captured) lines = [.. captured];
        return result;
    }

    private static bool Install(List<string> lines)
    {
        try
        {
            if (DefaultCallback() == 0) return false;
            Volatile.Write(ref _lines, lines);
            ffmpeg.av_log_set_callback(new av_log_set_callback_callback_func
            {
                Pointer = (nint)(delegate* unmanaged[Cdecl]<void*, int, byte*, byte*, void>)&OnLog,
            });
            return true;
        }
        catch (Exception)
        {
            Volatile.Write(ref _lines, null);
            return false;
        }
    }

    private static void Restore()
    {
        try { ffmpeg.av_log_set_callback(new av_log_set_callback_callback_func { Pointer = DefaultCallback() }); }
        catch (Exception) { }
    }

    /// <summary>O log padrão do FFmpeg (av_log_default_callback do avutil), para voltar a ele depois do teste.</summary>
    private static nint DefaultCallback()
    {
        if (_defaultCallback != -1) return _defaultCallback;
        _defaultCallback = 0;
        try
        {
            var path = Path.Combine(ffmpeg.RootPath ?? string.Empty, $"avutil-{ffmpeg.LibraryVersionMap["avutil"]}.dll");
            if (NativeLibrary.TryLoad(path, out var library) && NativeLibrary.TryGetExport(library, "av_log_default_callback", out var callback))
                _defaultCallback = callback;
        }
        catch (Exception) { }
        return _defaultCallback;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnLog(void* context, int level, byte* format, byte* arguments)
    {
        // Nada pode escapar daqui: uma exceção voltando para o código nativo derruba o app.
        try
        {
            if (level > ffmpeg.AV_LOG_WARNING || format is null || Volatile.Read(ref _lines) is not { } lines) return;
            const int size = 1024;
            var buffer = stackalloc byte[size];
            var prefix = 1;
            ffmpeg.av_log_format_line(context, level, Marshal.PtrToStringUTF8((nint)format), arguments, buffer, size, &prefix);
            var line = Marshal.PtrToStringUTF8((nint)buffer)?.Trim();
            if (string.IsNullOrEmpty(line)) return;
            lock (lines)
                if (lines.Count < MaxLines) lines.Add(line);
        }
        catch (Exception) { }
    }
}
