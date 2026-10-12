using System.Reflection;
using System.Text.RegularExpressions;
using Chuo.Abstractions;
using Chuo.Core.Hosting;
using Chuo.Core.Search;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Chuo.Core.Plugins;

/// <summary>読み込んだプラグイン1つ分。専用の ALC と、その中のミニ汎用ホストを持つ（設計 2 章）。</summary>
internal sealed class PluginInstance
{
    /// <summary>本体からプラグインのホストに中継するサービス。</summary>
    private static readonly Type[] RelayedServices = [typeof(IShell), typeof(IClipboard), typeof(ILauncher), typeof(ILoggerFactory)];

    private readonly PluginLoadContext _loadContext;
    private readonly IHost _host;

    public PluginManifest Manifest { get; }

    public string Directory { get; }

    /// <summary>カタログ型のプロバイダと、その項目の登録先。</summary>
    public IReadOnlyList<(CatalogRegistration Registration, ICatalogProvider Provider)> Catalogs { get; private set; }

    public IReadOnlyList<LiveQueryRegistration> LiveQueries { get; private set; }

    /// <summary>設定画面に出す設定のクラス。</summary>
    public IReadOnlyList<Type> SettingsTypes { get; private set; }

    private PluginInstance(
        PluginManifest manifest, string directory, PluginLoadContext loadContext, IHost host,
        IReadOnlyList<(CatalogRegistration, ICatalogProvider)> catalogs, IReadOnlyList<LiveQueryRegistration> liveQueries,
        IReadOnlyList<Type> settingsTypes)
    {
        SettingsTypes = settingsTypes;
        Manifest = manifest;
        Directory = directory;
        _loadContext = loadContext;
        _host = host;
        Catalogs = catalogs;
        LiveQueries = liveQueries;
    }

    /// <summary>プラグインを読み込み、ホストを開始してプロバイダを生成する。</summary>
    public static async Task<PluginInstance> LoadAsync(
        PluginManifest manifest, string directory, IServiceProvider hostServices, IConfiguration hostConfiguration,
        CancellationToken cancellationToken)
    {
        var entryPath = Path.Combine(directory, manifest.Entry);
        var loadContext = new PluginLoadContext(manifest.Id, entryPath);
        try
        {
            var assembly = loadContext.LoadFromAssemblyPath(entryPath);
            var types = assembly.GetTypes();

            var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings
            {
                ApplicationName = manifest.Id,
                ContentRootPath = directory,
            });
            foreach (var type in RelayedServices)
            {
                if (hostServices.GetService(type) is { } service)
                {
                    builder.Services.AddSingleton(type, service);
                }
            }
            builder.Services.Replace(ServiceDescriptor.Singleton<IHostLifetime, NullHostLifetime>());

            var context = new PluginContext(manifest.Id, directory, hostConfiguration.GetSection(PluginManager.SettingsSection(manifest.Id)));
            builder.Services.AddSingleton<IPluginContext>(context);

            if (types.FirstOrDefault(t => typeof(IChuoPlugin).IsAssignableFrom(t) && t is { IsAbstract: false, IsInterface: false }) is { } pluginType)
            {
                var plugin = (IChuoPlugin)Activator.CreateInstance(pluginType)!;
                plugin.ConfigureServices(builder.Services, context);
            }

            var catalogTypes = FindProviders<CatalogProviderAttribute>(types, typeof(ICatalogProvider));
            var liveQueryTypes = FindProviders<LiveQueryProviderAttribute>(types, typeof(ILiveQueryProvider));
            foreach (var (type, _) in catalogTypes)
            {
                builder.Services.TryAddSingleton(type);
            }
            foreach (var (type, _) in liveQueryTypes)
            {
                builder.Services.TryAddSingleton(type);
            }

            var host = builder.Build();
            await host.StartAsync(cancellationToken);

            var catalogs = catalogTypes
                .Select(p => (
                    new CatalogRegistration(manifest.Id, p.Type.Name, new ProviderCatalog(), p.Attribute.Keyword, p.Attribute.IncludeInGlobal),
                    (ICatalogProvider)host.Services.GetRequiredService(p.Type)))
                .ToList();
            var liveQueries = liveQueryTypes
                .Select(p => new LiveQueryRegistration(
                    manifest.Id, p.Type.Name, (ILiveQueryProvider)host.Services.GetRequiredService(p.Type), p.Attribute.Keyword,
                    p.Attribute.GlobalTrigger is { } trigger ? new Regex(trigger, RegexOptions.CultureInvariant) : null,
                    TimeSpan.FromMilliseconds(p.Attribute.TimeoutMs)))
                .ToList();

            var settingsTypes = host.Services.GetServices<PluginSettingsDescriptor>().Select(d => d.OptionsType).ToList();

            return new PluginInstance(manifest, directory, loadContext, host, catalogs, liveQueries, settingsTypes);
        }
        catch
        {
            loadContext.Unload();
            throw;
        }
    }

    /// <summary>ホストを止めて、ALC をアンロードする（設計 2 章の順）。</summary>
    public async Task UnloadAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _host.StopAsync(cancellationToken);
        }
        finally
        {
            _host.Dispose();
            // ALC をアンロードできるよう、プラグインの型への参照を残さない
            Catalogs = [];
            LiveQueries = [];
            SettingsTypes = [];
            _loadContext.Unload();
        }
    }

    private static List<(Type Type, TAttribute Attribute)> FindProviders<TAttribute>(Type[] types, Type providerInterface)
        where TAttribute : Attribute =>
        types
            .Where(t => t is { IsAbstract: false, IsInterface: false } && providerInterface.IsAssignableFrom(t))
            .Select(t => (Type: t, Attribute: t.GetCustomAttribute<TAttribute>()))
            .Where(p => p.Attribute is not null)
            .Select(p => (p.Type, p.Attribute!))
            .ToList();
}
