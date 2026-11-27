using System.Text;
using ScreenFlow.Core.Room;
using Xunit;

namespace ScreenFlow.Tests;

/// <summary>(1.5.0) Chat privado: a chave de cada conversa, a cifra, a confirmação, a fila de envio e o histórico guardado.</summary>
public sealed class DirectMessageTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 11, 27, 21, 0, 0, TimeSpan.Zero);
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "sf-dm-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); } catch (IOException) { }
    }

    /// <summary>Uma "rede" de mentira entre os PCs do teste: quem está nela recebe na hora (ou guarda para entregar depois).</summary>
    private sealed class TestNetwork
    {
        public readonly Dictionary<Guid, Person> People = [];
        public readonly HashSet<Guid> Offline = [];
        /// <summary>Joga fora o que combinar (de quem, para quem): um pacote que se perdeu no caminho.</summary>
        public Func<Guid, Guid, bool>? Drop;
        public readonly List<(Guid From, Guid To, byte[] Packet)> Wire = [];

        public void Send(Guid from, Guid to, byte[] packet)
        {
            Wire.Add((from, to, packet));
            if (Drop?.Invoke(from, to) == true || Offline.Contains(to) || !People.TryGetValue(to, out var target)) return;
            var sender = People[from];
            Assert.True(RoomPacket.TryReadHeader(packet, out var type, out var id));
            Assert.Equal(from, id);
            target.Chat.Receive(type, from, sender.Name, sender.AnnouncedKey, RoomPacket.Body(packet), Now,
                reply => { Send(to, from, reply); return true; });
        }
    }

    private sealed class Person : IDirectTransport
    {
        public Person(TestNetwork network, string name, string folder, Guid? id = null, DirectIdentity? identity = null)
        {
            Network = network;
            Name = name;
            Id = id ?? Guid.NewGuid();
            Identity = identity ?? DirectIdentity.Create();
            AnnouncedKey = Identity.PublicKey;
            Store = new DirectStore(folder, Enumerable.Repeat((byte)7, 32).ToArray());
            Chat = new DirectChat(Id, Identity, Store, this);
        }

        public TestNetwork Network { get; }
        public string Name { get; }
        public Guid Id { get; }
        public DirectIdentity Identity { get; }
        public string AnnouncedKey { get; set; }
        public DirectStore Store { get; }
        public DirectChat Chat { get; }

        public bool CanReach(Guid peer, string identityKey)
            => !Network.Offline.Contains(peer) && Network.People.TryGetValue(peer, out var other) && other.AnnouncedKey == identityKey;

        public bool Send(Guid peer, string identityKey, byte[] packet)
        {
            if (!CanReach(peer, identityKey)) return false;
            Network.Send(Id, peer, packet);
            return true;
        }
    }

    private (TestNetwork Network, Person Ana, Person Bia) Pair()
    {
        var network = new TestNetwork();
        var ana = new Person(network, "Ana", Path.Combine(_folder, "ana"));
        var bia = new Person(network, "Bia", Path.Combine(_folder, "bia"));
        network.People[ana.Id] = ana;
        network.People[bia.Id] = bia;
        return (network, ana, bia);
    }

    [Fact]
    public void BothSidesGetTheSameConversationKeyAndEveryPairIsDifferent()
    {
        using var ana = DirectIdentity.Create();
        using var bia = DirectIdentity.Create();
        using var caio = DirectIdentity.Create();
        Guid a = Guid.NewGuid(), b = Guid.NewGuid(), c = Guid.NewGuid();
        var ab = ana.PairKey(bia.PublicKey, a, b);
        Assert.NotNull(ab);
        Assert.Equal(32, ab!.Length);
        Assert.Equal(ab, bia.PairKey(ana.PublicKey, b, a));
        Assert.NotEqual(ab, ana.PairKey(caio.PublicKey, a, c));
        Assert.NotEqual(ab, caio.PairKey(bia.PublicKey, c, b));
        // A mesma chave com outro par de ids dá outra chave (o id entra na conta).
        Assert.NotEqual(ab, ana.PairKey(bia.PublicKey, a, c));
        Assert.Null(ana.PairKey("não é uma chave", a, b));
        Assert.Null(ana.PairKey(Convert.ToBase64String(new byte[65]), a, b));
    }

    [Fact]
    public void IdentityIsSavedAndOpenedAgainAndBadKeysAreRejected()
    {
        using var identity = DirectIdentity.Create();
        var exported = identity.Export();
        using var again = DirectIdentity.Import(exported);
        Assert.NotNull(again);
        Assert.Equal(identity.PublicKey, again!.PublicKey);
        Assert.True(DirectIdentity.IsValidPublicKey(identity.PublicKey));
        Assert.Equal(DirectIdentity.PublicKeyBytes, Convert.FromBase64String(identity.PublicKey).Length);
        Assert.Null(DirectIdentity.Import(null));
        Assert.Null(DirectIdentity.Import([1, 2, 3]));
        Assert.False(DirectIdentity.IsValidPublicKey(null));
        Assert.False(DirectIdentity.IsValidPublicKey("!!!"));
        Assert.False(DirectIdentity.IsValidPublicKey(Convert.ToBase64String(new byte[64])));
    }

    [Fact]
    public void SafetyCodeIsTheSameOnBothSidesAndChangesWithTheKey()
    {
        using var ana = DirectIdentity.Create();
        using var bia = DirectIdentity.Create();
        using var other = DirectIdentity.Create();
        var code = DirectIdentity.SafetyCode(ana.PublicKey, bia.PublicKey);
        Assert.Equal(code, DirectIdentity.SafetyCode(bia.PublicKey, ana.PublicKey));
        Assert.Matches(@"^\d{5} \d{5} \d{5} \d{5}$", code);
        Assert.NotEqual(code, DirectIdentity.SafetyCode(ana.PublicKey, other.PublicKey));
    }

    [Fact]
    public void MessageOpensOnlyForTheRecipientWithTheConversationKey()
    {
        using var ana = DirectIdentity.Create();
        using var bia = DirectIdentity.Create();
        Guid a = Guid.NewGuid(), b = Guid.NewGuid(), c = Guid.NewGuid();
        var key = ana.PairKey(bia.PublicKey, a, b)!;
        var image = new DirectImage(RoomBlob.IdOf([1, 2, 3]), 4096, 800, 600, Enumerable.Range(0, 32).Select(i => (byte)i).ToArray());
        var body = DirectCrypto.SealMessage(key, a, b, 42, Now, new DirectPayload("Oi, Bia! 🎮", image));
        Assert.True(DirectCrypto.TryReadRecipient(body, out var recipient));
        Assert.Equal(b, recipient);
        // O texto não vai aberto.
        Assert.DoesNotContain("Oi, Bia", Encoding.UTF8.GetString(body));

        Assert.True(DirectCrypto.TryOpenMessage(key, a, b, body, out var id, out var time, out var payload));
        Assert.Equal(42UL, id);
        Assert.Equal(Now, time);
        Assert.Equal("Oi, Bia! 🎮", payload.Text);
        Assert.NotNull(payload.Image);
        Assert.Equal(image.BlobId, payload.Image!.BlobId);
        Assert.Equal(image.Key, payload.Image.Key);
        Assert.Equal((800, 600, 4096), (payload.Image.Width, payload.Image.Height, payload.Image.Size));

        // Para outra pessoa, de outra pessoa, com outra chave ou mexido no caminho: não abre.
        Assert.False(DirectCrypto.TryOpenMessage(key, a, c, body, out _, out _, out _));
        Assert.False(DirectCrypto.TryOpenMessage(key, c, b, body, out _, out _, out _));
        Assert.False(DirectCrypto.TryOpenMessage(new byte[32], a, b, body, out _, out _, out _));
        for (var i = 0; i < body.Length; i += 7)
        {
            var tampered = body.ToArray();
            tampered[i] ^= 0x20;
            Assert.False(DirectCrypto.TryOpenMessage(key, a, b, tampered, out _, out _, out _));
        }
        Assert.False(DirectCrypto.TryOpenMessage(key, a, b, body.AsSpan(0, 40), out _, out _, out _));
    }

    [Fact]
    public void AckIsSignedWithTheConversationKey()
    {
        using var ana = DirectIdentity.Create();
        using var bia = DirectIdentity.Create();
        Guid a = Guid.NewGuid(), b = Guid.NewGuid();
        var key = bia.PairKey(ana.PublicKey, b, a)!;
        var ack = DirectCrypto.SealAck(key, b, a, 77);
        Assert.True(DirectCrypto.TryOpenAck(key, b, a, ack, out var id));
        Assert.Equal(77UL, id);
        Assert.False(DirectCrypto.TryOpenAck(key, a, a, ack, out _));
        Assert.False(DirectCrypto.TryOpenAck(new byte[32], b, a, ack, out _));
        var forged = ack.ToArray();
        forged[17] ^= 1; // outro número de mensagem
        Assert.False(DirectCrypto.TryOpenAck(key, b, a, forged, out _));
    }

    [Fact]
    public void PayloadSkipsUnknownFieldsAndRejectsBrokenOnes()
    {
        var plain = DirectCrypto.EncodePayload(new DirectPayload("legenda"));
        // Um campo de uma versão mais nova, depois do texto: é pulado.
        var newer = plain.Concat(new byte[] { 99, 0, 3, 1, 2, 3 }).ToArray();
        Assert.True(DirectCrypto.TryDecodePayload(newer, out var payload));
        Assert.Equal("legenda", payload.Text);
        // Campo cortado no meio: inválido.
        Assert.False(DirectCrypto.TryDecodePayload(plain.Concat(new byte[] { 99, 0, 9, 1 }).ToArray(), out _));
        // Sem texto e sem imagem: nada para mostrar.
        Assert.False(DirectCrypto.TryDecodePayload(DirectCrypto.EncodePayload(new DirectPayload("   ")), out _));
        Assert.False(DirectCrypto.TryDecodePayload([2, 0, 0], out _));
        // Texto grande demais é cortado no limite do chat.
        var big = DirectCrypto.EncodePayload(new DirectPayload(new string('a', 5000)));
        Assert.True(DirectCrypto.TryDecodePayload(big, out var cut));
        Assert.Equal(RoomPacket.MaxTextLength, cut.Text.Length);
    }

    [Fact]
    public void PrivateFilesAreEncryptedWithTheirOwnKey()
    {
        var picture = Enumerable.Range(0, 5000).Select(i => (byte)(i * 31)).ToArray();
        var (sealedBlob, key) = DirectCrypto.SealBlob(picture);
        Assert.Equal(picture.Length + DirectCrypto.NonceBytes + DirectCrypto.TagBytes, sealedBlob.Length);
        Assert.Equal(picture, DirectCrypto.OpenBlob(sealedBlob, key));
        Assert.Null(DirectCrypto.OpenBlob(sealedBlob, new byte[32]));
        sealedBlob[100] ^= 1;
        Assert.Null(DirectCrypto.OpenBlob(sealedBlob, key));
        Assert.NotEqual(DirectCrypto.SealBlob(picture).Key, key);
    }

    [Fact]
    public void PresenceCarriesTheIdentityKey()
    {
        using var identity = DirectIdentity.Create();
        var me = Guid.NewGuid();
        var presence = new RoomPresence("Ana", "1.5.0", PresenceFlags.Channels, IdentityKey: identity.PublicKey);
        Assert.True(RoomPacket.TryReadPresence(RoomPacket.Body(RoomPacket.Presence(me, presence)), out var read));
        Assert.Equal(identity.PublicKey, read.IdentityKey);
        // Uma chave que não é P-256 não vai.
        var broken = presence with { IdentityKey = Convert.ToBase64String(new byte[65]) };
        Assert.True(RoomPacket.TryReadPresence(RoomPacket.Body(RoomPacket.Presence(me, broken)), out var withoutKey));
        Assert.Null(withoutKey.IdentityKey);
    }

    [Fact]
    public void MessageIsDeliveredAndConfirmed()
    {
        var (network, ana, bia) = Pair();
        ana.Chat.Open(bia.Id, "Bia", bia.AnnouncedKey);
        var arrived = new List<DirectRecord>();
        bia.Chat.MessageChanged += (_, record) => arrived.Add(record);
        var record = ana.Chat.Send(bia.Id, new DirectPayload("bora jogar?"), Now);
        Assert.NotNull(record);

        Assert.Single(arrived);
        Assert.Equal("bora jogar?", arrived[0].Text);
        Assert.Equal(DirectState.Received, arrived[0].State);
        Assert.Equal(ana.Id, arrived[0].Author);
        // A confirmação voltou: entregue, e a fila ficou vazia.
        Assert.Equal(0, ana.Chat.PendingCount);
        Assert.Equal(DirectState.Delivered, ana.Store.Load(bia.Id).Single().State);
        Assert.False(ana.Store.Find(bia.Id)!.HasPending);
        // Do lado da Bia: a conversa com a Ana apareceu, com a chave dela guardada e uma não lida.
        var contact = bia.Store.Find(ana.Id)!;
        Assert.Equal("Ana", contact.Name);
        Assert.Equal(ana.AnnouncedKey, contact.IdentityKey);
        Assert.Equal(1, contact.Unread);
        Assert.Equal("bora jogar?", contact.LastPreview);
        bia.Chat.MarkRead(ana.Id);
        Assert.Equal(0, bia.Store.Unread);
        Assert.Equal(ana.Chat.SafetyCode(bia.Id), bia.Chat.SafetyCode(ana.Id));
        // Nada do texto passou aberto pela rede.
        Assert.All(network.Wire, packet => Assert.DoesNotContain("bora jogar", Encoding.UTF8.GetString(packet.Packet)));
    }

    [Fact]
    public void MessageWaitsWhileThePersonIsAwayAndGoesWhenSheComesBack()
    {
        var (network, ana, bia) = Pair();
        ana.Chat.Open(bia.Id, "Bia", bia.AnnouncedKey);
        network.Offline.Add(bia.Id);
        ana.Chat.Send(bia.Id, new DirectPayload("primeira"), Now);
        ana.Chat.Send(bia.Id, new DirectPayload("segunda"), Now.AddSeconds(1));
        Assert.Equal(2, ana.Chat.PendingCount);
        Assert.All(ana.Store.Load(bia.Id), r => Assert.Equal(DirectState.Pending, r.State));
        ana.Chat.Tick(Now.AddSeconds(10));
        Assert.Empty(network.Wire);

        network.Offline.Remove(bia.Id);
        ana.Chat.PeerAppeared(bia.Id, Now.AddSeconds(11));
        ana.Chat.Tick(Now.AddSeconds(11));
        Assert.Equal(0, ana.Chat.PendingCount);
        Assert.Equal(["primeira", "segunda"], bia.Store.Load(ana.Id).Select(r => r.Text));
        Assert.Equal(2, bia.Store.Find(ana.Id)!.Unread);
    }

    [Fact]
    public void LostAckIsResentAndTheMessageIsNotDuplicated()
    {
        var (network, ana, bia) = Pair();
        ana.Chat.Open(bia.Id, "Bia", bia.AnnouncedKey);
        // A confirmação da Bia se perde no caminho: a Ana manda de novo, e a Bia não mostra duas vezes.
        network.Drop = (from, _) => from == bia.Id;
        ana.Chat.Send(bia.Id, new DirectPayload("chegou?"), Now);
        Assert.Equal(1, ana.Chat.PendingCount);
        Assert.Equal(DirectState.Sent, ana.Store.Load(bia.Id).Single().State);
        Assert.Single(bia.Store.Load(ana.Id));

        network.Drop = null;
        ana.Chat.Tick(Now.AddSeconds(1));
        Assert.Equal(1, ana.Chat.PendingCount); // ainda não deu o tempo de mandar de novo
        ana.Chat.Tick(Now.AddSeconds(4));
        Assert.Equal(0, ana.Chat.PendingCount);
        Assert.Equal(DirectState.Delivered, ana.Store.Load(bia.Id).Single().State);
        Assert.Single(bia.Store.Load(ana.Id));
        Assert.Equal(1, bia.Store.Find(ana.Id)!.Unread);
    }

    [Fact]
    public void AnotherKeyWithTheSameIdGetsNothingInUntilAccepted()
    {
        var (network, ana, bia) = Pair();
        ana.Chat.Open(bia.Id, "Bia", bia.AnnouncedKey);
        ana.Chat.Send(bia.Id, new DirectPayload("oi"), Now);
        Assert.Equal(ana.AnnouncedKey, bia.Store.Find(ana.Id)!.IdentityKey);

        // Alguém aparece com o id da Ana, mas com outra chave (ou a Ana reinstalou): não entra nada.
        var impostor = new Person(network, "Ana", Path.Combine(_folder, "falsa"), ana.Id);
        network.People[ana.Id] = impostor;
        impostor.Chat.Open(bia.Id, "Bia", bia.AnnouncedKey);
        impostor.Chat.Send(bia.Id, new DirectPayload("me passa a senha"), Now.AddSeconds(5));
        Assert.Equal(["oi"], bia.Store.Load(ana.Id).Select(r => r.Text));
        var contact = bia.Store.Find(ana.Id)!;
        Assert.Equal(impostor.AnnouncedKey, contact.ChangedKey);
        Assert.Equal(ana.AnnouncedKey, contact.IdentityKey);
        Assert.Equal(1, impostor.Chat.PendingCount);
        // A Bia também não manda nada para a chave nova sem aceitar: só a Ana verdadeira abriria.
        Assert.False(bia.CanReach(ana.Id, contact.IdentityKey!));

        // Ela confere o código de segurança com a pessoa e aceita: a partir daí entra.
        bia.Chat.AcceptKey(ana.Id, Now.AddSeconds(6));
        Assert.Null(bia.Store.Find(ana.Id)!.ChangedKey);
        impostor.Chat.Tick(Now.AddSeconds(9));
        Assert.Equal(0, impostor.Chat.PendingCount);
        Assert.Equal(["oi", "me passa a senha"], bia.Store.Load(ana.Id).Select(r => r.Text));
    }

    [Fact]
    public void SomeoneElseCannotReadOrAnswerAsTheOtherPerson()
    {
        var (network, ana, bia) = Pair();
        var caio = new Person(network, "Caio", Path.Combine(_folder, "caio"));
        network.People[caio.Id] = caio;
        ana.Chat.Open(bia.Id, "Bia", bia.AnnouncedKey);
        ana.Chat.Send(bia.Id, new DirectPayload("segredo"), Now);
        var (_, _, packet) = network.Wire.First(w => w.To == bia.Id);
        // O Caio pegou o pacote no caminho: não é para ele, e mesmo dizendo que é a Bia não abre com a chave dele.
        Assert.Equal(DirectReceive.Ignored, caio.Chat.Receive(RoomPacketType.Direct, ana.Id, "Ana", ana.AnnouncedKey, RoomPacket.Body(packet), Now, _ => true));
        Assert.Null(caio.Store.Find(ana.Id));
        // Uma confirmação falsa (feita pelo Caio) não marca nada como entregue.
        network.Offline.Add(bia.Id);
        ana.Chat.Send(bia.Id, new DirectPayload("chegou?"), Now);
        var record = ana.Store.Load(bia.Id).Last();
        var fake = caio.Identity.PairKey(ana.AnnouncedKey, caio.Id, ana.Id)!;
        var forged = RoomPacket.DirectAck(bia.Id, DirectCrypto.SealAck(fake, bia.Id, ana.Id, record.Id));
        Assert.Equal(DirectReceive.Ignored, ana.Chat.Receive(RoomPacketType.DirectAck, bia.Id, "Bia", bia.AnnouncedKey, RoomPacket.Body(forged), Now, _ => true));
        Assert.Equal(1, ana.Chat.PendingCount);
    }

    [Fact]
    public void ConversationsAndPendingMessagesSurviveARestart()
    {
        var (network, ana, bia) = Pair();
        ana.Chat.Open(bia.Id, "Bia", bia.AnnouncedKey);
        ana.Chat.Send(bia.Id, new DirectPayload("entregue"), Now);
        network.Offline.Add(bia.Id);
        ana.Chat.Send(bia.Id, new DirectPayload("ficou na fila"), Now.AddSeconds(2));

        // Fecha e abre de novo (mesma pasta, mesma chave): a conversa volta e a fila também.
        var reopened = new Person(network, "Ana", Path.Combine(_folder, "ana"), ana.Id, ana.Identity);
        network.People[ana.Id] = reopened;
        var contact = reopened.Store.Find(bia.Id)!;
        Assert.Equal("Bia", contact.Name);
        Assert.Equal(bia.AnnouncedKey, contact.IdentityKey);
        Assert.True(contact.HasPending);
        Assert.Equal([DirectState.Delivered, DirectState.Pending], reopened.Store.Load(bia.Id).Select(r => r.State));
        reopened.Chat.RestoreOutbox();
        Assert.Equal(1, reopened.Chat.PendingCount);

        network.Offline.Remove(bia.Id);
        reopened.Chat.Tick(Now.AddSeconds(30));
        Assert.Equal(0, reopened.Chat.PendingCount);
        Assert.Equal(["entregue", "ficou na fila"], bia.Store.Load(ana.Id).Select(r => r.Text));

        // Com outra chave do histórico, nada abre (o arquivo é cifrado).
        var stranger = new DirectStore(Path.Combine(_folder, "ana"), Enumerable.Repeat((byte)9, 32).ToArray());
        Assert.Empty(stranger.Contacts);
        Assert.Empty(stranger.Load(bia.Id));
        Assert.DoesNotContain("ficou na fila", Encoding.UTF8.GetString(File.ReadAllBytes(Path.Combine(_folder, "ana", $"{bia.Id:N}.history"))));
    }

    [Fact]
    public void RemovingAConversationDeletesItsHistoryFromThisPc()
    {
        var (_, ana, bia) = Pair();
        ana.Chat.Open(bia.Id, "Bia", bia.AnnouncedKey);
        ana.Chat.Send(bia.Id, new DirectPayload("tchau"), Now);
        var file = Path.Combine(_folder, "ana", $"{bia.Id:N}.history");
        Assert.True(File.Exists(file));
        ana.Chat.Remove(bia.Id);
        Assert.Null(ana.Store.Find(bia.Id));
        Assert.False(File.Exists(file));
        Assert.Empty(new DirectStore(Path.Combine(_folder, "ana"), Enumerable.Repeat((byte)7, 32).ToArray()).Contacts);
    }

    [Fact]
    public void StrangersCannotPushOutYourConversations()
    {
        var store = new DirectStore(Path.Combine(_folder, "cheio"), new byte[32]);
        for (var i = 0; i < DirectStore.MaxContacts; i++) Assert.NotNull(store.Upsert(Guid.NewGuid(), $"Pessoa {i}"));
        Assert.Null(store.Upsert(Guid.NewGuid(), "Desconhecido", evict: false));
        Assert.Equal(DirectStore.MaxContacts, store.Contacts.Count);
        Assert.NotNull(store.Upsert(Guid.NewGuid(), "Você abriu"));
        Assert.Equal(DirectStore.MaxContacts, store.Contacts.Count);
    }
}
