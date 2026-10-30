using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using Chuo.Core;
using Chuo.Core.Platform;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Chuo.App;

public sealed class App(IHost host, bool showOnStart, bool openSettingsOnStart) : Application
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

        var settingsLauncher = services.GetRequiredService<SettingsLauncher>();
        settingsLauncher.Closed += window.TrimMemoryIfEnabled;
        var updates = services.GetRequiredService<UpdateService>();
        updates.Shutdown = () => desktop.Shutdown();
        updates.Start();
        AddTrayIcon(window, settingsLauncher, updates, desktop, logger);
        if (showOnStart)
        {
            window.ShowLauncher();
        }
        else if (openSettingsOnStart)
        {
            settingsLauncher.Open();
        }
        else
        {
            window.TrimMemoryIfEnabled();
        }
    }

    /// <summary>
    /// タスクトレイ（通知領域）にアイコンを出す（要件定義書 4 章）。クリックでランチャーを開き、右クリックのメニューから設定画面と終了を選べる。
    /// トレイのない環境（拡張のない GNOME など）では表示されないだけで、ほかの動作には影響しない。
    /// </summary>
    private void AddTrayIcon(
        LauncherWindow window, SettingsLauncher settingsLauncher, UpdateService updates, IClassicDesktopStyleApplicationLifetime desktop, ILogger logger)
    {
        try
        {
            NativeMenuItem Item(string header, Action action)
            {
                var item = new NativeMenuItem(header);
                item.Click += (_, _) => action();
                return item;
            }

            // 新しい版があるときだけ出す
            var update = Item("", () => _ = updates.ApplyAsync());
            void UpdateItem()
            {
                update.Header = $"v{updates.AvailableVersion} に更新";
                update.IsVisible = updates.AvailableVersion is not null;
            }
            UpdateItem();
            updates.Changed += UpdateItem;

            using var icon = AssetLoader.Open(new Uri("avares://chuo/Assets/chuo.png"));
            var tray = new TrayIcon
            {
                Icon = new WindowIcon(icon),
                ToolTipText = "chuo",
                Menu =
                [
                    Item("ランチャーを開く", () => window.ShowLauncher()),
                    Item("設定", settingsLauncher.Open),
                    update,
                    new NativeMenuItemSeparator(),
                    Item("終了", () => desktop.Shutdown()),
                ],
            };
            tray.Clicked += (_, _) => window.ShowLauncher();
            TrayIcon.SetIcons(this, [tray]);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "タスクトレイにアイコンを出せなかった");
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
