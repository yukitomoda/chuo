using System.Diagnostics;
using Chuo.Abstractions;
using Chuo.Core.Items;
using Chuo.Core.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Chuo.Core.Search;

/// <summary>検索結果の1件。</summary>
public sealed record SearchResult(ResultItem Item, string PluginId, int Score)
{
    /// <summary>使用履歴やお気に入りで使う、グローバルな識別子（<c>&lt;pluginId&gt;:&lt;itemId&gt;</c>）。</summary>
    public string GlobalId => $"{PluginId}:{Item.Id}";
}

/// <summary>
/// 入力に応じて、カタログ型とライブクエリ型のプロバイダから結果を集めて順位を付ける（設計 3 章）。
/// お気に入りは加点し、無視した項目は除く（要件定義書 6 章）。
/// </summary>
public sealed class SearchService(
    ProviderRegistry registry, IOptionsMonitor<ChuoSettings> settings, ILogger<SearchService> logger, ItemPreferences? preferences = null)
{
    /// <summary>無視した項目の一覧を出すコマンド（要件定義書 6.3）。</summary>
    public const string IgnoredCommand = ":ignored";

    /// <summary>
    /// お気に入りへの加点。連続した一致の減点（最大でおよそ 600）より大きくし、一致の度合いが近ければお気に入りが上に来るようにする。
    /// </summary>
    internal const int FavoriteBonus = 5_000;

    /// <summary>
    /// 検索する。UI スレッドを止めないよう、処理はスレッドプールで行う。
    /// キーが押されるたびに前の検索をキャンセルすること。
    /// </summary>
    public Task<IReadOnlyList<SearchResult>> SearchAsync(string text, CancellationToken cancellationToken) =>
        Task.Run(() => SearchCoreAsync(text, cancellationToken), cancellationToken);

    private async Task<IReadOnlyList<SearchResult>> SearchCoreAsync(string text, CancellationToken cancellationToken)
    {
        var limit = Math.Max(1, settings.CurrentValue.MaxResults);
        var preference = preferences?.Current ?? ItemPreferenceSnapshot.Empty;
        if (string.IsNullOrWhiteSpace(text))
        {
            // 入力が空のときは、お気に入りを指定した順に出す（要件定義書 6.2）
            return [.. preference.FavoritesInOrder
                .Where(id => !preference.IsIgnored(id))
                .Select(Find)
                .OfType<SearchResult>()
                .Take(limit)];
        }
        if (text == IgnoredCommand || text.StartsWith(IgnoredCommand + " ", StringComparison.Ordinal))
        {
            return SearchIgnored(preference, text[IgnoredCommand.Length..].Trim(), limit);
        }

        var (keyword, body) = SplitKeyword(text);

        IEnumerable<CatalogRegistration> catalogs;
        List<(LiveQueryRegistration Registration, Query Query)> liveQueries;
        string catalogPattern;
        if (keyword is not null)
        {
            catalogs = registry.Catalogs.Where(r => registry.KeywordOf(r) == keyword);
            liveQueries = registry.LiveQueries.Where(r => registry.KeywordOf(r) == keyword).Select(r => (r, new Query(text, body, keyword))).ToList();
            catalogPattern = body;
        }
        else
        {
            catalogs = registry.Catalogs.Where(registry.IncludesInGlobal);
            liveQueries = registry.LiveQueries
                .Where(r => registry.IncludesInGlobal(r) && r.GlobalTrigger?.IsMatch(text) == true)
                .Select(r => (r, new Query(text, text, null)))
                .ToList();
            catalogPattern = text;
        }

        var liveTask = QueryLiveAsync(liveQueries, cancellationToken);
        var catalogResults = SearchCatalogs(catalogs, TextNormalizer.Normalize(catalogPattern.Trim()), preference, limit, cancellationToken);
        var liveResults = (await liveTask).Where(r => !preference.IsIgnored(r.GlobalId));

        // ライブクエリの結果は、明示的に呼び出したもの（キーワードかグローバルトリガー）なので上に置く
        return [.. liveResults.Concat(catalogResults).Take(limit)];
    }

    /// <summary>
    /// 無視した項目を出す。カタログにない項目（プラグインを外した、ライブクエリの結果など）は、識別子だけを出す。
    /// </summary>
    private List<SearchResult> SearchIgnored(ItemPreferenceSnapshot preference, string pattern, int limit)
    {
        var normalized = TextNormalizer.Normalize(pattern);
        var results = new List<SearchResult>();
        foreach (var id in preference.Ignored.Order(StringComparer.Ordinal))
        {
            var result = Resolve(id);
            var score = pattern.Length == 0 ? 0 : FuzzyMatcher.Score(normalized, TextNormalizer.Normalize(result.Item.Title));
            if (score is not null)
            {
                results.Add(result with { Score = score.Value });
            }
        }
        return [.. results.OrderByDescending(r => r.Score).ThenBy(r => r.Item.Title, StringComparer.CurrentCulture).Take(limit)];
    }

    /// <summary>グローバルな識別子から項目を探す。カタログにない項目は、識別子をタイトルにした項目を返す。</summary>
    public SearchResult Resolve(string globalId) => Find(globalId) ?? Unresolved(globalId);

    private static SearchResult Unresolved(string globalId)
    {
        var separator = globalId.IndexOf(':');
        var pluginId = separator < 0 ? "" : globalId[..separator];
        var item = new ResultItem { Id = globalId[(separator + 1)..], Title = globalId, Subtitle = "今は見つからない項目", Actions = [] };
        return new SearchResult(item, pluginId, 0);
    }

    /// <summary>グローバルな識別子から、カタログの項目を探す。</summary>
    private SearchResult? Find(string globalId)
    {
        foreach (var registration in registry.Catalogs)
        {
            var prefix = registration.PluginId + ":";
            if (globalId.StartsWith(prefix, StringComparison.Ordinal) && registration.Catalog.Find(globalId[prefix.Length..]) is { } item)
            {
                return new SearchResult(item.Item, registration.PluginId, 0);
            }
        }
        return null;
    }

    /// <summary>先頭の語が登録済みのキーワードで、その後に空白があればキーワードとして扱う（例: <c>g rust</c>）。</summary>
    private (string? Keyword, string Body) SplitKeyword(string text)
    {
        var space = text.IndexOf(' ');
        if (space <= 0)
        {
            return (null, text);
        }

        var candidate = text[..space];
        var known = registry.Catalogs.Any(r => registry.KeywordOf(r) == candidate)
            || registry.LiveQueries.Any(r => registry.KeywordOf(r) == candidate);
        return known ? (candidate, text[(space + 1)..]) : (null, text);
    }

    private static List<SearchResult> SearchCatalogs(
        IEnumerable<CatalogRegistration> catalogs, string pattern, ItemPreferenceSnapshot preference, int limit, CancellationToken cancellationToken)
    {
        var results = new List<SearchResult>();
        foreach (var registration in catalogs)
        {
            foreach (var item in registration.Catalog.Snapshot())
            {
                if (item.Score(pattern) is not { } score)
                {
                    continue;
                }
                var result = new SearchResult(item.Item, registration.PluginId, score);
                if (preference.IsIgnored(result.GlobalId))
                {
                    continue;
                }
                results.Add(preference.IsFavorite(result.GlobalId) ? result with { Score = score + FavoriteBonus } : result);
            }
            cancellationToken.ThrowIfCancellationRequested();
        }

        return results
            .OrderByDescending(r => r.Score)
            .ThenBy(r => r.Item.Title, StringComparer.CurrentCulture)
            .Take(limit)
            .ToList();
    }

    private async Task<List<SearchResult>> QueryLiveAsync(
        List<(LiveQueryRegistration Registration, Query Query)> queries, CancellationToken cancellationToken)
    {
        if (queries.Count == 0)
        {
            return [];
        }

        var tasks = queries.Select(q => QueryOneAsync(q.Registration, q.Query, cancellationToken)).ToList();
        var results = new List<SearchResult>();
        foreach (var items in await Task.WhenAll(tasks))
        {
            results.AddRange(items);
        }
        return results;
    }

    private async Task<IEnumerable<SearchResult>> QueryOneAsync(LiveQueryRegistration registration, Query query, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(registration.Timeout);
        var start = Stopwatch.GetTimestamp();
        try
        {
            // プラグインが同期的に重い処理をしても検索全体を止めないよう、別のタスクで呼ぶ
            var items = await Task.Run(() => registration.Provider.QueryAsync(query, timeout.Token).AsTask(), timeout.Token);
            return items.Select((item, i) => new SearchResult(item, registration.PluginId, -i));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("{Plugin}/{Provider} が {Timeout}ms 以内に結果を返さなかった",
                registration.PluginId, registration.ProviderName, registration.Timeout.TotalMilliseconds);
            return [];
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "{Plugin}/{Provider} の検索で例外が発生した（{Elapsed}ms）",
                registration.PluginId, registration.ProviderName, Stopwatch.GetElapsedTime(start).TotalMilliseconds);
            return [];
        }
    }
}
