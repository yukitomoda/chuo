using Chuo.Abstractions;
using System.Runtime.Versioning;
using Chuo.Plugins.Shared;

namespace Chuo.Plugins.Apps;

/// <summary>
/// Windows のスタートメニューのアプリを列挙する（設計 4.5）。
/// ショートカット（.lnk と .url）と、ショートカットを持たないパッケージのアプリ（Microsoft Store のアプリなど）。
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class StartMenuSource : IAppSource
{
    private static readonly EnumerationOptions Enumeration = new() { RecurseSubdirectories = true, IgnoreInaccessible = true };

    /// <summary>ユーザーのものを優先するので、ユーザーのスタートメニューを先に並べる。</summary>
    private readonly IReadOnlyList<string> _shortcutRoots =
        new[] { Environment.SpecialFolder.Programs, Environment.SpecialFolder.CommonPrograms }
            .Select(Environment.GetFolderPath)
            .Where(path => path.Length > 0)
            .ToList();

    public IReadOnlyList<WatchedFolder> Roots =>
    [
        .. _shortcutRoots.Select(root => new WatchedFolder(root)),
        // パッケージをユーザーに登録すると、ここにパッケージのフォルダができる。中はアプリのデータなので直下だけを監視する
        new WatchedFolder(PackagedApps.DataRoot, IncludeSubdirectories: false),
    ];

    public IReadOnlyList<CatalogItem> Scan() =>
    [
        .. ShortcutDeduplication.Distinct(
                _shortcutRoots
                    .Where(Directory.Exists)
                    .SelectMany(root => Directory.EnumerateFiles(root, "*", Enumeration))
                    .Where(IsShortcut)
                    .Select(path => (path, IsLink(path) ? ShellLink.TryRead(path) : null)))
            .Select(shortcut => CreateItem(shortcut.Path, shortcut.Link)),
        .. PackagedApps.Enumerate().Select(CreatePackagedItem),
    ];

    public static bool IsShortcut(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is ".lnk" or ".url";

    private static bool IsLink(string path) => Path.GetExtension(path).Equals(".lnk", StringComparison.OrdinalIgnoreCase);

    private static CatalogItem CreateItem(string path, ShellLinkInfo? link)
    {
        var title = Path.GetFileNameWithoutExtension(path);
        var target = link?.TargetPath;

        // 実行ファイルの名前（例: Visual Studio Code の code）でも見つかるようにする
        var keywords = new List<string>();
        if (target is not null && Path.GetFileNameWithoutExtension(target) is { Length: > 0 } fileName
            && !title.Contains(fileName, StringComparison.OrdinalIgnoreCase))
        {
            keywords.Add(fileName);
        }

        List<ItemAction> actions =
        [
            ItemAction.Open(path, "起動"),
            ItemAction.ShowInFolder(target is not null && File.Exists(target) ? target : path, "ファイルの場所を開く"),
        ];
        if (Elevation.CanRunAsAdministrator(path, target))
        {
            actions.Add(ItemAction.RunAsAdministrator(path));
        }

        return new CatalogItem
        {
            Id = path,
            Title = title,
            Subtitle = target ?? path,
            Icon = IconSource.FromFile(path),
            Keywords = keywords,
            Actions = actions,
        };
    }

    private static CatalogItem CreatePackagedItem(PackagedApp app)
    {
        var location = PackagedApps.ShellPath(app.AppUserModelId);
        return new CatalogItem
        {
            Id = app.AppUserModelId,
            Title = app.Name,
            Subtitle = app.PackageName,
            Icon = IconSource.FromFile(location),
            Actions = [ItemAction.Open(location, "起動")],
        };
    }
}

/// <summary>
/// 同じアプリを指すショートカットを1つにまとめる（設計 4.5）。ユーザーと全ユーザーのスタートメニューの両方に
/// 同じショートカットがあると、同じアプリが2件出るため。
/// </summary>
internal static class ShortcutDeduplication
{
    /// <summary>
    /// リンク先と引数が同じショートカットは、先にあるものだけを残す。
    /// リンク先の読めないショートカットと .url は、何を開くか分からないのでまとめない。
    /// </summary>
    public static IEnumerable<(string Path, ShellLinkInfo? Link)> Distinct(IEnumerable<(string Path, ShellLinkInfo? Link)> shortcuts)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var shortcut in shortcuts)
        {
            if (shortcut.Link is { TargetPath: { } target } link && !seen.Add(target + "\0" + link.Arguments))
            {
                continue;
            }
            yield return shortcut;
        }
    }
}
