namespace DeskBox.Helpers;

/// <summary>
/// Liveness watchdog for one synchronous STA Shell operation that may never
/// return (feedback 455: an IFileOperation transfer started and never came
/// back; the caller's await hung forever and only killing the process ended
/// the pending import).
///
/// IFileOperation exposes no cross-thread Abort, and COM STA affinity makes
/// aborting the owning thread unsafe: <see cref="System.Threading.Thread.Abort"/>
/// cannot be delivered while the thread is inside the native call. The only
/// sanctioned move is to stop waiting — declare the operation dead once it
/// has shown no Shell progress for the whole inactivity window, abandon the
/// thread (a counted, logged, bounded leak), and surface a retryable failure.
///
/// Progress is whatever the call site can observe: for shell transfers every
/// <c>IFileOperationProgressSink</c> callback (StartOperations, Pre/Post item,
/// UpdateProgress, FinishOperations) pulses the watchdog, so a legitimate
/// minute-level transfer keeps renewing the deadline and is never abandoned.
/// </summary>
internal sealed class StaOperationWatchdog
{
    private readonly long _inactivityTimeoutMilliseconds;
    private readonly Func<long> _clockMilliseconds;
    private readonly TimeSpan _pollInterval;
    private long _lastActivityMilliseconds;

    /// <param name="inactivityTimeout">
    /// How long without a single <see cref="Pulse"/> the operation may run
    /// before it is declared dead. Tier per call site: interactive shell
    /// transfers use a short budget; headless bulk work keeps a far longer
    /// one.
    /// </param>
    /// <param name="clockMilliseconds">
    /// Monotonic millisecond clock. Injectable so tests drive time without
    /// real waits. Defaults to <see cref="Environment.TickCount64"/>.
    /// </param>
    /// <param name="pollInterval">
    /// How often the runner re-checks liveness. Defaults to a quarter of the
    /// inactivity timeout clamped to [50ms, 500ms].
    /// </param>
    internal StaOperationWatchdog(
        TimeSpan inactivityTimeout,
        Func<long>? clockMilliseconds = null,
        TimeSpan? pollInterval = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(
            inactivityTimeout,
            TimeSpan.Zero);
        _inactivityTimeoutMilliseconds =
            (long)inactivityTimeout.TotalMilliseconds;
        _clockMilliseconds = clockMilliseconds ??
            (static () => Environment.TickCount64);
        if (pollInterval is { } interval)
        {
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(interval, TimeSpan.Zero);
            _pollInterval = interval;
        }
        else
        {
            long derived = Math.Max(50, _inactivityTimeoutMilliseconds / 4);
            _pollInterval = TimeSpan.FromMilliseconds(Math.Min(derived, 500));
        }

        _lastActivityMilliseconds = _clockMilliseconds();
    }

    internal TimeSpan InactivityTimeout =>
        TimeSpan.FromMilliseconds(_inactivityTimeoutMilliseconds);

    internal TimeSpan PollInterval => _pollInterval;

    /// <summary>Current clock reading; exposed for the runner's poll loop.</summary>
    internal long ReadClockMilliseconds() => _clockMilliseconds();

    /// <summary>
    /// Records observable progress. Thread-safe: progress sinks pulse from
    /// the STA worker thread while the runner polls from the awaiting side.
    /// </summary>
    internal void Pulse()
    {
        Interlocked.Exchange(
            ref _lastActivityMilliseconds,
            _clockMilliseconds());
    }

    /// <param name="workerThread">
    /// The STA thread running the operation. A thread that is administratively
    /// suspended shows no progress but is not dead — abandoning it would be
    /// wrong, so suspension suppresses the timeout verdict.
    /// </param>
    internal bool ShouldAbandon(
        long nowMilliseconds,
        Thread? workerThread,
        out TimeSpan idleDuration)
    {
        long lastActivity = Volatile.Read(ref _lastActivityMilliseconds);
        long idleMilliseconds = nowMilliseconds - lastActivity;
        idleDuration = TimeSpan.FromMilliseconds(
            Math.Max(0, idleMilliseconds));
        if (idleMilliseconds < _inactivityTimeoutMilliseconds)
        {
            return false;
        }

        return workerThread is null ||
               (workerThread.ThreadState & ThreadState.Suspended) == 0;
    }
}
