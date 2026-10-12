namespace Chuo.Plugins.Shortcuts;

/// <summary>指定したフォルダから、対象の拡張子のファイルを探す。</summary>
public static class ShortcutScanner
{
    /// <summary>Windows で対象にする拡張子。</summary>
    public static readonly IReadOnlyList<string> WindowsExtensions = [".lnk", ".url", ".exe", ".bat", ".cmd"];

    /// <summary>Linux で対象にする拡張子。</summary>
    public static readonly IReadOnlyList<string> LinuxExtensions = [".desktop", ".appimage"];

    /// <summary>
    /// ファイルのフルパスを返す。存在しないフォルダは無視する。フォルダが重なっていても、同じファイルは1回だけ返す。
    /// </summary>
    public static IReadOnlyList<string> Scan(IEnumerable<ShortcutFolder> folders, IReadOnlyCollection<string> extensions)
    {
        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var seen = new HashSet<string>(comparer);
        var files = new List<string>();
        foreach (var folder in folders)
        {
            if (ResolvePath(folder) is not { } root || !Directory.Exists(root))
            {
                continue;
            }
            var options = new EnumerationOptions { RecurseSubdirectories = folder.IncludeSubfolders, IgnoreInaccessible = true };
            foreach (var path in Directory.EnumerateFiles(root, "*", options))
            {
                if (extensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase) && seen.Add(path))
                {
                    files.Add(path);
                }
            }
        }
        return files;
    }

    /// <summary>環境変数を展開したフルパスを返す。パスが空なら null。</summary>
    public static string? ResolvePath(ShortcutFolder folder) =>
        string.IsNullOrWhiteSpace(folder.Path) ? null : Path.GetFullPath(Environment.ExpandEnvironmentVariables(folder.Path.Trim()));
}
