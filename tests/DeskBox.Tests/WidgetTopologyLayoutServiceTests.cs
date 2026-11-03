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
    public void ReplacementMonitor_SeedCarriesTheUnscaledAuthoredReference()
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
        Assert.Equal(600, mapped.Width);
        Assert.Equal(500, mapped.Height);
        Assert.Equal(100, mapped.PositionMarginX);
        Assert.Equal(80, mapped.PositionMarginY);
        Assert.NotNull(mapped.Reference);
        Assert.Equal(1920, mapped.Reference.FrameWidth);
        Assert.Equal(1040, mapped.Reference.FrameHeight);
        Assert.Equal(600, mapped.Reference.Width);
        Assert.Equal(100, mapped.Reference.PositionMarginX);
        Assert.True(mapped.X >= 0 && mapped.Y >= 0);
        Assert.True(mapped.X + mapped.Width <= replacement.WorkAreaWidth + 1);
        Assert.True(mapped.Y + mapped.Height <= replacement.WorkAreaHeight + 1);
    }

    [Fact]
    public void SameMonitorResolutionGrow_StretchesWidgetsKeepsSpacingAndRoundTripsExactly()
    {
        WidgetConfig left = CreateRowWidget("left", WidgetPositionAnchors.LeftTop, 24);
        WidgetConfig middle = CreateRowWidget("middle", WidgetPositionAnchors.LeftTop, 640);
        WidgetConfig right = CreateRowWidget("right", WidgetPositionAnchors.RightTop, 24);
        var settings = new AppSettings { Widgets = [left, middle, right] };
        var service = new WidgetTopologyLayoutService();
        WidgetDisplayTopologySnapshot hd = WidgetTopologyLayoutService.CreateSnapshotForTest(
            Monitor("panel", @"\\.\DISPLAY1", true, 0, 0, 1920, 1040, 1));
        WidgetDisplayTopologySnapshot uhd = WidgetTopologyLayoutService.CreateSnapshotForTest(
            Monitor("panel", @"\\.\DISPLAY1", true, 0, 0, 3840, 2080, 1));
        Assert.Equal(hd.Key, uhd.Key);

        settings.WidgetLayout.WidgetDisplayScalingMode = SettingsService.WidgetDisplayScalingElasticScale;
        service.Activate(settings, hd);
        service.Activate(settings, uhd);

        double widthScale = (3840 - 120) / 1800.0;
        double heightScale = (2080 - 40) / 1000.0;
        Assert.All(new[] { left, middle, right }, widget =>
        {
            Assert.Equal(600 * widthScale, widget.Width, 6);
            Assert.Equal(500 * heightScale, widget.Height, 6);
            Assert.Equal(40, widget.PositionMarginY, 6);
        });
        Assert.Equal(24, left.PositionMarginX, 6);
        Assert.Equal(24, right.PositionMarginX, 6);
        Assert.Equal(16, middle.PositionMarginX - (left.PositionMarginX + left.Width), 6);
        Assert.Equal(
            56,
            3840 - right.PositionMarginX - right.Width - (middle.PositionMarginX + middle.Width),
            6);

        settings.WidgetLayout.WidgetDisplayScalingMode = SettingsService.WidgetDisplayScalingElasticScale;
        service.Activate(settings, hd);

        Assert.All(new[] { left, middle, right }, widget =>
        {
            Assert.Equal(600, widget.Width);
            Assert.Equal(500, widget.Height);
            Assert.Equal(40, widget.PositionMarginY);
        });
        Assert.Equal(24, left.PositionMarginX);
        Assert.Equal(640, middle.PositionMarginX);
        Assert.Equal(24, right.PositionMarginX);
        Assert.Equal(1920 - 24 - 600, right.X);
    }

    [Fact]
    public void SmallerRemoteDisplay_CompressesOpenAreaInsteadOfShrinkingWidgets()
    {
        WidgetConfig inner = CreateRowWidget("inner", WidgetPositionAnchors.RightTop, 440, width: 400, height: 300);
        WidgetConfig outer = CreateRowWidget("outer", WidgetPositionAnchors.RightTop, 24, width: 400, height: 300);
        var settings = new AppSettings { Widgets = [inner, outer] };
        var service = new WidgetTopologyLayoutService();
        WidgetDisplayTopologySnapshot console = WidgetTopologyLayoutService.CreateSnapshotForTest(
            Monitor("panel", @"\\.\DISPLAY1", true, 0, 0, 3840, 2080, 1.5));
        WidgetDisplayTopologySnapshot remote = WidgetTopologyLayoutService.CreateSnapshotForTest(
            Monitor("rdp", @"\\.\DISPLAY1", true, 0, 0, 1920, 1040, 1));

        service.Activate(settings, console);
        service.Activate(settings, remote);

        Assert.All(new[] { inner, outer }, widget =>
        {
            Assert.Equal(400, widget.Width, 6);
            Assert.Equal(300, widget.Height, 6);
            Assert.Equal(40, widget.PositionMarginY, 6);
        });
        Assert.Equal(24, outer.PositionMarginX, 6);
        Assert.Equal(440, inner.PositionMarginX, 6);

        service.Activate(settings, console);

        Assert.Equal(400, inner.Width);
        Assert.Equal(440, inner.PositionMarginX);
        Assert.Equal(24, outer.PositionMarginX);
    }

    [Fact]
    public void PlacementOnStretchedDisplay_IsStoredInTheReferenceFrame()
    {
        WidgetConfig widget = CreateRowWidget("solo", WidgetPositionAnchors.LeftTop, 24);
        var settings = new AppSettings { Widgets = [widget] };
        var service = new WidgetTopologyLayoutService();
        WidgetDisplayTopologySnapshot hd = WidgetTopologyLayoutService.CreateSnapshotForTest(
            Monitor("panel", @"\\.\DISPLAY1", true, 0, 0, 1920, 1040, 1));
        WidgetDisplayTopologySnapshot uhd = WidgetTopologyLayoutService.CreateSnapshotForTest(
            Monitor("panel", @"\\.\DISPLAY1", true, 0, 0, 3840, 2080, 1));

        settings.WidgetLayout.WidgetDisplayScalingMode = SettingsService.WidgetDisplayScalingElasticScale;
        service.Activate(settings, hd);
        service.Activate(settings, uhd);
        double widthScale = (3840 - 24) / (600.0 + 1296);
        Assert.Equal(600 * widthScale, widget.Width, 6);

        widget.Width = 1000;
        service.Activate(settings, uhd);
        Assert.Equal(1000, widget.Width);

        settings.WidgetLayout.WidgetDisplayScalingMode = SettingsService.WidgetDisplayScalingElasticScale;
        service.Activate(settings, hd);

        Assert.Equal(1000 / widthScale, widget.Width, 6);
        Assert.Equal(24, widget.PositionMarginX, 6);
    }

    [Fact]
    public void FixedContentWidget_GrowthIsCapped()
    {
        WidgetConfig weather = CreateRowWidget("weather", WidgetPositionAnchors.LeftTop, 24);
        weather.WidgetKind = WidgetKind.Weather;
        var settings = new AppSettings { Widgets = [weather] };
        var service = new WidgetTopologyLayoutService();

        settings.WidgetLayout.WidgetDisplayScalingMode = SettingsService.WidgetDisplayScalingElasticScale;
        service.Activate(settings, WidgetTopologyLayoutService.CreateSnapshotForTest(
            Monitor("panel", @"\\.\DISPLAY1", true, 0, 0, 1920, 1040, 1)));
        settings.WidgetLayout.WidgetDisplayScalingMode = SettingsService.WidgetDisplayScalingElasticScale;
        service.Activate(settings, WidgetTopologyLayoutService.CreateSnapshotForTest(
            Monitor("panel", @"\\.\DISPLAY1", true, 0, 0, 3840, 2080, 1)));

        Assert.Equal(900, weather.Width, 6);
        Assert.Equal(750, weather.Height, 6);
        Assert.Equal(24, weather.PositionMarginX, 6);
    }

    [Fact]
    public void CapsulePlacement_FollowsTheElasticMapWithoutChangingItsAnchor()
    {
        WidgetConfig widget = CreateRowWidget("capsule", WidgetPositionAnchors.LeftTop, 24);
        widget.CompactPlacement = new WidgetCompactPlacement
        {
            PositionAnchor = WidgetPositionAnchors.LeftTop,
            PositionMarginX = 24,
            PositionMarginY = 700,
            PositionMonitorDeviceName = @"\\.\DISPLAY1",
            PositionMonitorWasPrimary = true
        };
        var settings = new AppSettings { Widgets = [widget] };
        var service = new WidgetTopologyLayoutService();

        settings.WidgetLayout.WidgetDisplayScalingMode = SettingsService.WidgetDisplayScalingElasticScale;
        service.Activate(settings, WidgetTopologyLayoutService.CreateSnapshotForTest(
            Monitor("panel", @"\\.\DISPLAY1", true, 0, 0, 1920, 1040, 1)));
        settings.WidgetLayout.WidgetDisplayScalingMode = SettingsService.WidgetDisplayScalingElasticScale;
        service.Activate(settings, WidgetTopologyLayoutService.CreateSnapshotForTest(
            Monitor("panel", @"\\.\DISPLAY1", true, 0, 0, 3840, 2080, 1)));

        double heightScale = (2080 - 40) / 1000.0;
        Assert.NotNull(widget.CompactPlacement);
        Assert.Equal(WidgetPositionAnchors.LeftTop, widget.CompactPlacement.PositionAnchor);
        Assert.Equal(24, widget.CompactPlacement.PositionMarginX, 6);
        Assert.Equal(40 + 500 * heightScale + (700 - 540) * heightScale, widget.CompactPlacement.PositionMarginY, 6);
    }

    [Fact]
    public void UpgradeProfile_DerivesReferencesAndDropsSeededEntriesOfInactiveProfiles()
    {
        WidgetSurfaceLayoutProfile Entry(bool? authoritative) => new()
        {
            PositionMonitorStableId = "panel",
            PositionMonitorDeviceName = @"\\.\DISPLAY1",
            PositionAnchor = WidgetPositionAnchors.LeftTop,
            PositionMarginX = 24,
            PositionMarginY = 40,
            Width = 600,
            Height = 500,
            IsAuthoritative = authoritative
        };
        WidgetTopologyLayoutProfile Profile() => new()
        {
            Version = 1,
            Monitors = [Monitor("panel", @"\\.\DISPLAY1", true, 0, 0, 3840, 2080, 2)],
            Surfaces = new Dictionary<string, WidgetSurfaceLayoutProfile>
            {
                ["authored"] = Entry(true),
                ["seeded"] = Entry(false)
            }
        };
        WidgetTopologyLayoutProfile active = Profile();
        WidgetTopologyLayoutProfile inactive = Profile();

        Assert.True(WidgetTopologyLayoutService.UpgradeProfile("active", active, "active"));
        Assert.True(WidgetTopologyLayoutService.UpgradeProfile("inactive", inactive, "active"));
        Assert.False(WidgetTopologyLayoutService.UpgradeProfile("inactive", inactive, "active"));

        Assert.Equal(2, active.Surfaces.Count);
        Assert.Equal(new[] { "authored" }, inactive.Surfaces.Keys);
        WidgetSurfaceLayoutReference? reference = inactive.Surfaces["authored"].Reference;
        Assert.NotNull(reference);
        Assert.Equal(1920, reference.FrameWidth);
        Assert.Equal(1040, reference.FrameHeight);
        Assert.Equal(600, reference.Width);
        Assert.Equal(WidgetTopologyLayoutProfile.CurrentVersion, inactive.Version);
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
        settings.WidgetLayout.WidgetDisplayScalingMode = SettingsService.WidgetDisplayScalingElasticScale;

        Assert.True(service.Activate(settings, hd));
        widget.PositionMarginX = 44;
        widget.PositionMarginY = 36;
        widget.Width = 720;
        widget.Height = 620;
        widget.X = 44;
        widget.Y = 36;
        settings.WidgetLayout.WidgetDisplayScalingMode = SettingsService.WidgetDisplayScalingElasticScale;
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
        Assert.Equal(44, widget.PositionMarginX, 6);
        Assert.Equal(36, widget.PositionMarginY, 6);
        Assert.Equal(WidgetPositionAnchors.LeftTop, widget.PositionAnchor);
        Assert.Equal(720 * (2560 - 44) / (720.0 + 1156), widget.Width, 6);
        Assert.Equal(620 * (1440 - 36) / (620.0 + 424), widget.Height, 6);
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
        settings.WidgetLayout.WidgetDisplayScalingMode = SettingsService.WidgetDisplayScalingElasticScale;

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
        Assert.Equal(220 * 4 / 3.0, widget.PositionMarginX, 6);
        Assert.Equal(120 * 4 / 3.0, widget.PositionMarginY, 6);
        Assert.Equal(WidgetPositionAnchors.LeftTop, widget.PositionAnchor);
        Assert.Equal(960, widget.Width, 6);
        Assert.Equal(800, widget.Height, 6);
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

    [Fact]
    public void SevereShrinkBeyondPreferredFloor_KeepsWidgetsDisjointWithinWorkArea()
    {
        // Full-width 4K 3-column grid onto a 1366 remote display: even the
        // preferred 0.5 floor cannot fit, so widgets keep shrinking below it
        // until the axis exactly fills — butt-joined, never overlapping.
        double column = (3840 - 80) / 3.0;
        WidgetConfig left = CreateRowWidget("left", WidgetPositionAnchors.LeftTop, 24, width: column);
        WidgetConfig middle = CreateRowWidget("middle", WidgetPositionAnchors.LeftTop, 40 + column, width: column);
        WidgetConfig right = CreateRowWidget("right", WidgetPositionAnchors.RightTop, 24, width: column);
        var settings = new AppSettings { Widgets = [left, middle, right] };
        var service = new WidgetTopologyLayoutService();
        WidgetDisplayTopologySnapshot uhd = WidgetTopologyLayoutService.CreateSnapshotForTest(
            Monitor("panel", @"\\.\DISPLAY1", true, 0, 0, 3840, 2080, 1));
        WidgetDisplayTopologySnapshot small = WidgetTopologyLayoutService.CreateSnapshotForTest(
            Monitor("panel", @"\\.\DISPLAY1", true, 0, 0, 1366, 728, 1));
        Assert.Equal(uhd.Key, small.Key);

        service.Activate(settings, uhd);
        service.Activate(settings, small);

        Assert.All(new[] { left, middle, right }, widget =>
        {
            Assert.Equal(1366 / 3.0, widget.Width, 4);
            Assert.Equal(500, widget.Height, 6);
            Assert.Equal(40, widget.PositionMarginY, 6);
        });
        Assert.Equal(0, left.PositionMarginX, 6);
        Assert.Equal(1366 / 3.0, middle.PositionMarginX, 4);
        Assert.Equal(0, right.PositionMarginX, 6);
        double leftRight = left.PositionMarginX + left.Width;
        double middleRight = middle.PositionMarginX + middle.Width;
        double rightLeft = 1366 - right.PositionMarginX - right.Width;
        Assert.True(leftRight <= middle.PositionMarginX + 1e-6);
        Assert.True(middleRight <= rightLeft + 1e-6);
    }

    [Fact]
    public void PureDpiFlip_SameKey_ReflowsAndRoundTrips()
    {
        WidgetConfig left = CreateRowWidget("left", WidgetPositionAnchors.LeftTop, 24);
        WidgetConfig right = CreateRowWidget("right", WidgetPositionAnchors.RightTop, 24);
        var settings = new AppSettings { Widgets = [left, right] };
        var service = new WidgetTopologyLayoutService();
        WidgetDisplayTopologySnapshot dpi1 = WidgetTopologyLayoutService.CreateSnapshotForTest(
            Monitor("panel", @"\\.\DISPLAY1", true, 0, 0, 1920, 1040, 1));
        WidgetDisplayTopologySnapshot dpi15 = WidgetTopologyLayoutService.CreateSnapshotForTest(
            Monitor("panel", @"\\.\DISPLAY1", true, 0, 0, 1920, 1040, 1.5));
        Assert.Equal(dpi1.Key, dpi15.Key);

        service.Activate(settings, dpi1);
        service.Activate(settings, dpi15);

        // Effective width 1920 -> 1280: open gap compresses to the threshold,
        // widgets shrink just past it, spacing margins stay literal.
        double shrunkWidth = 600 * (1168 / 1200.0);
        Assert.Equal(shrunkWidth, left.Width, 6);
        Assert.Equal(shrunkWidth, right.Width, 6);
        Assert.Equal(24, left.PositionMarginX, 6);
        Assert.Equal(24, right.PositionMarginX, 6);
        Assert.Equal(64, 1280 - right.PositionMarginX - right.Width - (left.PositionMarginX + left.Width), 6);
        Assert.Equal(500, left.Height, 6);
        Assert.Equal(40, left.PositionMarginY, 6);

        service.Activate(settings, dpi1);

        Assert.Equal(600, left.Width);
        Assert.Equal(500, left.Height);
        Assert.Equal(24, left.PositionMarginX);
        Assert.Equal(40, left.PositionMarginY);
        Assert.Equal(600, right.Width);
        Assert.Equal(24, right.PositionMarginX);
    }

    [Fact]
    public void MixedResolutionMonitors_EachGetsItsOwnPlan()
    {
        WidgetConfig onPanel = CreateRowWidget("panel-widget", WidgetPositionAnchors.LeftTop, 24);
        onPanel.PositionMonitorKey = "0:0:1920:1040";
        WidgetConfig onExternal = CreateRowWidget("ext-widget", WidgetPositionAnchors.LeftTop, 24);
        onExternal.PositionMonitorDeviceName = @"\\.\DISPLAY2";
        onExternal.PositionMonitorWasPrimary = false;
        onExternal.PositionMonitorKey = "1920:0:1920:1040";
        var settings = new AppSettings { Widgets = [onPanel, onExternal] };
        settings.WidgetLayout.WidgetDisplayScalingMode = SettingsService.WidgetDisplayScalingElasticScale;
        var service = new WidgetTopologyLayoutService();
        WidgetDisplayTopologySnapshot dualHd = WidgetTopologyLayoutService.CreateSnapshotForTest(
            Monitor("panel", @"\\.\DISPLAY1", true, 0, 0, 1920, 1040, 1),
            Monitor("external", @"\\.\DISPLAY2", false, 1920, 0, 1920, 1040, 1));
        WidgetDisplayTopologySnapshot dualMixed = WidgetTopologyLayoutService.CreateSnapshotForTest(
            Monitor("panel", @"\\.\DISPLAY1", true, 0, 0, 2560, 1360, 1),
            Monitor("external", @"\\.\DISPLAY2", false, 2560, 0, 1280, 640, 1));
        Assert.Equal(dualHd.Key, dualMixed.Key);

        service.Activate(settings, dualHd);
        service.Activate(settings, dualMixed);

        // Panel grows 1920 -> 2560 (widgets stretch with its open area);
        // external shrinks 1920 -> 1280 (open area absorbs it, widgets keep
        // their DIP size). A shared plan would scale both identically.
        Assert.Equal(600 * (2536 / 1896.0), onPanel.Width, 6);
        Assert.Equal(500 * (1320 / 1000.0), onPanel.Height, 6);
        Assert.Equal(600, onExternal.Width, 6);
        Assert.Equal(500, onExternal.Height, 6);
        Assert.Equal(24, onPanel.PositionMarginX, 6);
        Assert.Equal(24, onExternal.PositionMarginX, 6);
    }

    [Fact]
    public void SevereShrinkWithNarrowWidget_NeverClampsIntoNeighbours()
    {
        // 1920 @ 100% full row (896 / 100 / 844, gaps 16, margins 24) onto
        // the same panel at 250% scale (effective width 768): the narrow
        // widget projects to 41.74 DIP, below the preferred 50 minimum. The
        // adaptive minimum keeps the projection clamp from inflating it into
        // its right neighbour (spec 5.2 rule 4).
        WidgetConfig left = CreateRowWidget("left", WidgetPositionAnchors.LeftTop, 24, width: 896);
        WidgetConfig narrow = CreateRowWidget("narrow", WidgetPositionAnchors.LeftTop, 24 + 896 + 16, width: 100);
        WidgetConfig right = CreateRowWidget("right", WidgetPositionAnchors.RightTop, 24, width: 844);
        var settings = new AppSettings { Widgets = [left, narrow, right] };
        var service = new WidgetTopologyLayoutService();
        WidgetDisplayTopologySnapshot scale100 = WidgetTopologyLayoutService.CreateSnapshotForTest(
            Monitor("panel", @"\\.\DISPLAY1", true, 0, 0, 1920, 1040, 1));
        WidgetDisplayTopologySnapshot scale250 = WidgetTopologyLayoutService.CreateSnapshotForTest(
            Monitor("panel", @"\\.\DISPLAY1", true, 0, 0, 1920, 1040, 2.5));
        Assert.Equal(scale100.Key, scale250.Key);

        service.Activate(settings, scale100);
        service.Activate(settings, scale250);

        double scale = 768 / 1840.0;
        Assert.Equal(896 * scale, left.Width, 4);
        Assert.Equal(100 * scale, narrow.Width, 4);
        Assert.Equal(844 * scale, right.Width, 4);
        Assert.Equal(0, left.PositionMarginX, 6);
        Assert.Equal(left.Width, narrow.PositionMarginX, 4);
        double narrowRight = narrow.PositionMarginX + narrow.Width;
        double rightLeft = 768 - right.PositionMarginX - right.Width;
        Assert.True(narrowRight <= rightLeft + 1e-6);
    }

    [Fact]
    public void SevereShrinkNarrowPairOnProjector_StayDisjointBelowPreferredMinimum()
    {
        // 4K full row [1772, 100, 100, 1772] onto a 1366 projector: the two
        // narrow widgets project to 36.49 DIP each and must stay disjoint.
        WidgetConfig a = CreateRowWidget("a", WidgetPositionAnchors.LeftTop, 24, width: 1772);
        WidgetConfig b = CreateRowWidget("b", WidgetPositionAnchors.LeftTop, 24 + 1772 + 16, width: 100);
        WidgetConfig c = CreateRowWidget("c", WidgetPositionAnchors.LeftTop, 24 + 1772 + 16 + 100 + 16, width: 100);
        WidgetConfig d = CreateRowWidget("d", WidgetPositionAnchors.RightTop, 24, width: 1772);
        var settings = new AppSettings { Widgets = [a, b, c, d] };
        var service = new WidgetTopologyLayoutService();
        WidgetDisplayTopologySnapshot uhd = WidgetTopologyLayoutService.CreateSnapshotForTest(
            Monitor("panel", @"\\.\DISPLAY1", true, 0, 0, 3840, 2080, 1));
        WidgetDisplayTopologySnapshot small = WidgetTopologyLayoutService.CreateSnapshotForTest(
            Monitor("panel", @"\\.\DISPLAY1", true, 0, 0, 1366, 728, 1));
        Assert.Equal(uhd.Key, small.Key);

        service.Activate(settings, uhd);
        service.Activate(settings, small);

        double scale = 1366 / 3744.0;
        Assert.Equal(100 * scale, b.Width, 4);
        Assert.Equal(100 * scale, c.Width, 4);
        Assert.True(
            b.PositionMarginX + b.Width <= c.PositionMarginX + 1e-6,
            "narrow pair must stay disjoint");
        double cRight = c.PositionMarginX + c.Width;
        double dLeft = 1366 - d.PositionMarginX - d.Width;
        Assert.True(cRight <= dLeft + 1e-6, "narrow pair must not clamp into the right column");
    }

    [Fact]
    public void DegenerateTinyWorkArea_TwoWidgetsStayDisjoint()
    {
        // Terminal state (spec 5.2 step 4 tail): a 40-DIP work area cannot
        // fit two 50-DIP widgets even fully shrunk; the adaptive minimum
        // follows the plan down so the clamp never stacks them.
        WidgetConfig first = CreateRowWidget("first", WidgetPositionAnchors.LeftTop, 0, width: 50);
        WidgetConfig second = CreateRowWidget("second", WidgetPositionAnchors.LeftTop, 50, width: 50);
        var settings = new AppSettings { Widgets = [first, second] };
        var service = new WidgetTopologyLayoutService();
        WidgetDisplayTopologySnapshot wide = WidgetTopologyLayoutService.CreateSnapshotForTest(
            Monitor("panel", @"\\.\DISPLAY1", true, 0, 0, 1920, 1040, 1));
        WidgetDisplayTopologySnapshot tiny = WidgetTopologyLayoutService.CreateSnapshotForTest(
            Monitor("panel", @"\\.\DISPLAY1", true, 0, 0, 40, 728, 1));
        Assert.Equal(wide.Key, tiny.Key);

        service.Activate(settings, wide);
        service.Activate(settings, tiny);

        Assert.Equal(20, first.Width, 4);
        Assert.Equal(20, second.Width, 4);
        Assert.Equal(0, first.PositionMarginX, 4);
        Assert.Equal(20, second.PositionMarginX, 4);
    }

    [Fact]
    public void QuantizedRoundTripThroughSevereState_PreservesAuthoredMargins()
    {
        // Window creation quantizes the projected geometry to whole pixels
        // (455.33 -> 455). Flipping back to the reference frame must treat
        // that noise as "unchanged" and keep the authored margins instead of
        // inverting the compressed (zero-spacing) geometry into the
        // reference frame.
        double column = (3840 - 80) / 3.0;
        WidgetConfig left = CreateRowWidget("left", WidgetPositionAnchors.LeftTop, 24, width: column);
        WidgetConfig middle = CreateRowWidget("middle", WidgetPositionAnchors.LeftTop, 40 + column, width: column);
        WidgetConfig right = CreateRowWidget("right", WidgetPositionAnchors.RightTop, 24, width: column);
        var settings = new AppSettings { Widgets = [left, middle, right] };
        var service = new WidgetTopologyLayoutService();
        WidgetDisplayTopologySnapshot uhd = WidgetTopologyLayoutService.CreateSnapshotForTest(
            Monitor("panel", @"\\.\DISPLAY1", true, 0, 0, 3840, 2080, 1));
        WidgetDisplayTopologySnapshot small = WidgetTopologyLayoutService.CreateSnapshotForTest(
            Monitor("panel", @"\\.\DISPLAY1", true, 0, 0, 1366, 728, 1));
        Assert.Equal(uhd.Key, small.Key);

        service.Activate(settings, uhd);
        service.Activate(settings, small);

        // Simulate the whole-pixel quantization window creation writes back.
        left.Width = Math.Floor(left.Width);
        middle.Width = Math.Floor(middle.Width);
        right.Width = Math.Floor(right.Width);
        middle.PositionMarginX = Math.Floor(middle.PositionMarginX);

        service.Activate(settings, uhd);

        Assert.Equal(24, left.PositionMarginX, 6);
        Assert.Equal(40 + column, middle.PositionMarginX, 6);
        Assert.Equal(24, right.PositionMarginX, 6);
        Assert.Equal(column, left.Width, 4);
        Assert.Equal(column, middle.Width, 4);
        Assert.Equal(column, right.Width, 4);
    }

    [Fact]
    public void SubDipEditInSevereState_IsAbsorbedByCaptureTolerance()
    {
        // A sub-DIP nudge is below the capture tolerance: treated as noise,
        // the authored reference survives the round trip unchanged.
        double column = (3840 - 80) / 3.0;
        WidgetConfig left = CreateRowWidget("left", WidgetPositionAnchors.LeftTop, 24, width: column);
        WidgetConfig middle = CreateRowWidget("middle", WidgetPositionAnchors.LeftTop, 40 + column, width: column);
        WidgetConfig right = CreateRowWidget("right", WidgetPositionAnchors.RightTop, 24, width: column);
        var settings = new AppSettings { Widgets = [left, middle, right] };
        var service = new WidgetTopologyLayoutService();
        WidgetDisplayTopologySnapshot uhd = WidgetTopologyLayoutService.CreateSnapshotForTest(
            Monitor("panel", @"\\.\DISPLAY1", true, 0, 0, 3840, 2080, 1));
        WidgetDisplayTopologySnapshot small = WidgetTopologyLayoutService.CreateSnapshotForTest(
            Monitor("panel", @"\\.\DISPLAY1", true, 0, 0, 1366, 728, 1));

        service.Activate(settings, uhd);
        service.Activate(settings, small);

        middle.PositionMarginX += 0.5;

        service.Activate(settings, uhd);

        Assert.Equal(24, left.PositionMarginX, 6);
        Assert.Equal(40 + column, middle.PositionMarginX, 6);
        Assert.Equal(24, right.PositionMarginX, 6);
        Assert.Equal(column, middle.Width, 4);
    }

    [Fact]
    public void MeaningfulEditInSevereState_InvertsThroughTheCompressedPlan()
    {
        // Drags of >= 1 DIP re-capture through the inversion: the margin
        // comes back amplified by 1/ws (compressed-plan semantics, spec 5.2
        // capture trade-off #2) while unedited surfaces restore exactly.
        double column = (3840 - 80) / 3.0;
        WidgetConfig left = CreateRowWidget("left", WidgetPositionAnchors.LeftTop, 24, width: column);
        WidgetConfig middle = CreateRowWidget("middle", WidgetPositionAnchors.LeftTop, 40 + column, width: column);
        WidgetConfig right = CreateRowWidget("right", WidgetPositionAnchors.RightTop, 24, width: column);
        var settings = new AppSettings { Widgets = [left, middle, right] };
        var service = new WidgetTopologyLayoutService();
        WidgetDisplayTopologySnapshot uhd = WidgetTopologyLayoutService.CreateSnapshotForTest(
            Monitor("panel", @"\\.\DISPLAY1", true, 0, 0, 3840, 2080, 1));
        WidgetDisplayTopologySnapshot small = WidgetTopologyLayoutService.CreateSnapshotForTest(
            Monitor("panel", @"\\.\DISPLAY1", true, 0, 0, 1366, 728, 1));

        service.Activate(settings, uhd);
        service.Activate(settings, small);

        const double drag = 40;
        middle.PositionMarginX += drag;

        service.Activate(settings, uhd);

        double ws = 1366 / 3760.0;
        Assert.Equal(40 + column + drag / ws, middle.PositionMarginX, 2);
        Assert.Equal(24, left.PositionMarginX, 6);
        Assert.Equal(24, right.PositionMarginX, 6);
    }

    [Fact]
    public void FixedSizeDefault_MonitorOrTaskbarGrow_KeepsAuthoredSizeAndCornerMargins()
    {
        WidgetConfig left = CreateRowWidget("left", WidgetPositionAnchors.LeftTop, 24, width: 320, height: 260);
        WidgetConfig right = CreateRowWidget("right", WidgetPositionAnchors.RightBottom, 32, width: 340, height: 280);
        var settings = new AppSettings { Widgets = [left, right] };
        var service = new WidgetTopologyLayoutService();
        WidgetDisplayTopologySnapshot hd = WidgetTopologyLayoutService.CreateSnapshotForTest(
            Monitor("panel", @"\\.\DISPLAY1", true, 0, 0, 1920, 1040, 1));
        WidgetDisplayTopologySnapshot qhd = WidgetTopologyLayoutService.CreateSnapshotForTest(
            Monitor("panel", @"\\.\DISPLAY1", true, 0, 0, 2560, 1440, 1));

        service.Activate(settings, hd);
        service.Activate(settings, qhd);

        Assert.Equal(320, left.Width, 6);
        Assert.Equal(260, left.Height, 6);
        Assert.Equal(24, left.PositionMarginX, 6);
        Assert.Equal(40, left.PositionMarginY, 6);
        Assert.Equal(340, right.Width, 6);
        Assert.Equal(280, right.Height, 6);
        Assert.Equal(32, right.PositionMarginX, 6);
        Assert.Equal(2560 - 32 - 340, right.X);
        Assert.Equal(1440 - 40 - 280, right.Y);
    }

    [Fact]
    public void DisconnectedHigherResMonitor_FourBorrowedWidgetsDoNotWarpTwoNativeWidgets()
    {
        WidgetConfig nativeA = CreateRowWidget("native-a", WidgetPositionAnchors.LeftTop, 24, width: 360, height: 300);
        nativeA.ScreenBindingMode = WidgetScreenBindingMode.Pinned;
        nativeA.BoundScreenId = "laptop";
        nativeA.PositionMonitorStableId = "laptop";
        nativeA.PositionMonitorKey = "0:0:1920:1040";

        WidgetConfig nativeB = CreateRowWidget("native-b", WidgetPositionAnchors.LeftBottom, 24, width: 380, height: 320);
        nativeB.ScreenBindingMode = WidgetScreenBindingMode.Pinned;
        nativeB.BoundScreenId = "laptop";
        nativeB.PositionMonitorStableId = "laptop";
        nativeB.PositionMonitorKey = "0:0:1920:1040";

        List<WidgetConfig> borrowed = [];
        for (int i = 0; i < 4; i++)
        {
            WidgetConfig ext = CreateRowWidget($"ext-{i}", WidgetPositionAnchors.LeftTop, 40 + i * 520, width: 480, height: 420);
            ext.ScreenBindingMode = WidgetScreenBindingMode.Pinned;
            ext.BoundScreenId = "external-qhd";
            ext.PositionMonitorStableId = "external-qhd";
            ext.PositionMonitorDeviceName = @"\\.\DISPLAY2";
            ext.PositionMonitorWasPrimary = false;
            ext.PositionMonitorKey = "1920:0:2560:1400";
            ext.X = 1920 + 40 + i * 520;
            borrowed.Add(ext);
        }

        var settings = new AppSettings { Widgets = [nativeA, nativeB, .. borrowed] };
        var service = new WidgetTopologyLayoutService();
        WidgetDisplayTopologySnapshot dual = WidgetTopologyLayoutService.CreateSnapshotForTest(
            Monitor("laptop", @"\\.\DISPLAY1", true, 0, 0, 1920, 1040, 1),
            Monitor("external-qhd", @"\\.\DISPLAY2", false, 1920, 0, 2560, 1400, 1));
        WidgetDisplayTopologySnapshot singleLaptop = WidgetTopologyLayoutService.CreateSnapshotForTest(
            Monitor("laptop", @"\\.\DISPLAY1", true, 0, 0, 1920, 1040, 1));

        service.Activate(settings, dual);
        service.Activate(settings, singleLaptop);

        // Native laptop widgets must remain 100% untouched even though the 4
        // borrowed 2K widgets outnumber them 4:2 on the laptop display.
        Assert.Equal(360, nativeA.Width, 6);
        Assert.Equal(300, nativeA.Height, 6);
        Assert.Equal(24, nativeA.PositionMarginX, 6);
        Assert.Equal(40, nativeA.PositionMarginY, 6);
        Assert.Equal(380, nativeB.Width, 6);
        Assert.Equal(320, nativeB.Height, 6);
        Assert.Equal(24, nativeB.PositionMarginX, 6);
        Assert.Equal(40, nativeB.PositionMarginY, 6);

        // And the 4 borrowed 2K widgets compress to fit inside 1920px width.
        Assert.All(borrowed, item => Assert.InRange(item.X + item.Width, 1, 1920 + 1));
    }

    [Fact]
    public void SizeLockedWidget_KeepsExactDimensionsUnderElasticGrowAndShrink()
    {
        WidgetConfig locked = CreateRowWidget("locked", WidgetPositionAnchors.LeftTop, 24, width: 420, height: 320);
        locked.IsSizeLocked = true;
        WidgetConfig unlocked = CreateRowWidget("unlocked", WidgetPositionAnchors.RightTop, 24, width: 420, height: 320);
        var settings = new AppSettings { Widgets = [locked, unlocked] };
        settings.WidgetLayout.WidgetDisplayScalingMode = SettingsService.WidgetDisplayScalingElasticScale;
        var service = new WidgetTopologyLayoutService();
        WidgetDisplayTopologySnapshot hd = WidgetTopologyLayoutService.CreateSnapshotForTest(
            Monitor("panel", @"\\.\DISPLAY1", true, 0, 0, 1920, 1040, 1));
        WidgetDisplayTopologySnapshot uhd = WidgetTopologyLayoutService.CreateSnapshotForTest(
            Monitor("panel", @"\\.\DISPLAY1", true, 0, 0, 3840, 2080, 1));
        WidgetDisplayTopologySnapshot compact = WidgetTopologyLayoutService.CreateSnapshotForTest(
            Monitor("panel", @"\\.\DISPLAY1", true, 0, 0, 800, 600, 1));

        service.Activate(settings, hd);
        service.Activate(settings, uhd);

        Assert.Equal(420, locked.Width, 6);
        Assert.Equal(320, locked.Height, 6);
        Assert.True(unlocked.Width > 700);

        service.Activate(settings, compact);
        Assert.Equal(420, locked.Width, 6);
        Assert.Equal(320, locked.Height, 6);
    }

    [Fact]
    public void DragOnSingleScreenFallback_DoesNotHijackHomeMonitorWhenSeedingThreeScreenTopology()
    {
        WidgetConfig pinnedExt = CreateRowWidget("ext", WidgetPositionAnchors.LeftTop, 80, width: 400, height: 320);
        pinnedExt.ScreenBindingMode = WidgetScreenBindingMode.Pinned;
        pinnedExt.BoundScreenId = "external";
        pinnedExt.PositionMonitorStableId = "external";
        pinnedExt.PositionMonitorDeviceName = @"\\.\DISPLAY2";
        pinnedExt.PositionMonitorWasPrimary = false;
        pinnedExt.PositionMonitorKey = "1920:0:1920:1040";
        pinnedExt.X = 2000;
        var settings = new AppSettings { Widgets = [pinnedExt] };
        var service = new WidgetTopologyLayoutService();
        WidgetDisplayTopologySnapshot dual = WidgetTopologyLayoutService.CreateSnapshotForTest(
            Monitor("laptop", @"\\.\DISPLAY1", true, 0, 0, 1920, 1040, 1),
            Monitor("external", @"\\.\DISPLAY2", false, 1920, 0, 1920, 1040, 1));
        WidgetDisplayTopologySnapshot singleLaptop = WidgetTopologyLayoutService.CreateSnapshotForTest(
            Monitor("laptop", @"\\.\DISPLAY1", true, 0, 0, 1920, 1040, 1));
        WidgetDisplayTopologySnapshot triple = WidgetTopologyLayoutService.CreateSnapshotForTest(
            Monitor("laptop", @"\\.\DISPLAY1", true, 0, 0, 1920, 1040, 1),
            Monitor("external", @"\\.\DISPLAY2", false, 1920, 0, 1920, 1040, 1),
            Monitor("tv", @"\\.\DISPLAY3", false, 3840, 0, 1920, 1040, 1));

        service.Activate(settings, dual);
        service.Activate(settings, singleLaptop);

        // User nudges the borrowed widget on the single laptop screen while external is unplugged.
        pinnedExt.PositionMarginX = 160;
        pinnedExt.X = 160;
        pinnedExt.LastUserPlacementUtc = DateTimeOffset.UtcNow.AddMinutes(5);
        service.Activate(settings, singleLaptop);

        // Docking into a brand-new 3-screen topology must seed onto "external", not "laptop".
        service.Activate(settings, triple);

        Assert.Equal("external", pinnedExt.PositionMonitorStableId);
        Assert.InRange(pinnedExt.X, 1920, 3840 - 400);
    }

    [Fact]
    public void BorrowedFallbackCollisionAvoidance_OffsetsOverlapAndRestoresExactPositionOnReconnect()
    {
        WidgetConfig nativeRight = CreateRowWidget("native-right", WidgetPositionAnchors.RightTop, 24, width: 400, height: 320);
        nativeRight.ScreenBindingMode = WidgetScreenBindingMode.Pinned;
        nativeRight.BoundScreenId = "laptop";
        nativeRight.PositionMonitorStableId = "laptop";
        nativeRight.PositionMonitorKey = "0:0:1920:1040";
        nativeRight.X = 1920 - 24 - 400;

        WidgetConfig extRight = CreateRowWidget("ext-right", WidgetPositionAnchors.RightTop, 24, width: 400, height: 320);
        extRight.ScreenBindingMode = WidgetScreenBindingMode.Pinned;
        extRight.BoundScreenId = "external";
        extRight.PositionMonitorStableId = "external";
        extRight.PositionMonitorDeviceName = @"\\.\DISPLAY2";
        extRight.PositionMonitorWasPrimary = false;
        extRight.PositionMonitorKey = "1920:0:1920:1040";
        extRight.X = 3840 - 24 - 400;

        var settings = new AppSettings { Widgets = [nativeRight, extRight] };
        var service = new WidgetTopologyLayoutService();
        WidgetDisplayTopologySnapshot dual = WidgetTopologyLayoutService.CreateSnapshotForTest(
            Monitor("laptop", @"\\.\DISPLAY1", true, 0, 0, 1920, 1040, 1),
            Monitor("external", @"\\.\DISPLAY2", false, 1920, 0, 1920, 1040, 1));
        WidgetDisplayTopologySnapshot singleLaptop = WidgetTopologyLayoutService.CreateSnapshotForTest(
            Monitor("laptop", @"\\.\DISPLAY1", true, 0, 0, 1920, 1040, 1));

        service.Activate(settings, dual);
        service.Activate(settings, singleLaptop);

        // Native widget stays put; borrowed widget shifts away instead of stacking 100% on top.
        Assert.Equal(1920 - 24 - 400, nativeRight.X);
        double interW = Math.Min(nativeRight.X + nativeRight.Width, extRight.X + extRight.Width) - Math.Max(nativeRight.X, extRight.X);
        double interH = Math.Min(nativeRight.Y + nativeRight.Height, extRight.Y + extRight.Height) - Math.Max(nativeRight.Y, extRight.Y);
        double overlapArea = Math.Max(0, interW) * Math.Max(0, interH);
        Assert.True(overlapArea <= 400 * 320 * 0.10, $"Expected <=10% overlap, got {overlapArea}");

        // Reconnecting the external display restores the exact original RightTop (24, 40) placement.
        service.Activate(settings, dual);
        Assert.Equal(24, extRight.PositionMarginX, 6);
        Assert.Equal(40, extRight.PositionMarginY, 6);
        Assert.Equal(3840 - 24 - 400, extRight.X);
        (WidgetConfig left, WidgetConfig right) = (nativeRight, extRight);
        Assert.Equal(24, left.PositionMarginX, 6);
        Assert.Equal(24, right.PositionMarginX, 6);
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

    private static WidgetConfig CreateRowWidget(
        string id,
        string anchor,
        double marginX,
        double width = 600,
        double height = 500) => new()
    {
        Id = id,
        Width = width,
        Height = height,
        BoundsCoordinateVersion = WidgetConfig.CurrentBoundsCoordinateVersion,
        PositionAnchor = anchor,
        PositionMarginX = marginX,
        PositionMarginY = 40,
        PositionMonitorDeviceName = @"\\.\DISPLAY1",
        PositionMonitorWasPrimary = true
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

    [Fact]
    public void Recalibrate_TogglingScalingModeOnActiveTopology_AppliesImmediatelyAndReversibly()
    {
        var service = new WidgetTopologyLayoutService();
        var settings = new AppSettings();
        var widget = new WidgetConfig
        {
            Id = "recal-scale",
            BoundsCoordinateVersion = WidgetConfig.CurrentBoundsCoordinateVersion,
            X = 1580,
            Y = 800,
            Width = 300,
            Height = 200,
            PositionAnchor = WidgetPositionAnchors.RightBottom,
            PositionMarginX = 40,
            PositionMarginY = 40,
            PositionMonitorStableId = "mon-main",
            PositionMonitorDeviceName = @"\\.\DISPLAY1",
            PositionMonitorWasPrimary = true,
            PositionMonitorKey = "0:0:1920:1040",
            ScreenBindingMode = WidgetScreenBindingMode.Pinned,
            BoundScreenId = "mon-main",
        };
        settings.WidgetLayout.Widgets.Add(widget);

        var t1080 = WidgetTopologyLayoutService.CreateSnapshotForTest(
            new WidgetTopologyMonitorProfile
            {
                StableId = "mon-main",
                DeviceName = @"\\.\DISPLAY1",
                IsPrimary = true,
                MonitorX = 0,
                MonitorY = 0,
                MonitorWidth = 1920,
                MonitorHeight = 1080,
                WorkAreaX = 0,
                WorkAreaY = 0,
                WorkAreaWidth = 1920,
                WorkAreaHeight = 1040,
                DpiScale = 1.0,
            });
        var t1440 = WidgetTopologyLayoutService.CreateSnapshotForTest(
            new WidgetTopologyMonitorProfile
            {
                StableId = "mon-main",
                DeviceName = @"\\.\DISPLAY1",
                IsPrimary = true,
                MonitorX = 0,
                MonitorY = 0,
                MonitorWidth = 2560,
                MonitorHeight = 1440,
                WorkAreaX = 0,
                WorkAreaY = 0,
                WorkAreaWidth = 2560,
                WorkAreaHeight = 1400,
                DpiScale = 1.0,
            });

        _ = service.Activate(settings, t1080);
        _ = service.Activate(settings, t1440);
        Assert.Equal(300, widget.Width);
        Assert.Equal(200, widget.Height);

        settings.WidgetLayout.WidgetDisplayScalingMode = SettingsService.WidgetDisplayScalingElasticScale;
        Assert.True(service.Recalibrate(settings, t1440));
        Assert.True(widget.Width > 350, $"Expected elastic grow after recalibrate, got Width={widget.Width}");
        Assert.True(widget.Height > 230, $"Expected elastic grow after recalibrate, got Height={widget.Height}");

        settings.WidgetLayout.WidgetDisplayScalingMode = SettingsService.WidgetDisplayScalingFixedSize;
        Assert.True(service.Recalibrate(settings, t1440));
        Assert.Equal(300, widget.Width);
        Assert.Equal(200, widget.Height);
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
