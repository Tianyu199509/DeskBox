// Copyright (c) DeskBox. All rights reserved.

using System.Diagnostics;
using DeskBox.Helpers;
using DeskBox.Models;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;

namespace DeskBox.Services;

/// <summary>
/// Shares one compositor-paced Rendering subscription across every capsule
/// transition. Rendering follows the active display/DRR cadence; elapsed time,
/// rather than an assumed frame rate, remains the source of animation progress.
/// </summary>
internal static class WidgetCompactAnimationCoordinator
{
    // A native bounds/clip transition still has one UI-thread coordinator, but
    // multiple capsules may animate concurrently (e.g. one collapsing while the
    // cursor expands the next). Allowing several in-flight transitions avoids
    // dropping a capsule's animation when the slot is occupied. First-frame
    // commit pressure is absorbed by the expansion warm-up instead of by
    // serializing transitions.
    internal const int MaximumConcurrentBoundsTransitions = 4;

    private static readonly Dictionary<long, Action> FrameCallbacks = [];
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
    private static Thread? s_windows10DwmFlushThread;
    private static volatile bool s_windows10DwmFlushThreadRunning;
    private static int s_windows10PendingFlushTick;
    private static int s_windows10InstantFlushCount;
    private static bool s_windows10UseTimerFallback;

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

    private readonly record struct PendingBoundsMove(
        IntPtr WindowHandle,
        RectInt32 Bounds,
        uint Flags,
        Action BeforeCommit,
        Action AfterCommit,
        Action Fallback);

    public static IDisposable Register(Action frameCallback)
    {
        return RegisterCore(frameCallback, isBoundsTransition: false);
    }

    public static bool HasBoundsTransitionCapacity =>
        WidgetCompactAnimationConcurrencyPolicy.ShouldAnimate(
            BoundsTransitionRegistrations.Count,
            MaximumConcurrentBoundsTransitions);

    internal static bool HasActiveAnimations => FrameCallbacks.Count > 0;

    internal static long RecentFrameOverrunMask => s_recentOverrunMask;

    public static IDisposable RegisterBoundsTransition(Action frameCallback)
    {
        if (!HasBoundsTransitionCapacity)
        {
            throw new InvalidOperationException("No compact bounds-transition animation slot is available.");
        }

        return RegisterCore(frameCallback, isBoundsTransition: true);
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
        Action afterCommit,
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

    private static IDisposable RegisterCore(Action frameCallback, bool isBoundsTransition)
    {
        ArgumentNullException.ThrowIfNull(frameCallback);

        long registrationId = ++s_nextRegistrationId;
        FrameCallbacks.Add(registrationId, frameCallback);
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
        if (s_windows10UseTimerFallback)
        {
            StartWindows10RefreshTimer(dispatcherQueue);
        }
        else
        {
            StartWindows10DwmFlushClock();
        }
    }

    /// <summary>
    /// Preferred Win10 clock: a background thread blocks on DwmFlush, which
    /// returns after every DWM composition pass, and enqueues one coalesced
    /// tick per pass. Commits therefore follow the display's native present
    /// cadence (120/144/165/240Hz alike) with no fixed-interval beat against
    /// the compositor.
    /// </summary>
    private static void StartWindows10DwmFlushClock()
    {
        s_windows10PendingFlushTick = 0;
        s_windows10InstantFlushCount = 0;
        s_windows10DwmFlushThreadRunning = true;
        s_windows10ClockSource = Windows10FrameClockSource.DwmFlushThread;
        ResetFrameTickBudget(
            WidgetDisplayRefreshRatePolicy.ResolveFrameTickInterval(
                Win32Helper.GetPrimaryDisplayRefreshRate()));
        s_windows10DwmFlushThread = new Thread(Windows10DwmFlushLoop)
        {
            IsBackground = true,
            Name = "DeskBoxWin10FramePacer",
            Priority = ThreadPriority.AboveNormal
        };
        s_windows10DwmFlushThread.Start();
        App.LogVerbose("[AnimationClock] compact source=DwmFlush present-aligned");
    }

    /// <summary>
    /// Fallback Win10 clock: a repeating timer derived from the primary
    /// display's refresh rate. Used when DwmFlush cannot actually pace the
    /// composition (or dwmapi is unavailable) for the rest of the session.
    /// </summary>
    private static void StartWindows10RefreshTimer(DispatcherQueue dispatcherQueue)
    {
        int refreshRateHz = Win32Helper.GetPrimaryDisplayRefreshRate();
        TimeSpan interval = WidgetDisplayRefreshRatePolicy.ResolveFrameTickInterval(refreshRateHz);
        s_windows10ClockSource = Windows10FrameClockSource.RefreshTimer;
        ResetFrameTickBudget(interval);
        s_windows10FrameTimer = dispatcherQueue.CreateTimer();
        s_windows10FrameTimer.Interval = interval;
        s_windows10FrameTimer.IsRepeating = true;
        s_windows10FrameTimer.Tick += OnWindows10FrameTimerTick;
        s_windows10FrameTimer.Start();
        App.LogVerbose(
            $"[AnimationClock] compact source=DispatcherQueueTimer " +
            $"intervalMs={interval.TotalMilliseconds:F1} refreshHz={refreshRateHz}");
    }

    private static void Windows10DwmFlushLoop()
    {
        // Each DwmFlush returns after one DWM composition pass, so enqueueing
        // one coalesced tick per pass paces commits to the display's native
        // cadence. If dwmapi fails or returns instantly (some remote/composited
        // sessions), switch permanently to the refresh-derived timer instead
        // of busy-spinning.
        while (s_windows10DwmFlushThreadRunning)
        {
            long started = Stopwatch.GetTimestamp();
            if (!Win32Helper.TryDwmFlush())
            {
                SwitchToWindows10TimerFallback("dwmapi-unavailable");
                return;
            }

            double elapsedMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            if (elapsedMs < 0.5)
            {
                if (Interlocked.Increment(ref s_windows10InstantFlushCount) >= 30)
                {
                    SwitchToWindows10TimerFallback("dwmflush-not-pacing");
                    return;
                }
            }
            else
            {
                Interlocked.Exchange(ref s_windows10InstantFlushCount, 0);
            }

            if (Interlocked.CompareExchange(ref s_windows10PendingFlushTick, 1, 0) == 0)
            {
                DispatcherQueue? dispatcherQueue = s_windows10FrameDispatcher;
                if (dispatcherQueue is null ||
                    !dispatcherQueue.TryEnqueue(DispatchWindows10FlushTick))
                {
                    SwitchToWindows10TimerFallback("enqueue-failed");
                    return;
                }
            }
        }
    }

    private static void DispatchWindows10FlushTick()
    {
        Interlocked.Exchange(ref s_windows10PendingFlushTick, 0);
        OnRendering(sender: null, args: EventArgs.Empty);
    }

    private static void SwitchToWindows10TimerFallback(string reason)
    {
        // Runs on the flush thread. The loop exits through the caller's return
        // and the actual timer must start on the UI thread (DispatcherQueueTimer
        // creation is thread-affine), so marshal it and re-check that
        // registrations are still active once it runs.
        DispatcherQueue? dispatcherQueue = s_windows10FrameDispatcher;
        s_windows10DwmFlushThreadRunning = false;
        s_windows10UseTimerFallback = true;
        s_windows10ClockSource = Windows10FrameClockSource.None;
        App.Log(
            $"[AnimationClock] compact DwmFlush pacing unavailable ({reason}); " +
            "using refresh-derived timer for this session");
        dispatcherQueue?.TryEnqueue(() =>
        {
            if (!s_isRenderingSubscribed ||
                s_windows10ClockSource is not Windows10FrameClockSource.None)
            {
                return;
            }

            StartWindows10RefreshTimer(dispatcherQueue);
        });
    }

    private static void OnWindows10FrameTimerTick(DispatcherQueueTimer sender, object args)
    {
        OnRendering(sender, args);
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

        foreach (PendingBoundsMove move in moves)
        {
            move.BeforeCommit();
        }

        try
        {
            bool committed = TryCommitBatch(moves);
            if (!committed)
            {
                foreach (PendingBoundsMove move in moves)
                {
                    bool moved = Win32Helper.SetWindowPos(
                        move.WindowHandle,
                        IntPtr.Zero,
                        move.Bounds.X,
                        move.Bounds.Y,
                        move.Bounds.Width,
                        move.Bounds.Height,
                        move.Flags);
                    if (!moved)
                    {
                        move.Fallback();
                    }
                }
            }
        }
        finally
        {
            foreach (PendingBoundsMove move in moves)
            {
                move.AfterCommit();
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
    }

    private static void StopFrameClock()
    {
        switch (s_windows10ClockSource)
        {
            case Windows10FrameClockSource.DwmFlushThread:
                s_windows10DwmFlushThreadRunning = false;
                s_windows10DwmFlushThread = null;
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
