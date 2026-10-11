# chuo 設計

要件定義書（`docs/requirements.md`）の要件を、どう実現するかを書く。プラグイン API の型とコードの書き方は `docs/plugin-api.md` にある。設計は要件定義書と矛盾させない。

## 1. 技術スタック

| 項目 | 採用するもの |
|---|---|
| ランタイム | .NET 10（LTS） |
| UI | Avalonia（その時点の最新安定版） |
| 本体の構成 | 汎用ホスト（`Microsoft.Extensions.Hosting`） |
| 本体のビルド | ReadyToRun（プラグインを動的に読み込むため、NativeAOT は使わない） |
| CLI クライアント | NativeAOT |
| 設定ファイル | JSON（`System.Text.Json`。読み込みは `Microsoft.Extensions.Configuration.Json`） |
| アプリが書き込むデータ | SQLite（`Microsoft.Data.Sqlite`） |
| CI と配布物の生成 | GitHub Actions |

### ソリューション構成

| プロジェクト | 役割 |
|---|---|
| `Chuo.Abstractions` | プラグイン API。NuGet でも配布できる形にする |
| `Chuo.Core` | 検索、インデックス、順位付け、プラグイン管理、プラットフォームの抽象インターフェイス |
| `Chuo.Platform.Windows` / `Chuo.Platform.Linux` | ホットキー、クリップボード、アプリ列挙などの OS ごとの実装。macOS 用は実装を空にしてビルドだけ通す |
| `Chuo.App` | Avalonia の UI と汎用ホスト（常駐する本体） |
| `Chuo.Cli` | NativeAOT でビルドする `chuo` コマンド（IPC クライアント） |
| `plugins/Chuo.Plugins.*` | 同梱プラグイン |
| `tests/` | テスト |

## 2. プラグインの読み込み

- プラグインは C# のクラスライブラリ（アセンブリ）とする。
- プラグインごとに collectible な `AssemblyLoadContext`（ALC）で読み込み、プラグイン単位でアンロードやホットリロードができるようにする。
- ホスト側の ALC と共有するアセンブリは、`Chuo.Abstractions` と `Microsoft.Extensions.*` に限る。それ以外の依存（NuGet 由来など）は、プラグインのフォルダ内のものをそのプラグインの ALC の中でだけ解決する。
- 本体は汎用ホストで構築する。プラグインごとに、専用の ALC の中で**ミニ汎用ホスト**（`Host.CreateEmptyApplicationBuilder`）を作る。
- プロバイダは、ホストが属性をスキャンし、プラグインの子コンテナから生成する。
- 常駐処理は `IHostedService` / `BackgroundService` で書く。
- 本体のサービスは、プラグインのホストにインスタンスを中継して登録する。
- アンロードは `StopAsync()`、`Dispose()`、`ALC.Unload()` の順に行う。
- プラグインの設定は `settings.json` の `plugins.<id>` のセクションを `IConfiguration` として渡し、`IOptionsMonitor<T>` で即座に反映する。

### 2.1 メタデータ

- パッケージの身元情報は、プラグインのフォルダの `plugin.json` に書く。承認時の表示や互換性チェックは、コードを実行せずにこのファイルで行う。
- 機能の定義（プロバイダの種類、キーワード、全体検索に含めるか）はコードの属性で行う。

### 2.2 信頼の検証

| 種別 | 置き場所 | 読み込みの条件 |
|---|---|---|
| 同梱プラグイン | 本体のフォルダの `plugins/` | ビルド時に埋め込んだハッシュと一致すること |
| ユーザーが追加したプラグイン | データフォルダの `plugins/` | 承認リストに記録された「プラグイン ID＋フォルダ全体のハッシュ」と一致すること |
| 開発中のプラグイン | 設定画面で明示的に登録したパス | ハッシュは検証しない。ホットリロードを有効にする |

- `chuo plugin add <フォルダ>` は、データフォルダへのコピーと承認をまとめて行う。

## 3. 検索

検索はカタログ型とライブクエリ型の 2 種類のプロバイダによるハイブリッド方式とする。

### 3.1 カタログ型

- 対象：アプリ、ファイル、クリップボード履歴など、項目の一覧を事前に作れるもの。
- プラグインは項目をホストに登録するだけで、検索はホストがメモリ上のインデックスに対して行う。
- 項目の更新は、プラグインが任意のタイミング（起動時、ファイル変更の監視時、定期実行時など）でホストに通知する。
- 全体検索に含めるか、キーワードを付けたときだけ検索対象にするかを属性で指定する。

### 3.2 ライブクエリ型

- 対象：電卓、Web 検索など、入力に応じて結果を動的に作るもの。
- 基本はキーワードを指定したときだけ呼ばれる。
- グローバルトリガー（正規表現）を宣言した場合は、入力が一致したときだけ全体検索にも参加する。
- `CancellationToken` を必須とし、キー入力ごとに前のクエリをキャンセルする。プロバイダごとにタイムアウトを設ける。

### 3.3 順位付けと読み

- 順位付けはホストが一括で行う。
- カタログに項目を登録するときに、ホストが形態素解析で読み（かなとローマ字）を付与する。
- 辞書はインデックスを作るときだけ開き、終わったら破棄する。読みは SQLite にキャッシュし、辞書を常駐させない（技術検証 #6）。

## 4. プラットフォーム層

### 4.1 呼び出し

- ウィンドウは起動時に生成し、閉じるときは破棄せずに隠すだけにする。

| 環境 | ホットキー | 前面化とフォーカス | 対応時期 |
|---|---|---|---|
| Windows | `RegisterHotKey` | `SetForegroundWindow`（ホットキー経由の呼び出しなので許可される） | 初版 |
| GNOME（Wayland） | 同梱の GNOME Shell 拡張（`Main.wm.addKeybinding`） | 同梱の GNOME Shell 拡張（`Main.activateWindow`） | 初版（挑戦） |
| Linux X11 | `XGrabKey` | `_NET_ACTIVE_WINDOW` | 後回し |
| Wayland（KDE / wlroots 系） | XDG Desktop Portal の `GlobalShortcuts` | 未定 | 後回し |

### 4.2 IME の制御

- Windows では、自分のウィンドウの入力コンテキストだけを制御する。IME が後から状態を復元するため、表示後しばらくは状態を監視してオフに戻す（技術検証 #3）。
- Linux では IBus（Fedora の既定）と Fcitx5 を D-Bus 経由で制御する。具体的な方式（入力コンテキストごとの制御か、Avalonia の入力目的のヒントを使うか）は試作で検証する。

### 4.3 クリップボードの監視

| 環境 | 監視方法 | 対応時期 |
|---|---|---|
| Windows | `AddClipboardFormatListener` | 初版 |
| GNOME（Wayland） | 同梱の GNOME Shell 拡張から D-Bus で通知を受ける（4.4） | 初版（挑戦） |
| Linux X11 | XFixes のイベント | 後回し |
| Wayland（KDE / wlroots 系） | data-control プロトコル | 後回し |

### 4.4 GNOME Shell 拡張

GNOME の Wayland では、アプリ側からはホットキーの登録、フォーカスの取得、ウィンドウの位置決め、クリップボードの監視ができない（または不確実である）。そこで、chuo に同梱する GNOME Shell 拡張に、GNOME Shell の内部でしかできない処理だけを任せる。

| 役割 | 拡張の中で使う API |
|---|---|
| グローバルホットキー | `Main.wm.addKeybinding` |
| ウィンドウの前面化とフォーカス | `Main.activateWindow` |
| ウィンドウの位置（マウスのあるモニタの中央など） | `MetaWindow.move_frame` |
| クリップボードの監視 | `St.Clipboard` と selection の変更イベント |

- 本体との通信は D-Bus で行う（本体側は `Tmds.DBus`）。
- 拡張は JavaScript で書く。C# だけで作るという方針の**唯一の例外**とし、ロジックはすべて C# 側に置く。拡張はイベントの中継と GNOME Shell の API 呼び出しだけを行う。
- GNOME のメジャー更新（約半年ごと）に追従する保守コストがかかるため、拡張の役割は上の表の範囲に限定する。

### 4.5 アプリの列挙

- Windows はスタートメニューの `.lnk`、Linux は `.desktop` からアプリを列挙し、アイコンも取得する。
- アプリを置いているフォルダの変更を `FileSystemWatcher` で監視し、変更が落ち着いてから（2秒後に）一覧を作り直す。
- Windows
  - 対象はユーザーと全ユーザーのスタートメニューの `.lnk` と `.url`。
  - `.lnk` は COM を使わずにファイルの形式（MS-SHLLINK）を直接読み、リンク先のパスを得る。リンク先は、サブタイトルと「ファイルの場所を開く」、実行ファイルの名前での検索に使う。
  - アイコンはホストが `IShellItemImageFactory` で取得する。シェルの COM オブジェクトを安全に使うため、STA の専用スレッドで処理する。
- Linux
  - XDG のデータフォルダの `applications/` を優先度の順に読み、同じデスクトップファイル ID は優先度の高いものだけを使う。`NoDisplay`、`Hidden`、`OnlyShowIn` / `NotShowIn`、`TryExec` に従って除外する。
  - 起動は `gio launch` に任せる（端末で開くアプリや起動の通知も正しく扱える）。`gio` がなければ `Exec` を直接実行する。
  - アイコンは hicolor と Adwaita のテーマ、`/usr/share/pixmaps` から PNG を探す。使用中のアイコンテーマには従わず、SVG にしかないアイコンは表示できない。

## 5. `chuo` コマンドと IPC

- `chuo` コマンドは NativeAOT でビルドした軽量クライアントとする。
- IPC は `NamedPipeServerStream`（Windows では名前付きパイプ、Unix ではドメインソケットとして動く）で行う。

## 6. 項目とユーザー設定のデータ

- 項目の ID はプラグイン内で一意で、再起動しても変わらない値とする（例：`.desktop` のパス、AppUserModelID、ファイルのフルパス）。
- グローバルな識別子は `<pluginId>:<itemId>` とする。使用履歴、エイリアス、お気に入り、無視の設定で共通に使う。
- 項目ごとのユーザー設定は `items.json` にまとめて保存する。

```json
{
  "chuo.apps:firefox.desktop": { "aliases": ["ff", "web"], "favorite": 1 },
  "chuo.apps:uninstall-foo.desktop": { "ignore": true }
}
```

- `favorite` はお気に入りの表示順を表す。
- 無視の glob パターンはグローバルな識別子に対して照合する（例：`chuo.apps:*uninstall*`）。無視の判定はホストが行い、カタログ型とライブクエリ型の両方の結果に適用する。

## 7. ファイル検索

- 自前のインデックスを持つ。起動時にスキャンし、その後は `FileSystemWatcher` で差分を反映する。

## 8. UI

- プラグインは UI のコントロールを返さない。プレビューや設定画面は、プラグインが返す宣言的なデータからホストが描画する。
- プレビューは項目を選択したときに初めて取得する。型は `docs/plugin-api.md` を参照。
- 設定画面は、常駐するメモリを増やさないため、開いたときに生成し、閉じたら破棄する。
- プラグインの設定画面は、ホストが Options クラスのプロパティと属性から自動で生成する。属性は `System.ComponentModel.DataAnnotations` を使い、標準の属性で表せないもの（フォルダやファイルの選択など）は独自の属性を用意する。対応していない型のプロパティがある場合、ホストは読み込み時に警告を出す。

## 9. 設定とデータのファイル

| 種類 | ファイル | 編集方法 |
|---|---|---|
| 設定 | `settings.json` | 設定画面で編集する。手で編集することもでき、保存すると自動で再読み込みされる |
| 項目ごとのユーザー設定 | `items.json` | アプリが書き込む。手で編集することもできる |
| 使用履歴、承認リスト | SQLite | アプリのみが書き込む |
| ログ | `logs/` | アプリが書き込む。日ごとにファイルを切り替え、プラグインごとにカテゴリを分ける |

- アプリが設定ファイルを書き込むときは、一時ファイルに書いてから置き換える。自分の書き込みによる変更の通知では再読み込みしない。

| OS | 設定 | データ |
|---|---|---|
| Linux | `~/.config/chuo/` | `~/.local/share/chuo/` |
| Windows | `%APPDATA%\chuo\` | `%APPDATA%\chuo\` |

## 10. インストール

| 処理 | Windows | Linux |
|---|---|---|
| 自動起動の登録 | `HKCU\...\Run` | `~/.config/autostart/chuo.desktop` |
| PATH への追加 | ユーザーの PATH に追加 | `~/.local/bin` にシンボリックリンクを作成 |
| アプリ一覧への登録 | — | `.desktop` を登録 |
| GNOME Shell 拡張 | — | GNOME の環境では、拡張をインストールして有効化する |
