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

        AppServices.Initialize();

        // Comando de diagnóstico: ScreenFlow.exe --diagnostico
        if (e.Args.Any(a => a is "--diagnostico" or "--diagnostics" or "/diag"))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            var report = await AppServices.BuildDiagnosticsAsync();
            var file = System.IO.Path.Combine(Log.Directory, $"diagnostico-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
            System.IO.Directory.CreateDirectory(Log.Directory);
            await System.IO.File.WriteAllTextAsync(file, report);
            if (AttachConsole(-1))
            {
                Console.WriteLine();
                Console.WriteLine(report);
                Console.WriteLine($"Relatório salvo em {file}");
            }
            Log.Flush();
            Shutdown(0);
            return;
        }

        ThemeManager.Apply(AppServices.Settings.Current.Theme);
        var window = new MainWindow();
        MainWindow = window;
        window.Show();
    }

    private void OnDispatcherException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error("Erro na interface", e.Exception);
        MessageBox.Show($"Algo deu errado: {e.Exception.Message}\n\nOs detalhes foram salvos no log.", AppInfo.Name, MessageBoxButton.OK, MessageBoxImage.Warning);
        e.Handled = true;
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
