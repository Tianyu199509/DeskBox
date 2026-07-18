using DeskBox.Services;
using Windows.Graphics;

namespace DeskBox.Tests;

public sealed class WidgetCompactExpansionCalculatorTests
{
    [Theory]
    [InlineData(WidgetCompactExpansionAnchor.LeftTop, 500, 300)]
    [InlineData(WidgetCompactExpansionAnchor.RightTop, 180, 300)]
    [InlineData(WidgetCompactExpansionAnchor.LeftBottom, 500, 42)]
    [InlineData(WidgetCompactExpansionAnchor.RightBottom, 180, 42)]
    public void Resolve_PreservesTheSelectedCompactCorner(
        WidgetCompactExpansionAnchor anchor,
        int expectedX,
        int expectedY)
    {
        RectInt32 compact = new(500, 300, 80, 42);
        WidgetCompactExpansionLayout result = WidgetCompactExpansionCalculator.Resolve(
            compact,
            new SizeInt32(400, 300),
            new RectInt32(0, 0, 1200, 900),
            [anchor]);

        Assert.Equal(new RectInt32(expectedX, expectedY, 400, 300), result.ExpandedBounds);
        Assert.Equal(
            WidgetCompactExpansionCalculator.GetPivot(compact, anchor),
            WidgetCompactExpansionCalculator.GetPivot(result.ExpandedBounds, anchor));
    }

    [Fact]
    public void Resolve_ChoosesAFittingDirectionBeforePreferredOverflow()
    {
        WidgetCompactExpansionLayout result = WidgetCompactExpansionCalculator.Resolve(
            new RectInt32(850, 40, 120, 42),
            new SizeInt32(500, 400),
            new RectInt32(0, 0, 1000, 800),
            [
                WidgetCompactExpansionAnchor.LeftTop,
                WidgetCompactExpansionAnchor.RightTop
            ]);

        Assert.Equal(WidgetCompactExpansionAnchor.RightTop, result.Anchor);
        Assert.False(result.IsSizeConstrained);
        Assert.Equal(new RectInt32(470, 40, 500, 400), result.ExpandedBounds);
    }

    [Fact]
    public void Resolve_ConstrainsSizeWithoutBreakingTheSharedCorner()
    {
        RectInt32 compact = new(480, 360, 80, 42);
        WidgetCompactExpansionLayout result = WidgetCompactExpansionCalculator.Resolve(
            compact,
            new SizeInt32(900, 700),
            new RectInt32(0, 0, 1000, 800),
            [WidgetCompactExpansionAnchor.LeftTop]);

        Assert.True(result.IsSizeConstrained);
        Assert.Equal(new RectInt32(480, 360, 520, 440), result.ExpandedBounds);
        Assert.Equal(
            WidgetCompactExpansionCalculator.GetPivot(compact, result.Anchor),
            WidgetCompactExpansionCalculator.GetPivot(result.ExpandedBounds, result.Anchor));
    }

    [Theory]
    [InlineData(WidgetCompactExpansionAnchor.LeftTop)]
    [InlineData(WidgetCompactExpansionAnchor.RightTop)]
    [InlineData(WidgetCompactExpansionAnchor.LeftBottom)]
    [InlineData(WidgetCompactExpansionAnchor.RightBottom)]
    public void InterpolateAnchoredBounds_KeepsPivotFixedForEveryFrame(
        WidgetCompactExpansionAnchor anchor)
    {
        RectInt32 compact = new(500, 300, 160, 42);
        PointInt32 pivot = WidgetCompactExpansionCalculator.GetPivot(compact, anchor);
        RectInt32 expanded = WidgetCompactExpansionCalculator.CreateBoundsFromPivot(
            pivot,
            new SizeInt32(420, 320),
            anchor);

        for (int step = 0; step <= 20; step++)
        {
            RectInt32 frame = WidgetCompactExpansionCalculator.InterpolateAnchoredBounds(
                compact,
                expanded,
                pivot,
                anchor,
                step / 20d);
            Assert.Equal(pivot, WidgetCompactExpansionCalculator.GetPivot(frame, anchor));
        }
    }

    [Fact]
    public void InterpolateAnchoredBounds_IsSymmetric()
    {
        const WidgetCompactExpansionAnchor anchor = WidgetCompactExpansionAnchor.RightBottom;
        RectInt32 compact = new(500, 300, 160, 42);
        PointInt32 pivot = WidgetCompactExpansionCalculator.GetPivot(compact, anchor);
        RectInt32 expanded = WidgetCompactExpansionCalculator.CreateBoundsFromPivot(
            pivot,
            new SizeInt32(420, 320),
            anchor);

        RectInt32 opening = WidgetCompactExpansionCalculator.InterpolateAnchoredBounds(
            compact,
            expanded,
            pivot,
            anchor,
            0.35);
        RectInt32 closing = WidgetCompactExpansionCalculator.InterpolateAnchoredBounds(
            expanded,
            compact,
            pivot,
            anchor,
            0.65);

        Assert.Equal(opening, closing);
    }

    [Theory]
    [InlineData(WidgetCompactExpansionAnchor.RightTop, "Right", WidgetCompactExpansionAnchor.LeftTop)]
    [InlineData(WidgetCompactExpansionAnchor.RightBottom, "Right", WidgetCompactExpansionAnchor.LeftBottom)]
    [InlineData(WidgetCompactExpansionAnchor.LeftTop, "Left", WidgetCompactExpansionAnchor.RightTop)]
    [InlineData(WidgetCompactExpansionAnchor.LeftBottom, "Left", WidgetCompactExpansionAnchor.RightBottom)]
    public void ResolveHorizontalResizeAnchor_PreservesTheOppositeEdge(
        WidgetCompactExpansionAnchor current,
        string direction,
        WidgetCompactExpansionAnchor expected)
    {
        Assert.Equal(
            expected,
            WidgetCompactExpansionCalculator.ResolveHorizontalResizeAnchor(
                current,
                direction));
    }
}
