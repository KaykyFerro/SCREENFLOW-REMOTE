using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ScreenFlow.App.Controls;
using ScreenFlow.App.Services;
using ScreenFlow.Core.Diagnostics;
using ScreenFlow.Core.Media;
using ScreenFlow.Core.Protocol;
using ScreenFlow.Infrastructure.Media;
using ScreenFlow.Infrastructure.Rendering;
using ScreenFlow.Infrastructure.Session;

namespace ScreenFlow.App.Windows;

/// <summary>
/// Player: o vídeo é desenhado direto pela GPU (swapchain D3D11 numa janela nativa) ou, como alternativa, copiado pela
/// CPU. Na festa de música mostra o visualizador. Os controles somem com o mouse parado; com o vídeo na GPU eles ficam
/// em janelas sobrepostas, porque o WPF não desenha por cima de uma janela nativa. Quando a câmera vem junto com a
/// tela, ela aparece num quadrinho num canto (clique muda de canto, C esconde).
/// </summary>
public partial class PlayerWindow : Window
{
    private static readonly Uri DarkTheme = new("/Themes/Dark.xaml", UriKind.Relative);
    private readonly DispatcherTimer _hideControls = new() { Interval = TimeSpan.FromSeconds(2.5) };
    private readonly DispatcherTimer _cameraWatch = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly string _host;
    private VideoSurface? _surface;
    private D3D11VideoPresenter? _presenter;
    private OverlayWindow? _controlsOverlay;
    private OverlayWindow? _statsOverlay;
    private OverlayWindow? _cameraOverlay;
    private DecodedFrame? _pending;
    private DecodedFrame? _pendingCamera;
    private WriteableBitmap? _bitmap;
    private WriteableBitmap? _cameraBitmap;
    private DateTime _lastCameraFrame = DateTime.MinValue;
    private bool _cameraExpected;
    private bool _cameraHidden;
    private bool _cameraDemo;
    private int _cameraCorner;
    private StreamMode _mode;
    private bool _gpuPath;
    private bool _demo;
    private bool _fullscreen;
    private WindowState _previousState;
    private double _volumeBeforeMute = 0.8;
    private int _framesShown;
    private DateTime _fpsWindow = DateTime.UtcNow;
    private double _displayFps;
    private bool _controlsShown = true;
    private bool _statsShown;
    private bool _shuttingDown;
    private bool _cleaned;

    public PlayerWindow(ViewerSession session, string host, StreamMode? expectedMode = null)
    {
        Session = session;
        _host = host;
        ExpectedMode = expectedMode;
        _mode = expectedMode ?? StreamMode.Screen;
        InitializeComponent();
        Title = $"{host} · ScreenFlow";
        BarTitle.Text = host;
        PartyHost.Text = host;

        var settings = AppServices.Settings.Current;
        VolumeSlider.Value = settings.Volume;
        if (settings.Volume > 0.001) _volumeBeforeMute = settings.Volume;
        _statsShown = settings.ShowStatsOverlay;
        _cameraCorner = settings.PlayerCameraCorner;
        PartySpectrum.Source = () => Session.AudioMeter;

        session.FrameReady += frame => Interlocked.Exchange(ref _pending, frame);
        session.CameraFrameReady += frame => Interlocked.Exchange(ref _pendingCamera, frame);
        session.StateChanged += (state, message) => Dispatcher.BeginInvoke(() => ShowState(state, message));
        session.StatisticsUpdated += stats => Dispatcher.BeginInvoke(() => ShowStatistics(stats));
        session.ConfigChanged += config => Dispatcher.BeginInvoke(() => ApplyConfig(config));
        session.RenderPathChanged += gpu => Dispatcher.BeginInvoke(() =>
        {
            _gpuPath = gpu;
            UpdateLayers();
        });

        CompositionTarget.Rendering += OnRendering;
        _hideControls.Tick += (_, _) => HideControls();
        _cameraWatch.Tick += (_, _) => UpdateCameraPip();
        SourceInitialized += (_, _) => ThemeManager.ApplyFrame(this);
        Loaded += (_, _) => OnLoaded();
        LocationChanged += (_, _) => RepositionOverlays();
        SizeChanged += (_, _) =>
        {
            ResizeCameraPip();
            RepositionOverlays();
        };
        StateChanged += (_, _) => RepositionOverlays();
        ShowState(ConnectionState.Connecting, $"Conectando em {host}…");
        UpdateLayers();
        UpdateStatsVisibility();
        PlaceCameraPip();
    }

    public ViewerSession Session { get; }

    /// <summary>O modo anunciado na rede (antes de conectar). O modo real chega com a configuração do host.</summary>
    public StreamMode? ExpectedMode { get; }

    // ───────────────────────── Vídeo ─────────────────────────

    private void OnLoaded()
    {
        if (_demo || AppServices.DemoMode) return;
        if (AppServices.Settings.Current.GpuVideoRenderer) CreateSurface();
    }

    /// <summary>Cria a janela nativa do vídeo; o swapchain é criado assim que ela existir.</summary>
    private void CreateSurface()
    {
        try
        {
            var surface = new VideoSurface();
            surface.HandleCreated += () => CreatePresenter(surface);
            surface.PixelSizeChanged += (width, height) => _presenter?.Resize(width, height);
            surface.MouseMoved += () => Dispatcher.BeginInvoke(ShowControls);
            surface.DoubleClicked += () => Dispatcher.BeginInvoke(ToggleFullscreen);
            _surface = surface;
            SurfaceHost.Child = surface;
            // Se a janela nativa ainda não existir depois do layout, mostra a área por um instante para forçar a criação.
            Dispatcher.BeginInvoke(() =>
            {
                if (surface.SurfaceHandle == 0 && !_shuttingDown) SurfaceHost.Visibility = Visibility.Visible;
            }, DispatcherPriority.Loaded);
        }
        catch (Exception exception)
        {
            Log.Warn($"Superfície de vídeo indisponível ({exception.Message}). Usando a CPU.");
            _surface = null;
            SurfaceHost.Child = null;
        }
    }

    private void CreatePresenter(VideoSurface surface)
    {
        if (_shuttingDown || surface.SurfaceHandle == 0 || _presenter is not null) return;
        try
        {
            var scale = VisualTreeHelper.GetDpi(this);
            var width = (int)Math.Max(1, Root.ActualWidth * scale.DpiScaleX);
            var height = (int)Math.Max(1, Root.ActualHeight * scale.DpiScaleY);
            _presenter = new D3D11VideoPresenter(surface.SurfaceHandle, width, height);
            Session.Presenter = _presenter;
            // O WPF não desenha por cima da janela nativa: controles e estatísticas vão para janelas sobrepostas.
            _controlsOverlay = MoveToOverlay(ControlBar, HorizontalAlignment.Center, VerticalAlignment.Bottom, new Thickness(0, 0, 0, 26));
            _statsOverlay = MoveToOverlay(StatsPanel, HorizontalAlignment.Left, VerticalAlignment.Top, new Thickness(18));
            UpdateStatsVisibility();
            UpdateLayers();
            UpdateCameraPip();
        }
        catch (Exception exception)
        {
            Log.Warn($"Player na GPU indisponível ({exception.Message}). O vídeo vai pela CPU.");
            _presenter?.Dispose();
            _presenter = null;
            Session.Presenter = null;
            SurfaceHost.Child = null;
            _surface = null;
        }
    }

    private OverlayWindow MoveToOverlay(FrameworkElement element, HorizontalAlignment horizontal, VerticalAlignment vertical, Thickness margin)
    {
        Root.Children.Remove(element);
        element.Margin = new Thickness(0);
        element.HorizontalAlignment = HorizontalAlignment.Left;
        element.VerticalAlignment = VerticalAlignment.Top;
        element.Visibility = Visibility.Visible;
        var overlay = new OverlayWindow(this, element, horizontal, vertical, margin, new ResourceDictionary { Source = DarkTheme });
        overlay.Show();
        overlay.Reposition();
        return overlay;
    }

    private void RepositionOverlays()
    {
        _controlsOverlay?.Reposition();
        _statsOverlay?.Reposition();
        _cameraOverlay?.Reposition();
    }

    /// <summary>O que aparece: vídeo pela GPU, vídeo pela CPU ou a festa de música.</summary>
    private void UpdateLayers()
    {
        var connected = _demo || Session.State == ConnectionState.Connected;
        var party = _mode == StreamMode.AudioOnly;
        var gpu = _presenter is { Failed: false } && _gpuPath;
        SurfaceHost.Visibility = connected && !party && gpu ? Visibility.Visible : Visibility.Collapsed;
        VideoImage.Visibility = connected && !party && !gpu ? Visibility.Visible : Visibility.Collapsed;
        PartyView.Visibility = party ? Visibility.Visible : Visibility.Collapsed;
        if (_surface is not null) _surface.SkipErase = gpu;
        UpdateCameraPip();
    }

    private void ApplyConfig(StreamConfigMessage config)
    {
        _mode = config.VideoEnabled ? config.Mode : StreamMode.AudioOnly;
        _cameraExpected = config.VideoEnabled && config.CameraOverlay;
        if (Session.HostName.Length > 0)
        {
            Title = $"{Session.HostName} · ScreenFlow";
            BarTitle.Text = PartyHost.Text = Session.HostName;
            CameraLabel.Text = $"Câmera · {Session.HostName}";
        }
        if (_cameraExpected) _cameraWatch.Start();
        else _cameraWatch.Stop();
        UpdateLayers();
    }

    /// <summary>Caminho pela CPU: copia o frame mais recente na hora da composição (sem fila, sem latência acumulada).</summary>
    private void OnRendering(object? sender, EventArgs e)
    {
        ShowCameraFrame();
        var frame = Interlocked.Exchange(ref _pending, null);
        if (frame is null) return;
        if (_bitmap is null || _bitmap.PixelWidth != frame.Width || _bitmap.PixelHeight != frame.Height)
        {
            _bitmap = new WriteableBitmap(frame.Width, frame.Height, 96, 96, PixelFormats.Bgra32, null);
            VideoImage.Source = _bitmap;
        }
        // O lock segura o buffer enquanto ele é copiado (o decoder reaproveita três buffers).
        lock (frame) _bitmap.WritePixels(new Int32Rect(0, 0, frame.Width, frame.Height), frame.Pixels, frame.Stride, 0);
        _framesShown++;
        var now = DateTime.UtcNow;
        if ((now - _fpsWindow).TotalSeconds >= 1)
        {
            _displayFps = _framesShown / (now - _fpsWindow).TotalSeconds;
            _framesShown = 0;
            _fpsWindow = now;
        }
    }

    // ───────────────────────── Câmera junto com a tela ─────────────────────────

    /// <summary>Copia o frame mais novo da câmera para o quadrinho (sempre pela CPU: é pequeno).</summary>
    private void ShowCameraFrame()
    {
        var frame = Interlocked.Exchange(ref _pendingCamera, null);
        if (frame is null) return;
        lock (frame)
        {
            if (_cameraBitmap is null || _cameraBitmap.PixelWidth != frame.Width || _cameraBitmap.PixelHeight != frame.Height)
            {
                _cameraBitmap = new WriteableBitmap(frame.Width, frame.Height, 96, 96, PixelFormats.Bgra32, null);
                CameraBrush.ImageSource = _cameraBitmap;
                ResizeCameraPip();
            }
            _cameraBitmap.WritePixels(new Int32Rect(0, 0, frame.Width, frame.Height), frame.Pixels, frame.Stride, 0);
        }
        var wasStale = DateTime.UtcNow - _lastCameraFrame > CameraTimeout;
        _lastCameraFrame = DateTime.UtcNow;
        if (wasStale) UpdateCameraPip();
    }

    private static readonly TimeSpan CameraTimeout = TimeSpan.FromSeconds(3);

    /// <summary>O quadrinho aparece quando a câmera está chegando e a pessoa não escondeu.</summary>
    private void UpdateCameraPip()
    {
        var connected = _demo || Session.State == ConnectionState.Connected;
        var arriving = _cameraDemo || DateTime.UtcNow - _lastCameraFrame < CameraTimeout;
        var show = _cameraExpected && !_cameraHidden && connected && _mode == StreamMode.Screen && arriving && !_shuttingDown;
        if (show && _cameraOverlay is null && _presenter is not null && WindowState != WindowState.Minimized)
        {
            // Com o vídeo na GPU o quadrinho precisa de uma janela sobreposta; ela só nasce quando a câmera chega.
            var (horizontal, vertical, margin) = CameraPlacement;
            _cameraOverlay = MoveToOverlay(CameraPip, horizontal, vertical, margin);
        }
        if (_cameraOverlay is not null) _cameraOverlay.Visibility = show ? Visibility.Visible : Visibility.Hidden;
        else CameraPip.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        CameraButton.Visibility = _cameraExpected && _mode == StreamMode.Screen ? Visibility.Visible : Visibility.Collapsed;
        CameraButton.SetResourceReference(ForegroundProperty, _cameraHidden ? "Text" : "AccentText");
        CameraButton.ToolTip = _cameraHidden ? "Mostrar a câmera (C)" : arriving ? "Esconder a câmera (C)" : "A câmera de quem transmite ainda não chegou (C esconde)";
    }

    /// <summary>Largura de uns 22% do player (entre 200 e 440), na proporção da câmera.</summary>
    private void ResizeCameraPip()
    {
        var width = Math.Round(Math.Clamp(ActualWidth * 0.22, 200, 440));
        var aspect = _cameraBitmap is { PixelWidth: > 0 } bitmap ? bitmap.PixelHeight / (double)bitmap.PixelWidth
            : CameraBrush.ImageSource is BitmapSource { PixelWidth: > 0 } source ? source.PixelHeight / (double)source.PixelWidth
            : 9 / 16.0;
        CameraPip.Width = width;
        CameraPip.Height = Math.Round(width * Math.Clamp(aspect, 0.4, 1.4));
    }

    /// <summary>Em cima à direita (longe da barra e das estatísticas), embaixo à direita ou embaixo à esquerda.</summary>
    private (HorizontalAlignment Horizontal, VerticalAlignment Vertical, Thickness Margin) CameraPlacement => _cameraCorner switch
    {
        1 => (HorizontalAlignment.Right, VerticalAlignment.Bottom, new Thickness(0, 0, 24, 96)),
        2 => (HorizontalAlignment.Left, VerticalAlignment.Bottom, new Thickness(24, 0, 0, 96)),
        _ => (HorizontalAlignment.Right, VerticalAlignment.Top, new Thickness(0, 24, 24, 0)),
    };

    private void PlaceCameraPip()
    {
        var (horizontal, vertical, margin) = CameraPlacement;
        if (_cameraOverlay is not null)
        {
            _cameraOverlay.SetPlacement(horizontal, vertical, margin);
            return;
        }
        CameraPip.HorizontalAlignment = horizontal;
        CameraPip.VerticalAlignment = vertical;
        CameraPip.Margin = margin;
    }

    private void CameraPip_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        _cameraCorner = (_cameraCorner + 1) % 3;
        AppServices.Settings.Current.PlayerCameraCorner = _cameraCorner;
        PlaceCameraPip();
        e.Handled = true;
    }

    private void Camera_Click(object sender, RoutedEventArgs e) => ToggleCamera();

    private void ToggleCamera()
    {
        if (!_cameraExpected) return;
        _cameraHidden = !_cameraHidden;
        UpdateCameraPip();
    }

    // ───────────────────────── Estado e estatísticas ─────────────────────────

    private void ShowState(ConnectionState state, string message)
    {
        var (title, glyph, background, foreground, busy) = state switch
        {
            ConnectionState.Connecting => ("Conectando…", "", "AccentSoft", "AccentText", true),
            ConnectionState.WaitingApproval => ("Esperando aprovação", "", "VioletSoft", "Violet", true),
            ConnectionState.Reconnecting => ("Reconectando…", "", "AmberSoft", "Amber", true),
            ConnectionState.Error => ("Não deu para assistir", "", "DangerSoft", "Danger", false),
            ConnectionState.Closed => ("A transmissão terminou", "", "SurfaceRaised", "TextMuted", false),
            _ => ("Conectado", "", "AccentSoft", "AccentText", false),
        };
        StateTitle.Text = title;
        StateMessage.Text = message;
        StateIcon.Text = glyph;
        StateTile.SetResourceReference(System.Windows.Controls.Border.BackgroundProperty, background);
        StateTile.SetResourceReference(System.Windows.Controls.Border.BorderBrushProperty, background);
        StateIcon.SetResourceReference(ForegroundProperty, foreground);
        StateProgress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        StateProgress.IsIndeterminate = busy;
        StateButtons.Visibility = state is ConnectionState.Error or ConnectionState.Closed ? Visibility.Visible : Visibility.Collapsed;
        StateOverlay.Visibility = state == ConnectionState.Connected ? Visibility.Collapsed : Visibility.Visible;
        UpdateLayers();
        if (state == ConnectionState.Connected) _hideControls.Start();
        else ShowControls();
    }

    private void ShowStatistics(ViewerStatistics s)
    {
        if (s.Mode == StreamMode.AudioOnly || s.Width == 0)
        {
            PartyQuality.Text = s.AudioKbps > 0 ? $"{s.AudioKbps / 2:0} kbps" : "—";
            PartyBuffer.Text = $"{s.AudioBufferMs:0} ms";
            PartyPing.Text = $"{s.RoundTripMs:0} ms";
            PartyRecovered.Text = s.AudioRecovered.ToString("N0");
            StatsText.Text =
                $"Modo          festa de música (só áudio)\n" +
                $"Recebendo     {s.AudioKbps,6:0} kbps (com cópia de segurança)\n" +
                $"Buffer        {s.AudioBufferMs,6:0} ms\n" +
                $"Ping (RTT)    {s.RoundTripMs,6:0} ms\n" +
                $"Perda         {s.LossRate,6:P1}\n" +
                $"Recuperados   {s.AudioRecovered,6:N0} pacotes";
            return;
        }
        var path = s.GpuRendering ? "GPU (D3D11)" : $"CPU · tela {_displayFps:0} FPS";
        StatsText.Text =
            $"FPS           {s.Fps,6:0}\n" +
            $"Bitrate       {s.BitrateMbps,6:0.0} Mbps\n" +
            $"Latência      {(s.LatencyMs > 0 ? $"{s.LatencyMs,6:0} ms" : "     —")}\n" +
            $"Ping (RTT)    {s.RoundTripMs,6:0} ms\n" +
            $"Perda         {s.LossRate,6:P1} agora · {s.TotalLossRate:P2} desde que entrou\n" +
            $"Codec         {s.Codec} · {s.Width}×{s.Height}\n" +
            $"Decoder       {s.Decoder}\n" +
            $"Desenho       {path}\n" +
            $"Áudio         buffer {s.AudioBufferMs:0} ms\n" +
            (s.CameraFps > 0 ? $"Câmera        {s.CameraWidth}×{s.CameraHeight} · {s.CameraFps:0} FPS\n" : _cameraExpected ? "Câmera        esperando a câmera de quem transmite\n" : string.Empty) +
            $"Recuperados   {s.FecRecovered:N0} pelo FEC · {s.NackRecovered:N0} reenviados\n" +
            $"Perdidos      {LostFrames(s)}\n" +
            $"Qualidade     {s.QualityReason}";
    }

    /// <summary>"0 frames", ou "12 frames (9 incompletos · 3 não chegaram)", e o que o decoder deixou para trás.</summary>
    private static string LostFrames(ViewerStatistics s)
    {
        var parts = new List<string>(3);
        if (s.FramesExpired > 0) parts.Add($"{s.FramesExpired:N0} incompletos");
        if (s.FramesMissing > 0) parts.Add($"{s.FramesMissing:N0} não chegaram");
        if (s.FramesSuperseded > 0) parts.Add($"{s.FramesSuperseded:N0} pulados por um keyframe");
        var text = $"{s.FramesDropped:N0} frames" + (parts.Count > 0 && parts.Count < 4 && s.FramesDropped > 0 ? $" ({string.Join(" · ", parts)})" : string.Empty);
        if (s.DecoderLate > 0) text += $" · decoder atrasou {s.DecoderLate:N0}";
        return text;
    }

    // ───────────────────────── Controles ─────────────────────────

    private void Volume_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        Session.Volume = (float)e.NewValue;
        MuteButton.Content = e.NewValue <= 0.001 ? "" : e.NewValue < 0.34 ? "" : e.NewValue < 0.67 ? "" : "";
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
        _statsShown = !_statsShown;
        AppServices.Settings.Current.ShowStatsOverlay = _statsShown;
        UpdateStatsVisibility();
    }

    private void UpdateStatsVisibility()
    {
        StatsButton.SetResourceReference(ForegroundProperty, _statsShown ? "AccentText" : "Text");
        if (_statsOverlay is not null) _statsOverlay.Visibility = _statsShown ? Visibility.Visible : Visibility.Hidden;
        else StatsPanel.Visibility = _statsShown ? Visibility.Visible : Visibility.Collapsed;
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
            SetCursorHidden(false);
        }
        _fullscreen = !_fullscreen;
        RepositionOverlays();
    }

    /// <summary>Traz o player para a frente (e tira do minimizado).</summary>
    public void BringToFront()
    {
        if (WindowState == WindowState.Minimized) WindowState = _fullscreen ? WindowState.Maximized : WindowState.Normal;
        Show();
        Activate();
    }

    private void Root_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2) ToggleFullscreen();
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.F11:
            case Key.F:
                ToggleFullscreen();
                break;
            case Key.Escape when _fullscreen: ToggleFullscreen(); break;
            case Key.F3:
            case Key.I:
                ToggleStats();
                break;
            case Key.M: ToggleMute(); break;
            case Key.C when _cameraExpected: ToggleCamera(); break;
            case Key.Up: VolumeSlider.Value = Math.Min(1, VolumeSlider.Value + 0.05); break;
            case Key.Down: VolumeSlider.Value = Math.Max(0, VolumeSlider.Value - 0.05); break;
            default: return;
        }
        ShowControls();
        e.Handled = true;
    }

    private void Window_MouseMove(object sender, MouseEventArgs e) => ShowControls();

    private void ShowControls()
    {
        SetCursorHidden(false);
        if (!_controlsShown)
        {
            _controlsShown = true;
            ControlBar.IsHitTestVisible = true;
            ControlBar.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(160)));
        }
        _hideControls.Stop();
        if (_demo || Session.State == ConnectionState.Connected) _hideControls.Start();
    }

    private void HideControls()
    {
        _hideControls.Stop();
        if (ControlBar.IsMouseOver || Session.State != ConnectionState.Connected) return;
        _controlsShown = false;
        ControlBar.IsHitTestVisible = false;
        ControlBar.BeginAnimation(OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(260)));
        if (_fullscreen) SetCursorHidden(true);
    }

    private void SetCursorHidden(bool hidden)
    {
        Cursor = hidden ? Cursors.None : null;
        if (_surface is not null) _surface.HideCursor = hidden;
    }

    // ───────────────────────── Fechar ─────────────────────────

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    /// <summary>Fecha o player e espera a sessão, o áudio e o decoder pararem.</summary>
    public Task ShutdownAsync()
    {
        if (!_cleaned && !_shuttingDown)
        {
            if (IsLoaded || IsVisible) Close();
            else _ = CleanupAsync();
        }
        return _closed.Task;
    }

    private async void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_cleaned) return;
        e.Cancel = true;
        if (_shuttingDown) return;
        await CleanupAsync();
        Close();
    }

    private async Task CleanupAsync()
    {
        _shuttingDown = true;
        CompositionTarget.Rendering -= OnRendering;
        _hideControls.Stop();
        _cameraWatch.Stop();
        _controlsOverlay?.Close();
        _statsOverlay?.Close();
        _cameraOverlay?.Close();
        _controlsOverlay = _statsOverlay = _cameraOverlay = null;
        Hide();
        AppServices.Settings.Save();
        try { await Session.DisposeAsync(); }
        catch (Exception exception) { Log.Warn($"Falha ao encerrar a sessão: {exception.Message}"); }
        Session.Presenter = null;
        _presenter?.Dispose();
        _presenter = null;
        _cleaned = true;
        if (!IsLoaded) _closed.TrySetResult();
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        _closed.TrySetResult();
    }

    // ───────────────────────── Capturas de tela (CI) ─────────────────────────

    /// <summary>Mostra o player com dados de exemplo (sem rede): a festa de música, a tela com a câmera junto ou o aviso de conexão.</summary>
    internal void ShowDemo(StreamMode mode, ConnectionState state, bool withCamera = false)
    {
        _demo = true;
        _mode = mode;
        if (withCamera && mode == StreamMode.Screen)
        {
            BarTitle.Text = "Ana Lu";
            CameraLabel.Text = "Câmera · Ana Lu";
            VideoImage.Source = DemoData.ScreenFrame();
            CameraBrush.ImageSource = DemoData.CameraFrame();
            _cameraExpected = _cameraDemo = true;
            ResizeCameraPip();
            ShowStatistics(DemoData.ViewerStats(StreamMode.Screen) with { CameraFps = 30, CameraWidth = 1280, CameraHeight = 720 });
        }
        if (mode == StreamMode.AudioOnly)
        {
            var meter = DemoData.LiveMeter(64);
            PartySpectrum.Source = () => meter;
            PartyHost.Text = BarTitle.Text = "Festa da Mari";
            ShowStatistics(DemoData.ViewerStats(StreamMode.AudioOnly));
        }
        if (state == ConnectionState.Connected)
        {
            StateOverlay.Visibility = Visibility.Collapsed;
            UpdateLayers();
        }
        else
        {
            ShowState(state, state == ConnectionState.WaitingApproval
                ? "Quem está transmitindo precisa aceitar a sua entrada. Assim que aceitar, a imagem aparece aqui."
                : $"Conectando em {_host}…");
        }
    }
}
