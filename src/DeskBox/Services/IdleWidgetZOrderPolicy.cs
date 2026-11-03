namespace DeskBox.Services;

internal readonly record struct IdleWidgetZOrderCandidate(
    IntPtr WindowHandle,
    string DisplayKey,
    double Top,
    double Left,
    string StableKey);

/// <summary>
/// Defines the canonical peer order used while widgets are visible but idle.
/// Lower rows are kept above upper rows so an upper widget's downward shadow
/// cannot darken the top edge of the widget below it.
/// </summary>
internal static class IdleWidgetZOrderPolicy
{
    public static IReadOnlyList<IdleWidgetZOrderCandidate> OrderHighestToLowest(
        IEnumerable<IdleWidgetZOrderCandidate> candidates)
    {
        return candidates
            .Where(candidate => candidate.WindowHandle != IntPtr.Zero)
            .GroupBy(candidate => candidate.WindowHandle)
            .Select(group => group.First())
            .OrderBy(candidate => candidate.DisplayKey, StringComparer.Ordinal)
            .ThenByDescending(candidate => candidate.Top)
            .ThenByDescending(candidate => candidate.Left)
            .ThenBy(candidate => candidate.StableKey, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// The idle peer order only exists to keep an upper widget's drop shadow
    /// off the widget below it. When Windows drop shadows are disabled the
    /// normalization has no visual purpose and every applied reorder is pure
    /// repaint churn.
    /// </summary>
    public static bool ShouldNormalizeIdlePeerOrder(bool windowDropShadowEnabled) =>
        windowDropShadowEnabled;

    /// <summary>
    /// Reports whether the HWND z-order already matches the requested peer
    /// order: no requested peer may sit above the requested highest handle,
    /// and walking down from it must encounter the requested peers in order.
    /// </summary>
    public static bool MatchesRequestedOrder(
        IReadOnlyList<IntPtr> requestedHighestToLowest,
        IReadOnlyList<IntPtr> peersObservedAboveHighest,
        IReadOnlyList<IntPtr> peersObservedFromHighestDown)
    {
        return peersObservedAboveHighest.Count == 0 &&
               peersObservedFromHighestDown.SequenceEqual(requestedHighestToLowest);
    }

    /// <summary>
    /// Default physical-pixel reach used to decide whether two idle widgets'
    /// DWM drop shadows can visually overlap a neighbor's body (generous up to
    /// 225% DPI so real overlaps are never skipped while separated widgets
    /// avoid redundant SetWindowPos peer reorders).
    /// </summary>
    public const double DefaultShadowOverlapReachPixels = 88.0;

    public static bool HasAnyShadowOverlapRisk(
        IEnumerable<Windows.Foundation.Rect> bounds,
        double shadowReachPixels = DefaultShadowOverlapReachPixels)
    {
        List<Windows.Foundation.Rect> list = bounds
            .Where(static rect => rect.Width > 0 && rect.Height > 0)
            .ToList();
        if (list.Count < 2)
        {
            return false;
        }

        double reach = Math.Max(0.0, shadowReachPixels);
        for (int i = 0; i < list.Count; i++)
        {
            Windows.Foundation.Rect first = list[i];
            for (int j = i + 1; j < list.Count; j++)
            {
                Windows.Foundation.Rect second = list[j];
                if (first.Left - reach < second.Right &&
                    second.Left - reach < first.Right &&
                    first.Top - reach < second.Bottom &&
                    second.Top - reach < first.Bottom)
                {
                    return true;
                }
            }
        }

        return false;
    }
}
