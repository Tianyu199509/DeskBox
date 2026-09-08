using System.Net;
using System.Text.Json;
using DeskBox.Services.Plugins;

namespace DeskBox.Tests;

/// <summary>
/// B2a: the declarative executor core - gate integration, JSON-path
/// binding evaluation, payload fallback on any failure, and the pinned
/// HTTP client factory's globally-routable classification. Network
/// touching is faked via the injectable client factory (deterministic).
/// </summary>
public sealed class PluginDeclarativeExecutorTests
{
    private static VerifiedPluginPackage CreateLivePackage() => new()
    {
        PackageId = "com.github.stats.live",
        Version = "0.1.0",
        PublisherFingerprint = new string('a', 64),
        Runtime = "none",
        ContentHash = new string('c', 64),
        ManifestRelativePath = "manifest.json",
        Permissions =
        [
            new PluginRequestedPermission("network.fetch", ["api.github.com"]),
            new PluginRequestedPermission("shell.open", ["github.com"])
        ],
        Contributions =
        [
            new VerifiedContribution(
                "live-stars",
                "DeskBox Stars (live)",
                "metric",
                new Dictionary<string, string>
                {
                    ["label"] = "Stars",
                    ["value"] = "…",
                    ["caption"] = "Tianyu199509/DeskBox"
                },
                new Dictionary<string, VerifiedBinding>
                {
                    ["value"] = new VerifiedBinding("github-repo", "$.stargazers_count")
                })
        ],
        DataSources = new Dictionary<string, VerifiedDataSource>
        {
            ["github-repo"] = new VerifiedDataSource(
                "https://api.github.com/repos/Tianyu199509/DeskBox",
                RefreshSeconds: 300)
        }
    };

    private static IReadOnlyDictionary<string, IReadOnlyList<string>> FullGrants =>
        new Dictionary<string, IReadOnlyList<string>>
        {
            ["network.fetch"] = ["api.github.com"]
        };

    private static PluginDeclarativeExecutor CreateExecutor(
        Func<string?, bool>? canReachHost = null,
        JsonElement? response = null)
    {
        return new PluginDeclarativeExecutor(
            gate: new PluginCapabilityGate(
                new Dictionary<string, IReadOnlyList<string>>
                {
                    ["network.fetch"] = ["api.github.com"]
                },
                FullGrants),
            httpClientFactory: hostname =>
            {
                if (canReachHost is not null && !canReachHost(hostname))
                {
                    return null;
                }
                var content = new StringContent(
                    response is null
                        ? "{\"stargazers_count\":1284}"
                        : JsonSerializer.Serialize(response.Value),
                    System.Text.Encoding.UTF8,
                    "application/json");
                var fake = new HttpMessageHandlerShim(content);
                return new HttpClient(fake);
            });
    }

    [Fact]
    public async Task Evaluate_BindsLiveValueOverFallback()
    {
        var executor = CreateExecutor();

        PluginWidgetStateResult result = await executor.EvaluateAsync(
            CreateLivePackage(), FullGrants);

        PluginContributionState state = result.Contributions["live-stars"];
        Assert.Equal("1284", state.EffectiveFields["value"]);
        Assert.Equal("Stars", state.EffectiveFields["label"]); // untouched payload field
        Assert.Empty(state.Notes);
        Assert.Empty(result.DataSourceErrors);
    }

    [Fact]
    public async Task Evaluate_NestedArrayPath_Resolves()
    {
        var executor = CreateExecutor(response: JsonDocument.Parse(
            "{\"items\":[{\"name\":\"first\"},{\"name\":\"second\"}]}").RootElement.Clone());

        var package = CreateLivePackage();
        var contribution = package.Contributions.Single();
        package = package with
        {
            Contributions =
            [
                new VerifiedContribution(
                    contribution.Id,
                    contribution.DisplayName,
                    contribution.Template,
                    contribution.PayloadStringFields,
                    new Dictionary<string, VerifiedBinding>
                    {
                        ["value"] = new VerifiedBinding("github-repo", "$.items[1].name")
                    })
            ]
        };

        PluginWidgetStateResult result = await executor.EvaluateAsync(package, FullGrants);

        Assert.Equal("second", result.Contributions["live-stars"].EffectiveFields["value"]);
    }

    [Fact]
    public async Task Evaluate_UngrantedCapability_FallsBack()
    {
        var executor = CreateExecutor();
        var emptyGrants = new Dictionary<string, IReadOnlyList<string>>();

        PluginWidgetStateResult result = await executor.EvaluateAsync(
            CreateLivePackage(), emptyGrants);

        PluginContributionState state = result.Contributions["live-stars"];
        Assert.Equal("…", state.EffectiveFields["value"]);
        Assert.Contains(state.Notes, n => n.Contains("requested != granted", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Evaluate_UnresolvableHost_FallsBack()
    {
        var executor = CreateExecutor(canReachHost: _ => false);

        PluginWidgetStateResult result = await executor.EvaluateAsync(
            CreateLivePackage(), FullGrants);

        Assert.Equal("…", result.Contributions["live-stars"].EffectiveFields["value"]);
        Assert.Contains("no globally-routable address", result.DataSourceErrors["github-repo"]);
    }

    [Fact]
    public async Task Evaluate_MissingPath_FallsBack()
    {
        var executor = CreateExecutor(response: JsonDocument.Parse(
            "{\"full_name\":\"DeskBox\"}").RootElement.Clone());

        PluginWidgetStateResult result = await executor.EvaluateAsync(
            CreateLivePackage(), FullGrants);

        PluginContributionState state = result.Contributions["live-stars"];
        Assert.Equal("…", state.EffectiveFields["value"]);
        Assert.Contains(state.Notes, n => n.Contains("path $.stargazers_count missing", StringComparison.Ordinal));
    }

    [Fact]
    public void GloballyRoutable_Classification_MatchesGateVectors()
    {
        Assert.True(PluginPinnedHttpClientFactory.IsGloballyRoutable(IPAddress.Parse("140.82.112.3")));
        Assert.True(PluginPinnedHttpClientFactory.IsGloballyRoutable(IPAddress.Parse("2607:f8b0::1")));
        Assert.False(PluginPinnedHttpClientFactory.IsGloballyRoutable(IPAddress.Parse("127.0.0.1")));
        Assert.False(PluginPinnedHttpClientFactory.IsGloballyRoutable(IPAddress.Parse("10.1.2.3")));
        Assert.False(PluginPinnedHttpClientFactory.IsGloballyRoutable(IPAddress.Parse("192.168.1.1")));
        Assert.False(PluginPinnedHttpClientFactory.IsGloballyRoutable(IPAddress.Parse("169.254.169.254")));
        Assert.False(PluginPinnedHttpClientFactory.IsGloballyRoutable(IPAddress.Parse("0.0.0.0")));
        Assert.False(PluginPinnedHttpClientFactory.IsGloballyRoutable(IPAddress.Parse("224.0.0.1")));
        Assert.False(PluginPinnedHttpClientFactory.IsGloballyRoutable(IPAddress.Parse("::1")));
        Assert.False(PluginPinnedHttpClientFactory.IsGloballyRoutable(IPAddress.Parse("fd12::1")));
        // IPv4-mapped must classify as the IPv4 it is.
        Assert.False(PluginPinnedHttpClientFactory.IsGloballyRoutable(
            IPAddress.Parse("::ffff:127.0.0.1")));
    }

    private sealed class HttpMessageHandlerShim(HttpContent content) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            await Task.Yield();
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        }
    }
}
