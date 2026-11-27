using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using ScreenFlow.Core.Settings;

namespace ScreenFlow.App.Services;

/// <summary>
/// Troca o dicionário de cores (índice 0) entre Dark.xaml e Light.xaml, pinta por cima a cor do tema escolhida (1.4.1),
/// registra as fontes embutidas e pinta a moldura das janelas.
/// </summary>
public static class ThemeManager
{
    private static readonly Uri PackRoot = new("pack://application:,,,/");

    public static AppTheme Current { get; private set; } = AppTheme.Dark;

    /// <summary>A cor do tema em uso (a menta é a de sempre).</summary>
    public static ColorTheme CurrentColor { get; private set; } = ColorThemes.Default;

    public static event Action? Changed;

    /// <summary>Inter embutida (Assets/Fonts, subconjunto latino). As fontes do sistema ficam como alternativa.</summary>
    public static void RegisterFonts()
    {
        var resources = Application.Current.Resources;
        resources["FontUi"] = new FontFamily(PackRoot, "./Assets/Fonts/#Inter, Segoe UI Variable Text, Segoe UI");
        resources["FontDisplay"] = new FontFamily(PackRoot, "./Assets/Fonts/#Inter Display, ./Assets/Fonts/#Inter, Segoe UI Variable Display, Segoe UI");
    }

    public static void Apply(AppTheme mode) => Apply(mode, CurrentColor.Id);

    /// <summary>O modo (escuro ou claro) e a cor do tema. As telas usam DynamicResource, então muda tudo na hora.</summary>
    public static void Apply(AppTheme mode, string? colorTheme)
    {
        var dictionaries = Application.Current.Resources.MergedDictionaries;
        var file = mode == AppTheme.Light ? "Themes/Light.xaml" : "Themes/Dark.xaml";
        var dictionary = new ResourceDictionary { Source = new Uri(file, UriKind.Relative) };
        var color = ColorThemes.Resolve(colorTheme);
        // A menta é o tema dos arquivos; as outras cores trocam o fundo, o texto e o destaque por cima deles.
        if (color != ColorThemes.Default) Paint(dictionary, ColorThemes.Palette(color, mode == AppTheme.Dark));
        dictionaries[0] = dictionary;
        Current = mode;
        CurrentColor = color;
        foreach (Window window in Application.Current.Windows) ApplyFrame(window);
        Changed?.Invoke();
    }

    private static void Paint(ResourceDictionary dictionary, ThemePalette palette)
    {
        foreach (var (key, argb) in palette.Solids)
        {
            var brush = new SolidColorBrush(ToColor(argb));
            brush.Freeze();
            dictionary[key] = brush;
        }
        foreach (var (key, colors) in palette.Gradients)
        {
            if (dictionary[key] is not GradientBrush original || original.GradientStops.Count != colors.Length) continue;
            var brush = original.Clone();
            for (var i = 0; i < colors.Length; i++) brush.GradientStops[i].Color = ToColor(colors[i]);
            brush.Freeze();
            dictionary[key] = brush;
        }
    }

    private static Color ToColor(uint argb) => Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb);

    /// <summary>A cor de uma chave do tema em uso (para o que não é desenhado pelo XAML, como a borda da janela).</summary>
    public static Color ColorOf(string key, Color fallback)
        => Application.Current?.TryFindResource(key) is SolidColorBrush brush ? brush.Color : fallback;

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
            var border = ColorRef(window is Windows.PlayerWindow ? Color.FromRgb(0x1D, 0x29, 0x48)
                : Current == AppTheme.Dark ? ColorOf("Border", Color.FromRgb(0x1D, 0x29, 0x48)) : ColorOf("BorderStrong", Color.FromRgb(0xB9, 0xC4, 0xD8)));
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
