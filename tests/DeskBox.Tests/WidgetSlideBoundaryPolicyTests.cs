using DeskBox.Services;

namespace DeskBox.Tests;

/// <summary>
/// Pure-geometry coverage for the crossing-fade rules. Display-topology
/// probing (DisplayArea.FindAll) is not unit-testable; only the fade-span
/// resolution arithmetic is pinned here. The slide travel itself is never
/// modified — the decision only describes when the fade starts and ends as
/// the subject crosses the monitor boundary.
/// </summary>
public sealed class WidgetSlideBoundaryPolicyTests
{
    [Fact]
    public void WithoutAdjacentDisplay_CrossingFadeIsDisabled()
    {
        var decision = WidgetSlideBoundaryPolicy.ResolveCrossingFade(
            unconfinedTravel: 500,
            farEdge: 1700,
            nearEdge: 1500,
            workAreaEdge: 1920,
            hasAdjacentDisplay: false);

        Assert.False(decision.UseCrossingFade);
        Assert.Equal(0, decision.StartOffset);
        Assert.Equal(0, decision.EndOffset);
    }

    [Fact]
    public void WithAdjacentDisplay_FadeSpansTheBoundaryCrossing()
    {
        // Window occupies [1500..1700], boundary at 1920: fully opaque for the
        // first 220px (leading edge travels to the boundary), then fades to
        // invisible across the next 200px (trailing edge clears it).
        var decision = WidgetSlideBoundaryPolicy.ResolveCrossingFade(
            unconfinedTravel: 500,
            farEdge: 1700,
            nearEdge: 1500,
            workAreaEdge: 1920,
            hasAdjacentDisplay: true);

        Assert.True(decision.UseCrossingFade);
        Assert.Equal(220, decision.StartOffset);
        Assert.Equal(420, decision.EndOffset);
    }

    [Fact]
    public void FlushSubject_FadesImmediatelyButOnlyWhileCrossing()
    {
        // Subject already touching the boundary: the fade starts at travel 0
        // and still spans the full subject width.
        var decision = WidgetSlideBoundaryPolicy.ResolveCrossingFade(
            unconfinedTravel: 500,
            farEdge: 1920,
            nearEdge: 1720,
            workAreaEdge: 1920,
            hasAdjacentDisplay: true);

        Assert.True(decision.UseCrossingFade);
        Assert.Equal(0, decision.StartOffset);
        Assert.Equal(200, decision.EndOffset);
    }

    [Fact]
    public void LeftwardSlide_UsesMirroredGeometry()
    {
        // Window occupies [20..220], boundary at x=0: leading edge (left edge)
        // is 20px from the boundary, trailing edge 220px.
        var decision = WidgetSlideBoundaryPolicy.ResolveCrossingFade(
            unconfinedTravel: 500,
            farEdge: 20,
            nearEdge: 220,
            workAreaEdge: 0,
            hasAdjacentDisplay: true);

        Assert.True(decision.UseCrossingFade);
        Assert.Equal(20, decision.StartOffset);
        Assert.Equal(220, decision.EndOffset);
    }

    [Fact]
    public void DownwardSlide_UsesVerticalGeometry()
    {
        // Window occupies y [800..1000], work-area bottom at 1080: leading
        // edge (bottom) 80px from the boundary, trailing edge (top) 280px.
        var decision = WidgetSlideBoundaryPolicy.ResolveCrossingFade(
            unconfinedTravel: 500,
            farEdge: 1000,
            nearEdge: 800,
            workAreaEdge: 1080,
            hasAdjacentDisplay: true);

        Assert.True(decision.UseCrossingFade);
        Assert.Equal(80, decision.StartOffset);
        Assert.Equal(280, decision.EndOffset);
    }

    [Fact]
    public void ZeroTravel_DisablesCrossingFade()
    {
        var decision = WidgetSlideBoundaryPolicy.ResolveCrossingFade(
            unconfinedTravel: 0,
            farEdge: 1700,
            nearEdge: 1500,
            workAreaEdge: 1920,
            hasAdjacentDisplay: true);

        Assert.False(decision.UseCrossingFade);
    }

    [Fact]
    public void FadeAlwaysCompletesWithinTheTravel()
    {
        // Odd geometry where the trailing-edge distance would outlive the
        // animation: the span is clamped so opacity reaches zero before the
        // final frame, and start never exceeds end.
        var decision = WidgetSlideBoundaryPolicy.ResolveCrossingFade(
            unconfinedTravel: 300,
            farEdge: 1700,
            nearEdge: 1500,
            workAreaEdge: 1920,
            hasAdjacentDisplay: true);

        Assert.True(decision.UseCrossingFade);
        Assert.Equal(220, decision.StartOffset);
        Assert.Equal(300, decision.EndOffset);

        var tighter = WidgetSlideBoundaryPolicy.ResolveCrossingFade(
            unconfinedTravel: 100,
            farEdge: 1700,
            nearEdge: 1500,
            workAreaEdge: 1920,
            hasAdjacentDisplay: true);

        Assert.True(tighter.UseCrossingFade);
        Assert.Equal(100, tighter.StartOffset);
        Assert.Equal(100, tighter.EndOffset);
    }

    [Fact]
    public void DegenerateSpan_StartNeverExceedsEnd()
    {
        var decision = WidgetSlideBoundaryPolicy.ResolveCrossingFade(
            unconfinedTravel: 500,
            farEdge: 1700,
            nearEdge: 1700,
            workAreaEdge: 1920,
            hasAdjacentDisplay: true);

        Assert.True(decision.UseCrossingFade);
        Assert.Equal(220, decision.StartOffset);
        Assert.Equal(220, decision.EndOffset);
    }

    [Fact]
    public void ShortSlide_ClampsInsideMonitorWhenSlidingTowardAdjacentSeam()
    {
        var outerBounds = new Windows.Graphics.RectInt32(0, 0, 1920, 1080);
        // Widget right edge is at 1908 -> only 12px from the right seam at 1920.
        var resting = new Windows.Foundation.Rect(1608, 200, 300, 400);
        var adjacentRight = new WidgetSlideBoundaryPolicy.AdjacentDisplaySides(
            Left: false, Right: true, Up: false, Down: false);

        var (seamX, seamY) = WidgetSlideBoundaryPolicy.ResolveShortSlideOffset(
            SettingsService.WidgetAnimationSlideDirectionRight,
            resting,
            outerBounds,
            adjacentRight,
            desiredThrowPx: 36);
        Assert.Equal(12, seamX);
        Assert.Equal(0, seamY);
        Assert.True(resting.Right + seamX <= outerBounds.X + outerBounds.Width);

        // Sliding left (non-adjacent outer bezel) keeps the full 36px throw.
        var (bezelX, bezelY) = WidgetSlideBoundaryPolicy.ResolveShortSlideOffset(
            SettingsService.WidgetAnimationSlideDirectionLeft,
            resting,
            outerBounds,
            adjacentRight,
            desiredThrowPx: 36);
        Assert.Equal(-36, bezelX);
        Assert.Equal(0, bezelY);
    }

    [Fact]
    public void ScreenEdgeSlide_AutoAvoidsAdjacentMonitorSeamAndPicksNearestBezel()
    {
        var outerBounds = new Windows.Graphics.RectInt32(0, 0, 1920, 1080);
        var workArea = new Windows.Graphics.RectInt32(0, 0, 1920, 1032);
        // Widget sits near the right seam (gap=20) and near the top bezel (gap=40),
        // with a second monitor attached on the right. Auto must skip Right and
        // slide out through the Top physical bezel!
        var resting = new Windows.Foundation.Rect(1600, 40, 300, 400);
        var adjacentRight = new WidgetSlideBoundaryPolicy.AdjacentDisplaySides(
            Left: false, Right: true, Up: false, Down: false);

        var autoPlan = WidgetSlideBoundaryPolicy.ResolveScreenEdgeSlide(
            SettingsService.WidgetAnimationSlideDirectionAuto,
            resting,
            workArea,
            outerBounds,
            adjacentRight,
            offscreenPaddingPx: 16,
            fallbackShortThrowPx: 48);

        Assert.Equal(SettingsService.WidgetAnimationSlideDirectionUp, autoPlan.ResolvedDirection);
        Assert.False(autoPlan.UseShortFadeFallback);
        Assert.Equal(0, autoPlan.OffsetX);
        Assert.Equal(-(440 + 16), autoPlan.OffsetY);
    }

    [Fact]
    public void ScreenEdgeSlide_ForcedTowardAdjacentSeam_FallsBackToContainedShortFade()
    {
        var outerBounds = new Windows.Graphics.RectInt32(0, 0, 1920, 1080);
        var workArea = new Windows.Graphics.RectInt32(0, 0, 1920, 1032);
        var resting = new Windows.Foundation.Rect(1600, 200, 300, 400);
        var adjacentRight = new WidgetSlideBoundaryPolicy.AdjacentDisplaySides(
            Left: false, Right: true, Up: false, Down: false);

        var blockedPlan = WidgetSlideBoundaryPolicy.ResolveScreenEdgeSlide(
            SettingsService.WidgetAnimationSlideDirectionRight,
            resting,
            workArea,
            outerBounds,
            adjacentRight,
            offscreenPaddingPx: 16,
            fallbackShortThrowPx: 48);

        Assert.Equal(SettingsService.WidgetAnimationSlideDirectionRight, blockedPlan.ResolvedDirection);
        Assert.True(blockedPlan.UseShortFadeFallback);
        Assert.Equal(20, blockedPlan.OffsetX);
        Assert.True(resting.Right + blockedPlan.OffsetX <= outerBounds.X + outerBounds.Width);
    }
}
