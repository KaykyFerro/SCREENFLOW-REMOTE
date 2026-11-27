using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Shapes;
using System.Windows.Threading;
using ScreenFlow.App.Services;
using ScreenFlow.Core.Diagnostics;
using ScreenFlow.Core.Media;
using ScreenFlow.Core.Room;
using ScreenFlow.Core.Settings;
using ScreenFlow.Infrastructure.Network;
using ScreenFlow.Infrastructure.Room;

namespace ScreenFlow.App.Pages;

/// <summary>As abas das Configurações.</summary>
public enum SettingsTab
{
    General,
    Stream,
    Player,
    Voice,
    Network,
}

/// <summary>
/// Configurações em abas: Geral (perfil, tema e janela), Transmissão (qualidade, som e quem pode entrar), Assistir
/// (player e atalhos), Voz e sala (microfone e chat guardado) e Rede e diagnóstico.
/// </summary>
public partial class SettingsPage : UserControl, IShellPage
{
    /// <summary>Pasta de exemplo do chat guardado nas capturas de tela.</summary>
    private const string DemoArchiveFolder = @"G:\Meu Drive\ScreenFlow Chat";

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
    private SettingsTab _tab = SettingsTab.General;
    private bool _archiveDemo;
    private bool _choosingFolder;

    public SettingsPage(MainWindow shell)
    {
        _shell = shell;
        InitializeComponent();
        SizeChanged += (_, _) => ApplyLayout();
        ShowTab(_tab);
        QualityCombo.Fill(Resolution.All.Select(r => new Choice<Resolution>(r, $"{r.Label} · {r.Width} × {r.Height}")));
        FpsCombo.Fill(Quality.FrameRates.Select(f => new Choice<int>(f, $"{f} FPS")));
        FecCombo.Fill(new[] { 0, 4, 6, 8, 10, 16 }.Select(n => new Choice<int>(n, n == 0 ? "Desligada" : $"1 extra a cada {n}")));
        AudioBitrateCombo.Fill(new[] { 96, 128, 160, 192, 256 }.Select(k => new Choice<int>(k, $"{k} kbps")));
        MusicCombo.Fill(AudioProfile.MusicBitrates.Select(k => new Choice<int>(k, OverviewPage.MusicLabel(k))));
        CameraQualityCombo.Fill(OverviewPage.CameraQualities);
        ThemeCombo.Fill([new Choice<AppTheme>(AppTheme.Dark, "Escuro"), new Choice<AppTheme>(AppTheme.Light, "Claro")]);
        _micTimer.Tick += (_, _) => UpdateMicMeter();
        AppServices.Room.ArchiveChanged += UpdateArchiveCard;
        AppServices.Room.PrivateRoomChanged += UpdatePrivateRoomCard;
        AppServices.Room.Changed += () => { if (_tab == SettingsTab.Voice && AppServices.Room.IsPrivateRoomOwner) UpdatePrivateRoomCard(); };
        Unloaded += (_, _) =>
        {
            StopMicTest();
            StopMonitor();
        };
    }

    private AppSettings Settings => AppServices.Settings.Current;

    public void OnShown() => _ = LoadAsync();

    // ───────────────────────── Abas ─────────────────────────

    public SettingsTab Tab => _tab;

    /// <summary>Abre uma aba (as outras telas usam para ir direto ao assunto, como a voz ou o perfil).</summary>
    public void ShowTab(SettingsTab tab)
    {
        _tab = tab;
        var radio = TabButton(tab);
        if (radio.IsChecked != true) radio.IsChecked = true; // chama o Tab_Checked
        else ApplyTab();
    }

    private RadioButton TabButton(SettingsTab tab) => tab switch
    {
        SettingsTab.Stream => TabStream,
        SettingsTab.Player => TabPlayer,
        SettingsTab.Voice => TabVoice,
        SettingsTab.Network => TabNetwork,
        _ => TabGeneral,
    };

    private void Tab_Checked(object sender, RoutedEventArgs e)
    {
        // O XAML marca a primeira aba antes de criar o resto da página.
        if (!IsInitialized || GeneralColumns is null) return;
        foreach (var tab in Enum.GetValues<SettingsTab>())
            if (ReferenceEquals(sender, TabButton(tab))) _tab = tab;
        ApplyTab();
        PageScroller.ScrollToTop();
    }

    private void ApplyTab()
    {
        GeneralColumns.Visibility = _tab == SettingsTab.General ? Visibility.Visible : Visibility.Collapsed;
        StreamPanel.Visibility = _tab == SettingsTab.Stream ? Visibility.Visible : Visibility.Collapsed;
        PlayerColumns.Visibility = _tab == SettingsTab.Player ? Visibility.Visible : Visibility.Collapsed;
        VoiceColumns.Visibility = _tab == SettingsTab.Voice ? Visibility.Visible : Visibility.Collapsed;
        NetworkColumns.Visibility = _tab == SettingsTab.Network ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Com a janela estreita, os cartões de cada aba ficam um embaixo do outro.</summary>
    private void ApplyLayout()
    {
        var stacked = ActualWidth < 1000;
        ResponsiveGrid.Apply(GeneralColumns, stacked);
        ResponsiveGrid.Apply(StreamColumns, stacked);
        ResponsiveGrid.Apply(PlayerColumns, stacked);
        ResponsiveGrid.Apply(VoiceColumns, stacked);
        ResponsiveGrid.Apply(NetworkColumns, stacked);
    }

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
        KeepVolumeToggle.IsChecked = s.KeepStreamVolume;
        GpuToggle.IsChecked = s.GpuVideoRenderer;
        StatsToggle.IsChecked = s.ShowStatsOverlay;
        MediaControlsToggle.IsChecked = s.SystemMediaControls;
        MediaPortBox.Text = s.MediaPort.ToString();
        ControlPortBox.Text = s.ControlPort.ToString();
        DiscoveryPortBox.Text = s.DiscoveryPort.ToString();
        UploadBox.Text = s.UploadMbps > 0 ? s.UploadMbps.ToString("0", CultureInfo.CurrentCulture) : string.Empty;
        MaxViewersBox.Text = s.MaxViewers.ToString();
        NameBox.Text = s.DeviceName;
        UpdateProfilePreview();
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
        UpdateArchiveCard();
        UpdatePrivateRoomCard();
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
        var audioChanged = s.SeparateAppAudio != (SeparateAudioToggle.IsChecked == true) || s.KeepStreamVolume != (KeepVolumeToggle.IsChecked == true);
        s.SeparateAppAudio = SeparateAudioToggle.IsChecked == true;
        s.KeepStreamVolume = KeepVolumeToggle.IsChecked == true;
        s.GpuVideoRenderer = GpuToggle.IsChecked == true;
        s.ShowStatsOverlay = StatsToggle.IsChecked == true;
        s.SystemMediaControls = MediaControlsToggle.IsChecked == true;
        s.MediaPort = int.TryParse(MediaPortBox.Text, out var mp) ? mp : s.MediaPort;
        s.ControlPort = int.TryParse(ControlPortBox.Text, out var cp) ? cp : s.ControlPort;
        s.DiscoveryPort = int.TryParse(DiscoveryPortBox.Text, out var dp) ? dp : s.DiscoveryPort;
        s.UploadMbps = TryNumber(UploadBox.Text, out var up) ? up : 0;
        s.MaxViewers = int.TryParse(MaxViewersBox.Text, out var mv) ? mv : s.MaxViewers;
        var name = NameBox.Text.Trim();
        s.DeviceName = name.Length == 0 ? Environment.MachineName : name;
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
        if (audioChanged) _shell.TransmitPage.OnAudioSettingsChanged();
        if (theme != ThemeManager.Current) ThemeManager.Apply(theme);
        SavedText.Text = $"Salvo às {DateTime.Now:HH:mm}";
        // Com a transmissão no ar, a qualidade nova vale na hora (a tela da Transmissão avisa); o resto já está salvo.
        if (!_shell.IsTransmitting || !_shell.TransmitPage.ApplySettingsQualityLive()) _shell.Toast("Configurações salvas.", ToastKind.Success);
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

    // ───────────────────────── Seu perfil ─────────────────────────

    private void UpdateProfilePreview()
    {
        var room = AppServices.Room;
        var name = NameBox.Text.Trim();
        ProfilePhoto.Colors = AvatarColors.For(room.SelfId);
        ProfilePhoto.Initial = AvatarColors.Initial(name.Length == 0 ? Settings.DeviceName : name);
        ProfilePhoto.Photo = room.SelfPhoto;
        var hasPhoto = Settings.AvatarId is not null;
        ChoosePhotoLabel.Text = hasPhoto ? "Trocar a foto" : "Escolher foto";
        RemovePhotoButton.Visibility = hasPhoto ? Visibility.Visible : Visibility.Collapsed;
    }

    private void NameBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (IsInitialized) UpdateProfilePreview();
    }

    /// <summary>A foto vale na hora (sem precisar salvar): quem está na rede baixa a nova em alguns segundos.</summary>
    private void ChoosePhoto_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Title = "Escolha a sua foto", Filter = ImageTools.FileFilter };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        try
        {
            var image = ImageTools.LoadFile(dialog.FileName, ImageTools.AvatarSide * 3, limitShortSide: true);
            AppServices.Room.SetPhoto(ImageTools.MakeAvatar(image));
            UpdateProfilePreview();
            _shell.Toast("Foto trocada. Quem está na rede vê a nova em alguns segundos.", ToastKind.Success);
        }
        catch (System.IO.InvalidDataException exception) { _shell.Toast(exception.Message, ToastKind.Warning); }
        catch (Exception exception)
        {
            Log.Warn($"Foto do perfil: {exception}");
            _shell.Toast("Não deu para usar essa imagem como foto.", ToastKind.Warning);
        }
    }

    private void RemovePhoto_Click(object sender, RoutedEventArgs e)
    {
        AppServices.Room.SetPhoto(null);
        UpdateProfilePreview();
        _shell.Toast("Foto tirada. Agora aparece a inicial do seu nome.", ToastKind.Success);
    }

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

    // ───────────────────────── Chat guardado (Google Drive) ─────────────────────────

    /// <summary>Mostra se o chat está sendo guardado, onde, ou por que não está.</summary>
    // ───────────────────────── Sala privada e moderação (1.4.0) ─────────────────────────

    private void UpdatePrivateRoomCard()
    {
        var room = AppServices.Room;
        var on = room.InPrivateRoom;
        var owner = room.IsPrivateRoomOwner;
        PrivateOffPanel.Visibility = on ? Visibility.Collapsed : Visibility.Visible;
        PrivateOnPanel.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        PrivateOwnerPanel.Visibility = owner ? Visibility.Visible : Visibility.Collapsed;
        PrivateRotateButton.Visibility = owner ? Visibility.Visible : Visibility.Collapsed;
        PrivateDot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, on ? "Accent" : "TextSubtle");
        PrivateStatus.Text = !on ? "Sala aberta: todo mundo da rede do Radmin com o ScreenFlow vê a sala."
            : owner ? "Sala privada ativa. Você é o dono: mande o convite para quem pode entrar."
            : "Sala privada ativa. Só quem tem o convite vê e ouve a sala.";
        PrivateFingerprint.Text = on && room.PrivateRoomFingerprint is { } fingerprint
            ? $"Código de conferência da chave: {fingerprint}. Todo mundo na mesma sala vê o mesmo código."
            : string.Empty;
        PrivateFingerprint.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        PrivateCodeBox.Text = room.PrivateRoomCode ?? string.Empty;
        PrivateApprovedOnlyToggle.IsChecked = AppServices.Settings.Current.PrivateRoomApprovedOnly;

        if (owner)
        {
            var pending = room.PrivateRoomPending;
            PrivatePendingTitle.Visibility = pending.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            FillPeople(PrivatePendingList, pending.Select(p => (p.Key, p.Value)), "Aprovar", (id, _) => room.ApprovePrivateMember(id));
            var approvedOnly = AppServices.Settings.Current.PrivateRoomApprovedOnly;
            var approved = AppServices.Settings.Current.PrivateRoomApproved;
            var members = room.People.Where(p => p.Id != room.SelfId && !pending.ContainsKey(p.Id))
                .Select(p => (p.Id, approvedOnly && !approved.Contains(p.Id.ToString("N")) ? $"{p.Name} (sem aprovação)" : p.Name));
            FillPeople(PrivateMembersList, members, "Remover", (id, name) =>
            {
                if (room.RemovePrivateMember(id, name)) _shell.Toast($"{name} saiu da sala privada e a chave foi trocada para os outros.", ToastKind.Success);
            }, "Ninguém além de você por enquanto.");
        }

        var blocked = room.Blocked;
        BlockedTitle.Visibility = blocked.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        FillPeople(BlockedList, blocked, "Desbloquear", (id, name) => room.SetBlocked(id, name, false));
    }

    /// <summary>Uma linha por pessoa, com o nome e um botão.</summary>
    private static void FillPeople(StackPanel list, IEnumerable<(Guid Id, string Name)> people, string action, Action<Guid, string> onClick, string? empty = null)
    {
        list.Children.Clear();
        foreach (var (id, name) in people)
        {
            var button = new Button { Content = action, MinHeight = 30, Padding = new Thickness(12, 0, 12, 0), Margin = new Thickness(8, 0, 0, 0) };
            button.SetResourceReference(StyleProperty, "GhostButton");
            System.Windows.Automation.AutomationProperties.SetName(button, $"{action} {name}");
            button.Click += (_, _) => onClick(id, name);
            var row = new DockPanel { Margin = new Thickness(0, 2, 0, 2) };
            DockPanel.SetDock(button, Dock.Right);
            row.Children.Add(button);
            row.Children.Add(new TextBlock { Text = name, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis });
            list.Children.Add(row);
        }
        if (list.Children.Count == 0 && empty is not null)
        {
            var text = new TextBlock { Text = empty, TextWrapping = TextWrapping.Wrap };
            text.SetResourceReference(StyleProperty, "Caption");
            list.Children.Add(text);
        }
    }

    private void PrivateCreate_Click(object sender, RoutedEventArgs e)
    {
        var code = AppServices.Room.CreatePrivateRoom(PrivateApprovedOnlyNew.IsChecked == true);
        CopyInvite(code);
        UpdatePrivateRoomCard();
    }

    private void PrivateJoin_Click(object sender, RoutedEventArgs e)
    {
        if (!AppServices.Room.JoinPrivateRoom(PrivateInviteBox.Text))
        {
            _shell.Toast("Esse convite não é válido. Confira se copiou o código inteiro, começando por SF1-.", ToastKind.Warning);
            return;
        }
        PrivateInviteBox.Text = string.Empty;
        _shell.Toast("Você entrou na sala privada.", ToastKind.Success);
        UpdatePrivateRoomCard();
    }

    private void PrivateCopy_Click(object sender, RoutedEventArgs e) => CopyInvite(AppServices.Room.PrivateRoomCode);

    private void CopyInvite(string? code)
    {
        if (code is null) return;
        try
        {
            Clipboard.SetText(code);
            _shell.Toast("Convite copiado. Mande só para quem pode entrar na sala.", ToastKind.Success);
        }
        catch (System.Runtime.InteropServices.COMException) { _shell.Toast("Não deu para copiar agora. Selecione o código e copie com Ctrl+C.", ToastKind.Warning); }
    }

    private void PrivateApprovedOnly_Click(object sender, RoutedEventArgs e)
        => AppServices.Room.SetPrivateRoomApprovedOnly(PrivateApprovedOnlyToggle.IsChecked == true);

    private void PrivateRotate_Click(object sender, RoutedEventArgs e)
    {
        if (AppServices.Room.RotatePrivateRoomKey())
            _shell.Toast("Chave trocada. Quem está na sala agora recebeu a nova; para os outros, mande o convite novo.", ToastKind.Success);
    }

    private void PrivateLeave_Click(object sender, RoutedEventArgs e)
    {
        AppServices.Room.LeavePrivateRoom();
        _shell.Toast("Você voltou para a sala aberta da rede.", ToastKind.Info);
    }

    private void UpdateArchiveCard()
    {
        if (!IsInitialized) return;
        var s = Settings;
        var room = AppServices.Room;
        var enabled = _archiveDemo || s.ChatArchiveEnabled;
        var custom = !_archiveDemo && s.ChatArchiveFolder is not null;
        var folder = _archiveDemo ? DemoArchiveFolder : enabled ? room.ArchiveFolder : null;
        var problem = _archiveDemo || !enabled ? null : room.ArchiveProblem;
        ArchiveToggle.IsChecked = enabled;
        string dot;
        if (!enabled)
        {
            ArchiveStatus.Text = "Desligado. As conversas ficam só enquanto o ScreenFlow está aberto.";
            dot = "TextSubtle";
        }
        else if (problem is not null)
        {
            ArchiveStatus.Text = problem;
            dot = "Warning";
        }
        else if (folder is null)
        {
            ArchiveStatus.Text = custom ? "Abrindo a pasta…" : "Procurando o Google Drive deste PC…";
            dot = "TextSubtle";
        }
        else
        {
            ArchiveStatus.Text = custom ? "Guardando as conversas nesta pasta:" : "Guardando no Google Drive, nesta pasta:";
            dot = "Success";
        }
        ArchiveDot.SetResourceReference(Shape.FillProperty, dot);
        ArchivePath.Text = folder ?? string.Empty;
        ArchivePath.Visibility = folder is null ? Visibility.Collapsed : Visibility.Visible;
        ArchiveDriveButton.Visibility = custom ? Visibility.Visible : Visibility.Collapsed;
        ArchiveOpenButton.Visibility = folder is null ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>Para as capturas do CI: o chat guardado ligado numa pasta do Google Drive.</summary>
    internal void ShowArchiveDemo()
    {
        _archiveDemo = true;
        UpdateArchiveCard();
    }

    /// <summary>O chat guardado vale na hora (sem precisar do Salvar): a sala abre ou fecha a pasta.</summary>
    private void SaveArchiveSettings()
    {
        try { AppServices.Settings.Save(); }
        catch (Exception exception)
        {
            Log.Warn($"Chat guardado: configurações não salvas: {exception.Message}");
            _shell.Toast("Não deu para salvar a escolha do chat guardado.", ToastKind.Warning);
        }
        UpdateArchiveCard();
    }

    private void ArchiveToggle_Click(object sender, RoutedEventArgs e)
    {
        if (_archiveDemo) return;
        Settings.ChatArchiveEnabled = ArchiveToggle.IsChecked == true;
        SaveArchiveSettings();
    }

    private async void ArchiveChoose_Click(object sender, RoutedEventArgs e)
    {
        if (_choosingFolder) return;
        _choosingFolder = true;
        ArchiveChooseButton.IsEnabled = false;
        try
        {
            var start = Settings.ChatArchiveFolder ?? await Task.Run(ChatArchiveFolders.FindGoogleDrive);
            var dialog = new Microsoft.Win32.OpenFolderDialog
            {
                Title = "Escolha a pasta em que o chat vai ficar guardado",
                InitialDirectory = start is not null && System.IO.Directory.Exists(start)
                    ? start
                    : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            };
            if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
            Settings.ChatArchiveFolder = ChatArchive.RootFor(dialog.FolderName);
            Settings.ChatArchiveEnabled = true;
            SaveArchiveSettings();
        }
        catch (Exception exception)
        {
            Log.Warn($"Escolher a pasta do chat: {exception.Message}");
            _shell.Toast("Não deu para usar essa pasta.", ToastKind.Warning);
        }
        finally
        {
            _choosingFolder = false;
            ArchiveChooseButton.IsEnabled = true;
        }
    }

    private void ArchiveDrive_Click(object sender, RoutedEventArgs e)
    {
        Settings.ChatArchiveFolder = null;
        Settings.ChatArchiveEnabled = true;
        SaveArchiveSettings();
    }

    private void ArchiveOpen_Click(object sender, RoutedEventArgs e)
    {
        if (AppServices.Room.ArchiveFolder is not { } folder) return;
        try { Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true }); }
        catch (Exception exception)
        {
            Log.Warn($"Não foi possível abrir a pasta do chat: {exception.Message}");
            _shell.Toast("Não deu para abrir a pasta do chat.", ToastKind.Warning);
        }
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
