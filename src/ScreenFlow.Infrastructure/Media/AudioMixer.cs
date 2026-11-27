using NAudio.Wave;
using ScreenFlow.Core.Media;
using ScreenFlow.Core.Settings;

namespace ScreenFlow.Infrastructure.Media;

/// <summary>Os três tipos de som do ScreenFlow que têm volume próprio no mixer (1.5.0).</summary>
public enum AudioBusKind { Voip, Streams, AppSounds }

/// <summary>
/// Um dos volumes do mixer: um ganho que só este tipo de som usa. A voz da sala passa pelo <see cref="AudioMixer.Voip"/>,
/// o som das transmissões pelo <see cref="AudioMixer.Streams"/> e os avisos, mensagens de voz e figurinhas pelo
/// <see cref="AudioMixer.AppSounds"/>. Eles só se juntam na saída (no mixer do Windows), então um nunca muda o outro, e
/// o volume geral do Windows não é tocado.
/// </summary>
public sealed class AudioBus(AudioBusKind kind)
{
    private volatile float _gain = 1f;

    public AudioBusKind Kind { get; } = kind;

    /// <summary>O ganho de agora (0 a 1), já na curva do ouvido (<see cref="MixerCurve"/>).</summary>
    public float Gain => _gain;

    public int Percent { get; private set; } = 100;
    public bool Muted { get; private set; }

    /// <summary>O volume mudou (chamado na thread de quem mudou).</summary>
    public event Action<AudioBus>? Changed;

    public void Set(int percent, bool muted)
    {
        percent = Math.Clamp(percent, 0, 100);
        if (percent == Percent && muted == Muted) return;
        Percent = percent;
        Muted = muted;
        _gain = MixerCurve.Gain(percent, muted);
        Changed?.Invoke(this);
    }
}

/// <summary>O mixer do ScreenFlow (1.5.0): os três volumes, aplicados na hora em tudo que está tocando.</summary>
public static class AudioMixer
{
    public static AudioBus Voip { get; } = new(AudioBusKind.Voip);
    public static AudioBus Streams { get; } = new(AudioBusKind.Streams);
    public static AudioBus AppSounds { get; } = new(AudioBusKind.AppSounds);

    public static AudioBus Bus(AudioBusKind kind) => kind switch
    {
        AudioBusKind.Voip => Voip,
        AudioBusKind.Streams => Streams,
        _ => AppSounds,
    };

    public static void Apply(AudioMixerSettings? settings)
    {
        settings ??= new AudioMixerSettings();
        Voip.Set(settings.VoipPercent, settings.VoipMuted);
        Streams.Set(settings.StreamsPercent, settings.StreamsMuted);
        AppSounds.Set(settings.AppSoundsPercent, settings.AppSoundsMuted);
    }
}

/// <summary>
/// Aplica um ganho que pode mudar a qualquer momento (o volume de um <see cref="AudioBus"/>, ensurdecer, o volume de
/// uma pessoa), sempre em rampa ao longo de cada leitura: mudar o volume não estala.
/// </summary>
internal sealed class GainSampleProvider(ISampleProvider source, Func<float> gain) : ISampleProvider
{
    private float _applied = gain();

    public GainSampleProvider(ISampleProvider source, AudioBus bus) : this(source, () => bus.Gain) { }

    public WaveFormat WaveFormat => source.WaveFormat;

    public int Read(float[] buffer, int offset, int count)
    {
        var read = source.Read(buffer, offset, count);
        if (read > 0) GainRamp.Apply(buffer.AsSpan(offset, read), ref _applied, gain(), WaveFormat.Channels);
        return read;
    }
}

/// <summary>
/// Limitador no fim do mix (1.5.0): várias vozes juntas, com o volume de alguém em 200%, passavam de 0 dBFS e
/// estouravam (clipping). Agora nada passa de <paramref name="ceiling"/>; o resto do som não muda.
/// </summary>
internal sealed class LimiterSampleProvider(ISampleProvider source, float ceiling = 0.97f) : ISampleProvider
{
    private readonly PeakLimiter _limiter = new(source.WaveFormat.SampleRate, ceiling, releaseMs: 80, channels: source.WaveFormat.Channels);

    public WaveFormat WaveFormat => source.WaveFormat;

    /// <summary>Quantas vezes o limitador precisou agir (para o diagnóstico).</summary>
    public long Engaged => _limiter.Engaged;

    public int Read(float[] buffer, int offset, int count)
    {
        var read = source.Read(buffer, offset, count);
        if (read > 0) _limiter.Process(buffer.AsSpan(offset, read));
        return read;
    }
}
