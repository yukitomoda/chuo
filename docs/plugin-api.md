# プラグイン API の設計

要件定義書（`docs/requirements.md` の 3 章と 10.2）を、コード上の形に落としたもの。型の名前や書き方はこのファイルに書き、要件定義書には書かない。実装は `src/Chuo.Abstractions` にあり、食い違ったときは実装が正とする。

## plugin.json

パッケージの身元情報を書く（要件定義書 3.3）。

```json
{
  "id": "chuo.files",
  "name": "File Search",
  "version": "1.0.0",
  "entry": "Chuo.Plugins.Files.dll",
  "hostApi": "1.x"
}
```

- `hostApi` は `HostApi.Version` のメジャーバージョンと比べる（`1.x`、`1.0`、`1` を受け付ける）。

## プロバイダの宣言

プロバイダはクラスに属性を付けて宣言する。属性の値は既定値で、ユーザーが設定画面で上書きできる（要件定義書 3.3）。

```csharp
[CatalogProvider(Keyword = "f", IncludeInGlobal = false)]
public sealed class FileCatalog : ICatalogProvider { ... }

[LiveQueryProvider(Keyword = "=", GlobalTrigger = @"^[\d\s+\-*/().]+$")]
public sealed class Calculator : ILiveQueryProvider { ... }
```

| 種類（要件定義書 4 章） | インターフェイス | 属性 |
|---|---|---|
| カタログ型 | `ICatalogProvider`（`InitializeAsync(ICatalog, CancellationToken)`） | `[CatalogProvider]`（`Keyword`、`IncludeInGlobal`） |
| ライブクエリ型 | `ILiveQueryProvider`（`QueryAsync(Query, CancellationToken)`） | `[LiveQueryProvider]`（`Keyword`、`GlobalTrigger`、`TimeoutMs`） |

- ライブクエリ型を `IQueryProvider` としないのは、暗黙の using に含まれる `System.Linq.IQueryProvider` と名前が衝突するため。

## プラグインのエントリと構成

プラグインは `IChuoPlugin` を実装したクラスを1つ持ち、ミニ汎用ホストにサービスを登録する（要件定義書 3.4）。

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

- `IPluginContext` は、プラグインの ID、フォルダ、データフォルダ、設定（`settings.json` の `plugins:<id>` のセクション）を持つ。
- プロバイダはホストが属性をスキャンし、プラグインの子コンテナから生成する。コンストラクタで依存を受け取れる。
- 常駐処理は `IHostedService` / `BackgroundService` で書く。
- 本体から中継するサービス：`IShell`、`IClipboard`、`ILauncher`、`ILoggerFactory`（`ILogger<T>`）。`IPluginStorage` は未実装。

## 項目とアクション

| 型 | 内容 |
|---|---|
| `ResultItem` | `Id`、`Title`、`Subtitle`、`Icon`、`Actions`、`Preview` |
| `CatalogItem` | `ResultItem` に検索用の `Keywords` を加えたもの |
| `IconSource` | ファイルのアイコン、画像ファイル、画像のバイト列 |
| `ItemAction` | タイトルと実行する処理。`Open`、`ShowInFolder`、`CopyText` のヘルパーがある |
| `ActionResult` | 実行後の動作：閉じる（`Close`）、開いたままにする（`KeepOpen`）、入力を置き換える（`SetQuery`） |
| `Preview` | テキスト、画像、ファイル、プロパティの一覧 |

## プラグインの設定画面

Options クラスのプロパティと属性から、ホストが画面を生成する（要件定義書 10.2）。属性は `System.ComponentModel.DataAnnotations` を使い、標準で表せないものは独自の属性を用意する。

```csharp
public sealed class FileSearchOptions
{
    [Display(Name = "対象のフォルダ"), FolderPath]
    public List<string> Folders { get; set; } = [];

    [Display(Name = "全体検索に含める", Description = "オフにすると f キーワードを付けたときだけ検索する")]
    public bool IncludeInGlobal { get; set; }
}
```

- 設定画面と `FolderPath` などの独自の属性は未実装。
