using System.Collections.Immutable;
using System.Diagnostics;
using Chuo.Core.Search;
using Chuo.Core.Settings;
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
    /// TODO: ハッシュの検証（設計 2.2）を実装したら、ユーザーが追加したプラグインと開発中のプラグインにも対応する。
    /// </summary>
    public List<string> BundledDirectories { get; } = [];
}

/// <summary>読み込んだプラグインの情報。設定画面で使う。</summary>
/// <param name="SettingsTypes">設定画面に出す設定のクラス。プラグインの ALC の型なので、使い終わったら参照を残さないこと。</param>
public sealed record PluginInfo(string Id, string Name, string Version, IReadOnlyList<ProviderInfo> Providers, IReadOnlyList<Type> SettingsTypes);

/// <summary>プロバイダの情報。</summary>
/// <param name="Name">プロバイダの名前（クラス名）。設定のキーに使う。</param>
/// <param name="DefaultKeyword">プロバイダが宣言したキーワード。</param>
/// <param name="DefaultIncludeInGlobal">プロバイダが宣言した、全体検索に含めるか。</param>
/// <param name="CanIncludeInGlobal">全体検索に参加できるか。ライブクエリ型はグローバルトリガーを宣言したものだけが参加できる。</param>
public sealed record ProviderInfo(string Name, bool IsLiveQuery, string? DefaultKeyword, bool DefaultIncludeInGlobal, bool CanIncludeInGlobal);

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
    private ImmutableArray<PluginInfo> _pluginInfos = [];

    /// <summary>プラグインの設定のセクションのキー（<c>plugins:&lt;id&gt;:settings</c>）。</summary>
    public static string SettingsSection(string pluginId) => $"plugins:{pluginId}:settings";

    /// <summary>読み込んだプラグインの一覧。どのスレッドから読んでもよい。</summary>
    public IReadOnlyList<PluginInfo> Plugins => _pluginInfos;

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
            _pluginInfos = _pluginInfos.Add(CreateInfo(plugin));
            WarnUnsupportedSettings(plugin);
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

    private static PluginInfo CreateInfo(PluginInstance plugin) =>
        new(plugin.Manifest.Id, plugin.Manifest.Name, plugin.Manifest.Version,
            [
                .. plugin.Catalogs.Select(c => new ProviderInfo(
                    c.Registration.ProviderName, false, c.Registration.Keyword, c.Registration.IncludeInGlobal, CanIncludeInGlobal: true)),
                .. plugin.LiveQueries.Select(q => new ProviderInfo(
                    q.ProviderName, true, q.Keyword, q.IncludeInGlobal, CanIncludeInGlobal: q.GlobalTrigger is not null)),
            ],
            plugin.SettingsTypes);

    /// <summary>設定画面に出せない型のプロパティがあれば警告する（要件定義書 9.2、設計 8 章）。</summary>
    private void WarnUnsupportedSettings(PluginInstance plugin)
    {
        foreach (var type in plugin.SettingsTypes)
        {
            try
            {
                SettingsSchema.Describe(type, out var unsupported);
                if (unsupported.Count > 0)
                {
                    logger.LogWarning("{Plugin} の設定 {Type} の次のプロパティは、設定画面に出せない型なので表示しない: {Properties}",
                        plugin.Manifest.Id, type.Name, string.Join("、", unsupported));
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "{Plugin} の設定 {Type} を設定画面に出せない", plugin.Manifest.Id, type.Name);
            }
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
        _pluginInfos = [];
    }
}
