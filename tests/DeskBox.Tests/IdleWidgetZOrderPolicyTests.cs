using DeskBox.Services;
using Windows.Foundation;

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
    public void ShouldNormalizeIdlePeerOrder_OnlyRunsWhileDropShadowsAreEnabled()
    {
        Assert.True(IdleWidgetZOrderPolicy.ShouldNormalizeIdlePeerOrder(
            windowDropShadowEnabled: true));
        Assert.False(IdleWidgetZOrderPolicy.ShouldNormalizeIdlePeerOrder(
            windowDropShadowEnabled: false));
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

    [Fact]
    public void HasAnyShadowOverlapRisk_DetectsNearbyWidgetsAndSkipsWidelySeparatedOnes()
    {
        Assert.False(IdleWidgetZOrderPolicy.HasAnyShadowOverlapRisk(
        [
            new Rect(20, 20, 300, 220),
            new Rect(500, 420, 300, 220)
        ]));

        // Separated by 24px vertically (< 88px shadow reach): upper shadow can
        // touch the lower widget's top edge, so peer normalization is needed.
        Assert.True(IdleWidgetZOrderPolicy.HasAnyShadowOverlapRisk(
        [
            new Rect(20, 20, 300, 220),
            new Rect(20, 264, 300, 220)
        ]));

        // Separated by exactly the reach (88px): non-overlapping shadow reach.
        Assert.False(IdleWidgetZOrderPolicy.HasAnyShadowOverlapRisk(
        [
            new Rect(20, 20, 300, 220),
            new Rect(20, 240 + IdleWidgetZOrderPolicy.DefaultShadowOverlapReachPixels, 300, 220)
        ]));
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
