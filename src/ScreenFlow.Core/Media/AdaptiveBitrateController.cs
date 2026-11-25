namespace ScreenFlow.Core.Media;

/// <summary>Medida de rede reportada pelo viewer em janelas de ~1 s.</summary>
public readonly record struct NetworkSample(double LossRate, double RoundTripMs, double ReceivedKbps, double DecodeFps);

public sealed record StreamTarget(int BitrateKbps, int Fps, Resolution Resolution)
{
    public override string ToString() => $"{Resolution.Label} {Fps} FPS {BitrateKbps / 1000.0:0.#} Mbps";
}

/// <summary>
/// Controle de qualidade adaptativo. Quando a rede não aguenta, reduz primeiro o bitrate (até 50% do perfil),
/// depois o FPS e por último a resolução. Sobe de volta devagar quando a rede fica estável.
/// </summary>
public sealed class AdaptiveBitrateController
{
    private readonly StreamTarget _profile;
    private readonly VideoCodec _codec;
    private int _goodStreak;
    private double _baselineRtt = double.NaN;

    public AdaptiveBitrateController(StreamTarget profile, VideoCodec codec)
    {
        _profile = profile;
        _codec = codec;
        Current = profile;
    }

    public StreamTarget Current { get; private set; }
    public string LastReason { get; private set; } = "perfil inicial";

    /// <summary>Recebe uma amostra e retorna o novo alvo quando ele muda; senão null.</summary>
    public StreamTarget? Update(NetworkSample sample)
    {
        if (double.IsNaN(_baselineRtt) || sample.RoundTripMs < _baselineRtt)
            _baselineRtt = sample.RoundTripMs;
        else
            _baselineRtt = _baselineRtt * 0.98 + sample.RoundTripMs * 0.02;

        var congested = sample.LossRate > 0.02 || sample.RoundTripMs > _baselineRtt + 40;
        var viewerSlow = sample.DecodeFps > 0 && sample.DecodeFps < Current.Fps * 0.8;

        if (congested || viewerSlow)
        {
            _goodStreak = 0;
            return Apply(StepDown(viewerSlow && !congested), congested ? $"perda {sample.LossRate:P1}, RTT {sample.RoundTripMs:0} ms" : $"viewer decodifica {sample.DecodeFps:0} FPS");
        }

        if (sample.LossRate < 0.005 && ++_goodStreak >= 5)
        {
            _goodStreak = 0;
            return Apply(StepUp(), "rede estável");
        }
        return null;
    }

    private StreamTarget? Apply(StreamTarget next, string reason)
    {
        if (next == Current) return null;
        Current = next;
        LastReason = reason;
        return next;
    }

    private StreamTarget StepDown(bool decoderBound)
    {
        var floorBitrate = Quality.TargetBitrateKbps(Current.Resolution, Current.Fps, _codec) / 2;
        if (!decoderBound && Current.BitrateKbps > floorBitrate)
            return Current with { BitrateKbps = Math.Max(floorBitrate, (int)(Current.BitrateKbps * 0.8)) };

        var fpsIndex = IndexOfFps(Current.Fps);
        // FPS acima de 60 cai antes da resolução; em 720p o FPS é o único eixo que sobra.
        if (fpsIndex > 0 && (Current.Fps > 60 || Current.Resolution == Resolution.P720))
        {
            var fps = Quality.FrameRates[fpsIndex - 1];
            return Current with { Fps = fps, BitrateKbps = Math.Min(Current.BitrateKbps, Quality.TargetBitrateKbps(Current.Resolution, fps, _codec)) };
        }

        var resIndex = IndexOfResolution(Current.Resolution);
        if (resIndex > 0)
        {
            var resolution = Resolution.All[resIndex - 1];
            return Current with { Resolution = resolution, BitrateKbps = Math.Min(Current.BitrateKbps, Quality.TargetBitrateKbps(resolution, Current.Fps, _codec)) };
        }

        if (fpsIndex > 0)
            return Current with { Fps = Quality.FrameRates[fpsIndex - 1] };
        return Current with { BitrateKbps = Math.Max(1_500, (int)(Current.BitrateKbps * 0.8)) };
    }

    private StreamTarget StepUp()
    {
        // Sobe na ordem inversa: resolução, depois FPS, depois bitrate, nunca acima do perfil escolhido.
        var resIndex = IndexOfResolution(Current.Resolution);
        if (resIndex < IndexOfResolution(_profile.Resolution))
        {
            var resolution = Resolution.All[resIndex + 1];
            return Current with { Resolution = resolution };
        }
        var fpsIndex = IndexOfFps(Current.Fps);
        if (Current.Fps < _profile.Fps)
        {
            var nextFps = Math.Min(_profile.Fps, Quality.FrameRates[Math.Min(fpsIndex + 1, Quality.FrameRates.Count - 1)]);
            return Current with { Fps = nextFps };
        }
        if (Current.BitrateKbps < _profile.BitrateKbps)
            return Current with { BitrateKbps = Math.Min(_profile.BitrateKbps, (int)(Current.BitrateKbps * 1.1) + 500) };
        return Current;
    }

    private static int IndexOfFps(int fps)
    {
        var index = 0;
        for (var i = 0; i < Quality.FrameRates.Count; i++) if (Quality.FrameRates[i] <= fps) index = i;
        return index;
    }

    private static int IndexOfResolution(Resolution resolution)
    {
        var index = 0;
        for (var i = 0; i < Resolution.All.Count; i++) if (Resolution.All[i].Pixels <= resolution.Pixels) index = i;
        return index;
    }
}
