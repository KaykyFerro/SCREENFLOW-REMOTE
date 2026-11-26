using System.Text.RegularExpressions;

namespace ScreenFlow.Core.Network;

/// <summary>
/// De onde e como baixar o Radmin VPN: o link do instalador que está na página oficial (a versão muda de tempos em
/// tempos), só do site da Famatech e por HTTPS. Se a página mudar e o link sumir, usa a última versão conhecida.
/// O instalador é Inno Setup e é assinado pela Famatech; a assinatura é conferida antes de rodar.
/// </summary>
public static partial class RadminDownload
{
    public const string HomePage = "https://www.radmin-vpn.com/";
    public const string KnownVersion = "2.1.4952.2";

    /// <summary>Quem assina o instalador e os programas do Radmin VPN.</summary>
    public const string Publisher = "Famatech";

    /// <summary>Inno Setup sem janelas, sem perguntas e sem reiniciar o PC.</summary>
    public const string SilentArguments = "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SP-";

    /// <summary>O instalador tem uns 55 MB: menos que 1 MB é uma página de erro; mais que 300 MB não é o Radmin.</summary>
    public const long MinBytes = 1_000_000;
    public const long MaxBytes = 300_000_000;

    private static readonly string[] Hosts = ["download.radmin-vpn.com", "www.radmin-vpn.com", "radmin-vpn.com"];

    public static Uri KnownInstaller { get; } = new($"https://download.radmin-vpn.com/download/files/Radmin_VPN_{KnownVersion}.exe");

    [GeneratedRegex("""["'](?<url>[^"'<>\s]*?Radmin_VPN_(?<version>\d+(?:\.\d+){1,3})\.exe)["']""", RegexOptions.IgnoreCase)]
    private static partial Regex InstallerLink();

    [GeneratedRegex(@"^Radmin_VPN_(?<version>\d+(?:\.\d+){1,3})\.exe$", RegexOptions.IgnoreCase)]
    private static partial Regex InstallerName();

    /// <summary>O link do instalador mais novo que aparece na página (null se nenhum link serve).</summary>
    public static Uri? FindInstaller(string html, Uri page)
    {
        Uri? best = null;
        Version? bestVersion = null;
        foreach (Match match in InstallerLink().Matches(html))
        {
            if (!Uri.TryCreate(page, match.Groups["url"].Value, out var url) || !IsAllowed(url)) continue;
            if (!Version.TryParse(match.Groups["version"].Value, out var version)) continue;
            if (bestVersion is not null && version <= bestVersion) continue;
            (best, bestVersion) = (url, version);
        }
        return best;
    }

    /// <summary>Só HTTPS, só os endereços da Famatech e só um arquivo "Radmin_VPN_x.y.z.exe".</summary>
    public static bool IsAllowed(Uri url)
        => url.IsAbsoluteUri
           && url.Scheme == Uri.UriSchemeHttps
           && url.IsDefaultPort
           && string.IsNullOrEmpty(url.UserInfo)
           && Hosts.Contains(url.Host, StringComparer.OrdinalIgnoreCase)
           && InstallerName().IsMatch(FileName(url));

    public static string FileName(Uri url) => Path.GetFileName(url.AbsolutePath);

    /// <summary>"2.1.4952.2" a partir do link (vazio se não der).</summary>
    public static string VersionOf(Uri url) => InstallerName().Match(FileName(url)) is { Success: true } match ? match.Groups["version"].Value : string.Empty;

    /// <summary>O que o código de saída do instalador (Inno Setup) quer dizer; null quando deu certo.</summary>
    public static string? ExplainExitCode(int code) => code switch
    {
        0 => null,
        1 or 3 => "O instalador do Radmin VPN não conseguiu começar. Tente de novo.",
        2 or 5 => "A instalação do Radmin VPN foi cancelada.",
        4 => "O instalador do Radmin VPN deu erro no meio da instalação. Tente de novo ou instale pelo site.",
        8 => "O Windows precisa reiniciar antes de instalar o Radmin VPN. Reinicie o PC e tente de novo.",
        _ => $"O instalador do Radmin VPN terminou com o erro {code}. Tente de novo ou instale pelo site.",
    };
}
