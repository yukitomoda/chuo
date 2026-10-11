namespace Chuo.Core.Platform;

/// <summary>グローバルホットキー。</summary>
public interface IGlobalHotkeyService
{
    /// <summary>
    /// ホットキーを登録する。<paramref name="pressed"/> は UI スレッドではないスレッドから呼ばれる。
    /// 登録に失敗した場合（他のアプリが使用中など）は例外を投げる。破棄すると登録を解除する。
    /// </summary>
    Task<IDisposable> RegisterAsync(string gesture, Action pressed);
}

/// <summary>ランチャーのウィンドウに対する、OS ごとの処理。メソッドはすべて UI スレッドで呼ぶ。</summary>
public interface ILauncherWindowIntegration
{
    /// <summary>ウィンドウが作られたときに一度呼ぶ。</summary>
    void Attach(nint windowHandle);

    /// <summary>マウスカーソルの位置（物理ピクセル）。取得できなければ null。</summary>
    (int X, int Y)? GetCursorPosition();

    /// <summary>ウィンドウを表示して Activate した直後に呼ぶ。前面化を確実にし、必要なら IME をオフにする。</summary>
    void OnShown(bool turnOffIme);

    /// <summary>ウィンドウを隠した後に呼ぶ。使っていないメモリを OS に返す。</summary>
    void TrimMemory();
}

/// <summary>OS ごとの処理がない環境で使う、何もしない実装。</summary>
public sealed class NullLauncherWindowIntegration : ILauncherWindowIntegration
{
    public void Attach(nint windowHandle)
    {
    }

    public (int X, int Y)? GetCursorPosition() => null;

    public void OnShown(bool turnOffIme)
    {
    }

    public void TrimMemory() =>
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
}

/// <summary>グローバルホットキーに対応していない環境で使う実装。</summary>
public sealed class UnsupportedGlobalHotkeyService : IGlobalHotkeyService
{
    public Task<IDisposable> RegisterAsync(string gesture, Action pressed) =>
        Task.FromException<IDisposable>(new PlatformNotSupportedException("この環境はグローバルホットキーに対応していません。"));
}

/// <summary>ファイル（実行ファイル、ショートカットなど）に関連付けられたアイコンを OS から取得する。</summary>
public interface IFileIconProvider
{
    /// <summary>
    /// アイコンを <paramref name="size"/> ピクセル四方以内で取得する。どのスレッドから呼んでもよい。取得できなければ null を返す。
    /// </summary>
    Task<IconPixels?> GetIconAsync(string path, int size, CancellationToken cancellationToken = default);
}

/// <summary>アイコンの画素。上の行から順に並んだ、アルファ乗算済みの BGRA（1行は <c>Width * 4</c> バイト）。</summary>
public sealed record IconPixels(int Width, int Height, byte[] Bgra);

/// <summary>ファイルのアイコンを取得できない環境で使う実装。</summary>
public sealed class NullFileIconProvider : IFileIconProvider
{
    public Task<IconPixels?> GetIconAsync(string path, int size, CancellationToken cancellationToken = default) =>
        Task.FromResult<IconPixels?>(null);
}
