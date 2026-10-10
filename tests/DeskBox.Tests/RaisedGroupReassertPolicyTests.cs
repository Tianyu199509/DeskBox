using DeskBox.Services;

namespace DeskBox.Tests;

public sealed class RaisedGroupReassertPolicyTests
{
    [Fact]
    public void Resolve_SplitBandEscalatesToGroupLift()
    {
        Assert.Equal(
            RaisedGroupReassertPolicy.ReassertAction.LiftGroupAboveForeignWindows,
            RaisedGroupReassertPolicy.Resolve(foreignWindowAboveRaisedPeer: true));
    }

    [Fact]
    public void Resolve_IntactBandKeepsRepaintFreePeerReorder()
    {
        Assert.Equal(
            RaisedGroupReassertPolicy.ReassertAction.ReorderPeersOnly,
            RaisedGroupReassertPolicy.Resolve(foreignWindowAboveRaisedPeer: false));
    }
}
