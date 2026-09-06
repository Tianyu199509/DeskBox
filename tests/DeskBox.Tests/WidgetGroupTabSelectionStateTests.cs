using DeskBox.Models;

namespace DeskBox.Tests;

public sealed class WidgetGroupTabSelectionStateTests
{
    private static readonly IReadOnlySet<string> Members = new HashSet<string> { "a", "b", "c" };

    [Fact]
    public void Click_KeepsNewTabSelectedThroughReleaseRefreshesAndContentCommit()
    {
        var selection = new WidgetGroupTabSelectionState();
        selection.Request("group", "b");

        // Pointer release, appearance refresh and content preparation can all
        // still carry the old committed member before the transition completes.
        string[] visibleSelections =
        [
            "b", // Native pointer selection.
            selection.ResolveSelection("group", "a", Members),
            selection.ResolveSelection("group", "a", Members),
            selection.ResolveSelection("group", "a", Members),
            selection.ResolveSelection("group", "b", Members)
        ];

        Assert.All(visibleSelections, id => Assert.Equal("b", id));
        Assert.Null(selection.PendingMemberId);
    }

    [Fact]
    public void FailedSwitch_RestoresCommittedMember()
    {
        var selection = new WidgetGroupTabSelectionState();
        selection.Request("group", "b");

        Assert.True(selection.Complete(selection.RequestVersion, "b", succeeded: false, activeMemberId: "a"));
        Assert.Equal("a", selection.ResolveSelection("group", "a", Members));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OlderCompletion_DoesNotOverrideNewerClick(bool succeeded)
    {
        var selection = new WidgetGroupTabSelectionState();
        long oldRequest = selection.Request("group", "b");
        selection.Request("group", "c");

        Assert.False(selection.Complete(oldRequest, "b", succeeded, activeMemberId: "a"));
        Assert.Equal("c", selection.ResolveSelection("group", "a", Members));
    }

    [Fact]
    public void CoalescedSuccessBeforeContentCommit_DoesNotSelectOldMember()
    {
        var selection = new WidgetGroupTabSelectionState();
        selection.Request("group", "b");

        Assert.False(selection.Complete(selection.RequestVersion, "b", succeeded: true, activeMemberId: "a"));
        Assert.Equal("b", selection.ResolveSelection("group", "a", Members));
        Assert.True(selection.Complete(selection.RequestVersion, "b", succeeded: true, activeMemberId: "b"));
        Assert.Null(selection.PendingMemberId);
    }

    [Fact]
    public void ReturningToCurrentMember_CancelsPendingVisualSelection()
    {
        var selection = new WidgetGroupTabSelectionState();
        selection.Request("group", "b");
        selection.Request("group", "a");

        Assert.Equal("a", selection.ResolveSelection("group", "a", Members));
        Assert.False(selection.Complete(selection.RequestVersion, "b", succeeded: false, activeMemberId: "a"));
        Assert.Null(selection.PendingMemberId);
    }

    [Fact]
    public void GroupChangeOrMemberRemoval_DiscardsObsoleteSelection()
    {
        var selection = new WidgetGroupTabSelectionState();
        selection.Request("group", "b");
        Assert.Equal("c", selection.ResolveSelection("other-group", "c", Members));

        selection.Request("group", "b");
        Assert.Equal("a", selection.ResolveSelection("group", "a", new HashSet<string> { "a", "c" }));
        Assert.Null(selection.PendingMemberId);
    }

    [Fact]
    public void StartingDrag_DiscardsClickIntentAndRestoresCommittedSelectionAfterDrop()
    {
        var selection = new WidgetGroupTabSelectionState();
        selection.Request("group", "b");
        selection.Reset();

        Assert.Equal("a", selection.ResolveSelection("group", "a", Members));
        Assert.False(selection.Complete(selection.RequestVersion, "b", succeeded: false, activeMemberId: "a"));
    }

    [Fact]
    public void RevisitingSameTab_OldCancellationCannotClearLatestRequest()
    {
        var selection = new WidgetGroupTabSelectionState();
        long oldRequest = selection.Request("group", "b");
        selection.Request("group", "c");
        long latestRequest = selection.Request("group", "b");

        Assert.False(selection.Complete(oldRequest, "b", succeeded: false, activeMemberId: "a"));
        Assert.Equal("b", selection.ResolveSelection("group", "a", Members));
        Assert.True(selection.Complete(latestRequest, "b", succeeded: false, activeMemberId: "a"));
        Assert.Equal("a", selection.ResolveSelection("group", "a", Members));
    }
}
