using System.ComponentModel.DataAnnotations;
using Chuo.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace Chuo.Plugins.Reminders;

public sealed class ReminderOptions
{
    [Display(Name = "既定の月", Order = 0), Range(1, 12)]
    public int DefaultMonth { get; set; } = 1;

    [Display(Name = "既定の日", Order = 1), Range(1, 31)]
    public int DefaultDay { get; set; } = 1;

    [Display(Name = "既定の時", Order = 2), Range(0, 23)]
    public int DefaultHour { get; set; } = 9;

    [Display(Name = "既定の分", Order = 3), Range(0, 59)]
    public int DefaultMinute { get; set; } = 0;

    [Display(Name = "通知音を鳴らす", Order = 4)]
    public bool PlaySound { get; set; } = true;

    internal ScheduleDefaults ToDefaults() => new(
        Math.Clamp(DefaultMonth, 1, 12),
        Math.Clamp(DefaultDay, 1, 31),
        Math.Clamp(DefaultHour, 0, 23),
        Math.Clamp(DefaultMinute, 0, 59));
}

/// <summary>指定した日時に通知を出すリマインダー。</summary>
public sealed class ReminderPlugin : IChuoPlugin
{
    public void ConfigureServices(IServiceCollection services, IPluginContext context)
    {
        services.AddPluginSettings<ReminderOptions>(context);
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton(new ReminderStore(Path.Combine(context.DataDirectory, "reminders.json")));
        services.AddHostedService<ReminderScheduler>();
    }
}
