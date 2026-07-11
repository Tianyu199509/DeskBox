using DeskBox.Helpers;
using Microsoft.UI.Dispatching;
using System.Runtime.InteropServices;

namespace DeskBox.Services;

internal sealed class WidgetDisplayChangeWatcher : IDisposable
{
    private const uint WmDisplayChange = 0x007E;
    private const uint WmSettingChange = 0x001A;
    private const uint WmDpiChanged = 0x02E0;
    private const uint WmNcDestroy = 0x0082;
    private const uint SpiSetWorkArea = 0x002F;
    private const int MaxRestoreRetryCount = 8;
    private static readonly uint s_taskbarCreatedMessage = Win32Helper.RegisterWindowMessage("TaskbarCreated");
    private static readonly TimeSpan RestoreDelay = TimeSpan.FromMilliseconds(280);
    private static readonly TimeSpan RestoreRetryDelay = TimeSpan.FromMilliseconds(180);
    private static readonly UIntPtr SubclassId = new(0xDDB2);

    private readonly IntPtr _hWnd;
    private readonly Func<bool> _restoreAction;
    private readonly Win32Helper.SubclassProc _subclassProc;
    private readonly DispatcherQueueTimer _timer;
    private bool _isDisposed;
    private bool _isSubclassInstalled;
    private int _restoreRetryCount;
    private bool _isSuppressed;
    private bool _hasPendingRestore;

    public WidgetDisplayChangeWatcher(IntPtr hWnd, DispatcherQueue dispatcherQueue, Func<bool> restoreAction)
    {
        _hWnd = hWnd;
        _restoreAction = restoreAction;
        _subclassProc = WindowSubclassProc;
        _timer = dispatcherQueue.CreateTimer();
        _timer.Interval = RestoreDelay;
        _timer.IsRepeating = false;
        _timer.Tick += DisplayChangeTimer_Tick;
        _isSubclassInstalled = Win32Helper.SetWindowSubclass(_hWnd, _subclassProc, SubclassId, UIntPtr.Zero);
    }

    /// <summary>
    /// Temporarily suppress restore operations during drag/resize.
    /// Pending restores are deferred until <see cref="ResumeRestore"/> is called.
    /// </summary>
    public void SuppressRestore()
    {
        _isSuppressed = true;
    }

    /// <summary>
    /// Resume restore operations. Any pending restore that was suppressed during
    /// drag/resize is discarded rather than triggered, because the drag/resize end
    /// handler has already updated the config to match the current physical position.
    /// This prevents the window from jumping to an anchor-resolved position that may
    /// differ from the user's intended drop location after a DPI change during drag.
    /// </summary>
    public void ResumeRestore()
    {
        if (!_isSuppressed)
        {
            return;
        }

        _isSuppressed = false;
        _hasPendingRestore = false;
    }

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        _timer.Stop();
        _timer.Tick -= DisplayChangeTimer_Tick;
        RemoveSubclass();
    }

    private IntPtr WindowSubclassProc(
        IntPtr hWnd,
        uint message,
        UIntPtr wParam,
        IntPtr lParam,
        UIntPtr subclassId,
        UIntPtr refData)
    {
        if (message is WmDisplayChange or WmDpiChanged ||
            message == s_taskbarCreatedMessage ||
            message == WmSettingChange && IsRelevantSettingChange(wParam, lParam))
        {
            WidgetLayerService.InvalidateDesktopIconViewCache();
            if (_isSuppressed)
            {
                // Defer the restore until ResumeRestore is called
                _hasPendingRestore = true;
            }
            else
            {
                QueueRestore();
            }
        }
        else if (message == WmNcDestroy)
        {
            Dispose();
        }

        return Win32Helper.DefSubclassProc(hWnd, message, wParam, lParam);
    }

    private static bool IsRelevantSettingChange(UIntPtr wParam, IntPtr lParam)
    {
        if (wParam.ToUInt64() == SpiSetWorkArea)
        {
            return true;
        }

        string? area = lParam == IntPtr.Zero
            ? null
            : Marshal.PtrToStringUni(lParam);
        if (string.IsNullOrWhiteSpace(area))
        {
            return false;
        }

        return area.Contains("Display", StringComparison.OrdinalIgnoreCase) ||
               area.Contains("Monitor", StringComparison.OrdinalIgnoreCase) ||
               area.Contains("WorkArea", StringComparison.OrdinalIgnoreCase) ||
               area.Contains("Taskbar", StringComparison.OrdinalIgnoreCase) ||
               area.Contains("Tray", StringComparison.OrdinalIgnoreCase) ||
               area.Contains("StuckRects", StringComparison.OrdinalIgnoreCase) ||
               area.Contains("ShellState", StringComparison.OrdinalIgnoreCase) ||
               area.Contains("AppBar", StringComparison.OrdinalIgnoreCase);
    }

    private void QueueRestore()
    {
        if (_isDisposed)
        {
            return;
        }

        _restoreRetryCount = 0;
        ScheduleRestore(RestoreDelay);
    }

    private void ScheduleRestore(TimeSpan delay)
    {
        _timer.Stop();
        _timer.Interval = delay;
        _timer.Start();
    }

    private void DisplayChangeTimer_Tick(DispatcherQueueTimer sender, object args)
    {
        _timer.Stop();
        if (_isDisposed)
        {
            return;
        }

        bool completed;
        try
        {
            completed = _restoreAction();
        }
        catch (Exception ex)
        {
            App.Log($"[WidgetDisplayChangeWatcher] Restore failed: {ex}");
            completed = true;
        }

        if (completed)
        {
            return;
        }

        _restoreRetryCount++;
        if (_restoreRetryCount <= MaxRestoreRetryCount)
        {
            ScheduleRestore(RestoreRetryDelay);
        }
    }

    private void RemoveSubclass()
    {
        if (!_isSubclassInstalled)
        {
            return;
        }

        Win32Helper.RemoveWindowSubclass(_hWnd, _subclassProc, SubclassId);
        _isSubclassInstalled = false;
    }
}
