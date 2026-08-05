#!/usr/bin/env bash
# SPDX-License-Identifier: AGPL-3.0-or-later
#
# Супервизор сервера. Запускается вместо ручного `dotnet run` и держит сервер
# поднятым: когда сервер выключается ради обновления, супервизор собирает
# проект и поднимает сервер обратно уже на новой версии.
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

# Сборка идёт только здесь — между выключением и запуском сервера, когда файлы
# в bin/ никем не заняты.
build_if_pending() {
    [[ -f $PENDING_MARK ]] || return 0

    local commit build_log code
    commit="$(cat "$PENDING_MARK" 2>/dev/null || true)"
    build_log="$LOG_DIR/build-$(date '+%Y%m%d-%H%M%S').log"

    slog "Собираю обновление${commit:+ (${commit:0:8})}. Лог: $build_log"
    slog "Сервер поднимется, как только сборка закончится."

    echo $$ > "$BUILDING_MARK"

    cd "$REPO_DIR"
    set +e
    dotnet build -c "$BUILD_CONFIG" --nologo > "$build_log" 2>&1
    code=$?
    set -e

    rm -f "$BUILDING_MARK"
    (ls -1t "$LOG_DIR"/build-*.log 2>/dev/null | tail -n +11 | xargs -r rm -f) || true

    if (( code != 0 )); then
        slog "СБОРКА НЕ УДАЛАСЬ (код $code). Подробности: $build_log"
        slog "Запускаю сервер на том, что собрано сейчас — проверьте лог сборки."
        slog "Маркер сборки оставлен: следующий перезапуск попробует собрать снова."
        tail -n 20 "$build_log" | sed 's/^/    /'
        return 0
    fi

    rm -f "$PENDING_MARK"
    if [[ -n $commit ]]; then
        echo "$commit" > "$BUILT_MARK"
    fi
    slog "Сборка завершена успешно."
}

[[ -f "$SERVER_BIN_DIR/$SERVER_DLL" ]] \
    || ss14_die "Не найден $SERVER_BIN_DIR/$SERVER_DLL.
Соберите проект: cd $REPO_DIR && dotnet build -c $BUILD_CONFIG"

slog "Супервизор запущен. Сервер: $SERVER_BIN_DIR"

quick_fails=0

while true; do
    rm -f "$STOP_MARK"

    build_if_pending
    if (( STOPPING == 1 )); then
        break
    fi

    started_at=$SECONDS
    slog "Запускаю сервер..."

    cd "$SERVER_BIN_DIR"
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
