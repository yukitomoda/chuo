namespace Chuo.Abstractions;

/// <summary>OS の機能でファイルや URL を開く。プラグインはコンストラクタで受け取れる。</summary>
public interface IShell
{
    /// <summary>ファイル、フォルダ、URL を既定のアプリで開く。</summary>
    void Open(string target);

    /// <summary>ファイルやフォルダを、それを含むフォルダで表示する。</summary>
    void ShowInFolder(string path);
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
