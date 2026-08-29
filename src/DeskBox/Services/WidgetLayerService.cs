using DeskBox.Helpers;
using System.Runtime.InteropServices;

namespace DeskBox.Services;

/// <summary>
/// Centralizes desktop widget Z-order operations so future layer modes can be
/// implemented without duplicating Win32 calls across each widget window type.
/// </summary>
public static class WidgetLayerService
{
    private const uint SpawnWorkerWMessage = 0x052C;

    private static readonly object s_desktopLayerLock = new();
    private static readonly Dictionary<IntPtr, DesktopLayerAttachment> s_desktopLayerAttachments = [];
    private static IntPtr s_cachedDesktopIconView;

    public static void MoveToDesktopBottom(
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
            return;
        }

        DetachFromDesktopIconLayerIfNeeded(windowHandle);
        Win32Helper.ClearWindowTopMost(windowHandle);
        Win32Helper.SetWindowToBottom(windowHandle);
    }

    public static IntPtr ClearTopMostPreservingForeground(IntPtr windowHandle)
    {
        ApplyDesktopPinnedActivationStyle(windowHandle);

        if (UsesDesktopPinnedMode())
        {
            if (!TryAttachToDesktopIconLayer(windowHandle))
            {
                MoveToDynamicDesktopBottom(windowHandle);
            }

            return Win32Helper.GetForegroundWindow();
        }

        IntPtr foreground = Win32Helper.GetForegroundWindow();
        IntPtr foregroundRoot = GetForegroundRoot(foreground);
        bool hasForeground =
            foregroundRoot != IntPtr.Zero &&
            Win32Helper.IsWindow(foregroundRoot);
        RelativeLayerRestoreDisposition disposition = RelativeLayerRestorePolicy.Decide(
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
                if (!TryAttachRestingWindowWithoutChangingLevel(windowHandle))
                {
                    DetachFromDesktopIconLayerIfNeeded(windowHandle);
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
        string reason)
    {
        List<IntPtr> handles = windowHandles
            .Where(handle => handle != IntPtr.Zero && Win32Helper.IsWindow(handle))
            .Distinct()
            .ToList();
        if (handles.Count == 0)
        {
            return true;
        }

        if (UsesDesktopPinnedMode())
        {
            foreach (IntPtr handle in handles)
            {
                MoveToDesktopBottom(handle);
            }

            bool pinnedApplied = ApplyPeerOrderHighestToLowest(handles);
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
        RelativeLayerRestoreDisposition disposition = RelativeLayerRestorePolicy.Decide(
            hasForeground,
            hasForeground && IsDesktopShellWindow(foregroundRoot),
            handles.Contains(foregroundRoot),
            hasForeground && App.Current.IsDeskBoxWindow(foregroundRoot));

        bool applied;
        switch (disposition)
        {
            case RelativeLayerRestoreDisposition.DesktopBottom:
                foreach (IntPtr handle in handles)
                {
                    MoveToDesktopBottom(handle);
                }

                applied = ApplyPeerOrderHighestToLowest(handles);
                break;

            case RelativeLayerRestoreDisposition.PreservePeerOrder:
                foreach (IntPtr handle in handles)
                {
                    PrepareRestingWindowForRelativePlacement(handle);
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
        Win32Helper.BringWindowTemporarilyToFront(windowHandle, showWindow);
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
        if (UsesDesktopPinnedMode())
        {
            // Fixed-layer widgets never acquire an interaction-time peer raise.
            // Keeping the active HWND at the desktop bottom is more important
            // than letting an expanded capsule cover another fixed widget.
            MoveToDesktopBottom(windowHandle);
            return true;
        }

        if (!ShouldAttachRestingWindowToDesktop() ||
            !TryAttachToDesktopIconLayer(windowHandle, placeAtBottom: false))
        {
            return false;
        }

        return Win32Helper.SetWindowPos(
            windowHandle,
            Win32Helper.HWND_TOP,
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

    private static IntPtr GetForegroundRoot(IntPtr foreground)
    {
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
            Win32Helper.SetWindowTopMost(handle);
        }

        foreach (IntPtr handle in handles.Where(handle => handle != activeWindowHandle))
        {
            Win32Helper.ClearWindowTopMost(handle);
        }

        IntPtr activeHandle = handles.Contains(activeWindowHandle)
            ? activeWindowHandle
            : handles[^1];
        Win32Helper.ClearWindowTopMost(activeHandle);
        Win32Helper.BringWindowToFront(activeHandle);
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

        IntPtr currentHighest = FindHighestPeer(handles);
        IntPtr boundary = currentHighest == IntPtr.Zero
            ? IntPtr.Zero
            : Win32Helper.GetWindow(currentHighest, Win32Helper.GW_HWNDPREV);
        return ApplyWindowOrderHighestToLowest(
            handles,
            boundary == IntPtr.Zero ? Win32Helper.HWND_TOP : boundary,
            "idle-peer-order");
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
        var settings = App.Current?.SettingsService?.Settings;
        string mode = SettingsService.NormalizeWidgetLayerModeSetting(settings?.WidgetLayerMode);
        return string.Equals(mode, SettingsService.WidgetLayerModeDesktopPinned, StringComparison.Ordinal);
    }

    public static bool UsesQuickRevealMode()
    {
        var settings = App.Current?.SettingsService?.Settings;
        string mode = SettingsService.NormalizeWidgetLayerModeSetting(settings?.WidgetLayerMode);
        return string.Equals(mode, SettingsService.WidgetLayerModeQuickReveal, StringComparison.Ordinal);
    }

    /// <summary>
    /// Determines whether a pointer press must leave the current foreground
    /// application active. Desktop-pinned widgets behave like desktop content:
    /// an exposed part can still receive the mouse message, but it must not
    /// activate and jump above another application. Dynamic mode keeps the
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
            App.Current?.WidgetManager?.IsWidgetWindow(foregroundRoot) == true;

        return WidgetLayerPointerActivationPolicy.ShouldSuppress(
            UsesDesktopPinnedMode(),
            hasForeground,
            hasForeground && IsDesktopShellWindow(foregroundRoot),
            foregroundIsWidget);
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
        bool keepVisible = App.Current?.SettingsService?.Settings
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

        IntPtr desktopIconView = FindDesktopIconView();
        if (desktopIconView == IntPtr.Zero)
        {
            App.Log("[WidgetLayer] DesktopPinned attach skipped: desktop icon view not found");
            return false;
        }

        lock (s_desktopLayerLock)
        {
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

            Win32Helper.ClearWindowTopMost(windowHandle);
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

            App.LogVerbose(
                $"[WidgetLayer] Desktop owner attached hwnd=0x{windowHandle.ToInt64():X} " +
                $"defView=0x{desktopIconView.ToInt64():X} bottom={placeAtBottom}");
            return true;
        }
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

        // First: check if a WorkerW already hosts SHELLDLL_DefView. This avoids
        // sending 0x052C again, which can disrupt DWM composition and cause the
        // desktop wallpaper to disappear (especially after display/DPI changes).
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

        // No existing WorkerW found: send 0x052C to Progman to spawn one.
        IntPtr progman = Win32Helper.FindWindow("Progman", null);
        if (progman != IntPtr.Zero)
        {
            _ = Win32Helper.SendMessageTimeout(
                progman,
                SpawnWorkerWMessage,
                UIntPtr.Zero,
                IntPtr.Zero,
                Win32Helper.SMTO_NORMAL,
                1000,
                out _);

            IntPtr progmanDefView = FindDesktopIconViewChild(progman);
            if (progmanDefView != IntPtr.Zero)
            {
                s_cachedDesktopIconView = progmanDefView;
                return s_cachedDesktopIconView;
            }
        }

        // Last resort: enum again after spawning.
        IntPtr workerDefView = IntPtr.Zero;
        Win32Helper.EnumWindows((hWnd, _) =>
        {
            IntPtr defView = FindDesktopIconViewChild(hWnd);
            if (defView != IntPtr.Zero)
            {
                workerDefView = defView;
                return false;
            }

            return true;
        }, IntPtr.Zero);

        s_cachedDesktopIconView = workerDefView;
        return s_cachedDesktopIconView;
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
        IntPtr current = windowHandle;
        while (current != IntPtr.Zero)
        {
            var className = new System.Text.StringBuilder(256);
            int length = Win32Helper.GetClassName(
                current,
                className,
                className.Capacity);
            if (length > 0 &&
                (string.Equals(className.ToString(), "Progman", StringComparison.Ordinal) ||
                 string.Equals(className.ToString(), "WorkerW", StringComparison.Ordinal) ||
                 string.Equals(className.ToString(), "SHELLDLL_DefView", StringComparison.Ordinal)))
            {
                return true;
            }

            current = Win32Helper.GetParent(current);
        }

        return false;
    }

    private sealed record DesktopLayerAttachment(IntPtr OriginalOwner);
}

internal static class WidgetLayerPointerActivationPolicy
{
    public static bool ShouldSuppress(
        bool usesDesktopPinnedMode,
        bool hasForegroundWindow,
        bool foregroundIsDesktopShell,
        bool foregroundIsWidget)
    {
        return usesDesktopPinnedMode &&
            hasForegroundWindow &&
            !foregroundIsDesktopShell &&
            !foregroundIsWidget;
    }

    public static bool ShouldPreserveActivatingClick(
        bool usesQuickRevealMode,
        bool widgetsRaisedFromTray)
    {
        return usesQuickRevealMode && widgetsRaisedFromTray;
    }
}
