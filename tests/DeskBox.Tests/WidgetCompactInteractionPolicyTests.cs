using DeskBox.Services;

namespace DeskBox.Tests;

public sealed class WidgetCompactInteractionPolicyTests
{
    [Theory]
    [InlineData(true, true, false, true)]
    [InlineData(true, false, true, true)]
    [InlineData(true, false, false, false)]
    [InlineData(false, true, true, false)]
    public void CanTrustPointerOwnership_UsesFreshRoutedEvidenceWhenNativeHitTestingLags(
        bool pointerInside,
        bool nativeRootCanReceivePointer,
        bool hasRecentRoutedEvidence,
        bool expected)
    {
        Assert.Equal(
            expected,
            WidgetCompactInteractionPolicy.CanTrustPointerOwnership(
                pointerInside,
                nativeRootCanReceivePointer,
                hasRecentRoutedEvidence));
    }

    [Theory]
    [InlineData(true, true, true, false, false, true)]
    [InlineData(false, true, true, false, false, false)]
    [InlineData(true, false, true, false, false, false)]
    [InlineData(true, true, false, false, false, false)]
    [InlineData(true, true, true, true, false, false)]
    [InlineData(true, true, true, false, true, false)]
    public void HoverSuppression_ReleasesOnlyForNewIntentOnSettledCapsule(
        bool establishesNewIntent,
        bool targetCollapsed,
        bool shellCollapsed,
        bool boundsTransitionActive,
        bool shellTransitionActive,
        bool expected)
    {
        Assert.Equal(
            expected,
            WidgetCompactInteractionPolicy
                .CanReleaseHoverSuppressionAfterRoutedPointerIntent(
                    establishesNewIntent,
                    targetCollapsed,
                    shellCollapsed,
                    boundsTransitionActive,
                    shellTransitionActive));
    }

    [Fact]
    public void RoutedPointerAuthorityLifetime_CoversLongestHoverDelayAndTwoRecoveryProbes()
    {
        Assert.Equal(
            WidgetCompactInteractionPolicy.InteractionRegionHoverDelayFloorMilliseconds + 240,
            WidgetCompactInteractionPolicy.ResolveRoutedPointerAuthorityLifetimeMilliseconds(
                configuredDelayMilliseconds: 180,
                recoveryProbeMilliseconds: 120));
        Assert.Equal(
            1240,
            WidgetCompactInteractionPolicy.ResolveRoutedPointerAuthorityLifetimeMilliseconds(
                configuredDelayMilliseconds: 1000,
                recoveryProbeMilliseconds: 120));
    }

    [Fact]
    public void SynchronizeForSmartEntry_ReplacesStaleRoutedPointerState()
    {
        WidgetCompactInteractionSnapshot stale = CollapsedSnapshot() with
        {
            IsCollapsed = false,
            IsPointerInside = true,
            IsExpansionZoneActive = true,
            IsPointerOverMoveHandle = true,
            IsPointerOverActions = true,
            SuppressHoverExpansion = true
        };

        WidgetCompactInteractionSnapshot synchronized =
            WidgetCompactInteractionPolicy.SynchronizeForSmartEntry(
                stale,
                isPointerPhysicallyInside: false);

        Assert.False(synchronized.IsPointerInside);
        Assert.False(synchronized.IsExpansionZoneActive);
        Assert.False(synchronized.IsPointerOverMoveHandle);
        Assert.False(synchronized.IsPointerOverActions);
        Assert.False(synchronized.SuppressHoverExpansion);
        Assert.True(WidgetCompactInteractionPolicy.CanAutoCollapse(
            WidgetCollapseBehavior.Smart,
            synchronized));
    }

    [Fact]
    public void SynchronizeForSmartEntry_PreservesRealPointerPresence()
    {
        WidgetCompactInteractionSnapshot synchronized =
            WidgetCompactInteractionPolicy.SynchronizeForSmartEntry(
                CollapsedSnapshot() with { IsCollapsed = false },
                isPointerPhysicallyInside: true);

        Assert.True(synchronized.IsPointerInside);
        Assert.False(WidgetCompactInteractionPolicy.CanAutoCollapse(
            WidgetCollapseBehavior.Smart,
            synchronized));
        Assert.True(WidgetCompactInteractionPolicy.ShouldRetryAutoCollapse(
            WidgetCollapseBehavior.Smart,
            synchronized));
    }

    [Fact]
    public void CanHoverExpand_OnlyAllowsUnblockedSmartContentHover()
    {
        WidgetCompactInteractionSnapshot snapshot = CollapsedSnapshot() with
        {
            IsPointerInside = true,
            IsExpansionZoneActive = true
        };

        Assert.True(WidgetCompactInteractionPolicy.CanHoverExpand(
            WidgetCollapseBehavior.Smart,
            snapshot));
        Assert.False(WidgetCompactInteractionPolicy.CanHoverExpand(
            WidgetCollapseBehavior.Click,
            snapshot));
        Assert.False(WidgetCompactInteractionPolicy.CanHoverExpand(
            WidgetCollapseBehavior.Smart,
            snapshot with { IsExpansionZoneActive = false }));
        Assert.False(WidgetCompactInteractionPolicy.CanHoverExpand(
            WidgetCollapseBehavior.Smart,
            snapshot with { IsPointerOverMoveHandle = true }));
        Assert.False(WidgetCompactInteractionPolicy.CanHoverExpand(
            WidgetCollapseBehavior.Smart,
            snapshot with { IsPointerOverActions = true }));
        Assert.False(WidgetCompactInteractionPolicy.CanHoverExpand(
            WidgetCollapseBehavior.Smart,
            snapshot with { SuppressHoverExpansion = true }));
    }

    [Fact]
    public void CanHoverExpand_InteractionRegionDwellAllowsHoverWithoutStealingActiveInput()
    {
        WidgetCompactInteractionSnapshot moveHandle = CollapsedSnapshot() with
        {
            IsPointerInside = true,
            IsPointerOverMoveHandle = true
        };
        WidgetCompactInteractionSnapshot actions = moveHandle with
        {
            IsPointerOverMoveHandle = false,
            IsPointerOverActions = true
        };

        Assert.False(WidgetCompactInteractionPolicy.CanHoverExpand(
            WidgetCollapseBehavior.Smart,
            moveHandle));
        Assert.False(WidgetCompactInteractionPolicy.CanHoverExpand(
            WidgetCollapseBehavior.Smart,
            moveHandle,
            allowInteractionRegionDwell: true));
        Assert.True(WidgetCompactInteractionPolicy.CanHoverExpand(
            WidgetCollapseBehavior.Smart,
            actions,
            allowInteractionRegionDwell: true));
        Assert.False(WidgetCompactInteractionPolicy.CanHoverExpand(
            WidgetCollapseBehavior.Smart,
            actions with { InteractionDepth = 1 },
            allowInteractionRegionDwell: true));
        Assert.False(WidgetCompactInteractionPolicy.CanHoverExpand(
            WidgetCollapseBehavior.Smart,
            actions with { SuppressHoverExpansion = true },
            allowInteractionRegionDwell: true));
    }

    [Theory]
    [InlineData(180, false, 180)]
    [InlineData(180, true, 620)]
    [InlineData(620, true, 620)]
    [InlineData(800, true, 800)]
    public void ResolveHoverExpandDelayMilliseconds_PreservesBodyDelayAndAddsControlDwellFloor(
        int configuredDelay,
        bool allowInteractionRegionDwell,
        int expected)
    {
        Assert.Equal(
            expected,
            WidgetCompactInteractionPolicy.ResolveHoverExpandDelayMilliseconds(
                configuredDelay,
                allowInteractionRegionDwell));
    }

    [Theory]
    [InlineData(false, "TopLeft", "TopLeft")]
    [InlineData(true, "Left", "Left")]
    [InlineData(true, "TopLeft", "Left")]
    [InlineData(true, "BottomLeft", "Left")]
    [InlineData(true, "Right", "Right")]
    [InlineData(true, "TopRight", "Right")]
    [InlineData(true, "BottomRight", "Right")]
    [InlineData(true, "Top", "")]
    [InlineData(true, "Bottom", "")]
    public void ResolveResizeDirection_MapsTheWholeCapsuleEndCapToHorizontalResize(
        bool compact,
        string direction,
        string expected)
    {
        Assert.Equal(
            expected,
            WidgetCompactInteractionPolicy.ResolveResizeDirection(
                compact,
                direction));
    }

    [Fact]
    public void CanResize_CapsuleAllowsOnlyHorizontalEndsWhenTransitionIsSettled()
    {
        Assert.True(WidgetCompactInteractionPolicy.CanResize(
            isCompactTransitionActive: false,
            isCompactBoundsStateActive: true,
            direction: "TopLeft"));
        Assert.False(WidgetCompactInteractionPolicy.CanResize(
            isCompactTransitionActive: false,
            isCompactBoundsStateActive: true,
            direction: "Top"));
        Assert.False(WidgetCompactInteractionPolicy.CanResize(
            isCompactTransitionActive: true,
            isCompactBoundsStateActive: true,
            direction: "Left"));
        Assert.True(WidgetCompactInteractionPolicy.CanResize(
            isCompactTransitionActive: false,
            isCompactBoundsStateActive: false,
            direction: "Top"));
    }

    [Fact]
    public void CanAutoCollapse_RequiresPointerAndInteractionToBeClear()
    {
        WidgetCompactInteractionSnapshot snapshot = CollapsedSnapshot() with
        {
            IsCollapsed = false
        };

        Assert.True(WidgetCompactInteractionPolicy.CanAutoCollapse(
            WidgetCollapseBehavior.Smart,
            snapshot));
        Assert.False(WidgetCompactInteractionPolicy.CanAutoCollapse(
            WidgetCollapseBehavior.Smart,
            snapshot with { IsPointerInside = true }));
        Assert.False(WidgetCompactInteractionPolicy.CanAutoCollapse(
            WidgetCollapseBehavior.Smart,
            snapshot with { InteractionDepth = 1 }));
        Assert.False(WidgetCompactInteractionPolicy.CanAutoCollapse(
            WidgetCollapseBehavior.Smart,
            snapshot with { HasBlockingSurface = true }));
        Assert.False(WidgetCompactInteractionPolicy.CanAutoCollapse(
            WidgetCollapseBehavior.Smart,
            snapshot with { IsDropInside = true }));
        Assert.False(WidgetCompactInteractionPolicy.CanAutoCollapse(
            WidgetCollapseBehavior.Smart,
            snapshot with { IsPinned = true }));
    }

    [Fact]
    public void ShouldRetryAutoCollapse_OnlyStopsForSettledPinnedOrNonSmartWidgets()
    {
        WidgetCompactInteractionSnapshot expanded = CollapsedSnapshot() with
        {
            IsCollapsed = false,
            IsPointerInside = true,
            InteractionDepth = 1
        };

        Assert.True(WidgetCompactInteractionPolicy.ShouldRetryAutoCollapse(
            WidgetCollapseBehavior.Smart,
            expanded));
        Assert.False(WidgetCompactInteractionPolicy.ShouldRetryAutoCollapse(
            WidgetCollapseBehavior.Smart,
            expanded with { IsCollapsed = true }));
        Assert.False(WidgetCompactInteractionPolicy.ShouldRetryAutoCollapse(
            WidgetCollapseBehavior.Smart,
            expanded with { IsPinned = true }));
        Assert.False(WidgetCompactInteractionPolicy.ShouldRetryAutoCollapse(
            WidgetCollapseBehavior.Click,
            expanded));
    }

    [Theory]
    [InlineData(true, false, false, "Glance")]
    [InlineData(false, false, false, "Peek")]
    [InlineData(false, true, false, "Pinned")]
    [InlineData(false, false, true, "Open")]
    public void ResolveViewState_MapsSmartInteractionToFourUserStates(
        bool collapsed,
        bool pinned,
        bool interacting,
        string expected)
    {
        WidgetCompactInteractionSnapshot snapshot = CollapsedSnapshot() with
        {
            IsCollapsed = collapsed,
            IsPinned = pinned,
            InteractionDepth = interacting ? 1 : 0
        };

        Assert.Equal(
            Enum.Parse<WidgetCompactViewState>(expected),
            WidgetCompactInteractionPolicy.ResolveViewState(
                WidgetCollapseBehavior.Smart,
                snapshot));
    }

    [Fact]
    public void ResolveViewState_ClickModeUsesOpenState()
    {
        WidgetCompactInteractionSnapshot snapshot = CollapsedSnapshot() with
        {
            IsCollapsed = false
        };

        Assert.Equal(
            WidgetCompactViewState.Open,
            WidgetCompactInteractionPolicy.ResolveViewState(
                WidgetCollapseBehavior.Click,
                snapshot));
    }

    [Theory]
    [InlineData(true, false, true, false, true, true)]
    [InlineData(true, false, false, true, true, true)]
    [InlineData(true, false, false, false, true, false)]
    [InlineData(true, false, true, true, false, false)]
    [InlineData(true, true, true, true, true, false)]
    [InlineData(false, false, true, true, true, false)]
    public void ShouldShowCompactActions_ShowsOnlyOnCollapsedClickCapsuleHoverOrFocus(
        bool isCollapsed,
        bool usesSmartBehavior,
        bool isPointerOverShell,
        bool isKeyboardFocused,
        bool hasActionButtons,
        bool expected)
    {
        Assert.Equal(
            expected,
            WidgetCompactInteractionPolicy.ShouldShowCompactActions(
                isCollapsed,
                usesSmartBehavior,
                isPointerOverShell,
                isKeyboardFocused,
                hasActionButtons));
    }

    [Theory]
    [InlineData(false, false, true, true, "Hidden")]
    [InlineData(true, false, false, false, "Hidden")]
    [InlineData(true, false, true, false, "ScrimAndGrip")]
    [InlineData(true, false, false, true, "ScrimAndGrip")]
    [InlineData(true, true, false, false, "Hidden")]
    [InlineData(true, true, true, false, "LockedCue")]
    public void ResolveLeftDragAffordanceStage_ShowsOnlyForLeftZoneHoverOrActiveDrag(
        bool isCollapsed,
        bool isPositionLocked,
        bool isPointerOverLeftDragZone,
        bool isActiveMoveDrag,
        string expected)
    {
        Assert.Equal(
            Enum.Parse<WidgetCompactDragAffordanceStage>(expected),
            WidgetCompactInteractionPolicy.ResolveLeftDragAffordanceStage(
                isCollapsed,
                isPositionLocked,
                isPointerOverLeftDragZone,
                isActiveMoveDrag));
    }

    [Theory]
    [InlineData(true, false, false, false, false, true)]
    [InlineData(false, false, false, false, false, false)]
    [InlineData(true, true, false, false, false, false)]
    [InlineData(true, false, true, false, false, false)]
    [InlineData(true, false, false, true, false, false)]
    [InlineData(true, false, false, false, true, false)]
    public void CanPromoteCompactBodyPressToDrag_AllowsClickModeMiddleBodyDragOnlyWhenUnlocked(
        bool isCollapsed,
        bool usesSmartBehavior,
        bool isPositionLocked,
        bool pressedMoveHandle,
        bool pressedReorderHandle,
        bool expected)
    {
        Assert.Equal(
            expected,
            WidgetCompactInteractionPolicy.CanPromoteCompactBodyPressToDrag(
                isCollapsed,
                usesSmartBehavior,
                isPositionLocked,
                pressedMoveHandle,
                pressedReorderHandle));
    }

    [Fact]
    public void ResolveResizeChrome_RemovesCapsuleVerticalBordersAndNarrowsHorizontalEnds()
    {
        WidgetCompactResizeChromeMetrics compactUnlocked =
            WidgetCompactInteractionPolicy.ResolveResizeChrome(
                isCompactBoundsStateActive: true,
                isSizeLocked: false,
                isCompactTransitionActive: false);
        Assert.Equal(new WidgetCompactResizeChromeMetrics(0, 0, 4, false, true), compactUnlocked);

        WidgetCompactResizeChromeMetrics compactSizeLocked =
            WidgetCompactInteractionPolicy.ResolveResizeChrome(
                isCompactBoundsStateActive: true,
                isSizeLocked: true,
                isCompactTransitionActive: false);
        Assert.Equal(new WidgetCompactResizeChromeMetrics(0, 0, 0, false, false), compactSizeLocked);

        WidgetCompactResizeChromeMetrics compactTransitioning =
            WidgetCompactInteractionPolicy.ResolveResizeChrome(
                isCompactBoundsStateActive: true,
                isSizeLocked: false,
                isCompactTransitionActive: true);
        Assert.Equal(new WidgetCompactResizeChromeMetrics(0, 0, 0, false, false), compactTransitioning);

        WidgetCompactResizeChromeMetrics expandedUnlocked =
            WidgetCompactInteractionPolicy.ResolveResizeChrome(
                isCompactBoundsStateActive: false,
                isSizeLocked: false,
                isCompactTransitionActive: false);
        Assert.Equal(new WidgetCompactResizeChromeMetrics(4, 8, 8, true, true), expandedUnlocked);

        WidgetCompactResizeChromeMetrics expandedLocked =
            WidgetCompactInteractionPolicy.ResolveResizeChrome(
                isCompactBoundsStateActive: false,
                isSizeLocked: true,
                isCompactTransitionActive: false);
        Assert.Equal(new WidgetCompactResizeChromeMetrics(4, 8, 8, false, false), expandedLocked);
    }

    [Fact]
    public void CompactShellLayoutContract_EliminatesActionOverlayPingPongAndUsesLeftDragScrim()
    {
        string widgetShellXaml = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Controls/WidgetShell.xaml"));
        string widgetShellCode = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Controls/WidgetShell.xaml.cs"));

        Assert.DoesNotContain("CompactActionRegionTrigger", widgetShellXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("CompactActionRegionHighlight", widgetShellXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("CompactLeftDragEdgeCue", widgetShellXaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"CompactActionHost\"", widgetShellXaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"CompactLeftDragAffordanceHost\"", widgetShellXaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"CompactLeftDragScrim\"", widgetShellXaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"CompactLeftDragGrip\"", widgetShellXaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"CompactLeftLockCueIcon\"", widgetShellXaml, StringComparison.Ordinal);
        Assert.Contains(
            "WidgetCompactInteractionPolicy.ShouldShowCompactActions(",
            widgetShellCode,
            StringComparison.Ordinal);
        Assert.Contains(
            "WidgetCompactInteractionPolicy.ResolveLeftDragAffordanceStage(",
            widgetShellCode,
            StringComparison.Ordinal);
        Assert.Contains(
            "PointerWheelChanged=\"CompactMoveHitRegion_PointerWheelChanged\"",
            widgetShellXaml,
            StringComparison.Ordinal);
        Assert.Contains("x:Name=\"CompactTextRollViewport\"", widgetShellXaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"CompactIdentityDimLayer\"", widgetShellXaml, StringComparison.Ordinal);
        Assert.Contains("PlayCompactGroupSwitchAnimation(", widgetShellCode, StringComparison.Ordinal);
        Assert.Contains("new BackEase", widgetShellCode, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true, false, false, 2, true)]
    [InlineData(true, false, false, 3, true)]
    [InlineData(false, false, false, 2, false)]
    [InlineData(true, true, false, 2, false)]
    [InlineData(true, false, true, 2, false)]
    [InlineData(true, false, false, 1, false)]
    public void CanAcceptCompactGroupWheel_RequiresSettledCollapsedMultiMemberGroup(
        bool isCollapsed,
        bool isCompactTransitionActive,
        bool isDragActive,
        int memberCount,
        bool expected)
    {
        Assert.Equal(
            expected,
            WidgetCompactInteractionPolicy.CanAcceptCompactGroupWheel(
                isCollapsed,
                isCompactTransitionActive,
                isDragActive,
                memberCount));
    }

    [Fact]
    public void TryConsumeCompactWheelStep_CommitsConsecutiveMouseDetentsAndDeduplicatesSameFrameBounce()
    {
        double accumulator = 0;
        DateTimeOffset lastObservedAt = default;
        DateTimeOffset lastCommittedAt = default;
        int lastDirection = 0;
        bool touchpadCommitted = false;
        DateTimeOffset t0 = DateTimeOffset.UtcNow;

        Assert.True(WidgetCompactInteractionPolicy.TryConsumeCompactWheelStep(
            ref accumulator, ref lastObservedAt, ref lastCommittedAt, ref lastDirection, ref touchpadCommitted,
            wheelDelta: -120, t0, out int dir1));
        Assert.Equal(1, dir1);

        // Same-direction duplicate within < 35ms is ignored.
        Assert.False(WidgetCompactInteractionPolicy.TryConsumeCompactWheelStep(
            ref accumulator, ref lastObservedAt, ref lastCommittedAt, ref lastDirection, ref touchpadCommitted,
            wheelDelta: -120, t0.AddMilliseconds(10), out _));

        // Rapid physical wheel clicks (>= 35ms apart) advance 1-for-1 without waiting 220ms.
        Assert.True(WidgetCompactInteractionPolicy.TryConsumeCompactWheelStep(
            ref accumulator, ref lastObservedAt, ref lastCommittedAt, ref lastDirection, ref touchpadCommitted,
            wheelDelta: -120, t0.AddMilliseconds(60), out int dir2));
        Assert.Equal(1, dir2);

        // Immediate direction reversal commits without delay.
        Assert.True(WidgetCompactInteractionPolicy.TryConsumeCompactWheelStep(
            ref accumulator, ref lastObservedAt, ref lastCommittedAt, ref lastDirection, ref touchpadCommitted,
            wheelDelta: 120, t0.AddMilliseconds(70), out int dir3));
        Assert.Equal(-1, dir3);
    }

    [Fact]
    public void TryConsumeCompactWheelStep_AccumulatesPrecisionTouchpadAndSuppressesInertiaBurst()
    {
        double accumulator = 0;
        DateTimeOffset lastObservedAt = default;
        DateTimeOffset lastCommittedAt = default;
        int lastDirection = 0;
        bool touchpadCommitted = false;
        DateTimeOffset t0 = DateTimeOffset.UtcNow;

        Assert.False(WidgetCompactInteractionPolicy.TryConsumeCompactWheelStep(
            ref accumulator, ref lastObservedAt, ref lastCommittedAt, ref lastDirection, ref touchpadCommitted,
            wheelDelta: -45, t0, out _));
        Assert.False(WidgetCompactInteractionPolicy.TryConsumeCompactWheelStep(
            ref accumulator, ref lastObservedAt, ref lastCommittedAt, ref lastDirection, ref touchpadCommitted,
            wheelDelta: -45, t0.AddMilliseconds(16), out _));
        Assert.True(WidgetCompactInteractionPolicy.TryConsumeCompactWheelStep(
            ref accumulator, ref lastObservedAt, ref lastCommittedAt, ref lastDirection, ref touchpadCommitted,
            wheelDelta: -45, t0.AddMilliseconds(32), out int dir1));
        Assert.Equal(1, dir1);

        // Inertia tail in the same touchpad flick does not spin extra pages.
        Assert.False(WidgetCompactInteractionPolicy.TryConsumeCompactWheelStep(
            ref accumulator, ref lastObservedAt, ref lastCommittedAt, ref lastDirection, ref touchpadCommitted,
            wheelDelta: -90, t0.AddMilliseconds(80), out _));
        Assert.False(WidgetCompactInteractionPolicy.TryConsumeCompactWheelStep(
            ref accumulator, ref lastObservedAt, ref lastCommittedAt, ref lastDirection, ref touchpadCommitted,
            wheelDelta: -60, t0.AddMilliseconds(120), out _));

        // After quiet period, next flick commits cleanly.
        Assert.True(WidgetCompactInteractionPolicy.TryConsumeCompactWheelStep(
            ref accumulator, ref lastObservedAt, ref lastCommittedAt, ref lastDirection, ref touchpadCommitted,
            wheelDelta: -130, t0.AddMilliseconds(320), out int dir2));
        Assert.Equal(1, dir2);
    }

    private static WidgetCompactInteractionSnapshot CollapsedSnapshot() => new(
        IsCollapsed: true,
        IsPinned: false,
        IsPointerInside: false,
        IsExpansionZoneActive: false,
        IsPointerOverMoveHandle: false,
        IsPointerOverActions: false,
        IsDropInside: false,
        IsBoundsInteractionActive: false,
        InteractionDepth: 0,
        IsDragging: false,
        IsResizing: false,
        HasBlockingSurface: false,
        SuppressHoverExpansion: false);
}
