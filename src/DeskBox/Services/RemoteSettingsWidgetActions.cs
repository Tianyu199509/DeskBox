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
        token.ThrowIfCancellationRequested();
        string operationId = Guid.NewGuid().ToString("N");
        await using CancellationTokenRegistration cancelReg = token.CanBeCanceled
            ? token.Register(() =>
                _ = NotifyWithoutBlockingAsync(
                    SettingsIpcMethods.WidgetMigrateCancel,
                    payload: operationId,
                    nameof(PreviewManagedStorageMigrationAsync)))
            : default;

        (bool ok, string? payload, string? error) = await _client.RequestAsync(
            SettingsIpcMethods.WidgetMigratePreview,
            SettingsIpcPayload.Encode(
                new SettingsIpcMigrationPreviewRequest(newRootPath, operationId),
                SettingsIpcJsonContext.Default.SettingsIpcMigrationPreviewRequest),
            CancellationToken.None);
        if (!ok)
        {
            throw new IOException(error ?? "storage migration preview failed");
        }

        SettingsIpcMigrationOutcome? outcome = SettingsIpcPayload.Decode(
            payload,
            SettingsIpcJsonContext.Default.SettingsIpcMigrationOutcome);
        if (outcome is { Ok: true, Preview: { } preview })
        {
            return preview;
        }

        if (outcome is { Stopped: true })
        {
            throw new OperationCanceledException(outcome.ErrorMessage ?? "Storage migration preview stopped.", token);
        }

        if (outcome is not null)
        {
            StorageMigrationProblem problem = outcome.ProblemKind is { } kind &&
                                              Enum.IsDefined(typeof(StorageMigrationProblem), kind)
                ? (StorageMigrationProblem)kind
                : StorageMigrationProblem.Unknown;
            throw new StorageMigrationException(
                problem,
                outcome.ErrorMessage ?? "storage migration preview failed")
            {
                TemporaryBytes = outcome.TemporaryBytes,
            };
        }

        throw new IOException(error ?? "storage migration preview failed");
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
        CancellationToken callerToken = options?.CancellationToken ?? default;
        if (callerToken.IsCancellationRequested)
        {
            throw new StorageMigrationStoppedException(
                new OperationCanceledException(callerToken),
                temporaryBytes: null);
        }

        string operationId = Guid.NewGuid().ToString("N");

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
                eventPayload.AsSpan(0, separator).SequenceEqual(operationId.AsSpan()) &&
                SettingsIpcPayload.Decode(
                    eventPayload[(separator + 1)..],
                    SettingsIpcJsonContext.Default.ManagedStorageMigrationProgress) is { } progress)
            {
                callerProgress?.Report(progress);
            }
        }

        // Forward Stop clicks to the main process over WidgetMigrateCancel and
        // wait for the server's structured Stopped response (which carries
        // TemporaryBytes after journal flush and gate release) instead of
        // abandoning the in-flight pipe request mid-copy.
        await using CancellationTokenRegistration cancelReg = callerToken.CanBeCanceled
            ? callerToken.Register(() =>
                _ = NotifyWithoutBlockingAsync(
                    SettingsIpcMethods.WidgetMigrateCancel,
                    payload: operationId,
                    nameof(UpdateDefaultManagedStorageRootCoreAsync)))
            : default;

        _client.EventReceived += OnMigrationEvent;
        try
        {
            (bool ok, string? payload, string? error) = await _client.RequestAsync(
                SettingsIpcMethods.WidgetMigrateRoot,
                SettingsIpcPayload.Encode(
                    new SettingsIpcMigrationRequest(
                        newRootPath,
                        options?.RestartIfCopiesRemoved ?? false,
                        operationId),
                    SettingsIpcJsonContext.Default.SettingsIpcMigrationRequest));
            if (!ok)
            {
                throw new IOException(error ?? "storage migration failed");
            }

            SettingsIpcMigrationOutcome? outcome = SettingsIpcPayload.Decode(
                payload,
                SettingsIpcJsonContext.Default.SettingsIpcMigrationOutcome);
            if (outcome is { Ok: true, Result: { } result })
            {
                return result;
            }

            if (outcome is { Stopped: true })
            {
                throw new StorageMigrationStoppedException(
                    new OperationCanceledException(outcome.ErrorMessage ?? "Storage migration stopped; originals retained."),
                    outcome.TemporaryBytes);
            }

            if (outcome is not null)
            {
                StorageMigrationProblem problem = outcome.ProblemKind is { } kind &&
                                                  Enum.IsDefined(typeof(StorageMigrationProblem), kind)
                    ? (StorageMigrationProblem)kind
                    : StorageMigrationProblem.Unknown;
                throw new StorageMigrationException(
                    problem,
                    outcome.ErrorMessage ?? "storage migration failed")
                {
                    TemporaryBytes = outcome.TemporaryBytes,
                };
            }

            throw new InvalidOperationException(error ?? "storage migration failed");
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

    public async Task<IReadOnlyList<GlanceWidgetInstanceInfo>> GetCustomWidgetInstancesAsync()
    {
        (bool ok, string? payload, _) = await _client.RequestAsync(SettingsIpcMethods.WidgetCustomList);
        return ok
            ? SettingsIpcPayload.Decode(payload, SettingsIpcJsonContext.Default.ListGlanceWidgetInstanceInfo) ?? []
            : [];
    }

    public async Task<GlanceWidgetInstanceInfo> CreateCustomWidgetAsync(string? templateOrPackageSpec)
    {
        (bool ok, string? payload, string? error) = await _client.RequestAsync(
            SettingsIpcMethods.WidgetCustomCreate,
            templateOrPackageSpec);
        if (!ok ||
            SettingsIpcPayload.Decode(payload, SettingsIpcJsonContext.Default.GlanceWidgetInstanceInfo)
                is not { } created)
        {
            throw new InvalidOperationException(error ?? "custom widget create failed");
        }

        return created;
    }

    public async Task<GlanceWidgetInstanceInfo> CreateCustomWidgetWithSetupAsync(Contracts.CustomWidgetCreateOptions options)
    {
        (bool ok, string? payload, string? error) = await _client.RequestAsync(
            SettingsIpcMethods.WidgetCustomCreateWithSetup,
            SettingsIpcPayload.Encode(
                options,
                SettingsIpcJsonContext.Default.CustomWidgetCreateOptions));
        if (!ok ||
            SettingsIpcPayload.Decode(payload, SettingsIpcJsonContext.Default.GlanceWidgetInstanceInfo)
                is not { } created)
        {
            throw new InvalidOperationException(error ?? "custom widget create failed");
        }

        return created;
    }

    public async Task ApplyCustomWidgetMutationAsync(Contracts.CustomWidgetSettingsMutation mutation)
    {
        (bool ok, _, string? error) = await _client.RequestAsync(
            SettingsIpcMethods.WidgetCustomApplyMutation,
            SettingsIpcPayload.Encode(
                mutation,
                SettingsIpcJsonContext.Default.CustomWidgetSettingsMutation));
        if (!ok)
        {
            throw new InvalidOperationException(error ?? "custom widget mutation failed");
        }
    }

    public Task SetCustomWidgetInstanceEnabledAsync(string widgetId, bool enabled) =>
        RequestVoidAsync(
            SettingsIpcMethods.WidgetCustomSetEnabled,
            SettingsIpcPayload.Encode(
                new SettingsIpcGlanceSetEnabled(widgetId, enabled),
                SettingsIpcJsonContext.Default.SettingsIpcGlanceSetEnabled));

    public async Task<bool> LocateCustomWidgetAsync(string widgetId)
    {
        (bool ok, _, _) = await _client.RequestAsync(SettingsIpcMethods.WidgetCustomLocate, widgetId);
        return ok;
    }

    public Task OpenCustomWidgetStudioAsync(string? widgetId, string? initialIdea) =>
        RequestVoidAsync(
            SettingsIpcMethods.WidgetCustomOpenStudio,
            EncodeStudioRequest(widgetId, initialIdea));

    /// <summary>"widgetId\nidea" — either part may be empty.</summary>
    internal static string EncodeStudioRequest(string? widgetId, string? initialIdea) =>
        (widgetId?.Trim() ?? string.Empty) + "\n" + (initialIdea ?? string.Empty);

    internal static (string? WidgetId, string? InitialIdea) DecodeStudioRequest(string? payload)
    {
        if (string.IsNullOrEmpty(payload))
        {
            return (null, null);
        }

        int separator = payload.IndexOf('\n');
        string id = separator < 0 ? payload : payload[..separator];
        string idea = separator < 0 ? string.Empty : payload[(separator + 1)..];
        return (
            string.IsNullOrWhiteSpace(id) ? null : id.Trim(),
            string.IsNullOrWhiteSpace(idea) ? null : idea);
    }

    public Task RemoveCustomWidgetAsync(string widgetId) =>
        RequestVoidAsync(SettingsIpcMethods.WidgetCustomRemove, widgetId);

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

        if (!string.IsNullOrEmpty(moveAll.LayoutSliceJson))
        {
            _ = _settingsService.ApplyExternalLayout(moveAll.LayoutSliceJson);
        }

        return (
            moveAll.MovedCount,
            new WidgetMoveAllUndoToken
            {
                IpcHandleId = moveAll.Handle,
                Persisted = moveAll.Persisted
            });
    }

    public async Task<bool> UndoMoveAllWidgetSurfacesAsync(WidgetMoveAllUndoToken token)
    {
        if (token.IpcHandleId is not { } handle)
        {
            return false;
        }

        (bool ok, string? payload, _) = await _client.RequestAsync(
            SettingsIpcMethods.WidgetMoveAllUndo,
            handle.ToString());
        if (ok && !string.IsNullOrEmpty(payload))
        {
            _ = _settingsService.ApplyExternalLayout(payload);
        }

        return ok;
    }

    public async Task<int> RecalibrateDisplayLayoutAsync()
    {
        (bool ok, string? payload, string? error) = await _client.RequestAsync(
            SettingsIpcMethods.WidgetRecalibrateDisplayLayout);
        if (!ok)
        {
            throw new InvalidOperationException(error ?? "recalibrate display layout failed");
        }

        if (string.IsNullOrEmpty(payload))
        {
            return 0;
        }

        int separator = payload.IndexOf('\n');
        string countPart = separator < 0 ? payload : payload[..separator];
        string? sliceJson = separator < 0 ? null : payload[(separator + 1)..];
        if (!string.IsNullOrEmpty(sliceJson))
        {
            _ = _settingsService.ApplyExternalLayout(sliceJson);
        }

        return int.TryParse(
            countPart,
            System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture,
            out int count)
            ? count
            : 0;
    }

    public async Task ApplyScreenBindingAsync(
        string widgetId,
        WidgetScreenBindingMode mode,
        string? boundScreenId,
        bool persistImmediately = true)
    {
        (bool ok, string? payload, string? error) = await _client.RequestAsync(
            SettingsIpcMethods.WidgetApplyScreenBinding,
            SettingsIpcPayload.Encode(
                new SettingsIpcScreenBinding(widgetId, (int)mode, boundScreenId, persistImmediately),
                SettingsIpcJsonContext.Default.SettingsIpcScreenBinding));
        if (!ok)
        {
            throw new InvalidOperationException(error ?? "apply screen binding failed");
        }

        if (!string.IsNullOrEmpty(payload))
        {
            _ = _settingsService.ApplyExternalLayout(payload);
        }
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

    private Task RequestVoidAsync(string method, string? payload = null) =>
        SettingsIpcShell.RequestVoidAsync(_client, method, payload);
}
