using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ScreenFlow.App.Services;
using ScreenFlow.Core.Diagnostics;
using ScreenFlow.Core.Media;
using ScreenFlow.Core.Settings;
using ScreenFlow.Infrastructure.Capture;

namespace ScreenFlow.App.Pages;

/// <summary>Visão geral: começar a transmitir (tela, câmera ou festa de música), conectar em alguém e o resumo do PC.</summary>
public partial class OverviewPage : UserControl, IShellPage
{
    private readonly MainWindow _shell;
    private StreamMode _mode = StreamMode.Screen;
    private bool _loading;
    private bool _camerasLoaded;

    public OverviewPage(MainWindow shell)
    {
        _shell = shell;
        InitializeComponent();
        ResolutionCombo.Fill(Resolution.All.Select(r => new Choice<Resolution>(r, $"{r.Label} · {r.Width} × {r.Height}")));
        FpsCombo.Fill(Quality.FrameRates.Select(f => new Choice<int>(f, $"{f} FPS")));
        CameraQualityCombo.Fill(CameraQualities);
        MusicCombo.Fill(AudioProfile.MusicBitrates.Select(k => new Choice<int>(k, MusicLabel(k))));
        ResolutionCombo.SelectionChanged += (_, _) => UpdateProfileCard();
        FpsCombo.SelectionChanged += (_, _) => UpdateProfileCard();
        CameraQualityCombo.SelectionChanged += (_, _) => UpdateProfileCard();
        MusicCombo.SelectionChanged += (_, _) => UpdateProfileCard();
        SizeChanged += (_, _) => EnvGrid.Columns = ActualWidth >= 1060 ? 4 : 2;
        shell.NetworkStatusChanged += UpdateNetworkCard;
        AppServices.RadminSetup.Changed += UpdateNetworkCard;
        shell.LiveStateChanged += UpdateLiveState;
    }

    /// <summary>Qualidades comuns de webcam (a câmera usa o formato mais próximo que ela tiver).</summary>
    public static IReadOnlyList<Choice<(Resolution Resolution, int Fps)>> CameraQualities { get; } =
    [
        new((Resolution.P720, 30), "720p · 30 FPS"),
        new((Resolution.P1080, 30), "1080p · 30 FPS"),
        new((Resolution.P1080, 60), "1080p · 60 FPS"),
        new((Resolution.P1440, 30), "1440p · 30 FPS"),
        new((Resolution.P2160, 30), "4K · 30 FPS"),
    ];

    public static string MusicLabel(int kbps) => kbps switch
    {
        >= 320 => "320 kbps · máxima",
        >= 256 => "256 kbps · excelente",
        >= 192 => "192 kbps · muito boa",
        _ => $"{kbps} kbps · boa",
    };

    private AppSettings Settings => AppServices.Settings.Current;

    public void OnShown()
    {
        if (!_shell.IsTransmitting) LoadFromSettings();
        RefreshEnvironment();
        RefreshRecent();
        UpdateLiveState();
    }

    private void LoadFromSettings()
    {
        _loading = true;
        ResolutionCombo.SelectValue(Resolution.FromLabel(Settings.Resolution));
        FpsCombo.SelectValue(Settings.Fps, f => f);
        CameraQualityCombo.SelectValue((Resolution.FromLabel(Settings.CameraResolution), Settings.CameraFps));
        MusicCombo.SelectValue(Settings.MusicBitrateKbps, k => k);
        AudioCheck.IsChecked = Settings.AudioEnabled;
        CameraCheck.IsChecked = Settings.CameraWithScreen;
        _loading = false;
        (Settings.Mode switch
        {
            StreamMode.Camera => ChipCamera,
            StreamMode.AudioOnly => ChipParty,
            _ => ChipScreen,
        }).IsChecked = true;
        ApplyMode(Settings.Mode);
    }

    private void Mode_Checked(object sender, RoutedEventArgs e)
    {
        if (!IsInitialized) return;
        ApplyMode(sender == ChipCamera ? StreamMode.Camera : sender == ChipParty ? StreamMode.AudioOnly : StreamMode.Screen);
    }

    private void ApplyMode(StreamMode mode)
    {
        _mode = mode;
        ScreenFields.Visibility = mode == StreamMode.Screen ? Visibility.Visible : Visibility.Collapsed;
        CameraFields.Visibility = mode == StreamMode.Camera ? Visibility.Visible : Visibility.Collapsed;
        PartyFields.Visibility = mode == StreamMode.AudioOnly ? Visibility.Visible : Visibility.Collapsed;
        AudioCheck.Visibility = mode == StreamMode.AudioOnly ? Visibility.Collapsed : Visibility.Visible;
        CameraCheck.Visibility = mode == StreamMode.Screen ? Visibility.Visible : Visibility.Collapsed;
        (HeroTitle.Text, HeroText.Text, HeroGlyph.Text) = mode switch
        {
            StreamMode.Camera => ("Compartilhar câmera", "Mostre a sua webcam ou placa de captura. A câmera só liga quando alguém entra.", ""),
            StreamMode.AudioOnly => ("Festa de música", "Toque qualquer música no PC e todo mundo ouve junto, sem vídeo e com som de alta qualidade.", ""),
            _ => ("Compartilhar tela", "Escolha a qualidade e inicie. O som do PC e a sua câmera podem ir junto. Quem estiver na sua rede Radmin entra com o seu IP.", ""),
        };
        StartGlyph.Text = HeroGlyph.Text;
        HeroTint.SetResourceReference(Border.BackgroundProperty, mode == StreamMode.AudioOnly ? "PartyGradient" : "CameraGradient");
        HeroTint.Opacity = mode == StreamMode.Screen ? 0 : 0.3;
        if (mode == StreamMode.Camera) _ = LoadCamerasAsync();
        UpdateProfileCard();
        UpdateLiveState();
    }

    private async Task LoadCamerasAsync()
    {
        if (_camerasLoaded) return;
        _camerasLoaded = true;
        IReadOnlyList<CameraDevice> cameras = AppServices.DemoMode ? [new CameraDevice("demo", "Logitech C920 HD Pro")] : await CameraDiscovery.GetCamerasAsync();
        CameraCombo.Items.Clear();
        foreach (var camera in cameras) CameraCombo.Items.Add(camera);
        CameraCombo.SelectedItem = cameras.FirstOrDefault(c => c.Id == Settings.CameraId) ?? cameras.FirstOrDefault();
        if (cameras.Count == 0)
        {
            CameraCombo.Items.Add("Nenhuma câmera encontrada");
            CameraCombo.SelectedIndex = 0;
            _camerasLoaded = false;
        }
    }

    // ───────────────────────── Ações ─────────────────────────

    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        if (_shell.IsTransmitting)
        {
            _shell.Navigate(_shell.TransmitPage);
            return;
        }
        if (_mode == StreamMode.Camera && CameraCombo.SelectedItem is not CameraDevice)
        {
            _shell.Toast("Nenhuma câmera encontrada. Conecte uma webcam e tente de novo.", ToastKind.Warning);
            _camerasLoaded = false;
            await LoadCamerasAsync();
            return;
        }
        SaveChoices();
        _shell.Navigate(_shell.TransmitPage);
        await _shell.TransmitPage.StartFromSettingsAsync();
    }

    private void SaveChoices()
    {
        if (_loading) return;
        Settings.Mode = _mode;
        if (ResolutionCombo.Value<Resolution>() is { } resolution) Settings.Resolution = resolution.Label;
        if (FpsCombo.SelectedItem is Choice<int> fps) Settings.Fps = fps.Value;
        if (CameraQualityCombo.SelectedItem is Choice<(Resolution Resolution, int Fps)> camera)
        {
            Settings.CameraResolution = camera.Value.Resolution.Label;
            Settings.CameraFps = camera.Value.Fps;
        }
        if (CameraCombo.SelectedItem is CameraDevice device) Settings.CameraId = device.Id;
        if (MusicCombo.SelectedItem is Choice<int> music) Settings.MusicBitrateKbps = music.Value;
        Settings.AudioEnabled = AudioCheck.IsChecked == true;
        Settings.CameraWithScreen = CameraCheck.IsChecked == true;
        AppServices.Settings.Save();
    }

    private void Connect_Click(object sender, RoutedEventArgs e) => Connect(HostBox.Text);

    private void HostBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        Connect(HostBox.Text);
    }

    private void Connect(string host)
    {
        host = host.Trim();
        if (host.Length == 0)
        {
            HostBox.Focus();
            _shell.Toast("Digite o IP do Radmin de quem está transmitindo, por exemplo 26.12.34.56.", ToastKind.Warning);
            return;
        }
        _shell.Navigate(_shell.WatchPage);
        _shell.WatchPage.ConnectTo(host);
    }

    private void RefreshRecent()
    {
        RecentPanel.Children.Clear();
        foreach (var host in Settings.RecentHosts.Take(4))
        {
            var chip = new Button { Content = host, Style = (Style)FindResource("ChipButton"), FontFamily = (System.Windows.Media.FontFamily)FindResource("FontMono"), ToolTip = $"Conectar em {host}" };
            chip.Click += (_, _) =>
            {
                HostBox.Text = host;
                Connect(host);
            };
            RecentPanel.Children.Add(chip);
        }
        RecentLabel.Visibility = RecentPanel.Children.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (HostBox.Text.Length == 0 && Settings.RecentHosts.Count > 0) HostBox.Text = Settings.RecentHosts[0];
    }

    // ───────────────────────── Estado e ambiente ─────────────────────────

    private void UpdateLiveState()
    {
        var live = _shell.IsTransmitting;
        LiveBadge.Visibility = live ? Visibility.Visible : Visibility.Collapsed;
        StartText.Text = live ? "Abrir painel da transmissão" : _mode == StreamMode.AudioOnly ? "Começar a festa" : "Iniciar transmissão";
        ModeChips.IsEnabled = ScreenFields.IsEnabled = CameraFields.IsEnabled = PartyFields.IsEnabled = AudioCheck.IsEnabled = !live;
        if (live)
        {
            var viewers = _shell.TransmitPage.Viewers;
            LiveText.Text = viewers switch { 0 => "Ao vivo · esperando gente", 1 => "Ao vivo · 1 pessoa", _ => $"Ao vivo · {viewers} pessoas" };
        }
    }

    private async void RefreshEnvironment()
    {
        var monitors = AppServices.Monitors();
        var monitor = monitors.FirstOrDefault(m => m.IsPrimary) ?? monitors.FirstOrDefault();
        MonitorValue.Text = monitor is null ? "—" : $"{monitor.Width} × {monitor.Height}";
        MonitorDetail.Text = monitor is null ? "Nenhum monitor encontrado" :
            $"{(monitor.IsPrimary ? "Principal" : "Monitor")} · {monitor.RefreshRateHz} Hz" + (monitors.Count > 1 ? $" · {monitors.Count} monitores" : string.Empty);
        UpdateProfileCard();
        UpdateNetworkCard();

        // A placa aparece logo; o encoder vem depois (abrir cada encoder leva uns segundos).
        try
        {
            ShowGpu(GpuInfo.Main(await AppServices.Gpus), null);
            var hardware = await AppServices.Hardware;
            ShowGpu(hardware.MainGpu, hardware);
        }
        catch (Exception exception)
        {
            Log.Warn($"Não deu para detectar a placa de vídeo: {exception.Message}");
            GpuValue.Text = "Não identificada";
            GpuDetail.Text = "A detecção falhou. Veja Configurações › Diagnóstico.";
        }
    }

    /// <summary>
    /// O nome curto da placa e, embaixo, o encoder que ela usa, ou o motivo de ela não funcionar ou de estar sem encoder
    /// (driver com erro, driver antigo para o NVENC, NVENC ocupado).
    /// </summary>
    private void ShowGpu(GpuInfo? gpu, HardwareProfile? hardware)
    {
        GpuValue.Text = gpu?.ShortName ?? (hardware is null ? "Detectando…" : "Não identificada");
        GpuValue.ToolTip = gpu is null ? null : gpu.Driver is null ? gpu.Name : $"{gpu.Name} · driver {gpu.Driver}";
        if (hardware is null)
        {
            GpuDetail.Text = gpu?.Problem is { } early ? $"Com problema: {early}." : "Procurando o encoder…";
            return;
        }
        // Com o encoder da placa abrindo, ela funciona, mesmo que o Windows aponte algo (como reiniciar depois do driver novo).
        if (gpu?.Problem is { } problem && hardware.MainGpuEncoder is null)
        {
            GpuDetail.Text = $"Com problema: {problem}.";
            return;
        }
        var encoder = hardware.Encoders.FirstOrDefault(e => e.FfmpegName == Settings.PreferredEncoder)
                      ?? hardware.MainGpuEncoder
                      ?? hardware.Encoders.OrderByDescending(e => e.Hardware).ThenByDescending(e => e.Codec).FirstOrDefault();
        var text = encoder is null ? "Sem encoder de vídeo" : $"Encoder {Quality.CodecLabel(encoder.Codec)} · {encoder.FfmpegName}" + (encoder.Hardware ? string.Empty : " (software)");
        if (gpu is null) text += ". Nenhuma placa encontrada: veja Configurações › Diagnóstico";
        else if (hardware.MainGpuEncoderHint is { } hint) text += $". Sem o encoder da placa: {hint}";
        GpuDetail.Text = text;
    }

    private void UpdateProfileCard()
    {
        if (!IsInitialized) return;
        switch (_mode)
        {
            case StreamMode.AudioOnly:
                var kbps = MusicCombo.Value<int>();
                ProfileValue.Text = "Música";
                ProfileDetail.Text = $"Opus estéreo · {(kbps > 0 ? kbps : Settings.MusicBitrateKbps)} kbps";
                break;
            case StreamMode.Camera when CameraQualityCombo.SelectedItem is Choice<(Resolution Resolution, int Fps)> camera:
                ProfileValue.Text = Quality.TierLabel(camera.Value.Resolution, camera.Value.Fps);
                ProfileDetail.Text = $"Câmera · {camera.Value.Resolution.Width} × {camera.Value.Resolution.Height} · {camera.Value.Fps} FPS";
                break;
            default:
                var resolution = ResolutionCombo.Value<Resolution>() ?? Resolution.FromLabel(Settings.Resolution);
                var fps = FpsCombo.SelectedItem is Choice<int> f ? f.Value : Settings.Fps;
                ProfileValue.Text = Quality.TierLabel(resolution, fps);
                ProfileDetail.Text = $"{resolution.Width} × {resolution.Height} · até {fps} FPS";
                break;
        }
    }

    private void UpdateNetworkCard()
    {
        var radmin = _shell.Radmin;
        var setup = AppServices.RadminSetup;
        var installed = _shell.RadminInstalled;
        NetworkValue.Text = radmin?.Address.ToString() ?? (installed ? "Desconectado" : "Não instalado");
        NetworkDetail.Text = radmin is not null ? (Settings.FirewallConfigured ? "Firewall liberado" : "Portas fechadas no firewall")
            : setup.Busy || (setup.Status is not null && !installed) ? setup.Status
            : installed ? "Abra o Radmin VPN e entre numa rede" : "O ScreenFlow baixa e instala para você";
        NetworkTile.Style = (Style)FindResource(radmin is null ? "IconTileAmber" : "IconTileCyan");
        RadminProgress.Visibility = setup.Progress is null ? Visibility.Collapsed : Visibility.Visible;
        RadminProgress.Value = setup.Progress ?? 0;
        RadminButton.Visibility = radmin is null && !setup.Busy ? Visibility.Visible : Visibility.Collapsed;
        RadminButton.Content = installed ? "Abrir o Radmin VPN" : "Instalar o Radmin VPN";
    }

    private async void RadminButton_Click(object sender, RoutedEventArgs e)
    {
        if (_shell.RadminInstalled)
        {
            if (!RadminSetup.OpenApp()) _shell.Toast("Abra o Radmin VPN pelo menu Iniciar e entre na mesma rede que os outros.", ToastKind.Info);
            return;
        }
        await AppServices.RadminSetup.InstallAsync(_shell);
    }
}
