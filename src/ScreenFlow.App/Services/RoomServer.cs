using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Threading;
using ScreenFlow.Core;
using ScreenFlow.Core.Diagnostics;
using ScreenFlow.Core.Media;
using ScreenFlow.Core.Network;
using ScreenFlow.Core.Room;
using ScreenFlow.Core.Security;
using ScreenFlow.Core.Server;
using ScreenFlow.Core.Settings;
using ScreenFlow.Infrastructure.Network;
using ScreenFlow.Infrastructure.Room;

namespace ScreenFlow.App.Services;

/// <summary>
/// Um servidor visto pela interface, como no Discord (1.3.0): os canais de texto com as conversas, os canais de voz com
/// quem está em cada um, quem está nele e o chat guardado. É a sala da rede (todo mundo do Radmin, como nas versões
/// antigas, com a sala privada e o servidor ScreenFlow da 1.4.x) ou, desde a 1.5.0, um servidor de uma rede do Radmin
/// (<see cref="Space"/>), com a própria chave, os próprios canais e o próprio histórico. Tudo aqui roda na thread da
/// interface; o que chega da rede é trazido para cá antes de mexer nas listas. Quem junta os servidores é o
/// <see cref="RoomService"/>.
/// </summary>
public sealed class RoomServer
{
    private static readonly TimeSpan GroupWindow = TimeSpan.FromMinutes(5);
    private static readonly StringComparer Names = StringComparer.Create(CultureInfo.GetCultureInfo("pt-BR"), ignoreCase: true);

    /// <summary>Mensagens mostradas por canal de texto (as mais antigas saem da tela).</summary>
    public const int ChannelCapacity = 300;

    private readonly RoomService _owner;
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _speakingTimer;
    private readonly Dictionary<Guid, RoomPeer> _peers = [];
    private readonly Dictionary<Guid, DateTimeOffset> _liveNoticed = [];
    /// <summary>(1.5.0) Os canais do servidor por rede (null na sala da rede).</summary>
    private readonly SpaceChannelTable? _table;
    /// <summary>(1.5.0) O histórico do servidor guardado (cifrado) neste PC.</summary>
    private EncryptedHistory<RoomChatMessage>? _history;
    /// <summary>Os canais de texto pelo nome comparável, cada um com a sua conversa (mesmo fora da lista).</summary>
    private readonly Dictionary<string, TextChannelRow> _textRows = [];
    private readonly Dictionary<string, VoiceChannelRow> _voiceRows = [];
    /// <summary>Uma linha por pessoa na voz: a mesma aparece no canal dela e no painel do menu lateral.</summary>
    private readonly Dictionary<Guid, VoiceMemberRow> _memberRows = [];
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
    private int _photosShown = -1;
    private bool _joining;
    private bool _demo;
    private bool _demoVoice;
    private string _demoVoiceChannel = RoomChannels.DefaultVoiceName;
    private bool _demoMuted;
    private bool _demoDeafened;
    private readonly List<(string Channel, VoiceMemberState State)> _demoMembers = [];
    private readonly List<(RoomChannel Channel, string Owner)> _demoChannels = [];

    public RoomServer(RoomService owner, Dispatcher dispatcher, RoomSpace? space)
    {
        _owner = owner;
        _dispatcher = dispatcher;
        Space = space;
        if (space is not null) _table = new SpaceChannelTable(space.Channels);
        Row = new ServerRailRow(this);
        _speakingTimer = new DispatcherTimer(DispatcherPriority.Background, dispatcher) { Interval = TimeSpan.FromMilliseconds(150) };
        _speakingTimer.Tick += (_, _) => UpdateSpeaking();
        _currentText = TextRow(null);
        _currentText.IsSelected = true;
        RebuildChannels();
    }

    /// <summary>(1.5.0) O servidor por rede do Radmin (null: a sala da rede).</summary>
    public RoomSpace? Space { get; }

    public bool IsSpace => Space is not null;

    /// <summary>O nome que aparece na lista de servidores.</summary>
    public string DisplayName => Space?.Name ?? "Sala da rede";

    /// <summary>A linha deste servidor na lista de servidores (à esquerda do chat).</summary>
    public ServerRailRow Row { get; }

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

    /// <summary>O canal de voz em que você está (null fora da voz). Num servidor por rede, é o id do canal.</summary>
    public string? VoiceChannel => _demo ? (_demoVoice ? _demoVoiceChannel : null) : _room?.VoiceChannel;

    /// <summary>(1.5.0) O nome que aparece de um canal de voz (num servidor por rede, os pacotes levam o id).</summary>
    public string VoiceChannelName(string? channel)
        => _table is { } table ? table.NameOf(RoomChannelKind.Voice, channel) ?? RoomChannels.DefaultVoiceName : channel ?? RoomChannels.DefaultVoiceName;

    /// <summary>(1.5.0) O nome que aparece de um canal de texto.</summary>
    public string TextChannelName(string? channel)
        => _table is { } table ? table.NameOf(RoomChannelKind.Text, channel) ?? RoomChannels.DefaultTextName
            : RoomChannels.IsDefaultText(channel) ? RoomChannels.DefaultTextName : channel!;

    /// <summary>Fora da voz, o canal em que o botão Entrar põe você: o que tem mais gente, senão o último em que você esteve.</summary>
    public string SuggestedVoiceChannel { get; private set; } = RoomChannels.DefaultVoiceName;

    /// <summary>Quantas pessoas estão em algum canal de voz, você incluído.</summary>
    public int VoiceCount => _memberRows.Count;

    /// <summary>Quantas pessoas da sala estão numa versão sem canais (1.2 ou mais antiga): só veem o #geral.</summary>
    public int OldVersionCount => _peers.Values.Count(p => !p.Presence.SupportsChannels);

    public Guid SelfId => _owner.SelfId;
    public string SelfName => AppServices.Settings.Current.DeviceName;

    /// <summary>A sala abriu e está funcionando (ou são os dados de exemplo das capturas).</summary>
    public bool IsAvailable => _demo || _room?.IsRunning == true;

    /// <summary>Por que a sala não está funcionando (null quando está).</summary>
    public string? Error => _demo || _room?.IsRunning == true ? null
        : Space is { Connected: false } ? "Este servidor está desconectado. Clique com o botão direito nele e escolha Conectar."
        : Space is { Secret: null } ? "A chave deste servidor não abriu neste PC. Edite a rede (botão direito no servidor) e digite a senha de novo."
        : _room?.Error ?? (IsSpace ? "O servidor ainda não abriu." : "A sala da rede ainda não abriu.");

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

    /// <summary>(1.4.1) Os endereços de quem está na sala direta (vazio pelo servidor). Pode ser lido de qualquer thread.</summary>
    public IReadOnlyList<IPAddress> PeerAddresses => _room?.PeerAddresses ?? [];

    // ───────────────────────── Ciclo de vida ─────────────────────────

    /// <summary>
    /// Abre o servidor na porta dividida (<paramref name="hub"/>). A sala da rede abre como nas versões antigas (sala
    /// privada, servidor ScreenFlow, Tailscale, chat guardado); um servidor por rede abre com a chave dele, os canais e o
    /// histórico guardados neste PC.
    /// </summary>
    public void Start(RoomHub? hub)
    {
        if (_room is not null || _demo) return;
        if (Space is { } space && (!space.Connected || space.Secret is null))
        {
            UpdateRow();
            return;
        }
        var settings = AppServices.Settings.Current;
        var blobs = AppServices.Blobs;
        var room = new NetworkRoom(SelfId, settings.DiscoveryPort, new RoomLocalState(settings.DeviceName, Avatar: SelfAvatarRef()), hub)
        {
            MicrophoneId = settings.MicrophoneId,
            SpeakerId = settings.SpeakerId,
            PushToTalk = settings.PushToTalk,
            // (1.5.0) O volume da voz é o "VOIP" do mixer (AudioMixer); a sala fica em 100%.
            Sensitivity = settings.VoiceSensitivity,
            MicGainDb = settings.MicGainDb,
            AutoGain = settings.VoiceAutoGain,
            Leveler = settings.VoiceLeveler,
            SoundsEnabled = settings.VoiceSounds,
            // Quem pede um arquivo que este PC tem (a sua foto, uma imagem do chat) recebe daqui.
            BlobSource = blobs.Get,
            HasBlob = blobs.Has,
            CreatedChannels = IsSpace ? [] : settings.MyChannels,
            SpaceChannels = _table,
            BroadcastTargets = IsSpace ? SpaceBroadcasts : () => NetworkRoom.RadminBroadcasts(includeLan: false),
        };
        // O volume de cada pessoa que você ajustou continua o mesmo nas próximas vezes.
        foreach (var (key, volume) in settings.PeerVolumes)
            if (Guid.TryParseExact(key, "N", out var peer)) room.SetPeerVolume(peer, (float)volume);
        room.SetMuted(settings.VoiceMuted);
        foreach (var key in settings.BlockedPeers)
            if (Guid.TryParseExact(key, "N", out var blocked)) room.SetBlocked(blocked, true);
        if (Space is { Secret: { } secret })
        {
            // (1.5.0) Tudo do servidor vai lacrado com a chave dele: quem está em outra rede nem vê.
            room.UseSpaceKey(secret);
            room.SpaceChannelsChanged += () => _dispatcher.BeginInvoke(DispatcherPriority.Background, OnSpaceChannelsChanged);
        }
        else
        {
            // Sala privada (1.4.0).
            if (RoomInvite.TryParse(settings.PrivateRoomCode, out var invite))
                room.UsePrivateRoom(invite, settings.PrivateRoomApprovedOnly, Approved(settings));
            room.KeyChanged += next => _dispatcher.BeginInvoke(() => OnKeyChanged(next));
            room.PendingChanged += () => _dispatcher.BeginInvoke(OnPendingChanged);
        }
        room.PeersChanged += QueueRefresh;
        room.VoiceChanged += QueueRefresh;
        room.VoiceChanged += () => _dispatcher.BeginInvoke(_owner.UpdateTalkKey);
        room.MessageReceived += message => _dispatcher.BeginInvoke(() => OnMessage(message));
        room.BlobReceived += (id, data) =>
        {
            blobs.Save(id, data);
            _dispatcher.BeginInvoke(() => _owner.OnBlobArrived(id));
        };
        room.BlobFailed += id => _dispatcher.BeginInvoke(() => OnBlobFailed(id));
        room.PeerWentLive += peer => _dispatcher.BeginInvoke(() => OnPeerWentLive(peer.Id));
        room.HostsChanged += () => _dispatcher.BeginInvoke(() => HostsChanged?.Invoke());
        room.ServerChanged += () => _dispatcher.BeginInvoke(DispatcherPriority.Background, OnRoomServerChanged);
        _room = room;
        if (Space is not null) LoadHistory(room);
        room.Start();
        AppServices.Settings.Saved += OnSettingsSaved;
        if (Space is { } joined)
        {
            foreach (var text in joined.ManualPeers)
                if (ParsePeer(text) is { } endpoint) room.AddManualPeer(endpoint.Address, endpoint.Port);
            if (joined.LastTextChannel is { } lastText) SelectText(lastText);
        }
        else
        {
            foreach (var text in settings.RoomPeers)
                if (IPAddress.TryParse(text, out var address)) room.AddManualPeer(address);
            // Servidor ScreenFlow (1.4.1): a sala vai pelo servidor configurado, e volta para a sala direta sem ele.
            AppServices.Server.Changed += ApplyServer;
            ApplyServer();
            // Tailscale (1.4.1): sem servidor, a presença vai direto para os PCs da rede do Tailscale.
            _tailscaleTimer.Tick += (_, _) => FindTailscalePeers();
            _tailscaleTimer.Start();
            FindTailscalePeers();
            if (settings.LastTextChannel is { } last) SelectText(last);
            _ = UpdateArchiveAsync();
        }
        QueueRefresh();
        UpdateRow();
    }

    /// <summary>(1.5.0) Fecha o servidor (para desconectar, mudar a rede ou remover). Dá para abrir de novo com <see cref="Start"/>.</summary>
    public async Task StopAsync()
    {
        AppServices.Settings.Saved -= OnSettingsSaved;
        if (!IsSpace) AppServices.Server.Changed -= ApplyServer;
        _speakingTimer.Stop();
        _tailscaleTimer.Stop();
        var room = _room;
        _room = null;
        if (room is not null) await room.DisposeAsync();
        var archive = _archive;
        _archive = null;
        _archiveVersion++;
        if (archive is not null) await CloseArchiveAsync(archive);
        _peers.Clear();
        Refresh(force: true);
        UpdateRow();
    }

    /// <summary>(1.5.0) Por qual adaptador o servidor fala: os do Radmin (padrão), todos, ou um escolhido.</summary>
    private IReadOnlyList<IPAddress> SpaceBroadcasts()
        => SpaceAdapters(Space?.Adapter).Select(a => a.Broadcast).Distinct().ToArray();

    /// <summary>Os adaptadores ligados que o servidor usa (vazio: a rede dele está desligada).</summary>
    internal static IReadOnlyList<NetworkAdapterInfo> SpaceAdapters(string? adapter)
    {
        var up = RadminNetwork.GetIPv4Adapters()
            .Where(a => a.IsUp && !IPAddress.IsLoopback(a.Address) && !a.Address.ToString().StartsWith("169.254.", StringComparison.Ordinal));
        return adapter switch
        {
            null => up.Where(a => a.IsRadmin).ToList(),
            "*" => up.ToList(),
            _ => up.Where(a => string.Equals(a.Name, adapter, StringComparison.OrdinalIgnoreCase) || string.Equals(a.Description, adapter, StringComparison.OrdinalIgnoreCase)).ToList(),
        };
    }

    internal static IPEndPoint? ParsePeer(string text)
    {
        if (RoomSpace.CleanPeer(text) is not { } clean) return null;
        var colon = clean.IndexOf(':');
        var address = IPAddress.Parse(colon > 0 ? clean[..colon] : clean);
        var port = colon > 0 ? int.Parse(clean[(colon + 1)..], CultureInfo.InvariantCulture) : AppServices.Settings.Current.DiscoveryPort;
        return new IPEndPoint(address, port);
    }

    /// <summary>(1.5.0) A rede voltou: o servidor chama todo mundo de novo.</summary>
    internal void Reconnect() => _room?.Reconnect();

    /// <summary>
    /// (1.5.0) Abre o histórico guardado do servidor (as últimas mensagens), mostra e põe na sala, para quem chegar
    /// depois também receber.
    /// </summary>
    private void LoadHistory(NetworkRoom room)
    {
        if (Space is not { } space) return;
        var folder = Path.Combine(AppInfo.DataDirectory, "servers");
        var path = Path.Combine(folder, $"{space.Id:N}.history");
        if (space.HistoryKey is null)
        {
            // Sem a chave (outro PC ou outra conta do Windows): o histórico antigo não abre; começa um novo.
            space.HistoryKey = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);
            try { if (File.Exists(path)) File.Delete(path); } catch (IOException) { }
            _owner.SaveSpacesSoon();
        }
        _history = new EncryptedHistory<RoomChatMessage>(path, space.HistoryKey);
        var messages = _history.Load(ChatHistory.Capacity);
        room.Preload(messages);
        foreach (var message in messages) AddMessage(message);
    }

    /// <summary>(1.5.0) Apaga o histórico guardado deste servidor (ao remover o servidor ou mudar a rede dele).</summary>
    internal void DeleteHistory()
    {
        _history?.Delete();
        _history = null;
        if (Space is { } space)
        {
            try
            {
                var path = Path.Combine(AppInfo.DataDirectory, "servers", $"{space.Id:N}.history");
                if (File.Exists(path)) File.Delete(path);
            }
            catch (IOException exception) { Log.Warn($"Histórico do servidor não apagado: {exception.Message}"); }
        }
        foreach (var row in _textRows.Values)
        {
            row.Messages.Clear();
            row.Unread = 0;
        }
        _shown.Clear();
        UnreadChanged?.Invoke();
    }

    private void OnSettingsSaved(AppSettings settings)
    {
        if (!_dispatcher.CheckAccess())
        {
            _dispatcher.BeginInvoke(() => OnSettingsSaved(settings));
            return;
        }
        if (!IsSpace && _archiveApplied != (settings.ChatArchiveEnabled, settings.ChatArchiveFolder)) _ = UpdateArchiveAsync();
        if (_room is not { } room) return;
        room.Sensitivity = settings.VoiceSensitivity;
        room.MicGainDb = settings.MicGainDb;
        room.AutoGain = settings.VoiceAutoGain;
        room.Leveler = settings.VoiceLeveler;
        room.SoundsEnabled = settings.VoiceSounds;
        if (!IsSpace) room.CreatedChannels = settings.MyChannels;
        var avatar = SelfAvatarRef();
        if (room.Local.Name != settings.DeviceName || room.Local.Avatar != avatar)
        {
            room.UpdateLocal(room.Local with { Name = settings.DeviceName, Avatar = avatar });
            Refresh();
        }
        room.PushToTalk = settings.PushToTalk;
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

    /// <summary>A sua foto como a sala anuncia (id e tamanho), se o arquivo dela estiver aqui.</summary>
    internal static RoomBlobRef? SelfAvatarRef()
    {
        var id = AppServices.Settings.Current.AvatarId;
        return id is not null && AppServices.Blobs.SizeOf(id) is { } size and <= RoomBlob.MaxAvatarBytes ? new RoomBlobRef(id, size) : null;
    }

    private ImageSource? PhotoFor(string? id) => _owner.PhotoFor(id);

    private ImageSource? PhotoOf(Guid person) => PhotoFor(person == SelfId ? AppServices.Settings.Current.AvatarId : _owner.AvatarOf(person));

    /// <summary>Um arquivo chegou: pode ser a foto de alguém ou uma imagem do chat (quem avisa é o <see cref="RoomService"/>).</summary>
    internal void OnBlobArrived(string id)
    {
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
        var value = RoomChannels.IsDefaultVoice(name) ? null : name;
        if (_demo) return;
        if (Space is { } space)
        {
            if (space.LastVoiceChannel == value) return;
            space.LastVoiceChannel = value;
            _owner.SaveSpacesSoon();
            return;
        }
        var settings = AppServices.Settings.Current;
        if (settings.LastVoiceChannel == value) return;
        settings.LastVoiceChannel = value;
        AppServices.Settings.Save();
    }

    /// <summary>O último canal de voz em que você esteve neste servidor (null: a "Geral").</summary>
    private string? LastVoice => Space is { } space ? space.LastVoiceChannel : AppServices.Settings.Current.LastVoiceChannel;

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

    /// <summary>O volume de uma pessoa só para você mudou (o <see cref="RoomService"/> guarda): vale na hora aqui.</summary>
    internal void ApplyPeerVolume(Guid id, double volume)
    {
        _room?.SetPeerVolume(id, (float)volume);
        if (_memberRows.TryGetValue(id, out var row)) row.Volume = volume;
    }

    // ───────────────────────── Apertar para falar (1.4.0) ─────────────────────────

    /// <summary>A tecla de apertar para falar (quem lê a tecla é o <see cref="RoomService"/>).</summary>
    internal void SetTalkKey(bool held) => _room?.SetTalkKey(held);

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
        ApplyBlocked(id, blocked);
        PrivateRoomChanged?.Invoke();
    }

    /// <summary>(1.5.0) Alguém foi bloqueado (ou desbloqueado) em outro servidor: vale aqui também.</summary>
    internal void ApplyBlocked(Guid id, bool blocked)
    {
        _room?.SetBlocked(id, blocked);
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

    // ───────────────────────── Servidor ScreenFlow (1.4.1) ─────────────────────────

    /// <summary>A sala está indo pelo servidor ScreenFlow (mesmo que ainda esteja entrando nele).</summary>
    public bool ViaServer => !_demo && _room?.ViaServer == true;

    /// <summary>Como está a sala no servidor (null: sala direta da rede).</summary>
    public RoomServerStatus? ServerStatus => _demo ? null : _room?.ServerStatus;

    /// <summary>A sala entrou, saiu ou mudou de estado no servidor, ou os canais dele mudaram. Na thread da interface.</summary>
    public event Action? ServerChanged;

    private IReadOnlyList<RoomChannel> _serverChannels = [];

    /// <summary>Canais de texto renomeados (a chave do nome antigo → o nome novo): o que ainda chega no nome antigo vai para o novo.</summary>
    private readonly Dictionary<string, string> _renamed = [];

    /// <summary>O canal de texto em que a mensagem aparece (seguindo os canais renomeados).</summary>
    private string ChannelOf(RoomChatMessage message)
    {
        var name = message.ChannelName;
        for (var hop = 0; hop < 8 && _renamed.TryGetValue(RoomChannels.Key(name), out var next); hop++) name = next;
        return name;
    }

    private void ApplyServer()
    {
        if (IsSpace || _room is not { } room) return;
        var target = AppServices.Server.Target;
        var was = room.ViaServer;
        room.UseServer(target?.RoomEndPoint, target?.Password);
        if (was != room.ViaServer)
        {
            _serverChannels = room.ServerChannels;
            RebuildChannels();
            Refresh();
            FindTailscalePeers();
        }
        ServerChanged?.Invoke();
    }

    // ───────────────────────── Tailscale direto entre PCs (1.4.1) ─────────────────────────

    /// <summary>No máximo tantos PCs do Tailscale recebem a presença direto.</summary>
    private const int MaxTailscalePeers = 64;

    private readonly DispatcherTimer _tailscaleTimer = new() { Interval = TimeSpan.FromSeconds(30) };
    private readonly HashSet<IPAddress> _tailscalePeers = [];
    private bool _readingTailscale;

    /// <summary>
    /// O Tailscale não repassa broadcast: sem servidor, a sala manda a presença direto para cada PC com Windows ligado na
    /// rede do Tailscale (a lista vem do próprio Tailscale, a cada 30 segundos). Pelo servidor, não precisa.
    /// </summary>
    private async void FindTailscalePeers()
    {
        if (_readingTailscale || _demo || IsSpace || _room is not { } room) return;
        if (room.ViaServer || AppServices.FindTailscale() is null)
        {
            ForgetTailscalePeers(room);
            return;
        }
        _readingTailscale = true;
        try
        {
            var status = await AppServices.Tailscale.RefreshAsync();
            if (!ReferenceEquals(_room, room) || room.ViaServer) return;
            var online = status is { Running: true }
                ? status.Peers.Where(p => p.Online && (p.Os.Length == 0 || p.Os.Equals("windows", StringComparison.OrdinalIgnoreCase)))
                    .Select(p => p.Address).Distinct().Take(MaxTailscalePeers).ToHashSet()
                : [];
            foreach (var gone in _tailscalePeers.Where(a => !online.Contains(a)).ToList()) Forget(room, gone);
            foreach (var address in online)
                if (_tailscalePeers.Add(address)) room.AddManualPeer(address);
        }
        catch (Exception exception) { Log.Debug($"PCs do Tailscale: {exception.Message}"); }
        finally { _readingTailscale = false; }
    }

    private void ForgetTailscalePeers(NetworkRoom room)
    {
        foreach (var address in _tailscalePeers.ToList()) Forget(room, address);
    }

    /// <summary>Tira um PC do Tailscale da lista direta (menos os que a pessoa adicionou pelo IP).</summary>
    private void Forget(NetworkRoom room, IPAddress address)
    {
        _tailscalePeers.Remove(address);
        if (!AppServices.Settings.Current.RoomPeers.Contains(address.ToString(), StringComparer.OrdinalIgnoreCase)) room.RemoveManualPeer(address);
    }

    private void OnRoomServerChanged()
    {
        if (_room is not { } room) return;
        var channels = room.ServerChannels;
        if (room.ViaServer) FollowRenames(_serverChannels, channels);
        _serverChannels = channels;
        RebuildChannels();
        ServerChanged?.Invoke();
        Changed?.Invoke();
    }

    // ───────────────────────── Canais ─────────────────────────

    /// <summary>
    /// Cria um canal seu: aparece para todo mundo da sala enquanto você estiver nela. Um canal que já existe só é aberto.
    /// Retorna o motivo quando não deu.
    /// </summary>
    public string? CreateChannel(RoomChannelKind kind, string name)
    {
        if (_table is { } table)
        {
            // (1.5.0) No servidor por rede, o canal fica guardado em todos os PCs do servidor (mesmo com você fora).
            var clean = RoomChannels.CleanName(kind, name);
            if (table.Visible(kind).FirstOrDefault(c => RoomChannels.SameName(c.Name, clean)) is { } same)
            {
                if (kind == RoomChannelKind.Text) SelectText(same.Id);
                return null;
            }
            if (table.Create(kind, name, SelfId, DateTimeOffset.UtcNow, out var problem) is not { } created) return problem;
            OnSpaceChannelsEdited([created]);
            if (kind == RoomChannelKind.Text) SelectText(created.Id);
            return null;
        }
        if (RoomChannels.Create(kind, name) is not { } channel) return "Escolha um nome com letras ou números.";
        var existing = kind == RoomChannelKind.Text
            ? TextChannels.Select(c => c.Channel).FirstOrDefault(c => c.Same(channel))
            : VoiceChannels.Select(c => c.Channel).FirstOrDefault(c => c.Same(channel));
        if (existing is not null && (!ViaServer || existing.IsDefault || _serverChannels.Any(c => c.Same(existing))))
        {
            if (kind == RoomChannelKind.Text) SelectText(existing.Name);
            return null;
        }
        _renamed.Remove(channel.Key);
        if (ViaServer)
        {
            // No servidor, o canal é do servidor: fica guardado nele e aparece para todo mundo, mesmo com você fora.
            if (ServerChannelProblem(RoomChannelOperation.Create, channel) is { } problem) return problem;
            if (kind == RoomChannelKind.Text) SelectText(channel.Name);
            Changed?.Invoke();
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

    /// <summary>
    /// Apaga um canal seu (no servidor, qualquer canal do servidor). Quem já conversou nele continua vendo a conversa até
    /// fechar o ScreenFlow. Retorna o motivo quando não deu.
    /// </summary>
    public string? DeleteChannel(RoomChannel channel)
    {
        if (_table is { } table)
        {
            if (table.Delete(channel.Kind, channel.Name, SelfId, DateTimeOffset.UtcNow, out var problem) is not { } deleted) return problem;
            OnSpaceChannelsEdited([deleted]);
            return null;
        }
        if (ViaServer)
        {
            if (ServerChannelProblem(RoomChannelOperation.Delete, channel) is { } problem) return problem;
            return null;
        }
        DeleteMine(channel);
        return null;
    }

    /// <summary>
    /// Renomeia um canal seu (no servidor, qualquer canal do servidor). A conversa do canal de texto vai junto para o nome
    /// novo, e quem está no canal de voz continua nele. Retorna o motivo quando não deu.
    /// </summary>
    public string? RenameChannel(RoomChannel channel, string newName)
    {
        if (_table is { } table)
        {
            // (1.5.0) Só o nome que aparece muda: o canal (e a conversa e quem está na voz) continua o mesmo.
            if (table.Rename(channel.Kind, channel.Name, newName, SelfId, DateTimeOffset.UtcNow, out var problem) is not { } edited) return problem;
            OnSpaceChannelsEdited([edited]);
            return null;
        }
        if (channel.IsDefault) return "O #geral e a voz Geral não mudam de nome.";
        if (RoomChannels.Create(channel.Kind, newName) is not { } renamed) return "Escolha um nome com letras ou números.";
        if (renamed.Same(channel))
        {
            if (renamed.Name == channel.Name) return null;
        }
        else if (renamed.IsDefault || (channel.Kind == RoomChannelKind.Text ? TextChannels.Any(c => c.Channel.Same(renamed)) : VoiceChannels.Any(c => c.Channel.Same(renamed))))
            return "Já existe um canal com esse nome.";
        if (ViaServer)
        {
            if (ServerChannelProblem(RoomChannelOperation.Rename, channel, renamed) is { } problem) return problem;
            return null;
        }
        var settings = AppServices.Settings.Current;
        var index = settings.MyChannels.FindIndex(c => c.Same(channel));
        if (index < 0) return "Só quem criou o canal pode mudar o nome dele.";
        settings.MyChannels[index] = renamed;
        AppServices.Settings.Save();
        if (_room is { } room) room.CreatedChannels = settings.MyChannels;
        ApplyRename(channel, renamed);
        RebuildChannels();
        Changed?.Invoke();
        return null;
    }

    /// <summary>(1.5.0) Você mudou canais do servidor: guarda, manda para todo mundo dele e atualiza a lista.</summary>
    private void OnSpaceChannelsEdited(IReadOnlyList<SpaceChannel> changed)
    {
        SaveChannels();
        _room?.PublishChannels(changed);
        AfterSpaceChannelsChanged();
    }

    /// <summary>(1.5.0) Alguém do servidor mudou os canais (chegou pela rede).</summary>
    private void OnSpaceChannelsChanged()
    {
        if (_table is null) return;
        SaveChannels();
        AfterSpaceChannelsChanged();
    }

    private void SaveChannels()
    {
        if (Space is not { } space || _table is not { } table) return;
        space.Channels = table.Snapshot().ToList();
        _owner.SaveSpacesSoon();
    }

    /// <summary>Um canal apagado some da lista; quem estava nele vai para o #geral (e, na voz, para a Geral).</summary>
    private void AfterSpaceChannelsChanged()
    {
        if (_table is not { } table) return;
        if (!_currentText.IsDefaultText && table.NameOf(RoomChannelKind.Text, _currentText.Name) is null) SelectText(null);
        if (_room is { InVoice: true } room && room.VoiceChannel is { } voice && !RoomChannels.IsDefaultVoice(voice)
            && table.NameOf(RoomChannelKind.Voice, voice) is null)
            room.MoveVoice(null);
        RebuildChannels();
        QueueRefresh();
        Changed?.Invoke();
    }

    /// <summary>Confere (com as regras do servidor) e manda a mudança do canal. Null quando foi.</summary>
    private string? ServerChannelProblem(RoomChannelOperation operation, RoomChannel channel, RoomChannel? renamed = null)
    {
        if (_room is not { } room || room.ServerStatus is not { State: RoomServerState.Connected })
            return "A sala ainda não entrou no servidor. Espere conectar para mexer nos canais.";
        if (ChannelStore.Problem(_serverChannels, operation, channel, renamed) is { } problem) return problem;
        return room.ChangeServerChannel(operation, channel.Kind, channel.Name, renamed?.Name) ? null : "A sala ainda não entrou no servidor.";
    }

    /// <summary>
    /// Quando os canais de alguém (ou do servidor) mudam, um canal que some junto com um que aparece, do mesmo tipo, é um
    /// canal renomeado: a conversa e quem está na voz vão para o nome novo.
    /// </summary>
    private void FollowRenames(IReadOnlyList<RoomChannel> before, IReadOnlyList<RoomChannel> after)
    {
        foreach (var kind in new[] { RoomChannelKind.Text, RoomChannelKind.Voice })
        {
            var gone = before.Where(c => c.Kind == kind && !after.Any(a => a.Same(c))).ToList();
            var added = after.Where(c => c.Kind == kind && !before.Any(b => b.Same(c))).ToList();
            if (gone.Count == 1 && added.Count == 1) ApplyRename(gone[0], added[0]);
            // Um canal novo com o nome antigo de outro é outro canal: a conversa nova fica nele.
            else foreach (var channel in added) _renamed.Remove(channel.Key);
        }
    }

    private void ApplyRename(RoomChannel old, RoomChannel renamed)
    {
        if (old.Kind == RoomChannelKind.Text)
        {
            _renamed.Remove(renamed.Key);
            if (old.Key != renamed.Key) _renamed[old.Key] = renamed.Name;
            if (!_textRows.TryGetValue(old.Key, out var from)) return;
            var to = TextRow(renamed.Name);
            if (to == from) return;
            // A conversa vai para o canal novo, na ordem de horário.
            foreach (var message in from.Messages.ToList())
            {
                var index = 0;
                while (index < to.Messages.Count && to.Messages[index].Time <= message.Time) index++;
                message.ChannelName = renamed.Name;
                to.Messages.Insert(index, message);
            }
            while (to.Messages.Count > ChannelCapacity) to.Messages.RemoveAt(0);
            to.Unread += from.Unread;
            from.Messages.Clear();
            from.Unread = 0;
            for (var i = 0; i < to.Messages.Count; i += 2) Regroup(to.Messages, i);
            if (_currentText == from) SelectText(renamed.Name);
            var settings = AppServices.Settings.Current;
            if (!_demo && settings.LastTextChannel is { } last && RoomChannels.SameName(last, old.Name))
            {
                settings.LastTextChannel = renamed.Name;
                AppServices.Settings.Save();
            }
            UnreadChanged?.Invoke();
        }
        else
        {
            if (_room is { InVoice: true } room && room.VoiceChannel is { } current && RoomChannels.SameName(current, old.Name)) room.MoveVoice(renamed.Name);
            var settings = AppServices.Settings.Current;
            if (!_demo && settings.LastVoiceChannel is { } last && RoomChannels.SameName(last, old.Name))
            {
                settings.LastVoiceChannel = renamed.Name;
                AppServices.Settings.Save();
            }
        }
    }

    private void DeleteMine(RoomChannel channel)
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
            var last = row.IsDefaultText ? null : row.Name;
            if (_demo) { }
            else if (Space is { } space)
            {
                if (space.LastTextChannel != last)
                {
                    space.LastTextChannel = last;
                    _owner.SaveSpacesSoon();
                }
            }
            else
            {
                var settings = AppServices.Settings.Current;
                if (settings.LastTextChannel != last)
                {
                    settings.LastTextChannel = last;
                    AppServices.Settings.Save();
                }
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
        if (_table is { } table)
        {
            RebuildSpaceChannels(table);
            return;
        }
        // Pelo servidor (1.4.1), os canais são os do servidor: os de cada pessoa só valem na sala direta da rede.
        var server = ViaServer;
        IReadOnlyList<RoomChannel> mine = server ? [] : AppServices.Settings.Current.MyChannels;
        var onServer = server ? _serverChannels : [];
        var owners = new Dictionary<(RoomChannelKind, string), string>();
        var created = new List<RoomChannel>();
        foreach (var channel in mine)
        {
            created.Add(channel);
            owners.TryAdd((channel.Kind, channel.Key), SelfName);
        }
        IEnumerable<(RoomChannel Channel, string? Owner)> announced = _demo
            ? _demoChannels.Select(c => (c.Channel, (string?)c.Owner))
            : server ? onServer.Select(channel => (channel, (string?)null))
            : _peers.Values.SelectMany(peer => peer.Presence.CreatedChannels.Select(channel => (channel, (string?)peer.Presence.Name)));
        foreach (var (channel, owner) in announced)
        {
            created.Add(channel);
            if (owner is not null) owners.TryAdd((channel.Kind, channel.Key), owner);
        }
        foreach (var row in _textRows.Values)
            if (row.Messages.Count > 0 || row == _currentText) created.Add(row.Channel);
        foreach (var name in _voiceInUse)
            if (RoomChannels.CleanName(RoomChannelKind.Voice, name) is { Length: > 0 } clean) created.Add(new RoomChannel(RoomChannelKind.Voice, clean));

        var text = RoomChannels.Merge(RoomChannelKind.Text, created);
        Sync(TextChannels, text.Select(c => (c.Key, c)).ToList(), row => row.Key, (_, channel) => TextRow(channel.Name), (row, channel) =>
        {
            row.IsMine = mine.Any(c => c.Same(channel));
            row.ServerRoom = server;
            row.OnServer = onServer.Any(c => c.Same(channel));
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
            row.OnServer = onServer.Any(c => c.Same(channel));
            row.Owner = owners.GetValueOrDefault((RoomChannelKind.Voice, channel.Key));
        });
        var voiceKeys = voice.Select(c => c.Key).ToHashSet();
        foreach (var key in _voiceRows.Keys.Where(k => !voiceKeys.Contains(k)).ToList()) _voiceRows.Remove(key);
    }

    /// <summary>
    /// (1.5.0) Os canais de um servidor por rede: os da lista guardada (iguais para todo mundo do servidor), com o nome
    /// que aparece. Qualquer pessoa do servidor muda o nome ou apaga.
    /// </summary>
    private void RebuildSpaceChannels(SpaceChannelTable table)
    {
        var text = table.Visible(RoomChannelKind.Text).Select(c => c.IsDefault ? RoomChannels.DefaultText : c.Channel).ToList();
        Sync(TextChannels, text.Select(c => (c.Key, c)).ToList(), row => row.Key, (_, channel) => TextRow(channel.Name), (row, channel) =>
        {
            row.SpaceName = DisplayName;
            row.DisplayName = table.NameOf(RoomChannelKind.Text, channel.Name) ?? channel.Name;
        });
        var listed = text.Select(c => c.Key).ToHashSet();
        foreach (var (key, row) in _textRows.ToList())
        {
            row.Listed = listed.Contains(key);
            if (!row.Listed && row.Messages.Count == 0 && row != _currentText) _textRows.Remove(key);
        }
        var voice = table.Visible(RoomChannelKind.Voice).Select(c => c.IsDefault ? RoomChannels.DefaultVoice : c.Channel).ToList();
        Sync(VoiceChannels, voice.Select(c => (c.Key, c)).ToList(), row => row.Key, (_, channel) => VoiceRow(channel), (row, channel) =>
        {
            row.SpaceName = DisplayName;
            row.DisplayName = table.NameOf(RoomChannelKind.Voice, channel.Name) ?? channel.Name;
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
        var last = LastVoice ?? RoomChannels.DefaultVoiceName;
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
        // (1.5.0) O servidor por rede guarda a conversa neste PC (cifrada), para continuar lá depois de fechar.
        _history?.Append(message with { History = false });
        if (row.IsMine || message.History) return;
        var channel = TextRow(row.ChannelName);
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
        var name = ChannelOf(message);
        var channel = TextRow(name);
        var row = new ChatRow(message, message.AuthorId == SelfId, fromArchive)
        {
            Photo = PhotoOf(message.AuthorId),
            ChannelName = name,
            ServerId = Space?.Id,
            ServerName = IsSpace ? DisplayName : null,
            ChannelDisplay = TextChannelName(name),
        };
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
        if (Space is { } space)
        {
            // (1.5.0) No servidor por rede, o IP fica guardado no servidor (dá para pôr a porta: 26.1.2.3:47802).
            if (ParsePeer(text) is not { } endpoint) return "Digite um IP válido, por exemplo 26.12.34.56.";
            if (_room is not { IsRunning: true } spaceRoom) return Error;
            spaceRoom.AddManualPeer(endpoint.Address, endpoint.Port);
            var clean = RoomSpace.CleanPeer(text)!;
            if (!space.ManualPeers.Contains(clean, StringComparer.OrdinalIgnoreCase))
            {
                space.ManualPeers.Insert(0, clean);
                _owner.SaveSpacesSoon();
            }
            return null;
        }
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

    private void Refresh() => Refresh(force: false);

    private void Refresh(bool force)
    {
        if (_room is not { } room)
        {
            if (!force || _demo) return;
            // Servidor fechado: ninguém na lista, ninguém na voz.
            People.Clear();
            ShowVoice([]);
            UpdateRow();
            Changed?.Invoke();
            return;
        }
        var peers = room.Peers;
        var before = new Dictionary<Guid, RoomPeer>(_peers);
        _peers.Clear();
        foreach (var peer in peers)
        {
            // Na sala direta, quem criou o canal muda o nome dele: a conversa e a voz vão junto.
            if (!room.ViaServer && before.TryGetValue(peer.Id, out var previous) && !ReferenceEquals(previous.Presence.CreatedChannels, peer.Presence.CreatedChannels))
                FollowRenames(previous.Presence.CreatedChannels, peer.Presence.CreatedChannels);
            _peers[peer.Id] = peer;
            _owner.RememberAvatar(peer.Id, peer.Presence.Avatar?.Id);
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
        if (_photosShown != _owner.PhotosVersion)
        {
            _photosShown = _owner.PhotosVersion;
            var photos = new Dictionary<Guid, ImageSource?>();
            foreach (var message in AllMessages())
            {
                if (!photos.TryGetValue(message.AuthorId, out var photo)) photos[message.AuthorId] = photo = PhotoOf(message.AuthorId);
                message.Photo = photo;
            }
        }

        _speakingTimer.IsEnabled = room.InVoice;
        if (!room.InVoice) foreach (var row in _memberRows.Values) row.Speaking = false;
        UpdateRow();
        Changed?.Invoke();
    }

    // ───────────────────────── Lista de servidores (1.5.0) ─────────────────────────

    /// <summary>Quantas pessoas estão no servidor agora, você incluído.</summary>
    public int OnlineCount => _demo && IsSpace ? _demoOnline : People.Count + (IsAvailable ? 1 : 0);

    /// <summary>Como está a conexão do servidor (para a bolinha e a dica na lista de servidores).</summary>
    public ServerConnection Connection
    {
        get
        {
            if (_demo) return Space is null ? ServerConnection.Online : _demoConnection;
            if (Space is { Connected: false }) return ServerConnection.Disconnected;
            if (Space is { Secret: null }) return ServerConnection.NeedsKey;
            if (_room is not { IsRunning: true }) return ServerConnection.Failed;
            if (Space is { } space && space.ManualPeers.Count == 0 && !AdaptersUp(space)) return ServerConnection.NoNetwork;
            return People.Count > 0 ? ServerConnection.Online : ServerConnection.Searching;
        }
    }

    internal ServerConnection _demoConnection = ServerConnection.Online;

    private (DateTimeOffset At, bool Up) _adapters;

    /// <summary>Algum adaptador do servidor está ligado (lido no máximo a cada 3 segundos).</summary>
    private bool AdaptersUp(RoomSpace space)
    {
        var now = DateTimeOffset.UtcNow;
        if (now - _adapters.At > TimeSpan.FromSeconds(3))
        {
            bool up;
            try { up = SpaceAdapters(space.Adapter).Count > 0; }
            catch (Exception exception)
            {
                Log.Debug($"Adaptadores do servidor: {exception.Message}");
                up = true;
            }
            _adapters = (now, up);
        }
        return _adapters.Up;
    }

    /// <summary>(1.5.0) O servidor mudou de nome: a lista, os canais e os assuntos mostram o nome novo.</summary>
    internal void RefreshNames()
    {
        RebuildChannels();
        UpdateRow();
        Changed?.Invoke();
    }

    /// <summary>A linha da lista de servidores acompanha o nome, as não lidas, quem está online e a conexão.</summary>
    internal void UpdateRow() => Row.Update();

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
        var ana = new Guid("0d6f3a52-8b1e-4c9a-a7f2-5e3b9c1d2f42");
        var bruno = new Guid("9a2c7e14-3f5b-4d8a-b6e1-7c4f2a9d3e51");
        var carla = new Guid("4e8b1c63-7a2d-4f9e-8c5b-1d6a3f7e2b5f");
        var diego = new Guid("c3a9e275-1b6f-4a8d-9e2c-6f1b8d4a7c73");
        var (anaPhoto, brunoPhoto, wallpaper) = DemoImages();
        _owner.RememberAvatar(ana, anaPhoto);
        _owner.RememberAvatar(bruno, brunoPhoto);
        People.Clear();
        People.Add(RoomPersonRow.Demo(ana, "Ana Lu", "Ao vivo · tela, câmera e áudio", true, StreamMode.Screen, "26.47.19.113", false, PhotoOf(ana)));
        People.Add(RoomPersonRow.Demo(bruno, "Bruno", "Online · na voz: Geral", false, StreamMode.Screen, null, true, PhotoOf(bruno)));
        People.Add(RoomPersonRow.Demo(carla, "Carla-PC", "Assistindo Ana Lu · na voz: Geral, mudo", false, StreamMode.Screen, null, true));
        People.Add(RoomPersonRow.Demo(diego, "Diego", "Online · na voz: Valorant", false, StreamMode.Screen, null, true));

        _demoChannels.Clear();
        _demoChannels.Add((new RoomChannel(RoomChannelKind.Voice, "Valorant"), "Diego"));
        _demoChannels.Add((new RoomChannel(RoomChannelKind.Text, "campeonato"), "Ana Lu"));
        _demoChannels.Add((new RoomChannel(RoomChannelKind.Text, "música"), "Diego"));
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
        UpdateRow();
    }

    /// <summary>(1.5.0) Um servidor de exemplo para as capturas: só a linha na lista, com não lidas e quem está online.</summary>
    internal void LoadDemoSpace(int unread, int online, ServerConnection connection)
    {
        _demo = true;
        _demoConnection = connection;
        _textRows.Values.First().Unread = unread;
        _demoOnline = online;
        UpdateRow();
    }

    internal int _demoOnline;

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
