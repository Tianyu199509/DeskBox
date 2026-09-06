namespace DeskBox.Services;

internal enum WidgetCompactViewState
{
    Glance,
    Peek,
    Open,
    Pinned
}

internal readonly record struct WidgetCompactInteractionSnapshot(
    bool IsCollapsed,
    bool IsPinned,
    bool IsPointerInside,
    bool IsExpansionZoneActive,
    bool IsPointerOverMoveHandle,
    bool IsPointerOverActions,
    bool IsDropInside,
    bool IsBoundsInteractionActive,
    int InteractionDepth,
    bool IsDragging,
    bool IsResizing,
    bool HasBlockingSurface,
    bool SuppressHoverExpansion)
{
    public bool HasActiveInteraction =>
        IsDropInside ||
        IsBoundsInteractionActive ||
        InteractionDepth > 0 ||
        IsDragging ||
        IsResizing ||
        HasBlockingSurface;
}

internal static class WidgetCompactInteractionPolicy
{
    internal const int InteractionRegionHoverDelayFloorMilliseconds = 620;
    internal const int RoutedPointerAuthoritySafetyProbeCount = 2;

    public static WidgetCompactInteractionSnapshot SynchronizeForSmartEntry(
        WidgetCompactInteractionSnapshot snapshot,
        bool isPointerPhysicallyInside)
    {
        return snapshot with
        {
            // Routed pointer events belong to the previous expanded layout and
            // can be left unmatched while widgets are created, moved, resized,
            // or covered by a flyout. The native cursor position is the only
            // authoritative whole-window state when Smart mode is entered.
            IsPointerInside = isPointerPhysicallyInside,
            IsExpansionZoneActive = false,
            IsPointerOverMoveHandle = false,
            IsPointerOverActions = false,
            SuppressHoverExpansion = false
        };
    }

    public static bool CanHoverExpand(
        WidgetCollapseBehavior behavior,
        WidgetCompactInteractionSnapshot snapshot,
        bool allowInteractionRegionDwell = false)
    {
        // The left identity strip is a move/drag affordance. Keep it excluded
        // even when the caller allows the longer interaction-region dwell used
        // by trailing actions; only the middle body may trigger expansion.
        bool hasEligibleHoverIntent = snapshot.IsPointerInside &&
            !snapshot.IsPointerOverMoveHandle &&
            (allowInteractionRegionDwell ||
                (snapshot.IsExpansionZoneActive && !snapshot.IsPointerOverActions));

        return behavior == WidgetCollapseBehavior.Smart &&
            snapshot.IsCollapsed &&
            snapshot.IsPointerInside &&
            hasEligibleHoverIntent &&
            !snapshot.IsDropInside &&
            !snapshot.HasActiveInteraction &&
            !snapshot.SuppressHoverExpansion;
    }

    public static int ResolveHoverExpandDelayMilliseconds(
        int configuredDelayMilliseconds,
        bool allowInteractionRegionDwell)
    {
        return allowInteractionRegionDwell
            ? Math.Max(
                configuredDelayMilliseconds,
                InteractionRegionHoverDelayFloorMilliseconds)
            : configuredDelayMilliseconds;
    }

    public static int ResolveRoutedPointerAuthorityLifetimeMilliseconds(
        int configuredDelayMilliseconds,
        int recoveryProbeMilliseconds)
    {
        int longestHoverDelay = ResolveHoverExpandDelayMilliseconds(
            configuredDelayMilliseconds,
            allowInteractionRegionDwell: true);
        return longestHoverDelay +
            (Math.Max(0, recoveryProbeMilliseconds) *
            RoutedPointerAuthoritySafetyProbeCount);
    }

    public static string ResolveResizeDirection(
        bool isCompactBoundsStateActive,
        string? direction)
    {
        string resolved = direction ?? string.Empty;
        if (!isCompactBoundsStateActive)
        {
            return resolved;
        }

        // In capsule mode the rounded end caps overlap the corner resize hit
        // targets. Treat every point on an end cap as horizontal resizing.
        if (resolved.Contains("Left", StringComparison.Ordinal))
        {
            return "Left";
        }

        return resolved.Contains("Right", StringComparison.Ordinal)
            ? "Right"
            : string.Empty;
    }

    public static bool CanResize(
        bool isCompactTransitionActive,
        bool isCompactBoundsStateActive,
        string? direction)
    {
        if (isCompactTransitionActive)
        {
            return false;
        }

        return !isCompactBoundsStateActive ||
            ResolveResizeDirection(
                isCompactBoundsStateActive,
                direction) is "Left" or "Right";
    }

    public static bool CanTrustPointerOwnership(
        bool isPointerPhysicallyInside,
        bool nativeRootCanReceivePointer,
        bool hasRecentRoutedPointerEvidence)
    {
        return isPointerPhysicallyInside &&
            (nativeRootCanReceivePointer || hasRecentRoutedPointerEvidence);
    }

    public static bool CanReleaseHoverSuppressionAfterRoutedPointerIntent(
        bool establishesNewPointerIntent,
        bool isTargetCollapsed,
        bool isShellCollapsed,
        bool isBoundsTransitionActive,
        bool isShellTransitionActive)
    {
        // PointerEntered/PointerMoved is authoritative only after the native
        // bounds and shell have both reached the settled capsule. Releasing the
        // guard any earlier lets the shrinking HWND expand again beneath a
        // stationary pointer.
        return establishesNewPointerIntent &&
            isTargetCollapsed &&
            isShellCollapsed &&
            !isBoundsTransitionActive &&
            !isShellTransitionActive;
    }

    public static bool CanAutoCollapse(
        WidgetCollapseBehavior behavior,
        WidgetCompactInteractionSnapshot snapshot)
    {
        return behavior == WidgetCollapseBehavior.Smart &&
            !snapshot.IsCollapsed &&
            !snapshot.IsPinned &&
            !snapshot.IsPointerInside &&
            !snapshot.HasActiveInteraction;
    }

    public static bool ShouldRetryAutoCollapse(
        WidgetCollapseBehavior behavior,
        WidgetCompactInteractionSnapshot snapshot)
    {
        // Pointer presence and active interactions are transient blockers. An
        // expanded Smart widget must keep probing until those blockers clear;
        // otherwise a missed PointerExited event can strand it open forever.
        return behavior == WidgetCollapseBehavior.Smart &&
            !snapshot.IsCollapsed &&
            !snapshot.IsPinned;
    }

    public static WidgetCompactViewState ResolveViewState(
        WidgetCollapseBehavior behavior,
        WidgetCompactInteractionSnapshot snapshot)
    {
        if (snapshot.IsCollapsed)
        {
            return WidgetCompactViewState.Glance;
        }

        if (snapshot.IsPinned)
        {
            return WidgetCompactViewState.Pinned;
        }

        if (behavior != WidgetCollapseBehavior.Smart || snapshot.HasActiveInteraction)
        {
            return WidgetCompactViewState.Open;
        }

        return WidgetCompactViewState.Peek;
    }
}
