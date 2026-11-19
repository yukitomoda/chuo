namespace Chuo.Abstractions;

/// <summary>OS の機能でファイルや URL を開く。プラグインはコンストラクタで受け取れる。</summary>
public interface IShell
{
    /// <summary>ファイル、フォルダ、URL を既定のアプリで開く。</summary>
    void Open(string target);

    /// <summary>ファイルやフォルダを、それを含むフォルダで表示する。</summary>
    void ShowInFolder(string path);

    /// <summary>
    /// 管理者として実行する（Windows のみ。ほかの OS では <see cref="NotSupportedException"/>）。
    /// ユーザーが UAC の確認を取り消したときは、何もせずに戻る。
    /// </summary>
    void RunAsAdministrator(string path);
}

/// <summary>クリップボード。プラグインはコンストラクタで受け取れる。</summary>
public interface IClipboard
{
    Task<string?> GetTextAsync(CancellationToken cancellationToken = default);

    Task SetTextAsync(string text, CancellationToken cancellationToken = default);
}

/// <summary>ランチャーのウィンドウの操作。プラグインはコンストラクタで受け取れる。</summary>
public interface ILauncher
{
    /// <summary>ランチャーを表示する。<paramref name="query"/> を指定すると入力欄に入れる。</summary>
    void Show(string? query = null);

    void Hide();
}

/// <summary>ユーザーへの通知を出す。プラグインはコンストラクタで受け取れる。</summary>
public interface INotifier
{
    /// <summary>通知を画面に表示する。どのスレッドから呼んでもよい。</summary>
    void Show(Notification notification);
}

/// <summary>通知の内容。</summary>
/// <param name="Title">見出し。</param>
/// <param name="Message">本文。</param>
/// <param name="Actions">閉じるボタンの横に並べる操作（スヌーズなど）。押すと操作を実行して通知を閉じる。</param>
/// <param name="PlaySound">通知音を鳴らすか。</param>
public sealed record Notification(
    string Title,
    string? Message = null,
    IReadOnlyList<NotificationAction>? Actions = null,
    bool PlaySound = true);

/// <summary>通知のボタン。</summary>
public sealed record NotificationAction(string Label, Action Execute);
