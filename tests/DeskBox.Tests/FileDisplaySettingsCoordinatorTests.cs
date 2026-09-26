using DeskBox.Contracts;
using DeskBox.Features.FileDisplay;
using DeskBox.Models;
using DeskBox.Services;

namespace DeskBox.Tests;

public sealed class FileDisplaySettingsCoordinatorTests : IDisposable
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
    public void ToggleWrites_PersistThroughThePort_AndSkipUnchangedSaves()
    {
        var settings = new SettingsService(_root);
        var coordinator = new FileDisplaySettingsCoordinator(settings);
        var editor = new FileDisplaySettingsViewModel(coordinator);
        int notified = 0;
        settings.SettingsChanged += () => notified++;

        editor.SetShowFileExtensions(true);
        editor.SetHideShortcutExtensionWhenShowingFileExtensions(false);
        editor.SetHideShortcutArrowOverlay(false);
        editor.SetShowImageFilesAsIcons(true);
        editor.SetShowListItemDetails(true);
        editor.SetShowFileItemPathTooltips(false);

        Assert.True(settings.Settings.ShowFileExtensions);
        Assert.False(settings.Settings.HideShortcutExtensionWhenShowingFileExtensions);
        Assert.False(settings.Settings.HideShortcutArrowOverlay);
        Assert.True(settings.Settings.ShowImageFilesAsIcons);
        Assert.True(settings.Settings.ShowListItemDetails);
        Assert.False(settings.Settings.ShowFileItemPathTooltips);
        Assert.Equal(6, notified);

        // Unchanged writes must not notify or save again; the icon-cache and
        // reprojection consumers only react to real SettingsChanged passes.
        editor.SetShowFileExtensions(true);
        editor.SetShowImageFilesAsIcons(true);
        Assert.Equal(6, notified);
    }

    [Fact]
    public async Task FileDisplayWrites_RoundTripThroughDisk()
    {
        var settings = new SettingsService(_root);
        var coordinator = new FileDisplaySettingsCoordinator(settings);

        coordinator.SetShowFileExtensions(true);
        coordinator.SetHideShortcutExtensionWhenShowingFileExtensions(false);
        coordinator.SetHideShortcutArrowOverlay(false);
        coordinator.SetShowImageFilesAsIcons(true);
        coordinator.SetShowListItemDetails(true);
        coordinator.SetShowFileItemPathTooltips(false);
        await settings.SaveAsync();

        var reloaded = new SettingsService(_root);
        await reloaded.LoadAsync();
        FileDisplaySettingsSnapshot snapshot =
            new FileDisplaySettingsCoordinator(reloaded).ReadAll();
        Assert.True(snapshot.ShowFileExtensions);
        Assert.False(snapshot.HideShortcutExtensionWhenShowingFileExtensions);
        Assert.False(snapshot.HideShortcutArrowOverlay);
        Assert.True(snapshot.ShowImageFilesAsIcons);
        Assert.True(snapshot.ShowListItemDetails);
        Assert.False(snapshot.ShowFileItemPathTooltips);
    }

    [Fact]
    public async Task Writes_PersistThroughDiskFlush()
    {
        var settings = new SettingsService(_root);
        var coordinator = new FileDisplaySettingsCoordinator(settings);

        coordinator.SetShowFileExtensions(true);
        coordinator.SetShowImageFilesAsIcons(true);
        coordinator.SetShowListItemDetails(true);
        await settings.SaveAsync();

        var reloaded = new SettingsService(_root);
        await reloaded.LoadAsync();
        Assert.True(reloaded.Settings.ShowFileExtensions);
        Assert.True(reloaded.Settings.ShowImageFilesAsIcons);
        Assert.True(reloaded.Settings.ShowListItemDetails);
        // Untouched fields keep their persisted defaults.
        Assert.True(reloaded.Settings.HideShortcutArrowOverlay);
        Assert.True(reloaded.Settings.HideShortcutExtensionWhenShowingFileExtensions);
        Assert.True(reloaded.Settings.ShowFileItemPathTooltips);
    }

    [Fact]
    public async Task StoppedCoordinator_RejectsFurtherWrites()
    {
        var settings = new SettingsService(_root);
        var coordinator = new FileDisplaySettingsCoordinator(settings);
        coordinator.Stop();

        Assert.Throws<ObjectDisposedException>(() => coordinator.SetShowFileExtensions(true));
        Assert.Throws<ObjectDisposedException>(
            () => coordinator.SetHideShortcutExtensionWhenShowingFileExtensions(false));
        Assert.Throws<ObjectDisposedException>(() => coordinator.SetHideShortcutArrowOverlay(false));
        Assert.Throws<ObjectDisposedException>(() => coordinator.SetShowImageFilesAsIcons(true));
        Assert.Throws<ObjectDisposedException>(() => coordinator.SetShowListItemDetails(true));
        Assert.Throws<ObjectDisposedException>(() => coordinator.SetShowFileItemPathTooltips(false));
        Assert.True(coordinator.IsStopped);

        await settings.SaveAsync();
        Assert.False(settings.Settings.ShowFileExtensions);
        Assert.True(settings.Settings.HideShortcutArrowOverlay);
    }
}
