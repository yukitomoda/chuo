using System.Text;

namespace Chuo.Plugins.Shared;

/// <summary>
/// .desktop ファイルの <c>[Desktop Entry]</c> グループ（Desktop Entry Specification）。
/// </summary>
internal sealed class DesktopEntry
{
    private const string MainGroup = "Desktop Entry";

    private readonly Dictionary<string, string> _values;

    private DesktopEntry(Dictionary<string, string> values) => _values = values;

    public static DesktopEntry Parse(TextReader reader)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        string? group = null;
        while (reader.ReadLine() is { } rawLine)
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line[0] == '#')
            {
                continue;
            }
            if (line[0] == '[' && line[^1] == ']')
            {
                group = line[1..^1];
                continue;
            }
            if (group != MainGroup)
            {
                continue;
            }
            var separator = line.IndexOf('=');
            if (separator <= 0)
            {
                continue;
            }
            // 同じキーが複数あるときは最初のものを使う
            values.TryAdd(line[..separator].TrimEnd(), line[(separator + 1)..].TrimStart());
        }
        return new DesktopEntry(values);
    }

    /// <summary>エスケープを解いた値。キーがなければ null。</summary>
    public string? Get(string key) => _values.TryGetValue(key, out var value) ? Unescape(value) : null;

    /// <summary>
    /// ロケールに合わせた値（例: <c>Name[ja]</c>）。<paramref name="locale"/> は <c>LANG</c> の形式（例: <c>ja_JP.UTF-8</c>）。
    /// </summary>
    public string? GetLocalized(string key, string? locale)
    {
        foreach (var candidate in LocaleCandidates(locale))
        {
            if (Get($"{key}[{candidate}]") is { } value)
            {
                return value;
            }
        }
        return Get(key);
    }

    public bool GetBool(string key) => Get(key) == "true";

    /// <summary>セミコロンで区切られた値の一覧（例: <c>Keywords</c>、<c>OnlyShowIn</c>）。</summary>
    public static IReadOnlyList<string> SplitList(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return [];
        }
        var items = new List<string>();
        var current = new StringBuilder();
        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] == '\\' && i + 1 < value.Length && value[i + 1] == ';')
            {
                current.Append(';');
                i++;
            }
            else if (value[i] == ';')
            {
                items.Add(current.ToString());
                current.Clear();
            }
            else
            {
                current.Append(value[i]);
            }
        }
        items.Add(current.ToString());
        return items.Where(s => s.Length > 0).ToList();
    }

    /// <summary>仕様で決められた順（lang_COUNTRY@MODIFIER、lang_COUNTRY、lang@MODIFIER、lang）に候補を返す。</summary>
    private static IEnumerable<string> LocaleCandidates(string? locale)
    {
        if (string.IsNullOrEmpty(locale) || locale is "C" or "POSIX")
        {
            yield break;
        }
        var dot = locale.IndexOf('.');
        var at = locale.IndexOf('@');
        var modifier = at >= 0 ? locale[at..] : "";
        var name = locale[..(dot >= 0 ? dot : at >= 0 ? at : locale.Length)];
        var underscore = name.IndexOf('_');
        var lang = underscore >= 0 ? name[..underscore] : name;

        if (modifier.Length > 0 && underscore >= 0)
        {
            yield return name + modifier;
        }
        if (underscore >= 0)
        {
            yield return name;
        }
        if (modifier.Length > 0)
        {
            yield return lang + modifier;
        }
        yield return lang;
    }

    private static string Unescape(string value)
    {
        if (!value.Contains('\\'))
        {
            return value;
        }
        var result = new StringBuilder(value.Length);
        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] == '\\' && i + 1 < value.Length)
            {
                i++;
                result.Append(value[i] switch
                {
                    's' => " ",
                    'n' => "\n",
                    't' => "\t",
                    'r' => "\r",
                    '\\' => "\\",
                    // リストの区切り（\;）は SplitList で扱うので残す
                    var c => "\\" + c,
                });
            }
            else
            {
                result.Append(value[i]);
            }
        }
        return result.ToString();
    }
}

/// <summary>.desktop の <c>Exec</c> の値を、実行するコマンドの引数の並びにする。</summary>
internal static class DesktopExec
{
    /// <summary>
    /// 引数を区切り、フィールドコード（<c>%f</c> など）を展開する。ファイルや URL は渡さないので、それらのコードは取り除く。
    /// 形式が正しくなければ null を返す。
    /// </summary>
    public static IReadOnlyList<string>? Parse(string exec, string? name, string? icon, string desktopFilePath)
    {
        var arguments = new List<string>();
        var current = new StringBuilder();
        var hasArgument = false;
        var quoted = false;

        for (var i = 0; i < exec.Length; i++)
        {
            var c = exec[i];
            if (quoted)
            {
                if (c == '"')
                {
                    quoted = false;
                }
                else if (c == '\\' && i + 1 < exec.Length && exec[i + 1] is '"' or '`' or '$' or '\\')
                {
                    current.Append(exec[++i]);
                }
                else
                {
                    current.Append(c);
                }
            }
            else if (c == '"')
            {
                quoted = true;
                hasArgument = true;
            }
            else if (c == ' ')
            {
                Flush();
            }
            else if (c == '%' && i + 1 < exec.Length)
            {
                var code = exec[++i];
                switch (code)
                {
                    case '%':
                        current.Append('%');
                        hasArgument = true;
                        break;
                    case 'c':
                        current.Append(name);
                        hasArgument = true;
                        break;
                    case 'k':
                        current.Append(desktopFilePath);
                        hasArgument = true;
                        break;
                    case 'i':
                        // %i は単独の引数のときだけ「--icon アイコン」の2つの引数になる
                        if (!string.IsNullOrEmpty(icon) && current.Length == 0 && !hasArgument)
                        {
                            arguments.Add("--icon");
                            arguments.Add(icon);
                        }
                        break;
                }
                // %f %F %u %U と、廃止された %d %D %n %N %v %m は取り除く
            }
            else
            {
                current.Append(c);
                hasArgument = true;
            }
        }

        if (quoted)
        {
            return null;
        }
        Flush();
        return arguments.Count > 0 ? arguments : null;

        void Flush()
        {
            if (hasArgument)
            {
                arguments.Add(current.ToString());
            }
            current.Clear();
            hasArgument = false;
        }
    }
}
