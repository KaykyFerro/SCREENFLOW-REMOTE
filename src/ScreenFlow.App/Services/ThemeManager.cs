using System.Windows;
using ScreenFlow.Core.Settings;

namespace ScreenFlow.App.Services;

/// <summary>Troca o dicionário de cores (índice 0) entre Dark.xaml e Light.xaml.</summary>
public static class ThemeManager
{
    public static AppTheme Current { get; private set; } = AppTheme.Dark;

    public static void Apply(AppTheme mode)
    {
        var dictionaries = Application.Current.Resources.MergedDictionaries;
        var file = mode == AppTheme.Light ? "Themes/Light.xaml" : "Themes/Dark.xaml";
        dictionaries[0] = new ResourceDictionary { Source = new Uri(file, UriKind.Relative) };
        Current = mode;
    }
}
