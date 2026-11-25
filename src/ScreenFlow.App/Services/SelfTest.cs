using System.Diagnostics;
using System.Net;
using System.Text;
using ScreenFlow.Core;
using ScreenFlow.Core.Diagnostics;
using ScreenFlow.Core.Media;
using ScreenFlow.Core.Room;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using ScreenFlow.Infrastructure.Media;
using ScreenFlow.Infrastructure.Room;
using ScreenFlow.Infrastructure.Session;

namespace ScreenFlow.App.Services;

/// <summary>
/// ScreenFlow.exe --autoteste: abre uma festa de música de verdade no 127.0.0.1 (com um tom de teste no lugar do som do
/// PC), conecta um viewer nela e confere se o áudio chega e é decodificado. Depois abre duas salas da rede no mesmo PC
/// e confere presença, chat e voz entre elas. Por fim, se o PC tiver saída de som, confere a separação do som do PC por
/// app. Usa portas próprias (47900 a 47902, 47910 e 47911).
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
        var soundOk = await PcSoundTestAsync(Line);
        Line(ok && roomOk && soundOk ? "PASSOU: tudo certo." : "FALHOU: veja as linhas acima.");
        return (ok && roomOk && soundOk, report.ToString());
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

    // Tons do teste do som do PC: o do ScreenFlow e os dois que o CI deixa tocando fora dele (veja windows-build.yml).
    private const double OwnHz = 1000, MutedHz = 440, KeptHz = 700;
    private const string MutedExe = "powershell.exe";

    /// <summary>
    /// Som do PC separado por app, no áudio de verdade do Windows (no CI, numa placa de som virtual). O ScreenFlow toca
    /// 1000 Hz; o CI deixa tocando 440 Hz no powershell.exe e 700 Hz no pwsh.exe. Confere que a captura normal ouve tudo,
    /// que a "sem o ScreenFlow" não ouve os 1000 Hz e que a mistura sem o powershell.exe não ouve nem 1000 nem 440 Hz, mas
    /// ouve os 700 Hz. Sem saída de som, o teste é pulado.
    /// </summary>
    private static async Task<bool> PcSoundTestAsync(Action<string> line)
    {
        line("Som do PC separado por app.");
        if (!ProcessLoopbackCapture.Supported)
        {
            line("Pulado: este Windows não separa o som por app (precisa do 10 versão 2004 ou mais novo).");
            return true;
        }
        if (!HasSpeakers())
        {
            line("Pulado: este PC não tem saída de som.");
            return true;
        }

        WasapiOut? tone = null;
        AppAudioMixer? mixer = null;
        var recorders = new List<Recorder>();
        try
        {
            await Task.Run(() =>
            {
                tone = new WasapiOut(AudioClientShareMode.Shared, true, 40);
                tone.Init(new SignalGenerator(48_000, 2) { Frequency = OwnHz, Gain = 0.2, Type = SignalGeneratorType.Sin });
                tone.Play();
            });
            await Task.Delay(400);
            await Task.Run(() =>
            {
                recorders.Add(new Recorder("todo o som", new WasapiLoopbackCapture()));
                recorders.Add(new Recorder("sem o ScreenFlow", ProcessLoopbackCapture.ExcludingProcess(Environment.ProcessId)));
                mixer = AppAudioMixer.Open([MutedExe]);
                recorders.Add(new Recorder($"sem o {MutedExe}", mixer));
                foreach (var recorder in recorders) recorder.Start();
            });
            await Task.Delay(1500); // o misturador acha os apps e enche as filas
            foreach (var recorder in recorders) recorder.Measure();
            await Task.Delay(1200);
            foreach (var recorder in recorders) recorder.Stop();

            foreach (var recorder in recorders)
                line($"{recorder.Name}: {recorder.Seconds:0.0} s, {OwnHz:0} Hz {recorder.Level(OwnHz):0.000}, {MutedHz:0} Hz {recorder.Level(MutedHz):0.000}, " +
                     $"{KeptHz:0} Hz {recorder.Level(KeptHz):0.000}");
            line($"Mistura capturando: {(mixer!.CapturedApps.Count == 0 ? "nenhum app" : string.Join(", ", mixer.CapturedApps))}.");

            var (all, withoutUs, withoutApp) = (recorders[0], recorders[1], recorders[2]);
            var reference = all.Level(OwnHz);
            if (reference < 0.05)
            {
                line($"Pulado: a captura normal não ouviu o tom do ScreenFlow ({reference:0.000}); a saída de som não devolve o que toca.");
                return true;
            }

            var ok = true;
            void Check(bool condition, string failure)
            {
                if (condition) return;
                ok = false;
                line($"FALHOU: {failure}");
            }
            bool Silent(Recorder recorder, double hz) => recorder.Level(hz) < Math.Max(0.005, all.Level(hz) * 0.05);
            bool Heard(Recorder recorder, double hz) => recorder.Seconds > 0.8 && recorder.Level(hz) > all.Level(hz) * 0.3;

            Check(withoutUs.Seconds > 0.8, "a captura sem o ScreenFlow não entregou áudio.");
            Check(withoutApp.Seconds > 0.8, "a mistura sem o app não entregou áudio.");
            Check(Silent(withoutUs, OwnHz), "o som do próprio ScreenFlow apareceu na captura sem o ScreenFlow.");
            Check(Silent(withoutApp, OwnHz), "o som do próprio ScreenFlow apareceu na mistura.");
            var external = 0;
            if (all.Level(MutedHz) > 0.05)
            {
                external++;
                Check(Heard(withoutUs, MutedHz), $"a captura sem o ScreenFlow perdeu o som do {MutedExe}.");
                Check(Silent(withoutApp, MutedHz), $"o som do {MutedExe} apareceu na mistura que deveria tirá-lo.");
            }
            if (all.Level(KeptHz) > 0.05)
            {
                external++;
                Check(Heard(withoutUs, KeptHz), "a captura sem o ScreenFlow perdeu o som de outro app.");
                Check(Heard(withoutApp, KeptHz), "a mistura perdeu o som de um app que não foi tirado.");
            }
            if (external < 2) line("Aviso: os tons de fora (440 e 700 Hz) não estavam tocando todos, então só parte da separação foi conferida.");
            line(ok ? "PASSOU: o som do ScreenFlow e o do app tirado ficam fora; o resto do som do PC vai." : "FALHOU: a separação do som por app não funcionou.");
            return ok;
        }
        catch (Exception exception)
        {
            Log.Error("Autoteste do som do PC falhou", exception);
            line($"FALHOU com erro no som do PC: {exception.GetType().Name}: {exception.Message}");
            return false;
        }
        finally
        {
            foreach (var recorder in recorders) recorder.Dispose();
            mixer?.Dispose();
            tone?.Dispose();
        }
    }

    private static bool HasSpeakers()
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            return enumerator.HasDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        }
        catch (Exception exception)
        {
            Log.Warn($"Não deu para procurar a saída de som: {exception.Message}");
            return false;
        }
    }

    /// <summary>Grava uma captura em mono (a partir de <see cref="Measure"/>) para medir o volume de cada tom.</summary>
    private sealed class Recorder : IDisposable
    {
        private readonly IWaveIn _capture;
        private readonly object _gate = new();
        private readonly List<float> _samples = [];
        private bool _measuring;

        public Recorder(string name, IWaveIn capture)
        {
            Name = name;
            _capture = capture;
            _capture.DataAvailable += OnData;
        }

        public string Name { get; }
        private int SampleRate => _capture.WaveFormat.SampleRate;

        public double Seconds
        {
            get
            {
                lock (_gate) return _samples.Count / (double)SampleRate;
            }
        }

        public void Start() => _capture.StartRecording();

        public void Measure()
        {
            lock (_gate)
            {
                _samples.Clear();
                _measuring = true;
            }
        }

        public void Stop()
        {
            lock (_gate) _measuring = false;
            _capture.StopRecording();
        }

        private void OnData(object? sender, WaveInEventArgs e)
        {
            var format = _capture.WaveFormat;
            var channels = Math.Max(1, format.Channels);
            var isFloat = format.Encoding == WaveFormatEncoding.IeeeFloat || (format is WaveFormatExtensible ext && ext.SubFormat == NAudio.Dmo.AudioMediaSubtypes.MEDIASUBTYPE_IEEE_FLOAT);
            var bytesPerSample = format.BitsPerSample / 8;
            var frames = e.BytesRecorded / (bytesPerSample * channels);
            lock (_gate)
            {
                if (!_measuring) return;
                for (var frame = 0; frame < frames; frame++)
                {
                    var sum = 0.0;
                    for (var channel = 0; channel < channels; channel++)
                    {
                        var offset = (frame * channels + channel) * bytesPerSample;
                        sum += isFloat && bytesPerSample == 4 ? BitConverter.ToSingle(e.Buffer, offset)
                            : bytesPerSample == 2 ? BitConverter.ToInt16(e.Buffer, offset) / 32768.0
                            : bytesPerSample == 4 ? BitConverter.ToInt32(e.Buffer, offset) / 2147483648.0
                            : 0;
                    }
                    _samples.Add((float)(sum / channels));
                }
            }
        }

        /// <summary>Amplitude do tom em <paramref name="hz"/> (Goertzel): 0,2 para um seno tocado com ganho 0,2.</summary>
        public double Level(double hz)
        {
            float[] samples;
            lock (_gate) samples = [.. _samples];
            if (samples.Length == 0) return 0;
            var coefficient = 2 * Math.Cos(2 * Math.PI * hz / SampleRate);
            double previous = 0, beforePrevious = 0;
            foreach (var sample in samples)
            {
                var current = sample + coefficient * previous - beforePrevious;
                beforePrevious = previous;
                previous = current;
            }
            var power = previous * previous + beforePrevious * beforePrevious - coefficient * previous * beforePrevious;
            return 2 * Math.Sqrt(Math.Max(0, power)) / samples.Length;
        }

        public void Dispose()
        {
            _capture.DataAvailable -= OnData;
            try { _capture.Dispose(); } catch { }
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
