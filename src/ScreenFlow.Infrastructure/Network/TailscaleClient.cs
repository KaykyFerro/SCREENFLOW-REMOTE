using System.ComponentModel;
using System.Diagnostics;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using ScreenFlow.Core;
using ScreenFlow.Core.Diagnostics;
using ScreenFlow.Core.Network;
using ScreenFlow.Infrastructure.Security;

namespace ScreenFlow.Infrastructure.Network;

/// <summary>
/// O programa <c>tailscale</c> deste PC (1.4.1): acha onde ele está, lê o estado (<c>tailscale status --json</c>) e liga
/// (<c>tailscale up</c>). Nunca usa chave de autenticação: quando falta entrar na conta, o próprio Tailscale imprime o
/// link do login, que abre no navegador da pessoa.
/// </summary>
public static class TailscaleCli
{
    /// <summary>O tailscale.exe (Windows) ou tailscale (Linux), ou null se o Tailscale não está instalado.</summary>
    public static string? Find()
    {
        if (OperatingSystem.IsWindows())
        {
            foreach (var folder in new[] { Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86 })
            {
                var root = Environment.GetFolderPath(folder);
                if (root.Length > 0 && File.Exists(Path.Combine(root, "Tailscale", "tailscale.exe"))) return Path.Combine(root, "Tailscale", "tailscale.exe");
            }
        }
        var name = OperatingSystem.IsWindows() ? "tailscale.exe" : "tailscale";
        foreach (var folder in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var path = Path.Combine(folder.Trim().Trim('"'), name);
                if (File.Exists(path)) return path;
            }
            catch (ArgumentException) { }
        }
        return null;
    }

    /// <summary>Abre o Tailscale (o ícone perto do relógio, onde se entra e sai da conta). false se não achou.</summary>
    public static bool OpenApp()
    {
        if (!OperatingSystem.IsWindows() || Find() is not { } cli) return false;
        var app = Path.Combine(Path.GetDirectoryName(cli)!, "tailscale-ipn.exe");
        if (!File.Exists(app)) return false;
        try
        {
            Process.Start(new ProcessStartInfo(app) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(app)! });
            return true;
        }
        catch (Exception exception)
        {
            Log.Warn($"O Tailscale não abriu: {exception.Message}");
            return false;
        }
    }

    /// <summary>O estado do Tailscale agora (null se não está instalado ou não respondeu).</summary>
    public static async Task<TailscaleStatus?> StatusAsync(CancellationToken token = default)
    {
        if (Find() is not { } cli) return null;
        var (_, output) = await RunAsync(cli, ["status", "--json"], TimeSpan.FromSeconds(6), token).ConfigureAwait(false);
        return output.Length == 0 ? null : TailscaleStatus.Parse(output);
    }

    /// <summary>
    /// Liga o Tailscale (<c>tailscale up</c>, com <c>--unattended</c> no PC servidor do Windows: continua ligado sem
    /// ninguém logado). Se precisar entrar na conta, <paramref name="onLoginUrl"/> recebe o link do login (fora da thread
    /// da interface) e a espera continua até a pessoa entrar, por até <paramref name="wait"/>. Devolve null quando ligou,
    /// ou o motivo de não ter ligado.
    /// </summary>
    public static async Task<string?> UpAsync(bool unattended, Action<string> onLoginUrl, TimeSpan wait, CancellationToken token)
    {
        if (Find() is not { } cli) return "O Tailscale não está instalado neste PC.";
        var info = new ProcessStartInfo(cli)
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        info.ArgumentList.Add("up");
        if (unattended && OperatingSystem.IsWindows()) info.ArgumentList.Add("--unattended");

        var output = new StringBuilder();
        var opened = 0;
        void OnLine(string? line)
        {
            if (line is null) return;
            lock (output)
                if (output.Length < 16_384) output.AppendLine(line);
            if (TailscaleLogin.FindLoginUrl(line) is { } url && Interlocked.Exchange(ref opened, 1) == 0) onLoginUrl(url);
        }

        Process? process;
        try { process = Process.Start(info); }
        catch (Win32Exception exception)
        {
            Log.Warn($"tailscale up não abriu: {exception.Message}");
            return "O Tailscale não abriu. Ligue pelo ícone do Tailscale, perto do relógio.";
        }
        if (process is null) return "O Tailscale não abriu. Ligue pelo ícone do Tailscale, perto do relógio.";
        using (process)
        {
            process.OutputDataReceived += (_, e) => OnLine(e.Data);
            process.ErrorDataReceived += (_, e) => OnLine(e.Data);
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(wait);
            try { await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false); }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch (Exception exception) when (exception is InvalidOperationException or Win32Exception) { }
                token.ThrowIfCancellationRequested();
                return opened == 1
                    ? "O login do Tailscale não terminou a tempo. Entre na sua conta pelo navegador e clique em Tentar de novo."
                    : "O Tailscale não respondeu. Ligue pelo ícone do Tailscale, perto do relógio.";
            }
            if (process.ExitCode == 0) return null;
            string text;
            lock (output) text = output.ToString();
            Log.Info($"tailscale up terminou com o código {process.ExitCode}: {text.Trim()}");
            return text.Contains("non-default flags", StringComparison.OrdinalIgnoreCase)
                // A pessoa já configurou o Tailscale do jeito dela: o ScreenFlow não mexe nisso.
                ? "O Tailscale deste PC tem configurações próprias, então o ScreenFlow não liga por você. Ligue pelo ícone do Tailscale, perto do relógio."
                : "O Tailscale não ligou. Ligue pelo ícone do Tailscale, perto do relógio.";
        }
    }

    /// <summary>Roda o tailscale e devolve o código de saída e o que ele escreveu (vazio se não rodou ou demorou).</summary>
    private static async Task<(int Code, string Output)> RunAsync(string cli, string[] arguments, TimeSpan limit, CancellationToken token)
    {
        var info = new ProcessStartInfo(cli)
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
        };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        try
        {
            using var process = Process.Start(info);
            if (process is null) return (-1, string.Empty);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(limit);
            var read = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var errors = process.StandardError.ReadToEndAsync(timeout.Token);
            try
            {
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
                var output = await read.ConfigureAwait(false);
                await errors.ConfigureAwait(false);
                return (process.ExitCode, output.Length > 1_048_576 ? string.Empty : output);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch (Exception exception) when (exception is InvalidOperationException or Win32Exception) { }
                token.ThrowIfCancellationRequested();
                return (-1, string.Empty);
            }
        }
        catch (Win32Exception exception)
        {
            Log.Debug($"tailscale {string.Join(' ', arguments)}: {exception.Message}");
            return (-1, string.Empty);
        }
    }
}

/// <summary>
/// Instala o Tailscale no Windows pelo ScreenFlow (1.4.1): acha o MSI mais novo na página oficial de pacotes, baixa,
/// confere o SHA-256 publicado ao lado e a assinatura digital da Tailscale (o Windows diz se o arquivo está íntegro e
/// quem assinou) e roda o msiexec sem janelas, como administrador (o Windows pede permissão).
/// </summary>
public sealed class TailscaleInstaller : IDisposable
{
    private readonly HttpClient _http;

    public TailscaleInstaller()
    {
        _http = new HttpClient { Timeout = TimeSpan.FromMinutes(15) };
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue(AppInfo.Name, AppInfo.DisplayVersion));
    }

    /// <summary>O MSI mais novo da página de pacotes para este Windows (ou o da última versão conhecida).</summary>
    public async Task<Uri> FindInstallerAsync(CancellationToken token = default)
    {
        var arch = TailscaleDownload.Architecture(RuntimeInformation.OSArchitecture);
        var page = new Uri(TailscaleDownload.PackagesPage);
        try
        {
            using var response = await _http.GetAsync(page, token);
            response.EnsureSuccessStatusCode();
            var html = await response.Content.ReadAsStringAsync(token);
            if (html.Length < 4_000_000 && TailscaleDownload.FindInstaller(html, response.RequestMessage?.RequestUri ?? page, arch) is { } found) return found;
            Log.Warn("A página de pacotes do Tailscale não tem o link do MSI: usando a última versão conhecida.");
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException && !token.IsCancellationRequested)
        {
            Log.Warn($"A página de pacotes do Tailscale não abriu ({exception.Message}): usando a última versão conhecida.");
        }
        return TailscaleDownload.KnownInstaller(arch);
    }

    /// <summary>Baixa o MSI para uma pasta temporária e confere o SHA-256 publicado. <paramref name="progress"/> vai de 0 a 1.</summary>
    public async Task<string> DownloadAsync(Uri url, IProgress<double>? progress, CancellationToken token = default)
    {
        if (!TailscaleDownload.IsAllowed(url)) throw new InvalidDataException("O link do instalador não é da página oficial do Tailscale.");
        var folder = Path.Combine(Path.GetTempPath(), AppInfo.Name + "-tailscale");
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, TailscaleDownload.FileName(url));

        using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token);
        response.EnsureSuccessStatusCode();
        if (response.RequestMessage?.RequestUri is { Scheme: not "https" })
            throw new InvalidDataException("O download do Tailscale foi desviado para um endereço sem HTTPS.");
        var total = response.Content.Headers.ContentLength ?? 0;
        if (total > TailscaleDownload.MaxBytes) throw new InvalidDataException("O arquivo do Tailscale veio grande demais.");

        long read = 0;
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        try
        {
            await using var source = await response.Content.ReadAsStreamAsync(token);
            await using var target = File.Create(path);
            var buffer = new byte[128 * 1024];
            int n;
            while ((n = await source.ReadAsync(buffer, token)) > 0)
            {
                read += n;
                if (read > TailscaleDownload.MaxBytes) throw new InvalidDataException("O arquivo do Tailscale veio grande demais.");
                sha.AppendData(buffer, 0, n);
                await target.WriteAsync(buffer.AsMemory(0, n), token);
                if (total > 0) progress?.Report(Math.Min(1, read / (double)total));
            }
        }
        catch
        {
            TryDelete(path);
            throw;
        }
        if (read < TailscaleDownload.MinBytes || (total > 0 && read != total))
        {
            TryDelete(path);
            throw new InvalidDataException("O download do Tailscale veio incompleto. Tente de novo.");
        }

        // O SHA-256 que a página de pacotes publica ao lado do MSI (a assinatura digital é conferida depois, de qualquer jeito).
        var actual = Convert.ToHexStringLower(sha.GetHashAndReset());
        string? expected = null;
        try
        {
            using var hash = await _http.GetAsync(new Uri(url.AbsoluteUri + ".sha256"), token);
            if (hash.IsSuccessStatusCode && hash.Content.Headers.ContentLength is null or < 4096)
                expected = TailscaleDownload.ParseSha256(await hash.Content.ReadAsStringAsync(token));
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException && !token.IsCancellationRequested)
        {
            Log.Warn($"SHA-256 do Tailscale não baixou ({exception.Message}): fica só a assinatura digital.");
        }
        if (expected is not null && expected != actual)
        {
            TryDelete(path);
            throw new InvalidDataException("O MSI do Tailscale baixado não bate com o SHA-256 publicado. Ele foi apagado por segurança.");
        }
        Log.Info($"Tailscale baixado: {url} ({read / 1_048_576.0:0.0} MB, SHA-256 {(expected is null ? "não publicado" : "conferido")}).");
        return path;
    }

    /// <summary>
    /// Confere a assinatura digital do MSI: precisa ser válida para o Windows e ser da Tailscale. Devolve quem assinou;
    /// se não conferir, apaga o arquivo e explica.
    /// </summary>
    public static string VerifySignature(string path)
    {
        var status = Authenticode.Verify(path);
        if (status.Kind != AuthenticodeKind.Valid || status.Signer is not { } signer)
        {
            TryDelete(path);
            Log.Warn($"Assinatura do MSI do Tailscale não conferiu (0x{status.Code:X8}).");
            throw new InvalidDataException(status.Kind == AuthenticodeKind.Unsigned
                ? "O instalador baixado não tem assinatura digital. Ele foi apagado por segurança."
                : "A assinatura digital do instalador baixado não confere. Ele foi apagado por segurança.");
        }
        if (!signer.StartsWith(TailscaleDownload.Publisher, StringComparison.OrdinalIgnoreCase))
        {
            TryDelete(path);
            Log.Warn($"MSI do Tailscale assinado por \"{signer}\", não pela Tailscale.");
            throw new InvalidDataException($"O instalador baixado foi assinado por \"{signer}\", não pela Tailscale. Ele foi apagado por segurança.");
        }
        Log.Info($"MSI do Tailscale assinado por {signer}.");
        return signer;
    }

    /// <summary>
    /// Roda o msiexec sem janelas, como administrador, e espera terminar. Devolve o código de saída;
    /// <see cref="OperationCanceledException"/> se a pessoa recusar o pedido de permissão do Windows.
    /// </summary>
    public static async Task<int> InstallAsync(string msi, bool unattended, CancellationToken token = default)
    {
        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "msiexec.exe"), TailscaleDownload.SilentArguments(msi, unattended))
        {
            UseShellExecute = true,
            Verb = "runas",
            WorkingDirectory = Path.GetDirectoryName(msi)!,
        };
        Process? process;
        try { process = Process.Start(start); }
        catch (Win32Exception exception) when (exception.NativeErrorCode == 1223) // ERROR_CANCELLED: recusou o UAC
        {
            throw new OperationCanceledException("A permissão de administrador foi recusada.", exception);
        }
        if (process is null) throw new InvalidOperationException("O instalador do Tailscale não abriu.");
        using (process)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromMinutes(10));
            await process.WaitForExitAsync(timeout.Token);
            Log.Info($"Instalador do Tailscale terminou com o código {process.ExitCode}.");
            return process.ExitCode;
        }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { }
    }

    public void Dispose() => _http.Dispose();
}
