using System.ComponentModel.DataAnnotations;
using Chuo.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace Chuo.Plugins.DailyFolder;

public sealed class DailyFolderOptions
{
    [Display(Name = "フォルダの置き場所", Description = "この中に日付ごとのフォルダを作る", Order = 0), FolderPath]
    public string Root { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Daily");

    [Display(Name = "フォルダの名前", Description = "日付の書式（例: yyyy-MM-dd）。この書式に合う名前のフォルダだけを扱う", Order = 1)]
    public string NameFormat { get; set; } = "yyyy-MM-dd";

    [Display(Name = "残す日数", Description = "prune で、今日を含めてこの日数より古いフォルダを削除する", Order = 2), Range(1, 3650)]
    public int RetentionDays { get; set; } = 30;
}

/// <summary>その日のフォルダを開き、古いフォルダを掃除する。</summary>
public sealed class DailyFolderPlugin : IChuoPlugin
{
    public void ConfigureServices(IServiceCollection services, IPluginContext context)
    {
        services.AddPluginSettings<DailyFolderOptions>(context);
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton(new KeptFolders(Path.Combine(context.DataDirectory, "kept.json")));
        services.AddSingleton<DailyFolders>();
    }
}
