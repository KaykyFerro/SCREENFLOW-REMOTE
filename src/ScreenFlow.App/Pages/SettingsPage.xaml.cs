using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using ScreenFlow.App.Services;
using ScreenFlow.Core;
using ScreenFlow.Core.Diagnostics;
using ScreenFlow.Core.Media;
using ScreenFlow.Core.Network;
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
    Server,
    Network,
}

/// <summary>
/// Configurações em abas: Geral (perfil, tema, cor e janela), Transmissão (qualidade, som e quem pode entrar), Assistir
/// (player e atalhos), Voz e sala (microfone e chat guardado), Servidor (1.4.1: o servidor ScreenFlow e o Tailscale) e
/// Rede e diagnóstico.
/// </summary>
public partial class SettingsPage : UserControl, IShellPage
{
    /// <summary>Pasta de exemplo do chat guardado nas capturas de tela.</summary>
    private const string DemoArchiveFolder = @"G:\Meu Drive\ScreenFlow Chat";

    private readonly MainWindow _shell;
    private readonly DispatcherTimer _micTimer = new() { Interval = TimeSpan.FromMilliseconds(60) };
    private readonly VoiceGate _micGate = new();
    /// <summary>(1.5.0) O tratamento da voz no teste do microfone (só o volume do microfone, antes do portão).</summary>
    private readonly VoiceProcessor _micProcessor = new();
    /// <summary>(1.5.0) O tratamento completo no "Ouvir meu microfone": você ouve a sua voz como ela sai para os outros.</summary>
    private VoiceProcessor? _monitorProcessor;
    private IVoiceInput? _micTest;
    private MicrophoneInput? _monitorInput;
    private MicrophoneMonitor? _monitor;
    private bool _monitorStarting;
    private double _micLevelDb = -100;
    private double _shownLevel;
    private bool _loading;
    private SettingsTab _tab = SettingsTab.General;
    private bool _archiveDemo;
    private bool _serverDemo;
    private bool _choosingFolder;
    private readonly ObservableCollection<AddressRow> _addresses = [];

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
        ThemeCombo.SelectionChanged += (_, _) => ThemeMode_Changed();
        BuildColorSwatches();
        AddressList.ItemsSource = _addresses;
        AppServices.Server.Changed += UpdateServerStatus;
        AppServices.Server.HostedChanged += UpdateServerStatus;
        AppServices.Room.ServerChanged += UpdateServerStatus;
        AppServices.Tailscale.Changed += UpdateTailscaleCard;
        ThemeManager.Changed += UpdateColorSwatches;
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
        SettingsTab.Server => TabServer,
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
        ServerColumns.Visibility = _tab == SettingsTab.Server ? Visibility.Visible : Visibility.Collapsed;
        NetworkColumns.Visibility = _tab == SettingsTab.Network ? Visibility.Visible : Visibility.Collapsed;
        if (_tab == SettingsTab.Server) RefreshServerTab();
    }

    /// <summary>Com a janela estreita, os cartões de cada aba ficam um embaixo do outro.</summary>
    private void ApplyLayout()
    {
        var stacked = ActualWidth < 1000;
        ResponsiveGrid.Apply(GeneralColumns, stacked);
        ResponsiveGrid.Apply(StreamColumns, stacked);
        ResponsiveGrid.Apply(PlayerColumns, stacked);
        ResponsiveGrid.Apply(VoiceColumns, stacked);
        ResponsiveGrid.Apply(ServerColumns, stacked);
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
        UpdateColorSwatches();
        LoadServer(s);
        LoadMicrophones(s.MicrophoneId);
        LoadSpeakers(s.SpeakerId);
        PushToTalkToggle.IsChecked = s.PushToTalk;
        _pushToTalkKey = s.PushToTalkKey;
        ShowPushToTalkKey();
        SensitivitySlider.Value = s.VoiceSensitivity;
        MicGainSlider.Value = s.MicGainDb;
        AutoGainToggle.IsChecked = s.VoiceAutoGain;
        LevelerToggle.IsChecked = s.VoiceLeveler;
        lock (_micProcessor) _micProcessor.ManualGainDb = s.MicGainDb;
        VoiceSoundsToggle.IsChecked = s.VoiceSounds;
        MessageSoundsToggle.IsChecked = s.MessageSounds;
        MixerControls.Refresh();
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

        // (1.4.1) O servidor: o endereço precisa fazer sentido antes de salvar.
        var role = SelectedRole;
        var serverAddress = ServerAddressBox.Text.Trim();
        if (role == ServerRole.Join && !LocalNetworks.TrySplitHostPort(serverAddress, AppInfo.DefaultServerControlPort, out _, out _))
        {
            SavedText.Text = string.Empty;
            ShowTab(SettingsTab.Server);
            _shell.Toast(serverAddress.Length == 0
                ? "Coloque o endereço do servidor (o IP dele no Tailscale ou no Radmin VPN, ou o nome do PC no Tailscale)."
                : $"O endereço \"{serverAddress}\" não parece um endereço de servidor. Use o IP (100.x do Tailscale ou 26.x do Radmin) ou o nome do PC no Tailscale.", ToastKind.Warning);
            ServerAddressBox.Focus();
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
        s.SpeakerId = SpeakerCombo.Value<string?>();
        s.PushToTalk = PushToTalkToggle.IsChecked == true;
        s.PushToTalkKey = _pushToTalkKey;
        s.VoiceSensitivity = SensitivitySlider.Value;
        s.MicGainDb = MicGainSlider.Value;
        s.VoiceAutoGain = AutoGainToggle.IsChecked == true;
        s.VoiceLeveler = LevelerToggle.IsChecked == true;
        s.VoiceSounds = VoiceSoundsToggle.IsChecked == true;
        s.MessageSounds = MessageSoundsToggle.IsChecked == true;
        var theme = ThemeCombo.Value<AppTheme>();
        s.Theme = theme;
        s.ServerRole = role;
        s.ServerAddress = serverAddress.Length == 0 ? null : serverAddress;
        s.ServerPassword = ServerPasswordBox.Password.Length == 0 ? null : ServerPasswordBox.Password;
        s.HostedServerName = HostedNameBox.Text.Trim() is { Length: > 0 } hostedName ? hostedName : null;
        s.HostedServerPassword = HostedPasswordBox.Text.Length == 0 ? null : HostedPasswordBox.Text;
        s.TransmitViaServer = TransmitViaServerToggle.IsChecked == true;

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

    // ───────────────────────── Cor do tema (1.4.1) ─────────────────────────

    /// <summary>Uma bolinha por cor; a escolha vale na hora e fica salva (é só deste PC).</summary>
    private void BuildColorSwatches()
    {
        var style = (Style)FindResource("ColorSwatch");
        foreach (var theme in ColorThemes.All)
        {
            var accent = ToColor(theme.Accent);
            // O ✓ em tinta escura ou branca, o que aparecer mais na cor.
            var check = ColorThemes.Contrast(theme.Accent, 0xFF0B1224) >= ColorThemes.Contrast(theme.Accent, 0xFFFFFFFF) ? Color.FromRgb(0x0B, 0x12, 0x24) : Colors.White;
            var swatch = new RadioButton
            {
                Style = style,
                GroupName = "colorTheme",
                Background = Frozen(accent),
                Foreground = Frozen(check),
                Tag = theme.Id,
                ToolTip = theme == ColorThemes.Default ? $"{theme.Name} (a de sempre)" : theme.Name,
            };
            AutomationProperties.SetName(swatch, $"Cor {theme.Name}");
            swatch.Checked += ColorSwatch_Checked;
            ColorSwatches.Children.Add(swatch);
        }
    }

    private void UpdateColorSwatches()
    {
        var current = ThemeManager.CurrentColor;
        foreach (RadioButton swatch in ColorSwatches.Children)
            if (swatch.Tag as string == current.Id && swatch.IsChecked != true) swatch.IsChecked = true;
        ColorName.Text = current == ColorThemes.Default ? $"{current.Name} (a de sempre)" : current.Name;
    }

    private void ColorSwatch_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton { Tag: string id } || id == ThemeManager.CurrentColor.Id) return;
        ThemeManager.Apply(ThemeManager.Current, id);
        Settings.ColorTheme = id == ColorThemes.Default.Id ? null : id;
        AppServices.Settings.Save();
    }

    /// <summary>Escuro ou claro também vale na hora (antes só valia ao salvar).</summary>
    private void ThemeMode_Changed()
    {
        if (_loading || !IsLoaded || ThemeCombo.SelectedItem is not Choice<AppTheme> { Value: var mode } || mode == ThemeManager.Current) return;
        ThemeManager.Apply(mode);
        Settings.Theme = mode;
        AppServices.Settings.Save();
    }

    private static Color ToColor(uint argb) => Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb);

    private static SolidColorBrush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    // ───────────────────────── Servidor (1.4.1) ─────────────────────────

    private ServerRole SelectedRole => RoleHost.IsChecked == true ? ServerRole.Host : RoleJoin.IsChecked == true ? ServerRole.Join : ServerRole.None;

    private void LoadServer(AppSettings s)
    {
        (s.ServerRole switch { ServerRole.Join => RoleJoin, ServerRole.Host => RoleHost, _ => RoleNone }).IsChecked = true;
        ServerAddressBox.Text = s.ServerAddress ?? string.Empty;
        ServerPasswordBox.Password = s.ServerPassword ?? string.Empty;
        HostedNameBox.Tag = s.DeviceName;
        HostedNameBox.Text = s.HostedServerName ?? string.Empty;
        HostedPasswordBox.Text = s.HostedServerPassword ?? string.Empty;
        TransmitViaServerToggle.IsChecked = s.TransmitViaServer;
        ApplyRolePanels();
        UpdateServerStatus();
    }

    private void Role_Checked(object sender, RoutedEventArgs e)
    {
        if (!IsInitialized || JoinPanel is null) return;
        ApplyRolePanels();
        UpdateServerStatus();
    }

    private void ApplyRolePanels()
    {
        var role = SelectedRole;
        JoinPanel.Visibility = role == ServerRole.Join ? Visibility.Visible : Visibility.Collapsed;
        HostPanel.Visibility = role == ServerRole.Host ? Visibility.Visible : Visibility.Collapsed;
        TransmitViaServerToggle.Visibility = role == ServerRole.None ? Visibility.Collapsed : Visibility.Visible;
        UpdateAddressHint();
    }

    /// <summary>Quando a aba abre: os endereços deste PC e o estado do Tailscale, lidos de novo.</summary>
    private void RefreshServerTab()
    {
        _addresses.Clear();
        IReadOnlyList<LocalNetworkAddress> addresses = AppServices.DemoMode
            ? [new("Tailscale", System.Net.IPAddress.Parse("100.101.102.103"), NetworkKind.Tailscale, true), new("Radmin VPN", System.Net.IPAddress.Parse("26.12.34.56"), NetworkKind.Radmin, true)]
            : ServerService.ReachableAddresses();
        foreach (var address in addresses) _addresses.Add(new AddressRow(address.Label, address.Address.ToString()));
        UpdateAddressHint();
        UpdateServerStatus();
        UpdateTailscaleCard();
        _ = AppServices.Tailscale.RefreshAsync();
    }

    private void UpdateAddressHint()
    {
        if (AddressHint is null) return;
        AddressHint.Text = _addresses.Count == 0
            ? "Nenhuma rede ativa (nem Tailscale nem Radmin VPN). Ligue uma delas para os outros PCs alcançarem este."
            : SelectedRole == ServerRole.Host
                ? $"Com este PC como servidor, os outros escolhem “Entrar num servidor” e colocam um destes endereços (o do Tailscale ou o do Radmin VPN). Libere as portas {AppInfo.DefaultServerControlPort} (TCP) e {AppInfo.DefaultServerMediaPort}–{AppInfo.DefaultServerRoomPort} (UDP) se o firewall pedir."
                : "Os endereços em que os outros PCs alcançam este.";
    }

    /// <summary>A linha "Conectado ao servidor X (5 pessoas)" e, no servidor deste PC, quem está nele e o que está no ar.</summary>
    private void UpdateServerStatus()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(UpdateServerStatus);
            return;
        }
        if (ServerStatusText is null) return;
        if (_serverDemo)
        {
            ServerStatusText.Text = "Este PC é o servidor \"Servidor do Kayky\": 4 pessoas nele e 1 transmissão no ar.";
            ServerDot.SetResourceReference(Shape.FillProperty, "Accent");
            HostedDetails.Text = "Servidor \"Servidor do Kayky\" · portas TCP 47810, UDP 47811 e 47812 · com senha\n"
                                 + "Na sala (4): Ana, Bruno, Carla, PC da Sala\n"
                                 + "  Ana (Tela): HEVC 1920x1080 60 FPS · entrando 18.2 Mbps, saindo 54.6 Mbps · 3 assistindo (Bruno, Carla, PC da Sala)";
            HostedDetails.Visibility = Visibility.Visible;
            return;
        }
        var service = AppServices.Server;
        var room = AppServices.Room.ServerStatus;
        var role = AppServices.DemoMode ? ServerRole.None : Settings.ServerRole;
        string text;
        string color;
        switch (role)
        {
            case ServerRole.None:
                (text, color) = ("Sem servidor: a sala e as transmissões vão direto de um PC para o outro.", "TextSubtle");
                break;
            case var _ when service.Target is null:
                (text, color) = (service.Problem ?? "Procurando o servidor…", service.Problem is null ? "Amber" : "Danger");
                break;
            default:
                text = room?.Text ?? $"Conectando ao servidor {service.Target.Label}…";
                color = room?.State switch
                {
                    RoomServerState.Connected => "Accent",
                    RoomServerState.NeedsPassword or RoomServerState.WrongPassword => "Danger",
                    _ => "Amber",
                };
                if (service.Problem is { } problem) text += "\n" + problem;
                break;
        }
        if (SelectedRole != role) text += "\nA escolha nova vale quando você salvar.";
        ServerStatusText.Text = text;
        ServerDot.SetResourceReference(Shape.FillProperty, color);
        if (service.Hosted is { } hosted)
        {
            HostedDetails.Text = hosted.Describe();
            HostedDetails.Visibility = Visibility.Visible;
        }
        else HostedDetails.Visibility = Visibility.Collapsed;
    }

    private void ServerRetry_Click(object sender, RoutedEventArgs e)
    {
        AppServices.Server.Retry();
        RefreshServerTab();
    }

    private void CopyAddress_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not string address) return;
        try
        {
            Clipboard.SetText(address);
            _shell.Toast($"Endereço {address} copiado. É só mandar para quem vai entrar no servidor.", ToastKind.Success);
        }
        catch (Exception exception) { Log.Warn($"Área de transferência ocupada: {exception.Message}"); }
    }

    private void ServerGuide_Click(object sender, RoutedEventArgs e) => MainWindow.OpenUrl(AppInfo.ServerGuideUrl);

    // ───────────────────────── Tailscale (1.4.1) ─────────────────────────

    private void UpdateTailscaleCard()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(UpdateTailscaleCard);
            return;
        }
        if (TailscaleText is null) return;
        var setup = AppServices.Tailscale;
        var installed = TailscaleSetup.IsInstalled();
        var running = AppServices.DemoMode || setup.Current?.Running == true;
        TailscaleText.Text = setup.Busy || (setup.Status is not null && !running) ? setup.Status : setup.Describe();
        TailscaleProgress.Visibility = setup.Progress is null ? Visibility.Collapsed : Visibility.Visible;
        TailscaleProgress.Value = setup.Progress ?? 0;
        TailscaleButton.Content = !installed ? "Instalar o Tailscale" : setup.Current?.NeedsLogin == true ? "Entrar no Tailscale" : "Ligar o Tailscale";
        TailscaleButton.Visibility = running && !setup.Busy ? Visibility.Collapsed : Visibility.Visible;
        TailscaleButton.IsEnabled = !setup.Busy && !AppServices.DemoMode;
        TailscaleOpenButton.Visibility = installed ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void Tailscale_Click(object sender, RoutedEventArgs e)
    {
        // No PC servidor, o Tailscale fica ligado mesmo sem ninguém logado no Windows.
        var unattended = SelectedRole == ServerRole.Host;
        var ok = TailscaleSetup.IsInstalled()
            ? await AppServices.Tailscale.ConnectAsync(_shell, unattended)
            : await AppServices.Tailscale.InstallAsync(_shell, unattended);
        if (ok) RefreshServerTab();
    }

    private void TailscaleOpen_Click(object sender, RoutedEventArgs e)
    {
        if (!TailscaleSetup.OpenApp()) _shell.Toast("Abra o Tailscale pelo menu Iniciar.", ToastKind.Info);
    }

    /// <summary>Um endereço deste PC na lista "Redes deste PC".</summary>
    public sealed record AddressRow(string Network, string Address);

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

    private void LoadSpeakers(string? selected)
    {
        IReadOnlyList<SpeakerInfo> speakers = AppServices.DemoMode
            ? [new SpeakerInfo("demo", "Fone (HyperX Cloud II)")]
            : Speakers.List();
        var choices = new List<Choice<string?>> { new(null, "Padrão do Windows") };
        choices.AddRange(speakers.Select(m => new Choice<string?>(m.Id, m.Name)));
        if (selected is not null && speakers.All(m => m.Id != selected)) choices.Add(new Choice<string?>(selected, "Saída desconectada"));
        SpeakerCombo.Fill(choices);
        SpeakerCombo.SelectValue(selected);
    }

    private void RefreshSpeakers_Click(object sender, RoutedEventArgs e) => LoadSpeakers(SpeakerCombo.Value<string?>());

    // Apertar para falar: o botão espera a próxima tecla (ou botão lateral do mouse).
    private string _pushToTalkKey = "F8";
    private bool _capturingKey;

    private void ShowPushToTalkKey()
        => PushToTalkKeyButton.Content = _capturingKey ? "Aperte a tecla…" : $"Tecla: {PushToTalkKeyWatcher.Describe(_pushToTalkKey)}";

    private void PushToTalkKey_Click(object sender, RoutedEventArgs e)
    {
        _capturingKey = !_capturingKey;
        ShowPushToTalkKey();
    }

    private void PushToTalkKey_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (!_capturingKey) return;
        var key = e.Key == System.Windows.Input.Key.System ? e.SystemKey : e.Key;
        e.Handled = true;
        _capturingKey = false;
        if (key != System.Windows.Input.Key.Escape && PushToTalkKeyWatcher.VirtualKey(key.ToString()) != 0) _pushToTalkKey = key.ToString();
        ShowPushToTalkKey();
    }

    private void PushToTalkKey_PreviewMouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (!_capturingKey || e.ChangedButton is not (System.Windows.Input.MouseButton.XButton1 or System.Windows.Input.MouseButton.XButton2)) return;
        e.Handled = true;
        _capturingKey = false;
        _pushToTalkKey = e.ChangedButton.ToString();
        ShowPushToTalkKey();
    }

    private void Sensitivity_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        _micGate.Sensitivity = e.NewValue;
        UpdateVoiceLabels();
    }

    /// <summary>(1.5.0) O volume do microfone vale na hora no teste e no "Ouvir meu microfone"; na voz, depois de Salvar.</summary>
    private void MicGain_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        lock (_micProcessor) _micProcessor.ManualGainDb = e.NewValue;
        if (_monitorProcessor is { } monitor) lock (monitor) monitor.ManualGainDb = e.NewValue;
        UpdateVoiceLabels();
    }

    private void AutoGain_Click(object sender, RoutedEventArgs e)
    {
        var on = AutoGainToggle.IsChecked == true;
        if (_monitorProcessor is { } monitor)
            lock (monitor)
            {
                monitor.AutoGain = on;
                if (!on) monitor.Reset();
            }
    }

    private void MicMeter_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateVoiceLabels();

    /// <summary>A barra vai de −60 dB (vazia) a 0 dB (cheia); a marca fica no limiar do portão da voz.</summary>
    private static double MeterFraction(double db) => Math.Clamp((db + 60) / 60, 0, 1);

    private void UpdateVoiceLabels()
    {
        if (!IsInitialized) return;
        var sensitivity = SensitivitySlider.Value;
        SensitivityValue.Text = sensitivity switch { < 0.25 => "Só voz alta", < 0.5 => "Baixa", < 0.75 => "Média", < 0.9 => "Alta", _ => "Capta tudo" };
        var gain = MicGainSlider.Value;
        MicGainValue.Text = Math.Abs(gain) < 0.5 ? "Normal (0 dB)" : gain > 0 ? $"+{gain:0} dB" : $"{gain:0} dB";
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
                var work = new float[VoiceFormat.FrameSamples];
                input.FrameReady += frame =>
                {
                    // Como na voz: o volume do microfone entra antes do portão (a barra mostra o que o portão vê).
                    var count = Math.Min(frame.Length, work.Length);
                    frame.AsSpan(0, count).CopyTo(work);
                    lock (_micProcessor) _micProcessor.ApplyManualGain(work.AsSpan(0, count));
                    _micGate.Process(work.AsSpan(0, count));
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
            var processor = new VoiceProcessor { ManualGainDb = MicGainSlider.Value, AutoGain = AutoGainToggle.IsChecked == true };
            var gate = new VoiceGate { Sensitivity = SensitivitySlider.Value };
            _monitorProcessor = processor;
            var (input, monitor) = await Task.Run(() =>
            {
                var monitor = new MicrophoneMonitor();
                try
                {
                    var input = new MicrophoneInput(id);
                    var work = new float[VoiceFormat.FrameSamples];
                    input.FrameReady += frame =>
                    {
                        // O mesmo caminho da voz: volume do microfone, portão e ajuste automático, compressor e limitador.
                        var count = Math.Min(frame.Length, work.Length);
                        frame.AsSpan(0, count).CopyTo(work);
                        lock (processor)
                        {
                            processor.ApplyManualGain(work.AsSpan(0, count));
                            gate.Process(work.AsSpan(0, count));
                            processor.Process(work.AsSpan(0, count), speech: gate.LevelDb >= gate.ThresholdDb);
                        }
                        monitor.Write(work);
                    };
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
        _monitorProcessor = null;
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

    /// <summary>Usado nas capturas de tela do CI: este PC é o servidor, com gente nele e uma transmissão no ar.</summary>
    internal void ShowServerDemo()
    {
        _serverDemo = true;
        RoleHost.IsChecked = true;
        HostedNameBox.Text = "Servidor do Kayky";
        HostedPasswordBox.Text = "senha da casa";
        TransmitViaServerToggle.IsChecked = true;
        ApplyRolePanels();
        UpdateServerStatus();
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
