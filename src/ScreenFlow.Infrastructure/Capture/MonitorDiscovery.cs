using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
using ScreenFlow.Core;

namespace ScreenFlow.Infrastructure.Capture;

public static class MonitorDiscovery
{
    public static IReadOnlyList<DisplayMonitor> GetMonitors()
    {
        var handles = new Dictionary<string, nint>(StringComparer.OrdinalIgnoreCase);
        EnumDisplayMonitors(0, 0, (monitor, _, _, _) =>
        {
            var info = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>() };
            if (GetMonitorInfo(monitor, ref info)) handles[info.szDevice] = monitor;
            return true;
        }, 0);

        var index = 1;
        return Screen.AllScreens.Select(screen =>
        {
            var bounds = screen.Bounds;
            handles.TryGetValue(screen.DeviceName, out var handle);
            return new DisplayMonitor(screen.DeviceName, $"Tela {index++}", bounds.Width, bounds.Height, GetRefreshRate(screen.DeviceName), screen.Primary, bounds.X, bounds.Y, handle);
        }).OrderByDescending(m => m.IsPrimary).ToArray();
    }

    private static int GetRefreshRate(string deviceName)
    {
        try
        {
            var settings = new DEVMODE { dmSize = (short)Marshal.SizeOf<DEVMODE>() };
            if (EnumDisplaySettings(deviceName, -1, ref settings) && settings.dmDisplayFrequency > 1)
                return settings.dmDisplayFrequency;
        }
        catch { /* taxa de atualização é opcional em alguns drivers */ }
        return 0;
    }

    private delegate bool MonitorEnumProc(nint monitor, nint hdc, nint rect, nint data);

    [DllImport("user32.dll")] private static extern bool EnumDisplayMonitors(nint hdc, nint clip, MonitorEnumProc callback, nint data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetMonitorInfo(nint monitor, ref MONITORINFOEX info);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MONITORINFOEX
    {
        public int cbSize;
        public RECT rcMonitor, rcWork;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szDevice;
    }

    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DEVMODE
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
        public short dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra;
        public int dmFields;
        public short dmOrientation, dmPaperSize, dmPaperLength, dmPaperWidth, dmScale, dmCopies, dmDefaultSource, dmPrintQuality;
        public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
        public short dmLogPixels;
        public int dmBitsPerPel, dmPelsWidth, dmPelsHeight;
        public int dmDisplayFlags, dmDisplayFrequency;
        public int dmICMMethod, dmICMIntent, dmMediaType, dmDitherType, dmReserved1, dmReserved2, dmPanningWidth, dmPanningHeight;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplaySettings(string deviceName, int modeNum, ref DEVMODE devMode);
}

/// <summary>Lista janelas visíveis com título para o seletor "monitor ou janela".</summary>
public static class WindowDiscovery
{
    public static IReadOnlyList<CaptureTarget> GetWindows()
    {
        var self = Environment.ProcessId;
        var list = new List<CaptureTarget>();
        EnumWindows((hwnd, _) =>
        {
            if (!IsWindowVisible(hwnd) || IsIconic(hwnd)) return true;
            var length = GetWindowTextLength(hwnd);
            if (length == 0) return true;
            GetWindowThreadProcessId(hwnd, out var pid);
            if (pid == self) return true;
            if (DwmGetWindowAttribute(hwnd, 14 /* DWMWA_CLOAKED */, out var cloaked, sizeof(int)) == 0 && cloaked != 0) return true;
            var builder = new StringBuilder(length + 1);
            GetWindowText(hwnd, builder, builder.Capacity);
            GetClientRect(hwnd, out var rect);
            var w = rect.Right - rect.Left;
            var h = rect.Bottom - rect.Top;
            if (w < 64 || h < 64) return true;
            var title = builder.ToString();
            list.Add(new CaptureTarget(CaptureKind.Window, hwnd.ToString(), "Janela · " + (title.Length > 60 ? title[..60] + "…" : title), w, h, 0, hwnd));
            return true;
        }, 0);
        return list.OrderBy(w => w.Label).ToArray();
    }

    private delegate bool EnumWindowsProc(nint hwnd, nint data);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc callback, nint data);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint hwnd);
    [DllImport("user32.dll")] private static extern bool IsIconic(nint hwnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(nint hwnd, StringBuilder text, int max);
    [DllImport("user32.dll")] private static extern int GetWindowTextLength(nint hwnd);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint hwnd, out int pid);
    [DllImport("user32.dll")] private static extern bool GetClientRect(nint hwnd, out RECT rect);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(nint hwnd, int attribute, out int value, int size);
    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
}
