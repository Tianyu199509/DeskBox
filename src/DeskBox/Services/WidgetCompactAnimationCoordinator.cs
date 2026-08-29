// Copyright (c) DeskBox. All rights reserved.

using System.Diagnostics;
using DeskBox.Helpers;
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
    private static DispatcherQueueTimer? s_windows10FrameTimer;

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
            CompositionTarget.Rendering += OnRendering;
            return;
        }

        DispatcherQueue? dispatcherQueue = DispatcherQueue.GetForCurrentThread();
        if (dispatcherQueue is null)
        {
            CompositionTarget.Rendering += OnRendering;
            return;
        }

        s_windows10FrameTimer = dispatcherQueue.CreateTimer();
        s_windows10FrameTimer.Interval = TimeSpan.FromMilliseconds(15);
        s_windows10FrameTimer.IsRepeating = true;
        s_windows10FrameTimer.Tick += OnWindows10FrameTimerTick;
        s_windows10FrameTimer.Start();
        App.LogVerbose("[AnimationClock] compact source=DispatcherQueueTimer intervalMs=15.0");
    }

    private static void OnWindows10FrameTimerTick(DispatcherQueueTimer sender, object args)
    {
        OnRendering(sender, args);
    }

    private static void OnRendering(object? sender, object args)
    {
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

        if (s_windows10FrameTimer is not null)
        {
            s_windows10FrameTimer.Stop();
            s_windows10FrameTimer.Tick -= OnWindows10FrameTimerTick;
            s_windows10FrameTimer = null;
        }
        else
        {
            CompositionTarget.Rendering -= OnRendering;
        }
        s_isRenderingSubscribed = false;
        s_frameCallbackSnapshot = [];
        s_frameCallbackSnapshotDirty = false;
        s_clockBoostLease?.Dispose();
        s_clockBoostLease = null;
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
