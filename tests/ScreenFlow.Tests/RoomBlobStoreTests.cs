using ScreenFlow.Core.Room;
using ScreenFlow.Core.Settings;
using Xunit;

namespace ScreenFlow.Tests;

public sealed class RoomBlobStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "screenflow-blobs-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); } catch { }
    }

    private static byte[] Blob(int size, int seed = 1)
    {
        var data = new byte[size];
        new Random(seed).NextBytes(data);
        return data;
    }

    [Fact]
    public void PutKeepsTheFileUnderItsHash()
    {
        var store = new RoomBlobStore(_directory);
        var data = Blob(5_000);
        var id = store.Put(data);
        Assert.Equal(RoomBlob.IdOf(data), id);
        Assert.True(store.Has(id));
        Assert.Equal(5_000, store.SizeOf(id));
        Assert.Equal(data, store.Get(id));

        // Outra instância (o ScreenFlow aberto de novo) lê do disco.
        var again = new RoomBlobStore(_directory);
        Assert.True(again.Has(id));
        Assert.Equal(data, again.Get(id));
    }

    [Fact]
    public void SaveRefusesContentThatDoesNotMatchTheId()
    {
        var store = new RoomBlobStore(_directory);
        var data = Blob(3_000);
        var other = Blob(3_000, seed: 2);
        Assert.False(store.Save(RoomBlob.IdOf(other), data));
        Assert.False(store.Has(RoomBlob.IdOf(other)));
        Assert.True(store.Save(RoomBlob.IdOf(data), data));
        Assert.Equal(data, store.Get(RoomBlob.IdOf(data)));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("../../segredo")]
    [InlineData("ABCDEF0123456789ABCDEF0123456789")]
    [InlineData("0123456789abcdef0123456789abcde")]
    public void InvalidIdsNeverTouchTheDisk(string? id)
    {
        var store = new RoomBlobStore(_directory);
        Assert.False(store.Has(id!));
        Assert.Null(store.Get(id!));
        Assert.Null(store.SizeOf(id!));
        Assert.False(store.Save(id!, Blob(10)));
    }

    [Fact]
    public void CorruptedFileIsDroppedInsteadOfSent()
    {
        var store = new RoomBlobStore(_directory);
        var data = Blob(2_000);
        var id = store.Put(data);
        File.WriteAllBytes(Path.Combine(_directory, id + ".blob"), Blob(2_000, seed: 9));

        var fresh = new RoomBlobStore(_directory);
        Assert.Null(fresh.Get(id));
        Assert.False(fresh.Has(id));
    }

    [Fact]
    public void TrimDropsOldFilesButKeepsYourPhoto()
    {
        var store = new RoomBlobStore(_directory);
        var mine = store.Put(Blob(1_000, 1));
        var old = store.Put(Blob(1_000, 2));
        var recent = store.Put(Blob(1_000, 3));
        var longAgo = DateTime.UtcNow - TimeSpan.FromDays(40);
        File.SetLastWriteTimeUtc(Path.Combine(_directory, mine + ".blob"), longAgo);
        File.SetLastWriteTimeUtc(Path.Combine(_directory, old + ".blob"), longAgo);

        Assert.Equal(1, store.Trim([mine, null]));
        Assert.True(store.Has(mine));
        Assert.False(store.Has(old));
        Assert.True(store.Has(recent));
    }

    [Fact]
    public void MemoryCacheStaysBounded()
    {
        var store = new RoomBlobStore(_directory);
        var ids = new List<string>();
        for (var i = 0; i < 14; i++) ids.Add(store.Put(Blob(RoomBlob.MaxImageBytes, seed: i)));
        // Tudo continua no disco, mesmo o que saiu da memória.
        foreach (var id in ids) Assert.Equal(RoomBlob.MaxImageBytes, store.Get(id)?.Length);
    }

    [Fact]
    public void ProfileSettingsAreNormalized()
    {
        var settings = new AppSettings { DeviceName = "   Kayky   ", AvatarId = "nada-a-ver" };
        settings.Normalize();
        Assert.Equal("Kayky", settings.DeviceName);
        Assert.Null(settings.AvatarId);

        var id = RoomBlob.IdOf(Blob(100));
        settings.AvatarId = id;
        settings.DeviceName = new string('a', 60);
        settings.Normalize();
        Assert.Equal(id, settings.AvatarId);
        Assert.Equal(RoomPacket.MaxNameLength, settings.DeviceName.Length);

        settings.DeviceName = "   ";
        settings.Normalize();
        Assert.Equal(Environment.MachineName, settings.DeviceName);
    }
}
