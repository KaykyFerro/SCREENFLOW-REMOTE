using System.Text.RegularExpressions;
using ScreenFlow.Core.Media;

namespace ScreenFlow.Core.Settings;

public sealed record GpuInfo(string Name, EncoderVendor Vendor, long DedicatedVideoMemoryMb)
{
    /// <summary>Nome curto para mostrar num cartão: "NVIDIA GeForce RTX 2060" vira "RTX 2060", "Intel(R) Arc(TM) A770 Graphics" vira "Arc A770".</summary>
    public string ShortName
    {
        get
        {
            var name = Regex.Replace(Name, @"\((R|TM)\)|[®™]", string.Empty, RegexOptions.IgnoreCase);
            name = Regex.Replace(name, @"^\s*(NVIDIA|AMD|Intel)\s+", string.Empty, RegexOptions.IgnoreCase);
            name = Regex.Replace(name, @"^GeForce\s+(?=(RTX|GTX)\b)", string.Empty, RegexOptions.IgnoreCase);
            name = Regex.Replace(name, @"\s+(Graphics|Series)\s*$", string.Empty, RegexOptions.IgnoreCase);
            name = Regex.Replace(name, @"\s{2,}", " ").Trim();
            return name.Length > 0 ? name : Name;
        }
    }
}

public sealed record EncoderInfo(string FfmpegName, VideoCodec Codec, EncoderVendor Vendor, bool Hardware, int MaxWidth, int MaxHeight)
{
    public string Label => $"{Quality.CodecLabel(Codec)} · {FfmpegName}" + (Hardware ? string.Empty : " (software)");
}

public sealed record HardwareProfile(
    IReadOnlyList<GpuInfo> Gpus,
    IReadOnlyList<EncoderInfo> Encoders,
    IReadOnlyList<VideoCodec> HardwareDecoders,
    int CpuCores,
    long MemoryMb,
    int MonitorWidth,
    int MonitorHeight,
    int MonitorRefreshHz,
    string OsVersion);

public sealed record NetworkProfile(string? RadminAddress, long LinkSpeedMbps, double? MeasuredMbps);

public sealed record AutoConfigResult(
    Resolution Resolution,
    int Fps,
    VideoCodec Codec,
    string Encoder,
    int BitrateKbps,
    int FecGroupSize,
    IReadOnlyList<string> Notes);

/// <summary>
/// Escolhe automaticamente o melhor perfil para o PC e a rede: encoder (AV1 &gt; HEVC &gt; H.264, hardware primeiro),
/// resolução e FPS limitados pelo monitor e pela GPU, e bitrate limitado pela banda medida até o viewer.
/// </summary>
public static class AutoConfigurator
{
    public static AutoConfigResult Recommend(HardwareProfile hardware, NetworkProfile network)
    {
        var notes = new List<string>();

        var encoder = hardware.Encoders
            .OrderByDescending(e => e.Hardware)
            .ThenByDescending(e => e.Codec)
            .FirstOrDefault();
        if (encoder is null)
        {
            encoder = new EncoderInfo("libx264", VideoCodec.H264, EncoderVendor.Software, false, 1920, 1080);
            notes.Add("Nenhum encoder de vídeo encontrado. Confira se a pasta \"ffmpeg\" está junto do ScreenFlow.exe.");
        }
        if (!encoder.Hardware)
            notes.Add("Sem encoder por hardware: 4K a 165 FPS não vai rodar. O perfil foi limitado a 1080p 60 FPS.");

        // Resolução: a maior da lista que cabe no monitor e no encoder.
        var maxW = Math.Min(hardware.MonitorWidth > 0 ? hardware.MonitorWidth : 1920, encoder.MaxWidth);
        var maxH = Math.Min(hardware.MonitorHeight > 0 ? hardware.MonitorHeight : 1080, encoder.MaxHeight);
        var resolution = Resolution.All.Where(r => r.Width <= maxW && r.Height <= maxH).DefaultIfEmpty(Resolution.P720).Last();

        // FPS: até a taxa do monitor (165 no máximo).
        var refresh = hardware.MonitorRefreshHz > 0 ? hardware.MonitorRefreshHz : 60;
        var fps = Quality.FrameRates.Where(f => f <= refresh + 1).DefaultIfEmpty(30).Max();

        if (!encoder.Hardware)
        {
            resolution = Resolution.All.Where(r => r.Pixels <= Resolution.P1080.Pixels && r.Pixels <= resolution.Pixels).Last();
            fps = Math.Min(fps, hardware.CpuCores >= 8 ? 60 : 30);
        }
        else if (resolution == Resolution.P2160 && fps > 60 && !SupportsHighRate4K(hardware, encoder))
        {
            fps = 120;
            notes.Add("A GPU detectada provavelmente não codifica 4K acima de 120 FPS em tempo real. FPS limitado a 120.");
        }

        if (hardware.CpuCores < 4)
        {
            fps = Math.Min(fps, 60);
            notes.Add("CPU com menos de 4 núcleos: FPS limitado a 60 para não travar a rede e o áudio.");
        }

        // Bitrate: tabela de perfis, limitado a 70% da banda medida (ou da velocidade do link).
        var bitrate = Quality.TargetBitrateKbps(resolution, fps, encoder.Codec);
        var budgetMbps = network.MeasuredMbps ?? (network.LinkSpeedMbps > 0 ? network.LinkSpeedMbps : 0);
        if (budgetMbps > 0)
        {
            var cap = (int)(budgetMbps * 1000 * 0.7);
            while (bitrate > cap)
            {
                // Reduz FPS acima de 60 primeiro, depois a resolução, como no controle adaptativo.
                if (fps > 60) fps = Quality.FrameRates.Where(f => f < fps).Max();
                else if (resolution != Resolution.P720) resolution = Resolution.All[Resolution.All.ToList().IndexOf(resolution) - 1];
                else if (fps > 30) fps = 30;
                else break;
                bitrate = Quality.TargetBitrateKbps(resolution, fps, encoder.Codec);
            }
            bitrate = Math.Min(bitrate, Math.Max(2_000, cap));
            if (network.MeasuredMbps is null)
                notes.Add($"Bitrate limitado pela velocidade do adaptador ({budgetMbps} Mbps). Rode o teste de banda com um viewer conectado para um ajuste mais preciso.");
            else
                notes.Add($"Banda medida até o viewer: {budgetMbps:0} Mbps. Bitrate limitado a 70% disso.");
        }

        if (network.RadminAddress is null)
            notes.Add("Radmin VPN não detectado. Instale e entre numa rede para transmitir pela internet.");

        // Mais FEC em links lentos/instáveis, menos em links rápidos.
        var fec = budgetMbps is > 0 and < 50 ? 6 : 10;

        return new AutoConfigResult(resolution, fps, encoder.Codec, encoder.FfmpegName, bitrate, fec, notes);
    }

    private static bool SupportsHighRate4K(HardwareProfile hardware, EncoderInfo encoder)
    {
        var gpu = hardware.Gpus.FirstOrDefault(g => g.Vendor == encoder.Vendor)?.Name ?? string.Empty;
        return encoder.Vendor switch
        {
            // Gerações com NVENC de 8ª geração ou mais nova (Ada/Blackwell) ou dois encoders.
            EncoderVendor.Nvidia => gpu.Contains("RTX 40", StringComparison.OrdinalIgnoreCase) || gpu.Contains("RTX 50", StringComparison.OrdinalIgnoreCase)
                                   || gpu.Contains("RTX 6000 Ada", StringComparison.OrdinalIgnoreCase),
            EncoderVendor.Amd => gpu.Contains("RX 7", StringComparison.OrdinalIgnoreCase) || gpu.Contains("RX 9", StringComparison.OrdinalIgnoreCase),
            EncoderVendor.Intel => gpu.Contains("Arc", StringComparison.OrdinalIgnoreCase),
            _ => false,
        };
    }

    public static void Apply(AutoConfigResult result, AppSettings settings)
    {
        settings.Resolution = result.Resolution.Label;
        settings.Fps = result.Fps;
        settings.PreferredCodec = result.Codec;
        settings.PreferredEncoder = result.Encoder;
        settings.BitrateKbps = 0; // automático pela tabela, o controle adaptativo cuida do resto
        settings.FecGroupSize = result.FecGroupSize;
        settings.AutoConfigured = true;
        settings.LastAutoConfig = DateTimeOffset.Now;
    }
}
