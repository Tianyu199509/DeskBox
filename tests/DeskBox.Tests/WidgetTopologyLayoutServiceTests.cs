using DeskBox.Models;
using DeskBox.Services;

namespace DeskBox.Tests;

public sealed class WidgetTopologyLayoutServiceTests
{
    [Fact]
    public void TopologyKey_IgnoresTransientAliasesForTheSamePhysicalMonitors()
    {
        WidgetDisplayTopologySnapshot initial = WidgetTopologyLayoutService.CreateSnapshotForTest(
            Monitor("panel", @"\\.\DISPLAY1", true, 0, 0, 1920, 1040, 1),
            Monitor("external", @"\\.\DISPLAY2", false, 1920, 0, 1920, 1040, 1));
        WidgetDisplayTopologySnapshot reEnumerated = WidgetTopologyLayoutService.CreateSnapshotForTest(
            Monitor("panel", @"\\.\DISPLAY2", true, 0, 0, 1920, 1040, 1),
            Monitor("external", @"\\.\DISPLAY1", false, 1920, 0, 1920, 1040, 1));

        Assert.StartsWith("v3-", initial.Key, StringComparison.Ordinal);
        Assert.Equal(initial.Key, reEnumerated.Key);
    }

    [Fact]
    public void FirstActivation_CapturesExistingGeometryWithoutChangingIt()
    {
        var widget = CreateWidget();
        var settings = new AppSettings { Widgets = [widget] };
        WidgetDisplayTopologySnapshot topology = WidgetTopologyLayoutService.CreateSnapshotForTest(
            Monitor("panel", @"\\.\DISPLAY1", true, 0, 0, 3840, 2080, 2));

        Assert.True(new WidgetTopologyLayoutService().Activate(settings, topology));

        Assert.Equal(topology.Key, settings.ActiveWidgetTopologyKey);
        Assert.Single(settings.WidgetTopologyLayouts);
        Assert.Equal(200, widget.X);
        Assert.Equal(160, widget.Y);
        Assert.Equal(600, widget.Width);
        Assert.Equal(500, widget.Height);
        Assert.Equal(100, widget.PositionMarginX);
        Assert.Equal(80, widget.PositionMarginY);
    }

    [Fact]
    public void SamePhysicalMonitorAtDifferentDpi_PreservesLogicalSizeAndMargins()
    {
        var widget = CreateWidget();
        var settings = new AppSettings { Widgets = [widget] };
        var service = new WidgetTopologyLayoutService();
        WidgetDisplayTopologySnapshot highDpi = WidgetTopologyLayoutService.CreateSnapshotForTest(
            Monitor("panel", @"\\.\DISPLAY1", true, 0, 0, 3840, 2080, 2));
        WidgetDisplayTopologySnapshot standardDpi = WidgetTopologyLayoutService.CreateSnapshotForTest(
            Monitor("panel", @"\\.\DISPLAY1", true, 0, 0, 1920, 1040, 1));

        service.Activate(settings, highDpi);
        service.Activate(settings, standardDpi);

        Assert.Equal(600, widget.Width);
        Assert.Equal(500, widget.Height);
        Assert.Equal(100, widget.PositionMarginX);
        Assert.Equal(80, widget.PositionMarginY);
        Assert.Equal(100, widget.X);
        Assert.Equal(80, widget.Y);
    }

    [Fact]
    public void ReturningToKnownTopology_RestoresTheLayoutEditedForThatTopology()
    {
        var widget = CreateWidget();
        var settings = new AppSettings { Widgets = [widget] };
        var service = new WidgetTopologyLayoutService();
        WidgetDisplayTopologySnapshot highDpi = WidgetTopologyLayoutService.CreateSnapshotForTest(
            Monitor("panel", @"\\.\DISPLAY1", true, 0, 0, 3840, 2080, 2));
        WidgetDisplayTopologySnapshot standardDpi = WidgetTopologyLayoutService.CreateSnapshotForTest(
            Monitor("panel", @"\\.\DISPLAY1", true, 0, 0, 1920, 1040, 1));

        service.Activate(settings, highDpi);
        service.Activate(settings, standardDpi);
        widget.Width = 720;
        widget.Height = 620;
        widget.PositionMarginX = 44;
        widget.PositionMarginY = 36;
        widget.X = 44;
        widget.Y = 36;

        service.Activate(settings, highDpi);
        Assert.Equal(600, widget.Width);
        Assert.Equal(500, widget.Height);
        Assert.Equal(200, widget.X);
        Assert.Equal(160, widget.Y);

        service.Activate(settings, standardDpi);
        Assert.Equal(720, widget.Width);
        Assert.Equal(620, widget.Height);
        Assert.Equal(44, widget.X);
        Assert.Equal(36, widget.Y);
    }

    [Fact]
    public void LaptopAndDualMonitorRoundTrip_RestoresEachIndependentLayout()
    {
        var widget = CreateWidget();
        widget.Name = "Shared widget identity";
        var settings = new AppSettings { Widgets = [widget] };
        var service = new WidgetTopologyLayoutService();
        WidgetDisplayTopologySnapshot laptop = WidgetTopologyLayoutService.CreateSnapshotForTest(
            Monitor("panel", @"\\.\DISPLAY1", true, 0, 0, 1920, 1040, 1));
        WidgetDisplayTopologySnapshot dual = WidgetTopologyLayoutService.CreateSnapshotForTest(
            Monitor("panel", @"\\.\DISPLAY1", true, 0, 0, 1920, 1040, 1),
            Monitor("external", @"\\.\DISPLAY2", false, 1920, 0, 1920, 1040, 1));
        WidgetDisplayTopologySnapshot dualWithReassignedAliases =
            WidgetTopologyLayoutService.CreateSnapshotForTest(
                Monitor("panel", @"\\.\DISPLAY2", true, 0, 0, 1920, 1040, 1),
                Monitor("external", @"\\.\DISPLAY1", false, 1920, 0, 1920, 1040, 1));

        service.Activate(settings, laptop);
        service.Activate(settings, dual);
        PlaceOnExternalMonitor(widget, @"\\.\DISPLAY2");

        service.Activate(settings, laptop);

        Assert.Equal(200, widget.X);
        Assert.Equal(160, widget.Y);
        Assert.Equal(600, widget.Width);
        Assert.Equal(500, widget.Height);
        Assert.Equal(@"\\.\DISPLAY1", widget.PositionMonitorDeviceName);
        Assert.Equal("Shared widget identity", widget.Name);

        service.Activate(settings, dualWithReassignedAliases);

        Assert.Equal(2140, widget.X);
        Assert.Equal(120, widget.Y);
        Assert.Equal(720, widget.Width);
        Assert.Equal(600, widget.Height);
        Assert.Equal(@"\\.\DISPLAY1", widget.PositionMonitorDeviceName);
        Assert.Equal(false, widget.PositionMonitorWasPrimary);
        Assert.Equal("Shared widget identity", widget.Name);
        Assert.Equal(dual.Key, dualWithReassignedAliases.Key);
        Assert.Equal(2, settings.WidgetTopologyLayouts.Count);
    }

    [Fact]
    public void LegacyCompatibleDualProfile_IsRestoredAndRetainedUnderStableKey()
    {
        var widget = CreateWidget();
        var settings = new AppSettings { Widgets = [widget] };
        var service = new WidgetTopologyLayoutService();
        WidgetDisplayTopologySnapshot laptop = WidgetTopologyLayoutService.CreateSnapshotForTest(
            Monitor("panel", @"\\.\DISPLAY1", true, 0, 0, 1920, 1040, 1));
        WidgetDisplayTopologySnapshot dual = WidgetTopologyLayoutService.CreateSnapshotForTest(
            Monitor("panel", @"\\.\DISPLAY1", true, 0, 0, 1920, 1040, 1),
            Monitor("external", @"\\.\DISPLAY2", false, 1920, 0, 1920, 1040, 1));
        WidgetDisplayTopologySnapshot dualWithReassignedAliases =
            WidgetTopologyLayoutService.CreateSnapshotForTest(
                Monitor("panel", @"\\.\DISPLAY2", true, 0, 0, 1920, 1040, 1),
                Monitor("external", @"\\.\DISPLAY1", false, 1920, 0, 1920, 1040, 1));

        service.Activate(settings, laptop);
        service.Activate(settings, dual);
        PlaceOnExternalMonitor(widget, @"\\.\DISPLAY2");
        service.Activate(settings, laptop);

        WidgetTopologyLayoutProfile legacyLaptop = settings.WidgetTopologyLayouts[laptop.Key];
        WidgetTopologyLayoutProfile legacyDual = settings.WidgetTopologyLayouts[dual.Key];
        settings.WidgetTopologyLayouts = new Dictionary<string, WidgetTopologyLayoutProfile>
        {
            ["v1-legacy-laptop"] = legacyLaptop,
            ["v1-legacy-dual"] = legacyDual
        };
        settings.ActiveWidgetTopologyKey = "v1-legacy-laptop";

        service.Activate(settings, dualWithReassignedAliases);

        Assert.Equal(dualWithReassignedAliases.Key, settings.ActiveWidgetTopologyKey);
        Assert.Equal(2140, widget.X);
        Assert.Equal(120, widget.Y);
        Assert.Equal(720, widget.Width);
        Assert.Equal(600, widget.Height);
        Assert.Equal(@"\\.\DISPLAY1", widget.PositionMonitorDeviceName);
        Assert.True(settings.WidgetTopologyLayouts.ContainsKey("v1-legacy-laptop"));
        Assert.True(settings.WidgetTopologyLayouts.ContainsKey("v1-legacy-dual"));
        Assert.True(settings.WidgetTopologyLayouts.ContainsKey(dualWithReassignedAliases.Key));
    }

    [Fact]
    public void ReplacementMonitor_SeedsProportionallyInsideItsEffectiveWorkArea()
    {
        var source = new WidgetSurfaceLayoutProfile
        {
            PositionMonitorStableId = "panel",
            PositionMonitorDeviceName = @"\\.\DISPLAY1",
            PositionMonitorWasPrimary = true,
            PositionAnchor = WidgetPositionAnchors.LeftTop,
            PositionMarginX = 100,
            PositionMarginY = 80,
            BoundsCoordinateVersion = WidgetConfig.CurrentBoundsCoordinateVersion,
            X = 200,
            Y = 160,
            Width = 600,
            Height = 500
        };
        WidgetTopologyMonitorProfile oldMonitor =
            Monitor("panel", @"\\.\DISPLAY1", true, 0, 0, 3840, 2080, 2);
        WidgetTopologyMonitorProfile replacement =
            Monitor("external", @"\\.\DISPLAY2", true, 0, 0, 1366, 728, 1);

        WidgetSurfaceLayoutProfile mapped = WidgetTopologyLayoutService.MapToTopology(
            source,
            [oldMonitor],
            [replacement]);

        Assert.Equal("external", mapped.PositionMonitorStableId);
        Assert.Equal(@"\\.\DISPLAY2", mapped.PositionMonitorDeviceName);
        Assert.InRange(mapped.Width, 426, 428);
        Assert.InRange(mapped.Height, 349, 351);
        Assert.InRange(mapped.PositionMarginX, 70, 72);
        Assert.InRange(mapped.PositionMarginY, 55, 57);
        Assert.True(mapped.X >= 0 && mapped.Y >= 0);
        Assert.True(mapped.X + mapped.Width <= replacement.WorkAreaWidth + 1);
        Assert.True(mapped.Y + mapped.Height <= replacement.WorkAreaHeight + 1);
    }

    [Fact]
    public void GroupedWidgets_PersistOneSurfaceAndProjectItToEveryMember()
    {
        WidgetConfig first = CreateWidget();
        WidgetConfig second = CreateWidget();
        second.Id = "widget-2";
        var group = new WidgetGroupConfig
        {
            Id = "group-1",
            SurfaceId = "surface-1",
            MemberIds = [first.Id, second.Id],
            ActiveMemberId = first.Id,
            X = 200,
            Y = 160,
            Width = 600,
            Height = 500,
            BoundsCoordinateVersion = WidgetConfig.CurrentBoundsCoordinateVersion,
            PositionAnchor = WidgetPositionAnchors.LeftTop,
            PositionMarginX = 100,
            PositionMarginY = 80,
            PositionMonitorDeviceName = @"\\.\DISPLAY1",
            PositionMonitorWasPrimary = true
        };
        var settings = new AppSettings
        {
            Widgets = [first, second],
            WidgetGroups = [group]
        };
        var service = new WidgetTopologyLayoutService();
        WidgetDisplayTopologySnapshot highDpi = WidgetTopologyLayoutService.CreateSnapshotForTest(
            Monitor("panel", @"\\.\DISPLAY1", true, 0, 0, 3840, 2080, 2));
        WidgetDisplayTopologySnapshot standardDpi = WidgetTopologyLayoutService.CreateSnapshotForTest(
            Monitor("panel", @"\\.\DISPLAY1", true, 0, 0, 1920, 1040, 1));

        service.Activate(settings, highDpi);
        Assert.True(settings.WidgetTopologyLayouts[highDpi.Key].Surfaces.ContainsKey("surface-1"));
        Assert.False(settings.WidgetTopologyLayouts[highDpi.Key].Surfaces.ContainsKey(first.Id));
        Assert.False(settings.WidgetTopologyLayouts[highDpi.Key].Surfaces.ContainsKey(second.Id));

        service.Activate(settings, standardDpi);

        Assert.Equal(group.X, first.X);
        Assert.Equal(group.Y, first.Y);
        Assert.Equal(group.Width, first.Width);
        Assert.Equal(group.Height, first.Height);
        Assert.Equal(group.X, second.X);
        Assert.Equal(group.Y, second.Y);
        Assert.Equal(group.Width, second.Width);
        Assert.Equal(group.Height, second.Height);
    }

    [Fact]
    public void ProfileActivation_BackfillsStableIdOntoGroupSurfaceAndMembers()
    {
        WidgetConfig first = CreateWidget();
        WidgetConfig second = CreateWidget();
        second.Id = "widget-2";
        var group = new WidgetGroupConfig
        {
            Id = "group-1",
            SurfaceId = "surface-1",
            MemberIds = [first.Id, second.Id],
            ActiveMemberId = first.Id,
            X = 200,
            Y = 160,
            Width = 600,
            Height = 500,
            BoundsCoordinateVersion = WidgetConfig.CurrentBoundsCoordinateVersion,
            PositionAnchor = WidgetPositionAnchors.LeftTop,
            PositionMarginX = 100,
            PositionMarginY = 80,
            PositionMonitorDeviceName = @"\\.\DISPLAY1",
            PositionMonitorWasPrimary = true
        };
        var settings = new AppSettings
        {
            Widgets = [first, second],
            WidgetGroups = [group]
        };
        var service = new WidgetTopologyLayoutService();
        WidgetDisplayTopologySnapshot highDpi = WidgetTopologyLayoutService.CreateSnapshotForTest(
            Monitor("panel", @"\\.\DISPLAY1", true, 0, 0, 3840, 2080, 2));
        WidgetDisplayTopologySnapshot standardDpi = WidgetTopologyLayoutService.CreateSnapshotForTest(
            Monitor("panel", @"\\.\DISPLAY1", true, 0, 0, 1920, 1040, 1));

        service.Activate(settings, highDpi);
        service.Activate(settings, standardDpi);

        // The projection must carry the stable id onto the shared group
        // surface and every member: WidgetPositioningService ranks the stable
        // id above the device name, so a stale/missing id on any of them
        // resolves the surface onto the wrong monitor after a switch.
        Assert.Equal("panel", group.PositionMonitorStableId);
        Assert.Equal("panel", first.PositionMonitorStableId);
        Assert.Equal("panel", second.PositionMonitorStableId);
    }

    private static WidgetConfig CreateWidget() => new()
    {
        Id = "widget-1",
        X = 200,
        Y = 160,
        Width = 600,
        Height = 500,
        BoundsCoordinateVersion = WidgetConfig.CurrentBoundsCoordinateVersion,
        PositionAnchor = WidgetPositionAnchors.LeftTop,
        PositionMarginX = 100,
        PositionMarginY = 80,
        PositionMonitorDeviceName = @"\\.\DISPLAY1",
        PositionMonitorWasPrimary = true,
        PositionMonitorKey = "0:0:3840:2080"
    };

    private static void PlaceOnExternalMonitor(WidgetConfig widget, string deviceName)
    {
        widget.X = 2140;
        widget.Y = 120;
        widget.Width = 720;
        widget.Height = 600;
        widget.PositionAnchor = WidgetPositionAnchors.LeftTop;
        widget.PositionMarginX = 220;
        widget.PositionMarginY = 120;
        widget.PositionMonitorDeviceName = deviceName;
        widget.PositionMonitorWasPrimary = false;
        widget.PositionMonitorKey = "1920:0:1920:1040";
    }

    private static WidgetTopologyMonitorProfile Monitor(
        string stableId,
        string deviceName,
        bool primary,
        int x,
        int y,
        int width,
        int height,
        double scale) => new()
    {
        StableId = stableId,
        DeviceName = deviceName,
        IsPrimary = primary,
        MonitorX = x,
        MonitorY = y,
        MonitorWidth = width,
        MonitorHeight = height,
        WorkAreaX = x,
        WorkAreaY = y,
        WorkAreaWidth = width,
        WorkAreaHeight = height,
        DpiScale = scale
    };
}
