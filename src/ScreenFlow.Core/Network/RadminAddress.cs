using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;

namespace ScreenFlow.Core.Network;

/// <summary>Regras de endereço da rede virtual do Radmin VPN (26.0.0.0/8).</summary>
public static class RadminAddress
{
    public static readonly IPAddress Network = IPAddress.Parse("26.0.0.0");
    public static readonly IPAddress DefaultMask = IPAddress.Parse("255.0.0.0");

    public static bool IsRadmin(IPAddress? address)
    {
        if (address is null || address.AddressFamily != AddressFamily.InterNetwork) return false;
        return address.GetAddressBytes()[0] == 26;
    }

    public static bool IsRadminAdapterName(string? text)
        => !string.IsNullOrEmpty(text) && text.Contains("Radmin VPN", StringComparison.OrdinalIgnoreCase);

    public static bool TryParseHost(string? input, out IPAddress address)
    {
        address = IPAddress.None;
        if (string.IsNullOrWhiteSpace(input)) return false;
        var text = input.Trim();
        var colon = text.LastIndexOf(':');
        if (colon > 0 && text.IndexOf(':') == colon) text = text[..colon];
        if (!IPAddress.TryParse(text, out var parsed) || parsed.AddressFamily != AddressFamily.InterNetwork) return false;
        address = parsed;
        return true;
    }

    public static IPAddress BroadcastFor(IPAddress address, IPAddress? mask)
    {
        var ip = BinaryPrimitives.ReadUInt32BigEndian(address.GetAddressBytes());
        var m = BinaryPrimitives.ReadUInt32BigEndian((mask ?? DefaultMask).GetAddressBytes());
        var broadcast = ip | ~m;
        var bytes = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, broadcast);
        return new IPAddress(bytes);
    }
}
