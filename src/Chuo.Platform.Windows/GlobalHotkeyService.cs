using System.ComponentModel;
using System.Runtime.Versioning;
using Chuo.Core.Platform;
using static Chuo.Platform.Windows.Native;

namespace Chuo.Platform.Windows;

/// <summary>
/// RegisterHotKey でグローバルホットキーを登録する。
/// UI スレッドの負荷に左右されないよう、メッセージループを持つ専用スレッドで受け取る（技術検証 #1）。
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class GlobalHotkeyService : IGlobalHotkeyService
{
    public Task<IDisposable> RegisterAsync(string gesture, Action pressed)
    {
        var (modifiers, key) = HotkeyGesture.Parse(gesture);
        var registration = new Registration(modifiers, key, pressed);
        return registration.StartAsync();
    }

    private sealed class Registration(uint modifiers, uint virtualKey, Action pressed) : IDisposable
    {
        private const int HotkeyId = 1;

        private readonly TaskCompletionSource<IDisposable> _registered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private Thread? _thread;
        private uint _threadId;

        public Task<IDisposable> StartAsync()
        {
            _thread = new Thread(Run) { IsBackground = true, Name = "GlobalHotkey" };
            _thread.Start();
            return _registered.Task;
        }

        private void Run()
        {
            _threadId = GetCurrentThreadId();

            // hWnd に 0 を渡すと、WM_HOTKEY はこのスレッドのメッセージキューに届く
            if (!RegisterHotKey(0, HotkeyId, modifiers | MOD_NOREPEAT, virtualKey))
            {
                _registered.SetException(new Win32Exception());
                return;
            }
            _registered.SetResult(this);

            try
            {
                while (GetMessage(out var msg, 0, 0, 0) > 0)
                {
                    if (msg.message == WM_HOTKEY)
                    {
                        pressed();
                    }
                }
            }
            finally
            {
                UnregisterHotKey(0, HotkeyId);
            }
        }

        public void Dispose()
        {
            if (_threadId != 0)
            {
                PostThreadMessage(_threadId, WM_QUIT, 0, 0);
                _thread?.Join(TimeSpan.FromSeconds(1));
                _threadId = 0;
            }
        }
    }
}

/// <summary>ホットキーの文字列（例: <c>Alt+Space</c>、<c>Ctrl+Shift+K</c>）を解釈する。</summary>
public static class HotkeyGesture
{
    public static (uint Modifiers, uint VirtualKey) Parse(string gesture)
    {
        uint modifiers = 0;
        uint? key = null;

        foreach (var part in gesture.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            switch (part.ToUpperInvariant())
            {
                case "ALT": modifiers |= MOD_ALT; break;
                case "CTRL" or "CONTROL": modifiers |= MOD_CONTROL; break;
                case "SHIFT": modifiers |= MOD_SHIFT; break;
                case "WIN": modifiers |= MOD_WIN; break;
                case var k: key = ParseKey(k) ?? throw new ArgumentException($"未対応のキーです: {part}"); break;
            }
        }

        return (modifiers, key ?? throw new ArgumentException($"キーが指定されていません: {gesture}"));
    }

    private static uint? ParseKey(string key) => key switch
    {
        "SPACE" => 0x20,
        "ENTER" => 0x0D,
        "TAB" => 0x09,
        // A〜Z と 0〜9 は仮想キーコードが ASCII コードと一致する
        { Length: 1 } when char.IsAsciiLetterUpper(key[0]) || char.IsAsciiDigit(key[0]) => key[0],
        ['F', .. var n] when int.TryParse(n, out var f) && f is >= 1 and <= 24 => (uint)(0x70 + f - 1),
        _ => null,
    };
}
