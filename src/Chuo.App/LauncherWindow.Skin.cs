using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.VisualTree;
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
    /// <summary>初回の表示で行の高さを測っている。選択の変化などに反応しない。</summary>
    private bool _measuringSkin;
    /// <summary>実際に描画した行から測った1行の高さ。スキンを変えたら 0 に戻して測り直す。</summary>
    private double _measuredRowHeight;
    /// <summary>スキンを変えた直後（まだ行を描画していない間）の仮の1行の高さ。</summary>
    private double _fallbackRowHeight = 56;
    /// <summary>入力欄など、一覧以外の部分の高さ。</summary>
    private double _measuredChromeHeight = 68;

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
        _skinRoot.PropertyChanged += (_, e) =>
        {
            if (e.Property == BoundsProperty)
            {
                OnSkinRootBoundsChanged();
            }
        };
        _results.PropertyChanged += (_, e) =>
        {
            if (e.Property == BoundsProperty)
            {
                OnSkinRootBoundsChanged();
            }
        };
        // 一度に表示する件数を変えたら、一覧の最大の高さと背景の画像を敷く範囲を更新する
        _settings.OnChange((_, _) => Dispatcher.UIThread.Post(() =>
        {
            ApplyListMaxHeight();
            UpdateSkinImageArea();
        }));
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
        // 文字の大きさが変わったかもしれないので、行の高さを測り直す
        _measuredRowHeight = 0;
        _fallbackRowHeight = Math.Max(32, current.TitleFontSize * 1.4 + current.SubtitleFontSize * 1.4) + 20;
        ApplyListMaxHeight();
        // リソースの変更（文字の大きさなど）が部品に届き、画面を作り直した後に測る
        Dispatcher.UIThread.Post(() =>
        {
            if (CaptureRowMetricsFromRealized())
            {
                ApplyListMaxHeight();
            }
            UpdateSkinImageArea();
        }, DispatcherPriority.Background);
    }

    /// <summary>
    /// 一度に画面に出す件数（<see cref="ChuoSettings.MaxResults"/>）ぶんの高さに一覧の上限を合わせる。
    /// これを超える候補はスクロールで選べる。
    /// </summary>
    private void ApplyListMaxHeight()
    {
        var count = Math.Max(1, _settings.CurrentValue.MaxResults);
        var rowHeight = _measuredRowHeight > 0 ? _measuredRowHeight : _fallbackRowHeight;
        var overhead = _results.Padding.Top + _results.Padding.Bottom + _results.BorderThickness.Top + _results.BorderThickness.Bottom;
        var maxHeight = Math.Ceiling(rowHeight * count + overhead);
        _results.MaxHeight = maxHeight;
        _panel.MaxHeight = maxHeight;
    }

    /// <summary>
    /// 実際に並んでいる行から、1行の高さと一覧以外の高さを取る。
    /// 候補の一覧は仮想化しているが、画面に出ている行は作られているので、その最も高い行から求められる。
    /// </summary>
    private bool CaptureRowMetricsFromRealized()
    {
        if (!_results.IsVisible)
        {
            return false;
        }
        var rows = _results.GetRealizedContainers().Select(c => c.Bounds.Height).Where(h => h > 0).ToList();
        if (rows.Count == 0)
        {
            return false;
        }
        var tallest = rows.Max();
        var updated = false;
        if (tallest > _measuredRowHeight + 0.5)
        {
            _measuredRowHeight = tallest;
            updated = true;
        }
        if (_skinRoot.Bounds.Height > _results.Bounds.Height)
        {
            _measuredChromeHeight = _skinRoot.Bounds.Height - _results.Bounds.Height;
        }
        return updated;
    }

    private double CurrentMaxWindowHeight()
    {
        var count = Math.Max(1, _settings.CurrentValue.MaxResults);
        var rowHeight = _measuredRowHeight > 0 ? _measuredRowHeight : _fallbackRowHeight;
        var overhead = _results.Padding.Top + _results.Padding.Bottom + _results.BorderThickness.Top + _results.BorderThickness.Bottom;
        return _measuredChromeHeight + Math.Ceiling(rowHeight * count + overhead);
    }

    /// <summary>
    /// 背景の画像を敷く範囲を、一覧の最大の高さのときの大きさに固定する。ウィンドウの大きさに合わせると、
    /// 候補の数で高さが変わるたびに画像の拡大率が変わり、がたつくため。低いときは、下の部分が切れて見える。
    /// </summary>
    private void UpdateSkinImageArea()
    {
        if (_skinRoot.Background is not ImageBrush brush || !_prewarmed)
        {
            return;
        }
        var height = Math.Max(_skinRoot.Bounds.Height, CurrentMaxWindowHeight());
        brush.DestinationRect = new RelativeRect(0, 0, Width, height, RelativeUnit.Absolute);
    }

    /// <summary>
    /// 実際に並んでいる行の高さが今までの計測より大きければ、一覧の上限と画像を敷く範囲を広げる。
    /// 表示中にスキンを変えたときや、サブタイトルのない行しか並んでいなかった後からサブタイトル付きの行が出たときに追従する。
    /// </summary>
    private void OnSkinRootBoundsChanged()
    {
        if (_measuringSkin || !IsVisible)
        {
            return;
        }
        if (CaptureRowMetricsFromRealized())
        {
            ApplyListMaxHeight();
        }
        if (_skinRoot.Background is ImageBrush { DestinationRect: { Unit: RelativeUnit.Absolute } rect } brush)
        {
            var needed = Math.Max(_skinRoot.Bounds.Height, CurrentMaxWindowHeight());
            if (needed > rect.Rect.Height + 0.5)
            {
                brush.DestinationRect = new RelativeRect(0, 0, Width, needed, RelativeUnit.Absolute);
            }
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
