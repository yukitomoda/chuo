using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Chuo.Core.Skins;
using Microsoft.Extensions.Logging;

namespace Chuo.App;

/// <summary>
/// スキンの反映（設計 8.1）。見た目の値は名前を付けたリソースに置き、部品はリソースから取る。
/// スキンを変えたらリソースを差し替えるだけで、部品を作り直さない。
/// </summary>
public sealed partial class LauncherWindow
{
    private const string InputFontSizeKey = "ChuoInputFontSize";
    private const string TitleFontSizeKey = "ChuoTitleFontSize";
    private const string SubtitleFontSizeKey = "ChuoSubtitleFontSize";
    /// <summary>キーの表示や更新の知らせなど。サブタイトルより一回り大きい。</summary>
    private const string SmallFontSizeKey = "ChuoSmallFontSize";
    private const string PanelTitleFontSizeKey = "ChuoPanelTitleFontSize";
    private const string TextKey = "ChuoText";
    private const string SubtextKey = "ChuoSubtext";
    private const string SelectedBackgroundKey = "ChuoSelectedBackground";
    private const string SelectedPointerOverBackgroundKey = "ChuoSelectedPointerOverBackground";
    private const string PointerOverBackgroundKey = "ChuoPointerOverBackground";
    private const string AdminBackgroundKey = "ChuoAdminBackground";
    private const string ErrorKey = "ChuoError";
    private const string LinkKey = "ChuoLink";
    private const string SeparatorKey = "ChuoSeparator";

    /// <summary>入力欄の色。Fluent の TextBox はフォーカスの状態ごとにリソースを持つので、すべて上書きする。</summary>
    private static readonly string[] InputBackgroundKeys = ["TextControlBackground", "TextControlBackgroundPointerOver", "TextControlBackgroundFocused"];
    private static readonly string[] InputBorderKeys = ["TextControlBorderBrush", "TextControlBorderBrushPointerOver", "TextControlBorderBrushFocused"];
    private static readonly string[] InputForegroundKeys = ["TextControlForeground", "TextControlForegroundPointerOver", "TextControlForegroundFocused"];

    private readonly Border _skinRoot;
    private Bitmap? _skinImage;

    /// <summary>
    /// 部品のプロパティを、リソースに結びつける。スキンが値を決めていないリソースはないので、部品の既定（Fluent の色）になる。
    /// ないリソースは null として届くので、値なし（UnsetValue）に置き換えて既定に戻す。
    /// </summary>
    private static T BindResource<T>(T control, AvaloniaProperty property, string key) where T : Control
    {
        control.Bind(property, control.GetResourceObservable(key, value => value ?? AvaloniaProperty.UnsetValue));
        return control;
    }

    private void AttachSkins(SkinService skins)
    {
        ApplySkin(skins.Current);
        skins.Changed += () => ApplySkin(skins.Current);
    }

    /// <summary>スキンを反映する。null なら OS のライト／ダークに従い、同梱のライトとダークを使い分ける。UI スレッドで呼ぶ。</summary>
    private void ApplySkin(Skin? skin)
    {
        Resources.ThemeDictionaries[ThemeVariant.Light] = CreateResources(skin ?? SkinCatalog.BuiltIn(SkinBase.Light));
        Resources.ThemeDictionaries[ThemeVariant.Dark] = CreateResources(skin ?? SkinCatalog.BuiltIn(SkinBase.Dark));

        var current = skin ?? SkinCatalog.BuiltIn(SkinBase.Light);
        Width = current.Width;
        if (current.FontFamily is { Length: > 0 } family)
        {
            FontFamily = new FontFamily(family);
        }
        else
        {
            ClearValue(FontFamilyProperty);
        }
        if (skin?.Colors.Background is { } background)
        {
            Background = new SolidColorBrush(Color.Parse(background));
        }
        else
        {
            ClearValue(BackgroundProperty);
        }

        // スキンが背景を決めたら、一覧の背景を透明にしてそれを見せる
        foreach (var list in new[] { _results, _panel })
        {
            if (skin is { Colors.Background: not null } or { BackgroundImagePath: not null })
            {
                list.Background = Brushes.Transparent;
            }
            else
            {
                list.ClearValue(BackgroundProperty);
            }
        }

        var previous = _skinImage;
        _skinImage = LoadImage(skin);
        _skinRoot.Background = _skinImage is null
            ? null
            : new ImageBrush(_skinImage)
            {
                Stretch = skin!.BackgroundImageStretch switch
                {
                    SkinImageStretch.Uniform => Stretch.Uniform,
                    SkinImageStretch.Fill => Stretch.Fill,
                    SkinImageStretch.None => Stretch.None,
                    _ => Stretch.UniformToFill,
                },
                Opacity = skin.BackgroundImageOpacity,
            };
        previous?.Dispose();
    }

    private static ResourceDictionary CreateResources(Skin skin)
    {
        var colors = skin.Colors;
        var resources = new ResourceDictionary
        {
            [InputFontSizeKey] = skin.InputFontSize,
            [TitleFontSizeKey] = skin.TitleFontSize,
            [SubtitleFontSizeKey] = skin.SubtitleFontSize,
            [SmallFontSizeKey] = skin.SubtitleFontSize + 1,
            [PanelTitleFontSizeKey] = skin.SubtitleFontSize + 2,
            [SelectedBackgroundKey] = Brush(colors.Selected),
            [SelectedPointerOverBackgroundKey] = Brush(colors.SelectedHover),
            [PointerOverBackgroundKey] = Brush(colors.Hover),
            [AdminBackgroundKey] = Brush(colors.Admin),
            [ErrorKey] = Brush(colors.Error),
            [LinkKey] = Brush(colors.Link),
            [SeparatorKey] = Brush(colors.Separator),
        };
        resources[TextKey] = Brush(colors.Text);
        resources[SubtextKey] = Brush(colors.Subtext);
        foreach (var key in InputForegroundKeys)
        {
            resources[key] = Brush(colors.Text);
        }
        if (colors.InputBackground is { } inputBackground)
        {
            foreach (var key in InputBackgroundKeys)
            {
                resources[key] = Brush(inputBackground);
            }
        }
        if (colors.InputBorder is { } inputBorder)
        {
            foreach (var key in InputBorderKeys)
            {
                resources[key] = Brush(inputBorder);
            }
        }
        return resources;
    }

    private static SolidColorBrush Brush(string color) => new(Color.Parse(color));

    /// <summary>
    /// 背景の画像を、ウィンドウの幅（画面の拡大率込み）に縮めて読む。大きな画像をそのまま持つと、メモリも描画の手間も増えるため。
    /// </summary>
    private Bitmap? LoadImage(Skin? skin)
    {
        if (skin?.BackgroundImagePath is not { } path)
        {
            return null;
        }
        try
        {
            var scaling = Screens.All.Count > 0 ? Screens.All.Max(s => s.Scaling) : 1;
            using var stream = File.OpenRead(path);
            return Bitmap.DecodeToWidth(stream, (int)Math.Ceiling(skin.Width * scaling), BitmapInterpolationMode.HighQuality);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "スキンの背景の画像を読めなかった: {Path}", path);
            return null;
        }
    }
}
