using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using ScreenFlow.Core.Settings;

namespace ScreenFlow.App.Services;

/// <summary>Troca o dicionário de cores (índice 0) entre Dark.xaml e Light.xaml, registra as fontes embutidas e pinta a moldura das janelas.</summary>
public static class ThemeManager
{
    private static readonly Uri PackRoot = new("pack://application:,,,/");

    public static AppTheme Current { get; private set; } = AppTheme.Dark;

    public static event Action? Changed;

    /// <summary>Inter embutida (Assets/Fonts, subconjunto latino). As fontes do sistema ficam como alternativa.</summary>
    public static void RegisterFonts()
    {
        var resources = Application.Current.Resources;
        resources["FontUi"] = new FontFamily(PackRoot, "./Assets/Fonts/#Inter, Segoe UI Variable Text, Segoe UI");
        resources["FontDisplay"] = new FontFamily(PackRoot, "./Assets/Fonts/#Inter Display, ./Assets/Fonts/#Inter, Segoe UI Variable Display, Segoe UI");
    }

    public static void Apply(AppTheme mode)
    {
        var dictionaries = Application.Current.Resources.MergedDictionaries;
        var file = mode == AppTheme.Light ? "Themes/Light.xaml" : "Themes/Dark.xaml";
        dictionaries[0] = new ResourceDictionary { Source = new Uri(file, UriKind.Relative) };
        Current = mode;
        foreach (Window window in Application.Current.Windows) ApplyFrame(window);
        Changed?.Invoke();
    }

    /// <summary>Moldura do Windows 11 no tom do tema: cantos arredondados, barra de título escura e borda discreta.</summary>
    public static void ApplyFrame(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == 0 || window is Windows.OverlayWindow || window.AllowsTransparency) return;
        try
        {
            var dark = Current == AppTheme.Dark || window is Windows.PlayerWindow ? 1 : 0;
            DwmSetWindowAttribute(hwnd, DwmUseImmersiveDarkMode, ref dark, sizeof(int));
            var corner = DwmCornerRound;
            DwmSetWindowAttribute(hwnd, DwmWindowCornerPreference, ref corner, sizeof(int));
            var border = ColorRef(Current == AppTheme.Dark || window is Windows.PlayerWindow ? Color.FromRgb(0x1D, 0x29, 0x48) : Color.FromRgb(0xD5, 0xDC, 0xEA));
            DwmSetWindowAttribute(hwnd, DwmBorderColor, ref border, sizeof(int));
            if (window is Windows.PlayerWindow)
            {
                var caption = ColorRef(Color.FromRgb(0x0B, 0x12, 0x24));
                DwmSetWindowAttribute(hwnd, DwmCaptionColor, ref caption, sizeof(int));
                var text = ColorRef(Colors.White);
                DwmSetWindowAttribute(hwnd, DwmTextColor, ref text, sizeof(int));
            }
        }
        catch (Exception) { /* Windows 10 antigo: sem esses atributos */ }
    }

    private static int ColorRef(Color color) => color.R | (color.G << 8) | (color.B << 16);

    private const int DwmUseImmersiveDarkMode = 20;
    private const int DwmWindowCornerPreference = 33;
    private const int DwmBorderColor = 34;
    private const int DwmCaptionColor = 35;
    private const int DwmTextColor = 36;
    private const int DwmCornerRound = 2;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(nint hwnd, int attribute, ref int value, int size);
}
