using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using static WinLauncherSpike.Native;

namespace WinLauncherSpike;

/// <summary>
/// ランチャーのウィンドウを模したもの。起動時に一度だけ生成し、以後は表示と非表示を切り替えるだけにする。
/// </summary>
public sealed class LauncherWindow : Window
{
    private const double WindowWidth = 680;
    private const double WindowHeight = 420;
    private const double ImeGuardMs = 500;

    private readonly SpikeLog _log;
    private readonly SpikeOptions _options;
    private readonly TextBox _input;
    private readonly TextBlock _status;
    private bool? _imeStatusBeforeShow;
    private bool _prewarming;
    private bool _hiding;
    private long _shownAt;
    private bool _userKeySinceShow;
    private int _showCount;

    public LauncherWindow(SpikeLog log, SpikeOptions options)
    {
        _log = log;
        _options = options;

        Width = WindowWidth;
        Height = WindowHeight;
        CanResize = false;
        ShowInTaskbar = false;
        Topmost = true;
        WindowDecorations = WindowDecorations.None;
        WindowStartupLocation = WindowStartupLocation.Manual;

        _input = new TextBox
        {
            PlaceholderText = "検索…",
            FontSize = 22,
            Margin = new Thickness(12),
        };

        // 結果の一覧の描画コストを本番に近づけるためのダミーの項目
        var results = new ListBox
        {
            ItemsSource = Enumerable.Range(1, 8).Select(i => $"ダミーの結果 {i}").ToList(),
            Margin = new Thickness(12, 0),
        };

        _status = new TextBlock
        {
            Margin = new Thickness(12),
            FontSize = 12,
            Foreground = Brushes.Gray,
            TextWrapping = TextWrapping.Wrap,
        };

        var root = new DockPanel();
        DockPanel.SetDock(_input, Dock.Top);
        DockPanel.SetDock(_status, Dock.Bottom);
        root.Children.Add(_input);
        root.Children.Add(_status);
        root.Children.Add(results);
        Content = root;

        KeyDown += OnKeyDown;
        Win32Properties.AddWndProcHookCallback(this, WndProcHook);
        Deactivated += (_, _) =>
        {
            if (!_prewarming && IsVisible)
            {
                HideLauncher("focus-lost");
            }
        };
    }

    private nint Hwnd => TryGetPlatformHandle()?.Handle ?? 0;

    /// <summary>
    /// ウィンドウのハンドルの生成と初回の描画を済ませておく。
    /// 画面外に一度表示してから隠すことで、初回の表示だけが遅くなるのを防ぐ。
    /// </summary>
    public async Task PrewarmAsync()
    {
        var start = Stopwatch.GetTimestamp();
        _prewarming = true;
        Position = new PixelPoint(-32000, -32000);
        Show();
        await WaitForFramesAsync(2);
        Hide();
        _prewarming = false;
        _log.Write($"prewarm: {SpikeLog.Ms(start, Stopwatch.GetTimestamp()):F1}ms hwnd=0x{Hwnd:X}");
    }

    /// <summary>ホットキーを受け取ったときの処理。UI スレッドで呼ぶこと。</summary>
    public async void ShowFromHotkey(HotkeyEvent e)
    {
        var dispatched = Stopwatch.GetTimestamp();

        if (IsVisible)
        {
            HideLauncher("toggle");
            return;
        }

        _showCount++;
        _shownAt = dispatched;
        _userKeySinceShow = false;
        MoveToCursorScreen();
        Show();

        // Activate だけでは前面に出ない場合があるため、結果を記録して SetForegroundWindow も試す
        Activate();
        var hwnd = Hwnd;
        var foregroundAfterActivate = GetForegroundWindow() == hwnd;
        var setForegroundResult = foregroundAfterActivate || SetForegroundWindow(hwnd);

        _input.Focus();
        _input.SelectAll();

        var (imeBefore, imeSetResult) = TurnOffIme(hwnd);
        var imeAttempts = 1;
        var shown = Stopwatch.GetTimestamp();

        // 1フレーム目のコールバックの時点では描画がまだ確定していないため、2フレーム目まで待つ。
        // Avalonia はフォーカスを失うと入力コンテキストの関連付けを外し、表示した直後はまだ付け直していないことがある
        // （2回目以降の表示で ImmGetContext が 0 を返した）。その場合はフレームごとに IME の操作をやり直す
        for (var i = 0; i < 2; i++)
        {
            await WaitForFramesAsync(1);
            if (imeBefore is null)
            {
                (imeBefore, imeSetResult) = TurnOffIme(hwnd);
                imeAttempts++;
            }
        }
        _imeStatusBeforeShow = imeBefore;
        var rendered = Stopwatch.GetTimestamp();

        var isForeground = GetForegroundWindow() == hwnd;
        var isFocused = _input.IsFocused;
        var imeStatusAfter = ImeController.GetOpenStatus(hwnd, _options.ImeMethod);

        var total = SpikeLog.Ms(e.ReceivedTimestamp, rendered);
        var message =
            $"show #{_showCount}: total={total:F1}ms " +
            $"(queue={e.QueueDelayMs}ms dispatch={SpikeLog.Ms(e.ReceivedTimestamp, dispatched):F1}ms " +
            $"show={SpikeLog.Ms(dispatched, shown):F1}ms render={SpikeLog.Ms(shown, rendered):F1}ms) " +
            $"foreground={isForeground} (activate={foregroundAfterActivate} setForeground={setForegroundResult}) " +
            $"focused={isFocused} " +
            $"ime(method={_options.ImeMethod} before={Format(_imeStatusBeforeShow)} set={imeSetResult} after={Format(imeStatusAfter)} attempts={imeAttempts}) " +
            $"{ImeNow(hwnd)} " +
            SpikeLog.MemorySummary();
        _log.Write(message);

        GuardImeAfterShow(hwnd);

        _status.Text =
            $"#{_showCount} 合計 {total:F1}ms（キュー {e.QueueDelayMs}ms／UIへの転送 {SpikeLog.Ms(e.ReceivedTimestamp, dispatched):F1}ms／" +
            $"表示 {SpikeLog.Ms(dispatched, shown):F1}ms／描画 {SpikeLog.Ms(shown, rendered):F1}ms）\n" +
            $"前面={isForeground} フォーカス={isFocused} IME: 表示前={Format(_imeStatusBeforeShow)} → 表示後={Format(imeStatusAfter)}\n" +
            $"{SpikeLog.MemorySummary()}\nログ: {_log.FilePath}";
    }

    /// <summary>
    /// 表示した直後にオフにしても、数百 ms 後に IME がオンに戻ることがある（Windows 11 の新しい Microsoft IME で確認）。
    /// 表示から一定時間、状態を監視し、ユーザーがキーを押していないのにオンになったらオフに戻す。
    /// </summary>
    private void GuardImeAfterShow(nint hwnd)
    {
        if (!_options.ImeOff)
        {
            return;
        }

        var showCount = _showCount;
        var last = ImeController.GetOpenStatus(hwnd, _options.ImeMethod);
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        timer.Tick += (_, _) =>
        {
            var elapsed = SpikeLog.Ms(_shownAt, Stopwatch.GetTimestamp());
            if (!IsVisible || showCount != _showCount || elapsed > ImeGuardMs)
            {
                timer.Stop();
                _log.Write($"ime-guard #{showCount} end +{elapsed:F0}ms: {ImeNow(hwnd)}");
                return;
            }

            var now = ImeController.GetOpenStatus(hwnd, _options.ImeMethod);
            if (now == last)
            {
                return;
            }

            last = now;
            if (now == true && !_userKeySinceShow)
            {
                var set = ImeController.SetOpenStatus(hwnd, false, _options.ImeMethod);
                last = ImeController.GetOpenStatus(hwnd, _options.ImeMethod);
                _log.Write($"ime-guard #{showCount} +{elapsed:F0}ms: turned on -> set off={set} now={Format(last)}");
            }
            else
            {
                _log.Write($"ime-guard #{showCount} +{elapsed:F0}ms: changed to {Format(now)} userKey={_userKeySinceShow}");
            }
        };
        timer.Start();
    }

    /// <summary>IME の状態が変わるきっかけを調べるため、表示から1秒間、関係するメッセージを記録する。</summary>
    private nint WndProcHook(nint hWnd, uint msg, nint wParam, nint lParam, ref bool handled)
    {
        switch (msg)
        {
            // ホットキーの修飾キーを押したままのキーリピートは、ユーザーの入力として数えない
            case WM_KEYDOWN or WM_SYSKEYDOWN or WM_IME_KEYDOWN
                when (lParam & (1 << 30)) == 0 && wParam is not (VK_SHIFT or VK_CONTROL or VK_MENU or VK_LWIN or VK_RWIN):
                _userKeySinceShow = true;
                break;
            case WM_IME_SETCONTEXT or WM_IME_NOTIFY when IsVisible:
                var elapsed = SpikeLog.Ms(_shownAt, Stopwatch.GetTimestamp());
                if (elapsed < 1000 && (msg == WM_IME_SETCONTEXT || wParam == IMN_SETOPENSTATUS))
                {
                    var name = msg == WM_IME_SETCONTEXT ? $"WM_IME_SETCONTEXT(active={wParam != 0})" : "IMN_SETOPENSTATUS";
                    _log.Write($"wndproc #{_showCount} +{elapsed:F0}ms: {name} {ImeNow(hWnd)}");
                }
                break;
        }
        return 0;
    }

    /// <summary>IME の状態を取得し、オプションに応じてオフにする。入力コンテキストが取得できなければ (null, false) を返す。</summary>
    private (bool? Before, bool Set) TurnOffIme(nint hwnd)
    {
        var before = ImeController.GetOpenStatus(hwnd, _options.ImeMethod);
        var set = _options.ImeOff && before is not null && ImeController.SetOpenStatus(hwnd, false, _options.ImeMethod);
        return (before, set);
    }

    private void HideLauncher(string reason)
    {
        // Hide() の中で Deactivated が発生し、もう一度呼ばれるので無視する
        if (_hiding)
        {
            return;
        }

        _hiding = true;
        try
        {
            HideCore(reason);
        }
        finally
        {
            _hiding = false;
        }
    }

    /// <summary>非表示の間のメモリを記録する。--trim-on-hide のときは、GC を実行してワーキングセットを OS に返す。</summary>
    private void AfterHide()
    {
        var showCount = _showCount;
        if (_options.TrimOnHide)
        {
            // 非表示にする処理を妨げないよう、後に回す
            Dispatcher.UIThread.Post(() =>
            {
                var start = Stopwatch.GetTimestamp();
                // Aggressive は、使っていない GC のメモリもできるだけ OS に返す
                GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
                var trimmed = SetProcessWorkingSetSize(GetCurrentProcess(), -1, -1);
                _log.Write($"trim #{showCount}: {SpikeLog.Ms(start, Stopwatch.GetTimestamp()):F1}ms ok={trimmed} {SpikeLog.MemorySummary()}");
            }, DispatcherPriority.Background);
        }

        DispatcherTimer.RunOnce(() =>
        {
            if (!IsVisible && showCount == _showCount)
            {
                _log.Write($"hidden(1s) #{showCount}: {SpikeLog.MemorySummary()}");
            }
        }, TimeSpan.FromSeconds(1));
    }

    private void HideCore(string reason)
    {
        // 閉じる前の状態を、両方の方法で読んで記録する（ユーザーが入力欄で IME をオンにしたかが読めるか）
        var imeNow = ImeNow(Hwnd);
        if (_options.ImeRestore && _imeStatusBeforeShow is { } before)
        {
            ImeController.SetOpenStatus(Hwnd, before, _options.ImeMethod);
        }

        Hide();
        _log.Write($"hide: reason={reason} {imeNow}");
        AfterHide();
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                HideLauncher("escape");
                e.Handled = true;
                break;
            case Key.Q when e.KeyModifiers == (KeyModifiers.Control | KeyModifiers.Shift):
                // 検証用の終了操作
                _log.Write("exit requested");
                (Application.Current?.ApplicationLifetime as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime)?.Shutdown();
                break;
        }
    }

    /// <summary>マウスカーソルのあるモニタの中央（やや上寄り）に移動する。</summary>
    private void MoveToCursorScreen()
    {
        GetCursorPos(out var cursor);
        var screen = Screens.ScreenFromPoint(new PixelPoint(cursor.X, cursor.Y)) ?? Screens.Primary;
        if (screen is null)
        {
            return;
        }

        var area = screen.WorkingArea;
        var width = (int)(WindowWidth * screen.Scaling);
        var height = (int)(WindowHeight * screen.Scaling);
        Position = new PixelPoint(area.X + (area.Width - width) / 2, area.Y + (area.Height - height) / 3);
    }

    private Task WaitForFramesAsync(int frames)
    {
        var tcs = new TaskCompletionSource();

        void Request(int remaining)
        {
            RequestAnimationFrame(_ =>
            {
                if (remaining <= 1)
                {
                    tcs.TrySetResult();
                }
                else
                {
                    Request(remaining - 1);
                }
            });
        }

        Request(frames);
        return tcs.Task;
    }

    private static string ImeNow(nint hwnd) =>
        $"imeNow(imm={Format(ImeController.GetOpenStatus(hwnd, ImeMethod.Imm))} " +
        $"imewnd={Format(ImeController.GetOpenStatus(hwnd, ImeMethod.ImeWnd))})";

    private static string Format(bool? status) => status switch
    {
        true => "on",
        false => "off",
        null => "n/a",
    };
}
