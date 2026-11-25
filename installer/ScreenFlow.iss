#define MyAppName "ScreenFlow"
#ifndef MyAppVersion
  #define MyAppVersion "1.0.0"
#endif
#define MyAppPublisher "KaykyFerro"
#define MyAppExeName "ScreenFlow.exe"

[Setup]
; Mesmo AppId da versão anterior (ScreenFlow Remote) para atualizar no lugar.
AppId={{C67E4FCB-DF9E-4E61-AFC2-8E70F66AA5B0}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={localappdata}\Programs\ScreenFlow
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
DisableDirPage=auto
PrivilegesRequired=lowest
ArchitecturesInstallIn64BitMode=x64compatible
ArchitecturesAllowed=x64compatible
OutputDir=..\artifacts
OutputBaseFilename=ScreenFlow-Setup-win-x64
SetupIconFile=..\src\ScreenFlow.App\Assets\screenflow.ico
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
UninstallDisplayIcon={app}\{#MyAppExeName}
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "brazilianportuguese"; MessagesFile: "compiler:Languages\BrazilianPortuguese.isl"

[Tasks]
Name: "desktopicon"; Description: "Criar atalho na área de trabalho"; GroupDescription: "Atalhos adicionais:"; Flags: unchecked

[InstallDelete]
; Remove o executável antigo (ScreenFlow.Remote.exe) de instalações anteriores.
Type: files; Name: "{app}\ScreenFlow.Remote.*"

[Files]
Source: "..\artifacts\publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "Abrir o ScreenFlow"; Flags: postinstall nowait skipifsilent
; Atualização silenciosa feita pelo próprio app: reabre o ScreenFlow no fim.
Filename: "{app}\{#MyAppExeName}"; Flags: nowait; Check: WizardSilent
