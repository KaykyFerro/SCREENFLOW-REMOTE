namespace ScreenFlow.Core.Media;

/// <summary>
/// Perfil do Opus. Tela e câmera usam quadros de 10 ms em CBR (menor atraso, sincronia com o vídeo).
/// A festa de música usa quadros de 20 ms, modo "audio" e VBR restrito (qualidade de música),
/// manda cada quadro junto com o anterior (uma perda isolada não vira buraco no som) e toca com buffer maior.
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

    /// <summary>Buffer alvo e máximo na reprodução. A música aceita mais atraso em troca de som sem cortes.</summary>
    public int PlaybackTargetMs => Music ? 120 : 40;
    public int PlaybackMaxMs => Music ? 400 : 150;

    public static AudioProfile Voice(int kbps) => new(Math.Clamp(kbps, MinVoiceKbps, MaxVoiceKbps), 10, false);
    public static AudioProfile ForMusic(int kbps) => new(Math.Clamp(kbps, MinMusicKbps, MaxMusicKbps), 20, true);

    public static AudioProfile ForMode(StreamMode mode, int screenKbps, int musicKbps)
        => mode == StreamMode.AudioOnly ? ForMusic(musicKbps) : Voice(screenKbps);
}
