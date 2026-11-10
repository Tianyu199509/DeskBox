using System.Buffers;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Xml;
using DeskBox.Models;
using DeskBox.Platform;

namespace DeskBox.Services;

public sealed class CustomWidgetCachedSourceSnapshot
{
    public string SourceCacheKey { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public string? ETag { get; set; }
    public string? LastModified { get; set; }
    public long UpdatedAtUtcTicks { get; set; }
    public Dictionary<string, string> StateUpdates { get; set; } = new(StringComparer.Ordinal);
    public List<CustomWidgetRecord> Items { get; set; } = [];
    public string? RawResponseBody { get; set; }
}

[JsonSourceGenerationOptions(
    GenerationMode = JsonSourceGenerationMode.Metadata,
    PropertyNameCaseInsensitive = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true)]
[JsonSerializable(typeof(CustomWidgetCachedSourceSnapshot))]
internal sealed partial class CustomWidgetNetworkCacheJsonContext : JsonSerializerContext;

public sealed record CustomWidgetNetworkFetchOutcome(
    bool Succeeded,
    bool FromDiskCache,
    bool NotModified,
    string? ErrorMessage,
    long UpdatedAtUtcTicks,
    IReadOnlyDictionary<string, string> StateUpdates,
    IReadOnlyList<CustomWidgetRecord> Items,
    string? RawResponseBody = null);

public sealed record CustomWidgetNetworkProbeResult(
    bool IsHealthy,
    bool SkippedForUnconfiguredSecret,
    string SummaryDiagnosis,
    string? JsonSkeleton);

public sealed record CustomWidgetInteractiveHttpOutcome(
    bool Succeeded,
    int StatusCode,
    string StatusText,
    string? ErrorMessage,
    string ResponseBody,
    IReadOnlyDictionary<string, string> ResponseHeaders,
    IReadOnlyDictionary<string, string> StateUpdates,
    IReadOnlyList<CustomWidgetRecord> Items);

public sealed record CustomWidgetRawEndpointProbeResult(
    bool Succeeded,
    int StatusCode,
    string ContentType,
    string? ErrorMessage,
    string? JsonSkeleton,
    string SampleText);

/// <summary>
/// Executes SSRF-hardened, rate-clamped, offline-cached <c>httpJson</c> and <c>rss</c>
/// (RSS 2.0 + Atom 1.0) declarative read-only data fetches and interactive <c>http.request</c> calls.
/// </summary>
public sealed class CustomWidgetNetworkSourceEngine
{
    private const int MaxRedirects = 3;
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(12);
    private static readonly JsonWriterOptions CompactCallbackJsonOptions = new()
    {
        Indented = false,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };
    private static readonly Regex HtmlTagRegex = new(
        @"<[^>]+>",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex MultiWhitespaceRegex = new(
        @"\s+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static CustomWidgetNetworkSourceEngine Shared { get; } = new();

    private static readonly TimeSpan DnsValidationTimeout = TimeSpan.FromSeconds(4);

    private readonly string _cacheDirectory;
    private readonly bool _customHandlerProvided;
    private readonly bool _shouldValidateTargetDnsPreConnect;
    private readonly Func<string, CancellationToken, Task<IPAddress[]>> _dnsResolver;
    private readonly IWebProxy? _systemProxy;
    private readonly HttpClient _directHttpClient;
    private readonly HttpClient _localAndPublicHttpClient;
    private readonly HttpClient? _loopbackProxyHttpClient;

    public CustomWidgetNetworkSourceEngine(
        string? cacheDirectory = null,
        HttpMessageHandler? httpMessageHandler = null,
        Func<string, CancellationToken, Task<IPAddress[]>>? dnsResolver = null,
        IWebProxy? systemProxy = null)
    {
        _cacheDirectory = string.IsNullOrWhiteSpace(cacheDirectory)
            ? Path.Combine(DeskBoxDataPathService.Current.DataDirectory, "cache", "widget-sources")
            : cacheDirectory;
        _customHandlerProvided = httpMessageHandler is not null;
        _shouldValidateTargetDnsPreConnect = httpMessageHandler is null || dnsResolver is not null;
        _dnsResolver = dnsResolver ?? DefaultResolveHostAddressesAsync;
        _systemProxy = systemProxy ?? HttpClient.DefaultProxy;

        if (httpMessageHandler is not null)
        {
            _directHttpClient = new HttpClient(httpMessageHandler, disposeHandler: false)
            {
                Timeout = RequestTimeout
            };
            _localAndPublicHttpClient = _directHttpClient;
            _loopbackProxyHttpClient = null;
        }
        else
        {
            _directHttpClient = CreateDirectSsrfHardenedHttpClient();
            _localAndPublicHttpClient = CreateLocalAndPublicHttpClient();
            _loopbackProxyHttpClient = _systemProxy is not null
                ? CreateLoopbackProxyHttpClient(_systemProxy)
                : null;
        }
    }

    private static Task<IPAddress[]> DefaultResolveHostAddressesAsync(string host, CancellationToken ct) =>
        Dns.GetHostAddressesAsync(host, ct);

    private HttpClient CreateDirectSsrfHardenedHttpClient()
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            UseProxy = false,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
            ConnectTimeout = TimeSpan.FromSeconds(8),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            ConnectCallback = ConnectToValidatedPublicIpAsync
        };
        var client = new HttpClient(handler)
        {
            Timeout = RequestTimeout
        };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("DeskBox-WidgetEngine", "1.0"));
        return client;
    }

    private static HttpClient CreateLocalAndPublicHttpClient()
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            UseProxy = false,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
            ConnectTimeout = TimeSpan.FromSeconds(8),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            ConnectCallback = CustomWidgetNetworkSecurity.ConnectToValidatedLocalOrPublicIpAsync
        };
        var client = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(30)
        };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("DeskBox-WidgetEngine", "1.0"));
        return client;
    }

    private static HttpClient CreateLoopbackProxyHttpClient(IWebProxy proxy)
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            UseProxy = true,
            Proxy = proxy,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
            ConnectTimeout = TimeSpan.FromSeconds(4),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            ConnectCallback = ConnectToLoopbackProxyAsync
        };
        var client = new HttpClient(handler)
        {
            Timeout = RequestTimeout
        };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("DeskBox-WidgetEngine", "1.0"));
        return client;
    }

    private async Task ValidateTargetHostDnsAsync(
        Uri targetUri,
        CancellationToken cancellationToken,
        bool allowLocalAndHttp = false)
    {
        string host = targetUri.IdnHost.Trim().TrimEnd('.');
        if (IPAddress.TryParse(host, out IPAddress? literalIp))
        {
            bool blocked = allowLocalAndHttp
                ? CustomWidgetNetworkSecurity.IsAlwaysForbiddenWidgetIpAddress(literalIp)
                : CustomWidgetNetworkSecurity.IsBlockedIpAddress(literalIp);
            if (blocked)
            {
                throw new InvalidOperationException(
                    $"Blocked connection to forbidden IP address '{literalIp}' resolved for '{host}'.");
            }

            return;
        }

        if (allowLocalAndHttp && string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        using var dnsCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        dnsCts.CancelAfter(DnsValidationTimeout);

        IPAddress[] addresses = await _dnsResolver(host, dnsCts.Token).ConfigureAwait(false);
        if (addresses is null || addresses.Length == 0)
        {
            throw new SocketException((int)SocketError.HostNotFound);
        }

        foreach (IPAddress ip in addresses)
        {
            bool blocked = allowLocalAndHttp
                ? CustomWidgetNetworkSecurity.IsAlwaysForbiddenWidgetIpAddress(ip)
                : CustomWidgetNetworkSecurity.IsBlockedIpAddress(ip);
            if (blocked)
            {
                throw new InvalidOperationException(
                    $"Blocked connection to non-public IP address '{ip}' resolved for '{host}'.");
            }
        }
    }

    private static bool TryValidateNetworkTargetUrl(
        string? rawUrl,
        bool allowLocalAndHttp,
        out Uri? uri,
        out bool requiresLocalClient,
        out string? error)
    {
        requiresLocalClient = false;
        if (!allowLocalAndHttp)
        {
            return CustomWidgetNetworkSecurity.TryValidatePublicHttpsUrl(rawUrl, out uri, out error);
        }

        return CustomWidgetNetworkSecurity.TryValidateWidgetNetworkUrl(
            rawUrl,
            allowLocalAndHttp: true,
            out uri,
            out requiresLocalClient,
            out error);
    }

    private bool TryGetLoopbackSystemProxy(Uri targetUri, out Uri? proxyUri)
    {
        proxyUri = null;
        if (_customHandlerProvided || _loopbackProxyHttpClient is null || _systemProxy is null)
        {
            return false;
        }

        try
        {
            if (_systemProxy.IsBypassed(targetUri))
            {
                return false;
            }

            Uri? candidate = _systemProxy.GetProxy(targetUri);
            if (candidate is null ||
                Uri.Compare(
                    candidate,
                    targetUri,
                    UriComponents.SchemeAndServer,
                    UriFormat.Unescaped,
                    StringComparison.OrdinalIgnoreCase) == 0)
            {
                return false;
            }

            string proxyHost = candidate.DnsSafeHost.Trim().TrimEnd('.');
            if (string.Equals(proxyHost, "localhost", StringComparison.OrdinalIgnoreCase) ||
                (IPAddress.TryParse(proxyHost, out IPAddress? proxyIp) && IPAddress.IsLoopback(proxyIp)))
            {
                proxyUri = candidate;
                return true;
            }

            return false;
        }
        catch
        {
            return false;
        }
    }

    private static async ValueTask<Stream> ConnectToLoopbackProxyAsync(
        SocketsHttpConnectionContext context,
        CancellationToken cancellationToken)
    {
        string host = context.DnsEndPoint.Host.Trim().TrimEnd('.');
        IPAddress[] loopbackTargets;
        if (IPAddress.TryParse(host, out IPAddress? parsedIp))
        {
            if (!IPAddress.IsLoopback(parsedIp))
            {
                throw new InvalidOperationException(
                    $"Blocked non-loopback proxy address '{parsedIp}'.");
            }

            loopbackTargets = [parsedIp];
        }
        else if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase))
        {
            loopbackTargets = [IPAddress.Loopback, IPAddress.IPv6Loopback];
        }
        else
        {
            throw new InvalidOperationException(
                $"Blocked non-loopback proxy host '{host}'.");
        }

        Exception? lastException = null;
        foreach (IPAddress ip in loopbackTargets)
        {
            var socket = new Socket(ip.AddressFamily, SocketType.Stream, ProtocolType.Tcp)
            {
                NoDelay = true
            };
            try
            {
                await socket.ConnectAsync(new IPEndPoint(ip, context.DnsEndPoint.Port), cancellationToken)
                    .ConfigureAwait(false);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (Exception ex)
            {
                socket.Dispose();
                lastException = ex;
            }
        }

        throw lastException ?? new SocketException((int)SocketError.ConnectionRefused);
    }

    private async ValueTask<Stream> ConnectToValidatedPublicIpAsync(
        SocketsHttpConnectionContext context,
        CancellationToken cancellationToken)
    {
        string host = context.DnsEndPoint.Host.Trim().TrimEnd('.');
        IPAddress[] addresses = IPAddress.TryParse(host, out IPAddress? literalIp)
            ? [literalIp]
            : await _dnsResolver(host, cancellationToken).ConfigureAwait(false);
        if (addresses.Length == 0)
        {
            throw new SocketException((int)SocketError.HostNotFound);
        }

        // Reject DNS rebinding if ANY resolved IP points to loopback / RFC1918 / link-local / metadata
        foreach (IPAddress ip in addresses)
        {
            if (CustomWidgetNetworkSecurity.IsBlockedIpAddress(ip))
            {
                throw new InvalidOperationException(
                    $"Blocked connection to non-public IP address '{ip}' resolved for '{host}'.");
            }
        }

        Exception? lastException = null;
        foreach (IPAddress ip in addresses)
        {
            var socket = new Socket(ip.AddressFamily, SocketType.Stream, ProtocolType.Tcp)
            {
                NoDelay = true
            };
            try
            {
                await socket.ConnectAsync(new IPEndPoint(ip, context.DnsEndPoint.Port), cancellationToken)
                    .ConfigureAwait(false);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (Exception ex)
            {
                socket.Dispose();
                lastException = ex;
            }
        }

        throw lastException ?? new SocketException((int)SocketError.HostUnreachable);
    }

    public static int ClampNetworkIntervalMs(int requestedIntervalMs)
    {
        int raw = requestedIntervalMs <= 0
            ? CustomWidgetNetworkSecurity.DefaultNetworkSourceIntervalMs
            : requestedIntervalMs;
        return Math.Clamp(
            raw,
            CustomWidgetNetworkSecurity.MinNetworkSourceIntervalMs,
            CustomWidgetNetworkSecurity.MaxNetworkSourceIntervalMs);
    }

    private static readonly HashSet<string> BlockedCustomHeaderNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Host", "Content-Length", "Transfer-Encoding", "Connection", "Keep-Alive",
        "Upgrade", "TE", "Trailer", "Proxy-Authorization", "Proxy-Authenticate", "Cookie"
    };

    public static string BuildCacheKey(
        CustomWidgetSourceSpec spec,
        string resolvedUrl,
        IReadOnlyDictionary<string, string>? resolvedHeaders = null)
    {
        var sb = new StringBuilder();
        sb.Append(spec.Type.ToLowerInvariant()).Append('|')
          .Append(resolvedUrl.Trim()).Append('|')
          .Append(spec.ItemsPath ?? string.Empty).Append('|')
          .Append(spec.MaxItems);

        if (spec.FieldMap is not null)
        {
            foreach (KeyValuePair<string, string> kv in spec.FieldMap.OrderBy(k => k.Key, StringComparer.Ordinal))
            {
                sb.Append("|F:").Append(kv.Key).Append('=').Append(kv.Value);
            }
        }

        if (spec.StateMap is not null)
        {
            foreach (KeyValuePair<string, string> kv in spec.StateMap.OrderBy(k => k.Key, StringComparer.Ordinal))
            {
                sb.Append("|S:").Append(kv.Key).Append('=').Append(kv.Value);
            }
        }

        if (resolvedHeaders is { Count: > 0 })
        {
            foreach (KeyValuePair<string, string> kv in resolvedHeaders.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
            {
                if (!string.IsNullOrWhiteSpace(kv.Key) && !string.IsNullOrWhiteSpace(kv.Value))
                {
                    sb.Append("|H:").Append(kv.Key.Trim().ToLowerInvariant()).Append('=').Append(kv.Value.Trim());
                }
            }
        }

        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()));
        return Convert.ToHexString(hash)[..32].ToLowerInvariant();
    }

    private static string BuildImageCacheKey(string canonicalUrl)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes("img:v1|" + canonicalUrl.Trim()));
        return Convert.ToHexString(hash)[..32].ToLowerInvariant();
    }

    public static bool IsRecognizedRasterImageHeader(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 8)
        {
            return false;
        }

        // PNG: 89 50 4E 47 0D 0A 1A 0A
        if (bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47 &&
            bytes[4] == 0x0D && bytes[5] == 0x0A && bytes[6] == 0x1A && bytes[7] == 0x0A)
        {
            return true;
        }

        // JPEG: FF D8 FF
        if (bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF)
        {
            return true;
        }

        // GIF87a / GIF89a: 47 49 46 38 (37|39) 61
        if (bytes[0] == 0x47 && bytes[1] == 0x49 && bytes[2] == 0x46 && bytes[3] == 0x38 &&
            (bytes[4] == 0x37 || bytes[4] == 0x39) && bytes[5] == 0x61)
        {
            return true;
        }

        // WEBP: "RIFF" .... "WEBP"
        if (bytes.Length >= 12 &&
            bytes[0] == 0x52 && bytes[1] == 0x49 && bytes[2] == 0x46 && bytes[3] == 0x46 &&
            bytes[8] == 0x57 && bytes[9] == 0x45 && bytes[10] == 0x42 && bytes[11] == 0x50)
        {
            return true;
        }

        // BMP: "BM"
        if (bytes[0] == 0x42 && bytes[1] == 0x4D)
        {
            return true;
        }

        // ICO: 00 00 01 00
        if (bytes[0] == 0x00 && bytes[1] == 0x00 && bytes[2] == 0x01 && bytes[3] == 0x00)
        {
            return true;
        }

        return false;
    }

    public async Task<byte[]?> FetchImageBytesAsync(
        string rawUrl,
        Func<Uri, CancellationToken, Task<bool>>? hostApprover = null,
        CancellationToken ct = default)
    {
        if (!CustomWidgetNetworkSecurity.TryValidatePublicHttpsUrl(rawUrl, out Uri? initialUri, out _) ||
            initialUri is null)
        {
            return null;
        }

        if (hostApprover is not null)
        {
            bool approved = await hostApprover(initialUri, ct).ConfigureAwait(false);
            if (!approved)
            {
                return null;
            }
        }

        string imageCacheDir = Path.Combine(_cacheDirectory, "images");
        string cachePath = Path.Combine(imageCacheDir, $"{BuildImageCacheKey(initialUri.AbsoluteUri)}.img");
        try
        {
            FileInfo cachedInfo = new(cachePath);
            if (cachedInfo.Exists &&
                cachedInfo.Length is > 8 and <= CustomWidgetNetworkSecurity.MaxRemoteImageBytes &&
                DateTime.UtcNow - cachedInfo.LastWriteTimeUtc <= TimeSpan.FromHours(24))
            {
                byte[] cachedBytes = await File.ReadAllBytesAsync(cachePath, ct).ConfigureAwait(false);
                if (IsRecognizedRasterImageHeader(cachedBytes))
                {
                    return cachedBytes;
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
        }

        Uri currentUri = initialUri;
        HttpResponseMessage? response = null;
        try
        {
            var acceptHeaders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Accept"] = "image/webp,image/png,image/jpeg,image/gif,image/*;q=0.8"
            };
            for (int redirectCount = 0; redirectCount <= MaxRedirects; redirectCount++)
            {
                if (_shouldValidateTargetDnsPreConnect)
                {
                    await ValidateTargetHostDnsAsync(currentUri, ct).ConfigureAwait(false);
                }

                response = await SendSingleHopWithProxyFallbackAsync(
                    currentUri,
                    cached: null,
                    includeConditionalHeaders: false,
                    customHeaders: acceptHeaders,
                    ct).ConfigureAwait(false);

                if (IsRedirectStatusCode(response.StatusCode) &&
                    response.Headers.Location is { } location)
                {
                    Uri nextUri = location.IsAbsoluteUri
                        ? location
                        : new Uri(currentUri, location);
                    response.Dispose();
                    response = null;

                    if (!CustomWidgetNetworkSecurity.TryValidatePublicHttpsUrl(
                            nextUri.AbsoluteUri,
                            out Uri? validatedNext,
                            out _) ||
                        validatedNext is null)
                    {
                        return null;
                    }

                    if (hostApprover is not null &&
                        !string.Equals(validatedNext.DnsSafeHost, currentUri.DnsSafeHost, StringComparison.OrdinalIgnoreCase))
                    {
                        bool redirectApproved = await hostApprover(validatedNext, ct).ConfigureAwait(false);
                        if (!redirectApproved)
                        {
                            return null;
                        }
                    }

                    currentUri = validatedNext;
                    continue;
                }

                break;
            }

            if (response is null || !response.IsSuccessStatusCode)
            {
                return TryReadFallbackCachedImage(cachePath);
            }

            if (response.Content.Headers.ContentLength is > CustomWidgetNetworkSecurity.MaxRemoteImageBytes)
            {
                return null;
            }

            await using Stream stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var ms = new MemoryStream();
            byte[] buffer = new byte[8192];
            int total = 0;
            while (true)
            {
                int read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), ct).ConfigureAwait(false);
                if (read <= 0)
                {
                    break;
                }

                total += read;
                if (total > CustomWidgetNetworkSecurity.MaxRemoteImageBytes)
                {
                    return null;
                }

                ms.Write(buffer, 0, read);
            }

            byte[] payload = ms.ToArray();
            if (!IsRecognizedRasterImageHeader(payload))
            {
                return null;
            }

            try
            {
                Directory.CreateDirectory(imageCacheDir);
                string tempPath = cachePath + $".{Guid.NewGuid():N}.tmp";
                await File.WriteAllBytesAsync(tempPath, payload, CancellationToken.None).ConfigureAwait(false);
                CustomWidgetStorageFileOps.MoveFileOverwrite(tempPath, cachePath);
            }
            catch
            {
            }

            return payload;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return TryReadFallbackCachedImage(cachePath);
        }
        finally
        {
            response?.Dispose();
        }
    }

    private static byte[]? TryReadFallbackCachedImage(string cachePath)
    {
        try
        {
            FileInfo info = new(cachePath);
            if (info.Exists && info.Length is > 8 and <= CustomWidgetNetworkSecurity.MaxRemoteImageBytes)
            {
                byte[] bytes = File.ReadAllBytes(cachePath);
                if (IsRecognizedRasterImageHeader(bytes))
                {
                    return bytes;
                }
            }
        }
        catch
        {
        }

        return null;
    }

    public CustomWidgetNetworkFetchOutcome? TryLoadCachedOutcome(
        CustomWidgetSourceSpec spec,
        string resolvedUrl,
        IReadOnlyDictionary<string, string>? resolvedHeaders = null)
    {
        CustomWidgetCachedSourceSnapshot? snapshot = TryReadCacheSnapshot(BuildCacheKey(spec, resolvedUrl, resolvedHeaders));
        if (snapshot is null)
        {
            return null;
        }

        return new CustomWidgetNetworkFetchOutcome(
            Succeeded: true,
            FromDiskCache: true,
            NotModified: false,
            ErrorMessage: null,
            UpdatedAtUtcTicks: snapshot.UpdatedAtUtcTicks,
            StateUpdates: snapshot.StateUpdates,
            Items: snapshot.Items,
            RawResponseBody: snapshot.RawResponseBody);
    }

    private static HttpRequestMessage BuildHopRequest(
        Uri targetUri,
        CustomWidgetCachedSourceSnapshot? cached,
        bool includeConditionalHeaders,
        IReadOnlyDictionary<string, string>? customHeaders = null,
        HttpMethod? method = null,
        string? body = null)
    {
        HttpMethod effectiveMethod = method ?? HttpMethod.Get;
        var request = new HttpRequestMessage(effectiveMethod, targetUri);
        request.Headers.Accept.ParseAdd(
            "application/json, application/rss+xml, application/atom+xml, application/xml, text/xml;q=0.9, */*;q=0.5");

        string? explicitContentType = null;
        if (customHeaders is { Count: > 0 })
        {
            foreach ((string rawName, string rawVal) in customHeaders)
            {
                string name = (rawName ?? string.Empty).Trim();
                string val = (rawVal ?? string.Empty).Replace("\r", string.Empty, StringComparison.Ordinal)
                                                     .Replace("\n", string.Empty, StringComparison.Ordinal)
                                                     .Trim();
                if (string.IsNullOrWhiteSpace(name) ||
                    string.IsNullOrWhiteSpace(val) ||
                    name.IndexOfAny(['\r', '\n', ':', ' ']) >= 0 ||
                    BlockedCustomHeaderNames.Contains(name))
                {
                    continue;
                }

                if (string.Equals(name, "Content-Type", StringComparison.OrdinalIgnoreCase))
                {
                    explicitContentType = val;
                    continue;
                }

                if (string.Equals(name, "User-Agent", StringComparison.OrdinalIgnoreCase))
                {
                    request.Headers.UserAgent.Clear();
                }

                request.Headers.TryAddWithoutValidation(name, val);
            }
        }

        if (body is not null &&
            effectiveMethod != HttpMethod.Get &&
            effectiveMethod != HttpMethod.Head)
        {
            byte[] utf8Body = Encoding.UTF8.GetBytes(body);
            var content = new ByteArrayContent(utf8Body);
            string? mediaType = explicitContentType;
            if (string.IsNullOrWhiteSpace(mediaType))
            {
                string trimmedBody = body.TrimStart();
                mediaType = trimmedBody.StartsWith('{') || trimmedBody.StartsWith('[')
                    ? "application/json; charset=utf-8"
                    : "text/plain; charset=utf-8";
            }

            if (!content.Headers.TryAddWithoutValidation("Content-Type", mediaType))
            {
                content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
            }

            request.Content = content;
        }

        if (includeConditionalHeaders && cached is not null)
        {
            if (!string.IsNullOrWhiteSpace(cached.ETag))
            {
                request.Headers.TryAddWithoutValidation("If-None-Match", cached.ETag);
            }

            if (!string.IsNullOrWhiteSpace(cached.LastModified))
            {
                request.Headers.TryAddWithoutValidation("If-Modified-Since", cached.LastModified);
            }
        }

        return request;
    }

    private async Task<HttpResponseMessage> SendSingleHopWithProxyFallbackAsync(
        Uri targetUri,
        CustomWidgetCachedSourceSnapshot? cached,
        bool includeConditionalHeaders,
        IReadOnlyDictionary<string, string>? customHeaders,
        CancellationToken ct,
        bool useLocalClient = false,
        HttpMethod? method = null,
        string? body = null)
    {
        if (!useLocalClient &&
            TryGetLoopbackSystemProxy(targetUri, out _) &&
            _loopbackProxyHttpClient is not null)
        {
            try
            {
                using HttpRequestMessage proxyRequest = BuildHopRequest(
                    targetUri,
                    cached,
                    includeConditionalHeaders,
                    customHeaders,
                    method,
                    body);
                return await _loopbackProxyHttpClient.SendAsync(
                    proxyRequest,
                    HttpCompletionOption.ResponseHeadersRead,
                    ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (
                !ct.IsCancellationRequested &&
                (ex is HttpRequestException || ex is SocketException || ex is TaskCanceledException))
            {
                // Local system proxy is unreachable or refused tunnel (e.g. stale proxy registry after closing proxy app);
                // fall back cleanly to direct SSRF-hardened connection.
            }
        }

        HttpClient client = useLocalClient ? _localAndPublicHttpClient : _directHttpClient;
        using HttpRequestMessage directRequest = BuildHopRequest(
            targetUri,
            cached,
            includeConditionalHeaders,
            customHeaders,
            method,
            body);
        return await client.SendAsync(
            directRequest,
            HttpCompletionOption.ResponseHeadersRead,
            ct).ConfigureAwait(false);
    }

    public async Task<CustomWidgetNetworkFetchOutcome> FetchAsync(
        CustomWidgetSourceSpec spec,
        string resolvedUrl,
        CancellationToken ct = default,
        IReadOnlyDictionary<string, string>? resolvedHeaders = null,
        Func<Uri, CancellationToken, Task<bool>>? crossOriginHostApprover = null,
        bool allowLocalAndHttp = false)
    {
        if (!TryValidateNetworkTargetUrl(
                resolvedUrl,
                allowLocalAndHttp,
                out Uri? initialUri,
                out bool requiresLocalClient,
                out string? urlError) ||
            initialUri is null)
        {
            return Failure(urlError ?? "Invalid HTTPS URL.");
        }

        Uri currentUri = initialUri;
        bool currentUsesLocalClient = requiresLocalClient;
        string cacheKey = BuildCacheKey(spec, resolvedUrl, resolvedHeaders);
        CustomWidgetCachedSourceSnapshot? cached = TryReadCacheSnapshot(cacheKey);

        try
        {
            HttpResponseMessage? response = null;
            try
            {
                for (int redirectCount = 0; redirectCount <= MaxRedirects; redirectCount++)
                {
                    if (_shouldValidateTargetDnsPreConnect)
                    {
                        await ValidateTargetHostDnsAsync(currentUri, ct, allowLocalAndHttp).ConfigureAwait(false);
                    }

                    bool isSameOrigin = Uri.Compare(
                        initialUri,
                        currentUri,
                        UriComponents.SchemeAndServer,
                        UriFormat.Unescaped,
                        StringComparison.OrdinalIgnoreCase) == 0;

                    response = await SendSingleHopWithProxyFallbackAsync(
                        currentUri,
                        cached,
                        includeConditionalHeaders: redirectCount == 0,
                        customHeaders: isSameOrigin ? resolvedHeaders : null,
                        ct,
                        useLocalClient: currentUsesLocalClient).ConfigureAwait(false);

                    if (IsRedirectStatusCode(response.StatusCode) &&
                        response.Headers.Location is { } location)
                    {
                        Uri nextUri = location.IsAbsoluteUri
                            ? location
                            : new Uri(currentUri, location);
                        response.Dispose();
                        response = null;

                        if (!TryValidateNetworkTargetUrl(
                                nextUri.AbsoluteUri,
                                allowLocalAndHttp,
                                out Uri? validatedNext,
                                out bool nextUsesLocalClient,
                                out string? redirectError) ||
                            validatedNext is null)
                        {
                            return Failure(redirectError ?? "Redirect target blocked by security policy.");
                        }

                        if (crossOriginHostApprover is not null &&
                            !string.Equals(validatedNext.DnsSafeHost, currentUri.DnsSafeHost, StringComparison.OrdinalIgnoreCase))
                        {
                            bool redirectApproved = await crossOriginHostApprover(validatedNext, ct).ConfigureAwait(false);
                            if (!redirectApproved)
                            {
                                return Failure($"Cross-origin redirect to '{validatedNext.DnsSafeHost}' was denied.");
                            }
                        }

                        currentUri = validatedNext;
                        currentUsesLocalClient = nextUsesLocalClient;
                        continue;
                    }

                    break;
                }

                if (response is null)
                {
                    return Failure("Too many HTTP redirects.");
                }

                if (response.StatusCode == HttpStatusCode.NotModified && cached is not null)
                {
                    return new CustomWidgetNetworkFetchOutcome(
                        Succeeded: true,
                        FromDiskCache: true,
                        NotModified: true,
                        ErrorMessage: null,
                        UpdatedAtUtcTicks: DateTimeOffset.UtcNow.UtcTicks,
                        StateUpdates: cached.StateUpdates,
                        Items: cached.Items,
                        RawResponseBody: cached.RawResponseBody);
                }

                if (!response.IsSuccessStatusCode)
                {
                    return Failure($"HTTP {(int)response.StatusCode} ({response.ReasonPhrase})");
                }

                string body = await ReadBoundedResponseStringAsync(
                    response.Content,
                    CustomWidgetNetworkSecurity.MaxResponseBytes,
                    ct).ConfigureAwait(false);

                (Dictionary<string, string> stateUpdates, List<CustomWidgetRecord> items) =
                    string.Equals(spec.Type, "rss", StringComparison.OrdinalIgnoreCase)
                        ? ParseRssOrAtomXml(body, Math.Clamp(spec.MaxItems, 1, 100))
                        : ExtractHttpJson(body, spec);

                string? cachedRawBody = body.Length <= 128 * 1024 ? body : body[..(128 * 1024)];
                long nowTicks = DateTimeOffset.UtcNow.UtcTicks;
                var newSnapshot = new CustomWidgetCachedSourceSnapshot
                {
                    SourceCacheKey = cacheKey,
                    Url = ScrubUrlForStorage(resolvedUrl),
                    ETag = response.Headers.ETag?.Tag,
                    LastModified = response.Content.Headers.LastModified?.ToString("R", CultureInfo.InvariantCulture),
                    UpdatedAtUtcTicks = nowTicks,
                    StateUpdates = stateUpdates,
                    Items = items,
                    RawResponseBody = cachedRawBody
                };
                WriteCacheSnapshot(cacheKey, newSnapshot);

                return new CustomWidgetNetworkFetchOutcome(
                    Succeeded: true,
                    FromDiskCache: false,
                    NotModified: false,
                    ErrorMessage: null,
                    UpdatedAtUtcTicks: nowTicks,
                    StateUpdates: stateUpdates,
                    Items: items,
                    RawResponseBody: body);
            }
            finally
            {
                response?.Dispose();
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return Failure(ex.Message);
        }
    }

    public static (Dictionary<string, string> StateUpdates, List<CustomWidgetRecord> Items) ParseRssOrAtomXml(
        string xml,
        int maxItems = 20)
    {
        var stateUpdates = new Dictionary<string, string>(StringComparer.Ordinal);
        var items = new List<CustomWidgetRecord>();
        if (string.IsNullOrWhiteSpace(xml))
        {
            return (stateUpdates, items);
        }

        int limit = Math.Clamp(maxItems, 1, 100);
        long nowTicks = DateTimeOffset.UtcNow.UtcTicks;

        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Ignore,
            XmlResolver = null,
            IgnoreComments = true,
            IgnoreWhitespace = true,
            MaxCharactersFromEntities = 1024,
            MaxCharactersInDocument = CustomWidgetNetworkSecurity.MaxResponseBytes * 2L
        };

        using var sr = new StringReader(xml.TrimStart('\uFEFF', ' ', '\t', '\r', '\n'));
        using var reader = XmlReader.Create(sr, settings);

        string feedTitle = string.Empty;
        bool hasNode = reader.Read();
        while (hasNode)
        {
            if (reader.NodeType != XmlNodeType.Element)
            {
                hasNode = reader.Read();
                continue;
            }

            string localName = reader.LocalName;
            if (string.IsNullOrEmpty(feedTitle) &&
                string.Equals(localName, "title", StringComparison.OrdinalIgnoreCase) &&
                reader.Depth <= 2)
            {
                feedTitle = CleanPlainText(reader.ReadElementContentAsString(), 120);
                hasNode = !reader.EOF;
                continue;
            }

            if (string.Equals(localName, "item", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(localName, "entry", StringComparison.OrdinalIgnoreCase))
            {
                bool hitLimit = false;
                using (XmlReader sub = reader.ReadSubtree())
                {
                    CustomWidgetRecord? record = ParseFeedEntry(sub, nowTicks, items.Count);
                    if (record is not null)
                    {
                        items.Add(record);
                        if (items.Count >= limit)
                        {
                            hitLimit = true;
                        }
                    }
                }

                if (hitLimit)
                {
                    break;
                }

                hasNode = reader.Read();
                continue;
            }

            hasNode = reader.Read();
        }

        if (!string.IsNullOrWhiteSpace(feedTitle))
        {
            stateUpdates["feedTitle"] = feedTitle;
        }

        stateUpdates["feedCount"] = items.Count.ToString(CultureInfo.InvariantCulture);
        if (items.Count > 0 && items[0].Fields.TryGetValue("title", out string? topTitle))
        {
            stateUpdates["topHeadline"] = topTitle;
        }

        return (stateUpdates, items);
    }

    private static CustomWidgetRecord? ParseFeedEntry(XmlReader entryReader, long fallbackTicks, int index)
    {
        string title = string.Empty;
        string link = string.Empty;
        string summary = string.Empty;
        string pubDateRaw = string.Empty;
        string guid = string.Empty;
        string category = string.Empty;
        string author = string.Empty;

        if (!entryReader.Read())
        {
            return null;
        }

        bool hasChild = entryReader.Read();
        while (hasChild)
        {
            if (entryReader.NodeType != XmlNodeType.Element)
            {
                hasChild = entryReader.Read();
                continue;
            }

            string name = entryReader.LocalName;
            if (string.Equals(name, "title", StringComparison.OrdinalIgnoreCase))
            {
                title = CleanPlainText(entryReader.ReadElementContentAsString(), 240);
                hasChild = !entryReader.EOF;
                continue;
            }

            if (string.Equals(name, "link", StringComparison.OrdinalIgnoreCase))
            {
                string? href = entryReader.GetAttribute("href");
                string? rel = entryReader.GetAttribute("rel");
                if (!string.IsNullOrWhiteSpace(href))
                {
                    if (string.IsNullOrWhiteSpace(link) ||
                        string.IsNullOrWhiteSpace(rel) ||
                        string.Equals(rel, "alternate", StringComparison.OrdinalIgnoreCase))
                    {
                        link = href.Trim();
                    }

                    entryReader.Skip();
                    hasChild = !entryReader.EOF;
                    continue;
                }

                string innerLink = entryReader.ReadElementContentAsString().Trim();
                if (!string.IsNullOrWhiteSpace(innerLink))
                {
                    link = innerLink;
                }

                hasChild = !entryReader.EOF;
                continue;
            }

            if (string.Equals(name, "description", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(name, "summary", StringComparison.OrdinalIgnoreCase) ||
                (string.IsNullOrWhiteSpace(summary) &&
                 (string.Equals(name, "content", StringComparison.OrdinalIgnoreCase) ||
                  string.Equals(name, "encoded", StringComparison.OrdinalIgnoreCase))))
            {
                summary = CleanPlainText(entryReader.ReadElementContentAsString(), 320);
                hasChild = !entryReader.EOF;
                continue;
            }

            if (string.Equals(name, "pubDate", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(name, "published", StringComparison.OrdinalIgnoreCase) ||
                (string.IsNullOrWhiteSpace(pubDateRaw) &&
                 string.Equals(name, "updated", StringComparison.OrdinalIgnoreCase)))
            {
                pubDateRaw = entryReader.ReadElementContentAsString().Trim();
                hasChild = !entryReader.EOF;
                continue;
            }

            if (string.Equals(name, "guid", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(name, "id", StringComparison.OrdinalIgnoreCase))
            {
                guid = entryReader.ReadElementContentAsString().Trim();
                hasChild = !entryReader.EOF;
                continue;
            }

            if (string.IsNullOrWhiteSpace(category) &&
                string.Equals(name, "category", StringComparison.OrdinalIgnoreCase))
            {
                string? term = entryReader.GetAttribute("term");
                if (!string.IsNullOrWhiteSpace(term))
                {
                    category = CleanPlainText(term, 48);
                    entryReader.Skip();
                }
                else
                {
                    category = CleanPlainText(entryReader.ReadElementContentAsString(), 48);
                }

                hasChild = !entryReader.EOF;
                continue;
            }

            if (string.IsNullOrWhiteSpace(author) &&
                (string.Equals(name, "creator", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(name, "author", StringComparison.OrdinalIgnoreCase)))
            {
                author = CleanPlainText(entryReader.ReadInnerXml(), 64);
                hasChild = !entryReader.EOF;
                continue;
            }

            hasChild = entryReader.Read();
        }

        if (string.IsNullOrWhiteSpace(title) && string.IsNullOrWhiteSpace(summary))
        {
            return null;
        }

        // Only permit safe https/http links inside feed records
        if (!string.IsNullOrWhiteSpace(link) &&
            (!Uri.TryCreate(link, UriKind.Absolute, out Uri? parsedLink) ||
             (!string.Equals(parsedLink.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) &&
              !string.Equals(parsedLink.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase))))
        {
            link = string.Empty;
        }

        long createdTicks = fallbackTicks - (index * TimeSpan.TicksPerSecond);
        string publishedDisplay = string.Empty;
        if (!string.IsNullOrWhiteSpace(pubDateRaw) &&
            DateTimeOffset.TryParse(pubDateRaw, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out DateTimeOffset dto))
        {
            createdTicks = dto.UtcTicks;
            DateTimeOffset local = dto.ToLocalTime();
            publishedDisplay = local.Date == DateTimeOffset.Now.Date
                ? local.ToString("HH:mm", CultureInfo.InvariantCulture)
                : local.ToString("MM-dd HH:mm", CultureInfo.InvariantCulture);
        }

        string stableSeed = !string.IsNullOrWhiteSpace(guid)
            ? guid
            : !string.IsNullOrWhiteSpace(link)
                ? link
                : title;
        string stableId = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(stableSeed)))[..12].ToLowerInvariant();

        var fields = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["title"] = string.IsNullOrWhiteSpace(title) ? summary : title,
            ["text"] = string.IsNullOrWhiteSpace(title) ? summary : title,
            ["summary"] = summary,
            ["subtitle"] = !string.IsNullOrWhiteSpace(summary) ? summary : publishedDisplay,
            ["url"] = link,
            ["link"] = link,
            ["publishedAt"] = publishedDisplay,
            ["category"] = category,
            ["author"] = author
        };

        return new CustomWidgetRecord
        {
            Id = stableId,
            CreatedAtUtcTicks = createdTicks,
            UpdatedAtUtcTicks = fallbackTicks,
            Fields = fields
        };
    }

    public static (Dictionary<string, string> StateUpdates, List<CustomWidgetRecord> Items) ExtractHttpJson(
        string json,
        CustomWidgetSourceSpec spec)
    {
        var stateUpdates = new Dictionary<string, string>(StringComparer.Ordinal);
        var items = new List<CustomWidgetRecord>();

        using JsonDocument doc = JsonDocument.Parse(
            json,
            new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip
            });

        JsonElement root = doc.RootElement;

        if (spec.StateMap is not null)
        {
            foreach ((string stateKey, string jsonPath) in spec.StateMap)
            {
                if (string.IsNullOrWhiteSpace(stateKey))
                {
                    continue;
                }

                if (TryResolveJsonPath(root, jsonPath, out JsonElement target))
                {
                    stateUpdates[stateKey] = ConvertJsonElementToString(target);
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(spec.TargetCollection))
        {
            JsonElement arrayCandidate = root;
            bool hasArray = !string.IsNullOrWhiteSpace(spec.ItemsPath)
                ? TryResolveJsonPath(root, spec.ItemsPath, out arrayCandidate) && arrayCandidate.ValueKind == JsonValueKind.Array
                : root.ValueKind == JsonValueKind.Array;

            if (hasArray && arrayCandidate.ValueKind == JsonValueKind.Array)
            {
                int limit = Math.Clamp(spec.MaxItems, 1, 100);
                long nowTicks = DateTimeOffset.UtcNow.UtcTicks;
                int index = 0;

                foreach (JsonElement elem in arrayCandidate.EnumerateArray())
                {
                    if (index >= limit)
                    {
                        break;
                    }

                    var fields = new Dictionary<string, string>(StringComparer.Ordinal);
                    if (spec.FieldMap is { Count: > 0 })
                    {
                        foreach ((string fieldName, string relPath) in spec.FieldMap)
                        {
                            if (!string.IsNullOrWhiteSpace(fieldName) &&
                                TryResolveJsonPath(elem, relPath, out JsonElement valEl))
                            {
                                fields[fieldName] = ConvertJsonElementToString(valEl);
                            }
                        }
                    }
                    else if (elem.ValueKind == JsonValueKind.Object)
                    {
                        foreach (JsonProperty prop in elem.EnumerateObject())
                        {
                            if (prop.Value.ValueKind is not JsonValueKind.Object and not JsonValueKind.Array)
                            {
                                fields[prop.Name] = ConvertJsonElementToString(prop.Value);
                            }
                        }
                    }
                    else
                    {
                        string scalar = ConvertJsonElementToString(elem);
                        fields["title"] = scalar;
                        fields["text"] = scalar;
                    }

                    string seed = fields.TryGetValue("id", out string? rawId) && !string.IsNullOrWhiteSpace(rawId)
                        ? rawId
                        : fields.TryGetValue("url", out string? rawUrl) && !string.IsNullOrWhiteSpace(rawUrl)
                            ? rawUrl
                            : fields.TryGetValue("title", out string? rawTitle) && !string.IsNullOrWhiteSpace(rawTitle)
                                ? $"{rawTitle}#{index}"
                                : $"item#{index}";
                    string stableId = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(seed)))[..12].ToLowerInvariant();

                    items.Add(new CustomWidgetRecord
                    {
                        Id = stableId,
                        CreatedAtUtcTicks = nowTicks - (index * TimeSpan.TicksPerSecond),
                        UpdatedAtUtcTicks = nowTicks,
                        Fields = fields
                    });
                    index++;
                }
            }
        }

        return (stateUpdates, items);
    }

    public static bool TryResolveJsonPath(JsonElement root, string? path, out JsonElement result)
    {
        result = root;
        if (string.IsNullOrWhiteSpace(path))
        {
            return true;
        }

        string trimmed = path.Trim();
        if (trimmed == "$" || trimmed == ".")
        {
            return true;
        }

        if (trimmed.StartsWith("$.", StringComparison.Ordinal))
        {
            trimmed = trimmed[2..];
        }
        else if (trimmed.StartsWith("$", StringComparison.Ordinal))
        {
            trimmed = trimmed[1..];
        }

        JsonElement current = root;
        string[] segments = trimmed.Split('.', StringSplitOptions.RemoveEmptyEntries);
        foreach (string rawSegment in segments)
        {
            ReadOnlySpan<char> span = rawSegment.AsSpan();
            while (!span.IsEmpty)
            {
                int bracketStart = span.IndexOf('[');
                if (bracketStart < 0)
                {
                    string propName = span.ToString();
                    if (!TryGetPropertyCaseInsensitive(current, propName, out current))
                    {
                        result = default;
                        return false;
                    }

                    break;
                }

                if (bracketStart > 0)
                {
                    string propName = span[..bracketStart].ToString();
                    if (!TryGetPropertyCaseInsensitive(current, propName, out current))
                    {
                        result = default;
                        return false;
                    }
                }

                int bracketEnd = span[bracketStart..].IndexOf(']');
                if (bracketEnd <= 1)
                {
                    result = default;
                    return false;
                }

                ReadOnlySpan<char> indexSpan = span.Slice(bracketStart + 1, bracketEnd - 1);
                if (!int.TryParse(indexSpan, NumberStyles.Integer, CultureInfo.InvariantCulture, out int arrayIdx) ||
                    arrayIdx < 0 ||
                    current.ValueKind != JsonValueKind.Array ||
                    arrayIdx >= current.GetArrayLength())
                {
                    result = default;
                    return false;
                }

                current = current[arrayIdx];
                span = span[(bracketStart + bracketEnd + 1)..];
            }
        }

        result = current;
        return true;
    }

    private static bool TryGetPropertyCaseInsensitive(JsonElement element, string name, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            if (element.TryGetProperty(name, out value))
            {
                return true;
            }

            foreach (JsonProperty prop in element.EnumerateObject())
            {
                if (string.Equals(prop.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    value = prop.Value;
                    return true;
                }
            }
        }

        value = default;
        return false;
    }

    private static string ConvertJsonElementToString(JsonElement el) =>
        el.ValueKind switch
        {
            JsonValueKind.String => el.GetString() ?? string.Empty,
            JsonValueKind.Number => el.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            JsonValueKind.Null or JsonValueKind.Undefined => string.Empty,
            _ => el.GetRawText()
        };

    private static string CleanPlainText(string? raw, int maxChars)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return string.Empty;
        }

        string decoded = WebUtility.HtmlDecode(raw);
        string noHtml = HtmlTagRegex.Replace(decoded, " ");
        string collapsed = MultiWhitespaceRegex.Replace(noHtml, " ").Trim();
        if (collapsed.Length > maxChars)
        {
            return string.Concat(collapsed.AsSpan(0, maxChars - 1), "…");
        }

        return collapsed;
    }

    private CustomWidgetCachedSourceSnapshot? TryReadCacheSnapshot(string cacheKey)
    {
        try
        {
            string path = Path.Combine(_cacheDirectory, $"{cacheKey}.json");
            if (!File.Exists(path))
            {
                return null;
            }

            string json = File.ReadAllText(path, Encoding.UTF8);
            return JsonSerializer.Deserialize(
                json,
                CustomWidgetNetworkCacheJsonContext.Default.CustomWidgetCachedSourceSnapshot);
        }
        catch
        {
            return null;
        }
    }

    private static string ScrubUrlForStorage(string? rawUrl)
    {
        if (string.IsNullOrWhiteSpace(rawUrl))
        {
            return string.Empty;
        }

        if (!Uri.TryCreate(rawUrl, UriKind.Absolute, out Uri? uri))
        {
            int qIdx = rawUrl.IndexOf('?');
            return qIdx >= 0 ? rawUrl[..qIdx] : rawUrl;
        }

        return uri.GetComponents(UriComponents.SchemeAndServer | UriComponents.Path, UriFormat.UriEscaped);
    }

    private void WriteCacheSnapshot(string cacheKey, CustomWidgetCachedSourceSnapshot snapshot)
    {
        try
        {
            Directory.CreateDirectory(_cacheDirectory);
            string path = Path.Combine(_cacheDirectory, $"{cacheKey}.json");
            string json = JsonSerializer.Serialize(
                snapshot,
                CustomWidgetNetworkCacheJsonContext.Default.CustomWidgetCachedSourceSnapshot);
            CustomWidgetStorageFileOps.ReplaceTextFileAtomically(path, json);
        }
        catch
        {
            // Ignore non-critical disk cache write errors
        }
    }

    private static bool IsRedirectStatusCode(HttpStatusCode statusCode) =>
        statusCode is HttpStatusCode.MovedPermanently
            or HttpStatusCode.Found
            or HttpStatusCode.SeeOther
            or HttpStatusCode.TemporaryRedirect
            or HttpStatusCode.PermanentRedirect;

    private static async Task<string> ReadBoundedResponseStringAsync(
        HttpContent content,
        int maxBytes,
        CancellationToken ct)
    {
        if (content.Headers.ContentLength is { } contentLength && contentLength > maxBytes)
        {
            throw new InvalidDataException($"Response exceeds {maxBytes / 1024} KB limit.");
        }

        await using Stream stream = await content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var ms = new MemoryStream();
        byte[] buffer = new byte[8192];
        int totalBytes = 0;
        while (true)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), ct).ConfigureAwait(false);
            if (read <= 0)
            {
                break;
            }

            totalBytes += read;
            if (totalBytes > maxBytes)
            {
                throw new InvalidDataException($"Response exceeds {maxBytes / 1024} KB limit.");
            }

            ms.Write(buffer, 0, read);
        }

        Encoding encoding = Encoding.UTF8;
        string? charset = content.Headers.ContentType?.CharSet?.Trim('"', '\'');
        if (!string.IsNullOrWhiteSpace(charset))
        {
            try
            {
                encoding = Encoding.GetEncoding(charset);
            }
            catch
            {
                encoding = Encoding.UTF8;
            }
        }

        return encoding.GetString(ms.GetBuffer(), 0, (int)ms.Length);
    }

    /// <summary>
    /// Fast, bounded pre-flight live probe used during Studio AI generation to catch broken URLs,
    /// anti-bot HTML pages, empty <c>itemsPath</c>, or mismatched <c>fieldMap</c> keys before showing an empty widget.
    /// </summary>
    public async Task<CustomWidgetNetworkProbeResult> ProbeSourceAsync(
        CustomWidgetSourceSpec spec,
        string resolvedUrl,
        IReadOnlyDictionary<string, string>? resolvedHeaders = null,
        bool hasUnconfiguredSecretRef = false,
        CancellationToken ct = default,
        bool allowLocalAndHttp = false)
    {
        if (hasUnconfiguredSecretRef)
        {
            return new CustomWidgetNetworkProbeResult(
                IsHealthy: true,
                SkippedForUnconfiguredSecret: true,
                SummaryDiagnosis: "Skipped live probe until user configures secret key.",
                JsonSkeleton: null);
        }

        if (!TryValidateNetworkTargetUrl(
                resolvedUrl,
                allowLocalAndHttp,
                out Uri? initialUri,
                out bool requiresLocalClient,
                out string? urlError) ||
            initialUri is null)
        {
            return new CustomWidgetNetworkProbeResult(
                IsHealthy: false,
                SkippedForUnconfiguredSecret: false,
                SummaryDiagnosis: $"Invalid or blocked URL '{resolvedUrl}': {urlError ?? "HTTPS required"}",
                JsonSkeleton: null);
        }

        using var probeCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        probeCts.CancelAfter(TimeSpan.FromSeconds(5));

        Uri currentUri = initialUri;
        bool currentUsesLocalClient = requiresLocalClient;
        HttpResponseMessage? response = null;
        try
        {
            for (int redirectCount = 0; redirectCount <= MaxRedirects; redirectCount++)
            {
                if (_shouldValidateTargetDnsPreConnect)
                {
                    await ValidateTargetHostDnsAsync(currentUri, probeCts.Token, allowLocalAndHttp).ConfigureAwait(false);
                }

                bool isSameOrigin = Uri.Compare(
                    initialUri,
                    currentUri,
                    UriComponents.SchemeAndServer,
                    UriFormat.Unescaped,
                    StringComparison.OrdinalIgnoreCase) == 0;

                response = await SendSingleHopWithProxyFallbackAsync(
                    currentUri,
                    cached: null,
                    includeConditionalHeaders: false,
                    customHeaders: isSameOrigin ? resolvedHeaders : null,
                    probeCts.Token,
                    useLocalClient: currentUsesLocalClient).ConfigureAwait(false);

                if (IsRedirectStatusCode(response.StatusCode) &&
                    response.Headers.Location is { } location)
                {
                    Uri nextUri = location.IsAbsoluteUri ? location : new Uri(currentUri, location);
                    response.Dispose();
                    response = null;
                    if (!TryValidateNetworkTargetUrl(
                            nextUri.AbsoluteUri,
                            allowLocalAndHttp,
                            out Uri? validatedNext,
                            out bool nextUsesLocalClient,
                            out string? redirectErr) ||
                        validatedNext is null)
                    {
                        return new CustomWidgetNetworkProbeResult(
                            IsHealthy: false,
                            SkippedForUnconfiguredSecret: false,
                            SummaryDiagnosis: $"Redirect blocked: {redirectErr}",
                            JsonSkeleton: null);
                    }

                    currentUri = validatedNext;
                    currentUsesLocalClient = nextUsesLocalClient;
                    continue;
                }

                break;
            }

            if (response is null)
            {
                return new CustomWidgetNetworkProbeResult(
                    IsHealthy: false,
                    SkippedForUnconfiguredSecret: false,
                    SummaryDiagnosis: "Too many HTTP redirects.",
                    JsonSkeleton: null);
            }

            if (!response.IsSuccessStatusCode)
            {
                int status = (int)response.StatusCode;
                string hint = status is 401 or 403
                    ? "Endpoint rejected unauthenticated/bot request. Switch to a public RSS/JSON API or declare a secret setting in headers."
                    : status == 404
                        ? "Endpoint returned 404 Not Found. Replace with a verified live endpoint."
                        : "Endpoint returned HTTP error.";
                return new CustomWidgetNetworkProbeResult(
                    IsHealthy: false,
                    SkippedForUnconfiguredSecret: false,
                    SummaryDiagnosis: $"HTTP {status} ({response.ReasonPhrase}) at '{resolvedUrl}'. {hint}",
                    JsonSkeleton: null);
            }

            string body = await ReadBoundedResponseStringAsync(
                response.Content,
                CustomWidgetNetworkSecurity.MaxResponseBytes,
                probeCts.Token).ConfigureAwait(false);

            string trimmedStart = body.AsSpan().TrimStart().ToString();
            if (trimmedStart.StartsWith("<!DOCTYPE html", StringComparison.OrdinalIgnoreCase) ||
                trimmedStart.StartsWith("<html", StringComparison.OrdinalIgnoreCase))
            {
                return new CustomWidgetNetworkProbeResult(
                    IsHealthy: false,
                    SkippedForUnconfiguredSecret: false,
                    SummaryDiagnosis: $"Endpoint '{resolvedUrl}' returned an HTML webpage instead of structured {spec.Type} data (anti-bot challenge or webpage URL).",
                    JsonSkeleton: null);
            }

            if (string.Equals(spec.Type, "rss", StringComparison.OrdinalIgnoreCase))
            {
                (_, List<CustomWidgetRecord> rssItems) = ParseRssOrAtomXml(body, Math.Clamp(spec.MaxItems, 1, 20));
                if (rssItems.Count == 0)
                {
                    return new CustomWidgetNetworkProbeResult(
                        IsHealthy: false,
                        SkippedForUnconfiguredSecret: false,
                        SummaryDiagnosis: $"RSS/Atom feed '{resolvedUrl}' yielded 0 items.",
                        JsonSkeleton: null);
                }

                return new CustomWidgetNetworkProbeResult(
                    IsHealthy: true,
                    SkippedForUnconfiguredSecret: false,
                    SummaryDiagnosis: $"OK ({rssItems.Count} RSS/Atom items).",
                    JsonSkeleton: null);
            }

            // httpJson structure & fieldMap verification
            using JsonDocument doc = JsonDocument.Parse(body, new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip
            });
            string skeleton = BuildCompactJsonSkeleton(doc.RootElement);
            (Dictionary<string, string> stateUpdates, List<CustomWidgetRecord> jsonItems) = ExtractHttpJson(body, spec);

            if (!string.IsNullOrWhiteSpace(spec.TargetCollection))
            {
                if (jsonItems.Count == 0)
                {
                    return new CustomWidgetNetworkProbeResult(
                        IsHealthy: false,
                        SkippedForUnconfiguredSecret: false,
                        SummaryDiagnosis: $"httpJson source '{resolvedUrl}' extracted 0 items for targetCollection='{spec.TargetCollection}' with itemsPath='{spec.ItemsPath ?? "(root)"}'. Fix itemsPath based on actual JSON skeleton.",
                        JsonSkeleton: skeleton);
                }

                if (spec.FieldMap is { Count: > 0 } &&
                    jsonItems[0].Fields.Values.All(string.IsNullOrWhiteSpace))
                {
                    return new CustomWidgetNetworkProbeResult(
                        IsHealthy: false,
                        SkippedForUnconfiguredSecret: false,
                        SummaryDiagnosis: $"httpJson source '{resolvedUrl}' extracted {jsonItems.Count} items, but ALL fieldMap paths ({string.Join(", ", spec.FieldMap.Select(static kv => $"{kv.Key}:{kv.Value}"))}) resolved to empty strings! Fix fieldMap keys based on actual JSON skeleton.",
                        JsonSkeleton: skeleton);
                }
            }
            else if (spec.StateMap is { Count: > 0 } &&
                     stateUpdates.Values.All(string.IsNullOrWhiteSpace))
            {
                return new CustomWidgetNetworkProbeResult(
                    IsHealthy: false,
                    SkippedForUnconfiguredSecret: false,
                    SummaryDiagnosis: $"httpJson source '{resolvedUrl}' stateMap paths all resolved to empty values. Fix stateMap paths based on actual JSON skeleton.",
                    JsonSkeleton: skeleton);
            }

            return new CustomWidgetNetworkProbeResult(
                IsHealthy: true,
                SkippedForUnconfiguredSecret: false,
                SummaryDiagnosis: $"OK ({jsonItems.Count} items, {stateUpdates.Count} state fields).",
                JsonSkeleton: skeleton);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new CustomWidgetNetworkProbeResult(
                IsHealthy: false,
                SkippedForUnconfiguredSecret: false,
                SummaryDiagnosis: $"Live probe failed for '{resolvedUrl}': {ex.Message}",
                JsonSkeleton: null);
        }
        finally
        {
            response?.Dispose();
        }
    }

    public static string BuildCompactJsonSkeleton(JsonElement element, int maxChars = 640)
    {
        var sb = new StringBuilder(256);
        WriteElementSkeleton(element, sb, depth: 0);
        if (sb.Length > maxChars)
        {
            return string.Concat(sb.ToString().AsSpan(0, maxChars - 3), "...");
        }
        return sb.ToString();
    }

    private static void WriteElementSkeleton(JsonElement el, StringBuilder sb, int depth)
    {
        if (sb.Length > 700)
        {
            return;
        }

        switch (el.ValueKind)
        {
            case JsonValueKind.Object:
            {
                sb.Append("{ ");
                int count = 0;
                foreach (JsonProperty prop in el.EnumerateObject())
                {
                    if (count >= (depth == 0 ? 10 : 8))
                    {
                        sb.Append(", ...");
                        break;
                    }
                    if (count > 0)
                    {
                        sb.Append(", ");
                    }
                    sb.Append('"').Append(prop.Name).Append("\": ");
                    if (depth < 2)
                    {
                        WriteElementSkeleton(prop.Value, sb, depth + 1);
                    }
                    else
                    {
                        sb.Append(DescribeValueKind(prop.Value));
                    }
                    count++;
                }
                sb.Append(" }");
                break;
            }
            case JsonValueKind.Array:
            {
                int len = el.GetArrayLength();
                sb.Append("Array(").Append(len.ToString(CultureInfo.InvariantCulture)).Append(')');
                if (len > 0 && depth <= 2)
                {
                    sb.Append(" [ ");
                    WriteElementSkeleton(el[0], sb, depth + 1);
                    sb.Append(" ]");
                }
                break;
            }
            default:
                sb.Append(DescribeValueKind(el));
                break;
        }
    }

    private static string DescribeValueKind(JsonElement el) => el.ValueKind switch
    {
        JsonValueKind.String => "string",
        JsonValueKind.Number => "number",
        JsonValueKind.True or JsonValueKind.False => "bool",
        JsonValueKind.Array => $"Array({el.GetArrayLength()})",
        JsonValueKind.Object => "{...}",
        JsonValueKind.Null => "null",
        _ => "any"
    };

    public async Task<CustomWidgetInteractiveHttpOutcome> SendInteractiveHttpRequestAsync(
        string resolvedUrl,
        string? method = "GET",
        IReadOnlyDictionary<string, string>? resolvedHeaders = null,
        string? body = null,
        int timeoutMs = 12_000,
        bool allowLocalAndHttp = false,
        IReadOnlyDictionary<string, string>? stateMap = null,
        string? targetCollection = null,
        string? itemsPath = null,
        IReadOnlyDictionary<string, string>? fieldMap = null,
        CancellationToken ct = default,
        Func<Uri, CancellationToken, Task<bool>>? crossOriginHostApprover = null)
    {
        if (!TryValidateNetworkTargetUrl(
                resolvedUrl,
                allowLocalAndHttp,
                out Uri? initialUri,
                out bool requiresLocalClient,
                out string? urlError) ||
            initialUri is null)
        {
            return new CustomWidgetInteractiveHttpOutcome(
                false,
                0,
                string.Empty,
                urlError ?? "Invalid URL.",
                string.Empty,
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                new Dictionary<string, string>(StringComparer.Ordinal),
                []);
        }

        string verb = string.IsNullOrWhiteSpace(method) ? "GET" : method.Trim().ToUpperInvariant();
        HttpMethod httpMethod = verb switch
        {
            "POST" => HttpMethod.Post,
            "PUT" => HttpMethod.Put,
            "PATCH" => HttpMethod.Patch,
            "DELETE" => HttpMethod.Delete,
            "HEAD" => HttpMethod.Head,
            _ => HttpMethod.Get
        };

        int clampedTimeout = Math.Clamp(timeoutMs <= 0 ? 12_000 : timeoutMs, 1_000, 30_000);
        using var reqCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        reqCts.CancelAfter(clampedTimeout);

        Uri currentUri = initialUri;
        bool currentUsesLocalClient = requiresLocalClient;
        HttpMethod currentMethod = httpMethod;
        string? currentBody = body;
        HttpResponseMessage? response = null;

        try
        {
            for (int redirectCount = 0; redirectCount <= MaxRedirects; redirectCount++)
            {
                if (_shouldValidateTargetDnsPreConnect)
                {
                    await ValidateTargetHostDnsAsync(currentUri, reqCts.Token, allowLocalAndHttp).ConfigureAwait(false);
                }

                bool isSameOrigin = Uri.Compare(
                    initialUri,
                    currentUri,
                    UriComponents.SchemeAndServer,
                    UriFormat.Unescaped,
                    StringComparison.OrdinalIgnoreCase) == 0;

                response = await SendSingleHopWithProxyFallbackAsync(
                    currentUri,
                    cached: null,
                    includeConditionalHeaders: false,
                    customHeaders: isSameOrigin ? resolvedHeaders : null,
                    reqCts.Token,
                    useLocalClient: currentUsesLocalClient,
                    method: currentMethod,
                    body: currentBody).ConfigureAwait(false);

                if (IsRedirectStatusCode(response.StatusCode) &&
                    response.Headers.Location is { } location)
                {
                    Uri nextUri = location.IsAbsoluteUri ? location : new Uri(currentUri, location);
                    HttpStatusCode code = response.StatusCode;
                    response.Dispose();
                    response = null;

                    if (!TryValidateNetworkTargetUrl(
                            nextUri.AbsoluteUri,
                            allowLocalAndHttp,
                            out Uri? validatedNext,
                            out bool nextUsesLocalClient,
                            out string? redirectErr) ||
                        validatedNext is null)
                    {
                        return new CustomWidgetInteractiveHttpOutcome(
                            false,
                            (int)code,
                            code.ToString(),
                            redirectErr ?? "Redirect blocked by security policy.",
                            string.Empty,
                            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                            new Dictionary<string, string>(StringComparer.Ordinal),
                            []);
                    }

                    if (crossOriginHostApprover is not null &&
                        !string.Equals(validatedNext.DnsSafeHost, currentUri.DnsSafeHost, StringComparison.OrdinalIgnoreCase))
                    {
                        bool redirectApproved = await crossOriginHostApprover(validatedNext, reqCts.Token).ConfigureAwait(false);
                        if (!redirectApproved)
                        {
                            return new CustomWidgetInteractiveHttpOutcome(
                                false,
                                (int)code,
                                code.ToString(),
                                $"Cross-origin redirect to '{validatedNext.DnsSafeHost}' was denied.",
                                string.Empty,
                                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                                new Dictionary<string, string>(StringComparer.Ordinal),
                                []);
                        }
                    }

                    bool isNextSameOrigin = Uri.Compare(
                        initialUri,
                        validatedNext,
                        UriComponents.SchemeAndServer,
                        UriFormat.Unescaped,
                        StringComparison.OrdinalIgnoreCase) == 0;

                    if (code is HttpStatusCode.MovedPermanently or HttpStatusCode.Found or HttpStatusCode.SeeOther ||
                        (!isNextSameOrigin && currentBody is not null))
                    {
                        currentMethod = HttpMethod.Get;
                        currentBody = null;
                    }

                    currentUri = validatedNext;
                    currentUsesLocalClient = nextUsesLocalClient;
                    continue;
                }

                break;
            }

            if (response is null)
            {
                return new CustomWidgetInteractiveHttpOutcome(
                    false,
                    0,
                    string.Empty,
                    "Too many HTTP redirects.",
                    string.Empty,
                    new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                    new Dictionary<string, string>(StringComparer.Ordinal),
                    []);
            }

            string responseBody = response.Content is not null
                ? await ReadBoundedResponseStringAsync(
                    response.Content,
                    CustomWidgetNetworkSecurity.MaxResponseBytes,
                    reqCts.Token).ConfigureAwait(false)
                : string.Empty;

            var respHeaders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var h in response.Headers)
            {
                respHeaders[h.Key] = string.Join(", ", h.Value);
            }
            if (response.Content is not null)
            {
                foreach (var h in response.Content.Headers)
                {
                    respHeaders[h.Key] = string.Join(", ", h.Value);
                }
            }

            var stateUpdates = new Dictionary<string, string>(StringComparer.Ordinal);
            List<CustomWidgetRecord> items = [];
            if (!string.IsNullOrWhiteSpace(responseBody) &&
                (stateMap is { Count: > 0 } || !string.IsNullOrWhiteSpace(targetCollection)))
            {
                try
                {
                    var syntheticSpec = new CustomWidgetSourceSpec(
                        Type: "httpJson",
                        Id: "http.request",
                        StateMap: stateMap,
                        TargetCollection: targetCollection,
                        ItemsPath: itemsPath,
                        FieldMap: fieldMap,
                        MaxItems: 100);
                    (stateUpdates, items) = ExtractHttpJson(responseBody, syntheticSpec);
                }
                catch
                {
                    // Non-JSON responses still succeed and pass raw text to onHttpResponse.
                }
            }

            bool ok = response.IsSuccessStatusCode;
            return new CustomWidgetInteractiveHttpOutcome(
                Succeeded: ok,
                StatusCode: (int)response.StatusCode,
                StatusText: response.ReasonPhrase ?? string.Empty,
                ErrorMessage: ok ? null : $"HTTP {(int)response.StatusCode} ({response.ReasonPhrase})",
                ResponseBody: responseBody,
                ResponseHeaders: respHeaders,
                StateUpdates: stateUpdates,
                Items: items);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return new CustomWidgetInteractiveHttpOutcome(
                false,
                0,
                "Timeout",
                $"HTTP request timed out after {clampedTimeout} ms.",
                string.Empty,
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                new Dictionary<string, string>(StringComparer.Ordinal),
                []);
        }
        catch (Exception ex)
        {
            return new CustomWidgetInteractiveHttpOutcome(
                false,
                0,
                string.Empty,
                ex.Message,
                string.Empty,
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                new Dictionary<string, string>(StringComparer.Ordinal),
                []);
        }
        finally
        {
            response?.Dispose();
        }
    }

    public static string BuildHttpCallbackPayloadJson(string requestId, CustomWidgetInteractiveHttpOutcome outcome)
    {
        var buffer = new ArrayBufferWriter<byte>(1024);
        using (var writer = new Utf8JsonWriter(buffer, CompactCallbackJsonOptions))
        {
            writer.WriteStartObject();
            writer.WriteString("id", requestId ?? string.Empty);
            writer.WriteBoolean("ok", outcome.Succeeded);
            writer.WriteNumber("status", outcome.StatusCode);
            writer.WriteString("statusText", outcome.StatusText ?? string.Empty);
            writer.WriteString("error", outcome.ErrorMessage ?? string.Empty);
            writer.WriteString("body", outcome.ResponseBody ?? string.Empty);

            writer.WritePropertyName("headers");
            writer.WriteStartObject();
            foreach ((string k, string v) in outcome.ResponseHeaders)
            {
                writer.WriteString(k, v ?? string.Empty);
            }
            writer.WriteEndObject();

            writer.WritePropertyName("data");
            bool wroteJsonData = false;
            if (!string.IsNullOrWhiteSpace(outcome.ResponseBody))
            {
                string trimmed = outcome.ResponseBody.Trim();
                if (trimmed.StartsWith('{') || trimmed.StartsWith('['))
                {
                    try
                    {
                        using JsonDocument doc = JsonDocument.Parse(
                            trimmed,
                            new JsonDocumentOptions
                            {
                                AllowTrailingCommas = true,
                                CommentHandling = JsonCommentHandling.Skip
                            });
                        doc.RootElement.WriteTo(writer);
                        wroteJsonData = true;
                    }
                    catch
                    {
                    }
                }
            }
            if (!wroteJsonData)
            {
                writer.WriteNullValue();
            }

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    /// <summary>
    /// Live HTTP sampler used by the Studio AI <c>probeHttp</c> local action (max 3 KB sample)
    /// so the model inspects real JSON/RSS structures instead of guessing field names.
    /// </summary>
    public async Task<CustomWidgetRawEndpointProbeResult> ProbeRawEndpointAsync(
        string resolvedUrl,
        string method = "GET",
        IReadOnlyDictionary<string, string>? resolvedHeaders = null,
        string? body = null,
        bool allowLocalAndHttp = true,
        int maxSampleChars = 2_800,
        CancellationToken ct = default)
    {
        CustomWidgetInteractiveHttpOutcome res = await SendInteractiveHttpRequestAsync(
            resolvedUrl,
            method,
            resolvedHeaders,
            body,
            timeoutMs: 6_000,
            allowLocalAndHttp: allowLocalAndHttp,
            ct: ct).ConfigureAwait(false);

        string contentType = res.ResponseHeaders.TryGetValue("Content-Type", out string? ctVal)
            ? ctVal
            : string.Empty;

        if (!res.Succeeded)
        {
            string errSample = string.IsNullOrWhiteSpace(res.ResponseBody)
                ? string.Empty
                : res.ResponseBody.Length <= 600 ? res.ResponseBody : res.ResponseBody[..600] + "...";
            return new CustomWidgetRawEndpointProbeResult(
                false,
                res.StatusCode,
                contentType,
                res.ErrorMessage ?? "Request failed.",
                null,
                errSample);
        }

        string raw = res.ResponseBody ?? string.Empty;
        string trimmed = raw.TrimStart();
        if (trimmed.StartsWith("<!DOCTYPE html", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("<html", StringComparison.OrdinalIgnoreCase))
        {
            return new CustomWidgetRawEndpointProbeResult(
                false,
                res.StatusCode,
                contentType,
                "Endpoint returned an HTML webpage rather than JSON/RSS/XML data.",
                null,
                trimmed.Length <= 400 ? trimmed : trimmed[..400] + "...");
        }

        string? skeleton = null;
        if (trimmed.StartsWith('{') || trimmed.StartsWith('['))
        {
            try
            {
                using JsonDocument doc = JsonDocument.Parse(
                    trimmed,
                    new JsonDocumentOptions
                    {
                        AllowTrailingCommas = true,
                        CommentHandling = JsonCommentHandling.Skip
                    });
                skeleton = BuildCompactJsonSkeleton(doc.RootElement, maxChars: 900);
            }
            catch
            {
            }
        }

        int cap = Math.Clamp(maxSampleChars, 400, 3_072);
        string sample = raw.Length <= cap ? raw : string.Concat(raw.AsSpan(0, cap), "\n...(truncated)");
        return new CustomWidgetRawEndpointProbeResult(
            true,
            res.StatusCode,
            contentType,
            null,
            skeleton,
            sample);
    }

    private static CustomWidgetNetworkFetchOutcome Failure(string message) =>
        new(
            Succeeded: false,
            FromDiskCache: false,
            NotModified: false,
            ErrorMessage: message,
            UpdatedAtUtcTicks: 0,
            StateUpdates: new Dictionary<string, string>(StringComparer.Ordinal),
            Items: []);
}
