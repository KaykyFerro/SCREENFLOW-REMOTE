using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using ScreenFlow.App.Services;
using ScreenFlow.Core;
using ScreenFlow.Core.Diagnostics;
using ScreenFlow.Core.Media;
using ScreenFlow.Core.Settings;
using ScreenFlow.Infrastructure.Capture;
using ScreenFlow.Infrastructure.Network;
using ScreenFlow.Infrastructure.Session;

namespace ScreenFlow.App.Pages;

public partial class TransmitPage : UserControl
{
    private readonly MainWindow _shell;
    private readonly ObservableCollection<PendingApproval> _approvals = [];
    private readonly ObservableCollection<ViewerRow> _viewerRows = [];
    private HostSession? _session;
    private IReadOnlyList<DisplayMonitor> _monitors = [];

    public TransmitPage(MainWindow shell)
    {
        _shell = shell;
        InitializeComponent();
        ApprovalList.ItemsSource = _approvals;
        ViewerList.ItemsSource = _viewerRows;

        foreach (var resolution in Resolution.All) QualityCombo.Items.Add(resolution);
        foreach (var fps in Quality.FrameRates) FpsCombo.Items.Add(fps);
        QualityCombo.SelectionChanged += (_, _) => UpdateProfileHint();
        FpsCombo.SelectionChanged += (_, _) => UpdateProfileHint();
        Loaded += (_, _) => { if (_session is null) LoadFromSettings(); };
    }

    public bool IsRunning => _session is not null;

    private AppSettings Settings => AppServices.Settings.Current;

    private void LoadFromSettings()
    {
        QualityCombo.SelectedItem = Resolution.FromLabel(Settings.Resolution);
        FpsCombo.SelectedItem = Settings.Fps;
        AudioToggle.IsChecked = Settings.AudioEnabled;
        ApproveToggle.IsChecked = Settings.ApproveEachViewer;
        PinBox.Text = Settings.Pin ?? string.Empty;
        RefreshNetwork();
        RefreshSources();
        UpdateProfileHint();
    }

    private void UpdateProfileHint()
    {
        if (QualityCombo.SelectedItem is not Resolution resolution || FpsCombo.SelectedItem is not int fps) return;
        var codec = Settings.PreferredCodec ?? VideoCodec.Hevc;
        var kbps = Settings.BitrateKbps > 0 ? Settings.BitrateKbps : Quality.TargetBitrateKbps(resolution, fps, codec);
        var text = $"Cerca de {kbps / 1000.0:0} Mbps por pessoa em {Quality.CodecLabel(codec)}.";
        if (Settings.UploadMbps > 0)
        {
            var people = Math.Max(1, (int)(Settings.UploadMbps * 0.8 * 1000 / kbps));
            text += $" Com o seu upload ({Settings.UploadMbps:0} Mbps) cabem umas {people} pessoas nessa qualidade; acima disso o app baixa a qualidade sozinho.";
        }
        else text += " Com mais gente assistindo, o upload é dividido e a qualidade se ajusta sozinha.";
        ProfileHint.Text = text;
    }

    // ───────────────────────── Rede e origem ─────────────────────────

    private void RefreshNetwork()
    {
        var adapters = RadminNetwork.GetIPv4Adapters().Where(a => a.IsUp).ToList();
        var radmin = adapters.FirstOrDefault(a => a.IsRadmin && Core.Network.RadminAddress.IsRadmin(a.Address));
        AdapterCombo.ItemsSource = adapters;
        AdapterCombo.SelectedItem = radmin ?? adapters.FirstOrDefault();
        RadminMissing.Visibility = radmin is null ? Visibility.Visible : Visibility.Collapsed;
        UpdateIp();
    }

    private void UpdateIp()
    {
        if (AdapterCombo.SelectedItem is NetworkAdapterInfo adapter)
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
        _monitors = MonitorDiscovery.GetMonitors();
        var sources = _monitors.Select(CaptureTarget.FromMonitor).Concat(WindowDiscovery.GetWindows()).ToList();
        var previous = SourceCombo.SelectedItem as CaptureTarget;
        SourceCombo.ItemsSource = sources;
        SourceCombo.SelectedItem = sources.FirstOrDefault(s => s.Id == previous?.Id) ?? sources.FirstOrDefault();
    }

    private void RefreshSources_Click(object sender, RoutedEventArgs e) => RefreshSources();

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        if (IpText.Text == "—") return;
        Clipboard.SetText(IpText.Text);
        _shell.SetStatus($"IP {IpText.Text} copiado.");
    }

    // ───────────────────────── Iniciar / parar ─────────────────────────

    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        if (AdapterCombo.SelectedItem is not NetworkAdapterInfo adapter)
        {
            ShowState("Nenhuma rede ativa. Conecte o Radmin VPN e tente de novo.", "ERRO", "Danger");
            return;
        }
        if (SourceCombo.SelectedItem is not CaptureTarget target)
        {
            ShowState("Escolha um monitor ou uma janela.", "ERRO", "Danger");
            return;
        }
        var pin = PinBox.Text.Trim();
        if (pin.Length > 0 && !Core.Security.SessionCrypto.IsValidPin(pin))
        {
            ShowState("O PIN precisa ter pelo menos 4 caracteres.", "ERRO", "Danger");
            return;
        }

        // Guarda as escolhas para a próxima vez.
        Settings.Resolution = ((Resolution)QualityCombo.SelectedItem).Label;
        Settings.Fps = (int)FpsCombo.SelectedItem;
        Settings.AudioEnabled = AudioToggle.IsChecked == true;
        Settings.ApproveEachViewer = ApproveToggle.IsChecked == true;
        Settings.Pin = pin.Length == 0 ? null : pin;
        AppServices.Settings.Save();

        StartButton.IsEnabled = false;
        ShowState("Preparando captura e encoder…", "INICIANDO", "Warning");
        try
        {
            var hardware = await AppServices.Hardware;
            if (hardware.Encoders.Count == 0)
                throw new InvalidOperationException("Nenhum encoder de vídeo foi encontrado. Confira se a pasta 'ffmpeg' está junto do ScreenFlow.exe (veja Configurações › Diagnóstico).");

            var monitor = target.Kind == CaptureKind.Monitor ? _monitors.FirstOrDefault(m => m.DeviceName == target.Id) : null;
            var options = new HostOptions(
                Settings.DeviceName, adapter.Address, adapter.Broadcast,
                Settings.MediaPort, Settings.ControlPort, Settings.DiscoveryPort,
                Settings.Pin, Settings.ApproveEachViewer, Settings.MaxViewers,
                target, monitor, Resolution.FromLabel(Settings.Resolution), Settings.Fps, Settings.BitrateKbps,
                Settings.PreferredCodec, Settings.PreferredEncoder, hardware.Encoders,
                Settings.AudioEnabled, Settings.AudioBitrateKbps, Settings.AdaptiveQuality, Settings.FecGroupSize, Settings.UploadMbps);

            var session = new HostSession(options) { ApprovalHandler = RequestApprovalAsync };
            session.StatusChanged += message => Dispatcher.BeginInvoke(() => { StateText.Text = message; _shell.SetStatus(message); });
            session.ViewersChanged += () => Dispatcher.BeginInvoke(RefreshViewers);
            session.StatisticsUpdated += stats => Dispatcher.BeginInvoke(() => ShowStatistics(stats));
            session.BandwidthMeasured += (name, mbps) => Dispatcher.BeginInvoke(() => OnBandwidthMeasured(name, mbps));
            session.Start();
            _session = session;

            OptionsPanel.IsEnabled = false;
            AdapterCombo.IsEnabled = false;
            StartButton.Visibility = Visibility.Collapsed;
            StopButton.Visibility = Visibility.Visible;
            ShowState($"Transmitindo em {adapter.Address}. Quem assistir só precisa digitar esse IP.", "AO VIVO", "Success");
        }
        catch (System.Net.Sockets.SocketException exception) when (exception.SocketErrorCode == System.Net.Sockets.SocketError.AddressAlreadyInUse)
        {
            ShowState($"As portas {Settings.ControlPort}/{Settings.MediaPort} já estão em uso. Feche outra cópia do ScreenFlow ou mude as portas em Configurações.", "ERRO", "Danger");
        }
        catch (Exception exception)
        {
            Log.Error("Falha ao iniciar transmissão", exception);
            ShowState(exception.Message, "ERRO", "Danger");
        }
        finally
        {
            StartButton.IsEnabled = true;
        }
    }

    private async void Stop_Click(object sender, RoutedEventArgs e) => await StopAsync();

    public async Task StopAsync()
    {
        var session = _session;
        _session = null;
        foreach (var pending in _approvals.ToList()) pending.Completion.TrySetResult(false);
        _approvals.Clear();
        ApprovalCard.Visibility = Visibility.Collapsed;
        if (session is not null) await session.DisposeAsync();
        if (!Dispatcher.CheckAccess()) return;
        _viewerRows.Clear();
        NoViewers.Visibility = Visibility.Visible;
        OptionsPanel.IsEnabled = true;
        AdapterCombo.IsEnabled = true;
        StartButton.Visibility = Visibility.Visible;
        StopButton.Visibility = Visibility.Collapsed;
        StatViewers.Text = "0";
        StatFps.Text = StatBitrate.Text = StatUpload.Text = "—";
        StatDetails.Text = string.Empty;
        ShowState("Transmissão encerrada.", "PARADO", "TextMuted");
    }

    private void ShowState(string message, string badge, string brush)
    {
        StateText.Text = message;
        LiveText.Text = badge;
        LiveText.SetResourceReference(TextBlock.ForegroundProperty, brush);
        _shell.SetStatus(message);
    }

    // ───────────────────────── Viewers e aprovação ─────────────────────────

    private Task<bool> RequestApprovalAsync(ApprovalRequest request, CancellationToken token)
    {
        var pending = new PendingApproval(request.Id, request.Name, request.Address);
        token.Register(() => pending.Completion.TrySetResult(false));
        Dispatcher.BeginInvoke(() =>
        {
            _approvals.Add(pending);
            ApprovalCard.Visibility = Visibility.Visible;
            _shell.SetStatus($"{request.Name} ({request.Address}) quer assistir.");
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

    private void RefreshViewers()
    {
        if (_session is null) return;
        var viewers = _session.Viewers;
        _viewerRows.Clear();
        foreach (var viewer in viewers.Where(v => v.State == ViewerState.Streaming))
            _viewerRows.Add(new ViewerRow(viewer));
        NoViewers.Visibility = _viewerRows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        StatViewers.Text = _viewerRows.Count.ToString();
    }

    private void ShowStatistics(HostStatistics stats)
    {
        if (_session is null) return;
        StatViewers.Text = stats.Viewers.ToString();
        StatFps.Text = stats.Viewers == 0 ? "—" : $"{stats.EncodeFps:0}";
        StatBitrate.Text = stats.Viewers == 0 ? "—" : $"{stats.BitrateMbps:0.#} Mbps";
        StatUpload.Text = stats.Viewers == 0 ? "—" : $"{stats.UploadMbps:0.#} Mbps";
        StatDetails.Text = stats.Viewers == 0
            ? "Captura e encoder ficam pausados até alguém entrar."
            : $"{stats.Encoder} · {stats.Capture} · alvo {stats.Target} · {stats.Reason}";
    }

    private void Kick_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is Guid id) _session?.Kick(id);
    }

    private async void Bandwidth_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not Guid id || _session is null) return;
        var button = (Button)sender;
        button.IsEnabled = false;
        button.Content = "Testando…";
        _shell.SetStatus("Testando a banda até o viewer (3 s)…");
        try { await _session.RunBandwidthTestAsync(id); }
        finally
        {
            button.IsEnabled = true;
            button.Content = "Testar banda";
        }
    }

    private void OnBandwidthMeasured(string name, double mbps)
    {
        _shell.ShowBanner($"Banda medida até {name}: {mbps:0} Mbps. Esse valor foi salvo como o seu upload para dividir a qualidade entre quem assiste.");
        // Guarda a maior medição recente como estimativa do upload do host.
        Settings.UploadMbps = Math.Max(mbps, Settings.UploadMbps * 0.5);
        AppServices.Settings.Save();
        UpdateProfileHint();
    }

    public sealed class PendingApproval(Guid id, string name, string address)
    {
        public Guid Id { get; } = id;
        public string Name { get; } = name;
        public string Address { get; } = address;
        public TaskCompletionSource<bool> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    public sealed class ViewerRow(ViewerInfo info)
    {
        public Guid Id { get; } = info.Id;
        public string Title { get; } = $"{info.Name} · {info.Address}";
        public string Details { get; } = info.RoundTripMs <= 0
            ? "Conectando…"
            : $"Recebendo {info.ReceivedMbps:0.#} Mbps · ping {info.RoundTripMs:0} ms · latência {info.LatencyMs:0} ms · perda {info.LossRate:P1}";
    }
}
