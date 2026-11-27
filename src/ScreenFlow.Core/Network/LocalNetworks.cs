using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace ScreenFlow.Core.Network;

public enum NetworkKind { Tailscale, Radmin, Local, Other }

/// <summary>Um IPv4 deste PC e de que rede ele é.</summary>
public sealed record LocalNetworkAddress(string Adapter, IPAddress Address, NetworkKind Kind, bool IsUp)
{
    public string Label => LocalNetworks.Label(Kind);
}

/// <summary>
/// As redes deste PC (1.4.1), no Windows e no Linux: o Tailscale (100.64.0.0/10), o Radmin VPN (26.0.0.0/8), a rede
/// local (10/8, 172.16/12, 192.168/16) e o resto.
/// </summary>
public static class LocalNetworks
{
    public static NetworkKind Classify(string? adapterName, string? adapterDescription, IPAddress address)
    {
        if (TailscaleAddress.IsTailscale(address) || TailscaleAddress.IsTailscaleAdapterName(adapterName) || TailscaleAddress.IsTailscaleAdapterName(adapterDescription))
            return NetworkKind.Tailscale;
        if (RadminAddress.IsRadmin(address) || RadminAddress.IsRadminAdapterName(adapterName) || RadminAddress.IsRadminAdapterName(adapterDescription))
            return NetworkKind.Radmin;
        return IsPrivate(address) ? NetworkKind.Local : NetworkKind.Other;
    }

    public static bool IsPrivate(IPAddress address)
    {
        if (address.AddressFamily != AddressFamily.InterNetwork) return false;
        var b = address.GetAddressBytes();
        return b[0] == 10 || (b[0] == 172 && (b[1] & 0xF0) == 16) || (b[0] == 192 && b[1] == 168);
    }

    public static string Label(NetworkKind kind) => kind switch
    {
        NetworkKind.Tailscale => "Tailscale",
        NetworkKind.Radmin => "Radmin VPN",
        NetworkKind.Local => "rede local",
        _ => "outra rede",
    };

    /// <summary>Os IPv4 deste PC (sem loopback nem 169.254.x), o Tailscale e o Radmin primeiro.</summary>
    public static IReadOnlyList<LocalNetworkAddress> List()
    {
        var result = new List<LocalNetworkAddress>();
        NetworkInterface[] interfaces;
        try { interfaces = NetworkInterface.GetAllNetworkInterfaces(); }
        catch (NetworkInformationException) { return result; }
        foreach (var nic in interfaces)
        {
            if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
            IPInterfaceProperties properties;
            try { properties = nic.GetIPProperties(); }
            catch (NetworkInformationException) { continue; }
            catch (PlatformNotSupportedException) { continue; }
            foreach (var unicast in properties.UnicastAddresses)
            {
                var address = unicast.Address;
                if (address.AddressFamily != AddressFamily.InterNetwork || IPAddress.IsLoopback(address)) continue;
                if (address.GetAddressBytes() is [169, 254, ..]) continue;
                result.Add(new LocalNetworkAddress(nic.Name, address, Classify(nic.Name, nic.Description, address), nic.OperationalStatus == OperationalStatus.Up));
            }
        }
        return result.OrderBy(a => a.Kind).ThenBy(a => a.Adapter, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>O endereço deste PC que o sistema usa para chegar em <paramref name="remote"/> (null se não tem rota).</summary>
    public static IPAddress? RouteTo(IPAddress remote)
    {
        try
        {
            using var probe = new Socket(remote.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
            probe.Connect(remote, 9);
            return (probe.LocalEndPoint as IPEndPoint)?.Address;
        }
        catch (SocketException) { return null; }
    }

    /// <summary>
    /// "100.101.102.103", "servidor:47810" ou "meu-pc.tailnet.ts.net" → endereço e porta (a porta padrão se não veio).
    /// Nomes são resolvidos pelo DNS (o MagicDNS do Tailscale resolve o nome dos PCs da rede).
    /// </summary>
    public static bool TrySplitHostPort(string? input, int defaultPort, out string host, out int port)
    {
        host = string.Empty;
        port = defaultPort;
        if (string.IsNullOrWhiteSpace(input)) return false;
        var text = input.Trim();
        if (text.Contains("://", StringComparison.Ordinal)) return false;
        var colon = text.LastIndexOf(':');
        if (colon > 0 && text.IndexOf(':') == colon)
        {
            if (!int.TryParse(text[(colon + 1)..], out port) || port is < 1 or > 65535) return false;
            text = text[..colon];
        }
        if (text.Length is 0 or > 253 || text.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_'))) return false;
        host = text;
        return true;
    }

    /// <summary>O IPv4 do servidor: o texto já é um IP ou o nome resolve para um (null se não deu).</summary>
    public static async Task<IPAddress?> ResolveAsync(string host, CancellationToken token)
    {
        if (IPAddress.TryParse(host, out var parsed)) return parsed.AddressFamily == AddressFamily.InterNetwork ? parsed : null;
        try
        {
            var addresses = await Dns.GetHostAddressesAsync(host, AddressFamily.InterNetwork, token).ConfigureAwait(false);
            return addresses.FirstOrDefault(a => TailscaleAddress.IsTailscale(a)) ?? addresses.FirstOrDefault();
        }
        catch (SocketException) { return null; }
        catch (ArgumentException) { return null; }
    }
}
