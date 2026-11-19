using Chuo.Abstractions;
using Chuo.Plugins.Reminders;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Chuo.Core.Tests;

public sealed class ReminderTests : IDisposable
{
    private static readonly ScheduleDefaults DefaultSchedule = new(1, 1, 9, 0);
    private readonly string _root = Path.Combine(Path.GetTempPath(), "chuo-reminders-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Theory]
    // 2026-11-19 は木曜日
    [InlineData("11h", "2026-11-19 10:30", "2026-11-19 11:00")]
    [InlineData("11h", "2026-11-19 11:00", "2026-11-20 11:00")]
    [InlineData("11h", "2026-11-19 12:00", "2026-11-20 11:00")]
    [InlineData("11", "2026-11-19 10:30", "2026-11-19 11:00")]
    [InlineData("11", "2026-11-19 12:00", "2026-11-20 11:00")]
    [InlineData("11h30m", "2026-11-19 10:30", "2026-11-19 11:30")]
    [InlineData("11h30", "2026-11-19 10:30", "2026-11-19 11:30")]
    [InlineData("30m", "2026-11-19 10:15", "2026-11-19 10:30")]
    [InlineData("30m", "2026-11-19 10:45", "2026-11-19 11:30")]
    [InlineData("1d11h30m", "2026-11-19 10:30", "2026-12-01 11:30")]
    [InlineData("1d11h30m", "2026-11-01 10:30", "2026-11-01 11:30")]
    [InlineData("1d11", "2026-11-19 10:30", "2026-12-01 11:00")]
    [InlineData("6M5d", "2026-01-10 10:00", "2026-06-05 09:00")]
    [InlineData("6M5d", "2026-11-19 10:00", "2027-06-05 09:00")]
    [InlineData("6M5", "2026-11-19 10:00", "2027-06-05 09:00")]
    [InlineData("2027y", "2026-11-19 10:00", "2027-01-01 09:00")]
    [InlineData("2027y6", "2026-11-19 10:00", "2027-06-01 09:00")]
    [InlineData("Mon", "2026-11-19 10:00", "2026-11-23 09:00")]
    [InlineData("mon", "2026-11-19 10:00", "2026-11-23 09:00")]
    [InlineData("Mon14h", "2026-11-19 10:00", "2026-11-23 14:00")]
    [InlineData("Mon;14h", "2026-11-19 10:00", "2026-11-23 14:00")]
    [InlineData("Mon14h", "2026-11-23 10:00", "2026-11-23 14:00")]
    [InlineData("Mon;14h", "2026-11-23 10:00", "2026-11-23 14:00")]
    [InlineData("Mon14h", "2026-11-23 15:00", "2026-11-30 14:00")]
    [InlineData("Mon;14h", "2026-11-23 15:00", "2026-11-30 14:00")]
    [InlineData("Mon14h30", "2026-11-19 10:00", "2026-11-23 14:30")]
    [InlineData("Mon14", "2026-11-19 10:00", "2026-11-23 14:00")]
    [InlineData("+30m", "2026-11-19 14:25", "2026-11-19 14:55")]
    [InlineData("+1h", "2026-11-19 14:25", "2026-11-19 15:25")]
    [InlineData("+1h30", "2026-11-19 14:25", "2026-11-19 15:55")]
    [InlineData("+1d", "2026-11-19 14:25", "2026-11-20 09:00")]
    [InlineData("+1d;11h30m", "2026-11-19 14:25", "2026-11-20 11:30")]
    [InlineData("+1d;8h", "2026-11-19 14:25", "2026-11-20 08:00")]
    [InlineData("+1d2h", "2026-11-19 14:25", "2026-11-20 16:25")]
    [InlineData("+3M", "2026-11-19 14:25", "2027-02-19 09:00")]
    [InlineData("+1y", "2026-11-19 14:25", "2027-11-19 09:00")]
    [InlineData("+1M;1d;9h", "2026-11-19 14:25", "2026-12-01 09:00")]
    [InlineData("+4y", "2024-02-29 10:00", "2028-02-29 09:00")]
    public void スケジュール指定を解釈できる(string expression, string nowText, string expectedText)
    {
        var now = DateTime.Parse(nowText);
        var expected = DateTime.Parse(expectedText);

        var result = ScheduleParser.Parse(expression, now, DefaultSchedule);

        Assert.True(result.IsSuccess, result.Error);
        Assert.Equal(expected, result.Value);
    }

    [Fact]
    public void 省略した時刻は設定の既定値を使う()
    {
        var defaults = new ScheduleDefaults(Month: 4, Day: 15, Hour: 10, Minute: 30);
        var now = new DateTime(2026, 1, 10, 14, 0, 0);

        Assert.Equal(new DateTime(2026, 1, 11, 10, 30, 0), ScheduleParser.Parse("+1d", now, defaults).Value);
        Assert.Equal(new DateTime(2027, 4, 15, 10, 30, 0), ScheduleParser.Parse("2027y", now, defaults).Value);
        Assert.Equal(new DateTime(2026, 5, 15, 10, 30, 0), ScheduleParser.Parse("5M", now, defaults).Value);
    }

    [Theory]
    [InlineData("+1M", "2026-01-31 10:00")] // 2月31日は存在しない
    [InlineData("+1y", "2024-02-29 10:00")] // 2025年2月29日は存在しない
    [InlineData("2M31d", "2026-01-10 10:00")]
    [InlineData("+Mon", "2026-11-19 10:00")] // 曜日に + は指定不可
    [InlineData("Mon5d", "2026-11-19 10:00")] // 曜日と日付は混在不可
    [InlineData("2026y", "2026-11-19 10:00")] // 過去の日時（2026-01-01 09:00）
    [InlineData("+0d;10h", "2026-11-19 14:00")] // 過去の日時
    [InlineData("+0m", "2026-11-19 14:00")]
    [InlineData("24h", "2026-11-19 10:00")]
    [InlineData("60m", "2026-11-19 10:00")]
    [InlineData("13M", "2026-11-19 10:00")]
    [InlineData("0d", "2026-11-19 10:00")]
    [InlineData("1h2h", "2026-11-19 10:00")]
    [InlineData("30m5", "2026-11-19 10:00")]
    [InlineData("+1d;", "2026-11-19 10:00")]
    [InlineData("10x", "2026-11-19 10:00")]
    [InlineData("+", "2026-11-19 10:00")]
    public void 不正なスケジュール指定はエラーになる(string expression, string nowText)
    {
        var result = ScheduleParser.Parse(expression, DateTime.Parse(nowText), DefaultSchedule);

        Assert.False(result.IsSuccess);
        Assert.False(string.IsNullOrEmpty(result.Error));
    }

    [Fact]
    public void 既定の日が31日のとき_2月の絶対指定は不正になる()
    {
        var defaults = new ScheduleDefaults(Month: 1, Day: 31, Hour: 9, Minute: 0);

        var result = ScheduleParser.Parse("2M", new DateTime(2026, 1, 10, 10, 0, 0), defaults);

        Assert.False(result.IsSuccess);
        Assert.Contains("2月31日", result.Error);
    }

    [Fact]
    public void ストアは日時順に保存と読み直しができ_期限が来たものだけを取り出せる()
    {
        var path = Path.Combine(_root, "reminders.json");
        var store = new ReminderStore(path);
        var t1 = new DateTimeOffset(2026, 11, 19, 11, 0, 0, TimeSpan.FromHours(9));
        var t2 = new DateTimeOffset(2026, 11, 19, 10, 0, 0, TimeSpan.FromHours(9));

        store.Add("遅い", t1);
        var early = store.Add("早い", t2);

        // 別のインスタンスで読み直しても日時順に並ぶ
        var reloaded = new ReminderStore(path);
        Assert.Equal(["早い", "遅い"], reloaded.List().Select(e => e.Title));

        var due = reloaded.TakeDue(t2);
        Assert.Single(due);
        Assert.Equal(early.Id, due[0].Id);
        Assert.Equal(["遅い"], reloaded.List().Select(e => e.Title));
    }

    [Fact]
    public async Task コマンドで登録と一覧と削除ができる()
    {
        var store = new ReminderStore(Path.Combine(_root, "reminders.json"));
        var time = new AdjustableTime(new DateTimeOffset(2026, 11, 19, 10, 0, 0, TimeSpan.FromHours(9)));
        var commands = new ReminderCommands(store, new TestOptionsMonitor<ReminderOptions>(new ReminderOptions()), time);
        var actionContext = new ActionContext(new StubShell(), new StubClipboard(), CancellationToken.None);

        // 登録前の一覧
        var empty = await commands.QueryAsync(new Query("remind", "", "remind"), CancellationToken.None);
        Assert.Single(empty);
        Assert.Equal("リマインダーはない", empty[0].Title);

        // 動的に具体的な日時と残り時間を表示して登録
        var preview = await commands.QueryAsync(new Query("remind +30m 会議", "+30m 会議", "remind"), CancellationToken.None);
        Assert.Single(preview);
        Assert.Contains("2026/11/19 (木) 10:30", preview[0].Title);
        Assert.Contains("あと 30分", preview[0].Title);
        Assert.Contains("会議", preview[0].Subtitle);

        var outcome = await preview[0].Actions[0].Execute(actionContext);
        Assert.IsType<ActionResult.CloseResult>(outcome);
        Assert.Single(store.List());

        // 一覧に出て、タイトルが日時・サブタイトルが名前と削除案内になり、Ctrl+Enter（2番目のアクション）で削除できる
        var list = await commands.QueryAsync(new Query("remind", "", "remind"), CancellationToken.None);
        Assert.Single(list);
        Assert.Contains("2026/11/19 (木) 10:30", list[0].Title);
        Assert.Contains("あと 30分", list[0].Title);
        Assert.Equal("会議 ・ Ctrl+Enter で削除", list[0].Subtitle);
        Assert.Equal("削除", list[0].Actions[1].Title);

        await list[0].Actions[1].Execute(actionContext);
        Assert.Empty(store.List());
    }

    [Fact]
    public void スケジューラが期限のリマインダーを通知し_スヌーズで再登録できる()
    {
        var store = new ReminderStore(Path.Combine(_root, "reminders.json"));
        var notifier = new RecordingNotifier();
        var now = new DateTimeOffset(2026, 11, 19, 10, 0, 0, TimeSpan.FromHours(9));
        var time = new AdjustableTime(now);
        var options = new TestOptionsMonitor<ReminderOptions>(new ReminderOptions { PlaySound = true });
        var scheduler = new ReminderScheduler(store, notifier, options, time, NullLogger<ReminderScheduler>.Instance);

        store.Add("朝会", now.AddMinutes(5));
        scheduler.FireDue();
        Assert.Empty(notifier.Shown);

        // 5分後に時刻到達
        time.Advance(TimeSpan.FromMinutes(5));
        scheduler.FireDue();
        Assert.Single(notifier.Shown);
        Assert.Equal("朝会", notifier.Shown[0].Title);
        Assert.True(notifier.Shown[0].PlaySound);
        Assert.Equal(["+5分", "+15分"], notifier.Shown[0].Actions!.Select(a => a.Label));
        Assert.Empty(store.List());

        // +5分スヌーズを押すと5分後のリマインダーが登録される
        notifier.Shown[0].Actions![0].Execute();
        var snoozed = Assert.Single(store.List());
        Assert.Equal("朝会", snoozed.Title);
        Assert.Equal(now.AddMinutes(10), snoozed.RemindAt);
    }

    private sealed class RecordingNotifier : INotifier
    {
        public List<Notification> Shown { get; } = [];
        public void Show(Notification notification) => Shown.Add(notification);
    }

    private sealed class TestOptionsMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue => value;
        public T Get(string? name) => value;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }

    private sealed class AdjustableTime(DateTimeOffset current) : TimeProvider
    {
        private DateTimeOffset _current = current;
        private readonly TimeZoneInfo _zone = TimeZoneInfo.CreateCustomTimeZone("TestJST", current.Offset, "JST", "JST");

        public override TimeZoneInfo LocalTimeZone => _zone;
        public override DateTimeOffset GetUtcNow() => _current.ToUniversalTime();
        public void Advance(TimeSpan delta) => _current = _current.Add(delta);
    }

    private sealed class StubShell : IShell
    {
        public void Open(string target) { }
        public void ShowInFolder(string path) { }
        public void RunAsAdministrator(string path) { }
    }

    private sealed class StubClipboard : IClipboard
    {
        public Task<string?> GetTextAsync(CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);
        public Task SetTextAsync(string text, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
