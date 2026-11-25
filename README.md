# ScreenFlow

Compartilhe a tela **e o áudio do sistema** do seu PC com várias pessoas ao mesmo tempo pela rede virtual do **Radmin VPN**. Quem assiste só digita o seu IP do Radmin (26.x.x.x) e a conexão acontece sozinha. A transmissão chega a **3840×2160 a 165 FPS** quando a GPU e a rede aguentam.

- Captura na GPU (Windows.Graphics.Capture, com DXGI Desktop Duplication de reserva), sem cópia para a CPU até o encoder.
- Encoder por hardware detectado sozinho: NVENC (NVIDIA), AMF (AMD) ou Quick Sync (Intel), na ordem AV1 → HEVC → H.264.
- Áudio do sistema por WASAPI loopback, em Opus 48 kHz estéreo.
- Transporte UDP próprio com pacotes numerados, FEC, retransmissão seletiva e qualidade adaptativa.
- Um PC transmitindo para 10, 20 ou mais pessoas: a tela é codificada uma vez só.
- Configuração automática da rede e do PC, e atualização direto pelo app.

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

Baixe `ScreenFlow-Setup-win-x64.exe` na página de [Releases](https://github.com/KaykyFerro/SCREENFLOW-REMOTE/releases) e instale. Não precisa de administrador.

Na primeira vez o app:
- pede permissão do Windows para liberar as portas no Firewall (clique em **Liberar portas**);
- detecta a GPU, os codecs, o monitor e o Radmin e escolhe a melhor qualidade sozinho.

### 3. Transmitir

1. Clique em **Transmitir**.
2. O seu IP do Radmin aparece em destaque. Clique no ícone de copiar e mande para quem vai assistir.
3. Escolha o monitor ou a janela, a qualidade (720p, 1080p, 1440p, 4K), o FPS (30, 60, 120, 144, 165) e se quer o áudio do sistema.
4. Opcional: defina um **PIN** e/ou deixe ligado **Aprovar cada pessoa**. Qualquer pessoa na mesma rede Radmin alcança o seu IP.
5. Clique em **Começar a transmitir**. A captura e o encoder só ligam quando alguém entra.
6. Na lista **Quem está assistindo** você vê ping, latência, perda e banda de cada pessoa, pode **Testar banda** e remover alguém.

### 4. Assistir

1. Clique em **Assistir**.
2. Digite o IP do Radmin de quem transmite (o app lembra os últimos) e clique em **Conectar**. Ou dê dois cliques num host da lista **Transmissões na sua rede Radmin**.
3. Se o host pedir PIN, o app pergunta. Se ele aprovar cada pessoa, você vê "Aguardando aprovação".
4. No player: **duplo clique** ou **F11** para tela cheia, **F3** para as estatísticas (FPS real, bitrate, latência, perda, codec, resolução), **M** para mudo e as setas para o volume.
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

As portas podem ser trocadas em **Configurações › Rede** (os dois lados precisam usar as mesmas). O app cria as regras de entrada do Firewall do Windows com o seu consentimento.

## Segurança

- Troca de chaves ECDH P-256 a cada conexão e criptografia AES-GCM de todo pacote de mídia.
- PIN opcional: a prova do PIN é derivada do segredo da conexão, então quem só escuta a rede não consegue testar PINs.
- Modo **Aprovar cada pessoa** e limite de tentativas erradas (5 por minuto por IP).

## Diagnóstico

Em **Configurações › Diagnóstico** (ou `ScreenFlow.exe --diagnostico` no terminal) aparecem: IP do Radmin e adaptadores, portas e firewall, GPUs, encoders e decoders por hardware, versão do FFmpeg e o perfil recomendado. O teste de banda até um viewer fica na tela Transmitir.

Logs ficam em `%LOCALAPPDATA%\ScreenFlow\logs` (botão **Abrir logs**).

## Atualizações

O app procura uma versão nova ao abrir (dá para desligar) e em **Configurações › Atualizações**. Ao clicar em **Atualizar agora**, ele baixa o instalador do último release, confere o SHA-256, instala em silêncio e reabre.

Para publicar uma versão: `git tag v1.1.0 && git push origin v1.1.0`. O workflow `release.yml` compila, gera o instalador e o `.sha256` e cria o release.

## Solução de problemas

| Problema | O que fazer |
|---|---|
| "Radmin VPN não encontrado" | Instale o Radmin VPN e entre numa rede. O IP 26.x precisa aparecer no Radmin. |
| Viewer não conecta | Os dois na mesma rede do Radmin? O host clicou em Começar? No host, crie as regras do firewall em Configurações › Rede. Tente `ping 26.x.x.x`. |
| Host não aparece na lista | A descoberta usa broadcast; algumas redes bloqueiam. Digite o IP direto. |
| Imagem trava ou pixeliza | Abra as estatísticas (F3). Perda acima de 2% indica rede saturada: baixe a qualidade ou o FPS, ou use **Testar banda**. Se o Radmin estiver passando pelo relay (ícone de conexão indireta no Radmin), a banda cai bastante. |
| FPS abaixo do escolhido | Veja o encoder em Diagnóstico. Sem NVENC/AMF/QSV o app usa software. Notebooks: conecte na tomada e use a GPU dedicada. |
| Sem áudio | Ligue **Áudio do sistema** antes de começar. Confira o volume no player (M e setas). |
| "Versões diferentes do ScreenFlow" | Atualize os dois PCs para a mesma versão. |

Mais detalhes em [docs/TROUBLESHOOTING.md](docs/TROUBLESHOOTING.md).

## Compilar

```powershell
./scripts/fetch-ffmpeg.ps1          # baixa as DLLs do FFmpeg 7.1 (LGPL) para third_party/
dotnet test ScreenFlow.sln
dotnet run --project src/ScreenFlow.App
```

Detalhes em [docs/DEVELOPMENT.md](docs/DEVELOPMENT.md) e arquitetura em [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md).

## Licenças de terceiros

FFmpeg (build LGPL de [BtbN/FFmpeg-Builds](https://github.com/BtbN/FFmpeg-Builds)), FFmpeg.AutoGen, Vortice.Windows e NAudio (MIT). As DLLs do FFmpeg são distribuídas sem modificação na pasta `ffmpeg`, junto com a licença.
