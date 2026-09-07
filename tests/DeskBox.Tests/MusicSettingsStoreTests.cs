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
    public void Pipeline_RegistersTheMusicMigration()
    {
        string pipelineSource = File.ReadAllText(TestPaths.SourceFile(
            "src/DeskBox/Services/SettingsMigrationService.cs"));
        Assert.Contains("_migrations.Add(new Migration_9_To_10());", pipelineSource, StringComparison.Ordinal);
        Assert.Contains("CurrentSchemaVersion = 10", pipelineSource, StringComparison.Ordinal);
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
