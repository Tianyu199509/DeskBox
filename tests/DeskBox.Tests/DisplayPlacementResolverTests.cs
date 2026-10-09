using DeskBox.Models;
using DeskBox.Services;
using Windows.Graphics;

namespace DeskBox.Tests;

/// <summary>
/// Spec 9.1 R1–R5: the unified resolver's priority matrix, the per-surface
/// authoritative seeding (defect B3), and capsule identity derivation (B4).
/// </summary>
public sealed class DisplayPlacementResolverTests
{
    private static readonly RectInt32 LaptopArea = new(0, 0, 1920, 1040);
    private static readonly RectInt32 ExternalArea = new(1920, 0, 1920, 1040);
    private static readonly RectInt32 TvArea = new(3840, 0, 1920, 1040);

    private static WidgetScreenInfo Screen(
        string stableId,
        RectInt32 area,
        bool primary = false,
        string? deviceName = null) => new(
        1, stableId, deviceName ?? @"\\.\DISPLAY9", area, area, primary, 1.0);

    [Fact]
    public void R3_FollowPrimary_OutanksEverything()
    {
        var decision = DisplayPlacementResolver.Resolve(
            new DisplayPlacementIntent(WidgetScreenBindingMode.FollowPrimary, "external"),
            new DisplayPlacementEntryReference("laptop", null, null, false),
            [Screen("laptop", LaptopArea, primary: true), Screen("external", ExternalArea)]);

        Assert.Equal("laptop", decision.Display.StableId);
        Assert.Equal(DisplayPlacementReason.FollowPrimary, decision.Reason);
        Assert.False(decision.IsFallback);
    }

    [Fact]
    public void R3_HomeOnline_WinsOverEntry()
    {
        var decision = DisplayPlacementResolver.Resolve(
            new DisplayPlacementIntent(WidgetScreenBindingMode.Pinned, "external"),
            new DisplayPlacementEntryReference("laptop", null, null, false),
            [Screen("laptop", LaptopArea, primary: true), Screen("external", ExternalArea)]);

        Assert.Equal("external", decision.Display.StableId);
        Assert.Equal(DisplayPlacementReason.Home, decision.Reason);
        Assert.False(decision.IsFallback);
    }

    [Fact]
    public void R3_HomeOffline_EntryIdentity_IsFallback()
    {
        var decision = DisplayPlacementResolver.Resolve(
            new DisplayPlacementIntent(WidgetScreenBindingMode.Pinned, "unplugged"),
            new DisplayPlacementEntryReference("laptop", null, null, false),
            [Screen("laptop", LaptopArea, primary: true)]);

        Assert.Equal("laptop", decision.Display.StableId);
        Assert.Equal(DisplayPlacementReason.Entry, decision.Reason);
        Assert.True(decision.IsFallback);
    }

    [Fact]
    public void R3_NonDegenerateEntryStableIdOffline_SkipsDeviceNameAndFallsToHeuristic()
    {
        // The entry's stable id names a monitor that is offline; its device
        // name now belongs to a DIFFERENT physical monitor. The resolver must
        // not be baited by the renumbered alias (spec 5.1 step 3).
        var decision = DisplayPlacementResolver.Resolve(
            new DisplayPlacementIntent(WidgetScreenBindingMode.Unbound, null),
            new DisplayPlacementEntryReference(
                "gone-external",
                @"\\.\DISPLAY2",
                "1920:0:1920:1040",
                WasPrimary: false),
            [
                Screen("laptop", LaptopArea, primary: true, @"\\.\DISPLAY1"),
                Screen("tv", TvArea, primary: false, @"\\.\DISPLAY2")
            ]);

        Assert.NotEqual(DisplayPlacementReason.Entry, decision.Reason);
        // Heuristic: entry geometry sat right of the primary → tv (also right).
        Assert.Equal("tv", decision.Display.StableId);
    }

    [Fact]
    public void R3_DegenerateEntryStableId_UsesDeviceNameChain()
    {
        var decision = DisplayPlacementResolver.Resolve(
            new DisplayPlacementIntent(WidgetScreenBindingMode.Unbound, null),
            new DisplayPlacementEntryReference(@"\\.\DISPLAY2", @"\\.\DISPLAY2", null, false),
            [
                Screen("laptop", LaptopArea, primary: true, @"\\.\DISPLAY1"),
                Screen("tv", TvArea, primary: false, @"\\.\DISPLAY2")
            ]);

        Assert.Equal(DisplayPlacementReason.Entry, decision.Reason);
        Assert.Equal("tv", decision.Display.StableId);
    }

    [Fact]
    public void R3_WasPrimaryHeuristic_FollowsCurrentPrimary()
    {
        var decision = DisplayPlacementResolver.Resolve(
            new DisplayPlacementIntent(WidgetScreenBindingMode.Unbound, null),
            new DisplayPlacementEntryReference("gone", null, "0:0:1920:1040", WasPrimary: true),
            [Screen("laptop", LaptopArea, primary: true), Screen("external", ExternalArea)]);

        Assert.Equal("laptop", decision.Display.StableId);
        Assert.Equal(DisplayPlacementReason.Heuristic, decision.Reason);
    }

    [Fact]
    public void R3_NoHeuristicCandidate_FallsBackToPrimary()
    {
        var decision = DisplayPlacementResolver.Resolve(
            new DisplayPlacementIntent(WidgetScreenBindingMode.Unbound, null),
            new DisplayPlacementEntryReference("gone", null, "1920:0:1920:1040", WasPrimary: false),
            [Screen("laptop", LaptopArea, primary: true)]);

        Assert.Equal("laptop", decision.Display.StableId);
        Assert.Equal(DisplayPlacementReason.Primary, decision.Reason);
    }

    [Fact]
    public void R4_DockPlusTv_FirstSeenSeedsFromAuthoritativeExternalEntry()
    {
        // {L,E} widget on E → unplug dock {L} (fallback to L) → dock + TV
        // {L,E,TV}: the new combination must seed from the widget's
        // authoritative entry on E, not from the fallback layout on L.
        var widget = new WidgetConfig
        {
            Id = "w",
            X = 2140,
            Y = 120,
            Width = 600,
            Height = 500,
            BoundsCoordinateVersion = WidgetConfig.CurrentBoundsCoordinateVersion,
            PositionAnchor = WidgetPositionAnchors.LeftTop,
            PositionMarginX = 220,
            PositionMarginY = 120,
            PositionMonitorStableId = "external",
            PositionMonitorDeviceName = @"\\.\DISPLAY2",
            PositionMonitorKey = "1920:0:1920:1040",
            PositionMonitorWasPrimary = false
        };
        var settings = new AppSettings { Widgets = [widget] };
        var service = new WidgetTopologyLayoutService();

        var dock = Snap(Mon("laptop", true, 0, 0), Mon("external", false, 1920, 0));
        var laptopOnly = Snap(Mon("laptop", true, 0, 0));
        var dockPlusTv = Snap(Mon("laptop", true, 0, 0), Mon("external", false, 1920, 0), Mon("tv", false, 3840, 0));

        service.Activate(settings, dock);
        service.Activate(settings, laptopOnly);
        // Simulate the user moving the widget while docked-unplugged: the
        // {L} profile records a fallback placement on the laptop.
        widget.X = 100;
        widget.Y = 80;
        widget.PositionMonitorStableId = "laptop";
        widget.PositionMonitorKey = "0:0:1920:1040";
        service.Activate(settings, dockPlusTv);

        // The external monitor is back: the widget returns to E with its
        // docked geometry (B3 fixed).
        Assert.Equal(2140, widget.X);
        Assert.Equal(120, widget.Y);
        Assert.Equal("external", widget.PositionMonitorStableId);
    }

    [Fact]
    public void R5_CapsulePlacement_SharesSurfaceIdentity()
    {
        var config = new WidgetConfig
        {
            Id = "w",
            Width = 600,
            Height = 500,
            PositionMonitorStableId = "external",
            PositionMonitorDeviceName = @"\\.\DISPLAY2",
            PositionMonitorKey = "1920:0:1920:1040",
            PositionMonitorWasPrimary = false,
            CompactPlacement = new WidgetCompactPlacement
            {
                X = 2000,
                Y = 40,
                PositionAnchor = WidgetPositionAnchors.LeftTop,
                PositionMonitorStableId = "stale-gone",
                PositionMonitorDeviceName = @"\\.\DISPLAY1",
                PositionMonitorKey = "0:0:1920:1040",
                PositionMonitorWasPrimary = true
            }
        };

        // CapturePlacementCore with the external monitor's work area: the
        // production DisplayArea probe needs the WinAppSDK WinRT runtime,
        // which the CI test host does not have registered.
        WidgetCompactBoundsCalculator.CapturePlacementCore(
            config,
            new RectInt32(2000, 40, 248, 42),
            workArea: new RectInt32(1920, 0, 1920, 1040));

        // Derived fields mirror the surface (spec 4.4), never the capsule's
        // own snapshot.
        Assert.Equal("external", config.CompactPlacement!.PositionMonitorStableId);
        Assert.Equal(@"\\.\DISPLAY2", config.CompactPlacement.PositionMonitorDeviceName);
        Assert.Equal("1920:0:1920:1040", config.CompactPlacement.PositionMonitorKey);
        Assert.False(config.CompactPlacement.PositionMonitorWasPrimary);
    }

    private static WidgetDisplayTopologySnapshot Snap(params WidgetTopologyMonitorProfile[] monitors) =>
        WidgetTopologyLayoutService.CreateSnapshotForTest(monitors);

    private static WidgetTopologyMonitorProfile Mon(
        string stableId,
        bool primary,
        int x,
        int y) => new()
    {
        StableId = stableId,
        DeviceName = @"\\.\DISPLAY9",
        IsPrimary = primary,
        MonitorX = x,
        MonitorY = y,
        MonitorWidth = 1920,
        MonitorHeight = 1040,
        WorkAreaX = x,
        WorkAreaY = y,
        WorkAreaWidth = 1920,
        WorkAreaHeight = 1040,
        DpiScale = 1
    };
}
