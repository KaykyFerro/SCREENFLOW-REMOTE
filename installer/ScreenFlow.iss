#define MyAppName "ScreenFlow"
#ifndef MyAppVersion
  #define MyAppVersion "1.2.0"
#endif
#define MyAppPublisher "KaykyFerro"
#define MyAppExeName "ScreenFlow.exe"
; Portas padrão do app (as mesmas de AppInfo.cs). As regras têm os mesmos nomes que o app usa (FirewallManager.cs).
#define MediaPort "47800"
#define ControlPort "47801"
#define DiscoveryPort "47802"

[Setup]
; Mesmo AppId da versão anterior (ScreenFlow Remote) para atualizar no lugar.
AppId={{C67E4FCB-DF9E-4E61-AFC2-8E70F66AA5B0}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL=https://github.com/KaykyFerro/SCREENFLOW-REMOTE
AppUpdatesURL=https://github.com/KaykyFerro/SCREENFLOW-REMOTE/releases
VersionInfoVersion={#MyAppVersion}
DefaultDirName={localappdata}\Programs\ScreenFlow
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
DisableDirPage=auto
PrivilegesRequired=lowest
ArchitecturesInstallIn64BitMode=x64compatible
ArchitecturesAllowed=x64compatible
MinVersion=10.0.19041
OutputDir=..\artifacts
OutputBaseFilename=ScreenFlow-Setup-win-x64
SetupIconFile=..\src\ScreenFlow.App\Assets\screenflow.ico
Compression=lzma2/ultra64
SolidCompression=yes
LZMAUseSeparateProcess=yes
WizardStyle=modern
UninstallDisplayIcon={app}\{#MyAppExeName}
UninstallDisplayName={#MyAppName}
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "brazilianportuguese"; MessagesFile: "compiler:Languages\BrazilianPortuguese.isl"

[Tasks]
Name: "firewall"; Description: "Liberar as portas do ScreenFlow no Firewall do Windows (recomendado; o Windows pede permissão uma vez)"; GroupDescription: "Rede:"
Name: "desktopicon"; Description: "Criar atalho na área de trabalho"; GroupDescription: "Atalhos adicionais:"; Flags: unchecked

[InstallDelete]
; Remove o executável antigo (ScreenFlow.Remote.exe) de instalações anteriores.
Type: files; Name: "{app}\ScreenFlow.Remote.*"
; DLLs do FFmpeg que o app não usa mais (avformat, avfilter, avdevice).
Type: files; Name: "{app}\ffmpeg\avformat-*.dll"
Type: files; Name: "{app}\ffmpeg\avfilter-*.dll"
Type: files; Name: "{app}\ffmpeg\avdevice-*.dll"

[Files]
Source: "..\artifacts\publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "Abrir o ScreenFlow"; Flags: postinstall nowait skipifsilent
; Atualização silenciosa feita pelo próprio app: reabre o ScreenFlow no fim.
Filename: "{app}\{#MyAppExeName}"; Flags: nowait; Check: WizardSilent

[Code]
const
  RuleMedia = 'ScreenFlow mídia (UDP {#MediaPort})';
  RuleControl = 'ScreenFlow controle (TCP {#ControlPort})';
  RuleDiscovery = 'ScreenFlow descoberta (UDP {#DiscoveryPort})';
  RuleApp = 'ScreenFlow (aplicativo)';

function Netsh(const Arguments: String): Boolean;
var
  ResultCode: Integer;
begin
  Result := Exec(ExpandConstant('{sys}\netsh.exe'), Arguments, '', SW_HIDE, ewWaitUntilTerminated, ResultCode) and (ResultCode = 0);
end;

function RuleExists(const Name: String): Boolean;
begin
  Result := Netsh('advfirewall firewall show rule name="' + Name + '"');
end;

{ As três regras de porta bastam (quem já liberou pela versão 1.0.0 não recebe outro pedido de permissão). }
function FirewallReady: Boolean;
begin
  Result := RuleExists(RuleMedia) and RuleExists(RuleControl) and RuleExists(RuleDiscovery);
end;

function ReplaceRule(const Name, Details: String): String;
begin
  Result := 'netsh advfirewall firewall delete rule name="' + Name + '" >nul 2>&1 & ' +
            'netsh advfirewall firewall add rule name="' + Name + '" dir=in action=allow ' + Details + ' profile=any enable=yes >nul & ';
end;

{ Cria as regras de entrada com um único pedido de permissão (UAC). Se já existem, não pergunta nada. }
procedure ConfigureFirewall;
var
  Exe, Script: String;
  ResultCode: Integer;
begin
  if FirewallReady then
  begin
    Log('Regras do firewall já existem.');
    exit;
  end;
  Exe := ExpandConstant('{app}\{#MyAppExeName}');
  Script := ReplaceRule(RuleMedia, 'protocol=UDP localport={#MediaPort} program="' + Exe + '"') +
            ReplaceRule(RuleControl, 'protocol=TCP localport={#ControlPort} program="' + Exe + '"') +
            ReplaceRule(RuleDiscovery, 'protocol=UDP localport={#DiscoveryPort} program="' + Exe + '"') +
            ReplaceRule(RuleApp, 'program="' + Exe + '"') +
            'exit /b 0';
  WizardForm.StatusLabel.Caption := 'Liberando as portas do ScreenFlow no Firewall do Windows...';
  if not ShellExec('runas', ExpandConstant('{cmd}'), '/c ' + Script, '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then
    Log('Permissão para criar as regras do firewall recusada: ' + SysErrorMessage(ResultCode))
  else if FirewallReady then
    Log('Regras do firewall criadas.')
  else
    Log('As regras do firewall não foram confirmadas.');
end;

procedure RemoveFirewall;
var
  Script: String;
  ResultCode: Integer;
begin
  if not (RuleExists(RuleMedia) or RuleExists(RuleControl) or RuleExists(RuleDiscovery) or RuleExists(RuleApp)) then exit;
  Script := 'netsh advfirewall firewall delete rule name="' + RuleMedia + '" >nul 2>&1 & ' +
            'netsh advfirewall firewall delete rule name="' + RuleControl + '" >nul 2>&1 & ' +
            'netsh advfirewall firewall delete rule name="' + RuleDiscovery + '" >nul 2>&1 & ' +
            'netsh advfirewall firewall delete rule name="' + RuleApp + '" >nul 2>&1 & exit /b 0';
  ShellExec('runas', ExpandConstant('{cmd}'), '/c ' + Script, '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if (CurStep = ssPostInstall) and WizardIsTaskSelected('firewall') then
    ConfigureFirewall;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usUninstall then
    RemoveFirewall;
end;
