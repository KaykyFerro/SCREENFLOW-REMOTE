using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using ScreenFlow.App.Services;
using ScreenFlow.Core.Updates;

namespace ScreenFlow.App.Pages;

/// <summary>Atualizações: versão instalada, procurar e instalar com um clique, novidades e preferência de aviso.</summary>
public partial class UpdatesPage : UserControl, IShellPage
{
    /// <summary>O que mudou nesta versão (mostrado quando não há versão nova).</summary>
    private static readonly string[] CurrentNotes =
    [
        "Sala da rede como um servidor do Discord: canais de texto e de voz, quem está em cada canal e os seus próprios canais.",
        "Volume de cada pessoa da voz só para você, de 0 a 200% (botão direito na pessoa).",
        "O som da transmissão não muda com o volume do PC de quem transmite: cada pessoa controla o próprio volume.",
        "O ScreenFlow aparece como player de mídia do Windows: no painel do volume, na tela de bloqueio e nas teclas de mídia.",
        "Guardar o chat no Google Drive (ou em qualquer pasta): as últimas conversas voltam quando o app abre.",
        "Configurações em abas, e quem está na sua rede entra na transmissão direto, sem pedir permissão.",
        "A barra do player não muda mais de tamanho, as telas foram revisadas para janelas pequenas e o app ficou mais leve.",
        "As imagens do chat abrem dentro do ScreenFlow: zoom, arraste, setas entre imagens, copiar e salvar.",
    ];

    private readonly MainWindow _shell;
    private bool _loading;

    public UpdatesPage(MainWindow shell)
    {
        _shell = shell;
        InitializeComponent();
        AppServices.Updates.Changed += () =>
        {
            if (!Dispatcher.CheckAccess()) Dispatcher.BeginInvoke(Refresh);
            else Refresh();
        };
        SizeChanged += (_, _) => ResponsiveGrid.Apply(Columns, ActualWidth < 900);
    }

    /// <summary>Em janela estreita os botões descem para baixo do título, sem cobrir o texto.</summary>
    private void HeroLayout_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        var stacked = HeroLayout.ActualWidth < 880;
        Grid.SetRow(Actions, stacked ? 1 : 0);
        Grid.SetColumn(Actions, stacked ? 0 : 1);
        Grid.SetColumnSpan(Actions, stacked ? 2 : 1);
        Actions.HorizontalAlignment = stacked ? HorizontalAlignment.Left : HorizontalAlignment.Stretch;
        Actions.Margin = stacked ? new Thickness(84, 20, 0, 0) : new Thickness(16, 0, 0, 0);
    }

    private static UpdateService Updates => AppServices.Updates;

    public void OnShown()
    {
        _shell.ClearUpdateBadge();
        _loading = true;
        AutoToggle.IsChecked = AppServices.Settings.Current.CheckUpdatesOnStart;
        _loading = false;
        InstalledText.Text = UpdateService.CurrentVersion;
        Refresh();
        // Abriu a tela e faz tempo que não procura: procura de novo.
        if (!AppServices.DemoMode && !Updates.Checking && !Updates.Installing &&
            (Updates.LastCheck is null || DateTimeOffset.Now - Updates.LastCheck > TimeSpan.FromMinutes(30)))
            _ = Updates.CheckAsync(manual: false);
    }

    private void Refresh()
    {
        var release = Updates.Available;
        var current = UpdateService.CurrentVersion;
        VersionTitle.Text = $"ScreenFlow {current}";
        CheckButton.IsEnabled = !Updates.Checking && !Updates.Installing;
        CheckText.Text = Updates.Checking ? "Procurando…" : "Procurar agora";
        InstallButton.Visibility = release is not null ? Visibility.Visible : Visibility.Collapsed;
        InstallButton.IsEnabled = !Updates.Installing;
        InstallText.Text = Updates.Installing ? "Atualizando…" : $"Atualizar para a {release?.Version.ToString(3)}";
        HeroTint.Opacity = release is not null ? 0.32 : 0;
        SkipButton.Visibility = release is not null && !Updates.Installing ? Visibility.Visible : Visibility.Collapsed;
        NotesBadge.Visibility = release is not null ? Visibility.Visible : Visibility.Collapsed;

        if (Updates.Error is { } error)
            SetStatus(error, Color.FromRgb(0xFF, 0xC2, 0x66));
        else if (Updates.Installing)
            SetStatus($"Instalando a versão {release?.Version.ToString(3)}. O ScreenFlow fecha e abre de novo sozinho.", Color.FromRgb(0x22, 0xD3, 0xEE));
        else if (Updates.Checking)
            SetStatus("Procurando uma versão nova no GitHub…", Color.FromRgb(0x22, 0xD3, 0xEE));
        else if (release is not null)
            SetStatus(AppServices.Settings.Current.SkippedVersion == release.Tag
                ? $"A versão {release.Version.ToString(3)} está disponível (você tinha pulado esta)."
                : $"A versão {release.Version.ToString(3)} está disponível. Atualize com um clique.", Color.FromRgb(0xFF, 0x5C, 0x97));
        else if (Updates.LastCheck is null)
            SetStatus("Ainda não procurou por versões novas.", Color.FromRgb(0xA9, 0xB4, 0xCF));
        else
            SetStatus("Você está na versão mais recente.", Color.FromRgb(0x3D, 0xF0, 0xB0));

        ProgressPanel.Visibility = Updates.Installing ? Visibility.Visible : Visibility.Collapsed;
        DownloadBar.Value = Updates.Progress;
        ProgressPercent.Text = $"{Updates.Progress:P0}";
        ProgressText.Text = Updates.Progress >= 1 ? "Download conferido. Abrindo o instalador…" : "Baixando o instalador oficial…";

        LastCheckText.Text = Updates.LastCheck is { } last ? Friendly(last.ToLocalTime()) : "Nunca";
        if (release is not null)
        {
            NotesTitle.Text = $"Novidades da versão {release.Version.ToString(3)}";
            RenderNotes(string.IsNullOrWhiteSpace(release.Notes) ? $"Veja as novidades em {UpdateService.ReleasesPage}" : release.Notes);
        }
        else
        {
            NotesTitle.Text = $"Novidades da versão {current}";
            RenderBullets(CurrentNotes);
        }
    }

    private void SetStatus(string text, Color color)
    {
        StatusText.Text = text;
        StatusDot.Fill = new SolidColorBrush(color);
    }

    private static string Friendly(DateTimeOffset when)
    {
        var today = DateTimeOffset.Now.Date;
        return when.Date == today ? $"hoje às {when:HH:mm}"
            : when.Date == today.AddDays(-1) ? $"ontem às {when:HH:mm}"
            : when.ToString("dd/MM/yyyy 'às' HH:mm");
    }

    // ───────────────────────── Notas da versão (Markdown simples) ─────────────────────────

    private void RenderBullets(IEnumerable<string> items)
    {
        NotesPanel.Children.Clear();
        foreach (var item in items) NotesPanel.Children.Add(Bullet(item));
    }

    /// <summary>Mostra o texto do release do GitHub: títulos, listas e parágrafos (sem links nem imagens).</summary>
    private void RenderNotes(string markdown)
    {
        NotesPanel.Children.Clear();
        var lines = markdown.Replace("\r", string.Empty).Split('\n');
        var shown = 0;
        foreach (var raw in lines)
        {
            var line = Clean(raw.Trim());
            if (line.Length == 0 || line.StartsWith("<!--") || line.StartsWith("![") || line.StartsWith("---")) continue;
            if (++shown > 40) break;
            if (raw.TrimStart().StartsWith('#'))
            {
                NotesPanel.Children.Add(new TextBlock
                {
                    Text = line.TrimStart('#', ' '),
                    Style = (Style)FindResource("Body"),
                    FontWeight = FontWeights.Bold,
                    Margin = new Thickness(0, NotesPanel.Children.Count == 0 ? 0 : 12, 0, 8),
                });
            }
            else if (raw.TrimStart() is var t && (t.StartsWith("- ") || t.StartsWith("* ") || Regex.IsMatch(t, @"^\d+\.\s")))
                NotesPanel.Children.Add(Bullet(Regex.Replace(line, @"^([-*]|\d+\.)\s+", string.Empty)));
            else
                NotesPanel.Children.Add(new TextBlock { Text = line, Style = (Style)FindResource("Muted"), FontSize = 13.5, Margin = new Thickness(0, 0, 0, 8) });
        }
        if (NotesPanel.Children.Count == 0) RenderBullets(["Correções e melhorias."]);
    }

    /// <summary>Tira a marcação do Markdown que não tem como mostrar aqui (negrito, código, links).</summary>
    private static string Clean(string text)
    {
        text = Regex.Replace(text, @"\[([^\]]+)\]\([^)]+\)", "$1");
        return text.Replace("**", string.Empty).Replace("__", string.Empty).Replace("`", string.Empty);
    }

    private FrameworkElement Bullet(string text)
    {
        var dot = new Border { Width = 7, Height = 7, CornerRadius = new CornerRadius(3.5), Margin = new Thickness(2, 7, 12, 0), VerticalAlignment = VerticalAlignment.Top };
        dot.SetResourceReference(Border.BackgroundProperty, "BrandGradient");
        var body = new TextBlock { Text = text, Style = (Style)FindResource("Body"), FontSize = 14, TextWrapping = TextWrapping.Wrap, LineHeight = 21 };
        var row = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };
        DockPanel.SetDock(dot, Dock.Left);
        row.Children.Add(dot);
        row.Children.Add(body);
        return row;
    }

    // ───────────────────────── Ações ─────────────────────────

    private async void Check_Click(object sender, RoutedEventArgs e)
    {
        var release = await Updates.CheckAsync(manual: true);
        if (release is null && Updates.Error is null) _shell.Toast($"Tudo certo: a {UpdateService.CurrentVersion} é a versão mais recente.", ToastKind.Success);
    }

    private void Install_Click(object sender, RoutedEventArgs e)
    {
        if (Updates.Available is { } release) _ = _shell.InstallUpdateAsync(release);
    }

    private void Skip_Click(object sender, RoutedEventArgs e)
    {
        if (Updates.Available is not { } release) return;
        Updates.Skip(release);
        Notifications.Remove("update");
        _shell.Toast($"Ok, a versão {release.Version.ToString(3)} não vai mais aparecer no sininho. Dá para atualizar por aqui quando quiser.", ToastKind.Info);
        Refresh();
    }

    private void AutoToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading || !IsInitialized) return;
        AppServices.Settings.Current.CheckUpdatesOnStart = AutoToggle.IsChecked == true;
        AppServices.Settings.Save();
    }

    private void Releases_Click(object sender, RoutedEventArgs e) => MainWindow.OpenUrl(UpdateService.ReleasesPage);

    /// <summary>Usado nas capturas de tela do CI: finge que existe uma versão nova.</summary>
    internal static ReleaseInfo DemoRelease { get; } = new(new Version(1, 4, 0), "v1.4.0", "ScreenFlow 1.4.0",
        "## Novidades\n- Melhorias de desempenho no player\n- Ajustes no visual\n\n## Correções\n- Correções de estabilidade",
        "https://example.invalid/ScreenFlow-Setup-win-x64.exe", "ScreenFlow-Setup-win-x64.exe", 48_000_000, null);
}
