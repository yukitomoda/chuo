using Chuo.Abstractions;
using Chuo.Core.Text;

namespace Chuo.Core.Search;

/// <summary>カタログ型のプロバイダ1つ分の項目を、検索しやすい形で保持する。</summary>
internal sealed class ProviderCatalog : ICatalog
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, IndexedItem> _items = new(StringComparer.Ordinal);
    private IndexedItem[]? _snapshot;

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _items.Count;
            }
        }
    }

    public void ReplaceAll(IEnumerable<CatalogItem> items)
    {
        var indexed = items.Select(IndexedItem.Create).ToList();
        lock (_gate)
        {
            _items.Clear();
            foreach (var item in indexed)
            {
                _items[item.Item.Id] = item;
            }
            _snapshot = null;
        }
    }

    public void AddOrUpdate(IEnumerable<CatalogItem> items)
    {
        var indexed = items.Select(IndexedItem.Create).ToList();
        lock (_gate)
        {
            foreach (var item in indexed)
            {
                _items[item.Item.Id] = item;
            }
            _snapshot = null;
        }
    }

    public void Remove(IEnumerable<string> ids)
    {
        lock (_gate)
        {
            foreach (var id in ids)
            {
                _items.Remove(id);
            }
            _snapshot = null;
        }
    }

    public IndexedItem? Find(string id)
    {
        lock (_gate)
        {
            return _items.GetValueOrDefault(id);
        }
    }

    /// <summary>検索用の一覧。変更がなければ同じ配列を使い回す。</summary>
    public IndexedItem[] Snapshot()
    {
        lock (_gate)
        {
            return _snapshot ??= [.. _items.Values];
        }
    }
}

/// <summary>検索の対象にする文字列を正規化して持つ項目。</summary>
internal sealed record IndexedItem(CatalogItem Item, string[] SearchTexts)
{
    public static IndexedItem Create(CatalogItem item) =>
        new(item, [TextNormalizer.Normalize(item.Title), .. item.Keywords.Select(TextNormalizer.Normalize)]);

    /// <summary>タイトルとキーワードのうち、最もよく一致したものの点数。</summary>
    public int? Score(string normalizedPattern)
    {
        int? best = null;
        foreach (var text in SearchTexts)
        {
            if (FuzzyMatcher.Score(normalizedPattern, text) is { } score && (best is null || score > best))
            {
                best = score;
            }
        }
        return best;
    }
}
