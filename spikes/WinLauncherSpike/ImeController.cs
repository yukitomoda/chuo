using static WinLauncherSpike.Native;

namespace WinLauncherSpike;

/// <summary>IMM32 を使って、指定したウィンドウの入力コンテキストの IME のオン・オフを操作する。</summary>
public static class ImeController
{
    /// <summary>現在の IME の状態を返す。入力コンテキストが取得できない場合は null を返す。</summary>
    public static bool? GetOpenStatus(nint hwnd)
    {
        var himc = ImmGetContext(hwnd);
        if (himc == 0)
        {
            return null;
        }

        try
        {
            return ImmGetOpenStatus(himc);
        }
        finally
        {
            ImmReleaseContext(hwnd, himc);
        }
    }

    /// <summary>IME の状態を設定する。入力コンテキストが取得できなかった場合や、設定に失敗した場合は false を返す。</summary>
    public static bool SetOpenStatus(nint hwnd, bool open)
    {
        var himc = ImmGetContext(hwnd);
        if (himc == 0)
        {
            return false;
        }

        try
        {
            return ImmSetOpenStatus(himc, open);
        }
        finally
        {
            ImmReleaseContext(hwnd, himc);
        }
    }
}
