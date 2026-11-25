# Status e próximos passos

## Implementado

- [x] App renomeado para ScreenFlow, tema escuro/claro com tokens
- [x] 1.0.1: visual novo com menu lateral, cores vivas e fonte Inter; telas Visão geral, Transmissão, Assistir, Monitoramento, Configurações e Atualizações
- [x] 1.0.1: festa de música (só áudio, Opus até 320 kbps com cópia de segurança) e compartilhamento de câmera
- [x] 1.0.1: sininho de avisos (versão nova, firewall) e procura de versão a cada 6 horas
- [x] 1.0.1: instalador libera as portas no firewall uma vez só
- [x] 1.0.1: player com vídeo direto na GPU (D3D11, swapchain com tearing) e controles em janela sobreposta
- [x] Detecção do Radmin VPN, bind no IP 26.x, aviso com link de instalação
- [x] Portas configuráveis (47800 UDP mídia, 47801 TCP controle, 47802 UDP descoberta) e regras do firewall com UAC
- [x] Descoberta de hosts por broadcast na rede do Radmin
- [x] Conexão só pelo IP, handshake com versão, decoders e negociação de codec; reconexão com backoff
- [x] PIN opcional, aprovação de cada viewer, limite de tentativas erradas, criptografia ECDH + AES-GCM
- [x] Várias pessoas assistindo (padrão 20), uma codificação para todas, upload dividido entre elas
- [x] Captura WGC (monitor/janela, com cursor) e DXGI de reserva, na GPU
- [x] Encoder FFmpeg por hardware (NVENC/AMF/QSV), AV1 > HEVC > H.264, baixa latência, sem B-frames, intra-refresh
- [x] Transporte UDP com pacotes numerados, FEC XOR, NACK, pedido de keyframe
- [x] Qualidade adaptativa (bitrate → FPS → resolução) mostrada no overlay
- [x] Decoder D3D11VA, player com tela cheia, volume e overlay de estatísticas
- [x] Monitoramento com gráficos de FPS, banda, ping, perda e áudio dos últimos 90 segundos
- [x] Áudio do sistema WASAPI loopback em Opus 48 kHz, sincronizado por timestamp
- [x] Configuração automática por hardware e rede; diagnóstico na tela e por `--diagnostico`
- [x] Atualização pelo app via GitHub Releases com SHA-256; workflow de release por tag
- [x] Logs em arquivo e encerramento limpo de captura, encoder, sockets e áudio

## Testado

- 71 testes automatizados: cabeçalho, FEC, NACK, descarte, mensagens, descoberta, troca de chaves, PIN, AES-GCM, Radmin, backoff, bitrates por perfil, negociação de codec, qualidade adaptativa, configuração automática, divisão de upload, configurações, releases, sincronia de relógio, modos (tela, câmera, festa), perfis de áudio, medidor de espectro, redundância do áudio, recursos do tema (cada cor e estilo existe, com um tipo só, nos temas escuro e claro).
- Compilação de toda a solução e testes no CI do Windows; o app publicado roda o `--diagnostico` (carrega o FFmpeg), o `--autoteste` (festa de música de verdade no 127.0.0.1, do encoder Opus do host até o decoder do viewer) e o `--capturas` (PNG de cada tela).

## Ainda não testado (precisa de PCs reais)

- Captura, encoder e decoder em GPUs NVIDIA, AMD e Intel.
- Conexão real pelo Radmin VPN, descoberta por broadcast e firewall.
- FPS, latência e banda reais; 4K a 165 FPS.
- 10 ou mais viewers ao mesmo tempo.
- Câmera em webcams e placas de captura reais.
- Player na GPU em placas NVIDIA, AMD e Intel (o caminho pela CPU continua de reserva).
- Atualização ponta a ponta da 1.0.0 para a 1.0.1 pelo app.

## Próximos passos

- Cursor desenhado na captura DXGI (a WGC já inclui o cursor).
- Ritmo de envio (pacing) dos pacotes UDP para evitar rajadas em links lentos.
- Modo relay: um viewer repassa o vídeo para outros, para passar do limite de upload do host.
- Controle remoto de mouse e teclado, com autorização separada.
