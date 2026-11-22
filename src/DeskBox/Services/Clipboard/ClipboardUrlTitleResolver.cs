using System.Buffers;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using DeskBox.Models;

namespace DeskBox.Services;

internal static partial class ClipboardUrlTitleResolver
{
    private const int MaxSnippetBytes = 32768;

    private static readonly Lazy<HttpClient> s_safeTitleHttpClient =
        new(CreateSafeUrlTitleHttpClient, LazyThreadSafetyMode.ExecutionAndPublication);

    static ClipboardUrlTitleResolver()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    private static readonly string[] SensitivePathFragments =
    [
        "/verify",
        "/confirm",
        "/activate",
        "/reset",
        "/unsubscribe",
        "/magic",
        "/one-time",
        "/onetime",
        "/invite",
        "/callback",
        "/oauth",
        "/sso",
        "/auth/",
        "/login/"
    ];

    private static readonly string[] SensitiveQueryParamFragments =
    [
        "token",
        "code",
        "sig",
        "signature",
        "x-amz-",
        "x-goog-",
        "x-oss-",
        "sv=",
        "se=",
        "sp=",
        "expires",
        "nonce",
        "ticket",
        "otp",
        "session",
        "secret",
        "key",
        "auth",
        "jwt",
        "state",
        "unsubscribe"
    ];

    internal static bool IsSafePublicPageCandidate(string rawUrl, out Uri? uri)
    {
        uri = null;
        if (!Uri.TryCreate(rawUrl, UriKind.Absolute, out Uri? parsed))
        {
            return false;
        }

        if (!string.Equals(parsed.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(parsed.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!string.IsNullOrEmpty(parsed.UserInfo) ||
            !string.IsNullOrEmpty(parsed.Fragment) && LooksLikeSensitiveFragment(parsed.Fragment) ||
            IsDisallowedHostName(parsed.IdnHost))
        {
            return false;
        }

        string path = parsed.AbsolutePath;
        foreach (string frag in SensitivePathFragments)
        {
            if (path.Contains(frag, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        string query = parsed.Query;
        if (!string.IsNullOrEmpty(query))
        {
            foreach (string paramFrag in SensitiveQueryParamFragments)
            {
                if (query.Contains(paramFrag, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }
        }

        uri = parsed;
        return true;
    }

    internal static async Task<string?> TryFetchHtmlTitleAsync(Uri uri, CancellationToken outerToken = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(outerToken);
        cts.CancelAfter(TimeSpan.FromMilliseconds(2600));

        using HttpResponseMessage? resp = await SendGetWithValidatedRedirectsAsync(
            uri,
            maxRedirects: 3,
            cts.Token).ConfigureAwait(false);
        if (resp is null || !resp.IsSuccessStatusCode)
        {
            return null;
        }

        string? mediaType = resp.Content.Headers.ContentType?.MediaType;
        if (!string.IsNullOrEmpty(mediaType) &&
            !mediaType.Contains("html", StringComparison.OrdinalIgnoreCase) &&
            !mediaType.Contains("xml", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        await using Stream stream = await resp.Content.ReadAsStreamAsync(cts.Token).ConfigureAwait(false);
        byte[] buf = ArrayPool<byte>.Shared.Rent(MaxSnippetBytes);
        string snippet;
        try
        {
            int total = 0;
            while (total < MaxSnippetBytes)
            {
                int r = await stream.ReadAsync(buf.AsMemory(total, MaxSnippetBytes - total), cts.Token).ConfigureAwait(false);
                if (r <= 0)
                {
                    break;
                }

                total += r;
            }

            if (total <= 0)
            {
                return null;
            }

            snippet = DecodeHtmlSnippet(buf.AsSpan(0, total), resp.Content.Headers.ContentType?.CharSet);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buf);
        }

        return ClipboardClassifier.TryExtractHtmlDocumentTitle(snippet, out string? pageTitle) &&
               !string.IsNullOrWhiteSpace(pageTitle)
            ? pageTitle
            : null;
    }

    internal static string DecodeHtmlSnippet(ReadOnlySpan<byte> bytes, string? headerCharset)
    {
        Encoding? encoding = TryResolveEncoding(headerCharset);
        if (encoding is null)
        {
            string asciiProbe = Encoding.ASCII.GetString(bytes[..Math.Min(bytes.Length, 2048)]);
            Match metaMatch = MetaCharsetRegex().Match(asciiProbe);
            if (metaMatch.Success)
            {
                encoding = TryResolveEncoding(metaMatch.Groups[1].Value);
            }
        }

        encoding ??= Encoding.UTF8;
        if (encoding.CodePage == Encoding.UTF8.CodePage)
        {
            // Trim trailing incomplete multi-byte UTF-8 sequence if sliced at buffer boundary.
            int validLen = TrimIncompleteTrailingUtf8Bytes(bytes);
            return Encoding.UTF8.GetString(bytes[..validLen]);
        }

        return encoding.GetString(bytes);
    }

    private static int TrimIncompleteTrailingUtf8Bytes(ReadOnlySpan<byte> bytes)
    {
        int len = bytes.Length;
        if (len == 0 || (bytes[len - 1] & 0x80) == 0)
        {
            return len;
        }

        int cont = 0;
        int idx = len - 1;
        while (idx >= 0 && cont < 3 && (bytes[idx] & 0xC0) == 0x80)
        {
            cont++;
            idx--;
        }

        if (idx < 0 || (bytes[idx] & 0x80) == 0)
        {
            return len;
        }

        byte lead = bytes[idx];
        int expectedSeqLen = (lead & 0xE0) == 0xC0 ? 2 :
                             (lead & 0xF0) == 0xE0 ? 3 :
                             (lead & 0xF8) == 0xF0 ? 4 : 1;
        return (1 + cont) < expectedSeqLen ? idx : len;
    }

    private static Encoding? TryResolveEncoding(string? rawCharset)
    {
        if (string.IsNullOrWhiteSpace(rawCharset))
        {
            return null;
        }

        string cleaned = rawCharset.Trim().Trim('"', '\'');
        try
        {
            return Encoding.GetEncoding(
                cleaned,
                EncoderFallback.ReplacementFallback,
                DecoderFallback.ReplacementFallback);
        }
        catch
        {
            return null;
        }
    }

    private static bool LooksLikeSensitiveFragment(string fragment) =>
        fragment.Contains("access_token=", StringComparison.OrdinalIgnoreCase) ||
        fragment.Contains("id_token=", StringComparison.OrdinalIgnoreCase) ||
        fragment.Contains("token=", StringComparison.OrdinalIgnoreCase) ||
        fragment.Contains("code=", StringComparison.OrdinalIgnoreCase);

    private static async Task<HttpResponseMessage?> SendGetWithValidatedRedirectsAsync(
        Uri initialUri,
        int maxRedirects,
        CancellationToken cancellationToken)
    {
        Uri currentUri = initialUri;
        for (int hop = 0; ; hop++)
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, currentUri);
            req.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) DeskBox/1.0");
            req.Headers.TryAddWithoutValidation("Accept", "text/html,application/xhtml+xml;q=0.9,*/*;q=0.1");

            HttpResponseMessage resp = await s_safeTitleHttpClient.Value
                .SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);

            int status = (int)resp.StatusCode;
            if (status is 301 or 302 or 303 or 307 or 308 &&
                resp.Headers.Location is { } location)
            {
                if (hop >= maxRedirects)
                {
                    resp.Dispose();
                    return null;
                }

                Uri nextCandidate = location.IsAbsoluteUri
                    ? location
                    : new Uri(currentUri, location);
                resp.Dispose();

                if ((!string.Equals(nextCandidate.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) &&
                     !string.Equals(nextCandidate.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)) ||
                    !string.IsNullOrEmpty(nextCandidate.UserInfo) ||
                    IsDisallowedHostName(nextCandidate.IdnHost))
                {
                    return null;
                }

                currentUri = nextCandidate;
                continue;
            }

            return resp;
        }
    }

    private static HttpClient CreateSafeUrlTitleHttpClient()
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
            UseCookies = false,
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            ConnectTimeout = TimeSpan.FromMilliseconds(1800),
            ConnectCallback = CustomWidgetNetworkSecurity.ConnectToValidatedPublicIpAsync
        };

        return new HttpClient(handler, disposeHandler: true)
        {
            Timeout = TimeSpan.FromSeconds(3)
        };
    }

    private static bool IsDisallowedHostName(string? host)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            return true;
        }

        string trimmed = host.Trim().TrimEnd('.');
        if (string.Equals(trimmed, "localhost", StringComparison.OrdinalIgnoreCase) ||
            trimmed.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase) ||
            trimmed.EndsWith(".local", StringComparison.OrdinalIgnoreCase) ||
            trimmed.EndsWith(".internal", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return IPAddress.TryParse(trimmed, out IPAddress? ip) &&
               (CustomWidgetNetworkSecurity.IsBlockedIpAddress(ip) ||
                CustomWidgetNetworkSecurity.IsBenchmarkTunIpAddress(ip));
    }

    [GeneratedRegex(@"charset\s*=\s*[""']?([A-Za-z0-9._\-]+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex MetaCharsetRegex();
}
