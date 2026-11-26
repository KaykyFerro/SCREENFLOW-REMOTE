# Arquitetura

## Projetos

| Projeto | Alvo | Conteúdo |
|---|---|---|
| `ScreenFlow.Core` | `net10.0` (testável em qualquer SO) | Protocolo (cabeçalho UDP, FEC, remontagem, NACK, mensagens de controle, regras dos controles de quem assiste), protocolo e estado da sala da rede (presença, chat, histórico, arquivos e downloads), arquivos da sala guardados no PC, criptografia, perfis de qualidade, controle adaptativo, configuração automática, regras do som do PC por app, nomes dos apps de mídia, link do instalador do Radmin, configurações, leitura de releases, log |
| `ScreenFlow.Infrastructure` | `net10.0-windows10.0.26100.0` | Captura de tela (WGC/DXGI) e de câmera (Windows.Media.Capture), conversão de cor na GPU, encoder/decoder FFmpeg, desenho do vídeo na GPU (D3D11), áudio WASAPI/Opus (com o loopback por processo), controles de mídia do Windows, sala da rede (chat, voz e arquivos), sessões host/viewer, Radmin (e o instalador dele), firewall, descoberta, hardware, atualização |
| `ScreenFlow.App` | WPF, gera `ScreenFlow.exe` | Tema escuro/claro, menu lateral com o painel da voz e o seu perfil, telas (Visão geral, Transmissão, Assistir, Chat da rede, Monitoramento, Configurações, Atualizações), fotos e imagens (abrir, virar, diminuir), sininho de avisos, ícone na bandeja, player, telemetria, `--diagnostico`, `--autoteste`, `--capturas` |
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

Desde a 1.1.1 os pacotes de cada frame saem espalhados no tempo (no mínimo 30 Mbps ou 3 vezes o bitrate somado, com prazo de 50 ms por frame), e as retransmissões passam na frente. Para viewers 1.1.1 a paridade é **espalhada**: o grupo g junta os pacotes g, g + G, g + 2G..., então uma rajada de perdas cai em grupos diferentes e cada grupo recupera o seu pacote.

### Qualidade trocada ao vivo e qualidade fixa (1.1.1)

A resolução, o FPS e a qualidade fixa podem mudar com a transmissão no ar (na Transmissão ou salvando nas Configurações). O host troca as opções da sessão, renegocia o perfil com quem está assistindo e o pipeline recria o encoder com um keyframe; a captura só é recriada quando o FPS sobe (o Windows.Graphics.Capture limita o ritmo na criação) ou quando muda o formato da câmera. Com **Qualidade fixa**, o controle adaptativo fica desligado: a qualidade não baixa sozinha quando a rede aperta.

### Câmera junto com a tela (1.1.0)

No modo tela, a câmera pode ir junto como uma **segunda faixa de vídeo**: device D3D11, captura e encoder próprios, no mesmo codec da tela e num tamanho de quadrinho (até 720p). Os pacotes têm tipos próprios (`CameraVideo` e `CameraFec`), com FEC, NACK e pedido de keyframe separados, então a tela nunca espera a câmera. Se a câmera falhar (em uso por outro app, desligada, bloqueada pela privacidade do Windows), a tela continua e a câmera tenta de novo com espera crescente. O viewer tem um decoder só para a câmera e desenha o quadrinho no canto pela CPU (é pequeno). O host só manda a câmera para quem avisou no `hello` que sabe mostrá-la (`camera-overlay`), então um viewer 1.0.x vê só a tela.

## Pipeline do viewer

```
UDP → AES-GCM → FrameReassembler (FEC, NACK quando o frame para de chegar, prazo de 120 a 500 ms pelo RTT)
   → fila de 8 frames → FFmpeg D3D11VA (textura NV12 na GPU)
   → D3D11 Video Processor (NV12→RGB e escala) → swapchain flip com tearing na janela do player
     (reserva: NV12→BGRA na CPU → WriteableBitmap, quando a GPU falha ou a opção está desligada)
UDP áudio → Opus → WASAPI (tela/câmera: buffer que começa em 25 ms e só cresce até 90 ms se o som falhar)
```

O decoder é criado no mesmo device D3D11 do presenter, então o frame não é copiado entre devices nem passa pela CPU. Como o WPF não desenha por cima de uma janela nativa, a barra de controles e as estatísticas ficam em janelas transparentes sobrepostas ao player.

## Festa de música (só áudio)

- O host não liga captura nem encoder de vídeo e pula a negociação de codec, então entra até quem não tem decoder de vídeo.
- Opus 48 kHz estéreo com quadros de 10 ms (20 ms até a 1.1.0), modo "audio" e VBR restrito, de 128 a 320 kbps (acima disso o pacote passa do MTU do Radmin).
- Cada pacote leva o quadro atual e o anterior: uma perda isolada é recuperada sem buraco no som.
- O viewer começa com 40 ms de buffer e só aumenta (até 160 ms) se o som falhar, com correção suave do atraso e do relógio. O player mostra o espectro do som decodificado.
- **Um app só** (1.1.1): em "O que toca na festa" dá para escolher um app (o Spotify, por exemplo). Aí vai só o som dele, capturado pelo loopback por processo ("só este processo e os filhos"). Quem decide os processos é o `AppAudioPlan.ChooseOnly`: o executável escolhido, nunca o ScreenFlow nem os ancestrais dele.
- A descoberta anuncia o modo (tela, câmera ou festa), e a tela Assistir mostra cada sessão com o modo.

## Som do PC

O som do PC é capturado por WASAPI e vira Opus 48 kHz estéreo. Desde a 1.1.0 ele é separado por app (Windows 10 2004 ou mais novo), para que a voz da rede, os avisos e qualquer som que o próprio ScreenFlow toca não voltem para quem assiste:

- **Sem apps escolhidos**: uma captura de loopback por processo (`ActivateAudioInterfaceAsync` no dispositivo virtual `VAD\Process_Loopback`) no modo "tudo menos a árvore do ScreenFlow".
- **Com apps escolhidos** (Spotify, Discord...): o Windows só tira uma árvore de processos por captura, então cada app que tem sessão de som é capturado separado ("só este processo e os filhos") e a mistura é feita no ScreenFlow. Quem decide quais processos capturar é o `AppAudioPlan` (no Core, com testes): o ScreenFlow e os apps escolhidos nunca entram; os ancestrais deles também não (capturar a árvore do Explorer traria o som deles junto); e um processo cujo ancestral já entra fica de fora, para não dobrar o volume. A lista é revista a cada segundo. O ritmo da mistura vem de uma captura "tudo menos o ScreenFlow", e cada app tem uma fila com 20 ms de folga e no máximo 120 ms guardados.
- **Reserva**: se o Windows não deixar separar, vai o loopback normal da saída padrão (todo o som do PC) e a tela Transmissão avisa.

A escolha dos apps vale na hora, durante a transmissão. Os apps são identificados pelo nome do executável, então um app que toca por outro processo com outro nome (o `steamwebhelper` do Steam, por exemplo) aparece separado na lista.

## Controles de mídia (1.1.1)

Transmitindo uma janela, ou a festa de um app só, a tela Transmissão mostra a música ou o vídeo desse app pelos controles de mídia do Windows (`GlobalSystemMediaTransportControlsSessionManager`, os mesmos das teclas de mídia): capa, título, artista, tempo e os botões voltar, pausar e pular, além da barra para mudar o tempo quando o app deixa. O app de mídia é ligado ao executável da janela ou da festa pelo `MediaApps` (no Core, com testes: "Spotify.exe", "Chrome", ids da Microsoft Store e os ids em hash do Firefox). Sem um app ligado, aparece o que as teclas de mídia controlariam. A lista é relida quando um app avisa que mudou e a cada 2 segundos; entre os avisos o tempo anda com o relógio.

## Controles de quem assiste (1.2.0)

Pelo player, quem assiste troca a qualidade, controla a música ou o vídeo e tira apps do som. Tudo vale para a transmissão inteira, como se quem transmite tivesse feito, e a tela Transmissão mostra um aviso com o nome de quem pediu.

- **Quem entende**: o viewer anuncia `remote-control` no `hello` e o host anuncia `remote-control` no `welcome`. Só entre os dois as mensagens novas passam; um viewer ou um host 1.1.x continua conectando como antes e não vê nada disso.
- **Host → viewer**: `remote` (o que dá para fazer, as resoluções e os FPS oferecidos, ou os pares da câmera, e a qualidade de agora), `media` (app, título, artista, tocando ou não, tempo, duração, o que o app deixa fazer e a capa num JPEG de até 160 px) e `apps` (os apps com som no PC, sem ícones, relidos a cada 6 s enquanto há alguém com os controles).
- **Viewer → host**: `setquality`, `mediacmd` (voltar, pausar, pular, ir para um ponto) e `setapps` (a lista inteira de apps que ficam fora).
- **O host confere tudo**: o `RemoteControl` (no Core, com testes) só aceita uma qualidade da lista oferecida, um comando que o app deixa e um ponto dentro da música, e cada pessoa tem um `RequestLimiter` (8 pedidos seguidos e depois 2 por segundo). O que passa é aplicado na tela Transmissão pelos mesmos caminhos dos botões de quem transmite: `ChangeQuality`, os controles de mídia do Windows e a lista de apps tirados. Quem assiste só mexe nos apps da lista publicada; um app que quem transmite tirou e não está na lista continua fora.
- **Privacidade**: a música só vai para quem assiste se o som dela vai junto. Um app tirado do som, ou fora da festa de um app só, não aparece e não é controlado.
- **Pouco tráfego**: a mídia é mandada de novo só quando muda a música ou o estado, quando a posição foge do relógio por mais de 2 s (alguém mudou o ponto) ou a cada 15 s; entre uma e outra o player anda o tempo sozinho. Os textos são cortados em 160 caracteres e a capa em 64 KB. Quem entra recebe o estado de agora, com o tempo já adiantado.
- **Desligar**: com **Quem assiste pode mexer nos controles** desligado, o host manda `remote` sem nada liberado, o player esconde os controles e todo pedido é recusado.
- **Versões futuras**: um tipo de mensagem desconhecido vira `UnknownControlMessage` e é ignorado, em vez de derrubar a conexão. Um tipo conhecido com um campo errado continua sendo erro.

## Placa de vídeo (1.2.0)

- As placas vêm do DXGI, cada adaptador lido à parte (um adaptador virtual que falha não esconde os outros), e da lista de dispositivos de vídeo do Windows (SetupAPI e cfgmgr32). Uma placa que o DXGI não mostra porque o Windows parou o driver (código 43), está sem driver (28), com o driver que não carregou (31 e 39), desativada (22) ou esperando reiniciar (14) aparece mesmo assim, com o motivo. Sem o driver do fabricante, o Windows usa o "Vídeo Básico da Microsoft" (o serviço `BasicDisplay`) e não sabe o modelo: a placa aparece como "Placa NVIDIA sem driver", por exemplo, com o motivo.
- A versão do driver vem do DXGI ou do registro da classe de vídeo; na NVIDIA ela é mostrada como no site ("31.0.15.5222" vira 552.22).
- A placa principal (`GpuInfo.Main`) é a de verdade com mais memória: as virtuais (Parsec, monitores virtuais, área de trabalho remota) e o vídeo básico ficam por último, e uma placa com problema continua sendo a principal (para o motivo aparecer). Uma NVIDIA ou AMD com problema que não informa a memória conta como uma dedicada de 4 GB, para não perder para a integrada.
- Só os encoders dos fabricantes presentes são testados (uma placa com problema também conta, porque o encoder dela pode funcionar mesmo assim). Enquanto cada um abre, o `FFmpegLog` guarda os erros e avisos do FFmpeg (no máximo 24 linhas, fora do caminho do vídeo), e o `GpuInfo.EncoderHint` transforma isso no motivo: driver da NVIDIA antigo para o NVENC (com a versão mínima), NVENC ocupado por outro app, driver que não carregou o NVENC, AMF ou Quick Sync.
- O cartão da Visão geral mostra a placa logo (sem esperar os encoders) e depois o encoder ou o motivo. O Diagnóstico lista cada placa com o driver, qual é a principal, o problema e a mensagem de cada encoder que não abriu.

## Desempenho da sessão (1.2.0)

Enquanto uma transmissão ou um player está no ar, o `SessionPerformance` pede ao Windows o timer de 1 ms (`timeBeginPeriod`), tira o processo do modo de eficiência do Windows 11 (EcoQoS, que com a janela minimizada ou na bandeja atrasava o ritmo de envio e ignorava o timer de 1 ms), sobe a prioridade do processo para acima do normal e põe o coletor de lixo no modo de baixa latência. Tudo volta ao normal quando a última sessão acaba.

## Sala da rede (UDP 47802)

Todo mundo com o ScreenFlow 1.1 aberto na mesma rede do Radmin se vê, conversa no chat e pode entrar na voz. Não há servidor: tudo passa pela porta de descoberta, que o instalador já libera.

- **Pacotes** binários com cabeçalho de 20 bytes (`SR`, versão, tipo e o id de quem mandou) e no máximo 1400 bytes: presença, saída, chat, confirmação do chat, voz e pedido de histórico. Os anúncios em JSON das transmissões das versões 1.0.x chegam na mesma porta e alimentam a tela Assistir.
- **Presença** por broadcast nos adaptadores do Radmin a cada 2 s (e direto para quem já é conhecido ou foi adicionado pelo IP), com o nome, se está na voz, mudo, sem som e se está ao vivo (tela, som, câmera). Quem some por 7 s sai da lista.
- **Chat** direto para cada PC, com confirmação e até 6 tentativas. O histórico guarda as últimas 300 mensagens só na memória; quem chega pede o histórico aos outros.
- **Voz** direto para quem está na voz: Opus 32 kbps, 48 kHz mono, quadros de 20 ms, com portão de voz (só manda enquanto você fala) e cada quadro levando o anterior para cobrir uma perda isolada. Cada pessoa tem um buffer de jitter (de 40 a 240 ms) e todas são mixadas numa saída só. Sem som (ensurdecer) também desliga o microfone. **Ouvir meu microfone** (Configurações) toca o microfone no fone com um buffer curto, sem passar pela rede.
- **Perfil** (1.1.1): o nome vem de **Seu perfil** (no começo, o nome do PC). A foto vai na presença como uma referência (id e tamanho) num campo novo no fim do pacote, que as versões antigas ignoram; quem vê uma foto que ainda não tem baixa de quem anunciou.
- **Arquivos** (1.1.1): as fotos de perfil (JPEG 192×192, até 96 KB) e as imagens do chat (JPEG de até 1920 px, em torno de 600 KB, no máximo 2 MB) passam pela mesma porta. O id de um arquivo são os 16 primeiros bytes do SHA-256 do conteúdo. Quem quer pede (`BlobRequest`) tudo ou só as partes que faltam (até 600 por pedido); quem tem manda partes (`BlobChunk`) de 1200 bytes, em lotes, só para quem está na sala e no endereço de onde ela fala, com limite de pedidos por pessoa e de envios ao mesmo tempo (um pedido pequeno não vira uma enxurrada para outro endereço). O `RoomBlobDownloads` (no Core, com testes) pede de novo o que falta depois de 400 ms sem partes novas, com espera crescente; depois de 5 tentativas sem nada passa para a próxima pessoa que tem o arquivo (o autor da mensagem, quem repassou o histórico, os outros da sala) e desiste em 60 s. No fim, o hash precisa conferir. Os arquivos ficam em `%LOCALAPPDATA%\ScreenFlow\imagens` (`RoomBlobStore`: gravação atômica, conferência do hash ao ler, 24 MB na memória, limpeza do que não é usado há 30 dias ou passa de 300 MB, a sua foto fica sempre).
- **Imagens no chat**: a mensagem leva a imagem (id, tamanho, largura e altura) num campo novo; o texto vira a legenda. Sem legenda, o texto é "📷 Imagem (atualize o ScreenFlow para ver)", o que as versões 1.1.0 mostram. Quem manda abre o arquivo (qualquer formato que o Windows abre), vira conforme o EXIF, diminui e grava em JPEG (o que tira a localização e os outros dados da foto). Quem recebe só abre JPEG ou PNG, confere o tamanho antes (até 8192 de lado e 24 milhões de pixels) e abre já diminuído para mostrar.
- **Segurança**: os pacotes da sala **não** são criptografados nem autenticados pelo ScreenFlow. Eles contam com o túnel do Radmin VPN, e qualquer um na mesma rede do Radmin com o ScreenFlow aberto ouve a voz, lê o chat e vê as imagens. A transmissão de tela continua com ECDH + AES-GCM.

## Instalar o Radmin VPN (1.1.1)

Sem o Radmin VPN, a Visão geral e a Transmissão oferecem **Instalar o Radmin VPN**. O `RadminInstaller` lê a página oficial e pega o link do instalador mais novo (`RadminDownload`, no Core, com testes: só HTTPS, só os endereços da Famatech e só um arquivo `Radmin_VPN_x.y.z.exe`; se a página mudar, usa a última versão conhecida). Baixa com barra de progresso, confere a assinatura Authenticode com o `WinVerifyTrust` (arquivo íntegro, certificado confiável) e exige que quem assinou seja a Famatech; se não conferir, apaga o arquivo. Depois roda o instalador (Inno Setup) com `/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SP-` como administrador (o Windows pede permissão), explica o código de saída e abre o Radmin VPN para a pessoa entrar numa rede. Com o Radmin já instalado e sem rede, o botão vira **Abrir o Radmin VPN** (o programa é achado pelo registro de desinstalação).

## Canal de controle (TCP 47801)

Mensagens JSON com 4 bytes de tamanho.

1. Viewer → `hello` (versão do protocolo, decoders, limites de tela, porta UDP, chave pública ECDH)
2. Host → `challenge` (chave pública, nonce, se pede PIN)
3. Viewer → `auth` (HMAC derivado do segredo ECDH + PIN)
4. Host → `pending` (se aprovação manual) → `welcome` (salt, porta de mídia, perfil) ou `rejected`
5. Depois: `ping`/`pong` (relógio e RTT), `stats` (perda, RTT, banda, FPS decodificado, latência), `nack`, `keyframe`, `config`, `bwtest`/`bwresult`, `bye`; e, entre os que anunciam `remote-control`, `remote`, `media`, `apps`, `setquality`, `mediacmd` e `setapps`.

## Qualidade adaptativa e vários viewers

- O codec é o melhor comum a todos os viewers (AV1 > HEVC > H.264).
- O host usa a **pior** amostra entre os viewers: perda > 2% ou RTT 40 ms acima da base reduz bitrate (até 50% do perfil), depois FPS, depois resolução. Cinco segundos estáveis sobem um degrau.
- O bitrate por viewer nunca passa de 80% do upload medido dividido pelo número de viewers.
- O NVENC muda bitrate sem reiniciar; outras mudanças recriam o encoder com um keyframe.

## Firewall

O instalador cria as regras (UDP 47800, TCP 47801, UDP 47802 e o próprio `ScreenFlow.exe`) uma vez só, com os mesmos nomes que o app usa, e as remove na desinstalação. O app confere as regras ao abrir e só pede permissão de novo (pelo sininho) se as portas mudarem.

## Bandeja e encerramento

- Fechar ou minimizar a janela (com **Configurações › Fechar e minimizar levam o ScreenFlow para a bandeja**, ligado por padrão) só esconde a janela: a transmissão, a voz e o chat continuam. Quando a transmissão acaba ou o player fecha, a janela volta sozinha (dá para desligar nas Configurações). A dica do ícone mostra se está ao vivo e o que está indo (tela, som, câmera), quantas pessoas assistem e a voz; o menu do botão direito tem abrir, transmitir ou encerrar, os controles da voz, o chat e **Sair do ScreenFlow**.
- Só uma cópia do app roda por vez (mutex `Local\ScreenFlow.Instancia`). Abrir de novo mostra a janela que já estava aberta.
- **Sair do ScreenFlow** (ou fechar com a bandeja desligada, ou o Windows desligando) encerra a sessão (manda `bye`, para áudio, pipeline, sockets, libera encoder/decoder/texturas/device D3D11), sai da sala da rede e só então fecha.
