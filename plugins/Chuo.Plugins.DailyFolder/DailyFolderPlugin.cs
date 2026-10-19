using System.ComponentModel.DataAnnotations;
using Chuo.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace Chuo.Plugins.DailyFolder;

public sealed class DailyFolderOptions
{
    [Display(Name = "置き場所", Order = 0), FolderPath]
    public string Root { get; set; } = DefaultRoot;

    [Display(Name = "名前の書式", Order = 1)]
    public string NameFormat { get; set; } = "yyyy-MM-dd";

    [Display(Name = "残す日数", Description = "これより古いものを prune で削除", Order = 2), Range(1, 3650)]
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
