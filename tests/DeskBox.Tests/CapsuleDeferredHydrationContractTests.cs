namespace DeskBox.Tests;

public sealed class CapsuleDeferredHydrationContractTests
{
    [Fact]
    public void DisplayTiming_NeverBlocksUiThreadOnEnumDisplaySettingsAndRetainsTimingsAcrossClear()
    {
        string collapseSource = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Views/WidgetWindowBase.Collapse.cs"));
        string startTransition = Slice(
            collapseSource,
            "private void StartBoundsTransition(",
            "private void CompleteBoundsTransitionAfterTimeout()");

        Assert.DoesNotContain(
            "Win32Helper.GetDisplayRefreshRateForWindow(",
            startTransition,
            StringComparison.Ordinal);
        Assert.Contains(
            "WidgetAnimationDisplayTiming.GetRefreshRateHz(HWnd)",
            startTransition,
            StringComparison.Ordinal);

        string displayTimingSource = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Services/WidgetAnimationDisplayTiming.cs"));
        Assert.DoesNotContain(
            "Win32Helper.GetAnimationDisplayTiming(",
            displayTimingSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "Win32Helper.TryGetCachedAnimationDisplayTiming(",
            displayTimingSource,
            StringComparison.Ordinal);

        string clearMethod = Slice(
            displayTimingSource,
            "internal static void Clear()",
            "internal static void InvalidateTopologyCache()");
        Assert.Contains("s_lastRefresh = 0;", clearMethod, StringComparison.Ordinal);
        Assert.DoesNotContain("s_timings.Clear()", clearMethod, StringComparison.Ordinal);
        Assert.DoesNotContain("Monitors.Clear()", clearMethod, StringComparison.Ordinal);
    }

    [Fact]
    public void QuiescenceTrim_AdvancesWorkingSetTrimEpochWithoutFiringMemoryCleanupEpoch()
    {
        string trimSource = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/App.QuiescenceWorkingSetTrim.cs"));

        Assert.Contains(
            "internal static long WorkingSetTrimEpoch => Interlocked.Read(ref s_workingSetTrimEpoch);",
            trimSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "WidgetAnimationDisplayTiming.WarmInBackground();",
            trimSource,
            StringComparison.Ordinal);

        int completeIndex = trimSource.IndexOf(
            "private long CompleteWorkingSetTrim(bool trimmed, string reason)",
            StringComparison.Ordinal);
        Assert.True(completeIndex >= 0);
        string completeSlice = trimSource[completeIndex..];
        Assert.Contains(
            "Interlocked.Increment(ref s_workingSetTrimEpoch);",
            completeSlice,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "MemoryCleanupEpochAdvanced",
            completeSlice,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ColdHibernatedOrTrimmedCapsule_UsesShellFirstDeferredHydrationAndStationaryReveal()
    {
        string collapseSource = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Views/WidgetWindowBase.Collapse.cs"));

        string warmReadySlice = Slice(
            collapseSource,
            "private bool IsWarmLiveContentExpansionReady =>",
            "protected bool IsWidgetCollapsedBoundsActive");
        Assert.Contains("IsCompactExpansionReady", warmReadySlice, StringComparison.Ordinal);
        Assert.Contains("WidgetShellControl.ShellContent is not null", warmReadySlice, StringComparison.Ordinal);
        Assert.Contains("_compactExpansionWorkingSetEpoch == App.WorkingSetTrimEpoch", warmReadySlice, StringComparison.Ordinal);

        string queueWarmup = Slice(
            collapseSource,
            "private void QueueCompactExpansionWarmup(",
            "private async Task RunCompactExpansionWarmupAsync(");
        int hibernatedGuard = queueWarmup.IndexOf(
            "if (IsCollapsedContentHibernated && !attachHibernated)",
            StringComparison.Ordinal);
        int workingSetGuard = queueWarmup.IndexOf(
            "_compactExpansionWorkingSetEpoch != App.WorkingSetTrimEpoch",
            StringComparison.Ordinal);
        int attachCall = queueWarmup.IndexOf(
            "EnsureCollapsedContentViewAttached()",
            StringComparison.Ordinal);
        Assert.True(hibernatedGuard >= 0);
        Assert.True(workingSetGuard > hibernatedGuard);
        Assert.True(attachCall > workingSetGuard);

        string setCollapsedState = Slice(
            collapseSource,
            "private void SetCollapsedState(",
            "private RectInt32 ResolvePersistedExpandedHostBounds()");
        Assert.Contains(
            "bool deferExpandedContentHydration =\r\n            willAnimateExpandMorph && !IsWarmLiveContentExpansionReady;",
            setCollapsedState.Replace("\n", "\r\n").Replace("\r\r\n", "\r\n"),
            StringComparison.Ordinal);
        Assert.Contains("if (deferExpandedContentHydration)", setCollapsedState, StringComparison.Ordinal);

        string completeTransition = Slice(
            collapseSource,
            "private void CompleteBoundsTransition(bool collapsed, long generation)",
            "private void ScheduleDeferredExpandedContentHydration(");
        Assert.Contains("keepContentDeferred: deferContentHydration", completeTransition, StringComparison.Ordinal);
        Assert.Contains("ScheduleDeferredExpandedContentHydration(generation, anchorsBottom);", completeTransition, StringComparison.Ordinal);

        string hydrateMethod = Slice(
            collapseSource,
            "private void HydrateDeferredExpandedContent(",
            "private void CancelDeferredExpandedContentHydration()");
        Assert.Contains("EnsureCollapsedContentViewAttached();", hydrateMethod, StringComparison.Ordinal);
        Assert.Contains("WidgetShellControl.RevealDeferredExpandedContent(", hydrateMethod, StringComparison.Ordinal);
        Assert.Contains("OnCompactBoundsTransitionActiveChanged(false);", hydrateMethod, StringComparison.Ordinal);
    }

    [Fact]
    public void WidgetShell_DefersLeafViewAttachmentDuringDeferredExpandMorph_AndAnimatesPostExpandReveal()
    {
        string shellSource = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Controls/WidgetShell.xaml.cs"));

        string setContent = Slice(
            shellSource,
            "public void SetContent(IWidgetContent content)",
            "internal bool HasPresentableContentFrame");
        Assert.Contains("bool deferCollapsedViewAttachment =", setContent, StringComparison.Ordinal);
        Assert.Contains("_isCompactDeferredContentPresentationActive", setContent, StringComparison.Ordinal);

        string prepare = Slice(
            shellSource,
            "public bool PrepareCompactTransition(",
            "public void SetCompactTransitionProgress(");
        Assert.Contains("bool deferContentPresentation = false", prepare, StringComparison.Ordinal);
        Assert.Contains("_isCompactDeferredContentPresentationActive = deferContentPresentation;", prepare, StringComparison.Ordinal);

        string applyChrome = Slice(
            shellSource,
            "private void ApplyChromeMode()",
            "if (_groupPresentation is not null)");
        Assert.Contains("CollapsedChromeLayer is not null && !_isCompactTransitionActive", applyChrome, StringComparison.Ordinal);
        Assert.Contains("_isCompactDeferredContentPresentationActive", applyChrome, StringComparison.Ordinal);

        string reveal = Slice(
            shellSource,
            "public void RevealDeferredExpandedContent(",
            "private void StartDeferredExpandedContentRevealAnimation(");
        Assert.Contains("contentVisual.Opacity = 0f;", reveal, StringComparison.Ordinal);
        Assert.Contains("ShellContentPresenter.Visibility = Visibility.Visible;", reveal, StringComparison.Ordinal);
        Assert.Contains("ShellContentPresenter.UpdateLayout();", reveal, StringComparison.Ordinal);
        Assert.Contains("StartDeferredExpandedContentRevealAnimation(anchorsBottom);", reveal, StringComparison.Ordinal);
    }

    private static string Slice(string source, string startMarker, string endMarker)
    {
        int start = source.IndexOf(startMarker, StringComparison.Ordinal);
        int end = source.IndexOf(endMarker, start, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Missing start marker: {startMarker}");
        Assert.True(end > start, $"Missing end marker: {endMarker}");
        return source[start..end];
    }
}
