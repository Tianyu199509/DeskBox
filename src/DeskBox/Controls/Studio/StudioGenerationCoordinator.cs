using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DeskBox.Contracts;
using DeskBox.Controls.WidgetContents;
using DeskBox.FileSafety;
using DeskBox.Helpers;
using DeskBox.Models;
using DeskBox.Platform;
using DeskBox.Services;
using DeskBox.Services.Studio;
using DeskBox.ViewModels;
using DeskBox.Views;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage.Streams;
using Windows.System;
using Windows.UI.Core;
using ToolkitControls = CommunityToolkit.WinUI.Controls;

namespace DeskBox.Controls.Studio;

internal sealed class StudioGenerationCoordinator : IDisposable
{
    private readonly CustomWidgetStudioWindow _shell;
    private LocalizationService _loc => _shell.Localization;
    private IStudioHostBridge _hostBridge => _shell.HostBridge;
    private CustomWidgetStudioPreferencesStore _prefs => _shell.Preferences;
    private CustomWidgetStudioSessionStore _sessionStore => _shell.SessionStore;
    private StudioSessionController _session => _shell.Session;

    internal StudioGenerationCoordinator(CustomWidgetStudioWindow shell)
    {
        _shell = shell;
    }
    private CancellationTokenSource? _generationCts;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _waitingElapsedTimer;

    internal CancellationTokenSource? GenerationCts { get => _generationCts; set => _generationCts = value; }

    /// <summary>
    /// Applies an AI reply (from the clipboard or external code tool).
    /// Supports bare <c>manifest.json</c>, dual-block <c>manifest.json</c> + <c>logic.js</c>, and co-creation envelopes.
    /// </summary>
    internal bool HandleManifestCandidate(
        string rawJson,
        string? prompt,
        string? fallbackLabel,
        string source = "ai",
        string? fencedLogicJs = null,
        CustomWidgetLocaleCatalog? externalLocales = null,
        bool hasExplicitExternalFiles = false,
        bool? hasExplicitLogicJsOverride = null,
        bool? hasExplicitLocalesOverride = null)
    {
        bool explicitLogic = hasExplicitLogicJsOverride ?? hasExplicitExternalFiles;
        bool explicitLocales = hasExplicitLocalesOverride ?? hasExplicitExternalFiles;
        string? prevJson = _session.CurrentManifestJson;
        string? prevLogicJs = _session.CurrentLogicJs;
        CustomWidgetLocaleCatalog? prevLocales = _session.CurrentLocales;
        string? targetNodePath = _session.SelectedTargetNode?.NodePath;
        string? targetNodeLabel = _session.InFlightTargetNodeLabel ?? StudioManifestInspector.FormatPickedNodeDisplayLabel(_session.SelectedTargetNode, _loc, prevJson);

        string combinedInput = !string.IsNullOrWhiteSpace(fencedLogicJs) && !explicitLogic
            ? $"```json\n{rawJson}\n```\n```js\n{fencedLogicJs}\n```"
            : rawJson;

        StudioTurnResult parsedEnvelope = CustomWidgetAiGeneratorService.ParseStudioTurnResponse(
            combinedInput,
            preferredEmoji: _session.Revisions.Count == 0 ? _session.SelectedEmoji : null,
            currentLogicJs: explicitLogic ? fencedLogicJs : prevLogicJs,
            currentLocales: explicitLocales ? externalLocales : prevLocales,
            fallbackCulture: _loc.CurrentCultureName,
            currentManifestJson: (explicitLogic || explicitLocales) ? null : prevJson);

        if (parsedEnvelope.Success && !string.Equals(parsedEnvelope.Kind, "edit", StringComparison.OrdinalIgnoreCase))
        {
            StopWaitingElapsedTimer();
            _session.IsInPlaceGenerating = false;
            _session.InFlightTargetNodeLabel = null;
            _session.InFlightDetectedWidgetName = string.Empty;

            bool envelopeSystemComposed = _session.IsSystemComposedPrompt(prompt);
            if (!string.IsNullOrWhiteSpace(prompt))
            {
                string userVisibleText = !string.IsNullOrWhiteSpace(_session.PendingDisplayPrompt) &&
                                         string.Equals(prompt, _session.PendingPrompt, StringComparison.Ordinal)
                    ? _session.PendingDisplayPrompt!
                    : prompt!;
                _session.AppendChatMessage(new StudioChatMessage
                {
                    Id = $"msg-u-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}",
                    Role = "user",
                    Kind = envelopeSystemComposed ? StudioContextCompactor.RepairMessageKind : "prompt",
                    Text = userVisibleText,
                    TargetNodePath = targetNodePath,
                    TargetNodeLabel = targetNodeLabel
                });
            }

            bool deferredBriefReset = _session.MergeTurnBriefUpdates(
                parsedEnvelope.BriefUpdates,
                null,
                envelopeSystemComposed ? null : prompt);
            List<StudioActionChip> envelopeChips = [];
            string envelopeReply = parsedEnvelope.Reply;
            if (deferredBriefReset)
            {
                envelopeReply = string.IsNullOrWhiteSpace(envelopeReply)
                    ? _loc.T("CustomWidget.Workshop.CoCreate.BriefResetConfirmPrompt")
                    : $"{envelopeReply}\n\n{_loc.T("CustomWidget.Workshop.CoCreate.BriefResetConfirmPrompt")}";
                envelopeChips.Add(new StudioActionChip(
                    "confirm_brief_reset",
                    _loc.T("CustomWidget.Workshop.CoCreate.BriefResetConfirmChip"),
                    string.Empty));
            }
            _session.AppendChatMessage(new StudioChatMessage
            {
                Id = $"msg-a-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}",
                Role = "assistant",
                Kind = parsedEnvelope.Kind,
                Text = envelopeReply,
                Suggestions = parsedEnvelope.Suggestions,
                ClarifyQuestions = parsedEnvelope.ClarifyQuestions ?? [],
                ActionChips = envelopeChips
            });
            _session.SelectedTargetNode = null;
            if (_shell.StageViewport.PreviewContent is not null)
            {
                _shell.StageViewport.PreviewContent.SelectedNodePath = null;
            }
            _session.RefineDraftIdea = _shell.ChatPanel.RefineTextBox?.Text ?? string.Empty;
            _session.PendingPrompt = string.Empty;
            _session.PendingDisplayPrompt = null;
            _session.PersistActiveDraftIfDirty();

            if (_session.Page == StudioPage.Preview)
            {
                _shell.ChatPanel.RefreshInPlaceGenerationChrome();
                _shell.ChatPanel.RefreshBriefButtonChrome();
                _shell.ChatPanel.RefreshTargetNodePillChrome();
                _shell.ChatPanel.RefreshSuggestionChips();
            }
            else
            {
                _shell.StageViewport.ShowPreviewPage(justLoaded: false);
            }

            return true;
        }

        string? effectiveLogicJs = explicitLogic
            ? fencedLogicJs
            : CustomWidgetAiGeneratorService.ResolveEffectiveLogicJs(
                prevLogicJs,
                parsedEnvelope.LogicJsPatchMode,
                parsedEnvelope.LogicJs);
        CustomWidgetLocaleCatalog? effectiveLocales = explicitLocales
            ? CustomWidgetAiGeneratorService.MergeLocaleCatalogs(externalLocales, parsedEnvelope.Locales)
            : CustomWidgetAiGeneratorService.MergeLocaleCatalogs(prevLocales, parsedEnvelope.Locales);

        string candidateJson = !string.IsNullOrWhiteSpace(parsedEnvelope.ManifestJson)
            ? parsedEnvelope.ManifestJson
            : rawJson;

        (CustomWidgetParseResult parsed, string normalized, CustomWidgetLocaleCatalog? validatedLocales) = _session.ValidateCandidate(
            candidateJson,
            effectiveLogicJs,
            effectiveLocales);
        bool hasErrors = parsed.Manifest is null || parsed.Diagnostics.Any(static d => d.IsError);
        if (hasErrors)
        {
            _session.LastBrokenJson = normalized;
            _session.LastBrokenLogicJs = effectiveLogicJs;
            _session.LastDiagnostics = parsed.Diagnostics;
            CustomWidgetValidationDiagnostic? first = parsed.Diagnostics.FirstOrDefault(static d => d.IsError);
            string techLine = _loc.Format(
                "CustomWidget.Studio.Diag.TechReasonLine",
                parsed.Diagnostics.Count(static d => d.IsError),
                first?.Message ?? "manifest.json");

            // P0：用户层只保留「发生了什么 + 下一步动作」；
            // 英文技术诊断折叠到可展开、可选中、可复制的详情区，不再与口语文案混排。
            UIElement BuildTechDetails() => new Expander
            {
                Header = new TextBlock
                {
                    Text = _loc.T("CustomWidget.Workshop.Diag.TechDetailsHeader"),
                    FontSize = 12
                },
                Content = new TextBlock
                {
                    Text = techLine,
                    IsTextSelectionEnabled = true,
                    TextWrapping = TextWrapping.Wrap,
                    FontSize = 11.5
                }
            };

            StopWaitingElapsedTimer();
            _session.IsInPlaceGenerating = false;
            if (_session.Page == StudioPage.Preview)
            {
                _shell.ChatPanel.RefreshInPlaceGenerationChrome();
            }
            _shell.ShowPageMessage(
                InfoBarSeverity.Error,
                _loc.T("CustomWidget.Workshop.Diag.ErrorTitle"),
                _loc.T("CustomWidget.Workshop.Diag.ErrorBody"),
                CreateRepairButton(),
                BuildTechDetails());
            return false;
        }

        StopWaitingElapsedTimer();
        _session.IsInPlaceGenerating = false;
        _session.InFlightTargetNodeLabel = null;
        _session.InFlightDetectedWidgetName = string.Empty;

        return ApplyValidatedEditTurn(
            normalized,
            parsed,
            prompt,
            replyText: parsedEnvelope.Reply,
            changeSummary: parsedEnvelope.ChangeSummary,
            briefUpdates: parsedEnvelope.BriefUpdates,
            suggestions: parsedEnvelope.Suggestions,
            sampleDataJson: parsedEnvelope.SampleDataJson,
            previousManifestJson: prevJson,
            targetNodePath: targetNodePath,
            targetNodeLabel: targetNodeLabel,
            fallbackLabel: fallbackLabel,
            source: source,
            nextLogicJs: effectiveLogicJs,
            hasExplicitLogicJs: true,
            nextLocales: validatedLocales,
            hasExplicitLocales: true,
            previousLogicJs: prevLogicJs,
            limitationNote: parsedEnvelope.LimitationNote);
    }

    private bool ApplyValidatedEditTurn(
        string normalizedManifestJson,
        CustomWidgetParseResult parsed,
        string? prompt,
        string? replyText,
        string? changeSummary,
        StudioBriefUpdateSpec? briefUpdates,
        IReadOnlyList<string>? suggestions,
        string? sampleDataJson,
        string? previousManifestJson,
        string? targetNodePath,
        string? targetNodeLabel,
        string? fallbackLabel,
        string source,
        string? nextLogicJs = null,
        bool hasExplicitLogicJs = false,
        CustomWidgetLocaleCatalog? nextLocales = null,
        bool hasExplicitLocales = false,
        string? previousLogicJs = null,
        IReadOnlyList<string>? fineGrainedDiffPills = null,
        string? executionSummary = null,
        IReadOnlyList<StudioStreamStepItem>? executionSteps = null,
        string? displayPrompt = null,
        IReadOnlyList<StudioActionChip>? actionChips = null,
        string? thinkingText = null,
        string? limitationNote = null)
    {
        bool editSystemComposed = !string.IsNullOrWhiteSpace(prompt) &&
                                  (!string.IsNullOrWhiteSpace(displayPrompt) || _session.IsSystemComposedPrompt(prompt));
        if (!string.IsNullOrWhiteSpace(prompt))
        {
            string userVisibleText = !string.IsNullOrWhiteSpace(displayPrompt)
                ? displayPrompt!
                : (!string.IsNullOrWhiteSpace(_session.PendingDisplayPrompt) &&
                   string.Equals(prompt, _session.PendingPrompt, StringComparison.Ordinal)
                    ? _session.PendingDisplayPrompt!
                    : prompt!);
            _session.AppendChatMessage(new StudioChatMessage
            {
                Id = $"msg-u-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}",
                Role = "user",
                Kind = editSystemComposed ? StudioContextCompactor.RepairMessageKind : "prompt",
                Text = userVisibleText,
                TargetNodePath = targetNodePath,
                TargetNodeLabel = targetNodeLabel
            });
        }

        string? resolvedChangeSummary = parsed.Manifest is not null
            ? StudioAiTurnOrchestrator.ResolveResidualI18nTokens(
                changeSummary,
                parsed.Manifest,
                nextLocales ?? _session.CurrentLocales,
                _session.EffectivePreviewCultureName)
            : changeSummary;
        string? resolvedReplyText = parsed.Manifest is not null
            ? StudioAiTurnOrchestrator.ResolveResidualI18nTokens(
                replyText,
                parsed.Manifest,
                nextLocales ?? _session.CurrentLocales,
                _session.EffectivePreviewCultureName)
            : replyText;

        string label = !string.IsNullOrWhiteSpace(resolvedChangeSummary)
            ? resolvedChangeSummary!
            : (!string.IsNullOrWhiteSpace(prompt)
                ? CustomWidgetWorkshopFlow.SummarizePrompt(prompt, maxLength: 64)
                : fallbackLabel ?? _loc.T("CustomWidget.Workshop.History.AiChange"));

        string? effectiveLogicJs = hasExplicitLogicJs ? nextLogicJs : _session.CurrentLogicJs;
        CustomWidgetManifest? prevManifest = !string.IsNullOrWhiteSpace(previousManifestJson)
            ? CustomWidgetManifestParser.Parse(previousManifestJson!).Manifest
            : null;
        IReadOnlyList<string> structuralDiffPills = parsed.Manifest is not null
            ? CustomWidgetHealthAnalyzer.FormatDiffPills(
                CustomWidgetHealthAnalyzer.ComputeStructuralDiff(
                    prevManifest,
                    parsed.Manifest,
                    previousLogicJs,
                    effectiveLogicJs),
                Localized.ActiveCultureName)
            : [];
        IReadOnlyList<string> diffPills;
        if (fineGrainedDiffPills is { Count: > 0 })
        {
            var mergedPills = new List<string>(fineGrainedDiffPills.Count + structuralDiffPills.Count);
            foreach (string pill in fineGrainedDiffPills)
            {
                if (!string.IsNullOrWhiteSpace(pill) && !mergedPills.Contains(pill, StringComparer.Ordinal))
                {
                    mergedPills.Add(pill);
                }
            }
            foreach (string pill in structuralDiffPills)
            {
                if (!string.IsNullOrWhiteSpace(pill) &&
                    !mergedPills.Contains(pill, StringComparer.Ordinal) &&
                    mergedPills.Count < 6)
                {
                    mergedPills.Add(pill);
                }
            }
            diffPills = mergedPills;
        }
        else
        {
            diffPills = structuralDiffPills;
        }

        string? activeRevisionIdBefore = _session.RevisionIndex >= 0 && _session.RevisionIndex < _session.Revisions.Count
            ? _session.Revisions[_session.RevisionIndex].RevisionId
            : null;
        bool isAi = string.Equals(source, "ai", StringComparison.OrdinalIgnoreCase);
        bool isAgentStage = string.Equals(source, "agent-stage", StringComparison.OrdinalIgnoreCase);
        StudioPersistedRevision rev = _session.AddRevision(
            normalizedManifestJson,
            label,
            isAiChange: isAi,
            source: source,
            isCommitted: false,
            markDirty: !isAgentStage,
            logicJs: nextLogicJs,
            hasExplicitLogicJs: hasExplicitLogicJs,
            locales: nextLocales,
            hasExplicitLocales: hasExplicitLocales);
        bool didMutateRevision = string.IsNullOrEmpty(activeRevisionIdBefore) ||
            !string.Equals(rev.RevisionId, activeRevisionIdBefore, StringComparison.Ordinal);

        if (!_session.IsEditMode && !string.IsNullOrWhiteSpace(sampleDataJson))
        {
            _session.PreviewSampleDataJson = sampleDataJson;
            _session.IsSampleDataMutedInPreview = false;
        }

        bool deferredBriefReset = _session.MergeTurnBriefUpdates(
            briefUpdates,
            parsed.Manifest,
            editSystemComposed ? null : prompt);
        _session.SelectedTargetNode = null;
        if (_shell.StageViewport.PreviewContent is not null)
        {
            _shell.StageViewport.PreviewContent.SelectedNodePath = null;
        }
        _session.RefineDraftIdea = _shell.ChatPanel.RefineTextBox?.Text ?? string.Empty;
        _session.PendingPrompt = string.Empty;
        _session.PendingDisplayPrompt = null;

        string assistantText = didMutateRevision
            ? (!string.IsNullOrWhiteSpace(resolvedReplyText) ? resolvedReplyText! : label)
            : (!string.IsNullOrWhiteSpace(resolvedReplyText) ? resolvedReplyText! : _loc.T("CustomWidget.Workshop.CoCreate.NoOpEditReply"));
        List<StudioActionChip> effectiveActionChips = actionChips is { Count: > 0 }
            ? [.. actionChips]
            : [];
        if (deferredBriefReset)
        {
            assistantText = $"{assistantText}\n\n{_loc.T("CustomWidget.Workshop.CoCreate.BriefResetConfirmPrompt")}";
            effectiveActionChips.Add(new StudioActionChip(
                "confirm_brief_reset",
                _loc.T("CustomWidget.Workshop.CoCreate.BriefResetConfirmChip"),
                string.Empty));
        }

        string? resolvedLimitationNote = !string.IsNullOrWhiteSpace(limitationNote) && parsed.Manifest is not null
            ? StudioAiTurnOrchestrator.ResolveResidualI18nTokens(
                limitationNote,
                parsed.Manifest,
                nextLocales ?? _session.CurrentLocales,
                _session.EffectivePreviewCultureName)
            : limitationNote;

        _session.AppendChatMessage(new StudioChatMessage
        {
            Id = $"msg-a-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}",
            Role = "assistant",
            Kind = didMutateRevision ? "edit" : "answer",
            Text = assistantText,
            LinkedRevisionId = didMutateRevision ? rev.RevisionId : null,
            ExecutionSummary = executionSummary,
            ExecutionSteps = executionSteps ?? [],
            ThinkingText = string.IsNullOrWhiteSpace(thinkingText) ? null : thinkingText,
            DiffPills = didMutateRevision ? diffPills : [],
            Suggestions = suggestions ?? [],
            ActionChips = effectiveActionChips,
            LimitationNote = string.IsNullOrWhiteSpace(resolvedLimitationNote) ? null : resolvedLimitationNote.Trim()
        });

        if (!isAgentStage)
        {
            _session.PersistActiveDraftIfDirty();
        }

        bool hasWarnings = parsed.Diagnostics.Any(static d => !d.IsError);
        if (hasWarnings)
        {
            _session.LastBrokenJson = normalizedManifestJson;
            _session.LastBrokenLogicJs = rev.LogicJs;
            _session.LastDiagnostics = parsed.Diagnostics;
        }
        else
        {
            _session.LastBrokenJson = null;
            _session.LastBrokenLogicJs = null;
            _session.LastDiagnostics = [];
        }

        if (_session.Page == StudioPage.Preview && _shell.StageViewport.CanMountOrUpdatePreviewInPlace)
        {
            _shell.StageViewport.ApplyLivePreviewUpdateInPlace(highlightDiffPills: didMutateRevision ? diffPills : null);
        }
        else
        {
            _shell.StageViewport.ShowPreviewPage(justLoaded: true);
        }

        _shell.StageViewport.RefreshScriptErrorBannerChrome();
        _shell.ChatPanel.RefreshSuggestionChips();

        return true;
    }

    private Button CreateRepairButton()
    {
        StudioAiChannelKind channel = _session.ResolveEffectiveStudioChannel(out _);
        string label = channel == StudioAiChannelKind.AgentMcp
            ? _loc.T("CustomWidget.Workshop.Repair.AgentAction")
            : _loc.T("CustomWidget.Workshop.Repair.DirectAction");

        var button = new Button { Content = label };
        button.Click += (_, _) => _ = StudioUiPrimitives.SafeInvokeAsync(_shell, "RepairBrokenDraft", async () =>
        {
            if (string.IsNullOrWhiteSpace(_session.LastBrokenJson) || _session.LastDiagnostics.Count == 0)
            {
                return;
            }

            StudioAiChannelKind activeChannel = _session.ResolveEffectiveStudioChannel(out string? activeProviderId);
            if (activeChannel == StudioAiChannelKind.AgentMcp)
            {
                string agentRepairPrompt = CustomWidgetTemplates.BuildChatAiRepairPrompt(
                    _session.LastBrokenJson,
                    _session.LastDiagnostics,
                    _loc.CurrentCultureName,
                    brokenLogicJs: _session.LastBrokenLogicJs);
                await _shell.CommitAndWorkspace.StartAgentMcpFlowAsync(agentRepairPrompt, _session.LastBrokenJson);
                button.Content = "✓ " + _loc.T("CustomWidget.Native.Dialog.CopiedButton");
                await Task.Delay(1800);
                button.Content = label;
                return;
            }

            if (activeProviderId is null)
            {
                await _shell.ChannelOnboarding.ShowAiConfigDialogInStudioAsync(null, _shell.ChannelOnboarding.NotifyChannelSelectorsRefresh);
                activeProviderId = CustomWidgetWorkshopFlow.ResolveDirectProvider(
                    CustomWidgetAiGeneratorService.Shared.GetConfiguredProviders(),
                    _prefs.PreferredAiProviderId);
                if (activeProviderId is null)
                {
                    return;
                }
            }

            string combinedPrompt = BuildCombinedHealthAndDiagnosticsRepairPrompt(primaryHealthIssue: null);
            await RunDirectGenerationAsync(
                activeProviderId,
                combinedPrompt,
                _session.LastBrokenJson,
                _loc.T("CustomWidget.Workshop.Repair.DirectAction"));
        });
        return button;
    }

    internal string BuildCombinedHealthAndDiagnosticsRepairPrompt(CustomWidgetHealthIssue? primaryHealthIssue)
    {
        var sb = new StringBuilder();
        sb.Append("[Target language: ")
            .Append(CustomWidgetRemotePromptService.DescribeTargetLanguage(_loc.CurrentCultureName))
            .AppendLine("]");
        sb.AppendLine("Please repair all active diagnostics and interaction issues in the current widget bundle in one pass without losing existing functionality, search/filter inputs, or state keys:");

        var emittedPrompts = new HashSet<string>(StringComparer.Ordinal);
        if (!string.IsNullOrWhiteSpace(_session.LiveRuntimeScriptError))
        {
            string locRef = _session.LiveRuntimeScriptErrorLine is { } ln and > 0
                ? $"logic.js:{ln}"
                : "logic.js";
            string runtimeLine = $"{locRef}: {_session.LiveRuntimeScriptError}";
            if (emittedPrompts.Add(runtimeLine))
            {
                sb.Append("- ").AppendLine(runtimeLine);
            }
        }

        if (primaryHealthIssue is not null && !string.IsNullOrWhiteSpace(primaryHealthIssue.SuggestedPrompt))
        {
            emittedPrompts.Add(primaryHealthIssue.SuggestedPrompt);
            sb.Append("- ").AppendLine(primaryHealthIssue.SuggestedPrompt);
        }

        if (_session.LatestHealthReport is { HasIssues: true })
        {
            foreach (CustomWidgetHealthIssue issue in _session.LatestHealthReport.Issues.Take(6))
            {
                if (string.IsNullOrWhiteSpace(issue.SuggestedPrompt) ||
                    issue.Code.StartsWith("layout.", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(issue.Code, "i18n.incomplete", StringComparison.OrdinalIgnoreCase) ||
                    !emittedPrompts.Add(issue.SuggestedPrompt))
                {
                    continue;
                }

                sb.Append("- ").AppendLine(issue.SuggestedPrompt);
            }
        }

        foreach (CustomWidgetValidationDiagnostic diag in _session.LastDiagnostics.Take(12))
        {
            string line = $"{diag.Path}: {diag.Message}";
            if (emittedPrompts.Add(line))
            {
                sb.Append("- ").AppendLine(line);
            }
        }

        if (!string.IsNullOrWhiteSpace(_session.LastBrokenLogicJs) &&
            !string.Equals(_session.LastBrokenLogicJs, _session.CurrentLogicJs, StringComparison.Ordinal))
        {
            sb.AppendLine();
            sb.AppendLine("Broken logic.js:");
            sb.AppendLine("```javascript");
            sb.AppendLine(_session.LastBrokenLogicJs);
            sb.AppendLine("```");
        }

        return sb.ToString().Trim();
    }

    internal async Task BeginGenerationAsync(string? idea, string? displayPrompt = null)
    {
        string trimmed = idea?.Trim() ?? string.Empty;
        if (trimmed.Length == 0)
        {
            _shell.ShowPageMessage(InfoBarSeverity.Informational, string.Empty, _loc.T("CustomWidget.Workshop.EmptyIdea"), null);
            return;
        }

        // Just-in-Time 模式选择：若已配置任意自有 API 服务商则自动启用自有 API 直连并立即生成；
        // 仅在未显式选择创作方式且尚未配置任何 API Key 时按需弹出二选一面板。
        if (string.IsNullOrWhiteSpace(_prefs.PreferredStudioChannel))
        {
            if (CustomWidgetAiGeneratorService.Shared.HasAnyConfiguredProvider())
            {
                _prefs.SetPreferredStudioChannel(CustomWidgetStudioPreferencesStore.ChannelDirect);
            }
            else
            {
                if (_shell.ChannelOnboarding.IsOnboardingActive &&
                    _shell.ChannelOnboarding.ActiveOnboardingOverlayKind == OnboardingOverlayKind.ChannelModal)
                {
                    _session.PendingChannelSelectionIdea = trimmed;
                    return;
                }

                _session.PendingChannelSelectionIdea = trimmed;
                _shell.ChannelOnboarding.ShowAgentMcpOnboardingTour(forceShow: true);
                return;
            }
        }

        if ((_session.IsInPlaceGenerating && _session.WaitingKind == WaitingKind.Direct) || _session.IsI18nQueueRunning)
        {
            return;
        }

        if (_session.IsInPlaceGenerating && _session.WaitingKind == WaitingKind.AgentMcp)
        {
            _session.IsInPlaceGenerating = false;
        }

        string? normalizedDisplayPrompt = string.IsNullOrWhiteSpace(displayPrompt) ? null : displayPrompt.Trim();
        if (_session.Page == StudioPage.Start && normalizedDisplayPrompt is null)
        {
            _session.StartDraftIdea = trimmed;
        }

        _session.PendingPrompt = trimmed;
        _session.PendingDisplayPrompt = normalizedDisplayPrompt;
        _session.PageBeforeWaiting = _session.Page;
        string? contextJson = _session.CurrentManifestJson;
        _session.InFlightTargetNodeLabel = StudioManifestInspector.FormatPickedNodeDisplayLabel(_session.SelectedTargetNode, _loc, contextJson);

        StudioAiChannelKind channel = _session.ResolveEffectiveStudioChannel(out string? directProviderId);
        if (channel == StudioAiChannelKind.AgentMcp)
        {
            await _shell.CommitAndWorkspace.StartAgentMcpFlowAsync(trimmed, contextJson);
            return;
        }

        if (directProviderId is null)
        {
            await _shell.ChannelOnboarding.ShowAiConfigDialogInStudioAsync(null, _shell.ChannelOnboarding.NotifyChannelSelectorsRefresh);
            directProviderId = CustomWidgetWorkshopFlow.ResolveDirectProvider(
                CustomWidgetAiGeneratorService.Shared.GetConfiguredProviders(),
                _prefs.PreferredAiProviderId);
            if (directProviderId is null)
            {
                return;
            }
        }

        await RunDirectGenerationAsync(directProviderId, trimmed, contextJson, normalizedDisplayPrompt);
    }

    private async Task RunDirectGenerationAsync(
        string providerId,
        string prompt,
        string? contextJson,
        string? displayPrompt = null)
    {
        CancelGeneration();
        var cts = new CancellationTokenSource();
        _generationCts = cts;

        string? normalizedDisplayPrompt = string.IsNullOrWhiteSpace(displayPrompt) ? null : displayPrompt.Trim();
        _session.PendingPrompt = prompt;
        _session.PendingDisplayPrompt = normalizedDisplayPrompt;

        string? effectiveContextJson = contextJson;
        string? currentLogicJs = _session.CurrentLogicJs;
        CustomWidgetLocaleCatalog? currentLocales = _session.CurrentLocales;
        string? targetNodePath = _session.SelectedTargetNode?.NodePath;
        string? targetNodePromptCtx = StudioManifestInspector.FormatTargetNodePromptContext(effectiveContextJson, targetNodePath);
        string? targetNodeLabel = _session.InFlightTargetNodeLabel ?? StudioManifestInspector.FormatPickedNodeDisplayLabel(_session.SelectedTargetNode, _loc, effectiveContextJson);
        bool allowSampleData = !_session.IsEditMode && string.IsNullOrWhiteSpace(effectiveContextJson);
        CustomWidgetHealthReport? healthReport = _shell.BuildCurrentHealthReport(effectiveContextJson);
        List<StudioChatMessage> recentContext = StudioContextCompactor
            .FilterAndClampRecentMessages(_session.ChatMessages, maxMessages: 8)
            .ToList();
        IReadOnlyList<string> effectivePinnedCapabilities = _session.ResolveEffectivePinnedCapabilitiesForPrompt(prompt);

        ResolvedAiInvocation baseResolved = CustomWidgetAiGeneratorService.Shared.ResolveEffectiveStudioInvocation(providerId);
        AiProviderCredentialEntry? credEntry = CustomWidgetAiCredentialStore.Shared.Get(baseResolved.Preset.Id);
        AiThinkingMode? uiThinkingOverride = StudioAiTurnOrchestrator.ResolveAdaptiveStudioThinkingOverride(
            baseResolved,
            credEntry?.FetchedModels,
            prompt,
            hasExistingManifest: !string.IsNullOrWhiteSpace(contextJson),
            targetNodePath: targetNodePromptCtx,
            pinnedCapabilityIds: effectivePinnedCapabilities,
            isSystemRepairPrompt: !string.IsNullOrWhiteSpace(normalizedDisplayPrompt),
            recentMessages: recentContext);
        ResolvedAiInvocation resolved = CustomWidgetAiGeneratorService.Shared.ResolveEffectiveStudioInvocation(
            providerId,
            thinkingOverride: uiThinkingOverride);

        _session.WaitingKind = WaitingKind.Direct;
        _session.IsInPlaceGenerating = true;
        _session.InFlightStage = AiGenerationStage.Connecting;
        _session.InFlightDetectedWidgetName = string.Empty;
        _shell.ChatPanel.DirectActiveProviderName = resolved.Preset.DisplayName;
        _shell.ChatPanel.DirectActiveModelName = resolved.Model;
        _shell.ChatPanel.DirectStartTickMs = Environment.TickCount64;
        _shell.ChatPanel.DirectLastCharTotal = 0;
        _shell.ChatPanel.DirectLastTokenTotal = 0;
        _shell.ChatPanel.DirectThinkingCollapsedByUser = true;
        _shell.ChatPanel.InFlightTraceSteps = [];
        _shell.ChatPanel.ChatStickToBottom = true;
        _shell.ChatPanel.ThinkingStickToBottom = true;

        if (_shell.PageInfoBar is not null)
        {
            _shell.PageInfoBar.IsOpen = false;
            _shell.PageInfoBar.Visibility = Visibility.Collapsed;
        }

        if (_session.Page != StudioPage.Preview)
        {
            _shell.StageViewport.ShowPreviewPage(justLoaded: false);
            if (_shell.PageInfoBar is not null)
            {
                _shell.PageInfoBar.IsOpen = false;
                _shell.PageInfoBar.Visibility = Visibility.Collapsed;
            }
        }
        else
        {
            if (normalizedDisplayPrompt is null && _shell.ChatPanel.RefineTextBox is not null)
            {
                _shell.ChatPanel.RefineTextBox.Text = string.Empty;
                _session.RefineDraftIdea = string.Empty;
            }
            _shell.ChatPanel.RefreshInPlaceGenerationChrome();
        }

        StartWaitingElapsedTimer(() =>
        {
            if (_shell.ChatPanel.DirectStatsBadgeText is not null)
            {
                TimeSpan elapsed = TimeSpan.FromMilliseconds(Math.Max(0, Environment.TickCount64 - _shell.ChatPanel.DirectStartTickMs));
                _shell.ChatPanel.DirectStatsBadgeText.Text = _shell.ChatPanel.FormatDirectStatsText(
                    _shell.ChatPanel.DirectActiveModelName,
                    elapsed,
                    _shell.ChatPanel.DirectLastTokenTotal);
            }
        });

        (int defaultWidgetWidth, int defaultWidgetHeight) = _hostBridge.GetDefaultWidgetSize();
        string? runtimeTraceSummary = _shell.StageViewport.PreviewVm?.RuntimeTrace.FormatForAiPrompt(6);
        AiCallTelemetrySnapshot telemetryBefore = DeskBoxAiService.Shared.GetTelemetrySnapshot();
        string? lastAppliedProgressiveManifest = null;
        try
        {
            StudioTurnResult result = await CustomWidgetAiGeneratorService.Shared.RunStudioTurnAsync(
                providerId: providerId,
                userMessage: prompt,
                currentManifestJson: effectiveContextJson,
                briefMarkdown: _session.BriefMarkdown,
                recentMessages: recentContext,
                healthReport: healthReport,
                targetNodePath: targetNodePromptCtx,
                preferredEmoji: _session.Revisions.Count == 0 ? _session.SelectedEmoji : null,
                allowSampleData: allowSampleData,
                cancellationToken: cts.Token,
                onProgress: progress =>
                {
                    if (cts.IsCancellationRequested || _shell.IsClosed)
                    {
                        return;
                    }

                    _shell.DispatcherQueue.TryEnqueue(() =>
                    {
                        if (!cts.IsCancellationRequested && !_shell.IsClosed && _session.IsInPlaceGenerating)
                        {
                            _shell.ChatPanel.UpdateDirectStreamProgress(progress);
                            if (!string.IsNullOrWhiteSpace(progress.ProgressiveManifestJson) &&
                                !string.Equals(progress.ProgressiveManifestJson, lastAppliedProgressiveManifest, StringComparison.Ordinal))
                            {
                                lastAppliedProgressiveManifest = progress.ProgressiveManifestJson;
                                _shell.StageViewport.TryApplyProgressiveManifestPreview(progress.ProgressiveManifestJson);
                            }
                        }
                    });
                },
                uiLanguage: _loc.CurrentCultureName,
                pinnedCapabilityIds: effectivePinnedCapabilities,
                currentLogicJs: currentLogicJs,
                currentLocales: currentLocales,
                isCapsuleViewport: _session.IsCapsulePreviewMode,
                defaultWidgetWidth: defaultWidgetWidth,
                runtimeTraceSummary: runtimeTraceSummary,
                defaultWidgetHeight: defaultWidgetHeight,
                existingSampleDataJson: _session.PreviewSampleDataJson,
                thinkingOverride: uiThinkingOverride,
                sessionCapabilityPackIds: _session.GetSortedLoadedCapabilityPackIds());

            if (result.DiscoveredCapabilityPackIds is { Count: > 0 })
            {
                _session.RecordLoadedCapabilityPacks(result.DiscoveredCapabilityPackIds);
            }

            if (cts.IsCancellationRequested || _shell.IsClosed || !ReferenceEquals(_generationCts, cts))
            {
                if (!_shell.IsClosed && ReferenceEquals(_generationCts, cts))
                {
                    _session.IsInPlaceGenerating = false;
                    RestoreAfterCancelledInPlaceWait();
                }
                return;
            }

            StopWaitingElapsedTimer();
            AiCallTelemetrySnapshot telemetryAfter = DeskBoxAiService.Shared.GetTelemetrySnapshot();
            long deltaPromptTokens = Math.Max(0, telemetryAfter.TotalPromptTokens - telemetryBefore.TotalPromptTokens);
            long deltaCachedTokens = Math.Max(0, telemetryAfter.TotalCachedPromptTokens - telemetryBefore.TotalCachedPromptTokens);
            long deltaCompletionTokens = Math.Max(0, telemetryAfter.TotalCompletionTokens - telemetryBefore.TotalCompletionTokens);
            IReadOnlyList<StudioStreamStepItem> completedTurnSteps =
                result.ExecutionSteps is { Count: > 0 } stepsFromResult
                    ? stepsFromResult
                    : _shell.ChatPanel.InFlightTraceSteps;
            TimeSpan turnElapsed = TimeSpan.FromMilliseconds(Math.Max(0, Environment.TickCount64 - _shell.ChatPanel.DirectStartTickMs));
            string turnSummary = _shell.ChatPanel.FormatDirectStatsText(
                _shell.ChatPanel.DirectActiveModelName,
                turnElapsed,
                _shell.ChatPanel.DirectLastTokenTotal,
                deltaPromptTokens,
                deltaCachedTokens,
                deltaCompletionTokens);
            _session.IsInPlaceGenerating = false;
            _session.InFlightTargetNodeLabel = null;
            _session.InFlightDetectedWidgetName = string.Empty;

            if (result.Success && !string.IsNullOrWhiteSpace(result.Kind) &&
                !string.Equals(result.Kind, "edit", StringComparison.OrdinalIgnoreCase))
            {
                bool resultSystemComposed = !string.IsNullOrWhiteSpace(normalizedDisplayPrompt);
                string userVisibleText = resultSystemComposed
                    ? normalizedDisplayPrompt!
                    : prompt;
                _session.AppendChatMessage(new StudioChatMessage
                {
                    Id = $"msg-u-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}",
                    Role = "user",
                    Kind = resultSystemComposed ? StudioContextCompactor.RepairMessageKind : "prompt",
                    Text = userVisibleText,
                    TargetNodePath = targetNodePath,
                    TargetNodeLabel = targetNodeLabel
                });
                bool isConsultOrUnsupported =
                    string.Equals(result.Kind, "answer", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(result.Kind, "unsupported", StringComparison.OrdinalIgnoreCase);
                bool deferredBriefReset = (!isConsultOrUnsupported || result.BriefUpdates is not null) &&
                    _session.MergeTurnBriefUpdates(
                        result.BriefUpdates,
                        null,
                        (resultSystemComposed || isConsultOrUnsupported) ? null : prompt);
                List<StudioActionChip> resultChips = [];
                string resultReply = result.Reply;
                if (deferredBriefReset)
                {
                    resultReply = string.IsNullOrWhiteSpace(resultReply)
                        ? _loc.T("CustomWidget.Workshop.CoCreate.BriefResetConfirmPrompt")
                        : $"{resultReply}\n\n{_loc.T("CustomWidget.Workshop.CoCreate.BriefResetConfirmPrompt")}";
                    resultChips.Add(new StudioActionChip(
                        "confirm_brief_reset",
                        _loc.T("CustomWidget.Workshop.CoCreate.BriefResetConfirmChip"),
                        string.Empty));
                }
                _session.AppendChatMessage(new StudioChatMessage
                {
                    Id = $"msg-a-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}",
                    Role = "assistant",
                    Kind = result.Kind,
                    Text = resultReply,
                    ExecutionSummary = turnSummary,
                    ExecutionSteps = completedTurnSteps,
                    ThinkingText = string.IsNullOrWhiteSpace(result.ThinkingText) ? null : result.ThinkingText,
                    Suggestions = result.Suggestions,
                    ActionChips = resultChips,
                    ClarifyQuestions = result.ClarifyQuestions ?? []
                });
                _session.SelectedTargetNode = null;
                if (_shell.StageViewport.PreviewContent is not null)
                {
                    _shell.StageViewport.PreviewContent.SelectedNodePath = null;
                }
                _session.RefineDraftIdea = _shell.ChatPanel.RefineTextBox?.Text ?? string.Empty;
                _session.PendingPrompt = string.Empty;
                _session.PendingDisplayPrompt = null;
                _session.PersistActiveDraftIfDirty();

                if (_session.Page == StudioPage.Preview && !string.IsNullOrWhiteSpace(_session.CurrentManifestJson))
                {
                    _shell.ChatPanel.RefreshInPlaceGenerationChrome();
                    _shell.ChatPanel.RefreshBriefButtonChrome();
                    _shell.ChatPanel.RefreshTargetNodePillChrome();
                    _shell.ChatPanel.RefreshSuggestionChips();
                }
                else
                {
                    _shell.StageViewport.ShowPreviewPage(justLoaded: false);
                }
                return;
            }

            if (result.Success &&
                !string.IsNullOrWhiteSpace(result.ManifestJson) &&
                result.ParseResult is { Manifest: not null })
            {
                string? nextLogicJs = CustomWidgetAiGeneratorService.ResolveEffectiveLogicJs(
                    currentLogicJs,
                    result.LogicJsPatchMode,
                    result.LogicJs);
                CustomWidgetLocaleCatalog? mergedTurnLocales = result.Locales ?? currentLocales;
                ApplyValidatedEditTurn(
                    result.ManifestJson,
                    result.ParseResult,
                    prompt,
                    replyText: result.Reply,
                    changeSummary: result.ChangeSummary,
                    briefUpdates: result.BriefUpdates,
                    suggestions: result.Suggestions,
                    sampleDataJson: result.SampleDataJson,
                    previousManifestJson: contextJson,
                    targetNodePath: targetNodePath,
                    targetNodeLabel: targetNodeLabel,
                    fallbackLabel: null,
                    source: "ai",
                    nextLogicJs: nextLogicJs,
                    hasExplicitLogicJs: true,
                    nextLocales: mergedTurnLocales,
                    hasExplicitLocales: true,
                    previousLogicJs: currentLogicJs,
                    fineGrainedDiffPills: result.FineGrainedDiffPills,
                    executionSummary: turnSummary,
                    executionSteps: completedTurnSteps,
                    displayPrompt: normalizedDisplayPrompt,
                    thinkingText: result.ThinkingText,
                    limitationNote: result.LimitationNote);
                return;
            }

            HandleDirectGenerationFailure(
                providerId,
                prompt,
                result.ErrorMessage ?? _loc.T("CustomWidget.Studio.Direct.Failed"),
                normalizedDisplayPrompt,
                failedResult: result);
        }
        catch (OperationCanceledException)
        {
            if (!_shell.IsClosed && ReferenceEquals(_generationCts, cts))
            {
                _session.IsInPlaceGenerating = false;
                RestoreAfterCancelledInPlaceWait();
            }
        }
        catch (Exception ex)
        {
            if (_shell.IsClosed || !ReferenceEquals(_generationCts, cts))
            {
                return;
            }

            _session.IsInPlaceGenerating = false;
            HandleDirectGenerationFailure(providerId, prompt, ex.Message, normalizedDisplayPrompt);
        }
        finally
        {
            if (ReferenceEquals(_generationCts, cts))
            {
                StopWaitingElapsedTimer();
                _generationCts = null;
            }

            cts.Dispose();
        }
    }

    private void HandleDirectGenerationFailure(
        string providerId,
        string prompt,
        string errorDetail,
        string? displayPrompt = null,
        StudioTurnResult? failedResult = null)
    {
        _session.InFlightTargetNodeLabel = null;
        _session.InFlightDetectedWidgetName = string.Empty;

        bool hasRepairableDraft = false;
        if (!string.IsNullOrWhiteSpace(failedResult?.ManifestJson))
        {
            _session.LastBrokenJson = failedResult.ManifestJson;
            _session.LastBrokenLogicJs = failedResult.LogicJs;
            _session.LastDiagnostics = failedResult.ParseResult?.Diagnostics is { Count: > 0 } diags
                ? diags
                : [new CustomWidgetValidationDiagnostic("$", errorDetail, IsError: true)];
            hasRepairableDraft = true;
        }

        bool canRestoreToComposer = string.IsNullOrWhiteSpace(displayPrompt);
        _session.PendingPrompt = string.Empty;
        _session.PendingDisplayPrompt = null;

        if (_session.Revisions.Count == 0 && _session.ChatMessages.Count == 0 && !_session.IsEditMode)
        {
            _session.LiveRuntimeScriptError = null;
            _session.LiveRuntimeScriptErrorLine = null;
            if (canRestoreToComposer)
            {
                _session.StartDraftIdea = prompt;
                _shell.StartGallery.ShowStartPage(prompt);
            }
            else
            {
                _shell.StartGallery.ShowStartPage(_session.StartDraftIdea);
            }

            ShowDirectFailureMessage(providerId, prompt, errorDetail, displayPrompt, hasRepairableDraft);
            return;
        }

        if (canRestoreToComposer)
        {
            _session.RefineDraftIdea = prompt;
            RestorePromptToComposerAfterFailure(prompt);
        }

        _shell.StageViewport.RevertUncommittedProgressivePreview();
        _shell.ChatPanel.RefreshInPlaceGenerationChrome();
        ShowDirectFailureMessage(providerId, prompt, errorDetail, displayPrompt, hasRepairableDraft);
    }

    private void RestorePromptToComposerAfterFailure(string prompt)
    {
        if (_shell.ChatPanel.RefineTextBox is not null && string.IsNullOrWhiteSpace(_shell.ChatPanel.RefineTextBox.Text))
        {
            _shell.ChatPanel.RefineTextBox.Text = prompt;
            _shell.ChatPanel.RefineTextBox.SelectionStart = _shell.ChatPanel.RefineTextBox.Text.Length;
        }
    }

    internal void RestoreAfterCancelledInPlaceWait()
    {
        bool canRestore = string.IsNullOrWhiteSpace(_session.PendingDisplayPrompt);
        string restoredPrompt = canRestore
            ? (!string.IsNullOrWhiteSpace(_session.PendingPrompt) ? _session.PendingPrompt : _session.StartDraftIdea)
            : _session.StartDraftIdea;
        string? refineRestore = canRestore ? _session.PendingPrompt : null;
        _session.PendingPrompt = string.Empty;
        _session.PendingDisplayPrompt = null;
        _session.InFlightTargetNodeLabel = null;
        _session.InFlightDetectedWidgetName = string.Empty;

        if (_session.Revisions.Count == 0 && _session.ChatMessages.Count == 0 && !_session.IsEditMode)
        {
            _session.LiveRuntimeScriptError = null;
            _session.LiveRuntimeScriptErrorLine = null;
            _session.StartDraftIdea = restoredPrompt;
            _shell.StartGallery.ShowStartPage(restoredPrompt);
            return;
        }

        if (!string.IsNullOrWhiteSpace(refineRestore))
        {
            _session.RefineDraftIdea = refineRestore;
            if (_shell.ChatPanel.RefineTextBox is not null &&
                string.IsNullOrWhiteSpace(_shell.ChatPanel.RefineTextBox.Text))
            {
                _shell.ChatPanel.RefineTextBox.Text = refineRestore;
                _shell.ChatPanel.RefineTextBox.SelectionStart = _shell.ChatPanel.RefineTextBox.Text.Length;
            }
        }

        _shell.StageViewport.RevertUncommittedProgressivePreview();
        _shell.ChatPanel.RefreshInPlaceGenerationChrome();
    }

    private void ShowDirectFailureMessage(
        string providerId,
        string prompt,
        string errorDetail,
        string? displayPrompt = null,
        bool hasRepairableDraft = false)
    {
        var actionsRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Margin = new Thickness(0, 0, 0, 10)
        };

        if (hasRepairableDraft)
        {
            Button repairBtn = CreateRepairButton();
            repairBtn.CornerRadius = new CornerRadius(6);
            repairBtn.Style = StudioUiPrimitives.AccentButtonStyle();
            actionsRow.Children.Add(repairBtn);
        }

        var retryBtn = new Button
        {
            Content = _loc.T("CustomWidget.Workshop.Direct.Retry"),
            CornerRadius = new CornerRadius(6)
        };
        retryBtn.Click += (_, _) => _ = StudioUiPrimitives.SafeInvokeAsync(
            _shell,
            "DirectErrorRetry",
            () => BeginGenerationAsync(prompt, displayPrompt));
        actionsRow.Children.Add(retryBtn);

        var configBtn = new Button
        {
            Content = _loc.T("CustomWidget.Workshop.Composer.ConfigKey"),
            CornerRadius = new CornerRadius(6)
        };
        configBtn.Click += (_, _) => _ = StudioUiPrimitives.SafeInvokeAsync(
            _shell,
            "DirectErrorConfigKey",
            () => _shell.ChannelOnboarding.ShowAiConfigDialogInStudioAsync(
                providerId,
                () =>
                {
                    _shell.ChannelOnboarding.NotifyChannelSelectorsRefresh();
                    if (CustomWidgetAiGeneratorService.Shared.HasAnyConfiguredProvider() &&
                        !string.IsNullOrWhiteSpace(prompt))
                    {
                        _ = StudioUiPrimitives.SafeInvokeAsync(
                            _shell,
                            "DirectErrorAutoResume",
                            () => BeginGenerationAsync(prompt, displayPrompt));
                    }
                }));
        actionsRow.Children.Add(configBtn);

        string messageKey = StudioManifestInspector.ClassifyDirectErrorMessageKey(errorDetail);
        _shell.ShowPageMessage(
            InfoBarSeverity.Error,
            string.Empty,
            _loc.Format(messageKey, errorDetail),
            action: null,
            extraContent: actionsRow);
    }

    private void StartWaitingElapsedTimer(Action onSecondTick)
    {
        StopWaitingElapsedTimer();
        Microsoft.UI.Dispatching.DispatcherQueueTimer timer = _shell.DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromSeconds(1);
        timer.IsRepeating = true;
        timer.Tick += (_, _) =>
        {
            if (_shell.IsClosed || !_session.IsInPlaceGenerating)
            {
                StopWaitingElapsedTimer();
                return;
            }

            onSecondTick();
        };
        timer.Start();
        _waitingElapsedTimer = timer;
    }

    internal void StopWaitingElapsedTimer()
    {
        if (_waitingElapsedTimer is { } timer)
        {
            timer.Stop();
            _waitingElapsedTimer = null;
        }
    }

    internal void CancelGeneration()
    {
        CancellationTokenSource? active = _generationCts;
        _generationCts = null;
        try
        {
            active?.Cancel();
        }
        catch
        {
        }
    }

    public void Dispose()
    {
        CancelGeneration();
        StopWaitingElapsedTimer();
    }
}
