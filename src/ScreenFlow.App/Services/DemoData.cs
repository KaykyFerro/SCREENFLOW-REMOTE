using System.Net;
using ScreenFlow.Core;
using ScreenFlow.Core.Media;
using ScreenFlow.Core.Protocol;
using ScreenFlow.Core.Settings;
using ScreenFlow.Infrastructure.Network;
using ScreenFlow.Infrastructure.Session;

namespace ScreenFlow.App.Services;

/// <summary>Dados de exemplo para as capturas de tela do CI (o runner não tem GPU, Radmin nem câmera).</summary>
public static class DemoData
{
    public static HardwareProfile Hardware { get; } = new(
        [new GpuInfo("NVIDIA GeForce RTX 2060", EncoderVendor.Nvidia, 6144), new GpuInfo("Parsec Virtual Display Adapter", EncoderVendor.Software, 0)],
        [
            new EncoderInfo("hevc_nvenc", VideoCodec.Hevc, EncoderVendor.Nvidia, true, 4096, 4096),
            new EncoderInfo("h264_nvenc", VideoCodec.H264, EncoderVendor.Nvidia, true, 4096, 4096),
        ],
        [VideoCodec.H264, VideoCodec.Hevc],
        12, 32768, 2560, 1440, 165, "Windows 11 Pro");

    public static IReadOnlyList<DisplayMonitor> Monitors { get; } =
    [
        new DisplayMonitor(@"\\.\DISPLAY1", "Monitor principal", 2560, 1440, 165, true, 0, 0),
        new DisplayMonitor(@"\\.\DISPLAY2", "Monitor 2", 1920, 1080, 60, false, 2560, 0),
    ];

    public static NetworkAdapterInfo Radmin { get; } = new("Radmin VPN", "Famatech Radmin VPN Ethernet Adapter",
        IPAddress.Parse("26.48.121.7"), IPAddress.Parse("255.0.0.0"), 100, true, true);

    /// <summary>Medidor alimentado com um acorde, para o visualizador ter forma de música de verdade.</summary>
    public static AudioMeter Meter(int bands = 48)
    {
        var meter = new AudioMeter(bands);
        var samples = new float[8192 * 2];
        double[] tones = [55, 110, 220, 330, 440, 660, 880, 1760, 3520, 7040];
        for (var i = 0; i < samples.Length / 2; i++)
        {
            var t = i / (double)AudioProfile.SampleRate;
            var v = 0.0;
            for (var k = 0; k < tones.Length; k++) v += Math.Sin(2 * Math.PI * tones[k] * t) * (0.5 / (k + 1));
            samples[i * 2] = samples[i * 2 + 1] = (float)(v * 0.45);
        }
        meter.AddInterleaved(samples);
        return meter;
    }

    /// <summary>Medidor que continua recebendo "música" (um acorde que pulsa), para o visualizador não cair a zero.</summary>
    public static AudioMeter LiveMeter(int bands = 48)
    {
        var meter = new AudioMeter(bands);
        var block = new float[960 * 2];
        double[] tones = [55, 82.4, 110, 164.8, 220, 329.6, 440, 659.3, 880, 1318.5, 1760, 2637, 3520, 5274, 7040, 10548];
        long position = 0;
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(20) };
        timer.Tick += (_, _) =>
        {
            for (var i = 0; i < block.Length / 2; i++)
            {
                var t = (position + i) / (double)AudioProfile.SampleRate;
                var v = 0.0;
                for (var k = 0; k < tones.Length; k++)
                {
                    var pulse = 0.55 + 0.45 * Math.Sin(t * (1.7 + k * 0.37) + k);
                    v += Math.Sin(2 * Math.PI * tones[k] * t) * pulse * (0.6 / (1 + k * 0.45));
                }
                block[i * 2] = block[i * 2 + 1] = (float)(v * 0.32);
            }
            position += block.Length / 2;
            meter.AddInterleaved(block);
        };
        timer.Start();
        return meter;
    }

    /// <summary>Transmissões "encontradas na rede" para a tela Assistir.</summary>
    public static IReadOnlyList<DiscoveryAnnouncement> Sessions { get; } =
    [
        new(AppInfo.Name, AppInfo.ProtocolVersion, "PC-do-Lucas", "26.40.18.100", AppInfo.DefaultControlPort, AppInfo.DisplayVersion, false, 3, StreamMode.Screen),
        new(AppInfo.Name, AppInfo.ProtocolVersion, "Festa da Mari", "26.47.19.113", AppInfo.DefaultControlPort, AppInfo.DisplayVersion, true, 6, StreamMode.AudioOnly),
        new(AppInfo.Name, AppInfo.ProtocolVersion, "Estúdio", "26.54.20.126", AppInfo.DefaultControlPort, AppInfo.DisplayVersion, false, 1, StreamMode.Camera),
    ];

    /// <summary>Pessoas assistindo, para a tela Transmissão e o Monitoramento.</summary>
    public static IReadOnlyList<ViewerInfo> Audience(int second = 0) =>
    [
        new(new Guid("6f1c1d1e-0000-0000-0000-000000000001"), "Lucas", "26.40.18.100", ViewerState.Streaming, 28 + Wave(second, 7) * 4, 0.000, 38.1, 41 + Wave(second, 5) * 6),
        new(new Guid("6f1c1d1e-0000-0000-0000-000000000002"), "Mari", "26.47.19.113", ViewerState.Streaming, 37 + Wave(second, 9) * 5, 0.001, 37.5, 52 + Wave(second, 6) * 7),
        new(new Guid("6f1c1d1e-0000-0000-0000-000000000003"), "PC-da-sala", "26.54.20.126", ViewerState.Streaming, 46 + Wave(second, 11) * 6, 0.002, 36.9, 63 + Wave(second, 8) * 8),
    ];

    /// <summary>Preenche 90 segundos de histórico de uma transmissão 1440p a 165 FPS para três pessoas.</summary>
    public static void FillTelemetry()
    {
        Telemetry.EndHost();
        var target = new StreamTarget(40_000, 165, Resolution.P1440);
        for (var second = 0; second < 90; second++)
        {
            var dip = second is >= 52 and < 57 ? 1.0 : 0.0; // alguém abriu um download: a qualidade se ajusta e volta
            var stats = new HostStatistics(3,
                164.2 + Wave(second, 3) * 0.8 - dip * 22,
                38.4 + Wave(second, 10) * 2.4 - dip * 9,
                116 + Wave(second, 10) * 7 - dip * 27,
                "hevc_nvenc", "Windows.Graphics.Capture", target, dip > 0 ? "perda de pacotes" : "rede estável", StreamMode.Screen, 160);
            Telemetry.Record(stats, Audience(second));
        }
    }

    /// <summary>Estatísticas de quem assiste, para o player e o Monitoramento.</summary>
    public static ViewerStatistics ViewerStats(StreamMode mode = StreamMode.Screen) => mode == StreamMode.AudioOnly
        ? new ViewerStatistics(0, 0, 0, 24, 0.001, "Opus", 0, 0, "Opus 256 kbps", 0, 0, "rede estável", StreamMode.AudioOnly, 62, 3, 512, false)
        : new ViewerStatistics(164.6, 38.2, 21, 24, 0.0004, "HEVC", 2560, 1440, "NVDEC (D3D11VA)", 2, 17, "rede estável", mode, 58, 0, 160, true);

    private static double Wave(int second, double period) => Math.Sin(second / period) * 0.6 + Math.Sin(second / (period * 0.37) + 1.3) * 0.4;
}
