using Chuo.Abstractions;
using Microsoft.Extensions.Options;

namespace Chuo.Plugins.Reminders;

/// <summary>
/// リマインダーの登録と一覧（キーワード <c>remind</c>）。
/// <para>
/// <c>remind {スケジュール指定} {名前}</c> で登録し、入力中に具体的なリマインド日時をリアルタイムに表示する。
/// キーワードだけのときは、登録済みのリマインダーを日時順に表示する。
/// </para>
/// </summary>
[LiveQueryProvider(Keyword = "remind")]
public sealed class ReminderCommands(
    ReminderStore store, IOptionsMonitor<ReminderOptions> options, TimeProvider time) : ILiveQueryProvider
{
    private const string DefaultTitle = "リマインダー";

    public ValueTask<IReadOnlyList<ResultItem>> QueryAsync(Query query, CancellationToken cancellationToken)
    {
        var prefix = query.Keyword + " ";
        var text = query.Text.TrimStart();
        var now = time.GetLocalNow();

        if (text.Length == 0)
        {
            return ValueTask.FromResult(ListItems(prefix, now));
        }

        var (scheduleText, name) = SplitScheduleAndName(text);
        var parsed = ScheduleParser.Parse(scheduleText, now.DateTime, options.CurrentValue.ToDefaults());
        if (!parsed.IsSuccess)
        {
            IReadOnlyList<ResultItem> errorItem =
            [
                new ResultItem
                {
                    Id = "error",
                    Title = parsed.Error!,
                    Subtitle = "例: 11h30 / +30m / +1d;9h / 6M5d / Mon14h",
                },
            ];
            return ValueTask.FromResult(errorItem);
        }

        var local = parsed.Value!.Value;
        var remindAt = new DateTimeOffset(local, time.LocalTimeZone.GetUtcOffset(local));
        var title = name.Length > 0 ? name : DefaultTitle;
        IReadOnlyList<ResultItem> addItem =
        [
            new ResultItem
            {
                Id = "add",
                Title = $"{ReminderFormat.FormatDateTime(remindAt, time.LocalTimeZone)}（{ReminderFormat.FormatRemaining(remindAt, now)}）",
                Subtitle = $"{title} ・ Enter で登録",
                Actions =
                [
                    new ItemAction("登録", _ =>
                    {
                        store.Add(title, remindAt);
                        return ValueTask.FromResult(ActionResult.Close);
                    }),
                ],
            },
        ];
        return ValueTask.FromResult(addItem);
    }

    private IReadOnlyList<ResultItem> ListItems(string prefix, DateTimeOffset now)
    {
        var entries = store.List();
        if (entries.Count == 0)
        {
            return
            [
                new ResultItem
                {
                    Id = "empty",
                    Title = "リマインダーはない",
                    Subtitle = "例: remind +30m 会議 / remind 11h30 昼休み / remind +1d;9h 確認",
                },
            ];
        }

        return
        [
            .. entries.Select(entry => new ResultItem
            {
                Id = "item:" + entry.Id,
                Title = $"{ReminderFormat.FormatDateTime(entry.RemindAt, time.LocalTimeZone)}（{ReminderFormat.FormatRemaining(entry.RemindAt, now)}）",
                Subtitle = $"{entry.Title} ・ Ctrl+Enter で削除",
                Actions =
                [
                    ItemAction.CopyText(entry.Title, "名前をコピー"),
                    new ItemAction("削除", _ =>
                    {
                        store.Remove(entry.Id);
                        return ValueTask.FromResult(ActionResult.SetQuery(prefix));
                    }),
                ],
            }),
        ];
    }

    private static (string Schedule, string Name) SplitScheduleAndName(string text)
    {
        var split = -1;
        for (var i = 0; i < text.Length; i++)
        {
            if (char.IsWhiteSpace(text[i]))
            {
                split = i;
                break;
            }
        }
        return split < 0
            ? (text, "")
            : (text[..split], text[(split + 1)..].Trim());
    }
}

/// <summary>リマインダーの日時と残り時間の表示を整える。</summary>
internal static class ReminderFormat
{
    private static readonly string[] DayNames = ["日", "月", "火", "水", "木", "金", "土"];

    public static string FormatDateTime(DateTimeOffset target, TimeZoneInfo timeZone)
    {
        var local = TimeZoneInfo.ConvertTime(target, timeZone);
        var dow = DayNames[(int)local.DayOfWeek];
        return $"{local:yyyy/MM/dd} ({dow}) {local:HH:mm}";
    }

    public static string FormatRemaining(DateTimeOffset target, DateTimeOffset now)
    {
        var diff = target - now;
        if (diff <= TimeSpan.Zero)
        {
            return "まもなく";
        }
        var totalMinutes = (int)Math.Ceiling(diff.TotalMinutes);
        if (totalMinutes < 60)
        {
            return $"あと {totalMinutes}分";
        }
        var totalHours = totalMinutes / 60;
        var minutes = totalMinutes % 60;
        if (totalHours < 24)
        {
            return minutes == 0 ? $"あと {totalHours}時間" : $"あと {totalHours}時間{minutes}分";
        }
        var days = totalHours / 24;
        var hours = totalHours % 24;
        return hours == 0 ? $"あと {days}日" : $"あと {days}日{hours}時間";
    }
}
