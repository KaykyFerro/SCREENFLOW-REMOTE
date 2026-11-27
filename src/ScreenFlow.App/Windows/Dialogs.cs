using System.Windows;
using System.Windows.Controls;
using System.Windows.Shell;
using ScreenFlow.App.Services;
using ScreenFlow.Core.Room;

namespace ScreenFlow.App.Windows;

/// <summary>Janela de diálogo no estilo do app: cartão escuro com ícone, título, texto e botões.</summary>
internal static class Dialog
{
    public static Window Create(Window? owner, string glyph, string tileStyle, string title, string message, FrameworkElement? extra,
        string confirmText, string cancelText, Style? confirmStyle, out Button confirm)
    {
        var app = Application.Current;
        var tile = new Border { Style = (Style)app.FindResource(tileStyle), VerticalAlignment = VerticalAlignment.Top };
        tile.Child = new TextBlock { Style = (Style)app.FindResource("Icon"), Text = glyph };

        var close = new Button { Style = (Style)app.FindResource("IconButton"), Content = "", FontSize = 11, Width = 32, Height = 32, MinHeight = 32, IsCancel = false };
        WindowChrome.SetIsHitTestVisibleInChrome(close, true);
        DockPanel.SetDock(close, Dock.Right);

        var heading = new TextBlock { Text = title, Style = (Style)app.FindResource("Heading"), FontSize = 19, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(16, 9, 8, 0) };
        var header = new DockPanel();
        header.Children.Add(close);
        header.Children.Add(tile);
        header.Children.Add(heading);

        var body = new StackPanel { Margin = new Thickness(28, 24, 28, 26) };
        body.Children.Add(header);
        body.Children.Add(new TextBlock { Text = message, Style = (Style)app.FindResource("Muted"), Margin = new Thickness(60, 6, 0, 0), LineHeight = 21 });
        if (extra is not null)
        {
            extra.Margin = new Thickness(60, 16, 0, 0);
            body.Children.Add(extra);
        }

        confirm = new Button { Content = confirmText, IsDefault = true, MinWidth = 120, Style = confirmStyle ?? (Style)app.FindResource("PrimaryButton") };
        var cancel = new Button { Content = cancelText, IsCancel = true, MinWidth = 110, Margin = new Thickness(0, 0, 10, 0) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 24, 0, 0) };
        buttons.Children.Add(cancel);
        buttons.Children.Add(confirm);
        body.Children.Add(buttons);

        var window = new Window
        {
            Title = "ScreenFlow",
            Content = body,
            Width = 470,
            SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = owner is null,
            WindowStartupLocation = owner is null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner,
            Owner = owner,
            UseLayoutRounding = true,
        };
        WindowChrome.SetWindowChrome(window, new WindowChrome
        {
            CaptionHeight = 56, ResizeBorderThickness = new Thickness(0), GlassFrameThickness = new Thickness(0), CornerRadius = new CornerRadius(0), UseAeroCaptionButtons = false,
        });
        System.Windows.Media.TextOptions.SetTextFormattingMode(window, System.Windows.Media.TextFormattingMode.Ideal);
        if (owner is PlayerWindow) window.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/Themes/Dark.xaml", UriKind.Relative) });
        window.SetResourceReference(Control.BackgroundProperty, "Surface");
        window.SetResourceReference(Control.ForegroundProperty, "Text");
        window.SetResourceReference(Control.FontFamilyProperty, "FontUi");
        window.SetResourceReference(Control.FontSizeProperty, "FontSizeBody");
        window.SourceInitialized += (_, _) => ThemeManager.ApplyFrame(window);
        close.Click += (_, _) => window.DialogResult = false;
        return window;
    }

    public static Window? Usable(Window? owner) => owner is { IsVisible: true } ? owner : null;
}

/// <summary>Pede o PIN quando o anfitrião exige.</summary>
public static class PinDialog
{
    /// <param name="server">(1.4.1) Pela transmissão de um servidor ScreenFlow: pede a senha do servidor, não o PIN.</param>
    public static string? Ask(Window? owner, string host, bool wrongPin, bool server = false)
    {
        var window = Build(owner, host, wrongPin, out var box, server);
        return window.ShowDialog() == true && box.Password.Length > 0 ? box.Password : null;
    }

    /// <summary>Monta a janela do PIN sem abrir (as capturas de tela do CI usam direto).</summary>
    internal static Window Build(Window? owner, string host, bool wrongPin, out PasswordBox passwordBox, bool server = false)
    {
        var box = passwordBox = new PasswordBox { MaxLength = server ? Core.Server.ServerCommandLine.MaxPasswordLength : 32 };
        var hint = new TextBlock
        {
            Text = !wrongPin ? string.Empty
                : server ? "Senha incorreta. Confira com quem cuida do servidor e tente de novo."
                : "PIN incorreto. Confira com quem está transmitindo e tente de novo.",
            Style = (Style)Application.Current.FindResource("Caption"),
            Margin = new Thickness(0, 8, 0, 0),
            Visibility = wrongPin ? Visibility.Visible : Visibility.Collapsed,
        };
        hint.SetResourceReference(TextBlock.ForegroundProperty, "Danger");
        var extra = new StackPanel();
        extra.Children.Add(box);
        extra.Children.Add(hint);

        var window = server
            ? Dialog.Create(Dialog.Usable(owner), "", wrongPin ? "IconTilePink" : "IconTileAmber", $"O servidor {host} pede senha",
                "O servidor ScreenFlow tem senha. Ela fica salva em Configurações › Servidor para as próximas vezes.", extra, "Entrar", "Cancelar", null, out var ok)
            : Dialog.Create(Dialog.Usable(owner), "", wrongPin ? "IconTilePink" : "IconTileAmber", $"{host} pede um PIN",
                "Quem está transmitindo protegeu a sessão com um PIN. Peça para essa pessoa e digite aqui.", extra, "Entrar", "Cancelar", null, out ok);
        ok.Click += (_, _) => window.DialogResult = true;
        window.Loaded += (_, _) => box.Focus();
        return window;
    }
}

/// <summary>O nome de um canal novo da sala (1.3.0), ou o nome novo de um canal (1.4.1), com a prévia de como ele vai aparecer.</summary>
public static class ChannelDialog
{
    /// <summary>
    /// Null quando a pessoa cancelou. Com <paramref name="rename"/>, muda o nome desse canal (1.5.0: num servidor por rede,
    /// <paramref name="currentName"/> é o nome que aparece; o canal continua o mesmo).
    /// </summary>
    public static string? Ask(Window? owner, RoomChannelKind kind, RoomChannel? rename = null, bool server = false, string? currentName = null)
    {
        var window = Build(owner, kind, out var box, rename, server, currentName);
        return window.ShowDialog() == true ? box.Text : null;
    }

    /// <summary>Monta a janela do canal sem abrir (as capturas de tela do CI usam direto).</summary>
    internal static Window Build(Window? owner, RoomChannelKind kind, out TextBox nameBox, RoomChannel? rename = null, bool server = false, string? currentName = null)
    {
        var text = kind == RoomChannelKind.Text;
        var shown = rename is null ? null : currentName ?? rename.Name;
        var box = nameBox = new TextBox { MaxLength = 40, Tag = text ? "ex.: campeonato" : "ex.: Valorant", Text = shown ?? string.Empty };
        var preview = new TextBlock { Style = (Style)Application.Current.FindResource("Caption"), Margin = new Thickness(0, 8, 0, 0) };
        var extra = new StackPanel();
        extra.Children.Add(box);
        extra.Children.Add(preview);
        var title = shown is not null ? (text ? $"Mudar o nome de #{shown}" : $"Mudar o nome de {shown}")
            : text ? "Criar canal de texto" : "Criar canal de voz";
        var message = rename is not null
            ? text ? "A conversa do canal vai junto para o nome novo." : "Quem está no canal continua nele com o nome novo."
            : server
                ? text ? "O canal fica guardado no servidor e aparece para todo mundo nele, mesmo com você fora. Qualquer pessoa no servidor pode mudar o nome ou apagar."
                    : "Quem está no canal só ouve quem está nele, como no Discord. Ele fica guardado no servidor, e qualquer pessoa nele pode mudar o nome ou apagar."
            : text ? "O canal aparece para todo mundo da sala enquanto você estiver nela. Quem está no ScreenFlow 1.2 ou mais antigo só vê o #geral."
                : "Quem está no canal só ouve quem está nele, como no Discord. Ele aparece para todo mundo enquanto você estiver na sala (ou enquanto tiver alguém nele).";
        var window = Dialog.Create(Dialog.Usable(owner), rename is not null ? "" : text ? "" : "", "IconTileCyan", title, message,
            extra, rename is not null ? "Mudar o nome" : "Criar canal", "Cancelar", null, out var ok);

        void Update()
        {
            var channel = RoomChannels.Create(kind, box.Text);
            ok.IsEnabled = channel is not null && (shown is null || channel.Name != shown);
            preview.Text = channel is null
                ? box.Text.Trim().Length == 0 ? $"Até {RoomChannels.MaxNameLength} letras." : "Use letras ou números."
                : text ? $"Vai aparecer como #{channel.Name}" : $"Vai aparecer como o canal de voz {channel.Name}";
        }

        box.TextChanged += (_, _) => Update();
        Update();
        ok.Click += (_, _) => window.DialogResult = true;
        window.Loaded += (_, _) =>
        {
            box.Focus();
            box.SelectAll();
        };
        return window;
    }
}

/// <summary>Confirmação de uma ação (trocar de transmissão, atualizar no meio de uma transmissão).</summary>
public static class ConfirmDialog
{
    public static bool Ask(Window? owner, string title, string message, string confirmText, bool danger = false)
    {
        var window = Dialog.Create(Dialog.Usable(owner), danger ? "" : "", danger ? "IconTilePink" : "IconTileViolet", title, message, null,
            confirmText, "Cancelar", danger ? (Style)Application.Current.FindResource("DangerButton") : null, out var ok);
        ok.Click += (_, _) => window.DialogResult = true;
        return window.ShowDialog() == true;
    }
}
