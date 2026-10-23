namespace DeskBox.Services;

using DeskBox.Contracts;
using DeskBox.Models;

/// <summary>
/// Settings-host widget facade over the IPC pipe (batch 3c-2). Move-all undo
/// tokens cross the wire as registry handles minted by the main side;
/// migration progress rides pushed events back into the caller's IProgress.
/// The synchronous query members block on the pipe round-trip (rare paths).
/// </summary>
public sealed class RemoteSettingsWidgetActions : ISettingsWidgetActions
{
    private readonly SettingsIpcClient _client;

    public RemoteSettingsWidgetActions(SettingsIpcClient client)
    {
        _client = client;
    }

    public bool IsHostAvailable => true;

    public bool IsFeatureWidgetEnabled(WidgetKind kind)
    {
        (bool ok, string? payload, _) = _client.RequestAsync(
                SettingsIpcMethods.WidgetIsFeatureEnabled,
                ((int)kind).ToString())
            .GetAwaiter().GetResult();
        return ok && SettingsIpcPayload.DecodeBoolean(payload);
    }

    public Task SyncFeatureWidgetEnabledAsync(WidgetKind kind, bool enabled) =>
        RequestVoidAsync(
            SettingsIpcMethods.WidgetSyncFeatureEnabled,
            SettingsIpcPayload.Encode(
                new SettingsIpcKindArg((int)kind, enabled),
                SettingsIpcJsonContext.Default.SettingsIpcKindArg));

    public Task ResetFeatureWidgetAsync(WidgetKind kind) =>
        RequestVoidAsync(
            SettingsIpcMethods.WidgetResetFeature,
            ((int)kind).ToString());

    public void RefreshVisibleWidgetDesktopLayers(string reason) =>
        RequestVoidAsync(SettingsIpcMethods.WidgetRefreshLayers, reason)
            .GetAwaiter().GetResult();

    public void NotifyWidgetGroupPresentationSettingsChanged() =>
        RequestVoidAsync(SettingsIpcMethods.WidgetNotifyGroupPresentation)
            .GetAwaiter().GetResult();

    public async Task<bool> ReorderWidgetGroupMemberAsync(string sourceWidgetId, string targetWidgetId)
    {
        (bool ok, _, _) = await _client.RequestAsync(
            SettingsIpcMethods.WidgetReorderGroupMember,
            SettingsIpcPayload.Encode(
                new SettingsIpcStringPair(sourceWidgetId, targetWidgetId),
                SettingsIpcJsonContext.Default.SettingsIpcStringPair));
        return ok;
    }

    public async Task<bool> RemoveWidgetFromGroupAsync(string widgetId)
    {
        (bool ok, _, _) = await _client.RequestAsync(
            SettingsIpcMethods.WidgetRemoveFromGroup,
            widgetId);
        return ok;
    }

    public async Task<bool> DissolveWidgetGroupContainingAsync(string widgetId)
    {
        (bool ok, _, _) = await _client.RequestAsync(
            SettingsIpcMethods.WidgetDissolveGroup,
            widgetId);
        return ok;
    }

    public IReadOnlyList<ManagedStorageFolderCleanupCandidate>? GetOrphanManagedStorageFolders()
    {
        (bool ok, string? payload, _) = _client.RequestAsync(SettingsIpcMethods.WidgetGetOrphans)
            .GetAwaiter().GetResult();
        return ok
            ? SettingsIpcPayload.Decode(payload, SettingsIpcJsonContext.Default.ListManagedStorageFolderCleanupCandidate)
            : null;
    }

    public async Task<int> RestoreOrphanManagedStorageFoldersAsync(IReadOnlyList<string> folderPaths)
    {
        (bool ok, string? payload, _) = await _client.RequestAsync(
            SettingsIpcMethods.WidgetRestoreOrphans,
            SettingsIpcPayload.Encode(folderPaths.ToList(), SettingsIpcJsonContext.Default.ListString));
        return ok && int.TryParse(payload, out int restored) ? restored : 0;
    }

    public Task MoveOrphanManagedStorageFolderContentsToDesktopAsync(string folderPath) =>
        RequestVoidAsync(SettingsIpcMethods.WidgetMoveOrphanToDesktop, folderPath);

    public Task DeleteOrphanManagedStorageFolderAsync(string folderPath, nint ownerWindowHandle) =>
        RequestVoidAsync(
            SettingsIpcMethods.WidgetDeleteOrphan,
            SettingsIpcPayload.Encode(
                new SettingsIpcOrphanDelete(folderPath, ownerWindowHandle),
                SettingsIpcJsonContext.Default.SettingsIpcOrphanDelete));

    public int GetDefaultManagedStorageWidgetCount()
    {
        (bool ok, string? payload, _) = _client.RequestAsync(SettingsIpcMethods.WidgetManagedCount)
            .GetAwaiter().GetResult();
        return ok && int.TryParse(payload, out int count) ? count : 0;
    }

    public async Task<ManagedStorageMigrationPreview> PreviewManagedStorageMigrationAsync(
        string newRootPath,
        CancellationToken token)
    {
        (bool ok, string? payload, _) = await _client.RequestAsync(
            SettingsIpcMethods.WidgetMigratePreview,
            newRootPath,
            token);
        return ok && SettingsIpcPayload.Decode(payload, SettingsIpcJsonContext.Default.ManagedStorageMigrationPreview)
            is { } preview
                ? preview
                : new ManagedStorageMigrationPreview(0, 0, 0, false, null);
    }

    public Task<ManagedStorageMigrationResult> UpdateDefaultManagedStorageRootAsync(
        string newRootPath,
        ManagedStorageMigrationOptions? options)
    {
        return UpdateDefaultManagedStorageRootCoreAsync(newRootPath, options);
    }

    private async Task<ManagedStorageMigrationResult> UpdateDefaultManagedStorageRootCoreAsync(
        string newRootPath,
        ManagedStorageMigrationOptions? options)
    {
        // The dialog's IProgress cannot ride the request; main-side progress
        // arrives as pushed events and is re-reported into the caller's
        // progress until the request completes.
        IProgress<ManagedStorageMigrationProgress>? callerProgress = options?.Progress;
        void OnMigrationEvent(string method, string? eventPayload)
        {
            if (method != SettingsIpcMethods.WidgetMigrationProgress ||
                eventPayload is null)
            {
                return;
            }

            int separator = eventPayload.IndexOf('\n');
            if (separator > 0 &&
                SettingsIpcPayload.Decode(
                    eventPayload[(separator + 1)..],
                    SettingsIpcJsonContext.Default.ManagedStorageMigrationProgress) is { } progress)
            {
                callerProgress?.Report(progress);
            }
        }

        _client.EventReceived += OnMigrationEvent;
        try
        {
            (bool ok, string? payload, string? error) = await _client.RequestAsync(
                SettingsIpcMethods.WidgetMigrateRoot,
                SettingsIpcPayload.Encode(
                    new SettingsIpcMigrationRequest(
                        newRootPath,
                        options?.RestartIfCopiesRemoved ?? false),
                    SettingsIpcJsonContext.Default.SettingsIpcMigrationRequest));
            if (!ok ||
                SettingsIpcPayload.Decode(payload, SettingsIpcJsonContext.Default.ManagedStorageMigrationResult)
                    is not { } result)
            {
                throw new InvalidOperationException(error ?? "storage migration failed");
            }

            return result;
        }
        finally
        {
            _client.EventReceived -= OnMigrationEvent;
        }
    }

    public IReadOnlyList<GlanceWidgetInstanceInfo> GetGlanceWidgetInstances()
    {
        (bool ok, string? payload, _) = _client.RequestAsync(SettingsIpcMethods.WidgetGlanceList)
            .GetAwaiter().GetResult();
        return ok
            ? SettingsIpcPayload.Decode(payload, SettingsIpcJsonContext.Default.ListGlanceWidgetInstanceInfo) ?? []
            : [];
    }

    public bool IsGlanceFeatureEnabled
    {
        get
        {
            (bool ok, string? payload, _) = _client.RequestAsync(SettingsIpcMethods.WidgetGlanceFeatureEnabled)
                .GetAwaiter().GetResult();
            return ok && SettingsIpcPayload.DecodeBoolean(payload);
        }
    }

    public async Task<GlanceWidgetInstanceInfo> CreateGlanceWidgetAsync(string? sourceWidgetId)
    {
        (bool ok, string? payload, string? error) = await _client.RequestAsync(
            SettingsIpcMethods.WidgetGlanceCreate,
            sourceWidgetId is null
                ? null
                : SettingsIpcPayload.Encode(
                    new SettingsIpcGlanceCreate(sourceWidgetId),
                    SettingsIpcJsonContext.Default.SettingsIpcGlanceCreate));
        if (!ok ||
            SettingsIpcPayload.Decode(payload, SettingsIpcJsonContext.Default.GlanceWidgetInstanceInfo)
                is not { } created)
        {
            throw new InvalidOperationException(error ?? "glance create failed");
        }

        return created;
    }

    public Task SetGlanceWidgetInstanceEnabledAsync(string widgetId, bool enabled) =>
        RequestVoidAsync(
            SettingsIpcMethods.WidgetGlanceSetEnabled,
            SettingsIpcPayload.Encode(
                new SettingsIpcGlanceSetEnabled(widgetId, enabled),
                SettingsIpcJsonContext.Default.SettingsIpcGlanceSetEnabled));

    public async Task<bool> LocateGlanceWidgetAsync(string widgetId)
    {
        (bool ok, _, _) = await _client.RequestAsync(SettingsIpcMethods.WidgetGlanceLocate, widgetId);
        return ok;
    }

    public Task RemoveGlanceWidgetAsync(string widgetId) =>
        RequestVoidAsync(SettingsIpcMethods.WidgetGlanceRemove, widgetId);

    public Task RenameWidgetAsync(string widgetId, string newName) =>
        RequestVoidAsync(
            SettingsIpcMethods.WidgetGlanceRename,
            SettingsIpcPayload.Encode(
                new SettingsIpcGlanceRename(widgetId, newName),
                SettingsIpcJsonContext.Default.SettingsIpcGlanceRename));

    public async Task<(int MovedCount, WidgetMoveAllUndoToken Token)> MoveAllWidgetSurfacesToDisplayAsync(
        string displayStableId)
    {
        (bool ok, string? payload, string? error) = await _client.RequestAsync(
            SettingsIpcMethods.WidgetMoveAll,
            displayStableId);
        if (!ok ||
            SettingsIpcPayload.Decode(payload, SettingsIpcJsonContext.Default.SettingsIpcMoveAllResult)
                is not { } moveAll)
        {
            throw new InvalidOperationException(error ?? "move-all failed");
        }

        return (moveAll.MovedCount, new WidgetMoveAllUndoToken { IpcHandleId = moveAll.Handle });
    }

    public async Task<bool> UndoMoveAllWidgetSurfacesAsync(WidgetMoveAllUndoToken token)
    {
        if (token.IpcHandleId is not { } handle)
        {
            return false;
        }

        (bool ok, _, _) = await _client.RequestAsync(
            SettingsIpcMethods.WidgetMoveAllUndo,
            handle.ToString());
        return ok;
    }

    public Task ApplyScreenBindingAsync(
        string widgetId,
        WidgetScreenBindingMode mode,
        string? boundScreenId,
        bool persistImmediately = true) =>
        RequestVoidAsync(
            SettingsIpcMethods.WidgetApplyScreenBinding,
            SettingsIpcPayload.Encode(
                new SettingsIpcScreenBinding(widgetId, (int)mode, boundScreenId, persistImmediately),
                SettingsIpcJsonContext.Default.SettingsIpcScreenBinding));

    public IReadOnlyList<FolderWatcherHealthSnapshot>? GetFolderWatcherHealthSnapshots()
    {
        (bool ok, string? payload, _) = _client.RequestAsync(SettingsIpcMethods.WidgetFolderWatcherHealth)
            .GetAwaiter().GetResult();
        return ok
            ? SettingsIpcPayload.Decode(payload, SettingsIpcJsonContext.Default.ListFolderWatcherHealthSnapshot)
            : null;
    }

    public OrganizationHistoryEntry? GetLatestUndoableDesktopOrganizationEntry()
    {
        (bool ok, string? payload, _) = _client.RequestAsync(SettingsIpcMethods.WidgetLatestUndoableOrgEntry)
            .GetAwaiter().GetResult();
        return ok
            ? SettingsIpcPayload.Decode(payload, SettingsIpcJsonContext.Default.OrganizationHistoryEntry)
            : null;
    }

    public OrganizationHistoryEntry? GetDesktopOrganizationHistoryEntry(string historyId)
    {
        (bool ok, string? payload, _) = _client.RequestAsync(
                SettingsIpcMethods.WidgetOrgHistoryEntry,
                historyId)
            .GetAwaiter().GetResult();
        return ok
            ? SettingsIpcPayload.Decode(payload, SettingsIpcJsonContext.Default.OrganizationHistoryEntry)
            : null;
    }

    public async Task<DesktopOrganizationUndoResult> UndoDesktopOrganizationAsync(
        string historyId,
        nint ownerWindowHandle)
    {
        (bool ok, string? payload, _) = await _client.RequestAsync(
            SettingsIpcMethods.WidgetOrgUndo,
            SettingsIpcPayload.Encode(
                new SettingsIpcOrgUndo(historyId, ownerWindowHandle),
                SettingsIpcJsonContext.Default.SettingsIpcOrgUndo));
        return ok && SettingsIpcPayload.Decode(payload, SettingsIpcJsonContext.Default.DesktopOrganizationUndoResult)
            is { } result
                ? result
                : new DesktopOrganizationUndoResult(
                    DesktopOrganizationUndoOutcome.Failed,
                    Error: "undo request failed");
    }

    public async Task<DesktopOrganizationUndoResult> AbandonDesktopOrganizationUndoAsync(string historyId)
    {
        (bool ok, string? payload, _) = await _client.RequestAsync(
            SettingsIpcMethods.WidgetOrgAbandon,
            historyId);
        return ok && SettingsIpcPayload.Decode(payload, SettingsIpcJsonContext.Default.DesktopOrganizationUndoResult)
            is { } result
                ? result
                : new DesktopOrganizationUndoResult(
                    DesktopOrganizationUndoOutcome.Failed,
                    Error: "abandon request failed");
    }

    private async Task RequestVoidAsync(string method, string? payload = null)
    {
        await _client.RequestAsync(method, payload);
    }
}
