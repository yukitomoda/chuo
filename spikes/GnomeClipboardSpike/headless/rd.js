// Mutter の RemoteDesktop セッションで仮想キーボードを作り、FIFO（rd.fifo）から命令を受けて入力する。
// ヘッドレスの gnome-shell にはキーボードがなく、Wayland のクライアントがフォーカスを得られないため。
//   key <keysym（16進）>   キーを押して離す
import Gio from 'gi://Gio';
import GLib from 'gi://GLib';
const bus = Gio.DBus.session;
const call = (path, iface, method, args) => bus.call_sync('org.gnome.Mutter.RemoteDesktop', path,
    iface, method, args, null, 0, -1, null);
const [path] = call('/org/gnome/Mutter/RemoteDesktop', 'org.gnome.Mutter.RemoteDesktop', 'CreateSession', null).deepUnpack();
const session = (method, args) => call(path, 'org.gnome.Mutter.RemoteDesktop.Session', method, args);
session('Start', null);
print('session', path);

const fifo = 'rd.fifo';
GLib.spawn_command_line_sync(`mkfifo ${fifo}`);
for (;;) {
    const [, contents] = GLib.file_get_contents(fifo);
    for (const line of new TextDecoder().decode(contents).split('\n')) {
        const [cmd, a] = line.trim().split(/\s+/);
        if (cmd === 'key') {
            for (const down of [true, false])
                session('NotifyKeyboardKeysym', new GLib.Variant('(ub)', [parseInt(a, 16), down]));
        }
        if (cmd)
            print('done', line);
    }
}
