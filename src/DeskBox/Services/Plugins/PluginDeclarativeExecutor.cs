using System.Net.Http;
using System.Text.Json;

namespace DeskBox.Services.Plugins;

/// <summary>
/// Executes a verified declarative package's widget state (roadmap B2a):
/// for each data source the HOST fetches (behind the capability gate and
/// the pinned-IP HTTP client), evaluates JSON-path bindings, and keeps
/// payload fallback values on any failure - the same resilience contract
/// as the Node spike harness. No third-party code ever runs.
///
/// Scheduling (refresh timers, widget registration) lands with the B2b
/// template renderer; this executor is the pure state-evaluation core so
/// it stays unit-testable without the UI.
/// </summary>
public sealed class PluginDeclarativeExecutor
{
    private const int MaxResponseBytes = 2 * 1024 * 1024;

    private readonly PluginCapabilityGate _outerGate;
    private readonly PluginPinnedHttpClientFactoryDelegate _httpClientFactory;
    private readonly TimeSpan _sourceTimeout;

    public PluginDeclarativeExecutor(
        PluginCapabilityGate gate,
        PluginPinnedHttpClientFactoryDelegate? httpClientFactory = null,
        TimeSpan? sourceTimeout = null)
    {
        _outerGate = gate;
        _sourceTimeout = sourceTimeout ?? TimeSpan.FromSeconds(10);
        if (_sourceTimeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(sourceTimeout));
        _httpClientFactory = httpClientFactory ?? new PluginPinnedHttpClientFactoryDelegate(
            PluginPinnedHttpClientFactory.CreateForHost);
    }

    /// <summary>
    /// Evaluates all contributions of a package: fetches each referenced
    /// data source once (gated + pinned), applies bindings, returns the
    /// effective payload fields per contribution. Failures keep fallbacks.
    /// </summary>
    public async Task<PluginWidgetStateResult> EvaluateAsync(
        VerifiedPluginPackage package,
        IReadOnlyDictionary<string, IReadOnlyList<string>> grantedHosts,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(package);

        // Per-package gate bound to THIS package's requested scopes and the
        // caller-provided grants (requested != granted stays real).
        var requestedScopes = package.Permissions.ToDictionary(
            p => p.Id,
            p => p.AllowHosts);
        var packageGate = new PluginCapabilityGate(requestedScopes, grantedHosts);

        var fetched = new Dictionary<string, JsonElement>();
        var dataSourceErrors = new Dictionary<string, string>();
        foreach (KeyValuePair<string, VerifiedDataSource> source in package.DataSources)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string url = source.Value.Url;
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(_sourceTimeout);
            try
            {
                packageGate.RequireAllowed(new Uri(url), "network.fetch");
                _outerGate.RequireAllowed(new Uri(url), "network.fetch");

                HttpClient? client = _httpClientFactory(new Uri(url).Host);
                if (client is null)
                {
                    dataSourceErrors[source.Key] = $"host '{new Uri(url).Host}' resolves to no globally-routable address";
                    continue;
                }
                using (client)
                using (var request = new HttpRequestMessage(HttpMethod.Get, url))
                {
                    request.Headers.UserAgent.ParseAdd("DeskBox-Plugins/1.0");
                    request.Headers.Accept.ParseAdd("application/json");
                    using HttpResponseMessage response = await client.SendAsync(
                        request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
                    response.EnsureSuccessStatusCode();
                    if ((int)response.StatusCode is >= 300 and < 400)
                        throw new HttpRequestException("plugin data source redirects are refused");
                    if (response.Content.Headers.ContentLength > MaxResponseBytes)
                        throw new IOException("response exceeds the host limit");
                    await using Stream stream = await response.Content.ReadAsStreamAsync(deadline.Token);
                    fetched[source.Key] = await ReadCappedJsonAsync(stream, deadline.Token);
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                dataSourceErrors[source.Key] = "data source timed out";
            }
            catch (PluginCapabilityRefusedException refused)
            {
                // Policy refusals are recorded per-source; bindings fall
                // back. (The spike exited non-zero for refusals; a product
                // widget degrades instead of vanishing.)
                dataSourceErrors[source.Key] = refused.Message;
            }
            catch (Exception error) when (error is HttpRequestException or JsonException or IOException)
            {
                dataSourceErrors[source.Key] = error.Message;
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        var states = new Dictionary<string, PluginContributionState>();
        foreach (VerifiedContribution contribution in package.Contributions)
        {
            var effective = new Dictionary<string, string>(contribution.PayloadStringFields);
            var effectivePayload = contribution.Payload.ValueKind == JsonValueKind.Object
                ? contribution.Payload.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone())
                : new Dictionary<string, JsonElement>();
            var notes = new List<string>();
            foreach (KeyValuePair<string, VerifiedBinding> binding in contribution.Bindings)
            {
                if (dataSourceErrors.TryGetValue(binding.Value.Source, out string? sourceError))
                {
                    notes.Add($"{binding.Key}: fallback ({sourceError})");
                    continue;
                }
                if (!fetched.TryGetValue(binding.Value.Source, out JsonElement document) ||
                    !PluginJsonPath.TryResolve(document, binding.Value.Path, out JsonElement value))
                {
                    notes.Add($"{binding.Key}: fallback (path {binding.Value.Path} missing)");
                    continue;
                }
                effectivePayload[binding.Key] = value.Clone();
                if (PluginJsonPath.ScalarText(value) is { } text) effective[binding.Key] = text;
            }
            states[contribution.Id] = new PluginContributionState(
                contribution.Id,
                effective,
                notes) { EffectivePayload = effectivePayload };
        }

        return new PluginWidgetStateResult(package.PackageId, states, dataSourceErrors);
    }

    /// <summary>Reads at most MaxResponseBytes and parses JSON (2MB host cap, packages cannot raise it).</summary>
    private static async Task<JsonElement> ReadCappedJsonAsync(Stream stream, CancellationToken cancellationToken)
    {
        using var capped = new MemoryStream();
        byte[] buffer = new byte[64 * 1024];
        long total = 0;
        int read;
        while ((read = await stream.ReadAsync(buffer, cancellationToken)) > 0)
        {
            total += read;
            if (total > MaxResponseBytes)
            {
                throw new IOException($"response exceeds the {MaxResponseBytes}-byte host limit");
            }
            capped.Write(buffer, 0, read);
        }
        using JsonDocument document = JsonDocument.Parse(capped.ToArray());
        return document.RootElement.Clone();
    }

    /// <summary>Minimal JSON path ($.a.b[0].c) with null on any miss - same semantics as the spike harness.</summary>
    internal static string? TryEvaluateJsonPath(JsonElement document, string pathExpression) =>
        PluginJsonPath.TryResolve(document, pathExpression, out JsonElement value) ? PluginJsonPath.ScalarText(value) : null;

}

public delegate HttpClient? PluginPinnedHttpClientFactoryDelegate(string hostname);

public sealed record PluginWidgetStateResult(
    string PackageId,
    IReadOnlyDictionary<string, PluginContributionState> Contributions,
    IReadOnlyDictionary<string, string> DataSourceErrors);

public sealed record PluginContributionState(
    string ContributionId,
    IReadOnlyDictionary<string, string> EffectiveFields,
    IReadOnlyList<string> Notes)
{
    public IReadOnlyDictionary<string, JsonElement> EffectivePayload { get; init; } =
        new Dictionary<string, JsonElement>();
}
