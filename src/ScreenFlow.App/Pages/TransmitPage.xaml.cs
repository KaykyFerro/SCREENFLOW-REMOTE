using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using ScreenFlow.App.Services;
using ScreenFlow.Core;
using ScreenFlow.Core.Diagnostics;
using ScreenFlow.Core.Media;
using ScreenFlow.Core.Protocol;
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
    private readonly DispatcherTimer _mediaTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private MediaControls? _media;
    private string? _mediaId;
    private bool _mediaPinned;
    private bool _mediaSeeking;
    private bool _fillingMediaSessions;
    private string? _mediaArtKey;
    private string? _liveWindowExe;
    private readonly DispatcherTimer _remoteAppsTimer = new() { Interval = TimeSpan.FromSeconds(6) };
    private MediaStateMessage? _publishedMedia;
    private long _publishedMediaAt;
    private byte[]? _coverSource;
    private string? _cover;
    private int _remoteAppsVersion;

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
        MediaSessionCombo.SelectionChanged += (_, _) => MediaSession_Changed();
        _mediaTimer.Tick += (_, _) => UpdateMedia();
        _remoteAppsTimer.Tick += (_, _) =>
        {
            if (_session is { HasRemoteViewers: true }) _ = PublishAudioAppsAsync();
        };
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
            else UpdateRadminPanel();
        };
        AppServices.RadminSetup.Changed += UpdateRadminPanel;
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
        ViewerControlsToggle.IsChecked = Settings.ViewerControls;
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
        if (!_syncingQuality) ApplyLiveQuality();
    }

    private bool _syncingQuality;

    /// <summary>
    /// A qualidade foi salva nas Configurações: com a transmissão no ar, vale na hora (uma troca só). true quando mudou
    /// (e o aviso já apareceu).
    /// </summary>
    public bool ApplySettingsQualityLive()
    {
        if (_session is not { HasVideo: true }) return false;
        _syncingQuality = true;
        try
        {
            QualityCombo.SelectValue(Resolution.FromLabel(Settings.Resolution));
            FpsCombo.SelectValue(Settings.Fps, f => f);
            CameraQualityCombo.SelectValue((Resolution.FromLabel(Settings.CameraResolution), Settings.CameraFps));
            FixedQualityToggle.IsChecked = !Settings.AdaptiveQuality;
        }
        finally { _syncingQuality = false; }
        return ApplyLiveQuality();
    }

    /// <summary>
    /// Qualidade trocada com a transmissão no ar (resolução, FPS ou qualidade fixa): vale na hora para quem está
    /// assistindo e fica salva para a próxima vez.
    /// </summary>
    private bool ApplyLiveQuality(string? requestedBy = null)
    {
        if (!IsInitialized || _session is not { HasVideo: true } session) return false;
        Resolution? resolution;
        int fps;
        if (session.Mode == StreamMode.Camera)
        {
            if (CameraQualityCombo.SelectedItem is not Choice<(Resolution Resolution, int Fps)> camera) return false;
            (resolution, fps) = camera.Value;
        }
        else
        {
            resolution = QualityCombo.Value<Resolution>();
            fps = FpsCombo.SelectedItem is Choice<int> f ? f.Value : 0;
        }
        if (resolution is null || fps <= 0) return false;
        var adaptive = FixedQualityToggle.IsChecked != true;
        if (!session.ChangeQuality(resolution, fps, adaptive)) return false;

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
        PublishRemoteState();
        if (requestedBy is not null)
            _shell.Toast(adaptive
                ? $"{requestedBy} trocou a qualidade para {resolution.Label} a {fps} FPS. Vale para todo mundo que está assistindo."
                : $"{requestedBy} deixou a qualidade fixa em {resolution.Label} a {fps} FPS. Vale para todo mundo que está assistindo.", ToastKind.Info);
        else
            _shell.Toast(adaptive
                ? $"Qualidade agora: {resolution.Label} a {fps} FPS. Quem está assistindo já recebe assim."
                : $"Qualidade fixa em {resolution.Label} a {fps} FPS. Quem está assistindo já recebe assim.", ToastKind.Success);
        return true;
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
        UpdateRadminPanel();
    }

    /// <summary>Sem o Radmin: instalar pelo app (com o andamento aqui) ou, já instalado, abrir para entrar numa rede.</summary>
    private void UpdateRadminPanel()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(UpdateRadminPanel);
            return;
        }
        var setup = AppServices.RadminSetup;
        var installed = _shell.RadminInstalled;
        RadminMissingText.Text = setup.Busy || (setup.Status is not null && !installed) ? setup.Status
            : installed ? "O Radmin VPN está instalado, mas sem rede ativa. Abra o Radmin, entre na mesma rede que os outros e clique em Tentar de novo."
            : "O Radmin VPN não está instalado. O ScreenFlow baixa do site oficial e instala para você (o Windows vai pedir permissão).";
        RadminProgress.Visibility = setup.Progress is null ? Visibility.Collapsed : Visibility.Visible;
        RadminProgress.Value = setup.Progress ?? 0;
        RadminActionButton.Content = installed && !setup.Busy ? "Abrir o Radmin VPN" : "Instalar o Radmin VPN";
        RadminActionButton.IsEnabled = !setup.Busy;
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
    private async void RadminAction_Click(object sender, RoutedEventArgs e)
    {
        if (_shell.RadminInstalled)
        {
            if (!RadminSetup.OpenApp()) _shell.Toast("Abra o Radmin VPN pelo menu Iniciar e entre na mesma rede que os outros.", ToastKind.Info);
            return;
        }
        if (await AppServices.RadminSetup.InstallAsync(_shell)) RefreshNetwork();
    }

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
            session.RemoteRequested += request => Dispatcher.BeginInvoke(() => OnRemoteRequested(request));
            await Task.Run(session.Start);
            _liveAddress = adapter.Address;
            _liveWindowExe = target.Kind == CaptureKind.Window ? WindowDiscovery.ProcessExe(target.Handle) : null;
            _session = session;
            PublishRemoteState();
            _ = PublishAudioAppsAsync();
            _remoteAppsTimer.Start();
            _ = StartMediaAsync();

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
        StopMedia();
        _remoteAppsTimer.Stop();
        _publishedMedia = null;
        _coverSource = null;
        _cover = null;
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

    // ───────────────────────── O que está tocando (controles de mídia do Windows) ─────────────────────────

    private async Task StartMediaAsync()
    {
        if (AppServices.DemoMode) return;
        var media = await MediaControls.OpenAsync();
        if (media is null) return;
        if (_session is null || _media is not null)
        {
            media.Dispose();
            return;
        }
        _media = media;
        _mediaTimer.Start();
        UpdateMedia();
    }

    private void StopMedia()
    {
        _mediaTimer.Stop();
        var media = _media;
        _media = null;
        media?.Dispose();
        _mediaId = null;
        _mediaPinned = false;
        _mediaSeeking = false;
        _mediaArtKey = null;
        MediaCard.Visibility = Visibility.Collapsed;
    }

    /// <summary>O app que está sendo transmitido: o dono da janela ou o app da festa (para mostrar a mídia dele).</summary>
    private string? SharedAppExe() => _session?.Mode == StreamMode.AudioOnly ? Settings.PartyApp?.Exe : _liveWindowExe;

    private void UpdateMedia()
    {
        if (_media is not { } media || _session is null)
        {
            MediaCard.Visibility = Visibility.Collapsed;
            return;
        }
        var sessions = media.Sessions;
        if (media.Pick(SharedAppExe(), _mediaPinned ? _mediaId : null) is not { } info)
        {
            MediaCard.Visibility = Visibility.Collapsed;
            PublishMedia(null);
            return;
        }
        _mediaId = info.Id;
        FillMediaSessions(sessions, info.Id);
        ShowMedia(info, DateTimeOffset.UtcNow);
        PublishMedia(info);
    }

    private void ShowMedia(MediaSessionInfo info, DateTimeOffset now)
    {
        MediaCard.Visibility = Visibility.Visible;
        MediaApp.Text = $"{info.AppName} · {(info.Playing ? "tocando agora" : "pausado")}";
        MediaTitle.Text = info.Title.Length > 0 ? info.Title : info.AppName;
        MediaArtist.Text = info.Artist;
        MediaArtist.Visibility = info.Artist.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        MediaPlayGlyph.Text = info.Playing ? "\uE769" : "\uE768";
        MediaPlayPause.ToolTip = info.Playing ? "Pausar" : "Tocar";
        MediaPrevious.IsEnabled = info.CanPrevious;
        MediaNext.IsEnabled = info.CanNext;
        MediaPlayPause.IsEnabled = info.CanPlayPause;

        var duration = info.Timeline.Duration;
        MediaSeekRow.Visibility = duration > TimeSpan.Zero ? Visibility.Visible : Visibility.Collapsed;
        MediaSeek.IsEnabled = info.CanSeek;
        MediaDuration.Text = MediaApps.FormatTime(duration);
        if (!_mediaSeeking)
        {
            var position = info.Timeline.PositionAt(now);
            MediaSeek.Maximum = Math.Max(1, duration.TotalSeconds);
            MediaSeek.Value = Math.Min(MediaSeek.Maximum, position.TotalSeconds);
            MediaPosition.Text = MediaApps.FormatTime(position);
        }

        var artKey = info.Thumbnail is null ? null : $"{info.Id}\n{info.Title}\n{info.Artist}\n{info.Thumbnail.Length}";
        if (artKey == _mediaArtKey) return;
        _mediaArtKey = artKey;
        var image = ToImage(info.Thumbnail);
        MediaArt.Background = image is null ? null : new ImageBrush(image) { Stretch = Stretch.UniformToFill };
        MediaArt.Visibility = image is null ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>Com mais de um app tocando, uma lista para escolher qual controlar.</summary>
    private void FillMediaSessions(IReadOnlyList<MediaSessionInfo> sessions, string selected)
    {
        MediaSessionCombo.Visibility = sessions.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        var choices = sessions.Select((s, i) => new Choice<string>(s.Id, sessions.Take(i).Any(o => o.AppName == s.AppName) ? $"{s.AppName} ({i + 1})" : s.AppName)).ToList();
        _fillingMediaSessions = true;
        if (!choices.SequenceEqual(MediaSessionCombo.Items.OfType<Choice<string>>())) MediaSessionCombo.Fill(choices);
        MediaSessionCombo.SelectValue(selected);
        _fillingMediaSessions = false;
    }

    private void MediaSession_Changed()
    {
        if (_fillingMediaSessions || MediaSessionCombo.Value<string>() is not { } id) return;
        _mediaId = id;
        _mediaPinned = true;
        _mediaArtKey = null;
        UpdateMedia();
    }

    private async void MediaPrevious_Click(object sender, RoutedEventArgs e) => await MediaCommandAsync(MediaCommand.Previous);
    private async void MediaPlayPause_Click(object sender, RoutedEventArgs e) => await MediaCommandAsync(MediaCommand.PlayPause);
    private async void MediaNext_Click(object sender, RoutedEventArgs e) => await MediaCommandAsync(MediaCommand.Next);

    private async Task MediaCommandAsync(MediaCommand command)
    {
        if (_media is not { } media || _mediaId is not { } id) return;
        if (!await media.SendAsync(id, command)) _shell.Toast("O app não aceitou o comando. Alguns apps não deixam pular ou voltar.", ToastKind.Warning);
    }

    private void MediaSeek_PreviewMouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (MediaSeek.IsEnabled) _mediaSeeking = true;
    }

    private async void MediaSeek_PreviewMouseLeftButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (!_mediaSeeking) return;
        _mediaSeeking = false;
        if (_media is not { } media || _mediaId is not { } id) return;
        if (!await media.SeekAsync(id, TimeSpan.FromSeconds(MediaSeek.Value))) _shell.Toast("O app não deixou mudar o ponto da música ou do vídeo.", ToastKind.Warning);
    }

    private void MediaSeek_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_mediaSeeking) MediaPosition.Text = MediaApps.FormatTime(TimeSpan.FromSeconds(e.NewValue));
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
        var joined = false;
        foreach (var info in current)
        {
            var row = _viewerRows.FirstOrDefault(r => r.Id == info.Id);
            if (row is null)
            {
                _viewerRows.Add(row = new ViewerRow(info.Id, info.Name, info.Address));
                joined = true;
            }
            row.Update(info, audioOnly);
        }
        // Quem entrou já recebeu a lista de apps do som guardada; uma lida nova vai logo em seguida.
        if (joined) _ = PublishAudioAppsAsync();
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
        if (_session is { } session)
        {
            session.ConfigureAudio(AudioOptions(session.Mode));
            // Quem assiste vê na hora o que mudou (e se ainda pode escolher os apps).
            PublishRemoteState();
            RepublishAudioApps(session);
            _publishedMedia = null;
            UpdateMedia();
        }
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

    // ───────────────────────── Controles de quem assiste (1.2.0) ─────────────────────────

    private void ViewerControls_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsInitialized) return;
        var on = ViewerControlsToggle.IsChecked == true;
        if (on == Settings.ViewerControls) return;
        Settings.ViewerControls = on;
        AppServices.Settings.Save();
        if (_session is not { } session) return;
        PublishRemoteState();
        RepublishAudioApps(session);
        _publishedMedia = null;
        if (_media is not null) UpdateMedia();
        else PublishMedia(null);
        if (on) _ = PublishAudioAppsAsync();
        _shell.Toast(on
            ? "Quem assiste já pode trocar a qualidade, pausar e pular a música e tirar apps do som."
            : "Agora só você mexe na qualidade, na música e no som da transmissão.", ToastKind.Success);
    }

    /// <summary>Manda para quem assiste o que pode ser feito pelo player e a qualidade de agora.</summary>
    private void PublishRemoteState()
    {
        if (_session is { } session) session.PublishRemoteState(RemoteStateFor(session));
    }

    /// <summary>As qualidades oferecidas são as mesmas das listas daqui; quem assiste escolhe uma delas.</summary>
    private RemoteStateMessage RemoteStateFor(HostSession session)
    {
        if (!Settings.ViewerControls) return RemoteStateMessage.Disabled;
        var video = session.HasVideo;
        var camera = session.Mode == StreamMode.Camera;
        var options = session.Options;
        return new RemoteStateMessage(
            CanChangeQuality: video,
            CanControlMedia: true,
            CanChooseApps: CanViewersChooseApps(session),
            Resolutions: video && !camera ? [.. Resolution.All.Select(r => r.Label)] : [],
            FrameRates: video && !camera ? [.. Quality.FrameRates] : [],
            CameraQualities: camera ? [.. OverviewPage.CameraQualities.Select(c => new RemoteQuality(c.Value.Resolution.Label, c.Value.Fps))] : [],
            Resolution: video ? options.Resolution.Label : string.Empty,
            Fps: video ? options.Fps : 0,
            Adaptive: session.AdaptiveQuality);
    }

    /// <summary>Tirar apps só faz sentido com o som do PC indo junto e separado por app (e a festa tocando todo o som).</summary>
    private bool CanViewersChooseApps(HostSession session)
        => Settings.ViewerControls && session.AudioEnabled && Settings.SeparateAppAudio && ProcessLoopbackSupported
           && !(session.Mode == StreamMode.AudioOnly && Settings.PartyApp is not null);

    /// <summary>
    /// O que está tocando vai para quem assiste só se o som dele vai junto: um app tirado do som (ou fora da festa de um
    /// app só) continua sendo só de quem transmite, e ninguém controla.
    /// </summary>
    private bool ShareableMedia(HostSession session, MediaSessionInfo info)
    {
        if (!Settings.ViewerControls || !session.AudioEnabled) return false;
        if (Settings.MutedApps.Any(app => MediaApps.Matches(info.AppId, app.Exe))) return false;
        return !(session.Mode == StreamMode.AudioOnly && Settings.PartyApp is { } party && !MediaApps.Matches(info.AppId, party.Exe));
    }

    /// <summary>Publica o que está tocando quando algo muda (a posição anda sozinha do lado de quem assiste).</summary>
    private void PublishMedia(MediaSessionInfo? info)
    {
        if (_session is not { } session) return;
        if (info is not null && !ShareableMedia(session, info)) info = null;
        var next = info is null
            ? MediaStateMessage.None
            : new MediaStateMessage(info.AppName, info.Title, info.Artist, info.Playing, info.Timeline.PositionAt(DateTimeOffset.UtcNow).TotalSeconds,
                info.Timeline.Duration.TotalSeconds, info.CanPrevious, info.CanNext, info.CanPlayPause, info.CanSeek, CoverFor(info.Thumbnail));
        var now = Environment.TickCount64;
        if (!RemoteControl.ShouldPublish(_publishedMedia, (now - _publishedMediaAt) / 1000.0, next)) return;
        _publishedMedia = next;
        _publishedMediaAt = now;
        session.PublishMedia(next);
    }

    /// <summary>A capa vai num JPEG pequeno, feito uma vez por capa.</summary>
    private string? CoverFor(byte[]? thumbnail)
    {
        if (thumbnail is null) return null;
        if (ReferenceEquals(thumbnail, _coverSource)) return _cover;
        _coverSource = thumbnail;
        _cover = SmallCover(thumbnail);
        return _cover;
    }

    /// <summary>A capa em JPEG de até 160 px de largura, em base64. Null se a imagem não abrir.</summary>
    internal static string? SmallCover(byte[] image)
    {
        try
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.StreamSource = new System.IO.MemoryStream(image);
            bitmap.DecodePixelWidth = 160;
            bitmap.EndInit();
            bitmap.Freeze();
            var encoder = new JpegBitmapEncoder { QualityLevel = 82 };
            encoder.Frames.Add(BitmapFrame.Create(new FormatConvertedBitmap(bitmap, PixelFormats.Bgr24, null, 0)));
            using var output = new System.IO.MemoryStream();
            encoder.Save(output);
            return output.Length <= RemoteControl.MaxCoverBytes ? Convert.ToBase64String(output.GetBuffer(), 0, (int)output.Length) : null;
        }
        catch (Exception exception) when (exception is NotSupportedException or InvalidOperationException or ArgumentException or System.IO.IOException)
        {
            Log.Debug($"Capa da música não abriu: {exception.Message}");
            return null;
        }
    }

    /// <summary>Os apps com som (e os que já estão fora) para a lista de quem assiste. Leve: sem ícones.</summary>
    private async Task PublishAudioAppsAsync()
    {
        if (_session is not { } session) return;
        if (!CanViewersChooseApps(session))
        {
            if (session.PublishedAudioApps.Apps.Length > 0) session.PublishAudioApps(new AudioAppsMessage([]));
            return;
        }
        var version = ++_remoteAppsVersion;
        IReadOnlyList<RunningApp> running;
        try { running = await Task.Run(AppAudioCatalog.ListWithSound); }
        catch (Exception exception)
        {
            Log.Debug($"Não deu para listar os apps com som para quem assiste: {exception.Message}");
            return;
        }
        if (version != _remoteAppsVersion || !ReferenceEquals(_session, session)) return;
        var next = AudioAppsFor(running);
        if (!next.Apps.SequenceEqual(session.PublishedAudioApps.Apps)) session.PublishAudioApps(next);
    }

    /// <summary>Mesma lista de antes com o que está fora agora (depois de uma troca, sem esperar a próxima leitura).</summary>
    private void RepublishAudioApps(HostSession session)
    {
        if (!CanViewersChooseApps(session))
        {
            if (session.PublishedAudioApps.Apps.Length > 0) session.PublishAudioApps(new AudioAppsMessage([]));
            return;
        }
        session.PublishAudioApps(AudioAppsFor(session.PublishedAudioApps.Apps.Select(a => new RunningApp(a.Exe, a.Name, null, a.Playing, null)).ToList()));
    }

    private AudioAppsMessage AudioAppsFor(IReadOnlyList<RunningApp> running)
    {
        var muted = Settings.MutedApps;
        var mutedExes = muted.Select(m => m.Exe).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var runningExes = running.Select(r => r.Exe).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var apps = running.Select(a => new RemoteAudioApp(a.Exe, a.Name, mutedExes.Contains(a.Exe), a.PlayingSound))
            .Concat(muted.Where(m => !runningExes.Contains(m.Exe)).Select(m => new RemoteAudioApp(m.Exe, m.Name, true, false)))
            .OrderByDescending(a => a.Playing).ThenByDescending(a => a.Excluded).ThenBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase)
            .Take(RemoteControl.MaxApps)
            .ToArray();
        return new AudioAppsMessage(apps);
    }

    /// <summary>Um pedido de quem assiste, já conferido pela sessão: aplica como se fosse daqui e avisa quem pediu.</summary>
    private void OnRemoteRequested(RemoteRequest request)
    {
        if (_session is not { } session || !Settings.ViewerControls) return;
        switch (request.Message)
        {
            case QualityRequestMessage quality:
                ApplyRemoteQuality(session, request.ViewerName, quality);
                break;
            case MediaCommandMessage command:
                _ = RemoteMediaAsync(request.ViewerName, command);
                break;
            case AudioAppsRequestMessage apps:
                ApplyRemoteApps(session, request.ViewerName, apps);
                break;
        }
    }

    private void ApplyRemoteQuality(HostSession session, string viewer, QualityRequestMessage request)
    {
        var resolution = Resolution.All.FirstOrDefault(r => string.Equals(r.Label, request.Resolution, StringComparison.OrdinalIgnoreCase));
        if (!session.HasVideo || resolution is null) return;
        _syncingQuality = true;
        try
        {
            if (session.Mode == StreamMode.Camera) CameraQualityCombo.SelectValue((resolution, request.Fps));
            else
            {
                QualityCombo.SelectValue(resolution);
                FpsCombo.SelectValue(request.Fps, f => f);
            }
            FixedQualityToggle.IsChecked = !request.Adaptive;
        }
        finally { _syncingQuality = false; }
        UpdateProfileHint();
        // Nada mudou (já era essa): quem pediu recebe o estado de novo e o player volta a mostrar a de agora.
        if (!ApplyLiveQuality(viewer)) PublishRemoteState();
    }

    private async Task RemoteMediaAsync(string viewer, MediaCommandMessage command)
    {
        if (_media is not { } media || _mediaId is not { } id) return;
        var what = _publishedMedia is { Title.Length: > 0 } playing ? $"“{(playing.Title.Length > 60 ? playing.Title[..60] + "…" : playing.Title)}”" : "o que está tocando";
        var (ok, done) = command.Command switch
        {
            MediaCommandMessage.PlayPause => (await media.SendAsync(id, MediaCommand.PlayPause), _publishedMedia?.Playing == true ? $"{viewer} pausou {what}." : $"{viewer} deu play em {what}."),
            MediaCommandMessage.Previous => (await media.SendAsync(id, MediaCommand.Previous), $"{viewer} voltou para a anterior."),
            MediaCommandMessage.Next => (await media.SendAsync(id, MediaCommand.Next), $"{viewer} pulou para a próxima."),
            MediaCommandMessage.Seek => (await media.SeekAsync(id, TimeSpan.FromSeconds(command.PositionSeconds)), $"{viewer} mudou o ponto de {what}."),
            _ => (false, string.Empty),
        };
        if (ok) _shell.Toast(done);
        else Log.Info($"O app não aceitou o comando de {viewer} ({command.Command}).");
        UpdateMedia();
    }

    private void ApplyRemoteApps(HostSession session, string viewer, AudioAppsRequestMessage request)
    {
        if (!CanViewersChooseApps(session)) return;
        var published = session.PublishedAudioApps;
        var before = Settings.MutedApps;
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var app in before) names.TryAdd(app.Exe, app.Name);
        foreach (var app in published.Apps) names.TryAdd(app.Exe, app.Name);
        var muted = MutedApp.Normalize(RemoteControl.Exclusion(published, before.Select(a => a.Exe), request)
            .Select(exe => new MutedApp(exe, names.GetValueOrDefault(exe) ?? MutedApp.DefaultName(exe))));
        var added = muted.Where(m => before.All(b => b.Exe != m.Exe)).Select(m => m.Name).ToList();
        var removed = before.Where(b => muted.All(m => m.Exe != b.Exe)).Select(b => b.Name).ToList();
        if (added.Count == 0 && removed.Count == 0)
        {
            RepublishAudioApps(session);
            return;
        }
        Settings.MutedApps = muted;
        AppServices.Settings.Save();
        SyncAppRows();
        OnAudioSettingsChanged();
        _shell.Toast((added.Count, removed.Count) switch
        {
            (> 0, 0) => $"{viewer} tirou o som de {MutedApp.JoinNames(added)} da transmissão.",
            (0, _) => $"{viewer} devolveu o som de {MutedApp.JoinNames(removed)} à transmissão.",
            _ => $"{viewer} tirou o som de {MutedApp.JoinNames(added)} e devolveu o de {MutedApp.JoinNames(removed)}.",
        });
    }

    /// <summary>
    /// Quem assiste mudou os apps: a janelinha daqui (se estiver aberta) mostra o que mudou, para não desfazer a troca
    /// ao fechar; fechada, ela é montada de novo na próxima vez.
    /// </summary>
    private void SyncAppRows()
    {
        if (!AppsPopup.IsOpen)
        {
            _apps.Clear();
            return;
        }
        var muted = Settings.MutedApps;
        foreach (var row in _apps) row.Muted = muted.Any(m => m.Exe == row.Exe);
        foreach (var app in muted.Where(m => _apps.All(r => r.Exe != m.Exe)).ToList())
            _apps.Add(new AppChoice(app.Exe, app.Name, "Tirado por quem assiste", null, true));
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
        var now = DateTimeOffset.UtcNow;
        ShowMedia(new MediaSessionInfo("Spotify.exe", "Spotify.exe", "Spotify", "Noite de Sexta", "Banda da Rede",
            new PlaybackTimeline(TimeSpan.FromSeconds(83), TimeSpan.FromSeconds(214), now, true), true, true, true, true, null), now);
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
