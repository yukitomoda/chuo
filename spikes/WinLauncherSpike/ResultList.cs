using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace WinLauncherSpike;

/// <summary>結果の一覧の1件。本番と同じく、アイコンと2行のテキストを持つ。</summary>
public sealed record ResultItem(Bitmap Icon, string Title, string Subtitle);

/// <summary>
/// 描画のコストを本番に近づけるための、ダミーの結果の一覧。
/// 絞り込みの正しさは検証の対象ではないので、入力が変わるたびに別の項目を並べ、毎回すべての行が描き直されるようにする。
/// </summary>
public static class ResultList
{
    public const int ItemCount = 200;
    public const int VisibleCount = 30;
    private const int IconPixels = 64;

    private static readonly string[] Names =
    [
        "Visual Studio Code", "Firefox", "ターミナル", "エクスプローラー", "メモ帳", "電卓", "設定", "Slack",
        "議事録 2026-10-09.docx", "見積書_最終版.xlsx", "スクリーンショット", "ダウンロード", "chuo", "README.md",
    ];

    private static readonly string[] Folders =
    [
        @"C:\Program Files", @"C:\Users\user\Documents", @"C:\Users\user\Downloads", @"D:\work\chuo", @"C:\Windows\System32",
    ];

    public static IReadOnlyList<ResultItem> CreateItems() =>
        Enumerable.Range(0, ItemCount)
            .Select(i => new ResultItem(
                CreateIcon(i),
                $"{Names[i % Names.Length]} {i + 1}",
                $@"{Folders[i % Folders.Length]}\{Names[(i * 7) % Names.Length]}"))
            .ToList();

    /// <summary>入力に応じて、表示する項目を選ぶ。入力が変われば、ほぼ必ず別の並びになる。</summary>
    public static IReadOnlyList<ResultItem> Filter(IReadOnlyList<ResultItem> items, string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return items.Take(VisibleCount).ToList();
        }

        var offset = (int)((uint)string.GetHashCode(text, StringComparison.Ordinal) % (uint)items.Count);
        var count = Math.Max(1, VisibleCount - text.Length);
        return Enumerable.Range(0, count).Select(i => items[(offset + i * 13) % items.Count]).ToList();
    }

    /// <summary>アイコンと2行のテキストを並べる行のテンプレート。</summary>
    public static IDataTemplate CreateItemTemplate() =>
        new FuncDataTemplate<ResultItem>((_, _) =>
        {
            var icon = new Image { Width = 32, Height = 32, Margin = new Thickness(0, 0, 10, 0) };
            var title = new TextBlock { FontSize = 15, TextTrimming = TextTrimming.CharacterEllipsis };
            var subtitle = new TextBlock { FontSize = 11, Foreground = Brushes.Gray, TextTrimming = TextTrimming.CharacterEllipsis };
            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Children = { title, subtitle } };
            var row = new DockPanel { Children = { icon, text } };
            DockPanel.SetDock(icon, Dock.Left);

            // 行は使い回されるので、DataContext が変わるたびに中身を差し替える
            row.DataContextChanged += (_, _) =>
            {
                if (row.DataContext is ResultItem item)
                {
                    icon.Source = item.Icon;
                    title.Text = item.Title;
                    subtitle.Text = item.Subtitle;
                }
            };
            return row;
        }, supportsRecycling: true);

    /// <summary>項目ごとに色と形の違うアイコンを作る（実際のアイコンと同じく、64px を縮小して描く）。</summary>
    private static unsafe Bitmap CreateIcon(int index)
    {
        var bitmap = new WriteableBitmap(new PixelSize(IconPixels, IconPixels), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
        using var buffer = bitmap.Lock();
        var hue = index * 37 % 360;
        var (r, g, b) = FromHue(hue);
        var radius = IconPixels / 2 - 2 - index % 8;
        for (var y = 0; y < IconPixels; y++)
        {
            var line = (uint*)((byte*)buffer.Address + y * buffer.RowBytes);
            for (var x = 0; x < IconPixels; x++)
            {
                var dx = x - IconPixels / 2;
                var dy = y - IconPixels / 2;
                var inside = dx * dx + dy * dy <= radius * radius;
                var shade = 0.6 + 0.4 * y / IconPixels;
                line[x] = inside
                    ? 0xFF000000u | (uint)(r * shade) << 16 | (uint)(g * shade) << 8 | (uint)(b * shade)
                    : 0u;
            }
        }
        return bitmap;
    }

    private static (int R, int G, int B) FromHue(int hue)
    {
        var x = (int)(255 * (1 - Math.Abs(hue / 60.0 % 2 - 1)));
        return (hue / 60) switch
        {
            0 => (255, x, 0),
            1 => (x, 255, 0),
            2 => (0, 255, x),
            3 => (0, x, 255),
            4 => (x, 0, 255),
            _ => (255, 0, x),
        };
    }
}
