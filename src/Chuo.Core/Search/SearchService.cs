using System.Collections.Immutable;
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

    /// <summary>Tab で補完したときの入力欄の文字列。項目の補完に、検索で使ったキーワードを前に付けたもの。</summary>
    public string? Completion { get; init; }

    /// <summary>
    /// 入力欄に薄く表示する補完の続き。補完が入力で始まっていなければ（大文字と小文字は区別しない）null。
    /// </summary>
    public string? CompletionSuffix(string input) =>
        Completion is { } completion && completion.Length > input.Length && completion.StartsWith(input, StringComparison.OrdinalIgnoreCase)
            ? completion[input.Length..]
            : null;
}

/// <summary>
/// 入力に応じて、カタログ型とライブクエリ型のプロバイダから結果を集めて順位を付ける（設計 3 章）。
/// エイリアスとお気に入りは加点し、無視した項目は除く（要件定義書 3.2、6 章）。
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

    /// <summary>エイリアスの前方一致への加点。お気に入りより大きくする（要件定義書 3.2）。</summary>
    internal const int AliasPrefixBonus = 50_000;

    /// <summary>エイリアスの完全一致の点。これ以上の点の項目は、ライブクエリの結果より上に固定する。</summary>
    internal const int AliasExactScore = 1_000_000;

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
        List<(LiveQueryRegistration Registration, Query Query, string CompletionPrefix)> liveQueries;
        string catalogPattern;
        if (keyword is not null)
        {
            catalogs = registry.Catalogs.Where(r => registry.KeywordOf(r) == keyword);
            liveQueries = registry.LiveQueries.Where(r => registry.KeywordOf(r) == keyword).Select(r => (r, new Query(text, body, keyword), text[..^body.Length])).ToList();
            catalogPattern = body;
        }
        else
        {
            catalogs = registry.Catalogs.Where(registry.IncludesInGlobal);
            // 入力がキーワードそのもの（空白なし）なら、そのライブクエリも空の検索で呼び、最初の一覧を混ぜる（要件定義書 2.1）
            liveQueries = [.. registry.LiveQueries
                .Select(r => registry.KeywordOf(r) == text
                    ? (r, new Query(text, "", text), text + " ")
                    : registry.IncludesInGlobal(r) && r.GlobalTrigger?.IsMatch(text) == true
                        ? (r, new Query(text, text, null), "")
                        : default((LiveQueryRegistration, Query, string)?))
                .OfType<(LiveQueryRegistration, Query, string)>()];
            catalogPattern = text;
        }

        var liveTask = QueryLiveAsync(liveQueries, cancellationToken);
        // 補完はキーワードを含まないので、入力欄の文字列にするためにキーワードの部分を前に付ける
        var completionPrefix = text[..^catalogPattern.Length];
        var catalogResults = SearchCatalogs(catalogs, TextNormalizer.Normalize(catalogPattern.Trim()), preference, limit, cancellationToken)
            .Select(r => WithCompletion(r, completionPrefix))
            .ToList();
        var liveResults = (await liveTask).Where(r => !preference.IsIgnored(r.GlobalId));

        // エイリアスの完全一致は最上位に固定する。ライブクエリの結果は、明示的に呼び出したもの（キーワードかグローバルトリガー）なので
        // その次に置く
        return [.. catalogResults.TakeWhile(r => r.Score >= AliasExactScore)
            .Concat(liveResults)
            .Concat(catalogResults.SkipWhile(r => r.Score >= AliasExactScore))
            .Take(limit)];
    }

    private static SearchResult WithCompletion(SearchResult result, string prefix) =>
        (result.Completion ?? result.Item.Completion) is { } completion ? result with { Completion = prefix + completion } : result;

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
                var globalId = $"{registration.PluginId}:{item.Item.Id}";
                var alias = MatchAlias(preference.AliasesOf(globalId), pattern);
                if (item.Score(pattern) is not { } score)
                {
                    if (alias is null)
                    {
                        continue;
                    }
                    score = 0;
                }
                if (preference.IsIgnored(globalId))
                {
                    continue;
                }
                if (alias is not null)
                {
                    // 前方一致は、補った文字が少ないエイリアスほど上にする。補完にはエイリアスを出す
                    score = alias.Normalized == pattern ? AliasExactScore : AliasPrefixBonus + score - (alias.Normalized.Length - pattern.Length);
                }
                if (preference.IsFavorite(globalId))
                {
                    score += FavoriteBonus;
                }
                results.Add(new SearchResult(item.Item, registration.PluginId, score) { Completion = alias?.Text });
            }
            cancellationToken.ThrowIfCancellationRequested();
        }

        return results
            .OrderByDescending(r => r.Score)
            .ThenBy(r => r.Item.Title, StringComparer.CurrentCulture)
            .Take(limit)
            .ToList();
    }

    /// <summary>入力と完全に一致するエイリアス、なければ入力で始まる最も短いエイリアス。</summary>
    private static ItemAlias? MatchAlias(ImmutableArray<ItemAlias> aliases, string pattern)
    {
        if (aliases.IsDefaultOrEmpty || pattern.Length == 0)
        {
            return null;
        }
        ItemAlias? best = null;
        foreach (var alias in aliases)
        {
            if (alias.Normalized == pattern)
            {
                return alias;
            }
            if (alias.Normalized.StartsWith(pattern, StringComparison.Ordinal) && (best is null || alias.Normalized.Length < best.Normalized.Length))
            {
                best = alias;
            }
        }
        return best;
    }

    private async Task<List<SearchResult>> QueryLiveAsync(
        List<(LiveQueryRegistration Registration, Query Query, string CompletionPrefix)> queries, CancellationToken cancellationToken)
    {
        if (queries.Count == 0)
        {
            return [];
        }

        var tasks = queries.Select(q => QueryOneAsync(q.Registration, q.Query, q.CompletionPrefix, cancellationToken)).ToList();
        var results = new List<SearchResult>();
        foreach (var items in await Task.WhenAll(tasks))
        {
            results.AddRange(items);
        }
        return results;
    }

    private async Task<IEnumerable<SearchResult>> QueryOneAsync(
        LiveQueryRegistration registration, Query query, string completionPrefix, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(registration.Timeout);
        var start = Stopwatch.GetTimestamp();
        try
        {
            // プラグインが同期的に重い処理をしても検索全体を止めないよう、別のタスクで呼ぶ
            var items = await Task.Run(() => registration.Provider.QueryAsync(query, timeout.Token).AsTask(), timeout.Token);
            return items.Select((item, i) => WithCompletion(new SearchResult(item, registration.PluginId, -i), completionPrefix));
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
