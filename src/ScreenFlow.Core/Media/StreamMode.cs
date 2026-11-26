namespace ScreenFlow.Core.Media;

/// <summary>O que o host transmite: a tela (monitor ou janela), a câmera, ou só o áudio do PC (festa de música).</summary>
public enum StreamMode { Screen = 0, Camera = 1, AudioOnly = 2 }

public static class StreamModes
{
    /// <summary>Recursos anunciados pelo viewer no handshake. Hosts antigos ignoram a lista.</summary>
    public const string FeatureAudioOnly = "audio-only";
    public const string FeatureCamera = "camera";

    /// <summary>O viewer recebe a segunda faixa de vídeo (a câmera junto com a tela) e mostra num quadrinho (1.1.0).</summary>
    public const string FeatureCameraOverlay = "camera-overlay";

    /// <summary>O viewer entende a paridade espalhada (1.1.1): perdas em sequência caem em grupos de FEC diferentes.</summary>
    public const string FeatureSpreadFec = "fec-spread";

    /// <summary>
    /// O viewer mostra os controles de quem assiste (1.2.0): qualidade, música e apps do som. Só ele recebe as mensagens
    /// <c>remote</c>, <c>media</c> e <c>apps</c>; as versões antigas não entendem esses tipos.
    /// </summary>
    public const string FeatureRemoteControl = "remote-control";

    public static IReadOnlyList<string> ViewerFeatures { get; } = [FeatureAudioOnly, FeatureCamera, FeatureCameraOverlay, FeatureSpreadFec, FeatureRemoteControl];

    /// <summary>Recursos anunciados pelo host na boas-vindas. Viewers antigos ignoram a lista.</summary>
    /// <remarks>"frame-nack": o host reenvia um frame inteiro quando o viewer não recebeu nada dele (NACK sem índices).</remarks>
    public const string HostFeatureFrameNack = "frame-nack";

    /// <summary>O host aceita os pedidos de quem assiste (1.2.0): trocar a qualidade, controlar a música e tirar apps do som.</summary>
    public const string HostFeatureRemoteControl = "remote-control";

    public static IReadOnlyList<string> HostFeatures { get; } = [HostFeatureFrameNack, HostFeatureRemoteControl];

    public static string Label(StreamMode mode, bool cameraWithScreen = false) => mode switch
    {
        StreamMode.Camera => "Câmera",
        StreamMode.AudioOnly => "Festa de música",
        _ => cameraWithScreen ? "Tela + câmera" : "Tela",
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
