namespace ScreenFlow.Core.Media;

/// <summary>
/// Portão do microfone: só deixa passar quando tem voz. Abre quando o volume passa do limiar, fica aberto um pouco
/// depois (para não cortar o fim das palavras) e fecha no silêncio, então ruído de fundo e silêncio não vão pela rede.
/// </summary>
public sealed class VoiceGate
{
    /// <summary>Quadros (de 20 ms) que o portão continua aberto depois que a voz some.</summary>
    public const int HangoverFrames = 15;

    private int _hangover;

    /// <summary>0 capta só voz alta (limiar −30 dBFS); 1 capta quase tudo (−60 dBFS).</summary>
    public double Sensitivity { get; set; } = 0.6;

    public double ThresholdDb => -30 - Math.Clamp(Sensitivity, 0, 1) * 30;

    public bool IsOpen { get; private set; }

    /// <summary>Volume do último quadro em dBFS (−100 no silêncio absoluto).</summary>
    public double LevelDb { get; private set; } = -100;

    /// <summary>Volume do último quadro de 0 a 255 (−60 dBFS ou menos = 0), para o anel de quem está falando.</summary>
    public byte LevelByte => (byte)Math.Clamp((LevelDb + 60) / 60 * 255, 0, 255);

    /// <summary>Mede o quadro e diz se ele deve ser enviado.</summary>
    public bool Process(ReadOnlySpan<float> samples)
    {
        LevelDb = RmsDb(samples);
        if (LevelDb >= ThresholdDb)
        {
            IsOpen = true;
            _hangover = HangoverFrames;
        }
        else if (_hangover > 0) _hangover--;
        else IsOpen = false;
        return IsOpen;
    }

    public void Reset()
    {
        IsOpen = false;
        _hangover = 0;
        LevelDb = -100;
    }

    public static double RmsDb(ReadOnlySpan<float> samples)
    {
        if (samples.IsEmpty) return -100;
        double sum = 0;
        foreach (var s in samples) sum += s * (double)s;
        var rms = Math.Sqrt(sum / samples.Length);
        return rms <= 1e-5 ? -100 : 20 * Math.Log10(rms);
    }
}
