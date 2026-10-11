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
