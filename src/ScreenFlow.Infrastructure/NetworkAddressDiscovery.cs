using System.Net;
using System.Net.Sockets;

namespace ScreenFlow.Infrastructure;

public static class NetworkAddressDiscovery
{
    public static IReadOnlyList<IPAddress> GetLocalIPv4Addresses()
    {
        return Dns.GetHostAddresses(Dns.GetHostName())
            .Where(address => address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(address))
            .Distinct()
            .ToArray();
    }
}