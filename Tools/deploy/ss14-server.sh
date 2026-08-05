#!/usr/bin/env bash
# SPDX-License-Identifier: AGPL-3.0-or-later
#
# Супервизор сервера. Запускается вместо ручного `dotnet run` и держит сервер
# поднятым: после каждого выключения (в том числе выключения ради обновления)
# применяет свежую сборку, если она готова, и запускает сервер снова.
#
# Запуск:  screen -S ss14 Tools/deploy/ss14-server.sh
# Остановка: Ctrl+C, либо Tools/deploy/ss14-stop.sh

source "$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/lib.sh"
ss14_load_conf

RUN_ONCE=0
if [[ ${1:-} == "--once" ]]; then
    RUN_ONCE=1
fi

mkdir -p "$LOG_DIR" "$STATE_DIR"

slog() {
    local msg
    msg="[$(date '+%Y-%m-%d %H:%M:%S')] $*"
    echo "$msg"
    echo "$msg" >> "$LOG_DIR/supervisor.log"
}

STOPPING=0
SERVER_PID=""

on_stop() {
    STOPPING=1
    if [[ -n $SERVER_PID ]] && kill -0 "$SERVER_PID" 2>/dev/null; then
        slog "Получен сигнал остановки, выключаю сервер..."
        kill -TERM "$SERVER_PID" 2>/dev/null || true
    fi
}
trap on_stop INT TERM

# Не запускать сервер на полусобранных файлах: ss14-update.sh держит маркер,
# пока идёт dotnet build.
wait_for_build() {
    local waited=0 pid announced=0

    while [[ -f $BUILDING_MARK ]]; do
        pid="$(cat "$BUILDING_MARK" 2>/dev/null || true)"

        if [[ -z $pid ]] || ! kill -0 "$pid" 2>/dev/null; then
            slog "Маркер сборки остался от прерванного процесса, игнорирую его."
            rm -f "$BUILDING_MARK"
            break
        fi

        if (( announced == 0 )); then
            slog "Идёт сборка обновления (PID $pid). Жду её окончания перед запуском."
            announced=1
        fi

        sleep 5
        waited=$(( waited + 5 ))

        if (( waited >= BUILD_WAIT_TIMEOUT )); then
            slog "Сборка идёт дольше $BUILD_WAIT_TIMEOUT с, запускаю сервер на текущей версии."
            break
        fi

        if (( STOPPING == 1 )); then
            return
        fi
    done
}

# Переносит собранное из репозитория в рабочую копию. Выполняется только между
# запусками сервера, поэтому файлы работающего процесса никогда не перезаписываются.
deploy_if_pending() {
    [[ -f $PENDING_MARK ]] || return 0

    local commit
    commit="$(cat "$PENDING_MARK" 2>/dev/null || true)"
    slog "Применяю новую сборку${commit:+ (${commit:0:8})}..."

    rsync -a --delete --exclude '/data/' --exclude '/logs/' \
        "$REPO_DIR/bin/Content.Server/" "$LIVE_DIR/bin/Content.Server/"
    rsync -a --delete "$REPO_DIR/bin/Content.Client/" "$LIVE_DIR/bin/Content.Client/"
    rsync -a --delete "$REPO_DIR/Resources/" "$LIVE_DIR/Resources/"

    mv "$PENDING_MARK" "$DEPLOYED_MARK"
    slog "Сборка применена."
}

[[ -f "$LIVE_DIR/bin/Content.Server/$SERVER_DLL" ]] \
    || ss14_die "Не найден $LIVE_DIR/bin/Content.Server/$SERVER_DLL. Запустите Tools/deploy/ss14-setup.sh"

slog "Супервизор запущен. Рабочая копия: $LIVE_DIR"

quick_fails=0

while true; do
    rm -f "$STOP_MARK"

    wait_for_build
    if (( STOPPING == 1 )); then
        break
    fi

    deploy_if_pending

    started_at=$SECONDS
    slog "Запускаю сервер..."

    cd "$LIVE_DIR/bin/Content.Server"
    dotnet "$SERVER_DLL" --config-file "$SERVER_CONFIG" --data-dir "$DATA_DIR" &
    SERVER_PID=$!

    set +e
    wait "$SERVER_PID"
    code=$?
    while kill -0 "$SERVER_PID" 2>/dev/null; do
        wait "$SERVER_PID"
        code=$?
    done
    set -e
    SERVER_PID=""

    uptime=$(( SECONDS - started_at ))
    slog "Сервер завершился (код $code, проработал ${uptime}с)."

    if (( STOPPING == 1 )); then
        slog "Остановка по запросу, перезапуска не будет."
        break
    fi

    if [[ -f $STOP_MARK ]]; then
        slog "Найден файл $STOP_MARK — перезапуска не будет."
        rm -f "$STOP_MARK"
        break
    fi

    if (( RUN_ONCE == 1 )); then
        slog "Режим --once, выхожу."
        break
    fi

    # Защита от бесконечного перезапуска упавшего сервера.
    if (( uptime < 15 )); then
        quick_fails=$(( quick_fails + 1 ))
    else
        quick_fails=0
    fi

    if (( quick_fails >= 3 )); then
        slog "Сервер падает сразу после запуска (3 раза подряд). Пауза 60 секунд."
        slog "Загляните в логи: $LOG_DIR"
        sleep 60
        quick_fails=0
    else
        sleep "$RESTART_DELAY"
    fi
done

slog "Супервизор остановлен."
