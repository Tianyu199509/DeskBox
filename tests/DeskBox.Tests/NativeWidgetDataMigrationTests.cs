using System.Text.Json;

namespace DeskBox.Tests;

/// <summary>
/// Legacy data handoff (audit round 18): until the formal ownership cutover,
/// the built-in store stays the SINGLE source of truth. Every native create
/// re-syncs the resolved legacy bytes into the package's instance data root,
/// resolving through the same recovery chain the built-in uses (per-widget
/// store, its .bak, the single-instance legacy store, its .bak), so a failed
/// native create never strands a stale snapshot and corrupt primaries fall
/// back to backups instead of stranding the package on defaults.
/// </summary>
public class NativeWidgetDataMigrationTests
{
    private static (string Root, string WidgetId) CreateRoot()
    {
        string root = Directory.CreateTempSubdirectory("deskbox-native-migrate").FullName;
        return (root, Guid.NewGuid().ToString());
    }

    // The internal (dataDirectory, widgetId) ctor does NOT add the
    // glance/widgets segment - passing it as the data directory reproduces
    // the production store path the migration reads from.
    private static DeskBox.Services.GlanceWidgetStore CreateProductionLayoutStore(string root, string widgetId) =>
        new(Path.Combine(root, "glance", "widgets"), widgetId);

    private static string TargetPath(string root, string publisher, string widgetId) =>
        Path.Combine(
            new DeskBox.Services.Plugins.NativePackageIdentity(publisher, "deskbox.glance")
                .ResolveInstanceDataRoot(root, widgetId),
            DeskBox.Services.Plugins.NativeWidgetDataMigration.DataFileName);

    [Fact]
    public async Task SyncCopiesHostStoreVerbatimIntoInstanceRoot()
    {
        (string root, string widgetId) = CreateRoot();
        try
        {
            var store = CreateProductionLayoutStore(root, widgetId);
            var data = new DeskBox.Models.GlanceWidgetData
            {
                RotationIntervalMinutes = 12,
                RandomOrder = false,
                TraditionalCalendarMode = DeskBox.Models.GlanceTraditionalCalendarMode.ChineseLunar,
            };
            data.LocalImagePaths.Add(@"C:\pictures\a.png");
            await store.SaveAsync(data);

            string publisher = "a".PadLeft(64, '0');
            DeskBox.Services.Plugins.NativeWidgetDataMigration.TryMigrate(publisher, "deskbox.glance", widgetId, root);

            string target = TargetPath(root, publisher, widgetId);
            Assert.True(File.Exists(target), "synced data file must exist in the instance data root");
            Assert.Equal(await File.ReadAllTextAsync(store.StorePath), await File.ReadAllTextAsync(target));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task HostStoreStaysAuthoritativeUntilCutover()
    {
        (string root, string widgetId) = CreateRoot();
        try
        {
            var store = CreateProductionLayoutStore(root, widgetId);
            string publisher = "b".PadLeft(64, '0');
            string target = TargetPath(root, publisher, widgetId);

            // First native create syncs state A.
            await store.SaveAsync(new DeskBox.Models.GlanceWidgetData { RotationIntervalMinutes = 5 });
            DeskBox.Services.Plugins.NativeWidgetDataMigration.TryMigrate(publisher, "deskbox.glance", widgetId, root);

            // The user changes settings while native is not running; the next
            // native create must NOT serve the stale snapshot. (Both values
            // come from the store's supported rotation steps - Normalize
            // snaps free-form values onto that set.)
            await store.SaveAsync(new DeskBox.Models.GlanceWidgetData { RotationIntervalMinutes = 60 });
            DeskBox.Services.Plugins.NativeWidgetDataMigration.TryMigrate(publisher, "deskbox.glance", widgetId, root);

            using JsonDocument synced = JsonDocument.Parse(await File.ReadAllTextAsync(target));
            Assert.Equal(60, synced.RootElement.GetProperty("rotationIntervalMinutes").GetInt32());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task SyncPrefersBackupWhenPrimaryIsCorrupt()
    {
        (string root, string widgetId) = CreateRoot();
        try
        {
            var store = CreateProductionLayoutStore(root, widgetId);
            await store.SaveAsync(new DeskBox.Models.GlanceWidgetData { RotationIntervalMinutes = 30 });
            // A torn write leaves the primary unreadable but the backup intact.
            await File.WriteAllTextAsync(store.StorePath + ".bak",
                """{ "rotationIntervalMinutes": 66 }""");
            await File.WriteAllTextAsync(store.StorePath, "{ torn");

            string publisher = "c".PadLeft(64, '0');
            DeskBox.Services.Plugins.NativeWidgetDataMigration.TryMigrate(publisher, "deskbox.glance", widgetId, root);

            using JsonDocument synced = JsonDocument.Parse(
                await File.ReadAllTextAsync(TargetPath(root, publisher, widgetId)));
            Assert.Equal(66, synced.RootElement.GetProperty("rotationIntervalMinutes").GetInt32());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task SyncFallsBackToLegacySingleInstanceStore()
    {
        (string root, string widgetId) = CreateRoot();
        try
        {
            // Upgrade path where the old single-instance store was never
            // migrated to per-widget files.
            Directory.CreateDirectory(Path.Combine(root, "glance"));
            string legacy = Path.Combine(root, "glance", "glance.json");
            await File.WriteAllTextAsync(legacy,
                """{ "version": 5, "rotationIntervalMinutes": 8, "futureField": "keep" }""");

            string publisher = "d".PadLeft(64, '0');
            DeskBox.Services.Plugins.NativeWidgetDataMigration.TryMigrate(publisher, "deskbox.glance", widgetId, root);

            using JsonDocument synced = JsonDocument.Parse(
                await File.ReadAllTextAsync(TargetPath(root, publisher, widgetId)));
            Assert.Equal(8, synced.RootElement.GetProperty("rotationIntervalMinutes").GetInt32());
            Assert.Equal("keep", synced.RootElement.GetProperty("futureField").GetString());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task NoHostDataLeavesPackageCopyAlone()
    {
        (string root, string widgetId) = CreateRoot();
        try
        {
            string publisher = "e".PadLeft(64, '0');
            string target = TargetPath(root, publisher, widgetId);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            await File.WriteAllTextAsync(target,
                """{ "version": 10, "rotationIntervalMinutes": 42 }""");

            // The package already owns data and the host has none: untouched.
            DeskBox.Services.Plugins.NativeWidgetDataMigration.TryMigrate(publisher, "deskbox.glance", widgetId, root);
            using JsonDocument kept = JsonDocument.Parse(await File.ReadAllTextAsync(target));
            Assert.Equal(42, kept.RootElement.GetProperty("rotationIntervalMinutes").GetInt32());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void PackagePersistsWithoutReflectionJson()
    {
        foreach (string relativePath in new[]
        {
            "src/DeskBox.GlancePackage/Rendering/GlanceDataFile.cs",
            "src/DeskBox.GlancePackage/Services/PackageFileStore.cs",
            "src/DeskBox/Services/Plugins/NativeWidgetDataMigration.cs",
        })
        {
            string source = File.ReadAllText(TestPaths.SourceFile(relativePath));
            Assert.DoesNotContain("JsonSerializer", source);
        }

        string builder = File.ReadAllText(TestPaths.SourceFile(
            "src/DeskBox.GlancePackage/Rendering/GlanceViewBuilder.cs"));
        Assert.Contains("GlanceDataFile.Load", builder);
        Assert.Contains("RotationIntervalMinutes > 0", builder);

        string pilot = File.ReadAllText(TestPaths.SourceFile(
            "src/DeskBox/Services/Plugins/NativeWidgetPilot.cs"));
        Assert.Contains("NativeWidgetDataMigration.TryMigrate", pilot);
    }
}
