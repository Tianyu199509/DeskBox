namespace DeskBox.Models;

/// <summary>
/// Keeps native tab selection stable while the host asynchronously commits
/// content. Unrelated presentation refreshes must not select the old tab again.
/// </summary>
public sealed class WidgetGroupTabSelectionState
{
    private string? _groupId;

    public string? PendingMemberId { get; private set; }
    public long RequestVersion { get; private set; }

    public long Request(string groupId, string memberId)
    {
        _groupId = groupId;
        PendingMemberId = memberId;
        return ++RequestVersion;
    }

    public string ResolveSelection(
        string groupId,
        string activeMemberId,
        IReadOnlySet<string> memberIds)
    {
        if (_groupId != groupId || PendingMemberId is not { } pending ||
            !memberIds.Contains(pending) || pending == activeMemberId)
        {
            Reset();
        }

        return PendingMemberId ?? activeMemberId;
    }

    public bool Complete(long requestVersion, string memberId, bool succeeded, string? activeMemberId)
    {
        if (requestVersion != RequestVersion || PendingMemberId != memberId ||
            (succeeded && activeMemberId != memberId))
        {
            // A newer request wins. A coalesced duplicate can also report
            // success before the original request commits its presentation.
            return false;
        }

        Reset();
        return true;
    }

    public void Reset()
    {
        _groupId = null;
        PendingMemberId = null;
    }
}
