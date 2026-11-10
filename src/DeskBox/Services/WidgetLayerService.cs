using DeskBox.Helpers;
using DeskBox.Platform;
using System.Runtime.InteropServices;

namespace DeskBox.Services;

/// <summary>
/// Centralizes desktop widget Z-order operations so future layer modes can be
/// implemented without duplicating Win32 calls across each widget window type.
/// </summary>
public static class WidgetLayerService
{
    internal const int StartupDesktopReadyRequiredStableSamples = 5;
    internal const int StartupDesktopReadyMaxProbeAttempts = 48;
    internal static readonly TimeSpan StartupDesktopReadyProbeInterval =
        TimeSpan.FromMilliseconds(250);

    private static readonly object s_desktopLayerLock = new();
    private static readonly Dictionary<IntPtr, DesktopLayerAttachment> s_desktopLayerAttachments = [];
    private static IntPtr s_cachedDesktopIconView;
    private static bool s_startupDesktopLayerAttachmentDeferred;
    [ThreadStatic]
    private static bool t_lastAttachMovedZOrder;
    private static long s_lastAnyWidgetMouseActivateTick;

    internal static void BeginStartupDesktopLayerAttachmentDeferral()
    {
        Volatile.Write(ref s_startupDesktopLayerAttachmentDeferred, true);
        InvalidateDesktopIconViewCache();
        App.Log("[Startup] Explorer desktop-layer attachment deferred without delaying widget restore");
    }

    internal static void EndStartupDesktopLayerAttachmentDeferral()
    {
        Volatile.Write(ref s_startupDesktopLayerAttachmentDeferred, false);
    }

    /// <summary>
    /// Waits until Explorer's existing desktop icon host has remained stable long
    /// enough for login-time icon restoration to finish. This is intentionally
    /// used only for startup launches; normal launches should remain immediate.
    /// </summary>
    internal static async Task<bool> WaitForDesktopIconViewReadyAsync(
        CancellationToken cancellationToken = default)
    {
        var stabilityTracker = new DesktopIconViewStabilityTracker(
            StartupDesktopReadyRequiredStableSamples);

        for (int attempt = 1; attempt <= StartupDesktopReadyMaxProbeAttempts; attempt++)
        {
            IntPtr desktopIconView = FindDesktopIconView();
            if (stabilityTracker.Observe(desktopIconView))
            {
                App.Log(
                    $"[Startup] Explorer desktop layer stable after {attempt} probes " +
                    $"hwnd=0x{desktopIconView.ToInt64():X}");
                return true;
            }

            if (attempt < StartupDesktopReadyMaxProbeAttempts)
            {
                await Task.Delay(StartupDesktopReadyProbeInterval, cancellationToken);
            }
        }

        double maximumWaitMilliseconds =
            StartupDesktopReadyProbeInterval.TotalMilliseconds *
            (StartupDesktopReadyMaxProbeAttempts - 1);
        App.Log(
            $"[Startup] Explorer desktop layer was not stable within " +
            $"{maximumWaitMilliseconds:0}ms; restoring widgets with the safe fallback layer");
        return false;
    }

    public static bool MoveToDesktopBottom(
        IntPtr windowHandle,
        bool showWindow = true)
    {
        ApplyDesktopPinnedActivationStyle(windowHandle);

        // Desktop-pinned mode always rests inside Explorer. Dynamic mode uses
        // the same owner only when the user wants widgets to survive Win+D.
        if (ShouldAttachRestingWindowToDesktop() &&
            TryAttachToDesktopIconLayer(
                windowHandle,
                showWindow: showWindow))
        {
            return t_lastAttachMovedZOrder;
        }

        DetachFromDesktopIconLayerIfNeeded(windowHandle);
        Win32Helper.ClearWindowTopMost(windowHandle);
        Win32Helper.SetWindowToBottom(windowHandle);
        return true;
    }

    internal static IntPtr ClearTopMostPreservingForeground(
        IntPtr windowHandle,
        out RelativeLayerRestoreDisposition disposition)
    {
        ApplyDesktopPinnedActivationStyle(windowHandle);

        if (UsesDesktopPinnedMode())
        {
            if (!TryAttachToDesktopIconLayer(windowHandle))
            {
                MoveToDynamicDesktopBottom(windowHandle);
            }

            disposition = RelativeLayerRestoreDisposition.DesktopBottom;
            return Win32Helper.GetForegroundWindow();
        }

        IntPtr foreground = Win32Helper.GetForegroundWindow();
        IntPtr foregroundRoot = GetForegroundRoot(foreground);
        bool hasForeground =
            foregroundRoot != IntPtr.Zero &&
            Win32Helper.IsWindow(foregroundRoot);
        disposition = RelativeLayerRestorePolicy.Decide(
            hasForeground,
            hasForeground && IsDesktopShellWindow(foregroundRoot),
            foregroundRoot == windowHandle,
            hasForeground && App.Current.IsDeskBoxWindow(foregroundRoot));

        switch (disposition)
        {
            case RelativeLayerRestoreDisposition.DesktopBottom:
                MoveToDesktopBottom(windowHandle);
                break;

            case RelativeLayerRestoreDisposition.PreservePeerOrder:
                DetachFromDesktopIconLayerIfNeeded(windowHandle);
                if (Win32Helper.IsWindowTopMost(windowHandle))
                {
                    Win32Helper.ClearWindowTopMost(windowHandle);
                }
                break;

            case RelativeLayerRestoreDisposition.BehindForeground:
                _ = TryPlaceDynamicWindowBehindForeground(
                    windowHandle,
                    foregroundRoot,
                    "restore");
                break;
        }

        App.LogVerbose(
            $"[ZOrder] Relative restore widget=0x{windowHandle.ToInt64():X} " +
            $"foreground=0x{foregroundRoot.ToInt64():X} disposition={disposition}");

        return foreground;
    }

    /// <summary>
    /// Restores a raised widget group as one Z-order unit. When another
    /// application owns the foreground, the whole group is inserted directly
    /// behind that application instead of being flattened to HWND_BOTTOM.
    /// </summary>
    internal static bool RestoreGroupPreservingForeground(
        IReadOnlyList<IntPtr> windowHandles,
        string reason) =>
        RestoreGroupPreservingForeground(windowHandles, reason, out _);

    internal static bool RestoreGroupPreservingForeground(
        IReadOnlyList<IntPtr> windowHandles,
        string reason,
        out RelativeLayerRestoreDisposition disposition)
    {
        List<IntPtr> handles = windowHandles
            .Where(handle => handle != IntPtr.Zero && Win32Helper.IsWindow(handle))
            .Distinct()
            .ToList();
        if (handles.Count == 0)
        {
            disposition = RelativeLayerRestoreDisposition.DesktopBottom;
            return true;
        }

        if (UsesDesktopPinnedMode())
        {
            disposition = RelativeLayerRestoreDisposition.DesktopBottom;
            bool pinnedApplied = RestoreGroupToDesktopBottomAtomically(
                handles,
                $"pinned-{reason}");
            App.LogVerbose(
                $"[ZOrder] Group restore reason={reason} mode=DesktopPinned " +
                $"count={handles.Count} applied={pinnedApplied}");
            return pinnedApplied;
        }

        IntPtr foreground = Win32Helper.GetForegroundWindow();
        IntPtr foregroundRoot = GetForegroundRoot(foreground);
        bool hasForeground =
            foregroundRoot != IntPtr.Zero &&
            Win32Helper.IsWindow(foregroundRoot);
        disposition = RelativeLayerRestorePolicy.Decide(
            hasForeground,
            hasForeground && IsDesktopShellWindow(foregroundRoot),
            handles.Contains(foregroundRoot),
            hasForeground && App.Current.IsDeskBoxWindow(foregroundRoot));

        bool applied;
        switch (disposition)
        {
            case RelativeLayerRestoreDisposition.DesktopBottom:
                applied = RestoreGroupToDesktopBottomAtomically(handles, reason);
                break;

            case RelativeLayerRestoreDisposition.PreservePeerOrder:
                foreach (IntPtr handle in handles)
                {
                    DetachFromDesktopIconLayerIfNeeded(handle);
                    if (Win32Helper.IsWindowTopMost(handle))
                    {
                        Win32Helper.ClearWindowTopMost(handle);
                    }
                }

                applied = ApplyPeerOrderHighestToLowest(handles);
                break;

            case RelativeLayerRestoreDisposition.BehindForeground:
                foreach (IntPtr handle in handles)
                {
                    PrepareRestingWindowForRelativePlacement(handle);
                }

                IntPtr boundary = Win32Helper.IsWindowTopMost(foregroundRoot)
                    ? Win32Helper.HWND_TOP
                    : foregroundRoot;
                applied = ApplyWindowOrderHighestToLowest(
                    handles,
                    boundary,
                    $"group-restore-{reason}");
                break;

            default:
                applied = false;
                break;
        }

        App.LogVerbose(
            $"[ZOrder] Group restore reason={reason} count={handles.Count} " +
            $"foreground=0x{foregroundRoot.ToInt64():X} " +
            $"disposition={disposition} applied={applied}");
        return applied;
    }

    public static void ClearTopMost(IntPtr windowHandle)
    {
        ApplyDesktopPinnedActivationStyle(windowHandle);

        if (UsesDesktopPinnedMode())
        {
            if (!TryAttachToDesktopIconLayer(windowHandle))
            {
                MoveToDynamicDesktopBottom(windowHandle);
            }

            return;
        }

        MoveToDesktopBottom(windowHandle);
    }

    public static void HoldTemporaryTopMost(
        IntPtr windowHandle,
        bool showWindow = true)
    {
        ApplyDesktopPinnedActivationStyle(windowHandle);

        if (UsesDesktopPinnedMode())
        {
            if (!TryAttachToDesktopIconLayer(
                    windowHandle,
                    showWindow: showWindow))
            {
                MoveToDynamicDesktopBottom(windowHandle, showWindow);
            }

            return;
        }

        DetachFromDesktopIconLayerIfNeeded(windowHandle);
        RaiseWindowToNormalBandFront(windowHandle, showWindow);
    }

    /// <summary>
    /// Raises one dynamically layered widget to the top of the normal band in
    /// a single transaction. The historical implementation pulsed
    /// HWND_TOPMOST followed by HWND_NOTOPMOST: every pulse visited the
    /// topmost band and contested its tail with resident topmost floaters
    /// (IME candidate windows, magnifiers), which both sides read as
    /// flicker (#375). HWND_TOP on a non-topmost window already places it
    /// above every normal application window with the same end state.
    /// </summary>
    private static void RaiseWindowToNormalBandFront(
        IntPtr windowHandle,
        bool showWindow)
    {
        uint flags = Win32Helper.SWP_NOMOVE |
            Win32Helper.SWP_NOSIZE |
            Win32Helper.SWP_NOACTIVATE;
        if (showWindow)
        {
            flags |= Win32Helper.SWP_SHOWWINDOW;
        }

        _ = Win32Helper.SetWindowPos(
            windowHandle,
            Win32Helper.HWND_TOP,
            0,
            0,
            0,
            0,
            flags);
    }

    /// <summary>
    /// Keeps a transient overlay group in the topmost band without activating
    /// any member. Callers must clear the state when the exit animation ends.
    /// The input order is visually highest to lowest.
    /// </summary>
    public static void HoldGroupTopMostWithoutActivation(
        IReadOnlyList<IntPtr> windowHandles)
    {
        List<IntPtr> handles = windowHandles
            .Where(handle => handle != IntPtr.Zero && Win32Helper.IsWindow(handle))
            .Distinct()
            .ToList();

        // Set the lowest peer first so the final call leaves the first input
        // handle visually highest inside the topmost band.
        for (int index = handles.Count - 1; index >= 0; index--)
        {
            IntPtr handle = handles[index];
            ApplyDesktopPinnedActivationStyle(handle);
            DetachFromDesktopIconLayerIfNeeded(handle);
            Win32Helper.SetWindowTopMost(handle);
        }

        App.LogVerbose(
            $"[ZOrder] Topmost overlay group held count={handles.Count}");
    }

    public static void BringToFront(IntPtr windowHandle)
    {
        ApplyDesktopPinnedActivationStyle(windowHandle);

        if (UsesDesktopPinnedMode())
        {
            if (!TryAttachToDesktopIconLayer(windowHandle))
            {
                MoveToDynamicDesktopBottom(windowHandle);
            }

            return;
        }

        DetachFromDesktopIconLayerIfNeeded(windowHandle);
        Win32Helper.BringWindowToFront(windowHandle);
    }

    /// <summary>
    /// Holds a DeskBox-owned auxiliary window (search popup, settings, desktop
    /// organization) in the topmost band above a raised quick-reveal widget
    /// group. The caller owns the release; see <see cref="ReleaseRaisedBandWindow"/>.
    /// </summary>
    internal static void HoldWindowAboveRaisedWidgets(IntPtr windowHandle)
    {
        Win32Helper.SetWindowTopMost(windowHandle);
    }

    /// <summary>
    /// Returns a raised-band guest to the normal Z-order band. When the session
    /// ended because the user activated a foreign application, the guest is
    /// placed directly below that window in a single transaction so it never
    /// covers the app the user just switched to; otherwise it lands at the top
    /// of the normal band as the still-active surface.
    /// </summary>
    internal static void ReleaseRaisedBandWindow(
        IntPtr windowHandle,
        RaisedBandReleasePlacement placement,
        IntPtr foreignForegroundRoot)
    {
        if (placement == RaisedBandReleasePlacement.BelowForeignForeground &&
            foreignForegroundRoot != IntPtr.Zero &&
            Win32Helper.IsWindow(foreignForegroundRoot))
        {
            Win32Helper.SetWindowPos(
                windowHandle,
                foreignForegroundRoot,
                0,
                0,
                0,
                0,
                Win32Helper.SWP_NOMOVE |
                    Win32Helper.SWP_NOSIZE |
                    Win32Helper.SWP_NOACTIVATE |
                    Win32Helper.SWP_NOOWNERZORDER);
            return;
        }

        Win32Helper.ClearWindowTopMost(windowHandle);
    }

    /// <summary>
    /// Raises one widget above its peers without activating it. In desktop-pinned
    /// mode the window remains attached to the desktop icon layer and only its
    /// sibling order changes.
    /// </summary>
    public static void BringAbovePeerWidgets(IntPtr windowHandle)
    {
        if (UsesDesktopPinnedMode())
        {
            MoveToDesktopBottom(windowHandle);
            return;
        }

        DetachFromDesktopIconLayerIfNeeded(windowHandle);
        Win32Helper.BringWindowTemporarilyToFront(windowHandle);
    }

    /// <summary>
    /// Raises a widget only within the Explorer desktop owner group. This is
    /// used by a capsule expanding in place: it must cover sibling widgets,
    /// but it must remain protected from Show Desktop when that preference is
    /// enabled. Tray-raised widgets intentionally use
    /// <see cref="BringAbovePeerWidgets"/> instead so they can appear above
    /// normal application windows.
    /// </summary>
    public static bool TryBringAbovePeerWidgetsAtDesktopLayer(IntPtr windowHandle)
    {
        // Ownership of SHELLDLL_DefView only enforces a minimum position
        // (above the owner); it never confines the window to the owner band.
        // HWND_TOP therefore reaches the top of the whole normal band — above
        // every application — so the expanded capsule is instead inserted
        // directly below the nearest visible foreign window above the group:
        // above sibling widgets, beneath every application (#574).
        if (!ShouldAttachRestingWindowToDesktop() ||
            !TryAttachToDesktopIconLayer(windowHandle, placeAtBottom: false))
        {
            return false;
        }

        return Win32Helper.SetWindowPos(
            windowHandle,
            FindInsertBelowAnchorForDesktopLayerRaise(windowHandle),
            0,
            0,
            0,
            0,
            Win32Helper.SWP_NOMOVE |
                Win32Helper.SWP_NOSIZE |
                Win32Helper.SWP_NOACTIVATE |
                Win32Helper.SWP_NOOWNERZORDER |
                Win32Helper.SWP_SHOWWINDOW);
    }

    /// <summary>
    /// Resolves the z-order ceiling for a desktop-layer peer raise: the
    /// nearest visible foreign window above the widget's group. The returned
    /// handle is used as the SetWindowPos insert-after anchor, so the raised
    /// widget lands directly below it. When no foreign window sits above the
    /// group, HWND_TOP is equivalent because nothing can be jumped over.
    /// DeskBox-owned surfaces, shell desktop windows, and invisible windows
    /// do not constrain the ceiling.
    /// </summary>
    private static IntPtr FindInsertBelowAnchorForDesktopLayerRaise(
        IntPtr windowHandle)
    {
        const int wsMinimizeStyle = 0x20000000;
        IntPtr current = Win32Helper.GetWindow(windowHandle, Win32Helper.GW_HWNDPREV);
        int visited = 0;
        while (current != IntPtr.Zero && visited++ < MaxBeddingWalkWindows)
        {
            if (ClassifyWindowForBedding(current, wsMinimizeStyle) ==
                    DesktopLayerBeddingWindowKind.ForeignAppWindow &&
                !WidgetManager.IsTaskbarWindow(current))
            {
                return current;
            }

            current = Win32Helper.GetWindow(current, Win32Helper.GW_HWNDPREV);
        }

        return Win32Helper.HWND_TOP;
    }

    /// <summary>
    /// Applies and verifies an explicit peer order for an expanded capsule.
    /// A successful SetWindowPos call is not sufficient for windows owned by
    /// Explorer because Windows may still preserve an older owner-group order.
    /// </summary>
    public static bool EnsurePeerOrderHighestToLowest(
        IReadOnlyList<IntPtr> windowHandles)
    {
        List<IntPtr> handles = windowHandles
            .Where(handle => handle != IntPtr.Zero && Win32Helper.IsWindow(handle))
            .Distinct()
            .ToList();
        if (handles.Count < 2)
        {
            return handles.Count == 1;
        }

        IntPtr activeWindow = handles[0];
        _ = ApplyPeerOrderHighestToLowest(handles);
        if (IsHighestPeer(activeWindow, handles))
        {
            return true;
        }

        bool raised = TryBringAbovePeerWidgetsAtDesktopLayer(activeWindow) ||
            TryBringAbovePeerWidgetsBehindForeground(activeWindow);
        if (!raised)
        {
            BringAbovePeerWidgets(activeWindow);
        }

        bool reapplied = ApplyPeerOrderHighestToLowest(handles);
        bool verified = IsHighestPeer(activeWindow, handles);
        App.LogVerbose(
            $"[ZOrder] Expanded peer order fallback " +
            $"active=0x{activeWindow.ToInt64():X} " +
            $"raised={raised} reapplied={reapplied} verified={verified}");
        return verified;
    }

    private static bool IsPeerOrderAlreadyApplied(IReadOnlyList<IntPtr> handles)
    {
        var peers = new HashSet<IntPtr>(handles);
        var peersAboveHighest = new List<IntPtr>();
        IntPtr cursor = Win32Helper.GetWindow(handles[0], Win32Helper.GW_HWNDPREV);
        while (cursor != IntPtr.Zero)
        {
            if (peers.Contains(cursor))
            {
                peersAboveHighest.Add(cursor);
            }

            cursor = Win32Helper.GetWindow(cursor, Win32Helper.GW_HWNDPREV);
        }

        var orderFromHighest = new List<IntPtr>();
        cursor = handles[0];
        while (cursor != IntPtr.Zero)
        {
            if (peers.Contains(cursor))
            {
                orderFromHighest.Add(cursor);
            }

            cursor = Win32Helper.GetWindow(cursor, Win32Helper.GW_HWNDNEXT);
        }

        return IdleWidgetZOrderPolicy.MatchesRequestedOrder(
            handles,
            peersAboveHighest,
            orderFromHighest);
    }

    public static bool IsHighestPeer(
        IntPtr windowHandle,
        IReadOnlyCollection<IntPtr> peerWindowHandles)
    {
        if (windowHandle == IntPtr.Zero || !Win32Helper.IsWindow(windowHandle))
        {
            return false;
        }

        HashSet<IntPtr> peers = peerWindowHandles
            .Where(handle => handle != IntPtr.Zero && handle != windowHandle)
            .ToHashSet();
        IntPtr current = Win32Helper.GetWindow(windowHandle, Win32Helper.GW_HWNDPREV);
        while (current != IntPtr.Zero)
        {
            if (peers.Contains(current))
            {
                return false;
            }

            current = Win32Helper.GetWindow(current, Win32Helper.GW_HWNDPREV);
        }

        return true;
    }

    /// <summary>
    /// Moves a dynamically layered widget above its DeskBox peers without
    /// overtaking the current foreground application. This is used when a
    /// compact widget expands from hover after a tray-raised session has
    /// already returned control to another application.
    /// </summary>
    /// <returns>
    /// <see langword="true"/> when a foreign foreground window was found and
    /// the widget was positioned behind it; otherwise <see langword="false"/>.
    /// </returns>
    public static bool TryBringAbovePeerWidgetsBehindForeground(IntPtr windowHandle)
    {
        if (UsesDesktopPinnedMode() ||
            windowHandle == IntPtr.Zero ||
            !Win32Helper.IsWindow(windowHandle))
        {
            return false;
        }

        IntPtr foregroundRoot = GetForegroundRoot(Win32Helper.GetForegroundWindow());

        if (foregroundRoot == IntPtr.Zero ||
            foregroundRoot == windowHandle ||
            !Win32Helper.IsWindow(foregroundRoot) ||
            App.Current.IsDeskBoxWindow(foregroundRoot) ||
            IsDesktopShellWindow(foregroundRoot))
        {
            return false;
        }

        return TryPlaceDynamicWindowBehindForeground(
            windowHandle,
            foregroundRoot,
            "peer-raise");
    }

    private static bool TryPlaceDynamicWindowBehindForeground(
        IntPtr windowHandle,
        IntPtr foregroundRoot,
        string reason)
    {
        bool attachedToDesktop = PrepareRestingWindowForRelativePlacement(windowHandle);

        // A topmost foreground window already owns the higher Z-order band, so
        // HWND_TOP safely places this non-topmost widget at the head of the
        // normal band. For a normal foreground window, inserting immediately
        // after it keeps that application visibly above the expanding widget.
        IntPtr insertAfter = Win32Helper.IsWindowTopMost(foregroundRoot)
            ? Win32Helper.HWND_TOP
            : foregroundRoot;
        bool moved = Win32Helper.SetWindowPos(
            windowHandle,
            insertAfter,
            0,
            0,
            0,
            0,
            Win32Helper.SWP_NOMOVE |
                Win32Helper.SWP_NOSIZE |
                Win32Helper.SWP_NOACTIVATE |
                Win32Helper.SWP_NOOWNERZORDER |
                Win32Helper.SWP_SHOWWINDOW);
        App.LogVerbose(
            $"[ZOrder] Place behind foreground reason={reason} " +
            $"widget=0x{windowHandle.ToInt64():X} " +
            $"foreground=0x{foregroundRoot.ToInt64():X} " +
            $"desktopAttached={attachedToDesktop} moved={moved}");
        return moved;
    }

    private static bool PrepareRestingWindowForRelativePlacement(IntPtr windowHandle)
    {
        bool attachedToDesktop =
            TryAttachRestingWindowWithoutChangingLevel(windowHandle);
        if (!attachedToDesktop)
        {
            DetachFromDesktopIconLayerIfNeeded(windowHandle);
        }

        if (Win32Helper.IsWindowTopMost(windowHandle))
        {
            Win32Helper.ClearWindowTopMost(windowHandle);
        }

        return attachedToDesktop;
    }

    internal static IntPtr GetForegroundRoot(IntPtr foreground)
    {
        if (foreground == IntPtr.Zero)
        {
            return IntPtr.Zero;
        }

        // DeskBox widgets with KeepWidgetsVisibleOnShowDesktop attach their
        // GWLP_HWNDPARENT to Explorer's SHELLDLL_DefView. A raw GA_ROOTOWNER
        // walk crosses that boundary and resolves an active DeskBox widget or
        // owned popup to Progman/WorkerW, misclassifying DeskBox foregrounds
        // as desktop shell windows. Stop at the highest DeskBox window before
        // crossing into Explorer's desktop host.
        if (App.Current?.IsDeskBoxWindow(foreground) == true)
        {
            IntPtr highestDeskBox = foreground;
            IntPtr current = foreground;
            for (int depth = 0; depth < 32 && current != IntPtr.Zero; depth++)
            {
                IntPtr owner = Win32Helper.GetWindow(current, Win32Helper.GW_OWNER);
                IntPtr next = owner != IntPtr.Zero
                    ? owner
                    : Win32Helper.GetParent(current);
                if (next == IntPtr.Zero ||
                    next == current ||
                    IsDesktopShellWindow(next) ||
                    App.Current?.IsDeskBoxWindow(next) != true)
                {
                    break;
                }

                highestDeskBox = next;
                current = next;
            }

            return highestDeskBox;
        }

        IntPtr foregroundRoot = Win32Helper.GetAncestor(
            foreground,
            Win32Helper.GA_ROOTOWNER);
        return foregroundRoot == IntPtr.Zero
            ? foreground
            : foregroundRoot;
    }

    public static void BringGroupTemporarilyToFront(
        IReadOnlyList<IntPtr> windowHandles,
        IntPtr activeWindowHandle)
    {
        if (UsesDesktopPinnedMode())
        {
            return;
        }

        var handles = windowHandles
            .Where(handle => handle != IntPtr.Zero && Win32Helper.IsWindow(handle))
            .Distinct()
            .ToList();
        if (handles.Count == 0)
        {
            return;
        }

        foreach (IntPtr handle in handles)
        {
            DetachFromDesktopIconLayerIfNeeded(handle);
        }

        // The whole group takes the top of the normal band in one atomic
        // relative ordering. The historical implementation pulsed every
        // window through HWND_TOPMOST and back (2N+2 transactions), which
        // visited the topmost band on every raise and contested its tail
        // with resident topmost floaters — IME candidate bars especially —
        // producing continuous flicker for both sides (#375). The end state
        // is unchanged: the group sits above all normal application windows
        // with the active window highest.
        IntPtr activeHandle = handles.Contains(activeWindowHandle)
            ? activeWindowHandle
            : handles[^1];
        List<IntPtr> orderedHandles = handles
            .Where(handle => handle != activeHandle)
            .Prepend(activeHandle)
            .ToList();
        ApplyWindowOrderHighestToLowest(
            orderedHandles,
            Win32Helper.HWND_TOP,
            "group-raise-normal-band");
        Win32Helper.BringWindowToFront(activeHandle);
        // #553: the trailing activation is redundant on every hot path — the
        // title click or tray activation already moved the foreground onto a
        // raised window by the time this runs — and this synchronous kernel
        // call deadlocked the UI thread when the input queue was still
        // draining after a long shell operation (drag/import). Skip it once
        // the raised band already owns the foreground; the skipped call was
        // only trying to establish exactly that state.
        IntPtr foregroundAtRaise = Win32Helper.GetForegroundWindow();
        if (foregroundAtRaise != IntPtr.Zero &&
            (handles.Contains(foregroundAtRaise) ||
                handles.Contains(GetForegroundRoot(foregroundAtRaise))))
        {
            return;
        }

        Win32Helper.SetForegroundWindow(activeHandle);
    }

    /// <summary>
    /// Applies a deterministic peer order without activating, moving, or
    /// resizing any widget. The first handle is the visually highest peer.
    /// The current highest DeskBox peer supplies the global Z-order boundary,
    /// so normal desktop widgets do not jump above unrelated applications.
    /// </summary>
    public static bool ApplyPeerOrderHighestToLowest(
        IReadOnlyList<IntPtr> windowHandles)
    {
        List<IntPtr> handles = windowHandles
            .Where(handle => handle != IntPtr.Zero && Win32Helper.IsWindow(handle))
            .Distinct()
            .ToList();
        if (handles.Count < 2)
        {
            return true;
        }

        // Avoid redundant SetWindowPos transactions when the relative peer
        // order is already applied.
        if (IsPeerOrderAlreadyApplied(handles))
        {
            App.LogVerbose(
                $"[ZOrder] Peer order unchanged count={handles.Count} " +
                $"highest=0x{handles[0].ToInt64():X}");
            return true;
        }

        IntPtr currentHighest = FindHighestPeer(handles);
        IntPtr boundary = currentHighest == IntPtr.Zero
            ? IntPtr.Zero
            : Win32Helper.GetWindow(currentHighest, Win32Helper.GW_HWNDPREV);
        return ApplyWindowOrderHighestToLowest(
            handles,
            boundary == IntPtr.Zero ? Win32Helper.HWND_TOP : boundary,
            "idle-peer-order");
    }

    /// <summary>
    /// Reasserts the raised band after one of its windows takes activation.
    /// A tray raise lifts the group while DeskBox may still be a background
    /// process, and Windows pins such windows below the foreground
    /// application — only the activating widget crosses that line. When the
    /// walk below the active widget still finds a foreign window before the
    /// remaining peers, the band is split and only the full group lift
    /// (which now runs from the foreground process) can repair it; an intact
    /// band keeps the repaint-free peer reorder.
    /// </summary>
    public static bool ApplyRaisedGroupReassertOrder(
        IReadOnlyList<IntPtr> orderedHandles,
        IntPtr activeWindowHandle)
    {
        if (UsesDesktopPinnedMode())
        {
            return false;
        }

        if (RaisedGroupReassertPolicy.Resolve(
                IsForeignWindowAboveRaisedPeers(orderedHandles)) ==
            RaisedGroupReassertPolicy.ReassertAction.LiftGroupAboveForeignWindows)
        {
            App.LogVerbose(
                "[ZOrder] Raised band split by foreign window; lifting group " +
                $"active=0x{activeWindowHandle.ToInt64():X} count={orderedHandles.Count}");
            BringGroupTemporarilyToFront(orderedHandles, activeWindowHandle);
            return true;
        }

        return ApplyPeerOrderHighestToLowest(orderedHandles);
    }

    /// <summary>
    /// Reports whether any window that DeskBox does not own sits between the
    /// active raised widget and its remaining peers — the raised band's
    /// integrity check. Invisible, cloaked, and minimized windows never split
    /// the band, and neither do DeskBox's own auxiliary surfaces.
    /// </summary>
    internal static bool IsForeignWindowAboveRaisedPeers(
        IReadOnlyList<IntPtr> orderedHandles,
        bool exemptShellDesktopWindows = false)
    {
        if (orderedHandles.Count < 2 ||
            orderedHandles[0] == IntPtr.Zero ||
            !Win32Helper.IsWindow(orderedHandles[0]))
        {
            return false;
        }

        const int WS_MINIMIZE = 0x20000000;
        var peers = new HashSet<IntPtr>(orderedHandles);
        int remainingPeers = orderedHandles.Count - 1;
        IntPtr current = Win32Helper.GetWindow(orderedHandles[0], Win32Helper.GW_HWNDNEXT);
        int visited = 0;
        while (current != IntPtr.Zero && visited++ < MaxBeddingWalkWindows)
        {
            if (!peers.Contains(current))
            {
                DesktopLayerBeddingWindowKind kind =
                    ClassifyWindowForBedding(current, WS_MINIMIZE);
                if (kind != DesktopLayerBeddingWindowKind.IgnoredWindow &&
                    kind != DesktopLayerBeddingWindowKind.DeskBoxWindow &&
                    !(exemptShellDesktopWindows &&
                        kind == DesktopLayerBeddingWindowKind.ShellDesktopWindow))
                {
                    return true;
                }
            }
            else if (--remainingPeers == 0)
            {
                return false;
            }

            current = Win32Helper.GetWindow(current, Win32Helper.GW_HWNDNEXT);
        }

        return false;
    }

    private static bool ApplyWindowOrderHighestToLowest(
        IReadOnlyList<IntPtr> handles,
        IntPtr boundary,
        string reason)
    {
        if (handles.Count == 0)
        {
            return true;
        }

        lock (s_desktopLayerLock)
        {
            IntPtr insertAfter = boundary;
            const uint flags =
                Win32Helper.SWP_NOMOVE |
                Win32Helper.SWP_NOSIZE |
                Win32Helper.SWP_NOACTIVATE |
                Win32Helper.SWP_NOOWNERZORDER;

            IntPtr deferred = Win32Helper.BeginDeferWindowPos(handles.Count);
            if (deferred != IntPtr.Zero)
            {
                foreach (IntPtr handle in handles)
                {
                    deferred = Win32Helper.DeferWindowPos(
                        deferred,
                        handle,
                        insertAfter,
                        0,
                        0,
                        0,
                        0,
                        flags);
                    if (deferred == IntPtr.Zero)
                    {
                        break;
                    }

                    insertAfter = handle;
                }

                if (deferred != IntPtr.Zero && Win32Helper.EndDeferWindowPos(deferred))
                {
                    App.LogVerbose(
                        $"[ZOrder] Window order applied reason={reason} " +
                        $"count={handles.Count} boundary=0x{boundary.ToInt64():X} " +
                        $"highest=0x{handles[0].ToInt64():X}");
                    return true;
                }
            }

            insertAfter = boundary;
            bool succeeded = true;
            foreach (IntPtr handle in handles)
            {
                succeeded &= Win32Helper.SetWindowPos(
                    handle,
                    insertAfter,
                    0,
                    0,
                    0,
                    0,
                    flags);
                insertAfter = handle;
            }

            App.LogVerbose(
                $"[ZOrder] Window order fallback reason={reason} " +
                $"count={handles.Count} boundary=0x{boundary.ToInt64():X} " +
                $"highest=0x{handles[0].ToInt64():X} succeeded={succeeded}");
            return succeeded;
        }
    }

    public static void ReleaseWindow(IntPtr windowHandle)
    {
        DetachFromDesktopIconLayerIfNeeded(windowHandle);
    }

    public static void InvalidateDesktopIconViewCache()
    {
        lock (s_desktopLayerLock)
        {
            s_cachedDesktopIconView = IntPtr.Zero;
        }
    }

    public static bool UsesDesktopPinnedMode()
    {
        var settings = TryGetCurrentSettings();
        string mode = SettingsService.NormalizeWidgetLayerModeSetting(settings?.WidgetLayerMode);
        return string.Equals(mode, SettingsService.WidgetLayerModeDesktopPinned, StringComparison.Ordinal);
    }

    public static bool UsesQuickRevealMode()
    {
        var settings = TryGetCurrentSettings();
        string mode = SettingsService.NormalizeWidgetLayerModeSetting(settings?.WidgetLayerMode);
        return string.Equals(mode, SettingsService.WidgetLayerModeQuickReveal, StringComparison.Ordinal);
    }

    /// <summary>
    /// Determines whether a pointer press must leave the current foreground
    /// application active. Desktop-pinned widgets behave like desktop content:
    /// an exposed part can still receive the mouse message, but it must not
    /// activate and jump above another application. Suppression applies both
    /// when a foreign application owns the foreground and when the widget is
    /// geometrically covered by a foreign window — activating a DefView-owned
    /// HWND promotes the whole owner band above such windows even while the
    /// desktop shell owns the foreground (#574). Dynamic mode keeps the
    /// existing interactive raise behavior.
    /// </summary>
    public static bool ShouldSuppressPointerActivation(IntPtr windowHandle)
    {
        IntPtr foregroundRoot = GetForegroundRoot(Win32Helper.GetForegroundWindow());
        bool hasForeground =
            foregroundRoot != IntPtr.Zero &&
            Win32Helper.IsWindow(foregroundRoot);
        bool foregroundIsWidget =
            foregroundRoot == windowHandle ||
            App.Current?.WidgetManager?.IsWidgetWindow(foregroundRoot) == true ||
            DeskBox.Controls.WidgetContents.FileSurfaceContent.IsActiveStackPopoverWindow(foregroundRoot) ||
            DeskBox.Views.WidgetEditorPopoverWindow.IsActivePopoverWindow(foregroundRoot);

        return WidgetLayerPointerActivationPolicy.ShouldSuppress(
            UsesDesktopPinnedMode(),
            hasForeground,
            hasForeground && IsDesktopShellWindow(foregroundRoot),
            foregroundIsWidget,
            IsWindowCoveredByForeignWindow(windowHandle));
    }

    /// <summary>
    /// Keeps the activating mouse message when a user clicks a widget that was
    /// shown by Quick Reveal. Only one window in the revealed group can be the
    /// foreground window, so every other widget must explicitly opt out of the
    /// default activate-and-eat behavior for its first click.
    /// </summary>
    public static bool ShouldPreserveQuickRevealActivatingClick()
    {
        return WidgetLayerPointerActivationPolicy.ShouldPreserveActivatingClick(
            UsesQuickRevealMode(),
            App.Current?.WidgetManager?.WidgetsRaisedFromTray == true);
    }

    /// <summary>
    /// Allows a desktop-pinned widget to become active only when the current
    /// foreground belongs to the desktop shell or another widget. The
    /// WS_EX_NOACTIVATE resting style makes this decision before the routed
    /// pointer event, so clicking an exposed blank area cannot raise the whole
    /// HWND above a foreign application.
    /// </summary>
    public static bool TryAllowDesktopPinnedPointerActivation(IntPtr windowHandle)
    {
        if (!UsesDesktopPinnedMode())
        {
            SetWindowNoActivate(windowHandle, enabled: false);
            return true;
        }

        if (ShouldSuppressPointerActivation(windowHandle))
        {
            SetWindowNoActivate(windowHandle, enabled: true);
            return false;
        }

        SetWindowNoActivate(windowHandle, enabled: false);
        return true;
    }

    /// <summary>
    /// Temporarily removes the desktop-pinned no-activate style for an explicit
    /// keyboard-input interaction. The caller must restore the resting desktop
    /// layer immediately after activating the window.
    /// </summary>
    public static void PrepareForDesktopPinnedKeyboardInput(IntPtr windowHandle)
    {
        SetWindowNoActivate(windowHandle, enabled: false);
    }

    public static bool IsWindowNoActivate(IntPtr windowHandle)
    {
        return windowHandle != IntPtr.Zero &&
            (Win32Helper.GetWindowLong(windowHandle, Win32Helper.GWL_EXSTYLE) &
                Win32Helper.WS_EX_NOACTIVATE) != 0;
    }

    internal static void ApplyDesktopPinnedRestingActivationStyle(IntPtr windowHandle)
    {
        SetWindowNoActivate(windowHandle, UsesDesktopPinnedMode());
    }

    private static void ApplyDesktopPinnedActivationStyle(IntPtr windowHandle)
    {
        SetWindowNoActivate(windowHandle, UsesDesktopPinnedMode());
    }

    private static void SetWindowNoActivate(IntPtr windowHandle, bool enabled)
    {
        if (windowHandle == IntPtr.Zero || !Win32Helper.IsWindow(windowHandle))
        {
            return;
        }

        int extendedStyle = Win32Helper.GetWindowLong(
            windowHandle,
            Win32Helper.GWL_EXSTYLE);
        int updatedStyle = enabled
            ? extendedStyle | Win32Helper.WS_EX_NOACTIVATE
            : extendedStyle & ~Win32Helper.WS_EX_NOACTIVATE;
        if (updatedStyle == extendedStyle)
        {
            return;
        }

        _ = Win32Helper.SetWindowLong(
            windowHandle,
            Win32Helper.GWL_EXSTYLE,
            updatedStyle);
        _ = Win32Helper.SetWindowPos(
            windowHandle,
            IntPtr.Zero,
            0,
            0,
            0,
            0,
            Win32Helper.SWP_NOMOVE |
                Win32Helper.SWP_NOSIZE |
                Win32Helper.SWP_NOZORDER |
                Win32Helper.SWP_NOACTIVATE |
                Win32Helper.SWP_FRAMECHANGED);
        App.LogVerbose(
            $"[WidgetLayer] no-activate style hwnd=0x{windowHandle.ToInt64():X} " +
            $"enabled={enabled}");
    }

    private static bool ShouldAttachRestingWindowToDesktop()
    {
        bool keepVisible = TryGetCurrentSettings()?
            .KeepWidgetsVisibleOnShowDesktop ?? true;
        return RelativeLayerRestorePolicy.ShouldAttachToDesktop(
            UsesDesktopPinnedMode(),
            keepVisible);
    }

    private static bool TryAttachRestingWindowWithoutChangingLevel(
        IntPtr windowHandle)
    {
        return ShouldAttachRestingWindowToDesktop() &&
               TryAttachToDesktopIconLayer(
                   windowHandle,
                   placeAtBottom: false);
    }

    private static bool TryAttachToDesktopIconLayer(
        IntPtr windowHandle,
        bool placeAtBottom = true,
        bool showWindow = true)
    {
        if (windowHandle == IntPtr.Zero || !Win32Helper.IsWindow(windowHandle))
        {
            return false;
        }

        if (Volatile.Read(ref s_startupDesktopLayerAttachmentDeferred))
        {
            App.LogVerbose(
                $"[Startup] Desktop-layer attach deferred hwnd=0x{windowHandle.ToInt64():X}");
            return false;
        }

        IntPtr desktopIconView = FindDesktopIconView();
        if (desktopIconView == IntPtr.Zero)
        {
            App.Log("[WidgetLayer] DesktopPinned attach skipped: desktop icon view not found");
            return false;
        }

        lock (s_desktopLayerLock)
        {
            bool ownerWasAlreadyAttached =
                s_desktopLayerAttachments.ContainsKey(windowHandle) &&
                Win32Helper.GetWindowLongPtr(windowHandle, Win32Helper.GWLP_HWNDPARENT) == desktopIconView;
            if (!s_desktopLayerAttachments.ContainsKey(windowHandle))
            {
                s_desktopLayerAttachments[windowHandle] = new DesktopLayerAttachment(
                    Win32Helper.GetWindowLongPtr(windowHandle, Win32Helper.GWLP_HWNDPARENT));
            }

            if (Win32Helper.GetWindowLongPtr(windowHandle, Win32Helper.GWLP_HWNDPARENT) != desktopIconView)
            {
                Win32Helper.SetLastError(0);
                _ = Win32Helper.SetWindowLongPtr(
                    windowHandle,
                    Win32Helper.GWLP_HWNDPARENT,
                    desktopIconView);
            }

            IntPtr actualOwner = Win32Helper.GetWindowLongPtr(windowHandle, Win32Helper.GWLP_HWNDPARENT);
            if (actualOwner != desktopIconView)
            {
                int error = Marshal.GetLastWin32Error();
                App.Log($"[WidgetLayer] DesktopPinned owner attach failed hwnd=0x{windowHandle.ToInt64():X} defView=0x{desktopIconView.ToInt64():X} actual=0x{actualOwner.ToInt64():X} error={error}");
                RestoreOriginalOwner(windowHandle);
                s_cachedDesktopIconView = IntPtr.Zero;
                return false;
            }

            // A suppressed desktop-pinned click funnels through here on every
            // message (WM_MOUSEACTIVATE plus the XAML routed event). When the
            // owner is unchanged and the window already rests in the desktop
            // band, re-issuing HWND_BOTTOM / HWND_NOTOPMOST transactions only
            // repaints the DefView owner band and reads as flicker under a
            // covering application (#241/#249/#449). HWND_NOTOPMOST on an
            // already non-topmost window is documented as a no-op, so both
            // transactions are skipped for an already-bedded window.
            if (placeAtBottom &&
                ownerWasAlreadyAttached &&
                !Win32Helper.IsWindowTopMost(windowHandle) &&
                IsWindowBeddedAtDesktopLayer(windowHandle))
            {
                t_lastAttachMovedZOrder = false;
                return true;
            }

            bool clearedTopMost = false;
            if (Win32Helper.IsWindowTopMost(windowHandle))
            {
                Win32Helper.ClearWindowTopMost(windowHandle);
                clearedTopMost = true;
            }

            if (placeAtBottom)
            {
                uint flags = Win32Helper.SWP_NOMOVE |
                    Win32Helper.SWP_NOSIZE |
                    Win32Helper.SWP_NOACTIVATE;
                if (showWindow)
                {
                    flags |= Win32Helper.SWP_SHOWWINDOW;
                }

                Win32Helper.SetWindowPos(
                    windowHandle,
                    Win32Helper.HWND_BOTTOM,
                    0,
                    0,
                    0,
                    0,
                    flags);
            }

            t_lastAttachMovedZOrder = placeAtBottom || clearedTopMost;
            App.LogVerbose(
                $"[WidgetLayer] Desktop owner attached hwnd=0x{windowHandle.ToInt64():X} " +
                $"defView=0x{desktopIconView.ToInt64():X} bottom={placeAtBottom}");
            return true;
        }
    }

    /// <summary>
    /// Walks the Z order below a desktop-pinned widget and reports whether it
    /// already rests in the Explorer desktop band: only DeskBox-owned windows,
    /// shell desktop windows (Progman/WorkerW/SHELLDLL_DefView), and visually
    /// meaningless windows (hidden, minimized, cloaked) may sit between the
    /// widget and the band bottom. A visible foreign application window below
    /// the widget means the group is floating above that application.
    /// </summary>
    internal static bool IsWindowBeddedAtDesktopLayer(IntPtr windowHandle)
    {
        if (windowHandle == IntPtr.Zero || !Win32Helper.IsWindow(windowHandle))
        {
            return false;
        }

        const int WS_MINIMIZE = 0x20000000;
        var observations = new List<DesktopLayerBeddingWindowKind>();
        IntPtr current = Win32Helper.GetWindow(windowHandle, Win32Helper.GW_HWNDNEXT);
        while (current != IntPtr.Zero && observations.Count < MaxBeddingWalkWindows)
        {
            observations.Add(ClassifyWindowForBedding(current, WS_MINIMIZE));
            current = Win32Helper.GetWindow(current, Win32Helper.GW_HWNDNEXT);
        }

        return DesktopLayerBeddingPolicy.IsBeddedAtDesktopLayer(observations);
    }

    /// <summary>
    /// Resolves the first foreign window found below a desktop-pinned widget,
    /// or <see cref="IntPtr.Zero"/> when the widget is safely bedded. The
    /// returned handle identifies the leak witness for conservative
    /// confirmations across repeated observations.
    /// </summary>
    internal static IntPtr FindForeignWindowBelow(IntPtr windowHandle)
    {
        if (windowHandle == IntPtr.Zero || !Win32Helper.IsWindow(windowHandle))
        {
            return IntPtr.Zero;
        }

        const int WS_MINIMIZE = 0x20000000;
        IntPtr current = Win32Helper.GetWindow(windowHandle, Win32Helper.GW_HWNDNEXT);
        int visited = 0;
        while (current != IntPtr.Zero && visited++ < MaxBeddingWalkWindows)
        {
            if (ClassifyWindowForBedding(current, WS_MINIMIZE) ==
                DesktopLayerBeddingWindowKind.ForeignAppWindow)
            {
                return current;
            }

            current = Win32Helper.GetWindow(current, Win32Helper.GW_HWNDNEXT);
        }

        return IntPtr.Zero;
    }

    /// <summary>
    /// Reports whether a visible foreign application window both sits above
    /// the widget in Z order and geometrically overlaps it. Activating a
    /// DefView-owned HWND promotes the whole owner band above every such
    /// window (#454/#574), so pointer clicks on an overlapped pinned widget
    /// must not activate even when the desktop shell owns the foreground; a
    /// fully exposed widget may still activate so keyboard flows (rename,
    /// text entry, file shortcuts) keep working. Promotion over a
    /// non-overlapping window is visually invisible, so only intersecting
    /// windows count. The taskbar is neutral: an auto-hide taskbar overlaps
    /// widgets without an activation being able to disturb it.
    /// </summary>
    public static bool IsWindowCoveredByForeignWindow(IntPtr windowHandle)
    {
        if (windowHandle == IntPtr.Zero || !Win32Helper.IsWindow(windowHandle))
        {
            return false;
        }

        if (!Win32Helper.GetWindowRect(windowHandle, out Win32Helper.RECT widgetRect))
        {
            return false;
        }

        const int wsMinimizeStyle = 0x20000000;
        IntPtr current = Win32Helper.GetWindow(windowHandle, Win32Helper.GW_HWNDPREV);
        int visited = 0;
        while (current != IntPtr.Zero && visited++ < MaxBeddingWalkWindows)
        {
            if (ClassifyWindowForBedding(current, wsMinimizeStyle) ==
                    DesktopLayerBeddingWindowKind.ForeignAppWindow &&
                !WidgetManager.IsTaskbarWindow(current) &&
                Win32Helper.GetWindowRect(current, out Win32Helper.RECT coverRect) &&
                RectsIntersect(widgetRect, coverRect))
            {
                return true;
            }

            current = Win32Helper.GetWindow(current, Win32Helper.GW_HWNDPREV);
        }

        return false;
    }

    private static bool RectsIntersect(Win32Helper.RECT first, Win32Helper.RECT second)
    {
        return first.Left < second.Right &&
            second.Left < first.Right &&
            first.Top < second.Bottom &&
            second.Top < first.Bottom;
    }

    // The walk terminates at Progman after a handful of windows in a healthy
    // session; the cap only guards against a pathological cycle.
    private const int MaxBeddingWalkWindows = 256;

    /// <summary>
    /// DeskBox surfaces are classified by process, not by the registered
    /// window set: every widget, owned flyout, and auxiliary surface of this
    /// application lives in the current process, and none of them may ever
    /// witness a desktop-band leak. This also keeps the module-boundary
    /// budget of this file free of new App-level global accesses.
    /// </summary>
    private static DesktopLayerBeddingWindowKind ClassifyWindowForBedding(
        IntPtr windowHandle,
        int wsMinimizeStyle)
    {
        if (!Win32Helper.IsWindowVisible(windowHandle) ||
            Win32Helper.IsWindowCloaked(windowHandle) ||
            (Win32Helper.GetWindowLong(windowHandle, Win32Helper.GWL_STYLE) & wsMinimizeStyle) != 0)
        {
            return DesktopLayerBeddingWindowKind.IgnoredWindow;
        }

        if (BelongsToCurrentProcess(windowHandle))
        {
            return DesktopLayerBeddingWindowKind.DeskBoxWindow;
        }

        return IsDesktopShellWindow(windowHandle)
            ? DesktopLayerBeddingWindowKind.ShellDesktopWindow
            : DesktopLayerBeddingWindowKind.ForeignAppWindow;
    }

    private static bool BelongsToCurrentProcess(IntPtr windowHandle)
    {
        _ = Win32Helper.GetWindowThreadProcessId(windowHandle, out uint processId);
        return processId == (uint)Environment.ProcessId;
    }

    private static void DetachFromDesktopIconLayerIfNeeded(IntPtr windowHandle)
    {
        lock (s_desktopLayerLock)
        {
            if (!s_desktopLayerAttachments.ContainsKey(windowHandle))
            {
                return;
            }

            RestoreOriginalOwner(windowHandle);
        }
    }

    private static void MoveToDynamicDesktopBottom(
        IntPtr windowHandle,
        bool showWindow = true)
    {
        // Try to attach to desktop icon layer to prevent Win+D from hiding the window
        // while maintaining dynamic layer behavior (can be raised on interaction)
        if (TryAttachToDesktopIconLayer(
                windowHandle,
                showWindow: showWindow))
        {
            return;
        }

        // Fallback: detach and use NOTOPMOST
        DetachFromDesktopIconLayerIfNeeded(windowHandle);
        Win32Helper.ClearWindowTopMost(windowHandle);
        Win32Helper.SetWindowToBottom(windowHandle);
    }

    private static void RestoreOriginalOwner(IntPtr windowHandle)
    {
        if (!s_desktopLayerAttachments.TryGetValue(windowHandle, out var attachment))
        {
            return;
        }

        Win32Helper.SetLastError(0);
        _ = Win32Helper.SetWindowLongPtr(
            windowHandle,
            Win32Helper.GWLP_HWNDPARENT,
            attachment.OriginalOwner);
        Win32Helper.SetWindowPos(
            windowHandle,
            Win32Helper.HWND_NOTOPMOST,
            0,
            0,
            0,
            0,
            Win32Helper.SWP_NOMOVE |
                Win32Helper.SWP_NOSIZE |
                Win32Helper.SWP_NOACTIVATE);
        s_desktopLayerAttachments.Remove(windowHandle);
        App.LogVerbose($"[WidgetLayer] DesktopPinned owner detached hwnd=0x{windowHandle.ToInt64():X}");
    }

    private static IntPtr FindDesktopIconView()
    {
        if (s_cachedDesktopIconView != IntPtr.Zero && Win32Helper.IsWindow(s_cachedDesktopIconView))
        {
            return s_cachedDesktopIconView;
        }

        s_cachedDesktopIconView = IntPtr.Zero;

        // Only use a SHELLDLL_DefView that Explorer has already created. Never
        // force WorkerW creation here: doing so during login can race Explorer's
        // icon-layout restoration and leave the user's desktop icons rearranged.
        IntPtr existingDefView = IntPtr.Zero;
        Win32Helper.EnumWindows((hWnd, _) =>
        {
            IntPtr defView = FindDesktopIconViewChild(hWnd);
            if (defView != IntPtr.Zero)
            {
                existingDefView = defView;
                return false; // stop enumeration
            }

            return true;
        }, IntPtr.Zero);

        if (existingDefView != IntPtr.Zero)
        {
            s_cachedDesktopIconView = existingDefView;
            return s_cachedDesktopIconView;
        }

        // The caller already has a bottom-of-desktop fallback and can retry on a
        // later layer operation after Explorer finishes initializing.
        return IntPtr.Zero;
    }

    private static IntPtr FindHighestPeer(IReadOnlyCollection<IntPtr> handles)
    {
        var peers = handles.ToHashSet();
        IntPtr current = Win32Helper.GetWindow(handles.First(), Win32Helper.GW_HWNDFIRST);
        while (current != IntPtr.Zero)
        {
            if (peers.Contains(current))
            {
                return current;
            }

            current = Win32Helper.GetWindow(current, Win32Helper.GW_HWNDNEXT);
        }

        return handles.FirstOrDefault();
    }

    private static IntPtr FindDesktopIconViewChild(IntPtr windowHandle)
    {
        return Win32Helper.FindWindowEx(windowHandle, IntPtr.Zero, "SHELLDLL_DefView", null);
    }

    private static bool IsDesktopShellWindow(IntPtr windowHandle)
    {
        return DesktopBlankHitTest.IsVerifiedDesktopShellWindow(
            windowHandle,
            allowDefView: true);
    }

    private static Models.AppSettings? TryGetCurrentSettings() =>
        App.Current?.SettingsService?.Settings;

    public static bool UsesDynamicGroupClickRaiseMode()
    {
        if (UsesDesktopPinnedMode() || UsesQuickRevealMode())
        {
            return false;
        }

        string scope = SettingsService.NormalizeDynamicClickRaiseScopeSetting(
            TryGetCurrentSettings()?.DynamicClickRaiseScope);
        return string.Equals(
            scope,
            SettingsService.DynamicClickRaiseScopeGroup,
            StringComparison.Ordinal);
    }

    internal static void RecordWidgetMouseActivate()
    {
        Volatile.Write(ref s_lastAnyWidgetMouseActivateTick, Environment.TickCount64);
    }

    public static void DetachSingleDynamicWidgetForInteraction(IntPtr windowHandle)
    {
        if (windowHandle == IntPtr.Zero || UsesDesktopPinnedMode())
        {
            return;
        }

        DetachFromDesktopIconLayerIfNeeded(windowHandle);
    }

    public static bool BringGroupToNormalBandWithoutActivation(
        IReadOnlyList<IntPtr> windowHandles,
        string reason)
    {
        if (UsesDesktopPinnedMode())
        {
            return false;
        }

        List<IntPtr> handles = windowHandles
            .Where(static handle => handle != IntPtr.Zero && Win32Helper.IsWindow(handle))
            .Distinct()
            .ToList();
        if (handles.Count == 0)
        {
            return false;
        }

        foreach (IntPtr handle in handles)
        {
            DetachFromDesktopIconLayerIfNeeded(handle);
        }

        return ApplyWindowOrderHighestToLowest(
            handles,
            Win32Helper.HWND_TOP,
            reason);
    }

    private static bool RestoreGroupToDesktopBottomAtomically(
        IReadOnlyList<IntPtr> handles,
        string reason)
    {
        bool attachToDesktop = ShouldAttachRestingWindowToDesktop();
        foreach (IntPtr handle in handles)
        {
            ApplyDesktopPinnedActivationStyle(handle);
            if (attachToDesktop)
            {
                if (!TryAttachToDesktopIconLayer(handle, placeAtBottom: false))
                {
                    DetachFromDesktopIconLayerIfNeeded(handle);
                }
            }
            else
            {
                DetachFromDesktopIconLayerIfNeeded(handle);
            }
        }

        bool alreadyBedded = handles.All(static handle =>
            !Win32Helper.IsWindowTopMost(handle) &&
            IsWindowBeddedAtDesktopLayer(handle));
        if (alreadyBedded &&
            (handles.Count < 2 || IsPeerOrderAlreadyApplied(handles)))
        {
            return true;
        }

        foreach (IntPtr handle in handles)
        {
            if (Win32Helper.IsWindowTopMost(handle))
            {
                Win32Helper.ClearWindowTopMost(handle);
            }
        }

        IntPtr lowestForeign = FindLowestForeignWindowAnchor(handles[0]);
        if (lowestForeign != IntPtr.Zero)
        {
            return ApplyWindowOrderHighestToLowest(
                handles,
                lowestForeign,
                $"group-bottom-{reason}");
        }

        if (handles.Count == 1)
        {
            if (!IsWindowBeddedAtDesktopLayer(handles[0]))
            {
                _ = Win32Helper.SetWindowPos(
                    handles[0],
                    Win32Helper.HWND_BOTTOM,
                    0,
                    0,
                    0,
                    0,
                    Win32Helper.SWP_NOMOVE |
                        Win32Helper.SWP_NOSIZE |
                        Win32Helper.SWP_NOACTIVATE |
                        Win32Helper.SWP_NOOWNERZORDER);
            }

            return true;
        }

        return ApplyPeerOrderHighestToLowest(handles);
    }

    private static IntPtr FindLowestForeignWindowAnchor(IntPtr seedWindow)
    {
        if (seedWindow == IntPtr.Zero)
        {
            return IntPtr.Zero;
        }

        const int wsMinimizeStyle = 0x20000000;
        IntPtr current = Win32Helper.GetWindow(seedWindow, Win32Helper.GW_HWNDFIRST);
        IntPtr lowestForeign = IntPtr.Zero;
        int visited = 0;
        while (current != IntPtr.Zero && visited++ < MaxBeddingWalkWindows)
        {
            if (!Win32Helper.IsWindowTopMost(current) &&
                ClassifyWindowForBedding(current, wsMinimizeStyle) ==
                    DesktopLayerBeddingWindowKind.ForeignAppWindow &&
                !WidgetManager.IsTaskbarWindow(current))
            {
                lowestForeign = current;
            }

            current = Win32Helper.GetWindow(current, Win32Helper.GW_HWNDNEXT);
        }

        return lowestForeign;
    }

    internal static bool IsDesktopHostTopMost()
    {
        IntPtr defView;
        lock (s_desktopLayerLock)
        {
            defView = s_cachedDesktopIconView;
        }

        if (defView == IntPtr.Zero || !Win32Helper.IsWindow(defView))
        {
            return false;
        }

        IntPtr host = Win32Helper.GetParent(defView);
        if (host != IntPtr.Zero && Win32Helper.IsWindowTopMost(host))
        {
            return true;
        }

        IntPtr root = Win32Helper.GetAncestor(defView, Win32Helper.GA_ROOT);
        return root != IntPtr.Zero && Win32Helper.IsWindowTopMost(root);
    }

    internal static bool ShouldSuppressUnsolicitedZOrderChange(
        IntPtr windowHandle,
        IntPtr insertAfter,
        uint flags,
        bool isAtDesktopLayer,
        bool hasExpandedLease,
        bool isBoundsInteracting,
        bool isRaisedFromTray,
        long lastDirectMouseActivateTick,
        out bool mayBeDynamicDesktopClick)
    {
        mayBeDynamicDesktopClick = false;
        if ((flags & Win32Helper.SWP_NOZORDER) != 0 ||
            Win32Helper.IsAuthorizedZOrderChangeInProgress ||
            !isAtDesktopLayer ||
            (hasExpandedLease && !UsesDesktopPinnedMode()) ||
            isBoundsInteracting ||
            isRaisedFromTray ||
            UsesQuickRevealMode() ||
            windowHandle == IntPtr.Zero ||
            !Win32Helper.IsWindowVisible(windowHandle))
        {
            return false;
        }

        // Preserve Show Desktop (Win+D): Explorer temporarily promotes its
        // WorkerW host (and owned desktop widgets) into the topmost band and
        // lowers them back out on the second Win+D toggle.
        if (insertAfter == Win32Helper.HWND_TOPMOST ||
            IsDesktopHostTopMost() ||
            Win32Helper.IsWindowTopMost(windowHandle))
        {
            return false;
        }

        if (UsesDesktopPinnedMode())
        {
            return true;
        }

        long nowTick = Environment.TickCount64;
        if (lastDirectMouseActivateTick > 0 &&
            nowTick - lastDirectMouseActivateTick <= 350)
        {
            return false;
        }

        long lastAnyWidgetActivate = Volatile.Read(ref s_lastAnyWidgetMouseActivateTick);
        bool siblingWidgetJustClicked =
            lastAnyWidgetActivate > 0 &&
            nowTick - lastAnyWidgetActivate <= 350;
        if (!siblingWidgetJustClicked && Win32Helper.IsLeftMouseButtonDown())
        {
            mayBeDynamicDesktopClick = true;
        }

        return true;
    }

    internal static bool IsWindowOrOwnedByWidget(
        IntPtr candidateWindow,
        IntPtr widgetWindowHandle)
    {
        if (candidateWindow == IntPtr.Zero || widgetWindowHandle == IntPtr.Zero)
        {
            return false;
        }

        IntPtr current = candidateWindow;
        for (int depth = 0; depth < 32 && current != IntPtr.Zero; depth++)
        {
            if (current == widgetWindowHandle)
            {
                return true;
            }

            if (IsDesktopShellWindow(current))
            {
                return false;
            }

            IntPtr owner = Win32Helper.GetWindow(current, Win32Helper.GW_OWNER);
            IntPtr next = owner != IntPtr.Zero
                ? owner
                : Win32Helper.GetParent(current);
            if (next == current)
            {
                break;
            }

            current = next;
        }

        return false;
    }

    private sealed record DesktopLayerAttachment(IntPtr OriginalOwner);
}

internal sealed class DesktopIconViewStabilityTracker
{
    private readonly int _requiredStableSamples;
    private IntPtr _candidate;
    private int _stableSampleCount;

    public DesktopIconViewStabilityTracker(int requiredStableSamples)
    {
        _requiredStableSamples = Math.Max(1, requiredStableSamples);
    }

    public bool Observe(IntPtr desktopIconView)
    {
        if (desktopIconView == IntPtr.Zero)
        {
            _candidate = IntPtr.Zero;
            _stableSampleCount = 0;
            return false;
        }

        if (desktopIconView != _candidate)
        {
            _candidate = desktopIconView;
            _stableSampleCount = 1;
        }
        else
        {
            _stableSampleCount++;
        }

        return _stableSampleCount >= _requiredStableSamples;
    }
}

internal static class WidgetLayerPointerActivationPolicy
{
    public static bool ShouldSuppress(
        bool usesDesktopPinnedMode,
        bool hasForegroundWindow,
        bool foregroundIsDesktopShell,
        bool foregroundIsWidget,
        bool coveredByForeignWindow)
    {
        return usesDesktopPinnedMode &&
            (coveredByForeignWindow ||
                (hasForegroundWindow &&
                    !foregroundIsDesktopShell &&
                    !foregroundIsWidget));
    }

    public static bool ShouldPreserveActivatingClick(
        bool usesQuickRevealMode,
        bool widgetsRaisedFromTray)
    {
        return usesQuickRevealMode && widgetsRaisedFromTray;
    }
}
