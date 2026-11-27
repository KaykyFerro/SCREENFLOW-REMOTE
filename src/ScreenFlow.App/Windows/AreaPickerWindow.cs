using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using ScreenFlow.Core.Media;

namespace ScreenFlow.App.Windows;

/// <summary>
/// Escolher uma parte da tela ou da janela arrastando (1.4.0): uma película escura por cima exatamente da origem
/// transmitida, onde a pessoa desenha um retângulo. Devolve o retângulo em frações da origem, então vale em qualquer
/// resolução e escala do Windows. Esc cancela.
/// </summary>
public sealed class AreaPickerWindow : Window
{
    private const uint SwpNoZOrder = 0x0004, SwpNoActivate = 0x0010;
    private readonly (int X, int Y, int Width, int Height) _bounds;
    private readonly Canvas _canvas = new() { Background = new SolidColorBrush(Color.FromArgb(0x70, 0, 0, 0)), Cursor = Cursors.Cross };
    private readonly Rectangle _selection;
    private Point? _start;

    private AreaPickerWindow((int X, int Y, int Width, int Height) bounds, string hint, bool mask, IEnumerable<RelativeRect> existing)
    {
        _bounds = bounds;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        Topmost = true;
        ResizeMode = ResizeMode.NoResize;
        Title = mask ? "Tapar uma área" : "Escolher uma parte";
        Left = -32000; // a posição certa vem em pixels físicos, depois que a janela existe
        Width = 200;
        Height = 200;

        var accent = mask ? Color.FromRgb(0xFF, 0x5C, 0x8A) : Color.FromRgb(0x7C, 0x6C, 0xFF);
        _selection = new Rectangle
        {
            Stroke = new SolidColorBrush(accent),
            StrokeThickness = 2,
            Fill = mask ? new SolidColorBrush(Color.FromArgb(0xE0, 0, 0, 0)) : new SolidColorBrush(Color.FromArgb(0x30, accent.R, accent.G, accent.B)),
            Visibility = Visibility.Collapsed,
        };
        _existing = existing.ToList();

        var tip = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xE6, 0x17, 0x17, 0x1F)),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(16, 10, 16, 10),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 24, 0, 0),
            IsHitTestVisible = false,
            Child = new TextBlock { Text = hint + "  ·  Esc cancela", Foreground = Brushes.White, FontSize = 15, TextWrapping = TextWrapping.Wrap, MaxWidth = 640 },
        };
        AutomationProperties.SetName(this, Title);

        _canvas.Children.Add(_selection);
        var root = new Grid();
        root.Children.Add(_canvas);
        root.Children.Add(tip);
        Content = root;
        SetResourceReference(FontFamilyProperty, "FontUi");

        _canvas.MouseLeftButtonDown += OnDown;
        _canvas.MouseMove += OnMove;
        _canvas.MouseLeftButtonUp += OnUp;
        KeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
        SourceInitialized += (_, _) => PlaceOverSource();
        Loaded += (_, _) => { DrawExisting(); Activate(); Focus(); };
    }

    private readonly List<RelativeRect> _existing;

    /// <summary>O retângulo escolhido, ou null se a pessoa cancelou.</summary>
    public RelativeRect? Result { get; private set; }

    /// <summary>Mostra o seletor por cima do monitor ou da janela transmitida. Null se cancelou ou não deu para achar a origem.</summary>
    public static RelativeRect? Pick(Window owner, ScreenFlow.Core.CaptureTarget target, ScreenFlow.Core.DisplayMonitor? monitor, bool mask, IEnumerable<RelativeRect>? existing = null)
    {
        var bounds = BoundsOf(target, monitor);
        if (bounds is null) return null;
        var hint = mask
            ? "Arraste por cima do que ninguém deve ver. A área fica preta para quem assiste."
            : "Arraste para escolher a parte da tela que vai para quem assiste.";
        var picker = new AreaPickerWindow(bounds.Value, hint, mask, existing ?? []) { Owner = owner };
        picker.ShowDialog();
        return picker.Result;
    }

    private static (int X, int Y, int Width, int Height)? BoundsOf(ScreenFlow.Core.CaptureTarget target, ScreenFlow.Core.DisplayMonitor? monitor)
    {
        if (target.Kind == ScreenFlow.Core.CaptureKind.Window && target.Handle != 0)
        {
            // A moldura que a captura de janela pega (sem a sombra invisível do Windows 10 e 11).
            if (DwmGetWindowAttribute(target.Handle, 9 /* DWMWA_EXTENDED_FRAME_BOUNDS */, out var frame, Marshal.SizeOf<Rect>()) == 0 && frame.Right > frame.Left)
                return (frame.Left, frame.Top, frame.Right - frame.Left, frame.Bottom - frame.Top);
            return GetWindowRect(target.Handle, out var rect) && rect.Right > rect.Left ? (rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top) : null;
        }
        return monitor is { Width: > 0, Height: > 0 } m ? (m.X, m.Y, m.Width, m.Height) : null;
    }

    private void PlaceOverSource()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        SetWindowPos(hwnd, 0, _bounds.X, _bounds.Y, _bounds.Width, _bounds.Height, SwpNoZOrder | SwpNoActivate);
    }

    private void DrawExisting()
    {
        foreach (var area in _existing)
        {
            var shape = new Rectangle
            {
                Width = area.Width * _canvas.ActualWidth,
                Height = area.Height * _canvas.ActualHeight,
                Stroke = Brushes.White,
                StrokeDashArray = [4, 3],
                StrokeThickness = 1,
                Fill = new SolidColorBrush(Color.FromArgb(0x90, 0, 0, 0)),
                IsHitTestVisible = false,
            };
            Canvas.SetLeft(shape, area.X * _canvas.ActualWidth);
            Canvas.SetTop(shape, area.Y * _canvas.ActualHeight);
            _canvas.Children.Insert(0, shape);
        }
    }

    private void OnDown(object sender, MouseButtonEventArgs e)
    {
        _start = e.GetPosition(_canvas);
        _canvas.CaptureMouse();
        Update(_start.Value);
        _selection.Visibility = Visibility.Visible;
    }

    private void OnMove(object sender, MouseEventArgs e)
    {
        if (_start is not null) Update(e.GetPosition(_canvas));
    }

    private void OnUp(object sender, MouseButtonEventArgs e)
    {
        if (_start is not { } start) return;
        _canvas.ReleaseMouseCapture();
        var end = e.GetPosition(_canvas);
        _start = null;
        var width = _canvas.ActualWidth;
        var height = _canvas.ActualHeight;
        if (width <= 0 || height <= 0) return;
        var rect = new RelativeRect(Math.Min(start.X, end.X) / width, Math.Min(start.Y, end.Y) / height,
            Math.Abs(end.X - start.X) / width, Math.Abs(end.Y - start.Y) / height).Clean();
        // Um clique sem arrastar não escolhe nada: continua esperando.
        if (rect is null || rect.Width < 0.03 || rect.Height < 0.03)
        {
            _selection.Visibility = Visibility.Collapsed;
            return;
        }
        Result = rect;
        Close();
    }

    private void Update(Point point)
    {
        var start = _start ?? point;
        Canvas.SetLeft(_selection, Math.Min(start.X, point.X));
        Canvas.SetTop(_selection, Math.Min(start.Y, point.Y));
        _selection.Width = Math.Abs(point.X - start.X);
        _selection.Height = Math.Abs(point.Y - start.Y);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetWindowRect(nint hwnd, out Rect rect);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetWindowPos(nint hwnd, nint after, int x, int y, int cx, int cy, uint flags);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(nint hwnd, int attribute, out Rect value, int size);
}
