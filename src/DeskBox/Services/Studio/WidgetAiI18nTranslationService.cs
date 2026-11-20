using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using DeskBox.Models;

namespace DeskBox.Services;

internal sealed class WidgetAiI18nTranslationService
{
    private readonly DeskBoxAiService _aiService;

    internal WidgetAiI18nTranslationService(DeskBoxAiService aiService)
    {
        _aiService = aiService;
    }

    public async Task<WidgetIncrementalI18nBatchResult> RunIncrementalI18nQueueAsync(
        string providerId,
        CustomWidgetI18nSyncPlan plan,
        IReadOnlyList<string>? onlyCultures = null,
        Action<LocaleTranslationProgressUpdate>? onLocaleProgress = null,
        Func<string, IReadOnlyDictionary<string, string>, CustomWidgetLocaleCatalog, Task>? onLocaleCompletedAsync = null,
        CancellationToken cancellationToken = default,
        int? maxConcurrencyOverride = null)
    {
        ArgumentNullException.ThrowIfNull(plan);

        HashSet<string>? filterSet = onlyCultures is { Count: > 0 }
            ? new HashSet<string>(
                onlyCultures.Select(CustomWidgetLocaleCatalog.NormalizeLocaleFileCode),
                StringComparer.OrdinalIgnoreCase)
            : null;

        var scopedPlans = new List<CustomWidgetLocaleTaskPlan>(plan.LocalePlans.Count);
        foreach (CustomWidgetLocaleTaskPlan lp in plan.LocalePlans)
        {
            if (filterSet is null || filterSet.Contains(lp.Culture))
            {
                scopedPlans.Add(lp);
            }
        }

        var skippedLocales = new List<string>();
        var pendingPlans = new List<CustomWidgetLocaleTaskPlan>();
        foreach (CustomWidgetLocaleTaskPlan lp in scopedPlans)
        {
            if (!lp.NeedsTranslation)
            {
                skippedLocales.Add(lp.Culture);
                onLocaleProgress?.Invoke(new LocaleTranslationProgressUpdate(
                    lp.Culture,
                    LocaleTranslationJobState.SkippedUpToDate,
                    TranslatedCount: 0,
                    TotalDeltaCount: 0,
                    ReadyTotalKeys: lp.TotalRequiredKeys,
                    RequiredTotalKeys: lp.TotalRequiredKeys));
            }
            else
            {
                pendingPlans.Add(lp);
                onLocaleProgress?.Invoke(new LocaleTranslationProgressUpdate(
                    lp.Culture,
                    LocaleTranslationJobState.Queued,
                    TranslatedCount: 0,
                    TotalDeltaCount: lp.DeltaKeys.Count,
                    ReadyTotalKeys: lp.ReadyKeyCount,
                    RequiredTotalKeys: lp.TotalRequiredKeys));
            }
        }

        if (pendingPlans.Count == 0)
        {
            return new WidgetIncrementalI18nBatchResult(
                Success: true,
                Cancelled: false,
                CompiledManifestJson: plan.CompiledManifestJson,
                ManifestChanged: plan.ManifestChanged,
                UpdatedCatalog: plan.SeededCatalog,
                CompletedLocales: [],
                SkippedLocales: skippedLocales,
                FailedLocales: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                ErrorMessage: null,
                ProviderId: providerId,
                EffectiveModel: null);
        }

        ResolvedAiInvocation resolved = _aiService.ResolveEffectiveSettingsForProfile(
            providerId,
            AiTaskProfile.FastMicroTask,
            out string? originalModelFallback);

        if (resolved.Preset.RequiresApiKey && string.IsNullOrWhiteSpace(resolved.ApiKey))
        {
            string missingKeyErr = Localized.T("CustomWidget.AiApi.Error.MissingConfiguredApiKey");
            var missingFailed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (CustomWidgetLocaleTaskPlan lp in pendingPlans)
            {
                missingFailed[lp.Culture] = missingKeyErr;
                onLocaleProgress?.Invoke(new LocaleTranslationProgressUpdate(
                    lp.Culture,
                    LocaleTranslationJobState.Failed,
                    TranslatedCount: 0,
                    TotalDeltaCount: lp.DeltaKeys.Count,
                    ReadyTotalKeys: lp.ReadyKeyCount,
                    RequiredTotalKeys: lp.TotalRequiredKeys,
                    ErrorMessage: missingKeyErr));
            }

            return new WidgetIncrementalI18nBatchResult(
                Success: false,
                Cancelled: false,
                CompiledManifestJson: plan.CompiledManifestJson,
                ManifestChanged: plan.ManifestChanged,
                UpdatedCatalog: plan.SeededCatalog,
                CompletedLocales: [],
                SkippedLocales: skippedLocales,
                FailedLocales: missingFailed,
                ErrorMessage: missingKeyErr,
                ProviderId: providerId,
                EffectiveModel: resolved.Model);
        }

        int initialConcurrency = maxConcurrencyOverride.HasValue
            ? Math.Clamp(maxConcurrencyOverride.Value, 1, 6)
            : (AiContextBudgetResolver.IsLocalModelRoute(resolved) ? 1 : 3);
        int workerCount = Math.Min(initialConcurrency, pendingPlans.Count);

        var jobQueue = new ConcurrentQueue<CustomWidgetLocaleTaskPlan>(pendingPlans);
        var catalogLock = new object();
        using var checkpointGate = new SemaphoreSlim(1, 1);
        using var singleThrottleGate = new SemaphoreSlim(1, 1);
        int throttledToSingle = 0;

        CustomWidgetLocaleCatalog workingCatalog = plan.SeededCatalog;
        var completedList = new List<string>();
        var failedDict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        async Task WorkerLoopAsync()
        {
            while (jobQueue.TryDequeue(out CustomWidgetLocaleTaskPlan? taskPlan))
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    onLocaleProgress?.Invoke(new LocaleTranslationProgressUpdate(
                        taskPlan.Culture,
                        LocaleTranslationJobState.Cancelled,
                        TranslatedCount: 0,
                        TotalDeltaCount: taskPlan.DeltaKeys.Count,
                        ReadyTotalKeys: taskPlan.ReadyKeyCount,
                        RequiredTotalKeys: taskPlan.TotalRequiredKeys));
                    continue;
                }

                try
                {
                    (bool ok, Dictionary<string, string>? validMerged, IReadOnlyList<string> stillBroken, string? err) =
                        await ExecuteSingleLocaleTranslationJobAsync(
                            resolved,
                            originalModelFallback,
                            plan,
                            taskPlan,
                            singleThrottleGate,
                            () => Volatile.Read(ref throttledToSingle) == 1,
                            () => Interlocked.Exchange(ref throttledToSingle, 1),
                            onLocaleProgress,
                            cancellationToken);

                    if (validMerged is { Count: > 0 })
                    {
                        CustomWidgetLocaleCatalog snapshot;
                        lock (catalogLock)
                        {
                            workingCatalog = workingCatalog.WithLocale(taskPlan.Culture, validMerged, plan.NextMeta);
                            snapshot = workingCatalog;
                            if (ok && stillBroken.Count == 0)
                            {
                                completedList.Add(taskPlan.Culture);
                            }
                            else
                            {
                                failedDict[taskPlan.Culture] = err ?? Localized.T("CustomWidget.AiApi.Error.ValidationFailed");
                            }
                        }

                        if (onLocaleCompletedAsync is not null)
                        {
                            await checkpointGate.WaitAsync(CancellationToken.None);
                            try
                            {
                                await onLocaleCompletedAsync(taskPlan.Culture, validMerged, snapshot);
                            }
                            finally
                            {
                                checkpointGate.Release();
                            }
                        }

                        if (ok && stillBroken.Count == 0)
                        {
                            onLocaleProgress?.Invoke(new LocaleTranslationProgressUpdate(
                                taskPlan.Culture,
                                LocaleTranslationJobState.Completed,
                                TranslatedCount: taskPlan.DeltaKeys.Count,
                                TotalDeltaCount: taskPlan.DeltaKeys.Count,
                                ReadyTotalKeys: taskPlan.TotalRequiredKeys,
                                RequiredTotalKeys: taskPlan.TotalRequiredKeys));
                        }
                        else
                        {
                            int validDeltaDone = Math.Max(0, validMerged.Count - taskPlan.ReadyKeyCount);
                            onLocaleProgress?.Invoke(new LocaleTranslationProgressUpdate(
                                taskPlan.Culture,
                                LocaleTranslationJobState.Failed,
                                TranslatedCount: validDeltaDone,
                                TotalDeltaCount: taskPlan.DeltaKeys.Count,
                                ReadyTotalKeys: validMerged.Count,
                                RequiredTotalKeys: taskPlan.TotalRequiredKeys,
                                ErrorMessage: err));
                        }
                    }
                    else if (cancellationToken.IsCancellationRequested)
                    {
                        onLocaleProgress?.Invoke(new LocaleTranslationProgressUpdate(
                            taskPlan.Culture,
                            LocaleTranslationJobState.Cancelled,
                            TranslatedCount: 0,
                            TotalDeltaCount: taskPlan.DeltaKeys.Count,
                            ReadyTotalKeys: taskPlan.ReadyKeyCount,
                            RequiredTotalKeys: taskPlan.TotalRequiredKeys));
                    }
                    else
                    {
                        string failMsg = err ?? Localized.T("CustomWidget.AiApi.Error.InvocationFailed");
                        lock (catalogLock)
                        {
                            failedDict[taskPlan.Culture] = failMsg;
                        }

                        onLocaleProgress?.Invoke(new LocaleTranslationProgressUpdate(
                            taskPlan.Culture,
                            LocaleTranslationJobState.Failed,
                            TranslatedCount: 0,
                            TotalDeltaCount: taskPlan.DeltaKeys.Count,
                            ReadyTotalKeys: taskPlan.ReadyKeyCount,
                            RequiredTotalKeys: taskPlan.TotalRequiredKeys,
                            ErrorMessage: failMsg));
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    onLocaleProgress?.Invoke(new LocaleTranslationProgressUpdate(
                        taskPlan.Culture,
                        LocaleTranslationJobState.Cancelled,
                        TranslatedCount: 0,
                        TotalDeltaCount: taskPlan.DeltaKeys.Count,
                        ReadyTotalKeys: taskPlan.ReadyKeyCount,
                        RequiredTotalKeys: taskPlan.TotalRequiredKeys));
                }
                catch (Exception ex)
                {
                    lock (catalogLock)
                    {
                        failedDict[taskPlan.Culture] = ex.Message;
                    }

                    onLocaleProgress?.Invoke(new LocaleTranslationProgressUpdate(
                        taskPlan.Culture,
                        LocaleTranslationJobState.Failed,
                        TranslatedCount: 0,
                        TotalDeltaCount: taskPlan.DeltaKeys.Count,
                        ReadyTotalKeys: taskPlan.ReadyKeyCount,
                        RequiredTotalKeys: taskPlan.TotalRequiredKeys,
                        ErrorMessage: ex.Message));
                }
            }
        }

        Task[] workers = new Task[workerCount];
        for (int i = 0; i < workerCount; i++)
        {
            workers[i] = WorkerLoopAsync();
        }

        await Task.WhenAll(workers);

        bool wasCancelled = cancellationToken.IsCancellationRequested;
        bool batchOk = !wasCancelled && failedDict.Count == 0;
        string? summaryErr = failedDict.Count > 0
            ? failedDict.Values.FirstOrDefault()
            : (wasCancelled ? Localized.T("CustomWidget.AiApi.Error.GenerationCancelled") : null);

        return new WidgetIncrementalI18nBatchResult(
            Success: batchOk,
            Cancelled: wasCancelled,
            CompiledManifestJson: plan.CompiledManifestJson,
            ManifestChanged: plan.ManifestChanged,
            UpdatedCatalog: workingCatalog,
            CompletedLocales: completedList,
            SkippedLocales: skippedLocales,
            FailedLocales: failedDict,
            ErrorMessage: summaryErr,
            ProviderId: providerId,
            EffectiveModel: resolved.Model);
    }

    internal async Task<(bool Success, Dictionary<string, string>? MergedValidEntries, IReadOnlyList<string> StillBrokenKeys, string? Error)>
        ExecuteSingleLocaleTranslationJobAsync(
            ResolvedAiInvocation resolved,
            string? originalModelFallback,
            CustomWidgetI18nSyncPlan plan,
            CustomWidgetLocaleTaskPlan taskPlan,
            SemaphoreSlim singleThrottleGate,
            Func<bool> isThrottledToSingle,
            Action activateSingleThrottle,
            Action<LocaleTranslationProgressUpdate>? onLocaleProgress,
            CancellationToken cancellationToken)
    {
        const int MaxLocaleKeysPerBatch = 25;
        const string systemPrompt =
            "You are a native desktop UI localization specialist for DeskBox widgets. " +
            "Translate the provided source UI dictionary into the requested target language. " +
            "Return ONLY a single flat valid JSON object mapping each exact input key to its translated string. " +
            "Never wrap in commentary, never rename or omit keys, and preserve all positional placeholders such as {0}, {1} verbatim.";

        bool baseContainsCjk = plan.BaseEntries.Values.Any(CustomWidgetI18nEngine.ContainsCjk);
        var acceptedDelta = new Dictionary<string, string>(StringComparer.Ordinal);
        var brokenKeys = new List<string>();

        onLocaleProgress?.Invoke(new LocaleTranslationProgressUpdate(
            taskPlan.Culture,
            LocaleTranslationJobState.Running,
            TranslatedCount: 0,
            TotalDeltaCount: taskPlan.DeltaKeys.Count,
            ReadyTotalKeys: taskPlan.ReadyKeyCount,
            RequiredTotalKeys: taskPlan.TotalRequiredKeys));

        for (int offset = 0; offset < taskPlan.DeltaKeys.Count; offset += MaxLocaleKeysPerBatch)
        {
            IReadOnlyList<string> batchKeys = taskPlan.DeltaKeys.Count <= MaxLocaleKeysPerBatch
                ? taskPlan.DeltaKeys
                : taskPlan.DeltaKeys.Skip(offset).Take(MaxLocaleKeysPerBatch).ToArray();
            var batchSet = new HashSet<string>(batchKeys, StringComparer.Ordinal);
            int batchMaxTokens = Math.Clamp(batchKeys.Count * 96 + 512, 1536, 4096);
            string userPrompt = BuildDeltaLocaleUserPrompt(plan.BaseLocale, taskPlan, plan.BaseEntries, batchKeys, isRepair: false);

            (bool httpOk, string rawReply, string? httpErr) = await SendLocaleDeltaWithRetryAsync(
                resolved,
                originalModelFallback,
                systemPrompt,
                userPrompt,
                taskPlan,
                batchSet,
                baseTranslatedOffset: acceptedDelta.Count,
                singleThrottleGate,
                isThrottledToSingle,
                activateSingleThrottle,
                onLocaleProgress,
                cancellationToken,
                maxOutputTokens: batchMaxTokens);

            if (!httpOk)
            {
                if (acceptedDelta.Count == 0)
                {
                    return (false, null, taskPlan.DeltaKeys, httpErr);
                }

                brokenKeys.AddRange(batchKeys);
                continue;
            }

            Dictionary<string, string> candidateDict = TryParseTranslationReplyDictionary(rawReply);
            foreach (string key in batchKeys)
            {
                string sourceText = plan.BaseEntries[key];
                if (candidateDict.TryGetValue(key, out string? val) &&
                    IsAcceptableLocaleEntry(taskPlan.Culture, baseContainsCjk, sourceText, val))
                {
                    acceptedDelta[key] = val.Trim();
                }
                else
                {
                    brokenKeys.Add(key);
                }
            }
        }

        // 1-pass targeted micro-repair when any key was omitted or lost a {0} placeholder
        if (brokenKeys.Count > 0 && !cancellationToken.IsCancellationRequested)
        {
            var remainingBroken = new List<string>();
            for (int offset = 0; offset < brokenKeys.Count; offset += MaxLocaleKeysPerBatch)
            {
                IReadOnlyList<string> repairBatch = brokenKeys.Count <= MaxLocaleKeysPerBatch
                    ? brokenKeys
                    : brokenKeys.Skip(offset).Take(MaxLocaleKeysPerBatch).ToArray();
                string repairUserPrompt = BuildDeltaLocaleUserPrompt(
                    plan.BaseLocale,
                    taskPlan,
                    plan.BaseEntries,
                    repairBatch,
                    isRepair: true);
                var repairSet = new HashSet<string>(repairBatch, StringComparer.Ordinal);
                int repairMaxTokens = Math.Clamp(repairBatch.Count * 96 + 512, 1536, 4096);

                (bool repairHttpOk, string repairRawReply, _) = await SendLocaleDeltaWithRetryAsync(
                    resolved,
                    originalModelFallback,
                    systemPrompt,
                    repairUserPrompt,
                    taskPlan,
                    repairSet,
                    baseTranslatedOffset: acceptedDelta.Count,
                    singleThrottleGate,
                    isThrottledToSingle,
                    activateSingleThrottle,
                    onLocaleProgress,
                    cancellationToken,
                    maxOutputTokens: repairMaxTokens);

                if (repairHttpOk)
                {
                    Dictionary<string, string> repairedDict = TryParseTranslationReplyDictionary(repairRawReply);
                    foreach (string key in repairBatch)
                    {
                        string sourceText = plan.BaseEntries[key];
                        if (repairedDict.TryGetValue(key, out string? val) &&
                            IsAcceptableLocaleEntry(taskPlan.Culture, baseContainsCjk, sourceText, val))
                        {
                            acceptedDelta[key] = val.Trim();
                        }
                        else
                        {
                            remainingBroken.Add(key);
                        }
                    }
                }
                else
                {
                    remainingBroken.AddRange(repairBatch);
                }
            }

            brokenKeys = remainingBroken;
        }

        if (acceptedDelta.Count == 0 && brokenKeys.Count > 0)
        {
            return (false, null, brokenKeys, Localized.T("CustomWidget.AiApi.Error.MalformedResponse"));
        }

        var merged = new Dictionary<string, string>(plan.RequiredKeys.Count, StringComparer.Ordinal);
        foreach (string reqKey in plan.RequiredKeys)
        {
            if (acceptedDelta.TryGetValue(reqKey, out string? freshVal))
            {
                merged[reqKey] = freshVal;
            }
            else if (taskPlan.ExistingValidEntries.TryGetValue(reqKey, out string? existingVal))
            {
                merged[reqKey] = existingVal;
            }
        }

        if (brokenKeys.Count > 0)
        {
            string errDetail = Localized.Format(
                "CustomWidget.Workshop.I18n.Error.InvalidPlaceholdersFormat",
                brokenKeys.Count,
                string.Join(", ", brokenKeys.Take(4)));
            return (false, merged.Count > 0 ? merged : null, brokenKeys, errDetail);
        }

        return (true, merged, [], null);
    }

    internal static bool IsAcceptableLocaleEntry(
        string targetCulture,
        bool baseContainsCjk,
        string sourceText,
        string? candidateTranslation)
    {
        if (string.IsNullOrWhiteSpace(candidateTranslation))
        {
            return false;
        }

        if (!CustomWidgetI18nEngine.HasMatchingPlaceholders(sourceText, candidateTranslation))
        {
            return false;
        }

        if (CustomWidgetI18nEngine.HasUntranslatedCjkResidue(
                targetCulture,
                baseContainsCjk,
                sourceText,
                candidateTranslation))
        {
            return false;
        }

        return true;
    }

    internal static string BuildDeltaLocaleUserPrompt(
        string baseLocale,
        CustomWidgetLocaleTaskPlan taskPlan,
        IReadOnlyDictionary<string, string> baseEntries,
        IReadOnlyList<string> keysToTranslate,
        bool isRepair)
    {
        var sourceSubset = new Dictionary<string, string>(keysToTranslate.Count, StringComparer.Ordinal);
        foreach (string key in keysToTranslate)
        {
            if (baseEntries.TryGetValue(key, out string? srcVal))
            {
                sourceSubset[key] = srcVal;
            }
        }

        string sourceJson = CustomWidgetLocaleCatalog.FormatLocaleDictionaryJson(sourceSubset, keysToTranslate);
        var sb = new StringBuilder(sourceJson.Length + 512);
        // KV Prefix-Cache Friendly Order: static rules + identical source locale + identical sourceJson FIRST,
        // and per-request Target locale AT THE VERY END so all 12 concurrent locale tasks share the same prefix!
        sb.AppendLine("Rules:");
        sb.AppendLine("1. Translate concisely and idiomatically for compact desktop widget UI cards and buttons.");
        sb.AppendLine("2. Keep every positional placeholder (`{0}`, `{1}`, `{2}`) intact. You may reorder `{0}` and `{1}` for natural grammar, never delete them.");
        sb.AppendLine("3. Keep technical acronyms (`CPU`, `RAM`, `GPU`, `API`, `MCP`, `RSS`, `JSON`, `HTTP`, `URL`) and emojis unchanged.");
        sb.Append("4. Output ONLY a flat JSON object containing these exact ").Append(keysToTranslate.Count).AppendLine(" keys.");
        sb.Append("Source locale: ").AppendLine(baseLocale);
        sb.AppendLine("Source JSON:");
        sb.AppendLine(sourceJson);
        if (isRepair)
        {
            sb.AppendLine("REPAIR PASS: The previous response omitted keys or dropped required `{0}` / `{1}` placeholders.");
        }

        sb.Append("Target locale: ").Append(taskPlan.Culture)
          .Append(" (").Append(taskPlan.NativeName).Append(" / ").Append(taskPlan.DisplayName).AppendLine(")");
        return sb.ToString();
    }

    internal static Dictionary<string, string> TryParseTranslationReplyDictionary(string? rawReply)
    {
        string extracted = AiChatTransportClient.ExtractStructuredJson(rawReply);
        if (string.IsNullOrWhiteSpace(extracted))
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }

        try
        {
            return CustomWidgetLocaleCatalog.ParseSingleLocaleDictionary(extracted);
        }
        catch
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }
    }

    internal async Task<(bool Success, string Content, string? Error)> SendLocaleDeltaWithRetryAsync(
        ResolvedAiInvocation resolved,
        string? originalModelFallback,
        string systemPrompt,
        string userPrompt,
        CustomWidgetLocaleTaskPlan taskPlan,
        HashSet<string> deltaKeySet,
        int baseTranslatedOffset,
        SemaphoreSlim singleThrottleGate,
        Func<bool> isThrottledToSingle,
        Action activateSingleThrottle,
        Action<LocaleTranslationProgressUpdate>? onLocaleProgress,
        CancellationToken cancellationToken,
        int? maxOutputTokens = null)
    {
        const int maxRetries = 2;
        ResolvedAiInvocation activeInvocation = resolved;
        bool includeThinkingParams = activeInvocation.ThinkingMode != AiThinkingMode.Auto &&
                                     !_aiService.IsThinkingParamsUnsupportedRoute(activeInvocation);

        for (int attempt = 0; attempt <= maxRetries; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            bool acquiredSingleGate = false;
            if (isThrottledToSingle())
            {
                await singleThrottleGate.WaitAsync(cancellationToken);
                acquiredSingleGate = true;
            }

            int statusCode;
            TimeSpan? retryAfter;
            bool ok;
            string content;
            string? err;

            try
            {
                (ok, statusCode, retryAfter, content, err) = await StreamSingleLocaleCompletionAttemptAsync(
                    activeInvocation,
                    systemPrompt,
                    userPrompt,
                    includeThinkingParams,
                    taskPlan,
                    deltaKeySet,
                    baseTranslatedOffset,
                    onLocaleProgress,
                    cancellationToken,
                    maxOutputTokens);

                if (!ok && statusCode is 400 or 422)
                {
                    includeThinkingParams = false;
                    (ok, statusCode, retryAfter, content, err) = await StreamSingleLocaleCompletionAttemptAsync(
                        activeInvocation,
                        systemPrompt,
                        userPrompt,
                        includeThinkingParams: false,
                        taskPlan,
                        deltaKeySet,
                        baseTranslatedOffset,
                        onLocaleProgress,
                        cancellationToken,
                        maxOutputTokens,
                        omitTemperature: true);
                }

                if (!ok &&
                    !string.IsNullOrWhiteSpace(originalModelFallback) &&
                    !string.Equals(originalModelFallback, activeInvocation.Model, StringComparison.OrdinalIgnoreCase) &&
                    statusCode is 400 or 403 or 404 or 422)
                {
                    activeInvocation = activeInvocation with
                    {
                        Model = originalModelFallback,
                        ThinkingMode = AiThinkingMode.Auto
                    };
                    includeThinkingParams = false;
                    (ok, statusCode, retryAfter, content, err) = await StreamSingleLocaleCompletionAttemptAsync(
                        activeInvocation,
                        systemPrompt,
                        userPrompt,
                        includeThinkingParams: false,
                        taskPlan,
                        deltaKeySet,
                        baseTranslatedOffset,
                        onLocaleProgress,
                        cancellationToken,
                        maxOutputTokens,
                        omitTemperature: true);
                }
            }
            finally
            {
                if (acquiredSingleGate)
                {
                    singleThrottleGate.Release();
                }
            }

            if (ok)
            {
                return (true, content, null);
            }

            bool isRateLimitedOrTransient = statusCode is 429 or 502 or 503 or 504;
            if (!isRateLimitedOrTransient || attempt >= maxRetries)
            {
                return (false, string.Empty, err);
            }

            if (statusCode is 429 or 503)
            {
                activateSingleThrottle();
            }

            TimeSpan backoff = retryAfter is { TotalMilliseconds: > 0 } headerDelay
                ? TimeSpan.FromMilliseconds(Math.Clamp(headerDelay.TotalMilliseconds, 400, 15_000))
                : TimeSpan.FromMilliseconds(1500 * Math.Pow(2, attempt));

            onLocaleProgress?.Invoke(new LocaleTranslationProgressUpdate(
                taskPlan.Culture,
                LocaleTranslationJobState.Retrying,
                TranslatedCount: baseTranslatedOffset,
                TotalDeltaCount: taskPlan.DeltaKeys.Count,
                ReadyTotalKeys: taskPlan.ReadyKeyCount + baseTranslatedOffset,
                RequiredTotalKeys: taskPlan.TotalRequiredKeys,
                ErrorMessage: err));

            await Task.Delay(backoff, cancellationToken);
        }

        return (false, string.Empty, Localized.T("CustomWidget.AiApi.Error.InvocationFailed"));
    }

    internal Task<(bool Success, int StatusCode, TimeSpan? RetryAfter, string Content, string? Error)>
        StreamSingleLocaleCompletionAttemptAsync(
            ResolvedAiInvocation resolved,
            string systemPrompt,
            string userPrompt,
            bool includeThinkingParams,
            CustomWidgetLocaleTaskPlan taskPlan,
            HashSet<string> deltaKeySet,
            int baseTranslatedOffset,
            Action<LocaleTranslationProgressUpdate>? onLocaleProgress,
            CancellationToken cancellationToken,
            int? maxOutputTokens = null,
            bool omitTemperature = false)
    {
        int lastCountedKeys = -1;
        Action<string>? onVisibleSnapshot = onLocaleProgress is null
            ? null
            : visible =>
            {
                int completedInStream = CountCompletedDeltaKeysInPartialJson(visible, deltaKeySet);
                if (completedInStream != lastCountedKeys)
                {
                    lastCountedKeys = completedInStream;
                    int totalDone = Math.Min(taskPlan.DeltaKeys.Count, baseTranslatedOffset + completedInStream);
                    onLocaleProgress(new LocaleTranslationProgressUpdate(
                        taskPlan.Culture,
                        LocaleTranslationJobState.Running,
                        TranslatedCount: totalDone,
                        TotalDeltaCount: taskPlan.DeltaKeys.Count,
                        ReadyTotalKeys: Math.Min(taskPlan.TotalRequiredKeys, taskPlan.ReadyKeyCount + totalDone),
                        RequiredTotalKeys: taskPlan.TotalRequiredKeys));
                }
            };

        return AiChatTransportClient.SendMicroTaskStreamingCompletionAsync(
            _aiService,
            resolved,
            systemPrompt,
            userPrompt,
            includeThinkingParams,
            maxOutputTokens: maxOutputTokens,
            onVisibleSnapshot: onVisibleSnapshot,
            cancellationToken: cancellationToken,
            omitTemperature: omitTemperature,
            includeStreamUsageOptions: true);
    }

    internal static int CountCompletedDeltaKeysInPartialJson(string partialText, HashSet<string> expectedKeys)
    {
        if (string.IsNullOrWhiteSpace(partialText) || expectedKeys.Count == 0)
        {
            return 0;
        }

        int braceStart = partialText.IndexOf('{');
        if (braceStart < 0)
        {
            return 0;
        }

        var matched = new HashSet<string>(StringComparer.Ordinal);
        int i = braceStart + 1;
        int n = partialText.Length;

        while (i < n)
        {
            while (i < n && (char.IsWhiteSpace(partialText[i]) || partialText[i] == ','))
            {
                i++;
            }

            if (i >= n || partialText[i] == '}')
            {
                break;
            }

            if (!TryScanCompleteJsonString(partialText, ref i, out string? key))
            {
                break;
            }

            while (i < n && char.IsWhiteSpace(partialText[i]))
            {
                i++;
            }

            if (i >= n || partialText[i] != ':')
            {
                break;
            }
            i++; // skip ':'

            while (i < n && char.IsWhiteSpace(partialText[i]))
            {
                i++;
            }

            if (i >= n)
            {
                break;
            }

            if (partialText[i] == '"')
            {
                if (!TryScanCompleteJsonString(partialText, ref i, out _))
                {
                    break;
                }

                if (key is not null && expectedKeys.Contains(key))
                {
                    matched.Add(key);
                }
            }
            else
            {
                // Skip non-string value or stop if incomplete
                break;
            }
        }

        return matched.Count;
    }

    internal static bool TryScanCompleteJsonString(string text, ref int index, out string? unescaped)
    {
        unescaped = null;
        if (index >= text.Length || text[index] != '"')
        {
            return false;
        }

        int i = index + 1;
        var sb = new StringBuilder();
        bool escaped = false;
        while (i < text.Length)
        {
            char c = text[i++];
            if (escaped)
            {
                sb.Append(c);
                escaped = false;
                continue;
            }

            if (c == '\\')
            {
                escaped = true;
                continue;
            }

            if (c == '"')
            {
                index = i;
                unescaped = sb.ToString();
                return true;
            }

            sb.Append(c);
        }

        return false;
    }

    internal static CustomWidgetLocaleCatalog? MergeLocaleCatalogs(
        CustomWidgetLocaleCatalog? baseCatalog,
        CustomWidgetLocaleCatalog? overlayCatalog)
    {
        if (overlayCatalog is null || overlayCatalog.IsEmpty)
        {
            return baseCatalog;
        }

        if (baseCatalog is null || baseCatalog.IsEmpty)
        {
            return overlayCatalog;
        }

        var merged = new Dictionary<string, IReadOnlyDictionary<string, string>>(
            baseCatalog.Locales,
            StringComparer.OrdinalIgnoreCase);
        foreach ((string culture, IReadOnlyDictionary<string, string> entries) in overlayCatalog.Locales)
        {
            if (merged.TryGetValue(culture, out IReadOnlyDictionary<string, string>? prev))
            {
                var combinedDict = new Dictionary<string, string>(prev, StringComparer.Ordinal);
                foreach ((string k, string v) in entries)
                {
                    combinedDict[k] = v;
                }
                merged[culture] = combinedDict;
            }
            else
            {
                merged[culture] = entries;
            }
        }

        return new CustomWidgetLocaleCatalog(overlayCatalog.Meta ?? baseCatalog.Meta, merged);
    }

    internal static CustomWidgetLocaleCatalog? OverlayFreshTurnTranslations(
        CustomWidgetLocaleCatalog? compiledCatalog,
        CustomWidgetLocaleCatalog? freshTurnLocales)
    {
        if (compiledCatalog is null ||
            compiledCatalog.IsEmpty ||
            freshTurnLocales is null ||
            freshTurnLocales.IsEmpty)
        {
            return compiledCatalog;
        }

        string? baseLocale = compiledCatalog.Meta?.BaseLocale;
        IReadOnlyDictionary<string, string>? baseEntries = null;
        if (!string.IsNullOrWhiteSpace(baseLocale))
        {
            compiledCatalog.Locales.TryGetValue(baseLocale!, out baseEntries);
        }

        var updated = new Dictionary<string, IReadOnlyDictionary<string, string>>(
            compiledCatalog.Locales,
            StringComparer.OrdinalIgnoreCase);

        foreach ((string culture, IReadOnlyDictionary<string, string> freshEntries) in freshTurnLocales.Locales)
        {
            if (freshEntries.Count == 0 ||
                !CustomWidgetLocaleCatalog.IsValidCultureCode(culture) ||
                (!string.IsNullOrWhiteSpace(baseLocale) &&
                 string.Equals(culture, baseLocale, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            var mergedDict = updated.TryGetValue(culture, out IReadOnlyDictionary<string, string>? curDict)
                ? new Dictionary<string, string>(curDict, StringComparer.Ordinal)
                : new Dictionary<string, string>(StringComparer.Ordinal);

            foreach ((string key, string val) in freshEntries)
            {
                if (string.IsNullOrWhiteSpace(val))
                {
                    continue;
                }

                if (baseEntries is not null && !baseEntries.ContainsKey(key))
                {
                    continue;
                }

                mergedDict[key] = val;
            }

            if (mergedDict.Count > 0)
            {
                updated[culture] = mergedDict;
            }
        }

        return new CustomWidgetLocaleCatalog(compiledCatalog.Meta, updated);
    }

    internal static string ExtractInlineManifestLocalesAndStrip(
        string manifestJson,
        CustomWidgetLocaleCatalog? existingLocales,
        out CustomWidgetLocaleCatalog? effectiveLocales)
    {
        effectiveLocales = existingLocales;
        if (string.IsNullOrWhiteSpace(manifestJson))
        {
            return manifestJson;
        }

        try
        {
            using JsonDocument doc = JsonDocument.Parse(
                manifestJson,
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = true,
                    CommentHandling = JsonCommentHandling.Skip
                });
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return manifestJson;
            }

            bool hasInlineI18n = doc.RootElement.TryGetProperty("i18n", out JsonElement inlineI18nEl);
            bool hasInlineLocales = doc.RootElement.TryGetProperty("locales", out JsonElement inlineLocalesEl);
            if (!hasInlineI18n && !hasInlineLocales)
            {
                return manifestJson;
            }

            if (hasInlineLocales && inlineLocalesEl.ValueKind == JsonValueKind.Object)
            {
                CustomWidgetLocaleCatalog rescued = CustomWidgetLocaleCatalog.ParseCatalogElement(inlineLocalesEl);
                effectiveLocales = MergeLocaleCatalogs(effectiveLocales, rescued);
            }
            else if (hasInlineI18n && inlineI18nEl.ValueKind == JsonValueKind.Object)
            {
                CustomWidgetLocaleCatalog rescued = CustomWidgetLocaleCatalog.ParseCatalogElement(inlineI18nEl);
                effectiveLocales = MergeLocaleCatalogs(effectiveLocales, rescued);
            }

            using var ms = new MemoryStream();
            using (var writer = new Utf8JsonWriter(
                       ms,
                       new JsonWriterOptions
                       {
                           Indented = true,
                           Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
                       }))
            {
                writer.WriteStartObject();
                foreach (JsonProperty prop in doc.RootElement.EnumerateObject())
                {
                    if (string.Equals(prop.Name, "i18n", StringComparison.Ordinal) ||
                        string.Equals(prop.Name, "locales", StringComparison.Ordinal))
                    {
                        continue;
                    }
                    prop.WriteTo(writer);
                }
                writer.WriteEndObject();
            }

            return Encoding.UTF8.GetString(ms.ToArray());
        }
        catch
        {
            return manifestJson;
        }
    }

}
