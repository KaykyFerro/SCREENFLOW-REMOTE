using System.Text.Json;
using System.Text.Json.Serialization;
using ScreenFlow.Core.Diagnostics;
using ScreenFlow.Core.Media;

namespace ScreenFlow.Core.Settings;

public enum AppTheme { Dark, Light }

public sealed class AppSettings
{
    public AppTheme Theme { get; set; } = AppTheme.Dark;
    public string DeviceName { get; set; } = Environment.MachineName;

    // Rede
    public int MediaPort { get; set; } = AppInfo.DefaultMediaPort;
    public int ControlPort { get; set; } = AppInfo.DefaultControlPort;
    public int DiscoveryPort { get; set; } = AppInfo.DefaultDiscoveryPort;
    public bool FirewallConfigured { get; set; }
    public bool AllowNonRadminFallback { get; set; }

    // Transmissão
    /// <summary>O que transmitir: tela (monitor ou janela), câmera ou só o áudio (festa de música).</summary>
    public StreamMode Mode { get; set; } = StreamMode.Screen;
    public string Resolution { get; set; } = Media.Resolution.P1080.Label;
    public int Fps { get; set; } = 60;
    public int BitrateKbps { get; set; } // 0 = automático pela tabela de perfis
    public VideoCodec? PreferredCodec { get; set; }
    public string? PreferredEncoder { get; set; }
    public bool AudioEnabled { get; set; } = true;
    public int AudioBitrateKbps { get; set; } = 160;
    public bool AdaptiveQuality { get; set; } = true;
    public int FecGroupSize { get; set; } = 10;

    // Câmera e festa de música
    public string? CameraId { get; set; }
    public string CameraResolution { get; set; } = Media.Resolution.P1080.Label;
    public int CameraFps { get; set; } = 30;
    /// <summary>Bitrate do Opus no modo festa de música (só áudio, qualidade de música).</summary>
    public int MusicBitrateKbps { get; set; } = 256;
    /// <summary>No modo tela, manda a câmera junto (quem assiste vê num quadrinho por cima da tela).</summary>
    public bool CameraWithScreen { get; set; }

    // Som do PC
    /// <summary>
    /// Separa o som do PC por app (Windows 10 2004 ou mais novo): a voz da rede, os sons do ScreenFlow e os apps de
    /// <see cref="MutedApps"/> ficam fora da transmissão. Desligado, vai todo o som do PC, como nas versões antigas.
    /// </summary>
    public bool SeparateAppAudio { get; set; } = true;
    /// <summary>Apps cujo som não vai na transmissão (Spotify, Discord...).</summary>
    public List<MutedApp> MutedApps { get; set; } = [];
    /// <summary>Festa de música com o som de um app só (o Spotify, por exemplo). Null: todo o som do PC.</summary>
    public MutedApp? PartyApp { get; set; }

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
    /// <summary>Desenha o vídeo direto na GPU (D3D11). Desligado, usa o caminho antigo pela CPU.</summary>
    public bool GpuVideoRenderer { get; set; } = true;
    /// <summary>Canto do quadrinho da câmera no player: 0 em cima à direita, 1 embaixo à direita, 2 embaixo à esquerda.</summary>
    public int PlayerCameraCorner { get; set; }

    // Bandeja
    /// <summary>Fechar e minimizar levam o app para a bandeja (perto do relógio) em vez de fechar.</summary>
    public bool MinimizeToTray { get; set; } = true;
    public bool TrayHintShown { get; set; }
    /// <summary>Quando a transmissão acaba (ou o player fecha) com o ScreenFlow na bandeja, a janela volta sozinha.</summary>
    public bool ShowWindowWhenSessionEnds { get; set; } = true;

    // Sala da rede: voz e chat com todo mundo que está na mesma rede
    /// <summary>Identifica este PC na sala da rede. Gerado na primeira vez que o app abre.</summary>
    public string? InstanceId { get; set; }
    /// <summary>Microfone da voz (id do Windows). Null usa o microfone padrão.</summary>
    public string? MicrophoneId { get; set; }
    public double VoiceVolume { get; set; } = 1.0;
    public bool VoiceMuted { get; set; }
    public bool VoiceSounds { get; set; } = true;
    /// <summary>Sensibilidade do microfone: 0 capta só voz alta, 1 capta tudo.</summary>
    public double VoiceSensitivity { get; set; } = 0.6;
    /// <summary>IPs adicionados à mão na sala (para redes em que o aviso automático não chega).</summary>
    public List<string> RoomPeers { get; set; } = [];

    // Configuração automática e atualização
    public bool AutoConfigured { get; set; }
    public DateTimeOffset? LastAutoConfig { get; set; }
    public bool CheckUpdatesOnStart { get; set; } = true;
    public string? SkippedVersion { get; set; }
    public DateTimeOffset? LastUpdateCheck { get; set; }

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
        if (!Enum.IsDefined(Mode)) Mode = StreamMode.Screen;
        Resolution = Media.Resolution.FromLabel(Resolution).Label;
        CameraResolution = Media.Resolution.FromLabel(CameraResolution).Label;
        if (!Quality.CameraFrameRates.Contains(CameraFps)) CameraFps = 30;
        MusicBitrateKbps = Math.Clamp(MusicBitrateKbps, AudioProfile.MinMusicKbps, AudioProfile.MaxMusicKbps);
        if (string.IsNullOrWhiteSpace(CameraId)) CameraId = null;
        if (PlayerCameraCorner is < 0 or > 2) PlayerCameraCorner = 0;
        BitrateKbps = BitrateKbps <= 0 ? 0 : Math.Clamp(BitrateKbps, 1_000, 200_000);
        AudioBitrateKbps = Math.Clamp(AudioBitrateKbps, 64, 256);
        FecGroupSize = Math.Clamp(FecGroupSize, 0, 32);
        MaxViewers = Math.Clamp(MaxViewers, 1, 64);
        UploadMbps = Math.Clamp(UploadMbps, 0, 100_000);
        Volume = Math.Clamp(Volume, 0, 1);
        if (!Security.SessionCrypto.IsValidPin(Pin)) Pin = null;
        if (string.IsNullOrWhiteSpace(DeviceName)) DeviceName = Environment.MachineName;
        RecentHosts ??= [];
        if (InstanceId is null || !Guid.TryParseExact(InstanceId, "N", out _)) InstanceId = Guid.NewGuid().ToString("N");
        if (string.IsNullOrWhiteSpace(MicrophoneId)) MicrophoneId = null;
        VoiceVolume = Math.Clamp(VoiceVolume, 0, 1.5);
        VoiceSensitivity = Math.Clamp(VoiceSensitivity, 0, 1);
        RoomPeers = (RoomPeers ?? []).Select(p => p.Trim()).Where(p => p.Length is > 0 and <= 64).Distinct(StringComparer.OrdinalIgnoreCase).Take(16).ToList();
        MutedApps = MutedApp.Normalize(MutedApps);
        PartyApp = MutedApp.Normalize([PartyApp]).FirstOrDefault();
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
