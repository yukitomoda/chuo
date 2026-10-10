namespace ReadingSpike;

/// <summary>読みの付与を試す項目。Windows と GNOME のアプリ名、ファイル名を想定している。</summary>
public static class Samples
{
    public static readonly string[] Items =
    [
        "メモ帳", "電卓", "ペイント", "設定", "エクスプローラー", "タスク マネージャー", "コントロール パネル",
        "コマンド プロンプト", "付箋", "天気", "カレンダー", "写真", "時計", "地図", "拡大鏡", "ナレーター",
        "リモート デスクトップ接続", "文字コード表", "ディスク クリーンアップ", "デバイス マネージャー",
        "イベント ビューアー", "サウンド レコーダー", "切り取り & スケッチ", "映画 & テレビ",
        "端末", "テキストエディター", "ソフトウェア", "システムモニター", "画面キーボード", "ファイル",
        "Microsoft Edge", "Google Chrome", "Visual Studio 2022", "秀丸エディタ", "一太郎", "家計簿",
        "議事録_2026年10月.docx", "見積書（株式会社山田）.xlsx", "東京出張報告.pdf", "新しいフォルダー",
        "ダウンロード", "ドキュメント", "ピクチャ", "日本語入力の設定", "スクリーンショット", "吾輩は猫である.txt",
        "ｶﾀｶﾅﾒﾓ", "ＦＵＬＬ　ＷＩＤＴＨ", "ひらがなのめも",
    ];

    /// <summary>ローマ字の入力（前方一致）で目的の項目に一致することを確かめる組。</summary>
    public static readonly (string Query, string Target)[] RomajiQueries =
    [
        ("memo", "メモ帳"),
        ("dentaku", "電卓"),
        ("settei", "設定"),
        ("ekusupuro", "エクスプローラー"),
        ("fusen", "付箋"),
        ("kakudaikyou", "拡大鏡"),
        ("tanmatsu", "端末"),
        ("gijiroku", "議事録_2026年10月.docx"),
        ("toukyou", "東京出張報告.pdf"),
        ("kakeibo", "家計簿"),
        ("hidemaru", "秀丸エディタ"),
        ("ichitarou", "一太郎"),
    ];

    /// <summary>計測用に、重複しない項目を指定した数だけ作る。</summary>
    public static IEnumerable<string> Bulk(int count)
    {
        for (var i = 0; i < count; i++)
        {
            yield return $"{Items[i % Items.Length]}_{i}";
        }
    }

    /// <summary>CJK 統合漢字の範囲からランダムに選んだ 2〜8 文字の漢字列を作る。辞書の多くのページを引かせるために使う。</summary>
    public static IEnumerable<string> RandomKanji(int count)
    {
        var random = new Random(42);
        for (var i = 0; i < count; i++)
        {
            var chars = new char[random.Next(2, 9)];
            for (var j = 0; j < chars.Length; j++)
            {
                chars[j] = (char)random.Next(0x4E00, 0x9FA0);
            }
            yield return new string(chars);
        }
    }
}
