#define MyAppName "ScreenFlow"
#ifndef MyAppVersion
  #define MyAppVersion "1.4.1"
#endif
#define MyAppPublisher "KaykyFerro"
#define MyAppExeName "ScreenFlow.exe"
; Portas padrão do app (as mesmas de AppInfo.cs). As regras têm os mesmos nomes que o app usa (FirewallManager.cs).
#define MediaPort "47800"
#define ControlPort "47801"
#define DiscoveryPort "47802"
#define FilePort "47803"
; O que o instalador pede ao ScreenFlow (StartupOptions.cs): o app lê na próxima vez que abrir e apaga o arquivo.
#define RequestsFile "{localappdata}\ScreenFlow\instalador.txt"

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
; (1.4.1) O ScreenFlow baixa do site oficial, confere a assinatura digital e instala quando abrir (o Windows pede permissão).
Name: "tailscale"; Description: "Instalar o Tailscale junto: junta os PCs pela internet, sem abrir portas no roteador (cada pessoa entra na própria conta)"; GroupDescription: "VPN para juntar os PCs (opcional; baixada do site oficial e conferida):"; Flags: unchecked
Name: "radmin"; Description: "Instalar o Radmin VPN junto: rede com nome e senha"; GroupDescription: "VPN para juntar os PCs (opcional; baixada do site oficial e conferida):"; Flags: unchecked
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
var
  ServerPage: TInputOptionWizardPage;
  AddressPage: TInputQueryWizardPage;

const
  RuleMedia = 'ScreenFlow mídia (UDP {#MediaPort})';
  RuleControl = 'ScreenFlow controle (TCP {#ControlPort})';
  RuleDiscovery = 'ScreenFlow descoberta (UDP {#DiscoveryPort})';
  RuleFiles = 'ScreenFlow arquivos (TCP {#FilePort})';
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

{ As regras de porta bastam. A 1.5.0 traz a porta dos arquivos do chat (TCP 47803): quem atualiza recebe um pedido de
  permissão, uma vez, para criar essa regra. O PC servidor precisa também da regra do aplicativo: o servidor dentro do
  ScreenFlow usa outras portas (47810 a 47812). }
function FirewallReady: Boolean;
begin
  Result := RuleExists(RuleMedia) and RuleExists(RuleControl) and RuleExists(RuleDiscovery) and RuleExists(RuleFiles)
            and ((ServerPage = nil) or (ServerPage.SelectedValueIndex <> 2) or RuleExists(RuleApp));
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
            ReplaceRule(RuleFiles, 'protocol=TCP localport={#FilePort} program="' + Exe + '"') +
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
  if not (RuleExists(RuleMedia) or RuleExists(RuleControl) or RuleExists(RuleDiscovery) or RuleExists(RuleFiles) or RuleExists(RuleApp)) then exit;
  Script := 'netsh advfirewall firewall delete rule name="' + RuleMedia + '" >nul 2>&1 & ' +
            'netsh advfirewall firewall delete rule name="' + RuleControl + '" >nul 2>&1 & ' +
            'netsh advfirewall firewall delete rule name="' + RuleDiscovery + '" >nul 2>&1 & ' +
            'netsh advfirewall firewall delete rule name="' + RuleFiles + '" >nul 2>&1 & ' +
            'netsh advfirewall firewall delete rule name="' + RuleApp + '" >nul 2>&1 & exit /b 0';
  ShellExec('runas', ExpandConstant('{cmd}'), '/c ' + Script, '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
end;

{ ───────────── (1.4.1) Servidor ScreenFlow e VPN ───────────── }

procedure InitializeWizard;
begin
  ServerPage := CreateInputOptionPage(wpSelectTasks,
    'Servidor ScreenFlow', 'Como este PC vai usar o ScreenFlow?',
    'Com um servidor, todo mundo entra num PC só (o seu PC servidor no Tailscale, por exemplo): o chat, a voz e as transmissões passam por ele. ' +
    'Dá para mudar depois em Configurações > Servidor.',
    True, False);
  ServerPage.Add('Agora não (direto entre os PCs, como sempre, ou do jeito que já estiver configurado)');
  ServerPage.Add('Entrar num servidor ScreenFlow');
  ServerPage.Add('Este PC é o servidor (ele precisa ficar ligado; os outros entram com o endereço dele)');
  ServerPage.SelectedValueIndex := 0;

  AddressPage := CreateInputQueryPage(ServerPage.ID,
    'Entrar num servidor', 'Qual é o endereço do servidor?',
    'O IP do PC servidor no Tailscale (100.x) ou no Radmin VPN (26.x), ou o nome dele no Tailscale. ' +
    'Se o servidor usa outra porta, coloque depois de dois pontos (100.64.1.2:47810).');
  AddressPage.Add('Endereço do servidor:', False);
end;

function ShouldSkipPage(PageID: Integer): Boolean;
begin
  Result := (AddressPage <> nil) and (PageID = AddressPage.ID) and (ServerPage.SelectedValueIndex <> 1);
end;

{ Só letras, números, ponto, hífen, sublinhado e dois pontos (IP, nome do PC e porta), como o app aceita. }
function IsValidAddress(const Text: String): Boolean;
var
  I: Integer;
  C: Char;
begin
  Result := (Length(Text) > 0) and (Length(Text) <= 255);
  for I := 1 to Length(Text) do
  begin
    C := Text[I];
    if not (((C >= 'a') and (C <= 'z')) or ((C >= 'A') and (C <= 'Z')) or ((C >= '0') and (C <= '9'))
            or (C = '.') or (C = '-') or (C = '_') or (C = ':')) then
    begin
      Result := False;
      exit;
    end;
  end;
end;

function NextButtonClick(CurPageID: Integer): Boolean;
begin
  Result := True;
  if (AddressPage <> nil) and (CurPageID = AddressPage.ID) then
  begin
    AddressPage.Values[0] := Trim(AddressPage.Values[0]);
    if not IsValidAddress(AddressPage.Values[0]) then
    begin
      MsgBox('Digite o IP do servidor (por exemplo 100.64.1.2 ou 26.12.34.56) ou o nome do PC no Tailscale.', mbError, MB_OK);
      Result := False;
    end;
  end;
end;

{ O que o ScreenFlow faz quando abrir: os mesmos argumentos que a linha de comando aceita. }
function InstallerRequests: String;
begin
  Result := '';
  case ServerPage.SelectedValueIndex of
    1: Result := '--entrar-servidor ' + AddressPage.Values[0];
    2: Result := '--modo-servidor';
  end;
  { Na atualização silenciosa (pelo app), as tarefas lembradas da instalação anterior não pedem para instalar de novo. }
  if not WizardSilent then
  begin
    if WizardIsTaskSelected('tailscale') then Result := Result + ' --instalar-tailscale';
    if WizardIsTaskSelected('radmin') then Result := Result + ' --instalar-radmin';
  end;
  Result := Trim(Result);
end;

{ Fica num arquivo (e não nos argumentos do "Abrir o ScreenFlow"): vale mesmo se a pessoa desmarcar abrir agora. }
procedure SaveInstallerRequests;
var
  Requests, FileName: String;
begin
  Requests := InstallerRequests;
  if Requests = '' then exit;
  FileName := ExpandConstant('{#RequestsFile}');
  ForceDirectories(ExtractFileDir(FileName));
  if SaveStringToFile(FileName, Requests, False) then
    Log('Pedidos para o ScreenFlow: ' + Requests)
  else
    Log('Não deu para salvar os pedidos para o ScreenFlow.');
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
  begin
    if WizardIsTaskSelected('firewall') then
      ConfigureFirewall;
    SaveInstallerRequests;
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usUninstall then
    RemoveFirewall;
end;
