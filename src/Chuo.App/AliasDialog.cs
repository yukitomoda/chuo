using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Chuo.App;

/// <summary>
/// 項目のエイリアスを編集するダイアログ（要件定義書 6.1）。ランチャーの右クリックのメニューと、設定画面の項目のページから開く。
/// エイリアスは空白で区切って入力する。保存したら新しいエイリアスの一覧を、取り消したら null を返す。
/// </summary>
public sealed class AliasDialog : Window
{
    private readonly TextBox _input;

    public AliasDialog(string itemTitle, IEnumerable<string> aliases)
    {
        Title = "エイリアス";
        Width = 420;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        _input = new TextBox { Text = string.Join(" ", aliases) };
        var save = new Button { Content = "保存", IsDefault = true, MinWidth = 80, HorizontalContentAlignment = HorizontalAlignment.Center };
        var cancel = new Button { Content = "キャンセル", IsCancel = true, MinWidth = 80, HorizontalContentAlignment = HorizontalAlignment.Center };
        save.Click += (_, _) => Close(Split(_input.Text));
        cancel.Click += (_, _) => Close(null);

        Content = new StackPanel
        {
            Margin = new Thickness(16),
            Spacing = 8,
            Children =
            {
                new TextBlock { Text = itemTitle, FontSize = 16, FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis },
                _input,
                new TextBlock
                {
                    Text = "空白で区切ると、複数付けられる。入力がエイリアスと一致すると最上位に、エイリアスの先頭と一致すると上のほうに出る",
                    FontSize = 12,
                    Opacity = 0.65,
                    TextWrapping = TextWrapping.Wrap,
                },
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Spacing = 8,
                    Margin = new Thickness(0, 8, 0, 0),
                    Children = { save, cancel },
                },
            },
        };

        Opened += (_, _) =>
        {
            _input.Focus();
            _input.CaretIndex = _input.Text?.Length ?? 0;
        };
    }

    /// <summary>空白で区切る。全角の空白も区切りにする。</summary>
    internal static IReadOnlyList<string> Split(string? text) =>
        (text ?? "").Split([' ', '　', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>ダイアログを開き、保存したらエイリアスの一覧を返す。取り消したら null。</summary>
    public static Task<IReadOnlyList<string>?> EditAsync(Window owner, string itemTitle, IEnumerable<string> aliases) =>
        new AliasDialog(itemTitle, aliases).ShowDialog<IReadOnlyList<string>?>(owner);
}
