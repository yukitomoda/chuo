using Chuo.Core.Text;

namespace Chuo.Core.Tests;

public class TextNormalizerTests
{
    [Theory]
    [InlineData("メモ帳", "めも帳")]
    [InlineData("ﾒﾓ帳", "めも帳")]
    [InlineData("ＡＢＣ１２３", "abc123")]
    [InlineData("Visual Studio", "visual studio")]
    [InlineData("ヴァイオリン", "ゔぁいおりん")]
    public void ひらがなとカタカナ_全角と半角_大文字と小文字を同一視する(string input, string expected) =>
        Assert.Equal(expected, TextNormalizer.Normalize(input));
}
