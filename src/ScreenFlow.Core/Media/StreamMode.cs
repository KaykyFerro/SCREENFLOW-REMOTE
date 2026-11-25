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

    /// <summary>
    /// O que está indo para quem assiste, em uma frase curta: "tela, câmera e áudio", "câmera e áudio", "só o áudio
    /// (festa de música)". Usado na dica do ícone da bandeja e na lista de pessoas da rede.
    /// </summary>
    public static string Summary(StreamMode mode, bool audio, bool cameraWithScreen = false)
    {
        if (mode == StreamMode.AudioOnly) return "só o áudio (festa de música)";
        var parts = new List<string>(3);
        if (mode == StreamMode.Screen) parts.Add("tela");
        if (mode == StreamMode.Camera || cameraWithScreen) parts.Add("câmera");
        if (audio) parts.Add("áudio");
        return parts.Count switch
        {
            1 => $"só a {parts[0]}",
            2 => $"{parts[0]} e {parts[1]}",
            _ => $"{parts[0]}, {parts[1]} e {parts[2]}",
        };
    }
}
