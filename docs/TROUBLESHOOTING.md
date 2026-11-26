# Solução de problemas

Comece sempre por **Configurações › Diagnóstico** (ou `ScreenFlow.exe --diagnostico`) e pelos logs em `%LOCALAPPDATA%\ScreenFlow\logs`.

## Rede

| Sintoma | Causa provável | Solução |
|---|---|---|
| "Radmin VPN não encontrado" | Radmin não instalado | Clique em **Instalar o Radmin VPN** na Visão geral ou na Transmissão (ou instale em <https://www.radmin-vpn.com/>) |
| "Radmin VPN sem rede ativa" | Instalado, mas fora de uma rede | Clique em **Abrir o Radmin VPN** e entre na mesma rede que os outros |
| A instalação do Radmin VPN parou | Pedido de permissão recusado, internet ou antivírus bloqueando, ou o Windows esperando reiniciar | Clique de novo e aceite o pedido do Windows. Se o download falhar, instale pelo site. Se o aviso pedir para reiniciar, reinicie o PC e tente de novo |
| "A assinatura digital do instalador baixado não confere" | O arquivo veio estragado ou não é o do site oficial | O ScreenFlow já apagou o arquivo. Tente de novo mais tarde ou instale pelo site |
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
- Festa com um app só: se não vem som, confira se o app escolhido em **O que toca na festa** está mesmo tocando (o botão de atualizar ao lado da lista mostra os apps abertos). Alguns apps tocam por outro processo com outro nome: escolha o que aparece tocando.
- O áudio capturado é o do dispositivo de saída padrão do Windows. Se trocar o fone ou a caixa no meio, o app acompanha sozinho.
- O visualizador da tela Transmissão mexe mesmo antes de alguém entrar: se ele fica parado com música tocando, o som está saindo por outro dispositivo.
- No player, M liga/desliga o som e as setas mudam o volume.
- `ScreenFlow.exe --autoteste` confere a festa de música no próprio PC (host e viewer no 127.0.0.1), a sala da rede e, se o PC tiver saída de som, a separação do som por app.

### Som do PC separado por app

- A voz da rede e os sons do ScreenFlow nunca vão na transmissão (Windows 10 2004 ou mais novo). Usar fone de ouvido não muda nada aqui: o som é pego direto do Windows, não pelo microfone.
- Para tirar outro app, use **Escolher apps** no cartão **Som do PC** da tela Transmissão. Vale na hora, até no meio da transmissão.
- Um app tirado continua aparecendo: alguns apps tocam por outro processo com outro nome (no Steam, o som da loja e dos vídeos vem do `steamwebhelper`). Abra **Escolher apps** de novo com o app tocando e marque o que aparece como tocando. Tirar o Steam não tira os jogos que ele abriu: cada jogo é um app separado.
- Quem assiste não ouve nada do PC: desligue **Separar o som por app** em Configurações › Câmera, som e festa de música. Aí vai todo o som do PC, como nas versões antigas (com a voz da rede junto). Mande os logs se isso acontecer: é um caso que precisa de correção.
- O cartão **Som do PC** diz o que está indo agora ("sem Spotify e a voz da rede", por exemplo). Se o Windows não deixar separar, ele avisa e vai todo o som do PC.

### Controles da música e do vídeo

- O cartão com a música ou o vídeo aparece transmitindo uma janela ou a festa de um app só, e só para apps que usam os controles de mídia do Windows (os mesmos das teclas de mídia do teclado): Spotify, navegadores, Reprodutor de Mídia e outros.
- A barra do tempo só aparece quando o app deixa mudar a posição (em lives, por exemplo, não dá).
- Com mais de um app tocando, a lista no canto do cartão escolhe qual controlar.

## Câmera

- A câmera só liga quando alguém entra. Se outro app (Discord, OBS, Teams) estiver usando a câmera, feche-o e clique em atualizar ao lado da lista.
- A câmera usa o formato mais próximo da qualidade escolhida que ela suportar.
- **Mandar a câmera junto** (modo tela) usa um segundo encoder na GPU. Se a câmera não abrir (em uso, bloqueada em Configurações do Windows › Privacidade e segurança › Câmera, ou a placa sem encoder livre porque o OBS está gravando), a tela continua e a câmera tenta de novo sozinha.
- Quem assiste com a versão 1.0.x vê só a tela. No player 1.1, clique no quadrinho para trocar de canto e aperte **C** para esconder ou mostrar.

## Voz e chat da rede

| Sintoma | Causa provável | Solução |
|---|---|---|
| Ninguém aparece no painel da voz nem no chat | Versão antiga, outra rede do Radmin ou broadcast bloqueado | Todos precisam do ScreenFlow 1.1 ou mais novo na mesma rede do Radmin. Adicione pelo IP do Radmin na tela do chat |
| "O chat da rede está desligado" | A porta UDP 47802 está em uso por outro programa | Feche o programa que usa a porta (ou mude a porta de descoberta em Configurações › Rede e firewall, igual em todos os PCs) e abra o ScreenFlow de novo |
| Ninguém me ouve | Microfone bloqueado ou abaixo da sensibilidade | Configurações do Windows › Privacidade e segurança › Microfone: deixe os apps usarem. Em Configurações › Voz da rede, use **Testar microfone**: a barra precisa passar da marca |
| Eu não ouço ninguém | Som desligado (ícone do fone) ou volume das vozes no zero | Clique no fone no painel da voz e confira o volume em Configurações › Voz da rede |
| Voz cortando | Perda na rede do Radmin (conexão indireta pelo relay) | Cada quadro leva uma cópia do anterior, então uma perda isolada não corta; muita perda corta. Veja a conexão no Radmin |
| Mensagens antigas sumiram | O histórico fica só na memória | O chat guarda as últimas 300 mensagens enquanto alguém da rede está com o app aberto |
| Imagem do chat fica em "Baixando a imagem" | Quem mandou saiu da sala, ou a rede do Radmin está perdendo muito | A imagem vem de quem mandou ou de quem já baixou. Clique em **Tentar de novo** quando alguém que tem a imagem estiver na sala |
| A foto de alguém não aparece | A foto ainda está chegando, ou a pessoa usa a versão 1.1.0 | A foto chega em alguns segundos e é pedida de novo a cada 30 s. Na 1.1.0 não há foto: aparece a inicial |
| "Não deu para abrir essa imagem" | Formato que o Windows não abre (HEIC e WebP precisam das extensões da Microsoft Store) | Abra a imagem em outro programa e copie (Ctrl+C), ou salve como JPEG ou PNG |
| Quem usa a 1.1.0 vê "📷 Imagem (atualize o ScreenFlow para ver)" | A 1.1.0 não mostra imagens | Atualize para a 1.1.1 |

A voz, o chat, as imagens e as fotos de perfil não são criptografados pelo ScreenFlow: eles contam com o túnel do Radmin VPN. Use uma rede do Radmin com senha, só com quem você conhece. As fotos e imagens ficam em `%LOCALAPPDATA%\ScreenFlow\imagens` e as que não são usadas há 30 dias são apagadas sozinhas.

Para ouvir como a sua voz sai, use **Ouvir meu microfone** em Configurações › Voz da rede (com fone, para não dar eco).

## Bandeja

- Fechar ou minimizar leva o ScreenFlow para a bandeja (perto do relógio) e a transmissão continua. Se o ícone não aparecer, ele pode estar escondido na setinha da bandeja.
- Quando a transmissão acaba ou o player fecha, a janela volta sozinha. Se não quiser isso, desligue a opção em Configurações › Segurança e aparência.
- Para fechar de vez: botão direito no ícone › **Sair do ScreenFlow**. Para o X fechar de verdade, desligue a opção em Configurações.
- Abrir o ScreenFlow de novo pelo atalho mostra a janela que já está aberta.

## Player

- Se a imagem ficar preta com o som funcionando, desligue **Desenhar o vídeo direto na GPU** em Configurações › Player: o vídeo passa a ir pela CPU.

## Atualização

- "Não foi possível verificar": sem internet ou sem release publicado ainda. A tela Atualizações tem o botão **Procurar agora**, que volta ao normal logo depois de procurar, mesmo sem versão nova.
- Se o instalador falhar, baixe manualmente em Releases.

## Build

- Rode `./scripts/fetch-ffmpeg.ps1` antes de compilar; sem as DLLs o app abre, mas não acha encoders.
- A versão do FFmpeg precisa ser 7.1.x (mesma do pacote FFmpeg.AutoGen).
