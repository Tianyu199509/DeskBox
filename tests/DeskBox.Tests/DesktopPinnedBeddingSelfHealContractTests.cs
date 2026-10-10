namespace DeskBox.Tests;

public sealed class DesktopPinnedBeddingSelfHealContractTests
{
    [Fact]
    public void Watchdog_RequiresConsecutiveConfirmationsBeforeRepairing()
    {
        string manager = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Services/WidgetManager.ZOrder.cs"));
        string verify = SliceMethod(
            manager,
            "private void VerifyDesktopPinnedBedding",
            "private void RebedDesktopPinnedWidgetGroup");

        Assert.Contains(
            "BeddingLeakConfirmationPolicy.Observe",
            verify,
            StringComparison.Ordinal);
        Assert.Contains(
            "RequiredBeddingLeakConfirmations",
            verify,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Watchdog_SkipsTransientSessionStates()
    {
        string manager = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Services/WidgetManager.ZOrder.cs"));
        string verify = SliceMethod(
            manager,
            "private void VerifyDesktopPinnedBedding",
            "private void RebedDesktopPinnedWidgetGroup");

        // Raised sessions, layer toggles, interactions, and live expanded
        // leases own the Z-order at that moment; the watchdog must observe
        // and hold, not repair underneath them.
        Assert.Contains("_widgetsRaisedFromTray", verify, StringComparison.Ordinal);
        Assert.Contains("_isTogglingWidgetsDesktopLayer", verify, StringComparison.Ordinal);
        Assert.Contains("_sessionManager.IsInteractionActive", verify, StringComparison.Ordinal);
        Assert.Contains("HasActiveExpandedWidgetLayerLease()", verify, StringComparison.Ordinal);
    }

    [Fact]
    public void Repair_ReusesGroupRestorePrimitive_WithoutBlindBottomFlattening()
    {
        string manager = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Services/WidgetManager.ZOrder.cs"));
        string rebed = SliceMethod(
            manager,
            "private void RebedDesktopPinnedWidgetGroup",
            "private void ResetBeddingLeakTracking");

        Assert.Contains(
            "RestoreGroupPreservingForeground",
            rebed,
            StringComparison.Ordinal);
        // The repair path must not issue raw bottom-flattening calls of its
        // own; all movement goes through the group restore primitive whose
        // per-window attach already short-circuits for bedded windows.
        Assert.DoesNotContain("SetWindowToBottom", rebed, StringComparison.Ordinal);
        Assert.DoesNotContain("MoveToDesktopBottom", rebed, StringComparison.Ordinal);
    }

    [Fact]
    public void FileOpenDispatch_QueuesABeddingRecheck()
    {
        string opening = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Controls/WidgetContents/FileSurfaceContent.Opening.cs"));
        string dispatch = SliceMethod(
            opening,
            "private void ClearOpenedItemSelectionAfterDispatch",
            "private void ClearOpenedItemSelection(");

        Assert.Contains(
            "QueueDesktopPinnedBeddingRecheck",
            dispatch,
            StringComparison.Ordinal);
        Assert.Contains("file-open-dispatched", dispatch, StringComparison.Ordinal);
    }

    [Fact]
    public void DisplayTopologyRestore_QueuesABeddingRecheck()
    {
        string manager = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Services/WidgetManager.cs"));
        string restore = SliceMethod(
            manager,
            "public async Task<bool> RestoreWidgetPositionsAsync",
            "private static bool HasUsableWorkArea");

        Assert.Contains(
            "QueueDesktopPinnedBeddingRecheck",
            restore,
            StringComparison.Ordinal);
        Assert.Contains("display-topology-restored", restore, StringComparison.Ordinal);
    }

    [Fact]
    public void AttachPath_ShortCircuitsForAlreadyBeddedWindows()
    {
        string layerService = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Services/WidgetLayerService.cs"));
        string attach = SliceMethod(
            layerService,
            "private static bool TryAttachToDesktopIconLayer",
            "private static void DetachFromDesktopIconLayerIfNeeded");

        Assert.Contains("IsWindowBeddedAtDesktopLayer(windowHandle)", attach, StringComparison.Ordinal);
        // The skip must not bypass a genuinely leaked window: it requires the
        // owner to already be attached AND the physical bedding walk to pass.
        Assert.Contains("ownerWasAlreadyAttached", attach, StringComparison.Ordinal);
    }

    private static string SliceMethod(string source, string startMarker, string endMarker)
    {
        int start = source.IndexOf(startMarker, StringComparison.Ordinal);
        int end = source.IndexOf(endMarker, start, StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start);
        return source[start..end];
    }
}
