namespace DeskBox.Tests;

/// <summary>
/// D3 legacy data migration: the built-in Glance store file is handed off to
/// the native package's instance data root (byte-for-byte, idempotent) so the
/// package owns its settings from the first native create. The host never
/// re-serializes during migration - the frozen JSON call baseline stays
/// untouched, and the package-side reader defaults fields older file
/// versions don't carry.
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

    [Fact]
    public async Task MigrateCopiesLegacyStoreVerbatimIntoInstanceRoot()
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

            string instanceRoot = new DeskBox.Services.Plugins.NativePackageIdentity(publisher, "deskbox.glance")
                .ResolveInstanceDataRoot(root, widgetId);
            string target = Path.Combine(
                instanceRoot, DeskBox.Services.Plugins.NativeWidgetDataMigration.DataFileName);
            Assert.True(File.Exists(target), "migrated data file must exist in the instance data root");
            Assert.Equal(await File.ReadAllTextAsync(store.StorePath), await File.ReadAllTextAsync(target));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task MigrateIsIdempotentPackageDataWins()
    {
        (string root, string widgetId) = CreateRoot();
        try
        {
            var store = CreateProductionLayoutStore(root, widgetId);
            await store.SaveAsync(new DeskBox.Models.GlanceWidgetData());
            string publisher = "b".PadLeft(64, '0');

            DeskBox.Services.Plugins.NativeWidgetDataMigration.TryMigrate(publisher, "deskbox.glance", widgetId, root);
            string instanceRoot = new DeskBox.Services.Plugins.NativePackageIdentity(publisher, "deskbox.glance")
                .ResolveInstanceDataRoot(root, widgetId);
            string target = Path.Combine(
                instanceRoot, DeskBox.Services.Plugins.NativeWidgetDataMigration.DataFileName);
            string first = await File.ReadAllTextAsync(target);

            // Legacy store changes after migration must NOT overwrite package data.
            await File.WriteAllTextAsync(store.StorePath, first + "\n");
            DeskBox.Services.Plugins.NativeWidgetDataMigration.TryMigrate(publisher, "deskbox.glance", widgetId, root);
            Assert.Equal(first, await File.ReadAllTextAsync(target));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void MissingLegacyStoreCreatesNothing()
    {
        (string root, string widgetId) = CreateRoot();
        try
        {
            string publisher = "c".PadLeft(64, '0');
            DeskBox.Services.Plugins.NativeWidgetDataMigration.TryMigrate(publisher, "deskbox.glance", widgetId, root);
            string instanceRoot = new DeskBox.Services.Plugins.NativePackageIdentity(publisher, "deskbox.glance")
                .ResolveInstanceDataRoot(root, widgetId);
            Assert.False(File.Exists(Path.Combine(
                instanceRoot, DeskBox.Services.Plugins.NativeWidgetDataMigration.DataFileName)));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void PackageConsumesMigratedSettingsWithoutReflectionJson()
    {
        string dataFile = File.ReadAllText(TestPaths.SourceFile(
            "src/DeskBox.GlancePackage/Rendering/GlanceDataFile.cs"));
        Assert.DoesNotContain("JsonSerializer", dataFile);
        Assert.Contains("rotationIntervalMinutes", dataFile);
        Assert.Contains("traditionalCalendarMode", dataFile);

        string builder = File.ReadAllText(TestPaths.SourceFile(
            "src/DeskBox.GlancePackage/Rendering/GlanceViewBuilder.cs"));
        Assert.Contains("GlanceDataFile.Load", builder);
        Assert.Contains("RotationIntervalMinutes", builder);
        Assert.Contains("LocalImagePaths", builder);

        string pilot = File.ReadAllText(TestPaths.SourceFile(
            "src/DeskBox/Services/Plugins/NativeWidgetPilot.cs"));
        Assert.Contains("NativeWidgetDataMigration.TryMigrate", pilot);
    }
}
