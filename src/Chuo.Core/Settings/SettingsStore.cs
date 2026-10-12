using System.Text.Json;
using System.Text.Json.Nodes;

namespace Chuo.Core.Settings;

/// <summary>
/// 設定画面での変更を <c>settings.json</c> に書き込む。
/// 書き込むと構成の再読み込みが走り、<c>IOptionsMonitor</c> を通じて本体とプラグインに反映される。
/// </summary>
public sealed class SettingsStore(string path)
{
    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static readonly JsonNodeOptions NodeOptions = new() { PropertyNameCaseInsensitive = true };

    private static readonly JsonDocumentOptions ReadOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private readonly Lock _gate = new();

    /// <summary>設定のキーの区切り（構成のキーと同じ）。</summary>
    public const char KeySeparator = ':';

    /// <summary>
    /// 値を書き込む。<paramref name="key"/> は構成のキー（例: <c>plugins:chuo.websearch:settings:urlTemplate</c>）。
    /// <paramref name="value"/> が null ならキーを削除する。キーの大文字と小文字は区別しない（構成と同じ）。
    /// </summary>
    public void Set(string key, JsonNode? value)
    {
        var segments = key.Split(KeySeparator);
        lock (_gate)
        {
            var root = Read();
            var parent = root;
            foreach (var segment in segments[..^1])
            {
                if (parent[segment] is not JsonObject child)
                {
                    if (value is null)
                    {
                        return;
                    }
                    child = new JsonObject(NodeOptions);
                    SetProperty(parent, segment, child);
                }
                parent = child;
            }

            if (value is null)
            {
                RemoveProperty(parent, segments[^1]);
            }
            else
            {
                SetProperty(parent, segments[^1], value);
            }
            Write(root);
        }
    }

    private JsonObject Read()
    {
        if (!File.Exists(path))
        {
            return new JsonObject(NodeOptions);
        }
        var text = File.ReadAllText(path);
        if (string.IsNullOrWhiteSpace(text))
        {
            return new JsonObject(NodeOptions);
        }
        return JsonNode.Parse(text, NodeOptions, ReadOptions) as JsonObject
            ?? throw new InvalidDataException($"{path} の最上位がオブジェクトではない");
    }

    /// <summary>書き込みの途中で壊れたファイルを読まれないよう、一時ファイルに書いてから置き換える。</summary>
    private void Write(JsonObject root)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, root.ToJsonString(WriteOptions));
        File.Move(temporary, path, overwrite: true);
    }

    /// <summary>既存のキーは、大文字と小文字が違っても元の綴りのまま置き換える。</summary>
    private static void SetProperty(JsonObject parent, string name, JsonNode value)
    {
        var existing = FindName(parent, name);
        parent[existing ?? name] = value;
    }

    private static void RemoveProperty(JsonObject parent, string name)
    {
        if (FindName(parent, name) is { } existing)
        {
            parent.Remove(existing);
        }
    }

    private static string? FindName(JsonObject parent, string name) =>
        parent.Select(p => p.Key).FirstOrDefault(k => string.Equals(k, name, StringComparison.OrdinalIgnoreCase));
}
