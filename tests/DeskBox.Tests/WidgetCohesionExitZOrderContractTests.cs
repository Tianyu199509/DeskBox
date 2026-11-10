namespace DeskBox.Tests;

public sealed class WidgetCohesionExitZOrderContractTests
{
    [Fact]
    public void ExpandedLeaseRelease_RequestsCohesionExitNormalization()
    {
        string manager = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Services/WidgetManager.ZOrder.cs"));
        string release = SliceMethod(
            manager,
            "internal bool ReleaseExpandedWidgetLayer",
            "private bool HasActiveExpandedWidgetLayerLease");

        Assert.Contains(
            "QueueIdleWidgetZOrderNormalization(reason, cohesionExit: true)",
            release,
            StringComparison.Ordinal);
    }

    [Fact]
    public void CohesionExitNormalization_RunsEvenInDesktopPinnedModeWhileIdlePassesDoNot()
    {
        string manager = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Services/WidgetManager.ZOrder.cs"));

        string queue = SliceMethod(
            manager,
            "internal void QueueIdleWidgetZOrderNormalization",
            "// ── Interaction-leak watchdog");
        Assert.Contains(
            "if (!cohesionExit && WidgetLayerService.UsesDesktopPinnedMode())",
            queue,
            StringComparison.Ordinal);

        string normalize = SliceMethod(
            manager,
            "private bool NormalizeIdleWidgetZOrder",
            "private static IReadOnlyList<IDesktopWidgetWindow> GetWindowsInIdleHighestFirstOrder");
        int nonCohesionBail = normalize.IndexOf("if (!cohesionExit)", StringComparison.Ordinal);
        int peerApply = normalize.IndexOf("ApplyPeerOrderHighestToLowest", StringComparison.Ordinal);
        Assert.True(nonCohesionBail >= 0 && peerApply > nonCohesionBail,
            "only cohesion-exit passes may reorder resting peers");
        Assert.DoesNotContain("ShouldNormalizeIdlePeerOrder", manager, StringComparison.Ordinal);
        Assert.DoesNotContain("HasAnyShadowOverlapRisk", manager, StringComparison.Ordinal);
    }

    [Fact]
    public void CohesionExitNormalization_AppliesWithoutAnimationDeferLadder()
    {
        string manager = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Services/WidgetManager.ZOrder.cs"));
        string normalize = SliceMethod(
            manager,
            "private bool NormalizeIdleWidgetZOrder",
            "private static IReadOnlyList<IDesktopWidgetWindow> GetWindowsInIdleHighestFirstOrder");

        Assert.DoesNotContain("_idleNormalizeAnimationDefers", manager, StringComparison.Ordinal);
        Assert.DoesNotContain("MaxIdleNormalizeAnimationDefers", manager, StringComparison.Ordinal);
        Assert.Contains("GetWindowsInIdleHighestFirstOrder(candidates)", normalize, StringComparison.Ordinal);
    }

    [Fact]
    public void CohesionExitNormalization_RemainsPeerOnlyReordering()
    {
        // The unconditional exit must not smuggle in any global re-bedding:
        // collapse exit is still a peer-order repair inside the existing band.
        string manager = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Services/WidgetManager.ZOrder.cs"));
        string normalize = SliceMethod(
            manager,
            "private bool NormalizeIdleWidgetZOrder",
            "private static IReadOnlyList<IDesktopWidgetWindow> GetWindowsInIdleHighestFirstOrder");

        Assert.Contains("ApplyPeerOrderHighestToLowest", normalize, StringComparison.Ordinal);
        Assert.DoesNotContain("MoveToDesktopBottom", normalize, StringComparison.Ordinal);
        Assert.DoesNotContain("SetWindowToBottom", normalize, StringComparison.Ordinal);
    }

    private static string SliceMethod(string source, string startMarker, string endMarker)
    {
        int start = source.IndexOf(startMarker, StringComparison.Ordinal);
        int end = source.IndexOf(endMarker, start, StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start);
        return source[start..end];
    }
}
