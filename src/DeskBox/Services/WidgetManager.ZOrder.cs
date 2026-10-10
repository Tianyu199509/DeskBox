// Copyright (c) DeskBox. All rights reserved.

using DeskBox.Models;
using DeskBox.Helpers;
using DeskBox.Controls.WidgetContents;
using DeskBox.Platform;
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
    private bool _lastRaiseOriginatedFromTrayIcon;
    private DateTime _lastQuickRevealDismissUtc = DateTime.MinValue;
    private bool _lastQuickRevealDismissTaskbarOrigin;
    private long _idlePeerOrderGeneration;
    private int _idleNormalizeAnimationDefers;
    private WidgetTemporaryRaiseLease _temporaryRaiseLease;

    private const int MaxIdleNormalizeAnimationDefers = 15;

    // ── Desktop-pinned global bedding self-heal (289/447/468) ──────────
    // Idle peer normalization is deliberately peer-only, so once the whole
    // pinned group leaks above application windows (Explorer lifting the
    // DefView owner band while hosting a launch, a fullscreen exit, a DPI
    // change), no daily path ever pulls it back. The self-heal is verified,
    // conservative, and re-beds the group through the existing
    // RestoreGroupPreservingForeground primitive — never a blind
    // SetWindowPos. The "already bedded" guard inside the re-bed makes it a
    // no-op transaction set when nothing is wrong.
    private DispatcherQueueTimer? _desktopPinnedBeddingWatchdog;
    private bool _desktopPinnedBeddingWatchdogArmed;
    private BeddingLeakConfirmationPolicy.LeakTracking _beddingLeakTracking =
        BeddingLeakConfirmationPolicy.LeakTracking.Empty;
    private static readonly TimeSpan DesktopPinnedBeddingWatchdogInterval =
        TimeSpan.FromSeconds(5);
    private const int RequiredBeddingLeakConfirmations = 2;

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

    // ── Quick-reveal raised-band guests ─────────────────────
    // A quick-reveal raised session holds the widget group WS_EX_TOPMOST
    // (system-flyout semantics). DeskBox-owned interactive surfaces (search
    // popup, settings, desktop organization) join that band above the widgets
    // while the session lives, then return to normal Z-order rules.
    // Registration is the authority for whether a window was made topmost
    // here; releases are generation-guarded against fast dismiss-then-reraise
    // sequences.

    private sealed record RaisedBandGuestRecord(string Reason, long SessionGeneration);

    private readonly Dictionary<IntPtr, RaisedBandGuestRecord> _raisedBandGuests = new();

    /// <summary>
    /// App-level provider of candidate auxiliary window handles (search popup,
    /// settings, desktop organization). Consulted when a quick-reveal raise
    /// completes so surfaces that were already open before the raise are
    /// promoted above the widget group too.
    /// </summary>
    internal Func<IReadOnlyList<IntPtr>>? AuxiliaryWindowProvider;

    /// <summary>
    /// Single choke point for bringing a DeskBox-owned auxiliary window to the
    /// front. During a quick-reveal raised session the window joins the
    /// topmost band above the widget group and is registered for release;
    /// otherwise it takes the ordinary TOPMOST→NOTOPMOST pulse.
    /// </summary>
    public void BringAuxiliaryWindowToFront(IntPtr windowHandle, string reason)
    {
        if (!HasUiThreadAccess())
        {
            App.UiDispatcherQueue.TryEnqueue(
                () => BringAuxiliaryWindowToFront(windowHandle, reason));
            return;
        }

        if (windowHandle == IntPtr.Zero || !Win32Helper.IsWindow(windowHandle))
        {
            return;
        }

        if (RaisedBandGuestPolicy.ShouldHoldGuest(
                WidgetLayerService.UsesQuickRevealMode(),
                _widgetsRaisedFromTray))
        {
            HoldRaisedBandGuest(windowHandle, reason);
            return;
        }

        Win32Helper.BringWindowTemporarilyToFront(windowHandle);
    }

    /// <summary>
    /// Releases one guest when its own window hides or closes. No-op for
    /// windows that were never held, so stray calls cannot reorder unrelated
    /// windows.
    /// </summary>
    public void ReleaseRaisedBandGuest(IntPtr windowHandle, string reason)
    {
        if (!HasUiThreadAccess())
        {
            App.UiDispatcherQueue.TryEnqueue(
                () => ReleaseRaisedBandGuest(windowHandle, reason));
            return;
        }

        if (!_raisedBandGuests.Remove(windowHandle, out RaisedBandGuestRecord? record))
        {
            return;
        }

        ReleaseRaisedBandGuestCore(windowHandle, record, reason);
    }

    private void HoldRaisedBandGuest(IntPtr windowHandle, string reason)
    {
        WidgetLayerService.HoldWindowAboveRaisedWidgets(windowHandle);
        _raisedBandGuests[windowHandle] =
            new RaisedBandGuestRecord(reason, _trayRaiseBatchGeneration);
        App.LogVerbose(
            $"[RaisedBand] Guest held reason={reason} hwnd=0x{windowHandle.ToInt64():X} " +
            $"generation={_trayRaiseBatchGeneration} guests={_raisedBandGuests.Count}");
    }

    /// <summary>
    /// Promotes already-open auxiliary windows above a freshly raised widget
    /// group (the reverse open-then-raise order). Hidden or destroyed handles
    /// are skipped: their own hide paths have already released them.
    /// </summary>
    private void HoldVisibleAuxiliaryWindowsAboveRaisedWidgets()
    {
        if (AuxiliaryWindowProvider is not { } provider)
        {
            return;
        }

        IReadOnlyList<IntPtr> handles;
        try
        {
            handles = provider() ?? Array.Empty<IntPtr>();
        }
        catch (Exception ex)
        {
            App.Log($"[RaisedBand] Auxiliary window provider failed: {ex.Message}");
            return;
        }

        foreach (IntPtr handle in handles)
        {
            if (handle == IntPtr.Zero ||
                !Win32Helper.IsWindow(handle) ||
                !Win32Helper.IsWindowVisible(handle))
            {
                continue;
            }

            HoldRaisedBandGuest(handle, "raise-promoted");
        }
    }

    private void ReleaseRaisedBandGuestCore(
        IntPtr windowHandle,
        RaisedBandGuestRecord record,
        string reason)
    {
        if (!Win32Helper.IsWindow(windowHandle))
        {
            App.LogVerbose(
                $"[RaisedBand] Guest release skipped (destroyed) reason={reason} " +
                $"hwnd=0x{windowHandle.ToInt64():X}");
            return;
        }

        IntPtr foregroundRoot = WidgetLayerService.GetForegroundRoot(
            Win32Helper.GetForegroundWindow());
        bool foreignForeground =
            foregroundRoot != IntPtr.Zero &&
            Win32Helper.IsWindow(foregroundRoot) &&
            foregroundRoot != windowHandle &&
            !App.Current.IsDeskBoxWindow(foregroundRoot);
        RaisedBandReleasePlacement placement =
            RaisedBandGuestPolicy.ResolveReleasePlacement(foreignForeground);

        WidgetLayerService.ReleaseRaisedBandWindow(
            windowHandle,
            placement,
            foreignForeground ? foregroundRoot : IntPtr.Zero);
        App.LogVerbose(
            $"[RaisedBand] Guest released reason={reason} " +
            $"heldReason={record.Reason} placement={placement} " +
            $"hwnd=0x{windowHandle.ToInt64():X} guests={_raisedBandGuests.Count}");
    }

    /// <summary>
    /// Ends band membership for every guest of the dismissed session. Runs
    /// after the hide animations settle so a guest does not sink below widgets
    /// that are still fading out, and is generation-guarded so a fast
    /// dismiss-then-reraise cannot demote guests that now belong to the new
    /// session.
    /// </summary>
    private void SweepRaisedBandGuests(string reason, long endedSessionGeneration)
    {
        if (_raisedBandGuests.Count == 0)
        {
            return;
        }

        List<IntPtr> staleHandles = _raisedBandGuests
            .Where(pair => RaisedBandGuestPolicy.ShouldSweepGuest(
                pair.Value.SessionGeneration,
                endedSessionGeneration))
            .Select(pair => pair.Key)
            .ToList();
        int released = 0;
        foreach (IntPtr handle in staleHandles)
        {
            if (_raisedBandGuests.Remove(handle, out RaisedBandGuestRecord? record))
            {
                ReleaseRaisedBandGuestCore(handle, record, reason);
                released++;
            }
        }

        App.LogVerbose(
            $"[RaisedBand] Sweep reason={reason} " +
            $"endedGeneration={endedSessionGeneration} " +
            $"released={released} remaining={_raisedBandGuests.Count}");
    }

    internal long AcquireExpandedWidgetLayer(IntPtr windowHandle, string reason)
    {
        if (windowHandle == IntPtr.Zero || !Win32Helper.IsWindow(windowHandle))
        {
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
        // Cohesion exit: this queueing supersedes the one inside the restore
        // above (later generation wins), and unlike idle normalization it may
        // not be dropped by the shadow policy or abandoned after animation
        // defers — the lease just ended with the expanded window on top of the
        // peer order, and compact arrangements read that residue as capsules
        // occluding each other (#302).
        QueueIdleWidgetZOrderNormalization(reason, cohesionExit: true);
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
        TimeSpan? delay = null,
        bool cohesionExit = false)
    {
        if (!HasUiThreadAccess())
        {
            App.UiDispatcherQueue.TryEnqueue(
                () => QueueIdleWidgetZOrderNormalization(reason, delay, cohesionExit));
            return;
        }

        // Any normalization traffic implies live widgets; piggyback the
        // one-time watchdog arming here so pinned sessions are covered even
        // before the first file-open dispatch. Runs before the shadow gate:
        // a shadows-off system skips idle reorders but still needs the
        // bedding watchdog.
        EnsureDesktopPinnedBeddingWatchdogArmed();

        // Idle normalization is shadow-protection work only; queueing it while
        // Windows drop shadows are off can only produce needless reorders.
        // A cohesion-exit request is different: an expanded-layer lease (for
        // example a capsule collapsing back into a compact arrangement) has
        // just left the peer order with the expanded window on top, and
        // leaving that residue behind shows as capsules occluding each other
        // (#302). Correctness outranks the shadow policy there.
        if (!cohesionExit &&
            Win32Helper.TryGetWindowDropShadowEnabled(out bool shadowEnabledAtQueue) &&
            !IdleWidgetZOrderPolicy.ShouldNormalizeIdlePeerOrder(shadowEnabledAtQueue))
        {
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

            NormalizeIdleWidgetZOrder(reason, cohesionExit);
        });
    }

    // ── Desktop-pinned global bedding self-heal (289/447/468) ──────────

    /// <summary>
    /// Low-frequency watchdog. It only observes: a repair requires the same
    /// leak pattern (a visible foreign application window below the pinned
    /// group) across consecutive ticks, so transient activation states and
    /// deliberate fullscreen/topmost arrangements never trigger it.
    /// </summary>
    private void EnsureDesktopPinnedBeddingWatchdogArmed()
    {
        if (_desktopPinnedBeddingWatchdogArmed)
        {
            return;
        }

        _desktopPinnedBeddingWatchdog ??= App.UiDispatcherQueue.CreateTimer();
        _desktopPinnedBeddingWatchdog.Interval = DesktopPinnedBeddingWatchdogInterval;
        _desktopPinnedBeddingWatchdog.Tick -= DesktopPinnedBeddingWatchdog_Tick;
        _desktopPinnedBeddingWatchdog.Tick += DesktopPinnedBeddingWatchdog_Tick;
        _desktopPinnedBeddingWatchdog.Start();
        _desktopPinnedBeddingWatchdogArmed = true;
        App.LogVerbose(
            "[ZOrder] DesktopPinned bedding watchdog armed " +
            $"intervalMs={(int)DesktopPinnedBeddingWatchdogInterval.TotalMilliseconds}");
    }

    private void DesktopPinnedBeddingWatchdog_Tick(
        DispatcherQueueTimer sender,
        object args)
    {
        if (App.IsShuttingDown)
        {
            return;
        }

        if (!WidgetLayerService.UsesDesktopPinnedMode() || !HasVisibleWidgets)
        {
            ResetBeddingLeakTracking();
            return;
        }

        VerifyDesktopPinnedBedding("watchdog", requireConfirmation: true);
    }

    /// <summary>
    /// Teardown counterpart of <see cref="EnsureDesktopPinnedBeddingWatchdogArmed"/>:
    /// without this, a tick that lands while CloseAll is already closing windows
    /// would re-bed and re-attach icon layers mid-shutdown.
    /// </summary>
    private void StopDesktopPinnedBeddingWatchdog()
    {
        if (_desktopPinnedBeddingWatchdog is { } timer)
        {
            timer.Stop();
            timer.Tick -= DesktopPinnedBeddingWatchdog_Tick;
        }

        _desktopPinnedBeddingWatchdogArmed = false;
    }

    /// <summary>
    /// Event-driven recheck for a known leak window: after DeskBox dispatches
    /// a file open (the Explorer-hosted launch lifts the DefView owner band,
    /// #447/#468) or after a display-topology restore. Known leak points act
    /// on the first verified observation instead of waiting for the
    /// watchdog's consecutive confirmations, but the leak predicate is still
    /// required — nothing moves when the group is already bedded.
    /// </summary>
    internal void QueueDesktopPinnedBeddingRecheck(string reason, TimeSpan delay)
    {
        if (!HasUiThreadAccess())
        {
            UiDispatch.RunOrDefer(() => QueueDesktopPinnedBeddingRecheck(reason, delay));
            return;
        }

        EnsureDesktopPinnedBeddingWatchdogArmed();
        // RunOrDefer instead of a bare dispatcher enqueue: the bare call
        // would NullReference before dispatcher-init.
        UiDispatch.RunOrDefer(async () =>
        {
            await Task.Delay(delay);
            VerifyDesktopPinnedBedding(reason, requireConfirmation: false);
        });
    }

    private void VerifyDesktopPinnedBedding(string reason, bool requireConfirmation)
    {
        if (!WidgetLayerService.UsesDesktopPinnedMode() ||
            _widgetsRaisedFromTray ||
            _isTogglingWidgetsDesktopLayer ||
            _sessionManager.IsInteractionActive ||
            HasActiveExpandedWidgetLayerLease())
        {
            ResetBeddingLeakTracking();
            return;
        }

        IntPtr witness = IntPtr.Zero;
        foreach (IDesktopWidgetWindow window in GetLoadedDesktopWindows())
        {
            if (!window.Visible || window.WindowHandle == IntPtr.Zero)
            {
                continue;
            }

            witness = WidgetLayerService.FindForeignWindowBelow(window.WindowHandle);
            if (witness != IntPtr.Zero)
            {
                break;
            }
        }

        if (witness == IntPtr.Zero)
        {
            ResetBeddingLeakTracking();
            return;
        }

        if (requireConfirmation)
        {
            BeddingLeakConfirmationPolicy.LeakTracking previous = _beddingLeakTracking;
            (_beddingLeakTracking, bool confirmed) = BeddingLeakConfirmationPolicy.Observe(
                previous,
                witness,
                RequiredBeddingLeakConfirmations);
            if (!confirmed)
            {
                App.LogVerbose(
                    $"[ZOrder] DesktopPinned bedding leak observed (unconfirmed) " +
                    $"reason={reason} witness=0x{witness.ToInt64():X} " +
                    $"confirmations={_beddingLeakTracking.Confirmations}");
                return;
            }
        }

        RebedDesktopPinnedWidgetGroup(reason, witness);
    }

    private void RebedDesktopPinnedWidgetGroup(string reason, IntPtr witness)
    {
        ResetBeddingLeakTracking();

        List<IntPtr> handles = GetLoadedDesktopWindows()
            .Where(window => window.Visible &&
                window.WindowHandle != IntPtr.Zero &&
                Win32Helper.IsWindow(window.WindowHandle))
            .Select(window => window.WindowHandle)
            .ToList();
        if (handles.Count == 0)
        {
            return;
        }

        // Pinned mode's resting state is the desktop band bottom; the group
        // restore re-attaches every window and re-applies the peer order in
        // one pass. Windows that are already bedded short-circuit inside the
        // attach path (step "already bedded" guard), so the repair only costs
        // transactions for genuinely displaced windows.
        bool applied = WidgetLayerService.RestoreGroupPreservingForeground(
            handles,
            reason);
        App.Log(
            $"[ZOrder] DesktopPinned group re-bedded reason={reason} " +
            $"witness=0x{witness.ToInt64():X} count={handles.Count} applied={applied}");
    }

    private void ResetBeddingLeakTracking()
    {
        _beddingLeakTracking = BeddingLeakConfirmationPolicy.LeakTracking.Empty;
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

    // A stuck interaction flag would otherwise re-enqueue the deferred
    // restore forever — every 180 ms of dispatcher work plus a lease that
    // never settles. Bounded retries give up and leave the lease held, so a
    // later explicit restore still resolves the elevation.
    private const int MaxTemporaryRaiseRestoreAttempts = 25;

    private void QueueTemporaryRaisedWidgetRestore(
        string reason,
        long generation,
        TimeSpan delay,
        int attempt = 0)
    {
        App.UiDispatcherQueue.TryEnqueue(async () =>
        {
            await Task.Delay(delay);
            RestoreTemporarilyRaisedWidgetsToDesktopLayerCore(
                reason,
                generation,
                retryWhenBusy: true,
                attempt);
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
        bool retryWhenBusy,
        int attempt = 0)
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
                attempt < MaxTemporaryRaiseRestoreAttempts &&
                !_widgetsRaisedFromTray &&
                !_expandedWidgetLayerLease.IsActive)
            {
                QueueTemporaryRaisedWidgetRestore(
                    reason,
                    generation,
                    TimeSpan.FromMilliseconds(180),
                    attempt + 1);
            }
            else if (retryWhenBusy)
            {
                App.Log(
                    $"[ZOrder] TemporaryRaise restore gave up after {attempt} retries " +
                    $"reason={reason} generation={generation} — lease stays held " +
                    $"for the next explicit restore.");
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

    private bool NormalizeIdleWidgetZOrder(string reason, bool cohesionExit = false)
    {
        // Keep the legacy behavior when the system setting cannot be read:
        // skipping normalization on a query failure would silently reintroduce
        // the shadow-overlap artifact this ordering exists to prevent.
        if (!Win32Helper.TryGetWindowDropShadowEnabled(out bool dropShadowEnabled))
        {
            dropShadowEnabled = true;
        }

        if (_widgetsRaisedFromTray ||
            _isTogglingWidgetsDesktopLayer ||
            _sessionManager.IsInteractionActive ||
            _sessionManager.State == WidgetSessionState.Hidden ||
            HasActiveExpandedWidgetLayerLease() ||
            (!cohesionExit &&
                !IdleWidgetZOrderPolicy.ShouldNormalizeIdlePeerOrder(dropShadowEnabled)))
        {
            _idleNormalizeAnimationDefers = 0;
            App.LogVerbose(
                $"[ZOrder] Idle normalize skipped reason={reason} " +
                $"raised={_widgetsRaisedFromTray} toggling={_isTogglingWidgetsDesktopLayer} " +
                $"interaction={_sessionManager.IsInteractionActive} state={_sessionManager.State} " +
                $"expandedOwner=0x{_expandedWidgetLayerLease.WindowHandle.ToInt64():X} " +
                $"dropShadow={dropShadowEnabled}");
            return false;
        }

        List<IDesktopWidgetWindow> candidates = GetLoadedDesktopWindows()
            .Where(window => window.Visible && !window.IsRaisedAboveDesktopLayer)
            .ToList();

        // Ordering on transient animation bounds produces a different order
        // once the windows settle, which replays as a visible second reorder.
        // Wait for the transitions to finish instead of dropping the request.
        // A cohesion-exit request may not wait forever: the sort keys come
        // from RestingAnimationBounds, which already report the animation
        // target, so after the bounded defers the residue from the just-ended
        // expanded lease (#302) is applied with deterministic keys instead of
        // being abandoned to an unrelated future trigger.
        if (!cohesionExit &&
            candidates.Any(window => window.IsBoundsTransitionActive))
        {
            if (_idleNormalizeAnimationDefers < MaxIdleNormalizeAnimationDefers)
            {
                _idleNormalizeAnimationDefers++;
                App.LogVerbose(
                    $"[ZOrder] Idle normalize deferred reason={reason} " +
                    $"defers={_idleNormalizeAnimationDefers}");
                QueueIdleWidgetZOrderNormalization($"{reason}-animation-deferred");
            }
            else
            {
                App.LogVerbose(
                    $"[ZOrder] Idle normalize abandon reason={reason} " +
                    $"defers={_idleNormalizeAnimationDefers} still-animating");
                _idleNormalizeAnimationDefers = 0;
            }

            return false;
        }

        if (cohesionExit && candidates.Any(window => window.IsBoundsTransitionActive))
        {
            App.LogVerbose(
                $"[ZOrder] Cohesion-exit normalize applied while animating reason={reason} " +
                $"defers={_idleNormalizeAnimationDefers}");
        }

        _idleNormalizeAnimationDefers = 0;

        IReadOnlyList<IDesktopWidgetWindow> ordered =
            GetWindowsInIdleHighestFirstOrder(candidates);
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

        List<IDesktopWidgetWindow> visibleWindows =
            GetLoadedDesktopWindows().Where(window => window.Visible).ToList();
        if (visibleWindows.Count == 0)
        {
            return;
        }

        // Rapid title interaction (press-release-press, or a flyout bouncing
        // focus back to the title bar) used to replay a full group raise for
        // every press even while the previous raise was still live. Reuse the
        // live raise within the suppress window instead of re-issuing the
        // Z-order transactions (#375).
        DateTime nowUtc = DateTime.UtcNow;
        if (TitleActivationRaisePolicy.ShouldSkipRepeatRaise(
                _temporaryRaiseLease.IsActive,
                _lastTitleActivationRaiseWindowHandle == activeHwnd,
                nowUtc - _lastTitleActivationRaiseUtc))
        {
            _lastTitleActivationRaiseUtc = nowUtc;
            App.LogVerbose(
                $"[ZOrder] TitleActivatedAll reuses live raise " +
                $"active=0x{activeHwnd.ToInt64():X}");
            return;
        }

        var handles = GetWindowsInIdleHighestFirstOrder(visibleWindows)
            .Select(window => window.WindowHandle)
            .ToList();
        _lastTitleActivationRaiseWindowHandle = activeHwnd;
        _lastTitleActivationRaiseUtc = nowUtc;
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

    private IntPtr _lastTitleActivationRaiseWindowHandle;
    private DateTime _lastTitleActivationRaiseUtc = DateTime.MinValue;

    /// <summary>
    /// Keeps a tray-raised widget group contiguous after one DeskBox window
    /// or one of its transient owned windows takes activation. Windows can
    /// otherwise insert the previously foreground application between the
    /// active widget and its peers, making the other widgets appear to fall
    /// behind that application.
    /// </summary>
    private DateTime _suppressRaisedGroupReassertUntilUtc = DateTime.MinValue;

    internal void SuppressRaisedGroupReassertBriefly()
    {
        // The popover is permanently topmost (set once at construction), so
        // its open/close no longer performs any band migration. The reassert
        // suppression is kept as a cheap safety net for the activation
        // hand-off that still accompanies a popover close.
        _suppressRaisedGroupReassertUntilUtc =
            DateTime.UtcNow + TimeSpan.FromMilliseconds(400);
    }

    internal bool ReassertRaisedWidgetGroupAfterDeskBoxActivation(
        IntPtr activeWidgetHandle,
        string reason)
    {
        if (!_widgetsRaisedFromTray ||
            WidgetLayerService.UsesDesktopPinnedMode() ||
            activeWidgetHandle == IntPtr.Zero ||
            !Win32Helper.IsWindow(activeWidgetHandle) ||
            !IsForegroundDeskBoxWindow() ||
            DateTime.UtcNow < _suppressRaisedGroupReassertUntilUtc)
        {
            return false;
        }

        IReadOnlyList<IDesktopWidgetWindow> visibleWindows =
            GetWindowsInIdleHighestFirstOrder(
                GetLoadedDesktopWindows().Where(window => window.Visible));
        if (!visibleWindows.Any(window =>
                window.WindowHandle == activeWidgetHandle))
        {
            return false;
        }

        List<IntPtr> handles = [activeWidgetHandle];
        handles.AddRange(visibleWindows
            .Select(window => window.WindowHandle)
            .Where(handle => handle != activeWidgetHandle));
        bool applied =
            WidgetLayerService.ApplyRaisedGroupReassertOrder(handles, activeWidgetHandle);
        App.LogVerbose(
            $"[ZOrder] Raised group reasserted reason={reason} " +
            $"active=0x{activeWidgetHandle.ToInt64():X} " +
            $"count={handles.Count} applied={applied}");
        return applied;
    }

    public bool RequestRestoreRaisedWidgetsToDesktopLayer(string reason = "interaction-ended")
    {
        if (!_widgetsRaisedFromTray)
        {
            App.LogVerbose($"[TrayBatch] RestoreRequest ignored reason={reason} state=not-raised");
            return false;
        }

        if (!UiDispatch.HasAccess)
        {
            UiDispatch.RunOrDefer(() => RequestRestoreRaisedWidgetsToDesktopLayer(reason));
            return true;
        }

        App.LogVerbose($"[TrayBatch] RestoreRequest held reason={reason} until=next-toggle");
        return true;
    }

    private void QueueRequestedLayerRestoreCheck(string reason, TimeSpan delay)
    {
        long generation = _trayRaiseBatchGeneration;
        // RunOrDefer instead of a bare TryEnqueue on App.UiDispatcherQueue:
        // the bare call would NullReference before dispatcher-init.
        UiDispatch.RunOrDefer(async () =>
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
        if (foreground == IntPtr.Zero)
        {
            // Activation is mid-transition — for example an owned popover
            // hid itself while its owner widget is being activated, leaving
            // the foreground briefly unassigned. Treat the gap as "still
            // ours" instead of reading it as a DeskBox leave.
            return;
        }

        if (IsDeskBoxForegroundWindow(foreground))
        {
            _hasDeskBoxForegroundSinceRaise = true;
            App.LogVerbose($"[TrayBatch] RaisedState kept reason={reason} foreground=0x{foreground.ToInt64():X}");
            return;
        }

        if (IsTaskbarWindow(foreground))
        {
            if (QuickRevealTrayRaisePolicy.KeepsRaisedStateOnTaskbarForeground(
                    WidgetLayerService.UsesQuickRevealMode(),
                    _lastRaiseOriginatedFromTrayIcon))
            {
                App.LogVerbose(
                    $"[TrayBatch] RaisedState kept reason={reason} " +
                    "foreground=taskbar raiseSource=tray-icon");
                return;
            }

            if (WidgetLayerService.UsesQuickRevealMode())
            {
                QueueQuickRevealDismiss(
                    $"{reason}-taskbar",
                    outsideInteraction: true,
                    taskbarOrigin: true);
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

    private void QueueQuickRevealDismiss(
        string reason,
        bool outsideInteraction,
        bool taskbarOrigin = false)
    {
        if (_quickRevealDismissQueued || !HasVisibleWidgets)
        {
            return;
        }

        _lastQuickRevealDismissUtc = DateTime.UtcNow;
        _lastQuickRevealDismissTaskbarOrigin = taskbarOrigin;
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
            bool pressOverTaskbar = IsPointerOverTaskbar(cursor);
            if (!IsPointerOverDeskBoxWindow(cursor) &&
                (!pressOverTaskbar || WidgetLayerService.UsesQuickRevealMode()))
            {
                if (WidgetLayerService.UsesQuickRevealMode())
                {
                    _lastMouseButtonsDown = isDown;
                    QueueQuickRevealDismiss(
                        "mouse-sampler-outside-click",
                        outsideInteraction: true,
                        taskbarOrigin: pressOverTaskbar);
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
