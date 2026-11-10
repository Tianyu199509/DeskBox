using System.Globalization;
using System.Text;

namespace DeskBox.Services;

internal readonly record struct SearchCalculationResult(
    string NormalizedExpression,
    string FormattedValue,
    double NumericValue);

/// <summary>
/// Zero-reflection, NativeAOT-safe recursive-descent arithmetic evaluator for inline
/// search calculations (for example "=128*3.5", "(1920-660)/2", "2^10", "200*15%").
/// </summary>
internal static class SearchCalculatorEvaluator
{
    private const int MaxExpressionLength = 160;
    private const int MaxNestingDepth = 32;

    public static bool TryEvaluate(string? rawQuery, out SearchCalculationResult result)
    {
        result = default;
        if (string.IsNullOrWhiteSpace(rawQuery))
        {
            return false;
        }

        ReadOnlySpan<char> trimmed = rawQuery.AsSpan().Trim();
        if (trimmed.IsEmpty || trimmed.Length > MaxExpressionLength)
        {
            return false;
        }

        bool hasExplicitEquals = trimmed[0] is '=' or '＝';
        if (hasExplicitEquals)
        {
            trimmed = trimmed[1..].Trim();
            if (trimmed.IsEmpty)
            {
                return false;
            }
        }

        if (!TryNormalizeExpression(trimmed, out string normalized, out bool hasDigit))
        {
            return false;
        }

        if (!hasDigit)
        {
            return false;
        }

        if (!hasExplicitEquals && IsLikelyDateOrIdentifier(normalized))
        {
            return false;
        }

        var parser = new ExpressionParser(normalized);
        if (!parser.TryParseComplete(out double value, out int binaryOrPostfixOperatorCount))
        {
            return false;
        }

        if (!hasExplicitEquals && binaryOrPostfixOperatorCount == 0)
        {
            return false;
        }

        if (double.IsNaN(value) || double.IsInfinity(value))
        {
            return false;
        }

        string formatted = FormatResult(value);
        if (string.IsNullOrEmpty(formatted))
        {
            return false;
        }

        // Avoid redundant "= 42 -> 42" noise unless an operator or parentheses were evaluated,
        // or the user explicitly prefixed '='.
        if (!hasExplicitEquals &&
            string.Equals(normalized.Trim(), formatted, StringComparison.Ordinal))
        {
            return false;
        }

        result = new SearchCalculationResult(
            NormalizedExpression: PrettifyExpression(normalized),
            FormattedValue: formatted,
            NumericValue: value);
        return true;
    }

    private static bool TryNormalizeExpression(
        ReadOnlySpan<char> input,
        out string normalized,
        out bool hasDigit)
    {
        normalized = string.Empty;
        hasDigit = false;
        var builder = new StringBuilder(input.Length);

        for (int i = 0; i < input.Length; i++)
        {
            char c = input[i];
            if (c is >= '0' and <= '9')
            {
                hasDigit = true;
                builder.Append(c);
                continue;
            }

            if (c is >= '０' and <= '９')
            {
                hasDigit = true;
                builder.Append((char)('0' + (c - '０')));
                continue;
            }

            switch (c)
            {
                case '.':
                case '．':
                    builder.Append('.');
                    break;
                case ',':
                case '，':
                case '_':
                    // Allow numeric group separators only between digits (e.g. 1,000,000).
                    if (i == 0 || i + 1 >= input.Length ||
                        !IsAsciiOrFullWidthDigit(input[i - 1]) ||
                        !IsAsciiOrFullWidthDigit(input[i + 1]))
                    {
                        return false;
                    }
                    break;
                case '+':
                case '＋':
                    builder.Append('+');
                    break;
                case '-':
                case '－':
                case '−':
                case '–':
                    builder.Append('-');
                    break;
                case '*':
                case '＊':
                case '×':
                    builder.Append('*');
                    break;
                case '/':
                case '／':
                case '÷':
                    builder.Append('/');
                    break;
                case '%':
                case '％':
                    builder.Append('%');
                    break;
                case '^':
                case '＾':
                    builder.Append('^');
                    break;
                case '(':
                case '（':
                    builder.Append('(');
                    break;
                case ')':
                case '）':
                    builder.Append(')');
                    break;
                default:
                    if (char.IsWhiteSpace(c))
                    {
                        builder.Append(' ');
                    }
                    else
                    {
                        return false;
                    }
                    break;
            }
        }

        normalized = builder.ToString();
        return builder.Length > 0;
    }

    private static bool IsAsciiOrFullWidthDigit(char c) =>
        (c is >= '0' and <= '9') || (c is >= '０' and <= '９');

    /// <summary>
    /// Suppresses implicit evaluation for unspaced date-like or hyphenated code patterns
    /// such as "2026-11-04", "2026/11/04", or "400-800-1234" when '=' is not prefixed.
    /// </summary>
    private static bool IsLikelyDateOrIdentifier(string normalized)
    {
        if (normalized.Contains(' '))
        {
            return false;
        }

        int dashCount = 0;
        int slashCount = 0;
        foreach (char c in normalized)
        {
            if (c == '-')
            {
                dashCount++;
            }
            else if (c == '/')
            {
                slashCount++;
            }
            else if (c is not (>= '0' and <= '9'))
            {
                return false;
            }
        }

        // Multi-segment dates/codes like 2026-11-04 or 2026/11/04
        if ((dashCount >= 2 && slashCount == 0) || (slashCount >= 2 && dashCount == 0))
        {
            return true;
        }

        // Year-month like 2026-11 when first group is 4 digits (1900..2199) and second is 01..12
        if (dashCount == 1 && slashCount == 0)
        {
            int dashIndex = normalized.IndexOf('-');
            if (dashIndex == 4 &&
                normalized.Length is 6 or 7 &&
                int.TryParse(normalized.AsSpan(0, 4), NumberStyles.None, CultureInfo.InvariantCulture, out int year) &&
                year is >= 1900 and <= 2199 &&
                int.TryParse(normalized.AsSpan(5), NumberStyles.None, CultureInfo.InvariantCulture, out int month) &&
                month is >= 1 and <= 12)
            {
                return true;
            }
        }

        return false;
    }

    private static string FormatResult(double value)
    {
        if (value == 0)
        {
            return "0";
        }

        double abs = Math.Abs(value);
        if (abs >= 1e-6 && abs < 1e15)
        {
            double rounded = Math.Round(value, 10, MidpointRounding.AwayFromZero);
            if (rounded == 0)
            {
                return "0";
            }

            try
            {
                decimal dec = (decimal)rounded;
                return dec.ToString("0.##########", CultureInfo.InvariantCulture);
            }
            catch (OverflowException)
            {
                // Fall back to double formatting below.
            }
        }

        return value.ToString("G12", CultureInfo.InvariantCulture);
    }

    private static string PrettifyExpression(string normalized)
    {
        var sb = new StringBuilder(normalized.Length + 8);
        bool prevSpace = false;
        foreach (char c in normalized.Trim())
        {
            if (c == ' ')
            {
                if (!prevSpace)
                {
                    sb.Append(' ');
                    prevSpace = true;
                }
                continue;
            }

            prevSpace = false;
            sb.Append(c);
        }

        return sb.ToString();
    }

    private ref struct ExpressionParser
    {
        private readonly ReadOnlySpan<char> _text;
        private int _pos;
        private int _depth;
        private int _operatorCount;

        public ExpressionParser(string text)
        {
            _text = text.AsSpan();
            _pos = 0;
            _depth = 0;
            _operatorCount = 0;
        }

        public bool TryParseComplete(out double value, out int operatorCount)
        {
            value = 0;
            operatorCount = 0;
            if (!TryParseAdditive(out value))
            {
                return false;
            }

            SkipWhitespace();
            if (_pos != _text.Length)
            {
                return false;
            }

            operatorCount = _operatorCount;
            return true;
        }

        private bool TryParseAdditive(out double value)
        {
            if (!TryParseMultiplicative(out value))
            {
                return false;
            }

            while (true)
            {
                SkipWhitespace();
                if (_pos >= _text.Length)
                {
                    return true;
                }

                char op = _text[_pos];
                if (op is not ('+' or '-'))
                {
                    return true;
                }

                _pos++;
                _operatorCount++;
                if (!TryParseMultiplicative(out double rhs))
                {
                    return false;
                }

                value = op == '+' ? value + rhs : value - rhs;
            }
        }

        private bool TryParseMultiplicative(out double value)
        {
            if (!TryParsePower(out value))
            {
                return false;
            }

            while (true)
            {
                SkipWhitespace();
                if (_pos >= _text.Length)
                {
                    return true;
                }

                char op = _text[_pos];
                if (op is '*' or '/')
                {
                    _pos++;
                    _operatorCount++;
                    if (!TryParsePower(out double rhs))
                    {
                        return false;
                    }

                    if (op == '/' && rhs == 0)
                    {
                        return false;
                    }

                    value = op == '*' ? value * rhs : value / rhs;
                    continue;
                }

                // '%' is binary modulo only when immediately followed by another operand;
                // otherwise TryParsePostfix already consumed it as '/ 100'.
                if (op == '%' && StartsOperandAfter(_pos + 1))
                {
                    _pos++;
                    _operatorCount++;
                    if (!TryParsePower(out double rhs) || rhs == 0)
                    {
                        return false;
                    }

                    value %= rhs;
                    continue;
                }

                return true;
            }
        }

        private bool TryParsePower(out double value)
        {
            if (!TryParseUnary(out value))
            {
                return false;
            }

            SkipWhitespace();
            if (_pos < _text.Length && _text[_pos] == '^')
            {
                _pos++;
                _operatorCount++;
                if (++_depth > MaxNestingDepth)
                {
                    return false;
                }

                bool ok = TryParsePower(out double exponent);
                _depth--;
                if (!ok)
                {
                    return false;
                }

                value = Math.Pow(value, exponent);
            }

            return true;
        }

        private bool TryParseUnary(out double value)
        {
            value = 0;
            SkipWhitespace();
            if (_pos >= _text.Length)
            {
                return false;
            }

            char c = _text[_pos];
            if (c is '+' or '-')
            {
                _pos++;
                if (++_depth > MaxNestingDepth)
                {
                    return false;
                }

                bool ok = TryParseUnary(out double inner);
                _depth--;
                if (!ok)
                {
                    return false;
                }

                value = c == '-' ? -inner : inner;
                return true;
            }

            return TryParsePostfix(out value);
        }

        private bool TryParsePostfix(out double value)
        {
            if (!TryParsePrimary(out value))
            {
                return false;
            }

            while (true)
            {
                SkipWhitespace();
                if (_pos < _text.Length && _text[_pos] == '%' && !StartsOperandAfter(_pos + 1))
                {
                    _pos++;
                    _operatorCount++;
                    value /= 100.0;
                    continue;
                }

                return true;
            }
        }

        private bool TryParsePrimary(out double value)
        {
            value = 0;
            SkipWhitespace();
            if (_pos >= _text.Length)
            {
                return false;
            }

            if (_text[_pos] == '(')
            {
                _pos++;
                if (++_depth > MaxNestingDepth)
                {
                    return false;
                }

                bool ok = TryParseAdditive(out value);
                _depth--;
                if (!ok)
                {
                    return false;
                }

                SkipWhitespace();
                if (_pos >= _text.Length || _text[_pos] != ')')
                {
                    return false;
                }

                _pos++;
                return true;
            }

            return TryParseNumber(out value);
        }

        private bool TryParseNumber(out double value)
        {
            value = 0;
            int start = _pos;
            bool sawDigit = false;
            bool sawDot = false;

            while (_pos < _text.Length)
            {
                char c = _text[_pos];
                if (c is >= '0' and <= '9')
                {
                    sawDigit = true;
                    _pos++;
                }
                else if (c == '.')
                {
                    if (sawDot)
                    {
                        return false;
                    }

                    sawDot = true;
                    _pos++;
                }
                else
                {
                    break;
                }
            }

            if (!sawDigit)
            {
                return false;
            }

            return double.TryParse(
                _text[start.._pos],
                NumberStyles.AllowDecimalPoint,
                CultureInfo.InvariantCulture,
                out value);
        }

        private bool StartsOperandAfter(int index)
        {
            while (index < _text.Length && _text[index] == ' ')
            {
                index++;
            }

            if (index >= _text.Length)
            {
                return false;
            }

            char next = _text[index];
            return (next is >= '0' and <= '9') || next == '.' || next == '(';
        }

        private void SkipWhitespace()
        {
            while (_pos < _text.Length && _text[_pos] == ' ')
            {
                _pos++;
            }
        }
    }
}
