using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using ScreenFlow.App.Services;
using ScreenFlow.App.Windows;
using ScreenFlow.Core.Network;
using ScreenFlow.Infrastructure.Hardware;
using ScreenFlow.Infrastructure.Network;
using ScreenFlow.Infrastructure.Session;

namespace ScreenFlow.App.Pages;

public partial class WatchPage : UserControl
{
    private readonly MainWindow _shell;
    private readonly DispatcherTimer _refresh = new() { Interval = TimeSpan.FromSeconds(1) };
    private DiscoveryListener? _discovery;
    private PlayerWindow? _player;

    public WatchPage(MainWindow shell)
    {
        _shell = shell;
        InitializeComponent();
        _refresh.Tick += (_, _) => RefreshHosts();
        Loaded += (_, _) => OnShown();
        Unloaded += async (_, _) => await StopDiscoveryAsync();
    }

    private void OnShown()
    {
        var settings = AppServices.Settings.Current;
        HostBox.ItemsSource = settings.RecentHosts.ToList();
        if (string.IsNullOrEmpty(HostBox.Text) && settings.RecentHosts.Count > 0) HostBox.Text = settings.RecentHosts[0];
        if (_discovery is null)
        {
            _discovery = new DiscoveryListener(settings.DiscoveryPort);
            _discovery.Start();
        }
        _refresh.Start();
        RefreshHosts();
        HostBox.Focus();
    }

    private async Task StopDiscoveryAsync()
    {
        _refresh.Stop();
        if (_discovery is not null)
        {
            await _discovery.DisposeAsync();
            _discovery = null;
        }
    }

    private void RefreshHosts()
    {
        if (_discovery is null) return;
        var selected = (HostList.SelectedItem as HostRow)?.Address;
        var hosts = _discovery.Hosts.Select(h => new HostRow(h)).ToList();
        HostList.ItemsSource = hosts;
        HostList.SelectedItem = hosts.FirstOrDefault(h => h.Address == selected);
        NoHosts.Visibility = hosts.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ScanText.Text = hosts.Count == 0 ? "Procurando…" : $"{hosts.Count} encontrada(s)";
    }

    private void HostList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (HostList.SelectedItem is HostRow row) HostBox.Text = row.Address;
    }

    private void HostList_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (HostList.SelectedItem is HostRow row)
        {
            HostBox.Text = row.Address;
            Connect();
        }
    }

    private void Connect_Click(object sender, RoutedEventArgs e) => Connect();

    private async void Connect()
    {
        var host = HostBox.Text.Trim();
        if (!RadminAddress.TryParseHost(host, out var address))
        {
            ShowState("Digite um IP válido, por exemplo 26.12.34.56.", "Danger");
            return;
        }
        if (_player is not null)
        {
            _player.Activate();
            return;
        }

        var settings = AppServices.Settings.Current;
        settings.RememberHost(address.ToString());
        AppServices.Settings.Save();
        HostBox.ItemsSource = settings.RecentHosts.ToList();

        ConnectButton.IsEnabled = false;
        ShowState($"Conectando em {address}…", "Warning");

        var hardware = await AppServices.Hardware;
        var decoders = await Task.Run(HardwareDetector.SupportedDecoders);
        if (decoders.Length == 0)
        {
            ShowState("Nenhum decoder de vídeo foi encontrado. Confira se a pasta 'ffmpeg' está junto do ScreenFlow.exe.", "Danger");
            ConnectButton.IsEnabled = true;
            return;
        }

        var hostText = address.ToString();
        var options = new ViewerOptions(hostText, settings.ControlPort, settings.DeviceName, null, (float)settings.Volume, decoders,
            Math.Max(1280, hardware.MonitorWidth), Math.Max(720, hardware.MonitorHeight), Math.Max(60, hardware.MonitorRefreshHz));
        var session = new ViewerSession(options);
        session.PinProvider = wrong => Dispatcher.InvokeAsync(() => PinDialog.Ask(Window.GetWindow(this), hostText, wrong)).Task;
        session.StateChanged += (state, message) => Dispatcher.BeginInvoke(() => OnStateChanged(state, message));

        _player = new PlayerWindow(session, hostText);
        _player.Closed += async (_, _) =>
        {
            var player = _player;
            _player = null;
            ConnectButton.IsEnabled = true;
            StatePanel.Visibility = Visibility.Collapsed;
            if (player is not null) await player.Session.DisposeAsync();
        };
        _player.Show();
        session.Start();
    }

    private void OnStateChanged(ConnectionState state, string message)
    {
        switch (state)
        {
            case ConnectionState.Connected:
                ShowState(message, "Success");
                break;
            case ConnectionState.Error:
                ShowState(message, "Danger");
                break;
            case ConnectionState.Closed:
                StatePanel.Visibility = Visibility.Collapsed;
                ConnectButton.IsEnabled = true;
                break;
            default:
                ShowState(message, "Warning");
                break;
        }
    }

    private void ShowState(string message, string brush)
    {
        StatePanel.Visibility = Visibility.Visible;
        StateText.Text = message;
        StateDot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, brush);
        CancelButton.Visibility = _player is null ? Visibility.Collapsed : Visibility.Visible;
        _shell.SetStatus(message);
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => _player?.Close();

    public async Task ShutdownAsync()
    {
        _player?.Close();
        await StopDiscoveryAsync();
    }

    private sealed class HostRow(DiscoveredHost host)
    {
        public string Name { get; } = host.Announcement.HostName;
        public string Address { get; } = host.Announcement.Address;
        public string Subtitle { get; } = (host.Announcement.PinRequired ? "PIN · " : string.Empty)
                                          + (host.Announcement.Viewers == 1 ? "1 assistindo" : $"{host.Announcement.Viewers} assistindo");
    }
}
