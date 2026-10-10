namespace DeskBox.Tests;

public sealed class WidgetCompactTrayVisibilityContractTests
{
    [Fact]
    public void TrayHide_CollapsesOnlyTransientSmartExpansionToStableCapsuleBounds()
    {
        string source = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Views/WidgetWindowBase.Collapse.cs"));
        string method = ExtractSection(
            source,
            "protected void PrepareCompactHostForTrayHide()",
            "protected void NotifyCompactHostVisibilityChanged(bool isVisible)");

        Assert.Contains("UsesSmartCollapseBehavior()", method, StringComparison.Ordinal);
        Assert.Contains("_isSmartPinnedOpen", method, StringComparison.Ordinal);
        // Feedback 340: tray hide runs over freshly restored geometry and must
        // never fabricate a persisted capsule placement from the expanded
        // window corner — the collapse below derives bounds transiently.
        Assert.DoesNotContain(
            "EnsureCompactPlacementFromExpandedBounds",
            method,
            StringComparison.Ordinal);
        Assert.DoesNotContain("CaptureCompactPlacement", method, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "Config.CompactPlacement = null;",
            method,
            StringComparison.Ordinal);
        Assert.Contains("collapsed: true", method, StringComparison.Ordinal);
        Assert.Contains("persistManualState: false", method, StringComparison.Ordinal);
        Assert.Contains("animate: false", method, StringComparison.Ordinal);
        Assert.Contains("durationMs: 0", method, StringComparison.Ordinal);
        Assert.Contains("allowDuringInteraction: true", method, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("src/DeskBox/Views/ContentWidgetWindow.xaml.cs")]
    public void TrayHide_PreparesCapsuleBeforeCapturingAnimationPositionAndStopsHoverRecovery(
        string relativePath)
    {
        string source = File.ReadAllText(TestPaths.FromRepository(relativePath));
        string method = ExtractSection(
            source,
            "public bool PrepareTrayHideAnimation(bool persistVisibility = true)",
            "public void PlayPreparedTrayHideAnimation()");

        int prepareIndex = method.IndexOf(
            "PrepareCompactHostForTrayHide();",
            StringComparison.Ordinal);
        int hiddenIndex = method.IndexOf("Visible = false;", StringComparison.Ordinal);
        int notifyIndex = method.IndexOf(
            "NotifyCompactHostVisibilityChanged(false);",
            StringComparison.Ordinal);

        Assert.True(prepareIndex >= 0 && prepareIndex < hiddenIndex);
        Assert.True(hiddenIndex >= 0 && hiddenIndex < notifyIndex);
    }

    [Theory]
    [InlineData(
        "src/DeskBox/Views/ContentWidgetWindow.Commands.cs",
        "private void ContentWidgetShell_RightTapped",
        "private void ContentWidgetShell_TitleDoubleTapped")]
    public void TrayHide_SuppressesNativeAndRoutedPointerInputUntilHwndIsHidden(
        string relativePath,
        string rightTappedMarker,
        string nextMarker)
    {
        string source = File.ReadAllText(TestPaths.FromRepository(relativePath));
        string handler = ExtractSection(
            source,
            rightTappedMarker,
            nextMarker);

        Assert.Contains("IsTrayHideInputSuppressed", handler, StringComparison.Ordinal);
        Assert.Contains("IsHideAnimationRunning", handler, StringComparison.Ordinal);
        Assert.Contains("e.Handled = true;", handler, StringComparison.Ordinal);

        string inputSource = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Views/WidgetWindowBase.InputSuppression.cs"));
        Assert.Contains(
            "RootElement.IsHitTestVisible = false;",
            inputSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "Win32Helper.WS_EX_TRANSPARENT",
            inputSource,
            StringComparison.Ordinal);
    }

    [Fact]
    public void SmartEntry_ReconcilesNativePointerAfterFlyoutInteractionActuallyCloses()
    {
        string source = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Views/WidgetWindowBase.Collapse.cs"));
        string endInteraction = ExtractSection(
            source,
            "protected void EndCompactInteraction()",
            "private void QueueCompactInteractionReconcile()");
        string reconcile = ExtractSection(
            source,
            "private void QueueCompactInteractionReconcile()",
            "private void ReleaseCompactInteraction(string reason)");

        Assert.Contains("QueueCompactInteractionReconcile();", endInteraction, StringComparison.Ordinal);
        Assert.Contains("DispatcherQueue.TryEnqueue", reconcile, StringComparison.Ordinal);
        Assert.Contains("IsPointerPhysicallyInsideWindow()", reconcile, StringComparison.Ordinal);
        Assert.Contains("ApplyEffectiveCollapseBehavior(animate: true);", reconcile, StringComparison.Ordinal);
        Assert.Contains("ScheduleSmartCollapse(SmartCollapseProbeMs);", reconcile, StringComparison.Ordinal);
    }

    [Fact]
    public void OpenXamlPopups_BlockCapsuleCollapseAcrossHostedContent()
    {
        string baseSource = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Views/WidgetWindowBase.cs"));
        string popupGuard = ExtractSection(
            baseSource,
            "protected virtual bool HasBlockingFlyoutOpen()",
            "/// <summary>Allows hosts with custom title bars to update collapse actions.</summary>");
        string collapseSource = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Views/WidgetWindowBase.Collapse.cs"));

        Assert.Contains("RootElement.XamlRoot", popupGuard, StringComparison.Ordinal);
        Assert.Contains(
            "VisualTreeHelper.GetOpenPopupsForXamlRoot(xamlRoot).Count > 0",
            popupGuard,
            StringComparison.Ordinal);
        Assert.Contains(
            "HasBlockingSurface: HasBlockingFlyoutOpen()",
            collapseSource,
            StringComparison.Ordinal);
    }

    [Fact]
    public void HostVisibilityReset_ClearsShellHoverVisualsBeforeRebuildingNativePointerState()
    {
        string source = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Views/WidgetWindowBase.Collapse.cs"));
        string method = ExtractSection(
            source,
            "protected void NotifyCompactHostVisibilityChanged(bool isVisible)",
            "private void StartCompactHoverRecoveryProbe()");

        Assert.Equal(
            2,
            CountOccurrences(
                method,
                "WidgetShellControl.ResetTransientCompactPointerState();"));
        int visibleResetIndex = method.LastIndexOf(
            "WidgetShellControl.ResetTransientCompactPointerState();",
            StringComparison.Ordinal);
        int recoveryIndex = method.IndexOf(
            "StartCompactHoverRecoveryProbe();",
            visibleResetIndex,
            StringComparison.Ordinal);
        int synchronizeIndex = method.IndexOf(
            "SynchronizeCompactHoverFromCurrentCursor();",
            visibleResetIndex,
            StringComparison.Ordinal);
        Assert.True(visibleResetIndex >= 0 && visibleResetIndex < recoveryIndex);
        Assert.True(recoveryIndex >= 0 && recoveryIndex < synchronizeIndex);
    }

    /// <summary>
    /// A hidden widget can never satisfy the warm-up gate, and no in-loop
    /// delay changes that — only a native show can. The deferred run must
    /// stop instead of polling until the next lifecycle event (perf-log
    /// flood, 2026-10-03: hidden widgets logged CompactExpansionWarmupDeferred
    /// every ~320 ms and rotated the log within an hour).
    /// </summary>
    [Fact]
    public void WarmupRetry_StopsWhileHiddenAndReliesOnShowPathToRearm()
    {
        string source = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Views/WidgetWindowBase.Collapse.cs"));
        string warmupRun = ExtractSection(
            source,
            "private async Task RunCompactExpansionWarmupAsync(",
            "private static async Task<bool> WaitForCompactExpansionWarmupGateAsync(");
        string visibility = ExtractSection(
            source,
            "protected void NotifyCompactHostVisibilityChanged(bool isVisible)",
            "private void StartCompactHoverRecoveryProbe()");

        int stopIndex = warmupRun.IndexOf(
            "action=stop-until-shown",
            StringComparison.Ordinal);
        int retryIndex = warmupRun.IndexOf(
            "action=retry",
            StringComparison.Ordinal);
        Assert.Contains(
            "WidgetCompactWarmupPolicy.ShouldKeepWaiting(warmupSnapshot)",
            warmupRun,
            StringComparison.Ordinal);
        Assert.True(stopIndex >= 0 && stopIndex < retryIndex);

        // Deferral logging is deduplicated so a long-lived transient block
        // (cursor parked on the capsule) cannot flood the log either.
        Assert.Contains("lastDeferral", warmupRun, StringComparison.Ordinal);

        // The re-arm contract the stop relies on: hiding cancels the run,
        // and every show path funnels through this method, which queues an
        // urgent warm-up for the now-visible window.
        int hideCancelIndex = visibility.IndexOf(
            "CancelCompactExpansionWarmup();",
            StringComparison.Ordinal);
        int showRearmIndex = visibility.IndexOf(
            "QueueCompactExpansionWarmup(urgent: true);",
            StringComparison.Ordinal);
        Assert.True(hideCancelIndex >= 0);
        Assert.True(showRearmIndex > hideCancelIndex);
    }

    [Fact]
    public void StaleRoutedDragSession_DoesNotPermanentlyBlockHoverExpansion()
    {
        string shellSource = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Controls/WidgetShell.xaml.cs"));
        string shellRecovery = ExtractSection(
            shellSource,
            "internal bool TryClearStaleShellDragSessionAfterPointerRelease()",
            "private Brush CreateOpaqueOverlayButtonBackground()");
        string nativeExitRecovery = ExtractSection(
            shellSource,
            "internal bool TryEndShellDragSessionAfterNativePointerExit()",
            "private Brush CreateOpaqueOverlayButtonBackground()");
        string collapseSource = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Views/WidgetWindowBase.Collapse.cs"));
        string compactRecovery = ExtractSection(
            collapseSource,
            "private void ReconcileCompactDragStateAfterPointerRelease()",
            "private void WidgetShellControl_CompactDragLeft");
        string dragRecoveryProbe = ExtractSection(
            collapseSource,
            "private void ProbeCompactDragSessionState()",
            "protected bool UsesCompactExpansionGeometry()");
        string hoverProbe = ExtractSection(
            collapseSource,
            "private void RunCompactHoverRecoveryProbe(",
            "private void SynchronizeCompactHoverFromNativeCursor(");

        Assert.Contains("Win32Helper.IsAnyMouseButtonDown()", shellRecovery, StringComparison.Ordinal);
        Assert.Contains("_isShellDragActive = false;", shellRecovery, StringComparison.Ordinal);
        Assert.Contains("fileSurface.CompleteReleasedDragSession();", shellRecovery, StringComparison.Ordinal);
        Assert.DoesNotContain("fileSurface.ClearDragSessionVisualState();", shellRecovery, StringComparison.Ordinal);
        Assert.DoesNotContain("CompactDragLeft?.Invoke", shellRecovery, StringComparison.Ordinal);
        Assert.Contains(
            "EndShellDragSession(notifyCompact: true);",
            nativeExitRecovery,
            StringComparison.Ordinal);
        Assert.Contains("_isCompactDragInside = false;", compactRecovery, StringComparison.Ordinal);
        Assert.Contains("_dragExpandedFromCollapsed = false;", compactRecovery, StringComparison.Ordinal);
        Assert.Contains("CancelTimer(ref _collapseDragRestoreTimer);", compactRecovery, StringComparison.Ordinal);
        Assert.Contains("ReconcileCompactDragStateAfterPointerRelease();", hoverProbe, StringComparison.Ordinal);
        Assert.Contains("Win32Helper.IsAnyMouseButtonDown()", dragRecoveryProbe, StringComparison.Ordinal);
        Assert.Contains("!IsPointerPhysicallyInsideWindow()", dragRecoveryProbe, StringComparison.Ordinal);
        Assert.Contains(
            "TryEndShellDragSessionAfterNativePointerExit()",
            dragRecoveryProbe,
            StringComparison.Ordinal);
        Assert.Contains("ScheduleCompactDragSessionRecoveryProbe();", dragRecoveryProbe, StringComparison.Ordinal);
        Assert.Contains("ScheduleDragRestore(DragRestoreDelayMs);", dragRecoveryProbe, StringComparison.Ordinal);
    }

    [Fact]
    public void EnteringCompactBehavior_DerivesCapsuleBoundsBeforeStateTransition()
    {
        string source = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Views/WidgetWindowBase.Collapse.cs"));
        string method = ExtractSection(
            source,
            "private void ApplyEffectiveCollapseBehavior(bool animate)",
            "private void SynchronizeCompactPointerStateForSmartEntry()");

        // Feedback 340: the derivation is transient for widgets that have
        // never been collapsed; only the deliberate re-commit of an existing
        // placement captures. Either way the bounds are derived before the
        // state transition so the collapse lands on the window's region.
        int captureIndex = method.IndexOf(
            "DeriveCompactPlacementFromExpandedBounds(",
            StringComparison.Ordinal);
        int transitionIndex = method.IndexOf("SetCollapsedState(", StringComparison.Ordinal);
        Assert.True(captureIndex >= 0 && captureIndex < transitionIndex);
        Assert.Contains(
            "capturePlacement: Config.CompactPlacement is not null",
            method,
            StringComparison.Ordinal);
    }

    [Fact]
    public void FixedDirectionCollapse_DoesNotRepairOrRewriteExistingPlacement()
    {
        string source = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Views/WidgetWindowBase.Collapse.cs"));
        string method = ExtractSection(
            source,
            "private void SetCollapsedState(",
            "private RectInt32 ResolvePersistedExpandedHostBounds()");

        Assert.DoesNotContain("CompactPlacementNeedsDirectionRepair()", method, StringComparison.Ordinal);
        Assert.DoesNotContain("RefreshCompactPlacementFromExpandedBounds", method, StringComparison.Ordinal);
        Assert.Contains("EnsureCompactPlacementFromExpandedBounds();", method, StringComparison.Ordinal);
    }

    [Fact]
    public void DirectionOnlySettingsChange_DoesNotMoveTheCurrentWindow()
    {
        string source = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Views/WidgetWindowBase.Collapse.cs"));
        string method = ExtractSection(
            source,
            "private void ApplyCollapseSettingsChanged(bool appearanceOnly)",
            "private void ApplyEffectiveCollapseBehavior(bool animate)");

        int directionChangedIndex = method.IndexOf(
            "if (compactExpansionDirectionChanged)",
            StringComparison.Ordinal);
        int directionOnlyGuardIndex = method.IndexOf(
            "EffectiveCollapseBehavior == _lastEffectiveCollapseBehavior",
            directionChangedIndex,
            StringComparison.Ordinal);
        int returnIndex = method.IndexOf(
            "return;",
            directionOnlyGuardIndex,
            StringComparison.Ordinal);
        int behaviorApplyIndex = method.IndexOf(
            "ApplyEffectiveCollapseBehavior(animate: true);",
            StringComparison.Ordinal);

        Assert.True(directionChangedIndex >= 0);
        Assert.Contains(
            "ApplyCompactExpansionDirectionChange();",
            method,
            StringComparison.Ordinal);
        Assert.DoesNotContain("Config.CompactPlacement = null;", method, StringComparison.Ordinal);
        Assert.True(directionOnlyGuardIndex > directionChangedIndex);
        Assert.True(returnIndex > directionOnlyGuardIndex && returnIndex < behaviorApplyIndex);

        string directionChange = ExtractSection(
            source,
            "private void ApplyCompactExpansionDirectionChange()",
            "private void EnsureCompactPlacementFromExpandedBounds(");
        Assert.Contains("CancelPendingCompactExpansion();", directionChange, StringComparison.Ordinal);
        Assert.Contains("InvalidateCompactExpansionReadiness();", directionChange, StringComparison.Ordinal);
    }

    [Fact]
    public void ExpansionReadiness_NeverBlocksResolvedExpansionRequests()
    {
        string source = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Views/WidgetWindowBase.Collapse.cs"));
        string method = ExtractSection(
            source,
            "private void SetCollapsedState(",
            "private RectInt32 ResolvePersistedExpandedHostBounds()");

        int adaptiveResolveIndex = method.IndexOf(
            "ResolveRequestedCompactExpansion(compact)",
            StringComparison.Ordinal);
        int currentPlacementCaptureIndex = method.IndexOf(
            "CaptureCompactPlacement(GetCurrentWindowBounds(), persist: false);",
            StringComparison.Ordinal);
        int targetChangeIndex = method.IndexOf(
            "_targetCollapsed = collapsed;",
            StringComparison.Ordinal);

        // Adaptive resolution keeps fixed directions fixed and shrinks the
        // expanded size to the space actually available instead of blocking,
        // so a resolved expansion request always proceeds: no CanExpand=false
        // gate may return before the capsule state flips.
        Assert.True(currentPlacementCaptureIndex >= 0 && currentPlacementCaptureIndex < adaptiveResolveIndex);
        Assert.True(adaptiveResolveIndex >= 0 && adaptiveResolveIndex < targetChangeIndex);
        Assert.DoesNotContain("!readinessLayout.CanExpand", method, StringComparison.Ordinal);
        Assert.DoesNotContain("LogCompactExpansionBlocked(compact, readinessLayout", method, StringComparison.Ordinal);

        // Collapsing still resolves a strict full-size layout to pick the
        // transition anchor; only that degenerate path may keep its verbose
        // blocked diagnostic.
        Assert.Contains("LogCompactExpansionBlocked(to, layout)", method, StringComparison.Ordinal);
    }

    [Fact]
    public void RequestedCompactExpansion_AlwaysExpandsAndNeverShowsBlockedFeedback()
    {
        string source = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Views/WidgetWindowBase.Collapse.cs"));
        string resolver = ExtractSection(
            source,
            "private WidgetCompactExpansionLayout ResolveRequestedCompactExpansion(",
            "private void LogCompactExpansionBlocked(");

        // The strict pass requires the full requested size; the fallback pass
        // drops requireFullSize, which makes the calculator's canExpand
        // clause unconditionally true. The result therefore can never report
        // a blocked expansion, and the retired "not enough room" feedback
        // bubble must not resurface.
        Assert.Contains("requireFullSize: true", resolver, StringComparison.Ordinal);
        Assert.Contains(
            ": ResolveCompactExpansionLayout(compactBounds);",
            resolver,
            StringComparison.Ordinal);
        Assert.DoesNotContain("showFeedback", source, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "Widget.Compact.ExpansionSpaceInsufficient",
            source,
            StringComparison.Ordinal);
        Assert.DoesNotContain("compact-expansion-space", source, StringComparison.Ordinal);
    }

    private static string ExtractSection(string source, string startMarker, string endMarker)
    {
        int start = source.IndexOf(startMarker, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Missing start marker: {startMarker}");
        int end = source.IndexOf(endMarker, start, StringComparison.Ordinal);
        Assert.True(end > start, $"Missing end marker: {endMarker}");
        return source[start..end];
    }

    private static int CountOccurrences(string source, string value)
    {
        int count = 0;
        int index = 0;
        while ((index = source.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }

        return count;
    }
}
