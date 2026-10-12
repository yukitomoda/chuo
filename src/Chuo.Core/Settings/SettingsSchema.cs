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

    /// <summary>設定のクラスのリスト。要素は <see cref="SettingField.ItemFields"/> の値の組。</summary>
    ObjectList,
}

/// <summary>設定のクラスのプロパティ1つ分。設定画面の入力欄1つに対応する。</summary>
/// <param name="Key">設定のセクションの中でのキー（プロパティ名の先頭を小文字にしたもの）。</param>
/// <param name="Choices">列挙型の値の名前。</param>
/// <param name="DefaultValue">
/// 設定のクラスを new したときの値。<see cref="SettingKind.ObjectList"/> では、要素ごとに
/// キーと値の <see cref="Dictionary{TKey, TValue}"/> を持つ <see cref="List{T}"/>。
/// </param>
/// <param name="ItemFields"><see cref="SettingKind.ObjectList"/> の要素の入力欄。</param>
public sealed record SettingField(
    string Key, string DisplayName, string? Description, int Order, SettingKind Kind,
    double? Minimum, double? Maximum, IReadOnlyList<string> Choices, object? DefaultValue,
    IReadOnlyList<SettingField>? ItemFields = null)
{
    public bool IsList => Kind is SettingKind.TextList or SettingKind.FolderList or SettingKind.FileList;

    /// <summary>
    /// <see cref="SettingKind.ObjectList"/> の要素を代表する入力欄（最初の文字列かパス）。一覧ではこの値を表示し、重複を判定する。
    /// </summary>
    public SettingField? PrimaryItemField =>
        ItemFields?.FirstOrDefault(f => f.Kind is SettingKind.Text or SettingKind.Folder or SettingKind.File);

    /// <summary><see cref="SettingKind.ObjectList"/> に追加する要素を、既定値で作る。</summary>
    public Dictionary<string, object?> CreateItem(string primary)
    {
        var item = (ItemFields ?? []).ToDictionary(f => f.Key, f => f.DefaultValue, StringComparer.OrdinalIgnoreCase);
        item[PrimaryItemField!.Key] = primary;
        return item;
    }

    /// <summary>今の値を読む。設定にないときは既定値を返す。</summary>
    public object? Read(IConfiguration section)
    {
        if (Kind == SettingKind.ObjectList)
        {
            var list = section.GetSection(Key);
            var children = list.GetChildren().ToList();
            return children.Count > 0 || list.Value is not null
                ? children.Select(ReadItem).ToList()
                : (DefaultValue as List<Dictionary<string, object?>>)?.Select(d => new Dictionary<string, object?>(d, StringComparer.OrdinalIgnoreCase)).ToList() ?? [];
        }
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

    private Dictionary<string, object?> ReadItem(IConfigurationSection item) =>
        // 文字列だけの要素は、代表の値として読む（文字列のリストから型を変えたときのため）
        item.Value is { } text && !item.GetChildren().Any()
            ? CreateItem(text)
            : ItemFields!.ToDictionary(f => f.Key, f => f.Read(item), StringComparer.OrdinalIgnoreCase);

    /// <summary>書き込む値を JSON にする。</summary>
    public JsonNode? ToJson(object? value) => value switch
    {
        null => null,
        IEnumerable<IReadOnlyDictionary<string, object?>> items when Kind == SettingKind.ObjectList =>
            new JsonArray([.. items.Select(item => (JsonNode?)new JsonObject(
                ItemFields!.Select(f => KeyValuePair.Create(f.Key, f.ToJson(item.GetValueOrDefault(f.Key))))))]),
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
        var skipped = new List<string>();
        var fields = Describe(optionsType, skipped, nested: false);
        unsupported = skipped;
        return [.. fields.Select(p => p.Field)];
    }

    /// <param name="nested">リストの要素のクラスなら true。要素の中のリストには対応しない。</param>
    private static List<(SettingField Field, PropertyInfo Property)> Describe(Type optionsType, List<string> unsupported, bool nested)
    {
        var defaults = Activator.CreateInstance(optionsType);
        var fields = new List<(SettingField, PropertyInfo)>();
        foreach (var property in optionsType.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (property.GetMethod is null || property.SetMethod?.IsPublic != true || property.GetIndexParameters().Length > 0)
            {
                continue;
            }
            var kind = GetKind(property);
            List<(SettingField Field, PropertyInfo Property)>? itemFields = null;
            if (kind == SettingKind.ObjectList && !nested)
            {
                itemFields = Describe(GetItemType(property.PropertyType)!, unsupported, nested: true);
                if (!itemFields.Any(p => p.Field.Kind is SettingKind.Text or SettingKind.Folder or SettingKind.File))
                {
                    kind = null;
                }
            }
            if (kind is null || nested && (kind == SettingKind.ObjectList || IsListKind(kind.Value)))
            {
                unsupported.Add($"{(nested ? optionsType.Name + "." : "")}{property.Name}（{property.PropertyType.Name}）");
                continue;
            }

            var display = property.GetCustomAttribute<DisplayAttribute>();
            var range = property.GetCustomAttribute<RangeAttribute>();
            var value = property.GetValue(defaults);
            fields.Add((new SettingField(
                Key: char.ToLowerInvariant(property.Name[0]) + property.Name[1..],
                DisplayName: display?.GetName() ?? property.Name,
                Description: display?.GetDescription(),
                Order: display?.GetOrder() ?? 0,
                Kind: kind.Value,
                Minimum: range is null ? null : Convert.ToDouble(range.Minimum, CultureInfo.InvariantCulture),
                Maximum: range is null ? null : Convert.ToDouble(range.Maximum, CultureInfo.InvariantCulture),
                Choices: property.PropertyType.IsEnum ? Enum.GetNames(property.PropertyType) : [],
                DefaultValue: itemFields is null ? Normalize(value) : NormalizeItems(value, itemFields),
                ItemFields: itemFields?.Select(p => p.Field).ToList()), property));
        }
        // 並び順が同じものは、宣言の順を保つ
        return [.. fields.Select((f, i) => (f, i)).OrderBy(p => p.f.Item1.Order).ThenBy(p => p.i).Select(p => p.f)];
    }

    private static bool IsListKind(SettingKind kind) => kind is SettingKind.TextList or SettingKind.FolderList or SettingKind.FileList;

    /// <summary>リストの要素の型を返す。リストでなければ null。</summary>
    private static Type? GetItemType(Type type)
    {
        if (type.IsArray)
        {
            return type.GetElementType();
        }
        return type.IsGenericType && type.GetGenericTypeDefinition() is var definition
            && (definition == typeof(List<>) || definition == typeof(IList<>))
            ? type.GetGenericArguments()[0]
            : null;
    }

    private static List<Dictionary<string, object?>> NormalizeItems(object? value, List<(SettingField Field, PropertyInfo Property)> itemFields) =>
        value is System.Collections.IEnumerable items
            ? [.. items.Cast<object>().Select(item => itemFields.ToDictionary(
                p => p.Field.Key, p => Normalize(p.Property.GetValue(item)), StringComparer.OrdinalIgnoreCase))]
            : [];

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
        if (GetItemType(type) is { } itemType)
        {
            if (itemType == typeof(string))
            {
                return folder ? SettingKind.FolderList : file ? SettingKind.FileList : SettingKind.TextList;
            }
            if (itemType.IsClass && itemType.GetConstructor(Type.EmptyTypes) is not null)
            {
                return SettingKind.ObjectList;
            }
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
