using Chuo.Abstractions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Chuo.Plugins.Reminders;

/// <summary>
/// 登録されたリマインダーの時刻を監視し、時刻になったら通知を出す。
/// PC のスリープや終了中に時刻を過ぎたものも、起動・復帰時にそのまま通知する。
/// </summary>
public sealed class ReminderScheduler(
    ReminderStore store, INotifier notifier, IOptionsMonitor<ReminderOptions> options,
    TimeProvider time, ILogger<ReminderScheduler> logger) : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            using var timer = new PeriodicTimer(PollInterval, time);
            do
            {
                FireDue();
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    internal void FireDue()
    {
        var now = time.GetLocalNow();
        if (store.PeekNextDue() is not { } next || next > now)
        {
            return;
        }

        IReadOnlyList<ReminderEntry> due;
        try
        {
            due = store.TakeDue(now);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "リマインダーの読み書きに失敗した");
            return;
        }

        var playSound = options.CurrentValue.PlaySound;
        foreach (var entry in due)
        {
            var overdue = now - entry.RemindAt >= TimeSpan.FromMinutes(2);
            var message = overdue
                ? $"{ReminderFormat.FormatDateTime(entry.RemindAt, time.LocalTimeZone)} のリマインダー（予定時刻を過ぎています）"
                : $"{TimeZoneInfo.ConvertTime(entry.RemindAt, time.LocalTimeZone):HH:mm} のリマインダー";

            NotificationAction[] actions =
            [
                new("+5分", () => Snooze(entry.Title, TimeSpan.FromMinutes(5))),
                new("+15分", () => Snooze(entry.Title, TimeSpan.FromMinutes(15))),
            ];

            logger.LogInformation("リマインダー「{Title}」（{RemindAt}）を通知する", entry.Title, entry.RemindAt);
            notifier.Show(new Notification(entry.Title, message, actions, playSound));
        }
    }

    private void Snooze(string title, TimeSpan delay)
    {
        var next = time.GetLocalNow() + delay;
        store.Add(title, next);
        logger.LogInformation("リマインダー「{Title}」を {Minutes} 分スヌーズした（{Next}）", title, (int)delay.TotalMinutes, next);
    }
}
