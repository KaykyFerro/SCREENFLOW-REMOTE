using System.Net;
using ScreenFlow.Core.Media;
using ScreenFlow.Core.Room;
using Xunit;

namespace ScreenFlow.Tests;

/// <summary>Canais de texto e de voz da sala, como num servidor do Discord (1.3.0).</summary>
public class RoomChannelTests
{
    private static readonly Guid Me = Guid.Parse("8a3f0c1e-2b4d-4e6f-9a1b-3c5d7e9f1a2b");

    [Fact]
    public void TextChannelNamesLookLikeDiscord()
    {
        Assert.Equal("pedidos-de-música", RoomChannels.CleanName(RoomChannelKind.Text, "  Pedidos de Música!! "));
        Assert.Equal("Sala do Minecraft", RoomChannels.CleanName(RoomChannelKind.Voice, " Sala   do Minecraft "));
        Assert.Equal(string.Empty, RoomChannels.CleanName(RoomChannelKind.Text, "!!! ---"));
        Assert.True(RoomChannels.CleanName(RoomChannelKind.Voice, new string('a', 80)).Length <= RoomChannels.MaxNameLength);
        // Um emoji no limite não é cortado no meio.
        var emoji = RoomChannels.CleanName(RoomChannelKind.Voice, new string('a', 23) + "🎮🎮");
        Assert.False(char.IsHighSurrogate(emoji[^1]));
    }

    [Fact]
    public void ChannelsWithTheSameNameAreOne()
    {
        Assert.Equal(RoomChannels.Key("Música"), RoomChannels.Key("musica"));
        Assert.True(new RoomChannel(RoomChannelKind.Voice, "Música").Same(new RoomChannel(RoomChannelKind.Voice, "MUSICA")));
        Assert.False(new RoomChannel(RoomChannelKind.Text, "música").Same(new RoomChannel(RoomChannelKind.Voice, "Música")));
        Assert.True(RoomChannels.IsDefaultText(null));
        Assert.True(RoomChannels.IsDefaultText("Geral"));
        Assert.True(RoomChannels.IsDefaultVoice("geral"));
        Assert.False(RoomChannels.IsDefaultVoice("Jogando"));
    }

    [Fact]
    public void ListShowsDefaultsFirstAndEachChannelOnce()
    {
        var voice = RoomChannels.Merge(RoomChannelKind.Voice,
        [
            new RoomChannel(RoomChannelKind.Voice, "Valorant"),
            new RoomChannel(RoomChannelKind.Voice, "valorant"),
            new RoomChannel(RoomChannelKind.Voice, "jogando"),
            new RoomChannel(RoomChannelKind.Text, "fora-da-lista"),
            new RoomChannel(RoomChannelKind.Voice, "Apex"),
        ]);
        // 1.4.1: só a "Geral" vem pronta; "Jogando" agora é um canal como os outros.
        Assert.Equal(["Geral", "Apex", "jogando", "Valorant"], voice.Select(c => c.Name));
        Assert.Equal(RoomChannels.MaxListed, RoomChannels.Merge(RoomChannelKind.Text,
            Enumerable.Range(0, 100).Select(i => new RoomChannel(RoomChannelKind.Text, $"canal-{i}"))).Count);
    }

    [Fact]
    public void CreatedChannelsAreCleanedAndCapped()
    {
        var list = RoomChannels.Normalize(
        [
            new RoomChannel(RoomChannelKind.Text, "Geral"),            // padrão: não precisa criar
            new RoomChannel(RoomChannelKind.Text, "Memes e Prints"),
            new RoomChannel(RoomChannelKind.Text, "memes-e-prints"),   // repetido
            new RoomChannel(RoomChannelKind.Voice, "  "),              // vazio
            .. Enumerable.Range(1, 20).Select(i => new RoomChannel(RoomChannelKind.Voice, $"Sala {i}")),
        ]);
        Assert.Equal(RoomChannels.MaxPerPerson, list.Count);
        Assert.Equal(new RoomChannel(RoomChannelKind.Text, "memes-e-prints"), list[0]);
        Assert.Null(RoomChannels.Create((RoomChannelKind)9, "x"));
    }

    [Fact]
    public void PresenceCarriesTheVoiceChannelAndCreatedChannels()
    {
        var channels = new ValueList<RoomChannel>([new(RoomChannelKind.Voice, "Valorant"), new(RoomChannelKind.Text, "clipes")]);
        var presence = new RoomPresence("Kayky", "1.3.0", PresenceFlags.InVoiceChannel | PresenceFlags.Channels | PresenceFlags.Muted,
            StreamMode.Screen, 0, 47801, null, 7, VoiceChannel: "Valorant", Channels: channels);
        var packet = RoomPacket.Presence(Me, presence);
        Assert.True(packet.Length <= RoomPacket.MaximumSize);
        Assert.True(RoomPacket.TryReadPresence(RoomPacket.Body(packet), out var read));
        Assert.Equal(presence, read);
        Assert.Equal("Valorant", read.CurrentVoiceChannel);
        Assert.True(read.InVoiceChannel("valorant"));
        Assert.False(read.InVoiceChannel(RoomChannels.DefaultVoiceName));
        Assert.False(read.InVoice); // as versões antigas não veem na voz "Geral"
        Assert.True(read.SupportsChannels);
        Assert.Equal(2, read.CreatedChannels.Count);
    }

    [Fact]
    public void OldVersionsInVoiceAreInTheDefaultChannel()
    {
        var old = new RoomPresence("PC antigo", "1.2.0", PresenceFlags.InVoice);
        Assert.Equal(RoomChannels.DefaultVoiceName, old.CurrentVoiceChannel);
        Assert.True(old.InVoiceChannel("geral"));
        Assert.False(old.SupportsChannels);
        Assert.Empty(old.CreatedChannels);
        // Sem o nome do canal, a bandeira nova sozinha não conta como voz.
        Assert.Null(new RoomPresence("x", "1.3.0", PresenceFlags.InVoiceChannel).CurrentVoiceChannel);
    }

    [Fact]
    public void PresenceWithManyLongChannelsStillFits()
    {
        var channels = new ValueList<RoomChannel>(Enumerable.Range(0, 10)
            .Select(i => new RoomChannel(i % 2 == 0 ? RoomChannelKind.Voice : RoomChannelKind.Text, new string('é', 20) + i)));
        var presence = new RoomPresence(new string('ã', 40), "1.3.0", PresenceFlags.InVoiceChannel | PresenceFlags.Channels,
            Avatar: new RoomBlobRef(new string('a', 32), 1000), VoiceChannel: new string('ç', 24), Channels: channels);
        var packet = RoomPacket.Presence(Me, presence);
        Assert.True(packet.Length <= RoomPacket.MaximumSize);
        Assert.True(RoomPacket.TryReadPresence(RoomPacket.Body(packet), out var read));
        Assert.Equal(Math.Min(10, RoomChannels.MaxPerPerson), read.CreatedChannels.Count);
        Assert.NotNull(read.Avatar);
    }

    [Fact]
    public void ChatCarriesTheChannelAfterTheImage()
    {
        var image = new ChatImage(new string('b', 32), 120_000, 1600, 900);
        var message = new RoomChatMessage(Me, "Ana", 9, DateTimeOffset.FromUnixTimeMilliseconds(1_764_000_000_000), "olha esse clipe", Image: image,
            Channel: "prints-e-clipes");
        var packet = RoomPacket.Chat(Me, message);
        Assert.True(RoomPacket.TryReadChat(RoomPacket.Body(packet), out var read));
        Assert.Equal(message, read);
        Assert.False(read.InDefaultChannel);

        // Uma versão antiga ignora a bandeira do canal e o que vem depois da imagem: lê o texto e a imagem normalmente.
        var body = RoomPacket.Body(packet).ToArray();
        body[32] &= 0b011;
        Assert.True(RoomPacket.TryReadChat(body, out var asOld));
        Assert.Equal(message with { Channel = null }, asOld);
    }

    [Fact]
    public void ChatInTheDefaultChannelIsTheSameAsBefore()
    {
        var plain = new RoomChatMessage(Me, "Ana", 1, DateTimeOffset.FromUnixTimeMilliseconds(1_764_000_000_000), "oi");
        Assert.Equal(RoomPacket.Chat(Me, plain), RoomPacket.Chat(Me, plain with { Channel = "Geral" }));
        Assert.True(RoomPacket.TryReadChat(RoomPacket.Body(RoomPacket.Chat(Me, plain with { Channel = "geral" })), out var read));
        Assert.Null(read.Channel);
        Assert.Equal(RoomChannels.DefaultTextName, read.ChannelName);
    }

    [Fact]
    public void ChatWithABrokenChannelIsDropped()
    {
        var message = new RoomChatMessage(Me, "Ana", 3, DateTimeOffset.FromUnixTimeMilliseconds(1_764_000_000_000), "oi", Channel: "clipes");
        var body = RoomPacket.Body(RoomPacket.Chat(Me, message)).ToArray();
        Assert.False(RoomPacket.TryReadChat(body.AsSpan(0, body.Length - 3), out _));
    }

    [Fact]
    public void ValueListComparesByContent()
    {
        var a = new ValueList<RoomChannel>([new(RoomChannelKind.Text, "a")]);
        var b = new ValueList<RoomChannel>([new(RoomChannelKind.Text, "a")]);
        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
        Assert.Equal(new RoomPresence("x", "1", PresenceFlags.None, Channels: a), new RoomPresence("x", "1", PresenceFlags.None, Channels: b));
        _ = IPAddress.Loopback;
    }
}
