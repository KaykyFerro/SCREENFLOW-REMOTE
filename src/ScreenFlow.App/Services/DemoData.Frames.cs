using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ScreenFlow.App.Services;

/// <summary>Imagens de exemplo para o player nas capturas: o CI não tem tela de jogo nem webcam para transmitir.</summary>
public static partial class DemoData
{
    /// <summary>Um jogo de corrida de exemplo (1280×720) para o vídeo da tela.</summary>
    public static BitmapSource ScreenFrame() => Render(1280, 720, DrawRace);

    /// <summary>Uma webcam de exemplo (640×360) para o quadrinho da câmera.</summary>
    public static BitmapSource CameraFrame() => Render(640, 360, DrawWebcam);

    private static BitmapSource Render(int width, int height, Action<DrawingContext, double, double> draw)
    {
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen()) draw(context, width, height);
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    private static void DrawRace(DrawingContext dc, double w, double h)
    {
        var horizon = h * 0.6;
        var center = w / 2;

        // Céu de fim de tarde, sol e montanhas
        dc.DrawRectangle(Gradient(90, ("#141A46", 0), ("#4B2F7F", 0.55), ("#FF8A5B", 1)), null, new Rect(0, 0, w, horizon));
        var sun = new Point(w * 0.68, horizon - 70);
        dc.DrawEllipse(Radial(("#FFFFE7A3", 0), ("#B3FFB347", 0.3), ("#00FF8A5B", 1)), null, sun, 240, 240);
        dc.DrawEllipse(Solid("#FFE7A3"), null, sun, 68, 68);
        dc.DrawGeometry(Solid("#5A3D8F"), null, Mountains(w, horizon, 0.2, 130, 6));
        dc.DrawGeometry(Solid("#2C2258"), null, Mountains(w, horizon, 1.1, 80, 9));

        // Chão e pista com a faixa do meio tracejada
        dc.DrawRectangle(Gradient(90, ("#1C1838", 0), ("#0A0918", 1)), null, new Rect(0, horizon, w, h - horizon));
        dc.DrawGeometry(Solid("#2A2744"), null, Polygon(new(center - 40, horizon), new(center + 40, horizon), new(w * 0.94, h), new(w * 0.06, h)));
        var edge = new Pen(Solid("#FF4D8D"), 5);
        edge.Freeze();
        dc.DrawLine(edge, new Point(center - 40, horizon), new Point(w * 0.06, h));
        dc.DrawLine(edge, new Point(center + 40, horizon), new Point(w * 0.94, h));
        for (var i = 0; i < 8; i++)
        {
            var t0 = Math.Pow(i / 8.0, 1.8);
            var t1 = Math.Pow((i + 0.5) / 8.0, 1.8);
            double y0 = horizon + (h - horizon) * t0, y1 = horizon + (h - horizon) * t1;
            double half0 = 1.5 + 11 * t0, half1 = 1.5 + 11 * t1;
            dc.DrawGeometry(Solid("#FFE7A3"), null, Polygon(new(center - half0, y0), new(center + half0, y0), new(center + half1, y1), new(center - half1, y1)));
        }

        // Carro visto de trás
        var bottom = h - 46;
        dc.DrawEllipse(Solid("#99000000"), null, new Point(center, bottom + 8), 225, 24);
        dc.DrawRoundedRectangle(Solid("#0B0B14"), null, new Rect(center - 205, bottom - 50, 64, 54), 10, 10);
        dc.DrawRoundedRectangle(Solid("#0B0B14"), null, new Rect(center + 141, bottom - 50, 64, 54), 10, 10);
        dc.DrawGeometry(Solid("#241B3D"), null, Polygon(new(center - 128, bottom - 205), new(center + 128, bottom - 205), new(center + 168, bottom - 140), new(center - 168, bottom - 140)));
        dc.DrawGeometry(Gradient(90, ("#5E6BA8", 0), ("#2B3160", 1)), null, Polygon(new(center - 112, bottom - 195), new(center + 112, bottom - 195), new(center + 146, bottom - 146), new(center - 146, bottom - 146)));
        dc.DrawRoundedRectangle(Gradient(90, ("#FF5D73", 0), ("#B0174F", 1)), null, new Rect(center - 210, bottom - 150, 420, 118), 30, 30);
        dc.DrawRoundedRectangle(Solid("#1B1F3B"), null, new Rect(center - 226, bottom - 162, 452, 16), 6, 6);
        foreach (var x in new[] { center - 192, center + 100 })
        {
            dc.DrawEllipse(Radial(("#99FFE066", 0), ("#00FFE066", 1)), null, new Point(x + 46, bottom - 112), 90, 40);
            dc.DrawRoundedRectangle(Solid("#FFE066"), null, new Rect(x, bottom - 124, 92, 24), 10, 10);
        }
        dc.DrawRoundedRectangle(Solid("#F5F7FF"), null, new Rect(center - 58, bottom - 84, 116, 32), 6, 6);
        Text(dc, "SF·110", 20, "#1B1F3B", FontWeights.Bold, new Point(center, bottom - 82), centered: true);

        // Placar: volta, posição, tempo e velocidade
        dc.DrawRoundedRectangle(Solid("#B30B1224"), null, new Rect(32, 28, 330, 96), 16, 16);
        Text(dc, "VOLTA", 14, "#9AA6C8", FontWeights.SemiBold, new Point(54, 42));
        Text(dc, "2/3", 36, "#FFFFFF", FontWeights.Bold, new Point(52, 60));
        Text(dc, "POSIÇÃO", 14, "#9AA6C8", FontWeights.SemiBold, new Point(144, 42));
        Text(dc, "1º", 36, "#2EE6A6", FontWeights.Bold, new Point(142, 60));
        Text(dc, "TEMPO", 14, "#9AA6C8", FontWeights.SemiBold, new Point(234, 42));
        Text(dc, "1:42.3", 30, "#FFFFFF", FontWeights.Bold, new Point(232, 64));
        dc.DrawRoundedRectangle(Solid("#B30B1224"), null, new Rect(w - 236, h - 142, 204, 110), 18, 18);
        Text(dc, "212", 60, "#FFFFFF", FontWeights.Bold, new Point(w - 216, h - 140));
        Text(dc, "km/h", 18, "#FF4D8D", FontWeights.SemiBold, new Point(w - 104, h - 84));
    }

    private static void DrawWebcam(DrawingContext dc, double w, double h)
    {
        var cx = w * 0.5;

        // Quarto com luz de abajur, um quadro e uma fita de LED
        dc.DrawRectangle(Gradient(90, ("#3A2E52", 0), ("#1E1830", 1)), null, new Rect(0, 0, w, h));
        dc.DrawEllipse(Radial(("#99FFB86B", 0), ("#00FFB86B", 1)), null, new Point(w * 0.1, h * 0.18), 240, 220);
        dc.DrawRoundedRectangle(Solid("#26FFFFFF"), null, new Rect(w * 0.72, h * 0.1, 124, 84), 6, 6);
        dc.DrawRoundedRectangle(Gradient(45, ("#8B6CFF", 0), ("#22D3EE", 1)), null, new Rect(w * 0.72 + 10, h * 0.1 + 10, 104, 64), 4, 4);
        dc.DrawRectangle(Gradient(0, ("#00FF4D8D", 0), ("#CCFF4D8D", 0.5), ("#008B6CFF", 1)), null, new Rect(0, h * 0.06, w, 5));

        // Pessoa de moletom com fone de ouvido
        dc.DrawEllipse(Gradient(90, ("#2EE6A6", 0), ("#178A70", 1)), null, new Point(cx, h + 46), 200, 136);
        dc.DrawRoundedRectangle(Solid("#D9A07A"), null, new Rect(cx - 26, h * 0.55, 52, 74), 18, 18);
        dc.DrawEllipse(Solid("#2B1C14"), null, new Point(cx, h * 0.38), 70, 78);
        dc.DrawEllipse(Solid("#F0BE96"), null, new Point(cx, h * 0.46), 60, 70);
        dc.DrawEllipse(Solid("#2B1C14"), null, new Point(cx - 14, h * 0.29), 54, 24);
        dc.DrawEllipse(Solid("#2B1C14"), null, new Point(cx - 22, h * 0.45), 5, 6.5);
        dc.DrawEllipse(Solid("#2B1C14"), null, new Point(cx + 22, h * 0.45), 5, 6.5);
        var smile = new Pen(Solid("#8A3B2E"), 4) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        smile.Freeze();
        dc.DrawGeometry(null, smile, Path(new(cx - 20, h * 0.54), new ArcSegment(new Point(cx + 20, h * 0.54), new Size(26, 20), 0, false, SweepDirection.Counterclockwise, true)));
        var band = new Pen(Solid("#15151F"), 12) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        band.Freeze();
        dc.DrawGeometry(null, band, Path(new(cx - 70, h * 0.44), new ArcSegment(new Point(cx + 70, h * 0.44), new Size(70, 92), 0, false, SweepDirection.Clockwise, true)));
        var accent = new Pen(Solid("#2EE6A6"), 2);
        accent.Freeze();
        dc.DrawRoundedRectangle(Solid("#1B1B28"), accent, new Rect(cx - 86, h * 0.37, 28, 56), 10, 10);
        dc.DrawRoundedRectangle(Solid("#1B1B28"), accent, new Rect(cx + 58, h * 0.37, 28, 56), 10, 10);
        var boom = new Pen(Solid("#15151F"), 5) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        boom.Freeze();
        dc.DrawGeometry(null, boom, Path(new(cx - 74, h * 0.50), new BezierSegment(new Point(cx - 76, h * 0.60), new Point(cx - 46, h * 0.62), new Point(cx - 24, h * 0.60), true)));
        dc.DrawEllipse(Solid("#15151F"), null, new Point(cx - 22, h * 0.60), 8, 6);
    }

    private static Color Hex(string hex) => (Color)ColorConverter.ConvertFromString(hex);

    private static SolidColorBrush Solid(string hex)
    {
        var brush = new SolidColorBrush(Hex(hex));
        brush.Freeze();
        return brush;
    }

    private static LinearGradientBrush Gradient(double angle, params (string Color, double Offset)[] stops)
    {
        var brush = new LinearGradientBrush(new GradientStopCollection(stops.Select(s => new GradientStop(Hex(s.Color), s.Offset))), angle);
        brush.Freeze();
        return brush;
    }

    private static RadialGradientBrush Radial(params (string Color, double Offset)[] stops)
    {
        var brush = new RadialGradientBrush(new GradientStopCollection(stops.Select(s => new GradientStop(Hex(s.Color), s.Offset))));
        brush.Freeze();
        return brush;
    }

    private static Geometry Polygon(params Point[] points)
    {
        var figure = new PathFigure(points[0], points.Skip(1).Select(p => (PathSegment)new LineSegment(p, true)), true);
        var geometry = new PathGeometry([figure]);
        geometry.Freeze();
        return geometry;
    }

    private static Geometry Path(Point start, PathSegment segment)
    {
        var geometry = new PathGeometry([new PathFigure(start, [segment], false) { IsFilled = false }]);
        geometry.Freeze();
        return geometry;
    }

    /// <summary>Uma serra de montanhas entre a esquerda e a direita da imagem, apoiada em <paramref name="baseY"/>.</summary>
    private static Geometry Mountains(double width, double baseY, double phase, double height, int peaks)
    {
        var points = new List<Point> { new(0, baseY) };
        var step = width / (peaks * 2);
        for (var i = 0; i <= peaks * 2; i++)
        {
            var peak = i % 2 == 1;
            var rise = peak ? height * (0.55 + 0.45 * Math.Abs(Math.Sin(i * 1.7 + phase * 3))) : height * 0.2 * Math.Abs(Math.Cos(i + phase));
            points.Add(new Point(i * step, baseY - rise));
        }
        points.Add(new Point(width, baseY));
        return Polygon([.. points]);
    }

    private static void Text(DrawingContext dc, string text, double size, string color, FontWeight weight, Point at, bool centered = false)
    {
        var typeface = new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, weight, FontStretches.Normal);
        var formatted = new FormattedText(text, CultureInfo.GetCultureInfo("pt-BR"), FlowDirection.LeftToRight, typeface, size, Solid(color), 1.0);
        dc.DrawText(formatted, centered ? new Point(at.X - formatted.Width / 2, at.Y) : at);
    }
}
