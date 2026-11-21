using DeskBox.Services;
using Windows.Graphics;

namespace DeskBox.Tests;

public sealed class WidgetGroupDropHitTestPolicyTests
{
    [Fact]
    public void Contains_AcceptsOnlyTheVisibleTitleRectangle()
    {
        var titleBounds = new RectInt32(120, 80, 240, 42);

        Assert.True(WidgetGroupDropHitTestPolicy.Contains(titleBounds, 180, 100));
        Assert.False(WidgetGroupDropHitTestPolicy.Contains(titleBounds, 180, 122));
        Assert.False(WidgetGroupDropHitTestPolicy.Contains(titleBounds, 180, 220));
    }

    [Fact]
    public void Contains_UsesHalfOpenEdges()
    {
        var titleBounds = new RectInt32(10, 20, 30, 40);

        Assert.True(WidgetGroupDropHitTestPolicy.Contains(titleBounds, 10, 20));
        Assert.True(WidgetGroupDropHitTestPolicy.Contains(titleBounds, 39, 59));
        Assert.False(WidgetGroupDropHitTestPolicy.Contains(titleBounds, 40, 59));
        Assert.False(WidgetGroupDropHitTestPolicy.Contains(titleBounds, 39, 60));
    }

    [Fact]
    public void Contains_RejectsMissingOrEmptyTitleBounds()
    {
        Assert.False(WidgetGroupDropHitTestPolicy.Contains(null, 0, 0));
        Assert.False(WidgetGroupDropHitTestPolicy.Contains(
            new RectInt32(0, 0, 0, 12),
            0,
            0));
    }

    [Fact]
    public void Contains_SupportsNegativeMonitorCoordinates()
    {
        var titleBounds = new RectInt32(-1920, -50, 300, 40);

        Assert.True(WidgetGroupDropHitTestPolicy.Contains(
            titleBounds,
            -1800,
            -30));
    }

    [Fact]
    public void Contains_CompactIdentityDoesNotIncludeAdjacentCompactActions()
    {
        var compactIdentityBounds = new RectInt32(500, 300, 96, 34);

        Assert.True(WidgetGroupDropHitTestPolicy.Contains(
            compactIdentityBounds,
            560,
            317));
        Assert.False(WidgetGroupDropHitTestPolicy.Contains(
            compactIdentityBounds,
            610,
            317));
    }

    [Fact]
    public void GetCenterZone_InsetsAQuarterOfEachEdge()
    {
        RectInt32? zone = WidgetGroupDropHitTestPolicy.GetCenterZone(
            new RectInt32(100, 100, 400, 300),
            minimumSize: 80);

        Assert.Equal(new RectInt32(200, 175, 200, 150), zone);
    }

    [Fact]
    public void GetCenterZone_GrowsToTheMinimumButNeverPastTheSurface()
    {
        RectInt32? zone = WidgetGroupDropHitTestPolicy.GetCenterZone(
            new RectInt32(0, 0, 120, 40),
            minimumSize: 80);

        Assert.Equal(new RectInt32(20, 0, 80, 40), zone);
        Assert.Null(WidgetGroupDropHitTestPolicy.GetCenterZone(null, 80));
        Assert.Null(WidgetGroupDropHitTestPolicy.GetCenterZone(new RectInt32(0, 0, 0, 40), 80));
    }

    [Fact]
    public void ResolveZone_KeepsTheOuterRingForSnappingNotMerging()
    {
        var surface = new RectInt32(100, 100, 400, 300);
        var title = new RectInt32(100, 100, 400, 40);

        // Near the surface edge (where side-by-side arranging happens) the
        // pointer is over the widget but outside every drop zone.
        Assert.Equal(
            WidgetGroupDropZone.None,
            WidgetGroupDropHitTestPolicy.ResolveZone(surface, title, 110, 380, 80));
        Assert.Equal(
            WidgetGroupDropZone.None,
            WidgetGroupDropHitTestPolicy.ResolveZone(surface, title, 490, 250, 80));
        Assert.Equal(
            WidgetGroupDropZone.Center,
            WidgetGroupDropHitTestPolicy.ResolveZone(surface, title, 300, 250, 80));
        Assert.Equal(
            WidgetGroupDropZone.Title,
            WidgetGroupDropHitTestPolicy.ResolveZone(surface, title, 110, 120, 80));
    }

    [Fact]
    public void ResolveZone_TitleWinsWhereItOverlapsTheCenterZone()
    {
        // A collapsed capsule: the title strip covers the whole surface.
        var capsule = new RectInt32(0, 0, 120, 36);

        Assert.Equal(
            WidgetGroupDropZone.Title,
            WidgetGroupDropHitTestPolicy.ResolveZone(capsule, capsule, 60, 18, 80));
    }

    [Fact]
    public void ResolveZone_HiddenTitleTargetsAreReachableThroughTheCenter()
    {
        var surface = new RectInt32(0, 0, 300, 300);

        Assert.Equal(
            WidgetGroupDropZone.Center,
            WidgetGroupDropHitTestPolicy.ResolveZone(surface, title: null, 150, 150, 80));
        Assert.Equal(
            WidgetGroupDropZone.None,
            WidgetGroupDropHitTestPolicy.ResolveZone(surface, title: null, 10, 10, 80));
    }

    [Fact]
    public void GetDwell_TitleFastLaneArmsSoonerThanTheCenterZone()
    {
        Assert.Equal(
            TimeSpan.FromMilliseconds(200),
            WidgetGroupDropHitTestPolicy.GetDwell(WidgetGroupDropZone.Title));
        Assert.Equal(
            TimeSpan.FromMilliseconds(450),
            WidgetGroupDropHitTestPolicy.GetDwell(WidgetGroupDropZone.Center));
    }
}