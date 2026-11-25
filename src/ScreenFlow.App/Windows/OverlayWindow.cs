using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace ScreenFlow.App.Windows;

/// <summary>
/// Janela transparente presa ao player que mostra controles por cima do vídeo desenhado pela GPU (o WPF não desenha por
/// cima de janelas nativas). Não rouba o foco do player e acompanha a posição e o tamanho dele.
/// </summary>
public sealed class OverlayWindow : Window
{
    private const int GwlExStyle = -20, WsExNoActivate = 0x08000000, WsExToolWindow = 0x00000080;
    private const uint SwpNoSize = 0x0001, SwpNoZOrder = 0x0004, SwpNoActivate = 0x0010;
    private readonly Window _owner;
    private readonly HorizontalAlignment _horizontal;
    private readonly VerticalAlignment _vertical;
    private readonly Thickness _margin;

    public OverlayWindow(Window owner, FrameworkElement content, HorizontalAlignment horizontal, VerticalAlignment vertical, Thickness margin,
        ResourceDictionary? theme = null)
    {
        if (theme is not null) Resources.MergedDictionaries.Add(theme);
        _owner = owner;
        _horizontal = horizontal;
        _vertical = vertical;
        _margin = margin;
        Owner = owner;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.WidthAndHeight;
        Focusable = false;
        Content = content;
        SetResourceReference(FontFamilyProperty, "FontUi");
        SetResourceReference(ForegroundProperty, "VideoText");
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Ideal);
        UseLayoutRounding = true;
        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            SetWindowLongW(hwnd, GwlExStyle, GetWindowLongW(hwnd, GwlExStyle) | WsExNoActivate | WsExToolWindow);
        };
        SizeChanged += (_, _) => Reposition();
    }

    /// <summary>Recoloca a janela em relação à área do player (em pixels, funciona com qualquer DPI).</summary>
    public void Reposition()
    {
        var ownerHwnd = new WindowInteropHelper(_owner).Handle;
        var hwnd = new WindowInteropHelper(this).Handle;
        if (ownerHwnd == 0 || hwnd == 0 || !GetClientRect(ownerHwnd, out var client) || !GetWindowRect(hwnd, out var self)) return;
        var origin = new NativePoint();
        ClientToScreen(ownerHwnd, ref origin);
        var scale = VisualTreeHelper.GetDpi(_owner).DpiScaleX;
        var width = self.Right - self.Left;
        var height = self.Bottom - self.Top;
        var clientWidth = client.Right - client.Left;
        var clientHeight = client.Bottom - client.Top;
        var x = _horizontal switch
        {
            HorizontalAlignment.Left => origin.X + (int)(_margin.Left * scale),
            HorizontalAlignment.Right => origin.X + clientWidth - width - (int)(_margin.Right * scale),
            _ => origin.X + (clientWidth - width) / 2,
        };
        var y = _vertical switch
        {
            VerticalAlignment.Top => origin.Y + (int)(_margin.Top * scale),
            VerticalAlignment.Bottom => origin.Y + clientHeight - height - (int)(_margin.Bottom * scale),
            _ => origin.Y + (clientHeight - height) / 2,
        };
        SetWindowPos(hwnd, 0, x, y, 0, 0, SwpNoSize | SwpNoZOrder | SwpNoActivate);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X, Y;
    }

    [DllImport("user32.dll")]
    private static extern bool GetClientRect(nint hwnd, out NativeRect rect);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(nint hwnd, out NativeRect rect);

    [DllImport("user32.dll")]
    private static extern bool ClientToScreen(nint hwnd, ref NativePoint point);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(nint hwnd, nint after, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    private static extern int GetWindowLongW(nint hwnd, int index);

    [DllImport("user32.dll")]
    private static extern int SetWindowLongW(nint hwnd, int index, int value);
}
