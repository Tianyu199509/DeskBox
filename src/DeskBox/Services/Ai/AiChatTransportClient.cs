using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using DeskBox.Models;

namespace DeskBox.Services;

internal static partial class AiChatTransportClient
{
    internal static readonly TimeSpan StreamingOverallTimeout = TimeSpan.FromMinutes(5);
    internal static readonly TimeSpan StreamingInitialIdleTimeout = TimeSpan.FromSeconds(120);
    internal static readonly TimeSpan StreamingInterChunkIdleTimeout = TimeSpan.FromSeconds(75);
    internal const int MaxSseLineChars = 4 * 1024 * 1024;
    internal const int MaxSseCumulativeChars = 16 * 1024 * 1024;
    internal const int MaxTransportAttempts = 4;

    internal readonly record struct SseChunkUsage(
        bool HasUsage,
        int PromptTokens,
        int CachedPromptTokens,
        int CompletionTokens);

    internal static async Task<(bool Success, string Content, string? Error)> SendProbeCompletionAsync(
        DeskBoxAiService aiService,
        ResolvedAiInvocation resolved,
        bool includeThinkingParams,
        CancellationToken requestToken,
        CancellationToken callerToken = default)
    {
        (bool ok, int statusCode, string content, string? err) = await SendSingleNonStreamingCompletionAsync(
            aiService,
            resolved,
            systemPrompt: null,
            userPrompt: "Reply with OK.",
            includeThinkingParams: includeThinkingParams,
            isProbe: true,
            requestToken: requestToken,
            callerToken: callerToken);

        if (!ok && statusCode is 400 or 422)
        {
            (ok, _, content, err) = await SendSingleNonStreamingCompletionAsync(
                aiService,
                resolved,
                systemPrompt: null,
                userPrompt: "Reply with OK.",
                includeThinkingParams: false,
                isProbe: true,
                requestToken: requestToken,
                callerToken: callerToken,
                omitTemperature: true);
        }

        return (ok, string.IsNullOrWhiteSpace(content) ? "OK" : content, err);
    }

    /// <summary>
    /// Executes a bounded, AOT-safe structured JSON completion using role-based routing
    /// (defaults to <see cref="AiTaskProfile.FastMicroTask"/>).
    /// </summary>

    internal static async Task<(bool Success, int StatusCode, string Content, string? Error)> SendSingleNonStreamingCompletionAsync(
        DeskBoxAiService aiService,
        ResolvedAiInvocation resolved,
        string? systemPrompt,
        string userPrompt,
        bool includeThinkingParams,
        bool isProbe,
        CancellationToken requestToken,
        CancellationToken callerToken,
        int? maxOutputTokens = null,
        bool omitTemperature = false)
    {
        if (string.IsNullOrWhiteSpace(resolved.Endpoint))
        {
            return (false, 400, string.Empty, "Invalid HTTP/HTTPS endpoint URL.");
        }

        try
        {
            byte[] payload = BuildChatRequestPayload(
                resolved,
                systemPrompt: systemPrompt,
                userPrompt: userPrompt,
                stream: false,
                includeThinkingParams: includeThinkingParams,
                isProbe: isProbe,
                capMicroTaskTokens: !isProbe,
                microTaskMaxTokensOverride: maxOutputTokens,
                omitTemperature: omitTemperature);

            using var req = new HttpRequestMessage(HttpMethod.Post, resolved.Endpoint)
            {
                Content = CreateJsonContent(payload)
            };
            ApplyAuthHeaders(req, resolved.Endpoint, resolved.ApiKey);

            var callSw = Stopwatch.StartNew();
            using HttpResponseMessage resp = await aiService.HttpClient.SendAsync(req, requestToken);
            string respBody = await resp.Content.ReadAsStringAsync(requestToken);
            callSw.Stop();

            if (!resp.IsSuccessStatusCode)
            {
                return (false, (int)resp.StatusCode, string.Empty, FormatHttpError(resp.StatusCode, respBody));
            }

            if (TryExtractNonStreamingReply(respBody, out string content, out string reasoning))
            {
                SplitInlineThinkBlock(content, out _, out string visibleContent, out _);
                if (!string.IsNullOrWhiteSpace(visibleContent))
                {
                    if (!isProbe)
                    {
                        TryExtractUsageFromJson(respBody, out int pt, out int ctCached, out int comp);
                        aiService.RecordCallTelemetry(pt, ctCached, comp, Math.Max(1, callSw.ElapsedMilliseconds));
                    }

                    return (true, (int)resp.StatusCode, visibleContent, null);
                }

                if (isProbe)
                {
                    return (true, (int)resp.StatusCode, !string.IsNullOrWhiteSpace(reasoning) ? reasoning : "OK", null);
                }

                return (false, (int)resp.StatusCode, string.Empty, Localized.T("CustomWidget.AiApi.Error.EmptyContent"));
            }

            // Compatibility with OpenAI-compatible relays that return SSE chunks even when stream=false.
            if (respBody.Contains("data:", StringComparison.OrdinalIgnoreCase) &&
                TryExtractSseFullReply(respBody, out string sseContent, out string sseReasoning))
            {
                SplitInlineThinkBlock(sseContent, out _, out string visibleSse, out _);
                if (!string.IsNullOrWhiteSpace(visibleSse))
                {
                    return (true, (int)resp.StatusCode, visibleSse, null);
                }

                if (isProbe)
                {
                    return (true, (int)resp.StatusCode, !string.IsNullOrWhiteSpace(sseReasoning) ? sseReasoning : "OK", null);
                }

                return (false, (int)resp.StatusCode, string.Empty, Localized.T("CustomWidget.AiApi.Error.EmptyContent"));
            }

            if (TryExtractStructuredJsonErrorMessage(respBody) is { Length: > 0 } http200Err)
            {
                return (false, (int)resp.StatusCode, string.Empty, http200Err);
            }

            if (isProbe)
            {
                return (true, (int)resp.StatusCode, "OK", null);
            }

            return (false, (int)resp.StatusCode, string.Empty, Localized.T("CustomWidget.AiApi.Error.MalformedResponse"));
        }
        catch (OperationCanceledException) when (callerToken.IsCancellationRequested)
        {
            return (false, 0, string.Empty, Localized.T(
                isProbe ? "CustomWidget.AiApi.Error.ProbeCancelled" : "CustomWidget.AiApi.Error.GenerationCancelled"));
        }
        catch (OperationCanceledException)
        {
            return (false, 0, string.Empty, Localized.T(
                isProbe ? "CustomWidget.AiApi.Error.ProbeTimeout" : "CustomWidget.AiApi.Error.StreamTimeout"));
        }
        catch (Exception ex)
        {
            return (false, 0, string.Empty, ex.Message);
        }
    }

    internal static byte[] BuildChatRequestPayload(
        ResolvedAiInvocation resolved,
        string? systemPrompt,
        string userPrompt,
        bool stream,
        bool includeThinkingParams,
        bool isProbe,
        bool capMicroTaskTokens = false,
        int? microTaskMaxTokensOverride = null,
        bool includeExplicitStudioMaxTokens = false,
        int? studioMaxTokensOverride = null,
        IReadOnlyList<AiChatTurnMessage>? structuredTurns = null,
        bool includeStudioTools = false,
        bool includeStreamUsageOptions = false,
        bool forceIncludeReasoningHistory = false,
        bool omitTemperature = false,
        IReadOnlyList<AiChatTurnMessage>? prefixMessages = null)
    {
        bool reasoningOnly = AiProviderCatalog.IsReasoningOnlyModel(resolved.Model);
        int? effectiveMaxTokens = ResolveEffectiveMaxOutputTokens(
            resolved,
            reasoningOnly,
            isProbe,
            capMicroTaskTokens,
            microTaskMaxTokensOverride,
            includeExplicitStudioMaxTokens,
            studioMaxTokensOverride);

        bool emitThinkingParams = TryPlanVendorThinkingEmission(
            resolved,
            includeThinkingParams,
            isProbe,
            ref effectiveMaxTokens,
            out AiThinkingProtocol effectiveThinkingProtocol,
            out bool enableThinking,
            out int? claudeBudgetTokens);

        bool activeThinkingDisallowsCustomTemp =
            emitThinkingParams &&
            enableThinking &&
            effectiveThinkingProtocol is AiThinkingProtocol.ThinkingTypeObject or AiThinkingProtocol.EnableThinkingFlag;

        bool emitTemperature =
            !omitTemperature &&
            !reasoningOnly &&
            !AiProviderCatalog.IsFixedTemperatureModel(resolved.Model) &&
            !activeThinkingDisallowsCustomTemp;

        using var ms = new MemoryStream();
        using (var writer = new Utf8JsonWriter(
                   ms,
                   new JsonWriterOptions
                   {
                       Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
                   }))
        {
            writer.WriteStartObject();
            writer.WriteString("model", resolved.Model);
            if (emitTemperature)
            {
                writer.WriteNumber("temperature", 0.2);
            }

            writer.WriteBoolean("stream", stream);
            if (stream && includeStreamUsageOptions)
            {
                writer.WriteStartObject("stream_options");
                writer.WriteBoolean("include_usage", true);
                writer.WriteEndObject();
            }

            if (effectiveMaxTokens.HasValue)
            {
                if (reasoningOnly)
                {
                    writer.WriteNumber("max_completion_tokens", effectiveMaxTokens.Value);
                }
                else
                {
                    writer.WriteNumber("max_tokens", effectiveMaxTokens.Value);
                }
            }

            if (emitThinkingParams)
            {
                WriteVendorThinkingParametersCore(
                    writer,
                    effectiveThinkingProtocol,
                    resolved.Model,
                    enableThinking,
                    claudeBudgetTokens);
            }

            if (includeStudioTools && !isProbe)
            {
                StudioToolTransport.WriteOpenAiToolsArray(writer);
            }

            bool requireReasoningHistory = ShouldIncludeAssistantReasoningContentInHistory(
                resolved,
                includeThinkingParams,
                forceIncludeReasoningHistory);
            bool isDeepSeekRoute =
                string.Equals(resolved.Preset.Id, "deepseek", StringComparison.OrdinalIgnoreCase) ||
                resolved.Model.Contains("deepseek", StringComparison.OrdinalIgnoreCase);
            bool preserveNonEmptyReasoning = forceIncludeReasoningHistory ||
                                             isDeepSeekRoute ||
                                             (includeThinkingParams && resolved.ThinkingMode == AiThinkingMode.Auto);

            writer.WriteStartArray("messages");
            if (!string.IsNullOrWhiteSpace(systemPrompt))
            {
                string modelLeaf = resolved.Model.Contains('/')
                    ? resolved.Model[(resolved.Model.LastIndexOf('/') + 1)..]
                    : resolved.Model;
                writer.WriteStartObject();
                writer.WriteString(
                    "role",
                    reasoningOnly && modelLeaf.StartsWith("o1", StringComparison.OrdinalIgnoreCase) ? "user" : "system");
                writer.WriteString("content", systemPrompt);
                writer.WriteEndObject();
            }

            // Tier-2 KV-cacheable prefix messages (e.g. canonically sorted session capability domain packs)
            if (prefixMessages is { Count: > 0 })
            {
                foreach (AiChatTurnMessage prefix in prefixMessages)
                {
                    WriteStructuredChatTurn(writer, prefix, requireReasoningHistory, preserveNonEmptyReasoning);
                }
            }

            writer.WriteStartObject();
            writer.WriteString("role", "user");
            writer.WriteString("content", userPrompt);
            writer.WriteEndObject();

            if (structuredTurns is { Count: > 0 })
            {
                foreach (AiChatTurnMessage turn in structuredTurns)
                {
                    WriteStructuredChatTurn(writer, turn, requireReasoningHistory, preserveNonEmptyReasoning);
                }
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return ms.ToArray();
    }

    private static int? ResolveEffectiveMaxOutputTokens(
        ResolvedAiInvocation resolved,
        bool reasoningOnly,
        bool isProbe,
        bool capMicroTaskTokens,
        int? microTaskMaxTokensOverride,
        bool includeExplicitStudioMaxTokens,
        int? studioMaxTokensOverride)
    {
        if (isProbe)
        {
            return reasoningOnly ? 64 : 16;
        }

        if (capMicroTaskTokens)
        {
            return reasoningOnly
                ? Math.Clamp(microTaskMaxTokensOverride ?? 4096, 512, 8192)
                : Math.Clamp(microTaskMaxTokensOverride ?? 1536, 256, 4096);
        }

        if (includeExplicitStudioMaxTokens)
        {
            int defaultStudioTokens = IsStrict8kStudioEndpoint(resolved) ? 8192 : 16384;
            return Math.Clamp(studioMaxTokensOverride ?? defaultStudioTokens, 2048, 32768);
        }

        return null;
    }

    internal const int RunawayReasoningCharThreshold = 20_000;
    internal const long RunawayReasoningSoftTimeoutMs = 45_000;

    internal static bool ShouldAbortForRunawayReasoning(
        int totalUiContentChars,
        bool hasToolCalls,
        int totalThinkingChars,
        long elapsedMs,
        bool isReasoningOnlyModel,
        AiThinkingMode thinkingMode) =>
        totalUiContentChars == 0 &&
        !hasToolCalls &&
        totalThinkingChars > 0 &&
        !isReasoningOnlyModel &&
        thinkingMode != AiThinkingMode.Disabled &&
        (totalThinkingChars >= RunawayReasoningCharThreshold || elapsedMs >= RunawayReasoningSoftTimeoutMs);

    internal static void WriteStructuredChatTurn(
        Utf8JsonWriter writer,
        AiChatTurnMessage turn,
        bool requireReasoningContentOnAssistant,
        bool preserveNonEmptyReasoning)
    {
        if (string.IsNullOrWhiteSpace(turn.Role))
        {
            return;
        }

        writer.WriteStartObject();
        writer.WriteString("role", turn.Role);

        if (string.Equals(turn.Role, "tool", StringComparison.OrdinalIgnoreCase))
        {
            if (!string.IsNullOrWhiteSpace(turn.ToolCallId))
            {
                writer.WriteString("tool_call_id", turn.ToolCallId);
            }
            writer.WriteString("content", turn.Content ?? string.Empty);
            writer.WriteEndObject();
            return;
        }

        bool hasToolCalls = turn.ToolCalls is { Count: > 0 };
        if (string.Equals(turn.Role, "assistant", StringComparison.OrdinalIgnoreCase))
        {
            if (requireReasoningContentOnAssistant)
            {
                writer.WriteString("reasoning_content", turn.ReasoningContent ?? string.Empty);
            }
            else if (preserveNonEmptyReasoning && !string.IsNullOrWhiteSpace(turn.ReasoningContent))
            {
                writer.WriteString("reasoning_content", turn.ReasoningContent);
            }
        }

        writer.WriteString("content", turn.Content ?? string.Empty);

        if (hasToolCalls)
        {
            writer.WriteStartArray("tool_calls");
            foreach (AiToolCallSpec tc in turn.ToolCalls!)
            {
                writer.WriteStartObject();
                writer.WriteString("id", tc.Id);
                writer.WriteString("type", "function");
                writer.WriteStartObject("function");
                writer.WriteString("name", tc.Name);
                writer.WriteString("arguments", tc.ArgumentsJson);
                writer.WriteEndObject();
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
        }

        writer.WriteEndObject();
    }

    internal static ByteArrayContent CreateJsonContent(byte[] payload)
    {
        var content = new ByteArrayContent(payload);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json")
        {
            CharSet = "utf-8"
        };
        return content;
    }

    internal static void ApplyAuthHeaders(HttpRequestMessage req, string endpoint, string apiKey)
    {
        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        }

        if (endpoint.Contains("openrouter.ai", StringComparison.OrdinalIgnoreCase))
        {
            req.Headers.TryAddWithoutValidation("HTTP-Referer", "https://deskbox.app");
            req.Headers.TryAddWithoutValidation("X-Title", "DeskBox");
        }
    }

    internal static async Task<StudioStreamRoundOutcome> SendStudioStreamingRoundAsync(
        DeskBoxAiService aiService,
        ResolvedAiInvocation resolved,
        string nativeSystemPrompt,
        string blockSystemPrompt,
        string userPrompt,
        IReadOnlyList<AiChatTurnMessage> structuredTurns,
        bool includeStudioTools,
        bool isRepairRound,
        Stopwatch sw,
        Action<AiGenerationStreamProgress>? onProgress,
        CancellationToken cancellationToken,
        bool allowParamFallback = true,
        bool allowThinkingExhaustionRetry = true,
        int? studioMaxTokensOverride = null,
        bool retriedWithoutTools = false,
        bool retriedContextOverflow = false,
        Func<string>? compactUserPromptFactory = null,
        bool allowAutoSynthesisThinking = false,
        bool forceReasoningHistory = false,
        IReadOnlyList<AiChatTurnMessage>? prefixMessages = null)
    {
        if (string.IsNullOrWhiteSpace(resolved.Endpoint))
        {
            return new StudioStreamRoundOutcome(
                false,
                string.Empty,
                string.Empty,
                [],
                null,
                "Invalid HTTP/HTTPS endpoint URL.",
                retriedWithoutTools);
        }

        ResolvedAiInvocation currentResolved = resolved;
        string currentUserPrompt = userPrompt;
        IReadOnlyList<AiChatTurnMessage> currentTurns = structuredTurns;
        bool currentIncludeTools = includeStudioTools;
        bool currentAllowParamFallback = allowParamFallback;
        bool currentAllowThinkingExhaustionRetry = allowThinkingExhaustionRetry;
        int? currentMaxTokensOverride = studioMaxTokensOverride;
        bool currentRetriedWithoutTools = retriedWithoutTools;
        bool currentRetriedContextOverflow = retriedContextOverflow;
        bool currentForceReasoningHistory = forceReasoningHistory;
        bool currentOmitTemperature = false;
        ResolvedAiInvocation activeAttemptResolved = currentResolved;

        using var overallCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        overallCts.CancelAfter(StreamingOverallTimeout);

        try
        {
            for (int attempt = 0; attempt < MaxTransportAttempts; attempt++)
            {
                bool hasMoreAttempts = attempt + 1 < MaxTransportAttempts;

                // Phase-aware adaptive thinking: allow thinking on Round 0 (or at most one post-discovery architecture
                // synthesis round before the workspace has been mutated), then force fast non-thinking mode on post-mutation and repair rounds.
                bool shouldDowngradeSynthesisThinking = isRepairRound || (currentTurns.Count > 0 && !allowAutoSynthesisThinking);
                activeAttemptResolved = shouldDowngradeSynthesisThinking
                    ? StudioTurnStageRouter.DowngradeToFastNonThinkingInvocation(
                        currentResolved,
                        aiService.IsThinkingParamsUnsupportedRoute(currentResolved),
                        aiService.CredentialStore.Get(currentResolved.Preset.Id)?.FetchedModels)
                    : currentResolved;

                bool includeThinkingParams = currentAllowParamFallback && !aiService.IsThinkingParamsUnsupportedRoute(activeAttemptResolved);
                string effectiveSystemPrompt = currentIncludeTools ? nativeSystemPrompt : blockSystemPrompt;

                byte[] payload = BuildChatRequestPayload(
                    activeAttemptResolved,
                    effectiveSystemPrompt,
                    currentUserPrompt,
                    stream: true,
                    includeThinkingParams: includeThinkingParams,
                    isProbe: false,
                    includeExplicitStudioMaxTokens: true,
                    studioMaxTokensOverride: currentMaxTokensOverride,
                    structuredTurns: currentTurns,
                    includeStudioTools: currentIncludeTools,
                    includeStreamUsageOptions: currentAllowParamFallback && !AiContextBudgetResolver.IsLocalModelRoute(activeAttemptResolved),
                    forceIncludeReasoningHistory: currentForceReasoningHistory,
                    omitTemperature: currentOmitTemperature,
                    prefixMessages: prefixMessages);

                using var req = new HttpRequestMessage(HttpMethod.Post, activeAttemptResolved.Endpoint)
                {
                    Content = CreateJsonContent(payload)
                };
                req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
                req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                ApplyAuthHeaders(req, activeAttemptResolved.Endpoint, activeAttemptResolved.ApiKey);

                using HttpResponseMessage resp = await aiService.HttpClient.SendAsync(
                    req,
                    HttpCompletionOption.ResponseHeadersRead,
                    overallCts.Token);

                if (!resp.IsSuccessStatusCode)
                {
                    string errBody = await resp.Content.ReadAsStringAsync(overallCts.Token);
                    int statusCode = (int)resp.StatusCode;
                    string errExcerpt = SanitizeErrorBodyExcerpt(errBody);
                    bool hadPriorNativeToolCall = currentTurns.Any(static t => t.ToolCalls is { Count: > 0 });

                    if (hasMoreAttempts)
                    {
                        if (!currentForceReasoningHistory &&
                            statusCode is 400 or 422 &&
                            ErrorRequiresReasoningContentInHistory(errBody))
                        {
                            App.Log(
                                $"[StudioAI] HTTP {statusCode} missing reasoning_content in history (provider={activeAttemptResolved.Preset.Id}, model={activeAttemptResolved.Model}, err={errExcerpt}); retrying with reasoning_content preserved on all assistant turns");
                            currentForceReasoningHistory = true;
                            continue;
                        }

                        if (includeThinkingParams &&
                            activeAttemptResolved.ThinkingMode != AiThinkingMode.Auto &&
                            statusCode is 400 or 422 &&
                            ErrorMentionsUnsupportedThinkingParams(errBody))
                        {
                            aiService.MarkThinkingParamsUnsupportedRoute(activeAttemptResolved);
                            App.Log(
                                $"[StudioAI] HTTP {statusCode} unsupported thinking parameter (provider={activeAttemptResolved.Preset.Id}, model={activeAttemptResolved.Model}, thinking={activeAttemptResolved.ThinkingMode}, err={errExcerpt}); caching route and retrying without thinking params while preserving native tools & max_tokens");
                            currentResolved = activeAttemptResolved with { ThinkingMode = AiThinkingMode.Auto };
                            continue;
                        }

                        if (currentIncludeTools &&
                            statusCode is 400 or 422 or 501 &&
                            (statusCode == 501 || ErrorMentionsUnsupportedTools(errBody)))
                        {
                            if (!hadPriorNativeToolCall)
                            {
                                aiService.MarkToolsUnsupportedRoute(activeAttemptResolved);
                            }

                            App.Log(
                                $"[StudioAI] HTTP {statusCode} with native tools (provider={activeAttemptResolved.Preset.Id}, model={activeAttemptResolved.Model}, priorNativeTools={hadPriorNativeToolCall}, allowParamFallback={currentAllowParamFallback}, err={errExcerpt}); falling back to block protocol");
                            currentTurns = FlattenStructuredTurnsForBlockFallback(currentTurns);
                            currentIncludeTools = false;
                            currentRetriedWithoutTools = true;
                            continue;
                        }

                        if ((currentAllowParamFallback || !currentRetriedContextOverflow) &&
                            statusCode is 400 or 422 &&
                            !currentRetriedContextOverflow &&
                            AiContextBudgetResolver.ErrorLooksLikeContextOverflow(errBody, out int? parsedContextLimit))
                        {
                            int learnedLimit = parsedContextLimit ?? 16_384;
                            aiService.RecordLearnedContextLimit(activeAttemptResolved, learnedLimit);
                            currentUserPrompt = compactUserPromptFactory?.Invoke() ?? currentUserPrompt;
                            currentTurns = StudioContextCompactor.CompactSupersededStructuredTurns(
                                currentTurns,
                                compactAllWriteBundles: true,
                                forceCompactLatestForOverflow: true);
                            currentMaxTokensOverride = Math.Clamp(learnedLimit / 4, 2_048, 4_096);
                            currentRetriedContextOverflow = true;
                            App.Log(
                                $"[StudioAI] HTTP {statusCode} context overflow (provider={activeAttemptResolved.Preset.Id}, model={activeAttemptResolved.Model}, parsedLimit={learnedLimit}, err={errExcerpt}); compacting prompt+turns and retrying with max_tokens={currentMaxTokensOverride}");
                            continue;
                        }

                        if (currentAllowParamFallback && statusCode is 400 or 422)
                        {
                            if ((!currentMaxTokensOverride.HasValue || currentMaxTokensOverride.Value > 8192) &&
                                ErrorLooksLikeMaxTokensRejection(errBody))
                            {
                                App.Log(
                                    $"[StudioAI] HTTP {statusCode} on max_tokens (provider={activeAttemptResolved.Preset.Id}, model={activeAttemptResolved.Model}, thinking={activeAttemptResolved.ThinkingMode}, err={errExcerpt}); retrying with 8192 while preserving thinking params");
                                currentMaxTokensOverride = 8192;
                                continue;
                            }

                            App.Log(
                                $"[StudioAI] HTTP {statusCode} on request (provider={activeAttemptResolved.Preset.Id}, model={activeAttemptResolved.Model}, thinking={activeAttemptResolved.ThinkingMode}, err={errExcerpt}); retrying with conservative 8k parameter set (omitting temperature & optional params)");
                            currentAllowParamFallback = false;
                            currentMaxTokensOverride = Math.Min(currentMaxTokensOverride ?? 8192, 8192);
                            currentOmitTemperature = true;
                            continue;
                        }
                    }

                    App.Log(
                        $"[StudioAI] HTTP {statusCode} failure (provider={activeAttemptResolved.Preset.Id}, model={activeAttemptResolved.Model}, thinking={activeAttemptResolved.ThinkingMode}, repair={isRepairRound}, elapsedMs={sw.ElapsedMilliseconds}, err={errExcerpt})");
                    return new StudioStreamRoundOutcome(
                        false,
                        string.Empty,
                        string.Empty,
                        [],
                        null,
                        FormatHttpError(resp.StatusCode, errBody),
                        currentRetriedWithoutTools);
                }

                var callLatencySw = Stopwatch.StartNew();
                string? mediaType = resp.Content.Headers.ContentType?.MediaType;
                if (string.Equals(mediaType, "application/json", StringComparison.OrdinalIgnoreCase))
                {
                    string jsonBody = await resp.Content.ReadAsStringAsync(overallCts.Token);
                    if (TryExtractNonStreamingReply(
                            jsonBody,
                            out string fullContent,
                            out string fullReasoning,
                            out string? nonStreamFinishReason,
                            out List<AiToolCallSpec> nonStreamTools))
                    {
                        if (TryFinalizeNonStreamingStudioOutcome(
                                aiService,
                                activeAttemptResolved,
                                jsonBody,
                                fullContent,
                                fullReasoning,
                                nonStreamFinishReason,
                                nonStreamTools,
                                isRepairRound,
                                hasMoreAttempts && currentAllowThinkingExhaustionRetry,
                                currentRetriedWithoutTools,
                                callLatencySw,
                                sw,
                                onProgress,
                                out StudioStreamRoundOutcome nonStreamOutcome,
                                out ResolvedAiInvocation nonStreamRetryResolved))
                        {
                            return nonStreamOutcome;
                        }

                        AppendLengthStopRetryHint(currentUserPrompt, currentTurns, out currentUserPrompt, out currentTurns);
                        currentResolved = nonStreamRetryResolved;
                        currentAllowParamFallback = true;
                        currentAllowThinkingExhaustionRetry = false;
                        continue;
                    }

                    App.Log(
                        $"[StudioAI] Non-streaming malformed response (provider={activeAttemptResolved.Preset.Id}, model={activeAttemptResolved.Model}, bodyChars={jsonBody.Length})");
                    return new StudioStreamRoundOutcome(
                        false,
                        string.Empty,
                        string.Empty,
                        [],
                        null,
                        TryExtractStructuredJsonErrorMessage(jsonBody) ?? Localized.T("CustomWidget.AiApi.Error.MalformedResponse"),
                        currentRetriedWithoutTools);
                }

                var accumulator = new StreamingTurnAccumulator();
                int sseDataEvents = 0;
                string? lastFinishReason = null;
                int ssePromptTokens = 0;
                int sseCachedTokens = 0;
                int sseCompletionTokens = 0;
                long lastEmitMs = -1000;
                AiGenerationStage lastStage = AiGenerationStage.Connecting;
                string? lastActiveTool = null;
                StudioStreamRoundOutcome? sseAbortOutcome = null;

                bool OnStudioSsePayload(string payload, out bool isCompleteJson, out bool stopForRunawayCot)
                {
                    stopForRunawayCot = false;
                    bool appended = TryAppendSseDeltaCore(
                        payload,
                        chunk => accumulator.AppendContentDelta(chunk.AsSpan()),
                        chunk => accumulator.AppendReasoningDelta(chunk.AsSpan()),
                        accumulator,
                        out string? streamError,
                        out string? chunkFinishReason,
                        out SseChunkUsage chunkUsage,
                        out isCompleteJson);
                    if (!isCompleteJson)
                    {
                        return true;
                    }

                    sseDataEvents++;
                    if (chunkUsage.HasUsage)
                    {
                        ssePromptTokens = chunkUsage.PromptTokens;
                        sseCachedTokens = chunkUsage.CachedPromptTokens;
                        sseCompletionTokens = chunkUsage.CompletionTokens;
                    }

                    if (!string.IsNullOrWhiteSpace(chunkFinishReason))
                    {
                        lastFinishReason = chunkFinishReason;
                    }

                    if (!appended)
                    {
                        if (!string.IsNullOrWhiteSpace(streamError))
                        {
                            sseAbortOutcome = new StudioStreamRoundOutcome(
                                false,
                                string.Empty,
                                string.Empty,
                                [],
                                lastFinishReason,
                                streamError,
                                currentRetriedWithoutTools);
                            return false;
                        }
                        return true;
                    }

                    long totalAccumulatedChars = (long)accumulator.TotalVisibleChars + accumulator.TotalThinkingChars + accumulator.TotalToolCallArgumentChars;
                    if (totalAccumulatedChars > MaxSseCumulativeChars)
                    {
                        App.Log($"[StudioAI] Cumulative SSE payload exceeded {MaxSseCumulativeChars} chars (provider={activeAttemptResolved.Preset.Id}, model={activeAttemptResolved.Model})");
                        sseAbortOutcome = new StudioStreamRoundOutcome(
                            false,
                            string.Empty,
                            string.Empty,
                            [],
                            lastFinishReason,
                            Localized.T("CustomWidget.AiApi.Error.MalformedResponse"),
                            currentRetriedWithoutTools);
                        return false;
                    }

                    string? activeTool = accumulator.ActiveToolName;
                    AiGenerationStage stage = isRepairRound
                        ? AiGenerationStage.AutoRepairing
                        : (!string.IsNullOrWhiteSpace(activeTool)
                            ? activeTool switch
                            {
                                StudioToolTransport.ToolReadWorkspace => AiGenerationStage.ReadingWorkspace,
                                StudioToolTransport.ToolInspectCapabilities => AiGenerationStage.InspectingCapabilities,
                                StudioToolTransport.ToolProbeHttp => AiGenerationStage.ProbingApi,
                                StudioToolTransport.ToolWriteBundle or StudioToolTransport.ToolEditManifest or StudioToolTransport.ToolPatchScript => AiGenerationStage.PatchingWorkspace,
                                _ => AiGenerationStage.GeneratingJson
                            }
                            : (accumulator.TotalUiContentChars == 0 && (accumulator.TotalThinkingChars > 0 || accumulator.IsInsideInlineThink)
                                ? AiGenerationStage.Thinking
                                : AiGenerationStage.GeneratingJson));

                    long nowMs = sw.ElapsedMilliseconds;
                    if (stage != lastStage ||
                        !string.Equals(activeTool, lastActiveTool, StringComparison.Ordinal) ||
                        nowMs - lastEmitMs >= 66)
                    {
                        lastStage = stage;
                        lastActiveTool = activeTool;
                        lastEmitMs = nowMs;
                        EmitBoundedProgress(onProgress, stage, activeAttemptResolved, accumulator, sw.Elapsed, sseCompletionTokens);
                    }

                    if (hasMoreAttempts &&
                        currentAllowThinkingExhaustionRetry &&
                        ShouldAbortForRunawayReasoning(
                            accumulator.TotalUiContentChars,
                            accumulator.HasToolCalls,
                            accumulator.TotalThinkingChars,
                            callLatencySw.ElapsedMilliseconds,
                            AiProviderCatalog.IsReasoningOnlyModel(activeAttemptResolved.Model),
                            activeAttemptResolved.ThinkingMode))
                    {
                        lastFinishReason = "length";
                        stopForRunawayCot = true;
                    }

                    return true;
                }

                (SseStreamExitKind exitKind, bool sawAnySseData, string nonSseFallbackBody) = await ReadSsePayloadStreamAsync(
                    resp.Content,
                    callLatencySw,
                    maxFallbackChars: 262_144,
                    OnStudioSsePayload,
                    overallCts.Token);

                if (exitKind == SseStreamExitKind.Oversized)
                {
                    App.Log($"[StudioAI] Oversized SSE frame rejected (provider={activeAttemptResolved.Preset.Id}, model={activeAttemptResolved.Model})");
                    return new StudioStreamRoundOutcome(
                        false,
                        string.Empty,
                        string.Empty,
                        [],
                        lastFinishReason,
                        Localized.T("CustomWidget.AiApi.Error.MalformedResponse"),
                        currentRetriedWithoutTools);
                }

                if (sseAbortOutcome is not null)
                {
                    return sseAbortOutcome;
                }

                if (!sawAnySseData && nonSseFallbackBody.Length > 0)
                {
                    string rawBody = nonSseFallbackBody;
                    if (TryExtractNonStreamingReply(
                            rawBody,
                            out string fbContent,
                            out string fbReasoning,
                            out string? fbFinishReason,
                            out List<AiToolCallSpec> fbTools))
                    {
                        if (TryFinalizeNonStreamingStudioOutcome(
                                aiService,
                                activeAttemptResolved,
                                rawBody,
                                fbContent,
                                fbReasoning,
                                fbFinishReason,
                                fbTools,
                                isRepairRound,
                                hasMoreAttempts && currentAllowThinkingExhaustionRetry,
                                currentRetriedWithoutTools,
                                callLatencySw,
                                sw,
                                onProgress,
                                out StudioStreamRoundOutcome fbOutcome,
                                out ResolvedAiInvocation fbRetryResolved))
                        {
                            return fbOutcome;
                        }

                        AppendLengthStopRetryHint(currentUserPrompt, currentTurns, out currentUserPrompt, out currentTurns);
                        currentResolved = fbRetryResolved;
                        currentAllowParamFallback = true;
                        currentAllowThinkingExhaustionRetry = false;
                        continue;
                    }

                    if (TryExtractStructuredJsonErrorMessage(rawBody) is { Length: > 0 } nonSseErr)
                    {
                        return new StudioStreamRoundOutcome(false, string.Empty, string.Empty, [], null, nonSseErr, currentRetriedWithoutTools);
                    }
                }

                string finalVisible = accumulator.GetFullVisibleContent();
                string finalThinking = accumulator.GetFullThinking();
                IReadOnlyList<AiToolCallSpec> finalTools = accumulator.BuildToolCalls();

                if (string.IsNullOrWhiteSpace(finalVisible) && finalTools.Count == 0)
                {
                    if (hasMoreAttempts &&
                        TryBuildThinkingExhaustionRetry(
                            activeAttemptResolved,
                            finalThinking,
                            lastFinishReason,
                            accumulator.IsInsideInlineThink,
                            currentAllowThinkingExhaustionRetry,
                            out ResolvedAiInvocation retryResolved,
                            aiService.CredentialStore.Get(activeAttemptResolved.Preset.Id)?.FetchedModels))
                    {
                        AppendLengthStopRetryHint(currentUserPrompt, currentTurns, out currentUserPrompt, out currentTurns);
                        currentResolved = retryResolved;
                        currentAllowParamFallback = true;
                        currentAllowThinkingExhaustionRetry = false;
                        continue;
                    }

                    App.Log(
                        $"[StudioAI] SSE EmptyContent (provider={activeAttemptResolved.Preset.Id}, model={activeAttemptResolved.Model}, thinking={activeAttemptResolved.ThinkingMode}, finishReason={lastFinishReason ?? "none"}, reasoningChars={finalThinking.Length}, sseEvents={sseDataEvents})");
                    return new StudioStreamRoundOutcome(
                        false,
                        string.Empty,
                        finalThinking,
                        [],
                        lastFinishReason,
                        Localized.T("CustomWidget.AiApi.Error.EmptyContent"),
                        currentRetriedWithoutTools);
                }

                aiService.RecordCallTelemetry(
                    ssePromptTokens,
                    sseCachedTokens,
                    sseCompletionTokens,
                    Math.Max(1, callLatencySw.ElapsedMilliseconds),
                    isAutoRepair: isRepairRound);

                EmitBoundedProgress(
                    onProgress,
                    isRepairRound ? AiGenerationStage.AutoRepairing : AiGenerationStage.GeneratingJson,
                    activeAttemptResolved,
                    accumulator,
                    sw.Elapsed,
                    sseCompletionTokens);

                return new StudioStreamRoundOutcome(
                    true,
                    finalVisible,
                    finalThinking,
                    finalTools,
                    lastFinishReason,
                    null,
                    currentRetriedWithoutTools,
                    ssePromptTokens,
                    sseCachedTokens,
                    sseCompletionTokens);
            }

            return new StudioStreamRoundOutcome(
                false,
                string.Empty,
                string.Empty,
                [],
                null,
                Localized.T("CustomWidget.AiApi.Error.InvocationFailed"),
                currentRetriedWithoutTools);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new StudioStreamRoundOutcome(
                false,
                string.Empty,
                string.Empty,
                [],
                null,
                Localized.T("CustomWidget.AiApi.Error.GenerationCancelled"),
                currentRetriedWithoutTools);
        }
        catch (Exception ex) when (ex is OperationCanceledException or TimeoutException)
        {
            App.Log($"[StudioAI] Studio SSE stream timed out (provider={activeAttemptResolved.Preset.Id}, model={activeAttemptResolved.Model}, elapsedMs={sw.ElapsedMilliseconds}, err={ex.Message})");
            return new StudioStreamRoundOutcome(
                false,
                string.Empty,
                string.Empty,
                [],
                null,
                Localized.T("CustomWidget.AiApi.Error.StreamTimeout"),
                currentRetriedWithoutTools);
        }
        catch (Exception ex)
        {
            return new StudioStreamRoundOutcome(false, string.Empty, string.Empty, [], null, ex.Message, currentRetriedWithoutTools);
        }
    }

    private static bool TryFinalizeNonStreamingStudioOutcome(
        DeskBoxAiService aiService,
        ResolvedAiInvocation resolved,
        string rawBody,
        string fullContent,
        string fullReasoning,
        string? finishReason,
        IReadOnlyList<AiToolCallSpec> toolCalls,
        bool isRepairRound,
        bool allowThinkingExhaustionRetry,
        bool retriedWithoutTools,
        Stopwatch callLatencySw,
        Stopwatch overallSw,
        Action<AiGenerationStreamProgress>? onProgress,
        out StudioStreamRoundOutcome outcome,
        out ResolvedAiInvocation retryResolved)
    {
        retryResolved = resolved;
        SplitInlineThinkBlock(fullContent, out string inlineThink, out string visibleContent, out bool unclosedThink);
        string mergedThink = MergeThinking(fullReasoning, inlineThink);
        if (string.IsNullOrWhiteSpace(visibleContent) && toolCalls.Count == 0)
        {
            if (TryBuildThinkingExhaustionRetry(
                    resolved,
                    mergedThink,
                    finishReason,
                    unclosedThink,
                    allowThinkingExhaustionRetry,
                    out retryResolved,
                    aiService.CredentialStore.Get(resolved.Preset.Id)?.FetchedModels))
            {
                outcome = default!;
                return false;
            }

            outcome = new StudioStreamRoundOutcome(
                false,
                string.Empty,
                mergedThink,
                [],
                finishReason,
                Localized.T("CustomWidget.AiApi.Error.EmptyContent"),
                retriedWithoutTools);
            return true;
        }

        TryExtractUsageFromJson(rawBody, out int pt, out int cachedPt, out int comp);
        aiService.RecordCallTelemetry(pt, cachedPt, comp, Math.Max(1, callLatencySw.ElapsedMilliseconds), isAutoRepair: isRepairRound);

        var tempAcc = new StreamingTurnAccumulator();
        tempAcc.AppendReasoningDelta(mergedThink.AsSpan());
        tempAcc.AppendContentDelta(visibleContent.AsSpan());
        for (int t = 0; t < toolCalls.Count; t++)
        {
            tempAcc.AppendToolCallDelta(t, toolCalls[t].Id, toolCalls[t].Name, toolCalls[t].ArgumentsJson);
        }

        EmitBoundedProgress(
            onProgress,
            isRepairRound ? AiGenerationStage.AutoRepairing : AiGenerationStage.GeneratingJson,
            resolved,
            tempAcc,
            overallSw.Elapsed,
            comp);

        outcome = new StudioStreamRoundOutcome(
            true,
            visibleContent,
            mergedThink,
            toolCalls,
            finishReason,
            null,
            retriedWithoutTools,
            pt,
            cachedPt,
            comp);
        return true;
    }

    internal static void EmitBoundedProgress(
        Action<AiGenerationStreamProgress>? onProgress,
        AiGenerationStage stage,
        ResolvedAiInvocation resolved,
        StreamingTurnAccumulator accumulator,
        TimeSpan elapsed,
        int reportedCompletionTokens = 0)
    {
        if (onProgress is null)
        {
            return;
        }

        int thinkTokens = accumulator.EstimatedThinkingTokens;
        int contentTokens = accumulator.EstimatedUiContentTokens;
        int totalOutTokens = reportedCompletionTokens > 0
            ? reportedCompletionTokens
            : thinkTokens + contentTokens;

        onProgress(new AiGenerationStreamProgress(
            stage,
            resolved.Preset.DisplayName,
            resolved.Model,
            accumulator.GetThinkingTail(),
            accumulator.GetVisibleTail(),
            accumulator.TotalThinkingChars,
            accumulator.TotalUiContentChars,
            accumulator.LatchedWidgetName,
            elapsed,
            ActiveToolName: accumulator.ActiveToolName,
            StreamingHint: accumulator.GetStreamingHint(),
            ThinkingTokens: thinkTokens,
            ContentTokens: contentTokens,
            TotalOutputTokens: totalOutTokens));
    }

    internal static async Task<(bool Success, int StatusCode, TimeSpan? RetryAfter, string Content, string? Error)>
        SendMicroTaskStreamingCompletionAsync(
            DeskBoxAiService aiService,
            ResolvedAiInvocation resolved,
            string? systemPrompt,
            string userPrompt,
            bool includeThinkingParams,
            int? maxOutputTokens = null,
            Action<string>? onVisibleSnapshot = null,
            CancellationToken cancellationToken = default,
            bool omitTemperature = false,
            bool includeStreamUsageOptions = true)
    {
        if (string.IsNullOrWhiteSpace(resolved.Endpoint))
        {
            return (false, 400, null, string.Empty, "Invalid HTTP/HTTPS endpoint URL.");
        }

        bool effectiveIncludeThinking = includeThinkingParams && !aiService.IsThinkingParamsUnsupportedRoute(resolved);
        bool effectiveIncludeUsage = includeStreamUsageOptions && !AiContextBudgetResolver.IsLocalModelRoute(resolved);

        try
        {
            byte[] payload = BuildChatRequestPayload(
                resolved,
                systemPrompt: systemPrompt,
                userPrompt: userPrompt,
                stream: true,
                includeThinkingParams: effectiveIncludeThinking,
                isProbe: false,
                capMicroTaskTokens: true,
                microTaskMaxTokensOverride: maxOutputTokens,
                includeStreamUsageOptions: effectiveIncludeUsage,
                omitTemperature: omitTemperature);

            using var req = new HttpRequestMessage(HttpMethod.Post, resolved.Endpoint)
            {
                Content = CreateJsonContent(payload)
            };
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            ApplyAuthHeaders(req, resolved.Endpoint, resolved.ApiKey);

            using var overallCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            overallCts.CancelAfter(StreamingOverallTimeout);

            using HttpResponseMessage resp = await aiService.HttpClient.SendAsync(
                req,
                HttpCompletionOption.ResponseHeadersRead,
                overallCts.Token);

            TimeSpan? retryAfter = resp.Headers.RetryAfter?.Delta;
            if (retryAfter is null && resp.Headers.RetryAfter?.Date is { } retryDate)
            {
                TimeSpan diff = retryDate - DateTimeOffset.UtcNow;
                if (diff > TimeSpan.Zero)
                {
                    retryAfter = diff;
                }
            }

            if (!resp.IsSuccessStatusCode)
            {
                string errBody = await resp.Content.ReadAsStringAsync(overallCts.Token);
                int statusCode = (int)resp.StatusCode;
                if (effectiveIncludeThinking &&
                    resolved.ThinkingMode != AiThinkingMode.Auto &&
                    statusCode is 400 or 422 &&
                    ErrorMentionsUnsupportedThinkingParams(errBody))
                {
                    aiService.MarkThinkingParamsUnsupportedRoute(resolved);
                }

                return (false, statusCode, retryAfter, string.Empty, FormatHttpError(resp.StatusCode, errBody));
            }

            var attemptLatencySw = Stopwatch.StartNew();
            string? mediaType = resp.Content.Headers.ContentType?.MediaType;
            if (string.Equals(mediaType, "application/json", StringComparison.OrdinalIgnoreCase))
            {
                string jsonBody = await resp.Content.ReadAsStringAsync(overallCts.Token);
                if (TryExtractNonStreamingReply(jsonBody, out string fullContent, out _))
                {
                    SplitInlineThinkBlock(fullContent, out _, out string cleanJsonBody, out _);
                    if (string.IsNullOrWhiteSpace(cleanJsonBody))
                    {
                        return (false, (int)resp.StatusCode, null, string.Empty, Localized.T("CustomWidget.AiApi.Error.EmptyContent"));
                    }

                    TryExtractUsageFromJson(jsonBody, out int pt, out int cachedPt, out int comp);
                    aiService.RecordCallTelemetry(pt, cachedPt, comp, Math.Max(1, attemptLatencySw.ElapsedMilliseconds));
                    onVisibleSnapshot?.Invoke(cleanJsonBody);
                    return (true, (int)resp.StatusCode, null, cleanJsonBody, null);
                }

                return (false, (int)resp.StatusCode, null, string.Empty,
                    TryExtractStructuredJsonErrorMessage(jsonBody)
                    ?? Localized.T("CustomWidget.AiApi.Error.MalformedResponse"));
            }

            var rawContentBuilder = new StringBuilder(1024);
            var reasoningBuilder = new StringBuilder(256);
            int ssePromptTokens = 0;
            int sseCachedTokens = 0;
            int sseCompletionTokens = 0;
            long lastEmitMs = -1000;
            (bool Success, int StatusCode, TimeSpan? RetryAfter, string Content, string? Error)? sseErrorResult = null;

            bool OnMicroSsePayload(string payload, out bool isCompleteJson, out bool stopEarly)
            {
                stopEarly = false;
                bool appended = TryAppendSseDeltaCore(
                    payload,
                    chunk => rawContentBuilder.Append(chunk),
                    chunk => reasoningBuilder.Append(chunk),
                    toolAccumulator: null,
                    out string? streamError,
                    out _,
                    out SseChunkUsage chunkUsage,
                    out isCompleteJson);
                if (!isCompleteJson)
                {
                    return true;
                }

                if (chunkUsage.HasUsage)
                {
                    ssePromptTokens = chunkUsage.PromptTokens;
                    sseCachedTokens = chunkUsage.CachedPromptTokens;
                    sseCompletionTokens = chunkUsage.CompletionTokens;
                }

                if (!appended)
                {
                    if (!string.IsNullOrWhiteSpace(streamError))
                    {
                        sseErrorResult = (false, 200, null, string.Empty, streamError);
                        return false;
                    }
                    return true;
                }

                if ((long)rawContentBuilder.Length + reasoningBuilder.Length > MaxSseCumulativeChars)
                {
                    sseErrorResult = (false, 502, null, string.Empty, Localized.T("CustomWidget.AiApi.Error.MalformedResponse"));
                    return false;
                }

                long nowMs = attemptLatencySw.ElapsedMilliseconds;
                if (onVisibleSnapshot is not null && nowMs - lastEmitMs >= 75)
                {
                    lastEmitMs = nowMs;
                    SplitInlineThinkBlock(rawContentBuilder.ToString(), out _, out string visible, out _);
                    onVisibleSnapshot(visible);
                }

                return true;
            }

            (SseStreamExitKind exitKind, bool sawAnySseData, string nonSseFallbackBody) = await ReadSsePayloadStreamAsync(
                resp.Content,
                attemptLatencySw,
                maxFallbackChars: 131_072,
                OnMicroSsePayload,
                overallCts.Token);

            if (exitKind == SseStreamExitKind.Oversized)
            {
                return (false, 502, null, string.Empty, Localized.T("CustomWidget.AiApi.Error.MalformedResponse"));
            }

            if (sseErrorResult.HasValue)
            {
                return sseErrorResult.Value;
            }

            if (!sawAnySseData && nonSseFallbackBody.Length > 0)
            {
                string rawBody = nonSseFallbackBody;
                if (TryExtractNonStreamingReply(rawBody, out string fbContent, out _))
                {
                    SplitInlineThinkBlock(fbContent, out _, out string cleanFbContent, out _);
                    if (string.IsNullOrWhiteSpace(cleanFbContent))
                    {
                        return (false, 200, null, string.Empty, Localized.T("CustomWidget.AiApi.Error.EmptyContent"));
                    }

                    TryExtractUsageFromJson(rawBody, out int fbPt, out int fbCached, out int fbComp);
                    aiService.RecordCallTelemetry(fbPt, fbCached, fbComp, Math.Max(1, attemptLatencySw.ElapsedMilliseconds));
                    onVisibleSnapshot?.Invoke(cleanFbContent);
                    return (true, 200, null, cleanFbContent, null);
                }

                if (TryExtractStructuredJsonErrorMessage(rawBody) is { Length: > 0 } nonSseErr)
                {
                    return (false, 200, null, string.Empty, nonSseErr);
                }
            }

            string finalContent = rawContentBuilder.ToString();
            SplitInlineThinkBlock(finalContent, out _, out string visibleFinalContent, out _);
            if (string.IsNullOrWhiteSpace(visibleFinalContent))
            {
                return (false, 200, null, string.Empty, Localized.T("CustomWidget.AiApi.Error.EmptyContent"));
            }

            aiService.RecordCallTelemetry(ssePromptTokens, sseCachedTokens, sseCompletionTokens, Math.Max(1, attemptLatencySw.ElapsedMilliseconds));
            return (true, 200, null, visibleFinalContent, null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is OperationCanceledException or TimeoutException)
        {
            return (false, 503, null, string.Empty, Localized.T("CustomWidget.AiApi.Error.StreamTimeout"));
        }
        catch (HttpRequestException ex)
        {
            int code = ex.StatusCode.HasValue ? (int)ex.StatusCode.Value : 503;
            return (false, code, null, string.Empty, ex.Message);
        }
        catch (Exception ex)
        {
            return (false, 0, null, string.Empty, ex.Message);
        }
    }

    private enum SseStreamExitKind
    {
        Completed,
        StoppedEarly,
        Oversized,
        CallbackAborted
    }

    private delegate bool SsePayloadDispatchHandler(
        string jsonPayload,
        out bool isCompleteJson,
        out bool stopEarly);

    private static async Task<(SseStreamExitKind ExitKind, bool SawAnySseData, string NonSseFallbackText)> ReadSsePayloadStreamAsync(
        HttpContent content,
        Stopwatch latencySw,
        int maxFallbackChars,
        SsePayloadDispatchHandler onPayload,
        CancellationToken overallToken)
    {
        await using Stream bodyStream = await content.ReadAsStreamAsync(overallToken);
        using var reader = new DeskBox.Services.Ipc.PipeBoundedLineReader(bodyStream, detectEncodingFromByteOrderMarks: true);

        StringBuilder? nonSseFallbackBuilder = null;
        StringBuilder? pendingSseData = null;
        bool sawAnySseData = false;
        long lastMeaningfulFrameMs = latencySw.ElapsedMilliseconds;

        bool FlushPendingEvent(out bool stopEarly)
        {
            stopEarly = false;
            if (pendingSseData is not { Length: > 0 })
            {
                return true;
            }

            string combined = pendingSseData.ToString();
            pendingSseData.Clear();
            return onPayload(combined, out _, out stopEarly);
        }

        while (true)
        {
            TimeSpan currentIdleWindow = sawAnySseData ? StreamingInterChunkIdleTimeout : StreamingInitialIdleTimeout;
            DeskBox.Services.Ipc.BoundedLineReadResult lineResult = await reader.ReadBoundedLineAsync(
                MaxSseLineChars,
                overallToken,
                currentIdleWindow);
            if (lineResult.Kind == DeskBox.Services.Ipc.BoundedLineReadKind.EndOfStream)
            {
                if (!FlushPendingEvent(out bool stopAtEof))
                {
                    return (SseStreamExitKind.CallbackAborted, sawAnySseData, string.Empty);
                }

                return (
                    stopAtEof ? SseStreamExitKind.StoppedEarly : SseStreamExitKind.Completed,
                    sawAnySseData,
                    !sawAnySseData && nonSseFallbackBuilder is { Length: > 0 } ? nonSseFallbackBuilder.ToString() : string.Empty);
            }

            if (lineResult.Kind == DeskBox.Services.Ipc.BoundedLineReadKind.Oversized)
            {
                return (SseStreamExitKind.Oversized, sawAnySseData, string.Empty);
            }

            string line = lineResult.Line!;
            if (line.Length == 0)
            {
                if (pendingSseData is { Length: > 0 })
                {
                    lastMeaningfulFrameMs = latencySw.ElapsedMilliseconds;
                    if (!FlushPendingEvent(out bool stopAfterFlush))
                    {
                        return (SseStreamExitKind.CallbackAborted, sawAnySseData, string.Empty);
                    }

                    if (stopAfterFlush)
                    {
                        return (SseStreamExitKind.StoppedEarly, sawAnySseData, string.Empty);
                    }

                    continue;
                }

                if (latencySw.ElapsedMilliseconds - lastMeaningfulFrameMs > (long)currentIdleWindow.TotalMilliseconds)
                {
                    throw new TimeoutException("SSE stream received only comment/empty keep-alive lines past idle guard.");
                }

                continue;
            }

            if (line.StartsWith(':'))
            {
                if (latencySw.ElapsedMilliseconds - lastMeaningfulFrameMs > (long)currentIdleWindow.TotalMilliseconds)
                {
                    throw new TimeoutException("SSE stream received only comment/empty keep-alive lines past idle guard.");
                }

                continue;
            }

            lastMeaningfulFrameMs = latencySw.ElapsedMilliseconds;

            string trimmedStart = line.TrimStart();
            if (!trimmedStart.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                if (!sawAnySseData)
                {
                    nonSseFallbackBuilder ??= new StringBuilder();
                    if (nonSseFallbackBuilder.Length < maxFallbackChars)
                    {
                        nonSseFallbackBuilder.AppendLine(line);
                    }
                }

                continue;
            }

            sawAnySseData = true;
            ReadOnlySpan<char> dataSlice = ExtractSseDataFieldSlice(line);
            ReadOnlySpan<char> dataTrimmed = dataSlice.Trim();
            if (dataTrimmed.Equals("[DONE]", StringComparison.Ordinal))
            {
                if (!FlushPendingEvent(out bool stopAtDone))
                {
                    return (SseStreamExitKind.CallbackAborted, true, string.Empty);
                }

                return (stopAtDone ? SseStreamExitKind.StoppedEarly : SseStreamExitKind.Completed, true, string.Empty);
            }

            if (pendingSseData is { Length: > 0 })
            {
                if (pendingSseData.Length + 1 + dataSlice.Length > MaxSseLineChars)
                {
                    return (SseStreamExitKind.Oversized, true, string.Empty);
                }

                pendingSseData.Append('\n').Append(dataSlice);
                continue;
            }

            if (dataTrimmed.IsEmpty)
            {
                continue;
            }

            string dataPayload = dataTrimmed.ToString();
            if (!onPayload(dataPayload, out bool isCompleteJson, out bool stopSingle))
            {
                return (SseStreamExitKind.CallbackAborted, true, string.Empty);
            }

            if (!isCompleteJson)
            {
                (pendingSseData ??= new StringBuilder(dataSlice.Length * 2)).Append(dataSlice);
                continue;
            }

            if (stopSingle)
            {
                return (SseStreamExitKind.StoppedEarly, true, string.Empty);
            }
        }
    }

    internal static bool TryBuildThinkingExhaustionRetry(
        ResolvedAiInvocation current,
        string mergedThinking,
        string? finishReason,
        bool unclosedInlineThink,
        bool allowRetry,
        out ResolvedAiInvocation retryResolved,
        IReadOnlyList<string>? fetchedModels = null)
    {
        retryResolved = current;
        if (!allowRetry)
        {
            return false;
        }

        bool exhaustedByReasoning =
            mergedThinking.Length > 0 ||
            unclosedInlineThink ||
            string.Equals(finishReason, "length", StringComparison.OrdinalIgnoreCase);
        if (!exhaustedByReasoning)
        {
            return false;
        }

        if (string.Equals(current.Model, "deepseek-reasoner", StringComparison.OrdinalIgnoreCase) &&
            AiProviderCatalog.SupportsClassicDeepSeekModelSwap(current.Preset, fetchedModels))
        {
            retryResolved = current with
            {
                Model = "deepseek-chat",
                ThinkingMode = AiThinkingMode.Disabled
            };
            return true;
        }

        if (!AiProviderCatalog.IsReasoningOnlyModel(current.Model))
        {
            if (current.ThinkingMode != AiThinkingMode.Disabled)
            {
                retryResolved = current with
                {
                    ThinkingMode = AiThinkingMode.Disabled
                };
                return true;
            }

            // Upstream relays may ignore thinking=disabled and still exhaust output tokens in reasoning_content;
            // allow one retry with the concise LengthStopHint (allowRetry is immediately cleared by the caller).
            if (mergedThinking.Length > 0 || unclosedInlineThink)
            {
                retryResolved = current;
                return true;
            }
        }

        return false;
    }

    private static void AppendLengthStopRetryHint(
        string userPrompt,
        IReadOnlyList<AiChatTurnMessage> structuredTurns,
        out string retryUserPrompt,
        out IReadOnlyList<AiChatTurnMessage> retryTurns)
    {
        const string LengthStopHint =
            "<system-injection>\n" +
            "Previous attempt hit the output token limit during reasoning without emitting tool calls. " +
            "Act immediately without extended deliberation: call your tools directly now (for large widgets, call write_bundle with manifest first, then patch_script for logic.js).\n" +
            "</system-injection>";

        if (structuredTurns is { Count: > 0 })
        {
            retryUserPrompt = userPrompt;
            var extended = new List<AiChatTurnMessage>(structuredTurns.Count + 1);
            extended.AddRange(structuredTurns);
            extended.Add(new AiChatTurnMessage("user", LengthStopHint));
            retryTurns = extended;
        }
        else
        {
            retryUserPrompt = string.Concat(userPrompt, "\n\n", LengthStopHint);
            retryTurns = structuredTurns;
        }
    }
}
