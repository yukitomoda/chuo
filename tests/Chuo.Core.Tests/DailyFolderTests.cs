using Chuo.Plugins.DailyFolder;
using Microsoft.Extensions.Options;

namespace Chuo.Core.Tests;

public sealed class DailyFolderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "chuo-daily-" + Guid.NewGuid().ToString("N"));
    private readonly DailyFolderOptions _options;
    private readonly KeptFolders _kept;
    private readonly DailyFolders _folders;

    public DailyFolderTests()
    {
        _options = new DailyFolderOptions { Root = Path.Combine(_root, "daily"), RetentionDays = 7 };
        _kept = new KeptFolders(Path.Combine(_root, "data", "kept.json"));
        _folders = new DailyFolders(new StaticOptionsMonitor<DailyFolderOptions>(_options), _kept, new FixedTime(new DateTime(2026, 10, 19, 9, 0, 0)));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private void Create(params string[] names)
    {
        foreach (var name in names)
        {
            Directory.CreateDirectory(Path.Combine(_options.Root, name));
        }
    }

    [Fact]
    public void 今日のフォルダがなければ作る()
    {
        var path = _folders.EnsureToday();

        Assert.Equal(Path.Combine(_options.Root, "2026-10-19"), path);
        Assert.True(Directory.Exists(path));
    }

    [Fact]
    public void 書式に合う名前のフォルダだけを新しい順に並べる()
    {
        Create("2026-10-01", "2026-10-18", "memo", "2026-13-01");

        Assert.Equal(["2026-10-18", "2026-10-01"], _folders.List().Select(f => f.Name));
    }

    [Fact]
    public void 直近のフォルダは今日を除いて新しい順に指定の数まで()
    {
        Create("2026-10-20", "2026-10-19", "2026-10-18", "2026-10-15", "2026-10-01");

        Assert.Equal(["2026-10-18", "2026-10-15"], _folders.Recent(2).Select(f => f.Name));
    }

    [Fact]
    public void 今日のフォルダは作る前でも保持できる()
    {
        Assert.False(_folders.TodayEntry().Kept);

        _folders.ToggleKept(_folders.TodayEntry().Name);

        Assert.Equal("2026-10-19", _folders.TodayEntry().Name);
        Assert.True(_folders.TodayEntry().Kept);
        Assert.False(Directory.Exists(_folders.TodayPath));
    }

    [Fact]
    public void 残す日数より古く保持していないフォルダを削除の対象にする()
    {
        // 残す日数は 7 なので、10-13 から今日（10-19）までは残す
        Create("2026-10-19", "2026-10-13", "2026-10-12", "2026-10-01", "2026-09-30");
        _kept.Toggle("2026-10-01");

        Assert.Equal(["2026-10-12", "2026-09-30"], _folders.PruneCandidates().Select(f => f.Name));
    }

    [Fact]
    public void 保持は切り替えられ_ファイルに残る()
    {
        Assert.True(_kept.Toggle("2026-10-01"));
        Assert.True(new KeptFolders(Path.Combine(_root, "data", "kept.json")).Contains("2026-10-01"));

        Assert.False(_kept.Toggle("2026-10-01"));
        Assert.False(new KeptFolders(Path.Combine(_root, "data", "kept.json")).Contains("2026-10-01"));
    }

    [Fact]
    public void 既定の置き場所はホームの直下にしない()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        Assert.Equal("Daily", Path.GetFileName(DailyFolderOptions.DefaultRoot));
        Assert.NotEqual(Path.GetFullPath(home).TrimEnd(Path.DirectorySeparatorChar),
            Path.GetFullPath(Path.GetDirectoryName(DailyFolderOptions.DefaultRoot)!).TrimEnd(Path.DirectorySeparatorChar));
    }

    private sealed class FixedTime(DateTime local) : TimeProvider
    {
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;

        public override DateTimeOffset GetUtcNow() => new(local, TimeSpan.Zero);
    }
}
