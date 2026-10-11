namespace Chuo.Abstractions;

/// <summary>結果の一覧に表示する項目。</summary>
public class ResultItem
{
    /// <summary>プラグインの中で一意で、再起動しても変わらない値（例: ファイルのフルパス）。使用履歴やお気に入りの識別に使う。</summary>
    public required string Id { get; init; }

    public required string Title { get; init; }

    public string? Subtitle { get; init; }

    public IconSource? Icon { get; init; }

    /// <summary>項目のアクション。先頭が既定のアクションで、Enter で実行される。</summary>
    public IReadOnlyList<ItemAction> Actions { get; init; } = [];

    /// <summary>プレビューを取得する処理。項目が選択されたときに初めて呼ばれる。プレビューがなければ null。</summary>
    public Func<CancellationToken, Task<Preview>>? Preview { get; init; }
}

/// <summary>カタログ型のプロバイダが登録する項目。</summary>
public class CatalogItem : ResultItem
{
    /// <summary>タイトルのほかに検索の対象にする文字列（読み、別名など）。</summary>
    public IReadOnlyList<string> Keywords { get; init; } = [];
}

/// <summary>項目のアイコン。</summary>
public abstract record IconSource
{
    /// <summary>ファイル（実行ファイル、ショートカット、フォルダなど）に関連付けられたアイコン。ホストが OS から取得する。</summary>
    public static IconSource FromFile(string path) => new FileIconSource(path);

    /// <summary>画像ファイル（PNG など）。</summary>
    public static IconSource FromImage(string path) => new ImageFileIconSource(path);

    /// <summary>画像のバイト列（PNG など）。</summary>
    public static IconSource FromBytes(ReadOnlyMemory<byte> data) => new ImageBytesIconSource(data);
}

public sealed record FileIconSource(string Path) : IconSource;

public sealed record ImageFileIconSource(string Path) : IconSource;

public sealed record ImageBytesIconSource(ReadOnlyMemory<byte> Data) : IconSource;
