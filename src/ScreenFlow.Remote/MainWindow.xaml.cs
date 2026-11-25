using System.IO;
using System.Net;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using ScreenFlow.Core;
using ScreenFlow.Infrastructure;

namespace ScreenFlow.Remote;

public partial class MainWindow : Window
{
    private IReadOnlyList<DisplayMonitor> _monitors = [];
    private LanScreenHost? _host;
    private LanScreenClient? _client;
    private int _frameDispatchPending;

    public MainWindow()
    {
        InitializeComponent();
        RefreshMonitors();
    }

    private void RefreshMonitors_Click(object sender, RoutedEventArgs e) => RefreshMonitors();

    private void RefreshMonitors()
    {
        try
        {
            _monitors = MonitorDiscovery.GetMonitors();
            MonitorCombo.ItemsSource = _monitors;
            if (_monitors.Count > 0) MonitorCombo.SelectedIndex = 0;
            else MonitorDetailsUnavailable();
        }
        catch (Exception exception)
        {
            SetStatus($"Falha ao detectar monitores: {exception.Message}");
        }
    }

    private void MonitorDetailsUnavailable()
    {
        SetStatus("Nenhum monitor foi encontrado pelo Windows.");
    }

    private void MonitorCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (MonitorCombo.SelectedItem is DisplayMonitor monitor)
            ViewerResolutionText.Text = $"{monitor.Width} × {monitor.Height}";
    }

    private async void StartHostButton_Click(object sender, RoutedEventArgs e)
    {
        if (MonitorCombo.SelectedItem is not DisplayMonitor monitor)
        {
            MessageBox.Show(this, "Selecione um monitor antes de iniciar.", "ScreenFlow Remote", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        try
        {
            var maxWidth = GetSelectedNumber(ResolutionCombo, 1280);
            var fps = GetSelectedNumber(FpsCombo, 20);
            _host = new LanScreenHost(monitor, maxWidth, fps);
            _host.ConnectionApproval = async remote => await Dispatcher.InvokeAsync(() =>
                MessageBox.Show(this, $"O computador {remote} deseja visualizar sua tela.\n\nPermitir esta conexão?", "Solicitação de conexão", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes);
            _host.StatusChanged += SetStatus;
            _host.Start();

            SessionCodeText.Text = _host.SessionCode;
            var addresses = NetworkAddressDiscovery.GetLocalIPv4Addresses();
            AddressesText.Text = addresses.Count == 0
                ? $"Nenhum IPv4 local foi encontrado. Porta: {_host.Port}"
                : string.Join(Environment.NewLine, addresses.Select(address => $"{address} : {_host.Port}"));
            StartHostButton.IsEnabled = false;
            StopHostButton.IsEnabled = true;
            SetStatus($"Sessão criada. Aguardando conexão na porta {_host.Port}.");
        }
        catch (Exception exception)
        {
            if (_host is not null) await _host.DisposeAsync();
            _host = null;
            MessageBox.Show(this, exception.Message, "Não foi possível iniciar a sessão", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void StopHostButton_Click(object sender, RoutedEventArgs e) => await StopHostAsync();

    private async Task StopHostAsync()
    {
        if (_host is null) return;
        await _host.DisposeAsync();
        _host = null;
        SessionCodeText.Text = "Nenhuma sessão ativa";
        AddressesText.Text = "Aguardando criação da sessão";
        StartHostButton.IsEnabled = true;
        StopHostButton.IsEnabled = false;
    }

    private void CopyCodeButton_Click(object sender, RoutedEventArgs e)
    {
        if (_host is null)
        {
            SetStatus("Crie uma sessão antes de copiar o código.");
            return;
        }
        Clipboard.SetText(_host.SessionCode);
        SetStatus("Código da sessão copiado.");
    }

    private async void ConnectButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _client = new LanScreenClient();
            _client.FrameReceived += OnFrameReceived;
            _client.StatusChanged += SetStatus;
            ConnectButton.IsEnabled = false;
            SetStatus("Conectando e autenticando...");
            await _client.ConnectAsync(HostIpBox.Text, SessionCodeBox.Text);
            DisconnectButton.IsEnabled = true;
            ViewerPlaceholder.Visibility = Visibility.Collapsed;
        }
        catch (Exception exception)
        {
            if (_client is not null) await _client.DisposeAsync();
            _client = null;
            ConnectButton.IsEnabled = true;
            DisconnectButton.IsEnabled = false;
            MessageBox.Show(this, exception.Message, "Falha na conexão", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OnFrameReceived(byte[] jpeg)
    {
        // Drop stale UI work instead of allowing a slow renderer to build an unbounded frame queue.
        if (Interlocked.Exchange(ref _frameDispatchPending, 1) != 0) return;
        _ = Dispatcher.BeginInvoke(new Action(() =>
        {
            try
            {
                using var stream = new MemoryStream(jpeg);
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.StreamSource = stream;
                bitmap.EndInit();
                bitmap.Freeze();
                RemoteImage.Source = bitmap;
                ViewerPlaceholder.Visibility = Visibility.Collapsed;
                ViewerResolutionText.Text = $"{bitmap.PixelWidth} × {bitmap.PixelHeight}";
            }
            catch (Exception exception)
            {
                SetStatus($"Falha ao renderizar frame: {exception.Message}");
            }
            finally
            {
                Interlocked.Exchange(ref _frameDispatchPending, 0);
            }
        }));
    }

    private async void DisconnectButton_Click(object sender, RoutedEventArgs e) => await DisconnectClientAsync();

    private async Task DisconnectClientAsync()
    {
        if (_client is not null)
        {
            await _client.DisposeAsync();
            _client = null;
        }
        RemoteImage.Source = null;
        ViewerPlaceholder.Visibility = Visibility.Visible;
        ViewerResolutionText.Text = string.Empty;
        ConnectButton.IsEnabled = true;
        DisconnectButton.IsEnabled = false;
    }

    private static int GetSelectedNumber(ComboBox comboBox, int fallback)
    {
        if (comboBox.SelectedItem is ComboBoxItem item && int.TryParse(item.Content?.ToString(), out var value))
            return value;
        return fallback;
    }

    private void SetStatus(string message)
    {
        if (Dispatcher.HasShutdownStarted) return;
        _ = Dispatcher.BeginInvoke(new Action(() => StatusText.Text = message));
    }

    private async void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_host is not null) await StopHostAsync();
        if (_client is not null) await DisconnectClientAsync();
    }
}