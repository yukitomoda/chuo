using Chuo.Core.Plugins;
using Chuo.Core.Search;
using Microsoft.Extensions.DependencyInjection;

namespace Chuo.Core;

public static class ServiceCollectionExtensions
{
    /// <summary>検索とプラグインの管理を登録する。</summary>
    public static IServiceCollection AddChuoCore(this IServiceCollection services)
    {
        services.AddSingleton<ProviderRegistry>();
        services.AddSingleton<SearchService>();
        services.AddHostedService<PluginManager>();
        services.Configure<PluginManagerOptions>(o => o.BundledDirectories.Add(ChuoPaths.BundledPluginDirectory));
        return services;
    }
}
