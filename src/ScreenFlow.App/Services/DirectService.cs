using System.Collections.ObjectModel;
using System.IO;
using System.Security.Cryptography;
using System.Windows.Media;
using System.Windows.Threading;
using ScreenFlow.Core;
using ScreenFlow.Core.Diagnostics;
using ScreenFlow.Core.Room;
using ScreenFlow.Infrastructure.Room;

namespace ScreenFlow.App.Services;

/// <summary>Uma mensagem privada chegou sem ninguém ver (para o aviso e o som).</summary>
public sealed record DirectArrival(Guid Peer, string Name, string Preview);

/// <summary>
/// (1.5.0) As mensagens diretas na tela: a lista de conversas, a conversa aberta e os avisos. Quem manda, recebe e guarda
/// é o <see cref="DirectChat"/>; aqui ele é ligado às salas e servidores abertos (cada mensagem vai pela sala em que a
/// pessoa está agora, anunciando a chave guardada dela) e à interface. Tudo na thread da interface.
/// </summary>
public sealed class DirectService : IDirectTransport
{
    /// <summary>Quantas mensagens da conversa aberta ficam na tela (as mais antigas continuam guardadas).</summary>
    public const int ShownMessages = 300;
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _timer;
    private DirectIdentity? _identity;
    private DirectChat? _chat;
    private DirectConversationRow? _current;
    private bool _showing;
    private bool _demo;
    private int _ticks;

    public DirectService(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
        _timer = new DispatcherTimer(DispatcherPriority.Background, dispatcher) { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => Tick();
    }

    /// <summary>As conversas, da mais recente para a mais antiga.</summary>
    public ObservableCollection<DirectConversationRow> Conversations { get; } = [];

    /// <summary>As mensagens da conversa aberta.</summary>
    public ObservableCollection<ChatRow> Messages { get; } = [];

    /// <summary>A conversa aberta (null: nenhuma).</summary>
    public DirectConversationRow? Current => _current;

    /// <summary>A chave pública deste PC no chat privado (vai na presença de cada sala). Null sem chat privado.</summary>
    public string? PublicKey => _identity?.PublicKey;

    /// <summary>Por que o chat privado não abriu (null quando está funcionando).</summary>
    public string? Problem { get; private set; }

    public bool IsAvailable => _chat is not null || _demo;

    /// <summary>Mensagens privadas não lidas, somando as conversas.</summary>
    public int Unread => _demo ? Conversations.Sum(c => c.Unread) : _chat?.Store.Unread ?? 0;

    /// <summary>As mensagens diretas estão abertas no chat (no lugar dos canais do servidor).</summary>
    public bool Showing
    {
        get => _showing;
        set
        {
            if (_showing == value) return;
            _showing = value;
            ShowingChanged?.Invoke();
            if (value) MarkRead();
        }
    }

    /// <summary>A tela do chat está aberta e na frente (quem decide é a janela principal).</summary>
    public Func<bool>? IsChatInFront { get; set; }

    public event Action? ShowingChanged;

    /// <summary>A conversa aberta mudou (outra pessoa, ou o estado dela: online, a chave).</summary>
    public event Action? CurrentChanged;

    /// <summary>A lista de conversas mudou (entrou, saiu ou subiu alguma).</summary>
    public event Action? ConversationsChanged;

    public event Action? UnreadChanged;

    /// <summary>Uma mensagem privada chegou e não está na frente de quem usa.</summary>
    public event Action<DirectArrival>? MessageArrived;

    // ───────────────────────── Ciclo de vida ─────────────────────────

    /// <summary>
    /// Abre o chat privado deste PC: a identidade (criada na primeira vez e guardada cifrada pelo Windows) e as conversas
    /// guardadas. Chamado antes das salas abrirem, para elas anunciarem a chave.
    /// </summary>
    public void Start(Guid self)
    {
        if (_chat is not null || _demo) return;
        var settings = AppServices.Settings.Current;
        var folder = Path.Combine(AppInfo.DataDirectory, "mensagens-diretas");
        try
        {
            var save = false;
            var identity = DirectIdentity.Import(settings.DirectIdentityKey);
            if (identity is null)
            {
                if (settings.DirectIdentityKey is not null) Log.Warn("A chave do chat privado guardada não abriu: este PC passa a ter uma chave nova.");
                identity = DirectIdentity.Create();
                settings.DirectIdentityKey = identity.Export();
                save = true;
            }
            var key = settings.DirectHistoryKey is { Length: 32 } stored ? stored : null;
            if (key is null)
            {
                // As conversas guardadas foram cifradas com uma chave que não abre mais (outro PC ou outra conta do Windows):
                // ficam de lado numa pasta, sem misturar com as novas.
                SetAside(folder);
                key = RandomNumberGenerator.GetBytes(32);
                settings.DirectHistoryKey = key;
                save = true;
            }
            if (save) AppServices.Settings.Save();
            Directory.CreateDirectory(folder);
            _identity = identity;
            _chat = new DirectChat(self, identity, new DirectStore(folder, key), this);
            _chat.MessageChanged += OnMessageChanged;
            _chat.ContactChanged += OnContactChanged;
            _chat.RestoreOutbox();
            Rebuild();
            _timer.Start();
            if (_chat.PendingCount > 0) Log.Info($"Chat privado: {_chat.PendingCount} mensagem(ns) esperando para ir.");
        }
        catch (Exception exception)
        {
            Problem = "O chat privado não abriu neste PC. Feche e abra o ScreenFlow de novo; se continuar, veja o registro em Configurações › Sobre.";
            Log.Warn($"O chat privado não abriu: {exception}");
            _chat = null;
            _identity?.Dispose();
            _identity = null;
        }
    }

    public void Stop()
    {
        _timer.Stop();
        try { _chat?.Store.SaveContacts(); } catch (Exception exception) { Log.Debug($"Chat privado: {exception.Message}"); }
    }

    private static void SetAside(string folder)
    {
        try
        {
            if (!Directory.Exists(folder) || !Directory.EnumerateFileSystemEntries(folder).Any()) return;
            var aside = $"{folder}-sem-chave-{DateTime.Now:yyyyMMdd-HHmmss}";
            Directory.Move(folder, aside);
            Log.Warn($"As conversas privadas guardadas não abrem com a chave deste PC: ficaram em {aside}.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"As conversas privadas antigas não foram postas de lado: {exception.Message}");
        }
    }

    // ───────────────────────── Por onde vai (IDirectTransport) ─────────────────────────

    /// <summary>
    /// A sala em que a pessoa está agora anunciando esta chave: primeiro o servidor aberto, depois os outros e a sala da
    /// rede. Nunca manda para quem aparece com outra chave (pode ser alguém se passando pela pessoa).
    /// </summary>
    private static RoomServer? Route(Guid peer, string identityKey)
    {
        var room = AppServices.Room;
        if (room.Active.IdentityKeyOf(peer) == identityKey) return room.Active;
        foreach (var server in room.AllServers)
            if (server != room.Active && server.IdentityKeyOf(peer) == identityKey) return server;
        return null;
    }

    bool IDirectTransport.CanReach(Guid peer, string identityKey) => Route(peer, identityKey) is not null;

    bool IDirectTransport.Send(Guid peer, string identityKey, byte[] packet) => Route(peer, identityKey)?.SendDirect(peer, packet) == true;

    /// <summary>Uma mensagem privada (ou a confirmação) chegou por um servidor: o <see cref="DirectChat"/> confere e abre.</summary>
    internal void OnDirect(RoomServer server, RoomDirectPacket packet)
    {
        if (_chat is not { } chat) return;
        var result = chat.Receive(packet.Type, packet.Sender, packet.SenderName, packet.IdentityKey, packet.Body, DateTimeOffset.UtcNow,
            reply => server.SendDirect(packet.Sender, reply));
        if (result == DirectReceive.KeyChanged) Log.Info($"Chat privado: {packet.SenderName} apareceu com outra chave; nada entra até você aceitar.");
    }

    /// <summary>Alguém apareceu (ou mudou) numa sala: se for de uma conversa, confere a chave e manda o que estava esperando.</summary>
    internal void OnPeerSeen(Guid peer, string name, string? identityKey, string? avatar, bool appeared)
    {
        if (_chat is not { } chat || chat.Store.Find(peer) is not { } contact) return;
        if (avatar is not null && contact.Avatar != avatar)
        {
            contact.Avatar = avatar;
            chat.Store.SaveContacts();
            if (Find(peer) is { } row) row.Photo = AppServices.Room.PhotoFor(avatar);
        }
        if (identityKey is not null) chat.NoticeKey(peer, name, identityKey);
        if (appeared) chat.PeerAppeared(peer, DateTimeOffset.UtcNow);
    }

    private void Tick()
    {
        if (_chat is not { } chat) return;
        chat.Tick(DateTimeOffset.UtcNow);
        // A cada 2 s: quem está online (e por qual servidor).
        if (++_ticks % 2 == 0) UpdatePresence();
    }

    // ───────────────────────── Conversas ─────────────────────────

    /// <summary>
    /// Abre a conversa com alguém que está numa sala agora (clicou na pessoa). Null quando a pessoa está num ScreenFlow
    /// antigo (sem chat privado) ou o chat privado deste PC não abriu.
    /// </summary>
    public DirectConversationRow? OpenWith(Guid peer, string name, out string? problem)
    {
        problem = null;
        if (_demo)
        {
            var demo = Find(peer) ?? Conversations.FirstOrDefault();
            Show(demo);
            Showing = true;
            return demo;
        }
        if (_chat is not { } chat)
        {
            problem = Problem ?? "O chat privado não está funcionando neste PC.";
            return null;
        }
        if (peer == chat.Self) return null;
        var announced = AnnouncedKeys(peer).FirstOrDefault();
        var contact = chat.Store.Find(peer);
        if (contact is null && announced is null)
        {
            problem = $"{name} está numa versão antiga do ScreenFlow, sem mensagens diretas. Quando atualizar para a 1.5.0, dá para conversar.";
            return null;
        }
        chat.Open(peer, name, announced);
        var row = Find(peer);
        Show(row);
        Showing = true;
        return row;
    }

    /// <summary>A chave que a pessoa anuncia nas salas em que está agora (a do servidor aberto primeiro).</summary>
    private static IEnumerable<string> AnnouncedKeys(Guid peer)
    {
        var room = AppServices.Room;
        foreach (var server in new[] { room.Active }.Concat(room.AllServers.Where(s => s != room.Active)))
            if (server.IdentityKeyOf(peer) is { } key) yield return key;
    }

    /// <summary>Abre uma conversa da lista (null: nenhuma).</summary>
    public void Show(DirectConversationRow? row)
    {
        if (row is not null && !Conversations.Contains(row)) row = null;
        if (_current == row)
        {
            MarkRead();
            return;
        }
        if (_current is not null) _current.IsSelected = false;
        _current = row;
        if (row is not null) row.IsSelected = true;
        LoadMessages();
        MarkRead();
        CurrentChanged?.Invoke();
    }

    /// <summary>Abre a conversa com essa pessoa, se tiver uma (o aviso de mensagem nova).</summary>
    public void Show(Guid peer)
    {
        Show(Find(peer));
        Showing = true;
    }

    public DirectConversationRow? Find(Guid peer) => Conversations.FirstOrDefault(c => c.Id == peer);

    /// <summary>A conversa aberta está na frente de quem usa: marca como lida.</summary>
    public void MarkRead()
    {
        if (_current is not { } row || !IsShowing(row.Id)) return;
        if (_demo)
        {
            if (row.Unread == 0) return;
            row.Unread = 0;
            UnreadChanged?.Invoke();
            return;
        }
        _chat?.MarkRead(row.Id);
    }

    private bool IsShowing(Guid peer) => _showing && _current?.Id == peer && IsChatInFront?.Invoke() == true;

    /// <summary>Manda na conversa aberta. Retorna o motivo quando não deu.</summary>
    public string? Send(string text, PreparedImage? image)
    {
        if (_current is not { } row) return "Escolha uma conversa.";
        if (_demo)
        {
            var clean = RoomPacket.CleanText(text);
            if (clean.Length == 0 && image is null) return null;
            var record = new DirectRecord((ulong)Random.Shared.NextInt64(1, long.MaxValue), AppServices.Room.SelfId, DateTimeOffset.Now, clean,
                image is null ? null : new DirectImage(AppServices.Blobs.Put(image.Data), image.Data.Length, image.Width, image.Height, []),
                DirectState.Delivered, DateTimeOffset.Now);
            AddRow(record, row);
            return null;
        }
        if (_chat is not { } chat) return Problem ?? "O chat privado não está funcionando neste PC.";
        DirectImage? attached = null;
        if (image is not null)
        {
            // A imagem vai cifrada com uma chave só dela (que vai dentro da mensagem): quem baixa da sala não vê nada.
            if (image.Data.Length > RoomBlob.MaxImageBytes - DirectCrypto.NonceBytes - DirectCrypto.TagBytes) return "A imagem é grande demais para mandar.";
            var (sealedBlob, key) = DirectCrypto.SealBlob(image.Data);
            var id = AppServices.Blobs.Put(sealedBlob);
            attached = new DirectImage(id, sealedBlob.Length, image.Width, image.Height, key);
        }
        return chat.Send(row.Id, new DirectPayload(text, attached), DateTimeOffset.UtcNow) is null ? "A mensagem está vazia." : null;
    }

    /// <summary>Aceita a chave nova da pessoa da conversa aberta (depois de conferir o código de segurança com ela).</summary>
    public void AcceptKey()
    {
        if (_current is { } row) _chat?.AcceptKey(row.Id, DateTimeOffset.UtcNow);
    }

    /// <summary>Continua com a chave guardada da pessoa da conversa aberta.</summary>
    public void KeepKey()
    {
        if (_current is { } row) _chat?.KeepKey(row.Id);
    }

    /// <summary>O código de segurança da conversa (null sem a chave da pessoa).</summary>
    public string? SafetyCode(Guid peer) => _demo ? "48213 90577 16402 33859" : _chat?.SafetyCode(peer);

    /// <summary>O código de segurança que a chave nova daria (para conferir antes de aceitar).</summary>
    public string? ChangedSafetyCode(Guid peer)
        => _chat is { } chat && chat.Store.Find(peer)?.ChangedKey is { } changed ? DirectIdentity.SafetyCode(chat.PublicKey, changed) : null;

    /// <summary>Apaga a conversa deste PC (a outra pessoa continua com a dela).</summary>
    public void Remove(DirectConversationRow row)
    {
        if (_current == row) Show((DirectConversationRow?)null);
        if (_demo)
        {
            Conversations.Remove(row);
            ConversationsChanged?.Invoke();
            UnreadChanged?.Invoke();
            return;
        }
        _chat?.Remove(row.Id);
    }

    /// <summary>A imagem não veio: pede de novo para a pessoa da conversa.</summary>
    public void RetryImage(ChatRow row)
    {
        if (row.Image is not { } image || _current is not { } conversation) return;
        row.ImageState = ChatImageState.Loading;
        if (AppServices.Blobs.Has(image.Id))
        {
            LoadPicture(row);
            return;
        }
        if (!Fetch(conversation.Id, image)) row.ImageState = ChatImageState.Failed;
    }

    /// <summary>Um arquivo chegou (por qualquer servidor): pode ser a imagem de uma mensagem privada.</summary>
    internal void OnBlobArrived(string id)
    {
        foreach (var row in Messages)
            if (row.Image?.Id == id) LoadPicture(row);
    }

    internal void OnPhotosChanged()
    {
        if (_chat is not { } chat) return;
        foreach (var row in Conversations) row.Photo = AppServices.Room.PhotoFor(chat.Store.Find(row.Id)?.Avatar ?? AppServices.Room.AvatarOf(row.Id));
        foreach (var message in Messages) message.Photo = PhotoOf(message.AuthorId);
    }

    // ───────────────────────── Mudanças do DirectChat ─────────────────────────

    private void OnMessageChanged(Guid peer, DirectRecord record)
    {
        if (_current?.Id == peer && Find(peer) is { } current) AddRow(record, current);
        if (record.State != DirectState.Received) return;
        if (record.Image is { } image && !AppServices.Blobs.Has(image.BlobId)) Fetch(peer, new ChatImage(image.BlobId, image.Size, image.Width, image.Height));
        if (IsShowing(peer))
        {
            _chat?.MarkRead(peer);
            return;
        }
        var name = _chat?.Store.Find(peer)?.Name ?? "Alguém";
        MessageArrived?.Invoke(new DirectArrival(peer, name, DirectChat.Preview(record)));
    }

    private void OnContactChanged(Guid peer)
    {
        if (_chat is not { } chat) return;
        var contact = chat.Store.Find(peer);
        var row = Find(peer);
        if (contact is null)
        {
            if (row is not null)
            {
                if (_current == row) Show((DirectConversationRow?)null);
                Conversations.Remove(row);
                ConversationsChanged?.Invoke();
            }
            UnreadChanged?.Invoke();
            return;
        }
        if (row is null)
        {
            row = new DirectConversationRow(peer);
            Conversations.Add(row);
            // Com conversas demais, abrir uma nova apaga a mais antiga: ela sai da lista também.
            foreach (var gone in Conversations.Where(c => chat.Store.Find(c.Id) is null).ToList()) Conversations.Remove(gone);
        }
        Fill(row, contact);
        Reorder(row);
        ConversationsChanged?.Invoke();
        UnreadChanged?.Invoke();
        if (_current == row) CurrentChanged?.Invoke();
    }

    private void Rebuild()
    {
        if (_chat is not { } chat) return;
        Conversations.Clear();
        foreach (var contact in chat.Store.Contacts)
        {
            var row = new DirectConversationRow(contact.Id);
            Fill(row, contact);
            Conversations.Add(row);
        }
        UpdatePresence();
        ConversationsChanged?.Invoke();
        UnreadChanged?.Invoke();
    }

    private void Fill(DirectConversationRow row, DirectContact contact)
    {
        row.Name = contact.Name.Length > 0 ? contact.Name : "Alguém";
        row.Preview = contact.LastPreview ?? "Nenhuma mensagem ainda";
        row.LastActivity = contact.LastActivity;
        row.Unread = contact.Unread;
        row.KeyChanged = contact.ChangedKey is not null;
        row.HasKey = contact.IdentityKey is not null;
        row.Photo ??= AppServices.Room.PhotoFor(contact.Avatar ?? AppServices.Room.AvatarOf(contact.Id));
        UpdatePresence(row, contact);
    }

    /// <summary>A conversa mais recente fica em cima.</summary>
    private void Reorder(DirectConversationRow row)
    {
        var from = Conversations.IndexOf(row);
        var to = Conversations.Count(c => c != row && c.LastActivity > row.LastActivity);
        if (from >= 0 && to != from) Conversations.Move(from, to);
    }

    private void UpdatePresence()
    {
        if (_chat is not { } chat) return;
        var changed = false;
        foreach (var row in Conversations)
        {
            if (chat.Store.Find(row.Id) is not { } contact) continue;
            var was = (row.IsOnline, row.StatusText);
            UpdatePresence(row, contact);
            changed |= was != (row.IsOnline, row.StatusText);
        }
        if (changed && _current is not null) CurrentChanged?.Invoke();
    }

    /// <summary>Online (e onde) quando a pessoa está numa sala com a chave guardada.</summary>
    private static void UpdatePresence(DirectConversationRow row, DirectContact contact)
    {
        var where = contact.IdentityKey is { } key ? Route(row.Id, key) : null;
        row.IsOnline = where is not null;
        row.StatusText = where is null
            ? contact.IdentityKey is null ? "Esperando a pessoa aparecer com o ScreenFlow 1.5.0" : "Offline · a mensagem vai quando ela aparecer"
            : where.IsSpace ? $"Online em {where.DisplayName}" : "Online na sala da rede";
    }

    // ───────────────────────── Mensagens da conversa aberta ─────────────────────────

    private void LoadMessages()
    {
        Messages.Clear();
        if (_current is not { } row) return;
        if (_demo)
        {
            foreach (var record in row.DemoMessages) AddRow(record, row);
            return;
        }
        if (_chat is not { } chat) return;
        foreach (var record in chat.Store.Load(row.Id, ShownMessages)) AddRow(record, row);
    }

    /// <summary>Põe a mensagem na conversa aberta (ou atualiza o estado de uma que já está).</summary>
    private void AddRow(DirectRecord record, DirectConversationRow conversation)
    {
        var self = AppServices.Room.SelfId;
        var mine = record.Author == self;
        var existing = Messages.FirstOrDefault(m => m.AuthorId == record.Author && m.MessageId == record.Id);
        if (existing is not null)
        {
            existing.Delivery = mine ? record.State : null;
            UpdateDeliveryMarks();
            return;
        }
        var name = mine ? AppServices.Room.SelfName : conversation.Name;
        var image = record.Image is { } attached ? new ChatImage(attached.BlobId, attached.Size, attached.Width, attached.Height) : null;
        var message = new RoomChatMessage(record.Author, name, record.Id, record.Time, record.Text, Image: image);
        var row = new ChatRow(message, mine)
        {
            Photo = PhotoOf(record.Author),
            ImageKey = record.Image is { Key.Length: 32 } secret ? secret.Key : null,
            DirectPeer = conversation.Id,
            Delivery = mine ? record.State : null,
        };
        var index = Messages.Count;
        while (index > 0 && Messages[index - 1].Time > row.Time) index--;
        LoadPicture(row);
        Messages.Insert(index, row);
        while (Messages.Count > ShownMessages)
        {
            Messages.RemoveAt(0);
            index--;
        }
        RoomServer.Regroup(Messages, Math.Max(0, index));
        UpdateDeliveryMarks();
    }

    /// <summary>"Entregue" só na última mensagem sua que chegou; as que ainda não chegaram sempre dizem.</summary>
    private void UpdateDeliveryMarks()
    {
        var lastDelivered = Messages.LastOrDefault(m => m.Delivery == DirectState.Delivered);
        foreach (var row in Messages)
            row.ShowDelivery = row.Delivery is DirectState.Pending or DirectState.Sent || (row == lastDelivered && row == Messages.Last(m => m.IsMine));
    }

    private ImageSource? PhotoOf(Guid person)
    {
        var room = AppServices.Room;
        if (person == room.SelfId) return room.SelfPhoto;
        var avatar = _chat?.Store.Find(person)?.Avatar ?? room.AvatarOf(person);
        return room.PhotoFor(avatar);
    }

    /// <summary>Abre a imagem (cifrada no disco) de uma mensagem para mostrar.</summary>
    private static void LoadPicture(ChatRow row)
    {
        if (row.Image is not { } image) return;
        if (row.ImageData() is not { } data)
        {
            if (row.ImageState != ChatImageState.Failed) row.ImageState = AppServices.Blobs.Has(image.Id) ? ChatImageState.Failed : ChatImageState.Loading;
            return;
        }
        row.Picture = ImageTools.Decode(data, Math.Min(image.Width, ChatRow.PictureDecodeWidth));
        row.ImageState = row.Picture is null ? ChatImageState.Failed : ChatImageState.Ready;
    }

    /// <summary>Baixa a imagem (cifrada) de uma mensagem privada de quem mandou, pela sala em que ela está.</summary>
    private bool Fetch(Guid peer, ChatImage image)
    {
        if (_chat?.Store.Find(peer)?.IdentityKey is not { } key || Route(peer, key) is not { } server) return false;
        server.FetchBlob(image.Blob, peer);
        return true;
    }

    // ───────────────────────── Capturas de tela ─────────────────────────

    /// <summary>Conversas de exemplo das capturas do CI (só na memória: nada é guardado nem vai para a rede).</summary>
    public void LoadDemo(IReadOnlyList<(Guid Id, string Name, ImageSource? Photo)> people)
    {
        _demo = true;
        Conversations.Clear();
        var self = AppServices.Room.SelfId;
        var today = DateTimeOffset.Now.Date;
        DateTimeOffset At(int hour, int minute, int daysAgo = 0) => new DateTimeOffset(today.AddDays(-daysAgo)).AddHours(hour).AddMinutes(minute);
        var rows = new List<DirectConversationRow>();
        for (var i = 0; i < people.Count; i++)
        {
            var (id, name, photo) = people[i];
            var row = new DirectConversationRow(id) { Name = name, Photo = photo, HasKey = true };
            rows.Add(row);
            switch (i)
            {
                case 0:
                    row.DemoMessages.AddRange(
                    [
                        new DirectRecord(1, id, At(20, 41), "Ei, você vai transmitir a ranqueada hoje?", State: DirectState.Received),
                        new DirectRecord(2, self, At(20, 43), "Vou sim, lá pelas 21h. Quer entrar na voz junto?", State: DirectState.Delivered),
                        new DirectRecord(3, id, At(20, 44), "Bora! Me chama quando abrir 🎮", State: DirectState.Received),
                        new DirectRecord(4, id, At(20, 45), "E me passa aquela config do OBS depois", State: DirectState.Received),
                        new DirectRecord(5, self, At(20, 47), "Fechado, mando aqui mesmo", State: DirectState.Delivered),
                        new DirectRecord(6, self, At(20, 48), "Só um minuto que já te passo", State: DirectState.Sent),
                    ]);
                    row.Unread = 0;
                    row.IsOnline = true;
                    row.StatusText = "Online em Amigos do CS";
                    break;
                case 1:
                    row.DemoMessages.Add(new DirectRecord(7, id, At(19, 12), "Valeu pela transmissão de ontem!", State: DirectState.Received));
                    row.Unread = 2;
                    row.IsOnline = true;
                    row.StatusText = "Online na sala da rede";
                    break;
                default:
                    row.DemoMessages.Add(new DirectRecord(8, self, At(22, 30, daysAgo: 1), "Amanhã eu te mostro o mixer novo", State: DirectState.Pending));
                    row.IsOnline = false;
                    row.StatusText = "Offline · a mensagem vai quando ela aparecer";
                    break;
            }
            var last = row.DemoMessages.Last();
            row.Preview = DirectChat.Preview(last);
            row.LastActivity = last.Time;
        }
        foreach (var row in rows.OrderByDescending(r => r.LastActivity)) Conversations.Add(row);
        ConversationsChanged?.Invoke();
        UnreadChanged?.Invoke();
    }
}

/// <summary>(1.5.0) Uma conversa na lista das mensagens diretas.</summary>
public sealed class DirectConversationRow(Guid id) : RoomRowBase
{
    private string _name = string.Empty;
    private string _preview = string.Empty;
    private DateTimeOffset _lastActivity;
    private int _unread;
    private bool _online;
    private string _status = string.Empty;
    private bool _selected;
    private bool _keyChanged;
    private bool _visible = true;
    private ImageSource? _photo;

    public Guid Id { get; } = id;
    public AvatarColors Avatar { get; } = AvatarColors.For(id);
    public string Name { get => _name; set => Set(ref _name, value, nameof(Name), nameof(Initial)); }
    public string Initial => AvatarColors.Initial(_name);
    public ImageSource? Photo { get => _photo; set => Set(ref _photo, value, nameof(Photo)); }
    /// <summary>O começo da última mensagem.</summary>
    public string Preview { get => _preview; set => Set(ref _preview, value, nameof(Preview)); }
    public DateTimeOffset LastActivity { get => _lastActivity; set => Set(ref _lastActivity, value, nameof(LastActivity), nameof(TimeText)); }
    public int Unread { get => _unread; set => Set(ref _unread, value, nameof(Unread), nameof(HasUnread), nameof(UnreadText)); }
    public bool HasUnread => _unread > 0;
    public string UnreadText => _unread > 99 ? "99+" : _unread.ToString();
    public bool IsOnline { get => _online; set => Set(ref _online, value, nameof(IsOnline)); }
    /// <summary>"Online em Amigos do CS", "Offline · a mensagem vai quando ela aparecer".</summary>
    public string StatusText { get => _status; set => Set(ref _status, value, nameof(StatusText)); }
    public bool IsSelected { get => _selected; set => Set(ref _selected, value, nameof(IsSelected)); }
    /// <summary>A pessoa apareceu com outra chave: a conversa mostra o aviso.</summary>
    public bool KeyChanged { get => _keyChanged; set => Set(ref _keyChanged, value, nameof(KeyChanged)); }
    /// <summary>Já tem a chave da pessoa (ela está ou esteve num ScreenFlow 1.5.0).</summary>
    public bool HasKey { get; set; }
    /// <summary>Aparece na lista (a busca esconde as outras).</summary>
    public bool IsVisible { get => _visible; set => Set(ref _visible, value, nameof(IsVisible)); }

    /// <summary>"21:05" hoje, "ontem", "24/11" antes.</summary>
    public string TimeText
    {
        get
        {
            if (_lastActivity == default) return string.Empty;
            var local = _lastActivity.ToLocalTime();
            if (local.Date == DateTime.Today) return local.ToString("HH:mm");
            return local.Date == DateTime.Today.AddDays(-1) ? "ontem" : local.ToString("dd/MM");
        }
    }

    /// <summary>As mensagens de exemplo (só nas capturas do CI).</summary>
    internal List<DirectRecord> DemoMessages { get; } = [];
}
