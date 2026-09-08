using System.Net;
using System.Net.Sockets;

namespace DeskBox.Services.Plugins;

/// <summary>Direct HTTPS transport. Async DNS and connection establishment share the request deadline.</summary>
public static class PluginPinnedHttpClientFactory
{
    public static HttpClient CreateForHost(string hostname)
    {
        string host = hostname.Trim('[', ']').ToLowerInvariant();
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseProxy = false,
            ConnectCallback = async (context, cancellationToken) =>
            {
                if (!string.Equals(context.DnsEndPoint.Host.Trim('[', ']'), host, StringComparison.OrdinalIgnoreCase))
                    throw new HttpRequestException("connection host differs from the authorized host");
                IPAddress[] addresses = await Dns.GetHostAddressesAsync(host, cancellationToken);
                // Mixed public/private DNS answers are refused as a whole.
                if (addresses.Length == 0 || addresses.Any(a => !IsGloballyRoutable(a)))
                    throw new HttpRequestException("DNS answer contains a non-public destination");
                foreach (IPAddress address in addresses)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
                    try
                    {
                        await socket.ConnectAsync(address, context.DnsEndPoint.Port, cancellationToken);
                        return new NetworkStream(socket, ownsSocket: true);
                    }
                    catch (SocketException) { socket.Dispose(); }
                    catch { socket.Dispose(); throw; }
                }
                throw new HttpRequestException("no validated address could be reached");
            }
        };
        return new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
    }

    /// <summary>Conservative public-unicast policy; special-purpose ranges require a future explicit local-network capability.</summary>
    public static bool IsGloballyRoutable(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        byte[] bytes = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            // Global unicast only. Exclude transition/protocol-assignment and
            // documentation ranges in addition to local/multicast space.
            return (bytes[0] & 0xe0) == 0x20 &&
                !(bytes[0] == 0x20 && bytes[1] == 0x01 && bytes[2] < 2) &&
                !(bytes[0] == 0x20 && bytes[1] == 0x01 && bytes[2] == 0x0d && bytes[3] == 0xb8) &&
                !(bytes[0] == 0x20 && bytes[1] == 0x02) &&
                !(bytes[0] == 0x3f && bytes[1] == 0xff && (bytes[2] & 0xf0) == 0);
        }
        if (address.AddressFamily != AddressFamily.InterNetwork) return false;
        return bytes[0] is not (0 or 10 or 127) && bytes[0] < 224 &&
            !(bytes[0] == 100 && bytes[1] is >= 64 and <= 127) &&
            !(bytes[0] == 169 && bytes[1] == 254) &&
            !(bytes[0] == 172 && bytes[1] is >= 16 and <= 31) &&
            !(bytes[0] == 192 && (bytes[1] == 168 || bytes[1] == 0 && bytes[2] is 0 or 2 ||
                bytes[1] == 88 && bytes[2] == 99)) &&
            !(bytes[0] == 198 && (bytes[1] is 18 or 19 || bytes[1] == 51 && bytes[2] == 100)) &&
            !(bytes[0] == 203 && bytes[1] == 0 && bytes[2] == 113);
    }
}
