using Microsoft.Extensions.DependencyInjection;

namespace Chuo.Abstractions;

/// <summary>プラグインの設定を登録する。</summary>
public static class PluginSettingsServiceCollectionExtensions
{
    /// <summary>
    /// 設定画面に出す設定のクラスを登録する。<c>settings.json</c> の値を <typeparamref name="TOptions"/> に結び付け、
    /// <c>IOptionsMonitor&lt;TOptions&gt;</c> で受け取れるようにする。設定画面での変更はその場で反映される。
    /// </summary>
    /// <remarks>
    /// 設定画面に出せるプロパティの型は、bool、整数、小数、string、列挙型、<c>List&lt;string&gt;</c>。
    /// 表示名や説明は <c>[Display]</c>、値の範囲は <c>[Range]</c>、パスの選択は <see cref="FolderPathAttribute"/> と
    /// <see cref="FilePathAttribute"/> で指定する。
    /// </remarks>
    public static IServiceCollection AddPluginSettings<TOptions>(this IServiceCollection services, IPluginContext context)
        where TOptions : class, new()
    {
        services.Configure<TOptions>(context.Configuration);
        services.AddSingleton(new PluginSettingsDescriptor(typeof(TOptions)));
        return services;
    }
}

/// <summary>設定画面に出す設定のクラス。<see cref="PluginSettingsServiceCollectionExtensions.AddPluginSettings"/> が登録する。</summary>
public sealed record PluginSettingsDescriptor(Type OptionsType);

/// <summary>string または <c>List&lt;string&gt;</c> のプロパティが、フォルダのパスであることを示す。設定画面にフォルダの選択を出す。</summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class FolderPathAttribute : Attribute;

/// <summary>string または <c>List&lt;string&gt;</c> のプロパティが、ファイルのパスであることを示す。設定画面にファイルの選択を出す。</summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class FilePathAttribute : Attribute;
