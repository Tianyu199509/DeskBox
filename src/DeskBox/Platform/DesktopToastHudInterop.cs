using System.Diagnostics;
using System.Runtime.InteropServices;

namespace DeskBox.Platform;

internal enum DropTargetWindowKind
{
    Unknown = 0,
    ExplorerFileView = 1,
    DeskBoxOwnWindow = 2,
    NonExplorerWindow = 3,
}

internal readonly record struct DropReleaseSnapshot(
    DropTargetWindowKind TargetKind,
    bool EscapePressed,
    Win32Helper.POINT CursorPoint,
    bool IsDesktopShellView = false);

internal readonly record struct OutboundCursorEnvironmentSnapshot(
    bool MouseButtonDown,
    bool IsOutsideEligibleTarget,
    Win32Helper.POINT CursorPoint,
    Win32Helper.POINT? SafeNudgeTarget,
    DropTargetWindowKind TargetKind = DropTargetWindowKind.Unknown);

/// <summary>
/// Win32 platform primitives for the global bottom-center no-activate desktop
/// toast pill HUD (<see cref="DeskBox.Views.DesktopToastHudWindow"/>) and
/// post-drop target window classification.
/// </summary>
internal static unsafe partial class DesktopToastHudInterop
{
    internal const uint WmGetMinMaxInfo = 0x0024;
    internal const uint WmMouseActivate = 0x0021;
    internal const uint WmNcDestroy = 0x0082;
    internal const int MaNoActivate = 3;
    internal const int SwShowNoActivate = 4;
    internal const int DwmwaCloak = 13;

    private static readonly IntPtr DragVisualRefreshInputTag = new(0x44424456); // "DBDV"

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeMinMaxInfo
    {
        public Win32Helper.POINT Reserved;
        public Win32Helper.POINT MaxSize;
        public Win32Helper.POINT MaxPosition;
        public Win32Helper.POINT MinTrackSize;
        public Win32Helper.POINT MaxTrackSize;
    }

    private const uint MonitorDefaultToNearest = 2;
    private const uint GaRoot = 2;
    private const int VkLButton = 0x01;
    private const int VkRButton = 0x02;
    private const int VkEscape = 0x1B;
    private const int MdtEffectiveDpi = 0;

    [LibraryImport("user32.dll")]
    private static partial IntPtr WindowFromPoint(Win32Helper.POINT point);

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial int SetCursorPos(int x, int y);

    [LibraryImport("user32.dll")]
    private static partial IntPtr GetParent(IntPtr hWnd);

    [LibraryImport("user32.dll")]
    private static partial IntPtr GetAncestor(IntPtr hWnd, uint gaFlags);

    [LibraryImport("user32.dll")]
    private static partial IntPtr GetShellWindow();

    [LibraryImport("user32.dll")]
    private static partial uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [LibraryImport("user32.dll")]
    private static partial short GetAsyncKeyState(int vKey);

    [LibraryImport("user32.dll")]
    private static partial IntPtr MonitorFromPoint(Win32Helper.POINT pt, uint dwFlags);

    [LibraryImport("shcore.dll")]
    private static partial int GetDpiForMonitor(
        IntPtr hmonitor,
        int dpiType,
        out uint dpiX,
        out uint dpiY);

    internal static bool IsAnyMouseButtonDown() =>
        (GetAsyncKeyState(VkLButton) & unchecked((short)0x8000)) != 0 ||
        (GetAsyncKeyState(VkRButton) & unchecked((short)0x8000)) != 0;

    internal static bool IsHudOrChildWindow(IntPtr hwnd, IntPtr activeHudHwnd)
    {
        if (hwnd == IntPtr.Zero || activeHudHwnd == IntPtr.Zero)
        {
            return false;
        }

        return hwnd == activeHudHwnd || GetAncestor(hwnd, GaRoot) == activeHudHwnd;
    }

    internal static bool IsCursorOutsideAllDeskBoxWindows(
        IntPtr primaryWidgetHwnd,
        IntPtr secondaryPopupHwnd = default)
    {
        if (!Win32Helper.GetCursorPos(out Win32Helper.POINT pt))
        {
            return false;
        }

        if (IsPointInsideVisibleWindow(primaryWidgetHwnd, pt) ||
            IsPointInsideVisibleWindow(secondaryPopupHwnd, pt))
        {
            return false;
        }

        IntPtr activeHudHwnd = Services.DesktopToastHudService.Shared.ActiveHudWindowHandle;
        IntPtr hitHwnd = WindowFromPoint(pt);
        if (IsHudOrChildWindow(hitHwnd, activeHudHwnd))
        {
            SetWindowTreeMousePassthrough(activeHudHwnd, passthrough: true);
            hitHwnd = WindowFromPoint(pt);
        }

        if (hitHwnd == IntPtr.Zero)
        {
            return false;
        }

        (DropTargetWindowKind kind, _) = ClassifyWindowDetailed(hitHwnd);
        return kind != DropTargetWindowKind.DeskBoxOwnWindow;
    }

    private static bool IsPointInsideVisibleWindow(IntPtr hwnd, Win32Helper.POINT pt)
    {
        if (hwnd == IntPtr.Zero ||
            !Win32Helper.IsWindowVisible(hwnd) ||
            !Win32Helper.GetWindowRect(hwnd, out Win32Helper.RECT rect))
        {
            return false;
        }

        return IsPointInsideRect(rect, pt);
    }

    internal static bool IsPointInsideRect(in Win32Helper.RECT rect, Win32Helper.POINT pt) =>
        rect.Right > rect.Left &&
        rect.Bottom > rect.Top &&
        pt.X >= rect.Left &&
        pt.X < rect.Right &&
        pt.Y >= rect.Top &&
        pt.Y < rect.Bottom;

    /// <summary>
    /// Selects a 1-physical-pixel neighbor around <paramref name="cursor"/> that
    /// stays strictly inside <paramref name="monitorBounds"/> (preventing edge
    /// clamping at 0px on any monitor, including negative multi-monitor spaces)
    /// and never steps into DeskBox's primary or secondary exclusion rectangles.
    /// </summary>
    internal static Win32Helper.POINT? ChooseSafeOnePixelNudgeTarget(
        Win32Helper.POINT cursor,
        in Win32Helper.RECT monitorBounds,
        Win32Helper.RECT? primaryExclusionRect = null,
        Win32Helper.RECT? secondaryExclusionRect = null,
        Func<Win32Helper.POINT, bool>? additionalAcceptPredicate = null)
    {
        if (monitorBounds.Right - monitorBounds.Left < 2 &&
            monitorBounds.Bottom - monitorBounds.Top < 2)
        {
            return null;
        }

        ReadOnlySpan<(int Dx, int Dy)> deltas =
        [
            (1, 0),
            (-1, 0),
            (0, 1),
            (0, -1),
        ];

        foreach ((int dx, int dy) in deltas)
        {
            var candidate = new Win32Helper.POINT
            {
                X = cursor.X + dx,
                Y = cursor.Y + dy,
            };
            if (!IsPointInsideRect(monitorBounds, candidate))
            {
                continue;
            }

            if (primaryExclusionRect is { } primary && IsPointInsideRect(primary, candidate))
            {
                continue;
            }

            if (secondaryExclusionRect is { } secondary && IsPointInsideRect(secondary, candidate))
            {
                continue;
            }

            if (additionalAcceptPredicate is not null && !additionalAcceptPredicate(candidate))
            {
                continue;
            }

            return candidate;
        }

        return null;
    }

    internal static OutboundCursorEnvironmentSnapshot SampleOutboundDragRefreshEnvironment(
        IntPtr primaryWidgetHwnd,
        IntPtr secondaryPopupHwnd = default)
    {
        bool buttonDown = IsAnyMouseButtonDown();
        if (!buttonDown || !Win32Helper.GetCursorPos(out Win32Helper.POINT pt))
        {
            return new OutboundCursorEnvironmentSnapshot(
                MouseButtonDown: false,
                IsOutsideEligibleTarget: false,
                CursorPoint: default,
                SafeNudgeTarget: null);
        }

        Win32Helper.RECT? primaryRect = TryGetVisibleWindowRect(primaryWidgetHwnd);
        Win32Helper.RECT? secondaryRect = TryGetVisibleWindowRect(secondaryPopupHwnd);
        if ((primaryRect is { } pRect && IsPointInsideRect(pRect, pt)) ||
            (secondaryRect is { } sRect && IsPointInsideRect(sRect, pt)))
        {
            return new OutboundCursorEnvironmentSnapshot(
                MouseButtonDown: true,
                IsOutsideEligibleTarget: false,
                CursorPoint: pt,
                SafeNudgeTarget: null,
                TargetKind: DropTargetWindowKind.DeskBoxOwnWindow);
        }

        IntPtr activeHudHwnd = Services.DesktopToastHudService.Shared.ActiveHudWindowHandle;
        IntPtr hitHwnd = WindowFromPoint(pt);
        if (IsHudOrChildWindow(hitHwnd, activeHudHwnd))
        {
            SetWindowTreeMousePassthrough(activeHudHwnd, passthrough: true);
            hitHwnd = WindowFromPoint(pt);
        }

        if (hitHwnd == IntPtr.Zero)
        {
            return new OutboundCursorEnvironmentSnapshot(
                MouseButtonDown: true,
                IsOutsideEligibleTarget: false,
                CursorPoint: pt,
                SafeNudgeTarget: null);
        }

        (DropTargetWindowKind kind, _) = ClassifyWindowDetailed(hitHwnd);
        bool eligible = kind is DropTargetWindowKind.ExplorerFileView or DropTargetWindowKind.NonExplorerWindow;
        if (!eligible)
        {
            return new OutboundCursorEnvironmentSnapshot(
                MouseButtonDown: true,
                IsOutsideEligibleTarget: false,
                CursorPoint: pt,
                SafeNudgeTarget: null,
                TargetKind: kind);
        }

        Win32Helper.RECT monitorBounds = GetMonitorBoundsForPoint(pt);
        Win32Helper.POINT? nudgeTarget = ChooseSafeOnePixelNudgeTarget(
            pt,
            in monitorBounds,
            primaryRect,
            secondaryRect,
            candidate =>
            {
                IntPtr candidateHwnd = WindowFromPoint(candidate);
                if (IsHudOrChildWindow(candidateHwnd, activeHudHwnd))
                {
                    return true;
                }

                return candidateHwnd != IntPtr.Zero &&
                       ClassifyWindowDetailed(candidateHwnd).Kind is
                           DropTargetWindowKind.ExplorerFileView or DropTargetWindowKind.NonExplorerWindow;
            });

        return new OutboundCursorEnvironmentSnapshot(
            MouseButtonDown: true,
            IsOutsideEligibleTarget: true,
            CursorPoint: pt,
            SafeNudgeTarget: nudgeTarget,
            TargetKind: kind);
    }

    /// <summary>
    /// Moves the physical cursor to <paramref name="target"/> in Per-Monitor DPI
    /// v2 coordinates (immune to mixed-DPI virtual-screen warping and mouse-speed
    /// scaling) and emits a zero-delta <c>MOUSEEVENTF_MOVE</c> pulse so both
    /// <c>ole32!DoDragDrop</c> and <c>DataExchange.dll</c> (<c>CoreInput</c>)
    /// re-run <c>DragOver</c> + <c>GiveFeedback</c> on Windows 10 and Windows 11.
    /// </summary>
    internal static bool TryMoveCursorAndPulseDragOver(Win32Helper.POINT target)
    {
        if (!IsAnyMouseButtonDown())
        {
            return false;
        }

        if (SetCursorPos(target.X, target.Y) == 0)
        {
            return false;
        }

        if (!Win32Helper.GetCursorPos(out Win32Helper.POINT actual) ||
            actual.X != target.X ||
            actual.Y != target.Y)
        {
            return false;
        }

        _ = Win32Helper.TrySendZeroDeltaMouseMovePulse(DragVisualRefreshInputTag, out _);
        return true;
    }

    private static Win32Helper.RECT? TryGetVisibleWindowRect(IntPtr hwnd) =>
        hwnd != IntPtr.Zero &&
        Win32Helper.IsWindowVisible(hwnd) &&
        Win32Helper.GetWindowRect(hwnd, out Win32Helper.RECT rect)
            ? rect
            : null;

    private static Win32Helper.RECT GetMonitorBoundsForPoint(Win32Helper.POINT pt)
    {
        IntPtr hMonitor = MonitorFromPoint(pt, MonitorDefaultToNearest);
        if (hMonitor != IntPtr.Zero)
        {
            var info = new MonitorAndBackdropInterop.NativeMonitorInfo
            {
                CbSize = (uint)sizeof(MonitorAndBackdropInterop.NativeMonitorInfo),
            };
            if (MonitorAndBackdropInterop.GetMonitorInfoW(hMonitor, &info) != 0 &&
                info.Monitor.Right > info.Monitor.Left &&
                info.Monitor.Bottom > info.Monitor.Top)
            {
                return new Win32Helper.RECT
                {
                    Left = info.Monitor.Left,
                    Top = info.Monitor.Top,
                    Right = info.Monitor.Right,
                    Bottom = info.Monitor.Bottom,
                };
            }
        }

        return new Win32Helper.RECT
        {
            Left = pt.X - 64,
            Top = pt.Y - 64,
            Right = pt.X + 64,
            Bottom = pt.Y + 64,
        };
    }

    internal static DropReleaseSnapshot SampleDropReleaseState()
    {
        bool escapePressed = (GetAsyncKeyState(VkEscape) & unchecked((short)0x8000)) != 0;
        if (!Win32Helper.GetCursorPos(out Win32Helper.POINT pt))
        {
            return new DropReleaseSnapshot(DropTargetWindowKind.Unknown, escapePressed, default, IsDesktopShellView: false);
        }

        IntPtr activeHudHwnd = Services.DesktopToastHudService.Shared.ActiveHudWindowHandle;
        IntPtr hitHwnd = WindowFromPoint(pt);
        if (IsHudOrChildWindow(hitHwnd, activeHudHwnd))
        {
            SetWindowTreeMousePassthrough(activeHudHwnd, passthrough: true);
            hitHwnd = WindowFromPoint(pt);
        }

        (DropTargetWindowKind kind, bool isDesktopShellView) = ClassifyWindowDetailed(hitHwnd);
        return new DropReleaseSnapshot(kind, escapePressed, pt, isDesktopShellView);
    }

    internal static DropTargetWindowKind ClassifyWindow(IntPtr hwnd) =>
        ClassifyWindowDetailed(hwnd).Kind;

    internal static Services.DragDrop.OutboundMoveTargetTrust ClassifyPointMoveTargetTrust(Win32Helper.POINT pt)
    {
        IntPtr activeHudHwnd = Services.DesktopToastHudService.Shared.ActiveHudWindowHandle;
        IntPtr hitHwnd = WindowFromPoint(pt);
        if (IsHudOrChildWindow(hitHwnd, activeHudHwnd))
        {
            SetWindowTreeMousePassthrough(activeHudHwnd, passthrough: true);
            hitHwnd = WindowFromPoint(pt);
        }

        if (hitHwnd == IntPtr.Zero)
        {
            return Services.DragDrop.OutboundMoveTargetTrust.Untrusted;
        }

        IntPtr rootHwnd = GetAncestor(hitHwnd, GaRoot);
        if (rootHwnd == IntPtr.Zero)
        {
            rootHwnd = hitHwnd;
        }

        bool isCurrentProcess = Win32Helper.IsWindowInCurrentProcess(hitHwnd) ||
            Win32Helper.IsWindowInCurrentProcess(rootHwnd);
        _ = GetWindowThreadProcessId(rootHwnd, out uint rootPid);
        bool isSettingsHost = rootPid != 0 &&
            (App.Current as App)?.BelongsToSettingsHostProcess(rootPid) == true;

        uint shellPid = 0;
        IntPtr shellHwnd = GetShellWindow();
        if (shellHwnd != IntPtr.Zero)
        {
            _ = GetWindowThreadProcessId(shellHwnd, out shellPid);
        }

        string rootClass = ReadWindowClassName(rootHwnd);
        string hitClass = hitHwnd == rootHwnd ? rootClass : ReadWindowClassName(hitHwnd);

        return Services.DragDrop.TargetAwareDropEffectPolicy.ClassifyMoveTargetWindow(
            hitHwnd,
            rootHwnd,
            rootPid,
            shellPid,
            isCurrentProcess,
            isSettingsHost,
            rootClass,
            hitClass,
            IsExplorerProcessId);
    }

    internal static bool IsPointOverTrustedMoveTarget(Win32Helper.POINT pt) =>
        ClassifyPointMoveTargetTrust(pt) != Services.DragDrop.OutboundMoveTargetTrust.Untrusted;

    internal static (DropTargetWindowKind Kind, bool IsDesktopShellView) ClassifyWindowDetailed(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
        {
            return (DropTargetWindowKind.Unknown, false);
        }

        if (IsHudOrChildWindow(hwnd, Services.DesktopToastHudService.Shared.ActiveHudWindowHandle))
        {
            return (DropTargetWindowKind.Unknown, false);
        }

        if (Win32Helper.IsWindowInCurrentProcess(hwnd))
        {
            return (DropTargetWindowKind.DeskBoxOwnWindow, false);
        }

        IntPtr rootHwnd = GetAncestor(hwnd, GaRoot);
        if (rootHwnd == IntPtr.Zero)
        {
            rootHwnd = hwnd;
        }

        if (rootHwnd != hwnd && Win32Helper.IsWindowInCurrentProcess(rootHwnd))
        {
            return (DropTargetWindowKind.DeskBoxOwnWindow, false);
        }

        _ = GetWindowThreadProcessId(hwnd, out uint pid);
        _ = GetWindowThreadProcessId(rootHwnd, out uint rootPid);
        if (pid == 0 && rootPid == 0)
        {
            return (DropTargetWindowKind.Unknown, false);
        }

        if ((pid != 0 && (App.Current as App)?.BelongsToSettingsHostProcess(pid) == true) ||
            (rootPid != 0 && (App.Current as App)?.BelongsToSettingsHostProcess(rootPid) == true))
        {
            return (DropTargetWindowKind.DeskBoxOwnWindow, false);
        }

        string rootClass = ReadWindowClassName(rootHwnd);
        bool isDesktopShellView = IsDesktopRootWindowClass(rootClass);

        // Wallpaper engines (Wallpaper Engine, Lively, etc.) parent their child
        // render HWND inside Explorer's WorkerW/Progman desktop root. Check the
        // root HWND's PID for desktop roots so child wallpaper windows still
        // classify as the Desktop shell view rather than NonExplorerWindow.
        if (isDesktopShellView && IsExplorerProcessId(rootPid))
        {
            return (DropTargetWindowKind.ExplorerFileView, true);
        }

        bool isExplorerProcess = IsExplorerProcessId(pid) ||
            (rootPid != 0 && rootPid != pid && IsExplorerProcessId(rootPid));
        if (!isExplorerProcess)
        {
            return (DropTargetWindowKind.NonExplorerWindow, false);
        }

        // Inside explorer.exe, distinguish Desktop / Folder file views (and
        // Explorer's native #32768 drop confirmation menu) from the taskbar /
        // tray so hovering a taskbar button is never treated as a folder drop.
        IntPtr current = hwnd;
        for (int depth = 0; depth < 8 && current != IntPtr.Zero; depth++)
        {
            string className = ReadWindowClassName(current);
            if (IsExplorerTaskbarOrTrayClass(className))
            {
                return (DropTargetWindowKind.Unknown, false);
            }

            if (IsExplorerFileViewOrMenuClass(className))
            {
                return (DropTargetWindowKind.ExplorerFileView, isDesktopShellView);
            }

            current = GetParent(current);
        }

        if (rootHwnd != hwnd)
        {
            if (IsExplorerTaskbarOrTrayClass(rootClass))
            {
                return (DropTargetWindowKind.Unknown, false);
            }

            if (IsExplorerFileViewOrMenuClass(rootClass))
            {
                return (DropTargetWindowKind.ExplorerFileView, isDesktopShellView);
            }
        }

        return (DropTargetWindowKind.Unknown, false);
    }

    internal static bool IsDesktopRootWindowClass(string className) =>
        className.Equals("Progman", StringComparison.Ordinal) ||
        className.Equals("WorkerW", StringComparison.Ordinal);

    internal static bool IsExplorerTaskbarOrTrayClass(string className) =>
        className.Equals("Shell_TrayWnd", StringComparison.Ordinal) ||
        className.Equals("Shell_SecondaryTrayWnd", StringComparison.Ordinal) ||
        className.Equals("NotifyIconOverflowWindow", StringComparison.Ordinal) ||
        className.Equals("TopLevelWindowForOverflowXamlIsland", StringComparison.Ordinal);

    internal static bool IsExplorerFileViewOrMenuClass(string className) =>
        className.Equals("Progman", StringComparison.Ordinal) ||
        className.Equals("WorkerW", StringComparison.Ordinal) ||
        className.Equals("CabinetWClass", StringComparison.Ordinal) ||
        className.Equals("ExploreWClass", StringComparison.Ordinal) ||
        className.Equals("SHELLDLL_DefView", StringComparison.Ordinal) ||
        className.Equals("DirectUIHWND", StringComparison.Ordinal) ||
        className.Equals("SysListView32", StringComparison.Ordinal) ||
        className.Equals("#32768", StringComparison.Ordinal);

    internal static void GetCursorMonitorWorkArea(
        out MonitorAndBackdropInterop.NativeRect workArea,
        out double rasterizationScale)
    {
        rasterizationScale = 1.0;
        workArea = new MonitorAndBackdropInterop.NativeRect
        {
            Left = 0,
            Top = 0,
            Right = 1920,
            Bottom = 1040,
        };

        if (!Win32Helper.GetCursorPos(out Win32Helper.POINT pt))
        {
            pt = new Win32Helper.POINT { X = 960, Y = 540 };
        }

        IntPtr hMonitor = MonitorFromPoint(pt, MonitorDefaultToNearest);
        if (hMonitor == IntPtr.Zero)
        {
            return;
        }

        var info = new MonitorAndBackdropInterop.NativeMonitorInfo
        {
            CbSize = (uint)sizeof(MonitorAndBackdropInterop.NativeMonitorInfo),
        };
        if (MonitorAndBackdropInterop.GetMonitorInfoW(hMonitor, &info) != 0 &&
            info.Work.Right > info.Work.Left &&
            info.Work.Bottom > info.Work.Top)
        {
            workArea = info.Work;
        }

        try
        {
            if (GetDpiForMonitor(hMonitor, MdtEffectiveDpi, out uint dpiX, out _) == 0 && dpiX > 0)
            {
                rasterizationScale = dpiX / 96.0;
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            rasterizationScale = 1.0;
        }
    }

    internal static void ApplyNoActivateHudWindowStyles(IntPtr hwnd)
    {
        EnsureNoActivateExtendedStyle(hwnd);

        int style = Win32Helper.GetWindowLong(hwnd, Win32Helper.GWL_STYLE);
        style &= ~(Win32Helper.WS_CAPTION |
            Win32Helper.WS_BORDER |
            Win32Helper.WS_DLGFRAME |
            Win32Helper.WS_THICKFRAME);

        _ = Win32Helper.SetWindowLong(hwnd, Win32Helper.GWL_STYLE, style);

        _ = Win32Helper.SetWindowPos(
            hwnd,
            Win32Helper.HWND_TOPMOST,
            0,
            0,
            0,
            0,
            Win32Helper.SWP_NOMOVE |
                Win32Helper.SWP_NOSIZE |
                Win32Helper.SWP_NOACTIVATE |
                Win32Helper.SWP_FRAMECHANGED);

        // Borderless and shadowless on every OS: the frosted pill is clipped by
        // the backdrop element's own rounded mask, so no DWM frame, border, or
        // corner preference is wanted at all.
        Win32Helper.EnableTransparentWindowHost(hwnd);
        Win32Helper.DisableWindowFrameShadow(hwnd);

        SetCloaked(hwnd, cloaked: true);
    }

    internal static void EnsureNoActivateExtendedStyle(IntPtr hwnd)
    {
        int extendedStyle = Win32Helper.GetWindowLong(hwnd, Win32Helper.GWL_EXSTYLE);
        // WS_EX_LAYERED is deliberately absent: DWM system backdrops (the fixed
        // acrylic pill) do not render through the layered redirection surface,
        // and mouse/OLE passthrough relies on WS_EX_TRANSPARENT alone — the same
        // mechanism the stack popover host uses for external drag passthrough.
        int desired = extendedStyle |
            Win32Helper.WS_EX_TOOLWINDOW |
            Win32Helper.WS_EX_TOPMOST |
            Win32Helper.WS_EX_NOACTIVATE;
        desired &= ~Win32Helper.WS_EX_LAYERED;
        if (desired != extendedStyle)
        {
            _ = Win32Helper.SetWindowLong(hwnd, Win32Helper.GWL_EXSTYLE, desired);
        }
    }

    internal static void SetWindowTreeMousePassthrough(IntPtr rootHwnd, bool passthrough)
    {
        if (rootHwnd == IntPtr.Zero)
        {
            return;
        }

        bool rootChanged = ApplyTransparentStyleSingleWindow(rootHwnd, passthrough);
        _ = Win32Helper.EnumChildWindows(
            rootHwnd,
            (childHwnd, lParam) =>
            {
                _ = ApplyTransparentStyleSingleWindow(childHwnd, passthrough);
                return true;
            },
            IntPtr.Zero);

        if (rootChanged)
        {
            _ = Win32Helper.SetWindowPos(
                rootHwnd,
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
        }
    }

    private static bool ApplyTransparentStyleSingleWindow(IntPtr hwnd, bool passthrough)
    {
        if (hwnd == IntPtr.Zero)
        {
            return false;
        }

        int extendedStyle = Win32Helper.GetWindowLong(hwnd, Win32Helper.GWL_EXSTYLE);
        int updatedStyle = passthrough
            ? (extendedStyle | Win32Helper.WS_EX_TRANSPARENT)
            : (extendedStyle & ~Win32Helper.WS_EX_TRANSPARENT);
        if (updatedStyle == extendedStyle)
        {
            return false;
        }

        _ = Win32Helper.SetWindowLong(hwnd, Win32Helper.GWL_EXSTYLE, updatedStyle);
        return true;
    }

    internal static void RelaxMinTrackSize(IntPtr lParam)
    {
        if (lParam == IntPtr.Zero)
        {
            return;
        }

        NativeMinMaxInfo* info = (NativeMinMaxInfo*)lParam;
        info->MinTrackSize.X = 1;
        info->MinTrackSize.Y = 1;
    }

    internal static void SetCloaked(IntPtr hwnd, bool cloaked)
    {
        int value = cloaked ? 1 : 0;
        Win32Helper.TrySetDwmWindowAttribute(hwnd, DwmwaCloak, ref value);
    }

    internal static HashSet<string> ReadDismissedHintsFile(string filePath)
    {
        var loaded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (File.Exists(filePath))
            {
                foreach (string rawLine in File.ReadAllLines(filePath, System.Text.Encoding.UTF8))
                {
                    string trimmed = rawLine.Trim();
                    if (trimmed.Length > 0 && !trimmed.StartsWith('#'))
                    {
                        loaded.Add(trimmed);
                    }
                }
            }
        }
        catch
        {
        }

        return loaded;
    }

    internal static void WriteDismissedHintsFile(
        string dataDirectory,
        string fileName,
        IEnumerable<string> orderedIds)
    {
        try
        {
            Directory.CreateDirectory(dataDirectory);
            string targetPath = Path.Combine(dataDirectory, fileName);
            string tempPath = Path.Combine(
                dataDirectory,
                $"{fileName}.{Guid.NewGuid():N}.tmp");
            File.WriteAllLines(tempPath, orderedIds, System.Text.Encoding.UTF8);
            File.Move(tempPath, targetPath, overwrite: true);
        }
        catch (Exception ex)
        {
            App.LogVerbose($"[DragHud] Persist dismissed hints failed: {ex.Message}");
        }
    }

    internal static void DeleteDismissedHintsFile(string filePath)
    {
        try
        {
            if (File.Exists(filePath))
            {
                File.Delete(filePath);
            }
        }
        catch
        {
        }
    }

    internal static (bool Exists, long LastWriteUtcTicks, long Length) GetDismissedHintsFileStamp(
        string filePath)
    {
        try
        {
            var info = new FileInfo(filePath);
            if (!info.Exists)
            {
                return (false, 0, 0);
            }

            return (true, info.LastWriteTimeUtc.Ticks, info.Length);
        }
        catch
        {
            return (false, 0, 0);
        }
    }

    private static bool IsExplorerProcessId(uint pid)
    {
        if (pid == 0)
        {
            return false;
        }

        IntPtr shellHwnd = GetShellWindow();
        if (shellHwnd != IntPtr.Zero)
        {
            _ = GetWindowThreadProcessId(shellHwnd, out uint shellPid);
            if (shellPid != 0 && shellPid == pid)
            {
                return true;
            }
        }

        return ProcessImageNameResolver.IsExplorerProcess(pid);
    }

    private static string ReadWindowClassName(IntPtr hwnd)
    {
        char* buffer = stackalloc char[128];
        int length = MonitorAndBackdropInterop.GetClassNameW(hwnd, buffer, 128);
        return length > 0 ? new string(buffer, 0, length) : string.Empty;
    }
}
