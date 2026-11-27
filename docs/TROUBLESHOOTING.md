# Solução de problemas

Comece sempre por **Configurações › Rede e diagnóstico** (ou `ScreenFlow.exe --diagnostico`) e pelos logs em `%LOCALAPPDATA%\ScreenFlow\logs`.

## Rede

| Sintoma | Causa provável | Solução |
|---|---|---|
| "Radmin VPN não encontrado" | Radmin não instalado | Clique em **Instalar o Radmin VPN** na Visão geral ou na Transmissão (ou instale em <https://www.radmin-vpn.com/>) |
| "Radmin VPN sem rede ativa" | Instalado, mas fora de uma rede | Clique em **Abrir o Radmin VPN** e entre na mesma rede que os outros |
| A instalação do Radmin VPN parou | Pedido de permissão recusado, internet ou antivírus bloqueando, ou o Windows esperando reiniciar | Clique de novo e aceite o pedido do Windows. Se o download falhar, instale pelo site. Se o aviso pedir para reiniciar, reinicie o PC e tente de novo |
| "A assinatura digital do instalador baixado não confere" | O arquivo veio estragado ou não é o do site oficial | O ScreenFlow já apagou o arquivo. Tente de novo mais tarde ou instale pelo site |
| Viewer fica em "Conectando" | Firewall do host bloqueando | O instalador libera as portas; se mudou as portas, use Configurações › Rede e diagnóstico › Liberar portas (ou o aviso no sininho) |
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
- Transmitindo com a janela minimizada ou na bandeja, o ScreenFlow pede ao Windows para não economizar energia com ele (o modo de eficiência do Windows 11 atrasava o envio). Se mesmo assim cair, confira se o notebook não está no modo de economia de bateria.

## Placa de vídeo

O cartão **Placa de vídeo** da Visão geral mostra a placa principal (a de verdade, não as virtuais nem a integrada, quando há uma dedicada) e, embaixo, o encoder que ela usa ou o motivo de não usar. Passe o mouse no nome para ver o nome completo e a versão do driver. Depois de resolver, clique em **Detectar o hardware de novo** em Configurações › Rede e diagnóstico.

| O cartão mostra | O que quer dizer | O que fazer |
|---|---|---|
| "Não identificada" | Nem o DXGI nem a lista de dispositivos do Windows mostraram uma placa | Veja se a placa aparece no Gerenciador de Dispositivos › Adaptadores de vídeo e mande o texto do Diagnóstico (botão de copiar) |
| "Com problema: ... (código 43)" | O Windows parou a placa porque o driver ou a placa relatou erro | Reinstale o driver do fabricante (instalação limpa) e reinicie. Se voltar, pode ser a placa ou a alimentação dela |
| "Placa NVIDIA sem driver" ou "Com problema: sem driver instalado (código 28)", "o driver não carregou" ou "está sem o driver do fabricante" | O driver da placa não está instalado (o Windows usa o vídeo básico da Microsoft e nem sabe o modelo) ou não carregou | Instale o driver do site da NVIDIA, da AMD ou da Intel e reinicie |
| "Com problema: desativada no Gerenciador de Dispositivos (código 22)" | A placa foi desativada | Gerenciador de Dispositivos › botão direito na placa › **Habilitar dispositivo** |
| "Com problema: o Windows precisa reiniciar para a placa funcionar (código 14)" | Um driver novo foi instalado e falta reiniciar | Reinicie o PC |
| "Sem o encoder da placa: o driver da NVIDIA é antigo para o NVENC" | O FFmpeg 7.1 do ScreenFlow usa uma versão do NVENC que pede driver novo (o número mínimo aparece no aviso) | Atualize o driver pelo app da NVIDIA ou em nvidia.com |
| "Sem o encoder da placa: o NVENC está ocupado por outro app" | OBS, Discord ou a gravação da NVIDIA estão usando todas as sessões do NVENC | Feche esses apps e detecte de novo |
| A integrada aparece no lugar da dedicada | O Windows não mostrou a dedicada ao app | Confira o Diagnóstico: cada placa aparece com o driver e, se houver, o PROBLEMA que o Windows relata |

No Diagnóstico, cada encoder que não abriu aparece com a mensagem do FFmpeg, para mandar junto se algo estiver estranho.

## Áudio

- Ligue **Compartilhar o som do PC** antes de começar a transmitir (na festa de música ele sempre vai).
- Festa com um app só: se não vem som, confira se o app escolhido em **O que toca na festa** está mesmo tocando (o botão de atualizar ao lado da lista mostra os apps abertos). Alguns apps tocam por outro processo com outro nome: escolha o que aparece tocando.
- O áudio capturado é o do dispositivo de saída padrão do Windows. Se trocar o fone ou a caixa no meio, o app acompanha sozinho.
- O visualizador da tela Transmissão mexe mesmo antes de alguém entrar: se ele fica parado com música tocando, o som está saindo por outro dispositivo.
- No player, M liga/desliga o som e as setas mudam o volume (só o seu).
- O volume do Windows de quem transmite não muda o som que vai: abaixar ou tirar o som do PC só muda o que essa pessoa ouve, e quem assiste ouve no volume normal (a 1.3.0 compensa o volume do aparelho na captura). Se quiser o jeito antigo, em que quem assiste ouve no mesmo volume que você, desligue a opção em Configurações › Transmissão.
- `ScreenFlow.exe --autoteste` confere a festa de música no próprio PC (host e viewer no 127.0.0.1), a sala da rede e, se o PC tiver saída de som, a separação do som por app.

### Som do PC separado por app

- A voz da rede e os sons do ScreenFlow nunca vão na transmissão (Windows 10 2004 ou mais novo). Usar fone de ouvido não muda nada aqui: o som é pego direto do Windows, não pelo microfone.
- Para tirar outro app, use **Escolher apps** no cartão **Som do PC** da tela Transmissão. Vale na hora, até no meio da transmissão.
- Um app tirado continua aparecendo: alguns apps tocam por outro processo com outro nome (no Steam, o som da loja e dos vídeos vem do `steamwebhelper`). Abra **Escolher apps** de novo com o app tocando e marque o que aparece como tocando. Tirar o Steam não tira os jogos que ele abriu: cada jogo é um app separado.
- Quem assiste não ouve nada do PC: desligue **Separar o som por app** em Configurações › Transmissão. Aí vai todo o som do PC, como nas versões antigas (com a voz da rede junto). Mande os logs se isso acontecer: é um caso que precisa de correção.
- O cartão **Som do PC** diz o que está indo agora ("sem Spotify e a voz da rede", por exemplo). Se o Windows não deixar separar, ele avisa e vai todo o som do PC.

### Controles da música e do vídeo

- O cartão com a música ou o vídeo aparece para apps que usam os controles de mídia do Windows (os mesmos das teclas de mídia do teclado): Spotify, navegadores, Reprodutor de Mídia e outros. Transmitindo uma janela ou a festa de um app só, ele mostra esse app; senão, o que as teclas de mídia controlariam.
- A barra do tempo só aparece quando o app deixa mudar a posição (em lives, por exemplo, não dá).
- Com mais de um app tocando, a lista no canto do cartão escolhe qual controlar.

## Câmera

- A câmera só liga quando alguém entra. Se outro app (Discord, OBS, Teams) estiver usando a câmera, feche-o e clique em atualizar ao lado da lista.
- A câmera usa o formato mais próximo da qualidade escolhida que ela suportar.
- **Mandar a câmera junto** (modo tela) usa um segundo encoder na GPU. Se a câmera não abrir (em uso, bloqueada em Configurações do Windows › Privacidade e segurança › Câmera, ou a placa sem encoder livre porque o OBS está gravando), a tela continua e a câmera tenta de novo sozinha.
- Quem assiste com a versão 1.0.x vê só a tela. No player 1.1, clique no quadrinho para trocar de canto e aperte **C** para esconder ou mostrar.

## Sala da rede (voz e chat)

| Sintoma | Causa provável | Solução |
|---|---|---|
| Ninguém aparece na Sala da rede nem no painel da voz | Versão antiga, outra rede do Radmin ou broadcast bloqueado | Todos precisam do ScreenFlow 1.1 ou mais novo na mesma rede do Radmin. Adicione pelo IP do Radmin na lista de quem está na rede |
| "A sala da rede ainda não abriu" | A porta UDP 47802 está em uso por outro programa | Feche o programa que usa a porta (ou mude a porta de descoberta em Configurações › Rede e diagnóstico, igual em todos os PCs) e abra o ScreenFlow de novo |
| Ninguém me ouve | Canais de voz diferentes, microfone bloqueado ou abaixo da sensibilidade | Quem está num canal só ouve quem está no mesmo canal: confira na Sala da rede. Configurações do Windows › Privacidade e segurança › Microfone: deixe os apps usarem. Em Configurações › Voz e sala, use **Testar microfone**: a barra precisa passar da marca |
| Eu não ouço ninguém | Som desligado (ícone do fone), volume das vozes no zero ou outro canal | Clique no fone no painel da voz, confira o volume em Configurações › Voz e sala e veja se vocês estão no mesmo canal |
| Não ouço uma pessoa só | O volume dela só para você está em 0% | Botão direito nela (na Sala ou no painel da voz): desmarque **Silenciar para mim** ou clique em **Volume normal (100%)** |
| Alguém não vê o canal que eu criei | A pessoa usa a 1.2 ou mais antiga, ou você saiu da sala | Quem está na 1.2 ou mais antiga só vê o #geral e a voz Geral (e não recebe as mensagens dos outros canais). Os canais criados aparecem enquanto quem criou está na sala; um canal de texto com conversa continua na lista |
| "O Google Drive para computador não foi encontrado" | O chat guardado procura a pasta do Google Drive para computador e não achou | Instale o Google Drive para computador e entre na sua conta, ou use **Escolher outra pasta** em Configurações › Voz e sala |
| "O chat não foi gravado na pasta" | A pasta sumiu, o Google Drive fechou ou a unidade G: saiu | Abra o Google Drive de novo. As conversas continuam na sala e as novas voltam a ser guardadas quando a pasta volta |
| Voz cortando | Perda na rede do Radmin (conexão indireta pelo relay) | Cada quadro leva uma cópia do anterior, então uma perda isolada não corta; muita perda corta. Veja a conexão no Radmin |
| Mensagens antigas sumiram | O histórico fica só na memória | O chat guarda as últimas 300 mensagens de cada canal enquanto alguém da rede está com o app aberto. Para guardar de vez, ligue **Guardar o chat no Google Drive** em Configurações › Voz e sala |
| Imagem do chat fica em "Baixando a imagem" | Quem mandou saiu da sala, ou a rede do Radmin está perdendo muito | A imagem vem de quem mandou ou de quem já baixou. Clique em **Tentar de novo** quando alguém que tem a imagem estiver na sala |
| A foto de alguém não aparece | A foto ainda está chegando, ou a pessoa usa a versão 1.1.0 | A foto chega em alguns segundos e é pedida de novo a cada 30 s. Na 1.1.0 não há foto: aparece a inicial |
| "Não deu para abrir essa imagem" | Formato que o Windows não abre (HEIC e WebP precisam das extensões da Microsoft Store) | Abra a imagem em outro programa e copie (Ctrl+C), ou salve como JPEG ou PNG |
| Quem usa a 1.1.0 vê "📷 Imagem (atualize o ScreenFlow para ver)" | A 1.1.0 não mostra imagens | Atualize para a 1.1.1 ou mais nova |

A voz, o chat, as imagens e as fotos de perfil não são criptografados pelo ScreenFlow: eles contam com o túnel do Radmin VPN. Use uma rede do Radmin com senha, só com quem você conhece. O chat guardado fica em texto simples na pasta escolhida (e na sua conta do Google Drive, quando é a pasta dele). As fotos e imagens ficam em `%LOCALAPPDATA%\ScreenFlow\imagens` e as que não são usadas há 30 dias são apagadas sozinhas.

Para ouvir como a sua voz sai, use **Ouvir meu microfone** em Configurações › Voz e sala (com fone, para não dar eco).

## Bandeja

- Fechar ou minimizar leva o ScreenFlow para a bandeja (perto do relógio) e a transmissão continua. Se o ícone não aparecer, ele pode estar escondido na setinha da bandeja.
- Quando a transmissão acaba ou o player fecha, a janela volta sozinha. Se não quiser isso, desligue a opção em Configurações › Geral.
- Para fechar de vez: botão direito no ícone › **Sair do ScreenFlow**. Para o X fechar de verdade, desligue a opção em Configurações › Geral.
- Abrir o ScreenFlow de novo pelo atalho mostra a janela que já está aberta.

## Player

- Se a imagem ficar preta com o som funcionando, desligue **Desenhar o vídeo direto na GPU** em Configurações › Assistir: o vídeo passa a ir pela CPU.
- O ScreenFlow não aparece no painel do volume, na tela de bloqueio ou nas teclas de mídia: ligue **Aparecer como player de mídia do Windows** em Configurações › Assistir e abra o player de novo. As teclas de pausar e pular só mexem na música quando quem transmite deixa **Quem assiste pode mexer nos controles** ligado.
- A barra do player tem tamanho fixo: o nome da música é cortado com "…" quando não cabe (passe o mouse para ver inteiro).

### Controles de quem assiste (1.2)

| Sintoma | Causa provável | Solução |
|---|---|---|
| A barra do player não mostra a qualidade, a música nem os apps | Quem transmite usa uma versão antiga ou desligou **Quem assiste pode mexer nos controles** | Os dois precisam da 1.2 ou mais nova. Quem transmite liga a chave na tela Transmissão |
| Só o botão da qualidade não aparece | Festa de música (não tem vídeo) | Na festa, o que dá para mudar é a música e os apps |
| A música não aparece no player | Nada tocando nos controles de mídia do Windows de quem transmite, o app foi tirado do som, ou a festa é de outro app | O player só mostra o que quem assiste está ouvindo: o que toca num app tirado do som fica só com quem transmite |
| O botão dos apps não aparece | **Separar o som por app** desligado, o som do PC não vai na transmissão ou a festa é de um app só | Quem transmite liga **Separar o som por app** e o som do PC |
| Um clique não mudou nada | Cliques demais seguidos (quem transmite aceita uns poucos de uma vez e depois dois por segundo), ou a lista mudou do lado de quem transmite | Espere um segundo e clique de novo. O player mostra o que vale depois que quem transmite aplica |

Tudo o que quem assiste muda vale para todo mundo que está assistindo, e quem transmite vê um aviso com o nome de quem pediu.

## Atualização

- "Não foi possível verificar": sem internet ou sem release publicado ainda. A tela Atualizações tem o botão **Procurar agora**, que volta ao normal logo depois de procurar, mesmo sem versão nova.
- Se o instalador falhar, baixe manualmente em Releases.

## Build

- Rode `./scripts/fetch-ffmpeg.ps1` antes de compilar; sem as DLLs o app abre, mas não acha encoders.
- A versão do FFmpeg precisa ser 7.1.x (mesma do pacote FFmpeg.AutoGen).
