using System.Collections.Immutable;
using System.Diagnostics;
using Chuo.Core.Search;
using Chuo.Core.Settings;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;

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

/// <summary>見つけたプラグインの情報。設定画面で使う。</summary>
/// <param name="IsEnabled">有効か。無効なプラグインは読み込まないので、<paramref name="Providers"/> と <paramref name="SettingsTypes"/> は空。</param>
/// <param name="SettingsTypes">設定画面に出す設定のクラス。プラグインの ALC の型なので、使い終わったら参照を残さないこと。</param>
/// <param name="LoadError">有効だが読み込めなかったときの理由。</param>
public sealed record PluginInfo(
    string Id, string Name, string Version, bool IsEnabled, IReadOnlyList<ProviderInfo> Providers, IReadOnlyList<Type> SettingsTypes,
    string? LoadError = null);

/// <summary>プロバイダの情報。</summary>
/// <param name="Name">プロバイダの名前（クラス名）。設定のキーに使う。</param>
/// <param name="DefaultKeyword">プロバイダが宣言したキーワード。</param>
/// <param name="DefaultIncludeInGlobal">プロバイダが宣言した、全体検索に含めるか。</param>
/// <param name="CanIncludeInGlobal">全体検索に参加できるか。ライブクエリ型はグローバルトリガーを宣言したものだけが参加できる。</param>
public sealed record ProviderInfo(string Name, bool IsLiveQuery, string? DefaultKeyword, bool DefaultIncludeInGlobal, bool CanIncludeInGlobal);

/// <summary>
/// プラグインの読み込み、開始、アンロードを行う。本体の汎用ホストのホステッドサービスとして動く。
/// 無効にしたプラグインは読み込まない。設定で有効と無効を切り替えると、その場で読み込みかアンロードをする（要件定義書 9.1）。
/// </summary>
public sealed class PluginManager(
    ProviderRegistry registry,
    IServiceProvider services,
    IConfiguration configuration,
    IOptions<PluginManagerOptions> options,
    ILogger<PluginManager> logger) : IHostedService
{
    /// <summary>見つけたプラグイン。読み込み、アンロード、一覧の更新は <see cref="_gate"/> の中で行う。</summary>
    private readonly List<DiscoveredPlugin> _discovered = [];
    private readonly SemaphoreSlim _gate = new(1, 1);
    private ImmutableArray<PluginInfo> _pluginInfos = [];
    private IDisposable? _configurationChange;
    private bool _stopped;

    /// <summary>プラグインの設定のセクションのキー（<c>plugins:&lt;id&gt;:settings</c>）。</summary>
    public static string SettingsSection(string pluginId) => $"plugins:{pluginId}:settings";

    /// <summary>プラグインを有効にするかのキー（<c>plugins:&lt;id&gt;:enabled</c>）。値がなければ有効。</summary>
    public static string EnabledKey(string pluginId) => $"plugins:{pluginId}:enabled";

    /// <summary>見つけたプラグインの一覧（無効なものを含む）。どのスレッドから読んでもよい。</summary>
    public IReadOnlyList<PluginInfo> Plugins => _pluginInfos;

    /// <summary>プラグインを読み込んだか、アンロードしたとき。どのスレッドからも発生する。</summary>
    public event Action? PluginsChanged;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            foreach (var directory in options.Value.BundledDirectories.Where(Directory.Exists).SelectMany(Directory.EnumerateDirectories))
            {
                Discover(directory);
            }
            foreach (var plugin in _discovered)
            {
                plugin.Enabled = IsEnabled(plugin.Manifest.Id);
                if (plugin.Enabled)
                {
                    await LoadAsync(plugin, cancellationToken);
                }
            }
            UpdateInfos();
        }
        finally
        {
            _gate.Release();
        }
        _configurationChange = ChangeToken.OnChange(configuration.GetReloadToken, () => _ = ApplyEnabledAsync());
    }

    /// <summary>設定の有効と無効に合わせて、プラグインを読み込むかアンロードする。</summary>
    internal async Task ApplyEnabledAsync()
    {
        var changed = false;
        await _gate.WaitAsync();
        try
        {
            if (_stopped)
            {
                return;
            }
            foreach (var plugin in _discovered)
            {
                // 設定を書き込むたびに呼ばれるので、有効と無効が変わったものだけを扱う（読み込みに失敗したものを繰り返さない）
                var enabled = IsEnabled(plugin.Manifest.Id);
                if (enabled == plugin.Enabled)
                {
                    continue;
                }
                plugin.Enabled = enabled;
                changed = true;
                if (enabled)
                {
                    logger.LogInformation("{Plugin} を有効にした", plugin.Manifest.Id);
                    await LoadAsync(plugin, CancellationToken.None);
                }
                else
                {
                    logger.LogInformation("{Plugin} を無効にした", plugin.Manifest.Id);
                    await UnloadAsync(plugin, CancellationToken.None);
                }
            }
            if (changed)
            {
                UpdateInfos();
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "プラグインの有効と無効を反映できなかった");
        }
        finally
        {
            _gate.Release();
        }
        if (changed)
        {
            PluginsChanged?.Invoke();
        }
    }

    /// <summary>値が読めなければ有効とする。</summary>
    private bool IsEnabled(string pluginId) =>
        !bool.TryParse(configuration[EnabledKey(pluginId)], out var enabled) || enabled;

    /// <summary>plugin.json を読み、読み込めるプラグインなら一覧に加える。コードは読み込まない。</summary>
    private void Discover(string directory)
    {
        if (!File.Exists(Path.Combine(directory, PluginManifest.FileName)))
        {
            return;
        }
        try
        {
            var manifest = PluginManifest.Load(directory);
            if (!manifest.IsCompatible())
            {
                logger.LogWarning("{Plugin} は hostApi {Required} を要求しており、このホスト（{Host}）では読み込めない",
                    manifest.Id, manifest.HostApi, Abstractions.HostApi.Version);
                return;
            }
            if (_discovered.Any(p => p.Manifest.Id == manifest.Id))
            {
                logger.LogWarning("{Plugin} は見つけ済みなので、{Directory} は無視する", manifest.Id, directory);
                return;
            }
            _discovered.Add(new DiscoveredPlugin(manifest, directory));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "{Directory} の {File} を読めなかった", directory, PluginManifest.FileName);
        }
    }

    private void UpdateInfos() => _pluginInfos = [.. _discovered.Select(CreateInfo)];

    private async Task LoadAsync(DiscoveredPlugin discovered, CancellationToken cancellationToken)
    {
        var manifest = discovered.Manifest;
        var start = Stopwatch.GetTimestamp();
        try
        {
            discovered.LoadError = null;
            var plugin = await PluginInstance.LoadAsync(manifest, discovered.Directory, services, configuration, cancellationToken);
            discovered.Instance = plugin;
            WarnUnsupportedSettings(plugin);
            foreach (var registration in plugin.LiveQueries)
            {
                registry.Add(registration);
            }
            foreach (var (registration, provider) in plugin.Catalogs)
            {
                registry.Add(registration);
                // 項目の列挙に時間がかかっても起動を止めないよう、待たずに進む
                _ = InitializeCatalogAsync(registration, provider, plugin.Stopping);
            }

            logger.LogInformation("{Plugin} {Version} を読み込んだ（{Elapsed:F1}ms、カタログ {Catalogs} 件、ライブクエリ {LiveQueries} 件）",
                manifest.Id, manifest.Version, Stopwatch.GetElapsedTime(start).TotalMilliseconds, plugin.Catalogs.Count, plugin.LiveQueries.Count);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "{Directory} のプラグインを読み込めなかった", discovered.Directory);
            discovered.LoadError = ex.Message;
        }
    }

    private async Task UnloadAsync(DiscoveredPlugin discovered, CancellationToken cancellationToken)
    {
        if (discovered.Instance is not { } plugin)
        {
            return;
        }
        discovered.Instance = null;
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

    private static PluginInfo CreateInfo(DiscoveredPlugin discovered) =>
        discovered.Instance is { } plugin
            ? CreateInfo(plugin)
            : new(discovered.Manifest.Id, discovered.Manifest.Name, discovered.Manifest.Version, discovered.Enabled, [], [], discovered.LoadError);

    private static PluginInfo CreateInfo(PluginInstance plugin) =>
        new(plugin.Manifest.Id, plugin.Manifest.Name, plugin.Manifest.Version, IsEnabled: true,
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

    private async Task InitializeCatalogAsync(
        CatalogRegistration registration, Abstractions.ICatalogProvider provider, CancellationToken stopping)
    {
        var start = Stopwatch.GetTimestamp();
        try
        {
            await Task.Run(() => provider.InitializeAsync(registration.Catalog, stopping));
            logger.LogInformation("{Plugin}/{Provider} の項目を登録した（{Count} 件、{Elapsed:F1}ms）",
                registration.PluginId, registration.ProviderName, registration.Catalog.Count, Stopwatch.GetElapsedTime(start).TotalMilliseconds);
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "{Plugin}/{Provider} の項目の登録に失敗した", registration.PluginId, registration.ProviderName);
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _configurationChange?.Dispose();
        await _gate.WaitAsync(cancellationToken);
        try
        {
            _stopped = true;
            foreach (var plugin in _discovered)
            {
                await UnloadAsync(plugin, cancellationToken);
            }
            _discovered.Clear();
            _pluginInfos = [];
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>見つけたプラグインと、読み込んでいればそのインスタンス。</summary>
    private sealed class DiscoveredPlugin(PluginManifest manifest, string directory)
    {
        public PluginManifest Manifest { get; } = manifest;

        public string Directory { get; } = directory;

        /// <summary>最後に反映した、有効か無効か。</summary>
        public bool Enabled { get; set; }

        public PluginInstance? Instance { get; set; }

        public string? LoadError { get; set; }
    }
}
