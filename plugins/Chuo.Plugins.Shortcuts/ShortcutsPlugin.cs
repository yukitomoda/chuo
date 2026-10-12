using System.ComponentModel.DataAnnotations;
using Chuo.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace Chuo.Plugins.Shortcuts;

public sealed class ShortcutsOptions
{
    // 既定値を入れると、設定の値がその後ろに追加されてしまう（構成のバインドの仕様）ので空にしておく
    [Display(Name = "対象のフォルダ", Description = "この中のショートカットと実行ファイルを検索する", Order = 0), FolderPath]
    public List<string> Folders { get; set; } = [];

    [Display(Name = "サブフォルダも対象にする", Order = 1)]
    public bool IncludeSubfolders { get; set; } = true;
}

public sealed class ShortcutsPlugin : IChuoPlugin
{
    public void ConfigureServices(IServiceCollection services, IPluginContext context) =>
        services.AddPluginSettings<ShortcutsOptions>(context);
}
