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
using ScreenFlow.Infrastructure.Room;

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
    private ChatPage? _chat;
    private NetworkAdapterInfo? _radmin;
    private bool _radminInstalled = true;
    private bool _navigating;
    private bool _compact;
    private bool? _narrow;
    private bool _cleanedUp;
    private TrayIcon? _tray;
    private bool _exitRequested;
    private WindowState _restoreState = WindowState.Normal;
    private Action? _balloonAction;
    private DateTimeOffset _lastChatToast;
    private DateTimeOffset _lastChatBalloon;

    public MainWindow()
    {
        InitializeComponent();
        VersionText.Text = $"Versão {AppInfo.DisplayVersion}";
        MachineText.Text = AppServices.OsName;
        foreach (RadioButton item in Nav.Children) _navLabels[item] = (string)item.Content;
        UpdateThemeButton();
        ThemeManager.Changed += UpdateThemeButton;
        NoticeList.ItemsSource = Notifications.Items;
        Notifications.Changed += UpdateBell;
        UpdateBell();

        // Voz e chat da rede
        var room = AppServices.Room;
        VoiceList.ItemsSource = room.VoiceMembers;
        room.Changed += UpdateVoicePanel;
        room.UnreadChanged += UpdateChatBadge;
        room.MessageArrived += OnRoomMessage;
        room.PersonWentLive += OnPersonWentLive;
        room.ProfileChanged += UpdateProfile;
        UpdateProfile();
        room.IsChatInFront = () => Host.Content is Pages.ChatPage && IsVisible && IsActive && WindowState != WindowState.Minimized;
        Activated += (_, _) =>
        {
            if (Host.Content is Pages.ChatPage) AppServices.Room.MarkRead();
        };
        UpdateVoicePanel();
        UpdateChatBadge();

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
        Closed += (_, _) =>
        {
            _tray?.Dispose();
            _tray = null;
        };
        Navigate(OverviewPage);
    }

    // ───────────────────────── Páginas ─────────────────────────

    public OverviewPage OverviewPage => _overview ??= new OverviewPage(this);
    public TransmitPage TransmitPage => _transmit ??= new TransmitPage(this);
    public WatchPage WatchPage => _watch ??= new WatchPage(this);
    public MonitorPage MonitorPage => _monitor ??= new MonitorPage(this);
    public SettingsPage SettingsPage => _settings ??= new SettingsPage(this);
    public UpdatesPage UpdatesPage => _updates ??= new UpdatesPage(this);
    public ChatPage ChatPage => _chat ??= new ChatPage(this);

    public bool IsTransmitting => _transmit?.IsRunning == true;
    public bool IsWatching => _watch?.IsWatching == true;

    /// <summary>Adaptador do Radmin com IP 26.x (null sem Radmin). Atualizado quando a rede muda.</summary>
    public NetworkAdapterInfo? Radmin => _radmin;

    /// <summary>O Radmin VPN está instalado (mesmo que sem rede ativa).</summary>
    public bool RadminInstalled => _radminInstalled;

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
        PushRoomState();
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
            Pages.ChatPage => (NavChat, "Chat da rede"),
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
            RadioButton r when r == NavChat => ChatPage,
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
        SetupTray();
        try
        {
            AppServices.Room.Start();
            PushRoomState();
        }
        catch (Exception exception)
        {
            Log.Error("A sala da rede não abriu", exception);
        }
        UpdateVoicePanel();

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
                return (found, found is not null || RadminSetup.IsInstalled());
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
        UpdateTray();
    }

    // ───────────────────────── Janela ─────────────────────────

    private void ToggleSidebar_Click(object sender, RoutedEventArgs e) => SetCompact(!_compact);

    private void SetCompact(bool compact)
    {
        _compact = compact;
        SidebarColumn.Width = new GridLength(_compact ? 86 : 260);
        BrandText.Visibility = MachinePanel.Visibility = _compact ? Visibility.Collapsed : Visibility.Visible;
        MachineCard.Padding = new Thickness(_compact ? 6 : 12); // só o ícone cabe no menu recolhido
        UpdateProfile();
        foreach (var (item, label) in _navLabels)
        {
            item.Content = _compact ? null : label;
            item.ToolTip = _compact ? label : null;
        }
        // Painel da voz recolhido: só os botões, um embaixo do outro.
        VoiceHeader.Visibility = _compact ? Visibility.Collapsed : Visibility.Visible;
        VoicePanel.Padding = new Thickness(_compact ? 5 : 10);
        VoiceButtons.Rows = _compact ? 0 : 1;
        VoiceButtons.Columns = _compact ? 1 : 0;
        VoiceButtons.Margin = _compact ? new Thickness(0, -3, 0, -3) : new Thickness(-3, 10, -3, 0);
        foreach (FrameworkElement child in VoiceButtons.Children) child.Margin = _compact ? new Thickness(0, 3, 0, 3) : new Thickness(3, 0, 3, 0);
        // Recolhido, o menu pode não caber em janelas baixas: continua rolando com a rodinha, sem a barra por cima dos ícones.
        NavScroll.VerticalScrollBarVisibility = _compact ? ScrollBarVisibility.Hidden : ScrollBarVisibility.Auto;
        UpdateVoicePanel();
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

    private void Minimize_Click(object sender, RoutedEventArgs e)
    {
        if (TrayEnabled) HideToTray();
        else WindowState = WindowState.Minimized;
    }

    private void Maximize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void Window_StateChanged(object? sender, EventArgs e)
    {
        // Maximizada, a janela sem moldura passa um pouco das bordas da tela: compensa com margem.
        Root.Margin = WindowState == WindowState.Maximized ? SystemParameters.WindowResizeBorderThickness : new Thickness(0);
        MaximizeButton.Content = WindowState == WindowState.Maximized ? "" : "";
        MaximizeButton.ToolTip = WindowState == WindowState.Maximized ? "Restaurar" : "Maximizar";
        // Minimizada pela barra de tarefas (ou Win+↓): vai para a bandeja em vez de ficar na barra.
        if (WindowState == WindowState.Minimized && TrayEnabled && IsVisible) HideToTray();
    }

    // ───────────────────────── Bandeja ─────────────────────────

    /// <summary>Fechar e minimizar mandam para a bandeja (configurável), desde que o ícone esteja lá.</summary>
    private bool TrayEnabled => _tray is { IsVisible: true } && AppServices.Settings.Current.MinimizeToTray;

    private void SetupTray()
    {
        try
        {
            _tray = new TrayIcon();
            _tray.Activated += () => ShowFromTray();
            _tray.MenuRequested += () => _tray?.ShowMenu(BuildTrayMenu());
            _tray.BalloonClicked += () =>
            {
                var action = _balloonAction;
                _balloonAction = null;
                ShowFromTray();
                action?.Invoke();
            };
            UpdateTray();
        }
        catch (Exception exception)
        {
            Log.Warn($"Ícone da bandeja indisponível: {exception.Message}");
            _tray?.Dispose();
            _tray = null;
        }
    }

    /// <summary>Some da tela e da barra de tarefas; o app continua rodando no ícone perto do relógio.</summary>
    public void HideToTray()
    {
        if (_tray is null)
        {
            WindowState = WindowState.Minimized;
            return;
        }
        if (WindowState != WindowState.Minimized) _restoreState = WindowState;
        NotificationsPopup.IsOpen = false;
        Hide();
        var settings = AppServices.Settings.Current;
        if (!settings.TrayHintShown)
        {
            settings.TrayHintShown = true;
            AppServices.Settings.Save();
            _tray.ShowBalloon("O ScreenFlow continua aberto aqui",
                "Clique no ícone para voltar. Para fechar de vez, clique com o botão direito no ícone e escolha Sair do ScreenFlow.", quiet: true);
        }
    }

    /// <summary>Volta da bandeja (ou traz para a frente), opcionalmente já numa página.</summary>
    public void ShowFromTray(UserControl? page = null)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => ShowFromTray(page));
            return;
        }
        if (_cleanedUp) return;
        Show();
        if (WindowState == WindowState.Minimized) WindowState = _restoreState;
        Activate();
        // Garante que a janela venha para a frente mesmo quando outro app está em primeiro plano.
        Topmost = true;
        Topmost = false;
        Focus();
        if (page is not null) Navigate(page);
    }

    /// <summary>
    /// A transmissão acabou (pelo menu da bandeja, por erro) ou o player fechou: se o ScreenFlow estava na bandeja ou
    /// minimizado, a janela volta na página certa. Com a janela aberta nada muda; saindo do app, nada acontece.
    /// </summary>
    public void ShowAfterSessionEnded(UserControl page)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => ShowAfterSessionEnded(page));
            return;
        }
        if (_exitRequested || _cleanedUp || !IsLoaded || !AppServices.Settings.Current.ShowWindowWhenSessionEnds) return;
        if (IsVisible && WindowState != WindowState.Minimized) return;
        ShowFromTray(page);
    }

    /// <summary>Notificação do Windows pelo ícone da bandeja; o clique nela abre o app e roda a ação.</summary>
    public void TrayNotify(string title, string message, Action? onClick = null, bool quiet = false)
    {
        if (_tray is null) return;
        _balloonAction = onClick;
        _tray.ShowBalloon(title, message, quiet);
    }

    /// <summary>true quando a janela está escondida na bandeja ou minimizada (quem usa decide se avisa pelo Windows).</summary>
    public bool IsInBackground => !IsVisible || WindowState == WindowState.Minimized || !IsActive;

    /// <summary>Fecha de verdade: para tudo e sai (menu da bandeja ou fechar com a bandeja desligada).</summary>
    public void ExitApplication()
    {
        _exitRequested = true;
        Close();
    }

    /// <summary>O Windows está desligando ou o usuário está saindo da conta: não esconde na bandeja.</summary>
    public void PrepareForSessionEnd() => _exitRequested = true;

    /// <summary>Dica e bolinha do ícone: ao vivo (o que está indo), assistindo, voz da rede.</summary>
    public void UpdateTray()
    {
        if (_tray is null) return;
        var lines = new List<string>(4);
        TrayBadge badge;
        if (IsTransmitting)
        {
            badge = TrayBadge.Live;
            lines.Add("ScreenFlow · AO VIVO");
            lines.Add($"Transmitindo {TransmitPage.LiveSummary}");
            lines.Add(TransmitPage.Viewers switch { 0 => "Ninguém assistindo ainda", 1 => "1 pessoa assistindo", var n => $"{n} pessoas assistindo" });
        }
        else if (IsWatching)
        {
            badge = TrayBadge.Watching;
            lines.Add("ScreenFlow · assistindo");
            if (WatchPage.WatchingSummary is { Length: > 0 } watching) lines.Add(watching);
        }
        else
        {
            badge = TrayBadge.None;
            lines.Add(_radmin is null ? "ScreenFlow · sem Radmin" : "ScreenFlow · disponível");
            lines.Add("Não está transmitindo");
        }
        if (VoiceTrayLine() is { } voice)
        {
            lines.Add(voice);
            if (badge == TrayBadge.None && AppServices.Room.InVoice) badge = TrayBadge.Voice;
        }
        _tray.Set(string.Join("\n", lines), badge);
    }

    private ContextMenu BuildTrayMenu()
    {
        var menu = new ContextMenu();
        var header = new StackPanel();
        header.Children.Add(new TextBlock { Text = "ScreenFlow", FontWeight = FontWeights.Bold, FontSize = 14.5 });
        var status = IsTransmitting
            ? $"Ao vivo: {TransmitPage.LiveSummary}"
            : IsWatching ? "Assistindo uma transmissão" : "Não está transmitindo";
        var line = new TextBlock { Text = status, FontSize = 12.5, Margin = new Thickness(0, 2, 0, 0) };
        line.SetResourceReference(TextBlock.ForegroundProperty, IsTransmitting ? "Pink" : "TextMuted");
        header.Children.Add(line);
        menu.Items.Add(new MenuItem { Header = header, Style = (Style)FindResource("MenuHeader") });
        menu.Items.Add(new Separator());

        menu.Items.Add(TrayItem("Abrir o ScreenFlow", "\uE8A7", () => ShowFromTray()));
        if (IsTransmitting) menu.Items.Add(TrayItem("Encerrar a transmissão", "\uE71A", () => _ = TransmitPage.StopAsync()));
        else menu.Items.Add(TrayItem("Transmitir…", "\uE714", () => ShowFromTray(TransmitPage)));
        AddVoiceTrayItems(menu);
        menu.Items.Add(new Separator());
        var exit = TrayItem("Sair do ScreenFlow", "\uE7E8", ExitApplication);
        exit.SetResourceReference(ForegroundProperty, "Danger");
        menu.Items.Add(exit);
        return menu;
    }

    /// <summary>Linha da voz (ou das mensagens novas do chat) na dica da bandeja. Null quando não tem nada.</summary>
    private static string? VoiceTrayLine()
    {
        var room = AppServices.Room;
        if (!room.InVoice)
            return room.Unread switch { 0 => null, 1 => "1 mensagem nova no chat", var n => $"{n} mensagens novas no chat" };
        var others = Math.Max(0, room.VoiceMembers.Count - 1);
        var line = others switch { 0 => "Na voz da rede (só você)", 1 => "Na voz com 1 pessoa", var n => $"Na voz com {n} pessoas" };
        return room.Deafened ? line + " · sem som" : room.MicrophoneOff ? line + " · mudo" : line;
    }

    private void AddVoiceTrayItems(ContextMenu menu)
    {
        var room = AppServices.Room;
        if (!room.IsAvailable) return;
        menu.Items.Add(new Separator());
        if (room.InVoice)
        {
            menu.Items.Add(TrayItem("Microfone desligado", "\uE720", room.ToggleMute, room.MicrophoneOff));
            menu.Items.Add(TrayItem("Som desligado", "\uE7F6", room.ToggleDeafen, room.Deafened));
            menu.Items.Add(TrayItem("Sair da voz", "\uE778", () => _ = room.LeaveVoiceAsync()));
        }
        else menu.Items.Add(TrayItem("Entrar na voz da rede", "\uE7F6", () => _ = JoinVoiceAsync()));
        var chat = room.Unread switch { 0 => "Abrir o chat da rede", 1 => "Abrir o chat da rede (1 nova)", var n => $"Abrir o chat da rede ({n} novas)" };
        menu.Items.Add(TrayItem(chat, "\uE8BD", () => ShowFromTray(ChatPage)));
    }

    // ───────────────────────── Voz e chat da rede ─────────────────────────

    /// <summary>Painel da voz no menu lateral: quem está na voz, entrar e sair, microfone, fone e o chat.</summary>
    private void UpdateVoicePanel()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(UpdateVoicePanel);
            return;
        }
        var room = AppServices.Room;
        var inVoice = room.InVoice;
        var talking = room.VoiceMembers.Count;
        var others = room.People.Count;
        VoiceTitle.Text = inVoice ? "Voz conectada" : "Voz da rede";
        VoiceTitle.SetResourceReference(TextBlock.ForegroundProperty, inVoice ? "Success" : "Text");
        foreach (var bar in VoiceSignal.Children.OfType<Rectangle>()) bar.SetResourceReference(Shape.FillProperty, inVoice ? "Success" : "TextSubtle");
        VoiceSubtitle.Text = !room.IsAvailable ? "Sala da rede desligada"
            : room.IsJoining ? "Ligando o microfone…"
            : inVoice ? (talking <= 1 ? "Só você por enquanto" : $"{talking} pessoas conversando")
            : talking > 0 ? (talking == 1 ? "1 pessoa na voz" : $"{talking} pessoas na voz")
            : others switch { 0 => "Ninguém da rede ainda", 1 => "1 pessoa na rede", var n => $"{n} pessoas na rede" };
        VoiceSubtitle.ToolTip = room.Error;
        VoiceList.Visibility = talking > 0 && !_compact ? Visibility.Visible : Visibility.Collapsed;
        LeaveVoiceButton.Visibility = inVoice ? Visibility.Visible : Visibility.Collapsed;
        JoinVoiceButton.Visibility = inVoice ? Visibility.Collapsed : Visibility.Visible;
        JoinVoiceButton.IsEnabled = room.IsAvailable && !room.IsJoining;
        JoinVoiceButton.ToolTip = talking > 0 ? $"Entrar na voz da rede ({VoiceSubtitle.Text})" : "Entrar na voz da rede";
        MuteToggle.Visibility = DeafenToggle.Visibility = inVoice ? Visibility.Visible : Visibility.Collapsed;
        CompactLeaveButton.Visibility = inVoice && _compact ? Visibility.Visible : Visibility.Collapsed;
        MuteToggle.IsChecked = room.MicrophoneOff;
        MuteToggle.ToolTip = room.MicrophoneOff ? "Ligar o microfone" : "Desligar o microfone";
        DeafenToggle.IsChecked = room.Deafened;
        DeafenToggle.ToolTip = room.Deafened ? "Ligar o som" : "Desligar o som de todo mundo";
        JoinVoiceLabel.Visibility = _compact ? Visibility.Collapsed : Visibility.Visible;
        ChatLabel.Visibility = inVoice || _compact ? Visibility.Collapsed : Visibility.Visible;
        // No menu recolhido o chat já está na lista (com a bolinha de não lidas): o painel fica só com a voz.
        ChatButtonBox.Visibility = _compact ? Visibility.Collapsed : Visibility.Visible;
        UpdateTray();
    }

    /// <summary>A sua foto e o seu nome no canto do menu.</summary>
    private void UpdateProfile()
    {
        var room = AppServices.Room;
        var name = AppServices.Settings.Current.DeviceName;
        ProfileAvatar.Colors = AvatarColors.For(room.SelfId);
        ProfileAvatar.Initial = AvatarColors.Initial(name);
        ProfileAvatar.Photo = room.SelfPhoto;
        ProfileName.Text = name;
        MachineCard.ToolTip = _compact ? $"{name} · trocar seu nome e sua foto" : "Trocar seu nome e sua foto";
    }

    private void MachineCard_MouseLeftButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e) => Navigate(SettingsPage);

    private void UpdateChatBadge()
    {
        var unread = AppServices.Room.Unread;
        ChatBadge.Visibility = unread > 0 ? Visibility.Visible : Visibility.Collapsed;
        ChatCount.Text = unread > 9 ? "9+" : unread.ToString();
        SetNavBadge(NavChat, unread > 0);
        UpdateTray();
    }

    private async void JoinVoice_Click(object sender, RoutedEventArgs e) => await JoinVoiceAsync();

    private async Task JoinVoiceAsync()
    {
        if (await AppServices.Room.JoinVoiceAsync() is not { } error) return;
        if (IsInBackground) TrayNotify("Não deu para entrar na voz", error, () => Navigate(SettingsPage));
        else Toast(error, ToastKind.Warning, "Escolher o microfone", () => Navigate(SettingsPage));
    }

    private async void LeaveVoice_Click(object sender, RoutedEventArgs e) => await AppServices.Room.LeaveVoiceAsync();

    private void Mute_Click(object sender, RoutedEventArgs e) => AppServices.Room.ToggleMute();

    private void Deafen_Click(object sender, RoutedEventArgs e) => AppServices.Room.ToggleDeafen();

    private void OpenChat_Click(object sender, RoutedEventArgs e) => Navigate(ChatPage);

    /// <summary>Mensagem nova com o chat fechado: aviso rápido na janela, ou notificação do Windows com o app escondido.</summary>
    private void OnRoomMessage(ChatRow row)
    {
        if (AppServices.Room.IsChatInFront?.Invoke() == true) return;
        var now = DateTimeOffset.UtcNow;
        var preview = Preview(row.Text, 110);
        if (IsInBackground)
        {
            if (now - _lastChatBalloon < TimeSpan.FromSeconds(20)) return;
            _lastChatBalloon = now;
            TrayNotify($"{row.Author} no chat da rede", preview, () => Navigate(ChatPage));
            return;
        }
        if (now - _lastChatToast < TimeSpan.FromSeconds(4)) return;
        _lastChatToast = now;
        Toast($"{row.Author}: {preview}", ToastKind.Info, "Responder", () => Navigate(ChatPage));
    }

    private void OnPersonWentLive(RoomPersonRow person)
    {
        var what = person.StatusText.StartsWith("Ao vivo · ", StringComparison.Ordinal) ? person.StatusText["Ao vivo · ".Length..] : "agora";
        if (IsInBackground) TrayNotify($"{person.Name} está ao vivo", $"Transmitindo {what}. Clique para {person.ActionText.ToLowerInvariant()}.", () => WatchPerson(person));
        else Toast($"{person.Name} começou a transmitir: {what}.", ToastKind.Info, person.ActionText, () => WatchPerson(person));
    }

    /// <summary>Abre o player na transmissão de alguém da sala.</summary>
    public void WatchPerson(RoomPersonRow person)
    {
        if (person.WatchAddress is not { } address) return;
        ShowFromTray(WatchPage);
        WatchPage.ConnectTo(address, person.LiveMode);
    }

    /// <summary>Corta o texto num limite sem partir um emoji no meio.</summary>
    private static string Preview(string text, int max)
    {
        text = text.ReplaceLineEndings(" ");
        if (text.Length <= max) return text;
        var cut = max;
        if (char.IsHighSurrogate(text[cut - 1])) cut--;
        return text[..cut].TrimEnd() + "…";
    }

    /// <summary>Conta para a sala o que este PC está fazendo (transmitindo o quê, assistindo).</summary>
    private void PushRoomState()
    {
        if (AppServices.DemoMode) return;
        var transmit = _transmit;
        var live = transmit?.IsRunning == true;
        var settings = AppServices.Settings.Current;
        AppServices.Room.UpdateLocal(new RoomLocalState(
            settings.DeviceName,
            Live: live,
            LiveMode: transmit?.LiveMode ?? StreamMode.Screen,
            LiveAudio: live && transmit!.LiveAudio,
            LiveCamera: live && transmit!.LiveCamera,
            Viewers: live ? transmit!.Viewers : 0,
            PinRequired: live && transmit!.LivePinRequired,
            LiveAddress: live ? transmit!.LiveAddress : null,
            Watching: IsWatching,
            ControlPort: settings.ControlPort));
    }

    private static MenuItem TrayItem(string text, string glyph, Action action, bool? isChecked = null)
    {
        var item = new MenuItem { Header = text, Tag = glyph };
        if (isChecked is not null) item.IsChecked = isChecked.Value;
        item.Click += (_, _) => action();
        return item;
    }

    public async Task StopAllAsync()
    {
        if (_transmit is not null) await _transmit.StopAsync();
        if (_watch is not null) await _watch.ShutdownAsync();
        await AppServices.Room.StopAsync();
    }

    /// <summary>Encerramento limpo: para captura, encoder, sockets e áudio antes de fechar.</summary>
    private async void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_cleanedUp) return;
        e.Cancel = true;
        if (!_exitRequested && TrayEnabled)
        {
            // Fechar pelo X (ou Alt+F4) só esconde: a transmissão, a voz e o chat continuam.
            HideToTray();
            return;
        }
        _exitRequested = true; // a transmissão e o player que fecham agora não trazem a janela de volta
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
