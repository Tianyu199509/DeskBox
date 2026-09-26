using DeskBox.Features.Maintenance;
using DeskBox.Services;

namespace DeskBox.Tests;

public sealed class MaintenanceSettingsCoordinatorTests : IDisposable
{
    private static readonly DateTimeOffset Stamp =
        new(2026, 9, 26, 12, 0, 0, TimeSpan.FromHours(8));

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
    public void RecordUpdateCheck_StampsWithoutBroadcasting()
    {
        var settings = new SettingsService(_root);
        var coordinator = new MaintenanceSettingsCoordinator(settings);
        var editor = new MaintenanceSettingsViewModel(coordinator);
        int notified = 0;
        settings.SettingsChanged += () => notified++;

        editor.RecordUpdateCheck(Stamp);

        Assert.Equal(Stamp, editor.ReadLastUpdateCheckAt());
        Assert.Equal(Stamp, settings.Settings.Core.LastUpdateCheckAt);
        // The record write is silent: no SettingsChanged pass, so widget
        // consumers never react to a timestamp stamp.
        Assert.Equal(0, notified);
    }

    [Fact]
    public async Task UpdateCheckStamp_RoundTripsThroughDisk()
    {
        var settings = new SettingsService(_root);
        var coordinator = new MaintenanceSettingsCoordinator(settings);
        coordinator.RecordUpdateCheck(Stamp);
        await settings.SaveAsync();

        var reloaded = new SettingsService(_root);
        await reloaded.LoadAsync();
        Assert.Equal(
            Stamp,
            new MaintenanceSettingsCoordinator(reloaded).ReadLastUpdateCheckAt());
    }

    [Fact]
    public async Task StoppedCoordinator_RejectsFurtherWrites()
    {
        var settings = new SettingsService(_root);
        var coordinator = new MaintenanceSettingsCoordinator(settings);
        coordinator.Stop();

        Assert.True(coordinator.IsStopped);
        Assert.Throws<ObjectDisposedException>(
            () => coordinator.RecordUpdateCheck(Stamp));

        await settings.SaveAsync();
        Assert.Null(settings.Settings.Core.LastUpdateCheckAt);
    }
}
