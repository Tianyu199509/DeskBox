namespace DeskBox.Models;

/// <summary>
/// Tracks the optimistic wheel-switch target across the long member-switch
/// transaction ("prepare, persist, transition") together with an interruption
/// epoch. Stowing a group hides the window under the pointer without any
/// PointerExited, so an interrupt notification is the only reliable way to
/// drop the cursor; the epoch guarantees a cursor that predates the latest
/// interruption can never reconcile back into a live gesture after the
/// surface is revealed again (feedback 387: one extra page flip).
/// </summary>
public sealed class WidgetGroupWheelSwitchCursor
{
    private string? _pendingTargetId;
    private long _pendingEpoch;
    private long _interruptionEpoch;

    /// <summary>Optimistic target of the in-flight wheel switch, if any.</summary>
    public string? PendingTargetId => _pendingTargetId;

    /// <summary>
    /// True when the pending cursor was recorded before the most recent
    /// interruption (stow/remove/dissolve). Such a cursor is stale: the
    /// transaction it belonged to was interrupted, so it must be discarded
    /// instead of reconciled.
    /// </summary>
    public bool WasInterruptedSinceSet =>
        _pendingTargetId is not null && _pendingEpoch != _interruptionEpoch;

    /// <summary>Records the optimistic target for a newly dispatched wheel step.</summary>
    public void SetPending(string targetId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetId);
        _pendingTargetId = targetId;
        _pendingEpoch = _interruptionEpoch;
    }

    /// <summary>Drops the optimistic cursor without touching the epoch.</summary>
    public void Clear()
    {
        _pendingTargetId = null;
    }

    /// <summary>
    /// The group surface was interrupted (stowed, member removed, dissolved).
    /// Every optimistic gesture state, including this cursor, is void from
    /// this point on.
    /// </summary>
    public void NotifyInterrupted()
    {
        _interruptionEpoch++;
        _pendingTargetId = null;
    }

    /// <summary>
    /// Mirrors the completion-report policy of the title switcher: a failed
    /// invocation, or a successful one whose committed presentation already
    /// shows the target, releases the cursor; a coalesced duplicate that
    /// reports success while the committed presentation still lags keeps it.
    /// Returns true when the cursor must be released.
    /// </summary>
    public bool ShouldReleaseOnCompletion(
        string widgetId,
        bool succeeded,
        string? presentationActiveMemberId)
    {
        if (!string.Equals(_pendingTargetId, widgetId, StringComparison.Ordinal))
        {
            return false;
        }

        if (succeeded &&
            !string.Equals(
                presentationActiveMemberId,
                widgetId,
                StringComparison.Ordinal))
        {
            // A coalesced duplicate reports success while the original
            // request is still preparing. Keep its optimistic cursor until
            // the committed presentation catches up.
            return false;
        }

        return true;
    }

    /// <summary>
    /// Reconciles against a committed presentation. The cursor is discarded
    /// when the presentation already shows it, or when it predates the latest
    /// interruption (a reveal must never replay a pre-stow cursor). A
    /// non-matching, post-interruption cursor is kept: it still represents
    /// the in-flight switch.
    /// </summary>
    /// <returns>True when the cursor was discarded.</returns>
    public bool Reconcile(string? activeMemberId)
    {
        if (_pendingTargetId is null)
        {
            return false;
        }

        if (WasInterruptedSinceSet ||
            string.Equals(
                activeMemberId,
                _pendingTargetId,
                StringComparison.Ordinal))
        {
            _pendingTargetId = null;
            return true;
        }

        return false;
    }
}
