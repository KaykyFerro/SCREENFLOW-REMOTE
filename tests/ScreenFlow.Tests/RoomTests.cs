using System.Net;
using ScreenFlow.Core.Media;
using ScreenFlow.Core.Room;
using Xunit;

namespace ScreenFlow.Tests;

public class RoomProtocolTests
{
    private static readonly Guid Me = Guid.Parse("8a3f0c1e-2b4d-4e6f-9a1b-3c5d7e9f1a2b");
    private static readonly Guid Other = Guid.Parse("11111111-2222-3333-4444-555555555555");

    [Fact]
    public void Presence_RoundTrips()
    {
        var presence = new RoomPresence("PC da Mari", "1.1.0", PresenceFlags.InVoice | PresenceFlags.Live | PresenceFlags.LiveAudio | PresenceFlags.LiveCamera,
            StreamMode.Screen, 3, 47801, IPAddress.Parse("26.48.121.7"), 42);
        var packet = RoomPacket.Presence(Me, presence);
        Assert.True(RoomPacket.TryReadHeader(packet, out var type, out var sender));
        Assert.Equal(RoomPacketType.Presence, type);
        Assert.Equal(Me, sender);
        Assert.True(RoomPacket.TryReadPresence(RoomPacket.Body(packet), out var read));
        Assert.Equal(presence, read);
        Assert.Equal("tela, câmera e áudio", read.LiveSummary);
    }

    [Fact]
    public void Presence_WithoutLiveAddress_ReadsNull()
    {
        var packet = RoomPacket.Presence(Me, new RoomPresence("Kayky", "1.1.0", PresenceFlags.None));
        Assert.True(RoomPacket.TryReadPresence(RoomPacket.Body(packet), out var read));
        Assert.Null(read.LiveAddress);
        Assert.False(read.Live);
    }

    [Fact]
    public void Chat_RoundTripsWithAccentsAndEmoji()
    {
        var message = new RoomChatMessage(Me, "João", 0xDEADBEEFCAFEUL, DateTimeOffset.FromUnixTimeMilliseconds(1_764_000_000_000), "Bora jogar? 🎮\nÀs 21h!");
        var packet = RoomPacket.Chat(Me, message);
        Assert.True(packet.Length <= RoomPacket.MaximumSize);
        Assert.True(RoomPacket.TryReadHeader(packet, out var type, out _));
        Assert.Equal(RoomPacketType.Chat, type);
        Assert.True(RoomPacket.TryReadChat(RoomPacket.Body(packet), out var read));
        Assert.Equal(message, read);
    }

    [Fact]
    public void Chat_LongTextIsCappedAndFits()
    {
        var text = string.Concat(Enumerable.Repeat("ação 🎉 ", 400));
        var packet = RoomPacket.Chat(Me, new RoomChatMessage(Me, new string('x', 80), 1, DateTimeOffset.UtcNow, text));
        Assert.True(packet.Length <= RoomPacket.MaximumSize);
        Assert.True(RoomPacket.TryReadChat(RoomPacket.Body(packet), out var read));
        Assert.True(read.Text.Length <= RoomPacket.MaxTextLength);
        Assert.Equal(RoomPacket.MaxNameLength, read.AuthorName.Length);
        Assert.False(char.IsHighSurrogate(read.Text[^1]));
    }

    [Fact]
    public void Voice_RoundTripsWithPreviousFrame()
    {
        var current = Enumerable.Range(0, 90).Select(i => (byte)i).ToArray();
        var previous = Enumerable.Range(0, 70).Select(i => (byte)(255 - i)).ToArray();
        var buffer = new byte[RoomPacket.MaximumSize];
        var length = RoomPacket.WriteVoice(buffer, Me, 7, 200, current, previous);
        Assert.True(RoomPacket.TryReadHeader(buffer.AsSpan(0, length), out var type, out _));
        Assert.Equal(RoomPacketType.Voice, type);
        Assert.True(RoomPacket.TryReadVoice(RoomPacket.Body(buffer.AsSpan(0, length)), out var sequence, out var level, out var c, out var p));
        Assert.Equal(7u, sequence);
        Assert.Equal(200, level);
        Assert.Equal(current, c.ToArray());
        Assert.Equal(previous, p.ToArray());
    }

    [Fact]
    public void Ack_RoundTrips()
    {
        var packet = RoomPacket.ChatAck(Other, Me, 99);
        Assert.True(RoomPacket.TryReadHeader(packet, out var type, out var sender));
        Assert.Equal((RoomPacketType.ChatAck, Other), (type, sender));
        Assert.True(RoomPacket.TryReadChatAck(RoomPacket.Body(packet), out var author, out var id));
        Assert.Equal((Me, 99UL), (author, id));
    }

    [Theory]
    [InlineData("{\"app\":\"ScreenFlow\"}")]
    [InlineData("SR")]
    [InlineData("")]
    public void Header_RejectsOtherDatagrams(string text)
    {
        Assert.False(RoomPacket.TryReadHeader(System.Text.Encoding.UTF8.GetBytes(text), out _, out _));
    }

    [Fact]
    public void Header_RejectsTruncatedBodies()
    {
        var packet = RoomPacket.Chat(Me, new RoomChatMessage(Me, "Ana", 5, DateTimeOffset.UtcNow, "oi"));
        for (var cut = RoomPacket.HeaderSize; cut < packet.Length; cut++)
            Assert.False(RoomPacket.TryReadChat(RoomPacket.Body(packet.AsSpan(0, cut)), out _));
    }

    [Theory]
    [InlineData("  Mari ‮ gamer  ", "Mari gamer")]
    [InlineData("\u0007\u0000", "")]
    [InlineData("PC\tda\nSala", "PC da Sala")]
    public void CleanName_RemovesControlCharacters(string input, string expected) => Assert.Equal(expected, RoomPacket.CleanName(input));

    [Fact]
    public void CleanText_KeepsAtMostTwoLineBreaks()
    {
        Assert.Equal("a\n\nb", RoomPacket.CleanText("a\r\n\r\n\r\n\r\nb"));
        Assert.Equal("oi", RoomPacket.CleanText("  oi \u0007 "));
        Assert.Equal(string.Empty, RoomPacket.CleanText(" \n \t "));
    }
}

public class RoomStateTests
{
    private static readonly IPEndPoint Lan = new(IPAddress.Parse("192.168.0.20"), 47802);
    private static readonly IPEndPoint Radmin = new(IPAddress.Parse("26.10.20.30"), 47802);

    [Fact]
    public void Peers_JoinUpdateAndExpire()
    {
        var table = new RoomPeerTable(TimeSpan.FromSeconds(7));
        var id = Guid.NewGuid();
        var t0 = DateTimeOffset.UtcNow;
        var first = table.Update(id, new RoomPresence("Ana", "1.1.0", PresenceFlags.None), Radmin, t0);
        Assert.True(first.IsNew);
        var second = table.Update(id, new RoomPresence("Ana", "1.1.0", PresenceFlags.InVoice), Radmin, t0.AddSeconds(2));
        Assert.False(second.IsNew);
        Assert.False(second.Previous!.InVoice);
        Assert.True(table.Find(id)!.Presence.InVoice);
        Assert.Empty(table.Expire(t0.AddSeconds(8)));
        Assert.Single(table.Expire(t0.AddSeconds(10)));
        Assert.Equal(0, table.Count);
    }

    [Fact]
    public void Peers_KeepFirstPathWhileItAnswers()
    {
        var table = new RoomPeerTable();
        var id = Guid.NewGuid();
        var t0 = DateTimeOffset.UtcNow;
        var presence = new RoomPresence("Ana", "1.1.0", PresenceFlags.None);
        table.Update(id, presence, Radmin, t0);
        table.Update(id, presence, Lan, t0.AddSeconds(1));
        Assert.Equal(Radmin, table.Find(id)!.EndPoint);
        table.Update(id, presence, Lan, t0.AddSeconds(7)); // o Radmin parou de responder
        Assert.Equal(Lan, table.Find(id)!.EndPoint);
    }

    [Fact]
    public void Peers_LimitHowManyAreKept()
    {
        var table = new RoomPeerTable();
        for (var i = 0; i < RoomPeerTable.MaxPeers + 10; i++)
            table.Update(Guid.NewGuid(), new RoomPresence($"PC {i}", "1.1.0", PresenceFlags.None), Radmin, DateTimeOffset.UtcNow);
        Assert.Equal(RoomPeerTable.MaxPeers, table.Count);
    }

    [Fact]
    public void Outbox_RetriesUntilAcknowledged()
    {
        var outbox = new ChatOutbox();
        var me = Guid.NewGuid();
        var (a, b) = (Guid.NewGuid(), Guid.NewGuid());
        var message = new RoomChatMessage(me, "Eu", 1, DateTimeOffset.UtcNow, "oi");
        var t0 = DateTimeOffset.UtcNow;
        outbox.Add(message, [1, 2, 3], [a, b], t0);
        Assert.Empty(outbox.Due(t0));
        Assert.Equal(2, outbox.Due(t0 + ChatOutbox.RetryInterval).Count);
        outbox.Acknowledge(a, me, 1);
        var due = outbox.Due(t0 + ChatOutbox.RetryInterval * 2);
        Assert.Single(due);
        Assert.Equal(b, due[0].Peer);
        outbox.Acknowledge(b, me, 1);
        Assert.Equal(0, outbox.Count);
    }

    [Fact]
    public void Outbox_GivesUpAfterMaxAttempts()
    {
        var outbox = new ChatOutbox();
        var t = DateTimeOffset.UtcNow;
        outbox.Add(new RoomChatMessage(Guid.NewGuid(), "Eu", 1, t, "oi"), [1], [Guid.NewGuid()], t);
        var sends = 0;
        for (var i = 1; i <= ChatOutbox.MaxAttempts + 2; i++) sends += outbox.Due(t + ChatOutbox.RetryInterval * i).Count;
        Assert.Equal(ChatOutbox.MaxAttempts - 1, sends);
        Assert.Equal(0, outbox.Count);
    }

    [Fact]
    public void History_SortsByTimeAndDropsDuplicates()
    {
        var history = new ChatHistory();
        var author = Guid.NewGuid();
        var t = DateTimeOffset.UtcNow;
        Assert.Equal(0, history.Add(new RoomChatMessage(author, "A", 2, t.AddSeconds(10), "segunda")));
        Assert.Equal(0, history.Add(new RoomChatMessage(author, "A", 1, t, "primeira")));
        Assert.Equal(-1, history.Add(new RoomChatMessage(author, "A", 1, t, "primeira")));
        Assert.Equal(["primeira", "segunda"], history.All().Select(m => m.Text));
    }

    [Fact]
    public void History_KeepsOnlyTheNewest()
    {
        var history = new ChatHistory();
        var author = Guid.NewGuid();
        var t = DateTimeOffset.UtcNow;
        for (var i = 0; i < ChatHistory.Capacity + 25; i++) history.Add(new RoomChatMessage(author, "A", (ulong)i, t.AddSeconds(i), $"m{i}"));
        Assert.Equal(ChatHistory.Capacity, history.Count);
        Assert.Equal("m25", history.All()[0].Text);
        Assert.False(history.Contains(author, 0));
    }

    [Fact]
    public void RateLimiter_AllowsBurstThenRefills()
    {
        var limiter = new RateLimiter<string>(burst: 3, perSecond: 1);
        var t = DateTimeOffset.UtcNow;
        Assert.True(limiter.TryTake("a", t));
        Assert.True(limiter.TryTake("a", t));
        Assert.True(limiter.TryTake("a", t));
        Assert.False(limiter.TryTake("a", t));
        Assert.True(limiter.TryTake("b", t));
        Assert.True(limiter.TryTake("a", t.AddSeconds(1.1)));
    }

    [Fact]
    public void RecentIds_ForgetsOldest()
    {
        var ids = new RecentIds<int>(3);
        Assert.True(ids.Add(1));
        Assert.False(ids.Add(1));
        ids.Add(2);
        ids.Add(3);
        ids.Add(4);
        Assert.False(ids.Contains(1));
        Assert.True(ids.Add(1));
    }

    [Fact]
    public void Speaking_NeedsRecentLoudVoiceAndMicOn()
    {
        var table = new RoomPeerTable();
        var id = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var peer = table.Update(id, new RoomPresence("Ana", "1.1.0", PresenceFlags.InVoice), Radmin, now).Peer!;
        Assert.False(peer.IsSpeaking(now));
        peer.RecordVoice(180, now);
        Assert.True(peer.IsSpeaking(now.AddMilliseconds(100)));
        Assert.False(peer.IsSpeaking(now.AddSeconds(1)));
        table.Update(id, new RoomPresence("Ana", "1.1.0", PresenceFlags.InVoice | PresenceFlags.Muted), Radmin, now);
        Assert.False(peer.IsSpeaking(now.AddMilliseconds(100)));
    }
}

public class VoiceGateTests
{
    private static float[] Tone(double amplitude) =>
        Enumerable.Range(0, 960).Select(i => (float)(amplitude * Math.Sin(2 * Math.PI * 220 * i / 48000.0))).ToArray();

    [Fact]
    public void Gate_OpensOnVoiceAndHoldsThenCloses()
    {
        var gate = new VoiceGate { Sensitivity = 0.6 };
        Assert.False(gate.Process(Tone(0.0005)));
        Assert.True(gate.Process(Tone(0.2)));
        for (var i = 0; i < VoiceGate.HangoverFrames; i++) Assert.True(gate.Process(Tone(0.0005)));
        Assert.False(gate.Process(Tone(0.0005)));
    }

    [Fact]
    public void Gate_SensitivityMovesThreshold()
    {
        var quiet = Tone(0.01); // ~ -43 dBFS
        Assert.False(new VoiceGate { Sensitivity = 0.2 }.Process(quiet));
        Assert.True(new VoiceGate { Sensitivity = 0.9 }.Process(quiet));
    }

    [Fact]
    public void Gate_LevelByteTracksVolume()
    {
        var gate = new VoiceGate();
        gate.Process(Tone(0.5));
        var loud = gate.LevelByte;
        gate.Process(Tone(0.01));
        Assert.True(loud > gate.LevelByte);
        gate.Process(new float[960]);
        Assert.Equal(0, gate.LevelByte);
    }

    [Theory]
    [InlineData(StreamMode.Screen, true, true, "tela, câmera e áudio")]
    [InlineData(StreamMode.Screen, true, false, "tela e áudio")]
    [InlineData(StreamMode.Screen, false, false, "só a tela")]
    [InlineData(StreamMode.Camera, true, false, "câmera e áudio")]
    [InlineData(StreamMode.Camera, false, false, "só a câmera")]
    [InlineData(StreamMode.AudioOnly, true, false, "só o áudio (festa de música)")]
    public void Summary_SaysWhatIsLive(StreamMode mode, bool audio, bool camera, string expected) => Assert.Equal(expected, StreamModes.Summary(mode, audio, camera));
}
