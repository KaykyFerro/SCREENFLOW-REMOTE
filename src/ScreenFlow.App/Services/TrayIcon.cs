using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ScreenFlow.Core.Diagnostics;

namespace ScreenFlow.App.Services;

/// <summary>Bolinha no canto do ícone da bandeja: ao vivo (rosa), assistindo (ciano) ou na voz da rede (verde).</summary>
public enum TrayBadge { None, Live, Watching, Voice }

/// <summary>
/// Ícone na área de notificação do Windows (bandeja). Clique abre o app, botão direito abre o menu e, com o mouse em
/// cima, a dica mostra o que está acontecendo (ao vivo, o que está sendo transmitido, quem está na voz). Usa uma janela
/// oculta própria para receber os eventos do ícone e para recriá-lo quando o Explorer reinicia.
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private const int CallbackMessage = 0x8000 + 0x51; // WM_APP + n
    private const uint NimAdd = 0, NimModify = 1, NimDelete = 2, NimSetVersion = 4;
    private const uint NifMessage = 0x01, NifIcon = 0x02, NifTip = 0x04, NifInfo = 0x10, NifShowTip = 0x80;
    private const uint NiifUser = 0x04, NiifNoSound = 0x10, NiifLargeIcon = 0x20;
    private const uint NotifyIconVersion4 = 4;
    private const int WmLButtonUp = 0x0202, WmRButtonUp = 0x0205, WmContextMenu = 0x007B;
    private const int NinSelect = 0x0400, NinKeySelect = 0x0401, NinBalloonUserClick = 0x0405;
    private const int SmCxSmIcon = 49;

    private readonly HwndSource _window;
    private readonly int _taskbarCreated;
    private readonly Dictionary<TrayBadge, nint> _icons = [];
    private nint _balloonIcon;
    private TrayBadge _badge;
    private string _tip = "ScreenFlow";
    private bool _added;
    private bool _disposed;
    private DateTime _lastActivate;
    private DateTime _lastMenu;
    private ContextMenu? _menu;

    public TrayIcon()
    {
        // Janela oculta comum (não "message-only"): só janelas de topo recebem o aviso de que a barra de tarefas foi recriada.
        _window = new HwndSource(new HwndSourceParameters("ScreenFlowTray")
        {
            WindowStyle = unchecked((int)0x80000000), // WS_POPUP, nunca mostrada
            ExtendedWindowStyle = 0x00000080,        // WS_EX_TOOLWINDOW: fora da barra de tarefas e do Alt+Tab
            Width = 0,
            Height = 0,
            PositionX = -32000,
            PositionY = -32000,
        });
        _window.AddHook(WndProc);
        _taskbarCreated = RegisterWindowMessageW("TaskbarCreated");
        Update();
    }

    /// <summary>Clique (ou Enter) no ícone.</summary>
    public event Action? Activated;

    /// <summary>Botão direito no ícone: quem usa monta o menu e chama <see cref="ShowMenu"/>.</summary>
    public event Action? MenuRequested;

    /// <summary>Clique no aviso (balão/notificação) mostrado por <see cref="ShowBalloon"/>.</summary>
    public event Action? BalloonClicked;

    public bool IsVisible => _added;

    /// <summary>Troca a dica (até 127 caracteres, uma informação por linha) e a bolinha do ícone.</summary>
    public void Set(string tip, TrayBadge badge)
    {
        tip = tip.Length > 127 ? tip[..126] + "…" : tip;
        if (tip == _tip && badge == _badge && _added) return;
        _tip = tip;
        _badge = badge;
        Update();
    }

    /// <summary>Notificação do Windows saindo do ícone (vira um aviso na central de notificações).</summary>
    public void ShowBalloon(string title, string message, bool quiet = false)
    {
        if (_disposed) return;
        if (_balloonIcon == 0) _balloonIcon = CreateIcon(TrayBadge.None, 48);
        var data = NewData(NifInfo);
        data.szInfoTitle = Truncate(title, 63);
        data.szInfo = Truncate(message, 255);
        data.dwInfoFlags = NiifUser | NiifLargeIcon | (quiet ? NiifNoSound : 0);
        data.hBalloonIcon = _balloonIcon;
        if (!_added) Update();
        Shell_NotifyIconW(NimModify, ref data);
    }

    /// <summary>Abre o menu do ícone onde está o mouse. O menu fecha ao clicar fora, como os menus do Windows.</summary>
    public void ShowMenu(ContextMenu menu)
    {
        if (_menu is { IsOpen: true }) _menu.IsOpen = false;
        _menu = menu;
        menu.Placement = PlacementMode.MousePoint;
        menu.IsOpen = true;
        // O menu só percebe o clique fora quando a janela dele está em primeiro plano.
        var handle = (PresentationSource.FromVisual(menu) as HwndSource)?.Handle ?? _window.Handle;
        SetForegroundWindow(handle);
    }

    private void Update()
    {
        if (_disposed) return;
        if (!_icons.TryGetValue(_badge, out var icon))
        {
            icon = CreateIcon(_badge, IconSize());
            if (icon != 0) _icons[_badge] = icon;
        }
        var data = NewData(NifMessage | NifIcon | NifTip | NifShowTip);
        data.uCallbackMessage = CallbackMessage;
        data.hIcon = icon;
        data.szTip = _tip;
        if (_added && Shell_NotifyIconW(NimModify, ref data)) return;

        _added = Shell_NotifyIconW(NimAdd, ref data);
        if (!_added)
        {
            // Já existia (Explorer reiniciado no meio do caminho): tenta atualizar.
            _added = Shell_NotifyIconW(NimModify, ref data);
            if (!_added) { Log.Debug("Ícone da bandeja indisponível no momento."); return; }
        }
        var version = NewData(0);
        version.uVersion = NotifyIconVersion4;
        Shell_NotifyIconW(NimSetVersion, ref version);
    }

    private NotifyIconData NewData(uint flags) => new()
    {
        cbSize = Marshal.SizeOf<NotifyIconData>(),
        hWnd = _window.Handle,
        uID = 1,
        uFlags = flags,
        szTip = string.Empty,
        szInfo = string.Empty,
        szInfoTitle = string.Empty,
    };

    private nint WndProc(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message == _taskbarCreated && _taskbarCreated != 0)
        {
            // O Explorer reiniciou: o ícone sumiu e precisa ser adicionado de novo.
            _added = false;
            Update();
            return 0;
        }
        if (message != CallbackMessage) return 0;
        handled = true;
        var now = DateTime.UtcNow;
        switch ((int)(lParam & 0xFFFF))
        {
            case WmLButtonUp:
            case NinSelect:
            case NinKeySelect:
                // Um clique gera mais de um evento: só o primeiro conta.
                if (now - _lastActivate < TimeSpan.FromMilliseconds(400)) break;
                _lastActivate = now;
                Activated?.Invoke();
                break;
            case WmContextMenu:
            case WmRButtonUp:
                if (now - _lastMenu < TimeSpan.FromMilliseconds(400)) break;
                _lastMenu = now;
                MenuRequested?.Invoke();
                break;
            case NinBalloonUserClick:
                BalloonClicked?.Invoke();
                break;
        }
        return 0;
    }

    // ───────────────────────── Ícone ─────────────────────────

    private static int IconSize()
    {
        try
        {
            var size = GetSystemMetricsForDpi(SmCxSmIcon, GetDpiForSystem());
            return size is >= 16 and <= 64 ? size : 16;
        }
        catch (EntryPointNotFoundException) { return 16; }
    }

    /// <summary>Desenha o logo do app (do .ico, no tamanho certo) com a bolinha de estado e cria o HICON.</summary>
    internal static nint CreateIcon(TrayBadge badge, int size)
    {
        try
        {
            var pixels = RenderIcon(badge, size);
            return CreateIconFromBgra(pixels, size);
        }
        catch (Exception exception)
        {
            Log.Warn($"Não foi possível desenhar o ícone da bandeja: {exception.Message}");
            return 0;
        }
    }

    internal static byte[] RenderIcon(TrayBadge badge, int size)
    {
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            context.DrawImage(LogoFrame(size), new Rect(0, 0, size, size));
            if (badge != TrayBadge.None)
            {
                var color = badge switch
                {
                    TrayBadge.Live => Color.FromRgb(0xFF, 0x4D, 0x8D),
                    TrayBadge.Watching => Color.FromRgb(0x22, 0xD3, 0xEE),
                    _ => Color.FromRgb(0x2E, 0xE6, 0xA6),
                };
                var radius = size * 0.2;
                var center = new Point(size - radius - size * 0.04, size - radius - size * 0.04);
                // Anel escuro em volta para a bolinha aparecer em cima do logo e de qualquer barra de tarefas.
                context.DrawEllipse(new SolidColorBrush(Color.FromRgb(0x0A, 0x10, 0x22)), null, center, radius + size * 0.075, radius + size * 0.075);
                context.DrawEllipse(new SolidColorBrush(color), null, center, radius, radius);
            }
        }
        var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var pixels = new byte[size * size * 4];
        bitmap.CopyPixels(pixels, size * 4, 0);
        // Ícones do Windows usam alfa "reto"; o WPF entrega pré-multiplicado.
        for (var i = 0; i < pixels.Length; i += 4)
        {
            var alpha = pixels[i + 3];
            if (alpha is 0 or 255) continue;
            pixels[i] = (byte)Math.Min(255, pixels[i] * 255 / alpha);
            pixels[i + 1] = (byte)Math.Min(255, pixels[i + 1] * 255 / alpha);
            pixels[i + 2] = (byte)Math.Min(255, pixels[i + 2] * 255 / alpha);
        }
        return pixels;
    }

    private static BitmapSource LogoFrame(int size)
    {
        var decoder = BitmapDecoder.Create(new Uri("pack://application:,,,/Assets/screenflow.ico"), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        var frames = decoder.Frames.OrderBy(f => f.PixelWidth).ToList();
        return frames.FirstOrDefault(f => f.PixelWidth >= size) ?? frames[^1];
    }

    private static nint CreateIconFromBgra(byte[] pixels, int size)
    {
        var header = new BitmapInfoHeader
        {
            biSize = Marshal.SizeOf<BitmapInfoHeader>(),
            biWidth = size,
            biHeight = -size, // de cima para baixo
            biPlanes = 1,
            biBitCount = 32,
        };
        var color = CreateDIBSection(0, ref header, 0, out var bits, 0, 0);
        if (color == 0) return 0;
        var mask = CreateBitmap(size, size, 1, 1, 0);
        try
        {
            Marshal.Copy(pixels, 0, bits, pixels.Length);
            var info = new IconInfo { fIcon = true, hbmMask = mask, hbmColor = color };
            return CreateIconIndirect(ref info);
        }
        finally
        {
            DeleteObject(color);
            if (mask != 0) DeleteObject(mask);
        }
    }

    private static string Truncate(string text, int max) => text.Length > max ? text[..(max - 1)] + "…" : text;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_menu is { IsOpen: true }) _menu.IsOpen = false;
        var data = NewData(0);
        Shell_NotifyIconW(NimDelete, ref data);
        _added = false;
        foreach (var icon in _icons.Values) DestroyIcon(icon);
        _icons.Clear();
        if (_balloonIcon != 0) DestroyIcon(_balloonIcon);
        _window.RemoveHook(WndProc);
        _window.Dispose();
    }

    // ───────────────────────── Win32 ─────────────────────────

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public int cbSize;
        public nint hWnd;
        public int uID;
        public uint uFlags;
        public int uCallbackMessage;
        public nint hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public uint dwState;
        public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public uint uVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public nint hBalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IconInfo
    {
        [MarshalAs(UnmanagedType.Bool)] public bool fIcon;
        public int xHotspot;
        public int yHotspot;
        public nint hbmMask;
        public nint hbmColor;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public int biSize;
        public int biWidth;
        public int biHeight;
        public short biPlanes;
        public short biBitCount;
        public int biCompression;
        public int biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public int biClrUsed;
        public int biClrImportant;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Shell_NotifyIconW(uint message, ref NotifyIconData data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int RegisterWindowMessageW(string name);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(nint hwnd);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetricsForDpi(int index, uint dpi);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForSystem();

    [DllImport("user32.dll")]
    private static extern nint CreateIconIndirect(ref IconInfo info);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(nint icon);

    [DllImport("gdi32.dll")]
    private static extern nint CreateDIBSection(nint hdc, ref BitmapInfoHeader header, uint usage, out nint bits, nint section, uint offset);

    [DllImport("gdi32.dll")]
    private static extern nint CreateBitmap(int width, int height, uint planes, uint bitCount, nint bits);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(nint handle);
}
