# ScreenFlow

Compartilhe a tela, a câmera **e o áudio do sistema** do seu PC com várias pessoas ao mesmo tempo pela rede virtual do **Radmin VPN**. Quem assiste só digita o seu IP do Radmin (26.x.x.x) ou clica na transmissão que aparece na tela Assistir. A transmissão chega a **3840×2160 a 165 FPS** quando a GPU e a rede aguentam.

Três jeitos de transmitir:

- **Tela**: um monitor ou uma janela, até 4K a 165 FPS, com o som do PC.
- **Câmera**: webcam ou placa de captura, com o som do PC. A câmera só liga quando alguém entra.
- **Festa de música**: só o som do PC, sem vídeo, em Opus estéreo de até 320 kbps com cópia de segurança de cada pacote. Quem entra vê um visualizador que dança com a música.

Por baixo:

- Captura na GPU (Windows.Graphics.Capture, com DXGI Desktop Duplication de reserva), sem cópia para a CPU até o encoder.
- Encoder por hardware detectado sozinho: NVENC (NVIDIA), AMF (AMD) ou Quick Sync (Intel), na ordem AV1 → HEVC → H.264.
- Áudio do sistema por WASAPI loopback, em Opus 48 kHz estéreo.
- Transporte UDP próprio com pacotes numerados, FEC, retransmissão seletiva e qualidade adaptativa.
- Um PC transmitindo para 10, 20 ou mais pessoas: a tela é codificada uma vez só.
- Player que desenha o vídeo direto na GPU (swapchain D3D11), com o caminho pela CPU de reserva.
- Configuração automática da rede e do PC, portas liberadas no firewall já na instalação, e atualização direto pelo app com aviso no sininho.

## Requisitos

| | Mínimo | Para 4K a 165 FPS |
|---|---|---|
| Sistema | Windows 10 21H2 (x64) | Windows 11 |
| GPU de quem transmite | Qualquer GPU com encoder (GTX 10xx, RX 400, Intel 7ª geração) | NVIDIA RTX 40/50, AMD RX 7000/9000 ou Intel Arc |
| GPU de quem assiste | Qualquer GPU com decodificação HEVC | GPU com decodificação AV1 ou HEVC por hardware |
| Upload de quem transmite | 20 Mbps por pessoa em 1080p60 | ~120 Mbps **por pessoa** |

Sem encoder por hardware o app usa OpenH264 por software como último recurso e avisa: 4K a 165 FPS não roda assim.

## Passo a passo

### 1. Instalar o Radmin VPN e entrar na mesma rede

1. Baixe e instale o Radmin VPN em <https://www.radmin-vpn.com/> nos dois PCs.
2. Num PC, clique em **Rede › Criar rede**, dê um nome e uma senha.
3. Nos outros, clique em **Rede › Entrar numa rede existente** com o mesmo nome e senha.
4. Cada PC ganha um IP 26.x.x.x, que aparece no Radmin e no rodapé do ScreenFlow.

### 2. Instalar o ScreenFlow

Baixe `ScreenFlow-Setup-win-x64.exe` na página de [Releases](https://github.com/KaykyFerro/SCREENFLOW-REMOTE/releases) e instale.

- O instalador pede permissão do Windows **uma vez** e já libera as portas no Firewall. Se você mudar as portas depois, o sininho avisa e o botão **Liberar portas** resolve.
- Na primeira vez o app detecta a GPU, os codecs, o monitor e o Radmin e escolhe a melhor qualidade sozinho.

### 3. Transmitir

1. Na **Visão geral**, escolha **Tela**, **Câmera** ou **Festa de música**, a qualidade, e clique em **Iniciar transmissão** (ou abra **Transmissão** no menu para todas as opções).
2. O seu IP do Radmin aparece em destaque. Clique no ícone de copiar e mande para quem vai assistir.
3. Na tela: o monitor ou a janela, a qualidade (720p, 1080p, 1440p, 4K), o FPS (30, 60, 120, 144, 165) e o som do PC. Na câmera: qual câmera e a qualidade. Na festa: a qualidade do som (128 a 320 kbps).
4. Opcional: defina um **PIN** e/ou deixe ligado **Aprovar cada pessoa**. Qualquer pessoa na mesma rede Radmin alcança o seu IP.
5. A captura e o encoder só ligam quando alguém entra. O visualizador do som já mexe antes, para você conferir o áudio.
6. Em **Quem está assistindo** você vê ping, latência, perda e banda de cada pessoa, pode **Testar banda** e remover alguém. O **Monitoramento** mostra os gráficos dos últimos 90 segundos.

### 4. Assistir

1. Clique em **Assistir**.
2. Clique numa transmissão em **Na sua rede** (mostra se é tela, câmera ou festa, quantas pessoas estão assistindo e se pede PIN), ou digite o IP do Radmin de quem transmite (o app lembra os últimos) e clique em **Conectar**.
3. Se o host pedir PIN, o app pergunta. Se ele aprovar cada pessoa, você vê "Esperando aprovação".
4. No player: **duplo clique**, **F** ou **F11** para tela cheia, **F3** para as estatísticas (FPS, bitrate, latência, perda, codec, resolução, GPU ou CPU), **M** para mudo e as setas para o volume.
5. Se a conexão cair, o player reconecta sozinho.

## Várias pessoas assistindo

O vídeo é capturado e codificado **uma vez**, mas cada pessoa recebe a própria cópia criptografada. Por isso o limite é o **upload** de quem transmite:

| Qualidade | Por pessoa | 10 pessoas |
|---|---|---|
| 1080p 60 FPS | ~15–20 Mbps | ~150–200 Mbps |
| 1440p 144 FPS | ~50 Mbps | ~500 Mbps |
| 4K 60 FPS | ~45–50 Mbps | ~450–500 Mbps |
| 4K 165 FPS | ~120 Mbps | ~1,2 Gbps |

O app divide 80% do upload medido entre quem está assistindo e baixa a qualidade sozinho (bitrate, depois FPS, depois resolução) quando não cabe. Para medir o upload, clique em **Testar banda** ao lado de alguém conectado. O padrão é até 20 pessoas; dá para mudar em **Configurações › Rede**.

## Portas

| Porta | Protocolo | Uso |
|---|---|---|
| 47800 | UDP | Vídeo, áudio, FEC e teste de banda (host → viewer) |
| 47801 | TCP | Handshake, controle, NACK, estatísticas |
| 47802 | UDP | Descoberta por broadcast na rede do Radmin |

As portas podem ser trocadas em **Configurações › Rede e firewall** (os dois lados precisam usar as mesmas). O instalador cria as regras de entrada do Firewall do Windows para as portas padrão e para o ScreenFlow.exe.

## Segurança

- Troca de chaves ECDH P-256 a cada conexão e criptografia AES-GCM de todo pacote de mídia.
- PIN opcional: a prova do PIN é derivada do segredo da conexão, então quem só escuta a rede não consegue testar PINs.
- Modo **Aprovar cada pessoa** e limite de tentativas erradas (5 por minuto por IP).

## Diagnóstico

Em **Configurações › Diagnóstico** (ou `ScreenFlow.exe --diagnostico` no terminal) aparecem: IP do Radmin e adaptadores, portas e firewall, GPUs, encoders e decoders por hardware, câmeras, versão do FFmpeg e o perfil recomendado. O teste de banda até um viewer fica na tela Transmissão.

`ScreenFlow.exe --autoteste` abre uma festa de música no 127.0.0.1 com um tom de teste, conecta um viewer nela e confere se o som chega e é decodificado (o CI roda isso a cada mudança).

Logs ficam em `%LOCALAPPDATA%\ScreenFlow\logs` (botão **Abrir logs**).

## Atualizações

O app procura uma versão nova ao abrir e a cada 6 horas (dá para desligar em **Atualizações**). Quando sai uma, o **sininho** da barra de cima avisa. Ao clicar em **Atualizar agora**, ele baixa o instalador do último release, confere o SHA-256, instala em silêncio e reabre.

Para publicar uma versão, crie um release no GitHub com a tag nova `v1.1.0` na `main` (ou rode `git tag v1.1.0 && git push origin v1.1.0`). O workflow `release.yml` compila, gera o instalador e o `.sha256` e anexa ao release.

## Solução de problemas

| Problema | O que fazer |
|---|---|
| "Radmin VPN não encontrado" | Instale o Radmin VPN e entre numa rede. O IP 26.x precisa aparecer no Radmin. |
| Viewer não conecta | Os dois na mesma rede do Radmin? O host clicou em Iniciar? No host, confira o firewall em Configurações › Rede e firewall. Tente `ping 26.x.x.x`. |
| Host não aparece na lista | A descoberta usa broadcast; algumas redes bloqueiam. Digite o IP direto. |
| Imagem trava ou pixeliza | Abra as estatísticas (F3). Perda acima de 2% indica rede saturada: baixe a qualidade ou o FPS, ou use **Testar banda**. Se o Radmin estiver passando pelo relay (ícone de conexão indireta no Radmin), a banda cai bastante. |
| FPS abaixo do escolhido | Veja o encoder em Diagnóstico. Sem NVENC/AMF/QSV o app usa software. Notebooks: conecte na tomada e use a GPU dedicada. |
| Sem áudio | Ligue **Compartilhar o som do PC** antes de começar (na festa de música ele já vem ligado). Confira o volume no player (M e setas). |
| Câmera não aparece | Feche outros apps que usam a câmera e clique em atualizar ao lado da lista. |
| "Versões diferentes do ScreenFlow" | Atualize os dois PCs para a mesma versão. |

Mais detalhes em [docs/TROUBLESHOOTING.md](docs/TROUBLESHOOTING.md).

## Compilar

```powershell
./scripts/fetch-ffmpeg.ps1          # baixa as DLLs do FFmpeg 7.1 (LGPL) para third_party/
dotnet test ScreenFlow.sln
dotnet run --project src/ScreenFlow.App
```

`ScreenFlow.exe --capturas <pasta>` salva um PNG de cada tela com dados de exemplo (o CI anexa as capturas em cada execução).

Detalhes em [docs/DEVELOPMENT.md](docs/DEVELOPMENT.md) e arquitetura em [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md).

## Licenças de terceiros

FFmpeg (build LGPL de [BtbN/FFmpeg-Builds](https://github.com/BtbN/FFmpeg-Builds)), FFmpeg.AutoGen, Vortice.Windows e NAudio (MIT). As DLLs do FFmpeg são distribuídas sem modificação na pasta `ffmpeg`, junto com a licença. Fonte [Inter](https://rsms.me/inter/) (SIL Open Font License), embutida só com os caracteres latinos; a licença vai em `licenses/Inter-OFL.txt`.
