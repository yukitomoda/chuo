using Chuo.Abstractions;
using Chuo.Core.Platform;
using Microsoft.Extensions.DependencyInjection;

namespace Chuo.Platform.Linux;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Linux の実装を登録する。
    /// TODO: GNOME Shell 拡張（ホットキー、前面化、クリップボード）と X11 に対応する。今はホットキーがないので、
    /// 起動時の <c>--show</c> でしか表示できない。
    /// </summary>
    public static IServiceCollection AddLinuxPlatform(this IServiceCollection services)
    {
        services.AddSingleton<IGlobalHotkeyService, UnsupportedGlobalHotkeyService>();
        services.AddSingleton<ILauncherWindowIntegration, NullLauncherWindowIntegration>();
        services.AddSingleton<IShell, LinuxShell>();
        services.AddSingleton<IFileIconProvider, NullFileIconProvider>();
        return services;
    }
}
