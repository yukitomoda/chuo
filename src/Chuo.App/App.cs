using System.Diagnostics;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using Chuo.Core;
using Chuo.Core.Platform;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Chuo.App;

public sealed class App(IHost host, bool showOnStart) : Application
{
    private IDisposable? _hotkey;

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

        var services = host.Services;
        var logger = services.GetRequiredService<ILogger<App>>();
        desktop.Exit += (_, _) => Shutdown(logger);

        var window = services.GetRequiredService<LauncherWindow>();
        services.GetRequiredService<LauncherService>().Attach(window);
        await window.PrewarmAsync();

        // プラグインの読み込みは UI スレッドを止めないよう、スレッドプールで行う
        await Task.Run(() => host.StartAsync());

        var settings = services.GetRequiredService<IOptions<ChuoSettings>>().Value;
        try
        {
            // ホットキーの専用スレッドから UI スレッドへは、最優先で転送する
            _hotkey = await services.GetRequiredService<IGlobalHotkeyService>()
                .RegisterAsync(settings.Hotkey, () => Dispatcher.UIThread.Post(window.Toggle, DispatcherPriority.Send));
        }
        catch (PlatformNotSupportedException ex)
        {
            logger.LogWarning("ホットキーを登録できない: {Message}", ex.Message);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "ホットキー {Hotkey} を登録できなかった", settings.Hotkey);
        }

        // ランタイムの起動も含めるため、プロセスの開始時刻から測る
        using var process = Process.GetCurrentProcess();
        logger.LogInformation("起動した（{Elapsed:F0}ms）", (DateTime.Now - process.StartTime).TotalMilliseconds);

        if (showOnStart)
        {
            window.ShowLauncher();
        }
        else
        {
            window.TrimMemoryIfEnabled();
        }
    }

    private void Shutdown(ILogger logger)
    {
        _hotkey?.Dispose();
        try
        {
            // プラグインの停止を待つ。UI スレッドの同期コンテキストを使わないよう、スレッドプールで行う
            Task.Run(() => host.StopAsync(TimeSpan.FromSeconds(5))).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "終了の処理に失敗した");
        }
        logger.LogInformation("終了した");
    }
}
