# Servidor ScreenFlow (1.4.1)

Um PC onde todo mundo se conecta. Em vez de cada pessoa mandar a tela direto para cada uma que assiste, quem transmite
manda uma cópia só para o servidor, e o servidor manda para cada pessoa que assiste por ele. O chat, a voz e os canais
também passam pelo servidor e ficam guardados nele.

```
            quem transmite ──(1 cópia)──►  SERVIDOR  ──► quem assiste
            quem transmite ──(1 cópia)──►  (chat,    ──► quem assiste
                                            voz e    ──► quem assiste
            todo mundo ◄──── chat e voz ──► canais)
```

O servidor é opcional. Sem ele, nada muda: a sala, o chat, a voz e as transmissões continuam direto entre os PCs, pelo
Radmin VPN, pelo Tailscale ou pela rede local.

## Quando vale a pena

- Com várias pessoas assistindo: o upload de quem transmite não multiplica pelo número de pessoas. O servidor é que
  precisa de upload (uma transmissão de 20 Mbps para 5 pessoas passa de 100 Mbps saindo do servidor).
- Com gente em casas diferentes, pelo Tailscale: todo mundo só precisa alcançar o servidor.
- Para o chat e os canais ficarem guardados num lugar só, mesmo com os PCs das pessoas desligados.

## Três jeitos de ter um servidor

### 1. No próprio app (Windows)

Em **Configurações › Servidor**, escolha **Este PC é o servidor** e salve. O servidor roda dentro do ScreenFlow
enquanto ele estiver aberto (também na bandeja, perto do relógio). A mesma aba mostra quem está no servidor e os
endereços deste PC para passar aos outros (o do Tailscale e o do Radmin VPN primeiro).

O nome e a senha do servidor ficam na mesma aba. O chat e os canais ficam em `%LOCALAPPDATA%\ScreenFlow\servidor`.

### 2. Programa do servidor no Linux (serviço do systemd)

Baixe `ScreenFlow-Servidor-linux-x64.tar.gz` (ou `-linux-arm64`, para Raspberry Pi e parecidos) na página de
releases, confira o `.sha256` e instale como serviço:

```bash
tar -xzf ScreenFlow-Servidor-linux-x64.tar.gz
cd screenflow-servidor
sudo ./install-server.sh --tailscale --nome "Servidor do Kayky" --senha
```

- `--tailscale` instala o Tailscale pelo script oficial (se faltar) e mostra o link para entrar na sua conta.
- `--senha` pergunta a senha sem mostrar na tela. Ela fica em `/etc/screenflow-servidor.env`, que só o root lê.
- O serviço liga sozinho quando o PC liga, roda com um usuário próprio, sem acesso ao resto do PC, e guarda o chat e
  os canais em `/var/lib/screenflow-servidor`.
- O script libera as portas no `ufw` ou no `firewalld`. Com `--somente-tailscale`, no `ufw`, só quem vem pelo Tailscale
  entra.

Outros comandos:

```bash
journalctl -u screenflow-servidor -f        # ver o que está acontecendo
sudo ./install-server.sh                    # atualizar (guarda o nome e a senha)
sudo ./install-server.sh --remover          # desinstalar (guarda o chat e os canais)
./screenflow-servidor --nome "Teste" --senha "minha senha"   # rodar sem instalar
./screenflow-servidor --ajuda               # todas as opções
```

O Radmin VPN não existe para Linux. Num servidor Linux, use o Tailscale (ou a rede local).

### 3. Programa do servidor no Windows

Baixe `ScreenFlow-Servidor-win-x64.zip`, confira o `.sha256`, extraia e rode:

```powershell
.\screenflow-servidor.exe --nome "Servidor do Kayky" --senha "minha senha"
```

Ele roda numa janela de console até o Ctrl+C. Libere as portas no Firewall do Windows (num PowerShell como
administrador, com o caminho onde você extraiu):

```powershell
netsh advfirewall firewall add rule name="Servidor ScreenFlow" dir=in action=allow program="C:\ScreenFlow-Servidor\screenflow-servidor.exe" profile=any enable=yes
```

Para a senha não aparecer na linha de comando, use a variável `SCREENFLOW_SENHA`. Para ligar junto com o Windows,
crie uma tarefa no Agendador de Tarefas que rode o programa ao iniciar.

Abra só um servidor por PC: o do app e o programa usam as mesmas portas. Se o programa já estiver aberto, o app em
"Este PC é o servidor" avisa e entra nele.

## Rede: Tailscale, Radmin VPN ou rede local

O servidor escuta em todas as redes do PC (ou só numa, com `--endereco`). Cada pessoa usa o endereço que alcança:

| Rede | Endereço do servidor | Observação |
| --- | --- | --- |
| Tailscale | o `100.x` do servidor ou o nome dele no Tailscale | todo mundo na mesma conta ou rede do Tailscale, ou com o servidor compartilhado com eles |
| Radmin VPN | o `26.x` do servidor | todo mundo na mesma rede do Radmin (só no Windows) |
| Rede local | o IP do servidor na rede da casa | só quem está na mesma rede |

O ScreenFlow nunca usa chave de autenticação do Tailscale nem guarda senha do Tailscale. Quando falta entrar na conta,
o próprio Tailscale mostra um link, e cada pessoa entra na conta dela pelo navegador.

### Portas

| Porta | Para quê |
| --- | --- |
| 47810/tcp | quem transmite e quem assiste entram por aqui |
| 47811/udp | vídeo e som chegando de quem transmite e saindo para quem assiste |
| 47812/udp | a sala: presença, chat, voz e canais |

Com `--porta N`, as portas ficam N/tcp, N+1/udp e N+2/udp, e no app o endereço vai com a porta (`100.64.1.2:N`).
Pelo Tailscale ou pelo Radmin VPN não é preciso abrir portas no roteador.

## Senha e quem pode entrar

- A senha do servidor vale para transmitir, assistir e entrar na sala do servidor. Sem senha, qualquer PC que alcance o
  servidor entra.
- **Pelo servidor, o PIN e a aprovação de quem transmite não valem: a senha do servidor faz esse papel.** Quem está no
  servidor com a senha assiste a qualquer transmissão que foi para ele. A conexão direta, fora do servidor, continua
  pedindo o PIN e a aprovação como antes.
- Senha errada para de tentar e avisa em Configurações › Servidor. Servidor cheio tenta de novo sozinho.
- Depois de muitas senhas erradas seguidas do mesmo IP, o servidor ignora as tentativas desse IP por um minuto.

## No app de cada pessoa

1. **Configurações › Servidor › Entrar num servidor ScreenFlow**: coloque o endereço (o `100.x`, o nome no Tailscale ou
   o `26.x`) e a senha, e salve. A aba mostra se o servidor foi achado e se a sala está conectada.
2. **Sala, chat e voz**: com o servidor configurado, a sala é a do servidor. Os canais são do servidor e ficam
   guardados nele. Qualquer pessoa no servidor cria, renomeia e apaga canais. Só o canal de texto `geral` e o de voz
   `Geral` vêm prontos. Renomear um canal de texto leva a conversa junto. Quem chega recebe as últimas mensagens.
3. **Transmitir**: com **Transmitir também pelo servidor** ligado, a transmissão vai para o servidor além de ficar
   aberta direto. A lista "Quem está assistindo" mostra o servidor e quantas pessoas assistem por lá.
4. **Assistir**: as transmissões do servidor aparecem na lista com o ícone de nuvem e abrem pelo servidor.

## Instalador do app

O instalador do ScreenFlow tem duas opções novas:

- **Instalar o Tailscale junto** e **Instalar o Radmin VPN junto** (as duas desmarcadas). O app baixa da página
  oficial, confere a assinatura digital (da Tailscale ou da Famatech) e instala. O Windows pede permissão. O MSI do
  Tailscale também é conferido pelo SHA-256 publicado ao lado dele.
- Uma página para o servidor: **Agora não**, **Entrar num servidor ScreenFlow** (pede o endereço) ou **Este PC é o
  servidor** (com a opção do firewall marcada, libera também o app no firewall, que é por onde o servidor do app
  escuta).

O instalador deixa o pedido em `%LOCALAPPDATA%\ScreenFlow\instalador.txt`. O app lê esse arquivo uma vez quando abre
(mesmo que ele não seja aberto no fim da instalação) e apaga. As mesmas opções também valem na linha de comando:

```
ScreenFlow.exe --modo-servidor
ScreenFlow.exe --entrar-servidor 100.64.1.2
ScreenFlow.exe --instalar-tailscale --instalar-radmin
```

Endereços estranhos (com espaço, `http://`, porta inválida) são ignorados. O pedido só troca o modo do servidor; a
senha é colocada depois, em Configurações › Servidor.

## Limites

| Limite | Padrão | Máximo | Opção |
| --- | --- | --- | --- |
| Transmissões ao mesmo tempo | 16 | 64 | `--max-transmissoes` |
| Pessoas assistindo cada transmissão | 64 | 256 | `--max-assistindo` |
| Pessoas na sala do servidor | 128 | 512 | `--max-pessoas` |

O servidor do app usa os padrões. Na prática, o limite costuma ser o upload do PC servidor.

## O que o servidor vê

- **A tela e o som**: o servidor decifra o que chega de quem transmite e cifra de novo para cada pessoa que assiste,
  com uma chave por pessoa (ECDH e AES-GCM, como na conexão direta). Quem controla o PC servidor pode ver o que passa
  por ele. Use um servidor seu ou de alguém de confiança.
- **A sala**: o servidor lê a presença (nome e canal de voz, para a lista) e o chat (para guardar e mostrar para quem
  chega). O chat fica em `ScreenFlow Chat` dentro da pasta de dados, por canal e por dia.
- **A sala privada** (1.4.0) funciona pelo servidor: a presença, o chat, a voz e as imagens vão lacrados com a chave do
  convite, e o servidor só repassa, sem conseguir ler nem guardar.

## Quando uma conexão cai

- Se cair a conexão de quem transmite, quem assiste pelo servidor espera até 2 minutos pela volta dela, e quem
  transmite tenta de novo sozinho.
- Se o servidor cair, quem transmite e quem assiste tentam de novo sozinhos até ele voltar. A transmissão direta, fora
  do servidor, continua no ar.
- A sala do servidor some até ele voltar. Para voltar à sala direta da rede, escolha **Sem servidor** em
  Configurações › Servidor.

## O que ficou para depois

O plano da 1.4.0 pensava num servidor público na internet. O que foi construído é um servidor seu, alcançado pelo
Tailscale, pelo Radmin VPN ou pela rede local. Ficaram para depois:

- Contas (e-mail ou GitHub) e salas guardadas no servidor, para entrar sem VPN.
- Conexão direta com a ajuda do servidor para atravessar o NAT (STUN e hole punching) e relay para quando ela falha.
- Presença assinada com uma chave por PC (acaba com "um membro da sala privada pode se passar por outro").
- Um contêiner com `docker compose` e certificado automático, para hospedar na nuvem.
