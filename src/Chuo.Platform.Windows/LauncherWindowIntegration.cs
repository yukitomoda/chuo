using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Chuo.Core.Platform;
using static Chuo.Platform.Windows.Native;

namespace Chuo.Platform.Windows;

/// <summary>
/// ランチャーのウィンドウに対する Windows 固有の処理。前面化、IME のオフ、メモリのトリムを行う。
/// ウィンドウをサブクラス化して、キー入力の検知と IME の監視のタイマーを処理する。
/// </summary>
[SupportedOSPlatform("windows")]
public sealed unsafe class LauncherWindowIntegration : ILauncherWindowIntegration
{
    private const nuint SubclassId = 0xC400;
    private const nuint ImeGuardTimerId = 0xC401;
    private const uint ImeGuardIntervalMs = 16;

    /// <summary>
    /// 表示してから IME の状態を監視する時間。IME はウィンドウごとに覚えた状態を、表示から 35〜40ms 後に非同期に復元する（技術検証 #3）。
    /// </summary>
    private static readonly TimeSpan ImeGuardDuration = TimeSpan.FromMilliseconds(500);

    private nint _hwnd;
    private GCHandle _self;
    private long _guardStartedAt;
    private bool _userKeySinceShow;

    public void Attach(nint windowHandle)
    {
        if (_hwnd != 0)
        {
            throw new InvalidOperationException("ウィンドウは設定済みです。");
        }

        _hwnd = windowHandle;
        _self = GCHandle.Alloc(this);
        SetWindowSubclass(windowHandle, &SubclassProc, SubclassId, (nuint)GCHandle.ToIntPtr(_self));
    }

    public (int X, int Y)? GetCursorPosition() => GetCursorPos(out var p) ? (p.X, p.Y) : null;

    public void OnShown(bool turnOffIme)
    {
        // ホットキー経由なら Activate だけで前面に出たが（技術検証 #2）、念のため確認する
        if (GetForegroundWindow() != _hwnd)
        {
            SetForegroundWindow(_hwnd);
        }

        _userKeySinceShow = false;
        if (turnOffIme)
        {
            ImeControl.SetOpenStatus(_hwnd, false);
            _guardStartedAt = Stopwatch.GetTimestamp();
            SetTimer(_hwnd, ImeGuardTimerId, ImeGuardIntervalMs, 0);
        }
    }

    public void TrimMemory()
    {
        // Aggressive は、使っていない GC のメモリもできるだけ OS に返す
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        SetProcessWorkingSetSize(GetCurrentProcess(), -1, -1);
    }

    /// <summary>
    /// 表示から一定時間、ユーザーがキーを押していないのに IME がオンになったら、オフに戻す。
    /// </summary>
    private void OnImeGuardTimer()
    {
        if (Stopwatch.GetElapsedTime(_guardStartedAt) > ImeGuardDuration)
        {
            KillTimer(_hwnd, ImeGuardTimerId);
            return;
        }
        if (!_userKeySinceShow && ImeControl.GetOpenStatus(_hwnd) == true)
        {
            ImeControl.SetOpenStatus(_hwnd, false);
        }
    }

    [UnmanagedCallersOnly]
    private static nint SubclassProc(nint hWnd, uint msg, nuint wParam, nint lParam, nuint id, nuint refData)
    {
        var self = (LauncherWindowIntegration)GCHandle.FromIntPtr((nint)refData).Target!;
        switch (msg)
        {
            // ホットキーの修飾キーを押したままのキーリピートは、ユーザーの入力として数えない
            case WM_KEYDOWN or WM_SYSKEYDOWN or WM_IME_KEYDOWN
                when (lParam & (1 << 30)) == 0 && (nint)wParam is not (VK_SHIFT or VK_CONTROL or VK_MENU or VK_LWIN or VK_RWIN):
                self._userKeySinceShow = true;
                break;
            case WM_TIMER when wParam == ImeGuardTimerId:
                self.OnImeGuardTimer();
                return 0;
            case WM_NCDESTROY:
                RemoveWindowSubclass(hWnd, &SubclassProc, SubclassId);
                self._self.Free();
                self._hwnd = 0;
                break;
        }
        return DefSubclassProc(hWnd, msg, wParam, lParam);
    }
}

/// <summary>
/// IME のオン・オフ。既定の IME ウィンドウに WM_IME_CONTROL を送って操作する。
/// Windows 11 の新しい Microsoft IME では、ImmGetContext / ImmSetOpenStatus では実際の状態を読み書きできなかった（技術検証 #3）。
/// </summary>
[SupportedOSPlatform("windows")]
internal static class ImeControl
{
    public static bool? GetOpenStatus(nint hwnd)
    {
        var imeWnd = ImmGetDefaultIMEWnd(hwnd);
        return imeWnd == 0 ? null : SendMessage(imeWnd, WM_IME_CONTROL, IMC_GETOPENSTATUS, 0) != 0;
    }

    public static bool SetOpenStatus(nint hwnd, bool open)
    {
        var imeWnd = ImmGetDefaultIMEWnd(hwnd);
        // IMC_SETOPENSTATUS は成功すると 0 を返す
        return imeWnd != 0 && SendMessage(imeWnd, WM_IME_CONTROL, IMC_SETOPENSTATUS, open ? 1 : 0) == 0;
    }
}
