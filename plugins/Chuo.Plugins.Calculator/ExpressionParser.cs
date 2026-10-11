using System.Globalization;

namespace Chuo.Plugins.Calculator;

/// <summary>
/// 四則演算、剰余（%）、べき乗（^）、括弧を含む数式を計算する再帰下降の構文解析器。
/// 数字の桁区切りのカンマは無視する。
/// </summary>
public static class ExpressionParser
{
    public static bool TryEvaluate(string expression, out double value)
    {
        var parser = new Parser(expression.Replace(",", "", StringComparison.Ordinal));
        try
        {
            value = parser.ParseExpression();
            parser.SkipSpaces();
            return parser.AtEnd && double.IsFinite(value);
        }
        catch (FormatException)
        {
            value = 0;
            return false;
        }
    }

    private ref struct Parser(string text)
    {
        private readonly string _text = text;
        private int _position;

        public readonly bool AtEnd => _position >= _text.Length;

        // expression := term (('+' | '-') term)*
        public double ParseExpression()
        {
            var value = ParseTerm();
            while (true)
            {
                if (TryConsume('+'))
                {
                    value += ParseTerm();
                }
                else if (TryConsume('-'))
                {
                    value -= ParseTerm();
                }
                else
                {
                    return value;
                }
            }
        }

        // term := unary (('*' | '/' | '%') unary)*
        private double ParseTerm()
        {
            var value = ParseUnary();
            while (true)
            {
                if (TryConsume('*'))
                {
                    value *= ParseUnary();
                }
                else if (TryConsume('/'))
                {
                    value /= ParseUnary();
                }
                else if (TryConsume('%'))
                {
                    value %= ParseUnary();
                }
                else
                {
                    return value;
                }
            }
        }

        // unary := ('-' | '+') unary | power   （数学と同じく -2^2 は -(2^2) とする）
        private double ParseUnary()
        {
            if (TryConsume('-'))
            {
                return -ParseUnary();
            }
            if (TryConsume('+'))
            {
                return ParseUnary();
            }
            return ParsePower();
        }

        // power := primary ('^' unary)?   （右結合）
        private double ParsePower()
        {
            var value = ParsePrimary();
            return TryConsume('^') ? Math.Pow(value, ParseUnary()) : value;
        }

        // primary := number | '(' expression ')'
        private double ParsePrimary()
        {
            if (TryConsume('('))
            {
                var value = ParseExpression();
                if (!TryConsume(')'))
                {
                    throw new FormatException("閉じ括弧がありません。");
                }
                return value;
            }

            SkipSpaces();
            var start = _position;
            while (!AtEnd && (char.IsAsciiDigit(_text[_position]) || _text[_position] == '.'))
            {
                _position++;
            }
            if (start == _position || !double.TryParse(_text.AsSpan(start, _position - start), NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
            {
                throw new FormatException("数値がありません。");
            }
            return number;
        }

        public void SkipSpaces()
        {
            while (!AtEnd && char.IsWhiteSpace(_text[_position]))
            {
                _position++;
            }
        }

        private bool Peek(char c)
        {
            SkipSpaces();
            return !AtEnd && _text[_position] == c;
        }

        private bool TryConsume(char c)
        {
            if (!Peek(c))
            {
                return false;
            }
            _position++;
            return true;
        }
    }
}
