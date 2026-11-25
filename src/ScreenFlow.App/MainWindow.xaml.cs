using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using System.Windows.Threading;
using ScreenFlow.App.Pages;
using ScreenFlow.App.Services;
using ScreenFlow.App.Windows;
using ScreenFlow.Core;
using ScreenFlow.Core.Diagnostics;
using ScreenFlow.Core.Media;
using ScreenFlow.Core.Settings;
using ScreenFlow.Core.Updates;
using ScreenFlow.Infrastructure.Network;

namespace ScreenFlow.App;

public enum ToastKind { Info, Success, Warning, Error }

public partial class MainWindow : Window
{
    private readonly DispatcherTimer _networkTimer = new() { Interval = TimeSpan.FromSeconds(30) };
    private readonly DispatcherTimer _updateTimer = new() { Interval = UpdateService.Interval };
    private readonly Dictionary<RadioButton, string> _navLabels = [];
    private OverviewPage? _overview;
    private TransmitPage? _transmit;
    private WatchPage? _watch;
    private MonitorPage? _monitor;
    private SettingsPage? _settings;
    private UpdatesPage? _updates;
    private NetworkAdapterInfo? _radmin;
    private bool _radminInstalled = true;
    private bool _navigating;
    private bool _compact;
    private bool? _narrow;
    private bool _cleanedUp;

    public MainWindow()
    {
        InitializeComponent();
        VersionText.Text = $"Versão {AppInfo.DisplayVersion}";
        MachineText.Text = $"{AppServices.Settings.Current.DeviceName} · {AppServices.OsName}";
        foreach (RadioButton item in Nav.Children) _navLabels[item] = (string)item.Content;
        UpdateThemeButton();
        ThemeManager.Changed += UpdateThemeButton;
        NoticeList.ItemsSource = Notifications.Items;
        Notifications.Changed += UpdateBell;
        UpdateBell();

        AppServices.Updates.Found += OnUpdateFound;
        _updateTimer.Tick += (_, _) =>
        {
            if (AppServices.Settings.Current.CheckUpdatesOnStart) _ = AppServices.Updates.CheckAsync(manual: false);
        };
        _networkTimer.Tick += (_, _) => RefreshNetworkStatus();
        if (!AppServices.DemoMode)
        {
            NetworkChange.NetworkAddressChanged += OnNetworkChanged;
            NetworkChange.NetworkAvailabilityChanged += OnNetworkAvailabilityChanged;
        }

        SourceInitialized += (_, _) => ThemeManager.ApplyFrame(this);
        SizeChanged += (_, _) =>
        {
            // Janela estreita: o menu vira só ícones para sobrar espaço para a página (o botão de menu continua mandando).
            var narrow = ActualWidth < 1180;
            if (narrow == _narrow) return;
            _narrow = narrow;
            SetCompact(narrow);
        };
        Loaded += async (_, _) => await OnLoadedAsync();
        Navigate(OverviewPage);
    }

    // ───────────────────────── Páginas ─────────────────────────

    public OverviewPage OverviewPage => _overview ??= new OverviewPage(this);
    public TransmitPage TransmitPage => _transmit ??= new TransmitPage(this);
    public WatchPage WatchPage => _watch ??= new WatchPage(this);
    public MonitorPage MonitorPage => _monitor ??= new MonitorPage(this);
    public SettingsPage SettingsPage => _settings ??= new SettingsPage(this);
    public UpdatesPage UpdatesPage => _updates ??= new UpdatesPage(this);

    public bool IsTransmitting => _transmit?.IsRunning == true;
    public bool IsWatching => _watch?.IsWatching == true;

    /// <summary>Adaptador do Radmin com IP 26.x (null sem Radmin). Atualizado quando a rede muda.</summary>
    public NetworkAdapterInfo? Radmin => _radmin;

    /// <summary>A rede mudou (Radmin conectou ou caiu).</summary>
    public event Action? NetworkStatusChanged;

    /// <summary>A transmissão começou, terminou ou mudou o número de pessoas; ou começou/parou de assistir.</summary>
    public event Action? LiveStateChanged;

    public void NotifyLiveStateChanged()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(NotifyLiveStateChanged);
            return;
        }
        RefreshStatus();
        LiveStateChanged?.Invoke();
    }

    public void Navigate(UserControl page)
    {
        if (Host.Content == page) return;
        Host.Content = page;
        var (nav, title) = page switch
        {
            Pages.TransmitPage => (NavTransmit, "Transmissão"),
            Pages.WatchPage => (NavWatch, "Assistir"),
            Pages.MonitorPage => (NavMonitor, "Monitoramento"),
            Pages.SettingsPage => (NavSettings, "Configurações"),
            Pages.UpdatesPage => (NavUpdates, "Atualizações"),
            _ => (NavOverview, "Visão geral"),
        };
        _navigating = true;
        nav.IsChecked = true;
        _navigating = false;
        CrumbPage.Text = title;
        (page as IShellPage)?.OnShown();

        if (!AppServices.DemoMode)
        {
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            Host.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(200)) { EasingFunction = ease });
            HostTranslate.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(10, 0, TimeSpan.FromMilliseconds(240)) { EasingFunction = ease });
        }
    }

    private void Nav_Checked(object sender, RoutedEventArgs e)
    {
        if (_navigating || !IsInitialized) return;
        Navigate(sender switch
        {
            RadioButton r when r == NavTransmit => TransmitPage,
            RadioButton r when r == NavWatch => WatchPage,
            RadioButton r when r == NavMonitor => MonitorPage,
            RadioButton r when r == NavSettings => SettingsPage,
            RadioButton r when r == NavUpdates => UpdatesPage,
            _ => OverviewPage,
        });
    }

    private static void SetNavBadge(RadioButton item, bool on) => item.Uid = on ? "badge" : string.Empty;

    /// <summary>Tira o ponto rosa de Atualizações (a tela foi aberta).</summary>
    public void ClearUpdateBadge() => SetNavBadge(NavUpdates, false);

    // ───────────────────────── Avisos rápidos ─────────────────────────

    public void Toast(string message, ToastKind kind = ToastKind.Info, string? actionText = null, Action? action = null)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => Toast(message, kind, actionText, action));
            return;
        }
        var (glyph, brush) = kind switch
        {
            ToastKind.Success => ("", "Success"),
            ToastKind.Warning => ("", "Warning"),
            ToastKind.Error => ("", "Danger"),
            _ => ("", "Cyan"),
        };
        var bar = new Border { Width = 4, CornerRadius = new CornerRadius(2), Margin = new Thickness(0, 1, 14, 1) };
        bar.SetResourceReference(Border.BackgroundProperty, brush);
        var icon = new TextBlock { Style = (Style)FindResource("Icon"), Text = glyph, FontSize = 16, Margin = new Thickness(0, 2, 12, 0), VerticalAlignment = VerticalAlignment.Top };
        icon.SetResourceReference(TextBlock.ForegroundProperty, brush);
        var close = new Button { Style = (Style)FindResource("IconButton"), Content = "", FontSize = 10, Width = 28, Height = 28, MinHeight = 28, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(10, -4, -6, 0) };
        var body = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        body.Children.Add(new TextBlock { Text = message, Style = (Style)FindResource("Body"), FontSize = 13.5 });
        Border? toast = null;
        if (actionText is not null)
        {
            var button = new Button { Content = actionText, Style = (Style)FindResource("LinkButton"), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 8, 0, 0) };
            button.Click += (_, _) =>
            {
                action?.Invoke();
                RemoveToast(toast!);
            };
            body.Children.Add(button);
        }
        var panel = new DockPanel();
        DockPanel.SetDock(bar, Dock.Left);
        DockPanel.SetDock(icon, Dock.Left);
        DockPanel.SetDock(close, Dock.Right);
        panel.Children.Add(bar);
        panel.Children.Add(icon);
        panel.Children.Add(close);
        panel.Children.Add(body);
        toast = new Border
        {
            CornerRadius = new CornerRadius(14),
            Padding = new Thickness(14, 12, 14, 12),
            Margin = new Thickness(0, 10, 0, 0),
            BorderThickness = new Thickness(1),
            Child = panel,
            Effect = new DropShadowEffect { BlurRadius = 24, ShadowDepth = 6, Direction = 270, Opacity = 0.35, Color = Colors.Black },
            RenderTransform = new TranslateTransform(24, 0),
            Opacity = 0,
        };
        toast.SetResourceReference(Border.BackgroundProperty, "SurfaceRaised");
        toast.SetResourceReference(Border.BorderBrushProperty, "BorderStrong");
        close.Click += (_, _) => RemoveToast(toast);
        Toasts.Children.Add(toast);
        while (Toasts.Children.Count > 4) Toasts.Children.RemoveAt(0);
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        toast.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(220)) { EasingFunction = ease });
        toast.RenderTransform.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(260)) { EasingFunction = ease });
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(actionText is null ? 5 : 9) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (toast.IsMouseOver) { timer.Start(); return; }
            RemoveToast(toast);
        };
        timer.Start();
    }

    private void RemoveToast(Border toast)
    {
        if (!Toasts.Children.Contains(toast)) return;
        var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(180));
        fade.Completed += (_, _) => Toasts.Children.Remove(toast);
        toast.BeginAnimation(OpacityProperty, fade);
    }

    // ───────────────────────── Sininho ─────────────────────────

    private void UpdateBell()
    {
        var unread = Notifications.Unread;
        BellBadge.Visibility = unread > 0 ? Visibility.Visible : Visibility.Collapsed;
        BellCount.Text = unread > 9 ? "9+" : unread.ToString();
        NoNotices.Visibility = Notifications.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Bell_Click(object sender, RoutedEventArgs e) => NotificationsPopup.IsOpen = !NotificationsPopup.IsOpen;

    private void NotificationsPopup_Closed(object? sender, EventArgs e) => Notifications.MarkAllRead();

    private void MarkRead_Click(object sender, RoutedEventArgs e) => Notifications.MarkAllRead();

    private void NoticeAction_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not Notice notice) return;
        NotificationsPopup.IsOpen = false;
        notice.Action?.Invoke();
    }

    // ───────────────────────── Início ─────────────────────────

    private async Task OnLoadedAsync()
    {
        RefreshNetworkStatus();
        _networkTimer.Start();
        if (AppServices.DemoMode) return;

        var settings = AppServices.Settings.Current;
        _ = CheckFirewallAsync();
        if (!settings.AutoConfigured)
        {
            try
            {
                var result = await AppServices.AutoConfigureAsync();
                Toast($"Configurado para este PC: {result.Resolution.Label} a {result.Fps} FPS em {Quality.CodecLabel(result.Codec)} ({result.Encoder}).",
                    ToastKind.Success, "Ver detalhes", () => Navigate(SettingsPage));
                (Host.Content as IShellPage)?.OnShown();
            }
            catch (Exception exception)
            {
                Log.Error("Configuração automática falhou", exception);
                Toast($"A configuração automática falhou: {exception.Message}", ToastKind.Warning);
            }
        }
        else
        {
            _ = AppServices.Hardware; // aquece a detecção em segundo plano
        }

        if (settings.CheckUpdatesOnStart) _ = AppServices.Updates.CheckAsync(manual: false);
        _updateTimer.Start();
    }

    /// <summary>O instalador já libera as portas; se faltar alguma regra (portas mudadas, instalação antiga), avisa pelo sininho.</summary>
    public async Task CheckFirewallAsync()
    {
        var s = AppServices.Settings.Current;
        var exists = await Task.Run(() => FirewallManager.RulesExist(s.MediaPort, s.ControlPort, s.DiscoveryPort));
        if (exists)
        {
            if (!s.FirewallConfigured)
            {
                s.FirewallConfigured = true;
                AppServices.Settings.Save();
            }
            Notifications.Remove("firewall");
            return;
        }
        s.FirewallConfigured = false;
        AppServices.Settings.Save();
        Notifications.Post(new Notice("firewall", NoticeKind.Firewall, "Portas fechadas no firewall",
            "Sem liberar as portas, quem está na sua rede Radmin não consegue assistir. O Windows pede permissão uma vez.",
            "Liberar portas", () => _ = OpenFirewallAsync()));
    }

    public async Task<bool> OpenFirewallAsync()
    {
        var s = AppServices.Settings.Current;
        var ok = await Task.Run(() => FirewallManager.CreateRules(s.MediaPort, s.ControlPort, s.DiscoveryPort));
        s.FirewallConfigured = ok;
        AppServices.Settings.Save();
        if (ok)
        {
            Notifications.Remove("firewall");
            Toast("Portas liberadas no firewall.", ToastKind.Success);
        }
        else Toast("As regras do firewall não foram criadas (a permissão foi recusada?).", ToastKind.Warning);
        return ok;
    }

    // ───────────────────────── Atualizações ─────────────────────────

    private void OnUpdateFound(ReleaseInfo release)
    {
        var version = release.Version.ToString(3);
        Notifications.Post(new Notice("update", NoticeKind.Update, $"Versão {version} disponível",
            "Atualize com um clique: o ScreenFlow baixa o instalador oficial, confere e reabre sozinho.",
            "Atualizar agora", () => _ = InstallUpdateAsync(release)));
        SetNavBadge(NavUpdates, true);
        Toast($"Saiu a versão {version} do ScreenFlow.", ToastKind.Info, "Ver novidades", () => Navigate(UpdatesPage));
    }

    public async Task InstallUpdateAsync(ReleaseInfo release)
    {
        if (IsTransmitting && !ConfirmDialog.Ask(this, "Atualizar agora?", "A transmissão vai ser encerrada e o ScreenFlow reabre na versão nova em alguns segundos.", "Atualizar"))
            return;
        Navigate(UpdatesPage);
        var ok = await AppServices.Updates.InstallAsync(release, StopAllAsync);
        if (!ok) return;
        _cleanedUp = true;
        Application.Current.Shutdown();
    }

    public static void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception exception) { Log.Warn($"Não foi possível abrir {url}: {exception.Message}"); }
    }

    // ───────────────────────── Rede e status ─────────────────────────

    private void OnNetworkChanged(object? sender, EventArgs e) => Dispatcher.BeginInvoke(RefreshNetworkStatus);
    private void OnNetworkAvailabilityChanged(object? sender, NetworkAvailabilityEventArgs e) => Dispatcher.BeginInvoke(RefreshNetworkStatus);

    public async void RefreshNetworkStatus()
    {
        try
        {
            var (radmin, installed) = await Task.Run(() =>
            {
                var found = AppServices.FindRadmin();
                return (found, found is not null || AppServices.DemoMode || RadminNetwork.IsRadminInstalled());
            });
            _radmin = radmin;
            _radminInstalled = installed;
        }
        catch (Exception exception)
        {
            Log.Warn($"Falha ao ler a rede: {exception.Message}");
        }
        RadminDot.SetResourceReference(Shape.FillProperty, _radmin is null ? "Warning" : "Success");
        RadminText.Text = _radmin is null ? (_radminInstalled ? "Radmin sem rede ativa" : "Radmin VPN não encontrado") : $"Radmin · {_radmin.Address}";
        RefreshStatus();
        NetworkStatusChanged?.Invoke();
    }

    /// <summary>Pílula de status da barra superior: ao vivo, assistindo, disponível ou sem Radmin.</summary>
    public void RefreshStatus()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(RefreshStatus);
            return;
        }
        var (text, foreground, background, glow) = IsTransmitting
            ? (_transmit!.Viewers switch { 0 => "Ao vivo", 1 => "Ao vivo · 1 pessoa", var n => $"Ao vivo · {n} pessoas" }, "Pink", "PinkSoft", Color.FromRgb(0xFF, 0x4D, 0x8D))
            : IsWatching
                ? ("Assistindo", "Cyan", "CyanSoft", Color.FromRgb(0x22, 0xD3, 0xEE))
                : _radmin is null
                    ? ("Sem Radmin", "Amber", "AmberSoft", Color.FromRgb(0xFF, 0xB5, 0x47))
                    : ("Disponível", "AccentText", "AccentSoft", Color.FromRgb(0x2E, 0xE6, 0xA6));
        StatusText.Text = text;
        StatusPill.SetResourceReference(TextElement.ForegroundProperty, foreground);
        StatusPill.SetResourceReference(Border.BackgroundProperty, background);
        StatusPill.SetResourceReference(Border.BorderBrushProperty, background);
        StatusDot.SetResourceReference(Shape.FillProperty, foreground);
        StatusGlow.Color = glow;
        SetNavBadge(NavTransmit, IsTransmitting);
    }

    // ───────────────────────── Janela ─────────────────────────

    private void ToggleSidebar_Click(object sender, RoutedEventArgs e) => SetCompact(!_compact);

    private void SetCompact(bool compact)
    {
        _compact = compact;
        SidebarColumn.Width = new GridLength(_compact ? 86 : 260);
        BrandText.Visibility = MachinePanel.Visibility = _compact ? Visibility.Collapsed : Visibility.Visible;
        foreach (var (item, label) in _navLabels)
        {
            item.Content = _compact ? null : label;
            item.ToolTip = _compact ? label : null;
        }
    }

    private void Theme_Click(object sender, RoutedEventArgs e)
    {
        var next = ThemeManager.Current == AppTheme.Dark ? AppTheme.Light : AppTheme.Dark;
        ThemeManager.Apply(next);
        AppServices.Settings.Current.Theme = next;
        AppServices.Settings.Save();
        UpdateThemeButton();
    }

    private void UpdateThemeButton() => ThemeButton.Content = ThemeManager.Current == AppTheme.Dark ? "" : "";

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void Maximize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void Window_StateChanged(object? sender, EventArgs e)
    {
        // Maximizada, a janela sem moldura passa um pouco das bordas da tela: compensa com margem.
        Root.Margin = WindowState == WindowState.Maximized ? SystemParameters.WindowResizeBorderThickness : new Thickness(0);
        MaximizeButton.Content = WindowState == WindowState.Maximized ? "" : "";
        MaximizeButton.ToolTip = WindowState == WindowState.Maximized ? "Restaurar" : "Maximizar";
    }

    public async Task StopAllAsync()
    {
        if (_transmit is not null) await _transmit.StopAsync();
        if (_watch is not null) await _watch.ShutdownAsync();
    }

    /// <summary>Encerramento limpo: para captura, encoder, sockets e áudio antes de fechar.</summary>
    private async void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_cleanedUp) return;
        e.Cancel = true;
        IsEnabled = false;
        _networkTimer.Stop();
        _updateTimer.Stop();
        NetworkChange.NetworkAddressChanged -= OnNetworkChanged;
        NetworkChange.NetworkAvailabilityChanged -= OnNetworkAvailabilityChanged;
        try { await StopAllAsync(); }
        catch (Exception exception) { Log.Error("Falha ao encerrar", exception); }
        _cleanedUp = true;
        Close();
    }
}
