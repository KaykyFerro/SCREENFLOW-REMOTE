using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using ScreenFlow.App.Services;
using ScreenFlow.Core;
using ScreenFlow.Core.Diagnostics;
using ScreenFlow.Core.Media;
using ScreenFlow.Core.Settings;
using ScreenFlow.Infrastructure.Network;

namespace ScreenFlow.App.Pages;

public partial class SettingsPage : UserControl
{
    private const string AutoEncoder = "Automático (melhor disponível)";
    private readonly MainWindow _shell;

    public SettingsPage(MainWindow shell)
    {
        _shell = shell;
        InitializeComponent();
        foreach (var r in Resolution.All) QualityCombo.Items.Add(r);
        foreach (var f in Quality.FrameRates) FpsCombo.Items.Add(f);
        foreach (var a in new[] { 96, 128, 160, 192, 256 }) AudioBitrateCombo.Items.Add(a);
        foreach (var n in new[] { 0, 4, 6, 8, 10, 16 }) FecCombo.Items.Add(n);
        ThemeCombo.Items.Add("Escuro");
        ThemeCombo.Items.Add("Claro");
        Loaded += async (_, _) => await LoadAsync();
    }

    private AppSettings Settings => AppServices.Settings.Current;

    private async Task LoadAsync()
    {
        var s = Settings;
        QualityCombo.SelectedItem = Resolution.FromLabel(s.Resolution);
        FpsCombo.SelectedItem = s.Fps;
        BitrateBox.Text = (s.BitrateKbps / 1000.0).ToString("0.#");
        AudioBitrateCombo.SelectedItem = AudioBitrateCombo.Items.Cast<int>().OrderBy(a => Math.Abs(a - s.AudioBitrateKbps)).First();
        FecCombo.SelectedItem = FecCombo.Items.Cast<int>().OrderBy(n => Math.Abs(n - s.FecGroupSize)).First();
        AdaptiveToggle.IsChecked = s.AdaptiveQuality;
        MediaPortBox.Text = s.MediaPort.ToString();
        ControlPortBox.Text = s.ControlPort.ToString();
        DiscoveryPortBox.Text = s.DiscoveryPort.ToString();
        UploadBox.Text = s.UploadMbps.ToString("0");
        MaxViewersBox.Text = s.MaxViewers.ToString();
        NameBox.Text = s.DeviceName;
        PinBox.Text = s.Pin ?? string.Empty;
        ThemeCombo.SelectedIndex = s.Theme == AppTheme.Light ? 1 : 0;
        ApproveToggle.IsChecked = s.ApproveEachViewer;
        UpdateToggle.IsChecked = s.CheckUpdatesOnStart;
        VersionText.Text = $"Versão instalada: {AppInfo.DisplayVersion}";
        FirewallText.Text = await Task.Run(() => FirewallManager.RulesExist(s.MediaPort, s.ControlPort, s.DiscoveryPort))
            ? "Regras do firewall criadas."
            : "Regras do firewall não encontradas: quem assiste pode não conseguir conectar.";

        AutoResult.Text = s.LastAutoConfig is { } when
            ? $"Última configuração automática: {when:dd/MM/yyyy HH:mm} → {s.Resolution} a {s.Fps} FPS, encoder {s.PreferredEncoder ?? "automático"}."
            : "Ainda não configurado automaticamente.";

        var hardware = await AppServices.Hardware;
        EncoderCombo.Items.Clear();
        EncoderCombo.Items.Add(AutoEncoder);
        foreach (var encoder in hardware.Encoders) EncoderCombo.Items.Add(encoder.Label);
        var current = hardware.Encoders.FirstOrDefault(e => e.FfmpegName == s.PreferredEncoder);
        EncoderCombo.SelectedItem = current?.Label ?? AutoEncoder;
        await RefreshDiagnosticsAsync();
    }

    private async void Auto_Click(object sender, RoutedEventArgs e)
    {
        AutoButton.IsEnabled = false;
        AutoResult.Text = "Detectando GPU, codecs, monitor e rede…";
        try
        {
            var result = await AppServices.AutoConfigureAsync(refresh: true);
            AutoResult.Text = $"Pronto: {result.Resolution.Label} a {result.Fps} FPS em {Quality.CodecLabel(result.Codec)} ({result.Encoder}), cerca de {result.BitrateKbps / 1000.0:0} Mbps por pessoa."
                              + (result.Notes.Count > 0 ? "\n• " + string.Join("\n• ", result.Notes) : string.Empty);
            await LoadAsync();
        }
        catch (Exception exception)
        {
            AutoResult.Text = $"Não foi possível configurar automaticamente: {exception.Message}";
        }
        finally { AutoButton.IsEnabled = true; }
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        var s = Settings;
        var portsBefore = (s.MediaPort, s.ControlPort, s.DiscoveryPort);
        s.Resolution = ((Resolution)QualityCombo.SelectedItem).Label;
        s.Fps = (int)FpsCombo.SelectedItem;
        s.BitrateKbps = double.TryParse(BitrateBox.Text.Replace(',', '.'), System.Globalization.CultureInfo.InvariantCulture, out var mbps) ? (int)(mbps * 1000) : 0;
        s.AudioBitrateKbps = (int)AudioBitrateCombo.SelectedItem;
        s.FecGroupSize = (int)FecCombo.SelectedItem;
        s.AdaptiveQuality = AdaptiveToggle.IsChecked == true;
        s.MediaPort = int.TryParse(MediaPortBox.Text, out var mp) ? mp : s.MediaPort;
        s.ControlPort = int.TryParse(ControlPortBox.Text, out var cp) ? cp : s.ControlPort;
        s.DiscoveryPort = int.TryParse(DiscoveryPortBox.Text, out var dp) ? dp : s.DiscoveryPort;
        s.UploadMbps = double.TryParse(UploadBox.Text.Replace(',', '.'), System.Globalization.CultureInfo.InvariantCulture, out var up) ? up : 0;
        s.MaxViewers = int.TryParse(MaxViewersBox.Text, out var mv) ? mv : s.MaxViewers;
        s.DeviceName = NameBox.Text.Trim();
        s.Pin = string.IsNullOrWhiteSpace(PinBox.Text) ? null : PinBox.Text.Trim();
        s.ApproveEachViewer = ApproveToggle.IsChecked == true;
        s.CheckUpdatesOnStart = UpdateToggle.IsChecked == true;
        s.Theme = ThemeCombo.SelectedIndex == 1 ? AppTheme.Light : AppTheme.Dark;

        var hardware = await AppServices.Hardware;
        var encoder = hardware.Encoders.FirstOrDefault(x => x.Label == EncoderCombo.SelectedItem as string);
        s.PreferredEncoder = encoder?.FfmpegName;
        s.PreferredCodec = encoder?.Codec;

        AppServices.Settings.Save();
        ThemeManager.Apply(s.Theme);
        SavedText.Text = s.Pin is null && !string.IsNullOrWhiteSpace(PinBox.Text) ? "Salvo. O PIN foi ignorado: use pelo menos 4 caracteres." : "Configurações salvas.";

        if (portsBefore != (s.MediaPort, s.ControlPort, s.DiscoveryPort))
        {
            s.FirewallConfigured = false;
            AppServices.Settings.Save();
            _shell.ShowBanner("As portas mudaram. Crie as regras do firewall de novo para as novas portas.", "Criar agora", () => Firewall_Click(this, new RoutedEventArgs()));
        }
    }

    private async void Firewall_Click(object sender, RoutedEventArgs e)
    {
        var s = Settings;
        var ok = await Task.Run(() => FirewallManager.CreateRules(s.MediaPort, s.ControlPort, s.DiscoveryPort));
        s.FirewallConfigured = ok;
        AppServices.Settings.Save();
        FirewallText.Text = ok ? "Regras do firewall criadas." : "As regras não foram criadas (permissão recusada?).";
    }

    private async void Update_Click(object sender, RoutedEventArgs e) => await _shell.CheckForUpdatesAsync(silent: false);

    private async Task RefreshDiagnosticsAsync()
    {
        DiagnosticsText.Text = "Carregando…";
        try { DiagnosticsText.Text = await AppServices.BuildDiagnosticsAsync(); }
        catch (Exception exception) { DiagnosticsText.Text = $"Falha ao gerar diagnóstico: {exception.Message}"; }
    }

    private async void RefreshDiagnostics_Click(object sender, RoutedEventArgs e)
    {
        await AppServices.RefreshHardware();
        await RefreshDiagnosticsAsync();
    }

    private void CopyDiagnostics_Click(object sender, RoutedEventArgs e)
    {
        Clipboard.SetText(DiagnosticsText.Text);
        _shell.SetStatus("Diagnóstico copiado.");
    }

    private void OpenLogs_Click(object sender, RoutedEventArgs e)
    {
        System.IO.Directory.CreateDirectory(Log.Directory);
        Process.Start(new ProcessStartInfo(Log.Directory) { UseShellExecute = true });
    }
}
