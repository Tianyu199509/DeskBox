namespace DeskBox.Services;

/// <summary>
/// Why the display-topology gate is closed (spec 5.6). While closed, the
/// coordinator records the latest signature but never runs the restore action
/// and never spends retry budget.
/// </summary>
public enum DisplayTopologyGateReason
{
    SessionLocked,
    DisplayOff,
    FullscreenApp,
    RemovalGrace,
    StartupSettling,
    UserInteraction
}

/// <summary>
/// Pure state machine for the display-topology application gate (spec 5.6).
/// Multiple reasons can hold simultaneously; the gate opens only when all of
/// them are released. Time is injected so the grace windows are unit-testable
/// without real timers.
/// </summary>
public sealed class DisplayTopologyGate
{
    private readonly Func<DateTimeOffset> _clock;
    private readonly HashSet<DisplayTopologyGateReason> _closedReasons = [];

    private DateTimeOffset? _graceStartedAt;
    // Counting maps, not sets: two degenerate monitors with the same
    // resolution share one geo token, and the grace must track them as two
    // units (a duplicate id can be removed and return independently).
    private readonly Dictionary<string, int> _graceActiveIds = new(StringComparer.OrdinalIgnoreCase);

    public DisplayTopologyGate(Func<DateTimeOffset>? clock = null)
    {
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    public bool IsClosed => _closedReasons.Count > 0;

    public IReadOnlyCollection<DisplayTopologyGateReason> ClosedReasons => _closedReasons;

    public TimeSpan RemovalGraceDuration { get; set; } = TimeSpan.FromSeconds(4);

    public event Action<DisplayTopologyGateReason>? GateClosed;
    public event Action<DisplayTopologyGateReason>? GateOpened;
    public event Action? GraceStarted;
    public event Action<string>? GraceEnded;

    public void Close(DisplayTopologyGateReason reason)
    {
        if (_closedReasons.Add(reason))
        {
            GateClosed?.Invoke(reason);
        }
    }

    public void Open(DisplayTopologyGateReason reason)
    {
        if (_closedReasons.Remove(reason))
        {
            GateOpened?.Invoke(reason);
            if (reason == DisplayTopologyGateReason.RemovalGrace)
            {
                EndGrace("opened");
            }
        }
    }

    /// <summary>
    /// Starts the removal grace: the new display set is a true subset of
    /// <paramref name="activeIds"/> (only removals). Ends early when the
    /// removed displays return, or when the grace expires.
    /// </summary>
    public void StartRemovalGrace(
        IReadOnlyCollection<string> activeIds,
        IReadOnlyCollection<string> currentIds)
    {
        // IReadOnlyCollection.Count over a duplicate-bearing sequence is the
        // total unit count, which is exactly the comparison wanted here.
        if (currentIds.Count >= activeIds.Count)
        {
            return;
        }

        _graceActiveIds.Clear();
        foreach (string id in activeIds)
        {
            _graceActiveIds[id] = _graceActiveIds.GetValueOrDefault(id) + 1;
        }

        _graceStartedAt = _clock();
        Close(DisplayTopologyGateReason.RemovalGrace);
        GraceStarted?.Invoke();
    }

    /// <summary>
    /// Observes the current display set while a grace is running: every
    /// removed unit returning (duplicates counted) cancels the grace
    /// outright (nothing to apply); otherwise the gate keeps waiting until
    /// the grace duration elapses.
    /// </summary>
    public void ObserveDisplays(IReadOnlyCollection<string> currentIds)
    {
        if (_graceStartedAt is null)
        {
            return;
        }

        Dictionary<string, int> currentCounts = new(StringComparer.OrdinalIgnoreCase);
        foreach (string id in currentIds)
        {
            currentCounts[id] = currentCounts.GetValueOrDefault(id) + 1;
        }

        // Counting-superset rule: the grace only cancels when every active
        // unit is back. Accepted trade versus the old set rule: a mixed swap
        // (one of two identical degenerate monitors leaves while a different
        // display arrives) reads as "not fully returned" and simply waits out
        // the bounded grace window instead of cancelling early.
        bool allReturned = true;
        foreach ((string id, int activeCount) in _graceActiveIds)
        {
            if (currentCounts.GetValueOrDefault(id) < activeCount)
            {
                allReturned = false;
                break;
            }
        }

        if (allReturned)
        {
            Open(DisplayTopologyGateReason.RemovalGrace);
            return;
        }

        if (_clock() - _graceStartedAt >= RemovalGraceDuration)
        {
            Open(DisplayTopologyGateReason.RemovalGrace);
        }
    }

    /// <summary>
    /// Ends the grace immediately (user reveal / drag / settings-page move).
    /// </summary>
    public void EndGraceByUserAction() => Open(DisplayTopologyGateReason.RemovalGrace);

    private void EndGrace(string reason)
    {
        if (_graceStartedAt is not null)
        {
            _graceStartedAt = null;
            GraceEnded?.Invoke(reason);
        }
    }
}
