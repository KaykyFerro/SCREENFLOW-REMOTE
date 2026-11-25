# Arquitetura

## Projetos

| Projeto | Alvo | Conteúdo |
|---|---|---|
| `ScreenFlow.Core` | `net10.0` (testável em qualquer SO) | Protocolo (cabeçalho UDP, FEC, remontagem, NACK, mensagens de controle), protocolo e estado da sala da rede (presença, chat, histórico), criptografia, perfis de qualidade, controle adaptativo, configuração automática, regras do som do PC por app, configurações, leitura de releases, log |
| `ScreenFlow.Infrastructure` | `net10.0-windows10.0.26100.0` | Captura de tela (WGC/DXGI) e de câmera (Windows.Media.Capture), conversão de cor na GPU, encoder/decoder FFmpeg, desenho do vídeo na GPU (D3D11), áudio WASAPI/Opus (com o loopback por processo), sala da rede (chat e voz), sessões host/viewer, Radmin, firewall, descoberta, hardware, atualização |
| `ScreenFlow.App` | WPF, gera `ScreenFlow.exe` | Tema escuro/claro, menu lateral com o painel da voz, telas (Visão geral, Transmissão, Assistir, Chat da rede, Monitoramento, Configurações, Atualizações), sininho de avisos, ícone na bandeja, player, telemetria, `--diagnostico`, `--autoteste`, `--capturas` |
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

### Câmera junto com a tela (1.1.0)

No modo tela, a câmera pode ir junto como uma **segunda faixa de vídeo**: device D3D11, captura e encoder próprios, no mesmo codec da tela e num tamanho de quadrinho (até 720p). Os pacotes têm tipos próprios (`CameraVideo` e `CameraFec`), com FEC, NACK e pedido de keyframe separados, então a tela nunca espera a câmera. Se a câmera falhar (em uso por outro app, desligada, bloqueada pela privacidade do Windows), a tela continua e a câmera tenta de novo com espera crescente. O viewer tem um decoder só para a câmera e desenha o quadrinho no canto pela CPU (é pequeno). O host só manda a câmera para quem avisou no `hello` que sabe mostrá-la (`camera-overlay`), então um viewer 1.0.x vê só a tela.

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

## Som do PC

O som do PC é capturado por WASAPI e vira Opus 48 kHz estéreo. Desde a 1.1.0 ele é separado por app (Windows 10 2004 ou mais novo), para que a voz da rede, os avisos e qualquer som que o próprio ScreenFlow toca não voltem para quem assiste:

- **Sem apps escolhidos**: uma captura de loopback por processo (`ActivateAudioInterfaceAsync` no dispositivo virtual `VAD\Process_Loopback`) no modo "tudo menos a árvore do ScreenFlow".
- **Com apps escolhidos** (Spotify, Discord...): o Windows só tira uma árvore de processos por captura, então cada app que tem sessão de som é capturado separado ("só este processo e os filhos") e a mistura é feita no ScreenFlow. Quem decide quais processos capturar é o `AppAudioPlan` (no Core, com testes): o ScreenFlow e os apps escolhidos nunca entram; os ancestrais deles também não (capturar a árvore do Explorer traria o som deles junto); e um processo cujo ancestral já entra fica de fora, para não dobrar o volume. A lista é revista a cada segundo. O ritmo da mistura vem de uma captura "tudo menos o ScreenFlow", e cada app tem uma fila com 20 ms de folga e no máximo 120 ms guardados.
- **Reserva**: se o Windows não deixar separar, vai o loopback normal da saída padrão (todo o som do PC) e a tela Transmissão avisa.

A escolha dos apps vale na hora, durante a transmissão. Os apps são identificados pelo nome do executável, então um app que toca por outro processo com outro nome (o `steamwebhelper` do Steam, por exemplo) aparece separado na lista.

## Sala da rede (UDP 47802)

Todo mundo com o ScreenFlow 1.1 aberto na mesma rede do Radmin se vê, conversa no chat e pode entrar na voz. Não há servidor: tudo passa pela porta de descoberta, que o instalador já libera.

- **Pacotes** binários com cabeçalho de 20 bytes (`SR`, versão, tipo e o id de quem mandou) e no máximo 1400 bytes: presença, saída, chat, confirmação do chat, voz e pedido de histórico. Os anúncios em JSON das transmissões das versões 1.0.x chegam na mesma porta e alimentam a tela Assistir.
- **Presença** por broadcast nos adaptadores do Radmin a cada 2 s (e direto para quem já é conhecido ou foi adicionado pelo IP), com o nome, se está na voz, mudo, sem som e se está ao vivo (tela, som, câmera). Quem some por 7 s sai da lista.
- **Chat** direto para cada PC, com confirmação e até 6 tentativas. O histórico guarda as últimas 300 mensagens só na memória; quem chega pede o histórico aos outros.
- **Voz** direto para quem está na voz: Opus 32 kbps, 48 kHz mono, quadros de 20 ms, com portão de voz (só manda enquanto você fala) e cada quadro levando o anterior para cobrir uma perda isolada. Cada pessoa tem um buffer de jitter e todas são mixadas numa saída só. Sem som (ensurdecer) também desliga o microfone.
- **Segurança**: os pacotes da sala **não** são criptografados nem autenticados pelo ScreenFlow. Eles contam com o túnel do Radmin VPN, e qualquer um na mesma rede do Radmin com o ScreenFlow aberto ouve a voz e lê o chat. A transmissão de tela continua com ECDH + AES-GCM.

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

## Bandeja e encerramento

- Fechar ou minimizar a janela (com **Configurações › Fechar e minimizar levam o ScreenFlow para a bandeja**, ligado por padrão) só esconde a janela: a transmissão, a voz e o chat continuam. A dica do ícone mostra se está ao vivo e o que está indo (tela, som, câmera), quantas pessoas assistem e a voz; o menu do botão direito tem abrir, transmitir ou encerrar, os controles da voz, o chat e **Sair do ScreenFlow**.
- Só uma cópia do app roda por vez (mutex `Local\ScreenFlow.Instancia`). Abrir de novo mostra a janela que já estava aberta.
- **Sair do ScreenFlow** (ou fechar com a bandeja desligada, ou o Windows desligando) encerra a sessão (manda `bye`, para áudio, pipeline, sockets, libera encoder/decoder/texturas/device D3D11), sai da sala da rede e só então fecha.
