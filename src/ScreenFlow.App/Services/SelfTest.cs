using System.Diagnostics;
using System.Net;
using System.Text;
using ScreenFlow.Core;
using ScreenFlow.Core.Diagnostics;
using ScreenFlow.Core.Media;
using ScreenFlow.Infrastructure.Media;
using ScreenFlow.Infrastructure.Session;

namespace ScreenFlow.App.Services;

/// <summary>
/// ScreenFlow.exe --autoteste: abre uma festa de música de verdade no 127.0.0.1 (com um tom de teste no lugar do som do
/// PC), conecta um viewer nela e confere se o áudio chega e é decodificado. Usa portas próprias (47900 a 47902).
/// </summary>
public static class SelfTest
{
    private const int MediaPort = 47900, ControlPort = 47901, DiscoveryPort = 47902;

    public static async Task<(bool Ok, string Report)> RunAsync()
    {
        var report = new StringBuilder();
        var clock = Stopwatch.StartNew();
        void Line(string text) => report.AppendLine($"[{clock.Elapsed.TotalSeconds,5:0.0}s] {text}");
        Line($"{AppInfo.Name} {AppInfo.DisplayVersion}: autoteste da festa de música no 127.0.0.1");

        var hostOptions = new HostOptions(
            "Autoteste", IPAddress.Loopback, IPAddress.Loopback, MediaPort, ControlPort, DiscoveryPort,
            null, false, 4,
            new CaptureTarget(CaptureKind.Monitor, string.Empty, "Festa de música", 0, 0, 0), null, Resolution.P720, 30, 0,
            null, null, [],
            true, 160, false, 10, 0,
            StreamMode.AudioOnly, 256, profile => new ToneAudioSource(profile));

        var ok = false;
        HostSession? host = null;
        ViewerSession? viewer = null;
        try
        {
            host = new HostSession(hostOptions);
            host.StatusChanged += message => Line($"host: {message}");
            await Task.Run(host.Start);
            Line("Host no ar.");

            viewer = new ViewerSession(new ViewerOptions("127.0.0.1", ControlPort, "Autoteste viewer", null, 0.8f, [], 1280, 720, 60, AudioOutput: false));
            var connected = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            ViewerStatistics? last = null;
            viewer.StateChanged += (state, message) =>
            {
                Line($"viewer: {state} · {message}");
                if (state == ConnectionState.Connected) connected.TrySetResult(true);
                if (state is ConnectionState.Error or ConnectionState.Closed) connected.TrySetResult(false);
            };
            viewer.StatisticsUpdated += stats => last = stats;
            viewer.Start();

            if (!await connected.Task.WaitAsync(TimeSpan.FromSeconds(15)).ContinueWith(t => t.IsCompletedSuccessfully && t.Result))
            {
                Line("FALHOU: o viewer não conectou em 15 segundos.");
                return (false, report.ToString());
            }

            // Espera o áudio chegar e ser decodificado (o medidor do viewer só anda com áudio decodificado).
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (DateTime.UtcNow < deadline)
            {
                await Task.Delay(250);
                if (viewer.AudioMeter is { Updates: > 20, Level: > 0.01f } && last is { AudioKbps: > 0 }) break;
            }
            var meter = viewer.AudioMeter;
            Line($"Viewer: modo {viewer.Config?.Mode}, {last?.AudioKbps ?? 0:0} kbps recebidos, buffer {last?.AudioBufferMs ?? 0:0} ms, " +
                 $"medidor {meter?.Updates ?? 0} blocos, nível {meter?.Level ?? 0:0.00}, perda {last?.LossRate ?? 0:P1}.");
            var viewers = host.Viewers.Count(v => v.State == ViewerState.Streaming);
            Line($"Host: {viewers} pessoa(s) ouvindo, {host.AudioDescription}.");

            ok = viewer.Config?.Mode == StreamMode.AudioOnly && meter is { Updates: > 20, Level: > 0.01f } && last is { AudioKbps: > 0 } && viewers == 1;
            Line(ok ? "PASSOU: o som saiu do host, atravessou a rede local e foi decodificado no viewer." : "FALHOU: o áudio não chegou ao viewer.");
        }
        catch (Exception exception)
        {
            Log.Error("Autoteste falhou", exception);
            Line($"FALHOU com erro: {exception.GetType().Name}: {exception.Message}");
        }
        finally
        {
            if (viewer is not null) await viewer.DisposeAsync();
            if (host is not null) await host.DisposeAsync();
            Line("Encerrado.");
        }
        return (ok, report.ToString());
    }
}
