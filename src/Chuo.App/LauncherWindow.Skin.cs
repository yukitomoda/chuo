using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Chuo.Abstractions;
using Chuo.Core.Search;
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
    /// <summary>ウィンドウを一度表示し、部品のテンプレートができた。</summary>
    private bool _prewarmed;
    /// <summary>背景の画像の範囲を測るため、仮の候補を並べている。選択の変化などに反応しない。</summary>
    private bool _measuringSkin;

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
        // 最大の件数を変えたら、背景の画像を敷く範囲を測り直す
        _settings.OnChange((_, _) => Dispatcher.UIThread.Post(UpdateSkinImageArea));
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
                AlignmentY = AlignmentY.Top,
                Opacity = skin.BackgroundImageOpacity,
            };
        previous?.Dispose();
        UpdateSkinImageArea();
    }

    /// <summary>
    /// 背景の画像を敷く範囲を、候補を最大の件数まで並べたときの大きさに固定する。ウィンドウの大きさに合わせると、
    /// 候補の数で高さが変わるたびに画像の拡大率が変わり、がたつくため。低いときは、下の部分が切れて見える。
    /// 測るには部品のテンプレートが要るので、ウィンドウを一度表示した（<see cref="PrewarmAsync"/>）後に行う。
    /// </summary>
    private void UpdateSkinImageArea()
    {
        if (_skinRoot.Background is not ImageBrush brush || !_prewarmed)
        {
            return;
        }
        var height = MeasureMaxHeight();
        brush.DestinationRect = new RelativeRect(0, 0, Width, height, RelativeUnit.Absolute);
    }

    /// <summary>
    /// 候補を最大の件数（タイトルとサブタイトルのある行）まで並べたときの、ウィンドウの中身の高さ。
    /// 一覧は見えない行を作らずに測るので、1件だけ並べて測り、残りは行の高さを足す。
    /// </summary>
    private double MeasureMaxHeight()
    {
        var count = Math.Max(1, _settings.CurrentValue.MaxResults);
        var (items, visible, selected) = (_results.ItemsSource, _results.IsVisible, _results.SelectedIndex);
        var panelVisible = _panelArea.IsVisible;
        _measuringSkin = true;
        try
        {
            _results.ItemsSource = new[] { new SearchResult(new ResultItem { Id = "measure", Title = "A", Subtitle = "A" }, "", 0) };
            _results.IsVisible = true;
            _panelArea.IsVisible = false;
            _skinRoot.Measure(new Size(Width, double.PositiveInfinity));
            var row = _results.ContainerFromIndex(0)?.DesiredSize.Height ?? 0;
            return _skinRoot.DesiredSize.Height + row * (count - 1);
        }
        finally
        {
            _results.ItemsSource = items;
            _results.IsVisible = visible;
            _results.SelectedIndex = selected;
            _panelArea.IsVisible = panelVisible;
            _measuringSkin = false;
            _skinRoot.InvalidateMeasure();
        }
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
