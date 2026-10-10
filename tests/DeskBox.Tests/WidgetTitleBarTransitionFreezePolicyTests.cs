using DeskBox.Models;
using DeskBox.Services;

namespace DeskBox.Tests;

public sealed class WidgetTitleBarTransitionFreezePolicyTests
{
    [Theory]
    [InlineData(WidgetCompactExpansionAnchor.LeftTop, false)]
    [InlineData(WidgetCompactExpansionAnchor.LeftBottom, false)]
    [InlineData(WidgetCompactExpansionAnchor.RightTop, true)]
    [InlineData(WidgetCompactExpansionAnchor.RightBottom, true)]
    public void Freeze_AlignsAgainstTheMovingWindowEdge(
        WidgetCompactExpansionAnchor anchor,
        bool expectedAlignRight)
    {
        WidgetTitleBarTransitionFreeze freeze = WidgetTitleBarTransitionFreezePolicy.Evaluate(
            isOverlayChromeMode: false,
            isTitleBarVisible: true,
            stableWindowWidth: 420,
            expansionAnchor: anchor);

        Assert.True(freeze.ShouldFreeze);
        Assert.Equal(420, freeze.Width);
        Assert.Equal(expectedAlignRight, freeze.AlignRight);
    }

    [Fact]
    public void Freeze_SkipsOverlayChromeAndHiddenTitleBars()
    {
        Assert.False(WidgetTitleBarTransitionFreezePolicy.Evaluate(
                isOverlayChromeMode: true,
                isTitleBarVisible: true,
                stableWindowWidth: 420,
                expansionAnchor: WidgetCompactExpansionAnchor.LeftTop)
            .ShouldFreeze);
        Assert.False(WidgetTitleBarTransitionFreezePolicy.Evaluate(
                isOverlayChromeMode: false,
                isTitleBarVisible: false,
                stableWindowWidth: 420,
                expansionAnchor: WidgetCompactExpansionAnchor.LeftTop)
            .ShouldFreeze);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void Freeze_RejectsNonFiniteOrNonPositiveWidths(double width)
    {
        WidgetTitleBarTransitionFreeze freeze = WidgetTitleBarTransitionFreezePolicy.Evaluate(
            isOverlayChromeMode: false,
            isTitleBarVisible: true,
            stableWindowWidth: width,
            expansionAnchor: WidgetCompactExpansionAnchor.LeftTop);

        Assert.False(freeze.ShouldFreeze);
        Assert.Equal(0, freeze.Width);
    }
}
