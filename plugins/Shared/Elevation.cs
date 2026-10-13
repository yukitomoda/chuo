namespace Chuo.Plugins.Shared;

/// <summary>管理者として実行できるか（Windows）。</summary>
internal static class Elevation
{
    private static readonly string[] ExecutableExtensions = [".exe", ".bat", ".cmd", ".com", ".msc"];

    /// <summary>
    /// 実行ファイルと、実行ファイルを指すショートカットなら true。リンク先が読めないショートカット
    /// （Office などのアドバタイズされたショートカット）も、アプリのことが多いので true にする。
    /// </summary>
    /// <param name="target">ショートカットのリンク先。ショートカットでなければ null。</param>
    public static bool CanRunAsAdministrator(string path, string? target)
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }
        var extension = Path.GetExtension(path);
        if (extension.Equals(".lnk", StringComparison.OrdinalIgnoreCase))
        {
            return target is null || IsExecutable(target);
        }
        return IsExecutable(path);
    }

    private static bool IsExecutable(string path) =>
        ExecutableExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);
}
