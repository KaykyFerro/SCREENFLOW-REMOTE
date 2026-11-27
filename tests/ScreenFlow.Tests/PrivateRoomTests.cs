using ScreenFlow.Core.Room;
using Xunit;

namespace ScreenFlow.Tests;

/// <summary>Sala privada (1.4.0): convite, pacotes lacrados, troca de chave, lista do dono e bloqueio.</summary>
public class PrivateRoomTests
{
    private static readonly Guid Owner = Guid.NewGuid();
    private static readonly Guid Member = Guid.NewGuid();

    [Fact]
    public void InviteCodeRoundTripsAndCatchesTypos()
    {
        var invite = RoomInvite.Create(Owner) with { Epoch = 3 };
        var code = invite.ToCode();
        Assert.StartsWith(RoomInvite.Prefix, code);
        Assert.True(RoomInvite.TryParse(code, out var parsed));
        Assert.Equal(invite.Secret, parsed.Secret);
        Assert.Equal(Owner, parsed.Owner);
        Assert.Equal(3u, parsed.Epoch);
        // Minúsculas, espaços e sem os traços também abrem.
        Assert.True(RoomInvite.TryParse("  " + code.ToLowerInvariant().Replace("-", " ").Replace("sf1 ", "SF1-") + " ", out _));
        // Uma letra trocada é pega pelo verificador.
        var body = code[RoomInvite.Prefix.Length..].ToCharArray();
        var index = Array.FindIndex(body, c => c != '-');
        body[index] = body[index] == 'A' ? 'B' : 'A';
        Assert.False(RoomInvite.TryParse(RoomInvite.Prefix + new string(body), out _));
        Assert.False(RoomInvite.TryParse("SF1-", out _));
        Assert.False(RoomInvite.TryParse("qualquer coisa", out _));
        Assert.False(RoomInvite.TryParse(null, out _));
    }

    private static byte[] Chat(Guid sender, string text)
        => RoomPacket.Chat(sender, new RoomChatMessage(sender, "Fulano", 1, DateTimeOffset.UtcNow, text));

    [Fact]
    public void SealedPacketOpensOnlyWithTheRoomKey()
    {
        var invite = RoomInvite.Create(Owner);
        var mine = new RoomKeyring();
        mine.Use(invite.Secret, 0, DateTimeOffset.UtcNow);
        var theirs = new RoomKeyring();
        theirs.Use(invite.Secret, 0, DateTimeOffset.UtcNow);
        var other = new RoomKeyring();
        other.Use(RoomInvite.Create(Owner).Secret, 0, DateTimeOffset.UtcNow);

        var plain = Chat(Member, "segredo");
        var sealedPacket = mine.Seal(plain, Member)!;
        Assert.True(RoomPacket.TryReadHeader(sealedPacket, out var type, out var sender));
        Assert.Equal(RoomPacketType.Sealed, type);
        Assert.Equal(Member, sender);
        Assert.Equal(plain.Length + RoomSeal.Overhead, sealedPacket.Length);
        // O texto não aparece aberto.
        Assert.DoesNotContain("segredo", System.Text.Encoding.UTF8.GetString(sealedPacket));

        Assert.Equal(plain, theirs.Open(sealedPacket, Member, DateTimeOffset.UtcNow));
        Assert.Null(other.Open(sealedPacket, Member, DateTimeOffset.UtcNow));
        // Quem lacrou precisa ser quem diz que mandou.
        Assert.Null(theirs.Open(sealedPacket, Guid.NewGuid(), DateTimeOffset.UtcNow));
        // Um byte mexido derruba o pacote.
        var tampered = sealedPacket.ToArray();
        tampered[^20] ^= 1;
        Assert.Null(theirs.Open(tampered, Member, DateTimeOffset.UtcNow));
        var header = sealedPacket.ToArray();
        header[RoomPacket.HeaderSize + 9] ^= 1; // a geração da chave, que vai aberta, também é conferida
        Assert.Null(theirs.Open(header, Member, DateTimeOffset.UtcNow));
    }

    [Fact]
    public void WithoutAPrivateRoomNothingIsSealed()
    {
        var keys = new RoomKeyring();
        Assert.False(keys.Active);
        Assert.Null(keys.Seal(Chat(Member, "oi"), Member));
    }

    [Fact]
    public void OldVersionsDropSealedPacketsAndNewTypesNeverWorkOpen()
    {
        var keys = new RoomKeyring();
        keys.Use(RoomInvite.Create(Owner).Secret, 0, DateTimeOffset.UtcNow);
        var sealedPacket = keys.Seal(Chat(Member, "oi"), Member)!;
        // Como a 1.3.0 lia o cabeçalho: tipos acima de BlobChunk eram descartados.
        Assert.True(sealedPacket[3] > (byte)RoomPacketType.BlobChunk);
        Assert.True(sealedPacket.Length <= RoomSeal.MaximumSealedSize);
    }

    [Fact]
    public void PreviousKeyWorksOnlyForTheGracePeriod()
    {
        var oldSecret = RoomInvite.Create(Owner).Secret;
        var sender = new RoomKeyring();
        sender.Use(oldSecret, 0, DateTimeOffset.UtcNow);
        var inFlight = sender.Seal(Chat(Member, "a caminho"), Member)!;

        var now = DateTimeOffset.UtcNow;
        var receiver = new RoomKeyring();
        receiver.Use(oldSecret, 0, now);
        receiver.Use(RoomInvite.Create(Owner).Secret, 1, now, keepPrevious: true);
        Assert.NotNull(receiver.Open(inFlight, Member, now + TimeSpan.FromSeconds(30), out var current));
        Assert.False(current);
        Assert.Null(receiver.Open(inFlight, Member, now + RoomKeyring.Grace + TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public void NewKeyIsWrappedOnlyForItsRecipient()
    {
        using var owner = new RoomKeyExchange();
        using var member = new RoomKeyExchange();
        using var outsider = new RoomKeyExchange();
        var secret = RoomInvite.Create(Owner).Secret;
        var wrapped = owner.Wrap(member.PublicKey, secret, 5, Member);
        Assert.Equal(secret, member.Unwrap(owner.PublicKey, wrapped, 5, Member));
        Assert.Null(outsider.Unwrap(owner.PublicKey, wrapped, 5, Member));
        Assert.Null(member.Unwrap(owner.PublicKey, wrapped, 6, Member));
        Assert.Null(member.Unwrap(owner.PublicKey, wrapped, 5, Guid.NewGuid()));
        Assert.Null(member.Unwrap("não é base64", wrapped, 5, Member));

        var packet = RoomPacket.Rekey(Owner, Member, 5, owner.PublicKey, wrapped);
        Assert.True(RoomPacket.TryReadHeader(packet, out var type, out _));
        Assert.Equal(RoomPacketType.Rekey, type);
        Assert.True(RoomPacket.TryReadRekey(RoomPacket.Body(packet), out var recipient, out var epoch, out var key, out var read));
        Assert.Equal((Member, 5u, owner.PublicKey), (recipient, epoch, key));
        Assert.Equal(wrapped, read);
    }

    [Fact]
    public void ExchangeKeyTravelsInThePresenceAndOldPresenceStillReads()
    {
        using var exchange = new RoomKeyExchange();
        var presence = new RoomPresence("Fulano", "1.4.0", PresenceFlags.Channels, ExchangeKey: exchange.PublicKey);
        var packet = RoomPacket.Presence(Member, presence);
        Assert.True(RoomPacket.TryReadPresence(RoomPacket.Body(packet), out var read));
        Assert.Equal(exchange.PublicKey, read.ExchangeKey);
        var plain = RoomPacket.Presence(Member, presence with { ExchangeKey = null });
        Assert.True(RoomPacket.TryReadPresence(RoomPacket.Body(plain), out var old));
        Assert.Null(old.ExchangeKey);
        Assert.Equal("Fulano", old.Name);
    }

    [Fact]
    public void RosterRoundTrips()
    {
        var members = Enumerable.Range(0, 5).Select(_ => Guid.NewGuid()).ToList();
        var packet = RoomPacket.Roster(Owner, true, members);
        Assert.True(RoomPacket.TryReadRoster(RoomPacket.Body(packet), out var approvedOnly, out var read));
        Assert.True(approvedOnly);
        Assert.Equal(members, read);
        Assert.True(packet.Length <= RoomPacket.MaximumSize);
        var full = RoomPacket.Roster(Owner, false, Enumerable.Range(0, 200).Select(_ => Guid.NewGuid()).ToList());
        Assert.True(RoomPacket.TryReadRoster(RoomPacket.Body(full), out _, out var capped));
        Assert.Equal(RoomPacket.MaxRoster, capped.Count);
        Assert.True(full.Length <= RoomPacket.MaximumSize);
    }

    [Fact]
    public void AccessRules()
    {
        var blocked = new HashSet<Guid> { Member };
        var none = new HashSet<Guid>();
        var other = Guid.NewGuid();
        // Bloqueado não entra, nem em sala aberta.
        Assert.False(RoomAccess.Allows(Member, Owner, false, null, blocked));
        Assert.True(RoomAccess.Allows(other, Owner, false, null, none));
        // Só aprovados: o dono sempre; os outros só na lista.
        Assert.True(RoomAccess.Allows(Owner, Owner, true, null, none));
        Assert.False(RoomAccess.Allows(other, Owner, true, null, none));
        Assert.False(RoomAccess.Allows(other, Owner, true, [Member], none));
        Assert.True(RoomAccess.Allows(other, Owner, true, [other], none));
    }

    [Fact]
    public void BiggestRoomPacketsStillFitWhenSealed()
    {
        var keys = new RoomKeyring();
        keys.Use(RoomInvite.Create(Owner).Secret, 0, DateTimeOffset.UtcNow);
        var biggest = new byte[RoomPacket.MaximumSize];
        RoomPacket.Leave(Member).CopyTo(biggest, 0);
        var sealedPacket = keys.Seal(biggest, Member)!;
        Assert.True(RoomPacket.TryReadHeader(sealedPacket, out _, out _));
        Assert.Equal(biggest, keys.Open(sealedPacket, Member, DateTimeOffset.UtcNow));
    }

    [Fact]
    public void FingerprintChangesWithTheKey()
    {
        var secret = RoomInvite.Create(Owner).Secret;
        Assert.Equal(RoomKeys.Fingerprint(secret, 0), RoomKeys.Fingerprint(secret, 0));
        Assert.NotEqual(RoomKeys.Fingerprint(secret, 0), RoomKeys.Fingerprint(secret, 1));
        Assert.Matches("^[0-9A-F]{4}-[0-9A-F]{4}$", RoomKeys.Fingerprint(secret, 0));
    }
}
