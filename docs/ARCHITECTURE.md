# Arquitetura

## Projetos

| Projeto | Alvo | Conteúdo |
|---|---|---|
| `ScreenFlow.Core` | `net10.0` (testável em qualquer SO) | Protocolo (cabeçalho UDP, FEC, remontagem, NACK, mensagens de controle), criptografia, perfis de qualidade, controle adaptativo, configuração automática, configurações, leitura de releases, log |
| `ScreenFlow.Infrastructure` | `net10.0-windows10.0.26100.0` | Captura de tela (WGC/DXGI) e de câmera (Windows.Media.Capture), conversão de cor na GPU, encoder/decoder FFmpeg, desenho do vídeo na GPU (D3D11), áudio WASAPI/Opus, sessões host/viewer, Radmin, firewall, descoberta, hardware, atualização |
| `ScreenFlow.App` | WPF, gera `ScreenFlow.exe` | Tema escuro/claro, menu lateral e telas (Visão geral, Transmissão, Assistir, Monitoramento, Configurações, Atualizações), sininho de avisos, player, telemetria, `--diagnostico`, `--autoteste`, `--capturas` |
| `ScreenFlow.Tests` | xUnit | Testes do Core |

## Pipeline de vídeo (host)

```
Windows.Graphics.Capture / DXGI Duplication / câmera (MediaFrameReader)
        │  textura BGRA (GPU)
        ▼
D3D11 Video Processor ── escala + BGRA→NV12 BT.709 (GPU)
        │  textura NV12 (GPU)
        ▼
FFmpeg encoder ── NVENC/AMF: textura D3D11 direto (zero-copy)
        │          QSV/OpenH264: staging NV12 → CPU
        │  pacote H.264/HEVC/AV1
        ▼
Packetizer ── pedaços de 1150 bytes + 1 paridade XOR a cada N
        │
        ├─► viewer 1: AES-GCM (chave própria) → UDP 47800
        ├─► viewer 2: ...
        └─► viewer N
```

A captura (e a câmera) e o encoder só rodam quando há alguém assistindo. Com a tela parada, um frame é reenviado a cada 100 ms para manter o intra-refresh. A câmera usa o formato que ela suporta mais perto da qualidade escolhida, e o Windows entrega o frame já em BGRA.

## Pipeline do viewer

```
UDP → AES-GCM → FrameReassembler (FEC, NACK após 8 ms, descarte após 120 ms)
   → fila de 8 frames → FFmpeg D3D11VA (textura NV12 na GPU)
   → D3D11 Video Processor (NV12→RGB e escala) → swapchain flip com tearing na janela do player
     (reserva: NV12→BGRA na CPU → WriteableBitmap, quando a GPU falha ou a opção está desligada)
UDP áudio → Opus → WASAPI (tela/câmera: buffer de 40 a 150 ms, descarta áudio >80 ms atrás do vídeo)
```

O decoder é criado no mesmo device D3D11 do presenter, então o frame não é copiado entre devices nem passa pela CPU. Como o WPF não desenha por cima de uma janela nativa, a barra de controles e as estatísticas ficam em janelas transparentes sobrepostas ao player.

## Festa de música (só áudio)

- O host não liga captura nem encoder de vídeo e pula a negociação de codec, então entra até quem não tem decoder de vídeo.
- Opus 48 kHz estéreo com quadros de 20 ms, modo "audio" e VBR restrito, de 128 a 320 kbps (acima disso o pacote passa do MTU do Radmin).
- Cada pacote leva o quadro atual e o anterior: uma perda isolada é recuperada sem buraco no som.
- O viewer toca com buffer de 120 a 400 ms, em troca de som sem cortes. O player mostra o espectro do som decodificado.
- A descoberta anuncia o modo (tela, câmera ou festa), e a tela Assistir mostra cada sessão com o modo.

## Canal de controle (TCP 47801)

Mensagens JSON com 4 bytes de tamanho.

1. Viewer → `hello` (versão do protocolo, decoders, limites de tela, porta UDP, chave pública ECDH)
2. Host → `challenge` (chave pública, nonce, se pede PIN)
3. Viewer → `auth` (HMAC derivado do segredo ECDH + PIN)
4. Host → `pending` (se aprovação manual) → `welcome` (salt, porta de mídia, perfil) ou `rejected`
5. Depois: `ping`/`pong` (relógio e RTT), `stats` (perda, RTT, banda, FPS decodificado, latência), `nack`, `keyframe`, `config`, `bwtest`/`bwresult`, `bye`.

## Qualidade adaptativa e vários viewers

- O codec é o melhor comum a todos os viewers (AV1 > HEVC > H.264).
- O host usa a **pior** amostra entre os viewers: perda > 2% ou RTT 40 ms acima da base reduz bitrate (até 50% do perfil), depois FPS, depois resolução. Cinco segundos estáveis sobem um degrau.
- O bitrate por viewer nunca passa de 80% do upload medido dividido pelo número de viewers.
- O NVENC muda bitrate sem reiniciar; outras mudanças recriam o encoder com um keyframe.

## Firewall

O instalador cria as regras (UDP 47800, TCP 47801, UDP 47802 e o próprio `ScreenFlow.exe`) uma vez só, com os mesmos nomes que o app usa, e as remove na desinstalação. O app confere as regras ao abrir e só pede permissão de novo (pelo sininho) se as portas mudarem.

## Encerramento

Fechar a janela cancela o fechamento, encerra a sessão (manda `bye`, para áudio, pipeline, sockets, libera encoder/decoder/texturas/device D3D11) e só então fecha.
