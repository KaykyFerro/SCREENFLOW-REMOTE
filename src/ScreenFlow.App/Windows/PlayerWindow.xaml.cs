using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ScreenFlow.App.Services;
using ScreenFlow.Core.Media;
using ScreenFlow.Infrastructure.Media;
using ScreenFlow.Infrastructure.Session;

namespace ScreenFlow.App.Windows;

/// <summary>
/// Player: mostra o vídeo decodificado, volume, tela cheia e overlay de estatísticas.
/// O frame mais recente é copiado na hora da composição (sem fila), para não acumular latência.
/// </summary>
public partial class PlayerWindow : Window
{
    private readonly DispatcherTimer _hideControls = new() { Interval = TimeSpan.FromSeconds(2.5) };
    private DecodedFrame? _pending;
    private WriteableBitmap? _bitmap;
    private bool _fullscreen;
    private WindowState _previousState;
    private double _volumeBeforeMute = 0.8;
    private int _framesShown;
    private DateTime _fpsWindow = DateTime.UtcNow;
    private double _displayFps;

    public PlayerWindow(ViewerSession session, string host)
    {
        Session = session;
        InitializeComponent();
        Title = $"ScreenFlow — {host}";
        VolumeSlider.Value = AppServices.Settings.Current.Volume;
        StatsPanel.Visibility = AppServices.Settings.Current.ShowStatsOverlay ? Visibility.Visible : Visibility.Collapsed;

        session.FrameReady += frame => Interlocked.Exchange(ref _pending, frame);
        session.StateChanged += (state, message) => Dispatcher.BeginInvoke(() => ShowState(state, message));
        session.StatisticsUpdated += stats => Dispatcher.BeginInvoke(() => ShowStatistics(stats));
        CompositionTarget.Rendering += OnRendering;
        _hideControls.Tick += (_, _) => HideControls();
        ShowState(ConnectionState.Connecting, $"Conectando em {host}…");
    }

    public ViewerSession Session { get; }

    private void OnRendering(object? sender, EventArgs e)
    {
        var frame = Interlocked.Exchange(ref _pending, null);
        if (frame is null) return;
        lock (frame)
        {
            if (_bitmap is null || _bitmap.PixelWidth != frame.Width || _bitmap.PixelHeight != frame.Height)
            {
                _bitmap = new WriteableBitmap(frame.Width, frame.Height, 96, 96, PixelFormats.Bgra32, null);
                VideoImage.Source = _bitmap;
            }
            _bitmap.WritePixels(new Int32Rect(0, 0, frame.Width, frame.Height), frame.Pixels, frame.Stride, 0);
        }
        _framesShown++;
        var now = DateTime.UtcNow;
        if ((now - _fpsWindow).TotalSeconds >= 1)
        {
            _displayFps = _framesShown / (now - _fpsWindow).TotalSeconds;
            _framesShown = 0;
            _fpsWindow = now;
        }
    }

    private void ShowState(ConnectionState state, string message)
    {
        var (title, icon, brush, visible) = state switch
        {
            ConnectionState.Connecting => ("Conectando…", "", "Accent", true),
            ConnectionState.WaitingApproval => ("Aguardando aprovação", "", "Accent", true),
            ConnectionState.Reconnecting => ("Reconectando…", "", "Warning", true),
            ConnectionState.Error => ("Não foi possível assistir", "", "Danger", true),
            ConnectionState.Closed => ("Desconectado", "", "TextMuted", true),
            _ => ("Conectado", "", "Success", false),
        };
        StateTitle.Text = title;
        StateMessage.Text = message;
        StateIcon.Text = icon;
        StateIcon.SetResourceReference(ForegroundProperty, brush);
        StateOverlay.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        StateClose.Visibility = state is ConnectionState.Error or ConnectionState.Closed ? Visibility.Visible : Visibility.Collapsed;
        if (state == ConnectionState.Connected) _hideControls.Start();
    }

    private void ShowStatistics(ViewerStatistics s)
    {
        StatsText.Text =
            $"FPS real      {s.Fps,6:0} (tela {_displayFps:0})\n" +
            $"Bitrate       {s.BitrateMbps,6:0.0} Mbps\n" +
            $"Latência      {(s.LatencyMs > 0 ? $"{s.LatencyMs,6:0} ms" : "     —")}\n" +
            $"Ping (RTT)    {s.RoundTripMs,6:0} ms\n" +
            $"Perda         {s.LossRate,6:P1}\n" +
            $"Codec         {s.Codec}\n" +
            $"Resolução     {s.Width}×{s.Height}\n" +
            $"Decoder       {s.Decoder}\n" +
            $"FEC recuperou {s.FecRecovered} pacotes · frames perdidos {s.FramesDropped}\n" +
            $"Qualidade     {s.QualityReason}";
    }

    // ───────────────────────── Controles ─────────────────────────

    private void Volume_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        Session.Volume = (float)e.NewValue;
        MuteButton.Content = e.NewValue <= 0.001 ? "" : "";
        AppServices.Settings.Current.Volume = e.NewValue;
    }

    private void Mute_Click(object sender, RoutedEventArgs e) => ToggleMute();

    private void ToggleMute()
    {
        if (VolumeSlider.Value > 0.001)
        {
            _volumeBeforeMute = VolumeSlider.Value;
            VolumeSlider.Value = 0;
        }
        else VolumeSlider.Value = _volumeBeforeMute;
    }

    private void Stats_Click(object sender, RoutedEventArgs e) => ToggleStats();

    private void ToggleStats()
    {
        var show = StatsPanel.Visibility != Visibility.Visible;
        StatsPanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        AppServices.Settings.Current.ShowStatsOverlay = show;
    }

    private void Fullscreen_Click(object sender, RoutedEventArgs e) => ToggleFullscreen();

    private void ToggleFullscreen()
    {
        if (!_fullscreen)
        {
            _previousState = WindowState;
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            WindowState = WindowState.Normal; // necessário para cobrir a barra de tarefas
            WindowState = WindowState.Maximized;
            FullscreenButton.Content = "";
        }
        else
        {
            WindowStyle = WindowStyle.SingleBorderWindow;
            ResizeMode = ResizeMode.CanResize;
            WindowState = _previousState;
            FullscreenButton.Content = "";
        }
        _fullscreen = !_fullscreen;
    }

    private void Root_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2) ToggleFullscreen();
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.F11: ToggleFullscreen(); break;
            case Key.Escape when _fullscreen: ToggleFullscreen(); break;
            case Key.F3: ToggleStats(); break;
            case Key.M: ToggleMute(); break;
            case Key.Up: VolumeSlider.Value = Math.Min(1, VolumeSlider.Value + 0.05); break;
            case Key.Down: VolumeSlider.Value = Math.Max(0, VolumeSlider.Value - 0.05); break;
            default: return;
        }
        e.Handled = true;
    }

    private void Window_MouseMove(object sender, MouseEventArgs e)
    {
        ControlBar.Visibility = Visibility.Visible;
        Cursor = null;
        _hideControls.Stop();
        if (Session.State == ConnectionState.Connected) _hideControls.Start();
    }

    private void HideControls()
    {
        _hideControls.Stop();
        if (ControlBar.IsMouseOver) return;
        ControlBar.Visibility = Visibility.Collapsed;
        if (_fullscreen) Cursor = Cursors.None;
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        CompositionTarget.Rendering -= OnRendering;
        _hideControls.Stop();
        AppServices.Settings.Save();
    }
}
