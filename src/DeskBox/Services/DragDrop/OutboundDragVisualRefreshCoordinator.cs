using DeskBox.Platform;

namespace DeskBox.Services;

internal enum OutboundDragVisualRefreshAction
{
    ContinueWatching = 0,
    MoveCursor = 1,
    StopWatching = 2,
}

internal readonly record struct OutboundDragVisualRefreshDecision(
    OutboundDragVisualRefreshAction Action,
    Win32Helper.POINT TargetPoint = default,
    int PulseNumber = 0,
    bool IsReturnStep = false,
    bool CompletesWarmup = false);

/// <summary>
/// Coordinates a process-once micro-refresh for the first outbound WinUI
/// <c>CanDrag</c> + <c>DragUI.SetContentFromDataPackage()</c> drag session on
/// Windows 10 and Windows 11.
/// <para>
/// On the first drag after process startup, <c>DataExchange.dll</c> initializes
/// its cross-process DirectComposition <c>DragVisual</c> and populates
/// <c>UsingDefaultDragImage</c> / <c>IsShowingText</c> asynchronously while the
/// pointer is already entering the Desktop (<c>CDefView::DragEnter</c>). If the
/// pointer stops moving before that cold-start completes, <c>ole32!DoDragDrop</c>
/// sits idle with stale initial visual feedback ("复制") even though the
/// negotiated OLE drop operation is <c>Move</c>. Emitting a zero-net-displacement
/// 1px step-and-return once the pointer has settled over the external target
/// triggers <c>DragOver</c> + <c>GiveFeedback</c> without requiring a manual
/// mouse wiggle.
/// </para>
/// </summary>
internal sealed class OutboundDragVisualRefreshCoordinator
{
    internal const int MinElapsedSinceDragStartMs = 100;
    internal const int SecondaryFollowUpDelayMs = 160;
    internal const int NaturalPostWarmupThresholdMs = 220;
    internal const int MinOutsideConsecutiveTicks = 2;
    internal const int MaxSettleMovementPx = 2;
    internal const int MaxBlockedMoveAttemptsPerSession = 2;

    public static OutboundDragVisualRefreshCoordinator Shared { get; } = new();

    private readonly record struct PendingReturnStep(
        Win32Helper.POINT Origin,
        Win32Helper.POINT NudgedTo,
        int PulseNumber);

    private readonly object _gate = new();
    private long _nextSerial;
    private long _activeSerial;
    private long _startTick64;
    private int _outsideConsecutiveTicks;
    private bool _hasLastOutsidePoint;
    private Win32Helper.POINT _lastOutsidePoint;
    private int _pulsesCompleted;
    private Win32Helper.POINT _lastPulseOrigin;
    private long _lastPulseCompletedTick64;
    private PendingReturnStep? _pendingReturn;
    private int _blockedAttempts;
    private int _postWarmupNaturalMoveTicks;
    private int _warmupCompleted;

    internal bool IsWarmupCompleted => Volatile.Read(ref _warmupCompleted) != 0;

    internal bool TryBeginDragSession(long nowTick64, out long sessionSerial)
    {
        if (IsWarmupCompleted)
        {
            sessionSerial = 0;
            return false;
        }

        lock (_gate)
        {
            if (_warmupCompleted != 0)
            {
                sessionSerial = 0;
                return false;
            }

            // If an earlier session already completed at least one external refresh
            // pulse before overlapping cleanup, latch warmup now.
            if (_activeSerial != 0 && (_pulsesCompleted >= 1 || _postWarmupNaturalMoveTicks >= 3))
            {
                Volatile.Write(ref _warmupCompleted, 1);
                ResetSessionStateUnderLock();
                sessionSerial = 0;
                return false;
            }

            sessionSerial = ++_nextSerial;
            _activeSerial = sessionSerial;
            _startTick64 = nowTick64;
            _outsideConsecutiveTicks = 0;
            _hasLastOutsidePoint = false;
            _lastOutsidePoint = default;
            _pulsesCompleted = 0;
            _lastPulseOrigin = default;
            _lastPulseCompletedTick64 = 0;
            _pendingReturn = null;
            _blockedAttempts = 0;
            _postWarmupNaturalMoveTicks = 0;
            return true;
        }
    }

    internal OutboundDragVisualRefreshDecision EvaluateTick(
        long sessionSerial,
        long nowTick64,
        in OutboundCursorEnvironmentSnapshot env)
    {
        if (sessionSerial <= 0 || IsWarmupCompleted)
        {
            return new OutboundDragVisualRefreshDecision(OutboundDragVisualRefreshAction.StopWatching);
        }

        lock (_gate)
        {
            if (_warmupCompleted != 0 || _activeSerial != sessionSerial)
            {
                return new OutboundDragVisualRefreshDecision(OutboundDragVisualRefreshAction.StopWatching);
            }

            if (!env.MouseButtonDown)
            {
                FinalizeSessionUnderLock();
                return new OutboundDragVisualRefreshDecision(OutboundDragVisualRefreshAction.StopWatching);
            }

            // Step 2 of an in-flight 1px step-and-return pulse: restore the exact
            // starting pixel if the user's hand remained stationary, or yield cleanly
            // if the user moved the pointer themselves during the interval.
            if (_pendingReturn is { } pending)
            {
                _pendingReturn = null;
                _pulsesCompleted = pending.PulseNumber;
                _lastPulseOrigin = pending.Origin;
                _lastPulseCompletedTick64 = nowTick64;
                _lastOutsidePoint = pending.Origin;
                _hasLastOutsidePoint = true;

                bool completesAllPulses = _pulsesCompleted >= 2;
                if (completesAllPulses)
                {
                    Volatile.Write(ref _warmupCompleted, 1);
                    ResetSessionStateUnderLock();
                }

                bool cursorUntouchedByUser =
                    env.CursorPoint.X == pending.NudgedTo.X &&
                    env.CursorPoint.Y == pending.NudgedTo.Y;
                if (cursorUntouchedByUser)
                {
                    return new OutboundDragVisualRefreshDecision(
                        OutboundDragVisualRefreshAction.MoveCursor,
                        TargetPoint: pending.Origin,
                        PulseNumber: pending.PulseNumber,
                        IsReturnStep: true,
                        CompletesWarmup: completesAllPulses);
                }

                return new OutboundDragVisualRefreshDecision(
                    completesAllPulses
                        ? OutboundDragVisualRefreshAction.StopWatching
                        : OutboundDragVisualRefreshAction.ContinueWatching,
                    CompletesWarmup: completesAllPulses);
            }

            if (!env.IsOutsideEligibleTarget)
            {
                _outsideConsecutiveTicks = 0;
                _hasLastOutsidePoint = false;
                return new OutboundDragVisualRefreshDecision(OutboundDragVisualRefreshAction.ContinueWatching);
            }

            _outsideConsecutiveTicks++;
            long elapsedSinceStartMs = Math.Max(0, nowTick64 - _startTick64);

            bool isSettled = false;
            if (_hasLastOutsidePoint)
            {
                int dx = Math.Abs(env.CursorPoint.X - _lastOutsidePoint.X);
                int dy = Math.Abs(env.CursorPoint.Y - _lastOutsidePoint.Y);
                isSettled = dx <= MaxSettleMovementPx && dy <= MaxSettleMovementPx;

                if (!isSettled && elapsedSinceStartMs >= NaturalPostWarmupThresholdMs)
                {
                    _postWarmupNaturalMoveTicks++;
                }
            }

            _lastOutsidePoint = env.CursorPoint;
            _hasLastOutsidePoint = true;

            if (_pulsesCompleted == 1)
            {
                bool movedSinceFirstPulse =
                    env.CursorPoint.X != _lastPulseOrigin.X ||
                    env.CursorPoint.Y != _lastPulseOrigin.Y;
                if (movedSinceFirstPulse)
                {
                    if (elapsedSinceStartMs >= NaturalPostWarmupThresholdMs)
                    {
                        // Live physical movement after cold-start warmup already drove a warm
                        // DragOver + GiveFeedback pass on the external target.
                        Volatile.Write(ref _warmupCompleted, 1);
                        ResetSessionStateUnderLock();
                        return new OutboundDragVisualRefreshDecision(
                            OutboundDragVisualRefreshAction.StopWatching,
                            CompletesWarmup: true);
                    }

                    _lastPulseOrigin = env.CursorPoint;
                }

                long sinceFirstPulseMs = Math.Max(0, nowTick64 - _lastPulseCompletedTick64);
                if (sinceFirstPulseMs < SecondaryFollowUpDelayMs || !isSettled)
                {
                    return new OutboundDragVisualRefreshDecision(OutboundDragVisualRefreshAction.ContinueWatching);
                }

                if (env.SafeNudgeTarget is not { } secondTarget)
                {
                    Volatile.Write(ref _warmupCompleted, 1);
                    ResetSessionStateUnderLock();
                    return new OutboundDragVisualRefreshDecision(
                        OutboundDragVisualRefreshAction.StopWatching,
                        CompletesWarmup: true);
                }

                _pendingReturn = new PendingReturnStep(env.CursorPoint, secondTarget, PulseNumber: 2);
                return new OutboundDragVisualRefreshDecision(
                    OutboundDragVisualRefreshAction.MoveCursor,
                    TargetPoint: secondTarget,
                    PulseNumber: 2,
                    IsReturnStep: false,
                    CompletesWarmup: false);
            }

            // Continuous live cursor motion well after cold-start already refreshed
            // the target naturally; stop watching once enough warm moves occurred.
            if (_postWarmupNaturalMoveTicks >= 4)
            {
                Volatile.Write(ref _warmupCompleted, 1);
                ResetSessionStateUnderLock();
                return new OutboundDragVisualRefreshDecision(
                    OutboundDragVisualRefreshAction.StopWatching,
                    CompletesWarmup: true);
            }

            if (_outsideConsecutiveTicks < MinOutsideConsecutiveTicks ||
                elapsedSinceStartMs < MinElapsedSinceDragStartMs ||
                !isSettled)
            {
                return new OutboundDragVisualRefreshDecision(OutboundDragVisualRefreshAction.ContinueWatching);
            }

            if (env.SafeNudgeTarget is not { } firstTarget)
            {
                return new OutboundDragVisualRefreshDecision(OutboundDragVisualRefreshAction.ContinueWatching);
            }

            _pendingReturn = new PendingReturnStep(env.CursorPoint, firstTarget, PulseNumber: 1);
            return new OutboundDragVisualRefreshDecision(
                OutboundDragVisualRefreshAction.MoveCursor,
                TargetPoint: firstTarget,
                PulseNumber: 1,
                IsReturnStep: false,
                CompletesWarmup: false);
        }
    }

    internal OutboundDragVisualRefreshDecision NotifyMoveFailed(long sessionSerial)
    {
        lock (_gate)
        {
            if (_activeSerial != sessionSerial)
            {
                return new OutboundDragVisualRefreshDecision(OutboundDragVisualRefreshAction.StopWatching);
            }

            // Roll back any pending return step so Step 2 never snaps back a Step 1
            // move that the OS rejected (e.g. UIPI when an elevated foreign window
            // holds foreground focus).
            _pendingReturn = null;
            if (++_blockedAttempts >= MaxBlockedMoveAttemptsPerSession)
            {
                ResetSessionStateUnderLock();
                return new OutboundDragVisualRefreshDecision(OutboundDragVisualRefreshAction.StopWatching);
            }

            return new OutboundDragVisualRefreshDecision(OutboundDragVisualRefreshAction.ContinueWatching);
        }
    }

    internal void EndDragSession(long sessionSerial)
    {
        if (sessionSerial <= 0)
        {
            return;
        }

        lock (_gate)
        {
            if (_activeSerial != sessionSerial)
            {
                return;
            }

            FinalizeSessionUnderLock();
        }
    }

    private void FinalizeSessionUnderLock()
    {
        if (_pulsesCompleted >= 1 || _postWarmupNaturalMoveTicks >= 3)
        {
            Volatile.Write(ref _warmupCompleted, 1);
        }

        ResetSessionStateUnderLock();
    }

    private void ResetSessionStateUnderLock()
    {
        _activeSerial = 0;
        _startTick64 = 0;
        _outsideConsecutiveTicks = 0;
        _hasLastOutsidePoint = false;
        _lastOutsidePoint = default;
        _pulsesCompleted = 0;
        _lastPulseOrigin = default;
        _lastPulseCompletedTick64 = 0;
        _pendingReturn = null;
        _blockedAttempts = 0;
        _postWarmupNaturalMoveTicks = 0;
    }
}
