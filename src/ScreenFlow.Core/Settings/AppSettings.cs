using System.Text.Json;
using System.Text.Json.Serialization;
using ScreenFlow.Core.Diagnostics;
using ScreenFlow.Core.Media;

namespace ScreenFlow.Core.Settings;

public enum ThemeMode { Dark, Light }

public sealed class AppSettings
{
    public ThemeMode Theme { get; set; } = ThemeMode.Dark;
    public string DeviceName { get; set; } = Environment.MachineName;

    // Rede
    public int MediaPort { get; set; } = AppInfo.DefaultMediaPort;
    public int ControlPort { get; set; } = AppInfo.DefaultControlPort;
    public int DiscoveryPort { get; set; } = AppInfo.DefaultDiscoveryPort;
    public bool FirewallConfigured { get; set; }
    public bool AllowNonRadminFallback { get; set; }

    // Transmissão
    public string Resolution { get; set; } = Media.Resolution.P1080.Label;
    public int Fps { get; set; } = 60;
    public int BitrateKbps { get; set; } // 0 = automático pela tabela de perfis
    public VideoCodec? PreferredCodec { get; set; }
    public string? PreferredEncoder { get; set; }
    public bool AudioEnabled { get; set; } = true;
    public int AudioBitrateKbps { get; set; } = 160;
    public bool AdaptiveQuality { get; set; } = true;
    public int FecGroupSize { get; set; } = 10;

    // Segurança
    public string? Pin { get; set; }
    public bool ApproveEachViewer { get; set; } = true;
    public int MaxViewers { get; set; } = 20;

    /// <summary>Upload do host em Mbps (medido pelo teste de banda ou digitado). 0 = desconhecido.</summary>
    public double UploadMbps { get; set; }

    // Viewer
    public List<string> RecentHosts { get; set; } = [];
    public double Volume { get; set; } = 0.8;
    public bool ShowStatsOverlay { get; set; }

    // Configuração automática e atualização
    public bool AutoConfigured { get; set; }
    public DateTimeOffset? LastAutoConfig { get; set; }
    public bool CheckUpdatesOnStart { get; set; } = true;
    public string? SkippedVersion { get; set; }

    public void RememberHost(string host)
    {
        host = host.Trim();
        if (host.Length == 0) return;
        RecentHosts.RemoveAll(h => string.Equals(h, host, StringComparison.OrdinalIgnoreCase));
        RecentHosts.Insert(0, host);
        if (RecentHosts.Count > 8) RecentHosts.RemoveRange(8, RecentHosts.Count - 8);
    }

    public void Normalize()
    {
        MediaPort = ValidPort(MediaPort, AppInfo.DefaultMediaPort);
        ControlPort = ValidPort(ControlPort, AppInfo.DefaultControlPort);
        DiscoveryPort = ValidPort(DiscoveryPort, AppInfo.DefaultDiscoveryPort);
        if (!Quality.FrameRates.Contains(Fps)) Fps = 60;
        Resolution = Media.Resolution.FromLabel(Resolution).Label;
        BitrateKbps = BitrateKbps <= 0 ? 0 : Math.Clamp(BitrateKbps, 1_000, 200_000);
        AudioBitrateKbps = Math.Clamp(AudioBitrateKbps, 64, 256);
        FecGroupSize = Math.Clamp(FecGroupSize, 0, 32);
        MaxViewers = Math.Clamp(MaxViewers, 1, 64);
        UploadMbps = Math.Clamp(UploadMbps, 0, 100_000);
        Volume = Math.Clamp(Volume, 0, 1);
        if (!Security.SessionCrypto.IsValidPin(Pin)) Pin = null;
        if (string.IsNullOrWhiteSpace(DeviceName)) DeviceName = Environment.MachineName;
        RecentHosts ??= [];
    }

    private static int ValidPort(int port, int fallback) => port is >= 1024 and <= 65535 ? port : fallback;
}

public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _path;
    private readonly object _gate = new();

    public SettingsStore(string? path = null)
    {
        _path = path ?? Path.Combine(AppInfo.DataDirectory, "settings.json");
        Current = Load();
        IsFirstRun = !File.Exists(_path);
    }

    public AppSettings Current { get; private set; }
    public bool IsFirstRun { get; }
    public string FilePath => _path;

    public event Action<AppSettings>? Saved;

    private AppSettings Load()
    {
        try
        {
            if (File.Exists(_path))
            {
                var loaded = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(_path), Options) ?? new AppSettings();
                loaded.Normalize();
                return loaded;
            }
        }
        catch (Exception exception)
        {
            Log.Warn($"Configurações corrompidas, usando padrão: {exception.Message}");
        }
        var settings = new AppSettings();
        settings.Normalize();
        return settings;
    }

    public void Save()
    {
        lock (_gate)
        {
            Current.Normalize();
            var temp = _path + ".tmp";
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(temp, JsonSerializer.Serialize(Current, Options));
            File.Move(temp, _path, overwrite: true);
        }
        Saved?.Invoke(Current);
    }
}
