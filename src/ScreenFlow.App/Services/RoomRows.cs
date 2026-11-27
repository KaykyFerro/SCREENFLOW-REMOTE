using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.Windows.Media;
using ScreenFlow.Core;
using ScreenFlow.Core.Diagnostics;
using ScreenFlow.Core.Media;
using ScreenFlow.Core.Network;
using ScreenFlow.Core.Room;
using ScreenFlow.Infrastructure.Room;

namespace ScreenFlow.App.Services;

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
    private bool _viaServer;
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
    /// <summary>A porta de controle de quem transmite (a do servidor, quando a transmissão vai por ele).</summary>
    public int WatchPort { get; private set; } = AppInfo.DefaultControlPort;
    /// <summary>(1.4.1) A transmissão está no servidor ScreenFlow: assistir é pedir ao servidor a transmissão desta pessoa.</summary>
    public bool ViaServer { get => _viaServer; private set => Set(ref _viaServer, value, nameof(ViaServer)); }
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
        WatchPort = presence.ControlPort;
        ViaServer = presence.LiveViaServer;
        var status = presence.Live
            ? (presence.LiveMode == StreamMode.AudioOnly ? "Ao vivo · festa de música" : $"Ao vivo · {presence.LiveSummary}")
              + (presence.LiveViaServer ? " · pelo servidor" : presence.Flags.HasFlag(PresenceFlags.PinRequired) ? " · com PIN" : string.Empty)
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
    private bool _serverRoom;
    private bool _onServer;
    private string? _owner;
    private string? _displayName;
    private string? _spaceName;

    public RoomChannel Channel { get; } = channel;
    public string Key { get; } = channel.Key;
    /// <summary>O nome do canal nos pacotes (num servidor por rede, o id).</summary>
    public string Name => Channel.Name;

    /// <summary>(1.5.0) O nome que aparece (num servidor por rede, o nome guardado no servidor; nas outras salas, o próprio nome).</summary>
    public string DisplayName { get => _displayName ?? Name; set => Set(ref _displayName, value, nameof(DisplayName)); }

    /// <summary>(1.5.0) O nome do servidor por rede deste canal (null na sala da rede).</summary>
    public string? SpaceName { get => _spaceName; set => Set(ref _spaceName, value, nameof(SpaceName), nameof(Topic), nameof(CanEdit), nameof(CanDelete)); }
    public bool IsDefaultText => RoomChannels.IsDefaultText(Channel.Name);
    public ObservableCollection<ChatRow> Messages { get; } = [];

    /// <summary>Está na lista de canais agora.</summary>
    internal bool Listed { get; set; }

    public int Unread { get => _unread; set => Set(ref _unread, Math.Max(0, value), nameof(Unread), nameof(HasUnread), nameof(UnreadText)); }
    public bool HasUnread => _unread > 0;
    public string UnreadText => _unread > 99 ? "99+" : _unread.ToString(CultureInfo.InvariantCulture);
    public bool IsSelected { get => _selected; set => Set(ref _selected, value, nameof(IsSelected)); }
    public bool IsMine { get => _mine; set => Set(ref _mine, value, nameof(IsMine), nameof(Topic), nameof(CanEdit)); }
    /// <summary>(1.4.1) A sala está no servidor ScreenFlow.</summary>
    public bool ServerRoom { get => _serverRoom; set => Set(ref _serverRoom, value, nameof(ServerRoom), nameof(Topic)); }
    /// <summary>(1.4.1) É um canal do servidor (qualquer pessoa nele muda o nome ou apaga).</summary>
    public bool OnServer { get => _onServer; set => Set(ref _onServer, value, nameof(OnServer), nameof(Topic), nameof(CanEdit)); }
    public string? Owner { get => _owner; set => Set(ref _owner, value, nameof(Owner), nameof(Topic)); }

    /// <summary>Dá para mudar o nome: um canal seu, qualquer canal do servidor ScreenFlow, ou (1.5.0) qualquer canal de um servidor por rede (o #geral também).</summary>
    public bool CanEdit => SpaceName is not null || (!Channel.IsDefault && (IsMine || OnServer));

    /// <summary>Dá para apagar (o #geral nunca).</summary>
    public bool CanDelete => !Channel.IsDefault && CanEdit;

    /// <summary>O assunto do canal, embaixo do nome na conversa.</summary>
    public string Topic => SpaceName is { } space
        ? Channel.IsDefault
            ? $"Conversa de todo mundo do servidor {space}. Só quem está nesta rede (com a chave dela) vê."
            : $"Canal do servidor {space}: fica guardado com todo mundo dele, e qualquer pessoa do servidor muda o nome ou apaga (botão direito no canal)."
        : Key switch
    {
        "geral" => ServerRoom
            ? "Conversa de todo mundo do servidor. Fica guardada nele, e quem ainda está numa versão antiga do ScreenFlow também vê este canal."
            : "Conversa de todo mundo da rede. Quem ainda está numa versão antiga do ScreenFlow também vê este canal.",
        _ => OnServer ? "Canal do servidor: fica guardado nele, e qualquer pessoa no servidor pode mudar o nome ou apagar (botão direito no canal)."
            : IsMine ? "Canal criado por você. Aparece para todo mundo da sala enquanto você estiver nela (botão direito no canal para mudar o nome ou apagar)."
            : Owner is { } owner ? $"Canal criado por {owner}."
            : ServerRoom ? "Este canal não está mais no servidor. A conversa fica aqui até você fechar o ScreenFlow."
            : "Canal criado por alguém da sala.",
    };
}

/// <summary>Um canal de voz da sala e quem está nele.</summary>
public sealed class VoiceChannelRow : RoomRowBase
{
    private bool _current;
    private bool _mine;
    private bool _onServer;
    private string? _owner;
    private string? _displayName;
    private string? _spaceName;

    public VoiceChannelRow(RoomChannel channel)
    {
        Channel = channel;
        Key = channel.Key;
        Members.CollectionChanged += OnMembersChanged;
    }

    public RoomChannel Channel { get; }
    public string Key { get; }
    /// <summary>O nome do canal nos pacotes (num servidor por rede, o id).</summary>
    public string Name => Channel.Name;

    /// <summary>(1.5.0) O nome que aparece.</summary>
    public string DisplayName { get => _displayName ?? Name; set => Set(ref _displayName, value, nameof(DisplayName), nameof(Hint)); }

    /// <summary>(1.5.0) O nome do servidor por rede deste canal (null na sala da rede).</summary>
    public string? SpaceName { get => _spaceName; set => Set(ref _spaceName, value, nameof(SpaceName), nameof(Hint), nameof(CanEdit), nameof(CanDelete)); }

    public ObservableCollection<VoiceMemberRow> Members { get; } = [];
    public bool HasMembers => Members.Count > 0;
    public string CountText => Members.Count == 0 ? string.Empty : Members.Count.ToString(CultureInfo.InvariantCulture);

    /// <summary>Você está neste canal.</summary>
    public bool IsCurrent { get => _current; set => Set(ref _current, value, nameof(IsCurrent), nameof(Hint)); }
    public bool IsMine { get => _mine; set => Set(ref _mine, value, nameof(IsMine), nameof(Hint), nameof(CanEdit)); }
    /// <summary>(1.4.1) É um canal do servidor (qualquer pessoa nele muda o nome ou apaga).</summary>
    public bool OnServer { get => _onServer; set => Set(ref _onServer, value, nameof(OnServer), nameof(Hint), nameof(CanEdit)); }
    public string? Owner { get => _owner; set => Set(ref _owner, value, nameof(Owner), nameof(Hint)); }

    /// <summary>Dá para mudar o nome: um canal seu, do servidor ScreenFlow ou (1.5.0) de um servidor por rede (a Geral também).</summary>
    public bool CanEdit => SpaceName is not null || (!Channel.IsDefault && (IsMine || OnServer));

    /// <summary>Dá para apagar (a Geral nunca).</summary>
    public bool CanDelete => !Channel.IsDefault && CanEdit;

    /// <summary>A dica ao passar o mouse: o que o clique faz.</summary>
    public string Hint
    {
        get
        {
            var action = IsCurrent ? "Você está neste canal" : Members.Count switch
            {
                0 => $"Entrar no canal {DisplayName}",
                1 => $"Entrar no canal {DisplayName} (1 pessoa)",
                var n => $"Entrar no canal {DisplayName} ({n} pessoas)",
            };
            if (SpaceName is { } space) return $"{action} · servidor {space}";
            return Channel.IsDefault ? action : OnServer ? $"{action} · canal do servidor" : IsMine ? $"{action} · criado por você"
                : Owner is { } owner ? $"{action} · criado por {owner}" : action;
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
    /// <summary>O canal de texto da mensagem ("geral" nas das versões antigas). Num servidor por rede, o id do canal.</summary>
    public string ChannelName { get; internal set; }

    /// <summary>(1.5.0) O nome que aparece do canal (para os avisos).</summary>
    public string ChannelDisplay { get; internal set; } = string.Empty;

    /// <summary>(1.5.0) O servidor por rede da mensagem (null: a sala da rede).</summary>
    public Guid? ServerId { get; init; }

    /// <summary>(1.5.0) O nome do servidor por rede da mensagem (null: a sala da rede).</summary>
    public string? ServerName { get; init; }
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

/// <summary>(1.5.0) Como está a conexão de um servidor.</summary>
public enum ServerConnection { Online, Searching, NoNetwork, Disconnected, NeedsKey, Failed }

/// <summary>
/// (1.5.0) Um servidor na lista à esquerda do chat, como no Discord: a inicial (ou o ícone da sala da rede), as
/// mensagens não lidas, a bolinha da conexão e quantas pessoas estão nele.
/// </summary>
public sealed class ServerRailRow(RoomServer server) : RoomRowBase
{
    private static readonly AvatarColors[] SpaceColors =
    [
        AvatarColors.For(new Guid("00000000-0000-0000-0000-000000000006")),
        AvatarColors.For(new Guid("00000000-0000-0000-0000-000000000001")),
        AvatarColors.For(new Guid("00000000-0000-0000-0000-000000000002")),
        AvatarColors.For(new Guid("00000000-0000-0000-0000-000000000003")),
        AvatarColors.For(new Guid("00000000-0000-0000-0000-000000000004")),
        AvatarColors.For(new Guid("00000000-0000-0000-0000-000000000005")),
    ];

    private string _name = string.Empty;
    private int _unread;
    private bool _selected;
    private bool _inVoice;
    private ServerConnection _connection;
    private int _online;

    public RoomServer Server { get; } = server;
    public bool IsSpace => Server.IsSpace;
    public bool IsNetworkRoom => !Server.IsSpace;
    public string Name { get => _name; private set => Set(ref _name, value, nameof(Name), nameof(Initials), nameof(Hint)); }

    /// <summary>Até duas letras do nome ("Amigos do CS" vira "AC").</summary>
    public string Initials
    {
        get
        {
            var words = _name.Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(w => char.IsLetterOrDigit(w[0])).ToList();
            if (words.Count == 0) return AvatarColors.Initial(_name);
            var first = char.ToUpperInvariant(words[0][0]).ToString();
            return words.Count > 1 ? first + char.ToUpperInvariant(words[1][0]) : first;
        }
    }

    public AvatarColors Colors => SpaceColors[Math.Abs(Server.Space?.Color ?? 0) % SpaceColors.Length];
    public int Unread { get => _unread; private set => Set(ref _unread, value, nameof(Unread), nameof(HasUnread), nameof(UnreadText), nameof(Hint)); }
    public bool HasUnread => _unread > 0;
    public string UnreadText => _unread > 99 ? "99+" : _unread.ToString(CultureInfo.InvariantCulture);
    public bool IsSelected { get => _selected; set => Set(ref _selected, value, nameof(IsSelected)); }

    /// <summary>Você está na voz deste servidor.</summary>
    public bool InVoice { get => _inVoice; private set => Set(ref _inVoice, value, nameof(InVoice), nameof(Hint)); }

    public ServerConnection Connection
    {
        get => _connection;
        private set => Set(ref _connection, value, nameof(Connection), nameof(IsOnline), nameof(IsSearching), nameof(IsOffline), nameof(Hint), nameof(StatusText));
    }

    public bool IsOnline => _connection == ServerConnection.Online;
    public bool IsSearching => _connection == ServerConnection.Searching;
    public bool IsOffline => _connection is ServerConnection.Disconnected or ServerConnection.NoNetwork or ServerConnection.NeedsKey or ServerConnection.Failed;
    public int Online { get => _online; private set => Set(ref _online, value, nameof(Online), nameof(Hint), nameof(StatusText)); }

    /// <summary>"3 online", "Procurando quem está na rede…", "Desconectado"…</summary>
    public string StatusText => _connection switch
    {
        ServerConnection.Online => _online == 1 ? "1 online" : $"{_online} online",
        ServerConnection.Searching => "Só você por enquanto (procurando quem está na rede)",
        ServerConnection.NoNetwork => Server.Space?.Adapter is null ? "O Radmin VPN está desligado neste PC" : "O adaptador escolhido está desligado",
        ServerConnection.Disconnected => "Desconectado",
        ServerConnection.NeedsKey => "Precisa da senha da rede de novo",
        _ => "Não abriu",
    };

    public string Hint
    {
        get
        {
            var line = $"{_name} · {StatusText}";
            if (_inVoice) line += " · você está na voz";
            if (_unread > 0) line += _unread == 1 ? " · 1 mensagem nova" : $" · {_unread} mensagens novas";
            return line;
        }
    }

    public void Update()
    {
        Name = Server.DisplayName;
        Unread = Server.Unread;
        InVoice = Server.InVoice;
        Connection = Server.Connection;
        Online = Server.OnlineCount;
        Notify(nameof(Colors));
    }
}
