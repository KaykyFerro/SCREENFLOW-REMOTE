using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using ScreenFlow.App.Services;
using ScreenFlow.Core;
using ScreenFlow.Core.Diagnostics;

namespace ScreenFlow.App;

public partial class App : Application
{
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        Log.Info($"{AppInfo.Name} {AppInfo.DisplayVersion} iniciando ({Environment.OSVersion.VersionString})");
        DispatcherUnhandledException += OnDispatcherException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) => Log.Error("Exceção não tratada", args.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, args) => { Log.Error("Task não observada", args.Exception); args.SetObserved(); };

        // Capturas de tela do CI: ScreenFlow.exe --capturas <pasta>. Usa dados de exemplo e configurações descartáveis.
        var captures = ArgumentValue(e.Args, "--capturas");
        if (captures is not null)
        {
            AppServices.DemoMode = true;
            AppServices.Initialize(Path.Combine(Path.GetTempPath(), $"screenflow-capturas-{Guid.NewGuid():N}.json"));
        }
        else AppServices.Initialize();
        ThemeManager.RegisterFonts();

        // Diagnóstico: ScreenFlow.exe --diagnostico
        if (e.Args.Any(a => a is "--diagnostico" or "--diagnostics" or "/diag"))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            var report = await AppServices.BuildDiagnosticsAsync();
            Finish(report, $"diagnostico-{DateTime.Now:yyyyMMdd-HHmmss}.txt", 0);
            return;
        }

        // Autoteste: ScreenFlow.exe --autoteste (festa de música no 127.0.0.1 com tom de teste)
        if (e.Args.Any(a => a is "--autoteste" or "--selftest"))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            var (ok, report) = await SelfTest.RunAsync();
            Finish(report, $"autoteste-{DateTime.Now:yyyyMMdd-HHmmss}.txt", ok ? 0 : 1);
            return;
        }

        ThemeManager.Apply(AppServices.Settings.Current.Theme);

        if (captures is not null)
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            var ok = false;
            try { ok = await Screenshots.RunAsync(Path.GetFullPath(captures)); }
            catch (Exception exception) { Log.Error("Capturas falharam", exception); }
            Log.Flush();
            Shutdown(ok ? 0 : 1);
            return;
        }

        var window = new MainWindow();
        MainWindow = window;
        window.Show();
    }

    private static string? ArgumentValue(string[] args, string name)
    {
        var index = Array.FindIndex(args, a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));
        if (index < 0) return null;
        return index + 1 < args.Length && !args[index + 1].StartsWith("--") ? args[index + 1] : "capturas";
    }

    /// <summary>Salva o relatório na pasta de logs, mostra no console (quando aberto por um terminal) e fecha.</summary>
    private void Finish(string report, string fileName, int exitCode)
    {
        var file = Path.Combine(Log.Directory, fileName);
        Directory.CreateDirectory(Log.Directory);
        File.WriteAllText(file, report);
        if (AttachConsole(-1))
        {
            Console.WriteLine();
            Console.WriteLine(report);
            Console.WriteLine($"Relatório salvo em {file}");
        }
        Log.Flush();
        Shutdown(exitCode);
    }

    private void OnDispatcherException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error("Erro na interface", e.Exception);
        e.Handled = true;
        if (AppServices.DemoMode) return;
        MessageBox.Show($"Algo deu errado: {e.Exception.Message}\n\nOs detalhes foram salvos no log.", AppInfo.Name, MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Log.Info("Encerrando.");
        Log.Flush();
        base.OnExit(e);
    }

    [DllImport("kernel32.dll")]
    private static extern bool AttachConsole(int processId);
}
