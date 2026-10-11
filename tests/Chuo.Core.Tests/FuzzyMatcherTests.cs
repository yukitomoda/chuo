using Chuo.Core.Search;

namespace Chuo.Core.Tests;

public class FuzzyMatcherTests
{
    [Fact]
    public void 一致しなければnull() => Assert.Null(FuzzyMatcher.Score("xyz", "firefox"));

    [Fact]
    public void 順に現れる文字は一致する() => Assert.NotNull(FuzzyMatcher.Score("vsc", "visual studio code"));

    [Fact]
    public void 完全一致_前方一致_単語の先頭_途中の順に高い()
    {
        var exact = FuzzyMatcher.Score("code", "code");
        var prefix = FuzzyMatcher.Score("code", "code insiders");
        var wordStart = FuzzyMatcher.Score("code", "visual studio code");
        var middle = FuzzyMatcher.Score("code", "unicode");

        Assert.True(exact > prefix);
        Assert.True(prefix > wordStart);
        Assert.True(wordStart > middle);
    }

    [Fact]
    public void 連続した一致は飛び飛びの一致より高い() =>
        Assert.True(FuzzyMatcher.Score("fire", "unfire") > FuzzyMatcher.Score("fire", "f-i-r-e"));

    [Fact]
    public void 飛び飛びの一致では単語の先頭を優先する() =>
        Assert.True(FuzzyMatcher.Score("vsc", "visual studio code") > FuzzyMatcher.Score("vsc", "visible scope"));
}
