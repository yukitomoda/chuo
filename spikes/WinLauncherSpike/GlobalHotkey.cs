using System.ComponentModel;
using System.Diagnostics;
using static WinLauncherSpike.Native;

namespace WinLauncherSpike;

/// <summary>ホットキーを受け取った時点の計測値。</summary>
/// <param name="ReceivedTimestamp">専用スレッドが WM_HOTKEY を取り出した時点の <see cref="Stopwatch"/> のタイムスタンプ。</param>
/// <param name="QueueDelayMs">WM_HOTKEY がキューに積まれてから取り出されるまでの時間（ミリ秒、GetTickCount の精度）。</param>
public readonly record struct HotkeyEvent(long ReceivedTimestamp, uint QueueDelayMs);

/// <summary>
/// RegisterHotKey でグローバルホットキーを登録する。
/// UI スレッドの負荷に左右されないよう、メッセージループを持つ専用スレッドで受け取る。
/// </summary>
public sealed class GlobalHotkey : IDisposable
{
    private const int HotkeyId = 1;

    private readonly uint _modifiers;
    private readonly uint _virtualKey;
    private readonly Thread _thread;
    private readonly TaskCompletionSource _registered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private uint _threadId;

    public event Action<HotkeyEvent>? Pressed;

    public GlobalHotkey(string gesture)
    {
        (_modifiers, _virtualKey) = Parse(gesture);
        _thread = new Thread(Run) { IsBackground = true, Name = "GlobalHotkey" };
    }

    /// <summary>専用スレッドを開始し、ホットキーの登録が終わるまで待つ。登録に失敗した場合は例外を投げる。</summary>
    public Task StartAsync()
    {
        _thread.Start();
        return _registered.Task;
    }

    private void Run()
    {
        _threadId = GetCurrentThreadId();

        // hWnd に 0 を渡すと、WM_HOTKEY はこのスレッドのメッセージキューに届く
        if (!RegisterHotKey(0, HotkeyId, _modifiers | MOD_NOREPEAT, _virtualKey))
        {
            _registered.SetException(new Win32Exception());
            return;
        }
        _registered.SetResult();

        try
        {
            while (GetMessage(out var msg, 0, 0, 0) > 0)
            {
                if (msg.message != WM_HOTKEY)
                {
                    continue;
                }

                var received = Stopwatch.GetTimestamp();
                var queueDelay = unchecked(GetTickCount() - msg.time);
                Pressed?.Invoke(new HotkeyEvent(received, queueDelay));
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
            _thread.Join(TimeSpan.FromSeconds(1));
        }
    }

    private static (uint Modifiers, uint VirtualKey) Parse(string gesture)
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
