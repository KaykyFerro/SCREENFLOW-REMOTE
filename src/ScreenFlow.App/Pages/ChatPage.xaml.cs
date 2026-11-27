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
/// Sala da rede, como um servidor do Discord (1.3.0): canais de texto e de voz à esquerda (com quem está em cada canal
/// de voz), a conversa do canal aberto no meio e quem está na rede à direita.
/// </summary>
public partial class ChatPage : UserControl, IShellPage
{
    private readonly MainWindow _shell;
    private bool _stickToBottom = true;
    private PreparedImage? _attachment;
    private ObservableCollection<ChatRow>? _messages;
    /// <summary>Mostrar quem está na rede: null segue a largura da janela; o botão de pessoas escolhe.</summary>
    private bool? _peopleWanted;

    public ChatPage(MainWindow shell)
    {
        _shell = shell;
        InitializeComponent();
        var room = AppServices.Room;
        TextChannelList.ItemsSource = room.TextChannels;
        VoiceChannelList.ItemsSource = room.VoiceChannels;
        PeopleList.ItemsSource = room.People;
        room.CurrentTextChanged += ShowChannel;
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
        AppServices.Room.MarkRead();
        UpdateAll();
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            ScrollToEnd();
            if (AppServices.Room.IsAvailable && !AppServices.DemoMode) Composer.Focus();
        });
    }

    private void UpdateAll()
    {
        var room = AppServices.Room;
        var available = room.IsAvailable;
        ContentGrid.Visibility = available ? Visibility.Visible : Visibility.Collapsed;
        ErrorPanel.Visibility = available ? Visibility.Collapsed : Visibility.Visible;
        ErrorText.Text = $"{room.Error} Feche o programa que está usando a porta UDP {AppServices.Settings.Current.DiscoveryPort} (ou mude a porta de descoberta em Configurações) e abra o ScreenFlow de novo.";
        UpdateColumns();
        UpdatePeople();
        UpdateArchive();
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
        PeopleCard.Visibility = showPeople ? Visibility.Visible : Visibility.Collapsed;
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
        var others = room.People.Count;
        OnlineText.Text = $"{others + 1} online";
        NoPeople.Visibility = others == 0 ? Visibility.Visible : Visibility.Collapsed;
        var voice = room.VoiceCount;
        RoomInfo.Text = (others, voice) switch
        {
            (0, 0) => "Só você online",
            (_, 0) => $"{others + 1} online",
            (_, 1) => $"{others + 1} online · 1 na voz",
            _ => $"{others + 1} online · {voice} na voz",
        };

        SelfAvatar.Colors = AvatarColors.For(room.SelfId);
        SelfAvatar.Initial = AvatarColors.Initial(room.SelfName);
        SelfAvatar.Photo = room.SelfPhoto;
        SelfName.Text = $"{room.SelfName} (você)";
        var status = _shell.IsTransmitting ? $"Ao vivo · {_shell.TransmitPage.LiveSummary}" : _shell.IsWatching ? "Assistindo uma transmissão" : "Online";
        if (room.VoiceChannel is { } channel)
            status += room.Deafened ? $" · na voz: {channel}, sem som" : room.MicrophoneOff ? $" · na voz: {channel}, mudo" : $" · na voz: {channel}";
        SelfStatus.Text = status;
        ChannelTopic.Text = room.CurrentText.Topic;
        ChannelTopic.ToolTip = room.CurrentText.Topic;
        UpdateComposerHint();
    }

    // ───────────────────────── Canais ─────────────────────────

    /// <summary>Mostra a conversa do canal de texto aberto.</summary>
    private void ShowChannel()
    {
        var room = AppServices.Room;
        if (_messages is not null) _messages.CollectionChanged -= OnMessagesChanged;
        _messages = room.Messages;
        _messages.CollectionChanged += OnMessagesChanged;
        MessageList.ItemsSource = _messages;
        var channel = room.CurrentText;
        ChannelTitle.Text = channel.Name;
        ChannelTopic.Text = channel.Topic;
        ChannelTopic.ToolTip = channel.Topic;
        Composer.Tag = $"Conversar em #{channel.Name}";
        EmptyTitle.Text = channel.IsDefaultText ? "Ninguém escreveu nada ainda" : $"Este é o começo do #{channel.Name}";
        EmptyText.Text = channel.IsDefaultText
            ? "Mande um oi: a mensagem vai para todo mundo que está com o ScreenFlow aberto na sua rede."
            : "Mande a primeira mensagem do canal. Quem está na sala vê o canal na lista da esquerda.";
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
        if (channel.IsMine)
        {
            menu.Items.Add(new Separator());
            menu.Items.Add(MenuItemFor("Apagar o canal", "", () => DeleteChannel(channel.Channel), danger: true));
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
        if (channel.IsMine)
        {
            menu.Items.Add(new Separator());
            menu.Items.Add(MenuItemFor("Apagar o canal", "", () => DeleteChannel(channel.Channel), danger: true));
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
        if (ChannelDialog.Ask(Window.GetWindow(this), kind) is not { } name) return;
        if (AppServices.Room.CreateChannel(kind, name) is { } error)
        {
            _shell.Toast(error, ToastKind.Warning);
            return;
        }
        if (kind == RoomChannelKind.Voice) _shell.Toast("Canal de voz criado. Clique nele para entrar.", ToastKind.Success);
        else if (!AppServices.DemoMode) Composer.Focus();
    }

    private void DeleteChannel(RoomChannel channel)
    {
        var name = channel.Kind == RoomChannelKind.Text ? $"#{channel.Name}" : channel.Name;
        if (!ConfirmDialog.Ask(Window.GetWindow(this), $"Apagar o canal {name}?",
                "Ele some da lista de todo mundo. Quem já conversou nele continua vendo as mensagens até fechar o ScreenFlow.", "Apagar", danger: true))
            return;
        AppServices.Room.DeleteChannel(channel);
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
        ArchivePill.Visibility = folder is not null || problem is not null ? Visibility.Visible : Visibility.Collapsed;
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
        if (string.IsNullOrWhiteSpace(Composer.Text) && _attachment is null) return;
        bool sent;
        try { sent = AppServices.Room.Send(Composer.Text, _attachment); }
        catch (Exception exception)
        {
            Log.Warn($"Mensagem com imagem não foi enviada: {exception.Message}");
            sent = false;
        }
        if (!sent)
        {
            _shell.Toast(AppServices.Room.Error ?? "A mensagem não foi enviada.", ToastKind.Warning);
            return;
        }
        Composer.Clear();
        SetAttachment(null);
        _stickToBottom = true;
    }

    private void Composer_TextChanged(object sender, TextChangedEventArgs e) => UpdateComposer();

    private void UpdateComposer()
    {
        var length = Composer.Text.Length;
        SendButton.IsEnabled = !string.IsNullOrWhiteSpace(Composer.Text) || _attachment is not null;
        CounterText.Text = length >= 400 ? $"{length}/500" : string.Empty;
    }

    /// <summary>Num canal criado, avisa que quem está numa versão antiga não vê (eles só têm o #geral).</summary>
    private void UpdateComposerHint()
    {
        var room = AppServices.Room;
        var old = room.CurrentText.IsDefaultText ? 0 : room.OldVersionCount;
        ComposerHint.Text = old switch
        {
            0 => "Enter envia · Shift+Enter pula linha · Ctrl+V cola uma imagem",
            1 => "1 pessoa está numa versão antiga do ScreenFlow e só vê o #geral",
            var n => $"{n} pessoas estão numa versão antiga do ScreenFlow e só veem o #geral",
        };
        ComposerHint.SetResourceReference(TextBlock.ForegroundProperty, old > 0 ? "Amber" : "TextSubtle");
    }

    // ───────────────────────── Imagens ─────────────────────────

    private void Attach_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "Escolha uma imagem para mandar", Filter = ImageTools.FileFilter };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        Attach(() => ImageTools.LoadFile(dialog.FileName, ImageTools.ChatMaxSide));
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

    private static string? DroppedImage(DragEventArgs e)
        => e.Data.GetDataPresent(DataFormats.FileDrop) && e.Data.GetData(DataFormats.FileDrop) is string[] files
            ? files.FirstOrDefault(ImageTools.LooksLikeImageFile)
            : null;

    private void Conversation_PreviewDragOver(object sender, DragEventArgs e)
    {
        if (DroppedImage(e) is null) return;
        e.Effects = DragDropEffects.Copy;
        e.Handled = true;
    }

    private void Conversation_PreviewDrop(object sender, DragEventArgs e)
    {
        if (DroppedImage(e) is not { } file) return;
        e.Handled = true;
        Attach(() => ImageTools.LoadFile(file, ImageTools.ChatMaxSide));
    }

    private static ChatRow? RowOf(object sender) => (sender as FrameworkElement)?.DataContext as ChatRow;

    private void Picture_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (RowOf(sender) is { ImageState: ChatImageState.Ready } row) OpenPicture(row);
    }

    private void OpenPicture_Click(object sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is { } row) OpenPicture(row);
    }

    /// <summary>Abre no visualizador de fotos do Windows (com zoom, tela cheia e o resto).</summary>
    private void OpenPicture(ChatRow row)
    {
        if (row.Image is not { } image || AppServices.Blobs.Get(image.Id) is not { } data)
        {
            _shell.Toast("A imagem ainda não chegou.", ToastKind.Warning);
            return;
        }
        try { Process.Start(new ProcessStartInfo(ImageTools.TempFile(image.Id, data)) { UseShellExecute = true }); }
        catch (Exception exception)
        {
            Log.Warn($"Não abriu a imagem: {exception.Message}");
            _shell.Toast("O Windows não abriu a imagem. Use \"Salvar como…\" no botão direito.", ToastKind.Warning);
        }
    }

    private void SavePicture_Click(object sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is not { Image: { } image } || AppServices.Blobs.Get(image.Id) is not { } data)
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
        if (RowOf(sender) is not { Image: { } image } || ImageTools.Decode(AppServices.Blobs.Get(image.Id)) is not { } bitmap)
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
        if ((sender as FrameworkElement)?.Tag is ChatRow row) AppServices.Room.RetryImage(row);
    }

    private void SelfAvatar_MouseLeftButtonUp(object sender, MouseButtonEventArgs e) => _shell.OpenSettings(SettingsTab.General);

    /// <summary>Capturas do CI: uma imagem anexada esperando para ir, com legenda.</summary>
    internal void ShowAttachmentDemo(PreparedImage? image, string caption)
    {
        Composer.Text = caption;
        SetAttachment(image);
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
        _shell.Toast($"Chamando {text}. Se o ScreenFlow estiver aberto lá, a pessoa aparece na lista em alguns segundos.", ToastKind.Success);
        PeerBox.Clear();
    }
}
