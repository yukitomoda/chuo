// chuo の技術検証（#4）用の拡張。クリップボードの変化を D-Bus で本体に中継する。
// ロジックは本体（C#）に置き、ここではイベントの中継と GNOME Shell の API 呼び出しだけを行う。
import Gio from 'gi://Gio';
import GLib from 'gi://GLib';
import Meta from 'gi://Meta';
import St from 'gi://St';
import {Extension} from 'resource:///org/gnome/shell/extensions/extension.js';

const BUS_NAME = 'chuo.Spike';
const OBJECT_PATH = '/chuo/Spike';
const IFACE_XML = `
<node>
  <interface name="chuo.Spike.Clipboard">
    <!-- 中身は送らず、MIME タイプだけを通知する。本体が必要なものを取りに来る -->
    <signal name="Changed">
      <arg name="serial" type="u"/>
      <arg name="mimeTypes" type="as"/>
      <arg name="realtimeUs" type="x"/>
    </signal>
    <method name="GetText">
      <arg name="text" type="s" direction="out"/>
    </method>
    <method name="GetContent">
      <arg name="mimeType" type="s" direction="in"/>
      <arg name="data" type="ay" direction="out"/>
    </method>
    <method name="SetContent">
      <arg name="mimeType" type="s" direction="in"/>
      <arg name="data" type="ay" direction="in"/>
    </method>
  </interface>
</node>`;

class ClipboardService {
    constructor() {
        this._serial = 0;
        this._clipboard = St.Clipboard.get_default();
    }

    GetTextAsync(params, invocation) {
        this._clipboard.get_text(St.ClipboardType.CLIPBOARD, (_clip, text) => {
            invocation.return_value(new GLib.Variant('(s)', [text ?? '']));
        });
    }

    GetContentAsync([mimeType], invocation) {
        this._clipboard.get_content(St.ClipboardType.CLIPBOARD, mimeType, (_clip, bytes) => {
            // 大きい画像を JS の配列に展開しないよう、GBytes から直接 Variant を作る
            const data = GLib.Variant.new_from_bytes(new GLib.VariantType('ay'), bytes ?? new GLib.Bytes(null), true);
            invocation.return_value(GLib.Variant.new_tuple([data]));
        });
    }

    SetContentAsync(_params, invocation) {
        // 展開済みの params ではなく元の Variant から GBytes を取り出す
        const params = invocation.get_parameters();
        const [mimeType] = params.get_child_value(0).get_string();
        const data = params.get_child_value(1).get_data_as_bytes();
        this._clipboard.set_content(St.ClipboardType.CLIPBOARD, mimeType, data);
        invocation.return_value(null);
    }

    emitChanged(mimeTypes) {
        this._serial++;
        this.dbus.emit_signal('Changed', new GLib.Variant('(uasx)',
            [this._serial, mimeTypes, GLib.get_real_time()]));
    }
}

export default class ChuoSpikeExtension extends Extension {
    enable() {
        this._service = new ClipboardService();
        this._service.dbus = Gio.DBusExportedObject.wrapJSObject(IFACE_XML, this._service);
        this._service.dbus.export(Gio.DBus.session, OBJECT_PATH);
        this._nameId = Gio.bus_own_name_on_connection(Gio.DBus.session, BUS_NAME,
            Gio.BusNameOwnerFlags.NONE, null, null);

        const selection = global.display.get_selection();
        this._ownerChangedId = selection.connect('owner-changed', (sel, type, source) => {
            if (type !== Meta.SelectionType.SELECTION_CLIPBOARD)
                return;
            // source が null のときはクリップボードが空になった
            const mimeTypes = source ? source.get_mimetypes() : [];
            this._service.emitChanged(mimeTypes);
        });
    }

    disable() {
        global.display.get_selection().disconnect(this._ownerChangedId);
        Gio.bus_unown_name(this._nameId);
        this._service.dbus.unexport();
        this._service = null;
    }
}
