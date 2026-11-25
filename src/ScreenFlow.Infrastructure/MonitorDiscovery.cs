using System.Windows.Forms;
using ScreenFlow.Core;

namespace ScreenFlow.Infrastructure;

public static class MonitorDiscovery
{
    public static IReadOnlyList<DisplayMonitor> GetMonitors()
    {
        return Screen.AllScreens.Select(screen =>
        {
            var bounds = screen.Bounds;
            var refresh = GetRefreshRate(screen.DeviceName);
            return new DisplayMonitor(screen.DeviceName, screen.DeviceName, bounds.Width, bounds.Height, refresh, screen.Primary, bounds.X, bounds.Y);
        }).OrderByDescending(m => m.IsPrimary).ToArray();
    }

    private static int GetRefreshRate(string deviceName)
    {
        try
        {
            var settings = new DEVMODE { dmSize = (short)System.Runtime.InteropServices.Marshal.SizeOf<DEVMODE>() };
            if (EnumDisplaySettings(deviceName, -1, ref settings) && settings.dmDisplayFrequency > 1)
                return settings.dmDisplayFrequency;
        }
        catch { /* Fallback below: refresh-rate information is optional on some display drivers. */ }
        return 0;
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential, CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private struct DEVMODE
    {
        private const int CCHDEVICENAME = 32;
        private const int CCHFORMNAME = 32;
        [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.ByValTStr, SizeConst = CCHDEVICENAME)] public string dmDeviceName;
        public short dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra;
        public int dmFields;
        public short dmOrientation, dmPaperSize, dmPaperLength, dmPaperWidth, dmScale, dmCopies, dmDefaultSource, dmPrintQuality;
        public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
        [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.ByValTStr, SizeConst = CCHFORMNAME)] public string dmFormName;
        public short dmLogPixels;
        public int dmBitsPerPel, dmPelsWidth, dmPelsHeight;
        public int dmDisplayFlags, dmDisplayFrequency;
        public int dmICMMethod, dmICMIntent, dmMediaType, dmDitherType, dmReserved1, dmReserved2, dmPanningWidth, dmPanningHeight;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern bool EnumDisplaySettings(string deviceName, int modeNum, ref DEVMODE devMode);
}
