namespace ScreenFlow.Core.Media;

/// <summary>
/// Apertar para falar (1.4.0): com ele ligado, a voz só vai enquanto a tecla está apertada (e um pouquinho depois de
/// soltar, para não cortar o fim da palavra), sem depender da sensibilidade. Desligado, vale o portão de voz de sempre.
/// </summary>
public sealed class PushToTalk
{
    /// <summary>Quanto tempo a voz continua depois de soltar a tecla.</summary>
    public const long TailMs = 250;

    private long _releasedAt = long.MinValue;
    private bool _held;

    public bool Enabled { get; set; }

    /// <summary>A tecla mudou (chamado pelo leitor da tecla, a cada leitura).</summary>
    public void SetHeld(bool held, long nowMs)
    {
        if (_held && !held) _releasedAt = nowMs;
        _held = held;
    }

    public bool Held => _held;

    /// <summary>O quadro vai pela rede: com apertar para falar, só com a tecla (ou logo depois); sem ele, pelo portão.</summary>
    public bool ShouldSend(bool gateOpen, long nowMs)
        => !Enabled ? gateOpen : _held || (_releasedAt != long.MinValue && nowMs - _releasedAt <= TailMs);
}
