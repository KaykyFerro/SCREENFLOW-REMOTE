using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using ScreenFlow.App.Services;
using ScreenFlow.App.Windows;
using ScreenFlow.Core.Diagnostics;
using ScreenFlow.Core.Room;

namespace ScreenFlow.App.Pages;

/// <summary>
/// O chat, como no Discord: (1.5.0) os servidores na barra da esquerda (a sala da rede e os servidores por rede do
/// Radmin), os canais de texto e de voz do servidor aberto (1.3.0, com quem está em cada canal de voz), a conversa do
/// canal aberto no meio e quem está no servidor à direita.
/// </summary>
public partial class ChatPage : UserControl, IShellPage
{
    private readonly MainWindow _shell;
    private bool _stickToBottom = true;
    private PreparedImage? _attachment;
    private ObservableCollection<ChatRow>? _messages;
    /// <summary>Mostrar quem está na rede: null segue a largura da janela; o botão de pessoas escolhe.</summary>
    private bool? _peopleWanted;
    /// <summary>
    /// (1.5.0) O que estava escrito (e a imagem anexada) em cada servidor e em cada conversa privada, para não mandar no
    /// lugar errado. A chave é o <see cref="RoomServer"/> ou o id da pessoa da conversa.
    /// </summary>
    private readonly Dictionary<object, (string Text, PreparedImage? Image, List<PendingFile> Files)> _drafts = [];
    private object? _draftOwner;
    /// <summary>(1.5.0) Os arquivos anexados na caixa de escrever (cada um vira uma mensagem ao mandar).</summary>
    private readonly ObservableCollection<PendingFile> _pendingFiles = [];

    public ChatPage(MainWindow shell)
    {
        _shell = shell;
        InitializeComponent();
        var room = AppServices.Room;
        ServerList.ItemsSource = room.Servers;
        BindServer();
        _draftOwner = ComposerOwner;
        PendingFilesList.ItemsSource = _pendingFiles;
        _pendingFiles.CollectionChanged += (_, _) => UpdateComposer();
        // (1.5.0) Mensagens diretas.
        var direct = AppServices.Direct;
        DirectList.ItemsSource = direct.Conversations;
        direct.ShowingChanged += OnDirectModeChanged;
        direct.CurrentChanged += OnDirectCurrentChanged;
        direct.ConversationsChanged += UpdateDirectList;
        direct.UnreadChanged += UpdateDirectBadge;
        room.ActiveServerChanged += OnActiveServerChanged;
        room.ServersChanged += OnServersChanged;
        room.CurrentTextChanged += ShowChannel;
        room.Changed += UpdateState;
        room.Changed += UpdatePeople;
        room.ProfileChanged += UpdatePeople;
        room.ArchiveChanged += UpdateArchive;
        shell.LiveStateChanged += UpdatePeople;
        SizeChanged += (_, _) => UpdateColumns();
        Loaded += (_, _) =>
        {
            UpdateAll();
            ScrollToEnd();
        };
        ShowChannel();
        UpdateComposer();
    }

    public void OnShown()
    {
        if (DirectMode) AppServices.Direct.MarkRead();
        else AppServices.Room.MarkRead();
        UpdateAll();
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            ScrollToEnd();
            if (AppServices.Room.IsAvailable && !AppServices.DemoMode) Composer.Focus();
        });
    }

    private void UpdateAll()
    {
        UpdateState();
        UpdateColumns();
        UpdatePeople();
        UpdateArchive();
        UpdateDirectList();
        UpdateDirectBadge();
    }

    // ───────────────────────── Servidores (1.5.0) ─────────────────────────

    /// <summary>As listas da tela são as do servidor aberto.</summary>
    private void BindServer()
    {
        var room = AppServices.Room;
        TextChannelList.ItemsSource = room.TextChannels;
        VoiceChannelList.ItemsSource = room.VoiceChannels;
        PeopleList.ItemsSource = room.People;
    }

    /// <summary>Trocou o servidor aberto: as listas e a caixa de escrever passam a ser as dele.</summary>
    private void OnActiveServerChanged()
    {
        var room = AppServices.Room;
        BindServer();
        SwitchComposer();
        _stickToBottom = true;
        UpdateAll();
        if (room.IsAvailable && IsVisible && !AppServices.DemoMode && !DirectMode) Composer.Focus();
    }

    private void OnServersChanged()
    {
        var servers = AppServices.Room.AllServers;
        foreach (var gone in _drafts.Keys.OfType<RoomServer>().Where(s => !servers.Contains(s)).ToList()) _drafts.Remove(gone);
        UpdateState();
    }

    /// <summary>De quem é a caixa de escrever agora: o servidor aberto ou (1.5.0) a pessoa da conversa privada aberta.</summary>
    private static object? ComposerOwner
        => AppServices.Direct.Showing ? AppServices.Direct.Current?.Id : AppServices.Room.Active;

    /// <summary>Guarda o que estava escrito para o dono anterior e mostra o rascunho do dono de agora.</summary>
    private void SwitchComposer()
    {
        var owner = ComposerOwner;
        if (Equals(owner, _draftOwner)) return;
        if (_draftOwner is { } previous)
        {
            if (Composer.Text.Length > 0 || _attachment is not null || _pendingFiles.Count > 0) _drafts[previous] = (Composer.Text, _attachment, _pendingFiles.ToList());
            else _drafts.Remove(previous);
        }
        _draftOwner = owner;
        _pendingFiles.Clear();
        if (owner is not null && _drafts.Remove(owner, out var draft))
        {
            Composer.Text = draft.Text;
            Composer.CaretIndex = Composer.Text.Length;
            SetAttachment(draft.Image);
            foreach (var file in draft.Files) _pendingFiles.Add(file);
        }
        else
        {
            Composer.Clear();
            SetAttachment(null);
        }
        UpdatePendingFiles();
    }

    /// <summary>O cabeçalho do servidor aberto e, quando ele não está funcionando, o aviso com o que fazer.</summary>
    private void UpdateState()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(UpdateState);
            return;
        }
        if (!IsInitialized) return;
        var room = AppServices.Room;
        var server = room.Active;
        var direct = DirectMode;
        // (1.5.0) As mensagens diretas não dependem do servidor aberto: aparecem mesmo com ele desligado.
        var available = direct || room.IsAvailable;
        ContentGrid.Visibility = available ? Visibility.Visible : Visibility.Collapsed;
        ErrorPanel.Visibility = available ? Visibility.Collapsed : Visibility.Visible;
        ChannelsCard.Visibility = direct ? Visibility.Collapsed : Visibility.Visible;
        DirectCard.Visibility = direct ? Visibility.Visible : Visibility.Collapsed;
        DirectButton.IsChecked = direct;
        server.Row.IsSelected = !direct;
        UpdateServerHeader(server);
        if (available) return;
        var port = AppServices.Settings.Current.DiscoveryPort;
        if (server.Space is { } space)
        {
            var connection = server.Connection;
            var byNetwork = space.KeySource == SpaceKeySource.Network;
            ErrorTitle.Text = connection switch
            {
                ServerConnection.Disconnected => $"{server.DisplayName} está desconectado",
                ServerConnection.NeedsKey => $"A chave de {server.DisplayName} não abriu neste PC",
                _ => $"{server.DisplayName} não abriu",
            };
            ErrorText.Text = connection switch
            {
                ServerConnection.Disconnected => "Desconectado, você não aparece para ninguém do servidor e não recebe nada dele. A conversa guardada neste PC continua aqui para quando conectar de novo.",
                ServerConnection.NeedsKey => byNetwork
                    ? "A chave do servidor fica protegida pela sua conta do Windows e não abriu aqui (outra conta ou outro PC). Digite a senha da rede de novo para abrir."
                    : "A chave do servidor fica protegida pela sua conta do Windows e não abriu aqui (outra conta ou outro PC). Cole o convite do servidor de novo para abrir.",
                _ => $"{server.Error} Feche o programa que está usando a porta UDP {port} (ou mude a porta de descoberta em Configurações) e abra o ScreenFlow de novo.",
            };
            ErrorConnectButton.Visibility = connection == ServerConnection.Disconnected ? Visibility.Visible : Visibility.Collapsed;
            ErrorEditButton.Content = connection != ServerConnection.NeedsKey ? "Editar o servidor"
                : byNetwork ? "Digitar a senha de novo" : "Colar o convite de novo";
            ErrorActions.Visibility = Visibility.Visible;
        }
        else
        {
            ErrorTitle.Text = "A sala da rede está desligada";
            ErrorText.Text = $"{room.Error} Feche o programa que está usando a porta UDP {port} (ou mude a porta de descoberta em Configurações) e abra o ScreenFlow de novo.";
            ErrorActions.Visibility = Visibility.Collapsed;
        }
    }

    private void UpdateServerHeader(RoomServer server)
    {
        var space = server.IsSpace;
        ServerTitle.Text = server.DisplayName;
        NetworkTile.Visibility = space ? Visibility.Collapsed : Visibility.Visible;
        ServerTile.Visibility = space ? Visibility.Visible : Visibility.Collapsed;
        ServerMenuButton.Visibility = space ? Visibility.Visible : Visibility.Collapsed;
        if (space)
        {
            var colors = server.Row.Colors;
            ServerTile.Background = colors.Background;
            ServerTileText.Foreground = colors.Foreground;
            ServerTileText.Text = server.Row.Initials;
        }
        PeopleTitle.Text = space ? "No servidor" : "Na rede";
        PeopleToggle.ToolTip = space ? "Mostrar ou esconder quem está no servidor" : "Mostrar ou esconder quem está na rede";
        PeerHint.Text = space ? "Alguém do servidor não aparece? Adicione pelo IP (fica guardado no servidor)." : "Não está vendo alguém? Adicione pelo IP do Radmin.";
    }

    private void Server_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not ServerRailRow row) return;
        AppServices.Direct.Showing = false;
        AppServices.Room.ShowServer(row.Server);
    }

    private void Server_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement element || element.DataContext is not ServerRailRow row) return;
        e.Handled = true;
        OpenServerMenu(element, row.Server);
    }

    private void ServerMenu_Click(object sender, RoutedEventArgs e) => OpenServerMenu((FrameworkElement)sender, AppServices.Room.Active);

    /// <summary>O menu de um servidor: abrir, mudar o nome, editar, convite, conectar, mover e remover.</summary>
    private void OpenServerMenu(FrameworkElement target, RoomServer server)
    {
        var room = AppServices.Room;
        var menu = new ContextMenu { PlacementTarget = target };
        if (server != room.Active) menu.Items.Add(MenuItemFor("Abrir", "", () => room.ShowServer(server)));
        if (server.Unread > 0) menu.Items.Add(MenuItemFor("Marcar tudo como lido", "", server.MarkAllRead));
        if (server.Space is not { } space)
        {
            menu.Items.Add(MenuItemFor("Configurações da rede", "", () => _shell.OpenSettings(SettingsTab.Network)));
            menu.Items.Add(MenuItemFor("Adicionar um servidor…", "", () => AddServer()));
            menu.IsOpen = true;
            return;
        }
        if (menu.Items.Count > 0) menu.Items.Add(new Separator());
        menu.Items.Add(MenuItemFor("Mudar o nome…", "", () => RenameServer(server)));
        menu.Items.Add(MenuItemFor("Editar a rede e a conexão…", "", () => EditServer(server)));
        if (space.Secret is not null) menu.Items.Add(MenuItemFor("Copiar o convite…", "", () => ServerDialogs.ShowInvite(Window.GetWindow(this), server)));
        menu.Items.Add(space.Connected
            ? MenuItemFor("Desconectar", "", () => _ = SetConnectedAsync(server, false))
            : MenuItemFor("Conectar", "", () => _ = SetConnectedAsync(server, true)));
        var all = room.AllServers;
        var spaces = all.Count - 1;
        var index = IndexOf(all, server) - 1;
        if (spaces > 1)
        {
            menu.Items.Add(new Separator());
            if (index > 0) menu.Items.Add(MenuItemFor("Mover para cima", "", () => room.MoveServer(server, index - 1)));
            if (index < spaces - 1) menu.Items.Add(MenuItemFor("Mover para baixo", "", () => room.MoveServer(server, index + 1)));
        }
        menu.Items.Add(new Separator());
        menu.Items.Add(MenuItemFor("Remover o servidor…", "", () => _ = RemoveServerAsync(server), danger: true));
        menu.IsOpen = true;
    }

    private static int IndexOf(IReadOnlyList<RoomServer> servers, RoomServer server)
    {
        for (var i = 0; i < servers.Count; i++)
            if (servers[i] == server) return i;
        return -1;
    }

    private void AddServer_Click(object sender, RoutedEventArgs e) => AddServer();

    private void AddServer(ServerDialogs.AddMode mode = ServerDialogs.AddMode.Network)
    {
        if (ServerDialogs.Add(Window.GetWindow(this), mode, out var chosen) is not { } server) return;
        if (chosen == ServerDialogs.AddMode.Create) ServerDialogs.ShowInvite(Window.GetWindow(this), server, created: true);
        else _shell.Toast($"Servidor {server.DisplayName} adicionado. Quem estiver nele aparece em alguns segundos.", ToastKind.Success);
    }

    private void RenameServer(RoomServer server)
    {
        if (ServerDialogs.AskName(Window.GetWindow(this), server) is not { } name) return;
        if (AppServices.Room.RenameServer(server, name) is { } error) _shell.Toast(error, ToastKind.Warning);
    }

    private void EditServer(RoomServer server)
    {
        if (ServerDialogs.Edit(Window.GetWindow(this), server)) _shell.Toast("Servidor salvo.", ToastKind.Success);
    }

    private async Task SetConnectedAsync(RoomServer server, bool connected)
    {
        await AppServices.Room.SetServerConnectedAsync(server, connected);
        UpdateAll();
    }

    private async Task RemoveServerAsync(RoomServer server)
    {
        if (!ConfirmDialog.Ask(Window.GetWindow(this), $"Remover {server.DisplayName} deste PC?",
                "Você sai do servidor e a conversa guardada dele neste PC é apagada. Quem está nele continua com tudo; para voltar, adicione de novo (a mesma rede e senha, ou o convite).",
                "Remover", danger: true))
            return;
        await AppServices.Room.RemoveServerAsync(server);
        _shell.Toast($"{server.DisplayName} saiu da sua lista.", ToastKind.Success);
    }

    private void ErrorConnect_Click(object sender, RoutedEventArgs e) => _ = SetConnectedAsync(AppServices.Room.Active, true);

    private void ErrorEdit_Click(object sender, RoutedEventArgs e)
    {
        if (AppServices.Room.Active.IsSpace) EditServer(AppServices.Room.Active);
    }

    private void ErrorRemove_Click(object sender, RoutedEventArgs e)
    {
        if (AppServices.Room.Active.IsSpace) _ = RemoveServerAsync(AppServices.Room.Active);
    }

    /// <summary>
    /// Janela larga: canais, conversa e quem está na rede lado a lado. Estreita: a lista de quem está na rede some (o
    /// botão de pessoas mostra de novo), como no Discord.
    /// </summary>
    private void UpdateColumns()
    {
        if (!IsInitialized) return;
        var width = ActualWidth;
        var wide = width >= 1400;
        ChannelsColumn.Width = new GridLength(wide ? 256 : width >= 1040 ? 236 : 216);
        var showPeople = _peopleWanted ?? width >= 1040;
        // (1.5.0) Nas mensagens diretas, no lugar de quem está no servidor fica quem é a pessoa da conversa.
        var direct = DirectMode;
        PeopleCard.Visibility = showPeople && !direct ? Visibility.Visible : Visibility.Collapsed;
        DirectProfileCard.Visibility = showPeople && direct ? Visibility.Visible : Visibility.Collapsed;
        PeopleGap.Width = new GridLength(showPeople ? 14 : 0);
        PeopleColumn.Width = new GridLength(showPeople ? wide ? 290 : 256 : 0);
        PeopleToggle.IsChecked = showPeople;
    }

    private void PeopleToggle_Click(object sender, RoutedEventArgs e)
    {
        _peopleWanted = PeopleToggle.IsChecked == true;
        UpdateColumns();
    }

    private void UpdatePeople()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(UpdatePeople);
            return;
        }
        var room = AppServices.Room;
        var server = room.Active;
        var others = room.People.Count;
        var online = Math.Max(1, server.OnlineCount);
        OnlineText.Text = $"{online} online";
        NoPeople.Visibility = others == 0 ? Visibility.Visible : Visibility.Collapsed;
        NoPeopleText.Text = !server.IsSpace
            ? "Ninguém da rede apareceu ainda. Quem abrir o ScreenFlow 1.1 ou mais novo na mesma rede do Radmin aparece aqui sozinho."
            : server.Connection == ServerConnection.NoNetwork
                ? server.Space?.Adapter is null
                    ? "O Radmin VPN está desligado neste PC. Ligue e entre na rede para achar quem está no servidor."
                    : "A rede escolhida para este servidor está desligada. Ligue ou escolha outra em Editar a rede e a conexão."
                : "Ninguém do servidor apareceu ainda. Quem adicionar este servidor (a mesma rede e senha do Radmin, ou o convite) aparece aqui sozinho.";
        var voice = room.VoiceCount;
        RoomInfo.Text = (online, voice) switch
        {
            (1, 0) => "Só você online",
            (_, 0) => $"{online} online",
            (_, 1) => $"{online} online · 1 na voz",
            _ => $"{online} online · {voice} na voz",
        };

        SelfAvatar.Colors = AvatarColors.For(room.SelfId);
        SelfAvatar.Initial = AvatarColors.Initial(room.SelfName);
        SelfAvatar.Photo = room.SelfPhoto;
        SelfName.Text = $"{room.SelfName} (você)";
        var status = _shell.IsTransmitting ? $"Ao vivo · {_shell.TransmitPage.LiveSummary}" : _shell.IsWatching ? "Assistindo uma transmissão" : "Online";
        if (room.VoiceChannel is { } channel)
            status += room.Deafened ? $" · na voz: {channel}, sem som" : room.MicrophoneOff ? $" · na voz: {channel}, mudo" : $" · na voz: {channel}";
        SelfStatus.Text = status;
        if (DirectMode)
        {
            UpdateConversationHeader();
            return;
        }
        ChannelTopic.Text = room.CurrentText.Topic;
        ChannelTopic.ToolTip = room.CurrentText.Topic;
        ChannelTitle.Text = room.CurrentText.DisplayName;
        UpdateComposerHint();
    }

    // ───────────────────────── Canais ─────────────────────────

    /// <summary>Mostra a conversa do canal de texto aberto.</summary>
    private void ShowChannel()
    {
        if (DirectMode)
        {
            ShowConversation();
            return;
        }
        var room = AppServices.Room;
        ChannelHash.Text = "#";
        EmptyGlyph.Text = "#";
        SafetyPill.Visibility = Visibility.Collapsed;
        KeyBanner.Visibility = Visibility.Collapsed;
        Composer.IsEnabled = true;
        AttachButton.IsEnabled = true;
        PeopleToggle.ToolTip = room.Active.IsSpace ? "Mostrar ou esconder quem está no servidor" : "Mostrar ou esconder quem está na rede";
        if (_messages is not null) _messages.CollectionChanged -= OnMessagesChanged;
        _messages = room.Messages;
        _messages.CollectionChanged += OnMessagesChanged;
        MessageList.ItemsSource = _messages;
        var channel = room.CurrentText;
        var space = room.Active.IsSpace;
        ChannelTitle.Text = channel.DisplayName;
        ChannelTopic.Text = channel.Topic;
        ChannelTopic.ToolTip = channel.Topic;
        Composer.Tag = $"Conversar em #{channel.DisplayName}";
        EmptyTitle.Text = channel.IsDefaultText ? "Ninguém escreveu nada ainda" : $"Este é o começo do #{channel.DisplayName}";
        EmptyText.Text = (channel.IsDefaultText, space) switch
        {
            (true, false) => "Mande um oi: a mensagem vai para todo mundo que está com o ScreenFlow aberto na sua rede.",
            (true, true) => "Mande um oi: a mensagem vai só para quem está neste servidor, cifrada com a chave dele.",
            (false, false) => "Mande a primeira mensagem do canal. Quem está na sala vê o canal na lista da esquerda.",
            _ => "Mande a primeira mensagem do canal. Todo mundo do servidor vê o canal na lista da esquerda.",
        };
        NoMessages.Visibility = _messages.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        JumpButton.Visibility = Visibility.Collapsed;
        UpdateComposerHint();
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, ScrollToEnd);
    }

    private void TextChannel_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not TextChannelRow channel) return;
        AppServices.Room.SelectText(channel.Name);
        if (!AppServices.DemoMode) Composer.Focus();
    }

    private void TextChannel_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement element || element.DataContext is not TextChannelRow channel) return;
        e.Handled = true;
        var menu = new ContextMenu { PlacementTarget = element };
        menu.Items.Add(MenuItemFor("Abrir o canal", "", () => AppServices.Room.SelectText(channel.Name)));
        if (channel.HasUnread) menu.Items.Add(MenuItemFor("Marcar como lido", "", () => channel.Unread = 0));
        if (channel.CanEdit)
        {
            menu.Items.Add(new Separator());
            menu.Items.Add(MenuItemFor("Mudar o nome", "", () => RenameChannel(channel.Channel, channel.DisplayName)));
            if (channel.CanDelete) menu.Items.Add(MenuItemFor("Apagar o canal", "", () => DeleteChannel(channel.Channel, channel.DisplayName), danger: true));
        }
        menu.IsOpen = true;
    }

    private async void VoiceChannel_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not VoiceChannelRow channel || channel.IsCurrent) return;
        await JoinVoiceAsync(channel.Name);
    }

    private void VoiceChannel_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement element || element.DataContext is not VoiceChannelRow channel) return;
        e.Handled = true;
        var room = AppServices.Room;
        var menu = new ContextMenu { PlacementTarget = element };
        if (channel.IsCurrent) menu.Items.Add(MenuItemFor("Sair da voz", "", () => _ = room.LeaveVoiceAsync()));
        else menu.Items.Add(MenuItemFor(room.InVoice ? "Mudar para este canal" : "Entrar no canal", "", () => _ = JoinVoiceAsync(channel.Name)));
        if (channel.CanEdit)
        {
            menu.Items.Add(new Separator());
            menu.Items.Add(MenuItemFor("Mudar o nome", "", () => RenameChannel(channel.Channel, channel.DisplayName)));
            if (channel.CanDelete) menu.Items.Add(MenuItemFor("Apagar o canal", "", () => DeleteChannel(channel.Channel, channel.DisplayName), danger: true));
        }
        menu.IsOpen = true;
    }

    private async Task JoinVoiceAsync(string channel)
    {
        if (await AppServices.Room.JoinVoiceAsync(channel) is not { } error) return;
        _shell.Toast(error, ToastKind.Warning, "Escolher o microfone", () => _shell.OpenSettings(SettingsTab.Voice));
    }

    private void Member_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement element || element.DataContext is not VoiceMemberRow member) return;
        e.Handled = true;
        VoiceMemberMenu.Open(element, member, _shell);
    }

    private void CreateText_Click(object sender, RoutedEventArgs e) => CreateChannel(RoomChannelKind.Text);

    private void CreateVoice_Click(object sender, RoutedEventArgs e) => CreateChannel(RoomChannelKind.Voice);

    private void CreateChannel(RoomChannelKind kind)
    {
        var room = AppServices.Room;
        if (ChannelDialog.Ask(Window.GetWindow(this), kind, server: room.ViaServer || room.Active.IsSpace) is not { } name) return;
        if (AppServices.Room.CreateChannel(kind, name) is { } error)
        {
            _shell.Toast(error, ToastKind.Warning);
            return;
        }
        if (kind == RoomChannelKind.Voice) _shell.Toast("Canal de voz criado. Clique nele para entrar.", ToastKind.Success);
        else if (!AppServices.DemoMode) Composer.Focus();
    }

    private void DeleteChannel(RoomChannel channel, string displayName)
    {
        var name = channel.Kind == RoomChannelKind.Text ? $"#{displayName}" : displayName;
        var room = AppServices.Room;
        var message = room.Active.IsSpace
            ? "Ele some da lista de todo mundo do servidor, e a conversa dele deixa de aparecer."
            : room.ViaServer
                ? "Ele sai do servidor e some da lista de todo mundo. Quem já conversou nele continua vendo as mensagens até fechar o ScreenFlow."
                : "Ele some da lista de todo mundo. Quem já conversou nele continua vendo as mensagens até fechar o ScreenFlow.";
        if (!ConfirmDialog.Ask(Window.GetWindow(this), $"Apagar o canal {name}?", message, "Apagar", danger: true)) return;
        if (room.DeleteChannel(channel) is { } error) _shell.Toast(error, ToastKind.Warning);
    }

    /// <summary>(1.4.1) Muda o nome de um canal seu (no servidor, de qualquer canal do servidor; 1.5.0: num servidor por rede, de qualquer canal).</summary>
    private void RenameChannel(RoomChannel channel, string displayName)
    {
        if (ChannelDialog.Ask(Window.GetWindow(this), channel.Kind, channel, currentName: displayName) is not { } name) return;
        if (AppServices.Room.RenameChannel(channel, name) is { } error) _shell.Toast(error, ToastKind.Warning);
    }

    private static MenuItem MenuItemFor(string text, string glyph, Action action, bool danger = false)
    {
        var item = new MenuItem { Header = text, Tag = glyph };
        if (danger) item.SetResourceReference(ForegroundProperty, "Danger");
        item.Click += (_, _) => action();
        return item;
    }

    // ───────────────────────── Chat guardado ─────────────────────────

    private void UpdateArchive()
    {
        if (!IsInitialized) return;
        var room = AppServices.Room;
        var folder = room.ArchiveFolder;
        var problem = room.ArchiveProblem;
        ArchivePill.Visibility = !room.Active.IsSpace && (folder is not null || problem is not null) ? Visibility.Visible : Visibility.Collapsed;
        ArchiveText.Text = problem is not null ? "Chat não guardado"
            : AppServices.Settings.Current.ChatArchiveFolder is null ? "Guardado no Google Drive" : "Guardado na pasta";
        ArchivePill.ToolTip = problem ?? $"As conversas ficam guardadas em {folder}. Clique para abrir a pasta.";
        ArchivePill.SetResourceReference(TextElement.ForegroundProperty, problem is null ? "AccentText" : "Amber");
        ArchivePill.SetResourceReference(Border.BackgroundProperty, problem is null ? "AccentSoft" : "AmberSoft");
        ArchivePill.SetResourceReference(Border.BorderBrushProperty, problem is null ? "AccentBorder" : "AmberSoft");
    }

    private void ArchivePill_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (AppServices.Room.ArchiveFolder is not { } folder)
        {
            _shell.OpenSettings(SettingsTab.Voice);
            return;
        }
        try
        {
            Directory.CreateDirectory(folder);
            Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true });
        }
        catch (Exception exception)
        {
            Log.Warn($"Não abriu a pasta do chat: {exception.Message}");
            _shell.Toast("O Windows não abriu a pasta do chat guardado.", ToastKind.Warning);
        }
    }

    // ───────────────────────── Conversa ─────────────────────────

    private void OnMessagesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        NoMessages.Visibility = _messages is { Count: > 0 } ? Visibility.Collapsed : Visibility.Visible;
        if (e.Action != NotifyCollectionChangedAction.Add || e.NewItems?[0] is not ChatRow row) return;
        if (row.IsMine) _stickToBottom = true;
        if (_stickToBottom) Dispatcher.BeginInvoke(DispatcherPriority.Loaded, ScrollToEnd);
        else if (!row.IsMine) JumpButton.Visibility = Visibility.Visible;
    }

    private void ScrollToEnd()
    {
        MessageScroll.ScrollToEnd();
        _stickToBottom = true;
        JumpButton.Visibility = Visibility.Collapsed;
    }

    /// <summary>Continua grudado no fim quando chega mensagem nova, a menos que a pessoa tenha rolado para cima.</summary>
    private void MessageScroll_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (e.ExtentHeightChange == 0 && e.ViewportHeightChange == 0)
            _stickToBottom = MessageScroll.VerticalOffset >= MessageScroll.ScrollableHeight - 24;
        else if (_stickToBottom) MessageScroll.ScrollToEnd();
        if (_stickToBottom) JumpButton.Visibility = Visibility.Collapsed;
    }

    /// <summary>O texto das mensagens (selecionável) não pode segurar a rodinha do mouse: a conversa rola daqui.</summary>
    private void MessageScroll_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        MessageScroll.ScrollToVerticalOffset(MessageScroll.VerticalOffset - e.Delta / 120.0 * 56);
        e.Handled = true;
    }

    private void Jump_Click(object sender, RoutedEventArgs e) => ScrollToEnd();

    // ───────────────────────── Escrever ─────────────────────────

    private void Composer_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        // Ctrl+V com uma imagem (print, imagem copiada do navegador ou arquivo copiado): anexa em vez de colar texto.
        if (e.Key == Key.V && Keyboard.Modifiers == ModifierKeys.Control && !Clipboard.ContainsText())
        {
            // (1.5.0) Arquivos copiados no Explorer viram anexos; uma imagem copiada (print) continua indo como imagem.
            if (ClipboardFiles() is { Count: > 0 } copied && !(copied.Count == 1 && ImageTools.LooksLikeImageFile(copied[0]) && Clipboard.ContainsImage()))
            {
                AttachFiles(copied);
                e.Handled = true;
                return;
            }
            if (PasteImage()) e.Handled = true;
            return;
        }
        if (e.Key != Key.Enter || Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) return;
        e.Handled = true;
        SendMessage();
    }

    private void Send_Click(object sender, RoutedEventArgs e)
    {
        SendMessage();
        Composer.Focus();
    }

    private void SendMessage()
    {
        var files = _pendingFiles.Where(f => f.IsReady).ToList();
        if (string.IsNullOrWhiteSpace(Composer.Text) && _attachment is null && files.Count == 0)
        {
            if (_pendingFiles.Count > 0) _shell.Toast(PendingProblem(), ToastKind.Info);
            return;
        }
        if (_pendingFiles.Any(f => f.IsPreparing))
        {
            _shell.Toast("Espere os arquivos ficarem prontos (o ScreenFlow confere cada um antes de mandar).", ToastKind.Info);
            return;
        }
        var text = Composer.Text;
        // O texto e a imagem colada vão primeiro; depois, um arquivo por mensagem (o texto vai junto com o primeiro).
        if (_attachment is not null || files.Count == 0)
        {
            if (!SendOne(text, _attachment, null)) return;
            text = string.Empty;
            Composer.Clear();
            SetAttachment(null);
        }
        foreach (var pending in files)
        {
            var file = AppServices.Files.Share(pending, DirectMode ? AppServices.Direct.Current?.Id : null, out var problem);
            if (file is null)
            {
                _shell.Toast(problem ?? "O arquivo não foi enviado.", ToastKind.Warning);
                break;
            }
            if (!SendOne(text, pending.Preview, file)) break;
            text = string.Empty;
            Composer.Clear();
            _pendingFiles.Remove(pending);
        }
        // Os que deram problema (mudaram, não abriram) ficam para você tirar.
        UpdatePendingFiles();
        _stickToBottom = true;
    }

    /// <summary>Manda uma mensagem na conversa aberta (servidor ou privada). false e o aviso quando não foi.</summary>
    private bool SendOne(string text, PreparedImage? image, ChatFile? file)
    {
        if (DirectMode)
        {
            // (1.5.0) Na conversa privada: vai cifrada só para a pessoa (ou fica esperando ela aparecer).
            string? problem;
            try { problem = AppServices.Direct.Send(text, image, file); }
            catch (Exception exception)
            {
                Log.Warn($"Mensagem direta não foi enviada: {exception.Message}");
                problem = "A mensagem não foi enviada.";
            }
            if (problem is null) return true;
            _shell.Toast(problem, ToastKind.Warning);
            return false;
        }
        bool sent;
        try { sent = AppServices.Room.Send(text, image, file); }
        catch (Exception exception)
        {
            Log.Warn($"Mensagem com anexo não foi enviada: {exception.Message}");
            sent = false;
        }
        if (sent) return true;
        _shell.Toast(AppServices.Room.Error ?? "A mensagem não foi enviada.", ToastKind.Warning);
        return false;
    }

    private void Composer_TextChanged(object sender, TextChangedEventArgs e) => UpdateComposer();

    private void UpdateComposer()
    {
        var length = Composer.Text.Length;
        SendButton.IsEnabled = !string.IsNullOrWhiteSpace(Composer.Text) || _attachment is not null || _pendingFiles.Any(f => f.IsReady);
        CounterText.Text = length >= 400 ? $"{length}/500" : string.Empty;
    }

    /// <summary>Num canal criado, avisa que quem está numa versão antiga não vê (eles só têm o #geral).</summary>
    private void UpdateComposerHint()
    {
        var room = AppServices.Room;
        if (DirectMode)
        {
            ComposerHint.Text = "Enter envia · Shift+Enter pula linha · arraste arquivos para cá · só a pessoa lê";
            ComposerHint.SetResourceReference(TextBlock.ForegroundProperty, "TextSubtle");
            return;
        }
        var old = room.CurrentText.IsDefaultText ? 0 : room.OldVersionCount;
        ComposerHint.Text = old switch
        {
            0 => "Enter envia · Shift+Enter pula linha · Ctrl+V cola uma imagem · arraste arquivos para cá",
            1 => "1 pessoa está numa versão antiga do ScreenFlow e só vê o #geral",
            var n => $"{n} pessoas estão numa versão antiga do ScreenFlow e só veem o #geral",
        };
        ComposerHint.SetResourceReference(TextBlock.ForegroundProperty, old > 0 ? "Amber" : "TextSubtle");
    }

    // ───────────────────────── Imagens ─────────────────────────

    private void Attach_Click(object sender, RoutedEventArgs e)
    {
        // (1.5.0) Qualquer arquivo (vários de uma vez): fotos e vídeos vão com a prévia, tudo no tamanho original.
        var dialog = new OpenFileDialog
        {
            Title = "Escolha os arquivos para mandar",
            Filter = "Todos os arquivos|*.*|Imagens|*.jpg;*.jpeg;*.jfif;*.png;*.bmp;*.gif;*.webp;*.tif;*.tiff|Vídeos|*.mp4;*.m4v;*.mov;*.avi;*.wmv;*.mkv;*.webm|Áudio|*.mp3;*.wav;*.ogg;*.opus;*.m4a;*.aac;*.flac",
            Multiselect = true,
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        AttachFiles(dialog.FileNames);
    }

    /// <summary>Anexa arquivos (até 10 por vez): cada um é conferido (SHA-256) e ganha a prévia antes de poder mandar.</summary>
    private void AttachFiles(IEnumerable<string> paths)
    {
        foreach (var path in paths)
        {
            if (_pendingFiles.Count >= FileService.MaxFilesPerSend)
            {
                _shell.Toast($"Até {FileService.MaxFilesPerSend} arquivos por vez: mande estes e anexe o resto depois.", ToastKind.Info);
                break;
            }
            if (_pendingFiles.Any(f => string.Equals(f.Path, Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase))) continue;
            if (AppServices.Files.Prepare(path, out var problem) is not { } pending)
            {
                _shell.Toast(problem ?? "Não deu para anexar o arquivo.", ToastKind.Warning);
                continue;
            }
            pending.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName is nameof(PendingFile.IsReady) or nameof(PendingFile.Error)) UpdateComposer();
            };
            _pendingFiles.Add(pending);
        }
        UpdatePendingFiles();
        Composer.Focus();
    }

    private void UpdatePendingFiles()
    {
        PendingFilesList.Visibility = _pendingFiles.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateComposer();
    }

    private string PendingProblem() => _pendingFiles.FirstOrDefault(f => f.HasError)?.Error ?? "Espere os arquivos ficarem prontos.";

    private void RemovePendingFile_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not PendingFile pending) return;
        pending.Cancellation.Cancel();
        _pendingFiles.Remove(pending);
        UpdatePendingFiles();
        Composer.Focus();
    }

    private static List<string>? ClipboardFiles()
    {
        try { return Clipboard.ContainsFileDropList() ? Clipboard.GetFileDropList().Cast<string>().ToList() : null; }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            return null;
        }
    }

    private bool PasteImage()
    {
        try
        {
            if (ImageTools.FromClipboard() is not { } image) return false;
            Attach(() => image);
            return true;
        }
        catch (InvalidDataException exception)
        {
            _shell.Toast(exception.Message, ToastKind.Warning);
            return true;
        }
    }

    /// <summary>Prepara a imagem (vira, diminui e grava em JPEG) e mostra a prévia em cima da caixa de escrever.</summary>
    private void Attach(Func<BitmapSource> open)
    {
        try
        {
            SetAttachment(ImageTools.MakeChatImage(open()));
            Composer.Focus();
        }
        catch (InvalidDataException exception)
        {
            _shell.Toast(exception.Message, ToastKind.Warning);
        }
        catch (Exception exception)
        {
            Log.Warn($"Imagem não abriu: {exception}");
            _shell.Toast("Não deu para abrir essa imagem.", ToastKind.Warning);
        }
    }

    private void SetAttachment(PreparedImage? image)
    {
        _attachment = image;
        AttachmentPanel.Visibility = image is null ? Visibility.Collapsed : Visibility.Visible;
        AttachmentPreview.ImageSource = image?.Preview;
        AttachmentInfo.Text = image is null ? string.Empty
            : $"{image.Width} × {image.Height} · {image.Data.Length / 1024.0:0} KB · escreva uma legenda se quiser e aperte Enter";
        UpdateComposer();
    }

    private void RemoveAttachment_Click(object sender, RoutedEventArgs e)
    {
        SetAttachment(null);
        Composer.Focus();
    }

    private static string[]? DroppedFiles(DragEventArgs e)
        => e.Data.GetDataPresent(DataFormats.FileDrop) && e.Data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } files ? files : null;

    private void Conversation_PreviewDragOver(object sender, DragEventArgs e)
    {
        if (DroppedFiles(e) is null || !Composer.IsEnabled) return;
        e.Effects = DragDropEffects.Copy;
        e.Handled = true;
    }

    /// <summary>(1.5.0) Arquivos arrastados para a conversa viram anexos (vários de uma vez, de qualquer tipo).</summary>
    private void Conversation_PreviewDrop(object sender, DragEventArgs e)
    {
        if (DroppedFiles(e) is not { } files || !Composer.IsEnabled) return;
        e.Handled = true;
        AttachFiles(files);
    }

    private static ChatRow? RowOf(object sender) => (sender as FrameworkElement)?.DataContext as ChatRow;

    private void Picture_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (RowOf(sender) is { IsVideoPreview: true, Transfer: { } video })
        {
            PlayVideo(video);
            return;
        }
        if (RowOf(sender) is { ImageState: ChatImageState.Ready } row) OpenPicture(row);
    }

    // ───────────────────────── Arquivos (1.5.0) ─────────────────────────

    private static FileTransferItem? ItemOf(object sender) => (sender as FrameworkElement)?.DataContext as FileTransferItem;

    /// <summary>Assiste o vídeo no ScreenFlow (o seu, ou o que você já baixou); sem baixar ainda, oferece baixar.</summary>
    private void PlayVideo(FileTransferItem item)
    {
        var path = item.IsMine ? item.SharedPath : item.State == Core.Files.FileDownloadState.Completed ? item.SavedPath : null;
        if (path is not null && File.Exists(path))
        {
            new ChatVideoWindow(path, item.Name) { Owner = Window.GetWindow(this) }.Show();
            return;
        }
        if (item.CanDownload)
        {
            _shell.Toast("Baixe o vídeo para assistir: escolha onde salvar.", ToastKind.Info);
            AppServices.Files.Download(item, Window.GetWindow(this));
        }
        else if (item.ShowProgress) _shell.Toast("O vídeo ainda está baixando.", ToastKind.Info);
    }

    private void FileDownload_Click(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is { } item) AppServices.Files.Download(item, Window.GetWindow(this));
    }

    private void FilePause_Click(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is { } item) AppServices.Files.Pause(item);
    }

    private void FileResume_Click(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is { } item) AppServices.Files.Resume(item);
    }

    private void FileCancel_Click(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is not { } item) return;
        if (!ConfirmDialog.Ask(Window.GetWindow(this), "Parar e apagar o download?", $"O que já baixou de \"{item.Name}\" é apagado. Para baixar de novo, começa do zero.",
                "Parar e apagar", danger: true)) return;
        AppServices.Files.Cancel(item);
    }

    private void FileOpen_Click(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is not { } item) return;
        if (item.IsVideo && item.SavedPath is { } path && File.Exists(path))
        {
            new ChatVideoWindow(path, item.Name) { Owner = Window.GetWindow(this) }.Show();
            return;
        }
        if (AppServices.Files.Open(item) is { } problem) _shell.Toast(problem, ToastKind.Warning);
    }

    private void FileFolder_Click(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is { } item) AppServices.Files.ShowInFolder(item);
    }

    private void FileStopSharing_Click(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is not { } item) return;
        if (!ConfirmDialog.Ask(Window.GetWindow(this), "Parar de compartilhar?", $"Quem ainda não baixou \"{item.Name}\" não vai conseguir mais. O arquivo continua no seu PC.",
                "Parar de compartilhar", danger: true)) return;
        AppServices.Files.StopSharing(item);
    }

    private void FileCopyName_Click(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is { } item) CopyText(item.Name, "Nome copiado.");
    }

    private void FileCopyHash_Click(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is { } item) CopyText(item.File.Hash, "SHA-256 copiado.");
    }

    private void OpenPicture_Click(object sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is { } row) OpenPicture(row);
    }

    /// <summary>Abre a imagem no visualizador do ScreenFlow e permite passar pelas imagens deste canal.</summary>
    private void OpenPicture(ChatRow row)
    {
        var images = (_messages ?? AppServices.Room.Messages)
            .Where(candidate => candidate.ImageState == ChatImageState.Ready
                && candidate.Image is { } image
                && AppServices.Blobs.Has(image.Id))
            .ToList();
        var index = images.IndexOf(row);
        if (index < 0)
        {
            _shell.Toast("A imagem ainda não chegou.", ToastKind.Warning);
            return;
        }
        var viewer = new ChatImageViewerWindow(images, index) { Owner = Window.GetWindow(this) };
        viewer.ShowDialog();
    }

    private void SavePicture_Click(object sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is not { Image: not null } row || row.ImageData() is not { } data)
        {
            _shell.Toast("A imagem ainda não chegou.", ToastKind.Warning);
            return;
        }
        var extension = ImageTools.Extension(data);
        var dialog = new SaveFileDialog
        {
            Title = "Salvar a imagem",
            FileName = $"ScreenFlow {DateTime.Now:yyyy-MM-dd HH-mm-ss}{extension}",
            Filter = extension == ".png" ? "Imagem PNG|*.png" : "Imagem JPEG|*.jpg",
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        try
        {
            File.WriteAllBytes(dialog.FileName, data);
            _shell.Toast("Imagem salva.", ToastKind.Success);
        }
        catch (Exception exception) { _shell.Toast($"Não deu para salvar: {exception.Message}", ToastKind.Warning); }
    }

    private void CopyPicture_Click(object sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is not { Image: not null } row || ImageTools.Decode(row.ImageData()) is not { } bitmap)
        {
            _shell.Toast("A imagem ainda não chegou.", ToastKind.Warning);
            return;
        }
        try
        {
            Clipboard.SetImage(bitmap);
            _shell.Toast("Imagem copiada.", ToastKind.Success);
        }
        catch (Exception exception) { _shell.Toast($"Não deu para copiar: {exception.Message}", ToastKind.Warning); }
    }

    private void RetryPicture_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not ChatRow row) return;
        if (row.DirectPeer is not null) AppServices.Direct.RetryImage(row);
        else AppServices.Room.RetryImage(row);
    }

    private void SelfAvatar_MouseLeftButtonUp(object sender, MouseButtonEventArgs e) => _shell.OpenSettings(SettingsTab.General);

    /// <summary>Capturas do CI: uma imagem anexada esperando para ir, com legenda.</summary>
    internal void ShowAttachmentDemo(PreparedImage? image, string caption)
    {
        Composer.Text = caption;
        SetAttachment(image);
    }

    /// <summary>(1.5.0) Arquivos anexados de exemplo na caixa de escrever (as capturas de tela).</summary>
    internal void ShowFilesDemo(IReadOnlyList<string> paths, string caption = "")
    {
        foreach (var pending in _pendingFiles) pending.Cancellation.Cancel();
        _pendingFiles.Clear();
        Composer.Text = caption;
        if (paths.Count > 0) AttachFiles(paths);
        UpdatePendingFiles();
    }

    // ───────────────────────── Mensagens diretas (1.5.0) ─────────────────────────

    private static bool DirectMode => AppServices.Direct.Showing;

    private void DirectButton_Click(object sender, RoutedEventArgs e)
    {
        var direct = AppServices.Direct;
        direct.Showing = true;
        // Abre a conversa mais recente, como quem volta para onde estava.
        if (direct.Current is null && direct.Conversations.FirstOrDefault() is { } latest) direct.Show(latest);
        DirectButton.IsChecked = true;
    }

    /// <summary>Entrou ou saiu das mensagens diretas: a coluna da esquerda, a conversa e a caixa de escrever mudam juntas.</summary>
    private void OnDirectModeChanged()
    {
        SwitchComposer();
        _stickToBottom = true;
        ShowChannel();
        UpdateAll();
        // Voltou para o canal: o que chegou nele enquanto as mensagens diretas estavam abertas fica lido.
        if (!DirectMode && IsVisible) AppServices.Room.MarkRead();
        if (DirectMode && AppServices.Direct.Current is not null && IsVisible && !AppServices.DemoMode) Composer.Focus();
    }

    private void OnDirectCurrentChanged()
    {
        if (!DirectMode) return;
        var switched = !Equals(ComposerOwner, _draftOwner);
        SwitchComposer();
        if (switched)
        {
            _stickToBottom = true;
            ShowConversation();
            if (IsVisible && !AppServices.DemoMode) Composer.Focus();
        }
        else UpdateConversationHeader();
    }

    /// <summary>Mostra a conversa privada aberta no meio (ou o convite para escolher uma).</summary>
    private void ShowConversation()
    {
        var direct = AppServices.Direct;
        if (_messages is not null) _messages.CollectionChanged -= OnMessagesChanged;
        _messages = direct.Messages;
        _messages.CollectionChanged += OnMessagesChanged;
        MessageList.ItemsSource = _messages;
        UpdateConversationHeader();
        NoMessages.Visibility = _messages.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        JumpButton.Visibility = Visibility.Collapsed;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, ScrollToEnd);
    }

    /// <summary>O cabeçalho, o aviso de chave trocada e o cartão da pessoa da conversa aberta.</summary>
    private void UpdateConversationHeader()
    {
        if (!IsInitialized) return;
        var direct = AppServices.Direct;
        var row = direct.Current;
        ChannelHash.Text = "@";
        EmptyGlyph.Text = "@";
        ArchivePill.Visibility = Visibility.Collapsed;
        PeopleToggle.ToolTip = "Mostrar ou esconder quem é a pessoa da conversa";
        ChannelTitle.Text = row?.Name ?? "Mensagens diretas";
        ChannelTopic.Text = row?.StatusText ?? (direct.Problem ?? "Escolha uma conversa na lista, ou comece uma pelo balão ao lado de alguém do servidor.");
        ChannelTopic.ToolTip = ChannelTopic.Text;
        SafetyPill.Visibility = row is { HasKey: true } ? Visibility.Visible : Visibility.Collapsed;
        Composer.IsEnabled = row is not null && direct.IsAvailable;
        AttachButton.IsEnabled = Composer.IsEnabled;
        Composer.Tag = row is null ? "Escolha uma conversa" : $"Mensagem para {row.Name}";
        EmptyTitle.Text = row is null ? "Mensagens diretas" : $"Começo da conversa com {row.Name}";
        EmptyText.Text = row is null
            ? "As conversas privadas aparecem aqui. Elas vão cifradas só para a outra pessoa e ficam guardadas neste PC, cifradas também."
            : $"Só você e {row.Name} leem esta conversa. Se a pessoa estiver offline, a mensagem fica guardada e vai sozinha quando ela aparecer.";

        var changed = row is { KeyChanged: true };
        KeyBanner.Visibility = changed ? Visibility.Visible : Visibility.Collapsed;
        if (changed)
        {
            KeyBannerTitle.Text = $"A chave de {row!.Name} mudou";
            var code = direct.ChangedSafetyCode(row.Id);
            KeyBannerText.Text = "Acontece quando a pessoa reinstala o ScreenFlow ou usa outro PC, mas também pode ser alguém se passando por ela. "
                + "Nada do que chega com a chave nova aparece até você aceitar."
                + (code is null ? string.Empty : $" Antes, confira com a pessoa por voz se o código novo é {code}.");
        }

        if (row is not null)
        {
            ProfileAvatar.Colors = row.Avatar;
            ProfileAvatar.Initial = row.Initial;
            ProfileAvatar.Photo = row.Photo;
            ProfileName.Text = row.Name;
            ProfileStatus.Text = row.StatusText;
            ProfileDot.SetResourceReference(Border.BackgroundProperty, row.IsOnline ? "Success" : "TextSubtle");
            ProfileCode.Text = direct.SafetyCode(row.Id) ?? "Aparece quando a pessoa entrar com o ScreenFlow 1.5.0.";
        }
        DirectProfileCard.IsEnabled = row is not null;
        DirectProfileCard.Opacity = row is null ? 0.5 : 1;
        UpdateComposerHint();
    }

    private void UpdateDirectList()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(UpdateDirectList);
            return;
        }
        if (!IsInitialized) return;
        ApplyDirectSearch();
    }

    private void UpdateDirectBadge()
    {
        if (!IsInitialized) return;
        var unread = AppServices.Direct.Unread;
        DirectButton.Tag = unread switch { 0 => null, > 99 => "99+", _ => unread.ToString() };
    }

    private void DirectSearch_TextChanged(object sender, TextChangedEventArgs e) => ApplyDirectSearch();

    /// <summary>A busca: pelo nome da pessoa ou pelo começo da última mensagem.</summary>
    private void ApplyDirectSearch()
    {
        var query = DirectSearch.Text.Trim();
        var conversations = AppServices.Direct.Conversations;
        foreach (var row in conversations)
            row.IsVisible = query.Length == 0 || row.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase)
                || row.Preview.Contains(query, StringComparison.CurrentCultureIgnoreCase);
        var none = !conversations.Any(c => c.IsVisible);
        NoConversations.Visibility = none ? Visibility.Visible : Visibility.Collapsed;
        NoConversations.Text = conversations.Count == 0
            ? AppServices.Direct.Problem ?? "Nenhuma conversa ainda. Para começar, passe o mouse em alguém na lista de quem está no servidor e clique no balão (ou use o botão direito)."
            : $"Nenhuma conversa com \"{query}\".";
    }

    private void Conversation_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not DirectConversationRow row) return;
        AppServices.Direct.Show(row);
        if (!AppServices.DemoMode) Composer.Focus();
    }

    private void Conversation_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement element || element.DataContext is not DirectConversationRow row) return;
        e.Handled = true;
        var direct = AppServices.Direct;
        var menu = new ContextMenu { PlacementTarget = element };
        menu.Items.Add(MenuItemFor("Abrir a conversa", "\uE8BD", () => direct.Show(row)));
        if (direct.SafetyCode(row.Id) is { } code)
            menu.Items.Add(MenuItemFor("Copiar o código de segurança", "\uE8C8", () => CopyText(code, "Código de segurança copiado.")));
        menu.Items.Add(new Separator());
        menu.Items.Add(MenuItemFor("Apagar a conversa deste PC…", "\uE74D", () => RemoveConversation(row), danger: true));
        menu.IsOpen = true;
    }

    private void SafetyPill_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        // O código de segurança fica no cartão da pessoa, à direita.
        _peopleWanted = true;
        UpdateColumns();
        if (AppServices.Direct.Current is { } row && AppServices.Direct.SafetyCode(row.Id) is { } code)
            _shell.Toast($"Código de segurança com {row.Name}: {code}. Confira por voz se o da pessoa é igual.", ToastKind.Info,
                "Copiar", () => CopyText(code, "Código de segurança copiado."));
    }

    private void CopyText(string text, string done)
    {
        try
        {
            Clipboard.SetText(text);
            _shell.Toast(done, ToastKind.Success);
        }
        catch (Exception exception) { _shell.Toast($"Não deu para copiar: {exception.Message}", ToastKind.Warning); }
    }

    private void AcceptKey_Click(object sender, RoutedEventArgs e)
    {
        if (AppServices.Direct.Current is not { } row) return;
        if (!ConfirmDialog.Ask(Window.GetWindow(this), $"Aceitar a chave nova de {row.Name}?",
                "Faça isso só depois de conferir com a pessoa (por voz ou pessoalmente) que o código de segurança novo é o mesmo no PC dela. A partir daqui, o que chegar com a chave nova aparece na conversa.",
                "Aceitar"))
            return;
        AppServices.Direct.AcceptKey();
        _shell.Toast($"Chave nova de {row.Name} aceita.", ToastKind.Success);
    }

    private void KeepKey_Click(object sender, RoutedEventArgs e) => AppServices.Direct.KeepKey();

    private void RemoveConversation_Click(object sender, RoutedEventArgs e)
    {
        if (AppServices.Direct.Current is { } row) RemoveConversation(row);
    }

    private void RemoveConversation(DirectConversationRow row)
    {
        if (!ConfirmDialog.Ask(Window.GetWindow(this), $"Apagar a conversa com {row.Name} deste PC?",
                "As mensagens somem deste PC (as suas que ainda não tinham chegado não vão mais). A outra pessoa continua com a conversa dela.",
                "Apagar", danger: true))
            return;
        AppServices.Direct.Remove(row);
        _shell.Toast("Conversa apagada deste PC.", ToastKind.Success);
    }

    private void MessagePerson_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is RoomPersonRow person) OpenDirect(person.Id, person.Name);
    }

    private void Person_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement element || element.DataContext is not RoomPersonRow person) return;
        e.Handled = true;
        var menu = new ContextMenu { PlacementTarget = element };
        var message = MenuItemFor(person.CanMessage ? "Mandar mensagem" : "Mandar mensagem (precisa do ScreenFlow 1.5.0)", "\uE8BD",
            () => OpenDirect(person.Id, person.Name));
        message.IsEnabled = person.CanMessage;
        menu.Items.Add(message);
        if (person.IsLive) menu.Items.Add(MenuItemFor(person.ActionText, "\uE768", () => _shell.WatchPerson(person)));
        menu.IsOpen = true;
    }

    /// <summary>Abre (ou começa) a conversa privada com alguém do servidor.</summary>
    internal void OpenDirect(Guid id, string name)
    {
        if (AppServices.Direct.OpenWith(id, name, out var problem) is null)
        {
            if (problem is not null) _shell.Toast(problem, ToastKind.Info);
            return;
        }
        if (IsVisible && !AppServices.DemoMode) Composer.Focus();
    }

    // ───────────────────────── Pessoas ─────────────────────────

    private void Watch_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is RoomPersonRow person) _shell.WatchPerson(person);
    }

    private void AddPeer_Click(object sender, RoutedEventArgs e) => AddPeer();

    private void PeerBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        AddPeer();
    }

    private void AddPeer()
    {
        var text = PeerBox.Text.Trim();
        if (text.Length == 0)
        {
            PeerBox.Focus();
            return;
        }
        if (AppServices.Room.AddPeer(text) is { } error)
        {
            _shell.Toast(error, ToastKind.Warning);
            return;
        }
        _shell.Toast(AppServices.Room.Active.IsSpace
            ? $"Chamando {text}. Se a pessoa estiver no servidor, ela aparece em alguns segundos (o IP fica guardado no servidor)."
            : $"Chamando {text}. Se o ScreenFlow estiver aberto lá, a pessoa aparece na lista em alguns segundos.", ToastKind.Success);
        PeerBox.Clear();
    }
}
