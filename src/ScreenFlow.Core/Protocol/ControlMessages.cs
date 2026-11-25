using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Serialization;
using ScreenFlow.Core.Media;

namespace ScreenFlow.Core.Protocol;

/// <summary>
/// Mensagens do canal de controle TCP (porta 47801). Cada mensagem é um JSON precedido de 4 bytes (big-endian) de tamanho.
/// Fluxo: Hello → Challenge → Auth → (aprovação) → Welcome | Rejected. Depois: Ping/Pong, Stats, Nack, KeyframeRequest, StreamConfig, Bye.
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
public abstract record ControlMessage;

public sealed record HelloMessage(
    int Protocol,
    string AppVersion,
    string ViewerName,
    VideoCodec[] Decoders,
    int MaxWidth,
    int MaxHeight,
    int MaxFps,
    int UdpPort,
    string PublicKey) : ControlMessage;

public sealed record ChallengeMessage(string HostName, string PublicKey, string Nonce, bool PinRequired) : ControlMessage;

public sealed record AuthMessage(string Proof) : ControlMessage;

public sealed record PendingApprovalMessage(string Message) : ControlMessage;

public sealed record WelcomeMessage(string HostName, string SessionSalt, int MediaPort, StreamConfigMessage Stream) : ControlMessage;

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
    string Reason) : ControlMessage;

public sealed record PingMessage(long ViewerTimeUs) : ControlMessage;

public sealed record PongMessage(long ViewerTimeUs, long HostTimeUs) : ControlMessage;

public sealed record StatsMessage(
    double LossRate,
    double RoundTripMs,
    double ReceivedKbps,
    double DecodeFps,
    double LatencyMs) : ControlMessage;

public sealed record NackMessage(uint FrameId, ushort[] Missing) : ControlMessage;

public sealed record KeyframeRequestMessage(string Reason) : ControlMessage;

public sealed record BandwidthTestRequestMessage(int Seconds, int TargetMbps) : ControlMessage;

public sealed record BandwidthTestResultMessage(double ReceivedMbps, double LossRate, int Seconds) : ControlMessage;

public sealed record ByeMessage(string Reason) : ControlMessage;

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

    public static ControlMessage Deserialize(ReadOnlySpan<byte> json)
        => JsonSerializer.Deserialize<ControlMessage>(json, Options) ?? throw new InvalidDataException("Mensagem de controle vazia.");

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
