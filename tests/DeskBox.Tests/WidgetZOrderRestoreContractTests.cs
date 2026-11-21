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
        // Band-integrity upgrades (2026-10-17) must leave through the same
        // relative-restore funnel the session restore uses — anchored behind
        // the current foreign foreground — and only on non-cohesion passes.
        Assert.Contains("RestoreGroupPreservingForeground", method, StringComparison.Ordinal);
        Assert.Contains("if (!cohesionExit &&", method, StringComparison.Ordinal);
        Assert.DoesNotContain("MoveToDesktopBottom", method, StringComparison.Ordinal);
        Assert.DoesNotContain("SetWindowToBottom", method, StringComparison.Ordinal);
    }

    [Fact]
    public void IdleBandRepair_IsDynamicOnlyGuardedAndRelative()
    {
        string source = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Services/WidgetManager.ZOrder.cs"));
        string method = SliceMethod(
            source,
            "private bool NormalizeIdleWidgetZOrder",
            "private static IReadOnlyList<IDesktopWidgetWindow> GetWindowsInIdleHighestFirstOrder");

        // The pinned band keeps its own bedding watchdog; the sweep must not
        // double-repair there (full-line pin: polarity and both conditions).
        Assert.Contains(
            "if (!cohesionExit && !WidgetLayerService.UsesDesktopPinnedMode())",
            method,
            StringComparison.Ordinal);
        // A title-bar drag in flight must not be healed under the pointer.
        Assert.Contains(
            "!bandWindows.Any(window => window.IsTitleBarDragTransitionActive)",
            method,
            StringComparison.Ordinal);
        // The tear gate must precede the repair exit — without the ordering
        // the relative restore could run unconditionally on idle passes.
        int tear = method.IndexOf("IsForeignWindowAboveRaisedPeers", StringComparison.Ordinal);
        int heal = method.IndexOf("RestoreGroupPreservingForeground", StringComparison.Ordinal);
        Assert.True(tear >= 0 && heal > tear, "tear detection must precede the repair exit");
        // Requeue only when the tear is verified gone: the PreservePeerOrder
        // no-op "heal" must not fuel a 120ms self-requeue loop.
        Assert.Contains("healed && !stillTorn", method, StringComparison.Ordinal);
        Assert.Contains("exemptShellDesktopWindows: true", method, StringComparison.Ordinal);
        // T1 (2026-10-18): a DeskBox-owned foreground makes the heal resolve
        // to PreservePeerOrder, whose only repair form is an untracked
        // peer-lift — the heal must be skipped entirely in that state, before
        // the relative-restore call.
        int deferral = method.IndexOf(
            "Idle band tear deferred; DeskBox owns foreground",
            StringComparison.Ordinal);
        int repair = method.IndexOf("RestoreGroupPreservingForeground", StringComparison.Ordinal);
        Assert.True(deferral >= 0 && repair > deferral,
            "the DeskBox-foreground deferral must precede the repair exit");
        Assert.Contains("IsDeskBoxForegroundWindow(sweepForeground)", method, StringComparison.Ordinal);
        // Cohesion-exit passes preserve the collapsed capsule's relative
        // residue; they only queue a follow-up plain pass for the sweep.
        Assert.Contains("cohesionExit && applied", method, StringComparison.Ordinal);
        Assert.Contains("band-sweep", method, StringComparison.Ordinal);
    }

    [Fact]
    public void TransientDeskBoxActivation_ReassertsTheWholeRaisedGroupWithoutEndingSession()
    {
        string manager = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Services/WidgetManager.ZOrder.cs"));
        string method = SliceMethod(
            manager,
            "internal bool ReassertRaisedWidgetGroupAfterDeskBoxActivation",
            "private void TryRestoreRaisedWidgetsAfterInteraction");
        string layerService = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Services/WidgetLayerService.cs"));
        string reassertOrder = SliceMethod(
            layerService,
            "public static bool ApplyRaisedGroupReassertOrder",
            "internal static bool IsForeignWindowAboveRaisedPeers");
        string contentWindow = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Views/ContentWidgetWindow.WindowInteraction.cs"));
        string stackPopover = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Controls/WidgetContents/FileSurfaceContent.StackPopover.cs"));

        Assert.Contains("_widgetsRaisedFromTray", method, StringComparison.Ordinal);
        Assert.Contains("IsForegroundDeskBoxWindow()", method, StringComparison.Ordinal);
        Assert.Contains("ApplyRaisedGroupReassertOrder", method, StringComparison.Ordinal);
        Assert.DoesNotContain("RestoreRaisedWidgetsToDesktopLayer", method, StringComparison.Ordinal);

        // The reassert order must escalate: a tray raise that ran while
        // DeskBox was a background process pins the peers below the
        // then-foreground window, and only a repeated group lift can repair
        // that split band once DeskBox owns the foreground. An intact band
        // keeps the repaint-free peer reorder.
        Assert.Contains("RaisedGroupReassertPolicy.Resolve", reassertOrder, StringComparison.Ordinal);
        Assert.Contains("BringGroupTemporarilyToFront", reassertOrder, StringComparison.Ordinal);
        Assert.Contains("ApplyPeerOrderHighestToLowest", reassertOrder, StringComparison.Ordinal);
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
        // Since the callback-isolation batch the routed handler schedules the
        // activation/restore units off the input-callback frame instead of
        // running them inline; the units themselves stay pinned by the
        // keyboard-input test's activate slice and the deferral test below.
        Assert.Contains("DeferDesktopPinnedRestore(reason)", routedHandler, StringComparison.Ordinal);
        Assert.Contains("DeferDesktopPinnedActivation(reason)", routedHandler, StringComparison.Ordinal);
        Assert.DoesNotContain("args.Handled = true", routedHandler, StringComparison.Ordinal);
    }

    [Fact]
    public void DesktopPinnedWindowWork_IsDeferredOutOfCallbackFrames()
    {
        string bounds = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Views/WidgetWindowBase.Bounds.cs"));
        string subclass = SliceMethod(
            bounds,
            "private IntPtr DesktopPinnedActivationSubclassProc",
            "// ── Bounds management");
        string deferActivation = SliceMethod(
            bounds,
            "private void DeferDesktopPinnedActivation",
            "private void DeferDesktopPinnedRestore");
        string deferRestore = SliceMethod(
            bounds,
            "private void DeferDesktopPinnedRestore",
            "private void RestoreDesktopPinnedBottomState");

        // The WM_MOUSEACTIVATE answer and the suppression stamp stay
        // synchronous (the routed event follows in the same click); the
        // bedding surgery leaves the callback frame.
        Assert.Contains("UiDispatch.PostAfterCallback", subclass, StringComparison.Ordinal);
        Assert.Contains(
            "_desktopPinnedNativeSuppressionTimestamp = DateTime.UtcNow.Ticks;",
            subclass,
            StringComparison.Ordinal);
        Assert.Contains("Win32Helper.MA_NOACTIVATE", subclass, StringComparison.Ordinal);
        Assert.Contains("IsWindowInCurrentProcess(hWnd)", subclass, StringComparison.Ordinal);
        Assert.Contains("IsDragging", subclass, StringComparison.Ordinal);

        // Landing guards on the deferred activation: window liveness plus
        // process ownership plus the drag guard; the foreground snapshot —
        // a late activation must never steal a foreground the user gave away
        // during the deferred turn — is taken OUTSIDE the landing lambda
        // (inside it the guard would always pass).
        Assert.Contains("UiDispatch.PostAfterCallback", deferActivation, StringComparison.Ordinal);
        int snapshotIndex = deferActivation.IndexOf(
            "IntPtr foregroundAtEnqueue = Win32Helper.GetForegroundWindow();",
            StringComparison.Ordinal);
        int enqueueIndex = deferActivation.IndexOf(
            "UiDispatch.PostAfterCallback",
            StringComparison.Ordinal);
        Assert.True(snapshotIndex >= 0 && snapshotIndex < enqueueIndex,
            "The foreground snapshot must be taken before enqueueing.");
        Assert.Contains("IsWindowInCurrentProcess(HWnd)", deferActivation, StringComparison.Ordinal);
        Assert.Contains("IsDragging", deferActivation, StringComparison.Ordinal);
        Assert.Contains("ActivateDesktopPinnedWindow(reason)", deferActivation, StringComparison.Ordinal);

        Assert.Contains("UiDispatch.PostAfterCallback", deferRestore, StringComparison.Ordinal);
        Assert.Contains("IsWindowInCurrentProcess(HWnd)", deferRestore, StringComparison.Ordinal);
        // The restore call itself must stay inside the deferred closure —
        // emptying the lambda would otherwise pass every other pin.
        Assert.Contains(
            "RestoreDesktopPinnedBottomState(reason);",
            deferRestore,
            StringComparison.Ordinal);

        // The native-activation handler defers restore and re-bed as ONE
        // landing unit (#574 same-cycle invariant), with the in-flight flag
        // captured at event time.
        string activatedHandler = SliceMethod(
            bounds,
            "private void WidgetWindowBase_ActivatedForDesktopPinnedLayer",
            "private void DeferDesktopPinnedActivation");
        Assert.Contains("UiDispatch.PostAfterCallback", activatedHandler, StringComparison.Ordinal);
        Assert.Contains("deliberateActivationInFlight", activatedHandler, StringComparison.Ordinal);
        int restoreIndex = activatedHandler.IndexOf(
            "RestoreDesktopPinnedBottomState",
            StringComparison.Ordinal);
        int rebedIndex = activatedHandler.IndexOf(
            "RebedPinnedSiblingsAfterActivation",
            StringComparison.Ordinal);
        Assert.True(restoreIndex >= 0 && rebedIndex > restoreIndex,
            "Restore must run before the sibling re-bed inside the same unit.");
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
        // Desktop-pinned expansion additionally re-beds a floating group
        // through the manager guard before raising (owner-band promotion
        // residue must not carry the expanded capsule above apps, #574) —
        // the guard excludes active leases, so an expanded capsule is never
        // flattened by it, and the expand flow itself still never calls
        // MoveToDesktopBottom directly.
        Assert.Contains(
            "TryRebedDesktopPinnedGroupBeforeExpand",
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

    [Fact]
    public void IdleNormalization_NeverReordersRestingPeersForShadowsAndShortCircuitsAlreadyAppliedOrder()
    {
        string zorder = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Services/WidgetManager.ZOrder.cs"));
        string bounds = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Views/WidgetWindowBase.Bounds.cs"));
        string interaction = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Views/WidgetWindowBase.Interaction.cs"));
        string method = SliceMethod(
            zorder,
            "private bool NormalizeIdleWidgetZOrder",
            "private static IReadOnlyList<IDesktopWidgetWindow> GetWindowsInIdleHighestFirstOrder");
        string layerService = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Services/WidgetLayerService.cs"));
        string apply = SliceMethod(
            layerService,
            "public static bool ApplyPeerOrderHighestToLowest",
            "/// <summary>");

        // Desktop widgets are shadowless: automatic shadow-covering peer
        // reordering is removed across manager and window restore paths.
        Assert.DoesNotContain("TryGetWindowDropShadowEnabled", zorder, StringComparison.Ordinal);
        Assert.DoesNotContain("ShouldNormalizeIdlePeerOrder", zorder, StringComparison.Ordinal);
        Assert.DoesNotContain("HasAnyShadowOverlapRisk", zorder, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "ApplyImmediateRestingPeerOrderAfterRestore",
            zorder + bounds + interaction,
            StringComparison.Ordinal);

        int nonCohesionBail = method.IndexOf("if (!cohesionExit)", StringComparison.Ordinal);
        int peerApply = method.IndexOf("ApplyPeerOrderHighestToLowest", StringComparison.Ordinal);
        Assert.True(nonCohesionBail >= 0 && peerApply > nonCohesionBail,
            "non-cohesion idle passes must exit before any peer reorder");

        // An already-correct peer order must still short-circuit before SetWindowPos.
        Assert.Contains("IsPeerOrderAlreadyApplied", apply, StringComparison.Ordinal);
        Assert.DoesNotContain("MoveToDesktopBottom", method, StringComparison.Ordinal);
        Assert.DoesNotContain("SetWindowToBottom", method, StringComparison.Ordinal);
    }

    [Fact]
    public void DesktopPinnedCoveredPointerActivation_SuppressesEvenOnDesktopForeground()
    {
        string layerService = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Services/WidgetLayerService.cs"));
        string managerZOrder = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Services/WidgetManager.ZOrder.cs"));
        string decision = SliceMethod(
            layerService,
            "public static bool ShouldSuppressPointerActivation",
            "public static bool ShouldPreserveQuickRevealActivatingClick");
        string coveredWalk = SliceMethod(
            layerService,
            "public static bool IsWindowCoveredByForeignWindow",
            "private static bool RectsIntersect");
        // The activation decision must consider geometric coverage: activating
        // a DefView-owned HWND promotes the whole owner band above covering
        // applications even while the desktop shell owns the foreground (#574).
        Assert.Contains("IsWindowCoveredByForeignWindow(windowHandle)", decision, StringComparison.Ordinal);

        // The coverage walk looks upward in Z order and requires a rect
        // intersection, so promotion over non-overlapping windows stays
        // allowed (keyboard flows keep working on exposed widgets).
        Assert.Contains("Win32Helper.GW_HWNDPREV", coveredWalk, StringComparison.Ordinal);
        Assert.Contains("ClassifyWindowForBedding", coveredWalk, StringComparison.Ordinal);
        Assert.Contains("RectsIntersect", coveredWalk, StringComparison.Ordinal);
        Assert.Contains("MaxBeddingWalkWindows", coveredWalk, StringComparison.Ordinal);

        // The taskbar is neutral: an auto-hide taskbar overlapping a widget
        // must not read as a covering application. The classifier lives on
        // the manager and is shared, not duplicated per walk.
        Assert.Contains("WidgetManager.IsTaskbarWindow", coveredWalk, StringComparison.Ordinal);
        string taskbarClassifier = SliceMethod(
            managerZOrder,
            "internal static bool IsTaskbarWindow",
            "private static bool IsDesktopShellWindow");
        Assert.Contains("Shell_TrayWnd", taskbarClassifier, StringComparison.Ordinal);
        Assert.Contains("Shell_SecondaryTrayWnd", taskbarClassifier, StringComparison.Ordinal);
        Assert.Contains("NotifyIconOverflowWindow", taskbarClassifier, StringComparison.Ordinal);
    }

    [Fact]
    public void DesktopPinnedExpand_RebedsFloatingGroupBeforeRaisingCapsule()
    {
        string collapse = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Views/WidgetWindowBase.Collapse.cs"));
        string expand = SliceMethod(
            collapse,
            "private void RaiseForExpandedState",
            "private void AcquireExpandedWidgetLayerLease");
        string manager = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Services/WidgetManager.ZOrder.cs"));
        string rebed = SliceMethod(
            manager,
            "internal bool TryRebedDesktopPinnedGroupBeforeExpand",
            "private void VerifyDesktopPinnedBedding");

        // A pinned group floats only through owner-band promotion residue; the
        // expanded capsule must not ride that band above covering apps (#574).
        int rebedBranch = expand.IndexOf(
            "TryRebedDesktopPinnedGroupBeforeExpand",
            StringComparison.Ordinal);
        int floatingBranch = expand.IndexOf(
            "expanded-state-floating",
            StringComparison.Ordinal);
        Assert.True(rebedBranch >= 0, "Expand flow must consult the pinned re-bed guard");
        Assert.True(
            floatingBranch > rebedBranch,
            "The pinned re-bed guard must run before the float-preserving branch");

        // The guard repairs through the same evidence-based funnel as the
        // watchdog and stays out of the way of active sessions.
        Assert.Contains("WidgetLayerService.UsesDesktopPinnedMode()", rebed, StringComparison.Ordinal);
        Assert.Contains("_widgetsRaisedFromTray", rebed, StringComparison.Ordinal);
        Assert.Contains("_sessionManager.IsInteractionActive", rebed, StringComparison.Ordinal);
        Assert.Contains("HasActiveExpandedWidgetLayerLease()", rebed, StringComparison.Ordinal);
        Assert.Contains("WidgetLayerService.FindForeignWindowBelow", rebed, StringComparison.Ordinal);
        Assert.Contains("RebedDesktopPinnedWidgetGroup", rebed, StringComparison.Ordinal);
    }

    [Fact]
    public void DesktopLayerPeerRaise_InsertsBelowNearestForeignWindowNotGlobalTop()
    {
        string layerService = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Services/WidgetLayerService.cs"));
        string peerRaise = SliceMethod(
            layerService,
            "public static bool TryBringAbovePeerWidgetsAtDesktopLayer",
            "private static IntPtr FindInsertBelowAnchorForDesktopLayerRaise");
        string anchor = SliceMethod(
            layerService,
            "private static IntPtr FindInsertBelowAnchorForDesktopLayerRaise",
            "public static bool EnsurePeerOrderHighestToLowest");

        // DefView ownership enforces only a minimum z position; HWND_TOP on a
        // pinned window reaches the top of the whole normal band and floats
        // the expanded capsule above every application (#574). The raise must
        // instead insert below the nearest visible foreign window above the
        // group — above sibling widgets, beneath every application.
        Assert.Contains(
            "FindInsertBelowAnchorForDesktopLayerRaise(windowHandle)",
            peerRaise,
            StringComparison.Ordinal);
        Assert.DoesNotContain("Win32Helper.HWND_TOP", peerRaise, StringComparison.Ordinal);

        // The anchor walk skips DeskBox/shell/invisible windows and treats
        // the taskbar as neutral; HWND_TOP survives only as the no-foreign-
        // window fallback where nothing can be jumped over.
        Assert.Contains("Win32Helper.GW_HWNDPREV", anchor, StringComparison.Ordinal);
        Assert.Contains("ClassifyWindowForBedding", anchor, StringComparison.Ordinal);
        Assert.Contains("WidgetManager.IsTaskbarWindow", anchor, StringComparison.Ordinal);
        Assert.Contains("return Win32Helper.HWND_TOP", anchor, StringComparison.Ordinal);
    }

    [Fact]
    public void DesktopPinnedActivation_RebedsSiblingsInTheSameCycle()
    {
        string bounds = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Views/WidgetWindowBase.Bounds.cs"));
        string explicitActivation = SliceMethod(
            bounds,
            "private void ActivateDesktopPinnedWindow",
            "private static bool IsDesktopShellRootWindow");
        string activatedHandler = SliceMethod(
            bounds,
            "private void WidgetWindowBase_ActivatedForDesktopPinnedLayer",
            "private void RestoreDesktopPinnedBottomState");
        string manager = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Services/WidgetManager.ZOrder.cs"));
        string rebed = SliceMethod(
            manager,
            "internal void RebedPinnedSiblingsAfterActivation",
            "private void VerifyDesktopPinnedBedding");

        // Activating any DefView-owned HWND promotes the whole owner band;
        // both the deliberate (keyboard) and the native activation paths must
        // drop the siblings back under covering applications in the same
        // cycle (#574), or the group lingers above apps until idle
        // normalization and reads as "all widgets jump to the front".
        Assert.Contains(
            "RebedPinnedSiblingsAfterActivation",
            explicitActivation,
            StringComparison.Ordinal);
        Assert.Contains(
            "RebedPinnedSiblingsAfterActivation",
            activatedHandler,
            StringComparison.Ordinal);
        Assert.Contains(
            "WindowActivationState.CodeActivated",
            activatedHandler,
            StringComparison.Ordinal);

        // The sibling pass reuses the evidence-based group restore, never
        // touches the activating window itself, and exempts the
        // expanded-capsule lease owner — bedding it would bury the expanded
        // capsule beneath sibling widgets mid-use.
        Assert.Contains(
            "WidgetLayerService.RestoreGroupPreservingForeground",
            rebed,
            StringComparison.Ordinal);
        Assert.Contains("window.WindowHandle != activeWindowHandle", rebed, StringComparison.Ordinal);
        Assert.Contains(
            "_expandedWidgetLayerLease.WindowHandle",
            rebed,
            StringComparison.Ordinal);
    }

    [Fact]
    public void NativeActivation_MarksWindowAboveDesktopLayerForDeactivateFunnel()
    {
        string source = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Views/ContentWidgetWindow.WindowInteraction.cs"));
        string method = SliceMethod(
            source,
            "private void ContentWidgetWindow_Activated(",
            "private void QueueRestoreDesktopLayerIfForegroundLeavesDeskBox()");

        // Funnel-4 fix (2026-10-17): a native body-click activation arrives
        // while the window is still logically bedded; without flipping
        // IsAtDesktopLayer the Deactivated guard `!IsAtDesktopLayer` never
        // passes and the window floats above foreign apps with no funnel
        // owning its restore. Full-line pin: both conditions and polarity.
        Assert.Contains(
            "if (IsAtDesktopLayer && !WidgetLayerService.UsesDesktopPinnedMode())",
            method,
            StringComparison.Ordinal);
        Assert.Contains("IsAtDesktopLayer = false", method, StringComparison.Ordinal);
        // The flip must live in the activation branch only — a flip inside
        // the Deactivated branch would mis-mark bedded windows as raised.
        string deactivated = SliceMethod(
            source,
            "private void ContentWidgetWindow_Activated(",
            "// A long-hidden release dropped");
        Assert.Contains("WindowActivationState.Deactivated", deactivated, StringComparison.Ordinal);
        Assert.DoesNotContain("IsAtDesktopLayer = false", deactivated, StringComparison.Ordinal);
    }

    [Fact]
    public void RaisedSessionRestoreHolds_AreInlinedAtWindowCallSites()
    {
        string manager = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Services/WidgetManager.ZOrder.cs"));
        string interaction = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Views/WidgetWindowBase.Interaction.cs"));
        string commands = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Views/ContentWidgetWindow.Commands.cs"));
        string windowInteraction = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Views/ContentWidgetWindow.WindowInteraction.cs"));

        // The pseudo-restore request (returned true and held until next
        // toggle without scheduling anything) is gone; each former call site
        // decides the hold inline from the session flag. Count pins: each
        // surface must keep its own hold — one site regressing to an
        // unconditional self-restore must fail here.
        Assert.DoesNotContain(
            "RequestRestoreRaisedWidgetsToDesktopLayer",
            manager + interaction + commands + windowInteraction,
            StringComparison.Ordinal);
        Assert.True(
            CountOccurrences(interaction, "is { WidgetsRaisedFromTray: true }") >= 2,
            "ReleaseInteractionLayer and ElevateForInteraction must both hold during a raised session");
        Assert.True(
            CountOccurrences(commands, "is { WidgetsRaisedFromTray: true }") >= 2,
            "title-rename completion and flyout close must both hold during a raised session");
        Assert.Contains(
            "is not { WidgetsRaisedFromTray: true }",
            windowInteraction,
            StringComparison.Ordinal);
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

    [Fact]
    public void TrayRaiseConfirmation_QueuesOneShotBandRecheckAfterAnimationsSettle()
    {
        string source = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Services/WidgetManager.TrayAnimation.cs"));
        string raise = SliceMethod(
            source,
            "private async Task<bool?> RaiseWidgetsFromTrayCoreAsync(",
            "private async Task<IDesktopWidgetWindow?> PrepareWidgetForBatchShowAsync(");
        string confirm = SliceMethod(
            source,
            "private void ConfirmTrayRaiseTopMost(",
            "private const int TrayRaiseBandRecheckDelayMs");
        string recheck = SliceMethod(
            source,
            "private void QueueTrayRaiseBandRecheck(",
            "private void SaveBatchVisibilityState()");

        // The recheck must be queued after the animation driver settles:
        // queued from the confirm itself it always landed inside its own
        // raise's toggle window (animations run 137ms+ against an 80ms
        // delay) and aborted on the toggling guard — the guard's only effect
        // was blocking the raise that queued it (#553 review, 2026-10-18).
        int settle = raise.IndexOf(
            "await _trayBatchAnimationDriver.WaitForIdleAsync();",
            StringComparison.Ordinal);
        int queue = raise.IndexOf(
            "QueueTrayRaiseBandRecheck(",
            StringComparison.Ordinal);
        Assert.True(settle >= 0 && queue > settle,
            "band recheck must be queued after WaitForIdleAsync");
        Assert.Contains(
            "!WidgetLayerService.UsesQuickRevealMode()",
            raise,
            StringComparison.Ordinal);

        // Neither confirm branch queues a recheck (quick-reveal never gets
        // one; the dynamic branch defers to the post-settle queue).
        Assert.DoesNotContain("QueueTrayRaiseBandRecheck", confirm, StringComparison.Ordinal);

        // Guard set (the v1.2.9 removal taught us repeated confirmations
        // must never fight the user's foreground switch) — note the
        // foreground polarity: abort unless DeskBox owns it.
        Assert.Contains("generation != _trayRaiseBatchGeneration", recheck, StringComparison.Ordinal);
        Assert.Contains("!_widgetsRaisedFromTray", recheck, StringComparison.Ordinal);
        Assert.Contains("!IsForegroundDeskBoxWindow())", recheck, StringComparison.Ordinal);
        Assert.Contains("IsTitleBarDragTransitionActive", recheck, StringComparison.Ordinal);
        Assert.Contains("exemptShellDesktopWindows: true", recheck, StringComparison.Ordinal);
        Assert.Contains("IsForeignWindowAboveRaisedPeers", recheck, StringComparison.Ordinal);
    }

    [Fact]
    public void GroupRaise_TrailingActivationSkipsWhenBandOwnsForeground()
    {
        string layerService = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Services/WidgetLayerService.cs"));
        string lift = SliceMethod(
            layerService,
            "public static void BringGroupTemporarilyToFront(",
            "/// <summary>");

        // #553: the trailing SetForegroundWindow is a synchronous kernel
        // call that deadlocked the UI thread while the input queue drained
        // after a long shell operation. It is redundant whenever the raised
        // band already owns the foreground — the skip must precede the call.
        int skip = lift.IndexOf("handles.Contains(foregroundAtRaise)", StringComparison.Ordinal);
        int activate = lift.IndexOf(
            "Win32Helper.SetForegroundWindow(activeHandle);",
            StringComparison.Ordinal);
        Assert.True(skip >= 0 && activate > skip,
            "foreground-ownership skip must guard the trailing activation");
        Assert.Contains("return;", lift, StringComparison.Ordinal);
    }

    [Fact]
    public void NoDisplacementRestore_KeepsRaisedFlagHonest()
    {
        string interaction = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Views/WidgetWindowBase.Interaction.cs"));
        string clear = SliceMethod(
            interaction,
            "protected void ClearTopMostOnly()",
            "// ── Drag logic");

        // PreservePeerOrder deliberately leaves a raised window at its
        // current level; marking it bedded would blind the Deactivated
        // funnel that owns its descent (the flyout-Esc lingering loop).
        Assert.Contains(
            "out RelativeLayerRestoreDisposition disposition",
            clear,
            StringComparison.Ordinal);
        Assert.Contains(
            "disposition != RelativeLayerRestoreDisposition.PreservePeerOrder",
            clear,
            StringComparison.Ordinal);
        // Line-ending-agnostic form pins (the previous multi-line literal
        // never matched a CRLF checkout): the flag write must sit inside the
        // disposition guard and be the only unconditional-form-free write.
        int guard = clear.IndexOf(
            "disposition != RelativeLayerRestoreDisposition.PreservePeerOrder",
            StringComparison.Ordinal);
        int flagWrite = clear.IndexOf("IsAtDesktopLayer = true;", StringComparison.Ordinal);
        Assert.True(guard >= 0 && flagWrite > guard,
            "the bedded-flag write must live inside the disposition guard");
    }

    [Fact]
    public void InteractionLeakWatchdog_ResetsOnlyAfterForegroundExemptionsExpire()
    {
        string source = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Services/WidgetManager.ZOrder.cs"));
        string tick = SliceMethod(
            source,
            "private void InteractionLeakWatchdog_Tick(",
            "// ── Desktop-pinned global bedding self-heal");
        string sessionManager = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Services/WidgetSessionManager.cs"));

        // First real implementation (2026-10-17) of the watchdog this
        // contract has documented since 2026-07-24: a leaked interaction
        // depth is self-sustaining, only an outside observer breaks it.
        Assert.Contains("ForceResetInteractions", sessionManager, StringComparison.Ordinal);
        Assert.Contains("ForceResetInteractions", tick, StringComparison.Ordinal);
        // The old design claim "real interactions always have a DeskBox
        // foreground" is false in the pinned band: taskbar foregrounds and
        // proxy-hosted native menu rounds are legal mid-interaction.
        Assert.Contains("IsDeskBoxForegroundWindow(foreground)", tick, StringComparison.Ordinal);
        Assert.Contains("IsTaskbarWindow(foreground)", tick, StringComparison.Ordinal);
        Assert.Contains("IsMenuRoundInFlight", tick, StringComparison.Ordinal);
        // Pinned suppressed-click interactions outlast the dynamic threshold
        // (full ternary pin — a flattened single threshold must not pass).
        Assert.Contains(
            "TimeSpan threshold = WidgetLayerService.UsesDesktopPinnedMode()",
            tick,
            StringComparison.Ordinal);
        Assert.Contains(": InteractionLeakThreshold;", tick, StringComparison.Ordinal);
        // Exemptions must refresh the observation clock, or the elapsed time
        // keeps accruing across legal DeskBox/taskbar/menu foregrounds.
        int refresh = tick.IndexOf(
            "_interactionLeakObservedUtc = DateTime.UtcNow;",
            StringComparison.Ordinal);
        int elapsed = tick.IndexOf(
            "TimeSpan elapsed = DateTime.UtcNow - _interactionLeakObservedUtc;",
            StringComparison.Ordinal);
        Assert.True(refresh >= 0 && elapsed > refresh,
            "exemption refresh must precede the elapsed computation");
        Assert.Contains("[TrayBatch] Interaction watchdog", tick, StringComparison.Ordinal);
        // A stale End from an interaction cleared by the force reset must
        // not zero a NEW interaction opened inside the grace window.
        Assert.Contains(
            "_forceResetInteractionsUtc < StaleEndGraceAfterForceReset",
            sessionManager,
            StringComparison.Ordinal);
        Assert.Contains(
            "_forceResetInteractionsUtc = DateTime.UtcNow;",
            sessionManager,
            StringComparison.Ordinal);
        // T2 (2026-10-18): a swallowed End parks the depth at one; the tick
        // must reconcile it (with foreign foreground) before judging a leak,
        // and the swallow itself must be visible in the default log.
        Assert.Contains("ReconcilePostResetDepth", tick, StringComparison.Ordinal);
        Assert.Contains("foregroundConsistentWithNoLiveInteraction", tick, StringComparison.Ordinal);
        int reconcile = tick.IndexOf("ReconcilePostResetDepth", StringComparison.Ordinal);
        int threshold = tick.IndexOf("TimeSpan threshold = ", StringComparison.Ordinal);
        Assert.True(reconcile >= 0 && threshold > reconcile,
            "depth reconciliation must run before the leak-threshold judgment");
        Assert.Contains("ReconcilePostResetDepth", sessionManager, StringComparison.Ordinal);
        Assert.Contains("_logImportant ?? _log", sessionManager, StringComparison.Ordinal);
    }

    [Fact]
    public void LayerRefreshDuringToggle_IsDeferredNeverDropped()
    {
        string source = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Services/WidgetManager.cs"));
        string refresh = SliceMethod(
            source,
            "public void RefreshVisibleWidgetDesktopLayers(",
            "private void ApplyAppearancePreview()");

        // T3 (2026-10-18): force-restoring visible windows mid-animation
        // dismantles the band being raised; this path is also the only
        // immediate re-layerer for visible windows on QR→dynamic switches,
        // so a deferral during the toggle must re-invoke, not drop.
        Assert.Contains("_isTogglingWidgetsDesktopLayer", refresh, StringComparison.Ordinal);
        int deferredCall = refresh.IndexOf(
            "RefreshVisibleWidgetDesktopLayers(reason);",
            refresh.IndexOf("_isTogglingWidgetsDesktopLayer", StringComparison.Ordinal),
            StringComparison.Ordinal);
        Assert.True(deferredCall > 0,
            "the deferred branch must re-invoke the refresh after the retry delay");
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
