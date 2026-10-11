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

namespace Chuo.App;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
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

        using var host = builder.Build();
        var settings = host.Services.GetRequiredService<IOptions<ChuoSettings>>().Value;

        var appBuilder = AppBuilder.Configure(() => new App(host, showOnStart: args.Contains("--show")))
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
}
