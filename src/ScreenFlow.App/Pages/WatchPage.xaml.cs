using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using ScreenFlow.App.Services;
using ScreenFlow.App.Windows;
using ScreenFlow.Core.Diagnostics;
using ScreenFlow.Core.Media;
using ScreenFlow.Core.Network;
using ScreenFlow.Core.Protocol;
using ScreenFlow.Core.Settings;
using ScreenFlow.Infrastructure.Hardware;
using ScreenFlow.Infrastructure.Network;
using ScreenFlow.Infrastructure.Session;

namespace ScreenFlow.App.Pages;

/// <summary>Assistir: transmissões encontradas na rede Radmin, conexão pelo IP e o resumo do que você está assistindo.</summary>
public partial class WatchPage : UserControl, IShellPage
{
    private readonly MainWindow _shell;
    private readonly DispatcherTimer _refresh = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly ObservableCollection<SessionCard> _sessions = [];
    private DiscoveryListener? _discovery;
    private PlayerWindow? _player;
    private UniformGrid? _sessionGrid;
    private string? _currentHost;
    private bool _connecting;
    private bool _pulsing;

    public WatchPage(MainWindow shell)
    {
        _shell = shell;
        InitializeComponent();
        SessionList.ItemsSource = _sessions;
        _refresh.Tick += (_, _) => RefreshSessions();
        Unloaded += async (_, _) => await StopDiscoveryAsync();
        SizeChanged += (_, _) => UpdateColumns();
        Telemetry.Updated += () =>
        {
            if (_player is not null && IsLoaded) UpdateNowCard();
        };
    }

    /// <summary>Tem um player aberto (conectando, assistindo ou tentando reconectar).</summary>
    public bool IsWatching => _player is not null;

    /// <summary>Quem e o que você está assistindo, para a dica do ícone da bandeja. Vazio sem player.</summary>
    public string WatchingSummary
    {
        get
        {
            if (_player is null) return string.Empty;
            var session = _player.Session;
            var name = session.HostName.Length > 0 ? session.HostName : _currentHost ?? string.Empty;
            if (session.State != ConnectionState.Connected) return $"Conectando em {name}";
            var config = session.Config;
            var mode = config?.Mode ?? _player.ExpectedMode ?? StreamMode.Screen;
            return $"{name}: {StreamModes.Summary(mode, config?.AudioEnabled ?? true, config?.CameraOverlay ?? false)}";
        }
    }

    private AppSettings Settings => AppServices.Settings.Current;

    public void OnShown()
    {
        RefreshRecent();
        StartDiscovery();
        RefreshSessions();
        UpdateNowCard();
    }

    // ───────────────────────── Descoberta ─────────────────────────

    private void StartDiscovery()
    {
        if (_discovery is null && !AppServices.DemoMode)
        {
            var discovery = new DiscoveryListener(Settings.DiscoveryPort);
            discovery.Changed += () => Dispatcher.BeginInvoke(RefreshSessions);
            discovery.Start();
            _discovery = discovery;
        }
        _refresh.Start();
    }

    private async Task StopDiscoveryAsync()
    {
        _refresh.Stop();
        var discovery = _discovery;
        _discovery = null;
        if (discovery is not null) await discovery.DisposeAsync();
    }

    /// <summary>Atualiza os cartões no lugar (sem recriar), para não piscar a cada segundo.</summary>
    private void RefreshSessions()
    {
        IReadOnlyList<DiscoveryAnnouncement> found = AppServices.DemoMode
            ? DemoData.Sessions
            : _discovery?.Hosts.Select(h => h.Announcement).ToList() ?? [];
        foreach (var gone in _sessions.Where(s => found.All(f => f.Address != s.Address)).ToList()) _sessions.Remove(gone);
        foreach (var announcement in found)
        {
            var card = _sessions.FirstOrDefault(s => s.Address == announcement.Address);
            if (card is null)
            {
                card = new SessionCard(announcement.Address);
                var index = 0;
                while (index < _sessions.Count && string.Compare(_sessions[index].Name, announcement.HostName, StringComparison.CurrentCultureIgnoreCase) < 0) index++;
                _sessions.Insert(index, card);
            }
            card.Update(announcement, _player is not null && _currentHost == announcement.Address);
        }

        var any = _sessions.Count > 0;
        SessionList.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
        NoSessions.Visibility = any ? Visibility.Collapsed : Visibility.Visible;
        ScanText.Text = _sessions.Count switch
        {
            0 => _discovery is null && !AppServices.DemoMode ? "Busca parada" : "Procurando…",
            1 => "1 transmissão ao vivo",
            var n => $"{n} transmissões ao vivo",
        };
        SetPulse(!any && !AppServices.DemoMode);
    }

    private void SetPulse(bool on)
    {
        if (on == _pulsing) return;
        _pulsing = on;
        ScanDot.BeginAnimation(OpacityProperty, on
            ? new DoubleAnimation(1, 0.2, TimeSpan.FromSeconds(0.8)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever }
            : null);
        ScanDot.SetResourceReference(Shape.FillProperty, on ? "Accent" : "Pink");
    }

    private void SessionGrid_Loaded(object sender, RoutedEventArgs e)
    {
        _sessionGrid = sender as UniformGrid;
        UpdateColumns();
    }

    private void UpdateColumns()
    {
        if (_sessionGrid is null) return;
        _sessionGrid.Columns = ActualWidth >= 1060 ? 3 : ActualWidth >= 720 ? 2 : 1;
    }

    // ───────────────────────── Conectar ─────────────────────────

    /// <summary>Usado pela Visão geral: conecta direto no IP digitado lá.</summary>
    public void ConnectTo(string host)
    {
        HostBox.Text = host.Trim();
        _ = ConnectAsync(host);
    }

    private void Connect_Click(object sender, RoutedEventArgs e) => _ = ConnectAsync(HostBox.Text);

    private void HostBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        _ = ConnectAsync(HostBox.Text);
    }

    private void Join_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not SessionCard card) return;
        HostBox.Text = card.Address;
        _ = ConnectAsync(card.Address, card.Mode);
    }

    private async Task ConnectAsync(string text, StreamMode? expectedMode = null)
    {
        if (!RadminAddress.TryParseHost(text.Trim(), out var address))
        {
            ShowState("Digite um IP válido, por exemplo 26.12.34.56.", "Danger", canCancel: false);
            HostBox.Focus();
            return;
        }
        var host = address.ToString();
        if (_player is not null)
        {
            if (_currentHost == host)
            {
                _player.BringToFront();
                return;
            }
            if (!ConfirmDialog.Ask(Window.GetWindow(this), "Trocar de transmissão?", $"Você está assistindo {_currentHost}. Sair e entrar em {host}?", "Trocar"))
                return;
            await _player.ShutdownAsync();
        }
        if (_connecting) return;
        _connecting = true;
        ConnectButton.IsEnabled = false;
        try
        {
            Settings.RememberHost(host);
            AppServices.Settings.Save();
            RefreshRecent();
            ShowState($"Conectando em {host}…", "Warning", canCancel: false);

            var hardware = await AppServices.Hardware;
            var decoders = await Task.Run(HardwareDetector.SupportedDecoders);
            if (decoders.Length == 0 && expectedMode is not StreamMode.AudioOnly)
            {
                const string missing = "Nenhum decoder de vídeo foi encontrado: confira se a pasta 'ffmpeg' está junto do ScreenFlow.exe. Festas de música funcionam mesmo assim.";
                if (expectedMode is not null)
                {
                    ShowState(missing, "Danger", canCancel: false);
                    return;
                }
                _shell.Toast(missing, ToastKind.Warning);
            }

            var options = new ViewerOptions(host, Settings.ControlPort, Settings.DeviceName, null, (float)Settings.Volume, decoders,
                Math.Max(1280, hardware.MonitorWidth), Math.Max(720, hardware.MonitorHeight), Math.Max(60, hardware.MonitorRefreshHz));
            var session = new ViewerSession(options);
            var player = new PlayerWindow(session, host, expectedMode);
            session.PinProvider = wrong => Dispatcher.InvokeAsync(() => PinDialog.Ask(player.IsVisible ? player : Window.GetWindow(this), host, wrong)).Task;
            session.StateChanged += (state, message) => Dispatcher.BeginInvoke(() => OnStateChanged(player, state, message));
            session.StatisticsUpdated += stats => Dispatcher.BeginInvoke(() =>
            {
                if (_player == player) Telemetry.Record(stats, session.HostName.Length > 0 ? session.HostName : host);
            });
            player.Closed += (_, _) => OnPlayerClosed(player);

            _player = player;
            _currentHost = host;
            player.Show();
            session.Start();
            UpdateNowCard();
            RefreshSessions();
            _shell.NotifyLiveStateChanged();
        }
        catch (Exception exception)
        {
            Log.Error("Falha ao conectar", exception);
            ShowState(exception.Message, "Danger", canCancel: false);
        }
        finally
        {
            _connecting = false;
            ConnectButton.IsEnabled = true;
        }
    }

    private void OnStateChanged(PlayerWindow player, ConnectionState state, string message)
    {
        if (_player != player) return;
        switch (state)
        {
            case ConnectionState.Connected:
                ShowState(message, "Success", canCancel: true);
                _shell.NotifyLiveStateChanged();
                break;
            case ConnectionState.Error:
                ShowState(message, "Danger", canCancel: true);
                break;
            case ConnectionState.Closed:
                break;
            default:
                ShowState(message, "Warning", canCancel: true);
                break;
        }
        UpdateNowCard();
    }

    private void OnPlayerClosed(PlayerWindow player)
    {
        if (_player != player) return;
        _player = null;
        _currentHost = null;
        Telemetry.EndViewer();
        StatePanel.Visibility = Visibility.Collapsed;
        UpdateNowCard();
        RefreshSessions();
        _shell.NotifyLiveStateChanged();
    }

    private void ShowState(string message, string brush, bool canCancel)
    {
        StatePanel.Visibility = Visibility.Visible;
        StateText.Text = message;
        StateDot.SetResourceReference(Shape.FillProperty, brush);
        CancelButton.Visibility = canCancel && _player is not null ? Visibility.Visible : Visibility.Collapsed;
        CancelButton.Content = _player?.Session.State == ConnectionState.Connected ? "Sair" : "Cancelar";
    }

    private async void Cancel_Click(object sender, RoutedEventArgs e)
    {
        if (_player is { } player) await player.ShutdownAsync();
    }

    private void OpenPlayer_Click(object sender, RoutedEventArgs e) => _player?.BringToFront();

    public async Task ShutdownAsync()
    {
        if (_player is { } player) await player.ShutdownAsync();
        await StopDiscoveryAsync();
    }

    private void RefreshRecent()
    {
        RecentPanel.Children.Clear();
        foreach (var host in Settings.RecentHosts.Take(6))
        {
            var chip = new Button { Content = host, Style = (Style)FindResource("ChipButton"), FontFamily = (System.Windows.Media.FontFamily)FindResource("FontMono"), ToolTip = $"Conectar em {host}" };
            chip.Click += (_, _) =>
            {
                HostBox.Text = host;
                _ = ConnectAsync(host);
            };
            RecentPanel.Children.Add(chip);
        }
        RecentLabel.Visibility = RecentPanel.Children.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (HostBox.Text.Length == 0 && Settings.RecentHosts.Count > 0) HostBox.Text = Settings.RecentHosts[0];
    }

    // ───────────────────────── Assistindo agora ─────────────────────────

    private void UpdateNowCard()
    {
        var watching = _player is not null;
        NowCard.Visibility = watching ? Visibility.Visible : Visibility.Collapsed;
        TipsCard.Visibility = watching ? Visibility.Collapsed : Visibility.Visible;
        if (_player is null) return;

        var session = _player.Session;
        var mode = session.Config?.Mode ?? _player.ExpectedMode ?? StreamMode.Screen;
        var name = session.HostName.Length > 0 ? session.HostName : _currentHost ?? string.Empty;
        NowTitle.Text = name;
        (NowGlyph.Text, NowTile.Style) = mode switch
        {
            StreamMode.Camera => ("", (Style)FindResource("IconTileCyan")),
            StreamMode.AudioOnly => ("", (Style)FindResource("IconTilePink")),
            _ => ("", (Style)FindResource("IconTile")),
        };

        var stats = Telemetry.Viewer;
        var config = session.Config;
        NowDetail.Text = session.State switch
        {
            ConnectionState.Connected when mode == StreamMode.AudioOnly => $"Festa de música · {_currentHost}",
            ConnectionState.Connected when config is not null => $"{StreamModes.Label(mode)} · {config.Width} × {config.Height} · {Quality.CodecLabel(config.Codec)} · {_currentHost}",
            ConnectionState.Connected => $"{StreamModes.Label(mode)} · {_currentHost}",
            ConnectionState.WaitingApproval => "Esperando o anfitrião aprovar a entrada…",
            ConnectionState.Reconnecting => "A conexão caiu, reconectando…",
            ConnectionState.Error => "Não foi possível conectar. Veja o aviso no player.",
            _ => $"Conectando em {_currentHost}…",
        };

        if (mode == StreamMode.AudioOnly)
        {
            NowStat1Label.Text = "Buffer";
            NowStat1.Text = stats is null ? "—" : $"{stats.AudioBufferMs:0} ms";
            NowStat2Label.Text = "Áudio";
            NowStat2.Text = stats is null ? "—" : $"{stats.AudioKbps:0}k";
        }
        else
        {
            NowStat1Label.Text = "FPS";
            NowStat1.Text = stats is null ? "—" : $"{stats.Fps:0}";
            NowStat2Label.Text = "Mbps";
            NowStat2.Text = stats is null ? "—" : $"{stats.BitrateMbps:0.#}";
        }
        NowStat3.Text = stats is null ? "—" : $"{stats.RoundTripMs:0} ms";
    }

    /// <summary>Cartão de uma transmissão encontrada na rede.</summary>
    public sealed class SessionCard(string address) : INotifyPropertyChanged
    {
        private string _name = address;
        private StreamMode _mode;
        private int _viewers;
        private bool _pin;
        private bool _current;

        public string Address { get; } = address;
        public string Name { get => _name; private set => Set(ref _name, value, nameof(Name)); }
        public StreamMode Mode { get => _mode; private set => Set(ref _mode, value, nameof(Mode), nameof(IsScreen), nameof(IsCamera), nameof(IsParty), nameof(ModeLabel), nameof(ActionText)); }
        public bool PinRequired { get => _pin; private set => Set(ref _pin, value, nameof(PinRequired)); }
        public bool IsScreen => Mode == StreamMode.Screen;
        public bool IsCamera => Mode == StreamMode.Camera;
        public bool IsParty => Mode == StreamMode.AudioOnly;
        public string ModeLabel => StreamModes.Label(Mode);
        public string ViewersText => _viewers switch { 0 => "ninguém ainda", 1 => "1 assistindo", var n => $"{n} assistindo" };
        public string ActionText => _current ? "Abrir o player" : Mode == StreamMode.AudioOnly ? "Entrar na festa" : "Assistir";

        public void Update(DiscoveryAnnouncement announcement, bool current)
        {
            Name = announcement.HostName;
            Mode = announcement.Mode;
            PinRequired = announcement.PinRequired;
            Set(ref _viewers, announcement.Viewers, nameof(ViewersText));
            Set(ref _current, current, nameof(ActionText));
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        private void Set<T>(ref T field, T value, params string[] names)
        {
            if (EqualityComparer<T>.Default.Equals(field, value)) return;
            field = value;
            foreach (var name in names) PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }
}
