using System.Diagnostics;
using System.Net;
using System.Text;
using ScreenFlow.Core;
using ScreenFlow.Core.Diagnostics;
using ScreenFlow.Core.Media;
using ScreenFlow.Core.Room;
using ScreenFlow.Infrastructure.Media;
using ScreenFlow.Infrastructure.Room;
using ScreenFlow.Infrastructure.Session;

namespace ScreenFlow.App.Services;

/// <summary>
/// ScreenFlow.exe --autoteste: abre uma festa de música de verdade no 127.0.0.1 (com um tom de teste no lugar do som do
/// PC), conecta um viewer nela e confere se o áudio chega e é decodificado. Depois abre duas salas da rede no mesmo PC
/// e confere presença, chat e voz entre elas. Usa portas próprias (47900 a 47902, 47910 e 47911).
/// </summary>
public static class SelfTest
{
    private const int MediaPort = 47900, ControlPort = 47901, DiscoveryPort = 47902;
    private const int RoomPortA = 47910, RoomPortB = 47911;

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
            Line("Festa encerrada.");
        }

        var roomOk = await RoomTestAsync(Line);
        Line(ok && roomOk ? "PASSOU: tudo certo." : "FALHOU: veja as linhas acima.");
        return (ok && roomOk, report.ToString());
    }

    /// <summary>Dois PCs de mentira (duas salas em portas diferentes) se acham, trocam uma mensagem e conversam por voz.</summary>
    private static async Task<bool> RoomTestAsync(Action<string> line)
    {
        line("Sala da rede: dois PCs de mentira no 127.0.0.1.");
        var a = new NetworkRoom(Guid.NewGuid(), RoomPortA, new RoomLocalState("Autoteste A")) { BroadcastTargets = () => [], OutputEnabled = false };
        var b = new NetworkRoom(Guid.NewGuid(), RoomPortB, new RoomLocalState("Autoteste B")) { BroadcastTargets = () => [], OutputEnabled = false };
        try
        {
            a.Start();
            b.Start();
            if (!a.IsRunning || !b.IsRunning)
            {
                line($"FALHOU: a sala não abriu ({a.Error ?? b.Error}).");
                return false;
            }
            a.AddManualPeer(IPAddress.Loopback, RoomPortB);
            b.AddManualPeer(IPAddress.Loopback, RoomPortA);
            if (!await WaitAsync(() => a.Peers.Count == 1 && b.Peers.Count == 1, 8))
            {
                line("FALHOU: as duas salas não se encontraram em 8 segundos.");
                return false;
            }
            line($"Presença: A vê \"{a.Peers[0].Presence.Name}\" e B vê \"{b.Peers[0].Presence.Name}\".");

            var received = new TaskCompletionSource<RoomChatMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
            b.MessageReceived += message =>
            {
                if (message.AuthorId == a.SelfId) received.TrySetResult(message);
            };
            const string text = "Olá do autoteste! Acentos e emoji: ação, pão, 🎮";
            a.SendChat(text);
            var chatOk = await Task.WhenAny(received.Task, Task.Delay(5000)) == received.Task && received.Task.Result.Text == text;
            line(chatOk ? $"Chat: B recebeu \"{received.Task.Result.Text}\" de {received.Task.Result.AuthorName}." : "FALHOU: a mensagem do chat não chegou.");

            a.JoinVoice(new ToneVoiceInput());
            b.JoinVoice(new ToneVoiceInput());
            var voiceOk = await WaitAsync(() => a.Voice?.FramesDecoded > 50 && b.Voice?.FramesDecoded > 50, 10);
            line($"Voz: A mandou {a.Voice?.FramesSent ?? 0} quadros e tocou {a.Voice?.FramesDecoded ?? 0}; " +
                 $"B mandou {b.Voice?.FramesSent ?? 0} e tocou {b.Voice?.FramesDecoded ?? 0} (recuperados pela cópia: {(a.Voice?.FramesRecovered ?? 0) + (b.Voice?.FramesRecovered ?? 0)}).");
            var peerInVoice = a.Peers.FirstOrDefault()?.Presence.InVoice == true && b.Peers.FirstOrDefault()?.Presence.InVoice == true;
            if (!peerInVoice) line("FALHOU: um não viu o outro entrar na voz.");
            if (!voiceOk) line("FALHOU: a voz não chegou dos dois lados.");

            await a.LeaveVoiceAsync();
            var leftOk = await WaitAsync(() => b.Peers.FirstOrDefault()?.Presence.InVoice == false, 4);
            if (!leftOk) line("FALHOU: B não viu A sair da voz.");

            var ok = chatOk && voiceOk && peerInVoice && leftOk;
            line(ok ? "PASSOU: presença, chat e voz funcionam entre dois PCs." : "FALHOU: a sala da rede não funcionou como deveria.");
            return ok;
        }
        catch (Exception exception)
        {
            Log.Error("Autoteste da sala falhou", exception);
            line($"FALHOU com erro na sala: {exception.GetType().Name}: {exception.Message}");
            return false;
        }
        finally
        {
            await a.DisposeAsync();
            await b.DisposeAsync();
            line("Salas fechadas.");
        }
    }

    private static async Task<bool> WaitAsync(Func<bool> condition, int seconds)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return true;
            await Task.Delay(100);
        }
        return condition();
    }
}
