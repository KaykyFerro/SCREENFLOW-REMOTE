using System.Security.Cryptography;
using ScreenFlow.Core.Media;
using ScreenFlow.Core.Protocol;
using Xunit;

namespace ScreenFlow.Tests;

public class ProtocolTests
{
    private static byte[] RandomFrame(int size) => RandomNumberGenerator.GetBytes(size);

    [Fact]
    public void Header_RoundTrips()
    {
        var header = new MediaHeader { Type = MediaPacketType.Video, PacketNumber = 1234567890123, FrameId = 42, Index = 3, Count = 9, FecGroupSize = 10, Flags = MediaPacketFlags.Keyframe, TimestampUs = 987654321, PayloadLength = 1150 };
        var buffer = new byte[MediaHeader.Size];
        header.Write(buffer);
        Assert.True(MediaHeader.TryRead(buffer, out var read));
        Assert.Equal(header, read);
    }

    [Fact]
    public void Header_RejectsGarbage()
    {
        Assert.False(MediaHeader.TryRead(new byte[10], out _));
        Assert.False(MediaHeader.TryRead(new byte[MediaHeader.Size], out _));
    }

    [Fact]
    public void Reassembler_RebuildsFrameInAnyOrder()
    {
        var frame = RandomFrame(10_000);
        var chunks = Packetizer.Packetize(MediaPacketType.Video, 7, frame, 100, true);
        var reassembler = new FrameReassembler();
        var ready = new List<AssembledFrame>();
        foreach (var chunk in chunks.OrderByDescending(c => c.Header.Index))
            ready.AddRange(reassembler.Add(chunk.Header, chunk.Payload.Span, TimeSpan.Zero));
        var result = Assert.Single(ready);
        Assert.Equal(frame, result.Data);
        Assert.True(result.Keyframe);
    }

    [Fact]
    public void Fec_RecoversOneLostPacketPerGroup()
    {
        var frame = RandomFrame(25_000); // 22 pacotes, 3 grupos
        var chunks = Packetizer.Packetize(MediaPacketType.Video, 1, frame, 0, false, fecGroupSize: 10);
        var lost = new HashSet<int> { 0, 15, 21 }; // um por grupo, incluindo o último pacote (curto)
        var reassembler = new FrameReassembler();
        var ready = new List<AssembledFrame>();
        foreach (var chunk in chunks.Where(c => !(c.Header.Type == MediaPacketType.Video && lost.Contains(c.Header.Index))))
            ready.AddRange(reassembler.Add(chunk.Header, chunk.Payload.Span, TimeSpan.Zero));
        Assert.Equal(frame, Assert.Single(ready).Data);
        Assert.Equal(3, reassembler.PacketsRecoveredByFec);
    }

    [Fact]
    public void Reassembler_NacksMissingPacketsAndAcceptsRetransmission()
    {
        var frame = RandomFrame(5_000);
        var chunks = Packetizer.Packetize(MediaPacketType.Video, 1, frame, 0, false, fecGroupSize: 0);
        var reassembler = new FrameReassembler(TimeSpan.FromMilliseconds(5), TimeSpan.FromMilliseconds(200));
        foreach (var chunk in chunks.Where(c => c.Header.Index is not (1 or 2)))
            Assert.Empty(reassembler.Add(chunk.Header, chunk.Payload.Span, TimeSpan.Zero));

        var (nacks, _, dropped) = reassembler.Tick(TimeSpan.FromMilliseconds(10));
        Assert.Equal(0, dropped);
        var nack = Assert.Single(nacks);
        Assert.Equal(new ushort[] { 1, 2 }, nack.Missing);

        var cache = new RetransmitCache();
        cache.Store(1, chunks);
        var ready = new List<AssembledFrame>();
        foreach (var chunk in cache.Lookup(nack.FrameId, nack.Missing))
            ready.AddRange(reassembler.Add(chunk.Header, chunk.Payload.Span, TimeSpan.FromMilliseconds(12)));
        Assert.Equal(frame, Assert.Single(ready).Data);
    }

    [Fact]
    public void Reassembler_DropsExpiredFrameAndFlagsGap()
    {
        var first = Packetizer.Packetize(MediaPacketType.Video, 1, RandomFrame(3_000), 0, false, fecGroupSize: 0);
        var second = Packetizer.Packetize(MediaPacketType.Video, 2, RandomFrame(3_000), 0, false, fecGroupSize: 0);
        var reassembler = new FrameReassembler(TimeSpan.FromMilliseconds(5), TimeSpan.FromMilliseconds(50));
        reassembler.Add(first[0].Header, first[0].Payload.Span, TimeSpan.Zero); // frame 1 incompleto
        foreach (var chunk in second) Assert.Empty(reassembler.Add(chunk.Header, chunk.Payload.Span, TimeSpan.FromMilliseconds(1)));

        var (_, ready, dropped) = reassembler.Tick(TimeSpan.FromMilliseconds(60));
        Assert.Equal(1, dropped);
        var frame = Assert.Single(ready);
        Assert.Equal(2u, frame.FrameId);
        Assert.True(frame.AfterGap);
    }

    [Fact]
    public void ControlMessages_RoundTripPolymorphically()
    {
        ControlMessage hello = new HelloMessage(1, "1.0.0", "PC", [VideoCodec.Av1, VideoCodec.H264], 3840, 2160, 165, 50000, "abc");
        var bytes = ControlFraming.Serialize(hello);
        var parsed = ControlFraming.Deserialize(bytes.AsSpan(4));
        var typed = Assert.IsType<HelloMessage>(parsed);
        Assert.Equal(165, typed.MaxFps);
        Assert.Equal([VideoCodec.Av1, VideoCodec.H264], typed.Decoders);
    }

    [Fact]
    public async Task ControlFraming_WorksOverStream()
    {
        using var stream = new MemoryStream();
        await ControlFraming.WriteAsync(stream, new NackMessage(5, [1, 2, 3]), CancellationToken.None);
        await ControlFraming.WriteAsync(stream, new ByeMessage("fim"), CancellationToken.None);
        stream.Position = 0;
        Assert.IsType<NackMessage>(await ControlFraming.ReadAsync(stream, CancellationToken.None));
        Assert.Equal("fim", Assert.IsType<ByeMessage>(await ControlFraming.ReadAsync(stream, CancellationToken.None)).Reason);
    }

    [Fact]
    public void Discovery_RoundTripsAndIgnoresOtherApps()
    {
        var announcement = new DiscoveryAnnouncement("ScreenFlow", 1, "PC", "26.1.2.3", 47801, "1.0.0", true, 2);
        Assert.True(DiscoveryAnnouncement.TryParse(announcement.ToBytes(), out var parsed));
        Assert.Equal(announcement, parsed);
        Assert.False(DiscoveryAnnouncement.TryParse("{\"app\":\"Outro\",\"address\":\"1.1.1.1\"}"u8, out _));
        Assert.False(DiscoveryAnnouncement.TryParse("lixo"u8, out _));
    }

    [Fact]
    public void Bye_CarriesTheResumeHintAndOldByesAreFinal()
    {
        // 1.5.0: a despedida com volta prevista vai e volta pelo JSON.
        var bye = new ByeMessage("Trocando.", ByeMessage.Switching, 45, StreamMode.AudioOnly);
        var read = Assert.IsType<ByeMessage>(ControlFraming.Deserialize(ControlFraming.Serialize(bye).AsSpan(4)));
        Assert.Equal(bye, read);
        Assert.True(read.CanResume);
        Assert.Equal(TimeSpan.FromSeconds(45), read.ResumeWindow);

        // A despedida de uma versão antiga (só o motivo) é de vez.
        var old = Assert.IsType<ByeMessage>(ControlFraming.Deserialize("{\"t\":\"bye\",\"reason\":\"O host encerrou a transmissão.\"}"u8));
        Assert.False(old.CanResume);
        Assert.Null(old.Code);

        // Tirado da transmissão nunca espera, mesmo que venha com tempo; e o tempo é limitado.
        Assert.False(new ByeMessage("Saiu.", ByeMessage.Removed, 60).CanResume);
        Assert.False(new ByeMessage("Servidor desligado.", ByeMessage.ServerStopped, 60).CanResume);
        Assert.Equal(TimeSpan.FromSeconds(ByeMessage.MaxResumeSeconds), new ByeMessage("x", ByeMessage.Ended, 99_999).ResumeWindow);
        Assert.Contains(StreamModes.FeatureFollowHost, StreamModes.ViewerFeatures);
    }
}
