using System.Windows;
using System.Windows.Controls;
using System.Windows.Shell;
using ScreenFlow.App.Services;

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
    public static string? Ask(Window? owner, string host, bool wrongPin)
    {
        var window = Build(owner, host, wrongPin, out var box);
        return window.ShowDialog() == true && box.Password.Length > 0 ? box.Password : null;
    }

    /// <summary>Monta a janela do PIN sem abrir (as capturas de tela do CI usam direto).</summary>
    internal static Window Build(Window? owner, string host, bool wrongPin, out PasswordBox passwordBox)
    {
        var box = passwordBox = new PasswordBox { MaxLength = 32 };
        var hint = new TextBlock
        {
            Text = wrongPin ? "PIN incorreto. Confira com quem está transmitindo e tente de novo." : string.Empty,
            Style = (Style)Application.Current.FindResource("Caption"),
            Margin = new Thickness(0, 8, 0, 0),
            Visibility = wrongPin ? Visibility.Visible : Visibility.Collapsed,
        };
        hint.SetResourceReference(TextBlock.ForegroundProperty, "Danger");
        var extra = new StackPanel();
        extra.Children.Add(box);
        extra.Children.Add(hint);

        var window = Dialog.Create(Dialog.Usable(owner), "", wrongPin ? "IconTilePink" : "IconTileAmber", $"{host} pede um PIN",
            "Quem está transmitindo protegeu a sessão com um PIN. Peça para essa pessoa e digite aqui.", extra, "Entrar", "Cancelar", null, out var ok);
        ok.Click += (_, _) => window.DialogResult = true;
        window.Loaded += (_, _) => box.Focus();
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
