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

    /// <summary>Versão do driver como o fabricante mostra ("552.22" na NVIDIA), ou a do Windows ("31.0.15.5222"). Null se não deu para ler.</summary>
    public string? Driver { get; init; }

    /// <summary>
    /// O Windows achou a placa mas ela não está funcionando (driver com erro, desativada): o motivo, para mostrar no
    /// cartão. Null quando está tudo certo.
    /// </summary>
    public string? Problem { get; init; }

    /// <summary>
    /// A placa principal: as de verdade antes das virtuais e do vídeo básico da Microsoft (que aparecem como
    /// "Software"), e entre elas a com mais memória de vídeo (a dedicada, não a integrada).
    /// </summary>
    public static GpuInfo? Main(IEnumerable<GpuInfo> gpus)
        => gpus.OrderBy(g => g.Vendor == EncoderVendor.Software).ThenByDescending(RankingMemory).ThenBy(g => g.Problem is not null).FirstOrDefault();

    /// <summary>
    /// A memória que conta para escolher a principal. Uma NVIDIA ou AMD com problema que não informa a memória (sem o driver
    /// dela, o Windows não sabe) conta como uma dedicada de 4 GB: é ela que a pessoa espera ver no cartão, não a integrada.
    /// </summary>
    private static long RankingMemory(GpuInfo gpu)
        => gpu is { Problem: not null, DedicatedVideoMemoryMb: <= 0, Vendor: EncoderVendor.Nvidia or EncoderVendor.Amd } ? 4096 : gpu.DedicatedVideoMemoryMb;

    /// <summary>
    /// A versão do driver da NVIDIA a partir da do Windows: "31.0.15.5222" vira "552.22" (os 5 últimos dígitos das duas
    /// últimas partes). Null se o texto não tiver esse formato.
    /// </summary>
    public static string? NvidiaVersion(string? windowsVersion)
    {
        var parts = (windowsVersion ?? string.Empty).Split('.');
        // A NVIDIA usa sempre 2 dígitos na terceira parte e 4 na última ("31.0.15.5222").
        if (parts.Length != 4 || parts[2].Length != 2 || parts[3].Length != 4 || !parts.All(p => p.Length > 0 && p.All(char.IsAsciiDigit))) return null;
        var digits = parts[2] + parts[3];
        if (digits.Length < 5) return null;
        var last = digits[^5..];
        return $"{int.Parse(last[..3])}.{last[3..]}";
    }

    /// <summary>
    /// Por que o encoder da placa não abriu, numa frase para o cartão, a partir das mensagens do FFmpeg ao tentar abrir.
    /// Null quando as mensagens não dizem nada conhecido.
    /// </summary>
    public static string? EncoderHint(EncoderVendor vendor, IEnumerable<string> messages)
    {
        var text = string.Join(" ", messages);
        if (text.Length == 0) return null;
        if (vendor == EncoderVendor.Nvidia)
        {
            if (Regex.IsMatch(text, @"required nvenc API version|minimum required Nvidia driver|nvenc API version", RegexOptions.IgnoreCase))
            {
                var minimum = Regex.Match(text, @"driver for nvenc is (\d{3}\.\d{2})", RegexOptions.IgnoreCase);
                return minimum.Success ? $"o driver da NVIDIA é antigo para o NVENC: atualize para o {minimum.Groups[1].Value} ou mais novo" : "o driver da NVIDIA é antigo para o NVENC: atualize o driver";
            }
            if (Regex.IsMatch(text, @"out of memory|incompatible client key|OpenEncodeSessionEx failed", RegexOptions.IgnoreCase))
                return "o NVENC está ocupado por outro app (OBS, Discord, gravação da NVIDIA): feche e detecte de novo";
            if (Regex.IsMatch(text, @"Cannot load nvcuda|Cannot load nvEncodeAPI|CUDA_ERROR", RegexOptions.IgnoreCase))
                return "o driver da NVIDIA não carregou o NVENC: reinstale o driver";
            if (Regex.IsMatch(text, @"No capable devices found|No NVENC capable devices", RegexOptions.IgnoreCase))
                return "a placa não tem NVENC para esse formato";
        }
        if (vendor == EncoderVendor.Amd && Regex.IsMatch(text, @"amfrt|AMF", RegexOptions.IgnoreCase)) return "o driver da AMD não abriu o AMF: atualize o driver";
        if (vendor == EncoderVendor.Intel && Regex.IsMatch(text, @"mfx|qsv|vpl", RegexOptions.IgnoreCase)) return "o driver da Intel não abriu o Quick Sync: atualize o driver";
        return null;
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
    string OsVersion)
{
    /// <summary>Encoders por hardware que não abriram e as mensagens do FFmpeg de cada um ("hevc_nvenc" → mensagens).</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> EncoderFailures { get; init; } = new Dictionary<string, IReadOnlyList<string>>();

    /// <summary>A placa principal (veja <see cref="GpuInfo.Main"/>).</summary>
    public GpuInfo? MainGpu => GpuInfo.Main(Gpus);

    /// <summary>O encoder da placa principal (o melhor formato dela), ou null se ela não tiver um que abriu.</summary>
    public EncoderInfo? MainGpuEncoder => MainGpu is { } gpu
        ? Encoders.Where(e => e.Hardware && e.Vendor == gpu.Vendor).OrderByDescending(e => e.Codec).FirstOrDefault()
        : null;

    /// <summary>Por que a placa principal ficou sem encoder (para o cartão), ou null.</summary>
    public string? MainGpuEncoderHint => MainGpu is { } gpu && MainGpuEncoder is null
        ? GpuInfo.EncoderHint(gpu.Vendor, EncoderFailures.Where(f => VendorOf(f.Key) == gpu.Vendor).SelectMany(f => f.Value))
        : null;

    /// <summary>O fabricante pelo nome do encoder do FFmpeg ("hevc_nvenc" → NVIDIA).</summary>
    public static EncoderVendor VendorOf(string ffmpegName) => ffmpegName switch
    {
        _ when ffmpegName.EndsWith("_nvenc", StringComparison.Ordinal) => EncoderVendor.Nvidia,
        _ when ffmpegName.EndsWith("_amf", StringComparison.Ordinal) => EncoderVendor.Amd,
        _ when ffmpegName.EndsWith("_qsv", StringComparison.Ordinal) => EncoderVendor.Intel,
        _ => EncoderVendor.Software,
    };
}

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
