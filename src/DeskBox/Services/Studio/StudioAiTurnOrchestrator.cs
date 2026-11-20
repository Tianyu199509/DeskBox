using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DeskBox.Models;

namespace DeskBox.Services;

internal sealed partial class StudioAiTurnOrchestrator
{
    private readonly DeskBoxAiService _aiService;
    private readonly CustomWidgetNetworkSourceEngine? _networkSourceEngine;
    private readonly CustomWidgetDiagnosticArchive? _diagnosticArchive;
    private readonly StudioPreFlightProbeService _preFlightProbe;

    internal StudioAiTurnOrchestrator(
        DeskBoxAiService aiService,
        CustomWidgetNetworkSourceEngine? networkSourceEngine,
        CustomWidgetDiagnosticArchive? diagnosticArchive,
        StudioPreFlightProbeService preFlightProbe)
    {
        _aiService = aiService;
        _networkSourceEngine = networkSourceEngine;
        _diagnosticArchive = diagnosticArchive;
        _preFlightProbe = preFlightProbe;
    }

    internal StudioTurnResult FinalizeStudioTurnResult(
        StudioTurnResult result,
        ResolvedAiInvocation resolved,
        long elapsedMs,
        string userMessage,
        int llmCallsUsed,
        int toolRoundsUsed,
        int repairRoundsUsed)
    {
        IReadOnlyList<CustomWidgetValidationDiagnostic> rawDiags = result.ParseResult?.Diagnostics ?? [];
        string? manifestId = result.ParseResult?.Manifest?.Id;
        bool shouldArchive = !result.Success || string.Equals(result.Kind, "edit", StringComparison.OrdinalIgnoreCase);
        RecordAndLogTurnExit(
            mode: "studio",
            kind: result.Kind,
            recordToArchive: shouldArchive,
            success: result.Success,
            wasAutoRepaired: result.WasAutoRepaired,
            manifestId: manifestId,
            resolved: resolved,
            llmCallsUsed: llmCallsUsed,
            toolRoundsUsed: toolRoundsUsed,
            repairRoundsUsed: repairRoundsUsed,
            elapsedMs: elapsedMs,
            userPrompt: userMessage,
            summaryOrError: result.Success ? result.ChangeSummary : (result.ErrorMessage ?? result.Reply),
            rawDiags: rawDiags,
            manifestJson: result.ManifestJson,
            logicJs: result.LogicJs,
            errorForLog: result.ErrorMessage);
        return result;
    }

    internal void RecordAndLogTurnExit(
        string mode,
        string kind,
        bool recordToArchive,
        bool success,
        bool wasAutoRepaired,
        string? manifestId,
        ResolvedAiInvocation resolved,
        int llmCallsUsed,
        int toolRoundsUsed,
        int repairRoundsUsed,
        long elapsedMs,
        string? userPrompt,
        string? summaryOrError,
        IReadOnlyList<CustomWidgetValidationDiagnostic> rawDiags,
        string? manifestJson,
        string? logicJs,
        string? errorForLog)
    {
        var diagMessages = new List<string>(Math.Min(rawDiags.Count, 8));
        foreach (CustomWidgetValidationDiagnostic d in rawDiags)
        {
            if (diagMessages.Count >= 8)
            {
                break;
            }

            diagMessages.Add($"{(d.IsError ? "ERR" : "WARN")} {d.Code} {d.Path}: {d.Message}");
        }

        string safeErr = CustomWidgetDiagnosticArchive.ScrubSensitiveText(errorForLog, 160);
        App.Log(
            $"[StudioAI] TurnDone mode={mode} kind={kind} ok={success} provider={resolved.Preset.Id} model={resolved.Model} thinking={resolved.ThinkingMode} llm={llmCallsUsed} tools={toolRoundsUsed} repairs={repairRoundsUsed} autoRepaired={wasAutoRepaired} widgetId={manifestId ?? "(none)"} elapsedMs={elapsedMs} diags={diagMessages.Count} err={safeErr}");

        if (!recordToArchive || _diagnosticArchive is null)
        {
            return;
        }

        string eventKind = string.Equals(mode, "studio", StringComparison.Ordinal)
            ? $"studio-{kind}"
            : mode;
        _diagnosticArchive.Record(new StudioDiagnosticArchiveEntry(
            EntryId: string.Empty,
            CreatedAtUtc: string.Empty,
            EventKind: eventKind,
            WidgetId: null,
            ManifestId: manifestId,
            ProviderId: resolved.Preset.Id,
            Model: resolved.Model,
            ThinkingMode: resolved.ThinkingMode.ToString(),
            Success: success,
            WasAutoRepaired: wasAutoRepaired,
            LlmCalls: llmCallsUsed,
            ToolRounds: toolRoundsUsed,
            RepairRounds: repairRoundsUsed,
            ElapsedMs: elapsedMs,
            UserPrompt: userPrompt ?? string.Empty,
            SummaryOrError: summaryOrError ?? string.Empty,
            Diagnostics: diagMessages,
            ManifestJson: manifestJson ?? string.Empty,
            LogicJs: logicJs));
    }


    public ResolvedAiInvocation ResolveEffectiveSettings(
        string providerId,
        string? endpointOverride = null,
        string? modelOverride = null,
        string? apiKeyOverride = null,
        AiThinkingMode? thinkingOverride = null) =>
        _aiService.ResolveEffectiveSettings(
            providerId,
            endpointOverride,
            modelOverride,
            apiKeyOverride,
            thinkingOverride);

    public ResolvedAiInvocation ResolveEffectiveStudioInvocation(
        string providerId,
        string? endpointOverride = null,
        string? modelOverride = null,
        string? apiKeyOverride = null,
        AiThinkingMode? thinkingOverride = null) =>
        _aiService.CoerceSafeStudioAuthoringInvocation(ResolveEffectiveSettings(
            providerId,
            endpointOverride,
            modelOverride,
            apiKeyOverride,
            thinkingOverride));

    internal static string? ResolveTurnErrorMessage(
        string? transportError,
        CancellationToken userCancellationToken) =>
        userCancellationToken.IsCancellationRequested
            ? Localized.T("CustomWidget.AiApi.Error.GenerationCancelled")
            : transportError;

    internal static string BuildProbeHttpCacheKey(StudioAgenticActionRequest req)
    {
        string method = (req.Method ?? "GET").Trim().ToUpperInvariant();
        string url = (req.Url ?? string.Empty).Trim();
        string headers = req.Headers is { Count: > 0 }
            ? string.Join("&", req.Headers
                .OrderBy(static kv => kv.Key, StringComparer.OrdinalIgnoreCase)
                .Select(static kv => $"{kv.Key.Trim().ToLowerInvariant()}={kv.Value}"))
            : string.Empty;
        return $"{method}|{url}|{headers}|{req.Body ?? string.Empty}";
    }

    internal static int EstimateStructuredTurnsChars(IReadOnlyList<AiChatTurnMessage> turns)
    {
        int total = 0;
        for (int i = 0; i < turns.Count; i++)
        {
            AiChatTurnMessage m = turns[i];
            total += 48 + (m.Content?.Length ?? 0) + (m.ReasoningContent?.Length ?? 0);
            if (m.ToolCalls is { Count: > 0 })
            {
                for (int t = 0; t < m.ToolCalls.Count; t++)
                {
                    total += 32 + m.ToolCalls[t].Name.Length + m.ToolCalls[t].ArgumentsJson.Length;
                }
            }
        }

        return total;
    }

    internal static (int Width, int Height) ResolveHostDefaultWidgetSize(int? widthOverride, int? heightOverride)
    {
        double rawW = widthOverride ?? SettingsService.DefaultWidgetWidth;
        double rawH = heightOverride ?? SettingsService.DefaultWidgetHeight;
        int w = Math.Clamp((int)Math.Round(double.IsFinite(rawW) && rawW > 0 ? rawW : SettingsService.DefaultWidgetWidth), 180, 1200);
        int h = Math.Clamp((int)Math.Round(double.IsFinite(rawH) && rawH > 0 ? rawH : SettingsService.DefaultWidgetHeight), 80, 1200);
        return (w, h);
    }

    public async Task<StudioTurnResult> RunStudioTurnAsync(
        string providerId,
        string userMessage,
        string? currentManifestJson = null,
        string? briefMarkdown = null,
        IReadOnlyList<StudioChatMessage>? recentMessages = null,
        CustomWidgetHealthReport? healthReport = null,
        string? targetNodePath = null,
        string? preferredEmoji = null,
        bool allowSampleData = false,
        CancellationToken cancellationToken = default,
        Action<AiGenerationStreamProgress>? onProgress = null,
        string? uiLanguage = null,
        CustomWidgetEffectivePromptPack? promptPackOverride = null,
        IReadOnlyList<string>? pinnedCapabilityIds = null,
        string? currentLogicJs = null,
        CustomWidgetLocaleCatalog? currentLocales = null,
        bool isCapsuleViewport = false,
        int? defaultWidgetWidth = null,
        int? defaultWidgetHeight = null,
        string? existingSampleDataJson = null,
        AiThinkingMode? thinkingOverride = null,
        string? runtimeTraceSummary = null,
        IReadOnlyList<string>? sessionCapabilityPackIds = null)
    {
        ResolvedAiInvocation resolved = ResolveEffectiveStudioInvocation(providerId, thinkingOverride: thinkingOverride);
        if (thinkingOverride is null)
        {
            AiThinkingMode stageThinking = StudioTurnStageRouter.ResolveAdaptiveStudioThinkingMode(
                resolved,
                userMessage,
                hasExistingManifest: !string.IsNullOrWhiteSpace(currentManifestJson),
                targetNodePath: targetNodePath,
                recentMessages: recentMessages,
                isPostMutationRound: false,
                thinkingParamsUnsupportedOnRoute: _aiService.IsThinkingParamsUnsupportedRoute(resolved),
                thinkingExhaustedOnRoute: _aiService.IsThinkingExhaustedRoute(resolved));
            if (stageThinking != resolved.ThinkingMode)
            {
                resolved = ResolveEffectiveStudioInvocation(providerId, thinkingOverride: stageThinking);
            }
        }
        if (resolved.Preset.RequiresApiKey && string.IsNullOrWhiteSpace(resolved.ApiKey))
        {
            return FinalizeStudioTurnResult(
                new StudioTurnResult(
                    Success: false,
                    Kind: "edit",
                    Reply: string.Empty,
                    ChangeSummary: string.Empty,
                    BriefUpdates: null,
                    Suggestions: [],
                    SampleDataJson: null,
                    ManifestJson: string.Empty,
                    ParseResult: null,
                    ErrorMessage: Localized.T("CustomWidget.AiApi.Error.MissingConfiguredApiKey"),
                    ProviderId: providerId,
                    EffectiveModel: resolved.Model),
                resolved,
                elapsedMs: 0,
                userMessage,
                llmCallsUsed: 0,
                toolRoundsUsed: 0,
                repairRoundsUsed: 0);
        }

        App.Log(
            $"[StudioAI] TurnStart mode=studio provider={resolved.Preset.Id} model={resolved.Model} thinking={resolved.ThinkingMode} edit={!string.IsNullOrWhiteSpace(currentManifestJson)} promptChars={(userMessage ??= string.Empty).Length} targetNode={targetNodePath ?? "(none)"} capsule={isCapsuleViewport}");

        string effectiveLanguage = string.IsNullOrWhiteSpace(uiLanguage)
            ? Localized.ActiveCultureName
            : uiLanguage;
        bool isChinese = CustomWidgetRemotePromptService.IsChineseCulture(effectiveLanguage);

        string TranslateStep(string key, params object[] args)
        {
            string template = LocalizationService.TranslateForCulture(effectiveLanguage, key);
            return args.Length == 0
                ? template
                : string.Format(CultureInfo.InvariantCulture, template, args);
        }

        var sw = Stopwatch.StartNew();
        var stepTrace = new List<StudioStreamStepItem>(8)
        {
            new("connect", StudioStreamStepState.Running, TranslateStep("CustomWidget.Workshop.Stream.StepConnect"))
        };
        int completedReasoningChars = 0;
        int completedContentChars = 0;
        int completedThinkingTokens = 0;
        int completedContentTokens = 0;
        int completedOutputTokens = 0;
        string? lastWarnStepId = null;
        string? lastWarnToolName = null;
        string? lastWarnBaseLabel = null;
        int consecutiveWarnCount = 0;

        void ResetConsecutiveToolWarnings()
        {
            consecutiveWarnCount = 0;
            lastWarnToolName = null;
            lastWarnStepId = null;
            lastWarnBaseLabel = null;
        }

        void UpsertStep(string id, StudioStreamStepState state, string label)
        {
            if ((state == StudioStreamStepState.Completed && !string.Equals(id, "connect", StringComparison.Ordinal)) ||
                id.StartsWith("repair-", StringComparison.Ordinal) ||
                id.StartsWith("noop-", StringComparison.Ordinal))
            {
                ResetConsecutiveToolWarnings();
            }

            for (int s = 0; s < stepTrace.Count; s++)
            {
                if (string.Equals(stepTrace[s].Id, id, StringComparison.Ordinal))
                {
                    stepTrace[s] = new StudioStreamStepItem(id, state, label);
                    return;
                }
            }

            stepTrace.Add(new StudioStreamStepItem(id, state, label));
        }

        void EmitTrace(
            AiGenerationStage stage,
            string thinkingText,
            string contentText,
            int roundReasoningChars,
            int roundContentChars,
            string? detectedWidgetName,
            string? activeToolName = null,
            string? streamingHint = null,
            string? progressiveManifestJson = null,
            int roundThinkingTokens = 0,
            int roundContentTokens = 0,
            int roundOutputTokens = 0)
        {
            int effectiveRoundOut = roundOutputTokens > 0
                ? roundOutputTokens
                : roundThinkingTokens + roundContentTokens;
            onProgress?.Invoke(new AiGenerationStreamProgress(
                stage,
                resolved.Preset.DisplayName,
                resolved.Model,
                thinkingText,
                contentText,
                completedReasoningChars + roundReasoningChars,
                completedContentChars + roundContentChars,
                detectedWidgetName,
                sw.Elapsed,
                Steps: stepTrace.ToArray(),
                ActiveToolName: activeToolName,
                StreamingHint: streamingHint,
                ProgressiveManifestJson: progressiveManifestJson,
                ThinkingTokens: completedThinkingTokens + roundThinkingTokens,
                ContentTokens: completedContentTokens + roundContentTokens,
                TotalOutputTokens: completedOutputTokens + effectiveRoundOut));
        }

        EmitTrace(
            AiGenerationStage.Connecting,
            string.Empty,
            string.Empty,
            roundReasoningChars: 0,
            roundContentChars: 0,
            detectedWidgetName: null);

        CustomWidgetEffectivePromptPack promptPack = promptPackOverride
            ?? CustomWidgetRemotePromptService.Shared.GetEffectivePromptPack();

        (int resolvedDefaultWidth, int resolvedDefaultHeight) = ResolveHostDefaultWidgetSize(
            defaultWidgetWidth,
            defaultWidgetHeight);

        bool supportsOverrideEnvelope = !string.IsNullOrWhiteSpace(promptPack.SystemPromptOverride) &&
                                        promptPack.SystemPromptOverride.Contains("\"kind\"", StringComparison.Ordinal) &&
                                        !promptPack.SystemPromptOverride.Contains("write_bundle", StringComparison.Ordinal) &&
                                        !promptPack.SystemPromptOverride.Contains("finish_turn", StringComparison.Ordinal);
        bool useNativeTools = !_aiService.IsToolsUnsupportedRoute(resolved) && !supportsOverrideEnvelope;

        string? cachedNativeSystemPrompt = useNativeTools
            ? CustomWidgetTemplates.BuildStudioCoCreationSystemPrompt(
                effectiveLanguage,
                promptPack,
                useNativeTools: true)
            : null;
        string? cachedBlockSystemPrompt = !useNativeTools
            ? CustomWidgetTemplates.BuildStudioCoCreationSystemPrompt(
                effectiveLanguage,
                promptPack,
                useNativeTools: false)
            : null;

        string GetNativeSystemPrompt() => cachedNativeSystemPrompt ??=
            CustomWidgetTemplates.BuildStudioCoCreationSystemPrompt(
                effectiveLanguage,
                promptPack,
                useNativeTools: true);

        string GetBlockSystemPrompt() => cachedBlockSystemPrompt ??=
            CustomWidgetTemplates.BuildStudioCoCreationSystemPrompt(
                effectiveLanguage,
                promptPack,
                useNativeTools: false);

        var turnDiscoveredPackIds = new SortedSet<string>(StringComparer.Ordinal);
        foreach (string seedPack in CustomWidgetCapabilityRegistry.ResolveDomainPackIdsForCapabilities(sessionCapabilityPackIds))
        {
            turnDiscoveredPackIds.Add(seedPack);
        }
        foreach (string pinPack in CustomWidgetCapabilityRegistry.ResolveDomainPackIdsForCapabilities(pinnedCapabilityIds))
        {
            turnDiscoveredPackIds.Add(pinPack);
        }

        IReadOnlyList<AiChatTurnMessage>? tier2PrefixMessages = null;
        IReadOnlySet<string>? tier2CoveredAtomicIds = null;
        if (sessionCapabilityPackIds is { Count: > 0 })
        {
            string cachedCapsDoc = CustomWidgetCapabilityRegistry.BuildCachedCapabilityPrefixMessage(
                sessionCapabilityPackIds,
                isChinese,
                out IReadOnlySet<string> coveredAtomic);
            if (!string.IsNullOrWhiteSpace(cachedCapsDoc))
            {
                tier2CoveredAtomicIds = coveredAtomic;
                // Strict user/assistant alternation keeps Tier-2 KV-cache prefix compatible with every OpenAI-compatible provider.
                tier2PrefixMessages =
                [
                    new AiChatTurnMessage("user", cachedCapsDoc),
                    new AiChatTurnMessage("assistant", isChinese ? "已加载上述会话能力域契约，后续将严格遵循。" : "Acknowledged. I will follow these loaded capability domain contracts.")
                ];
            }
        }

        int estimatedWorkspacePromptChars =
            (currentManifestJson?.Length ?? 0) +
            (currentLogicJs?.Length ?? 0) +
            (briefMarkdown?.Length ?? 0) +
            18_000;
        AiContextBudget activeBudget = AiContextBudgetResolver.Resolve(
            resolved,
            _aiService.GetKnownContextTokens(resolved),
            estimatedPromptChars: estimatedWorkspacePromptChars);
        string BuildCoCreationUserPromptForBudget(AiContextBudget budget) =>
            CustomWidgetTemplates.BuildStudioCoCreationUserPrompt(
                userMessage,
                currentManifestJson,
                briefMarkdown,
                recentMessages,
                healthReport,
                targetNodePath,
                preferredEmoji,
                allowSampleData,
                effectiveLanguage,
                pinnedCapabilityIds,
                currentLogicJs: currentLogicJs,
                isCapsuleViewport: isCapsuleViewport,
                currentLocales: currentLocales,
                defaultWidgetWidth: resolvedDefaultWidth,
                defaultWidgetHeight: resolvedDefaultHeight,
                useNativeTools: useNativeTools,
                sampleDataJson: existingSampleDataJson,
                contextBudget: budget,
                runtimeTraceSummary: runtimeTraceSummary,
                alreadyInjectedCapabilityIds: tier2CoveredAtomicIds);
        string userPrompt = BuildCoCreationUserPromptForBudget(activeBudget);
        Func<string> buildCompactUserPrompt = () => BuildCoCreationUserPromptForBudget(
            AiContextBudgetResolver.Resolve(
                resolved,
                _aiService.GetKnownContextTokens(resolved),
                estimatedPromptChars: estimatedWorkspacePromptChars,
                forceCompact: true));

        const int MaxTotalLlmRounds = 8;
        const int MaxDiscoveryRounds = 2;
        const int MaxHardRepairRounds = 3;
        const int MaxSoftPreflightRepairRounds = 1;
        const int MaxContinuationRounds = 2;

        string? effectiveLockedTargetPath = StudioTurnStageRouter.ShouldEnforceHardSubtreeGuard(targetNodePath, userMessage)
            ? targetNodePath
            : null;
        var workspace = new StudioWorkspaceEngine(currentManifestJson, currentLogicJs, currentLocales, effectiveLockedTargetPath);
        var structuredTurns = new List<AiChatTurnMessage>();

        bool isInitialTurn = string.IsNullOrWhiteSpace(currentManifestJson);
        JsonNode? baselineCanonicalNode = null;
        string baselineLocaleSha = currentLocales is { HasAnyLocale: true } baseLoc
            ? baseLoc.ComputeSha256()
            : string.Empty;
        if (!isInitialTurn)
        {
            var baselineWorkspace = new StudioWorkspaceEngine(currentManifestJson, currentLogicJs, currentLocales, effectiveLockedTargetPath);
            (StudioTurnResult baselineSnapshot, _) = BuildWorkspaceTurnResult(
                baselineWorkspace,
                firstFinish: null,
                latestFinish: null,
                capturedSampleDataJson: null,
                existingSampleDataJson: null,
                originalManifestJson: currentManifestJson,
                originalLogicJs: currentLogicJs,
                originalLocales: currentLocales,
                effectiveLanguage: effectiveLanguage,
                preferredEmoji: null,
                isInitialCreation: false,
                wasAutoRepaired: false,
                providerId: providerId,
                effectiveModel: resolved.Model,
                runCompilerPipeline: false);
            string baselineJsonToParse = !string.IsNullOrWhiteSpace(baselineSnapshot.ManifestJson)
                ? baselineSnapshot.ManifestJson
                : currentManifestJson!;
            try
            {
                baselineCanonicalNode = StudioWorkspaceEngine.ParsePermissiveJsonNode(baselineJsonToParse);
            }
            catch
            {
                try { baselineCanonicalNode = StudioWorkspaceEngine.ParsePermissiveJsonNode(currentManifestJson!); } catch { }
            }

            baselineLocaleSha = baselineSnapshot.Locales is { HasAnyLocale: true } normLoc
                ? normLoc.ComputeSha256()
                : string.Empty;
        }

        bool IsCandidateNoOp(StudioTurnResult candidate) =>
            !isInitialTurn && IsUnchangedFromBaseline(candidate, baselineCanonicalNode, currentManifestJson, currentLogicJs, baselineLocaleSha);

        StudioTurnResult CreateNoOpResult(StudioTurnResult candidate) => new(
            Success: true,
            Kind: "answer",
            Reply: TranslateStep("CustomWidget.Workshop.CoCreate.NoOpEditReply"),
            ChangeSummary: string.Empty,
            BriefUpdates: candidate.BriefUpdates,
            Suggestions: candidate.Suggestions,
            SampleDataJson: null,
            ManifestJson: string.Empty,
            ParseResult: null,
            ErrorMessage: null,
            ProviderId: providerId,
            WasAutoRepaired: false,
            EffectiveModel: resolved.Model,
            FineGrainedDiffPills: [],
            WasNoOpEdit: true);

        bool workspaceMutated = false;
        bool wasAutoRepaired = false;
        bool retriedNoOpEdit = false;
        bool lastMutationRoundHadToolError = false;
        bool allowLocalProbe = StudioTurnResponseParser.ContainsExplicitLocalNetworkIntent(userMessage, currentManifestJson, briefMarkdown);

        int llmCallsUsed = 0;
        int toolRoundsUsed = 0;
        int discoveryRoundsUsed = 0;
        int hardRepairRoundsUsed = 0;
        int softRepairRoundsUsed = 0;
        int repairRoundsUsed = 0;
        int continuationRoundsUsed = 0;

        bool CanConsumeRepair(in TwoTierGateEvaluation gate, out bool consumesSoft)
        {
            if (llmCallsUsed >= MaxTotalLlmRounds)
            {
                consumesSoft = false;
                return false;
            }

            if (gate.HasBlockingFailure)
            {
                consumesSoft = false;
                return hardRepairRoundsUsed < MaxHardRepairRounds;
            }

            if (gate.HasSoftFailure)
            {
                consumesSoft = true;
                return softRepairRoundsUsed < MaxSoftPreflightRepairRounds;
            }

            consumesSoft = false;
            return false;
        }

        void RecordGateRepair(bool consumesSoft)
        {
            if (consumesSoft)
            {
                softRepairRoundsUsed++;
            }
            else
            {
                hardRepairRoundsUsed++;
            }

            repairRoundsUsed = hardRepairRoundsUsed + softRepairRoundsUsed;
            wasAutoRepaired = true;
        }

        StudioFinishTurnSpec? firstFinish = null;
        StudioFinishTurnSpec? latestFinish = null;
        string? capturedSampleDataJson = null;

        StudioTurnResult? firstParsedEdit = null;
        StudioTurnResult? latestStructurallyValidTurn = null;
        StudioTurnResult? lastAttemptTurn = null;
        string assistantReply = string.Empty;
        string thinkingReply = string.Empty;
        int activeRoundIndex = 0;

        void RecordToolWarningStep(
            string stepId,
            string toolName,
            string baseLabel,
            string reason,
            bool isTruncatedArg,
            string? rawErrorForLog,
            string? detectedWidgetNameOverride = null)
        {
            App.Log(
                $"[StudioAI] ToolWarn round={activeRoundIndex} tool={toolName} truncated={isTruncatedArg} err={CompactOneLine(rawErrorForLog, 180)}");

            string formattedReason = string.IsNullOrWhiteSpace(reason) ? string.Empty : $" · {reason}";
            if (string.Equals(lastWarnToolName, toolName, StringComparison.Ordinal) &&
                lastWarnStepId is not null)
            {
                if (!string.Equals(stepId, lastWarnStepId, StringComparison.Ordinal))
                {
                    for (int s = stepTrace.Count - 1; s >= 0; s--)
                    {
                        if (string.Equals(stepTrace[s].Id, stepId, StringComparison.Ordinal))
                        {
                            stepTrace.RemoveAt(s);
                            break;
                        }
                    }
                }

                consecutiveWarnCount++;
                string badged = TranslateStep(
                    "CustomWidget.Workshop.Stream.StepRetryBadgeFormat",
                    lastWarnBaseLabel ?? baseLabel,
                    consecutiveWarnCount);
                UpsertStep(lastWarnStepId, StudioStreamStepState.Warning, $"{badged}{formattedReason}");
            }
            else
            {
                consecutiveWarnCount = 1;
                lastWarnStepId = stepId;
                lastWarnToolName = toolName;
                lastWarnBaseLabel = baseLabel;
                UpsertStep(stepId, StudioStreamStepState.Warning, $"{baseLabel}{formattedReason}");
            }

            EmitTrace(
                AiGenerationStage.PatchingWorkspace,
                thinkingReply,
                assistantReply,
                0,
                0,
                detectedWidgetNameOverride ?? AiChatTransportClient.TryExtractStreamingWidgetName(assistantReply),
                toolName);
        }

        StudioTurnResult CompleteStudioTurn(StudioTurnResult turn)
        {
            string interruptedReason = TranslateStep("CustomWidget.Workshop.Stream.ReasonTruncatedRetry");
            for (int s = 0; s < stepTrace.Count; s++)
            {
                if (stepTrace[s].State == StudioStreamStepState.Running)
                {
                    if (string.Equals(stepTrace[s].Id, "connect", StringComparison.Ordinal))
                    {
                        stepTrace[s] = stepTrace[s] with
                        {
                            State = turn.Success ? StudioStreamStepState.Completed : StudioStreamStepState.Warning
                        };
                    }
                    else
                    {
                        string sealedLabel = stepTrace[s].Label.Contains(" · ", StringComparison.Ordinal)
                            ? stepTrace[s].Label
                            : $"{stepTrace[s].Label} · {interruptedReason}";
                        stepTrace[s] = new StudioStreamStepItem(stepTrace[s].Id, StudioStreamStepState.Warning, sealedLabel);
                    }
                }
            }

            StudioStreamStepItem[] finalSteps = stepTrace.ToArray();
            string? persistedThinking = string.IsNullOrWhiteSpace(thinkingReply)
                ? null
                : (thinkingReply.Length > 4000 ? "…\n" + thinkingReply[^4000..] : thinkingReply);
            if (!string.IsNullOrWhiteSpace(turn.ManifestJson))
            {
                (IReadOnlyList<CustomWidgetCapabilitySpec> usedPinned, IReadOnlyList<CustomWidgetCapabilitySpec> usedAuto) =
                    CustomWidgetCapabilityRegistry.SelectCapabilitiesForTurn(
                        turn.ManifestJson,
                        userMessage,
                        briefMarkdown,
                        pinnedCapabilityIds,
                        maxAutoCaps: 4,
                        compositionQuota: 0);
                foreach (string packId in CustomWidgetCapabilityRegistry.ResolveDomainPackIdsForCapabilities(
                             usedPinned.Concat(usedAuto).Select(static s => s.Id)))
                {
                    turnDiscoveredPackIds.Add(packId);
                }
            }
            turn = turn with
            {
                ExecutionSteps = finalSteps,
                ThinkingText = persistedThinking,
                DiscoveredCapabilityPackIds = turnDiscoveredPackIds.Count > 0 ? [.. turnDiscoveredPackIds] : null
            };
            EmitTrace(
                turn.Success ? AiGenerationStage.Validating : AiGenerationStage.AutoRepairing,
                thinkingReply,
                assistantReply,
                0,
                0,
                ResolveHumanWidgetTitle(turn.ParseResult?.Manifest, turn.Locales, effectiveLanguage));
            return FinalizeStudioTurnResult(
                turn,
                resolved,
                sw.ElapsedMilliseconds,
                userMessage,
                llmCallsUsed,
                toolRoundsUsed,
                repairRoundsUsed);
        }

        var probeHttpCache = new Dictionary<string, string>(StringComparer.Ordinal);
        var failedProbeHosts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var succeededProbeHosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string? lastFailedToolSignature = null;
        bool synthesisThinkingConsumed = false;

        Task<(TwoTierGateEvaluation Gate, StudioTurnResult CandidateTurn)> ValidateCurrentWorkspaceAsync(
            StudioFinishTurnSpec? effectiveFirstFinish,
            StudioFinishTurnSpec? effectiveLatestFinish) =>
            TryValidateAndPreflightWorkspaceTurnAsync(
                workspace,
                effectiveFirstFinish,
                effectiveLatestFinish,
                capturedSampleDataJson,
                existingSampleDataJson: existingSampleDataJson,
                originalManifestJson: currentManifestJson,
                originalLogicJs: currentLogicJs,
                originalLocales: currentLocales,
                effectiveLanguage: effectiveLanguage,
                preferredEmoji: isInitialTurn ? preferredEmoji : null,
                isInitialCreation: isInitialTurn,
                wasAutoRepaired: wasAutoRepaired,
                providerId: providerId,
                effectiveModel: resolved.Model,
                repairRoundsUsed: repairRoundsUsed,
                cancellationToken: cancellationToken,
                failedProbeHosts: failedProbeHosts);

        bool TrackGateCandidate(
            TwoTierGateEvaluation gate,
            StudioTurnResult candidate,
            bool onlyTrackAttemptWhenNonBlocking = false)
        {
            bool isNoOp = IsCandidateNoOp(candidate);
            bool isNonBlockingStructurallyValid = !gate.HasBlockingFailure && IsStructurallyValidEdit(candidate);
            if (!isNoOp && (!onlyTrackAttemptWhenNonBlocking || isNonBlockingStructurallyValid))
            {
                firstParsedEdit ??= candidate;
                lastAttemptTurn = candidate;
            }

            if (isNonBlockingStructurallyValid && !isNoOp)
            {
                latestStructurallyValidTurn = candidate;
            }

            return isNoOp;
        }

        StudioTurnResult CompleteCleanGateTurn(
            StudioTurnResult candidate,
            bool isNoOp,
            string? widgetNameOverride = null)
        {
            UpsertStep("validate", StudioStreamStepState.Completed, TranslateStep("CustomWidget.Workshop.Stream.StepValidatePass"));
            if (isNoOp)
            {
                EmitTrace(AiGenerationStage.Validating, thinkingReply, assistantReply, 0, 0, null);
                return CompleteStudioTurn(CreateNoOpResult(candidate));
            }

            string? resolvedTitle = widgetNameOverride
                ?? ResolveHumanWidgetTitle(candidate.ParseResult?.Manifest, candidate.Locales, effectiveLanguage);
            EmitTrace(AiGenerationStage.Validating, thinkingReply, assistantReply, 0, 0, resolvedTitle);
            return CompleteStudioTurn(candidate);
        }

        bool TryQueueChatNoOpRetry(string noOpPrompt)
        {
            if (retriedNoOpEdit || llmCallsUsed >= MaxTotalLlmRounds)
            {
                return false;
            }

            retriedNoOpEdit = true;
            UpsertStep($"noop-{llmCallsUsed}", StudioStreamStepState.Warning, TranslateStep("CustomWidget.Workshop.Stream.StepRepairing"));
            EmitTrace(AiGenerationStage.AutoRepairing, thinkingReply, assistantReply, 0, 0, null);
            structuredTurns.Add(new AiChatTurnMessage("assistant", assistantReply, thinkingReply));
            structuredTurns.Add(new AiChatTurnMessage("user", noOpPrompt));
            return true;
        }

        void EmitRepairStepTrace(string candidateManifestJson)
        {
            UpsertStep($"repair-{repairRoundsUsed}", StudioStreamStepState.Warning, TranslateStep("CustomWidget.Workshop.Stream.StepRepairing"));
            EmitTrace(
                AiGenerationStage.AutoRepairing,
                thinkingReply,
                candidateManifestJson,
                0,
                0,
                AiChatTransportClient.TryExtractStreamingWidgetName(candidateManifestJson));
        }

        while (llmCallsUsed < MaxTotalLlmRounds)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                if (latestStructurallyValidTurn is not null || lastAttemptTurn is not null)
                {
                    break;
                }

                return CompleteStudioTurn(
                    new StudioTurnResult(
                        Success: false,
                        Kind: "edit",
                        Reply: string.Empty,
                        ChangeSummary: string.Empty,
                        BriefUpdates: null,
                        Suggestions: [],
                        SampleDataJson: null,
                        ManifestJson: string.Empty,
                        ParseResult: null,
                        ErrorMessage: Localized.T("CustomWidget.AiApi.Error.GenerationCancelled"),
                        ProviderId: providerId,
                        EffectiveModel: resolved.Model));
            }

            string activeSystemPrompt = useNativeTools ? GetNativeSystemPrompt() : GetBlockSystemPrompt();
            bool allowDiscoveryTools = discoveryRoundsUsed < MaxDiscoveryRounds &&
                                       repairRoundsUsed == 0 &&
                                       llmCallsUsed < MaxTotalLlmRounds - 1;
            int roundIdxForStreaming = llmCallsUsed;
            activeRoundIndex = roundIdxForStreaming;
            Action<AiGenerationStreamProgress>? roundProgress = onProgress is null
                ? null
                : raw =>
                {
                    if (raw.Stage != AiGenerationStage.Connecting)
                    {
                        UpsertStep("connect", StudioStreamStepState.Completed, TranslateStep("CustomWidget.Workshop.Stream.StepConnect"));
                    }

                    if (!string.IsNullOrWhiteSpace(raw.ActiveToolName))
                    {
                        string? streamingStepId = null;
                        string? streamingStepLabel = null;
                        switch (raw.ActiveToolName)
                        {
                            case StudioToolTransport.ToolReadWorkspace:
                                streamingStepId = $"r{roundIdxForStreaming}-{StudioToolTransport.ToolReadWorkspace}";
                                streamingStepLabel = TranslateStep("CustomWidget.Workshop.Stream.StepReadWorkspace");
                                break;
                            case StudioToolTransport.ToolInspectCapabilities:
                                streamingStepId = $"r{roundIdxForStreaming}-{StudioToolTransport.ToolInspectCapabilities}";
                                streamingStepLabel = TranslateStep("CustomWidget.Workshop.Stream.StepInspectCapsFormat", "…");
                                break;
                            case StudioToolTransport.ToolProbeHttp:
                                streamingStepId = $"r{roundIdxForStreaming}-{StudioToolTransport.ToolProbeHttp}";
                                streamingStepLabel = TranslateStep("CustomWidget.Workshop.Stream.StepProbeApi");
                                break;
                            case StudioToolTransport.ToolWriteBundle:
                                streamingStepId = $"r{roundIdxForStreaming}-{StudioToolTransport.ToolWriteBundle}";
                                streamingStepLabel = TranslateStep("CustomWidget.Workshop.Stream.StepWriteBundle");
                                break;
                            case StudioToolTransport.ToolEditManifest:
                                streamingStepId = $"r{roundIdxForStreaming}-{StudioToolTransport.ToolEditManifest}";
                                streamingStepLabel = TranslateStep("CustomWidget.Workshop.Stream.StepPatchManifestStreaming");
                                break;
                            case StudioToolTransport.ToolPatchScript:
                                streamingStepId = $"r{roundIdxForStreaming}-{StudioToolTransport.ToolPatchScript}";
                                streamingStepLabel = TranslateStep("CustomWidget.Workshop.Stream.StepPatchScript");
                                break;
                        }

                        if (streamingStepId is not null && streamingStepLabel is not null)
                        {
                            UpsertStep(streamingStepId, StudioStreamStepState.Running, streamingStepLabel);
                        }
                    }

                    EmitTrace(
                        raw.Stage,
                        raw.ThinkingText,
                        raw.ContentText,
                        raw.ReasoningChars,
                        raw.ContentChars,
                        raw.DetectedWidgetName,
                        raw.ActiveToolName,
                        raw.StreamingHint,
                        roundThinkingTokens: raw.ThinkingTokens,
                        roundContentTokens: raw.ContentTokens,
                        roundOutputTokens: raw.TotalOutputTokens);
                };

            ResolvedAiInvocation roundResolved = StudioTurnStageRouter.ResolveRoundInvocation(
                resolved,
                structuredTurns.Count,
                workspaceMutated,
                isRepairRound: repairRoundsUsed > 0,
                synthesisThinkingConsumed,
                _aiService.IsThinkingParamsUnsupportedRoute(resolved),
                _aiService.CredentialStore.Get(resolved.Preset.Id)?.FetchedModels,
                out bool allowRoundSynthesisThinking);
            IReadOnlyList<AiChatTurnMessage> compactedTurns =
                StudioContextCompactor.CompactSupersededStructuredTurns(
                    structuredTurns,
                    compactAllWriteBundles: false);
            int roundEstimatedPromptChars = activeSystemPrompt.Length +
                                            userPrompt.Length +
                                            (tier2PrefixMessages is { Count: > 0 } ? EstimateStructuredTurnsChars(tier2PrefixMessages) : 0) +
                                            EstimateStructuredTurnsChars(compactedTurns);
            activeBudget = AiContextBudgetResolver.Resolve(
                roundResolved,
                _aiService.GetKnownContextTokens(roundResolved),
                estimatedPromptChars: roundEstimatedPromptChars);

            StudioStreamRoundOutcome round = await AiChatTransportClient.SendStudioStreamingRoundAsync(_aiService, 
                roundResolved,
                activeSystemPrompt,
                useNativeTools ? (cachedBlockSystemPrompt ?? activeSystemPrompt) : GetBlockSystemPrompt(),
                userPrompt,
                compactedTurns,
                includeStudioTools: useNativeTools,
                isRepairRound: repairRoundsUsed > 0,
                sw,
                roundProgress,
                cancellationToken,
                studioMaxTokensOverride: activeBudget.MaxStudioOutputTokens,
                compactUserPromptFactory: buildCompactUserPrompt,
                allowAutoSynthesisThinking: allowRoundSynthesisThinking,
                prefixMessages: tier2PrefixMessages);
            llmCallsUsed++;
            UpsertStep("connect", StudioStreamStepState.Completed, TranslateStep("CustomWidget.Workshop.Stream.StepConnect"));
            completedReasoningChars += round.ThinkingText.Length;
            int roundUiChars = round.VisibleContent.Length;
            int roundToolArgTokens = 0;
            for (int t = 0; t < round.ToolCalls.Count; t++)
            {
                roundUiChars += round.ToolCalls[t].ArgumentsJson.Length;
                roundToolArgTokens += StreamingTurnAccumulator.EstimateTokenCount(round.ToolCalls[t].ArgumentsJson);
            }
            completedContentChars += roundUiChars;

            int roundEstThinkTokens = StreamingTurnAccumulator.EstimateTokenCount(round.ThinkingText);
            if (allowRoundSynthesisThinking || roundEstThinkTokens >= 600)
            {
                synthesisThinkingConsumed = true;
            }
            int roundEstContentTokens = StreamingTurnAccumulator.EstimateTokenCount(round.VisibleContent) + roundToolArgTokens;
            int roundEstSum = roundEstThinkTokens + roundEstContentTokens;
            if (round.CompletionTokens > 0)
            {
                completedOutputTokens += round.CompletionTokens;
                if (roundEstSum > 0)
                {
                    int scaledThink = (int)Math.Round((double)round.CompletionTokens * roundEstThinkTokens / roundEstSum);
                    completedThinkingTokens += scaledThink;
                    completedContentTokens += Math.Max(0, round.CompletionTokens - scaledThink);
                }
                else
                {
                    completedContentTokens += round.CompletionTokens;
                }
            }
            else
            {
                completedThinkingTokens += roundEstThinkTokens;
                completedContentTokens += roundEstContentTokens;
                completedOutputTokens += roundEstSum;
            }

            if (round.RetriedWithoutTools)
            {
                useNativeTools = false;
            }

            if (!round.Success)
            {
                bool sealedOnAbort = false;
                for (int s = 0; s < stepTrace.Count; s++)
                {
                    if (stepTrace[s].State == StudioStreamStepState.Running)
                    {
                        stepTrace[s] = new StudioStreamStepItem(stepTrace[s].Id, StudioStreamStepState.Warning, stepTrace[s].Label);
                        sealedOnAbort = true;
                    }
                }
                if (sealedOnAbort)
                {
                    EmitTrace(AiGenerationStage.Validating, round.ThinkingText, round.VisibleContent, 0, 0, null);
                }

                if (latestStructurallyValidTurn is not null ||
                    lastAttemptTurn is not null ||
                    (workspaceMutated && !lastMutationRoundHadToolError && workspace.HasManifest))
                {
                    break;
                }

                return CompleteStudioTurn(
                    new StudioTurnResult(
                        Success: false,
                        Kind: "edit",
                        Reply: string.Empty,
                        ChangeSummary: string.Empty,
                        BriefUpdates: null,
                        Suggestions: [],
                        SampleDataJson: null,
                        ManifestJson: string.Empty,
                        ParseResult: null,
                        ErrorMessage: ResolveTurnErrorMessage(round.ErrorMessage, cancellationToken)
                            ?? Localized.T("CustomWidget.AiApi.Error.InvocationFailed"),
                        ProviderId: providerId,
                        EffectiveModel: resolved.Model));
            }

            assistantReply = round.VisibleContent;
            thinkingReply = round.ThinkingText;

            IReadOnlyList<AiToolCallSpec> activeCalls = round.ToolCalls;
            bool usedNativeCallArray = activeCalls.Count > 0;
            bool isTruncated = string.Equals(round.FinishReason, "length", StringComparison.OrdinalIgnoreCase);

            if (!usedNativeCallArray && StudioToolTransport.TryExtractBlockToolCalls(assistantReply, out List<AiToolCallSpec> blockCalls, allowCompleteBundleBlocks: !isTruncated))
            {
                activeCalls = blockCalls;
            }

            if (activeCalls.Count > 1)
            {
                activeCalls = StudioToolTransport.NormalizeAndBoundRoundToolCalls(activeCalls, out int droppedToolCalls);
                if (droppedToolCalls > 0)
                {
                    App.Log($"[StudioAI] Bounded runaway tool calls in round={activeRoundIndex}: kept={activeCalls.Count}, dropped={droppedToolCalls}");
                }
            }

            if (activeCalls.Count == 0)
            {
                string roundPrefix = $"r{activeRoundIndex}-";
                string nonCallReason = TranslateStep(isTruncated
                    ? "CustomWidget.Workshop.Stream.ReasonTruncatedRetry"
                    : "CustomWidget.Workshop.Stream.ReasonInvalidArgs");
                bool closedStreamingStep = false;
                for (int s = 0; s < stepTrace.Count; s++)
                {
                    if (stepTrace[s].Id.StartsWith(roundPrefix, StringComparison.Ordinal) &&
                        stepTrace[s].State == StudioStreamStepState.Running)
                    {
                        string label = stepTrace[s].Label.Contains(" · ", StringComparison.Ordinal)
                            ? stepTrace[s].Label
                            : $"{stepTrace[s].Label} · {nonCallReason}";
                        stepTrace[s] = new StudioStreamStepItem(stepTrace[s].Id, StudioStreamStepState.Warning, label);
                        closedStreamingStep = true;
                    }
                }
                if (closedStreamingStep)
                {
                    EmitTrace(AiGenerationStage.Validating, thinkingReply, assistantReply, 0, 0, null);
                }
            }

            // 1. Handle tool-based flow (OpenAI native tools OR block-based tools: deskbox-action / manifest-edit / script-patch)
            if (activeCalls.Count > 0)
            {
                var toolResults = new List<(string Id, string Name, string Content, bool TruncatedArgs)>(activeCalls.Count);
                StudioFinishTurnSpec? turnFinish = null;
                string? finishCallId = null;
                bool hadNonFinishMutationOrRead = false;
                bool roundHadDiscoveryCall = false;
                bool roundMutatedWorkspace = false;
                bool roundHadToolError = false;
                bool roundSalvagedWriteBundle = false;
                bool roundSalvagedIncludesLogicJs = false;
                bool roundAppliedWriteBundle = false;
                bool roundAppliedPatchScript = false;
                bool roundWriteBundleDeclaredScriptWithoutLogic = false;
                bool roundWriteBundleOmittedLogicOnScriptedEdit = false;
                int lastMutationResultIdx = -1;

                var toolCallCountsByName = new Dictionary<string, int>(StringComparer.Ordinal);
                string ResolveStepId(string toolName)
                {
                    toolCallCountsByName.TryGetValue(toolName, out int seenCount);
                    toolCallCountsByName[toolName] = seenCount + 1;
                    return seenCount == 0
                        ? $"r{roundIdxForStreaming}-{toolName}"
                        : $"r{roundIdxForStreaming}-{toolName}-{seenCount}";
                }

                static string? ResolveSectionReadTargetLabel(StudioReadWorkspaceRequest req)
                {
                    string section = (req.Section ?? string.Empty).Trim().ToLowerInvariant();
                    string rangeSuffix = req.StartLine.HasValue || req.EndLine.HasValue
                        ? $":{req.StartLine ?? 1}-{req.EndLine?.ToString() ?? "end"}"
                        : string.Empty;
                    return section switch
                    {
                        "script" or "logic" => $"logic.js{rangeSuffix}",
                        "locales" or "locale" => "locales",
                        "manifest" or "manifest_section" => $"manifest.json{rangeSuffix}",
                        _ => null
                    };
                }

                for (int tcIdx = 0; tcIdx < activeCalls.Count; tcIdx++)
                {
                    AiToolCallSpec tc = activeCalls[tcIdx];
                    string stepId = ResolveStepId(tc.Name);
                    switch (tc.Name)
                    {
                        case StudioToolTransport.ToolReadWorkspace:
                        {
                            hadNonFinishMutationOrRead = true;
                            if (!workspace.HasManifest)
                            {
                                UpsertStep(stepId, StudioStreamStepState.Completed, TranslateStep("CustomWidget.Workshop.Stream.StepReadWorkspace"));
                                toolResults.Add((
                                    tc.Id,
                                    tc.Name,
                                    "Workspace is currently empty (initial creation). Do not call read_workspace; call inspect_capabilities or write_bundle directly.",
                                    false));
                                EmitTrace(AiGenerationStage.ReadingWorkspace, thinkingReply, assistantReply, 0, 0, null, tc.Name);
                                break;
                            }
                            if (!allowDiscoveryTools)
                            {
                                UpsertStep(stepId, StudioStreamStepState.Completed, TranslateStep("CustomWidget.Workshop.Stream.StepReadWorkspace"));
                                toolResults.Add((
                                    tc.Id,
                                    tc.Name,
                                    "Discovery budget is already exhausted for this turn — read_workspace is disabled. Do not call read_workspace again; apply your changes immediately with edit_manifest / patch_script (or write_bundle) and call finish_turn.",
                                    false));
                                EmitTrace(AiGenerationStage.ReadingWorkspace, thinkingReply, assistantReply, 0, 0, null, tc.Name);
                                break;
                            }
                            roundHadDiscoveryCall = true;
                            if (!StudioWorkspaceEngine.IsWorkspaceFolded(
                                    workspace.GetManifestJson(indented: false),
                                    workspace.LogicJs))
                            {
                                discoveryRoundsUsed = MaxDiscoveryRounds;
                                UpsertStep(stepId, StudioStreamStepState.Completed, TranslateStep("CustomWidget.Workshop.Stream.StepReadWorkspace"));
                                toolResults.Add((
                                    tc.Id,
                                    tc.Name,
                                    "Full manifest.json and logic.js are already provided verbatim in the user prompt above. Do not call read_workspace again; emit your edit_manifest / patch_script / finish_turn calls directly.",
                                    false));
                                EmitTrace(AiGenerationStage.ReadingWorkspace, thinkingReply, assistantReply, 0, 0, null, tc.Name);
                                break;
                            }
                            if (StudioToolTransport.TryParseReadWorkspaceArgs(tc.ArgumentsJson, out StudioReadWorkspaceRequest? readReq, out string? readErr) &&
                                readReq is not null)
                            {
                                string content = workspace.ReadWorkspace(readReq);
                                toolResults.Add((tc.Id, tc.Name, content, false));
                                string? readTarget = !string.IsNullOrWhiteSpace(readReq.NodePath)
                                    ? readReq.NodePath.Trim()
                                    : (!string.IsNullOrWhiteSpace(readReq.Symbol)
                                        ? readReq.Symbol.Trim()
                                        : ResolveSectionReadTargetLabel(readReq));
                                string readLabel = !string.IsNullOrWhiteSpace(readTarget)
                                    ? TranslateStep("CustomWidget.Workshop.Stream.StepReadNodeFormat", readTarget)
                                    : TranslateStep("CustomWidget.Workshop.Stream.StepReadWorkspace");
                                UpsertStep(stepId, StudioStreamStepState.Completed, readLabel);
                                EmitTrace(AiGenerationStage.ReadingWorkspace, thinkingReply, assistantReply, 0, 0, null, tc.Name);
                            }
                            else
                            {
                                roundHadToolError = true;
                                bool readTruncated = isTruncated || IsTruncatedJsonError(readErr);
                                RecordToolWarningStep(
                                    stepId,
                                    tc.Name,
                                    TranslateStep("CustomWidget.Workshop.Stream.StepReadWorkspace"),
                                    TranslateStep(readTruncated ? "CustomWidget.Workshop.Stream.ReasonTruncatedRetry" : "CustomWidget.Workshop.Stream.ReasonInvalidArgs"),
                                    readTruncated,
                                    readErr);
                                toolResults.Add((tc.Id, tc.Name, $"Tool error: {readErr}", readTruncated));
                            }
                            break;
                        }

                        case StudioToolTransport.ToolInspectCapabilities:
                        case StudioToolTransport.ToolProbeHttp:
                        {
                            hadNonFinishMutationOrRead = true;
                            roundHadDiscoveryCall = true;
                            if (discoveryRoundsUsed >= MaxDiscoveryRounds || repairRoundsUsed > 0 || llmCallsUsed >= MaxTotalLlmRounds - 1)
                            {
                                string cappedLabel = tc.Name == StudioToolTransport.ToolProbeHttp
                                    ? TranslateStep("CustomWidget.Workshop.Stream.StepProbeApi")
                                    : TranslateStep("CustomWidget.Workshop.Stream.StepInspectCapsFormat", "…");
                                UpsertStep(stepId, StudioStreamStepState.Completed, cappedLabel);
                                toolResults.Add((
                                    tc.Id,
                                    tc.Name,
                                    "Discovery budget reached for this turn; proceed immediately to write_bundle / edit_manifest / patch_script + finish_turn.",
                                    false));
                                break;
                            }
                            if (StudioToolTransport.TryParseInspectOrProbeArgs(tc.Name, tc.ArgumentsJson, out StudioAgenticActionRequest? actReq, out string? actErr) &&
                                actReq is not null)
                            {
                                bool isProbe = string.Equals(actReq.Action, "probeHttp", StringComparison.OrdinalIgnoreCase);
                                AiGenerationStage actionStage = isProbe
                                    ? AiGenerationStage.ProbingApi
                                    : AiGenerationStage.InspectingCapabilities;
                                string stepLabel = isProbe
                                    ? TranslateStep("CustomWidget.Workshop.Stream.StepProbeApi")
                                    : TranslateStep(
                                        "CustomWidget.Workshop.Stream.StepInspectCapsFormat",
                                        actReq.CapabilityIds is { Count: > 0 }
                                            ? string.Join(", ", actReq.CapabilityIds.Take(3))
                                            : (!string.IsNullOrWhiteSpace(actReq.Query) ? actReq.Query : "…"));
                                UpsertStep(stepId, StudioStreamStepState.Running, stepLabel);
                                EmitTrace(actionStage, thinkingReply, assistantReply, 0, 0, null, tc.Name);

                                string resultText;
                                string? probeCacheKey = isProbe ? BuildProbeHttpCacheKey(actReq) : null;
                                if (probeCacheKey is not null && probeHttpCache.TryGetValue(probeCacheKey, out string? cachedProbeResult))
                                {
                                    resultText = cachedProbeResult;
                                }
                                else
                                {
                                    resultText = await StudioTurnResponseParser.ExecuteStudioAgenticActionAsync(_networkSourceEngine,
                                        actReq,
                                        useNativeTools: useNativeTools,
                                        cancellationToken,
                                        allowLocalProbe: allowLocalProbe);
                                    if (!isProbe && actReq.CapabilityIds is { Count: > 0 })
                                    {
                                        foreach (string discoveredPack in CustomWidgetCapabilityRegistry.ResolveDomainPackIdsForCapabilities(actReq.CapabilityIds))
                                        {
                                            turnDiscoveredPackIds.Add(discoveredPack);
                                        }
                                    }
                                    if (probeCacheKey is not null)
                                    {
                                        probeHttpCache[probeCacheKey] = resultText;
                                    }
                                }
                                if (isProbe &&
                                    !string.IsNullOrWhiteSpace(actReq.Url) &&
                                    Uri.TryCreate(actReq.Url.Trim(), UriKind.Absolute, out Uri? probedUri) &&
                                    !string.IsNullOrWhiteSpace(probedUri.DnsSafeHost))
                                {
                                    string probeMethod = string.IsNullOrWhiteSpace(actReq.Method) ? "GET" : actReq.Method.Trim();
                                    if (resultText.Contains("(ok=true)", StringComparison.Ordinal))
                                    {
                                        succeededProbeHosts.Add(probedUri.DnsSafeHost);
                                        failedProbeHosts.Remove(probedUri.DnsSafeHost);
                                    }
                                    else if (resultText.Contains("(ok=false)", StringComparison.Ordinal) &&
                                             string.Equals(probeMethod, "GET", StringComparison.OrdinalIgnoreCase) &&
                                             !succeededProbeHosts.Contains(probedUri.DnsSafeHost))
                                    {
                                        failedProbeHosts[probedUri.DnsSafeHost] = ExtractProbeFailureSummary(resultText);
                                    }
                                }
                                UpsertStep(stepId, StudioStreamStepState.Completed, stepLabel);
                                EmitTrace(actionStage, thinkingReply, assistantReply, 0, 0, null, tc.Name);
                                toolResults.Add((tc.Id, tc.Name, resultText, false));
                            }
                            else
                            {
                                roundHadToolError = true;
                                bool actTruncated = isTruncated || IsTruncatedJsonError(actErr);
                                string fallbackLabel = tc.Name == StudioToolTransport.ToolProbeHttp
                                    ? TranslateStep("CustomWidget.Workshop.Stream.StepProbeApi")
                                    : TranslateStep("CustomWidget.Workshop.Stream.StepInspectCapsFormat", "…");
                                RecordToolWarningStep(
                                    stepId,
                                    tc.Name,
                                    fallbackLabel,
                                    TranslateStep(actTruncated ? "CustomWidget.Workshop.Stream.ReasonTruncatedRetry" : "CustomWidget.Workshop.Stream.ReasonInvalidArgs"),
                                    actTruncated,
                                    actErr);
                                toolResults.Add((tc.Id, tc.Name, $"Tool error: {actErr}", actTruncated));
                            }
                            break;
                        }

                        case StudioToolTransport.ToolEditManifest:
                        {
                            hadNonFinishMutationOrRead = true;
                            if (StudioToolTransport.TryParseEditManifestArgs(tc.ArgumentsJson, out StudioFlatManifestEditRequest? flatEdit, out string? flatErr) &&
                                flatEdit is not null)
                            {
                                StudioWorkspaceOpResult opRes = workspace.ApplyFlatManifestEdit(flatEdit);
                                int editCount = Math.Max(1, opRes.TouchedPaths?.Count ?? 1);
                                if (opRes.Succeeded)
                                {
                                    workspaceMutated = true;
                                    roundMutatedWorkspace = true;
                                    lastMutationResultIdx = toolResults.Count;
                                    UpsertStep(stepId, StudioStreamStepState.Completed, TranslateStep("CustomWidget.Workshop.Stream.StepPatchManifestFormat", editCount));
                                    EmitTrace(AiGenerationStage.PatchingWorkspace, thinkingReply, assistantReply, 0, 0, AiChatTransportClient.TryExtractStreamingWidgetName(assistantReply), tc.Name);
                                }
                                else
                                {
                                    roundHadToolError = true;
                                    RecordToolWarningStep(
                                        stepId,
                                        tc.Name,
                                        TranslateStep("CustomWidget.Workshop.Stream.StepPatchManifestFormat", editCount),
                                        TranslateStep("CustomWidget.Workshop.Stream.ReasonPatchMismatch"),
                                        isTruncatedArg: false,
                                        rawErrorForLog: $"{opRes.Summary}: {opRes.ErrorMessage}");
                                }
                                string msg = opRes.Succeeded
                                    ? opRes.Summary
                                    : $"Tool error ({opRes.Summary}): {opRes.ErrorMessage}";
                                toolResults.Add((tc.Id, tc.Name, msg, false));
                            }
                            else
                            {
                                roundHadToolError = true;
                                bool flatTruncated = isTruncated || IsTruncatedJsonError(flatErr);
                                RecordToolWarningStep(
                                    stepId,
                                    tc.Name,
                                    TranslateStep("CustomWidget.Workshop.Stream.StepPatchManifestStreaming"),
                                    TranslateStep(flatTruncated ? "CustomWidget.Workshop.Stream.ReasonTruncatedRetry" : "CustomWidget.Workshop.Stream.ReasonInvalidArgs"),
                                    flatTruncated,
                                    flatErr);
                                toolResults.Add((tc.Id, tc.Name, $"Tool error: {flatErr}", flatTruncated));
                            }
                            break;
                        }

                        case StudioToolTransport.ToolPatchScript:
                        {
                            hadNonFinishMutationOrRead = true;
                            string scriptBaseLabel = TranslateStep("CustomWidget.Workshop.Stream.StepPatchScript");
                            if (StudioToolTransport.TryParsePatchScriptArgs(tc.ArgumentsJson, out List<StudioScriptPatchHunk> hunks, out string? fullScriptContent, out string? hunkErr))
                            {
                                if (fullScriptContent is not null)
                                {
                                    bool canApplyFullScript = string.IsNullOrWhiteSpace(workspace.LogicJs) ||
                                                              fullScriptContent.Contains("export function", StringComparison.Ordinal);
                                    if (canApplyFullScript)
                                    {
                                        workspace.SetLogicJs(fullScriptContent);
                                        workspaceMutated = true;
                                        roundMutatedWorkspace = true;
                                        roundAppliedPatchScript = true;
                                        lastMutationResultIdx = toolResults.Count;
                                        UpsertStep(stepId, StudioStreamStepState.Completed, scriptBaseLabel);
                                        EmitTrace(AiGenerationStage.PatchingWorkspace, thinkingReply, assistantReply, 0, 0, AiChatTransportClient.TryExtractStreamingWidgetName(assistantReply), tc.Name);
                                        toolResults.Add((tc.Id, tc.Name, "Updated logic.js.", false));
                                    }
                                    else
                                    {
                                        roundHadToolError = true;
                                        const string partialErr = "Partial snippet in 'content' rejected while logic.js already exists; pass 'hunks' with SEARCH/REPLACE anchors or pass the complete ES2023 module.";
                                        RecordToolWarningStep(
                                            stepId,
                                            tc.Name,
                                            scriptBaseLabel,
                                            TranslateStep("CustomWidget.Workshop.Stream.ReasonPatchMismatch"),
                                            isTruncatedArg: false,
                                            rawErrorForLog: partialErr);
                                        toolResults.Add((tc.Id, tc.Name, $"Tool error: {partialErr}", false));
                                    }
                                }
                                else
                                {
                                    StudioWorkspaceOpResult opRes = workspace.ApplyScriptPatch(hunks);
                                    if (opRes.Succeeded)
                                    {
                                        workspaceMutated = true;
                                        roundMutatedWorkspace = true;
                                        roundAppliedPatchScript = true;
                                        lastMutationResultIdx = toolResults.Count;
                                        UpsertStep(stepId, StudioStreamStepState.Completed, scriptBaseLabel);
                                        EmitTrace(AiGenerationStage.PatchingWorkspace, thinkingReply, assistantReply, 0, 0, AiChatTransportClient.TryExtractStreamingWidgetName(assistantReply), tc.Name);
                                        toolResults.Add((tc.Id, tc.Name, opRes.Summary, false));
                                    }
                                    else
                                    {
                                        roundHadToolError = true;
                                        RecordToolWarningStep(
                                            stepId,
                                            tc.Name,
                                            scriptBaseLabel,
                                            TranslateStep("CustomWidget.Workshop.Stream.ReasonPatchMismatch"),
                                            isTruncatedArg: false,
                                            rawErrorForLog: $"{opRes.Summary}: {opRes.ErrorMessage}");
                                        toolResults.Add((tc.Id, tc.Name, $"Tool error ({opRes.Summary}): {opRes.ErrorMessage}", false));
                                    }
                                }
                            }
                            else
                            {
                                roundHadToolError = true;
                                bool scriptTruncated = isTruncated || IsTruncatedJsonError(hunkErr);
                                RecordToolWarningStep(
                                    stepId,
                                    tc.Name,
                                    scriptBaseLabel,
                                    TranslateStep(scriptTruncated ? "CustomWidget.Workshop.Stream.ReasonTruncatedRetry" : "CustomWidget.Workshop.Stream.ReasonInvalidArgs"),
                                    scriptTruncated,
                                    hunkErr);
                                toolResults.Add((tc.Id, tc.Name, $"Tool error: {hunkErr}", scriptTruncated));
                            }
                            break;
                        }

                        case StudioToolTransport.ToolWriteBundle:
                        {
                            hadNonFinishMutationOrRead = true;
                            string bundleBaseLabel = TranslateStep("CustomWidget.Workshop.Stream.StepWriteBundle");
                            if (StudioToolTransport.TryParseWriteBundleArgs(
                                    tc.ArgumentsJson,
                                    out string wbManifest,
                                    out string? wbLogicJs,
                                    out CustomWidgetLocaleCatalog? wbLocales,
                                    out bool wbDelScript,
                                    out string? wbSampleData,
                                    out string? wbErr))
                            {
                                StudioWorkspaceOpResult opRes;
                                if (string.IsNullOrWhiteSpace(wbManifest))
                                {
                                    if (!workspace.HasManifest)
                                    {
                                        opRes = new StudioWorkspaceOpResult(
                                            false,
                                            "write_bundle failed",
                                            "Workspace does not have manifest.json yet. Include 'manifest' in your first write_bundle call.");
                                    }
                                    else
                                    {
                                        if (wbDelScript || wbLogicJs is not null)
                                        {
                                            workspace.SetLogicJs(wbLogicJs, deleteScript: wbDelScript);
                                        }
                                        if (wbLocales is not null)
                                        {
                                            workspace.Locales = wbLocales;
                                        }
                                        opRes = new StudioWorkspaceOpResult(
                                            true,
                                            "Updated companion bundle files on existing workspace.",
                                            TouchedPaths: ["logic.js"]);
                                    }
                                }
                                else
                                {
                                    opRes = workspace.SetFullBundle(wbManifest, wbLogicJs, locales: wbLocales, deleteScript: wbDelScript);
                                }
                                if (opRes.Succeeded)
                                {
                                    workspaceMutated = true;
                                    roundMutatedWorkspace = true;
                                    roundAppliedWriteBundle = true;
                                    if (!string.IsNullOrWhiteSpace(wbManifest) && string.IsNullOrWhiteSpace(wbLogicJs) && !wbDelScript)
                                    {
                                        roundWriteBundleDeclaredScriptWithoutLogic =
                                            string.IsNullOrWhiteSpace(workspace.LogicJs) &&
                                            RawManifestDeclaresScript(wbManifest);
                                        roundWriteBundleOmittedLogicOnScriptedEdit = !isInitialTurn && !string.IsNullOrWhiteSpace(currentLogicJs);
                                    }
                                    else
                                    {
                                        roundWriteBundleDeclaredScriptWithoutLogic = false;
                                        roundWriteBundleOmittedLogicOnScriptedEdit = false;
                                    }
                                    lastMutationResultIdx = toolResults.Count;
                                    if (!string.IsNullOrWhiteSpace(wbSampleData))
                                    {
                                        capturedSampleDataJson = wbSampleData;
                                    }
                                    UpsertStep(stepId, StudioStreamStepState.Completed, bundleBaseLabel);
                                    EmitTrace(
                                        AiGenerationStage.PatchingWorkspace,
                                        thinkingReply,
                                        assistantReply,
                                        0,
                                        0,
                                        AiChatTransportClient.TryExtractStreamingWidgetName(wbManifest),
                                        tc.Name,
                                        progressiveManifestJson: workspace.GetManifestJson(indented: true));
                                }
                                else
                                {
                                    roundHadToolError = true;
                                    bool isGuard = opRes.Summary.Contains("Subtree Guard", StringComparison.OrdinalIgnoreCase);
                                    RecordToolWarningStep(
                                        stepId,
                                        tc.Name,
                                        bundleBaseLabel,
                                        TranslateStep(isGuard ? "CustomWidget.Workshop.Stream.ReasonGuardRejected" : "CustomWidget.Workshop.Stream.ReasonInvalidArgs"),
                                        isTruncatedArg: false,
                                        rawErrorForLog: $"{opRes.Summary}: {opRes.ErrorMessage}");
                                }
                                string msg = opRes.Succeeded
                                    ? opRes.Summary
                                    : $"Tool error ({opRes.Summary}): {opRes.ErrorMessage}";
                                toolResults.Add((tc.Id, tc.Name, msg, false));
                            }
                            else
                            {
                                bool wbTruncated = isTruncated || IsTruncatedJsonError(wbErr);
                                if (wbTruncated &&
                                    StudioWorkspaceEngine.TrySalvageManifestFromTruncatedWriteBundle(tc.ArgumentsJson, out string salvagedManifest))
                                {
                                    StudioWorkspaceOpResult salvageRes = workspace.ReplaceManifestJson(salvagedManifest);
                                    if (salvageRes.Succeeded)
                                    {
                                        workspaceMutated = true;
                                        roundMutatedWorkspace = true;
                                        roundSalvagedWriteBundle = true;
                                        roundSalvagedIncludesLogicJs = !string.IsNullOrWhiteSpace(workspace.LogicJs);
                                        lastMutationResultIdx = toolResults.Count;
                                        string overviewSummary = workspace.ReadWorkspace(new StudioReadWorkspaceRequest("overview"));
                                        RecordToolWarningStep(
                                            stepId,
                                            tc.Name,
                                            bundleBaseLabel,
                                            TranslateStep("CustomWidget.Workshop.Stream.ReasonTruncatedSalvaged"),
                                            isTruncatedArg: true,
                                            rawErrorForLog: wbErr,
                                            detectedWidgetNameOverride: AiChatTransportClient.TryExtractStreamingWidgetName(salvagedManifest));
                                        string salvageResultMessage = roundSalvagedIncludesLogicJs
                                            ? $"Manifest and logic.js salvaged from truncated write_bundle and applied to workspace. Salvaged workspace summary:\n{overviewSummary}\n\nNext: verify the salvaged summary, apply only missing deltas via edit_manifest (upsertElements/removeElements/patchRoot) / patch_script if needed, and call finish_turn without resending full manifest.json or logic.js."
                                            : $"Manifest salvaged from truncated write_bundle and applied to workspace; logicJs was truncated at output limit. Salvaged workspace summary:\n{overviewSummary}\n\nNext: add any missing elements with edit_manifest (upsertElements/patchRoot), write logic.js via patch_script(content='...'), and call finish_turn without resending manifest.json.";
                                        toolResults.Add((
                                            tc.Id,
                                            tc.Name,
                                            salvageResultMessage,
                                            true));
                                    }
                                    else
                                    {
                                        roundHadToolError = true;
                                        bool isGuard = salvageRes.Summary.Contains("Subtree Guard", StringComparison.OrdinalIgnoreCase);
                                        RecordToolWarningStep(
                                            stepId,
                                            tc.Name,
                                            bundleBaseLabel,
                                            TranslateStep(isGuard ? "CustomWidget.Workshop.Stream.ReasonGuardRejected" : "CustomWidget.Workshop.Stream.ReasonTruncatedRetry"),
                                            isTruncatedArg: true,
                                            rawErrorForLog: salvageRes.ErrorMessage ?? wbErr);
                                        toolResults.Add((tc.Id, tc.Name, $"Tool error: {salvageRes.ErrorMessage ?? wbErr}", true));
                                    }
                                }
                                else
                                {
                                    roundHadToolError = true;
                                    RecordToolWarningStep(
                                        stepId,
                                        tc.Name,
                                        bundleBaseLabel,
                                        TranslateStep(wbTruncated ? "CustomWidget.Workshop.Stream.ReasonTruncatedRetry" : "CustomWidget.Workshop.Stream.ReasonInvalidArgs"),
                                        wbTruncated,
                                        wbErr);
                                    string wbErrContent = wbTruncated && !workspace.HasManifest
                                        ? "Tool error: write_bundle JSON was truncated at output length limit before manifest.json completed. Step 1: call write_bundle with ONLY a concise 'manifest' object (omit 'logicJs' and 'sampleData' for now). Step 2: in the next round, write logic.js via patch_script(content='...') and call finish_turn."
                                        : $"Tool error: {wbErr}";
                                    toolResults.Add((tc.Id, tc.Name, wbErrContent, wbTruncated));
                                }
                            }
                            break;
                        }

                        case StudioToolTransport.ToolFinishTurn:
                        {
                            if (StudioToolTransport.TryParseFinishTurnArgs(tc.ArgumentsJson, out StudioFinishTurnSpec? finSpec, out string? finErr) &&
                                finSpec is not null)
                            {
                                turnFinish = finSpec;
                                finishCallId = tc.Id;
                                firstFinish ??= finSpec;
                                latestFinish = finSpec;
                                if (!string.IsNullOrWhiteSpace(finSpec.SampleDataJson))
                                {
                                    capturedSampleDataJson = finSpec.SampleDataJson;
                                }
                                if (finSpec.DeleteScript)
                                {
                                    workspace.SetLogicJs(null, deleteScript: true);
                                    workspaceMutated = true;
                                    roundMutatedWorkspace = true;
                                }
                                toolResults.Add((tc.Id, tc.Name, "finish_turn acknowledged.", false));
                            }
                            else
                            {
                                roundHadToolError = true;
                                bool finTruncated = isTruncated || IsTruncatedJsonError(finErr);
                                App.Log($"[StudioAI] ToolWarn round={activeRoundIndex} tool={tc.Name} truncated={finTruncated} err={CompactOneLine(finErr, 180)}");
                                toolResults.Add((tc.Id, tc.Name, $"Tool error: {finErr}", finTruncated));
                            }
                            break;
                        }

                        default:
                            roundHadToolError = true;
                            toolResults.Add((tc.Id, tc.Name, $"Unsupported tool '{tc.Name}'.", false));
                            break;
                    }
                }

                // Seal any lingering streaming Running step from this round (e.g. if active streaming tool name differed from finalized tool call list)
                string roundToolPrefix = $"r{activeRoundIndex}-";
                bool sealedLingeringStep = false;
                for (int s = 0; s < stepTrace.Count; s++)
                {
                    if (stepTrace[s].Id.StartsWith(roundToolPrefix, StringComparison.Ordinal) &&
                        stepTrace[s].State == StudioStreamStepState.Running)
                    {
                        stepTrace[s] = new StudioStreamStepItem(
                            stepTrace[s].Id,
                            StudioStreamStepState.Warning,
                            $"{stepTrace[s].Label} · {TranslateStep(isTruncated ? "CustomWidget.Workshop.Stream.ReasonTruncatedRetry" : "CustomWidget.Workshop.Stream.ReasonInvalidArgs")}");
                        sealedLingeringStep = true;
                    }
                }
                if (sealedLingeringStep)
                {
                    EmitTrace(AiGenerationStage.PatchingWorkspace, thinkingReply, assistantReply, 0, 0, null);
                }

                if (roundHadToolError && activeCalls.Count > 0)
                {
                    AiToolCallSpec primaryCall = activeCalls[0];
                    string sig = $"{primaryCall.Name}:{primaryCall.ArgumentsJson.Trim()}";
                    if (string.Equals(sig, lastFailedToolSignature, StringComparison.Ordinal) && toolResults.Count > 0)
                    {
                        var lastRes = toolResults[^1];
                        toolResults[^1] = (
                            lastRes.Id,
                            lastRes.Name,
                            $"{lastRes.Content}\n[Loop Guard] Identical failing tool call repeated. Do NOT resend the same arguments — inspect the error path above or switch strategy.",
                            lastRes.TruncatedArgs);
                    }
                    lastFailedToolSignature = sig;
                }
                else if (roundMutatedWorkspace && !roundHadToolError)
                {
                    lastFailedToolSignature = null;
                }

                if (roundMutatedWorkspace && roundHadToolError && !roundSalvagedWriteBundle)
                {
                    lastMutationRoundHadToolError = true;
                }
                else if (roundMutatedWorkspace && !roundHadToolError)
                {
                    lastMutationRoundHadToolError = false;
                }

                bool anyTruncatedToolArgs = roundSalvagedWriteBundle || toolResults.Any(static r => r.TruncatedArgs);
                if (anyTruncatedToolArgs && continuationRoundsUsed < MaxContinuationRounds && llmCallsUsed < MaxTotalLlmRounds)
                {
                    continuationRoundsUsed++;
                    string truncationHint = roundSalvagedWriteBundle
                        ? (roundSalvagedIncludesLogicJs
                            ? "Manifest and logic.js from truncated write_bundle have ALREADY been salvaged into workspace. Apply only missing deltas via edit_manifest (upsertElements/removeElements/patchRoot) / patch_script if needed, and call finish_turn without resending full manifest.json or logic.js."
                            : "Manifest from truncated write_bundle has ALREADY been salvaged into workspace. Add any missing UI elements with edit_manifest (upsertElements/patchRoot), write logic.js via patch_script(content='...'), and call finish_turn without resending manifest.json.")
                        : workspace.HasManifest
                            ? "Tool arguments hit output length limit. Re-issue smaller incremental edit_manifest / patch_script chunks."
                            : "Tool arguments hit output length limit before manifest.json was created. First call write_bundle with ONLY the 'manifest' object (omit 'logicJs' and 'sampleData'), then add logic.js via patch_script in the next round.";
                    AppendToolTurnMessages(structuredTurns, usedNativeCallArray, assistantReply, thinkingReply, activeCalls, toolResults,
                        extraHint: truncationHint);
                    continue;
                }

                if (turnFinish is not null)
                {
                    if (roundHadToolError &&
                        !roundSalvagedWriteBundle &&
                        string.Equals(turnFinish.Kind, "edit", StringComparison.OrdinalIgnoreCase) &&
                        llmCallsUsed < MaxTotalLlmRounds)
                    {
                        UpdateToolResultContent(
                            toolResults,
                            finishCallId,
                            "finish_turn REJECTED because another tool call in this round failed. Fix the tool error first and call finish_turn again.");
                        AppendToolTurnMessages(structuredTurns, usedNativeCallArray, assistantReply, thinkingReply, activeCalls, toolResults);
                        continue;
                    }

                    bool effectiveClarifyOrAnswerNoOp = false;
                    if (!string.Equals(turnFinish.Kind, "edit", StringComparison.OrdinalIgnoreCase))
                    {
                        if (!workspaceMutated)
                        {
                            effectiveClarifyOrAnswerNoOp = true;
                        }
                        else if (!roundMutatedWorkspace)
                        {
                            (StudioTurnResult probeSnapshot, _) = BuildWorkspaceTurnResult(
                                workspace,
                                firstFinish,
                                turnFinish,
                                capturedSampleDataJson,
                                existingSampleDataJson: existingSampleDataJson,
                                originalManifestJson: currentManifestJson,
                                originalLogicJs: currentLogicJs,
                                originalLocales: currentLocales,
                                effectiveLanguage: effectiveLanguage,
                                preferredEmoji: null,
                                isInitialCreation: false,
                                wasAutoRepaired: wasAutoRepaired,
                                providerId: providerId,
                                effectiveModel: resolved.Model,
                                runCompilerPipeline: false);
                            effectiveClarifyOrAnswerNoOp = IsCandidateNoOp(probeSnapshot);
                        }
                    }

                    // Pure clarify / answer 1-RTT fast exit when workspace was not mutated (or net diff from baseline is zero)
                    if (effectiveClarifyOrAnswerNoOp)
                    {
                        var clarifyOrAnswer = new StudioTurnResult(
                            Success: true,
                            Kind: turnFinish.Kind,
                            Reply: turnFinish.Reply,
                            ChangeSummary: turnFinish.ChangeSummary,
                            BriefUpdates: turnFinish.BriefUpdates,
                            Suggestions: turnFinish.Suggestions,
                            SampleDataJson: capturedSampleDataJson,
                            ManifestJson: string.Empty,
                            ParseResult: null,
                            ErrorMessage: null,
                            ProviderId: providerId,
                            WasAutoRepaired: wasAutoRepaired,
                            EffectiveModel: resolved.Model,
                            ClarifyQuestions: turnFinish.ClarifyQuestions);
                        return CompleteStudioTurn(clarifyOrAnswer);
                    }

                    // Edit turn -> finalize and validate workspace
                    EmitTrace(
                        AiGenerationStage.Validating,
                        thinkingReply,
                        assistantReply,
                        0,
                        0,
                        AiChatTransportClient.TryExtractStreamingWidgetName(assistantReply));

                    if (!workspace.HasManifest)
                    {
                        if (llmCallsUsed < MaxTotalLlmRounds)
                        {
                            UpdateToolResultContent(toolResults, finishCallId,
                                "finish_turn(kind='edit') rejected: no manifest.json in workspace yet. Call write_bundle or edit_manifest first.");
                            AppendToolTurnMessages(structuredTurns, usedNativeCallArray, assistantReply, thinkingReply, activeCalls, toolResults);
                            continue;
                        }
                        break;
                    }

                    (TwoTierGateEvaluation gate, StudioTurnResult candidateTurn) =
                        await ValidateCurrentWorkspaceAsync(firstFinish, turnFinish);
                    bool candidateNoOp = TrackGateCandidate(gate, candidateTurn);

                    if (gate.IsClean)
                    {
                        if (candidateNoOp && !retriedNoOpEdit && llmCallsUsed < MaxTotalLlmRounds)
                        {
                            retriedNoOpEdit = true;
                            UpsertStep($"noop-{llmCallsUsed}", StudioStreamStepState.Warning, TranslateStep("CustomWidget.Workshop.Stream.StepRepairing"));
                            EmitTrace(AiGenerationStage.AutoRepairing, thinkingReply, assistantReply, 0, 0, null);
                            UpdateToolResultContent(
                                toolResults,
                                finishCallId,
                                "finish_turn REJECTED — no-op edit: your tool calls produced zero net change to manifest.json and logic.js compared to the current widget. Apply the requested concrete change using edit_manifest / patch_script first, or call finish_turn(kind='answer') if no edit is needed.");
                            AppendToolTurnMessages(structuredTurns, usedNativeCallArray, assistantReply, thinkingReply, activeCalls, toolResults);
                            continue;
                        }

                        return CompleteCleanGateTurn(candidateTurn, candidateNoOp);
                    }

                    if (!CanConsumeRepair(gate, out bool consumesSoft))
                    {
                        break;
                    }

                    RecordGateRepair(consumesSoft);
                    string localizedFeedback = BuildToolGateRepairFeedback(
                        candidateTurn,
                        gate,
                        currentLogicJs,
                        isChinese,
                        "Fix only the failing manifest element(s) or script line(s) using edit_manifest (upsertElements/removeElements/patchRoot) / patch_script, then call finish_turn again.");

                    UpdateToolResultContent(
                        toolResults,
                        finishCallId,
                        $"finish_turn REJECTED — validation/pre-flight failed:\n{localizedFeedback}");

                    EmitRepairStepTrace(candidateTurn.ManifestJson);
                    AppendToolTurnMessages(structuredTurns, usedNativeCallArray, assistantReply, thinkingReply, activeCalls, toolResults);
                    continue;
                }

                // [P0-3] Multi-round tool protocol: when the workspace was mutated WITHOUT finish_turn in this round,
                // never burn a repair round on intermediate state (e.g. MissingLogicJs before patch_script),
                // while still auto-finalizing a self-contained clean write_bundle and caching valid candidates for stream-drop recovery.
                if (roundMutatedWorkspace && !roundHadToolError && !roundSalvagedWriteBundle)
                {
                    bool canFastCompleteWriteBundle =
                        workspace.HasManifest &&
                        roundAppliedWriteBundle &&
                        !roundHadDiscoveryCall &&
                        continuationRoundsUsed == 0 &&
                        !(roundWriteBundleDeclaredScriptWithoutLogic && !roundAppliedPatchScript) &&
                        !(roundWriteBundleOmittedLogicOnScriptedEdit && !roundAppliedPatchScript);
                    if (canFastCompleteWriteBundle)
                    {
                        (TwoTierGateEvaluation wbGate, StudioTurnResult wbCandidate) =
                            await ValidateCurrentWorkspaceAsync(firstFinish, latestFinish);
                        bool wbNoOp = TrackGateCandidate(wbGate, wbCandidate, onlyTrackAttemptWhenNonBlocking: true);
                        if (wbGate.IsClean && !wbNoOp)
                        {
                            return CompleteCleanGateTurn(wbCandidate, isNoOp: false);
                        }
                    }

                    if (llmCallsUsed < MaxTotalLlmRounds)
                    {
                        const string pendingFinishResponse =
                            "{ \"ok\": true, \"pendingFinishTurn\": true, \"hint\": \"Workspace updated. Call patch_script/edit_manifest if needed, then call finish_turn to validate and complete.\" }";
                        int mutIdx = lastMutationResultIdx >= 0 ? lastMutationResultIdx : toolResults.Count - 1;
                        if (mutIdx >= 0)
                        {
                            var mutRes = toolResults[mutIdx];
                            toolResults[mutIdx] = (mutRes.Id, mutRes.Name, pendingFinishResponse, false);
                        }
                    }
                    else if (workspace.HasManifest)
                    {
                        // Final round at MaxTotalLlmRounds exhaustion -> run terminal gate validation + HTTP preflight once.
                        EmitTrace(
                            AiGenerationStage.Validating,
                            thinkingReply,
                            assistantReply,
                            0,
                            0,
                            AiChatTransportClient.TryExtractStreamingWidgetName(assistantReply));

                        (TwoTierGateEvaluation finalGate, StudioTurnResult finalCandidate) =
                            await ValidateCurrentWorkspaceAsync(firstFinish, latestFinish);
                        bool finalNoOp = TrackGateCandidate(finalGate, finalCandidate);
                        if (finalGate.IsClean)
                        {
                            return CompleteCleanGateTurn(finalCandidate, finalNoOp);
                        }

                        break;
                    }
                }

                if (roundHadDiscoveryCall && !roundMutatedWorkspace)
                {
                    discoveryRoundsUsed++;
                    toolRoundsUsed++;
                    if (discoveryRoundsUsed >= MaxDiscoveryRounds && toolResults.Count > 0)
                    {
                        var lastRes = toolResults[^1];
                        toolResults[^1] = (
                            lastRes.Id,
                            lastRes.Name,
                            $"{lastRes.Content}\n\n[System Notice: Discovery budget reached. Discovery tools are now disabled for this turn — you MUST immediately call write_bundle (or edit_manifest/patch_script) and finish_turn.]",
                            lastRes.TruncatedArgs);
                    }
                }
                else if (hadNonFinishMutationOrRead)
                {
                    toolRoundsUsed++;
                }

                if ((roundHadDiscoveryCall || roundMutatedWorkspace || roundHadToolError || hadNonFinishMutationOrRead) &&
                    llmCallsUsed < MaxTotalLlmRounds)
                {
                    AppendToolTurnMessages(structuredTurns, usedNativeCallArray, assistantReply, thinkingReply, activeCalls, toolResults);
                    continue;
                }

                break;
            }

            // 2. Model finished round with toolCalls.Count == 0 (finish_reason == "stop" or fenced-block fallback)
            EmitTrace(
                AiGenerationStage.Validating,
                thinkingReply,
                assistantReply,
                0,
                0,
                AiChatTransportClient.TryExtractStreamingWidgetName(assistantReply));

            StudioTurnResult parsedTurn = StudioTurnResponseParser.ParseStudioTurnResponse(
                assistantReply,
                preferredEmoji: isInitialTurn ? preferredEmoji : null,
                providerId: providerId,
                effectiveModel: resolved.Model,
                isInitialCreation: isInitialTurn,
                currentLogicJs: workspace.LogicJs,
                currentLocales: workspace.Locales,
                fallbackCulture: effectiveLanguage,
                currentManifestJson: workspace.GetManifestJson(indented: false) ?? currentManifestJson,
                runOnInitDryRun: false);

            // Terminal gate validation + HTTP preflight when prior tool round(s) mutated workspace and model stopped with 0 tool calls
            if (!isTruncated &&
                workspaceMutated &&
                workspace.HasManifest &&
                string.IsNullOrWhiteSpace(parsedTurn.ManifestJson) &&
                parsedTurn.LogicJsPatchMode == StudioLogicJsPatchMode.KeepExisting)
            {
                var stopFinish = new StudioFinishTurnSpec(
                    Kind: "edit",
                    Reply: assistantReply.Trim(),
                    ChangeSummary: string.Empty,
                    DeleteScript: false,
                    SampleDataJson: capturedSampleDataJson,
                    Suggestions: [],
                    BriefUpdates: null);

                (TwoTierGateEvaluation stopGate, StudioTurnResult stopCandidate) =
                    await ValidateCurrentWorkspaceAsync(firstFinish ?? stopFinish, latestFinish ?? stopFinish);
                bool stopNoOp = TrackGateCandidate(stopGate, stopCandidate);

                if (stopGate.IsClean)
                {
                    if (stopNoOp && TryQueueChatNoOpRetry(
                            "No-op edit detected: your workspace mutations produced zero net change compared to the current widget. Apply the requested concrete change using edit_manifest / patch_script and call finish_turn, or call finish_turn(kind='answer') if no edit is needed."))
                    {
                        continue;
                    }

                    return CompleteCleanGateTurn(stopCandidate, stopNoOp);
                }

                if (!CanConsumeRepair(stopGate, out bool consumesSoftStop))
                {
                    break;
                }

                RecordGateRepair(consumesSoftStop);
                string stopRepairFeedback = BuildToolGateRepairFeedback(
                    stopCandidate,
                    stopGate,
                    currentLogicJs,
                    isChinese,
                    "Fix only the failing manifest element(s) or script line(s) using edit_manifest (upsertElements/removeElements/patchRoot) / patch_script, then call finish_turn.");

                structuredTurns.Add(new AiChatTurnMessage("assistant", assistantReply, thinkingReply));
                structuredTurns.Add(new AiChatTurnMessage("user", stopRepairFeedback));
                EmitRepairStepTrace(stopCandidate.ManifestJson);
                continue;
            }

            if (isTruncated &&
                string.Equals(parsedTurn.Kind, "edit", StringComparison.OrdinalIgnoreCase) &&
                !parsedTurn.Success &&
                continuationRoundsUsed < MaxContinuationRounds &&
                llmCallsUsed < MaxTotalLlmRounds)
            {
                continuationRoundsUsed++;
                SalvageCompleteBlocksIntoWorkspace(assistantReply, workspace, ref workspaceMutated);
                structuredTurns.Add(new AiChatTurnMessage("assistant", assistantReply, thinkingReply));
                structuredTurns.Add(new AiChatTurnMessage(
                    "user",
                    BuildTruncatedContinuationPrompt(workspace.HasManifest, !string.IsNullOrWhiteSpace(workspace.LogicJs), isChinese)));
                continue;
            }

            if (!string.Equals(parsedTurn.Kind, "edit", StringComparison.OrdinalIgnoreCase) && !workspaceMutated)
            {
                if (repairRoundsUsed == 0)
                {
                    return CompleteStudioTurn(parsedTurn);
                }

                break;
            }

            // Single-Core Orchestrator: all structured Fenced-Block and JSON-envelope turns were already normalized
            // into standard tool calls (write_bundle / edit_manifest / patch_script / finish_turn) above. Reaching here
            // means the model emitted unparseable or plain non-structured text without tool calls.
            lastAttemptTurn ??= parsedTurn;
            if (llmCallsUsed < MaxTotalLlmRounds && repairRoundsUsed < MaxHardRepairRounds)
            {
                RecordGateRepair(consumesSoft: false);
                structuredTurns.Add(new AiChatTurnMessage("assistant", assistantReply, thinkingReply));
                structuredTurns.Add(new AiChatTurnMessage(
                    "user",
                    isChinese
                        ? "未检测到有效的工具调用或结构化代码块。若需新建或修改组件，请调用 `edit_manifest` / `write_bundle` / `patch_script` + `finish_turn(kind=\"edit\")`；若仅解答问题或说明能力边界，请直接调用 `finish_turn(kind=\"answer\"|\"unsupported\", reply, suggestions?)`。"
                        : "No valid tool call or structured block was detected. Call `edit_manifest` / `write_bundle` / `patch_script` + `finish_turn(kind=\"edit\")` to create or modify the widget, or call `finish_turn(kind=\"answer\"|\"unsupported\", reply, suggestions?)` if no edit is needed."));
                EmitRepairStepTrace(parsedTurn.ManifestJson);
                continue;
            }

            break;
        }

        // If mutations landed in workspace without an explicit final validation pass (e.g., max rounds reached right after edit_manifest), validate once now.
        if (latestStructurallyValidTurn is null && workspaceMutated && !lastMutationRoundHadToolError && workspace.HasManifest)
        {
            (StudioTurnResult residual, _) = BuildWorkspaceTurnResult(
                workspace,
                firstFinish,
                latestFinish,
                capturedSampleDataJson,
                existingSampleDataJson: existingSampleDataJson,
                originalManifestJson: currentManifestJson,
                originalLogicJs: currentLogicJs,
                originalLocales: currentLocales,
                effectiveLanguage: effectiveLanguage,
                preferredEmoji: isInitialTurn ? preferredEmoji : null,
                isInitialCreation: isInitialTurn,
                wasAutoRepaired: wasAutoRepaired || repairRoundsUsed > 0,
                providerId: providerId,
                effectiveModel: resolved.Model);
            bool residualNoOp = IsCandidateNoOp(residual);
            if (!residualNoOp)
            {
                lastAttemptTurn = residual;
            }
            if (residual.Success && residual.ParseResult is { Manifest: not null } && !residual.ParseResult.Diagnostics.Any(static d => d.IsError))
            {
                if (residualNoOp)
                {
                    return CompleteStudioTurn(CreateNoOpResult(residual));
                }

                latestStructurallyValidTurn = residual;
            }
        }

        if (latestStructurallyValidTurn is not null)
        {
            latestStructurallyValidTurn = ApplyExhaustionFallbacks(
                latestStructurallyValidTurn,
                existingSampleDataJson,
                currentLogicJs,
                effectiveLanguage);
            return CompleteStudioTurn(latestStructurallyValidTurn);
        }

        if (lastAttemptTurn is not null)
        {
            IReadOnlyList<CustomWidgetValidationDiagnostic> finalDiags = lastAttemptTurn.ParseResult?.Diagnostics ?? [];
            return CompleteStudioTurn(
                lastAttemptTurn with
                {
                    Success = false,
                    ErrorMessage = finalDiags.FirstOrDefault(d => d.IsError)?.Message ??
                                   (repairRoundsUsed > 0
                                       ? Localized.T("CustomWidget.AiApi.Error.AutoRepairStillInvalid")
                                       : Localized.T("CustomWidget.AiApi.Error.ValidationFailed")),
                    WasAutoRepaired = wasAutoRepaired || repairRoundsUsed > 0
                });
        }

        return CompleteStudioTurn(
            new StudioTurnResult(
                Success: false,
                Kind: "edit",
                Reply: string.Empty,
                ChangeSummary: string.Empty,
                BriefUpdates: null,
                Suggestions: [],
                SampleDataJson: null,
                ManifestJson: string.Empty,
                ParseResult: null,
                ErrorMessage: Localized.T("CustomWidget.AiApi.Error.ValidationFailed"),
                ProviderId: providerId,
                EffectiveModel: resolved.Model));
    }

    private static string CompactOneLine(string? text, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        string singleLine = text.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return CustomWidgetDiagnosticArchive.ScrubSensitiveText(singleLine, maxLength);
    }

    internal static bool IsTruncatedJsonError(string? err)
    {
        if (string.IsNullOrWhiteSpace(err))
        {
            return false;
        }

        return err.Contains("end of data", StringComparison.OrdinalIgnoreCase) ||
               err.Contains("end of the json payload", StringComparison.OrdinalIgnoreCase) ||
               err.Contains("unterminated", StringComparison.OrdinalIgnoreCase) ||
               err.Contains("unexpected end", StringComparison.OrdinalIgnoreCase) ||
               err.Contains("incomplete", StringComparison.OrdinalIgnoreCase) ||
               err.Contains("expected depth", StringComparison.OrdinalIgnoreCase) ||
               err.Contains("expected a value", StringComparison.OrdinalIgnoreCase) ||
               err.Contains("start with a valid json token", StringComparison.OrdinalIgnoreCase) ||
               err.Contains("lineNumber:", StringComparison.OrdinalIgnoreCase) && err.Contains("BytePositionInLine:", StringComparison.OrdinalIgnoreCase) && err.Contains("Expected", StringComparison.OrdinalIgnoreCase);
    }

    internal static void UpdateToolResultContent(
        List<(string Id, string Name, string Content, bool TruncatedArgs)> results,
        string? callId,
        string newContent)
    {
        if (string.IsNullOrEmpty(callId))
        {
            return;
        }

        for (int i = 0; i < results.Count; i++)
        {
            if (string.Equals(results[i].Id, callId, StringComparison.Ordinal))
            {
                results[i] = (results[i].Id, results[i].Name, newContent, false);
                return;
            }
        }
    }

    internal static void AppendToolTurnMessages(
        List<AiChatTurnMessage> structuredTurns,
        bool usedNativeCallArray,
        string assistantReply,
        string thinkingReply,
        IReadOnlyList<AiToolCallSpec> activeCalls,
        List<(string Id, string Name, string Content, bool TruncatedArgs)> toolResults,
        string? extraHint = null)
    {
        if (usedNativeCallArray)
        {
            structuredTurns.Add(new AiChatTurnMessage(
                "assistant",
                string.IsNullOrWhiteSpace(assistantReply) ? null : assistantReply,
                string.IsNullOrWhiteSpace(thinkingReply) ? null : thinkingReply,
                activeCalls));
            foreach (var res in toolResults)
            {
                string content = extraHint is null ? res.Content : $"{res.Content}\n{extraHint}";
                structuredTurns.Add(new AiChatTurnMessage("tool", content, ToolCallId: res.Id, ToolName: res.Name));
            }
            return;
        }

        structuredTurns.Add(new AiChatTurnMessage("assistant", assistantReply, thinkingReply));
        var sb = new StringBuilder();
        foreach (var res in toolResults)
        {
            sb.AppendLine(res.Content);
        }
        if (extraHint is not null)
        {
            sb.AppendLine(extraHint);
        }
        structuredTurns.Add(new AiChatTurnMessage("user", sb.ToString().TrimEnd()));
    }

    internal static void SalvageCompleteBlocksIntoWorkspace(
        string partialVisibleReply,
        StudioWorkspaceEngine workspace,
        ref bool workspaceMutated)
    {
        Dictionary<string, List<string>> blocks = StudioTurnResponseParser.ExtractTaggedFencedBlocks(partialVisibleReply, out _);
        if (blocks.TryGetValue("deskbox-manifest", out List<string>? manifestBlocks) && manifestBlocks.Count > 0)
        {
            string candidate = manifestBlocks[^1].Trim();
            if (workspace.ReplaceManifestJson(candidate).Succeeded)
            {
                workspaceMutated = true;
            }
        }

        if (blocks.TryGetValue("deskbox-script", out List<string>? scriptBlocks) && scriptBlocks.Count > 0)
        {
            string js = scriptBlocks[^1];
            if (!string.IsNullOrWhiteSpace(js) && CustomWidgetScriptEngine.Compile(js).IsSuccess)
            {
                workspace.SetLogicJs(js);
                workspaceMutated = true;
            }
        }
    }

    internal static string BuildTruncatedContinuationPrompt(bool hasManifest, bool hasScript, bool isChinese)
    {
        if (isChinese)
        {
            return $"你的上一条回复在输出中途达到长度上限（finish_reason=length）被截断。工作区现状：manifest={hasManifest}, script={hasScript}。请**不要从头重写已保存部分**，优先用 `edit_manifest` / `patch_script` / `finish_turn`（或仅补全缺失的代码块）接续完成剩余内容。";
        }

        return $"Your previous response was truncated at max_tokens (finish_reason=length). Current workspace state: manifest={hasManifest}, script={hasScript}. Do NOT regenerate completed sections from scratch; continue incrementally using `edit_manifest` / `patch_script` / `finish_turn` (or emit only the missing blocks).";
    }

    private static string ExtractProbeFailureSummary(string probeResultText)
    {
        foreach (string rawLine in probeResultText.Split('\n'))
        {
            string line = rawLine.Trim();
            if (line.StartsWith("- Error:", StringComparison.Ordinal) ||
                line.StartsWith("- Status:", StringComparison.Ordinal))
            {
                return line.TrimStart('-', ' ');
            }
        }

        return "probe_http returned ok=false";
    }

    public static AiThinkingMode? ResolveAdaptiveStudioThinkingOverride(
        ResolvedAiInvocation baseResolved,
        IReadOnlyList<string>? fetchedModels,
        string? userPrompt,
        bool hasExistingManifest,
        string? targetNodePath,
        IReadOnlyCollection<string>? pinnedCapabilityIds,
        bool isSystemRepairPrompt = false,
        IReadOnlyList<StudioChatMessage>? recentMessages = null)
    {
        _ = pinnedCapabilityIds;
        bool canSwapDeepSeek = AiProviderCatalog.SupportsClassicDeepSeekModelSwap(baseResolved.Preset, fetchedModels) &&
                               (string.Equals(baseResolved.Model, "deepseek-chat", StringComparison.OrdinalIgnoreCase) ||
                                string.Equals(baseResolved.Model, "deepseek-reasoner", StringComparison.OrdinalIgnoreCase));
        if (!canSwapDeepSeek && AiProviderCatalog.IsReasoningOnlyModel(baseResolved.Model))
        {
            return null;
        }

        if (isSystemRepairPrompt || baseResolved.ThinkingMode == AiThinkingMode.Disabled)
        {
            return AiThinkingMode.Disabled;
        }

        if (baseResolved.ThinkingMode == AiThinkingMode.Enabled)
        {
            return AiThinkingMode.Enabled;
        }

        return StudioTurnStageRouter.ResolveAdaptiveStudioThinkingMode(
            baseResolved with { ThinkingMode = AiThinkingMode.Auto },
            userPrompt,
            hasExistingManifest,
            targetNodePath,
            recentMessages,
            isPostMutationRound: false,
            thinkingParamsUnsupportedOnRoute: DeskBoxAiService.Shared.IsThinkingParamsUnsupportedRoute(baseResolved),
            thinkingExhaustedOnRoute: DeskBoxAiService.Shared.IsThinkingExhaustedRoute(baseResolved));
    }
}
