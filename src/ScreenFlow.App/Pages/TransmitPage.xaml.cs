using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using ScreenFlow.App.Services;
using ScreenFlow.Core;
using ScreenFlow.Core.Diagnostics;
using ScreenFlow.Core.Media;
using ScreenFlow.Core.Settings;
using ScreenFlow.Infrastructure.Capture;
using ScreenFlow.Infrastructure.Media;
using ScreenFlow.Infrastructure.Network;
using ScreenFlow.Infrastructure.Session;

namespace ScreenFlow.App.Pages;

/// <summary>Transmissão: escolher tela, câmera ou festa de música, quem pode entrar e acompanhar ao vivo.</summary>
public partial class TransmitPage : UserControl, IShellPage
{
    private readonly MainWindow _shell;
    private readonly ObservableCollection<PendingApproval> _approvals = [];
    private readonly ObservableCollection<ViewerRow> _viewerRows = [];
    private readonly ObservableCollection<AppChoice> _apps = [];
    private int _appsVersion;
    private int _partyAppsVersion;
    private bool _fillingPartyApps;
    private HostSession? _session;
    private System.Net.IPAddress? _liveAddress;
    private IReadOnlyList<DisplayMonitor> _monitors = [];
    private bool _loaded;
    private bool _starting;
    private Task? _camerasLoading;
    private bool _camerasFound;

    public TransmitPage(MainWindow shell)
    {
        _shell = shell;
        InitializeComponent();
        ApprovalList.ItemsSource = _approvals;
        ViewerList.ItemsSource = _viewerRows;
        AppsList.ItemsSource = _apps;
        QualityCombo.Fill(Resolution.All.Select(r => new Choice<Resolution>(r, $"{r.Label} · {r.Width} × {r.Height}")));
        FpsCombo.Fill(Quality.FrameRates.Select(f => new Choice<int>(f, $"{f} FPS")));
        CameraQualityCombo.Fill(OverviewPage.CameraQualities);
        MusicCombo.Fill(AudioProfile.MusicBitrates.Select(k => new Choice<int>(k, OverviewPage.MusicLabel(k))));
        QualityCombo.SelectionChanged += (_, _) => QualityChanged();
        FpsCombo.SelectionChanged += (_, _) => QualityChanged();
        CameraQualityCombo.SelectionChanged += (_, _) => QualityChanged();
        MusicCombo.SelectionChanged += (_, _) => UpdateProfileHint();
        PartyAppCombo.SelectionChanged += (_, _) => PartyApp_Changed();
        AudioToggle.Checked += (_, _) => UpdateAudioCard();
        AudioToggle.Unchecked += (_, _) => UpdateAudioCard();
        Spectrum.Source = () => _session?.AudioMeter;
        SizeChanged += (_, _) =>
        {
            ModeGrid.Columns = ActualWidth >= 700 ? 3 : 1;
            ResponsiveGrid.Apply(Columns, ActualWidth < 1000);
        };
        shell.NetworkStatusChanged += () =>
        {
            if (_session is null && _loaded) RefreshNetwork();
        };
    }

    public bool IsRunning => _session is not null;

    /// <summary>Pessoas assistindo agora.</summary>
    public int Viewers => _viewerRows.Count;

    /// <summary>O que está indo para quem assiste agora ("tela, câmera e áudio"). Vazio quando parado.</summary>
    public string LiveSummary => _session?.Summary ?? string.Empty;

    /// <summary>Modo da transmissão no ar (null quando parado).</summary>
    public StreamMode? LiveMode => _session?.Mode;

    /// <summary>O som do PC vai junto (sempre, na festa de música).</summary>
    public bool LiveAudio => _session?.AudioEnabled == true;

    /// <summary>A câmera vai junto com a tela.</summary>
    public bool LiveCamera => _session?.HasCameraOverlay == true;

    public bool LivePinRequired => _session is not null && Settings.Pin is not null;

    /// <summary>IP em que a transmissão está (o do adaptador escolhido), para a sala da rede.</summary>
    public System.Net.IPAddress? LiveAddress => _session is null ? null : _liveAddress;

    private AppSettings Settings => AppServices.Settings.Current;

    private StreamMode SelectedMode => ModeCamera.IsChecked == true ? StreamMode.Camera : ModeParty.IsChecked == true ? StreamMode.AudioOnly : StreamMode.Screen;

    public void OnShown()
    {
        if (_session is not null) return;
        LoadFromSettings();
    }

    private void LoadFromSettings()
    {
        _loaded = true;
        QualityCombo.SelectValue(Resolution.FromLabel(Settings.Resolution));
        FpsCombo.SelectValue(Settings.Fps, f => f);
        CameraQualityCombo.SelectValue((Resolution.FromLabel(Settings.CameraResolution), Settings.CameraFps));
        MusicCombo.SelectValue(Settings.MusicBitrateKbps, k => k);
        AudioToggle.IsChecked = Settings.AudioEnabled;
        FixedQualityToggle.IsChecked = !Settings.AdaptiveQuality;
        CameraWithScreenToggle.IsChecked = Settings.CameraWithScreen;
        ApproveToggle.IsChecked = Settings.ApproveEachViewer;
        PinBox.Text = Settings.Pin ?? string.Empty;
        (Settings.Mode switch
        {
            StreamMode.Camera => ModeCamera,
            StreamMode.AudioOnly => ModeParty,
            _ => ModeScreen,
        }).IsChecked = true;
        ApplyMode(Settings.Mode);
        UpdateMutedApps();
        RefreshNetwork();
        RefreshSources();
    }

    // ───────────────────────── Modo ─────────────────────────

    private void Mode_Checked(object sender, RoutedEventArgs e)
    {
        if (IsInitialized) ApplyMode(SelectedMode);
    }

    private void ApplyMode(StreamMode mode)
    {
        ScreenOptions.Visibility = mode == StreamMode.Screen ? Visibility.Visible : Visibility.Collapsed;
        CameraOptions.Visibility = mode == StreamMode.Camera ? Visibility.Visible : Visibility.Collapsed;
        PartyOptions.Visibility = mode == StreamMode.AudioOnly ? Visibility.Visible : Visibility.Collapsed;
        AudioToggle.Visibility = FixedQualityToggle.Visibility = mode == StreamMode.AudioOnly ? Visibility.Collapsed : Visibility.Visible;
        OptionsTitle.Text = StreamModes.Label(mode);
        StartText.Text = mode == StreamMode.AudioOnly ? "Começar a festa" : "Iniciar transmissão";
        StartGlyph.Text = mode switch { StreamMode.Camera => "", StreamMode.AudioOnly => "", _ => "" };
        if (mode == StreamMode.Camera) _ = LoadCamerasAsync();
        if (mode == StreamMode.AudioOnly) _ = LoadPartyAppsAsync();
        UpdateAudioCard();
        UpdateProfileHint();
        UpdateMutedApps();
    }

    // ───────────────────────── Festa de música: o app da música ─────────────────────────

    private void RefreshPartyApps_Click(object sender, RoutedEventArgs e) => _ = LoadPartyAppsAsync();

    /// <summary>"O que toca na festa": todo o som do PC ou um app só (os que estão tocando som aparecem primeiro).</summary>
    private async Task LoadPartyAppsAsync()
    {
        var version = ++_partyAppsVersion;
        IReadOnlyList<RunningApp> running = [];
        if (AppServices.DemoMode) running = DemoData.PartyApps;
        else
        {
            try { running = await Task.Run(AppAudioCatalog.List); }
            catch (Exception exception) { Log.Warn($"Não deu para listar os apps abertos: {exception.Message}"); }
        }
        if (version != _partyAppsVersion) return;

        var saved = Settings.PartyApp;
        var choices = new List<Choice<MutedApp?>> { new(null, "Todo o som do PC") };
        choices.AddRange(running.Select(app => new Choice<MutedApp?>(new MutedApp(app.Exe, app.Name), app.PlayingSound ? $"Só {app.Name} · tocando agora" : $"Só {app.Name}")));
        if (saved is not null && running.All(app => app.Exe != saved.Exe)) choices.Add(new(saved, $"Só {saved.Name} · fechado agora"));
        _fillingPartyApps = true;
        PartyAppCombo.Fill(choices);
        PartyAppCombo.SelectedItem = choices.FirstOrDefault(c => c.Value?.Exe == saved?.Exe) ?? choices[0];
        _fillingPartyApps = false;
        UpdatePartyInfo();
    }

    private void PartyApp_Changed()
    {
        if (_fillingPartyApps || PartyAppCombo.SelectedItem is not Choice<MutedApp?> choice) return;
        var app = choice.Value;
        if (app?.Exe == Settings.PartyApp?.Exe) return;
        Settings.PartyApp = app;
        AppServices.Settings.Save();
        UpdatePartyInfo();
        if (_session?.Mode != StreamMode.AudioOnly)
        {
            UpdateMutedApps();
            return;
        }
        OnAudioSettingsChanged();
        _shell.Toast(app is null ? "A festa agora toca todo o som do PC." : $"A festa agora toca só o som de {app.Name}.", ToastKind.Success);
    }

    private void UpdatePartyInfo()
    {
        const string quality = "Opus estéreo 48 kHz, com cópia de cada pacote no seguinte: se um se perder na rede, ninguém percebe.";
        PartyInfoText.Text = Settings.PartyApp is { } app
            ? $"Toca só o som de {app.Name}: notificações, jogos e o resto do PC ficam de fora. Dá para trocar com a festa no ar. {quality}"
            : $"Toca tudo o que sai no som do PC (Spotify, YouTube, jogos). Para tocar só a música, escolha o app dela aqui em cima. {quality}";
    }

    /// <summary>Como separar o som do PC para a transmissão neste modo (a festa pode ir com o som de um app só).</summary>
    private SystemAudioOptions AudioOptions(StreamMode mode)
        => new(Settings.SeparateAppAudio, Settings.MutedApps, mode == StreamMode.AudioOnly ? Settings.PartyApp : null);

    /// <summary>"Mandar a câmera junto": mostra a escolha da câmera e já procura as câmeras do PC.</summary>
    private void CameraWithScreen_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsInitialized) return;
        var on = CameraWithScreenToggle.IsChecked == true;
        OverlayCameraPanel.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        if (on) _ = LoadCamerasAsync();
        UpdateProfileHint();
    }

    private void FixedQuality_Changed(object sender, RoutedEventArgs e)
    {
        if (IsInitialized) QualityChanged();
    }

    private void QualityChanged()
    {
        UpdateProfileHint();
        ApplyLiveQuality();
    }

    /// <summary>
    /// Qualidade trocada com a transmissão no ar (resolução, FPS ou qualidade fixa): vale na hora para quem está
    /// assistindo e fica salva para a próxima vez.
    /// </summary>
    private void ApplyLiveQuality()
    {
        if (!IsInitialized || _session is not { HasVideo: true } session) return;
        Resolution? resolution;
        int fps;
        if (session.Mode == StreamMode.Camera)
        {
            if (CameraQualityCombo.SelectedItem is not Choice<(Resolution Resolution, int Fps)> camera) return;
            (resolution, fps) = camera.Value;
        }
        else
        {
            resolution = QualityCombo.Value<Resolution>();
            fps = FpsCombo.SelectedItem is Choice<int> f ? f.Value : 0;
        }
        if (resolution is null || fps <= 0) return;
        var adaptive = FixedQualityToggle.IsChecked != true;
        if (!session.ChangeQuality(resolution, fps, adaptive)) return;

        if (session.Mode == StreamMode.Camera)
        {
            Settings.CameraResolution = resolution.Label;
            Settings.CameraFps = fps;
        }
        else
        {
            Settings.Resolution = resolution.Label;
            Settings.Fps = fps;
        }
        Settings.AdaptiveQuality = adaptive;
        AppServices.Settings.Save();
        _shell.Toast(adaptive
            ? $"Qualidade agora: {resolution.Label} a {fps} FPS. Quem está assistindo já recebe assim."
            : $"Qualidade fixa em {resolution.Label} a {fps} FPS. Quem está assistindo já recebe assim.", ToastKind.Success);
    }

    /// <summary>
    /// Ao vivo, trava só o que não dá para trocar com a transmissão no ar (o modo, a tela ou câmera, quem pode entrar).
    /// A qualidade continua livre.
    /// </summary>
    private void LockWhileLive(bool live)
    {
        ModeGrid.IsEnabled = AccessPanel.IsEnabled = !live;
        SourceRow.IsEnabled = CameraWithScreenToggle.IsEnabled = OverlayCameraPanel.IsEnabled = CameraRow.IsEnabled = !live;
        MusicCombo.IsEnabled = AudioToggle.IsEnabled = !live;
    }

    private void UpdateAudioCard()
    {
        var mode = _session?.Mode ?? SelectedMode;
        var audio = mode == StreamMode.AudioOnly || (_session?.AudioEnabled ?? AudioToggle.IsChecked == true);
        AudioCard.Visibility = audio ? Visibility.Visible : Visibility.Collapsed;
    }

    private const string FixedHint = " Qualidade fixa: ela não baixa sozinha, então se a rede não aguentar a imagem pode travar.";

    private void UpdateProfileHint()
    {
        if (!IsInitialized) return;
        switch (SelectedMode)
        {
            case StreamMode.AudioOnly:
                var music = MusicCombo.Value<int>();
                ProfileHint.Text = $"Cerca de {music * 2} kbps por pessoa (o dobro do Opus por causa da cópia de segurança). Cabe muita gente: 20 pessoas usam uns {music * 2 * 20 / 1000.0:0.#} Mbps de upload.";
                break;
            case StreamMode.Camera when CameraQualityCombo.SelectedItem is Choice<(Resolution Resolution, int Fps)> camera:
                var codec = Settings.PreferredCodec ?? VideoCodec.Hevc;
                var kbps = Quality.TargetBitrateKbps(camera.Value.Resolution, camera.Value.Fps, codec);
                ProfileHint.Text = $"Cerca de {kbps / 1000.0:0.#} Mbps por pessoa em {Quality.CodecLabel(codec)}. A câmera usa o formato mais próximo que ela suportar."
                                   + (FixedQualityToggle.IsChecked == true ? FixedHint : string.Empty);
                break;
            default:
                if (QualityCombo.Value<Resolution>() is not { } resolution || FpsCombo.SelectedItem is not Choice<int> fps) return;
                var screenCodec = Settings.PreferredCodec ?? VideoCodec.Hevc;
                var target = Settings.BitrateKbps > 0 ? Settings.BitrateKbps : Quality.TargetBitrateKbps(resolution, fps.Value, screenCodec);
                var text = $"Perfil {Quality.TierLabel(resolution, fps.Value)}: cerca de {target / 1000.0:0} Mbps por pessoa em {Quality.CodecLabel(screenCodec)}.";
                if (CameraWithScreenToggle.IsChecked == true)
                    text += $" A câmera junto usa mais uns {Quality.CameraOverlayBitrateKbps(1280, 720, 30, screenCodec) / 1000.0:0.#} Mbps por pessoa.";
                var fixedQuality = FixedQualityToggle.IsChecked == true;
                if (Settings.UploadMbps > 0)
                {
                    var people = Math.Max(1, (int)(Settings.UploadMbps * 0.8 * 1000 / target));
                    text += $" Com o seu upload ({Settings.UploadMbps:0} Mbps) cabem umas {people} pessoas nessa qualidade"
                            + (fixedQuality ? "." : "; acima disso a qualidade se ajusta sozinha.");
                }
                else if (!fixedQuality) text += " Com mais gente assistindo, o upload é dividido e a qualidade se ajusta sozinha.";
                if (fixedQuality) text += FixedHint;
                ProfileHint.Text = text;
                break;
        }
    }

    // ───────────────────────── Rede, telas e câmeras ─────────────────────────

    private void RefreshNetwork()
    {
        IReadOnlyList<NetworkAdapterInfo> adapters = AppServices.DemoMode ? [DemoData.Radmin] : RadminNetwork.GetIPv4Adapters().Where(a => a.IsUp).ToList();
        var radmin = adapters.FirstOrDefault(a => a.IsRadmin && Core.Network.RadminAddress.IsRadmin(a.Address));
        var previous = AdapterCombo.Value<NetworkAdapterInfo>();
        AdapterCombo.Fill(adapters.Select(a => new Choice<NetworkAdapterInfo>(a, a.IsRadmin ? $"Radmin VPN · {a.Address}" : $"{a.Name} · {a.Address}")));
        AdapterCombo.SelectValue(adapters.FirstOrDefault(a => a.Address.Equals(previous?.Address)) ?? radmin ?? adapters.FirstOrDefault()!);
        RadminMissing.Visibility = radmin is null ? Visibility.Visible : Visibility.Collapsed;
        UpdateIp();
    }

    private void UpdateIp()
    {
        if (AdapterCombo.Value<NetworkAdapterInfo>() is { } adapter)
        {
            IpText.Text = adapter.Address.ToString();
            IpHint.Text = adapter.IsRadmin ? "Passe este IP para quem vai assistir." : "Este IP não é do Radmin: só funciona para quem está na mesma rede local.";
        }
        else
        {
            IpText.Text = "—";
            IpHint.Text = "Nenhuma rede ativa encontrada.";
        }
    }

    private void Adapter_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateIp();
    private void RefreshNetwork_Click(object sender, RoutedEventArgs e) => RefreshNetwork();
    private void InstallRadmin_Click(object sender, RoutedEventArgs e) => MainWindow.OpenUrl(AppInfo.RadminDownloadUrl);

    private void RefreshSources()
    {
        _monitors = AppServices.Monitors();
        var sources = _monitors.Select(CaptureTarget.FromMonitor).ToList();
        if (!AppServices.DemoMode) sources.AddRange(WindowDiscovery.GetWindows());
        var previous = SourceCombo.SelectedItem as CaptureTarget;
        SourceCombo.ItemsSource = sources;
        SourceCombo.SelectedItem = sources.FirstOrDefault(s => s.Id == previous?.Id) ?? sources.FirstOrDefault();
    }

    private void RefreshSources_Click(object sender, RoutedEventArgs e) => RefreshSources();

    /// <summary>Quatro números lado a lado quando cabem; senão dois por linha (o texto nunca fica em pé).</summary>
    private void StatsGrid_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        var columns = StatsGrid.ActualWidth >= 470 ? 4 : 2;
        if (StatsGrid.Columns == columns) return;
        StatsGrid.Columns = columns;
        foreach (FrameworkElement tile in StatsGrid.Children)
            tile.Margin = new Thickness(0, 0, 12, columns == 4 ? 0 : 12);
    }

    /// <summary>
    /// Procura as câmeras do PC uma vez. Quem pedir enquanto a busca está andando espera a mesma busca (iniciar pela
    /// Visão geral não pode começar com a lista ainda vazia); sem câmera nenhuma, a próxima chamada procura de novo.
    /// </summary>
    private Task LoadCamerasAsync(bool force = false)
    {
        if (!force && _camerasLoading is { } loading && (!loading.IsCompleted || _camerasFound)) return loading;
        return _camerasLoading = FillCamerasAsync();
    }

    private async Task FillCamerasAsync()
    {
        IReadOnlyList<CameraDevice> cameras = AppServices.DemoMode ? [new CameraDevice("demo", "Logitech C920 HD Pro")] : await CameraDiscovery.GetCamerasAsync();
        // A mesma lista serve para o modo câmera e para a câmera que vai junto com a tela.
        var preferred = cameras.FirstOrDefault(c => c.Id == Settings.CameraId) ?? cameras.FirstOrDefault();
        foreach (var combo in new[] { CameraCombo, OverlayCameraCombo })
        {
            var previous = combo.SelectedItem as CameraDevice;
            combo.Items.Clear();
            foreach (var camera in cameras) combo.Items.Add(camera);
            combo.SelectedItem = cameras.FirstOrDefault(c => c.Id == previous?.Id) ?? preferred;
            if (cameras.Count == 0)
            {
                combo.Items.Add("Nenhuma câmera encontrada");
                combo.SelectedIndex = 0;
            }
        }
        _camerasFound = cameras.Count > 0;
    }

    private async void RefreshCameras_Click(object sender, RoutedEventArgs e) => await LoadCamerasAsync(force: true);

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        if (IpText.Text == "—") return;
        try
        {
            Clipboard.SetText(IpText.Text);
            _shell.Toast($"IP {IpText.Text} copiado. É só mandar para quem vai assistir.", ToastKind.Success);
        }
        catch (Exception exception) { Log.Warn($"Área de transferência ocupada: {exception.Message}"); }
    }

    // ───────────────────────── Iniciar / parar ─────────────────────────

    /// <summary>Usado pela Visão geral: carrega as escolhas salvas e começa.</summary>
    public async Task StartFromSettingsAsync()
    {
        if (_session is not null) return;
        LoadFromSettings();
        if (Settings.Mode == StreamMode.Camera || Settings.CameraWithScreen) await LoadCamerasAsync();
        await StartAsync();
    }

    private async void Start_Click(object sender, RoutedEventArgs e) => await StartAsync();

    private async Task StartAsync()
    {
        if (_session is not null || _starting) return;
        if (AdapterCombo.Value<NetworkAdapterInfo>() is not { } adapter)
        {
            ShowError("Nenhuma rede ativa. Conecte o Radmin VPN e tente de novo.");
            return;
        }
        var mode = SelectedMode;
        CaptureTarget target;
        DisplayMonitor? monitor = null;
        CameraOverlay? overlay = null;
        Resolution resolution;
        int fps;
        switch (mode)
        {
            case StreamMode.Camera:
                if (CameraCombo.SelectedItem is not CameraDevice camera)
                {
                    ShowError("Nenhuma câmera encontrada. Conecte uma webcam e clique em atualizar.");
                    return;
                }
                (resolution, fps) = CameraQualityCombo.Value<(Resolution, int)>();
                resolution ??= Resolution.P1080;
                if (fps <= 0) fps = 30;
                target = CaptureTarget.FromCamera(camera.Id, camera.Name, resolution.Width, resolution.Height, fps);
                Settings.CameraId = camera.Id;
                Settings.CameraResolution = resolution.Label;
                Settings.CameraFps = fps;
                break;
            case StreamMode.AudioOnly:
                target = new CaptureTarget(CaptureKind.Monitor, string.Empty, "Festa de música", 0, 0, 0);
                resolution = Resolution.P720;
                fps = 30;
                Settings.MusicBitrateKbps = MusicCombo.Value<int>() is > 0 and var kbps ? kbps : Settings.MusicBitrateKbps;
                break;
            default:
                if (SourceCombo.SelectedItem is not CaptureTarget source)
                {
                    ShowError("Escolha um monitor ou uma janela.");
                    return;
                }
                target = source;
                monitor = source.Kind == CaptureKind.Monitor ? _monitors.FirstOrDefault(m => m.DeviceName == source.Id) : null;
                resolution = QualityCombo.Value<Resolution>() ?? Resolution.P1080;
                fps = FpsCombo.SelectedItem is Choice<int> f ? f.Value : 60;
                if (CameraWithScreenToggle.IsChecked == true)
                {
                    if (OverlayCameraCombo.SelectedItem is not CameraDevice overlayCamera)
                    {
                        ShowError("Nenhuma câmera encontrada para mandar junto. Conecte uma webcam e clique em atualizar, ou desligue “Mandar a câmera junto”.");
                        return;
                    }
                    // A câmera vai num quadrinho: 720p a 30 FPS sobra (e pesa pouco no upload).
                    overlay = new CameraOverlay(overlayCamera.Id, overlayCamera.Name, 1280, 720, 30);
                    Settings.CameraId = overlayCamera.Id;
                }
                Settings.Resolution = resolution.Label;
                Settings.Fps = fps;
                break;
        }
        var pin = PinBox.Text.Trim();
        if (pin.Length > 0 && !Core.Security.SessionCrypto.IsValidPin(pin))
        {
            ShowError("O PIN precisa ter pelo menos 4 caracteres.");
            return;
        }

        Settings.Mode = mode;
        Settings.AudioEnabled = AudioToggle.IsChecked == true;
        if (mode != StreamMode.AudioOnly) Settings.AdaptiveQuality = FixedQualityToggle.IsChecked != true;
        Settings.CameraWithScreen = CameraWithScreenToggle.IsChecked == true;
        Settings.ApproveEachViewer = ApproveToggle.IsChecked == true;
        Settings.Pin = pin.Length == 0 ? null : pin;
        AppServices.Settings.Save();

        _starting = true;
        StartButton.IsEnabled = false;
        SetBadge(mode == StreamMode.Camera ? "Ligando a câmera…" : "Iniciando…", "Warning", "AmberSoft");
        StateText.Text = mode == StreamMode.AudioOnly ? "Preparando o som do PC…" : "Preparando captura e encoder…";
        try
        {
            var hardware = await AppServices.Hardware;
            if (mode != StreamMode.AudioOnly && hardware.Encoders.Count == 0)
                throw new InvalidOperationException("Nenhum encoder de vídeo foi encontrado. Confira se a pasta 'ffmpeg' está junto do ScreenFlow.exe (veja Configurações › Diagnóstico).");

            var options = new HostOptions(
                Settings.DeviceName, adapter.Address, adapter.Broadcast,
                Settings.MediaPort, Settings.ControlPort, Settings.DiscoveryPort,
                Settings.Pin, Settings.ApproveEachViewer, Settings.MaxViewers,
                target, monitor, resolution, fps, mode == StreamMode.Screen ? Settings.BitrateKbps : 0,
                Settings.PreferredCodec, Settings.PreferredEncoder, hardware.Encoders,
                mode == StreamMode.AudioOnly || Settings.AudioEnabled, Settings.AudioBitrateKbps, Settings.AdaptiveQuality, Settings.FecGroupSize, Settings.UploadMbps,
                mode, Settings.MusicBitrateKbps, OverlayCamera: overlay,
                AudioSeparation: AudioOptions(mode));

            var session = new HostSession(options) { ApprovalHandler = RequestApprovalAsync };
            session.StatusChanged += message => Dispatcher.BeginInvoke(() => StateText.Text = message);
            session.ViewersChanged += () => Dispatcher.BeginInvoke(RefreshViewers);
            session.StatisticsUpdated += stats => Dispatcher.BeginInvoke(() => ShowStatistics(stats));
            session.BandwidthMeasured += (name, mbps) => Dispatcher.BeginInvoke(() => OnBandwidthMeasured(name, mbps));
            await Task.Run(session.Start);
            _liveAddress = adapter.Address;
            _session = session;

            LockWhileLive(true);
            StartButton.Visibility = Visibility.Collapsed;
            StopButton.Visibility = Visibility.Visible;
            SetBadge("Ao vivo", "Pink", "PinkSoft");
            StateText.Text = mode == StreamMode.AudioOnly
                ? $"Festa no ar em {adapter.Address}. Quem quiser ouvir só precisa digitar esse IP."
                : $"Transmitindo {session.Summary} em {adapter.Address}. Quem quiser assistir só precisa digitar esse IP.";
            StatFpsLabel.Text = mode == StreamMode.AudioOnly ? "Opus" : "FPS";
            StatBitrateLabel.Text = mode == StreamMode.AudioOnly ? "Áudio enviado" : "Vídeo";
            AudioDescription.Text = session.AudioDescription ?? (session.AudioEnabled ? "Som do PC" : "Som desligado");
            UpdateAudioCard();
            UpdateMutedApps();
            _shell.NotifyLiveStateChanged();
            _shell.Toast(mode == StreamMode.AudioOnly ? "A festa começou. Toque uma música no PC!" : "Transmissão no ar.", ToastKind.Success);
        }
        catch (System.Net.Sockets.SocketException exception) when (exception.SocketErrorCode == System.Net.Sockets.SocketError.AddressAlreadyInUse)
        {
            ShowError($"As portas {Settings.ControlPort}/{Settings.MediaPort} já estão em uso. Feche outra cópia do ScreenFlow ou mude as portas em Configurações.");
        }
        catch (Exception exception)
        {
            Log.Error("Falha ao iniciar transmissão", exception);
            ShowError(exception.Message);
        }
        finally
        {
            _starting = false;
            StartButton.IsEnabled = true;
        }
    }

    private async void Stop_Click(object sender, RoutedEventArgs e) => await StopAsync();

    public async Task StopAsync()
    {
        var session = _session;
        _session = null;
        foreach (var pending in _approvals.ToList()) pending.Completion.TrySetResult(false);
        if (session is not null) await session.DisposeAsync();
        if (!Dispatcher.CheckAccess()) return;
        _approvals.Clear();
        ApprovalCard.Visibility = Visibility.Collapsed;
        _viewerRows.Clear();
        NoViewers.Visibility = Visibility.Visible;
        ViewerCount.Text = string.Empty;
        LockWhileLive(false);
        StartButton.Visibility = Visibility.Visible;
        StopButton.Visibility = Visibility.Collapsed;
        StatViewers.Text = "0";
        StatFps.Text = StatBitrate.Text = StatUpload.Text = "—";
        StatDetails.Text = "Os números aparecem quando alguém entra.";
        AudioState.Text = "Sem som";
        UpdateMutedApps();
        SetBadge("Parado", "TextSubtle", "SurfaceRaised");
        if (session is not null) StateText.Text = "Transmissão encerrada.";
        Telemetry.EndHost();
        UpdateAudioCard();
        _shell.NotifyLiveStateChanged();
        if (session is not null) _shell.ShowAfterSessionEnded(this);
    }

    private void ShowError(string message)
    {
        StateText.Text = message;
        SetBadge("Erro", "Danger", "DangerSoft");
        _shell.Toast(message, ToastKind.Error);
    }

    private void SetBadge(string text, string foreground, string background)
    {
        LiveText.Text = text;
        LiveBadge.SetResourceReference(TextElement.ForegroundProperty, foreground);
        LiveBadge.SetResourceReference(Border.BackgroundProperty, background);
        LiveBadge.SetResourceReference(Border.BorderBrushProperty, background);
        LiveDot.SetResourceReference(Shape.FillProperty, foreground);
    }

    // ───────────────────────── Pessoas e aprovação ─────────────────────────

    private Task<bool> RequestApprovalAsync(ApprovalRequest request, CancellationToken token)
    {
        var pending = new PendingApproval(request.Id, request.Name, request.Address);
        token.Register(() => pending.Completion.TrySetResult(false));
        Dispatcher.BeginInvoke(() =>
        {
            _approvals.Add(pending);
            ApprovalCard.Visibility = Visibility.Visible;
            _shell.Toast($"{request.Name} ({request.Address}) quer entrar.", ToastKind.Info, "Ver pedido", () => _shell.Navigate(this));
            System.Media.SystemSounds.Asterisk.Play();
        });
        return pending.Completion.Task.ContinueWith(t =>
        {
            Dispatcher.BeginInvoke(() =>
            {
                _approvals.Remove(pending);
                if (_approvals.Count == 0) ApprovalCard.Visibility = Visibility.Collapsed;
            });
            return t.Result;
        });
    }

    private void Approve_Click(object sender, RoutedEventArgs e) => ((sender as FrameworkElement)?.Tag as PendingApproval)?.Completion.TrySetResult(true);
    private void Deny_Click(object sender, RoutedEventArgs e) => ((sender as FrameworkElement)?.Tag as PendingApproval)?.Completion.TrySetResult(false);

    /// <summary>Atualiza a lista no lugar (sem recriar as linhas), para não piscar a cada segundo.</summary>
    private void RefreshViewers()
    {
        if (_session is null) return;
        var audioOnly = _session.Mode == StreamMode.AudioOnly;
        var current = _session.Viewers.Where(v => v.State == ViewerState.Streaming).ToList();
        foreach (var gone in _viewerRows.Where(r => current.All(v => v.Id != r.Id)).ToList()) _viewerRows.Remove(gone);
        foreach (var info in current)
        {
            var row = _viewerRows.FirstOrDefault(r => r.Id == info.Id);
            if (row is null) _viewerRows.Add(row = new ViewerRow(info.Id, info.Name, info.Address));
            row.Update(info, audioOnly);
        }
        var changed = StatViewers.Text != _viewerRows.Count.ToString();
        NoViewers.Visibility = _viewerRows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ViewerCount.Text = _viewerRows.Count switch { 0 => string.Empty, 1 => "1 pessoa", var n => $"{n} pessoas" };
        StatViewers.Text = _viewerRows.Count.ToString();
        if (changed) _shell.NotifyLiveStateChanged();
    }

    private void ShowStatistics(HostStatistics stats)
    {
        if (_session is null) return;
        RefreshViewers();
        var audioOnly = stats.Mode == StreamMode.AudioOnly;
        StatViewers.Text = stats.Viewers.ToString();
        if (audioOnly)
        {
            StatFps.Text = $"{_session.AudioProfile.BitrateKbps}k";
            StatBitrate.Text = stats.Viewers == 0 ? "—" : $"{stats.AudioKbps:0}k";
        }
        else
        {
            StatFps.Text = stats.Viewers == 0 ? "—" : $"{stats.EncodeFps:0}";
            StatBitrate.Text = stats.Viewers == 0 ? "—" : $"{stats.BitrateMbps:0.#}";
        }
        StatUpload.Text = stats.Viewers == 0 ? "—" : $"{stats.UploadMbps:0.#}";
        StatDetails.Text = stats.Viewers == 0
            ? (audioOnly ? "O som fica pronto e é enviado assim que alguém entra." : "Captura e encoder ficam pausados até alguém entrar (vídeo e upload em Mbps).")
            : audioOnly
                ? $"{stats.Encoder} · {_session.AudioDescription} · upload em Mbps"
                : $"{stats.Encoder} · {stats.Capture} · alvo {stats.Target} · {stats.Reason} · vídeo e upload em Mbps";
        if (stats.Camera.Length > 0) StatDetails.Text += "\n" + stats.Camera;
        var level = _session.AudioMeter?.Level ?? 0;
        AudioState.Text = !_session.AudioEnabled ? "Desligado" : level > 0.05 ? "Tocando" : "Silêncio";
        if (_session.AudioDescription is { } description) AudioDescription.Text = description;
        UpdateMutedApps();
        Telemetry.Record(stats, _session.Viewers.Where(v => v.State == ViewerState.Streaming).ToList());
    }

    private void Kick_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is Guid id) _session?.Kick(id);
    }

    private async void Bandwidth_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not Guid id || _session is null) return;
        var row = _viewerRows.FirstOrDefault(r => r.Id == id);
        if (row is null) return;
        row.Testing = true;
        try { await _session.RunBandwidthTestAsync(id); }
        finally { row.Testing = false; }
    }

    private void OnBandwidthMeasured(string name, double mbps)
    {
        _shell.Toast($"Banda até {name}: {mbps:0} Mbps. Esse valor fica salvo como o seu upload, para dividir a qualidade entre quem assiste.", ToastKind.Success);
        Settings.UploadMbps = Math.Max(mbps, Settings.UploadMbps * 0.5);
        AppServices.Settings.Save();
        UpdateProfileHint();
    }

    // ───────────────────────── Apps cujo som fica fora ─────────────────────────

    private void ChooseApps_Click(object sender, RoutedEventArgs e)
    {
        // Alinha a borda direita da janelinha (380 + margens de 14) com a do botão.
        AppsPopup.HorizontalOffset = ChooseAppsButton.ActualWidth - 408 + 14;
        AppsPopup.IsOpen = true;
        _ = LoadAppsAsync();
    }

    private void RefreshApps_Click(object sender, RoutedEventArgs e) => _ = LoadAppsAsync();

    private void AppsDone_Click(object sender, RoutedEventArgs e) => AppsPopup.IsOpen = false;

    private void AppsPopup_Closed(object? sender, EventArgs e) => ApplyMutedApps();

    /// <summary>Lista os apps abertos (os que estão tocando som primeiro) com os já tirados marcados.</summary>
    private async Task LoadAppsAsync()
    {
        var version = ++_appsVersion;
        var muted = (_apps.Count > 0 ? _apps.Where(a => a.Muted).Select(a => new MutedApp(a.Exe, a.Name)) : Settings.MutedApps).ToList();
        AppsStatus.Text = "Procurando os apps abertos…";
        AppsStatus.Visibility = Visibility.Visible;
        IReadOnlyList<RunningApp> running;
        try { running = await Task.Run(AppAudioCatalog.List); }
        catch (Exception exception)
        {
            Log.Warn($"Não deu para listar os apps abertos: {exception.Message}");
            running = [];
        }
        if (version != _appsVersion) return;

        var mutedExes = muted.Select(a => a.Exe).ToHashSet();
        var rows = running.Select(app => new AppChoice(app.Exe, app.Name, app.PlayingSound ? "Tocando som agora" : "Aberto", ToImage(app.IconPng), mutedExes.Contains(app.Exe)))
            .Concat(muted.Where(m => running.All(r => r.Exe != m.Exe)).Select(m => new AppChoice(m.Exe, m.Name, "Fechado agora", null, true)))
            .OrderByDescending(a => a.Muted)
            .ToList();
        _apps.Clear();
        foreach (var row in rows) _apps.Add(row);
        AppsStatus.Text = "Nenhum app com janela ou som aberto agora. Abra o app e clique em atualizar.";
        AppsStatus.Visibility = _apps.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        AppsHint.Text = ProcessLoopbackSupported ? "Vale na hora, até no meio da transmissão." : "Precisa do Windows 10 versão 2004 ou mais novo.";
    }

    private static bool ProcessLoopbackSupported => ProcessLoopbackCapture.Supported;

    /// <summary>Salva os apps marcados e, se estiver ao vivo, troca o som na hora.</summary>
    private void ApplyMutedApps()
    {
        if (_apps.Count == 0 || AppServices.DemoMode) return;
        var muted = MutedApp.Normalize(_apps.Where(a => a.Muted).Select(a => new MutedApp(a.Exe, a.Name)));
        if (muted.Select(a => a.Exe).SequenceEqual(Settings.MutedApps.Select(a => a.Exe))) return;
        Settings.MutedApps = muted;
        AppServices.Settings.Save();
        OnAudioSettingsChanged();
        var names = muted.Select(a => a.Name).ToList();
        _shell.Toast(names.Count == 0
            ? "Os outros apps voltam a ir no som da transmissão."
            : $"O som de {MutedApp.JoinNames(names)} fica fora da transmissão.", ToastKind.Success);
    }

    /// <summary>As opções do som do PC mudaram (aqui ou nas Configurações): vale na hora para a transmissão no ar.</summary>
    public void OnAudioSettingsChanged()
    {
        if (_session is { } session) session.ConfigureAudio(AudioOptions(session.Mode));
        UpdateMutedApps();
    }

    /// <summary>"Fica fora da transmissão": o que foi pedido ou, ao vivo, o que está acontecendo de fato.</summary>
    private void UpdateMutedApps()
    {
        if (!IsInitialized) return;
        var names = Settings.MutedApps.Select(a => a.Name).ToList();
        var live = _session?.AudioSeparated;
        // Festa com o som de um app só: tirar apps não faz diferença.
        var partyApp = (_session?.Mode ?? SelectedMode) == StreamMode.AudioOnly ? Settings.PartyApp : null;
        ChooseAppsButton.IsEnabled = partyApp is null;
        if (_session?.AudioOnlyApp is { } only)
            MutedAppsText.Text = $"Tudo menos {only}: a festa toca só o som dele.";
        else if (_session is null && partyApp is not null)
            MutedAppsText.Text = $"Tudo menos {partyApp.Name}: a festa toca só o som dele.";
        else if (live == true)
            MutedAppsText.Text = $"Agora vai o som do PC {_session!.AudioSeparation}.";
        else if (live == false)
            MutedAppsText.Text = Settings.SeparateAppAudio
                ? "Agora vai todo o som do PC, com a voz da rede junto: o Windows não deixou separar o som por app."
                : "Agora vai todo o som do PC, com a voz da rede junto (separar o som por app está desligado nas Configurações).";
        else if (!Settings.SeparateAppAudio)
            MutedAppsText.Text = "Nada: separar o som por app está desligado nas Configurações, então vai todo o som do PC, até a voz da rede.";
        else
            MutedAppsText.Text = names.Count == 0
                ? "A voz da rede e os sons do ScreenFlow. Dá para tirar outros apps também."
                : $"A voz da rede, os sons do ScreenFlow, {MutedApp.JoinNames(names)}.";
    }

    private static ImageSource? ToImage(byte[]? png)
    {
        if (png is null) return null;
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = new System.IO.MemoryStream(png);
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception exception) when (exception is NotSupportedException or InvalidOperationException or System.IO.IOException) { return null; }
    }

    /// <summary>Um app na lista de "Tirar o som de apps".</summary>
    public sealed class AppChoice(string exe, string name, string detail, ImageSource? icon, bool muted) : INotifyPropertyChanged
    {
        private bool _muted = muted;

        public string Exe { get; } = exe;
        public string Name { get; } = name;
        public string Detail { get; } = detail;
        public ImageSource? Icon { get; } = icon;
        public string Initial => Name.Length > 0 ? char.ToUpperInvariant(Name[0]).ToString() : "?";

        public bool Muted
        {
            get => _muted;
            set
            {
                if (_muted == value) return;
                _muted = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Muted)));
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
    }

    // ───────────────────────── Capturas de tela (CI) ─────────────────────────

    /// <summary>Mostra a tela "ao vivo" com dados de exemplo, sem abrir portas nem capturar nada.</summary>
    internal void ShowDemo()
    {
        LoadFromSettings();
        ModeScreen.IsChecked = true;
        CameraWithScreenToggle.IsChecked = true;
        var meter = DemoData.LiveMeter();
        Spectrum.Source = () => meter;
        LockWhileLive(true);
        StartButton.Visibility = Visibility.Collapsed;
        StopButton.Visibility = Visibility.Visible;
        SetBadge("Ao vivo", "Pink", "PinkSoft");
        StateText.Text = $"Transmitindo tela, câmera e áudio em {DemoData.Radmin.Address}. Quem quiser assistir só precisa digitar esse IP.";
        StatViewers.Text = "3";
        StatFps.Text = "165";
        StatBitrate.Text = "38.5";
        StatUpload.Text = "117";
        StatDetails.Text = "HEVC · hevc_nvenc · Windows.Graphics.Capture · alvo 1440p 165 FPS 40 Mbps · rede estável · vídeo e upload em Mbps"
                           + "\nCâmera junto: 1280×720 · 30 FPS · 2.1 Mbps";
        AudioDescription.Text = "Alto-falantes (Realtek) · Opus 160 kbps · sem Spotify, Discord e a voz da rede";
        AudioState.Text = "Tocando";
        MutedAppsText.Text = "Agora vai o som do PC sem Spotify, Discord e a voz da rede.";
        _viewerRows.Clear();
        foreach (var info in DemoData.Audience())
        {
            var row = new ViewerRow(info.Id, info.Name, info.Address);
            row.Update(info, false);
            _viewerRows.Add(row);
        }
        NoViewers.Visibility = Visibility.Collapsed;
        ViewerCount.Text = "3 pessoas";
        UpdateAudioCard();
    }

    /// <summary>Abre "Tirar o som de apps" com apps de exemplo (para a captura de tela).</summary>
    internal void ShowAppsDemo()
    {
        _apps.Clear();
        foreach (var app in new[]
                 {
                     new AppChoice("spotify.exe", "Spotify", "Tocando som agora", null, true),
                     new AppChoice("discord.exe", "Discord", "Aberto", null, true),
                     new AppChoice("forzahorizon5.exe", "Forza Horizon 5", "Tocando som agora", null, false),
                     new AppChoice("chrome.exe", "Google Chrome", "Tocando som agora", null, false),
                     new AppChoice("steamwebhelper.exe", "Steam", "Aberto", null, false),
                 })
            _apps.Add(app);
        AppsStatus.Visibility = Visibility.Collapsed;
        AppsPopup.IsOpen = true;
    }

    public sealed class PendingApproval(Guid id, string name, string address)
    {
        public Guid Id { get; } = id;
        public string Name { get; } = name;
        public string Address { get; } = address;
        public string Initial => Name.Length > 0 ? char.ToUpperInvariant(Name[0]).ToString() : "?";
        public TaskCompletionSource<bool> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    public sealed class ViewerRow(Guid id, string name, string address) : INotifyPropertyChanged
    {
        private string _details = "Conectando…";
        private bool _testing;

        public Guid Id { get; } = id;
        public string Name { get; } = name;
        public string Address { get; } = address;
        public string Initial => Name.Length > 0 ? char.ToUpperInvariant(Name[0]).ToString() : "?";
        public string TestText => _testing ? "Testando…" : "Testar banda";
        public bool CanTest => !_testing;

        public string Details
        {
            get => _details;
            private set => Set(ref _details, value);
        }

        public bool Testing
        {
            get => _testing;
            set
            {
                if (!Set(ref _testing, value)) return;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(TestText)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanTest)));
            }
        }

        public void Update(ViewerInfo info, bool audioOnly)
            => Details = info.RoundTripMs <= 0
                ? "Conectando…"
                : audioOnly
                    ? $"Ouvindo · ping {info.RoundTripMs:0} ms · perda {info.LossRate:P1}"
                    : $"Recebendo {info.ReceivedMbps:0.#} Mbps · ping {info.RoundTripMs:0} ms · latência {info.LatencyMs:0} ms · perda {info.LossRate:P1}";

        public event PropertyChangedEventHandler? PropertyChanged;

        private bool Set<T>(ref T field, T value, [CallerMemberName] string? property = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value)) return false;
            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
            return true;
        }
    }
}
