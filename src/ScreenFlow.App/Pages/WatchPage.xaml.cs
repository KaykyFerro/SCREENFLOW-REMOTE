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

/// <summary>
/// Um pedido para assistir: direto num PC (<see cref="StreamId"/> null) ou, pelo servidor ScreenFlow (1.4.1), a
/// transmissão de alguém nele (o id da pessoa na sala) com a senha do servidor.
/// </summary>
public sealed record WatchRequest(string Host, int Port, string? StreamId = null, string? ServerPassword = null, string? Name = null, StreamMode? Mode = null)
{
    public bool ViaServer => StreamId is not null;

    /// <summary>Identifica a transmissão (o mesmo PC pode ter várias pelo servidor).</summary>
    public string Key => StreamId is null ? $"{Host}:{Port}" : $"{Host}:{Port}/{StreamId}";

    /// <summary>Como aparece nos textos: o nome de quem transmite (pelo servidor) ou o endereço.</summary>
    public string Label => Name is { Length: > 0 } name ? name : Host;
}

/// <summary>Assistir: transmissões encontradas na rede (e no servidor), conexão pelo endereço e o resumo do que você está assistindo.</summary>
public partial class WatchPage : UserControl, IShellPage
{
    private readonly MainWindow _shell;
    private readonly DispatcherTimer _refresh = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly ObservableCollection<SessionCard> _sessions = [];
    private DiscoveryListener? _discovery;
    private PlayerWindow? _player;
    private UniformGrid? _sessionGrid;
    private string? _currentHost;
    private string? _currentKey;
    private bool _connecting;
    private bool _pulsing;

    public WatchPage(MainWindow shell)
    {
        _shell = shell;
        InitializeComponent();
        SessionList.ItemsSource = _sessions;
        _refresh.Tick += (_, _) => RefreshSessions();
        AppServices.Room.HostsChanged += () =>
        {
            if (IsLoaded) RefreshSessions();
        };
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

    /// <summary>
    /// A sala da rede já escuta a porta de descoberta e guarda os anúncios das transmissões. Só quando ela não abriu
    /// (porta ocupada) é que esta tela tenta escutar sozinha, como nas versões 1.0.
    /// </summary>
    private void StartDiscovery()
    {
        if (_discovery is null && !AppServices.DemoMode && !AppServices.Room.IsAvailable)
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
            : (AppServices.Room.IsAvailable ? AppServices.Room.Hosts : _discovery?.Hosts ?? []).Select(h => h.Announcement).ToList();
        foreach (var gone in _sessions.Where(s => found.All(f => f.Key != s.Key)).ToList()) _sessions.Remove(gone);
        foreach (var announcement in found)
        {
            var card = _sessions.FirstOrDefault(s => s.Key == announcement.Key);
            if (card is null)
            {
                card = new SessionCard(announcement.Key, announcement.Address);
                var index = 0;
                while (index < _sessions.Count && string.Compare(_sessions[index].Name, announcement.HostName, StringComparison.CurrentCultureIgnoreCase) < 0) index++;
                _sessions.Insert(index, card);
            }
            card.Update(announcement, _player is not null && _currentKey == RequestFor(announcement).Key);
        }

        var any = _sessions.Count > 0;
        SessionList.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
        NoSessions.Visibility = any ? Visibility.Collapsed : Visibility.Visible;
        ScanText.Text = _sessions.Count switch
        {
            0 => _discovery is null && !AppServices.Room.IsAvailable && !AppServices.DemoMode ? "Busca parada" : "Procurando…",
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

    /// <summary>Usado pela Visão geral e pelo chat da rede: conecta direto num PC (IP, ou nome do Tailscale).</summary>
    public void ConnectTo(string host, StreamMode? expectedMode = null, int? port = null)
    {
        HostBox.Text = host.Trim();
        _ = ConnectTextAsync(host, expectedMode, port);
    }

    /// <summary>(1.4.1) A transmissão de alguém pelo servidor ScreenFlow em uso.</summary>
    public void ConnectToServer(Services.ServerTarget server, Guid person, string name, StreamMode? expectedMode = null)
        => _ = ConnectAsync(new WatchRequest(server.Address.ToString(), server.ControlPort, person.ToString("N"), server.Password, name, expectedMode));

    private void Connect_Click(object sender, RoutedEventArgs e) => _ = ConnectTextAsync(HostBox.Text);

    private void HostBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        _ = ConnectTextAsync(HostBox.Text);
    }

    private void Join_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not SessionCard { Announcement: { } announcement } card) return;
        var request = RequestFor(announcement);
        if (!request.ViaServer) HostBox.Text = card.Address;
        _ = ConnectAsync(request);
    }

    /// <summary>
    /// Como conectar numa transmissão anunciada: as do servidor vão pelo servidor (o endereço já é o dele, e a senha é a
    /// do servidor em uso); as outras, direto no PC de quem transmite.
    /// </summary>
    private WatchRequest RequestFor(DiscoveryAnnouncement announcement)
        => announcement.Stream is { Length: > 0 } stream
            ? new WatchRequest(announcement.Address, announcement.ControlPort, stream, AppServices.Server.Target?.Password, announcement.HostName, announcement.Mode)
            : new WatchRequest(announcement.Address, Settings.ControlPort, Mode: announcement.Mode);

    /// <summary>O que foi digitado: um IP ou o nome de um PC (Tailscale), com ":porta" se não for a de sempre.</summary>
    private Task ConnectTextAsync(string text, StreamMode? expectedMode = null, int? port = null)
    {
        if (!LocalNetworks.TrySplitHostPort(text, port ?? Settings.ControlPort, out var host, out var parsedPort))
        {
            ShowState("Digite o IP de quem transmite (por exemplo 26.12.34.56 ou 100.64.1.2) ou o nome do PC no Tailscale.", "Danger", canCancel: false);
            HostBox.Focus();
            return Task.CompletedTask;
        }
        return ConnectAsync(new WatchRequest(host, parsedPort, Mode: expectedMode));
    }

    private async Task ConnectAsync(WatchRequest request)
    {
        var host = request.Host;
        var expectedMode = request.Mode;
        if (_player is not null)
        {
            if (_currentKey == request.Key)
            {
                _player.BringToFront();
                return;
            }
            if (!ConfirmDialog.Ask(Window.GetWindow(this), "Trocar de transmissão?", $"Você está assistindo {_currentHost}. Sair e entrar em {request.Label}?", "Trocar"))
                return;
            await _player.ShutdownAsync();
        }
        if (_connecting) return;
        _connecting = true;
        ConnectButton.IsEnabled = false;
        try
        {
            if (!request.ViaServer)
            {
                Settings.RememberHost(request.Port == Settings.ControlPort ? host : $"{host}:{request.Port}");
                AppServices.Settings.Save();
                RefreshRecent();
            }
            ShowState(request.ViaServer ? $"Conectando em {request.Label} pelo servidor…" : $"Conectando em {host}…", "Warning", canCancel: false);

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

            var options = new ViewerOptions(host, request.Port, Settings.DeviceName, request.ServerPassword, (float)Settings.Volume, decoders,
                Math.Max(1280, hardware.MonitorWidth), Math.Max(720, hardware.MonitorHeight), Math.Max(60, hardware.MonitorRefreshHz),
                StreamId: request.StreamId, ViaServer: request.ViaServer);
            var session = new ViewerSession(options);
            var player = new PlayerWindow(session, request.Label, expectedMode);
            session.PinProvider = request.ViaServer
                ? wrong => Dispatcher.InvokeAsync(() => AskServerPassword(player, wrong)).Task
                : wrong => Dispatcher.InvokeAsync(() => PinDialog.Ask(player.IsVisible ? player : Window.GetWindow(this), host, wrong)).Task;
            session.StateChanged += (state, message) => Dispatcher.BeginInvoke(() => OnStateChanged(player, state, message));
            session.StatisticsUpdated += stats => Dispatcher.BeginInvoke(() =>
            {
                if (_player == player) Telemetry.Record(stats, session.HostName.Length > 0 ? session.HostName : host);
            });
            player.Closed += (_, _) => OnPlayerClosed(player);

            _player = player;
            _currentHost = request.Label;
            _currentKey = request.Key;
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

    /// <summary>A senha do servidor (pedida quando faltou ou estava errada). Fica salva para as próximas vezes.</summary>
    private string? AskServerPassword(PlayerWindow player, bool wrong)
    {
        var target = AppServices.Server.Target;
        var password = PinDialog.Ask(player.IsVisible ? player : Window.GetWindow(this), target?.Label ?? "ScreenFlow", wrong, server: true);
        if (password is not null && target is { ThisPc: false } && Settings.ServerRole == ServerRole.Join && Settings.ServerPassword != password)
        {
            Settings.ServerPassword = password;
            AppServices.Settings.Save();
        }
        return password;
    }

    private void OnPlayerClosed(PlayerWindow player)
    {
        if (_player != player) return;
        _player = null;
        _currentHost = null;
        _currentKey = null;
        Telemetry.EndViewer();
        StatePanel.Visibility = Visibility.Collapsed;
        UpdateNowCard();
        RefreshSessions();
        _shell.NotifyLiveStateChanged();
        _shell.ShowAfterSessionEnded(this);
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
                _ = ConnectTextAsync(host);
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
            ConnectionState.Connected when config is not null => $"{StreamModes.Label(mode, config.CameraOverlay)} · {config.Width} × {config.Height} · {Quality.CodecLabel(config.Codec)} · {_currentHost}",
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

    /// <summary>Cartão de uma transmissão encontrada na rede (ou no servidor).</summary>
    public sealed class SessionCard(string key, string address) : INotifyPropertyChanged
    {
        private string _name = address;
        private bool _viaServer;
        private StreamMode _mode;
        private int _viewers;
        private bool _pin;
        private bool _camera;
        private bool _current;

        public string Key { get; } = key;
        public string Address { get; } = address;
        public DiscoveryAnnouncement? Announcement { get; private set; }
        /// <summary>(1.4.1) A transmissão está no servidor ScreenFlow.</summary>
        public bool ViaServer { get => _viaServer; private set => Set(ref _viaServer, value, nameof(ViaServer), nameof(PlaceText), nameof(PinLabel)); }
        /// <summary>Onde está: o servidor (pelo nome dele) ou o endereço do PC.</summary>
        public string PlaceText => _viaServer ? $"pelo servidor {Announcement?.Via ?? Address}" : Address;
        /// <summary>O que protege a transmissão: a senha do servidor ou o PIN de quem transmite.</summary>
        public string PinLabel => _viaServer ? "Senha" : "PIN";
        public string Name { get => _name; private set => Set(ref _name, value, nameof(Name)); }
        public StreamMode Mode { get => _mode; private set => Set(ref _mode, value, nameof(Mode), nameof(IsScreen), nameof(IsCamera), nameof(IsParty), nameof(ModeLabel), nameof(ActionText)); }
        public bool PinRequired { get => _pin; private set => Set(ref _pin, value, nameof(PinRequired)); }
        public bool IsScreen => Mode == StreamMode.Screen;
        public bool IsCamera => Mode == StreamMode.Camera;
        public bool IsParty => Mode == StreamMode.AudioOnly;
        public string ModeLabel => StreamModes.Label(Mode, _camera);
        public string ViewersText => _viewers switch { 0 => "ninguém ainda", 1 => "1 assistindo", var n => $"{n} assistindo" };
        public string ActionText => _current ? "Abrir o player" : Mode == StreamMode.AudioOnly ? "Entrar na festa" : "Assistir";

        public void Update(DiscoveryAnnouncement announcement, bool current)
        {
            Announcement = announcement;
            ViaServer = announcement.Stream is { Length: > 0 };
            Name = announcement.HostName;
            Mode = announcement.Mode;
            Set(ref _camera, announcement.CameraOverlay, nameof(ModeLabel));
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
