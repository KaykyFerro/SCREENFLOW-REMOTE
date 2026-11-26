namespace ScreenFlow.Core.Media;

/// <summary>
/// Quanto áudio o player guarda antes de tocar (o atraso do som). Começa no mínimo e só sobe quando o som falha
/// (faltou pacote na hora de tocar): cada falha soma 10 ms, e depois de 15 s sem falha volta 5 ms por vez. Numa rede
/// estável o som fica com o menor atraso possível; numa rede instável o buffer cresce só o necessário para não cortar.
/// </summary>
public sealed class PlayoutController
{
    public static readonly TimeSpan CalmBeforeShrinking = TimeSpan.FromSeconds(15);
    public static readonly TimeSpan ShrinkEvery = TimeSpan.FromSeconds(5);

    private TimeSpan? _lastUnderrun;
    private TimeSpan? _lastShrink;
    private double _smoothedMs = double.NaN;

    public PlayoutController(int minMs, int maxTargetMs)
    {
        if (minMs <= 0 || maxTargetMs < minMs) throw new ArgumentOutOfRangeException(nameof(maxTargetMs));
        MinMs = minMs;
        MaxTargetMs = maxTargetMs;
        TargetMs = minMs;
    }

    public int MinMs { get; }
    public int MaxTargetMs { get; }

    /// <summary>O alvo de agora, em ms.</summary>
    public int TargetMs { get; private set; }

    /// <summary>Acima disso o buffer pula de volta para o alvo (atraso acumulado demais, por exemplo depois de um travamento).</summary>
    public int HardLimitMs => TargetMs * 3 + 40;

    /// <summary>O som falhou: guarda um pouco mais daqui para frente.</summary>
    public void Underrun(TimeSpan now)
    {
        TargetMs = Math.Min(MaxTargetMs, TargetMs + 10);
        _lastUnderrun = now;
    }

    /// <summary>
    /// Chamado a cada leitura com o que está guardado. Devolve a velocidade de leitura: 1 no alvo, 0,5% mais rápido
    /// acima dele (o atraso volta para o alvo sem corte; o ouvido não percebe) e 0,5% mais devagar abaixo (para não
    /// faltar). Corrige também a diferença entre o relógio da placa de som de quem transmite e a de quem ouve.
    /// </summary>
    public double Update(double bufferedMs, TimeSpan now)
    {
        if (TargetMs > MinMs && (_lastUnderrun is not { } underrun || now - underrun > CalmBeforeShrinking)
            && (_lastShrink is not { } shrink || now - shrink > ShrinkEvery))
        {
            TargetMs = Math.Max(MinMs, TargetMs - 5);
            _lastShrink = now;
        }
        // Média de ~0,5 s: o buffer sobe e desce 10 ms a cada pacote, e isso não é atraso acumulado.
        _smoothedMs = double.IsNaN(_smoothedMs) ? bufferedMs : _smoothedMs * 0.98 + bufferedMs * 0.02;
        var excess = _smoothedMs - TargetMs;
        return excess switch
        {
            > 40 => 1.01,
            > 8 => 1.005,
            < -8 => 0.995,
            _ => 1.0,
        };
    }

    /// <summary>Recomeço (outro dispositivo, buffer zerado): a média recomeça junto.</summary>
    public void Reset() => _smoothedMs = double.NaN;
}
