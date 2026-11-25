# Arquitetura

## Projetos

| Projeto | Alvo | Conteúdo |
|---|---|---|
| `ScreenFlow.Core` | `net10.0` (testável em qualquer SO) | Protocolo (cabeçalho UDP, FEC, remontagem, NACK, mensagens de controle), criptografia, perfis de qualidade, controle adaptativo, configuração automática, configurações, leitura de releases, log |
| `ScreenFlow.Infrastructure` | `net10.0-windows10.0.26100.0` | Captura (WGC/DXGI), conversão de cor na GPU, encoder/decoder FFmpeg, áudio WASAPI/Opus, sessões host/viewer, Radmin, firewall, descoberta, hardware, atualização |
| `ScreenFlow.App` | WPF, gera `ScreenFlow.exe` | Tema, telas, player, diagnóstico |
| `ScreenFlow.Tests` | xUnit | Testes do Core |

## Pipeline de vídeo (host)

```
Windows.Graphics.Capture / DXGI Duplication
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

A captura e o encoder só rodam quando há alguém assistindo. Com a tela parada, um frame é reenviado a cada 100 ms para manter o intra-refresh.

## Pipeline do viewer

```
UDP → AES-GCM → FrameReassembler (FEC, NACK após 8 ms, descarte após 120 ms)
   → fila de 8 frames → FFmpeg D3D11VA → NV12→BGRA → WriteableBitmap (na composição)
UDP áudio → Opus → WASAPI (buffer ≤100 ms, descarta áudio >80 ms atrás do vídeo)
```

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

## Encerramento

Fechar a janela cancela o fechamento, encerra a sessão (manda `bye`, para áudio, pipeline, sockets, libera encoder/decoder/texturas/device D3D11) e só então fecha.
