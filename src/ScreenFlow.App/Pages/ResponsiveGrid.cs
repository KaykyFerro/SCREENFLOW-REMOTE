using System.Windows;
using System.Windows.Controls;

namespace ScreenFlow.App.Pages;

/// <summary>
/// Grade de duas colunas (conteúdo nas colunas 0 e 2, espaço na 1) que empilha a coluna da direita embaixo da
/// esquerda quando a janela fica estreita, para nada ficar espremido.
/// </summary>
internal static class ResponsiveGrid
{
    private static readonly DependencyProperty WideColumnsProperty =
        DependencyProperty.RegisterAttached("WideColumns", typeof(GridLength[]), typeof(ResponsiveGrid));

    private static readonly DependencyProperty RightSideProperty =
        DependencyProperty.RegisterAttached("RightSide", typeof(bool), typeof(ResponsiveGrid));

    public static void Apply(Grid grid, bool stacked)
    {
        if (grid.GetValue(WideColumnsProperty) is not GridLength[] wide)
        {
            wide = grid.ColumnDefinitions.Select(c => c.Width).ToArray();
            grid.SetValue(WideColumnsProperty, wide);
            while (grid.RowDefinitions.Count < 2) grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            foreach (UIElement child in grid.Children)
                child.SetValue(RightSideProperty, Grid.GetColumn(child) == 2);
        }

        for (var i = 0; i < grid.ColumnDefinitions.Count; i++)
            grid.ColumnDefinitions[i].Width = !stacked ? wide[i] : i == 0 ? new GridLength(1, GridUnitType.Star) : new GridLength(0);

        foreach (UIElement child in grid.Children)
        {
            if (!(bool)child.GetValue(RightSideProperty)) continue;
            Grid.SetColumn(child, stacked ? 0 : 2);
            Grid.SetRow(child, stacked ? 1 : 0);
        }
    }
}
