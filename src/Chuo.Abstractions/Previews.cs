namespace Chuo.Abstractions;

/// <summary>項目のプレビュー。描画はホストが行う。</summary>
public abstract record Preview;

public sealed record TextPreview(string Text) : Preview;

public sealed record ImagePreview(IconSource Image) : Preview;

/// <summary>ファイルの内容のプレビュー。ホストが種類に応じて表示する。</summary>
public sealed record FilePreview(string Path) : Preview;

/// <summary>名前と値の組の一覧。</summary>
public sealed record PropertiesPreview(IReadOnlyList<KeyValuePair<string, string>> Properties) : Preview;
