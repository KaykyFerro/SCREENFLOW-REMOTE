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
/// <remarks>
/// Servidor (1.4.1): quem transmite abre a conexão com o servidor e manda <see cref="PublishMessage"/>; o servidor confere a
/// senha (Challenge/Auth) e responde <see cref="PublishAcceptedMessage"/>. Daí os papéis se invertem na mesma conexão: o
/// servidor faz o papel de quem assiste (Hello → Challenge → Auth → Welcome) e conta quem está assistindo por ele com
/// <see cref="AudienceMessage"/>. Quem assiste pelo servidor manda o <see cref="HelloMessage.StreamId"/> da transmissão.
/// </remarks>
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
[JsonDerivedType(typeof(MediaCommandResultMessage), "mediaresult")]
[JsonDerivedType(typeof(PublishMessage), "publish")]
[JsonDerivedType(typeof(PublishAcceptedMessage), "published")]
[JsonDerivedType(typeof(AudienceMessage), "audience")]
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
    string[]? Features = null,
    string? StreamId = null) : ControlMessage
{
    public bool Supports(string feature) => Features is not null && Features.Contains(feature, StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// (1.4.1) Quem transmite se apresenta ao servidor. <paramref name="StreamId"/> é o id da pessoa na sala, então quem vê
/// a pessoa ao vivo na sala sabe qual transmissão pedir ao servidor.
/// </summary>
public sealed record PublishMessage(
    int Protocol,
    string AppVersion,
    string HostName,
    string StreamId,
    string PublicKey,
    StreamMode Mode,
    bool CameraOverlay = false,
    bool AudioEnabled = true) : ControlMessage;

/// <summary>(1.4.1) O servidor aceitou a transmissão. A seguir ele manda o Hello, no papel de quem assiste.</summary>
public sealed record PublishAcceptedMessage(string ServerName, string ServerVersion) : ControlMessage;

/// <summary>
/// (1.4.1) Do servidor para quem transmite: quantas pessoas estão assistindo por ele, os nomes, e o que todas conseguem
/// decodificar (o codec e o tamanho do vídeo valem para todo mundo). Com 0 pessoas, quem transmite para de capturar.
/// </summary>
public sealed record AudienceMessage(int Viewers, string[] Names, VideoCodec[] Decoders, int MaxWidth, int MaxHeight, int MaxFps) : ControlMessage;

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
/// <param name="SessionKey">
/// (1.4.0) Qual sessão de mídia do host é esta: muda quando o host passa a controlar outro app. Um comando com outra chave
/// é recusado, para nunca mexer num app que não é o que está sendo transmitido. Hosts antigos não mandam.
/// </param>
/// <param name="SeekStartSeconds">(1.4.0) Começo do trecho em que dá para buscar (0 para quase tudo).</param>
/// <param name="SeekEndSeconds">(1.4.0) Fim do trecho em que dá para buscar. 0: o trecho é o vídeo inteiro (0 até a duração).</param>
/// <param name="Live">(1.4.0) Conteúdo ao vivo: a duração pode crescer e a busca vale só no trecho informado.</param>
/// <param name="NoSeek">(1.4.0) Por que não dá para buscar (<see cref="MediaSeek"/>), ou null.</param>
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
    string? Cover = null,
    string? SessionKey = null,
    double SeekStartSeconds = 0,
    double SeekEndSeconds = 0,
    bool Live = false,
    string? NoSeek = null) : ControlMessage
{
    public static MediaStateMessage None { get; } = new(string.Empty, string.Empty, string.Empty, false, 0, 0, false, false, false, false);

    [JsonIgnore]
    public bool Available => AppName.Length > 0 || Title.Length > 0;
}

/// <summary>Os apps que estão tocando som no PC de quem transmite, e quais ficam fora da transmissão.</summary>
public sealed record AudioAppsMessage(RemoteAudioApp[] Apps) : ControlMessage;

public sealed record RemoteAudioApp(string Exe, string Name, bool Excluded, bool Playing);

/// <summary>Quem assiste pede outra qualidade. Vale para a transmissão toda (um encoder só para todo mundo).</summary>
/// <param name="From">(1.4.1) Quem pediu, quando o pedido passa pelo servidor (o host vê o nome da pessoa, não o do servidor).</param>
public sealed record QualityRequestMessage(string Resolution, int Fps, bool Adaptive, string? From = null) : ControlMessage;

/// <summary>Quem assiste pede voltar, pausar ou tocar, pular ou ir para um ponto da música ou do vídeo.</summary>
/// <param name="RequestId">(1.4.0) Número do pedido, para a resposta (<see cref="MediaCommandResultMessage"/>) e para o host ignorar repetidos.</param>
/// <param name="SessionKey">(1.4.0) A <see cref="MediaStateMessage.SessionKey"/> que quem assiste estava vendo.</param>
/// <param name="DeltaSeconds">(1.4.0) Só em <see cref="SkipBy"/>: quanto andar (negativo volta).</param>
/// <param name="From">(1.4.1) Quem pediu, quando o pedido passa pelo servidor.</param>
public sealed record MediaCommandMessage(string Command, double PositionSeconds = 0, long RequestId = 0, string? SessionKey = null, double DeltaSeconds = 0,
    string? From = null) : ControlMessage
{
    public const string PlayPause = "playpause";
    public const string Previous = "previous";
    public const string Next = "next";
    public const string Seek = "seek";

    /// <summary>(1.4.0) Avança ou volta <see cref="DeltaSeconds"/> a partir da posição de agora no host.</summary>
    public const string SkipBy = "skip";
}

/// <summary>
/// (1.4.0) O host conta a quem pediu como o comando terminou: feito (com a posição conferida depois da busca), recusado
/// pelo app, sem confirmação ou indisponível. Só vai para quem anunciou <see cref="Media.StreamModes.FeatureMediaSeek"/>.
/// </summary>
/// <param name="Outcome">Um dos <see cref="MediaSeek"/>.Outcome*.</param>
/// <param name="PositionSeconds">A posição lida no app depois do comando (0 se não leu).</param>
public sealed record MediaCommandResultMessage(long RequestId, string Command, string Outcome, double PositionSeconds = 0) : ControlMessage;

/// <summary>Quem assiste escolhe os apps (da lista que o host mandou) que ficam fora do som da transmissão.</summary>
/// <param name="From">(1.4.1) Quem pediu, quando o pedido passa pelo servidor.</param>
public sealed record AudioAppsRequestMessage(string[] Excluded, string? From = null) : ControlMessage;

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
