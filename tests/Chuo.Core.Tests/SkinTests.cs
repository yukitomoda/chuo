using System.Text.Json;
using Chuo.Core.Skins;

namespace Chuo.Core.Tests;

public sealed class SkinTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "chuo-skins-" + Guid.NewGuid().ToString("N"));
    private readonly SkinCatalog _catalog;

    public SkinTests() => _catalog = new SkinCatalog(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private void Write(string id, string json)
    {
        Directory.CreateDirectory(Path.Combine(_root, id));
        File.WriteAllText(Path.Combine(_root, id, SkinCatalog.FileName), json);
    }

    [Fact]
    public void OS_に従うときは_null()
    {
        Assert.Null(_catalog.Load(SkinCatalog.FollowSystem));
    }

    [Fact]
    public void 同梱のスキンは今の見た目の値()
    {
        var dark = _catalog.Load(SkinCatalog.Dark)!;

        Assert.Equal(SkinBase.Dark, dark.Base);
        Assert.Equal("#2F4F6F", dark.Colors.Selected);
        Assert.Equal(22, dark.InputFontSize);
        Assert.Equal(680, dark.Width);
        Assert.Null(dark.Colors.Background);
        // 文字の色を決めないと、一覧の文字が黒になってダークで読めない
        Assert.Equal("#FFFFFFFF", dark.Colors.Text);
        Assert.Equal("#E4000000", _catalog.Load(SkinCatalog.Light)!.Colors.Text);
    }

    [Fact]
    public void 省いた値は土台の既定を使う()
    {
        Write("night", """
            {
              // コメントも書ける
              "base": "dark",
              "font": { "titleSize": 18 },
              "colors": { "text": "#EEEEEE", "selected": "#112233", },
              "backgroundImage": { "path": "bg.png", "opacity": 2 }
            }
            """);

        var skin = _catalog.Load("night")!;

        Assert.Equal("night", skin.Name);
        Assert.Equal(18, skin.TitleFontSize);
        Assert.Equal(22, skin.InputFontSize);
        Assert.Equal("#112233", skin.Colors.Selected);
        Assert.Equal("#375C80", skin.Colors.SelectedHover);
        // 控えめな文字は、省けば文字の色
        Assert.Equal("#EEEEEE", skin.Colors.Subtext);
        Assert.Equal(Path.Combine(_root, "night", "bg.png"), skin.BackgroundImagePath);
        Assert.Equal(1, skin.BackgroundImageOpacity);
    }

    [Fact]
    public void 一覧は同梱のスキンの後に_skin_json_のあるフォルダを並べる()
    {
        Write("b-skin", """{ "name": "ビー" }""");
        Write("a-skin", "{ broken");
        Directory.CreateDirectory(Path.Combine(_root, "empty"));

        Assert.Equal(
            [(SkinCatalog.FollowSystem, "OS に従う"), (SkinCatalog.Light, "ライト"), (SkinCatalog.Dark, "ダーク"), ("a-skin", "a-skin"), ("b-skin", "ビー")],
            _catalog.List());
    }

    [Fact]
    public void 壊れたスキンは例外()
    {
        Write("broken", "{ broken");

        Assert.ThrowsAny<JsonException>(() => _catalog.Load("broken"));
    }

    [Fact]
    public void 見本は読めるスキン()
    {
        Write("sample", SkinCatalog.SampleJson);

        var skin = _catalog.Load("sample")!;

        Assert.Equal("見本", skin.Name);
        Assert.Equal(SkinBase.Dark, skin.Base);
        Assert.Null(skin.BackgroundImagePath);
    }

    [Fact]
    public void EnsureDirectory_はスキーマを書き出し_空なら見本も置く()
    {
        _catalog.EnsureDirectory();

        Assert.True(File.Exists(Path.Combine(_root, SkinCatalog.SchemaFileName)));
        Assert.True(File.Exists(Path.Combine(_root, "sample", "skin.jsonc")));
        Assert.Equal("見本", _catalog.Load("sample")!.Name);

        // すでにスキンのフォルダがあるときは、見本を作り直さずスキーマだけを更新する
        Directory.Delete(Path.Combine(_root, "sample"), recursive: true);
        Write("custom", """{ "name": "自作" }""");
        File.WriteAllText(Path.Combine(_root, SkinCatalog.SchemaFileName), "old");

        _catalog.EnsureDirectory();

        Assert.Equal(SkinCatalog.SchemaJson, File.ReadAllText(Path.Combine(_root, SkinCatalog.SchemaFileName)));
        Assert.False(Directory.Exists(Path.Combine(_root, "sample")));
    }

    [Fact]
    public void 従来の_skin_json_も読み込め_skin_jsonc_が優先される()
    {
        var folder = Path.Combine(_root, "legacy");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, SkinCatalog.LegacyFileName), """{ "name": "旧スキン" }""");

        Assert.Contains(("legacy", "旧スキン"), _catalog.List());
        Assert.Equal("旧スキン", _catalog.Load("legacy")!.Name);

        // skin.jsonc を置いたらそちらを優先する
        File.WriteAllText(Path.Combine(folder, SkinCatalog.FileName), """{ "name": "新スキン" }""");
        Assert.Equal("新スキン", _catalog.Load("legacy")!.Name);
    }

    [Fact]
    public void スキーマは_SkinFile_のすべての項目と選択肢を持つ()
    {
        using var doc = JsonDocument.Parse(SkinCatalog.SchemaJson);
        var root = doc.RootElement;
        var props = root.GetProperty("properties");

        static string[] ExpectedNames(Type type) =>
            [.. type.GetProperties().Select(p => JsonNamingPolicy.CamelCase.ConvertName(p.Name)).Order()];
        static string[] SchemaPropertyNames(JsonElement element) =>
            [.. element.GetProperty("properties").EnumerateObject().Select(p => p.Name).Where(n => n != "$schema").Order()];
        static string[] SchemaEnumValues(JsonElement element) =>
            [.. element.GetProperty("enum").EnumerateArray().Select(e => e.GetString()!)];

        Assert.Equal(ExpectedNames(typeof(SkinFile)), SchemaPropertyNames(root));
        Assert.Equal(ExpectedNames(typeof(SkinFont)), SchemaPropertyNames(props.GetProperty("font")));
        Assert.Equal(ExpectedNames(typeof(SkinWindowSize)), SchemaPropertyNames(props.GetProperty("window")));
        Assert.Equal(ExpectedNames(typeof(SkinColors)), SchemaPropertyNames(props.GetProperty("colors")));
        Assert.Equal(ExpectedNames(typeof(SkinImage)), SchemaPropertyNames(props.GetProperty("backgroundImage")));
        Assert.Equal(
            [.. Enum.GetNames<SkinBase>().Select(JsonNamingPolicy.CamelCase.ConvertName)],
            SchemaEnumValues(props.GetProperty("base")));
        Assert.Equal(
            [.. Enum.GetNames<SkinImageStretch>().Select(JsonNamingPolicy.CamelCase.ConvertName)],
            SchemaEnumValues(props.GetProperty("backgroundImage").GetProperty("properties").GetProperty("stretch")));

        // draft-07 では $ref と並べた description が無視されるので、各項目に直接 type と description を書く
        foreach (var color in props.GetProperty("colors").GetProperty("properties").EnumerateObject())
        {
            Assert.True(color.Value.TryGetProperty("type", out _));
            Assert.True(color.Value.TryGetProperty("description", out _));
            Assert.False(color.Value.TryGetProperty("$ref", out _));
        }
    }
}
