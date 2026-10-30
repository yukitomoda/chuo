using Avalonia;
using Chuo.Abstractions;
using Chuo.Core;
using Chuo.Core.Hosting;
using Chuo.Platform.Linux;
using Chuo.Platform.Windows;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Velopack;

namespace Chuo.App;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // インストーラーと更新の処理（設計 10.2）。インストールやアンインストールのときは、ここで処理して終了する。
        // インストール直後の初回の起動では、動いていることが分かるようにランチャーを出す
        var firstRun = false;
        VelopackApp.Build()
            .OnAfterInstallFastCallback(_ => SetAutoStart(true))
            .OnBeforeUninstallFastCallback(_ => SetAutoStart(false))
            .OnFirstRun(_ => firstRun = true)
            .Run();

        Directory.CreateDirectory(ChuoPaths.ConfigDirectory);

        // 起動を速くするため、既定の構成（appsettings.json や環境変数の読み込みなど）は使わない
        var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings
        {
            Args = args,
            ContentRootPath = AppContext.BaseDirectory,
        });
        builder.Configuration.AddJsonFile(ChuoPaths.SettingsFile, optional: true, reloadOnChange: true);
        builder.Logging.SetMinimumLevel(LogLevel.Information);
        builder.Logging.AddProvider(new FileLoggerProvider(ChuoPaths.LogDirectory));

        var services = builder.Services;
        services.AddSingleton<IHostLifetime, NullHostLifetime>();
        services.Configure<ChuoSettings>(builder.Configuration);
        services.AddChuoCore();
        if (OperatingSystem.IsWindows())
        {
            services.AddWindowsPlatform();
        }
        else
        {
            services.AddLinuxPlatform();
        }
        services.AddSingleton<LauncherService>();
        services.AddSingleton<ILauncher>(sp => sp.GetRequiredService<LauncherService>());
        services.AddSingleton<IClipboard, AvaloniaClipboard>();
        services.AddSingleton<IconCache>();
        services.AddSingleton<LauncherWindow>();
        services.AddSingleton<SettingsLauncher>();
        services.AddSingleton<UpdateService>();
        services.AddTransient<SettingsWindow>();

        using var host = builder.Build();
        var settings = host.Services.GetRequiredService<IOptions<ChuoSettings>>().Value;

        var appBuilder = AppBuilder.Configure(() => new App(host, showOnStart: firstRun || args.Contains("--show"), openSettingsOnStart: args.Contains("--settings")))
            .UsePlatformDetect();
        if (!settings.Rendering.UseGpu)
        {
            // ソフトウェア描画にすると、常駐メモリが大幅に小さくなる（技術検証 #5）
            appBuilder = appBuilder
                .With(new Win32PlatformOptions { RenderingMode = [Win32RenderingMode.Software] })
                .With(new X11PlatformOptions { RenderingMode = [X11RenderingMode.Software] });
        }
        return appBuilder.StartWithClassicDesktopLifetime(args, Avalonia.Controls.ShutdownMode.OnExplicitShutdown);
    }

    /// <summary>
    /// インストールしたら自動起動に登録し、アンインストールの前に外す（要件定義書 10 章）。
    /// アンインストールでは、別の場所の chuo が登録されていれば残す。失敗してもインストールは止めない。
    /// </summary>
    private static void SetAutoStart(bool enabled)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }
        try
        {
            var autoStart = new AutoStart();
            if (enabled || string.Equals(autoStart.GetRegisteredPath(), autoStart.CurrentPath, StringComparison.OrdinalIgnoreCase))
            {
                autoStart.SetEnabled(enabled);
            }
        }
        catch (Exception)
        {
            // ログはまだ使えない。設定画面から登録し直せる
        }
    }
}
