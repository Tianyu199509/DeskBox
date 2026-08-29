namespace DeskBox.Tests;

public sealed class Windows10WidgetMotionContractTests
{
    [Fact]
    public void TitleDrag_CoalescesPointerBurstsAndDefersPersistenceUntilRelease()
    {
        string baseWindow = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Views/WidgetWindowBase.cs"));
        string interaction = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Views/WidgetWindowBase.Interaction.cs"));
        string bounds = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Views/WidgetWindowBase.Bounds.cs"));
        string coordinatedMove = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Services/WidgetManager.CoordinatedMove.cs"));
        string snapCalculator = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Services/WidgetSnapCalculator.cs"));
        string groupDrag = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Services/WidgetManager.GroupDragPerformance.cs"));

        Assert.Contains("_pendingTitleBarDragFrame", baseWindow, StringComparison.Ordinal);
        Assert.Contains("QueueTitleBarDragFrame(deltaX, deltaY);", interaction, StringComparison.Ordinal);
        Assert.Contains(
            "WidgetCompactAnimationCoordinator.Register(ApplyPendingTitleBarDragFrame)",
            interaction,
            StringComparison.Ordinal);
        Assert.Contains("FlushPendingTitleBarDragFrame();", interaction, StringComparison.Ordinal);
        Assert.Contains("updateConfig: false", interaction, StringComparison.Ordinal);
        Assert.Contains("_deferTitleBarDragConfigUpdates", baseWindow, StringComparison.Ordinal);
        Assert.Contains("_deferTitleBarDragConfigUpdates", bounds, StringComparison.Ordinal);
        Assert.Contains("if (!IsDragging &&", bounds, StringComparison.Ordinal);

        Assert.Contains("CoordinatedMoveTarget[] targets = session.Targets;", coordinatedMove, StringComparison.Ordinal);
        Assert.Contains(
            "new CoordinatedMoveTarget[entries.Length]",
            coordinatedMove,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            ".Select(entry => new CoordinatedMoveTarget(",
            coordinatedMove,
            StringComparison.Ordinal);
        Assert.Contains("ConsiderCandidate", snapCalculator, StringComparison.Ordinal);
        Assert.DoesNotContain("List<SnapCandidate>", snapCalculator, StringComparison.Ordinal);
        Assert.DoesNotContain(".OrderBy(", snapCalculator, StringComparison.Ordinal);
        Assert.Contains("_groupDragCandidates", groupDrag, StringComparison.Ordinal);
        Assert.Contains("EnsureWidgetGroupDragCandidateCache", groupDrag, StringComparison.Ordinal);
        Assert.DoesNotContain(".OrderBy(", groupDrag, StringComparison.Ordinal);
        Assert.DoesNotContain(".ToHashSet(", groupDrag, StringComparison.Ordinal);
    }

    [Fact]
    public void GroupDragPreview_CachesMergeBoundsAndInvalidatesOnWindowMoves()
    {
        string groupDrag = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Services/WidgetManager.GroupDragPerformance.cs"));
        string capsuleArrangement = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Services/WidgetManager.CapsuleArrangement.cs"));
        string coordinatedMove = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Services/WidgetManager.CoordinatedMove.cs"));

        // Per-frame hit-testing must not recompute merge bounds per candidate.
        Assert.Contains("_groupDragCandidateBounds", groupDrag, StringComparison.Ordinal);
        Assert.Contains(
            "RefreshWidgetGroupDragCandidateBoundsCacheIfStale",
            groupDrag,
            StringComparison.Ordinal);
        // Every manager path that physically moves windows must invalidate
        // the cached bounds (capsule-bar previews/applies, topology restore,
        // coordinated moves).
        Assert.Contains("NoteWidgetWindowsMoved();", capsuleArrangement, StringComparison.Ordinal);
        Assert.Contains("NoteWidgetWindowsMoved();", coordinatedMove, StringComparison.Ordinal);
    }

    [Fact]
    public void InteractiveResize_CommitsThroughFrameCoordinatorWithoutTimerOrBursts()
    {
        string baseWindow = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Views/WidgetWindowBase.cs"));
        string bounds = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Views/WidgetWindowBase.Bounds.cs"));
        string resizeGuides = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Services/ResizeGuideOverlayService.cs"));
        string contentWindow = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Views/ContentWidgetWindow.xaml.cs"));

        Assert.Contains(
            "WidgetCompactAnimationCoordinator.Register(ApplyPendingInteractiveResizeBounds)",
            baseWindow,
            StringComparison.Ordinal);
        Assert.Contains("_pendingInteractiveResizeBounds", baseWindow, StringComparison.Ordinal);
        Assert.Contains("_deferInteractiveResizeConfigUpdates", baseWindow, StringComparison.Ordinal);
        Assert.Contains("_deferInteractiveResizeConfigUpdates", bounds, StringComparison.Ordinal);
        // The legacy 8ms burst commits (up to ~125Hz of full XAML re-layout) must
        // stay gone on every OS version.
        Assert.DoesNotContain("_interactiveResizeCommitQueued", baseWindow, StringComparison.Ordinal);
        Assert.DoesNotContain("DispatcherQueuePriority.High", baseWindow, StringComparison.Ordinal);
        Assert.DoesNotContain("TotalMilliseconds >= 8", baseWindow, StringComparison.Ordinal);
        Assert.DoesNotContain("Windows10InteractiveResize", baseWindow, StringComparison.Ordinal);
        Assert.DoesNotContain("_windows10InteractiveResizeTimer", baseWindow, StringComparison.Ordinal);
        Assert.Contains("!WindowsCompatibilityService.IsWindows11OrLater", bounds, StringComparison.Ordinal);
        Assert.Contains("SWP_NOCOPYBITS | Win32Helper.SWP_DEFERERASE", bounds, StringComparison.Ordinal);
        Assert.Contains("_resizeWorkAreaBounds", resizeGuides, StringComparison.Ordinal);
        Assert.Contains("static glow", resizeGuides, StringComparison.Ordinal);
        Assert.Contains("if (WindowsCompatibilityService.IsWindows11OrLater)", resizeGuides, StringComparison.Ordinal);
        Assert.DoesNotContain("child.PointerPressed += ResizeBorder_PointerPressed", contentWindow, StringComparison.Ordinal);
    }

    [Fact]
    public void CompactResize_CommitsFinalHwndWidthBeforeReleaseCanSettleOldBounds()
    {
        string interaction = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Views/WidgetWindowBase.Interaction.cs"));
        string collapse = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Views/WidgetWindowBase.Collapse.cs"));

        Assert.Contains(
            "if (IsSizeLocked || IsResizing || sender is not FrameworkElement element)",
            interaction,
            StringComparison.Ordinal);

        int commitStart = interaction.IndexOf(
            "private void CommitInteractiveResizeBounds()",
            StringComparison.Ordinal);
        int commitEnd = interaction.IndexOf(
            "protected void ResizeBorder_PointerReleasedCore",
            commitStart,
            StringComparison.Ordinal);
        Assert.True(commitStart >= 0);
        Assert.True(commitEnd > commitStart);
        string commit = interaction[commitStart..commitEnd];

        int flush = commit.IndexOf(
            "FlushPendingInteractiveResizeBounds();",
            StringComparison.Ordinal);
        int capture = commit.IndexOf(
            "RectInt32 finalBounds = GetActualWindowBounds();",
            StringComparison.Ordinal);
        int stopResize = commit.IndexOf(
            "IsResizing = false;",
            StringComparison.Ordinal);
        int persist = commit.IndexOf(
            "PersistCompletedWidgetResize(finalBounds);",
            StringComparison.Ordinal);
        int resumeConfigSync = commit.IndexOf(
            "_deferInteractiveResizeConfigUpdates = false;",
            StringComparison.Ordinal);

        Assert.True(flush >= 0);
        Assert.True(capture > flush);
        Assert.True(stopResize > capture);
        Assert.True(persist > stopResize);
        Assert.True(resumeConfigSync > persist);
        Assert.Equal(
            2,
            CountOccurrences(
                interaction,
                "\n        CommitInteractiveResizeBounds();"));

        Assert.Contains(
            "_compactArrangementSizeOverride = new SizeInt32(",
            collapse,
            StringComparison.Ordinal);
        Assert.Contains(
            "[Resize] Compact width committed",
            collapse,
            StringComparison.Ordinal);
    }

    [Fact]
    public void CompactTransition_RetainsAnimatedBoundsAndSharedCapacity()
    {
        string collapse = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Views/WidgetWindowBase.Collapse.cs"));
        string coordinator = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Services/WidgetCompactAnimationCoordinator.cs"));

        Assert.DoesNotContain("_collapseAnimationUsesVisualOnlyBounds", collapse, StringComparison.Ordinal);
        Assert.Contains("MoveWindowWithoutPersisting(bounds, suppressRedraw: true);", collapse, StringComparison.Ordinal);
        Assert.Contains("WidgetCompactAnimationCoordinator.TryQueueBoundsMove", collapse, StringComparison.Ordinal);
        Assert.Contains("internal const int MaximumConcurrentBoundsTransitions = 4", coordinator, StringComparison.Ordinal);
        Assert.Contains("Win32Helper.BeginDeferWindowPos", coordinator, StringComparison.Ordinal);
        Assert.Contains("Win32Helper.DeferWindowPos", coordinator, StringComparison.Ordinal);
        Assert.Contains("Win32Helper.EndDeferWindowPos", coordinator, StringComparison.Ordinal);
        Assert.DoesNotContain("preposition", collapse, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Win10CompactVisuals_UseCompositionWithoutReplacingRealBoundsMotion()
    {
        string shell = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Controls/WidgetShell.xaml.cs"));
        string collapse = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Views/WidgetWindowBase.Collapse.cs"));

        Assert.Contains("StartCompactOpacityAnimation", shell, StringComparison.Ordinal);
        Assert.Contains("!WindowsCompatibilityService.IsWindows11OrLater", shell, StringComparison.Ordinal);
        Assert.Contains("ScalarKeyFrameAnimation", shell, StringComparison.Ordinal);
        Assert.Contains("MoveWindowWithoutPersisting(bounds, suppressRedraw: true);", collapse, StringComparison.Ordinal);
    }

    [Fact]
    public void Win10AnimationClocks_UsePresentAlignedFlushWithRefreshDerivedFallback()
    {
        string coordinator = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Services/WidgetCompactAnimationCoordinator.cs"));
        string trayDriver = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Services/WidgetTrayBatchAnimationDriver.cs"));
        string clockBoost = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Services/CompositorClockBoostCoordinator.cs"));
        string refreshPolicy = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Models/WidgetDisplayRefreshRatePolicy.cs"));

        // Primary Win10 clock: DwmFlush paces one coalesced tick per DWM
        // composition pass (native refresh rate, no fixed-interval beat).
        Assert.Contains("Win32Helper.TryDwmFlush", coordinator, StringComparison.Ordinal);
        Assert.Contains("DispatchWindows10FlushTick", coordinator, StringComparison.Ordinal);
        // Fallback clock: timer interval derived from the measured refresh rate.
        Assert.Contains("DispatcherQueueTimer", coordinator, StringComparison.Ordinal);
        Assert.Contains("ResolveFrameTickInterval", coordinator, StringComparison.Ordinal);
        Assert.DoesNotContain("TimeSpan.FromMilliseconds(15)", coordinator, StringComparison.Ordinal);
        Assert.Contains("ResolveFrameTickInterval(int refreshRateHz)", refreshPolicy, StringComparison.Ordinal);
        Assert.Contains("Win32Helper.DeferWindowPos", coordinator, StringComparison.Ordinal);
        Assert.Contains("DispatcherQueueTimer", trayDriver, StringComparison.Ordinal);
        Assert.Contains("MoveEntriesFrameCore", trayDriver, StringComparison.Ordinal);
        Assert.Contains("Win32Helper.DeferWindowPos", trayDriver, StringComparison.Ordinal);
        Assert.Contains("TrySetHighResolutionTimer", clockBoost, StringComparison.Ordinal);
    }

    [Fact]
    public void CapsuleAnimation_AdaptsFrameSkipFromMeasuredBudgetInsteadOfHardCap()
    {
        string collapse = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Views/WidgetWindowBase.Collapse.cs"));
        string frameSkipPolicy = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Services/WidgetCompactFrameSkipPolicy.cs"));

        Assert.Contains("WidgetCompactFrameSkipPolicy.ResolveSkip", collapse, StringComparison.Ordinal);
        Assert.Contains("RecordCollapseAnimationTickCadence", collapse, StringComparison.Ordinal);
        Assert.Contains("s_compactSessionFrameSkipLevel", collapse, StringComparison.Ordinal);
        Assert.DoesNotContain("(int)Math.Round(Math.Max(1, refreshRateHz) / 60.0)", collapse, StringComparison.Ordinal);
        Assert.Contains("ShouldEscalate", frameSkipPolicy, StringComparison.Ordinal);
        Assert.Contains("Escalate", frameSkipPolicy, StringComparison.Ordinal);
    }

    [Fact]
    public void InteractionBackdropSimplification_IsAdaptiveAndAlwaysRestored()
    {
        string backdrop = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Views/WidgetWindowBase.Backdrop.cs"));
        string interaction = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Views/WidgetWindowBase.Interaction.cs"));
        string collapse = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Views/WidgetWindowBase.Collapse.cs"));
        string trayAnimation = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Services/WidgetManager.TrayAnimation.cs"));
        string policy = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Services/InteractionBackdropSimplificationPolicy.cs"));
        string win32Helper = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Helpers/Win32Helper.cs"));

        // Frosted glass stays by default: simplification is gated on measured
        // frame-budget overruns or ResourceSaver, never unconditional.
        Assert.Contains("InteractionBackdropSimplificationPolicy.ShouldSimplify", backdrop, StringComparison.Ordinal);
        Assert.Contains("RecentFrameOverrunMask", backdrop, StringComparison.Ordinal);
        Assert.Contains("blurEnabled: false", backdrop, StringComparison.Ordinal);
        Assert.Contains("ResolveAccentState", win32Helper, StringComparison.Ordinal);
        Assert.Contains("MinimumRecentOverrunTicks", policy, StringComparison.Ordinal);
        // Every interaction start/end pair must restore the blurred accent.
        Assert.Contains("SimplifyBackdropForInteraction();", interaction, StringComparison.Ordinal);
        Assert.Contains("RestoreBackdropAfterInteraction();", interaction, StringComparison.Ordinal);
        Assert.Contains("RestoreBackdropAfterInteraction();", collapse, StringComparison.Ordinal);
        Assert.Contains("window.SimplifyBackdropForInteraction();", trayAnimation, StringComparison.Ordinal);
        Assert.Contains("RestoreInteractionBackdropsWhenIdleAsync", trayAnimation, StringComparison.Ordinal);
    }

    [Fact]
    public void TrayToggleQueue_WaitsForBatchCompletion_AndHotkeyRegistersAfterRestore()
    {
        string trayDriver = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Services/WidgetTrayBatchAnimationDriver.cs"));
        string widgetManager = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Services/WidgetManager.cs"));
        string trayAnimation = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Services/WidgetManager.TrayAnimation.cs"));
        string app = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/App.xaml.cs"));

        Assert.Contains("public Task WaitForIdleAsync()", trayDriver, StringComparison.Ordinal);
        Assert.Contains("idleCompletion?.TrySetResult();", trayDriver, StringComparison.Ordinal);
        Assert.Contains(
            "await _trayBatchAnimationDriver.WaitForIdleAsync();",
            widgetManager,
            StringComparison.Ordinal);
        Assert.Contains(
            "await _trayBatchAnimationDriver.WaitForIdleAsync();",
            trayAnimation,
            StringComparison.Ordinal);

        int restoreIndex = app.IndexOf(
            "await WidgetManager.RestoreWidgetsAsync();",
            StringComparison.Ordinal);
        int hotkeyIndex = app.IndexOf(
            "InitializeGlobalHotkeyService(localizationService);",
            StringComparison.Ordinal);
        Assert.True(restoreIndex >= 0);
        Assert.True(hotkeyIndex > restoreIndex);
    }

    private static int CountOccurrences(string source, string marker)
    {
        int count = 0;
        int index = 0;
        while ((index = source.IndexOf(marker, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += marker.Length;
        }

        return count;
    }
}
