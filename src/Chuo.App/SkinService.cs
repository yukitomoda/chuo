using Avalonia;
using Avalonia.Styling;
using Avalonia.Threading;
using Chuo.Core;
using Chuo.Core.Skins;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Chuo.App;

/// <summary>
/// 設定で選んだスキンを読み、変わったら知らせる（設計 8.1）。設定を変えたときと、スキンのフォルダのファイルを
/// 保存したときに読み直す。読めないスキンは、ログに書いて OS のライト／ダークに従う。
/// </summary>
public sealed class SkinService : IDisposable
{
    /// <summary>保存が続いても読み直しが1回で済むよう、落ち着いてから読む。</summary>
    private static readonly TimeSpan ReloadDelay = TimeSpan.FromMilliseconds(300);

    private readonly IOptionsMonitor<ChuoSettings> _settings;
    private readonly ILogger<SkinService> _logger;
    private readonly IDisposable? _settingsChange;
    private readonly Timer _reloadTimer;
    private FileSystemWatcher? _watcher;
    private string _id;

    public SkinService(IOptionsMonitor<ChuoSettings> settings, ILogger<SkinService> logger)
    {
        _settings = settings;
        _logger = logger;
        _id = settings.CurrentValue.Skin;
        Current = Load(_id);
        _reloadTimer = new Timer(_ => Dispatcher.UIThread.Post(Reload));
        _settingsChange = settings.OnChange(s =>
        {
            if (s.Skin != _id)
            {
                _reloadTimer.Change(TimeSpan.Zero, Timeout.InfiniteTimeSpan);
            }
        });
        WatchDirectory();
    }

    public SkinCatalog Catalog { get; } = new(ChuoPaths.SkinDirectory);

    /// <summary>今のスキン。null なら OS のライト／ダークに従う。</summary>
    public Skin? Current { get; private set; }

    /// <summary>スキンが変わった。UI スレッドで呼ぶ。</summary>
    public event Action? Changed;

    /// <summary>アプリ全体（設定画面を含む）のライト／ダークを、スキンに合わせる。UI スレッドで呼ぶ。</summary>
    public void ApplyThemeVariant()
    {
        if (Application.Current is { } app)
        {
            app.RequestedThemeVariant = Current?.Base switch
            {
                SkinBase.Light => ThemeVariant.Light,
                SkinBase.Dark => ThemeVariant.Dark,
                _ => ThemeVariant.Default,
            };
        }
    }

    /// <summary>スキンのフォルダを開く。空なら、作り方が分かるよう見本を置く。</summary>
    public string EnsureDirectory()
    {
        var directory = Catalog.Directory;
        if (!Directory.Exists(directory) || !Directory.EnumerateFileSystemEntries(directory).Any())
        {
            var sample = Path.Combine(directory, "sample");
            Directory.CreateDirectory(sample);
            File.WriteAllText(Path.Combine(sample, SkinCatalog.FileName), SkinCatalog.SampleJson);
        }
        WatchDirectory();
        return directory;
    }

    private void Reload()
    {
        _id = _settings.CurrentValue.Skin;
        Current = Load(_id);
        ApplyThemeVariant();
        Changed?.Invoke();
    }

    private Skin? Load(string id)
    {
        try
        {
            return Catalog.Load(id);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "スキン {Skin} を読めなかった。OS のライト／ダークに従う", id);
            return null;
        }
    }

    /// <summary>スキンのフォルダがあれば、中のファイルの変更を監視する。今のスキンのものが変わったら読み直す。</summary>
    private void WatchDirectory()
    {
        if (_watcher is not null || !Directory.Exists(Catalog.Directory))
        {
            return;
        }
        try
        {
            _watcher = new FileSystemWatcher(Catalog.Directory) { IncludeSubdirectories = true };
            FileSystemEventHandler changed = (_, e) =>
            {
                var skinDirectory = Path.Combine(Catalog.Directory, _id);
                if (_id.Length > 0 && (e.FullPath.Equals(skinDirectory, StringComparison.OrdinalIgnoreCase)
                    || e.FullPath.StartsWith(skinDirectory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
                {
                    _reloadTimer.Change(ReloadDelay, Timeout.InfiniteTimeSpan);
                }
            };
            _watcher.Changed += changed;
            _watcher.Created += changed;
            _watcher.Deleted += changed;
            _watcher.Renamed += (s, e) => changed(s, e);
            _watcher.EnableRaisingEvents = true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            _logger.LogWarning(ex, "スキンのフォルダを監視できない");
        }
    }

    public void Dispose()
    {
        _settingsChange?.Dispose();
        _watcher?.Dispose();
        _reloadTimer.Dispose();
    }
}
