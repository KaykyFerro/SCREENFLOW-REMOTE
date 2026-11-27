# ScreenFlow

Compartilhe a tela, a câmera **e o áudio do sistema** do seu PC com várias pessoas ao mesmo tempo pela rede virtual do **Radmin VPN**. Quem assiste só digita o seu IP do Radmin (26.x.x.x) ou clica na transmissão que aparece na tela Assistir. A transmissão chega a **3840×2160 a 165 FPS** quando a GPU e a rede aguentam.

Três jeitos de transmitir:

- **Tela**: um monitor ou uma janela, até 4K a 165 FPS, com o som do PC. Dá para mandar a **câmera junto**: quem assiste vê a sua webcam num quadrinho no canto, com tela, câmera e som ao mesmo tempo.
- **Câmera**: webcam ou placa de captura, com o som do PC. A câmera só liga quando alguém entra.
- **Festa de música**: o som do PC (ou só o de um app, como o Spotify), sem vídeo, em Opus estéreo de até 320 kbps com cópia de segurança de cada pacote. Quem entra vê um visualizador que dança com a música.

Transmitindo uma janela ou a festa de um app só, um cartão na Transmissão controla a música ou o vídeo desse app: voltar, pausar, pular e mudar o tempo. A qualidade pode ser trocada com a transmissão no ar, ou ficar fixa, sem adaptar.

Quem assiste também mexe, pelo próprio player: troca a qualidade, volta, pausa e pula a música ou o vídeo que toca no PC de quem transmite e tira o som de um app da transmissão. Vale para todo mundo que está assistindo, quem transmite vê um aviso com o nome de quem pediu e uma chave desliga tudo isso.

O volume do PC de quem transmite não muda o som que vai: abaixar ou tirar o som do Windows só muda o que essa pessoa ouve, e cada um controla o próprio volume no player. O player aparece como **player de mídia do Windows** (no painel do volume, na tela de bloqueio e nas teclas de mídia), com a música da festa e a capa.

E para quem está na mesma rede do Radmin:

- **Sala da rede**, como um servidor do Discord: canais de texto (#geral, #música, #prints-e-clipes e os que você criar) e canais de voz (Geral, Jogando, Música e os seus), com quem está em cada canal de voz. O painel **Voz conectada** fica no menu lateral, logo acima do seu perfil: entrar, sair, desligar o microfone ou o som de todo mundo com um clique.
- **Volume de cada pessoa só para você**: botão direito em alguém da voz, de 0 a 200%, ou silenciar essa pessoa só para você.
- **Chat com imagens** em cada canal de texto: anexe, cole com Ctrl+V ou arraste. Se quiser, as conversas ficam **guardadas no Google Drive** (ou em qualquer pasta) e voltam quando o app abre.
- **Seu perfil**: o seu nome (no lugar do nome do PC) e uma foto, que aparecem no chat, na voz e para quem assiste.
- **Som do PC sem a voz da rede**: quem assiste a sua transmissão não ouve a conversa da voz nem os sons do ScreenFlow, e você escolhe outros apps para ficarem de fora (Spotify, Discord, o navegador...).
- **Bandeja do Windows**: fechar ou minimizar leva o ScreenFlow para perto do relógio. Passando o mouse no ícone aparece se você está ao vivo e o que está indo (tela, som, câmera ou tudo junto).

Por baixo:

- Captura na GPU (Windows.Graphics.Capture, com DXGI Desktop Duplication de reserva), sem cópia para a CPU até o encoder.
- Encoder por hardware detectado sozinho: NVENC (NVIDIA), AMF (AMD) ou Quick Sync (Intel), na ordem AV1 → HEVC → H.264.
- Som do PC por WASAPI, em Opus 48 kHz estéreo. O loopback por processo do Windows deixa o próprio ScreenFlow e os apps escolhidos de fora.
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

1. Instale o Radmin VPN nos dois PCs. Sem ele, o próprio ScreenFlow oferece **Instalar o Radmin VPN** (na Visão geral e na Transmissão): baixa do site oficial, confere a assinatura digital da Famatech e instala sem janelas, depois de você aceitar o pedido de permissão do Windows. Também dá para baixar em <https://www.radmin-vpn.com/>.
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
3. Na tela: o monitor ou a janela, a qualidade (720p, 1080p, 1440p, 4K), o FPS (30, 60, 120, 144, 165), o som do PC e, se quiser, **Mandar a câmera junto**. Na câmera: qual câmera e a qualidade. Na festa: **O que toca na festa** (o som do PC todo ou só um app) e a qualidade do som (128 a 320 kbps). A qualidade pode ser trocada no meio da transmissão; com **Qualidade fixa** ligada, ela não baixa sozinha quando a rede aperta.
4. Transmitindo uma janela (ou a festa com um app só), aparece um cartão com o que está tocando nesse app: capa, título e tempo da música ou do vídeo, com voltar, pausar, pular e a barra para mudar o tempo. Funciona com o que aparece nos controles de mídia do Windows (Spotify, YouTube no navegador, Reprodutor de Mídia...).
5. No cartão **Som do PC**, o botão **Escolher apps** mostra os apps abertos: marque os que quem assiste não deve ouvir. A voz da rede e os sons do ScreenFlow já ficam de fora. Vale na hora, até no meio da transmissão, e o app lembra a escolha.
6. Com **Quem assiste pode mexer nos controles** ligado (o padrão), quem assiste troca a qualidade, controla a música ou o vídeo e tira apps do som pelo player, e cada pedido aparece num aviso com o nome de quem pediu. Desligue para ninguém mexer; dá para trocar com a transmissão no ar. O que toca num app tirado do som (ou fora da festa de um app só) não aparece para quem assiste.
7. Já vem liberado: quem está na sua rede do Radmin entra direto, sem pedir permissão. Se quiser, defina um **PIN** e/ou ligue **Aprovar cada pessoa** em **Configurações › Transmissão**. Qualquer pessoa na mesma rede Radmin alcança o seu IP.
8. A captura e o encoder só ligam quando alguém entra. O visualizador do som já mexe antes, para você conferir o áudio.
9. Em **Quem está assistindo** você vê ping, latência, perda e banda de cada pessoa, pode **Testar banda** e remover alguém. O **Monitoramento** mostra os gráficos dos últimos 90 segundos.
10. Pode fechar ou minimizar a janela: o ScreenFlow vai para a bandeja e a transmissão continua. Quando a transmissão acaba (ou o player fecha), a janela volta sozinha; dá para desligar isso em **Configurações › Geral**. Para fechar de vez, clique com o botão direito no ícone e escolha **Sair do ScreenFlow**.

### 4. Assistir

1. Clique em **Assistir**.
2. Clique numa transmissão em **Na sua rede** (mostra se é tela, câmera ou festa, quantas pessoas estão assistindo e se pede PIN), ou digite o IP do Radmin de quem transmite (o app lembra os últimos) e clique em **Conectar**.
3. Se o host pedir PIN, o app pergunta. Se ele aprovar cada pessoa, você vê "Esperando aprovação".
4. No player: **duplo clique**, **F** ou **F11** para tela cheia, **F3** para as estatísticas (FPS, bitrate, latência, perda, codec, resolução, GPU ou CPU), **M** para mudo e as setas para o volume (só o seu). As teclas de mídia do teclado e do fone tocam, pausam e pulam a música quando quem transmite deixa.
5. Se o host mandar a câmera junto, ela aparece num quadrinho no canto: clique nele para trocar de canto e **C** para esconder ou mostrar.
6. Se quem transmite deixar, a barra do player traz mais três controles, que valem para todo mundo que está assistindo:
   - **Qualidade**: a resolução e o FPS (ou a qualidade da câmera), entre os que quem transmite oferece, e se a qualidade fica fixa.
   - **Música ou vídeo**: o que está tocando no PC de quem transmite, com voltar, pausar e pular. Na festa de música aparece o cartão **Tocando agora**, com a capa, o tempo e a barra para mudar o ponto.
   - **Tirar apps do som**: os apps com som no PC de quem transmite; marque os que ninguém deve ouvir.
7. Se a conexão cair, o player reconecta sozinho.

### 5. Sala da rede (voz e chat, como no Discord)

1. Quem está com o ScreenFlow 1.1 ou mais novo aberto na mesma rede do Radmin aparece sozinho na **Sala da rede**: à esquerda ficam os canais de texto e de voz (com quem está em cada canal de voz), no meio a conversa do canal aberto e à direita quem está na rede.
2. Clique num canal de voz para entrar nele; já na voz, clicar em outro canal muda de canal sem desligar o microfone. Quem está num canal só ouve quem está no mesmo canal. O painel **Voz conectada**, no menu lateral, mostra o canal e quem está nele, com os botões do microfone e do fone (sem som, o microfone também desliga, como no Discord). Quem está falando fica com um anel verde.
3. Botão direito em alguém da voz: o volume dessa pessoa só para você (0 a 200%) e **Silenciar para mim**. Os outros continuam ouvindo normal, e o app lembra o volume de cada pessoa.
4. Os botões **+** ao lado de **Canais de texto** e **Canais de voz** criam um canal seu (até 6). Ele aparece para todo mundo enquanto você estiver na sala; o botão direito nele apaga. Quem está no ScreenFlow 1.2 ou mais antigo só vê o #geral e a voz Geral.
5. Cada mensagem vai para todo mundo da sala, no canal aberto, e quem acabou de abrir o app recebe as últimas mensagens dos outros. Cada canal tem a sua bolinha de mensagens novas.
6. Para mandar uma imagem, clique no botão de imagem ao lado da caixa de escrever, cole com **Ctrl+V** (um print, por exemplo) ou arraste o arquivo para a conversa. A imagem vira um JPEG de até 1920 pixels, sem a localização e os outros dados da foto. Clique numa imagem para abrir; o botão direito salva ou copia.
7. Para guardar as conversas, ligue **Guardar o chat no Google Drive** em **Configurações › Voz e sala**. Com o Google Drive para computador instalado, o app acha a pasta sozinho; sem ele, escolha qualquer pasta. Fica uma pasta **ScreenFlow Chat** com um arquivo de texto por canal e por dia, mais as imagens, e as conversas dos últimos dois meses voltam quando o ScreenFlow abre. Sem essa opção, as mensagens ficam só enquanto o app está aberto.
8. Microfone, sensibilidade, volume das vozes, o teste do microfone e **Ouvir meu microfone** (toca o seu microfone no fone, para você ouvir como a sua voz sai) ficam em **Configurações › Voz e sala**.
9. Seu nome e sua foto ficam em **Configurações › Geral** (ou clique no seu perfil, no canto do menu). A foto troca na hora para todo mundo da rede.
10. Não está vendo alguém? Adicione pelo IP do Radmin na lista de quem está na rede, à direita da Sala.

## Várias pessoas assistindo

O vídeo é capturado e codificado **uma vez**, mas cada pessoa recebe a própria cópia criptografada. Por isso o limite é o **upload** de quem transmite:

| Qualidade | Por pessoa | 10 pessoas |
|---|---|---|
| 1080p 60 FPS | ~15–20 Mbps | ~150–200 Mbps |
| 1440p 144 FPS | ~50 Mbps | ~500 Mbps |
| 4K 60 FPS | ~45–50 Mbps | ~450–500 Mbps |
| 4K 165 FPS | ~120 Mbps | ~1,2 Gbps |

O app divide 80% do upload medido entre quem está assistindo e baixa a qualidade sozinho (bitrate, depois FPS, depois resolução) quando não cabe. Para medir o upload, clique em **Testar banda** ao lado de alguém conectado. O padrão é até 20 pessoas; dá para mudar em **Configurações › Rede e diagnóstico**.

## Portas

| Porta | Protocolo | Uso |
|---|---|---|
| 47800 | UDP | Vídeo, áudio, FEC e teste de banda (host → viewer) |
| 47801 | TCP | Handshake, controle, NACK, estatísticas |
| 47802 | UDP | Descoberta das transmissões e sala da rede: presença, chat e voz |

As portas podem ser trocadas em **Configurações › Rede e diagnóstico** (os dois lados precisam usar as mesmas). O instalador cria as regras de entrada do Firewall do Windows para as portas padrão e para o ScreenFlow.exe.

## Segurança

- Troca de chaves ECDH P-256 a cada conexão e criptografia AES-GCM de todo pacote de mídia.
- PIN opcional: a prova do PIN é derivada do segredo da conexão, então quem só escuta a rede não consegue testar PINs.
- Modo **Aprovar cada pessoa** (vem desligado: quem está na rede entra direto) e limite de tentativas erradas (5 por minuto por IP).
- A voz e o chat da rede **não** são criptografados pelo ScreenFlow: eles contam com o túnel do Radmin VPN, que já é criptografado entre os PCs. Quem está na mesma rede do Radmin e tem o ScreenFlow aberto ouve a voz, lê o chat e vê as imagens e as fotos de perfil, e o nome de quem fala ou escreve não é verificado. Use uma rede com senha, só com quem você conhece. O chat guardado fica em texto simples na pasta escolhida (e na sua conta do Google Drive, quando é a pasta dele).
- Imagens e fotos chegam pela sala em partes e só aparecem se o conteúdo bater com o hash SHA-256 anunciado. O app só abre JPEG e PNG que vêm da rede, com o tamanho conferido antes, e quem manda uma imagem só responde a quem está na sala.
- O instalador do Radmin VPN baixado pelo app só roda se a assinatura digital for válida para o Windows e for da Famatech.

## Diagnóstico

Em **Configurações › Rede e diagnóstico** (ou `ScreenFlow.exe --diagnostico` no terminal) aparecem: IP do Radmin e adaptadores, portas e firewall, as placas de vídeo (com a versão do driver, qual é a principal e o problema que o Windows relata, se houver), encoders e decoders por hardware (e por que o da placa principal não abriu), câmeras, versão do FFmpeg e o perfil recomendado. O teste de banda até um viewer fica na tela Transmissão.

`ScreenFlow.exe --autoteste` confere, no próprio PC: uma festa de música no 127.0.0.1 (o som chega ao viewer e é decodificado) e, nela, os controles de quem assiste (a música e os apps chegam ao viewer, os pedidos para pular e para tirar um app chegam ao host e um pedido inválido é recusado); duas salas da rede conversando no chat e na voz, passando a foto do perfil e uma imagem de 1 MB de uma para a outra e usando canais (um canal de texto e um de voz criados, a mensagem no canal certo e a voz seguindo a troca de canal); e, se o PC tiver saída de som, a separação do som do PC por app e a festa com um app só (o som do ScreenFlow e o do app tirado ficam fora, o resto vai). O CI roda isso a cada mudança, com uma placa de som virtual.

Logs ficam em `%LOCALAPPDATA%\ScreenFlow\logs` (botão **Abrir logs**). As fotos de perfil e as imagens do chat ficam em `%LOCALAPPDATA%\ScreenFlow\imagens`; as que não são usadas há 30 dias são apagadas sozinhas.

## Atualizações

O app procura uma versão nova ao abrir e a cada 6 horas (dá para desligar em **Atualizações**). Quando sai uma, o **sininho** da barra de cima avisa. Ao clicar em **Atualizar agora**, ele baixa o instalador do último release, confere o SHA-256, instala em silêncio e reabre.

Para publicar uma versão, crie um release no GitHub com a tag nova `v1.3.0` na `main` (ou rode `git tag v1.3.0 && git push origin v1.3.0`). O workflow `release.yml` compila, gera o instalador e o `.sha256` e anexa ao release.

## Solução de problemas

| Problema | O que fazer |
|---|---|
| "Radmin VPN não encontrado" | Clique em **Instalar o Radmin VPN** na Visão geral (ou instale pelo site) e entre numa rede. O IP 26.x precisa aparecer no Radmin. |
| A instalação do Radmin VPN não terminou | Aceite o pedido de permissão do Windows. Se o antivírus ou a internet bloquearem o download, baixe em <https://www.radmin-vpn.com/>. Se pedir para reiniciar, reinicie e clique de novo. |
| Viewer não conecta | Os dois na mesma rede do Radmin? O host clicou em Iniciar? No host, confira o firewall em Configurações › Rede e diagnóstico. Tente `ping 26.x.x.x`. |
| Host não aparece na lista | A descoberta usa broadcast; algumas redes bloqueiam. Digite o IP direto. |
| Imagem trava ou pixeliza | Abra as estatísticas (F3). Perda acima de 2% indica rede saturada: baixe a qualidade ou o FPS, ou use **Testar banda**. Se o Radmin estiver passando pelo relay (ícone de conexão indireta no Radmin), a banda cai bastante. |
| FPS abaixo do escolhido | Veja o encoder em Diagnóstico. Sem NVENC/AMF/QSV o app usa software. Notebooks: conecte na tomada e use a GPU dedicada. |
| Sem áudio | Ligue **Compartilhar o som do PC** antes de começar (na festa de música ele já vem ligado). Confira o volume no player (M e setas). Se ainda assim não vier som, desligue **Separar o som por app** em Configurações › Transmissão. |
| Um app tirado continua aparecendo no som | Alguns apps tocam por outro processo (no Steam, o som vem do `steamwebhelper`). Abra **Escolher apps** de novo com o app tocando e marque o que aparece tocando. |
| Ninguém aparece na Sala da rede | Todos precisam do ScreenFlow 1.1 ou mais novo, na mesma rede do Radmin, com a porta UDP 47802 liberada. Adicione pelo IP do Radmin na lista de quem está na rede. |
| Ninguém me ouve na voz | Vocês precisam estar no mesmo canal de voz. Veja se o Windows deixa apps usarem o microfone (Configurações do Windows › Privacidade e segurança › Microfone) e use **Testar microfone** em Configurações › Voz e sala. |
| Não ouço uma pessoa só | Botão direito nela na voz: o volume dela pode estar em 0% ou com **Silenciar para mim** marcado. **Volume normal (100%)** volta ao normal. |
| Alguém não vê o meu canal | Quem está no ScreenFlow 1.2 ou mais antigo só vê o #geral e a voz Geral. Os canais criados aparecem enquanto quem criou está na sala. |
| O chat não está sendo guardado | O cartão em Configurações › Voz e sala diz o motivo. Instale o Google Drive para computador e entre na conta, ou clique em **Escolher outra pasta**. |
| A transmissão não aparece nos controles de mídia do Windows | Ligue **Aparecer como player de mídia do Windows** em Configurações › Assistir e abra o player de novo. |
| Câmera não aparece | Feche outros apps que usam a câmera e clique em atualizar ao lado da lista. |
| Imagem do chat fica em "Baixando" | A imagem vem de quem mandou (ou de quem já baixou). Se essa pessoa saiu da sala, clique em **Tentar de novo** quando ela voltar. Quem usa o ScreenFlow 1.1.0 vê só o aviso "📷 Imagem (atualize o ScreenFlow para ver)". |
| Os controles da música não aparecem | Eles aparecem para apps que usam os controles de mídia do Windows (os mesmos das teclas de mídia), como o Spotify e o YouTube no navegador. Para quem assiste, o som desse app também precisa estar indo na transmissão. |
| Quem assiste não vê os botões de qualidade, música ou apps | Os dois PCs precisam da 1.2 ou mais nova, e quem transmite precisa deixar ligado **Quem assiste pode mexer nos controles**. |
| A placa de vídeo não aparece ou aparece "Com problema" | O cartão da Visão geral diz o motivo (driver antigo para o NVENC, placa parada pelo Windows com o código 43, sem driver). Veja a tabela em [docs/TROUBLESHOOTING.md](docs/TROUBLESHOOTING.md#placa-de-vídeo) e mande o texto do Diagnóstico se continuar. |
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
