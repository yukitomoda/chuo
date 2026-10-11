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
