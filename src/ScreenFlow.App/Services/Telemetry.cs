using ScreenFlow.Infrastructure.Session;
using ScreenFlow.Core.Media;

namespace ScreenFlow.App.Services;

/// <summary>Série dos últimos N valores (um por segundo) para os gráficos do Monitoramento.</summary>
public sealed class MetricSeries(int capacity = 90)
{
    private readonly double[] _values = new double[capacity];
    private int _count;
    private int _next;

    public int Count => _count;
    public double Last => _count == 0 ? 0 : _values[(_next - 1 + _values.Length) % _values.Length];

    public void Add(double value)
    {
        _values[_next] = double.IsFinite(value) ? value : 0;
        _next = (_next + 1) % _values.Length;
        if (_count < _values.Length) _count++;
    }

    public void Clear() => _count = _next = 0;

    /// <summary>Copia em ordem (mais antigo primeiro).</summary>
    public double[] Snapshot()
    {
        var result = new double[_count];
        var start = (_next - _count + _values.Length) % _values.Length;
        for (var i = 0; i < _count; i++) result[i] = _values[(start + i) % _values.Length];
        return result;
    }

    public double Max()
    {
        var max = 0.0;
        for (var i = 0; i < _count; i++) max = Math.Max(max, _values[(_next - 1 - i + _values.Length * 2) % _values.Length]);
        return max;
    }
}

/// <summary>Histórico das estatísticas de transmissão e de quem assiste. Atualizado na thread da interface.</summary>
public static class Telemetry
{
    public static MetricSeries HostFps { get; } = new();
    public static MetricSeries HostVideoMbps { get; } = new();
    public static MetricSeries HostUploadMbps { get; } = new();
    public static MetricSeries HostViewers { get; } = new();
    public static MetricSeries HostAudioKbps { get; } = new();
    public static MetricSeries HostWorstPing { get; } = new();

    public static MetricSeries ViewerFps { get; } = new();
    public static MetricSeries ViewerMbps { get; } = new();
    public static MetricSeries ViewerLatency { get; } = new();
    public static MetricSeries ViewerPing { get; } = new();
    public static MetricSeries ViewerLoss { get; } = new();
    public static MetricSeries ViewerAudioBuffer { get; } = new();

    public static HostStatistics? Host { get; private set; }
    public static IReadOnlyList<ViewerInfo> HostAudience { get; private set; } = [];
    public static ViewerStatistics? Viewer { get; private set; }
    public static string? ViewerHost { get; private set; }

    public static bool Hosting => Host is not null;
    public static bool Watching => Viewer is not null;

    public static event Action? Updated;

    public static void Record(HostStatistics stats, IReadOnlyList<ViewerInfo> audience)
    {
        Host = stats;
        HostAudience = audience;
        HostFps.Add(stats.Viewers == 0 ? 0 : stats.EncodeFps);
        HostVideoMbps.Add(stats.Viewers == 0 ? 0 : stats.BitrateMbps);
        HostUploadMbps.Add(stats.UploadMbps);
        HostViewers.Add(stats.Viewers);
        HostAudioKbps.Add(stats.AudioKbps);
        HostWorstPing.Add(audience.Count == 0 ? 0 : audience.Max(v => v.RoundTripMs));
        Updated?.Invoke();
    }

    public static void Record(ViewerStatistics stats, string host)
    {
        Viewer = stats;
        ViewerHost = host;
        ViewerFps.Add(stats.Fps);
        ViewerMbps.Add(stats.Mode == StreamMode.AudioOnly ? stats.AudioKbps / 1000.0 : stats.BitrateMbps);
        ViewerLatency.Add(stats.LatencyMs);
        ViewerPing.Add(stats.RoundTripMs);
        ViewerLoss.Add(stats.LossRate * 100);
        ViewerAudioBuffer.Add(stats.AudioBufferMs);
        Updated?.Invoke();
    }

    public static void EndHost()
    {
        Host = null;
        HostAudience = [];
        foreach (var series in new[] { HostFps, HostVideoMbps, HostUploadMbps, HostViewers, HostAudioKbps, HostWorstPing }) series.Clear();
        Updated?.Invoke();
    }

    public static void EndViewer()
    {
        Viewer = null;
        ViewerHost = null;
        foreach (var series in new[] { ViewerFps, ViewerMbps, ViewerLatency, ViewerPing, ViewerLoss, ViewerAudioBuffer }) series.Clear();
        Updated?.Invoke();
    }
}
