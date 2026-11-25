using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace ScreenFlow.App.Controls;

/// <summary>
/// Janela filha nativa onde o vídeo é desenhado direto pela GPU (swapchain D3D11). O WPF não consegue desenhar por cima
/// dela; os controles do player ficam numa janela sobreposta (<see cref="Windows.OverlayWindow"/>).
/// </summary>
public sealed class VideoSurface : HwndHost
{
    private const string ClassName = "ScreenFlowVideoSurface";
    private const int WsChild = 0x40000000, WsVisible = 0x10000000, WsClipSiblings = 0x04000000, WsClipChildren = 0x02000000;
    private const int CsDblClks = 0x0008, CsHRedraw = 0x0002, CsVRedraw = 0x0001;
    private const int IdcArrow = 32512;
    private const int WmSize = 0x0005, WmSetCursor = 0x0020, WmMouseMove = 0x0200, WmLButtonDblClk = 0x0203, WmEraseBkgnd = 0x0014;
    private static readonly object RegisterGate = new();
    private static bool _registered;
    private nint _hwnd;

    /// <summary>A janela nativa foi criada (<see cref="SurfaceHandle"/> já é válido). Chega pela fila da interface.</summary>
    public event Action? HandleCreated;

    /// <summary>O mouse andou sobre o vídeo.</summary>
    public event Action? MouseMoved;

    /// <summary>Duplo clique no vídeo (alterna tela cheia).</summary>
    public event Action? DoubleClicked;

    /// <summary>A janela nativa mudou de tamanho (em pixels).</summary>
    public event Action<int, int>? PixelSizeChanged;

    /// <summary>Esconde o cursor sobre o vídeo (tela cheia parada).</summary>
    public bool HideCursor { get; set; }

    /// <summary>Quando a GPU já está desenhando, o fundo não precisa ser pintado (evita piscar ao redimensionar).</summary>
    public bool SkipErase { get; set; }

    public nint SurfaceHandle => _hwnd;

    protected override HandleRef BuildWindowCore(HandleRef hwndParent)
    {
        EnsureClass();
        _hwnd = CreateWindowExW(0, ClassName, "", WsChild | WsVisible | WsClipSiblings | WsClipChildren, 0, 0,
            Math.Max(1, (int)ActualWidth), Math.Max(1, (int)ActualHeight), hwndParent.Handle, 0, GetModuleHandleW(null), 0);
        if (_hwnd == 0) throw new InvalidOperationException($"Não foi possível criar a superfície de vídeo (erro {Marshal.GetLastWin32Error()}).");
        var created = HandleCreated;
        if (created is not null) Dispatcher.BeginInvoke(created);
        return new HandleRef(this, _hwnd);
    }

    protected override void DestroyWindowCore(HandleRef hwnd)
    {
        DestroyWindow(hwnd.Handle);
        _hwnd = 0;
    }

    protected override nint WndProc(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        switch (msg)
        {
            case WmMouseMove:
                MouseMoved?.Invoke();
                break;
            case WmLButtonDblClk:
                DoubleClicked?.Invoke();
                handled = true;
                return 0;
            case WmSetCursor when (lParam & 0xFFFF) == 1: // HTCLIENT: a classe não tem cursor próprio
                SetCursor(HideCursor ? 0 : LoadCursorW(0, IdcArrow));
                handled = true;
                return 1;
            case WmEraseBkgnd when SkipErase:
                handled = true;
                return 1;
            case WmSize:
                var width = (int)(lParam & 0xFFFF);
                var height = (int)((lParam >> 16) & 0xFFFF);
                if (width > 0 && height > 0) PixelSizeChanged?.Invoke(width, height);
                break;
        }
        return base.WndProc(hwnd, msg, wParam, lParam, ref handled);
    }

    private static void EnsureClass()
    {
        lock (RegisterGate)
        {
            if (_registered) return;
            var user32 = NativeLibrary.Load("user32.dll");
            var wc = new WndClassExW
            {
                cbSize = (uint)Marshal.SizeOf<WndClassExW>(),
                style = CsDblClks | CsHRedraw | CsVRedraw,
                lpfnWndProc = NativeLibrary.GetExport(user32, "DefWindowProcW"),
                hInstance = GetModuleHandleW(null),
                hbrBackground = GetStockObject(4), // BLACK_BRUSH
                lpszClassName = ClassName,
            };
            if (RegisterClassExW(ref wc) == 0 && Marshal.GetLastWin32Error() != 1410) // ERROR_CLASS_ALREADY_EXISTS
                throw new InvalidOperationException($"RegisterClassEx falhou ({Marshal.GetLastWin32Error()}).");
            _registered = true;
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WndClassExW
    {
        public uint cbSize;
        public uint style;
        public nint lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public nint hInstance;
        public nint hIcon;
        public nint hCursor;
        public nint hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
        public nint hIconSm;
    }

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern ushort RegisterClassExW(ref WndClassExW wc);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern nint CreateWindowExW(int exStyle, string className, string windowName, int style, int x, int y, int width, int height,
        nint parent, nint menu, nint instance, nint param);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(nint hwnd);

    [DllImport("user32.dll")]
    private static extern nint SetCursor(nint cursor);

    [DllImport("user32.dll")]
    private static extern nint LoadCursorW(nint instance, nint name);

    [DllImport("gdi32.dll")]
    private static extern nint GetStockObject(int index);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern nint GetModuleHandleW(string? name);
}
