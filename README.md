# ScreenFlow Remote

Aplicativo desktop Windows para compartilhamento remoto de tela, projetado para baixa latência, conexão P2P, captura eficiente e qualidade adaptativa.

> **Status atual: Fase 1 / Fundação.** A base da solução, dashboard WPF e detecção real de monitores estão no repositório. Captura WGC, vídeo WebRTC e transmissão ainda não foram implementados nem testados.

## Requisitos
- Windows 10/11 x64
- .NET 10 SDK

## Build e execução
```powershell
dotnet restore ScreenFlowRemote.sln
dotnet build ScreenFlowRemote.sln
dotnet run --project src/ScreenFlow.Remote/ScreenFlow.Remote.csproj
```

## Estrutura
- `ScreenFlow.Remote`: aplicativo desktop WPF
- `ScreenFlow.Core`: modelos e contratos
- `ScreenFlow.Infrastructure`: serviços de Windows e diagnóstico
- `tests/`: testes automatizados
- `docs/`: arquitetura, decisões técnicas, desenvolvimento, roadmap e troubleshooting

## Documentação
- [Arquitetura](docs/ARCHITECTURE.md)
- [Decisões técnicas](docs/TECHNICAL_DECISIONS.md)
- [Desenvolvimento](docs/DEVELOPMENT.md)
- [Roadmap](docs/ROADMAP.md)
- [Troubleshooting](docs/TROUBLESHOOTING.md)

O objetivo arquitetural é suportar até 4K e 165 FPS quando equipamento, encoder, decoder e rede forem capazes. Esses limites não são garantias de desempenho.
