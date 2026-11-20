using System.Globalization;
using System.Text.Json;
using DeskBox.Contracts;
using DeskBox.Models;
using DeskBox.ViewModels;
using Launcher = Windows.System.Launcher;

namespace DeskBox.Services;

/// <summary>
/// Dispatches declarative Custom Widget action chains, live evaluation context refreshes,
/// HTTP interactive requests, and inline collection edits/reordering.
/// </summary>
internal sealed class CustomWidgetActionDispatcher
{
    private readonly CustomWidgetViewModel _vm;

    internal CustomWidgetRuntimeTraceBuffer RuntimeTrace { get; } = new();

    internal CustomWidgetActionDispatcher(CustomWidgetViewModel vm) => _vm = vm;

    internal const int MaxActionExecutionDepth = 64;

    internal sealed class ActionBatchTracker
    {
        public bool MutatedData;
        public bool RanScript;
        public bool MutatedComputed;
    }

    internal async Task ExecuteActionsAsync(
        IReadOnlyList<CustomWidgetActionSpec> actions,
        CustomWidgetEvaluationContext? context = null,
        bool isNested = false,
        int depth = 0)
    {
        try
        {
            await ExecuteActionsCoreAsync(actions, context, isNested, depth);
        }
        catch (Exception ex)
        {
            _vm.RaiseFeedback(ex.Message, WidgetFeedbackSeverity.Warning);
        }
    }

    internal async Task ExecuteActionsCoreAsync(
        IReadOnlyList<CustomWidgetActionSpec> actions,
        CustomWidgetEvaluationContext? context,
        bool isNested,
        int depth,
        ActionBatchTracker? batchTracker = null)
    {
        if (actions.Count == 0 || _vm.IsDisposedInternal || depth > MaxActionExecutionDepth)
        {
            return;
        }

        batchTracker ??= new ActionBatchTracker();

        long actionBatchStartTicks = DateTimeOffset.UtcNow.UtcTicks;
        Dictionary<int, string>? networkKeysBefore = null;
        if (!isNested && _vm.ManifestUsesNetworkSources())
        {
            networkKeysBefore = new Dictionary<int, string>();
            for (int i = 0; i < _vm.ManifestInternal.Sources.Count; i++)
            {
                CustomWidgetSourceSpec src = _vm.ManifestInternal.Sources[i];
                if (string.Equals(src.Type, "rss", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(src.Type, "httpJson", StringComparison.OrdinalIgnoreCase))
                {
                    networkKeysBefore[i] = _vm.BuildRuntimeNetworkSourceKey(src);
                }
            }
        }

        string? weatherCityBefore = !isNested && _vm.ManifestUsesWeather()
            ? _vm.ResolveEffectiveWeatherCity()
            : null;

        if (_vm.HasDirtyStateSinceLastScriptInitInternal && _vm.HasScriptCodeInternal)
        {
            _vm.TryRunScriptLifecycleHook(
                "onInit",
                CustomWidgetScriptEngine.DefaultInitOrSourceTimeoutMs,
                persistIfMutated: false);
        }

        CustomWidgetEvaluationContext evalContext = RefreshLiveEvaluationContext(context);
        bool needsSave = false;
        bool needsRebuild = false;
        bool needsLiveRefreshOnly = false;

        foreach (CustomWidgetActionSpec action in actions)
        {
            if (!string.IsNullOrWhiteSpace(action.Guard) &&
                !CustomWidgetExpressionEvaluator.EvaluateCondition(action.Guard, evalContext))
            {
                continue;
            }

            bool skipThen = false;
            string actionType = action.Type.Trim();
            switch (actionType.ToLowerInvariant())
            {
                case "state.set":
                {
                    if (!string.IsNullOrWhiteSpace(action.Key))
                    {
                        string key = CustomWidgetViewModel.NormalizeStateKey(action.Key);
                        if (action.RawJsonValue is { } rawJson &&
                            rawJson.ValueKind is not (JsonValueKind.String or JsonValueKind.Undefined))
                        {
                            _vm.SetStructuredStateValue(key, rawJson);
                        }
                        else
                        {
                            string val = CustomWidgetExpressionEvaluator.Interpolate(action.Value, evalContext);
                            _vm.SyncFlatStateValue(key, val);
                        }

                        if (_vm.IsPreviewOnlyInternal && !_vm.IsApplyingSampleDataInternal)
                        {
                            _vm.SampleSeededStateKeysInternal.Remove(key);
                            _vm.HasUserStateMutationInternal = true;
                        }
                        if (string.Equals(key, "soundVolume", StringComparison.OrdinalIgnoreCase) &&
                            _vm.ManifestUsesPomodoro())
                        {
                            _vm.UpdatePomodoroTimerSubscription();
                        }
                        batchTracker.MutatedData = true;
                        needsSave = true;
                        needsRebuild = true;
                    }
                    break;
                }

                case "settings.set":
                {
                    if (!string.IsNullOrWhiteSpace(action.Key))
                    {
                        string key = CustomWidgetViewModel.NormalizeSettingKey(action.Key);
                        if (_vm.ManifestInternal.Settings.Any(d => string.Equals(d.Key, key, StringComparison.Ordinal)))
                        {
                            string val = CustomWidgetExpressionEvaluator.Interpolate(action.Value, evalContext);
                            await _vm.SetSettingValueAsync(key, val);
                        }
                    }
                    break;
                }

                case "state.toggle":
                {
                    if (!string.IsNullOrWhiteSpace(action.Key))
                    {
                        string key = CustomWidgetViewModel.NormalizeStateKey(action.Key);
                        bool current = _vm.StateInternal.TryGetValue(key, out string? v) &&
                                       CustomWidgetExpressionEvaluator.IsTruthy(v);
                        bool next = !current;
                        _vm.StateInternal[key] = next ? "true" : "false";
                        _vm.StateJsonInternal[key] = CustomWidgetScriptEngine.CreateBooleanJsonElement(next);
                        if (_vm.IsPreviewOnlyInternal && !_vm.IsApplyingSampleDataInternal)
                        {
                            _vm.SampleSeededStateKeysInternal.Remove(key);
                            _vm.HasUserStateMutationInternal = true;
                        }
                        if (string.Equals(key, "soundVolume", StringComparison.OrdinalIgnoreCase) &&
                            _vm.ManifestUsesPomodoro())
                        {
                            _vm.UpdatePomodoroTimerSubscription();
                        }
                        batchTracker.MutatedData = true;
                        needsSave = true;
                        needsRebuild = true;
                    }
                    break;
                }

                case "state.increment":
                {
                    if (!string.IsNullOrWhiteSpace(action.Key))
                    {
                        string key = CustomWidgetViewModel.NormalizeStateKey(action.Key);
                        double current = _vm.StateInternal.TryGetValue(key, out string? v) &&
                                         double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out double c)
                            ? c
                            : 0;
                        double delta = double.TryParse(
                            CustomWidgetExpressionEvaluator.Interpolate(action.Value ?? "1", evalContext),
                            NumberStyles.Float,
                            CultureInfo.InvariantCulture,
                            out double d)
                            ? d
                            : 1;
                        double nextNum = current + delta;
                        _vm.StateInternal[key] = nextNum.ToString(CultureInfo.InvariantCulture);
                        _vm.StateJsonInternal[key] = CustomWidgetScriptEngine.CreateNumberJsonElement(nextNum);
                        if (_vm.IsPreviewOnlyInternal && !_vm.IsApplyingSampleDataInternal)
                        {
                            _vm.SampleSeededStateKeysInternal.Remove(key);
                            _vm.HasUserStateMutationInternal = true;
                        }
                        if (string.Equals(key, "soundVolume", StringComparison.OrdinalIgnoreCase) &&
                            _vm.ManifestUsesPomodoro())
                        {
                            _vm.UpdatePomodoroTimerSubscription();
                        }
                        batchTracker.MutatedData = true;
                        needsSave = true;
                        needsRebuild = true;
                    }
                    break;
                }

                case "collection.prepend":
                case "collection.append":
                {
                    string colName = action.Collection ?? string.Empty;
                    if (!string.IsNullOrWhiteSpace(colName))
                    {
                        var record = new CustomWidgetRecord
                        {
                            UpdatedAtUtcTicks = DateTimeOffset.UtcNow.UtcTicks
                        };
                        bool hasBlankPrimaryDraftField = false;
                        if (action.ItemFields is not null)
                        {
                            foreach ((string fk, string fv) in action.ItemFields)
                            {
                                string interpolated = ApplyEvaluatedItemField(record, fk, fv, action.ItemFieldsJson, evalContext);

                                string trimmedExpr = fv?.Trim() ?? string.Empty;
                                if (trimmedExpr.StartsWith("${state.", StringComparison.OrdinalIgnoreCase) &&
                                    trimmedExpr.EndsWith('}') &&
                                    trimmedExpr.IndexOf('}', 8) == trimmedExpr.Length - 1)
                                {
                                    string stateKey = trimmedExpr[8..^1].Trim();
                                    bool isPrimaryTextProp =
                                        string.Equals(fk, "title", StringComparison.OrdinalIgnoreCase) ||
                                        string.Equals(fk, "text", StringComparison.OrdinalIgnoreCase) ||
                                        string.Equals(fk, "name", StringComparison.OrdinalIgnoreCase) ||
                                        string.Equals(fk, "content", StringComparison.OrdinalIgnoreCase) ||
                                        string.Equals(fk, "label", StringComparison.OrdinalIgnoreCase) ||
                                        string.Equals(fk, "note", StringComparison.OrdinalIgnoreCase);

                                    if (isPrimaryTextProp && string.IsNullOrWhiteSpace(interpolated))
                                    {
                                        hasBlankPrimaryDraftField = true;
                                    }
                                }
                            }
                        }

                        if (action.ItemFieldsJson is not null)
                        {
                            foreach ((string fk, JsonElement rawEl) in action.ItemFieldsJson)
                            {
                                if (action.ItemFields is not null && action.ItemFields.ContainsKey(fk))
                                {
                                    continue;
                                }

                                string rawScalar = rawEl.ValueKind == JsonValueKind.String
                                    ? (rawEl.GetString() ?? string.Empty)
                                    : CustomWidgetScriptEngine.JsonElementToDisplayString(rawEl);
                                _ = ApplyEvaluatedItemField(record, fk, rawScalar, action.ItemFieldsJson, evalContext);
                            }
                        }

                        if (hasBlankPrimaryDraftField)
                        {
                            skipThen = true;
                            break;
                        }

                        if (!_vm.CollectionsInternal.TryGetValue(colName, out List<CustomWidgetRecord>? list))
                        {
                            list = [];
                            _vm.CollectionsInternal[colName] = list;
                        }

                        if (string.Equals(actionType, "collection.prepend", StringComparison.OrdinalIgnoreCase))
                        {
                            list.Insert(0, record);
                        }
                        else
                        {
                            list.Add(record);
                        }

                        int maxItems = _vm.ManifestInternal.Collections.TryGetValue(colName, out CustomWidgetCollectionSchema? schema)
                            ? schema.MaxItems
                            : 200;
                        while (list.Count > maxItems)
                        {
                            list.RemoveAt(list.Count - 1);
                        }

                        if (_vm.IsPreviewOnlyInternal && !_vm.IsApplyingSampleDataInternal)
                        {
                            _vm.UserMutatedCollectionsInternal.Add(colName);
                        }
                        batchTracker.MutatedData = true;
                        needsSave = true;
                        needsRebuild = true;
                    }
                    break;
                }

                case "collection.remove":
                {
                    string colName = action.Collection ?? string.Empty;
                    string targetId = CustomWidgetExpressionEvaluator.Interpolate(action.TargetId, evalContext);
                    if (!string.IsNullOrWhiteSpace(colName) &&
                        !string.IsNullOrWhiteSpace(targetId) &&
                        _vm.CollectionsInternal.TryGetValue(colName, out List<CustomWidgetRecord>? list))
                    {
                        int removed = list.RemoveAll(r => string.Equals(r.Id, targetId, StringComparison.Ordinal));
                        if (removed > 0)
                        {
                            if (_vm.IsPreviewOnlyInternal && !_vm.IsApplyingSampleDataInternal)
                            {
                                _vm.UserMutatedCollectionsInternal.Add(colName);
                            }
                            batchTracker.MutatedData = true;
                            needsSave = true;
                            needsRebuild = true;
                        }
                    }
                    break;
                }

                case "collection.update":
                {
                    string colName = action.Collection ?? string.Empty;
                    string targetId = CustomWidgetExpressionEvaluator.Interpolate(action.TargetId, evalContext);
                    if (string.IsNullOrWhiteSpace(targetId))
                    {
                        targetId = evalContext.CurrentItem?.Id ?? string.Empty;
                    }

                    if (!string.IsNullOrWhiteSpace(colName) &&
                        !string.IsNullOrWhiteSpace(targetId) &&
                        (action.ItemFields is { Count: > 0 } || action.ItemFieldsJson is { Count: > 0 }) &&
                        _vm.CollectionsInternal.TryGetValue(colName, out List<CustomWidgetRecord>? list))
                    {
                        int targetIndex = list.FindIndex(r =>
                            string.Equals(r.Id, targetId, StringComparison.Ordinal));
                        if (targetIndex >= 0)
                        {
                            CustomWidgetRecord target = list[targetIndex];
                            CustomWidgetEvaluationContext itemEvalCtx = evalContext.CurrentItem is not null
                                ? evalContext
                                : evalContext.WithItem(target, targetIndex);
                            if (action.ItemFields is not null)
                            {
                                foreach ((string fk, string fv) in action.ItemFields)
                                {
                                    _ = ApplyEvaluatedItemField(target, fk, fv, action.ItemFieldsJson, itemEvalCtx);
                                }
                            }

                            if (action.ItemFieldsJson is not null)
                            {
                                foreach ((string fk, JsonElement rawEl) in action.ItemFieldsJson)
                                {
                                    if (action.ItemFields is not null && action.ItemFields.ContainsKey(fk))
                                    {
                                        continue;
                                    }

                                    string rawScalar = rawEl.ValueKind == JsonValueKind.String
                                        ? (rawEl.GetString() ?? string.Empty)
                                        : CustomWidgetScriptEngine.JsonElementToDisplayString(rawEl);
                                    _ = ApplyEvaluatedItemField(target, fk, rawScalar, action.ItemFieldsJson, itemEvalCtx);
                                }
                            }
                            target.UpdatedAtUtcTicks = DateTimeOffset.UtcNow.UtcTicks;
                            if (_vm.IsPreviewOnlyInternal && !_vm.IsApplyingSampleDataInternal)
                            {
                                _vm.UserMutatedCollectionsInternal.Add(colName);
                            }
                            batchTracker.MutatedData = true;
                            needsSave = true;
                            needsRebuild = true;
                        }
                    }
                    break;
                }

                case "collection.clear":
                {
                    string colName = action.Collection ?? string.Empty;
                    if (!string.IsNullOrWhiteSpace(colName) &&
                        _vm.CollectionsInternal.TryGetValue(colName, out List<CustomWidgetRecord>? list) &&
                        list.Count > 0)
                    {
                        list.Clear();
                        if (_vm.IsPreviewOnlyInternal && !_vm.IsApplyingSampleDataInternal)
                        {
                            _vm.UserMutatedCollectionsInternal.Add(colName);
                        }
                        batchTracker.MutatedData = true;
                        needsSave = true;
                        needsRebuild = true;
                    }
                    break;
                }

                case "collection.reorder":
                {
                    if (ExecuteCollectionReorder(action, evalContext))
                    {
                        if (_vm.IsPreviewOnlyInternal && !_vm.IsApplyingSampleDataInternal && !string.IsNullOrWhiteSpace(action.Collection))
                        {
                            _vm.UserMutatedCollectionsInternal.Add(action.Collection);
                        }
                        batchTracker.MutatedData = true;
                        needsSave = true;
                        needsRebuild = true;
                    }
                    break;
                }

                case "popover.open":
                case "popover.close":
                case "popover.toggle":
                {
                    string rawPopover = CustomWidgetExpressionEvaluator.Interpolate(
                        action.PopoverId ?? action.TargetId ?? action.Key ?? action.Value,
                        evalContext).Trim();
                    string mode = actionType.EndsWith(".open", StringComparison.OrdinalIgnoreCase)
                        ? "open"
                        : actionType.EndsWith(".close", StringComparison.OrdinalIgnoreCase)
                            ? "close"
                            : "toggle";
                    _vm.RequestPopoverCommand(mode, rawPopover);
                    break;
                }

                case "tip.open":
                case "tip.close":
                case "tip.toggle":
                {
                    string rawTip = CustomWidgetExpressionEvaluator.Interpolate(
                        action.PopoverId ?? action.TargetId ?? action.Key ?? action.Value,
                        evalContext).Trim();
                    string mode = actionType.EndsWith(".open", StringComparison.OrdinalIgnoreCase)
                        ? "open"
                        : actionType.EndsWith(".close", StringComparison.OrdinalIgnoreCase)
                            ? "close"
                            : "toggle";
                    _vm.RequestTeachingTipCommand(mode, rawTip);
                    break;
                }

                case "audio.cue":
                case "audio.play":
                case "audio.pause":
                case "audio.stop":
                case "audio.toggle":
                case "audio.setvolume":
                case "audio.stopall":
                {
                    await _vm.ExecuteAudioActionAsync(actionType, action, evalContext);
                    break;
                }

                case "collection.exportmarkdown":
                {
                    string colName = action.Collection ?? _vm.CollectionsInternal.Keys.FirstOrDefault() ?? "logs";
                    IReadOnlyList<CustomWidgetRecord> records =
                        _vm.CollectionsInternal.TryGetValue(colName, out List<CustomWidgetRecord>? list)
                            ? list
                            : [];
                    string exportTitle = !string.IsNullOrWhiteSpace(action.Title)
                        ? CustomWidgetExpressionEvaluator.Interpolate(action.Title, evalContext)
                        : _vm.Config.Name;
                    string markdown = CustomWidgetDataStore.FormatCollectionAsMarkdown(
                        exportTitle,
                        colName,
                        records);
                    CustomWidgetViewModel.CopyTextToClipboard(markdown);
                    _vm.RaiseFeedback(
                        _vm.LocalizationServiceInternal.T("CustomWidget.Toast.MarkdownCopied"),
                        WidgetFeedbackSeverity.Info);
                    break;
                }

                case "file.open":
                {
                    string path = CustomWidgetExpressionEvaluator.Interpolate(action.Path, evalContext);
                    await _vm.OpenFileWithDefaultAppAsync(path);
                    break;
                }

                case "file.reveal":
                {
                    string path = CustomWidgetExpressionEvaluator.Interpolate(action.Path, evalContext);
                    await _vm.RevealFileInExplorerAsync(path);
                    break;
                }

                case "clipboard.copy":
                {
                    string text = CustomWidgetExpressionEvaluator.Interpolate(action.Text ?? action.Value, evalContext);
                    if (!string.IsNullOrEmpty(text))
                    {
                        CustomWidgetViewModel.CopyTextToClipboard(text);
                        if (_vm.ManifestUsesClipboard())
                        {
                            _vm.CapabilityHost.ClipboardSnapshot = CustomWidgetClipboardSnapshot.FromText(text);
                            evalContext = _vm.CreateEvaluationContext().WithEventValue(evalContext.EventValue);
                            needsRebuild = true;
                        }
                        _vm.RaiseFeedback(
                            _vm.LocalizationServiceInternal.T("CustomWidget.Toast.Copied"),
                            WidgetFeedbackSeverity.Info);
                    }
                    break;
                }

                case "url.open":
                {
                    string url = CustomWidgetExpressionEvaluator.Interpolate(action.Url ?? action.Value, evalContext);
                    if (Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) &&
                        (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
                    {
                        if (!await _vm.EnsureWidgetPermissionGrantedAsync(
                                CustomWidgetPermissionKind.PublicNetwork,
                                actionKind: "url.open",
                                targetDisplay: uri.AbsoluteUri,
                                isReadOnlyPublicGet: true))
                        {
                            RuntimeTrace.Record("url.open", uri.Host, ok: false, "Permission denied");
                            skipThen = true;
                            break;
                        }

                        if (await _vm.EnsureCommunityExternalTargetApprovedAsync(
                                actionKind: "url.open",
                                trustToken: $"host:{uri.DnsSafeHost.ToLowerInvariant()}",
                                targetDisplay: uri.AbsoluteUri))
                        {
                            _ = Launcher.LaunchUriAsync(uri);
                        }
                    }
                    else
                    {
                        RuntimeTrace.Record("url.open", url, ok: false, "Only http:// or https:// URLs are supported (use file.reveal for local paths)");
                    }
                    break;
                }

                case "source.refresh":
                case "refreshsource":
                {
                    string? targetSourceId = !string.IsNullOrWhiteSpace(action.TargetId)
                        ? CustomWidgetExpressionEvaluator.Interpolate(action.TargetId, evalContext)
                        : !string.IsNullOrWhiteSpace(action.Value)
                            ? CustomWidgetExpressionEvaluator.Interpolate(action.Value, evalContext)
                            : null;
                    if (_vm.ManifestUsesNetworkSources())
                    {
                        _ = _vm.RefreshNetworkSourcesNowAsync(targetSourceId, isManualUserAction: true);
                    }
                    if (_vm.ManifestUsesWeather() &&
                        (string.IsNullOrWhiteSpace(targetSourceId) ||
                         string.Equals(targetSourceId, "weather", StringComparison.OrdinalIgnoreCase) ||
                         _vm.ManifestInternal.Sources.Any(s =>
                             string.Equals(s.Type, "weather", StringComparison.OrdinalIgnoreCase) &&
                             string.Equals(s.Id, targetSourceId, StringComparison.OrdinalIgnoreCase))))
                    {
                        _ = _vm.RefreshWeatherNowAsync(forceRefresh: true, isManualUserAction: true);
                    }
                    if (_vm.ManifestUsesDeviceState() &&
                        (string.IsNullOrWhiteSpace(targetSourceId) ||
                         string.Equals(targetSourceId, "deviceState", StringComparison.OrdinalIgnoreCase)))
                    {
                        _ = _vm.RefreshDeviceStateNowAsync();
                    }
                    if (_vm.ManifestUsesStorageDrives() &&
                        (string.IsNullOrWhiteSpace(targetSourceId) ||
                         string.Equals(targetSourceId, "storageDrives", StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(targetSourceId, "drives", StringComparison.OrdinalIgnoreCase)))
                    {
                        _vm.RefreshStorageDrivesNow(triggerRebuild: true);
                    }
                    if (_vm.ManifestUsesClipboard() &&
                        (string.IsNullOrWhiteSpace(targetSourceId) ||
                         string.Equals(targetSourceId, "clipboard", StringComparison.OrdinalIgnoreCase)))
                    {
                        _ = _vm.RefreshClipboardNowAsync();
                    }
                    if (_vm.ManifestUsesMusic() &&
                        (string.IsNullOrWhiteSpace(targetSourceId) ||
                         string.Equals(targetSourceId, "music", StringComparison.OrdinalIgnoreCase)))
                    {
                        _ = _vm.RefreshMusicNowAsync(triggerRebuild: true);
                    }
                    if (_vm.ManifestUsesTodo() &&
                        (string.IsNullOrWhiteSpace(targetSourceId) ||
                         string.Equals(targetSourceId, "todo", StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(targetSourceId, "todos", StringComparison.OrdinalIgnoreCase)))
                    {
                        _ = _vm.RefreshTodoNowAsync(triggerRebuild: true);
                    }
                    if (_vm.ManifestUsesFileTextSources() &&
                        (string.IsNullOrWhiteSpace(targetSourceId) ||
                         string.Equals(targetSourceId, "fileText", StringComparison.OrdinalIgnoreCase) ||
                         _vm.ManifestInternal.Sources.Any(s =>
                             string.Equals(s.Type, "fileText", StringComparison.OrdinalIgnoreCase) &&
                             string.Equals(s.Id, targetSourceId, StringComparison.OrdinalIgnoreCase))))
                    {
                        _ = _vm.RefreshFileTextSourcesNowAsync(
                            string.Equals(targetSourceId, "fileText", StringComparison.OrdinalIgnoreCase) ? null : targetSourceId);
                    }
                    if (_vm.ManifestUsesCommandSources() &&
                        (string.IsNullOrWhiteSpace(targetSourceId) ||
                         string.Equals(targetSourceId, "command", StringComparison.OrdinalIgnoreCase) ||
                         _vm.ManifestInternal.Sources.Any(s =>
                             string.Equals(s.Type, "command", StringComparison.OrdinalIgnoreCase) &&
                             string.Equals(s.Id, targetSourceId, StringComparison.OrdinalIgnoreCase))))
                    {
                        _ = _vm.RefreshCommandSourcesNowAsync(
                            string.Equals(targetSourceId, "command", StringComparison.OrdinalIgnoreCase) ? null : targetSourceId);
                    }
                    break;
                }

                case "toast.show":
                {
                    string msg = CustomWidgetExpressionEvaluator.Interpolate(action.Message ?? action.Text, evalContext);
                    if (!string.IsNullOrWhiteSpace(msg))
                    {
                        _vm.RaiseFeedback(msg, WidgetFeedbackSeverity.Info);
                    }
                    break;
                }

                case "reminder.notify":
                {
                    string? rawTitle = action.Title ?? (!string.IsNullOrWhiteSpace(action.Message) ? action.Text : null);
                    string title = CustomWidgetExpressionEvaluator.Interpolate(rawTitle, evalContext).Trim();
                    if (string.IsNullOrWhiteSpace(title))
                    {
                        title = _vm.Config.Name;
                    }
                    string msg = CustomWidgetExpressionEvaluator.Interpolate(
                        action.Message ?? action.Text ?? action.Value,
                        evalContext).Trim();
                    if (!string.IsNullOrWhiteSpace(msg))
                    {
                        _vm.RaiseFeedback(msg, WidgetFeedbackSeverity.Info);
                        _vm.DispatchReminderToastNotification(title, msg);
                    }
                    break;
                }

                case "file.transform":
                {
                    string rawSourcePath = CustomWidgetExpressionEvaluator.Interpolate(
                        action.Path ?? evalContext.DropFile?.Path,
                        evalContext).Trim();
                    string resolvedSourcePath = _vm.Store.ResolveRuntimePath(rawSourcePath);
                    string formatOrOp = CustomWidgetExpressionEvaluator.Interpolate(
                        action.Format ?? action.Value,
                        evalContext).Trim();
                    string normalizedOp = CustomWidgetFileTransformService.NormalizeOperation(formatOrOp);
                    bool isReadOnlyTransform = normalizedOp is "sha256" or "md5";

                    if (!string.IsNullOrWhiteSpace(resolvedSourcePath) &&
                        !_vm.Store.IsInsideManagedAttachments(resolvedSourcePath))
                    {
                        if (!CustomWidgetLocalFileEngine.TryResolveSafeLocalPath(
                                resolvedSourcePath,
                                forWrite: !isReadOnlyTransform,
                                out string safePath,
                                out string? pathError))
                        {
                            _vm.StateInternal["transformStatus"] = "error";
                            _vm.StateInternal["transformError"] = pathError ?? "Blocked unsafe file path.";
                            needsRebuild = true;
                            skipThen = true;
                            break;
                        }

                        resolvedSourcePath = safePath;
                        bool filePermGranted = await _vm.EnsureWidgetPermissionGrantedAsync(
                            CustomWidgetPermissionKind.LocalFileAccess,
                            actionKind: "file.transform",
                            targetDisplay: resolvedSourcePath);
                        if (!filePermGranted)
                        {
                            _vm.StateInternal["transformStatus"] = "error";
                            _vm.StateInternal["transformError"] = "Permission denied.";
                            needsRebuild = true;
                            skipThen = true;
                            break;
                        }
                    }

                    string tfId = !string.IsNullOrWhiteSpace(action.Id)
                        ? CustomWidgetExpressionEvaluator.Interpolate(action.Id, evalContext).Trim()
                        : (action.Key ?? formatOrOp);
                    string tfJobKey = !string.IsNullOrWhiteSpace(action.JobKey)
                        ? CustomWidgetExpressionEvaluator.Interpolate(action.JobKey, evalContext).Trim()
                        : tfId;
                    using CustomWidgetJobBus.ActiveJobHandle tfJob = _vm.JobBusInternal.BeginJob(tfJobKey, tfId, avoidSupersede: string.IsNullOrWhiteSpace(action.JobKey));

                    CustomWidgetTransformResult tr;
                    try
                    {
                        tr = await CustomWidgetFileTransformService.TransformFileAsync(
                            resolvedSourcePath,
                            formatOrOp,
                            action.MaxWidth,
                            action.Quality,
                            cancellationToken: tfJob.Token);
                    }
                    catch (OperationCanceledException) when (tfJob.IsCancellationRequested)
                    {
                        tr = new CustomWidgetTransformResult(
                            false,
                            formatOrOp,
                            resolvedSourcePath,
                            string.Empty,
                            string.Empty,
                            string.Empty,
                            string.Empty,
                            "Cancelled");
                    }

                    if (_vm.IsDisposedInternal)
                    {
                        return;
                    }

                    _vm.LastTransformResultInternal = tr;
                    evalContext = evalContext.WithTransform(tr);
                    string tfOut = !string.IsNullOrWhiteSpace(tr.OutputPath) ? tr.OutputPath : tr.ResultText;
                    _vm.SyncFlatStateValue("transformStatus", tr.Succeeded ? "ok" : "error");
                    _vm.SyncFlatStateValue("transformOutput", tfOut);
                    _vm.SyncFlatStateValue("transformError", tr.ErrorMessage ?? string.Empty);
                    if (!string.IsNullOrWhiteSpace(action.Key))
                    {
                        string outKey = CustomWidgetViewModel.NormalizeStateKey(action.Key);
                        if (!string.IsNullOrWhiteSpace(outKey))
                        {
                            _vm.SyncFlatStateValue(outKey, tr.Succeeded ? tfOut : (tr.ErrorMessage ?? string.Empty));
                        }
                    }

                    string tfCallback = !string.IsNullOrWhiteSpace(action.OnComplete)
                        ? action.OnComplete!.Trim()
                        : (!string.IsNullOrWhiteSpace(action.Fn) ? action.Fn!.Trim() : "onFileTransformResult");
                    if (_vm.CompiledScriptInternal is not null && _vm.CompiledScriptInternal.ExportedFunctions.Contains(tfCallback))
                    {
                        string summaryText = !string.IsNullOrWhiteSpace(tr.OutputName)
                            ? $"{tr.OutputName} ({tr.SizeText})"
                            : tr.ResultText;
                        string cbPayload = CustomWidgetJobBus.BuildFileOperationResultPayloadJson(
                            tfJob.Id,
                            tfJob.JobKey,
                            "file.transform",
                            formatOrOp,
                            tr.Succeeded,
                            resolvedSourcePath,
                            tr.OutputPath ?? string.Empty,
                            summaryText,
                            tr.ErrorMessage,
                            tfJob.ElapsedMs,
                            cancelled: tfJob.IsCancellationRequested);
                        _vm.InvokeScriptCore(
                            functionName: tfCallback,
                            arg: tfJob.Id,
                            eventValue: evalContext.EventValue,
                            currentItem: evalContext.CurrentItem,
                            dropFile: evalContext.DropFile,
                            sourceId: tfJob.Id,
                            rawSourcePayload: cbPayload,
                            timeoutMs: CustomWidgetScriptEngine.DefaultActionTimeoutMs,
                            requireExport: false,
                            allowCopy: true,
                            out _,
                            out _,
                            out _,
                            out IReadOnlyList<CustomWidgetActionSpec>? tfDispatched,
                            callKind: CustomWidgetScriptCallKind.JobResult,
                            dropFiles: evalContext.DropFiles,
                            rawJsonPayload: cbPayload);
                        evalContext = RefreshLiveEvaluationContext(evalContext);
                        if (tfDispatched is { Count: > 0 })
                        {
                            await ExecuteActionsAsync(tfDispatched, evalContext, isNested: true, depth: depth + 1);
                            evalContext = RefreshLiveEvaluationContext(evalContext);
                        }
                    }

                    batchTracker.MutatedData = true;
                    needsSave = true;
                    needsRebuild = true;
                    if (!tr.Succeeded)
                    {
                        _vm.RaiseFeedback(
                            tr.ErrorMessage ?? _vm.LocalizationServiceInternal.T("CustomWidget.Toast.TransformFailed"),
                            WidgetFeedbackSeverity.Warning);
                        skipThen = true;
                    }
                    else
                    {
                        string summary = !string.IsNullOrWhiteSpace(tr.OutputName)
                            ? $"{tr.OutputName} ({tr.SizeText})"
                            : tr.ResultText;
                        if (!string.IsNullOrWhiteSpace(summary))
                        {
                            _vm.RaiseFeedback(summary, WidgetFeedbackSeverity.Info);
                        }
                    }
                    break;
                }

                case "file.pick":
                {
                    IntPtr ownerHwnd = _vm.OwnerWindowHandleResolver?.Invoke() ?? IntPtr.Zero;
                    string pickMode = CustomWidgetExpressionEvaluator.Interpolate(action.Mode, evalContext).Trim();
                    bool pickFolder = string.Equals(pickMode, "folder", StringComparison.OrdinalIgnoreCase) ||
                                      string.Equals(pickMode, "directory", StringComparison.OrdinalIgnoreCase);

                    string pickId = !string.IsNullOrWhiteSpace(action.Id)
                        ? CustomWidgetExpressionEvaluator.Interpolate(action.Id, evalContext).Trim()
                        : (action.Key ?? "file.pick");
                    string pickJobKey = !string.IsNullOrWhiteSpace(action.JobKey)
                        ? CustomWidgetExpressionEvaluator.Interpolate(action.JobKey, evalContext).Trim()
                        : pickId;
                    using CustomWidgetJobBus.ActiveJobHandle pickJob = _vm.JobBusInternal.BeginJob(
                        pickJobKey, pickId, avoidSupersede: string.IsNullOrWhiteSpace(action.JobKey));
                    string pickCallback = !string.IsNullOrWhiteSpace(action.OnComplete)
                        ? action.OnComplete!.Trim()
                        : (!string.IsNullOrWhiteSpace(action.Fn) ? action.Fn!.Trim() : "onFilePickResult");

                    IReadOnlyList<string> picked;
                    if (pickFolder)
                    {
                        string? folder = ownerHwnd != IntPtr.Zero
                            ? await FolderPickerService.PickFolderAsync(ownerHwnd, "widget-action")
                            : null;
                        picked = !string.IsNullOrWhiteSpace(folder) ? [folder] : [];
                    }
                    else
                    {
                        IReadOnlyList<string> filters = CustomWidgetOnboardingHelper.ParseAcceptFileTypeFilters(
                            action.Accept ?? action.Format);
                        if (filters.Count > 0 && ownerHwnd != IntPtr.Zero)
                        {
                            if (action.Multiple)
                            {
                                picked = await FileOpenPickerService.PickMultipleFilesAsync(
                                    ownerHwnd,
                                    filters,
                                    Microsoft.Windows.Storage.Pickers.PickerLocationId.Desktop);
                            }
                            else
                            {
                                string? single = await FileOpenPickerService.PickSingleFileAsync(
                                    ownerHwnd,
                                    filters,
                                    Microsoft.Windows.Storage.Pickers.PickerLocationId.Desktop);
                                picked = !string.IsNullOrWhiteSpace(single) ? [single] : [];
                            }
                        }
                        else
                        {
                            picked = ownerHwnd != IntPtr.Zero
                                ? await FileOpenPickerService.PickFilesAsync(ownerHwnd)
                                : [];
                        }
                    }

                    if (picked.Count == 0)
                    {
                        skipThen = true;
                        break;
                    }

                    bool hasExplicitReceiver = !string.IsNullOrWhiteSpace(action.Key) ||
                                               !string.IsNullOrWhiteSpace(action.OnComplete) ||
                                               !string.IsNullOrWhiteSpace(action.Fn);
                    bool usePortableMode = !pickFolder &&
                                           !hasExplicitReceiver &&
                                           action.Then is not { Count: > 0 } &&
                                           _vm.ManifestInternal.OnDrop is not null;

                    var resolvedFiles = new List<CustomWidgetDropFileContext>(picked.Count);
                    foreach (string pickedPath in picked)
                    {
                        CustomWidgetDropFileContext? dropCtx = await _vm.DropZoneCoordinator.BuildDropFileContextAsync(
                            pickedPath,
                            usePortableStoreMode: usePortableMode);
                        if (dropCtx is not null)
                        {
                            resolvedFiles.Add(dropCtx);
                        }
                    }

                    if (resolvedFiles.Count == 0)
                    {
                        if (_vm.CompiledScriptInternal is not null &&
                            _vm.CompiledScriptInternal.ExportedFunctions.Contains(pickCallback))
                        {
                            string cancelPayload = CustomWidgetJobBus.BuildFileListResultPayloadJson(
                                pickJob.Id,
                                pickJob.JobKey,
                                "file.pick",
                                ok: false,
                                primaryPath: string.Empty,
                                files: [],
                                error: null,
                                durationMs: pickJob.ElapsedMs,
                                cancelled: true);
                            _vm.InvokeScriptCore(
                                functionName: pickCallback,
                                arg: pickJob.Id,
                                eventValue: evalContext.EventValue,
                                currentItem: evalContext.CurrentItem,
                                dropFile: evalContext.DropFile,
                                sourceId: pickJob.Id,
                                rawSourcePayload: cancelPayload,
                                timeoutMs: CustomWidgetScriptEngine.DefaultActionTimeoutMs,
                                requireExport: false,
                                allowCopy: true,
                                out _,
                                out _,
                                out _,
                                out _,
                                callKind: CustomWidgetScriptCallKind.JobResult,
                                rawJsonPayload: cancelPayload);
                        }

                        skipThen = true;
                        break;
                    }

                    evalContext = evalContext.WithDrop(resolvedFiles[0], resolvedFiles);
                    if (!string.IsNullOrWhiteSpace(action.Key))
                    {
                        string outKey = CustomWidgetViewModel.NormalizeStateKey(action.Key);
                        if (!string.IsNullOrWhiteSpace(outKey))
                        {
                            _vm.SyncFlatStateValue(outKey, resolvedFiles[0].Path);
                            needsSave = true;
                            needsRebuild = true;
                        }
                    }

                    if (_vm.CompiledScriptInternal is not null &&
                        _vm.CompiledScriptInternal.ExportedFunctions.Contains(pickCallback))
                    {
                        string payloadJson = CustomWidgetJobBus.BuildFileListResultPayloadJson(
                            pickJob.Id,
                            pickJob.JobKey,
                            "file.pick",
                            ok: true,
                            primaryPath: resolvedFiles[0].Path,
                            files: resolvedFiles,
                            error: null,
                            durationMs: pickJob.ElapsedMs);
                        _vm.InvokeScriptCore(
                            functionName: pickCallback,
                            arg: resolvedFiles[0].Path,
                            eventValue: evalContext.EventValue,
                            currentItem: evalContext.CurrentItem,
                            dropFile: resolvedFiles[0],
                            sourceId: pickId,
                            rawSourcePayload: payloadJson,
                            timeoutMs: CustomWidgetScriptEngine.DefaultActionTimeoutMs,
                            requireExport: false,
                            allowCopy: true,
                            out bool pickMutatedPersisted,
                            out bool pickMutatedUi,
                            out bool pickMutatedStructuralUi,
                            out IReadOnlyList<CustomWidgetActionSpec>? pickDispatched,
                            callKind: CustomWidgetScriptCallKind.JobResult,
                            dropFiles: resolvedFiles,
                            rawJsonPayload: payloadJson);
                        if (pickMutatedPersisted) needsSave = true;
                        if (pickMutatedStructuralUi || pickMutatedPersisted || pickMutatedUi) needsRebuild = true;
                        evalContext = RefreshLiveEvaluationContext(evalContext);
                        if (pickDispatched is { Count: > 0 })
                        {
                            await ExecuteActionsAsync(pickDispatched, evalContext, isNested: true, depth: depth + 1);
                            evalContext = RefreshLiveEvaluationContext(evalContext);
                        }
                    }

                    IReadOnlyList<CustomWidgetActionSpec> followUp = action.Then is { Count: > 0 }
                        ? action.Then
                        : (!hasExplicitReceiver
                            ? (_vm.ManifestInternal.OnDrop?.Actions ?? CustomWidgetDropZoneCoordinator.FindFirstDropZoneActions(_vm.ManifestInternal.View))
                            : []);
                    if (followUp.Count > 0)
                    {
                        bool batchViaScript = followUp.Any(static a =>
                            string.Equals(a.Type, "script.run", StringComparison.OrdinalIgnoreCase));
                        if (batchViaScript)
                        {
                            await ExecuteActionsAsync(
                                followUp,
                                evalContext.WithDrop(resolvedFiles[0], resolvedFiles),
                                isNested: true,
                                depth: depth + 1);
                        }
                        else
                        {
                            foreach (CustomWidgetDropFileContext dropCtx in resolvedFiles)
                            {
                                await ExecuteActionsAsync(
                                    followUp,
                                    evalContext.WithDrop(dropCtx, resolvedFiles),
                                    isNested: true,
                                    depth: depth + 1);
                            }
                        }
                    }
                    skipThen = true;
                    break;
                }

                case "text.transform":
                {
                    string op = !string.IsNullOrWhiteSpace(action.Format)
                        ? CustomWidgetExpressionEvaluator.Interpolate(action.Format, evalContext).Trim()
                        : !string.IsNullOrWhiteSpace(action.Text) && !string.IsNullOrWhiteSpace(action.Value)
                            ? CustomWidgetExpressionEvaluator.Interpolate(action.Value, evalContext).Trim()
                            : CustomWidgetExpressionEvaluator.Interpolate(action.TargetId ?? action.Value, evalContext).Trim();

                    string inputText;
                    if (!string.IsNullOrWhiteSpace(action.Text))
                    {
                        inputText = CustomWidgetExpressionEvaluator.Interpolate(action.Text, evalContext);
                    }
                    else if (!string.IsNullOrWhiteSpace(action.Format) && !string.IsNullOrWhiteSpace(action.Value))
                    {
                        inputText = CustomWidgetExpressionEvaluator.Interpolate(action.Value, evalContext);
                    }
                    else
                    {
                        inputText = _vm.CapabilityHost.ClipboardSnapshot.Text;
                    }

                    CustomWidgetTransformResult tr = CustomWidgetUtilityEngine.ExecuteTextTransform(
                        inputText,
                        op,
                        cultureName: _vm.ActiveCultureName);
                    _vm.LastTransformResultInternal = tr;
                    evalContext = evalContext.WithTransform(tr);
                    _vm.StateInternal["transformStatus"] = tr.Succeeded ? "ok" : "error";
                    _vm.StateInternal["transformOutput"] = tr.ResultText;
                    _vm.StateInternal["transformError"] = tr.ErrorMessage ?? string.Empty;
                    if (!string.IsNullOrWhiteSpace(action.Key))
                    {
                        string outKey = CustomWidgetViewModel.NormalizeStateKey(action.Key);
                        if (!string.IsNullOrWhiteSpace(outKey))
                        {
                            _vm.StateInternal[outKey] = tr.Succeeded ? tr.ResultText : string.Empty;
                        }
                    }

                    batchTracker.MutatedData = true;
                    needsSave = true;
                    needsRebuild = true;
                    if (!tr.Succeeded)
                    {
                        _vm.RaiseFeedback(
                            tr.ErrorMessage ?? _vm.LocalizationServiceInternal.T("CustomWidget.Toast.TransformFailed"),
                            WidgetFeedbackSeverity.Warning);
                        skipThen = true;
                    }
                    else if (string.Equals(action.Mode, "copy", StringComparison.OrdinalIgnoreCase) &&
                             !string.IsNullOrEmpty(tr.ResultText))
                    {
                        CustomWidgetViewModel.CopyTextToClipboard(tr.ResultText);
                        if (_vm.ManifestUsesClipboard())
                        {
                            _vm.CapabilityHost.ClipboardSnapshot = CustomWidgetClipboardSnapshot.FromText(tr.ResultText);
                        }
                        _vm.RaiseFeedback(
                            _vm.LocalizationServiceInternal.T("CustomWidget.Toast.Copied"),
                            WidgetFeedbackSeverity.Info);
                    }
                    break;
                }

                case "launcher.open":
                {
                    string rawTarget = CustomWidgetExpressionEvaluator.Interpolate(
                        action.TargetId ?? action.Path ?? action.Url ?? action.Value,
                        evalContext).Trim();
                    if (!await _vm.EnsureWidgetPermissionGrantedAsync(
                            CustomWidgetPermissionKind.SystemAndClipboard,
                            actionKind: "launcher.open",
                            targetDisplay: rawTarget))
                    {
                        skipThen = true;
                        break;
                    }

                    if (!CustomWidgetUtilityEngine.TryResolveQuickLaunchTarget(
                            rawTarget,
                            _vm.Store,
                            out QuickLaunchTarget resolvedTarget))
                    {
                        _vm.RaiseFeedback(
                            _vm.LocalizationServiceInternal.T("CustomWidget.Toast.FileNotFound"),
                            WidgetFeedbackSeverity.Warning);
                        skipThen = true;
                        break;
                    }

                    if (resolvedTarget.RequiresCommunityConsent)
                    {
                        bool forceForExecutable = !resolvedTarget.IsUri &&
                                                  CustomWidgetUtilityEngine.IsExecutableOrScriptPath(resolvedTarget.LaunchTarget);
                        bool approved = await _vm.EnsureCommunityExternalTargetApprovedAsync(
                            actionKind: "launcher.open",
                            trustToken: resolvedTarget.TrustToken,
                            targetDisplay: resolvedTarget.DisplayTarget,
                            requireConsentForAllOrigins: forceForExecutable);
                        if (!approved)
                        {
                            skipThen = true;
                            break;
                        }
                    }

                    bool launched = await CustomWidgetUtilityEngine.ExecuteQuickLaunchAsync(resolvedTarget);
                    if (!launched)
                    {
                        _vm.RaiseFeedback(
                            _vm.LocalizationServiceInternal.T("CustomWidget.Toast.FileNotFound"),
                            WidgetFeedbackSeverity.Warning);
                        skipThen = true;
                    }
                    break;
                }

                case "system.control":
                {
                    string cmd = !string.IsNullOrWhiteSpace(action.Key)
                        ? CustomWidgetExpressionEvaluator.Interpolate(action.Key, evalContext).Trim()
                        : !string.IsNullOrWhiteSpace(action.TargetId)
                            ? CustomWidgetExpressionEvaluator.Interpolate(action.TargetId, evalContext).Trim()
                            : CustomWidgetExpressionEvaluator.Interpolate(action.Value, evalContext).Trim();
                    string? rawVal = !string.IsNullOrWhiteSpace(action.Key) || !string.IsNullOrWhiteSpace(action.TargetId)
                        ? CustomWidgetExpressionEvaluator.Interpolate(action.Value, evalContext).Trim()
                        : null;

                    if (!await _vm.EnsureWidgetPermissionGrantedAsync(
                            CustomWidgetPermissionKind.SystemAndClipboard,
                            actionKind: "system.control",
                            targetDisplay: cmd))
                    {
                        skipThen = true;
                        break;
                    }

                    if (cmd.Equals("recyclebin.empty", StringComparison.OrdinalIgnoreCase) ||
                        cmd.Equals("power.lock", StringComparison.OrdinalIgnoreCase) ||
                        cmd.Equals("lock", StringComparison.OrdinalIgnoreCase))
                    {
                        bool approved = await _vm.EnsureCommunityExternalTargetApprovedAsync(
                            actionKind: "system.control",
                            trustToken: $"sys:{cmd.ToLowerInvariant()}",
                            targetDisplay: cmd);
                        if (!approved)
                        {
                            skipThen = true;
                            break;
                        }
                    }

                    string? prevEventVal = evalContext.EventValue;
                    CustomWidgetSystemControlOutcome sysOutcome = await _vm.SystemControlServiceInternal.ExecuteControlAsync(
                        cmd,
                        rawVal,
                        _vm.OwnerWindowHandleResolver?.Invoke() ?? IntPtr.Zero,
                        _vm.ActiveCultureName);
                    _vm.CapabilityHost.DeviceStateSnapshot = _vm.SystemControlServiceInternal.Latest;
                    evalContext = _vm.CreateEvaluationContext().WithEventValue(prevEventVal);
                    if (cmd.Equals("volume.set", StringComparison.OrdinalIgnoreCase) ||
                        cmd.Equals("brightness.set", StringComparison.OrdinalIgnoreCase))
                    {
                        needsLiveRefreshOnly = true;
                    }
                    else
                    {
                        needsRebuild = true;
                    }

                    if (!string.IsNullOrWhiteSpace(sysOutcome.FeedbackMessage) &&
                        !cmd.Equals("volume.set", StringComparison.OrdinalIgnoreCase) &&
                        !cmd.Equals("brightness.set", StringComparison.OrdinalIgnoreCase))
                    {
                        _vm.RaiseFeedback(
                            sysOutcome.FeedbackMessage,
                            sysOutcome.Succeeded ? WidgetFeedbackSeverity.Info : WidgetFeedbackSeverity.Warning);
                    }
                    if (!sysOutcome.Succeeded)
                    {
                        skipThen = true;
                    }
                    break;
                }

                case "system.opensetting":
                {
                    string target = CustomWidgetExpressionEvaluator.Interpolate(
                        action.TargetId ?? action.Key ?? action.Value ?? action.Url,
                        evalContext).Trim();
                    if (!await _vm.EnsureWidgetPermissionGrantedAsync(
                            CustomWidgetPermissionKind.SystemAndClipboard,
                            actionKind: "system.opensetting",
                            targetDisplay: target))
                    {
                        skipThen = true;
                        break;
                    }
                    await _vm.SystemControlServiceInternal.OpenSettingAsync(target);
                    break;
                }

                case "deskbox.search":
                case "app.opensearch":
                {
                    if (_vm.IsUntrustedStagePreview)
                    {
                        break;
                    }

                    string? initialQuery = !string.IsNullOrWhiteSpace(action.Text)
                        ? CustomWidgetExpressionEvaluator.Interpolate(action.Text, evalContext).Trim()
                        : !string.IsNullOrWhiteSpace(action.Value)
                            ? CustomWidgetExpressionEvaluator.Interpolate(action.Value, evalContext).Trim()
                            : null;
                    if (_vm.OpenSearchPopupOverride is { } overrideHandler)
                    {
                        overrideHandler(string.IsNullOrWhiteSpace(initialQuery) ? null : initialQuery);
                    }
                    else
                    {
                        CustomWidgetHostActionBridge.RequestOpenSearchPopup(initialQuery);
                    }
                    break;
                }

                case "timer.toggle":
                case "pomodoro.toggle":
                {
                    CustomWidgetPomodoroEngine.ToggleStartPause(_vm.StateInternal, _vm.SettingsInternal, DateTimeOffset.Now);
                    _vm.UpdatePomodoroTimerSubscription();
                    needsSave = true;
                    needsRebuild = true;
                    break;
                }

                case "timer.reset":
                case "pomodoro.reset":
                {
                    CustomWidgetPomodoroEngine.ResetCurrentPhase(_vm.StateInternal, _vm.SettingsInternal, DateTimeOffset.Now);
                    _vm.UpdatePomodoroTimerSubscription();
                    needsSave = true;
                    needsRebuild = true;
                    break;
                }

                case "pomodoro.extend":
                {
                    string rawVal = CustomWidgetExpressionEvaluator.Interpolate(action.Value ?? "300", evalContext).Trim();
                    int extraSeconds = 300;
                    if (rawVal.EndsWith("m", StringComparison.OrdinalIgnoreCase) &&
                        int.TryParse(rawVal[..^1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int mins))
                    {
                        extraSeconds = Math.Clamp(mins, 1, 120) * 60;
                    }
                    else if (int.TryParse(rawVal, NumberStyles.Integer, CultureInfo.InvariantCulture, out int secs))
                    {
                        extraSeconds = Math.Clamp(secs, 10, 7200);
                    }

                    CustomWidgetPomodoroEngine.ExtendSeconds(_vm.StateInternal, _vm.SettingsInternal, extraSeconds, DateTimeOffset.Now);
                    _vm.UpdatePomodoroTimerSubscription();
                    needsSave = true;
                    needsLiveRefreshOnly = true;
                    break;
                }

                case "pomodoro.skip":
                {
                    CustomWidgetPomodoroEngine.SkipPhase(_vm.StateInternal, _vm.SettingsInternal, DateTimeOffset.Now);
                    _vm.UpdatePomodoroTimerSubscription();
                    needsSave = true;
                    needsRebuild = true;
                    break;
                }

                case "pomodoro.setphase":
                {
                    string targetPhase = CustomWidgetExpressionEvaluator.Interpolate(action.Value, evalContext);
                    CustomWidgetPomodoroEngine.SetPhase(_vm.StateInternal, _vm.SettingsInternal, targetPhase, DateTimeOffset.Now);
                    _vm.UpdatePomodoroTimerSubscription();
                    needsSave = true;
                    needsRebuild = true;
                    break;
                }

                case "pomodoro.cyclephase":
                {
                    CustomWidgetPomodoroEngine.CyclePhase(_vm.StateInternal, _vm.SettingsInternal, DateTimeOffset.Now);
                    _vm.UpdatePomodoroTimerSubscription();
                    needsSave = true;
                    needsRebuild = true;
                    break;
                }

                case "pomodoro.sedentaryconfirm":
                {
                    CustomWidgetPomodoroEngine.ConfirmStoodUp(_vm.StateInternal, _vm.SettingsInternal, DateTimeOffset.Now);
                    _vm.UpdatePomodoroTimerSubscription();
                    needsSave = true;
                    needsRebuild = true;
                    break;
                }

                case "pomodoro.sedentarysnooze":
                {
                    string rawSnooze = CustomWidgetExpressionEvaluator.Interpolate(action.Value ?? "15m", evalContext).Trim();
                    int snoozeSeconds = 900;
                    if (rawSnooze.EndsWith("m", StringComparison.OrdinalIgnoreCase) &&
                        int.TryParse(rawSnooze[..^1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int snoozeMin))
                    {
                        snoozeSeconds = Math.Clamp(snoozeMin, 1, 120) * 60;
                    }
                    else if (int.TryParse(rawSnooze, NumberStyles.Integer, CultureInfo.InvariantCulture, out int snoozeSec))
                    {
                        snoozeSeconds = Math.Clamp(snoozeSec, 10, 7200);
                    }

                    CustomWidgetPomodoroEngine.SnoozeSedentaryReminder(_vm.StateInternal, _vm.SettingsInternal, snoozeSeconds, DateTimeOffset.Now);
                    _vm.UpdatePomodoroTimerSubscription();
                    needsSave = true;
                    needsRebuild = true;
                    break;
                }

                case "countdown.pin":
                {
                    string targetId = CustomWidgetExpressionEvaluator.Interpolate(
                        action.TargetId ?? action.Value,
                        evalContext);
                    if (CustomWidgetCountdownEngine.PinEvent(_vm.CollectionsInternal, targetId))
                    {
                        needsSave = true;
                        needsRebuild = true;
                    }
                    break;
                }

                case "countdown.delete":
                {
                    string targetId = CustomWidgetExpressionEvaluator.Interpolate(
                        action.TargetId ?? action.Value,
                        evalContext);
                    if (CustomWidgetCountdownEngine.DeleteEvent(_vm.CollectionsInternal, targetId))
                    {
                        needsSave = true;
                        needsRebuild = true;
                    }
                    break;
                }

                case "countdown.upsert":
                {
                    if (_vm.ExecuteCountdownUpsertAction(action, evalContext))
                    {
                        needsSave = true;
                        needsRebuild = true;
                    }
                    break;
                }

                case "todo.add":
                {
                    string title = ResolveTodoArg(
                        action,
                        evalContext,
                        action.Title ?? action.Text ?? action.Value,
                        "title", "text", "value") ?? string.Empty;
                    if (string.IsNullOrWhiteSpace(title))
                    {
                        skipThen = true;
                        break;
                    }

                    string? dueIso = ResolveTodoArg(action, evalContext, action.DueIso, "dueIso", "dueDate", "due");
                    string? priority = ResolveTodoArg(action, evalContext, action.Priority ?? action.Format ?? action.Mode, "priority");
                    bool added = await CustomWidgetHostSnapshotService.AddTodoFromCustomWidgetAsync(
                        _vm.SettingsServiceInternal,
                        title,
                        dueIso,
                        priority);
                    if (added)
                    {
                        await _vm.CapabilityHost.Todo.RefreshTodoNowAsync(triggerRebuild: false);
                        batchTracker.MutatedData = true;
                        needsRebuild = true;
                        evalContext = RefreshLiveEvaluationContext(evalContext);
                    }
                    else
                    {
                        skipThen = true;
                    }
                    break;
                }

                case "todo.toggle":
                case "todo.remove":
                case "todo.cyclepriority":
                {
                    string? itemId = ResolveTodoItemId(action, evalContext);
                    if (string.IsNullOrWhiteSpace(itemId))
                    {
                        skipThen = true;
                        break;
                    }

                    bool changed = actionType.ToLowerInvariant() switch
                    {
                        "todo.toggle" => await CustomWidgetHostSnapshotService.ToggleTodoFromCustomWidgetAsync(_vm.SettingsServiceInternal, itemId),
                        "todo.remove" => await CustomWidgetHostSnapshotService.RemoveTodoFromCustomWidgetAsync(_vm.SettingsServiceInternal, itemId),
                        _ => await CustomWidgetHostSnapshotService.CycleTodoPriorityFromCustomWidgetAsync(_vm.SettingsServiceInternal, itemId)
                    };
                    if (changed)
                    {
                        await _vm.CapabilityHost.Todo.RefreshTodoNowAsync(triggerRebuild: false);
                        batchTracker.MutatedData = true;
                        needsRebuild = true;
                        evalContext = RefreshLiveEvaluationContext(evalContext);
                    }
                    else
                    {
                        skipThen = true;
                    }
                    break;
                }

                case "todo.clearcompleted":
                {
                    string? dueIsoFilter = ResolveTodoArg(
                        action,
                        evalContext,
                        action.DueIso ?? action.Value,
                        "dueIsoFilter", "dueIso", "dueDate", "due");
                    int cleared = await CustomWidgetHostSnapshotService.ClearCompletedTodosFromCustomWidgetAsync(
                        _vm.SettingsServiceInternal,
                        dueIsoFilter);
                    if (cleared > 0)
                    {
                        await _vm.CapabilityHost.Todo.RefreshTodoNowAsync(triggerRebuild: false);
                        batchTracker.MutatedData = true;
                        needsRebuild = true;
                        evalContext = RefreshLiveEvaluationContext(evalContext);
                    }
                    break;
                }

                case "todo.update":
                {
                    string? itemId = ResolveTodoItemId(action, evalContext);
                    if (string.IsNullOrWhiteSpace(itemId))
                    {
                        skipThen = true;
                        break;
                    }

                    string? title = ResolveTodoArg(
                        action,
                        evalContext,
                        action.Title ?? action.Text,
                        "title", "text");
                    string? dueIso = ResolveTodoArg(
                        action,
                        evalContext,
                        action.DueIso,
                        "dueIso", "dueDate", "due");
                    string? priority = ResolveTodoArg(
                        action,
                        evalContext,
                        action.Priority ?? action.Mode,
                        "priority");
                    bool changed = await CustomWidgetHostSnapshotService.UpdateTodoFromCustomWidgetAsync(
                        _vm.SettingsServiceInternal,
                        itemId,
                        title,
                        dueIso,
                        priority);
                    if (changed)
                    {
                        await _vm.CapabilityHost.Todo.RefreshTodoNowAsync(triggerRebuild: false);
                        batchTracker.MutatedData = true;
                        needsRebuild = true;
                        evalContext = RefreshLiveEvaluationContext(evalContext);
                    }
                    else
                    {
                        skipThen = true;
                    }
                    break;
                }

                case "todo.rolloveroverdue":
                {
                    string? targetDueIso = ResolveTodoArg(
                        action,
                        evalContext,
                        action.DueIso ?? action.Value,
                        "targetDueIso", "dueIso", "dueDate", "value");
                    int rolled = await CustomWidgetHostSnapshotService.RolloverOverdueTodosFromCustomWidgetAsync(
                        _vm.SettingsServiceInternal,
                        targetDueIso);
                    if (rolled > 0)
                    {
                        await _vm.CapabilityHost.Todo.RefreshTodoNowAsync(triggerRebuild: false);
                        batchTracker.MutatedData = true;
                        needsRebuild = true;
                        evalContext = RefreshLiveEvaluationContext(evalContext);
                    }
                    else
                    {
                        skipThen = true;
                    }
                    break;
                }

                case "ai.selectprovider":
                {
                    string providerId = CustomWidgetExpressionEvaluator.Interpolate(
                        action.Value ?? action.TargetId,
                        evalContext);
                    if (!string.IsNullOrWhiteSpace(providerId))
                    {
                        _vm.StateInternal[CustomWidgetAiMonitorEngine.KeySelectedProvider] = providerId.Trim();
                        needsSave = true;
                        needsRebuild = true;
                    }
                    break;
                }

                case "ai.refresh":
                {
                    _ = _vm.RefreshAiMonitorNowAsync(showToast: true);
                    break;
                }

                case "ai.run":
                {
                    (bool aiOk, bool aiMutated, IReadOnlyList<CustomWidgetActionSpec>? aiDispatched) =
                        await _vm.ExecuteAiRunActionAsync(action, evalContext);
                    if (aiMutated)
                    {
                        needsSave = true;
                        needsRebuild = true;
                        evalContext = RefreshLiveEvaluationContext(evalContext);
                    }
                    if (aiDispatched is { Count: > 0 })
                    {
                        await ExecuteActionsAsync(aiDispatched, evalContext, isNested: true, depth: depth + 1);
                        evalContext = RefreshLiveEvaluationContext(evalContext);
                    }
                    if (!aiOk)
                    {
                        skipThen = true;
                    }
                    break;
                }

                case "script.run":
                {
                    batchTracker.RanScript = true;
                    string fnName = (action.Fn ?? action.Key ?? action.TargetId ?? string.Empty).Trim();
                    string? resolvedArg = action.Arg is not null
                        ? CustomWidgetExpressionEvaluator.Interpolate(action.Arg, evalContext)
                        : action.Value is not null
                            ? CustomWidgetExpressionEvaluator.Interpolate(action.Value, evalContext)
                            : action.CommandArgs is { Count: > 0 }
                                ? CustomWidgetExpressionEvaluator.Interpolate(action.CommandArgs[0], evalContext)
                                : null;

                    Dictionary<string, string>? resolvedArgs = null;
                    if (action.Args is { Count: > 0 })
                    {
                        resolvedArgs = new Dictionary<string, string>(action.Args.Count, StringComparer.Ordinal);
                        foreach ((string ak, string av) in action.Args)
                        {
                            if (!string.IsNullOrWhiteSpace(ak))
                            {
                                resolvedArgs[ak.Trim()] = CustomWidgetExpressionEvaluator.Interpolate(av, evalContext);
                            }
                        }
                    }

                    Dictionary<string, string>? computedBefore = SnapshotComputedRaw(_vm.ComputedInternal);
                    bool scriptOk = _vm.InvokeScriptCore(
                        functionName: fnName,
                        arg: resolvedArg,
                        eventValue: evalContext.EventValue,
                        currentItem: evalContext.CurrentItem,
                        dropFile: evalContext.DropFile,
                        sourceId: null,
                        rawSourcePayload: null,
                        timeoutMs: CustomWidgetScriptEngine.DefaultActionTimeoutMs,
                        requireExport: true,
                        allowCopy: true,
                        out bool scriptMutatedPersisted,
                        out bool scriptMutatedUi,
                        out bool scriptMutatedStructuralUi,
                        out IReadOnlyList<CustomWidgetActionSpec>? scriptDispatched,
                        args: resolvedArgs,
                        eventId: evalContext.EventId,
                        eventX: evalContext.EventX,
                        eventY: evalContext.EventY,
                        fromIndex: evalContext.EventFromIndex,
                        toIndex: evalContext.EventToIndex,
                        callKind: CustomWidgetScriptCallKind.UiEvent,
                        dropFiles: evalContext.DropFiles,
                        eventNormX: evalContext.EventNormX,
                        eventNormY: evalContext.EventNormY,
                        eventIsPressed: evalContext.EventIsPressed,
                        eventKind: evalContext.EventDropKind,
                        eventText: evalContext.EventText,
                        eventItems: evalContext.EventDropItems,
                        argsJson: action.ArgsJson);

                    if (!scriptOk)
                    {
                        skipThen = true;
                        if (!_vm.IsPreviewOnlyInternal && _vm.LastScriptError is { } err)
                        {
                            _vm.RaiseFeedback(err.ErrorMessage, WidgetFeedbackSeverity.Warning);
                        }
                    }
                    else
                    {
                        if (string.Equals(fnName, "onInit", StringComparison.OrdinalIgnoreCase) ||
                            HasComputedChanged(computedBefore, _vm.ComputedInternal))
                        {
                            batchTracker.MutatedComputed = true;
                        }
                        if (scriptMutatedPersisted)
                        {
                            needsSave = true;
                        }
                        if (scriptMutatedStructuralUi || scriptMutatedPersisted)
                        {
                            needsRebuild = true;
                        }
                        else if (scriptMutatedUi)
                        {
                            needsLiveRefreshOnly = true;
                        }

                        evalContext = RefreshLiveEvaluationContext(evalContext);
                        if (scriptDispatched is { Count: > 0 })
                        {
                            if (scriptMutatedStructuralUi || scriptMutatedPersisted || scriptMutatedUi)
                            {
                                _vm.RefreshCapsuleProjection(bumpLiveState: scriptMutatedPersisted);
                                _vm.NotifyViewRebuildRequested();
                            }

                            await ExecuteActionsCoreAsync(scriptDispatched, evalContext, isNested: true, depth: depth + 1, batchTracker);
                            evalContext = RefreshLiveEvaluationContext(evalContext);
                        }
                    }
                    break;
                }

                case "http.request":
                {
                    var (httpOk, httpMutated, httpDispatched) =
                        await ExecuteInteractiveHttpRequestActionAsync(action, evalContext);
                    if (httpMutated)
                    {
                        needsSave = true;
                        needsRebuild = true;
                        evalContext = RefreshLiveEvaluationContext(evalContext);
                    }
                    if (httpDispatched is { Count: > 0 })
                    {
                        await ExecuteActionsAsync(httpDispatched, evalContext, isNested: true, depth: depth + 1);
                        evalContext = RefreshLiveEvaluationContext(evalContext);
                    }
                    if (!httpOk)
                    {
                        skipThen = true;
                    }
                    break;
                }

                case "file.writetext":
                {
                    var (fwOk, fwMutated, fwDispatched) = await _vm.ExecuteFileWriteTextActionAsync(action, evalContext);
                    if (fwMutated)
                    {
                        needsSave = true;
                        needsRebuild = true;
                        evalContext = RefreshLiveEvaluationContext(evalContext);
                    }
                    if (fwDispatched is { Count: > 0 })
                    {
                        await ExecuteActionsAsync(fwDispatched, evalContext, isNested: true, depth: depth + 1);
                        evalContext = RefreshLiveEvaluationContext(evalContext);
                    }
                    if (!fwOk)
                    {
                        skipThen = true;
                    }
                    break;
                }

                case "file.listdir":
                {
                    var (lsOk, lsMutated, lsDispatched) = await _vm.ExecuteFileListDirActionAsync(action, evalContext);
                    if (lsMutated)
                    {
                        needsSave = true;
                        needsRebuild = true;
                        evalContext = RefreshLiveEvaluationContext(evalContext);
                    }
                    if (lsDispatched is { Count: > 0 })
                    {
                        await ExecuteActionsAsync(lsDispatched, evalContext, isNested: true, depth: depth + 1);
                        evalContext = RefreshLiveEvaluationContext(evalContext);
                    }
                    if (!lsOk)
                    {
                        skipThen = true;
                    }
                    break;
                }

                case "file.manage":
                {
                    var (fmOk, fmMutated, fmDispatched) = await _vm.ExecuteFileManageActionAsync(action, evalContext);
                    if (fmMutated)
                    {
                        needsSave = true;
                        needsRebuild = true;
                        evalContext = RefreshLiveEvaluationContext(evalContext);
                    }
                    if (fmDispatched is { Count: > 0 })
                    {
                        await ExecuteActionsAsync(fmDispatched, evalContext, isNested: true, depth: depth + 1);
                        evalContext = RefreshLiveEvaluationContext(evalContext);
                    }
                    if (!fmOk)
                    {
                        skipThen = true;
                    }
                    break;
                }

                case "clipboard.captureimage":
                {
                    var (capOk, capMutated, capFile, capDispatched) = await _vm.ExecuteClipboardCaptureImageActionAsync(action, evalContext);
                    if (capFile is not null)
                    {
                        evalContext = evalContext.WithDrop(capFile, [capFile]);
                    }
                    if (capMutated)
                    {
                        needsSave = true;
                        needsRebuild = true;
                        evalContext = RefreshLiveEvaluationContext(evalContext);
                    }
                    if (capDispatched is { Count: > 0 })
                    {
                        await ExecuteActionsAsync(capDispatched, evalContext, isNested: true, depth: depth + 1);
                        evalContext = RefreshLiveEvaluationContext(evalContext);
                    }
                    if (!capOk)
                    {
                        skipThen = true;
                    }
                    break;
                }

                case "job.cancel":
                case "command.cancel":
                {
                    string targetJobKey = CustomWidgetExpressionEvaluator.Interpolate(
                        action.JobKey ?? action.Id ?? action.TargetId ?? action.Key ?? action.Value,
                        evalContext).Trim();
                    int cancelledCount = _vm.JobBusInternal.Cancel(targetJobKey);
                    if ((string.IsNullOrWhiteSpace(targetJobKey) ||
                         string.Equals(targetJobKey, "*", StringComparison.Ordinal) ||
                         string.Equals(targetJobKey, "ai", StringComparison.OrdinalIgnoreCase)) &&
                        _vm.TryCancelPendingAiAction())
                    {
                        cancelledCount++;
                    }

                    _vm.SyncFlatStateValue("lastCancelledCount", cancelledCount.ToString(CultureInfo.InvariantCulture));
                    needsRebuild = true;
                    break;
                }

                case "command.run":
                {
                    var (cmdOk, cmdMutated, cmdDispatched) =
                        await _vm.ExecuteCommandRunActionAsync(action, evalContext);
                    if (cmdMutated)
                    {
                        needsSave = true;
                        needsRebuild = true;
                        evalContext = RefreshLiveEvaluationContext(evalContext);
                    }
                    if (cmdDispatched is { Count: > 0 })
                    {
                        await ExecuteActionsAsync(cmdDispatched, evalContext, isNested: true, depth: depth + 1);
                        evalContext = RefreshLiveEvaluationContext(evalContext);
                    }
                    if (!cmdOk)
                    {
                        skipThen = true;
                    }
                    break;
                }
            }

            if (!skipThen && action.Then is { Count: > 0 })
            {
                await ExecuteActionsCoreAsync(action.Then, evalContext, isNested: true, depth: depth + 1, batchTracker);
            }
        }

        if (!isNested &&
            batchTracker.MutatedData &&
            (!batchTracker.RanScript || !batchTracker.MutatedComputed) &&
            _vm.CompiledScriptInternal is not null &&
            _vm.CompiledScriptInternal.ExportedFunctions.Contains("onInit"))
        {
            _vm.TryRunScriptLifecycleHook("onInit", CustomWidgetScriptEngine.DefaultInitOrSourceTimeoutMs, persistIfMutated: false);
            needsRebuild = true;
        }

        bool dynamicNetworkUrlChanged = false;
        if (!isNested && networkKeysBefore is { Count: > 0 })
        {
            foreach ((int idx, string prevKey) in networkKeysBefore)
            {
                if (idx >= 0 && idx < _vm.ManifestInternal.Sources.Count)
                {
                    CustomWidgetSourceSpec src = _vm.ManifestInternal.Sources[idx];
                    string nextKey = _vm.BuildRuntimeNetworkSourceKey(src);
                    if (!string.Equals(prevKey, nextKey, StringComparison.Ordinal))
                    {
                        dynamicNetworkUrlChanged = true;
                        if (_vm.CapabilityHost.Network.NeedsFetchAfter(nextKey, actionBatchStartTicks))
                        {
                            _vm.HydrateSingleNetworkSourceFromDiskCache(
                                src,
                                _vm.ResolveNetworkSourceUrl(src),
                                overwriteExistingCollection: true);
                        }
                    }
                }
            }

            if (dynamicNetworkUrlChanged)
            {
                needsRebuild = true;
            }
        }

        bool weatherCityChanged = weatherCityBefore is not null &&
            !string.Equals(weatherCityBefore, _vm.ResolveEffectiveWeatherCity(), StringComparison.OrdinalIgnoreCase);

        if (needsSave)
        {
            await _vm.PersistStoreAsync();
        }

        if (batchTracker.MutatedData)
        {
            _vm.BumpCollectionsVersion();
        }

        if (needsRebuild)
        {
            _vm.RefreshCapsuleProjection(bumpLiveState: true);
            _vm.NotifyViewRebuildRequested();
        }
        else if (needsLiveRefreshOnly)
        {
            _vm.RefreshCapsuleProjection(bumpLiveState: true);
            _vm.NotifyMetricsRefreshed();
        }

        if (dynamicNetworkUrlChanged && !_vm.IsDisposedInternal)
        {
            _ = _vm.RefreshDueNetworkSourcesAsync();
        }

        if (weatherCityChanged && !_vm.IsDisposedInternal)
        {
            _ = _vm.RefreshWeatherNowAsync(forceRefresh: false, isManualUserAction: false);
        }
    }


    internal CustomWidgetEvaluationContext RefreshLiveEvaluationContext(CustomWidgetEvaluationContext? previous)
    {
        CustomWidgetEvaluationContext fresh = _vm.CreateEvaluationContext();
        if (previous is null)
        {
            return fresh;
        }

        if (previous.EventX.HasValue && previous.EventY.HasValue)
        {
            fresh = fresh.WithPointerEvent(
                previous.EventId,
                previous.EventX.Value,
                previous.EventY.Value,
                previous.EventNormX,
                previous.EventNormY,
                previous.EventIsPressed);
        }
        if (previous.EventFromIndex.HasValue && previous.EventToIndex.HasValue)
        {
            fresh = fresh.WithReorderEvent(previous.EventFromIndex.Value, previous.EventToIndex.Value);
        }
        if (previous.EventValue is not null)
        {
            fresh = fresh.WithEventValue(previous.EventValue);
        }
        if (previous.CurrentItem is not null)
        {
            fresh = fresh.WithItem(previous.CurrentItem, previous.CurrentIndex);
        }
        if (previous.DropFile is not null || previous.DropFiles is { Count: > 0 })
        {
            fresh = fresh.WithDrop(previous.DropFile, previous.DropFiles);
        }
        if (previous.EventDropKind is not null ||
            previous.EventText is not null ||
            previous.EventDropItems is not null)
        {
            fresh = fresh.WithStructuredDrop(
                previous.EventDropKind,
                previous.EventText,
                previous.EventDropItems,
                previous.DropFile,
                previous.DropFiles);
        }
        if (previous.TransformResult is not null)
        {
            fresh = fresh.WithTransform(previous.TransformResult);
        }
        if (previous.IsPairedSlot)
        {
            fresh = fresh.WithPairedSlot(true);
        }

        return fresh;
    }


    internal bool ExecuteCollectionReorder(
        CustomWidgetActionSpec action,
        CustomWidgetEvaluationContext evalContext)
    {
        string colName = (action.Collection ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(colName) ||
            !_vm.CollectionsInternal.TryGetValue(colName, out List<CustomWidgetRecord>? list) ||
            list.Count < 2)
        {
            return false;
        }

        int fromIdx = -1;
        int toIdx = -1;

        if (!string.IsNullOrWhiteSpace(action.FromIndex))
        {
            int.TryParse(
                CustomWidgetExpressionEvaluator.Interpolate(action.FromIndex, evalContext).Trim(),
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out fromIdx);
        }
        else if (evalContext.EventFromIndex.HasValue)
        {
            fromIdx = evalContext.EventFromIndex.Value;
        }

        if (!string.IsNullOrWhiteSpace(action.ToIndex))
        {
            int.TryParse(
                CustomWidgetExpressionEvaluator.Interpolate(action.ToIndex, evalContext).Trim(),
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out toIdx);
        }
        else if (evalContext.EventToIndex.HasValue)
        {
            toIdx = evalContext.EventToIndex.Value;
        }

        if (fromIdx < 0 || fromIdx >= list.Count || toIdx < 0 || toIdx >= list.Count || fromIdx == toIdx)
        {
            return false;
        }

        CustomWidgetRecord moving = list[fromIdx];
        list.RemoveAt(fromIdx);
        list.Insert(toIdx, moving);
        return true;
    }

    internal async Task<bool> ReorderCollectionItemsAsync(
        string collectionName,
        int fromIndex,
        int toIndex,
        IReadOnlyList<CustomWidgetActionSpec>? onReorderActions = null)
    {
        if (_vm.IsDisposedInternal || string.IsNullOrWhiteSpace(collectionName))
        {
            return false;
        }

        if (!_vm.CollectionsInternal.TryGetValue(collectionName.Trim(), out List<CustomWidgetRecord>? list) ||
            fromIndex < 0 || fromIndex >= list.Count ||
            toIndex < 0 || toIndex >= list.Count ||
            fromIndex == toIndex)
        {
            return false;
        }

        CustomWidgetRecord moving = list[fromIndex];
        list.RemoveAt(fromIndex);
        list.Insert(toIndex, moving);
        _vm.BumpCollectionsVersion();

        if (onReorderActions is { Count: > 0 })
        {
            CustomWidgetEvaluationContext ctx = _vm.CreateEvaluationContext()
                .WithItem(moving, toIndex)
                .WithReorderEvent(fromIndex, toIndex);
            await ExecuteActionsAsync(onReorderActions, ctx);
        }

        await _vm.PersistStoreAsync();
        _vm.RefreshCapsuleProjection(bumpLiveState: true);
        _vm.NotifyViewRebuildRequested();
        return true;
    }

    internal async Task<bool> CommitInlineItemEditAsync(
        string collectionName,
        string itemId,
        string fieldName,
        string newValue,
        IReadOnlyList<CustomWidgetActionSpec>? onInlineCommitActions = null)
    {
        if (_vm.IsDisposedInternal ||
            string.IsNullOrWhiteSpace(itemId) ||
            string.IsNullOrWhiteSpace(fieldName))
        {
            return false;
        }

        string normalizedField = fieldName.Trim();
        CustomWidgetRecord? target = null;
        int idx = -1;
        bool mutatedLocalCollection = false;

        if (!string.IsNullOrWhiteSpace(collectionName) &&
            _vm.CollectionsInternal.TryGetValue(collectionName.Trim(), out List<CustomWidgetRecord>? list))
        {
            idx = list.FindIndex(r => string.Equals(r.Id, itemId, StringComparison.Ordinal));
            if (idx >= 0)
            {
                target = list[idx];
                target.Fields[normalizedField] = newValue ?? string.Empty;
                target.UpdatedAtUtcTicks = DateTimeOffset.UtcNow.UtcTicks;
                mutatedLocalCollection = true;
            }
        }

        if (target is null)
        {
            if (onInlineCommitActions is not { Count: > 0 })
            {
                return false;
            }

            target = new CustomWidgetRecord
            {
                Id = itemId,
                UpdatedAtUtcTicks = DateTimeOffset.UtcNow.UtcTicks
            };
            target.Fields[normalizedField] = newValue ?? string.Empty;
            idx = 0;
        }

        if (onInlineCommitActions is { Count: > 0 })
        {
            CustomWidgetEvaluationContext ctx = _vm.CreateEvaluationContext()
                .WithItem(target, idx)
                .WithEventValue(newValue);
            await ExecuteActionsAsync(onInlineCommitActions, ctx);
        }

        if (mutatedLocalCollection)
        {
            _vm.BumpCollectionsVersion();
            await _vm.PersistStoreAsync();
        }
        _vm.RefreshCapsuleProjection(bumpLiveState: true);
        _vm.NotifyViewRebuildRequested();
        return true;
    }

    internal async Task<(bool Succeeded, bool Mutated, IReadOnlyList<CustomWidgetActionSpec>? Dispatched)> ExecuteInteractiveHttpRequestActionAsync(
        CustomWidgetActionSpec action,
        CustomWidgetEvaluationContext evalContext)
    {
        CustomWidgetEvaluationContext secretAwareCtx = evalContext.WithAllowSecrets(true);
        string resolvedUrl = CustomWidgetExpressionEvaluator.Interpolate(action.Url ?? action.Value, secretAwareCtx).Trim();
        string method = string.IsNullOrWhiteSpace(action.Method)
            ? "GET"
            : CustomWidgetExpressionEvaluator.Interpolate(action.Method, evalContext).Trim().ToUpperInvariant();

        if (!CustomWidgetNetworkSecurity.TryValidateWidgetNetworkUrl(
                resolvedUrl,
                allowLocalAndHttp: true,
                out Uri? targetUri,
                out bool requiresLocal,
                out string? urlErr) ||
            targetUri is null)
        {
            _vm.StateInternal["httpOk"] = "false";
            _vm.StateInternal["httpError"] = urlErr ?? "Invalid URL.";
            _vm.RaiseFeedback(urlErr ?? "Invalid URL.", WidgetFeedbackSeverity.Warning);
            return (false, true, null);
        }

        CustomWidgetPermissionKind requiredPerm = requiresLocal
            ? CustomWidgetPermissionKind.LocalNetwork
            : CustomWidgetPermissionKind.PublicNetwork;
        bool isReadOnlyGet = !requiresLocal && string.Equals(method, "GET", StringComparison.OrdinalIgnoreCase);
        bool wasAlreadyPermitted = _vm.IsPermissionAllowed(requiredPerm, isReadOnlyPublicGet: isReadOnlyGet);

        bool granted = await _vm.EnsureWidgetPermissionGrantedAsync(
            requiredPerm,
            actionKind: "http.request",
            targetDisplay: targetUri.AbsoluteUri,
            isReadOnlyPublicGet: isReadOnlyGet);
        if (!granted)
        {
            _vm.StateInternal["httpOk"] = "false";
            _vm.StateInternal["httpError"] = "Permission denied.";
            return (false, true, null);
        }

        CustomWidgetTrustProfile currentTrust = _vm.GetTrustProfile();
        if (!requiresLocal && !currentTrust.TrustedLocalAuthoring && wasAlreadyPermitted)
        {
            bool hostApproved = await _vm.EnsureCommunityExternalTargetApprovedAsync(
                actionKind: "http.request",
                trustToken: $"host:{targetUri.DnsSafeHost.ToLowerInvariant()}",
                targetDisplay: targetUri.AbsoluteUri);
            if (!hostApproved)
            {
                _vm.StateInternal["httpOk"] = "false";
                _vm.StateInternal["httpError"] = "External host not approved.";
                return (false, true, null);
            }
        }

        Dictionary<string, string>? headers = null;
        if (action.Headers is { Count: > 0 })
        {
            headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach ((string hk, string hv) in action.Headers)
            {
                if (!string.IsNullOrWhiteSpace(hk))
                {
                    string val = CustomWidgetExpressionEvaluator.Interpolate(hv, secretAwareCtx).Trim();
                    if (!string.IsNullOrEmpty(val))
                    {
                        headers[hk.Trim()] = val;
                    }
                }
            }
        }

        string? body = action.Body is not null
            ? CustomWidgetExpressionEvaluator.Interpolate(action.Body, secretAwareCtx)
            : (!string.Equals(method, "GET", StringComparison.OrdinalIgnoreCase) &&
               !string.Equals(method, "HEAD", StringComparison.OrdinalIgnoreCase) &&
               action.Text is not null
                ? CustomWidgetExpressionEvaluator.Interpolate(action.Text, secretAwareCtx)
                : null);

        Func<Uri, CancellationToken, Task<bool>>? redirectHostApprover =
            !currentTrust.TrustedLocalAuthoring
                ? (redirectUri, _) => _vm.EnsureCommunityExternalTargetApprovedAsync(
                    actionKind: "http.request",
                    trustToken: $"host:{redirectUri.DnsSafeHost.ToLowerInvariant()}",
                    targetDisplay: redirectUri.AbsoluteUri)
                : null;

        string reqId = !string.IsNullOrWhiteSpace(action.Id)
            ? CustomWidgetExpressionEvaluator.Interpolate(action.Id, evalContext).Trim()
            : (action.TargetId ?? action.Key ?? method);
        string reqJobKey = !string.IsNullOrWhiteSpace(action.JobKey)
            ? CustomWidgetExpressionEvaluator.Interpolate(action.JobKey, evalContext).Trim()
            : reqId;
        using CustomWidgetJobBus.ActiveJobHandle httpJob = _vm.JobBusInternal.BeginJob(reqJobKey, reqId, avoidSupersede: string.IsNullOrWhiteSpace(action.JobKey));

        CustomWidgetInteractiveHttpOutcome outcome;
        try
        {
            outcome = await _vm.NetworkSourceEngineInternal.SendInteractiveHttpRequestAsync(
                resolvedUrl: targetUri.AbsoluteUri,
                method: method,
                resolvedHeaders: headers,
                body: body,
                timeoutMs: action.TimeoutMs > 0 ? action.TimeoutMs : 12_000,
                allowLocalAndHttp: requiresLocal || _vm.IsPermissionAllowed(CustomWidgetPermissionKind.LocalNetwork),
                stateMap: action.StateMap,
                targetCollection: action.Collection,
                itemsPath: action.ItemsPath,
                fieldMap: action.FieldMap,
                ct: httpJob.Token,
                crossOriginHostApprover: redirectHostApprover);
        }
        catch (OperationCanceledException) when (httpJob.IsCancellationRequested)
        {
            outcome = new CustomWidgetInteractiveHttpOutcome(
                false,
                0,
                "Cancelled",
                "Cancelled",
                string.Empty,
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                new Dictionary<string, JsonElement>(StringComparer.Ordinal),
                []);
        }

        if (_vm.IsDisposedInternal)
        {
            return (false, false, null);
        }

        bool mutated = true;
        _vm.SyncFlatStateValue("httpStatus", outcome.StatusCode.ToString(CultureInfo.InvariantCulture));
        _vm.SyncFlatStateValue("httpOk", outcome.Succeeded ? "true" : "false");
        _vm.SyncFlatStateValue("httpError", outcome.ErrorMessage ?? string.Empty);

        foreach ((string sk, JsonElement sv) in outcome.StateUpdates)
        {
            _vm.SetStructuredStateValue(sk, sv);
        }

        if (!string.IsNullOrWhiteSpace(action.Key))
        {
            string outKey = CustomWidgetViewModel.NormalizeStateKey(action.Key);
            if (!string.IsNullOrWhiteSpace(outKey))
            {
                _vm.SyncFlatStateValue(outKey, outcome.ResponseBody ?? string.Empty);
            }
        }

        if (!string.IsNullOrWhiteSpace(action.Collection) &&
            outcome.Succeeded &&
            (outcome.Items.Count > 0 ||
             !string.IsNullOrWhiteSpace(action.ItemsPath) ||
             (outcome.ResponseBody?.TrimStart().StartsWith('[') == true)))
        {
            int maxItems = _vm.ManifestInternal.Collections.TryGetValue(action.Collection, out CustomWidgetCollectionSchema? schema)
                ? schema.MaxItems
                : 200;
            _vm.CollectionsInternal[action.Collection] = outcome.Items.Take(maxItems).ToList();
            _vm.BumpCollectionsVersion();
        }

        IReadOnlyList<CustomWidgetActionSpec>? dispatched = null;
        string callbackFn = !string.IsNullOrWhiteSpace(action.OnComplete)
            ? action.OnComplete!.Trim()
            : (!string.IsNullOrWhiteSpace(action.Fn) ? action.Fn!.Trim() : "onHttpResponse");
        if (_vm.CompiledScriptInternal is not null && _vm.CompiledScriptInternal.ExportedFunctions.Contains(callbackFn))
        {
            string callbackJson = CustomWidgetJobBus.BuildHttpResultPayloadJson(
                httpJob.Id,
                httpJob.JobKey,
                outcome,
                durationMs: httpJob.ElapsedMs,
                timedOut: outcome.TimedOut,
                cancelled: httpJob.IsCancellationRequested);
            _vm.InvokeScriptCore(
                functionName: callbackFn,
                arg: httpJob.Id,
                eventValue: evalContext.EventValue,
                currentItem: evalContext.CurrentItem,
                dropFile: evalContext.DropFile,
                sourceId: httpJob.Id,
                rawSourcePayload: callbackJson,
                timeoutMs: CustomWidgetScriptEngine.DefaultActionTimeoutMs,
                requireExport: false,
                allowCopy: true,
                out _,
                out _,
                out _,
                out dispatched,
                callKind: CustomWidgetScriptCallKind.JobResult,
                dropFiles: evalContext.DropFiles,
                rawJsonPayload: callbackJson);
        }

        if (!outcome.Succeeded && !httpJob.IsCancellationRequested && !string.IsNullOrWhiteSpace(outcome.ErrorMessage))
        {
            _vm.RaiseFeedback(outcome.ErrorMessage, WidgetFeedbackSeverity.Warning);
        }

        return (outcome.Succeeded, mutated, dispatched);
    }

    private static string ApplyEvaluatedItemField(
        CustomWidgetRecord record,
        string fieldKey,
        string? rawTemplate,
        IReadOnlyDictionary<string, JsonElement>? itemFieldsJson,
        CustomWidgetEvaluationContext evalContext)
    {
        string template = rawTemplate ?? string.Empty;
        if (itemFieldsJson is not null &&
            itemFieldsJson.TryGetValue(fieldKey, out JsonElement rawJson) &&
            !template.Contains("${", StringComparison.Ordinal))
        {
            record.FieldsJson[fieldKey] = rawJson.Clone();
            return CustomWidgetScriptEngine.JsonElementToDisplayString(rawJson);
        }

        string interpolated = CustomWidgetExpressionEvaluator.Interpolate(template, evalContext);
        string trimmed = template.Trim();
        if (trimmed.StartsWith("${", StringComparison.Ordinal) &&
            trimmed.EndsWith('}') &&
            trimmed.IndexOf('}', 2) == trimmed.Length - 1)
        {
            string inner = trimmed[2..^1].Trim();
            if (TryResolveDirectStructuredToken(inner, evalContext, out JsonElement directEl))
            {
                record.FieldsJson[fieldKey] = directEl.Clone();
                return interpolated;
            }

            if (inner.StartsWith('!') &&
                (string.Equals(interpolated, "true", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(interpolated, "false", StringComparison.OrdinalIgnoreCase)))
            {
                record.FieldsJson[fieldKey] = CustomWidgetScriptEngine.CreateBooleanJsonElement(
                    string.Equals(interpolated, "true", StringComparison.OrdinalIgnoreCase));
                return interpolated;
            }
        }

        record.Fields[fieldKey] = interpolated;
        return interpolated;
    }

    private static bool TryResolveDirectStructuredToken(
        string expr,
        CustomWidgetEvaluationContext evalContext,
        out JsonElement resolved)
    {
        resolved = default;
        if (expr.StartsWith("$state.", StringComparison.Ordinal))
        {
            return evalContext.StateJson.TryGetValue(expr["$state.".Length..], out resolved) &&
                   resolved.ValueKind != JsonValueKind.Undefined;
        }
        if (expr.StartsWith("state.", StringComparison.Ordinal))
        {
            return evalContext.StateJson.TryGetValue(expr["state.".Length..], out resolved) &&
                   resolved.ValueKind != JsonValueKind.Undefined;
        }
        if (expr.StartsWith("$item.", StringComparison.Ordinal) && evalContext.CurrentItem is not null)
        {
            return evalContext.CurrentItem.FieldsJson.TryGetValue(expr["$item.".Length..], out resolved) &&
                   resolved.ValueKind != JsonValueKind.Undefined;
        }
        if (expr.StartsWith("$computed.", StringComparison.Ordinal))
        {
            return evalContext.Computed.TryGetValue(expr["$computed.".Length..], out resolved) &&
                   resolved.ValueKind != JsonValueKind.Undefined;
        }
        if (expr.StartsWith("computed.", StringComparison.Ordinal))
        {
            return evalContext.Computed.TryGetValue(expr["computed.".Length..], out resolved) &&
                   resolved.ValueKind != JsonValueKind.Undefined;
        }
        return evalContext.StateJson.TryGetValue(expr, out resolved) &&
               resolved.ValueKind != JsonValueKind.Undefined;
    }

    private static string? ResolveTodoArg(
        CustomWidgetActionSpec action,
        CustomWidgetEvaluationContext evalContext,
        string? directRaw,
        params string[] altKeys)
    {
        if (!string.IsNullOrWhiteSpace(directRaw))
        {
            string val = CustomWidgetExpressionEvaluator.Interpolate(directRaw, evalContext).Trim();
            return string.IsNullOrWhiteSpace(val) ? null : val;
        }

        if (action.Args is { Count: > 0 })
        {
            foreach (string key in altKeys)
            {
                foreach ((string ak, string av) in action.Args)
                {
                    if (string.Equals(ak, key, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(av))
                    {
                        string val = CustomWidgetExpressionEvaluator.Interpolate(av, evalContext).Trim();
                        if (!string.IsNullOrWhiteSpace(val))
                        {
                            return val;
                        }
                    }
                }
            }
        }

        if (action.ItemFields is { Count: > 0 })
        {
            foreach (string key in altKeys)
            {
                foreach ((string fk, string fv) in action.ItemFields)
                {
                    if (string.Equals(fk, key, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(fv))
                    {
                        string val = CustomWidgetExpressionEvaluator.Interpolate(fv, evalContext).Trim();
                        if (!string.IsNullOrWhiteSpace(val))
                        {
                            return val;
                        }
                    }
                }
            }
        }

        return null;
    }

    private static string? ResolveTodoItemId(
        CustomWidgetActionSpec action,
        CustomWidgetEvaluationContext evalContext)
    {
        string? resolved = ResolveTodoArg(
            action,
            evalContext,
            action.TargetId ?? action.Id ?? action.Key ?? action.Value,
            "targetId", "id", "itemId", "key", "value");
        if (!string.IsNullOrWhiteSpace(resolved))
        {
            return resolved;
        }

        string? itemId = evalContext.CurrentItem?.Id;
        return string.IsNullOrWhiteSpace(itemId) ? null : itemId.Trim();
    }

    private static Dictionary<string, string>? SnapshotComputedRaw(IReadOnlyDictionary<string, JsonElement> computed)
    {
        if (computed.Count == 0)
        {
            return null;
        }

        var snapshot = new Dictionary<string, string>(computed.Count, StringComparer.Ordinal);
        foreach ((string k, JsonElement v) in computed)
        {
            snapshot[k] = v.GetRawText();
        }
        return snapshot;
    }

    private static bool HasComputedChanged(
        Dictionary<string, string>? before,
        IReadOnlyDictionary<string, JsonElement> after)
    {
        if ((before?.Count ?? 0) != after.Count)
        {
            return true;
        }

        if (before is null)
        {
            return false;
        }

        foreach ((string k, JsonElement v) in after)
        {
            if (!before.TryGetValue(k, out string? prev) ||
                !string.Equals(prev, v.GetRawText(), StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

}
