using System.Text.Json;
using DeskBox.Contracts;
using DeskBox.Models;
using DeskBox.ViewModels;
using Microsoft.UI.Dispatching;

namespace DeskBox.Services;

/// <summary>
/// Owns Custom Widget JavaScript runtime compilation, lifecycle/UI invocation,
/// and periodic script-tick scheduling so <see cref="CustomWidgetViewModel"/> holds zero timers.
/// </summary>
internal sealed class CustomWidgetScriptRuntimeCoordinator : IDisposable
{
    private readonly CustomWidgetViewModel _vm;
    private DispatcherQueueTimer? _scriptTickTimer;
    private bool _scriptTickCatchUpPending;
    private long _scriptTickExecutionCount;
    private long _lastScriptNotifyUtcTicks;
    private CustomWidgetCompiledScript? _compiledScript;
    private string? _rawLogicJs;
    private CustomWidgetManifest? _cachedI18nManifest;
    private string? _cachedI18nCulture;
    private IReadOnlyDictionary<string, string>? _cachedI18nDict;
    private CustomWidgetManifest? _cachedSecretKeysManifest;
    private HashSet<string>? _cachedSecretKeys;

    internal CustomWidgetScriptRuntimeCoordinator(CustomWidgetViewModel vm) => _vm = vm;

    internal CustomWidgetCompiledScript? CompiledScript => _compiledScript;
    internal string? RawLogicJs
    {
        get => _rawLogicJs;
        set => _rawLogicJs = value;
    }
    internal long ExecutionCount => Interlocked.Read(ref _scriptTickExecutionCount);

    internal void CompileCurrentScript()
    {
        if (string.IsNullOrWhiteSpace(_rawLogicJs))
        {
            _compiledScript = null;
            if (_vm.ManifestInternal.Script is not null)
            {
                SetScriptError(new CustomWidgetScriptRuntimeError(
                    "compile",
                    "manifest.json declares 'script', but logic.js is missing.",
                    0,
                    null,
                    DateTimeOffset.UtcNow.UtcTicks));
            }
            else
            {
                SetScriptError(null);
            }
            return;
        }

        CustomWidgetScriptCompileResult compileResult = CustomWidgetScriptEngine.Compile(_rawLogicJs);
        if (compileResult.IsSuccess && compileResult.Compiled is not null)
        {
            _compiledScript = compileResult.Compiled;
            SetScriptError(null);
        }
        else
        {
            _compiledScript = null;
            SetScriptError(new CustomWidgetScriptRuntimeError(
                "compile",
                compileResult.ErrorMessage ?? "Failed to compile logic.js.",
                compileResult.ErrorLine ?? 0,
                null,
                DateTimeOffset.UtcNow.UtcTicks));
        }
    }

    internal void SetScriptError(CustomWidgetScriptRuntimeError? error)
    {
        if (_vm.LastScriptError is null && error is null)
        {
            return;
        }

        if (_vm.LastScriptError is not null &&
            error is not null &&
            string.Equals(_vm.LastScriptError.FunctionName, error.FunctionName, StringComparison.Ordinal) &&
            string.Equals(_vm.LastScriptError.ErrorMessage, error.ErrorMessage, StringComparison.Ordinal) &&
            _vm.LastScriptError.ErrorLine == error.ErrorLine)
        {
            return;
        }

        _vm.LastScriptError = error;
        _vm.NotifyPropertyChanged(nameof(CustomWidgetViewModel.LastScriptError));
        _vm.NotifyScriptErrorChanged(error);
    }

    internal void TryRunScriptLifecycleHook(string hookName, uint timeoutMs, bool persistIfMutated)
    {
        if (string.Equals(hookName, "onInit", StringComparison.Ordinal))
        {
            _vm.HasDirtyStateSinceLastScriptInitInternal = false;
        }

        if (_compiledScript is null || !_compiledScript.ExportedFunctions.Contains(hookName))
        {
            return;
        }

        bool ok = InvokeScriptCore(
            functionName: hookName,
            arg: null,
            eventValue: null,
            currentItem: null,
            dropFile: null,
            sourceId: null,
            rawSourcePayload: null,
            timeoutMs: timeoutMs,
            requireExport: false,
            allowCopy: false,
            out bool mutatedPersisted,
            out _,
            out _,
            out IReadOnlyList<CustomWidgetActionSpec>? dispatched,
            callKind: CustomWidgetScriptCallKind.Lifecycle);

        if (ok && mutatedPersisted && persistIfMutated && !_vm.IsPreviewOnlyInternal)
        {
            _ = _vm.PersistStoreAsync();
        }
        if (ok && dispatched is { Count: > 0 })
        {
            _ = _vm.ExecuteActionsAsync(dispatched, isNested: true, depth: 1);
        }
    }

    internal bool InvokeScriptCore(
        string functionName,
        string? arg,
        string? eventValue,
        CustomWidgetRecord? currentItem,
        CustomWidgetDropFileContext? dropFile,
        string? sourceId,
        string? rawSourcePayload,
        uint timeoutMs,
        bool requireExport,
        bool allowCopy,
        out bool mutatedPersisted,
        out bool mutatedUi,
        out bool mutatedStructuralUi) =>
        InvokeScriptCore(
            functionName,
            arg,
            eventValue,
            currentItem,
            dropFile,
            sourceId,
            rawSourcePayload,
            timeoutMs,
            requireExport,
            allowCopy,
            out mutatedPersisted,
            out mutatedUi,
            out mutatedStructuralUi,
            out _);

    internal bool InvokeScriptCore(
        string functionName,
        string? arg,
        string? eventValue,
        CustomWidgetRecord? currentItem,
        CustomWidgetDropFileContext? dropFile,
        string? sourceId,
        string? rawSourcePayload,
        uint timeoutMs,
        bool requireExport,
        bool allowCopy,
        out bool mutatedPersisted,
        out bool mutatedUi,
        out bool mutatedStructuralUi,
        out IReadOnlyList<CustomWidgetActionSpec>? dispatchedActions,
        IReadOnlyDictionary<string, string>? args = null,
        string? eventId = null,
        double? eventX = null,
        double? eventY = null,
        int? fromIndex = null,
        int? toIndex = null,
        CustomWidgetScriptCallKind callKind = CustomWidgetScriptCallKind.UiEvent,
        IReadOnlyList<CustomWidgetDropFileContext>? dropFiles = null,
        string? rawJsonPayload = null,
        double? eventNormX = null,
        double? eventNormY = null,
        bool? eventIsPressed = null,
        string? eventKind = null,
        string? eventText = null,
        IReadOnlyList<CustomWidgetDropItemRecord>? eventItems = null,
        IReadOnlyDictionary<string, JsonElement>? argsJson = null)
    {
        mutatedPersisted = false;
        mutatedUi = false;
        mutatedStructuralUi = false;
        dispatchedActions = null;

        if (string.Equals(functionName, "onInit", StringComparison.Ordinal))
        {
            _vm.HasDirtyStateSinceLastScriptInitInternal = false;
        }

        if (_compiledScript is null)
        {
            if (!requireExport)
            {
                return true;
            }

            if (_vm.LastScriptError is null)
            {
                SetScriptError(new CustomWidgetScriptRuntimeError(
                    functionName,
                    "logic.js is missing or failed to compile.",
                    0,
                    arg,
                    DateTimeOffset.UtcNow.UtcTicks));
            }
            return false;
        }

        CustomWidgetManifest currentManifest = _vm.ManifestInternal;
        if (!ReferenceEquals(_cachedSecretKeysManifest, currentManifest))
        {
            HashSet<string>? builtSecrets = null;
            foreach (CustomWidgetSettingDefinition def in currentManifest.Settings)
            {
                if (string.Equals(def.Type, "secret", StringComparison.OrdinalIgnoreCase))
                {
                    builtSecrets ??= new HashSet<string>(StringComparer.Ordinal);
                    builtSecrets.Add(def.Key);
                }
            }
            _cachedSecretKeys = builtSecrets;
            _cachedSecretKeysManifest = currentManifest;
        }

        string activeCulture = _vm.ActiveCultureName;
        if (!ReferenceEquals(_cachedI18nManifest, currentManifest) ||
            !string.Equals(_cachedI18nCulture, activeCulture, StringComparison.Ordinal) ||
            _cachedI18nDict is null)
        {
            _cachedI18nDict = currentManifest.ResolveI18nDictionary(activeCulture);
            _cachedI18nManifest = currentManifest;
            _cachedI18nCulture = activeCulture;
        }

        bool suppressPreviewSideEffects = _vm.IsUntrustedStagePreview || _vm.IsStudioGalleryThumbnailPreview;

        var input = new CustomWidgetScriptExecutionInput
        {
            State = _vm.StateInternal,
            StateJson = _vm.StateJsonInternal,
            Computed = _vm.ComputedInternal,
            Collections = _vm.CollectionsInternal,
            Settings = BuildEffectiveScriptSettings(),
            SecretKeysToExclude = _cachedSecretKeys,
            I18n = _cachedI18nDict,
            Args = args,
            ArgsJson = argsJson,
            Event = new CustomWidgetScriptEventPayload(
                Arg: arg ?? string.Empty,
                Value: eventValue ?? string.Empty,
                Item: currentItem,
                Drop: dropFile,
                Id: eventId ?? string.Empty,
                X: eventX,
                Y: eventY,
                FromIndex: fromIndex,
                ToIndex: toIndex,
                Args: args,
                Files: dropFiles,
                NormX: eventNormX,
                NormY: eventNormY,
                IsPressed: eventIsPressed,
                Kind: eventKind,
                Text: eventText,
                Items: eventItems,
                ArgsJson: argsJson),
            CultureName = activeCulture,
            WidgetId = _vm.Config.Id,
            ClockZones = _vm.ManifestUsesClock() ? BuildClockScriptZones() : null,
            SystemMetrics = _vm.CapabilityHost.SystemMetricsSnapshot,
            DeviceState = _vm.CapabilityHost.DeviceStateSnapshot,
            Weather = _vm.CapabilityHost.WeatherSnapshot,
            Music = _vm.CapabilityHost.MusicSnapshot,
            Todo = _vm.CapabilityHost.TodoSnapshot,
            TodoHasLiveStore = !_vm.IsPreviewOnlyInternal || _vm.CapabilityHost.Todo.HasLiveStore,
            IsPreview = _vm.IsPreviewOnlyInternal,
            IsAmbientPlaying = _vm.IsAmbientAudioPlaying,
            Clipboard = _vm.CapabilityHost.ClipboardSnapshot,
            TransformResult = _vm.LastTransformResultInternal,
            CallKind = callKind,
            SourceId = sourceId,
            RawSourcePayload = rawSourcePayload,
            RawJsonPayload = CustomWidgetScriptEngine.TryParseStructuredJsonElement(rawJsonPayload, out JsonElement parsedRawPayload)
                ? parsedRawPayload
                : null,
            AllowCopy = allowCopy && !suppressPreviewSideEffects,
            TimeoutMs = timeoutMs
        };

        CustomWidgetScriptExecutionResult res = CustomWidgetScriptEngine.Execute(
            _compiledScript,
            functionName,
            input,
            requireExport);

        if (!res.IsSuccess)
        {
            string errMsg = res.ErrorMessage ?? $"Script error in '{functionName}'.";
            _vm.RuntimeTrace.Record("script.run", functionName, ok: false, errMsg);
            SetScriptError(new CustomWidgetScriptRuntimeError(
                functionName,
                errMsg,
                res.ErrorLine ?? 0,
                arg,
                DateTimeOffset.UtcNow.UtcTicks));
            return false;
        }

        if (!res.Invoked)
        {
            return true;
        }

        if (_vm.LastScriptError is not null)
        {
            SetScriptError(null);
        }

        foreach (string removedKey in res.StateRemoved)
        {
            bool removedFlat = _vm.StateInternal.Remove(removedKey);
            bool removedJson = _vm.StateJsonInternal.Remove(removedKey);
            if (removedFlat || removedJson)
            {
                mutatedPersisted = true;
                mutatedUi = true;
            }
        }

        foreach ((string sk, JsonElement el) in res.StateSetJson)
        {
            bool changed = !_vm.StateJsonInternal.TryGetValue(sk, out JsonElement prevJson) ||
                           !JsonElement.DeepEquals(prevJson, el);
            if (changed)
            {
                _vm.SetStructuredStateValue(sk, el);
                mutatedPersisted = true;
                mutatedUi = true;
                if (el.ValueKind is JsonValueKind.Array or JsonValueKind.Object)
                {
                    mutatedStructuralUi = true;
                }
            }
        }

        foreach ((string sk, string sv) in res.StateSet)
        {
            if (res.StateSetJson.ContainsKey(sk))
            {
                continue;
            }

            if (!_vm.StateInternal.TryGetValue(sk, out string? prevVal) ||
                !string.Equals(prevVal, sv, StringComparison.Ordinal))
            {
                _vm.SyncFlatStateValue(sk, sv);
                mutatedPersisted = true;
                mutatedUi = true;
            }
        }

        foreach ((string colName, List<CustomWidgetRecord> items) in res.DirtyCollections)
        {
            int maxItems = _vm.ManifestInternal.Collections.TryGetValue(colName, out CustomWidgetCollectionSchema? schema)
                ? schema.MaxItems
                : CustomWidgetScriptEngine.MaxCollectionItems;
            _vm.CollectionsInternal[colName] = items.Count > maxItems
                ? items.Take(maxItems).ToList()
                : new List<CustomWidgetRecord>(items);
            _vm.BumpCollectionsVersion();
            mutatedPersisted = true;
            mutatedUi = true;
            mutatedStructuralUi = true;
        }

        bool computedChanged = false;
        HashSet<string>? changedComputedKeys = null;

        foreach (string prevKey in _vm.ComputedInternal.Keys)
        {
            if (!res.Computed.ContainsKey(prevKey))
            {
                changedComputedKeys ??= new HashSet<string>(StringComparer.Ordinal);
                changedComputedKeys.Add(prevKey);
                computedChanged = true;
            }
        }

        foreach ((string ck, JsonElement cv) in res.Computed)
        {
            if (!_vm.ComputedInternal.TryGetValue(ck, out JsonElement prevEl) ||
                !JsonElement.DeepEquals(prevEl, cv))
            {
                changedComputedKeys ??= new HashSet<string>(StringComparer.Ordinal);
                changedComputedKeys.Add(ck);
                computedChanged = true;
            }
        }

        if (computedChanged)
        {
            _vm.InvalidateComputedRecordCacheKeys(changedComputedKeys);
            _vm.ComputedInternal.Clear();
            foreach ((string ck, JsonElement cv) in res.Computed)
            {
                _vm.ComputedInternal[ck] = cv;
            }
            _vm.BumpComputedVersion();

            mutatedUi = true;
            if (NodeTreeWhenReferencesComputed(_vm.ManifestInternal.View) ||
                _vm.ManifestInternal.Popovers.Any(NodeTreeWhenReferencesComputed) ||
                NodeTreeTableReferencesComputed(_vm.ManifestInternal.View, changedComputedKeys) ||
                _vm.ManifestInternal.Popovers.Any(p => NodeTreeTableReferencesComputed(p, changedComputedKeys)))
            {
                mutatedStructuralUi = true;
            }
        }

        if (!suppressPreviewSideEffects)
        {
            foreach (CustomWidgetScriptUiOp op in res.UiOps)
            {
                switch (op.Type.Trim().ToLowerInvariant())
                {
                    case "toast":
                    {
                        if (!string.IsNullOrWhiteSpace(op.Message))
                        {
                            _vm.RaiseFeedback(op.Message, ParseScriptToastSeverity(op.Tone));
                        }
                        break;
                    }

                    case "copy":
                    {
                        if (allowCopy && !string.IsNullOrEmpty(op.Text))
                        {
                            CustomWidgetViewModel.CopyTextToClipboard(op.Text);
                        }
                        break;
                    }

                    case "notify":
                    {
                        long nowTicks = DateTimeOffset.UtcNow.UtcTicks;
                        long lastTicks = Interlocked.Read(ref _lastScriptNotifyUtcTicks);
                        if (lastTicks <= 0 || nowTicks - lastTicks >= TimeSpan.FromSeconds(60).Ticks)
                        {
                            Interlocked.Exchange(ref _lastScriptNotifyUtcTicks, nowTicks);
                            string notifyTitle = string.IsNullOrWhiteSpace(op.Title) ? _vm.Config.Name : op.Title.Trim();
                            string notifyBody = string.IsNullOrWhiteSpace(op.Body) ? notifyTitle : op.Body.Trim();
                            _vm.RaiseFeedback(notifyBody, WidgetFeedbackSeverity.Info);
                            _vm.DispatchReminderToastNotification(notifyTitle, notifyBody);
                        }
                        break;
                    }

                    case "refreshsource":
                    {
                        string? srcId = string.IsNullOrWhiteSpace(op.SourceId) ? null : op.SourceId.Trim();
                        _ = _vm.RefreshNetworkSourcesNowAsync(targetSourceId: srcId, isManualUserAction: false);
                        if (_vm.ManifestUsesFileTextSources() &&
                            (srcId is null || _vm.ManifestInternal.Sources.Any(s =>
                                string.Equals(s.Type, "fileText", StringComparison.OrdinalIgnoreCase) &&
                                string.Equals(s.Id, srcId, StringComparison.OrdinalIgnoreCase))))
                        {
                            _ = _vm.RefreshFileTextSourcesNowAsync(targetSourceId: srcId);
                        }
                        if (_vm.ManifestUsesCommandSources() &&
                            (srcId is null || _vm.ManifestInternal.Sources.Any(s =>
                                string.Equals(s.Type, "command", StringComparison.OrdinalIgnoreCase) &&
                                string.Equals(s.Id, srcId, StringComparison.OrdinalIgnoreCase))))
                        {
                            _ = _vm.RefreshCommandSourcesNowAsync(targetSourceId: srcId);
                        }
                        break;
                    }
                }
            }
        }

        dispatchedActions = suppressPreviewSideEffects
            ? FilterSafePreviewDispatchedActions(res.DispatchedActions)
            : res.DispatchedActions;
        return true;
    }

    private static IReadOnlyList<CustomWidgetActionSpec>? FilterSafePreviewDispatchedActions(
        IReadOnlyList<CustomWidgetActionSpec>? actions)
    {
        if (actions is not { Count: > 0 })
        {
            return null;
        }

        List<CustomWidgetActionSpec>? filtered = null;
        for (int i = 0; i < actions.Count; i++)
        {
            CustomWidgetActionSpec act = actions[i];
            if (!IsPurePreviewLocalMutationAction(act.Type))
            {
                continue;
            }

            IReadOnlyList<CustomWidgetActionSpec>? filteredThen = FilterSafePreviewDispatchedActions(act.Then);
            CustomWidgetActionSpec normalized = ReferenceEquals(filteredThen, act.Then)
                ? act
                : act with { Then = filteredThen };
            filtered ??= new List<CustomWidgetActionSpec>(actions.Count);
            filtered.Add(normalized);
        }

        return filtered;
    }

    private static bool IsPurePreviewLocalMutationAction(string? actionType)
    {
        if (string.IsNullOrWhiteSpace(actionType))
        {
            return false;
        }

        string trimmed = actionType.Trim();
        return trimmed.StartsWith("state.", StringComparison.OrdinalIgnoreCase) ||
               trimmed.StartsWith("collection.", StringComparison.OrdinalIgnoreCase) ||
               trimmed.StartsWith("popover.", StringComparison.OrdinalIgnoreCase) ||
               trimmed.StartsWith("tip.", StringComparison.OrdinalIgnoreCase);
    }

    internal static WidgetFeedbackSeverity ParseScriptToastSeverity(string? tone) =>
        tone?.Trim().ToLowerInvariant() switch
        {
            "success" or "ok" => WidgetFeedbackSeverity.Success,
            "warning" or "warn" => WidgetFeedbackSeverity.Warning,
            "error" or "danger" => WidgetFeedbackSeverity.Error,
            _ => WidgetFeedbackSeverity.Info
        };

    internal static bool NodeTreeWhenReferencesComputed(CustomWidgetNodeSpec node) =>
        node.EnumerateAllNodes().Any(static n =>
            !string.IsNullOrEmpty(n.When) &&
            n.When.Contains("computed.", StringComparison.OrdinalIgnoreCase));

    private static bool NodeTreeTableReferencesComputed(
        CustomWidgetNodeSpec node,
        HashSet<string>? changedComputedKeys)
    {
        foreach (CustomWidgetNodeSpec current in node.EnumerateAllNodes())
        {
            if (!string.Equals(current.Type, "Table", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string? src = !string.IsNullOrWhiteSpace(current.Items) ? current.Items : current.Collection;
            if (string.IsNullOrWhiteSpace(src) ||
                !src.Contains("computed.", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (changedComputedKeys is null)
            {
                return true;
            }

            foreach (string key in changedComputedKeys)
            {
                if (src.Contains($"computed.{key}", StringComparison.Ordinal))
                {
                    return true;
                }
            }
        }

        return false;
    }

    internal static bool CapsuleReferencesDynamicScriptState(CustomWidgetCapsuleSpec? capsule) =>
        CustomWidgetViewModel.CapsuleReferencesToken(capsule, "computed.") ||
        CustomWidgetViewModel.CapsuleReferencesToken(capsule, "state.");

    internal int ResolveEffectiveScriptTickIntervalMs()
    {
        int rawTickMs = _vm.ManifestInternal.Script?.TickIntervalMs ?? 0;
        bool hasOnTick = _compiledScript is not null &&
                         _compiledScript.ExportedFunctions.Contains("onTick");
        if (_vm.IsDisposedInternal ||
            _vm.IsStudioGalleryThumbnailPreview ||
            CustomWidgetManifestParser.IsIncompatibleClientVersion(_vm.DiagnosticsInternal) ||
            !hasOnTick ||
            rawTickMs <= 0)
        {
            return 0;
        }

        bool hasBackgroundWork = _vm.HasActiveBackgroundWork;
        if (!_vm.IsWindowVisibleInternal)
        {
            return hasBackgroundWork
                ? Math.Clamp(Math.Max(rawTickMs, 1000), 1000, 3_600_000)
                : 0;
        }

        if (_vm.IsCompactCollapsedInternal)
        {
            bool activeWhileCollapsed = hasBackgroundWork ||
                                        CapsuleReferencesDynamicScriptState(_vm.ManifestInternal.Capsule);
            return activeWhileCollapsed
                ? Math.Clamp(Math.Max(rawTickMs, 1000), 1000, 3_600_000)
                : 0;
        }

        return Math.Clamp(rawTickMs, 1000, 3_600_000);
    }

    internal void UpdateScriptTickSubscription()
    {
        int rawTickMs = _vm.ManifestInternal.Script?.TickIntervalMs ?? 0;
        bool hasOnTick = _compiledScript is not null &&
                         _compiledScript.ExportedFunctions.Contains("onTick");
        bool eligible = !_vm.IsDisposedInternal &&
                        !_vm.IsStudioGalleryThumbnailPreview &&
                        !CustomWidgetManifestParser.IsIncompatibleClientVersion(_vm.DiagnosticsInternal) &&
                        hasOnTick &&
                        rawTickMs > 0;

        int effectiveMs = ResolveEffectiveScriptTickIntervalMs();
        if (effectiveMs <= 0)
        {
            if (eligible && (!_vm.IsWindowVisibleInternal || _vm.IsCompactCollapsedInternal))
            {
                _scriptTickCatchUpPending = true;
            }
            else
            {
                _scriptTickCatchUpPending = false;
            }

            StopScriptTickTimer();
            return;
        }

        if (_scriptTickCatchUpPending && _vm.IsInitializedInternal)
        {
            _scriptTickCatchUpPending = false;
            ExecuteScriptTickNow();
        }

        if (_vm.DispatcherQueueInternal is null)
        {
            StopScriptTickTimer();
            return;
        }

        if (_scriptTickTimer is null)
        {
            _scriptTickTimer = _vm.DispatcherQueueInternal.CreateTimer();
            _scriptTickTimer.IsRepeating = true;
            _scriptTickTimer.Tick += OnScriptTickTimer;
        }

        TimeSpan interval = TimeSpan.FromMilliseconds(effectiveMs);
        if (_scriptTickTimer.Interval != interval)
        {
            _scriptTickTimer.Interval = interval;
        }

        if (!_scriptTickTimer.IsRunning)
        {
            _scriptTickTimer.Start();
        }
    }

    internal void StopScriptTickTimer()
    {
        if (_scriptTickTimer is not null)
        {
            _scriptTickTimer.Stop();
            _scriptTickTimer.Tick -= OnScriptTickTimer;
            _scriptTickTimer = null;
        }
    }

    internal void OnScriptTickTimer(DispatcherQueueTimer sender, object args) =>
        ExecuteScriptTickNow();

    internal void ExecuteScriptTickNow()
    {
        if (_vm.IsDisposedInternal)
        {
            return;
        }

        Interlocked.Increment(ref _scriptTickExecutionCount);
        bool ok = InvokeScriptCore(
            functionName: "onTick",
            arg: null,
            eventValue: null,
            currentItem: null,
            dropFile: null,
            sourceId: null,
            rawSourcePayload: null,
            timeoutMs: CustomWidgetScriptEngine.DefaultActionTimeoutMs,
            requireExport: false,
            allowCopy: false,
            out bool mutatedPersisted,
            out bool mutatedUi,
            out bool mutatedStructuralUi,
            out IReadOnlyList<CustomWidgetActionSpec>? dispatched,
            callKind: CustomWidgetScriptCallKind.Tick);

        if (!ok)
        {
            return;
        }

        if (mutatedPersisted && !_vm.IsPreviewOnlyInternal)
        {
            _vm.ScheduleDebouncedPersist(delayMilliseconds: 2500, coalesceExistingPending: true);
        }

        if (mutatedStructuralUi)
        {
            _vm.RefreshCapsuleProjection(bumpLiveState: mutatedPersisted);
            _vm.NotifyViewRebuildRequested();
        }
        else if (mutatedPersisted || mutatedUi)
        {
            _vm.RefreshCapsuleProjection(bumpLiveState: mutatedPersisted);
            _vm.NotifyMetricsRefreshed();
        }

        if (dispatched is { Count: > 0 })
        {
            _ = _vm.ExecuteActionsAsync(dispatched, isNested: true, depth: 1);
        }
    }


    internal IReadOnlyDictionary<string, string> BuildEffectiveScriptSettings()
    {
        if (_vm.ManifestInternal.Settings.Count == 0 || _vm.SettingsInternal.Count == 0)
        {
            return _vm.SettingsInternal;
        }

        Dictionary<string, string>? expandedDict = null;
        foreach (CustomWidgetSettingDefinition def in _vm.ManifestInternal.Settings)
        {
            if (!_vm.SettingsInternal.TryGetValue(def.Key, out string? rawVal) ||
                string.IsNullOrWhiteSpace(rawVal) ||
                string.Equals(def.DefaultValue, rawVal, StringComparison.Ordinal) ||
                !CustomWidgetOnboardingHelper.IsMultilineMediaOrPathListSetting(def, rawVal))
            {
                continue;
            }

            string[] lines = rawVal.Split(
                ['\r', '\n'],
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            List<string>? rewrittenLines = null;
            for (int li = 0; li < lines.Length; li++)
            {
                string line = lines[li];
                string candidate = line;
                int pipeIdx = candidate.IndexOf('|');
                if (pipeIdx > 0)
                {
                    candidate = candidate[..pipeIdx].Trim();
                }

                if (string.IsNullOrWhiteSpace(candidate) ||
                    candidate.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                    candidate.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                {
                    rewrittenLines?.Add(line);
                    continue;
                }

                candidate = CustomWidgetViewModel.TryUnwrapFileUriToLocalPath(candidate);
                string expandedPath = CustomWidgetOnboardingHelper.ExpandUserProfilePlaceholder(candidate);
                if (!_vm.IsPermissionAllowed(CustomWidgetPermissionKind.LocalFileAccess) ||
                    !Path.IsPathRooted(expandedPath) ||
                    !CustomWidgetLocalFileEngine.TryResolveSafeLocalPath(expandedPath, forWrite: false, out string safeDir, out _) ||
                    !Directory.Exists(safeDir))
                {
                    rewrittenLines?.Add(line);
                    continue;
                }

                if (rewrittenLines is null)
                {
                    rewrittenLines = new List<string>(lines.Length + 16);
                    for (int prev = 0; prev < li; prev++)
                    {
                        rewrittenLines.Add(lines[prev]);
                    }
                }

                IReadOnlyList<string> acceptedExts = CustomWidgetViewModel.ResolveAllowedExtensionsForFolderEnumeration(def.Accept, def);
                var enumOptions = new EnumerationOptions
                {
                    IgnoreInaccessible = true,
                    RecurseSubdirectories = false,
                    ReturnSpecialDirectories = false,
                    AttributesToSkip = FileAttributes.System | FileAttributes.Hidden
                };
                int addedFromFolder = 0;
                try
                {
                    foreach (string entry in Directory.EnumerateFiles(safeDir, "*", enumOptions))
                    {
                        if (acceptedExts.Count > 0)
                        {
                            string ext = Path.GetExtension(entry);
                            if (string.IsNullOrWhiteSpace(ext) ||
                                !acceptedExts.Contains(ext, StringComparer.OrdinalIgnoreCase))
                            {
                                continue;
                            }
                        }

                        rewrittenLines.Add(entry);
                        if (++addedFromFolder >= 120)
                        {
                            break;
                        }
                    }
                }
                catch
                {
                }
            }

            if (rewrittenLines is not null)
            {
                expandedDict ??= new Dictionary<string, string>(_vm.SettingsInternal, StringComparer.Ordinal);
                expandedDict[def.Key] = string.Join("\n", rewrittenLines);
            }
        }

        return expandedDict ?? _vm.SettingsInternal;
    }



    internal IReadOnlyList<string> BuildClockScriptZones()
    {
        var zones = new List<string> { "local", "UTC" };
        var seen = new HashSet<string>(zones, StringComparer.Ordinal);
        IReadOnlyDictionary<string, string> effectiveSettings = BuildEffectiveScriptSettings();
        foreach (CustomWidgetSettingDefinition def in _vm.ManifestInternal.Settings)
        {
            if (def.Key.Contains("city", StringComparison.OrdinalIgnoreCase) ||
                def.Key.Contains("zone", StringComparison.OrdinalIgnoreCase) ||
                def.Key.Contains("tz", StringComparison.OrdinalIgnoreCase))
            {
                if (effectiveSettings.TryGetValue(def.Key, out string? zone) &&
                    !string.IsNullOrWhiteSpace(zone) &&
                    !string.Equals(zone.Trim(), "none", StringComparison.OrdinalIgnoreCase) &&
                    seen.Add(zone.Trim()))
                {
                    zones.Add(zone.Trim());
                }
            }
        }
        return zones;
    }


    public void Dispose() => StopScriptTickTimer();
}
