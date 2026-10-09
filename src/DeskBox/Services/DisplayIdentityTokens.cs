namespace DeskBox.Services;

/// <summary>
/// Normalized identity tokens for display-set comparisons (spec 5.6): stable
/// ids when usable, geometry fallback tokens when degenerate, so removal
/// grace / startup settling compare display SETS instead of v3 signatures.
/// </summary>
internal static class DisplayIdentityTokens
{
    public static string TokenFor(string? stableId, int width, int height)
    {
        if (!DisplayPlacementResolver.IsDegenerateIdentity(stableId))
        {
            return stableId!.Trim().ToUpperInvariant();
        }

        return $"geo:{width}x{height}";
    }

    public static string TokenFor(WidgetScreenInfo screen) =>
        TokenFor(screen.StableId, screen.Monitor.Width, screen.Monitor.Height);

    public static string TokenFor(Models.WidgetTopologyMonitorProfile monitor) =>
        TokenFor(monitor.StableId, monitor.MonitorWidth, monitor.MonitorHeight);

    public static HashSet<string> CurrentSet() =>
        new(
            WidgetScreenCatalog.Capture().Select(TokenFor),
            StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The persisted display set of the active topology profile, or an empty
    /// set when no profile is active yet.
    /// </summary>
    public static HashSet<string> ActiveProfileSet(Models.AppSettings settings)
    {
        string? activeKey = settings.ActiveWidgetTopologyKey;
        if (string.IsNullOrWhiteSpace(activeKey) ||
            !settings.WidgetTopologyLayouts.TryGetValue(activeKey, out var profile) ||
            profile.Monitors is null)
        {
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        return new HashSet<string>(
            profile.Monitors.Select(TokenFor),
            StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// True when <paramref name="subset"/> is missing at least one token from
    /// <paramref name="superset"/> and adds none (true subset = only removals).
    /// </summary>
    public static bool IsTrueSubset(
        IReadOnlyCollection<string> subset,
        HashSet<string> superset)
    {
        if (subset.Count >= superset.Count)
        {
            return false;
        }

        foreach (string token in subset)
        {
            if (!superset.Contains(token))
            {
                return false;
            }
        }

        return true;
    }
}
