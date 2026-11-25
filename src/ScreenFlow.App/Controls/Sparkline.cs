using System.Windows;
using System.Windows.Media;

namespace ScreenFlow.App.Controls;

/// <summary>Gráfico de linha pequeno com área em degradê (Monitoramento).</summary>
public sealed class Sparkline : FrameworkElement
{
    public static readonly DependencyProperty StrokeProperty =
        DependencyProperty.Register(nameof(Stroke), typeof(Brush), typeof(Sparkline), new FrameworkPropertyMetadata(Brushes.MediumSpringGreen, FrameworkPropertyMetadataOptions.AffectsRender));

    private double[] _values = [];
    private double _ceiling;

    public Brush Stroke
    {
        get => (Brush)GetValue(StrokeProperty);
        set => SetValue(StrokeProperty, value);
    }

    /// <summary>Quantos pontos cabem no gráfico (o mais novo fica na direita).</summary>
    public int Capacity { get; set; } = 90;

    public void SetValues(double[] values, double ceiling = 0)
    {
        _values = values;
        _ceiling = ceiling;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        var width = ActualWidth;
        var height = ActualHeight;
        if (width <= 0 || height <= 0) return;
        var stroke = Stroke;
        var color = stroke is SolidColorBrush solid ? solid.Color : Colors.MediumSpringGreen;

        // Linha de base pontilhada.
        var grid = new Pen(new SolidColorBrush(Color.FromArgb(0x30, color.R, color.G, color.B)), 1) { DashStyle = DashStyles.Dash };
        dc.DrawLine(grid, new Point(0, height - 0.5), new Point(width, height - 0.5));
        if (_values.Length < 2) return;

        var max = Math.Max(_ceiling, _values.Max());
        if (max <= 0) max = 1;
        max *= 1.15;
        var step = width / Math.Max(1, Capacity - 1);
        var startX = width - step * (_values.Length - 1);
        var points = new Point[_values.Length];
        for (var i = 0; i < _values.Length; i++)
            points[i] = new Point(startX + i * step, height - 2 - (height - 4) * Math.Clamp(_values[i] / max, 0, 1));

        var line = new StreamGeometry();
        var area = new StreamGeometry();
        using (var context = line.Open())
        {
            context.BeginFigure(points[0], false, false);
            context.PolyLineTo(points[1..], true, true);
        }
        using (var context = area.Open())
        {
            context.BeginFigure(new Point(points[0].X, height), true, true);
            context.LineTo(points[0], false, false);
            context.PolyLineTo(points[1..], false, true);
            context.LineTo(new Point(points[^1].X, height), false, false);
        }
        line.Freeze();
        area.Freeze();

        var fill = new LinearGradientBrush(Color.FromArgb(0x55, color.R, color.G, color.B), Color.FromArgb(0x00, color.R, color.G, color.B), 90);
        fill.Freeze();
        dc.DrawGeometry(fill, null, area);
        var pen = new Pen(stroke, 2) { LineJoin = PenLineJoin.Round, StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        pen.Freeze();
        dc.DrawGeometry(null, pen, line);
        dc.DrawEllipse(stroke, null, points[^1], 3.5, 3.5);
    }
}
