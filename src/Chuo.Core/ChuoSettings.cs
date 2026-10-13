using System.ComponentModel.DataAnnotations;

namespace Chuo.Core;

/// <summary><c>settings.json</c> のホストの設定。</summary>
public sealed class ChuoSettings
{
    /// <summary>ランチャーを表示するグローバルホットキー（例: <c>Alt+Space</c>）。</summary>
    public string Hotkey { get; set; } = "Alt+Space";

    /// <summary>結果の一覧に表示する最大の件数。</summary>
    public int MaxResults { get; set; } = 8;

    public ImeSettings Ime { get; set; } = new();

    public RenderingSettings Rendering { get; set; } = new();

    /// <summary><see cref="Window"/> の設定のセクション。</summary>
    public const string WindowSection = "window";

    public WindowSettings Window { get; set; } = new();
}

/// <summary>ランチャーのウィンドウの設定。設定画面の一般のページに出す。</summary>
public sealed class WindowSettings
{
    [Display(Name = "表示する画面", Description = "画面が複数あるときに、ランチャーをどの画面に表示するか")]
    public LauncherScreen Screen { get; set; } = LauncherScreen.Cursor;
}

public enum LauncherScreen
{
    [Display(Name = "マウスカーソルのある画面")]
    Cursor,

    [Display(Name = "メインの画面")]
    Primary,
}

public sealed class ImeSettings
{
    /// <summary>ランチャーを表示したときに IME をオフにするか。</summary>
    public bool OffOnShow { get; set; } = true;
}

public sealed class RenderingSettings
{
    /// <summary>
    /// GPU で描画するか。既定はソフトウェア描画で、常駐メモリが大幅に小さくなる（技術検証 #5）。
    /// 変更は再起動後に反映される。
    /// </summary>
    public bool UseGpu { get; set; }

    /// <summary>ランチャーを隠したときに、使っていないメモリを OS に返すか（技術検証 #5）。</summary>
    public bool TrimMemoryOnHide { get; set; } = true;
}
