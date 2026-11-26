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

## Autoteste

`ScreenFlow.exe --autoteste` grava um relatório em `%LOCALAPPDATA%\ScreenFlow\logs\autoteste-*.txt` e sai com código 0 quando tudo passa:

1. **Festa de música** no 127.0.0.1: host com um tom de teste, viewer sem saída de som; confere que o som chega e é decodificado. Na mesma festa, os **controles de quem assiste**: o host publica a música e os apps do som, o viewer recebe e pede para pular e para tirar um app, e o host recebe só os pedidos que valem (um ponto fora da música é recusado, e a qualidade, que a festa não oferece, nem sai do viewer).
2. **Sala da rede**: duas salas no mesmo PC trocam chat e voz (um tom no lugar do microfone), e uma baixa da outra a foto do perfil (40 KB) e uma imagem do chat (1 MB), com o hash conferido.
3. **Som do PC por app** (só com saída de som e Windows 10 2004+): o ScreenFlow toca 1000 Hz e confere, por Goertzel, que a captura normal ouve tudo, que a captura "sem o ScreenFlow" não ouve os 1000 Hz e que a mistura sem o `powershell.exe` não ouve os 1000 nem os 440 Hz que ele toca, mas ouve os 700 Hz do `pwsh.exe`.

O runner do CI não tem placa de som, então o `windows-build.yml` instala a placa virtual [VB-CABLE](https://vb-audio.com/Cable/) (o driver antigo do pacote, que o Windows Server aceita, criado com o `devcon` que vem no pacote da [Scream](https://github.com/duncanthrax/scream); os dois zips têm o SHA-256 conferido) e deixa os dois tons tocando antes do autoteste. Se a placa não instalar, a parte do som é pulada, o resumo do CI mostra um aviso e o resto segue. Para rodar no seu PC com os tons, toque um som de 440 Hz no `powershell.exe` e um de 700 Hz no `pwsh.exe` (veja o passo do autoteste no workflow); sem eles, só a parte do som do ScreenFlow é conferida.

## Publicar

```powershell
dotnet publish src/ScreenFlow.App/ScreenFlow.App.csproj -c Release -r win-x64 --self-contained true -o artifacts/publish
& "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe" installer\ScreenFlow.iss
```

O instalador sai em `artifacts/ScreenFlow-Setup-win-x64.exe`.

## Lançar uma versão

Pelo site: **Releases › Draft a new release**, crie a tag nova `v1.2.0` na `main` e clique em **Publish release**. Ou pelo git:

```powershell
git tag v1.2.0
git push origin v1.2.0
```

Nos dois casos o workflow `release.yml` usa a tag como versão, gera o instalador e o `.sha256` e anexa ao release (pelo git, ele também cria o release). Leva uns 10 minutos. O app instalado só oferece a versão nova depois que o instalador estiver anexado.

## Testar com dois PCs

1. Os dois no Radmin VPN, na mesma rede.
2. No host: Transmitir, copie o IP, Começar.
3. No viewer: Assistir, cole o IP, Conectar.
4. No viewer, F3 mostra FPS real, latência e perda. No host, **Testar banda** mede o upload até aquele viewer.
5. Para a 1.1.1: troque a foto em Configurações › Seu perfil e veja ela mudar no outro PC; mande uma imagem no chat (Ctrl+V com um print); transmita uma janela do Spotify ou do navegador e use os controles da música; faça uma festa só com o Spotify.
6. Para a 1.2.0: no player, troque a qualidade, pause e pule a música tocando no host e tire um app do som; no host, veja o aviso com o nome de quem pediu e desligue **Quem assiste pode mexer nos controles** para os botões sumirem. Confira o cartão da placa de vídeo na Visão geral e o texto do Diagnóstico.
7. Logs: `%LOCALAPPDATA%\ScreenFlow\logs`. As fotos e imagens ficam em `%LOCALAPPDATA%\ScreenFlow\imagens`.
