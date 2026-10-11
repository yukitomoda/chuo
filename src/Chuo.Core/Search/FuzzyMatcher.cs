namespace Chuo.Core.Search;

/// <summary>
/// あいまい検索の点数を付ける。どちらの文字列も <see cref="Text.TextNormalizer"/> で正規化しておくこと。
/// </summary>
public static class FuzzyMatcher
{
    /// <summary>連続して一致した場合の基本点。飛び飛びの一致（部分列）より必ず上になるようにする。</summary>
    private const int ContiguousBase = 10_000;

    /// <summary>一致しなければ null を返す。点数が大きいほどよく一致している。</summary>
    public static int? Score(string pattern, string text)
    {
        if (pattern.Length == 0)
        {
            return 0;
        }
        if (pattern.Length > text.Length)
        {
            return null;
        }

        var index = text.IndexOf(pattern, StringComparison.Ordinal);
        if (index >= 0)
        {
            var score = ContiguousBase;
            if (pattern.Length == text.Length)
            {
                score += 3000;
            }
            else if (index == 0)
            {
                score += 2000;
            }
            else if (IsWordStart(text, index))
            {
                score += 1000;
            }
            // 同じ一致なら、前で一致したものと短いものを上にする
            return score - Math.Min(index, 100) * 5 - Math.Min(text.Length - pattern.Length, 100);
        }

        return SubsequenceScore(pattern, text);
    }

    /// <summary>パターンの文字が順に現れるか（例: <c>vsc</c> と <c>visual studio code</c>）。</summary>
    private static int? SubsequenceScore(string pattern, string text)
    {
        // 単語の先頭を優先して拾う方法と、最も近い文字を拾う方法の両方を試し、よい方を採る
        var preferWordStart = SubsequenceScore(pattern, text, preferWordStart: true);
        var nearest = SubsequenceScore(pattern, text, preferWordStart: false);
        return preferWordStart is null ? nearest : Math.Max(preferWordStart.Value, nearest ?? int.MinValue);
    }

    private static int? SubsequenceScore(string pattern, string text, bool preferWordStart)
    {
        var score = 0;
        var position = 0;
        var previous = -2;
        foreach (var c in pattern)
        {
            var found = text.IndexOf(c, position);
            if (found < 0)
            {
                return null;
            }
            if (preferWordStart && found != previous + 1 && !IsWordStart(text, found))
            {
                found = FindWordStart(text, c, found + 1) ?? found;
            }

            score += 10;
            if (found == previous + 1)
            {
                score += 15;
            }
            if (IsWordStart(text, found))
            {
                score += 30;
            }
            score -= Math.Min(found - position, 10);
            previous = found;
            position = found + 1;
        }
        return score - Math.Min(text.Length - pattern.Length, 100) / 4;
    }

    private static int? FindWordStart(string text, char c, int start)
    {
        for (var i = start; i < text.Length; i++)
        {
            if (text[i] == c && IsWordStart(text, i))
            {
                return i;
            }
        }
        return null;
    }

    private static bool IsWordStart(string text, int index) =>
        index == 0 || text[index - 1] is ' ' or '-' or '_' or '.' or '/' or '\\' or '(' or '[';
}
