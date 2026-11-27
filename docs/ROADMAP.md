# Status e próximos passos

## Implementado

- [x] App renomeado para ScreenFlow, tema escuro/claro com tokens
- [x] 1.0.1: visual novo com menu lateral, cores vivas e fonte Inter; telas Visão geral, Transmissão, Assistir, Monitoramento, Configurações e Atualizações
- [x] 1.0.1: festa de música (só áudio, Opus até 320 kbps com cópia de segurança) e compartilhamento de câmera
- [x] 1.0.1: sininho de avisos (versão nova, firewall) e procura de versão a cada 6 horas
- [x] 1.0.1: instalador libera as portas no firewall uma vez só
- [x] 1.0.1: player com vídeo direto na GPU (D3D11, swapchain com tearing) e controles em janela sobreposta
- [x] 1.1.0: voz da rede no estilo do Discord, com o painel no menu lateral acima do nome do PC (entrar, sair, mudo, sem som, anel de quem fala, sons de aviso)
- [x] 1.1.0: chat da rede com todo mundo que está com o ScreenFlow aberto, com confirmação, reenvio e histórico para quem chega
- [x] 1.1.0: tela, câmera e som ao mesmo tempo (a câmera vai junto como segunda faixa, num quadrinho)
- [x] 1.1.0: fechar e minimizar levam para a bandeja; a dica do ícone mostra se está ao vivo e o que está indo; uma cópia do app por vez
- [x] 1.1.0: som do PC separado por app: a voz da rede e os sons do ScreenFlow ficam fora, e dá para escolher outros apps para tirar
- [x] 1.1.0: tema claro com bordas visíveis em todos os cartões
- [x] 1.1.1: seu perfil, com nome e foto que aparecem no chat, na voz e para quem assiste (a foto vai pela sala da rede)
- [x] 1.1.1: imagens no chat (anexar, colar com Ctrl+V, arrastar; abrir, salvar e copiar), com download pela sala que pede só o que falta e confere o hash
- [x] 1.1.1: controles da música e do vídeo (voltar, pausar, pular e tempo) transmitindo uma janela ou a festa de um app só
- [x] 1.1.1: qualidade trocada com a transmissão no ar e qualidade fixa, sem adaptar
- [x] 1.1.1: festa de música com o som de um app só
- [x] 1.1.1: ouvir o próprio microfone nas Configurações
- [x] 1.1.1: instalar o Radmin VPN pelo app (página oficial, assinatura da Famatech conferida, instalação sem janelas)
- [x] 1.1.1: a janela volta sozinha quando a transmissão acaba ou o player fecha
- [x] 1.1.1: ritmo de envio dos pacotes, FEC espalhado, NACK de frame inteiro e prazo pelo RTT; som com buffer adaptativo e música em quadros de 10 ms
- [x] 1.2.0: quem assiste troca a qualidade, controla a música ou o vídeo (voltar, pausar, pular e tempo) e tira apps do som pelo player, com a chave para quem transmite desligar e um aviso com o nome de quem pediu
- [x] 1.2.0: "Tocando agora" com a capa na festa de música, para quem ouve
- [x] 1.2.0: mensagens novas do controle só vão para quem anunciou que entende; um tipo desconhecido é ignorado em vez de derrubar a conexão
- [x] 1.2.0: placa de vídeo lida uma a uma, com as do Gerenciador de Dispositivos (código 43, sem driver, desativada), a versão do driver e o motivo quando o encoder dela não abre (driver antigo para o NVENC, NVENC ocupado)
- [x] 1.2.0: o cartão da placa aparece na hora, sem esperar os encoders, e só os encoders das placas presentes são testados
- [x] 1.2.0: com a transmissão ou o player no ar, o Windows 11 não põe o ScreenFlow no modo de eficiência (o timer de 1 ms continua valendo com a janela minimizada ou na bandeja)
- [x] 1.3.0: Sala da rede como um servidor do Discord: canais de texto e de voz (os padrão e até 6 criados por PC), quem está em cada canal de voz, trocar de canal sem desligar o microfone e a voz só entre quem está no mesmo canal
- [x] 1.3.0: volume de cada pessoa da voz só para você (0 a 200%) e silenciar só para você, lembrados entre as sessões
- [x] 1.3.0: o volume do Windows de quem transmite não muda o som da transmissão (o som capturado é compensado pelo volume do aparelho)
- [x] 1.3.0: o player aparece nos controles de mídia do Windows (SMTC), com a música da festa, a capa e as teclas de mídia
- [x] 1.3.0: chat guardado no Google Drive para computador (achado sozinho) ou em qualquer pasta, com um arquivo por canal e por dia e as conversas dos últimos dois meses de volta ao abrir
- [x] 1.3.0: configurações em abas; "Aprovar cada pessoa" desligado por padrão (e desligado uma vez em quem já tinha)
- [x] 1.3.0: barra do player com tamanho fixo (não pula quando a música ou a qualidade mudam)
- [x] 1.4.0: minutagem do vídeo transmitido (posição, duração, arrastar, ±10 s, pausar) ligada à sessão de mídia do app transmitido; a busca é no app de origem do host, conferida relendo a posição, com o trecho de busca do ao vivo, explicação quando não dá para buscar, envio só ao soltar e pedidos repetidos ignorados
- [x] 1.4.0: atualizador só por HTTPS do GitHub (também depois dos redirecionamentos), SHA-256 obrigatório, tamanho conferido, cancelar o download, assinatura Authenticode conferida quando o instalador é assinado e erro claro quando o Windows recusa abrir o instalador
- [x] 1.4.0: configurações que não abrem são copiadas (settings.invalido-…json) antes do app usar os padrões, com um aviso; AllowNonRadminFallback saiu
- [x] 1.4.0: sala privada com convite (código SF1-…), só quem o dono aprovou, remover da sala (a chave troca para quem fica), trocar a chave e bloquear alguém só para você; presença, chat, voz e imagens lacrados com AES-256-GCM
- [x] 1.4.0: saída de som da voz escolhida nas Configurações e apertar para falar com uma tecla ou botão lateral do mouse que vale em qualquer app
- [x] 1.4.0: transmitir só uma parte da tela ou da janela, esconder o cursor e tapar áreas de preto (as notificações do Windows com um clique), também com a transmissão no ar
- [x] 1.4.1: servidor ScreenFlow: quem transmite manda uma cópia só para o servidor e ele manda para cada pessoa que assiste; a sala, o chat, a voz e os canais passam por ele e ficam guardados nele; senha do servidor no lugar do PIN e da aprovação (guia em [SERVIDOR.md](SERVIDOR.md))
- [x] 1.4.1: o servidor de três jeitos: dentro do app ("Este PC é o servidor"), o programa `screenflow-servidor` para Linux (x64 e arm64, com serviço do systemd e `install-server.sh`) e para Windows
- [x] 1.4.1: canais de voz e de texto livres: só o `geral` e o `Geral` vêm prontos; no servidor, qualquer pessoa cria, renomeia e apaga, e renomear um canal de texto leva a conversa
- [x] 1.4.1: 10 temas de cor (um de cada cor) por pessoa, em cima do escuro ou do claro, aplicados na hora
- [x] 1.4.1: Tailscale pelo app: estado, ligar com o login no navegador da pessoa (sem chave de autenticação) e instalar o MSI oficial (SHA-256 publicado e assinatura da Tailscale conferidos); sem servidor, a sala e os anúncios vão direto para os PCs da rede do Tailscale, que não repassa broadcast
- [x] 1.4.1: instalador com as opções de instalar o Tailscale e o Radmin VPN junto e de escolher o modo do servidor (o app lê o pedido quando abre)
- [x] 1.4.1: a sala guarda até 512 pessoas (o máximo do servidor), e a voz deixou de ordenar a lista da sala a cada quadro
- [x] Detecção do Radmin VPN, bind no IP 26.x, instalação pelo app
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
- [x] Monitoramento com gráficos de FPS, banda, ping, perda e áudio dos últimos 90 segundos
- [x] Áudio do sistema WASAPI loopback em Opus 48 kHz, sincronizado por timestamp
- [x] Configuração automática por hardware e rede; diagnóstico na tela e por `--diagnostico`
- [x] Atualização pelo app via GitHub Releases com SHA-256; workflow de release por tag
- [x] Logs em arquivo e encerramento limpo de captura, encoder, sockets e áudio

## Testado

- 475 testes automatizados (na 1.4.1: o servidor de ponta a ponta no 127.0.0.1, com a tela indo para várias pessoas, quem assiste esperando quem transmite voltar, a sala com senha, canais e histórico e os limites contra senha errada e ids inventados; os pacotes do servidor, o vídeo guardado para os NACKs com a paridade, as opções do programa do servidor e do instalador, o Tailscale (estado, link do login e MSI) e as cores dos temas nos dois modos; na 1.4.0: a minutagem, o trecho de busca, a ligação ao app transmitido e as mensagens novas lidas por versões antigas; o atualizador e a cópia das configurações; o convite, os lacres, as gerações de chave, a troca de chave por ECDH e quem pode entrar na sala privada; apertar para falar; a região e as áreas tapadas). Antes disso: cabeçalho, FEC, NACK, descarte, mensagens, descoberta, troca de chaves, PIN, AES-GCM, Radmin, backoff, bitrates por perfil, negociação de codec, qualidade adaptativa, configuração automática, nome curto da placa de vídeo, divisão de upload, configurações, releases, sincronia de relógio, modos (tela, câmera, festa, câmera junto), segunda faixa de vídeo, perfis de áudio, medidor de espectro, redundância do áudio, pacotes e presença da sala, chat com confirmação e histórico, limites de envio, portão de voz, quais processos capturar para tirar apps do som, fila da mistura do som, recursos do tema (cada cor e estilo existe, com um tipo só, nos temas escuro e claro); na 1.1.1, festa com um app só, nomes dos apps de mídia e o tempo da música, foto de perfil e imagens na presença e no chat (com o formato antigo continuando igual), pedidos e partes dos arquivos, downloads com troca de quem manda, arquivos guardados no PC, nome e foto nas configurações e o link do instalador do Radmin; na 1.2.0, as mensagens novas do controle e as antigas lidas igual, tipos desconhecidos ignorados, o que o host aceita de quem assiste (qualidades oferecidas, comandos que o app deixa, limite de pedidos), a lista de apps tirados, quando a música é mandada de novo, a placa principal (até sem o driver), a versão do driver da NVIDIA e o motivo do encoder não abrir; na 1.3.0, os canais (nomes, limites e a lista juntando os de todo mundo), a presença e o chat com canal (e o formato antigo igual para a 1.2), a compensação do volume do Windows, o chat guardado (arquivos por canal e por dia, imagens, histórico e a pasta do Google Drive) e a migração das configurações.
- Compilação de toda a solução e testes no CI do Windows; o app publicado roda o `--diagnostico` (carrega o FFmpeg), o `--autoteste` e o `--capturas` (PNG de cada tela). O autoteste faz uma festa de música de verdade no 127.0.0.1 (do encoder Opus do host até o decoder do viewer) e passa nela pelos controles de quem assiste (a música e os apps chegam ao viewer, os pedidos para pular e para tirar um app chegam ao host e um ponto fora da música é recusado), põe duas salas da rede para conversar no chat e na voz, passar a foto do perfil e uma imagem de 1 MB de uma para a outra e usar canais (os canais criados aparecem, a mensagem chega no canal certo e a voz segue a troca de canal) e confere a separação do som do PC por app numa placa de som virtual, com dois apps tocando tons fora do ScreenFlow. Na 1.4.0 o autoteste também abre uma sala privada com três salas no 127.0.0.1 (a sem convite não vê nem ouve nada, o chat e a voz cifrados chegam, a troca de chave e a remoção funcionam) e passa uma imagem pelo corte e pela máscara na GPU, lendo os pixels de volta (sem placa de vídeo, pelo Direct3D de software do Windows). Na 1.4.1 o autoteste também transmite uma festa de música por um servidor ScreenFlow aberto no próprio PC, com senha (quem assiste pelo servidor recebe o som, e quem transmite e o servidor contam a pessoa), e passa o chat e os canais de duas salas pelo servidor (criar, renomear e apagar). O CI do servidor instala o programa de verdade como serviço do systemd num Linux, confere as portas e desinstala.

## Ainda não testado (precisa de PCs reais)

- Captura, encoder e decoder em GPUs NVIDIA, AMD e Intel.
- Conexão real pelo Radmin VPN, descoberta por broadcast e firewall.
- FPS, latência e banda reais; 4K a 165 FPS.
- 10 ou mais viewers ao mesmo tempo.
- Câmera em webcams e placas de captura reais.
- Player na GPU em placas NVIDIA, AMD e Intel (o caminho pela CPU continua de reserva).
- Atualização ponta a ponta da 1.2.0 para a 1.3.0 pelo app.
- Canais de voz e de texto entre PCs reais com a 1.3.0 e um com a 1.2.0 (que só vê o #geral e a voz Geral).
- Som da transmissão com o volume do Windows do host baixo ou mudo, em placas de som reais.
- Controles de mídia do Windows (painel do volume, tela de bloqueio, teclas de mídia e apps que mostram o que está tocando).
- Chat guardado no Google Drive para computador de verdade (unidade G: e modo espelhado).
- Controles de quem assiste entre dois PCs reais: qualidade, música e apps, com o Spotify e o navegador.
- Cartão da placa de vídeo numa RTX com driver antigo, com o código 43 e num notebook com duas placas.
- Voz e chat pela rede real do Radmin, com microfones reais (o autoteste usa um tom no lugar do microfone).
- Câmera junto com a tela: duas sessões de encoder ao mesmo tempo em GPUs reais.
- Som do PC por app no Windows 10 e 11 de verdade, com apps como Spotify, Discord, navegadores e jogos (e a festa de um app só).
- Controles de mídia com Spotify, navegadores e o Reprodutor de Mídia de verdade.
- Fotos e imagens pela rede real do Radmin, inclusive passando pelo relay.
- Instalação do Radmin VPN pelo app num PC sem ele (o pedido de permissão e o driver do adaptador).

- 1.4.0: minutagem com o Spotify, o navegador (YouTube, Twitch ao vivo) e o Reprodutor de Mídia, com a tela inteira e com uma janela, e entre um host 1.4.0 e um viewer 1.3.0.
- 1.4.0: sala privada entre PCs reais pelo Radmin (convite, aprovação, remover e trocar a chave com alguém desligado).
- 1.4.0: apertar para falar dentro de jogos em tela cheia e com o botão lateral do mouse; saída de som em fones USB e Bluetooth.
- 1.4.0: região, cursor e áreas tapadas em monitores com escala de 125% e 150%, em dois monitores e com a captura DXGI.
- 1.4.0: atualização da 1.3.0 para a 1.4.0 pelo app, e o cancelamento no meio do download.
- 1.4.1: servidor entre PCs reais pelo Tailscale e pelo Radmin VPN: transmitir por ele com 3 ou mais pessoas assistindo, a sala, o chat, a voz e os canais, a queda e a volta do servidor e de quem transmite.
- 1.4.1: o programa do servidor num Linux de verdade com o Tailscale (o `install-server.sh --tailscale`, o login pelo link e o serviço ligando junto com o PC) e no Windows.
- 1.4.1: instalar o Tailscale pelo app e pelo instalador num PC sem ele (o pedido de permissão, a assinatura do MSI, o `tailscale up` com o login e o modo sem ninguém logado no PC servidor).
- 1.4.1: as páginas novas do instalador (modo do servidor e endereço) e o Radmin VPN instalado junto.
- 1.4.1: a sala direta pelo Tailscale, sem servidor, achando os PCs da rede sozinha.
- 1.4.1: os 10 temas de cor em todas as telas, no escuro e no claro.

## Próximos passos

O estado de cada item do plano está em [PLANO-DE-MELHORIAS.md](PLANO-DE-MELHORIAS.md), e o servidor ScreenFlow em [SERVIDOR.md](SERVIDOR.md).

- Cursor desenhado na captura DXGI (a WGC já inclui o cursor).
- Modo relay: um viewer repassa o vídeo para outros, para passar do limite de upload do host. Na 1.4.1, o servidor ScreenFlow já faz isso para quem assiste por ele; sem servidor, continua pendente.
- Servidor público na internet, sem VPN (contas, STUN e relay), descrito no fim de [SERVIDOR.md](SERVIDOR.md).
- Controle remoto de mouse e teclado, com autorização separada.
- Fora da sala privada, a voz e o chat da rede continuam contando com o túnel do Radmin VPN ou do Tailscale (e, pelo servidor, com a confiança no PC servidor).
