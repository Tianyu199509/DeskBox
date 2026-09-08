using DeskBox.Services.Plugins;

namespace DeskBox.Tests;

/// <summary>
/// Capability-gate conformance vectors (roadmap 16.10): the same decision
/// table every runtime must satisfy - https allow, http deny, host-suffix
/// deny, port normalization, userinfo, and local-network denial by default.
/// The spike harnesses (Node/Rust) enforce the same table minus the
/// local-network vectors (their self-test mocks use loopback through an
/// explicit, documented exemption).
/// </summary>
public sealed class PluginCapabilityGateConformanceTests
{
    private static PluginCapabilityGate CreateGate(
        string[] requestedScope,
        string[] grantedHosts)
    {
        return new PluginCapabilityGate(
            new Dictionary<string, IReadOnlyList<string>>
            {
                ["network.fetch"] = requestedScope
            },
            new Dictionary<string, IReadOnlyList<string>>
            {
                ["network.fetch"] = grantedHosts
            });
    }

    private static void AssertAllowed(string url, string[] scope, string[] grants)
    {
        CreateGate(scope, grants).RequireAllowed(new Uri(url), "network.fetch");
    }

    private static string Refusal(string url, string[] scope, string[] grants)
    {
        return Assert.Throws<PluginCapabilityRefusedException>(() =>
                AssertAllowed(url, scope, grants))
            .Message;
    }

    [Fact]
    public void HttpsGrantedHost_IsAllowed()
    {
        AssertAllowed(
            "https://api.github.com/repos/DeskBox",
            ["api.github.com"],
            ["api.github.com"]);
    }

    [Fact]
    public void ExplicitDefaultPort_NormalizesToTheSameHost()
    {
        AssertAllowed(
            "https://api.github.com:443/repos/DeskBox",
            ["api.github.com"],
            ["api.github.com"]);
    }

    [Fact]
    public void HttpScheme_IsRefusedEvenWhenGranted()
    {
        string refusal = Refusal(
            "http://api.github.com/repos/DeskBox",
            ["api.github.com"],
            ["api.github.com"]);

        Assert.Contains("must use https", refusal, StringComparison.Ordinal);
    }

    [Fact]
    public void HostSuffixLookalike_IsRefused()
    {
        // api.github.com.evil.com is NOT api.github.com.
        string refusal = Refusal(
            "https://api.github.com.evil.com/x",
            ["api.github.com"],
            ["api.github.com"]);

        Assert.Contains("outside the declared", refusal, StringComparison.Ordinal);
    }

    [Fact]
    public void UserinfoDoesNotChangeTheCanonicalHost()
    {
        // https://user@api.github.com/ connects to api.github.com; Uri's
        // Host excludes userinfo, so the gate decision is host-based (the
        // hand-parser bypass shape from round 8 must not recur).
        AssertAllowed(
            "https://user@api.github.com/x",
            ["api.github.com"],
            ["api.github.com"]);
    }

    [Theory]
    [InlineData("https://127.0.0.1/x")]
    [InlineData("https://localhost/x")]
    [InlineData("https://[::1]/x")]
    [InlineData("https://10.1.2.3/x")]
    [InlineData("https://172.16.5.4/x")]
    [InlineData("https://192.168.1.1/x")]
    [InlineData("https://169.254.169.254/latest/meta-data")]
    [InlineData("https://[fd12::1]/x")]
    // Round 10: IPv4-mapped IPv6 must classify as the IPv4 it is.
    [InlineData("https://[::ffff:127.0.0.1]/x")]
    [InlineData("https://[::ffff:10.1.2.3]/x")]
    [InlineData("https://[::ffff:192.168.1.1]/x")]
    // Round 10: unspecified and multicast are not globally routable.
    [InlineData("https://0.0.0.0/x")]
    [InlineData("https://[::]/x")]
    [InlineData("https://224.0.0.1/x")]
    public void LocalNetwork_IsRefusedByDefault(string url)
    {
        string refusal = Refusal(url,
            ["127.0.0.1", "localhost", "::1", "10.1.2.3", "169.254.169.254", "fd12::1",
             "::ffff:127.0.0.1", "::ffff:10.1.2.3", "::ffff:192.168.1.1", "0.0.0.0", "224.0.0.1"],
            ["127.0.0.1", "localhost", "::1", "10.1.2.3", "169.254.169.254", "fd12::1",
             "::ffff:127.0.0.1", "::ffff:10.1.2.3", "::ffff:192.168.1.1", "0.0.0.0", "224.0.0.1"]);

        Assert.Contains("local-network", refusal, StringComparison.Ordinal);
    }

    [Fact]
    public void UndeclaredCapability_IsRefused()
    {
        var gate = new PluginCapabilityGate(
            new Dictionary<string, IReadOnlyList<string>>(),
            new Dictionary<string, IReadOnlyList<string>>());

        PluginCapabilityRefusedException refusal = Assert.Throws<PluginCapabilityRefusedException>(() =>
            gate.RequireAllowed(new Uri("https://api.github.com/x"), "network.fetch"));

        Assert.Contains("not declared", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RequestedButNotGranted_IsRefused()
    {
        string refusal = Refusal(
            "https://api.github.com/x",
            ["api.github.com"],
            []);

        Assert.Contains("requested != granted", refusal, StringComparison.Ordinal);
    }
}
