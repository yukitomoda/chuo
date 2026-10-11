using System.Collections.Immutable;
using System.Text.RegularExpressions;
using Chuo.Abstractions;

namespace Chuo.Core.Search;

/// <summary>カタログ型のプロバイダの登録内容。</summary>
internal sealed record CatalogRegistration(string PluginId, string ProviderName, ProviderCatalog Catalog, string? Keyword, bool IncludeInGlobal);

/// <summary>ライブクエリ型のプロバイダの登録内容。</summary>
internal sealed record LiveQueryRegistration(
    string PluginId, string ProviderName, ILiveQueryProvider Provider, string? Keyword, Regex? GlobalTrigger, TimeSpan Timeout);

/// <summary>読み込んだプラグインのプロバイダの一覧。プラグインの読み込みと検索が別のスレッドから使う。</summary>
public sealed class ProviderRegistry
{
    private readonly Lock _gate = new();
    private ImmutableArray<CatalogRegistration> _catalogs = [];
    private ImmutableArray<LiveQueryRegistration> _liveQueries = [];

    internal ImmutableArray<CatalogRegistration> Catalogs => _catalogs;

    internal ImmutableArray<LiveQueryRegistration> LiveQueries => _liveQueries;

    internal void Add(CatalogRegistration registration)
    {
        lock (_gate)
        {
            _catalogs = _catalogs.Add(registration);
        }
    }

    internal void Add(LiveQueryRegistration registration)
    {
        lock (_gate)
        {
            _liveQueries = _liveQueries.Add(registration);
        }
    }

    /// <summary>プラグインのプロバイダをすべて取り除く（アンロードの前に呼ぶ）。</summary>
    internal void RemovePlugin(string pluginId)
    {
        lock (_gate)
        {
            _catalogs = _catalogs.RemoveAll(r => r.PluginId == pluginId);
            _liveQueries = _liveQueries.RemoveAll(r => r.PluginId == pluginId);
        }
    }
}
