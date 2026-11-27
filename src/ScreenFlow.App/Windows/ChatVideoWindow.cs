using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ScreenFlow.App.Services;

namespace ScreenFlow.App.Windows;

/// <summary>
/// (1.5.0) Assiste um vídeo do chat (já baixado e conferido) dentro do ScreenFlow, pelo próprio Windows: tocar e pausar
/// (espaço), arrastar a barra, ±5 s pelas setas, velocidade, volume e tela cheia (duplo clique ou F). O vídeo é só tocado,
/// nada do arquivo roda. Os formatos que o Windows não toca mostram o aviso para abrir no seu player.
/// </summary>
public sealed class ChatVideoWindow : Window
{
    private static readonly double[] Speeds = [1.0, 1.25, 1.5, 2.0];
    private readonly MediaElement _media;
    private readonly Slider _seek;
    private readonly TextBlock _time;
    private readonly Button _play;
    private readonly Button _speed;
    private readonly Slider _volume;
    private readonly TextBlock _status;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(200) };
    private bool _playing;
    private bool _dragging;
    private int _speedIndex;
    private WindowState _beforeFull = WindowState.Normal;

    public ChatVideoWindow(string path, string title)
    {
        Title = title;
        Width = 1100;
        Height = 700;
        MinWidth = 560;
        MinHeight = 380;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        UseLayoutRounding = true;
        SetResourceReference(BackgroundProperty, "VideoBackground");
        SetResourceReference(ForegroundProperty, "VideoText");
        SetResourceReference(FontFamilyProperty, "FontUi");
        try { Icon = BitmapFrame.Create(new Uri("pack://application:,,,/Assets/screenflow.ico")); } catch (Exception) { }

        var app = Application.Current;
        _status = new TextBlock
        {
            Text = "Abrindo o vídeo…",
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
            MaxWidth = 460,
            FontSize = 14,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };

        _media = new MediaElement
        {
            LoadedBehavior = MediaState.Manual,
            UnloadedBehavior = MediaState.Close,
            Stretch = Stretch.Uniform,
            Volume = 0.8,
            ScrubbingEnabled = true,
            Source = new Uri(path, UriKind.Absolute),
        };
        _media.MediaOpened += (_, _) => OnOpened();
        _media.MediaEnded += (_, _) =>
        {
            _media.Pause();
            _media.Position = TimeSpan.Zero;
            SetPlaying(false);
        };
        _media.MediaFailed += (_, e) =>
        {
            SetPlaying(false);
            _status.Text = "O Windows não tocou este vídeo (formato sem suporte neste PC). Use \"Mostrar na pasta\" no cartão e abra no seu player.";
            _status.Visibility = Visibility.Visible;
            ScreenFlow.Core.Diagnostics.Log.Warn($"Vídeo do chat não tocou: {e.ErrorException?.Message}");
        };
        _media.MouseLeftButtonUp += (_, e) =>
        {
            if (e.ClickCount == 1) Toggle();
        };
        _media.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ClickCount == 2) ToggleFullScreen();
        };

        _play = IconButton("", "Tocar ou pausar (espaço)");
        _play.Click += (_, _) => Toggle();
        _seek = new Slider { Minimum = 0, Maximum = 1, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 12, 0), IsMoveToPointEnabled = true };
        _seek.AddHandler(Thumb.DragStartedEvent, new DragStartedEventHandler((_, _) => _dragging = true));
        _seek.AddHandler(Thumb.DragCompletedEvent, new DragCompletedEventHandler((_, _) =>
        {
            _dragging = false;
            _media.Position = TimeSpan.FromSeconds(_seek.Value);
        }));
        _seek.PreviewMouseLeftButtonUp += (_, _) =>
        {
            if (!_dragging) _media.Position = TimeSpan.FromSeconds(_seek.Value);
        };
        _time = new TextBlock { VerticalAlignment = VerticalAlignment.Center, FontFamily = (FontFamily)app.FindResource("FontMono"), FontSize = 12, Text = "0:00 / 0:00" };
        _speed = new Button { Content = "1×", MinWidth = 52, Height = 34, MinHeight = 34, Padding = new Thickness(10, 0, 10, 0), Margin = new Thickness(12, 0, 0, 0), ToolTip = "Velocidade" };
        _speed.Click += (_, _) => NextSpeed();
        var volumeIcon = new TextBlock { Style = (Style)app.FindResource("Icon"), Text = "", FontSize = 14, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(14, 0, 8, 0) };
        _volume = new Slider { Minimum = 0, Maximum = 1, Value = 0.8, Width = 110, VerticalAlignment = VerticalAlignment.Center, ToolTip = "Volume do vídeo" };
        _volume.ValueChanged += (_, e) => _media.Volume = e.NewValue;
        var full = IconButton("", "Tela cheia (F)");
        full.Margin = new Thickness(10, 0, 0, 0);
        full.Click += (_, _) => ToggleFullScreen();

        var bar = new DockPanel { Margin = new Thickness(16, 10, 16, 12) };
        DockPanel.SetDock(_play, Dock.Left);
        DockPanel.SetDock(full, Dock.Right);
        DockPanel.SetDock(_volume, Dock.Right);
        DockPanel.SetDock(volumeIcon, Dock.Right);
        DockPanel.SetDock(_speed, Dock.Right);
        DockPanel.SetDock(_time, Dock.Right);
        bar.Children.Add(_play);
        bar.Children.Add(full);
        bar.Children.Add(_volume);
        bar.Children.Add(volumeIcon);
        bar.Children.Add(_speed);
        bar.Children.Add(_time);
        bar.Children.Add(_seek);

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.Children.Add(_media);
        root.Children.Add(_status);
        var barHost = new Border { Child = bar, Background = new SolidColorBrush(Color.FromArgb(0xCC, 0x0B, 0x12, 0x24)) };
        Grid.SetRow(barHost, 1);
        root.Children.Add(barHost);
        Content = root;

        _timer.Tick += (_, _) => UpdateTime();
        SourceInitialized += (_, _) => ThemeManager.ApplyFrame(this);
        Loaded += (_, _) =>
        {
            _media.Play();
            SetPlaying(true);
            Focus();
        };
        Closed += (_, _) =>
        {
            _timer.Stop();
            _media.Stop();
            _media.Close();
        };
        PreviewKeyDown += OnKey;
    }

    private static Button IconButton(string glyph, string tip) => new()
    {
        Style = (Style)Application.Current.FindResource("IconButton"),
        Content = glyph,
        Width = 38,
        Height = 38,
        MinHeight = 38,
        ToolTip = tip,
    };

    private void OnOpened()
    {
        _status.Visibility = Visibility.Collapsed;
        _seek.Maximum = _media.NaturalDuration.HasTimeSpan ? Math.Max(0.1, _media.NaturalDuration.TimeSpan.TotalSeconds) : 1;
        if (!_media.HasVideo)
        {
            _status.Text = "Só tem som neste arquivo.";
            _status.Visibility = Visibility.Visible;
        }
        UpdateTime();
    }

    private void Toggle()
    {
        if (_playing) _media.Pause();
        else _media.Play();
        SetPlaying(!_playing);
    }

    private void SetPlaying(bool playing)
    {
        _playing = playing;
        _play.Content = playing ? "" : "";
        if (playing) _timer.Start();
        else _timer.Stop();
        UpdateTime();
    }

    private void NextSpeed()
    {
        _speedIndex = (_speedIndex + 1) % Speeds.Length;
        _media.SpeedRatio = Speeds[_speedIndex];
        _speed.Content = Speeds[_speedIndex].ToString("0.##", System.Globalization.CultureInfo.GetCultureInfo("pt-BR")) + "×";
    }

    private void UpdateTime()
    {
        var position = _media.Position;
        var total = _media.NaturalDuration.HasTimeSpan ? _media.NaturalDuration.TimeSpan : TimeSpan.Zero;
        if (!_dragging) _seek.Value = Math.Min(_seek.Maximum, position.TotalSeconds);
        _time.Text = $"{Clock(position)} / {Clock(total)}";
    }

    private static string Clock(TimeSpan time) => time.TotalHours >= 1 ? time.ToString(@"h\:mm\:ss") : time.ToString(@"m\:ss");

    private void Seek(double seconds)
    {
        var target = Math.Clamp(_media.Position.TotalSeconds + seconds, 0, _seek.Maximum);
        _media.Position = TimeSpan.FromSeconds(target);
        UpdateTime();
    }

    private void ToggleFullScreen()
    {
        if (WindowStyle == WindowStyle.None)
        {
            WindowStyle = WindowStyle.SingleBorderWindow;
            WindowState = _beforeFull;
        }
        else
        {
            _beforeFull = WindowState;
            WindowStyle = WindowStyle.None;
            WindowState = WindowState.Normal;
            WindowState = WindowState.Maximized;
        }
    }

    private void OnKey(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Space:
                Toggle();
                break;
            case Key.Left:
                Seek(-5);
                break;
            case Key.Right:
                Seek(5);
                break;
            case Key.F or Key.F11:
                ToggleFullScreen();
                break;
            case Key.Escape:
                if (WindowStyle == WindowStyle.None) ToggleFullScreen();
                else Close();
                break;
            default:
                return;
        }
        e.Handled = true;
    }
}
