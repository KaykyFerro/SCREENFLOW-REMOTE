namespace ScreenFlow.Core.Media;

/// <summary>O que o host transmite: a tela (monitor ou janela), a câmera, ou só o áudio do PC (festa de música).</summary>
public enum StreamMode { Screen = 0, Camera = 1, AudioOnly = 2 }

public static class StreamModes
{
    /// <summary>Recursos anunciados pelo viewer no handshake. Hosts antigos ignoram a lista.</summary>
    public const string FeatureAudioOnly = "audio-only";
    public const string FeatureCamera = "camera";

    public static IReadOnlyList<string> ViewerFeatures { get; } = [FeatureAudioOnly, FeatureCamera];

    public static string Label(StreamMode mode) => mode switch
    {
        StreamMode.Camera => "Câmera",
        StreamMode.AudioOnly => "Festa de música",
        _ => "Tela",
    };

    public static string Description(StreamMode mode) => mode switch
    {
        StreamMode.Camera => "Mostre a sua webcam com o som do PC.",
        StreamMode.AudioOnly => "Só o áudio do PC, em alta qualidade, para ouvir junto.",
        _ => "Um monitor ou uma janela, com o som do PC.",
    };

    public static bool HasVideo(StreamMode mode) => mode != StreamMode.AudioOnly;
}
