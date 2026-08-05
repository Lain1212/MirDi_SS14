#!/usr/bin/env bash
# SPDX-License-Identifier: AGPL-3.0-or-later
#
# Немедленно выключить сервер, не дожидаясь конца раунда.
# По умолчанию супервизор поднимет его обратно (и применит готовую сборку).
# С --no-restart супервизор тоже завершится.
#
# Если раунд обрывать не нужно — используйте ss14-notify.sh.

source "$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/lib.sh"
ss14_load_conf

RESTART=1
REASON="Сервер выключается для обслуживания."

while [[ $# -gt 0 ]]; do
    case "$1" in
        --no-restart) RESTART=0; shift ;;
        --reason)     REASON="$2"; shift 2 ;;
        -h|--help)
            echo "Использование: ss14-stop.sh [--no-restart] [--reason ТЕКСТ]"
            exit 0
            ;;
        *) ss14_die "Неизвестный аргумент: $1" ;;
    esac
done

mkdir -p "$STATE_DIR"

if (( RESTART == 0 )); then
    touch "$STOP_MARK"
    ss14_log "Супервизор завершится вместе с сервером."
fi

set +e
code="$(curl -sS -m 10 -o /dev/null -w '%{http_code}' \
    -X POST -H "WatchdogToken: $WATCHDOG_TOKEN" \
    -H "Content-Type: application/json" \
    --data "$(printf '{"Reason": "%s"}' "$REASON")" \
    "$STATUS_URL/shutdown" 2>&1)"
status=$?
set -e

if [[ $status -ne 0 ]]; then
    rm -f "$STOP_MARK"
    ss14_die "Сервер не отвечает на $STATUS_URL — похоже, он и так не запущен."
fi

if [[ $code != "200" ]]; then
    rm -f "$STOP_MARK"
    ss14_die "Сервер ответил HTTP $code. Проверьте watchdog.token в $SERVER_CONFIG."
fi

if (( RESTART == 1 )); then
    ss14_log "Сервер выключается, супервизор перезапустит его через $RESTART_DELAY с."
else
    ss14_log "Сервер выключается, перезапуска не будет."
fi
