using System.Text.Json;
using System.Text.Json.Nodes;

namespace Chuo.Core.Settings;

/// <summary>アプリが書き込み、手でも編集できる JSON のファイルの読み書き（設計 9 章）。</summary>
internal static class JsonFile
{
    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    // 手で編集したときのために、コメントと末尾のカンマを許す
    private static readonly JsonDocumentOptions ReadOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>最上位のオブジェクトを読む。ファイルがないか空なら、空のオブジェクトを返す。</summary>
    public static JsonObject ReadObject(string path, JsonNodeOptions nodeOptions)
    {
        if (!File.Exists(path))
        {
            return new JsonObject(nodeOptions);
        }
        var text = File.ReadAllText(path);
        if (string.IsNullOrWhiteSpace(text))
        {
            return new JsonObject(nodeOptions);
        }
        return JsonNode.Parse(text, nodeOptions, ReadOptions) as JsonObject
            ?? throw new InvalidDataException($"{path} の最上位がオブジェクトではない");
    }

    /// <summary>書き込みの途中で壊れたファイルを読まれないよう、一時ファイルに書いてから置き換える。</summary>
    public static void WriteAtomic(string path, JsonNode root)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, root.ToJsonString(WriteOptions));
        File.Move(temporary, path, overwrite: true);
    }
}
