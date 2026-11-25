using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ScreenFlow.App.Pages;
using ScreenFlow.App.Windows;
using ScreenFlow.Core;
using ScreenFlow.Core.Diagnostics;
using ScreenFlow.Core.Media;
using ScreenFlow.Core.Settings;
using ScreenFlow.Infrastructure.Session;

namespace ScreenFlow.App.Services;

/// <summary>
/// ScreenFlow.exe --capturas pasta: abre cada tela com dados de exemplo (sem rede, GPU ou Radmin) e salva um PNG de
/// cada uma. Roda no CI para conferir o visual a cada mudança.
/// </summary>
public static class Screenshots
{
    private const double Scale = 2;

    public static async Task<bool> RunAsync(string folder)
    {
        Directory.CreateDirectory(folder);
        var saved = 0;
        var failed = 0;

        var window = new MainWindow
        {
            Width = 1440,
            Height = 900,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -30000,
            Top = -30000,
            ShowActivated = false,
            ShowInTaskbar = false,
        };
        window.Show();
        await SettleAsync();
        AppServices.Updates.ShowDemo(UpdatesPage.DemoRelease);
        window.Toasts.Children.Clear();

        async Task Shot(string name, Func<Task> prepare, Func<FrameworkElement>? target = null)
        {
            try
            {
                await prepare();
                await SettleAsync();
                window.Toasts.Children.Clear();
                Save(target?.Invoke() ?? window, Path.Combine(folder, name));
                saved++;
            }
            catch (Exception exception)
            {
                failed++;
                Log.Error($"Captura {name} falhou", exception);
            }
        }

        await Shot("01-visao-geral.png", () => Go(window, window.OverviewPage));
        await Shot("02-transmissao-ao-vivo.png", () =>
        {
            window.Navigate(window.TransmitPage);
            window.TransmitPage.ShowDemo();
            return Task.Delay(600);
        });
        await Shot("03-assistir.png", () => Go(window, window.WatchPage));
        await Shot("04-monitoramento.png", () =>
        {
            DemoData.FillTelemetry();
            return Go(window, window.MonitorPage);
        });
        await Shot("05-configuracoes.png", () => Go(window, window.SettingsPage, 800));
        await Shot("06-atualizacoes.png", () => Go(window, window.UpdatesPage));
        await Shot("07-sininho.png", () =>
        {
            window.NotificationsPopup.IsOpen = true;
            return Task.Delay(300);
        }, () => (FrameworkElement)window.NotificationsPopup.Child);
        window.NotificationsPopup.IsOpen = false;
        await Shot("08-visao-geral-tema-claro.png", async () =>
        {
            ThemeManager.Apply(AppTheme.Light);
            window.Navigate(window.WatchPage);
            await Go(window, window.OverviewPage);
        });
        ThemeManager.Apply(AppTheme.Dark);

        foreach (var (name, mode, state) in new[]
                 {
                     ("09-player-festa-de-musica.png", StreamMode.AudioOnly, ConnectionState.Connected),
                     ("10-player-conectando.png", StreamMode.Screen, ConnectionState.WaitingApproval),
                 })
        {
            PlayerWindow? player = null;
            await Shot(name, () =>
            {
                var session = new ViewerSession(new ViewerOptions("26.47.19.113", AppInfo.DefaultControlPort, "Capturas", null, 0.8f, [], 2560, 1440, 165, AudioOutput: false));
                player = new PlayerWindow(session, "26.47.19.113", mode)
                {
                    Width = 1280,
                    Height = 720,
                    WindowStartupLocation = WindowStartupLocation.Manual,
                    Left = -30000,
                    Top = -30000,
                    ShowActivated = false,
                    ShowInTaskbar = false,
                };
                player.Show();
                player.ShowDemo(mode, state);
                return Task.Delay(700);
            }, () => player!);
            if (player is not null) await player.ShutdownAsync();
        }

        Telemetry.EndHost();
        await window.StopAllAsync(); // a janela fecha junto com o app
        Log.Info($"Capturas: {saved} salvas em {folder}, {failed} com erro.");
        return failed == 0 && saved > 0;
    }

    private static async Task Go(MainWindow window, UserControl page, int wait = 300)
    {
        window.Navigate(page);
        await Task.Delay(wait);
    }

    /// <summary>Espera o layout, as imagens e alguns quadros de animação.</summary>
    private static async Task SettleAsync()
    {
        await Application.Current.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        await Task.Delay(150);
        await Application.Current.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
    }

    private static void Save(FrameworkElement element, string path)
    {
        element.UpdateLayout();
        var width = Math.Max(1, (int)Math.Ceiling(element.ActualWidth * Scale));
        var height = Math.Max(1, (int)Math.Ceiling(element.ActualHeight * Scale));
        var bitmap = new RenderTargetBitmap(width, height, 96 * Scale, 96 * Scale, PixelFormats.Pbgra32);
        bitmap.Render(element);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(path);
        encoder.Save(file);
        Log.Info($"Captura salva: {path} ({width}x{height})");
    }
}
