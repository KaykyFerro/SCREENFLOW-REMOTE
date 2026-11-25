using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using ScreenFlow.App.Pages;
using ScreenFlow.App.Services;
using ScreenFlow.Core;
using ScreenFlow.Core.Diagnostics;
using ScreenFlow.Core.Settings;
using ScreenFlow.Core.Updates;
using ScreenFlow.Infrastructure.Network;
using ScreenFlow.Infrastructure.Updates;

namespace ScreenFlow.App;

public partial class MainWindow : Window
{
    private readonly Stack<UserControl> _history = new();
    private readonly DispatcherTimer _networkTimer = new() { Interval = TimeSpan.FromSeconds(3) };
    private TransmitPage? _transmit;
    private WatchPage? _watch;

    public MainWindow()
    {
        InitializeComponent();
        VersionText.Text = $"v{AppInfo.DisplayVersion}";
        Navigate(new HomePage(this), "Início", root: true);
        _networkTimer.Tick += (_, _) => RefreshRadminStatus();
        _networkTimer.Start();
        RefreshRadminStatus();
        Loaded += async (_, _) => await FirstRunAsync();
    }

    public TransmitPage TransmitPage => _transmit ??= new TransmitPage(this);
    public WatchPage WatchPage => _watch ??= new WatchPage(this);

    public void Navigate(UserControl page, string title, bool root = false)
    {
        if (root) _history.Clear();
        else if (Host.Content is UserControl current && current != page) _history.Push(current);
        Host.Content = page;
        PageTitle.Text = root ? string.Empty : "· " + title;
        BackButton.Visibility = _history.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    public void GoHome()
    {
        _history.Clear();
        Navigate(new HomePage(this), "Início", root: true);
    }

    private void Back_Click(object sender, RoutedEventArgs e)
    {
        if (_history.Count == 0) return;
        Host.Content = _history.Pop();
        BackButton.Visibility = _history.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        PageTitle.Text = Host.Content switch
        {
            Pages.TransmitPage => "· Transmitir",
            Pages.WatchPage => "· Assistir",
            SettingsPage => "· Configurações",
            _ => string.Empty,
        };
    }

    private void Settings_Click(object sender, RoutedEventArgs e) => Navigate(new SettingsPage(this), "Configurações");

    private void Theme_Click(object sender, RoutedEventArgs e)
    {
        var next = ThemeManager.Current == AppTheme.Dark ? AppTheme.Light : AppTheme.Dark;
        ThemeManager.Apply(next);
        AppServices.Settings.Current.Theme = next;
        AppServices.Settings.Save();
    }

    public void SetStatus(string message)
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(() => SetStatus(message)); return; }
        StatusText.Text = message;
    }

    private void RefreshRadminStatus()
    {
        var radmin = RadminNetwork.FindRadmin();
        RadminDot.Fill = (System.Windows.Media.Brush)FindResource(radmin is null ? "Warning" : "Success");
        RadminStatus.Text = radmin is null
            ? (RadminNetwork.IsRadminInstalled() ? "Radmin VPN sem rede ativa" : "Radmin VPN não encontrado")
            : $"Radmin VPN · {radmin.Address}";
    }

    // ───────────────────────── Avisos ─────────────────────────

    public Border ShowBanner(string text, string? actionText = null, Action? action = null, string? secondaryText = null, Action? secondary = null, bool warning = false)
    {
        var panel = new DockPanel();
        var close = new Button { Style = (Style)FindResource("IconButton"), Content = "", ToolTip = "Fechar" };
        DockPanel.SetDock(close, Dock.Right);
        panel.Children.Add(close);
        Button? primary = null;
        if (actionText is not null)
        {
            primary = new Button { Content = actionText, Style = (Style)FindResource("PrimaryButton"), Margin = new Thickness(8, 0, 0, 0) };
            DockPanel.SetDock(primary, Dock.Right);
        }
        if (secondaryText is not null)
        {
            var second = new Button { Content = secondaryText, Margin = new Thickness(8, 0, 0, 0) };
            DockPanel.SetDock(second, Dock.Right);
            second.Click += (_, _) => secondary?.Invoke();
            panel.Children.Add(second);
        }
        if (primary is not null) panel.Children.Add(primary);
        var icon = new TextBlock { Style = (Style)FindResource("Icon"), Text = warning ? "" : "", Margin = new Thickness(0, 0, 12, 0) };
        icon.SetResourceReference(TextBlock.ForegroundProperty, warning ? "Warning" : "Accent");
        panel.Children.Add(icon);
        panel.Children.Add(new TextBlock { Text = text, Style = (Style)FindResource("Body"), VerticalAlignment = VerticalAlignment.Center });

        var border = new Border { Style = (Style)FindResource("Card"), Padding = new Thickness(16, 10, 10, 10), Margin = new Thickness(0, 0, 0, 8), Child = panel };
        close.Click += (_, _) => Banners.Children.Remove(border);
        if (primary is not null) primary.Click += (_, _) => { action?.Invoke(); };
        Banners.Children.Add(border);
        return border;
    }

    public void RemoveBanner(Border banner) => Banners.Children.Remove(banner);

    // ───────────────────────── Primeira execução ─────────────────────────

    private async Task FirstRunAsync()
    {
        var store = AppServices.Settings;
        var settings = store.Current;

        if (!settings.FirewallConfigured)
        {
            Border? banner = null;
            banner = ShowBanner("Para outras pessoas conseguirem assistir, o ScreenFlow precisa liberar as portas no Firewall do Windows. O Windows vai pedir permissão.",
                "Liberar portas", () =>
                {
                    var ok = FirewallManager.CreateRules(settings.MediaPort, settings.ControlPort, settings.DiscoveryPort);
                    settings.FirewallConfigured = ok;
                    store.Save();
                    RemoveBanner(banner!);
                    if (!ok) ShowBanner("As regras do firewall não foram criadas. Você pode tentar de novo em Configurações › Rede.", warning: true);
                    else SetStatus("Portas liberadas no firewall.");
                },
                "Agora não", () =>
                {
                    settings.FirewallConfigured = FirewallManager.RulesExist(settings.MediaPort, settings.ControlPort, settings.DiscoveryPort);
                    store.Save();
                    RemoveBanner(banner!);
                });
        }

        if (!settings.AutoConfigured)
        {
            SetStatus("Detectando GPU, codecs, monitor e rede…");
            try
            {
                var result = await AppServices.AutoConfigureAsync();
                ShowBanner($"Configurado automaticamente para este PC: {result.Resolution.Label} a {result.Fps} FPS em {Core.Media.Quality.CodecLabel(result.Codec)} ({result.Encoder})."
                           + (result.Notes.Count > 0 ? " " + string.Join(" ", result.Notes) : string.Empty),
                    "Ver detalhes", () => Navigate(new SettingsPage(this), "Configurações"));
                SetStatus("Configuração automática concluída.");
            }
            catch (Exception exception)
            {
                Log.Error("Configuração automática falhou", exception);
                SetStatus($"Configuração automática falhou: {exception.Message}");
            }
        }
        else
        {
            _ = AppServices.Hardware; // aquece a detecção em segundo plano
        }

        if (settings.CheckUpdatesOnStart) await CheckForUpdatesAsync(silent: true);
    }

    public async Task CheckForUpdatesAsync(bool silent)
    {
        try
        {
            using var updater = new Updater();
            var release = await updater.CheckAsync();
            if (release is null)
            {
                if (!silent) ShowBanner($"Você já está na versão mais nova ({AppInfo.DisplayVersion}).");
                return;
            }
            if (silent && AppServices.Settings.Current.SkippedVersion == release.Tag) return;
            Border? banner = null;
            banner = ShowBanner($"Nova versão disponível: {release.Tag}.", "Atualizar agora", async () =>
            {
                RemoveBanner(banner!);
                await InstallUpdateAsync(release);
            }, "Pular esta versão", () =>
            {
                AppServices.Settings.Current.SkippedVersion = release.Tag;
                AppServices.Settings.Save();
                RemoveBanner(banner!);
            });
        }
        catch (Exception exception)
        {
            Log.Warn($"Verificação de atualização falhou: {exception.Message}");
            if (!silent) ShowBanner($"Não foi possível verificar atualizações: {exception.Message}", warning: true);
        }
    }

    public async Task InstallUpdateAsync(ReleaseInfo release)
    {
        if (_transmit?.IsRunning == true &&
            MessageBox.Show(this, "Atualizar agora vai encerrar a transmissão. Continuar?", AppInfo.Name, MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;
        try
        {
            using var updater = new Updater();
            var progress = new Progress<double>(p => SetStatus($"Baixando atualização… {p:P0}"));
            var path = await updater.DownloadAsync(release, progress);
            SetStatus("Instalando atualização…");
            if (_transmit is not null) await _transmit.StopAsync();
            Updater.LaunchInstaller(path);
            Application.Current.Shutdown();
        }
        catch (Exception exception)
        {
            Log.Error("Atualização falhou", exception);
            ShowBanner($"A atualização falhou: {exception.Message}", "Abrir página de downloads", () => OpenUrl(Updater.ReleasesPage), warning: true);
        }
    }

    public static void OpenUrl(string url) => Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });

    private bool _cleanedUp;

    /// <summary>Encerramento limpo: para captura, encoder, sockets e áudio antes de fechar.</summary>
    private async void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_cleanedUp) return;
        e.Cancel = true;
        IsEnabled = false;
        SetStatus("Encerrando…");
        _networkTimer.Stop();
        try
        {
            if (_transmit is not null) await _transmit.StopAsync();
            if (_watch is not null) await _watch.ShutdownAsync();
        }
        catch (Exception exception) { Log.Error("Falha ao encerrar", exception); }
        _cleanedUp = true;
        Close();
    }
}
