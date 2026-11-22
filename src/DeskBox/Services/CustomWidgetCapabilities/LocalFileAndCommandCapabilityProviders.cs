using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Json;
using DeskBox.Models;
using DeskBox.ViewModels;
using Microsoft.UI.Dispatching;

namespace DeskBox.Services;

internal static class LocalSourcePayloadHelper
{
    private const int MaxSourceFetchTimestampEntries = 64;

    internal static void RecordBoundedTimestamp(Dictionary<string, long> map, string key, long ticks)
    {
        if (!map.ContainsKey(key) && map.Count >= MaxSourceFetchTimestampEntries)
        {
            string? oldestKey = null;
            long oldestTicks = long.MaxValue;
            foreach ((string k, long v) in map)
            {
                if (v < oldestTicks)
                {
                    oldestTicks = v;
                    oldestKey = k;
                }
            }

            if (oldestKey is not null)
            {
                map.Remove(oldestKey);
            }
        }

        map[key] = ticks;
    }
    internal static IReadOnlyList<string> ResolveAllowedExtensionsForFolderEnumeration(
        string? rawAccept,
        CustomWidgetSettingDefinition? def)
    {
        if (!string.IsNullOrWhiteSpace(rawAccept))
        {
            string[] tokens = rawAccept.Split([',', ';', '|', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var list = new List<string>();
            foreach (string t in tokens)
            {
                if (t is "*" or "*.*")
                {
                    return [];
                }
                if (string.Equals(t, "image/*", StringComparison.OrdinalIgnoreCase))
                {
                    list.AddRange([".png", ".jpg", ".jpeg", ".webp", ".gif", ".bmp"]);
                }
                else if (string.Equals(t, "video/*", StringComparison.OrdinalIgnoreCase))
                {
                    list.AddRange([".mp4", ".webm", ".mov", ".mkv", ".avi", ".m4v"]);
                }
                else if (string.Equals(t, "audio/*", StringComparison.OrdinalIgnoreCase))
                {
                    list.AddRange([".mp3", ".wav", ".flac", ".m4a", ".ogg"]);
                }
                else
                {
                    string clean = t.StartsWith("*.", StringComparison.Ordinal) ? t[1..] : (t.StartsWith('.') ? t : "." + t);
                    if (clean.Length is >= 2 and <= 16)
                    {
                        list.Add(clean);
                    }
                }
            }

            return list;
        }

        string keyAndLabel = $"{def?.Key} {def?.Label} {def?.Placeholder}";
        if (keyAndLabel.Contains("slide", StringComparison.OrdinalIgnoreCase) ||
            keyAndLabel.Contains("media", StringComparison.OrdinalIgnoreCase) ||
            keyAndLabel.Contains("photo", StringComparison.OrdinalIgnoreCase) ||
            keyAndLabel.Contains("image", StringComparison.OrdinalIgnoreCase) ||
            keyAndLabel.Contains("video", StringComparison.OrdinalIgnoreCase) ||
            keyAndLabel.Contains("素材", StringComparison.Ordinal) ||
            keyAndLabel.Contains("媒体", StringComparison.Ordinal) ||
            keyAndLabel.Contains("图片", StringComparison.Ordinal) ||
            keyAndLabel.Contains("照片", StringComparison.Ordinal) ||
            keyAndLabel.Contains("视频", StringComparison.Ordinal))
        {
            return [".png", ".jpg", ".jpeg", ".webp", ".gif", ".bmp", ".mp4", ".webm", ".mov", ".mkv", ".m4v", ".mp3", ".wav", ".m4a", ".flac", ".ogg"];
        }

        return [];
    }

    internal static string ClassifyMediaKindByExtension(string? extWithoutDot) =>
        (extWithoutDot ?? string.Empty).TrimStart('.').ToLowerInvariant() switch
        {
            "mp4" or "webm" or "mov" or "mkv" or "avi" or "m4v" => "video",
            "mp3" or "wav" or "flac" or "m4a" or "ogg" or "aac" => "audio",
            "png" or "jpg" or "jpeg" or "webp" or "gif" or "bmp" or "svg" => "image",
            _ => "file"
        };

    internal static string? BuildGlobPatternFromExtensions(IReadOnlyList<string>? extensions)
    {
        if (extensions is not { Count: > 0 })
        {
            return null;
        }

        var globs = new List<string>();
        foreach (string raw in extensions)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                continue;
            }

            foreach (string token in raw.Split([',', ';', '|', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (token is "*" or "*.*")
                {
                    return "*";
                }
                if (string.Equals(token, "image/*", StringComparison.OrdinalIgnoreCase))
                {
                    globs.AddRange(["*.png", "*.jpg", "*.jpeg", "*.webp", "*.gif", "*.bmp"]);
                }
                else if (string.Equals(token, "video/*", StringComparison.OrdinalIgnoreCase))
                {
                    globs.AddRange(["*.mp4", "*.webm", "*.mov", "*.mkv", "*.avi", "*.m4v"]);
                }
                else if (string.Equals(token, "audio/*", StringComparison.OrdinalIgnoreCase))
                {
                    globs.AddRange(["*.mp3", "*.wav", "*.flac", "*.m4a", "*.ogg"]);
                }
                else if (token.Contains('*') || token.Contains('?'))
                {
                    globs.Add(token);
                }
                else
                {
                    globs.Add(token.StartsWith('.') ? "*" + token : "*." + token);
                }
            }
        }

        return globs.Count == 0 ? null : string.Join(';', globs);
    }


    internal static void ApplyTextOrCommandPayloadOnUi(
        CustomWidgetViewModel vm,
        
        CustomWidgetSourceSpec spec,
        string primaryStatePrefix,
        bool succeeded,
        string payloadText,
        string? errorMessage,
        Action? extraMetaSetter = null)
    {
        void Apply()
        {
            if (vm.IsDisposedInternal)
            {
                return;
            }

            if (!succeeded)
            {
                string err = string.IsNullOrWhiteSpace(errorMessage) ? "Source failed." : errorMessage.Trim();
                if (!string.IsNullOrWhiteSpace(spec.Id))
                {
                    vm.StateInternal[$"sourceStatus.{spec.Id}"] = "error";
                    vm.StateInternal[$"sourceError.{spec.Id}"] = err;
                }
                vm.StateInternal["sourceStatus"] = "error";
                vm.StateInternal["sourceError"] = err;
                vm.NotifyViewRebuildRequested();
                return;
            }

            vm.StateInternal[primaryStatePrefix] = payloadText;
            if (!string.IsNullOrWhiteSpace(spec.Id))
            {
                vm.StateInternal[$"{primaryStatePrefix}.{spec.Id}"] = payloadText;
                vm.StateInternal[$"sourceStatus.{spec.Id}"] = "ok";
                vm.StateInternal[$"sourceError.{spec.Id}"] = string.Empty;
            }
            vm.StateInternal["sourceStatus"] = "ok";
            vm.StateInternal["sourceError"] = string.Empty;
            vm.StateInternal["sourceUpdatedAt"] = DateTimeOffset.Now.ToString("HH:mm", CultureInfo.InvariantCulture);
            extraMetaSetter?.Invoke();

            ExtractTextOrJsonSourceMappings(vm, spec, payloadText);

            IReadOnlyList<CustomWidgetActionSpec>? dispatched = null;
            if (vm.CompiledScriptInternal is not null &&
                vm.CompiledScriptInternal.ExportedFunctions.Contains("onSourceData"))
            {
                vm.InvokeScriptCore(
                    functionName: "onSourceData",
                    arg: null,
                    eventValue: null,
                    currentItem: null,
                    dropFile: null,
                    sourceId: spec.Id ?? spec.Type,
                    rawSourcePayload: payloadText,
                    timeoutMs: CustomWidgetScriptEngine.DefaultInitOrSourceTimeoutMs,
                    requireExport: false,
                    allowCopy: false,
                    out bool scriptMutatedPersisted,
                    out _,
                    out _,
                    out dispatched,
                    callKind: CustomWidgetScriptCallKind.SourceData);
                if (scriptMutatedPersisted && !vm.IsPreviewOnlyInternal)
                {
                    _ = vm.PersistStoreAsync();
                }
            }

            vm.RefreshCapsuleProjection(bumpLiveState: true);
            vm.NotifyViewRebuildRequested();
            if (dispatched is { Count: > 0 })
            {
                _ = vm.ExecuteActionsAsync(dispatched, isNested: true, depth: 1);
            }
        }

        if (vm.DispatcherQueueInternal is not null && !vm.DispatcherQueueInternal.HasThreadAccess)
        {
            vm.DispatcherQueueInternal.TryEnqueue(Apply);
        }
        else
        {
            Apply();
        }
    }

    internal static void ApplyLocalFolderSourceOutcomeOnUi(
        CustomWidgetViewModel vm,
        
        CustomWidgetSourceSpec spec,
        CustomWidgetDirectoryListOutcome outcome)
    {
        void Apply()
        {
            if (vm.IsDisposedInternal)
            {
                return;
            }

            string targetCol = string.IsNullOrWhiteSpace(spec.TargetCollection) ? "items" : spec.TargetCollection;
            if (!outcome.Succeeded)
            {
                string err = string.IsNullOrWhiteSpace(outcome.ErrorMessage) ? "Folder scan failed." : outcome.ErrorMessage.Trim();
                if (!string.IsNullOrWhiteSpace(spec.Id))
                {
                    vm.StateInternal[$"sourceStatus.{spec.Id}"] = "error";
                    vm.StateInternal[$"sourceError.{spec.Id}"] = err;
                }
                vm.StateInternal["sourceStatus"] = "error";
                vm.StateInternal["sourceError"] = err;
                vm.NotifyViewRebuildRequested();
                return;
            }

            long nowTicks = DateTimeOffset.UtcNow.UtcTicks;
            var records = new List<CustomWidgetRecord>(outcome.Files.Count);
            for (int i = 0; i < outcome.Files.Count; i++)
            {
                CustomWidgetDropFileContext file = outcome.Files[i];
                string baseTitle = Path.GetFileNameWithoutExtension(file.Name);
                if (string.IsNullOrWhiteSpace(baseTitle))
                {
                    baseTitle = file.Name;
                }

                records.Add(new CustomWidgetRecord
                {
                    Id = $"folder-{i + 1}",
                    CreatedAtUtcTicks = nowTicks,
                    UpdatedAtUtcTicks = nowTicks,
                    Fields = new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["name"] = file.Name,
                        ["title"] = baseTitle,
                        ["path"] = file.Path,
                        ["filePath"] = file.Path,
                        ["src"] = file.Path,
                        ["url"] = file.Path,
                        ["ext"] = file.Extension,
                        ["kind"] = ClassifyMediaKindByExtension(file.Extension),
                        ["sizeText"] = file.SizeText,
                        ["sizeBytes"] = file.SizeBytes.ToString(CultureInfo.InvariantCulture),
                        ["modifiedIso"] = file.ModifiedIso
                    }
                });
            }

            vm.CollectionsInternal[targetCol] = records;
            string countStr = records.Count.ToString(CultureInfo.InvariantCulture);
            vm.StateInternal["folderItemCount"] = countStr;
            if (!string.IsNullOrWhiteSpace(spec.Id))
            {
                vm.StateInternal[$"folderItemCount.{spec.Id}"] = countStr;
                vm.StateInternal[$"sourceStatus.{spec.Id}"] = "ok";
                vm.StateInternal[$"sourceError.{spec.Id}"] = string.Empty;
            }
            vm.StateInternal["sourceStatus"] = "ok";
            vm.StateInternal["sourceError"] = string.Empty;
            vm.StateInternal["sourceUpdatedAt"] = DateTimeOffset.Now.ToString("HH:mm", CultureInfo.InvariantCulture);

            IReadOnlyList<CustomWidgetActionSpec>? dispatched = null;
            if (vm.CompiledScriptInternal is not null &&
                vm.CompiledScriptInternal.ExportedFunctions.Contains("onSourceData"))
            {
                string payloadJson = CustomWidgetJobBus.BuildFileListResultPayloadJson(
                    spec.Id ?? "folder",
                    spec.Id ?? "folder",
                    CustomWidgetSourceTypes.LocalFolder,
                    ok: true,
                    primaryPath: outcome.ResolvedDirectory,
                    files: outcome.Files,
                    error: null,
                    durationMs: 0);
                vm.InvokeScriptCore(
                    functionName: "onSourceData",
                    arg: outcome.ResolvedDirectory,
                    eventValue: countStr,
                    currentItem: null,
                    dropFile: null,
                    sourceId: spec.Id ?? spec.Type,
                    rawSourcePayload: payloadJson,
                    timeoutMs: CustomWidgetScriptEngine.DefaultInitOrSourceTimeoutMs,
                    requireExport: false,
                    allowCopy: false,
                    out bool scriptMutatedPersisted,
                    out _,
                    out _,
                    out dispatched,
                    callKind: CustomWidgetScriptCallKind.SourceData,
                    rawJsonPayload: payloadJson);
                if (scriptMutatedPersisted && !vm.IsPreviewOnlyInternal)
                {
                    _ = vm.PersistStoreAsync();
                }
            }

            vm.RefreshCapsuleProjection(bumpLiveState: true);
            vm.NotifyViewRebuildRequested();
            if (dispatched is { Count: > 0 })
            {
                _ = vm.ExecuteActionsAsync(dispatched, isNested: true, depth: 1);
            }
        }

        if (vm.DispatcherQueueInternal is not null && !vm.DispatcherQueueInternal.HasThreadAccess)
        {
            vm.DispatcherQueueInternal.TryEnqueue(Apply);
        }
        else
        {
            Apply();
        }
    }

    internal static void ExtractTextOrJsonSourceMappings(
        CustomWidgetViewModel vm,
        CustomWidgetSourceSpec spec, string payloadText)
    {
        static string NormalizeTargetStateKey(string rawKey) =>
            rawKey.StartsWith("state.", StringComparison.OrdinalIgnoreCase)
                ? rawKey["state.".Length..]
                : rawKey;

        static bool IsRawPayloadSelector(string? selector) =>
            string.IsNullOrWhiteSpace(selector) ||
            selector.Trim() is "text" or "stdout" or "raw" or "$" or "." or "content";

        string safePayload = payloadText ?? string.Empty;
        string trimmed = safePayload.Trim();
        bool looksJson = trimmed.StartsWith('{') || trimmed.StartsWith('[');
        if (looksJson && (spec.StateMap is { Count: > 0 } || !string.IsNullOrWhiteSpace(spec.TargetCollection)))
        {
            try
            {
                var (stateUpdates, items) = CustomWidgetNetworkSourceEngine.ExtractHttpJson(trimmed, spec);
                var appliedKeys = new HashSet<string>(StringComparer.Ordinal);
                foreach ((string sk, JsonElement sv) in stateUpdates)
                {
                    string normalizedKey = NormalizeTargetStateKey(sk);
                    if (!string.IsNullOrWhiteSpace(normalizedKey))
                    {
                        vm.SetStructuredStateValue(normalizedKey, sv);
                        appliedKeys.Add(normalizedKey);
                    }
                }

                if (spec.StateMap is { Count: > 0 })
                {
                    foreach ((string rawKey, string selector) in spec.StateMap)
                    {
                        string normalizedKey = NormalizeTargetStateKey(rawKey);
                        if (!string.IsNullOrWhiteSpace(normalizedKey) &&
                            !appliedKeys.Contains(normalizedKey) &&
                            IsRawPayloadSelector(selector))
                        {
                            vm.StateInternal[normalizedKey] = safePayload;
                        }
                    }
                }

                if (!string.IsNullOrWhiteSpace(spec.TargetCollection))
                {
                    int maxItems = vm.ManifestInternal.Collections.TryGetValue(spec.TargetCollection, out CustomWidgetCollectionSchema? schema)
                        ? schema.MaxItems
                        : Math.Clamp(spec.MaxItems, 1, 200);
                    vm.CollectionsInternal[spec.TargetCollection] = items.Take(maxItems).ToList();
                }
                return;
            }
            catch
            {
                // Fall back to plain-text state/collection mapping when payload is not valid JSON.
            }
        }

        if (spec.StateMap is { Count: > 0 })
        {
            foreach ((string rawKey, string selector) in spec.StateMap)
            {
                string normalizedKey = NormalizeTargetStateKey(rawKey);
                if (!string.IsNullOrWhiteSpace(normalizedKey) && IsRawPayloadSelector(selector))
                {
                    vm.StateInternal[normalizedKey] = safePayload;
                }
            }
        }

        if (string.IsNullOrWhiteSpace(safePayload))
        {
            if (!string.IsNullOrWhiteSpace(spec.TargetCollection))
            {
                vm.CollectionsInternal[spec.TargetCollection] = [];
            }
            return;
        }

        if (!string.IsNullOrWhiteSpace(spec.TargetCollection))
        {
            int maxItems = vm.ManifestInternal.Collections.TryGetValue(spec.TargetCollection, out CustomWidgetCollectionSchema? schema)
                ? schema.MaxItems
                : Math.Clamp(spec.MaxItems, 1, 200);
            string[] lines = safePayload.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
            var records = new List<CustomWidgetRecord>();
            long nowTicks = DateTimeOffset.UtcNow.UtcTicks;
            for (int i = 0; i < lines.Length && records.Count < maxItems; i++)
            {
                string line = lines[i].TrimEnd('\r');
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                records.Add(new CustomWidgetRecord
                {
                    Id = $"line-{i + 1}",
                    CreatedAtUtcTicks = nowTicks,
                    UpdatedAtUtcTicks = nowTicks,
                    Fields = new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["text"] = line,
                        ["title"] = line,
                        ["lineNumber"] = (i + 1).ToString(CultureInfo.InvariantCulture)
                    }
                });
            }
            vm.CollectionsInternal[spec.TargetCollection] = records;
        }
    }

}

internal sealed class FileTextCapabilityProvider : ICustomWidgetCapabilityProvider
{
    private readonly CustomWidgetViewModel _vm;
    private readonly Dictionary<string, FileSystemWatcher> _fileTextWatchers = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, long> _fileTextSourceLastFetchUtcTicks = new(StringComparer.Ordinal);
    private readonly object _fileTextWatchDebounceGate = new();
    private DispatcherQueueTimer? _fileTextSourceTimer;
    private CancellationTokenSource? _fileTextFetchCts;
    private CancellationTokenSource? _fileTextWatchDebounceCts;
    private readonly SemaphoreSlim _fileTextRefreshGate = new(1, 1);
    private bool _hasPendingFileTextWatchSource;
    private string? _pendingFileTextWatchSourceId;

    internal FileTextCapabilityProvider(CustomWidgetViewModel vm) => _vm = vm;

    public string ProviderId => "fileText";
    public IReadOnlyList<string> CapabilityIds => ["fileText", CustomWidgetSourceTypes.LocalFolder];

    internal void ClearFetchTimestamps() => _fileTextSourceLastFetchUtcTicks.Clear();

    public void UpdateSubscription() => UpdateFileTextSourcesSubscription();

    public void Stop() => StopFileTextSourcesPolling();

    public void Dispose() => Stop();

    internal bool ManifestUsesFileTextSources() =>
        _vm.ManifestInternal.Sources.Any(static s =>
            string.Equals(s.Type, "fileText", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(s.Type, CustomWidgetSourceTypes.LocalFolder, StringComparison.OrdinalIgnoreCase));


    internal void UpdateFileTextSourcesSubscription()
    {
        bool hasSources = ManifestUsesFileTextSources();
        bool allowed = hasSources && _vm.IsPermissionAllowed(CustomWidgetPermissionKind.LocalFileAccess);
        bool shouldRun = !_vm.IsDisposedInternal &&
                         _vm.IsWindowVisibleInternal &&
                         (!_vm.IsCompactCollapsedInternal || _vm.CapsuleUsesDynamicBindings()) &&
                         !_vm.IsStudioGalleryThumbnailPreview &&
                         !CustomWidgetManifestParser.IsIncompatibleClientVersion(_vm.DiagnosticsInternal) &&
                         hasSources &&
                         allowed;

        if (!shouldRun)
        {
            StopFileTextSourcesPolling();
            if (!_vm.IsDisposedInternal && hasSources && !allowed)
            {
                _vm.StateInternal["sourceStatus"] = "permission_denied";
            }
            return;
        }

        bool wasPolling = _fileTextSourceTimer?.IsRunning == true;
        int minIntervalMs = _vm.ManifestInternal.Sources
            .Where(static s =>
                string.Equals(s.Type, "fileText", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(s.Type, CustomWidgetSourceTypes.LocalFolder, StringComparison.OrdinalIgnoreCase))
            .Select(static s => Math.Clamp(s.IntervalMs is > 0 ? s.IntervalMs : 15_000, 2_000, 3_600_000))
            .DefaultIfEmpty(15_000)
            .Min();

        RebuildFileTextWatchers();

        if (_vm.DispatcherQueueInternal is not null)
        {
            if (_fileTextSourceTimer is null)
            {
                _fileTextSourceTimer = _vm.DispatcherQueueInternal.CreateTimer();
                _fileTextSourceTimer.IsRepeating = true;
                _fileTextSourceTimer.Tick += OnFileTextSourceTimerTick;
            }

            TimeSpan interval = TimeSpan.FromMilliseconds(minIntervalMs);
            if (_fileTextSourceTimer.Interval != interval)
            {
                _fileTextSourceTimer.Interval = interval;
            }

            if (!_fileTextSourceTimer.IsRunning)
            {
                _fileTextSourceTimer.Start();
            }
        }

        _ = RefreshFileTextSourcesNowAsync(targetSourceId: null, respectMinInterval: wasPolling);
    }

    internal void RebuildFileTextWatchers()
    {
        CustomWidgetEvaluationContext evalCtx = _vm.CreateEvaluationContext();
        var desiredPaths = new Dictionary<string, (string? SourceId, bool Recursive)>(StringComparer.OrdinalIgnoreCase);
        foreach (CustomWidgetSourceSpec spec in _vm.ManifestInternal.Sources)
        {
            bool isFileText = string.Equals(spec.Type, "fileText", StringComparison.OrdinalIgnoreCase);
            bool isLocalFolder = string.Equals(spec.Type, CustomWidgetSourceTypes.LocalFolder, StringComparison.OrdinalIgnoreCase);
            if (!isFileText && !isLocalFolder)
            {
                continue;
            }

            string rawPath = CustomWidgetExpressionEvaluator.Interpolate(spec.Path ?? spec.Url, evalCtx).Trim();
            rawPath = CustomWidgetOnboardingHelper.ExpandUserProfilePlaceholder(rawPath);
            if (CustomWidgetLocalFileEngine.TryResolveSafeLocalPath(rawPath, forWrite: false, out string fullPath, out _) &&
                !desiredPaths.ContainsKey(fullPath))
            {
                desiredPaths[fullPath] = (spec.Id, isLocalFolder && spec.Recursive);
            }
        }

        string[] stalePaths = _fileTextWatchers.Keys
            .Where(existing => !desiredPaths.ContainsKey(existing))
            .ToArray();
        foreach (string stale in stalePaths)
        {
            if (_fileTextWatchers.Remove(stale, out FileSystemWatcher? oldWatcher))
            {
                try
                {
                    oldWatcher.EnableRaisingEvents = false;
                    oldWatcher.Dispose();
                }
                catch
                {
                }
            }
        }

        foreach ((string fullPath, (string? sourceId, bool recursive)) in desiredPaths)
        {
            if (_fileTextWatchers.ContainsKey(fullPath))
            {
                continue;
            }

            FileSystemWatcher? watcher = CustomWidgetLocalFileEngine.TryCreateWatcher(
                fullPath,
                () => ScheduleDebouncedFileTextWatcherRefresh(sourceId),
                includeSubdirectories: recursive);
            if (watcher is not null)
            {
                _fileTextWatchers[fullPath] = watcher;
            }
        }
    }

    internal void ScheduleDebouncedFileTextWatcherRefresh(string? sourceId)
    {
        if (_vm.IsDisposedInternal)
        {
            return;
        }

        CancellationTokenSource cts;
        lock (_fileTextWatchDebounceGate)
        {
            if (_vm.IsDisposedInternal)
            {
                return;
            }

            if (!_hasPendingFileTextWatchSource)
            {
                _hasPendingFileTextWatchSource = true;
                _pendingFileTextWatchSourceId = sourceId;
            }
            else if (!string.Equals(_pendingFileTextWatchSourceId, sourceId, StringComparison.OrdinalIgnoreCase))
            {
                // Coalesce distinct concurrently-changed watched files into a full fileText refresh.
                _pendingFileTextWatchSourceId = null;
            }

            CancellationTokenSource? prev = _fileTextWatchDebounceCts;
            cts = new CancellationTokenSource();
            _fileTextWatchDebounceCts = cts;
            if (prev is not null)
            {
                try { prev.Cancel(); } catch (ObjectDisposedException) { }
                prev.Dispose();
            }
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(120, cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            if (_vm.IsDisposedInternal || cts.IsCancellationRequested)
            {
                return;
            }

            string? coalescedTargetId;
            lock (_fileTextWatchDebounceGate)
            {
                if (!ReferenceEquals(_fileTextWatchDebounceCts, cts))
                {
                    return;
                }
                coalescedTargetId = _pendingFileTextWatchSourceId;
                _hasPendingFileTextWatchSource = false;
                _pendingFileTextWatchSourceId = null;
            }

            if (_vm.DispatcherQueueInternal is not null)
            {
                _vm.DispatcherQueueInternal.TryEnqueue(() =>
                {
                    if (!_vm.IsDisposedInternal)
                    {
                        _ = RefreshFileTextSourcesNowAsync(targetSourceId: coalescedTargetId, respectMinInterval: false);
                    }
                });
            }
            else
            {
                await RefreshFileTextSourcesNowAsync(targetSourceId: coalescedTargetId, respectMinInterval: false).ConfigureAwait(false);
            }
        });
    }

    internal void ClearFileTextWatchers()
    {
        lock (_fileTextWatchDebounceGate)
        {
            _hasPendingFileTextWatchSource = false;
            _pendingFileTextWatchSourceId = null;
            if (_fileTextWatchDebounceCts is { } debounceCts)
            {
                _fileTextWatchDebounceCts = null;
                try { debounceCts.Cancel(); } catch (ObjectDisposedException) { }
                debounceCts.Dispose();
            }
        }

        foreach (FileSystemWatcher watcher in _fileTextWatchers.Values)
        {
            try
            {
                watcher.EnableRaisingEvents = false;
                watcher.Dispose();
            }
            catch
            {
            }
        }
        _fileTextWatchers.Clear();
    }

    internal void StopFileTextSourcesPolling()
    {
        ClearFileTextWatchers();
        if (_fileTextSourceTimer is not null)
        {
            _fileTextSourceTimer.Stop();
            _fileTextSourceTimer.Tick -= OnFileTextSourceTimerTick;
            _fileTextSourceTimer = null;
        }

        if (Interlocked.Exchange(ref _fileTextFetchCts, null) is { } cts)
        {
            try
            {
                cts.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }

    internal void OnFileTextSourceTimerTick(DispatcherQueueTimer sender, object args)
    {
        if (!_vm.IsDisposedInternal)
        {
            _ = RefreshFileTextSourcesNowAsync(targetSourceId: null, respectMinInterval: true);
        }
    }

    internal async Task<bool> RefreshFileTextSourcesNowAsync(
        string? targetSourceId = null,
        bool respectMinInterval = false)
    {
        if (_vm.IsDisposedInternal || !ManifestUsesFileTextSources())
        {
            return false;
        }

        if (!_vm.IsPermissionAllowed(CustomWidgetPermissionKind.LocalFileAccess))
        {
            return false;
        }

        if (respectMinInterval)
        {
            if (!_fileTextRefreshGate.Wait(0))
            {
                return false;
            }
        }
        else
        {
            if (Volatile.Read(ref _fileTextFetchCts) is { } inflight)
            {
                try { inflight.Cancel(); } catch (ObjectDisposedException) { }
            }

            await _fileTextRefreshGate.WaitAsync();
            if (_vm.IsDisposedInternal)
            {
                _fileTextRefreshGate.Release();
                return false;
            }
        }

        using var cts = new CancellationTokenSource();
        Interlocked.Exchange(ref _fileTextFetchCts, cts);
        try
        {
            CustomWidgetSourceSpec[] specs = _vm.ManifestInternal.Sources
                .Where(s =>
                    (string.Equals(s.Type, "fileText", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(s.Type, CustomWidgetSourceTypes.LocalFolder, StringComparison.OrdinalIgnoreCase)) &&
                    (string.IsNullOrWhiteSpace(targetSourceId) ||
                     string.Equals(s.Id, targetSourceId, StringComparison.OrdinalIgnoreCase)))
                .ToArray();
            if (specs.Length == 0)
            {
                return false;
            }

            bool anyUpdated = false;
            long nowTicks = DateTimeOffset.UtcNow.UtcTicks;
            CustomWidgetEvaluationContext evalCtx = _vm.CreateEvaluationContext();
            foreach (CustomWidgetSourceSpec spec in specs)
            {
                if (_vm.IsDisposedInternal || cts.IsCancellationRequested)
                {
                    break;
                }

                string rawPath = CustomWidgetExpressionEvaluator.Interpolate(spec.Path ?? spec.Url, evalCtx).Trim();
                rawPath = CustomWidgetOnboardingHelper.ExpandUserProfilePlaceholder(rawPath);
                bool isLocalFolder = string.Equals(spec.Type, CustomWidgetSourceTypes.LocalFolder, StringComparison.OrdinalIgnoreCase);
                string effectiveSort = isLocalFolder
                    ? CustomWidgetExpressionEvaluator.Interpolate(spec.Sort, evalCtx).Trim()
                    : string.Empty;
                if (isLocalFolder && string.IsNullOrWhiteSpace(effectiveSort))
                {
                    effectiveSort = "nameAsc";
                }
                string cacheKey = isLocalFolder
                    ? $"folder|{spec.Id}|{rawPath}|{spec.Recursive}|{spec.MaxItems}|{effectiveSort}|{string.Join(',', spec.Extensions ?? [])}"
                    : $"{spec.Id}|{rawPath}|{spec.TailLines}";
                int minMs = Math.Clamp(spec.IntervalMs is > 0 ? spec.IntervalMs : 15_000, 2_000, 3_600_000);
                if (respectMinInterval &&
                    _fileTextSourceLastFetchUtcTicks.TryGetValue(cacheKey, out long lastTicks) &&
                    nowTicks - lastTicks < TimeSpan.FromMilliseconds(minMs * 0.85).Ticks)
                {
                    continue;
                }

                LocalSourcePayloadHelper.RecordBoundedTimestamp(_fileTextSourceLastFetchUtcTicks, cacheKey, nowTicks);
                if (isLocalFolder)
                {
                    string? globPattern = LocalSourcePayloadHelper.BuildGlobPatternFromExtensions(spec.Extensions);
                    CustomWidgetDirectoryListOutcome dirOutcome = await CustomWidgetLocalFileEngine.ListDirectoryAsync(
                        rawPath,
                        pattern: globPattern,
                        recursive: spec.Recursive,
                        sort: effectiveSort,
                        maxItems: Math.Clamp(spec.MaxItems <= 0 ? 100 : spec.MaxItems, 1, 500),
                        ct: cts.Token);
                    if (_vm.IsDisposedInternal || cts.IsCancellationRequested)
                    {
                        break;
                    }

                    LocalSourcePayloadHelper.ApplyLocalFolderSourceOutcomeOnUi(_vm, spec, dirOutcome);
                    if (dirOutcome.Succeeded)
                    {
                        anyUpdated = true;
                    }
                    continue;
                }

                CustomWidgetFileReadOutcome outcome = await CustomWidgetLocalFileEngine.ReadTextSourceAsync(
                    rawPath,
                    spec.TailLines,
                    cts.Token);
                if (_vm.IsDisposedInternal || cts.IsCancellationRequested)
                {
                    break;
                }

                LocalSourcePayloadHelper.ApplyTextOrCommandPayloadOnUi(_vm, 
                    spec,
                    primaryStatePrefix: "fileText",
                    succeeded: outcome.Succeeded,
                    payloadText: outcome.Content,
                    errorMessage: outcome.ErrorMessage,
                    extraMetaSetter: () =>
                    {
                        string linesStr = outcome.LineCount.ToString(CultureInfo.InvariantCulture);
                        _vm.StateInternal["fileLineCount"] = linesStr;
                        if (!string.IsNullOrWhiteSpace(spec.Id))
                        {
                            _vm.StateInternal[$"fileLineCount.{spec.Id}"] = linesStr;
                        }
                    });
                if (outcome.Succeeded)
                {
                    anyUpdated = true;
                }
            }

            return anyUpdated;
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
        {
            return false;
        }
        finally
        {
            Interlocked.CompareExchange(ref _fileTextFetchCts, null, cts);
            _fileTextRefreshGate.Release();
        }
    }

}

internal sealed class CommandCapabilityProvider : ICustomWidgetCapabilityProvider
{
    private readonly CustomWidgetViewModel _vm;
    private readonly Dictionary<string, long> _commandSourceLastFetchUtcTicks = new(StringComparer.Ordinal);
    private DispatcherQueueTimer? _commandSourceTimer;
    private CancellationTokenSource? _commandSourceCts;
#if !DESKBOX_STORE
    private int _commandRefreshInFlight;
    private int _commandRefreshPending;
#endif

    internal CommandCapabilityProvider(CustomWidgetViewModel vm) => _vm = vm;

    public string ProviderId => "command";
    public IReadOnlyList<string> CapabilityIds => ["command"];

    internal void ClearFetchTimestamps() => _commandSourceLastFetchUtcTicks.Clear();

    public void UpdateSubscription() => UpdateCommandSourcesSubscription();

    public void Stop() => StopCommandSourcesPolling();

    public void Dispose() => Stop();

    internal bool ManifestUsesCommandSources() =>
        _vm.ManifestInternal.Sources.Any(static s => string.Equals(s.Type, "command", StringComparison.OrdinalIgnoreCase));


    internal void UpdateCommandSourcesSubscription()
    {
#if DESKBOX_STORE
        StopCommandSourcesPolling();
        return;
#else
        bool hasSources = ManifestUsesCommandSources();
        bool allowed = hasSources && _vm.IsPermissionAllowed(CustomWidgetPermissionKind.CommandExecution);
        bool shouldRun = !_vm.IsDisposedInternal &&
                         _vm.IsWindowVisibleInternal &&
                         (!_vm.IsCompactCollapsedInternal || _vm.CapsuleUsesDynamicBindings()) &&
                         !_vm.IsStudioGalleryThumbnailPreview &&
                         !CustomWidgetManifestParser.IsIncompatibleClientVersion(_vm.DiagnosticsInternal) &&
                         hasSources &&
                         allowed;

        if (!shouldRun)
        {
            StopCommandSourcesPolling();
            if (!_vm.IsDisposedInternal && hasSources && !allowed)
            {
                _vm.StateInternal["sourceStatus"] = "permission_denied";
            }
            return;
        }

        int minIntervalMs = _vm.ManifestInternal.Sources
            .Where(static s => string.Equals(s.Type, "command", StringComparison.OrdinalIgnoreCase))
            .Select(static s => Math.Clamp(s.IntervalMs is > 0 ? s.IntervalMs : 30_000, 5_000, 3_600_000))
            .DefaultIfEmpty(30_000)
            .Min();

        if (_vm.DispatcherQueueInternal is not null)
        {
            if (_commandSourceTimer is null)
            {
                _commandSourceTimer = _vm.DispatcherQueueInternal.CreateTimer();
                _commandSourceTimer.IsRepeating = true;
                _commandSourceTimer.Tick += OnCommandSourceTimerTick;
            }

            TimeSpan interval = TimeSpan.FromMilliseconds(minIntervalMs);
            if (_commandSourceTimer.Interval != interval)
            {
                _commandSourceTimer.Interval = interval;
            }

            if (!_commandSourceTimer.IsRunning)
            {
                _commandSourceTimer.Start();
            }
        }

        _ = RefreshCommandSourcesNowAsync(targetSourceId: null, respectMinInterval: true);
#endif
    }

    internal void StopCommandSourcesPolling()
    {
        if (_commandSourceTimer is not null)
        {
            _commandSourceTimer.Stop();
            _commandSourceTimer.Tick -= OnCommandSourceTimerTick;
            _commandSourceTimer = null;
        }

        if (Interlocked.Exchange(ref _commandSourceCts, null) is { } cts)
        {
            try
            {
                cts.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }

    internal void OnCommandSourceTimerTick(DispatcherQueueTimer sender, object args)
    {
        if (!_vm.IsDisposedInternal)
        {
            _ = RefreshCommandSourcesNowAsync(targetSourceId: null, respectMinInterval: true);
        }
    }

    internal async Task<bool> RefreshCommandSourcesNowAsync(
        string? targetSourceId = null,
        bool respectMinInterval = false)
    {
#if DESKBOX_STORE
        _ = targetSourceId;
        _ = respectMinInterval;
        await Task.CompletedTask;
        return false;
#else
        if (_vm.IsDisposedInternal || !ManifestUsesCommandSources())
        {
            return false;
        }

        if (!_vm.IsPermissionAllowed(CustomWidgetPermissionKind.CommandExecution))
        {
            return false;
        }

        if (Interlocked.CompareExchange(ref _commandRefreshInFlight, 1, 0) != 0)
        {
            Interlocked.Exchange(ref _commandRefreshPending, 1);
            return false;
        }

        using var cts = new CancellationTokenSource();
        Interlocked.Exchange(ref _commandSourceCts, cts)?.Cancel();
        try
        {
            CustomWidgetSourceSpec[] specs = _vm.ManifestInternal.Sources
                .Where(s =>
                    string.Equals(s.Type, "command", StringComparison.OrdinalIgnoreCase) &&
                    (string.IsNullOrWhiteSpace(targetSourceId) ||
                     string.Equals(s.Id, targetSourceId, StringComparison.OrdinalIgnoreCase)))
                .ToArray();
            if (specs.Length == 0)
            {
                return false;
            }

            bool anyUpdated = false;
            long nowTicks = DateTimeOffset.UtcNow.UtcTicks;
            CustomWidgetEvaluationContext evalCtx = _vm.CreateEvaluationContext();
            foreach (CustomWidgetSourceSpec spec in specs)
            {
                if (_vm.IsDisposedInternal || cts.IsCancellationRequested)
                {
                    break;
                }

                string exe = CustomWidgetExpressionEvaluator.Interpolate(spec.Command ?? spec.Path, evalCtx).Trim();
                List<string>? argList = null;
                if (spec.Arguments is { Count: > 0 })
                {
                    argList = new List<string>(spec.Arguments.Count);
                    foreach (string rawArg in spec.Arguments)
                    {
                        argList.Add(CustomWidgetExpressionEvaluator.Interpolate(rawArg, evalCtx));
                    }
                }
                string? workDir = !string.IsNullOrWhiteSpace(spec.WorkingDirectory)
                    ? CustomWidgetExpressionEvaluator.Interpolate(spec.WorkingDirectory, evalCtx).Trim()
                    : null;

                string cacheKey = $"{spec.Id}|{exe}|{(argList is null ? string.Empty : string.Join('\u001F', argList))}|{workDir}";
                int minMs = Math.Clamp(spec.IntervalMs is > 0 ? spec.IntervalMs : 30_000, 5_000, 3_600_000);
                if (respectMinInterval &&
                    _commandSourceLastFetchUtcTicks.TryGetValue(cacheKey, out long lastTicks) &&
                    nowTicks - lastTicks < TimeSpan.FromMilliseconds(minMs * 0.85).Ticks)
                {
                    continue;
                }

                LocalSourcePayloadHelper.RecordBoundedTimestamp(_commandSourceLastFetchUtcTicks, cacheKey, nowTicks);
                CustomWidgetCommandOutcome outcome = await CustomWidgetCommandRunner.ExecuteAsync(
                    exe,
                    argumentList: argList,
                    workingDirectory: workDir,
                    timeoutMs: CustomWidgetCommandRunner.DefaultTimeoutMs,
                    ct: cts.Token);
                if (_vm.IsDisposedInternal || cts.IsCancellationRequested)
                {
                    break;
                }

                string cleanStdout = (outcome.Stdout ?? string.Empty).TrimEnd('\r', '\n');
                LocalSourcePayloadHelper.ApplyTextOrCommandPayloadOnUi(_vm, 
                    spec,
                    primaryStatePrefix: "commandStdout",
                    succeeded: outcome.Succeeded,
                    payloadText: cleanStdout,
                    errorMessage: outcome.ErrorMessage ?? outcome.Stderr,
                    extraMetaSetter: () =>
                    {
                        string codeStr = outcome.ExitCode.ToString(CultureInfo.InvariantCulture);
                        _vm.StateInternal["commandExitCode"] = codeStr;
                        if (!string.IsNullOrWhiteSpace(spec.Id))
                        {
                            _vm.StateInternal[$"commandExitCode.{spec.Id}"] = codeStr;
                        }
                    });
                if (outcome.Succeeded)
                {
                    anyUpdated = true;
                }
            }

            return anyUpdated;
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
        {
            return false;
        }
        finally
        {
            Interlocked.CompareExchange(ref _commandSourceCts, null, cts);
            Interlocked.Exchange(ref _commandRefreshInFlight, 0);
            if (!_vm.IsDisposedInternal && Interlocked.Exchange(ref _commandRefreshPending, 0) == 1)
            {
                _ = RefreshCommandSourcesNowAsync(targetSourceId: null, respectMinInterval: false);
            }
        }
#endif
    }

}
