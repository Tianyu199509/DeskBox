using DeskBox.Contracts;

namespace DeskBox.Services;

internal sealed class BackupBackend(
    SettingsService settings,
    DeskBoxDataBackupService local,
    CloudBackupService cloud) : IBackupBackend
{
    public bool LocalScheduleEnabled => local.AutomaticBackupOptions.IsEnabled;
    public bool CloudScheduleEnabled => cloud.Options.IsConfigured;

    public void RefreshOptions()
    {
        local.UpdateAutomaticBackupOptions(DataBackupSettingsPolicy.GetOptions(settings.Settings));
        cloud.UpdateOptions(CloudBackupSettingsPolicy.GetOptions(settings.Settings));
    }

    public async Task PrepareManualBackupAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!await settings.FlushPendingSaveAsync(notifySubscribers: false))
            throw new IOException("Settings could not be saved before creating a backup.");
        cancellationToken.ThrowIfCancellationRequested();
        RefreshOptions();
    }

    public Task<LocalBackupResult> CreateSnapshotAsync(bool force, CancellationToken cancellationToken) =>
        local.CreateAutomaticSnapshotResultAsync(force, cancellationToken);

    public Task<string> ExportAsync(string destinationDirectory, CancellationToken cancellationToken) =>
        local.ExportBackupAsync(destinationDirectory, cancellationToken);

    public Task<CloudBackupRunResult> UploadAsync(bool scheduled, CancellationToken cancellationToken) =>
        scheduled ? cloud.RunScheduledIfDueAsync(cancellationToken) : cloud.RunBackupNowAsync(cancellationToken);
}
