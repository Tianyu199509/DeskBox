using System.Net;

namespace DeskBox.Services.Plugins;

/// <summary>
/// The product-side capability gate for plugin capabilities (roadmap 16.10):
/// a capability runs only when the package REQUESTED the permission, the
/// concrete URL host falls inside the declared scope, and the HOST granted
/// it (requested != granted - manifests never grant anything). Stricter
/// than the spike harnesses: https-only with NO loopback exemption, and
/// loopback/private/link-local/metadata addresses are denied by default
/// (DNS-rebinding style SSRF defense; a hostname that resolves to a
/// private address is the network.local backlog item).
/// URL canonicalization is Uri-based - one implementation, never a
/// hand-rolled parse (round-8 discipline), shared conformance vectors in
/// PluginCapabilityGateConformanceTests.
/// </summary>
public sealed class PluginCapabilityGate
{
    private readonly IReadOnlyDictionary<string, IReadOnlyList<string>> _requestedScopes;
    private readonly IReadOnlyDictionary<string, IReadOnlyList<string>> _grantedHosts;

    public PluginCapabilityGate(
        IReadOnlyDictionary<string, IReadOnlyList<string>> requestedScopes,
        IReadOnlyDictionary<string, IReadOnlyList<string>> grantedHosts)
    {
        _requestedScopes = requestedScopes;
        _grantedHosts = grantedHosts;
    }

    /// <summary>Throws <see cref="PluginCapabilityRefusedException"/> when the call must not run.</summary>
    public void RequireAllowed(Uri url, string permissionId)
    {
        if (!_requestedScopes.TryGetValue(permissionId, out IReadOnlyList<string>? scope))
        {
            throw new PluginCapabilityRefusedException(
                $"capability '{permissionId}' is not declared by the package");
        }

        if (!string.Equals(url.Scheme, "https", StringComparison.OrdinalIgnoreCase))
        {
            throw new PluginCapabilityRefusedException(
                $"url '{url}' must use https (scheme enforcement; http requests are refused)");
        }

        string host = url.Host.ToLowerInvariant();
        if (IsLocalNetworkAddress(url, host))
        {
            throw new PluginCapabilityRefusedException(
                $"url host '{host}' is a local-network address; plugin capabilities do not reach loopback, " +
                "private, link-local, or metadata addresses");
        }

        if (!scope.Contains(host, StringComparer.OrdinalIgnoreCase))
        {
            throw new PluginCapabilityRefusedException(
                $"url '{url}' is outside the declared {permissionId} scope");
        }

        if (!_grantedHosts.TryGetValue(permissionId, out IReadOnlyList<string>? granted) ||
            !granted.Contains(host, StringComparer.OrdinalIgnoreCase))
        {
            throw new PluginCapabilityRefusedException(
                $"url host '{host}' has no granted {permissionId} capability (requested != granted)");
        }
    }

    private static bool IsLocalNetworkAddress(Uri url, string host)
    {
        if (host is "localhost")
        {
            return true;
        }
        if (url.HostNameType is not (UriHostNameType.IPv4 or UriHostNameType.IPv6))
        {
            return false;
        }
        if (!IPAddress.TryParse(host.Trim('[', ']'), out IPAddress? address))
        {
            return false;
        }

        return !PluginPinnedHttpClientFactory.IsGloballyRoutable(address);
    }
}

/// <summary>Policy refusal: the capability call must not execute (not a data failure).</summary>
public sealed class PluginCapabilityRefusedException(string message) : Exception(message);
