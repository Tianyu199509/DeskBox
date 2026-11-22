using System.Globalization;
using System.Text.Json;
using DeskBox.Contracts;
using DeskBox.Models;
using DeskBox.ViewModels;

namespace DeskBox.Services;

/// <summary>
/// Manages Custom Widget Studio preview seeding/sampling, manifest &amp; remote catalog reload debouncing,
/// secret completeness queries, calendar stand-in seeding, and auto-localized title syncing.
/// </summary>
internal sealed class CustomWidgetPreviewAndReloadCoordinator : IDisposable
{
    private readonly CustomWidgetViewModel _vm;
    private readonly object _reloadDebounceLock = new();
    private IDisposable? _manifestWatcherSubscription;
    private CancellationTokenSource? _reloadDebounceCts;
    private bool _pendingWorkspaceCodeReload;
    private bool _pendingReloadNotifyFeedback;
    private string? _lastAutoLocalizedTitle;

    internal CustomWidgetPreviewAndReloadCoordinator(CustomWidgetViewModel vm) => _vm = vm;

    internal string? LastAutoLocalizedTitle
    {
        get => _lastAutoLocalizedTitle;
        set => _lastAutoLocalizedTitle = value;
    }

    internal IReadOnlyList<CustomWidgetSettingDefinition> GetUnconfiguredSecretSettings()
    {
        if (_vm.ManifestInternal.Settings.Count == 0)
        {
            return [];
        }

        List<CustomWidgetSettingDefinition>? missing = null;
        foreach (CustomWidgetSettingDefinition def in _vm.ManifestInternal.ResolveLocalizedSettings(_vm.ActiveCultureName))
        {
            if (!string.Equals(def.Type, "secret", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!_vm.SecretsInternal.TryGetValue(def.Key, out string? val) || string.IsNullOrWhiteSpace(val))
            {
                missing ??= [];
                missing.Add(def);
            }
        }

        return missing is null ? [] : missing;
    }


    internal void ApplyPreviewSeedPayload(CustomWidgetStorePayload? payload)
    {
        if (_vm.IsDisposedInternal || !_vm.IsPreviewOnlyInternal || payload is null)
        {
            return;
        }

        _vm.IsApplyingSampleDataInternal = true;
        try
        {
            _vm.HasPreviewSampleDataInternal = false;
            _vm.SampleSeededCollectionsInternal.Clear();
            _vm.UserMutatedCollectionsInternal.Clear();
            _vm.SampleSeededStateKeysInternal.Clear();
            _vm.HasUserStateMutationInternal = false;

            _vm.StateJsonInternal.Clear();
            foreach ((string k, string v) in _vm.ManifestInternal.State)
            {
                _vm.SyncFlatStateValue(k, v);
            }
            foreach ((string k, JsonElement el) in _vm.ManifestInternal.StateJson)
            {
                _vm.StateJsonInternal[k] = el.Clone();
            }
            foreach ((string k, JsonElement v) in payload.StateJson)
            {
                _vm.SetStructuredStateValue(k, v);
            }

            _vm.CollectionsInternal.Clear();
            foreach ((string colName, _) in _vm.ManifestInternal.Collections)
            {
                _vm.CollectionsInternal[colName] = [];
            }
            foreach ((string colName, List<CustomWidgetRecord> items) in payload.Collections)
            {
                var cloned = new List<CustomWidgetRecord>(items.Count);
                foreach (CustomWidgetRecord rec in items)
                {
                    cloned.Add(rec.Clone());
                }
                _vm.CollectionsInternal[colName] = cloned;
            }

            _vm.TryRunScriptLifecycleHook("onInit", CustomWidgetScriptEngine.DefaultInitOrSourceTimeoutMs, persistIfMutated: false);
            _vm.RefreshCapsuleProjection(bumpLiveState: true, forceNotify: true);
            _vm.NotifyViewRebuildRequested();
        }
        finally
        {
            _vm.IsApplyingSampleDataInternal = false;
        }
    }

    /// <summary>
    /// Extracts user-entered records (excluding synthetic sample items) and user-modified state from an interactive studio preview.
    /// </summary>
    internal CustomWidgetStorePayload? BuildUserEnteredStoreSeedPayload()
    {
        if (!_vm.IsPreviewOnlyInternal || !_vm.HasUserRuntimeMutations)
        {
            return null;
        }

        var result = new CustomWidgetStorePayload();
        bool hasAny = false;

        foreach ((string colName, List<CustomWidgetRecord> records) in _vm.CollectionsInternal)
        {
            List<CustomWidgetRecord> realRecords = records
                .Where(static r => !r.Id.StartsWith("sample-", StringComparison.Ordinal))
                .Select(static r => r.Clone())
                .ToList();
            if (realRecords.Count > 0)
            {
                result.Collections[colName] = realRecords;
                hasAny = true;
            }
        }

        if (_vm.HasUserStateMutationInternal)
        {
            foreach ((string k, JsonElement v) in _vm.StateJsonInternal)
            {
                if (_vm.SampleSeededStateKeysInternal.Contains(k))
                {
                    continue;
                }
                result.StateJson[k] = v.Clone();
                hasAny = true;
            }
        }

        return hasAny ? result : null;
    }

    internal bool ApplyPreviewSampleDataJson(string? sampleDataJson, bool seedOnlyIfEmptyOrSample = false)
    {
        if (_vm.IsDisposedInternal || !_vm.IsPreviewOnlyInternal || string.IsNullOrWhiteSpace(sampleDataJson))
        {
            return false;
        }

        _vm.AllowPreviewHostStandIns = true;
        _vm.IsApplyingSampleDataInternal = true;
        try
        {
            using JsonDocument doc = JsonDocument.Parse(
                sampleDataJson,
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = true,
                    CommentHandling = JsonCommentHandling.Skip
                });
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            bool appliedAny = false;
            JsonElement root = doc.RootElement;

            if (root.TryGetProperty("state", out JsonElement stateObj) &&
                stateObj.ValueKind == JsonValueKind.Object)
            {
                foreach (JsonProperty prop in stateObj.EnumerateObject())
                {
                    if (!string.IsNullOrEmpty(prop.Name))
                    {
                        if (seedOnlyIfEmptyOrSample &&
                            (_vm.StateInternal.ContainsKey(prop.Name) || _vm.StateJsonInternal.ContainsKey(prop.Name)) &&
                            !_vm.SampleSeededStateKeysInternal.Contains(prop.Name))
                        {
                            continue;
                        }

                        _vm.SetStructuredStateValue(prop.Name, prop.Value);
                        _vm.SampleSeededStateKeysInternal.Add(prop.Name);
                        appliedAny = true;
                    }
                }
            }

            JsonElement collectionsRoot = root;
            if (root.TryGetProperty("collections", out JsonElement colsObj) &&
                colsObj.ValueKind == JsonValueKind.Object)
            {
                collectionsRoot = colsObj;
            }

            foreach (JsonProperty colProp in collectionsRoot.EnumerateObject())
            {
                if (string.Equals(colProp.Name, "state", StringComparison.Ordinal) ||
                    colProp.Value.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                if (seedOnlyIfEmptyOrSample &&
                    (_vm.UserMutatedCollectionsInternal.Contains(colProp.Name) ||
                     (_vm.CollectionsInternal.TryGetValue(colProp.Name, out List<CustomWidgetRecord>? existing) &&
                      existing.Count > 0 &&
                      existing.Any(static r => !r.Id.StartsWith("sample-", StringComparison.Ordinal) ||
                                               r.UpdatedAtUtcTicks > r.CreatedAtUtcTicks))))
                {
                    continue;
                }

                var records = new List<CustomWidgetRecord>();
                int idx = 0;
                foreach (JsonElement itemEl in colProp.Value.EnumerateArray())
                {
                    if (itemEl.ValueKind != JsonValueKind.Object)
                    {
                        continue;
                    }

                    DateTimeOffset createdUtc = DateTimeOffset.UtcNow.AddDays(-idx);
                    var rec = new CustomWidgetRecord
                    {
                        Id = $"sample-{idx + 1}"
                    };
                    foreach (JsonProperty fieldProp in itemEl.EnumerateObject())
                    {
                        if (!string.IsNullOrEmpty(fieldProp.Name) && fieldProp.Value.ValueKind != JsonValueKind.Undefined)
                        {
                            rec.FieldsJson[fieldProp.Name] = fieldProp.Value.Clone();
                        }

                        string strVal = CustomWidgetViewModel.JsonScalarToString(fieldProp.Value);
                        if (CustomWidgetSampleDataHydrator.IsSampleDateFieldName(fieldProp.Name) &&
                            DateTimeOffset.TryParse(strVal, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out DateTimeOffset parsedDate))
                        {
                            createdUtc = parsedDate.ToUniversalTime();
                        }
                    }

                    rec.CreatedAtUtcTicks = createdUtc.UtcTicks;
                    rec.UpdatedAtUtcTicks = createdUtc.UtcTicks;
                    records.Add(rec);
                    idx++;
                }

                if (records.Count > 0)
                {
                    _vm.CollectionsInternal[colProp.Name] = records;
                    _vm.SampleSeededCollectionsInternal.Add(colProp.Name);
                    _vm.UserMutatedCollectionsInternal.Remove(colProp.Name);
                    appliedAny = true;
                }
            }

            if (appliedAny)
            {
                bool explicitSampleSourceStatus =
                    root.TryGetProperty("state", out JsonElement explicitStateEl) &&
                    explicitStateEl.ValueKind == JsonValueKind.Object &&
                    explicitStateEl.TryGetProperty("sourceStatus", out _);
                if (!explicitSampleSourceStatus &&
                    _vm.SampleSeededCollectionsInternal.Count > 0 &&
                    _vm.StateInternal.TryGetValue("sourceStatus", out string? curSrcStatus) &&
                    string.Equals(curSrcStatus, "loading", StringComparison.OrdinalIgnoreCase))
                {
                    _vm.SyncFlatStateValue("sourceStatus", "ok");
                    _vm.SampleSeededStateKeysInternal.Add("sourceStatus");
                }

                CustomWidgetSampleDataHydrator.RebaseStaleSampleRecordDatesInPlace(_vm.CollectionsInternal);
                _vm.BumpCollectionsVersion();
                _vm.HasPreviewSampleDataInternal = true;
                _vm.TryRunScriptLifecycleHook("onInit", CustomWidgetScriptEngine.DefaultInitOrSourceTimeoutMs, persistIfMutated: false);
                _vm.RefreshCapsuleProjection(bumpLiveState: true, forceNotify: true);
                _vm.NotifyViewRebuildRequested();
            }

            return appliedAny;
        }
        catch
        {
            return false;
        }
        finally
        {
            _vm.IsApplyingSampleDataInternal = false;
        }
    }

    internal void ClearPreviewSampleData()
    {
        foreach (string colName in _vm.SampleSeededCollectionsInternal.ToArray())
        {
            if (!_vm.UserMutatedCollectionsInternal.Contains(colName) &&
                _vm.CollectionsInternal.TryGetValue(colName, out List<CustomWidgetRecord>? list))
            {
                list.RemoveAll(static r => r.Id.StartsWith("sample-", StringComparison.Ordinal));
                _vm.BumpCollectionsVersion();
            }
        }

        if (!_vm.HasUserStateMutationInternal)
        {
            foreach (string stateKey in _vm.SampleSeededStateKeysInternal.ToArray())
            {
                if (_vm.ManifestInternal.StateJson.TryGetValue(stateKey, out JsonElement defaultEl))
                {
                    _vm.SetStructuredStateValue(stateKey, defaultEl);
                }
                else if (_vm.ManifestInternal.State.TryGetValue(stateKey, out string? defaultFlat))
                {
                    _vm.SyncFlatStateValue(stateKey, defaultFlat);
                    _vm.StateJsonInternal.Remove(stateKey);
                }
                else
                {
                    _vm.StateInternal.Remove(stateKey);
                    _vm.StateJsonInternal.Remove(stateKey);
                }
            }
        }

        _vm.HasPreviewSampleDataInternal = false;
        _vm.AllowPreviewHostStandIns = false;
        _vm.SampleSeededCollectionsInternal.Clear();
        _vm.UserMutatedCollectionsInternal.Clear();
        _vm.SampleSeededStateKeysInternal.Clear();
        _vm.HasUserStateMutationInternal = false;

        if (_vm.IsPreviewOnlyInternal)
        {
            _vm.CapabilityHost.Clipboard.ClearStandInIfActive();
            _vm.CapabilityHost.Todo.ClearStandInIfActive();

            _vm.TryRunScriptLifecycleHook("onInit", CustomWidgetScriptEngine.DefaultInitOrSourceTimeoutMs, persistIfMutated: false);
            _vm.RefreshCapsuleProjection(bumpLiveState: true, forceNotify: true);
            _vm.NotifyViewRebuildRequested();
        }
    }


    internal void SeedCalendarStandInIfEmpty()
    {
        if (!_vm.IsPreviewOnlyInternal || _vm.IsStudioGalleryThumbnailPreview || !_vm.AllowPreviewHostStandIns || _vm.IsDisposedInternal)
        {
            return;
        }

        var calendarNodes = new List<CustomWidgetNodeSpec>();
        CollectCalendarGridNodes(_vm.ManifestInternal.View, calendarNodes);
        foreach (CustomWidgetNodeSpec popRoot in _vm.ManifestInternal.Popovers)
        {
            CollectCalendarGridNodes(popRoot, calendarNodes);
        }

        if (calendarNodes.Count == 0)
        {
            return;
        }

        bool isZh = _vm.ActiveCultureName.StartsWith("zh", StringComparison.OrdinalIgnoreCase);
        DateOnly today = DateOnly.FromDateTime(DateTime.Now);
        DateOnly tomorrow = today.AddDays(1);

        foreach (CustomWidgetNodeSpec calSpec in calendarNodes)
        {
            string rawCol = (calSpec.Collection ?? calSpec.Items ?? string.Empty).Trim();
            if (rawCol.StartsWith("collections.", StringComparison.OrdinalIgnoreCase))
            {
                rawCol = rawCol["collections.".Length..].Trim();
            }
            if (rawCol.Contains("${", StringComparison.Ordinal))
            {
                continue;
            }

            string colName = string.IsNullOrWhiteSpace(rawCol) ? "events" : rawCol;
            if (_vm.UserMutatedCollectionsInternal.Contains(colName) ||
                (_vm.CollectionsInternal.TryGetValue(colName, out List<CustomWidgetRecord>? existing) && existing.Count > 0))
            {
                continue;
            }

            string dateField = string.IsNullOrWhiteSpace(calSpec.DateField) ? "date" : calSpec.DateField.Trim();
            long nowTicks = DateTimeOffset.UtcNow.UtcTicks;
            _vm.CollectionsInternal[colName] =
            [
                new CustomWidgetRecord
                {
                    Id = "sample-cal-1",
                    CreatedAtUtcTicks = nowTicks,
                    UpdatedAtUtcTicks = nowTicks,
                    Fields = new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        [dateField] = today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                        ["title"] = isZh ? "示例日程 · 设计评审" : "Sample event · Design sync",
                        ["time"] = "10:00"
                    }
                },
                new CustomWidgetRecord
                {
                    Id = "sample-cal-2",
                    CreatedAtUtcTicks = nowTicks,
                    UpdatedAtUtcTicks = nowTicks,
                    Fields = new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        [dateField] = tomorrow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                        ["title"] = isZh ? "示例日程 · 周会同步" : "Sample event · Weekly check-in",
                        ["time"] = "15:30"
                    }
                }
            ];
            _vm.SampleSeededCollectionsInternal.Add(colName);
        }
    }

    internal static void CollectCalendarGridNodes(CustomWidgetNodeSpec node, List<CustomWidgetNodeSpec> output)
    {
        if (string.Equals(node.Type, "CalendarGrid", StringComparison.OrdinalIgnoreCase))
        {
            output.Add(node);
        }

        if (node.ItemTemplate is not null)
        {
            CollectCalendarGridNodes(node.ItemTemplate, output);
        }

        for (int i = 0; i < node.Children.Count; i++)
        {
            CollectCalendarGridNodes(node.Children[i], output);
        }
    }


    internal void StartManifestWatcher()
    {
        if (_vm.IsDisposedInternal)
        {
            return;
        }

        _vm.Store.StoreMutated -= OnStoreMutated;
        _vm.Store.StoreMutated += OnStoreMutated;
        _manifestWatcherSubscription ??= _vm.Store.RegisterManifestWatcher(OnManifestFileChanged);
        if (!_vm.IsPreviewOnlyInternal && _vm.RemoteCatalogServiceInternal is not null)
        {
            _vm.RemoteCatalogServiceInternal.CatalogUpdated -= OnRemoteCatalogUpdated;
            _vm.RemoteCatalogServiceInternal.CatalogUpdated += OnRemoteCatalogUpdated;
        }
    }

    internal void OnRemoteCatalogUpdated(object? sender, EventArgs e)
    {
        if (_vm.IsDisposedInternal || !_vm.IsInitializedInternal || _vm.IsPreviewOnlyInternal || _vm.RemoteCatalogServiceInternal is null)
        {
            return;
        }

        _ = ApplyPendingRemoteCatalogUpgradeAsync();
    }

    internal async Task<bool> ApplyPendingRemoteCatalogUpgradeAsync()
    {
        if (_vm.IsDisposedInternal || !_vm.IsInitializedInternal || _vm.IsPreviewOnlyInternal || _vm.RemoteCatalogServiceInternal is null)
        {
            return false;
        }

        try
        {
            string? existingJson = await _vm.Store.TryReadExistingManifestJsonAsync().ConfigureAwait(false);
            if (_vm.IsDisposedInternal ||
                string.IsNullOrWhiteSpace(existingJson) ||
                !CustomWidgetManifestMetadataStamper.ShouldAllowAutomaticOfficialUpgrade(existingJson) ||
                !_vm.RemoteCatalogServiceInternal.TryResolveNewerOfficialBundle(existingJson, out _))
            {
                return false;
            }

            if (_vm.DispatcherQueueInternal is not null)
            {
                var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                if (!_vm.DispatcherQueueInternal.TryEnqueue(async () =>
                {
                    try
                    {
                        if (_vm.IsDisposedInternal)
                        {
                            tcs.TrySetResult(false);
                            return;
                        }

                        await _vm.ReloadManifestFromDiskAsync(notifyFeedback: false);
                        tcs.TrySetResult(true);
                    }
                    catch (Exception ex)
                    {
                        tcs.TrySetException(ex);
                    }
                }))
                {
                    return false;
                }

                return await tcs.Task.ConfigureAwait(false);
            }

            await _vm.ReloadManifestFromDiskAsync(notifyFeedback: false).ConfigureAwait(false);
            return true;
        }
        catch
        {
            return false;
        }
    }

    internal void OnStoreMutated(object? origin, bool isManifestChange)
    {
        if (_vm.IsDisposedInternal || !_vm.IsInitializedInternal || ReferenceEquals(origin, _vm))
        {
            return;
        }

        ScheduleDebouncedReload(
            isWorkspaceCodeChange: isManifestChange,
            notifyFeedback: isManifestChange,
            delayMs: 40);
    }

    internal void OnManifestFileChanged(FileSystemEventArgs e)
    {
        if (_vm.IsDisposedInternal || !_vm.IsInitializedInternal)
        {
            return;
        }

        string fileName = Path.GetFileName(e.FullPath);
        string? parentDirName = Path.GetFileName(Path.GetDirectoryName(e.FullPath));
        bool isLocaleFileChange =
            string.Equals(parentDirName, CustomWidgetDataStore.LocalesDirectoryName, StringComparison.OrdinalIgnoreCase) &&
            fileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase);
        bool isWorkspaceCodeChange =
            string.Equals(fileName, "manifest.json", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(fileName, "logic.js", StringComparison.OrdinalIgnoreCase) ||
            isLocaleFileChange;
        bool isStoreChange = string.Equals(fileName, "store.json", StringComparison.OrdinalIgnoreCase);
        if (!isWorkspaceCodeChange && !isStoreChange)
        {
            return;
        }

        if (isWorkspaceCodeChange && File.Exists(e.FullPath))
        {
            long initTicks = _vm.InitializedWorkspaceWriteTicks;
            if (initTicks > 0 && File.GetLastWriteTimeUtc(e.FullPath).Ticks <= initTicks)
            {
                return;
            }
        }

        if (isStoreChange)
        {
            string lastSavedHash = _vm.Store.LastSavedStoreContentHash;
            if (!string.IsNullOrEmpty(lastSavedHash) &&
                string.Equals(_vm.Store.ReadCurrentStoreContentHash(), lastSavedHash, StringComparison.Ordinal))
            {
                return;
            }
        }

        ScheduleDebouncedReload(
            isWorkspaceCodeChange: isWorkspaceCodeChange,
            notifyFeedback: isWorkspaceCodeChange,
            delayMs: isWorkspaceCodeChange ? 150 : 120);
    }

    internal void ScheduleDebouncedReload(bool isWorkspaceCodeChange, bool notifyFeedback, int delayMs)
    {
        CancellationTokenSource cts;
        lock (_reloadDebounceLock)
        {
            if (_vm.IsDisposedInternal)
            {
                return;
            }

            _pendingWorkspaceCodeReload |= isWorkspaceCodeChange;
            _pendingReloadNotifyFeedback |= notifyFeedback;
            if (_reloadDebounceCts is { } previousCts)
            {
                _reloadDebounceCts = null;
                try
                {
                    previousCts.Cancel();
                }
                catch (ObjectDisposedException)
                {
                }
                previousCts.Dispose();
            }

            cts = new CancellationTokenSource();
            _reloadDebounceCts = cts;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(delayMs, cts.Token);
                bool shouldReloadWorkspaceCode;
                bool shouldNotifyFeedback;
                lock (_reloadDebounceLock)
                {
                    if (_vm.IsDisposedInternal || cts.IsCancellationRequested || !ReferenceEquals(_reloadDebounceCts, cts))
                    {
                        return;
                    }

                    shouldReloadWorkspaceCode = _pendingWorkspaceCodeReload;
                    shouldNotifyFeedback = _pendingReloadNotifyFeedback;
                    _pendingWorkspaceCodeReload = false;
                    _pendingReloadNotifyFeedback = false;
                }

                if (!shouldReloadWorkspaceCode)
                {
                    string lastSavedHash = _vm.Store.LastSavedStoreContentHash;
                    if (!string.IsNullOrEmpty(lastSavedHash) &&
                        string.Equals(_vm.Store.ReadCurrentStoreContentHash(), lastSavedHash, StringComparison.Ordinal))
                    {
                        return;
                    }

                    if (_vm.DispatcherQueueInternal is not null)
                    {
                        _vm.DispatcherQueueInternal.TryEnqueue(() =>
                        {
                            if (!_vm.IsDisposedInternal)
                            {
                                _ = _vm.ReloadStoreOnlyFromDiskAsync();
                            }
                        });
                    }
                    else
                    {
                        await _vm.ReloadStoreOnlyFromDiskAsync();
                    }
                    return;
                }

                if (_vm.DispatcherQueueInternal is not null)
                {
                    _vm.DispatcherQueueInternal.TryEnqueue(() =>
                    {
                        if (!_vm.IsDisposedInternal)
                        {
                            _ = _vm.ReloadManifestFromDiskAsync(notifyFeedback: shouldNotifyFeedback);
                        }
                    });
                }
                else
                {
                    await _vm.ReloadManifestFromDiskAsync(notifyFeedback: shouldNotifyFeedback);
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (ObjectDisposedException)
            {
            }
        });
    }


    internal void OnLanguageChanged()
    {
        if (_vm.IsDisposedInternal)
        {
            return;
        }

        void ApplyLanguageChangeOnUi()
        {
            if (_vm.IsDisposedInternal)
            {
                return;
            }

            if (!_vm.IsPreviewOnlyInternal)
            {
                _vm.LoadedLocalesInternal = _vm.Store.ReadLazyRuntimeLocales(_vm.ActiveCultureName);
                _vm.ManifestInternal = _vm.ManifestInternal.WithLocales(_vm.LoadedLocalesInternal);
            }
            else if (!string.IsNullOrWhiteSpace(_vm.Store.LocalesDirectoryPath) &&
                     Directory.Exists(_vm.Store.LocalesDirectoryPath))
            {
                CustomWidgetLocaleCatalog reloadedLocales = _vm.Store.ReadLazyRuntimeLocales(_vm.ActiveCultureName);
                if (reloadedLocales.HasAnyLocale)
                {
                    _vm.LoadedLocalesInternal = reloadedLocales;
                    _vm.ManifestInternal = _vm.ManifestInternal.WithLocales(reloadedLocales);
                }
            }

            TrySyncAutoLocalizedTitle(notifyIdentityChange: true);
            _vm.TryRunScriptLifecycleHook(
                "onInit",
                CustomWidgetScriptEngine.DefaultInitOrSourceTimeoutMs,
                persistIfMutated: false);
            if (_vm.CompiledScriptInternal?.ExportedFunctions.Contains("onTick") == true)
            {
                _vm.TryRunScriptLifecycleHook(
                    "onTick",
                    CustomWidgetScriptEngine.DefaultActionTimeoutMs,
                    persistIfMutated: false);
            }
            if (_vm.ManifestUsesNetworkSources() &&
                _vm.CompiledScriptInternal?.ExportedFunctions.Contains("onSourceData") == true)
            {
                _vm.HydrateNetworkSourcesFromDiskCache(overwriteExistingCollection: false);
            }
            if (_vm.ManifestUsesTodo() && !_vm.IsStudioGalleryThumbnailPreview)
            {
                _ = _vm.CapabilityHost.Todo.RefreshTodoNowAsync(triggerRebuild: _vm.IsInitializedInternal);
            }
            if (_vm.CapabilityHost.AiMonitor.IsSubscribed)
            {
                _ = CustomWidgetAiMonitorSource.Shared.RefreshNowAsync(forceCloudRefresh: false);
            }
            if (_vm.ManifestUsesWeather() && !_vm.IsStudioGalleryThumbnailPreview)
            {
                _ = _vm.RefreshWeatherNowAsync(forceRefresh: false, isManualUserAction: false);
            }
            _vm.RefreshCapsuleProjection(bumpLiveState: true, forceNotify: true);
            if (_vm.IsInitializedInternal)
            {
                _vm.NotifyViewRebuildRequested();
            }
        }

        if (_vm.DispatcherQueueInternal is not null && !_vm.DispatcherQueueInternal.HasThreadAccess)
        {
            _vm.DispatcherQueueInternal.TryEnqueue(ApplyLanguageChangeOnUi);
        }
        else
        {
            ApplyLanguageChangeOnUi();
        }
    }

    internal void TrySyncAutoLocalizedTitle(bool notifyIdentityChange)
    {
        string culture = _vm.ActiveCultureName;
        CustomWidgetPresetInfo? matchedPreset = CustomWidgetTemplates.MatchPresetOrNull(_vm.ManifestInternal.Id);
        string targetTitle = _vm.ManifestInternal.ResolveLocalizedName(culture);
        if (string.IsNullOrWhiteSpace(targetTitle) && matchedPreset is not null)
        {
            targetTitle = _vm.LocalizationServiceInternal.T(matchedPreset.NameKey);
        }

        if (string.IsNullOrWhiteSpace(targetTitle) ||
            string.Equals(targetTitle, _vm.Config.Name, StringComparison.Ordinal))
        {
            if (!string.IsNullOrWhiteSpace(targetTitle))
            {
                _lastAutoLocalizedTitle = targetTitle;
            }
            return;
        }

        if (!IsKnownAutoTitle(_vm.Config.Name, matchedPreset))
        {
            return;
        }

        _vm.Config.Name = targetTitle;
        _lastAutoLocalizedTitle = targetTitle;
        if (notifyIdentityChange)
        {
            _vm.NotifyManifestIdentityChanged(targetTitle, _vm.ManifestInternal.Icon);
        }
    }

    internal bool IsKnownAutoTitle(string? currentName, CustomWidgetPresetInfo? matchedPreset) =>
        CustomWidgetTemplates.IsAutoLocalizedTitle(
            currentName,
            matchedPreset,
            _vm.ManifestInternal,
            _lastAutoLocalizedTitle);


    public void Dispose()
    {
        lock (_reloadDebounceLock)
        {
            _pendingWorkspaceCodeReload = false;
            _pendingReloadNotifyFeedback = false;
            if (_reloadDebounceCts is { } cts)
            {
                _reloadDebounceCts = null;
                try
                {
                    cts.Cancel();
                }
                catch (ObjectDisposedException)
                {
                }
                cts.Dispose();
            }
        }

        if (_vm.RemoteCatalogServiceInternal is not null)
        {
            _vm.RemoteCatalogServiceInternal.CatalogUpdated -= OnRemoteCatalogUpdated;
        }
        _vm.Store.StoreMutated -= OnStoreMutated;
        _manifestWatcherSubscription?.Dispose();
        _manifestWatcherSubscription = null;
    }
}
