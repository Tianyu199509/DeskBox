using DeskBox.Models;

namespace DeskBox.Services;

/// <summary>
/// Why the resolver picked a display. Surfaced for tests, seeding decisions,
/// and verbose logging; runtime callers mostly consume the display itself.
/// </summary>
public enum DisplayPlacementReason
{
    FollowPrimary,
    Home,
    Entry,
    Heuristic,
    Primary
}

public readonly record struct ResolvedDisplayPlacement(
    WidgetScreenInfo Display,
    DisplayPlacementReason Reason,
    bool IsFallback);

/// <summary>
/// The display reference a surface entry (widget config or profile entry)
/// carries about the monitor it was last placed on.
/// </summary>
public readonly record struct DisplayPlacementEntryReference(
    string? StableId,
    string? DeviceName,
    string? WorkAreaKey,
    bool? WasPrimary);

public readonly record struct DisplayPlacementIntent(
    WidgetScreenBindingMode Mode,
    string? HomeId);

/// <summary>
/// The single display-resolution policy for widget placement (spec 5.1).
/// Runtime resolution, profile mapping, and seeding all funnel through this
/// pure function so their priorities can never drift apart again.
///
/// Priority: FollowPrimary → Home (online) → entry identity (stable id only
/// when non-degenerate; legacy entries fall back to device name / work-area
/// key) → heuristic (was-primary → current primary; otherwise the non-primary
/// screen with the same dominant direction relative to the primary, then
/// closest size, then same device name) → primary.
/// </summary>
public static class DisplayPlacementResolver
{
    public static ResolvedDisplayPlacement Resolve(
        DisplayPlacementIntent intent,
        DisplayPlacementEntryReference entry,
        IReadOnlyList<WidgetScreenInfo> online)
    {
        if (online.Count == 0)
        {
            throw new ArgumentException("At least one online display is required.", nameof(online));
        }

        WidgetScreenInfo primary = PrimaryScreen(online);

        if (intent.Mode == WidgetScreenBindingMode.FollowPrimary)
        {
            return new ResolvedDisplayPlacement(primary, DisplayPlacementReason.FollowPrimary, IsFallback: false);
        }

        if (TryFindByIdentity(online, intent.HomeId) is { } home)
        {
            return new ResolvedDisplayPlacement(home, DisplayPlacementReason.Home, IsFallback: false);
        }

        bool hasHome = !string.IsNullOrWhiteSpace(intent.HomeId);
        if (ResolveEntryScreen(entry, online) is { } entryScreen)
        {
            return new ResolvedDisplayPlacement(entryScreen, DisplayPlacementReason.Entry, hasHome);
        }

        WidgetScreenInfo heuristic = ResolveHeuristicScreen(entry, online, primary);
        bool primaryByWasPrimaryHint = entry.WasPrimary == true && heuristic.IsPrimary;
        return new ResolvedDisplayPlacement(
            heuristic,
            heuristic.IsPrimary && !primaryByWasPrimaryHint
                ? DisplayPlacementReason.Primary
                : DisplayPlacementReason.Heuristic,
            hasHome);
    }

    /// <summary>
    /// Identity-only lookup (resolver step 3): which online display does this
    /// entry reference point at, or null when the referenced display is not
    /// online. Used by profile seeding to find a surface's source monitor.
    /// </summary>
    public static WidgetScreenInfo? ResolveEntryDisplay(
        DisplayPlacementEntryReference entry,
        IReadOnlyList<WidgetScreenInfo> online) => ResolveEntryScreen(entry, online);

    private static WidgetScreenInfo PrimaryScreen(IReadOnlyList<WidgetScreenInfo> online)
    {
        foreach (WidgetScreenInfo screen in online)
        {
            if (screen.IsPrimary)
            {
                return screen;
            }
        }

        return online[0];
    }

    private static WidgetScreenInfo? ResolveEntryScreen(
        DisplayPlacementEntryReference entry,
        IReadOnlyList<WidgetScreenInfo> online)
    {
        // A non-degenerate stable id is the only identity used for it: when
        // that monitor is offline we skip straight to the heuristic instead
        // of trying device names, because Windows renumbers \\.\DISPLAYn
        // around lock/sleep/mode switches and a name hit can name the wrong
        // physical screen (spec 5.1 step 3).
        if (!IsDegenerateIdentity(entry.StableId))
        {
            return TryFindByIdentity(online, entry.StableId);
        }

        // Legacy entries (no stable id yet) keep the exact-match chain.
        if (!string.IsNullOrWhiteSpace(entry.DeviceName))
        {
            foreach (WidgetScreenInfo screen in online)
            {
                if (string.Equals(screen.DeviceName, entry.DeviceName, StringComparison.OrdinalIgnoreCase))
                {
                    return screen;
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(entry.WorkAreaKey))
        {
            foreach (WidgetScreenInfo screen in online)
            {
                if (string.Equals(
                        WidgetPositioningService.CreateMonitorKey(screen.WorkArea),
                        entry.WorkAreaKey,
                        StringComparison.Ordinal))
                {
                    return screen;
                }
            }
        }

        return null;
    }

    private static WidgetScreenInfo ResolveHeuristicScreen(
        DisplayPlacementEntryReference entry,
        IReadOnlyList<WidgetScreenInfo> online,
        WidgetScreenInfo primary)
    {
        // A surface that lived on the primary display keeps following the
        // current primary (dock-only laptop etc.). WasPrimary is the only
        // runtime-available "was it primary" signal; profile-based callers
        // with a monitors snapshot pre-normalize it into this field.
        if (entry.WasPrimary == true)
        {
            return primary;
        }

        WidgetScreenInfo? best = null;
        double bestScore = double.PositiveInfinity;
        foreach (WidgetScreenInfo screen in online)
        {
            if (screen.IsPrimary)
            {
                continue;
            }

            double score = HeuristicScore(entry, screen, primary);
            if (score < bestScore)
            {
                bestScore = score;
                best = screen;
            }
        }

        return best ?? primary;
    }

    /// <summary>
    /// Step 4b ranking (lower is better): dominant direction relative to the
    /// primary wins first, then closest work-area extent, then a small
    /// tiebreak bonus for the same device name. Extents are compared in
    /// physical pixels on both sides because the saved work-area key carries
    /// no DPI; on equal-DPI sets this equals the DIP comparison from the
    /// spec.
    /// </summary>
    private static double HeuristicScore(
        DisplayPlacementEntryReference entry,
        WidgetScreenInfo candidate,
        WidgetScreenInfo primary)
    {
        const double DirectionWeight = 1_000_000_000;
        const double SizeWeight = 1;

        double score = 0;
        if (TryParseWorkAreaKey(entry.WorkAreaKey, out Windows.Graphics.RectInt32 savedArea))
        {
            int wanted = DominantDirection(primary, savedArea);
            int actual = DominantDirection(primary, candidate.Monitor);
            if (wanted != actual)
            {
                score += DirectionWeight;
            }

            score += SizeWeight * (Math.Abs(candidate.WorkArea.Width - savedArea.Width) +
                                   Math.Abs(candidate.WorkArea.Height - savedArea.Height));
        }
        else
        {
            // No saved geometry: any non-primary screen ranks equally by size
            // unknown; keep insertion order (first non-primary wins).
            score = 0;
        }

        if (!string.IsNullOrWhiteSpace(entry.DeviceName) &&
            string.Equals(candidate.DeviceName, entry.DeviceName, StringComparison.OrdinalIgnoreCase))
        {
            score -= 0.5;
        }

        return score;
    }

    /// <summary>
    /// Dominant axis direction of a rect relative to the primary monitor:
    /// ±100 for left/right on the horizontal axis, ±1 for up/down.
    /// </summary>
    private static int DominantDirection(
        WidgetScreenInfo primary,
        Windows.Graphics.RectInt32 area)
    {
        double dx = (area.X + area.Width / 2.0) - (primary.Monitor.X + primary.Monitor.Width / 2.0);
        double dy = (area.Y + area.Height / 2.0) - (primary.Monitor.Y + primary.Monitor.Height / 2.0);
        return Math.Abs(dx) >= Math.Abs(dy)
            ? (dx >= 0 ? 100 : -100)
            : (dy >= 0 ? 1 : -1);
    }

    private static bool TryParseWorkAreaKey(string? key, out Windows.Graphics.RectInt32 area) =>
        WidgetPositioningService.TryParseMonitorKeyForResolver(key, out area);

    internal static bool IsDegenerateIdentity(string? stableId)
    {
        if (string.IsNullOrWhiteSpace(stableId))
        {
            return true;
        }

        string trimmed = stableId.Trim();
        return trimmed.Equals("unknown-display", StringComparison.OrdinalIgnoreCase) ||
               trimmed.StartsWith(@"\\.\DISPLAY", StringComparison.OrdinalIgnoreCase);
    }

    private static WidgetScreenInfo? TryFindByIdentity(
        IReadOnlyList<WidgetScreenInfo> online,
        string? stableId)
    {
        if (IsDegenerateIdentity(stableId))
        {
            return null;
        }

        string normalized = stableId!.Trim();
        foreach (WidgetScreenInfo screen in online)
        {
            if (string.Equals(screen.StableId.Trim(), normalized, StringComparison.OrdinalIgnoreCase))
            {
                return screen;
            }
        }

        return null;
    }
}
