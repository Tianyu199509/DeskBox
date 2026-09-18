using DeskBox.Helpers;
using DeskBox.Platform;
using Microsoft.UI.Dispatching;

namespace DeskBox.Services;

/// <summary>
/// Monitors display configuration changes (hot-plug add/remove, resolution
/// changes, DPI changes) by periodically polling the display topology via
/// Win32 <c>EnumDisplayMonitors</c>.
/// <para>
/// This service provides a consolidated <see cref="DisplaysChanged"/> event
/// with debouncing, allowing the application to reposition widgets and
/// invalidate caches when the display topology changes.
/// </para>
/// <para>
/// The native change signal is already delivered through other windows:
/// <c>WidgetDisplayChangeWatcher</c> subclasses each widget window for
/// <c>WM_DISPLAYCHANGE</c> / <c>WM_DPICHANGED</c> / <c>WM_SETTINGCHANGE</c>
/// (work area), and the lifecycle paths call <see cref="RefreshNow"/> on
/// resume, unlock and shell restart. Polling is therefore a fallback for the
/// cases those cannot observe (for example no widget window is alive yet),
/// which is why its interval is deliberately coarse.
/// </para>
/// </summary>
public sealed class DisplayAreaWatcherService : IDisposable
{
    private const int PollIntervalMs = 10000;
    private const int EventDrivenPollIntervalMs = 30000;
    private const int DebounceDelayMs = 500;
    private const uint WmDisplayChange = 0x007E;
    private const uint WmSettingChange = 0x001A;
    private const uint WmDpiChanged = 0x02E0;
    private const uint WmNcDestroy = 0x0082;
    private const ulong SpiSetWorkArea = 0x002F;
    private static readonly UIntPtr MessageSubclassId = new(0xDDB3);

    private readonly DispatcherQueue _dispatcherQueue;
    private readonly DispatcherQueueTimer _pollTimer;
    private readonly DispatcherQueueTimer _debounceTimer;
    private readonly Win32Helper.SubclassProc _messageSubclassProc;
    private IntPtr _messageWindow;
    private bool _isMessageSubclassInstalled;
    private bool _isEventDriven;
    private bool _isDisposed;
    private int _displayCount;
    private string _displaySignature = string.Empty;

    /// <summary>
    /// Fired when the set of displays has changed (add, remove, resolution,
    /// or DPI change).  Fires after a short debounce to avoid spamming
    /// during rapid display configuration changes.
    /// </summary>
    public event Action? DisplaysChanged;

    /// <summary>
    /// The current number of displays.
    /// </summary>
    public int DisplayCount => _displayCount;

    public DisplayAreaWatcherService(DispatcherQueue dispatcherQueue)
    {
        _dispatcherQueue = dispatcherQueue;
        _messageSubclassProc = MessageWindowSubclassProc;
        _pollTimer = dispatcherQueue.CreateTimer();
        _pollTimer.Interval = TimeSpan.FromMilliseconds(PollIntervalMs);
        _pollTimer.IsRepeating = true;
        _pollTimer.Tick += PollTimer_Tick;

        _debounceTimer = dispatcherQueue.CreateTimer();
        _debounceTimer.Interval = TimeSpan.FromMilliseconds(DebounceDelayMs);
        _debounceTimer.IsRepeating = false;
        _debounceTimer.Tick += DebounceTimer_Tick;
    }

    public void Start()
    {
        if (_isDisposed)
        {
            return;
        }

        _displayCount = CountDisplays();
        _displaySignature = CaptureCurrentSignature();
        App.Log($"[DisplayAreaWatcher] Started, initial display count: {_displayCount}, signature: {_displaySignature}");

        // After AttachToMessageWindow the timer is already running at the slow
        // safety interval; starting again is a no-op but keeps a future
        // attach-before-start ordering on the slow poll instead of none.
        _pollTimer.Start();
    }

    /// <summary>
    /// Subscribes this watcher to the native topology signals
    /// (<c>WM_DISPLAYCHANGE</c>, <c>WM_DPICHANGED</c>, and
    /// <c>WM_SETTINGCHANGE</c> for the work area) on an application-lifetime
    /// message window, which slows the periodic poll in <see cref="Start"/>
    /// to a safety net. The window is subclassed rather than created here so
    /// the service keeps no window of its own; pass <see cref="IntPtr.Zero"/>
    /// to stay poll-driven.
    /// </summary>
    public void AttachToMessageWindow(IntPtr hWnd)
    {
        if (_isDisposed || _isEventDriven || hWnd == IntPtr.Zero)
        {
            return;
        }

        try
        {
            _isMessageSubclassInstalled = Win32Helper.SetWindowSubclass(
                hWnd,
                _messageSubclassProc,
                MessageSubclassId,
                UIntPtr.Zero);
        }
        catch (Exception ex)
        {
            App.Log($"[DisplayAreaWatcher] Message window attach failed: {ex.Message}");
            _isMessageSubclassInstalled = false;
        }

        if (!_isMessageSubclassInstalled)
        {
            App.Log("[DisplayAreaWatcher] Message window unavailable; keeping the fallback poll.");
            return;
        }

        _messageWindow = hWnd;
        _isEventDriven = true;
        _pollTimer.Stop();
        // Pure DPI changes, topology reflows, and RDP sessions do not raise
        // WM_DISPLAYCHANGE, so the event path keeps a slow signature poll as
        // the safety net instead of stopping it entirely.
        _pollTimer.Interval = TimeSpan.FromMilliseconds(EventDrivenPollIntervalMs);
        _pollTimer.Start();
        App.Log(
            $"[DisplayAreaWatcher] Event driven on hwnd=0x{hWnd.ToInt64():X}; " +
            $"slow safety poll every {EventDrivenPollIntervalMs / 1000}s.");
    }

    private IntPtr MessageWindowSubclassProc(
        IntPtr hWnd,
        uint message,
        UIntPtr wParam,
        IntPtr lParam,
        UIntPtr subclassId,
        UIntPtr refData)
    {
        if (message is WmDisplayChange or WmDpiChanged ||
            message == WmSettingChange && wParam.ToUInt64() == SpiSetWorkArea)
        {
            // Cheap Win32 query plus signature compare; the debounce timer
            // already coalesces the burst a topology change produces.
            PollForChanges();
        }
        else if (message == WmNcDestroy)
        {
            Dispose();
        }

        return Win32Helper.DefSubclassProc(hWnd, message, wParam, lParam);
    }

    private void PollTimer_Tick(DispatcherQueueTimer sender, object args)
    {
        PollForChanges();
    }

    /// <summary>
    /// Forces an immediate topology check after a resume, unlock, or shell
    /// restart instead of waiting for the next slow poll tick.
    /// </summary>
    public void RefreshNow()
    {
        if (_isDisposed)
        {
            return;
        }

        PollForChanges();
    }

    private void PollForChanges()
    {
        if (_isDisposed)
        {
            return;
        }

        int newCount = CountDisplays();
        string newSignature = CaptureCurrentSignature();

        if (newCount != _displayCount || !string.Equals(newSignature, _displaySignature, StringComparison.Ordinal))
        {
            bool isCountChange = newCount != _displayCount;
            _displayCount = newCount;
            _displaySignature = newSignature;

            App.Log(
                $"[DisplayAreaWatcher] Display topology changed: " +
                $"count={newCount} countChanged={isCountChange} " +
                $"signature={newSignature}");

            _debounceTimer.Start();
        }
    }

    private void DebounceTimer_Tick(DispatcherQueueTimer sender, object args)
    {
        _debounceTimer.Stop();
        DisplaysChanged?.Invoke();
    }

    /// <summary>
    /// Creates a string signature of the current display topology
    /// (monitor bounds + work areas) to detect any geometry changes.
    /// </summary>
    internal static string CaptureCurrentSignature()
    {
        try
        {
            var areas = Win32Helper.GetMonitorWorkAreaInfos();
            return string.Join("|", areas
                .OrderBy(a => a.DeviceName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(a => a.Monitor.Left)
                .ThenBy(a => a.Monitor.Top)
                .Select(a =>
                $"{a.DeviceName};{a.IsPrimary};{a.DpiScale:F3};" +
                $"{a.Monitor.Left},{a.Monitor.Top},{a.Monitor.Right},{a.Monitor.Bottom};" +
                $"{a.WorkArea.Left},{a.WorkArea.Top},{a.WorkArea.Right},{a.WorkArea.Bottom}"));
        }
        catch
        {
            return string.Empty;
        }
    }

    private static int CountDisplays()
    {
        try
        {
            return Win32Helper.GetMonitorWorkAreas().Count;
        }
        catch
        {
            return 1;
        }
    }

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        _pollTimer.Stop();
        _pollTimer.Tick -= PollTimer_Tick;
        _debounceTimer.Stop();
        _debounceTimer.Tick -= DebounceTimer_Tick;

        if (_isMessageSubclassInstalled)
        {
            Win32Helper.RemoveWindowSubclass(_messageWindow, _messageSubclassProc, MessageSubclassId);
            _isMessageSubclassInstalled = false;
        }
    }
}
