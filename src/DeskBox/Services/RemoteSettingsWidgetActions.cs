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

    public bool IsHostAvailable => true;

    private readonly SettingsService _settingsService;

    // Deep review R13-2 (D15 audit): layout writes fail into the log only —
    // the host's combos showed new values while the desktop kept the old
    // ones with zero user-visible signal. Optional failure reporter wired
    // by the settings host to its save-failed banner.
    private readonly Action? _onWriteFailure;

    public RemoteSettingsWidgetActions(SettingsIpcClient client, SettingsService settingsService, Action? onWriteFailure = null)
    {
        _client = client;
        _settingsService = settingsService;
        _onWriteFailure = onWriteFailure;
    }

    public Task ApplyGroupPresentation(DeskBox.Contracts.GroupPresentationChange change) =>
        SendLayoutWrite(
            SettingsIpcMethods.WidgetApplyGroupPresentation,
            SettingsIpcPayload.Encode(change, SettingsIpcJsonContext.Default.GroupPresentationChange));

    public Task ApplyGroupNavigationDefaults(DeskBox.Contracts.GroupNavigationDefaultsChange change) =>
        SendLayoutWrite(
            SettingsIpcMethods.WidgetApplyGroupNavDefaults,
            SettingsIpcPayload.Encode(change, SettingsIpcJsonContext.Default.GroupNavigationDefaultsChange));

    public Task ApplyLayoutPreferences(DeskBox.Contracts.LayoutPreferencesChange change) =>
        SendLayoutWrite(
            SettingsIpcMethods.WidgetApplyLayoutPreferences,
            SettingsIpcPayload.Encode(change, SettingsIpcJsonContext.Default.LayoutPreferencesChange));

    public Task ResetCapsuleOverrides(DeskBox.Contracts.CapsuleOverrideReset reset) =>
        SendLayoutWrite(
            SettingsIpcMethods.WidgetResetCapsuleOverrides,
            SettingsIpcPayload.Encode(reset, SettingsIpcJsonContext.Default.CapsuleOverrideReset));

    /// <summary>
    /// The appearance page's "清除每格背景" button (batch R8): the overrides
    /// live in the layout domain the main process owns, so the clear rides a
    /// dedicated command (asset deletion included) instead of the snapshot.
    /// </summary>
    public Task ClearPerWidgetBackgrounds() =>
        SendLayoutWrite(
            SettingsIpcMethods.AppearanceClearPerWidgetBackgrounds,
            string.Empty);

    /// <summary>
    /// Layout writes: the response carries the authoritative slice — merge
    /// it into the local mirror BEFORE returning so the caller's immediate
    /// refresh reads fresh state (no stale flash, no reliance on push
    /// ordering).
    /// </summary>
    private async Task SendLayoutWrite(string method, string payload)
    {
        // The callers are fire-and-forget (_ = ...): without this catch a
        // pipe failure/timeout vanished with zero logs while the local
        // mirror kept the edit — a silent false success (batch R5). R13-2
        // adds the failure reporter: the banner tells the user the change
        // did not land instead of leaving the UI and desktop diverging.
        try
        {
            (bool ok, string? sliceJson, _) = await _client.RequestAsync(method, payload);
            if (ok &&
                !string.IsNullOrEmpty(sliceJson) &&
                _settingsService.ApplyExternalLayout(sliceJson))
            {
                return;
            }

            // Best effort: pull the slice directly when the response carried none.
            (bool fetchOk, string? fetched, _) = await _client.RequestAsync(
                SettingsIpcMethods.WidgetLayoutRead);
            if (fetchOk && !string.IsNullOrEmpty(fetched) &&
                _settingsService.ApplyExternalLayout(fetched))
            {
                return;
            }

            _onWriteFailure?.Invoke();
        }
        catch (Exception ex)
        {
            App.Log($"[LayoutWrite] {method} failed: {ex.Message}");
            _onWriteFailure?.Invoke();
        }
    }

    // Batch R11: this is a VM property-getter hot path — every settings
    // reload push re-projects the feature list, and each entry used to cost
    // a synchronous pipe roundtrip on the UI thread. The feature dictionary
    // lives in the layout domain of the local mirror, which the host keeps
    // current through layout pushes and the authoritative slice merged into
    // every layout-write response — the same source the in-process facade
    // reads. No roundtrip needed.
    public bool IsFeatureWidgetEnabled(WidgetKind kind) =>
        FeatureWidgetSettings.IsEnabled(_settingsService.Settings, kind);

    public async Task<bool> SetFeatureWidgetEnabled(WidgetKind kind, bool enabled)
    {
        try
        {
            (bool ok, string? payload, string? error) = await _client.RequestAsync(
                SettingsIpcMethods.WidgetSyncFeatureEnabled,
                SettingsIpcPayload.Encode(
                    new SettingsIpcKindArg((int)kind, enabled),
                    SettingsIpcJsonContext.Default.SettingsIpcKindArg));
            if (!ok)
            {
                // A refused command left the authoritative dictionary on its
                // old value: report the OPPOSITE of the request so every
                // optimistic mirror (card, Todo/QC section switches) reverts
                // instead of showing a fabricated success (batch R9).
                App.Log(
                    $"[SettingsIpc] Feature switch {kind} refused: {error ?? "unknown"} — reverting the optimistic mirror");
                return !enabled;
            }

            // The response carries the applied authoritative value; on failure
            // keep the requested value so the optimistic mirror stands.
            return SettingsIpcPayload.DecodeBoolean(payload) is { } applied && applied != enabled
                ? applied
                : enabled;
        }
        catch (Exception ex)
        {
            // Same revert contract for transport failures/timeouts: the main
            // process either never saw the switch or will report the truth
            // with its next layout push.
            App.Log(
                $"[SettingsIpc] Feature switch {kind} failed: {ex.Message} — reverting the optimistic mirror");
            return !enabled;
        }
    }

    public Task ResetFeatureWidgetAsync(WidgetKind kind) =>
        RequestVoidAsync(
            SettingsIpcMethods.WidgetResetFeature,
            ((int)kind).ToString());

    // Fire-and-forget notifications (batch R10): both are invoked from
    // VM property setters driven by XAML bindings, i.e. synchronously on
    // the UI thread. A blocking roundtrip there parks the thread in the
    // COM modal loop, where an incoming accessibility callback that never
    // returns (a UIA client that died mid-call) keeps the loop from ever
    // checking the wait handles — a permanently wedged, unclosable
    // settings window even after the awaited task completes (dump-proven,
    // 0x73d8, batch R10 acceptance). Nothing reads their outcome, so they
    // fly without waiting; failures surface through the log.
    public void RefreshVisibleWidgetDesktopLayers(string reason) =>
        _ = NotifyWithoutBlockingAsync(
            SettingsIpcMethods.WidgetRefreshLayers,
            reason,
            nameof(RefreshVisibleWidgetDesktopLayers));

    public void NotifyWidgetGroupPresentationSettingsChanged() =>
        _ = NotifyWithoutBlockingAsync(
            SettingsIpcMethods.WidgetNotifyGroupPresentation,
            payload: null,
            nameof(NotifyWidgetGroupPresentationSettingsChanged));

    private Task NotifyWithoutBlockingAsync(string method, string? payload, string caller) =>
        SettingsIpcShell.NotifyWithoutBlockingAsync(_client, method, payload, caller);

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

    private Task RequestVoidAsync(string method, string? payload = null) =>
        SettingsIpcShell.RequestVoidAsync(_client, method, payload);
}
