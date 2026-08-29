using DeskBox.Helpers;
using DeskBox.Models;
using DeskBox.Services;

namespace DeskBox.Tests;

public sealed class ManagedStorageDesktopShortcutServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "DeskBoxShortcutTests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public void AppSettings_DesktopStorageShortcutDefaultsOnForExistingProfiles()
    {
        var settings = new AppSettings();

        Assert.True(settings.ManagedStorageDesktopShortcutEnabled);
        Assert.Equal(string.Empty, settings.ManagedStorageDesktopShortcutPath);
    }

    [Fact]
    public void GetAvailableShortcutPath_DoesNotOverwriteAnExistingName()
    {
        Directory.CreateDirectory(_root);
        string existing = Path.Combine(
            _root,
            ManagedStorageDesktopShortcutService.ShortcutFileName);
        File.WriteAllText(existing, "belongs to the user");

        string candidate =
            ManagedStorageDesktopShortcutService.GetAvailableShortcutPath(_root);

        Assert.Equal(Path.Combine(_root, "DeskBox Files (2).lnk"), candidate);
        Assert.Equal("belongs to the user", File.ReadAllText(existing));
    }

    [Fact]
    public async Task SyncAsync_CreatesRetargetsAndRemovesTheOwnedShortcut()
    {
        string dataDirectory = Path.Combine(_root, "data");
        string desktopDirectory = Path.Combine(_root, "desktop");
        string firstRoot = Path.Combine(_root, "storage-one");
        string secondRoot = Path.Combine(_root, "storage-two");
        Directory.CreateDirectory(desktopDirectory);
        Directory.CreateDirectory(firstRoot);
        Directory.CreateDirectory(secondRoot);
        File.WriteAllText(Path.Combine(firstRoot, "kept.txt"), "one");
        File.WriteAllText(Path.Combine(secondRoot, "kept.txt"), "two");

        var settingsService = new SettingsService(dataDirectory);
        settingsService.Settings.DefaultManagedStorageRootPath = firstRoot;
        var service = new ManagedStorageDesktopShortcutService(
            settingsService,
            desktopDirectory);

        await service.SyncAsync();

        string shortcutPath = settingsService.Settings.ManagedStorageDesktopShortcutPath;
        Assert.True(File.Exists(shortcutPath));
        Assert.Equal(
            Path.GetFullPath(firstRoot),
            Path.GetFullPath(ShortcutHelper.ReadStoredMetadata(shortcutPath)!.TargetPath));

        settingsService.Settings.DefaultManagedStorageRootPath = secondRoot;
        await service.SyncAsync(firstRoot);

        Assert.Equal(shortcutPath, settingsService.Settings.ManagedStorageDesktopShortcutPath);
        Assert.Equal(
            Path.GetFullPath(secondRoot),
            Path.GetFullPath(ShortcutHelper.ReadStoredMetadata(shortcutPath)!.TargetPath));

        settingsService.Settings.ManagedStorageDesktopShortcutEnabled = false;
        await service.SyncAsync();

        Assert.False(File.Exists(shortcutPath));
        Assert.Equal(string.Empty, settingsService.Settings.ManagedStorageDesktopShortcutPath);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
