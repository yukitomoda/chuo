namespace Chuo.Core;

/// <summary>設定とデータの保存場所（設計 9 章）。</summary>
public static class ChuoPaths
{
    /// <summary>設定のフォルダ。Windows は <c>%APPDATA%\chuo</c>、Linux は <c>~/.config/chuo</c>。</summary>
    public static string ConfigDirectory { get; } = OperatingSystem.IsWindows()
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "chuo")
        : Path.Combine(XdgDirectory("XDG_CONFIG_HOME", ".config"), "chuo");

    /// <summary>データのフォルダ。Windows は <c>%APPDATA%\chuo</c>、Linux は <c>~/.local/share/chuo</c>。</summary>
    public static string DataDirectory { get; } = OperatingSystem.IsWindows()
        ? ConfigDirectory
        : Path.Combine(XdgDirectory("XDG_DATA_HOME", Path.Combine(".local", "share")), "chuo");

    public static string SettingsFile => Path.Combine(ConfigDirectory, "settings.json");

    /// <summary>項目ごとのユーザー設定（お気に入り、無視など）。</summary>
    public static string ItemsFile => Path.Combine(ConfigDirectory, "items.json");

    public static string LogDirectory => Path.Combine(DataDirectory, "logs");

    /// <summary>本体に同梱するプラグインのフォルダ。</summary>
    public static string BundledPluginDirectory => Path.Combine(AppContext.BaseDirectory, "plugins");

    /// <summary>プラグインごとのデータのフォルダ。</summary>
    public static string PluginDataDirectory(string pluginId) => Path.Combine(DataDirectory, "plugin-data", pluginId);

    private static string XdgDirectory(string variable, string fallback) =>
        Environment.GetEnvironmentVariable(variable) is { Length: > 0 } dir
            ? dir
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), fallback);
}
