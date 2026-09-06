using DeskBox.Services;

namespace DeskBox.Tests;

public sealed class WidgetGroupOrderTests
{
    [Theory]
    [InlineData("a", "b,a,c,d", "b")]
    [InlineData("a", "b,c,d,a", "d")]
    [InlineData("d", "d,a,b,c", "a")]
    [InlineData("d", "a,b,d,c", "c")]
    [InlineData("b", "a,c,b,d", "c")]
    [InlineData("c", "a,c,b,d", "b")]
    public void NativeDrag_MapsBothDirectionsAndEndsToPersistedOrder(
        string source, string visualOrder, string expectedTarget)
    {
        string[] before = ["a", "b", "c", "d"];
        string[] after = visualOrder.Split(',');

        Assert.True(WidgetGroupOrder.TryResolveDragTarget(before, after, source, out string? target));
        Assert.Equal(expectedTarget, target);
        IList<string> persisted = before.ToList();
        Assert.True(WidgetGroupOrder.MoveToTargetSlot(persisted, source, target!));
        Assert.Equal(after, persisted);
        Assert.Equal(["a", "b", "c", "d"], before);
    }

    [Theory]
    [InlineData("a,b,c,d", "a")]
    [InlineData("b,c,d", "a")]
    [InlineData("b,c,d,a,e", "a")]
    [InlineData("b,b,d,a", "a")]
    [InlineData("c,b,d,a", "a")]
    [InlineData("b,c,d,a", "missing")]
    public void NativeDrag_RejectsNoMoveRemovedMembersAndConcurrentChanges(string visualOrder, string source)
    {
        Assert.False(WidgetGroupOrder.TryResolveDragTarget(
            ["a", "b", "c", "d"], visualOrder.Split(','), source, out string? target));
        Assert.Null(target);
    }

    [Fact]
    public async Task Reorder_SaveFailureRestoresTheOriginalListInstanceAndOrder()
    {
        IList<string> members = new List<string> { "a", "b", "c" };
        IList<string> originalReference = members;
        int saves = 0;

        Assert.False(await WidgetGroupOrder.MoveAndSaveAsync(members, "a", "c", () =>
        {
            saves++;
            Assert.Equal(["b", "c", "a"], members);
            return Task.FromResult(false);
        }));

        Assert.Equal(1, saves);
        Assert.Same(originalReference, members);
        Assert.Equal(["a", "b", "c"], members);
    }

    [Fact]
    public async Task Reorder_SaveExceptionRestoresOrderBeforePropagating()
    {
        IList<string> members = new List<string> { "a", "b", "c" };
        await Assert.ThrowsAsync<IOException>(() => WidgetGroupOrder.MoveAndSaveAsync(
            members, "c", "a", () => Task.FromException<bool>(new IOException("Save failed"))));
        Assert.Equal(["a", "b", "c"], members);
    }

    [Fact]
    public async Task Reorder_SuccessSavesOnceAndRetainsTheNewOrder()
    {
        IList<string> members = new List<string> { "a", "b", "c" };
        int saves = 0;
        Assert.True(await WidgetGroupOrder.MoveAndSaveAsync(members, "c", "a", () =>
        {
            saves++;
            return Task.FromResult(true);
        }));
        Assert.Equal(1, saves);
        Assert.Equal(["c", "a", "b"], members);
    }

    [Fact]
    public async Task Reorder_NoChangeDoesNotWriteSettings()
    {
        IList<string> members = new List<string> { "a", "b", "c" };
        Assert.False(await WidgetGroupOrder.MoveAndSaveAsync(members, "b", "b",
            () => throw new InvalidOperationException("No-op must not save")));
        Assert.Equal(["a", "b", "c"], members);
    }

    [Fact]
    public void AdjacentMoves_AreSymmetric()
    {
        IList<string> movingDown = new List<string> { "a", "b", "c" };
        IList<string> movingUp = new List<string> { "a", "b", "c" };

        Assert.True(
            WidgetGroupOrder.MoveToTargetSlot(
                movingDown,
                "a",
                "b"));
        Assert.True(
            WidgetGroupOrder.MoveToTargetSlot(
                movingUp,
                "c",
                "b"));

        Assert.Equal(["b", "a", "c"], movingDown);
        Assert.Equal(["a", "c", "b"], movingUp);
    }

    [Fact]
    public void MoveToDistantTarget_UsesTargetsOriginalSlot()
    {
        IList<string> members = new List<string> { "a", "b", "c", "d" };

        Assert.True(
            WidgetGroupOrder.MoveToTargetSlot(
                members,
                "a",
                "d"));

        Assert.Equal(["b", "c", "d", "a"], members);
    }
}
