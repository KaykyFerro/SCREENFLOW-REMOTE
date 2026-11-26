using System.Diagnostics;

namespace ScreenFlow.Core.Media;

/// <summary>Relógio monotônico em microssegundos, usado nos timestamps de captura e na sincronia A/V.</summary>
public static class MediaClock
{
    private static readonly long Origin = Stopwatch.GetTimestamp();
    public static long NowUs => (long)((Stopwatch.GetTimestamp() - Origin) * (1_000_000.0 / Stopwatch.Frequency));
    public static TimeSpan Now => TimeSpan.FromTicks((long)((Stopwatch.GetTimestamp() - Origin) * (TimeSpan.TicksPerSecond / (double)Stopwatch.Frequency)));
}

/// <summary>Estima o deslocamento entre o relógio do host e o do viewer (estilo NTP), para medir latência ponta a ponta.</summary>
public sealed class ClockSync
{
    private double _offsetUs = double.NaN;
    private double _bestRttUs = double.MaxValue;

    public double RoundTripMs { get; private set; }
    public bool HasEstimate => !double.IsNaN(_offsetUs);

    /// <summary>offset = hostTime - viewerTime no instante médio do ping.</summary>
    public void AddSample(long viewerSentUs, long hostUs, long viewerReceivedUs)
    {
        var rtt = viewerReceivedUs - viewerSentUs;
        if (rtt < 0) return;
        RoundTripMs = RoundTripMs == 0 ? rtt / 1000.0 : RoundTripMs * 0.8 + rtt / 1000.0 * 0.2;
        var offset = hostUs - (viewerSentUs + rtt / 2.0);
        // Amostras com RTT menor são mais confiáveis; o melhor RTT envelhece devagar.
        _bestRttUs *= 1.02;
        if (rtt <= _bestRttUs || double.IsNaN(_offsetUs))
        {
            _bestRttUs = rtt;
            _offsetUs = double.IsNaN(_offsetUs) ? offset : _offsetUs * 0.5 + offset * 0.5;
        }
    }

    public long HostToViewerUs(long hostUs) => HasEstimate ? (long)(hostUs - _offsetUs) : hostUs;
}

/// <summary>Contador de taxa em janela deslizante de 1 s.</summary>
public sealed class RateMeter
{
    private readonly Queue<(long T, long V)> _samples = new();
    private long _sum;
    private readonly object _gate = new();

    public void Add(long value, long nowUs)
    {
        lock (_gate)
        {
            _samples.Enqueue((nowUs, value));
            _sum += value;
            Trim(nowUs);
        }
    }

    /// <summary>Soma dos valores no último segundo.</summary>
    public long PerSecond(long nowUs)
    {
        lock (_gate)
        {
            Trim(nowUs);
            return _sum;
        }
    }

    private void Trim(long nowUs)
    {
        while (_samples.Count > 0 && nowUs - _samples.Peek().T > 1_000_000) _sum -= _samples.Dequeue().V;
    }
}

public sealed record ViewerStatistics(
    double Fps,
    double BitrateMbps,
    double LatencyMs,
    double RoundTripMs,
    double LossRate,
    string Codec,
    int Width,
    int Height,
    string Decoder,
    long FramesDropped,
    long FecRecovered,
    string QualityReason,
    StreamMode Mode = StreamMode.Screen,
    double AudioBufferMs = 0,
    long AudioRecovered = 0,
    double AudioKbps = 0,
    bool GpuRendering = false,
    double CameraFps = 0,
    int CameraWidth = 0,
    int CameraHeight = 0)
{
    /// <summary>Perda de pacotes desde que conectou (o <see cref="LossRate"/> é só o último segundo).</summary>
    public double TotalLossRate { get; init; }

    /// <summary>Frames que chegaram em parte e venceram o prazo (o NACK não conseguiu completar).</summary>
    public long FramesExpired { get; init; }

    /// <summary>Frames dos quais nenhum pacote chegou.</summary>
    public long FramesMissing { get; init; }

    /// <summary>Frames presos atrás de um incompleto, pulados porque um keyframe mais novo já tinha chegado.</summary>
    public long FramesSuperseded { get; init; }

    /// <summary>Pacotes perdidos que voltaram por retransmissão (NACK).</summary>
    public long NackRecovered { get; init; }

    /// <summary>Frames descartados porque o decoder não acompanhou (fila cheia).</summary>
    public long DecoderLate { get; init; }
}
