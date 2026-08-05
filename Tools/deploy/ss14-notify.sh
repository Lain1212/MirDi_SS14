#!/usr/bin/env bash
# SPDX-License-Identifier: AGPL-3.0-or-later
#
# Сообщить игрокам о перезапуске и мягко перезапустить сервер в конце раунда,
# ничего не обновляя. Полезно, чтобы применить правки в server_config.toml
# или просто перезапустить сервер, не обрывая текущий раунд.
#
# Запуск:  Tools/deploy/ss14-notify.sh

source "$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/lib.sh"
ss14_load_conf

if ss14_notify_update; then
    ss14_log "Сервер перезапустится в конце текущего раунда (или сразу, если он пуст)."
else
    exit 1
fi
