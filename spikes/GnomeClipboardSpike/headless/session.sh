#!/bin/sh
# start.sh から dbus-run-session の中で呼ばれる。gnome-shell を起動し、準備ができたら env.sh を書き出す。
set -u
HERE=$(cd "$(dirname "$0")" && pwd)
WORK=$1
export XDG_RUNTIME_DIR=$(mktemp -d)
chmod 700 "$XDG_RUNTIME_DIR"

gnome-shell --headless --wayland --virtual-monitor 1280x720 &
until gdbus call --session -d org.gnome.Shell -o /org/gnome/Shell \
        -m org.gnome.Shell.Extensions.ListExtensions > /dev/null 2>&1; do
    sleep 0.2
done
gdbus call --session -d org.gnome.Shell -o /org/gnome/Shell \
    -m org.gnome.Shell.Extensions.EnableExtension chuo-spike@chuo.local

cd "$WORK"
gjs -m "$HERE/rd.js" &
until [ -p rd.fifo ]; do sleep 0.1; done
# 起動直後はオーバービューが開いていて、新しいウィンドウにフォーカスが当たらないので、Esc で閉じる。
# 開く途中に送ると効かないことがあるので少し待つ
sleep 1
echo 'key ff1b' > rd.fifo

X11_DISPLAY=$(sed -n 's/.*Using public X11 display \(:[0-9]*\).*/\1/p' "$WORK/gs.log" | tail -1)
cat > env.sh.tmp <<EOS
export XDG_RUNTIME_DIR=$XDG_RUNTIME_DIR WAYLAND_DISPLAY=wayland-0 DBUS_SESSION_BUS_ADDRESS=$DBUS_SESSION_BUS_ADDRESS
export XDG_CONFIG_HOME=$XDG_CONFIG_HOME XDG_DATA_HOME=$XDG_DATA_HOME XDG_CACHE_HOME=$XDG_CACHE_HOME
export DISPLAY=$X11_DISPLAY XAUTHORITY=\$(ls $XDG_RUNTIME_DIR/.mutter-Xwaylandauth.* 2>/dev/null | head -1)
EOS
mv env.sh.tmp env.sh
wait
