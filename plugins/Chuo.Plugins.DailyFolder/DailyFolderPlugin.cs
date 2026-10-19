using System.ComponentModel.DataAnnotations;
using Chuo.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace Chuo.Plugins.DailyFolder;

public sealed class DailyFolderOptions
{
    [Display(Name = "フォルダの置き場所", Description = "この中に日付ごとのフォルダを作る", Order = 0), FolderPath]
    public string Root { get; set; } = DefaultRoot;

    [Display(Name = "フォルダの名前", Description = "日付の書式（例: yyyy-MM-dd）。この書式に合う名前のフォルダだけを扱う", Order = 1)]
    public string NameFormat { get; set; } = "yyyy-MM-dd";

    [Display(Name = "残す日数", Description = "prune で、今日を含めてこの日数より古いフォルダをごみ箱に移す", Order = 2), Range(1, 3650)]
    public int RetentionDays { get; set; } = 30;

    /// <summary>
    /// 既定の置き場所は、ドキュメントのフォルダの中の Daily。ホームの直下は散らかさない。
    /// Linux でドキュメントのフォルダが決まっていない（ホームが返る）ときは、~/Documents を使う。
    /// </summary>
    public static string DefaultRoot
    {
        get
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            if (documents.Length == 0 || Path.GetFullPath(documents).TrimEnd(Path.DirectorySeparatorChar) == Path.GetFullPath(home).TrimEnd(Path.DirectorySeparatorChar))
            {
                documents = Path.Combine(home, "Documents");
            }
            return Path.Combine(documents, "Daily");
        }
    }
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
