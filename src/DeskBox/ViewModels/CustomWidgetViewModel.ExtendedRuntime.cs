using System.Globalization;
using System.Text.Json;
using DeskBox.Contracts;
using DeskBox.Models;
using DeskBox.Services;
using Microsoft.UI.Dispatching;

namespace DeskBox.ViewModels;

public sealed partial class CustomWidgetViewModel
{
    private readonly Dictionary<string, FileSystemWatcher> _fileTextWatchers = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, long> _fileTextSourceLastFetchUtcTicks = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _commandSourceLastFetchUtcTicks = new(StringComparer.Ordinal);
    private readonly object _fileTextWatchDebounceGate = new();
    private DispatcherQueueTimer? _fileTextSourceTimer;
    private DispatcherQueueTimer? _commandSourceTimer;
    private CancellationTokenSource? _fileTextFetchCts;
    private CancellationTokenSource? _fileTextWatchDebounceCts;
    private CancellationTokenSource? _commandSourceCts;
    private int _fileTextRefreshInFlight;
    private int _fileTextRefreshPending;
    private bool _hasPendingFileTextWatchSource;
    private string? _pendingFileTextWatchSourceId;
#if !DESKBOX_STORE
    private int _commandRefreshInFlight;
    private int _commandRefreshPending;
#endif

    internal CustomWidgetTrustStore? TrustStoreOverride { get; set; }

    /// <summary>
    /// True when rendering an uncommitted external/agent stage preview (e.g. <c>deskbox_capture_widget_preview</c>
    /// or unconfirmed Studio stage preview) where side-effecting local file/command/LAN sources must not auto-run.
    /// </summary>
    public bool IsUntrustedStagePreview { get; set; }

    /// <summary>
    /// Fired when an action requests opening, closing, or toggling an anchored popover (`(mode, popoverId)`).
    /// </summary>
    public event Action<string, string>? PopoverCommandRequested;

    private CustomWidgetTrustStore EffectiveTrustStore => TrustStoreOverride ?? CustomWidgetTrustStore.Shared;

    public CustomWidgetTrustProfile GetTrustProfile()
    {
        if (IsUntrustedStagePreview)
        {
            return new CustomWidgetTrustProfile(
                Config.Id,
                TrustedLocalAuthoring: false,
                new Dictionary<CustomWidgetPermissionKind, bool>());
        }

        if (_isPreviewOnly && TrustStoreOverride is null)
        {
            bool trustedPreview = !string.Equals(
                _manifest.Origin,
                CustomWidgetOriginKinds.Community,
                StringComparison.OrdinalIgnoreCase);
            return new CustomWidgetTrustProfile(
                Config.Id,
                TrustedLocalAuthoring: trustedPreview,
                new Dictionary<CustomWidgetPermissionKind, bool>());
        }

        return EffectiveTrustStore.GetProfile(Config.Id, _manifest.Origin);
    }

    public bool IsPermissionAllowed(CustomWidgetPermissionKind kind, bool isReadOnlyPublicGet = false) =>
        GetTrustProfile().IsPermissionGranted(kind, isReadOnlyPublicGet);

    public void RequestPopoverCommand(string mode, string? popoverId)
    {
        if (_isDisposed || string.IsNullOrWhiteSpace(popoverId))
        {
            return;
        }

        string normalizedMode = (mode ?? "toggle").Trim().ToLowerInvariant();
        if (normalizedMode is not ("open" or "close" or "toggle"))
        {
            normalizedMode = "toggle";
        }

        PopoverCommandRequested?.Invoke(normalizedMode, popoverId.Trim());
    }

    internal async Task<bool> EnsureWidgetPermissionGrantedAsync(
        CustomWidgetPermissionKind kind,
        string actionKind,
        string targetDisplay,
        bool isReadOnlyPublicGet = false,
        bool showBlockedToast = true)
    {
        if (_isDisposed)
        {
            return false;
        }

        string culture = ActiveCultureName;
        CustomWidgetPermissionDescriptor descriptor = CustomWidgetTrustStore.GetDescriptor(kind, culture);

#if DESKBOX_STORE
        if (kind == CustomWidgetPermissionKind.CommandExecution)
        {
            if (showBlockedToast)
            {
                RaiseFeedback(
                    LocalizationService.TranslateForCulture(culture, "CustomWidget.Perm.ToastStoreCommandDisabled"),
                    WidgetFeedbackSeverity.Warning);
            }
            return false;
        }
#endif

        CustomWidgetTrustProfile profile = GetTrustProfile();
        if (profile.ExplicitOverrides.TryGetValue(kind, out bool explicitAllowed))
        {
            if (explicitAllowed)
            {
                return true;
            }

            if (showBlockedToast)
            {
                RaiseFeedback(
                    LocalizationService.FormatForCulture(culture, "CustomWidget.Perm.ToastBlockedBySettings", descriptor.Title),
                    WidgetFeedbackSeverity.Warning);
            }
            return false;
        }

        if (profile.IsPermissionGranted(kind, isReadOnlyPublicGet))
        {
            return true;
        }

        Func<CustomWidgetSecurityPrompt, Task<bool>>? promptFunc = ExternalActionConsentPrompt;
        if (promptFunc is not null)
        {
            await _externalConsentPromptGate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (_isDisposed)
                {
                    return false;
                }

                CustomWidgetTrustProfile refreshed = GetTrustProfile();
                if (refreshed.IsPermissionGranted(kind, isReadOnlyPublicGet))
                {
                    return true;
                }

                string widgetTitle = string.IsNullOrWhiteSpace(_manifest.Name)
                    ? Config.Name
                    : _manifest.ResolveLocalizedName(culture);
                bool approved = await promptFunc(new CustomWidgetSecurityPrompt(
                    actionKind,
                    $"{descriptor.Title} · {targetDisplay}",
                    widgetTitle)).ConfigureAwait(false);

                if (approved)
                {
                    if (!_isPreviewOnly || TrustStoreOverride is not null)
                    {
                        EffectiveTrustStore.SetPermissionOverride(
                            Config.Id,
                            kind,
                            enabled: true,
                            defaultTrustedIfNew: false);
                    }
                    return true;
                }
            }
            finally
            {
                _externalConsentPromptGate.Release();
            }
        }

        if (showBlockedToast)
        {
            RaiseFeedback(
                LocalizationService.FormatForCulture(culture, "CustomWidget.Perm.ToastPermissionRequired", descriptor.Title),
                WidgetFeedbackSeverity.Warning);
        }
        return false;
    }

    private bool ManifestUsesFileTextSources() =>
        _manifest.Sources.Any(static s => string.Equals(s.Type, "fileText", StringComparison.OrdinalIgnoreCase));

    private bool ManifestUsesCommandSources() =>
        _manifest.Sources.Any(static s => string.Equals(s.Type, "command", StringComparison.OrdinalIgnoreCase));

    private void UpdateFileTextSourcesSubscription()
    {
        bool hasSources = ManifestUsesFileTextSources();
        bool allowed = hasSources && IsPermissionAllowed(CustomWidgetPermissionKind.LocalFileAccess);
        bool shouldRun = !_isDisposed &&
                         _isWindowVisible &&
                         !IsStudioGalleryThumbnailPreview &&
                         !CustomWidgetManifestParser.IsIncompatibleClientVersion(_diagnostics) &&
                         hasSources &&
                         allowed;

        if (!shouldRun)
        {
            StopFileTextSourcesPolling();
            if (!_isDisposed && hasSources && !allowed)
            {
                _state["sourceStatus"] = "permission_denied";
            }
            return;
        }

        int minIntervalMs = _manifest.Sources
            .Where(static s => string.Equals(s.Type, "fileText", StringComparison.OrdinalIgnoreCase))
            .Select(static s => Math.Clamp(s.IntervalMs is > 0 ? s.IntervalMs : 15_000, 2_000, 3_600_000))
            .DefaultIfEmpty(15_000)
            .Min();

        RebuildFileTextWatchers();

        if (_dispatcherQueue is not null)
        {
            if (_fileTextSourceTimer is null)
            {
                _fileTextSourceTimer = _dispatcherQueue.CreateTimer();
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

        _ = RefreshFileTextSourcesNowAsync(targetSourceId: null, respectMinInterval: true);
    }

    private void RebuildFileTextWatchers()
    {
        CustomWidgetEvaluationContext evalCtx = CreateEvaluationContext();
        var desiredPaths = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (CustomWidgetSourceSpec spec in _manifest.Sources)
        {
            if (!string.Equals(spec.Type, "fileText", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string rawPath = CustomWidgetExpressionEvaluator.Interpolate(spec.Path ?? spec.Url, evalCtx).Trim();
            if (CustomWidgetLocalFileEngine.TryResolveSafeLocalPath(rawPath, forWrite: false, out string fullPath, out _) &&
                !desiredPaths.ContainsKey(fullPath))
            {
                desiredPaths[fullPath] = spec.Id;
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

        foreach ((string fullPath, string? sourceId) in desiredPaths)
        {
            if (_fileTextWatchers.ContainsKey(fullPath))
            {
                continue;
            }

            FileSystemWatcher? watcher = CustomWidgetLocalFileEngine.TryCreateWatcher(
                fullPath,
                () => ScheduleDebouncedFileTextWatcherRefresh(sourceId));
            if (watcher is not null)
            {
                _fileTextWatchers[fullPath] = watcher;
            }
        }
    }

    private void ScheduleDebouncedFileTextWatcherRefresh(string? sourceId)
    {
        if (_isDisposed)
        {
            return;
        }

        CancellationTokenSource cts;
        lock (_fileTextWatchDebounceGate)
        {
            if (_isDisposed)
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

            if (_isDisposed || cts.IsCancellationRequested)
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

            if (_dispatcherQueue is not null)
            {
                _dispatcherQueue.TryEnqueue(() =>
                {
                    if (!_isDisposed)
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

    private void ClearFileTextWatchers()
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

    private void StopFileTextSourcesPolling()
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

    private void OnFileTextSourceTimerTick(DispatcherQueueTimer sender, object args)
    {
        if (!_isDisposed)
        {
            _ = RefreshFileTextSourcesNowAsync(targetSourceId: null, respectMinInterval: true);
        }
    }

    public async Task<bool> RefreshFileTextSourcesNowAsync(
        string? targetSourceId = null,
        bool respectMinInterval = false)
    {
        if (_isDisposed || !ManifestUsesFileTextSources())
        {
            return false;
        }

        if (!IsPermissionAllowed(CustomWidgetPermissionKind.LocalFileAccess))
        {
            return false;
        }

        if (Interlocked.CompareExchange(ref _fileTextRefreshInFlight, 1, 0) != 0)
        {
            if (!respectMinInterval)
            {
                Interlocked.Exchange(ref _fileTextRefreshPending, 1);
            }
            return false;
        }

        using var cts = new CancellationTokenSource();
        Interlocked.Exchange(ref _fileTextFetchCts, cts)?.Cancel();
        try
        {
            CustomWidgetSourceSpec[] specs = _manifest.Sources
                .Where(s =>
                    string.Equals(s.Type, "fileText", StringComparison.OrdinalIgnoreCase) &&
                    (string.IsNullOrWhiteSpace(targetSourceId) ||
                     string.Equals(s.Id, targetSourceId, StringComparison.OrdinalIgnoreCase)))
                .ToArray();
            if (specs.Length == 0)
            {
                return false;
            }

            bool anyUpdated = false;
            long nowTicks = DateTimeOffset.UtcNow.UtcTicks;
            CustomWidgetEvaluationContext evalCtx = CreateEvaluationContext();
            foreach (CustomWidgetSourceSpec spec in specs)
            {
                if (_isDisposed || cts.IsCancellationRequested)
                {
                    break;
                }

                string rawPath = CustomWidgetExpressionEvaluator.Interpolate(spec.Path ?? spec.Url, evalCtx).Trim();
                string cacheKey = $"{spec.Id}|{rawPath}|{spec.TailLines}";
                int minMs = Math.Clamp(spec.IntervalMs is > 0 ? spec.IntervalMs : 15_000, 2_000, 3_600_000);
                if (respectMinInterval &&
                    _fileTextSourceLastFetchUtcTicks.TryGetValue(cacheKey, out long lastTicks) &&
                    nowTicks - lastTicks < TimeSpan.FromMilliseconds(minMs * 0.85).Ticks)
                {
                    continue;
                }

                _fileTextSourceLastFetchUtcTicks[cacheKey] = nowTicks;
                CustomWidgetFileReadOutcome outcome = await CustomWidgetLocalFileEngine.ReadTextSourceAsync(
                    rawPath,
                    spec.TailLines,
                    cts.Token);
                if (_isDisposed || cts.IsCancellationRequested)
                {
                    break;
                }

                ApplyTextOrCommandPayloadOnUi(
                    spec,
                    primaryStatePrefix: "fileText",
                    succeeded: outcome.Succeeded,
                    payloadText: outcome.Content,
                    errorMessage: outcome.ErrorMessage,
                    extraMetaSetter: () =>
                    {
                        string linesStr = outcome.LineCount.ToString(CultureInfo.InvariantCulture);
                        _state["fileLineCount"] = linesStr;
                        if (!string.IsNullOrWhiteSpace(spec.Id))
                        {
                            _state[$"fileLineCount.{spec.Id}"] = linesStr;
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
            Interlocked.Exchange(ref _fileTextRefreshInFlight, 0);
            if (!_isDisposed && Interlocked.Exchange(ref _fileTextRefreshPending, 0) == 1)
            {
                _ = RefreshFileTextSourcesNowAsync(targetSourceId: null, respectMinInterval: false);
            }
        }
    }

    private void UpdateCommandSourcesSubscription()
    {
#if DESKBOX_STORE
        StopCommandSourcesPolling();
        return;
#else
        bool hasSources = ManifestUsesCommandSources();
        bool allowed = hasSources && IsPermissionAllowed(CustomWidgetPermissionKind.CommandExecution);
        bool shouldRun = !_isDisposed &&
                         _isWindowVisible &&
                         !IsStudioGalleryThumbnailPreview &&
                         !CustomWidgetManifestParser.IsIncompatibleClientVersion(_diagnostics) &&
                         hasSources &&
                         allowed;

        if (!shouldRun)
        {
            StopCommandSourcesPolling();
            if (!_isDisposed && hasSources && !allowed)
            {
                _state["sourceStatus"] = "permission_denied";
            }
            return;
        }

        int minIntervalMs = _manifest.Sources
            .Where(static s => string.Equals(s.Type, "command", StringComparison.OrdinalIgnoreCase))
            .Select(static s => Math.Clamp(s.IntervalMs is > 0 ? s.IntervalMs : 30_000, 5_000, 3_600_000))
            .DefaultIfEmpty(30_000)
            .Min();

        if (_dispatcherQueue is not null)
        {
            if (_commandSourceTimer is null)
            {
                _commandSourceTimer = _dispatcherQueue.CreateTimer();
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

    private void StopCommandSourcesPolling()
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

    private void OnCommandSourceTimerTick(DispatcherQueueTimer sender, object args)
    {
        if (!_isDisposed)
        {
            _ = RefreshCommandSourcesNowAsync(targetSourceId: null, respectMinInterval: true);
        }
    }

    public async Task<bool> RefreshCommandSourcesNowAsync(
        string? targetSourceId = null,
        bool respectMinInterval = false)
    {
#if DESKBOX_STORE
        _ = targetSourceId;
        _ = respectMinInterval;
        await Task.CompletedTask;
        return false;
#else
        if (_isDisposed || !ManifestUsesCommandSources())
        {
            return false;
        }

        if (!IsPermissionAllowed(CustomWidgetPermissionKind.CommandExecution))
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
            CustomWidgetSourceSpec[] specs = _manifest.Sources
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
            CustomWidgetEvaluationContext evalCtx = CreateEvaluationContext();
            foreach (CustomWidgetSourceSpec spec in specs)
            {
                if (_isDisposed || cts.IsCancellationRequested)
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

                _commandSourceLastFetchUtcTicks[cacheKey] = nowTicks;
                CustomWidgetCommandOutcome outcome = await CustomWidgetCommandRunner.ExecuteAsync(
                    exe,
                    argumentList: argList,
                    workingDirectory: workDir,
                    timeoutMs: CustomWidgetCommandRunner.DefaultTimeoutMs,
                    ct: cts.Token);
                if (_isDisposed || cts.IsCancellationRequested)
                {
                    break;
                }

                string cleanStdout = (outcome.Stdout ?? string.Empty).TrimEnd('\r', '\n');
                ApplyTextOrCommandPayloadOnUi(
                    spec,
                    primaryStatePrefix: "commandStdout",
                    succeeded: outcome.Succeeded,
                    payloadText: cleanStdout,
                    errorMessage: outcome.ErrorMessage ?? outcome.Stderr,
                    extraMetaSetter: () =>
                    {
                        string codeStr = outcome.ExitCode.ToString(CultureInfo.InvariantCulture);
                        _state["commandExitCode"] = codeStr;
                        if (!string.IsNullOrWhiteSpace(spec.Id))
                        {
                            _state[$"commandExitCode.{spec.Id}"] = codeStr;
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
            if (!_isDisposed && Interlocked.Exchange(ref _commandRefreshPending, 0) == 1)
            {
                _ = RefreshCommandSourcesNowAsync(targetSourceId: null, respectMinInterval: false);
            }
        }
#endif
    }

    private void ApplyTextOrCommandPayloadOnUi(
        CustomWidgetSourceSpec spec,
        string primaryStatePrefix,
        bool succeeded,
        string payloadText,
        string? errorMessage,
        Action? extraMetaSetter = null)
    {
        void Apply()
        {
            if (_isDisposed)
            {
                return;
            }

            if (!succeeded)
            {
                string err = string.IsNullOrWhiteSpace(errorMessage) ? "Source failed." : errorMessage.Trim();
                if (!string.IsNullOrWhiteSpace(spec.Id))
                {
                    _state[$"sourceStatus.{spec.Id}"] = "error";
                    _state[$"sourceError.{spec.Id}"] = err;
                }
                _state["sourceStatus"] = "error";
                _state["sourceError"] = err;
                ViewRebuildRequested?.Invoke(this, EventArgs.Empty);
                return;
            }

            _state[primaryStatePrefix] = payloadText;
            if (!string.IsNullOrWhiteSpace(spec.Id))
            {
                _state[$"{primaryStatePrefix}.{spec.Id}"] = payloadText;
                _state[$"sourceStatus.{spec.Id}"] = "ok";
                _state[$"sourceError.{spec.Id}"] = string.Empty;
            }
            _state["sourceStatus"] = "ok";
            _state["sourceError"] = string.Empty;
            _state["sourceUpdatedAt"] = DateTimeOffset.Now.ToString("HH:mm", CultureInfo.InvariantCulture);
            extraMetaSetter?.Invoke();

            ExtractTextOrJsonSourceMappings(spec, payloadText);

            IReadOnlyList<CustomWidgetActionSpec>? dispatched = null;
            if (_compiledScript is not null &&
                _compiledScript.ExportedFunctions.Contains("onSourceData"))
            {
                InvokeScriptCore(
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
                    out dispatched);
                if (scriptMutatedPersisted && !_isPreviewOnly)
                {
                    _ = PersistStoreAsync();
                }
            }

            RefreshCapsuleProjection(bumpLiveState: true);
            ViewRebuildRequested?.Invoke(this, EventArgs.Empty);
            if (dispatched is { Count: > 0 })
            {
                _ = ExecuteActionsAsync(dispatched, isNested: true, depth: 1);
            }
        }

        if (_dispatcherQueue is not null && !_dispatcherQueue.HasThreadAccess)
        {
            _dispatcherQueue.TryEnqueue(Apply);
        }
        else
        {
            Apply();
        }
    }

    private void ExtractTextOrJsonSourceMappings(CustomWidgetSourceSpec spec, string payloadText)
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
                foreach ((string sk, string sv) in stateUpdates)
                {
                    string normalizedKey = NormalizeTargetStateKey(sk);
                    if (!string.IsNullOrWhiteSpace(normalizedKey))
                    {
                        _state[normalizedKey] = sv;
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
                            _state[normalizedKey] = safePayload;
                        }
                    }
                }

                if (!string.IsNullOrWhiteSpace(spec.TargetCollection))
                {
                    int maxItems = _manifest.Collections.TryGetValue(spec.TargetCollection, out CustomWidgetCollectionSchema? schema)
                        ? schema.MaxItems
                        : Math.Clamp(spec.MaxItems, 1, 200);
                    _collections[spec.TargetCollection] = items.Take(maxItems).ToList();
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
                    _state[normalizedKey] = safePayload;
                }
            }
        }

        if (string.IsNullOrWhiteSpace(safePayload))
        {
            if (!string.IsNullOrWhiteSpace(spec.TargetCollection))
            {
                _collections[spec.TargetCollection] = [];
            }
            return;
        }

        if (!string.IsNullOrWhiteSpace(spec.TargetCollection))
        {
            int maxItems = _manifest.Collections.TryGetValue(spec.TargetCollection, out CustomWidgetCollectionSchema? schema)
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
            _collections[spec.TargetCollection] = records;
        }
    }

    private bool ExecuteCollectionReorder(
        CustomWidgetActionSpec action,
        CustomWidgetEvaluationContext evalContext)
    {
        string colName = (action.Collection ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(colName) ||
            !_collections.TryGetValue(colName, out List<CustomWidgetRecord>? list) ||
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

    public async Task<bool> ReorderCollectionItemsAsync(
        string collectionName,
        int fromIndex,
        int toIndex,
        IReadOnlyList<CustomWidgetActionSpec>? onReorderActions = null)
    {
        if (_isDisposed || string.IsNullOrWhiteSpace(collectionName))
        {
            return false;
        }

        if (!_collections.TryGetValue(collectionName.Trim(), out List<CustomWidgetRecord>? list) ||
            fromIndex < 0 || fromIndex >= list.Count ||
            toIndex < 0 || toIndex >= list.Count ||
            fromIndex == toIndex)
        {
            return false;
        }

        CustomWidgetRecord moving = list[fromIndex];
        list.RemoveAt(fromIndex);
        list.Insert(toIndex, moving);

        if (onReorderActions is { Count: > 0 })
        {
            CustomWidgetEvaluationContext ctx = CreateEvaluationContext()
                .WithItem(moving, toIndex)
                .WithReorderEvent(fromIndex, toIndex);
            await ExecuteActionsAsync(onReorderActions, ctx);
        }

        await PersistStoreAsync();
        RefreshCapsuleProjection(bumpLiveState: true);
        ViewRebuildRequested?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public async Task<bool> CommitInlineItemEditAsync(
        string collectionName,
        string itemId,
        string fieldName,
        string newValue,
        IReadOnlyList<CustomWidgetActionSpec>? onInlineCommitActions = null)
    {
        if (_isDisposed ||
            string.IsNullOrWhiteSpace(collectionName) ||
            string.IsNullOrWhiteSpace(itemId) ||
            string.IsNullOrWhiteSpace(fieldName))
        {
            return false;
        }

        if (!_collections.TryGetValue(collectionName.Trim(), out List<CustomWidgetRecord>? list))
        {
            return false;
        }

        int idx = list.FindIndex(r => string.Equals(r.Id, itemId, StringComparison.Ordinal));
        if (idx < 0)
        {
            return false;
        }

        CustomWidgetRecord target = list[idx];
        target.Fields[fieldName.Trim()] = newValue ?? string.Empty;
        target.UpdatedAtUtcTicks = DateTimeOffset.UtcNow.UtcTicks;

        if (onInlineCommitActions is { Count: > 0 })
        {
            CustomWidgetEvaluationContext ctx = CreateEvaluationContext()
                .WithItem(target, idx)
                .WithEventValue(newValue);
            await ExecuteActionsAsync(onInlineCommitActions, ctx);
        }

        await PersistStoreAsync();
        RefreshCapsuleProjection(bumpLiveState: true);
        ViewRebuildRequested?.Invoke(this, EventArgs.Empty);
        return true;
    }

    private async Task<(bool Succeeded, bool Mutated, IReadOnlyList<CustomWidgetActionSpec>? Dispatched)> ExecuteInteractiveHttpRequestActionAsync(
        CustomWidgetActionSpec action,
        CustomWidgetEvaluationContext evalContext)
    {
        string resolvedUrl = CustomWidgetExpressionEvaluator.Interpolate(action.Url ?? action.Value, evalContext).Trim();
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
            _state["httpOk"] = "false";
            _state["httpError"] = urlErr ?? "Invalid URL.";
            RaiseFeedback(urlErr ?? "Invalid URL.", WidgetFeedbackSeverity.Warning);
            return (false, true, null);
        }

        CustomWidgetPermissionKind requiredPerm = requiresLocal
            ? CustomWidgetPermissionKind.LocalNetwork
            : CustomWidgetPermissionKind.PublicNetwork;
        bool isReadOnlyGet = !requiresLocal && string.Equals(method, "GET", StringComparison.OrdinalIgnoreCase);
        bool wasAlreadyPermitted = IsPermissionAllowed(requiredPerm, isReadOnlyPublicGet: isReadOnlyGet);

        bool granted = await EnsureWidgetPermissionGrantedAsync(
            requiredPerm,
            actionKind: "http.request",
            targetDisplay: targetUri.AbsoluteUri,
            isReadOnlyPublicGet: isReadOnlyGet);
        if (!granted)
        {
            _state["httpOk"] = "false";
            _state["httpError"] = "Permission denied.";
            return (false, true, null);
        }

        CustomWidgetTrustProfile currentTrust = GetTrustProfile();
        if (!requiresLocal && !currentTrust.TrustedLocalAuthoring && wasAlreadyPermitted)
        {
            bool hostApproved = await EnsureCommunityExternalTargetApprovedAsync(
                actionKind: "http.request",
                trustToken: $"host:{targetUri.DnsSafeHost.ToLowerInvariant()}",
                targetDisplay: targetUri.AbsoluteUri);
            if (!hostApproved)
            {
                _state["httpOk"] = "false";
                _state["httpError"] = "External host not approved.";
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
                    string val = CustomWidgetExpressionEvaluator.Interpolate(hv, evalContext).Trim();
                    if (!string.IsNullOrEmpty(val))
                    {
                        headers[hk.Trim()] = val;
                    }
                }
            }
        }

        string? body = action.Body is not null
            ? CustomWidgetExpressionEvaluator.Interpolate(action.Body, evalContext)
            : (!string.Equals(method, "GET", StringComparison.OrdinalIgnoreCase) &&
               !string.Equals(method, "HEAD", StringComparison.OrdinalIgnoreCase) &&
               action.Text is not null
                ? CustomWidgetExpressionEvaluator.Interpolate(action.Text, evalContext)
                : null);

        Func<Uri, CancellationToken, Task<bool>>? redirectHostApprover =
            !currentTrust.TrustedLocalAuthoring
                ? (redirectUri, _) => EnsureCommunityExternalTargetApprovedAsync(
                    actionKind: "http.request",
                    trustToken: $"host:{redirectUri.DnsSafeHost.ToLowerInvariant()}",
                    targetDisplay: redirectUri.AbsoluteUri)
                : null;

        CustomWidgetInteractiveHttpOutcome outcome = await _networkSourceEngine.SendInteractiveHttpRequestAsync(
            resolvedUrl: targetUri.AbsoluteUri,
            method: method,
            resolvedHeaders: headers,
            body: body,
            timeoutMs: action.TimeoutMs > 0 ? action.TimeoutMs : 12_000,
            allowLocalAndHttp: requiresLocal || IsPermissionAllowed(CustomWidgetPermissionKind.LocalNetwork),
            stateMap: action.StateMap,
            targetCollection: action.Collection,
            itemsPath: action.ItemsPath,
            fieldMap: action.FieldMap,
            crossOriginHostApprover: redirectHostApprover);

        if (_isDisposed)
        {
            return (false, false, null);
        }

        bool mutated = true;
        _state["httpStatus"] = outcome.StatusCode.ToString(CultureInfo.InvariantCulture);
        _state["httpOk"] = outcome.Succeeded ? "true" : "false";
        _state["httpError"] = outcome.ErrorMessage ?? string.Empty;

        foreach ((string sk, string sv) in outcome.StateUpdates)
        {
            _state[sk] = sv;
        }

        if (!string.IsNullOrWhiteSpace(action.Key))
        {
            string outKey = NormalizeStateKey(action.Key);
            if (!string.IsNullOrWhiteSpace(outKey))
            {
                _state[outKey] = outcome.ResponseBody ?? string.Empty;
            }
        }

        if (!string.IsNullOrWhiteSpace(action.Collection) &&
            outcome.Succeeded &&
            (outcome.Items.Count > 0 ||
             !string.IsNullOrWhiteSpace(action.ItemsPath) ||
             (outcome.ResponseBody?.TrimStart().StartsWith('[') == true)))
        {
            int maxItems = _manifest.Collections.TryGetValue(action.Collection, out CustomWidgetCollectionSchema? schema)
                ? schema.MaxItems
                : 200;
            _collections[action.Collection] = outcome.Items.Take(maxItems).ToList();
        }

        IReadOnlyList<CustomWidgetActionSpec>? dispatched = null;
        string callbackFn = !string.IsNullOrWhiteSpace(action.Fn) ? action.Fn!.Trim() : "onHttpResponse";
        if (_compiledScript is not null && _compiledScript.ExportedFunctions.Contains(callbackFn))
        {
            string reqId = action.TargetId ?? action.Key ?? method;
            string callbackJson = CustomWidgetNetworkSourceEngine.BuildHttpCallbackPayloadJson(reqId, outcome);
            InvokeScriptCore(
                functionName: callbackFn,
                arg: reqId,
                eventValue: evalContext.EventValue,
                currentItem: evalContext.CurrentItem,
                dropFile: evalContext.DropFile,
                sourceId: reqId,
                rawSourcePayload: callbackJson,
                timeoutMs: CustomWidgetScriptEngine.DefaultActionTimeoutMs,
                requireExport: false,
                allowCopy: true,
                out _,
                out _,
                out _,
                out dispatched);
        }

        if (!outcome.Succeeded && !string.IsNullOrWhiteSpace(outcome.ErrorMessage))
        {
            RaiseFeedback(outcome.ErrorMessage, WidgetFeedbackSeverity.Warning);
        }

        return (outcome.Succeeded, mutated, dispatched);
    }

    private async Task<(bool Succeeded, bool Mutated)> ExecuteFileWriteTextActionAsync(
        CustomWidgetActionSpec action,
        CustomWidgetEvaluationContext evalContext)
    {
        string rawPath = CustomWidgetExpressionEvaluator.Interpolate(action.Path, evalContext).Trim();
        if (!CustomWidgetLocalFileEngine.TryResolveSafeLocalPath(rawPath, forWrite: true, out string fullPath, out string? pathErr))
        {
            _state["fileWriteStatus"] = "error";
            _state["fileWriteError"] = pathErr ?? "Invalid file path.";
            RaiseFeedback(pathErr ?? "Invalid file path.", WidgetFeedbackSeverity.Warning);
            return (false, true);
        }

        bool granted = await EnsureWidgetPermissionGrantedAsync(
            CustomWidgetPermissionKind.LocalFileAccess,
            actionKind: "file.writeText",
            targetDisplay: fullPath);
        if (!granted)
        {
            _state["fileWriteStatus"] = "permission_denied";
            return (false, true);
        }

        string content = CustomWidgetExpressionEvaluator.Interpolate(
            action.Text ?? action.Body ?? action.Value ?? string.Empty,
            evalContext);
        bool append = string.Equals(action.Mode, "append", StringComparison.OrdinalIgnoreCase);

        CustomWidgetFileWriteOutcome outcome = await CustomWidgetLocalFileEngine.WriteTextFileAsync(
            fullPath,
            content,
            append);

        _state["fileWriteStatus"] = outcome.Succeeded ? "ok" : "error";
        _state["fileWriteError"] = outcome.ErrorMessage ?? string.Empty;

        if (!outcome.Succeeded)
        {
            RaiseFeedback(outcome.ErrorMessage ?? "Failed to write file.", WidgetFeedbackSeverity.Warning);
            return (false, true);
        }

        if (ManifestUsesFileTextSources())
        {
            _ = RefreshFileTextSourcesNowAsync();
        }

        return (true, true);
    }

    private async Task<(bool Succeeded, bool Mutated, IReadOnlyList<CustomWidgetActionSpec>? Dispatched)> ExecuteCommandRunActionAsync(
        CustomWidgetActionSpec action,
        CustomWidgetEvaluationContext evalContext)
    {
        string exe = CustomWidgetExpressionEvaluator.Interpolate(
            action.Path ?? action.TargetId ?? action.Value,
            evalContext).Trim();

        List<string>? argList = null;
        if (action.CommandArgs is { Count: > 0 })
        {
            argList = new List<string>(action.CommandArgs.Count);
            foreach (string rawArg in action.CommandArgs)
            {
                argList.Add(CustomWidgetExpressionEvaluator.Interpolate(rawArg, evalContext));
            }
        }

        string? rawArgs = argList is null && !string.IsNullOrWhiteSpace(action.Arg)
            ? CustomWidgetExpressionEvaluator.Interpolate(action.Arg, evalContext)
            : null;
        string? workDir = !string.IsNullOrWhiteSpace(action.WorkingDirectory)
            ? CustomWidgetExpressionEvaluator.Interpolate(action.WorkingDirectory, evalContext).Trim()
            : null;

        string targetDisplay = argList is { Count: > 0 }
            ? $"{exe} {string.Join(' ', argList)}"
            : !string.IsNullOrWhiteSpace(rawArgs)
                ? $"{exe} {rawArgs}"
                : exe;

        bool granted = await EnsureWidgetPermissionGrantedAsync(
            CustomWidgetPermissionKind.CommandExecution,
            actionKind: "command.run",
            targetDisplay: targetDisplay);
        if (!granted)
        {
            _state["commandStatus"] = "permission_denied";
            return (false, true, null);
        }

        CustomWidgetCommandOutcome outcome = await CustomWidgetCommandRunner.ExecuteAsync(
            exe,
            argumentList: argList,
            rawArguments: rawArgs,
            workingDirectory: workDir,
            timeoutMs: action.TimeoutMs > 0 ? action.TimeoutMs : CustomWidgetCommandRunner.DefaultTimeoutMs);

        if (_isDisposed)
        {
            return (false, false, null);
        }

        string cleanStdout = (outcome.Stdout ?? string.Empty).TrimEnd('\r', '\n');
        _state["commandStatus"] = outcome.Succeeded ? "ok" : "error";
        _state["commandExitCode"] = outcome.ExitCode.ToString(CultureInfo.InvariantCulture);
        _state["commandStdout"] = cleanStdout;
        _state["commandStderr"] = (outcome.Stderr ?? string.Empty).TrimEnd('\r', '\n');

        if (!string.IsNullOrWhiteSpace(action.Key))
        {
            string outKey = NormalizeStateKey(action.Key);
            if (!string.IsNullOrWhiteSpace(outKey))
            {
                _state[outKey] = cleanStdout;
            }
        }

        IReadOnlyList<CustomWidgetActionSpec>? dispatched = null;
        string callbackFn = !string.IsNullOrWhiteSpace(action.Fn) ? action.Fn!.Trim() : "onCommandResult";
        if (_compiledScript is not null && _compiledScript.ExportedFunctions.Contains(callbackFn))
        {
            string cmdId = action.TargetId ?? action.Key ?? exe;
            string callbackJson = CustomWidgetCommandRunner.BuildCallbackPayloadJson(cmdId, outcome);
            InvokeScriptCore(
                functionName: callbackFn,
                arg: cmdId,
                eventValue: evalContext.EventValue,
                currentItem: evalContext.CurrentItem,
                dropFile: evalContext.DropFile,
                sourceId: cmdId,
                rawSourcePayload: callbackJson,
                timeoutMs: CustomWidgetScriptEngine.DefaultActionTimeoutMs,
                requireExport: false,
                allowCopy: true,
                out _,
                out _,
                out _,
                out dispatched);
        }

        if (!outcome.Succeeded && !string.IsNullOrWhiteSpace(outcome.ErrorMessage))
        {
            RaiseFeedback(outcome.ErrorMessage, WidgetFeedbackSeverity.Warning);
        }

        return (outcome.Succeeded, true, dispatched);
    }
}
