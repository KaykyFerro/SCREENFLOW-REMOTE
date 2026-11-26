using System.Buffers.Binary;
using ScreenFlow.Core.Room;
using Xunit;

namespace ScreenFlow.Tests;

public class RoomBlobTests
{
    private static readonly Guid Me = Guid.Parse("8a3f0c1e-2b4d-4e6f-9a1b-3c5d7e9f1a2b");
    private static readonly Guid Ana = Guid.Parse("11111111-2222-3333-4444-555555555555");
    private static readonly Guid Bruno = Guid.Parse("99999999-8888-7777-6666-555555555555");
    private static readonly DateTimeOffset T0 = new(2026, 11, 26, 20, 0, 0, TimeSpan.Zero);

    private static byte[] Blob(int size, int seed = 1)
    {
        var data = new byte[size];
        new Random(seed).NextBytes(data);
        return data;
    }

    [Fact]
    public void PresenceCarriesTheAvatarAfterTheOldFields()
    {
        var avatar = Blob(9_000);
        var presence = new RoomPresence("Kayky", "1.1.1", PresenceFlags.InVoice, Avatar: new RoomBlobRef(RoomBlob.IdOf(avatar), avatar.Length));
        var packet = RoomPacket.Presence(Me, presence);
        Assert.True(RoomPacket.TryReadPresence(RoomPacket.Body(packet), out var read));
        Assert.Equal(presence, read);

        // O começo do pacote é igual ao de antes (as versões antigas leem até a versão e ignoram o resto).
        var old = RoomPacket.Presence(Me, presence with { Avatar = null });
        Assert.Equal(old, packet[..old.Length]);
        Assert.Equal(old.Length + 2 + 16 + 4, packet.Length);
    }

    [Fact]
    public void PresenceSkipsUnknownAndBrokenFields()
    {
        var packet = RoomPacket.Presence(Me, new RoomPresence("Kayky", "1.1.1", PresenceFlags.None)).ToList();
        packet.AddRange([42, 3, 1, 2, 3]);      // campo de uma versão futura
        packet.AddRange([1, 20, 9, 9]);         // foto cortada no meio
        Assert.True(RoomPacket.TryReadPresence(RoomPacket.Body(packet.ToArray()), out var read));
        Assert.Equal("Kayky", read.Name);
        Assert.Null(read.Avatar);
    }

    [Fact]
    public void ChatImageRoundTripsAndOldVersionsSeeThePlaceholder()
    {
        var image = new ChatImage(RoomBlob.IdOf(Blob(300_000)), 300_000, 1600, 900);
        var message = new RoomChatMessage(Me, "Kayky", 7, T0, "", Image: image);
        var packet = RoomPacket.Chat(Me, message);
        Assert.True(RoomPacket.TryReadChat(RoomPacket.Body(packet), out var read));
        Assert.Equal(image, read.Image);
        Assert.Equal(RoomPacket.ImagePlaceholder, read.Text);
        Assert.Equal(string.Empty, read.Caption);

        // Sem o bit da imagem (como uma versão antiga lê): só o texto, e o pacote continua válido.
        var body = RoomPacket.Body(packet).ToArray();
        body[32] &= 0xFD;
        Assert.True(RoomPacket.TryReadChat(body, out var legacy));
        Assert.Null(legacy.Image);
        Assert.Equal(RoomPacket.ImagePlaceholder, legacy.Text);
    }

    [Fact]
    public void ChatImageKeepsTheCaptionAndTheHistoryFlag()
    {
        var image = new ChatImage(RoomBlob.IdOf(Blob(1000)), 1000, 64, 64);
        var packet = RoomPacket.Chat(Me, new RoomChatMessage(Me, "Kayky", 8, T0, "olha isso", History: true, Image: image));
        Assert.True(RoomPacket.TryReadChat(RoomPacket.Body(packet), out var read));
        Assert.True(read.History);
        Assert.Equal("olha isso", read.Caption);
        Assert.Equal(image, read.Image);
    }

    [Theory]
    [InlineData(0, 10, 10)]
    [InlineData(RoomBlob.MaxImageBytes + 1, 10, 10)]
    [InlineData(1000, 0, 10)]
    [InlineData(1000, 10, RoomBlob.MaxImageSide + 1)]
    public void InvalidImagesAreNotSent(int size, int width, int height)
    {
        var packet = RoomPacket.Chat(Me, new RoomChatMessage(Me, "Kayky", 9, T0, "oi", Image: new ChatImage(new string('a', 32), size, width, height)));
        Assert.True(RoomPacket.TryReadChat(RoomPacket.Body(packet), out var read));
        Assert.Null(read.Image);
        Assert.Equal("oi", read.Text);
    }

    [Fact]
    public void BlobRequestAndChunksRoundTrip()
    {
        var data = Blob(5_000);
        var id = RoomBlob.IdOf(data);
        var request = RoomPacket.BlobRequest(Me, id, [1, 3]);
        Assert.True(RoomPacket.TryReadHeader(request, out var type, out var sender));
        Assert.Equal((RoomPacketType.BlobRequest, Me), (type, sender));
        Assert.True(RoomPacket.TryReadBlobRequest(RoomPacket.Body(request), out var askedId, out var missing));
        Assert.Equal(id, askedId);
        Assert.Equal([1, 3], missing);

        RoomBlob.TryParseId(id, out var hash);
        var buffer = new byte[RoomPacket.MaximumSize];
        for (var index = 0; index < RoomBlob.ChunkCount(data.Length); index++)
        {
            var length = RoomPacket.WriteBlobChunk(buffer, Me, hash, data, index);
            Assert.True(length <= RoomPacket.MaximumSize);
            Assert.True(RoomPacket.TryReadHeader(buffer.AsSpan(0, length), out var chunkType, out _));
            Assert.Equal(RoomPacketType.BlobChunk, chunkType);
            Assert.True(RoomPacket.TryReadBlobChunk(RoomPacket.Body(buffer.AsSpan(0, length)), out var chunkId, out var size, out var at, out var chunk));
            Assert.Equal((id, data.Length, index), (chunkId, size, at));
            Assert.Equal(data.AsSpan(index * RoomBlob.ChunkBytes, RoomBlob.ChunkLength(data.Length, index)).ToArray(), chunk.ToArray());
        }
    }

    [Fact]
    public void ChunksWithTheWrongShapeAreRejected()
    {
        var data = Blob(2_500);
        RoomBlob.TryParseId(RoomBlob.IdOf(data), out var hash);
        var buffer = new byte[RoomPacket.MaximumSize];
        var length = RoomPacket.WriteBlobChunk(buffer, Me, hash, data, 2);
        // Pedaço cortado, contagem errada e tamanho acima do limite.
        Assert.False(RoomPacket.TryReadBlobChunk(RoomPacket.Body(buffer.AsSpan(0, length - 1)), out _, out _, out _, out _));
        var body = RoomPacket.Body(buffer.AsSpan(0, length)).ToArray();
        BinaryPrimitives.WriteUInt16BigEndian(body.AsSpan(22), 9);
        Assert.False(RoomPacket.TryReadBlobChunk(body, out _, out _, out _, out _));
        body = RoomPacket.Body(buffer.AsSpan(0, length)).ToArray();
        BinaryPrimitives.WriteInt32BigEndian(body.AsSpan(16), RoomBlob.MaxImageBytes + 1);
        Assert.False(RoomPacket.TryReadBlobChunk(body, out _, out _, out _, out _));
    }

    [Theory]
    [InlineData("0123456789abcdef0123456789abcdef", true)]
    [InlineData("0123456789ABCDEF0123456789ABCDEF", false)]
    [InlineData("../../../../windows/system32/x", false)]
    [InlineData("0123456789abcdef0123456789abcde", false)]
    [InlineData(null, false)]
    public void OnlyHexIdsAreValid(string? id, bool valid) => Assert.Equal(valid, RoomBlob.IsValidId(id));

    /// <summary>Quem tem o arquivo, de mentira: responde aos pedidos com os pedaços, perdendo alguns no caminho.</summary>
    private static void Serve(RoomBlobDownloads downloads, Guid from, byte[] data, BlobAsk ask, DateTimeOffset now, Func<int, bool> lost, ref byte[]? result)
    {
        var count = RoomBlob.ChunkCount(data.Length);
        var indexes = ask.Missing.Length == 0 ? Enumerable.Range(0, count) : ask.Missing;
        foreach (var index in indexes)
        {
            if (lost(index)) continue;
            var chunk = data.AsSpan(index * RoomBlob.ChunkBytes, RoomBlob.ChunkLength(data.Length, index));
            result ??= downloads.Accept(ask.Id, from, data.Length, index, chunk, now);
        }
    }

    [Fact]
    public void LostChunksAreAskedAgainUntilTheFileIsComplete()
    {
        var data = Blob(20_000);
        var id = RoomBlob.IdOf(data);
        var downloads = new RoomBlobDownloads();
        Assert.True(downloads.Start(id, data.Length, [Ana], T0));

        var (asks, failed) = downloads.Due(T0);
        Assert.Empty(failed);
        var ask = Assert.Single(asks);
        Assert.Equal(Ana, ask.Source);
        Assert.Empty(ask.Missing); // primeiro pedido: tudo
        byte[]? result = null;
        Serve(downloads, Ana, data, ask, T0, i => i is 2 or 7 or 16, ref result);
        Assert.Null(result);

        // Enquanto está chegando, não pede de novo; depois do silêncio, pede só o que faltou.
        Assert.Empty(downloads.Due(T0 + TimeSpan.FromMilliseconds(100)).Asks);
        ask = Assert.Single(downloads.Due(T0 + RoomBlobDownloads.Quiet).Asks);
        Assert.Equal([2, 7, 16], ask.Missing);
        Serve(downloads, Ana, data, ask, T0 + RoomBlobDownloads.Quiet, _ => false, ref result);
        Assert.Equal(data, result);
        Assert.Equal(0, downloads.Count);
    }

    [Fact]
    public void ChunksFromSomeoneElseOrWithTheWrongSizeAreIgnored()
    {
        var data = Blob(1_000);
        var id = RoomBlob.IdOf(data);
        var downloads = new RoomBlobDownloads();
        downloads.Start(id, data.Length, [Ana], T0);
        Assert.Null(downloads.Accept(id, Bruno, data.Length, 0, data, T0));
        Assert.Null(downloads.Accept(id, Ana, data.Length + 1, 0, data, T0));
        Assert.Equal(data, downloads.Accept(id, Ana, data.Length, 0, data, T0));
    }

    [Fact]
    public void WrongContentMovesToTheNextSource()
    {
        var data = Blob(1_000);
        var id = RoomBlob.IdOf(data);
        var downloads = new RoomBlobDownloads();
        downloads.Start(id, data.Length, [Ana, Bruno], T0);
        downloads.Due(T0);
        Assert.Null(downloads.Accept(id, Ana, data.Length, 0, Blob(1_000, seed: 2), T0));
        var ask = Assert.Single(downloads.Due(T0).Asks);
        Assert.Equal(Bruno, ask.Source);
        Assert.Equal(data, downloads.Accept(id, Bruno, data.Length, 0, data, T0));
    }

    [Fact]
    public void GivesUpWhenNobodyAnswers()
    {
        var data = Blob(1_000);
        var id = RoomBlob.IdOf(data);
        var downloads = new RoomBlobDownloads();
        downloads.Start(id, data.Length, [Ana, Bruno], T0);
        var now = T0;
        var sources = new List<Guid>();
        for (var i = 0; i < 40 && downloads.Count > 0; i++)
        {
            var (asks, failed) = downloads.Due(now);
            sources.AddRange(asks.Select(a => a.Source));
            if (failed.Contains(id)) break;
            now += TimeSpan.FromSeconds(2);
        }
        Assert.Equal(0, downloads.Count);
        Assert.Equal(RoomBlobDownloads.MaxAttemptsPerSource, sources.Count(s => s == Ana));
        Assert.Equal(RoomBlobDownloads.MaxAttemptsPerSource, sources.Count(s => s == Bruno));
    }

    [Fact]
    public void ASourceThatLeftIsSkippedRightAway()
    {
        var data = Blob(1_000);
        var id = RoomBlob.IdOf(data);
        var downloads = new RoomBlobDownloads();
        downloads.Start(id, data.Length, [Ana, Bruno], T0);
        downloads.Due(T0);
        downloads.Forget(Ana, T0);
        Assert.Equal(Bruno, Assert.Single(downloads.Due(T0).Asks).Source);
    }

    [Fact]
    public void StartRejectsBadRequestsAndMergesSources()
    {
        var downloads = new RoomBlobDownloads();
        Assert.False(downloads.Start("nao-e-um-id", 100, [Ana], T0));
        Assert.False(downloads.Start(new string('a', 32), 0, [Ana], T0));
        Assert.False(downloads.Start(new string('a', 32), 100, [], T0));
        Assert.True(downloads.Start(new string('a', 32), 100, [Ana], T0));
        Assert.False(downloads.Start(new string('a', 32), 100, [Bruno], T0));
        Assert.Equal(1, downloads.Count);
    }
}
