#!/usr/bin/env bash
# Instala (ou atualiza) o servidor ScreenFlow 1.4.1 como serviço do systemd, e o Tailscale se for pedido.
#
#   sudo ./install-server.sh --tailscale                 instala o Tailscale (se faltar), entra na conta e instala o servidor
#   sudo ./install-server.sh --nome "Servidor do Kayky"  nome que aparece no app
#   sudo ./install-server.sh --senha                     pergunta a senha do servidor (sem mostrar na tela)
#   sudo ./install-server.sh --remover                   desinstala (o chat e os canais ficam guardados)
#
# Nunca usa chave de autenticação do Tailscale: o "tailscale up" mostra um link e a pessoa entra na própria conta.
set -euo pipefail

SERVICE=screenflow-servidor
INSTALL_DIR=/opt/screenflow-servidor
ENV_FILE=/etc/screenflow-servidor.env
UNIT_FILE=/etc/systemd/system/${SERVICE}.service
DATA_DIR=/var/lib/${SERVICE}
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ARGS=("$@")

TAILSCALE=0
ONLY_TAILSCALE=0
FIREWALL=1
REMOVE=0
PURGE=0
ASK_PASSWORD=0
NAME=""
PASSWORD=""
SET_PASSWORD=0
NO_PASSWORD=0
PORT=""

say()  { printf '\033[1;32m»\033[0m %s\n' "$*"; }
warn() { printf '\033[1;33m!\033[0m %s\n' "$*" >&2; }
die()  { printf '\033[1;31m✗\033[0m %s\n' "$*" >&2; exit 1; }

usage() {
  sed -n '2,9p' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//'
  cat <<'EOF'

Opções:
  --tailscale           Instala o Tailscale pelo script oficial (tailscale.com/install.sh) e entra na conta
  --somente-tailscale   No ufw, libera as portas só para quem vem pelo Tailscale (interface tailscale0)
  --nome NOME           Nome do servidor no app
  --senha [SENHA]       Senha do servidor (sem o valor, pergunta sem mostrar na tela)
  --sem-senha           Tira a senha
  --porta N             Portas N/tcp, N+1/udp e N+2/udp (padrão 47810)
  --sem-firewall        Não mexe no firewall
  --remover             Desinstala o serviço (guarda o chat e os canais em /var/lib/screenflow-servidor)
  --apagar-dados        Com --remover, apaga também o chat e os canais
  --ajuda               Mostra esta ajuda
EOF
}

while [[ $# -gt 0 ]]; do
  case "$1" in
    --tailscale) TAILSCALE=1 ;;
    --somente-tailscale) ONLY_TAILSCALE=1 ;;
    --sem-firewall) FIREWALL=0 ;;
    --remover) REMOVE=1 ;;
    --apagar-dados) PURGE=1 ;;
    --nome) [[ $# -ge 2 ]] || die "Faltou o nome depois de --nome."; NAME="$2"; shift ;;
    --nome=*) NAME="${1#*=}" ;;
    --senha)
      if [[ $# -ge 2 && "$2" != --* ]]; then PASSWORD="$2"; SET_PASSWORD=1; shift; else ASK_PASSWORD=1; fi ;;
    --senha=*) PASSWORD="${1#*=}"; SET_PASSWORD=1 ;;
    --sem-senha) NO_PASSWORD=1 ;;
    --porta) [[ $# -ge 2 ]] || die "Faltou o número depois de --porta."; PORT="$2"; shift ;;
    --porta=*) PORT="${1#*=}" ;;
    -h|--ajuda|--help) usage; exit 0 ;;
    *) die "Opção desconhecida: $1 (use --ajuda)." ;;
  esac
  shift
done

if [[ $EUID -ne 0 ]]; then
  command -v sudo >/dev/null 2>&1 || die "Rode como root."
  exec sudo -- "$0" ${ARGS[@]+"${ARGS[@]}"}
fi
command -v systemctl >/dev/null 2>&1 || die "Este PC não usa systemd. Rode o servidor direto: ./screenflow-servidor --ajuda"

if [[ -n "$PORT" ]] && ! [[ "$PORT" =~ ^[0-9]+$ && "$PORT" -ge 1024 && "$PORT" -le 65533 ]]; then
  die "Porta inválida: $PORT (use um número de 1024 a 65533)."
fi

read_env() { # read_env CHAVE → o valor guardado no arquivo de ambiente (sem as aspas)
  [[ -f "$ENV_FILE" ]] || return 0
  local line
  line="$(grep -E "^$1=" "$ENV_FILE" | tail -n1 || true)"
  line="${line#*=}"
  line="${line#\"}"; line="${line%\"}"
  printf '%s' "$line" | sed -e 's/\\"/"/g' -e 's/\\\\/\\/g'
}

quote_env() { # aspas para o EnvironmentFile do systemd
  local value="$1"
  value="${value//\\/\\\\}"
  value="${value//\"/\\\"}"
  printf '"%s"' "$value"
}

firewall_ports() {
  local base="${1:-47810}"
  echo "${base}/tcp $((base + 1))/udp $((base + 2))/udp"
}

# ───────────────────────── Remover ─────────────────────────
if [[ $REMOVE -eq 1 ]]; then
  say "Desligando e removendo o servidor ScreenFlow…"
  systemctl disable --now "$SERVICE" 2>/dev/null || true
  OLD_PORT="$(read_env SCREENFLOW_PORTA)"
  rm -f "$UNIT_FILE"
  systemctl daemon-reload
  rm -rf "$INSTALL_DIR"
  if [[ $FIREWALL -eq 1 ]] && command -v ufw >/dev/null 2>&1 && ufw status 2>/dev/null | grep -q "Status: active"; then
    for rule in $(firewall_ports "$OLD_PORT"); do
      ufw --force delete allow "$rule" >/dev/null 2>&1 || true
      ufw --force delete allow in on tailscale0 to any port "${rule%/*}" proto "${rule#*/}" >/dev/null 2>&1 || true
    done
  fi
  if [[ $PURGE -eq 1 ]]; then
    rm -f "$ENV_FILE"
    rm -rf "$DATA_DIR" "/var/lib/private/$SERVICE"
    say "Servidor removido, com o chat, os canais e a configuração."
  else
    say "Servidor removido. O chat e os canais continuam em $DATA_DIR e a configuração em $ENV_FILE."
  fi
  exit 0
fi

[[ -x "$HERE/screenflow-servidor" ]] || die "Não achei o programa screenflow-servidor ao lado deste script ($HERE)."

# ───────────────────────── Tailscale ─────────────────────────
if [[ $TAILSCALE -eq 1 ]]; then
  if ! command -v tailscale >/dev/null 2>&1; then
    command -v curl >/dev/null 2>&1 || die "O curl é necessário para instalar o Tailscale (ex.: sudo apt install curl)."
    say "Instalando o Tailscale pelo script oficial (https://tailscale.com/install.sh)…"
    curl -fsSL https://tailscale.com/install.sh | sh
  else
    say "Tailscale já instalado ($(tailscale version 2>/dev/null | head -n1))."
  fi
  systemctl enable --now tailscaled >/dev/null 2>&1 || warn "Não deu para ligar o tailscaled pelo systemctl."
  if ! tailscale ip -4 >/dev/null 2>&1; then
    say "Entrando na rede do Tailscale: abra o link que aparecer, entre na sua conta e volte aqui."
    tailscale up || warn "O tailscale up não terminou. Rode \"sudo tailscale up\" depois e reinicie o servidor."
  fi
  if TS_IP="$(tailscale ip -4 2>/dev/null | head -n1)" && [[ -n "$TS_IP" ]]; then
    say "Tailscale ligado: este PC é $TS_IP na rede."
  fi
fi

# ───────────────────────── Programa ─────────────────────────
say "Copiando o servidor para $INSTALL_DIR…"
install -d -m 0755 "$INSTALL_DIR"
install -m 0755 "$HERE/screenflow-servidor" "$INSTALL_DIR/screenflow-servidor.novo"
mv -f "$INSTALL_DIR/screenflow-servidor.novo" "$INSTALL_DIR/screenflow-servidor"
if [[ -f "$HERE/LEIA-ME.txt" ]]; then install -m 0644 "$HERE/LEIA-ME.txt" "$INSTALL_DIR/LEIA-ME.txt"; fi
install -m 0644 "$HERE/screenflow-servidor.service" "$UNIT_FILE"

# ───────────────────────── Configuração ─────────────────────────
CUR_NAME="$(read_env SCREENFLOW_NOME)"
CUR_PASSWORD="$(read_env SCREENFLOW_SENHA)"
CUR_PORT="$(read_env SCREENFLOW_PORTA)"
[[ -n "$NAME" ]] || NAME="$CUR_NAME"
[[ -n "$PORT" ]] || PORT="$CUR_PORT"
if [[ $ASK_PASSWORD -eq 0 && $SET_PASSWORD -eq 0 && $NO_PASSWORD -eq 0 && ! -f "$ENV_FILE" && -t 0 ]]; then
  ASK_PASSWORD=1
fi
if [[ $ASK_PASSWORD -eq 1 ]]; then
  read -r -s -p "Senha do servidor (Enter para ficar sem senha): " PASSWORD; echo
  if [[ -n "$PASSWORD" ]]; then
    read -r -s -p "Repita a senha: " AGAIN; echo
    [[ "$PASSWORD" == "$AGAIN" ]] || die "As senhas não são iguais."
  fi
  SET_PASSWORD=1
fi
if [[ $NO_PASSWORD -eq 1 ]]; then PASSWORD=""
elif [[ $SET_PASSWORD -eq 0 ]]; then PASSWORD="$CUR_PASSWORD"
fi
[[ "$PASSWORD" != *$'\n'* && "$NAME" != *$'\n'* ]] || die "O nome e a senha não podem ter quebra de linha."
[[ ${#PASSWORD} -le 128 ]] || die "A senha passa de 128 caracteres."

umask 077
{
  echo "# Configuração do servidor ScreenFlow (lida pelo systemd; só o root lê este arquivo)."
  [[ -n "$NAME" ]] && echo "SCREENFLOW_NOME=$(quote_env "$NAME")"
  [[ -n "$PASSWORD" ]] && echo "SCREENFLOW_SENHA=$(quote_env "$PASSWORD")"
  [[ -n "$PORT" ]] && echo "SCREENFLOW_PORTA=$PORT"
  true
} > "$ENV_FILE.novo"
chmod 0600 "$ENV_FILE.novo"
mv -f "$ENV_FILE.novo" "$ENV_FILE"
umask 022

# ───────────────────────── Firewall ─────────────────────────
PORTS="$(firewall_ports "$PORT")"
if [[ $FIREWALL -eq 1 ]]; then
  if command -v ufw >/dev/null 2>&1 && ufw status 2>/dev/null | grep -q "Status: active"; then
    for rule in $PORTS; do
      if [[ $ONLY_TAILSCALE -eq 1 ]]; then
        ufw allow in on tailscale0 to any port "${rule%/*}" proto "${rule#*/}" comment "ScreenFlow servidor" >/dev/null
      else
        ufw allow "$rule" comment "ScreenFlow servidor" >/dev/null
      fi
    done
    say "Firewall (ufw): portas $PORTS liberadas$([[ $ONLY_TAILSCALE -eq 1 ]] && echo " só pelo Tailscale")."
  elif command -v firewall-cmd >/dev/null 2>&1 && firewall-cmd --state >/dev/null 2>&1; then
    for rule in $PORTS; do firewall-cmd --quiet --permanent --add-port="$rule"; done
    firewall-cmd --quiet --reload
    say "Firewall (firewalld): portas $PORTS liberadas na zona padrão."
  else
    say "Nenhum firewall ligado (ufw ou firewalld): nada para liberar."
  fi
fi

# ───────────────────────── Serviço ─────────────────────────
systemctl daemon-reload
systemctl enable "$SERVICE" >/dev/null 2>&1
systemctl restart "$SERVICE"
sleep 2
if systemctl is-active --quiet "$SERVICE"; then
  say "Servidor ScreenFlow ligado (liga sozinho quando o PC liga)."
else
  warn "O servidor não ficou ligado. Veja o que aconteceu com: journalctl -u $SERVICE -n 50"
  exit 1
fi

echo
echo "No app de cada pessoa: Configurações › Servidor › endereço do servidor:"
if command -v tailscale >/dev/null 2>&1 && TS_IP="$(tailscale ip -4 2>/dev/null | head -n1)" && [[ -n "$TS_IP" ]]; then
  echo "  $TS_IP   (Tailscale)"
fi
for ip in $(hostname -I 2>/dev/null); do
  case "$ip" in
    100.*|*:*) ;;
    26.*) echo "  $ip   (Radmin VPN)" ;;
    *) echo "  $ip   (rede local)" ;;
  esac
done
[[ -n "$PASSWORD" ]] && echo "Senha: a que você escolheu (a mesma no app de cada pessoa)." || echo "Sem senha: qualquer PC que alcance este servidor entra."
echo
echo "Ver o que está acontecendo:  journalctl -u $SERVICE -f"
echo "Mudar nome ou senha:         sudo ./install-server.sh --nome \"Novo nome\" --senha"
echo "Desinstalar:                 sudo ./install-server.sh --remover"
