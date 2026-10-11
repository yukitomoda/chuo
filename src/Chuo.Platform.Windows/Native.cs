using System.Runtime.InteropServices;

namespace Chuo.Platform.Windows;

/// <summary>Win32 API の宣言。</summary>
internal static unsafe partial class Native
{
    public const uint WM_QUIT = 0x0012;
    public const uint WM_TIMER = 0x0113;
    public const uint WM_HOTKEY = 0x0312;
    public const uint WM_KEYDOWN = 0x0100;
    public const uint WM_SYSKEYDOWN = 0x0104;
    public const uint WM_IME_CONTROL = 0x0283;
    public const uint WM_IME_KEYDOWN = 0x0290;
    public const uint WM_NCDESTROY = 0x0082;

    public const nuint IMC_GETOPENSTATUS = 0x0005;
    public const nuint IMC_SETOPENSTATUS = 0x0006;

    public const uint MOD_ALT = 0x0001;
    public const uint MOD_CONTROL = 0x0002;
    public const uint MOD_SHIFT = 0x0004;
    public const uint MOD_WIN = 0x0008;
    public const uint MOD_NOREPEAT = 0x4000;

    public const nint VK_SHIFT = 0x10;
    public const nint VK_CONTROL = 0x11;
    public const nint VK_MENU = 0x12;
    public const nint VK_LWIN = 0x5B;
    public const nint VK_RWIN = 0x5C;

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MSG
    {
        public nint hwnd;
        public uint message;
        public nuint wParam;
        public nint lParam;
        public uint time;
        public POINT pt;
    }

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool RegisterHotKey(nint hWnd, int id, uint fsModifiers, uint vk);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool UnregisterHotKey(nint hWnd, int id);

    [LibraryImport("user32.dll", EntryPoint = "GetMessageW")]
    public static partial int GetMessage(out MSG lpMsg, nint hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [LibraryImport("user32.dll", EntryPoint = "PostThreadMessageW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool PostThreadMessage(uint idThread, uint msg, nuint wParam, nint lParam);

    [LibraryImport("kernel32.dll")]
    public static partial uint GetCurrentThreadId();

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetCursorPos(out POINT lpPoint);

    [LibraryImport("user32.dll")]
    public static partial nint GetForegroundWindow();

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetForegroundWindow(nint hWnd);

    [LibraryImport("user32.dll", EntryPoint = "SendMessageW")]
    public static partial nint SendMessage(nint hWnd, uint msg, nuint wParam, nint lParam);

    [LibraryImport("user32.dll")]
    public static partial nuint SetTimer(nint hWnd, nuint nIDEvent, uint uElapse, nint lpTimerFunc);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool KillTimer(nint hWnd, nuint uIDEvent);

    [LibraryImport("imm32.dll")]
    public static partial nint ImmGetDefaultIMEWnd(nint hWnd);

    [LibraryImport("comctl32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetWindowSubclass(
        nint hWnd, delegate* unmanaged<nint, uint, nuint, nint, nuint, nuint, nint> pfnSubclass, nuint uIdSubclass, nuint dwRefData);

    [LibraryImport("comctl32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool RemoveWindowSubclass(
        nint hWnd, delegate* unmanaged<nint, uint, nuint, nint, nuint, nuint, nint> pfnSubclass, nuint uIdSubclass);

    [LibraryImport("comctl32.dll")]
    public static partial nint DefSubclassProc(nint hWnd, uint uMsg, nuint wParam, nint lParam);

    [LibraryImport("kernel32.dll")]
    public static partial nint GetCurrentProcess();

    /// <summary>min と max に -1 を渡すと、ワーキングセットをできるだけ OS に返す（EmptyWorkingSet と同じ）。</summary>
    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetProcessWorkingSetSize(nint process, nint minimumWorkingSetSize, nint maximumWorkingSetSize);
}
