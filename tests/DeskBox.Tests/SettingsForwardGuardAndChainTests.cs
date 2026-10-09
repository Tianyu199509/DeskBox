using System.Text.Json;
using DeskBox.Models;
using DeskBox.Services;

namespace DeskBox.Tests;

/// <summary>
/// Two settings-store contracts that only compose across versions:
/// 1) a settings.json stamped by a NEWER build must never be overwritten
///    (the typed model would strip its unknown fields irreversibly and the
///    stripped file would never re-migrate), and 2) a v9-era file (the
///    schema of every released 1.4.x/1.5.x build) must chain cleanly through
///    every migration up to v13, including the entry-level binding pin the
///    11→12 step mirrors from the inferred home.
/// </summary>
public sealed class SettingsForwardGuardAndChainTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "DeskBox.Tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task SettingsFile_FromNewerSchema_RefusesEverySaveAndKeepsBytes()
    {
        string dataDir = Directory.CreateDirectory(Path.Combine(_root, "settings")).FullName;
        string settingsPath = Path.Combine(dataDir, "settings.json");
        // Serialize a real AppSettings graph so the source-generated context
        // round-trips it; a hand-written minimal object would fail
        // deserialization and take the recovery-defaults path instead.
        string newerSchemaJson = JsonSerializer.Serialize(
            new AppSettings { SchemaVersion = 99 },
            SettingsJsonContext.Default.AppSettings);
        await File.WriteAllTextAsync(settingsPath, newerSchemaJson);

        var service = new SettingsService(dataDir);
        await service.LoadAsync();

        Assert.Equal(99, service.Settings.SchemaVersion);
        service.Settings.TrayIconStyle = "Mono";

        Assert.False(await service.SaveCheckedAsync());
        Assert.Equal(newerSchemaJson, await File.ReadAllTextAsync(settingsPath));
    }

    [Fact]
    public void Pipeline_ChainsV9Settings_ThroughEveryMigrationToV13()
    {
        var settings = new AppSettings { SchemaVersion = 9 };
        settings.Widgets.Add(new WidgetConfig
        {
            Id = "w1",
            Name = "pinned",
            WidgetKind = WidgetKind.File,
            ScreenBindingMode = WidgetScreenBindingMode.Pinned,
            BoundScreenId = "DEL-1",
            X = 12,
            Y = 34
        });
        settings.Widgets.Add(new WidgetConfig
        {
            Id = "w2",
            Name = "unbound",
            WidgetKind = WidgetKind.File,
            ScreenBindingMode = WidgetScreenBindingMode.Pinned,
            BoundScreenId = @"\\.\DISPLAY3",
            PositionMonitorStableId = @"\\.\DISPLAY3"
        });

        var profile = new WidgetTopologyLayoutProfile
        {
            LastUsedAtUtc = new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero),
            Monitors =
            [
                new WidgetTopologyMonitorProfile
                {
                    StableId = "DEL-1",
                    DeviceName = @"\\.\DISPLAY1",
                    MonitorWidth = 2560,
                    MonitorHeight = 1440
                }
            ]
        };
        profile.Surfaces["w1"] = new WidgetSurfaceLayoutProfile
        {
            X = 12,
            Y = 34,
            PositionMonitorStableId = "DEL-1"
        };
        settings.WidgetTopologyLayouts["v3-legacy"] = profile;
        settings.ActiveWidgetTopologyKey = "v3-legacy";

        (AppSettings migrated, bool applied) =
            new SettingsMigrationPipeline().RunMigrationsOnCopy(settings);

        Assert.True(applied);
        Assert.Equal(13, migrated.SchemaVersion);

        // 12→13 rewrote the key to the v4 identity hash and remapped active.
        string mergedKey = Assert.Single(migrated.WidgetTopologyLayouts).Key;
        Assert.StartsWith("v4-", mergedKey, StringComparison.Ordinal);
        Assert.Equal(mergedKey, migrated.ActiveWidgetTopologyKey);

        // The pinned widget keeps its binding; the degenerate one resolves
        // to Unbound rather than inheriting a bogus screen id.
        WidgetConfig pinned = migrated.Widgets.Single(widget => widget.Id == "w1");
        Assert.Equal(WidgetScreenBindingMode.Pinned, pinned.ScreenBindingMode);
        Assert.Equal("DEL-1", pinned.BoundScreenId);
        WidgetConfig unbound = migrated.Widgets.Single(widget => widget.Id == "w2");
        Assert.Equal(WidgetScreenBindingMode.Unbound, unbound.ScreenBindingMode);
        Assert.Null(unbound.BoundScreenId);

        // 11→12 mirrored the inferred home into the profile entry so the
        // first topology projection after upgrade honors the pin.
        WidgetSurfaceLayoutProfile entry =
            migrated.WidgetTopologyLayouts[mergedKey].Surfaces["w1"];
        Assert.Equal(WidgetScreenBindingMode.Pinned, entry.ScreenBindingMode);
        Assert.Equal("DEL-1", entry.BoundScreenId);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
