using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using ScreenFlow.App.Services;

namespace ScreenFlow.App.Controls;

/// <summary>Cartão do Monitoramento: ícone, nome, valor atual grande e o gráfico dos últimos 90 segundos.</summary>
public sealed class MetricCard : Border
{
    private readonly Border _tile;
    private readonly TextBlock _glyph;
    private readonly TextBlock _label;
    private readonly TextBlock _note;
    private readonly TextBlock _value;
    private readonly TextBlock _unit;
    private readonly Sparkline _spark;
    private string? _accent;

    public MetricCard()
    {
        Style = (Style)Application.Current.FindResource("Card");
        Margin = new Thickness(0, 0, 16, 16);
        Padding = new Thickness(20, 18, 20, 16);

        _glyph = new TextBlock { Style = (Style)Application.Current.FindResource("Icon"), FontSize = 15 };
        _tile = new Border { Width = 34, Height = 34, CornerRadius = new CornerRadius(10), Child = _glyph, VerticalAlignment = VerticalAlignment.Center };
        _label = new TextBlock { Style = (Style)Application.Current.FindResource("Body"), FontWeight = FontWeights.SemiBold, Margin = new Thickness(12, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        _note = new TextBlock { Style = (Style)Application.Current.FindResource("Caption"), VerticalAlignment = VerticalAlignment.Center };
        DockPanel.SetDock(_tile, Dock.Left);
        DockPanel.SetDock(_note, Dock.Right);
        var header = new DockPanel();
        header.Children.Add(_tile);
        header.Children.Add(_note);
        header.Children.Add(_label);

        _value = new TextBlock { Style = (Style)Application.Current.FindResource("Stat"), FontSize = 32, Text = "—" };
        _unit = new TextBlock { Style = (Style)Application.Current.FindResource("Caption"), FontSize = 13, Margin = new Thickness(7, 0, 0, 6), VerticalAlignment = VerticalAlignment.Bottom };
        var value = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 16, 0, 0) };
        value.Children.Add(_value);
        value.Children.Add(_unit);

        _spark = new Sparkline { Height = 64, Margin = new Thickness(0, 12, 0, 0) };
        var stack = new StackPanel();
        stack.Children.Add(header);
        stack.Children.Add(value);
        stack.Children.Add(_spark);
        Child = stack;
    }

    /// <summary>Atualiza o cartão. <paramref name="accent"/> é o nome da cor do tema (Accent, Cyan, Violet, Pink, Amber, Blue).</summary>
    public void Set(string label, string glyph, string accent, MetricSeries series, string value, string unit, string note, double ceiling = 0)
    {
        _label.Text = label;
        _glyph.Text = glyph;
        _value.Text = value;
        _unit.Text = unit;
        _note.Text = note;
        if (_accent != accent)
        {
            _accent = accent;
            _tile.SetResourceReference(BackgroundProperty, accent == "Accent" ? "AccentSoft" : accent + "Soft");
            _glyph.SetResourceReference(TextElement.ForegroundProperty, accent == "Accent" ? "AccentText" : accent);
            _spark.SetResourceReference(Sparkline.StrokeProperty, accent);
        }
        _spark.SetValues(series.Snapshot(), ceiling);
    }
}
