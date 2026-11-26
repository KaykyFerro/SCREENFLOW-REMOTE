namespace ScreenFlow.Core.Media;

/// <summary>
/// Perfil do Opus. Tela e câmera usam quadros de 10 ms em CBR (menor atraso, sincronia com o vídeo).
/// A festa de música usa modo "audio" e VBR restrito (qualidade de música) e manda cada quadro junto com o anterior
/// (uma perda isolada não vira buraco no som). Desde a 1.1.1 a música também usa quadros de 10 ms (antes 20 ms): o
/// quadro e a cópia de segurança chegam 10 ms antes cada, e a 256 kbps a qualidade é a mesma.
/// </summary>
public sealed record AudioProfile(int BitrateKbps, int FrameMs, bool Music)
{
    public const int SampleRate = 48_000;
    public const int Channels = 2;
    public const int MinMusicKbps = 96;
    public const int MaxMusicKbps = 320; // acima disso o pacote passa do MTU do Radmin
    public const int MinVoiceKbps = 64;
    public const int MaxVoiceKbps = 256;

    public static IReadOnlyList<int> MusicBitrates { get; } = [128, 192, 256, 320];

    public int FrameSamples => SampleRate * FrameMs / 1000;

    /// <summary>"audio" (música) ou "lowdelay" (tela e câmera).</summary>
    public string OpusApplication => Music ? "audio" : "lowdelay";

    /// <summary>Envia o quadro anterior junto com o atual (só no modo festa de música, em que todos os viewers suportam).</summary>
    public bool Redundant => Music;

    /// <summary>
    /// Buffer de reprodução (o atraso do som): começa em <see cref="PlaybackMinMs"/> e só cresce até
    /// <see cref="PlaybackMaxTargetMs"/> se o som falhar (<see cref="PlayoutController"/>). Na música o mínimo cobre a
    /// cópia de segurança: um quadro perdido chega no pacote seguinte, 10 ms depois.
    /// </summary>
    public int PlaybackMinMs => Music ? 40 : 25;
    public int PlaybackMaxTargetMs => Music ? 160 : 90;

    public static AudioProfile Voice(int kbps) => new(Math.Clamp(kbps, MinVoiceKbps, MaxVoiceKbps), 10, false);
    public static AudioProfile ForMusic(int kbps) => new(Math.Clamp(kbps, MinMusicKbps, MaxMusicKbps), 10, true);

    public static AudioProfile ForMode(StreamMode mode, int screenKbps, int musicKbps)
        => mode == StreamMode.AudioOnly ? ForMusic(musicKbps) : Voice(screenKbps);
}
