namespace DeskBox.Services;

/// <summary>
/// Normalized identity tokens for display-set comparisons (spec 5.6): stable
/// ids when usable, geometry fallback tokens when degenerate, so removal
/// grace / startup settling compare display SETS instead of v3 signatures.
/// Tokens can REPEAT — two degenerate monitors with the same resolution map
/// to the same geo token — so the producers below keep duplicates and every
/// comparison must preserve counts (see <see cref="IsTrueSubset"/>), never
/// fold the tokens into a set.
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

    public static IReadOnlyList<string> CurrentSet() =>
        WidgetScreenCatalog.Capture().Select(TokenFor).ToList();

    /// <summary>
    /// The persisted display set of the active topology profile, duplicates
    /// preserved, or an empty list when no profile is active yet.
    /// </summary>
    public static IReadOnlyList<string> ActiveProfileSet(Models.AppSettings settings)
    {
        string? activeKey = settings.ActiveWidgetTopologyKey;
        if (string.IsNullOrWhiteSpace(activeKey) ||
            !settings.WidgetTopologyLayouts.TryGetValue(activeKey, out var profile) ||
            profile.Monitors is null)
        {
            return [];
        }

        return profile.Monitors.Select(TokenFor).ToList();
    }

    /// <summary>
    /// True when <paramref name="subset"/> is missing at least one token from
    /// <paramref name="superset"/> and adds none (true subset = only removals).
    /// Counting semantics: each token's occurrences are compared one by one,
    /// so one geo unit out of an identical degenerate pair counts as a
    /// removal while the pair itself does not.
    /// </summary>
    public static bool IsTrueSubset(
        IReadOnlyCollection<string> subset,
        IReadOnlyCollection<string> superset)
    {
        if (subset.Count >= superset.Count)
        {
            return false;
        }

        Dictionary<string, int> supersetCounts = new(StringComparer.OrdinalIgnoreCase);
        foreach (string token in superset)
        {
            supersetCounts[token] = supersetCounts.GetValueOrDefault(token) + 1;
        }

        foreach (var group in subset.GroupBy(token => token, StringComparer.OrdinalIgnoreCase))
        {
            if (supersetCounts.GetValueOrDefault(group.Key) < group.Count())
            {
                return false;
            }
        }

        return true;
    }
}
