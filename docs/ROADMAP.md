# Status e próximos passos

## Implementado

- [x] App renomeado para ScreenFlow, tema escuro/claro com tokens, telas Início, Transmitir, Assistir, Player e Configurações
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
- [x] Áudio do sistema WASAPI loopback em Opus 48 kHz, sincronizado por timestamp
- [x] Configuração automática por hardware e rede; diagnóstico na tela e por `--diagnostico`
- [x] Atualização pelo app via GitHub Releases com SHA-256; workflow de release por tag
- [x] Logs em arquivo e encerramento limpo de captura, encoder, sockets e áudio

## Testado

- 47 testes automatizados do Core: cabeçalho, FEC, NACK, descarte, mensagens, descoberta, troca de chaves, PIN, AES-GCM, Radmin, backoff, bitrates por perfil, negociação de codec, qualidade adaptativa, configuração automática, divisão de upload, configurações, releases, sincronia de relógio.
- Compilação de toda a solução e testes no CI do Windows; app publicado roda o `--diagnostico` e carrega o FFmpeg no CI.

## Ainda não testado (precisa de PCs reais)

- Captura, encoder e decoder em GPUs NVIDIA, AMD e Intel.
- Conexão real pelo Radmin VPN, descoberta por broadcast e firewall.
- FPS, latência e banda reais; 4K a 165 FPS.
- 10 ou mais viewers ao mesmo tempo.
- Atualização ponta a ponta (precisa do primeiro release publicado).

## Próximos passos

- Renderizador D3D11 no player (swapchain com tearing, frame decodificado direto da GPU). Hoje o frame passa por NV12→BGRA na CPU e `WriteableBitmap`, o que deve limitar a tela a cerca de 60–100 FPS em 4K.
- Cursor desenhado na captura DXGI (a WGC já inclui o cursor).
- Ritmo de envio (pacing) dos pacotes UDP para evitar rajadas em links lentos.
- Modo relay: um viewer repassa o vídeo para outros, para passar do limite de upload do host.
- Controle remoto de mouse e teclado, com autorização separada.
