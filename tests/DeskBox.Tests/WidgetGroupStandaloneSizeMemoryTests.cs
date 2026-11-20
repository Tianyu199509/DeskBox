using DeskBox.Models;
using DeskBox.Services;
using Windows.Graphics;

namespace DeskBox.Tests;

public sealed class WidgetGroupStandaloneSizeMemoryTests
{
    [Fact]
    public void CaptureAndRestore_PreservesStandaloneSizeAcrossGroupLayoutOverwrite()
    {
        var music = new WidgetConfig
        {
            Id = "music-1",
            WidgetKind = WidgetKind.Music,
            BoundsCoordinateVersion = WidgetConfig.CurrentBoundsCoordinateVersion,
            Width = 280,
            Height = 350
        };

        WidgetGroupStandaloneSizeMemory.CaptureIfStandalone(music, wasAlreadyGrouped: false);

        // Simulate ApplyGroupLayoutToMember overwriting dimensions with a wider file group.
        music.Width = 520;
        music.Height = 460;

        Assert.Equal(
            new SizeInt32(420, 525),
            WidgetGroupStandaloneSizeMemory.ResolveDetachedPhysicalSize(
                music,
                dpiScale: 1.5,
                fallbackPhysicalSize: new SizeInt32(780, 690)));

        Assert.True(WidgetGroupStandaloneSizeMemory.RestoreAndClear(music));
        Assert.Equal(280, music.Width);
        Assert.Equal(350, music.Height);
        Assert.Equal(WidgetConfig.CurrentBoundsCoordinateVersion, music.BoundsCoordinateVersion);
        Assert.DoesNotContain(WidgetGroupStandaloneSizeMemory.PreGroupWidthKey, music.Metadata.Keys);
        Assert.DoesNotContain(WidgetGroupStandaloneSizeMemory.PreGroupHeightKey, music.Metadata.Keys);
    }

    [Fact]
    public void CaptureIfStandalone_DoesNotOverwriteOriginalSizeWhenMovingBetweenGroups()
    {
        var widget = new WidgetConfig
        {
            BoundsCoordinateVersion = WidgetConfig.CurrentBoundsCoordinateVersion,
            Width = 260,
            Height = 320
        };

        WidgetGroupStandaloneSizeMemory.CaptureIfStandalone(widget, wasAlreadyGrouped: false);
        widget.Width = 480;
        widget.Height = 420;

        // Joining a second group while already grouped must keep 260x320.
        WidgetGroupStandaloneSizeMemory.CaptureIfStandalone(widget, wasAlreadyGrouped: true);
        widget.Width = 640;
        widget.Height = 520;

        Assert.True(WidgetGroupStandaloneSizeMemory.RestoreAndClear(widget));
        Assert.Equal(260, widget.Width);
        Assert.Equal(320, widget.Height);
    }

    [Fact]
    public void RestoreAndClear_LeavesCurrentSizeIntactWhenNothingWasStored()
    {
        var legacyGrouped = new WidgetConfig
        {
            BoundsCoordinateVersion = WidgetConfig.CurrentBoundsCoordinateVersion,
            Width = 400,
            Height = 360
        };

        Assert.Equal(
            new SizeInt32(500, 450),
            WidgetGroupStandaloneSizeMemory.ResolveDetachedPhysicalSize(
                legacyGrouped,
                dpiScale: 1.25,
                fallbackPhysicalSize: new SizeInt32(300, 300)));

        Assert.False(WidgetGroupStandaloneSizeMemory.RestoreAndClear(legacyGrouped));
        Assert.Equal(400, legacyGrouped.Width);
        Assert.Equal(360, legacyGrouped.Height);
    }
}
