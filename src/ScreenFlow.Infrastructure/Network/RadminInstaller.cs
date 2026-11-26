using System.ComponentModel;
using System.Diagnostics;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Win32;
using ScreenFlow.Core;
using ScreenFlow.Core.Diagnostics;
using ScreenFlow.Core.Network;

namespace ScreenFlow.Infrastructure.Network;

/// <summary>
/// Instala o Radmin VPN pelo ScreenFlow: acha o instalador na página oficial, baixa, confere a assinatura digital da
/// Famatech (o Windows diz se o arquivo está íntegro e quem assinou) e roda o instalador sem janelas, como
/// administrador (o Windows pede permissão). Também acha e abre o Radmin VPN já instalado.
/// </summary>
public sealed class RadminInstaller : IDisposable
{
    private readonly HttpClient _http;

    public RadminInstaller()
    {
        _http = new HttpClient { Timeout = TimeSpan.FromMinutes(15) };
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue(AppInfo.Name, AppInfo.DisplayVersion));
    }

    /// <summary>O link do instalador mais novo da página oficial (ou o da última versão conhecida, se a página não abrir).</summary>
    public async Task<Uri> FindInstallerAsync(CancellationToken token = default)
    {
        var page = new Uri(RadminDownload.HomePage);
        try
        {
            using var response = await _http.GetAsync(page, token);
            response.EnsureSuccessStatusCode();
            var html = await response.Content.ReadAsStringAsync(token);
            if (RadminDownload.FindInstaller(html, response.RequestMessage?.RequestUri ?? page) is { } found) return found;
            Log.Warn("A página do Radmin VPN não tem o link do instalador: usando a última versão conhecida.");
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException && !token.IsCancellationRequested)
        {
            Log.Warn($"A página do Radmin VPN não abriu ({exception.Message}): usando a última versão conhecida.");
        }
        return RadminDownload.KnownInstaller;
    }

    /// <summary>Baixa o instalador para uma pasta temporária. <paramref name="progress"/> vai de 0 a 1.</summary>
    public async Task<string> DownloadAsync(Uri url, IProgress<double>? progress, CancellationToken token = default)
    {
        if (!RadminDownload.IsAllowed(url)) throw new InvalidDataException("O link do instalador não é do site do Radmin VPN.");
        var folder = Path.Combine(Path.GetTempPath(), AppInfo.Name + "-radmin");
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, RadminDownload.FileName(url));

        using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token);
        response.EnsureSuccessStatusCode();
        if (response.RequestMessage?.RequestUri is { Scheme: not "https" })
            throw new InvalidDataException("O download do Radmin VPN foi desviado para um endereço sem HTTPS.");
        var total = response.Content.Headers.ContentLength ?? 0;
        if (total > RadminDownload.MaxBytes) throw new InvalidDataException("O arquivo do Radmin VPN veio grande demais.");

        long read = 0;
        try
        {
            await using var source = await response.Content.ReadAsStreamAsync(token);
            await using var target = File.Create(path);
            var buffer = new byte[128 * 1024];
            int n;
            while ((n = await source.ReadAsync(buffer, token)) > 0)
            {
                read += n;
                if (read > RadminDownload.MaxBytes) throw new InvalidDataException("O arquivo do Radmin VPN veio grande demais.");
                await target.WriteAsync(buffer.AsMemory(0, n), token);
                if (total > 0) progress?.Report(Math.Min(1, read / (double)total));
            }
        }
        catch
        {
            TryDelete(path);
            throw;
        }
        if (read < RadminDownload.MinBytes || (total > 0 && read != total))
        {
            TryDelete(path);
            throw new InvalidDataException("O download do Radmin VPN veio incompleto. Tente de novo.");
        }
        Log.Info($"Radmin VPN baixado: {url} ({read / 1_048_576.0:0.0} MB).");
        return path;
    }

    /// <summary>
    /// Confere a assinatura digital do instalador: precisa ser válida para o Windows (arquivo íntegro, certificado
    /// confiável) e ser da Famatech. Devolve o nome de quem assinou; se não conferir, apaga o arquivo e explica.
    /// </summary>
    public static string VerifySignature(string path)
    {
        var result = WinVerifyTrustFile(path);
        if (result != 0)
        {
            TryDelete(path);
            Log.Warn($"Assinatura do instalador do Radmin VPN não conferiu (0x{result:X8}).");
            throw new InvalidDataException(result == unchecked((int)0x800B0100)
                ? "O instalador baixado não tem assinatura digital. Ele foi apagado por segurança."
                : "A assinatura digital do instalador baixado não confere. Ele foi apagado por segurança.");
        }
        string signer;
        try
        {
#pragma warning disable SYSLIB0057 // o certificado de quem assinou o arquivo (o Windows já validou acima)
            using var certificate = new X509Certificate2(X509Certificate.CreateFromSignedFile(path));
#pragma warning restore SYSLIB0057
            signer = certificate.GetNameInfo(X509NameType.SimpleName, false);
        }
        catch (Exception exception)
        {
            TryDelete(path);
            throw new InvalidDataException("Não deu para ler quem assinou o instalador. Ele foi apagado por segurança.", exception);
        }
        if (!signer.StartsWith(RadminDownload.Publisher, StringComparison.OrdinalIgnoreCase))
        {
            TryDelete(path);
            Log.Warn($"Instalador do Radmin VPN assinado por \"{signer}\", não pela Famatech.");
            throw new InvalidDataException($"O instalador baixado foi assinado por \"{signer}\", não pela Famatech. Ele foi apagado por segurança.");
        }
        Log.Info($"Instalador do Radmin VPN assinado por {signer}.");
        return signer;
    }

    /// <summary>
    /// Roda o instalador sem janelas, como administrador, e espera terminar. Devolve o código de saída;
    /// <see cref="OperationCanceledException"/> se a pessoa recusar o pedido de permissão do Windows.
    /// </summary>
    public static async Task<int> InstallAsync(string path, CancellationToken token = default)
    {
        var start = new ProcessStartInfo(path, RadminDownload.SilentArguments)
        {
            UseShellExecute = true,
            Verb = "runas",
            WorkingDirectory = Path.GetDirectoryName(path)!,
        };
        Process? process;
        try { process = Process.Start(start); }
        catch (Win32Exception exception) when (exception.NativeErrorCode == 1223) // ERROR_CANCELLED: recusou o UAC
        {
            throw new OperationCanceledException("A permissão de administrador foi recusada.", exception);
        }
        if (process is null) throw new InvalidOperationException("O instalador do Radmin VPN não abriu.");
        using (process)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromMinutes(10));
            await process.WaitForExitAsync(timeout.Token);
            Log.Info($"Instalador do Radmin VPN terminou com o código {process.ExitCode}.");
            return process.ExitCode;
        }
    }

    /// <summary>O programa do Radmin VPN (a janela onde se entra numa rede), se estiver instalado.</summary>
    public static string? FindApp()
    {
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            try
            {
                using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
                using var uninstall = root.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
                if (uninstall is null) continue;
                foreach (var name in uninstall.GetSubKeyNames())
                {
                    using var key = uninstall.OpenSubKey(name);
                    if (key?.GetValue("DisplayName") is not string display || !display.StartsWith("Radmin VPN", StringComparison.OrdinalIgnoreCase)) continue;
                    if (key.GetValue("DisplayIcon") is string icon && Executable(icon) is { } fromIcon) return fromIcon;
                    if (key.GetValue("InstallLocation") is string folder && Executable(Path.Combine(folder.Trim('"'), "RvRvpnGui.exe")) is { } inFolder) return inFolder;
                }
            }
            catch (Exception exception) { Log.Debug($"Procurando o Radmin VPN no registro: {exception.Message}"); }
        }
        foreach (var folder in new[] { Environment.SpecialFolder.ProgramFilesX86, Environment.SpecialFolder.ProgramFiles })
        {
            var root = Environment.GetFolderPath(folder);
            if (root.Length > 0 && Executable(Path.Combine(root, "Radmin VPN", "RvRvpnGui.exe")) is { } known) return known;
        }
        return null;
    }

    /// <summary>Abre a janela do Radmin VPN. false se ele não estiver instalado (ou não abriu).</summary>
    public static bool OpenApp()
    {
        if (FindApp() is not { } app) return false;
        try
        {
            Process.Start(new ProcessStartInfo(app) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(app)! });
            return true;
        }
        catch (Exception exception)
        {
            Log.Warn($"O Radmin VPN não abriu: {exception.Message}");
            return false;
        }
    }

    /// <summary>"C:\...\RvRvpnGui.exe,0" ou com aspas → o caminho, se for um .exe que existe.</summary>
    private static string? Executable(string value)
    {
        var path = value.Trim();
        var comma = path.LastIndexOf(',');
        if (comma > 0 && int.TryParse(path[(comma + 1)..], out _)) path = path[..comma];
        path = path.Trim().Trim('"');
        return path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && File.Exists(path) ? path : null;
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { }
    }

    public void Dispose() => _http.Dispose();

    // ───────────────────────── WinVerifyTrust ─────────────────────────

    private static readonly Guid GenericVerifyV2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WinTrustFileInfo
    {
        public uint Size;
        public string FilePath;
        public IntPtr File;
        public IntPtr KnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WinTrustData
    {
        public uint Size;
        public IntPtr PolicyCallbackData;
        public IntPtr SipClientData;
        public uint UiChoice;           // WTD_UI_NONE = 2
        public uint RevocationChecks;   // WTD_REVOKE_NONE = 0
        public uint UnionChoice;        // WTD_CHOICE_FILE = 1
        public IntPtr FileInfo;
        public uint StateAction;        // WTD_STATEACTION_IGNORE = 0
        public IntPtr StateData;
        public IntPtr UrlReference;
        public uint ProviderFlags;
        public uint UiContext;
        public IntPtr SignatureSettings;
    }

    [DllImport("wintrust.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern int WinVerifyTrust(IntPtr window, [MarshalAs(UnmanagedType.LPStruct)] Guid action, ref WinTrustData data);

    /// <summary>0 quando a assinatura é válida e confiável para o Windows; senão o código do erro (HRESULT).</summary>
    private static int WinVerifyTrustFile(string path)
    {
        var fileInfo = new WinTrustFileInfo { Size = (uint)Marshal.SizeOf<WinTrustFileInfo>(), FilePath = path };
        var pointer = Marshal.AllocHGlobal(Marshal.SizeOf<WinTrustFileInfo>());
        try
        {
            Marshal.StructureToPtr(fileInfo, pointer, false);
            var data = new WinTrustData
            {
                Size = (uint)Marshal.SizeOf<WinTrustData>(),
                UiChoice = 2,
                RevocationChecks = 0,
                UnionChoice = 1,
                FileInfo = pointer,
                StateAction = 0,
            };
            return WinVerifyTrust(new IntPtr(-1), GenericVerifyV2, ref data);
        }
        finally
        {
            Marshal.DestroyStructure<WinTrustFileInfo>(pointer);
            Marshal.FreeHGlobal(pointer);
        }
    }
}
