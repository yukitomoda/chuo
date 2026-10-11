using Chuo.Abstractions;

namespace Chuo.Plugins.Apps;

/// <summary>OS ごとのアプリの一覧の取得元。</summary>
internal interface IAppSource
{
    /// <summary>アプリの一覧を置いているフォルダ。変更を監視し、変わったら一覧を作り直す。</summary>
    IReadOnlyList<string> Roots { get; }

    IReadOnlyList<CatalogItem> Scan();
}
