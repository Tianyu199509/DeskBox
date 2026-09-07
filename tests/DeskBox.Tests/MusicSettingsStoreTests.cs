using DeskBox.Models;
using DeskBox.Services;

namespace DeskBox.Tests;

/// <summary>
/// Per-kind settings store pilot (Music, pluginization roadmap stage 2).
/// All store and migration tests run against isolated temporary roots so
/// the production data directory is never touched: stores use the internal
/// constructor, and the migration uses its internal directory seam (the
/// DeskBoxDataPathService.Current static caches on first touch, so an env
/// var redirect is not reliable in serial full-suite runs).
/// </summary>
public sealed class MusicSettingsStoreTests : IDisposable
{
    private readonly string _tempRoot;

    public MusicSettingsStoreTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "DeskBox.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
    }

    [Fact]
    public void Load_ReturnsDefaultsWhenStoreDoesNotExist()
    {
        var store = CreateStore();

        var settings = store.Load();

        Assert.True(settings.UseArtworkBackdrop);
        Assert.True(settings.EnableCoverHoverMotion);
        Assert.Equal(SettingsService.MusicDisplayModeAuto, settings.DisplayMode);
    }

    [Fact]
    public async Task Save_PersistsAndReloadsThroughResilientStore()
    {
        var store = CreateStore();

        await store.SaveAsync(new MusicWidgetSettings
        {
            UseArtworkBackdrop = false,
            EnableCoverHoverMotion = false,
            DisplayMode = "Cover"
        });

        var reloaded = new MusicSettingsStore(Path.Combine(_tempRoot, "store", "music")).Load();
        Assert.False(reloaded.UseArtworkBackdrop);
        Assert.False(reloaded.EnableCoverHoverMotion);
        Assert.Equal("Cover", reloaded.DisplayMode);
    }

    [Fact]
    public void Load_NormalizesInvalidDisplayMode()
    {
        var store = CreateStore();
        Directory.CreateDirectory(Path.GetDirectoryName(store.StorePath)!);
        File.WriteAllText(store.StorePath, "{\"displayMode\":\"Nonsense\"}");

        var settings = store.Load();

        Assert.Equal(SettingsService.MusicDisplayModeAuto, settings.DisplayMode);
    }

    [Fact]
    public void Migration_9_To_10_CopiesLegacyFieldsAndIsIdempotent()
    {
        string dataDirectory = Path.Combine(_tempRoot, "data");
        var settings = new AppSettings
        {
            MusicUseArtworkBackdrop = false,
            MusicEnableCoverHoverMotion = true,
            MusicDisplayMode = "RecordVertical"
        };

        Migration_9_To_10.Migrate(
            settings, dataDirectory);

        // The legacy fields stay as an inert compatibility source (N+2 removes them).
        Assert.False(settings.MusicUseArtworkBackdrop);

        string storePath = Path.Combine(dataDirectory, "music", "settings.json");
        Assert.True(File.Exists(storePath), "Migration must create the music store.");
        using (var document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(storePath)))
        {
            Assert.False(document.RootElement.GetProperty("useArtworkBackdrop").GetBoolean());
            Assert.Equal("RecordVertical", document.RootElement.GetProperty("displayMode").GetString());
        }

        // Idempotence: a second migration run never overwrites the store.
        File.WriteAllText(storePath,
            File.ReadAllText(storePath).Replace("RecordVertical", "Cover"));
        Migration_9_To_10.Migrate(
            new AppSettings(), dataDirectory);
        using var reparsed = System.Text.Json.JsonDocument.Parse(File.ReadAllText(storePath));
        Assert.Equal("Cover", reparsed.RootElement.GetProperty("displayMode").GetString());
    }

    [Fact]
    public async Task Load_RecoversFromOrphanedBackupWhenPrimaryIsMissing()
    {
        var store = CreateStore();
        await store.SaveAsync(new MusicWidgetSettings
        {
            UseArtworkBackdrop = false,
            DisplayMode = "Cover"
        });
        await store.SaveAsync(new MusicWidgetSettings
        {
            UseArtworkBackdrop = true,
            DisplayMode = "Auto"
        });
        File.Delete(store.StorePath);

        // The .bak is one generation behind (File.Replace semantics): losing
        // only the primary must recover that generation, not reset to defaults.
        var recovered = new MusicSettingsStore(
            Path.Combine(_tempRoot, "store", "music")).Load();

        Assert.False(recovered.UseArtworkBackdrop);
        Assert.Equal("Cover", recovered.DisplayMode);
        Assert.True(File.Exists(store.StorePath), "Load must restore the primary from the backup.");
    }

    [Fact]
    public async Task Load_QuarantinesCorruptPrimaryAndReadsBackup()
    {
        var store = CreateStore();
        await store.SaveAsync(new MusicWidgetSettings
        {
            UseArtworkBackdrop = false,
            DisplayMode = "Cover"
        });
        await store.SaveAsync(new MusicWidgetSettings
        {
            UseArtworkBackdrop = true,
            DisplayMode = "Auto"
        });
        File.WriteAllText(store.StorePath, "{ not valid json");

        var recovered = new MusicSettingsStore(
            Path.Combine(_tempRoot, "store", "music")).Load();

        Assert.False(recovered.UseArtworkBackdrop);
        Assert.Equal("Cover", recovered.DisplayMode);
        Assert.NotEmpty(Directory.GetFiles(
            Path.GetDirectoryName(store.StorePath)!,
            "settings.json.corrupt-*"));
        string restoredPrimary = File.ReadAllText(store.StorePath);
        Assert.Contains("Cover", restoredPrimary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SaveSynchronously_WritesThroughAtomicReplaceAndThrowsOnFailure()
    {
        var store = CreateStore();
        await store.SaveAsync(new MusicWidgetSettings
        {
            UseArtworkBackdrop = false,
            DisplayMode = "Cover"
        });

        store.SaveSynchronously(new MusicWidgetSettings
        {
            UseArtworkBackdrop = true,
            DisplayMode = "Auto"
        });

        Assert.Contains("Auto", File.ReadAllText(store.StorePath), StringComparison.Ordinal);
        Assert.Contains(
            "Cover",
            File.ReadAllText(ResilientJsonStore.GetBackupPath(store.StorePath)),
            StringComparison.Ordinal);

        // A primary that cannot be replaced must throw so the migration
        // pipeline can stop instead of stamping the schema version.
        File.Delete(store.StorePath);
        Directory.CreateDirectory(store.StorePath);
        Assert.Throws<IOException>(() =>
            store.SaveSynchronously(new MusicWidgetSettings()));
        Directory.Delete(store.StorePath);
    }

    [Fact]
    public void Pipeline_LeavesVersionAtLastSuccessWhenAMigrationFails()
    {
        var settings = new AppSettings { SchemaVersion = 5 };

        bool applied = new SettingsMigrationPipeline(
        [
            new FakeMigration(5, succeeded: true),
            new FakeMigration(6, succeeded: false),
            new FakeMigration(7, succeeded: true)
        ]).RunMigrations(settings);

        // Partial progress is kept (so it is saved and not redone), the
        // failed step is retried next launch, and later steps never run.
        Assert.True(applied);
        Assert.Equal(6, settings.SchemaVersion);
    }

    [Fact]
    public void Pipeline_StampsCurrentVersionWhenAllMigrationsSucceed()
    {
        var settings = new AppSettings { SchemaVersion = 9 };

        bool applied = new SettingsMigrationPipeline(
        [
            new FakeMigration(9, succeeded: true)
        ]).RunMigrations(settings);

        Assert.True(applied);
        Assert.Equal(SettingsMigrationPipeline.CurrentSchemaVersion, settings.SchemaVersion);
    }

    [Fact]
    public void Pipeline_StopsWhenARegistryGapIsDetected()
    {
        // Registry missing the 6->7 step: the old >= comparison ran 7->8
        // directly and silently skipped it; exact-step matching must stop.
        var settings = new AppSettings { SchemaVersion = 5 };

        bool applied = new SettingsMigrationPipeline(
        [
            new FakeMigration(5, succeeded: true),
            new FakeMigration(7, succeeded: true)
        ]).RunMigrations(settings);

        Assert.True(applied);
        Assert.Equal(6, settings.SchemaVersion);
    }

    [Fact]
    public void Pipeline_NeverRunsStepsBeyondTheCurrentVersion()
    {
        // A step registered for a future schema version must never execute.
        var settings = new AppSettings { SchemaVersion = 9 };

        bool applied = new SettingsMigrationPipeline(
        [
            new FakeMigration(9, succeeded: true),
            new FakeMigration(10, succeeded: true)
        ]).RunMigrations(settings);

        Assert.True(applied);
        Assert.Equal(SettingsMigrationPipeline.CurrentSchemaVersion, settings.SchemaVersion);
    }

    [Fact]
    public void Migration_9_To_10_PropagatesExternalWriteFailures()
    {
        string dataDirectory = Path.Combine(_tempRoot, "blocked-data");
        Directory.CreateDirectory(dataDirectory);
        // A file named "music" makes the store's directory creation throw -
        // exactly a failed external write during migration 9-to-10. The
        // migration must let it propagate so the pipeline can stop.
        File.WriteAllText(Path.Combine(dataDirectory, "music"), "blocker");

        Assert.ThrowsAny<Exception>(() =>
            Migration_9_To_10.Migrate(new AppSettings(), dataDirectory));
    }

    private sealed class FakeMigration(int fromVersion, bool succeeded) : ISettingsMigration
    {
        public int FromVersion { get; } = fromVersion;

        public void Migrate(AppSettings settings)
        {
            if (!succeeded)
            {
                throw new InvalidOperationException("Injected migration failure.");
            }
        }
    }

    [Fact]
    public void Pipeline_RegistersTheMusicMigration()
    {
        string pipelineSource = File.ReadAllText(TestPaths.SourceFile(
            "src/DeskBox/Services/SettingsMigrationService.cs"));
        Assert.Contains("new Migration_9_To_10()", pipelineSource, StringComparison.Ordinal);
        Assert.Contains("CurrentSchemaVersion = 10", pipelineSource, StringComparison.Ordinal);
    }

    [Fact]
    public void Consumers_UseTheProcessWideSingletonAndSerializedWritePath()
    {
        string storeSource = File.ReadAllText(TestPaths.SourceFile(
            "src/DeskBox/Services/MusicSettingsStore.cs"));
        string migrationSource = File.ReadAllText(TestPaths.SourceFile(
            "src/DeskBox/Services/SettingsMigrationService.cs"));
        string settingsVmSource = File.ReadAllText(TestPaths.SourceFile(
            "src/DeskBox/ViewModels/SettingsViewModel.cs"));
        string widgetVmSource = File.ReadAllText(TestPaths.SourceFile(
            "src/DeskBox/ViewModels/MusicWidgetViewModel.cs"));

        // Singleton: two independently constructed stores would each pin a
        // private cache forever and the widget would never see settings-page
        // updates (the live-sync regression this pins shut).
        Assert.Contains(
            "private readonly MusicSettingsStore _musicSettingsStore = MusicSettingsStore.Current;",
            settingsVmSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "private readonly MusicSettingsStore _musicSettingsStore = MusicSettingsStore.Current;",
            widgetVmSource,
            StringComparison.Ordinal);
        Assert.DoesNotContain("new MusicSettingsStore()", settingsVmSource, StringComparison.Ordinal);
        Assert.DoesNotContain("new MusicSettingsStore()", widgetVmSource, StringComparison.Ordinal);

        // Persistence: writes go through the serialized chain enqueued under
        // the cache lock (disk order == update order), never a free-running
        // fire-and-forget task.
        Assert.Contains("EnqueuePersist(Clone(_cached));", storeSource, StringComparison.Ordinal);
        Assert.DoesNotContain("_ = PersistAsync(", storeSource, StringComparison.Ordinal);

        // Round 5: Current is explicitly transitional (static service
        // locator, not the final per-kind store model) so the pattern is
        // not copied to Weather/Todo/QuickCapture stores.
        Assert.Contains("LIFECYCLE NOTE", storeSource, StringComparison.Ordinal);
        Assert.Contains("do not copy", storeSource, StringComparison.Ordinal);

        // Migration: synchronous failure-propagating write, never a blocking
        // wait on an async continuation (UI-thread startup deadlock).
        Assert.Contains("store.SaveSynchronously(migrated);", migrationSource, StringComparison.Ordinal);
        Assert.DoesNotContain("GetAwaiter().GetResult()", migrationSource, StringComparison.Ordinal);
        Assert.Contains("settings.SchemaVersion = version;", migrationSource, StringComparison.Ordinal);
    }

    private MusicSettingsStore CreateStore() =>
        new(Path.Combine(_tempRoot, "store", "music"));

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempRoot))
            {
                Directory.Delete(_tempRoot, recursive: true);
            }
        }
        catch
        {
            // Best-effort cleanup for files briefly held by antivirus.
        }
    }
}
