using FFmpeg.AutoGen;
using ScreenFlow.Core.Diagnostics;

namespace ScreenFlow.Infrastructure.Media;

/// <summary>Carrega as DLLs do FFmpeg (build LGPL) distribuídas na pasta "ffmpeg" ao lado do executável.</summary>
public static unsafe class FFmpegLoader
{
    private static readonly object Gate = new();
    private static bool? _loaded;

    public static string? Error { get; private set; }
    public static string Version { get; private set; } = "não carregado";

    public static bool EnsureLoaded()
    {
        lock (Gate)
        {
            if (_loaded.HasValue) return _loaded.Value;
            var folder = Path.Combine(AppContext.BaseDirectory, "ffmpeg");
            ffmpeg.RootPath = Directory.Exists(folder) ? folder : AppContext.BaseDirectory;
            try
            {
                DynamicallyLoadedBindings.Initialize();
                Version = ffmpeg.av_version_info();
                ffmpeg.av_log_set_level(ffmpeg.AV_LOG_ERROR);
                Log.Info($"FFmpeg {Version} carregado de {ffmpeg.RootPath}");
                _loaded = true;
            }
            catch (Exception exception)
            {
                Error = $"FFmpeg não encontrado em {ffmpeg.RootPath}: {exception.Message}";
                Log.Error(Error);
                _loaded = false;
            }
            return _loaded.Value;
        }
    }

    public static string ErrorText(int code)
    {
        const int size = 256;
        var buffer = stackalloc byte[size];
        ffmpeg.av_strerror(code, buffer, size);
        return System.Runtime.InteropServices.Marshal.PtrToStringAnsi((nint)buffer) ?? code.ToString();
    }

    public static void Check(int result, string what)
    {
        if (result < 0) throw new InvalidOperationException($"{what}: {ErrorText(result)}");
    }
}
