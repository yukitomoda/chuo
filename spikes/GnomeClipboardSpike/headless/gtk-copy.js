// Wayland のクライアント（GTK4）としてクリップボードにコピーする。使い方: gjs -m gtk-copy.js text <文字列> | image <PNG>
import Gtk from 'gi://Gtk?version=4.0';
import Gdk from 'gi://Gdk?version=4.0';
import GLib from 'gi://GLib';
import GObject from 'gi://GObject';
import {programArgs} from 'system';

const [kind, value] = programArgs;
const app = new Gtk.Application({application_id: 'chuo.Spike.Copy'});
app.connect('activate', () => {
    const win = new Gtk.ApplicationWindow({application: app, default_width: 200, default_height: 100});
    win.present();
    // Wayland ではフォーカスがないとクリップボードを設定できないので、アクティブになるまで待つ（最大3秒）
    let waited = 0;
    GLib.timeout_add(GLib.PRIORITY_DEFAULT, 100, () => {
        if (!win.is_active && (waited += 100) < 3000)
            return GLib.SOURCE_CONTINUE;
        const clipboard = win.get_display().get_clipboard();
        if (kind === 'image') {
            // 具象型（GdkMemoryTexture）のままだと PNG への変換が登録されないので、Gdk.Texture として渡す
            const v = new GObject.Value();
            v.init(Gdk.Texture.$gtype);
            v.set_object(Gdk.Texture.new_from_filename(value));
            clipboard.set_content(Gdk.ContentProvider.new_for_value(v));
        } else {
            clipboard.set(value);
        }
        print(`copied ${kind} focus=${win.is_active}`);
        // 所有者として数秒間データを提供し続けてから終了する
        GLib.timeout_add(GLib.PRIORITY_DEFAULT, 3000, () => app.quit());
        return GLib.SOURCE_REMOVE;
    });
});
app.run([]);
