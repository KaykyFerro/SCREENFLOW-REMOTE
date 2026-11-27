# Servidor Linux opcional (planejamento)

Nada disto foi construído. É o plano para um servidor opcional que convive com o Radmin VPN, para quem não quer ou não
pode usar o Radmin. O Radmin continua sendo o caminho padrão, e o app nunca depende do servidor para funcionar na rede
do Radmin.

## Objetivo

- Achar e entrar numa sala sem uma VPN instalada.
- Passar vídeo, som, voz e chat quando dois PCs não se alcançam direto.
- Não ver o conteúdo: o servidor só repassa pacotes cifrados.

## Acesso pelo app

- Uma aba "Servidor" nas Configurações, com o endereço (`https://…`), o estado da conexão e "Entrar" ou "Sair".
- Na tela Assistir, as salas do servidor aparecem junto com os hosts do Radmin, com um selo dizendo por onde a conexão
  passa.
- Sem servidor configurado, nada muda.

## Autenticação

- Conta por e-mail com um link de entrada (sem senha guardada no servidor) ou entrar pelo GitHub (OAuth).
- O app guarda um token de acesso curto e um de renovação, protegidos com DPAPI no PC.
- Cada PC tem um par de chaves próprio (Ed25519). O servidor guarda só a chave pública, que serve para assinar a
  presença e acabar com a limitação de "um membro pode se passar por outro" da sala privada.

## Salas e descoberta

- Uma sala tem dono, membros e convites. É a sala privada da 1.4.0, com a lista de membros guardada no servidor.
- O convite continua sendo o código `SF1-…`. O servidor recebe só um identificador derivado (a etiqueta da sala) e
  nunca a chave.
- Para achar quem está online na sala, o servidor guarda a última presença assinada de cada membro, com validade curta.

## Relay

- Primeiro tenta a conexão direta, com a ajuda do servidor para atravessar o NAT (STUN, UDP hole punching).
- Se não der, o tráfego passa pelo relay do servidor (TURN, ou um relay UDP próprio que entende só o cabeçalho dos
  pacotes).
- O relay repassa sem decifrar, com limite de banda por sala e por conta.
- Um host com muitos viewers manda uma cópia ao relay, e o relay distribui (fan-out). Isso tira o limite de upload do
  host.

## Criptografia

- De ponta a ponta, como hoje:
  - A transmissão usa ECDH e AES-GCM por viewer.
  - A sala usa a chave do convite com rotação.
- O servidor vê quem fala com quem e quanto, mas não o conteúdo.
- Tudo com TLS 1.3 até o servidor, e o UDP do relay com DTLS ou com o lacre que já existe.

## Implantação

- Um binário .NET (ou Go) em contêiner, com Postgres para contas e salas e Redis para a presença.
- Atrás de um proxy (Caddy) com certificado automático.
- Um `docker compose` para quem quiser hospedar o próprio servidor.
- Um servidor por região (América do Sul primeiro), com o relay perto de quem usa.

## Atualização

- O servidor fala uma versão de protocolo, anunciada no Hello como as Features de hoje.
- O app aceita servidores uma versão para trás.
- O servidor é atualizado com troca sem queda (dois contêineres e o proxy trocando de um para o outro).

## Disponibilidade

- Se o servidor cair, quem está no Radmin não percebe nada.
- Quem está pelo servidor perde a sala até ele voltar. Conexões diretas já abertas continuam.
- Meta inicial: um servidor só, com backup diário do banco. Mais servidores só com uso que justifique.

## Latência

- Pela conexão direta, a latência é a mesma de hoje.
- Pelo relay, soma a ida e a volta até o servidor (estimativa: 10 a 40 ms dentro do Brasil). Por isso o relay fica
  perto de quem usa.
- O app mostra no Monitoramento por onde a conexão passa e o ping até o relay.

## Custo

- O que pesa é a banda do relay: uma transmissão de 20 Mbps para 5 pessoas pelo relay passa de 100 Mbps.
- Uma VPS pequena com 1 a 2 TB de tráfego por mês custa na faixa de US$ 5 a 20 e aguenta descoberta, presença e
  algumas transmissões pelo relay.
- Limites por conta (banda e horas no relay) evitam surpresas.

## Convivência com o Radmin

- O app tenta nesta ordem: Radmin (se o outro PC estiver nele), depois a conexão direta pelo servidor, e só então o
  relay.
- A sala privada é a mesma nos dois caminhos: o mesmo convite e a mesma chave.
- A descoberta por broadcast no Radmin continua igual. O servidor só soma salas na lista.

## Ordem sugerida

1. Contas, salas e presença assinada (sem relay), para achar as pessoas.
2. Conexão direta com STUN e hole punching.
3. Relay para quando a direta falha.
4. Fan-out do relay para muitos viewers.
