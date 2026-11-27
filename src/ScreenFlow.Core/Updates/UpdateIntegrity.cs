namespace ScreenFlow.Core.Updates;

/// <summary>
/// Regras de segurança da atualização pelo app (1.4.0): o instalador e o SHA-256 só vêm por HTTPS de endereços do GitHub,
/// o SHA-256 é obrigatório e precisa ter o formato certo, e o tamanho baixado tem que bater com o anunciado. Qualquer
/// falha cancela a atualização antes de rodar o instalador.
/// </summary>
public static class UpdateIntegrity
{
    /// <summary>Onde o GitHub guarda os arquivos dos releases (o download começa no github.com e é redirecionado para cá).</summary>
    private static readonly string[] TrustedHosts =
    [
        "github.com",
        "api.github.com",
        "objects.githubusercontent.com",
        "release-assets.githubusercontent.com",
        "github-releases.githubusercontent.com",
    ];

    /// <summary>O endereço é HTTPS e de um dos servidores do GitHub (inclusive depois de um redirecionamento).</summary>
    public static bool IsTrustedUrl(string? url)
        => Uri.TryCreate(url, UriKind.Absolute, out var uri) && IsTrustedUrl(uri);

    public static bool IsTrustedUrl(Uri uri)
        => uri.Scheme == Uri.UriSchemeHttps && uri.IsDefaultPort && string.IsNullOrEmpty(uri.UserInfo)
           && TrustedHosts.Contains(uri.IdnHost, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// O hash do arquivo .sha256 (o formato do <c>sha256sum</c>: "hash  nome" ou só o hash), em minúsculas. Null se não
    /// tiver um hash de 64 dígitos hexadecimais no começo.
    /// </summary>
    public static string? ParseChecksum(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var first = text.Trim().TrimStart('﻿').Split([' ', '\t', '\r', '\n', '*'], 2, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        if (first is null || first.Length != 64 || !first.All(Uri.IsHexDigit)) return null;
        return first.ToLowerInvariant();
    }

    /// <summary>O que falta para o release poder ser instalado pelo app. Null se está tudo certo.</summary>
    public static string? Problem(ReleaseInfo release)
    {
        if (release.InstallerUrl is null) return "O release não tem o instalador.";
        if (!IsTrustedUrl(release.InstallerUrl)) return "O instalador não vem por HTTPS do GitHub. Atualização cancelada.";
        if (release.Sha256Url is null) return "O release não tem o arquivo .sha256 do instalador. Sem ele não dá para conferir o download, então a atualização foi cancelada.";
        if (!IsTrustedUrl(release.Sha256Url)) return "O arquivo .sha256 não vem por HTTPS do GitHub. Atualização cancelada.";
        return null;
    }

    /// <summary>O download chegou inteiro (o GitHub informa o tamanho do arquivo; 0 quando não informa).</summary>
    public static bool SizeMatches(long expected, long actual) => expected <= 0 || expected == actual;
}
