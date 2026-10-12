using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Reflection;
using System.Text.Json.Nodes;
using Chuo.Abstractions;
using Microsoft.Extensions.Configuration;

namespace Chuo.Core.Settings;

/// <summary>設定画面での入力の種類。</summary>
public enum SettingKind
{
    Boolean,
    Integer,
    Number,
    Text,
    Choice,
    Folder,
    File,
    TextList,
    FolderList,
    FileList,
}

/// <summary>設定のクラスのプロパティ1つ分。設定画面の入力欄1つに対応する。</summary>
/// <param name="Key">設定のセクションの中でのキー（プロパティ名の先頭を小文字にしたもの）。</param>
/// <param name="Choices">列挙型の値の名前。</param>
/// <param name="DefaultValue">設定のクラスを new したときの値。</param>
public sealed record SettingField(
    string Key, string DisplayName, string? Description, int Order, SettingKind Kind,
    double? Minimum, double? Maximum, IReadOnlyList<string> Choices, object? DefaultValue)
{
    public bool IsList => Kind is SettingKind.TextList or SettingKind.FolderList or SettingKind.FileList;

    /// <summary>今の値を読む。設定にないときは既定値を返す。</summary>
    public object? Read(IConfiguration section)
    {
        if (IsList)
        {
            var children = section.GetSection(Key).GetChildren().ToList();
            return children.Count > 0 || section.GetSection(Key).Value is not null
                ? children.Select(c => c.Value ?? "").ToList()
                : (DefaultValue as IEnumerable<string>)?.ToList() ?? [];
        }

        var text = section[Key];
        if (text is null)
        {
            return DefaultValue;
        }
        return Kind switch
        {
            SettingKind.Boolean => bool.TryParse(text, out var b) ? b : DefaultValue,
            SettingKind.Integer => long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l) ? l : DefaultValue,
            SettingKind.Number => double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : DefaultValue,
            _ => text,
        };
    }

    /// <summary>書き込む値を JSON にする。</summary>
    public JsonNode? ToJson(object? value) => value switch
    {
        null => null,
        IEnumerable<string> list when IsList => new JsonArray([.. list.Select(s => (JsonNode?)JsonValue.Create(s))]),
        bool b => JsonValue.Create(b),
        long l => JsonValue.Create(l),
        int i => JsonValue.Create(i),
        double d => JsonValue.Create(d),
        decimal m => Kind == SettingKind.Integer ? JsonValue.Create((long)m) : JsonValue.Create((double)m),
        _ => JsonValue.Create(value.ToString()),
    };
}

/// <summary>プラグインの設定のクラスから、設定画面の入力欄の一覧を作る。</summary>
public static class SettingsSchema
{
    public static IReadOnlyList<SettingField> Describe(Type optionsType) => Describe(optionsType, out _);

    /// <summary>
    /// 設定画面に出せるプロパティを、表示の順に返す。対応していない型のプロパティは <paramref name="unsupported"/> に返す。
    /// </summary>
    public static IReadOnlyList<SettingField> Describe(Type optionsType, out IReadOnlyList<string> unsupported)
    {
        var defaults = Activator.CreateInstance(optionsType);
        var fields = new List<SettingField>();
        var skipped = new List<string>();
        foreach (var property in optionsType.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (property.GetMethod is null || property.SetMethod?.IsPublic != true || property.GetIndexParameters().Length > 0)
            {
                continue;
            }
            if (GetKind(property) is not { } kind)
            {
                skipped.Add($"{property.Name}（{property.PropertyType.Name}）");
                continue;
            }

            var display = property.GetCustomAttribute<DisplayAttribute>();
            var range = property.GetCustomAttribute<RangeAttribute>();
            fields.Add(new SettingField(
                Key: char.ToLowerInvariant(property.Name[0]) + property.Name[1..],
                DisplayName: display?.GetName() ?? property.Name,
                Description: display?.GetDescription(),
                Order: display?.GetOrder() ?? 0,
                Kind: kind,
                Minimum: range is null ? null : Convert.ToDouble(range.Minimum, CultureInfo.InvariantCulture),
                Maximum: range is null ? null : Convert.ToDouble(range.Maximum, CultureInfo.InvariantCulture),
                Choices: property.PropertyType.IsEnum ? Enum.GetNames(property.PropertyType) : [],
                DefaultValue: Normalize(property.GetValue(defaults))));
        }
        unsupported = skipped;
        // 並び順が同じものは、宣言の順を保つ
        return [.. fields.Select((f, i) => (f, i)).OrderBy(p => p.f.Order).ThenBy(p => p.i).Select(p => p.f)];
    }

    private static SettingKind? GetKind(PropertyInfo property)
    {
        var type = property.PropertyType;
        var folder = property.IsDefined(typeof(FolderPathAttribute));
        var file = property.IsDefined(typeof(FilePathAttribute));
        if (type == typeof(bool))
        {
            return SettingKind.Boolean;
        }
        if (type == typeof(int) || type == typeof(long))
        {
            return SettingKind.Integer;
        }
        if (type == typeof(double) || type == typeof(float) || type == typeof(decimal))
        {
            return SettingKind.Number;
        }
        if (type == typeof(string))
        {
            return folder ? SettingKind.Folder : file ? SettingKind.File : SettingKind.Text;
        }
        if (type.IsEnum)
        {
            return SettingKind.Choice;
        }
        if (type == typeof(List<string>) || type == typeof(IList<string>) || type == typeof(string[]))
        {
            return folder ? SettingKind.FolderList : file ? SettingKind.FileList : SettingKind.TextList;
        }
        return null;
    }

    /// <summary>既定値を、<see cref="SettingField.Read"/> が返すのと同じ型にそろえる。</summary>
    private static object? Normalize(object? value) => value switch
    {
        int i => (long)i,
        float f => (double)f,
        decimal m => (double)m,
        Enum e => e.ToString(),
        IEnumerable<string> list => list.ToList(),
        _ => value,
    };
}
