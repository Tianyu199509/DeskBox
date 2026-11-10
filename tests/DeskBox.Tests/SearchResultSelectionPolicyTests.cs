using DeskBox.Services;

namespace DeskBox.Tests;

public sealed class SearchResultSelectionPolicyTests
{
    [Theory]
    [InlineData(true, 2, true, true)]
    [InlineData(true, 1, true, false)]
    [InlineData(false, 2, true, false)]
    [InlineData(true, 2, false, false)]
    public void DragAnchor_PreservesOnlyARealMultiSelectionOnTheDragHandle(
        bool itemIsSelected,
        int selectedItemCount,
        bool pointerIsOnDragHandle,
        bool expected)
    {
        Assert.Equal(
            expected,
            SearchResultSelectionPolicy.ShouldPreserveSelectionForDrag(
                itemIsSelected,
                selectedItemCount,
                pointerIsOnDragHandle));
    }

    [Fact]
    public void ResolveDraggedItems_UsesTheFullSelectionForASelectedAnchor()
    {
        object first = new();
        object second = new();
        object third = new();

        Assert.Equal(
            [first, second, third],
            SearchResultSelectionPolicy.ResolveDraggedItems(
                second,
                [first, second, third]));
        Assert.Equal(
            [second],
            SearchResultSelectionPolicy.ResolveDraggedItems(
                second,
                [first, third]));
    }

    [Theory]
    [InlineData(true, false, false, true)]
    [InlineData(true, true, false, false)]
    [InlineData(true, false, true, false)]
    [InlineData(false, false, false, false)]
    public void RubberBand_StartsOnlyFromRealEmptySpace(
        bool isLeftPressed,
        bool isOverRow,
        bool isShiftPressed,
        bool expected)
    {
        Assert.Equal(
            expected,
            SearchResultSelectionPolicy.ShouldStartRubberBand(
                isLeftPressed,
                isOverRow,
                isShiftPressed));
    }

    [Fact]
    public void Range_UsesStableAnchorInEitherDirection()
    {
        Assert.Equal((2, 7), SearchResultSelectionPolicy.GetRange(2, 7, 10));
        Assert.Equal((2, 7), SearchResultSelectionPolicy.GetRange(7, 2, 10));
        Assert.Equal((-1, -1), SearchResultSelectionPolicy.GetRange(10, 2, 10));
    }

    [Fact]
    public void AutoScroll_AcceleratesTowardViewportEdges()
    {
        double topEdge = SearchResultSelectionPolicy.GetAutoScrollDelta(0, 400);
        double nearTop = SearchResultSelectionPolicy.GetAutoScrollDelta(24, 400);
        double middle = SearchResultSelectionPolicy.GetAutoScrollDelta(200, 400);
        double nearBottom = SearchResultSelectionPolicy.GetAutoScrollDelta(376, 400);
        double bottomEdge = SearchResultSelectionPolicy.GetAutoScrollDelta(400, 400);

        Assert.True(topEdge < nearTop);
        Assert.True(nearTop < 0);
        Assert.Equal(0, middle);
        Assert.True(nearBottom > 0);
        Assert.True(bottomEdge > nearBottom);
    }

    [Theory]
    // Expected decision as int: the public test method cannot expose the
    // internal nested enum in its signature.
    [InlineData(false, true, true, 20, 0)]
    [InlineData(true, false, true, 20, 0)]
    [InlineData(true, true, false, 20, 1)]
    [InlineData(true, true, true, 0, 0)]
    [InlineData(true, true, true, 7.99, 0)]
    [InlineData(true, true, true, 8, 2)]
    [InlineData(true, true, true, 800, 2)]
    public void EvaluateDragOutMove_GatesOnPointerIdentityContactAndThreshold(
        bool candidateArmed,
        bool samePointer,
        bool leftButtonPressed,
        double distanceUnits,
        int expected)
    {
        Assert.Equal(
            (SearchResultSelectionPolicy.DragOutMoveDecision)expected,
            SearchResultSelectionPolicy.EvaluateDragOutMove(
                candidateArmed,
                samePointer,
                leftButtonPressed,
                distanceUnits));
    }

    [Fact]
    public void EvaluateDragOutMove_IgnoresForeignPointerEvenWithoutContact()
    {
        // A move from a different pointer (second mouse, stray pen) never
        // touches the armed candidate — not even to disarm it. The press
        // owner keeps its gesture.
        Assert.Equal(
            SearchResultSelectionPolicy.DragOutMoveDecision.Ignore,
            SearchResultSelectionPolicy.EvaluateDragOutMove(
                candidateArmed: true,
                samePointer: false,
                leftButtonPressed: false,
                distanceUnits: 100));
    }
}
