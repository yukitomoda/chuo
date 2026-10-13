using Chuo.Abstractions;
using Chuo.Plugins.Shared;

namespace Chuo.Plugins.Apps;

/// <summary>Windows のスタートメニューのショートカット（.lnk と .url）からアプリを列挙する（設計 4.5）。</summary>
internal sealed class StartMenuSource : IAppSource
{
    private static readonly EnumerationOptions Enumeration = new() { RecurseSubdirectories = true, IgnoreInaccessible = true };

    public IReadOnlyList<string> Roots { get; } =
        new[] { Environment.SpecialFolder.Programs, Environment.SpecialFolder.CommonPrograms }
            .Select(Environment.GetFolderPath)
            .Where(path => path.Length > 0)
            .ToList();

    public IReadOnlyList<CatalogItem> Scan() =>
        Roots
            .Where(Directory.Exists)
            .SelectMany(root => Directory.EnumerateFiles(root, "*", Enumeration))
            .Where(IsShortcut)
            .Select(CreateItem)
            .ToList();

    public static bool IsShortcut(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is ".lnk" or ".url";

    private static CatalogItem CreateItem(string path)
    {
        var title = Path.GetFileNameWithoutExtension(path);
        var target = Path.GetExtension(path).Equals(".lnk", StringComparison.OrdinalIgnoreCase)
            ? ShellLink.TryRead(path)?.TargetPath
            : null;

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
}
