#!/bin/sh
# ヘッドレスの gnome-shell を、普段の設定と分けた環境で起動する（GNOME のないコンテナでの検証用）。
# 拡張を有効にし、仮想の入力デバイス（rd.js）を用意して、接続用の env.sh を書き出す。
#   使い方: ./start.sh [作業ディレクトリ（既定: /tmp/chuo-headless-gnome）]
#   終了:   ./stop.sh  [作業ディレクトリ]
set -eu
HERE=$(cd "$(dirname "$0")" && pwd)
WORK=${1:-/tmp/chuo-headless-gnome}
"$HERE/stop.sh" "$WORK" 2>/dev/null || true
mkdir -p "$WORK/home/data/gnome-shell/extensions" "$WORK/home/config" "$WORK/home/cache"
ln -sfn "$HERE/../extension/chuo-spike@chuo.local" "$WORK/home/data/gnome-shell/extensions/"
rm -f "$WORK/env.sh" "$WORK/rd.fifo"

export XDG_CONFIG_HOME="$WORK/home/config" XDG_DATA_HOME="$WORK/home/data" XDG_CACHE_HOME="$WORK/home/cache"
unset DISPLAY WAYLAND_DISPLAY
# 新しいプロセスグループで起動し、stop.sh でまとめて終了できるようにする
setsid dbus-run-session -- "$HERE/session.sh" "$WORK" > "$WORK/gs.log" 2>&1 &
echo $! > "$WORK/pgid"

for _ in $(seq 100); do
    [ -f "$WORK/env.sh" ] && { echo "起動しました。接続するには: . $WORK/env.sh"; exit 0; }
    sleep 0.2
done
echo "起動に失敗しました。$WORK/gs.log を見てください" >&2
exit 1
