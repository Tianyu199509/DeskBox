namespace DeskBox.Tests;

public sealed class WidgetZOrderRestoreContractTests
{
    [Fact]
    public void IdleNormalization_OnlyReordersWidgetPeers()
    {
        string source = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Services/WidgetManager.ZOrder.cs"));
        string method = SliceMethod(
            source,
            "private bool NormalizeIdleWidgetZOrder",
            "private static IReadOnlyList<IDesktopWidgetWindow> GetWindowsInIdleHighestFirstOrder");

        Assert.Contains("ApplyPeerOrderHighestToLowest", method, StringComparison.Ordinal);
        Assert.DoesNotContain("MoveToDesktopBottom", method, StringComparison.Ordinal);
        Assert.DoesNotContain("SetWindowToBottom", method, StringComparison.Ordinal);
    }

    [Fact]
    public void TransientDeskBoxActivation_ReassertsTheWholeRaisedGroupWithoutEndingSession()
    {
        string manager = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Services/WidgetManager.ZOrder.cs"));
        string method = SliceMethod(
            manager,
            "internal bool ReassertRaisedWidgetGroupAfterDeskBoxActivation",
            "public bool RequestRestoreRaisedWidgetsToDesktopLayer");
        string contentWindow = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Views/ContentWidgetWindow.WindowInteraction.cs"));
        string stackPopover = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Controls/WidgetContents/FileSurfaceContent.StackPopover.cs"));

        Assert.Contains("_widgetsRaisedFromTray", method, StringComparison.Ordinal);
        Assert.Contains("IsForegroundDeskBoxWindow()", method, StringComparison.Ordinal);
        Assert.Contains("ApplyPeerOrderHighestToLowest", method, StringComparison.Ordinal);
        Assert.DoesNotContain("RestoreRaisedWidgetsToDesktopLayer", method, StringComparison.Ordinal);
        Assert.Contains(
            "ReassertRaisedWidgetGroupAfterDeskBoxActivation",
            contentWindow,
            StringComparison.Ordinal);
        Assert.Contains(
            "ReassertRaisedWidgetGroupAfterDeskBoxActivation",
            stackPopover,
            StringComparison.Ordinal);
    }

    [Fact]
    public void RaisedSessionRestore_ReanchorsTheCompleteGroupBehindForeground()
    {
        string manager = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Services/WidgetManager.cs"));
        string restore = SliceMethod(
            manager,
            "private void RestoreRaisedWidgetsToDesktopLayer(bool force)",
            "public bool SetWidgetPositionLocked");
        string layerService = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Services/WidgetLayerService.cs"));

        Assert.Contains("RestoreGroupPreservingForeground", restore, StringComparison.Ordinal);
        Assert.DoesNotContain("NormalizeIdleWidgetZOrder(\"raised-session-restored\")", restore, StringComparison.Ordinal);
        Assert.Contains("case RelativeLayerRestoreDisposition.BehindForeground:", layerService, StringComparison.Ordinal);
        Assert.Contains("ApplyWindowOrderHighestToLowest", layerService, StringComparison.Ordinal);
    }

    [Fact]
    public void DesktopPinnedPointerActivation_IsBlockedBeforeDefaultWindowActivation()
    {
        string bounds = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Views/WidgetWindowBase.Bounds.cs"));
        string setup = SliceMethod(
            bounds,
            "private void InstallDesktopPinnedActivationGuard",
            "private void RemoveDesktopPinnedActivationGuard");
        string callback = SliceMethod(
            bounds,
            "private IntPtr DesktopPinnedActivationSubclassProc",
            "// ── Bounds management");

        Assert.Contains("SetWindowSubclass", setup, StringComparison.Ordinal);
        Assert.Contains("Win32Helper.WM_MOUSEACTIVATE", callback, StringComparison.Ordinal);
        Assert.Contains("WidgetLayerService.ShouldSuppressPointerActivation", callback, StringComparison.Ordinal);
        Assert.Contains("Win32Helper.MA_NOACTIVATE", callback, StringComparison.Ordinal);
        Assert.Contains("RestoreDesktopPinnedBottomState", callback, StringComparison.Ordinal);
        Assert.DoesNotContain("HoldTemporaryTopMost", callback, StringComparison.Ordinal);
    }

    [Fact]
    public void DesktopPinnedBlankAreaClick_UsesNoActivateRestingStyleAndRoutedGuard()
    {
        string bounds = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Views/WidgetWindowBase.Bounds.cs"));
        string layerService = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Services/WidgetLayerService.cs"));
        string routedHandler = SliceMethod(
            bounds,
            "private void RootElement_PointerPressedForDesktopPinnedLayer",
            "private void WidgetWindowBase_ActivatedForDesktopPinnedLayer");

        Assert.Contains("WS_EX_NOACTIVATE", layerService, StringComparison.Ordinal);
        Assert.Contains("ApplyDesktopPinnedActivationStyle(windowHandle)", layerService, StringComparison.Ordinal);
        Assert.Contains("UIElement.PointerPressedEvent", bounds, StringComparison.Ordinal);
        Assert.Contains("TryAllowDesktopPinnedPointerActivation", routedHandler, StringComparison.Ordinal);
        Assert.Contains("IsKeyboardInputTarget(args.OriginalSource)", routedHandler, StringComparison.Ordinal);
        Assert.Contains("PrepareForDesktopPinnedKeyboardInput", routedHandler, StringComparison.Ordinal);
        Assert.Contains("RestoreDesktopPinnedBottomState", routedHandler, StringComparison.Ordinal);
        Assert.Contains("ActivateDesktopPinnedWindow", routedHandler, StringComparison.Ordinal);
        Assert.DoesNotContain("args.Handled = true", routedHandler, StringComparison.Ordinal);
    }

    [Fact]
    public void DesktopPinnedKeyboardInput_BypassesBlankAreaSuppressionAndRestoresBottomLayer()
    {
        string bounds = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Views/WidgetWindowBase.Bounds.cs"));
        string layerService = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Services/WidgetLayerService.cs"));
        string setup = SliceMethod(
            bounds,
            "private void InstallDesktopPinnedPointerRouting",
            "private void RemoveDesktopPinnedPointerRouting");
        string gotFocus = SliceMethod(
            bounds,
            "private void RootElement_GotFocusForDesktopPinnedLayer",
            "private static bool IsKeyboardInputTarget");
        string keyboardTarget = SliceMethod(
            bounds,
            "private static bool IsKeyboardInputTarget",
            "private void ActivateDesktopPinnedWindow");
        string activate = SliceMethod(
            bounds,
            "private void ActivateDesktopPinnedWindow",
            "private void WidgetWindowBase_ActivatedForDesktopPinnedLayer");
        string prepare = SliceMethod(
            layerService,
            "public static void PrepareForDesktopPinnedKeyboardInput",
            "public static bool IsWindowNoActivate");

        Assert.Contains("RootElement.GotFocus +=", setup, StringComparison.Ordinal);
        Assert.Contains("IsKeyboardInputTarget(args.OriginalSource)", gotFocus, StringComparison.Ordinal);
        Assert.Contains("PrepareForDesktopPinnedKeyboardInput", gotFocus, StringComparison.Ordinal);
        Assert.Contains("TextBox", keyboardTarget, StringComparison.Ordinal);
        Assert.Contains("RichEditBox", keyboardTarget, StringComparison.Ordinal);
        Assert.Contains("PasswordBox", keyboardTarget, StringComparison.Ordinal);
        Assert.Contains("SetWindowNoActivate(windowHandle, enabled: false)", prepare, StringComparison.Ordinal);
        Assert.Contains("base.Activate()", activate, StringComparison.Ordinal);
        Assert.Contains("SetForegroundWindow(HWnd)", activate, StringComparison.Ordinal);
        Assert.Contains("RestoreDesktopPinnedBottomState(reason)", activate, StringComparison.Ordinal);
    }

    [Fact]
    public void DesktopPinnedInteraction_RestoresBottomWhileExpandedCapsuleRaisesAbovePeers()
    {
        string bounds = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Views/WidgetWindowBase.Bounds.cs"));
        string activated = SliceMethod(
            bounds,
            "private void WidgetWindowBase_ActivatedForDesktopPinnedLayer",
            "private void RestoreDesktopPinnedBottomState");
        string restore = SliceMethod(
            bounds,
            "private void RestoreDesktopPinnedBottomState",
            "private IntPtr DesktopPinnedActivationSubclassProc");
        string collapse = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Views/WidgetWindowBase.Collapse.cs"));
        string expand = SliceMethod(
            collapse,
            "private void RaiseForExpandedState()",
            "private void AcquireExpandedWidgetLayerLease");
        string manager = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Services/WidgetManager.ZOrder.cs"));
        string acquire = SliceMethod(
            manager,
            "internal long AcquireExpandedWidgetLayer",
            "internal bool ReleaseExpandedWidgetLayer");

        Assert.Contains("RestoreDesktopPinnedBottomState", activated, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "WindowActivationState.Deactivated",
            activated,
            StringComparison.Ordinal);
        Assert.Contains(
            "WidgetLayerService.MoveToDesktopBottom(HWnd, showWindow: false)",
            restore,
            StringComparison.Ordinal);
        Assert.Contains("TopMostSafetyTimer?.Stop()", restore, StringComparison.Ordinal);

        // The expanded capsule must not be flattened to the desktop bottom:
        // plain interaction reasserts the bottom, but an expand raises the
        // window to the top of the widget band through the shared helpers.
        Assert.DoesNotContain(
            "WidgetLayerService.UsesDesktopPinnedMode()",
            expand,
            StringComparison.Ordinal);
        Assert.DoesNotContain("MoveToDesktopBottom", expand, StringComparison.Ordinal);
        Assert.Contains(
            "TryBringAbovePeerWidgetsAtDesktopLayer",
            expand,
            StringComparison.Ordinal);
        Assert.Contains(
            "AcquireExpandedWidgetLayerLease(\"expanded-state-desktop\")",
            expand,
            StringComparison.Ordinal);

        // The expanded-layer lease applies in desktop-pinned mode too: the
        // acquire path must order the capsule above its peers without ever
        // moving it to the desktop bottom.
        Assert.Contains(
            "WidgetExpandedLayerLeasePolicy.Acquire(",
            acquire,
            StringComparison.Ordinal);
        Assert.Contains("EnsurePeerOrderHighestToLowest", acquire, StringComparison.Ordinal);
        Assert.DoesNotContain("MoveToDesktopBottom", acquire, StringComparison.Ordinal);
    }

    [Fact]
    public void DesktopPinnedActivationRestore_CannotReshowHiddenWindow()
    {
        string bounds = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Views/WidgetWindowBase.Bounds.cs"));
        string restore = SliceMethod(
            bounds,
            "private void RestoreDesktopPinnedBottomState",
            "private IntPtr DesktopPinnedActivationSubclassProc");

        int visibilityGuard = restore.IndexOf(
            "WidgetTemporaryRaiseLeasePolicy.CanRestoreDesktopLayer(",
            StringComparison.Ordinal);
        int layerRepair = restore.IndexOf(
            "WidgetLayerService.MoveToDesktopBottom(HWnd, showWindow: false)",
            StringComparison.Ordinal);

        Assert.InRange(visibilityGuard, 0, layerRepair - 1);
        Assert.Contains("Visible,", restore, StringComparison.Ordinal);
        Assert.Contains("IsHideAnimationRunning,", restore, StringComparison.Ordinal);
        Assert.Contains("IsClosing", restore, StringComparison.Ordinal);
        Assert.Contains("CancelPendingDesktopLayerRestore();", restore, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "WidgetLayerService.MoveToDesktopBottom(HWnd);",
            restore,
            StringComparison.Ordinal);
    }

    [Fact]
    public void DesktopPinnedPeerRaiseEntryPoints_SplitTrayRaiseBottomFromCapsuleBandTop()
    {
        string layerService = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Services/WidgetLayerService.cs"));
        string bring = SliceMethod(
            layerService,
            "public static void BringAbovePeerWidgets",
            "public static bool TryBringAbovePeerWidgetsAtDesktopLayer");
        string tryBring = SliceMethod(
            layerService,
            "public static bool TryBringAbovePeerWidgetsAtDesktopLayer",
            "public static bool EnsurePeerOrderHighestToLowest");

        // Tray-raise keeps desktop-pinned widgets resting at the band bottom.
        string pinnedBring = bring[..bring.IndexOf(
            "DetachFromDesktopIconLayerIfNeeded",
            StringComparison.Ordinal)];
        Assert.Contains("MoveToDesktopBottom(windowHandle)", pinnedBring, StringComparison.Ordinal);
        Assert.DoesNotContain("HWND_TOP", pinnedBring, StringComparison.Ordinal);

        // The capsule expand raise must stay inside the desktop band while
        // taking the top of it: attach without bottom placement, then HWND_TOP.
        Assert.DoesNotContain(
            "UsesDesktopPinnedMode()",
            tryBring,
            StringComparison.Ordinal);
        Assert.Contains("placeAtBottom: false", tryBring, StringComparison.Ordinal);
        Assert.Contains("HWND_TOP", tryBring, StringComparison.Ordinal);
        Assert.DoesNotContain("MoveToDesktopBottom", tryBring, StringComparison.Ordinal);
    }

    private static string SliceMethod(string source, string startMarker, string endMarker)
    {
        int start = source.IndexOf(startMarker, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Missing source marker: {startMarker}");
        int end = source.IndexOf(endMarker, start, StringComparison.Ordinal);
        Assert.True(end > start, $"Missing source marker: {endMarker}");
        return source[start..end];
    }
}
