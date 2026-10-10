using System.Text;
using Lucene.Net.Analysis.Ja;
using Lucene.Net.Analysis.Ja.TokenAttributes;
using Lucene.Net.Analysis.TokenAttributes;
using MeCab;
using NMeCab.Specialized;

namespace ReadingSpike;

/// <summary>文字列の読み（カタカナ）を返す形態素解析エンジン。</summary>
public interface IReadingEngine : IDisposable
{
    /// <summary>読みを返す。辞書にない語（英単語など）は表層形のまま連結する。</summary>
    string GetReading(string text);
}

public static class EngineFactory
{
    public static readonly string[] Names = ["none", "nmecab", "mecabdotnet", "kuromoji"];

    public static IReadingEngine Create(string name) => name switch
    {
        "none" => new NoneEngine(),
        "nmecab" => new NMeCabEngine(),
        "mecabdotnet" => new MeCabDotNetEngine(),
        "kuromoji" => new KuromojiEngine(),
        _ => throw new ArgumentException($"未知のエンジンです: {name}"),
    };
}

/// <summary>辞書を読み込まない場合の基準値を測るためのエンジン。</summary>
public sealed class NoneEngine : IReadingEngine
{
    public string GetReading(string text) => text;

    public void Dispose()
    {
    }
}

/// <summary>LibNMeCab（NMeCab の公式パッケージ）と LibNMeCab.IpaDicBin。</summary>
public sealed class NMeCabEngine : IReadingEngine
{
    private readonly MeCabIpaDicTagger _tagger = MeCabIpaDicTagger.Create(Path.Combine(AppContext.BaseDirectory, "IpaDic"));

    public string GetReading(string text)
    {
        var sb = new StringBuilder(text.Length * 2);
        foreach (var node in _tagger.Parse(text))
        {
            sb.Append(string.IsNullOrEmpty(node.Reading) || node.Reading == "*" ? node.Surface : node.Reading);
        }
        return sb.ToString();
    }

    public void Dispose() => _tagger.Dispose();
}

/// <summary>MeCab.DotNet（IPADIC を同梱）。</summary>
public sealed class MeCabDotNetEngine : IReadingEngine
{
    // IPADIC の素性 CSV の 8 列目が読み
    private const int ReadingColumn = 7;

    private readonly MeCabTagger _tagger = MeCabTagger.Create(new MeCabParam { DicDir = Path.Combine(AppContext.BaseDirectory, "dic") });

    public string GetReading(string text)
    {
        var sb = new StringBuilder(text.Length * 2);
        foreach (var node in _tagger.ParseToNodes(text))
        {
            // BOS/EOS ノードは読み飛ばす
            if (node.Stat is MeCabNodeStat.Bos or MeCabNodeStat.Eos)
            {
                continue;
            }

            var features = node.Feature.Split(',');
            sb.Append(features.Length > ReadingColumn && features[ReadingColumn] != "*" ? features[ReadingColumn] : node.Surface);
        }
        return sb.ToString();
    }

    public void Dispose() => _tagger.Dispose();
}

/// <summary>Lucene.Net の Kuromoji（辞書はアセンブリに埋め込み）。</summary>
public sealed class KuromojiEngine : IReadingEngine
{
    private readonly JapaneseTokenizer _tokenizer;
    private readonly ICharTermAttribute _term;
    private readonly IReadingAttribute _reading;

    public KuromojiEngine()
    {
        _tokenizer = new JapaneseTokenizer(new StringReader(""), null, discardPunctuation: false, JapaneseTokenizerMode.NORMAL);
        _term = _tokenizer.AddAttribute<ICharTermAttribute>();
        _reading = _tokenizer.AddAttribute<IReadingAttribute>();
        // SetReader は閉じた状態でしか呼べないため、空の入力をいったん閉じておく
        _tokenizer.Dispose();
        // 辞書は最初のトークン化のときに読み込まれるため、ここで読み込ませておく
        GetReading("初期化");
    }

    public string GetReading(string text)
    {
        var sb = new StringBuilder(text.Length * 2);
        _tokenizer.SetReader(new StringReader(text));
        _tokenizer.Reset();
        while (_tokenizer.IncrementToken())
        {
            sb.Append(_reading.GetReading() ?? _term.ToString());
        }
        _tokenizer.End();
        _tokenizer.Dispose();
        return sb.ToString();
    }

    public void Dispose()
    {
    }
}
