using System.Diagnostics;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using ScreenFlow.Core;
using ScreenFlow.Core.Diagnostics;
using ScreenFlow.Core.Updates;
using ScreenFlow.Infrastructure.Security;

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

    /// <summary>
    /// Baixa o instalador e confere antes de devolver: só HTTPS do GitHub (também depois dos redirecionamentos), SHA-256
    /// obrigatório e igual ao publicado, tamanho igual ao anunciado e, se o instalador for assinado, assinatura
    /// Authenticode válida. O arquivo é baixado com outro nome e só ganha o nome final depois de conferido; cancelando
    /// ou falhando, nada fica para trás.
    /// </summary>
    public async Task<string> DownloadAsync(ReleaseInfo release, IProgress<double>? progress, CancellationToken token = default)
    {
        if (UpdateIntegrity.Problem(release) is { } problem) throw new InvalidDataException(problem);

        // O SHA-256 vem primeiro: sem ele, nem baixa o instalador.
        var expected = UpdateIntegrity.ParseChecksum(await GetTrustedStringAsync(release.Sha256Url!, token))
                       ?? throw new InvalidDataException("O arquivo .sha256 do release está num formato que o ScreenFlow não entende. Atualização cancelada.");

        var folder = Path.Combine(Path.GetTempPath(), AppInfo.Name + "-update");
        Directory.CreateDirectory(folder);
        var name = Path.GetFileName(release.InstallerName ?? "ScreenFlow-Setup.exe");
        if (!name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name = "ScreenFlow-Setup.exe";
        var path = Path.Combine(folder, name);
        var partial = path + ".partial";
        TryDelete(partial);
        try
        {
            long read = 0;
            using (var response = await _http.GetAsync(release.InstallerUrl, HttpCompletionOption.ResponseHeadersRead, token))
            {
                EnsureTrustedFinalUrl(response);
                response.EnsureSuccessStatusCode();
                var total = response.Content.Headers.ContentLength ?? release.InstallerSize;
                await using var source = await response.Content.ReadAsStreamAsync(token);
                await using var target = File.Create(partial);
                var buffer = new byte[128 * 1024];
                int n;
                while ((n = await source.ReadAsync(buffer, token)) > 0)
                {
                    await target.WriteAsync(buffer.AsMemory(0, n), token);
                    read += n;
                    if (total > 0) progress?.Report(Math.Min(1, read / (double)total));
                }
            }
            if (!UpdateIntegrity.SizeMatches(release.InstallerSize, read))
                throw new InvalidDataException("O download do instalador veio incompleto (a conexão pode ter caído). Tente de novo.");

            string actual;
            await using (var file = File.OpenRead(partial))
                actual = Convert.ToHexString(await SHA256.HashDataAsync(file, token)).ToLowerInvariant();
            if (actual != expected)
                throw new InvalidDataException("O instalador baixado não confere com o SHA-256 publicado. Atualização cancelada.");

            VerifyAuthenticode(partial);
            TryDelete(path);
            File.Move(partial, path);
            return path;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            TryDelete(partial);
            throw;
        }
        catch (HttpRequestException exception)
        {
            TryDelete(partial);
            throw new InvalidDataException("A conexão caiu no meio do download. Confira a internet e tente de novo.", exception);
        }
        catch (IOException exception)
        {
            TryDelete(partial);
            throw new InvalidDataException($"O download do instalador falhou ({exception.Message}). Tente de novo.", exception);
        }
        catch
        {
            TryDelete(partial);
            throw;
        }
    }

    private async Task<string> GetTrustedStringAsync(string url, CancellationToken token)
    {
        try
        {
            using var response = await _http.GetAsync(url, token);
            EnsureTrustedFinalUrl(response);
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                throw new InvalidDataException("O arquivo .sha256 do release não foi encontrado. Atualização cancelada.");
            response.EnsureSuccessStatusCode();
            var text = await response.Content.ReadAsStringAsync(token);
            return text.Length <= 4096 ? text : text[..4096];
        }
        catch (HttpRequestException exception)
        {
            throw new InvalidDataException("Não deu para baixar o SHA-256 do release. Confira a internet e tente de novo.", exception);
        }
    }

    /// <summary>Depois dos redirecionamentos, o arquivo ainda tem que vir por HTTPS de um servidor do GitHub.</summary>
    private static void EnsureTrustedFinalUrl(HttpResponseMessage response)
    {
        var final = response.RequestMessage?.RequestUri;
        if (final is null || !UpdateIntegrity.IsTrustedUrl(final))
            throw new InvalidDataException("O download foi redirecionado para fora do GitHub. Atualização cancelada.");
    }

    /// <summary>
    /// Assinatura Authenticode: um instalador assinado precisa ter a assinatura válida para o Windows (adulterado ou com o
    /// certificado revogado, é recusado). Os instaladores ainda saem sem assinatura (o projeto não tem certificado de
    /// assinatura de código); nesse caso vale o SHA-256 conferido acima. Fora do Windows (testes), não confere.
    /// </summary>
    private static void VerifyAuthenticode(string path)
    {
        if (!OperatingSystem.IsWindows()) return;
        var status = Authenticode.Verify(path);
        switch (status.Kind)
        {
            case AuthenticodeKind.Valid:
                Log.Info($"Instalador assinado por {status.Signer}.");
                break;
            case AuthenticodeKind.Unsigned:
                Log.Info("Instalador sem assinatura Authenticode: conferido só pelo SHA-256 publicado.");
                break;
            default:
                Log.Warn($"Assinatura do instalador não confere (0x{status.Code:X8}).");
                throw new InvalidDataException("A assinatura digital do instalador baixado não confere. Ele foi apagado por segurança.");
        }
    }

    /// <summary>
    /// Roda o instalador silencioso; ele fecha este processo e abre a nova versão no fim. Se o Windows não deixar (pedido
    /// de permissão recusado, antivírus), explica em vez de deixar o app parado.
    /// </summary>
    public static void LaunchInstaller(string installerPath)
    {
        Log.Info($"Iniciando instalador {installerPath}");
        Log.Flush();
        try
        {
            using var process = Process.Start(new ProcessStartInfo(installerPath, "/SILENT /SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS /RESTARTAPPLICATIONS")
            {
                UseShellExecute = true,
            });
            if (process is null) throw new InvalidOperationException("O Windows não abriu o instalador.");
        }
        catch (System.ComponentModel.Win32Exception exception) when (exception.NativeErrorCode == 1223)
        {
            throw new InvalidOperationException("O pedido de permissão do Windows foi recusado, então a atualização não foi instalada.", exception);
        }
        catch (System.ComponentModel.Win32Exception exception)
        {
            throw new InvalidOperationException($"O Windows não deixou abrir o instalador ({exception.Message}). Um antivírus pode ter bloqueado.", exception);
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { Log.Debug($"Não apagou {path}: {exception.Message}"); }
    }

    public void Dispose() => _http.Dispose();
}
