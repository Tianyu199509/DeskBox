using DeskBox.Features.ManagedStorage;
using DeskBox.Services;

namespace DeskBox.Tests;

public sealed class ManagedStorageSettingsCoordinatorTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "DeskBox.Tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void SetDefaultRootPath_NormalizesPersistsAndKeepsTheBroadcast()
    {
        var settings = new SettingsService(_root);
        var coordinator = new ManagedStorageSettingsCoordinator(settings);
        var editor = new ManagedStorageSettingsViewModel(coordinator);
        int notified = 0;
        settings.SettingsChanged += () => notified++;

        string rawPath = Path.Combine(_root, "nested", "..", "store ");
        string normalized = editor.SetDefaultRootPath(rawPath);

        Assert.Equal(Path.GetFullPath(Path.Combine(_root, "store")), normalized);
        Assert.Equal(normalized, editor.ReadDefaultRootPath());
        Assert.Equal(normalized, settings.Settings.FileWidget.DefaultManagedStorageRootPath);
        // The post-migration commit keeps the regular SettingsChanged pass
        // so widget consumers re-read the stored root, exactly as the
        // pre-migration command did.
        Assert.Equal(1, notified);
    }

    [Fact]
    public void SetDefaultRootPath_BlankPathFallsBackToTheDefaultRoot()
    {
        var settings = new SettingsService(_root);
        var coordinator = new ManagedStorageSettingsCoordinator(settings);

        string fallback = coordinator.SetDefaultRootPath("   ");

        Assert.Equal(SettingsService.GetDefaultManagedStorageRootPath(), fallback);
        Assert.Equal(fallback, settings.Settings.FileWidget.DefaultManagedStorageRootPath);
    }

    [Fact]
    public async Task RootPathWrite_RoundTripsThroughDisk()
    {
        var settings = new SettingsService(_root);
        var coordinator = new ManagedStorageSettingsCoordinator(settings);
        string storeRoot = Path.Combine(_root, "managed-store");
        Assert.Equal(storeRoot, coordinator.SetDefaultRootPath(storeRoot));
        await settings.SaveAsync();

        var reloaded = new SettingsService(_root);
        await reloaded.LoadAsync();
        Assert.Equal(
            storeRoot,
            new ManagedStorageSettingsCoordinator(reloaded).ReadDefaultRootPath());
    }

    [Fact]
    public async Task StoppedCoordinator_RejectsFurtherWrites()
    {
        var settings = new SettingsService(_root);
        var coordinator = new ManagedStorageSettingsCoordinator(settings);
        coordinator.Stop();

        Assert.True(coordinator.IsStopped);
        Assert.Throws<ObjectDisposedException>(
            () => coordinator.SetDefaultRootPath(Path.Combine(_root, "late")));

        await settings.SaveAsync();
        Assert.NotEqual(
            Path.Combine(_root, "late"),
            settings.Settings.FileWidget.DefaultManagedStorageRootPath);
    }
}
