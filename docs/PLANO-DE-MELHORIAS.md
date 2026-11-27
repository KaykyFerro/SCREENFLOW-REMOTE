# Plano de melhorias (estado na 1.4.1)

Este arquivo registra o estado real de cada item do plano consolidado depois da 1.3.0. "Feito" quer dizer implementado,
com testes automatizados e passando no CI do Windows. Nada aqui foi testado entre PCs reais ainda: o que depende disso
está em [ROADMAP.md](ROADMAP.md), na parte "Ainda não testado".

Legenda: **Feito** · **Parcial** (uma parte entrou, o resto está descrito) · **Avaliado** (estudado e documentado, sem
código) · **Pendente**.

## Decisões de rede

- O Radmin VPN continua sendo um caminho de conexão. Na 1.4.1 o Tailscale entrou ao lado dele, e nada depende de um
  servidor: sem servidor configurado, tudo continua direto entre os PCs.
- `AllowNonRadminFallback` saiu das configurações. Ele nunca teve tela nem efeito. Um arquivo antigo que ainda tiver a
  chave é lido normalmente, e ela some no próximo salvamento.
- O servidor ScreenFlow (1.4.1) é um PC seu, alcançado pelo Tailscale, pelo Radmin VPN ou pela rede local. O guia está
  em [SERVIDOR.md](SERVIDOR.md). O servidor público na internet, com contas, continua só no plano (no fim do guia).

## O que já estava pronto e foi preservado

- A 1.3.0 inteira (canais, volume por pessoa, SMTC, chat no Google Drive, configurações em abas).
- As correções do atualizador e da cópia das configurações inválidas pedidas no plano não estavam no repositório. Elas
  foram feitas de novo na 1.4.0 (itens 6 e "Configurações" abaixo).

## Pedidos da 1.4.1

- **Canais livres — Feito.** Só o canal de texto `geral` e o de voz `Geral` vêm prontos. Cada pessoa cria os seus na
  sala direta, e no servidor qualquer pessoa cria, renomeia e apaga os canais, que ficam guardados nele.
- **Temas por pessoa — Feito.** 10 cores (uma de cada), em cima do tema escuro ou do claro, guardadas só no PC de quem
  escolheu e aplicadas na hora.
- **Radmin VPN e Tailscale — Feito.** O app mostra e liga o Tailscale, instala o MSI oficial conferido e, sem servidor,
  acha os PCs da rede do Tailscale sozinho (ele não repassa broadcast). O app continua só para Windows. O servidor roda
  também no Linux.
- **Servidor — Feito.** A tela e o chat geral vão para o servidor, e os outros assistem e conversam por ele. Guia em
  [SERVIDOR.md](SERVIDOR.md).
- **Instalador — Feito.** Opções de instalar o Tailscale e o Radmin VPN junto e de escolher o modo do servidor (entrar
  num servidor ou este PC é o servidor). O servidor Linux tem o `install-server.sh --tailscale`.
- **Revisão e otimização — Feito.** A sala guarda até 512 pessoas (o máximo do servidor), a voz e a presença usam a
  lista da sala sem ordenar, e o autoteste do CI passa uma transmissão, o chat e os canais por um servidor de verdade.
- Nada disto foi testado entre PCs reais ainda. A lista está em [ROADMAP.md](ROADMAP.md).

## Minutagem do vídeo transmitido — Feito

- A minutagem fica ligada à sessão de mídia do app transmitido, nunca à mais recente:
  - A primeira escolha é a sessão que quem transmite escolheu.
  - Depois vem o app da janela transmitida ou da festa de um app só.
  - Na tela inteira, liga sozinha só quando há uma única sessão com som indo junto. Senão, quem transmite escolhe o app.
- Mostra a posição e a duração, deixa arrastar, voltar e avançar 10 s (botões e as teclas ← → J L), pausar e retomar.
- A busca acontece no app de origem, no PC do host, pelos controles de mídia do Windows. Todo mundo vê o ponto novo
  porque a tela do host muda. Não há scraping, credenciais nem teclas genéricas.
- O host relê a posição para confirmar a busca e responde com o desfecho (pronto, recusado, não confirmado, indisponível
  ou vídeo trocado). Quem assiste mostra a falha quando o tempo acaba e nunca finge sucesso.
- Ao vivo, segue o trecho de busca que o app informa. Quando o app não deixa buscar, a barra fica desligada e diz o
  motivo.
- O pedido só é enviado ao soltar a barra. Cada pedido leva um número, então repetições são ignoradas, e o limite de
  pedidos por pessoa continua valendo.
- Respeita a chave "Deixar quem assiste controlar".
- Compatibilidade:
  - Os campos novos são opcionais.
  - Com um host 1.3.0, quem assiste confirma pela posição que chega.
  - Com um viewer 1.3.0, o host segue aceitando os comandos antigos.

## Backlog em ordem

1. **Validação em PCs reais — Pendente.** Precisa do Kayky e de outros PCs. A lista do que testar está no ROADMAP.
2. **Sala privada — Feito.**
   - Convite com código `SF1-…`, independente do PIN e da aprovação da transmissão.
   - "Só quem eu aprovar", lista de permitidos, remover da sala e bloquear alguém só para você.
   - O dono recebe um aviso quando alguém com o convite pede para entrar.
3. **Criptografia de ponta a ponta — Feito, com limites.**
   - Na sala privada, presença, chat, voz e imagens vão lacrados com AES-256-GCM.
   - A chave sai do convite e muda de geração a cada remoção ou quando o dono quiser. A chave nova vai cifrada para
     cada pessoa (ECDH P-256 com HKDF), e a anterior vale por mais 2 minutos.
   - Limites, que também aparecem na tela:
     - Quem tem o convite consegue ler até ser removido.
     - Dentro da sala não há assinatura por pessoa, então um membro poderia se passar por outro.
     - Quem estava desligado na troca de chave precisa de um convite novo.
     - Versões antigas não entram na sala privada.
   - **Proteção local do histórico — Avaliado.** Dá para cifrar o histórico com DPAPI do usuário do Windows. Mas o
     histórico pode ir para o Google Drive para ser lido em outro PC, e com DPAPI ele só abriria no mesmo usuário e no
     mesmo PC. A proposta é uma opção "Proteger o histórico neste PC", desligada por padrão quando a pasta é a do Drive.
     Fica para uma próxima versão.
4. **Voz — Parcial.**
   - Feito:
     - Escolher a saída de som. Se o aparelho some, volta para a padrão.
     - Apertar para falar com qualquer tecla ou botão lateral do mouse, valendo em qualquer app, com 250 ms de folga
       ao soltar.
   - Avaliado: supressão de ruído (RNNoise ou o NS do WebRTC) e cancelamento de eco (o AEC do WebRTC ou o modo de
     comunicação do Windows).
     - Os dois pedem uma biblioteca nativa nova ou o processamento de voz do Windows, que muda o som de todo o PC.
     - O portão de voz e o apertar para falar já cortam a maior parte do ruído de fundo.
     - A recomendação é testar o RNNoise (pequeno, licença BSD) numa próxima versão, com uma chave para desligar.
5. **Região da tela, cursor, áreas tapadas e notificações — Feito.**
   - Escolher uma parte da tela ou da janela arrastando, esconder o cursor e tapar até 7 áreas de preto, mais o canto
     das notificações.
   - O corte e as máscaras são feitos na GPU antes do encoder, então quem assiste nunca recebe o que fica embaixo.
   - Muda ao vivo.
   - Na captura de reserva (DXGI), o cursor nunca aparece.
6. **Authenticode e testes do atualizador — Feito.**
   - Só HTTPS do GitHub, também depois dos redirecionamentos.
   - O `.sha256` é obrigatório, e o tamanho e o hash são conferidos antes de o arquivo ganhar o nome final.
   - Dá para cancelar o download, e um arquivo pela metade é apagado.
   - Um instalador assinado precisa ter assinatura válida. Os instaladores de hoje não são assinados e continuam
     valendo só com o SHA-256.
   - Quando o Windows recusa abrir o instalador, o erro é claro.
   - Testes cobrem as URLs aceitas, o checksum e o tamanho. O download real é exercitado só na atualização de verdade.
   - Assinar o instalador exige um certificado de assinatura de código, que é uma compra do Kayky.
7. **Chat: busca, respostas, editar e remover, fixados, galeria — Pendente.**
8. **Perfis de transmissão e favoritos de hosts — Pendente.**
9. **Câmera sobreposta arrastável e redimensionável — Pendente.**
10. **Notificações por categoria, não perturbe, iniciar com o Windows, idioma, busca nas Configurações, atalhos —
    Pendente.**
11. **Acessibilidade — Pendente.** Na 1.4.0, a barra de minutagem também funciona pelo teclado (← → J L) e o seletor de
    área tem nome para o leitor de tela. Uma revisão geral (leitor de tela, contraste, ordem do Tab) fica para depois.
12. **Pacote de diagnóstico sem dados pessoais — Pendente.**
13. **Dividir HostSession, RoomService e code-behind — Pendente.** Os pedaços novos da 1.4.0 já entraram separados:
    `MediaSeek`, `MediaBindingRules`, `PrivateRoom`, `PushToTalk`, `CaptureArea`, `CaptureCrop` e `UpdateIntegrity`.
    Uma divisão maior pede testes entre PCs reais antes, para não quebrar nada.
14. **Gravação e replay — Pendente (sem avaliação ainda).**
15. **Servidor — Feito na 1.4.1** (o servidor seu, pelo Tailscale, pelo Radmin VPN ou pela rede local), com o guia em
    [SERVIDOR.md](SERVIDOR.md). Roda dentro do app, no Linux (serviço do systemd) e no Windows. O servidor público na
    internet, com contas, STUN e relay, continua **Pendente**. Nada do servidor foi testado entre PCs reais ainda.

## Configurações

- As configurações novas da 1.4.0 têm valores padrão que mantêm o comportamento antigo: cursor visível, tela inteira,
  nada tapado, voz pela saída padrão, apertar para falar desligado e sem sala privada.
- Um arquivo da 1.3.0 abre sem migração.
- Valores fora do lugar são corrigidos ao abrir: regiões fora da tela, códigos de convite inválidos e listas grandes
  demais.
