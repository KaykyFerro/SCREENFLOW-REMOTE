using System.IO;
using System.Windows.Threading;
using ScreenFlow.Core.Diagnostics;
using ScreenFlow.Core.Network;
using ScreenFlow.Infrastructure.Network;

namespace ScreenFlow.App.Services;

/// <summary>
/// O Tailscale pelo ScreenFlow (1.4.1): o estado dele neste PC, o botão "Instalar o Tailscale" (MSI oficial, SHA-256 e
/// assinatura da Tailscale conferidos, o Windows pede permissão) e o "Ligar o Tailscale" (abre o login no navegador
/// quando falta entrar na conta). Nunca usa chave de autenticação nem guarda senha: cada pessoa entra na própria conta.
/// As telas acompanham por <see cref="Changed"/>, na thread da interface.
/// </summary>
public sealed class TailscaleSetup
{
    private static readonly TimeSpan LoginWait = TimeSpan.FromMinutes(10);
    private readonly Dispatcher _dispatcher;
    private CancellationTokenSource? _cancel;
    private Task<TailscaleStatus?>? _refreshing;

    public TailscaleSetup(Dispatcher dispatcher) => _dispatcher = dispatcher;

    public bool Busy { get; private set; }

    /// <summary>O que está acontecendo agora ("Baixando… 42%"), ou o último resultado. Null antes de começar.</summary>
    public string? Status { get; private set; }

    /// <summary>De 0 a 1 enquanto baixa; null nas outras etapas.</summary>
    public double? Progress { get; private set; }

    /// <summary>O último estado lido do Tailscale (null: não instalado, não respondeu ou ainda não leu).</summary>
    public TailscaleStatus? Current { get; private set; }

    public event Action? Changed;

    /// <summary>O Tailscale está instalado neste PC (mesmo desligado)?</summary>
    public static bool IsInstalled() => AppServices.DemoMode || TailscaleCli.Find() is not null;

    /// <summary>Uma frase sobre o Tailscale deste PC, para os cartões.</summary>
    public string Describe()
    {
        if (AppServices.DemoMode) return "Ligado: 100.101.102.103 (PC-DO-KAYKY) · 4 PCs da rede ligados.";
        if (Busy && Status is not null) return Status;
        if (!IsInstalled()) return "O Tailscale não está instalado neste PC. Ele junta os PCs numa rede só, até pela internet, sem abrir portas no roteador.";
        if (Current is not { } status)
            return AppServices.FindTailscale() is { } adapter
                ? $"Ligado: {adapter.Address}."
                : "O Tailscale está instalado, mas não respondeu. Abra o Tailscale pelo ícone perto do relógio.";
        if (status.Running)
        {
            var online = status.Peers.Count(p => p.Online);
            return $"Ligado: {status.Self}" + (status.SelfName is { Length: > 0 } name ? $" ({name})" : string.Empty)
                   + online switch { 0 => " · nenhum outro PC da rede ligado agora.", 1 => " · 1 PC da rede ligado.", var n => $" · {n} PCs da rede ligados." };
        }
        return status.NeedsLogin ? "O Tailscale está instalado, mas falta entrar na conta (a mesma conta ou rede dos outros PCs)."
            : status.BackendState == "Stopped" ? "O Tailscale está instalado, mas desligado."
            : "O Tailscale está ligando…";
    }

    /// <summary>Lê o estado de novo (sem travar a interface). Quem pedir enquanto lê espera a mesma leitura.</summary>
    public Task<TailscaleStatus?> RefreshAsync()
    {
        if (AppServices.DemoMode) return Task.FromResult<TailscaleStatus?>(null);
        if (_refreshing is { IsCompleted: false } running) return running;
        return _refreshing = ReadAsync();
    }

    private async Task<TailscaleStatus?> ReadAsync()
    {
        TailscaleStatus? status = null;
        try { status = await Task.Run(() => TailscaleCli.StatusAsync()); }
        catch (Exception exception) { Log.Debug($"Estado do Tailscale: {exception.Message}"); }
        Current = status;
        Changed?.Invoke();
        return status;
    }

    public void Cancel() => _cancel?.Cancel();

    /// <summary>Abre o Tailscale (o ícone perto do relógio). false se não achou.</summary>
    public static bool OpenApp() => !AppServices.DemoMode && TailscaleCli.OpenApp();

    /// <summary>
    /// Baixa e instala o Tailscale e liga (com o login no navegador). <paramref name="unattended"/> no PC servidor: fica
    /// ligado mesmo sem ninguém logado no Windows.
    /// </summary>
    public async Task<bool> InstallAsync(MainWindow shell, bool unattended)
    {
        if (Busy || AppServices.DemoMode) return false;
        Busy = true;
        _cancel = new CancellationTokenSource();
        var token = _cancel.Token;
        string? installer = null;
        var installed = false;
        try
        {
            Set("Procurando a versão mais nova do Tailscale…");
            using var tailscale = new TailscaleInstaller();
            var url = await tailscale.FindInstallerAsync(token);
            var version = TailscaleDownload.VersionOf(url);
            var progress = new Progress<double>(p => Set($"Baixando o Tailscale {version}… {p:P0}", p));
            installer = await tailscale.DownloadAsync(url, progress, token);

            Set("Conferindo a assinatura digital da Tailscale…");
            var path = installer;
            await Task.Run(() => TailscaleInstaller.VerifySignature(path), token);

            Set("Instalando o Tailscale… Aceite o pedido de permissão do Windows.");
            var code = await TailscaleInstaller.InstallAsync(installer, unattended, token);
            if (TailscaleDownload.ExplainExitCode(code) is { } problem) throw new InvalidOperationException(problem);
            installed = true;
            Log.Info("Tailscale instalado pelo ScreenFlow.");
        }
        catch (OperationCanceledException)
        {
            Set("Instalação do Tailscale cancelada.");
            return false;
        }
        catch (Exception exception)
        {
            Log.Warn($"Instalar o Tailscale: {exception}");
            var message = exception is InvalidDataException or InvalidOperationException
                ? exception.Message
                : "Não deu para baixar o Tailscale. Confira a internet e tente de novo, ou baixe pelo site.";
            Set(message);
            shell.Toast(message, ToastKind.Warning, "Abrir o site", () => MainWindow.OpenUrl(TailscaleDownload.HomePage));
            return false;
        }
        finally
        {
            if (installer is not null)
                try { File.Delete(installer); } catch { }
            if (!installed) Finish();
        }
        // O serviço do Tailscale leva uns segundos para começar depois de instalar.
        try { await Task.Delay(TimeSpan.FromSeconds(3), token); }
        catch (OperationCanceledException) { }
        return await ConnectCoreAsync(shell, unattended, token);
    }

    /// <summary>Liga o Tailscale; se faltar entrar na conta, abre o login no navegador e espera a pessoa entrar.</summary>
    public async Task<bool> ConnectAsync(MainWindow shell, bool unattended)
    {
        if (Busy || AppServices.DemoMode) return false;
        Busy = true;
        _cancel = new CancellationTokenSource();
        return await ConnectCoreAsync(shell, unattended, _cancel.Token);
    }

    private async Task<bool> ConnectCoreAsync(MainWindow shell, bool unattended, CancellationToken token)
    {
        try
        {
            Set("Ligando o Tailscale…");
            var problem = await Task.Run(() => TailscaleCli.UpAsync(unattended, url => _dispatcher.BeginInvoke(() => OnLoginUrl(url)), LoginWait, token), token);
            if (problem is not null)
            {
                Set(problem);
                shell.Toast(problem, ToastKind.Warning, "Abrir o Tailscale", () => OpenApp());
                return false;
            }
            await ReadAsync();
            Set(Current?.Self is { } self
                ? $"Tailscale ligado: {self}. Os outros PCs da mesma conta (ou rede) já alcançam este."
                : "Tailscale ligado.");
            shell.Toast(Status!, ToastKind.Success);
            shell.RefreshNetworkStatus();
            return true;
        }
        catch (OperationCanceledException)
        {
            Set("Cancelado.");
            return false;
        }
        catch (Exception exception)
        {
            Log.Warn($"Ligar o Tailscale: {exception}");
            Set("O Tailscale não ligou. Ligue pelo ícone do Tailscale, perto do relógio.");
            return false;
        }
        finally
        {
            Finish();
        }
    }

    /// <summary>O Tailscale pediu login: abre o link no navegador (só se for um link do próprio Tailscale).</summary>
    private void OnLoginUrl(string url)
    {
        if (!TailscaleLogin.IsLoginUrl(url)) return;
        Set("Entre na sua conta do Tailscale no navegador que abriu (a mesma conta, ou rede, dos outros PCs). O ScreenFlow espera você terminar…");
        MainWindow.OpenUrl(url);
    }

    private void Finish()
    {
        Busy = false;
        _cancel?.Dispose();
        _cancel = null;
        Progress = null;
        Changed?.Invoke();
    }

    private void Set(string status, double? progress = null)
    {
        Status = status;
        Progress = progress;
        Changed?.Invoke();
    }
}
