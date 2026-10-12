using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Chuo.Abstractions;

/// <summary>
/// プラグインの入り口。プラグインのアセンブリに1つだけ置く（なくてもよい）。
/// ホストはプラグインごとにミニ汎用ホストを作り、ここで登録したサービスからプロバイダを生成する。
/// </summary>
public interface IChuoPlugin
{
    /// <summary>プラグインのサービスを登録する。常駐処理は <c>AddHostedService</c> で登録する。</summary>
    void ConfigureServices(IServiceCollection services, IPluginContext context);
}

/// <summary>プラグインに渡される、そのプラグインについての情報。</summary>
public interface IPluginContext
{
    /// <summary><c>plugin.json</c> の <c>id</c>。</summary>
    string PluginId { get; }

    /// <summary>プラグインのアセンブリがあるフォルダ。</summary>
    string PluginDirectory { get; }

    /// <summary>プラグインが自由に読み書きできるデータのフォルダ。初めて使うときに作られる。</summary>
    string DataDirectory { get; }

    /// <summary>
    /// <c>settings.json</c> の、このプラグインの設定のセクション（<c>plugins.&lt;id&gt;.settings</c>）。
    /// 設定画面に出すには <see cref="PluginSettingsServiceCollectionExtensions.AddPluginSettings"/> で登録する。
    /// </summary>
    IConfiguration Configuration { get; }
}
