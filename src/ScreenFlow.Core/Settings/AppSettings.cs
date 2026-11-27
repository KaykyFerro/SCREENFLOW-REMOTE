using System.Text.Json;
using System.Text.Json.Serialization;
using ScreenFlow.Core.Diagnostics;
using ScreenFlow.Core.Media;
using ScreenFlow.Core.Room;
using ScreenFlow.Core.Security;

namespace ScreenFlow.Core.Settings;

public enum AppTheme { Dark, Light }

/// <summary>
/// (1.4.1) Como este PC usa um servidor ScreenFlow: nenhum (a sala é direta entre os PCs da rede), entrar num servidor
/// de outro PC, ou ser o servidor (ele roda dentro do app, e o próprio app entra nele).
/// </summary>
public enum ServerRole { None, Join, Host }

public sealed class AppSettings
{
    public AppTheme Theme { get; set; } = AppTheme.Dark;
    /// <summary>(1.4.1) A cor do tema (id de <see cref="ColorThemes"/>), só deste PC. Null: a menta de sempre.</summary>
    public string? ColorTheme { get; set; }
    /// <summary>Seu nome: aparece no chat, na voz e para quem assiste (no começo é o nome do PC).</summary>
    public string DeviceName { get; set; } = Environment.MachineName;
    /// <summary>A sua foto do perfil (id do arquivo guardado na pasta de imagens). Null: aparece a inicial do nome.</summary>
    public string? AvatarId { get; set; }

    // Rede
    public int MediaPort { get; set; } = AppInfo.DefaultMediaPort;
    public int ControlPort { get; set; } = AppInfo.DefaultControlPort;
    public int DiscoveryPort { get; set; } = AppInfo.DefaultDiscoveryPort;
    /// <summary>(1.5.0) Porta TCP dos arquivos do chat (cada mensagem leva a porta de quem mandou).</summary>
    public int FilePort { get; set; } = AppInfo.DefaultFilePort;
    public bool FirewallConfigured { get; set; }
    // (1.4.0) "AllowNonRadminFallback" saiu: nunca teve tela nem uso. Configurações antigas que ainda têm a chave abrem
    // normalmente (chaves desconhecidas são ignoradas) e ela some no próximo salvamento. Na 1.4.1, o Tailscale e o
    // servidor ScreenFlow (docs/SERVIDOR.md) entraram ao lado do Radmin VPN.

    // Transmissão
    /// <summary>O que transmitir: tela (monitor ou janela), câmera ou só o áudio (festa de música).</summary>
    public StreamMode Mode { get; set; } = StreamMode.Screen;
    public string Resolution { get; set; } = Media.Resolution.P1080.Label;
    public int Fps { get; set; } = 60;
    public int BitrateKbps { get; set; } // 0 = automático pela tabela de perfis
    public VideoCodec? PreferredCodec { get; set; }
    public string? PreferredEncoder { get; set; }
    public bool AudioEnabled { get; set; } = true;
    public int AudioBitrateKbps { get; set; } = 160;
    public bool AdaptiveQuality { get; set; } = true;
    public int FecGroupSize { get; set; } = 10;

    // Câmera e festa de música
    public string? CameraId { get; set; }
    public string CameraResolution { get; set; } = Media.Resolution.P1080.Label;
    public int CameraFps { get; set; } = 30;
    /// <summary>Bitrate do Opus no modo festa de música (só áudio, qualidade de música).</summary>
    public int MusicBitrateKbps { get; set; } = 256;
    /// <summary>No modo tela, manda a câmera junto (quem assiste vê num quadrinho por cima da tela).</summary>
    public bool CameraWithScreen { get; set; }

    // O que a captura mostra (1.4.0)
    /// <summary>Mostra o cursor do mouse na transmissão (no DXGI, a captura de reserva, ele nunca aparece).</summary>
    public bool CaptureShowCursor { get; set; } = true;
    /// <summary>Só esta parte da tela ou da janela vai para quem assiste. Null = tudo.</summary>
    public RelativeRect? CaptureRegion { get; set; }
    /// <summary>Tapa de preto o canto onde o Windows mostra as notificações.</summary>
    public bool MaskNotifications { get; set; }
    /// <summary>Outras áreas tapadas de preto (frações da tela ou da janela).</summary>
    public List<RelativeRect> CaptureMasks { get; set; } = [];

    /// <summary>O cursor, a região e as áreas tapadas para a captura.</summary>
    public CaptureArea CaptureAreaNow()
    {
        var masks = new List<RelativeRect>();
        if (MaskNotifications) masks.Add(RelativeRect.Notifications);
        masks.AddRange(CaptureMasks ?? []);
        return new CaptureArea(CaptureShowCursor, CaptureRegion, masks.Take(CaptureArea.MaxMasks).ToList());
    }

    // Som do PC
    /// <summary>
    /// Separa o som do PC por app (Windows 10 2004 ou mais novo): a voz da rede, os sons do ScreenFlow e os apps de
    /// <see cref="MutedApps"/> ficam fora da transmissão. Desligado, vai todo o som do PC, como nas versões antigas.
    /// </summary>
    public bool SeparateAppAudio { get; set; } = true;
    /// <summary>Apps cujo som não vai na transmissão (Spotify, Discord...).</summary>
    public List<MutedApp> MutedApps { get; set; } = [];
    /// <summary>Festa de música com o som de um app só (o Spotify, por exemplo). Null: todo o som do PC.</summary>
    public MutedApp? PartyApp { get; set; }
    /// <summary>
    /// Desfaz o volume geral do PC de quem transmite no som que vai para quem assiste (1.3.0): cada um controla só o
    /// próprio volume. Desligado, quem assiste ouve o som como ele sai na caixa de quem transmite.
    /// </summary>
    public bool KeepStreamVolume { get; set; } = true;

    // Segurança
    /// <summary>O PIN para assistir. Gravado cifrado (1.5.0), em <see cref="PinProtected"/>.</summary>
    [JsonIgnore] public string? Pin { get; set; }
    /// <summary>
    /// Quem transmite aceita cada pessoa antes de ela ver a tela. Desde a 1.3.0 vem desligado: quem está na rede entra
    /// direto (as configurações antigas são ajustadas uma vez, em <see cref="Migrate"/>).
    /// </summary>
    public bool ApproveEachViewer { get; set; }
    public int MaxViewers { get; set; } = 20;
    /// <summary>
    /// Quem assiste (1.2.0 ou mais nova) pode trocar a qualidade, pausar e pular a música e tirar apps do som, pelo
    /// player. Vale para a transmissão inteira; dá para desligar com ela no ar.
    /// </summary>
    public bool ViewerControls { get; set; } = true;

    /// <summary>Upload do host em Mbps (medido pelo teste de banda ou digitado). 0 = desconhecido.</summary>
    public double UploadMbps { get; set; }

    // Viewer
    public List<string> RecentHosts { get; set; } = [];
    public double Volume { get; set; } = 0.8;
    public bool ShowStatsOverlay { get; set; }
    /// <summary>Desenha o vídeo direto na GPU (D3D11). Desligado, usa o caminho antigo pela CPU.</summary>
    public bool GpuVideoRenderer { get; set; } = true;
    /// <summary>Canto do quadrinho da câmera no player: 0 em cima à direita, 1 embaixo à direita, 2 embaixo à esquerda.</summary>
    public int PlayerCameraCorner { get; set; }
    /// <summary>
    /// O player aparece no Windows como um player de música (1.3.0): nos controles de mídia, nas teclas de mídia do
    /// teclado e nos apps que mostram o que está tocando.
    /// </summary>
    public bool SystemMediaControls { get; set; } = true;

    // Bandeja
    /// <summary>Fechar (X) leva o app para a bandeja (perto do relógio) em vez de fechar. Minimizar fica na barra de tarefas (1.5.0).</summary>
    public bool MinimizeToTray { get; set; } = true;
    public bool TrayHintShown { get; set; }
    /// <summary>Quando a transmissão acaba (ou o player fecha) com o ScreenFlow na bandeja, a janela volta sozinha.</summary>
    public bool ShowWindowWhenSessionEnds { get; set; } = true;

    // Sala da rede: voz e chat com todo mundo que está na mesma rede
    /// <summary>Identifica este PC na sala da rede. Gerado na primeira vez que o app abre.</summary>
    public string? InstanceId { get; set; }
    /// <summary>Microfone da voz (id do Windows). Null usa o microfone padrão.</summary>
    public string? MicrophoneId { get; set; }
    public bool VoiceMuted { get; set; }
    public bool VoiceSounds { get; set; } = true;
    /// <summary>(1.4.0) Saída de som da voz da sala (fone, caixa). Null = a padrão do Windows.</summary>
    public string? SpeakerId { get; set; }
    /// <summary>(1.4.0) Apertar para falar: a voz só vai enquanto <see cref="PushToTalkKey"/> está apertada, em qualquer app.</summary>
    public bool PushToTalk { get; set; }
    /// <summary>(1.4.0) A tecla (nome da tecla do WPF, como "F8") ou o botão lateral do mouse ("XButton1", "XButton2").</summary>
    public string PushToTalkKey { get; set; } = "F8";
    /// <summary>Sensibilidade do microfone: 0 capta só voz alta, 1 capta tudo.</summary>
    public double VoiceSensitivity { get; set; } = 0.6;
    /// <summary>(1.5.0) Volume do microfone em dB (−12 a +24), aplicado antes do portão de voz e do ajuste automático.</summary>
    public double MicGainDb { get; set; }
    /// <summary>(1.5.0) Ajuste automático do volume do microfone: leva a fala ao nível de uma chamada, sem estourar.</summary>
    public bool VoiceAutoGain { get; set; } = true;
    /// <summary>(1.5.0) Iguala o volume de quem fala baixo (ou ainda está numa versão antiga) com o de quem fala no nível certo.</summary>
    public bool VoiceLeveler { get; set; } = true;
    /// <summary>(1.5.0) Som curto quando chega mensagem no chat ou numa conversa privada (nos Sons do aplicativo).</summary>
    public bool MessageSounds { get; set; } = true;

    /// <summary>
    /// (1.5.0) O mixer: o volume da voz (VOIP), o das transmissões e o dos sons do app, cada um de 0 a 100% e com o seu
    /// mudo. Só mexe no som do ScreenFlow (nunca no volume do Windows) e um não muda os outros.
    /// </summary>
    public AudioMixerSettings Mixer { get; set; } = new();
    /// <summary>IPs adicionados à mão na sala (para redes em que o aviso automático não chega).</summary>
    public List<string> RoomPeers { get; set; } = [];
    /// <summary>Canais que você criou (1.3.0): aparecem para todo mundo da sala enquanto você estiver nela.</summary>
    public List<RoomChannel> MyChannels { get; set; } = [];
    /// <summary>O último canal de texto aberto e o último canal de voz (o botão Entrar volta para ele).</summary>
    public string? LastTextChannel { get; set; }
    public string? LastVoiceChannel { get; set; }

    /// <summary>(1.5.0) O servidor por rede aberto da última vez (null: a sala da rede).</summary>
    public Guid? LastServerId { get; set; }
    /// <summary>Volume de cada pessoa na voz, só para você (id da pessoa → 0 a 2; sem a pessoa = 1).</summary>
    public Dictionary<string, double> PeerVolumes { get; set; } = [];

    // Sala privada e moderação (1.4.0). Independentes do PIN e da aprovação para assistir à transmissão.
    /// <summary>O convite da sala privada em uso (o código "SF1-…"). Null: sala aberta. Gravado cifrado (1.5.0).</summary>
    [JsonIgnore] public string? PrivateRoomCode { get; set; }
    /// <summary>Só para o dono: a sala é só para quem ele aprovou (quem tem o convite e não foi aprovado espera).</summary>
    public bool PrivateRoomApprovedOnly { get; set; }
    /// <summary>Só para o dono: quem ele aprovou (ids da sala).</summary>
    public List<string> PrivateRoomApproved { get; set; } = [];
    /// <summary>Quem você bloqueou: nada dessas pessoas chega até você (ids da sala).</summary>
    public List<string> BlockedPeers { get; set; } = [];

    // Servidor ScreenFlow (1.4.1)
    /// <summary>
    /// Entrar num servidor ScreenFlow (ou ser um): a sala (chat, voz e canais) passa por ele, e as transmissões também
    /// (quem está no servidor assiste por ele). <see cref="ServerRole.None"/>: a sala direta entre os PCs da rede.
    /// </summary>
    public ServerRole ServerRole { get; set; }
    /// <summary>
    /// Endereço do servidor para entrar: o IP (do Tailscale, do Radmin VPN ou da rede local) ou o nome do PC no
    /// Tailscale. Com ":porta" no fim quando o servidor usa outras portas (a do servidor e as duas seguintes).
    /// </summary>
    public string? ServerAddress { get; set; }
    /// <summary>A senha do servidor em que você entra (se ele tiver uma). No lugar do PIN de quem transmite. Gravada cifrada (1.5.0).</summary>
    [JsonIgnore] public string? ServerPassword { get; set; }
    /// <summary>No servidor, a sua transmissão vai para ele e quem está nele assiste por ele (desligado: direto, como antes).</summary>
    public bool TransmitViaServer { get; set; } = true;
    /// <summary>O nome do servidor quando este PC é o servidor (vazio: o nome do PC).</summary>
    public string? HostedServerName { get; set; }
    /// <summary>A senha do servidor quando este PC é o servidor (vazio: sem senha, entra quem alcançar o PC). Gravada cifrada (1.5.0).</summary>
    [JsonIgnore] public string? HostedServerPassword { get; set; }

    // (1.5.0) Os segredos vão cifrados para o arquivo (SecretProtection: no Windows, o DPAPI da sua conta). Até a 1.4.1
    // iam em texto puro com o próprio nome ("Pin", "ServerPassword"...): esses nomes ainda são lidos, uma vez, na
    // atualização, e nunca mais gravados.
    [JsonInclude, JsonPropertyName("PinProtected")]
    private string? PinProtected { get => SecretProtection.Protect(Pin); set => Pin = SecretProtection.Unprotect(value) ?? Pin; }
    [JsonInclude, JsonPropertyName("PrivateRoomCodeProtected")]
    private string? PrivateRoomCodeProtected { get => SecretProtection.Protect(PrivateRoomCode); set => PrivateRoomCode = SecretProtection.Unprotect(value) ?? PrivateRoomCode; }
    [JsonInclude, JsonPropertyName("ServerPasswordProtected")]
    private string? ServerPasswordProtected { get => SecretProtection.Protect(ServerPassword); set => ServerPassword = SecretProtection.Unprotect(value) ?? ServerPassword; }
    [JsonInclude, JsonPropertyName("HostedServerPasswordProtected")]
    private string? HostedServerPasswordProtected { get => SecretProtection.Protect(HostedServerPassword); set => HostedServerPassword = SecretProtection.Unprotect(value) ?? HostedServerPassword; }
    /// <summary>
    /// (1.5.0) A chave privada do chat privado deste PC (P-256): é ela que abre as mensagens diretas. Gravada cifrada; se
    /// não abrir (outro PC ou outra conta do Windows), o PC passa a ter uma chave nova e as pessoas veem o aviso de troca.
    /// </summary>
    [JsonIgnore] public byte[]? DirectIdentityKey { get; set; }
    /// <summary>(1.5.0) A chave que cifra as conversas privadas guardadas neste PC (32 bytes). Gravada cifrada.</summary>
    [JsonIgnore] public byte[]? DirectHistoryKey { get; set; }
    /// <summary>(1.5.0) A chave que cifra a lista de arquivos mandados (com as chaves dos anexos) e os downloads parados.</summary>
    [JsonIgnore] public byte[]? FilesKey { get; set; }
    [JsonInclude, JsonPropertyName("FilesKeyProtected")]
    private string? FilesKeyProtected { get => ProtectBytes(FilesKey); set => FilesKey = UnprotectBytes(value) ?? FilesKey; }
    [JsonInclude, JsonPropertyName("DirectIdentityProtected")]
    private string? DirectIdentityProtected { get => ProtectBytes(DirectIdentityKey); set => DirectIdentityKey = UnprotectBytes(value) ?? DirectIdentityKey; }
    [JsonInclude, JsonPropertyName("DirectHistoryKeyProtected")]
    private string? DirectHistoryKeyProtected { get => ProtectBytes(DirectHistoryKey); set => DirectHistoryKey = UnprotectBytes(value) ?? DirectHistoryKey; }

    private static string? ProtectBytes(byte[]? bytes) => bytes is { Length: > 0 } ? SecretProtection.Protect(Convert.ToBase64String(bytes)) : null;

    private static byte[]? UnprotectBytes(string? stored)
    {
        if (SecretProtection.Unprotect(stored) is not { } text) return null;
        try { return Convert.FromBase64String(text); }
        catch (FormatException) { return null; }
    }

    [JsonInclude, JsonPropertyName("Pin"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    private string? LegacyPin { get => null; set => Pin ??= value; }
    [JsonInclude, JsonPropertyName("PrivateRoomCode"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    private string? LegacyPrivateRoomCode { get => null; set => PrivateRoomCode ??= value; }
    [JsonInclude, JsonPropertyName("ServerPassword"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    private string? LegacyServerPassword { get => null; set => ServerPassword ??= value; }
    [JsonInclude, JsonPropertyName("HostedServerPassword"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    private string? LegacyHostedServerPassword { get => null; set => HostedServerPassword ??= value; }
    /// <summary>O "volume de quem fala" da 1.4.1 (0 a 1,5): vira o volume da voz no mixer, uma vez (<see cref="Migrate"/>).</summary>
    [JsonInclude, JsonPropertyName("VoiceVolume"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    private double? LegacyVoiceVolume { get => null; set => _legacyVoiceVolume = value; }
    private double? _legacyVoiceVolume;

    /// <summary>O endereço do servidor configurado, separado em nome e porta (null sem endereço válido).</summary>
    public (string Host, int Port)? ServerEndpoint()
        => Network.LocalNetworks.TrySplitHostPort(ServerAddress, AppInfo.DefaultServerControlPort, out var host, out var port) ? (host, port) : null;

    // Chat guardado numa pasta (1.3.0)
    /// <summary>
    /// Guarda as mensagens do chat (e as imagens) numa pasta, por exemplo a do Google Drive para computador: o Drive
    /// manda para a nuvem sozinho. Ligado, o chat também volta quando o ScreenFlow abre de novo.
    /// </summary>
    public bool ChatArchiveEnabled { get; set; }
    /// <summary>A pasta do chat guardado. Null: a pasta do Google Drive achada no PC.</summary>
    public string? ChatArchiveFolder { get; set; }

    /// <summary>(1.5.0) A última pasta onde você salvou um arquivo do chat (a janela de salvar começa nela).</summary>
    public string? FileSaveFolder { get; set; }

    /// <summary>De que versão são estas configurações (para ajustar padrões antigos uma vez só).</summary>
    public int SettingsVersion { get; set; }

    /// <summary>A versão atual das configurações: 2 é a 1.3.0, 3 é a 1.5.0 (o mixer).</summary>
    public const int CurrentSettingsVersion = 3;

    // Configuração automática e atualização
    public bool AutoConfigured { get; set; }
    public DateTimeOffset? LastAutoConfig { get; set; }
    public bool CheckUpdatesOnStart { get; set; } = true;
    public string? SkippedVersion { get; set; }
    public DateTimeOffset? LastUpdateCheck { get; set; }

    public void RememberHost(string host)
    {
        host = host.Trim();
        if (host.Length == 0) return;
        RecentHosts.RemoveAll(h => string.Equals(h, host, StringComparison.OrdinalIgnoreCase));
        RecentHosts.Insert(0, host);
        if (RecentHosts.Count > 8) RecentHosts.RemoveRange(8, RecentHosts.Count - 8);
    }

    public void Normalize()
    {
        MediaPort = ValidPort(MediaPort, AppInfo.DefaultMediaPort);
        ControlPort = ValidPort(ControlPort, AppInfo.DefaultControlPort);
        DiscoveryPort = ValidPort(DiscoveryPort, AppInfo.DefaultDiscoveryPort);
        FilePort = ValidPort(FilePort, AppInfo.DefaultFilePort);
        if (!Quality.FrameRates.Contains(Fps)) Fps = 60;
        if (!Enum.IsDefined(Mode)) Mode = StreamMode.Screen;
        Resolution = Media.Resolution.FromLabel(Resolution).Label;
        CameraResolution = Media.Resolution.FromLabel(CameraResolution).Label;
        if (!Quality.CameraFrameRates.Contains(CameraFps)) CameraFps = 30;
        MusicBitrateKbps = Math.Clamp(MusicBitrateKbps, AudioProfile.MinMusicKbps, AudioProfile.MaxMusicKbps);
        if (string.IsNullOrWhiteSpace(CameraId)) CameraId = null;
        if (PlayerCameraCorner is < 0 or > 2) PlayerCameraCorner = 0;
        BitrateKbps = BitrateKbps <= 0 ? 0 : Math.Clamp(BitrateKbps, 1_000, 200_000);
        AudioBitrateKbps = Math.Clamp(AudioBitrateKbps, 64, 256);
        FecGroupSize = Math.Clamp(FecGroupSize, 0, 32);
        MaxViewers = Math.Clamp(MaxViewers, 1, 64);
        UploadMbps = Math.Clamp(UploadMbps, 0, 100_000);
        Volume = Math.Clamp(Volume, 0, 1);
        if (!Security.SessionCrypto.IsValidPin(Pin)) Pin = null;
        DeviceName = (DeviceName ?? string.Empty).Trim();
        if (DeviceName.Length > Room.RoomPacket.MaxNameLength) DeviceName = DeviceName[..Room.RoomPacket.MaxNameLength].TrimEnd();
        if (DeviceName.Length == 0) DeviceName = Environment.MachineName;
        if (!Room.RoomBlob.IsValidId(AvatarId)) AvatarId = null;
        RecentHosts ??= [];
        if (InstanceId is null || !Guid.TryParseExact(InstanceId, "N", out _)) InstanceId = Guid.NewGuid().ToString("N");
        if (string.IsNullOrWhiteSpace(MicrophoneId)) MicrophoneId = null;
        if (string.IsNullOrWhiteSpace(SpeakerId)) SpeakerId = null;
        if (string.IsNullOrWhiteSpace(PushToTalkKey) || PushToTalkKey.Length > 32) PushToTalkKey = "F8";
        CaptureRegion = CaptureRegion?.Clean();
        CaptureMasks = (CaptureMasks ?? []).Where(m => m is not null).Select(m => m.Clean()).OfType<RelativeRect>().Take(CaptureArea.MaxMasks - 1).ToList();
        VoiceSensitivity = Math.Clamp(VoiceSensitivity, 0, 1);
        MicGainDb = Math.Clamp(double.IsFinite(MicGainDb) ? Math.Round(MicGainDb, 1) : 0, VoiceProcessor.MinManualGainDb, VoiceProcessor.MaxManualGainDb);
        Mixer ??= new AudioMixerSettings();
        Mixer.Normalize();
        if (!Room.RoomInvite.TryParse(PrivateRoomCode, out _)) PrivateRoomCode = null;
        PrivateRoomApproved = ValidIds(PrivateRoomApproved, Room.RoomPacket.MaxRoster);
        BlockedPeers = ValidIds(BlockedPeers, 200);
        RoomPeers = (RoomPeers ?? []).Select(p => p.Trim()).Where(p => p.Length is > 0 and <= 64).Distinct(StringComparer.OrdinalIgnoreCase).Take(16).ToList();
        MutedApps = MutedApp.Normalize(MutedApps);
        PartyApp = MutedApp.Normalize([PartyApp]).FirstOrDefault();
        MyChannels = RoomChannels.Normalize(MyChannels);
        LastTextChannel = RoomChannels.CleanName(RoomChannelKind.Text, LastTextChannel) is { Length: > 0 } text ? text : null;
        LastVoiceChannel = RoomChannels.CleanName(RoomChannelKind.Voice, LastVoiceChannel) is { Length: > 0 } voice ? voice : null;
        PeerVolumes = (PeerVolumes ?? [])
            .Where(p => Guid.TryParseExact(p.Key, "N", out _) && double.IsFinite(p.Value) && Math.Abs(p.Value - 1) > 0.001)
            .Take(256)
            .ToDictionary(p => p.Key, p => Math.Clamp(p.Value, 0, 2));
        if (!Enum.IsDefined(ServerRole)) ServerRole = ServerRole.None;
        ServerAddress = string.IsNullOrWhiteSpace(ServerAddress) || ServerAddress.Trim().Length > 255 ? null : ServerAddress.Trim();
        ServerPassword = CleanPassword(ServerPassword);
        HostedServerPassword = CleanPassword(HostedServerPassword);
        HostedServerName = RoomPacket.CleanName(HostedServerName) is { Length: > 0 } serverName ? serverName : null;
        ColorTheme = ColorThemes.Find(ColorTheme)?.Id;
        if (ColorTheme == ColorThemes.Default.Id) ColorTheme = null;
        if (string.IsNullOrWhiteSpace(ChatArchiveFolder)) ChatArchiveFolder = null;
        else
        {
            ChatArchiveFolder = ChatArchiveFolder.Trim();
            if (ChatArchiveFolder.Length > 260 || !Path.IsPathFullyQualified(ChatArchiveFolder)) ChatArchiveFolder = null;
        }
        if (FileSaveFolder is not null && (FileSaveFolder.Length > 260 || !Path.IsPathFullyQualified(FileSaveFolder))) FileSaveFolder = null;
    }

    /// <summary>
    /// Ajusta, uma vez, configurações salvas por versões antigas: na 1.3.0 entrar na transmissão deixou de pedir a
    /// aprovação de quem transmite (o padrão antigo era pedir, então quase todo mundo estava com ele ligado sem escolher);
    /// na 1.5.0 o "volume de quem fala" virou o volume da voz no mixer (na curva nova, soa igual).
    /// </summary>
    public bool Migrate()
    {
        if (SettingsVersion >= CurrentSettingsVersion) return false;
        if (SettingsVersion < 2) ApproveEachViewer = false;
        if (SettingsVersion < 3 && _legacyVoiceVolume is { } voiceVolume && double.IsFinite(voiceVolume))
        {
            Mixer ??= new AudioMixerSettings();
            Mixer.VoipPercent = MixerCurve.PercentFor(voiceVolume);
        }
        _legacyVoiceVolume = null;
        SettingsVersion = CurrentSettingsVersion;
        return true;
    }

    /// <summary>Senha vazia é sem senha; uma senha longa demais (colada errado) também não vale.</summary>
    private static string? CleanPassword(string? password)
        => string.IsNullOrEmpty(password) || password.Length > Server.ServerCommandLine.MaxPasswordLength ? null : password;

    private static List<string> ValidIds(List<string>? ids, int max)
        => (ids ?? []).Where(id => Guid.TryParseExact(id, "N", out var guid) && guid != Guid.Empty).Distinct(StringComparer.OrdinalIgnoreCase).Take(max).ToList();

    private static int ValidPort(int port, int fallback) => port is >= 1024 and <= 65535 ? port : fallback;
}

/// <summary>
/// (1.5.0) Os três volumes do mixer. Cada um é um ganho próprio no som do ScreenFlow, misturado só na saída: a voz da
/// sala (VOIP), o som das transmissões que você assiste e os sons do app (avisos, mensagens de voz, figurinhas).
/// </summary>
public sealed class AudioMixerSettings
{
    public int VoipPercent { get; set; } = 100;
    public bool VoipMuted { get; set; }
    public int StreamsPercent { get; set; } = 100;
    public bool StreamsMuted { get; set; }
    public int AppSoundsPercent { get; set; } = 80;
    public bool AppSoundsMuted { get; set; }

    public void Normalize()
    {
        VoipPercent = Math.Clamp(VoipPercent, 0, 100);
        StreamsPercent = Math.Clamp(StreamsPercent, 0, 100);
        AppSoundsPercent = Math.Clamp(AppSoundsPercent, 0, 100);
    }

    public AudioMixerSettings Copy() => (AudioMixerSettings)MemberwiseClone();
}

public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _path;
    private readonly object _gate = new();

    public SettingsStore(string? path = null)
    {
        _path = path ?? Path.Combine(AppInfo.DataDirectory, "settings.json");
        Current = Load();
        IsFirstRun = !File.Exists(_path);
    }

    public AppSettings Current { get; private set; }
    public bool IsFirstRun { get; }

    /// <summary>
    /// (1.4.0) O arquivo de configurações não abriu (JSON quebrado, por exemplo) e foi copiado para cá antes do app usar
    /// os padrões, para nada se perder no próximo salvamento. Null quando abriu normalmente.
    /// </summary>
    public string? RecoveredCopy { get; private set; }

    /// <summary>Copia o arquivo que não abriu para "settings.invalido-AAAAMMDD-HHMMSS.json", ao lado dele.</summary>
    private string? PreserveInvalid()
    {
        try
        {
            if (!File.Exists(_path)) return null;
            var folder = Path.GetDirectoryName(_path) ?? ".";
            var name = Path.GetFileNameWithoutExtension(_path);
            var copy = Path.Combine(folder, $"{name}.invalido-{DateTime.Now:yyyyMMdd-HHmmss}.json");
            for (var n = 2; File.Exists(copy); n++) copy = Path.Combine(folder, $"{name}.invalido-{DateTime.Now:yyyyMMdd-HHmmss}-{n}.json");
            File.Copy(_path, copy);
            return copy;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"Não deu para guardar a cópia das configurações inválidas: {exception.Message}");
            return null;
        }
    }
    public string FilePath => _path;

    public event Action<AppSettings>? Saved;

    private AppSettings Load()
    {
        try
        {
            if (File.Exists(_path))
            {
                var loaded = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(_path), Options) ?? new AppSettings();
                if (loaded.Migrate()) Log.Info($"Configurações atualizadas para a versão {AppSettings.CurrentSettingsVersion}.");
                loaded.Normalize();
                return loaded;
            }
        }
        catch (Exception exception)
        {
            RecoveredCopy = PreserveInvalid();
            Log.Warn($"Configurações inválidas, usando o padrão: {exception.Message}" +
                     (RecoveredCopy is null ? string.Empty : $" O arquivo antigo foi guardado em {RecoveredCopy}."));
        }
        var settings = new AppSettings { SettingsVersion = AppSettings.CurrentSettingsVersion };
        settings.Normalize();
        return settings;
    }

    public void Save()
    {
        lock (_gate)
        {
            Current.Normalize();
            var temp = _path + ".tmp";
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(temp, JsonSerializer.Serialize(Current, Options));
            File.Move(temp, _path, overwrite: true);
        }
        Saved?.Invoke(Current);
    }
}
