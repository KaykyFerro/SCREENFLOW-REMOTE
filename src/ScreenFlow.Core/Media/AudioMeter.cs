namespace ScreenFlow.Core.Media;

/// <summary>
/// Nível e espectro do áudio para os visualizadores (festa de música, medidor na transmissão).
/// Recebe amostras estéreo intercaladas, roda uma FFT de 2048 pontos a cada 1024 amostras novas e agrupa
/// as frequências em bandas logarítmicas de 40 Hz a 16 kHz, já normalizadas de 0 a 1 (−60 dB a 0 dB).
/// Escrita numa thread (captura ou rede) e leitura na thread da interface.
/// </summary>
public sealed class AudioMeter
{
    public const int FftSize = 2048;
    private const int Hop = FftSize / 2;
    private const float FloorDb = -60f;

    private readonly object _gate = new();
    private readonly float[] _ring = new float[FftSize];
    private readonly float[] _window = new float[FftSize];
    private readonly float[] _re = new float[FftSize];
    private readonly float[] _im = new float[FftSize];
    private readonly float[] _cos = new float[FftSize / 2];
    private readonly float[] _sin = new float[FftSize / 2];
    private readonly int[] _reverse = new int[FftSize];
    private readonly int[] _bandStart;
    private readonly int[] _bandEnd;
    private readonly float[] _bands;
    private int _ringPosition;
    private int _sinceFft;
    private double _sumSquares;
    private int _sumCount;
    private float _level;
    private float _peak;
    private long _updates;

    public AudioMeter(int bandCount = 48, int sampleRate = AudioProfile.SampleRate, float minHz = 40, float maxHz = 16_000)
    {
        if (bandCount is < 4 or > 256) throw new ArgumentOutOfRangeException(nameof(bandCount));
        BandCount = bandCount;
        SampleRate = sampleRate;
        _bands = new float[bandCount];
        _bandStart = new int[bandCount];
        _bandEnd = new int[bandCount];

        for (var i = 0; i < FftSize; i++)
            _window[i] = 0.5f * (1 - MathF.Cos(2 * MathF.PI * i / (FftSize - 1)));
        for (var i = 0; i < FftSize / 2; i++)
        {
            _cos[i] = MathF.Cos(2 * MathF.PI * i / FftSize);
            _sin[i] = -MathF.Sin(2 * MathF.PI * i / FftSize);
        }
        var bits = (int)Math.Log2(FftSize);
        for (var i = 0; i < FftSize; i++)
        {
            var r = 0;
            for (var b = 0; b < bits; b++) r |= ((i >> b) & 1) << (bits - 1 - b);
            _reverse[i] = r;
        }

        // Bandas logarítmicas; cada uma cobre pelo menos um bin da FFT.
        var binHz = sampleRate / (double)FftSize;
        var maxBin = FftSize / 2 - 1;
        var previousEnd = Math.Max(1, (int)(minHz / binHz));
        for (var i = 0; i < bandCount; i++)
        {
            var high = minHz * Math.Pow(maxHz / minHz, (i + 1) / (double)bandCount);
            var start = Math.Min(previousEnd, maxBin);
            var end = Math.Clamp((int)Math.Ceiling(high / binHz), start + 1, maxBin + 1);
            _bandStart[i] = start;
            _bandEnd[i] = end;
            previousEnd = end;
        }
    }

    public int BandCount { get; }
    public int SampleRate { get; }

    /// <summary>Nível RMS suavizado (0 a 1, escala de −60 dB a 0 dB).</summary>
    public float Level { get { lock (_gate) return _level; } }

    /// <summary>Pico com queda lenta (0 a 1).</summary>
    public float Peak { get { lock (_gate) return _peak; } }

    /// <summary>Quantas vezes o espectro foi recalculado (para saber se há áudio chegando).</summary>
    public long Updates => Interlocked.Read(ref _updates);

    public void AddInterleaved(ReadOnlySpan<float> stereo, int channels = 2)
    {
        if (channels < 1 || stereo.Length < channels) return;
        lock (_gate)
        {
            for (var i = 0; i + channels - 1 < stereo.Length; i += channels)
            {
                var mono = channels == 1 ? stereo[i] : (stereo[i] + stereo[i + 1]) * 0.5f;
                _ring[_ringPosition] = mono;
                _ringPosition = (_ringPosition + 1) & (FftSize - 1);
                _sumSquares += mono * mono;
                _sumCount++;
                if (++_sinceFft >= Hop)
                {
                    _sinceFft = 0;
                    Analyze();
                }
            }
        }
    }

    /// <summary>Copia as bandas atuais (0 a 1) para o destino.</summary>
    public void CopyBands(Span<float> destination)
    {
        lock (_gate) _bands.AsSpan(0, Math.Min(_bands.Length, destination.Length)).CopyTo(destination);
    }

    /// <summary>Decai tudo até zero quando o áudio para de chegar (chamado pela interface).</summary>
    public void Decay(float factor = 0.85f)
    {
        lock (_gate)
        {
            for (var i = 0; i < _bands.Length; i++) _bands[i] *= factor;
            _level *= factor;
            _peak *= factor;
        }
    }

    public void Reset()
    {
        lock (_gate)
        {
            Array.Clear(_ring);
            Array.Clear(_bands);
            _ringPosition = _sinceFft = _sumCount = 0;
            _sumSquares = 0;
            _level = _peak = 0;
        }
    }

    private void Analyze()
    {
        // Janela de Hann sobre as últimas 2048 amostras, na ordem do tempo, já em ordem de bit reverso.
        for (var i = 0; i < FftSize; i++)
        {
            var sample = _ring[(_ringPosition + i) & (FftSize - 1)] * _window[i];
            var j = _reverse[i];
            _re[j] = sample;
            _im[j] = 0;
        }
        Transform();

        // Hann com seno de amplitude 1: magnitude do bin = N/4.
        const float reference = FftSize / 4f;
        for (var band = 0; band < _bands.Length; band++)
        {
            var max = 0f;
            for (var bin = _bandStart[band]; bin < _bandEnd[band]; bin++)
            {
                var magnitude = MathF.Sqrt(_re[bin] * _re[bin] + _im[bin] * _im[bin]);
                if (magnitude > max) max = magnitude;
            }
            var value = Normalize(max / reference);
            // Sobe rápido, desce devagar (fica bonito no visualizador).
            _bands[band] = value >= _bands[band] ? value : _bands[band] * 0.82f + value * 0.18f;
        }

        var rms = _sumCount == 0 ? 0 : (float)Math.Sqrt(_sumSquares / _sumCount);
        _sumSquares = 0;
        _sumCount = 0;
        var level = Normalize(rms * MathF.Sqrt(2)); // seno de pico 1 → 1.0
        _level = level >= _level ? level : _level * 0.8f + level * 0.2f;
        _peak = Math.Max(level, _peak * 0.95f);
        Interlocked.Increment(ref _updates);
    }

    private static float Normalize(float linear)
    {
        if (linear <= 1e-6f) return 0;
        var db = 20 * MathF.Log10(linear);
        return Math.Clamp((db - FloorDb) / -FloorDb, 0, 1);
    }

    private void Transform()
    {
        for (var size = 2; size <= FftSize; size <<= 1)
        {
            var half = size >> 1;
            var step = FftSize / size;
            for (var start = 0; start < FftSize; start += size)
            {
                for (var k = 0; k < half; k++)
                {
                    var wr = _cos[k * step];
                    var wi = _sin[k * step];
                    var a = start + k;
                    var b = a + half;
                    var tr = _re[b] * wr - _im[b] * wi;
                    var ti = _re[b] * wi + _im[b] * wr;
                    _re[b] = _re[a] - tr;
                    _im[b] = _im[a] - ti;
                    _re[a] += tr;
                    _im[a] += ti;
                }
            }
        }
    }
}
