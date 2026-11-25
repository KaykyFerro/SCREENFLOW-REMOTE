using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Shapes;
using ScreenFlow.App.Controls;
using ScreenFlow.App.Services;
using ScreenFlow.Core.Media;

namespace ScreenFlow.App.Pages;

/// <summary>Monitoramento: gráficos dos últimos 90 segundos da transmissão (ou do que você está assistindo).</summary>
public partial class MonitorPage : UserControl, IShellPage
{
    private readonly MainWindow _shell;
    private readonly MetricCard[] _cards = new MetricCard[6];

    public MonitorPage(MainWindow shell)
    {
        _shell = shell;
        InitializeComponent();
        for (var i = 0; i < _cards.Length; i++) MetricGrid.Children.Add(_cards[i] = new MetricCard());
        Telemetry.Updated += () =>
        {
            if (IsLoaded) Refresh();
        };
        shell.LiveStateChanged += () =>
        {
            if (IsLoaded) Refresh();
        };
        SizeChanged += (_, _) => MetricGrid.Columns = ActualWidth >= 980 ? 3 : 2;
    }

    public void OnShown() => Refresh();

    private void Source_Checked(object sender, RoutedEventArgs e)
    {
        if (IsInitialized && IsLoaded) Refresh();
    }

    private void Refresh()
    {
        var hosting = Telemetry.Hosting || _shell.IsTransmitting;
        var watching = Telemetry.Watching || _shell.IsWatching;
        SourceSwitch.Visibility = hosting && watching ? Visibility.Visible : Visibility.Collapsed;
        LivePanel.Visibility = hosting || watching ? Visibility.Visible : Visibility.Collapsed;
        EmptyState.Visibility = hosting || watching ? Visibility.Collapsed : Visibility.Visible;
        if (watching && (!hosting || SourceViewer.IsChecked == true)) ShowViewer();
        else if (hosting) ShowHost();
        else
        {
            SetBadge("Nada ao vivo", "TextSubtle", "SurfaceRaised");
            SubtitleText.Text = "Desempenho ao vivo, segundo a segundo, dos últimos 90 segundos.";
        }
    }

    // ───────────────────────── Transmissão ─────────────────────────

    private void ShowHost()
    {
        var s = Telemetry.Host;
        var audioOnly = s?.Mode == StreamMode.AudioOnly;
        SetBadge("Ao vivo", "Pink", "PinkSoft");
        SubtitleText.Text = s is null
            ? "Os números aparecem em instantes."
            : audioOnly
                ? $"Festa de música · {s.Encoder} · {People(s.Viewers)}"
                : $"{StreamModes.Label(s.Mode)} · {s.Target} · {s.Encoder} · {People(s.Viewers)}";

        if (audioOnly)
        {
            _cards[0].Set("Áudio enviado", "", "Pink", Telemetry.HostAudioKbps, Number(s?.AudioKbps, "0"), "kbps", "por pessoa");
            _cards[1].Set("Upload total", "", "Violet", Telemetry.HostUploadMbps, Number(s?.UploadMbps, "0.##"), "Mbps", "todos juntos");
            _cards[2].Set("Pessoas ouvindo", "", "Cyan", Telemetry.HostViewers, Number(s?.Viewers, "0"), string.Empty, $"máx {Telemetry.HostViewers.Max():0}");
            _cards[3].Set("Pior ping", "", "Amber", Telemetry.HostWorstPing, Number(Telemetry.HostWorstPing.Last, "0"), "ms", "entre quem ouve");
            _cards[4].Visibility = _cards[5].Visibility = Visibility.Collapsed;
        }
        else
        {
            _cards[4].Visibility = _cards[5].Visibility = Visibility.Visible;
            var targetFps = s?.Target.Fps ?? 0;
            _cards[0].Set("FPS do encoder", "", "Accent", Telemetry.HostFps, Number(s is { Viewers: > 0 } ? s.EncodeFps : null, "0"), "FPS",
                targetFps > 0 ? $"alvo {targetFps}" : string.Empty, targetFps);
            _cards[1].Set("Vídeo", "", "Cyan", Telemetry.HostVideoMbps, Number(s is { Viewers: > 0 } ? s.BitrateMbps : null, "0.#"), "Mbps",
                s is null ? string.Empty : $"alvo {s.Target.BitrateKbps / 1000.0:0.#}");
            _cards[2].Set("Upload total", "", "Violet", Telemetry.HostUploadMbps, Number(s?.UploadMbps, "0.#"), "Mbps", "todos juntos");
            _cards[3].Set("Pessoas assistindo", "", "Pink", Telemetry.HostViewers, Number(s?.Viewers, "0"), string.Empty, $"máx {Telemetry.HostViewers.Max():0}");
            _cards[4].Set("Pior ping", "", "Amber", Telemetry.HostWorstPing, Number(Telemetry.HostWorstPing.Last, "0"), "ms", "entre quem assiste");
            _cards[5].Set("Áudio", "", "Blue", Telemetry.HostAudioKbps, s is { AudioKbps: > 0 } ? $"{s.AudioKbps:0}" : "—", "kbps",
                s is { AudioKbps: > 0 } ? "Opus" : "sem som");
        }

        SetDetails(s is null ? [] :
        [
            ("Modo", StreamModes.Label(s.Mode)),
            ("Encoder", s.Encoder),
            (audioOnly ? "Som" : "Captura", s.Capture),
            ("Alvo", audioOnly ? $"Opus {s.AudioKbps:0} kbps" : s.Target.ToString()),
            ("Qualidade", s.Reason),
            ("Pessoas", People(s.Viewers)),
        ]);

        PeopleGap.Width = new GridLength(22);
        PeopleColumn.Width = new GridLength(1, GridUnitType.Star);
        PeopleCard.Visibility = Visibility.Visible;
        var audience = Telemetry.HostAudience;
        PeopleList.Children.Clear();
        foreach (var viewer in audience) PeopleList.Children.Add(PersonRow(viewer.Name, viewer.Address,
            audioOnly
                ? $"ping {viewer.RoundTripMs:0} ms · perda {viewer.LossRate:P1}"
                : $"{viewer.ReceivedMbps:0.#} Mbps · ping {viewer.RoundTripMs:0} ms · latência {viewer.LatencyMs:0} ms · perda {viewer.LossRate:P1}",
            viewer.LossRate > 0.03 || viewer.RoundTripMs > 150));
        NoPeople.Visibility = audience.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        PeopleCount.Text = People(audience.Count);
    }

    // ───────────────────────── Assistindo ─────────────────────────

    private void ShowViewer()
    {
        var s = Telemetry.Viewer;
        var audioOnly = s?.Mode == StreamMode.AudioOnly;
        SetBadge("Assistindo", "Cyan", "CyanSoft");
        var host = Telemetry.ViewerHost ?? "o anfitrião";
        SubtitleText.Text = s is null
            ? "Os números aparecem assim que a conexão abrir."
            : audioOnly
                ? $"Festa de música de {host} · Opus"
                : $"{host} · {s.Codec} {s.Width} × {s.Height} · {s.Decoder}";

        if (audioOnly)
        {
            _cards[0].Set("Áudio recebido", "", "Pink", Telemetry.ViewerMbps, Number(s?.AudioKbps, "0"), "kbps", "com cópia de segurança");
            _cards[1].Set("Ping", "", "Amber", Telemetry.ViewerPing, Number(s?.RoundTripMs, "0"), "ms", "ida e volta");
            _cards[2].Set("Perda", "", "Violet", Telemetry.ViewerLoss, Number(s is null ? null : s.LossRate * 100, "0.##"), "%", "antes da recuperação");
            _cards[3].Set("Atraso do som", "", "Blue", Telemetry.ViewerAudioBuffer, Number(s?.AudioBufferMs, "0"), "ms", "buffer");
            _cards[4].Visibility = _cards[5].Visibility = Visibility.Collapsed;
        }
        else
        {
            _cards[4].Visibility = _cards[5].Visibility = Visibility.Visible;
            _cards[0].Set("FPS recebido", "", "Accent", Telemetry.ViewerFps, Number(s?.Fps, "0"), "FPS", s?.GpuRendering == true ? "desenho na GPU" : "desenho na CPU");
            _cards[1].Set("Banda", "", "Cyan", Telemetry.ViewerMbps, Number(s?.BitrateMbps, "0.#"), "Mbps", s?.Codec ?? string.Empty);
            _cards[2].Set("Latência", "", "Violet", Telemetry.ViewerLatency, Number(s is { LatencyMs: > 0 } ? s.LatencyMs : null, "0"), "ms", "captura até a tela");
            _cards[3].Set("Ping", "", "Amber", Telemetry.ViewerPing, Number(s?.RoundTripMs, "0"), "ms", "ida e volta");
            _cards[4].Set("Perda", "", "Pink", Telemetry.ViewerLoss, Number(s is null ? null : s.LossRate * 100, "0.##"), "%", $"FEC salvou {s?.FecRecovered ?? 0:N0}");
            _cards[5].Set("Atraso do som", "", "Blue", Telemetry.ViewerAudioBuffer, Number(s?.AudioBufferMs, "0"), "ms", "buffer");
        }

        SetDetails(s is null ? [] : audioOnly
            ?
            [
                ("Anfitrião", host),
                ("Modo", "Festa de música"),
                ("Som", "Opus 48 kHz estéreo"),
                ("Recuperados", $"{s.AudioRecovered:N0} pacotes"),
                ("Qualidade", s.QualityReason),
            ]
            :
            [
                ("Anfitrião", host),
                ("Modo", StreamModes.Label(s.Mode)),
                ("Vídeo", $"{s.Codec} · {s.Width} × {s.Height}"),
                ("Decoder", s.Decoder),
                ("Desenho", s.GpuRendering ? "Direto na GPU (D3D11)" : "Pela CPU"),
                ("FEC", $"{s.FecRecovered:N0} recuperados · {s.FramesDropped:N0} frames perdidos"),
                ("Qualidade", s.QualityReason),
            ]);
        PeopleCard.Visibility = Visibility.Collapsed;
        PeopleGap.Width = new GridLength(0);
        PeopleColumn.Width = new GridLength(0);
    }

    // ───────────────────────── Ajudantes ─────────────────────────

    private static string Number(double? value, string format) => value is { } v && double.IsFinite(v) ? v.ToString(format) : "—";

    private static string People(int count) => count switch { 0 => "ninguém ainda", 1 => "1 pessoa", _ => $"{count} pessoas" };

    private void SetBadge(string text, string foreground, string background)
    {
        LiveText.Text = text;
        LiveBadge.SetResourceReference(TextElement.ForegroundProperty, foreground);
        LiveBadge.SetResourceReference(Border.BackgroundProperty, background);
        LiveBadge.SetResourceReference(Border.BorderBrushProperty, background);
        LiveDot.SetResourceReference(Shape.FillProperty, foreground);
    }

    private void SetDetails(IReadOnlyList<(string Key, string Value)> rows)
    {
        DetailsGrid.Children.Clear();
        DetailsGrid.RowDefinitions.Clear();
        if (rows.Count == 0)
        {
            DetailsGrid.RowDefinitions.Add(new RowDefinition());
            var waiting = new TextBlock { Text = "Esperando os primeiros números…", Style = (Style)FindResource("Muted") };
            Grid.SetColumnSpan(waiting, 2);
            DetailsGrid.Children.Add(waiting);
            return;
        }
        for (var i = 0; i < rows.Count; i++)
        {
            DetailsGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var key = new TextBlock { Text = rows[i].Key, Style = (Style)FindResource("Caption"), FontSize = 13, Margin = new Thickness(0, 0, 16, 10) };
            var value = new TextBlock { Text = rows[i].Value, Style = (Style)FindResource("Body"), FontSize = 13.5, Margin = new Thickness(0, 0, 0, 10), TextWrapping = TextWrapping.Wrap };
            Grid.SetRow(key, i);
            Grid.SetRow(value, i);
            Grid.SetColumn(value, 1);
            DetailsGrid.Children.Add(key);
            DetailsGrid.Children.Add(value);
        }
    }

    private Border PersonRow(string name, string address, string details, bool warn)
    {
        var avatar = new Border { Width = 36, Height = 36, CornerRadius = new CornerRadius(18), VerticalAlignment = VerticalAlignment.Center };
        avatar.SetResourceReference(Border.BackgroundProperty, "BrandGradient");
        var initial = new TextBlock { Text = name.Length > 0 ? char.ToUpperInvariant(name[0]).ToString() : "?", FontWeight = FontWeights.Bold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        initial.SetResourceReference(TextBlock.ForegroundProperty, "OnAccent");
        avatar.Child = initial;

        var title = new TextBlock { FontWeight = FontWeights.SemiBold };
        title.Inlines.Add(new Run(name));
        var address_ = new Run("  " + address) { FontWeight = FontWeights.Normal, FontSize = 12.5 };
        address_.SetResourceReference(TextElement.ForegroundProperty, "TextSubtle");
        title.Inlines.Add(address_);
        var info = new TextBlock { Text = details, Style = (Style)FindResource("Caption"), Margin = new Thickness(0, 2, 0, 0), TextWrapping = TextWrapping.Wrap };
        if (warn) info.SetResourceReference(TextBlock.ForegroundProperty, "Warning");
        var text = new StackPanel { Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(title);
        text.Children.Add(info);

        var row = new DockPanel();
        DockPanel.SetDock(avatar, Dock.Left);
        row.Children.Add(avatar);
        row.Children.Add(text);
        return new Border { Style = (Style)FindResource("InsetCard"), Margin = new Thickness(0, 0, 0, 8), Child = row };
    }

    private void GoTransmit_Click(object sender, RoutedEventArgs e) => _shell.Navigate(_shell.TransmitPage);
    private void GoWatch_Click(object sender, RoutedEventArgs e) => _shell.Navigate(_shell.WatchPage);
}
