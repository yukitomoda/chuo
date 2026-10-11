# 技術検証（スパイク）の結果

要件定義書の「14. 最初の作業（技術検証）」の結果を記録する。検証用のコードは `spikes/` にある。

| # | 検証項目 | 状況 | コード |
|---|---|---|---|
| 1 | ホットキーから表示までの時間 | **計測済み（Windows）**。初回 17〜22ms、2回目以降 7〜12ms で目標（50ms）を満たす | `spikes/WinLauncherSpike` |
| 2 | 前面化と入力欄へのフォーカス | **確認済み（Windows）**。全画面のアプリからも成功 | `spikes/WinLauncherSpike` |
| 3 | 表示時に IME をオフにする | **確認済み（Windows）**。IME が後から状態を復元するため、その対策が必要だった | `spikes/WinLauncherSpike` |
| 4 | GNOME Shell 拡張から D-Bus でクリップボードの変化を受け取る | **確認済み（ヘッドレスの GNOME Shell 50）**。Fedora の実機での確認が残る | `spikes/GnomeClipboardSpike` |
| 5 | 常駐時のメモリ使用量 | **計測済み（Windows）**。既定（ANGLE）ではプライベートワーキングセットが約 50〜65MB。ソフトウェア描画で約 24MB、非表示時のトリムで 2〜5MB に下がり、表示の速さは変わらない。Linux は未計測 | `spikes/WinLauncherSpike` |
| 6 | 形態素解析の辞書のメモリと読みの付与にかかる時間 | **計測済み（Linux）** | `spikes/ReadingSpike` |

## 1〜3, 5. Windows のランチャー（WinLauncherSpike）

### 実行方法

Windows で次のように発行して実行する（本番と同じく ReadyToRun でビルドする）。Linux からクロスビルドした発行物でもよい。

```powershell
cd spikes\WinLauncherSpike
dotnet publish -c Release -r win-x64 --self-contained -o publish
.\publish\WinLauncherSpike.exe                 # 既定: Alt+Space、表示時に IME をオフ
.\publish\WinLauncherSpike.exe --hotkey Ctrl+Shift+K --ime-restore
.\publish\WinLauncherSpike.exe --ime-method imm  # IME を IMM32 の入力コンテキストで操作する（比較用。既定は imewnd）

# メモリの調査用
.\publish\WinLauncherSpike.exe --render software      # 描画方式: angle（既定）/ wgl / software / vulkan
.\publish\WinLauncherSpike.exe --composition redirection  # 合成方式: winui / dcomp / lowlatency / redirection
.\publish\WinLauncherSpike.exe --render-on-ui-thread  # 描画専用のスレッドを使わない
.\publish\WinLauncherSpike.exe --trim-on-hide         # 非表示にしたら GC を実行し、ワーキングセットを OS に返す
```

- ホットキーで表示と非表示を切り替える。Esc やフォーカスを失ったときも隠れる。Ctrl+Shift+Q で終了する。
- 計測結果はウィンドウ下部と `%LOCALAPPDATA%\chuo-spike\spike.log` に出る。
- `show #n: total=...` の内訳：`queue`（WM_HOTKEY がキューに積まれてから取り出されるまで）、`dispatch`（UI スレッドへの転送）、`show`（Show、Activate、フォーカス、IME の操作）、`render`（2フレーム目の描画まで）。
- メモリは `workingSet`（ワーキングセット）、`private`（コミット済みのプライベートメモリ）、`privateWS`（プライベートワーキングセット。タスクマネージャーの「メモリ」列）、`managedHeap` を出す。非表示にしてから1秒後の値も `hidden(1s)` として出す。
- 結果の一覧には、アイコン（64px を 32px に縮小）と2行のテキストを持つダミーの項目を最大 30 件並べる。入力欄に文字を打つたびに別の項目に差し替え、`filter #n: total=...`（一覧の差し替えから2フレーム目の描画まで）を出す。
- IME の調査用に、`imeNow(imm=... imewnd=...)`（2つの方法で読んだ状態）、`wndproc`（表示から1秒間の WM_IME_SETCONTEXT と IMN_SETOPENSTATUS）、`ime-guard`（オンに戻されたのを検知してオフにした記録）を出す。

### 確認したいこと

- [x] `total` が 50ms 以内に収まるか（初回と2回目以降を分けて見る）
- [x] 他のアプリ（ブラウザ、エディタ、全画面のアプリ）が前面にある状態から、`foreground=True focused=True` になるか
- [x] IME がオンの状態から表示したときに `ime(... after=off)` になり、実際にローマ字のまま入力できるか
- [ ] `--ime-restore` で閉じたときに元の IME の状態に戻るか。戻した方が使いやすいか
- [x] `ready` と `idle(10s)` のメモリ使用量
- [ ] タスクマネージャーの「メモリ」列の値（`private` と一致するか）

### 結果

環境：Windows 11（10.0.26200）、.NET 10.0.12、自己完結・ReadyToRun（Linux からクロスビルド）、Microsoft IME。2026-10-10 に4回起動して計測した。

**表示までの時間（#1）と前面化（#2）**

| | total | show | render |
|---|---|---|---|
| 初回（#1） | 16.9〜22.3ms | 11.6〜14.7ms | 4.8〜6.5ms |
| 2回目以降 | 6.9〜12.4ms | 4.3〜9.7ms | 1.9〜3.7ms |

- `queue` は常に 0ms、`dispatch` は 0.1ms 前後（初回のみ 0.5〜1.0ms）。時間の大半は Show／Activate／フォーカスの操作である。
- **目標の 50ms を十分に満たす。** 起動時の事前描画（prewarm、127〜183ms）のおかげで、初回も 20ms 前後に収まった。起動にかかった時間（`startup`）は 346〜666ms だった。
- 全回で `foreground=True (activate=True) focused=True`。`Activate()` だけで前面に出ており、`SetForegroundWindow` は不要だった。全画面のアプリが前面にある状態からも表示できた。

**IME（#3）**

最終的に、ランチャーの中で IME をオンにして閉じても、次に表示したときはオフになるようにできた。そこに至るまでにわかったことは次のとおり。

1. **IMM32 の入力コンテキストの API では、2回目以降の表示で操作できなかった。** 表示した直後は `ImmGetContext` が 0 を返す（Avalonia がフォーカスを失ったときに関連付けを外し、表示後に付け直すため）。数フレーム待つと取得できるが、読める状態は常に `off` だった。
2. **既定の IME ウィンドウに `WM_IME_CONTROL` を送る方法（`ImmGetDefaultIMEWnd`）にしても、それだけでは直らなかった。** ウィンドウプロシージャで記録すると、2回目以降の表示は毎回次の順で進んでいた。

   | 表示からの時間 | 起きていること |
   |---|---|
   | +5ms | `WM_IME_SETCONTEXT(active)`。入力コンテキストはまだない |
   | +8〜11ms | 関連付けが一度外れ、付け直される（Avalonia がコンテキストを差し替える） |
   | +10ms 前後 | ここで IME をオフにする |
   | **+35〜40ms** | **`IMN_SETOPENSTATUS`。IME がウィンドウごとに覚えていた状態（オン）を非同期に復元する** |

3. **対策：** 表示から 500ms の間、ユーザーがキーを押していないのに IME がオンになったら、オフに戻す（今は 16ms ごとに状態を確認している）。オフに戻すのは表示から約 48ms 後で、人が打ち始めるまでには間に合う。
   - ホットキーの修飾キーを押したままのキーリピートは、ユーザーの入力として数えない。

**常駐時のメモリ（#5）**

| 時点 | ワーキングセット | プライベート | マネージドヒープ |
|---|---|---|---|
| 起動直後（`ready`） | 131〜135MB | 102MB | 4.9MB |
| 2回表示した後（`idle(10s)`） | 138〜148MB | 107〜114MB | 6.1〜6.3MB |
| 10回表示した後 | 152MB | 116MB | 8.7MB |

- マネージドヒープは 5〜9MB にとどまる。プライベートの大半はネイティブのメモリ（Skia、描画の GPU バックエンド、ReadyToRun のコードなど）と思われる。内訳はまだ調べていない。
- 何度か表示すると、プライベートが 10MB 余り増える。10回の範囲では増え続ける様子はなかった。

**描画方式と非表示時のトリム（2026-10-11）**

上の表の「プライベート」はコミット済みの量で、物理メモリに載っている量ではなかった。そこでプライベートワーキングセット（`privateWS`、タスクマネージャーの「メモリ」列）を追加し、条件を変えて1回ずつ起動して計測した（各回5〜6回表示）。

| 条件 | startup | `ready` の private / privateWS | `idle(10s)` の private / privateWS | 2回目以降の `total` |
|---|---|---|---|---|
| 既定（ANGLE、WinUI の合成） | 359ms | 102.4 / 57.0MB | 106.0 / 52.2MB | 9.2〜11.1ms |
| `--render software` | 281ms | **19.4 / 14.6MB** | **28.7 / 23.7MB** | 10.0〜12.7ms |
| `--render wgl` | 601ms | 388.0 / 183.8MB | 418.1 / 205.6MB | 9.9〜11.5ms |
| `--composition redirection` | 346ms | 100.2 / 56.5MB | 106.4 / 59.5MB | 8.2〜9.6ms |
| `--trim-on-hide` | 355ms | 102.0 / 56.8MB | 下記 | 9.7〜11.3ms |

`--trim-on-hide`（非表示にしたら GC を実行し、`SetProcessWorkingSetSize(-1, -1)` でワーキングセットを OS に返す）の推移：

| 時点 | workingSet | private | privateWS |
|---|---|---|---|
| トリムの直後（`trim #1〜5`） | 2.1〜12.2MB | 113〜121MB | **1.6〜3.9MB** |
| 非表示から1秒後（`hidden(1s)`） | 16.9MB | 113.2MB | 5.2MB |
| トリムの後に表示した時点（`show #2〜6`） | 34〜47MB | 105〜120MB | 9.9〜15.2MB |

- **描画の GPU バックエンド（ANGLE の D3D11）が、物理メモリで約 30〜40MB、コミットで約 80MB を占めている。** ソフトウェア描画にすると、これがほぼなくなる。表示の速さは 1ms 程度しか変わらなかった（このスパイクのウィンドウは小さく、描く内容も少ないことに注意）。
- WGL（OpenGL）は大幅に悪い（privateWS が約 200MB）。合成方式を変えても、メモリはほとんど変わらない（redirection は表示がわずかに速いが誤差の範囲）。
- **トリムは効果が大きく、次の表示も遅くならなかった。** トリム自体は UI スレッドで 8〜15ms かかるが、非表示にした後なので体感には影響しない。追い出したページはすぐには捨てられずスタンバイリストに残るので、直後の表示ではソフトページフォールトで安く戻せたと考えられる。何分も経ってメモリが逼迫し、ページファイルから読み直すことになったときの表示時間は、まだ測っていない。
- トリムしてもコミット（`private`）は減らない。タスクマネージャーの「メモリ」列には効くが、システム全体のコミットの量は変わらない。
- 表示すると、トリムした後でも privateWS は 10〜15MB まで戻る。表示中に使う分だけが戻り、起動直後の 57MB には戻らない。

### 考察と本実装に向けて

- **表示の速さと前面化は、Windows では問題ない。** 「隠しておいたウィンドウを表示する」方式（起動時に一度だけ生成し、事前に描画しておく）を本実装でも使う。
- **IME の対策は、本実装でも必要になる。** 次の案を試す価値がある。
  - 閉じる前に IME をオフにしておく。IME はウィンドウごとに覚えた状態を復元するので、復元されてもオフのままになるはず（未検証）。うまくいけば、表示後の監視は予備にできる。
  - 表示後の監視は、16ms ごとの確認ではなく、`IMN_SETOPENSTATUS` を受け取った時点でオフに戻す方式にする。オンになっている時間をほぼなくせる。
  - いずれも Microsoft IME で確認したもの。Google 日本語入力などのほかの IME でも同じかは確認していない。
- **メモリは、ソフトウェア描画と非表示時のトリムで大きく減らせる。** 既定のままでは、タスクマネージャーの「メモリ」列で 50〜65MB になる。ソフトウェア描画なら約 24MB、トリムすると非表示の間は 2〜5MB になり、どちらでも表示の速さは変わらなかった。本実装では両方を採用する方向で考える。次のことは未確認である。
  - ソフトウェア描画とトリムを組み合わせたときの値
  - 結果の一覧（数十件の項目とアイコン）を描いたときに、ソフトウェア描画で描画時間が増えないか
  - 長い時間（数十分〜数時間）非表示にした後の表示時間。トリムしたページがページファイルに出されると遅くなるかもしれない
  - タスクマネージャーの値とログの `privateWS` が一致するか

## 4. GNOME Shell 拡張からのクリップボードの通知（GnomeClipboardSpike）

### 構成

- 拡張（`extension/chuo-spike@chuo.local`）：`global.display.get_selection()` の `owner-changed` で CLIPBOARD の変化を検知し、D-Bus のシグナルで通知する。要件定義書 3.6 のとおり、拡張は中継だけを行う。
  - バス名 `chuo.Spike`、オブジェクト `/chuo/Spike`、インターフェース `chuo.Spike.Clipboard`
  - シグナル `Changed(u serial, as mimeTypes, x realtimeUs)`：**中身は送らず MIME タイプだけを送る**。本体が必要な形式だけを取りに来る（大きい画像を毎回送らないため）
  - メソッド `GetText() → s`、`GetContent(s mimeType) → ay`、`SetContent(s mimeType, ay data)`（`St.Clipboard` を呼ぶだけ）
- 本体側（`Program.cs`）：`Tmds.DBus.Protocol` 0.95.1 でシグナルを購読し、画像（`image/png`）があれば画像を、なければテキストを取得する。

### 実行方法

GNOME のないコンテナ（Alpine Linux＋XFCE の webtop）で、ヘッドレスの gnome-shell を使って検証した。

```sh
sudo apk add gnome-shell gdm wl-clipboard xclip   # gdm は Gdm の typelib のため
chmod +t /tmp/.X11-unix                          # スティッキービットがないと XWayland が起動しない
cd spikes/GnomeClipboardSpike
headless/start.sh                                # 設定を分けた環境で起動し、拡張を有効にする
. /tmp/chuo-headless-gnome/env.sh
dotnet run -c Release &                          # 待ち受け
gjs -m headless/gtk-copy.js text 'テキスト'      # Wayland のアプリ（GTK4）からコピー
gjs -m headless/gtk-copy.js image 画像.png
echo 'テキスト' | xclip -selection clipboard     # X11 のアプリ（XWayland）からコピー
dotnet run -c Release -- set-text 'テキスト'     # 拡張の SetContent でコピー
headless/stop.sh
```

- ヘッドレスの gnome-shell にはキーボードがなく、Wayland のアプリがフォーカスを得られない（＝クリップボードを設定できない）。`headless/rd.js` が Mutter の RemoteDesktop で仮想キーボードを作り、起動直後に開いているオーバービューを Esc で閉じる。
- Fedora の実機では、拡張を `~/.local/share/gnome-shell/extensions/` に置き、ログインし直してから `gnome-extensions enable chuo-spike@chuo.local` で有効にする。

### 結果

環境：Alpine Linux 3.24、GNOME Shell 50.3（mutter 50.2、ヘッドレス、GPU なし）、.NET 10.0.12、Release ビルド

| コピー元 | 形式 | 受信 | 通知の遅延 | 中身の取得 |
|---|---|---|---|---|
| GTK4（Wayland） | テキスト | ○ | 0.3〜4ms | 1〜5ms |
| GTK4（Wayland） | PNG 64×32 | ○ | 0.3ms | 2ms |
| GTK4（Wayland） | PNG 1920×1080（3.2MB） | ○ | 0.6〜4ms | 165ms（初回）、32〜47ms（mutter が保持した後） |
| GTK4（Wayland） | PNG 3840×2160（25MB、圧縮が効かない画像） | ○ | 0.7ms | 911ms（初回）、235ms（mutter が保持した後） |
| xclip（XWayland） | テキスト、PNG | ○ | 0.3〜4ms | 1〜23ms |
| 拡張の `SetContent` | テキスト、PNG | ○（バイト列も一致） | 0.3〜3ms | 1〜6ms |

- 通知の遅延は、拡張がシグナルを送った時刻から本体が受け取った時刻まで（UNIX 時刻で比較）。最初の1回は 3〜4ms、2回目以降は 1ms 未満。
- 中身の取得の時間は、コピー元のアプリが PNG に変換する時間を含む。
- **合格基準（テキストと画像の変化を受け取れる）を満たした。**

### わかったこと（本実装で考慮する）

- **1回のコピーで通知が3回来る：** コピー元のアプリが終了すると、`owner-changed` が「所有者なし（MIME タイプが空）」と「mutter が中身を引き取って保持し直した」の2回、追加で発生する。mutter はテキストだけでなく画像も保持し直した（形式は元の一覧のうち1つに絞られる）。履歴は**内容（ハッシュ）で重複を除く**必要がある。MIME タイプが空の通知は無視してよい。
- **取得の競合：** シグナルを受けてから中身を取りに行くまでに、クリップボードが次の内容に変わっていることがある。取得した内容が、どの通知に対応するのかは保証されない。本実装では、取得の前後で serial が変わっていないかを確かめるか、短いテキストに限ってはシグナルに中身を含める。
- **自分で設定した変化も通知される：** 履歴から選んでコピーしたときにも通知が来るので、本体は直前に自分で設定した内容を覚えておき、履歴を重複させない（先頭へ移動するだけにする）。
- **大きい画像：** 25MB でも D-Bus で受け取れた（このコンテナのセッションバスは上限 1GB。D-Bus の仕様上の上限は 128MiB）。ただし `byte[]` がラージオブジェクトヒープに載るので、履歴に保存する画像は大きさの上限（例えば 20MB）を設けて、超えたものは取得しない。
- **パスワード：** KeePassXC などは `x-kde-passwordManagerHint` を付ける。本体で無視するようにしたが、実際のパスワードマネージャーでの確認はしていない。
- **Tmds.DBus.Protocol の注意点：** `MessageWriter` は構造体なので、`Action<MessageWriter>` に渡すとコピーに書き込まれる。ヘッダーの本文の長さと食い違い、バスに接続を切られる（`Connection closed by peer`）。`ref` で渡すデリゲートを使う。
- **拡張の API（gjs）：** `St.Clipboard.get_content` が返す `GBytes` は、`GLib.Variant.new_from_bytes` で JS の配列に展開せずに返せる。拡張を更新したときは、Wayland ではログインし直さないと新しいコードが読み込まれない（`chuo install` で更新するときの注意点）。

### Fedora の実機で確認すること

- [ ] Firefox、LibreOffice、ターミナル、スクリーンショット（GNOME 標準の機能）からのコピーを受け取れるか
- [ ] KeePassXC などのパスワードマネージャーからのコピーに `x-kde-passwordManagerHint` が付くか
- [ ] 拡張を入れたことで gnome-shell のメモリや動作に影響がないか

## 6. 形態素解析（ReadingSpike）

### 方法

3つのライブラリで、読み（カタカナ）を付与してローマ字（ヘボン式）に変換した。エンジンごとに別のプロセスで計測し、辞書を読み込まない場合（`none`）を基準とした。

| ライブラリ | 版 | 辞書 | ライセンス |
|---|---|---|---|
| LibNMeCab（NMeCab）＋ LibNMeCab.IpaDicBin | 0.10.2 / 0.10.0 | IPADIC（`sys.dic` 約 49MB をメモリマップドファイルで読み込む） | GPL / LGPL / BSD から選択、IPADIC は独自ライセンス |
| MeCab.DotNet | 1.2.0 | IPADIC（同上） | GPL / LGPL / BSD から選択 |
| Lucene.Net.Analysis.Kuromoji | 4.8.0-beta00018 | アセンブリに埋め込み（マネージドヒープに展開する） | Apache 2.0 |

- 実行方法：`cd spikes/ReadingSpike && dotnet run -c Release`
- 環境：Linux（Debian 13、x64）、.NET 10.0.12、Release ビルド（ReadyToRun なし）
- `bulk` はアプリ名やファイル名を模した 20,000 件、`random` は CJK 統合漢字をランダムに並べた 20,000 件（辞書の広い範囲を引かせて、常駐量の上限の目安を得るため）

### 結果

時間（1件あたりの時間は、ローマ字への変換を含む）：

| | 辞書の読み込み | 初回の呼び出し | bulk（1件あたり） | random（1件あたり） |
|---|---|---|---|---|
| NMeCab | 20ms | 6.8ms | 19µs | 11µs |
| MeCab.DotNet | 24ms | 4.0ms | 26µs | 9µs |
| Kuromoji | 129ms | 0.6ms | 21µs | 8µs |

メモリ（`random` まで実行した後の値から、`none` の同じ時点の値を引いた増分）：

| | ワーキングセット | うち匿名メモリ（RssAnon） | うちファイルの割り当て（RssFile） | マネージドヒープ | 辞書を破棄した後のワーキングセットの増分 |
|---|---|---|---|---|---|
| NMeCab | +46MB | +5MB | +40MB | +0.3MB | +8MB |
| MeCab.DotNet | +48MB | +8MB | +40MB | +0.3MB | +10MB |
| Kuromoji | +35MB | +20MB | +14MB（主に DLL） | +13MB | +35MB（解放されない） |

読みの付与の品質は3つとも同等だった（同じ IPADIC 系の辞書のため）。

- ローマ字の前方一致：`memo`→メモ帳、`dentaku`→電卓、`gijiroku`→議事録_2026年10月.docx など 12 件中 11 件が一致した。
- 一致しなかったのは `hidemaru`→秀丸エディタ（`シュウマル` と読まれる）。固有名詞は辞書にないので、要件どおりプラグインの `Keywords` で補う必要がある。
- 半角カナ（`ｶﾀｶﾅﾒﾓ`）や全角英字は、**形態素解析にかける前に** NFKC で正規化しないと読みが付かない。

### 考察と推奨

- **NMeCab を採用する。** 読み込みが速く（20ms）、辞書はメモリマップドファイルなので、増えるワーキングセットの大半（約 40MB）は OS が必要に応じて捨てられるファイルのページである。匿名メモリの増分は約 5MB にとどまる。
- Kuromoji は辞書をマネージドヒープに展開するので、匿名メモリが約 20MB 増え、破棄しても解放されない（静的なシングルトン）。Lucene.Net が 2026 年時点でもベータ版である点も不安がある。
- 常駐量をさらに抑えるには、**インデックスを作るときだけ辞書を開き、終わったら破棄する**。破棄すればファイルのページは解放される（増分は +46MB から +8MB に下がる）。要件定義書 4.4 の「読みを SQLite にキャッシュし、辞書を常駐させない」方針は、この方式で実現できる見込みが高い。
  - ファイル検索で FileSystemWatcher から差分が来るたびに開き直しても、読み込みは約 20ms なので問題にならない。数秒まとめてから開く程度でよい。
- 1件あたり 10〜20µs なので、アプリ一覧（数百件）は 10ms 未満、ファイル 10 万件でも 2 秒程度で読みを付与できる。

### Windows で追加で確認すること

- Windows のタスクマネージャーの「メモリ」列はプライベートワーキングセットを表示するので、メモリマップドファイルの分は含まれないはずである。実機で確認する。

### 本実装に向けて決めること

検証の過程で、次の設計上の論点が見つかった。

- **ローマ字の表記揺れ：** `shi`/`si`、`tsu`/`tu`、`chi`/`ti`、`ou`/`o`/`oo` や、長音（`ー`）を `-` と打つか母音を重ねるか（`ekusupuro-ra-`、`ekusupuroora`）。読みからローマ字を作る方向で揺れを網羅するより、**入力したローマ字をかなに変換してから、かなの読みと照合する**方が素直に扱える可能性が高い（IME と同じ考え方で、末尾の未確定の子音だけ特別に扱う）。
- **空白や記号の扱い：** 「タスク マネージャー」の空白を読みから除くか。Kuromoji は空白を残し、MeCab 系は除く。照合の前に空白と記号を除く正規化を入れる方が扱いやすい。
- **数字を含む語の読み：** 「10月」の「月」が数字と切り離されて `ツキ` と読まれる（`10ツキ`）。数字の直後の助数詞は誤読しやすいが、検索への影響は小さいので当面は許容する。
