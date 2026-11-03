using System.Text.Json;
using DeskBox.Models;
using DeskBox.Services;
using Windows.Graphics;

namespace DeskBox.Tests;

/// <summary>
/// Screen binding semantics: explicit Pinned/FollowPrimary modes, the stable-id
/// layer in the unbound resolution chain, and binding survival across topology
/// profile switches. The stacked-identical-monitors scenarios reproduce the
/// lock-screen re-enumeration reports (feedback 224): Windows swaps the
/// unstable <c>\\.\DISPLAYn</c> aliases while the stable PnP ids stay put.
/// </summary>
public sealed class WidgetScreenBindingTests
{
    private static RectInt32 ResolveWithScreens(
        WidgetConfig config,
        RectInt32 fallbackWorkArea,
        IReadOnlyList<(RectInt32 WorkArea, string? DeviceName, string? StableId, bool IsPrimary)> screens,
        double scale = 1.0)
    {
        return WidgetPositioningService.ResolveBoundsForTestWithScreens(
            config,
            fallbackWorkArea,
            screens,
            _ => scale);
    }

    private static WidgetConfig WidgetCapturedOn(
        string deviceName,
        string stableId,
        RectInt32 workArea) => new()
    {
        Id = "widget-1",
        BoundsCoordinateVersion = WidgetConfig.CurrentBoundsCoordinateVersion,
        X = workArea.X + 100,
        Y = workArea.Y + 60,
        Width = 300,
        Height = 400,
        PositionAnchor = WidgetPositionAnchors.LeftTop,
        PositionMarginX = 100,
        PositionMarginY = 60,
        PositionMonitorDeviceName = deviceName,
        PositionMonitorStableId = stableId,
        PositionMonitorWasPrimary = false,
        PositionMonitorKey = WidgetPositioningService.CreateMonitorKey(workArea)
    };

    // The report scenario: two identical panels stacked vertically at the same
    // X, plus a primary on the left. Lock screen re-enumeration swaps the
    // DISPLAYn aliases between the two stacked panels.
    private static RectInt32 PrimaryArea => new(0, 0, 2560, 1392);
    private static RectInt32 UpperArea => new(2560, -531, 1920, 1152);
    private static RectInt32 LowerArea => new(2560, 686, 1920, 1152);

    [Fact]
    public void PinnedBinding_KeepsWidgetOnBoundScreen_WhenAliasesSwap()
    {
        var config = WidgetCapturedOn(@"\\.\DISPLAY1", "upper-panel", UpperArea);
        config.ScreenBindingMode = WidgetScreenBindingMode.Pinned;
        config.BoundScreenId = "upper-panel";

        // After resume the upper panel inherited DISPLAY2; only the stable id
        // still names it. A wrong resolution would land the widget on the
        // lower panel with the same anchor ("jumps to display 2's top-right").
        var reEnumerated = ResolveWithScreens(
            config,
            PrimaryArea,
            new List<(RectInt32, string?, string?, bool)>
            {
                (PrimaryArea, @"\\.\DISPLAY3", "primary-panel", true),
                (UpperArea, @"\\.\DISPLAY2", "upper-panel", false),
                (LowerArea, @"\\.\DISPLAY1", "lower-panel", false)
            });

        Assert.InRange(reEnumerated.X, UpperArea.X, UpperArea.X + UpperArea.Width - 10);
        Assert.InRange(reEnumerated.Y, UpperArea.Y, UpperArea.Y + UpperArea.Height - 10);
    }

    [Fact]
    public void UnboundChain_StableIdLayer_OutranksSwappedDeviceName()
    {
        var config = WidgetCapturedOn(@"\\.\DISPLAY1", "upper-panel", UpperArea);

        var reEnumerated = ResolveWithScreens(
            config,
            PrimaryArea,
            new List<(RectInt32, string?, string?, bool)>
            {
                (PrimaryArea, @"\\.\DISPLAY3", "primary-panel", true),
                (UpperArea, @"\\.\DISPLAY2", "upper-panel", false),
                (LowerArea, @"\\.\DISPLAY1", "lower-panel", false)
            });

        Assert.InRange(reEnumerated.X, UpperArea.X, UpperArea.X + UpperArea.Width - 10);
        Assert.InRange(reEnumerated.Y, UpperArea.Y, UpperArea.Y + UpperArea.Height - 10);
    }

    [Fact]
    public void PinnedBinding_FallsBackToLegacyChain_WhenBoundScreenAbsent()
    {
        var config = WidgetCapturedOn(@"\\.\DISPLAY1", "upper-panel", UpperArea);
        config.ScreenBindingMode = WidgetScreenBindingMode.Pinned;
        config.BoundScreenId = "unplugged-panel";

        // The stacked pair was unplugged entirely: the widget must stay visible
        // on an attached monitor instead of dead-ending on the missing pin.
        var resolved = ResolveWithScreens(
            config,
            PrimaryArea,
            new List<(RectInt32, string?, string?, bool)>
            {
                (PrimaryArea, @"\\.\DISPLAY3", "primary-panel", true),
                (LowerArea, @"\\.\DISPLAY1", "lower-panel", false)
            });

        bool onPrimary = resolved.X >= PrimaryArea.X && resolved.X < PrimaryArea.X + PrimaryArea.Width;
        bool onLower = resolved.X >= LowerArea.X && resolved.X < LowerArea.X + LowerArea.Width;
        Assert.True(onPrimary || onLower);
    }

    [Fact]
    public void FollowPrimaryBinding_TracksCurrentPrimaryMonitor()
    {
        var config = WidgetCapturedOn(@"\\.\DISPLAY2", "upper-panel", UpperArea);
        config.ScreenBindingMode = WidgetScreenBindingMode.FollowPrimary;

        var resolved = ResolveWithScreens(
            config,
            UpperArea,
            new List<(RectInt32, string?, string?, bool)>
            {
                (PrimaryArea, @"\\.\DISPLAY3", "primary-panel", true),
                (UpperArea, @"\\.\DISPLAY2", "upper-panel", false)
            });

        Assert.InRange(resolved.X, PrimaryArea.X, PrimaryArea.X + PrimaryArea.Width - 10);
        Assert.InRange(resolved.Y, PrimaryArea.Y, PrimaryArea.Y + PrimaryArea.Height - 10);
    }

    [Fact]
    public void DegenerateStableIds_NeverLatchOntoRenumberedMonitor()
    {
        // A stable id that degenerated to the DISPLAYn alias must not match an
        // unrelated monitor that now carries that alias.
        var config = WidgetCapturedOn(@"\\.\DISPLAY1", @"\\.\DISPLAY1", UpperArea);
        config.ScreenBindingMode = WidgetScreenBindingMode.Pinned;
        config.BoundScreenId = @"\\.\DISPLAY1";

        var resolved = ResolveWithScreens(
            config,
            UpperArea,
            new List<(RectInt32, string?, string?, bool)>
            {
                (PrimaryArea, @"\\.\DISPLAY3", "primary-panel", true),
                (LowerArea, @"\\.\DISPLAY1", "lower-panel", false)
            });

        // Falls through to the legacy chain: device name matches the lower
        // panel, which is acceptable interim placement; the point is that it
        // did not crash and stayed on an attached screen.
        Assert.InRange(resolved.X, LowerArea.X, LowerArea.X + LowerArea.Width - 10);
    }

    [Fact]
    public void BindingModeJsonConverter_DowngradesUnknownValues()
    {
        WidgetConfig parsed = JsonSerializer.Deserialize<WidgetConfig>(
            """{"ScreenBindingMode":"Bogus"}""")!;
        Assert.Equal(WidgetScreenBindingMode.Unbound, parsed.ScreenBindingMode);

        WidgetConfig numeric = JsonSerializer.Deserialize<WidgetConfig>(
            """{"ScreenBindingMode":99}""")!;
        Assert.Equal(WidgetScreenBindingMode.Unbound, numeric.ScreenBindingMode);

        string serialized = JsonSerializer.Serialize(new WidgetConfig
        {
            ScreenBindingMode = WidgetScreenBindingMode.FollowPrimary
        });
        Assert.Contains("\"FollowPrimary\"", serialized);
    }

    [Fact]
    public void CatalogTryFindScreen_RejectsUnstableIdentities()
    {
        var screens = new List<WidgetScreenInfo>
        {
            new(1, "primary-panel", @"\\.\DISPLAY1", PrimaryArea, PrimaryArea, true, 1),
            new(2, "upper-panel", @"\\.\DISPLAY2", UpperArea, UpperArea, false, 1)
        };

        Assert.NotNull(WidgetScreenCatalog.TryFindScreen(screens, "UPPER-PANEL"));
        Assert.Null(WidgetScreenCatalog.TryFindScreen(screens, @"\\.\DISPLAY2"));
        Assert.Null(WidgetScreenCatalog.TryFindScreen(screens, "unknown-display"));
        Assert.Null(WidgetScreenCatalog.TryFindScreen(screens, null));
        Assert.False(WidgetScreenCatalog.IsScreenAttached(screens, "missing"));
        Assert.True(WidgetScreenCatalog.IsScreenAttached(screens, "upper-panel"));
    }

    [Fact]
    public void MapToTopology_PinnedLayoutMigratesOntoBoundMonitor()
    {
        var layout = new WidgetSurfaceLayoutProfile
        {
            X = 2140,
            Y = 120,
            Width = 720,
            Height = 600,
            PositionAnchor = WidgetPositionAnchors.LeftTop,
            PositionMarginX = 220,
            PositionMarginY = 120,
            PositionMonitorStableId = "external",
            PositionMonitorDeviceName = @"\\.\DISPLAY2",
            PositionMonitorWasPrimary = false,
            ScreenBindingMode = WidgetScreenBindingMode.Pinned,
            BoundScreenId = "external"
        };

        var sourceMonitors = new List<WidgetTopologyMonitorProfile>
        {
            Monitor("panel", @"\\.\DISPLAY1", true, 0, 0, 1920, 1040),
            Monitor("external", @"\\.\DISPLAY2", false, 1920, 0, 1920, 1040)
        };
        var targetMonitors = new List<WidgetTopologyMonitorProfile>
        {
            Monitor("wide", @"\\.\DISPLAY1", true, 0, 0, 2560, 1320),
            Monitor("external", @"\\.\DISPLAY4", false, 2560, 0, 1920, 1040),
            Monitor("tv", @"\\.\DISPLAY3", false, 4480, 0, 1920, 1040)
        };

        WidgetSurfaceLayoutProfile mapped = WidgetTopologyLayoutService.MapToTopology(
            layout,
            sourceMonitors,
            targetMonitors);

        Assert.Equal("external", mapped.PositionMonitorStableId, StringComparer.OrdinalIgnoreCase);
        // The mapped geometry must sit on the bound monitor's work area (the
        // 220px anchor margin lands on its origin), not on the primary or the
        // same-placement twin at 4480.
        Assert.Equal(2560 + 220, mapped.X);
    }

    [Fact]
    public void TopologyProfileActivation_RepositionsButNeverRebinds()
    {
        var widget = new WidgetConfig
        {
            Id = "widget-1",
            X = 2140,
            Y = 120,
            Width = 720,
            Height = 600,
            BoundsCoordinateVersion = WidgetConfig.CurrentBoundsCoordinateVersion,
            PositionAnchor = WidgetPositionAnchors.LeftTop,
            PositionMonitorDeviceName = @"\\.\DISPLAY2",
            PositionMonitorWasPrimary = false,
            ScreenBindingMode = WidgetScreenBindingMode.Pinned,
            BoundScreenId = "external"
        };
        var settings = new AppSettings { Widgets = [widget] };
        var service = new WidgetTopologyLayoutService();

        WidgetDisplayTopologySnapshot laptop = WidgetTopologyLayoutService.CreateSnapshotForTest(
            Monitor("panel", @"\\.\DISPLAY1", true, 0, 0, 1920, 1040));
        WidgetDisplayTopologySnapshot dual = WidgetTopologyLayoutService.CreateSnapshotForTest(
            Monitor("panel", @"\\.\DISPLAY1", true, 0, 0, 1920, 1040),
            Monitor("external", @"\\.\DISPLAY2", false, 1920, 0, 1920, 1040));

        service.Activate(settings, dual);
        Assert.True(settings.WidgetTopologyLayouts.TryGetValue(
            dual.Key,
            out WidgetTopologyLayoutProfile? profile));
        Assert.NotNull(profile);
        WidgetSurfaceLayoutProfile captured = profile.Surfaces["widget-1"];
        Assert.Equal(WidgetScreenBindingMode.Pinned, captured.ScreenBindingMode);
        Assert.Equal("external", captured.BoundScreenId);

        // Round-trip through the laptop topology and back: the pin survives
        // even though the laptop profile never contained the external monitor.
        service.Activate(settings, laptop);
        Assert.Equal(WidgetScreenBindingMode.Pinned, widget.ScreenBindingMode);
        Assert.Equal("external", widget.BoundScreenId);

        service.Activate(settings, dual);
        Assert.Equal(WidgetScreenBindingMode.Pinned, widget.ScreenBindingMode);
        Assert.Equal("external", widget.BoundScreenId);
    }

    [Fact]
    public void TopologyProfileActivation_BackfillsStableIdSoResolutionLandsOnProfileMonitor()
    {
        // Regression shape of the wrong-screen report: profile activation
        // rewrote geometry/device name but left the previous stable id behind,
        // and the resolution chain ranks the stable id ABOVE the device name.
        var widget = new WidgetConfig
        {
            Id = "widget-1",
            BoundsCoordinateVersion = WidgetConfig.CurrentBoundsCoordinateVersion,
            PositionAnchor = WidgetPositionAnchors.LeftTop,
            PositionMarginX = 100,
            PositionMarginY = 80,
            X = 200,
            Y = 160,
            Width = 600,
            Height = 500,
            PositionMonitorDeviceName = @"\\.\DISPLAY1",
            PositionMonitorWasPrimary = true,
            PositionMonitorKey = "0:0:1920:1040"
        };
        var settings = new AppSettings { Widgets = [widget] };
        var service = new WidgetTopologyLayoutService();
        WidgetDisplayTopologySnapshot laptop = WidgetTopologyLayoutService.CreateSnapshotForTest(
            Monitor("panel", @"\\.\DISPLAY1", true, 0, 0, 1920, 1040));
        WidgetDisplayTopologySnapshot dual = WidgetTopologyLayoutService.CreateSnapshotForTest(
            Monitor("panel", @"\\.\DISPLAY1", true, 0, 0, 1920, 1040),
            Monitor("external", @"\\.\DISPLAY2", false, 1920, 0, 1920, 1040));

        // Docked: capture the dual-screen profile, then drag the widget onto
        // the external panel (a drag capture refreshes the whole monitor
        // triple on the config, stable id included).
        service.Activate(settings, dual);
        widget.X = 2140;
        widget.Y = 120;
        widget.PositionMarginX = 220;
        widget.PositionMarginY = 120;
        widget.PositionMonitorDeviceName = @"\\.\DISPLAY2";
        widget.PositionMonitorStableId = "external";
        widget.PositionMonitorWasPrimary = false;
        widget.PositionMonitorKey = "1920:0:1920:1040";

        // Undocked: the single-screen profile folds the widget onto the laptop
        // panel and the user keeps working there, so the config stable id now
        // legitimately names the panel (drag refresh + profile activation).
        service.Activate(settings, laptop);
        widget.X = 100;
        widget.Y = 120;
        widget.PositionMarginX = 100;
        widget.PositionMonitorDeviceName = @"\\.\DISPLAY1";
        widget.PositionMonitorStableId = "panel";
        widget.PositionMonitorWasPrimary = true;
        widget.PositionMonitorKey = "0:0:1920:1040";
        // Persist the dragged single-screen layout (what the debounced
        // CaptureCurrentSurface would write after the drag settles).
        service.Activate(settings, laptop);

        // Re-docked: the dual profile projects the saved external layout back.
        // The stable id must be backfilled too, or the resolution chain keeps
        // resolving the widget onto the laptop panel named by the stale id.
        service.Activate(settings, dual);

        Assert.Equal("external", widget.PositionMonitorStableId);
        Assert.Equal(@"\\.\DISPLAY2", widget.PositionMonitorDeviceName);
        Assert.Equal(2140, widget.X);

        RectInt32 resolved = ResolveWithScreens(
            widget,
            PrimaryArea,
            new List<(RectInt32, string?, string?, bool)>
            {
                (new RectInt32(0, 0, 1920, 1040), @"\\.\DISPLAY1", "panel", true),
                (new RectInt32(1920, 0, 1920, 1040), @"\\.\DISPLAY2", "external", false)
            });

        Assert.InRange(resolved.X, 1920, 1920 + 1920 - 10);
        Assert.InRange(resolved.Y, 0, 1040 - 10);
    }

    [Fact]
    public void NegativeLeftNonPrimaryEnumeratedFirst_RightPrimaryWidget_StaysOnRightPrimaryScreen()
    {
        var leftNonPrimary = new RectInt32(-1920, 0, 1920, 1040);
        var rightPrimary = new RectInt32(0, 0, 1920, 1040);
        var widget = new WidgetConfig
        {
            X = 120,
            Y = 80,
            Width = 320,
            Height = 240,
            BoundsCoordinateVersion = WidgetConfig.CurrentBoundsCoordinateVersion,
            PositionAnchor = WidgetPositionAnchors.LeftTop,
            PositionMarginX = 120,
            PositionMarginY = 80,
            ScreenBindingMode = WidgetScreenBindingMode.Pinned,
            BoundScreenId = "RIGHT-PRIMARY-ID",
            PositionMonitorStableId = "RIGHT-PRIMARY-ID",
            PositionMonitorDeviceName = @"\\.\DISPLAY1",
            PositionMonitorKey = "0:0:1920:1040",
            PositionMonitorWasPrimary = true
        };

        // EnumDisplayMonitors can enumerate the left monitor (-1920, 0) at index 0
        // before the primary right monitor (0, 0) at index 1. When full screen
        // identity + IsPrimary metadata is preserved, the widget stays on (0, 0).
        RectInt32 resolved = ResolveWithScreens(
            widget,
            rightPrimary,
            new List<(RectInt32, string?, string?, bool)>
            {
                (leftNonPrimary, @"\\.\DISPLAY2", "LEFT-EXT-ID", false),
                (rightPrimary, @"\\.\DISPLAY1", "RIGHT-PRIMARY-ID", true)
            });

        Assert.Equal(120, resolved.X);
        Assert.Equal(80, resolved.Y);
        Assert.InRange(resolved.X, rightPrimary.X, rightPrimary.X + rightPrimary.Width - resolved.Width);
    }

    [Fact]
    public void ResolveActualDisplay_OnePixelSeamOverlapOnLeftEdge_SelectsMajorityRightDisplay()
    {
        var leftScreen = new WidgetScreenInfo(
            1,
            "LEFT-ID",
            @"\\.\DISPLAY1",
            new RectInt32(0, 0, 1920, 1080),
            new RectInt32(0, 0, 1920, 1040),
            true,
            1.0);
        var rightScreen = new WidgetScreenInfo(
            2,
            "RIGHT-ID",
            @"\\.\DISPLAY2",
            new RectInt32(1920, 0, 1920, 1080),
            new RectInt32(1920, 0, 1920, 1040),
            false,
            1.0);

        // Top-left X=1919 is 1px inside leftScreen, while 319x240 sits on rightScreen.
        var widget = new WidgetConfig
        {
            X = 1919,
            Y = 100,
            Width = 320,
            Height = 240,
            BoundsCoordinateVersion = WidgetConfig.CurrentBoundsCoordinateVersion
        };

        WidgetScreenInfo? actual = WidgetScreenMenuBuilder.ResolveActualDisplay(
            widget,
            [leftScreen, rightScreen]);

        Assert.NotNull(actual);
        Assert.Equal("RIGHT-ID", actual!.StableId);
    }

    [Fact]
    public void PreviewResolver_ExcludesUnimplementedAndDisabledFeatureWidgets()
    {
        var monitor = new WidgetScreenInfo(
            2,
            "RIGHT-MAIN",
            @"\\.\DISPLAY2",
            new RectInt32(0, 0, 1920, 1080),
            new RectInt32(0, 0, 1920, 1032),
            true,
            1.0);

        var settings = new AppSettings();
        FeatureWidgetSettings.SetEnabled(settings, WidgetKind.Weather, false);
        settings.WidgetLayout.Widgets =
        [
            new WidgetConfig
            {
                Id = "sys-mon",
                Name = "系统监控",
                WidgetKind = WidgetKind.SystemMonitor,
                Width = 299,
                Height = 994,
                BoundsCoordinateVersion = WidgetConfig.CurrentBoundsCoordinateVersion
            },
            new WidgetConfig
            {
                Id = "weather-off",
                Name = "天气",
                WidgetKind = WidgetKind.Weather,
                Width = 319,
                Height = 321,
                BoundsCoordinateVersion = WidgetConfig.CurrentBoundsCoordinateVersion
            },
            new WidgetConfig
            {
                Id = "clipboard-on",
                Name = "剪贴板",
                WidgetKind = WidgetKind.Clipboard,
                X = 1006,
                Y = 25,
                Width = 324,
                Height = 617,
                PositionAnchor = WidgetPositionAnchors.LeftTop,
                PositionMarginX = 1006,
                PositionMarginY = 25,
                PositionMonitorStableId = "RIGHT-MAIN",
                BoundsCoordinateVersion = WidgetConfig.CurrentBoundsCoordinateVersion
            }
        ];

        List<WidgetSurfacePreviewItem> surfaces = WidgetPlacementPreviewResolver.ResolveSurfaces(
            settings,
            [monitor],
            count => $"分组 ({count})",
            names => names);

        WidgetSurfacePreviewItem single = Assert.Single(surfaces);
        Assert.Equal("clipboard-on", single.ApplyWidgetId);
        Assert.Equal("剪贴板", single.ShortLabel);
        Assert.False(single.IsCompact);
        Assert.Equal(new RectInt32(1006, 25, 324, 617), single.PreviewBounds);
    }

    [Fact]
    public void PreviewResolver_MatchesRuntimeExpandedAndSmartCapsuleGeometry()
    {
        var monitor = new WidgetScreenInfo(
            2,
            "RIGHT-MAIN",
            @"\\.\DISPLAY2",
            new RectInt32(0, 0, 1920, 1080),
            new RectInt32(0, 0, 1920, 1032),
            true,
            1.0);

        var settings = new AppSettings();
        FeatureWidgetSettings.SetEnabled(settings, WidgetKind.Search, true);
        FeatureWidgetSettings.SetEnabled(settings, WidgetKind.Todo, true);
        FeatureWidgetSettings.SetEnabled(settings, WidgetKind.QuickCapture, true);
        settings.WidgetShell.WidgetCollapseBehavior = WidgetCollapseBehaviorNames.Expanded;
        settings.WidgetShell.WidgetCompactWidthMode = SettingsService.WidgetCompactWidthModeAligned;
        settings.WidgetShell.WidgetCompactContentMode = SettingsService.WidgetCompactContentModeSmart;

        settings.WidgetLayout.Widgets =
        [
            // Stale IsCollapsed=true while effective CollapseBehavior=Expanded -> full 253x301 card.
            new WidgetConfig
            {
                Id = "work-file",
                Name = "Work",
                WidgetKind = WidgetKind.File,
                IsCollapsed = true,
                Metadata = new Dictionary<string, string>
                {
                    [WidgetCollapseBehaviorNames.MetadataKey] = WidgetCollapseBehaviorNames.Expanded
                },
                X = 1659,
                Y = 722,
                Width = 253,
                Height = 301,
                PositionAnchor = WidgetPositionAnchors.RightBottom,
                PositionMarginX = 8,
                PositionMarginY = 9,
                PositionMonitorStableId = "RIGHT-MAIN",
                BoundsCoordinateVersion = WidgetConfig.CurrentBoundsCoordinateVersion
            },
            // Smart override with IsCollapsed=false -> rests as 253x52 capsule at CompactPlacement.
            new WidgetConfig
            {
                Id = "search-smart",
                Name = "搜索",
                WidgetKind = WidgetKind.Search,
                IsCollapsed = false,
                Metadata = new Dictionary<string, string>
                {
                    [WidgetCollapseBehaviorNames.MetadataKey] = WidgetCollapseBehaviorNames.Smart
                },
                X = 1659,
                Y = 82,
                Width = 253,
                Height = 321,
                PositionAnchor = WidgetPositionAnchors.RightTop,
                PositionMarginX = 8,
                PositionMarginY = 82,
                PositionMonitorStableId = "RIGHT-MAIN",
                BoundsCoordinateVersion = WidgetConfig.CurrentBoundsCoordinateVersion,
                CompactPlacement = new WidgetCompactPlacement
                {
                    BoundsCoordinateVersion = WidgetConfig.CurrentBoundsCoordinateVersion,
                    X = 1659,
                    Y = 82,
                    PositionAnchor = WidgetPositionAnchors.RightTop,
                    PositionMarginX = 8,
                    PositionMarginY = 82,
                    PositionMonitorStableId = "RIGHT-MAIN"
                }
            },
            new WidgetConfig
            {
                Id = "todo-member",
                Name = "待办",
                WidgetKind = WidgetKind.Todo,
                BoundsCoordinateVersion = WidgetConfig.CurrentBoundsCoordinateVersion
            },
            new WidgetConfig
            {
                Id = "capture-member",
                Name = "随记",
                WidgetKind = WidgetKind.QuickCapture,
                BoundsCoordinateVersion = WidgetConfig.CurrentBoundsCoordinateVersion
            },
            new WidgetConfig
            {
                Id = "doc-a",
                Name = "综合",
                WidgetKind = WidgetKind.File,
                BoundsCoordinateVersion = WidgetConfig.CurrentBoundsCoordinateVersion
            },
            new WidgetConfig
            {
                Id = "doc-b",
                Name = "需求文档",
                WidgetKind = WidgetKind.File,
                BoundsCoordinateVersion = WidgetConfig.CurrentBoundsCoordinateVersion
            }
        ];

        settings.WidgetLayout.WidgetGroups =
        [
            // Smart group -> rests as aligned 577x52 capsule.
            new WidgetGroupConfig
            {
                Id = "group-smart",
                MemberIds = ["todo-member", "capture-member"],
                ActiveMemberId = "todo-member",
                CollapseBehavior = WidgetCollapseBehaviorNames.Smart,
                IsCollapsed = false,
                X = 1335,
                Y = 25,
                Width = 577,
                Height = 690,
                PositionAnchor = WidgetPositionAnchors.RightTop,
                PositionMarginX = 8,
                PositionMarginY = 25,
                PositionMonitorStableId = "RIGHT-MAIN",
                BoundsCoordinateVersion = WidgetConfig.CurrentBoundsCoordinateVersion,
                CompactPlacement = new WidgetCompactPlacement
                {
                    BoundsCoordinateVersion = WidgetConfig.CurrentBoundsCoordinateVersion,
                    X = 1335,
                    Y = 25,
                    PositionAnchor = WidgetPositionAnchors.RightTop,
                    PositionMarginX = 8,
                    PositionMarginY = 25,
                    PositionMonitorStableId = "RIGHT-MAIN"
                }
            },
            // Expanded group with stale IsCollapsed=true -> full 319x301 card.
            new WidgetGroupConfig
            {
                Id = "group-expanded",
                MemberIds = ["doc-a", "doc-b"],
                ActiveMemberId = "doc-a",
                CollapseBehavior = "System",
                IsCollapsed = true,
                X = 1335,
                Y = 722,
                Width = 319,
                Height = 301,
                PositionAnchor = WidgetPositionAnchors.RightBottom,
                PositionMarginX = 266,
                PositionMarginY = 9,
                PositionMonitorStableId = "RIGHT-MAIN",
                BoundsCoordinateVersion = WidgetConfig.CurrentBoundsCoordinateVersion
            }
        ];

        List<WidgetSurfacePreviewItem> surfaces = WidgetPlacementPreviewResolver.ResolveSurfaces(
            settings,
            [monitor],
            count => $"分组 ({count})",
            names => names);

        Assert.Equal(4, surfaces.Count);

        WidgetSurfacePreviewItem work = Assert.Single(surfaces, s => s.ApplyWidgetId == "work-file");
        Assert.False(work.IsCompact);
        Assert.Equal(new RectInt32(1659, 722, 253, 301), work.PreviewBounds);

        WidgetSurfacePreviewItem search = Assert.Single(surfaces, s => s.ApplyWidgetId == "search-smart");
        Assert.True(search.IsCompact);
        Assert.Equal(new RectInt32(1659, 82, 253, 52), search.PreviewBounds);

        WidgetSurfacePreviewItem smartGroup = Assert.Single(surfaces, s => s.ApplyWidgetId == "todo-member");
        Assert.True(smartGroup.IsCompact);
        Assert.Equal("待办 / 随记", smartGroup.ShortLabel);
        Assert.Equal(new RectInt32(1335, 25, 577, 52), smartGroup.PreviewBounds);

        WidgetSurfacePreviewItem expandedGroup = Assert.Single(surfaces, s => s.ApplyWidgetId == "doc-a");
        Assert.False(expandedGroup.IsCompact);
        Assert.Equal("综合 / 需求文档", expandedGroup.ShortLabel);
        Assert.Equal(new RectInt32(1335, 722, 319, 301), expandedGroup.PreviewBounds);
    }

    private static WidgetTopologyMonitorProfile Monitor(
        string stableId,
        string deviceName,
        bool primary,
        int x,
        int y,
        int width,
        int height) => new()
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
        WorkAreaHeight = height
    };
}
