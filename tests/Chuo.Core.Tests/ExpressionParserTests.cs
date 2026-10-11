namespace Chuo.Core.Tests;

public class ExpressionParserTests
{
    [Theory]
    [InlineData("1+2*3", 7)]
    [InlineData("(1+2)*3", 9)]
    [InlineData("10-2-3", 5)]
    [InlineData("2^3^2", 512)]
    [InlineData("-2^2", -4)]
    [InlineData("2^-1", 0.5)]
    [InlineData("7%3", 1)]
    [InlineData("1,000 * 2", 2000)]
    [InlineData(" 1.5 / 2 ", 0.75)]
    public void 計算できる(string expression, double expected)
    {
        Assert.True(Chuo.Plugins.Calculator.ExpressionParser.TryEvaluate(expression, out var value));
        Assert.Equal(expected, value, 10);
    }

    [Theory]
    [InlineData("1+")]
    [InlineData("(1+2")]
    [InlineData("1/0")]
    [InlineData("abc")]
    public void 計算できない式はfalse(string expression) =>
        Assert.False(Chuo.Plugins.Calculator.ExpressionParser.TryEvaluate(expression, out _));
}
