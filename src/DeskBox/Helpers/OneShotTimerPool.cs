namespace DeskBox.Helpers;

/// <summary>
/// Bounded per-owner pool for reusable one-shot UI timers (such as WinRT
/// <c>DispatcherQueueTimer</c>). WinRT timers do not implement <see cref="IDisposable"/>,
/// so calling <c>CreateTimer()</c> on transient hover/hibernation re-arms leaks two NT
/// kernel handles (<c>Timer</c> + <c>WaitCompletionPacket</c>) until finalization.
/// Renting and returning stopped, handler-detached timer instances keeps handle churn at 0.
/// </summary>
internal sealed class OneShotTimerPool<TTimer>
    where TTimer : class
{
    private readonly Func<TTimer> _createTimer;
    private readonly Action<TTimer> _releaseTimer;
    private readonly Stack<TTimer> _available;
    private readonly int _maxRetained;
    private bool _isDrained;

    public OneShotTimerPool(
        Func<TTimer> createTimer,
        Action<TTimer> releaseTimer,
        int maxRetained = 12)
    {
        ArgumentNullException.ThrowIfNull(createTimer);
        ArgumentNullException.ThrowIfNull(releaseTimer);
        ArgumentOutOfRangeException.ThrowIfNegative(maxRetained);

        _createTimer = createTimer;
        _releaseTimer = releaseTimer;
        _maxRetained = maxRetained;
        _available = new Stack<TTimer>(Math.Min(maxRetained, 16));
    }

    public int RetainedCount => _available.Count;

    public int TotalCreatedCount { get; private set; }

    public int TotalReleasedCount { get; private set; }

    public TTimer Rent()
    {
        _isDrained = false;
        if (_available.TryPop(out TTimer? existing))
        {
            return existing;
        }

        TotalCreatedCount++;
        return _createTimer();
    }

    public void Return(TTimer timer, bool forceRelease = false)
    {
        ArgumentNullException.ThrowIfNull(timer);

        if (forceRelease || _isDrained || _available.Count >= _maxRetained)
        {
            TotalReleasedCount++;
            _releaseTimer(timer);
            return;
        }

        _available.Push(timer);
    }

    public void DrainAll()
    {
        _isDrained = true;
        while (_available.TryPop(out TTimer? timer))
        {
            TotalReleasedCount++;
            _releaseTimer(timer);
        }
    }
}
