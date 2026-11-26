using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Shapes;
using System.Windows.Threading;
using ScreenFlow.App.Services;
using ScreenFlow.Core.Diagnostics;
using ScreenFlow.Core.Media;
using ScreenFlow.Core.Settings;
using ScreenFlow.Infrastructure.Network;
using ScreenFlow.Infrastructure.Room;

namespace ScreenFlow.App.Pages;

/// <summary>Configurações: qualidade, câmera e som, player, rede e firewall, segurança, tema e diagnóstico.</summary>
public partial class SettingsPage : UserControl, IShellPage
{
    private readonly MainWindow _shell;
    private readonly DispatcherTimer _micTimer = new() { Interval = TimeSpan.FromMilliseconds(60) };
    private readonly VoiceGate _micGate = new();
    private IVoiceInput? _micTest;
    private MicrophoneInput? _monitorInput;
    private MicrophoneMonitor? _monitor;
    private bool _monitorStarting;
    private double _micLevelDb = -100;
    private double _shownLevel;
    private bool _loading;

    public SettingsPage(MainWindow shell)
    {
        _shell = shell;
        InitializeComponent();
        SizeChanged += (_, _) => ResponsiveGrid.Apply(Columns, ActualWidth < 1000);
        QualityCombo.Fill(Resolution.All.Select(r => new Choice<Resolution>(r, $"{r.Label} · {r.Width} × {r.Height}")));
        FpsCombo.Fill(Quality.FrameRates.Select(f => new Choice<int>(f, $"{f} FPS")));
        FecCombo.Fill(new[] { 0, 4, 6, 8, 10, 16 }.Select(n => new Choice<int>(n, n == 0 ? "Desligada" : $"1 extra a cada {n}")));
        AudioBitrateCombo.Fill(new[] { 96, 128, 160, 192, 256 }.Select(k => new Choice<int>(k, $"{k} kbps")));
        MusicCombo.Fill(AudioProfile.MusicBitrates.Select(k => new Choice<int>(k, OverviewPage.MusicLabel(k))));
        CameraQualityCombo.Fill(OverviewPage.CameraQualities);
        ThemeCombo.Fill([new Choice<AppTheme>(AppTheme.Dark, "Escuro"), new Choice<AppTheme>(AppTheme.Light, "Claro")]);
        _micTimer.Tick += (_, _) => UpdateMicMeter();
        Unloaded += (_, _) =>
        {
            StopMicTest();
            StopMonitor();
        };
    }

    private AppSettings Settings => AppServices.Settings.Current;

    public void OnShown() => _ = LoadAsync();

    private async Task LoadAsync()
    {
        _loading = true;
        var s = Settings;
        QualityCombo.SelectValue(Resolution.FromLabel(s.Resolution));
        FpsCombo.SelectValue(s.Fps, f => f);
        BitrateBox.Text = s.BitrateKbps > 0 ? (s.BitrateKbps / 1000.0).ToString("0.#", CultureInfo.CurrentCulture) : string.Empty;
        FecCombo.SelectValue(s.FecGroupSize, n => n);
        AdaptiveToggle.IsChecked = s.AdaptiveQuality;
        CameraQualityCombo.SelectValue((Resolution.FromLabel(s.CameraResolution), s.CameraFps));
        AudioBitrateCombo.SelectValue(s.AudioBitrateKbps, k => k);
        MusicCombo.SelectValue(s.MusicBitrateKbps, k => k);
        SeparateAudioToggle.IsChecked = s.SeparateAppAudio;
        GpuToggle.IsChecked = s.GpuVideoRenderer;
        StatsToggle.IsChecked = s.ShowStatsOverlay;
        MediaPortBox.Text = s.MediaPort.ToString();
        ControlPortBox.Text = s.ControlPort.ToString();
        DiscoveryPortBox.Text = s.DiscoveryPort.ToString();
        UploadBox.Text = s.UploadMbps > 0 ? s.UploadMbps.ToString("0", CultureInfo.CurrentCulture) : string.Empty;
        MaxViewersBox.Text = s.MaxViewers.ToString();
        NameBox.Text = s.DeviceName;
        PinBox.Text = s.Pin ?? string.Empty;
        ApproveToggle.IsChecked = s.ApproveEachViewer;
        TrayToggle.IsChecked = s.MinimizeToTray;
        ReopenToggle.IsChecked = s.ShowWindowWhenSessionEnds;
        ThemeCombo.SelectValue(ThemeManager.Current);
        LoadMicrophones(s.MicrophoneId);
        SensitivitySlider.Value = s.VoiceSensitivity;
        VoiceVolumeSlider.Value = s.VoiceVolume;
        VoiceSoundsToggle.IsChecked = s.VoiceSounds;
        UpdateVoiceLabels();
        if (AppServices.DemoMode) ShowDemoMeter();
        SavedText.Text = string.Empty;
        AutoResult.Text = s.LastAutoConfig is { } when
            ? $"Feita em {when.ToLocalTime():dd/MM/yyyy 'às' HH:mm}: {s.Resolution} a {s.Fps} FPS, encoder {s.PreferredEncoder ?? "automático"}."
            : "Detecta a GPU, os codecs, o monitor e a rede e escolhe a melhor qualidade para este PC.";

        var hardware = await AppServices.Hardware;
        var encoders = new List<Choice<string?>> { new(null, "Automático (o melhor disponível)") };
        encoders.AddRange(hardware.Encoders.Select(e => new Choice<string?>(e.FfmpegName, e.Label)));
        EncoderCombo.Fill(encoders);
        EncoderCombo.SelectValue(hardware.Encoders.Any(e => e.FfmpegName == s.PreferredEncoder) ? s.PreferredEncoder : null);
        _loading = false;

        await RefreshFirewallAsync();
        await RefreshDiagnosticsAsync();
    }

    // ───────────────────────── Salvar ─────────────────────────

    private void Save_Click(object sender, RoutedEventArgs e) => _ = SaveAsync();

    private async Task SaveAsync()
    {
        if (_loading) return;
        var s = Settings;
        var portsBefore = (s.MediaPort, s.ControlPort, s.DiscoveryPort);
        var pinText = PinBox.Text.Trim();
        if (pinText.Length is > 0 and < 4)
        {
            SavedText.Text = string.Empty;
            _shell.Toast("O PIN precisa ter pelo menos 4 caracteres (ou fique vazio).", ToastKind.Warning);
            PinBox.Focus();
            return;
        }

        if (QualityCombo.Value<Resolution>() is { } resolution) s.Resolution = resolution.Label;
        if (FpsCombo.SelectedItem is Choice<int> fps) s.Fps = fps.Value;
        s.BitrateKbps = TryNumber(BitrateBox.Text, out var mbps) ? (int)(mbps * 1000) : 0;
        if (FecCombo.SelectedItem is Choice<int> fec) s.FecGroupSize = fec.Value;
        s.AdaptiveQuality = AdaptiveToggle.IsChecked == true;
        if (CameraQualityCombo.SelectedItem is Choice<(Resolution Resolution, int Fps)> camera)
        {
            s.CameraResolution = camera.Value.Resolution.Label;
            s.CameraFps = camera.Value.Fps;
        }
        if (AudioBitrateCombo.SelectedItem is Choice<int> audio) s.AudioBitrateKbps = audio.Value;
        if (MusicCombo.SelectedItem is Choice<int> music) s.MusicBitrateKbps = music.Value;
        var separateChanged = s.SeparateAppAudio != (SeparateAudioToggle.IsChecked == true);
        s.SeparateAppAudio = SeparateAudioToggle.IsChecked == true;
        s.GpuVideoRenderer = GpuToggle.IsChecked == true;
        s.ShowStatsOverlay = StatsToggle.IsChecked == true;
        s.MediaPort = int.TryParse(MediaPortBox.Text, out var mp) ? mp : s.MediaPort;
        s.ControlPort = int.TryParse(ControlPortBox.Text, out var cp) ? cp : s.ControlPort;
        s.DiscoveryPort = int.TryParse(DiscoveryPortBox.Text, out var dp) ? dp : s.DiscoveryPort;
        s.UploadMbps = TryNumber(UploadBox.Text, out var up) ? up : 0;
        s.MaxViewers = int.TryParse(MaxViewersBox.Text, out var mv) ? mv : s.MaxViewers;
        s.DeviceName = NameBox.Text.Trim();
        s.Pin = pinText.Length == 0 ? null : pinText;
        s.ApproveEachViewer = ApproveToggle.IsChecked == true;
        s.MinimizeToTray = TrayToggle.IsChecked == true;
        s.ShowWindowWhenSessionEnds = ReopenToggle.IsChecked == true;
        s.MicrophoneId = MicrophoneCombo.Value<string?>();
        s.VoiceSensitivity = SensitivitySlider.Value;
        s.VoiceVolume = VoiceVolumeSlider.Value;
        s.VoiceSounds = VoiceSoundsToggle.IsChecked == true;
        var theme = ThemeCombo.Value<AppTheme>();
        s.Theme = theme;

        var hardware = await AppServices.Hardware;
        var encoder = hardware.Encoders.FirstOrDefault(x => x.FfmpegName == EncoderCombo.Value<string?>());
        s.PreferredEncoder = encoder?.FfmpegName;
        s.PreferredCodec = encoder?.Codec;

        AppServices.Settings.Save();
        if (separateChanged) _shell.TransmitPage.OnAudioSettingsChanged();
        if (theme != ThemeManager.Current) ThemeManager.Apply(theme);
        SavedText.Text = $"Salvo às {DateTime.Now:HH:mm}";
        _shell.Toast(_shell.IsTransmitting ? "Configurações salvas. As mudanças de qualidade valem na próxima transmissão." : "Configurações salvas.", ToastKind.Success);
        _ = LoadAsync();

        if (portsBefore != (s.MediaPort, s.ControlPort, s.DiscoveryPort))
        {
            await _shell.CheckFirewallAsync();
            if (!s.FirewallConfigured)
                _shell.Toast("As portas mudaram. Libere as novas portas no firewall para quem assiste conseguir entrar.", ToastKind.Warning, "Liberar portas", () => _ = OpenFirewallAsync());
        }
    }

    private static bool TryNumber(string text, out double value)
        => double.TryParse(text.Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out value) && value > 0;

    // ───────────────────────── Automático ─────────────────────────

    private async void Auto_Click(object sender, RoutedEventArgs e)
    {
        AutoButton.IsEnabled = false;
        AutoResult.Text = "Detectando GPU, codecs, monitor e rede…";
        try
        {
            var result = await AppServices.AutoConfigureAsync(refresh: true);
            await LoadAsync();
            AutoResult.Text = $"Pronto: {result.Resolution.Label} a {result.Fps} FPS em {Quality.CodecLabel(result.Codec)} ({result.Encoder}), cerca de {result.BitrateKbps / 1000.0:0} Mbps por pessoa."
                              + (result.Notes.Count > 0 ? " " + string.Join(" ", result.Notes) : string.Empty);
            _shell.Toast("Configuração automática concluída.", ToastKind.Success);
        }
        catch (Exception exception)
        {
            AutoResult.Text = $"Não foi possível configurar automaticamente: {exception.Message}";
        }
        finally { AutoButton.IsEnabled = true; }
    }

    // ───────────────────────── Voz da rede ─────────────────────────

    private void LoadMicrophones(string? selected)
    {
        IReadOnlyList<MicrophoneInfo> microphones = AppServices.DemoMode
            ? [new MicrophoneInfo("demo", "Microfone (HyperX Cloud II)")]
            : Microphones.List();
        var choices = new List<Choice<string?>> { new(null, "Padrão do Windows") };
        choices.AddRange(microphones.Select(m => new Choice<string?>(m.Id, m.Name)));
        if (selected is not null && microphones.All(m => m.Id != selected)) choices.Add(new Choice<string?>(selected, "Microfone desconectado"));
        MicrophoneCombo.Fill(choices);
        MicrophoneCombo.SelectValue(selected);
    }

    private void RefreshMicrophones_Click(object sender, RoutedEventArgs e) => LoadMicrophones(MicrophoneCombo.Value<string?>());

    private void Sensitivity_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        _micGate.Sensitivity = e.NewValue;
        UpdateVoiceLabels();
    }

    private void VoiceVolume_Changed(object sender, RoutedPropertyChangedEventArgs<double> e) => UpdateVoiceLabels();

    private void MicMeter_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateVoiceLabels();

    /// <summary>A barra vai de −60 dB (vazia) a 0 dB (cheia); a marca fica no limiar do portão da voz.</summary>
    private static double MeterFraction(double db) => Math.Clamp((db + 60) / 60, 0, 1);

    private void UpdateVoiceLabels()
    {
        if (!IsInitialized) return;
        var sensitivity = SensitivitySlider.Value;
        SensitivityValue.Text = sensitivity switch { < 0.25 => "Só voz alta", < 0.5 => "Baixa", < 0.75 => "Média", < 0.9 => "Alta", _ => "Capta tudo" };
        VoiceVolumeValue.Text = $"{VoiceVolumeSlider.Value * 100:0}%";
        var threshold = -30 - Math.Clamp(sensitivity, 0, 1) * 30;
        MicThreshold.Margin = new Thickness(Math.Max(0, MeterFraction(threshold) * MicMeter.ActualWidth - 1.5), -4, 0, -4);
    }

    private void MicTest_Click(object sender, RoutedEventArgs e)
    {
        if (_micTimer.IsEnabled) StopMicTest();
        else _ = StartMicTestAsync();
    }

    private async Task StartMicTestAsync()
    {
        if (AppServices.DemoMode) return;
        MicTestButton.IsEnabled = false;
        try
        {
            // Já na voz: o microfone está aberto, mostra o volume dele mesmo. Fora da voz: abre só para o teste.
            if (!AppServices.Room.InVoice)
            {
                var id = MicrophoneCombo.Value<string?>();
                var input = await Task.Run(() => new MicrophoneInput(id));
                input.FrameReady += frame =>
                {
                    _micGate.Process(frame);
                    _micLevelDb = _micGate.LevelDb;
                };
                input.Start();
                _micTest = input;
            }
            _micTimer.Start();
            MicTestButton.Content = "Parar o teste";
            MicTestText.Text = "Ouvindo o microfone… fale normalmente.";
        }
        catch (Exception exception)
        {
            Log.Warn($"Teste do microfone: {exception.Message}");
            MicTestText.Text = RoomService.MicrophoneProblem(exception);
        }
        finally { MicTestButton.IsEnabled = true; }
    }

    private void StopMicTest()
    {
        _micTimer.Stop();
        var input = _micTest;
        _micTest = null;
        if (input is not null) _ = Task.Run(input.Dispose);
        _micLevelDb = -100;
        _shownLevel = 0;
        if (!IsInitialized) return;
        MicLevel.Width = 0;
        MicTestButton.Content = "Testar microfone";
        MicTestText.Text = "Fale normalmente: a barra precisa passar da marca para a sua voz ir. Se não passar, puxe a sensibilidade para a direita.";
    }

    // ───────────────────────── Ouvir o próprio microfone ─────────────────────────

    private void MicMonitor_Click(object sender, RoutedEventArgs e)
    {
        if (_monitor is not null) StopMonitor();
        else _ = StartMonitorAsync();
    }

    /// <summary>
    /// Abre o microfone escolhido só para o retorno (o Windows deixa vários programas lerem o mesmo microfone, então
    /// funciona também com a voz ligada) e toca no fone.
    /// </summary>
    private async Task StartMonitorAsync()
    {
        if (AppServices.DemoMode || _monitorStarting) return;
        _monitorStarting = true;
        MicMonitorButton.IsEnabled = false;
        var id = MicrophoneCombo.Value<string?>();
        try
        {
            var (input, monitor) = await Task.Run(() =>
            {
                var monitor = new MicrophoneMonitor();
                try
                {
                    var input = new MicrophoneInput(id);
                    input.FrameReady += monitor.Write;
                    input.Start();
                    return (input, monitor);
                }
                catch
                {
                    monitor.Dispose();
                    throw;
                }
            });
            if (!IsLoaded)
            {
                _ = Task.Run(() => { input.Dispose(); monitor.Dispose(); });
                return;
            }
            _monitorInput = input;
            _monitor = monitor;
            MicMonitorLabel.Text = "Parar de ouvir";
            MicMonitorText.Text = "Você está se ouvindo. Use fone de ouvido: na caixa de som o microfone capta o próprio som e começa a apitar.";
            MicMonitorText.SetResourceReference(TextBlock.ForegroundProperty, "Success");
        }
        catch (Exception exception)
        {
            Log.Warn($"Ouvir o microfone: {exception.Message}");
            MicMonitorText.Text = RoomService.MicrophoneProblem(exception);
            MicMonitorText.SetResourceReference(TextBlock.ForegroundProperty, "Danger");
        }
        finally
        {
            _monitorStarting = false;
            MicMonitorButton.IsEnabled = true;
        }
    }

    private void StopMonitor()
    {
        var input = _monitorInput;
        var monitor = _monitor;
        _monitorInput = null;
        _monitor = null;
        if (input is not null || monitor is not null)
            _ = Task.Run(() =>
            {
                input?.Dispose();
                monitor?.Dispose();
            });
        if (!IsInitialized) return;
        MicMonitorLabel.Text = "Ouvir meu microfone";
        MicMonitorText.Text = "Toca o seu microfone no fone, para você ouvir como a sua voz está saindo.";
        MicMonitorText.SetResourceReference(TextBlock.ForegroundProperty, "TextSubtle");
    }

    private void UpdateMicMeter()
    {
        var level = _micTest is not null ? _micLevelDb : AppServices.Room.VoiceInputLevelDb ?? -100;
        // Sobe na hora e desce devagar, como um medidor de verdade.
        var target = MeterFraction(level);
        _shownLevel = target > _shownLevel ? target : Math.Max(target, _shownLevel - 0.04);
        MicLevel.Width = _shownLevel * MicMeter.ActualWidth;
        var threshold = -30 - Math.Clamp(SensitivitySlider.Value, 0, 1) * 30;
        var passing = level >= threshold;
        MicTestText.Text = passing ? "Passando da marca: é assim que a sua voz vai para os outros." : "Abaixo da marca: isso não vai pela rede (silêncio e ruído ficam de fora).";
        MicTestText.SetResourceReference(TextBlock.ForegroundProperty, passing ? "Success" : "TextSubtle");
    }

    private void ShowDemoMeter()
    {
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            _shownLevel = MeterFraction(-22);
            MicLevel.Width = _shownLevel * MicMeter.ActualWidth;
            MicTestText.Text = "Passando da marca: é assim que a sua voz vai para os outros.";
            MicTestText.SetResourceReference(TextBlock.ForegroundProperty, "Success");
        });
    }

    // ───────────────────────── Firewall ─────────────────────────

    private async Task RefreshFirewallAsync()
    {
        var s = Settings;
        var ok = AppServices.DemoMode || await Task.Run(() => FirewallManager.RulesExist(s.MediaPort, s.ControlPort, s.DiscoveryPort));
        FirewallText.Text = ok
            ? $"Portas {s.MediaPort}, {s.ControlPort} e {s.DiscoveryPort} liberadas no firewall."
            : "As portas estão fechadas no firewall: quem está na sua rede pode não conseguir assistir.";
        FirewallDot.SetResourceReference(Shape.FillProperty, ok ? "Success" : "Warning");
        FirewallButton.Visibility = ok ? Visibility.Collapsed : Visibility.Visible;
    }

    private void Firewall_Click(object sender, RoutedEventArgs e) => _ = OpenFirewallAsync();

    private async Task OpenFirewallAsync()
    {
        FirewallButton.IsEnabled = false;
        try
        {
            await _shell.OpenFirewallAsync();
            await RefreshFirewallAsync();
        }
        finally { FirewallButton.IsEnabled = true; }
    }

    // ───────────────────────── Diagnóstico ─────────────────────────

    private async Task RefreshDiagnosticsAsync()
    {
        DiagnosticsText.Text = "Carregando…";
        try { DiagnosticsText.Text = await AppServices.BuildDiagnosticsAsync(); }
        catch (Exception exception) { DiagnosticsText.Text = $"Falha ao gerar o diagnóstico: {exception.Message}"; }
    }

    private async void RefreshDiagnostics_Click(object sender, RoutedEventArgs e)
    {
        await AppServices.RefreshHardware();
        await RefreshDiagnosticsAsync();
    }

    private void CopyDiagnostics_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(DiagnosticsText.Text);
            _shell.Toast("Diagnóstico copiado. Cole na conversa com quem está ajudando.", ToastKind.Success);
        }
        catch (Exception exception) { Log.Warn($"Área de transferência ocupada: {exception.Message}"); }
    }

    private void OpenLogs_Click(object sender, RoutedEventArgs e)
    {
        System.IO.Directory.CreateDirectory(Log.Directory);
        try { Process.Start(new ProcessStartInfo(Log.Directory) { UseShellExecute = true }); }
        catch (Exception exception) { Log.Warn($"Não foi possível abrir a pasta de logs: {exception.Message}"); }
    }
}
