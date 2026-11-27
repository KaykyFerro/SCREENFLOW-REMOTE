using System.Text.Json;
using System.Text.Json.Serialization;

namespace ScreenFlow.Core.Protocol;

/// <summary>Anúncio enviado pelo host por broadcast UDP (porta 47802) na sub-rede do Radmin, a cada segundo.</summary>
/// <param name="Stream">
/// (1.4.1) A transmissão dentro de um servidor ScreenFlow: quem assiste manda este id no <see cref="HelloMessage.StreamId"/>.
/// Null nas transmissões diretas.
/// </param>
/// <param name="Via">(1.4.1) O nome do servidor por onde a transmissão passa, ou null.</param>
public sealed record DiscoveryAnnouncement(
    string App,
    int Protocol,
    string HostName,
    string Address,
    int ControlPort,
    string Version,
    bool PinRequired,
    int Viewers,
    ScreenFlow.Core.Media.StreamMode Mode = ScreenFlow.Core.Media.StreamMode.Screen,
    bool CameraOverlay = false,
    string? Stream = null,
    string? Via = null)
{
    /// <summary>Chave da transmissão na lista: o endereço e, num servidor, o id da transmissão.</summary>
    [JsonIgnore]
    public string Key => Stream is { Length: > 0 } stream ? $"{Address}/{stream}" : Address;

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public byte[] ToBytes() => JsonSerializer.SerializeToUtf8Bytes(this, Options);

    public static bool TryParse(ReadOnlySpan<byte> data, out DiscoveryAnnouncement? announcement)
    {
        announcement = null;
        if (data.Length is 0 or > 2048) return false;
        try
        {
            announcement = JsonSerializer.Deserialize<DiscoveryAnnouncement>(data, Options);
            return announcement is { App: AppInfo.Name } && !string.IsNullOrWhiteSpace(announcement.Address);
        }
        catch (JsonException) { return false; }
    }
}
