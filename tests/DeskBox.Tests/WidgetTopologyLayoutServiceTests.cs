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

        Assert.StartsWith("v4-", initial.Key, StringComparison.Ordinal);
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
    public void SameTopology_DpiRoundTrip_KeepsEditedIntentAndRealizesPhysicalCache()
    {
        // v4 (D5): resolution/DPI changes stay in ONE profile. The edits made
        // at either DPI survive as intent (DIP); only the physical X/Y cache
        // is re-realized for the current DPI (spec 5.2).
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
        // One profile: the standard-DPI edits persist (no revival of the
        // pre-edit high-DPI arrangement), and the physical cache follows the
        // 2× scale: margin 44 DIP → 88 physical.
        Assert.Equal(720, widget.Width);
        Assert.Equal(620, widget.Height);
        Assert.Equal(44, widget.PositionMarginX);
        Assert.Equal(88, widget.X);
        Assert.Equal(72, widget.Y);

        service.Activate(settings, standardDpi);
        Assert.Equal(720, widget.Width);
        Assert.Equal(620, widget.Height);
        Assert.Equal(44, widget.X);
        Assert.Equal(36, widget.Y);
        Assert.Single(settings.WidgetTopologyLayouts);
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

        // v4 semantics: the physical cache is realized from the anchor at
        // the laptop's scale (margin 100 DIP → 100 physical at 1×), the
        // stored intent keeps the original capture.
        Assert.Equal(100, widget.X);
        Assert.Equal(80, widget.Y);
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

    [Fact]
    public void Activation_NeverFabricatesCompactPlacementForSurfacesWithoutOne()
    {
        // Feedback 340 companion invariant: restore/seeding must never invent
        // a capsule placement for a surface that has none. Placement creation
        // is reserved for user placement commits and the capsule bar
        // arrangement; the first collapse computes transient bounds instead.
        var neverCollapsed = CreateWidget();
        neverCollapsed.CompactPlacement = null;
        var collapsed = CreateWidget();
        collapsed.Id = "widget-collapsed";
        collapsed.CompactPlacement = new WidgetCompactPlacement
        {
            X = 12,
            Y = 910,
            PositionAnchor = WidgetPositionAnchors.LeftBottom,
            PositionMarginX = 12,
            PositionMarginY = 8,
            PositionMonitorDeviceName = @"\\.\DISPLAY1",
            PositionMonitorWasPrimary = true
        };
        var settings = new AppSettings { Widgets = [neverCollapsed, collapsed] };
        var service = new WidgetTopologyLayoutService();
        WidgetDisplayTopologySnapshot highDpi = WidgetTopologyLayoutService.CreateSnapshotForTest(
            Monitor("panel", @"\\.\DISPLAY1", true, 0, 0, 3840, 2080, 2));
        WidgetDisplayTopologySnapshot standardDpi = WidgetTopologyLayoutService.CreateSnapshotForTest(
            Monitor("panel", @"\\.\DISPLAY1", true, 0, 0, 1920, 1040, 1));
        WidgetDisplayTopologySnapshot dual = WidgetTopologyLayoutService.CreateSnapshotForTest(
            Monitor("panel", @"\\.\DISPLAY1", true, 0, 0, 1920, 1040, 1),
            Monitor("external", @"\\.\DISPLAY2", false, 1920, 0, 1920, 1040, 1));

        service.Activate(settings, highDpi);
        service.Activate(settings, standardDpi);
        service.Activate(settings, dual);
        service.Activate(settings, standardDpi);

        Assert.Null(neverCollapsed.CompactPlacement);
        Assert.NotNull(collapsed.CompactPlacement);
        Assert.Equal(
            WidgetPositionAnchors.LeftBottom,
            collapsed.CompactPlacement.PositionAnchor);
    }

    [Fact]
    public void DuplicateDegenerateTokens_SameKeyMetadataChange_SkipsAmbiguousGroupWithoutThrowing()
    {
        // Two same-resolution degenerate monitors collapse onto one geo token
        // under the same v4 key. A metadata-only change must survive the
        // duplicate token (the old token-keyed dictionary threw) and leave
        // the ambiguous entries' stale hints untouched.
        var widget = CreateWidget();
        widget.PositionMonitorKey = "0:0:1920:1040";
        var settings = new AppSettings { Widgets = [widget] };
        var service = new WidgetTopologyLayoutService();
        WidgetTopologyMonitorProfile firstTaskbar =
            Monitor(@"\\.\DISPLAY1", @"\\.\DISPLAY1", true, 0, 0, 1920, 1040, 1);
        firstTaskbar.WorkAreaHeight = 1000;
        WidgetTopologyMonitorProfile secondTaskbar =
            Monitor(@"\\.\DISPLAY2", @"\\.\DISPLAY2", false, 1920, 0, 1920, 1040, 1);
        secondTaskbar.WorkAreaHeight = 1000;
        WidgetDisplayTopologySnapshot initial = WidgetTopologyLayoutService.CreateSnapshotForTest(
            Monitor(@"\\.\DISPLAY1", @"\\.\DISPLAY1", true, 0, 0, 1920, 1040, 1),
            Monitor(@"\\.\DISPLAY2", @"\\.\DISPLAY2", false, 1920, 0, 1920, 1040, 1));
        WidgetDisplayTopologySnapshot withTaskbar = WidgetTopologyLayoutService.CreateSnapshotForTest(
            firstTaskbar,
            secondTaskbar);
        Assert.Equal(initial.Key, withTaskbar.Key);

        Assert.True(service.Activate(settings, initial));
        Assert.True(service.Activate(settings, withTaskbar));

        Assert.Single(settings.WidgetTopologyLayouts);
        // The duplicated geo token group is skipped: the entry keeps its
        // stale hint instead of being re-bound to one arbitrary monitor.
        Assert.Equal("0:0:1920:1040", widget.PositionMonitorKey);
        Assert.Equal(@"\\.\DISPLAY1", widget.PositionMonitorDeviceName);
    }

    [Fact]
    public void DegenerateResolutionChange_NewKeySeedsFromPriorProfile()
    {
        // v4 goal on degenerate monitors: a resolution flip mints a new geo
        // key yet the stored arrangement is reused (margins/anchor), not
        // reset to whatever the runtime config happens to carry. Requires
        // the compatibility signature to ignore geometry on geometry-only
        // monitors.
        var widget = CreateWidget();
        widget.PositionMonitorKey = "0:0:1920:1080";
        var settings = new AppSettings { Widgets = [widget] };
        var service = new WidgetTopologyLayoutService();
        WidgetDisplayTopologySnapshot hd = WidgetTopologyLayoutService.CreateSnapshotForTest(
            Monitor(@"\\.\DISPLAY1", @"\\.\DISPLAY1", true, 0, 0, 1920, 1080, 1));
        WidgetDisplayTopologySnapshot qhd = WidgetTopologyLayoutService.CreateSnapshotForTest(
            Monitor(@"\\.\DISPLAY1", @"\\.\DISPLAY1", true, 0, 0, 2560, 1440, 1));
        Assert.NotEqual(hd.Key, qhd.Key);

        Assert.True(service.Activate(settings, hd));
        widget.PositionMarginX = 44;
        widget.PositionMarginY = 36;
        widget.Width = 720;
        widget.Height = 620;
        widget.X = 44;
        widget.Y = 36;
        service.Activate(settings, hd); // persist the hand placement
        // First-run-after-restore shape: no active key, and the runtime
        // config carries the pre-edit placement instead of the profile's.
        settings.ActiveWidgetTopologyKey = null;
        widget.PositionMarginX = 100;
        widget.PositionMarginY = 80;
        widget.Width = 600;
        widget.Height = 500;
        widget.X = 200;
        widget.Y = 160;

        Assert.True(service.Activate(settings, qhd));

        Assert.Equal(qhd.Key, settings.ActiveWidgetTopologyKey);
        Assert.Equal(2, settings.WidgetTopologyLayouts.Count);
        Assert.Equal(44, widget.PositionMarginX);
        Assert.Equal(36, widget.PositionMarginY);
        Assert.Equal(WidgetPositionAnchors.LeftTop, widget.PositionAnchor);
        Assert.Equal(720, widget.Width);
        Assert.Equal(620, widget.Height);
        Assert.Equal(44, widget.X);
        Assert.Equal(36, widget.Y);
    }

    [Fact]
    public void DualDegenerateResolutionChange_SeedsExternalPlacementFromPriorProfile()
    {
        var widget = CreateWidget();
        widget.PositionMonitorKey = "0:0:1920:1080";
        var settings = new AppSettings { Widgets = [widget] };
        var service = new WidgetTopologyLayoutService();
        WidgetDisplayTopologySnapshot dualHd = WidgetTopologyLayoutService.CreateSnapshotForTest(
            Monitor(@"\\.\DISPLAY1", @"\\.\DISPLAY1", true, 0, 0, 1920, 1080, 1),
            Monitor(@"\\.\DISPLAY2", @"\\.\DISPLAY2", false, 1920, 0, 1920, 1080, 1));
        WidgetDisplayTopologySnapshot dualQhd = WidgetTopologyLayoutService.CreateSnapshotForTest(
            Monitor(@"\\.\DISPLAY1", @"\\.\DISPLAY1", true, 0, 0, 2560, 1440, 1),
            Monitor(@"\\.\DISPLAY2", @"\\.\DISPLAY2", false, 2560, 0, 2560, 1440, 1));
        Assert.NotEqual(dualHd.Key, dualQhd.Key);

        Assert.True(service.Activate(settings, dualHd));
        PlaceOnExternalMonitor(widget, @"\\.\DISPLAY2");
        widget.PositionMonitorKey = "1920:0:1920:1080";
        service.Activate(settings, dualHd); // persist the hand placement
        settings.ActiveWidgetTopologyKey = null;
        widget.X = 200;
        widget.Y = 160;
        widget.Width = 600;
        widget.Height = 500;
        widget.PositionMarginX = 100;
        widget.PositionMarginY = 80;
        widget.PositionMonitorDeviceName = @"\\.\DISPLAY1";
        widget.PositionMonitorWasPrimary = true;
        widget.PositionMonitorKey = "0:0:1920:1080";

        Assert.True(service.Activate(settings, dualQhd));

        Assert.Equal(dualQhd.Key, settings.ActiveWidgetTopologyKey);
        Assert.Equal(2, settings.WidgetTopologyLayouts.Count);
        // The external-monitor placement survives the resolution change.
        Assert.Equal(220, widget.PositionMarginX);
        Assert.Equal(120, widget.PositionMarginY);
        Assert.Equal(WidgetPositionAnchors.LeftTop, widget.PositionAnchor);
        Assert.Equal(720, widget.Width);
        Assert.Equal(600, widget.Height);
        Assert.Equal(@"\\.\DISPLAY2", widget.PositionMonitorDeviceName);
    }

    [Fact]
    public void DegenerateSet_DifferentMonitorCount_IsNotCompatible()
    {
        var widget = CreateWidget();
        widget.PositionMonitorKey = "0:0:1920:1080";
        var settings = new AppSettings { Widgets = [widget] };
        var service = new WidgetTopologyLayoutService();
        WidgetDisplayTopologySnapshot single = WidgetTopologyLayoutService.CreateSnapshotForTest(
            Monitor(@"\\.\DISPLAY1", @"\\.\DISPLAY1", true, 0, 0, 1920, 1080, 1));
        WidgetDisplayTopologySnapshot dual = WidgetTopologyLayoutService.CreateSnapshotForTest(
            Monitor(@"\\.\DISPLAY1", @"\\.\DISPLAY1", true, 0, 0, 1920, 1080, 1),
            Monitor(@"\\.\DISPLAY2", @"\\.\DISPLAY2", false, 1920, 0, 1920, 1080, 1));
        Assert.NotEqual(single.Key, dual.Key);

        Assert.True(service.Activate(settings, single));
        widget.PositionMarginX = 44;
        widget.PositionMarginY = 36;
        service.Activate(settings, single); // persist the hand placement
        settings.ActiveWidgetTopologyKey = null;
        widget.PositionMarginX = 100;
        widget.PositionMarginY = 80;

        Assert.True(service.Activate(settings, dual));

        // 1 vs 2 degenerate monitors: a different display COUNT never counts
        // as a compatible profile — the dual set captures fresh instead of
        // inheriting the single-monitor pin.
        Assert.Equal(2, settings.WidgetTopologyLayouts.Count);
        Assert.Equal(100, widget.PositionMarginX);
        Assert.Equal(80, widget.PositionMarginY);
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
