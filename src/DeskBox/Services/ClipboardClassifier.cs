using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using DeskBox.Models;

namespace DeskBox.Services;

/// <summary>
/// Classifies plain-text clipboard payloads into semantic sub-kinds (URL, Color,
/// Email, Path, HTML/RTF) and detects/masks high-confidence credentials for safe
/// always-on-desktop presentation.
/// </summary>
public static partial class ClipboardClassifier
{
    public const int SummaryMaxChars = 256;
    private const int SummaryMaxScanChars = 1024;

    [GeneratedRegex(@"^#(?:[0-9a-fA-F]{3}|[0-9a-fA-F]{6}|[0-9a-fA-F]{8})$", RegexOptions.CultureInvariant)]
    private static partial Regex HexColorRegex();

    [GeneratedRegex(@"^rgba?\(\s*(\d{1,3})\s*,\s*(\d{1,3})\s*,\s*(\d{1,3})(?:\s*[,/]\s*([0-9.]+%?))?\s*\)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex RgbColorRegex();

    [GeneratedRegex(@"^hsla?\(\s*([0-9.]+)(?:deg)?\s*,\s*([0-9.]+)%\s*,\s*([0-9.]+)%(?:\s*[,/]\s*([0-9.]+%?))?\s*\)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex HslColorRegex();

    [GeneratedRegex(@"^[A-Za-z0-9._%+\-]+@[A-Za-z0-9.\-]+\.[A-Za-z]{2,24}$", RegexOptions.CultureInvariant)]
    private static partial Regex EmailRegex();

    [GeneratedRegex(@"(?:sk-(?:proj-|ant-|live-|test-)?[A-Za-z0-9_\-]{20,}|gh[pousr]_[A-Za-z0-9]{24,}|github_pat_[A-Za-z0-9_]{28,}|xox[baprs]-[A-Za-z0-9\-]{20,}|AKIA[0-9A-Z]{16}|AIza[0-9A-Za-z\-_]{35}|eyJ[A-Za-z0-9_\-]{12,}\.eyJ[A-Za-z0-9_\-]{12,}\.[A-Za-z0-9_\-]{12,})", RegexOptions.CultureInvariant)]
    private static partial Regex HighConfidenceTokenRegex();

    [GeneratedRegex(@"(?i)(?<![A-Za-z0-9])(?:[A-Za-z0-9_]*_)?(?:api[_-]?key|access[_-]?token|client[_-]?secret|secret[_-]?key|authorization\s*:\s*bearer)\s*[:=]\s*['""]?([A-Za-z0-9_\-\.~+/=]{16,})", RegexOptions.CultureInvariant)]
    private static partial Regex LabeledSecretRegex();

    public static string BuildSummary(string? plainText)
    {
        if (string.IsNullOrEmpty(plainText))
        {
            return string.Empty;
        }

        int length = plainText.Length;
        int start = 0;
        while (start < length && char.IsWhiteSpace(plainText[start]))
        {
            start++;
        }

        if (start >= length)
        {
            return string.Empty;
        }

        // Scan only the first ~1,024 chars after leading whitespace and collapse blank lines in a single pass.
        int scanEnd = Math.Min(length, start + SummaryMaxScanChars);
        var sb = new StringBuilder(Math.Min(SummaryMaxChars, scanEnd - start));
        bool lastWasNewline = false;
        int i = start;

        for (; i < scanEnd && sb.Length < SummaryMaxChars; i++)
        {
            char ch = plainText[i];
            if (ch == '\r')
            {
                if (i + 1 < length && plainText[i + 1] == '\n')
                {
                    i++;
                    ch = '\n';
                }
            }

            if (ch == '\n')
            {
                if (!lastWasNewline)
                {
                    sb.Append('\n');
                    lastWasNewline = true;
                }

                continue;
            }

            lastWasNewline = false;
            sb.Append(ch);
        }

        if (sb.Length < SummaryMaxChars)
        {
            bool reachedLogicalEnd = i >= length;
            if (!reachedLogicalEnd)
            {
                reachedLogicalEnd = true;
                int probeLimit = Math.Min(length, i + SummaryMaxScanChars);
                for (int j = i; j < probeLimit; j++)
                {
                    if (!char.IsWhiteSpace(plainText[j]))
                    {
                        reachedLogicalEnd = false;
                        break;
                    }
                }

                if (reachedLogicalEnd && probeLimit < length)
                {
                    reachedLogicalEnd = false;
                }
            }

            if (reachedLogicalEnd)
            {
                while (sb.Length > 0 && char.IsWhiteSpace(sb[^1]))
                {
                    sb.Length--;
                }
            }
        }
        else if (sb.Length == SummaryMaxChars &&
                 char.IsHighSurrogate(sb[^1]))
        {
            sb.Length--;
        }

        return sb.ToString();
    }

    public static void ClassifyText(
        string plainText,
        string? htmlContent,
        string? rtfContent,
        out ClipboardTextSubKind subKind,
        out string? normalizedColorHex)
    {
        normalizedColorHex = null;
        ReadOnlySpan<char> trimmedSpan = plainText.AsSpan().Trim();

        // Single-line semantic tokens take priority even when copied from a browser with HTML wrapper.
        if (!trimmedSpan.Contains('\n') && trimmedSpan.Length <= 2048)
        {
            string trimmed = trimmedSpan.ToString();
            if (TryParseColor(trimmed, out string? colorHex))
            {
                subKind = ClipboardTextSubKind.Color;
                normalizedColorHex = colorHex;
                return;
            }

            if (Uri.TryCreate(trimmed, UriKind.Absolute, out Uri? uri) &&
                (string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)))
            {
                subKind = ClipboardTextSubKind.Url;
                return;
            }

            if (trimmed.Length <= 254 && EmailRegex().IsMatch(trimmed))
            {
                subKind = ClipboardTextSubKind.Email;
                return;
            }

            if (LooksLikeWindowsPath(trimmed))
            {
                subKind = ClipboardTextSubKind.Path;
                return;
            }
        }

        if (!string.IsNullOrWhiteSpace(htmlContent))
        {
            subKind = ClipboardTextSubKind.Html;
            return;
        }

        if (!string.IsNullOrWhiteSpace(rtfContent))
        {
            subKind = ClipboardTextSubKind.Rtf;
            return;
        }

        subKind = ClipboardTextSubKind.Plain;
    }

    public static bool IsHighConfidenceSecret(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        ReadOnlySpan<char> trimmedSpan = text.AsSpan().Trim();
        if (trimmedSpan.Length < 16 || trimmedSpan.Length > 32768)
        {
            return false;
        }

        string trimmed = trimmedSpan.ToString();
        if (trimmed.Contains("-----BEGIN ", StringComparison.Ordinal) &&
            trimmed.Contains("PRIVATE KEY-----", StringComparison.Ordinal))
        {
            return true;
        }

        return HighConfidenceTokenRegex().IsMatch(trimmed) ||
               LabeledSecretRegex().IsMatch(trimmed);
    }

    /// <summary>
    /// Produces a desktop-safe masked preview (e.g. <c>sk-proj-••••••••••••9a2f</c>)
    /// while preserving recognizable prefix/suffix hints for the owner.
    /// </summary>
    public static string MaskSecretForDisplay(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        string trimmed = text.Trim();
        if (trimmed.Contains("PRIVATE KEY-----", StringComparison.Ordinal))
        {
            int firstNewline = trimmed.IndexOf('\n');
            string header = firstNewline > 0 ? trimmed[..firstNewline].Trim() : "-----BEGIN PRIVATE KEY-----";
            return $"{header} ••••••••••••";
        }

        string masked = HighConfidenceTokenRegex().Replace(trimmed, match => MaskSingleToken(match.Value));
        masked = LabeledSecretRegex().Replace(masked, match =>
        {
            Group secretGroup = match.Groups[1];
            if (!secretGroup.Success)
            {
                return match.Value;
            }

            int relStart = secretGroup.Index - match.Index;
            return string.Concat(
                match.Value.AsSpan(0, relStart),
                MaskSingleToken(secretGroup.Value),
                match.Value.AsSpan(relStart + secretGroup.Length));
        });

        if (string.Equals(masked, trimmed, StringComparison.Ordinal))
        {
            return MaskSingleToken(trimmed);
        }

        return BuildSummary(masked);
    }

    private static string MaskSingleToken(string token)
    {
        if (token.Length <= 10)
        {
            return "••••••••";
        }

        int prefixLen = Math.Min(7, token.Length / 4);
        int suffixLen = Math.Min(4, token.Length / 5);
        return $"{token[..prefixLen]}••••••••••••{token[^suffixLen..]}";
    }

    public static bool TryParseColor(string input, out string? normalizedArgbHex)
    {
        normalizedArgbHex = null;
        string trimmed = input.Trim();
        if (HexColorRegex().IsMatch(trimmed))
        {
            string hex = trimmed[1..].ToUpperInvariant();
            if (hex.Length == 3)
            {
                normalizedArgbHex = $"#FF{hex[0]}{hex[0]}{hex[1]}{hex[1]}{hex[2]}{hex[2]}";
                return true;
            }

            if (hex.Length == 6)
            {
                normalizedArgbHex = $"#FF{hex}";
                return true;
            }

            if (hex.Length == 8)
            {
                // CSS #RRGGBBAA -> #AARRGGBB
                string rrggbb = hex[..6];
                string aa = hex[6..8];
                normalizedArgbHex = $"#{aa}{rrggbb}";
                return true;
            }
        }

        Match rgbMatch = RgbColorRegex().Match(trimmed);
        if (rgbMatch.Success &&
            int.TryParse(rgbMatch.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int r) &&
            int.TryParse(rgbMatch.Groups[2].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int g) &&
            int.TryParse(rgbMatch.Groups[3].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int b) &&
            r <= 255 && g <= 255 && b <= 255)
        {
            byte alpha = ParseAlphaByte(rgbMatch.Groups[4].Success ? rgbMatch.Groups[4].Value : null);
            normalizedArgbHex = $"#{alpha:X2}{r:X2}{g:X2}{b:X2}";
            return true;
        }

        Match hslMatch = HslColorRegex().Match(trimmed);
        if (hslMatch.Success &&
            double.TryParse(hslMatch.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double h) &&
            double.TryParse(hslMatch.Groups[2].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double s) &&
            double.TryParse(hslMatch.Groups[3].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double l) &&
            s <= 100 && l <= 100)
        {
            byte alpha = ParseAlphaByte(hslMatch.Groups[4].Success ? hslMatch.Groups[4].Value : null);
            (byte hr, byte hg, byte hb) = HslToRgb(h, s / 100.0, l / 100.0);
            normalizedArgbHex = $"#{alpha:X2}{hr:X2}{hg:X2}{hb:X2}";
            return true;
        }

        return false;
    }

    private static byte ParseAlphaByte(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return 255;
        }

        string s = raw.Trim();
        if (s.EndsWith('%') &&
            double.TryParse(s[..^1], NumberStyles.Float, CultureInfo.InvariantCulture, out double pct))
        {
            return (byte)Math.Clamp(Math.Round(pct * 2.55), 0, 255);
        }

        if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double unit))
        {
            return unit <= 1.0
                ? (byte)Math.Clamp(Math.Round(unit * 255.0), 0, 255)
                : (byte)Math.Clamp(Math.Round(unit), 0, 255);
        }

        return 255;
    }

    private static (byte R, byte G, byte B) HslToRgb(double h, double s, double l)
    {
        h = ((h % 360) + 360) % 360;
        double c = (1 - Math.Abs((2 * l) - 1)) * s;
        double x = c * (1 - Math.Abs(((h / 60.0) % 2) - 1));
        double m = l - (c / 2.0);
        (double r1, double g1, double b1) = h switch
        {
            < 60 => (c, x, 0.0),
            < 120 => (x, c, 0.0),
            < 180 => (0.0, c, x),
            < 240 => (0.0, x, c),
            < 300 => (x, 0.0, c),
            _ => (c, 0.0, x)
        };

        return (
            (byte)Math.Clamp(Math.Round((r1 + m) * 255), 0, 255),
            (byte)Math.Clamp(Math.Round((g1 + m) * 255), 0, 255),
            (byte)Math.Clamp(Math.Round((b1 + m) * 255), 0, 255));
    }

    private static bool LooksLikeWindowsPath(string candidate)
    {
        if (candidate.Length < 3 || candidate.Length > 520)
        {
            return false;
        }

        bool drivePath = char.IsAsciiLetter(candidate[0]) &&
                         candidate[1] == ':' &&
                         (candidate[2] == '\\' || candidate[2] == '/');
        bool uncPath = candidate.StartsWith(@"\\", StringComparison.Ordinal) && candidate.Length > 4;
        if (!drivePath && !uncPath)
        {
            return false;
        }

        return candidate.IndexOfAny(Path.GetInvalidPathChars()) < 0;
    }
}
