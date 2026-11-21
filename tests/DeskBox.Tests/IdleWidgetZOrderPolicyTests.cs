using DeskBox.Services;

namespace DeskBox.Tests;

public sealed class IdleWidgetZOrderPolicyTests
{
    [Fact]
    public void OrderHighestToLowest_PlacesLowerRowsAboveUpperRows()
    {
        var candidates = new[]
        {
            Candidate(1, top: 100, left: 20, stableKey: "top-left"),
            Candidate(2, top: 100, left: 380, stableKey: "top-right"),
            Candidate(3, top: 300, left: 20, stableKey: "bottom-left"),
            Candidate(4, top: 300, left: 380, stableKey: "bottom-right")
        };

        IReadOnlyList<IdleWidgetZOrderCandidate> ordered =
            IdleWidgetZOrderPolicy.OrderHighestToLowest(candidates);

        Assert.Equal(new long[] { 4, 3, 2, 1 }, Handles(ordered));
    }

    [Fact]
    public void OrderHighestToLowest_IsIndependentOfSessionEnumerationOrder()
    {
        var first = new[]
        {
            Candidate(1, top: 100, left: 20, stableKey: "top-left"),
            Candidate(2, top: 300, left: 20, stableKey: "bottom-left"),
            Candidate(3, top: 300, left: 380, stableKey: "bottom-right")
        };
        var reversed = first.Reverse();

        Assert.Equal(
            Handles(IdleWidgetZOrderPolicy.OrderHighestToLowest(first)),
            Handles(IdleWidgetZOrderPolicy.OrderHighestToLowest(reversed)));
    }

    [Fact]
    public void OrderHighestToLowest_UsesStableHorizontalOrderWithinRow()
    {
        var candidates = new[]
        {
            Candidate(1, top: 200, left: 20, stableKey: "left"),
            Candidate(2, top: 200, left: 380, stableKey: "right")
        };

        IReadOnlyList<IdleWidgetZOrderCandidate> ordered =
            IdleWidgetZOrderPolicy.OrderHighestToLowest(candidates);

        Assert.Equal(new long[] { 2, 1 }, Handles(ordered));
    }

    [Fact]
    public void OrderHighestToLowest_DeduplicatesWindowHandles()
    {
        var candidates = new[]
        {
            Candidate(1, top: 100, left: 20, stableKey: "first"),
            Candidate(1, top: 500, left: 20, stableKey: "duplicate"),
            Candidate(2, top: 300, left: 20, stableKey: "second")
        };

        IReadOnlyList<IdleWidgetZOrderCandidate> ordered =
            IdleWidgetZOrderPolicy.OrderHighestToLowest(candidates);

        Assert.Equal(new long[] { 2, 1 }, Handles(ordered));
    }

    [Fact]
    public void MatchesRequestedOrder_AcceptsAlreadyAppliedOrder()
    {
        var requested = Handles(10, 20, 30);

        Assert.True(IdleWidgetZOrderPolicy.MatchesRequestedOrder(
            requested,
            peersObservedAboveHighest: Array.Empty<IntPtr>(),
            peersObservedFromHighestDown: Handles(10, 20, 30)));
    }

    [Fact]
    public void MatchesRequestedOrder_RejectsPeerAboveRequestedHighest()
    {
        var requested = Handles(10, 20, 30);

        Assert.False(IdleWidgetZOrderPolicy.MatchesRequestedOrder(
            requested,
            peersObservedAboveHighest: Handles(30),
            peersObservedFromHighestDown: Handles(10, 20, 30)));
    }

    [Fact]
    public void MatchesRequestedOrder_RejectsSwappedPeerOrder()
    {
        var requested = Handles(10, 20, 30);

        Assert.False(IdleWidgetZOrderPolicy.MatchesRequestedOrder(
            requested,
            peersObservedAboveHighest: Array.Empty<IntPtr>(),
            peersObservedFromHighestDown: Handles(10, 30, 20)));
    }

    [Fact]
    public void MatchesRequestedOrder_RejectsMissingPeer()
    {
        var requested = Handles(10, 20, 30);

        Assert.False(IdleWidgetZOrderPolicy.MatchesRequestedOrder(
            requested,
            peersObservedAboveHighest: Array.Empty<IntPtr>(),
            peersObservedFromHighestDown: Handles(10, 20)));
    }

    private static IntPtr[] Handles(params long[] handles) =>
        handles.Select(handle => new IntPtr(handle)).ToArray();

    private static IdleWidgetZOrderCandidate Candidate(
        long handle,
        double top,
        double left,
        string stableKey)
    {
        return new IdleWidgetZOrderCandidate(
            new IntPtr(handle),
            "0:0:1920:1080",
            top,
            left,
            stableKey);
    }

    private static long[] Handles(IEnumerable<IdleWidgetZOrderCandidate> candidates)
    {
        return candidates.Select(candidate => candidate.WindowHandle.ToInt64()).ToArray();
    }
}
