namespace Chuo.Abstractions;

/// <summary>
/// カタログ型のプロバイダ。項目をホストに登録するだけで、検索はホストがメモリ上のインデックスに対して行う。
/// </summary>
public interface ICatalogProvider
{
    /// <summary>
    /// 起動時に一度呼ばれる。項目を <paramref name="catalog"/> に登録する。
    /// <paramref name="catalog"/> は保持しておき、後から項目が変わったとき（ファイルの変更の監視など）にも使ってよい。
    /// </summary>
    Task InitializeAsync(ICatalog catalog, CancellationToken cancellationToken);
}

/// <summary>カタログ型のプロバイダが項目を登録する先。どのスレッドから呼んでもよい。</summary>
public interface ICatalog
{
    /// <summary>このプロバイダの項目をすべて置き換える。</summary>
    void ReplaceAll(IEnumerable<CatalogItem> items);

    /// <summary>項目を追加する。同じ ID の項目があれば置き換える。</summary>
    void AddOrUpdate(IEnumerable<CatalogItem> items);

    /// <summary>指定した ID の項目を削除する。</summary>
    void Remove(IEnumerable<string> ids);
}

/// <summary>ライブクエリ型のプロバイダ。入力に応じて結果を作る。</summary>
public interface ILiveQueryProvider
{
    /// <summary>
    /// 入力に応じた結果を返す。キーが押されるたびに前の呼び出しはキャンセルされるので、
    /// 時間のかかる処理では <paramref name="cancellationToken"/> を確認すること。
    /// </summary>
    ValueTask<IReadOnlyList<ResultItem>> QueryAsync(Query query, CancellationToken cancellationToken);
}

/// <summary>ライブクエリ型のプロバイダに渡す入力。</summary>
/// <param name="RawText">入力欄の文字列そのもの。</param>
/// <param name="Text">キーワードを除いた検索の文字列。</param>
/// <param name="Keyword">キーワードで呼び出されたときはそのキーワード。グローバルトリガーで呼び出されたときは null。</param>
public sealed record Query(string RawText, string Text, string? Keyword);
