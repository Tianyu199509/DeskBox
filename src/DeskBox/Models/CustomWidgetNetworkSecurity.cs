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
    public const int MinNetworkSourceIntervalMs = 300_000;      // 5 minutes floor
    public const int DefaultNetworkSourceIntervalMs = 900_000;  // 15 minutes default
    public const int MaxNetworkSourceIntervalMs = 86_400_000;   // 24 hours ceiling
    public const int ManualRefreshCooldownSeconds = 15;
    public const int MaxResponseBytes = 1_048_576;              // 1 MB cap

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

        if (IPAddress.TryParse(host, out IPAddress? literalIp) &&
            IsBlockedIpAddress(literalIp))
        {
            errorMessage = $"Private or loopback IP address '{host}' is blocked for security.";
            return false;
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
        }

        return false;
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
