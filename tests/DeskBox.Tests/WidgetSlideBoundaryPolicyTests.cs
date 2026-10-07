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
}
