using System.Diagnostics;
using Chuo.Abstractions;
using Chuo.Plugins.Shared;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Chuo.Plugins.Shortcuts;

/// <summary>
/// 設定で指定したフォルダの中のショートカットと実行ファイルを一覧にする。
/// フォルダの変更と、設定の変更を監視して作り直す。
/// </summary>
[CatalogProvider(Keyword = "l", IncludeInGlobal = true)]
public sealed class ShortcutCatalogProvider(IOptionsMonitor<ShortcutsOptions> options, ILogger<ShortcutCatalogProvider> logger)
    : ICatalogProvider, IDisposable
{
    /// <summary>ファイルのコピーなどは変更が続くので、落ち着いてから作り直す。</summary>
    private static readonly TimeSpan RescanDelay = TimeSpan.FromSeconds(1);

    private readonly Lock _gate = new();
    private readonly List<FileSystemWatcher> _watchers = [];
    private Timer? _rescanTimer;
    private IDisposable? _optionsSubscription;
    private ICatalog? _catalog;
    private bool _disposed;

    public Task InitializeAsync(ICatalog catalog, CancellationToken cancellationToken)
    {
        _catalog = catalog;
        _rescanTimer = new Timer(_ => Rescan());
        // 設定ファイルの再読み込みでは同じ通知が続けて来ることがあるので、まとめて1回作り直す
        _optionsSubscription = options.OnChange(_ => ScheduleRescan());
        Rescan();
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            DisposeWatchers();
        }
        _optionsSubscription?.Dispose();
        _rescanTimer?.Dispose();
    }

    private void ScheduleRescan() => _rescanTimer?.Change(RescanDelay, Timeout.InfiniteTimeSpan);

    private void Rescan()
    {
        lock (_gate)
        {
            if (_disposed || _catalog is null)
            {
                return;
            }
            try
            {
                var stopwatch = Stopwatch.StartNew();
                var current = options.CurrentValue;
                var extensions = OperatingSystem.IsWindows() ? ShortcutScanner.WindowsExtensions : ShortcutScanner.LinuxExtensions;
                var items = ShortcutScanner.Scan(current.Folders, extensions)
                    .Select(CreateItem)
                    .OfType<CatalogItem>()
                    .ToList();
                _catalog.ReplaceAll(items);
                Watch(current);
                logger.LogInformation("{Folders} 個のフォルダから {Count} 件を読み込んだ（{Elapsed:F0}ms）",
                    current.Folders.Count, items.Count, stopwatch.Elapsed.TotalMilliseconds);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "ショートカットの一覧を作れなかった");
            }
        }
    }

    private void Watch(ShortcutsOptions current)
    {
        DisposeWatchers();
        foreach (var folder in current.Folders)
        {
            if (ShortcutScanner.ResolvePath(folder) is not { } root)
            {
                continue;
            }
            if (!Directory.Exists(root))
            {
                logger.LogWarning("フォルダが見つからない: {Folder}", root);
                continue;
            }
            try
            {
                var watcher = new FileSystemWatcher(root) { IncludeSubdirectories = folder.IncludeSubfolders };
                watcher.Created += OnChanged;
                watcher.Deleted += OnChanged;
                watcher.Renamed += OnChanged;
                watcher.EnableRaisingEvents = true;
                _watchers.Add(watcher);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                logger.LogWarning(ex, "フォルダの変更を監視できない: {Folder}", root);
            }
        }
    }

    private void OnChanged(object sender, FileSystemEventArgs e) => ScheduleRescan();

    private void DisposeWatchers()
    {
        foreach (var watcher in _watchers)
        {
            watcher.Dispose();
        }
        _watchers.Clear();
    }

    private static CatalogItem? CreateItem(string path) =>
        Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".desktop" => CreateDesktopEntryItem(path),
            ".appimage" => CreateExecutableItem(path),
            _ => CreateWindowsItem(path),
        };

    private static CatalogItem CreateWindowsItem(string path)
    {
        var title = Path.GetFileNameWithoutExtension(path);
        var target = Path.GetExtension(path).Equals(".lnk", StringComparison.OrdinalIgnoreCase)
            ? ShellLink.TryRead(path)?.TargetPath
            : null;

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
            Completion = title,
            Icon = IconSource.FromFile(path),
            Keywords = keywords,
            Actions = actions,
        };
    }

    private static CatalogItem? CreateDesktopEntryItem(string path)
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
        var locale = Environment.GetEnvironmentVariable("LANG");
        var title = entry.GetLocalized("Name", locale) ?? Path.GetFileNameWithoutExtension(path);
        return new CatalogItem
        {
            Id = path,
            Title = title,
            Completion = title,
            Subtitle = entry.GetLocalized("Comment", locale) ?? path,
            Actions =
            [
                new ItemAction("起動", _ =>
                {
                    Run("gio", "launch", path);
                    return ValueTask.FromResult(ActionResult.Close);
                }),
                ItemAction.ShowInFolder(path, "ファイルの場所を開く"),
            ],
        };
    }

    private static CatalogItem CreateExecutableItem(string path) =>
        new()
        {
            Id = path,
            Title = Path.GetFileNameWithoutExtension(path),
            Completion = Path.GetFileNameWithoutExtension(path),
            Subtitle = path,
            Actions =
            [
                new ItemAction("起動", _ =>
                {
                    Run(path);
                    return ValueTask.FromResult(ActionResult.Close);
                }),
                ItemAction.ShowInFolder(path, "ファイルの場所を開く"),
            ],
        };

    private static void Run(string fileName, params string[] arguments)
    {
        var start = new ProcessStartInfo(fileName) { UseShellExecute = false };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }
        Process.Start(start)?.Dispose();
    }
}
