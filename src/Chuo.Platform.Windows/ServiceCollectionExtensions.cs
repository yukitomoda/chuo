using System.Runtime.Versioning;
using Chuo.Abstractions;
using Chuo.Core.Platform;
using Microsoft.Extensions.DependencyInjection;

namespace Chuo.Platform.Windows;

public static class ServiceCollectionExtensions
{
    [SupportedOSPlatform("windows")]
    public static IServiceCollection AddWindowsPlatform(this IServiceCollection services)
    {
        services.AddSingleton<IGlobalHotkeyService, GlobalHotkeyService>();
        services.AddSingleton<ILauncherWindowIntegration, LauncherWindowIntegration>();
        services.AddSingleton<IShell, WindowsShell>();
        services.AddSingleton<IFileIconProvider, FileIconProvider>();
        services.AddSingleton<IAutoStart, AutoStart>();
        return services;
    }
}
