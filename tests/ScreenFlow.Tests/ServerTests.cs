using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using ScreenFlow.Core;
using ScreenFlow.Core.Media;
using ScreenFlow.Core.Protocol;
using ScreenFlow.Core.Room;
using ScreenFlow.Core.Security;
using ScreenFlow.Core.Server;
using Xunit;

namespace ScreenFlow.Tests;

/// <summary>O servidor ScreenFlow (1.4.1): pacotes novos, o cache do repasse e o servidor de ponta a ponta pela rede local.</summary>
public class ServerPacketTests
{
    private static readonly Guid A = Guid.Parse("11111111-2222-3333-4444-555555555555");
    private static readonly Guid B = Guid.Parse("66666666-7777-8888-9999-aaaaaaaaaaaa");

    [Fact]
    public void RelayEnvelopeCarriesThePacketAndTheTargets()
    {
        var inner = RoomPacket.Leave(A);
        var envelope = RoomPacket.Relay(A, inner, [B]);
        Assert.True(RoomPacket.TryReadHeader(envelope, out var type, out var sender));
        Assert.Equal(RoomPacketType.Relay, type);
        Assert.Equal(A, sender);
        Assert.True(RoomPacket.TryReadRelay(RoomPacket.Body(envelope), A, out var targets, out var read));
        Assert.Equal([B], targets);
        Assert.Equal(inner, read.ToArray());

        // Para todo mundo (sem destinos) e com destinos demais, que viram "todo mundo".
        Assert.True(RoomPacket.TryReadRelay(RoomPacket.Body(RoomPacket.Relay(A, inner)), A, out targets, out _));
        Assert.Empty(targets);
        var many = Enumerable.Range(0, RoomPacket.MaxRelayTargets + 1).Select(_ => Guid.NewGuid()).ToList();
        Assert.True(RoomPacket.TryReadRelay(RoomPacket.Body(RoomPacket.Relay(A, inner, many)), A, out targets, out _));
        Assert.Empty(targets);
    }

    [Fact]
    public void RelayRefusesAPacketSignedBySomeoneElseOrANestedEnvelope()
    {
        var notMine = RoomPacket.Relay(A, RoomPacket.Leave(B));
        Assert.False(RoomPacket.TryReadRelay(RoomPacket.Body(notMine), A, out _, out _));
        var nested = RoomPacket.Relay(A, RoomPacket.Relay(A, RoomPacket.Leave(A)));
        Assert.False(RoomPacket.TryReadRelay(RoomPacket.Body(nested), A, out _, out _));
        Assert.False(RoomPacket.TryReadRelay([3, 1, 2], A, out _, out _));
    }

    [Fact]
    public void ProbeAndServerInfoRoundTrip()
    {
        var info = new RoomServerInfo("Servidor da Casa", "1.4.1", "bm9uY2U=", PasswordRequired: true, Authorized: false, Members: 3, Streams: 1, ControlPort: 47810);
        var packet = RoomPacket.ServerInfo(B, info);
        Assert.True(RoomPacket.TryReadHeader(packet, out var type, out var sender));
        Assert.Equal((RoomPacketType.ServerInfo, B), (type, sender));
        Assert.True(RoomPacket.TryReadServerInfo(RoomPacket.Body(packet), out var read));
        Assert.Equal(info, read);
        Assert.False(RoomPacket.TryReadServerProbe(RoomPacket.Body(packet), out _, out _));

        var proof = RoomPacket.ServerProof("senha boa", info.Nonce, A);
        var probe = RoomPacket.ServerProbe(A, "1.4.1", proof);
        Assert.True(RoomPacket.TryReadServerProbe(RoomPacket.Body(probe), out var version, out var readProof));
        Assert.Equal("1.4.1", version);
        Assert.True(RoomPacket.VerifyServerProof("senha boa", info.Nonce, A, readProof));
        Assert.False(RoomPacket.VerifyServerProof("senha ruim", info.Nonce, A, readProof));
        Assert.False(RoomPacket.VerifyServerProof("senha boa", info.Nonce, B, readProof)); // a prova de um PC não serve para outro
        Assert.True(RoomPacket.TryReadServerProbe(RoomPacket.Body(RoomPacket.ServerProbe(A, "1.4.1")), out _, out var none));
        Assert.Null(none);
    }

    [Fact]
    public void ChannelOperationsRoundTrip()
    {
        var rename = RoomPacket.ChannelOp(A, RoomChannelOperation.Rename, RoomChannelKind.Voice, "Valorant", "Valorant ranked");
        Assert.True(RoomPacket.TryReadChannelOp(RoomPacket.Body(rename), out var operation, out var channel, out var renamed));
        Assert.Equal(RoomChannelOperation.Rename, operation);
        Assert.Equal(new RoomChannel(RoomChannelKind.Voice, "Valorant"), channel);
        Assert.Equal(new RoomChannel(RoomChannelKind.Voice, "Valorant ranked"), renamed);
        var create = RoomPacket.ChannelOp(A, RoomChannelOperation.Create, RoomChannelKind.Text, "Pedidos de Música");
        Assert.True(RoomPacket.TryReadChannelOp(RoomPacket.Body(create), out _, out channel, out renamed));
        Assert.Equal("pedidos-de-música", channel.Name);
        Assert.Null(renamed);
        Assert.False(RoomPacket.TryReadChannelOp(RoomPacket.Body(RoomPacket.ChannelOp(A, RoomChannelOperation.Create, RoomChannelKind.Text, "!!!")), out _, out _, out _));
    }

    [Fact]
    public void ServerPresenceCarriesTheServerChannels()
    {
        var channels = new ValueList<RoomChannel>(Enumerable.Range(1, 30).Select(i => new RoomChannel(i % 2 == 0 ? RoomChannelKind.Voice : RoomChannelKind.Text, $"canal {i}")));
        var presence = new RoomPresence("Servidor", "1.4.1", PresenceFlags.Server | PresenceFlags.Channels, Channels: channels);
        var packet = RoomPacket.Presence(A, presence);
        Assert.True(packet.Length <= RoomPacket.MaximumSize);
        Assert.True(RoomPacket.TryReadPresence(RoomPacket.Body(packet), out var read));
        Assert.True(read.IsServer);
        Assert.Equal(30, read.CreatedChannels.Count); // uma pessoa leva até 12; o servidor leva todos

        // Nomes longos demais para caber todos: os que não cabem ficam de fora, mas o pacote continua válido.
        var longOnes = new ValueList<RoomChannel>(Enumerable.Range(1, RoomChannels.MaxPerServer)
            .Select(i => new RoomChannel(RoomChannelKind.Voice, new string('ç', 20) + i)));
        var big = RoomPacket.Presence(A, presence with { Channels = longOnes });
        Assert.True(big.Length <= RoomPacket.MaximumSize);
        Assert.True(RoomPacket.TryReadPresence(RoomPacket.Body(big), out read));
        Assert.InRange(read.CreatedChannels.Count, 20, RoomChannels.MaxPerServer);
    }

    [Fact]
    public void ChannelStoreKeepsTheChannelsAndProtectsTheGeneralOnes()
    {
        var folder = Path.Combine(Path.GetTempPath(), "screenflow-canais-" + Guid.NewGuid().ToString("N"));
        var file = Path.Combine(folder, "canais.json");
        try
        {
            var store = new ChannelStore(file);
            var valorant = new RoomChannel(RoomChannelKind.Voice, "Valorant");
            Assert.Null(store.Apply(RoomChannelOperation.Create, valorant));
            Assert.NotNull(store.Apply(RoomChannelOperation.Create, new RoomChannel(RoomChannelKind.Voice, "VALORANT")));
            Assert.NotNull(store.Apply(RoomChannelOperation.Create, RoomChannels.DefaultText));
            Assert.NotNull(store.Apply(RoomChannelOperation.Delete, RoomChannels.DefaultVoice));
            Assert.Null(store.Apply(RoomChannelOperation.Create, new RoomChannel(RoomChannelKind.Text, "clipes")));
            Assert.Null(store.Apply(RoomChannelOperation.Rename, valorant, new RoomChannel(RoomChannelKind.Voice, "Ranked")));
            Assert.NotNull(store.Apply(RoomChannelOperation.Rename, new RoomChannel(RoomChannelKind.Voice, "Ranked"), RoomChannels.DefaultVoice));

            var again = new ChannelStore(file); // guardado no disco
            Assert.Equal(["clipes", "Ranked"], again.All.Select(c => c.Name).OrderBy(n => n));
            Assert.Null(again.Apply(RoomChannelOperation.Delete, new RoomChannel(RoomChannelKind.Text, "clipes")));
            Assert.Equal(["Ranked"], new ChannelStore(file).All.Select(c => c.Name));
        }
        finally
        {
            try { Directory.Delete(folder, recursive: true); } catch { }
        }
    }

    [Fact]
    public void RenamedTextChannelsKeepTheirConversation()
    {
        var folder = Path.Combine(Path.GetTempPath(), "screenflow-canais-" + Guid.NewGuid().ToString("N"));
        var file = Path.Combine(folder, "canais.json");
        try
        {
            var store = new ChannelStore(file);
            var jogos = new RoomChannel(RoomChannelKind.Text, "jogos");
            var games = new RoomChannel(RoomChannelKind.Text, "games");
            var partidas = new RoomChannel(RoomChannelKind.Text, "partidas");
            Assert.Null(store.Apply(RoomChannelOperation.Create, jogos));
            Assert.Null(store.Apply(RoomChannelOperation.Rename, jogos, games));
            Assert.Equal("games", store.CurrentTextName("jogos"));
            Assert.Equal("games", store.CurrentTextName("Jogos"));
            // Renomear de novo leva junto a conversa do primeiro nome.
            Assert.Null(store.Apply(RoomChannelOperation.Rename, games, partidas));
            Assert.Equal("partidas", store.CurrentTextName("jogos"));
            Assert.Equal("partidas", store.CurrentTextName("games"));
            Assert.Null(store.CurrentTextName(null));
            Assert.Equal("geral", store.CurrentTextName("geral"));
            Assert.Equal("outro", store.CurrentTextName("outro"));

            // Guardado no disco; um canal novo com um nome antigo começa sem a conversa de lá.
            var again = new ChannelStore(file);
            Assert.Equal("partidas", again.CurrentTextName("jogos"));
            Assert.Null(again.Apply(RoomChannelOperation.Create, games));
            Assert.Equal("games", again.CurrentTextName("games"));
            Assert.Equal("partidas", again.CurrentTextName("jogos"));

            // O app confere antes de mandar: as mesmas regras do servidor.
            Assert.NotNull(ChannelStore.Problem(again.All, RoomChannelOperation.Create, partidas));
            Assert.NotNull(ChannelStore.Problem(again.All, RoomChannelOperation.Rename, partidas, games));
            Assert.Null(ChannelStore.Problem(again.All, RoomChannelOperation.Rename, partidas, new RoomChannel(RoomChannelKind.Text, "ranked")));
            Assert.NotNull(ChannelStore.Problem(again.All, RoomChannelOperation.Delete, new RoomChannel(RoomChannelKind.Voice, "Nada")));
        }
        finally
        {
            try { Directory.Delete(folder, recursive: true); } catch { }
        }
    }

    [Fact]
    public void RelayCacheServesAndRecoversByParity()
    {
        var frame = RandomNumberGenerator.GetBytes(Packetizer.DefaultPayloadSize * 9 + 77);
        var chunks = Packetizer.Packetize(MediaPacketType.Video, 42, frame, 1234, keyframe: true, fecGroupSize: 5, spreadFec: true);
        var cache = new RelayTrackCache(8, MediaPacketType.Video);
        var lost = chunks.First(c => c.Header.Type == MediaPacketType.Video && c.Header.Index == 3);
        foreach (var chunk in chunks.Where(c => c != lost)) Assert.True(cache.Store(chunk.Header, chunk.Payload.Span));
        Assert.False(cache.Store(chunks[0].Header, chunks[0].Payload.Span)); // repetido
        Assert.False(cache.Has(42, 3));

        var lookup = cache.Find(42, [3, 4]);
        Assert.False(lookup.Unknown);
        Assert.Empty(lookup.Missing);
        var recovered = lookup.Found.Single(p => p.Header.Index == 3);
        Assert.Equal(lost.Payload.ToArray(), recovered.Payload);
        Assert.True(recovered.Header.Flags.HasFlag(MediaPacketFlags.Keyframe));
        Assert.Equal((uint)42, recovered.Header.FrameId);
        Assert.Equal(1234, recovered.Header.TimestampUs);
        Assert.True(cache.Has(42, 3)); // ficou guardado

        // O frame inteiro, e um frame que o servidor nunca viu.
        Assert.Equal(10, cache.Find(42, []).Found.Count);
        var unknown = cache.Find(41, [1]);
        Assert.True(unknown.Unknown);
        Assert.Equal([1], unknown.Missing);
    }

    [Fact]
    public void RelayCacheForgetsOldFrames()
    {
        var cache = new RelayTrackCache(4, MediaPacketType.CameraVideo);
        for (uint id = 0; id < 10; id++)
            foreach (var chunk in Packetizer.Packetize(MediaPacketType.CameraVideo, id, new byte[500], 0, keyframe: false))
                cache.Store(chunk.Header, chunk.Payload.Span);
        Assert.Equal(4, cache.Frames);
        Assert.True(cache.Find(3, [0]).Unknown);
        Assert.False(cache.Find(9, [0]).Unknown);
        // A outra faixa não entra neste cache.
        var video = Packetizer.Packetize(MediaPacketType.Video, 20, new byte[100], 0, keyframe: false)[0];
        Assert.False(cache.Store(video.Header, video.Payload.Span));
    }

    [Fact]
    public void ClockSyncTranslatesBothWays()
    {
        var clock = new ClockSync();
        Assert.Equal(500, clock.ViewerToHostUs(500)); // sem amostra, não mexe
        clock.AddSample(1_000, 1_001_050, 1_100); // host 1 s adiantado, RTT 100 µs (meio do ping em 1_050)
        Assert.InRange(clock.ViewerToHostUs(2_000) - 2_000, 999_900, 1_000_100);
        Assert.InRange(clock.HostToViewerUs(clock.ViewerToHostUs(5_000)), 4_999, 5_001);
    }

    [Fact]
    public void OldMessagesStillReadWithoutTheNewFields()
    {
        var hello = ControlFraming.Deserialize("""{"t":"hello","protocol":1,"appVersion":"1.4.0","viewerName":"x","decoders":["H264"],"maxWidth":1920,"maxHeight":1080,"maxFps":60,"udpPort":5,"publicKey":"k"}"""u8);
        Assert.Null(Assert.IsType<HelloMessage>(hello).StreamId);
        var command = ControlFraming.Deserialize("""{"t":"mediacmd","command":"seek","positionSeconds":10}"""u8);
        Assert.Null(Assert.IsType<MediaCommandMessage>(command).From);
        var audience = new AudienceMessage(2, ["Ana", "Beto"], [VideoCodec.H264], 1920, 1080, 60);
        var read = Assert.IsType<AudienceMessage>(ControlFraming.Deserialize(ControlFraming.Serialize(audience).AsSpan(4)));
        Assert.Equal(audience.Names, read.Names);
        Assert.Equal(audience.Decoders, read.Decoders);
        var announcement = new DiscoveryAnnouncement(AppInfo.Name, 1, "Ana", "100.64.0.9", 47810, "1.4.1", false, 0, Stream: A.ToString("N"), Via: "Casa");
        Assert.True(DiscoveryAnnouncement.TryParse(announcement.ToBytes(), out var parsed));
        Assert.Equal($"100.64.0.9/{A:N}", parsed!.Key);
        Assert.DoesNotContain("stream", System.Text.Encoding.UTF8.GetString(new DiscoveryAnnouncement(AppInfo.Name, 1, "Ana", "26.1.2.3", 47801, "1.4.1", false, 0).ToBytes()));
    }
}

/// <summary>
/// O servidor de verdade na rede local do PC: alguém transmite para ele (fazendo o papel do app), duas pessoas assistem
/// por ele e a sala passa chat e canais. Sem vídeo de verdade: os frames são bytes aleatórios, que o servidor não abre.
/// </summary>
public sealed class ServerEndToEndTests : IAsyncLifetime
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);
    private ScreenFlowServer _server = null!;
    private string _folder = null!;

    public Task InitializeAsync()
    {
        _folder = Path.Combine(Path.GetTempPath(), "screenflow-servidor-" + Guid.NewGuid().ToString("N"));
        _server = new ScreenFlowServer(new ServerOptions
        {
            Name = "Servidor de teste",
            BindAddress = IPAddress.Loopback,
            ControlPort = 0,
            MediaPort = 0,
            RoomPort = 0,
            Password = "segredo",
            DataDirectory = _folder,
            OfflineGrace = TimeSpan.FromSeconds(20),
        });
        _server.Start();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await _server.DisposeAsync();
        try { Directory.Delete(_folder, recursive: true); } catch { }
    }

    [Fact]
    public async Task ScreenGoesThroughTheServerToEveryViewer()
    {
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var token = cancel.Token;
        var streamId = Guid.NewGuid();
        await using var publisher = await FakePublisher.ConnectAsync(_server.Streams.ControlPort, streamId, "segredo", token);
        Assert.Equal("Servidor de teste", publisher.ServerName);
        Assert.Equal(_server.Streams.MediaPort, publisher.ServerHello.UdpPort);
        Assert.Contains(StreamModes.FeatureSpreadFec, publisher.ServerHello.Features!);

        // Senha errada não entra.
        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => FakeViewer.ConnectAsync(_server.Streams.ControlPort, streamId, "errada", "Intruso", token));
        Assert.Contains("Senha", refused.Message);

        await using var ana = await FakeViewer.ConnectAsync(_server.Streams.ControlPort, streamId, "segredo", "Ana", token);
        await using var beto = await FakeViewer.ConnectAsync(_server.Streams.ControlPort, streamId, "segredo", "Beto", token);
        Assert.Equal("PC de quem transmite", ana.Welcome.HostName);
        Assert.Equal(_server.Streams.MediaPort, ana.Welcome.MediaPort);

        // Quem transmite fica sabendo de quem assiste e do codec que todos decodificam.
        var audience = await publisher.WaitForAsync<AudienceMessage>(a => a.Viewers == 2, token);
        Assert.Equal(["Ana", "Beto"], audience.Names.OrderBy(n => n));
        Assert.Equal([VideoCodec.H264], audience.Decoders);
        Assert.Equal(1280, audience.MaxWidth);
        await publisher.WaitForAsync<KeyframeRequestMessage>(_ => true, token);

        // Um frame com um pacote perdido entre quem transmite e o servidor (e sem a paridade daquele grupo).
        var frame = RandomNumberGenerator.GetBytes(Packetizer.DefaultPayloadSize * 6 + 10);
        var chunks = Packetizer.Packetize(MediaPacketType.Video, 7, frame, 99, keyframe: true, fecGroupSize: 4, spreadFec: true);
        var lost = chunks.Single(c => c.Header.Type == MediaPacketType.Video && c.Header.Index == 2);
        var parityOfLost = chunks.Single(c => c.Header.Type == MediaPacketType.VideoFec && Packetizer.GroupMembers(c.Header.Index, 7, 4, true).Contains(2));
        foreach (var chunk in chunks.Where(c => c != lost && c != parityOfLost)) publisher.SendMedia(chunk);
        publisher.SendMedia(new MediaChunk { Header = new MediaHeader { Type = MediaPacketType.Audio, FrameId = 1, Count = 1, TimestampUs = 5 }, Payload = new byte[] { 1, 2, 3 } });

        var anaGot = await ana.WaitForPacketsAsync(p => p.Header.Type == MediaPacketType.Video && p.Header.FrameId == 7, 6, token);
        Assert.DoesNotContain(anaGot, p => p.Header.Index == 2);
        await ana.WaitForPacketsAsync(p => p.Header.Type == MediaPacketType.Audio, 1, token);
        await beto.WaitForPacketsAsync(p => p.Header.Type == MediaPacketType.Video && p.Header.FrameId == 7, 6, token);

        // Ana pede um pacote que o servidor tem: vem do servidor, sem incomodar quem transmite.
        await ana.SendAsync(new NackMessage(7, [1]), token);
        var resent = await ana.WaitForPacketsAsync(p => p.Header.FrameId == 7 && p.Header.Index == 1 && p.Header.Flags.HasFlag(MediaPacketFlags.Retransmit), 1, token);
        Assert.Equal(chunks.Single(c => c.Header.Type == MediaPacketType.Video && c.Header.Index == 1).Payload.ToArray(), resent[0].Payload);

        // Ana pede o que o servidor também não tem: o servidor pede a quem transmite (uma vez só, mesmo com Beto pedindo junto)…
        await ana.SendAsync(new NackMessage(7, [2]), token);
        await beto.SendAsync(new NackMessage(7, [2]), token);
        var nack = await publisher.WaitForAsync<NackMessage>(n => n.FrameId == 7, token);
        Assert.Equal([(ushort)2], nack.Missing);
        // …e a retransmissão vai para todo mundo que assiste.
        publisher.SendMedia(lost, retransmit: true);
        var anaFixed = await ana.WaitForPacketsAsync(p => p.Header.FrameId == 7 && p.Header.Index == 2, 1, token);
        Assert.Equal(lost.Payload.ToArray(), anaFixed[0].Payload);
        await beto.WaitForPacketsAsync(p => p.Header.FrameId == 7 && p.Header.Index == 2, 1, token);
        await Task.Delay(300, token);
        Assert.Equal(1, publisher.Count<NackMessage>(n => n.FrameId == 7));

        // O ping de quem assiste volta com a hora de quem transmite (aqui, 5 s adiantada).
        await publisher.WaitForAsync<PingMessage>(_ => true, token); // o servidor já mediu o relógio
        await Task.Delay(700, token);
        var sent = MediaClock.NowUs;
        await ana.SendAsync(new PingMessage(sent), token);
        var pong = await ana.WaitForAsync<PongMessage>(p => p.ViewerTimeUs == sent, token);
        Assert.InRange(pong.HostTimeUs - sent, 4_500_000, 5_500_000);

        // Um pedido de quem assiste chega com o nome da pessoa, e a resposta volta para ela com o número dela.
        publisher.Publish(new RemoteStateMessage(true, true, true, ["1080p"], [60], [], "1080p", 60, true));
        publisher.Publish(new MediaStateMessage("Spotify", "Música", "Artista", true, 10, 200, true, true, true, true, SessionKey: "s1"));
        await ana.WaitForAsync<MediaStateMessage>(m => m.SessionKey == "s1", token);
        await ana.SendAsync(new MediaCommandMessage(MediaCommandMessage.Seek, 42, RequestId: 5, SessionKey: "s1"), token);
        var command = await publisher.WaitForAsync<MediaCommandMessage>(_ => true, token);
        Assert.Equal("Ana", command.From);
        Assert.NotEqual(0, command.RequestId);
        publisher.Publish(new MediaCommandResultMessage(command.RequestId, command.Command, MediaSeek.OutcomeDone, 42));
        var result = await ana.WaitForAsync<MediaCommandResultMessage>(_ => true, token);
        Assert.Equal(5, result.RequestId);
        await Task.Delay(200, token);
        Assert.Equal(0, beto.Count<MediaCommandResultMessage>(_ => true));

        // As estatísticas de quem assiste chegam juntas para quem transmite (a pior perda e o pior RTT).
        await ana.SendAsync(new StatsMessage(0.05, 30, 8000, 58, 40), token);
        await beto.SendAsync(new StatsMessage(0.01, 80, 9000, 60, 70), token);
        var stats = await publisher.WaitForAsync<StatsMessage>(s => s.LossRate >= 0.05 && s.RoundTripMs >= 80, token);
        Assert.Equal(58, stats.DecodeFps);

        var info = Assert.Single(_server.Streams.Streams);
        Assert.Equal(2, info.Viewers);
        Assert.True(info.Online);

        // Beto sai: quem transmite fica sabendo. Depois quem transmite encerra: Ana recebe a despedida.
        await beto.SendAsync(new ByeMessage("saindo"), token);
        await publisher.WaitForAsync<AudienceMessage>(a => a.Viewers == 1, token);
        await publisher.SendAsync(new ByeMessage("fim"), token);
        var bye = await ana.WaitForAsync<ByeMessage>(_ => true, token);
        Assert.Contains("encerrou", bye.Reason);
        await WaitUntil(() => _server.Streams.Streams.Count == 0, token);
    }

    [Fact]
    public async Task ViewersWaitWhenThePublisherDropsAndComeBackWithIt()
    {
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var token = cancel.Token;
        var streamId = Guid.NewGuid();
        var publisher = await FakePublisher.ConnectAsync(_server.Streams.ControlPort, streamId, "segredo", token);
        await using (var viewer = await FakeViewer.ConnectAsync(_server.Streams.ControlPort, streamId, "segredo", "Ana", token))
        {
            // A conexão de quem transmite cai (sem despedida): quem assiste é desconectado para reconectar.
            await publisher.DisposeAsync();
            await viewer.WaitForCloseAsync(token);
        }
        await WaitUntil(() => _server.Streams.Streams is [{ Online: false }], token);

        // Quem reconecta enquanto quem transmite está fora fica esperando, sem erro…
        var waiting = FakeViewer.ConnectAsync(_server.Streams.ControlPort, streamId, "segredo", "Ana", token, acceptPending: true);
        await Task.Delay(500, token);
        Assert.False(waiting.IsCompleted);
        // …e entra quando ele volta.
        await using var back = await FakePublisher.ConnectAsync(_server.Streams.ControlPort, streamId, "segredo", token);
        await using var viewerAgain = await waiting.WaitAsync(Wait, token);
        Assert.True(viewerAgain.SawPending);
        await back.WaitForAsync<AudienceMessage>(a => a.Viewers == 1, token);

        // Outro PC não toma o lugar de quem está transmitindo (aqui todos estão no mesmo IP, então o servidor aceita
        // e troca a conexão: é a reconexão do mesmo PC).
        Assert.Single(_server.Streams.Streams);
    }

    [Fact]
    public async Task ViewersFollowThePublisherWhenItSwitchesModes()
    {
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var token = cancel.Token;
        var streamId = Guid.NewGuid();
        var publisher = await FakePublisher.ConnectAsync(_server.Streams.ControlPort, streamId, "segredo", token);
        await using (var viewer = await FakeViewer.ConnectAsync(_server.Streams.ControlPort, streamId, "segredo", "Ana", token))
        {
            // Quem transmite troca de modo (1.5.0): a despedida diz que volta, e quem assiste (1.5.0) recebe a mesma
            // despedida, sabe que é uma troca e fica esperando em vez de encerrar.
            await publisher.SendAsync(new ByeMessage("Trocando de modo.", ByeMessage.Switching, ByeMessage.SwitchResumeSeconds, StreamMode.AudioOnly), token);
            var bye = await viewer.WaitForAsync<ByeMessage>(_ => true, token);
            Assert.True(bye.CanResume);
            Assert.Equal(ByeMessage.Switching, bye.Code);
            Assert.Equal(StreamMode.AudioOnly, bye.NextMode);
            await viewer.WaitForCloseAsync(token);
        }
        await publisher.DisposeAsync();
        // A transmissão continua no servidor, esperando quem transmite voltar.
        await WaitUntil(() => _server.Streams.Streams is [{ Online: false }], token);
        var waiting = FakeViewer.ConnectAsync(_server.Streams.ControlPort, streamId, "segredo", "Ana", token, acceptPending: true);
        await Task.Delay(400, token);
        Assert.False(waiting.IsCompleted);
        await using var back = await FakePublisher.ConnectAsync(_server.Streams.ControlPort, streamId, "segredo", token);
        await using var viewerAgain = await waiting.WaitAsync(Wait, token);
        Assert.True(viewerAgain.SawPending);
    }

    [Fact]
    public async Task AFinalByeStillEndsTheStreamAndMissingStreamsAreNotFound()
    {
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var token = cancel.Token;
        var streamId = Guid.NewGuid();
        var publisher = await FakePublisher.ConnectAsync(_server.Streams.ControlPort, streamId, "segredo", token);
        await using var viewer = await FakeViewer.ConnectAsync(_server.Streams.ControlPort, streamId, "segredo", "Ana", token);
        // Uma despedida sem volta (o app fechou): a transmissão sai do servidor e quem assiste recebe um tchau final.
        await publisher.SendAsync(new ByeMessage("O host encerrou a transmissão."), token);
        var bye = await viewer.WaitForAsync<ByeMessage>(_ => true, token);
        Assert.False(bye.CanResume);
        await WaitUntil(() => _server.Streams.Streams.Count == 0, token);
        await publisher.DisposeAsync();
        // Pedir uma transmissão que não está lá responde "não está aqui" (quem espera o host voltar continua esperando).
        using var client = new TcpClient(AddressFamily.InterNetwork) { NoDelay = true };
        await client.ConnectAsync(IPAddress.Loopback, _server.Streams.ControlPort, token);
        var stream = client.GetStream();
        using var key = new KeyExchange();
        await ControlFraming.WriteAsync(stream, new HelloMessage(AppInfo.ProtocolVersion, AppInfo.DisplayVersion, "Ana", [VideoCodec.H264], 1280, 720, 60,
            40000, key.PublicKey, [.. StreamModes.ViewerFeatures], streamId.ToString("N")), token);
        var rejected = Assert.IsType<RejectedMessage>(await ControlFraming.ReadAsync(stream, token));
        Assert.Equal(RejectedMessage.NotFound, rejected.Code);
        Assert.Contains("não está mais no servidor", rejected.Message);
    }

    [Fact]
    public async Task RoomGoesThroughTheServerWithPasswordChannelsAndHistory()
    {
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var token = cancel.Token;
        var port = _server.Room.Port;
        using var ana = new FakeRoomClient(port, "Ana");
        using var beto = new FakeRoomClient(port, "Beto");

        // Sem a senha, o servidor responde mas não deixa entrar.
        var info = await ana.ProbeAsync(null, token);
        Assert.True(info.PasswordRequired);
        Assert.False(info.Authorized);
        ana.SendRelay(RoomPacket.Presence(ana.Id, ana.Presence()));
        Assert.True((await ana.ProbeAsync(null, token)).PasswordRequired);

        // Com a senha, entra e recebe a presença do servidor (com os canais dele).
        Assert.True((await ana.ProbeAsync("segredo", token)).Authorized);
        Assert.True((await beto.ProbeAsync("segredo", token)).Authorized);
        var serverPresence = await ana.WaitForAsync(p => p.Type == RoomPacketType.Presence && p.Sender == _server.Id, token);
        Assert.True(RoomPacket.TryReadPresence(RoomPacket.Body(serverPresence.Data), out var presence));
        Assert.True(presence.IsServer);

        // A presença de um vai para o outro pelo servidor.
        ana.SendRelay(RoomPacket.Presence(ana.Id, ana.Presence()));
        beto.SendRelay(RoomPacket.Presence(beto.Id, beto.Presence()));
        await beto.WaitForAsync(p => p.Type == RoomPacketType.Presence && p.Sender == ana.Id, token);
        await ana.WaitForAsync(p => p.Type == RoomPacketType.Presence && p.Sender == beto.Id, token);
        await WaitUntil(() => _server.Room.Members.Count == 2 && _server.Room.Members.All(m => m.Name is "Ana" or "Beto"), token);

        // Chat para uma pessoa só: chega para ela, e o servidor guarda.
        var message = new RoomChatMessage(ana.Id, "Ana", 77, DateTimeOffset.UtcNow, "oi, Beto!");
        ana.SendRelay(RoomPacket.Chat(ana.Id, message), [beto.Id]);
        var chat = await beto.WaitForAsync(p => p.Type == RoomPacketType.Chat && p.Sender == ana.Id, token);
        Assert.True(RoomPacket.TryReadChat(RoomPacket.Body(chat.Data), out var received));
        Assert.Equal("oi, Beto!", received.Text);
        await WaitUntil(() => _server.Room.MessageCount == 1, token);

        // Um canal criado por alguém aparece na presença do servidor para todo mundo.
        beto.Send(RoomPacket.ChannelOp(beto.Id, RoomChannelOperation.Create, RoomChannelKind.Voice, "Valorant"));
        var updated = await ana.WaitForAsync(p => p.Type == RoomPacketType.Presence && p.Sender == _server.Id
            && RoomPacket.TryReadPresence(RoomPacket.Body(p.Data), out var withChannel) && withChannel.CreatedChannels.Any(c => c.Name == "Valorant"), token);
        Assert.NotNull(updated);

        // Quem chega depois pede o histórico ao servidor e recebe a conversa, mesmo com quem escreveu fora.
        using var caio = new FakeRoomClient(port, "Caio");
        Assert.True((await caio.ProbeAsync("segredo", token)).Authorized);
        caio.Send(RoomPacket.HistoryRequest(caio.Id));
        var history = await caio.WaitForAsync(p => p.Type == RoomPacketType.Chat && p.Sender == _server.Id, token);
        Assert.True(RoomPacket.TryReadChat(RoomPacket.Body(history.Data), out var old));
        Assert.True(old.History);
        Assert.Equal("oi, Beto!", old.Text);
        Assert.Equal(ana.Id, old.AuthorId);

        // O canal ficou guardado no disco do servidor.
        Assert.Contains(new ChannelStore(Path.Combine(_folder, "canais.json")).All, c => c.Name == "Valorant");
    }

    [Fact]
    public async Task RoomKeepsLittleSpaceForWhoHasNotProvedThePassword()
    {
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var token = cancel.Token;
        var port = _server.Room.Port;
        var strangers = Enumerable.Range(0, RoomRelay.MaxPendingPerAddress + 1).Select(i => new FakeRoomClient(port, $"Estranho {i}")).ToList();
        try
        {
            // Do mesmo IP, sem a senha, só alguns ficam esperando: o próximo id inventado nem recebe resposta.
            for (var i = 0; i < RoomRelay.MaxPendingPerAddress; i++) Assert.False((await strangers[i].ProbeAsync(null, token)).Authorized);
            Assert.Null(await strangers[^1].ProbeOrNullAsync(TimeSpan.FromSeconds(1), token));

            // Quem prova a senha sai da espera e abre lugar.
            Assert.True((await strangers[0].ProbeAsync("segredo", token)).Authorized);
            Assert.False((await strangers[^1].ProbeAsync(null, token)).Authorized);
        }
        finally
        {
            foreach (var stranger in strangers) stranger.Dispose();
        }
    }

    [Fact]
    public async Task RoomStopsCheckingPasswordsAfterTooManyWrongOnes()
    {
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var token = cancel.Token;
        using var guesser = new FakeRoomClient(_server.Room.Port, "Chutador");
        Assert.True((await guesser.ProbeAsync(null, token)).PasswordRequired);
        for (var i = 0; i < RoomRelay.MaxWrongPasswordsPerMinute; i++)
            Assert.False((await guesser.ProbeAsync($"chute {i}", token)).Authorized);
        // Passou do limite: por um minuto, nem a senha certa entra por este IP.
        Assert.False((await guesser.ProbeAsync("segredo", token)).Authorized);
        Assert.Empty(_server.Room.Members);
    }

    [Fact]
    public async Task RoomAnnouncesTheStreamsOfTheServer()
    {
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var token = cancel.Token;
        var streamId = Guid.NewGuid();
        await using var publisher = await FakePublisher.ConnectAsync(_server.Streams.ControlPort, streamId, "segredo", token);
        using var ana = new FakeRoomClient(_server.Room.Port, "Ana");
        Assert.True((await ana.ProbeAsync("segredo", token)).Authorized);
        var json = await ana.WaitForAsync(p => p.Json is not null, token);
        Assert.True(DiscoveryAnnouncement.TryParse(json.Data, out var announcement));
        Assert.Equal(streamId.ToString("N"), announcement!.Stream);
        Assert.Equal(_server.Streams.ControlPort, announcement.ControlPort);
        Assert.Equal("Servidor de teste", announcement.Via);
        Assert.True(announcement.PinRequired);
    }

    private static async Task WaitUntil(Func<bool> condition, CancellationToken token)
    {
        var deadline = DateTime.UtcNow + Wait;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("A condição não aconteceu a tempo.");
            await Task.Delay(50, token);
        }
    }

    // ───────────────────────── Quem transmite (no papel do app) ─────────────────────────

    private sealed class FakePublisher : IAsyncDisposable
    {
        private readonly TcpClient _client;
        private readonly NetworkStream _stream;
        private readonly Socket _udp;
        private readonly MediaSender _sender;
        private readonly PacketCipher _cipher;
        private readonly IPEndPoint _serverMedia;
        private readonly MessageInbox _inbox = new();
        private readonly CancellationTokenSource _stop = new();
        private readonly SemaphoreSlim _write = new(1, 1);
        private readonly Task _reader;
        private ulong _counter;

        private FakePublisher(TcpClient client, NetworkStream stream, Socket udp, PacketCipher cipher, IPEndPoint serverMedia, string serverName, HelloMessage hello)
        {
            _client = client;
            _stream = stream;
            _udp = udp;
            _sender = new MediaSender(udp);
            _cipher = cipher;
            _serverMedia = serverMedia;
            ServerName = serverName;
            ServerHello = hello;
            _reader = Task.Run(ReadLoopAsync);
        }

        public string ServerName { get; }
        public HelloMessage ServerHello { get; }

        public static async Task<FakePublisher> ConnectAsync(int port, Guid streamId, string password, CancellationToken token)
        {
            var client = new TcpClient(AddressFamily.InterNetwork) { NoDelay = true };
            await client.ConnectAsync(IPAddress.Loopback, port, token);
            var stream = client.GetStream();
            using var publisherKey = new KeyExchange();
            await ControlFraming.WriteAsync(stream, new PublishMessage(AppInfo.ProtocolVersion, AppInfo.DisplayVersion, "PC de quem transmite", streamId.ToString("N"),
                publisherKey.PublicKey, StreamMode.Screen), token);
            var challenge = Assert.IsType<ChallengeMessage>(await ControlFraming.ReadAsync(stream, token));
            Assert.True(challenge.PinRequired);
            var secret = publisherKey.DeriveSharedSecret(challenge.PublicKey);
            var transcript = SessionCrypto.Transcript(publisherKey.PublicKey, challenge.PublicKey, challenge.Nonce);
            await ControlFraming.WriteAsync(stream, new AuthMessage(SessionCrypto.CreateProof(secret, password, transcript)), token);
            var accepted = Assert.IsType<PublishAcceptedMessage>(await ControlFraming.ReadAsync(stream, token));

            // Agora o servidor é quem assiste: faz o handshake do host, sem PIN.
            var hello = Assert.IsType<HelloMessage>(await ControlFraming.ReadAsync(stream, token));
            using var hostKey = new KeyExchange();
            var nonce = SessionCrypto.CreateNonce();
            await ControlFraming.WriteAsync(stream, new ChallengeMessage("PC de quem transmite", hostKey.PublicKey, nonce, false), token);
            var auth = Assert.IsType<AuthMessage>(await ControlFraming.ReadAsync(stream, token));
            var hostSecret = hostKey.DeriveSharedSecret(hello.PublicKey);
            var hostTranscript = SessionCrypto.Transcript(hello.PublicKey, hostKey.PublicKey, nonce);
            Assert.True(SessionCrypto.VerifyProof(hostSecret, null, hostTranscript, auth.Proof));
            var key = SessionCrypto.DeriveMediaKey(hostSecret, hostTranscript);
            var salt = RandomNumberGenerator.GetBytes(4);
            var udp = MediaSender.CreateUdp(IPAddress.Loopback, 0);
            var config = new StreamConfigMessage(VideoCodec.H264, 1280, 720, 60, 8000, true, 48000, 2, "teste", "conexão");
            await ControlFraming.WriteAsync(stream, new WelcomeMessage("PC de quem transmite", Convert.ToBase64String(salt), ((IPEndPoint)udp.LocalEndPoint!).Port, config,
                [.. StreamModes.HostFeatures]), token);
            return new FakePublisher(client, stream, udp, new PacketCipher(key, salt), new IPEndPoint(IPAddress.Loopback, hello.UdpPort), accepted.ServerName, hello);
        }

        public void SendMedia(MediaChunk chunk, bool retransmit = false)
            => _sender.Send(_cipher, ref _counter, chunk, _serverMedia.Serialize(), retransmit);

        public void Publish(ControlMessage message) => _ = SendAsync(message, CancellationToken.None);

        public async Task SendAsync(ControlMessage message, CancellationToken token)
        {
            await _write.WaitAsync(token);
            try { await ControlFraming.WriteAsync(_stream, message, token); }
            finally { _write.Release(); }
        }

        public Task<T> WaitForAsync<T>(Func<T, bool> match, CancellationToken token) where T : ControlMessage => _inbox.WaitForAsync(match, Wait, token);

        public int Count<T>(Func<T, bool> match) where T : ControlMessage => _inbox.Count(match);

        private async Task ReadLoopAsync()
        {
            try
            {
                while (!_stop.IsCancellationRequested)
                {
                    var message = await ControlFraming.ReadAsync(_stream, _stop.Token);
                    // Responde o ping do servidor como um host com o relógio 5 s adiantado.
                    if (message is PingMessage ping) _ = SendAsync(new PongMessage(ping.ViewerTimeUs, ping.ViewerTimeUs + 5_000_000), _stop.Token);
                    _inbox.Add(message);
                }
            }
            catch { }
        }

        public async ValueTask DisposeAsync()
        {
            _stop.Cancel();
            _client.Dispose();
            _udp.Dispose();
            try { await _reader; } catch { }
            _cipher.Dispose();
        }
    }

    // ───────────────────────── Quem assiste (no papel do app) ─────────────────────────

    private sealed class FakeViewer : IAsyncDisposable
    {
        private readonly TcpClient _client;
        private readonly NetworkStream _stream;
        private readonly Socket _udp;
        private readonly PacketCipher _cipher;
        private readonly MessageInbox _inbox = new();
        private readonly List<RelayPacket> _packets = [];
        private readonly CancellationTokenSource _stop = new();
        private readonly SemaphoreSlim _write = new(1, 1);
        private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Task _reader;
        private readonly Thread _media;

        private FakeViewer(TcpClient client, NetworkStream stream, Socket udp, PacketCipher cipher, WelcomeMessage welcome, bool sawPending)
        {
            _client = client;
            _stream = stream;
            _udp = udp;
            _cipher = cipher;
            Welcome = welcome;
            SawPending = sawPending;
            _reader = Task.Run(ReadLoopAsync);
            _media = new Thread(MediaLoop) { IsBackground = true };
            _media.Start();
        }

        public WelcomeMessage Welcome { get; }
        public bool SawPending { get; }

        public static async Task<FakeViewer> ConnectAsync(int port, Guid streamId, string password, string name, CancellationToken token, bool acceptPending = false)
        {
            var client = new TcpClient(AddressFamily.InterNetwork) { NoDelay = true };
            await client.ConnectAsync(IPAddress.Loopback, port, token);
            var stream = client.GetStream();
            var udp = MediaSender.CreateUdp(IPAddress.Loopback, 0);
            using var key = new KeyExchange();
            await ControlFraming.WriteAsync(stream, new HelloMessage(AppInfo.ProtocolVersion, AppInfo.DisplayVersion, name, [VideoCodec.H264], 1280, 720, 60,
                ((IPEndPoint)udp.LocalEndPoint!).Port, key.PublicKey, [.. StreamModes.ViewerFeatures], streamId.ToString("N")), token);
            var challenge = Assert.IsType<ChallengeMessage>(await ControlFraming.ReadAsync(stream, token));
            var secret = key.DeriveSharedSecret(challenge.PublicKey);
            var transcript = SessionCrypto.Transcript(key.PublicKey, challenge.PublicKey, challenge.Nonce);
            await ControlFraming.WriteAsync(stream, new AuthMessage(SessionCrypto.CreateProof(secret, password, transcript)), token);
            var pending = false;
            while (true)
            {
                var message = await ControlFraming.ReadAsync(stream, token);
                if (message is RejectedMessage rejected)
                {
                    client.Dispose();
                    udp.Dispose();
                    throw new InvalidOperationException(rejected.Message);
                }
                if (message is PendingApprovalMessage && acceptPending) { pending = true; continue; }
                var welcome = Assert.IsType<WelcomeMessage>(message);
                var cipher = new PacketCipher(SessionCrypto.DeriveMediaKey(secret, transcript), Convert.FromBase64String(welcome.SessionSalt));
                return new FakeViewer(client, stream, udp, cipher, welcome, pending);
            }
        }

        public async Task SendAsync(ControlMessage message, CancellationToken token)
        {
            await _write.WaitAsync(token);
            try { await ControlFraming.WriteAsync(_stream, message, token); }
            finally { _write.Release(); }
        }

        public Task<T> WaitForAsync<T>(Func<T, bool> match, CancellationToken token) where T : ControlMessage => _inbox.WaitForAsync(match, Wait, token);

        public int Count<T>(Func<T, bool> match) where T : ControlMessage => _inbox.Count(match);

        public Task WaitForCloseAsync(CancellationToken token) => _closed.Task.WaitAsync(Wait, token);

        public async Task<List<RelayPacket>> WaitForPacketsAsync(Func<RelayPacket, bool> match, int count, CancellationToken token)
        {
            var deadline = DateTime.UtcNow + Wait;
            while (true)
            {
                lock (_packets)
                {
                    var found = _packets.Where(match).ToList();
                    if (found.Count >= count) return found;
                }
                if (DateTime.UtcNow > deadline) throw new TimeoutException("Os pacotes não chegaram.");
                await Task.Delay(20, token);
            }
        }

        private void MediaLoop()
        {
            var buffer = new byte[16 * 1024];
            var plain = new byte[16 * 1024];
            EndPoint any = new IPEndPoint(IPAddress.Any, 0);
            _udp.ReceiveTimeout = 200;
            while (!_stop.IsCancellationRequested)
            {
                int length;
                try { length = _udp.ReceiveFrom(buffer, ref any); }
                catch (SocketException) { continue; }
                catch (ObjectDisposedException) { break; }
                if (!MediaHeader.TryRead(buffer.AsSpan(0, length), out var header)) continue;
                if (!_cipher.TryDecrypt(header.PacketNumber, buffer.AsSpan(0, MediaHeader.Size), buffer.AsSpan(MediaHeader.Size, length - MediaHeader.Size), plain)) continue;
                lock (_packets) _packets.Add(new RelayPacket(header, plain.AsSpan(0, header.PayloadLength).ToArray()));
            }
        }

        private async Task ReadLoopAsync()
        {
            try
            {
                while (!_stop.IsCancellationRequested) _inbox.Add(await ControlFraming.ReadAsync(_stream, _stop.Token));
            }
            catch { }
            finally { _closed.TrySetResult(); }
        }

        public async ValueTask DisposeAsync()
        {
            _stop.Cancel();
            _client.Dispose();
            _udp.Dispose();
            try { await _reader; } catch { }
            _media.Join(1000);
            _cipher.Dispose();
        }
    }

    /// <summary>Mensagens que chegaram, para os testes esperarem a que interessa.</summary>
    private sealed class MessageInbox
    {
        private readonly List<ControlMessage> _messages = [];

        public void Add(ControlMessage message)
        {
            lock (_messages) _messages.Add(message);
        }

        public int Count<T>(Func<T, bool> match) where T : ControlMessage
        {
            lock (_messages) return _messages.OfType<T>().Count(match);
        }

        public async Task<T> WaitForAsync<T>(Func<T, bool> match, TimeSpan timeout, CancellationToken token) where T : ControlMessage
        {
            var deadline = DateTime.UtcNow + timeout;
            while (true)
            {
                lock (_messages)
                    if (_messages.OfType<T>().FirstOrDefault(match) is { } found) return found;
                if (DateTime.UtcNow > deadline) throw new TimeoutException($"{typeof(T).Name} não chegou.");
                await Task.Delay(20, token);
            }
        }
    }

    // ───────────────────────── Um PC na sala do servidor ─────────────────────────

    private sealed record RoomDatagram(RoomPacketType Type, Guid Sender, byte[] Data, string? Json);

    private sealed class FakeRoomClient : IDisposable
    {
        private readonly Socket _socket = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        private readonly IPEndPoint _server;
        private readonly List<RoomDatagram> _received = [];
        private readonly Thread _thread;
        private volatile bool _stopped;
        private DateTime _lastProbe = DateTime.MinValue;

        public FakeRoomClient(int serverPort, string name)
        {
            Name = name;
            _server = new IPEndPoint(IPAddress.Loopback, serverPort);
            _socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            _socket.ReceiveTimeout = 200;
            _thread = new Thread(Loop) { IsBackground = true };
            _thread.Start();
        }

        public Guid Id { get; } = Guid.NewGuid();
        public string Name { get; }

        public RoomPresence Presence() => new(Name, AppInfo.DisplayVersion, PresenceFlags.Channels);

        public void Send(byte[] packet) => _socket.SendTo(packet, _server);

        public void SendRelay(byte[] inner, IReadOnlyCollection<Guid>? targets = null) => Send(RoomPacket.Relay(Id, inner, targets));

        public async Task<RoomServerInfo> ProbeAsync(string? password, CancellationToken token)
        {
            byte[]? proof = null;
            if (password is not null && Last(p => p.Type == RoomPacketType.ServerInfo) is { } previous
                && RoomPacket.TryReadServerInfo(RoomPacket.Body(previous.Data), out var known))
                proof = RoomPacket.ServerProof(password, known.Nonce, Id);
            if (password is not null && proof is null)
            {
                // Primeiro descobre o desafio, depois manda a prova.
                await ProbeOnceAsync(null, token);
                Assert.True(RoomPacket.TryReadServerInfo(RoomPacket.Body(Last(p => p.Type == RoomPacketType.ServerInfo)!.Data), out var challenge));
                proof = RoomPacket.ServerProof(password, challenge.Nonce, Id);
            }
            return await ProbeOnceAsync(proof, token);
        }

        private async Task<RoomServerInfo> ProbeOnceAsync(byte[]? proof, CancellationToken token)
            => await ProbeOnceAsync(proof, Wait, token) ?? throw new TimeoutException("O servidor não respondeu a sondagem.");

        /// <summary>Uma sondagem sem a senha; null se o servidor não responder dentro de <paramref name="timeout"/>.</summary>
        public Task<RoomServerInfo?> ProbeOrNullAsync(TimeSpan timeout, CancellationToken token) => ProbeOnceAsync(null, timeout, token);

        private async Task<RoomServerInfo?> ProbeOnceAsync(byte[]? proof, TimeSpan timeout, CancellationToken token)
        {
            // O servidor responde no máximo uma sondagem a cada 150 ms por PC (menos para quem acabou de entrar).
            var wait = _lastProbe + TimeSpan.FromMilliseconds(250) - DateTime.UtcNow;
            if (wait > TimeSpan.Zero) await Task.Delay(wait, token);
            var before = Count(p => p.Type == RoomPacketType.ServerInfo);
            _lastProbe = DateTime.UtcNow;
            Send(RoomPacket.ServerProbe(Id, AppInfo.DisplayVersion, proof));
            var deadline = DateTime.UtcNow + timeout;
            while (Count(p => p.Type == RoomPacketType.ServerInfo) <= before)
            {
                if (DateTime.UtcNow > deadline) return null;
                await Task.Delay(20, token);
            }
            var reply = Last(p => p.Type == RoomPacketType.ServerInfo)!;
            Assert.True(RoomPacket.TryReadServerInfo(RoomPacket.Body(reply.Data), out var info));
            return info;
        }

        public async Task<RoomDatagram> WaitForAsync(Func<RoomDatagram, bool> match, CancellationToken token)
        {
            var deadline = DateTime.UtcNow + Wait;
            while (true)
            {
                lock (_received)
                    if (_received.FirstOrDefault(match) is { } found) return found;
                if (DateTime.UtcNow > deadline) throw new TimeoutException("O pacote da sala não chegou.");
                await Task.Delay(20, token);
            }
        }

        private int Count(Func<RoomDatagram, bool> match)
        {
            lock (_received) return _received.Count(match);
        }

        private RoomDatagram? Last(Func<RoomDatagram, bool> match)
        {
            lock (_received) return _received.LastOrDefault(match);
        }

        private void Loop()
        {
            var buffer = new byte[4096];
            EndPoint any = new IPEndPoint(IPAddress.Any, 0);
            while (!_stopped)
            {
                int length;
                try { length = _socket.ReceiveFrom(buffer, ref any); }
                catch (SocketException) { continue; }
                catch (ObjectDisposedException) { break; }
                var data = buffer.AsSpan(0, length).ToArray();
                if (data.Length > 0 && data[0] == (byte)'{')
                {
                    lock (_received) _received.Add(new RoomDatagram(0, Guid.Empty, data, System.Text.Encoding.UTF8.GetString(data)));
                    continue;
                }
                if (!RoomPacket.TryReadHeader(data, out var type, out var sender)) continue;
                lock (_received) _received.Add(new RoomDatagram(type, sender, data, null));
            }
        }

        public void Dispose()
        {
            _stopped = true;
            _socket.Dispose();
            _thread.Join(1000);
        }
    }
}
