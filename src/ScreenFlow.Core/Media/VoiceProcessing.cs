namespace ScreenFlow.Core.Media;

/// <summary>
/// Tratamento do microfone antes do Opus (1.5.0). A voz ia do jeito que o microfone entregava: a maioria dos microfones
/// no modo compartilhado do Windows dá a fala em −35 a −45 dBFS, e nada no caminho subia esse nível (o Discord sobe com o
/// controle automático de ganho). Por isso a voz chegava baixa para os outros, mesmo com o volume no máximo. Aqui:
/// <list type="number">
/// <item>ganho manual (o "volume do microfone", de −12 a +24 dB);</item>
/// <item>controle automático de ganho (AGC): mede o nível só enquanto a pessoa fala (portão aberto) e leva a fala para
/// perto de <see cref="TargetDb"/>, subindo devagar e descendo rápido, sem subir o ruído do silêncio;</item>
/// <item>compressão leve: segura gritos e risadas sem achatar a voz;</item>
/// <item>limitador de pico: nada passa de −1 dBFS, então a voz nunca estoura (clipping) no Opus nem em quem ouve.</item>
/// </list>
/// O ganho muda amostra a amostra (rampa), então não há estalos quando ele se ajusta.
/// </summary>
public sealed class VoiceProcessor
{
    /// <summary>Nível da fala que o AGC procura (RMS em dBFS): parecido com o de uma chamada do Discord.</summary>
    public const double TargetDb = -20;

    /// <summary>O AGC sobe no máximo isto (um microfone bem baixo, a −44 dBFS, chega no alvo).</summary>
    public const double MaxBoostDb = 24;

    /// <summary>O AGC desce no máximo isto (microfone muito alto ou perto da boca).</summary>
    public const double MaxCutDb = -12;

    public const double MinManualGainDb = -12;
    public const double MaxManualGainDb = 24;

    /// <summary>Teto do limitador: −1 dBFS.</summary>
    public const float Ceiling = 0.891f;

    /// <summary>Quando o ganho precisa descer, desce em 1 ms (48 amostras): rápido, mas sem degrau.</summary>
    private const int FastRampSamples = 48;

    private readonly SoftCompressor _compressor;
    private readonly PeakLimiter _limiter;
    private double _manualGainDb;
    private float _manualGain = 1f;
    private double? _speechDb;
    private double _agcDb;
    private float _appliedGain = 1f;
    private int _speechFrames;

    public VoiceProcessor(int sampleRate = 48_000)
    {
        // O compressor olha os picos: a fala no alvo (−20 dBFS de RMS) tem picos perto de −10 dBFS e quase não é
        // tocada; gritos e risadas passam disso e descem.
        _compressor = new SoftCompressor(sampleRate, thresholdDb: -10, ratio: 3, attackMs: 3, releaseMs: 100);
        _limiter = new PeakLimiter(sampleRate, Ceiling, releaseMs: 60);
    }

    /// <summary>O ganho manual do microfone em dB (de <see cref="MinManualGainDb"/> a <see cref="MaxManualGainDb"/>).</summary>
    public double ManualGainDb
    {
        get => _manualGainDb;
        set
        {
            _manualGainDb = Math.Clamp(double.IsFinite(value) ? value : 0, MinManualGainDb, MaxManualGainDb);
            _manualGain = (float)DbToGain(_manualGainDb);
        }
    }

    /// <summary>Controle automático de ganho ligado (padrão).</summary>
    public bool AutoGain { get; set; } = true;

    /// <summary>O ganho que o AGC está usando agora (dB).</summary>
    public double AutoGainDb => _agcDb;

    /// <summary>
    /// Primeiro passo, antes do portão de voz: aplica o ganho manual (o portão e o medidor veem o microfone já com ele).
    /// </summary>
    public void ApplyManualGain(Span<float> frame)
    {
        if (Math.Abs(_manualGain - 1f) < 0.0001f) return;
        for (var i = 0; i < frame.Length; i++) frame[i] *= _manualGain;
    }

    /// <summary>
    /// Segundo passo, no quadro que vai pela rede: AGC (que só aprende com <paramref name="speech"/> verdadeiro),
    /// compressão e limitador. Mexe no próprio quadro.
    /// </summary>
    public void Process(Span<float> frame, bool speech)
    {
        if (frame.IsEmpty) return;
        if (AutoGain && speech)
        {
            // O nível que chega (já com o ganho manual, antes do AGC). Só quadros com som de verdade ensinam o AGC:
            // as pausas entre as palavras puxariam a média para baixo.
            var measured = VoiceGate.RmsDb(frame);
            if (measured > -60)
            {
                _speechFrames++;
                // Segue mais rápido quando a fala sobe do que quando desce: a estimativa fica perto dos trechos com voz.
                var current = _speechDb ?? measured;
                var alpha = measured > current ? 0.18 : 0.04;
                _speechDb = current + alpha * (measured - current);
                var wanted = Math.Clamp(TargetDb - _speechDb.Value, MaxCutDb, MaxBoostDb);
                // Sobe devagar (sem "bombear") e desce rápido (sem estourar). Nos primeiros 2 s de fala sobe mais rápido.
                var up = _speechFrames < 100 ? 0.5 : 0.15;
                var step = Math.Clamp(wanted - _agcDb, -1.0, up);
                _agcDb += step;
            }
        }
        else if (!AutoGain) _agcDb = 0;

        // O quadro inteiro já está aqui: se com o ganho de agora ele passaria do teto, o ganho desce já neste quadro
        // (é um "olhar adiante" de graça, sem atraso), e o AGC passa a partir dali.
        var target = (float)DbToGain(_agcDb);
        var peak = 0f;
        foreach (var sample in frame) peak = Math.Max(peak, Math.Abs(sample));
        if (peak * target > Ceiling)
        {
            target = Ceiling / peak;
            if (AutoGain) _agcDb = Math.Max(MaxCutDb, GainToDb(target));
        }
        // Rampa do ganho: subindo, ao longo do quadro; descendo, em 1 ms (rápido para não estourar, sem degrau).
        var start = _appliedGain;
        var count = frame.Length;
        var ramp = target < start ? Math.Min(count, FastRampSamples) : count;
        for (var i = 0; i < count; i++)
        {
            var gain = i < ramp ? start + (target - start) * (i + 1) / ramp : target;
            frame[i] *= gain;
        }
        _appliedGain = target;
        _compressor.Process(frame);
        _limiter.Process(frame);
    }

    /// <summary>Começa de novo (outro microfone, entrou de novo na voz). Mantém o ganho manual.</summary>
    public void Reset()
    {
        _speechDb = null;
        _agcDb = 0;
        _appliedGain = 1f;
        _speechFrames = 0;
        _compressor.Reset();
        _limiter.Reset();
    }

    public static double DbToGain(double db) => Math.Pow(10, db / 20);

    public static double GainToDb(double gain) => gain <= 1e-6 ? -120 : 20 * Math.Log10(gain);
}

/// <summary>
/// Nivelador de quem fala (na recepção, 1.5.0): quem ainda está numa versão sem o AGC (ou com o microfone muito baixo)
/// chega mais alto, até +12 dB; quem chega alto demais desce até −6 dB. Aprende só com a fala (quadros com som) e muda
/// devagar, em rampa. Quem já manda a voz no nível certo (1.5.0) passa praticamente sem mudança.
/// </summary>
public sealed class VoiceLeveler
{
    public const double TargetDb = VoiceProcessor.TargetDb;
    public const double MaxBoostDb = 12;
    public const double MaxCutDb = -6;

    private double? _speechDb;
    private double _gainDb;
    private float _applied = 1f;

    public bool Enabled { get; set; } = true;

    /// <summary>O ganho que está sendo usado agora (dB).</summary>
    public double GainDb => _gainDb;

    /// <summary>Ajusta um quadro decodificado (intercalado ou não; o nível é medido no quadro todo).</summary>
    public void Process(Span<float> samples)
    {
        if (samples.IsEmpty) return;
        if (Enabled)
        {
            var measured = VoiceGate.RmsDb(samples);
            if (measured > -50)
            {
                var current = _speechDb ?? measured;
                var alpha = measured > current ? 0.12 : 0.03;
                _speechDb = current + alpha * (measured - current);
                var wanted = Math.Clamp(TargetDb - _speechDb.Value, MaxCutDb, MaxBoostDb);
                _gainDb += Math.Clamp(wanted - _gainDb, -0.8, 0.2);
            }
        }
        else _gainDb = 0;
        var target = (float)VoiceProcessor.DbToGain(_gainDb);
        if (Math.Abs(target - 1f) < 0.0005f && Math.Abs(_applied - 1f) < 0.0005f) return;
        var start = _applied;
        for (var i = 0; i < samples.Length; i++) samples[i] *= start + (target - start) * (i + 1) / samples.Length;
        _applied = target;
    }
}

/// <summary>Compressor suave (joelho de 6 dB), com envelope de pico. Trabalha amostra a amostra, mono ou intercalado.</summary>
public sealed class SoftCompressor
{
    private readonly double _threshold;
    private readonly double _ratio;
    private readonly double _attack;
    private readonly double _release;
    private double _envelopeDb = -120;

    public SoftCompressor(int sampleRate, double thresholdDb, double ratio, double attackMs, double releaseMs)
    {
        _threshold = thresholdDb;
        _ratio = Math.Max(1, ratio);
        _attack = Math.Exp(-1.0 / (sampleRate * attackMs / 1000.0));
        _release = Math.Exp(-1.0 / (sampleRate * releaseMs / 1000.0));
    }

    private const double Knee = 6;

    /// <summary>Quanto o compressor está tirando agora (dB, ≥ 0).</summary>
    public double ReductionDb { get; private set; }

    public void Process(Span<float> samples)
    {
        for (var i = 0; i < samples.Length; i++)
        {
            var x = samples[i];
            var db = VoiceProcessor.GainToDb(Math.Abs(x));
            var coefficient = db > _envelopeDb ? _attack : _release;
            _envelopeDb = coefficient * _envelopeDb + (1 - coefficient) * db;
            var over = _envelopeDb - _threshold;
            double reduction;
            if (over <= -Knee / 2) reduction = 0;
            else if (over >= Knee / 2) reduction = over * (1 - 1 / _ratio);
            else
            {
                var k = over + Knee / 2;
                reduction = (1 - 1 / _ratio) * k * k / (2 * Knee);
            }
            ReductionDb = reduction;
            if (reduction > 0.001) samples[i] = (float)(x * VoiceProcessor.DbToGain(-reduction));
        }
    }

    public void Reset()
    {
        _envelopeDb = -120;
        ReductionDb = 0;
    }
}

/// <summary>
/// Limitador de pico: o ganho cai na hora em que um pico passaria do teto e volta devagar (sem distorcer a voz nem
/// "bombear"). Garante que nada passe de <paramref name="ceiling"/>. Com <paramref name="channels"/> 2, os dois lados
/// usam o mesmo ganho (a imagem estéreo não muda).
/// </summary>
public sealed class PeakLimiter(int sampleRate, float ceiling = VoiceProcessor.Ceiling, double releaseMs = 80, int channels = 1)
{
    private readonly float _ceiling = Math.Clamp(ceiling, 0.05f, 1f);
    private readonly float _release = (float)Math.Exp(-1.0 / (sampleRate * releaseMs / 1000.0));
    private readonly int _channels = Math.Max(1, channels);
    private float _gain = 1f;

    /// <summary>O ganho de agora (1 = sem limitar).</summary>
    public float Gain => _gain;

    /// <summary>Quantas vezes o limitador precisou agir (para o diagnóstico).</summary>
    public long Engaged { get; private set; }

    public void Process(Span<float> samples)
    {
        for (var i = 0; i + _channels <= samples.Length; i += _channels)
        {
            var peak = 0f;
            for (var c = 0; c < _channels; c++) peak = Math.Max(peak, Math.Abs(samples[i + c]));
            // Volta devagar para 1; desce na hora se este pico passaria do teto.
            _gain = 1f - (1f - _gain) * _release;
            if (peak * _gain > _ceiling)
            {
                _gain = _ceiling / peak;
                Engaged++;
            }
            if (_gain < 0.99999f)
                for (var c = 0; c < _channels; c++) samples[i + c] *= _gain;
        }
    }

    public void Reset() => _gain = 1f;
}

/// <summary>
/// (1.5.0) Separa um buraco de rede de um fim de fala. A voz só vai enquanto a pessoa fala, então o buffer de quem ouve
/// esvazia no fim de cada frase; antes isso contava como falha da rede e o atraso da voz subia 10 ms a cada frase (até
/// 240 ms numa chamada longa). Agora o buffer vazio só vira falha se a voz voltar logo depois (foi a rede que atrasou);
/// se demorar, era só uma pausa.
/// </summary>
public sealed class UnderrunClassifier(TimeSpan? window = null)
{
    private readonly TimeSpan _window = window ?? TimeSpan.FromMilliseconds(120);
    private TimeSpan? _dryAt;

    /// <summary>O buffer esvaziou agora (no relógio de mídia).</summary>
    public void Dry(TimeSpan now) => _dryAt ??= now;

    /// <summary>Chegou som de novo. Devolve o momento da falha quando o buffer vazio era um buraco da rede.</summary>
    public TimeSpan? Wrote(TimeSpan now)
    {
        if (_dryAt is not { } dry) return null;
        _dryAt = null;
        return now - dry <= _window ? dry : null;
    }

    public void Reset() => _dryAt = null;
}

/// <summary>
/// (1.5.0) Curva dos controles de volume do mixer: 0 a 100% vira ganho de 0 a 1 numa curva que acompanha o ouvido
/// (50% soa como metade do volume, uns −10 dB), em vez de uma reta que deixa quase todo o controle no "alto".
/// </summary>
public static class MixerCurve
{
    public const double Exponent = 1.66;

    public static float Gain(int percent, bool muted = false)
    {
        if (muted || percent <= 0) return 0f;
        if (percent >= 100) return 1f;
        return (float)Math.Pow(percent / 100.0, Exponent);
    }

    /// <summary>A porcentagem que dá o mesmo volume que um ganho linear antigo (acima de 1 fica em 100%).</summary>
    public static int PercentFor(double gain)
    {
        if (!double.IsFinite(gain) || gain <= 0) return 0;
        if (gain >= 1) return 100;
        return (int)Math.Round(100 * Math.Pow(gain, 1 / Exponent));
    }
}

/// <summary>
/// (1.5.0) Muda um ganho em rampa ao longo de um trecho de áudio (intercalado com <c>channels</c> canais): mexer num
/// volume, ensurdecer ou mudar o mixer nunca dá um degrau (estalo) no som.
/// </summary>
public static class GainRamp
{
    /// <summary>Multiplica o trecho por um ganho que vai de <paramref name="applied"/> até <paramref name="target"/>.</summary>
    public static void Apply(Span<float> samples, ref float applied, float target, int channels = 1)
    {
        channels = Math.Max(1, channels);
        var frames = samples.Length / channels;
        if (frames == 0) return;
        var start = applied;
        applied = target;
        if (Math.Abs(target - start) < 1e-6f)
        {
            if (Math.Abs(target - 1f) < 1e-6f) return;
            for (var i = 0; i < frames * channels; i++) samples[i] *= target;
            return;
        }
        for (var f = 0; f < frames; f++)
        {
            var gain = start + (target - start) * (f + 1) / frames;
            for (var c = 0; c < channels; c++) samples[f * channels + c] *= gain;
        }
    }
}
