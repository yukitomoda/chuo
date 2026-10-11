#!/bin/sh
# start.sh で起動したプロセスグループをまとめて終了する
WORK=${1:-/tmp/chuo-headless-gnome}
[ -f "$WORK/pgid" ] || exit 0
kill -- "-$(cat "$WORK/pgid")" 2>/dev/null
rm -f "$WORK/pgid" "$WORK/env.sh"
