namespace Chuo.Abstractions;

/// <summary>
/// カタログ型のプロバイダであることを示す。ここで指定する値は既定値で、ユーザーが設定画面で上書きできる。
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class CatalogProviderAttribute : Attribute
{
    /// <summary>設定画面に出す名前。プラグインにプロバイダが複数あるときに使う。なければクラス名。</summary>
    public string? Title { get; set; }

    /// <summary>このプロバイダだけを検索するときのキーワード（例: <c>f</c>）。</summary>
    public string? Keyword { get; set; }

    /// <summary>キーワードを付けない全体検索にも含めるか。</summary>
    public bool IncludeInGlobal { get; set; } = true;
}

/// <summary>
/// ライブクエリ型のプロバイダであることを示す。ここで指定する値は既定値で、ユーザーが設定画面で上書きできる。
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class LiveQueryProviderAttribute : Attribute
{
    /// <summary>設定画面に出す名前。プラグインにプロバイダが複数あるときに使う。なければクラス名。</summary>
    public string? Title { get; set; }

    /// <summary>このプロバイダを呼び出すキーワード（例: <c>g</c>）。</summary>
    public string? Keyword { get; set; }

    /// <summary>入力全体がこの正規表現に一致したときは、キーワードがなくても呼び出す。</summary>
    public string? GlobalTrigger { get; set; }

    /// <summary>この時間を過ぎても結果を返さなければ、キャンセルして結果に含めない。</summary>
    public int TimeoutMs { get; set; } = 1000;
}
