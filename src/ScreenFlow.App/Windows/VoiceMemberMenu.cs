using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using ScreenFlow.App.Pages;
using ScreenFlow.App.Services;

namespace ScreenFlow.App.Windows;

/// <summary>
/// O botão direito em alguém na voz, como no Discord: o volume dessa pessoa só para você (0 a 200%) e silenciar só
/// para você. Em você mesmo: microfone, fone, as configurações da voz e sair.
/// </summary>
internal static class VoiceMemberMenu
{
    /// <summary>Abre o menu no ponteiro do mouse (as capturas de tela do CI usam o menu devolvido).</summary>
    public static ContextMenu Open(FrameworkElement target, VoiceMemberRow member, MainWindow shell)
    {
        var room = AppServices.Room;
        var menu = new ContextMenu { PlacementTarget = target, Placement = PlacementMode.MousePoint };
        if (member.IsMe)
        {
            menu.Items.Add(Item("Microfone desligado", "", room.ToggleMute, room.MicrophoneOff));
            menu.Items.Add(Item("Som desligado", "", room.ToggleDeafen, room.Deafened));
            menu.Items.Add(Item("Configurações da voz", "", () => shell.OpenSettings(SettingsTab.Voice)));
            if (room.InVoice)
            {
                menu.Items.Add(new Separator());
                var leave = Item("Sair da voz", "", () => _ = room.LeaveVoiceAsync());
                leave.SetResourceReference(Control.ForegroundProperty, "Danger");
                menu.Items.Add(leave);
            }
            menu.IsOpen = true;
            return menu;
        }

        var app = Application.Current;
        var caption = (Style)app.FindResource("Caption");
        var title = new TextBlock { Text = member.Name, FontWeight = FontWeights.SemiBold, FontSize = 14, TextTrimming = TextTrimming.CharacterEllipsis };
        var hint = new TextBlock { Text = "Volume só para você: os outros continuam ouvindo normal.", Style = caption, Margin = new Thickness(0, 2, 0, 10) };
        var value = new TextBlock { Style = caption, Width = 42, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        var slider = new Slider
        {
            Minimum = 0,
            Maximum = 2,
            SmallChange = 0.05,
            LargeChange = 0.1,
            Value = member.Volume,
            IsMoveToPointEnabled = true,
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip = "0% (silenciado) até 200%",
        };
        var line = new DockPanel();
        DockPanel.SetDock(value, Dock.Right);
        line.Children.Add(value);
        line.Children.Add(slider);
        var panel = new StackPanel { Width = 250 };
        panel.Children.Add(title);
        panel.Children.Add(hint);
        panel.Children.Add(line);
        menu.Items.Add(new MenuItem { Header = panel, Style = (Style)app.FindResource("MenuPanel") });
        menu.Items.Add(new Separator());

        var mute = Item("Silenciar para mim", "", () => room.SetPeerMuted(member.Id, !member.LocallyMuted), member.LocallyMuted);
        menu.Items.Add(mute);
        menu.Items.Add(Item("Volume normal (100%)", "", () => room.SetPeerVolume(member.Id, 1)));
        // Moderação (1.4.0): bloquear só para você; o dono da sala privada também pode remover da sala.
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Bloquear para mim", "\uE8F8", () =>
        {
            room.SetBlocked(member.Id, member.Name, true);
            shell.Toast($"{member.Name} foi bloqueado: nada dessa pessoa chega mais até você. Desbloqueie em Configurações › Voz e sala.", ToastKind.Info);
        }));
        if (room.IsPrivateRoomOwner)
            menu.Items.Add(Item("Remover da sala privada", "\uE8F8", () =>
            {
                if (room.RemovePrivateMember(member.Id, member.Name))
                    shell.Toast($"{member.Name} saiu da sala privada e a chave foi trocada para os outros.", ToastKind.Success);
            }));
        value.Text = $"{member.Volume * 100:0}%";
        slider.ValueChanged += (_, e) =>
        {
            room.SetPeerVolume(member.Id, e.NewValue, save: false);
            value.Text = $"{e.NewValue * 100:0}%";
            mute.IsChecked = e.NewValue <= 0.001;
        };
        // O volume é salvo uma vez, quando o menu fecha (não a cada passo do arraste).
        menu.Closed += (_, _) => AppServices.Settings.Save();
        menu.IsOpen = true;
        return menu;
    }

    private static MenuItem Item(string text, string glyph, Action action, bool isChecked = false)
    {
        var item = new MenuItem { Header = text, Tag = glyph, IsChecked = isChecked };
        item.Click += (_, _) => action();
        return item;
    }
}
