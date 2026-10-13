namespace Chuo.Abstractions;

/// <summary>項目に対して実行できる操作。</summary>
/// <param name="Title">アクションパネルに表示する名前。</param>
/// <param name="Execute">実行する処理。実行後のランチャーの振る舞いを返す。</param>
public sealed record ItemAction(string Title, Func<ActionContext, ValueTask<ActionResult>> Execute)
{
    /// <summary>標準のアクションの種類。ホストは種類ごとに決まったキーを割り当てる。</summary>
    public ItemActionKind Kind { get; init; }

    /// <summary>管理者として実行する（Windows のみ）。キーは Ctrl+Shift+Enter。</summary>
    public static ItemAction RunAsAdministrator(string path, string title = "管理者として実行") =>
        new(title, ctx => Close(() => ctx.Shell.RunAsAdministrator(path))) { Kind = ItemActionKind.RunAsAdministrator };

    /// <summary>ファイルや URL を既定のアプリで開く。</summary>
    public static ItemAction Open(string target, string title = "開く") =>
        new(title, ctx => Close(() => ctx.Shell.Open(target)));

    /// <summary>ファイルやフォルダを、それを含むフォルダで表示する。</summary>
    public static ItemAction ShowInFolder(string path, string title = "フォルダを表示") =>
        new(title, ctx => Close(() => ctx.Shell.ShowInFolder(path)));

    /// <summary>テキストをクリップボードにコピーする。</summary>
    public static ItemAction CopyText(string text, string title = "コピー") =>
        new(title, async ctx =>
        {
            await ctx.Clipboard.SetTextAsync(text, ctx.CancellationToken);
            return ActionResult.Close;
        });

    private static ValueTask<ActionResult> Close(Action action)
    {
        action();
        return ValueTask.FromResult(ActionResult.Close);
    }
}

/// <summary>標準のアクションの種類。</summary>
public enum ItemActionKind
{
    /// <summary>種類を持たないアクション。キーは並びの位置で決まる（1つめは Enter、2つめは Ctrl+Enter）。</summary>
    None,

    /// <summary>管理者として実行する。キーは Ctrl+Shift+Enter。</summary>
    RunAsAdministrator,
}

/// <summary>アクションの実行時に渡される情報。</summary>
public sealed record ActionContext(IShell Shell, IClipboard Clipboard, CancellationToken CancellationToken);

/// <summary>アクションを実行した後のランチャーの振る舞い。</summary>
public abstract record ActionResult
{
    /// <summary>ランチャーを閉じる（既定）。</summary>
    public static readonly ActionResult Close = new CloseResult();

    /// <summary>ランチャーを開いたままにする。</summary>
    public static readonly ActionResult KeepOpen = new KeepOpenResult();

    /// <summary>入力欄の文字列を書き換える（フォルダの中に入るような、掘り下げ型の操作に使う）。</summary>
    public static ActionResult SetQuery(string text) => new SetQueryResult(text);

    public sealed record CloseResult : ActionResult;

    public sealed record KeepOpenResult : ActionResult;

    public sealed record SetQueryResult(string Text) : ActionResult;
}
