#!/usr/bin/env bash
# SPDX-License-Identifier: AGPL-3.0-or-later
#
# Обновление сервера одной командой.
#
#   1. git pull из настроенной ветки;
#   2. игроки сразу получают в чат сообщение, что сервер перезапустится
#      в конце раунда (сервер объявляет это сам, получив POST /update);
#   3. когда раунд закончится (или когда все выйдут), сервер выключится,
#      супервизор соберёт проект и поднимет сервер на новой версии.
#
# Сборка идёт при выключенном сервере, так что на её время он недоступен.
#
# Запуск:  Tools/deploy/ss14-update.sh

source "$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/lib.sh"
ss14_load_conf

usage() {
    cat <<'EOF'
Использование: ss14-update.sh [опции]

  --force        перезапустить с пересборкой, даже если новых коммитов нет
  --no-notify    не сообщать игрокам (пересборка произойдёт при следующем запуске)
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

# --- Не тянем обновления посреди идущей сборки ---

if [[ -f $BUILDING_MARK ]]; then
    other_pid="$(cat "$BUILDING_MARK" 2>/dev/null || true)"
    if [[ -n $other_pid ]] && kill -0 "$other_pid" 2>/dev/null; then
        ss14_die "Сейчас идёт сборка (PID $other_pid). Дождитесь её окончания и повторите."
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
    ss14_log "Новых коммитов нет, но указан --force — сервер пересоберётся при перезапуске."
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

# --- 2. Просим супервизор собрать проект при следующем запуске ---

git rev-parse HEAD > "$PENDING_MARK"

# --- 3. Сообщаем игрокам ---

notified=0
if (( NOTIFY == 1 )); then
    if ss14_notify_update; then
        notified=1
    fi
fi

if (( notified == 1 )); then
    ss14_log "Готово. В конце раунда сервер выключится, соберётся и поднимется на новой версии."
    ss14_log "На время сборки сервер будет недоступен."
else
    ss14_log "Готово. Сборка и обновление произойдут при ближайшем запуске сервера."
fi
