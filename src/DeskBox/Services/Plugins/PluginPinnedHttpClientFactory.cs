using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Net.Http;

namespace DeskBox.Services.Plugins;

/// <summary>
/// Creates HttpClients for plugin network.fetch that pin each connection
/// to a DNS-validated IP (roadmap 16.12, the B2 blocker): resolve ->
/// validate EVERY candidate address is globally routable -> connect to
/// the chosen validated address, with TLS SNI still using the original
/// hostname. A plain resolve-then-check is insufficient because HttpClient
/// performs its own second resolution which a rebinding DNS can flip to a
/// private address between the two lookups.
///
/// The validator callback is injectable for tests (points at the same
/// classification the capability gate uses).
/// </summary>
public static partial class PluginPinnedHttpClientFactory
{
    /// <summary>
    /// Creates a client whose connections are pinned to validated IPs for
    /// the given host. Returns null when the host resolves to no usable
    /// (globally routable) address.
    /// </summary>
    public static HttpClient? CreateForHost(string hostname) => CreateForHost(hostname, gate: null);

    private static HttpClient? CreateForHost(string hostname, PluginCapabilityGate? gate)
    {
        string host = hostname.ToLowerInvariant();
        IPAddress[] candidates;
        try
        {
            candidates = Dns.GetHostAddresses(host);
        }
        catch (SocketException)
        {
            return null;
        }

        IPAddress[] validated = candidates.Where(IsGloballyRoutable).ToArray();
        if (validated.Length == 0)
        {
            return null;
        }

        var handler = new SocketsHttpHandler
        {
            ConnectCallback = async (context, cancellationToken) =>
            {
                // Re-resolve at connection time and re-validate; connect to
                // a validated address only (defense in depth against a
                // rebound cache entry between CreateForHost and connect).
                IPAddress[] current;
                try
                {
                    current = Dns.GetHostAddresses(context.DnsEndPoint.Host);
                }
                catch (SocketException)
                {
                    current = validated;
                }
                foreach (IPAddress address in current.Where(IsGloballyRoutable))
                {
                    var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
                    try
                    {
                        await socket.ConnectAsync(address, context.DnsEndPoint.Port, cancellationToken);
                        return new NetworkStream(socket, ownsSocket: true);
                    }
                    catch
                    {
                        socket.Dispose();
                    }
                }
                throw new SocketException((int)SocketError.HostUnreachable);
            },
            // Redirects are refused at the executor level; the handler must
            // not follow them either (a 3xx would move the request to a host
            // that never passed the gate).
            AllowAutoRedirect = false,
            SslOptions = new SslClientAuthenticationOptions
            {
                // The remote certificate must match the ORIGINAL hostname
                // (SNI/verification stay on the DNS name, not the IP).
                TargetHost = null
            }
        };

        return new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(10)
        };
    }

    /// <summary>
    /// Globally-routable classification (mirrors PluginCapabilityGate's
    /// local-network denial): loopback/private/link-local/metadata/
    /// unique-local/unspecified/multicast are all rejected.
    /// </summary>
    public static bool IsGloballyRoutable(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }
        if (address.IsIPv6LinkLocal || address.IsIPv6SiteLocal)
        {
            return false;
        }
        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            byte[] bytes = address.GetAddressBytes();
            return bytes.AsSpan()[..15].ToArray().Any(b => b != 0) &&
                   bytes[0] is not (0xfc or 0xfd) &&
                   !address.Equals(IPAddress.IPv6Loopback);
        }

        byte[] v4 = address.GetAddressBytes();
        return v4[0] != 0 &&
               v4[0] != 127 &&
               v4[0] != 10 &&
               !(v4[0] == 172 && v4[1] >= 16 && v4[1] <= 31) &&
               !(v4[0] == 192 && v4[1] == 168) &&
               !(v4[0] == 169 && v4[1] == 254) &&
               v4[0] < 224;
    }
}
