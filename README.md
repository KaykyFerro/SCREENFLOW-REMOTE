# ScreenFlow Remote

Aplicativo desktop Windows de compartilhamento de tela. O repositório contém um **MVP LAN funcional em desenvolvimento**, com host, visualizador, aprovação explícita, autenticação por código aleatório e frames criptografados.

> **Status honesto:** o caminho LAN atual usa captura GDI + JPEG por TCP, com largura limitada a 1920 pixels e alvo de até 30 FPS. Ainda não é a arquitetura final de baixa latência baseada em Windows Graphics Capture, Direct3D e WebRTC. Não existe validação de 4K/165 FPS.

## Requisitos

- **Para instalar/usar:** Windows 10/11 x64. O pacote Windows autocontido inclui o runtime .NET.
- **Para desenvolver:** .NET 10 SDK.

## Build de desenvolvimento

    dotnet restore ScreenFlowRemote.sln
    dotnet build ScreenFlowRemote.sln --configuration Release
    dotnet test ScreenFlowRemote.sln --configuration Release
    dotnet run --project src/ScreenFlow.Remote/ScreenFlow.Remote.csproj

O GitHub Actions compila, executa os testes, publica a aplicação autocontida win-x64 e gera o instalador Windows usando Inno Setup. O instalador e a pasta publicada aparecem no artefato ScreenFlow-Remote-win-x64 da execução de Actions.

## Usar o MVP LAN

1. Instale o ScreenFlow Remote nos dois computadores.
2. No computador que compartilhará a tela, selecione monitor, largura máxima e FPS.
3. Clique em INICIAR COMPARTILHAMENTO.
4. Compartilhe o IP mostrado, a porta TCP 39217 e o código da sessão com o outro computador.
5. No segundo computador, informe IP e código e clique em CONECTAR.
6. Aprove a solicitação no computador transmissor.

Os dois computadores precisam estar na mesma rede local. Libere TCP 39217 apenas no perfil de rede privada confiável do Windows.

## Segurança

O código possui 128 bits de aleatoriedade. O cliente prova que conhece o código por meio de um desafio novo por conexão, e os frames são protegidos com AES-GCM. A transmissão só começa depois da aprovação explícita no computador transmissor. O MVP ainda não passou por auditoria externa e não deve ser exposto diretamente à Internet.

## Arquitetura e documentação

- [LAN MVP](docs/LAN_MVP.md)
- [Arquitetura](docs/ARCHITECTURE.md)
- [Decisões técnicas](docs/TECHNICAL_DECISIONS.md)
- [Desenvolvimento](docs/DEVELOPMENT.md)
- [Roadmap](docs/ROADMAP.md)
- [Troubleshooting](docs/TROUBLESHOOTING.md)
