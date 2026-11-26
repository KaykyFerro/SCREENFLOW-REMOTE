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

- 271 testes automatizados: cabeçalho, FEC, NACK, descarte, mensagens, descoberta, troca de chaves, PIN, AES-GCM, Radmin, backoff, bitrates por perfil, negociação de codec, qualidade adaptativa, configuração automática, nome curto da placa de vídeo, divisão de upload, configurações, releases, sincronia de relógio, modos (tela, câmera, festa, câmera junto), segunda faixa de vídeo, perfis de áudio, medidor de espectro, redundância do áudio, pacotes e presença da sala, chat com confirmação e histórico, limites de envio, portão de voz, quais processos capturar para tirar apps do som, fila da mistura do som, recursos do tema (cada cor e estilo existe, com um tipo só, nos temas escuro e claro); na 1.1.1, festa com um app só, nomes dos apps de mídia e o tempo da música, foto de perfil e imagens na presença e no chat (com o formato antigo continuando igual), pedidos e partes dos arquivos, downloads com troca de quem manda, arquivos guardados no PC, nome e foto nas configurações e o link do instalador do Radmin; na 1.2.0, as mensagens novas do controle e as antigas lidas igual, tipos desconhecidos ignorados, o que o host aceita de quem assiste (qualidades oferecidas, comandos que o app deixa, limite de pedidos), a lista de apps tirados, quando a música é mandada de novo, a placa principal (até sem o driver), a versão do driver da NVIDIA e o motivo do encoder não abrir.
- Compilação de toda a solução e testes no CI do Windows; o app publicado roda o `--diagnostico` (carrega o FFmpeg), o `--autoteste` e o `--capturas` (PNG de cada tela). O autoteste faz uma festa de música de verdade no 127.0.0.1 (do encoder Opus do host até o decoder do viewer) e passa nela pelos controles de quem assiste (a música e os apps chegam ao viewer, os pedidos para pular e para tirar um app chegam ao host e um ponto fora da música é recusado), põe duas salas da rede para conversar no chat e na voz e passar a foto do perfil e uma imagem de 1 MB de uma para a outra e confere a separação do som do PC por app numa placa de som virtual, com dois apps tocando tons fora do ScreenFlow.

## Ainda não testado (precisa de PCs reais)

- Captura, encoder e decoder em GPUs NVIDIA, AMD e Intel.
- Conexão real pelo Radmin VPN, descoberta por broadcast e firewall.
- FPS, latência e banda reais; 4K a 165 FPS.
- 10 ou mais viewers ao mesmo tempo.
- Câmera em webcams e placas de captura reais.
- Player na GPU em placas NVIDIA, AMD e Intel (o caminho pela CPU continua de reserva).
- Atualização ponta a ponta da 1.1.1 para a 1.2.0 pelo app.
- Controles de quem assiste entre dois PCs reais: qualidade, música e apps, com o Spotify e o navegador.
- Cartão da placa de vídeo numa RTX com driver antigo, com o código 43 e num notebook com duas placas.
- Voz e chat pela rede real do Radmin, com microfones reais (o autoteste usa um tom no lugar do microfone).
- Câmera junto com a tela: duas sessões de encoder ao mesmo tempo em GPUs reais.
- Som do PC por app no Windows 10 e 11 de verdade, com apps como Spotify, Discord, navegadores e jogos (e a festa de um app só).
- Controles de mídia com Spotify, navegadores e o Reprodutor de Mídia de verdade.
- Fotos e imagens pela rede real do Radmin, inclusive passando pelo relay.
- Instalação do Radmin VPN pelo app num PC sem ele (o pedido de permissão e o driver do adaptador).

## Próximos passos

- Cursor desenhado na captura DXGI (a WGC já inclui o cursor).
- Modo relay: um viewer repassa o vídeo para outros, para passar do limite de upload do host.
- Controle remoto de mouse e teclado, com autorização separada.
- Criptografia da voz e do chat da rede com uma senha da sala (hoje eles contam com o túnel do Radmin).
