namespace DeskBox.Services;

using DeskBox.Contracts;

/// <summary>
/// In-process default of <see cref="ISettingsRestoreActions"/>: a thin
/// wrapper over the App-owned backup and attachment-health services.
/// </summary>
public sealed class InProcessSettingsRestoreActions : ISettingsRestoreActions
{
    private readonly DeskBoxDataBackupService _dataBackupService;
    private readonly DeskBoxAttachmentHealthService _attachmentHealthService;

    public InProcessSettingsRestoreActions(
        DeskBoxDataBackupService dataBackupService,
        DeskBoxAttachmentHealthService attachmentHealthService)
    {
        _dataBackupService = dataBackupService;
        _attachmentHealthService = attachmentHealthService;
    }

    public Task<DeskBoxRestorePreparation> PrepareRestoreAsync(string archivePath) =>
        _dataBackupService.PrepareRestoreAsync(archivePath);

    public Task<bool> ConfirmPendingFullRestoreAsync() =>
        _dataBackupService.ConfirmPendingFullRestoreAsync();

    public Task CancelPendingRestoreAsync() =>
        _dataBackupService.CancelPendingRestoreAsync();

    public Task<IReadOnlyList<DeskBoxBackupSnapshotInfo>> GetSnapshotInventoryAsync() =>
        _dataBackupService.GetSnapshotInventoryAsync();

    public Task DeleteSnapshotAsync(string snapshotPath) =>
        _dataBackupService.DeleteSnapshotAsync(snapshotPath);

    public Task<DeskBoxAttachmentHealthReport> ScanAttachmentHealthAsync() =>
        _attachmentHealthService.ScanAsync();
}
