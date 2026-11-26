using System.IO;
using ScreenFlow.Core.Diagnostics;
using ScreenFlow.Core.Network;
using ScreenFlow.Infrastructure.Network;

namespace ScreenFlow.App.Services;

/// <summary>
/// O botão "Instalar o Radmin VPN": baixa do site oficial, confere a assinatura da Famatech, instala sem janelas (o
/// Windows pede permissão) e abre o Radmin para a pessoa entrar numa rede. As telas mostram o andamento por
/// <see cref="Changed"/>. Tudo na thread da interface.
/// </summary>
public sealed class RadminSetup
{
    private CancellationTokenSource? _cancel;

    public bool Busy { get; private set; }

    /// <summary>O que está acontecendo agora ("Baixando… 42%"), ou o último resultado. Null antes de começar.</summary>
    public string? Status { get; private set; }

    /// <summary>De 0 a 1 enquanto baixa; null nas outras etapas.</summary>
    public double? Progress { get; private set; }

    public event Action? Changed;

    /// <summary>O Radmin VPN está instalado (mesmo sem rede ativa)?</summary>
    public static bool IsInstalled() => AppServices.DemoMode || RadminInstaller.FindApp() is not null || RadminNetwork.IsRadminInstalled();

    /// <summary>Abre a janela do Radmin VPN (para entrar numa rede). false se não achou.</summary>
    public static bool OpenApp() => !AppServices.DemoMode && RadminInstaller.OpenApp();

    public void Cancel() => _cancel?.Cancel();

    public async Task<bool> InstallAsync(MainWindow shell)
    {
        if (Busy) return false;
        Busy = true;
        _cancel = new CancellationTokenSource();
        var token = _cancel.Token;
        string? installer = null;
        try
        {
            Set("Procurando a versão mais nova do Radmin VPN…");
            using var radmin = new RadminInstaller();
            var url = await radmin.FindInstallerAsync(token);
            var version = RadminDownload.VersionOf(url);
            var progress = new Progress<double>(p => Set($"Baixando o Radmin VPN {version}… {p:P0}", p));
            installer = await radmin.DownloadAsync(url, progress, token);

            Set("Conferindo a assinatura digital da Famatech…");
            var path = installer;
            await Task.Run(() => RadminInstaller.VerifySignature(path), token);

            Set("Instalando o Radmin VPN… Aceite o pedido de permissão do Windows.");
            var code = await RadminInstaller.InstallAsync(installer, token);
            if (RadminDownload.ExplainExitCode(code) is { } problem) throw new InvalidOperationException(problem);

            var opened = OpenApp();
            Set(opened
                ? "Radmin VPN instalado. Na janela dele, entre na mesma rede dos seus amigos (nome e senha da rede)."
                : "Radmin VPN instalado. Abra o Radmin VPN pelo menu Iniciar e entre na mesma rede dos seus amigos.");
            shell.Toast(Status!, ToastKind.Success);
            shell.RefreshNetworkStatus();
            return true;
        }
        catch (OperationCanceledException)
        {
            Set("Instalação do Radmin VPN cancelada.");
            return false;
        }
        catch (Exception exception)
        {
            Log.Warn($"Instalar o Radmin VPN: {exception}");
            var message = exception is InvalidDataException or InvalidOperationException
                ? exception.Message
                : "Não deu para baixar o Radmin VPN. Confira a internet e tente de novo, ou baixe pelo site.";
            Set(message);
            shell.Toast(message, ToastKind.Warning, "Abrir o site", () => MainWindow.OpenUrl(RadminDownload.HomePage));
            return false;
        }
        finally
        {
            if (installer is not null)
                try { File.Delete(installer); } catch { }
            Busy = false;
            _cancel.Dispose();
            _cancel = null;
            Progress = null;
            Changed?.Invoke();
        }
    }

    private void Set(string status, double? progress = null)
    {
        Status = status;
        Progress = progress;
        Changed?.Invoke();
    }
}
