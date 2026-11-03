using System.Globalization;
using System.Net;
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
        ClassifyText(plainText, htmlContent, rtfContent, out subKind, out normalizedColorHex, out _);
    }

    public static void ClassifyText(
        string plainText,
        string? htmlContent,
        string? rtfContent,
        out ClipboardTextSubKind subKind,
        out string? normalizedColorHex,
        out string? codeLanguage)
    {
        normalizedColorHex = null;
        codeLanguage = null;
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

        if (TryDetectCodeSnippet(trimmedSpan, htmlContent, out string? detectedLanguage))
        {
            subKind = ClipboardTextSubKind.Code;
            codeLanguage = detectedLanguage;
            return;
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

    public static bool IsPreviewableImageExtension(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        string ext = Path.GetExtension(path);
        return ext.Equals(".png", StringComparison.OrdinalIgnoreCase) ||
               ext.Equals(".jpg", StringComparison.OrdinalIgnoreCase) ||
               ext.Equals(".jpeg", StringComparison.OrdinalIgnoreCase) ||
               ext.Equals(".bmp", StringComparison.OrdinalIgnoreCase) ||
               ext.Equals(".gif", StringComparison.OrdinalIgnoreCase) ||
               ext.Equals(".webp", StringComparison.OrdinalIgnoreCase);
    }

    public static string NormalizeFriendlyAppDisplayName(string? rawName)
    {
        if (string.IsNullOrWhiteSpace(rawName))
        {
            return string.Empty;
        }

        string name = rawName.Trim();
        if (name.Length > 4 && name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            name = name[..^4].Trim();
        }

        if (string.Equals(name, "DeskBox.Settings", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(name, "DeskBox.ThumbnailProxy", StringComparison.OrdinalIgnoreCase))
        {
            return "DeskBox";
        }

        return name;
    }

    public static bool IsSinglePreviewableImageFile(ClipboardHistoryItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        return item.Kind == ClipboardItemKind.Files &&
               item.FilePaths.Count == 1 &&
               IsPreviewableImageExtension(item.FilePaths[0]);
    }

    public static bool IsGeneratedOrChatCacheImagePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !IsPreviewableImageExtension(path))
        {
            return false;
        }

        if (path.Contains(@"\MsgAttach\", StringComparison.OrdinalIgnoreCase) ||
            path.Contains(@"\nt_qq\nt_data\Pic\", StringComparison.OrdinalIgnoreCase) ||
            path.Contains(@"\ClipboardExports\", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        string stem = Path.GetFileNameWithoutExtension(path);
        if (string.IsNullOrWhiteSpace(stem))
        {
            return false;
        }

        if ((stem.StartsWith("mmexport", StringComparison.OrdinalIgnoreCase) ||
             stem.StartsWith("wx_camera_", StringComparison.OrdinalIgnoreCase) ||
             stem.StartsWith("Clipboard_", StringComparison.OrdinalIgnoreCase)) &&
            stem.Length >= 16)
        {
            return true;
        }

        if (path.Contains(@"\AppData\Local\Temp\", StringComparison.OrdinalIgnoreCase) &&
            stem.StartsWith("tmp", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (stem.Length >= 24)
        {
            int hexOrDash = 0;
            int hexDigits = 0;
            for (int i = 0; i < stem.Length; i++)
            {
                char ch = stem[i];
                if (Uri.IsHexDigit(ch))
                {
                    hexDigits++;
                    hexOrDash++;
                }
                else if (ch is '-' or '_')
                {
                    hexOrDash++;
                }
                else
                {
                    hexOrDash = -1;
                    break;
                }
            }

            if (hexOrDash == stem.Length && hexDigits >= 24)
            {
                return true;
            }
        }

        return false;
    }

    public static string FormatReadableUrl(string? rawUrl, int maxLength = 72)
    {
        if (string.IsNullOrWhiteSpace(rawUrl))
        {
            return string.Empty;
        }

        string trimmed = rawUrl.Trim();
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out Uri? uri) || string.IsNullOrWhiteSpace(uri.Host))
        {
            return trimmed.Length > maxLength ? string.Concat(trimmed.AsSpan(0, maxLength), "…") : trimmed;
        }

        string host = uri.Host.StartsWith("www.", StringComparison.OrdinalIgnoreCase)
            ? uri.Host[4..]
            : uri.Host;
        string path = Uri.UnescapeDataString(uri.AbsolutePath ?? string.Empty).TrimEnd('/');
        string query = uri.Query;
        if (!string.IsNullOrEmpty(query) &&
            (query.Contains("utm_", StringComparison.OrdinalIgnoreCase) ||
             query.Contains("spm=", StringComparison.OrdinalIgnoreCase) ||
             query.Contains("fbclid=", StringComparison.OrdinalIgnoreCase) ||
             query.Length > 32))
        {
            query = string.Empty;
        }

        string combined = string.IsNullOrEmpty(path) || path == "/"
            ? host
            : $"{host}{path}{query}";
        return combined.Length > maxLength
            ? string.Concat(combined.AsSpan(0, maxLength), "…")
            : combined;
    }

    public static bool TryExtractHtmlAnchorTitle(string? htmlPayload, string rawUrl, out string? title)
    {
        title = null;
        if (string.IsNullOrWhiteSpace(htmlPayload))
        {
            return false;
        }

        ReadOnlySpan<char> span = htmlPayload.AsSpan();
        int aStart = span.IndexOf("<a ", StringComparison.OrdinalIgnoreCase);
        if (aStart < 0)
        {
            return false;
        }

        span = span[aStart..];
        int openEnd = span.IndexOf('>');
        if (openEnd < 0)
        {
            return false;
        }

        span = span[(openEnd + 1)..];
        int aClose = span.IndexOf("</a>", StringComparison.OrdinalIgnoreCase);
        if (aClose <= 0 || aClose > 512)
        {
            return false;
        }

        string inner = StripHtmlTagsAndNormalizeWhitespace(span[..aClose]);
        if (inner.Length is < 2 or > 140 ||
            string.Equals(inner, rawUrl.Trim(), StringComparison.OrdinalIgnoreCase) ||
            inner.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            inner.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        title = inner;
        return true;
    }

    public static bool TryExtractHtmlDocumentTitle(string? htmlDocument, out string? title)
    {
        title = null;
        if (string.IsNullOrWhiteSpace(htmlDocument))
        {
            return false;
        }

        ReadOnlySpan<char> span = htmlDocument.AsSpan();
        int tStart = span.IndexOf("<title", StringComparison.OrdinalIgnoreCase);
        if (tStart < 0)
        {
            return false;
        }

        span = span[tStart..];
        int gt = span.IndexOf('>');
        if (gt < 0)
        {
            return false;
        }

        span = span[(gt + 1)..];
        int tEnd = span.IndexOf("</title>", StringComparison.OrdinalIgnoreCase);
        if (tEnd <= 0 || tEnd > 512)
        {
            return false;
        }

        string clean = StripHtmlTagsAndNormalizeWhitespace(span[..tEnd]);
        if (clean.Length < 2)
        {
            return false;
        }

        title = clean.Length > 120 ? string.Concat(clean.AsSpan(0, 120), "…") : clean;
        return true;
    }

    private static string StripHtmlTagsAndNormalizeWhitespace(ReadOnlySpan<char> html)
    {
        var sb = new StringBuilder(html.Length);
        bool inTag = false;
        for (int i = 0; i < html.Length; i++)
        {
            char c = html[i];
            if (c == '<')
            {
                inTag = true;
                continue;
            }

            if (c == '>')
            {
                inTag = false;
                continue;
            }

            if (!inTag)
            {
                sb.Append(c);
            }
        }

        string decoded = WebUtility.HtmlDecode(sb.ToString());
        var normalized = new StringBuilder(decoded.Length);
        bool prevSpace = true;
        foreach (char c in decoded)
        {
            if (char.IsWhiteSpace(c))
            {
                if (!prevSpace)
                {
                    normalized.Append(' ');
                    prevSpace = true;
                }
            }
            else
            {
                normalized.Append(c);
                prevSpace = false;
            }
        }

        return normalized.ToString().Trim();
    }

    private static bool TryDetectCodeSnippet(
        ReadOnlySpan<char> trimmed,
        string? htmlContent,
        out string? codeLanguage)
    {
        codeLanguage = null;
        if (trimmed.Length < 6)
        {
            return false;
        }

        ReadOnlySpan<char> probe = trimmed.Length > 4096 ? trimmed[..4096] : trimmed;

        // 1. JSON object or array
        if ((probe[0] == '{' && trimmed[^1] == '}' && probe.Contains(':') && probe.Contains('"')) ||
            (probe[0] == '[' && trimmed[^1] == ']' && probe.Contains('{') && probe.Contains(':') && probe.Contains('"')))
        {
            codeLanguage = "JSON";
            return true;
        }

        // 2. XML / XAML / SVG / HTML source text
        if (probe.StartsWith("<?xml", StringComparison.OrdinalIgnoreCase) ||
            probe.StartsWith("<!DOCTYPE", StringComparison.OrdinalIgnoreCase) ||
            probe.StartsWith("<svg", StringComparison.OrdinalIgnoreCase) ||
            probe.StartsWith("<UserControl", StringComparison.Ordinal) ||
            probe.StartsWith("<Window", StringComparison.Ordinal) ||
            probe.StartsWith("<Page", StringComparison.Ordinal) ||
            probe.StartsWith("<ResourceDictionary", StringComparison.Ordinal))
        {
            codeLanguage = probe.StartsWith("<!DOCTYPE html", StringComparison.OrdinalIgnoreCase) ? "HTML" : "XML";
            return true;
        }

        // 3. SQL queries
        if ((probe.StartsWith("SELECT ", StringComparison.OrdinalIgnoreCase) && probe.IndexOf(" FROM ", StringComparison.OrdinalIgnoreCase) > 0) ||
            (probe.StartsWith("INSERT INTO ", StringComparison.OrdinalIgnoreCase) && probe.IndexOf(" VALUES", StringComparison.OrdinalIgnoreCase) > 0) ||
            (probe.StartsWith("UPDATE ", StringComparison.OrdinalIgnoreCase) && probe.IndexOf(" SET ", StringComparison.OrdinalIgnoreCase) > 0) ||
            probe.StartsWith("CREATE TABLE ", StringComparison.OrdinalIgnoreCase) ||
            probe.StartsWith("ALTER TABLE ", StringComparison.OrdinalIgnoreCase))
        {
            codeLanguage = "SQL";
            return true;
        }

        // 4. Shell / CLI commands
        if (StartsWithAnyCommand(probe))
        {
            codeLanguage = "Shell";
            return true;
        }

        // 5. IDE HTML marker (VS Code / JetBrains / <pre><code>) or multi-line structural code
        bool fromCodeHtml = !string.IsNullOrWhiteSpace(htmlContent) &&
            (htmlContent.Contains("vscode-editor-data", StringComparison.OrdinalIgnoreCase) ||
             htmlContent.Contains("<pre", StringComparison.OrdinalIgnoreCase) ||
             htmlContent.Contains("<code", StringComparison.OrdinalIgnoreCase));

        bool hasNewline = probe.Contains('\n');
        if (!hasNewline && !fromCodeHtml)
        {
            return false;
        }

        string? inferred = InferProgrammingLanguage(probe);
        if (inferred is not null)
        {
            codeLanguage = inferred;
            return true;
        }

        if (fromCodeHtml && hasNewline)
        {
            return true;
        }

        // Fallback structural code check for multi-line snippets (>= 2 code punctuation lines + indentation)
        int lineCount = 1;
        int indentedLines = 0;
        int codeEndedLines = 0;
        int start = 0;
        for (int i = 0; i <= probe.Length; i++)
        {
            if (i == probe.Length || probe[i] == '\n')
            {
                ReadOnlySpan<char> rawLine = probe.Slice(start, i - start);
                if (rawLine.Length > 0 && rawLine[^1] == '\r')
                {
                    rawLine = rawLine[..^1];
                }

                if (rawLine.Length >= 2 && (rawLine.StartsWith("  ", StringComparison.Ordinal) || rawLine[0] == '\t'))
                {
                    indentedLines++;
                }

                ReadOnlySpan<char> tLine = rawLine.Trim();
                if (tLine.Length > 0)
                {
                    char last = tLine[^1];
                    if (last is '{' or '}' or ';' || tLine.StartsWith("//", StringComparison.Ordinal))
                    {
                        codeEndedLines++;
                    }
                }

                if (i < probe.Length)
                {
                    lineCount++;
                }

                start = i + 1;
            }
        }

        return lineCount >= 3 && codeEndedLines >= 2 && indentedLines >= 1;
    }

    private static bool StartsWithAnyCommand(ReadOnlySpan<char> probe)
    {
        string[] prefixes =
        [
            "$ ", "git ", "dotnet ", "npm ", "pnpm ", "yarn ", "cargo ",
            "docker ", "kubectl ", "curl ", "pwsh ", "powershell ", "pip ", "uv "
        ];
        foreach (string prefix in prefixes)
        {
            if (probe.StartsWith(prefix, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static string? InferProgrammingLanguage(ReadOnlySpan<char> probe)
    {
        bool hasStructure = probe.Contains('{') || probe.Contains(';') ||
                            probe.IndexOf("=>", StringComparison.Ordinal) >= 0 ||
                            probe.IndexOf("->", StringComparison.Ordinal) >= 0 ||
                            probe.Contains('(');
        if (!hasStructure)
        {
            return null;
        }

        if (probe.StartsWith("using System", StringComparison.Ordinal) ||
            probe.StartsWith("namespace ", StringComparison.Ordinal) ||
            probe.IndexOf("public sealed ", StringComparison.Ordinal) >= 0 ||
            probe.IndexOf("public async Task", StringComparison.Ordinal) >= 0 ||
            probe.IndexOf("private void ", StringComparison.Ordinal) >= 0)
        {
            return "C#";
        }

        if (probe.StartsWith("fn ", StringComparison.Ordinal) ||
            probe.StartsWith("pub fn ", StringComparison.Ordinal) ||
            probe.StartsWith("use std::", StringComparison.Ordinal) ||
            probe.IndexOf("impl ", StringComparison.Ordinal) >= 0 && probe.IndexOf("::", StringComparison.Ordinal) >= 0)
        {
            return "Rust";
        }

        if (probe.StartsWith("def ", StringComparison.Ordinal) ||
            probe.StartsWith("async def ", StringComparison.Ordinal) ||
            (probe.StartsWith("from ", StringComparison.Ordinal) && probe.IndexOf(" import ", StringComparison.Ordinal) > 0))
        {
            return "Python";
        }

        if (probe.StartsWith("import ", StringComparison.Ordinal) ||
            probe.StartsWith("export ", StringComparison.Ordinal) ||
            probe.StartsWith("const ", StringComparison.Ordinal) ||
            probe.StartsWith("let ", StringComparison.Ordinal) ||
            probe.StartsWith("function ", StringComparison.Ordinal) ||
            probe.StartsWith("interface ", StringComparison.Ordinal))
        {
            return "TS/JS";
        }

        if (probe.StartsWith("#include <", StringComparison.Ordinal) ||
            probe.StartsWith("#include \"", StringComparison.Ordinal))
        {
            return "C/C++";
        }

        if (probe.StartsWith("package ", StringComparison.Ordinal) &&
            probe.IndexOf("func ", StringComparison.Ordinal) > 0)
        {
            return "Go";
        }

        return null;
    }
}
