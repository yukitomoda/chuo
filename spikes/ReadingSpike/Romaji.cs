using System.Text;

namespace ReadingSpike;

/// <summary>
/// 読み（カタカナ）を、検索用の正規化した文字列とローマ字（ヘボン式）に変換する。
/// 検証用の最小限の実装であり、訓令式などの表記揺れ（si/shi、tu/tsu など）にはまだ対応しない。
/// </summary>
public static class Romaji
{
    private static readonly Dictionary<string, string> Table = Build();

    /// <summary>NFKC で全角と半角をそろえ、ひらがなをカタカナに、英字を小文字にする。</summary>
    public static string Normalize(string text)
    {
        var nfkc = text.Normalize(NormalizationForm.FormKC);
        var sb = new StringBuilder(nfkc.Length);
        foreach (var c in nfkc)
        {
            sb.Append(c switch
            {
                >= 'ぁ' and <= 'ゖ' => (char)(c + ('ァ' - 'ぁ')),
                _ => char.ToLowerInvariant(c),
            });
        }
        return sb.ToString();
    }

    public static string FromKatakana(string reading)
    {
        var text = Normalize(reading);
        var sb = new StringBuilder(text.Length * 2);
        var doubleNext = false;

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == 'ッ')
            {
                doubleNext = true;
                continue;
            }
            if (c == 'ー')
            {
                // 長音は直前の母音を伸ばさずに読み飛ばす（コーヒー → kohi）。表記揺れの扱いは本実装で決める
                continue;
            }

            // 拗音（キャ など）を優先して 2 文字で引く
            string? romaji = null;
            if (i + 1 < text.Length && Table.TryGetValue(text.Substring(i, 2), out var pair))
            {
                romaji = pair;
                i++;
            }
            else if (Table.TryGetValue(c.ToString(), out var single))
            {
                romaji = single;
            }

            if (romaji is null)
            {
                sb.Append(c);
                doubleNext = false;
                continue;
            }

            if (doubleNext)
            {
                sb.Append(romaji.StartsWith("ch") ? 't' : romaji[0]);
                doubleNext = false;
            }
            sb.Append(romaji);
        }

        return sb.ToString();
    }

    private static Dictionary<string, string> Build()
    {
        var table = new Dictionary<string, string>();

        void Row(string kana, params string[] romaji)
        {
            for (var i = 0; i < kana.Length; i++)
            {
                if (romaji[i] != "")
                {
                    table[kana[i].ToString()] = romaji[i];
                }
            }
        }

        Row("アイウエオ", "a", "i", "u", "e", "o");
        Row("カキクケコ", "ka", "ki", "ku", "ke", "ko");
        Row("ガギグゲゴ", "ga", "gi", "gu", "ge", "go");
        Row("サシスセソ", "sa", "shi", "su", "se", "so");
        Row("ザジズゼゾ", "za", "ji", "zu", "ze", "zo");
        Row("タチツテト", "ta", "chi", "tsu", "te", "to");
        Row("ダヂヅデド", "da", "ji", "zu", "de", "do");
        Row("ナニヌネノ", "na", "ni", "nu", "ne", "no");
        Row("ハヒフヘホ", "ha", "hi", "fu", "he", "ho");
        Row("バビブベボ", "ba", "bi", "bu", "be", "bo");
        Row("パピプペポ", "pa", "pi", "pu", "pe", "po");
        Row("マミムメモ", "ma", "mi", "mu", "me", "mo");
        Row("ヤユヨ", "ya", "yu", "yo");
        Row("ラリルレロ", "ra", "ri", "ru", "re", "ro");
        Row("ワヲン", "wa", "o", "n");
        Row("ァィゥェォャュョヮヴ", "a", "i", "u", "e", "o", "ya", "yu", "yo", "wa", "vu");

        // 拗音
        foreach (var (head, consonant) in new[]
                 {
                     ("キ", "ky"), ("ギ", "gy"), ("シ", "sh"), ("ジ", "j"), ("チ", "ch"), ("ヂ", "j"), ("ニ", "ny"),
                     ("ヒ", "hy"), ("ビ", "by"), ("ピ", "py"), ("ミ", "my"), ("リ", "ry"),
                 })
        {
            table[head + "ャ"] = consonant + "a";
            table[head + "ュ"] = consonant + "u";
            table[head + "ョ"] = consonant + "o";
        }

        // 外来語の表記
        table["シェ"] = "she";
        table["ジェ"] = "je";
        table["チェ"] = "che";
        table["ティ"] = "ti";
        table["ディ"] = "di";
        table["デュ"] = "dyu";
        table["ファ"] = "fa";
        table["フィ"] = "fi";
        table["フェ"] = "fe";
        table["フォ"] = "fo";
        table["ウィ"] = "wi";
        table["ウェ"] = "we";
        table["ウォ"] = "wo";
        table["ヴァ"] = "va";
        table["ヴィ"] = "vi";
        table["ヴェ"] = "ve";
        table["ヴォ"] = "vo";

        return table;
    }
}
