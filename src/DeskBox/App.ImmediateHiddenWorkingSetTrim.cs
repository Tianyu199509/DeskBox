using System.Diagnostics;
using DeskBox.Helpers;
using DeskBox.Platform;
using DeskBox.Services;
using Microsoft.UI.Dispatching;

namespace DeskBox;

public partial class App
{
    // A quick hide/show round trip (hotkey toggles) must not pay the
    // back-fault cost of a trim the user is about to undo. The grace window
    // also collapses consecutive hide/show cycles into at most one trim.
    // Any visible transition during the window cancels the pending request,
    // so only a genuinely hidden session trims. 3 s covered only rapid
    // toggling: a reveal after a trim costs a 250-296 ms batch animation
    // (measured 2026-10-21, budget 16.7 ms) because SetProcessWorkingSetSize
    // keeps no resident floor — 30 s keeps every casual reveal rhythm out of
    // the trim/refault loop while the drop still fires for real idle.
    private const int ImmediateHiddenWorkingSetTrimGracePeriodMs = 30000;

    // Mirrors the quiescence episode floor's semantics (skip an already-lean
    // process): an unconditional trim of a small working set buys no memory
    // worth its back-fault cost.
    private const long ImmediateHiddenWorkingSetFloorBytes = 80L * 1024 * 1024;

    private readonly HiddenWorkingSetTrimTracker _immediateHiddenWorkingSetTrimTracker = new();

    private bool IsImmediateHiddenWorkingSetTrimEnabled =>
        SettingsService.Settings.Performance.IdleWorkingSetTrimEnabled;

    internal void ObserveWidgetVisibilityForImmediateWorkingSetTrim(
        WidgetMemoryVisibilitySnapshot visibility,
        string reason)
    {
        long? request = _immediateHiddenWorkingSetTrimTracker.Observe(
            visibility,
            IsImmediateHiddenWorkingSetTrimEnabled);
        if (request is not long generation)
        {
            return;
        }

        // Let the native hidden notification and its synchronous cleanup finish
        // before touching pages used by the hide animation or its completion.
        bool enqueued = UiDispatcherQueue.TryEnqueue(
            DispatcherQueuePriority.Low,
            () => SafeFireAndForget(() => RunImmediateHiddenWorkingSetTrimAsync(
                generation,
                reason)));
        if (!enqueued)
        {
            _immediateHiddenWorkingSetTrimTracker.TryConsume(generation);
            Log("[Memory] Immediate hidden working-set trim skipped reason=dispatcher-unavailable");
        }
    }

    private async Task RunImmediateHiddenWorkingSetTrimAsync(long generation, string reason)
    {
        var manager = WidgetManager;
        if (manager is null || !_immediateHiddenWorkingSetTrimTracker.IsPending(generation))
        {
            return;
        }

        await manager.WaitForTrayAnimationsIdleAsync();
        await Task.Delay(ImmediateHiddenWorkingSetTrimGracePeriodMs);
        if (!_immediateHiddenWorkingSetTrimTracker.TryConsume(generation))
        {
            return;
        }

        WidgetMemoryVisibilitySnapshot visibility =
            manager.CaptureMemoryCleanupVisibilitySnapshot();
        if (!IsImmediateHiddenWorkingSetTrimEnabled ||
            visibility.LoadedWindowCount == 0 ||
            visibility.LogicalVisibleCount != 0 ||
            visibility.HasNativeVisibleWidgets ||
            manager.HasActiveVisualWork ||
            Volatile.Read(ref _quiescenceWorkingSetTrimRunning) != 0 ||
            _quiescenceWorkingSetTrimTracker.LastTrimWithin(
                DateTimeOffset.UtcNow,
                QuiescenceWorkingSetTrimTracker.MinimumTrimInterval) ||
            !CanRunBackgroundMemoryCleanup())
        {
            Log($"[Memory] Immediate hidden working-set trim skipped reason=activity-or-disabled trigger={reason}");
            return;
        }

        using var process = Process.GetCurrentProcess();
        process.Refresh();
        long workingSetBefore = process.WorkingSet64;
        long privateBytesBefore = process.PrivateMemorySize64;
        if (workingSetBefore < ImmediateHiddenWorkingSetFloorBytes)
        {
            _immediateHiddenWorkingSetTrimTracker.Complete(generation, trimmed: false);
            Log(
                "[Memory] Immediate hidden working-set trim skipped reason=below-floor " +
                $"workingSetMB={workingSetBefore / (1024.0 * 1024):F1} trigger={reason}");
            return;
        }

        long started = Stopwatch.GetTimestamp();
        bool trimmed = Win32Helper.TrimWorkingSet();
        _immediateHiddenWorkingSetTrimTracker.Complete(generation, trimmed);
        CompleteWorkingSetTrim(trimmed, $"immediate-hidden:{reason}");

        process.Refresh();
        Log(
            $"[Memory] Immediate hidden working-set trim completed workingSetTrimmed={trimmed} " +
            $"durationMs={Stopwatch.GetElapsedTime(started).TotalMilliseconds:F1} " +
            $"workingSetBeforeMB={workingSetBefore / (1024.0 * 1024):F1} " +
            $"workingSetAfterMB={process.WorkingSet64 / (1024.0 * 1024):F1} " +
            $"privateBeforeMB={privateBytesBefore / (1024.0 * 1024):F1} " +
            $"privateAfterMB={process.PrivateMemorySize64 / (1024.0 * 1024):F1} " +
            $"loadedWidgets={visibility.LoadedWindowCount} logicalVisibleWidgets=0 nativeVisibleWidgets=0 " +
            $"forcedCollection=false fullViewRebuilds=0 trigger={reason}");
    }
}
