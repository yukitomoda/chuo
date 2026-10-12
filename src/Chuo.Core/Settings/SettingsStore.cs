using System.Text.Json.Nodes;

namespace Chuo.Core.Settings;

/// <summary>
/// 設定画面での変更を <c>settings.json</c> に書き込む。
/// 書き込むと構成の再読み込みが走り、<c>IOptionsMonitor</c> を通じて本体とプラグインに反映される。
/// </summary>
public sealed class SettingsStore(string path)
{
    private static readonly JsonNodeOptions NodeOptions = new() { PropertyNameCaseInsensitive = true };

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
            var root = JsonFile.ReadObject(path, NodeOptions);
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
            JsonFile.WriteAtomic(path, root);
        }
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
