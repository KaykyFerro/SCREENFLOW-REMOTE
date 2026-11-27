namespace ScreenFlow.Core.Media;

/// <summary>
/// (1.5.0) Toca um trecho mais rápido ou mais devagar sem mudar o tom da voz (como o 1,5× e o 2× das mensagens de voz
/// do WhatsApp). Usa WSOLA: pedaços de 30 ms do original, com metade sobreposta, são juntados com uma janela de Hann; a
/// cada pedaço, o ponto de corte é ajustado (até 7,5 ms para os lados) para o pedaço que mais combina com a continuação
/// natural do anterior, então a voz não fica "metálica" nem com eco. Trabalha com o trecho inteiro na memória.
/// </summary>
public sealed class TimeStretcher
{
    public const double MinSpeed = 0.5;
    public const double MaxSpeed = 2.0;

    private readonly int _channels;
    private readonly int _window;
    private readonly int _hop;
    private readonly int _search;
    private readonly float[] _hann;
    private readonly float[] _pending;
    private readonly float[] _ready;
    private int _readyFrames;
    private int _readyOffset;
    private bool _hasPending;
    private int _lastStart = -1;
    private double _nominal = -1;
    private bool _flushed;

    public TimeStretcher(int sampleRate, int channels)
    {
        _channels = Math.Max(1, channels);
        _window = Math.Max(64, sampleRate * 30 / 1000) & ~1;
        _hop = _window / 2;
        _search = sampleRate * 75 / 10_000;
        _hann = new float[_window];
        // Hann "periódica": com metade sobreposta, as janelas somam exatamente 1 (o volume não oscila).
        for (var i = 0; i < _window; i++) _hann[i] = (float)(0.5 - 0.5 * Math.Cos(2 * Math.PI * i / _window));
        _pending = new float[_hop * _channels];
        _ready = new float[_hop * _channels];
    }

    public static double CleanSpeed(double speed) => double.IsFinite(speed) ? Math.Clamp(speed, MinSpeed, MaxSpeed) : 1.0;

    /// <summary>Recomeça (depois de um pulo ou de mudar a velocidade): o próximo pedaço sai do ponto pedido.</summary>
    public void Reset()
    {
        _hasPending = false;
        _lastStart = -1;
        _nominal = -1;
        _readyFrames = _readyOffset = 0;
        _flushed = false;
    }

    /// <summary>
    /// Enche <paramref name="output"/> (intercalado) com o trecho <paramref name="clip"/> a partir de
    /// <paramref name="position"/> (em quadros), na velocidade pedida. <paramref name="position"/> anda o equivalente no
    /// original. Devolve quantos quadros escreveu (0 = acabou).
    /// </summary>
    public int Process(ReadOnlySpan<float> clip, ref int position, Span<float> output, double speed)
    {
        speed = CleanSpeed(speed);
        var clipFrames = clip.Length / _channels;
        if (_nominal < 0) _nominal = position;
        var outFrames = output.Length / _channels;
        var written = 0;
        while (written < outFrames)
        {
            if (_readyOffset < _readyFrames)
            {
                var n = Math.Min(outFrames - written, _readyFrames - _readyOffset);
                _ready.AsSpan(_readyOffset * _channels, n * _channels).CopyTo(output[(written * _channels)..]);
                _readyOffset += n;
                written += n;
                continue;
            }
            if (!NextHop(clip, clipFrames, speed)) break;
        }
        position = (int)Math.Min(clipFrames, Math.Max(0, _nominal));
        return written;
    }

    /// <summary>Monta o próximo trecho de saída (<see cref="_hop"/> quadros). False quando o trecho acabou.</summary>
    private bool NextHop(ReadOnlySpan<float> clip, int clipFrames, double speed)
    {
        if (_nominal >= clipFrames)
        {
            // Só falta a metade final do último pedaço.
            if (!_hasPending || _flushed) return false;
            _pending.CopyTo(_ready, 0);
            _readyFrames = _hop;
            _readyOffset = 0;
            _hasPending = false;
            _flushed = true;
            return true;
        }
        var start = (int)_nominal;
        if (_lastStart >= 0) start = BestStart(clip, clipFrames, start, _lastStart + _hop);
        for (var f = 0; f < _hop; f++)
        {
            var w1 = _hann[f];
            var w2 = _hann[f + _hop];
            for (var c = 0; c < _channels; c++)
            {
                var first = Sample(clip, clipFrames, start + f, c) * w1;
                var second = Sample(clip, clipFrames, start + _hop + f, c) * w2;
                var index = f * _channels + c;
                _ready[index] = (_hasPending ? _pending[index] : 0f) + first;
                _pending[index] = second;
            }
        }
        if (!_hasPending)
        {
            // O primeiro pedaço não tem com quem se juntar: entra direto, sem a subida da janela (não come o começo).
            for (var f = 0; f < _hop; f++)
                for (var c = 0; c < _channels; c++)
                    _ready[f * _channels + c] = Sample(clip, clipFrames, start + f, c);
        }
        _hasPending = true;
        _lastStart = start;
        _readyFrames = _hop;
        _readyOffset = 0;
        _nominal += _hop * speed;
        return true;
    }

    /// <summary>
    /// O começo, perto de <paramref name="nominal"/>, cujo trecho mais combina com <paramref name="natural"/> (por onde o
    /// pedaço anterior continuaria). Primeiro uma busca grossa (de 2 em 2, olhando 1 a cada 3 amostras), depois fina.
    /// </summary>
    private int BestStart(ReadOnlySpan<float> clip, int clipFrames, int nominal, int natural)
    {
        var from = Math.Max(0, nominal - _search);
        var to = Math.Min(Math.Max(from, clipFrames - _hop), nominal + _search);
        if (to <= from) return Math.Clamp(nominal, 0, Math.Max(0, clipFrames - 1));
        var best = nominal;
        var bestScore = double.NegativeInfinity;
        for (var candidate = from; candidate <= to; candidate += 2)
        {
            var score = Similarity(clip, clipFrames, candidate, natural, 3);
            if (score > bestScore) { bestScore = score; best = candidate; }
        }
        var coarse = best;
        for (var candidate = Math.Max(from, coarse - 2); candidate <= Math.Min(to, coarse + 2); candidate++)
        {
            var score = Similarity(clip, clipFrames, candidate, natural, 1);
            if (score > bestScore) { bestScore = score; best = candidate; }
        }
        return best;
    }

    private double Similarity(ReadOnlySpan<float> clip, int clipFrames, int candidate, int natural, int step)
    {
        double cross = 0, energy = 1e-9;
        for (var i = 0; i < _hop; i += step)
        {
            var a = Mono(clip, clipFrames, candidate + i);
            var b = Mono(clip, clipFrames, natural + i);
            cross += a * b;
            energy += a * a;
        }
        return cross / Math.Sqrt(energy);
    }

    private float Mono(ReadOnlySpan<float> clip, int clipFrames, int frame)
    {
        if ((uint)frame >= (uint)clipFrames) return 0f;
        var sum = 0f;
        for (var c = 0; c < _channels; c++) sum += clip[frame * _channels + c];
        return sum / _channels;
    }

    private float Sample(ReadOnlySpan<float> clip, int clipFrames, int frame, int channel)
        => (uint)frame < (uint)clipFrames ? clip[frame * _channels + channel] : 0f;
}
