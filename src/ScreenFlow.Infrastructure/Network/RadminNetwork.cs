using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using ScreenFlow.Core.Network;

namespace ScreenFlow.Infrastructure.Network;

public sealed record NetworkAdapterInfo(string Name, string Description, IPAddress Address, IPAddress Mask, long SpeedMbps, bool IsRadmin, bool IsUp)
{
    public IPAddress Broadcast => RadminAddress.BroadcastFor(Address, Mask);
    public override string ToString() => $"{Description} ({Address})";
}

/// <summary>Detecta o adaptador do Radmin VPN (nome "Radmin VPN" ou IPv4 em 26.0.0.0/8).</summary>
public static class RadminNetwork
{
    public static IReadOnlyList<NetworkAdapterInfo> GetIPv4Adapters()
    {
        var result = new List<NetworkAdapterInfo>();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
            IPInterfaceProperties properties;
            try { properties = nic.GetIPProperties(); }
            catch (NetworkInformationException) { continue; }

            foreach (var unicast in properties.UnicastAddresses)
            {
                if (unicast.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                var isRadmin = RadminAddress.IsRadminAdapterName(nic.Description) || RadminAddress.IsRadminAdapterName(nic.Name) || RadminAddress.IsRadmin(unicast.Address);
                var speed = nic.Speed > 0 ? nic.Speed / 1_000_000 : 0;
                result.Add(new NetworkAdapterInfo(nic.Name, nic.Description, unicast.Address, unicast.IPv4Mask ?? RadminAddress.DefaultMask, speed, isRadmin, nic.OperationalStatus == OperationalStatus.Up));
            }
        }
        return result;
    }

    /// <summary>O adaptador Radmin ativo com IP 26.x, ou null se o Radmin não estiver instalado/conectado.</summary>
    public static NetworkAdapterInfo? FindRadmin()
        => GetIPv4Adapters()
            .Where(a => a.IsRadmin && a.IsUp && RadminAddress.IsRadmin(a.Address))
            .OrderByDescending(a => RadminAddress.IsRadminAdapterName(a.Description))
            .FirstOrDefault();

    public static bool IsRadminInstalled()
        => NetworkInterface.GetAllNetworkInterfaces().Any(n => RadminAddress.IsRadminAdapterName(n.Description) || RadminAddress.IsRadminAdapterName(n.Name));

    /// <summary>Endereço local usado para alcançar o host (para escolher a interface do viewer).</summary>
    public static IPAddress? LocalAddressFor(IPAddress remote)
    {
        try
        {
            using var probe = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            probe.Connect(remote, 9);
            return (probe.LocalEndPoint as IPEndPoint)?.Address;
        }
        catch (SocketException) { return null; }
    }
}
