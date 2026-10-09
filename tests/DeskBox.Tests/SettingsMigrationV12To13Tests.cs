using DeskBox.Models;
using DeskBox.Services;

namespace DeskBox.Tests;

/// <summary>
/// Behavioral contracts for the schema v12 → v13 topology-key rewrite: v3
/// profiles that collapse onto the same v4 identity key merge with the newest
/// usage as the base and the loser's unique surfaces filled in — including
/// when the NEWER profile arrives second, the branch that previously compared
/// the old base against itself and silently dropped those entries.
/// </summary>
public sealed class SettingsMigrationV12To13Tests
{
    private static WidgetTopologyLayoutProfile Profile(
        DateTimeOffset lastUsedAt,
        params (string SurfaceId, double X)[] surfaces)
    {
        var profile = new WidgetTopologyLayoutProfile
        {
            LastUsedAtUtc = lastUsedAt,
            Monitors =
            [
                new WidgetTopologyMonitorProfile
                {
                    StableId = "DEL-1000",
                    DeviceName = @"\\.\DISPLAY1",
                    MonitorWidth = 1920,
                    MonitorHeight = 1080
                }
            ]
        };
        foreach ((string surfaceId, double x) in surfaces)
        {
            profile.Surfaces[surfaceId] = new WidgetSurfaceLayoutProfile
            {
                X = x,
                Y = x + 1,
                IsAuthoritative = true
            };
        }

        return profile;
    }

    [Fact]
    public void Merge_FillsMissingSurfacesFromOlderBaseWhenNewerArrivesSecond()
    {
        var settings = new AppSettings { SchemaVersion = 12 };
        // "v3-a" sorts before "v3-b", so the newer profile is processed second
        // — exactly the branch that used to drop the older base's entries.
        settings.WidgetTopologyLayouts["v3-a"] = Profile(
            new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
            ("w1", 10), ("w2", 20));
        settings.WidgetTopologyLayouts["v3-b"] = Profile(
            new DateTimeOffset(2026, 2, 1, 0, 0, 0, TimeSpan.Zero),
            ("w1", 99));

        new Migration_12_To_13().Migrate(settings);

        KeyValuePair<string, WidgetTopologyLayoutProfile> merged =
            Assert.Single(settings.WidgetTopologyLayouts);
        Assert.StartsWith("v4-", merged.Key, StringComparison.Ordinal);
        // The newer profile wins as the base for the shared surface…
        Assert.Equal(99, merged.Value.Surfaces["w1"].X);
        // …and the older base's unique surface is filled in, not dropped.
        Assert.Equal(20, merged.Value.Surfaces["w2"].X);
        Assert.True(merged.Value.Surfaces["w2"].IsAuthoritative);
    }

    [Fact]
    public void Merge_FillsMissingSurfacesWhenOlderArrivesSecond()
    {
        var settings = new AppSettings { SchemaVersion = 12 };
        settings.WidgetTopologyLayouts["v3-a"] = Profile(
            new DateTimeOffset(2026, 2, 1, 0, 0, 0, TimeSpan.Zero),
            ("w1", 99));
        settings.WidgetTopologyLayouts["v3-b"] = Profile(
            new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
            ("w1", 10), ("w2", 20));

        new Migration_12_To_13().Migrate(settings);

        KeyValuePair<string, WidgetTopologyLayoutProfile> merged =
            Assert.Single(settings.WidgetTopologyLayouts);
        Assert.Equal(99, merged.Value.Surfaces["w1"].X);
        Assert.Equal(20, merged.Value.Surfaces["w2"].X);
    }

    [Fact]
    public void Merge_RemapsActiveKeyAndDropsUnmappableActive()
    {
        var settings = new AppSettings { SchemaVersion = 12 };
        DateTimeOffset newer = new(2026, 2, 1, 0, 0, 0, TimeSpan.Zero);
        DateTimeOffset older = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        settings.WidgetTopologyLayouts["v3-a"] = Profile(older, ("w1", 10));
        settings.WidgetTopologyLayouts["v3-b"] = Profile(newer, ("w1", 99));
        settings.ActiveWidgetTopologyKey = "v3-b";

        new Migration_12_To_13().Migrate(settings);

        string mergedKey = Assert.Single(settings.WidgetTopologyLayouts).Key;
        Assert.Equal(mergedKey, settings.ActiveWidgetTopologyKey);

        settings = new AppSettings { SchemaVersion = 12 };
        settings.WidgetTopologyLayouts["v3-a"] = Profile(older, ("w1", 10));
        settings.ActiveWidgetTopologyKey = "v3-gone";
        new Migration_12_To_13().Migrate(settings);
        Assert.Null(settings.ActiveWidgetTopologyKey);
    }

    [Fact]
    public void Merge_DropsProfilesWithDegenerateMonitors()
    {
        var settings = new AppSettings { SchemaVersion = 12 };
        settings.WidgetTopologyLayouts["v3-a"] = Profile(
            new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
            ("w1", 10));
        var degenerate = new WidgetTopologyLayoutProfile
        {
            LastUsedAtUtc = new DateTimeOffset(2026, 2, 1, 0, 0, 0, TimeSpan.Zero),
            Monitors = []
        };
        settings.WidgetTopologyLayouts["v3-empty"] = degenerate;

        new Migration_12_To_13().Migrate(settings);

        KeyValuePair<string, WidgetTopologyLayoutProfile> merged =
            Assert.Single(settings.WidgetTopologyLayouts);
        Assert.Equal(10, merged.Value.Surfaces["w1"].X);
    }

    [Fact]
    public void Migration_11_To_12_InfersHomesWithDocumentedFallbacks()
    {
        var settings = new AppSettings { SchemaVersion = 11 };
        settings.Widgets.Add(new WidgetConfig
        {
            Id = "follow",
            Name = "follow",
            WidgetKind = WidgetKind.File,
            ScreenBindingMode = WidgetScreenBindingMode.FollowPrimary,
            BoundScreenId = "stale"
        });
        settings.Widgets.Add(new WidgetConfig
        {
            Id = "recover",
            Name = "recover",
            WidgetKind = WidgetKind.File,
            ScreenBindingMode = WidgetScreenBindingMode.Pinned,
            BoundScreenId = @"\\.\DISPLAY5",
            PositionMonitorStableId = "DEL-9"
        });
        settings.Widgets.Add(new WidgetConfig
        {
            Id = "orphan",
            Name = "orphan",
            WidgetKind = WidgetKind.File,
            ScreenBindingMode = WidgetScreenBindingMode.Pinned,
            BoundScreenId = null,
            PositionMonitorStableId = null
        });

        new Migration_11_To_12().Migrate(settings);

        WidgetConfig follow = settings.Widgets.Single(widget => widget.Id == "follow");
        Assert.Equal(WidgetScreenBindingMode.FollowPrimary, follow.ScreenBindingMode);
        Assert.Null(follow.BoundScreenId);

        WidgetConfig recover = settings.Widgets.Single(widget => widget.Id == "recover");
        Assert.Equal(WidgetScreenBindingMode.Pinned, recover.ScreenBindingMode);
        Assert.Equal("DEL-9", recover.BoundScreenId);

        WidgetConfig orphan = settings.Widgets.Single(widget => widget.Id == "orphan");
        Assert.Equal(WidgetScreenBindingMode.Unbound, orphan.ScreenBindingMode);
        Assert.Null(orphan.BoundScreenId);
    }
}
