using System.Windows;
using System.Windows.Controls;
using ScreenFlow.Core;
using ScreenFlow.Infrastructure;

namespace ScreenFlow.Remote;

public partial class MainWindow : Window
{
    private IReadOnlyList<DisplayMonitor> _monitors = [];
    public MainWindow() { InitializeComponent(); RefreshMonitors(); }
    private void RefreshMonitors_Click(object sender, RoutedEventArgs e) => RefreshMonitors();
    private void RefreshMonitors()
    {
        _monitors = MonitorDiscovery.GetMonitors();
        MonitorCombo.ItemsSource = _monitors;
        MonitorCombo.DisplayMemberPath = nameof(DisplayMonitor.DisplayLabel);
        if (_monitors.Count > 0) MonitorCombo.SelectedIndex = 0;
        else MonitorDetails.Text = "Nenhum monitor foi encontrado pelo Windows.";
    }
    private void MonitorCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (MonitorCombo.SelectedItem is DisplayMonitor monitor)
            MonitorDetails.Text = $"Identificação: {monitor.DeviceName}\nResolução: {monitor.Width} × {monitor.Height}\nTaxa reportada: {(monitor.RefreshRateHz > 0 ? $"{monitor.RefreshRateHz} Hz" : "indisponível")}\nPosição: {monitor.X}, {monitor.Y}\nPrincipal: {(monitor.IsPrimary ? "Sim" : "Não")}";
    }
}