using System.Text;

namespace Chuo.Core.Text;

/// <summary>
/// 検索のために文字列を正規化する。ひらがなとカタカナ、全角と半角、大文字と小文字を同一視する（要件定義書 3.3）。
/// </summary>
public static class TextNormalizer
{
    public static string Normalize(string text)
    {
        if (text.Length == 0)
        {
            return text;
        }

        // NFKC で全角英数を半角に、半角カナを全角にそろえる
        var normalized = text.Normalize(NormalizationForm.FormKC).ToLowerInvariant();
        return string.Create(normalized.Length, normalized, static (span, source) =>
        {
            for (var i = 0; i < source.Length; i++)
            {
                var c = source[i];
                // カタカナ（ァ〜ヶ）をひらがなにする
                span[i] = c is >= 'ァ' and <= 'ヶ' ? (char)(c - 0x60) : c;
            }
        });
    }
}
