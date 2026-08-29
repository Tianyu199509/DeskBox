using DeskBox.Services;

namespace DeskBox.Tests;

public sealed class DesktopShellStartupSafetyTests
{
    [Fact]
    public void StabilityTracker_RequiresConsecutiveMatchingDesktopHosts()
    {
        var tracker = new DesktopIconViewStabilityTracker(requiredStableSamples: 3);
        var firstHost = new IntPtr(101);
        var secondHost = new IntPtr(202);

        Assert.False(tracker.Observe(firstHost));
        Assert.False(tracker.Observe(firstHost));
        Assert.False(tracker.Observe(secondHost));
        Assert.False(tracker.Observe(secondHost));
        Assert.True(tracker.Observe(secondHost));
    }

    [Fact]
    public void StabilityTracker_MissingDesktopHostResetsProgress()
    {
        var tracker = new DesktopIconViewStabilityTracker(requiredStableSamples: 2);
        var desktopHost = new IntPtr(303);

        Assert.False(tracker.Observe(desktopHost));
        Assert.False(tracker.Observe(IntPtr.Zero));
        Assert.False(tracker.Observe(desktopHost));
        Assert.True(tracker.Observe(desktopHost));
    }

    [Fact]
    public void WidgetLayer_DoesNotForceExplorerToSpawnWorkerW()
    {
        string widgetLayer = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Services/WidgetLayerService.cs"));

        Assert.DoesNotContain("SpawnWorkerWMessage", widgetLayer, StringComparison.Ordinal);
        Assert.DoesNotContain("0x052C", widgetLayer, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            "Only use a SHELLDLL_DefView that Explorer has already created.",
            widgetLayer,
            StringComparison.Ordinal);
    }

    [Fact]
    public void StartupLaunch_DefersDesktopAttachmentWithoutBlockingWidgetRestore()
    {
        string app = File.ReadAllText(TestPaths.FromRepository("src/DeskBox/App.xaml.cs"));
        int beginDeferralIndex = app.IndexOf(
            "WidgetLayerService.BeginStartupDesktopLayerAttachmentDeferral();",
            StringComparison.Ordinal);
        int readinessTaskIndex = app.IndexOf(
            "WidgetLayerService.WaitForDesktopIconViewReadyAsync();",
            StringComparison.Ordinal);
        int startupConditionIndex = beginDeferralIndex < 0
            ? -1
            : app.LastIndexOf("if (IsStartupMode)", beginDeferralIndex, StringComparison.Ordinal);
        int restoreIndex = app.IndexOf(
            "await WidgetManager.RestoreWidgetsAsync();",
            StringComparison.Ordinal);
        int deferredCompletionIndex = app.IndexOf(
            "_ = CompleteStartupDesktopLayerInitializationAsync(",
            StringComparison.Ordinal);

        Assert.True(beginDeferralIndex >= 0);
        Assert.InRange(beginDeferralIndex - startupConditionIndex, 1, 200);
        Assert.True(readinessTaskIndex > beginDeferralIndex);
        Assert.DoesNotContain(
            "await WidgetLayerService.WaitForDesktopIconViewReadyAsync();",
            app,
            StringComparison.Ordinal);
        Assert.True(restoreIndex > readinessTaskIndex);
        Assert.True(deferredCompletionIndex > restoreIndex);
    }
}
