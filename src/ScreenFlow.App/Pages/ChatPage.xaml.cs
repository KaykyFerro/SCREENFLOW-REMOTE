using System.Collections.Specialized;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using ScreenFlow.App.Services;
using ScreenFlow.Core.Diagnostics;

namespace ScreenFlow.App.Pages;

/// <summary>Chat da rede: uma conversa só com todo mundo que está com o ScreenFlow aberto na rede, e quem está lá.</summary>
public partial class ChatPage : UserControl, IShellPage
{
    private readonly MainWindow _shell;
    private bool _stickToBottom = true;
    private PreparedImage? _attachment;

    public ChatPage(MainWindow shell)
    {
        _shell = shell;
        InitializeComponent();
        var room = AppServices.Room;
        MessageList.ItemsSource = room.Messages;
        PeopleList.ItemsSource = room.People;
        room.Messages.CollectionChanged += OnMessagesChanged;
        room.Changed += UpdatePeople;
        room.ProfileChanged += UpdatePeople;
        shell.LiveStateChanged += UpdatePeople;
        SizeChanged += (_, _) => PeopleColumn.Width = new GridLength(ActualWidth >= 1000 ? 320 : 270);
        Loaded += (_, _) =>
        {
            UpdateAll();
            ScrollToEnd();
        };
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
        NoMessages.Visibility = room.Messages.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdatePeople();
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
        ConversationInfo.Text = others switch
        {
            0 => "Só você por aqui agora. As mensagens não ficam salvas depois que o ScreenFlow fecha.",
            1 => "Vai para 1 pessoa da rede. As mensagens não ficam salvas depois que o ScreenFlow fecha.",
            var n => $"Vai para {n} pessoas da rede. As mensagens não ficam salvas depois que o ScreenFlow fecha.",
        };

        SelfAvatar.Colors = AvatarColors.For(room.SelfId);
        SelfAvatar.Initial = AvatarColors.Initial(room.SelfName);
        SelfAvatar.Photo = room.SelfPhoto;
        SelfName.Text = $"{room.SelfName} (você)";
        var status = _shell.IsTransmitting ? $"Ao vivo · {_shell.TransmitPage.LiveSummary}" : _shell.IsWatching ? "Assistindo uma transmissão" : "Online";
        if (room.InVoice) status += room.Deafened ? " · na voz, sem som" : room.MicrophoneOff ? " · na voz, mudo" : " · na voz";
        SelfStatus.Text = status;
    }

    // ───────────────────────── Conversa ─────────────────────────

    private void OnMessagesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        NoMessages.Visibility = AppServices.Room.Messages.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
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

    private void SelfAvatar_MouseLeftButtonUp(object sender, MouseButtonEventArgs e) => _shell.Navigate(_shell.SettingsPage);

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
