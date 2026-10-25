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
                SettingsService,
                // Live resolution (batch R10): the search hotkey service is
                // created lazily when the feature is first enabled — long
                // after this router construction — so the constructor
                // snapshot was null and every search-hotkey command
                // silently no-opped.
                searchHotkeyProvider: () => _searchHotkeyService),
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
                LocalizationService,
                applyQuickCaptureEnabled: enabled =>
                    (_quickCaptureSettings ?? throw new InvalidOperationException(
                        "Quick Capture is not initialized.")).SetEnabledAsync(enabled, reveal: enabled),
                applyTodoEnabled: enabled =>
                    (_todoSettings ?? throw new InvalidOperationException(
                        "Todo settings are not initialized.")).SetEnabledAsync(enabled),
                applySearchEnabled: enabled =>
                    (_searchSettings ?? throw new InvalidOperationException(
                        "Search settings are not initialized.")).SetEnabledAsync(enabled, reveal: enabled)),
            new InProcessSettingsAppActions(this, LocalizationService),
            pushEvent: (method, payload) =>
                _settingsIpcServer?.SendEventAsync(method, payload) ?? Task.CompletedTask,
            backupCommands: _backupRuntime,
            backupRestore: _backupRestoreActions,
            backupSettings: _backupSettings,
            appUpdate: AppUpdateService,
            everythingClient: _everythingSearchService,
            // Batch R11: the coordinator flips its spawn-vs-show decision
            // the moment the host announces its exit, not when the process
            // handle finally reports death (~2s later, mid-flush). The
            // payload is the sender pid (R12 re-review) so a stale goodbye
            // from an old host can never evict a fresh replacement.
            onHostFarewell: payload =>
            {
                if (uint.TryParse(payload, out uint senderPid))
                {
                    MarkSettingsHostExiting(senderPid);
                }
            });
        _settingsIpcRouter = router;
        _settingsIpcServer = SettingsIpcServer.Start(
            DeskBoxDataPathService.Current.SettingsIpcPipeName,
            router.DispatchAsync,
            // Batch R12: only the settings host this process spawned (or
            // the dying one saying goodbye) may use the pipe.
            expectedClientPidProvider: () => SettingsHostPipeClientPid);

        SettingsService.LocalPersistCompleted += OnSettingsIpcLocalPersistCompleted;
        SettingsService.LayoutPersistCompleted += OnSettingsIpcLayoutPersistCompleted;
        GlobalHotkeyService.RegistrationChanged += OnSettingsIpcHotkeyRegistrationChanged;
        DisplayTopologyChanged += OnSettingsIpcDisplayTopologyChanged;
        ScreenHomeChanged += OnSettingsIpcScreenHomeChanged;
        CloudBackupService.BackupRunCompleted += OnSettingsIpcBackupRunCompleted;
        AppUpdateService.CheckCompleted += OnSettingsIpcUpdateCheckCompleted;
        AppUpdateService.DownloadProgressChanged += OnSettingsIpcUpdateDownloadProgress;
        AppUpdateService.DownloadCompleted += OnSettingsIpcUpdateDownloadCompleted;
        Log("[SettingsIpc] Server started");
    }

    private void OnSettingsIpcLayoutPersistCompleted()
    {
        // The authoritative layout slice changed on disk: push it so the
        // host's mirror (group list, displays, capsule counts) stays live.
        _ = PushSettingsIpcEventAsync(
            SettingsIpcMethods.LayoutChanged,
            SettingsService.SerializeCurrentLayout());
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
