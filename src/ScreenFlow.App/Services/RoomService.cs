using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
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
/// A sala da rede vista pela interface, como um servidor do Discord (1.3.0): os canais de texto com as conversas, os
/// canais de voz com quem está em cada um, quem está na rede e o chat guardado numa pasta. Tudo aqui roda na thread da
/// interface; o que chega da rede é trazido para cá antes de mexer nas listas.
/// </summary>
public sealed class RoomService
{
    private static readonly TimeSpan GroupWindow = TimeSpan.FromMinutes(5);
    private static readonly StringComparer Names = StringComparer.Create(CultureInfo.GetCultureInfo("pt-BR"), ignoreCase: true);

    /// <summary>Mensagens mostradas por canal de texto (as mais antigas saem da tela).</summary>
    public const int ChannelCapacity = 300;

    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _speakingTimer;
    private readonly Dictionary<Guid, RoomPeer> _peers = [];
    private readonly Dictionary<Guid, DateTimeOffset> _liveNoticed = [];
    /// <summary>Fotos do perfil já abertas (id do arquivo → imagem; null quando o arquivo não é uma imagem).</summary>
    private readonly Dictionary<string, ImageSource?> _photos = [];
    /// <summary>A última foto que cada pessoa mostrou (fica para as mensagens de quem já saiu).</summary>
    private readonly Dictionary<Guid, string?> _avatars = [];
    /// <summary>Os canais de texto pelo nome comparável, cada um com a sua conversa (mesmo fora da lista).</summary>
    private readonly Dictionary<string, TextChannelRow> _textRows = [];
    private readonly Dictionary<string, VoiceChannelRow> _voiceRows = [];
    /// <summary>Uma linha por pessoa na voz: a mesma aparece no canal dela e no painel do menu lateral.</summary>
    private readonly Dictionary<Guid, VoiceMemberRow> _memberRows = [];
    private readonly Dictionary<Guid, double> _volumeBeforeMute = [];
    /// <summary>Mensagens já na tela (quem mandou, id), para não repetir quando chegam de novo.</summary>
    private readonly HashSet<(Guid, ulong)> _shown = [];
    private readonly HashSet<string> _restoring = [];
    private List<string> _voiceInUse = [];
    private TextChannelRow _currentText;
    private NetworkRoom? _room;
    private ChatArchive? _archive;
    private (bool Enabled, string? Folder)? _archiveApplied;
    private int _archiveVersion;
    private int _refreshQueued;
    private int _photosVersion;
    private int _photosShown = -1;
    private bool _joining;
    private bool _demo;
    private bool _demoVoice;
    private string _demoVoiceChannel = RoomChannels.DefaultVoiceName;
    private bool _demoMuted;
    private bool _demoDeafened;
    private readonly List<(string Channel, VoiceMemberState State)> _demoMembers = [];
    private readonly List<(RoomChannel Channel, string Owner)> _demoChannels = [];

    public RoomService(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
        _speakingTimer = new DispatcherTimer(DispatcherPriority.Background, dispatcher) { Interval = TimeSpan.FromMilliseconds(150) };
        _speakingTimer.Tick += (_, _) => UpdateSpeaking();
        _currentText = TextRow(null);
        _currentText.IsSelected = true;
        RebuildChannels();
    }

    /// <summary>Os outros PCs da sala (você não entra nesta lista).</summary>
    public ObservableCollection<RoomPersonRow> People { get; } = [];

    /// <summary>
    /// Quem está no seu canal de voz, você incluído, em ordem de nome. Fora da voz, quem está no canal em que o botão
    /// Entrar põe você (<see cref="SuggestedVoiceChannel"/>).
    /// </summary>
    public ObservableCollection<VoiceMemberRow> VoiceMembers { get; } = [];

    /// <summary>Os canais de texto: os padrão primeiro, depois os criados pelo pessoal da sala.</summary>
    public ObservableCollection<TextChannelRow> TextChannels { get; } = [];

    /// <summary>Os canais de voz, cada um com quem está nele.</summary>
    public ObservableCollection<VoiceChannelRow> VoiceChannels { get; } = [];

    /// <summary>O canal de texto aberto no chat.</summary>
    public TextChannelRow CurrentText => _currentText;

    /// <summary>A conversa do canal de texto aberto.</summary>
    public ObservableCollection<ChatRow> Messages => _currentText.Messages;

    /// <summary>Trocou o canal de texto aberto.</summary>
    public event Action? CurrentTextChanged;

    /// <summary>O canal de voz em que você está (null fora da voz).</summary>
    public string? VoiceChannel => _demo ? (_demoVoice ? _demoVoiceChannel : null) : _room?.VoiceChannel;

    /// <summary>Fora da voz, o canal em que o botão Entrar põe você: o que tem mais gente, senão o último em que você esteve.</summary>
    public string SuggestedVoiceChannel { get; private set; } = RoomChannels.DefaultVoiceName;

    /// <summary>Quantas pessoas estão em algum canal de voz, você incluído.</summary>
    public int VoiceCount => _memberRows.Count;

    /// <summary>Quantas pessoas da sala estão numa versão sem canais (1.2 ou mais antiga): só veem o #geral.</summary>
    public int OldVersionCount => _peers.Values.Count(p => !p.Presence.SupportsChannels);

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

    /// <summary>Mensagens de outras pessoas que chegaram sem ninguém ver, somando todos os canais.</summary>
    public int Unread
    {
        get
        {
            var total = 0;
            foreach (var row in _textRows.Values) total += row.Unread;
            return total;
        }
    }

    /// <summary>A tela do chat está aberta e na frente (quem decide é a janela principal).</summary>
    public Func<bool>? IsChatInFront { get; set; }

    /// <summary>Pessoas, canais, voz ou mudo mudaram: o painel da voz e a bandeja se atualizam.</summary>
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
            SpeakerId = settings.SpeakerId,
            PushToTalk = settings.PushToTalk,
            Volume = (float)settings.VoiceVolume,
            Sensitivity = settings.VoiceSensitivity,
            SoundsEnabled = settings.VoiceSounds,
            // Quem pede um arquivo que este PC tem (a sua foto, uma imagem do chat) recebe daqui.
            BlobSource = blobs.Get,
            HasBlob = blobs.Has,
            CreatedChannels = settings.MyChannels,
        };
        // O volume de cada pessoa que você ajustou continua o mesmo nas próximas vezes.
        foreach (var (key, volume) in settings.PeerVolumes)
            if (Guid.TryParseExact(key, "N", out var peer)) room.SetPeerVolume(peer, (float)volume);
        room.SetMuted(settings.VoiceMuted);
        _talkKey = new PushToTalkKeyWatcher(held => _room?.SetTalkKey(held));
        // Sala privada e bloqueios (1.4.0).
        foreach (var key in settings.BlockedPeers)
            if (Guid.TryParseExact(key, "N", out var blocked)) room.SetBlocked(blocked, true);
        if (RoomInvite.TryParse(settings.PrivateRoomCode, out var invite))
            room.UsePrivateRoom(invite, settings.PrivateRoomApprovedOnly, Approved(settings));
        room.KeyChanged += next => _dispatcher.BeginInvoke(() => OnKeyChanged(next));
        room.PendingChanged += () => _dispatcher.BeginInvoke(OnPendingChanged);
        room.PeersChanged += QueueRefresh;
        room.VoiceChanged += QueueRefresh;
        room.VoiceChanged += () => _dispatcher.BeginInvoke(UpdateTalkKey);
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
        if (settings.LastTextChannel is { } last) SelectText(last);
        _ = UpdateArchiveAsync();
        QueueRefresh();
    }

    public async Task StopAsync()
    {
        AppServices.Settings.Saved -= OnSettingsSaved;
        _speakingTimer.Stop();
        _talkKey?.Dispose();
        _talkKey = null;
        var room = _room;
        _room = null;
        if (room is not null) await room.DisposeAsync();
        var archive = _archive;
        _archive = null;
        _archiveVersion++;
        if (archive is not null) await CloseArchiveAsync(archive);
    }

    private void OnSettingsSaved(AppSettings settings)
    {
        if (!_dispatcher.CheckAccess())
        {
            _dispatcher.BeginInvoke(() => OnSettingsSaved(settings));
            return;
        }
        ProfileChanged?.Invoke();
        if (_archiveApplied != (settings.ChatArchiveEnabled, settings.ChatArchiveFolder)) _ = UpdateArchiveAsync();
        if (_room is not { } room) return;
        room.Volume = (float)settings.VoiceVolume;
        room.Sensitivity = settings.VoiceSensitivity;
        room.SoundsEnabled = settings.VoiceSounds;
        room.CreatedChannels = settings.MyChannels;
        var avatar = SelfAvatarRef();
        if (room.Local.Name != settings.DeviceName || room.Local.Avatar != avatar)
        {
            room.UpdateLocal(room.Local with { Name = settings.DeviceName, Avatar = avatar });
            Refresh();
        }
        room.PushToTalk = settings.PushToTalk;
        UpdateTalkKey();
        if (room.SpeakerId != settings.SpeakerId)
        {
            room.SpeakerId = settings.SpeakerId;
            if (room.InVoice && room.MicrophoneId == settings.MicrophoneId) _ = RejoinAsync(); // troca a saída na hora
        }
        if (room.MicrophoneId != settings.MicrophoneId)
        {
            room.MicrophoneId = settings.MicrophoneId;
            if (room.InVoice) _ = RejoinAsync(); // troca o microfone na hora
        }
    }

    private async Task RejoinAsync()
    {
        var channel = VoiceChannel;
        await LeaveVoiceAsync();
        if (await JoinVoiceAsync(channel) is { } error) Log.Warn($"Troca de microfone: {error}");
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
        _photosVersion++;
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
        _photosVersion++;
        Refresh();
        var referenced = false;
        foreach (var row in AllMessages())
        {
            if (row.Image?.Id != id) continue;
            referenced = true;
            LoadPicture(row);
        }
        // A imagem de uma mensagem que já está na pasta do chat guardado vai para lá também.
        if (referenced && _archive is { } archive && AppServices.Blobs.Get(id) is { } data) archive.AddImage(id, data);
    }

    private void OnBlobFailed(string id)
    {
        foreach (var row in AllMessages())
            if (row.Image?.Id == id && row.Picture is null) row.ImageState = ChatImageState.Failed;
    }

    private IEnumerable<ChatRow> AllMessages() => _textRows.Values.SelectMany(channel => channel.Messages);

    // ───────────────────────── Voz ─────────────────────────

    /// <summary>
    /// Entra num canal de voz (null: o <see cref="SuggestedVoiceChannel"/>). Já na voz, só troca de canal, sem desligar o
    /// microfone. Retorna o motivo quando não deu (microfone ausente ou bloqueado).
    /// </summary>
    public async Task<string?> JoinVoiceAsync(string? channel = null)
    {
        var name = RoomChannels.CleanName(RoomChannelKind.Voice, channel ?? SuggestedVoiceChannel) is { Length: > 0 } clean ? clean : RoomChannels.DefaultVoiceName;
        RememberVoice(name);
        if (_demo)
        {
            _demoVoice = true;
            _demoVoiceChannel = name;
            ShowDemoVoice();
            return null;
        }
        if (_room is not { IsRunning: true } room) return Error;
        if (room.InVoice)
        {
            room.MoveVoice(name);
            return null;
        }
        if (_joining) return null;
        _joining = true;
        Changed?.Invoke();
        try
        {
            await Task.Run(() => room.JoinVoice(channel: name));
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

    /// <summary>O último canal de voz fica salvo: o botão Entrar volta para ele quando ninguém está na voz.</summary>
    private void RememberVoice(string name)
    {
        var settings = AppServices.Settings.Current;
        var value = RoomChannels.IsDefaultVoice(name) ? null : name;
        if (_demo || settings.LastVoiceChannel == value) return;
        settings.LastVoiceChannel = value;
        AppServices.Settings.Save();
    }

    public async Task LeaveVoiceAsync()
    {
        if (_demo)
        {
            _demoVoice = false;
            ShowDemoVoice();
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

    // ───────────────────────── Volume de cada pessoa ─────────────────────────

    /// <summary>O volume de uma pessoa só para você (0 = silenciada, 1 = normal, até 2).</summary>
    public double PeerVolume(Guid id)
        => id != SelfId && AppServices.Settings.Current.PeerVolumes.TryGetValue(id.ToString("N"), out var volume) ? volume : 1;

    /// <summary>
    /// Muda o volume de uma pessoa só para você (como no Discord). Fica salvo para as próximas vezes; durante o arraste
    /// do controle passe <paramref name="save"/> false e salve no fim.
    /// </summary>
    public void SetPeerVolume(Guid id, double volume, bool save = true)
    {
        if (id == SelfId || id == Guid.Empty) return;
        volume = Math.Round(Math.Clamp(volume, 0, 2), 2);
        var volumes = AppServices.Settings.Current.PeerVolumes;
        var key = id.ToString("N");
        if (Math.Abs(volume - 1) < 0.005) volumes.Remove(key);
        else volumes[key] = volume;
        _room?.SetPeerVolume(id, (float)volume);
        if (_memberRows.TryGetValue(id, out var row)) row.Volume = volume;
        if (save) AppServices.Settings.Save();
    }

    /// <summary>Silencia (ou volta a ouvir) uma pessoa só para você. Voltar devolve o volume de antes.</summary>
    public void SetPeerMuted(Guid id, bool muted)
    {
        var current = PeerVolume(id);
        if (muted)
        {
            if (current <= 0.001) return;
            _volumeBeforeMute[id] = current;
            SetPeerVolume(id, 0);
        }
        else if (current <= 0.001) SetPeerVolume(id, _volumeBeforeMute.Remove(id, out var before) ? before : 1);
    }

    // ───────────────────────── Apertar para falar (1.4.0) ─────────────────────────

    private PushToTalkKeyWatcher? _talkKey;

    /// <summary>A tecla só é lida com apertar para falar ligado e você na voz.</summary>
    internal void UpdateTalkKey()
    {
        var settings = AppServices.Settings.Current;
        _talkKey?.Watch(settings.PushToTalk && _room?.InVoice == true ? settings.PushToTalkKey : null);
    }

    // ───────────────────────── Sala privada e moderação (1.4.0) ─────────────────────────

    /// <summary>A sala privada mudou (entrou, saiu, chave nova, alguém pediu para entrar ou foi aprovado).</summary>
    public event Action? PrivateRoomChanged;

    /// <summary>O convite da sala privada em uso, ou null (sala aberta).</summary>
    public string? PrivateRoomCode => AppServices.Settings.Current.PrivateRoomCode;

    public bool InPrivateRoom => PrivateRoomCode is not null;

    /// <summary>Este PC criou a sala privada (aprova, remove e troca a chave).</summary>
    public bool IsPrivateRoomOwner => RoomInvite.TryParse(PrivateRoomCode, out var invite) && invite.Owner == SelfId;

    /// <summary>O resumo da chave atual ("7F3A-91C2"): todo mundo na mesma sala vê o mesmo.</summary>
    public string? PrivateRoomFingerprint => _room?.Keys.Fingerprint
        ?? (RoomInvite.TryParse(PrivateRoomCode, out var invite) ? RoomKeys.Fingerprint(invite.Secret, invite.Epoch) : null);

    /// <summary>Quem tem o convite e espera a aprovação (só o dono vê).</summary>
    public IReadOnlyDictionary<Guid, string> PrivateRoomPending => _room?.Pending ?? new Dictionary<Guid, string>();

    /// <summary>Quem o dono aprovou, com o nome de agora (ou "Fora da sala").</summary>
    public IReadOnlyList<(Guid Id, string Name)> PrivateRoomApproved
        => Approved(AppServices.Settings.Current).Select(id => (id, _room?.Peers.FirstOrDefault(p => p.Id == id)?.Presence.Name ?? "Fora da sala agora")).ToList();

    /// <summary>Quem você bloqueou, com o nome que aparecia.</summary>
    public IReadOnlyList<(Guid Id, string Name)> Blocked
        => AppServices.Settings.Current.BlockedPeers.Select(key => Guid.ParseExact(key, "N"))
            .Select(id => (id, _blockedNames.GetValueOrDefault(id) ?? "Pessoa bloqueada")).ToList();

    private readonly Dictionary<Guid, string> _blockedNames = [];

    private static IReadOnlyList<Guid> Approved(AppSettings settings)
        => settings.PrivateRoomApproved.Select(key => Guid.ParseExact(key, "N")).ToList();

    /// <summary>Cria uma sala privada com este PC de dono e entra nela. Devolve o código do convite.</summary>
    public string CreatePrivateRoom(bool approvedOnly)
    {
        var invite = RoomInvite.Create(SelfId);
        var settings = AppServices.Settings.Current;
        settings.PrivateRoomCode = invite.ToCode();
        settings.PrivateRoomApprovedOnly = approvedOnly;
        settings.PrivateRoomApproved = [];
        AppServices.Settings.Save();
        _room?.UsePrivateRoom(invite, approvedOnly, []);
        Log.Info("Sala privada criada.");
        PrivateRoomChanged?.Invoke();
        return settings.PrivateRoomCode;
    }

    /// <summary>Entra numa sala privada pelo convite. false se o código não é válido.</summary>
    public bool JoinPrivateRoom(string code)
    {
        if (!RoomInvite.TryParse(code, out var invite)) return false;
        var settings = AppServices.Settings.Current;
        settings.PrivateRoomCode = invite.ToCode();
        settings.PrivateRoomApprovedOnly = false;
        settings.PrivateRoomApproved = [];
        AppServices.Settings.Save();
        _room?.UsePrivateRoom(invite);
        Log.Info("Entrou numa sala privada pelo convite.");
        PrivateRoomChanged?.Invoke();
        return true;
    }

    /// <summary>Volta para a sala aberta da rede.</summary>
    public void LeavePrivateRoom()
    {
        var settings = AppServices.Settings.Current;
        settings.PrivateRoomCode = null;
        settings.PrivateRoomApprovedOnly = false;
        settings.PrivateRoomApproved = [];
        AppServices.Settings.Save();
        _room?.UsePrivateRoom(null);
        PrivateRoomChanged?.Invoke();
    }

    /// <summary>O dono liga ou desliga "só quem eu aprovar".</summary>
    public void SetPrivateRoomApprovedOnly(bool approvedOnly)
    {
        if (!IsPrivateRoomOwner) return;
        var settings = AppServices.Settings.Current;
        settings.PrivateRoomApprovedOnly = approvedOnly;
        AppServices.Settings.Save();
        _room?.SetRoster(approvedOnly, Approved(settings));
        PrivateRoomChanged?.Invoke();
    }

    /// <summary>O dono aprova quem tem o convite.</summary>
    public void ApprovePrivateMember(Guid id)
    {
        if (!IsPrivateRoomOwner || id == SelfId) return;
        var settings = AppServices.Settings.Current;
        var key = id.ToString("N");
        if (!settings.PrivateRoomApproved.Contains(key)) settings.PrivateRoomApproved.Add(key);
        AppServices.Settings.Save();
        _room?.SetRoster(settings.PrivateRoomApprovedOnly, Approved(settings));
        PrivateRoomChanged?.Invoke();
    }

    /// <summary>
    /// O dono remove alguém: a chave da sala troca para todo mundo que está nela agora, menos essa pessoa, que fica
    /// bloqueada aqui. Quem estiver fora da sala agora precisa de um convite novo.
    /// </summary>
    public bool RemovePrivateMember(Guid id, string name)
    {
        if (!IsPrivateRoomOwner || _room is not { } room || id == SelfId) return false;
        var settings = AppServices.Settings.Current;
        settings.PrivateRoomApproved.Remove(id.ToString("N"));
        room.SetRoster(settings.PrivateRoomApprovedOnly, Approved(settings));
        if (room.RotateKey([id]) is not { } next) return false;
        settings.PrivateRoomCode = next.ToCode();
        _blockedNames[id] = name;
        var blocked = id.ToString("N");
        if (!settings.BlockedPeers.Contains(blocked)) settings.BlockedPeers.Add(blocked);
        AppServices.Settings.Save();
        PrivateRoomChanged?.Invoke();
        QueueRefresh();
        return true;
    }

    /// <summary>O dono troca a chave da sala sem tirar ninguém (o convite antigo deixa de valer).</summary>
    public bool RotatePrivateRoomKey()
    {
        if (!IsPrivateRoomOwner || _room?.RotateKey([]) is not { } next) return false;
        AppServices.Settings.Current.PrivateRoomCode = next.ToCode();
        AppServices.Settings.Save();
        PrivateRoomChanged?.Invoke();
        return true;
    }

    /// <summary>Bloqueia (ou desbloqueia) alguém só para você: nada dele chega, nem chat, nem voz, nem arquivos.</summary>
    public void SetBlocked(Guid id, string name, bool blocked)
    {
        if (id == SelfId) return;
        var settings = AppServices.Settings.Current;
        var key = id.ToString("N");
        if (blocked)
        {
            _blockedNames[id] = name;
            if (!settings.BlockedPeers.Contains(key)) settings.BlockedPeers.Add(key);
        }
        else settings.BlockedPeers.Remove(key);
        AppServices.Settings.Save();
        _room?.SetBlocked(id, blocked);
        PrivateRoomChanged?.Invoke();
        QueueRefresh();
    }

    private void OnKeyChanged(RoomInvite next)
    {
        AppServices.Settings.Current.PrivateRoomCode = next.ToCode();
        AppServices.Settings.Save();
        PrivateRoomChanged?.Invoke();
    }

    private readonly HashSet<Guid> _pendingSeen = [];

    private void OnPendingChanged()
    {
        foreach (var (id, name) in PrivateRoomPending)
            if (_pendingSeen.Add(id)) PendingMember?.Invoke(name);
        PrivateRoomChanged?.Invoke();
    }

    /// <summary>Alguém com o convite pediu para entrar na sala privada (só o dono recebe).</summary>
    public event Action<string>? PendingMember;

    // ───────────────────────── Canais ─────────────────────────

    /// <summary>
    /// Cria um canal seu: aparece para todo mundo da sala enquanto você estiver nela. Um canal que já existe só é aberto.
    /// Retorna o motivo quando não deu.
    /// </summary>
    public string? CreateChannel(RoomChannelKind kind, string name)
    {
        if (RoomChannels.Create(kind, name) is not { } channel) return "Escolha um nome com letras ou números.";
        var existing = kind == RoomChannelKind.Text
            ? TextChannels.Select(c => c.Channel).FirstOrDefault(c => c.Same(channel))
            : VoiceChannels.Select(c => c.Channel).FirstOrDefault(c => c.Same(channel));
        if (existing is not null)
        {
            if (kind == RoomChannelKind.Text) SelectText(existing.Name);
            return null;
        }
        var settings = AppServices.Settings.Current;
        if (settings.MyChannels.Count >= RoomChannels.MaxPerPerson)
            return $"Cada PC cria até {RoomChannels.MaxPerPerson} canais. Apague um dos seus (botão direito no canal) para criar outro.";
        settings.MyChannels.Add(channel);
        AppServices.Settings.Save();
        if (_room is { } room) room.CreatedChannels = settings.MyChannels;
        RebuildChannels();
        if (kind == RoomChannelKind.Text) SelectText(channel.Name);
        Changed?.Invoke();
        return null;
    }

    /// <summary>Apaga um canal seu. Quem já conversou nele continua vendo a conversa até fechar o ScreenFlow.</summary>
    public void DeleteChannel(RoomChannel channel)
    {
        var settings = AppServices.Settings.Current;
        if (settings.MyChannels.RemoveAll(c => c.Same(channel)) == 0) return;
        AppServices.Settings.Save();
        if (_room is { } room) room.CreatedChannels = settings.MyChannels;
        if (channel.Kind == RoomChannelKind.Text && _currentText.Channel.Same(channel) && _currentText.Messages.Count == 0) SelectText(null);
        RebuildChannels();
        Changed?.Invoke();
    }

    /// <summary>Abre um canal de texto no chat (null: o "geral") e marca como lido se o chat estiver na frente.</summary>
    public void SelectText(string? channel)
    {
        var row = TextRow(channel);
        if (row != _currentText)
        {
            _currentText.IsSelected = false;
            _currentText = row;
            row.IsSelected = true;
            if (!row.Listed) RebuildChannels();
            var settings = AppServices.Settings.Current;
            var last = row.IsDefaultText ? null : row.Name;
            if (!_demo && settings.LastTextChannel != last)
            {
                settings.LastTextChannel = last;
                AppServices.Settings.Save();
            }
            CurrentTextChanged?.Invoke();
        }
        if (IsChatInFront?.Invoke() == true) MarkRead();
    }

    /// <summary>A linha de um canal de texto (criada na primeira vez), pelo nome; null ou vazio é o "geral".</summary>
    private TextChannelRow TextRow(string? name)
    {
        var clean = RoomChannels.IsDefaultText(name) ? RoomChannels.DefaultTextName : RoomChannels.CleanName(RoomChannelKind.Text, name);
        if (clean.Length == 0) clean = RoomChannels.DefaultTextName;
        var key = RoomChannels.Key(clean);
        if (_textRows.TryGetValue(key, out var known)) return known;
        var channel = RoomChannels.Defaults.FirstOrDefault(c => c.Kind == RoomChannelKind.Text && c.Key == key) ?? new RoomChannel(RoomChannelKind.Text, clean);
        if (!_textRows.TryGetValue(channel.Key, out var row)) _textRows[channel.Key] = row = new TextChannelRow(channel);
        return row;
    }

    private VoiceChannelRow VoiceRow(RoomChannel channel)
    {
        if (!_voiceRows.TryGetValue(channel.Key, out var row)) _voiceRows[channel.Key] = row = new VoiceChannelRow(channel);
        return row;
    }

    /// <summary>
    /// Monta as listas de canais: os padrão, os seus, os que o pessoal da sala criou, os de texto que têm conversa (mesmo
    /// que quem criou tenha saído) e os de voz que têm gente.
    /// </summary>
    private void RebuildChannels()
    {
        var mine = AppServices.Settings.Current.MyChannels;
        var owners = new Dictionary<(RoomChannelKind, string), string>();
        var created = new List<RoomChannel>();
        foreach (var channel in mine)
        {
            created.Add(channel);
            owners.TryAdd((channel.Kind, channel.Key), SelfName);
        }
        IEnumerable<(RoomChannel Channel, string Owner)> announced = _demo
            ? _demoChannels
            : _peers.Values.SelectMany(peer => peer.Presence.CreatedChannels.Select(channel => (channel, peer.Presence.Name)));
        foreach (var (channel, owner) in announced)
        {
            created.Add(channel);
            owners.TryAdd((channel.Kind, channel.Key), owner);
        }
        foreach (var row in _textRows.Values)
            if (row.Messages.Count > 0 || row == _currentText) created.Add(row.Channel);
        foreach (var name in _voiceInUse)
            if (RoomChannels.CleanName(RoomChannelKind.Voice, name) is { Length: > 0 } clean) created.Add(new RoomChannel(RoomChannelKind.Voice, clean));

        var text = RoomChannels.Merge(RoomChannelKind.Text, created);
        Sync(TextChannels, text.Select(c => (c.Key, c)).ToList(), row => row.Key, (_, channel) => TextRow(channel.Name), (row, channel) =>
        {
            row.IsMine = mine.Any(c => c.Same(channel));
            row.Owner = owners.GetValueOrDefault((RoomChannelKind.Text, channel.Key));
        });
        var listed = text.Select(c => c.Key).ToHashSet();
        foreach (var (key, row) in _textRows.ToList())
        {
            row.Listed = listed.Contains(key);
            // Canal que saiu da lista sem conversa nenhuma não precisa ficar na memória.
            if (!row.Listed && row.Messages.Count == 0 && row != _currentText) _textRows.Remove(key);
        }

        var voice = RoomChannels.Merge(RoomChannelKind.Voice, created);
        Sync(VoiceChannels, voice.Select(c => (c.Key, c)).ToList(), row => row.Key, (_, channel) => VoiceRow(channel), (row, channel) =>
        {
            row.IsMine = mine.Any(c => c.Same(channel));
            row.Owner = owners.GetValueOrDefault((RoomChannelKind.Voice, channel.Key));
        });
        var voiceKeys = voice.Select(c => c.Key).ToHashSet();
        foreach (var key in _voiceRows.Keys.Where(k => !voiceKeys.Contains(k)).ToList()) _voiceRows.Remove(key);
    }

    /// <summary>Quem está em cada canal de voz, o canal sugerido e o painel da voz do menu lateral.</summary>
    private void ShowVoice(List<(string Channel, VoiceMemberState State)> members)
    {
        _voiceInUse = members.Select(m => m.Channel).ToList();
        RebuildChannels();
        var byChannel = members
            .GroupBy(m => RoomChannels.Key(m.Channel))
            .ToDictionary(g => g.Key, g => g.Select(m => m.State).OrderBy(s => s.Name, Names).ToList());
        var current = VoiceChannel;
        foreach (var row in VoiceChannels)
        {
            var list = byChannel.GetValueOrDefault(row.Key) ?? [];
            Sync(row.Members, list.Select(s => (s.Id, s)), MemberRow, UpdateMember);
            row.IsCurrent = current is not null && RoomChannels.SameName(current, row.Name);
        }
        var inVoice = members.Select(m => m.State.Id).ToHashSet();
        foreach (var id in _memberRows.Keys.Where(id => !inVoice.Contains(id)).ToList()) _memberRows.Remove(id);

        // Fora da voz, o Entrar põe você onde está o pessoal (ou no último canal em que você esteve).
        var last = AppServices.Settings.Current.LastVoiceChannel ?? RoomChannels.DefaultVoiceName;
        SuggestedVoiceChannel = VoiceChannels
            .Where(c => c.Members.Count > 0)
            .OrderByDescending(c => c.Members.Count)
            .ThenByDescending(c => RoomChannels.SameName(c.Name, last))
            .FirstOrDefault()?.Name
            ?? VoiceChannels.FirstOrDefault(c => RoomChannels.SameName(c.Name, last))?.Name
            ?? RoomChannels.DefaultVoiceName;
        var focus = RoomChannels.Key(current ?? SuggestedVoiceChannel);
        Sync(VoiceMembers, (byChannel.GetValueOrDefault(focus) ?? []).Select(s => (s.Id, s)), MemberRow, UpdateMember);
    }

    private VoiceMemberRow MemberRow(Guid id)
    {
        if (!_memberRows.TryGetValue(id, out var row)) _memberRows[id] = row = new VoiceMemberRow(id);
        return row;
    }

    private void UpdateMember(VoiceMemberRow row, VoiceMemberState state)
    {
        row.Update(state);
        row.Photo = PhotoOf(state.Id);
        row.Volume = state.IsMe ? 1 : PeerVolume(state.Id);
    }

    // ───────────────────────── Chat ─────────────────────────

    /// <summary>
    /// Manda no canal de texto aberto para todo mundo da sala, com uma imagem se quiser (quem recebe baixa a imagem
    /// deste PC). false quando a sala está desligada ou não há nada para mandar.
    /// </summary>
    public bool Send(string text, PreparedImage? image = null)
    {
        ChatImage? attached = null;
        if (image is not null)
        {
            var id = AppServices.Blobs.Put(image.Data);
            attached = new ChatImage(id, image.Data.Length, image.Width, image.Height);
        }
        var channel = _currentText.IsDefaultText ? null : _currentText.Name;
        if (_demo)
        {
            var clean = RoomPacket.CleanText(text);
            if (clean.Length == 0 && attached is null) return false;
            AddMessage(new RoomChatMessage(SelfId, SelfName, (ulong)Random.Shared.NextInt64(), DateTimeOffset.Now,
                clean.Length == 0 ? RoomPacket.ImagePlaceholder : clean, Image: attached, Channel: channel));
            return true;
        }
        return _room is { IsRunning: true } room && room.SendChat(text, attached, channel) is not null;
    }

    /// <summary>A imagem não veio: pede de novo, a quem mandou e a todo mundo da sala (quem já baixou também tem).</summary>
    public void RetryImage(ChatRow row)
    {
        if (row.Image is not { } image) return;
        row.ImageState = ChatImageState.Loading;
        if (AppServices.Blobs.Has(image.Id))
        {
            LoadPicture(row);
            return;
        }
        if (_room is not { } room)
        {
            row.ImageState = ChatImageState.Failed;
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
            if (row.FromArchive) RestoreFromArchive(image.Id);
            return;
        }
        row.Picture = ImageTools.Decode(data, Math.Min(image.Width, ChatRow.PictureDecodeWidth));
        row.ImageState = row.Picture is null ? ChatImageState.Failed : ChatImageState.Ready;
    }

    /// <summary>A imagem de uma mensagem guardada já saiu deste PC: busca na pasta do chat (fora da thread da interface).</summary>
    private async void RestoreFromArchive(string id)
    {
        if (_archive is not { } archive || !_restoring.Add(id)) return;
        var restored = false;
        try
        {
            var data = await Task.Run(() => archive.ReadImage(id));
            restored = data is not null && AppServices.Blobs.Save(id, data);
        }
        catch (Exception exception) { Log.Debug($"Imagem do chat guardado: {exception.Message}"); }
        if (restored) OnBlobArrived(id);
        else OnBlobFailed(id); // "Tentar de novo" pede para quem está na sala
    }

    /// <summary>Marca como lido o canal aberto (o chat está na frente).</summary>
    public void MarkRead()
    {
        if (_currentText.Unread == 0) return;
        _currentText.Unread = 0;
        UnreadChanged?.Invoke();
    }

    /// <summary>A mensagem está na frente de quem usa: o chat aberto, nesse canal.</summary>
    public bool IsShowing(ChatRow row) => IsShowing(TextRow(row.ChannelName));

    private bool IsShowing(TextChannelRow channel) => channel == _currentText && IsChatInFront?.Invoke() == true;

    private void OnMessage(RoomChatMessage message)
    {
        var row = AddMessage(message);
        if (row is null) return;
        _archive?.Add(message, message.Image is { } image ? AppServices.Blobs.Get(image.Id) : null);
        if (row.IsMine || message.History) return;
        var channel = TextRow(message.ChannelName);
        if (!IsShowing(channel))
        {
            channel.Unread++;
            UnreadChanged?.Invoke();
        }
        MessageArrived?.Invoke(row);
    }

    private ChatRow? AddMessage(RoomChatMessage message, bool fromArchive = false)
    {
        if (!_shown.Add((message.AuthorId, message.MessageId))) return null;
        var channel = TextRow(message.ChannelName);
        var row = new ChatRow(message, message.AuthorId == SelfId, fromArchive) { Photo = PhotoOf(message.AuthorId) };
        var messages = channel.Messages;
        var index = messages.Count;
        while (index > 0 && messages[index - 1].Time > row.Time) index--;
        if (index == 0 && messages.Count >= ChannelCapacity) return null; // mais antiga que tudo o que cabe na tela
        LoadPicture(row);
        messages.Insert(index, row);
        while (messages.Count > ChannelCapacity)
        {
            messages.RemoveAt(0);
            index--;
        }
        Regroup(messages, Math.Max(0, index));
        // Um canal que não estava na lista (quem criou já saiu) aparece com a conversa.
        if (!channel.Listed) RebuildChannels();
        return row;
    }

    /// <summary>Mensagens seguidas da mesma pessoa (até 5 minutos) ficam juntas, sem repetir nome e foto.</summary>
    private static void Regroup(ObservableCollection<ChatRow> messages, int index)
    {
        for (var i = Math.Max(0, index); i <= Math.Min(messages.Count - 1, index + 1); i++)
        {
            var previous = i > 0 ? messages[i - 1] : null;
            var current = messages[i];
            current.ShowHeader = previous is null || previous.AuthorId != current.AuthorId || current.Time - previous.Time > GroupWindow
                || previous.Time.ToLocalTime().Date != current.Time.ToLocalTime().Date;
        }
    }

    // ───────────────────────── Chat guardado (Google Drive) ─────────────────────────

    /// <summary>A pasta em que o chat está sendo guardado (null com a opção desligada ou sem pasta).</summary>
    public string? ArchiveFolder => _archive?.Folder;

    /// <summary>Por que o chat não está sendo guardado (null quando está tudo certo ou a opção está desligada).</summary>
    public string? ArchiveProblem { get; private set; }

    /// <summary>A pasta do chat guardado abriu, fechou ou deu problema.</summary>
    public event Action? ArchiveChanged;

    /// <summary>Uma gravação do chat guardado falhou (a pasta sumiu, o Drive está fechado...).</summary>
    public event Action<string>? ArchiveFailed;

    /// <summary>Abre (ou fecha) a pasta do chat guardado conforme as configurações. A pasta é lida fora da interface.</summary>
    private async Task UpdateArchiveAsync()
    {
        var settings = AppServices.Settings.Current;
        var wanted = (settings.ChatArchiveEnabled, settings.ChatArchiveFolder);
        _archiveApplied = wanted;
        var version = ++_archiveVersion;
        string? root = null;
        string? problem = null;
        if (wanted.ChatArchiveEnabled && !_demo)
        {
            root = wanted.ChatArchiveFolder ?? await Task.Run(ChatArchiveFolders.FindGoogleDrive);
            if (version != _archiveVersion) return;
            if (root is null) problem = "O Google Drive para computador não foi encontrado neste PC. Instale e entre na sua conta, ou escolha outra pasta.";
        }
        if (_archive is { } open && open.Root != root)
        {
            _archive = null;
            _ = CloseArchiveAsync(open);
        }
        ArchiveProblem = problem;
        if (root is null || _archive is not null)
        {
            ArchiveChanged?.Invoke();
            return;
        }

        ChatArchive archive;
        try { archive = new ChatArchive(root); }
        catch (Exception exception)
        {
            ArchiveProblem = exception.Message;
            ArchiveChanged?.Invoke();
            return;
        }
        archive.Failed += error => _dispatcher.BeginInvoke(() =>
        {
            if (_archive != archive) return;
            ArchiveProblem = $"O chat não foi gravado na pasta: {error}";
            ArchiveFailed?.Invoke(error);
            ArchiveChanged?.Invoke();
        });
        _archive = archive;
        ArchiveChanged?.Invoke();
        Log.Info($"Chat guardado em {archive.Folder}.");

        IReadOnlyList<RoomChatMessage> recent;
        try { recent = await Task.Run(() => archive.LoadRecent()); }
        catch (Exception exception)
        {
            Log.Warn($"Chat guardado não foi lido: {exception.Message}");
            recent = [];
        }
        if (_archive != archive) return;
        foreach (var message in recent) AddMessage(message, fromArchive: true);
        // O que já estava na tela antes de a pasta abrir também vai para ela.
        foreach (var row in AllMessages().ToList())
            archive.Add(row.Message, row.Image is { } image ? AppServices.Blobs.Get(image.Id) : null);
    }

    private static async Task CloseArchiveAsync(ChatArchive archive)
    {
        try
        {
            await Task.Run(() =>
            {
                archive.Flush(TimeSpan.FromSeconds(3));
                archive.Dispose();
            });
        }
        catch (Exception exception) { Log.Debug($"Chat guardado ao fechar: {exception.Message}"); }
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
            var avatar = peer.Presence.Avatar?.Id;
            if (!_avatars.TryGetValue(peer.Id, out var known) || known != avatar) _photosVersion++;
            _avatars[peer.Id] = avatar;
        }
        Sync(People, peers.Select(p => (p.Id, p)), id => new RoomPersonRow(id), (row, peer) =>
        {
            row.Update(peer);
            row.Photo = PhotoFor(peer.Presence.Avatar?.Id);
        });

        var members = new List<(string Channel, VoiceMemberState State)>();
        foreach (var peer in peers)
            if (peer.Presence.CurrentVoiceChannel is { } channel)
                members.Add((channel, new VoiceMemberState(peer.Id, peer.Presence.Name, peer.Presence.Muted, peer.Presence.Deafened, false)));
        if (room.VoiceChannel is { } mine) members.Add((mine, new VoiceMemberState(SelfId, SelfName, room.Muted || room.Deafened, room.Deafened, true)));
        ShowVoice(members);

        // As fotos das mensagens só mudam quando alguém troca de foto (ou uma foto chega).
        if (_photosShown != _photosVersion)
        {
            _photosShown = _photosVersion;
            var photos = new Dictionary<Guid, ImageSource?>();
            foreach (var message in AllMessages())
            {
                if (!photos.TryGetValue(message.AuthorId, out var photo)) photos[message.AuthorId] = photo = PhotoOf(message.AuthorId);
                message.Photo = photo;
            }
        }

        _speakingTimer.IsEnabled = room.InVoice;
        if (!room.InVoice) foreach (var row in _memberRows.Values) row.Speaking = false;
        Changed?.Invoke();
    }

    private void UpdateSpeaking()
    {
        if (_room is not { } room) return;
        var now = DateTimeOffset.UtcNow;
        var self = room.Voice?.IsSpeaking == true;
        // Só quem está no seu canal manda voz para cá: o anel verde aparece para eles.
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
        => Sync(rows, items.ToList(), row => row.Id, (id, _) => create(id), update);

    private static void Sync<TRow, TKey, TData>(ObservableCollection<TRow> rows, IReadOnlyList<(TKey Key, TData Data)> items, Func<TRow, TKey> keyOf,
        Func<TKey, TData, TRow> create, Action<TRow, TData> update) where TKey : notnull
    {
        var keys = new HashSet<TKey>(items.Count);
        foreach (var item in items) keys.Add(item.Key);
        for (var i = rows.Count - 1; i >= 0; i--)
            if (!keys.Contains(keyOf(rows[i]))) rows.RemoveAt(i);
        var comparer = EqualityComparer<TKey>.Default;
        for (var index = 0; index < items.Count; index++)
        {
            var (key, data) = items[index];
            var current = -1;
            for (var j = index; j < rows.Count; j++)
            {
                if (!comparer.Equals(keyOf(rows[j]), key)) continue;
                current = j;
                break;
            }
            TRow row;
            if (current < 0)
            {
                row = create(key, data);
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

    /// <summary>
    /// Sala de exemplo para as capturas do CI: quatro pessoas, gente em dois canais de voz, um canal criado e conversas
    /// no #geral e no #música.
    /// </summary>
    public void LoadDemo(bool inVoice = true, int unread = 2)
    {
        _demo = true;
        SelfId = new Guid("6b1f1d5e-0c4a-4f7e-9d3b-2a8c5e7f9a0f");
        var ana = new Guid("0d6f3a52-8b1e-4c9a-a7f2-5e3b9c1d2f42");
        var bruno = new Guid("9a2c7e14-3f5b-4d8a-b6e1-7c4f2a9d3e51");
        var carla = new Guid("4e8b1c63-7a2d-4f9e-8c5b-1d6a3f7e2b5f");
        var diego = new Guid("c3a9e275-1b6f-4a8d-9e2c-6f1b8d4a7c73");
        var (anaPhoto, brunoPhoto, wallpaper) = DemoImages();
        _avatars[ana] = anaPhoto;
        _avatars[bruno] = brunoPhoto;
        _photosVersion++;
        People.Clear();
        People.Add(RoomPersonRow.Demo(ana, "Ana Lu", "Ao vivo · tela, câmera e áudio", true, StreamMode.Screen, "26.47.19.113", false, PhotoOf(ana)));
        People.Add(RoomPersonRow.Demo(bruno, "Bruno", "Online · na voz: Geral", false, StreamMode.Screen, null, true, PhotoOf(bruno)));
        People.Add(RoomPersonRow.Demo(carla, "Carla-PC", "Assistindo Ana Lu · na voz: Geral, mudo", false, StreamMode.Screen, null, true));
        People.Add(RoomPersonRow.Demo(diego, "Diego", "Online · na voz: Valorant", false, StreamMode.Screen, null, true));

        _demoChannels.Clear();
        _demoChannels.Add((new RoomChannel(RoomChannelKind.Voice, "Valorant"), "Diego"));
        _demoChannels.Add((new RoomChannel(RoomChannelKind.Text, "campeonato"), "Ana Lu"));
        _demoMembers.Clear();
        _demoMembers.Add((RoomChannels.DefaultVoiceName, new VoiceMemberState(bruno, "Bruno", false, false, false)));
        _demoMembers.Add((RoomChannels.DefaultVoiceName, new VoiceMemberState(carla, "Carla-PC", true, false, false)));
        _demoMembers.Add(("Valorant", new VoiceMemberState(diego, "Diego", false, false, false)));
        _demoVoice = inVoice;
        _demoVoiceChannel = RoomChannels.DefaultVoiceName;

        foreach (var row in _textRows.Values)
        {
            row.Messages.Clear();
            row.Unread = 0;
        }
        _shown.Clear();
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
        AddMessage(new RoomChatMessage(diego, "Diego", 8, at(20, 9), "Coloquei a playlist da festa, quem quiser é só entrar", Channel: "música"));
        AddMessage(new RoomChatMessage(ana, "Ana Lu", 9, at(20, 10), "Chave do campeonato de sábado no ar!", Channel: "campeonato"));
        _currentText.IsSelected = false;
        _currentText = TextRow(null);
        _currentText.IsSelected = true;
        _currentText.Unread = unread;
        TextRow("música").Unread = 1;
        CurrentTextChanged?.Invoke();
        ShowDemoVoice();
        UnreadChanged?.Invoke();
        ProfileChanged?.Invoke();
    }

    private void ShowDemoVoice()
    {
        var members = new List<(string Channel, VoiceMemberState State)>(_demoMembers);
        if (_demoVoice) members.Add((_demoVoiceChannel, new VoiceMemberState(SelfId, SelfName, _demoMuted || _demoDeafened, _demoDeafened, true)));
        ShowVoice(members);
        // O Bruno está falando (o anel verde aparece) quando você está no canal dele.
        var bruno = new Guid("9a2c7e14-3f5b-4d8a-b6e1-7c4f2a9d3e51");
        foreach (var row in _memberRows.Values) row.Speaking = _demoVoice && row.Id == bruno && RoomChannels.IsDefaultVoice(_demoVoiceChannel);
        Changed?.Invoke();
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

/// <summary>Acha a pasta do Google Drive para computador neste PC (as unidades e a pasta do usuário).</summary>
public static class ChatArchiveFolders
{
    public static string? FindGoogleDrive()
    {
        var drives = new List<(string Root, string? Label)>();
        try
        {
            foreach (var drive in System.IO.DriveInfo.GetDrives())
            {
                try
                {
                    if (drive.DriveType == System.IO.DriveType.CDRom || !drive.IsReady) continue;
                    drives.Add((drive.RootDirectory.FullName, drive.VolumeLabel));
                }
                catch (Exception exception) when (exception is System.IO.IOException or UnauthorizedAccessException) { }
            }
        }
        catch (Exception exception) { Log.Debug($"Unidades do PC: {exception.Message}"); }
        return CloudFolders.FindGoogleDrive(drives, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
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
        Notify(names);
    }

    protected void Notify(params string[] names)
    {
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
        InVoice = presence.InAnyVoice;
        Muted = presence.Muted;
        WatchAddress = presence.Live ? peer.WatchAddress.ToString() : null;
        var status = presence.Live
            ? (presence.LiveMode == StreamMode.AudioOnly ? "Ao vivo · festa de música" : $"Ao vivo · {presence.LiveSummary}")
              + (presence.Flags.HasFlag(PresenceFlags.PinRequired) ? " · com PIN" : string.Empty)
            : presence.Watching ? "Assistindo uma transmissão" : "Online";
        if (presence.CurrentVoiceChannel is { } channel)
            status += presence.Deafened ? $" · na voz: {channel}, sem som" : presence.Muted ? $" · na voz: {channel}, mudo" : $" · na voz: {channel}";
        if (!presence.SupportsChannels) status += " · versão antiga, só vê o #geral";
        StatusText = status;
    }

    internal static RoomPersonRow Demo(Guid id, string name, string status, bool live, StreamMode mode, string? address, bool inVoice, ImageSource? photo = null)
        => new(id) { Name = name, StatusText = status, IsLive = live, LiveMode = mode, WatchAddress = address, InVoice = inVoice, Photo = photo };
}

public readonly record struct VoiceMemberState(Guid Id, string Name, bool Muted, bool Deafened, bool IsMe);

/// <summary>Alguém na voz: no canal de voz da sala e no painel do menu lateral.</summary>
public sealed class VoiceMemberRow(Guid id) : RoomRowBase, IRoomRow
{
    private string _name = string.Empty;
    private bool _muted;
    private bool _deafened;
    private bool _speaking;
    private bool _isMe;
    private double _volume = 1;
    private ImageSource? _photo;

    public Guid Id { get; } = id;
    public AvatarColors Avatar { get; } = AvatarColors.For(id);
    public string Name { get => _name; private set => Set(ref _name, value, nameof(Name), nameof(Initial), nameof(DisplayName), nameof(Hint)); }
    public string Initial => AvatarColors.Initial(_name);
    public ImageSource? Photo { get => _photo; set => Set(ref _photo, value, nameof(Photo)); }
    public string DisplayName => _isMe ? $"{_name} (você)" : _name;
    public bool IsMe { get => _isMe; private set => Set(ref _isMe, value, nameof(IsMe), nameof(DisplayName), nameof(LocallyMuted), nameof(Hint)); }
    public bool Muted { get => _muted; private set => Set(ref _muted, value, nameof(Muted), nameof(ShowMuted)); }
    public bool Deafened { get => _deafened; private set => Set(ref _deafened, value, nameof(Deafened), nameof(ShowMuted)); }
    /// <summary>O ícone de microfone cortado aparece só quando não está com o fone cortado (que já inclui o mudo).</summary>
    public bool ShowMuted => _muted && !_deafened;
    public bool Speaking { get => _speaking; set => Set(ref _speaking, value, nameof(Speaking)); }

    /// <summary>O volume desta pessoa só para você (1 = normal).</summary>
    public double Volume { get => _volume; set => Set(ref _volume, value, nameof(Volume), nameof(LocallyMuted), nameof(VolumeChanged), nameof(Hint)); }

    /// <summary>Silenciada só para você.</summary>
    public bool LocallyMuted => !_isMe && _volume <= 0.001;

    /// <summary>Volume diferente do normal (aparece um ícone).</summary>
    public bool VolumeChanged => !_isMe && _volume > 0.001 && Math.Abs(_volume - 1) > 0.005;

    /// <summary>A dica ao passar o mouse.</summary>
    public string Hint => _isMe ? "Você" : LocallyMuted ? $"{_name} · silenciado só para você (botão direito para mudar)"
        : VolumeChanged ? $"{_name} · volume {_volume * 100:0}% para você (botão direito para mudar)"
        : $"{_name} · botão direito para mudar o volume só para você";

    public void Update(VoiceMemberState state)
    {
        Name = state.Name;
        IsMe = state.IsMe;
        Muted = state.Muted;
        Deafened = state.Deafened;
        if (state.Muted || state.Deafened) Speaking = false;
    }
}

/// <summary>Um canal de texto da sala, com a conversa dele.</summary>
public sealed class TextChannelRow(RoomChannel channel) : RoomRowBase
{
    private int _unread;
    private bool _selected;
    private bool _mine;
    private string? _owner;

    public RoomChannel Channel { get; } = channel;
    public string Key { get; } = channel.Key;
    public string Name => Channel.Name;
    public bool IsDefaultText => RoomChannels.IsDefaultText(Channel.Name);
    public ObservableCollection<ChatRow> Messages { get; } = [];

    /// <summary>Está na lista de canais agora.</summary>
    internal bool Listed { get; set; }

    public int Unread { get => _unread; set => Set(ref _unread, Math.Max(0, value), nameof(Unread), nameof(HasUnread), nameof(UnreadText)); }
    public bool HasUnread => _unread > 0;
    public string UnreadText => _unread > 99 ? "99+" : _unread.ToString(CultureInfo.InvariantCulture);
    public bool IsSelected { get => _selected; set => Set(ref _selected, value, nameof(IsSelected)); }
    public bool IsMine { get => _mine; set => Set(ref _mine, value, nameof(IsMine), nameof(Topic)); }
    public string? Owner { get => _owner; set => Set(ref _owner, value, nameof(Owner), nameof(Topic)); }

    /// <summary>O assunto do canal, embaixo do nome na conversa.</summary>
    public string Topic => Key switch
    {
        "geral" => "Conversa de todo mundo da rede. Quem ainda está numa versão antiga do ScreenFlow também vê este canal.",
        "musica" => "Pedidos, indicações e a festa de música.",
        "prints-e-clipes" => "Prints, clipes e imagens: cole com Ctrl+V ou arraste para cá.",
        _ => IsMine ? "Canal criado por você. Aparece para todo mundo da sala enquanto você estiver nela."
            : Owner is { } owner ? $"Canal criado por {owner}."
            : "Canal criado por alguém da sala.",
    };
}

/// <summary>Um canal de voz da sala e quem está nele.</summary>
public sealed class VoiceChannelRow : RoomRowBase
{
    private bool _current;
    private bool _mine;
    private string? _owner;

    public VoiceChannelRow(RoomChannel channel)
    {
        Channel = channel;
        Key = channel.Key;
        Members.CollectionChanged += OnMembersChanged;
    }

    public RoomChannel Channel { get; }
    public string Key { get; }
    public string Name => Channel.Name;
    public ObservableCollection<VoiceMemberRow> Members { get; } = [];
    public bool HasMembers => Members.Count > 0;
    public string CountText => Members.Count == 0 ? string.Empty : Members.Count.ToString(CultureInfo.InvariantCulture);

    /// <summary>Você está neste canal.</summary>
    public bool IsCurrent { get => _current; set => Set(ref _current, value, nameof(IsCurrent), nameof(Hint)); }
    public bool IsMine { get => _mine; set => Set(ref _mine, value, nameof(IsMine), nameof(Hint)); }
    public string? Owner { get => _owner; set => Set(ref _owner, value, nameof(Owner), nameof(Hint)); }

    /// <summary>A dica ao passar o mouse: o que o clique faz.</summary>
    public string Hint
    {
        get
        {
            var action = IsCurrent ? "Você está neste canal" : Members.Count switch
            {
                0 => $"Entrar no canal {Name}",
                1 => $"Entrar no canal {Name} (1 pessoa)",
                var n => $"Entrar no canal {Name} ({n} pessoas)",
            };
            return Channel.IsDefault ? action : IsMine ? $"{action} · criado por você" : Owner is { } owner ? $"{action} · criado por {owner}" : action;
        }
    }

    private void OnMembersChanged(object? sender, NotifyCollectionChangedEventArgs e) => Notify(nameof(HasMembers), nameof(CountText), nameof(Hint));
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

    public ChatRow(RoomChatMessage message, bool isMine, bool fromArchive = false)
    {
        Message = message;
        AuthorId = message.AuthorId;
        MessageId = message.MessageId;
        Author = message.AuthorName;
        Text = message.Caption;
        Image = message.Image;
        Time = message.Time;
        ChannelName = message.ChannelName;
        IsMine = isMine;
        FromArchive = fromArchive;
        Avatar = AvatarColors.For(message.AuthorId);
        _imageState = Image is null ? ChatImageState.None : ChatImageState.Loading;
        if (Image is { } image)
        {
            var scale = Math.Min(1, Math.Min(PictureMaxWidth / Math.Max(1, image.Width), PictureMaxHeight / Math.Max(1, image.Height)));
            PictureWidth = Math.Max(48, Math.Round(image.Width * scale));
            PictureHeight = Math.Max(48, Math.Round(image.Height * scale));
        }
    }

    /// <summary>A mensagem como chegou (para guardar na pasta do chat).</summary>
    public RoomChatMessage Message { get; }
    public Guid AuthorId { get; }
    public ulong MessageId { get; }
    public string Author { get; }
    public string Initial => AvatarColors.Initial(Author);
    public string AuthorText => IsMine ? $"{Author} (você)" : Author;
    /// <summary>O texto (a legenda, quando tem imagem; vazio quando a imagem foi sozinha).</summary>
    public string Text { get; }
    public bool HasText => Text.Length > 0;
    public DateTimeOffset Time { get; }
    /// <summary>O canal de texto da mensagem ("geral" nas das versões antigas).</summary>
    public string ChannelName { get; }
    public bool IsMine { get; }
    /// <summary>Veio da pasta do chat guardado (a imagem, se faltar, é buscada lá).</summary>
    public bool FromArchive { get; }
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

    public string FullTimeText => Time.ToLocalTime().ToString("dddd, dd/MM/yyyy 'às' HH:mm", CultureInfo.GetCultureInfo("pt-BR"));

    /// <summary>Primeira de um grupo: mostra foto, nome e hora.</summary>
    public bool ShowHeader { get => _showHeader; set => Set(ref _showHeader, value, nameof(ShowHeader)); }
}
