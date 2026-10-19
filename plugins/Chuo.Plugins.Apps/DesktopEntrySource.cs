using System.Diagnostics;
using Chuo.Abstractions;
using Chuo.Plugins.Shared;

namespace Chuo.Plugins.Apps;

/// <summary>Linux の .desktop ファイルからアプリを列挙する（設計 4.5）。</summary>
internal sealed class DesktopEntrySource : IAppSource
{
    private static readonly EnumerationOptions Enumeration = new() { RecurseSubdirectories = true, IgnoreInaccessible = true };

    private readonly string? _locale = GetLocale();
    private readonly HashSet<string> _currentDesktops = (Environment.GetEnvironmentVariable("XDG_CURRENT_DESKTOP") ?? "")
        .Split(':', StringSplitOptions.RemoveEmptyEntries)
        .ToHashSet(StringComparer.OrdinalIgnoreCase);
    private readonly IconThemeLookup _icons = new(DataDirectories());

    /// <summary>優先度の高い順。同じ ID の .desktop が複数あれば、先にあるものを使う。</summary>
    public IReadOnlyList<WatchedFolder> Roots { get; } = DataDirectories().Select(d => new WatchedFolder(Path.Combine(d, "applications"))).ToList();

    public IReadOnlyList<CatalogItem> Scan()
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var items = new List<CatalogItem>();
        foreach (var root in Roots.Select(r => r.Path).Where(Directory.Exists))
        {
            foreach (var path in Directory.EnumerateFiles(root, "*.desktop", Enumeration))
            {
                // 仕様のデスクトップファイル ID: applications/ からの相対パスの / を - にしたもの
                var id = Path.GetRelativePath(root, path).Replace('/', '-');
                // 隠す設定のものも、優先度の低い同じ ID のものを隠すために記録する
                if (seen.Add(id) && CreateItem(id, path) is { } item)
                {
                    items.Add(item);
                }
            }
        }
        return items;
    }

    private CatalogItem? CreateItem(string id, string path)
    {
        DesktopEntry entry;
        try
        {
            using var reader = File.OpenText(path);
            entry = DesktopEntry.Parse(reader);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        if (entry.Get("Type") != "Application" || entry.GetBool("NoDisplay") || entry.GetBool("Hidden") || !IsShownInCurrentDesktop(entry))
        {
            return null;
        }
        if (entry.GetLocalized("Name", _locale) is not { Length: > 0 } title || entry.Get("Exec") is not { } exec)
        {
            return null;
        }
        if (entry.Get("TryExec") is { Length: > 0 } tryExec && FindExecutable(tryExec) is null)
        {
            return null;
        }
        var icon = entry.Get("Icon");
        if (DesktopExec.Parse(exec, title, icon, path) is not { } command)
        {
            return null;
        }

        var keywords = new List<string>();
        if (entry.Get("Name") is { } name && name != title)
        {
            keywords.Add(name);
        }
        if (entry.GetLocalized("GenericName", _locale) is { Length: > 0 } genericName)
        {
            keywords.Add(genericName);
        }
        keywords.AddRange(DesktopEntry.SplitList(entry.GetLocalized("Keywords", _locale)));
        // 実行ファイルの名前（例: GNOME Text Editor の gnome-text-editor）でも見つかるようにする
        var program = Path.GetFileName(command[0]);
        if (program is not ("env" or "flatpak" or "sh") && !title.Contains(program, StringComparison.OrdinalIgnoreCase))
        {
            keywords.Add(program);
        }

        var terminal = entry.GetBool("Terminal");
        var workingDirectory = entry.Get("Path");
        return new CatalogItem
        {
            Id = id,
            Title = title,
            Completion = title,
            Subtitle = entry.GetLocalized("Comment", _locale) ?? entry.GetLocalized("GenericName", _locale),
            Icon = icon is null ? null : _icons.Find(icon),
            Keywords = keywords,
            Actions =
            [
                new ItemAction("起動", _ =>
                {
                    Launch(path, command, terminal, workingDirectory);
                    return ValueTask.FromResult(ActionResult.Close);
                }),
            ],
        };
    }

    private bool IsShownInCurrentDesktop(DesktopEntry entry)
    {
        var onlyShowIn = DesktopEntry.SplitList(entry.Get("OnlyShowIn"));
        if (onlyShowIn.Count > 0 && !onlyShowIn.Any(_currentDesktops.Contains))
        {
            return false;
        }
        return !DesktopEntry.SplitList(entry.Get("NotShowIn")).Any(_currentDesktops.Contains);
    }

    /// <summary>
    /// gio があれば任せる（端末で開くアプリや起動の通知も正しく扱える）。なければ Exec を直接実行する。
    /// </summary>
    private static void Launch(string desktopFilePath, IReadOnlyList<string> command, bool terminal, string? workingDirectory)
    {
        ProcessStartInfo start;
        if (FindExecutable("gio") is { } gio)
        {
            start = new ProcessStartInfo(gio) { ArgumentList = { "launch", desktopFilePath } };
        }
        else
        {
            // TODO: gio がなく、端末で開くアプリ（Terminal=true）のときは端末の中で実行する
            _ = terminal;
            start = new ProcessStartInfo(command[0]);
            foreach (var argument in command.Skip(1))
            {
                start.ArgumentList.Add(argument);
            }
            if (!string.IsNullOrEmpty(workingDirectory))
            {
                start.WorkingDirectory = workingDirectory;
            }
        }
        start.UseShellExecute = false;
        Process.Start(start)?.Dispose();
    }

    private static string? FindExecutable(string name)
    {
        if (Path.IsPathRooted(name))
        {
            return File.Exists(name) ? name : null;
        }
        return (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(':', StringSplitOptions.RemoveEmptyEntries)
            .Select(dir => Path.Combine(dir, name))
            .FirstOrDefault(File.Exists);
    }

    private static string? GetLocale() =>
        new[] { "LC_ALL", "LC_MESSAGES", "LANG" }
            .Select(Environment.GetEnvironmentVariable)
            .FirstOrDefault(value => !string.IsNullOrEmpty(value));

    /// <summary>XDG のデータフォルダ。優先度の高い順。</summary>
    private static IReadOnlyList<string> DataDirectories()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var dataHome = Environment.GetEnvironmentVariable("XDG_DATA_HOME") is { Length: > 0 } h ? h : Path.Combine(home, ".local", "share");
        var dataDirs = Environment.GetEnvironmentVariable("XDG_DATA_DIRS") is { Length: > 0 } d ? d : "/usr/local/share:/usr/share";
        return [dataHome, .. dataDirs.Split(':', StringSplitOptions.RemoveEmptyEntries)];
    }
}

/// <summary>
/// アイコンの名前から画像ファイルを探す。
/// TODO: 使用中のアイコンテーマに従う。SVG にしかないアイコンは表示できない（ホストが SVG に対応していない）。
/// </summary>
internal sealed class IconThemeLookup(IReadOnlyList<string> dataDirectories)
{
    private static readonly string[] Themes = ["hicolor", "Adwaita"];
    private static readonly string[] Sizes = ["64x64", "48x48", "96x96", "128x128", "256x256", "32x32"];
    private static readonly string[] ImageExtensions = [".png", ".jpg", ".jpeg"];

    private readonly IReadOnlyList<string> _iconDirectories =
    [
        .. dataDirectories.Select(d => Path.Combine(d, "icons")),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".icons"),
    ];

    public IconSource? Find(string icon)
    {
        if (Path.IsPathRooted(icon))
        {
            return File.Exists(icon) && ImageExtensions.Contains(Path.GetExtension(icon).ToLowerInvariant())
                ? IconSource.FromImage(icon)
                : null;
        }

        foreach (var directory in _iconDirectories)
        {
            foreach (var theme in Themes)
            {
                foreach (var size in Sizes)
                {
                    var path = Path.Combine(directory, theme, size, "apps", icon + ".png");
                    if (File.Exists(path))
                    {
                        return IconSource.FromImage(path);
                    }
                }
            }
        }

        var pixmap = Path.Combine("/usr/share/pixmaps", icon + ".png");
        return File.Exists(pixmap) ? IconSource.FromImage(pixmap) : null;
    }
}
