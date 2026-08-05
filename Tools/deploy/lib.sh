# SPDX-License-Identifier: AGPL-3.0-or-later
#
# Общие настройки и функции для скриптов автообновления сервера.
# Подключается остальными скриптами через source, самостоятельно не запускается.

set -euo pipefail

DEPLOY_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

# --- Значения по умолчанию. Переопределяются в файле конфигурации. ---

# Папка с git-репозиторием: отсюда сервер и запускается, здесь же идёт сборка.
REPO_DIR="$(cd "$DEPLOY_DIR/../.." && pwd)"
# Служебная папка: конфиг сервера, данные, состояние, логи.
RUN_DIR="$HOME/ss14-run"

GIT_REMOTE="origin"
GIT_BRANCH="master"

BUILD_CONFIG="Release"
SERVER_DLL="Content.Goobstation.Server.dll"

# Адрес status-хоста сервера. По умолчанию совпадает с net.port из server_config.toml.
STATUS_URL="http://127.0.0.1:1212"
# Токен для POST /update. Генерируется в ss14-setup.sh, должен совпадать
# с watchdog.token в server_config.toml.
WATCHDOG_TOKEN=""

# Пауза между перезапусками сервера, секунды.
RESTART_DELAY=3

CONF_FILE="${SS14_DEPLOY_CONF:-$HOME/.ss14-deploy.conf}"

# --- Загрузка конфигурации и производные пути ---

ss14_load_conf() {
    if [[ -f $CONF_FILE ]]; then
        # shellcheck disable=SC1090
        source "$CONF_FILE"
    elif [[ ${SS14_ALLOW_NO_CONF:-0} != 1 ]]; then
        echo "Не найден файл конфигурации: $CONF_FILE" >&2
        echo "Сначала запустите Tools/deploy/ss14-setup.sh" >&2
        exit 1
    fi

    ss14_derive_paths
}

# Пути, вычисляемые из RUN_DIR. Вынесено отдельно, потому что ss14-setup.sh
# переопределяет RUN_DIR аргументами уже после чтения файла конфигурации.
ss14_derive_paths() {
    DATA_DIR="$RUN_DIR/data"
    STATE_DIR="$RUN_DIR/state"
    LOG_DIR="$RUN_DIR/logs"
    SERVER_CONFIG="$RUN_DIR/server_config.toml"
    SERVER_BIN_DIR="$REPO_DIR/bin/Content.Server"

    # Просьба супервизору собрать проект перед следующим запуском сервера.
    PENDING_MARK="$STATE_DIR/build-pending"
    # Держится, пока идёт сборка: не даёт делать git pull в этот момент.
    BUILDING_MARK="$STATE_DIR/build-in-progress"
    STOP_MARK="$STATE_DIR/stop"
    BUILT_MARK="$STATE_DIR/built-commit"
}

# --- Вывод ---

ss14_log()  { printf '[%s] %s\n' "$(date '+%H:%M:%S')" "$*"; }
ss14_warn() { printf '[%s] ВНИМАНИЕ: %s\n' "$(date '+%H:%M:%S')" "$*" >&2; }
ss14_die()  { printf '[%s] ОШИБКА: %s\n' "$(date '+%H:%M:%S')" "$*" >&2; exit 1; }

# --- Уведомление сервера об обновлении ---

# Отправляет POST /update. Сервер объявляет игрокам в чат, что получено
# обновление, и выключается в конце раунда (или сразу, если никого нет).
# Возвращает 0 — уведомление принято, 1 — сервер не отвечает,
# 2 — сервер ответил ошибкой.
ss14_notify_update() {
    local out status
    set +e
    out="$(curl -sS -m 10 -o /dev/null -w '%{http_code}' \
        -X POST -H "WatchdogToken: $WATCHDOG_TOKEN" \
        "$STATUS_URL/update" 2>&1)"
    status=$?
    set -e

    if [[ $status -ne 0 ]]; then
        ss14_warn "Сервер не отвечает на $STATUS_URL — уведомлять некого."
        ss14_warn "Обновление применится при следующем запуске сервера."
        return 1
    fi

    case "$out" in
        200)
            ss14_log "Игрокам отправлено уведомление об обновлении."
            return 0
            ;;
        401)
            ss14_warn "Сервер отклонил токен (401). Проверьте, что watchdog.token"
            ss14_warn "в $SERVER_CONFIG совпадает с WATCHDOG_TOKEN в $CONF_FILE."
            return 2
            ;;
        404)
            ss14_warn "Сервер ответил 404 на /update. Скорее всего, watchdog.token"
            ss14_warn "не задан в конфиге сервера, либо сервер запущен со старым конфигом."
            return 2
            ;;
        *)
            ss14_warn "Неожиданный ответ сервера на /update: HTTP $out"
            return 2
            ;;
    esac
}
