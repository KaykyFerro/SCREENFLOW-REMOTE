# Solução de problemas

Comece sempre por **Configurações › Diagnóstico** (ou `ScreenFlow.exe --diagnostico`) e pelos logs em `%LOCALAPPDATA%\ScreenFlow\logs`.

## Rede

| Sintoma | Causa provável | Solução |
|---|---|---|
| "Radmin VPN não encontrado" | Radmin não instalado | Instale em <https://www.radmin-vpn.com/> |
| "Radmin VPN sem rede ativa" | Instalado, mas fora de uma rede | Entre na mesma rede que os outros |
| Viewer fica em "Conectando" | Firewall do host bloqueando | O instalador libera as portas; se mudou as portas, use Configurações › Rede e firewall › Liberar portas (ou o aviso no sininho) |
| "O ScreenFlow não está transmitindo em …" | Host não clicou em Iniciar, ou portas diferentes | Confira as portas nos dois lados |
| Host não aparece na lista | Broadcast bloqueado | Digite o IP direto |
| Conecta mas não chega vídeo | UDP 47800 bloqueado no host ou no viewer | Libere as portas no firewall nos dois PCs |

Teste a rede com `ping 26.x.x.x`. No Radmin, uma conexão indireta (relay) tem banda bem menor que uma direta.

## Qualidade e FPS

- Abra as estatísticas no player (F3). Perda acima de 2% significa que a rede não aguenta: o app já reduz bitrate, FPS e resolução sozinho; a linha "Qualidade" mostra o motivo.
- Use **Testar banda** no host para medir o upload até cada pessoa. O valor é usado para dividir a qualidade entre quem assiste.
- FPS real abaixo do escolhido: veja o encoder em Diagnóstico. Notebooks precisam estar na tomada e usar a GPU dedicada.
- O monitor de origem a 60 Hz limita a captura a 60 FPS.
- 4K a 165 FPS exige NVIDIA RTX 40/50, AMD RX 7000/9000 ou Intel Arc no host, e uns 120 Mbps de upload por pessoa.

## Áudio

- Ligue **Compartilhar o som do PC** antes de começar a transmitir (na festa de música ele sempre vai).
- O áudio capturado é o do dispositivo de saída padrão do Windows. Se trocar o fone ou a caixa no meio, o app acompanha sozinho.
- O visualizador da tela Transmissão mexe mesmo antes de alguém entrar: se ele fica parado com música tocando, o som está saindo por outro dispositivo.
- No player, M liga/desliga o som e as setas mudam o volume.
- `ScreenFlow.exe --autoteste` confere a festa de música no próprio PC (host e viewer no 127.0.0.1).

## Câmera

- A câmera só liga quando alguém entra. Se outro app (Discord, OBS, Teams) estiver usando a câmera, feche-o e clique em atualizar ao lado da lista.
- A câmera usa o formato mais próximo da qualidade escolhida que ela suportar.

## Player

- Se a imagem ficar preta com o som funcionando, desligue **Desenhar o vídeo direto na GPU** em Configurações › Player: o vídeo passa a ir pela CPU.

## Atualização

- "Não foi possível verificar": sem internet ou sem release publicado ainda. A tela Atualizações tem o botão **Procurar agora**.
- Se o instalador falhar, baixe manualmente em Releases.

## Build

- Rode `./scripts/fetch-ffmpeg.ps1` antes de compilar; sem as DLLs o app abre, mas não acha encoders.
- A versão do FFmpeg precisa ser 7.1.x (mesma do pacote FFmpeg.AutoGen).
