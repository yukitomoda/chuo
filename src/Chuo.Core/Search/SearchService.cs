using System.Diagnostics;
using Chuo.Abstractions;
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

/// <summary>入力に応じて、カタログ型とライブクエリ型のプロバイダから結果を集めて順位を付ける（設計 3 章）。</summary>
public sealed class SearchService(ProviderRegistry registry, IOptionsMonitor<ChuoSettings> settings, ILogger<SearchService> logger)
{
    /// <summary>
    /// 検索する。UI スレッドを止めないよう、処理はスレッドプールで行う。
    /// キーが押されるたびに前の検索をキャンセルすること。
    /// </summary>
    public Task<IReadOnlyList<SearchResult>> SearchAsync(string text, CancellationToken cancellationToken) =>
        Task.Run(() => SearchCoreAsync(text, cancellationToken), cancellationToken);

    private async Task<IReadOnlyList<SearchResult>> SearchCoreAsync(string text, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        var limit = Math.Max(1, settings.CurrentValue.MaxResults);
        var (keyword, body) = SplitKeyword(text);

        IEnumerable<CatalogRegistration> catalogs;
        List<(LiveQueryRegistration Registration, Query Query)> liveQueries;
        string catalogPattern;
        if (keyword is not null)
        {
            catalogs = registry.Catalogs.Where(r => r.Keyword == keyword);
            liveQueries = registry.LiveQueries.Where(r => r.Keyword == keyword).Select(r => (r, new Query(text, body, keyword))).ToList();
            catalogPattern = body;
        }
        else
        {
            catalogs = registry.Catalogs.Where(r => r.IncludeInGlobal);
            liveQueries = registry.LiveQueries
                .Where(r => r.GlobalTrigger?.IsMatch(text) == true)
                .Select(r => (r, new Query(text, text, null)))
                .ToList();
            catalogPattern = text;
        }

        var liveTask = QueryLiveAsync(liveQueries, cancellationToken);
        var catalogResults = SearchCatalogs(catalogs, TextNormalizer.Normalize(catalogPattern.Trim()), limit, cancellationToken);
        var liveResults = await liveTask;

        // ライブクエリの結果は、明示的に呼び出したもの（キーワードかグローバルトリガー）なので上に置く
        return [.. liveResults.Concat(catalogResults).Take(limit)];
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
        var known = registry.Catalogs.Any(r => r.Keyword == candidate) || registry.LiveQueries.Any(r => r.Keyword == candidate);
        return known ? (candidate, text[(space + 1)..]) : (null, text);
    }

    private static List<SearchResult> SearchCatalogs(
        IEnumerable<CatalogRegistration> catalogs, string pattern, int limit, CancellationToken cancellationToken)
    {
        var results = new List<SearchResult>();
        foreach (var registration in catalogs)
        {
            foreach (var item in registration.Catalog.Snapshot())
            {
                if (item.Score(pattern) is { } score)
                {
                    results.Add(new SearchResult(item.Item, registration.PluginId, score));
                }
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
