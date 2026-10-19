using System.ComponentModel.DataAnnotations;
using Chuo.Abstractions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Chuo.Plugins.Shortcuts;

public sealed class ShortcutsOptions
{
    // 既定値を入れると、設定の値がその後ろに追加されてしまう（構成のバインドの仕様）ので空にしておく
    [Display(Name = "対象のフォルダ")]
    public List<ShortcutFolder> Folders { get; set; } = [];
}

/// <summary>対象のフォルダ1つ分。</summary>
public sealed class ShortcutFolder
{
    [Display(Name = "フォルダ", Order = 0), FolderPath]
    public string Path { get; set; } = "";

    [Display(Name = "サブフォルダも対象にする", Order = 1)]
    public bool IncludeSubfolders { get; set; } = true;
}

public sealed class ShortcutsPlugin : IChuoPlugin
{
    public void ConfigureServices(IServiceCollection services, IPluginContext context)
    {
        services.AddPluginSettings<ShortcutsOptions>(context);
        services.PostConfigure<ShortcutsOptions>(options => ReadPlainFolders(options, context.Configuration));
    }

    /// <summary>
    /// 以前はフォルダを文字列のリストで持っていたので、文字列だけの要素はフォルダのパスとして読む。
    /// 構成のバインドは文字列の要素を飛ばすので、そういう要素があれば一覧を作り直す。
    /// （<c>[TypeConverter]</c> は型名からの解決がプラグインのアセンブリを見つけられず、使えない）
    /// </summary>
    public static void ReadPlainFolders(ShortcutsOptions options, IConfiguration configuration)
    {
        var elements = configuration.GetSection("folders").GetChildren().ToList();
        if (elements.All(e => e.Value is null))
        {
            return;
        }
        options.Folders = [.. elements.Select(e => e.Value is { } path ? new ShortcutFolder { Path = path } : e.Get<ShortcutFolder>() ?? new())];
    }
}
