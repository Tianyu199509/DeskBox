using System.Diagnostics;
using DeskBox.Platform;
using DeskBox.Services;
using Microsoft.UI.Dispatching;

namespace DeskBox;

public partial class App
{
    private static readonly TimeSpan PostStartupMemorySettleDelay = TimeSpan.FromSeconds(12);
    private static long s_workingSetTrimEpoch;

    internal static long WorkingSetTrimEpoch => Interlocked.Read(ref s_workingSetTrimEpoch);

    private readonly QuiescenceWorkingSetTrimTracker _quiescenceWorkingSetTrimTracker = new();
    private DispatcherQueueTimer? _quiescenceWorkingSetTrimTimer;
    private DispatcherQueueTimer? _postStartupMemorySettleTimer;
    private int _postStartupMemorySettleAttempts;
    private int _quiescenceWorkingSetTrimRunning;

    private bool IsQuiescenceWorkingSetTrimEnabled =>
        SettingsService.Settings.Performance.IdleWorkingSetTrimEnabled;

    private void StartQuiescenceWorkingSetTrim()
    {
        WidgetAnimationDisplayTiming.WarmInBackground();
        EnsureQuiescenceWorkingSetTrimTimerCreated();
        ReconcileQuiescenceWorkingSetTrimSchedule();
        SchedulePostStartupMemorySettle();
    }

    private void EnsureQuiescenceWorkingSetTrimTimerCreated()
    {
        if (_quiescenceWorkingSetTrimTimer is not null)
        {
            return;
        }

        _quiescenceWorkingSetTrimTimer = UiDispatcherQueue.CreateTimer();
        _quiescenceWorkingSetTrimTimer.IsRepeating = false;
        _quiescenceWorkingSetTrimTimer.Tick += QuiescenceWorkingSetTrimTimer_Tick;
    }

    internal void ReconcileQuiescenceWorkingSetTrimSchedule()
    {
        if (IsShuttingDown)
        {
            _quiescenceWorkingSetTrimTimer?.Stop();
            return;
        }

        if (!UiDispatcherQueue.HasThreadAccess)
        {
            UiDispatcherQueue.TryEnqueue(ReconcileQuiescenceWorkingSetTrimSchedule);
            return;
        }

        if (!IsQuiescenceWorkingSetTrimEnabled ||
            WidgetManager is not { } manager ||
            manager.CaptureMemoryCleanupVisibilitySnapshot().LoadedWindowCount == 0)
        {
            _quiescenceWorkingSetTrimTimer?.Stop();
            _quiescenceWorkingSetTrimTracker.NoteActivity();
            return;
        }

        DateTimeOffset now = DateTimeOffset.UtcNow;
        QuiescenceTrimActivitySnapshot activity = CaptureQuiescenceTrimActivity();
        _ = _quiescenceWorkingSetTrimTracker.Observe(
            now,
            activity,
            static () => Environment.WorkingSet);

        TimeSpan? nextDelay = _quiescenceWorkingSetTrimTracker.GetNextEvaluationDelay(
            now,
            activity);
        if (nextDelay is null)
        {
            _quiescenceWorkingSetTrimTimer?.Stop();
            return;
        }

        EnsureQuiescenceWorkingSetTrimTimerCreated();
        _quiescenceWorkingSetTrimTimer!.Stop();
        _quiescenceWorkingSetTrimTimer.Interval = nextDelay.Value;
        _quiescenceWorkingSetTrimTimer.Start();
    }

    private void SchedulePostStartupMemorySettle()
    {
        if (_postStartupMemorySettleTimer is null)
        {
            _postStartupMemorySettleTimer = UiDispatcherQueue.CreateTimer();
            _postStartupMemorySettleTimer.IsRepeating = false;
            _postStartupMemorySettleTimer.Interval = PostStartupMemorySettleDelay;
            _postStartupMemorySettleTimer.Tick += PostStartupMemorySettleTimer_Tick;
        }

        _postStartupMemorySettleAttempts = 0;
        _postStartupMemorySettleTimer.Start();
    }

    private void PostStartupMemorySettleTimer_Tick(DispatcherQueueTimer sender, object args)
    {
        sender.Stop();
        if (IsShuttingDown)
        {
            return;
        }

        MemoryCleanupActivitySnapshot activity = CaptureMemoryCleanupActivity();
        bool isUserBusy =
            activity.IsWidgetInteractionActive ||
            activity.IsDeskBoxForeground ||
            activity.IsPointerOverDeskBox ||
            activity.IsSettingsOpen ||
            activity.IsOnboardingOpen ||
            activity.IsSearchPopupVisible ||
            activity.IsDesktopOrganizationOpen ||
            WidgetManager?.HasActiveVisualWork == true;
        if (isUserBusy)
        {
            if (_postStartupMemorySettleAttempts < 3)
            {
                _postStartupMemorySettleAttempts++;
                sender.Start();
                return;
            }

            _postStartupMemorySettleTimer!.Tick -= PostStartupMemorySettleTimer_Tick;
            _postStartupMemorySettleTimer = null;
            Log("[Memory] Post-startup GC settle skipped after max busy retries");
            return;
        }

        _postStartupMemorySettleTimer!.Tick -= PostStartupMemorySettleTimer_Tick;
        _postStartupMemorySettleTimer = null;

        _ = Task.Run(() =>
        {
            if (IsShuttingDown)
            {
                return;
            }

            MemoryReclaimResult result = MemoryReclaimer.TryCollect(
                "post-startup-settle",
                bypassCooldown: true,
                updateCooldown: false);
            bool workingSetTrimmed = false;
            long workingSetAfter = Environment.WorkingSet;
            if (result.Executed && !IsShuttingDown && IsQuiescenceWorkingSetTrimEnabled)
            {
                workingSetTrimmed = Win32Helper.TrimWorkingSet();
                workingSetAfter = CompleteWorkingSetTrim(
                    workingSetTrimmed,
                    "post-startup-settle");
                RequestQuiescenceScheduleReconcile();
            }

            Log(
                $"[Memory] Post-startup GC settle status={result.Status} " +
                $"heapBeforeMB={result.HeapBeforeBytes / (1024.0 * 1024):F1} " +
                $"heapAfterMB={result.HeapAfterBytes / (1024.0 * 1024):F1} " +
                $"privateBeforeMB={result.PrivateBeforeBytes / (1024.0 * 1024):F1} " +
                $"privateAfterMB={result.PrivateAfterBytes / (1024.0 * 1024):F1} " +
                $"workingSetTrimmed={workingSetTrimmed} " +
                $"workingSetAfterMB={workingSetAfter / (1024.0 * 1024):F1} " +
                $"durationMs={result.DurationMilliseconds}");
        });
    }

    private void StopQuiescenceWorkingSetTrim()
    {
        if (_postStartupMemorySettleTimer is not null)
        {
            _postStartupMemorySettleTimer.Stop();
            _postStartupMemorySettleTimer.Tick -= PostStartupMemorySettleTimer_Tick;
            _postStartupMemorySettleTimer = null;
        }

        if (_quiescenceWorkingSetTrimTimer is null)
        {
            return;
        }

        _quiescenceWorkingSetTrimTimer.Stop();
        _quiescenceWorkingSetTrimTimer.Tick -= QuiescenceWorkingSetTrimTimer_Tick;
        _quiescenceWorkingSetTrimTimer = null;
    }

    private void NoteQuiescenceWorkingSetTrimActivity()
    {
        _quiescenceWorkingSetTrimTracker.NoteActivity();
        if (IsShuttingDown || !IsQuiescenceWorkingSetTrimEnabled)
        {
            _quiescenceWorkingSetTrimTimer?.Stop();
            return;
        }

        ReconcileQuiescenceWorkingSetTrimSchedule();
    }

    private QuiescenceTrimActivitySnapshot CaptureQuiescenceTrimActivity()
    {
        MemoryCleanupActivitySnapshot activity = CaptureMemoryCleanupActivity();
        bool hasUnoccludedVisibleWidgets =
            WidgetManager?.HasUnoccludedVisibleWidgets ?? activity.HasVisibleWidgets;
        return new QuiescenceTrimActivitySnapshot(
            HasVisibleWidgets: hasUnoccludedVisibleWidgets,
            IsWidgetInteractionActive: activity.IsWidgetInteractionActive,
            HasActiveVisualWork: WidgetManager?.HasActiveVisualWork == true,
            IsTransientUiOpen:
                activity.IsSettingsOpen ||
                activity.IsOnboardingOpen ||
                activity.IsSearchPopupVisible ||
                activity.IsDesktopOrganizationOpen,
            IsDeskBoxForeground: activity.IsDeskBoxForeground,
            IsPointerOverDeskBox: activity.IsPointerOverDeskBox,
            HasAmbientVisualWork: WidgetManager?.HasAmbientVisualWork == true);
    }

    private void QuiescenceWorkingSetTrimTimer_Tick(DispatcherQueueTimer sender, object args)
    {
        sender.Stop();
        if (Volatile.Read(ref _quiescenceWorkingSetTrimRunning) != 0)
        {
            return;
        }

        if (!IsQuiescenceWorkingSetTrimEnabled ||
            WidgetManager is not { } manager ||
            manager.CaptureMemoryCleanupVisibilitySnapshot().LoadedWindowCount == 0)
        {
            _quiescenceWorkingSetTrimTracker.NoteActivity();
            return;
        }

        DateTimeOffset now = DateTimeOffset.UtcNow;
        QuiescenceTrimActivitySnapshot activity = CaptureQuiescenceTrimActivity();
        QuiescenceTrimDecision decision = _quiescenceWorkingSetTrimTracker.Observe(
            now,
            activity,
            static () => Environment.WorkingSet);
        if (decision.ShouldTrim)
        {
            SafeFireAndForget(() => RunQuiescenceWorkingSetTrimAsync(decision));
            return;
        }

        TimeSpan? nextDelay = _quiescenceWorkingSetTrimTracker.GetNextEvaluationDelay(
            now,
            activity);
        if (nextDelay is not null && _quiescenceWorkingSetTrimTimer is not null)
        {
            _quiescenceWorkingSetTrimTimer.Interval = nextDelay.Value;
            _quiescenceWorkingSetTrimTimer.Start();
        }
    }

    private async Task RunQuiescenceWorkingSetTrimAsync(QuiescenceTrimDecision decision)
    {
        if (Interlocked.Exchange(ref _quiescenceWorkingSetTrimRunning, 1) != 0)
        {
            return;
        }

        try
        {
            QuiescenceTrimDecision recheck = _quiescenceWorkingSetTrimTracker.Observe(
                DateTimeOffset.UtcNow,
                CaptureQuiescenceTrimActivity(),
                static () => Environment.WorkingSet);
            if (!recheck.ShouldTrim)
            {
                Log(
                    $"[Memory] Quiescence working-set trim skipped " +
                    $"reason={recheck.Reason} tier={decision.Tier}");
                return;
            }

            long workingSetBefore = Environment.WorkingSet;
            long started = Stopwatch.GetTimestamp();
            bool trimmed = await Task.Run(Win32Helper.TrimWorkingSet);
            long workingSetAfter = CompleteWorkingSetTrim(
                trimmed,
                $"quiescence:{decision.Tier}");
            Log(
                $"[Memory] Quiescence working-set trim completed trimmed={trimmed} " +
                $"tier={decision.Tier} quietSeconds={decision.QuietDuration.TotalSeconds:F1} " +
                $"durationMs={Stopwatch.GetElapsedTime(started).TotalMilliseconds:F1} " +
                $"workingSetBeforeMB={workingSetBefore / (1024.0 * 1024):F1} " +
                $"workingSetAfterMB={workingSetAfter / (1024.0 * 1024):F1}");
        }
        finally
        {
            Volatile.Write(ref _quiescenceWorkingSetTrimRunning, 0);
            ReconcileQuiescenceWorkingSetTrimSchedule();
        }
    }

    /// <summary>
    /// Shared bookkeeping for every working-set trim path. Tells the
    /// quiescence tracker a trim happened so its cooldown and settle gates
    /// account for trims it did not initiate.
    /// </summary>
    private long CompleteWorkingSetTrim(bool trimmed, string reason)
    {
        _ = reason;
        long workingSetAfter = Environment.WorkingSet;
        if (trimmed)
        {
            // SetProcessWorkingSetSize(-1, -1) only pages cold physical frames
            // to standby and does not invalidate XAML Measure/Arrange or Visual
            // state; advancing MemoryCleanupEpoch here used to wake every
            // collapsed capsule into a full-size re-layout + 2-frame DComp
            // warmup right after the trim, faulting ~60 MB straight back in.
            // Track a dedicated WorkingSetTrimEpoch instead so idle capsules
            // stay asleep while the first post-trim expand knows its expanded
            // visual pages are cold and uses shell-first deferred hydration.
            Interlocked.Increment(ref s_workingSetTrimEpoch);
            _quiescenceWorkingSetTrimTracker.CommitTrim(DateTimeOffset.UtcNow);
        }

        return workingSetAfter;
    }
}
