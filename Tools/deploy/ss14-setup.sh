#!/usr/bin/env bash
# SPDX-License-Identifier: AGPL-3.0-or-later
#
# Первичная настройка автообновления сервера.
# Создаёт рабочую папку сервера, выносит из репозитория конфиг и данные,
# генерирует токен для POST /update и пишет файл конфигурации скриптов.
#
# Запускать один раз:  bash Tools/deploy/ss14-setup.sh
# Повторный запуск безопасен: существующие настройки и токен сохраняются.

SS14_ALLOW_NO_CONF=1
source "$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/lib.sh"

usage() {
    cat <<'EOF'
Использование: bash Tools/deploy/ss14-setup.sh [опции]

  --run-dir PATH    рабочая папка сервера (по умолчанию ~/ss14-run)
  --repo PATH       папка репозитория (по умолчанию определяется автоматически)
  --branch NAME     ветка, из которой берутся обновления (по умолчанию master)
  --port N          порт status-хоста сервера (по умолчанию берётся из конфига)
  -h, --help        эта справка
EOF
}

PORT_OVERRIDE=""

# Уже существующая конфигурация — источник значений по умолчанию,
# аргументы командной строки её перекрывают.
if [[ -f $CONF_FILE ]]; then
    ss14_log "Найден $CONF_FILE, беру значения из него."
    # shellcheck disable=SC1090
    source "$CONF_FILE"
fi

while [[ $# -gt 0 ]]; do
    case "$1" in
        --run-dir) RUN_DIR="$2"; shift 2 ;;
        --repo)    REPO_DIR="$2"; shift 2 ;;
        --branch)  GIT_BRANCH="$2"; shift 2 ;;
        --port)    PORT_OVERRIDE="$2"; shift 2 ;;
        -h|--help) usage; exit 0 ;;
        *) usage >&2; ss14_die "Неизвестный аргумент: $1" ;;
    esac
done

ss14_derive_paths

# --- Проверки окружения ---

for cmd in git dotnet rsync curl awk; do
    command -v "$cmd" >/dev/null 2>&1 || ss14_die "Не найдена команда '$cmd'. Установите её и повторите."
done

[[ -d "$REPO_DIR/.git" ]] || ss14_die "$REPO_DIR не похож на git-репозиторий."

ss14_log "Репозиторий:    $REPO_DIR"
ss14_log "Рабочая папка:  $RUN_DIR"

# --- Структура рабочей папки ---

mkdir -p "$LIVE_DIR/bin/Content.Server" "$LIVE_DIR/bin/Content.Client" "$LIVE_DIR/Resources"
mkdir -p "$DATA_DIR" "$STATE_DIR" "$LOG_DIR"

# --- Конфиг сервера ---

REPO_CONFIG="$REPO_DIR/bin/Content.Server/server_config.toml"

if [[ ! -f $SERVER_CONFIG ]]; then
    if [[ -f $REPO_CONFIG ]]; then
        cp "$REPO_CONFIG" "$SERVER_CONFIG"
        ss14_log "Конфиг сервера скопирован из $REPO_CONFIG"
    elif [[ -f "$REPO_DIR/RobustToolbox/Robust.Server/server_config.toml" ]]; then
        cp "$REPO_DIR/RobustToolbox/Robust.Server/server_config.toml" "$SERVER_CONFIG"
        ss14_warn "Взят стандартный конфиг из RobustToolbox — проверьте настройки в $SERVER_CONFIG"
    else
        ss14_die "Не найден server_config.toml ни в bin/Content.Server, ни в RobustToolbox."
    fi
else
    ss14_log "Конфиг сервера уже существует: $SERVER_CONFIG"
fi

# Читает значение ключа из секции TOML-файла.
read_toml_value() {
    local file="$1" section="$2" key="$3"
    awk -v sec="$section" -v k="$key" '
        /^[[:space:]]*\[[^]]*\][[:space:]]*$/ {
            s = $0; gsub(/[][[:space:]]/, "", s); next
        }
        s == sec && $0 ~ "^[[:space:]]*" k "[[:space:]]*=" {
            sub(/^[^=]*=[[:space:]]*/, "")
            sub(/[[:space:]]*#.*$/, "")
            gsub(/"/, "")
            gsub(/^[[:space:]]+|[[:space:]]+$/, "")
            print
            exit
        }
    ' "$file"
}

# --- Токен для /update ---

TOKEN_IN_CONFIG="$(read_toml_value "$SERVER_CONFIG" watchdog token)"

if [[ -n $TOKEN_IN_CONFIG ]]; then
    WATCHDOG_TOKEN="$TOKEN_IN_CONFIG"
    ss14_log "В конфиге сервера уже задан watchdog.token — использую его."
else
    if [[ -z ${WATCHDOG_TOKEN:-} ]]; then
        if command -v openssl >/dev/null 2>&1; then
            WATCHDOG_TOKEN="$(openssl rand -hex 24)"
        else
            WATCHDOG_TOKEN="$(head -c 24 /dev/urandom | od -An -tx1 | tr -d ' \n')"
        fi
    fi

    if grep -qE '^[[:space:]]*\[watchdog\][[:space:]]*$' "$SERVER_CONFIG"; then
        awk -v tok="$WATCHDOG_TOKEN" '
            { print }
            /^[[:space:]]*\[watchdog\][[:space:]]*$/ && !done {
                print "# Токен локального API POST /update, им пользуются скрипты Tools/deploy."
                print "token = \"" tok "\""
                done = 1
            }
        ' "$SERVER_CONFIG" > "$SERVER_CONFIG.tmp" && mv "$SERVER_CONFIG.tmp" "$SERVER_CONFIG"
    else
        {
            echo ""
            echo "[watchdog]"
            echo "# Токен локального API POST /update, им пользуются скрипты Tools/deploy."
            echo "token = \"$WATCHDOG_TOKEN\""
        } >> "$SERVER_CONFIG"
    fi
    ss14_log "В $SERVER_CONFIG добавлен watchdog.token."
fi

chmod 600 "$SERVER_CONFIG"

# --- Порт status-хоста ---

if [[ -n $PORT_OVERRIDE ]]; then
    STATUS_URL="http://127.0.0.1:$PORT_OVERRIDE"
else
    NET_PORT="$(read_toml_value "$SERVER_CONFIG" net port)"
    STATUS_URL="http://127.0.0.1:${NET_PORT:-1212}"
fi

STATUS_BIND="$(read_toml_value "$SERVER_CONFIG" status bind)"
if [[ -n $STATUS_BIND ]]; then
    ss14_warn "В конфиге задан status.bind = $STATUS_BIND."
    ss14_warn "Проверьте, что STATUS_URL=$STATUS_URL в $CONF_FILE ему соответствует."
fi

# --- Данные сервера (БД настроек, банов и т.д.) ---

REPO_DATA="$REPO_DIR/bin/Content.Server/data"

if [[ -d $REPO_DATA ]] && [[ -z "$(ls -A "$DATA_DIR" 2>/dev/null)" ]]; then
    ss14_log "Переношу данные сервера из $REPO_DATA в $DATA_DIR ..."
    cp -a "$REPO_DATA/." "$DATA_DIR/"
    ss14_log "Готово. Старая папка оставлена на месте как резервная копия."
fi

# --- Первичное наполнение рабочей копии ---

if [[ -f "$REPO_DIR/bin/Content.Server/$SERVER_DLL" ]]; then
    ss14_log "Копирую текущую сборку в $LIVE_DIR (первый раз это займёт время) ..."
    rsync -a --delete --exclude '/data/' --exclude '/logs/' \
        "$REPO_DIR/bin/Content.Server/" "$LIVE_DIR/bin/Content.Server/"

    if [[ -d "$REPO_DIR/bin/Content.Client" ]]; then
        rsync -a --delete "$REPO_DIR/bin/Content.Client/" "$LIVE_DIR/bin/Content.Client/"
    else
        ss14_warn "Не найден $REPO_DIR/bin/Content.Client — клиентский zip собрать не из чего."
        ss14_warn "Соберите всё решение: cd $REPO_DIR && dotnet build -c $BUILD_CONFIG"
    fi

    rsync -a --delete "$REPO_DIR/Resources/" "$LIVE_DIR/Resources/"
    git -C "$REPO_DIR" rev-parse HEAD > "$DEPLOYED_MARK"
    ss14_log "Рабочая копия готова."
else
    ss14_warn "В $REPO_DIR/bin/Content.Server не найден $SERVER_DLL."
    ss14_warn "Соберите сервер (dotnet build -c $BUILD_CONFIG) и запустите setup ещё раз."
fi

# --- Файл конфигурации скриптов ---

if [[ -f $CONF_FILE ]]; then
    cp "$CONF_FILE" "$CONF_FILE.bak"
fi

cat > "$CONF_FILE" <<EOF
# Конфигурация скриптов автообновления сервера (Tools/deploy).
# Создан ss14-setup.sh $(date '+%Y-%m-%d %H:%M:%S')

REPO_DIR="$REPO_DIR"
RUN_DIR="$RUN_DIR"

GIT_REMOTE="$GIT_REMOTE"
GIT_BRANCH="$GIT_BRANCH"

BUILD_CONFIG="$BUILD_CONFIG"
SERVER_DLL="$SERVER_DLL"

STATUS_URL="$STATUS_URL"
WATCHDOG_TOKEN="$WATCHDOG_TOKEN"

# 1 — сообщить игрокам сразу после git pull, сборка идёт во время раунда.
# 0 — сообщить только после успешной сборки.
NOTIFY_BEFORE_BUILD=$NOTIFY_BEFORE_BUILD

RESTART_DELAY=$RESTART_DELAY
BUILD_WAIT_TIMEOUT=$BUILD_WAIT_TIMEOUT
EOF

chmod 600 "$CONF_FILE"
chmod +x "$DEPLOY_DIR"/*.sh 2>/dev/null || true

cat <<EOF

Настройка завершена.

  Конфигурация скриптов : $CONF_FILE
  Конфиг сервера        : $SERVER_CONFIG   <- теперь правьте только его
  Данные сервера        : $DATA_DIR
  Рабочая копия сборки  : $LIVE_DIR

Дальше:

  1. Остановите старый сервер (Ctrl+C в screen -r ss14).
  2. Запустите супервизор вместо ручного dotnet run:
       screen -S ss14 $DEPLOY_DIR/ss14-server.sh
  3. Обновление после пуша в GitHub — одной командой:
       $DEPLOY_DIR/ss14-update.sh

EOF
