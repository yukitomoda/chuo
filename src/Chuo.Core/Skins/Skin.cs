using System.Text.Json;
using System.Text.Json.Serialization;

namespace Chuo.Core.Skins;

/// <summary>スキンの土台。Fluent のライトかダークかと、省いた値の既定を決める。</summary>
public enum SkinBase
{
    Light,
    Dark,
}

/// <summary>背景の画像の広げ方。</summary>
public enum SkinImageStretch
{
    /// <summary>縦横の比を保ち、ウィンドウを覆うまで広げる（はみ出した分は切る）。</summary>
    UniformToFill,

    /// <summary>縦横の比を保ち、ウィンドウに収まるように広げる。</summary>
    Uniform,

    /// <summary>縦横の比を変えて、ウィンドウに合わせる。</summary>
    Fill,

    /// <summary>広げない。</summary>
    None,
}

/// <summary>
/// スキンのフォルダの <c>skin.json</c>（設計 8.1）。省いた値は <see cref="Base"/> の既定を使う。
/// 色は <c>#RRGGBB</c> か <c>#AARRGGBB</c>。
/// </summary>
public sealed class SkinFile
{
    /// <summary>設定画面に出す名前。省けばフォルダの名前。</summary>
    public string? Name { get; set; }

    public SkinBase Base { get; set; } = SkinBase.Light;

    public SkinFont? Font { get; set; }

    public SkinWindowSize? Window { get; set; }

    public SkinColors? Colors { get; set; }

    public SkinImage? BackgroundImage { get; set; }
}

public sealed class SkinFont
{
    public string? Family { get; set; }

    /// <summary>入力欄の文字の大きさ。</summary>
    public double? InputSize { get; set; }

    /// <summary>項目のタイトルの文字の大きさ。</summary>
    public double? TitleSize { get; set; }

    /// <summary>項目のサブタイトルなど、小さい文字の大きさ。</summary>
    public double? SubtitleSize { get; set; }
}

public sealed class SkinWindowSize
{
    public double? Width { get; set; }
}

/// <summary>色。背景と入力欄の色を省くと Fluent のテーマの色、文字の色を省くと土台（ライトかダーク）の色を使う。</summary>
public sealed class SkinColors
{
    public string? Background { get; set; }
    public string? Text { get; set; }
    /// <summary>サブタイトルなどの控えめな文字。省けば <see cref="Text"/>。</summary>
    public string? Subtext { get; set; }
    public string? InputBackground { get; set; }
    public string? InputBorder { get; set; }
    /// <summary>選んでいる行の背景。</summary>
    public string? Selected { get; set; }
    /// <summary>選んでいる行にマウスを重ねたときの背景。</summary>
    public string? SelectedHover { get; set; }
    /// <summary>マウスを重ねた行の背景。</summary>
    public string? Hover { get; set; }
    /// <summary>Ctrl+Shift を押している間（管理者として実行）の、選んでいる行の背景。</summary>
    public string? Admin { get; set; }
    /// <summary>アクションの失敗の知らせ。</summary>
    public string? Error { get; set; }
    /// <summary>更新の知らせなど、押せる文字。</summary>
    public string? Link { get; set; }
    /// <summary>アクションパネルの区切りの線。</summary>
    public string? Separator { get; set; }
}

public sealed class SkinImage
{
    /// <summary>画像のパス。スキンのフォルダからの相対パスか、絶対パス。</summary>
    public string? Path { get; set; }

    /// <summary>画像の不透明度（0〜1）。文字を読みやすくするため、薄くして使う。</summary>
    public double Opacity { get; set; } = 1;

    public SkinImageStretch Stretch { get; set; } = SkinImageStretch.UniformToFill;
}

/// <summary>既定と合わせた、実際に使うスキン。</summary>
/// <param name="Id">設定に書く値（<see cref="SkinCatalog.Light"/>、<see cref="SkinCatalog.Dark"/>、またはフォルダの名前）。</param>
/// <param name="BackgroundImagePath">背景の画像のフルパス。なければ null。</param>
public sealed record Skin(
    string Id,
    string Name,
    SkinBase Base,
    string? FontFamily,
    double InputFontSize,
    double TitleFontSize,
    double SubtitleFontSize,
    double Width,
    SkinPalette Colors,
    string? BackgroundImagePath,
    double BackgroundImageOpacity,
    SkinImageStretch BackgroundImageStretch);

/// <summary>実際に使う色。null は Fluent のテーマの色を使う。文字の色は、省いても土台の色を決めておく。</summary>
public sealed record SkinPalette(
    string? Background,
    string Text,
    string Subtext,
    string? InputBackground,
    string? InputBorder,
    string Selected,
    string SelectedHover,
    string Hover,
    string Admin,
    string Error,
    string Link,
    string Separator);

/// <summary>
/// 同梱のスキン（ライトとダーク）と、設定のフォルダの <c>skins/</c> に置いたスキンの一覧と読み込み（設計 8.1）。
/// </summary>
public sealed class SkinCatalog(string directory)
{
    /// <summary>OS のライト／ダークに従う（設定の既定）。</summary>
    public const string FollowSystem = "";
    public const string Light = "light";
    public const string Dark = "dark";

    public const string FileName = "skin.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public string Directory { get; } = directory;

    /// <summary>同梱のスキン。今のランチャーの見た目と同じ値。</summary>
    public static Skin BuiltIn(SkinBase @base) => @base == SkinBase.Dark
        ? Create(Dark, "ダーク", @base, null, null, null)
        : Create(Light, "ライト", @base, null, null, null);

    /// <summary>選べるスキン（OS に従う、ライト、ダーク、置いたスキンの順）。</summary>
    public IReadOnlyList<(string Id, string Name)> List()
    {
        List<(string, string)> skins = [(FollowSystem, "OS に従う"), (Light, "ライト"), (Dark, "ダーク")];
        if (!System.IO.Directory.Exists(Directory))
        {
            return skins;
        }
        foreach (var folder in System.IO.Directory.EnumerateDirectories(Directory).Order(StringComparer.OrdinalIgnoreCase))
        {
            var id = Path.GetFileName(folder);
            if (id is Light or Dark || !File.Exists(Path.Combine(folder, FileName)))
            {
                continue;
            }
            string name;
            try
            {
                name = Read(id).Name ?? id;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                name = id;
            }
            skins.Add((id, name));
        }
        return skins;
    }

    /// <summary>
    /// スキンを読む。<see cref="FollowSystem"/> なら null（OS に従う）。読めないスキンは例外を投げる。
    /// </summary>
    public Skin? Load(string id) => id switch
    {
        FollowSystem => null,
        Light => BuiltIn(SkinBase.Light),
        Dark => BuiltIn(SkinBase.Dark),
        _ => FromFile(id, Read(id)),
    };

    private SkinFile Read(string id) =>
        JsonSerializer.Deserialize<SkinFile>(File.ReadAllText(Path.Combine(Directory, id, FileName)), JsonOptions)
        ?? throw new JsonException("skin.json が空");

    private Skin FromFile(string id, SkinFile file)
    {
        var image = file.BackgroundImage?.Path is { Length: > 0 } path
            ? Path.GetFullPath(Path.Combine(Directory, id, path))
            : null;
        return Create(id, file.Name ?? id, file.Base, file.Font, file.Window, file.Colors) with
        {
            BackgroundImagePath = image,
            BackgroundImageOpacity = Math.Clamp(file.BackgroundImage?.Opacity ?? 1, 0, 1),
            BackgroundImageStretch = file.BackgroundImage?.Stretch ?? SkinImageStretch.UniformToFill,
        };
    }

    private static Skin Create(string id, string name, SkinBase @base, SkinFont? font, SkinWindowSize? window, SkinColors? colors)
    {
        var dark = @base == SkinBase.Dark;
        var palette = new SkinPalette(
            Background: colors?.Background,
            // 文字の色は必ず決める。決めないと、一覧の文字が Fluent のテーマの色ではなく黒になる（ダークで読めない）
            Text: colors?.Text ?? (dark ? "#FFFFFFFF" : "#E4000000"),
            Subtext: colors?.Subtext ?? colors?.Text ?? (dark ? "#FFFFFFFF" : "#E4000000"),
            InputBackground: colors?.InputBackground,
            InputBorder: colors?.InputBorder,
            Selected: colors?.Selected ?? (dark ? "#2F4F6F" : "#D3E8FB"),
            SelectedHover: colors?.SelectedHover ?? (dark ? "#375C80" : "#C5E0F9"),
            Hover: colors?.Hover ?? (dark ? "#3A3A3A" : "#E6E6E6"),
            Admin: colors?.Admin ?? (dark ? "#6B4A1F" : "#FCDDB4"),
            Error: colors?.Error ?? "#CD5C5C",
            Link: colors?.Link ?? "#4682B4",
            Separator: colors?.Separator ?? "#4D808080");
        return new Skin(
            id, name, @base,
            FontFamily: font?.Family,
            InputFontSize: Positive(font?.InputSize, 22),
            TitleFontSize: Positive(font?.TitleSize, 15),
            SubtitleFontSize: Positive(font?.SubtitleSize, 11),
            Width: Math.Clamp(window?.Width ?? 680, 320, 2000),
            Colors: palette,
            BackgroundImagePath: null,
            BackgroundImageOpacity: 1,
            BackgroundImageStretch: SkinImageStretch.UniformToFill);
    }

    private static double Positive(double? value, double fallback) => value is > 0 ? value.Value : fallback;

    /// <summary>スキンを作るときの見本。スキンのフォルダが空のときに置く。</summary>
    public const string SampleJson = """
        {
          // 設定画面に出す名前
          "name": "見本",
          // light か dark。Fluent のテーマと、省いた値の既定を決める
          "base": "dark",
          "font": { "family": null, "inputSize": 22, "titleSize": 15, "subtitleSize": 11 },
          "window": { "width": 680 },
          // 色は #RRGGBB か #AARRGGBB。省くと base の色を使う
          "colors": {
            "background": "#1E1E2E",
            "text": "#CDD6F4",
            "subtext": "#A6ADC8",
            "inputBackground": "#313244",
            "inputBorder": "#89B4FA",
            "selected": "#45475A",
            "selectedHover": "#585B70",
            "hover": "#313244",
            "admin": "#6B4A1F",
            "error": "#F38BA8",
            "link": "#89B4FA",
            "separator": "#4D808080"
          },
          // 画像はこのフォルダからの相対パス。文字が読めるよう、opacity で薄くする
          "backgroundImage": { "path": null, "opacity": 0.3, "stretch": "uniformToFill" }
        }
        """;
}
