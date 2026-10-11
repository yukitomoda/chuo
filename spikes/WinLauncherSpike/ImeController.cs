using static WinLauncherSpike.Native;

namespace WinLauncherSpike;

/// <summary>IME のオン・オフを操作する方法。</summary>
public enum ImeMethod
{
    /// <summary>ウィンドウの入力コンテキストを ImmGetContext で取得し、ImmGetOpenStatus / ImmSetOpenStatus で操作する。</summary>
    Imm,

    /// <summary>
    /// 既定の IME ウィンドウ（ImmGetDefaultIMEWnd）に WM_IME_CONTROL を送って操作する。
    /// Windows 11 の新しい Microsoft IME では、Imm の方法だと実際の状態を読み書きできなかった。
    /// </summary>
    ImeWnd,
}

/// <summary>指定したウィンドウの IME のオン・オフを操作する。</summary>
public static class ImeController
{
    /// <summary>現在の IME の状態を返す。取得できない場合は null を返す。</summary>
    public static bool? GetOpenStatus(nint hwnd, ImeMethod method) => method switch
    {
        ImeMethod.Imm => GetViaContext(hwnd),
        _ => GetViaImeWnd(hwnd),
    };

    /// <summary>IME の状態を設定する。設定できなかった場合は false を返す。</summary>
    public static bool SetOpenStatus(nint hwnd, bool open, ImeMethod method) => method switch
    {
        ImeMethod.Imm => SetViaContext(hwnd, open),
        _ => SetViaImeWnd(hwnd, open),
    };

    private static bool? GetViaContext(nint hwnd)
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

    private static bool SetViaContext(nint hwnd, bool open)
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

    private static bool? GetViaImeWnd(nint hwnd)
    {
        var imeWnd = ImmGetDefaultIMEWnd(hwnd);
        return imeWnd == 0 ? null : SendMessage(imeWnd, WM_IME_CONTROL, IMC_GETOPENSTATUS, 0) != 0;
    }

    private static bool SetViaImeWnd(nint hwnd, bool open)
    {
        var imeWnd = ImmGetDefaultIMEWnd(hwnd);
        // IMC_SETOPENSTATUS は成功すると 0 を返す
        return imeWnd != 0 && SendMessage(imeWnd, WM_IME_CONTROL, IMC_SETOPENSTATUS, open ? 1 : 0) == 0;
    }
}
