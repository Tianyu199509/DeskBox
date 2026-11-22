using System.Text;
using System.Text.Json.Nodes;
using DeskBox.Models;

namespace DeskBox.Services;

internal sealed partial class StudioAiTurnOrchestrator
{
    internal readonly record struct TwoTierGateEvaluation(
        bool IsClean,
        bool HasBlockingFailure,
        bool HasSoftFailure,
        string? PreflightFeedback);

    internal static bool IsStructurallyValidEdit(StudioTurnResult candidate) =>
        candidate.Success &&
        candidate.ParseResult is { Manifest: not null } &&
        !candidate.ParseResult.Diagnostics.Any(static d => d.IsError);

    internal static bool RawManifestDeclaresScript(string? rawManifestJson)
    {
        if (string.IsNullOrWhiteSpace(rawManifestJson))
        {
            return false;
        }

        try
        {
            if (StudioWorkspaceEngine.ParsePermissiveJsonNode(rawManifestJson) is JsonObject rootObj &&
                rootObj.TryGetPropertyValue("script", out JsonNode? scriptNode) &&
                scriptNode is not null)
            {
                if (scriptNode is JsonObject scriptObj)
                {
                    return scriptObj.TryGetPropertyValue("entry", out JsonNode? entryNode)
                        ? entryNode is JsonValue entryVal &&
                          entryVal.TryGetValue<string>(out string? entryStr) &&
                          !string.IsNullOrWhiteSpace(entryStr)
                        : scriptObj.Count > 0;
                }

                if (scriptNode is JsonValue val && val.TryGetValue<string>(out string? scriptStr))
                {
                    return !string.IsNullOrWhiteSpace(scriptStr);
                }
            }
        }
        catch
        {
        }

        return false;
    }

    private static string BuildToolGateRepairFeedback(
        StudioTurnResult candidateTurn,
        TwoTierGateEvaluation gate,
        string? baselineLogicJs,
        bool isChinese,
        string extraSummary)
    {
        string? effectiveTurnJs = StudioTurnResponseParser.ResolveEffectiveLogicJs(
            baselineLogicJs,
            candidateTurn.LogicJsPatchMode,
            candidateTurn.LogicJs);
        string[]? preflightNotes = string.IsNullOrWhiteSpace(gate.PreflightFeedback)
            ? null
            : [gate.PreflightFeedback];
        return StudioWorkspaceEngine.BuildLocalizedRepairFeedback(
            candidateTurn.ParseResult?.Diagnostics ?? [],
            candidateTurn.ManifestJson,
            effectiveTurnJs,
            preFlightNotes: preflightNotes,
            extraSummary: extraSummary,
            isChinese: isChinese);
    }

    internal async Task<(TwoTierGateEvaluation Gate, StudioTurnResult CandidateTurn)>
        TryValidateAndPreflightWorkspaceTurnAsync(
            StudioWorkspaceEngine workspace,
            StudioFinishTurnSpec? firstFinish,
            StudioFinishTurnSpec? latestFinish,
            string? capturedSampleDataJson,
            string? existingSampleDataJson,
            string? originalManifestJson,
            string? originalLogicJs,
            CustomWidgetLocaleCatalog? originalLocales,
            string effectiveLanguage,
            string? preferredEmoji,
            bool isInitialCreation,
            bool wasAutoRepaired,
            string providerId,
            string effectiveModel,
            int repairRoundsUsed,
            CancellationToken cancellationToken,
            IReadOnlyDictionary<string, string>? failedProbeHosts = null)
    {
        (StudioTurnResult candidateTurn, CustomWidgetCompilerResult? compilerResult) = BuildWorkspaceTurnResult(
            workspace,
            firstFinish,
            latestFinish,
            capturedSampleDataJson,
            existingSampleDataJson: existingSampleDataJson,
            originalManifestJson: originalManifestJson,
            originalLogicJs: originalLogicJs,
            originalLocales: originalLocales,
            effectiveLanguage: effectiveLanguage,
            preferredEmoji: preferredEmoji,
            isInitialCreation: isInitialCreation,
            wasAutoRepaired: wasAutoRepaired,
            providerId: providerId,
            effectiveModel: effectiveModel,
            runCompilerPipeline: true);

        bool structurallyValid = IsStructurallyValidEdit(candidateTurn);
        string? effectiveTurnJs = StudioTurnResponseParser.ResolveEffectiveLogicJs(
            originalLogicJs,
            candidateTurn.LogicJsPatchMode,
            candidateTurn.LogicJs);

        if (structurallyValid && compilerResult is not null)
        {
            bool overflowResolved = TryResolveVerticalOverflowDeterministically(
                ref candidateTurn,
                compilerResult,
                effectiveTurnJs);

            string? sampleForSmoke = !string.IsNullOrWhiteSpace(candidateTurn.SampleDataJson)
                ? candidateTurn.SampleDataJson
                : existingSampleDataJson;
            TwoTierGateEvaluation gate = await EvaluateTwoTierGateAsync(
                compilerResult,
                effectiveLanguage,
                cancellationToken,
                effectiveTurnJs,
                failedProbeHosts,
                candidateTurn.Locales,
                sampleForSmoke,
                skipResolvedVerticalOverflow: overflowResolved).ConfigureAwait(false);
            App.Log(
                $"[StudioAI] Validate mode=studio kind={candidateTurn.Kind} repair={repairRoundsUsed} structural=true manifestId={candidateTurn.ParseResult?.Manifest?.Id ?? "(none)"} diags={candidateTurn.ParseResult?.Diagnostics.Count ?? 0} preflightChars={gate.PreflightFeedback?.Length ?? 0}");
            return (gate, candidateTurn);
        }

        App.Log(
            $"[StudioAI] Validate mode=studio kind={candidateTurn.Kind} repair={repairRoundsUsed} structural=false manifestId={candidateTurn.ParseResult?.Manifest?.Id ?? "(none)"} diags={candidateTurn.ParseResult?.Diagnostics.Count ?? 0} preflightChars=0");
        return (new TwoTierGateEvaluation(IsClean: false, HasBlockingFailure: true, HasSoftFailure: false, PreflightFeedback: null), candidateTurn);
    }

    private static bool TryResolveVerticalOverflowDeterministically(
        ref StudioTurnResult turn,
        CustomWidgetCompilerResult compiler,
        string? effectiveLogicJs)
    {
        if (!compiler.ActionableSoftDiagnostics.Any(static d =>
                string.Equals(d.Code, "layout.vertical_overflow", StringComparison.Ordinal)))
        {
            return false;
        }

        if (turn.ParseResult?.Manifest is { } initialManifest)
        {
            if (CustomWidgetHealthAnalyzer.HasRootFlexibleListOrScroll(initialManifest.View))
            {
                return true;
            }

            CustomWidgetHealthAnalyzer.EstimateManifestLayoutHeight(initialManifest, out int initialOverflowPx, out _);
            if (initialOverflowPx <= 0)
            {
                return true;
            }
        }

        StudioTurnResult calibrated = ApplyDeterministicGeometryFallback(turn, compiler, effectiveLogicJs);
        if (calibrated.ParseResult?.Manifest is { } calibratedManifest)
        {
            if (CustomWidgetHealthAnalyzer.HasRootFlexibleListOrScroll(calibratedManifest.View))
            {
                turn = calibrated;
                return true;
            }

            CustomWidgetHealthAnalyzer.EstimateManifestLayoutHeight(calibratedManifest, out int postOverflowPx, out _);
            if (postOverflowPx <= 0)
            {
                turn = calibrated;
                return true;
            }
        }

        return false;
    }

    internal async Task<TwoTierGateEvaluation> EvaluateTwoTierGateAsync(
        CustomWidgetCompilerResult compiler,
        string effectiveLanguage,
        CancellationToken ct,
        string? logicJs = null,
        IReadOnlyDictionary<string, string>? failedProbeHosts = null,
        CustomWidgetLocaleCatalog? locales = null,
        string? sampleDataJson = null,
        bool skipResolvedVerticalOverflow = false)
    {
        if (compiler.HasHardErrors || compiler.Manifest is null)
        {
            return new TwoTierGateEvaluation(
                IsClean: false,
                HasBlockingFailure: true,
                HasSoftFailure: false,
                PreflightFeedback: null);
        }

        IReadOnlyList<CustomWidgetValidationDiagnostic> smokeErrors = StudioBehavioralSmokeVerifier.Verify(
            compiler.Manifest,
            compiler.NormalizedManifestJson,
            logicJs,
            locales,
            sampleDataJson,
            effectiveLanguage);

        (string? blockingProbe, string? softProbe) = await _preFlightProbe
            .BuildNetworkProbeFeedbackAsync(compiler.Manifest, ct, logicJs, failedProbeHosts)
            .ConfigureAwait(false);
        bool hasBlocking = !string.IsNullOrWhiteSpace(blockingProbe) || smokeErrors.Count > 0;

        var notes = new StringBuilder();
        foreach (CustomWidgetValidationDiagnostic smoke in smokeErrors)
        {
            notes.Append("- [").Append(smoke.Code ?? "BehavioralCheck").Append("] ")
                 .Append(smoke.Path).Append(": ").AppendLine(smoke.Message);
        }
        if (!string.IsNullOrWhiteSpace(blockingProbe))
        {
            notes.AppendLine(blockingProbe);
        }

        int emittedSoftCount = 0;
        foreach (CustomWidgetCompilerDiagnostic soft in compiler.ActionableSoftDiagnostics)
        {
            if (skipResolvedVerticalOverflow &&
                string.Equals(soft.Code, "layout.vertical_overflow", StringComparison.Ordinal))
            {
                continue;
            }

            notes.Append("- [").Append(soft.Code).Append("] ").Append(soft.Message);
            if (!string.IsNullOrWhiteSpace(soft.SuggestedPrompt))
            {
                notes.Append(" -> ").Append(soft.SuggestedPrompt);
            }
            notes.AppendLine();

            if (++emittedSoftCount >= 4)
            {
                break;
            }
        }

        if (!string.IsNullOrWhiteSpace(softProbe))
        {
            notes.AppendLine(softProbe);
        }

        string? combinedFeedback = notes.Length > 0 ? notes.ToString().TrimEnd() : null;
        bool hasSoft = !hasBlocking && !string.IsNullOrWhiteSpace(combinedFeedback);
        bool isClean = !hasBlocking && !hasSoft;

        return new TwoTierGateEvaluation(isClean, hasBlocking, hasSoft, combinedFeedback);
    }

    internal static (StudioTurnResult Turn, CustomWidgetCompilerResult? Compiler) BuildWorkspaceTurnResult(
        StudioWorkspaceEngine workspace,
        StudioFinishTurnSpec? firstFinish,
        StudioFinishTurnSpec? latestFinish,
        string? capturedSampleDataJson,
        string? existingSampleDataJson,
        string? originalManifestJson,
        string? originalLogicJs,
        CustomWidgetLocaleCatalog? originalLocales,
        string effectiveLanguage,
        string? preferredEmoji,
        bool isInitialCreation,
        bool wasAutoRepaired,
        string providerId,
        string effectiveModel,
        bool runCompilerPipeline = true)
    {
        StudioWorkspaceEngine candidateWorkspace = workspace.CloneForCandidateCompilation();
        candidateWorkspace.NormalizeDomInPlace();
        string manifestJson = candidateWorkspace.GetManifestJson(indented: true) ?? string.Empty;
        string? effectiveLogicJs = candidateWorkspace.LogicJs;

        manifestJson = WidgetAiI18nTranslationService.ExtractInlineManifestLocalesAndStrip(manifestJson, candidateWorkspace.Locales, out CustomWidgetLocaleCatalog? inlineLocales);
        CustomWidgetLocaleCatalog? effectiveLocales = WidgetAiI18nTranslationService.MergeLocaleCatalogs(originalLocales, candidateWorkspace.Locales ?? inlineLocales);
        manifestJson = CustomWidgetTemplates.SyncManifestScriptPropertyJson(manifestJson, effectiveLogicJs);
        manifestJson = StudioTurnResponseParser.PreservePriorLineageMetadataIfOmitted(originalManifestJson, manifestJson);
        manifestJson = StudioTurnResponseParser.ApplyEmojiAndMinVersion(manifestJson, preferredEmoji, isInitialCreation);
        manifestJson = CustomWidgetI18nEngine.AutoCompileIfLocalized(
            manifestJson,
            effectiveLogicJs,
            effectiveLocales,
            effectiveLanguage,
            out effectiveLocales);
        effectiveLocales = WidgetAiI18nTranslationService.OverlayFreshTurnTranslations(effectiveLocales, inlineLocales);
        if (isInitialCreation && (originalLocales is null || originalLocales.IsEmpty))
        {
            effectiveLocales = PruneIncompleteInitialSecondaryLocales(effectiveLocales);
        }

        manifestJson = StudioTurnResponseParser.CalibrateDefaultHeightOnOverflowJson(
            manifestJson,
            isInitialCreation ? null : originalManifestJson);

        string? effectiveCapturedSample = !string.IsNullOrWhiteSpace(capturedSampleDataJson)
            ? capturedSampleDataJson
            : candidateWorkspace.ExtractedInlineSampleDataJson;

        string? sampleForValidation = !string.IsNullOrWhiteSpace(effectiveCapturedSample)
            ? effectiveCapturedSample
            : existingSampleDataJson;

        CustomWidgetCompilerResult? compilerResult = null;
        CustomWidgetParseResult parseRes;
        if (runCompilerPipeline)
        {
            compilerResult = CustomWidgetCompilerPipeline.Compile(
                manifestJson,
                effectiveLogicJs,
                effectiveLocales,
                sampleDataJson: sampleForValidation,
                cultureName: effectiveLanguage,
                runOnInitDryRun: true,
                normalizeDom: false,
                autoWireCapabilities: true,
                targetNodePath: candidateWorkspace.LockedCanonicalPath);
            manifestJson = compilerResult.NormalizedManifestJson;
            parseRes = compilerResult.ValidationResult.ToParseResult();
        }
        else
        {
            parseRes = CustomWidgetScriptValidator.ValidateWorkspaceBundle(
                manifestJson,
                effectiveLogicJs,
                effectiveLocales,
                runOnInitDryRun: true,
                sampleDataJson: sampleForValidation,
                strictAuthoring: true);
        }

        bool isOk = parseRes.Manifest is not null && !parseRes.Diagnostics.Any(static d => d.IsError);

        // effectiveLogicJs is newline-normalized by the workspace; normalize the baseline
        // too so a CRLF current script is not misreported as Replace (same fix as 7d58f45c).
        string? normalizedOriginalLogicJs = string.IsNullOrWhiteSpace(originalLogicJs)
            ? null
            : StudioWorkspaceEngine.NormalizeNewlines(originalLogicJs);
        StudioLogicJsPatchMode logicMode = effectiveLogicJs is null
            ? (normalizedOriginalLogicJs is null
                ? StudioLogicJsPatchMode.KeepExisting
                : StudioLogicJsPatchMode.Remove)
            : (string.Equals(effectiveLogicJs, normalizedOriginalLogicJs, StringComparison.Ordinal)
                ? StudioLogicJsPatchMode.KeepExisting
                : StudioLogicJsPatchMode.Replace);

        if (parseRes.Manifest is not null && effectiveLocales is { HasAnyLocale: true })
        {
            parseRes = parseRes with { Manifest = parseRes.Manifest.WithLocales(effectiveLocales) };
        }

        string widgetTitle = ResolveHumanWidgetTitle(parseRes.Manifest, effectiveLocales, effectiveLanguage);
        IReadOnlyList<string> finePills = isInitialCreation
            ? []
            : candidateWorkspace.BuildFineGrainedDiffPills(effectiveLanguage);

        (string autoReplyFallback, string autoSummaryFallback) = candidateWorkspace.BuildAutoFinalizedTurnCopy(
            widgetTitle,
            isInitialTurn: isInitialCreation,
            effectiveLanguage);
        string rawReply = !string.IsNullOrWhiteSpace(latestFinish?.Reply)
            ? latestFinish!.Reply
            : (!string.IsNullOrWhiteSpace(firstFinish?.Reply) ? firstFinish!.Reply : autoReplyFallback);
        string rawSummary = !string.IsNullOrWhiteSpace(latestFinish?.ChangeSummary)
            ? latestFinish!.ChangeSummary
            : (!string.IsNullOrWhiteSpace(firstFinish?.ChangeSummary)
                ? firstFinish!.ChangeSummary
                : (!isInitialCreation ? autoSummaryFallback : widgetTitle));
        string reply = ResolveResidualI18nTokens(rawReply, parseRes.Manifest, effectiveLocales, effectiveLanguage);
        string changeSummary = ResolveResidualI18nTokens(rawSummary, parseRes.Manifest, effectiveLocales, effectiveLanguage);
        string? rawLimitationNote = !string.IsNullOrWhiteSpace(latestFinish?.LimitationNote)
            ? latestFinish!.LimitationNote
            : firstFinish?.LimitationNote;
        string? limitationNote = !string.IsNullOrWhiteSpace(rawLimitationNote)
            ? ResolveResidualI18nTokens(rawLimitationNote!, parseRes.Manifest, effectiveLocales, effectiveLanguage)
            : null;
        StudioBriefUpdateSpec? briefUpdates = latestFinish?.BriefUpdates ?? firstFinish?.BriefUpdates;
        IReadOnlyList<string> suggestions = latestFinish?.Suggestions is { Count: > 0 }
            ? latestFinish.Suggestions
            : (firstFinish?.Suggestions ?? []);

        var resultTurn = new StudioTurnResult(
            Success: isOk,
            Kind: "edit",
            Reply: reply,
            ChangeSummary: changeSummary,
            BriefUpdates: briefUpdates,
            Suggestions: suggestions,
            SampleDataJson: effectiveCapturedSample,
            ManifestJson: manifestJson,
            ParseResult: parseRes,
            ErrorMessage: isOk ? null : parseRes.Diagnostics.FirstOrDefault(static d => d.IsError)?.Message,
            ProviderId: providerId,
            WasAutoRepaired: wasAutoRepaired,
            EffectiveModel: effectiveModel,
            LogicJsPatchMode: logicMode,
            LogicJs: logicMode == StudioLogicJsPatchMode.Replace ? effectiveLogicJs : null,
            Locales: effectiveLocales,
            FineGrainedDiffPills: finePills,
            LimitationNote: limitationNote);
        return (resultTurn, compilerResult);
    }

    internal static StudioTurnResult ApplyExhaustionFallbacks(
        StudioTurnResult turn,
        string? existingSampleDataJson,
        string? originalLogicJs,
        string effectiveLanguage)
    {
        if (string.IsNullOrWhiteSpace(turn.ManifestJson))
        {
            return turn;
        }

        string? effJs = StudioTurnResponseParser.ResolveEffectiveLogicJs(originalLogicJs, turn.LogicJsPatchMode, turn.LogicJs);
        string? effSample = !string.IsNullOrWhiteSpace(turn.SampleDataJson)
            ? turn.SampleDataJson
            : existingSampleDataJson;

        CustomWidgetCompilerResult compiler = CustomWidgetCompilerPipeline.Compile(
            turn.ManifestJson,
            effJs,
            turn.Locales,
            sampleDataJson: effSample,
            cultureName: effectiveLanguage,
            runOnInitDryRun: true,
            normalizeDom: false,
            autoWireCapabilities: false);

        StudioTurnResult updated = turn;
        if (!compiler.HasHardErrors &&
            !string.IsNullOrWhiteSpace(turn.SampleDataJson) &&
            compiler.SoftDiagnostics.Any(static d => string.Equals(d.Code, "ScriptSampleDataRuntimeError", StringComparison.Ordinal)))
        {
            updated = updated with { SampleDataJson = null };
        }

        if (compiler.Manifest is not null &&
            compiler.SoftDiagnostics.Any(static d =>
                string.Equals(d.Code, "layout.vertical_overflow", StringComparison.Ordinal) ||
                string.Equals(d.Code, "layout.cjk_column_squeeze", StringComparison.Ordinal)))
        {
            updated = ApplyDeterministicGeometryFallback(updated, compiler, effJs);
        }

        return updated;
    }

    internal static StudioTurnResult ApplyDeterministicGeometryFallback(
        StudioTurnResult turn,
        CustomWidgetCompilerResult compiler,
        string? effJs)
    {
        if (compiler.Manifest is null)
        {
            return turn;
        }

        try
        {
            if (StudioWorkspaceEngine.ParsePermissiveJsonNode(turn.ManifestJson) is not JsonObject rootObj)
            {
                return turn;
            }

            bool hasCjkSqueeze = compiler.SoftDiagnostics.Any(static d =>
                string.Equals(d.Code, "layout.cjk_column_squeeze", StringComparison.Ordinal));
            bool hasVerticalOverflow = compiler.SoftDiagnostics.Any(static d =>
                string.Equals(d.Code, "layout.vertical_overflow", StringComparison.Ordinal));

            int currentW = compiler.Manifest.DefaultSize.Width;
            int currentH = compiler.Manifest.DefaultSize.Height;
            int nextW = hasCjkSqueeze && currentW < 340 ? 340 : currentW;
            int nextH = currentH;

            if (hasVerticalOverflow)
            {
                double estBody = CustomWidgetHealthAnalyzer.EstimateManifestLayoutHeight(compiler.Manifest, out int overflowPx, out _);
                if (overflowPx > 0)
                {
                    nextH = Math.Clamp((int)Math.Ceiling((estBody + 44) / 10.0) * 10, currentH, 520);
                }
            }

            if (nextW != currentW || nextH != currentH)
            {
                if (rootObj["defaultSize"] is not JsonObject sizeObj)
                {
                    sizeObj = new JsonObject();
                    rootObj["defaultSize"] = sizeObj;
                }
                sizeObj["width"] = nextW;
                sizeObj["height"] = nextH;
            }

            string stepAJson = StudioTurnResponseParser.SerializeJsonNodeIndented(rootObj);
            CustomWidgetWorkspaceValidationResult stepAVal = CustomWidgetScriptValidator.ValidateWorkspaceBundle(
                stepAJson,
                effJs,
                turn.Locales,
                runOnInitDryRun: true,
                sampleDataJson: null);

            if (stepAVal.Manifest is { } stepAManifest &&
                hasVerticalOverflow &&
                !CustomWidgetHealthAnalyzer.HasRootFlexibleListOrScroll(stepAManifest.View))
            {
                CustomWidgetHealthAnalyzer.EstimateManifestLayoutHeight(stepAManifest, out int remainingOverflow, out _);
                if (remainingOverflow > 24 &&
                    StudioWorkspaceEngine.ParsePermissiveJsonNode(stepAJson) is JsonObject stepBObj &&
                    stepBObj["root"] is JsonValue rootIdVal &&
                    rootIdVal.TryGetValue<string>(out string? rootElemId) &&
                    !string.IsNullOrWhiteSpace(rootElemId) &&
                    stepBObj["elements"] is JsonObject elementsMap &&
                    elementsMap[rootElemId.Trim()] is JsonObject viewObj)
                {
                    string rootType = viewObj["type"]?.GetValue<string>() ?? "Stack";
                    string orientation = viewObj["orientation"]?.GetValue<string>() ?? "vertical";
                    bool isVerticalStack = (string.Equals(rootType, "Stack", StringComparison.OrdinalIgnoreCase) ||
                                            string.Equals(rootType, "Column", StringComparison.OrdinalIgnoreCase) ||
                                            string.Equals(rootType, "ModuleStack", StringComparison.OrdinalIgnoreCase)) &&
                                           !string.Equals(orientation, "horizontal", StringComparison.OrdinalIgnoreCase);

                    if (isVerticalStack && viewObj["children"] is JsonArray oldChildren && oldChildren.Count > 0)
                    {
                        string? footerElemId = null;
                        int scrollChildCount = oldChildren.Count;
                        if (oldChildren.Count >= 2 &&
                            oldChildren[^1] is JsonValue lastIdVal &&
                            lastIdVal.TryGetValue<string>(out string? lastId) &&
                            !string.IsNullOrWhiteSpace(lastId) &&
                            elementsMap[lastId.Trim()] is JsonObject lastObj)
                        {
                            string lastType = lastObj["type"]?.GetValue<string>() ?? string.Empty;
                            bool isActionRow = string.Equals(lastType, "Button", StringComparison.OrdinalIgnoreCase) ||
                                               string.Equals(lastType, "Row", StringComparison.OrdinalIgnoreCase) ||
                                               (string.Equals(lastType, "Stack", StringComparison.OrdinalIgnoreCase) &&
                                                string.Equals(lastObj["orientation"]?.GetValue<string>(), "horizontal", StringComparison.OrdinalIgnoreCase));
                            if (isActionRow)
                            {
                                footerElemId = lastId.Trim();
                                lastObj["row"] = 1;
                                scrollChildCount = oldChildren.Count - 1;
                            }
                        }

                        var scrollChildren = new JsonArray();
                        for (int i = 0; i < scrollChildCount; i++)
                        {
                            if (oldChildren[i] is not null)
                            {
                                scrollChildren.Add(oldChildren[i]!.DeepClone());
                            }
                        }

                        string autoScrollId = "auto_scroll_body";
                        int suffix = 1;
                        while (elementsMap.ContainsKey(autoScrollId))
                        {
                            autoScrollId = $"auto_scroll_body_{++suffix}";
                        }

                        elementsMap[autoScrollId] = new JsonObject
                        {
                            ["type"] = "Scroll",
                            ["row"] = 0,
                            ["spacing"] = viewObj["spacing"]?.DeepClone() ?? 8,
                            ["children"] = scrollChildren
                        };

                        var gridChildren = new JsonArray((JsonNode?)JsonValue.Create(autoScrollId));
                        if (footerElemId is not null)
                        {
                            gridChildren.Add((JsonNode?)JsonValue.Create(footerElemId));
                        }

                        viewObj["type"] = "Grid";
                        viewObj["rows"] = footerElemId is not null ? "*,Auto" : "*";
                        viewObj.Remove("orientation");
                        viewObj["children"] = gridChildren;

                        string stepBJson = StudioTurnResponseParser.SerializeJsonNodeIndented(stepBObj);
                        CustomWidgetWorkspaceValidationResult stepBVal = CustomWidgetScriptValidator.ValidateWorkspaceBundle(
                            stepBJson,
                            effJs,
                            turn.Locales,
                            runOnInitDryRun: true,
                            sampleDataJson: null);
                        if (stepBVal.IsValid && stepBVal.Manifest is not null)
                        {
                            return turn with
                            {
                                ManifestJson = stepBJson,
                                ParseResult = stepBVal.ToParseResult(),
                                WasAutoRepaired = true
                            };
                        }
                    }
                }
            }

            if (stepAVal.IsValid && stepAVal.Manifest is not null)
            {
                return turn with
                {
                    ManifestJson = stepAJson,
                    ParseResult = stepAVal.ToParseResult(),
                    WasAutoRepaired = true
                };
            }
        }
        catch
        {
        }

        return turn;
    }

    internal static bool IsUnchangedFromBaseline(
        StudioTurnResult candidate,
        JsonNode? baselineCanonicalNode,
        string? baselineManifestRaw,
        string? baselineLogicJs,
        string baselineLocaleSha)
    {
        string? effectiveCandidateLogicJs = StudioTurnResponseParser.ResolveEffectiveLogicJs(
            baselineLogicJs,
            candidate.LogicJsPatchMode,
            candidate.LogicJs);
        string normBaseJs = (baselineLogicJs ?? string.Empty).Replace("\r\n", "\n", StringComparison.Ordinal).Trim();
        string normCandJs = (effectiveCandidateLogicJs ?? string.Empty).Replace("\r\n", "\n", StringComparison.Ordinal).Trim();
        if (!string.Equals(normBaseJs, normCandJs, StringComparison.Ordinal))
        {
            return false;
        }

        string candLocaleSha = candidate.Locales is { HasAnyLocale: true } candLoc
            ? candLoc.ComputeSha256()
            : string.Empty;
        if (!string.Equals(baselineLocaleSha, candLocaleSha, StringComparison.Ordinal))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(candidate.ManifestJson))
        {
            return true;
        }

        try
        {
            var candNode = StudioWorkspaceEngine.ParsePermissiveJsonNode(candidate.ManifestJson);
            if (baselineCanonicalNode is not null && candNode is not null)
            {
                return JsonNode.DeepEquals(baselineCanonicalNode, candNode);
            }
        }
        catch
        {
        }

        return string.Equals(baselineManifestRaw?.Trim(), candidate.ManifestJson.Trim(), StringComparison.Ordinal);
    }

    internal static string ResolveHumanWidgetTitle(
        CustomWidgetManifest? manifest,
        CustomWidgetLocaleCatalog? locales,
        string? cultureName)
    {
        if (manifest is null)
        {
            return "Widget";
        }

        CustomWidgetManifest enriched = locales is { HasAnyLocale: true }
            ? manifest.WithLocales(locales)
            : manifest;
        string localized = enriched.ResolveLocalizedName(cultureName);
        if (!string.IsNullOrWhiteSpace(localized) && !localized.Contains("${", StringComparison.Ordinal))
        {
            return localized.Trim();
        }

        if (!string.IsNullOrWhiteSpace(manifest.Name) && !manifest.Name.Contains("${", StringComparison.Ordinal))
        {
            return manifest.Name.Trim();
        }

        return "Widget";
    }

    internal static string ResolveResidualI18nTokens(
        string? text,
        CustomWidgetManifest? manifest,
        CustomWidgetLocaleCatalog? locales,
        string? cultureName)
    {
        if (string.IsNullOrWhiteSpace(text) || !text.Contains("${", StringComparison.Ordinal) || manifest is null)
        {
            return text ?? string.Empty;
        }

        CustomWidgetManifest enriched = locales is { HasAnyLocale: true }
            ? manifest.WithLocales(locales)
            : manifest;
        string interpolated = enriched.ResolveLocalizedText(text, cultureName);
        return !string.IsNullOrWhiteSpace(interpolated) ? interpolated : text;
    }

    internal static CustomWidgetLocaleCatalog? PruneIncompleteInitialSecondaryLocales(CustomWidgetLocaleCatalog? catalog)
    {
        if (catalog is null || catalog.Locales.Count <= 1)
        {
            return catalog;
        }

        string? baseLocale = catalog.Meta?.BaseLocale;
        if (string.IsNullOrWhiteSpace(baseLocale) ||
            !catalog.Locales.TryGetValue(baseLocale, out IReadOnlyDictionary<string, string>? baseEntries) ||
            baseEntries.Count == 0)
        {
            return catalog;
        }

        Dictionary<string, IReadOnlyDictionary<string, string>>? filtered = null;
        foreach ((string code, IReadOnlyDictionary<string, string> entries) in catalog.Locales)
        {
            bool isBase = string.Equals(code, baseLocale, StringComparison.OrdinalIgnoreCase);
            bool complete = isBase || baseEntries.Keys.All(entries.ContainsKey);
            if (!complete)
            {
                filtered ??= new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
                continue;
            }
        }

        if (filtered is null)
        {
            return catalog;
        }

        foreach ((string code, IReadOnlyDictionary<string, string> entries) in catalog.Locales)
        {
            if (string.Equals(code, baseLocale, StringComparison.OrdinalIgnoreCase) ||
                baseEntries.Keys.All(entries.ContainsKey))
            {
                filtered[code] = entries;
            }
        }

        return new CustomWidgetLocaleCatalog(catalog.Meta, filtered);
    }
}
