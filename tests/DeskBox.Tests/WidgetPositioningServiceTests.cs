using DeskBox.Models;
using DeskBox.Services;
using Windows.Graphics;

namespace DeskBox.Tests;

public sealed class WidgetPositioningServiceTests
{
    [Fact]
    public void CaptureAnchor_UsesNearestCornerMargins()
    {
        var config = new WidgetConfig();
        var workArea = new RectInt32(0, 0, 1920, 1040);
        var bounds = new RectInt32(1580, 80, 300, 400);

        WidgetPositioningService.CaptureAnchor(config, bounds, workArea);

        Assert.Equal(WidgetPositionAnchors.RightTop, config.PositionAnchor);
        Assert.Equal(40, config.PositionMarginX);
        Assert.Equal(80, config.PositionMarginY);
        Assert.Equal("0:0:1920:1040", config.PositionMonitorKey);
    }

    [Fact]
    public void ResolveBounds_KeepsRightTopMarginWhenWorkAreaWidthChanges()
    {
        var config = new WidgetConfig
        {
            X = 1580,
            Y = 80,
            Width = 300,
            Height = 400,
            PositionAnchor = WidgetPositionAnchors.RightTop,
            PositionMarginX = 40,
            PositionMarginY = 80
        };
        var largerWorkArea = new RectInt32(0, 0, 3840, 2080);

        var bounds = WidgetPositioningService.ResolveBounds(config, largerWorkArea);

        Assert.Equal(3500, bounds.X);
        Assert.Equal(80, bounds.Y);
        Assert.Equal(300, bounds.Width);
        Assert.Equal(400, bounds.Height);
    }

    [Fact]
    public void ResolveBounds_KeepsRightBottomMarginWhenWorkAreaChanges()
    {
        var config = new WidgetConfig
        {
            Width = 320,
            Height = 240,
            PositionAnchor = WidgetPositionAnchors.RightBottom,
            PositionMarginX = 24,
            PositionMarginY = 36
        };
        var workArea = new RectInt32(100, 50, 1600, 900);

        var bounds = WidgetPositioningService.ResolveBounds(config, workArea);

        Assert.Equal(1356, bounds.X);
        Assert.Equal(674, bounds.Y);
    }

    [Fact]
    public void ResolveBounds_ClampsLegacyAbsoluteCoordinatesIntoFallbackWorkArea()
    {
        var config = new WidgetConfig
        {
            X = 3500,
            Y = 120,
            Width = 300,
            Height = 400
        };
        var laptopWorkArea = new RectInt32(0, 0, 1920, 1040);

        var bounds = WidgetPositioningService.ResolveBounds(config, laptopWorkArea);

        Assert.Equal(32, bounds.X);
        Assert.Equal(32, bounds.Y);
    }

    [Fact]
    public void ResolveBounds_PrefersSavedMonitorWhenAvailable()
    {
        var savedMonitor = new RectInt32(1920, 0, 2560, 1400);
        var primaryMonitor = new RectInt32(0, 0, 1920, 1040);
        var config = new WidgetConfig
        {
            Width = 300,
            Height = 400,
            PositionAnchor = WidgetPositionAnchors.RightTop,
            PositionMarginX = 30,
            PositionMarginY = 60,
            PositionMonitorKey = WidgetPositioningService.CreateMonitorKey(savedMonitor)
        };

        var bounds = WidgetPositioningService.ResolveBounds(
            config,
            primaryMonitor,
            [primaryMonitor, savedMonitor]);

        Assert.Equal(4150, bounds.X);
        Assert.Equal(60, bounds.Y);
    }
}
