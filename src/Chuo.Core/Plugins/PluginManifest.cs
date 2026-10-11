using System.Text.Json;
using System.Text.Json.Serialization;
using Chuo.Abstractions;

namespace Chuo.Core.Plugins;

/// <summary>プラグインのフォルダにある <c>plugin.json</c>（要件定義書 3.3）。</summary>
public sealed record PluginManifest(string Id, string Name, string Version, string Entry, string HostApi)
{
    public const string FileName = "plugin.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>フォルダの <c>plugin.json</c> を読む。必須の項目が欠けていれば例外を投げる。</summary>
    public static PluginManifest Load(string directory)
    {
        var path = Path.Combine(directory, FileName);
        using var stream = File.OpenRead(path);
        var raw = JsonSerializer.Deserialize<RawManifest>(stream, JsonOptions)
                  ?? throw new InvalidDataException($"{path} が空です。");
        return new PluginManifest(
            Required(raw.Id, "id"), raw.Name ?? raw.Id!, raw.Version ?? "0.0.0", Required(raw.Entry, "entry"), Required(raw.HostApi, "hostApi"));

        string Required(string? value, string name) =>
            string.IsNullOrWhiteSpace(value) ? throw new InvalidDataException($"{path} に {name} がありません。") : value;
    }

    /// <summary>
    /// <c>hostApi</c> がホスト API のバージョンと合うか。<c>1.x</c> はメジャーが同じなら、
    /// <c>1.2</c> はメジャーが同じでホストのマイナーが 2 以上なら合うとする。
    /// </summary>
    public bool IsCompatible(Version hostApi)
    {
        var parts = HostApi.Split('.');
        if (!int.TryParse(parts[0], out var major) || major != hostApi.Major)
        {
            return false;
        }
        return parts.Length < 2 || parts[1] is "x" or "*" || (int.TryParse(parts[1], out var minor) && minor <= hostApi.Minor);
    }

    public bool IsCompatible() => IsCompatible(Abstractions.HostApi.Version);

    private sealed record RawManifest(
        [property: JsonPropertyName("id")] string? Id,
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("version")] string? Version,
        [property: JsonPropertyName("entry")] string? Entry,
        [property: JsonPropertyName("hostApi")] string? HostApi);
}
