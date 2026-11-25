using System.Text.Json;

namespace ScreenFlow.Core.Protocol;

/// <summary>Anúncio enviado pelo host por broadcast UDP (porta 47802) na sub-rede do Radmin, a cada segundo.</summary>
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
    bool CameraOverlay = false)
{
    private static readonly JsonSerializerOptions Options = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

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
