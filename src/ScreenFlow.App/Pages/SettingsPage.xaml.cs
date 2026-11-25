using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Shapes;
using ScreenFlow.App.Services;
using ScreenFlow.Core.Diagnostics;
using ScreenFlow.Core.Media;
using ScreenFlow.Core.Settings;
using ScreenFlow.Infrastructure.Network;

namespace ScreenFlow.App.Pages;

/// <summary>Configurações: qualidade, câmera e som, player, rede e firewall, segurança, tema e diagnóstico.</summary>
public partial class SettingsPage : UserControl, IShellPage
{
    private readonly MainWindow _shell;
    private bool _loading;

    public SettingsPage(MainWindow shell)
    {
        _shell = shell;
        InitializeComponent();
        QualityCombo.Fill(Resolution.All.Select(r => new Choice<Resolution>(r, $"{r.Label} · {r.Width} × {r.Height}")));
        FpsCombo.Fill(Quality.FrameRates.Select(f => new Choice<int>(f, $"{f} FPS")));
        FecCombo.Fill(new[] { 0, 4, 6, 8, 10, 16 }.Select(n => new Choice<int>(n, n == 0 ? "Desligada" : $"1 extra a cada {n} pacotes")));
        AudioBitrateCombo.Fill(new[] { 96, 128, 160, 192, 256 }.Select(k => new Choice<int>(k, $"{k} kbps")));
        MusicCombo.Fill(AudioProfile.MusicBitrates.Select(k => new Choice<int>(k, OverviewPage.MusicLabel(k))));
        CameraQualityCombo.Fill(OverviewPage.CameraQualities);
        ThemeCombo.Fill([new Choice<AppTheme>(AppTheme.Dark, "Escuro"), new Choice<AppTheme>(AppTheme.Light, "Claro")]);
    }

    private AppSettings Settings => AppServices.Settings.Current;

    public void OnShown() => _ = LoadAsync();

    private async Task LoadAsync()
    {
        _loading = true;
        var s = Settings;
        QualityCombo.SelectValue(Resolution.FromLabel(s.Resolution));
        FpsCombo.SelectValue(s.Fps, f => f);
        BitrateBox.Text = s.BitrateKbps > 0 ? (s.BitrateKbps / 1000.0).ToString("0.#", CultureInfo.CurrentCulture) : string.Empty;
        FecCombo.SelectValue(s.FecGroupSize, n => n);
        AdaptiveToggle.IsChecked = s.AdaptiveQuality;
        CameraQualityCombo.SelectValue((Resolution.FromLabel(s.CameraResolution), s.CameraFps));
        AudioBitrateCombo.SelectValue(s.AudioBitrateKbps, k => k);
        MusicCombo.SelectValue(s.MusicBitrateKbps, k => k);
        GpuToggle.IsChecked = s.GpuVideoRenderer;
        StatsToggle.IsChecked = s.ShowStatsOverlay;
        MediaPortBox.Text = s.MediaPort.ToString();
        ControlPortBox.Text = s.ControlPort.ToString();
        DiscoveryPortBox.Text = s.DiscoveryPort.ToString();
        UploadBox.Text = s.UploadMbps > 0 ? s.UploadMbps.ToString("0", CultureInfo.CurrentCulture) : string.Empty;
        MaxViewersBox.Text = s.MaxViewers.ToString();
        NameBox.Text = s.DeviceName;
        PinBox.Text = s.Pin ?? string.Empty;
        ApproveToggle.IsChecked = s.ApproveEachViewer;
        ThemeCombo.SelectValue(ThemeManager.Current);
        SavedText.Text = string.Empty;
        AutoResult.Text = s.LastAutoConfig is { } when
            ? $"Feita em {when.ToLocalTime():dd/MM/yyyy 'às' HH:mm}: {s.Resolution} a {s.Fps} FPS, encoder {s.PreferredEncoder ?? "automático"}."
            : "Detecta a GPU, os codecs, o monitor e a rede e escolhe a melhor qualidade para este PC.";

        var hardware = await AppServices.Hardware;
        var encoders = new List<Choice<string?>> { new(null, "Automático (o melhor disponível)") };
        encoders.AddRange(hardware.Encoders.Select(e => new Choice<string?>(e.FfmpegName, e.Label)));
        EncoderCombo.Fill(encoders);
        EncoderCombo.SelectValue(hardware.Encoders.Any(e => e.FfmpegName == s.PreferredEncoder) ? s.PreferredEncoder : null);
        _loading = false;

        await RefreshFirewallAsync();
        await RefreshDiagnosticsAsync();
    }

    // ───────────────────────── Salvar ─────────────────────────

    private void Save_Click(object sender, RoutedEventArgs e) => _ = SaveAsync();

    private async Task SaveAsync()
    {
        if (_loading) return;
        var s = Settings;
        var portsBefore = (s.MediaPort, s.ControlPort, s.DiscoveryPort);
        var pinText = PinBox.Text.Trim();
        if (pinText.Length is > 0 and < 4)
        {
            SavedText.Text = string.Empty;
            _shell.Toast("O PIN precisa ter pelo menos 4 caracteres (ou fique vazio).", ToastKind.Warning);
            PinBox.Focus();
            return;
        }

        if (QualityCombo.Value<Resolution>() is { } resolution) s.Resolution = resolution.Label;
        if (FpsCombo.SelectedItem is Choice<int> fps) s.Fps = fps.Value;
        s.BitrateKbps = TryNumber(BitrateBox.Text, out var mbps) ? (int)(mbps * 1000) : 0;
        if (FecCombo.SelectedItem is Choice<int> fec) s.FecGroupSize = fec.Value;
        s.AdaptiveQuality = AdaptiveToggle.IsChecked == true;
        if (CameraQualityCombo.SelectedItem is Choice<(Resolution Resolution, int Fps)> camera)
        {
            s.CameraResolution = camera.Value.Resolution.Label;
            s.CameraFps = camera.Value.Fps;
        }
        if (AudioBitrateCombo.SelectedItem is Choice<int> audio) s.AudioBitrateKbps = audio.Value;
        if (MusicCombo.SelectedItem is Choice<int> music) s.MusicBitrateKbps = music.Value;
        s.GpuVideoRenderer = GpuToggle.IsChecked == true;
        s.ShowStatsOverlay = StatsToggle.IsChecked == true;
        s.MediaPort = int.TryParse(MediaPortBox.Text, out var mp) ? mp : s.MediaPort;
        s.ControlPort = int.TryParse(ControlPortBox.Text, out var cp) ? cp : s.ControlPort;
        s.DiscoveryPort = int.TryParse(DiscoveryPortBox.Text, out var dp) ? dp : s.DiscoveryPort;
        s.UploadMbps = TryNumber(UploadBox.Text, out var up) ? up : 0;
        s.MaxViewers = int.TryParse(MaxViewersBox.Text, out var mv) ? mv : s.MaxViewers;
        s.DeviceName = NameBox.Text.Trim();
        s.Pin = pinText.Length == 0 ? null : pinText;
        s.ApproveEachViewer = ApproveToggle.IsChecked == true;
        var theme = ThemeCombo.Value<AppTheme>();
        s.Theme = theme;

        var hardware = await AppServices.Hardware;
        var encoder = hardware.Encoders.FirstOrDefault(x => x.FfmpegName == EncoderCombo.Value<string?>());
        s.PreferredEncoder = encoder?.FfmpegName;
        s.PreferredCodec = encoder?.Codec;

        AppServices.Settings.Save();
        if (theme != ThemeManager.Current) ThemeManager.Apply(theme);
        SavedText.Text = $"Salvo às {DateTime.Now:HH:mm}";
        _shell.Toast(_shell.IsTransmitting ? "Configurações salvas. As mudanças de qualidade valem na próxima transmissão." : "Configurações salvas.", ToastKind.Success);
        _ = LoadAsync();

        if (portsBefore != (s.MediaPort, s.ControlPort, s.DiscoveryPort))
        {
            await _shell.CheckFirewallAsync();
            if (!s.FirewallConfigured)
                _shell.Toast("As portas mudaram. Libere as novas portas no firewall para quem assiste conseguir entrar.", ToastKind.Warning, "Liberar portas", () => _ = OpenFirewallAsync());
        }
    }

    private static bool TryNumber(string text, out double value)
        => double.TryParse(text.Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out value) && value > 0;

    // ───────────────────────── Automático ─────────────────────────

    private async void Auto_Click(object sender, RoutedEventArgs e)
    {
        AutoButton.IsEnabled = false;
        AutoResult.Text = "Detectando GPU, codecs, monitor e rede…";
        try
        {
            var result = await AppServices.AutoConfigureAsync(refresh: true);
            await LoadAsync();
            AutoResult.Text = $"Pronto: {result.Resolution.Label} a {result.Fps} FPS em {Quality.CodecLabel(result.Codec)} ({result.Encoder}), cerca de {result.BitrateKbps / 1000.0:0} Mbps por pessoa."
                              + (result.Notes.Count > 0 ? " " + string.Join(" ", result.Notes) : string.Empty);
            _shell.Toast("Configuração automática concluída.", ToastKind.Success);
        }
        catch (Exception exception)
        {
            AutoResult.Text = $"Não foi possível configurar automaticamente: {exception.Message}";
        }
        finally { AutoButton.IsEnabled = true; }
    }

    // ───────────────────────── Firewall ─────────────────────────

    private async Task RefreshFirewallAsync()
    {
        var s = Settings;
        var ok = AppServices.DemoMode || await Task.Run(() => FirewallManager.RulesExist(s.MediaPort, s.ControlPort, s.DiscoveryPort));
        FirewallText.Text = ok
            ? $"Portas {s.MediaPort}, {s.ControlPort} e {s.DiscoveryPort} liberadas no firewall."
            : "As portas estão fechadas no firewall: quem está na sua rede pode não conseguir assistir.";
        FirewallDot.SetResourceReference(Shape.FillProperty, ok ? "Success" : "Warning");
        FirewallButton.Visibility = ok ? Visibility.Collapsed : Visibility.Visible;
    }

    private void Firewall_Click(object sender, RoutedEventArgs e) => _ = OpenFirewallAsync();

    private async Task OpenFirewallAsync()
    {
        FirewallButton.IsEnabled = false;
        try
        {
            await _shell.OpenFirewallAsync();
            await RefreshFirewallAsync();
        }
        finally { FirewallButton.IsEnabled = true; }
    }

    // ───────────────────────── Diagnóstico ─────────────────────────

    private async Task RefreshDiagnosticsAsync()
    {
        DiagnosticsText.Text = "Carregando…";
        try { DiagnosticsText.Text = await AppServices.BuildDiagnosticsAsync(); }
        catch (Exception exception) { DiagnosticsText.Text = $"Falha ao gerar o diagnóstico: {exception.Message}"; }
    }

    private async void RefreshDiagnostics_Click(object sender, RoutedEventArgs e)
    {
        await AppServices.RefreshHardware();
        await RefreshDiagnosticsAsync();
    }

    private void CopyDiagnostics_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(DiagnosticsText.Text);
            _shell.Toast("Diagnóstico copiado. Cole na conversa com quem está ajudando.", ToastKind.Success);
        }
        catch (Exception exception) { Log.Warn($"Área de transferência ocupada: {exception.Message}"); }
    }

    private void OpenLogs_Click(object sender, RoutedEventArgs e)
    {
        System.IO.Directory.CreateDirectory(Log.Directory);
        try { Process.Start(new ProcessStartInfo(Log.Directory) { UseShellExecute = true }); }
        catch (Exception exception) { Log.Warn($"Não foi possível abrir a pasta de logs: {exception.Message}"); }
    }
}
