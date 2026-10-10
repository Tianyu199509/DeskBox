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
    public void CohesionExitNormalization_BypassesTheDropShadowPolicy()
    {
        string manager = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Services/WidgetManager.ZOrder.cs"));

        string queue = SliceMethod(
            manager,
            "internal void QueueIdleWidgetZOrderNormalization",
            "private long TrackTemporarilyRaisedWidgets");
        Assert.Contains("!cohesionExit", queue, StringComparison.Ordinal);

        string normalize = SliceMethod(
            manager,
            "private bool NormalizeIdleWidgetZOrder",
            "private static IReadOnlyList<IDesktopWidgetWindow> GetWindowsInIdleHighestFirstOrder");
        // The shadow gate stays conditional so an idle request is still
        // dropped when the system disables window drop shadows.
        Assert.Contains(
            "(!cohesionExit &&\r\n                !IdleWidgetZOrderPolicy.ShouldNormalizeIdlePeerOrder(dropShadowEnabled))",
            normalize.ReplaceLineEndings("\r\n"),
            StringComparison.Ordinal);
    }

    [Fact]
    public void CohesionExitNormalization_NeverAbandonsOnAnimationDefers()
    {
        string manager = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Services/WidgetManager.ZOrder.cs"));
        string normalize = SliceMethod(
            manager,
            "private bool NormalizeIdleWidgetZOrder",
            "private static IReadOnlyList<IDesktopWidgetWindow> GetWindowsInIdleHighestFirstOrder")
            .ReplaceLineEndings("\r\n");

        // The defer/abandon ladder only applies to idle requests.
        Assert.Contains(
            "if (!cohesionExit &&\r\n            candidates.Any(window => window.IsBoundsTransitionActive))",
            normalize,
            StringComparison.Ordinal);
        // A cohesion exit that meets an in-flight animation still applies the
        // order (sorted on resting/target bounds) instead of being dropped.
        Assert.Contains(
            "cohesionExit && candidates.Any(window => window.IsBoundsTransitionActive)",
            normalize,
            StringComparison.Ordinal);
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
