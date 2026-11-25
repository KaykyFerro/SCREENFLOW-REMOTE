# Desenvolvimento

## Pré-requisitos

- Windows 10/11 x64 com o SDK do .NET 10.
- PowerShell 7 para os scripts.
- Para testar de verdade: dois PCs (ou um PC e uma VM com GPU) na mesma rede do Radmin VPN.

## Compilar e rodar

```powershell
./scripts/fetch-ffmpeg.ps1                  # FFmpeg 7.1 LGPL → third_party/ffmpeg/bin
dotnet build ScreenFlow.sln
dotnet test ScreenFlow.sln
dotnet run --project src/ScreenFlow.App
```

As DLLs do FFmpeg são copiadas para `ffmpeg\` ao lado do executável. A versão precisa bater com o pacote `FFmpeg.AutoGen` (7.1.x).

O projeto `ScreenFlow.Core` e os testes rodam em Linux/macOS; os outros compilam fora do Windows com `EnableWindowsTargeting` (já ligado em `Directory.Build.props`), mas só rodam no Windows.

## Publicar

```powershell
dotnet publish src/ScreenFlow.App/ScreenFlow.App.csproj -c Release -r win-x64 --self-contained true -o artifacts/publish
& "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe" installer\ScreenFlow.iss
```

O instalador sai em `artifacts/ScreenFlow-Setup-win-x64.exe`.

## Lançar uma versão

Pelo site: **Releases › Draft a new release**, crie a tag nova `v1.1.0` na `main` e clique em **Publish release**. Ou pelo git:

```powershell
git tag v1.1.0
git push origin v1.1.0
```

Nos dois casos o workflow `release.yml` usa a tag como versão, gera o instalador e o `.sha256` e anexa ao release (pelo git, ele também cria o release). Leva uns 10 minutos. O app instalado só oferece a versão nova depois que o instalador estiver anexado.

## Testar com dois PCs

1. Os dois no Radmin VPN, na mesma rede.
2. No host: Transmitir, copie o IP, Começar.
3. No viewer: Assistir, cole o IP, Conectar.
4. No viewer, F3 mostra FPS real, latência e perda. No host, **Testar banda** mede o upload até aquele viewer.
5. Logs: `%LOCALAPPDATA%\ScreenFlow\logs`.
