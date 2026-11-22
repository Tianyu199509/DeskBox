namespace DeskBox.Services;

internal readonly record struct PendingStackPopoverNativeDrop(
    long GestureId,
    long EpochAtDrop,
    bool IsDeskBoxOwnedSource,
    string MatchedSessionId,
    string MatchedWidgetId,
    bool FromStackPopover,
    ActiveDeskBoxDragRegistry.MatchedDragEntry? CapturedSelfDrag = null)
{
    public static PendingStackPopoverNativeDrop Unclaimed =>
        new(
            GestureId: 0,
            EpochAtDrop: -1,
            IsDeskBoxOwnedSource: false,
            MatchedSessionId: string.Empty,
            MatchedWidgetId: string.Empty,
            FromStackPopover: false,
            CapturedSelfDrag: null);
}

/// <summary>
/// Two-phase claim coordinator for inbound drops onto <c>StackPopoverHostWindow</c>.
/// Separates synchronous <c>IDropTarget::Drop</c> receipt (<see cref="TryBeginNativeDropIntent"/>)
/// from deferred UI-thread execution (<see cref="TryClaimDeferredNativeDrop"/>) so an
/// external Explorer/desktop drop never self-cancels its own queued import while an
/// in-process XAML <c>Drop</c> that lands in between still wins deterministically —
/// even if <c>Items_DropCompleted</c> clears <see cref="ActiveDeskBoxDragRegistry"/>
/// before the low-priority native fallback runs.
/// </summary>
internal sealed class StackPopoverDropClaimTracker
{
    private const long LegacyDropFilesDeduplicationWindowMs = 250;

    private readonly object _gate = new();
    private long _inboundEpoch;
    private long _lastXamlClaimedEpoch = -1;
    private string? _lastXamlClaimedSessionId;
    private string? _lastObservedXamlSessionId;
    private long _lastNativeClaimedGestureId;
    private long _lastNativeCompletedTick64;
    private string? _lastNativeFirstPath;
    private int _pendingDropCount;

    public int PendingDropCount
    {
        get
        {
            lock (_gate)
            {
                return _pendingDropCount;
            }
        }
    }

    public void OnNativeDragEnter()
    {
        lock (_gate)
        {
            _inboundEpoch++;
        }
    }

    public void OnNativeDragLeave()
    {
        lock (_gate)
        {
            _inboundEpoch++;
        }
    }

    public void ObserveXamlDragSession(string? sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return;
        }

        lock (_gate)
        {
            if (!string.Equals(sessionId, _lastObservedXamlSessionId, StringComparison.Ordinal))
            {
                _lastObservedXamlSessionId = sessionId;
                _inboundEpoch++;
            }
        }
    }

    public void MarkXamlDropClaimed(string? sessionId, long maxActiveNativeGestureId)
    {
        lock (_gate)
        {
            _lastXamlClaimedEpoch = _inboundEpoch;
            if (!string.IsNullOrWhiteSpace(sessionId))
            {
                _lastXamlClaimedSessionId = sessionId;
            }

            if (maxActiveNativeGestureId > _lastNativeClaimedGestureId)
            {
                _lastNativeClaimedGestureId = maxActiveNativeGestureId;
            }
        }
    }

    public bool TryBeginNativeDropIntent(
        IReadOnlyList<string> ownedPaths,
        long gestureId,
        long nowTick64,
        out PendingStackPopoverNativeDrop pendingDrop)
    {
        if (ownedPaths.Count == 0)
        {
            pendingDrop = PendingStackPopoverNativeDrop.Unclaimed;
            return false;
        }

        bool isDeskBoxOwnedSource = ActiveDeskBoxDragRegistry.TryMatchDetailed(
            ownedPaths,
            out ActiveDeskBoxDragRegistry.MatchedDragEntry matched);
        string matchedSessionId = isDeskBoxOwnedSource ? matched.SessionId : string.Empty;
        string matchedWidgetId = isDeskBoxOwnedSource ? matched.WidgetId : string.Empty;
        bool fromStackPopover = isDeskBoxOwnedSource && matched.FromStackPopover;

        lock (_gate)
        {
            long capturedEpoch = _inboundEpoch;
            if (IsAlreadyClaimedLocked(gestureId, capturedEpoch, matchedSessionId))
            {
                pendingDrop = PendingStackPopoverNativeDrop.Unclaimed;
                return false;
            }

            _lastNativeCompletedTick64 = nowTick64;
            _lastNativeFirstPath = ownedPaths[0];
            _pendingDropCount++;
            pendingDrop = new PendingStackPopoverNativeDrop(
                gestureId,
                capturedEpoch,
                isDeskBoxOwnedSource,
                matchedSessionId,
                matchedWidgetId,
                fromStackPopover,
                CapturedSelfDrag: isDeskBoxOwnedSource ? matched : null);
            return true;
        }
    }

    public bool TryClaimDeferredNativeDrop(
        PendingStackPopoverNativeDrop pendingDrop,
        IReadOnlyList<string> paths)
    {
        string effectiveSessionId = pendingDrop.MatchedSessionId;
        if (string.IsNullOrWhiteSpace(effectiveSessionId) &&
            paths.Count > 0 &&
            ActiveDeskBoxDragRegistry.TryMatch(
                paths,
                out string liveSessionId,
                out _,
                out _))
        {
            effectiveSessionId = liveSessionId;
        }

        lock (_gate)
        {
            if (pendingDrop.EpochAtDrop >= 0 &&
                IsAlreadyClaimedLocked(
                    pendingDrop.GestureId,
                    pendingDrop.EpochAtDrop,
                    effectiveSessionId))
            {
                return false;
            }

            if (pendingDrop.GestureId > _lastNativeClaimedGestureId)
            {
                _lastNativeClaimedGestureId = pendingDrop.GestureId;
            }

            _inboundEpoch++;
            return true;
        }
    }

    public void CompletePendingNativeDrop()
    {
        lock (_gate)
        {
            if (_pendingDropCount > 0)
            {
                _pendingDropCount--;
            }
        }
    }

    public bool ShouldSkipLegacyDropFiles(IReadOnlyList<string> paths, long nowTick64)
    {
        if (paths.Count == 0)
        {
            return true;
        }

        lock (_gate)
        {
            if (string.IsNullOrWhiteSpace(_lastNativeFirstPath) ||
                nowTick64 - _lastNativeCompletedTick64 >= LegacyDropFilesDeduplicationWindowMs)
            {
                return false;
            }

            if (string.Equals(paths[0], _lastNativeFirstPath, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            // Transient archive/temp drops get staged under VirtualDrops/DroppedFiles
            // on the OLE path while WM_DROPFILES delivers the original %TEMP% path.
            if (Platform.DropStagingFileOps.IsAnyDeskBoxStagingPath(_lastNativeFirstPath) &&
                string.Equals(
                    Path.GetFileName(paths[0]),
                    Path.GetFileName(_lastNativeFirstPath),
                    StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return false;
        }
    }

    private bool IsAlreadyClaimedLocked(
        long gestureId,
        long epochAtDrop,
        string? matchedSessionId)
    {
        if (gestureId > 0 && gestureId <= _lastNativeClaimedGestureId)
        {
            return true;
        }

        if (epochAtDrop >= 0 && _lastXamlClaimedEpoch == epochAtDrop)
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(matchedSessionId) &&
            string.Equals(
                matchedSessionId,
                _lastXamlClaimedSessionId,
                StringComparison.Ordinal))
        {
            return true;
        }

        return false;
    }
}
