using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text.Json;
using Chuo.Abstractions;
using Chuo.Plugins.Shared;
using Microsoft.Extensions.Logging;

namespace Chuo.Plugins.Apps;

/// <summary>
/// Windows のスタートメニューのアプリを列挙する（設計 4.5）。
/// ショートカット（.lnk と .url）と、ショートカットを持たないパッケージのアプリ（Microsoft Store のアプリなど）。
/// 集めたもの（<see cref="StartMenuSnapshot"/>）は <paramref name="cachePath"/> に保存し、次の起動ではまずそこから一覧を作る。
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class StartMenuSource(string cachePath, ILogger logger) : IAppSource
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

    public IReadOnlyList<CatalogItem> Scan()
    {
        var snapshot = Collect();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
            File.WriteAllText(cachePath, JsonSerializer.Serialize(snapshot));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "アプリの一覧を保存できなかった");
        }
        return CreateItems(snapshot);
    }

    public IReadOnlyList<CatalogItem>? LoadCached()
    {
        try
        {
            return File.Exists(cachePath) && JsonSerializer.Deserialize<StartMenuSnapshot>(File.ReadAllText(cachePath)) is { } snapshot
                ? CreateItems(snapshot)
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            logger.LogWarning(ex, "前回のアプリの一覧を読めなかった");
            return null;
        }
    }

    /// <summary>
    /// ショートカットとパッケージのアプリを集める。パッケージのアプリの列挙（シェルの COM）は遅いことがあるので、
    /// ショートカットを読むのと並行して行う。
    /// </summary>
    private StartMenuSnapshot Collect()
    {
        var packagedTask = Task.Run(() =>
        {
            var start = Stopwatch.GetTimestamp();
            var apps = PackagedApps.Enumerate();
            return (Apps: apps, Elapsed: Stopwatch.GetElapsedTime(start));
        });

        var shortcutStart = Stopwatch.GetTimestamp();
        var shortcuts = ShortcutDeduplication.Distinct(
                _shortcutRoots
                    .Where(Directory.Exists)
                    .SelectMany(root => Directory.EnumerateFiles(root, "*", Enumeration))
                    .Where(IsShortcut)
                    .ToList()
                    // サインインの直後はディスクが遅いので、ショートカットは並行して読む。重複をまとめる順序は保つ
                    .AsParallel().AsOrdered().WithDegreeOfParallelism(4)
                    .Select(path => (path, IsLink(path) ? ShellLink.TryRead(path) : null))
                    .ToList())
            .Select(s => new ShortcutEntry(s.Path, s.Link?.TargetPath))
            .ToList();
        var shortcutElapsed = Stopwatch.GetElapsedTime(shortcutStart);

        var packaged = packagedTask.GetAwaiter().GetResult();
        logger.LogInformation("ショートカット {Shortcuts} 件（{ShortcutMs:F0}ms）、パッケージのアプリ {Packaged} 件（{PackagedMs:F0}ms）",
            shortcuts.Count, shortcutElapsed.TotalMilliseconds, packaged.Apps.Count, packaged.Elapsed.TotalMilliseconds);
        return new StartMenuSnapshot(shortcuts, [.. packaged.Apps]);
    }

    /// <summary>集めたものから項目を作る。ファイルには触らないので速い。</summary>
    internal static IReadOnlyList<CatalogItem> CreateItems(StartMenuSnapshot snapshot) =>
    [
        .. snapshot.Shortcuts.Select(CreateItem),
        .. snapshot.Packaged.Select(CreatePackagedItem),
    ];

    public static bool IsShortcut(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is ".lnk" or ".url";

    private static bool IsLink(string path) => Path.GetExtension(path).Equals(".lnk", StringComparison.OrdinalIgnoreCase);

    private static CatalogItem CreateItem(ShortcutEntry shortcut)
    {
        var (path, target) = shortcut;
        var title = Path.GetFileNameWithoutExtension(path);

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
            // リンク先があるかは、実行するときに確かめる。一覧を作るときに確かめると、つながっていないドライブで待たされる
            new("ファイルの場所を開く", ctx =>
            {
                ctx.Shell.ShowInFolder(target is not null && File.Exists(target) ? target : path);
                return ValueTask.FromResult(ActionResult.Close);
            }),
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
            Completion = title,
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
            Completion = app.Name,
            Subtitle = app.PackageName,
            Icon = IconSource.FromFile(location),
            Actions = [ItemAction.Open(location, "起動")],
        };
    }
}

/// <summary>スタートメニューから集めたもの。前回の一覧として保存する。</summary>
internal sealed record StartMenuSnapshot(List<ShortcutEntry> Shortcuts, List<PackagedApp> Packaged);

/// <summary>ショートカットのパスと、そのリンク先（読めなければ null）。</summary>
internal sealed record ShortcutEntry(string Path, string? Target);

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
