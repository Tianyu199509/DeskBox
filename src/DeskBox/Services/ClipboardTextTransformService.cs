using System.Buffers;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace DeskBox.Services;

/// <summary>
/// Context-aware, zero-dependency text clean-up &amp; developer formatting utilities
/// for Clipboard entries (PDF broken line repair, tracking URL stripping, NativeAOT JSON format/minify).
/// </summary>
public static class ClipboardTextTransformService
{
    private static readonly HashSet<string> s_universalTrackingParams = new(StringComparer.OrdinalIgnoreCase)
    {
        "fbclid", "gclid", "dclid", "msclkid", "twclid", "igshid", "yclid",
        "mc_cid", "mc_eid", "_hsenc", "_hsmi", "mkt_tok", "vero_id",
        "oly_anon_id", "oly_enc_id", "rb_clickid", "s_kwcid", "wickedid",
        "spm", "spm_id_from", "from_spmid", "vd_source", "unique_k", "buvid", "is_story_h5",
        "share_source", "share_medium", "share_plat", "share_session_id", "share_tag", "share_from",
        "xsec_source", "xsec_token", "xhsshare", "appuid", "apptime",
        "tt_from", "utm_campaign", "utm_source", "utm_medium", "utm_term", "utm_content", "utm_id"
    };

    private static readonly HashSet<string> s_wechatRemovableParams = new(StringComparer.OrdinalIgnoreCase)
    {
        "chksm", "mpshare", "scene", "srcid", "sharer_shareinfo", "sharer_shareinfo_first",
        "ascene", "devicetype", "version", "nettype", "abtest_cookie", "wx_header", "poc_token"
    };

    private static readonly HashSet<string> s_mediaShareRemovableParams = new(StringComparer.OrdinalIgnoreCase)
    {
        "si", "pp", "feature"
    };

    public static bool CanMergePdfLineBreaks(string? text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length < 8)
        {
            return false;
        }

        if (!text.Contains('\n') && !text.Contains('\r'))
        {
            return false;
        }

        string trimmed = text.Trim();
        if (CanTransformJson(trimmed))
        {
            return false;
        }

        string merged = MergePdfLineBreaks(text);
        return !string.Equals(merged, NormalizeLineEndings(text).Trim(), StringComparison.Ordinal);
    }

    /// <summary>
    /// Repairs hard line breaks copied from PDFs / narrow columns while preserving
    /// double-newline paragraph breaks, list items, hyphenated English words, and CJK spacing.
    /// </summary>
    public static string MergePdfLineBreaks(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        string normalized = NormalizeLineEndings(text).Trim();
        string[] rawLines = normalized.Split('\n');
        if (rawLines.Length <= 1)
        {
            return normalized;
        }

        var result = new StringBuilder(normalized.Length);
        bool previousWasBlank = false;
        bool hasContentInCurrentParagraph = false;

        for (int i = 0; i < rawLines.Length; i++)
        {
            string line = rawLines[i].Trim();
            if (line.Length == 0)
            {
                if (hasContentInCurrentParagraph)
                {
                    previousWasBlank = true;
                }

                continue;
            }

            if (previousWasBlank)
            {
                result.Append("\n\n");
                previousWasBlank = false;
                hasContentInCurrentParagraph = false;
            }

            if (!hasContentInCurrentParagraph)
            {
                result.Append(line);
                hasContentInCurrentParagraph = true;
                continue;
            }

            // Keep explicit bullet / numbered list items on their own line.
            if (LooksLikeListBulletLine(line))
            {
                result.Append('\n').Append(line);
                continue;
            }

            char prevChar = result[^1];
            char nextChar = line[0];

            // Repair English PDF end-of-line hyphenation (e.g. "inter-\nnational" -> "international").
            if (prevChar == '-' &&
                result.Length >= 2 &&
                IsAsciiLetter(result[^2]) &&
                char.IsAsciiLetterLower(nextChar))
            {
                result.Length -= 1;
                result.Append(line);
                continue;
            }

            if (ClipboardOcrService.IsCjkOrFullwidth(prevChar) &&
                ClipboardOcrService.IsCjkOrFullwidth(nextChar))
            {
                result.Append(line);
            }
            else
            {
                result.Append(' ').Append(line);
            }
        }

        return result.ToString();
    }

    public static bool TryCleanTrackingUrl(string? rawText, out string cleanedUrl)
    {
        cleanedUrl = string.Empty;
        if (string.IsNullOrWhiteSpace(rawText))
        {
            return false;
        }

        string trimmed = rawText.Trim();
        if (trimmed.Contains('\n') || trimmed.Contains('\r') || trimmed.Contains(' '))
        {
            return false;
        }

        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out Uri? uri) ||
            (!string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) &&
             !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        string rawQuery = uri.Query;
        if (string.IsNullOrEmpty(rawQuery) || rawQuery.Length <= 1)
        {
            return false;
        }

        string host = uri.Host;
        bool isWeChat = host.EndsWith("weixin.qq.com", StringComparison.OrdinalIgnoreCase);
        bool isMediaShareHost =
            host.EndsWith("youtube.com", StringComparison.OrdinalIgnoreCase) ||
            host.Equals("youtu.be", StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith("spotify.com", StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith("music.163.com", StringComparison.OrdinalIgnoreCase);

        string[] pairs = rawQuery.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries);
        if (pairs.Length == 0)
        {
            return false;
        }

        var keptPairs = new List<string>(pairs.Length);
        bool removedAny = false;

        foreach (string pair in pairs)
        {
            int eqIdx = pair.IndexOf('=');
            string rawKey = eqIdx >= 0 ? pair[..eqIdx] : pair;
            string decodedKey = Uri.UnescapeDataString(rawKey).Trim();

            if (IsRemovableTrackingParam(decodedKey, isWeChat, isMediaShareHost))
            {
                removedAny = true;
                continue;
            }

            keptPairs.Add(pair);
        }

        if (!removedAny)
        {
            return false;
        }

        var builder = new UriBuilder(uri)
        {
            Query = keptPairs.Count > 0 ? string.Join('&', keptPairs) : string.Empty
        };

        // Clean default ports so UriBuilder does not turn https://example.com into https://example.com:443
        if ((builder.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) && builder.Port == 443) ||
            (builder.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) && builder.Port == 80))
        {
            builder.Port = -1;
        }

        cleanedUrl = builder.Uri.AbsoluteUri;

        // Preserve original trailing-slash absence on root path if original had none before '?'
        int qIndexInOriginal = trimmed.IndexOf('?');
        if (qIndexInOriginal > 0 &&
            uri.AbsolutePath == "/" &&
            trimmed[qIndexInOriginal - 1] != '/' &&
            cleanedUrl.EndsWith('/') &&
            keptPairs.Count == 0 &&
            string.IsNullOrEmpty(uri.Fragment))
        {
            cleanedUrl = cleanedUrl[..^1];
        }

        return !string.Equals(cleanedUrl, trimmed, StringComparison.Ordinal);
    }

    public static bool CanTransformJson(string? rawText)
    {
        if (string.IsNullOrWhiteSpace(rawText))
        {
            return false;
        }

        ReadOnlySpan<char> span = rawText.AsSpan().Trim();
        if (span.Length < 2)
        {
            return false;
        }

        bool isObjectOrArray =
            (span[0] == '{' && span[^1] == '}') ||
            (span[0] == '[' && span[^1] == ']');
        if (!isObjectOrArray)
        {
            return false;
        }

        try
        {
            using JsonDocument _ = JsonDocument.Parse(
                rawText,
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = true,
                    CommentHandling = JsonCommentHandling.Skip
                });
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static bool TryFormatJson(string? rawText, bool indented, out string formatted)
    {
        formatted = string.Empty;
        if (!CanTransformJson(rawText))
        {
            return false;
        }

        try
        {
            using JsonDocument doc = JsonDocument.Parse(
                rawText!,
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = true,
                    CommentHandling = JsonCommentHandling.Skip
                });

            var buffer = new ArrayBufferWriter<byte>(Math.Max(256, rawText!.Length));
            using (var writer = new Utf8JsonWriter(
                       buffer,
                       new JsonWriterOptions
                       {
                           Indented = indented,
                           Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
                       }))
            {
                doc.RootElement.WriteTo(writer);
            }

            formatted = Encoding.UTF8.GetString(buffer.WrittenSpan);
            return formatted.Length > 0;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsRemovableTrackingParam(string key, bool isWeChat, bool isMediaShareHost)
    {
        if (string.IsNullOrEmpty(key))
        {
            return false;
        }

        if (key.StartsWith("utm_", StringComparison.OrdinalIgnoreCase) ||
            s_universalTrackingParams.Contains(key))
        {
            return true;
        }

        if (isWeChat && s_wechatRemovableParams.Contains(key))
        {
            return true;
        }

        if (isMediaShareHost && s_mediaShareRemovableParams.Contains(key))
        {
            return true;
        }

        return false;
    }

    private static string NormalizeLineEndings(string value) =>
        value.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');

    private static bool IsAsciiLetter(char ch) =>
        (ch >= 'A' && ch <= 'Z') || (ch >= 'a' && ch <= 'z');

    private static bool LooksLikeListBulletLine(string line)
    {
        if (line.Length < 2)
        {
            return false;
        }

        if ((line[0] is '-' or '*' or '•' or '·') && char.IsWhiteSpace(line[1]))
        {
            return true;
        }

        int idx = 0;
        while (idx < line.Length && idx < 4 && char.IsAsciiDigit(line[idx]))
        {
            idx++;
        }

        return idx > 0 &&
               idx + 1 < line.Length &&
               line[idx] is '.' or ')' or '、' &&
               (line[idx] == '、' || char.IsWhiteSpace(line[idx + 1]));
    }
}
