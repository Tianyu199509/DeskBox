using DeskBox.Services;
namespace DeskBox.Tests;

public sealed class NativeMusicSettingsMigrationTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("deskbox-music-migration-").FullName;
    [Fact]
    public void Version9GlobalFieldsMigrateWithoutResettingPreferences()
    {
        File.WriteAllText(Path.Combine(_root, "settings.json"), """{"schemaVersion":9,"musicUseArtworkBackdrop":false,"musicEnableCoverHoverMotion":false,"musicDisplayMode":"RecordVertical"}""");
        var settings = MusicInstanceMigration.ReadLegacySettings(_root);
        Assert.False(settings.UseArtworkBackdrop);
        Assert.False(settings.EnableCoverHoverMotion);
        Assert.Equal("RecordVertical", settings.DisplayMode);
    }
    [Fact]
    public void Version10PerKindStoreWinsOverLegacyMirror()
    {
        Directory.CreateDirectory(Path.Combine(_root, "music"));
        File.WriteAllText(Path.Combine(_root, "settings.json"), """{"musicDisplayMode":"Cover"}""");
        File.WriteAllText(Path.Combine(_root, "music", "settings.json"), """{"schemaVersion":1,"displayMode":"Controls","useArtworkBackdrop":false}""");
        var settings = MusicInstanceMigration.ReadLegacySettings(_root);
        Assert.Equal("Controls", settings.DisplayMode);
        Assert.False(settings.UseArtworkBackdrop);
    }
    [Fact]
    public void WrongTypedPrimaryFallsBackToBackupBeforeLegacy()
    {
        Directory.CreateDirectory(Path.Combine(_root, "music"));
        File.WriteAllText(Path.Combine(_root, "settings.json"), """{"musicDisplayMode":"Cover"}""");
        File.WriteAllText(Path.Combine(_root, "music", "settings.json"), """{"useArtworkBackdrop":"invalid"}""");
        File.WriteAllText(Path.Combine(_root, "music", "settings.json.bak"), """{"displayMode":"RecordHorizontal"}""");
        Assert.Equal("RecordHorizontal", MusicInstanceMigration.ReadLegacySettings(_root).DisplayMode);
    }
    [Fact]
    public async Task StoreNotifiesOnlyAfterBytesArePersisted()
    {
        var store = new MusicSettingsStore(Path.Combine(_root, "music"));
        string? observed = null;
        store.Persisted += () => observed = File.ReadAllText(store.StorePath);
        await store.SaveAsync(new MusicWidgetSettings { DisplayMode = "Cover" });
        Assert.NotNull(observed);
        Assert.Contains("Cover", observed);
    }
    [Fact]
    public async Task SubscriberFailureDoesNotBreakLaterWrites()
    {
        var store = new MusicSettingsStore(Path.Combine(_root, "music"));
        store.Persisted += () => throw new InvalidOperationException("test subscriber");
        await store.SaveAsync(new MusicWidgetSettings { DisplayMode = "Cover" });
        await store.SaveAsync(new MusicWidgetSettings { DisplayMode = "Controls" });
        Assert.Contains("Controls", File.ReadAllText(store.StorePath));
    }
    public void Dispose() { Directory.Delete(_root, recursive: true); }
}

