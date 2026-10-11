using System.Diagnostics;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;

namespace WinLauncherSpike;

public sealed class App : Application
{
    private GlobalHotkey? _hotkey;

    public override void Initialize()
    {
        Styles.Add(new FluentTheme());
    }

    public override async void OnFrameworkInitializationCompleted()
    {
        base.OnFrameworkInitializationCompleted();

        if (ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop)
        {
            return;
        }

        var options = Program.Options;
        var log = new SpikeLog();
        log.Write($"--- start: hotkey={options.Hotkey} imeOff={options.ImeOff} imeRestore={options.ImeRestore} imeMethod={options.ImeMethod} " +
                  $"render={options.RenderingMode?.ToString() ?? "default"} composition={options.CompositionMode?.ToString() ?? "default"} " +
                  $"renderOnUiThread={options.RenderOnUiThread} trimOnHide={options.TrimOnHide} " +
                  $"runtime={Environment.Version} os={Environment.OSVersion}");

        var window = new LauncherWindow(log, options);
        await window.PrewarmAsync();

        _hotkey = new GlobalHotkey(options.Hotkey);
        // ホットキーの専用スレッドから UI スレッドへは、最優先で転送する
        _hotkey.Pressed += e => Dispatcher.UIThread.Post(() => window.ShowFromHotkey(e), DispatcherPriority.Send);

        try
        {
            await _hotkey.StartAsync();
        }
        catch (Exception ex)
        {
            log.Write($"ホットキーの登録に失敗しました（他のアプリが使用中の可能性があります）: {ex.Message}");
            desktop.Shutdown(1);
            return;
        }

        desktop.Exit += (_, _) =>
        {
            _hotkey.Dispose();
            log.Write("--- exit");
        };

        log.Write($"ready: startup={SpikeLog.Ms(Program.StartTimestamp, Stopwatch.GetTimestamp()):F1}ms {SpikeLog.MemorySummary()}");

        // 待機状態が落ち着いた後のメモリ使用量も記録する
        DispatcherTimer.RunOnce(() => log.Write($"idle(10s): {SpikeLog.MemorySummary()}"), TimeSpan.FromSeconds(10));
    }
}
