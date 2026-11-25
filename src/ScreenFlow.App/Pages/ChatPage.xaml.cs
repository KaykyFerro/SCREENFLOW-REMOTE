using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using ScreenFlow.App.Services;

namespace ScreenFlow.App.Pages;

/// <summary>Chat da rede: uma conversa só com todo mundo que está com o ScreenFlow aberto na rede, e quem está lá.</summary>
public partial class ChatPage : UserControl, IShellPage
{
    private readonly MainWindow _shell;
    private bool _stickToBottom = true;

    public ChatPage(MainWindow shell)
    {
        _shell = shell;
        InitializeComponent();
        var room = AppServices.Room;
        MessageList.ItemsSource = room.Messages;
        PeopleList.ItemsSource = room.People;
        room.Messages.CollectionChanged += OnMessagesChanged;
        room.Changed += UpdatePeople;
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

        var avatar = AvatarColors.For(room.SelfId);
        SelfAvatar.Fill = avatar.Background;
        SelfInitial.Foreground = avatar.Foreground;
        SelfInitial.Text = AvatarColors.Initial(room.SelfName);
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
        if (string.IsNullOrWhiteSpace(Composer.Text)) return;
        if (!AppServices.Room.Send(Composer.Text))
        {
            _shell.Toast(AppServices.Room.Error ?? "A mensagem não foi enviada.", ToastKind.Warning);
            return;
        }
        Composer.Clear();
        _stickToBottom = true;
    }

    private void Composer_TextChanged(object sender, TextChangedEventArgs e) => UpdateComposer();

    private void UpdateComposer()
    {
        var length = Composer.Text.Length;
        SendButton.IsEnabled = !string.IsNullOrWhiteSpace(Composer.Text);
        CounterText.Text = length >= 400 ? $"{length}/500" : string.Empty;
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
