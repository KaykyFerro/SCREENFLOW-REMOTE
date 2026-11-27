using System.Net;
using System.Text;
using ScreenFlow.Core.Room;
using ScreenFlow.Core.Security;
using Xunit;

namespace ScreenFlow.Tests;

/// <summary>(1.5.0) Servidores por rede do Radmin: a chave da rede, o convite, os canais guardados e os pacotes novos.</summary>
[Collection("Segredos")]
public class RoomSpaceTests
{
    private sealed class TestProtector : ISecretProtector
    {
        public string Scheme => "teste";
        public byte[] Protect(byte[] data) => data.Reverse().Select(b => (byte)(b ^ 0x5A)).ToArray();
        public byte[] Unprotect(byte[] data) => data.Select(b => (byte)(b ^ 0x5A)).Reverse().ToArray();
    }

    private static readonly DateTimeOffset Now = new(2026, 11, 27, 20, 0, 0, TimeSpan.Zero);

    [Fact]
    public void TheSameRadminNetworkGivesTheSameKeyAndOthersDoNot()
    {
        var a = SpaceKeys.FromNetwork("Amigos do CS", "senha123");
        var b = SpaceKeys.FromNetwork("  amigos   do cs ", " senha123 ");
        Assert.Equal(a, b);
        Assert.Equal(32, a.Length);
        Assert.NotEqual(a, SpaceKeys.FromNetwork("Amigos do CS", "Senha123"));
        Assert.NotEqual(a, SpaceKeys.FromNetwork("Amigos do CS 2", "senha123"));
        Assert.Equal(SpaceKeys.TagText(a), SpaceKeys.TagText(b));
        Assert.Throws<ArgumentException>(() => SpaceKeys.FromNetwork("   ", "x"));
        Assert.Equal("Amigos do CS", SpaceKeys.CleanNetworkName("  Amigos \t do​ CS "));
    }

    [Fact]
    public void ServerInviteRoundTripsAndRejectsTypos()
    {
        var invite = SpaceInvite.Create("Família 🎮");
        var code = invite.ToCode();
        Assert.StartsWith("SF2-", code);
        Assert.True(SpaceInvite.TryParse("sf2-" + code[4..].ToLowerInvariant().Replace("-", " "), out var back));
        Assert.Equal(invite.Secret, back.Secret);
        Assert.Equal("Família 🎮", back.Name);

        var broken = code[..^1] + (code[^1] == '0' ? '1' : '0');
        Assert.False(SpaceInvite.TryParse(broken, out _));
        Assert.False(SpaceInvite.TryParse("SF1-" + code[4..], out _));
        // Um convite de sala privada não vira convite de servidor (nem o contrário).
        Assert.False(RoomInvite.TryParse(code, out _));
        Assert.False(SpaceInvite.TryParse(RoomInvite.Create(Guid.NewGuid()).ToCode(), out _));
    }

    [Fact]
    public void ChannelsAreCreatedRenamedAndDeletedByAnyone()
    {
        var table = new SpaceChannelTable();
        var ana = Guid.NewGuid();
        var created = table.Create(RoomChannelKind.Text, "Pedidos de Música", ana, Now, out var problem);
        Assert.Null(problem);
        Assert.NotNull(created);
        Assert.Equal("pedidos-de-música", created!.Name);
        Assert.True(SpaceChannelTable.IsValidId(created.Id));
        Assert.Null(table.Create(RoomChannelKind.Text, "pedidos de musica", ana, Now, out problem));
        Assert.Equal("Já existe um canal com esse nome.", problem);
        // O mesmo nome em voz é outro canal.
        Assert.NotNull(table.Create(RoomChannelKind.Voice, "Pedidos de Música", ana, Now, out _));

        var renamed = table.Rename(RoomChannelKind.Text, created.Id, "música", Guid.NewGuid(), Now.AddSeconds(1), out problem);
        Assert.Null(problem);
        Assert.Equal(created.Id, renamed!.Id);
        Assert.Equal("música", table.NameOf(RoomChannelKind.Text, created.Id));

        // O "geral" pode mudar de nome, mas nunca ser apagado.
        Assert.NotNull(table.Rename(RoomChannelKind.Text, "geral", "bate-papo", ana, Now, out _));
        Assert.Equal("bate-papo", table.NameOf(RoomChannelKind.Text, null));
        Assert.Null(table.Delete(RoomChannelKind.Text, "geral", ana, Now, out problem));
        Assert.NotNull(problem);

        Assert.NotNull(table.Delete(RoomChannelKind.Text, created.Id, ana, Now.AddSeconds(2), out _));
        Assert.Null(table.NameOf(RoomChannelKind.Text, created.Id));
        Assert.DoesNotContain(table.Visible(RoomChannelKind.Text), c => c.Id == created.Id);
        Assert.Equal("geral", table.Visible(RoomChannelKind.Text)[0].Id);
    }

    [Fact]
    public void TheNewestChangeWinsOnEveryPc()
    {
        var a = new SpaceChannelTable();
        var b = new SpaceChannelTable();
        var created = a.Create(RoomChannelKind.Voice, "Valorant", Guid.NewGuid(), Now, out _)!;
        Assert.True(b.Merge(created, Now));
        Assert.False(b.Merge(created, Now)); // repetido não muda nada

        var renamedOnA = a.Rename(RoomChannelKind.Voice, created.Id, "Valorant 5x5", Guid.NewGuid(), Now.AddSeconds(5), out _)!;
        var deletedOnB = b.Delete(RoomChannelKind.Voice, created.Id, Guid.NewGuid(), Now.AddSeconds(9), out _)!;
        // Cada um recebe a mudança do outro: os dois ficam com a mais nova (apagado).
        Assert.True(a.Merge(deletedOnB, Now.AddSeconds(10)));
        Assert.False(b.Merge(renamedOnA, Now.AddSeconds(10)));
        Assert.Null(a.NameOf(RoomChannelKind.Voice, created.Id));
        Assert.Null(b.NameOf(RoomChannelKind.Voice, created.Id));

        // Hora no futuro demais é recusada (senão ninguém mudaria o canal depois).
        var future = created with { Name = "dono", Version = Now.AddHours(2).ToUnixTimeMilliseconds() };
        Assert.False(a.Merge(future, Now));
        // Lixo não entra.
        Assert.False(a.Merge(new SpaceChannel("../../x", RoomChannelKind.Text, "x", 1, Guid.Empty), Now));
        Assert.False(a.Merge(new SpaceChannel("geral", RoomChannelKind.Text, "x", 5, Guid.Empty, Deleted: true), Now));
    }

    [Fact]
    public void DeletedChannelsAreForgottenAfterAWhileAndTheListHasLimits()
    {
        var table = new SpaceChannelTable();
        var author = Guid.NewGuid();
        var channel = table.Create(RoomChannelKind.Text, "velho", author, Now, out _)!;
        table.Delete(RoomChannelKind.Text, channel.Id, author, Now, out _);
        Assert.Equal(0, table.Expire(Now.AddDays(10)));
        Assert.Equal(1, table.Expire(Now.AddDays(31)));

        for (var i = 0; i < SpaceChannelTable.MaxPerKind; i++)
            Assert.NotNull(table.Create(RoomChannelKind.Text, $"canal {i}", author, Now, out _));
        Assert.Null(table.Create(RoomChannelKind.Text, "mais um", author, Now, out var problem));
        Assert.Contains("50", problem);
    }

    [Fact]
    public void PeerListAndChannelTablePacketsRoundTrip()
    {
        var sender = Guid.NewGuid();
        var hints = Enumerable.Range(1, 60).Select(i => new RoomPeerHint(Guid.NewGuid(), new IPEndPoint(IPAddress.Parse($"26.0.0.{i}"), 47802))).ToList();
        var packet = RoomPacket.PeerList(sender, hints);
        Assert.True(packet.Length <= RoomPacket.MaximumSize);
        Assert.True(RoomPacket.TryReadHeader(packet, out var type, out var from));
        Assert.Equal(RoomPacketType.PeerList, type);
        Assert.Equal(sender, from);
        Assert.True(RoomPacket.TryReadPeerList(RoomPacket.Body(packet), out var back));
        Assert.Equal(RoomPacket.MaxPeerHints, back.Count);
        Assert.Equal(hints[0].EndPoint, back[0].EndPoint);
        Assert.Equal(hints[0].Id, back[0].Id);
        Assert.False(RoomPacket.TryReadPeerList(RoomPacket.Body(packet)[..^1], out _));

        var table = new SpaceChannelTable();
        for (var i = 0; i < 45; i++) table.Create(i % 2 == 0 ? RoomChannelKind.Text : RoomChannelKind.Voice, $"{i} canal com nome comprido", sender, Now, out _);
        Assert.Equal(45, table.Count);
        var packets = RoomPacket.ChannelTable(sender, table.Snapshot());
        Assert.True(packets.Count > 1);
        var other = new SpaceChannelTable();
        foreach (var part in packets)
        {
            Assert.True(part.Length <= RoomPacket.MaximumSize);
            Assert.True(RoomPacket.TryReadHeader(part, out var partType, out _));
            Assert.Equal(RoomPacketType.ChannelTable, partType);
            Assert.True(RoomPacket.TryReadChannelTable(RoomPacket.Body(part), out var entries));
            foreach (var entry in entries) other.Merge(entry, Now);
        }
        Assert.Equal(table.Snapshot(), other.Snapshot());
    }

    [Fact]
    public void EncryptedHistoryKeepsMessagesUnreadableAndSurvivesACutTail()
    {
        var path = Path.Combine(Path.GetTempPath(), $"screenflow-history-{Guid.NewGuid():N}.sfl");
        var key = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);
        try
        {
            var history = new EncryptedHistory<RoomChatMessage>(path, key);
            var author = Guid.NewGuid();
            for (var i = 0; i < 5; i++)
                history.Append(new RoomChatMessage(author, "Ana", (ulong)i, Now.AddMinutes(i), $"mensagem secreta {i}", Channel: "a1b2c3d4e5f6"));
            var bytes = File.ReadAllBytes(path);
            Assert.DoesNotContain("secreta", Encoding.UTF8.GetString(bytes));
            Assert.DoesNotContain("Ana", Encoding.UTF8.GetString(bytes));

            var loaded = new EncryptedHistory<RoomChatMessage>(path, key).Load(3);
            Assert.Equal(3, loaded.Count);
            Assert.Equal("mensagem secreta 4", loaded[^1].Text);
            Assert.Equal("a1b2c3d4e5f6", loaded[^1].Channel);

            // O PC desligou no meio da última gravação: só ela se perde.
            File.WriteAllBytes(path, bytes[..^7]);
            Assert.Equal(4, new EncryptedHistory<RoomChatMessage>(path, key).Load(100).Count);
            // Outra chave não abre nada.
            Assert.Empty(new EncryptedHistory<RoomChatMessage>(path, System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)).Load(100));

            history.Compact();
            Assert.Equal(4, new EncryptedHistory<RoomChatMessage>(path, key).Load(100).Count);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void ServersAreSavedWithTheKeyProtectedAndRepeatedOnesAreRefused()
    {
        SecretProtection.Register(new TestProtector());
        var path = Path.Combine(Path.GetTempPath(), $"screenflow-servers-{Guid.NewGuid():N}.json");
        try
        {
            var store = new SpaceStore(path);
            var secret = SpaceKeys.FromNetwork("Amigos", "abc123");
            var space = new RoomSpace { Name = "  Amigos  do CS ", NetworkName = "Amigos", Secret = secret, HistoryKey = new byte[32], ManualPeers = ["26.1.2.3", "26.1.2.3", "lixo", "26.4.5.6:5000"] };
            Assert.Null(store.Add(space));
            Assert.Equal("Amigos do CS", space.Name);
            Assert.Equal(["26.1.2.3", "26.4.5.6:5000"], space.ManualPeers);
            Assert.NotNull(store.Add(new RoomSpace { Name = "Outro nome", Secret = secret.ToArray() }));

            var text = File.ReadAllText(path);
            Assert.DoesNotContain(Convert.ToBase64String(secret), text);
            Assert.Contains("\"SecretProtected\": \"teste:", text);

            var reloaded = new SpaceStore(path).Spaces.Single();
            Assert.Equal(secret, reloaded.Secret);
            Assert.Equal(space.Tag, reloaded.Tag);
            Assert.Equal("Amigos do CS", reloaded.Name);
            Assert.StartsWith("SF2-", reloaded.InviteCode());

            // Arquivo quebrado: a lista começa vazia, mas o arquivo antigo é guardado.
            File.WriteAllText(path, "{ quebrado");
            Assert.Empty(new SpaceStore(path).Spaces);
            Assert.NotEmpty(Directory.GetFiles(Path.GetDirectoryName(path)!, Path.GetFileName(path) + ".invalido-*"));
        }
        finally
        {
            File.Delete(path);
            foreach (var copy in Directory.GetFiles(Path.GetDirectoryName(path)!, Path.GetFileName(path) + ".invalido-*")) File.Delete(copy);
        }
    }
}
