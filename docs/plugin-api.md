# プラグイン API の設計

要件定義書（`docs/requirements.md`）の 2 章と 9.2、設計（`docs/design.md`）の 2 章と 3 章を、コード上の形に落としたもの。型の名前や書き方はこのファイルに書き、要件定義書には書かない。実装は `src/Chuo.Abstractions` にあり、食い違ったときは実装が正とする。

## plugin.json

パッケージの身元情報を書く（設計 2.1）。

```json
{
  "id": "chuo.files",
  "name": "File Search",
  "version": "1.0.0",
  "entry": "Chuo.Plugins.Files.dll",
  "hostApi": "1.x"
}
```

- `hostApi` は `HostApi.Version`（今は 1.3）と比べる。`1.x` と `1` はメジャーが同じなら、`1.1` はさらにホストのマイナーが 1 以上なら読み込む。
- API を足したらマイナーを、互換性のない変更をしたらメジャーを上げる。1.1 では `IShell.RunAsAdministrator` と `ItemAction.Kind` を、1.2 では `ResultItem.Completion` を、1.3 ではプロバイダの属性の `Title` を足した。

## プロバイダの宣言

プロバイダはクラスに属性を付けて宣言する。属性の値は既定値で、ユーザーが設定画面で上書きできる（要件定義書 2.1）。

```csharp
[CatalogProvider(Keyword = "f", IncludeInGlobal = false)]
public sealed class FileCatalog : ICatalogProvider { ... }

[LiveQueryProvider(Keyword = "=", GlobalTrigger = @"^[\d\s+\-*/().]+$")]
public sealed class Calculator : ILiveQueryProvider { ... }
```

| 種類（設計 3 章） | インターフェイス | 属性 |
|---|---|---|
| カタログ型 | `ICatalogProvider`（`InitializeAsync(ICatalog, CancellationToken)`） | `[CatalogProvider]`（`Title`、`Keyword`、`IncludeInGlobal`） |
| ライブクエリ型 | `ILiveQueryProvider`（`QueryAsync(Query, CancellationToken)`） | `[LiveQueryProvider]`（`Title`、`Keyword`、`GlobalTrigger`、`TimeoutMs`） |

- ライブクエリ型は、入力がキーワードだけ（空白なし）のときも `Text` を空にして呼ばれる。結果は全体検索の結果の上に出るので、空の入力で出すものがなければ空を返す。入力欄を書き換える `SetQuery` には、`RawText` ではなく `Keyword + " "` を前に付ける。
- `Title` は設定画面に出す名前で、プラグインにプロバイダが複数あるときの見出しに使う。なければクラス名を出す。

- ライブクエリ型を `IQueryProvider` としないのは、暗黙の using に含まれる `System.Linq.IQueryProvider` と名前が衝突するため。

## プラグインのエントリと構成

プラグインは `IChuoPlugin` を実装したクラスを1つ持ち、ミニ汎用ホストにサービスを登録する（設計 2 章）。

```csharp
public sealed class ClipboardPlugin : IChuoPlugin
{
    public void ConfigureServices(IServiceCollection services, IPluginContext ctx)
    {
        services.AddSingleton<ClipboardStore>();
        services.AddHostedService<ClipboardWatcher>();
        services.Configure<ClipboardOptions>(ctx.Configuration);
    }
}
```

- `IPluginContext` は、プラグインの ID、フォルダ、データフォルダ、設定（`settings.json` の `plugins:<id>:settings` のセクション）を持つ。
- プロバイダはホストが属性をスキャンし、プラグインの子コンテナから生成する。コンストラクタで依存を受け取れる。
- 常駐処理は `IHostedService` / `BackgroundService` で書く。
- 本体から中継するサービス：`IShell`、`IClipboard`、`ILauncher`、`ILoggerFactory`（`ILogger<T>`）。`IPluginStorage` は未実装。

## 項目とアクション

| 型 | 内容 |
|---|---|
| `ResultItem` | `Id`、`Title`、`Subtitle`、`Icon`、`Actions`、`Preview`、`Completion` |
| `CatalogItem` | `ResultItem` に検索用の `Keywords` を加えたもの |
| `IconSource` | ファイルのアイコン（今は Windows のみ）、画像ファイル、画像のバイト列 |
| `ItemAction` | タイトルと実行する処理。項目の `Actions` に並べた順に、Enter、Ctrl+Enter に割り当たり、すべてアクションパネルと右クリックのメニューに出る。`Open`、`ShowInFolder`、`CopyText`、`RunAsAdministrator`（Windows のみ）のヘルパーがある |
| `ItemActionKind` | 標準のアクションの種類。ホストが種類ごとにキーを割り当てる（`RunAsAdministrator` は Ctrl+Shift+Enter）。`None` のアクションは並びの位置でキーが決まる（1つめは Enter、2つめは Ctrl+Enter） |
| `ActionResult` | 実行後の動作：閉じる（`Close`）、開いたままにする（`KeepOpen`）、入力を置き換える（`SetQuery`。入力が同じでも検索し直すので、一覧の作り直しにも使える） |
| `Preview` | テキスト、画像、ファイル、プロパティの一覧 |

- `Completion` は Tab で補完したときの検索の文字列で、キーワードは含めない（例：ファイル検索で `C:\Us` に対して `C:\Users\`）。ホストは、選んでいる項目の補完が入力で始まっていれば、続きを薄く表示する。同梱のアプリとショートカットのプラグインは、タイトルを補完にしている。

## プラグインの設定画面

Options クラスを `AddPluginSettings<T>` で登録すると、ホストがプロパティと属性から画面を生成する（要件定義書 9.2、設計 8 章）。設定の値は `IOptionsMonitor<T>` で受け取り、変更はその場で反映される。

```csharp
public sealed class ShortcutsOptions
{
    [Display(Name = "対象のフォルダ", Description = "この中のショートカットと実行ファイルを検索する")]
    public List<ShortcutFolder> Folders { get; set; } = [];
}

// リストの要素ごとの設定は、要素を設定のクラスにする
public sealed class ShortcutFolder
{
    [Display(Name = "フォルダ", Order = 0), FolderPath]
    public string Path { get; set; } = "";

    [Display(Name = "サブフォルダも対象にする", Order = 1)]
    public bool IncludeSubfolders { get; set; } = true;
}

public sealed class ShortcutsPlugin : IChuoPlugin
{
    public void ConfigureServices(IServiceCollection services, IPluginContext context) =>
        services.AddPluginSettings<ShortcutsOptions>(context);
}
```

| プロパティの型 | 入力欄 |
|---|---|
| `bool` | チェックボックス |
| `int`、`long` / `double`、`float`、`decimal` | 数値（`[Range]` で範囲を指定できる） |
| `string` | テキスト。`[FolderPath]` / `[FilePath]` を付けると「参照…」が付く |
| 列挙型 | 選択肢。値に `[Display(Name)]` を付けると、その名前で表示する（設定には値の名前を書き込む） |
| `List<string>` | 一覧と追加・削除。`[FolderPath]` / `[FilePath]` を付けると「参照…」が付く |
| `List<T>`（T は設定のクラス） | 要素ごとに値を持つ一覧。下を参照 |

- 表示名、説明、並び順は `[Display(Name, Description, Order)]` で指定する。説明は、名前で分かるなら付けない。付けるときも短い句にする（設計 8 章）。
- これ以外の型のプロパティは画面に出さず、読み込み時にログに警告を出す。
- `List<T>` の要素のクラスにも、同じ型と属性が使える。
  - 最初の `string` のプロパティ（並び順で最初のもの）が要素の代表になる。一覧に表示し、追加の入力欄でこの値を入れる。同じ値の要素は追加できない。`string` のプロパティがないクラスは対応しない。
  - 残りのプロパティは、要素ごとに代表の値の下に入力欄が並ぶ。新しい要素は、それらに既定値を入れて作る。
  - 要素の中のリストには対応しない。
  - `settings.json` には、要素ごとにオブジェクトとして保存する（例：`"folders": [{ "path": "C:\\Tools", "includeSubfolders": false }]`）。
- **リストの既定値は空にする。** 構成のバインドは既存のリストに値を追加するので、既定値を入れると設定の値がその後ろに足されてしまう。
- キーワードと全体検索に含めるかの上書きは、ホストがすべてのプロバイダについて画面に出す。プラグインの設定に含める必要はない。
