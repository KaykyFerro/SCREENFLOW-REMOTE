namespace ScreenFlow.Core.Media;

/// <summary>
/// Desfaz o volume do PC de quem transmite no som que vai para quem assiste (1.3.0).
/// <para>
/// Em muitos PCs o Windows aplica o volume geral antes de o ScreenFlow capturar o som (no loopback por app, quase
/// sempre): baixar o volume para ouvir mais baixo deixava a transmissão baixa para todo mundo. Aqui o som capturado é
/// multiplicado pelo inverso do volume, para cada um controlar só o próprio volume, inclusive quem transmite.
/// </para>
/// <para>
/// Nem todo PC baixa o som antes da captura (placas com o volume no hardware, fones Bluetooth com volume próprio). Um
/// som que baixou com o volume nunca passa do teto que o volume deixa; se o som capturado passar desse teto de forma
/// constante, o Windows não baixou o som antes, e a correção desliga sozinha para este dispositivo. Um limitador
/// segura os picos que passariam do máximo.
/// </para>
/// Uma instância por captura: <see cref="SetVolume"/> numa thread qualquer, <see cref="Process"/> na thread da captura.
/// </summary>
public sealed class VolumeCompensation
{
    /// <summary>O máximo que a correção sobe (o Windows chega a −65 dB no volume 1).</summary>
    public const float MaxBoostDb = 60f;

    /// <summary>Folga sobre o teto que o volume deixa (+2 dB), para somas de apps que passam um pouco do máximo.</summary>
    public const float CeilingMargin = 1.25f;

    /// <summary>Tempo sem mudança de volume antes de aceitar a prova de que o som não baixou (a notificação chega atrasada).</summary>
    public const long SettleMs = 1000;

    /// <summary>Blocos com som acima do teto (em tempo, perto de 100 ms) para concluir que o Windows não baixou o som.</summary>
    public const int OverBlocksToBypass = 10;

    private const float LimitThreshold = 0.97f;
    private const float ReleasePerFrame = 0.99958f; // ~50 ms a 48 kHz

    private readonly object _gate = new();
    private float _levelDb;
    private bool _muted;
    private bool _known;
    private long _changedAtMs = long.MinValue;
    private float _gain = 1f;
    private float _envelope;
    private int _overBlocks;

    /// <summary>Ligada pela configuração "O volume do PC não muda o som da transmissão".</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Concluiu que o Windows não baixa o som antes da captura neste dispositivo: não corrige nada.</summary>
    public bool Bypassed { get; private set; }

    /// <summary>O ganho que está sendo aplicado agora (1 = nada).</summary>
    public float CurrentGain => _gain;

    /// <summary>O volume geral do PC em dB (0 = máximo) e se está no mudo.</summary>
    public void SetVolume(float levelDb, bool muted, long nowMs)
    {
        lock (_gate)
        {
            if (_known && Math.Abs(levelDb - _levelDb) < 0.01f && muted == _muted) return;
            _levelDb = float.IsFinite(levelDb) ? Math.Min(0f, levelDb) : 0f;
            _muted = muted;
            _known = true;
            _changedAtMs = nowMs;
            _overBlocks = 0;
        }
    }

    /// <summary>Quanto o volume baixa o som (1 = nada), pelo nível em dB.</summary>
    public static float Attenuation(float levelDb) => MathF.Pow(10f, Math.Max(levelDb, -MaxBoostDb) / 20f);

    /// <summary>
    /// Corrige amostras float intercaladas no lugar. <paramref name="nowMs"/> é o relógio em milissegundos (para a
    /// espera depois de uma mudança de volume).
    /// </summary>
    public void Process(Span<float> samples, int channels, long nowMs)
    {
        if (channels <= 0 || samples.Length < channels) return;
        float target;
        lock (_gate)
        {
            var attenuation = _known ? Attenuation(_levelDb) : 1f;
            if (Enabled && _known && !_muted && !Bypassed && attenuation < 0.97f)
            {
                // Prova de que o som não baixou com o volume: muita amostra acima do teto, longe de uma mudança de volume.
                var ceiling = attenuation * CeilingMargin;
                var overs = 0;
                foreach (var sample in samples)
                    if (Math.Abs(sample) > ceiling && ++overs >= 16) break;
                if (overs >= 16 && nowMs - _changedAtMs >= SettleMs)
                {
                    if (++_overBlocks >= OverBlocksToBypass) Bypassed = true;
                }
                else if (overs == 0) _overBlocks = Math.Max(0, _overBlocks - 1);
            }
            target = Enabled && _known && !_muted && !Bypassed ? 1f / attenuation : 1f;
        }

        var frames = samples.Length / channels;
        var start = _gain;
        if (start == 1f && target == 1f)
        {
            // Sem correção, o som passa exatamente como veio (o limitador só existe para o ganho extra).
            _envelope = 0f;
            return;
        }
        var step = (target - start) / frames;
        var envelope = _envelope;
        for (var frame = 0; frame < frames; frame++)
        {
            // O ganho anda devagar até o novo valor dentro do bloco, sem estalo quando o volume muda.
            var gain = start + step * (frame + 1);
            var offset = frame * channels;
            var peak = 0f;
            for (var c = 0; c < channels; c++) peak = Math.Max(peak, Math.Abs(samples[offset + c] * gain));
            envelope = Math.Max(peak, envelope * ReleasePerFrame);
            var limit = envelope > LimitThreshold ? LimitThreshold / envelope : 1f;
            var total = gain * limit;
            if (total == 1f) continue;
            for (var c = 0; c < channels; c++) samples[offset + c] *= total;
        }
        _gain = target;
        _envelope = envelope;
    }
}
