using System.Net;
using System.Net.Http;
using System.Net.Sockets;

namespace DeskBox.Models;

/// <summary>
/// Shared SSRF, scheme, and trusted-host validators for declarative Custom Widget
/// network sources (<c>httpJson</c>, <c>rss</c>), remote catalog OTA, and <c>deskbox://</c> deep links.
/// </summary>
public static class CustomWidgetNetworkSecurity
{
    public const int MinNetworkSourceIntervalMs = 15_000;       // 15 seconds floor (auto-throttled when hidden)
    public const int DefaultNetworkSourceIntervalMs = 900_000;  // 15 minutes default
    public const int MaxNetworkSourceIntervalMs = 86_400_000;   // 24 hours ceiling
    public const int ManualRefreshCooldownSeconds = 2;
    public const int MaxResponseBytes = 1_048_576;              // 1 MB cap
    public const int MaxRemoteImageBytes = 2_097_152;           // 2 MB image cap

    /// <summary>
    /// Validates a Custom Widget network URL, classifying whether it requires the
    /// <c>LocalNetwork</c> permission (<c>http://</c>, <c>localhost</c>, or private LAN/loopback IPs)
    /// while unconditionally blocking link-local/cloud-metadata (<c>169.254.0.0/16</c>), unspecified, and multicast addresses.
    /// </summary>
    public static bool TryValidateWidgetNetworkUrl(
        string? rawUrl,
        bool allowLocalAndHttp,
        out Uri? validUri,
        out bool requiresLocalNetworkPermission,
        out string? errorMessage)
    {
        validUri = null;
        requiresLocalNetworkPermission = false;
        errorMessage = null;

        if (string.IsNullOrWhiteSpace(rawUrl))
        {
            errorMessage = "Network 'url' is required.";
            return false;
        }

        if (!Uri.TryCreate(rawUrl.Trim(), UriKind.Absolute, out Uri? uri))
        {
            errorMessage = $"Invalid URL '{rawUrl}'.";
            return false;
        }

        bool isHttps = string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase);
        bool isHttp = string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase);
        if (!isHttps && !isHttp)
        {
            errorMessage = "Only 'https://' or 'http://' URLs are permitted.";
            return false;
        }

        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            errorMessage = "Embedded credentials in URLs are not permitted.";
            return false;
        }

        string host = uri.DnsSafeHost.Trim().TrimEnd('.');
        if (string.IsNullOrWhiteSpace(host))
        {
            errorMessage = "URL host cannot be empty.";
            return false;
        }

        bool isLocalHostName =
            string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith(".local", StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith(".internal", StringComparison.OrdinalIgnoreCase);

        bool isPrivateOrLoopbackIp = false;
        if (IPAddress.TryParse(host, out IPAddress? literalIp))
        {
            if (IsAlwaysForbiddenWidgetIpAddress(literalIp))
            {
                errorMessage = $"Link-local, metadata, unspecified, or multicast IP '{host}' is blocked for security.";
                return false;
            }

            if (IsBlockedIpAddress(literalIp))
            {
                isPrivateOrLoopbackIp = true;
            }
        }

        requiresLocalNetworkPermission = isHttp || isLocalHostName || isPrivateOrLoopbackIp;
        if (requiresLocalNetworkPermission && !allowLocalAndHttp)
        {
            errorMessage = isHttp
                ? $"Plaintext HTTP URL '{rawUrl}' requires LocalNetwork permission."
                : $"Local or internal address '{host}' requires LocalNetwork permission.";
            return false;
        }

        validUri = uri;
        return true;
    }

    public static bool IsAlwaysForbiddenWidgetIpAddress(IPAddress address)
    {
        if (address.Equals(IPAddress.Any) ||
            address.Equals(IPAddress.IPv6Any) ||
            address.Equals(IPAddress.Broadcast) ||
            address.Equals(IPAddress.IPv6None))
        {
            return true;
        }

        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            byte[] bytes = address.GetAddressBytes();
            return bytes[0] == 0 ||
                   (bytes[0] == 169 && bytes[1] == 254) ||
                   bytes[0] >= 224;
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (address.IsIPv6LinkLocal || address.IsIPv6Multicast)
            {
                return true;
            }

            byte[] bytes = address.GetAddressBytes();
            // NAT64 local-use prefix 64:ff9b:1::/48
            if (bytes[0] == 0x00 && bytes[1] == 0x64 && bytes[2] == 0xFF && bytes[3] == 0x9B &&
                bytes[4] == 0x00 && bytes[5] == 0x01)
            {
                return true;
            }

            // NAT64 well-known prefix 64:ff9b::/96 -> embedded IPv4 in bytes[12..16]
            if (bytes[0] == 0x00 && bytes[1] == 0x64 && bytes[2] == 0xFF && bytes[3] == 0x9B &&
                bytes[4] == 0 && bytes[5] == 0 && bytes[6] == 0 && bytes[7] == 0 &&
                bytes[8] == 0 && bytes[9] == 0 && bytes[10] == 0 && bytes[11] == 0)
            {
                return IsAlwaysForbiddenWidgetIpAddress(new IPAddress(bytes.AsSpan(12, 4)));
            }

            // 6to4 prefix 2002::/16 -> embedded IPv4 in bytes[2..6]
            if (bytes[0] == 0x20 && bytes[1] == 0x02)
            {
                return IsAlwaysForbiddenWidgetIpAddress(new IPAddress(bytes.AsSpan(2, 4)));
            }

            // Teredo tunnel endpoints remain blocked outright
            if (bytes[0] == 0x20 && bytes[1] == 0x01 && bytes[2] == 0x00 && bytes[3] == 0x00)
            {
                return true;
            }

            // Deprecated IPv4-compatible IPv6 ::w.x.y.z
            if (bytes.AsSpan(0, 12).IndexOfAnyExcept((byte)0) < 0)
            {
                return IsAlwaysForbiddenWidgetIpAddress(new IPAddress(bytes.AsSpan(12, 4)));
            }
        }

        return false;
    }

    public static bool TryValidatePublicHttpsUrl(
        string? rawUrl,
        out Uri? validUri,
        out string? errorMessage)
    {
        validUri = null;
        errorMessage = null;

        if (string.IsNullOrWhiteSpace(rawUrl))
        {
            errorMessage = "Network source 'url' is required.";
            return false;
        }

        if (!Uri.TryCreate(rawUrl.Trim(), UriKind.Absolute, out Uri? uri))
        {
            errorMessage = $"Invalid URL '{rawUrl}'.";
            return false;
        }

        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            errorMessage = "Only 'https://' URLs are permitted for Custom Widget network sources.";
            return false;
        }

        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            errorMessage = "Embedded credentials in URLs are not permitted.";
            return false;
        }

        string host = uri.DnsSafeHost.Trim().TrimEnd('.');
        if (string.IsNullOrWhiteSpace(host))
        {
            errorMessage = "URL host cannot be empty.";
            return false;
        }

        if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith(".local", StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith(".internal", StringComparison.OrdinalIgnoreCase))
        {
            errorMessage = $"Local or internal host '{host}' is blocked for security.";
            return false;
        }

        if (IPAddress.TryParse(host, out IPAddress? literalIp))
        {
            byte[] literalBytes = literalIp.IsIPv4MappedToIPv6
                ? literalIp.MapToIPv4().GetAddressBytes()
                : literalIp.GetAddressBytes();
            bool isLiteralBenchmarkTunIp = literalBytes.Length == 4 &&
                                           literalBytes[0] == 198 &&
                                           literalBytes[1] is 18 or 19;
            if (IsBlockedIpAddress(literalIp) || isLiteralBenchmarkTunIp)
            {
                errorMessage = $"Private or loopback IP address '{host}' is blocked for security.";
                return false;
            }
        }

        validUri = uri;
        return true;
    }

    public static bool IsBlockedIpAddress(IPAddress address)
    {
        if (IPAddress.IsLoopback(address) ||
            address.Equals(IPAddress.Any) ||
            address.Equals(IPAddress.IPv6Any) ||
            address.Equals(IPAddress.Broadcast) ||
            address.Equals(IPAddress.IPv6None))
        {
            return true;
        }

        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            byte[] bytes = address.GetAddressBytes();
            return bytes[0] switch
            {
                0 => true,                                           // 0.0.0.0/8
                10 => true,                                          // 10.0.0.0/8 RFC1918
                100 when bytes[1] >= 64 && bytes[1] <= 127 => true,  // 100.64.0.0/10 CGNAT
                127 => true,                                         // 127.0.0.0/8 loopback
                169 when bytes[1] == 254 => true,                    // 169.254.0.0/16 link-local / cloud metadata
                172 when bytes[1] >= 16 && bytes[1] <= 31 => true,   // 172.16.0.0/12 RFC1918
                192 when bytes[1] == 168 => true,                    // 192.168.0.0/16 RFC1918
                >= 224 => true,                                      // Multicast & reserved
                _ => false
            };
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6Multicast)
            {
                return true;
            }

            byte[] bytes = address.GetAddressBytes();
            // fc00::/7 Unique Local Addresses
            if ((bytes[0] & 0xFE) == 0xFC)
            {
                return true;
            }

            // NAT64 local-use prefix 64:ff9b:1::/48 (RFC 8215)
            if (bytes[0] == 0x00 && bytes[1] == 0x64 && bytes[2] == 0xFF && bytes[3] == 0x9B &&
                bytes[4] == 0x00 && bytes[5] == 0x01)
            {
                return true;
            }

            // NAT64 well-known prefix 64:ff9b::/96 (RFC 6052) -> embedded IPv4 in bytes[12..16]
            if (bytes[0] == 0x00 && bytes[1] == 0x64 && bytes[2] == 0xFF && bytes[3] == 0x9B &&
                bytes[4] == 0 && bytes[5] == 0 && bytes[6] == 0 && bytes[7] == 0 &&
                bytes[8] == 0 && bytes[9] == 0 && bytes[10] == 0 && bytes[11] == 0)
            {
                return IsBlockedIpAddress(new IPAddress(bytes.AsSpan(12, 4)));
            }

            // 6to4 prefix 2002::/16 (RFC 3056) -> embedded IPv4 in bytes[2..6]
            if (bytes[0] == 0x20 && bytes[1] == 0x02)
            {
                return IsBlockedIpAddress(new IPAddress(bytes.AsSpan(2, 4)));
            }

            // Teredo prefix 2001:0000::/32 (RFC 4380) -> block tunnel endpoints outright
            if (bytes[0] == 0x20 && bytes[1] == 0x01 && bytes[2] == 0x00 && bytes[3] == 0x00)
            {
                return true;
            }

            // Deprecated IPv4-compatible IPv6 ::w.x.y.z (bytes[0..12] == 0)
            if (bytes.AsSpan(0, 12).IndexOfAnyExcept((byte)0) < 0)
            {
                return IsBlockedIpAddress(new IPAddress(bytes.AsSpan(12, 4)));
            }
        }

        return false;
    }

    public static async ValueTask<Stream> ConnectToValidatedPublicIpAsync(
        SocketsHttpConnectionContext context,
        CancellationToken cancellationToken)
    {
        string host = context.DnsEndPoint.Host.Trim().TrimEnd('.');
        IPAddress[] addresses = IPAddress.TryParse(host, out IPAddress? literalIp)
            ? [literalIp]
            : await Dns.GetHostAddressesAsync(host, cancellationToken).ConfigureAwait(false);
        if (addresses.Length == 0)
        {
            throw new SocketException((int)SocketError.HostNotFound);
        }

        foreach (IPAddress ip in addresses)
        {
            if (IsBlockedIpAddress(ip))
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

    public static async ValueTask<Stream> ConnectToValidatedLocalOrPublicIpAsync(
        SocketsHttpConnectionContext context,
        CancellationToken cancellationToken)
    {
        string host = context.DnsEndPoint.Host.Trim().TrimEnd('.');
        IPAddress[] addresses = IPAddress.TryParse(host, out IPAddress? literalIp)
            ? [literalIp]
            : await Dns.GetHostAddressesAsync(host, cancellationToken).ConfigureAwait(false);
        if (addresses.Length == 0)
        {
            throw new SocketException((int)SocketError.HostNotFound);
        }

        foreach (IPAddress ip in addresses)
        {
            if (IsAlwaysForbiddenWidgetIpAddress(ip))
            {
                throw new InvalidOperationException(
                    $"Blocked connection to forbidden link-local/metadata IP address '{ip}' resolved for '{host}'.");
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

    public static bool IsTrustedOfficialRemoteHost(Uri uri)
    {
        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            !uri.IsDefaultPort)
        {
            return false;
        }

        string host = uri.DnsSafeHost.Trim().TrimEnd('.').ToLowerInvariant();
        if (host == "deskbox.fun" || host.EndsWith(".deskbox.fun", StringComparison.Ordinal))
        {
            return true;
        }

        if (host is "raw.githubusercontent.com" or "github.com")
        {
            string unescapedPath;
            try
            {
                unescapedPath = Uri.UnescapeDataString(uri.AbsolutePath);
            }
            catch (UriFormatException)
            {
                return false;
            }

            if (unescapedPath.Contains('\\') ||
                unescapedPath.Contains("/..", StringComparison.Ordinal) ||
                unescapedPath.Contains("/./", StringComparison.Ordinal) ||
                unescapedPath.Contains("//", StringComparison.Ordinal))
            {
                return false;
            }

            return unescapedPath.StartsWith("/Tianyu199509/DeskBox/", StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }

    public static async Task<HttpResponseMessage> SendGetWithValidatedRedirectsAsync(
        HttpClient client,
        Uri initialUri,
        bool requireTrustedOfficialHost,
        Action<HttpRequestMessage>? configureRequest = null,
        int maxRedirects = 4,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(initialUri);

        if (!TryValidatePublicHttpsUrl(initialUri.AbsoluteUri, out Uri? currentUri, out string? initError) ||
            currentUri is null)
        {
            throw new InvalidOperationException(initError ?? "Invalid HTTPS URL.");
        }

        if (requireTrustedOfficialHost && !IsTrustedOfficialRemoteHost(currentUri))
        {
            throw new InvalidOperationException(
                $"URL host '{currentUri.DnsSafeHost}' is not in the trusted DeskBox official allowlist.");
        }

        for (int hop = 0; ; hop++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, currentUri);
            if (hop == 0)
            {
                configureRequest?.Invoke(request);
            }

            HttpResponseMessage response = await client.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);

            int status = (int)response.StatusCode;
            if (status is 301 or 302 or 303 or 307 or 308 &&
                response.Headers.Location is { } location)
            {
                if (hop >= maxRedirects)
                {
                    response.Dispose();
                    throw new InvalidOperationException("Too many HTTP redirects.");
                }

                Uri nextCandidate = location.IsAbsoluteUri
                    ? location
                    : new Uri(currentUri, location);
                response.Dispose();

                if (!TryValidatePublicHttpsUrl(nextCandidate.AbsoluteUri, out Uri? validatedNext, out string? redirectError) ||
                    validatedNext is null)
                {
                    throw new InvalidOperationException(
                        redirectError ?? "Redirect target failed HTTPS/SSRF validation.");
                }

                if (requireTrustedOfficialHost && !IsTrustedOfficialRemoteHost(validatedNext))
                {
                    throw new InvalidOperationException(
                        $"Redirect target host '{validatedNext.DnsSafeHost}' is not in the trusted DeskBox official allowlist.");
                }

                currentUri = validatedNext;
                continue;
            }

            return response;
        }
    }
}
