namespace DeskBox;

using DeskBox.Contracts;
using DeskBox.Services;

/// <summary>
/// Main-process side of the settings-host IPC pipe (batch 3c): owns the
/// server, the method router over the in-process facades, and the event
/// pushes — reload after every durable local save (carrying the host commit
/// id of the last applied external snapshot so the settings host can
/// echo-suppress its own writes), hotkey registration changes, and the
/// display/screen-home notifications.
/// </summary>
public partial class App
{
    private SettingsIpcServer? _settingsIpcServer;
    private SettingsIpcMethodRouter? _settingsIpcRouter;

    private void StartSettingsIpcServer()
    {
        if (_settingsIpcServer is not null)
        {
            return;
        }

        var router = new SettingsIpcMethodRouter(
            SettingsService,
            new InProcessSettingsHotkeyActions(
                GlobalHotkeyService,
                SearchHotkeyService,
                DesktopDoubleClickActivationService,
                SettingsService),
            new InProcessSettingsDataActions(
                QuickCaptureService ?? throw new InvalidOperationException("Quick Capture service is not initialized."),
                LocalizationService),
            new InProcessSettingsRestoreActions(
                DataBackupService ?? throw new InvalidOperationException("Backup service is not initialized."),
                AttachmentHealthService ?? throw new InvalidOperationException("Attachment health service is not initialized.")),
            new InProcessSettingsWidgetActions(
                WidgetManager,
                SettingsService,
                FileService,
                OrganizerService,
                LocalizationService),
            new InProcessSettingsAppActions(this, LocalizationService),
            pushEvent: (method, payload) =>
                _settingsIpcServer?.SendEventAsync(method, payload) ?? Task.CompletedTask,
            backupCommands: _backupRuntime,
            backupRestore: _backupRestoreActions,
            backupSettings: _backupSettings,
            appUpdate: AppUpdateService);
        _settingsIpcRouter = router;
        _settingsIpcServer = SettingsIpcServer.Start(
            DeskBoxDataPathService.Current.SettingsIpcPipeName,
            router.DispatchAsync);

        SettingsService.LocalPersistCompleted += OnSettingsIpcLocalPersistCompleted;
        GlobalHotkeyService.RegistrationChanged += OnSettingsIpcHotkeyRegistrationChanged;
        DisplayTopologyChanged += OnSettingsIpcDisplayTopologyChanged;
        ScreenHomeChanged += OnSettingsIpcScreenHomeChanged;
        CloudBackupService.BackupRunCompleted += OnSettingsIpcBackupRunCompleted;
        AppUpdateService.CheckCompleted += OnSettingsIpcUpdateCheckCompleted;
        AppUpdateService.DownloadProgressChanged += OnSettingsIpcUpdateDownloadProgress;
        AppUpdateService.DownloadCompleted += OnSettingsIpcUpdateDownloadCompleted;
        Log("[SettingsIpc] Server started");
    }

    private void OnSettingsIpcLocalPersistCompleted(long mainCommitId)
    {
        long? hostCommitId = _settingsIpcRouter?.LastAppliedHostCommitId;
        _ = PushSettingsIpcEventAsync(
            SettingsIpcMethods.SettingsReloaded,
            SettingsIpcReloadPayload.Compose(mainCommitId, hostCommitId));
    }

    private void OnSettingsIpcHotkeyRegistrationChanged()
    {
        _ = PushSettingsIpcEventAsync(SettingsIpcMethods.HotkeyRegistrationChanged, null);
    }

    private void OnSettingsIpcDisplayTopologyChanged()
    {
        _ = PushSettingsIpcEventAsync(SettingsIpcMethods.AppDisplayTopologyChanged, null);
    }

    private void OnSettingsIpcScreenHomeChanged()
    {
        _ = PushSettingsIpcEventAsync(SettingsIpcMethods.AppScreenHomeChanged, null);
    }

    private void OnSettingsIpcBackupRunCompleted(CloudBackupRunCompletedInfo info)
    {
        if (info.Endpoint is not { } endpoint)
        {
            return;
        }

        // Same conversion BackupSettingsCoordinator applies for its
        // UploadCompleted subscribers (non-secret endpoint identity only).
        _ = PushSettingsIpcEventAsync(
            SettingsIpcMethods.BackupUploadCompleted,
            SettingsIpcPayload.Encode(
                new BackupUploadNotification(endpoint, info.Uploaded, info.RemoteFilePath),
                SettingsIpcJsonContext.Default.BackupUploadNotification));
    }

    private void OnSettingsIpcUpdateCheckCompleted(DeskBox.Models.AppUpdateCheckResult result) =>
        PushEncodedEventAsync(
            SettingsIpcMethods.UpdateCheckCompleted,
            result,
            SettingsIpcJsonContext.Default.AppUpdateCheckResult);

    private void OnSettingsIpcUpdateDownloadProgress(DeskBox.Models.AppUpdateDownloadProgress progress) =>
        PushEncodedEventAsync(
            SettingsIpcMethods.UpdateDownloadProgress,
            progress,
            SettingsIpcJsonContext.Default.AppUpdateDownloadProgress);

    private void OnSettingsIpcUpdateDownloadCompleted(DeskBox.Models.AppUpdateDownloadResult result) =>
        PushEncodedEventAsync(
            SettingsIpcMethods.UpdateDownloadCompleted,
            result,
            SettingsIpcJsonContext.Default.AppUpdateDownloadResult);

    private void PushEncodedEventAsync<T>(string method, T payload, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo)
    {
        _ = PushSettingsIpcEventAsync(method, SettingsIpcPayload.Encode(payload, typeInfo));
    }

    private async Task PushSettingsIpcEventAsync(string method, string? payload)
    {
        try
        {
            if (_settingsIpcServer is { } server)
            {
                await server.SendEventAsync(method, payload);
            }
        }
        catch (Exception ex)
        {
            Log($"[SettingsIpc] Event push failed for {method}: {ex.Message}");
        }
    }
}
