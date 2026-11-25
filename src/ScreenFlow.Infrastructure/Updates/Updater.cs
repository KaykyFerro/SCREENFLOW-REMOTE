using System.Diagnostics;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using ScreenFlow.Core;
using ScreenFlow.Core.Diagnostics;
using ScreenFlow.Core.Updates;

namespace ScreenFlow.Infrastructure.Updates;

/// <summary>
/// Atualização pelo próprio app: consulta o último release no GitHub, baixa o instalador, confere o SHA-256
/// publicado junto e roda o instalador em modo silencioso, que fecha e reabre o ScreenFlow.
/// </summary>
public sealed class Updater : IDisposable
{
    private readonly HttpClient _http;

    public Updater()
    {
        _http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue(AppInfo.Name, AppInfo.DisplayVersion));
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
    }

    public static string LatestReleaseUrl => $"https://api.github.com/repos/{AppInfo.GitHubOwner}/{AppInfo.GitHubRepository}/releases/latest";
    public static string ReleasesPage => $"https://github.com/{AppInfo.GitHubOwner}/{AppInfo.GitHubRepository}/releases";

    /// <summary>Retorna o release se houver versão mais nova com instalador; senão null.</summary>
    public async Task<ReleaseInfo?> CheckAsync(CancellationToken token = default)
    {
        using var response = await _http.GetAsync(LatestReleaseUrl, token);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return null; // ainda não há release publicado
        response.EnsureSuccessStatusCode();
        var release = ReleaseParser.Parse(await response.Content.ReadAsStringAsync(token));
        if (release is null || release.InstallerUrl is null) return null;
        Log.Info($"Último release: {release.Tag} (atual {AppInfo.DisplayVersion})");
        return ReleaseParser.IsNewer(release, AppInfo.Version) ? release : null;
    }

    public async Task<string> DownloadAsync(ReleaseInfo release, IProgress<double>? progress, CancellationToken token = default)
    {
        var folder = Path.Combine(Path.GetTempPath(), AppInfo.Name + "-update");
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, release.InstallerName ?? "ScreenFlow-Setup.exe");

        using (var response = await _http.GetAsync(release.InstallerUrl, HttpCompletionOption.ResponseHeadersRead, token))
        {
            response.EnsureSuccessStatusCode();
            var total = response.Content.Headers.ContentLength ?? release.InstallerSize;
            await using var source = await response.Content.ReadAsStreamAsync(token);
            await using var target = File.Create(path);
            var buffer = new byte[128 * 1024];
            long read = 0;
            int n;
            while ((n = await source.ReadAsync(buffer, token)) > 0)
            {
                await target.WriteAsync(buffer.AsMemory(0, n), token);
                read += n;
                if (total > 0) progress?.Report(read / (double)total);
            }
        }

        if (release.Sha256Url is not null)
        {
            var expected = (await _http.GetStringAsync(release.Sha256Url, token)).Trim().Split(' ', '\t', '\n')[0].ToLowerInvariant();
            await using var file = File.OpenRead(path);
            var actual = Convert.ToHexString(await SHA256.HashDataAsync(file, token)).ToLowerInvariant();
            if (actual != expected)
            {
                File.Delete(path);
                throw new InvalidDataException("O instalador baixado não confere com o SHA-256 publicado. Atualização cancelada.");
            }
        }
        return path;
    }

    /// <summary>Roda o instalador silencioso; ele fecha este processo e abre a nova versão no fim.</summary>
    public static void LaunchInstaller(string installerPath)
    {
        Log.Info($"Iniciando instalador {installerPath}");
        Log.Flush();
        Process.Start(new ProcessStartInfo(installerPath, "/SILENT /SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS /RESTARTAPPLICATIONS")
        {
            UseShellExecute = true,
        });
    }

    public void Dispose() => _http.Dispose();
}
