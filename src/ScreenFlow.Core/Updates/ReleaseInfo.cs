using System.Text.Json;

namespace ScreenFlow.Core.Updates;

public sealed record ReleaseInfo(Version Version, string Tag, string Name, string Notes, string? InstallerUrl, string? InstallerName, long InstallerSize, string? Sha256Url);

/// <summary>Lê a resposta de /repos/{owner}/{repo}/releases/latest do GitHub e compara versões.</summary>
public static class ReleaseParser
{
    public const string InstallerSuffix = "-Setup-win-x64.exe";

    public static bool TryParseVersion(string? tag, out Version version)
    {
        version = new Version(0, 0, 0);
        if (string.IsNullOrWhiteSpace(tag)) return false;
        var text = tag.Trim().TrimStart('v', 'V');
        var dash = text.IndexOfAny(['-', '+']);
        if (dash > 0) text = text[..dash];
        if (!Version.TryParse(text, out var parsed)) return false;
        version = new Version(parsed.Major, parsed.Minor, Math.Max(0, parsed.Build));
        return true;
    }

    public static ReleaseInfo? Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.TryGetProperty("draft", out var draft) && draft.GetBoolean()) return null;
        if (root.TryGetProperty("prerelease", out var pre) && pre.GetBoolean()) return null;
        var tag = root.GetProperty("tag_name").GetString();
        if (!TryParseVersion(tag, out var version)) return null;

        string? url = null, name = null, sha = null;
        long size = 0;
        if (root.TryGetProperty("assets", out var assets))
        {
            foreach (var asset in assets.EnumerateArray())
            {
                var assetName = asset.GetProperty("name").GetString() ?? string.Empty;
                var download = asset.GetProperty("browser_download_url").GetString();
                if (assetName.EndsWith(InstallerSuffix, StringComparison.OrdinalIgnoreCase))
                {
                    url = download;
                    name = assetName;
                    size = asset.TryGetProperty("size", out var s) ? s.GetInt64() : 0;
                }
                else if (assetName.EndsWith(InstallerSuffix + ".sha256", StringComparison.OrdinalIgnoreCase))
                {
                    sha = download;
                }
            }
        }

        return new ReleaseInfo(version, tag!,
            root.TryGetProperty("name", out var n) ? n.GetString() ?? tag! : tag!,
            root.TryGetProperty("body", out var b) ? b.GetString() ?? string.Empty : string.Empty,
            url, name, size, sha);
    }

    public static bool IsNewer(ReleaseInfo release, Version current)
        => release.Version > new Version(current.Major, current.Minor, Math.Max(0, current.Build));
}
