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
}
