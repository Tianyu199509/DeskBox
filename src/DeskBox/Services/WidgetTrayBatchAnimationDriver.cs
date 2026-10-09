// Copyright (c) DeskBox. All rights reserved.

using System.Diagnostics;
using DeskBox.Helpers;
using DeskBox.Platform;

namespace DeskBox.Services;

/// <summary>
/// One window's participation in a shared batch tray animation.
/// The batch driver moves the HWND physically; the owning window keeps
/// its own GPU-driven opacity/scale Composition animations and its own
/// completion logic.
/// </summary>
public sealed class WidgetTrayBatchAnimationEntry
{
    public required IntPtr WindowHandle { get; init; }
    public required int BaseX { get; init; }
    public required int BaseY { get; init; }
    public required double FromOffsetX { get; init; }
    public required double FromOffsetY { get; init; }
    public required double ToOffsetX { get; init; }
    public required double ToOffsetY { get; init; }
    public required int RefreshRateHz { get; init; }
    public int? RefreshAnchorX { get; init; }
    public int? RefreshAnchorY { get; init; }

    /// <summary>
    /// Optional per-frame visual hook (eased progress) for effects whose
    /// visuals must track the shared position clock — e.g. the crossing fade,
    /// whose opacity follows the window's boundary-relative displacement.
    /// </summary>
    public Action<double>? FrameVisual { get; init; }

    /// <summary>Returns false when the owning window started a newer animation.</summary>
    public required Func<bool> IsValid { get; init; }

    /// <summary>Invoked once on the UI thread after the final frame commits.</summary>
    public required Action Completed { get; init; }

    /// <summary>Restores a usable state if native movement could not complete.</summary>
    public Action? Failed { get; init; }
}

internal readonly record struct WidgetTrayWindowPosition(IntPtr WindowHandle, int X, int Y);

/// <summary>
/// Keeps Win32 transaction ownership and success accounting independent from
/// the animation clock so native failure paths can be verified without HWNDs.
/// </summary>
internal static class WidgetTrayWindowPositionCommitter
{
    internal interface INativeApi
    {
        IntPtr Begin(int count);
        IntPtr Defer(IntPtr transaction, WidgetTrayWindowPosition position);
        bool End(IntPtr transaction);
        bool Set(WidgetTrayWindowPosition position);
    }

    private sealed class NativeApi : INativeApi
    {
        private const uint MoveFlags =
            Win32Helper.SWP_NOSIZE | Win32Helper.SWP_NOZORDER | Win32Helper.SWP_NOACTIVATE;

        public IntPtr Begin(int count) => Win32Helper.BeginDeferWindowPos(count);
        public IntPtr Defer(IntPtr transaction, WidgetTrayWindowPosition position) =>
            Win32Helper.DeferWindowPos(transaction, position.WindowHandle, IntPtr.Zero,
                position.X, position.Y, 0, 0, MoveFlags);
        public bool End(IntPtr transaction) => Win32Helper.EndDeferWindowPos(transaction);
        public bool Set(WidgetTrayWindowPosition position) =>
            Win32Helper.SetWindowPos(position.WindowHandle, IntPtr.Zero,
                position.X, position.Y, 0, 0, MoveFlags);
    }

    private static readonly INativeApi SystemApi = new NativeApi();

    public static bool[] Commit(IReadOnlyList<WidgetTrayWindowPosition> positions, INativeApi? api = null)
    {
        api ??= SystemApi;
        var results = new bool[positions.Count];
        if (positions.Count == 0)
        {
            return results;
        }

        IntPtr transaction = api.Begin(positions.Count);
        if (transaction != IntPtr.Zero)
        {
            foreach (var position in positions)
            {
                transaction = api.Defer(transaction, position);
                if (transaction == IntPtr.Zero)
                {
                    // DeferWindowPos destroys the transaction on failure. Do
                    // not submit the previous, now-invalid handle to End.
                    break;
                }
            }

            if (transaction != IntPtr.Zero && api.End(transaction))
            {
                Array.Fill(results, true);
                return results;
            }
        }

        // A failed transaction can leave the outcome uncertain. Retry every
        // participant individually and only acknowledge successful calls.
        for (int i = 0; i < positions.Count; i++)
        {
            results[i] = api.Set(positions[i]);
        }
        return results;
    }
}

/// <summary>
/// Drives a batch of widget slide animations from a single
/// shared interaction-clock registration,
/// committing all window positions in one DeferWindowPos transaction per
/// frame so every window stays frame-aligned (no per-window commit jitter).
/// Windows move in lockstep by default; an optional per-window stagger delay
/// produces a top-left-first wave while keeping the atomic per-frame commit.
/// Physical movement semantics are preserved: HWNDs still travel off the
/// screen; only the commit mechanism changes from N independent
/// SetWindowPos calls to one atomic batch commit.
/// </summary>
public sealed class WidgetTrayBatchAnimationDriver
{
    private readonly List<WidgetTrayBatchAnimationEntry> _entries = new();
    private readonly Dictionary<WidgetTrayBatchAnimationEntry, EntryMotionState> _motionStates = new();
    private readonly List<WidgetTrayWindowPosition> _pendingMoves = new();
    private readonly List<(EntryMotionState State, int RefreshRateHz)> _pendingParticipants = new();
    private readonly Action<string> _log;
    private readonly Func<Action, Func<double>, IDisposable> _registerFrame;
    private readonly Func<WidgetTrayBatchAnimationEntry, double> _getFrameBudget;
    private readonly Func<IReadOnlyList<WidgetTrayWindowPosition>, bool[]> _commitPositions;
    private readonly Func<long> _getTimestamp;
    private long? _startedTimestamp;
    private double _durationMs = 1;
    private double _maxStaggerDelayMs;
    private string _easingIntensity = string.Empty;
    private bool _isShowing;
    private int _remainingDelayFrames;
    private bool _isRunning;
    private IDisposable? _frameRegistration;
    private WidgetTrayAnimationFrameTracker? _frameTracker;
    private TaskCompletionSource? _idleCompletion;
    private Task _idleTask = Task.CompletedTask;

    public WidgetTrayBatchAnimationDriver(Action<string>? log = null)
        : this(
            (callback, budget) => WidgetCompactAnimationCoordinator.Register(callback, budget),
            entry => WidgetCompactAnimationCoordinator.GetFrameBudgetMillisecondsForPoint(
                entry.RefreshAnchorX ?? entry.BaseX, entry.RefreshAnchorY ?? entry.BaseY),
            positions => WidgetTrayWindowPositionCommitter.Commit(positions),
            Stopwatch.GetTimestamp,
            log)
    {
    }

    internal WidgetTrayBatchAnimationDriver(
        Func<Action, Func<double>, IDisposable> registerFrame,
        Func<WidgetTrayBatchAnimationEntry, double> getFrameBudget,
        Func<IReadOnlyList<WidgetTrayWindowPosition>, bool[]> commitPositions,
        Func<long> getTimestamp,
        Action<string>? log = null)
    {
        _registerFrame = registerFrame;
        _getFrameBudget = getFrameBudget;
        _commitPositions = commitPositions;
        _getTimestamp = getTimestamp;
        _log = log ?? (_ => { });
    }

    public bool IsRunning => _isRunning;

    /// <summary>
    /// Completes after the active batch has fully stopped. Visibility request
    /// queues use this to avoid starting a new Composition animation while the
    /// previous batch is still rendering.
    /// </summary>
    public Task WaitForIdleAsync()
    {
        return _idleTask;
    }

    /// <summary>
    /// Starts a shared batch run. Any previously running batch is cancelled
    /// first (its entries complete nothing; window-side generation checks
    /// keep state consistent). With <paramref name="staggerIntervalMs"/> &gt; 0
    /// each window starts after a per-window delay (top-left first) while
    /// positions still commit through the same single per-frame transaction.
    /// </summary>
    public void Start(
        IReadOnlyList<WidgetTrayBatchAnimationEntry> entries,
        int durationMs,
        string easingIntensity,
        bool isShowing,
        int startDelayFrames,
        int staggerIntervalMs = 0)
    {
        Cancel();
        _idleTask = Task.CompletedTask;
        if (entries.Count == 0)
        {
            return;
        }

        _entries.AddRange(entries);
        double maxDelayMs = 0;
        if (staggerIntervalMs > 0 && entries.Count > 1)
        {
            // Wave order: top-left window leads, the rest follow by position.
            var ordered = entries
                .Select((entry, index) => (entry, index))
                .OrderBy(pair => pair.entry.BaseY)
                .ThenBy(pair => pair.entry.BaseX)
                .ToList();
            double[] delays = new double[entries.Count];
            for (int rank = 0; rank < ordered.Count; rank++)
            {
                delays[ordered[rank].index] = (double)staggerIntervalMs * rank;
            }

            for (int index = 0; index < entries.Count; index++)
            {
                var entry = entries[index];
                _motionStates[entry] = new EntryMotionState(entry)
                {
                    DelayMs = delays[index]
                };
                maxDelayMs = Math.Max(maxDelayMs, delays[index]);
            }
        }
        else
        {
            foreach (var entry in entries)
            {
                _motionStates[entry] = new EntryMotionState(entry);
            }
        }

        _durationMs = Math.Max(1, durationMs);
        _maxStaggerDelayMs = maxDelayMs;
        _easingIntensity = easingIntensity;
        _isShowing = isShowing;
        _remainingDelayFrames = Math.Max(0, startDelayFrames);
        _startedTimestamp = null;
        _idleCompletion = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        _idleTask = _idleCompletion.Task;
        _isRunning = true;
        StartFrameClock();
        _log(
            $"[BatchAnim] Start count={_entries.Count} durationMs={_durationMs} " +
            $"mode={(isShowing ? "show" : "hide")} delayFrames={_remainingDelayFrames} " +
            $"staggerIntervalMs={staggerIntervalMs}");
    }

    private void StartFrameClock()
    {
        StopFrameClock();
        _frameRegistration = _registerFrame(
            OnRenderingFrame,
            () => _entries.Select(_getFrameBudget).DefaultIfEmpty(1000d / 60).Min());
    }

    private void StopFrameClock()
    {
        _frameRegistration?.Dispose();
        _frameRegistration = null;
    }

    public void Cancel()
    {
        if (!_isRunning)
        {
            return;
        }

        ReportFrameMetrics("cancelled");
        StopCore();
        _log("[BatchAnim] Cancelled");
    }

    private void OnRenderingFrame()
    {
        try
        {
            if (!_isRunning)
            {
                return;
            }

            // Drop entries whose window started a newer animation meanwhile.
            for (int i = _entries.Count - 1; i >= 0; i--)
            {
                if (!_entries[i].IsValid())
                {
                    _motionStates.Remove(_entries[i]);
                    _entries.RemoveAt(i);
                }
            }

            if (_entries.Count == 0)
            {
                Cancel();
                return;
            }

            // Give freshly shown windows a couple of frames to commit their
            // first surface before the shared clock starts (same semantics
            // as the per-window PlayAfterContentReady wait).
            if (_remainingDelayFrames > 0)
            {
                _remainingDelayFrames--;
                return;
            }

            long frameTimestamp = _getTimestamp();
            if (_startedTimestamp is null)
            {
                _startedTimestamp = frameTimestamp;
                _frameTracker = new WidgetTrayAnimationFrameTracker(
                    frameTimestamp,
                    _entries.Select(entry => entry.RefreshRateHz));
            }
            _frameTracker?.RecordFrame(frameTimestamp);

            double nowMs = Stopwatch.GetElapsedTime(_startedTimestamp.Value, frameTimestamp).TotalMilliseconds;
            // The batch ends when the latest-staggered window has finished.
            double totalMs = _durationMs + _maxStaggerDelayMs;
            double rawProgress = Math.Clamp(nowMs / totalMs, 0.0, 1.0);
            bool finalFrame = nowMs >= totalMs;
            bool committed = MoveEntriesFrame(nowMs, finalFrame, frameTimestamp);

            if (finalFrame && !committed)
            {
                FailBatch(new InvalidOperationException("One or more tray animation windows could not reach their final position."));
                return;
            }

            if (rawProgress < 1.0)
            {
                return;
            }

            FinishBatch();
        }
        catch (Exception ex)
        {
            // Never let a frame exception escape the Rendering callback.
            App.Log($"[WidgetTrayBatchAnimationDriver] Frame exception: {ex.Message}\n{ex.StackTrace}");
            FailBatch(ex);
        }
    }

    private bool MoveEntriesFrame(double nowMs, bool finalFrame, long timestamp)
    {
        long started = Stopwatch.GetTimestamp();
        bool committed = MoveEntriesFrameCore(nowMs, finalFrame, timestamp);
        double elapsedMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        if (elapsedMs >= 8)
        {
            string details = $"count={_entries.Count} elapsedMs={elapsedMs:F1}";
            PerformanceLogger.Mark("TrayBoundsBatch", details);
            App.LogVerbose($"[TrayBoundsBatch] {details}");
        }
        return committed;
    }

    private bool MoveEntriesFrameCore(double nowMs, bool finalFrame, long timestamp)
    {
        var moves = _pendingMoves;
        var participants = _pendingParticipants;
        moves.Clear();
        participants.Clear();
        foreach (var entry in _entries)
        {
            EntryMotionState state = _motionStates[entry];
            double budgetMs = _getFrameBudget(entry);
            if (!state.Pacing.ShouldSubmit(nowMs, budgetMs, force: finalFrame))
            {
                continue;
            }

            // Each window runs its own eased progress on the shared clock,
            // offset by its stagger delay (zero when staggering is off).
            double entryElapsedMs = nowMs - state.DelayMs;
            double entryProgress = Math.Clamp(entryElapsedMs / _durationMs, 0.0, 1.0);
            double easedProgress = entryProgress >= 1.0
                ? 1.0
                : WidgetAnimationSettings.Ease(entryProgress, _easingIntensity, _isShowing);
            var position = GetEntryFramePosition(entry, easedProgress);
            if (!finalFrame && state.LastPosition == position)
            {
                continue;
            }

            // Position unchanged implies displacement unchanged, so the visual
            // hook only needs to run on frames that actually move the window.
            entry.FrameVisual?.Invoke(easedProgress);
            moves.Add(new WidgetTrayWindowPosition(entry.WindowHandle, position.X, position.Y));
            participants.Add((state, (int)Math.Round(1000.0 / Math.Max(0.1, budgetMs))));
        }

        if (moves.Count == 0)
        {
            return true;
        }

        long started = Stopwatch.GetTimestamp();
        bool[] results = _commitPositions(moves);
        double workMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        bool allCommitted = true;
        for (int i = 0; i < results.Length; i++)
        {
            participants[i].State.Pacing.RecordSubmission(nowMs, workMs);
            if (results[i])
            {
                participants[i].State.LastPosition = (moves[i].X, moves[i].Y);
                _frameTracker?.RecordPositionSubmission(timestamp, participants[i].RefreshRateHz);
            }
            else
            {
                allCommitted = false;
            }
        }
        return allCommitted;
    }

    private static (int X, int Y) GetEntryFramePosition(
        WidgetTrayBatchAnimationEntry entry,
        double easedProgress)
    {
        double offsetX = Lerp(entry.FromOffsetX, entry.ToOffsetX, easedProgress);
        double offsetY = Lerp(entry.FromOffsetY, entry.ToOffsetY, easedProgress);
        return (
            entry.BaseX + (int)Math.Round(offsetX),
            entry.BaseY + (int)Math.Round(offsetY));
    }

    private void FinishBatch(string outcome = "completed")
    {
        var completed = _entries.ToArray();
        ReportFrameMetrics(outcome);
        StopCore();
        foreach (var entry in completed)
        {
            try
            {
                if (entry.IsValid())
                {
                    entry.Completed();
                }
            }
            catch (Exception ex)
            {
                App.Log($"[WidgetTrayBatchAnimationDriver] Completed exception: {ex.Message}");
            }
        }
    }

    private void FailBatch(Exception failure)
    {
        _log($"[BatchAnim] Failed: {failure.Message}");
        var failedEntries = _entries.ToArray();
        ReportFrameMetrics("failed");
        StopCore(failure);
        foreach (var entry in failedEntries)
        {
            try
            {
                if (entry.IsValid())
                {
                    entry.Failed?.Invoke();
                }
            }
            catch (Exception ex) { App.Log($"[WidgetTrayBatchAnimationDriver] Failure cleanup: {ex.Message}"); }
        }
    }

    private void ReportFrameMetrics(string outcome)
    {
        WidgetTrayAnimationFrameTracker? tracker = _frameTracker;
        _frameTracker = null;
        WidgetTrayAnimationDiagnostics.Report(
            tracker,
            _getTimestamp(),
            _isShowing,
            outcome,
            "batch",
            _log);
    }

    private void StopCore(Exception? failure = null)
    {
        TaskCompletionSource? idleCompletion = _idleCompletion;
        _idleCompletion = null;
        _isRunning = false;
        _entries.Clear();
        _motionStates.Clear();
        _pendingMoves.Clear();
        _pendingParticipants.Clear();
        _startedTimestamp = null;
        _frameTracker = null;
        StopFrameClock();
        if (failure is null)
        {
            idleCompletion?.TrySetResult();
        }
        else
        {
            idleCompletion?.TrySetException(failure);
        }
    }

    private sealed class EntryMotionState
    {
        public EntryMotionState(WidgetTrayBatchAnimationEntry entry)
        {
            LastPosition = GetEntryFramePosition(entry, 0);
            Pacing.Reset(0, 1000.0 / Math.Max(1, entry.RefreshRateHz));
        }

        public WidgetAnimationFramePacingPolicy Pacing { get; } = new();

        public double DelayMs { get; init; }

        public (int X, int Y) LastPosition { get; set; }
    }

    private static double Lerp(double from, double to, double progress)
    {
        return from + (to - from) * progress;
    }
}
