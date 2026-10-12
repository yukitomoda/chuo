using Chuo.Core.Items;
using Chuo.Core.Plugins;
using Chuo.Core.Search;
using Chuo.Core.Settings;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Chuo.Core;

public static class ServiceCollectionExtensions
{
    /// <summary>検索とプラグインの管理を登録する。</summary>
    public static IServiceCollection AddChuoCore(this IServiceCollection services)
    {
        services.AddSingleton<ProviderRegistry>();
        services.AddSingleton<SearchService>();
        services.AddSingleton<PluginManager>();
        services.AddHostedService(sp => sp.GetRequiredService<PluginManager>());
        services.AddSingleton(new SettingsStore(ChuoPaths.SettingsFile));
        services.AddSingleton(sp => new ItemPreferences(ChuoPaths.ItemsFile, sp.GetRequiredService<ILogger<ItemPreferences>>()));
        services.Configure<PluginManagerOptions>(o => o.BundledDirectories.Add(ChuoPaths.BundledPluginDirectory));
        return services;
    }
}
