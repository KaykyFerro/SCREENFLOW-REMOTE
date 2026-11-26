using ScreenFlow.Core;
using ScreenFlow.Core.Diagnostics;
using ScreenFlow.Core.Updates;
using ScreenFlow.Infrastructure.Updates;

namespace ScreenFlow.App.Services;

/// <summary>
/// Atualização pelo próprio app: procura ao abrir e a cada 6 horas, avisa pelo sininho e instala com um clique.
/// Todos os eventos chegam na thread da interface.
/// </summary>
public sealed class UpdateService
{
    public static readonly TimeSpan Interval = TimeSpan.FromHours(6);
    private Task<ReleaseInfo?>? _checking;

    public ReleaseInfo? Available { get; private set; }

    /// <summary>
    /// Procurando agora. É um campo próprio (e não "a tarefa ainda não terminou") porque o aviso de fim sai de dentro da
    /// tarefa: lendo a tarefa, o botão Procurar agora ficava travado em "Procurando…" quando não havia versão nova.
    /// </summary>
    public bool Checking { get; private set; }
    public bool Installing { get; private set; }
    public double Progress { get; private set; }
    public string? Error { get; private set; }
    public DateTimeOffset? LastCheck => AppServices.Settings.Current.LastUpdateCheck;

    /// <summary>Mudou algo (verificação, download, erro).</summary>
    public event Action? Changed;

    /// <summary>Encontrou uma versão nova que o usuário ainda não pulou.</summary>
    public event Action<ReleaseInfo>? Found;

    public Task<ReleaseInfo?> CheckAsync(bool manual) => Checking && _checking is not null ? _checking : _checking = CheckCoreAsync(manual);

    private async Task<ReleaseInfo?> CheckCoreAsync(bool manual)
    {
        Checking = true;
        Error = null;
        Changed?.Invoke();
        try
        {
            using var updater = new Updater();
            var release = await updater.CheckAsync();
            var settings = AppServices.Settings;
            settings.Current.LastUpdateCheck = DateTimeOffset.Now;
            settings.Save();
            Available = release;
            if (release is not null && (manual || settings.Current.SkippedVersion != release.Tag))
                Found?.Invoke(release);
            return release;
        }
        catch (Exception exception)
        {
            Log.Warn($"Verificação de atualização falhou: {exception.Message}");
            Error = $"Não foi possível verificar: {exception.Message}";
            return null;
        }
        finally
        {
            Checking = false;
            Changed?.Invoke();
        }
    }

    public void Skip(ReleaseInfo release)
    {
        AppServices.Settings.Current.SkippedVersion = release.Tag;
        AppServices.Settings.Save();
    }

    /// <summary>Baixa, confere o SHA-256 e roda o instalador silencioso (ele fecha e reabre o app).</summary>
    public async Task<bool> InstallAsync(ReleaseInfo release, Func<Task> beforeInstall)
    {
        if (Installing) return false;
        Installing = true;
        Error = null;
        Progress = 0;
        Changed?.Invoke();
        try
        {
            using var updater = new Updater();
            var progress = new Progress<double>(p =>
            {
                Progress = p;
                Changed?.Invoke();
            });
            var path = await updater.DownloadAsync(release, progress);
            Progress = 1;
            Changed?.Invoke();
            await beforeInstall();
            Updater.LaunchInstaller(path);
            return true;
        }
        catch (Exception exception)
        {
            Log.Error("Atualização falhou", exception);
            Error = $"A atualização falhou: {exception.Message}";
            Installing = false;
            Changed?.Invoke();
            return false;
        }
    }

    /// <summary>Capturas de tela do CI: mostra uma versão nova de exemplo sem acessar a rede.</summary>
    public void ShowDemo(ReleaseInfo release)
    {
        Available = release;
        AppServices.Settings.Current.LastUpdateCheck = DateTimeOffset.Now;
        Changed?.Invoke();
        Found?.Invoke(release);
    }

    public static string ReleasesPage => Updater.ReleasesPage;
    public static string CurrentVersion => AppInfo.DisplayVersion;
}
