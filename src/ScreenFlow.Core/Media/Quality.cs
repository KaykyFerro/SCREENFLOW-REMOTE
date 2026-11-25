namespace ScreenFlow.Core.Media;

public enum VideoCodec { H264 = 0, Hevc = 1, Av1 = 2 }

public enum EncoderVendor { Nvidia, Amd, Intel, Software }

public sealed record Resolution(string Label, int Width, int Height)
{
    public static readonly Resolution P720 = new("720p", 1280, 720);
    public static readonly Resolution P1080 = new("1080p", 1920, 1080);
    public static readonly Resolution P1440 = new("1440p", 2560, 1440);
    public static readonly Resolution P2160 = new("4K", 3840, 2160);

    public static IReadOnlyList<Resolution> All { get; } = [P720, P1080, P1440, P2160];

    public long Pixels => (long)Width * Height;

    public static Resolution FromLabel(string? label)
        => All.FirstOrDefault(r => string.Equals(r.Label, label, StringComparison.OrdinalIgnoreCase)) ?? P1080;

    /// <summary>Escala a resolução para caber na origem, mantendo proporção e dimensões pares.</summary>
    public (int Width, int Height) FitTo(int sourceWidth, int sourceHeight)
    {
        if (sourceWidth <= 0 || sourceHeight <= 0) return (Width, Height);
        var scale = Math.Min(1.0, Math.Min(Width / (double)sourceWidth, Height / (double)sourceHeight));
        var w = Math.Max(2, (int)Math.Round(sourceWidth * scale) & ~1);
        var h = Math.Max(2, (int)Math.Round(sourceHeight * scale) & ~1);
        return (w, h);
    }

    public override string ToString() => Label;
}

public static class Quality
{
    public static IReadOnlyList<int> FrameRates { get; } = [30, 60, 120, 144, 165];

    /// <summary>Taxas comuns de webcams.</summary>
    public static IReadOnlyList<int> CameraFrameRates { get; } = [15, 30, 60];

    /// <summary>Nome curto do perfil pela taxa de pixels (largura × altura × FPS): Leve, Alto, Ultra ou Máximo.</summary>
    public static string TierLabel(Resolution resolution, int fps)
    {
        var rate = resolution.Pixels * (long)Math.Max(1, fps);
        return rate switch
        {
            >= 480_000_000 => "Máximo",
            >= 200_000_000 => "Ultra",
            >= 100_000_000 => "Alto",
            _ => "Leve",
        };
    }

    /// <summary>
    /// Bitrate alvo em kbps para HEVC/AV1. H.264 recebe +40% para qualidade equivalente.
    /// Valores baseados na tabela de perfis do projeto (1080p60 15–25, 1440p144 40–60, 4K60 40–60, 4K165 90–150 Mbps).
    /// </summary>
    public static int TargetBitrateKbps(Resolution resolution, int fps, VideoCodec codec)
    {
        // Bits por pixel por frame calibrado para conteúdo de desktop/jogos com encoder de baixa latência.
        var reference = resolution.Pixels * (double)fps;
        double bpp = reference switch
        {
            <= 1280d * 720 * 60 => 0.135,
            <= 1920d * 1080 * 60 => 0.10,
            <= 2560d * 1440 * 144 => 0.095,
            _ => 0.085,
        };
        var kbps = reference * bpp / 1000.0;
        if (codec == VideoCodec.H264) kbps *= 1.4;
        else if (codec == VideoCodec.Av1) kbps *= 0.9;
        return (int)Math.Clamp(Math.Round(kbps / 500) * 500, 2_000, 150_000);
    }

    /// <summary>
    /// Bitrate por viewer quando o host envia uma cópia para cada pessoa: divide 80% do upload pelo número de viewers.
    /// Retorna o próprio alvo quando o upload é desconhecido.
    /// </summary>
    public static int PerViewerBitrateKbps(int targetKbps, double uploadMbps, int viewers)
    {
        if (uploadMbps <= 0 || viewers <= 0) return targetKbps;
        var share = (int)(uploadMbps * 1000 * 0.8 / viewers);
        return Math.Max(1_000, Math.Min(targetKbps, share));
    }

    public static string CodecLabel(VideoCodec codec) => codec switch
    {
        VideoCodec.Av1 => "AV1",
        VideoCodec.Hevc => "HEVC",
        _ => "H.264",
    };
}

public static class CodecNegotiation
{
    /// <summary>Ordem de preferência: AV1, HEVC, H.264, limitada ao que os dois lados suportam.</summary>
    public static VideoCodec? Choose(IEnumerable<VideoCodec> hostEncoders, IEnumerable<VideoCodec> viewerDecoders, VideoCodec? hostPreferred = null)
    {
        var common = hostEncoders.Intersect(viewerDecoders).ToHashSet();
        if (common.Count == 0) return null;
        if (hostPreferred is { } preferred && common.Contains(preferred)) return preferred;
        foreach (var codec in new[] { VideoCodec.Av1, VideoCodec.Hevc, VideoCodec.H264 })
            if (common.Contains(codec)) return codec;
        return null;
    }
}
