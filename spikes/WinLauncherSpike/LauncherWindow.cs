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

    private readonly SpikeLog _log;
    private readonly SpikeOptions _options;
    private readonly TextBox _input;
    private readonly TextBlock _status;
    private bool? _imeStatusBeforeShow;
    private bool _prewarming;
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
        MoveToCursorScreen();
        Show();

        // Activate だけでは前面に出ない場合があるため、結果を記録して SetForegroundWindow も試す
        Activate();
        var hwnd = Hwnd;
        var foregroundAfterActivate = GetForegroundWindow() == hwnd;
        var setForegroundResult = foregroundAfterActivate || SetForegroundWindow(hwnd);

        _input.Focus();
        _input.SelectAll();

        _imeStatusBeforeShow = ImeController.GetOpenStatus(hwnd);
        var imeSetResult = _options.ImeOff && ImeController.SetOpenStatus(hwnd, false);
        var shown = Stopwatch.GetTimestamp();

        // 1フレーム目のコールバックの時点では描画がまだ確定していないため、2フレーム目まで待つ
        await WaitForFramesAsync(2);
        var rendered = Stopwatch.GetTimestamp();

        var isForeground = GetForegroundWindow() == hwnd;
        var isFocused = _input.IsFocused;
        var imeStatusAfter = ImeController.GetOpenStatus(hwnd);

        var total = SpikeLog.Ms(e.ReceivedTimestamp, rendered);
        var message =
            $"show #{_showCount}: total={total:F1}ms " +
            $"(queue={e.QueueDelayMs}ms dispatch={SpikeLog.Ms(e.ReceivedTimestamp, dispatched):F1}ms " +
            $"show={SpikeLog.Ms(dispatched, shown):F1}ms render={SpikeLog.Ms(shown, rendered):F1}ms) " +
            $"foreground={isForeground} (activate={foregroundAfterActivate} setForeground={setForegroundResult}) " +
            $"focused={isFocused} " +
            $"ime(before={Format(_imeStatusBeforeShow)} set={imeSetResult} after={Format(imeStatusAfter)}) " +
            SpikeLog.MemorySummary();
        _log.Write(message);

        _status.Text =
            $"#{_showCount} 合計 {total:F1}ms（キュー {e.QueueDelayMs}ms／UIへの転送 {SpikeLog.Ms(e.ReceivedTimestamp, dispatched):F1}ms／" +
            $"表示 {SpikeLog.Ms(dispatched, shown):F1}ms／描画 {SpikeLog.Ms(shown, rendered):F1}ms）\n" +
            $"前面={isForeground} フォーカス={isFocused} IME: 表示前={Format(_imeStatusBeforeShow)} → 表示後={Format(imeStatusAfter)}\n" +
            $"{SpikeLog.MemorySummary()}\nログ: {_log.FilePath}";
    }

    private void HideLauncher(string reason)
    {
        if (_options.ImeRestore && _imeStatusBeforeShow is { } before)
        {
            ImeController.SetOpenStatus(Hwnd, before);
        }

        Hide();
        _log.Write($"hide: reason={reason}");
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

    private static string Format(bool? status) => status switch
    {
        true => "on",
        false => "off",
        null => "n/a",
    };
}
