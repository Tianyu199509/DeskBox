namespace DeskBox.Tests;

public sealed class WidgetStackPopoverLayerContractTests
{
    [Fact]
    public void StackPopoverHost_RemainsUnownedAndDefersHideOutOfDeactivateCallback()
    {
        string source = Read("src/DeskBox/Views/StackPopoverHostWindow.cs");
        string ctor = SliceMethod(
            source,
            "public StackPopoverHostWindow(IntPtr ownerWindowHandle)",
            "internal event Action? DeactivatedByOutsideClick;");
        string hide = SliceMethod(
            source,
            "internal void HidePopover(",
            "internal void UpdateAppearance(");

        // Chaining GWLP_HWNDPARENT to the widget promotes the entire
        // SHELLDLL_DefView pinned band when the popover activates and lets
        // owner SetWindowPos(HWND_BOTTOM) strip WS_EX_TOPMOST while parked.
        Assert.DoesNotContain("GWLP_HWNDPARENT", ctor, StringComparison.Ordinal);

        // Calling _appWindow.Hide() synchronously inside WM_ACTIVATE triggers
        // Win32 fallback activation that skips WS_EX_TOOLWINDOW widgets and
        // hands foreground to Explorer/foreign apps, collapsing Dynamic groups.
        int postIndex = ctor.IndexOf("UiDispatch.PostAfterCallback", StringComparison.Ordinal);
        int invokeIndex = ctor.IndexOf("DeactivatedByOutsideClick?.Invoke();", StringComparison.Ordinal);
        Assert.True(
            postIndex >= 0 && invokeIndex > postIndex,
            "DeactivatedByOutsideClick must be deferred out of the WM_ACTIVATE callback frame.");
        Assert.Contains("sessionId != _showSessionId", ctor, StringComparison.Ordinal);

        // Explicit closes (Escape, close button, stack toggle) hand foreground
        // back to the visible owner widget in non-pinned modes before hiding,
        // and outside-click hides preserve any DeskBox widget foreground.
        Assert.Contains("bool restoreOwnerForeground = false", hide, StringComparison.Ordinal);
        Assert.Contains("!WidgetLayerService.UsesDesktopPinnedMode()", hide, StringComparison.Ordinal);
        Assert.Contains("SetForegroundWindow(_ownerWindowHandle)", hide, StringComparison.Ordinal);
        Assert.Contains("App.Current?.IsDeskBoxWindow(foregroundBeforeHide) == true", hide, StringComparison.Ordinal);
        Assert.Contains("SetForegroundWindow(foregroundBeforeHide)", hide, StringComparison.Ordinal);
    }

    [Fact]
    public void StackPopoverDismiss_PreservesPeerCohesionAndNotifiesExternalForegroundLeave()
    {
        string popover = Read("src/DeskBox/Controls/WidgetContents/FileSurfaceContent.StackPopover.cs");
        string dismiss = SliceMethod(
            popover,
            "private void StackPopoverHost_DeactivatedByOutsideClick()",
            "private bool IsScreenPointInsideStackSurface(");
        string hideReuse = SliceMethod(
            popover,
            "private void HideStackPopoverForReuse(",
            "private void ReconcileStackPopoverItems(");
        string bounds = Read("src/DeskBox/Views/WidgetWindowBase.Bounds.cs");

        Assert.Contains("HideStackPopoverForReuse(restoreOwnerForeground: false);", dismiss, StringComparison.Ordinal);
        Assert.Contains("NotifyTransientPopupDismissedFromWidget(_hostWindowHandle)", dismiss, StringComparison.Ordinal);
        Assert.DoesNotContain("SuppressRaisedGroupReassertBriefly", hideReuse, StringComparison.Ordinal);
        Assert.Contains(
            "DismissActiveStackPopoverFromDesktopPinnedClick(HWnd, args.OriginalSource);",
            bounds,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ForegroundAndOwnerResolution_NeverOvershootsThroughDefViewIntoExplorer()
    {
        string layerService = Read("src/DeskBox/Services/WidgetLayerService.cs");
        string clearSingle = SliceMethod(
            layerService,
            "internal static IntPtr ClearTopMostPreservingForeground(",
            "/// <summary>");
        string restoreGroup = SliceMethod(
            layerService,
            "internal static bool RestoreGroupPreservingForeground(\n        IReadOnlyList<IntPtr> windowHandles,\n        string reason,\n        out RelativeLayerRestoreDisposition disposition)",
            "public static void ClearTopMost(IntPtr windowHandle)");
        string foregroundRoot = SliceMethod(
            layerService,
            "internal static IntPtr GetForegroundRoot(IntPtr foreground)",
            "public static void BringGroupTemporarilyToFront(");
        string ownedWalk = SliceMethod(
            layerService,
            "internal static bool IsWindowOrOwnedByWidget(",
            "private sealed record DesktopLayerAttachment");
        string baseInteraction = Read("src/DeskBox/Views/WidgetWindowBase.Interaction.cs");
        string contentInteraction = Read("src/DeskBox/Views/ContentWidgetWindow.WindowInteraction.cs");

        // GetForegroundRoot and IsWindowOrOwnedByWidget must stop before
        // crossing SHELLDLL_DefView into Explorer's Progman/WorkerW host.
        Assert.Contains("App.Current?.IsDeskBoxWindow(foreground) == true", foregroundRoot, StringComparison.Ordinal);
        Assert.Contains("IsDesktopShellWindow(next)", foregroundRoot, StringComparison.Ordinal);
        Assert.Contains("IsDesktopShellWindow(current)", ownedWalk, StringComparison.Ordinal);
        Assert.DoesNotContain("GA_ROOTOWNER", baseInteraction, StringComparison.Ordinal);
        Assert.DoesNotContain("GA_ROOTOWNER", contentInteraction, StringComparison.Ordinal);

        // PreservePeerOrder leaves Dynamic widgets raised in an active DeskBox
        // session and must not re-attach them to SHELLDLL_DefView mid-session.
        string singlePreserve = SliceMethod(
            clearSingle,
            "case RelativeLayerRestoreDisposition.PreservePeerOrder:",
            "case RelativeLayerRestoreDisposition.BehindForeground:");
        string groupPreserve = SliceMethod(
            restoreGroup,
            "case RelativeLayerRestoreDisposition.PreservePeerOrder:",
            "case RelativeLayerRestoreDisposition.BehindForeground:");
        Assert.DoesNotContain("TryAttachRestingWindowWithoutChangingLevel", singlePreserve, StringComparison.Ordinal);
        Assert.DoesNotContain("PrepareRestingWindowForRelativePlacement", groupPreserve, StringComparison.Ordinal);
        Assert.Contains("DetachFromDesktopIconLayerIfNeeded", singlePreserve, StringComparison.Ordinal);
        Assert.Contains("DetachFromDesktopIconLayerIfNeeded", groupPreserve, StringComparison.Ordinal);
    }

    [Fact]
    public void DynamicAndAuxiliarySessions_KeepGroupCohesionAndHonorProxyMenus()
    {
        string manager = Read("src/DeskBox/Services/WidgetManager.ZOrder.cs");
        string contentInteraction = Read("src/DeskBox/Views/ContentWidgetWindow.WindowInteraction.cs");
        string trayMenu = Read("src/DeskBox/Views/TrayContextMenuWindow.cs");
        string searchPopup = Read("src/DeskBox/Views/SearchPopupWindow.xaml.cs");

        string activated = SliceMethod(
            contentInteraction,
            "private void ContentWidgetWindow_Activated(",
            "private void QueueRestoreDesktopLayerIfForegroundLeavesDeskBox()");
        Assert.Contains("WidgetLayerService.UsesDynamicGroupClickRaiseMode()", activated, StringComparison.Ordinal);

        string promoteGroup = SliceMethod(
            manager,
            "internal void PromotePeerWidgetsAfterDynamicBodyActivation(",
            "internal void RebedDynamicPassiveSiblingsAfterSingleActivation(");
        Assert.Contains("Prepend(activeWindowHandle)", promoteGroup, StringComparison.Ordinal);
        Assert.Contains("IsForeignWindowAboveRaisedPeers", promoteGroup, StringComparison.Ordinal);

        string singleRebed = SliceMethod(
            manager,
            "internal void RebedDynamicPassiveSiblingsAfterSingleActivation(",
            "internal void NotifyPotentialDynamicDesktopClick(");
        Assert.Contains("if (!_dynamicBlankDesktopPromoted)", singleRebed, StringComparison.Ordinal);

        string titleRaise = SliceMethod(
            manager,
            "public void ActivateAllVisibleWidgetsFromTitle(",
            "private IntPtr _lastTitleActivationRaiseWindowHandle;");
        Assert.Contains("CancelDynamicBlankDesktopPromotion(\"title-group-activated\");", titleRaise, StringComparison.Ordinal);
        Assert.Contains("MarkRaisedAboveDesktopLayerFromManager()", titleRaise, StringComparison.Ordinal);

        // Proxy-hosted Shell context menus run out-of-process and must hold
        // temporary-raise, tray-raise/QuickReveal, mouse-sampler, and blank-desktop monitors.
        string tempRestore = SliceMethod(
            manager,
            "private bool RestoreTemporarilyRaisedWidgetsToDesktopLayerCore(",
            "private void ClearTemporaryRaiseLease(");
        string trayRestore = SliceMethod(
            manager,
            "private void TryRestoreRaisedWidgetsAfterInteraction(",
            "private void CompleteRaisedSessionAfterExternalInteraction(");
        string sampler = SliceMethod(
            manager,
            "private void TrayMouseSamplerTimer_Tick(",
            "private void TrayLayerRestoreTimer_Tick(");
        string blankTick = SliceMethod(
            manager,
            "private void DynamicBlankDesktopTimer_Tick(",
            "private void PromoteWidgetsFromDynamicBlankDesktopClick()");
        Assert.Contains("ShellContextMenuProxy.IsMenuRoundInFlight", tempRestore, StringComparison.Ordinal);
        Assert.Contains("ShellContextMenuProxy.IsMenuRoundInFlight", trayRestore, StringComparison.Ordinal);
        Assert.Contains("ShellContextMenuProxy.IsMenuRoundInFlight", sampler, StringComparison.Ordinal);
        Assert.Contains("ShellContextMenuProxy.IsMenuRoundInFlight", blankTick, StringComparison.Ordinal);

        // Auxiliary toolwindows must not steal or drop raised-session foreground on close.
        string trayActivated = SliceMethod(
            trayMenu,
            "private void OnWindowActivated(",
            "private void CloseMenu(");
        string trayClose = SliceMethod(
            trayMenu,
            "private void CloseMenu(",
            "private void ShutdownCore()");
        Assert.Contains("UiDispatch.PostAfterCallback(", trayActivated, StringComparison.Ordinal);
        Assert.Contains("IsCurrentProcessPopupBridge(fg)", trayActivated, StringComparison.Ordinal);
        Assert.Contains("CloseMenu(invokedEntry: _invokedEntry);", trayActivated, StringComparison.Ordinal);
        Assert.Contains("RestoreForegroundToRaisedWidgetBeforeAuxiliaryHide(WindowHandle)", trayClose, StringComparison.Ordinal);

        string searchHide = SliceMethod(
            searchPopup,
            "private void OnPopupHideCompleted(",
            "private void ArmColdHiddenCleanupTimer()");
        Assert.Contains("RestoreForegroundToRaisedWidgetBeforeAuxiliaryHide(_hwnd)", searchHide, StringComparison.Ordinal);
    }

    private static string Read(string relativePath) =>
        File.ReadAllText(TestPaths.FromRepository(relativePath)).Replace("\r\n", "\n");

    private static string SliceMethod(string source, string startMarker, string endMarker)
    {
        int start = source.IndexOf(startMarker, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Missing source marker: {startMarker}");
        int end = source.IndexOf(endMarker, start, StringComparison.Ordinal);
        Assert.True(end > start, $"Missing source marker: {endMarker}");
        return source[start..end];
    }
}
