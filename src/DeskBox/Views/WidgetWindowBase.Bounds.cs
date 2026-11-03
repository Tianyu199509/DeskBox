// Copyright (c) DeskBox. All rights reserved.

using DeskBox.Helpers;
using DeskBox.Models;
using DeskBox.Platform;
using DeskBox.Services;
using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using System.Runtime.CompilerServices;
using Windows.Graphics;
using WinRT;
using WinRT.Interop;

namespace DeskBox.Views;

public abstract partial class WidgetWindowBase
{
    private bool _desktopPinnedInputActivationInProgress;
    private IntPtr _lastCornerPreferenceHwnd;
    private int _lastAppliedWindowCornerPreference = -1;

    // WM_MOUSEACTIVATE suppression runs before the same click is routed to
    // XAML, so without this stamp one suppressed click would run the bottom
    // reassert twice (native subclass + routed handler). The guard window
    // only needs to cover the native-message → XAML-dispatch hop of a single
    // click; anything older is a genuinely new click.
    private long _desktopPinnedNativeSuppressionTimestamp;
    private long _lastDirectMouseActivateTick;
    private static readonly long DesktopPinnedNativeSuppressionWindowTicks =
        TimeSpan.FromMilliseconds(150).Ticks;

    protected void ConfigureWindowCore()
    {
        if (!_isTrackedForDiagnostics)
        {
            PerformanceLogger.TrackWindowOpen(LogPrefix);
            _isTrackedForDiagnostics = true;
        }

        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.SetBorderAndTitleBar(false, false);
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
        }

        int exStyle = Win32Helper.GetWindowLong(HWnd, Win32Helper.GWL_EXSTYLE);
        exStyle |= Win32Helper.WS_EX_TOOLWINDOW;
        Win32Helper.SetWindowLong(HWnd, Win32Helper.GWL_EXSTYLE, exStyle);

        InstallDesktopPinnedActivationGuard();
        InstallDesktopPinnedPointerRouting();
        ConfigureWindowExtra();

        int style = Win32Helper.GetWindowLong(HWnd, Win32Helper.GWL_STYLE);
        style &= ~(Win32Helper.WS_CAPTION | Win32Helper.WS_BORDER | Win32Helper.WS_DLGFRAME | Win32Helper.WS_THICKFRAME);
        Win32Helper.SetWindowLong(HWnd, Win32Helper.GWL_STYLE, style);
        Win32Helper.SetWindowPos(
            HWnd,
            IntPtr.Zero,
            0, 0, 0, 0,
            Win32Helper.SWP_NOMOVE | Win32Helper.SWP_NOSIZE | Win32Helper.SWP_NOACTIVATE | Win32Helper.SWP_FRAMECHANGED);

        WindowShellState.TryHideFromSwitchers(AppWindow);
        ExtendsContentIntoTitleBar = false;

        var config = Config;
        // Use center point for consistent monitor determination.
        var initBounds = new RectInt32(
            (int)Math.Round(config.X),
            (int)Math.Round(config.Y),
            (int)Math.Round(config.Width),
            (int)Math.Round(config.Height));



        // The display-area lookup can come back empty while the topology is in
        // flux; falling back to the configured bounds keeps the widget instead
        // of failing its construction.



        bool placementFellBack = false;
        RectInt32 bounds;
        try
        {
            bounds = WidgetPositioningService.ResolveBoundsForCurrentTopology(config, out placementFellBack);
        }
        catch
        {
            bounds = initBounds;
            placementFellBack = true;
        }





        bounds = ExpandContentBoundsToHost(bounds);
        ApplyWindowBounds(bounds.X, bounds.Y, bounds.Width, bounds.Height, persist: false, updateConfig: !placementFellBack);

        ApplyDwmBorderStyle(RootElement.ActualTheme == ElementTheme.Dark);
        ApplyWindowCornerPreference();
        ApplyWidgetForegroundAppearance();
        Win32Helper.EnsureSystemDispatcherQueue();
        Win32Helper.ApplyFullWindowFrame(HWnd);
        ApplyBackdropPreference();
        InitializeWidgetCollapse();
        InitializeWidgetGrouping();
        WidgetShellControl.HostedContentChanged -= WidgetShellControl_HostedContentChanged;
        WidgetShellControl.HostedContentChanged += WidgetShellControl_HostedContentChanged;

        RootElement.Loaded += (_, _) =>
        {
            AttachXamlRootScaleWatcher();
            _ = TryRestoreBoundsForCurrentTopology(allowHidden: true, updateConfig: false);
            OnRootElementLoaded();
            SettleCompactBoundsAfterHostShown();
            ApplyWidgetForegroundAppearance();
            ApplyBackdropPreference();
            Win32Helper.ApplyFullWindowFrame(HWnd);
            if (SupportsBackdropRefresh)
            {
                QueueBackdropRefresh();
            }
        };
        RootElement.ActualThemeChanged += (_, _) =>
        {
            ApplyWidgetForegroundAppearance();
            ApplyBackdropPreference();
            OnRootElementThemeChanged();
        };
    }

    private void InstallDesktopPinnedActivationGuard()
    {
        _desktopPinnedActivationSubclassProc ??= CallbackDepth.WrapSubclassProc(DesktopPinnedActivationSubclassProc);
        if (_isDesktopPinnedActivationSubclassInstalled)
        {
            return;
        }

        _isDesktopPinnedActivationSubclassInstalled = Win32Helper.SetWindowSubclass(
            HWnd,
            _desktopPinnedActivationSubclassProc,
            DesktopPinnedActivationSubclassId,
            UIntPtr.Zero);
        App.LogVerbose(
            $"[ZOrder] {LogPrefix} desktop-pinned activation guard installed=" +
            $"{_isDesktopPinnedActivationSubclassInstalled} hwnd=0x{HWnd.ToInt64():X}");
    }

    private void RemoveDesktopPinnedActivationGuard()
    {
        if (!_isDesktopPinnedActivationSubclassInstalled ||
            _desktopPinnedActivationSubclassProc is null)
        {
            return;
        }

        _ = Win32Helper.RemoveWindowSubclass(
            HWnd,
            _desktopPinnedActivationSubclassProc,
            DesktopPinnedActivationSubclassId);
        _isDesktopPinnedActivationSubclassInstalled = false;
    }

    private void InstallDesktopPinnedPointerRouting()
    {
        _desktopPinnedPointerPressedHandler ??=
            RootElement_PointerPressedForDesktopPinnedLayer;
        RootElement.AddHandler(
            UIElement.PointerPressedEvent,
            _desktopPinnedPointerPressedHandler,
            handledEventsToo: true);
        RootElement.GotFocus -= RootElement_GotFocusForDesktopPinnedLayer;
        RootElement.GotFocus += RootElement_GotFocusForDesktopPinnedLayer;
        Activated -= WidgetWindowBase_ActivatedForDesktopPinnedLayer;
        Activated += WidgetWindowBase_ActivatedForDesktopPinnedLayer;
    }

    private void RemoveDesktopPinnedPointerRouting()
    {
        if (_desktopPinnedPointerPressedHandler is not null)
        {
            RootElement.RemoveHandler(
                UIElement.PointerPressedEvent,
                _desktopPinnedPointerPressedHandler);
            _desktopPinnedPointerPressedHandler = null;
        }

        RootElement.GotFocus -= RootElement_GotFocusForDesktopPinnedLayer;
        Activated -= WidgetWindowBase_ActivatedForDesktopPinnedLayer;
    }

    private void RootElement_PointerPressedForDesktopPinnedLayer(
        object sender,
        PointerRoutedEventArgs args)
    {
        App.NotifyMemoryCleanupActivity();
        if (!WidgetLayerService.UsesDesktopPinnedMode())
        {
            return;
        }

        bool isKeyboardInput = IsKeyboardInputTarget(args.OriginalSource);
        bool allowActivation;
        string reason;
        if (isKeyboardInput)
        {
            WidgetLayerService.PrepareForDesktopPinnedKeyboardInput(HWnd);
            allowActivation = true;
            reason = "routed-keyboard-input";
        }
        else
        {
            allowActivation =
                WidgetLayerService.TryAllowDesktopPinnedPointerActivation(HWnd);
            reason = allowActivation
                ? "routed-pointer"
                : "routed-pointer-suppressed";
        }

        if (allowActivation)
        {
            DeferDesktopPinnedActivation(reason);
        }
        else if (WasRecentlyNativeSuppressed())
        {
            // The WM_MOUSEACTIVATE subclass already scheduled the desktop
            // bottom reassert for this exact click; the routed event is the
            // same press, not a new one.
        }
        else
        {
            DeferDesktopPinnedRestore(reason);
        }
    }

    private bool WasRecentlyNativeSuppressed()
    {
        long timestamp = _desktopPinnedNativeSuppressionTimestamp;
        return timestamp != 0 &&
            DateTime.UtcNow.Ticks - timestamp < DesktopPinnedNativeSuppressionWindowTicks;
    }

    private void RootElement_GotFocusForDesktopPinnedLayer(
        object sender,
        RoutedEventArgs args)
    {
        if (!WidgetLayerService.UsesDesktopPinnedMode() ||
            _desktopPinnedInputActivationInProgress ||
            !IsKeyboardInputTarget(args.OriginalSource))
        {
            return;
        }

        // Buttons that open an editor focus it programmatically after their
        // click event. That focus request is also an explicit text-entry action.
        WidgetLayerService.PrepareForDesktopPinnedKeyboardInput(HWnd);
        DeferDesktopPinnedActivation("keyboard-focus");
    }

    private static bool IsKeyboardInputTarget(object? source)
    {
        DependencyObject? current = source as DependencyObject;
        while (current is not null)
        {
            if (current is TextBox or
                RichEditBox or
                PasswordBox or
                AutoSuggestBox or
                NumberBox)
            {
                return true;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return false;
    }

    private void ActivateDesktopPinnedWindow(string reason)
    {
        if (_desktopPinnedInputActivationInProgress)
        {
            return;
        }

        _desktopPinnedInputActivationInProgress = true;
        try
        {
            IntPtr foreground = Win32Helper.GetForegroundWindow();
            if (foreground != HWnd)
            {
                // 454: after "show desktop" the shell owns the foreground, and
                // activating this DefView-owned HWND promotes the whole owner
                // band — the hand-off that can drag a minimized borderless
                // fullscreen application back to the front. A pointer click
                // does not need that promotion: the mouse message is already
                // delivered (MA_NOACTIVATE semantics), and keyboard-oriented
                // activations still take the full path below.
                bool pointerOriginOnDesktopForeground =
                    string.Equals(reason, "routed-pointer", StringComparison.Ordinal) &&
                    IsDesktopShellRootWindow(foreground);
                if (!pointerOriginOnDesktopForeground)
                {
                    // Activation is retained for keyboard-oriented controls, but
                    // the HWND is returned to the desktop bottom before this input
                    // dispatch completes, so activation never creates a layer lease.
                    base.Activate();
                    _ = Win32Helper.SetForegroundWindow(HWnd);
                }
            }
        }
        finally
        {
            _desktopPinnedInputActivationInProgress = false;
        }

        RestoreDesktopPinnedBottomState(reason);
        // The deliberate activation promoted the whole DefView owner band;
        // drop the siblings back under covering applications in this same
        // cycle so only the interacting window stays up (#574).
        App.Current.WidgetManager?.RebedPinnedSiblingsAfterActivation(
            HWnd,
            $"{reason}-siblings");
    }

    private static bool IsDesktopShellRootWindow(IntPtr windowHandle)
    {
        if (windowHandle == IntPtr.Zero)
        {
            return false;
        }

        return DesktopBlankHitTest.IsVerifiedDesktopShellWindow(
            windowHandle,
            allowDefView: true);
    }

    private void WidgetWindowBase_ActivatedForDesktopPinnedLayer(
        object sender,
        WindowActivatedEventArgs args)
    {
        if (!WidgetLayerService.UsesDesktopPinnedMode())
        {
            return;
        }

        // Capture the in-flight flag at event time: when the deliberate
        // activation path raises this event synchronously the flag is true,
        // and that path's own restore/rebed already covers this activation.
        bool deliberateActivationInFlight = _desktopPinnedInputActivationInProgress;
        WindowActivationState state = args.WindowActivationState;
        // The bottom restore and the sibling re-bed must land in the same
        // dispatcher turn (#574: the group must never linger above covering
        // applications between the two), so they defer together as one unit.
        UiDispatch.PostAfterCallback(() =>
        {
            // IsDragging alone must NOT gate this unit: it is set
            // synchronously inside the same press dispatch (BeginWindowDragCore
            // runs right after the enqueue), so a bare check would kill every
            // deferred unit raised by this click. Gate on real movement
            // instead — owner surgery while the window is actually moving is
            // the hazard, not the press-and-hold.
            if (!Win32Helper.IsWindowInCurrentProcess(HWnd) ||
                deliberateActivationInFlight ||
                (IsDragging && HasMovedTitleBarDrag))
            {
                return;
            }

            RestoreDesktopPinnedBottomState($"window-{state}");
            // A native activation also promoted the whole owner band; re-bed
            // the siblings in this same landing turn so the group never
            // lingers above covering applications (#574).
            if (state == WindowActivationState.CodeActivated)
            {
                App.Current.WidgetManager?.RebedPinnedSiblingsAfterActivation(
                    HWnd,
                    "window-CodeActivated-siblings");
            }
        });
    }

    /// <summary>
    /// Defers the whole activation unit (busy flag, activation transaction,
    /// bedding restore, sibling re-bed) off the current callback frame —
    /// input-callback stacks must not run window surgery (drag-freeze
    /// family). The foreground snapshot guard drops the unit if focus moved
    /// to any other window between the callback and the landing turn: a late
    /// activation must never steal a foreground the user has since given
    /// away, and the in-method foreground check cannot see that on its own.
    /// </summary>
    private void DeferDesktopPinnedActivation(string reason)
    {
        IntPtr foregroundAtEnqueue = Win32Helper.GetForegroundWindow();
        UiDispatch.PostAfterCallback(() =>
        {
            if (!Win32Helper.IsWindowInCurrentProcess(HWnd) ||
                (IsDragging && HasMovedTitleBarDrag))
            {
                return;
            }

            IntPtr foregroundNow = Win32Helper.GetForegroundWindow();
            if (foregroundNow != HWnd && foregroundNow != foregroundAtEnqueue)
            {
                App.LogVerbose(
                    $"[ZOrder] {LogPrefix} deferred activation dropped " +
                    $"reason={reason} foreground moved on " +
                    $"hwnd=0x{HWnd.ToInt64():X}");
                return;
            }

            ActivateDesktopPinnedWindow(reason);
        });
    }

    /// <summary>Defers the bedding repair off the current callback frame with
    /// window-liveness and drag guards; the decision that led here stays
    /// synchronous. The drag guard matters because WM_MOUSEACTIVATE arrives
    /// synchronously inside the same WM_LBUTTONDOWN dispatch that starts a
    /// title-bar drag — the deferred unit can otherwise land mid-drag and
    /// re-parent a window the user is moving.</summary>
    private void DeferDesktopPinnedRestore(string reason)
    {
        UiDispatch.PostAfterCallback(() =>
        {
            // Drag guard gates on real movement (see the Activated handler):
            // WM_MOUSEACTIVATE arrives inside the same WM_LBUTTONDOWN
            // dispatch that starts a title-bar drag, and IsDragging is true
            // before this unit can land.
            if (Win32Helper.IsWindowInCurrentProcess(HWnd) &&
                !(IsDragging && HasMovedTitleBarDrag))
            {
                RestoreDesktopPinnedBottomState(reason);
            }
        });
    }

    private void RestoreDesktopPinnedBottomState(string reason)
    {
        if (_expandedWidgetLayerLeaseGeneration != 0)
        {
            // The expanded capsule owns the top of the desktop band until its
            // collapse re-beds the window; a bottom reassert here would bury
            // it beneath sibling widgets mid-use.
            return;
        }

        if (!WidgetTemporaryRaiseLeasePolicy.CanRestoreDesktopLayer(
                Visible,
                IsHideAnimationRunning,
                IsClosing))
        {
            CancelPendingDesktopLayerRestore();
            App.LogVerbose(
                $"[ZOrder] {LogPrefix} fixed-layer bottom reassert skipped " +
                $"reason={reason} visible={Visible} " +
                $"hideRunning={IsHideAnimationRunning} closing={IsClosing} " +
                $"hwnd=0x{HWnd.ToInt64():X}");
            return;
        }

        // This path only repairs owner/Z-order state for an already visible
        // widget. Never let that repair carry SWP_SHOWWINDOW: Explorer can
        // deliver activation changes while a desktop drag is in progress, and
        // a hidden desktop-owned widget must stay hidden.
        bool movedToBottom = WidgetLayerService.MoveToDesktopBottom(HWnd, showWindow: false);
        IsAtDesktopLayer = true;
        IsRaisedFromManager = false;
        KeepRaisedUntilDeactivate = false;
        RestoreDesktopLayerWhenIdle = false;
        TopMostSafetyTimer?.Stop();
        // Only reorder peers when Z-order actually moved: an already-bedded
        // suppressed click short-circuits inside MoveToDesktopBottom and must
        // not schedule a delayed 120ms normalization pass.
        if (movedToBottom)
        {
            App.Current.WidgetManager?.ApplyImmediateRestingPeerOrderAfterRestore(
                $"{LogPrefix}-pinned-bottom-reasserted");
        }
        App.LogVerbose(
            $"[ZOrder] {LogPrefix} fixed-layer bottom reasserted reason={reason} " +
            $"hwnd=0x{HWnd.ToInt64():X}");
    }

    private IntPtr DesktopPinnedActivationSubclassProc(
        IntPtr hWnd,
        uint message,
        UIntPtr wParam,
        IntPtr lParam,
        UIntPtr subclassId,
        UIntPtr referenceData)
    {
        if (message == Win32Helper.WM_MOUSEACTIVATE &&
            WidgetLayerService.ShouldSuppressPointerActivation(hWnd))
        {
            // MA_NOACTIVATE keeps the existing foreground application active
            // but still lets the widget receive the mouse message. The stamp
            // lets the XAML routed handler recognize this same click and skip
            // its own duplicate reassert; the stamp must be written in this
            // frame because the routed event is dispatched right after the
            // native message. The bedding surgery itself (owner rewrite +
            // HWND_BOTTOM) is window work and runs one dispatcher turn later,
            // off the win32k callback frame (drag-freeze family).
            _desktopPinnedNativeSuppressionTimestamp = DateTime.UtcNow.Ticks;
            UiDispatch.PostAfterCallback(() =>
            {
                NativeTrayIconHost.DismissActiveMenuIfOpen();
                TrayContextMenuWindow.DismissActiveIfOpen();
                if (Win32Helper.IsWindowInCurrentProcess(hWnd) &&
                    !(IsDragging && HasMovedTitleBarDrag))
                {
                    RestoreDesktopPinnedBottomState("native-pointer-suppressed");
                }
            });
            return new IntPtr(Win32Helper.MA_NOACTIVATE);
        }

        if (message == Win32Helper.WM_MOUSEACTIVATE)
        {
            _lastDirectMouseActivateTick = Environment.TickCount64;
            WidgetLayerService.RecordWidgetMouseActivate();
            if (WidgetLayerService.ShouldPreserveQuickRevealActivatingClick())
            {
                // Quick Reveal activates only the highest widget. Explicitly keep
                // the activating mouse message for every other revealed HWND so a
                // stack, button, or item responds to the first click.
                return new IntPtr(Win32Helper.MA_ACTIVATE);
            }
        }

        if (message == Win32Helper.WM_WINDOWPOSCHANGING &&
            Win32Helper.TryReadWindowPos(lParam, out Win32Helper.WINDOWPOS windowPos) &&
            WidgetLayerService.ShouldSuppressUnsolicitedZOrderChange(
                hWnd,
                windowPos.hwndInsertAfter,
                windowPos.flags,
                IsAtDesktopLayer,
                _expandedWidgetLayerLeaseGeneration != 0,
                IsDragging || IsResizing || IsBoundsTransitionActive,
                App.Current?.WidgetManager?.WidgetsRaisedFromTray == true,
                _lastDirectMouseActivateTick,
                out bool mayBeDynamicDesktopClick))
        {
            Win32Helper.SuppressWindowPosZOrder(lParam, windowPos);
            if (mayBeDynamicDesktopClick && Win32Helper.GetCursorPos(out Win32Helper.POINT clickPt))
            {
                App.Current?.WidgetManager?.NotifyPotentialDynamicDesktopClick(clickPt);
            }
        }

        return Win32Helper.DefSubclassProc(hWnd, CreateObservedCompactSubclassMessage(hWnd, message), wParam, lParam);
    }

    // ── Bounds management ──────────────────────────────────────

    public void BeginDisplayTopologyTransition(long generation)
    {
        _displayTopologyTransitionGeneration = generation;
        _isDisplayTopologyTransitionActive = true;
    }

    public void EndDisplayTopologyTransition(long generation)
    {
        if (_displayTopologyTransitionGeneration != generation)
        {
            return;
        }

        _isDisplayTopologyTransitionActive = false;
    }

    protected bool CanPersistBoundsChange(bool requested) =>
        requested && !_isDisplayTopologyTransitionActive;

    private void AttachXamlRootScaleWatcher()
    {
        XamlRoot? xamlRoot = RootElement.XamlRoot;
        if (xamlRoot is null || ReferenceEquals(_observedXamlRoot, xamlRoot))
        {
            return;
        }

        DetachXamlRootScaleWatcher();
        _observedXamlRoot = xamlRoot;
        _observedRasterizationScale = xamlRoot.RasterizationScale;
        xamlRoot.Changed += ObservedXamlRoot_Changed;
    }

    private void DetachXamlRootScaleWatcher()
    {
        if (_observedXamlRoot is not null)
        {
            _observedXamlRoot.Changed -= ObservedXamlRoot_Changed;
            _observedXamlRoot = null;
        }

        _observedRasterizationScale = 0;
    }

    private void ObservedXamlRoot_Changed(XamlRoot sender, XamlRootChangedEventArgs args)
    {
        double scale = sender.RasterizationScale;
        if (!double.IsFinite(scale) || scale <= 0 ||
            Math.Abs(scale - _observedRasterizationScale) < 0.001)
        {
            return;
        }

        double previous = _observedRasterizationScale;
        _observedRasterizationScale = scale;
        App.LogVerbose(
            $"[DisplayTopology] {LogPrefix} XamlRoot scale {previous:F3}->{scale:F3}");
        App.Current?.RequestDisplayTopologyRestore("xaml-root-scale");
    }

    protected void ApplyWindowBounds(int x, int y, int width, int height, bool persist, bool updateConfig = true)
    {
        if (_isDisplayTopologyTransitionActive)
        {
            persist = false;
            updateConfig = false;
        }

        if (!IsDragging &&
            !IsCompactBoundsStateActive &&
            !UsesCompactExpansionGeometry())
        {
            SizeInt32 minSize = IsResizing && _interactiveResizeMinimumSize.Width > 0
                ? _interactiveResizeMinimumSize
                : GetPhysicalMinimumWindowSize(x, y, width, height);
            width = Math.Max(minSize.Width, width);
            height = Math.Max(minSize.Height, height);
        }

        IsApplyingBounds = true;
        try
        {
            var bounds = new RectInt32(x, y, width, height);
            if (IsCompactBoundsStateActive ||
                !WindowsCompatibilityService.IsWindows11OrLater)
            {
                uint flags = Win32Helper.SWP_NOZORDER | Win32Helper.SWP_NOACTIVATE;
                if (IsResizing && !WindowsCompatibilityService.IsWindows11OrLater)
                {
                    flags |= Win32Helper.SWP_NOCOPYBITS | Win32Helper.SWP_DEFERERASE;
                }

                bool moved = Win32Helper.SetWindowPos(
                    HWnd,
                    IntPtr.Zero,
                    bounds.X,
                    bounds.Y,
                    bounds.Width,
                    bounds.Height,
                    flags);
                if (!moved)
                {
                    AppWindow.MoveAndResize(bounds);
                }
            }
            else
            {
                AppWindow.MoveAndResize(bounds);
            }
        }
        finally
        {
            IsApplyingBounds = false;
        }

        if (persist)
        {
            CapturePositionAnchor(x, y, width, height);
            UpdateConfigBoundsFromPhysical(x, y, width, height, persist: true);
            return;
        }

        if (updateConfig)
        {
            // Restore paths may only refresh the physical X/Y cache (spec
            // 5.2): width/height are placement intent and must survive
            // topology repositioning untouched, so the write is physical-
            // position-only here.
            UpdateConfigPhysicalPositionOnly(x, y);
        }
    }

    private void UpdateConfigPhysicalPositionOnly(int x, int y)
    {
        Config.X = x;
        Config.Y = y;
    }

    protected SizeInt32 GetPhysicalMinimumWindowSize(int x, int y, int width, int height)
    {
        return WidgetPositioningService.GetPhysicalMinimumSizeForBounds(
            new RectInt32(x, y, Math.Max(1, width), Math.Max(1, height)));
    }

    protected void CapturePositionAnchor(
        int x,
        int y,
        int width,
        int height,
        bool preserveCurrentEdge = false)
    {
        var bounds = CollapseHostBoundsToContent(
            new RectInt32(x, y, width, height));
        if (IsCompactBoundsStateActive)
        {
            CaptureCompactPlacement(new RectInt32(x, y, width, height), persist: false);
            return;
        }

        // Use the window center point to determine the owning display.
        // This prevents incorrect anchor capture when the window straddles
        // two monitors during a cross-screen drag.
        var center = new PointInt32(
            bounds.X + Math.Max(1, bounds.Width) / 2,
            bounds.Y + Math.Max(1, bounds.Height) / 2);
        var workArea = DisplayArea.GetFromPoint(center, DisplayAreaFallback.Nearest).WorkArea;
        Config.BoundsCoordinateVersion = WidgetConfig.CurrentBoundsCoordinateVersion;
        if (preserveCurrentEdge)
        {
            WidgetPositioningService.CaptureAnchorPreservingCurrentEdge(
                Config,
                bounds,
                workArea);
        }
        else
        {
            WidgetPositioningService.CaptureAnchor(Config, bounds, workArea);
        }
    }

    /// <summary>
    /// Unified user-placement commit (spec 5.4): captures the placement with
    /// max-intersection display ownership, updates the surface's home display
    /// ("放哪属于哪"), and marks the profile entry authoritative. Expanded
    /// state captures the main anchor; compact state keeps the capsule
    /// capture and only commits home/entry.
    /// </summary>
    protected void CommitUserPlacementFromWindow(WidgetPlacementSource source)
    {
        WidgetManager? manager = App.Current?.WidgetManager;
        if (manager is null)
        {
            return;
        }

        RectInt32 actual = GetActualWindowBounds();
        if (IsCompactBoundsStateActive)
        {
            CaptureCompactPlacement(actual, persist: false);
            manager.CommitUserPlacement(Config, actual, actual, source, captureIntent: false);
            return;
        }

        manager.CommitUserPlacement(
            Config,
            CollapseHostBoundsToContent(actual),
            actual,
            source);
    }

    // ── Display change restoration ─────────────────────────────

    protected bool TryRestoreBoundsForCurrentTopology(bool allowHidden, bool updateConfig = true)
    {
        if (IsClosing || IsHideAnimationRunning)
        {
            return true;
        }

        if (!allowHidden && !Visible)
        {
            return true;
        }

        if (IsDragging || IsResizing || TrayAnimation.IsPositionTransitionActive)
        {
            return false;
        }

        var bounds = ResolveWidgetBoundsForCurrentState(out bool placementFellBack);
        RectInt32 actual = GetActualWindowBounds();
        if (actual.X == bounds.X &&
            actual.Y == bounds.Y &&
            actual.Width == bounds.Width &&
            actual.Height == bounds.Height)
        {
            return true;
        }

        // A fallback resolve (shelter geometry from EnsureVisible) parks the
        // window somewhere visible but is NOT user intent — feeding it into
        // the config's physical cache would let the fallback masquerade as
        // a placement and win later arbitrations (2026-10-20 lineage).
        ApplyWindowBounds(
            bounds.X,
            bounds.Y,
            bounds.Width,
            bounds.Height,
            persist: false,
            updateConfig: updateConfig && !placementFellBack);
        return true;
    }

    protected void RestoreBoundsAfterDisplayChange()
    {
        App.Current?.RequestDisplayTopologyRestore("widget-window-message");
    }

    public void RestoreBoundsForCurrentTopology()
    {
        // A programmatic re-resolve must not trust the memoized capsule
        // placement: screen-binding changes resolve a different monitor, and
        // the stale stable-bounds memo would silently pin the collapsed
        // capsule to its old screen (the pin-all batch missed collapsed
        // widgets through exactly this path).
        InvalidateStableCompactBounds();
        _ = TryRestoreBoundsForCurrentTopology(allowHidden: true);
    }

    public bool TryRestoreBoundsForDisplayTopology()
    {
        InvalidateStableCompactBounds();
        bool restored = TryRestoreBoundsForCurrentTopology(
            allowHidden: true,
            updateConfig: false);
        return restored;
    }

    // ── AppWindow change handling ──────────────────────────────

    /// <summary>
    /// True while any bounds write or position transition is still in flight.
    /// Z-order normalization uses this to avoid sorting on transient bounds
    /// that would produce a second, different order once the window settles.
    /// </summary>
    public bool IsBoundsTransitionActive =>
        IsApplyingBounds ||
        TrayAnimation.IsApplyingBounds ||
        TrayAnimation.IsPositionTransitionActive;

    protected void OnAppWindowChanged(AppWindow sender, AppWindowChangedEventArgs args)
    {
        if (args.DidVisibilityChange)
        {
            NotifyCompactHostVisibilityChanged(sender.IsVisible);
            App.Current?.WidgetManager?
                .ReconcileBackgroundMemoryCleanupForWidgetVisibility(
                    sender.IsVisible
                        ? "native-window-shown"
                        : "native-window-hidden",
                    observedNativeVisibility: sender.IsVisible);
        }

        // Panorama backgrounds sample the slice under the window, so they
        // must follow drags and resizes in real time — this runs before the
        // drag/resize config guards below on purpose.
        if ((args.DidPositionChange || args.DidSizeChange) &&
            !_isCollapseAnimationRendering)
        {
            UpdateCustomPanoramaViewport();
        }

        if (IsApplyingBounds ||
            TrayAnimation.IsApplyingBounds ||
            _deferTitleBarDragConfigUpdates ||
            _deferInteractiveResizeConfigUpdates ||
            (!IsDragging && !IsResizing))
        {
            return;
        }

        if (args.DidPositionChange || args.DidSizeChange)
        {
            var pos = AppWindow.Position;
            var size = AppWindow.Size;
            UpdateConfigBoundsFromPhysical(pos.X, pos.Y, size.Width, size.Height, persist: false);
        }
    }

    // ── Window corner & DWM border ─────────────────────────────

    protected void ApplyWindowCornerPreference()
    {
        if (HWnd == IntPtr.Zero)
        {
            return;
        }

        string effectivePreference = WindowsCompatibilityService.ResolveEffectiveWidgetCornerPreference(
            SettingsService.Settings.WidgetCornerPreference);
        int cornerPreference = effectivePreference switch
        {
            SettingsService.WidgetCornerPreferenceSquare => Win32Helper.DWMWCP_DONOTROUND,
            SettingsService.WidgetCornerPreferenceSmall => Win32Helper.DWMWCP_ROUNDSMALL,
            _ => Win32Helper.DWMWCP_ROUND
        };

        if (_lastCornerPreferenceHwnd == HWnd &&
            _lastAppliedWindowCornerPreference == cornerPreference)
        {
            return;
        }

        if (Win32Helper.TrySetDwmWindowAttribute(HWnd, Win32Helper.DWMWA_WINDOW_CORNER_PREFERENCE, ref cornerPreference) == 0)
        {
            _lastCornerPreferenceHwnd = HWnd;
            _lastAppliedWindowCornerPreference = cornerPreference;
        }
    }

    protected void ApplyDwmBorderStyle(bool isDark)
    {
        // Always set DWM border to transparent — the visual border is drawn by XAML
        // BackgroundPlate.BorderBrush which correctly follows the XAML CornerRadius.
        int borderNone = unchecked((int)0xFFFFFFFE);
        Win32Helper.SetWindowBorderColor(HWnd, borderNone);
    }

    protected double GetCornerRadiusFromPreference()
    {
        string effectivePreference = WindowsCompatibilityService.ResolveEffectiveWidgetCornerPreference(
            SettingsService.Settings.WidgetCornerPreference);
        return WidgetCompactBoundsCalculator.ResolveOuterCornerRadius(
            effectivePreference);
    }

    protected double GetCurrentSurfaceCornerRadius()
    {
        if (_isCollapseAnimationRendering)
        {
            return WidgetShellControl.BackgroundSurface.CornerRadius.TopLeft;
        }

        return IsWidgetCollapsedBoundsActive
            ? WidgetCompactBoundsCalculator.ResolveOuterCornerRadius(
                WindowsCompatibilityService.ResolveEffectiveWidgetCornerPreference(
                    SettingsService.Settings.WidgetCornerPreference))
            : GetCornerRadiusFromPreference();
    }

    // ── Backdrop preference ────────────────────────────────────
}
