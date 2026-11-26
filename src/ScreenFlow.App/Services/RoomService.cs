using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Net;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Threading;
using ScreenFlow.Core.Diagnostics;
using ScreenFlow.Core.Media;
using ScreenFlow.Core.Network;
using ScreenFlow.Core.Room;
using ScreenFlow.Core.Settings;
using ScreenFlow.Infrastructure.Network;
using ScreenFlow.Infrastructure.Room;

namespace ScreenFlow.App.Services;

/// <summary>
/// A sala da rede vista pela interface: quem está na rede, quem está na voz e o chat, em listas que as telas ligam
/// direto. Tudo aqui roda na thread da interface; o que chega da rede é trazido para cá antes de mexer nas listas.
/// </summary>
public sealed class RoomService
{
    private static readonly TimeSpan GroupWindow = TimeSpan.FromMinutes(5);
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _speakingTimer;
    private readonly Dictionary<Guid, RoomPeer> _peers = [];
    private readonly Dictionary<Guid, DateTimeOffset> _liveNoticed = [];
    /// <summary>Fotos do perfil já abertas (id do arquivo → imagem; null quando o arquivo não é uma imagem).</summary>
    private readonly Dictionary<string, ImageSource?> _photos = [];
    /// <summary>A última foto que cada pessoa mostrou (fica para as mensagens de quem já saiu).</summary>
    private readonly Dictionary<Guid, string?> _avatars = [];
    private NetworkRoom? _room;
    private int _refreshQueued;
    private int _unread;
    private bool _joining;
    private bool _demo;
    private bool _demoVoice;
    private bool _demoMuted;
    private bool _demoDeafened;

    public RoomService(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
        _speakingTimer = new DispatcherTimer(DispatcherPriority.Background, dispatcher) { Interval = TimeSpan.FromMilliseconds(150) };
        _speakingTimer.Tick += (_, _) => UpdateSpeaking();
    }

    /// <summary>Os outros PCs da sala (você não entra nesta lista).</summary>
    public ObservableCollection<RoomPersonRow> People { get; } = [];

    /// <summary>Quem está na voz agora, você incluído, em ordem de nome.</summary>
    public ObservableCollection<VoiceMemberRow> VoiceMembers { get; } = [];

    public ObservableCollection<ChatRow> Messages { get; } = [];

    public Guid SelfId { get; private set; }
    public string SelfName => AppServices.Settings.Current.DeviceName;

    /// <summary>A sua foto do perfil (null sem foto).</summary>
    public ImageSource? SelfPhoto => PhotoFor(AppServices.Settings.Current.AvatarId);

    /// <summary>O seu nome ou a sua foto mudaram (o menu lateral se atualiza).</summary>
    public event Action? ProfileChanged;

    /// <summary>A sala abriu e está funcionando (ou são os dados de exemplo das capturas).</summary>
    public bool IsAvailable => _demo || _room?.IsRunning == true;

    /// <summary>Por que a sala não está funcionando (null quando está).</summary>
    public string? Error => _demo || _room?.IsRunning == true ? null : _room?.Error ?? "A sala da rede ainda não abriu.";

    public bool InVoice => _demo ? _demoVoice : _room?.InVoice == true;
    public bool Muted => _demo ? _demoMuted : _room?.Muted == true;
    public bool Deafened => _demo ? _demoDeafened : _room?.Deafened == true;
    public bool IsJoining => _joining;

    /// <summary>Mensagens de outras pessoas que chegaram com o chat fechado.</summary>
    public int Unread => _unread;

    /// <summary>A tela do chat está aberta e na frente (quem decide é a janela principal).</summary>
    public Func<bool>? IsChatInFront { get; set; }

    /// <summary>Pessoas, voz ou mudo mudaram: o painel da voz e a bandeja se atualizam.</summary>
    public event Action? Changed;
    public event Action? UnreadChanged;

    /// <summary>Mensagem nova de outra pessoa (não chega para as suas nem para as antigas).</summary>
    public event Action<ChatRow>? MessageArrived;

    /// <summary>Alguém da sala começou a transmitir.</summary>
    public event Action<RoomPersonRow>? PersonWentLive;

    /// <summary>A lista de transmissões anunciadas na rede mudou (tela Assistir).</summary>
    public event Action? HostsChanged;

    /// <summary>Transmissões anunciadas na rede nos últimos segundos (de qualquer versão).</summary>
    public IReadOnlyList<DiscoveredHost> Hosts => _room?.Hosts ?? [];

    // ───────────────────────── Ciclo de vida ─────────────────────────

    public void Start()
    {
        if (_room is not null || _demo) return;
        var settings = AppServices.Settings.Current;
        settings.Normalize();
        SelfId = Guid.ParseExact(settings.InstanceId!, "N");
        var blobs = AppServices.Blobs;
        var keep = settings.AvatarId;
        _ = Task.Run(() => blobs.Trim([keep]));
        var room = new NetworkRoom(SelfId, settings.DiscoveryPort, new RoomLocalState(settings.DeviceName, Avatar: SelfAvatarRef()))
        {
            MicrophoneId = settings.MicrophoneId,
            Volume = (float)settings.VoiceVolume,
            Sensitivity = settings.VoiceSensitivity,
            SoundsEnabled = settings.VoiceSounds,
            // Quem pede um arquivo que este PC tem (a sua foto, uma imagem do chat) recebe daqui.
            BlobSource = blobs.Get,
            HasBlob = blobs.Has,
        };
        room.SetMuted(settings.VoiceMuted);
        room.PeersChanged += QueueRefresh;
        room.VoiceChanged += QueueRefresh;
        room.MessageReceived += message => _dispatcher.BeginInvoke(() => OnMessage(message));
        room.BlobReceived += (id, data) =>
        {
            blobs.Save(id, data);
            _dispatcher.BeginInvoke(() => OnBlobArrived(id));
        };
        room.BlobFailed += id => _dispatcher.BeginInvoke(() => OnBlobFailed(id));
        room.PeerWentLive += peer => _dispatcher.BeginInvoke(() => OnPeerWentLive(peer.Id));
        room.HostsChanged += () => _dispatcher.BeginInvoke(() => HostsChanged?.Invoke());
        _room = room;
        room.Start();
        foreach (var text in settings.RoomPeers)
            if (IPAddress.TryParse(text, out var address)) room.AddManualPeer(address);
        AppServices.Settings.Saved += OnSettingsSaved;
        // O id deste PC na sala precisa continuar o mesmo nas próximas vezes.
        try { AppServices.Settings.Save(); } catch (Exception exception) { Log.Warn($"Configurações não salvas: {exception.Message}"); }
        QueueRefresh();
    }

    public async Task StopAsync()
    {
        AppServices.Settings.Saved -= OnSettingsSaved;
        _speakingTimer.Stop();
        var room = _room;
        _room = null;
        if (room is not null) await room.DisposeAsync();
    }

    private void OnSettingsSaved(AppSettings settings)
    {
        if (!_dispatcher.CheckAccess())
        {
            _dispatcher.BeginInvoke(() => OnSettingsSaved(settings));
            return;
        }
        ProfileChanged?.Invoke();
        if (_room is not { } room) return;
        room.Volume = (float)settings.VoiceVolume;
        room.Sensitivity = settings.VoiceSensitivity;
        room.SoundsEnabled = settings.VoiceSounds;
        var avatar = SelfAvatarRef();
        if (room.Local.Name != settings.DeviceName || room.Local.Avatar != avatar)
        {
            room.UpdateLocal(room.Local with { Name = settings.DeviceName, Avatar = avatar });
            Refresh();
        }
        if (room.MicrophoneId != settings.MicrophoneId)
        {
            room.MicrophoneId = settings.MicrophoneId;
            if (room.InVoice) _ = RejoinAsync(); // troca o microfone na hora
        }
    }

    private async Task RejoinAsync()
    {
        await LeaveVoiceAsync();
        if (await JoinVoiceAsync() is { } error) Log.Warn($"Troca de microfone: {error}");
    }

    /// <summary>O que este PC mostra para a sala (transmissão, assistindo). O nome e a foto vêm sempre das configurações.</summary>
    public void UpdateLocal(RoomLocalState state)
    {
        if (_room is not { } room) return;
        room.UpdateLocal(state with { Name = SelfName, Avatar = SelfAvatarRef() });
    }

    // ───────────────────────── Perfil ─────────────────────────

    /// <summary>Troca a sua foto (null tira). Vale na hora: quem está na sala baixa a nova.</summary>
    public void SetPhoto(byte[]? avatar)
    {
        var settings = AppServices.Settings.Current;
        settings.AvatarId = avatar is null ? null : AppServices.Blobs.Put(avatar);
        AppServices.Settings.Save();
        if (_demo) ProfileChanged?.Invoke();
    }

    /// <summary>A sua foto como a sala anuncia (id e tamanho), se o arquivo dela estiver aqui.</summary>
    private static RoomBlobRef? SelfAvatarRef()
    {
        var id = AppServices.Settings.Current.AvatarId;
        return id is not null && AppServices.Blobs.SizeOf(id) is { } size and <= RoomBlob.MaxAvatarBytes ? new RoomBlobRef(id, size) : null;
    }

    /// <summary>A foto do perfil com este id, se o arquivo já estiver neste PC (senão null: aparece a inicial).</summary>
    public ImageSource? PhotoFor(string? id)
    {
        if (id is null) return null;
        if (_photos.TryGetValue(id, out var cached)) return cached;
        if (AppServices.Blobs.Get(id) is not { } data) return null; // ainda não chegou: tenta de novo quando chegar
        var photo = ImageTools.Decode(data, ImageTools.AvatarSide);
        if (_photos.Count >= 256) _photos.Clear();
        _photos[id] = photo;
        return photo;
    }

    private ImageSource? PhotoOf(Guid person) => PhotoFor(person == SelfId ? AppServices.Settings.Current.AvatarId : _avatars.GetValueOrDefault(person));

    /// <summary>Um arquivo chegou: pode ser a foto de alguém ou uma imagem do chat.</summary>
    private void OnBlobArrived(string id)
    {
        _photos.Remove(id);
        Refresh();
        foreach (var row in Messages)
            if (row.Image?.Id == id) LoadPicture(row);
    }

    private void OnBlobFailed(string id)
    {
        foreach (var row in Messages)
            if (row.Image?.Id == id && row.Picture is null) row.ImageState = ChatImageState.Failed;
    }

    // ───────────────────────── Voz ─────────────────────────

    /// <summary>Entra na voz da rede. Retorna o motivo quando não deu (microfone ausente ou bloqueado).</summary>
    public async Task<string?> JoinVoiceAsync()
    {
        if (_demo)
        {
            _demoVoice = true;
            Changed?.Invoke();
            return null;
        }
        if (_room is not { IsRunning: true } room) return Error;
        if (room.InVoice || _joining) return null;
        _joining = true;
        Changed?.Invoke();
        try
        {
            await Task.Run(() => room.JoinVoice());
            return null;
        }
        catch (Exception exception)
        {
            Log.Warn($"Não deu para entrar na voz: {exception.Message}");
            return MicrophoneProblem(exception);
        }
        finally
        {
            _joining = false;
            QueueRefresh();
        }
    }

    public async Task LeaveVoiceAsync()
    {
        if (_demo)
        {
            _demoVoice = false;
            Changed?.Invoke();
            return;
        }
        if (_room is { } room) await room.LeaveVoiceAsync();
    }

    /// <summary>Liga ou desliga o microfone. Ligar com o fone cortado religa o fone também (como no Discord).</summary>
    public void SetMuted(bool muted)
    {
        AppServices.Settings.Current.VoiceMuted = muted;
        AppServices.Settings.Save();
        var deafened = muted && Deafened;
        if (_demo)
        {
            (_demoMuted, _demoDeafened) = (muted, deafened);
            Changed?.Invoke();
            return;
        }
        _room?.SetVoiceState(muted, deafened);
    }

    /// <summary>Corta o som de todo mundo (e o seu microfone). Religar volta o microfone como estava.</summary>
    public void SetDeafened(bool deafened)
    {
        if (_demo)
        {
            _demoDeafened = deafened;
            Changed?.Invoke();
            return;
        }
        _room?.SetDeafened(deafened);
    }

    /// <summary>O botão do microfone: mostra cortado também quando o fone está cortado.</summary>
    public bool MicrophoneOff => Muted || Deafened;

    public void ToggleMute() => SetMuted(!MicrophoneOff);

    public void ToggleDeafen() => SetDeafened(!Deafened);

    /// <summary>Volume do microfone agora (dBFS) quando você está na voz, para o medidor das configurações.</summary>
    public double? VoiceInputLevelDb => _room?.Voice?.InputLevelDb;

    /// <summary>Explica em português por que o microfone não abriu.</summary>
    public static string MicrophoneProblem(Exception exception)
    {
        var code = (exception as COMException)?.HResult ?? exception.HResult;
        return code switch
        {
            unchecked((int)0x80070490) => "Nenhum microfone foi encontrado. Conecte um microfone (ou fone com microfone) e tente de novo.",
            unchecked((int)0x80070005) => "O Windows bloqueou o microfone. Abra Configurações › Privacidade e segurança › Microfone e permita o acesso dos aplicativos da área de trabalho.",
            unchecked((int)0x88890004) => "O microfone foi desconectado. Conecte de novo e tente outra vez.",
            unchecked((int)0x8889000A) => "Outro programa está usando o microfone sozinho. Feche-o e tente de novo.",
            _ => $"O microfone não abriu: {exception.Message}",
        };
    }

    // ───────────────────────── Chat ─────────────────────────

    /// <summary>
    /// Manda para todo mundo da sala, com uma imagem se quiser (quem recebe baixa a imagem deste PC). false quando a
    /// sala está desligada ou não há nada para mandar.
    /// </summary>
    public bool Send(string text, PreparedImage? image = null)
    {
        ChatImage? attached = null;
        if (image is not null)
        {
            var id = AppServices.Blobs.Put(image.Data);
            attached = new ChatImage(id, image.Data.Length, image.Width, image.Height);
        }
        if (_demo)
        {
            var clean = RoomPacket.CleanText(text);
            if (clean.Length == 0 && attached is null) return false;
            AddMessage(new RoomChatMessage(SelfId, SelfName, (ulong)Random.Shared.NextInt64(), DateTimeOffset.Now,
                clean.Length == 0 ? RoomPacket.ImagePlaceholder : clean, Image: attached));
            return true;
        }
        return _room is { IsRunning: true } room && room.SendChat(text, attached) is not null;
    }

    /// <summary>A imagem não veio: pede de novo, a quem mandou e a todo mundo da sala (quem já baixou também tem).</summary>
    public void RetryImage(ChatRow row)
    {
        if (row.Image is not { } image || _room is not { } room) return;
        row.ImageState = ChatImageState.Loading;
        if (AppServices.Blobs.Has(image.Id))
        {
            LoadPicture(row);
            return;
        }
        room.FetchBlob(image.Blob, [row.AuthorId, .. People.Select(p => p.Id).Where(id => id != row.AuthorId)]);
    }

    /// <summary>Abre a imagem (já baixada) de uma mensagem para mostrar.</summary>
    private void LoadPicture(ChatRow row)
    {
        if (row.Image is not { } image) return;
        if (AppServices.Blobs.Get(image.Id) is not { } data)
        {
            if (row.ImageState != ChatImageState.Failed) row.ImageState = ChatImageState.Loading;
            return;
        }
        row.Picture = ImageTools.Decode(data, Math.Min(image.Width, ChatRow.PictureDecodeWidth));
        row.ImageState = row.Picture is null ? ChatImageState.Failed : ChatImageState.Ready;
    }

    public void MarkRead()
    {
        if (_unread == 0) return;
        _unread = 0;
        UnreadChanged?.Invoke();
    }

    private void OnMessage(RoomChatMessage message)
    {
        var row = AddMessage(message);
        if (row is null || row.IsMine || message.History) return;
        if (IsChatInFront?.Invoke() != true)
        {
            _unread++;
            UnreadChanged?.Invoke();
        }
        MessageArrived?.Invoke(row);
    }

    private ChatRow? AddMessage(RoomChatMessage message)
    {
        foreach (var existing in Messages)
            if (existing.AuthorId == message.AuthorId && existing.MessageId == message.MessageId) return null;
        var row = new ChatRow(message, message.AuthorId == SelfId) { Photo = PhotoOf(message.AuthorId) };
        LoadPicture(row);
        var index = Messages.Count;
        while (index > 0 && Messages[index - 1].Time > row.Time) index--;
        Messages.Insert(index, row);
        while (Messages.Count > ChatHistory.Capacity)
        {
            Messages.RemoveAt(0);
            index--;
        }
        Regroup(Math.Max(0, index));
        return row;
    }

    /// <summary>Mensagens seguidas da mesma pessoa (até 5 minutos) ficam juntas, sem repetir nome e foto.</summary>
    private void Regroup(int index)
    {
        for (var i = Math.Max(0, index); i <= Math.Min(Messages.Count - 1, index + 1); i++)
        {
            var previous = i > 0 ? Messages[i - 1] : null;
            var current = Messages[i];
            current.ShowHeader = previous is null || previous.AuthorId != current.AuthorId || current.Time - previous.Time > GroupWindow
                || previous.Time.ToLocalTime().Date != current.Time.ToLocalTime().Date;
        }
    }

    // ───────────────────────── Pessoas ─────────────────────────

    /// <summary>Adiciona um PC pelo IP (quando o aviso automático não chega). Retorna o erro, ou null.</summary>
    public string? AddPeer(string text)
    {
        if (!RadminAddress.TryParseHost(text.Trim(), out var address)) return "Digite um IP válido, por exemplo 26.12.34.56.";
        if (_room is not { IsRunning: true } room) return Error;
        room.AddManualPeer(address);
        var settings = AppServices.Settings.Current;
        var value = address.ToString();
        if (!settings.RoomPeers.Contains(value, StringComparer.OrdinalIgnoreCase))
        {
            settings.RoomPeers.Insert(0, value);
            AppServices.Settings.Save();
        }
        return null;
    }

    private void QueueRefresh()
    {
        if (Interlocked.Exchange(ref _refreshQueued, 1) == 1) return;
        _dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            Interlocked.Exchange(ref _refreshQueued, 0);
            Refresh();
        });
    }

    private void Refresh()
    {
        if (_room is not { } room) return;
        var peers = room.Peers;
        _peers.Clear();
        foreach (var peer in peers)
        {
            _peers[peer.Id] = peer;
            _avatars[peer.Id] = peer.Presence.Avatar?.Id;
        }
        Sync(People, peers.Select(p => (p.Id, p)), id => new RoomPersonRow(id), (row, peer) =>
        {
            row.Update(peer);
            row.Photo = PhotoFor(peer.Presence.Avatar?.Id);
        });

        var voice = peers.Where(p => p.Presence.InVoice)
            .Select(p => new VoiceMemberState(p.Id, p.Presence.Name, p.Presence.Muted, p.Presence.Deafened, false))
            .ToList();
        if (room.InVoice) voice.Add(new VoiceMemberState(SelfId, SelfName, room.Muted || room.Deafened, room.Deafened, true));
        voice.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase));
        Sync(VoiceMembers, voice.Select(v => (v.Id, v)), id => new VoiceMemberRow(id), (row, state) =>
        {
            row.Update(state);
            row.Photo = PhotoOf(state.Id);
        });
        var photos = new Dictionary<Guid, ImageSource?>();
        foreach (var message in Messages)
        {
            if (!photos.TryGetValue(message.AuthorId, out var photo)) photos[message.AuthorId] = photo = PhotoOf(message.AuthorId);
            message.Photo = photo;
        }

        _speakingTimer.IsEnabled = room.InVoice;
        if (!room.InVoice) foreach (var row in VoiceMembers) row.Speaking = false;
        Changed?.Invoke();
    }

    private void UpdateSpeaking()
    {
        if (_room is not { } room) return;
        var now = DateTimeOffset.UtcNow;
        var self = room.Voice?.IsSpeaking == true;
        foreach (var row in VoiceMembers)
            row.Speaking = row.IsMe ? self : _peers.TryGetValue(row.Id, out var peer) && peer.IsSpeaking(now);
    }

    private void OnPeerWentLive(Guid id)
    {
        Refresh();
        if (People.FirstOrDefault(p => p.Id == id) is not { IsLive: true } person) return;
        var now = DateTimeOffset.UtcNow;
        // Quem liga e desliga várias vezes seguidas não vira uma enxurrada de avisos.
        if (_liveNoticed.TryGetValue(id, out var last) && now - last < TimeSpan.FromMinutes(2)) return;
        _liveNoticed[id] = now;
        PersonWentLive?.Invoke(person);
    }

    /// <summary>Atualiza a lista no lugar (sem recriar as linhas), na ordem de <paramref name="items"/>.</summary>
    private static void Sync<TRow, TData>(ObservableCollection<TRow> rows, IEnumerable<(Guid Id, TData Data)> items, Func<Guid, TRow> create, Action<TRow, TData> update)
        where TRow : IRoomRow
    {
        var list = items.ToList();
        var ids = list.Select(i => i.Id).ToHashSet();
        for (var i = rows.Count - 1; i >= 0; i--)
            if (!ids.Contains(rows[i].Id)) rows.RemoveAt(i);
        for (var index = 0; index < list.Count; index++)
        {
            var (id, data) = list[index];
            var current = -1;
            for (var j = index; j < rows.Count; j++)
            {
                if (rows[j].Id != id) continue;
                current = j;
                break;
            }
            TRow row;
            if (current < 0)
            {
                row = create(id);
                rows.Insert(index, row);
            }
            else
            {
                row = rows[current];
                if (current != index) rows.Move(current, index);
            }
            update(row, data);
        }
    }

    // ───────────────────────── Capturas de tela ─────────────────────────

    /// <summary>Sala de exemplo para as capturas do CI: quatro pessoas, três na voz e uma conversa.</summary>
    public void LoadDemo(bool inVoice = true, int unread = 2)
    {
        _demo = true;
        SelfId = new Guid("6b1f1d5e-0c4a-4f7e-9d3b-2a8c5e7f9a0f");
        _demoVoice = inVoice;
        var ana = new Guid("0d6f3a52-8b1e-4c9a-a7f2-5e3b9c1d2f42");
        var bruno = new Guid("9a2c7e14-3f5b-4d8a-b6e1-7c4f2a9d3e51");
        var carla = new Guid("4e8b1c63-7a2d-4f9e-8c5b-1d6a3f7e2b5f");
        var diego = new Guid("c3a9e275-1b6f-4a8d-9e2c-6f1b8d4a7c73");
        var (anaPhoto, brunoPhoto, wallpaper) = DemoImages();
        _avatars[ana] = anaPhoto;
        _avatars[bruno] = brunoPhoto;
        People.Clear();
        People.Add(RoomPersonRow.Demo(ana, "Ana Lu", "Ao vivo · tela, câmera e áudio", true, StreamMode.Screen, "26.47.19.113", false, PhotoOf(ana)));
        People.Add(RoomPersonRow.Demo(bruno, "Bruno", "Na voz", false, StreamMode.Screen, null, true, PhotoOf(bruno)));
        People.Add(RoomPersonRow.Demo(carla, "Carla-PC", "Assistindo Ana Lu", false, StreamMode.Screen, null, true));
        People.Add(RoomPersonRow.Demo(diego, "Diego", "Online", false, StreamMode.Screen, null, false));
        VoiceMembers.Clear();
        VoiceMembers.Add(VoiceMemberRow.Demo(bruno, "Bruno", speaking: inVoice, muted: false, photo: PhotoOf(bruno)));
        VoiceMembers.Add(VoiceMemberRow.Demo(carla, "Carla-PC", speaking: false, muted: true));
        if (inVoice) VoiceMembers.Insert(2, VoiceMemberRow.Demo(SelfId, SelfName, speaking: false, muted: false, isMe: true, photo: SelfPhoto));
        Messages.Clear();
        var today = DateTimeOffset.Now.Date;
        var at = (int h, int m) => new DateTimeOffset(today.AddHours(h).AddMinutes(m));
        AddMessage(new RoomChatMessage(ana, "Ana Lu", 1, at(20, 2), "Boa noite, gente! Vou abrir a transmissão do jogo daqui a pouco"));
        AddMessage(new RoomChatMessage(bruno, "Bruno", 2, at(20, 3), "bora! entra na voz que fica mais fácil"));
        AddMessage(new RoomChatMessage(bruno, "Bruno", 3, at(20, 3), "o som tá saindo bem aqui"));
        AddMessage(new RoomChatMessage(SelfId, SelfName, 4, at(20, 5), "Tô entrando. Ana, liga a câmera junto com a tela?"));
        AddMessage(new RoomChatMessage(ana, "Ana Lu", 5, at(20, 6), "Liguei! Agora vai tela, câmera e áudio ao mesmo tempo 🎮"));
        if (wallpaper is not null)
            AddMessage(new RoomChatMessage(ana, "Ana Lu", 7, at(20, 6), "O fundo que eu fiz pra live de hoje", Image: wallpaper));
        AddMessage(new RoomChatMessage(carla, "Carla-PC", 6, at(20, 7), "Já estou assistindo, ficou ótimo"));
        _unread = unread;
        Changed?.Invoke();
        UnreadChanged?.Invoke();
        ProfileChanged?.Invoke();
    }

    /// <summary>Fotos e uma imagem de exemplo (recortes do fundo do app), guardadas como se tivessem chegado pela rede.</summary>
    private static (string? Ana, string? Bruno, ChatImage? Wallpaper) DemoImages()
    {
        try
        {
            var picture = DemoData.ChatPicture();
            var id = AppServices.Blobs.Put(picture.Data);
            return (AppServices.Blobs.Put(DemoData.ProfilePhoto(860, 40, 300)), AppServices.Blobs.Put(DemoData.ProfilePhoto(880, 430, 320)),
                new ChatImage(id, picture.Data.Length, picture.Width, picture.Height));
        }
        catch (Exception exception)
        {
            Log.Warn($"Imagens de exemplo: {exception.Message}");
            return (null, null, null);
        }
    }
}

public interface IRoomRow
{
    Guid Id { get; }
}

/// <summary>Cor do avatar de alguém: sempre a mesma para o mesmo PC.</summary>
public sealed record AvatarColors(Brush Background, Brush Foreground)
{
    private static readonly AvatarColors[] Palette =
    [
        Make("#2EE6A6", "#22D3EE", "#03231A"),
        Make("#8B6CFF", "#FF4D8D", "#FFFFFF"),
        Make("#22D3EE", "#5B9BFF", "#04202A"),
        Make("#FFB547", "#FF4D8D", "#2A1204"),
        Make("#5B9BFF", "#8B6CFF", "#FFFFFF"),
        Make("#FF4D8D", "#FF7A59", "#FFFFFF"),
    ];

    public static AvatarColors For(Guid id)
    {
        var bytes = id.ToByteArray();
        var sum = 0;
        foreach (var b in bytes) sum += b;
        return Palette[sum % Palette.Length];
    }

    private static AvatarColors Make(string from, string to, string text)
    {
        var background = new LinearGradientBrush((Color)ColorConverter.ConvertFromString(from), (Color)ColorConverter.ConvertFromString(to), 45);
        background.Freeze();
        var foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(text));
        foreground.Freeze();
        return new AvatarColors(background, foreground);
    }

    /// <summary>Primeira letra (ou número) do nome, para o avatar.</summary>
    public static string Initial(string name)
    {
        foreach (var c in name)
            if (char.IsLetterOrDigit(c)) return char.ToUpperInvariant(c).ToString();
        return "?";
    }
}

public abstract class RoomRowBase : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void Set<T>(ref T field, T value, params string[] names)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        foreach (var name in names) PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}

/// <summary>Uma pessoa da sala na lista do chat.</summary>
public sealed class RoomPersonRow(Guid id) : RoomRowBase, IRoomRow
{
    private string _name = string.Empty;
    private string _status = "Online";
    private bool _live;
    private bool _inVoice;
    private bool _muted;
    private StreamMode _mode;
    private string? _watchAddress;
    private ImageSource? _photo;

    public Guid Id { get; } = id;
    public AvatarColors Avatar { get; } = AvatarColors.For(id);
    public string Name { get => _name; private set => Set(ref _name, value, nameof(Name), nameof(Initial)); }
    public string Initial => AvatarColors.Initial(_name);
    /// <summary>A foto do perfil (null: sem foto, ou ainda baixando).</summary>
    public ImageSource? Photo { get => _photo; set => Set(ref _photo, value, nameof(Photo)); }
    public string StatusText { get => _status; private set => Set(ref _status, value, nameof(StatusText)); }
    public bool IsLive { get => _live; private set => Set(ref _live, value, nameof(IsLive), nameof(ActionText)); }
    public bool InVoice { get => _inVoice; private set => Set(ref _inVoice, value, nameof(InVoice)); }
    public bool Muted { get => _muted; private set => Set(ref _muted, value, nameof(Muted)); }
    public StreamMode LiveMode { get => _mode; private set => Set(ref _mode, value, nameof(LiveMode), nameof(ActionText)); }
    public string? WatchAddress { get => _watchAddress; private set => Set(ref _watchAddress, value, nameof(WatchAddress)); }
    public string ActionText => LiveMode == StreamMode.AudioOnly ? "Ouvir" : "Assistir";

    public void Update(RoomPeer peer)
    {
        var presence = peer.Presence;
        Name = presence.Name;
        IsLive = presence.Live;
        LiveMode = presence.LiveMode;
        InVoice = presence.InVoice;
        Muted = presence.Muted;
        WatchAddress = presence.Live ? peer.WatchAddress.ToString() : null;
        var status = presence.Live
            ? (presence.LiveMode == StreamMode.AudioOnly ? "Ao vivo · festa de música" : $"Ao vivo · {presence.LiveSummary}")
              + (presence.Flags.HasFlag(PresenceFlags.PinRequired) ? " · com PIN" : string.Empty)
            : presence.Watching ? "Assistindo uma transmissão" : "Online";
        if (presence.InVoice) status += presence.Deafened ? " · na voz, sem som" : presence.Muted ? " · na voz, mudo" : " · na voz";
        StatusText = status;
    }

    internal static RoomPersonRow Demo(Guid id, string name, string status, bool live, StreamMode mode, string? address, bool inVoice, ImageSource? photo = null)
        => new(id) { Name = name, StatusText = status, IsLive = live, LiveMode = mode, WatchAddress = address, InVoice = inVoice, Photo = photo };
}

public readonly record struct VoiceMemberState(Guid Id, string Name, bool Muted, bool Deafened, bool IsMe);

/// <summary>Alguém na voz, no painel do menu lateral.</summary>
public sealed class VoiceMemberRow(Guid id) : RoomRowBase, IRoomRow
{
    private string _name = string.Empty;
    private bool _muted;
    private bool _deafened;
    private bool _speaking;
    private bool _isMe;
    private ImageSource? _photo;

    public Guid Id { get; } = id;
    public AvatarColors Avatar { get; } = AvatarColors.For(id);
    public string Name { get => _name; private set => Set(ref _name, value, nameof(Name), nameof(Initial), nameof(DisplayName)); }
    public string Initial => AvatarColors.Initial(_name);
    public ImageSource? Photo { get => _photo; set => Set(ref _photo, value, nameof(Photo)); }
    public string DisplayName => _isMe ? $"{_name} (você)" : _name;
    public bool IsMe { get => _isMe; private set => Set(ref _isMe, value, nameof(IsMe), nameof(DisplayName)); }
    public bool Muted { get => _muted; private set => Set(ref _muted, value, nameof(Muted), nameof(ShowMuted)); }
    public bool Deafened { get => _deafened; private set => Set(ref _deafened, value, nameof(Deafened), nameof(ShowMuted)); }
    /// <summary>O ícone de microfone cortado aparece só quando não está com o fone cortado (que já inclui o mudo).</summary>
    public bool ShowMuted => _muted && !_deafened;
    public bool Speaking { get => _speaking; set => Set(ref _speaking, value, nameof(Speaking)); }

    public void Update(VoiceMemberState state)
    {
        Name = state.Name;
        IsMe = state.IsMe;
        Muted = state.Muted;
        Deafened = state.Deafened;
        if (state.Muted || state.Deafened) Speaking = false;
    }

    internal static VoiceMemberRow Demo(Guid id, string name, bool speaking, bool muted, bool isMe = false, ImageSource? photo = null)
        => new(id) { Name = name, Speaking = speaking, Muted = muted, IsMe = isMe, Photo = photo };
}

public enum ChatImageState { None, Loading, Ready, Failed }

/// <summary>Uma mensagem do chat da rede.</summary>
public sealed class ChatRow : RoomRowBase
{
    /// <summary>A caixa da imagem na conversa (a imagem diminui para caber, sem mudar o formato).</summary>
    public const double PictureMaxWidth = 340;
    public const double PictureMaxHeight = 280;
    /// <summary>Largura em pixels com que a imagem é aberta para mostrar (nítida até 150% de escala, sem gastar memória à toa).</summary>
    public const int PictureDecodeWidth = 720;

    private bool _showHeader = true;
    private ImageSource? _photo;
    private ImageSource? _picture;
    private ChatImageState _imageState;

    public ChatRow(RoomChatMessage message, bool isMine)
    {
        AuthorId = message.AuthorId;
        MessageId = message.MessageId;
        Author = message.AuthorName;
        Text = message.Caption;
        Image = message.Image;
        Time = message.Time;
        IsMine = isMine;
        Avatar = AvatarColors.For(message.AuthorId);
        _imageState = Image is null ? ChatImageState.None : ChatImageState.Loading;
        if (Image is { } image)
        {
            var scale = Math.Min(1, Math.Min(PictureMaxWidth / Math.Max(1, image.Width), PictureMaxHeight / Math.Max(1, image.Height)));
            PictureWidth = Math.Max(48, Math.Round(image.Width * scale));
            PictureHeight = Math.Max(48, Math.Round(image.Height * scale));
        }
    }

    public Guid AuthorId { get; }
    public ulong MessageId { get; }
    public string Author { get; }
    public string Initial => AvatarColors.Initial(Author);
    public string AuthorText => IsMine ? $"{Author} (você)" : Author;
    /// <summary>O texto (a legenda, quando tem imagem; vazio quando a imagem foi sozinha).</summary>
    public string Text { get; }
    public bool HasText => Text.Length > 0;
    public DateTimeOffset Time { get; }
    public bool IsMine { get; }
    public AvatarColors Avatar { get; }
    public ImageSource? Photo { get => _photo; set => Set(ref _photo, value, nameof(Photo)); }

    public ChatImage? Image { get; }
    public bool HasImage => Image is not null;
    public double PictureWidth { get; }
    public double PictureHeight { get; }
    public ImageSource? Picture { get => _picture; set => Set(ref _picture, value, nameof(Picture)); }

    public ChatImageState ImageState
    {
        get => _imageState;
        set => Set(ref _imageState, value, nameof(ImageState), nameof(ImageLoading), nameof(ImageFailed));
    }

    public bool ImageLoading => _imageState == ChatImageState.Loading;
    public bool ImageFailed => _imageState == ChatImageState.Failed;

    /// <summary>"1920 × 1080 · 320 KB" (na dica da imagem).</summary>
    public string ImageInfo => Image is { } image ? $"{image.Width} × {image.Height} · {image.Size / 1024.0:0} KB" : string.Empty;

    /// <summary>"20:05" hoje; "24/11 20:05" em outro dia.</summary>
    public string TimeText
    {
        get
        {
            var local = Time.ToLocalTime();
            return local.Date == DateTime.Today ? local.ToString("HH:mm") : local.ToString("dd/MM HH:mm");
        }
    }

    public string FullTimeText => Time.ToLocalTime().ToString("dddd, dd/MM/yyyy 'às' HH:mm", System.Globalization.CultureInfo.GetCultureInfo("pt-BR"));

    /// <summary>Primeira de um grupo: mostra foto, nome e hora.</summary>
    public bool ShowHeader { get => _showHeader; set => Set(ref _showHeader, value, nameof(ShowHeader)); }
}
