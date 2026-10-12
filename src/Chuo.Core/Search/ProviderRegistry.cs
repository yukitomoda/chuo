using System.Collections.Immutable;
using System.Text.RegularExpressions;
using Chuo.Abstractions;
using Microsoft.Extensions.Configuration;

namespace Chuo.Core.Search;

/// <summary>プロバイダの登録内容のうち、カタログ型とライブクエリ型に共通のもの。</summary>
internal interface IProviderRegistration
{
    string PluginId { get; }

    string ProviderName { get; }

    /// <summary>プロバイダが宣言したキーワード（既定値）。</summary>
    string? Keyword { get; }

    /// <summary>プロバイダが宣言した、全体検索に含めるか（既定値）。</summary>
    bool IncludeInGlobal { get; }
}

/// <summary>カタログ型のプロバイダの登録内容。</summary>
internal sealed record CatalogRegistration(string PluginId, string ProviderName, ProviderCatalog Catalog, string? Keyword, bool IncludeInGlobal)
    : IProviderRegistration;

/// <summary>ライブクエリ型のプロバイダの登録内容。グローバルトリガーがあるものだけが全体検索に参加できる。</summary>
internal sealed record LiveQueryRegistration(
    string PluginId, string ProviderName, ILiveQueryProvider Provider, string? Keyword, Regex? GlobalTrigger, TimeSpan Timeout)
    : IProviderRegistration
{
    public bool IncludeInGlobal => GlobalTrigger is not null;
}

/// <summary>
/// 読み込んだプラグインのプロバイダの一覧。プラグインの読み込みと検索が別のスレッドから使う。
/// キーワードと全体検索に含めるかは、プロバイダの宣言を既定値とし、<c>settings.json</c> の
/// <c>plugins.&lt;id&gt;.providers.&lt;プロバイダ名&gt;</c> で上書きできる（要件定義書 2.1）。
/// </summary>
public sealed class ProviderRegistry(IConfiguration? configuration = null)
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

    /// <summary>設定で上書きしたものを含めた、実際のキーワード。空文字列で上書きすると、キーワードなしになる。</summary>
    internal string? KeywordOf(IProviderRegistration registration)
    {
        var configured = configuration?[ProviderSettingKey(registration.PluginId, registration.ProviderName, KeywordKey)];
        return configured is null ? registration.Keyword : configured.Trim() is { Length: > 0 } keyword ? keyword : null;
    }

    /// <summary>設定で上書きしたものを含めた、全体検索に含めるか。</summary>
    internal bool IncludesInGlobal(IProviderRegistration registration) =>
        configuration?[ProviderSettingKey(registration.PluginId, registration.ProviderName, IncludeInGlobalKey)] is { } configured
        && bool.TryParse(configured, out var value)
            ? value
            : registration.IncludeInGlobal;

    public const string KeywordKey = "keyword";

    public const string IncludeInGlobalKey = "includeInGlobal";

    /// <summary>プロバイダの設定のキー（例: <c>plugins:chuo.apps:providers:AppCatalogProvider:keyword</c>）。</summary>
    public static string ProviderSettingKey(string pluginId, string providerName, string name) =>
        $"plugins:{pluginId}:providers:{providerName}:{name}";

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
