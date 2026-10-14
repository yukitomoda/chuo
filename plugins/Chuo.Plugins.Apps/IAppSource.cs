using Chuo.Abstractions;

namespace Chuo.Plugins.Apps;

/// <summary>OS ごとのアプリの一覧の取得元。</summary>
internal interface IAppSource
{
    /// <summary>アプリの一覧を置いているフォルダ。変更を監視し、変わったら一覧を作り直す。</summary>
    IReadOnlyList<WatchedFolder> Roots { get; }

    IReadOnlyList<CatalogItem> Scan();
}

/// <summary>変更を監視するフォルダ。</summary>
/// <param name="IncludeSubdirectories">false なら、直下のフォルダの追加と削除だけを監視する。</param>
internal sealed record WatchedFolder(string Path, bool IncludeSubdirectories = true);
