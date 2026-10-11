using System.Globalization;
using Chuo.Abstractions;

namespace Chuo.Plugins.Calculator;

/// <summary>数式を計算する。数字と演算子だけの入力なら、キーワードなしでも結果を出す。</summary>
[LiveQueryProvider(Keyword = "=", GlobalTrigger = @"^[\d\s+\-*/().,^%]+$")]
public sealed class CalculatorProvider : ILiveQueryProvider
{
    public ValueTask<IReadOnlyList<ResultItem>> QueryAsync(Query query, CancellationToken cancellationToken)
    {
        var expression = query.Text.Trim();
        // 数字だけの入力（例: 2026）は計算とみなさない
        if (query.Keyword is null && !expression.Any(c => c is '+' or '-' or '*' or '/' or '^' or '%'))
        {
            return ValueTask.FromResult<IReadOnlyList<ResultItem>>([]);
        }
        if (!ExpressionParser.TryEvaluate(expression, out var value))
        {
            return ValueTask.FromResult<IReadOnlyList<ResultItem>>([]);
        }

        var text = value.ToString("G15", CultureInfo.InvariantCulture);
        ResultItem item = new()
        {
            Id = "result",
            Title = text,
            Subtitle = $"{expression} の計算結果（Enter でコピー）",
            Actions = [ItemAction.CopyText(text)],
        };
        return ValueTask.FromResult<IReadOnlyList<ResultItem>>([item]);
    }
}
