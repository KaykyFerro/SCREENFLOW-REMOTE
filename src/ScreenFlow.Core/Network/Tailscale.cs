using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ScreenFlow.Core.Network;

/// <summary>Regras de endereço do Tailscale (1.4.1): cada PC da rede ganha um IPv4 em 100.64.0.0/10.</summary>
public static class TailscaleAddress
{
    public static bool IsTailscale(IPAddress? address)
    {
        if (address is null || address.AddressFamily != AddressFamily.InterNetwork) return false;
        var bytes = address.GetAddressBytes();
        return bytes[0] == 100 && (bytes[1] & 0xC0) == 64;
    }

    /// <summary>"Tailscale" (Windows) ou "tailscale0" (Linux).</summary>
    public static bool IsTailscaleAdapterName(string? text)
        => !string.IsNullOrEmpty(text) && text.Contains("tailscale", StringComparison.OrdinalIgnoreCase);
}

/// <summary>Um PC da rede do Tailscale, como o <c>tailscale status --json</c> mostra.</summary>
public sealed record TailscalePeer(string HostName, string DnsName, IPAddress Address, string Os, bool Online);

/// <summary>
/// O estado do Tailscale neste PC (1.4.1): se está ligado, o endereço deste PC e os outros PCs da rede. O Tailscale não
/// repassa broadcast, então a sala e a descoberta falam direto com o endereço de cada PC daqui.
/// </summary>
public sealed record TailscaleStatus(string BackendState, IPAddress? Self, string? SelfName, IReadOnlyList<TailscalePeer> Peers, string? AuthUrl)
{
    public bool Running => BackendState == "Running";

    /// <summary>Precisa entrar na conta (o Tailscale está instalado mas ainda não logou).</summary>
    public bool NeedsLogin => BackendState is "NeedsLogin" or "NoState" || AuthUrl is { Length: > 0 };

    /// <summary>Lê a saída de <c>tailscale status --json</c>. null se não for o que o Tailscale imprime.</summary>
    public static TailscaleStatus? Parse(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 16 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            var state = root.TryGetProperty("BackendState", out var backend) && backend.ValueKind == JsonValueKind.String ? backend.GetString() ?? string.Empty : string.Empty;
            var auth = root.TryGetProperty("AuthURL", out var url) && url.ValueKind == JsonValueKind.String && url.GetString() is { Length: > 0 } link
                       && TailscaleLogin.IsLoginUrl(link) ? link : null;
            IPAddress? self = null;
            string? selfName = null;
            if (root.TryGetProperty("Self", out var me) && me.ValueKind == JsonValueKind.Object)
            {
                self = FirstIPv4(me);
                selfName = Text(me, "HostName");
            }
            self ??= root.TryGetProperty("TailscaleIPs", out var ips) ? FirstIPv4(ips) : null;
            var peers = new List<TailscalePeer>();
            if (root.TryGetProperty("Peer", out var map) && map.ValueKind == JsonValueKind.Object)
            {
                foreach (var entry in map.EnumerateObject())
                {
                    if (peers.Count >= 512) break;
                    var peer = entry.Value;
                    if (peer.ValueKind != JsonValueKind.Object || FirstIPv4(peer) is not { } address) continue;
                    peers.Add(new TailscalePeer(Text(peer, "HostName") ?? address.ToString(), (Text(peer, "DNSName") ?? string.Empty).TrimEnd('.'), address,
                        Text(peer, "OS") ?? string.Empty, peer.TryGetProperty("Online", out var online) && online.ValueKind == JsonValueKind.True));
                }
            }
            return new TailscaleStatus(state, self, selfName, peers, auth);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? Text(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    /// <summary>O primeiro IPv4 do Tailscale em "TailscaleIPs" (o objeto do PC ou a própria lista).</summary>
    private static IPAddress? FirstIPv4(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object && !element.TryGetProperty("TailscaleIPs", out element)) return null;
        if (element.ValueKind != JsonValueKind.Array) return null;
        foreach (var item in element.EnumerateArray())
            if (item.ValueKind == JsonValueKind.String && IPAddress.TryParse(item.GetString(), out var address) && TailscaleAddress.IsTailscale(address))
                return address;
        return null;
    }
}

/// <summary>O link de login que o <c>tailscale up</c> imprime: só endereços do próprio Tailscale, por HTTPS.</summary>
public static partial class TailscaleLogin
{
    [GeneratedRegex(@"https://login\.tailscale\.com/a/[A-Za-z0-9]{4,64}\b")]
    private static partial Regex LoginLink();

    public static bool IsLoginUrl(string? text)
        => text is { Length: > 0 } && Uri.TryCreate(text.Trim(), UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps && uri.IsDefaultPort
           && string.IsNullOrEmpty(uri.UserInfo) && uri.Host.Equals("login.tailscale.com", StringComparison.OrdinalIgnoreCase);

    /// <summary>O link de login no meio do que o <c>tailscale up</c> escreveu (null se não tem).</summary>
    public static string? FindLoginUrl(string output) => LoginLink().Match(output) is { Success: true } match ? match.Value : null;
}

/// <summary>
/// De onde baixar o Tailscale para Windows (1.4.1): o MSI da página oficial de pacotes (pkgs.tailscale.com), só por
/// HTTPS. Se a página mudar, usa a última versão conhecida. O SHA-256 vem do mesmo endereço com ".sha256" no fim e a
/// assinatura digital (da Tailscale) é conferida antes de instalar. Nunca usa chave de autenticação: quem instala entra
/// na própria conta pelo navegador.
/// </summary>
public static partial class TailscaleDownload
{
    public const string PackagesPage = "https://pkgs.tailscale.com/stable/";
    public const string HomePage = "https://tailscale.com/download";
    public const string KnownVersion = "1.104.7";

    /// <summary>Quem assina o instalador do Tailscale (o nome começa assim: "Tailscale Inc.").</summary>
    public const string Publisher = "Tailscale";

    /// <summary>
    /// MSI sem janelas e sem reiniciar. TS_UNATTENDEDMODE deixa o Tailscale ligado mesmo sem ninguém logado no Windows
    /// (o PC servidor continua na rede depois de reiniciar).
    /// </summary>
    public static string SilentArguments(string msi, bool unattended)
        => $"/i \"{msi}\" /quiet /norestart" + (unattended ? " TS_UNATTENDEDMODE=always" : string.Empty);

    /// <summary>O MSI tem uns 30 MB: menos que 1 MB é uma página de erro; mais que 300 MB não é o Tailscale.</summary>
    public const long MinBytes = 1_000_000;
    public const long MaxBytes = 300_000_000;

    [GeneratedRegex(@"tailscale-setup-(?<version>\d+\.\d+\.\d+)-(?<arch>amd64|arm64|x86)\.msi", RegexOptions.IgnoreCase)]
    private static partial Regex MsiName();

    /// <summary>A arquitetura do MSI para este Windows ("amd64", "arm64" ou "x86").</summary>
    public static string Architecture(System.Runtime.InteropServices.Architecture os) => os switch
    {
        System.Runtime.InteropServices.Architecture.Arm64 => "arm64",
        System.Runtime.InteropServices.Architecture.X86 => "x86",
        _ => "amd64",
    };

    public static Uri KnownInstaller(string arch) => new($"{PackagesPage}tailscale-setup-{KnownVersion}-{arch}.msi");

    /// <summary>O MSI mais novo da página de pacotes para a arquitetura (null se nenhum link serve).</summary>
    public static Uri? FindInstaller(string html, Uri page, string arch)
    {
        Uri? best = null;
        Version? bestVersion = null;
        foreach (Match match in MsiName().Matches(html))
        {
            if (!match.Groups["arch"].Value.Equals(arch, StringComparison.OrdinalIgnoreCase)) continue;
            if (!Version.TryParse(match.Groups["version"].Value, out var version)) continue;
            if (!Uri.TryCreate(page, match.Value, out var url) || !IsAllowed(url)) continue;
            if (bestVersion is not null && version <= bestVersion) continue;
            (best, bestVersion) = (url, version);
        }
        return best;
    }

    /// <summary>Só HTTPS, só pkgs.tailscale.com e só um arquivo "tailscale-setup-x.y.z-arq.msi".</summary>
    public static bool IsAllowed(Uri url)
        => url.IsAbsoluteUri
           && url.Scheme == Uri.UriSchemeHttps
           && url.IsDefaultPort
           && string.IsNullOrEmpty(url.UserInfo)
           && url.Host.Equals("pkgs.tailscale.com", StringComparison.OrdinalIgnoreCase)
           && url.AbsolutePath.StartsWith("/stable/", StringComparison.Ordinal)
           && MsiName().Match(FileName(url)) is { Success: true } match && match.Length == FileName(url).Length;

    public static string FileName(Uri url) => Path.GetFileName(url.AbsolutePath);

    public static string VersionOf(Uri url) => MsiName().Match(FileName(url)) is { Success: true } match ? match.Groups["version"].Value : string.Empty;

    /// <summary>O SHA-256 em hexadecimal no começo do arquivo ".sha256" (null se não for isso).</summary>
    public static string? ParseSha256(string text)
    {
        var first = text.Trim().Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        return first is { Length: 64 } && first.All(Uri.IsHexDigit) ? first.ToLowerInvariant() : null;
    }

    /// <summary>O que o código de saída do msiexec quer dizer; null quando deu certo.</summary>
    public static string? ExplainExitCode(int code) => code switch
    {
        0 or 3010 or 1641 => null, // 3010/1641: instalou e pede para reiniciar
        1602 => "A instalação do Tailscale foi cancelada.",
        1618 => "Outra instalação está rodando no Windows. Espere terminar e tente de novo.",
        1603 => "O instalador do Tailscale deu erro no meio da instalação. Tente de novo ou instale pelo site.",
        1638 => null, // já tem uma versão instalada
        _ => $"O instalador do Tailscale terminou com o erro {code}. Tente de novo ou instale pelo site.",
    };
}
