using System.Diagnostics;
using Chuo.Core.Search;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Chuo.Core.Plugins;

/// <summary>プラグインを探すフォルダ。</summary>
public sealed class PluginManagerOptions
{
    /// <summary>
    /// 同梱プラグインのフォルダ。この下のフォルダを1つずつプラグインとして読み込む。
    /// TODO: ハッシュの検証（要件定義書 3.2）を実装したら、ユーザーが追加したプラグインと開発中のプラグインにも対応する。
    /// </summary>
    public List<string> BundledDirectories { get; } = [];
}

/// <summary>プラグインの読み込み、開始、アンロードを行う。本体の汎用ホストのホステッドサービスとして動く。</summary>
public sealed class PluginManager(
    ProviderRegistry registry,
    IServiceProvider services,
    IConfiguration configuration,
    IOptions<PluginManagerOptions> options,
    ILogger<PluginManager> logger) : IHostedService
{
    private readonly List<PluginInstance> _plugins = [];
    private readonly CancellationTokenSource _stopping = new();

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        foreach (var directory in options.Value.BundledDirectories.Where(Directory.Exists).SelectMany(Directory.EnumerateDirectories))
        {
            await LoadAsync(directory, cancellationToken);
        }
    }

    private async Task LoadAsync(string directory, CancellationToken cancellationToken)
    {
        if (!File.Exists(Path.Combine(directory, PluginManifest.FileName)))
        {
            return;
        }

        var start = Stopwatch.GetTimestamp();
        try
        {
            var manifest = PluginManifest.Load(directory);
            if (!manifest.IsCompatible())
            {
                logger.LogWarning("{Plugin} は hostApi {Required} を要求しており、このホスト（{Host}）では読み込めない",
                    manifest.Id, manifest.HostApi, Abstractions.HostApi.Version);
                return;
            }
            if (_plugins.Any(p => p.Manifest.Id == manifest.Id))
            {
                logger.LogWarning("{Plugin} は読み込み済みなので、{Directory} は無視する", manifest.Id, directory);
                return;
            }

            var plugin = await PluginInstance.LoadAsync(manifest, directory, services, configuration, cancellationToken);
            _plugins.Add(plugin);
            foreach (var registration in plugin.LiveQueries)
            {
                registry.Add(registration);
            }
            foreach (var (registration, provider) in plugin.Catalogs)
            {
                registry.Add(registration);
                // 項目の列挙に時間がかかっても起動を止めないよう、待たずに進む
                _ = InitializeCatalogAsync(registration, provider);
            }

            logger.LogInformation("{Plugin} {Version} を読み込んだ（{Elapsed:F1}ms、カタログ {Catalogs} 件、ライブクエリ {LiveQueries} 件）",
                manifest.Id, manifest.Version, Stopwatch.GetElapsedTime(start).TotalMilliseconds, plugin.Catalogs.Count, plugin.LiveQueries.Count);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "{Directory} のプラグインを読み込めなかった", directory);
        }
    }

    private async Task InitializeCatalogAsync(CatalogRegistration registration, Abstractions.ICatalogProvider provider)
    {
        var start = Stopwatch.GetTimestamp();
        try
        {
            await Task.Run(() => provider.InitializeAsync(registration.Catalog, _stopping.Token));
            logger.LogInformation("{Plugin}/{Provider} の項目を登録した（{Count} 件、{Elapsed:F1}ms）",
                registration.PluginId, registration.ProviderName, registration.Catalog.Count, Stopwatch.GetElapsedTime(start).TotalMilliseconds);
        }
        catch (OperationCanceledException) when (_stopping.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "{Plugin}/{Provider} の項目の登録に失敗した", registration.PluginId, registration.ProviderName);
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await _stopping.CancelAsync();
        foreach (var plugin in _plugins)
        {
            registry.RemovePlugin(plugin.Manifest.Id);
            try
            {
                await plugin.UnloadAsync(cancellationToken);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "{Plugin} の停止に失敗した", plugin.Manifest.Id);
            }
        }
        _plugins.Clear();
    }
}
