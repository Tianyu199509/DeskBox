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
            if (IsNumberTokenChar(c))
            {
                int start = i;
                while (i + 1 < input.Length && IsNumberTokenChar(input[i + 1]))
                {
                    i++;
                }

                if (!TryAppendNormalizedNumberToken(input[start..(i + 1)], builder, out bool tokenHasDigit))
                {
                    return false;
                }

                hasDigit |= tokenHasDigit;
                continue;
            }

            switch (c)
            {
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

    private static bool IsNumberTokenChar(char c) =>
        IsAsciiOrFullWidthDigit(c) || c is '.' or '．' or ',' or '，' or '_';

    private static bool TryAppendNormalizedNumberToken(
        ReadOnlySpan<char> rawToken,
        StringBuilder destination,
        out bool hasDigit)
    {
        hasDigit = false;
        var cleanedBuilder = new StringBuilder(rawToken.Length);
        int dotCount = 0;
        int commaCount = 0;

        for (int k = 0; k < rawToken.Length; k++)
        {
            char c = rawToken[k];
            if (c is >= '0' and <= '9')
            {
                hasDigit = true;
                cleanedBuilder.Append(c);
            }
            else if (c is >= '０' and <= '９')
            {
                hasDigit = true;
                cleanedBuilder.Append((char)('0' + (c - '０')));
            }
            else if (c is '.' or '．')
            {
                dotCount++;
                cleanedBuilder.Append('.');
            }
            else if (c is ',' or '，')
            {
                commaCount++;
                cleanedBuilder.Append(',');
            }
            else if (c == '_')
            {
                if (k == 0 || k + 1 >= rawToken.Length ||
                    !IsAsciiOrFullWidthDigit(rawToken[k - 1]) ||
                    !IsAsciiOrFullWidthDigit(rawToken[k + 1]))
                {
                    return false;
                }
            }
        }

        if (!hasDigit)
        {
            return false;
        }

        string cleaned = cleanedBuilder.ToString();
        ReadOnlySpan<char> span = cleaned.AsSpan();

        if (commaCount == 0)
        {
            if (dotCount <= 1)
            {
                destination.Append(span);
                return true;
            }

            if (!IsValidThousandsGrouping(span, '.'))
            {
                return false;
            }

            AppendWithoutChar(destination, span, '.');
            return true;
        }

        if (dotCount == 0)
        {
            if (commaCount >= 2)
            {
                if (!IsValidThousandsGrouping(span, ','))
                {
                    return false;
                }

                AppendWithoutChar(destination, span, ',');
                return true;
            }

            int commaIndex = span.IndexOf(',');
            ReadOnlySpan<char> before = span[..commaIndex];
            ReadOnlySpan<char> after = span[(commaIndex + 1)..];
            if (after.IsEmpty)
            {
                return false;
            }

            // Treat [1-9]d{0,2},ddd (e.g. 1,000 or 12,500) as thousands grouping;
            // treat 1,5 / 19,99 / 0,125 / ,5 / 1000,5 as decimal comma.
            if (before.Length is >= 1 and <= 3 && before[0] != '0' && after.Length == 3)
            {
                destination.Append(before);
                destination.Append(after);
                return true;
            }

            destination.Append(before);
            destination.Append('.');
            destination.Append(after);
            return true;
        }

        // Mixed '.' and ',': either 1,234.56 (',' grouping + '.' decimal) or 1.234,56 ('.' grouping + ',' decimal).
        int lastDot = span.LastIndexOf('.');
        int lastComma = span.LastIndexOf(',');
        if (lastDot > lastComma)
        {
            if (dotCount != 1 || lastDot + 1 >= span.Length || !IsValidThousandsGrouping(span[..lastDot], ','))
            {
                return false;
            }

            AppendWithoutChar(destination, span[..lastDot], ',');
            destination.Append('.');
            destination.Append(span[(lastDot + 1)..]);
            return true;
        }

        if (commaCount != 1 || lastComma + 1 >= span.Length || !IsValidThousandsGrouping(span[..lastComma], '.'))
        {
            return false;
        }

        AppendWithoutChar(destination, span[..lastComma], '.');
        destination.Append('.');
        destination.Append(span[(lastComma + 1)..]);
        return true;
    }

    private static bool IsValidThousandsGrouping(ReadOnlySpan<char> text, char separator)
    {
        if (text.IsEmpty)
        {
            return false;
        }

        int firstSep = text.IndexOf(separator);
        if (firstSep is <= 0 or > 3 || text[0] == '0')
        {
            return false;
        }

        for (int i = 0; i < firstSep; i++)
        {
            if (text[i] is < '0' or > '9')
            {
                return false;
            }
        }

        int pos = firstSep;
        while (pos < text.Length)
        {
            if (text[pos] != separator || pos + 4 > text.Length)
            {
                return false;
            }

            for (int j = 1; j <= 3; j++)
            {
                if (text[pos + j] is < '0' or > '9')
                {
                    return false;
                }
            }

            pos += 4;
        }

        return true;
    }

    private static void AppendWithoutChar(StringBuilder destination, ReadOnlySpan<char> span, char skip)
    {
        foreach (char ch in span)
        {
            if (ch != skip)
            {
                destination.Append(ch);
            }
        }
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
