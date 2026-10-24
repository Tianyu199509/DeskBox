using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using DeskBox.Contracts;
using DeskBox.Models;

namespace DeskBox.Services;

/// <summary>
/// Main-side method router for the settings-host pipe (batch 3c-1): maps
/// wire methods onto the in-process facade implementations, so the settings
/// host's remote facades talk to the exact same behavior the in-process
/// settings window uses. Also remembers the host commit id of the last
/// applied external snapshot so the reload push can echo-suppress it.
/// </summary>
public sealed class SettingsIpcMethodRouter
{
    private readonly SettingsService? _settings;
    private readonly ISettingsHotkeyActions? _hotkey;
    private readonly ISettingsDataActions? _data;
    private readonly ISettingsRestoreActions? _restore;
    private readonly ISettingsWidgetActions? _widget;
    private readonly ISettingsAppActions? _appActions;
    private readonly IBackupCommands? _backupCommands;
    private readonly DeskBox.Contracts.ISearchConnectionClient? _everythingClient;
    private readonly IBackupRestoreActions? _backupRestore;
    private readonly IBackupSettings? _backupSettings;
    private readonly IAppUpdateService? _appUpdate;

    // Move-all undo tokens minted for the settings host: the token object
    // itself cannot cross the wire, so the main side keeps it and the host
    // gets an opaque handle to send back.
    private readonly Dictionary<long, WidgetMoveAllUndoToken> _moveAllTokens = new();
    private long _nextMoveAllHandle;
    private long _activeMigrationHandle;

    public SettingsIpcMethodRouter(
        SettingsService? settings,
        ISettingsHotkeyActions? hotkey,
        ISettingsDataActions? data,
        ISettingsRestoreActions? restore = null,
        ISettingsWidgetActions? widget = null,
        ISettingsAppActions? appActions = null,
        Func<string, string?, Task>? pushEvent = null,
        IBackupCommands? backupCommands = null,
        IBackupRestoreActions? backupRestore = null,
        IBackupSettings? backupSettings = null,
        IAppUpdateService? appUpdate = null,
        DeskBox.Contracts.ISearchConnectionClient? everythingClient = null)
    {
        _settings = settings;
        _hotkey = hotkey;
        _data = data;
        _restore = restore;
        _widget = widget;
        _appActions = appActions;
        _backupCommands = backupCommands;
        _everythingClient = everythingClient;
        _backupRestore = backupRestore;
        _backupSettings = backupSettings;
        _appUpdate = appUpdate;
        PushEvent = pushEvent;
    }

    /// <summary>
    /// Optional event push (the owning server), used for migration progress.
    /// </summary>
    private Func<string, string?, Task>? PushEvent { get; }

    /// <summary>
    /// The host commit id of the settings-commit currently being applied
    /// through this router. AsyncLocal: dispatches run concurrently on pool
    /// threads and each one's chained writes must only ever see its own
    /// echo tag — a shared field let command B's save carry command A's
    /// hostCommitId, making the host skip a reload it needed (batch R4).
    /// </summary>
    private static readonly AsyncLocal<long> CurrentDispatchHostCommitId = new();

    public long LastAppliedHostCommitId => CurrentDispatchHostCommitId.Value;

    public async Task<(bool Ok, string? Payload, string? Error)> DispatchAsync(
        string method,
        string? payload,
        CancellationToken cancellationToken)
    {
        // Snapshot-scoped: a host echo tag only ever applies to the write
        // caused by that snapshot itself (see CurrentDispatchHostCommitId).
        CurrentDispatchHostCommitId.Value = 0;
        switch (method)
        {
            case SettingsIpcMethods.Ping:
                return (true, "pong", null);

            case SettingsIpcMethods.AppearancePreview:
                if (_settings is null)
                {
                    return (false, null, "settings service unavailable");
                }

                // Preview-only apply: swap memory + preview broadcast, never
                // a durable write (the host commits the snapshot on release).
                bool previewApplied = await DeskBox.Platform.UiDispatch.RunAsync(
                    () => Task.FromResult(_settings.ApplyExternalAppearancePreview(payload ?? string.Empty)));
                return (previewApplied, null, previewApplied ? null : "preview rejected");

            case SettingsIpcMethods.SettingsCommit:
                if (_settings is null)
                {
                    return (false, null, "settings service unavailable");
                }

                // The commit id must be visible BEFORE the apply: the
                // LocalPersistCompleted push fires inside SaveCheckedAsync,
                // so setting it afterwards meant every push carried 0 and the
                // host reloaded its own writes (echo suppression never
                // engaged — batch R5). The payload is "commitId\njson".
                int separatorIndex = payload?.IndexOf('\n') ?? -1;
                if (separatorIndex > 0 &&
                    long.TryParse(payload!.AsSpan(0, separatorIndex), out long hostCommitId))
                {
                    CurrentDispatchHostCommitId.Value = hostCommitId;
                }

                (bool ok, string? resultPayload, string? error) =
                    await SettingsIpcDispatch.SettingsCommitAsync(_settings, payload);
                return (ok, resultPayload, error);

            case SettingsIpcMethods.HotkeyState:
                if (_hotkey is null) return unavailable(method);
                return (true, SerializeNullable(_hotkey.GetRuntimeState(),
                    SettingsIpcJsonContext.Default.GlobalHotkeyRuntimeState), null);

            case SettingsIpcMethods.HotkeyApply:
                if (_hotkey is null) return unavailable(method);
                if (SettingsIpcPayload.DecodeStruct(payload, SettingsIpcJsonContext.Default.GlobalHotkeyActivation)
                    is not { } activation)
                {
                    return (false, null, "missing activation payload");
                }

                bool applied = _hotkey.TryApplyActivation(activation, out string? applyError);
                return (applied, null, applyError);

            case SettingsIpcMethods.HotkeyReset:
                if (_hotkey is null) return unavailable(method);
                bool reset = _hotkey.ResetToDefault(out string? resetError);
                return (reset, null, resetError);

            case SettingsIpcMethods.HotkeyRefreshRegistration:
                if (_hotkey is null) return unavailable(method);
                _hotkey.RefreshRegistration();
                return (true, null, null);

            case SettingsIpcMethods.HotkeySetEnabled:
                if (_hotkey is null) return unavailable(method);
                _hotkey.SetEnabled(SettingsIpcPayload.DecodeBoolean(payload));
                return (true, null, null);

            case SettingsIpcMethods.HotkeyGestureOwned:
                if (_hotkey is null) return unavailable(method);
                if (SettingsIpcPayload.DecodeStruct(payload, SettingsIpcJsonContext.Default.GlobalHotkeyGesture)
                    is not { } gesture)
                {
                    return (false, null, "missing gesture payload");
                }

                return (true, SettingsIpcPayload.EncodeBoolean(_hotkey.IsGestureOwnedBySearchHotkey(gesture)), null);

            case SettingsIpcMethods.SearchHotkeyState:
                if (_hotkey is null) return unavailable(method);
                return (true, SettingsIpcPayload.EncodeBoolean(
                    _hotkey.IsSearchHotkeyRegistered()), null);

            case SettingsIpcMethods.SearchHotkeySetEnabled:
                if (_hotkey is null) return unavailable(method);
                _hotkey.SetSearchHotkeyEnabled(SettingsIpcPayload.DecodeBoolean(payload));
                return (true, null, null);

            case SettingsIpcMethods.SearchHotkeyApplyGesture:
                if (_hotkey is null) return unavailable(method);
                if (SettingsIpcPayload.DecodeStruct(payload, SettingsIpcJsonContext.Default.GlobalHotkeyGesture)
                    is not { } searchGesture)
                {
                    return (false, null, "missing gesture payload");
                }

                bool searchApplied = _hotkey.TryApplySearchHotkeyGesture(searchGesture, out string? searchError);
                return (searchApplied, null, searchError);

            case SettingsIpcMethods.SearchHotkeyGestureOwnedByMain:
                if (_hotkey is null) return unavailable(method);
                if (SettingsIpcPayload.DecodeStruct(payload, SettingsIpcJsonContext.Default.GlobalHotkeyGesture)
                    is not { } ownedGesture)
                {
                    return (false, null, "missing gesture payload");
                }

                return (true, SettingsIpcPayload.EncodeBoolean(
                    _hotkey.IsGestureOwnedByMainHotkey(ownedGesture)), null);

            case SettingsIpcMethods.SearchHotkeyRefreshRegistration:
                if (_hotkey is null) return unavailable(method);
                _hotkey.RefreshSearchHotkeyRegistration();
                return (true, null, null);

            case SettingsIpcMethods.HotkeyDoubleClickSet:
                if (_hotkey is null) return unavailable(method);
                DesktopDoubleClickToggleResult toggle =
                    _hotkey.TrySetDesktopDoubleClickEnabled(SettingsIpcPayload.DecodeBoolean(payload));
                return (true, SettingsIpcPayload.Encode(
                    toggle,
                    SettingsIpcJsonContext.Default.DesktopDoubleClickToggleResult), null);

            case SettingsIpcMethods.HotkeySuspend:
                if (_hotkey is null) return unavailable(method);
                // The suspend/resume calls are documented to run on the UI
                // dispatcher; acknowledge after the marshal completes so a
                // failure surfaces to the recorder before its hook starts.
                await DeskBox.Platform.UiDispatch.RunAsync(() =>
                {
                    _hotkey.SuspendHotkeysForRecording();
                    return Task.CompletedTask;
                });
                return (true, null, null);

            case SettingsIpcMethods.HotkeyResume:
                if (_hotkey is null) return unavailable(method);
                await DeskBox.Platform.UiDispatch.RunAsync(() =>
                {
                    _hotkey.ResumeHotkeysAfterRecording();
                    return Task.CompletedTask;
                });
                return (true, null, null);

            case SettingsIpcMethods.DataQuickCaptureCounts:
                if (_data is null) return unavailable(method);
                (int active, int recent) = await _data.GetQuickCaptureCountsAsync();
                return (true, SettingsIpcPayload.Encode(
                    new SettingsIpcIntPair(active, recent),
                    SettingsIpcJsonContext.Default.SettingsIpcIntPair), null);

            case SettingsIpcMethods.DataQuickCaptureClear:
                if (_data is null) return unavailable(method);
                await _data.ClearQuickCaptureDataAsync();
                return (true, null, null);

            case SettingsIpcMethods.DataQuickCaptureClearRecent:
                if (_data is null) return unavailable(method);
                await _data.ClearQuickCaptureRecentAsync();
                return (true, null, null);

            case SettingsIpcMethods.DataQuickCaptureCacheCleanup:
                if (_data is null) return unavailable(method);
                QuickCaptureImageCacheCleanupResult cleanup =
                    await _data.ClearUnusedQuickCaptureImageCacheAsync();
                return (true, SerializeNullable(cleanup,
                    SettingsIpcJsonContext.Default.QuickCaptureImageCacheCleanupResult), null);

            case SettingsIpcMethods.DataQuickCaptureCacheInfo:
                if (_data is null) return unavailable(method);
                QuickCaptureImageCacheInfo? info = await _data.GetQuickCaptureImageCacheInfoAsync();
                return (true, SerializeNullable(info,
                    SettingsIpcJsonContext.Default.QuickCaptureImageCacheInfo), null);

            // ── Restore facade (batch 3c-2) ──────────────────────────

            case SettingsIpcMethods.RestorePrepare:
                if (_restore is null) return unavailable(method);
                DeskBoxRestorePreparation preparation = await _restore.PrepareRestoreAsync(payload ?? string.Empty);
                return (true, SettingsIpcPayload.Encode(
                    preparation,
                    SettingsIpcJsonContext.Default.DeskBoxRestorePreparation), null);

            case SettingsIpcMethods.RestoreConfirm:
                if (_restore is null) return unavailable(method);
                return (await _restore.ConfirmPendingFullRestoreAsync(), null, null);

            case SettingsIpcMethods.RestoreCancel:
                if (_restore is null) return unavailable(method);
                await _restore.CancelPendingRestoreAsync();
                return (true, null, null);

            case SettingsIpcMethods.RestoreInventory:
                if (_restore is null) return unavailable(method);
                IReadOnlyList<DeskBoxBackupSnapshotInfo> inventory = await _restore.GetSnapshotInventoryAsync();
                return (true, SettingsIpcPayload.Encode(
                    inventory.ToList(),
                    SettingsIpcJsonContext.Default.ListDeskBoxBackupSnapshotInfo), null);

            case SettingsIpcMethods.RestoreDeleteSnapshot:
                if (_restore is null) return unavailable(method);
                await _restore.DeleteSnapshotAsync(payload ?? string.Empty);
                return (true, null, null);

            case SettingsIpcMethods.RestoreAttachmentHealth:
                if (_restore is null) return unavailable(method);
                DeskBoxAttachmentHealthReport report = await _restore.ScanAttachmentHealthAsync();
                return (true, SettingsIpcPayload.Encode(
                    report,
                    SettingsIpcJsonContext.Default.DeskBoxAttachmentHealthReport), null);

            // ── Widget facade (batch 3c-2) ───────────────────────────

            case SettingsIpcMethods.WidgetApplyGroupPresentation:
                if (_widget is null) return unavailable(method);
                if (SettingsIpcPayload.Decode(payload, SettingsIpcJsonContext.Default.GroupPresentationChange)
                    is not { } groupPresentation)
                {
                    return (false, null, "missing payload");
                }

                await _widget.ApplyGroupPresentation(groupPresentation);
                return (true, SerializeLayout(_settings), null);

            case SettingsIpcMethods.WidgetApplyGroupNavDefaults:
                if (_widget is null) return unavailable(method);
                if (SettingsIpcPayload.Decode(payload, SettingsIpcJsonContext.Default.GroupNavigationDefaultsChange)
                    is not { } navDefaults)
                {
                    return (false, null, "missing payload");
                }

                await _widget.ApplyGroupNavigationDefaults(navDefaults);
                return (true, SerializeLayout(_settings), null);

            case SettingsIpcMethods.WidgetApplyLayoutPreferences:
                if (_widget is null) return unavailable(method);
                if (SettingsIpcPayload.Decode(payload, SettingsIpcJsonContext.Default.LayoutPreferencesChange)
                    is not { } layoutPreferences)
                {
                    return (false, null, "missing payload");
                }

                await _widget.ApplyLayoutPreferences(layoutPreferences);
                return (true, SerializeLayout(_settings), null);

            case SettingsIpcMethods.WidgetResetCapsuleOverrides:
                if (_widget is null) return unavailable(method);
                if (SettingsIpcPayload.Decode(payload, SettingsIpcJsonContext.Default.CapsuleOverrideReset)
                    is not { } capsuleReset)
                {
                    return (false, null, "missing payload");
                }

                await _widget.ResetCapsuleOverrides(capsuleReset);
                return (true, SerializeLayout(_settings), null);

            case SettingsIpcMethods.AppearanceClearPerWidgetBackgrounds:
                if (_widget is null) return unavailable(method);
                await _widget.ClearPerWidgetBackgrounds();
                return (true, SerializeLayout(_settings), null);

            case SettingsIpcMethods.SettingsSnapshot:
                if (_settings is null) return unavailable(method);
                return (true, _settings.SerializeCurrentSettings(), null);

            case SettingsIpcMethods.WidgetLayoutRead:
                if (_settings is null) return unavailable(method);
                return (true, SerializeLayout(_settings), null);

            case SettingsIpcMethods.WidgetIsFeatureEnabled:
                if (_widget is null) return unavailable(method);
                return (true, SettingsIpcPayload.EncodeBoolean(
                    _widget.IsFeatureWidgetEnabled(DecodeKind(payload))), null);

            case SettingsIpcMethods.WidgetSyncFeatureEnabled:
                if (_widget is null) return unavailable(method);
                SettingsIpcKindArg sync = DecodeKindArg(payload);
                // The feature switch: dictionary write + widget apply in one
                // command; the response carries the applied value so the
                // host can mirror its switch UI immediately.
                bool switchApplied = await _widget.SetFeatureWidgetEnabled((WidgetKind)sync.Kind, sync.Enabled);
                return (true, SettingsIpcPayload.EncodeBoolean(switchApplied), null);

            case SettingsIpcMethods.WidgetResetFeature:
                if (_widget is null) return unavailable(method);
                await _widget.ResetFeatureWidgetAsync(DecodeKind(payload));
                return (true, null, null);

            case SettingsIpcMethods.WidgetRefreshLayers:
                if (_widget is null) return unavailable(method);
                _widget.RefreshVisibleWidgetDesktopLayers(payload ?? string.Empty);
                return (true, null, null);

            case SettingsIpcMethods.WidgetNotifyGroupPresentation:
                if (_widget is null) return unavailable(method);
                _widget.NotifyWidgetGroupPresentationSettingsChanged();
                return (true, null, null);

            case SettingsIpcMethods.WidgetReorderGroupMember:
                if (_widget is null) return unavailable(method);
                SettingsIpcStringPair? reorder = SettingsIpcPayload.Decode(
                    payload, SettingsIpcJsonContext.Default.SettingsIpcStringPair);
                if (reorder is null) return (false, null, "missing payload");
                return (await _widget.ReorderWidgetGroupMemberAsync(reorder.A, reorder.B), null, null);

            case SettingsIpcMethods.WidgetRemoveFromGroup:
                if (_widget is null) return unavailable(method);
                return (await _widget.RemoveWidgetFromGroupAsync(payload ?? string.Empty), null, null);

            case SettingsIpcMethods.WidgetDissolveGroup:
                if (_widget is null) return unavailable(method);
                return (await _widget.DissolveWidgetGroupContainingAsync(payload ?? string.Empty), null, null);

            case SettingsIpcMethods.WidgetGetOrphans:
                if (_widget is null) return unavailable(method);
                IReadOnlyList<ManagedStorageFolderCleanupCandidate>? orphans = _widget.GetOrphanManagedStorageFolders();
                return (true, orphans is null
                    ? null
                    : SettingsIpcPayload.Encode(
                        orphans.ToList(),
                        SettingsIpcJsonContext.Default.ListManagedStorageFolderCleanupCandidate), null);

            case SettingsIpcMethods.WidgetRestoreOrphans:
                if (_widget is null) return unavailable(method);
                List<string>? paths = SettingsIpcPayload.Decode(
                    payload, SettingsIpcJsonContext.Default.ListString);
                if (paths is null) return (false, null, "missing payload");
                return (true, (await _widget.RestoreOrphanManagedStorageFoldersAsync(paths)).ToString(), null);

            case SettingsIpcMethods.WidgetMoveOrphanToDesktop:
                if (_widget is null) return unavailable(method);
                await _widget.MoveOrphanManagedStorageFolderContentsToDesktopAsync(payload ?? string.Empty);
                return (true, null, null);

            case SettingsIpcMethods.WidgetDeleteOrphan:
                if (_widget is null) return unavailable(method);
                if (SettingsIpcPayload.Decode(payload, SettingsIpcJsonContext.Default.SettingsIpcOrphanDelete)
                    is not { } orphanDelete)
                {
                    return (false, null, "missing payload");
                }

                await _widget.DeleteOrphanManagedStorageFolderAsync(orphanDelete.Path, (nint)orphanDelete.OwnerHwnd);
                return (true, null, null);

            case SettingsIpcMethods.WidgetManagedCount:
                if (_widget is null) return unavailable(method);
                return (true, _widget.GetDefaultManagedStorageWidgetCount().ToString(), null);

            case SettingsIpcMethods.WidgetMigratePreview:
                if (_widget is null) return unavailable(method);
                ManagedStorageMigrationPreview preview = await _widget.PreviewManagedStorageMigrationAsync(
                    payload ?? string.Empty,
                    cancellationToken);
                return (true, SettingsIpcPayload.Encode(
                    preview,
                    SettingsIpcJsonContext.Default.ManagedStorageMigrationPreview), null);

            case SettingsIpcMethods.WidgetMigrateRoot:
                if (_widget is null) return unavailable(method);
                if (SettingsIpcPayload.Decode(payload, SettingsIpcJsonContext.Default.SettingsIpcMigrationRequest)
                    is not { } migration)
                {
                    return (false, null, "missing payload");
                }

                // The host dialog's IProgress cannot ride the request; the
                // main side re-reports progress as pushed events instead.
                long migrationHandle = ++_activeMigrationHandle;
                var progress = new Progress<ManagedStorageMigrationProgress>(p =>
                {
                    _ = PushEvent?.Invoke(
                        SettingsIpcMethods.WidgetMigrationProgress,
                        migrationHandle + "\n" + SettingsIpcPayload.Encode(
                            p,
                            SettingsIpcJsonContext.Default.ManagedStorageMigrationProgress));
                });
                ManagedStorageMigrationResult migrationResult = await _widget.UpdateDefaultManagedStorageRootAsync(
                    migration.NewRootPath,
                    new ManagedStorageMigrationOptions(
                        Progress: progress,
                        RestartIfCopiesRemoved: migration.RestartIfCopiesRemoved));
                return (true, SettingsIpcPayload.Encode(
                    migrationResult,
                    SettingsIpcJsonContext.Default.ManagedStorageMigrationResult), null);

            case SettingsIpcMethods.WidgetGlanceList:
                if (_widget is null) return unavailable(method);
                IReadOnlyList<GlanceWidgetInstanceInfo> instances = _widget.GetGlanceWidgetInstances();
                return (true, SettingsIpcPayload.Encode(
                    instances.ToList(),
                    SettingsIpcJsonContext.Default.ListGlanceWidgetInstanceInfo), null);

            case SettingsIpcMethods.WidgetGlanceFeatureEnabled:
                if (_widget is null) return unavailable(method);
                return (true, SettingsIpcPayload.EncodeBoolean(_widget.IsGlanceFeatureEnabled), null);

            case SettingsIpcMethods.WidgetGlanceCreate:
                if (_widget is null) return unavailable(method);
                string? sourceId = payload is null || payload.Length == 0
                    ? null
                    : SettingsIpcPayload.Decode(payload, SettingsIpcJsonContext.Default.SettingsIpcGlanceCreate)
                        ?.SourceWidgetId;
                GlanceWidgetInstanceInfo created = await _widget.CreateGlanceWidgetAsync(sourceId);
                return (true, SettingsIpcPayload.Encode(
                    created,
                    SettingsIpcJsonContext.Default.GlanceWidgetInstanceInfo), null);

            case SettingsIpcMethods.WidgetGlanceSetEnabled:
                if (_widget is null) return unavailable(method);
                if (SettingsIpcPayload.Decode(payload, SettingsIpcJsonContext.Default.SettingsIpcGlanceSetEnabled)
                    is not { } glanceToggle)
                {
                    return (false, null, "missing payload");
                }

                await _widget.SetGlanceWidgetInstanceEnabledAsync(glanceToggle.WidgetId, glanceToggle.Enabled);
                return (true, null, null);

            case SettingsIpcMethods.WidgetGlanceLocate:
                if (_widget is null) return unavailable(method);
                return (await _widget.LocateGlanceWidgetAsync(payload ?? string.Empty), null, null);

            case SettingsIpcMethods.WidgetGlanceRemove:
                if (_widget is null) return unavailable(method);
                await _widget.RemoveGlanceWidgetAsync(payload ?? string.Empty);
                return (true, null, null);

            case SettingsIpcMethods.WidgetGlanceRename:
                if (_widget is null) return unavailable(method);
                if (SettingsIpcPayload.Decode(payload, SettingsIpcJsonContext.Default.SettingsIpcGlanceRename)
                    is not { } rename)
                {
                    return (false, null, "missing payload");
                }

                await _widget.RenameWidgetAsync(rename.WidgetId, rename.NewName);
                return (true, null, null);

            case SettingsIpcMethods.WidgetGlanceApplyData:
                return await HandleGlanceApplyDataAsync(payload);

            case SettingsIpcMethods.WidgetMoveAll:
                if (_widget is null) return unavailable(method);
                (int movedCount, WidgetMoveAllUndoToken token) = await _widget
                    .MoveAllWidgetSurfacesToDisplayAsync(payload ?? string.Empty);
                long handle = ++_nextMoveAllHandle;
                _moveAllTokens[handle] = token;
                return (true, SettingsIpcPayload.Encode(
                    new SettingsIpcMoveAllResult(movedCount, handle),
                    SettingsIpcJsonContext.Default.SettingsIpcMoveAllResult), null);

            case SettingsIpcMethods.WidgetMoveAllUndo:
                if (_widget is null) return unavailable(method);
                if (!long.TryParse(payload, out long undoHandle) ||
                    !_moveAllTokens.Remove(undoHandle, out WidgetMoveAllUndoToken? undoToken))
                {
                    return (false, null, "unknown undo handle");
                }

                return (await _widget.UndoMoveAllWidgetSurfacesAsync(undoToken), null, null);

            case SettingsIpcMethods.WidgetApplyScreenBinding:
                if (_widget is null) return unavailable(method);
                if (SettingsIpcPayload.Decode(payload, SettingsIpcJsonContext.Default.SettingsIpcScreenBinding)
                    is not { } binding)
                {
                    return (false, null, "missing payload");
                }

                await _widget.ApplyScreenBindingAsync(
                    binding.WidgetId,
                    (WidgetScreenBindingMode)binding.Mode,
                    binding.BoundScreenId,
                    binding.Persist);
                return (true, null, null);

            case SettingsIpcMethods.WidgetFolderWatcherHealth:
                if (_widget is null) return unavailable(method);
                IReadOnlyList<FolderWatcherHealthSnapshot>? watcherHealth = _widget.GetFolderWatcherHealthSnapshots();
                return (true, watcherHealth is null
                    ? null
                    : SettingsIpcPayload.Encode(
                        watcherHealth.ToList(),
                        SettingsIpcJsonContext.Default.ListFolderWatcherHealthSnapshot), null);

            case SettingsIpcMethods.WidgetLatestUndoableOrgEntry:
                if (_widget is null) return unavailable(method);
                return (true, SerializeNullable(
                    _widget.GetLatestUndoableDesktopOrganizationEntry(),
                    SettingsIpcJsonContext.Default.OrganizationHistoryEntry), null);

            case SettingsIpcMethods.WidgetOrgHistoryEntry:
                if (_widget is null) return unavailable(method);
                return (true, SerializeNullable(
                    _widget.GetDesktopOrganizationHistoryEntry(payload ?? string.Empty),
                    SettingsIpcJsonContext.Default.OrganizationHistoryEntry), null);

            case SettingsIpcMethods.WidgetOrgUndo:
                if (_widget is null) return unavailable(method);
                if (SettingsIpcPayload.Decode(payload, SettingsIpcJsonContext.Default.SettingsIpcOrgUndo)
                    is not { } orgUndo)
                {
                    return (false, null, "missing payload");
                }

                DesktopOrganizationUndoResult undoResult = await _widget.UndoDesktopOrganizationAsync(
                    orgUndo.HistoryId,
                    (nint)orgUndo.OwnerHwnd);
                return (true, SettingsIpcPayload.Encode(
                    undoResult,
                    SettingsIpcJsonContext.Default.DesktopOrganizationUndoResult), null);

            case SettingsIpcMethods.WidgetOrgAbandon:
                if (_widget is null) return unavailable(method);
                DesktopOrganizationUndoResult abandonResult =
                    await _widget.AbandonDesktopOrganizationUndoAsync(payload ?? string.Empty);
                return (true, SettingsIpcPayload.Encode(
                    abandonResult,
                    SettingsIpcJsonContext.Default.DesktopOrganizationUndoResult), null);

            // ── App facade (batch 3c-2) ──────────────────────────────

            case SettingsIpcMethods.AppShutdownForRestart:
                if (_appActions is null) return unavailable(method);
                await _appActions.ShutdownForRestartAsync();
                return (true, null, null);

            case SettingsIpcMethods.AppShutdownForUpdate:
                if (_appActions is null) return unavailable(method);
                await _appActions.ShutdownForUpdateAsync();
                return (true, null, null);

            case SettingsIpcMethods.AppScheduleRestart:
                if (_appActions is null) return unavailable(method);
                AppRelaunchScheduleResult schedule = _appActions.ScheduleRestartAfterCurrentProcessExit();
                return (true, SettingsIpcPayload.Encode(
                    schedule,
                    SettingsIpcJsonContext.Default.AppRelaunchScheduleResult), null);

            case SettingsIpcMethods.AppRelaunchAsExplorerUser:
                if (_appActions is null) return unavailable(method);
                return (_appActions.TryRelaunchAsExplorerUser(payload ?? string.Empty), null, null);

            case SettingsIpcMethods.AppShowOnboarding:
                if (_appActions is null) return unavailable(method);
                // The onboarding window is heavy XAML; build it on the UI
                // thread and answer immediately (org-window pattern).
                bool onboardingResume = SettingsIpcPayload.DecodeBoolean(payload);
                _ = DeskBox.Platform.UiDispatch.RunAsync(() =>
                {
                    _appActions.ShowOnboarding(onboardingResume);
                    return Task.CompletedTask;
                });
                return (true, null, null);

            case SettingsIpcMethods.AppShowDesktopOrgWindow:
                if (_appActions is null) return unavailable(method);
                // The window build + desktop scan can outlast the host's
                // UI-thread wait; acknowledge now and open on the UI thread.
                long orgOwner = long.TryParse(payload, out long parsedOwner) ? parsedOwner : 0;
                _ = DeskBox.Platform.UiDispatch.RunAsync(() =>
                {
                    _appActions.ShowDesktopOrganizationWindow((nint)orgOwner);
                    return Task.CompletedTask;
                });
                return (true, null, null);

            case SettingsIpcMethods.AppOpenReleaseNotes:
                if (_appActions is null) return unavailable(method);
                if (SettingsIpcPayload.Decode(payload, SettingsIpcJsonContext.Default.SettingsIpcReleaseNotes)
                    is not { } notes)
                {
                    return (false, null, "missing payload");
                }

                AppUpdateManifest? manifest = notes.ManifestJson is null
                    ? null
                    : SettingsIpcPayload.Decode(notes.ManifestJson, SettingsIpcJsonContext.Default.AppUpdateManifest);
                // The manifest loader delegate cannot cross the wire; the
                // allow-lazy-fetch flag tells the main side to fall back to
                // its own fetch when no manifest rides along. The window is
                // XAML-heavy: open on the UI thread and answer immediately
                // (org-window pattern).
                _ = DeskBox.Platform.UiDispatch.RunAsync(() =>
                {
                    _appActions.OpenReleaseNotesWindow(manifest, notes.Version, null, (nint)notes.OwnerHwnd);
                    return Task.CompletedTask;
                });
                return (true, null, null);

            case SettingsIpcMethods.AppUpdateTrayIcon:
                if (_appActions is null) return unavailable(method);
                _appActions.UpdateTrayIcon();
                return (true, null, null);

            case SettingsIpcMethods.AppSetSnapEnabled:
                if (_appActions is null) return unavailable(method);
                _appActions.SetSnapEnabled(SettingsIpcPayload.DecodeBoolean(payload));
                return (true, null, null);

            case SettingsIpcMethods.AppSetSnapSpacing:
                if (_appActions is null) return unavailable(method);
                if (!double.TryParse(payload, out double spacingDips))
                {
                    return (false, null, "missing spacing payload");
                }

                _appActions.SetSnapSpacingDips(spacingDips);
                return (true, null, null);

            case SettingsIpcMethods.AppScheduleLightCleanup:
                if (_appActions is null) return unavailable(method);
                _appActions.ScheduleLightMemoryCleanup();
                return (true, null, null);

            case SettingsIpcMethods.AppScheduleHiddenCleanup:
                if (_appActions is null) return unavailable(method);
                _appActions.ScheduleSettingsHiddenCleanup();
                return (true, null, null);

            case SettingsIpcMethods.AppRaiseBand:
                if (_appActions is null) return unavailable(method);
                if (SettingsIpcPayload.Decode(payload, SettingsIpcJsonContext.Default.SettingsIpcStringPair)
                    is not { } raise)
                {
                    return (false, null, "missing payload");
                }

                _appActions.BringSettingsWindowToRaisedBand(long.TryParse(raise.A, out long raiseHwnd) ? (nint)raiseHwnd : 0, raise.B);
                return (true, null, null);

            case SettingsIpcMethods.AppReleaseBand:
                if (_appActions is null) return unavailable(method);
                if (SettingsIpcPayload.Decode(payload, SettingsIpcJsonContext.Default.SettingsIpcStringPair)
                    is not { } release)
                {
                    return (false, null, "missing payload");
                }

                _appActions.ReleaseRaisedBandGuest(long.TryParse(release.A, out long releaseHwnd) ? (nint)releaseHwnd : 0, release.B);
                return (true, null, null);

            case SettingsIpcMethods.AppHasShortcut:
                if (_appActions is null) return unavailable(method);
                return (true, SettingsIpcPayload.EncodeBoolean(_appActions.HasManagedStorageDesktopShortcut()), null);

            case SettingsIpcMethods.AppCreateShortcut:
                if (_appActions is null) return unavailable(method);
                return (await _appActions.CreateManagedStorageDesktopShortcutAsync(), null, null);

            case SettingsIpcMethods.AppRemoveShortcut:
                if (_appActions is null) return unavailable(method);
                return (await _appActions.RemoveManagedStorageDesktopShortcutAsync(), null, null);

            case SettingsIpcMethods.AppSyncShortcut:
                if (_appActions is null) return unavailable(method);
                await _appActions.SyncManagedStorageDesktopShortcutAsync(payload ?? string.Empty);
                return (true, null, null);

            case SettingsIpcMethods.AppExportDiagnostics:
                if (_appActions is null) return unavailable(method);
                return (true, await _appActions.ExportDiagnosticBundleAsync(payload ?? string.Empty), null);

            case SettingsIpcMethods.AppUploadDiagnostics:
                if (_appActions is null) return unavailable(method);
                if (!long.TryParse(payload, out long feedbackId))
                {
                    return (false, null, "missing feedback id");
                }

                // The host's temp directory is unreachable from the main
                // process; the export uses a main-side scratch directory.
                return (await _appActions.UploadDiagnosticsForFeedbackAsync(
                    feedbackId,
                    Path.Combine(Path.GetTempPath(), "DeskBoxFeedback")), null, null);

            case SettingsIpcMethods.AppRuntimeHealth:
                if (_appActions is null) return unavailable(method);
                return (true, SerializeNullable(
                    _appActions.GetRuntimeHealthSnapshot(),
                    SettingsIpcJsonContext.Default.AppRuntimeHealthSnapshot), null);

            case SettingsIpcMethods.AppForceExternalRecovery:
                if (_appActions is null) return unavailable(method);
                await _appActions.ForceExternalStateRecoveryAsync();
                return (true, null, null);

            case SettingsIpcMethods.AppDiagnoseDragDrop:
                if (_appActions is null) return unavailable(method);
                return (true, SerializeNullable(
                    _appActions.DiagnoseDragDropPermissions(),
                    SettingsIpcJsonContext.Default.DragDropPermissionDiagnostic), null);

            case SettingsIpcMethods.AppRepairDragDrop:
                if (_appActions is null) return unavailable(method);
                return (true, SettingsIpcPayload.Encode(
                    _appActions.RepairDragDropPermissions(),
                    SettingsIpcJsonContext.Default.DragDropPermissionRepairResult), null);

            // ── Backup commands (batch 3c-3a) ────────────────────────

            case SettingsIpcMethods.BackupIsStopping:
                if (_backupCommands is null) return unavailable(method);
                return (true, SettingsIpcPayload.EncodeBoolean(_backupCommands.IsStopping), null);

            case SettingsIpcMethods.BackupCreateSnapshotNow:
                if (_backupCommands is null) return unavailable(method);
                LocalBackupResult snapshot = await _backupCommands.CreateSnapshotNowAsync(cancellationToken);
                return (true, SettingsIpcPayload.Encode(
                    snapshot,
                    SettingsIpcJsonContext.Default.LocalBackupResult), null);

            case SettingsIpcMethods.BackupExport:
                if (_backupCommands is null) return unavailable(method);
                return (true, await _backupCommands.ExportAsync(payload ?? string.Empty, cancellationToken), null);

            case SettingsIpcMethods.BackupUploadNow:
                if (_backupCommands is null) return unavailable(method);
                CloudBackupRunResult upload = await _backupCommands.UploadNowAsync(cancellationToken);
                return (true, SettingsIpcPayload.Encode(
                    upload,
                    SettingsIpcJsonContext.Default.CloudBackupRunResult), null);

            // ── Backup settings (batch 3c-3a) ────────────────────────

            case SettingsIpcMethods.BackupSettingsRead:
                if (_backupSettings is null) return unavailable(method);
                return (true, SettingsIpcPayload.Encode(
                    _backupSettings.Read(),
                    SettingsIpcJsonContext.Default.BackupSettingsSnapshot), null);

            case SettingsIpcMethods.BackupSettingsUpdate:
                if (_backupSettings is null) return unavailable(method);
                if (SettingsIpcPayload.Decode(payload, SettingsIpcJsonContext.Default.BackupSettingsChange)
                    is not { } change)
                {
                    return (false, null, "missing payload");
                }

                _backupSettings.Update(change);
                return (true, null, null);

            case SettingsIpcMethods.BackupSettingsIsValidLocalDir:
                if (_backupSettings is null) return unavailable(method);
                bool valid = _backupSettings.IsValidLocalDirectory(
                    payload ?? string.Empty,
                    out string? rejectionKey);
                return (true, SettingsIpcPayload.Encode(
                    new SettingsIpcLocalDirCheck(valid, rejectionKey),
                    SettingsIpcJsonContext.Default.SettingsIpcLocalDirCheck), null);

            case SettingsIpcMethods.BackupSettingsSave:
                if (_backupSettings is null) return unavailable(method);
                await _backupSettings.SaveAsync();
                return (true, null, null);

            case SettingsIpcMethods.BackupSettingsHasCredential:
                if (_backupSettings is null) return unavailable(method);
                if (SettingsIpcPayload.Decode(payload, SettingsIpcJsonContext.Default.BackupEndpoint)
                    is not { } hasEndpoint)
                {
                    return (false, null, "missing payload");
                }

                return (true, SettingsIpcPayload.EncodeBoolean(
                    await _backupSettings.HasCredentialAsync(hasEndpoint, cancellationToken)), null);

            case SettingsIpcMethods.BackupSettingsSaveCredential:
                if (_backupSettings is null) return unavailable(method);
                if (SettingsIpcPayload.Decode(payload, SettingsIpcJsonContext.Default.SettingsIpcCredential)
                    is not { } credential || credential.Secret is null)
                {
                    return (false, null, "missing payload");
                }

                await _backupSettings.SaveCredentialAsync(credential.Endpoint, credential.Secret, cancellationToken);
                return (true, null, null);

            case SettingsIpcMethods.BackupSettingsProbe:
                if (_backupSettings is null) return unavailable(method);
                if (SettingsIpcPayload.Decode(payload, SettingsIpcJsonContext.Default.SettingsIpcCredential)
                    is not { } probe)
                {
                    return (false, null, "missing payload");
                }

                await _backupSettings.ProbeAsync(probe.Endpoint, probe.SecretOverride, cancellationToken);
                return (true, null, null);

            case SettingsIpcMethods.BackupSettingsList:
                if (_backupSettings is null) return unavailable(method);
                if (SettingsIpcPayload.Decode(payload, SettingsIpcJsonContext.Default.BackupEndpoint)
                    is not { } listEndpoint)
                {
                    return (false, null, "missing payload");
                }

                IReadOnlyList<BackupRemoteSnapshot> remoteSnapshots =
                    await _backupSettings.ListAsync(listEndpoint, cancellationToken);
                return (true, SettingsIpcPayload.Encode(
                    remoteSnapshots.ToList(),
                    SettingsIpcJsonContext.Default.ListBackupRemoteSnapshot), null);

            // ── Cloud restore actions (batch 3c-3a) ──────────────────

            case SettingsIpcMethods.CloudStop:
                if (_backupRestore is null) return unavailable(method);
                await _backupRestore.StopAsync();
                return (true, null, null);

            case SettingsIpcMethods.CloudIsCurrentEndpoint:
                if (_backupRestore is null) return unavailable(method);
                if (SettingsIpcPayload.Decode(payload, SettingsIpcJsonContext.Default.BackupEndpoint)
                    is not { } currentEndpoint)
                {
                    return (false, null, "missing payload");
                }

                return (true, SettingsIpcPayload.EncodeBoolean(
                    _backupRestore.IsCurrentEndpoint(currentEndpoint)), null);

            case SettingsIpcMethods.CloudDeleteSnapshot:
                if (_backupRestore is null) return unavailable(method);
                if (SettingsIpcPayload.Decode(payload, SettingsIpcJsonContext.Default.SettingsIpcEndpointName)
                    is not { } deleteTarget)
                {
                    return (false, null, "missing payload");
                }

                await _backupRestore.DeleteSnapshotAsync(deleteTarget.Endpoint, deleteTarget.Name, cancellationToken);
                return (true, null, null);

            case SettingsIpcMethods.CloudDownloadSnapshot:
                if (_backupRestore is null) return unavailable(method);
                if (SettingsIpcPayload.Decode(payload, SettingsIpcJsonContext.Default.SettingsIpcEndpointName)
                    is not { } downloadTarget)
                {
                    return (false, null, "missing payload");
                }

                return (true, await _backupRestore.DownloadSnapshotAsync(
                    downloadTarget.Endpoint,
                    downloadTarget.Name,
                    downloadTarget.Directory ?? string.Empty,
                    cancellationToken), null);

            case SettingsIpcMethods.CloudPrepareScopedRestore:
                if (_backupRestore is null) return unavailable(method);
                if (SettingsIpcPayload.Decode(payload, SettingsIpcJsonContext.Default.SettingsIpcScopedRestore)
                    is not { } scoped)
                {
                    return (false, null, "missing payload");
                }

                DeskBoxRestorePreparation scopedPreparation = await _backupRestore.PrepareScopedRestoreAsync(
                    scoped.ArchivePath,
                    (CloudBackupDomain)scoped.Scope,
                    cancellationToken);
                return (true, SettingsIpcPayload.Encode(
                    scopedPreparation,
                    SettingsIpcJsonContext.Default.DeskBoxRestorePreparation), null);

            case SettingsIpcMethods.CloudCancelPendingRestore:
                if (_backupRestore is null) return unavailable(method);
                await _backupRestore.CancelPendingRestoreAsync(cancellationToken);
                return (true, null, null);

            case SettingsIpcMethods.CloudSetReplaceMode:
                if (_backupRestore is null) return unavailable(method);
                return (await _backupRestore.SetPendingRestoreItemReplaceModeAsync(
                    SettingsIpcPayload.DecodeBoolean(payload),
                    cancellationToken), null, null);

            case SettingsIpcMethods.CloudScheduleRelaunch:
                if (_backupRestore is null) return unavailable(method);
                AppRelaunchScheduleResult relaunch = _backupRestore.ScheduleRelaunch();
                return (true, SettingsIpcPayload.Encode(
                    relaunch,
                    SettingsIpcJsonContext.Default.AppRelaunchScheduleResult), null);

            case SettingsIpcMethods.CloudShutdownForRestart:
                if (_backupRestore is null) return unavailable(method);
                await _backupRestore.ShutdownForRestartAsync();
                return (true, null, null);

            case SettingsIpcMethods.CloudDeleteTemporaryDownload:
                if (_backupRestore is null) return unavailable(method);
                _backupRestore.DeleteTemporaryDownload(payload);
                return (true, null, null);

            // ── Update service (batch 3c-3a) ─────────────────────────

            case SettingsIpcMethods.UpdateState:
                if (_appUpdate is null) return unavailable(method);
                return (true, SettingsIpcPayload.Encode(
                    new SettingsIpcUpdateState(
                        _appUpdate.LastCheckResult is { } lastCheck
                            ? SettingsIpcPayload.Encode(
                                lastCheck,
                                SettingsIpcJsonContext.Default.AppUpdateCheckResult)
                            : null,
                        _appUpdate.LastCheckTimeUtc?.Ticks ?? 0,
                        _appUpdate.ManifestUrl,
                        (int)_appUpdate.DeliveryKind,
                        _appUpdate.LastDownloadedInstallerPath,
                        _appUpdate.IsDownloadInProgress),
                    SettingsIpcJsonContext.Default.SettingsIpcUpdateState), null);

            case SettingsIpcMethods.UpdateCheck:
                if (_appUpdate is null) return unavailable(method);
                string? version = SettingsIpcPayload.Decode(
                        payload,
                        SettingsIpcJsonContext.Default.SettingsIpcUpdateCheck)
                    ?.CurrentVersion;
                AppUpdateCheckResult checkResult = string.IsNullOrEmpty(version)
                    ? await _appUpdate.CheckForUpdatesAsync(cancellationToken)
                    : await _appUpdate.CheckForUpdatesAsync(version, cancellationToken);
                return (true, SettingsIpcPayload.Encode(
                    checkResult,
                    SettingsIpcJsonContext.Default.AppUpdateCheckResult), null);

            case SettingsIpcMethods.UpdateDownload:
                if (_appUpdate is null) return unavailable(method);
                if (SettingsIpcPayload.Decode(payload, SettingsIpcJsonContext.Default.AppUpdateManifest)
                    is not { } downloadManifest)
                {
                    return (false, null, "missing payload");
                }

                AppUpdateDownloadResult downloadResult = await _appUpdate.DownloadUpdateAsync(
                    downloadManifest,
                    progress: null,
                    cancellationToken);
                return (true, SettingsIpcPayload.Encode(
                    downloadResult,
                    SettingsIpcJsonContext.Default.AppUpdateDownloadResult), null);

            case SettingsIpcMethods.UpdateClearInstaller:
                if (_appUpdate is null) return unavailable(method);
                _appUpdate.ClearLastDownloadedInstaller();
                return (true, null, null);

            case SettingsIpcMethods.UpdatePruneStale:
                if (_appUpdate is null) return unavailable(method);
                _appUpdate.PruneStaleInstallerDirectories();
                return (true, null, null);

            case SettingsIpcMethods.UpdateStartInstaller:
                if (_appUpdate is null) return unavailable(method);
                if (SettingsIpcPayload.Decode(payload, SettingsIpcJsonContext.Default.SettingsIpcStartInstaller)
                    is not { } installer)
                {
                    return (false, null, "missing payload");
                }

                AppUpdateInstallResult installResult = _appUpdate.StartInstallerHelper(
                    installer.InstallerPath,
                    installer.Silent,
                    installer.AppCultureName);
                return (true, SettingsIpcPayload.Encode(
                    installResult,
                    SettingsIpcJsonContext.Default.AppUpdateInstallResult), null);

            // ── Everything integration (batch 3d follow-up) ──────────

            case SettingsIpcMethods.SearchEverythingState:
                if (_everythingClient is null) return unavailable(method);
                return (true, SettingsIpcPayload.Encode(
                    _everythingClient.CurrentSnapshot,
                    SettingsIpcJsonContext.Default.EverythingConnectionSnapshot), null);

            case SettingsIpcMethods.SearchEverythingRefresh:
                if (_everythingClient is null) return unavailable(method);
                bool allowProbe = SettingsIpcPayload.Decode(
                    payload, SettingsIpcJsonContext.Default.SettingsIpcEverythingRefresh)
                    ?.AllowIpcProbe ?? true;
                // The refresh touches the Everything process and file system;
                // run it marshalled like every other settings-side chain.
                DeskBox.Contracts.EverythingConnectionSnapshot refreshed =
                    await DeskBox.Platform.UiDispatch.RunAsync(() =>
                        _everythingClient.RefreshConnectionAsync(allowProbe, cancellationToken));
                return (true, SettingsIpcPayload.Encode(
                    refreshed,
                    SettingsIpcJsonContext.Default.EverythingConnectionSnapshot), null);

            case SettingsIpcMethods.SearchEverythingAuto:
                if (_everythingClient is null) return unavailable(method);
                await DeskBox.Platform.UiDispatch.RunAsync(
                    () => _everythingClient.UseAutomaticDetectionAsync(cancellationToken));
                return (true, SettingsIpcPayload.Encode(
                    _everythingClient.CurrentSnapshot,
                    SettingsIpcJsonContext.Default.EverythingConnectionSnapshot), null);

            case SettingsIpcMethods.SearchEverythingSetPath:
                if (_everythingClient is null) return unavailable(method);
                if (SettingsIpcPayload.Decode(payload, SettingsIpcJsonContext.Default.SettingsIpcEverythingPath)
                    is not { } everythingPath)
                {
                    return (false, null, "missing payload");
                }

                bool pathApplied = await DeskBox.Platform.UiDispatch.RunAsync(() =>
                    _everythingClient.SetExecutablePathAsync(everythingPath.Path, cancellationToken));
                return (pathApplied, null, pathApplied ? null : "path rejected");

            case SettingsIpcMethods.SearchEverythingLaunch:
                if (_everythingClient is null) return unavailable(method);
                bool launched = await DeskBox.Platform.UiDispatch.RunAsync(() =>
                    _everythingClient.LaunchEverythingAsync(cancellationToken));
                return (launched, null, launched ? null : "launch failed");

            default:
                return (false, null, $"unknown method {method}");
        }
    }

    private static string SerializeLayout(SettingsService? settings) =>
        settings?.SerializeCurrentLayout() ?? string.Empty;

    private static WidgetKind DecodeKind(string? payload) =>
        int.TryParse(payload, out int kind) ? (WidgetKind)kind : WidgetKind.File;

    private static SettingsIpcKindArg DecodeKindArg(string? payload) =>
        SettingsIpcPayload.Decode(payload, SettingsIpcJsonContext.Default.SettingsIpcKindArg)
            ?? new SettingsIpcKindArg(0, false);

    private static (bool, string?, string?) unavailable(string method) =>
        (false, null, $"{method}: host service unavailable");

    /// <summary>
    /// widget.glance-apply-data (batch R8): the settings section's per-widget
    /// Glance edits land in the MAIN process's store instance — it owns the
    /// file and its Changed event is what live desktop Glance widgets
    /// re-read from. The UI-thread marshal matches the in-process section,
    /// which always saved from the UI thread.
    /// </summary>
    private static async Task<(bool, string?, string?)> HandleGlanceApplyDataAsync(
        string? payload)
    {
        if (string.IsNullOrEmpty(payload))
        {
            return (false, null, "empty payload");
        }

        int separator = payload.IndexOf('\n');
        if (separator <= 0 || separator == payload.Length - 1)
        {
            return (false, null, "malformed payload");
        }

        string widgetId = payload[..separator];
        GlanceWidgetData? data;
        try
        {
            data = SettingsIpcPayload.Decode(
                payload[(separator + 1)..],
                GlancePreferencesJsonContext.Default.Preferences);
        }
        catch (JsonException)
        {
            return (false, null, "malformed payload");
        }

        if (string.IsNullOrWhiteSpace(widgetId) || data is null)
        {
            return (false, null, "malformed payload");
        }

        GlanceWidgetStore store = GlanceWidgetStore.ForWidget(widgetId);
        await DeskBox.Platform.UiDispatch.RunAsync(() => store.SaveAsync(data));
        return (true, null, null);
    }

    private static string? SerializeNullable<T>(T? value, JsonTypeInfo<T> typeInfo) where T : class =>
        value is null ? null : SettingsIpcPayload.Encode(value, typeInfo);
}
