using System.Windows;
using System.Windows.Controls;

namespace ScreenFlow.App.Pages;

public partial class HomePage : UserControl
{
    private readonly MainWindow _shell;

    public HomePage(MainWindow shell)
    {
        _shell = shell;
        InitializeComponent();
    }

    private void Transmit_Click(object sender, RoutedEventArgs e) => _shell.Navigate(_shell.TransmitPage, "Transmitir");
    private void Watch_Click(object sender, RoutedEventArgs e) => _shell.Navigate(_shell.WatchPage, "Assistir");
}
