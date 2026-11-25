using System.Windows;
using System.Windows.Media;
using ScreenFlow.Core.Media;

namespace ScreenFlow.App.Controls;

/// <summary>
/// Barras do espectro do áudio ao vivo (festa de música e painel da transmissão). Lê o <see cref="AudioMeter"/> a cada
/// quadro da tela, só enquanto está visível. As cores seguem a posição: menta nos graves, ciano, violeta e rosa nos agudos.
/// </summary>
public sealed class SpectrumVisualizer : FrameworkElement
{
    public static readonly DependencyProperty BarGapProperty =
        DependencyProperty.Register(nameof(BarGap), typeof(double), typeof(SpectrumVisualizer), new FrameworkPropertyMetadata(4.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty MirrorProperty =
        DependencyProperty.Register(nameof(Mirror), typeof(bool), typeof(SpectrumVisualizer), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    private static readonly Color[] Palette =
    [
        Color.FromRgb(0x2E, 0xE6, 0xA6), Color.FromRgb(0x22, 0xD3, 0xEE), Color.FromRgb(0x8B, 0x6C, 0xFF), Color.FromRgb(0xFF, 0x4D, 0x8D),
    ];

    private float[] _bands = [];
    private float[] _shown = [];
    private float[] _peaks = [];
    private long _lastUpdates = -1;
    private bool _hooked;
    private LinearGradientBrush? _brush;
    private SolidColorBrush? _idle;
    private double _brushWidth;

    public SpectrumVisualizer()
    {
        IsVisibleChanged += (_, _) => Hook(IsVisible);
        Unloaded += (_, _) => Hook(false);
        Loaded += (_, _) => Hook(IsVisible);
        SnapsToDevicePixels = true;
    }

    /// <summary>De onde ler o medidor (pode mudar no meio, por exemplo quando o player de áudio é recriado).</summary>
    public Func<AudioMeter?>? Source { get; set; }

    public double BarGap
    {
        get => (double)GetValue(BarGapProperty);
        set => SetValue(BarGapProperty, value);
    }

    /// <summary>Desenha um reflexo suave abaixo das barras.</summary>
    public bool Mirror
    {
        get => (bool)GetValue(MirrorProperty);
        set => SetValue(MirrorProperty, value);
    }

    /// <summary>Nível geral (0 a 1) do último quadro, para animar outros elementos.</summary>
    public double Level { get; private set; }

    private void Hook(bool on)
    {
        if (on == _hooked) return;
        _hooked = on;
        if (on) CompositionTarget.Rendering += OnRendering;
        else CompositionTarget.Rendering -= OnRendering;
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        var meter = Source?.Invoke();
        if (meter is null)
        {
            if (_shown.Length > 0 && _shown.Max() > 0.001f)
            {
                for (var i = 0; i < _shown.Length; i++) _shown[i] *= 0.85f;
                InvalidateVisual();
            }
            Level = 0;
            return;
        }
        if (_bands.Length != meter.BandCount)
        {
            _bands = new float[meter.BandCount];
            _shown = new float[meter.BandCount];
            _peaks = new float[meter.BandCount];
        }
        // Sem áudio novo desde o último quadro: deixa cair devagar em vez de congelar.
        var updates = meter.Updates;
        if (updates == _lastUpdates) meter.Decay(0.94f);
        _lastUpdates = updates;
        meter.CopyBands(_bands);
        for (var i = 0; i < _bands.Length; i++)
        {
            var target = _bands[i];
            _shown[i] = target > _shown[i] ? _shown[i] + (target - _shown[i]) * 0.6f : _shown[i] + (target - _shown[i]) * 0.18f;
            _peaks[i] = Math.Max(_peaks[i] - 0.008f, _shown[i]);
        }
        Level = meter.Level;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        var width = ActualWidth;
        var height = ActualHeight;
        if (width <= 0 || height <= 0) return;
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, width, height));

        var count = _shown.Length > 0 ? _shown.Length : 48;
        var gap = BarGap;
        var barWidth = Math.Max(2, (width - gap * (count - 1)) / count);
        var radius = Math.Min(barWidth / 2, 4);
        var mirror = Mirror;
        var baseline = mirror ? height * 0.78 : height;
        var maxBar = baseline - 2;

        if (_brush is null || Math.Abs(_brushWidth - width) > 0.5)
        {
            _brush = new LinearGradientBrush { MappingMode = BrushMappingMode.Absolute, StartPoint = new Point(0, 0), EndPoint = new Point(width, 0) };
            for (var i = 0; i < Palette.Length; i++) _brush.GradientStops.Add(new GradientStop(Palette[i], i / (double)(Palette.Length - 1)));
            _brush.Freeze();
            _brushWidth = width;
        }
        _idle ??= new SolidColorBrush(Color.FromArgb(0x40, 0x8B, 0x9B, 0xC4));

        for (var i = 0; i < count; i++)
        {
            var value = i < _shown.Length ? _shown[i] : 0f;
            var x = i * (barWidth + gap);
            var h = Math.Max(3, value * maxBar);
            Brush brush = value < 0.02f ? _idle : _brush;
            dc.DrawRoundedRectangle(brush, null, new Rect(x, baseline - h, barWidth, h), radius, radius);
            if (i < _peaks.Length && _peaks[i] > 0.04f)
            {
                var py = baseline - _peaks[i] * maxBar - 5;
                dc.DrawRoundedRectangle(_brush, null, new Rect(x, Math.Max(0, py), barWidth, 2.5), 1.25, 1.25);
            }
            if (mirror && value >= 0.02f)
            {
                var reflection = Math.Min(height - baseline - 2, h * 0.35);
                dc.PushOpacity(0.18);
                dc.DrawRoundedRectangle(_brush, null, new Rect(x, baseline + 3, barWidth, reflection), radius, radius);
                dc.Pop();
            }
        }
    }
}
