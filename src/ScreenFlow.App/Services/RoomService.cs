using System.Collections.ObjectModel;
using System.Net;
using System.Windows.Media;
using System.Windows.Threading;
using ScreenFlow.Core.Diagnostics;
using ScreenFlow.Core.Network;
using ScreenFlow.Core.Room;
using ScreenFlow.Core.Settings;
using ScreenFlow.Infrastructure.Network;
using ScreenFlow.Infrastructure.Room;

namespace ScreenFlow.App.Services;

/// <summary>
/// A sala da rede e (1.5.0) os servidores por rede do Radmin, juntos, como a lista de servidores do Discord. Cada
/// servidor (<see cref="RoomServer"/>) tem as próprias pessoas, canais, voz e histórico, e nada passa de um para o outro;
/// todos dividem a porta da sala (<see cref="RoomHub"/>) e ficam ligados ao mesmo tempo (as mensagens novas de um
/// servidor que não está aberto aparecem na bolinha dele). A tela mostra o servidor aberto (<see cref="Active"/>); a voz
/// fica num servidor só por vez (<see cref="VoiceServer"/>), e o painel da voz do menu lateral mostra o dele.
/// </summary>
public sealed class RoomService
{
    private readonly Dispatcher _dispatcher;
    /// <summary>Fotos do perfil já abertas (id do arquivo → imagem; null quando o arquivo não é uma imagem).</summary>
    private readonly Dictionary<string, ImageSource?> _photos = [];
    /// <summary>A última foto que cada pessoa mostrou (fica para as mensagens de quem já saiu), de qualquer servidor.</summary>
    private readonly Dictionary<Guid, string?> _avatars = [];
    private readonly Dictionary<Guid, double> _volumeBeforeMute = [];
    private readonly List<RoomServer> _spaces = [];
    private readonly DispatcherTimer _saveTimer;
    private readonly DispatcherTimer _statusTimer;
    private readonly DispatcherTimer _reconnectTimer;
    private RoomServer _active;
    private RoomServer? _panelSource;
    private RoomHub? _hub;
    private SpaceStore? _store;
    private PushToTalkKeyWatcher? _talkKey;
    private bool _started;
    private bool _demo;

    public RoomService(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
        _saveTimer = new DispatcherTimer(DispatcherPriority.Background, dispatcher) { Interval = TimeSpan.FromMilliseconds(800) };
        _saveTimer.Tick += (_, _) => SaveSpacesNow();
        _statusTimer = new DispatcherTimer(DispatcherPriority.Background, dispatcher) { Interval = TimeSpan.FromSeconds(4) };
        _statusTimer.Tick += (_, _) =>
        {
            foreach (var server in AllServers) server.UpdateRow();
        };
        _reconnectTimer = new DispatcherTimer(DispatcherPriority.Background, dispatcher) { Interval = TimeSpan.FromSeconds(2) };
        _reconnectTimer.Tick += (_, _) => ReconnectSpaces();
        Open = Create(null);
        _active = Open;
        Open.Row.IsSelected = true;
        Servers.Add(Open.Row);
    }

    // ───────────────────────── Servidores (1.5.0) ─────────────────────────

    /// <summary>A sala da rede (todo mundo com o ScreenFlow no Radmin, como nas versões antigas).</summary>
    public RoomServer Open { get; }

    /// <summary>O servidor aberto na tela do chat.</summary>
    public RoomServer Active => _active;

    /// <summary>A sala da rede e os servidores por rede, na ordem da lista.</summary>
    public IReadOnlyList<RoomServer> AllServers => [Open, .. _spaces];

    /// <summary>A lista de servidores à esquerda do chat (a sala da rede primeiro).</summary>
    public ObservableCollection<ServerRailRow> Servers { get; } = [];

    /// <summary>O servidor em que você está na voz (ou entrando nela), se estiver.</summary>
    public RoomServer? VoiceServer => AllServers.FirstOrDefault(s => s.InVoice || s.IsJoining);

    /// <summary>O servidor do painel da voz do menu lateral: o da voz, ou o aberto.</summary>
    private RoomServer PanelServer => VoiceServer ?? _active;

    /// <summary>Trocou o servidor aberto (as listas da tela do chat são outras).</summary>
    public event Action? ActiveServerChanged;

    /// <summary>Um servidor entrou, saiu ou mudou de posição na lista.</summary>
    public event Action? ServersChanged;

    private RoomServer Create(RoomSpace? space)
    {
        var server = new RoomServer(this, _dispatcher, space);
        server.IsChatInFront = () => _active == server && IsChatInFront?.Invoke() == true;
        server.Changed += () => OnServerChanged(server);
        server.UnreadChanged += () =>
        {
            server.UpdateRow();
            UnreadChanged?.Invoke();
        };
        server.MessageArrived += row => MessageArrived?.Invoke(row);
        server.PersonWentLive += person => PersonWentLive?.Invoke(person);
        server.CurrentTextChanged += () =>
        {
            if (_active == server) CurrentTextChanged?.Invoke();
        };
        if (space is null)
        {
            server.HostsChanged += () => HostsChanged?.Invoke();
            server.ServerChanged += () => ServerChanged?.Invoke();
            server.PrivateRoomChanged += () => PrivateRoomChanged?.Invoke();
            server.PendingMember += name => PendingMember?.Invoke(name);
            server.ArchiveChanged += () => ArchiveChanged?.Invoke();
            server.ArchiveFailed += error => ArchiveFailed?.Invoke(error);
        }
        return server;
    }

    private void OnServerChanged(RoomServer server)
    {
        server.UpdateRow();
        var panel = PanelServer;
        if (server != _active && server != panel && server != _panelSource && panel == _panelSource) return;
        _panelSource = panel;
        SyncPanel();
        Changed?.Invoke();
    }

    /// <summary>O painel da voz mostra quem está no canal de voz do servidor do painel (as mesmas linhas, com o anel de quem fala).</summary>
    private void SyncPanel()
    {
        var source = PanelServer.VoiceMembers;
        for (var i = VoiceMembers.Count - 1; i >= 0; i--)
            if (!source.Contains(VoiceMembers[i])) VoiceMembers.RemoveAt(i);
        for (var i = 0; i < source.Count; i++)
        {
            var at = VoiceMembers.IndexOf(source[i]);
            if (at < 0) VoiceMembers.Insert(i, source[i]);
            else if (at != i) VoiceMembers.Move(at, i);
        }
    }

    /// <summary>Abre um servidor na tela do chat.</summary>
    public void ShowServer(RoomServer server)
    {
        if (server == _active || !AllServers.Contains(server)) return;
        _active.Row.IsSelected = false;
        _active = server;
        server.Row.IsSelected = true;
        if (!_demo)
        {
            var settings = AppServices.Settings.Current;
            var id = server.Space?.Id;
            if (settings.LastServerId != id)
            {
                settings.LastServerId = id;
                try { AppServices.Settings.Save(); } catch (Exception exception) { Log.Warn($"Configurações não salvas: {exception.Message}"); }
            }
        }
        _panelSource = PanelServer;
        SyncPanel();
        ActiveServerChanged?.Invoke();
        CurrentTextChanged?.Invoke();
        Changed?.Invoke();
        if (IsChatInFront?.Invoke() == true) server.MarkRead();
    }

    /// <summary>Abre um servidor (null: a sala da rede) num canal de texto, como ao clicar no aviso de uma mensagem.</summary>
    public void OpenChannel(Guid? serverId, string? channel)
    {
        var server = serverId is { } id ? _spaces.FirstOrDefault(s => s.Space!.Id == id) ?? Open : Open;
        ShowServer(server);
        server.SelectText(channel);
    }

    public RoomServer? FindServer(Guid id) => _spaces.FirstOrDefault(s => s.Space!.Id == id);

    /// <summary>
    /// Adiciona o servidor de uma rede do Radmin: quem digitar o mesmo nome e a mesma senha da rede cai no mesmo
    /// servidor. A chave sai do nome e da senha (fora da thread da interface, demora um pouco). Devolve o servidor ou o motivo.
    /// </summary>
    public async Task<(RoomServer? Server, string? Error)> AddNetworkServerAsync(string name, string networkName, string? password, string? adapter, IReadOnlyList<string> peers)
    {
        if (SpaceKeys.NormalizeNetworkName(networkName).Length == 0) return (null, "Digite o nome da rede do Radmin.");
        byte[] secret;
        try { secret = await Task.Run(() => SpaceKeys.FromNetwork(networkName, password)); }
        catch (ArgumentException exception) { return (null, exception.Message); }
        var space = new RoomSpace
        {
            Name = string.IsNullOrWhiteSpace(name) ? SpaceKeys.CleanNetworkName(networkName) : name,
            NetworkName = networkName,
            KeySource = SpaceKeySource.Network,
            Secret = secret,
        };
        return AddSpace(space, adapter, peers);
    }

    /// <summary>Entra num servidor pelo convite (SF2-…). O nome sugerido vem do convite, se não escolher outro.</summary>
    public (RoomServer? Server, string? Error) JoinInviteServer(string code, string? name, string? adapter, IReadOnlyList<string> peers)
    {
        if (!SpaceInvite.TryParse(code, out var invite)) return (null, "Esse convite não é válido. Confira se copiou o código inteiro (começa com SF2-).");
        var space = new RoomSpace
        {
            Name = string.IsNullOrWhiteSpace(name) ? invite.Name : name,
            KeySource = SpaceKeySource.Invite,
            Secret = invite.Secret,
        };
        return AddSpace(space, adapter, peers);
    }

    /// <summary>Cria um servidor novo com uma chave aleatória (para chamar as pessoas pelo convite).</summary>
    public (RoomServer? Server, string? Error) CreateInviteServer(string name, string? adapter, IReadOnlyList<string> peers)
    {
        var invite = SpaceInvite.Create(name);
        var space = new RoomSpace { Name = invite.Name, KeySource = SpaceKeySource.Invite, Secret = invite.Secret };
        return AddSpace(space, adapter, peers);
    }

    private (RoomServer? Server, string? Error) AddSpace(RoomSpace space, string? adapter, IReadOnlyList<string> peers)
    {
        if (_store is null) return (null, "A sala ainda não abriu.");
        space.Adapter = adapter;
        space.ManualPeers = peers.ToList();
        space.HistoryKey = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);
        space.Color = (_spaces.Count + 1) % 6;
        if (_store.Add(space) is { } problem) return (null, problem);
        var server = Create(space);
        _spaces.Add(server);
        Servers.Add(server.Row);
        server.Start(_hub);
        Log.Info($"Servidor por rede adicionado ({(space.KeySource == SpaceKeySource.Network ? "pela rede do Radmin" : "por convite")}).");
        ServersChanged?.Invoke();
        ShowServer(server);
        return (server, null);
    }

    /// <summary>Muda só o nome que aparece do servidor (para você).</summary>
    public string? RenameServer(RoomServer server, string name)
    {
        if (server.Space is not { } space) return "A sala da rede não muda de nome.";
        var clean = RoomSpace.CleanName(name);
        if (clean == space.Name) return null;
        space.Name = clean;
        SaveSpacesSoon();
        AfterRename(server);
        return null;
    }

    /// <summary>
    /// Edita um servidor: o nome que aparece, a chave (<see cref="ServerEdit.Secret"/>: da rede do Radmin ou do convite
    /// digitado de novo), o adaptador e os IPs chamados direto. Se a chave mudar, é outro servidor: os canais e a conversa
    /// guardados aqui do servidor antigo são apagados (quem pergunta antes é a janela). Devolve o motivo quando não deu.
    /// </summary>
    public async Task<string?> EditServerAsync(RoomServer server, ServerEdit edit)
    {
        if (server.Space is not { } space || _store is null || !_spaces.Contains(server)) return "Esse servidor não está mais na lista.";
        var secret = edit.Secret;
        if (secret is { Length: not 32 }) return "Chave inválida.";
        if (secret is not null && _store.FindByTag(SpaceKeys.TagText(secret)) is { } other && other.Id != space.Id)
            return $"Você já tem esse servidor (\"{other.Name}\").";
        var peers = edit.Peers.Select(RoomSpace.CleanPeer).OfType<string>().Distinct().Take(RoomSpace.MaxManualPeers).ToList();
        var name = RoomSpace.CleanName(edit.Name);
        var renamed = name != space.Name;
        var connection = space.Adapter != edit.Adapter || !space.ManualPeers.SequenceEqual(peers);
        var sameKey = secret is null || (space.Secret is { } current && current.AsSpan().SequenceEqual(secret));
        var otherServer = secret is not null && SpaceKeys.TagText(secret) != space.Tag;
        space.Name = name;
        space.Adapter = edit.Adapter;
        space.ManualPeers = peers;
        if (secret is not null && edit.NetworkName is { } networkName)
        {
            space.NetworkName = SpaceKeys.CleanNetworkName(networkName);
            space.KeySource = SpaceKeySource.Network;
        }
        if (sameKey && !connection)
        {
            SaveSpacesNow();
            if (renamed) AfterRename(server);
            return null;
        }
        if (sameKey && (!space.Connected || space.Secret is null))
        {
            // Desconectado: só guarda (abre com as mudanças ao conectar).
            SaveSpacesNow();
            if (renamed) AfterRename(server);
            server.UpdateRow();
            return null;
        }
        var channel = server.InVoice ? server.VoiceChannel : null;
        if (server.InVoice) await server.LeaveVoiceAsync();
        await server.StopAsync();
        if (sameKey)
        {
            // Mesma chave, outra conexão: o mesmo servidor reabre na hora.
            SaveSpacesNow();
            server.Start(_hub);
            if (renamed) AfterRename(server);
            if (channel is not null) _ = JoinVoiceAsync(server, channel);
            return null;
        }
        // Outra chave: outro servidor. Apaga o que era do servidor antigo e abre de novo com a chave nova.
        if (otherServer)
        {
            server.DeleteHistory();
            space.Channels = [];
            space.LastTextChannel = null;
            space.LastVoiceChannel = null;
            space.HistoryKey = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);
        }
        space.Secret = secret;
        space.Connected = true;
        SaveSpacesNow();
        var wasActive = server == _active;
        var fresh = Create(space);
        _spaces[_spaces.IndexOf(server)] = fresh;
        Servers[Servers.IndexOf(server.Row)] = fresh.Row;
        fresh.Start(_hub);
        if (wasActive)
        {
            _active = Open;
            ShowServer(fresh);
        }
        Log.Info(otherServer ? "Servidor por rede mudou de chave (é outro servidor agora)." : "Servidor por rede abriu com a chave digitada de novo.");
        ServersChanged?.Invoke();
        UnreadChanged?.Invoke();
        return null;
    }

    /// <summary>A chave do servidor de uma rede do Radmin (demora um pouco: roda fora da thread da interface).</summary>
    public static Task<byte[]> NetworkKeyAsync(string networkName, string? password) => Task.Run(() => SpaceKeys.FromNetwork(networkName, password));

    private void AfterRename(RoomServer server)
    {
        server.RefreshNames();
        ServersChanged?.Invoke();
        if (server == _active) Changed?.Invoke();
    }

    /// <summary>Conecta ou desconecta um servidor (desconectado, ele fica na lista parado, e continua assim ao abrir o ScreenFlow).</summary>
    public async Task SetServerConnectedAsync(RoomServer server, bool connected)
    {
        if (server.Space is not { } space || space.Connected == connected) return;
        space.Connected = connected;
        SaveSpacesNow();
        if (connected) server.Start(_hub);
        else
        {
            if (server.InVoice) await server.LeaveVoiceAsync();
            await server.StopAsync();
        }
        server.UpdateRow();
        if (server == _active) Changed?.Invoke();
    }

    /// <summary>Remove um servidor deste PC (a conversa guardada dele aqui é apagada). Os outros do servidor continuam nele.</summary>
    public async Task RemoveServerAsync(RoomServer server)
    {
        if (server.Space is not { } space || _store is null) return;
        if (server == _active) ShowServer(Open);
        if (server.InVoice) await server.LeaveVoiceAsync();
        await server.StopAsync();
        server.DeleteHistory();
        _spaces.Remove(server);
        Servers.Remove(server.Row);
        _store.Remove(space.Id);
        Log.Info("Servidor por rede removido.");
        ServersChanged?.Invoke();
        UnreadChanged?.Invoke();
        Changed?.Invoke();
    }

    /// <summary>Muda a posição de um servidor na lista (a sala da rede fica sempre em primeiro).</summary>
    public void MoveServer(RoomServer server, int index)
    {
        if (server.Space is not { } space || _store is null) return;
        var from = _spaces.IndexOf(server);
        index = Math.Clamp(index, 0, _spaces.Count - 1);
        if (from < 0 || from == index) return;
        _spaces.RemoveAt(from);
        _spaces.Insert(index, server);
        Servers.Move(from + 1, index + 1);
        _store.Move(space.Id, index);
        ServersChanged?.Invoke();
    }

    /// <summary>
    /// A rede mudou (o Radmin conectou, o Wi-Fi voltou): os servidores chamam todo mundo de novo. O Windows avisa várias
    /// vezes seguidas; a chamada vai uma vez só, 2 segundos depois do último aviso.
    /// </summary>
    public void OnNetworkChanged()
    {
        if (_demo) return;
        _reconnectTimer.Stop();
        _reconnectTimer.Start();
    }

    private void ReconnectSpaces()
    {
        _reconnectTimer.Stop();
        foreach (var server in _spaces)
        {
            server.Reconnect();
            server.UpdateRow();
        }
        if (_spaces.Count > 0) Log.Info("A rede mudou: os servidores chamaram todo mundo de novo.");
    }

    /// <summary>Guarda a lista de servidores daqui a pouco (várias mudanças seguidas viram uma gravação só).</summary>
    internal void SaveSpacesSoon()
    {
        if (_demo || _store is null) return;
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private void SaveSpacesNow()
    {
        _saveTimer.Stop();
        if (_demo || _store is null) return;
        try { _store.Save(); }
        catch (Exception exception) { Log.Warn($"A lista de servidores não foi gravada: {exception.Message}"); }
    }

    // ───────────────────────── O servidor aberto ─────────────────────────

    public ObservableCollection<RoomPersonRow> People => _active.People;
    public ObservableCollection<TextChannelRow> TextChannels => _active.TextChannels;
    public ObservableCollection<VoiceChannelRow> VoiceChannels => _active.VoiceChannels;
    public TextChannelRow CurrentText => _active.CurrentText;
    public ObservableCollection<ChatRow> Messages => _active.Messages;

    /// <summary>Trocou o canal de texto aberto (ou o servidor aberto).</summary>
    public event Action? CurrentTextChanged;

    /// <summary>Quantas pessoas estão na voz do servidor aberto, você incluído.</summary>
    public int VoiceCount => _active.VoiceCount;

    public int OldVersionCount => _active.OldVersionCount;

    public bool IsAvailable => _active.IsAvailable;

    public string? Error => _active.Error;

    public void SelectText(string? channel) => _active.SelectText(channel);

    public string? CreateChannel(RoomChannelKind kind, string name) => _active.CreateChannel(kind, name);

    public string? DeleteChannel(RoomChannel channel) => _active.DeleteChannel(channel);

    public string? RenameChannel(RoomChannel channel, string newName) => _active.RenameChannel(channel, newName);

    public bool Send(string text, PreparedImage? image = null) => _active.Send(text, image);

    public void RetryImage(ChatRow row) => ServerOf(row).RetryImage(row);

    public void MarkRead() => _active.MarkRead();

    public bool IsShowing(ChatRow row) => ServerOf(row).IsShowing(row);

    public string? AddPeer(string text) => _active.AddPeer(text);

    private RoomServer ServerOf(ChatRow row) => row.ServerId is { } id ? FindServer(id) ?? Open : Open;

    // ───────────────────────── Você ─────────────────────────

    public Guid SelfId { get; private set; }
    public string SelfName => AppServices.Settings.Current.DeviceName;

    /// <summary>A sua foto do perfil (null sem foto).</summary>
    public ImageSource? SelfPhoto => PhotoFor(AppServices.Settings.Current.AvatarId);

    /// <summary>O seu nome ou a sua foto mudaram (o menu lateral se atualiza).</summary>
    public event Action? ProfileChanged;

    /// <summary>Troca a sua foto (null tira). Vale na hora: quem está nos servidores baixa a nova.</summary>
    public void SetPhoto(byte[]? avatar)
    {
        var settings = AppServices.Settings.Current;
        settings.AvatarId = avatar is null ? null : AppServices.Blobs.Put(avatar);
        AppServices.Settings.Save();
        PhotosVersion++;
        if (_demo) ProfileChanged?.Invoke();
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

    /// <summary>Muda a cada foto nova (as linhas das mensagens trocam as fotos quando muda).</summary>
    internal int PhotosVersion { get; private set; }

    internal string? AvatarOf(Guid person) => _avatars.GetValueOrDefault(person);

    /// <summary>A foto que alguém mostra agora (de qualquer servidor).</summary>
    internal void RememberAvatar(Guid person, string? avatar)
    {
        if (_avatars.TryGetValue(person, out var known) && known == avatar) return;
        _avatars[person] = avatar;
        PhotosVersion++;
    }

    /// <summary>Um arquivo chegou (por qualquer servidor): a foto de alguém ou uma imagem do chat.</summary>
    internal void OnBlobArrived(string id)
    {
        _photos.Remove(id);
        PhotosVersion++;
        foreach (var server in AllServers) server.OnBlobArrived(id);
        AppServices.Direct.OnBlobArrived(id);
        AppServices.Direct.OnPhotosChanged();
    }

    // ───────────────────────── Juntando todos ─────────────────────────

    /// <summary>Mensagens de outras pessoas que chegaram sem ninguém ver, somando todos os servidores e canais.</summary>
    public int Unread => AllServers.Sum(s => s.Unread);

    /// <summary>A tela do chat está aberta e na frente (quem decide é a janela principal).</summary>
    public Func<bool>? IsChatInFront { get; set; }

    /// <summary>Pessoas, canais, voz ou mudo mudaram no servidor aberto ou no da voz: o painel da voz e a bandeja se atualizam.</summary>
    public event Action? Changed;
    public event Action? UnreadChanged;

    /// <summary>Mensagem nova de outra pessoa, de qualquer servidor (<see cref="ChatRow.ServerId"/> diz de qual).</summary>
    public event Action<ChatRow>? MessageArrived;

    /// <summary>Alguém de algum servidor começou a transmitir.</summary>
    public event Action<RoomPersonRow>? PersonWentLive;

    /// <summary>A lista de transmissões anunciadas na rede mudou (tela Assistir).</summary>
    public event Action? HostsChanged;

    /// <summary>Transmissões anunciadas na rede nos últimos segundos (de qualquer versão).</summary>
    public IReadOnlyList<DiscoveredHost> Hosts => Open.Hosts;

    /// <summary>Os endereços de quem está nos servidores (para anunciar a transmissão direto). Pode ser lido de qualquer thread.</summary>
    public IReadOnlyList<IPAddress> PeerAddresses
    {
        get
        {
            var servers = AllServers;
            return servers.Count == 1 ? Open.PeerAddresses : servers.SelectMany(s => s.PeerAddresses).Distinct().ToArray();
        }
    }

    // ───────────────────────── Ciclo de vida ─────────────────────────

    public void Start()
    {
        if (_started || _demo) return;
        _started = true;
        var settings = AppServices.Settings.Current;
        settings.Normalize();
        SelfId = Guid.ParseExact(settings.InstanceId!, "N");
        var blobs = AppServices.Blobs;
        var keep = settings.AvatarId;
        _ = Task.Run(() => blobs.Trim([keep]));
        _hub = new RoomHub(settings.DiscoveryPort);
        _hub.Start();
        _talkKey = new PushToTalkKeyWatcher(held => VoiceServer?.SetTalkKey(held));
        // (1.5.0) O chat privado abre antes das salas: cada uma anuncia a chave dele.
        AppServices.Direct.Start(SelfId);
        Open.Start(_hub);
        _store = new SpaceStore();
        foreach (var space in _store.Spaces)
        {
            var server = Create(space);
            _spaces.Add(server);
            Servers.Add(server.Row);
            server.Start(_hub);
        }
        AppServices.Settings.Saved += OnSettingsSaved;
        // O id deste PC na sala precisa continuar o mesmo nas próximas vezes.
        try { AppServices.Settings.Save(); } catch (Exception exception) { Log.Warn($"Configurações não salvas: {exception.Message}"); }
        if (settings.LastServerId is { } last && FindServer(last) is { } previous) ShowServer(previous);
        _statusTimer.Start();
        UpdateTalkKey();
        if (_spaces.Count > 0) Log.Info($"{_spaces.Count} servidor(es) por rede na lista.");
    }

    public async Task StopAsync()
    {
        AppServices.Settings.Saved -= OnSettingsSaved;
        _statusTimer.Stop();
        _reconnectTimer.Stop();
        _talkKey?.Dispose();
        _talkKey = null;
        if (_saveTimer.IsEnabled) SaveSpacesNow();
        foreach (var server in AllServers) await server.StopAsync();
        AppServices.Direct.Stop();
        _hub?.Dispose();
        _hub = null;
    }

    private void OnSettingsSaved(AppSettings settings)
    {
        if (!_dispatcher.CheckAccess())
        {
            _dispatcher.BeginInvoke(() => OnSettingsSaved(settings));
            return;
        }
        ProfileChanged?.Invoke();
        UpdateTalkKey();
    }

    /// <summary>O que este PC mostra para os servidores (transmissão, assistindo). O nome e a foto vêm sempre das configurações.</summary>
    public void UpdateLocal(RoomLocalState state)
    {
        foreach (var server in AllServers) server.UpdateLocal(state);
    }

    // ───────────────────────── Voz ─────────────────────────

    /// <summary>Quem está no canal de voz do painel (o seu, ou o que o Entrar escolhe no servidor aberto).</summary>
    public ObservableCollection<VoiceMemberRow> VoiceMembers { get; } = [];

    /// <summary>O canal de voz em que você está, com o nome que aparece (e o servidor, num servidor por rede). Null fora da voz.</summary>
    public string? VoiceChannel => VoiceServer is { VoiceChannel: { } channel } server
        ? server.IsSpace ? $"{server.VoiceChannelName(channel)} · {server.DisplayName}" : server.VoiceChannelName(channel)
        : null;

    /// <summary>Fora da voz, o canal em que o botão Entrar põe você (no servidor aberto).</summary>
    public string SuggestedVoiceChannel => PanelServer.VoiceChannelName(PanelServer.SuggestedVoiceChannel);

    public bool InVoice => VoiceServer?.InVoice == true;
    public bool Muted => PanelServer.Muted;
    public bool Deafened => PanelServer.Deafened;
    public bool IsJoining => AllServers.Any(s => s.IsJoining);

    /// <summary>O botão do microfone: mostra cortado também quando o fone está cortado.</summary>
    public bool MicrophoneOff => Muted || Deafened;

    /// <summary>Volume do microfone agora (dBFS) quando você está na voz, para o medidor das configurações.</summary>
    public double? VoiceInputLevelDb => VoiceServer?.VoiceInputLevelDb;

    /// <summary>
    /// Entra num canal de voz do servidor aberto (null: o canal que o Entrar escolhe). Na voz de outro servidor, sai de
    /// lá antes (a voz fica num servidor só). Retorna o motivo quando não deu.
    /// </summary>
    public Task<string?> JoinVoiceAsync(string? channel = null) => JoinVoiceAsync(channel is null ? PanelServer : _active, channel);

    /// <summary>Entra num canal de voz de um servidor (saindo antes da voz de outro, se estiver).</summary>
    public async Task<string?> JoinVoiceAsync(RoomServer target, string? channel)
    {
        if (!AllServers.Contains(target)) return "Esse servidor não está mais na lista.";
        if (VoiceServer is { } current && current != target) await current.LeaveVoiceAsync();
        var error = await target.JoinVoiceAsync(channel);
        UpdateTalkKey();
        _panelSource = PanelServer;
        SyncPanel();
        Changed?.Invoke();
        return error;
    }

    public async Task LeaveVoiceAsync()
    {
        if (VoiceServer is { } server) await server.LeaveVoiceAsync();
        UpdateTalkKey();
    }

    /// <summary>Liga ou desliga o microfone (em todos os servidores: vale para onde você entrar na voz).</summary>
    public void SetMuted(bool muted)
    {
        AppServices.Settings.Current.VoiceMuted = muted;
        AppServices.Settings.Save();
        foreach (var server in AllServers) server.SetMuted(muted);
    }

    /// <summary>Corta o som de todo mundo (e o seu microfone). Religar volta o microfone como estava.</summary>
    public void SetDeafened(bool deafened)
    {
        foreach (var server in AllServers) server.SetDeafened(deafened);
    }

    public void ToggleMute() => SetMuted(!MicrophoneOff);

    public void ToggleDeafen() => SetDeafened(!Deafened);

    /// <summary>O volume de uma pessoa só para você (0 = silenciada, 1 = normal, até 2). Vale em todos os servidores.</summary>
    public double PeerVolume(Guid id) => Open.PeerVolume(id);

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
        foreach (var server in AllServers) server.ApplyPeerVolume(id, volume);
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

    /// <summary>A tecla de apertar para falar só é lida com a opção ligada e você na voz.</summary>
    internal void UpdateTalkKey()
    {
        var settings = AppServices.Settings.Current;
        _talkKey?.Watch(settings.PushToTalk && VoiceServer?.InVoice == true ? settings.PushToTalkKey : null);
    }

    // ───────────────────────── Sala privada e moderação (1.4.0, na sala da rede) ─────────────────────────

    public event Action? PrivateRoomChanged;
    public event Action<string>? PendingMember;
    public string? PrivateRoomCode => Open.PrivateRoomCode;
    public bool InPrivateRoom => Open.InPrivateRoom;
    public bool IsPrivateRoomOwner => Open.IsPrivateRoomOwner;
    public string? PrivateRoomFingerprint => Open.PrivateRoomFingerprint;
    public IReadOnlyDictionary<Guid, string> PrivateRoomPending => Open.PrivateRoomPending;
    public IReadOnlyList<(Guid Id, string Name)> PrivateRoomApproved => Open.PrivateRoomApproved;
    public IReadOnlyList<(Guid Id, string Name)> Blocked => Open.Blocked;
    public string CreatePrivateRoom(bool approvedOnly) => Open.CreatePrivateRoom(approvedOnly);
    public bool JoinPrivateRoom(string code) => Open.JoinPrivateRoom(code);
    public void LeavePrivateRoom() => Open.LeavePrivateRoom();
    public void SetPrivateRoomApprovedOnly(bool approvedOnly) => Open.SetPrivateRoomApprovedOnly(approvedOnly);
    public void ApprovePrivateMember(Guid id) => Open.ApprovePrivateMember(id);
    public bool RotatePrivateRoomKey() => Open.RotatePrivateRoomKey();

    /// <summary>O dono remove alguém da sala privada: a pessoa fica bloqueada aqui, em todos os servidores.</summary>
    public bool RemovePrivateMember(Guid id, string name)
    {
        if (!Open.RemovePrivateMember(id, name)) return false;
        foreach (var server in _spaces) server.ApplyBlocked(id, true);
        return true;
    }

    /// <summary>Bloqueia (ou desbloqueia) alguém só para você, em todos os servidores: nada dele chega.</summary>
    public void SetBlocked(Guid id, string name, bool blocked)
    {
        Open.SetBlocked(id, name, blocked);
        foreach (var server in _spaces) server.ApplyBlocked(id, blocked);
    }

    // ───────────────────────── Servidor ScreenFlow (1.4.1, na sala da rede) ─────────────────────────

    /// <summary>O servidor aberto é a sala da rede e ela está indo pelo servidor ScreenFlow.</summary>
    public bool ViaServer => _active.ViaServer;

    public RoomServerStatus? ServerStatus => Open.ServerStatus;

    public event Action? ServerChanged;

    // ───────────────────────── Chat guardado (na sala da rede) ─────────────────────────

    public string? ArchiveFolder => Open.ArchiveFolder;
    public string? ArchiveProblem => Open.ArchiveProblem;
    public event Action? ArchiveChanged;
    public event Action<string>? ArchiveFailed;

    // ───────────────────────── Capturas de tela ─────────────────────────

    /// <summary>Os dados de exemplo das capturas do CI: a sala da rede cheia e três servidores na lista.</summary>
    public void LoadDemo(bool inVoice = true, int unread = 2)
    {
        _demo = true;
        SelfId = new Guid("6b1f1d5e-0c4a-4f7e-9d3b-2a8c5e7f9a0f");
        if (_spaces.Count == 0)
        {
            AddDemoSpace("Amigos do CS", 1, unread: 3, online: 4, ServerConnection.Online, rich: true);
            AddDemoSpace("Família", 3, unread: 0, online: 3, ServerConnection.Online);
            AddDemoSpace("Trabalho", 4, unread: 0, online: 1, ServerConnection.Searching);
        }
        if (_active != Open) ShowServer(Open);
        Open.LoadDemo(inVoice, unread);
        AppServices.Direct.LoadDemo(Open.People.Take(3).Select(p => (p.Id, p.Name, p.Photo)).ToList());
        _panelSource = PanelServer;
        SyncPanel();
        ProfileChanged?.Invoke();
        Changed?.Invoke();
    }

    private void AddDemoSpace(string name, int color, int unread, int online, ServerConnection connection, bool rich = false)
    {
        var server = Create(new RoomSpace { Name = name, Color = color, NetworkName = name });
        _spaces.Add(server);
        Servers.Add(server.Row);
        server.LoadDemoSpace(unread, online, connection, rich);
    }
}

/// <summary>(1.5.0) O que a janela de editar um servidor mudou.</summary>
/// <param name="Secret">A chave nova (da rede do Radmin ou do convite digitado de novo), ou null para continuar com a mesma.</param>
/// <param name="NetworkName">Com <paramref name="Secret"/> vinda da rede do Radmin, o nome da rede.</param>
public sealed record ServerEdit(string Name, byte[]? Secret, string? NetworkName, string? Adapter, IReadOnlyList<string> Peers);
