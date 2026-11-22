namespace DeskBox;

using DeskBox.Contracts;
using DeskBox.Models;
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
    private DeskBoxAgentBridgeServer? _agentBridgeServer;
    private readonly SemaphoreSlim _agentApprovalDialogGate = new(1, 1);

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
                searchHotkeyProvider: () => _searchHotkeyService,
                clipboardHotkeyProvider: () => _clipboardHotkeyService),
            new InProcessSettingsDataActions(
                QuickCaptureService ?? throw new InvalidOperationException("Quick Capture service is not initialized."),
                LocalizationService,
                clipboardServiceProvider: () => _clipboardHistoryService),
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
                openCustomWidgetStudio: (widgetId, initialIdea) => ShowCustomWidgetStudio(widgetId, initialIdea)),
            new InProcessSettingsAppActions(this, LocalizationService),
            pushEvent: (method, payload) =>
                _settingsIpcServer?.SendEventAsync(method, payload) ?? Task.CompletedTask,
            backupCommands: _backupRuntime,
            backupRestore: _backupRestoreActions,
            backupSettings: _backupSettings,
            appUpdate: AppUpdateService,
            everythingClient: _everythingSearchService,
            everythingClientProvider: EnsureEverythingSearchServiceForIpc,
            // Batch R11: the coordinator flips its spawn-vs-show decision
            // the moment the host announces its exit, not when the process
            // handle finally reports death (~2s later, mid-flush). The
            // payload is the sender pid (R12 re-review) so a stale goodbye
            // from an old host can never evict a fresh replacement.
            onHostFarewell: payload =>
            {
                if (uint.TryParse(payload, out uint senderPid))
                {
                    _settingsIpcServer?.SuppressEventsForExitingClient();
                    MarkSettingsHostExiting(senderPid);
                }
            },
            onHostBootReady: OnSettingsHostBootReady);
        _settingsIpcRouter = router;
        _settingsIpcServer = SettingsIpcServer.Start(
            DeskBoxDataPathService.Current.SettingsIpcPipeName,
            router.DispatchAsync,
            // Batch R12: only the settings host this process spawned (or
            // the dying one saying goodbye) may use the pipe. Deep review
            // R13 adds the spawn-time stamp so a recycled pid from an
            // unrelated process cannot pass the number check.
            expectedClientIdentityProvider: () => SettingsHostPipeClientIdentity,
            onClientDisconnected: router.CancelAllActiveMigrationsOnDisconnect);

        SettingsService.LocalPersistCompleted += OnSettingsIpcLocalPersistCompleted;
        SettingsService.LayoutPersistCompleted += OnSettingsIpcLayoutPersistCompleted;
        // Deep review R15 (F1 audit): global-hotkey-service is an optional
        // startup step whose constructor failures are swallowed internally,
        // leaving the property null — R10's live-resolution hardening was
        // only applied to the SEARCH hotkey side. A null here NRE'd this
        // step and silently degraded the settings IPC server for the whole
        // session (settings unopenable). Guard the subscription; the
        // registration-changed push is a convenience, not a dependency.
        GlobalHotkeyService?.RegistrationChanged += OnSettingsIpcHotkeyRegistrationChanged;
        DisplayTopologyChanged += OnSettingsIpcDisplayTopologyChanged;
        ScreenHomeChanged += OnSettingsIpcScreenHomeChanged;
        StartupService.StateChanged += OnSettingsIpcAutoStartStateChanged;
        CloudBackupService.BackupRunCompleted += OnSettingsIpcBackupRunCompleted;
        AppUpdateService.CheckCompleted += OnSettingsIpcUpdateCheckCompleted;
        AppUpdateService.DownloadProgressChanged += OnSettingsIpcUpdateDownloadProgress;
        AppUpdateService.DownloadCompleted += OnSettingsIpcUpdateDownloadCompleted;

        if (_agentBridgeServer is null)
        {
            _agentBridgeServer = new DeskBoxAgentBridgeServer(
                DeskBoxDataPathService.Current.AgentBridgePipeName,
                () => WidgetManager,
                includeManifest => BuildStudioContextResponseJsonAsync(includeManifest),
                () =>
                {
                    AppSettings? s = SettingsService?.Settings;
                    double rawW = s?.DefaultWidgetWidth ?? SettingsService.DefaultWidgetWidth;
                    double rawH = s?.DefaultWidgetHeight ?? SettingsService.DefaultWidgetHeight;
                    return (
                        Math.Clamp((int)Math.Round(double.IsFinite(rawW) && rawW > 0 ? rawW : SettingsService.DefaultWidgetWidth), 180, 1200),
                        Math.Clamp((int)Math.Round(double.IsFinite(rawH) && rawH > 0 ? rawH : SettingsService.DefaultWidgetHeight), 80, 1200));
                },
                async (requestedWidgetId, capsuleMode, timeout) =>
                {
                    if (!IsStudioHostRunning)
                    {
                        return null;
                    }

                    if (!string.IsNullOrWhiteSpace(requestedWidgetId))
                    {
                        string? studioTargetId = TryGetStudioTargetWidgetIdForAgentUpsert();
                        string? resolvedDesktopId = (await DeskBox.Platform.UiDispatch.RunAsync(
                            () => Task.FromResult(WidgetManager?.ResolveCustomWidgetConfig(requestedWidgetId)?.Id)))
                            ?? requestedWidgetId;
                        if (!string.Equals(studioTargetId, resolvedDesktopId, StringComparison.OrdinalIgnoreCase))
                        {
                            return null;
                        }
                    }

                    return await TryCaptureRemoteStudioPreviewAsync(capsuleMode, timeout);
                },
                request => ShowAgentCommitApprovalAsync(request));
            _agentBridgeServer.Start();
            _ = SyncAgentWorkspaceDocsOnStartupAsync();
        }

        Log("[SettingsIpc] Server started");
        StartStudioIpcServer();
    }

    private Views.AgentCommitApprovalWindow? _activeAgentApprovalWindow;

    internal void CloseActiveAgentApprovalWindow()
    {
        Views.AgentCommitApprovalWindow? active = Interlocked.Exchange(ref _activeAgentApprovalWindow, null);
        if (active is null)
        {
            return;
        }

        if (UiDispatcherQueue is { HasThreadAccess: false } queue)
        {
            _ = queue.TryEnqueue(() =>
            {
                try { active.Close(); } catch { }
            });
            return;
        }

        try
        {
            active.Close();
        }
        catch
        {
        }
    }

    private async Task<bool> ShowAgentCommitApprovalAsync(
        Services.AgentCommitApprovalRequest request)
    {
        // Fail closed: no UI dispatcher / shutting down => the commit is denied.
        if (IsShuttingDown || UiDispatcherQueue is null)
        {
            return false;
        }

        await _agentApprovalDialogGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (IsShuttingDown)
            {
                return false;
            }

            var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!UiDispatcherQueue.TryEnqueue(() =>
                {
                    if (IsShuttingDown)
                    {
                        completion.TrySetResult(false);
                        return;
                    }

                    try
                    {
                        EnsureXamlLoopLifelineIfNoOtherWindows();
                        var window = new Views.AgentCommitApprovalWindow(LocalizationService, request, ThemeService);
                        _activeAgentApprovalWindow = window;
                        window.Closed += (_, _) => Interlocked.CompareExchange(ref _activeAgentApprovalWindow, null, window);
                        window.Activate();
                        _ = window.Result.ContinueWith(
                            result => completion.TrySetResult(result.IsCompletedSuccessfully && result.Result),
                            TaskScheduler.Default);
                    }
                    catch
                    {
                        completion.TrySetResult(false);
                    }
                }))
            {
                return false;
            }

            return await completion.Task.ConfigureAwait(false);
        }
        finally
        {
            _agentApprovalDialogGate.Release();
        }
    }

    private async Task SyncAgentWorkspaceDocsOnStartupAsync()
    {
        try
        {
            await CustomWidgetTemplates.EnsureAiAgentWorkspaceDocsAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log($"[SettingsIpc] EnsureAiAgentWorkspaceDocsAsync failed: {ex.Message}");
        }
        finally
        {
            if (!IsStudioHostRunning)
            {
                CustomWidgetCapabilityRegistry.ReleaseIdleCache();
            }
        }
    }

    private bool HasConnectedSettingsIpcClient =>
        _settingsIpcServer is { HasConnectedClient: true };

    private void ShutdownSettingsIpcServer()
    {
        CloseActiveAgentApprovalWindow();
        ShutdownStudioIpcServer();
        if (_agentBridgeServer is { } agentBridge)
        {
            _agentBridgeServer = null;
            _ = agentBridge.DisposeAsync().AsTask();
        }

        if (_settingsIpcServer is null)
        {
            return;
        }

        SettingsService.LocalPersistCompleted -= OnSettingsIpcLocalPersistCompleted;
        SettingsService.LayoutPersistCompleted -= OnSettingsIpcLayoutPersistCompleted;
        GlobalHotkeyService?.RegistrationChanged -= OnSettingsIpcHotkeyRegistrationChanged;
        DisplayTopologyChanged -= OnSettingsIpcDisplayTopologyChanged;
        ScreenHomeChanged -= OnSettingsIpcScreenHomeChanged;
        StartupService.StateChanged -= OnSettingsIpcAutoStartStateChanged;
        CloudBackupService.BackupRunCompleted -= OnSettingsIpcBackupRunCompleted;
        if (_appUpdateService is { } appUpdate)
        {
            appUpdate.CheckCompleted -= OnSettingsIpcUpdateCheckCompleted;
            appUpdate.DownloadProgressChanged -= OnSettingsIpcUpdateDownloadProgress;
            appUpdate.DownloadCompleted -= OnSettingsIpcUpdateDownloadCompleted;
        }

        SettingsIpcServer server = _settingsIpcServer;
        _settingsIpcServer = null;
        _settingsIpcRouter = null;
        _ = server.DisposeAsync().AsTask();
    }

    private void OnSettingsIpcLayoutPersistCompleted()
    {
        if (!HasConnectedSettingsIpcClient)
        {
            return;
        }

        // The authoritative layout slice changed on disk: push it so the
        // host's mirror (group list, displays, capsule counts) stays live.
        _ = PushSettingsIpcEventAsync(
            SettingsIpcMethods.LayoutChanged,
            SettingsService.SerializeCurrentLayout());
    }

    private void OnSettingsIpcLocalPersistCompleted(long mainCommitId)
    {
        if (!HasConnectedSettingsIpcClient)
        {
            return;
        }

        long? hostCommitId = _settingsIpcRouter?.LastAppliedHostCommitId;
        _ = PushSettingsIpcEventAsync(
            SettingsIpcMethods.SettingsReloaded,
            SettingsIpcReloadPayload.Compose(mainCommitId, hostCommitId));
    }

    private bool _isReconcilingHotkeyYields;

    private void OnSettingsIpcHotkeyRegistrationChanged()
    {
        ReconcileHotkeyPriorityYields();

        if (!HasConnectedSettingsIpcClient)
        {
            return;
        }

        _ = PushSettingsIpcEventAsync(SettingsIpcMethods.HotkeyRegistrationChanged, null);
    }

    private void ReconcileHotkeyPriorityYields()
    {
        if (_isReconcilingHotkeyYields || IsShuttingDown)
        {
            return;
        }

        _isReconcilingHotkeyYields = true;
        try
        {
            if (GlobalHotkeyService is { IsRegistered: true } main)
            {
                GlobalHotkeyActivation mainAct = main.CurrentActivation;
                if (_searchHotkeyService is { IsRegistered: true } search &&
                    GlobalHotkeyService.DoActivationsConflict(mainAct, search.CurrentActivation))
                {
                    search.RefreshRegistration();
                }

                if (_clipboardHotkeyService is { IsRegistered: true } clip &&
                    GlobalHotkeyService.DoActivationsConflict(mainAct, clip.CurrentActivation))
                {
                    clip.RefreshRegistration();
                }
            }

            if (_searchHotkeyService is { IsRegistered: true } activeSearch &&
                _clipboardHotkeyService is { IsRegistered: true } activeClip &&
                GlobalHotkeyService.DoActivationsConflict(activeSearch.CurrentActivation, activeClip.CurrentActivation))
            {
                activeClip.RefreshRegistration();
            }
        }
        finally
        {
            _isReconcilingHotkeyYields = false;
        }
    }

    private void OnSettingsIpcDisplayTopologyChanged()
    {
        if (!HasConnectedSettingsIpcClient)
        {
            return;
        }

        _ = PushSettingsIpcEventAsync(SettingsIpcMethods.AppDisplayTopologyChanged, null);
    }

    private void OnSettingsIpcScreenHomeChanged()
    {
        if (!HasConnectedSettingsIpcClient)
        {
            return;
        }

        _ = PushSettingsIpcEventAsync(SettingsIpcMethods.AppScreenHomeChanged, null);
    }

    private void OnSettingsIpcAutoStartStateChanged()
    {
        if (!HasConnectedSettingsIpcClient)
        {
            return;
        }

        _ = PushSettingsIpcEventAsync(SettingsIpcMethods.AppAutoStartStateChanged, null);
    }

    private void OnSettingsIpcBackupRunCompleted(CloudBackupRunCompletedInfo info)
    {
        if (!HasConnectedSettingsIpcClient || info.Endpoint is not { } endpoint)
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
        if (!HasConnectedSettingsIpcClient)
        {
            return;
        }

        _ = PushSettingsIpcEventAsync(method, SettingsIpcPayload.Encode(payload, typeInfo));
    }

    private async Task SyncManagedStorageHostEntriesAfterMigrationAsync(string oldRootPath, string newRootPath)
    {
        try
        {
            await ManagedStorageDesktopShortcutService.SyncAsync(oldRootPath);
        }
        catch (Exception ex)
        {
            Log($"[ManagedStorageMigration] Desktop shortcut sync deferred: {ex.Message}");
        }

        try
        {
            var oldPinState = await DeskBox.Helpers.ExplorerQuickAccessHelper.GetQuickAccessPinStateAsync(oldRootPath);
            if (oldPinState.State == DeskBox.Helpers.QuickAccessPinState.Pinned)
            {
                await DeskBox.Helpers.ExplorerQuickAccessHelper.TryUnpinFolderFromQuickAccessAsync(oldRootPath);
                await DeskBox.Helpers.ExplorerQuickAccessHelper.TryPinFolderToQuickAccessAsync(newRootPath);
            }
        }
        catch (Exception ex)
        {
            Log($"[ManagedStorageMigration] Quick Access pin migration deferred: {ex.Message}");
        }
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
