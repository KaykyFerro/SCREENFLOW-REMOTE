using System.Windows;
using System.Windows.Controls;

namespace ScreenFlow.App.Windows;

/// <summary>Janela pequena que pede o PIN quando o host exige.</summary>
public static class PinDialog
{
    public static string? Ask(Window? owner, string host, bool wrongPin)
    {
        var box = new PasswordBox { MaxLength = 32, Margin = new Thickness(0, 12, 0, 16) };
        var ok = new Button { Content = "Entrar", IsDefault = true, MinWidth = 110, Style = (Style)Application.Current.FindResource("PrimaryButton") };
        var cancel = new Button { Content = "Cancelar", IsCancel = true, MinWidth = 110, Margin = new Thickness(8, 0, 0, 0) };
        var title = new TextBlock { Text = $"{host} pede um PIN", Style = (Style)Application.Current.FindResource("Heading") };
        var hint = new TextBlock
        {
            Text = wrongPin ? "PIN incorreto. Tente de novo." : "Peça o PIN para quem está transmitindo.",
            Style = (Style)Application.Current.FindResource(wrongPin ? "Body" : "Muted"),
        };
        if (wrongPin) hint.SetResourceReference(TextBlock.ForegroundProperty, "Danger");

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        var content = new StackPanel { Margin = new Thickness(24) };
        content.Children.Add(title);
        content.Children.Add(hint);
        content.Children.Add(box);
        content.Children.Add(buttons);

        var window = new Window
        {
            Title = "ScreenFlow",
            Content = content,
            SizeToContent = SizeToContent.WidthAndHeight,
            MinWidth = 380,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = owner is null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner,
            Owner = owner,
            ShowInTaskbar = owner is null,
        };
        window.SetResourceReference(Window.BackgroundProperty, "Surface");
        window.SetResourceReference(Window.ForegroundProperty, "Text");
        window.SetResourceReference(Window.FontFamilyProperty, "FontUi");
        ok.Click += (_, _) => window.DialogResult = true;
        window.Loaded += (_, _) => box.Focus();
        return window.ShowDialog() == true && box.Password.Length > 0 ? box.Password : null;
    }
}
