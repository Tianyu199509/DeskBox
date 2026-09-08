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

    public PluginDeclarativeExecutor(
        PluginCapabilityGate gate,
        PluginPinnedHttpClientFactoryDelegate? httpClientFactory = null)
    {
        _outerGate = gate;
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
            string url = source.Value.Url;
            try
            {
                packageGate.RequireAllowed(new Uri(url), "network.fetch");

                HttpClient? client = _httpClientFactory(new Uri(url).Host);
                if (client is null)
                {
                    dataSourceErrors[source.Key] = $"host '{new Uri(url).Host}' resolves to no globally-routable address";
                    continue;
                }
                using (client)
                await using (Stream stream = await client.GetStreamAsync(url, cancellationToken))
                {
                    fetched[source.Key] = await ReadCappedJsonAsync(stream, cancellationToken);
                }
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

        var states = new Dictionary<string, PluginContributionState>();
        foreach (VerifiedContribution contribution in package.Contributions)
        {
            var effective = new Dictionary<string, string>(contribution.PayloadStringFields);
            var notes = new List<string>();
            foreach (KeyValuePair<string, VerifiedBinding> binding in contribution.Bindings)
            {
                if (dataSourceErrors.TryGetValue(binding.Value.Source, out string? sourceError))
                {
                    notes.Add($"{binding.Key}: fallback ({sourceError})");
                    continue;
                }
                if (!fetched.TryGetValue(binding.Value.Source, out JsonElement document) ||
                    TryEvaluateJsonPath(document, binding.Value.Path) is not { } value)
                {
                    notes.Add($"{binding.Key}: fallback (path {binding.Value.Path} missing)");
                    continue;
                }
                effective[binding.Key] = value;
            }
            states[contribution.Id] = new PluginContributionState(
                contribution.Id,
                effective,
                notes);
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
    internal static string? TryEvaluateJsonPath(JsonElement document, string pathExpression)
    {
        if (!pathExpression.StartsWith("$.", StringComparison.Ordinal))
        {
            return null;
        }
        JsonElement current = document;
        foreach (string segment in pathExpression[2..].Split('.'))
        {
            System.Text.RegularExpressions.Match match =
                System.Text.RegularExpressions.Regex.Match(segment, @"^([A-Za-z0-9_]+)((?:\[\d+\])*)$");
            if (!match.Success)
            {
                return null;
            }
            if (match.Groups[1].Length > 0)
            {
                if (current.ValueKind != JsonValueKind.Object ||
                    !current.TryGetProperty(match.Groups[1].Value, out current))
                {
                    return null;
                }
            }
            foreach (System.Text.RegularExpressions.Match index in
                     System.Text.RegularExpressions.Regex.Matches(match.Groups[2].Value, @"\[(\d+)\]"))
            {
                int arrayIndex = int.Parse(index.Groups[1].Value);
                if (current.ValueKind != JsonValueKind.Array || arrayIndex >= current.GetArrayLength())
                {
                    return null;
                }
                current = current[arrayIndex];
            }
        }
        return current.ValueKind switch
        {
            JsonValueKind.String => current.GetString(),
            JsonValueKind.Number => current.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => null
        };
    }
}

public delegate HttpClient? PluginPinnedHttpClientFactoryDelegate(string hostname);

public sealed record PluginWidgetStateResult(
    string PackageId,
    IReadOnlyDictionary<string, PluginContributionState> Contributions,
    IReadOnlyDictionary<string, string> DataSourceErrors);

public sealed record PluginContributionState(
    string ContributionId,
    IReadOnlyDictionary<string, string> EffectiveFields,
    IReadOnlyList<string> Notes);
