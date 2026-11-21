using System.Text;
using DeskBox.Models;

namespace DeskBox.Services;

internal sealed partial class StudioPreFlightProbeService
{
    private readonly CustomWidgetNetworkSourceEngine? _networkSourceEngine;

    internal StudioPreFlightProbeService(CustomWidgetNetworkSourceEngine? networkSourceEngine = null)
    {
        _networkSourceEngine = networkSourceEngine;
    }

    internal async Task<(string? BlockingProbeFeedback, string? SoftProbeFeedback)> BuildNetworkProbeFeedbackAsync(
        CustomWidgetManifest manifest,
        CancellationToken ct,
        string? logicJs = null,
        IReadOnlyDictionary<string, string>? failedProbeHosts = null)
    {
        var blockingSb = new StringBuilder();
        var softSb = new StringBuilder();

        if (failedProbeHosts is { Count: > 0 })
        {
            var flaggedHosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (CustomWidgetSourceSpec src in manifest.Sources)
            {
                bool hasSecretAuth =
                    (src.Url?.Contains("$secrets.", StringComparison.OrdinalIgnoreCase) ?? false) ||
                    (src.Headers?.Values.Any(static v => v.Contains("$secrets.", StringComparison.OrdinalIgnoreCase)) ?? false);
                if (hasSecretAuth || string.IsNullOrWhiteSpace(src.Url))
                {
                    continue;
                }

                if (TryExtractHostFromRawUrlCandidate(src.Url, out string srcHost) &&
                    failedProbeHosts.TryGetValue(srcHost, out string? srcFailReason) &&
                    flaggedHosts.Add(srcHost))
                {
                    softSb.Append("- [Unreachable Probed Endpoint '")
                          .Append(srcHost)
                          .Append("']: ")
                          .Append(srcFailReason)
                          .AppendLine(". Switch to a reachable endpoint verified by probe_http, ask the user via finish_turn(kind='clarify'), or add immediate fallback data.");
                }
            }

            if (!string.IsNullOrWhiteSpace(logicJs))
            {
                foreach (System.Text.RegularExpressions.Match urlMatch in LogicUrlHostRegex().Matches(logicJs))
                {
                    string jsHost = urlMatch.Groups[1].Value;
                    if (failedProbeHosts.TryGetValue(jsHost, out string? jsFailReason) &&
                        flaggedHosts.Add(jsHost))
                    {
                        softSb.Append("- [Unreachable Probed Endpoint in logic.js '")
                              .Append(jsHost)
                              .Append("']: ")
                              .Append(jsFailReason)
                              .AppendLine(". Prefer declarative \"sources\" with a reachable endpoint verified by probe_http, ask the user via finish_turn(kind='clarify'), or populate immediate cold-start fallback data so the widget never hangs.");
                    }
                }
            }
        }

        if (_networkSourceEngine is not null && manifest.Sources.Count > 0)
        {
            var defaultSettings = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (CustomWidgetSettingDefinition def in manifest.Settings)
            {
                if (!string.Equals(def.Type, "secret", StringComparison.OrdinalIgnoreCase))
                {
                    defaultSettings[def.Key] = def.DefaultValue ?? string.Empty;
                }
            }

            var initialState = new Dictionary<string, string>(manifest.State, StringComparer.Ordinal);
            var evalCtx = new CustomWidgetEvaluationContext
            {
                State = initialState,
                Settings = defaultSettings
            };

            int probedCount = 0;
            foreach (CustomWidgetSourceSpec src in manifest.Sources)
            {
                if (!string.Equals(src.Type, "httpJson", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(src.Type, "rss", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (probedCount >= 3)
                {
                    break;
                }
                probedCount++;

                bool hasSecretRef =
                    (src.Url?.Contains("$secrets.", StringComparison.OrdinalIgnoreCase) ?? false) ||
                    (src.Headers?.Values.Any(static v => v.Contains("$secrets.", StringComparison.OrdinalIgnoreCase)) ?? false);

                string resolvedUrl = CustomWidgetExpressionEvaluator.Interpolate(src.Url, evalCtx).Trim();
                Dictionary<string, string>? resolvedHeaders = null;
                if (src.Headers is { Count: > 0 })
                {
                    resolvedHeaders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    foreach ((string hk, string hv) in src.Headers)
                    {
                        string rv = CustomWidgetExpressionEvaluator.Interpolate(hv, evalCtx).Trim();
                        if (!string.IsNullOrWhiteSpace(hk) && !string.IsNullOrWhiteSpace(rv))
                        {
                            resolvedHeaders[hk.Trim()] = rv;
                        }
                    }
                }

                CustomWidgetNetworkProbeResult probe = await _networkSourceEngine.ProbeSourceAsync(
                    src,
                    resolvedUrl,
                    resolvedHeaders,
                    hasSecretRef,
                    ct).ConfigureAwait(false);
                if (!probe.IsHealthy)
                {
                    StringBuilder target = probe.IsBlockingConfigError ? blockingSb : softSb;
                    target.Append("- [Live Network Probe Failed for source '")
                          .Append(src.Id ?? src.Type)
                          .Append("']: ")
                          .AppendLine(probe.SummaryDiagnosis);
                    if (!string.IsNullOrWhiteSpace(probe.JsonSkeleton))
                    {
                        target.AppendLine("  Actual Live JSON Response Skeleton (untrusted external sample — structure only):");
                        target.AppendLine("  <external_api_sample>");
                        target.Append("  ").AppendLine(probe.JsonSkeleton.Replace("</external_api_sample>", "[/external_api_sample]", StringComparison.OrdinalIgnoreCase));
                        target.AppendLine("  </external_api_sample>");
                    }
                }
            }
        }

        return (
            blockingSb.Length > 0 ? blockingSb.ToString().TrimEnd() : null,
            softSb.Length > 0 ? softSb.ToString().TrimEnd() : null);
    }

    private static bool TryExtractHostFromRawUrlCandidate(string rawUrl, out string host)
    {
        host = string.Empty;
        if (Uri.TryCreate(rawUrl.Trim(), UriKind.Absolute, out Uri? parsed) &&
            !string.IsNullOrWhiteSpace(parsed.DnsSafeHost))
        {
            host = parsed.DnsSafeHost;
            return true;
        }

        var match = RawUrlHostRegex().Match(rawUrl);
        if (match.Success)
        {
            host = match.Groups[1].Value;
            return true;
        }

        return false;
    }

    [System.Text.RegularExpressions.GeneratedRegex(
        @"https?://((?:[a-zA-Z0-9-]+\.)+[a-zA-Z]{2,})(?:[:/]|['""`])",
        System.Text.RegularExpressions.RegexOptions.CultureInvariant | System.Text.RegularExpressions.RegexOptions.NonBacktracking,
        matchTimeoutMilliseconds: 250)]
    private static partial System.Text.RegularExpressions.Regex LogicUrlHostRegex();

    [System.Text.RegularExpressions.GeneratedRegex(
        @"https?://((?:[a-zA-Z0-9-]+\.)+[a-zA-Z]{2,})",
        System.Text.RegularExpressions.RegexOptions.CultureInvariant | System.Text.RegularExpressions.RegexOptions.NonBacktracking,
        matchTimeoutMilliseconds: 250)]
    private static partial System.Text.RegularExpressions.Regex RawUrlHostRegex();
}
