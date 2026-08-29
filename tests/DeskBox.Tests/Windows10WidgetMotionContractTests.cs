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
    public void InteractiveResize_CoalescesWin10BurstsWithoutTimerOrDuplicatePressHandler()
    {
        string baseWindow = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Views/WidgetWindowBase.cs"));
        string bounds = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Views/WidgetWindowBase.Bounds.cs"));
        string resizeGuides = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Services/ResizeGuideOverlayService.cs"));
        string contentWindow = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Views/ContentWidgetWindow.xaml.cs"));

        Assert.Contains("if (WindowsCompatibilityService.IsWindows11OrLater)", baseWindow, StringComparison.Ordinal);
        Assert.Contains("DispatcherQueuePriority.High", baseWindow, StringComparison.Ordinal);
        Assert.Contains("_interactiveResizeCommitQueued", baseWindow, StringComparison.Ordinal);
        Assert.Contains("TotalMilliseconds >= 8", baseWindow, StringComparison.Ordinal);
        Assert.Contains("ApplyPendingInteractiveResizeBounds();", baseWindow, StringComparison.Ordinal);
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
    public void Win10AnimationClocks_UseHighResolutionDispatcherTicksAndRealHwndBatches()
    {
        string coordinator = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Services/WidgetCompactAnimationCoordinator.cs"));
        string trayDriver = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Services/WidgetTrayBatchAnimationDriver.cs"));
        string clockBoost = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Services/CompositorClockBoostCoordinator.cs"));

        Assert.Contains("DispatcherQueueTimer", coordinator, StringComparison.Ordinal);
        Assert.Contains("TimeSpan.FromMilliseconds(15)", coordinator, StringComparison.Ordinal);
        Assert.Contains("Win32Helper.DeferWindowPos", coordinator, StringComparison.Ordinal);
        Assert.Contains("DispatcherQueueTimer", trayDriver, StringComparison.Ordinal);
        Assert.Contains("MoveEntriesFrameCore", trayDriver, StringComparison.Ordinal);
        Assert.Contains("Win32Helper.DeferWindowPos", trayDriver, StringComparison.Ordinal);
        Assert.Contains("TrySetHighResolutionTimer", clockBoost, StringComparison.Ordinal);
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
}
