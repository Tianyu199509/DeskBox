namespace DeskBox.Services;

internal enum WidgetCompactViewState
{
    Glance,
    Peek,
    Open,
    Pinned
}

internal enum WidgetCompactDragAffordanceStage
{
    Hidden,
    ScrimAndGrip,
    LockedCue
}

internal readonly record struct WidgetCompactResizeChromeMetrics(
    double TopBorderHeight,
    double BottomBorderHeight,
    double HorizontalBorderWidth,
    bool AllowVerticalHitTest,
    bool AllowHorizontalHitTest);

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

    public static WidgetCompactResizeChromeMetrics ResolveResizeChrome(
        bool isCompactBoundsStateActive,
        bool isSizeLocked,
        bool isCompactTransitionActive)
    {
        if (!isCompactBoundsStateActive)
        {
            bool allowExpandedResize = !isSizeLocked && !isCompactTransitionActive;
            return new WidgetCompactResizeChromeMetrics(
                TopBorderHeight: 4,
                BottomBorderHeight: 8,
                HorizontalBorderWidth: 8,
                AllowVerticalHitTest: allowExpandedResize,
                AllowHorizontalHitTest: allowExpandedResize);
        }

        // Capsules have a fixed 42 DIP height and never resize vertically.
        // Removing top/bottom hit-testing returns the full vertical span to
        // WidgetShell, while narrowing left/right to 4 DIPs prevents the
        // right resize border from colliding with CompactActionHost buttons.
        bool allowHorizontalResize = !isSizeLocked && !isCompactTransitionActive;
        return new WidgetCompactResizeChromeMetrics(
            TopBorderHeight: 0,
            BottomBorderHeight: 0,
            HorizontalBorderWidth: allowHorizontalResize ? 4 : 0,
            AllowVerticalHitTest: false,
            AllowHorizontalHitTest: allowHorizontalResize);
    }

    public static bool ShouldShowCompactActions(
        bool isCollapsed,
        bool usesSmartBehavior,
        bool isPointerOverShell,
        bool isKeyboardFocused,
        bool hasActionButtons)
    {
        return isCollapsed &&
            !usesSmartBehavior &&
            hasActionButtons &&
            (isPointerOverShell || isKeyboardFocused);
    }

    public static WidgetCompactDragAffordanceStage ResolveLeftDragAffordanceStage(
        bool isCollapsed,
        bool isPositionLocked,
        bool isPointerOverLeftDragZone,
        bool isActiveMoveDrag)
    {
        if (!isCollapsed)
        {
            return WidgetCompactDragAffordanceStage.Hidden;
        }

        if (isPositionLocked)
        {
            return isPointerOverLeftDragZone
                ? WidgetCompactDragAffordanceStage.LockedCue
                : WidgetCompactDragAffordanceStage.Hidden;
        }

        return isPointerOverLeftDragZone || isActiveMoveDrag
            ? WidgetCompactDragAffordanceStage.ScrimAndGrip
            : WidgetCompactDragAffordanceStage.Hidden;
    }

    public const double CompactWheelStep = 120;
    public const int CompactWheelDetentMinIntervalMilliseconds = 35;
    public const int CompactTouchpadQuietPeriodMilliseconds = 180;

    public static bool CanPromoteCompactBodyPressToDrag(
        bool isCollapsed,
        bool usesSmartBehavior,
        bool isPositionLocked,
        bool pressedMoveHandle,
        bool pressedReorderHandle)
    {
        return isCollapsed &&
            !usesSmartBehavior &&
            !isPositionLocked &&
            !pressedMoveHandle &&
            !pressedReorderHandle;
    }

    public static bool CanAcceptCompactGroupWheel(
        bool isCollapsed,
        bool isCompactTransitionActive,
        bool isDragActive,
        int memberCount)
    {
        return isCollapsed &&
            !isCompactTransitionActive &&
            !isDragActive &&
            memberCount >= 2;
    }

    public static bool TryConsumeCompactWheelStep(
        ref double accumulator,
        ref DateTimeOffset lastObservedAt,
        ref DateTimeOffset lastCommittedAt,
        ref int lastDirection,
        ref bool touchpadStepCommitted,
        double wheelDelta,
        DateTimeOffset observedAt,
        out int direction)
    {
        direction = 0;
        if (wheelDelta == 0 || !double.IsFinite(wheelDelta))
        {
            return false;
        }

        int inputDirection = wheelDelta < 0 ? 1 : -1;
        bool isDiscreteMouseWheel =
            Math.Abs(wheelDelta) >= CompactWheelStep &&
            Math.Abs(wheelDelta % CompactWheelStep) < 0.01;

        if (isDiscreteMouseWheel)
        {
            TimeSpan sinceLastCommit = observedAt - lastCommittedAt;
            bool isSameDirectionDuplicateBounce =
                lastCommittedAt != default &&
                inputDirection == lastDirection &&
                sinceLastCommit >= TimeSpan.Zero &&
                sinceLastCommit < TimeSpan.FromMilliseconds(CompactWheelDetentMinIntervalMilliseconds);

            lastObservedAt = observedAt;
            accumulator = 0;
            touchpadStepCommitted = false;
            if (isSameDirectionDuplicateBounce)
            {
                return false;
            }

            lastCommittedAt = observedAt;
            lastDirection = inputDirection;
            direction = inputDirection;
            return true;
        }

        TimeSpan sinceObserved = observedAt - lastObservedAt;
        bool startsNewTouchpadGesture =
            lastObservedAt == default ||
            inputDirection != lastDirection ||
            sinceObserved < TimeSpan.Zero ||
            sinceObserved >= TimeSpan.FromMilliseconds(CompactTouchpadQuietPeriodMilliseconds);
        lastObservedAt = observedAt;
        lastDirection = inputDirection;

        if (startsNewTouchpadGesture)
        {
            accumulator = 0;
            touchpadStepCommitted = false;
        }

        if (touchpadStepCommitted)
        {
            return false;
        }

        if (accumulator != 0 && Math.Sign(accumulator) != Math.Sign(wheelDelta))
        {
            accumulator = 0;
        }

        accumulator += wheelDelta;
        if (Math.Abs(accumulator) < CompactWheelStep)
        {
            return false;
        }

        direction = accumulator < 0 ? 1 : -1;
        accumulator = 0;
        touchpadStepCommitted = true;
        lastCommittedAt = observedAt;
        return true;
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
