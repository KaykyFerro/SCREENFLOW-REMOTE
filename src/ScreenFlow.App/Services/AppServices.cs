using System.Text;
using ScreenFlow.Core;
using ScreenFlow.Core.Diagnostics;
using ScreenFlow.Core.Media;
using ScreenFlow.Core.Settings;
using ScreenFlow.Infrastructure.Capture;
using ScreenFlow.Infrastructure.Hardware;
using ScreenFlow.Infrastructure.Media;
using ScreenFlow.Infrastructure.Network;

namespace ScreenFlow.App.Services;

/// <summary>Serviços compartilhados entre as telas.</summary>
public static class AppServices
{
    private static Task<HardwareProfile>? _hardware;

    public static SettingsStore Settings { get; private set; } = null!;

    public static void Initialize() => Settings = new SettingsStore();

    /// <summary>Detecção de hardware em segundo plano (abre encoders de verdade, leva ~1–2 s).</summary>
    public static Task<HardwareProfile> Hardware => _hardware ??= Task.Run(() => HardwareDetector.Detect());

    public static Task<HardwareProfile> RefreshHardware() => _hardware = Task.Run(() => HardwareDetector.Detect(refresh: true));

    public static NetworkProfile CurrentNetwork()
    {
        var radmin = RadminNetwork.FindRadmin();
        double? measured = Settings.Current.UploadMbps > 0 ? Settings.Current.UploadMbps : null;
        return new NetworkProfile(radmin?.Address.ToString(), radmin?.SpeedMbps ?? 0, measured);
    }

    /// <summary>Detecta tudo e aplica a melhor configuração. Retorna o resultado para mostrar ao usuário.</summary>
    public static async Task<AutoConfigResult> AutoConfigureAsync(bool refresh = false)
    {
        var hardware = await (refresh ? RefreshHardware() : Hardware);
        var result = AutoConfigurator.Recommend(hardware, CurrentNetwork());
        AutoConfigurator.Apply(result, Settings.Current);
        Settings.Save();
        Log.Info($"Configuração automática: {result.Resolution.Label} {result.Fps} FPS {result.Encoder} {result.BitrateKbps} kbps");
        return result;
    }

    public static async Task<string> BuildDiagnosticsAsync()
    {
        var builder = new StringBuilder();
        builder.AppendLine($"{AppInfo.Name} {AppInfo.DisplayVersion} — diagnóstico gerado em {DateTime.Now:dd/MM/yyyy HH:mm:ss}");
        builder.AppendLine($"Windows: {Environment.OSVersion.VersionString}");
        builder.AppendLine();

        var radmin = RadminNetwork.FindRadmin();
        builder.AppendLine("REDE");
        builder.AppendLine(radmin is null
            ? (RadminNetwork.IsRadminInstalled() ? "  Radmin VPN instalado, mas sem IP 26.x ativo (entre numa rede no Radmin)." : "  Radmin VPN não encontrado.")
            : $"  Radmin VPN: {radmin.Address} (máscara {radmin.Mask}, link {radmin.SpeedMbps} Mbps)");
        foreach (var adapter in RadminNetwork.GetIPv4Adapters())
            builder.AppendLine($"  {(adapter.IsUp ? "●" : "○")} {adapter.Description}: {adapter.Address}{(adapter.IsRadmin ? " [Radmin]" : "")}");
        var s = Settings.Current;
        builder.AppendLine($"  Portas: mídia UDP {s.MediaPort}, controle TCP {s.ControlPort}, descoberta UDP {s.DiscoveryPort}");
        builder.AppendLine($"  Firewall: {(FirewallManager.RulesExist(s.MediaPort, s.ControlPort, s.DiscoveryPort) ? "regras criadas" : "regras NÃO encontradas")}");
        builder.AppendLine($"  Upload medido: {(s.UploadMbps > 0 ? $"{s.UploadMbps:0} Mbps" : "ainda não medido (use 'Testar banda' com um viewer conectado)")}");
        builder.AppendLine();

        var hardware = await Hardware;
        builder.AppendLine("HARDWARE");
        foreach (var gpu in hardware.Gpus) builder.AppendLine($"  GPU: {gpu.Name} ({gpu.Vendor}, {gpu.DedicatedVideoMemoryMb} MB)");
        builder.AppendLine($"  CPU: {hardware.CpuCores} núcleos lógicos · RAM: {hardware.MemoryMb / 1024.0:0.0} GB");
        foreach (var monitor in MonitorDiscovery.GetMonitors()) builder.AppendLine($"  {monitor.DisplayLabel}");
        builder.AppendLine();

        builder.AppendLine($"CODECS (FFmpeg {(FFmpegLoader.EnsureLoaded() ? FFmpegLoader.Version : "não carregado: " + FFmpegLoader.Error)})");
        builder.AppendLine("  Encoders disponíveis: " + (hardware.Encoders.Count == 0 ? "nenhum" : string.Join(", ", hardware.Encoders.Select(e => e.Label))));
        builder.AppendLine("  Decoders por hardware (D3D11): " + (hardware.HardwareDecoders.Count == 0 ? "nenhum" : string.Join(", ", hardware.HardwareDecoders.Select(Quality.CodecLabel))));
        builder.AppendLine("  Captura: " + (WgcCapture.IsSupported ? "Windows.Graphics.Capture" : "DXGI Desktop Duplication"));
        builder.AppendLine();

        var recommendation = AutoConfigurator.Recommend(hardware, CurrentNetwork());
        builder.AppendLine("PERFIL RECOMENDADO");
        builder.AppendLine($"  {recommendation.Resolution.Label} · {recommendation.Fps} FPS · {Quality.CodecLabel(recommendation.Codec)} ({recommendation.Encoder}) · {recommendation.BitrateKbps / 1000.0:0.#} Mbps");
        foreach (var note in recommendation.Notes) builder.AppendLine($"  • {note}");
        builder.AppendLine();
        builder.AppendLine($"Logs: {Log.Directory}");
        return builder.ToString();
    }
}
