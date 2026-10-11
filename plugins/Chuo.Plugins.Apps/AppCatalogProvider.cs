using System.Diagnostics;
using Chuo.Abstractions;
using Microsoft.Extensions.Logging;

namespace Chuo.Plugins.Apps;

/// <summary>
/// インストールされたアプリを一覧にする。アプリの追加や削除はフォルダの変更の監視で反映する。
/// </summary>
[CatalogProvider]
public sealed class AppCatalogProvider(ILogger<AppCatalogProvider> logger) : ICatalogProvider, IDisposable
{
    /// <summary>インストールの途中は変更が続くので、落ち着いてから作り直す。</summary>
    private static readonly TimeSpan RescanDelay = TimeSpan.FromSeconds(2);

    private readonly IAppSource? _source = CreateSource();
    private readonly List<FileSystemWatcher> _watchers = [];
    private readonly Lock _scanGate = new();
    private Timer? _rescanTimer;
    private ICatalog? _catalog;
    private bool _disposed;

    public Task InitializeAsync(ICatalog catalog, CancellationToken cancellationToken)
    {
        if (_source is null)
        {
            logger.LogInformation("この OS ではアプリの一覧に対応していない");
            return Task.CompletedTask;
        }

        _catalog = catalog;
        Rescan();

        _rescanTimer = new Timer(_ => Rescan());
        foreach (var root in _source.Roots.Where(Directory.Exists))
        {
            try
            {
                var watcher = new FileSystemWatcher(root) { IncludeSubdirectories = true };
                watcher.Created += OnChanged;
                watcher.Deleted += OnChanged;
                watcher.Renamed += OnChanged;
                watcher.Changed += OnChanged;
                watcher.EnableRaisingEvents = true;
                _watchers.Add(watcher);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                logger.LogWarning(ex, "フォルダの変更を監視できない: {Root}", root);
            }
        }
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        lock (_scanGate)
        {
            _disposed = true;
        }
        foreach (var watcher in _watchers)
        {
            watcher.Dispose();
        }
        _rescanTimer?.Dispose();
    }

    private void OnChanged(object sender, FileSystemEventArgs e) => _rescanTimer?.Change(RescanDelay, Timeout.InfiniteTimeSpan);

    private void Rescan()
    {
        lock (_scanGate)
        {
            if (_disposed || _source is null || _catalog is null)
            {
                return;
            }
            try
            {
                var stopwatch = Stopwatch.StartNew();
                var items = _source.Scan();
                _catalog.ReplaceAll(items);
                logger.LogInformation("アプリを {Count} 件読み込んだ（{Elapsed:F0}ms）", items.Count, stopwatch.Elapsed.TotalMilliseconds);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "アプリの一覧を作れなかった");
            }
        }
    }

    private static IAppSource? CreateSource() =>
        OperatingSystem.IsWindows() ? new StartMenuSource()
        : OperatingSystem.IsLinux() ? new DesktopEntrySource()
        : null;
}
