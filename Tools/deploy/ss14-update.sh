#!/usr/bin/env bash
# SPDX-License-Identifier: AGPL-3.0-or-later
#
# Обновление сервера одной командой.
#
#   1. git pull из настроенной ветки;
#   2. игроки сразу получают в чат сообщение, что сервер перезапустится
#      в конце раунда (сервер объявляет это сам, получив POST /update);
#   3. сборка идёт, пока раунд продолжается — сервер работает как обычно;
#   4. когда раунд закончится (или когда все выйдут), сервер выключится,
#      а супервизор поднимет его уже на новой сборке.
#
# Запуск:  Tools/deploy/ss14-update.sh

source "$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/lib.sh"
ss14_load_conf

usage() {
    cat <<'EOF'
Использование: ss14-update.sh [опции]

  --force        собрать и перезапустить, даже если новых коммитов нет
  --no-notify    не сообщать игрокам (сборка применится при следующем запуске)
  -h, --help     эта справка
EOF
}

FORCE=0
NOTIFY=1

while [[ $# -gt 0 ]]; do
    case "$1" in
        --force)     FORCE=1; shift ;;
        --no-notify) NOTIFY=0; shift ;;
        -h|--help)   usage; exit 0 ;;
        *) usage >&2; ss14_die "Неизвестный аргумент: $1" ;;
    esac
done

mkdir -p "$STATE_DIR" "$LOG_DIR"

# --- Не запускаем две сборки одновременно ---

if [[ -f $BUILDING_MARK ]]; then
    other_pid="$(cat "$BUILDING_MARK" 2>/dev/null || true)"
    if [[ -n $other_pid ]] && kill -0 "$other_pid" 2>/dev/null; then
        ss14_die "Обновление уже выполняется (PID $other_pid)."
    fi
    rm -f "$BUILDING_MARK"
fi

cd "$REPO_DIR"

# --- Проверки репозитория ---

current_branch="$(git rev-parse --abbrev-ref HEAD)"
if [[ $current_branch != "$GIT_BRANCH" ]]; then
    ss14_die "На сервере активна ветка '$current_branch', а обновления берутся из '$GIT_BRANCH'.
Переключитесь (git checkout $GIT_BRANCH) или поправьте GIT_BRANCH в $CONF_FILE."
fi

if [[ -n "$(git status --porcelain --untracked-files=no --ignore-submodules=dirty)" ]]; then
    ss14_die "В $REPO_DIR есть незакоммиченные изменения — git pull их затрёт или сорвётся.
Разберитесь с ними (git status) и повторите."
fi

ss14_log "Проверяю обновления в $GIT_REMOTE/$GIT_BRANCH ..."
git fetch --quiet "$GIT_REMOTE" "$GIT_BRANCH"

local_rev="$(git rev-parse HEAD)"
remote_rev="$(git rev-parse "$GIT_REMOTE/$GIT_BRANCH")"

if [[ $local_rev == "$remote_rev" ]]; then
    if (( FORCE == 0 )); then
        ss14_log "Новых коммитов нет, сервер уже на актуальной версии."
        exit 0
    fi
    ss14_log "Новых коммитов нет, но указан --force — пересобираю."
else
    ss14_log "Новые коммиты:"
    git log --oneline --no-decorate "HEAD..$GIT_REMOTE/$GIT_BRANCH" | sed 's/^/    /'

    # --- 1. Забираем обновление ---

    if ! git merge --ff-only --quiet "$GIT_REMOTE/$GIT_BRANCH"; then
        ss14_die "git merge --ff-only не прошёл: локальная ветка разошлась с $GIT_REMOTE/$GIT_BRANCH.
Приведите репозиторий в порядок вручную и повторите."
    fi
    ss14_log "Обновление получено: $(git rev-parse --short HEAD)"

    git submodule update --init --recursive --quiet
fi

# --- 2. Сообщаем игрокам (до сборки: раунд ещё идёт, времени больше) ---

notified=0
if (( NOTIFY == 1 && NOTIFY_BEFORE_BUILD == 1 )); then
    if ss14_notify_update; then
        notified=1
    fi
fi

# --- 3. Сборка. Сервер в это время работает на своей копии в $LIVE_DIR ---

build_log="$LOG_DIR/build-$(date '+%Y%m%d-%H%M%S').log"
echo $$ > "$BUILDING_MARK"
trap 'rm -f "$BUILDING_MARK"' EXIT

ss14_log "Собираю ($BUILD_CONFIG). Лог: $build_log"

set +e
dotnet build -c "$BUILD_CONFIG" --nologo 2>&1 | tee "$build_log"
build_code=${PIPESTATUS[0]}
set -e

# Держим последние 10 логов сборки.
(ls -1t "$LOG_DIR"/build-*.log 2>/dev/null | tail -n +11 | xargs -r rm -f) || true

if (( build_code != 0 )); then
    rm -f "$BUILDING_MARK"
    if (( notified == 1 )); then
        ss14_warn "Игроки уже получили уведомление, поэтому сервер всё равно перезапустится"
        ss14_warn "в конце раунда — но останется на прежней, рабочей версии."
    fi
    ss14_die "Сборка не удалась (код $build_code). Разбор в $build_log — сервер не тронут."
fi

git rev-parse HEAD > "$PENDING_MARK"
rm -f "$BUILDING_MARK"
trap - EXIT

ss14_log "Сборка готова и ждёт перезапуска."

# --- 4. Если уведомление ещё не отправляли — самое время ---

if (( NOTIFY == 1 && notified == 0 )); then
    ss14_notify_update && notified=1 || true
fi

if (( notified == 1 )); then
    ss14_log "Готово. Сервер перезапустится на новой версии в конце текущего раунда."
else
    ss14_log "Готово. Новая версия применится при ближайшем запуске сервера."
fi
