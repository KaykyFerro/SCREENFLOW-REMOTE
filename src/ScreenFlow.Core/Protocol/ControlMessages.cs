using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Serialization;
using ScreenFlow.Core.Media;

namespace ScreenFlow.Core.Protocol;

/// <summary>
/// Mensagens do canal de controle TCP (porta 47801). Cada mensagem é um JSON precedido de 4 bytes (big-endian) de tamanho.
/// Fluxo: Hello → Challenge → Auth → (aprovação) → Welcome | Rejected. Depois: Ping/Pong, Stats, Nack, KeyframeRequest, StreamConfig, Bye.
/// Desde a 1.2.0, quando os dois lados anunciam "remote-control": o host manda <see cref="RemoteStateMessage"/>,
/// <see cref="MediaStateMessage"/> e <see cref="AudioAppsMessage"/>, e quem assiste pede <see cref="QualityRequestMessage"/>,
/// <see cref="MediaCommandMessage"/> e <see cref="AudioAppsRequestMessage"/>. Um tipo desconhecido vira
/// <see cref="UnknownControlMessage"/> e é ignorado (as versões até a 1.1.1 derrubavam a conexão).
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "t")]
[JsonDerivedType(typeof(HelloMessage), "hello")]
[JsonDerivedType(typeof(ChallengeMessage), "challenge")]
[JsonDerivedType(typeof(AuthMessage), "auth")]
[JsonDerivedType(typeof(WelcomeMessage), "welcome")]
[JsonDerivedType(typeof(RejectedMessage), "rejected")]
[JsonDerivedType(typeof(PendingApprovalMessage), "pending")]
[JsonDerivedType(typeof(StreamConfigMessage), "config")]
[JsonDerivedType(typeof(PingMessage), "ping")]
[JsonDerivedType(typeof(PongMessage), "pong")]
[JsonDerivedType(typeof(StatsMessage), "stats")]
[JsonDerivedType(typeof(NackMessage), "nack")]
[JsonDerivedType(typeof(KeyframeRequestMessage), "keyframe")]
[JsonDerivedType(typeof(BandwidthTestRequestMessage), "bwtest")]
[JsonDerivedType(typeof(BandwidthTestResultMessage), "bwresult")]
[JsonDerivedType(typeof(ByeMessage), "bye")]
[JsonDerivedType(typeof(RemoteStateMessage), "remote")]
[JsonDerivedType(typeof(MediaStateMessage), "media")]
[JsonDerivedType(typeof(AudioAppsMessage), "apps")]
[JsonDerivedType(typeof(QualityRequestMessage), "setquality")]
[JsonDerivedType(typeof(MediaCommandMessage), "mediacmd")]
[JsonDerivedType(typeof(AudioAppsRequestMessage), "setapps")]
public abstract record ControlMessage;

/// <summary>Uma mensagem de um tipo que esta versão não conhece (de uma versão mais nova). Não é enviada: só lida e ignorada.</summary>
public sealed record UnknownControlMessage(string Type) : ControlMessage;

public sealed record HelloMessage(
    int Protocol,
    string AppVersion,
    string ViewerName,
    VideoCodec[] Decoders,
    int MaxWidth,
    int MaxHeight,
    int MaxFps,
    int UdpPort,
    string PublicKey,
    string[]? Features = null) : ControlMessage
{
    public bool Supports(string feature) => Features is not null && Features.Contains(feature, StringComparer.OrdinalIgnoreCase);
}

public sealed record ChallengeMessage(string HostName, string PublicKey, string Nonce, bool PinRequired) : ControlMessage;

public sealed record AuthMessage(string Proof) : ControlMessage;

public sealed record PendingApprovalMessage(string Message) : ControlMessage;

/// <param name="Features">Recursos do host (<see cref="StreamModes.HostFeatures"/>, 1.1.1). Hosts antigos não mandam.</param>
public sealed record WelcomeMessage(string HostName, string SessionSalt, int MediaPort, StreamConfigMessage Stream, string[]? Features = null) : ControlMessage
{
    public bool Supports(string feature) => Features is not null && Features.Contains(feature, StringComparer.OrdinalIgnoreCase);
}

public sealed record RejectedMessage(string Code, string Message) : ControlMessage
{
    public const string VersionMismatch = "version";
    public const string WrongPin = "pin";
    public const string Denied = "denied";
    public const string NoCodec = "codec";
    public const string Busy = "busy";
    public const string Throttled = "throttled";
}

public sealed record StreamConfigMessage(
    VideoCodec Codec,
    int Width,
    int Height,
    int Fps,
    int BitrateKbps,
    bool AudioEnabled,
    int AudioSampleRate,
    int AudioChannels,
    string EncoderName,
    string Reason,
    bool VideoEnabled = true,
    StreamMode Mode = StreamMode.Screen,
    bool CameraOverlay = false) : ControlMessage;

public sealed record PingMessage(long ViewerTimeUs) : ControlMessage;

public sealed record PongMessage(long ViewerTimeUs, long HostTimeUs) : ControlMessage;

public sealed record StatsMessage(
    double LossRate,
    double RoundTripMs,
    double ReceivedKbps,
    double DecodeFps,
    double LatencyMs) : ControlMessage;

/// <param name="Track">Faixa de vídeo (<see cref="VideoTrack"/>): 0 é a tela, 1 é a câmera junto com a tela (1.1.0).</param>
public sealed record NackMessage(uint FrameId, ushort[] Missing, int Track = VideoTrack.Main) : ControlMessage;

/// <param name="Track">Faixa de vídeo (<see cref="VideoTrack"/>): 0 é a tela, 1 é a câmera junto com a tela (1.1.0).</param>
public sealed record KeyframeRequestMessage(string Reason, int Track = VideoTrack.Main) : ControlMessage;

public sealed record BandwidthTestRequestMessage(int Seconds, int TargetMbps) : ControlMessage;

public sealed record BandwidthTestResultMessage(double ReceivedMbps, double LossRate, int Seconds) : ControlMessage;

public sealed record ByeMessage(string Reason) : ControlMessage;

// ───────────────────────── Controles de quem assiste (1.2.0) ─────────────────────────

/// <summary>
/// O que quem assiste pode fazer agora, e as qualidades que dá para pedir. Cada "Can" já junta a permissão de quem
/// transmite e se faz sentido (na festa de música não há qualidade de vídeo; na festa de um app só não há apps para tirar).
/// </summary>
/// <param name="Resolutions">Resoluções da tela ("720p", "1080p"...). Vazio no modo câmera.</param>
/// <param name="FrameRates">FPS da tela. Vazio no modo câmera.</param>
/// <param name="CameraQualities">Formatos da câmera (resolução e FPS juntos). Vazio no modo tela.</param>
/// <param name="Resolution">A qualidade escolhida agora (o controle adaptativo pode estar mandando menos).</param>
public sealed record RemoteStateMessage(
    bool CanChangeQuality,
    bool CanControlMedia,
    bool CanChooseApps,
    string[] Resolutions,
    int[] FrameRates,
    RemoteQuality[] CameraQualities,
    string Resolution,
    int Fps,
    bool Adaptive) : ControlMessage
{
    public static RemoteStateMessage Disabled { get; } = new(false, false, false, [], [], [], string.Empty, 0, true);
}

public sealed record RemoteQuality(string Resolution, int Fps);

/// <summary>O que está tocando no PC de quem transmite (pelos controles de mídia do Windows). Vazio: nada tocando.</summary>
/// <param name="PositionSeconds">A posição na hora em que a mensagem saiu; quem assiste anda com o relógio dele a partir daí.</param>
/// <param name="Cover">A capa, em JPEG pequeno (base64), ou null.</param>
public sealed record MediaStateMessage(
    string AppName,
    string Title,
    string Artist,
    bool Playing,
    double PositionSeconds,
    double DurationSeconds,
    bool CanPrevious,
    bool CanNext,
    bool CanPlayPause,
    bool CanSeek,
    string? Cover = null) : ControlMessage
{
    public static MediaStateMessage None { get; } = new(string.Empty, string.Empty, string.Empty, false, 0, 0, false, false, false, false);

    [JsonIgnore]
    public bool Available => AppName.Length > 0 || Title.Length > 0;
}

/// <summary>Os apps que estão tocando som no PC de quem transmite, e quais ficam fora da transmissão.</summary>
public sealed record AudioAppsMessage(RemoteAudioApp[] Apps) : ControlMessage;

public sealed record RemoteAudioApp(string Exe, string Name, bool Excluded, bool Playing);

/// <summary>Quem assiste pede outra qualidade. Vale para a transmissão toda (um encoder só para todo mundo).</summary>
public sealed record QualityRequestMessage(string Resolution, int Fps, bool Adaptive) : ControlMessage;

/// <summary>Quem assiste pede voltar, pausar ou tocar, pular ou ir para um ponto da música ou do vídeo.</summary>
public sealed record MediaCommandMessage(string Command, double PositionSeconds = 0) : ControlMessage
{
    public const string PlayPause = "playpause";
    public const string Previous = "previous";
    public const string Next = "next";
    public const string Seek = "seek";
}

/// <summary>Quem assiste escolhe os apps (da lista que o host mandou) que ficam fora do som da transmissão.</summary>
public sealed record AudioAppsRequestMessage(string[] Excluded) : ControlMessage;

public static class ControlFraming
{
    public const int MaximumMessageBytes = 256 * 1024;

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };

    public static byte[] Serialize(ControlMessage message)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(message, Options);
        var frame = new byte[4 + json.Length];
        BinaryPrimitives.WriteInt32BigEndian(frame, json.Length);
        json.CopyTo(frame, 4);
        return frame;
    }

    /// <summary>Os tipos que esta versão conhece (o "t" de cada <see cref="JsonDerivedTypeAttribute"/>).</summary>
    private static readonly HashSet<string> KnownTypes = typeof(ControlMessage)
        .GetCustomAttributes(typeof(JsonDerivedTypeAttribute), false)
        .OfType<JsonDerivedTypeAttribute>()
        .Select(a => a.TypeDiscriminator as string)
        .OfType<string>()
        .ToHashSet(StringComparer.Ordinal);

    /// <summary>
    /// Lê uma mensagem. Um tipo que esta versão não conhece (de uma versão mais nova) vira <see cref="UnknownControlMessage"/>
    /// em vez de derrubar a conexão; um JSON quebrado continua sendo erro.
    /// </summary>
    public static ControlMessage Deserialize(ReadOnlySpan<byte> json)
    {
        try
        {
            return JsonSerializer.Deserialize<ControlMessage>(json, Options) ?? throw new InvalidDataException("Mensagem de controle vazia.");
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException && UnknownType(json) is { } type)
        {
            return new UnknownControlMessage(type);
        }
    }

    /// <summary>O "t" do objeto, se ele não for um tipo conhecido. Null se for conhecido ou se o JSON não tiver "t".</summary>
    private static string? UnknownType(ReadOnlySpan<byte> json)
    {
        try
        {
            var reader = new Utf8JsonReader(json);
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject) return null;
            while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
            {
                var isType = reader.ValueTextEquals("t");
                if (!reader.Read()) return null;
                if (isType)
                    return reader.TokenType == JsonTokenType.String && reader.GetString() is { Length: > 0 and <= 64 } type && !KnownTypes.Contains(type) ? type : null;
                reader.Skip();
            }
        }
        catch (JsonException) { }
        return null;
    }

    public static async Task WriteAsync(Stream stream, ControlMessage message, CancellationToken cancellationToken)
    {
        var frame = Serialize(message);
        await stream.WriteAsync(frame, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public static async Task<ControlMessage> ReadAsync(Stream stream, CancellationToken cancellationToken)
    {
        var header = new byte[4];
        await stream.ReadExactlyAsync(header, cancellationToken).ConfigureAwait(false);
        var length = BinaryPrimitives.ReadInt32BigEndian(header);
        if (length is <= 0 or > MaximumMessageBytes) throw new InvalidDataException($"Tamanho de mensagem inválido: {length}.");
        var body = new byte[length];
        await stream.ReadExactlyAsync(body, cancellationToken).ConfigureAwait(false);
        return Deserialize(body);
    }
}
