using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ScreenFlow.App.Pages;
using ScreenFlow.App.Windows;
using ScreenFlow.Core;
using ScreenFlow.Core.Diagnostics;
using ScreenFlow.Core.Media;
using ScreenFlow.Core.Room;
using ScreenFlow.Core.Settings;
using ScreenFlow.Infrastructure.Session;

namespace ScreenFlow.App.Services;

/// <summary>
/// ScreenFlow.exe --capturas pasta: abre cada tela com dados de exemplo (sem rede, GPU ou Radmin) e salva um PNG de
/// cada uma. Roda no CI para conferir o visual a cada mudança. A janela usa o tamanho padrão do app (mesmo que a tela
/// do CI seja menor) e algumas telas são salvas de novo no tamanho mínimo da janela.
/// </summary>
public static class Screenshots
{
    private const double Scale = 2;

    public static async Task<bool> RunAsync(string folder)
    {
        Directory.CreateDirectory(folder);
        var saved = 0;
        var failed = 0;

        // Sala da rede de exemplo: gente online, duas pessoas na voz e uma conversa no chat.
        AppServices.Settings.Current.DeviceName = "PC da Sala";
        // A sua foto é a paisagem inteira (aurora, montanha e mata), para não confundir com as fotos da Ana e do Bruno.
        AppServices.Room.SetPhoto(DemoData.ProfilePhoto(690, 10, 880));
        AppServices.Room.LoadDemo(inVoice: false, unread: 2);

        var window = new MainWindow
        {
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -30000,
            Top = -30000,
            ShowActivated = false,
            ShowInTaskbar = false,
        };
        var defaultSize = new Size(window.Width, window.Height);
        AllowAnySize(window);
        window.Show();
        Resize(window, defaultSize);
        await SettleAsync();
        AppServices.Updates.ShowDemo(UpdatesPage.DemoRelease);
        window.Toasts.Children.Clear();

        async Task Shot(string name, Func<Task> prepare, Func<FrameworkElement>? target = null)
        {
            try
            {
                await prepare();
                await SettleAsync();
                window.Toasts.Children.Clear();
                Save(target?.Invoke() ?? WindowRoot(window), Path.Combine(folder, name), window.Background);
                saved++;
            }
            catch (Exception exception)
            {
                failed++;
                Log.Error($"Captura {name} falhou", exception);
            }
        }

        await Shot("01-visao-geral.png", () => Go(window, window.OverviewPage));
        await Shot("02-transmissao-ao-vivo.png", () =>
        {
            window.Navigate(window.TransmitPage);
            window.TransmitPage.ShowDemo();
            return Task.Delay(600);
        });
        await Shot("19-tirar-som-de-apps.png", () =>
        {
            window.TransmitPage.ShowAppsDemo();
            return Task.Delay(300);
        }, () => (FrameworkElement)window.TransmitPage.AppsPopup.Child);
        window.TransmitPage.AppsPopup.IsOpen = false;
        await Shot("20-cartao-som-do-pc.png", () => Task.Delay(300), () => window.TransmitPage.AudioCard);
        await Shot("03-assistir.png", () => Go(window, window.WatchPage));
        await Shot("04-monitoramento.png", () =>
        {
            DemoData.FillTelemetry();
            return Go(window, window.MonitorPage);
        });
        // Configurações em abas (1.3.0): Transmissão, Voz e sala (com o chat guardado no Google Drive) e Assistir.
        await Shot("05-configuracoes.png", () =>
        {
            window.SettingsPage.ShowTab(SettingsTab.Stream);
            return Go(window, window.SettingsPage, 800);
        });
        await Shot("25-configuracoes-voz-e-sala.png", () =>
        {
            window.SettingsPage.ShowArchiveDemo();
            window.SettingsPage.ShowTab(SettingsTab.Voice);
            return Go(window, window.SettingsPage, 500);
        });
        await Shot("26-configuracoes-assistir.png", () =>
        {
            window.SettingsPage.ShowTab(SettingsTab.Player);
            return Go(window, window.SettingsPage, 400);
        });
        await Shot("06-atualizacoes.png", () => Go(window, window.UpdatesPage));
        await Shot("07-sininho.png", () =>
        {
            window.NotificationsPopup.IsOpen = true;
            return Task.Delay(300);
        }, () => (FrameworkElement)window.NotificationsPopup.Child);
        window.NotificationsPopup.IsOpen = false;
        await Shot("08-visao-geral-tema-claro.png", async () =>
        {
            ThemeManager.Apply(AppTheme.Light);
            window.Navigate(window.WatchPage);
            await Go(window, window.OverviewPage);
        });
        ThemeManager.Apply(AppTheme.Dark);

        // (1.4.1) Duas das 10 cores, uma no escuro e uma no claro, e a escolha da cor nas Configurações.
        await Shot("33-tema-violeta.png", async () =>
        {
            ThemeManager.Apply(AppTheme.Dark, "violeta");
            window.Navigate(window.WatchPage);
            await Go(window, window.OverviewPage);
        });
        await Shot("34-tema-laranja-claro.png", async () =>
        {
            ThemeManager.Apply(AppTheme.Light, "laranja");
            window.Navigate(window.OverviewPage);
            await Go(window, window.TransmitPage, 600);
        });
        await Shot("35-escolher-a-cor.png", () =>
        {
            ThemeManager.Apply(AppTheme.Dark, "rosa");
            window.SettingsPage.ShowTab(SettingsTab.General);
            return Go(window, window.SettingsPage, 600);
        });
        ThemeManager.Apply(AppTheme.Dark, null);

        // (1.4.1) A aba do servidor, com este PC como servidor (depois de abrir a tela, que lê as configurações ao abrir).
        await Shot("36-configuracoes-servidor.png", async () =>
        {
            window.Navigate(window.SettingsPage);
            window.SettingsPage.ShowServerDemo();
            window.SettingsPage.ShowTab(SettingsTab.Server);
            await Task.Delay(600);
        });

        // Voz conectada no menu lateral e o chat da rede aberto.
        await Shot("15-chat-da-rede.png", () =>
        {
            AppServices.Room.LoadDemo(inVoice: true, unread: 0);
            return Go(window, window.ChatPage, 500);
        });
        ChatImageViewerWindow? imageViewer = null;
        await Shot("31-chat-visualizador-de-imagens.png", () =>
        {
            var first = AppServices.Room.Messages.First(row => row.Image is not null && row.ImageState == ChatImageState.Ready);
            var second = new ChatRow(first.Message with { MessageId = first.MessageId + 1, Time = first.Time.AddMinutes(1), Text = "Outra imagem deste canal" }, first.IsMine);
            var third = new ChatRow(first.Message with { MessageId = first.MessageId + 2, Time = first.Time.AddMinutes(2), Text = "Mais uma imagem deste canal" }, first.IsMine);
            imageViewer = new ChatImageViewerWindow([first, second, third], 1)
            {
                Owner = window,
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = -30000,
                Top = -30000,
                ShowActivated = false,
                ShowInTaskbar = false,
            };
            AllowAnySize(imageViewer);
            imageViewer.Show();
            Resize(imageViewer, new Size(1000, 640));
            imageViewer.SetDemoView(1.25);
            return Task.Delay(500);
        }, () => WindowRoot(imageViewer!));
        await Shot("32-chat-visualizador-janela-pequena.png", () =>
        {
            Resize(imageViewer!, new Size(imageViewer!.MinWidth, imageViewer.MinHeight));
            return Task.Delay(500);
        }, () => WindowRoot(imageViewer!));
        imageViewer?.Close();
        await Shot("16-chat-tema-claro.png", async () =>
        {
            ThemeManager.Apply(AppTheme.Light);
            window.Navigate(window.OverviewPage);
            await Go(window, window.ChatPage, 500);
        });
        ThemeManager.Apply(AppTheme.Dark);
        await Shot("21-chat-imagem-anexada.png", () =>
        {
            window.ChatPage.ShowAttachmentDemo(DemoData.ChatPicture(), "Ficou bom esse fundo?");
            return Go(window, window.ChatPage, 500);
        });
        window.ChatPage.ShowAttachmentDemo(null, string.Empty);
        await Shot("22-seu-perfil.png", () =>
        {
            window.SettingsPage.ShowTab(SettingsTab.General);
            return Go(window, window.SettingsPage, 600);
        }, () => window.SettingsPage.ProfileCard);

        // Um canal de texto criado por alguém da sala e o botão direito em alguém da voz (volume só para você).
        await Shot("30-sala-canal-criado.png", () =>
        {
            AppServices.Room.SelectText("campeonato");
            return Go(window, window.ChatPage, 500);
        });
        // (1.5.0) Arquivos no chat: um vídeo com prévia, um .zip baixando, um programa com o aviso, um seu sendo baixado
        // e dois anexados na caixa de escrever (conferidos, com a prévia da imagem).
        await Shot("43-arquivos-no-chat.png", async () =>
        {
            AppServices.Room.Open.LoadDemoFiles();
            AppServices.Room.SelectText("campeonato");
            window.ChatPage.ShowFilesDemo(DemoAttachments(), "Placar da final e o print");
            await Go(window, window.ChatPage, 1200);
        });
        window.ChatPage.ShowFilesDemo([]);
        AppServices.Room.SelectText(null);
        ContextMenu? memberMenu = null;
        await Shot("28-volume-por-pessoa.png", async () =>
        {
            await Go(window, window.ChatPage, 300);
            var member = AppServices.Room.VoiceMembers.First(m => !m.IsMe);
            AppServices.Room.SetPeerVolume(member.Id, 0.8, save: false);
            memberMenu = VoiceMemberMenu.Open(window.ChatPage, member, window);
            await Task.Delay(400);
        }, () => memberMenu!);
        if (memberMenu is not null) memberMenu.IsOpen = false;

        PlayerWindow OpenPlayer(StreamMode mode, ConnectionState state, bool camera)
        {
            var session = new ViewerSession(new ViewerOptions("26.47.19.113", AppInfo.DefaultControlPort, "Capturas", null, 0.8f, [], 2560, 1440, 165, AudioOutput: false));
            var player = new PlayerWindow(session, "26.47.19.113", mode)
            {
                Width = 1280,
                Height = 720,
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = -30000,
                Top = -30000,
                ShowActivated = false,
                ShowInTaskbar = false,
            };
            AllowAnySize(player);
            player.Show();
            Resize(player, new Size(1280, 720));
            player.ShowDemo(mode, state, camera);
            return player;
        }

        foreach (var (name, mode, state, camera) in new[]
                 {
                     ("09-player-festa-de-musica.png", StreamMode.AudioOnly, ConnectionState.Connected, false),
                     ("10-player-conectando.png", StreamMode.Screen, ConnectionState.WaitingApproval, false),
                     ("18-player-tela-e-camera.png", StreamMode.Screen, ConnectionState.Connected, true),
                 })
        {
            PlayerWindow? player = null;
            await Shot(name, () =>
            {
                player = OpenPlayer(mode, state, camera);
                return Task.Delay(700);
            }, () => WindowRoot(player!));
            if (player is not null) await player.ShutdownAsync();
        }

        // Controles de quem assiste (1.2.0): as janelinhas de qualidade e de apps do som, abertas pelo player.
        PlayerWindow? remote = null;
        await Shot("23-player-trocar-qualidade.png", () =>
        {
            remote = OpenPlayer(StreamMode.Screen, ConnectionState.Connected, true);
            remote.ShowQualityDemo();
            return Task.Delay(700);
        }, () => (FrameworkElement)remote!.RemoteQualityPopup.Child);
        if (remote is not null)
        {
            remote.RemoteQualityPopup.IsOpen = false;
            await Shot("24-player-tirar-som-de-apps.png", () =>
            {
                remote.ShowAppsDemo();
                return Task.Delay(400);
            }, () => (FrameworkElement)remote.RemoteAppsPopup.Child);
            remote.RemoteAppsPopup.IsOpen = false;
            await remote.ShutdownAsync();
        }

        Window? dialog = null;
        await Shot("11-pedido-de-pin.png", () =>
        {
            dialog = PinDialog.Build(window, "Festa da Mari", wrongPin: true, out var box);
            box.Password = "1234";
            dialog.WindowStartupLocation = WindowStartupLocation.Manual;
            dialog.Left = dialog.Top = -30000;
            dialog.ShowActivated = false;
            dialog.Show();
            return Task.Delay(300);
        }, () => WindowRoot(dialog!));
        dialog?.Close();
        dialog = null;
        await Shot("29-criar-canal-de-voz.png", () =>
        {
            dialog = ChannelDialog.Build(window, RoomChannelKind.Voice, out var name);
            name.Text = "Minecraft";
            dialog.WindowStartupLocation = WindowStartupLocation.Manual;
            dialog.Left = dialog.Top = -30000;
            dialog.ShowActivated = false;
            dialog.Show();
            return Task.Delay(300);
        }, () => WindowRoot(dialog!));
        dialog?.Close();
        dialog = null;

        // (1.5.0) Um servidor por rede do Radmin aberto (só as pessoas, os canais e a conversa dele) e a janela de adicionar.
        await Shot("40-servidor-por-rede.png", () =>
        {
            AppServices.Room.ShowServer(AppServices.Room.AllServers[1]);
            return Go(window, window.ChatPage, 500);
        });
        AppServices.Room.ShowServer(AppServices.Room.Open);
        // (1.5.0) Mensagens diretas: a lista de conversas, a conversa aberta (com o estado de entrega) e quem é a pessoa.
        await Shot("42-mensagens-diretas.png", () =>
        {
            var direct = AppServices.Direct;
            direct.Show(direct.Conversations.FirstOrDefault(c => c.DemoMessages.Count > 1) ?? direct.Conversations.FirstOrDefault());
            direct.Showing = true;
            return Go(window, window.ChatPage, 500);
        });
        AppServices.Direct.Showing = false;
        await Shot("41-adicionar-servidor.png", () =>
        {
            dialog = ServerDialogs.BuildAdd(window, ServerDialogs.AddMode.Network, out _, ("amigos-do-cs", "senha-da-rede", "Amigos do CS"));
            dialog.WindowStartupLocation = WindowStartupLocation.Manual;
            dialog.Left = dialog.Top = -30000;
            dialog.ShowActivated = false;
            dialog.Show();
            return Task.Delay(300);
        }, () => WindowRoot(dialog!));
        dialog?.Close();

        // Janela no tamanho mínimo: o menu vira ícones e as colunas se empilham.
        Resize(window, new Size(window.MinWidth, window.MinHeight));
        await Shot("12-transmissao-janela-pequena.png", () => Go(window, window.TransmitPage, 600));
        await Shot("13-configuracoes-janela-pequena.png", () =>
        {
            window.SettingsPage.ShowTab(SettingsTab.Stream);
            return Go(window, window.SettingsPage, 600);
        });
        await Shot("27-configuracoes-voz-janela-pequena.png", () =>
        {
            window.SettingsPage.ShowTab(SettingsTab.Voice);
            return Go(window, window.SettingsPage, 500);
        });
        await Shot("14-atualizacoes-janela-pequena.png", () => Go(window, window.UpdatesPage));
        await Shot("17-chat-janela-pequena.png", () => Go(window, window.ChatPage, 500));

        Telemetry.EndHost();
        await window.StopAllAsync(); // a janela fecha junto com o app
        Log.Info($"Capturas: {saved} salvas em {folder}, {failed} com erro.");
        return failed == 0 && saved > 0;
    }


    /// <summary>(1.5.0) Dois arquivos de verdade numa pasta temporária, para a captura dos anexos (uma imagem e uma planilha).</summary>
    private static IReadOnlyList<string> DemoAttachments()
    {
        var folder = Path.Combine(Path.GetTempPath(), "ScreenFlow-capturas");
        Directory.CreateDirectory(folder);
        var picture = Path.Combine(folder, "print da final.jpg");
        File.WriteAllBytes(picture, DemoData.ChatPicture().Data);
        var sheet = Path.Combine(folder, "placar da final.xlsx");
        File.WriteAllBytes(sheet, System.Security.Cryptography.RandomNumberGenerator.GetBytes(48 * 1024));
        return [picture, sheet];
    }
    private static async Task Go(MainWindow window, UserControl page, int wait = 300)
    {
        window.Navigate(page);
        await Task.Delay(wait);
    }

    /// <summary>Espera o layout, as imagens e alguns quadros de animação.</summary>
    private static async Task SettleAsync()
    {
        await Application.Current.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        await Task.Delay(150);
        await Application.Current.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
    }

    /// <summary>A borda do modelo da janela: ocupa a área interna inteira e já desenha o fundo da janela.</summary>
    private static FrameworkElement WindowRoot(Window window) => (FrameworkElement)VisualTreeHelper.GetChild(window, 0);

    /// <summary>
    /// Salva exatamente a área do elemento (sem a margem dele e sem a moldura da janela), em escala 2x, sobre um fundo
    /// opaco. O conteúdo do VisualBrush vem com o deslocamento do elemento dentro do pai (a margem, ou a posição de um
    /// cartão na coluna), então a área lida começa nesse deslocamento.
    /// </summary>
    private static void Save(FrameworkElement element, string path, Brush? background)
    {
        element.UpdateLayout();
        var area = new Rect(0, 0, element.ActualWidth, element.ActualHeight);
        var offset = VisualTreeHelper.GetOffset(element);
        var width = Math.Max(1, (int)Math.Ceiling(area.Width * Scale));
        var height = Math.Max(1, (int)Math.Ceiling(area.Height * Scale));
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            if (background is not null) context.DrawRectangle(background, null, area);
            context.DrawRectangle(new VisualBrush(element)
            {
                Viewbox = new Rect(offset.X, offset.Y, area.Width, area.Height),
                ViewboxUnits = BrushMappingMode.Absolute,
                Stretch = Stretch.Fill,
            }, null, area);
        }
        var bitmap = new RenderTargetBitmap(width, height, 96 * Scale, 96 * Scale, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(path);
        encoder.Save(file);
        Log.Info($"Captura salva: {path} ({width}x{height})");
    }

    // ───────── Tamanho da janela maior que a tela (a tela do CI tem 1024 × 768) ─────────

    private const int WmGetMinMaxInfo = 0x0024;
    private const uint SwpNoZOrder = 0x0004, SwpNoActivate = 0x0010, SwpNoMove = 0x0002;

    [StructLayout(LayoutKind.Sequential)]
    private struct MinMaxInfo
    {
        public int ReservedX, ReservedY;
        public int MaxSizeX, MaxSizeY;
        public int MaxPositionX, MaxPositionY;
        public int MinTrackSizeX, MinTrackSizeY;
        public int MaxTrackSizeX, MaxTrackSizeY;
    }

    /// <summary>Tira o limite do Windows que prende a janela ao tamanho da tela.</summary>
    private static void AllowAnySize(Window window)
    {
        window.SourceInitialized += (_, _) =>
        {
            var source = HwndSource.FromHwnd(new WindowInteropHelper(window).Handle);
            source?.AddHook((nint hwnd, int message, nint wParam, nint lParam, ref bool handled) =>
            {
                if (message == WmGetMinMaxInfo)
                {
                    var info = Marshal.PtrToStructure<MinMaxInfo>(lParam);
                    info.MaxTrackSizeX = info.MaxSizeX = 8000;
                    info.MaxTrackSizeY = info.MaxSizeY = 8000;
                    Marshal.StructureToPtr(info, lParam, false);
                }
                return 0;
            });
        };
    }

    /// <summary>Muda o tamanho direto no Windows (passa pelo gancho acima) e deixa o WPF acompanhar.</summary>
    private static void Resize(Window window, Size size)
    {
        var handle = new WindowInteropHelper(window).Handle;
        var toDevice = PresentationSource.FromVisual(window)?.CompositionTarget?.TransformToDevice ?? Matrix.Identity;
        SetWindowPos(handle, 0, 0, 0, (int)Math.Round(size.Width * toDevice.M11), (int)Math.Round(size.Height * toDevice.M22),
            SwpNoZOrder | SwpNoActivate | SwpNoMove);
        window.Width = size.Width;
        window.Height = size.Height;
        window.UpdateLayout();
        Log.Info($"Capturas: janela {window.GetType().Name} em {window.ActualWidth:0}x{window.ActualHeight:0}");
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(nint hwnd, nint insertAfter, int x, int y, int width, int height, uint flags);
}
