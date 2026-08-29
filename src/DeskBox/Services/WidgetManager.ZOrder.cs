// Copyright (c) DeskBox. All rights reserved.

using DeskBox.Models;
using DeskBox.Helpers;
using DeskBox.Controls.WidgetContents;
using DeskBox.ViewModels;
using DeskBox.Views;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;

namespace DeskBox.Services;

/// <summary>
/// Partial class containing ZOrder logic for WidgetManager.
/// </summary>
public sealed partial class WidgetManager
{

    private DispatcherQueueTimer? _trayLayerRestoreTimer;
    private DispatcherQueueTimer? _trayMouseSamplerTimer;
    private bool _widgetsRaisedFromTray;
    private bool _isTogglingWidgetsDesktopLayer;
    private string _lastWidgetLayerMode;
    private DateTime _lastTrayLayerToggleUtc = DateTime.MinValue;
    private DateTime _suppressTrayLayerRestoreUntilUtc = DateTime.MinValue;
    private bool _hasDeskBoxForegroundSinceRaise;
    private IntPtr _foregroundAtRaiseTime;
    private long _idlePeerOrderGeneration;
    private WidgetTemporaryRaiseLease _temporaryRaiseLease;

    // ── 50ms mouse sampler (方案 B) ──
    // Uses the HIGH bit of GetAsyncKeyState (global physical state) instead of
    // the low bit ("since last query") which is unreliable for cross-process
    // clicks. We poll every 50ms, detect up→down edges, and check the cursor
    // position at the moment of pressing. If the press is outside DeskBox /
    // taskbar, we set _outsideMousePressObserved for the 200ms restore monitor
    // to consume.
    private bool _lastMouseButtonsDown;
    private bool _outsideMousePressObserved;
    private bool _quickRevealDismissQueued;
    private readonly QuickRevealDesktopDismissTracker _quickRevealDesktopDismissTracker =
        QuickRevealDesktopDismissTracker.CreateForCurrentSystem();
    private WidgetExpandedLayerLease _expandedWidgetLayerLease;

    internal long AcquireExpandedWidgetLayer(IntPtr windowHandle, string reason)
    {
        if (windowHandle == IntPtr.Zero || !Win32Helper.IsWindow(windowHandle))
        {
            return 0;
        }

        if (WidgetLayerService.UsesDesktopPinnedMode())
        {
            WidgetLayerService.MoveToDesktopBottom(windowHandle);
            App.LogVerbose(
                $"[ZOrder] Expanded lease skipped fixed-layer reason={reason} " +
                $"owner=0x{windowHandle.ToInt64():X}");
            return 0;
        }

        _expandedWidgetLayerLease = WidgetExpandedLayerLeasePolicy.Acquire(
            _expandedWidgetLayerLease,
            windowHandle);
        _idlePeerOrderGeneration++;

        List<IntPtr> peerOrder = [windowHandle];
        peerOrder.AddRange(
            GetWindowsInIdleHighestFirstOrder(
                    GetLoadedDesktopWindows().Where(window =>
                        window.Visible && window.WindowHandle != windowHandle))
                .Select(window => window.WindowHandle));
        bool applied = WidgetLayerService.EnsurePeerOrderHighestToLowest(peerOrder);
        App.LogVerbose(
            $"[ZOrder] Expanded lease acquired reason={reason} " +
            $"generation={_expandedWidgetLayerLease.Generation} " +
            $"owner=0x{windowHandle.ToInt64():X} peers={peerOrder.Count} applied={applied}");
        return _expandedWidgetLayerLease.Generation;
    }

    internal bool ReleaseExpandedWidgetLayer(
        IntPtr windowHandle,
        long generation,
        string reason)
    {
        if (!WidgetExpandedLayerLeasePolicy.Owns(
            _expandedWidgetLayerLease,
            windowHandle,
            generation))
        {
            App.LogVerbose(
                $"[ZOrder] Expanded lease release ignored reason={reason} " +
                $"generation={generation} owner=0x{windowHandle.ToInt64():X} " +
                $"activeGeneration={_expandedWidgetLayerLease.Generation} " +
                $"activeOwner=0x{_expandedWidgetLayerLease.WindowHandle.ToInt64():X}");
            return false;
        }

        _expandedWidgetLayerLease = WidgetExpandedLayerLeasePolicy.Release(
            _expandedWidgetLayerLease,
            windowHandle,
            generation);
        App.LogVerbose(
            $"[ZOrder] Expanded lease released reason={reason} " +
            $"generation={generation} owner=0x{windowHandle.ToInt64():X}");
        RestoreTemporarilyRaisedWidgetsToDesktopLayer(
            $"{reason}-temporary-raise");
        QueueIdleWidgetZOrderNormalization(reason);
        return true;
    }

    private bool HasActiveExpandedWidgetLayerLease()
    {
        if (!_expandedWidgetLayerLease.IsActive)
        {
            return false;
        }

        bool ownerVisible = GetLoadedDesktopWindows().Any(window =>
            window.Visible &&
            window.WindowHandle == _expandedWidgetLayerLease.WindowHandle);
        if (ownerVisible)
        {
            return true;
        }

        _expandedWidgetLayerLease = new WidgetExpandedLayerLease(
            IntPtr.Zero,
            _expandedWidgetLayerLease.Generation);
        return false;
    }

    public void RestoreRaisedWidgetsToDesktopLayer()
    {
        RestoreRaisedWidgetsToDesktopLayer(force: false);
    }

    public void ForceRestoreRaisedWidgetsToDesktopLayer()
    {
        RestoreRaisedWidgetsToDesktopLayer(force: true);
    }

    internal void QueueIdleWidgetZOrderNormalization(
        string reason,
        TimeSpan? delay = null)
    {
        if (!HasUiThreadAccess())
        {
            App.UiDispatcherQueue.TryEnqueue(
                () => QueueIdleWidgetZOrderNormalization(reason, delay));
            return;
        }

        long generation = ++_idlePeerOrderGeneration;
        TimeSpan effectiveDelay = delay ?? TimeSpan.FromMilliseconds(120);
        App.UiDispatcherQueue.TryEnqueue(async () =>
        {
            await Task.Delay(effectiveDelay);
            if (generation != _idlePeerOrderGeneration)
            {
                return;
            }

            NormalizeIdleWidgetZOrder(reason);
        });
    }

    private long TrackTemporarilyRaisedWidgets(
        IEnumerable<IntPtr> windowHandles,
        string reason)
    {
        _temporaryRaiseLease = WidgetTemporaryRaiseLeasePolicy.Acquire(
            _temporaryRaiseLease,
            windowHandles);
        App.LogVerbose(
            $"[ZOrder] TemporaryRaise acquired reason={reason} " +
            $"generation={_temporaryRaiseLease.Generation} " +
            $"count={_temporaryRaiseLease.ActiveWindowHandles.Count}");
        return _temporaryRaiseLease.Generation;
    }

    private void QueueTemporaryRaisedWidgetRestore(
        string reason,
        long generation,
        TimeSpan delay)
    {
        App.UiDispatcherQueue.TryEnqueue(async () =>
        {
            await Task.Delay(delay);
            RestoreTemporarilyRaisedWidgetsToDesktopLayerCore(
                reason,
                generation,
                retryWhenBusy: true);
        });
    }

    public void RestoreTemporarilyRaisedWidgetsToDesktopLayer(string reason)
    {
        if (!HasUiThreadAccess())
        {
            App.UiDispatcherQueue.TryEnqueue(
                () => RestoreTemporarilyRaisedWidgetsToDesktopLayer(reason));
            return;
        }

        RestoreTemporarilyRaisedWidgetsToDesktopLayerCore(
            reason,
            _temporaryRaiseLease.Generation,
            retryWhenBusy: false);
    }

    private bool RestoreTemporarilyRaisedWidgetsToDesktopLayerCore(
        string reason,
        long generation,
        bool retryWhenBusy)
    {
        if (!WidgetTemporaryRaiseLeasePolicy.OwnsGeneration(
                _temporaryRaiseLease,
                generation))
        {
            return false;
        }

        if (_widgetsRaisedFromTray ||
            _isTogglingWidgetsDesktopLayer ||
            _sessionManager.IsInteractionActive ||
            HasActiveExpandedWidgetLayerLease())
        {
            App.LogVerbose(
                $"[ZOrder] TemporaryRaise restore deferred reason={reason} " +
                $"generation={generation} trayRaised={_widgetsRaisedFromTray} " +
                $"toggling={_isTogglingWidgetsDesktopLayer} " +
                $"interaction={_sessionManager.IsInteractionActive} " +
                $"expanded={_expandedWidgetLayerLease.IsActive}");
            if (retryWhenBusy &&
                !_widgetsRaisedFromTray &&
                !_expandedWidgetLayerLease.IsActive)
            {
                QueueTemporaryRaisedWidgetRestore(
                    reason,
                    generation,
                    TimeSpan.FromMilliseconds(180));
            }

            return false;
        }

        IReadOnlyList<IntPtr> handles =
            _temporaryRaiseLease.ActiveWindowHandles.ToList();
        _temporaryRaiseLease = WidgetTemporaryRaiseLeasePolicy.Release(
            _temporaryRaiseLease,
            generation);

        Dictionary<IntPtr, IDesktopWidgetWindow> windowsByHandle =
            GetLoadedDesktopWindows()
                .Where(window => window.WindowHandle != IntPtr.Zero)
                .GroupBy(window => window.WindowHandle)
                .ToDictionary(group => group.Key, group => group.First());
        int restored = 0;
        foreach (IntPtr handle in handles)
        {
            if (!windowsByHandle.TryGetValue(handle, out IDesktopWidgetWindow? window) ||
                !window.Visible)
            {
                continue;
            }

            try
            {
                window.ForceRestoreDesktopLayerFromManager();
                restored++;
            }
            catch (Exception ex)
            {
                App.Log(
                    $"[WidgetManager] Temporary desktop layer restore failed " +
                    $"reason={reason} {FormatHostWindow(window)}: {ex}");
            }
        }

        App.Log(
            $"[ZOrder] TemporaryRaise restored reason={reason} " +
            $"generation={generation} tracked={handles.Count} restored={restored}");
        QueueIdleWidgetZOrderNormalization(reason);
        return true;
    }

    private void ClearTemporaryRaiseLease(string reason)
    {
        if (!_temporaryRaiseLease.IsActive)
        {
            return;
        }

        int count = _temporaryRaiseLease.ActiveWindowHandles.Count;
        _temporaryRaiseLease = WidgetTemporaryRaiseLeasePolicy.Release(
            _temporaryRaiseLease,
            _temporaryRaiseLease.Generation);
        App.LogVerbose(
            $"[ZOrder] TemporaryRaise cleared reason={reason} count={count}");
    }

    private bool NormalizeIdleWidgetZOrder(string reason)
    {
        if (_widgetsRaisedFromTray ||
            _isTogglingWidgetsDesktopLayer ||
            _sessionManager.IsInteractionActive ||
            _sessionManager.State == WidgetSessionState.Hidden ||
            HasActiveExpandedWidgetLayerLease())
        {
            App.LogVerbose(
                $"[ZOrder] Idle normalize skipped reason={reason} " +
                $"raised={_widgetsRaisedFromTray} toggling={_isTogglingWidgetsDesktopLayer} " +
                $"interaction={_sessionManager.IsInteractionActive} state={_sessionManager.State} " +
                $"expandedOwner=0x{_expandedWidgetLayerLease.WindowHandle.ToInt64():X}");
            return false;
        }

        IReadOnlyList<IDesktopWidgetWindow> ordered =
            GetWindowsInIdleHighestFirstOrder(
                GetLoadedDesktopWindows().Where(window =>
                    window.Visible && !window.IsRaisedAboveDesktopLayer));
        // Normalization is deliberately peer-only. The current highest widget
        // supplies the global boundary, so a group restored directly behind an
        // activated application is not subsequently flattened to HWND_BOTTOM.
        bool applied = WidgetLayerService.ApplyPeerOrderHighestToLowest(
            ordered.Select(window => window.WindowHandle).ToList());
        App.LogVerbose(
            $"[ZOrder] Idle peer normalize reason={reason} count={ordered.Count} " +
            $"applied={applied} order={FormatIdlePeerOrder(ordered)}");
        return applied;
    }

    private static IReadOnlyList<IDesktopWidgetWindow> GetWindowsInIdleHighestFirstOrder(
        IEnumerable<IDesktopWidgetWindow> windows)
    {
        Dictionary<IntPtr, IDesktopWidgetWindow> byHandle = windows
            .Where(window => window.WindowHandle != IntPtr.Zero)
            .GroupBy(window => window.WindowHandle)
            .ToDictionary(group => group.Key, group => group.First());
        IReadOnlyList<IdleWidgetZOrderCandidate> ordered =
            IdleWidgetZOrderPolicy.OrderHighestToLowest(
                byHandle.Values.Select(window =>
                {
                    Windows.Foundation.Rect bounds = window.RestingAnimationBounds;
                    return new IdleWidgetZOrderCandidate(
                        window.WindowHandle,
                        GetAnimationWorkAreaKey(window),
                        bounds.Top,
                        bounds.Left,
                        window.Identity.SurfaceId);
                }));
        return ordered
            .Select(candidate => byHandle[candidate.WindowHandle])
            .ToList();
    }

    private static string FormatIdlePeerOrder(
        IReadOnlyList<IDesktopWidgetWindow> windows)
    {
        return string.Join(
            ',',
            windows.Select(window =>
                $"{window.Identity.ShortSurfaceId}@{window.RestingAnimationBounds.Top:F0}"));
    }

    public void BringAllVisibleWidgetsToFront(IntPtr exceptHwnd = default)
    {
        foreach (var window in GetLoadedDesktopWindows())
        {
            if (window.Visible && window.WindowHandle != exceptHwnd)
            {
                WidgetLayerService.BringToFront(window.WindowHandle);
            }
        }
    }

    private void RaiseVisibleWidgetsTemporarily(string reason)
    {
        if (WidgetLayerService.UsesDesktopPinnedMode())
        {
            App.LogVerbose($"[ZOrder] BatchTemporaryRaise skipped pinned reason={reason}");
            return;
        }

        IReadOnlyList<IDesktopWidgetWindow> windows =
            GetWindowsInIdleHighestFirstOrder(
                GetLoadedDesktopWindows().Where(window => window.Visible));
        foreach (IDesktopWidgetWindow window in windows)
        {
            window.RaiseTemporarilyFromManager();
        }

        long generation = TrackTemporarilyRaisedWidgets(
            windows.Select(window => window.WindowHandle),
            reason);
        bool applied = WidgetLayerService.ApplyPeerOrderHighestToLowest(
            windows.Select(window => window.WindowHandle).ToList());
        QueueTemporaryRaisedWidgetRestore(
            $"{reason}-settled",
            generation,
            TimeSpan.FromMilliseconds(2300));

        App.Log(
            $"[ZOrder] BatchTemporaryRaise reason={reason} count={windows.Count} " +
            $"applied={applied} " +
            $"handles={string.Join(',', windows.Select(window => $"0x{window.WindowHandle.ToInt64():X}"))}");
    }

    public void ActivateAllVisibleWidgetsFromTitle(IntPtr activeHwnd)
    {
        if (WidgetLayerService.UsesDesktopPinnedMode())
        {
            return;
        }

        var handles = GetLoadedDesktopWindows()
            .Where(window => window.Visible)
            .Select(window => window.WindowHandle)
            .ToList();
        long generation = TrackTemporarilyRaisedWidgets(
            handles,
            "title-activated-all");
        WidgetLayerService.BringGroupTemporarilyToFront(handles, activeHwnd);
        QueueTemporaryRaisedWidgetRestore(
            "title-activated-all-fallback",
            generation,
            TimeSpan.FromMilliseconds(2300));
        App.LogVerbose($"[ZOrder] TitleActivatedAll active=0x{activeHwnd.ToInt64():X}");
    }

    public bool RequestRestoreRaisedWidgetsToDesktopLayer(string reason = "interaction-ended")
    {
        if (!_widgetsRaisedFromTray)
        {
            App.LogVerbose($"[TrayBatch] RestoreRequest ignored reason={reason} state=not-raised");
            return false;
        }

        if (App.UiDispatcherQueue is { } dispatcherQueue && !dispatcherQueue.HasThreadAccess)
        {
            dispatcherQueue.TryEnqueue(() => RequestRestoreRaisedWidgetsToDesktopLayer(reason));
            return true;
        }

        App.LogVerbose($"[TrayBatch] RestoreRequest held reason={reason} until=next-toggle");
        return true;
    }

    private void QueueRequestedLayerRestoreCheck(string reason, TimeSpan delay)
    {
        long generation = _trayRaiseBatchGeneration;
        App.UiDispatcherQueue.TryEnqueue(async () =>
        {
            await Task.Delay(delay);
            TryRestoreRaisedWidgetsAfterInteraction(reason, generation);
        });
    }

    private void TryRestoreRaisedWidgetsAfterInteraction(string reason, long generation)
    {
        if (!_widgetsRaisedFromTray ||
            generation != _trayRaiseBatchGeneration ||
            _isTogglingWidgetsDesktopLayer ||
            IsWidgetInteractionActive ||
            DateTime.UtcNow < _suppressTrayLayerRestoreUntilUtc)
        {
            return;
        }

        IntPtr foreground = Win32Helper.GetForegroundWindow();
        if (IsDeskBoxForegroundWindow(foreground))
        {
            _hasDeskBoxForegroundSinceRaise = true;
            App.LogVerbose($"[TrayBatch] RaisedState kept reason={reason} foreground=0x{foreground.ToInt64():X}");
            return;
        }

        if (IsTaskbarWindow(foreground))
        {
            if (WidgetLayerService.UsesQuickRevealMode())
            {
                QueueQuickRevealDismiss($"{reason}-taskbar", outsideInteraction: true);
                return;
            }

            App.LogVerbose($"[TrayBatch] RaisedState kept reason={reason} foreground=taskbar");
            return;
        }

        if (!_hasDeskBoxForegroundSinceRaise)
        {
            // The raise happened while a foreign window owned the foreground (e.g.
            // an elevated app rejects our activation). Releasing the raised state
            // just because the foreground is not ours would undo the raise within
            // one tick, so we need evidence that the user actively switched away.
            //
            // Primary signal: the foreground window CHANGED to a different non-DeskBox
            // window since the raise. This is reliable because it does not depend on
            // GetAsyncKeyState (which only sees presses posted to our own thread).
            if (foreground != _foregroundAtRaiseTime && foreground != IntPtr.Zero)
            {
                App.Log($"[TrayBatch] RaisedState released reason={reason}-foreground-changed from=0x{_foregroundAtRaiseTime.ToInt64():X} to=0x{foreground.ToInt64():X}");
                CompleteRaisedSessionAfterExternalInteraction(
                    $"{reason}-foreground-changed",
                    outsideInteraction: true);
                return;
            }

            // Fallback: the foreground is still the same window as at raise time.
            // Use the 50ms mouse sampler's edge-detected _outsideMousePressObserved
            // flag (high-bit GetAsyncKeyState, reliable across processes) instead
            // of the legacy low-bit HasMouseButtonPressSinceLastQuery which fails
            // for clicks delivered to foreign windows.
            if (_outsideMousePressObserved)
            {
                _outsideMousePressObserved = false;
                App.Log($"[TrayBatch] RaisedState released reason={reason}-outside-click foreground=0x{foreground.ToInt64():X}");
                CompleteRaisedSessionAfterExternalInteraction(
                    $"{reason}-outside-click",
                    outsideInteraction: true);
            }

            return;
        }

        App.Log($"[TrayBatch] RaisedState released reason={reason}-deskbox-leave foreground=0x{foreground.ToInt64():X}");
        CompleteRaisedSessionAfterExternalInteraction(
            $"{reason}-deskbox-leave",
            outsideInteraction: true);
    }

    private void CompleteRaisedSessionAfterExternalInteraction(
        string reason,
        bool outsideInteraction)
    {
        if (WidgetLayerService.UsesQuickRevealMode())
        {
            QueueQuickRevealDismiss(reason, outsideInteraction);
            return;
        }

        RestoreRaisedWidgetsToDesktopLayer();
    }

    private void QueueQuickRevealDismiss(string reason, bool outsideInteraction)
    {
        if (_quickRevealDismissQueued || !HasVisibleWidgets)
        {
            return;
        }

        _quickRevealDismissQueued = true;
        if (outsideInteraction)
        {
            Win32Helper.POINT? cursor = TryGetCursorPosition();
            if (cursor.HasValue)
            {
                _quickRevealDesktopDismissTracker.Record(
                    cursor.Value.X,
                    cursor.Value.Y,
                    unchecked((uint)Environment.TickCount));
            }
            else
            {
                _quickRevealDesktopDismissTracker.Clear();
            }
        }

        StopTrayLayerRestoreMonitor();
        App.LogVerbose($"[QuickReveal] Dismiss queued reason={reason}");
        if (!App.UiDispatcherQueue.TryEnqueue(async () =>
        {
            try
            {
                await SetAllWidgetsVisibleAsync(false);
            }
            catch (Exception ex)
            {
                App.Log($"[QuickReveal] Dismiss failed reason={reason}: {ex}");
            }
            finally
            {
                _quickRevealDismissQueued = false;
            }
        }))
        {
            _quickRevealDismissQueued = false;
            App.Log($"[QuickReveal] Dismiss dispatch rejected reason={reason}");
        }
    }

    internal bool ConsumeQuickRevealDesktopDoubleClickDismiss(
        DesktopDoubleClickSequence sequence)
    {
        if (!WidgetLayerService.UsesQuickRevealMode())
        {
            _quickRevealDesktopDismissTracker.Clear();
            return false;
        }

        return _quickRevealDesktopDismissTracker.ConsumeIfSameSequence(sequence);
    }

    private void StartTrayLayerRestoreMonitor(
        bool hasRaisedWidgets,
        bool preserveSourceForeground = false)
    {
        if (!hasRaisedWidgets)
        {
            App.LogVerbose("[TrayBatch] RestoreMonitor not-started reason=no-raised-windows");
            StopTrayLayerRestoreMonitor();
            return;
        }

        _hasDeskBoxForegroundSinceRaise =
            !preserveSourceForeground && IsForegroundDeskBoxWindow();
        _trayLayerRestoreTimer ??= App.UiDispatcherQueue.CreateTimer();
        _trayLayerRestoreTimer.Stop();
        int restoreIntervalMs = WidgetLayerService.UsesQuickRevealMode() ? 50 : 200;
        _trayLayerRestoreTimer.Interval = TimeSpan.FromMilliseconds(restoreIntervalMs);
        _trayLayerRestoreTimer.Tick -= TrayLayerRestoreTimer_Tick;
        _trayLayerRestoreTimer.Tick += TrayLayerRestoreTimer_Tick;
        _trayLayerRestoreTimer.Start();
        App.LogVerbose(
            $"[TrayBatch] RaisedStateMonitor started intervalMs={restoreIntervalMs}");

        StartTrayMouseSampler();
    }

    private void StopTrayLayerRestoreMonitor()
    {
        if (_trayLayerRestoreTimer is not null)
        {
            _trayLayerRestoreTimer.Stop();
            _trayLayerRestoreTimer.Tick -= TrayLayerRestoreTimer_Tick;
            App.LogVerbose("[TrayBatch] RaisedStateMonitor stopped");
        }

        StopTrayMouseSampler();
    }

    // ── 50ms mouse sampler (方案 B) ──────────────────────────

    private void StartTrayMouseSampler()
    {
        // Pre-charge current button state so the press that triggered F7
        // (via keyboard hook) isn't mistaken for a new outside click.
        _lastMouseButtonsDown = Win32Helper.IsAnyMouseButtonDown();
        _outsideMousePressObserved = false;

        _trayMouseSamplerTimer ??= App.UiDispatcherQueue.CreateTimer();
        _trayMouseSamplerTimer.Stop();
        _trayMouseSamplerTimer.Interval = TimeSpan.FromMilliseconds(50);
        _trayMouseSamplerTimer.Tick -= TrayMouseSamplerTimer_Tick;
        _trayMouseSamplerTimer.Tick += TrayMouseSamplerTimer_Tick;
        _trayMouseSamplerTimer.Start();
        App.LogVerbose("[TrayBatch] MouseSampler started intervalMs=50");
    }

    private void StopTrayMouseSampler()
    {
        if (_trayMouseSamplerTimer is null)
        {
            return;
        }

        _trayMouseSamplerTimer.Stop();
        _trayMouseSamplerTimer.Tick -= TrayMouseSamplerTimer_Tick;
        App.LogVerbose("[TrayBatch] MouseSampler stopped");
    }

    private void TrayMouseSamplerTimer_Tick(DispatcherQueueTimer sender, object args)
    {
        if (!_widgetsRaisedFromTray)
        {
            StopTrayMouseSampler();
            return;
        }

        bool isDown = Win32Helper.IsAnyMouseButtonDown();

        // Detect up→down edge (new press).
        if (isDown && !_lastMouseButtonsDown)
        {
            // Check cursor position at the moment of pressing.
            Win32Helper.POINT? cursor = TryGetCursorPosition();
            if (!IsPointerOverDeskBoxWindow(cursor) &&
                (!IsPointerOverTaskbar(cursor) || WidgetLayerService.UsesQuickRevealMode()))
            {
                if (WidgetLayerService.UsesQuickRevealMode())
                {
                    _lastMouseButtonsDown = isDown;
                    QueueQuickRevealDismiss(
                        "mouse-sampler-outside-click",
                        outsideInteraction: true);
                    return;
                }

                _outsideMousePressObserved = true;
                App.LogVerbose($"[TrayBatch] MouseSampler detected outside-press at={FormatPoint(cursor)}");
            }
        }

        _lastMouseButtonsDown = isDown;
    }

    private void TrayLayerRestoreTimer_Tick(DispatcherQueueTimer sender, object args)
    {
        if (!_widgetsRaisedFromTray)
        {
            StopTrayLayerRestoreMonitor();
            return;
        }

        if (_isTogglingWidgetsDesktopLayer ||
            DateTime.UtcNow < _suppressTrayLayerRestoreUntilUtc)
        {
            return;
        }

        TryRestoreRaisedWidgetsAfterInteraction(
            "restore-monitor",
            _trayRaiseBatchGeneration);
    }

    private static bool IsPointerOverDeskBoxWindow(Win32Helper.POINT? cursor)
    {
        if (!cursor.HasValue)
        {
            return false;
        }

        IntPtr pointerWindow = Win32Helper.WindowFromPoint(cursor.Value);
        return App.Current.IsDeskBoxWindow(pointerWindow);
    }

    private static bool IsForegroundDeskBoxWindow()
    {
        IntPtr foregroundWindow = Win32Helper.GetForegroundWindow();
        return IsDeskBoxForegroundWindow(foregroundWindow);
    }

    private static bool IsDeskBoxForegroundWindow(IntPtr foregroundWindow)
    {
        return foregroundWindow != IntPtr.Zero &&
               App.Current.IsDeskBoxWindow(foregroundWindow);
    }

    private static bool IsPointerOverTaskbar(Win32Helper.POINT? cursor)
    {
        if (!cursor.HasValue)
        {
            return false;
        }

        IntPtr pointerWindow = Win32Helper.WindowFromPoint(cursor.Value);
        if (pointerWindow == IntPtr.Zero)
        {
            return false;
        }

        IntPtr currentWindow = pointerWindow;
        while (currentWindow != IntPtr.Zero)
        {
            if (IsTaskbarWindow(currentWindow))
            {
                return true;
            }

            currentWindow = Win32Helper.GetParent(currentWindow);
        }

        IntPtr rootWindow = Win32Helper.GetAncestor(pointerWindow, Win32Helper.GA_ROOT);
        return IsTaskbarWindow(rootWindow);
    }

    private static bool IsTaskbarWindow(IntPtr hWnd)
    {
        return WindowOrAncestorHasClass(
            hWnd,
            value => string.Equals(value, "Shell_TrayWnd", StringComparison.Ordinal) ||
                     string.Equals(value, "Shell_SecondaryTrayWnd", StringComparison.Ordinal) ||
                     string.Equals(value, "NotifyIconOverflowWindow", StringComparison.Ordinal));
    }

    private static bool IsDesktopShellWindow(IntPtr hWnd)
    {
        return WindowOrAncestorHasClass(
            hWnd,
            value => string.Equals(value, "Progman", StringComparison.Ordinal) ||
                     string.Equals(value, "WorkerW", StringComparison.Ordinal));
    }

    private static bool WindowOrAncestorHasClass(IntPtr hWnd, Func<string, bool> predicate)
    {
        if (hWnd == IntPtr.Zero)
        {
            return false;
        }

        IntPtr currentWindow = hWnd;
        while (currentWindow != IntPtr.Zero)
        {
            if (WindowHasClass(currentWindow, predicate))
            {
                return true;
            }

            currentWindow = Win32Helper.GetParent(currentWindow);
        }

        IntPtr rootWindow = Win32Helper.GetAncestor(hWnd, Win32Helper.GA_ROOT);
        return WindowHasClass(rootWindow, predicate);
    }

    private static bool WindowHasClass(IntPtr hWnd, Func<string, bool> predicate)
    {
        if (hWnd == IntPtr.Zero)
        {
            return false;
        }

        var className = new System.Text.StringBuilder(256);
        int length = Win32Helper.GetClassName(hWnd, className, className.Capacity);
        return length > 0 && predicate(className.ToString());
    }

    private static Win32Helper.POINT? TryGetCursorPosition()
    {
        return Win32Helper.GetCursorPos(out var cursor) ? cursor : null;
    }

    private void SetWidgetsRaisedFromTray(bool raised)
    {
        if (_widgetsRaisedFromTray == raised)
        {
            if (raised)
            {
                _sessionManager.MarkRaisedSession("raised-state-kept");
            }

            return;
        }

        App.LogVerbose($"[TrayBatch] RaisedState changed { _widgetsRaisedFromTray } -> {raised}");
        _widgetsRaisedFromTray = raised;
        if (raised)
        {
            _sessionManager.MarkRaisedSession("tray-raised");
        }
        else if (HasVisibleWidgets)
        {
            _sessionManager.MarkDesktopResting("tray-restored");
        }
        else
        {
            _sessionManager.MarkHidden("tray-hidden");
        }

        TrayLayerStateChanged?.Invoke(raised);
    }

    private static string FormatWidgetList(IReadOnlyList<WidgetConfig> widgets)
    {
        return widgets.Count == 0
            ? "[]"
            : "[" + string.Join(", ", widgets.Select(FormatWidget)) + "]";
    }

    private static string FormatWidget(WidgetConfig widget)
    {
        return $"{widget.Name}#{ShortId(widget.Id)} kind={widget.WidgetKind} visible={widget.IsVisible} disabled={widget.IsDisabled}";
    }

    private static string FormatHostWindow(IDesktopWidgetWindow window)
    {
        var identity = window.Identity;
        return $"{identity.LogDisplayName} kind={identity.WidgetKind} hwnd=0x{identity.WindowHandle.ToInt64():X}";
    }

    private static string ShortId(string id)
    {
        return string.IsNullOrWhiteSpace(id)
            ? "none"
            : id.Length <= 8 ? id : id[..8];
    }

    private static string FormatPoint(Win32Helper.POINT? point)
    {
        return point.HasValue ? $"{point.Value.X},{point.Value.Y}" : "unknown";
    }

}
