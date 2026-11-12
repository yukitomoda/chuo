using Chuo.Abstractions;

namespace Chuo.Plugins.Apps;

/// <summary>OS ごとのアプリの一覧の取得元。</summary>
internal interface IAppSource
{
    /// <summary>アプリの一覧を置いているフォルダ。変更を監視し、変わったら一覧を作り直す。</summary>
    IReadOnlyList<WatchedFolder> Roots { get; }

    /// <summary>アプリを集めて一覧を作る。時間がかかる（数秒〜十数秒）。</summary>
    IReadOnlyList<CatalogItem> Scan();

    /// <summary>
    /// 前回 <see cref="Scan"/> で集めたものから、すぐに一覧を作る。起動の直後に、作り直しを待たずに検索できるようにするため。
    /// 前回のものがない、または対応しない取得元なら null。
    /// </summary>
    IReadOnlyList<CatalogItem>? LoadCached();
}

/// <summary>変更を監視するフォルダ。</summary>
/// <param name="IncludeSubdirectories">false なら、直下のフォルダの追加と削除だけを監視する。</param>
internal sealed record WatchedFolder(string Path, bool IncludeSubdirectories = true);
