namespace Chuo.Plugins.Reminders;

/// <summary>省略した単位を補う既定の値（既定：1月/1日/9時/0分）。</summary>
public readonly record struct ScheduleDefaults(int Month = 1, int Day = 1, int Hour = 9, int Minute = 0);

/// <summary>スケジュール指定の解釈結果。</summary>
public readonly record struct ScheduleParseResult(DateTime? Value, string? Error)
{
    public bool IsSuccess => Value is not null;
}

/// <summary>
/// リマインダーのスケジュール指定を解釈する（設計 7.2）。
/// <para>
/// <c>+</c> なしは現在時刻から見て次の日時、<c>+</c> ありは現在時刻への加算を表す。
/// <c>;</c> で分割して左から順に適用できる（例：<c>+1d;11h30m</c>）。
/// 単位は <c>y</c>（年）、<c>M</c>（月）、<c>d</c>（日）、<c>h</c>（時）、<c>m</c>（分）と曜日（<c>Mon</c>〜<c>Sun</c>）。
/// </para>
/// </summary>
public static class ScheduleParser
{
    private enum Unit
    {
        Year = 0,
        Month = 1,
        Day = 2,
        Hour = 3,
        Minute = 4,
    }

    private sealed class Segment
    {
        public bool IsRelative { get; init; }
        public DayOfWeek? Weekday { get; set; }
        public int? Year { get; set; }
        public int? Month { get; set; }
        public int? Day { get; set; }
        public int? Hour { get; set; }
        public int? Minute { get; set; }

        public int HighestRank =>
            Year.HasValue ? 0
            : Month.HasValue ? 1
            : Day.HasValue || Weekday.HasValue ? 2
            : Hour.HasValue ? 3
            : 4;

        public int LowestRank =>
            Minute.HasValue ? 4
            : Hour.HasValue ? 3
            : Day.HasValue || Weekday.HasValue ? 2
            : Month.HasValue ? 1
            : 0;
    }

    private static readonly (string Name, DayOfWeek Day)[] Weekdays =
    [
        ("monday", DayOfWeek.Monday),
        ("tuesday", DayOfWeek.Tuesday),
        ("wednesday", DayOfWeek.Wednesday),
        ("thursday", DayOfWeek.Thursday),
        ("friday", DayOfWeek.Friday),
        ("saturday", DayOfWeek.Saturday),
        ("sunday", DayOfWeek.Sunday),
        ("mon", DayOfWeek.Monday),
        ("tue", DayOfWeek.Tuesday),
        ("wed", DayOfWeek.Wednesday),
        ("thu", DayOfWeek.Thursday),
        ("fri", DayOfWeek.Friday),
        ("sat", DayOfWeek.Saturday),
        ("sun", DayOfWeek.Sunday),
    ];

    public static ScheduleParseResult Parse(string expression, DateTime now, ScheduleDefaults defaults)
    {
        if (string.IsNullOrWhiteSpace(expression))
        {
            return Fail("日時を指定してください");
        }

        var rawParts = expression.Split(';');
        var segments = new List<Segment>(rawParts.Length);
        foreach (var raw in rawParts)
        {
            if (raw.Length == 0)
            {
                return Fail("日時の書式が正しくありません");
            }
            var (seg, error) = ParseSegment(raw);
            if (error is not null)
            {
                return Fail(error);
            }
            segments.Add(seg!);
        }

        // 連続する絶対指定で、後ろの単位が前より細かいもの（Mon;14h や 1d;11h30m など）は1つにまとめて、
        // Mon14h と Mon;14h が同じ結果になるようにする
        var merged = new List<Segment>(segments.Count);
        foreach (var seg in segments)
        {
            if (merged.Count > 0 && CanMerge(merged[^1], seg))
            {
                MergeInto(merged[^1], seg);
            }
            else
            {
                merged.Add(seg);
            }
        }

        // 最小単位は1分
        var baseNow = new DateTime(now.Year, now.Month, now.Day, now.Hour, now.Minute, 0, now.Kind);
        var current = baseNow;
        var anchored = false;
        var timeSpecified = false;

        foreach (var seg in merged)
        {
            var next = seg.IsRelative
                ? ApplyRelative(current, seg, defaults, timeSpecified)
                : ApplyAbsolute(current, baseNow, seg, defaults, anchored);
            if (!next.IsSuccess)
            {
                return next;
            }
            current = next.Value!.Value;
            anchored = true;
            if (!seg.IsRelative || seg.Hour.HasValue || seg.Minute.HasValue)
            {
                timeSpecified = true;
            }
        }

        if (current <= baseNow)
        {
            return Fail("過去の日時は指定できません");
        }
        return new ScheduleParseResult(current, null);
    }

    private static bool CanMerge(Segment prev, Segment next) =>
        !prev.IsRelative && !next.IsRelative && next.Weekday is null && next.HighestRank > prev.LowestRank;

    private static void MergeInto(Segment target, Segment source)
    {
        target.Year ??= source.Year;
        target.Month ??= source.Month;
        target.Day ??= source.Day;
        target.Hour ??= source.Hour;
        target.Minute ??= source.Minute;
    }

    private static (Segment? Segment, string? Error) ParseSegment(string raw)
    {
        var index = 0;
        var isRelative = false;
        if (raw[0] == '+')
        {
            isRelative = true;
            index = 1;
            if (index >= raw.Length)
            {
                return (null, "+ の後ろに値を指定してください");
            }
        }

        var segment = new Segment { IsRelative = isRelative };

        // 先頭が英字なら曜日を試す
        if (char.IsAsciiLetter(raw[index]))
        {
            if (TryMatchWeekday(raw, index, out var dow, out var nextIndex))
            {
                if (isRelative)
                {
                    return (null, "曜日に + は指定できません");
                }
                segment.Weekday = dow;
                index = nextIndex;
            }
            else
            {
                return (null, "日時の書式が正しくありません");
            }
        }

        Unit? lastUnit = null;
        var hasTerm = false;

        while (index < raw.Length)
        {
            if (!char.IsAsciiDigit(raw[index]))
            {
                return (null, "日時の書式が正しくありません");
            }
            var start = index;
            while (index < raw.Length && char.IsAsciiDigit(raw[index]))
            {
                index++;
            }
            if (!int.TryParse(raw.AsSpan(start, index - start), out var value))
            {
                return (null, "数値が大きすぎます");
            }

            Unit unit;
            if (index < raw.Length)
            {
                if (!TryParseUnit(raw[index], out unit))
                {
                    return (null, $"不明な単位 '{raw[index]}'");
                }
                index++;
            }
            else
            {
                // 単位を省略した場合：先頭なら時、直前に単位があればその次の単位（y→M→d→h→m）
                if (lastUnit is null)
                {
                    unit = Unit.Hour;
                }
                else if (lastUnit.Value < Unit.Minute)
                {
                    unit = lastUnit.Value + 1;
                }
                else
                {
                    return (null, "分の後ろで単位は省略できません");
                }
            }

            if (segment.Weekday.HasValue && unit < Unit.Hour)
            {
                return (null, "曜日と一緒に年・月・日は指定できません");
            }

            if (!TrySetUnit(segment, unit, value))
            {
                return (null, "同じ単位が重複しています");
            }
            lastUnit = unit;
            hasTerm = true;
        }

        if (!segment.Weekday.HasValue && !hasTerm)
        {
            return (null, "日時の書式が正しくありません");
        }
        return (segment, null);
    }

    private static bool TryMatchWeekday(string raw, int index, out DayOfWeek day, out int nextIndex)
    {
        var span = raw.AsSpan(index);
        foreach (var (name, dow) in Weekdays)
        {
            if (span.StartsWith(name, StringComparison.OrdinalIgnoreCase)
                && (span.Length == name.Length || !char.IsAsciiLetter(span[name.Length])))
            {
                day = dow;
                nextIndex = index + name.Length;
                return true;
            }
        }
        day = default;
        nextIndex = index;
        return false;
    }

    /// <summary>月（M）と分（m）のみ大文字・小文字を区別する。</summary>
    private static bool TryParseUnit(char ch, out Unit unit)
    {
        switch (ch)
        {
            case 'y' or 'Y':
                unit = Unit.Year;
                return true;
            case 'M':
                unit = Unit.Month;
                return true;
            case 'd' or 'D':
                unit = Unit.Day;
                return true;
            case 'h' or 'H':
                unit = Unit.Hour;
                return true;
            case 'm':
                unit = Unit.Minute;
                return true;
            default:
                unit = default;
                return false;
        }
    }

    private static bool TrySetUnit(Segment segment, Unit unit, int value)
    {
        switch (unit)
        {
            case Unit.Year:
                if (segment.Year.HasValue) return false;
                segment.Year = value;
                return true;
            case Unit.Month:
                if (segment.Month.HasValue) return false;
                segment.Month = value;
                return true;
            case Unit.Day:
                if (segment.Day.HasValue) return false;
                segment.Day = value;
                return true;
            case Unit.Hour:
                if (segment.Hour.HasValue) return false;
                segment.Hour = value;
                return true;
            case Unit.Minute:
                if (segment.Minute.HasValue) return false;
                segment.Minute = value;
                return true;
            default:
                return false;
        }
    }

    private static ScheduleParseResult ApplyRelative(
        DateTime current, Segment seg, ScheduleDefaults defaults, bool timeSpecified)
    {
        var hasDate = seg.Year.HasValue || seg.Month.HasValue || seg.Day.HasValue;
        var hasTime = seg.Hour.HasValue || seg.Minute.HasValue;

        var year = current.Year;
        var month = current.Month;
        var day = current.Day;

        // 年や月の加算は日を変更しない。結果の日が存在しない（例：1月31日 + 1M → 2月31日）ならエラーにする
        if (seg.Year.HasValue || seg.Month.HasValue)
        {
            var totalMonths = (long)year * 12 + (month - 1) + (seg.Year ?? 0L) * 12 + (seg.Month ?? 0L);
            if (totalMonths < 12 || totalMonths >= 10000L * 12)
            {
                return Fail("年が範囲外です");
            }
            year = (int)(totalMonths / 12);
            month = (int)(totalMonths % 12) + 1;
            if (day > DateTime.DaysInMonth(year, month))
            {
                return Fail($"{year}年{month}月{day}日は存在しません");
            }
        }

        // 日付のみの相対指定（+1d、+3M、+1y など）は時刻に既定値を使う。時・分を含む相対指定は現在時刻に加算する
        var hour = hasDate && !hasTime && !timeSpecified ? defaults.Hour : current.Hour;
        var minute = hasDate && !hasTime && !timeSpecified ? defaults.Minute : current.Minute;

        try
        {
            var dt = new DateTime(year, month, day, hour, minute, 0, current.Kind);
            if (seg.Day is { } d) dt = dt.AddDays(d);
            if (seg.Hour is { } h) dt = dt.AddHours(h);
            if (seg.Minute is { } m) dt = dt.AddMinutes(m);
            return new ScheduleParseResult(dt, null);
        }
        catch (ArgumentOutOfRangeException)
        {
            return Fail("日時が範囲外です");
        }
    }

    private static ScheduleParseResult ApplyAbsolute(
        DateTime current, DateTime baseNow, Segment seg, ScheduleDefaults defaults, bool anchored)
    {
        if (seg.Year is < 1 or > 9999) return Fail("年は 1〜9999 で指定してください");
        if (seg.Month is < 1 or > 12) return Fail("月は 1〜12 で指定してください");
        if (seg.Day is < 1 or > 31) return Fail("日は 1〜31 で指定してください");
        if (seg.Hour is < 0 or > 23) return Fail("時は 0〜23 で指定してください");
        if (seg.Minute is < 0 or > 59) return Fail("分は 0〜59 で指定してください");

        if (seg.Weekday is { } dow)
        {
            var hour = seg.Hour ?? defaults.Hour;
            var minute = seg.Minute ?? defaults.Minute;
            var startDate = anchored ? current.Date : baseNow.Date;
            var daysUntil = ((int)dow - (int)startDate.DayOfWeek + 7) % 7;
            var candidate = startDate.AddDays(daysUntil).AddHours(hour).AddMinutes(minute);
            if (!anchored && candidate <= baseNow)
            {
                candidate = candidate.AddDays(7);
            }
            return new ScheduleParseResult(candidate, null);
        }

        if (anchored)
        {
            // 前のセグメントで日時が決まっているときは、指定した最上位の単位から下を上書きし、省いた下位単位は既定値を使う
            var highest = seg.HighestRank;
            var y = seg.Year ?? current.Year;
            var mo = seg.Month ?? (highest < 1 ? defaults.Month : current.Month);
            var d = seg.Day ?? (highest < 2 ? defaults.Day : current.Day);
            var h = seg.Hour ?? (highest < 3 ? defaults.Hour : current.Hour);
            var mi = seg.Minute ?? (highest < 4 ? defaults.Minute : current.Minute);
            return CreateDate(y, mo, d, h, mi, current.Kind);
        }

        // 最初の絶対指定：「現在時刻から見て次の…」を求める
        if (seg.Year is { } year)
        {
            return CreateDate(
                year,
                seg.Month ?? defaults.Month,
                seg.Day ?? defaults.Day,
                seg.Hour ?? defaults.Hour,
                seg.Minute ?? defaults.Minute,
                baseNow.Kind);
        }

        if (seg.Month is { } month)
        {
            var d = seg.Day ?? defaults.Day;
            var h = seg.Hour ?? defaults.Hour;
            var mi = seg.Minute ?? defaults.Minute;
            var first = CreateDate(baseNow.Year, month, d, h, mi, baseNow.Kind);
            if (!first.IsSuccess || first.Value!.Value > baseNow)
            {
                return first;
            }
            return CreateDate(baseNow.Year + 1, month, d, h, mi, baseNow.Kind);
        }

        if (seg.Day is { } day)
        {
            var h = seg.Hour ?? defaults.Hour;
            var mi = seg.Minute ?? defaults.Minute;
            var first = CreateDate(baseNow.Year, baseNow.Month, day, h, mi, baseNow.Kind);
            if (!first.IsSuccess || first.Value!.Value > baseNow)
            {
                return first;
            }
            var (ny, nm) = baseNow.Month == 12 ? (baseNow.Year + 1, 1) : (baseNow.Year, baseNow.Month + 1);
            return CreateDate(ny, nm, day, h, mi, baseNow.Kind);
        }

        if (seg.Hour is { } absHour)
        {
            var mi = seg.Minute ?? defaults.Minute;
            var candidate = new DateTime(baseNow.Year, baseNow.Month, baseNow.Day, absHour, mi, 0, baseNow.Kind);
            if (candidate <= baseNow)
            {
                candidate = candidate.AddDays(1);
            }
            return new ScheduleParseResult(candidate, null);
        }

        var absMinute = seg.Minute!.Value;
        var minuteCandidate = new DateTime(baseNow.Year, baseNow.Month, baseNow.Day, baseNow.Hour, absMinute, 0, baseNow.Kind);
        if (minuteCandidate <= baseNow)
        {
            minuteCandidate = minuteCandidate.AddHours(1);
        }
        return new ScheduleParseResult(minuteCandidate, null);
    }

    private static ScheduleParseResult CreateDate(int year, int month, int day, int hour, int minute, DateTimeKind kind)
    {
        if (year is < 1 or > 9999)
        {
            return Fail("年が範囲外です");
        }
        if (day > DateTime.DaysInMonth(year, month))
        {
            return Fail($"{year}年{month}月{day}日は存在しません");
        }
        return new ScheduleParseResult(new DateTime(year, month, day, hour, minute, 0, kind), null);
    }

    private static ScheduleParseResult Fail(string message) => new(null, message);
}
