// Copyright (c) DeskBox. All rights reserved.

using System.Diagnostics;
using DeskBox.Helpers;
using DeskBox.Models;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;

namespace DeskBox.Services;

/// <summary>
/// Shares an active-only frame clock across dragging, resizing, capsule and tray
/// animations. Display timing is a budget; callbacks are not proof of presentation.
/// </summary>
internal static class WidgetCompactAnimationCoordinator
{
    // A native bounds/clip transition still has one UI-thread coordinator, but
    // multiple capsules may animate concurrently (e.g. one collapsing while the
    // cursor expands the next). Allowing several in-flight transitions avoids
    // dropping a capsule's animation when the slot is occupied. First-frame
    // commit pressure is absorbed by the expansion warm-up instead of by
    // serializing transitions.
    internal const int MaximumConcurrentBoundsTransitions = int.MaxValue;

    private static readonly Dictionary<long, Action> FrameCallbacks = [];
    private static readonly Dictionary<long, FrameTarget> FrameTargets = [];
    private static KeyValuePair<long, Action>[] s_frameCallbackSnapshot = [];
    private static bool s_frameCallbackSnapshotDirty;
    private static readonly HashSet<long> BoundsTransitionRegistrations = [];
    private static readonly Dictionary<IntPtr, PendingBoundsMove> PendingBoundsMoves = [];
    private static long s_nextRegistrationId;
    private static bool s_isRenderingSubscribed;
    private static bool s_isDispatchingFrame;
    private static IDisposable? s_clockBoostLease;
    private static DispatcherQueue? s_windows10FrameDispatcher;
    private static DispatcherQueueTimer? s_windows10FrameTimer;
    private static Windows10ClockRun? s_windows10ClockRun;

    // Session-level tick health, sampled on the UI thread from every frame
    // dispatch. The recent overrun bitmask feeds the interaction-time backdrop
    // simplification decision (see InteractionBackdropSimplificationPolicy).
    private static long s_lastFrameTickTimestamp;
    private static double s_frameTickBudgetMs;
    private static long s_recentOverrunMask;

    private enum Windows10FrameClockSource
    {
        None,
        CompositionRendering,
        DwmFlushThread,
        RefreshTimer
    }

    private static Windows10FrameClockSource s_windows10ClockSource =
        Windows10FrameClockSource.None;

    private sealed class Windows10ClockRun(DispatcherQueue dispatcher)
    {
        internal readonly DispatcherQueue Dispatcher = dispatcher;
        internal volatile bool IsActive = true;
        internal int PendingTick;
        internal int InstantFlushCount;
        internal double TargetIntervalMilliseconds;
        internal readonly WidgetFrameClockHealthPolicy Health = new();
    }

    private sealed class FrameTarget(IntPtr windowHandle, Func<IEnumerable<IntPtr>>? windows, bool paceToDisplay,
        Func<double>? frameBudget = null)
    {
        internal readonly IntPtr WindowHandle = windowHandle;
        internal readonly Func<IEnumerable<IntPtr>>? Windows = windows;
        internal readonly bool PaceToDisplay = paceToDisplay;
        internal readonly Func<double>? FrameBudget = frameBudget;
        internal readonly WidgetAnimationFramePacingPolicy Pacing = new();
        internal bool Initialized;
        internal double BudgetMilliseconds = 1000d / 60;
    }

    private readonly record struct PendingBoundsMove(
        IntPtr WindowHandle,
        RectInt32 Bounds,
        uint Flags,
        Action BeforeCommit,
        Action<bool> AfterCommit,
        Action Fallback);

    public static IDisposable Register(Action frameCallback, IntPtr windowHandle = default, bool paceToDisplay = false)
    {
        return RegisterCore(frameCallback, false, new FrameTarget(windowHandle, null, paceToDisplay));
    }

    public static IDisposable Register(Action frameCallback, Func<IEnumerable<IntPtr>> windowHandles)
    {
        ArgumentNullException.ThrowIfNull(windowHandles);
        return RegisterCore(frameCallback, false, new FrameTarget(IntPtr.Zero, windowHandles, false));
    }

    public static double GetFrameBudgetMilliseconds(IntPtr windowHandle) =>
        WidgetAnimationDisplayTiming.GetFrameBudgetMilliseconds(windowHandle);

    public static double GetFrameBudgetMillisecondsForPoint(int screenX, int screenY) =>
        WidgetAnimationDisplayTiming.GetFrameBudgetMillisecondsForPoint(screenX, screenY);

    public static IDisposable Register(Action frameCallback, Func<double> frameBudgetMilliseconds)
    {
        ArgumentNullException.ThrowIfNull(frameBudgetMilliseconds);
        return RegisterCore(frameCallback, false, new FrameTarget(IntPtr.Zero, null, false, frameBudgetMilliseconds));
    }

    public static bool HasBoundsTransitionCapacity =>
        WidgetCompactAnimationConcurrencyPolicy.ShouldAnimate(
            BoundsTransitionRegistrations.Count,
            MaximumConcurrentBoundsTransitions);

    internal static bool HasActiveAnimations => FrameCallbacks.Count > 0;

    internal static long RecentFrameOverrunMask => s_recentOverrunMask;

    public static IDisposable RegisterBoundsTransition(Action frameCallback, IntPtr windowHandle = default)
    {
        if (!HasBoundsTransitionCapacity)
        {
            throw new InvalidOperationException("No compact bounds-transition animation slot is available.");
        }

        return RegisterCore(frameCallback, true, new FrameTarget(windowHandle, null, false));
    }

    /// <summary>
    /// Queues one real HWND bounds update for the current compositor tick. All
    /// concurrent capsule transitions are committed atomically after their
    /// callbacks finish, avoiding N independent DWM commits without changing
    /// the physical-window animation semantics.
    /// </summary>
    public static bool TryQueueBoundsMove(
        IntPtr windowHandle,
        RectInt32 bounds,
        uint flags,
        Action beforeCommit,
        Action<bool> afterCommit,
        Action fallback)
    {
        if (!s_isDispatchingFrame || windowHandle == IntPtr.Zero)
        {
            return false;
        }

        PendingBoundsMoves[windowHandle] = new PendingBoundsMove(
            windowHandle,
            bounds,
            flags,
            beforeCommit,
            afterCommit,
            fallback);
        return true;
    }

    private static IDisposable RegisterCore(Action frameCallback, bool isBoundsTransition, FrameTarget target)
    {
        ArgumentNullException.ThrowIfNull(frameCallback);

        long registrationId = ++s_nextRegistrationId;
        FrameCallbacks.Add(registrationId, frameCallback);
        FrameTargets.Add(registrationId, target);
        s_frameCallbackSnapshotDirty = true;
        if (isBoundsTransition)
        {
            BoundsTransitionRegistrations.Add(registrationId);
        }
        if (!s_isRenderingSubscribed)
        {
            s_isRenderingSubscribed = true;
            s_clockBoostLease = CompositorClockBoostCoordinator.Acquire();
            StartFrameClock();
        }

        return new Registration(registrationId);
    }

    private static void StartFrameClock()
    {
        if (WindowsCompatibilityService.IsWindows11OrLater)
        {
            s_windows10ClockSource = Windows10FrameClockSource.None;
            CompositionTarget.Rendering += OnRendering;
            return;
        }

        DispatcherQueue? dispatcherQueue = DispatcherQueue.GetForCurrentThread();
        if (dispatcherQueue is null)
        {
            s_windows10ClockSource = Windows10FrameClockSource.None;
            CompositionTarget.Rendering += OnRendering;
            return;
        }

        s_windows10FrameDispatcher = dispatcherQueue;
        StartWindows10DwmFlushClock();
    }

    /// <summary>
    /// Uses the application's DWM flush completion as a pacing hint on Win10.
    /// Each run owns its cancellation and queued tick: a stopped run cannot
    /// dispatch into a later interaction. This is not a per-output vblank API.
    /// </summary>
    private static void StartWindows10DwmFlushClock()
    {
        var run = new Windows10ClockRun(s_windows10FrameDispatcher!);
        s_windows10ClockRun = run;
        s_windows10ClockSource = Windows10FrameClockSource.DwmFlushThread;
        RefreshFrameBudgets();
        s_lastFrameTickTimestamp = 0;
        var thread = new Thread(() => Windows10DwmFlushLoop(run))
        {
            IsBackground = true,
            Name = "DeskBoxWin10FramePacer",
            Priority = ThreadPriority.AboveNormal
        };
        thread.Start();
        App.LogVerbose("[AnimationClock] shared source=DwmFlush");
    }

    /// <summary>
    /// Fallback clock tracks the fastest participating display, including mode
    /// changes. DwmFlush is retried on the next active run, not disabled forever.
    /// </summary>
    private static void StartWindows10RefreshTimer(DispatcherQueue dispatcherQueue)
    {
        RefreshFrameBudgets();
        TimeSpan interval = TimeSpan.FromMilliseconds(s_frameTickBudgetMs);
        s_windows10ClockSource = Windows10FrameClockSource.RefreshTimer;
        ResetFrameTickBudget(interval);
        s_windows10FrameTimer = dispatcherQueue.CreateTimer();
        s_windows10FrameTimer.Interval = interval;
        s_windows10FrameTimer.IsRepeating = true;
        s_windows10FrameTimer.Tick += OnWindows10FrameTimerTick;
        s_windows10FrameTimer.Start();
        App.LogVerbose(
            $"[AnimationClock] shared source=DispatcherQueueTimer " +
            $"intervalMs={interval.TotalMilliseconds:F3} refreshHz={1000d / s_frameTickBudgetMs:F3}");
    }

    private static void Windows10DwmFlushLoop(Windows10ClockRun run)
    {
        while (run.IsActive)
        {
            long started = Stopwatch.GetTimestamp();
            if (!Win32Helper.TryDwmFlush())
            {
                SwitchToWindows10TimerFallback(run, "dwmapi-unavailable");
                return;
            }

            double elapsedMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            if (run.Health.RecordWait(elapsedMs, Volatile.Read(ref run.TargetIntervalMilliseconds)))
            {
                // Some mixed-output stacks pace this application to the slower
                // output. Do not let that clock cap a faster participating screen.
                SwitchToWindows10TimerFallback(run, "flush-slower-than-active-display");
                return;
            }
            if (elapsedMs < 0.5)
            {
                if (++run.InstantFlushCount >= 30)
                {
                    SwitchToWindows10TimerFallback(run, "dwmflush-not-pacing");
                    return;
                }
            }
            else
            {
                run.InstantFlushCount = 0;
            }

            if (run.IsActive && Interlocked.CompareExchange(ref run.PendingTick, 1, 0) == 0)
            {
                if (!run.Dispatcher.TryEnqueue(() => DispatchWindows10FlushTick(run)))
                {
                    run.IsActive = false; // Dispatcher shutdown; never enqueue a replacement clock.
                    return;
                }
            }
        }
    }

    private static void DispatchWindows10FlushTick(Windows10ClockRun run)
    {
        Interlocked.Exchange(ref run.PendingTick, 0);
        if (!run.IsActive || !ReferenceEquals(s_windows10ClockRun, run)) return;
        OnRendering(sender: null, args: EventArgs.Empty);
    }

    private static void SwitchToWindows10TimerFallback(Windows10ClockRun run, string reason)
    {
        run.IsActive = false;
        run.Dispatcher.TryEnqueue(() =>
        {
            if (!s_isRenderingSubscribed || !ReferenceEquals(s_windows10ClockRun, run)) return;
            s_windows10ClockRun = null;
            App.LogVerbose($"[AnimationClock] shared DwmFlush unavailable ({reason}); display timer for this run");
            StartWindows10RefreshTimer(run.Dispatcher);
        });
    }

    private static void OnWindows10FrameTimerTick(DispatcherQueueTimer sender, object args)
    {
        if (!ReferenceEquals(sender, s_windows10FrameTimer) || !s_isRenderingSubscribed) return;
        OnRendering(sender, args);
    }

    private static void RefreshFrameBudgets()
    {
        double fastestBudget = double.PositiveInfinity;
        foreach (FrameTarget target in FrameTargets.Values)
        {
            double budget = double.PositiveInfinity;
            if (target.FrameBudget is not null)
            {
                double provided = target.FrameBudget();
                budget = double.IsFinite(provided) && provided > 0 ? provided : 1000d / 60;
            }
            else if (target.WindowHandle != IntPtr.Zero)
                budget = GetFrameBudgetMilliseconds(target.WindowHandle);
            else if (target.Windows is not null)
                foreach (IntPtr window in target.Windows())
                    if (window != IntPtr.Zero) budget = Math.Min(budget, GetFrameBudgetMilliseconds(window));
            target.BudgetMilliseconds = budget;
            fastestBudget = Math.Min(fastestBudget, budget);
        }
        if (!double.IsFinite(fastestBudget)) fastestBudget = GetFrameBudgetMilliseconds(IntPtr.Zero);
        if (s_windows10ClockRun is { } run) Volatile.Write(ref run.TargetIntervalMilliseconds, fastestBudget);
        foreach (FrameTarget target in FrameTargets.Values)
            if (!double.IsFinite(target.BudgetMilliseconds)) target.BudgetMilliseconds = fastestBudget;
        if (Math.Abs(s_frameTickBudgetMs - fastestBudget) > 0.01)
        {
            ResetFrameTickBudget(TimeSpan.FromMilliseconds(fastestBudget));
            s_recentOverrunMask = 0;
        }
        if (s_windows10FrameTimer is not null &&
            Math.Abs(s_windows10FrameTimer.Interval.TotalMilliseconds - fastestBudget) > 0.01)
            s_windows10FrameTimer.Interval = TimeSpan.FromMilliseconds(fastestBudget);
    }

    /// <summary>
    /// Rolls one overrun bit per dispatched frame tick. All frame dispatches
    /// happen on the UI thread, so plain field access is safe.
    /// </summary>
    private static void RecordFrameTickCadence()
    {
        long now = Stopwatch.GetTimestamp();
        if (s_lastFrameTickTimestamp != 0 && s_frameTickBudgetMs > 0)
        {
            double intervalMs = Stopwatch
                .GetElapsedTime(s_lastFrameTickTimestamp, now)
                .TotalMilliseconds;
            if (intervalMs > 0)
            {
                bool overrun = intervalMs > s_frameTickBudgetMs *
                    WidgetCompactFrameSkipPolicy.OverrunBudgetFactor;
                s_recentOverrunMask = (s_recentOverrunMask << 1) | (overrun ? 1L : 0L);
            }
        }

        s_lastFrameTickTimestamp = now;
    }

    private static void ResetFrameTickBudget(TimeSpan expectedInterval)
    {
        s_frameTickBudgetMs = Math.Max(1.0, expectedInterval.TotalMilliseconds);
        s_lastFrameTickTimestamp = 0;
    }

    private static void OnRendering(object? sender, object args)
    {
        if (!s_isRenderingSubscribed || s_isDispatchingFrame) return;
        RefreshFrameBudgets();
        RecordFrameTickCadence();
        PendingBoundsMoves.Clear();
        s_isDispatchingFrame = true;
        try
        {
            // Callbacks may complete and unregister themselves while this snapshot
            // is being dispatched. The registration check avoids invoking an entry
            // that another callback cancelled earlier in the same compositor tick.
            foreach ((long registrationId, Action callback) in GetFrameCallbackSnapshot())
            {
                if (!FrameCallbacks.ContainsKey(registrationId))
                {
                    continue;
                }

                try
                {
                    FrameTarget target = FrameTargets[registrationId];
                    double timestampMs = Stopwatch.GetTimestamp() * 1000d / Stopwatch.Frequency;
                    if (target.PaceToDisplay)
                    {
                        if (!target.Initialized)
                        {
                            target.Pacing.Reset(timestampMs, target.BudgetMilliseconds);
                            target.Initialized = true;
                        }
                        if (!target.Pacing.ShouldSubmit(timestampMs, target.BudgetMilliseconds)) continue;
                        // Direct manipulation follows the latest input, never an artificial
                        // load-dependent lag. Only the target display period limits its work.
                        target.Pacing.RecordSubmission(timestampMs, 0);
                    }
                    callback();
                }
                catch (Exception ex)
                {
                    App.Log($"[CompactAnimationClock] Frame callback failed: {ex.Message}");
                }
            }
        }
        finally
        {
            s_isDispatchingFrame = false;
            FlushPendingBoundsMoves();
        }
    }

    private static KeyValuePair<long, Action>[] GetFrameCallbackSnapshot()
    {
        if (!s_frameCallbackSnapshotDirty)
        {
            return s_frameCallbackSnapshot;
        }

        s_frameCallbackSnapshot = FrameCallbacks.ToArray();
        s_frameCallbackSnapshotDirty = false;
        return s_frameCallbackSnapshot;
    }

    private static void FlushPendingBoundsMoves()
    {
        if (PendingBoundsMoves.Count == 0)
        {
            return;
        }

        PendingBoundsMove[] moves = PendingBoundsMoves.Values.ToArray();
        PendingBoundsMoves.Clear();
        long started = Stopwatch.GetTimestamp();
        var succeeded = new bool[moves.Length];

        try
        {
            foreach (PendingBoundsMove move in moves) move.BeforeCommit();
            bool committed = TryCommitBatch(moves);
            if (committed)
            {
                Array.Fill(succeeded, true);
            }
            else
            {
                for (int i = 0; i < moves.Length; i++)
                {
                    PendingBoundsMove move = moves[i];
                    try
                    {
                        bool moved = Win32Helper.SetWindowPos(
                            move.WindowHandle, IntPtr.Zero, move.Bounds.X, move.Bounds.Y,
                            move.Bounds.Width, move.Bounds.Height, move.Flags);
                        if (!moved) move.Fallback();
                        succeeded[i] = true;
                    }
                    catch (Exception ex) { App.Log($"[CompactBoundsBatch] Window commit failed: {ex.Message}"); }
                }
            }
        }
        finally
        {
            for (int i = 0; i < moves.Length; i++)
            {
                try { moves[i].AfterCommit(succeeded[i]); }
                catch (Exception ex) { App.Log($"[CompactBoundsBatch] Completion failed: {ex.Message}"); }
            }

            double elapsedMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            if (elapsedMs >= 8)
            {
                string details = $"count={moves.Length} elapsedMs={elapsedMs:F1}";
                PerformanceLogger.Mark("CompactBoundsBatch", details);
                App.LogVerbose($"[CompactBoundsBatch] {details}");
            }
        }
    }

    private static bool TryCommitBatch(IReadOnlyList<PendingBoundsMove> moves)
    {
        IntPtr deferred = Win32Helper.BeginDeferWindowPos(moves.Count);
        if (deferred == IntPtr.Zero)
        {
            return false;
        }

        foreach (PendingBoundsMove move in moves)
        {
            IntPtr next = Win32Helper.DeferWindowPos(
                deferred,
                move.WindowHandle,
                IntPtr.Zero,
                move.Bounds.X,
                move.Bounds.Y,
                move.Bounds.Width,
                move.Bounds.Height,
                move.Flags);
            if (next == IntPtr.Zero)
            {
                // A failed DeferWindowPos invalidates the transaction. The
                // caller retries every real bounds update directly.
                return false;
            }

            deferred = next;
        }

        return Win32Helper.EndDeferWindowPos(deferred);
    }

    private static void Unregister(long registrationId)
    {
        if (FrameCallbacks.Remove(registrationId))
        {
            s_frameCallbackSnapshotDirty = true;
        }
        FrameTargets.Remove(registrationId);
        BoundsTransitionRegistrations.Remove(registrationId);
        if (FrameCallbacks.Count != 0 || !s_isRenderingSubscribed)
        {
            return;
        }

        StopFrameClock();
        s_isRenderingSubscribed = false;
        s_frameCallbackSnapshot = [];
        s_frameCallbackSnapshotDirty = false;
        s_clockBoostLease?.Dispose();
        s_clockBoostLease = null;
        WidgetAnimationDisplayTiming.Clear();
        s_lastFrameTickTimestamp = 0;
    }

    private static void StopFrameClock()
    {
        switch (s_windows10ClockSource)
        {
            case Windows10FrameClockSource.DwmFlushThread:
                if (s_windows10ClockRun is { } run) run.IsActive = false;
                s_windows10ClockRun = null;
                s_windows10FrameDispatcher = null;
                break;
            case Windows10FrameClockSource.RefreshTimer:
                StopWindows10RefreshTimer();
                s_windows10FrameDispatcher = null;
                break;
            case Windows10FrameClockSource.None:
                // Covers the Win11/no-dispatcher path and is a no-op when
                // Rendering was never subscribed.
                CompositionTarget.Rendering -= OnRendering;
                break;
        }

        s_windows10ClockSource = Windows10FrameClockSource.None;
    }

    private static void StopWindows10RefreshTimer()
    {
        if (s_windows10FrameTimer is not null)
        {
            s_windows10FrameTimer.Stop();
            s_windows10FrameTimer.Tick -= OnWindows10FrameTimerTick;
            s_windows10FrameTimer = null;
        }
    }

    private sealed class Registration(long registrationId) : IDisposable
    {
        private long _registrationId = registrationId;

        public void Dispose()
        {
            long id = Interlocked.Exchange(ref _registrationId, 0);
            if (id != 0)
            {
                Unregister(id);
            }
        }
    }
}

internal static class WidgetCompactAnimationConcurrencyPolicy
{
    public static bool ShouldAnimate(int activeTransitions, int maximumConcurrentTransitions)
    {
        return maximumConcurrentTransitions > 0 &&
            activeTransitions >= 0 &&
            activeTransitions < maximumConcurrentTransitions;
    }
}
