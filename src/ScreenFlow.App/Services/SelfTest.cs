using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using ScreenFlow.Core;
using ScreenFlow.Core.Diagnostics;
using ScreenFlow.Core.Media;
using ScreenFlow.Core.Protocol;
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
    private const int RoomPortA = 47910, RoomPortB = 47911, RoomPortC = 47912;

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
            ok &= await RemoteControlTestAsync(host, viewer, Line);
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

    /// <summary>
    /// Controles de quem assiste (1.2.0): o host publica o que deixa fazer, a música e os apps do som; o viewer recebe,
    /// pede para pular e para tirar um app, e o host recebe só os pedidos que valem (um ponto fora da música é recusado).
    /// </summary>
    private static async Task<bool> RemoteControlTestAsync(HostSession host, ViewerSession viewer, Action<string> line)
    {
        var received = new System.Collections.Concurrent.ConcurrentQueue<ControlMessage>();
        host.RemoteRequested += request => received.Enqueue(request.Message);
        host.PublishRemoteState(new RemoteStateMessage(false, true, true, [], [], [], string.Empty, 0, true));
        host.PublishMedia(new MediaStateMessage("Autoteste", "Tom de teste", "ScreenFlow", true, 10, 120, true, true, true, true));
        host.PublishAudioApps(new AudioAppsMessage([new RemoteAudioApp("spotify.exe", "Spotify", false, true)]));

        var arrived = await WaitAsync(() => viewer.RemoteAvailable && viewer.RemoteState.CanControlMedia && viewer.Media.Title == "Tom de teste"
                                            && viewer.AudioApps.Apps.Length == 1, 5);
        line($"Viewer: controles {(viewer.RemoteAvailable ? "disponíveis" : "indisponíveis")}, tocando \"{viewer.Media.Title}\" em {viewer.MediaPosition:0} s, " +
             $"{viewer.AudioApps.Apps.Length} app(s) na lista do som.");
        if (!arrived)
        {
            line("FALHOU: o estado dos controles não chegou ao viewer.");
            return false;
        }

        var sent = await viewer.SendMediaCommandAsync(MediaCommandMessage.Next)
                   & await viewer.SendMediaCommandAsync(MediaCommandMessage.Seek, 999)
                   & await viewer.RequestAudioAppsAsync(["spotify.exe"]);
        // A qualidade não é oferecida na festa de música: o viewer nem manda.
        var qualityBlocked = !await viewer.RequestQualityAsync("1080p", 60, true);
        var got = await WaitAsync(() => received.Count >= 2, 5);
        await Task.Delay(300);
        var messages = received.ToArray();
        var next = messages.OfType<MediaCommandMessage>().Count(m => m.Command == MediaCommandMessage.Next);
        var seeks = messages.OfType<MediaCommandMessage>().Count(m => m.Command == MediaCommandMessage.Seek);
        var apps = messages.OfType<AudioAppsRequestMessage>().FirstOrDefault();
        line($"Host: {messages.Length} pedido(s) aceito(s): pular {next}, ponto fora da música {seeks}, apps {(apps is null ? "nenhum" : string.Join(", ", apps.Excluded))}.");
        var ok = sent && qualityBlocked && got && next == 1 && seeks == 0 && apps?.Excluded.SequenceEqual(["spotify.exe"]) == true && messages.Length == 2;
        line(ok ? "PASSOU: os controles de quem assiste foram e voltaram, e o pedido inválido foi recusado." : "FALHOU: os pedidos de quem assiste não chegaram como deviam.");
        return ok;
    }

    /// <summary>
    /// Dois PCs de mentira (duas salas em portas diferentes) se acham, trocam uma mensagem, passam a foto do perfil e uma
    /// imagem do chat de um para o outro e conversam por voz.
    /// </summary>
    private static async Task<bool> RoomTestAsync(Action<string> line)
    {
        line("Sala da rede: dois PCs de mentira no 127.0.0.1.");
        // Arquivos de A: a foto do perfil (40 KB) e uma imagem do chat (1 MB, umas 870 partes).
        var avatar = RandomNumberGenerator.GetBytes(40 * 1024);
        var picture = RandomNumberGenerator.GetBytes(1024 * 1024);
        var filesA = new Dictionary<string, byte[]> { [RoomBlob.IdOf(avatar)] = avatar, [RoomBlob.IdOf(picture)] = picture };
        var filesB = new System.Collections.Concurrent.ConcurrentDictionary<string, byte[]>();
        var a = new NetworkRoom(Guid.NewGuid(), RoomPortA, new RoomLocalState("Autoteste A", Avatar: new RoomBlobRef(RoomBlob.IdOf(avatar), avatar.Length)))
        {
            BroadcastTargets = () => [],
            OutputEnabled = false,
            BlobSource = id => filesA.GetValueOrDefault(id),
            HasBlob = filesA.ContainsKey,
        };
        var b = new NetworkRoom(Guid.NewGuid(), RoomPortB, new RoomLocalState("Autoteste B"))
        {
            BroadcastTargets = () => [],
            OutputEnabled = false,
            BlobSource = id => filesB.GetValueOrDefault(id),
            HasBlob = filesB.ContainsKey,
        };
        b.BlobReceived += (id, data) => filesB[id] = data;
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

            // A foto do perfil vem sozinha (está na presença de A); a imagem, quando a mensagem com ela chega.
            var started = Stopwatch.StartNew();
            var avatarOk = await WaitAsync(() => filesB.TryGetValue(RoomBlob.IdOf(avatar), out var got) && got.AsSpan().SequenceEqual(avatar), 10);
            line(avatarOk ? $"Foto do perfil: B baixou a de A ({avatar.Length / 1024} KB) em {started.ElapsedMilliseconds} ms." : "FALHOU: a foto do perfil de A não chegou em B.");
            started.Restart();
            a.SendChat("Imagem do autoteste", new ChatImage(RoomBlob.IdOf(picture), picture.Length, 1600, 900));
            var pictureOk = await WaitAsync(() => filesB.TryGetValue(RoomBlob.IdOf(picture), out var got) && got.AsSpan().SequenceEqual(picture), 20);
            line(pictureOk ? $"Imagem do chat: B baixou {picture.Length / 1024} KB em {started.ElapsedMilliseconds} ms, com o hash conferido." : "FALHOU: a imagem do chat não chegou em B.");

            a.JoinVoice(new ToneVoiceInput());
            b.JoinVoice(new ToneVoiceInput());
            var voiceOk = await WaitAsync(() => a.Voice?.FramesDecoded > 50 && b.Voice?.FramesDecoded > 50, 10);
            line($"Voz: A mandou {a.Voice?.FramesSent ?? 0} quadros e tocou {a.Voice?.FramesDecoded ?? 0}; " +
                 $"B mandou {b.Voice?.FramesSent ?? 0} e tocou {b.Voice?.FramesDecoded ?? 0} (recuperados pela cópia: {(a.Voice?.FramesRecovered ?? 0) + (b.Voice?.FramesRecovered ?? 0)}).");
            var peerInVoice = a.Peers.FirstOrDefault()?.Presence.InVoice == true && b.Peers.FirstOrDefault()?.Presence.InVoice == true;
            if (!peerInVoice) line("FALHOU: um não viu o outro entrar na voz.");
            if (!voiceOk) line("FALHOU: a voz não chegou dos dois lados.");

            var channelsOk = await ChannelsTestAsync(a, b, line);
            var privateOk = await PrivateRoomTestAsync(a, b, line);

            await a.LeaveVoiceAsync();
            var leftOk = await WaitAsync(() => b.Peers.FirstOrDefault()?.Presence.InAnyVoice == false, 4);
            if (!leftOk) line("FALHOU: B não viu A sair da voz.");

            var ok = chatOk && avatarOk && pictureOk && voiceOk && peerInVoice && channelsOk && privateOk && leftOk;
            line(ok ? "PASSOU: presença, chat, fotos, imagens, voz, canais e sala privada funcionam entre dois PCs." : "FALHOU: a sala da rede não funcionou como deveria.");
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

    /// <summary>
    /// Canais (1.3.0), com A e B na voz Geral: A cria um canal de texto e um de voz, manda uma mensagem no de texto e muda
    /// de canal de voz; B vê os canais, recebe a mensagem no canal certo, vê A mudar e, indo atrás, volta a ouvir A.
    /// </summary>
    private static async Task<bool> ChannelsTestAsync(NetworkRoom a, NetworkRoom b, Action<string> line)
    {
        a.CreatedChannels = [new RoomChannel(RoomChannelKind.Text, "autoteste"), new RoomChannel(RoomChannelKind.Voice, "Autoteste")];
        var listedOk = await WaitAsync(() => b.Peers.FirstOrDefault()?.Presence.CreatedChannels.Count == 2, 6);
        if (!listedOk) line("FALHOU: B não viu os canais que A criou.");

        var inChannel = new TaskCompletionSource<RoomChatMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        b.MessageReceived += message =>
        {
            if (message.AuthorId == a.SelfId && !message.InDefaultChannel) inChannel.TrySetResult(message);
        };
        a.SendChat("Mensagem no canal do autoteste", channel: "autoteste");
        var textOk = await Task.WhenAny(inChannel.Task, Task.Delay(5000)) == inChannel.Task && inChannel.Task.Result.ChannelName == "autoteste";
        if (!textOk) line("FALHOU: a mensagem do #autoteste não chegou no canal certo.");

        a.MoveVoice("Autoteste");
        var movedOk = await WaitAsync(() => b.Peers.FirstOrDefault()?.Presence.CurrentVoiceChannel == "Autoteste", 6);
        if (!movedOk) line("FALHOU: B não viu A mudar para o canal de voz Autoteste.");
        b.MoveVoice("Autoteste");
        var heard = b.Voice?.FramesDecoded ?? 0;
        var voiceOk = await WaitAsync(() => (b.Voice?.FramesDecoded ?? 0) > heard + 50 && b.Peers.FirstOrDefault()?.Presence.InVoiceChannel("Autoteste") == true, 10);
        if (!voiceOk) line("FALHOU: a voz não chegou no canal de voz novo.");

        var ok = listedOk && textOk && movedOk && voiceOk;
        if (ok) line($"Canais: B viu os 2 canais de A, a mensagem chegou no #autoteste e B ouviu A no canal Autoteste ({(b.Voice?.FramesDecoded ?? 0) - heard} quadros).");
        return ok;
    }

    /// <summary>
    /// Sala privada (1.4.0), com A e B na voz: A cria a sala e os dois entram com o convite. Um terceiro PC (C), sem o
    /// convite, não vê ninguém; o chat e a voz continuam entre A e B, cifrados. A troca a chave e B passa para a nova
    /// sozinho; depois A remove B, que fica de fora.
    /// </summary>
    private static async Task<bool> PrivateRoomTestAsync(NetworkRoom a, NetworkRoom b, Action<string> line)
    {
        var invite = RoomInvite.Create(a.SelfId);
        if (!RoomInvite.TryParse(invite.ToCode(), out var parsed))
        {
            line("FALHOU: o código do convite não abriu.");
            return false;
        }
        a.UsePrivateRoom(invite);
        b.UsePrivateRoom(parsed);
        var c = new NetworkRoom(Guid.NewGuid(), RoomPortC, new RoomLocalState("Autoteste C (sem convite)")) { BroadcastTargets = () => [], OutputEnabled = false };
        try
        {
            c.Start();
            c.AddManualPeer(IPAddress.Loopback, RoomPortA);
            a.AddManualPeer(IPAddress.Loopback, RoomPortC);
            var stillTogether = await WaitAsync(() => a.Peers.Any(p => p.Id == b.SelfId) && b.Peers.Any(p => p.Id == a.SelfId) && b.Peers[0].Presence.ExchangeKey is not null, 8);
            await Task.Delay(3000);
            var outsiderBlind = c.Peers.Count == 0 && a.Peers.All(p => p.Id != c.SelfId);
            if (!stillTogether) line("FALHOU: A e B não se viram na sala privada.");
            if (!outsiderBlind) line($"FALHOU: quem não tem o convite viu a sala (C vê {c.Peers.Count}).");

            var received = new TaskCompletionSource<RoomChatMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
            b.MessageReceived += message =>
            {
                if (message.AuthorId == a.SelfId && message.Text == "Mensagem cifrada") received.TrySetResult(message);
            };
            a.SendChat("Mensagem cifrada");
            var chatOk = await Task.WhenAny(received.Task, Task.Delay(5000)) == received.Task;
            if (!chatOk) line("FALHOU: o chat não chegou na sala privada.");
            var heard = b.Voice?.FramesDecoded ?? 0;
            var voiceOk = await WaitAsync(() => (b.Voice?.FramesDecoded ?? 0) > heard + 30, 8);
            if (!voiceOk) line("FALHOU: a voz não chegou na sala privada.");

            var rotated = a.RotateKey([]);
            var rekeyOk = rotated is not null && await WaitAsync(() => b.Keys.Epoch == rotated.Epoch, 10);
            if (!rekeyOk) line("FALHOU: B não recebeu a chave nova do dono.");
            var afterRekey = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            b.MessageReceived += message =>
            {
                if (message.AuthorId == a.SelfId && message.Text == "Depois da troca") afterRekey.TrySetResult(true);
            };
            a.SendChat("Depois da troca");
            var afterOk = await Task.WhenAny(afterRekey.Task, Task.Delay(5000)) == afterRekey.Task;
            if (!afterOk) line("FALHOU: o chat não chegou depois da troca de chave.");

            var removed = a.RotateKey([b.SelfId]);
            await Task.Delay(3000);
            var removedOk = removed is not null && b.Keys.Epoch != removed.Epoch && a.Peers.All(p => p.Id != b.SelfId);
            if (!removedOk) line("FALHOU: B continuou na sala depois de removido.");

            var ok = stillTogether && outsiderBlind && chatOk && voiceOk && rekeyOk && afterOk && removedOk;
            if (ok) line($"Sala privada: C (sem convite) não viu ninguém, chat e voz cifrados chegaram, B passou para a chave {rotated!.Epoch} sozinho e, removido, ficou de fora.");
            return ok;
        }
        finally
        {
            await c.DisposeAsync();
            a.SetBlocked(b.SelfId, false);
            a.UsePrivateRoom(null);
            b.UsePrivateRoom(null);
            await WaitAsync(() => a.Peers.Any(p => p.Id == b.SelfId) && b.Peers.Any(p => p.Id == a.SelfId), 6);
        }
    }

    // Tons do teste do som do PC: o do ScreenFlow e os dois que o CI deixa tocando fora dele (veja windows-build.yml).
    private const double OwnHz = 1000, MutedHz = 440, KeptHz = 700;
    private const string MutedExe = "powershell.exe";
    private const string KeptExe = "pwsh.exe";

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
        AppAudioMixer? only = null;
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
                only = AppAudioMixer.OpenOnly([KeptExe]);
                recorders.Add(new Recorder($"só o {KeptExe}", only));
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
            line($"Só um app capturando: {(only!.CapturedApps.Count == 0 ? "nenhum app" : string.Join(", ", only.CapturedApps))}.");

            var (all, withoutUs, withoutApp, onlyApp) = (recorders[0], recorders[1], recorders[2], recorders[3]);
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
            Check(onlyApp.Seconds > 0.8, "a captura de um app só não entregou áudio.");
            Check(Silent(onlyApp, OwnHz), "o som do próprio ScreenFlow apareceu na captura de um app só.");
            Check(Silent(withoutUs, OwnHz), "o som do próprio ScreenFlow apareceu na captura sem o ScreenFlow.");
            Check(Silent(withoutApp, OwnHz), "o som do próprio ScreenFlow apareceu na mistura.");
            var external = 0;
            if (all.Level(MutedHz) > 0.05)
            {
                external++;
                Check(Heard(withoutUs, MutedHz), $"a captura sem o ScreenFlow perdeu o som do {MutedExe}.");
                Check(Silent(withoutApp, MutedHz), $"o som do {MutedExe} apareceu na mistura que deveria tirá-lo.");
                Check(Silent(onlyApp, MutedHz), $"o som do {MutedExe} apareceu na captura que deveria ter só o {KeptExe}.");
            }
            if (all.Level(KeptHz) > 0.05)
            {
                external++;
                Check(Heard(withoutUs, KeptHz), "a captura sem o ScreenFlow perdeu o som de outro app.");
                Check(Heard(withoutApp, KeptHz), "a mistura perdeu o som de um app que não foi tirado.");
                Check(Heard(onlyApp, KeptHz), $"a captura de um app só perdeu o som do {KeptExe}.");
            }
            if (external < 2) line("Aviso: os tons de fora (440 e 700 Hz) não estavam tocando todos, então só parte da separação foi conferida.");
            line(ok ? $"PASSOU: o som do ScreenFlow e o do app tirado ficam fora; o resto do som do PC vai; e só o {KeptExe} vai quando é o único escolhido."
                : "FALHOU: a separação do som por app não funcionou.");
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
            only?.Dispose();
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
