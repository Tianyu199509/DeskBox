using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using DeskBox.Helpers;
using DeskBox.Platform;
using Microsoft.UI.Dispatching;
using static DeskBox.Platform.MonitorAndBackdropInterop;

namespace DeskBox.Services;

/// <summary>
/// Tracks per-monitor occlusion caused by maximized, borderless work-area-filling,
/// or fullscreen non-DeskBox application windows so desktop-layer widgets on
/// occluded monitors can suspend timers and visual updates without hiding their HWNDs.
/// </summary>
public sealed unsafe class MonitorOcclusionTracker : IDisposable
{
    private const uint EventSystemForeground = 0x0003;
    private const uint EventSystemMinimizeStart = 0x0016;
    private const uint EventSystemMinimizeEnd = 0x0017;
    private const uint EventObjectDestroy = 0x8001;
    private const uint EventObjectHide = 0x8003;
    private const uint EventObjectLocationChange = 0x800B;

    private const uint WinEventOutOfContext = 0x0000;
    private const uint WinEventSkipOwnProcess = 0x0002;
    private const int ObjIdWindow = 0;

    private const int GwlExStyle = -20;
    private const long WsExToolWindow = 0x00000080L;
    private const long WsExAppWindow = 0x00040000L;
    private const long WsExTransparent = 0x00000020L;
    private const long WsExNoActivate = 0x08000000L;

    private const int DwmwaExtendedFrameBounds = 9;
    private const int DwmwaCloaked = 14;
    private const uint MonitorDefaultToNull = 0;
    private const uint MonitorDefaultToNearest = 2;

    public static readonly TimeSpan DefaultDebounceInterval = TimeSpan.FromMilliseconds(150);
    public const double WorkAreaCoverageThreshold = 0.98;

    private static MonitorOcclusionTracker? s_activeInstance;

    private readonly DispatcherQueue _dispatcherQueue;
    private readonly Func<IntPtr, bool> _isDeskBoxWindow;
    private readonly TimeSpan _debounceInterval;
    private DispatcherQueueTimer? _debounceTimer;
    private IntPtr _foregroundHook;
    private IntPtr _minimizeHook;
    private IntPtr _objectVisibilityHook;
    private IntPtr _locationHook;
    private HashSet<IntPtr> _occludedMonitors = [];
    private int _debouncePending;
    private bool _started;
    private volatile bool _disposed;

    public event Action<IReadOnlySet<IntPtr>>? OccludedMonitorsChanged;

    public MonitorOcclusionTracker(
        DispatcherQueue dispatcherQueue,
        Func<IntPtr, bool> isDeskBoxWindow,
        TimeSpan? debounceInterval = null)
    {
        _dispatcherQueue = dispatcherQueue ?? throw new ArgumentNullException(nameof(dispatcherQueue));
        _isDeskBoxWindow = isDeskBoxWindow ?? throw new ArgumentNullException(nameof(isDeskBoxWindow));
        _debounceInterval = debounceInterval ?? DefaultDebounceInterval;
    }

    public IReadOnlySet<IntPtr> OccludedMonitors => _occludedMonitors;

    public bool IsMonitorOccluded(IntPtr hMonitor) =>
        hMonitor != IntPtr.Zero && _occludedMonitors.Contains(hMonitor);

    public static IntPtr GetMonitorFromWindow(IntPtr hwnd) =>
        hwnd == IntPtr.Zero ? IntPtr.Zero : MonitorFromWindow(hwnd, MonitorDefaultToNearest);

    public void Start()
    {
        if (_disposed || _started)
        {
            return;
        }

        _started = true;
        Volatile.Write(ref s_activeInstance, this);

        _debounceTimer = _dispatcherQueue.CreateTimer();
        _debounceTimer.IsRepeating = false;
        _debounceTimer.Interval = _debounceInterval;
        _debounceTimer.Tick += OnDebounceTimerTick;

        uint flags = WinEventOutOfContext | WinEventSkipOwnProcess;
        delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr, int, int, uint, uint, void> callback = &OnWinEventCallback;

        _foregroundHook = SetWinEventHook(
            EventSystemForeground,
            EventSystemForeground,
            IntPtr.Zero,
            callback,
            0,
            0,
            flags);
        _minimizeHook = SetWinEventHook(
            EventSystemMinimizeStart,
            EventSystemMinimizeEnd,
            IntPtr.Zero,
            callback,
            0,
            0,
            flags);
        _objectVisibilityHook = SetWinEventHook(
            EventObjectDestroy,
            EventObjectHide,
            IntPtr.Zero,
            callback,
            0,
            0,
            flags);
        _locationHook = SetWinEventHook(
            EventObjectLocationChange,
            EventObjectLocationChange,
            IntPtr.Zero,
            callback,
            0,
            0,
            flags);

        RefreshNow();
    }

    public void RefreshNow()
    {
        if (_disposed)
        {
            return;
        }

        if (!_dispatcherQueue.HasThreadAccess)
        {
            _dispatcherQueue.TryEnqueue(RefreshNow);
            return;
        }

        Interlocked.Exchange(ref _debouncePending, 0);
        _debounceTimer?.Stop();
        EvaluateAndPublish();
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static void OnWinEventCallback(
        IntPtr hWinEventHook,
        uint eventType,
        IntPtr hwnd,
        int idObject,
        int idChild,
        uint dwEventThread,
        uint dwmsEventTime)
    {
        _ = hWinEventHook;
        _ = dwEventThread;
        _ = dwmsEventTime;

        if (eventType >= EventObjectDestroy && (idObject != ObjIdWindow || idChild != 0))
        {
            return;
        }

        MonitorOcclusionTracker? instance = Volatile.Read(ref s_activeInstance);
        instance?.ScheduleDebouncedEvaluation();
    }

    private void ScheduleDebouncedEvaluation()
    {
        if (_disposed)
        {
            return;
        }

        if (Interlocked.CompareExchange(ref _debouncePending, 1, 0) != 0)
        {
            return;
        }

        if (_dispatcherQueue.HasThreadAccess)
        {
            ArmDebounceTimerOnUiThread();
            return;
        }

        if (!_dispatcherQueue.TryEnqueue(ArmDebounceTimerOnUiThread))
        {
            Interlocked.Exchange(ref _debouncePending, 0);
        }
    }

    private void ArmDebounceTimerOnUiThread()
    {
        if (_disposed || _debounceTimer is null)
        {
            Interlocked.Exchange(ref _debouncePending, 0);
            return;
        }

        _debounceTimer.Stop();
        _debounceTimer.Interval = _debounceInterval;
        _debounceTimer.Start();
    }

    private void OnDebounceTimerTick(DispatcherQueueTimer sender, object args)
    {
        sender.Stop();
        Interlocked.Exchange(ref _debouncePending, 0);
        if (_disposed)
        {
            return;
        }

        EvaluateAndPublish();
    }

    private void EvaluateAndPublish()
    {
        HashSet<IntPtr> nextOccluded = CaptureCurrentOccludedMonitors();
        if (_occludedMonitors.SetEquals(nextOccluded))
        {
            return;
        }

        _occludedMonitors = nextOccluded;
        OccludedMonitorsChanged?.Invoke(nextOccluded);
    }

    private HashSet<IntPtr> CaptureCurrentOccludedMonitors()
    {
        IntPtr foregroundHwnd = Win32Helper.GetForegroundWindow();
        bool isForegroundDeskBox = false;
        string foregroundClassName = string.Empty;
        IntPtr foregroundMonitor = IntPtr.Zero;

        if (foregroundHwnd != IntPtr.Zero)
        {
            IntPtr foregroundRoot = Win32Helper.GetAncestor(foregroundHwnd, Win32Helper.GA_ROOT);
            if (foregroundRoot == IntPtr.Zero)
            {
                foregroundRoot = foregroundHwnd;
            }

            isForegroundDeskBox = _isDeskBoxWindow(foregroundHwnd) || _isDeskBoxWindow(foregroundRoot);
            if (isForegroundDeskBox)
            {
                return [];
            }

            foregroundClassName = ReadWindowClassName(foregroundRoot);
            if (IsDesktopShellClassName(foregroundClassName))
            {
                return [];
            }

            foregroundMonitor = MonitorFromWindow(foregroundRoot, MonitorDefaultToNull);
        }

        var monitors = new Dictionary<IntPtr, MonitorBounds>(4);
        var candidates = new List<WindowOcclusionCandidate>(32);
        var context = new EnumContext(this, monitors, candidates);
        GCHandle handle = GCHandle.Alloc(context);
        try
        {
            EnumWindows(&EnumTopLevelWindowCallback, GCHandle.ToIntPtr(handle));
        }
        finally
        {
            handle.Free();
        }

        bool isFullscreenActive = Win32Helper.IsFullscreenAppActive();
        if (isFullscreenActive && foregroundMonitor != IntPtr.Zero && !monitors.ContainsKey(foregroundMonitor))
        {
            if (TryGetMonitorBounds(foregroundMonitor, out MonitorBounds fgBounds))
            {
                monitors[foregroundMonitor] = fgBounds;
            }
        }

        return EvaluateOccludedMonitors(
            monitors,
            candidates,
            foregroundClassName,
            isForegroundDeskBox,
            isFullscreenActive,
            foregroundMonitor);
    }

    private sealed class EnumContext(
        MonitorOcclusionTracker owner,
        Dictionary<IntPtr, MonitorBounds> monitors,
        List<WindowOcclusionCandidate> candidates)
    {
        public readonly MonitorOcclusionTracker Owner = owner;
        public readonly Dictionary<IntPtr, MonitorBounds> Monitors = monitors;
        public readonly List<WindowOcclusionCandidate> Candidates = candidates;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int EnumTopLevelWindowCallback(IntPtr hwnd, IntPtr lParam)
    {
        if (hwnd == IntPtr.Zero || lParam == IntPtr.Zero)
        {
            return 1;
        }

        if (IsWindowVisible(hwnd) == 0 || IsIconic(hwnd) != 0)
        {
            return 1;
        }

        var context = (EnumContext?)GCHandle.FromIntPtr(lParam).Target;
        if (context is null)
        {
            return 0;
        }

        if (IsWindowCloaked(hwnd))
        {
            return 1;
        }

        long exStyle = GetWindowExtendedStyle(hwnd);
        bool isToolOverlay =
            ((exStyle & WsExToolWindow) != 0 && (exStyle & WsExAppWindow) == 0) ||
            (exStyle & WsExTransparent) != 0 ||
            ((exStyle & WsExNoActivate) != 0 && (exStyle & WsExAppWindow) == 0);
        if (isToolOverlay)
        {
            return 1;
        }

        if (context.Owner._isDeskBoxWindow(hwnd))
        {
            return 1;
        }

        string className = ReadWindowClassName(hwnd);
        if (IsExcludedShellClassName(className))
        {
            return 1;
        }

        if (!TryGetWindowBounds(hwnd, out NativeRect rect))
        {
            return 1;
        }

        if (rect.Right <= rect.Left || rect.Bottom <= rect.Top)
        {
            return 1;
        }

        IntPtr hMonitor = MonitorFromWindow(hwnd, MonitorDefaultToNull);
        if (hMonitor == IntPtr.Zero)
        {
            return 1;
        }

        if (!context.Monitors.ContainsKey(hMonitor))
        {
            if (!TryGetMonitorBounds(hMonitor, out MonitorBounds monitorBounds))
            {
                return 1;
            }

            context.Monitors[hMonitor] = monitorBounds;
        }

        context.Candidates.Add(new WindowOcclusionCandidate(
            hwnd,
            hMonitor,
            IsVisible: true,
            IsMinimized: false,
            IsCloaked: false,
            IsZoomed: IsZoomed(hwnd) != 0,
            IsDeskBoxWindow: false,
            IsToolOverlay: false,
            className,
            rect.Left,
            rect.Top,
            rect.Right,
            rect.Bottom));
        return 1;
    }

    internal static HashSet<IntPtr> EvaluateOccludedMonitors(
        IReadOnlyDictionary<IntPtr, MonitorBounds> monitors,
        IReadOnlyList<WindowOcclusionCandidate> candidates,
        string? foregroundClassName,
        bool isForegroundDeskBoxWindow,
        bool isFullscreenAppActive,
        IntPtr foregroundMonitorHandle)
    {
        if (isForegroundDeskBoxWindow || IsDesktopShellClassName(foregroundClassName))
        {
            return [];
        }

        var occluded = new HashSet<IntPtr>();
        if (isFullscreenAppActive && foregroundMonitorHandle != IntPtr.Zero)
        {
            occluded.Add(foregroundMonitorHandle);
        }

        for (int i = 0; i < candidates.Count; i++)
        {
            WindowOcclusionCandidate candidate = candidates[i];
            if (candidate.MonitorHandle == IntPtr.Zero ||
                occluded.Contains(candidate.MonitorHandle))
            {
                continue;
            }

            if (!candidate.IsVisible ||
                candidate.IsMinimized ||
                candidate.IsCloaked ||
                candidate.IsDeskBoxWindow ||
                candidate.IsToolOverlay ||
                IsExcludedShellClassName(candidate.ClassName))
            {
                continue;
            }

            if (!monitors.TryGetValue(candidate.MonitorHandle, out MonitorBounds bounds))
            {
                continue;
            }

            if (DoesCandidateOccludeMonitor(candidate, bounds))
            {
                occluded.Add(candidate.MonitorHandle);
            }
        }

        return occluded;
    }

    internal static bool DoesCandidateOccludeMonitor(
        WindowOcclusionCandidate candidate,
        MonitorBounds bounds)
    {
        if (candidate.IsZoomed)
        {
            return true;
        }

        long workArea = bounds.WorkArea;
        if (workArea > 0)
        {
            long workIntersection = ComputeIntersectionArea(
                candidate.Left,
                candidate.Top,
                candidate.Right,
                candidate.Bottom,
                bounds.WorkLeft,
                bounds.WorkTop,
                bounds.WorkRight,
                bounds.WorkBottom);
            if (workIntersection >= (long)Math.Ceiling(workArea * WorkAreaCoverageThreshold))
            {
                return true;
            }
        }

        long monitorArea = bounds.MonitorArea;
        if (monitorArea > 0)
        {
            long monitorIntersection = ComputeIntersectionArea(
                candidate.Left,
                candidate.Top,
                candidate.Right,
                candidate.Bottom,
                bounds.MonitorLeft,
                bounds.MonitorTop,
                bounds.MonitorRight,
                bounds.MonitorBottom);
            if (monitorIntersection >= (long)Math.Ceiling(monitorArea * WorkAreaCoverageThreshold))
            {
                return true;
            }
        }

        return false;
    }

    internal static bool IsDesktopShellClassName(string? className) =>
        string.Equals(className, "Progman", StringComparison.Ordinal) ||
        string.Equals(className, "WorkerW", StringComparison.Ordinal);

    internal static bool IsExcludedShellClassName(string? className) =>
        IsDesktopShellClassName(className) ||
        string.Equals(className, "Shell_TrayWnd", StringComparison.Ordinal) ||
        string.Equals(className, "Shell_SecondaryTrayWnd", StringComparison.Ordinal) ||
        string.Equals(className, "NotifyIconOverflowWindow", StringComparison.Ordinal) ||
        string.Equals(className, "Windows.UI.Core.CoreWindow", StringComparison.Ordinal) ||
        string.Equals(className, "XamlExplorerHostIslandWindow", StringComparison.Ordinal);

    private static long ComputeIntersectionArea(
        int leftA,
        int topA,
        int rightA,
        int bottomA,
        int leftB,
        int topB,
        int rightB,
        int bottomB)
    {
        int left = Math.Max(leftA, leftB);
        int top = Math.Max(topA, topB);
        int right = Math.Min(rightA, rightB);
        int bottom = Math.Min(bottomA, bottomB);
        if (right <= left || bottom <= top)
        {
            return 0;
        }

        return (long)(right - left) * (bottom - top);
    }

    private static bool IsWindowCloaked(IntPtr hwnd)
    {
        int cloaked = 0;
        int hr = DwmGetWindowAttribute(hwnd, DwmwaCloaked, &cloaked, sizeof(int));
        return hr == 0 && cloaked != 0;
    }

    private static bool TryGetWindowBounds(IntPtr hwnd, out NativeRect rect)
    {
        NativeRect frameRect = default;
        if (DwmGetWindowAttribute(hwnd, DwmwaExtendedFrameBounds, &frameRect, sizeof(NativeRect)) == 0 &&
            frameRect.Right > frameRect.Left &&
            frameRect.Bottom > frameRect.Top)
        {
            rect = frameRect;
            return true;
        }

        return GetWindowRect(hwnd, out rect) != 0;
    }

    private static bool TryGetMonitorBounds(IntPtr hMonitor, out MonitorBounds bounds)
    {
        var info = new NativeMonitorInfo
        {
            CbSize = (uint)sizeof(NativeMonitorInfo)
        };
        if (GetMonitorInfoW(hMonitor, &info) == 0)
        {
            bounds = default;
            return false;
        }

        bounds = new MonitorBounds(
            hMonitor,
            info.Monitor.Left,
            info.Monitor.Top,
            info.Monitor.Right,
            info.Monitor.Bottom,
            info.Work.Left,
            info.Work.Top,
            info.Work.Right,
            info.Work.Bottom);
        return true;
    }

    private static string ReadWindowClassName(IntPtr hwnd)
    {
        char* buffer = stackalloc char[128];
        int length = GetClassNameW(hwnd, buffer, 128);
        return length > 0 ? new string(buffer, 0, length) : string.Empty;
    }

    private static long GetWindowExtendedStyle(IntPtr hwnd) =>
        IntPtr.Size == 8
            ? GetWindowLongPtrW(hwnd, GwlExStyle).ToInt64()
            : GetWindowLongW(hwnd, GwlExStyle);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Interlocked.CompareExchange(ref s_activeInstance, null, this);
        Interlocked.Exchange(ref _debouncePending, 0);

        UnhookAndClear(ref _foregroundHook);
        UnhookAndClear(ref _minimizeHook);
        UnhookAndClear(ref _objectVisibilityHook);
        UnhookAndClear(ref _locationHook);

        if (_debounceTimer is { } timer)
        {
            _debounceTimer = null;
            if (_dispatcherQueue.HasThreadAccess)
            {
                timer.Stop();
                timer.Tick -= OnDebounceTimerTick;
            }
            else
            {
                _dispatcherQueue.TryEnqueue(() =>
                {
                    timer.Stop();
                    timer.Tick -= OnDebounceTimerTick;
                });
            }
        }

        _occludedMonitors = [];
    }

    private static void UnhookAndClear(ref IntPtr hook)
    {
        IntPtr existing = Interlocked.Exchange(ref hook, IntPtr.Zero);
        if (existing != IntPtr.Zero)
        {
            _ = UnhookWinEvent(existing);
        }
    }

    internal readonly record struct MonitorBounds(
        IntPtr MonitorHandle,
        int MonitorLeft,
        int MonitorTop,
        int MonitorRight,
        int MonitorBottom,
        int WorkLeft,
        int WorkTop,
        int WorkRight,
        int WorkBottom)
    {
        public long MonitorArea =>
            Math.Max(0L, (long)(MonitorRight - MonitorLeft)) *
            Math.Max(0L, (long)(MonitorBottom - MonitorTop));

        public long WorkArea =>
            Math.Max(0L, (long)(WorkRight - WorkLeft)) *
            Math.Max(0L, (long)(WorkBottom - WorkTop));
    }

    internal readonly record struct WindowOcclusionCandidate(
        IntPtr WindowHandle,
        IntPtr MonitorHandle,
        bool IsVisible,
        bool IsMinimized,
        bool IsCloaked,
        bool IsZoomed,
        bool IsDeskBoxWindow,
        bool IsToolOverlay,
        string ClassName,
        int Left,
        int Top,
        int Right,
        int Bottom);
}
