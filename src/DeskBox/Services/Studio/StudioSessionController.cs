using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using DeskBox.Contracts;
using DeskBox.Controls.WidgetContents;
using DeskBox.Helpers;
using DeskBox.Models;
using DeskBox.Services;

namespace DeskBox.Services.Studio;

/// <summary>
/// Owns the mutable session state, revision history, chat transcript, capability pinning,
/// setting/secret overrides, i18n sync plan cache, and draft persistence for a Custom Widget Studio session.
/// Intentionally free of WinUI controls and global application lookups.
/// </summary>
internal sealed class StudioSessionController
{
    private readonly LocalizationService _loc;
    private readonly IStudioHostBridge _hostBridge;
    private readonly CustomWidgetStudioPreferencesStore _prefs;
    private readonly CustomWidgetStudioSessionStore _sessionStore;

    private long _lastDraftWriteTick;

    public StudioSessionController(
        LocalizationService loc,
        IStudioHostBridge? hostBridge = null,
        CustomWidgetStudioPreferencesStore? prefs = null,
        CustomWidgetStudioSessionStore? sessionStore = null,
        bool hasSystemWallpaperAvailable = false,
        StageBackdropMode initialBackdropMode = StageBackdropMode.Dark)
    {
        _loc = loc ?? throw new ArgumentNullException(nameof(loc));
        _hostBridge = hostBridge ?? NullStudioHostBridge.Instance;
        _prefs = prefs ?? CustomWidgetStudioPreferencesStore.Shared;
        _sessionStore = sessionStore ?? CustomWidgetStudioSessionStore.Shared;
        HasSystemWallpaperAvailable = hasSystemWallpaperAvailable;
        StageBackdropMode = initialBackdropMode;
    }

    // ── Target & Identity ───────────────────────────────────────────────
    private string? _targetWidgetId;
    private StudioPage _page = StudioPage.Start;
    private WaitingKind _waitingKind = WaitingKind.None;
    private bool _suppressDraftPersistOnExit;

    internal event Action? AgentUpsertTargetPotentiallyChanged;

    internal Func<bool, Task<(byte[] PngBytes, int Width, int Height, int OverflowPx)?>>? StagePreviewCapturer { get; set; }

    internal string? TargetWidgetId
    {
        get => _targetWidgetId;
        set
        {
            if (!string.Equals(_targetWidgetId, value, StringComparison.Ordinal))
            {
                _targetWidgetId = value;
                AgentUpsertTargetPotentiallyChanged?.Invoke();
            }
        }
    }
    internal string? TargetWidgetName { get; set; }
    internal string? SelectedEmoji { get; set; }
    internal bool IsExternalImportSession { get; set; }
    internal bool HasPromptedFirstRunSetupInSession { get; set; }

    // ── Session Collections ─────────────────────────────────────────────
    internal List<StudioPersistedRevision> Revisions { get; } = [];
    internal List<StudioChatMessage> ChatMessages { get; } = [];
    internal List<string> PinnedCapabilityIds { get; } = [];
    internal HashSet<string> LoadedCapabilityPackIds { get; } = new(StringComparer.OrdinalIgnoreCase);
    internal Dictionary<string, string> SessionSettingOverrides { get; } = new(StringComparer.Ordinal);
    internal Dictionary<string, string> SessionSecretOverrides { get; } = new(StringComparer.Ordinal);
    internal Dictionary<string, LocaleTranslationProgressUpdate> LiveLocaleJobUpdates { get; } =
        new(StringComparer.OrdinalIgnoreCase);
    internal Dictionary<string, string> LastFailedLocaleErrors { get; } =
        new(StringComparer.OrdinalIgnoreCase);

    // ── Brief, Drafts & Stage Preview State ─────────────────────────────
    internal string BriefMarkdown { get; set; } = string.Empty;
    internal string? PreviewSampleDataJson { get; set; }
    internal StudioDraftSnapshot? PendingRecoveryDraft { get; set; }
    internal bool WasReconciledExternalOnOpen { get; set; }
    internal CustomWidgetPickedNode? SelectedTargetNode { get; set; }
    internal bool IsInspectorModeEnabled { get; set; }
    internal StageBackdropMode StageBackdropMode { get; set; }
    internal string StageWidgetTheme { get; set; } = CustomWidgetStudioPreferencesStore.StageThemeAuto;
    internal WidgetChromeMode StageChromeMode { get; set; } = WidgetChromeMode.System;
    internal bool HasSystemWallpaperAvailable { get; }
    internal bool IsSampleDataMutedInPreview { get; set; }
    internal bool IsCapsulePreviewMode { get; set; }
    internal CustomWidgetHealthReport? LatestHealthReport { get; set; }

    // ── Revision Counters & Edit Baseline ───────────────────────────────
    private string _startDraftIdea = string.Empty;
    private string _refineDraftIdea = string.Empty;

    internal int RevisionIndex { get; set; } = -1;
    internal int AiRevisionCount { get; set; }
    internal int UncommittedEditCount { get; set; }
    internal bool HasDirtySettingOverrides { get; set; }
    internal bool HasDirtyChromeModeOverride { get; set; }
    internal bool Committed { get; set; }
    internal CustomWidgetStorePayload? EditBaselineStorePayload { get; set; }

    // ── Prompt / Generation / Repair / Pending Stage ────────────────────
    internal string StartDraftIdea
    {
        get => _startDraftIdea;
        set
        {
            string normalized = value ?? string.Empty;
            if (!string.Equals(_startDraftIdea, normalized, StringComparison.Ordinal))
            {
                _startDraftIdea = normalized;
                if (!string.IsNullOrWhiteSpace(normalized))
                {
                    _suppressDraftPersistOnExit = false;
                }
            }
        }
    }
    internal string? PendingChannelSelectionIdea { get; set; }
    internal string? PendingAgentStageId { get; set; }
    internal string? PendingAgentStageManifest { get; set; }
    internal bool IsCommittingPendingAgentStage { get; set; }
    internal string RefineDraftIdea
    {
        get => _refineDraftIdea;
        set
        {
            string normalized = value ?? string.Empty;
            if (!string.Equals(_refineDraftIdea, normalized, StringComparison.Ordinal))
            {
                _refineDraftIdea = normalized;
                if (!string.IsNullOrWhiteSpace(normalized))
                {
                    _suppressDraftPersistOnExit = false;
                }
            }
        }
    }
    internal string PendingPrompt { get; set; } = string.Empty;

    /// <summary>
    /// Short user-facing label shown instead of <see cref="PendingPrompt"/>. It is only set by flows that compose
    /// the real prompt on the user's behalf (validation repair, script-error fix, health auto-fix), so a non-empty
    /// value marks the pending prompt as system-composed. See <see cref="IsSystemComposedPrompt"/>.
    /// </summary>
    internal string? PendingDisplayPrompt { get; set; }

    /// <summary>
    /// True when <paramref name="prompt"/> is the pending system-composed repair/fix prompt. Such turns are tagged
    /// <see cref="StudioContextCompactor.RepairMessageKind"/> and never feed <c>brief.md</c> or recent-chat context,
    /// independent of UI language.
    /// </summary>
    internal bool IsSystemComposedPrompt(string? prompt) =>
        !string.IsNullOrWhiteSpace(prompt) &&
        !string.IsNullOrWhiteSpace(PendingDisplayPrompt) &&
        string.Equals(prompt, PendingPrompt, StringComparison.Ordinal);
    internal string? LastBrokenJson { get; set; }
    internal string? LastBrokenLogicJs { get; set; }
    internal IReadOnlyList<CustomWidgetValidationDiagnostic> LastDiagnostics { get; set; } = [];
    internal string? LiveRuntimeScriptError { get; set; }
    internal int? LiveRuntimeScriptErrorLine { get; set; }
    internal StudioBriefUpdateSpec? PendingBriefResetUpdate { get; set; }

    // ── Page / Waiting / In-Flight Generation ───────────────────────────
    internal StudioPage Page
    {
        get => _page;
        set
        {
            if (_page != value)
            {
                _page = value;
                AgentUpsertTargetPotentiallyChanged?.Invoke();
            }
        }
    }
    internal StudioPage PageBeforeWaiting { get; set; } = StudioPage.Start;
    internal WaitingKind WaitingKind
    {
        get => _waitingKind;
        set
        {
            if (value != WaitingKind.None)
            {
                _suppressDraftPersistOnExit = false;
            }

            if (_waitingKind != value)
            {
                _waitingKind = value;
                AgentUpsertTargetPotentiallyChanged?.Invoke();
            }
        }
    }
    internal bool IsInPlaceGenerating { get; set; }
    internal string? InFlightTargetNodeLabel { get; set; }
    internal string InFlightDetectedWidgetName { get; set; } = string.Empty;
    internal AiGenerationStage InFlightStage { get; set; } = AiGenerationStage.Connecting;

    // ── I18n Queue & Preview Culture ────────────────────────────────────
    internal bool IsLocaleCenterOpen { get; set; }
    internal bool IsI18nQueueRunning { get; set; }
    internal string? ActivePreviewCultureOverride { get; set; }
    internal string? ActiveI18nWorkingManifestJson { get; set; }
    internal CustomWidgetLocaleCatalog? ActiveI18nWorkingCatalog { get; set; }
    internal CustomWidgetI18nSyncPlan? CachedI18nSyncPlan { get; set; }
    internal string CachedI18nSyncPlanKey { get; set; } = string.Empty;

    // ── Computed State ──────────────────────────────────────────────────
    internal bool HasUnsavedWork =>
        !Committed && (UncommittedEditCount > 0 || HasDirtySettingOverrides || HasDirtyChromeModeOverride || IsI18nQueueRunning);

    internal bool IsEditMode => !string.IsNullOrWhiteSpace(TargetWidgetId);

    internal string? CurrentManifestJson =>
        RevisionIndex >= 0 && RevisionIndex < Revisions.Count
            ? Revisions[RevisionIndex].ManifestJson
            : null;

    internal string? CurrentLogicJs =>
        RevisionIndex >= 0 && RevisionIndex < Revisions.Count
            ? Revisions[RevisionIndex].LogicJs
            : null;

    internal CustomWidgetLocaleCatalog? CurrentLocales =>
        RevisionIndex >= 0 && RevisionIndex < Revisions.Count
            ? Revisions[RevisionIndex].Locales
            : null;

    internal bool IsAnyStudioJobRunning => IsInPlaceGenerating || IsI18nQueueRunning;

    internal string EffectivePreviewCultureName =>
        !string.IsNullOrWhiteSpace(ActivePreviewCultureOverride)
            ? ActivePreviewCultureOverride!
            : _loc.CurrentCultureName;

    internal bool CanUndo => RevisionIndex > 0;

    internal bool CanRedo => RevisionIndex >= 0 && RevisionIndex < Revisions.Count - 1;

    internal long TouchDraftWriteTick() =>
        Interlocked.Exchange(ref _lastDraftWriteTick, Environment.TickCount64);

    internal long ElapsedSinceLastDraftWriteMs() =>
        Environment.TickCount64 - Interlocked.Read(ref _lastDraftWriteTick);

    internal void InvalidateI18nSyncPlanCache()
    {
        CachedI18nSyncPlan = null;
        CachedI18nSyncPlanKey = string.Empty;
    }

    internal bool SelectRevisionIndex(int targetIndex)
    {
        if (Revisions.Count == 0)
        {
            return false;
        }

        int clamped = Math.Clamp(targetIndex, 0, Revisions.Count - 1);
        if (clamped == RevisionIndex)
        {
            return false;
        }

        RevisionIndex = clamped;
        if (Revisions[clamped].BriefMarkdown is { } revBrief)
        {
            BriefMarkdown = revBrief;
        }
        InvalidateI18nSyncPlanCache();
        SyncChatSupersededWithRevisionIndex();
        return true;
    }

    internal void WriteStudioContextTo(Utf8JsonWriter writer, bool includeManifest, bool isWindowClosed)
    {
        (int hostW, int hostH) = _hostBridge.GetDefaultWidgetSize();
        DeskBoxAgentBridgeServer.WriteCommonStudioContextHeader(
            writer,
            hostOnline: true,
            _loc.CurrentCultureName,
            hostW,
            hostH);
        if (isWindowClosed)
        {
            writer.WriteBoolean("isOpen", false);
            return;
        }

        string? manifestJson = CurrentManifestJson;
        CustomWidgetManifest? parsedManifest = !string.IsNullOrWhiteSpace(manifestJson)
            ? CustomWidgetManifestParser.Parse(manifestJson).Manifest
            : null;
        string? targetWidgetId = !string.IsNullOrWhiteSpace(TargetWidgetId)
            ? TargetWidgetId
            : null;
        string? manifestId = !string.IsNullOrWhiteSpace(parsedManifest?.Id)
            ? parsedManifest.Id
            : null;
        string? resolvedName = !string.IsNullOrWhiteSpace(TargetWidgetName)
            ? TargetWidgetName
            : parsedManifest?.ResolveLocalizedName(EffectivePreviewCultureName);

        writer.WriteBoolean("isOpen", true);
        writer.WriteString("page", Page.ToString().ToLowerInvariant());
        writer.WriteBoolean("isEditMode", IsEditMode);
        if (targetWidgetId is not null)
        {
            writer.WriteString("targetWidgetId", targetWidgetId);
            writer.WriteString("widgetId", targetWidgetId);
        }
        else
        {
            writer.WriteNull("targetWidgetId");
            writer.WriteNull("widgetId");
        }

        if (manifestId is not null)
        {
            writer.WriteString("manifestId", manifestId);
        }
        else
        {
            writer.WriteNull("manifestId");
        }

        if (!string.IsNullOrWhiteSpace(resolvedName))
        {
            writer.WriteString("widgetName", resolvedName);
        }
        writer.WriteString("activePreviewCulture", EffectivePreviewCultureName);
        writer.WriteBoolean("isCapsuleViewport", IsCapsulePreviewMode);
        writer.WriteBoolean("hasUnsavedChanges", HasUnsavedWork);
        writer.WriteNumber("revisionCount", Revisions.Count);

        writer.WriteStartArray("pinnedCapabilities");
        foreach (string capId in PinnedCapabilityIds)
        {
            writer.WriteStringValue(capId);
        }
        writer.WriteEndArray();

        if (SelectedTargetNode is { } picked)
        {
            writer.WritePropertyName("selectedNode");
            writer.WriteStartObject();
            writer.WriteString("path", picked.NodePath);
            writer.WriteString("nodeType", picked.NodeType);
            writer.WriteString("label", picked.DisplayLabel);
            writer.WriteEndObject();
        }
        else
        {
            writer.WriteNull("selectedNode");
        }

        if (!string.IsNullOrWhiteSpace(PendingAgentStageId))
        {
            writer.WriteString("pendingPreviewStageId", PendingAgentStageId);
        }

        if (!string.IsNullOrWhiteSpace(BriefMarkdown))
        {
            writer.WriteString("briefMarkdown", BriefMarkdown);
        }

        writer.WriteBoolean("hasLogicJs", !string.IsNullOrWhiteSpace(CurrentLogicJs));
        writer.WriteBoolean("hasLocales", CurrentLocales is { HasAnyLocale: true });
        if (includeManifest && !string.IsNullOrWhiteSpace(manifestJson))
        {
            writer.WriteString("manifestJson", manifestJson);
            if (!string.IsNullOrWhiteSpace(CurrentLogicJs))
            {
                writer.WriteString("logicJs", CurrentLogicJs);
            }

            if (CurrentLocales is { HasAnyLocale: true } currentLocales)
            {
                writer.WriteStartObject("locales");
                foreach ((string code, IReadOnlyDictionary<string, string> entries) in currentLocales.Locales)
                {
                    writer.WriteStartObject(code);
                    foreach ((string k, string v) in entries)
                    {
                        writer.WriteString(k, v);
                    }

                    writer.WriteEndObject();
                }

                writer.WriteEndObject();
            }
        }
    }

    internal StudioPersistedRevision AddRevision(
        string manifestJson,
        string label,
        bool isAiChange,
        string source = "ai",
        bool isCommitted = false,
        bool markDirty = true,
        string? logicJs = null,
        bool hasExplicitLogicJs = false,
        CustomWidgetLocaleCatalog? locales = null,
        bool hasExplicitLocales = false,
        string? coalesceGroupKey = null)
    {
        string? effectiveLogicJs = hasExplicitLogicJs ? logicJs : CurrentLogicJs;
        if (string.IsNullOrWhiteSpace(effectiveLogicJs))
        {
            effectiveLogicJs = null;
        }

        CustomWidgetLocaleCatalog? effectiveLocales = hasExplicitLocales ? locales : CurrentLocales;
        if (effectiveLocales is { HasAnyLocale: false })
        {
            effectiveLocales = null;
        }

        // 从画廊模板点击「以此为底改造」仅在首版，直到用户第一次真正改动时才惰性分叉为 user.fork-*，同时保留多语言包。
        if (!IsEditMode &&
            markDirty &&
            !string.Equals(source, "template", StringComparison.OrdinalIgnoreCase) &&
            Revisions.Count > 0 &&
            string.Equals(Revisions[0].Source, "template", StringComparison.OrdinalIgnoreCase))
        {
            CustomWidgetParseResult forkCheck = CustomWidgetManifestParser.Parse(manifestJson);
            if (forkCheck.Manifest is { } templateManifest &&
                StudioManifestInspector.IsBuiltInOrCatalogTemplateId(templateManifest.Id))
            {
                CustomWidgetForkBundleResult forkBundle = CustomWidgetOnboardingHelper.CreateForkBundle(
                    manifestJson,
                    effectiveLogicJs,
                    effectiveLocales,
                    EffectivePreviewCultureName,
                    forkTitleSuffix: null);
                manifestJson = CustomWidgetI18nEngine.AutoCompileIfLocalized(
                    forkBundle.ManifestJson,
                    forkBundle.LogicJs,
                    forkBundle.Locales,
                    EffectivePreviewCultureName,
                    out CustomWidgetLocaleCatalog? compiledForkLocales);
                effectiveLogicJs = forkBundle.LogicJs;
                effectiveLocales = compiledForkLocales is { HasAnyLocale: true }
                    ? compiledForkLocales
                    : null;
            }
        }

        InvalidateI18nSyncPlanCache();

        if (RevisionIndex >= 0 && RevisionIndex < Revisions.Count - 1)
        {
            Revisions.RemoveRange(RevisionIndex + 1, Revisions.Count - RevisionIndex - 1);
        }

        string sha = CustomWidgetStudioSessionStore.ComputeManifestSha256(
            manifestJson,
            effectiveLogicJs,
            effectiveLocales);
        if (Revisions.Count > 0 &&
            string.Equals(Revisions[^1].ManifestSha256, sha, StringComparison.Ordinal))
        {
            RevisionIndex = Revisions.Count - 1;
            SyncChatSupersededWithRevisionIndex();
            return Revisions[^1];
        }

        // 连续未提交的尺寸/标题/检查器微调在 1.5s 窗口内且 CoalesceGroupKey 一致时合并为同一撤销步。
        long nowUtcTicks = DateTimeOffset.UtcNow.UtcTicks;
        bool canCoalesceTopManual =
            !isCommitted &&
            string.Equals(source, "manual", StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(coalesceGroupKey) &&
            Revisions.Count > 0 &&
            !Revisions[^1].IsCommitted &&
            string.Equals(Revisions[^1].Source, "manual", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(Revisions[^1].CoalesceGroupKey, coalesceGroupKey, StringComparison.Ordinal) &&
            (nowUtcTicks - Revisions[^1].CreatedAtUtcTicks) <= TimeSpan.FromMilliseconds(1500).Ticks;
        if (canCoalesceTopManual)
        {
            if (Revisions.Count >= 2 &&
                string.Equals(Revisions[^2].ManifestSha256, sha, StringComparison.Ordinal))
            {
                Revisions.RemoveAt(Revisions.Count - 1);
                RevisionIndex = Revisions.Count - 1;
                if (UncommittedEditCount > 0)
                {
                    UncommittedEditCount--;
                }
                SyncChatSupersededWithRevisionIndex();
                return Revisions[^1];
            }

            var coalescedRev = CustomWidgetStudioSessionStore.ShareUnchangedArtifacts(
                new StudioPersistedRevision
                {
                    RevisionId = Revisions[^1].RevisionId,
                    CreatedAtUtcTicks = nowUtcTicks,
                    Source = source,
                    ChangeSummary = string.IsNullOrWhiteSpace(label)
                        ? _loc.T("CustomWidget.Workshop.History.AiChange")
                        : label,
                    ManifestSha256 = sha,
                    IsCommitted = isCommitted,
                    ManifestJson = manifestJson,
                    LogicJs = effectiveLogicJs,
                    Locales = effectiveLocales,
                    CoalesceGroupKey = coalesceGroupKey,
                    BriefMarkdown = string.IsNullOrWhiteSpace(BriefMarkdown) ? null : BriefMarkdown
                },
                Revisions.Count >= 2 ? Revisions[^2] : null);
            Revisions[^1] = coalescedRev;
            RevisionIndex = Revisions.Count - 1;
            SyncChatSupersededWithRevisionIndex();
            return coalescedRev;
        }

        var rev = CustomWidgetStudioSessionStore.ShareUnchangedArtifacts(
            new StudioPersistedRevision
            {
                RevisionId = $"rev-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture)}-{Revisions.Count + 1}",
                CreatedAtUtcTicks = nowUtcTicks,
                Source = source,
                ChangeSummary = string.IsNullOrWhiteSpace(label)
                    ? _loc.T("CustomWidget.Workshop.History.AiChange")
                    : label,
                ManifestSha256 = sha,
                IsCommitted = isCommitted,
                ManifestJson = manifestJson,
                LogicJs = effectiveLogicJs,
                Locales = effectiveLocales,
                CoalesceGroupKey = coalesceGroupKey,
                BriefMarkdown = string.IsNullOrWhiteSpace(BriefMarkdown) ? null : BriefMarkdown
            },
            Revisions.Count > 0 ? Revisions[^1] : null);
        Revisions.Add(rev);

        IReadOnlyList<StudioPersistedRevision> trimmed = CustomWidgetStudioSessionStore.TrimRevisions(Revisions);
        if (trimmed.Count != Revisions.Count)
        {
            Revisions.Clear();
            Revisions.AddRange(trimmed);
        }

        RevisionIndex = Revisions.Count - 1;
        SyncChatSupersededWithRevisionIndex();
        if (isAiChange)
        {
            AiRevisionCount++;
        }

        if (markDirty && !isCommitted)
        {
            _suppressDraftPersistOnExit = false;
            UncommittedEditCount++;
        }

        return rev;
    }

    internal void SyncChatSupersededWithRevisionIndex()
    {
        var indexByRevisionId = new Dictionary<string, int>(Revisions.Count, StringComparer.Ordinal);
        for (int i = 0; i < Revisions.Count; i++)
        {
            indexByRevisionId[Revisions[i].RevisionId] = i;
        }

        var supersededFlags = new bool[ChatMessages.Count];
        for (int i = 0; i < ChatMessages.Count; i++)
        {
            StudioChatMessage msg = ChatMessages[i];
            if (!string.IsNullOrWhiteSpace(msg.LinkedRevisionId))
            {
                bool isSuperseded = !indexByRevisionId.TryGetValue(msg.LinkedRevisionId, out int revIdx) ||
                                    revIdx > RevisionIndex;
                supersededFlags[i] = isSuperseded;
                if (i > 0 &&
                    string.Equals(ChatMessages[i - 1].Role, "user", StringComparison.OrdinalIgnoreCase))
                {
                    supersededFlags[i - 1] = isSuperseded;
                }
            }
        }

        for (int i = 0; i < ChatMessages.Count; i++)
        {
            if (ChatMessages[i].IsSuperseded != supersededFlags[i])
            {
                ChatMessages[i] = ChatMessages[i] with { IsSuperseded = supersededFlags[i] };
            }
        }
    }

    internal void AppendChatMessage(StudioChatMessage message)
    {
        _suppressDraftPersistOnExit = false;
        ChatMessages.Add(message);
        IReadOnlyList<StudioChatMessage> trimmed = CustomWidgetStudioSessionStore.TrimChatMessages(ChatMessages);
        if (trimmed.Count != ChatMessages.Count)
        {
            ChatMessages.Clear();
            ChatMessages.AddRange(trimmed);
        }
    }

    internal bool MergeTurnBriefUpdates(StudioBriefUpdateSpec? updates, CustomWidgetManifest? newManifest, string? userPrompt)
    {
        CustomWidgetDesignBrief current = CustomWidgetStudioSessionStore.ParseBriefMarkdown(BriefMarkdown);
        bool hadEmptyBrief = current.IsEmpty || StudioContextCompactor.IsSyntheticRepairOrSystemPrompt(current.Purpose);

        if (IsEditMode && updates is { Reset: true } && !hadEmptyBrief)
        {
            PendingBriefResetUpdate = updates;
            return true;
        }

        string? effectiveSeedIdea = StartDraftIdea;
        if (string.IsNullOrWhiteSpace(effectiveSeedIdea) && !IsEditMode)
        {
            effectiveSeedIdea = ChatMessages
                .FirstOrDefault(static m =>
                    string.Equals(m.Role, "user", StringComparison.OrdinalIgnoreCase) &&
                    !m.IsSuperseded &&
                    !string.Equals(m.Kind, StudioContextCompactor.RepairMessageKind, StringComparison.OrdinalIgnoreCase) &&
                    !StudioContextCompactor.IsSyntheticRepairOrSystemPrompt(m.Text))
                ?.Text;
        }

        current = StudioContextCompactor.SanitizeAndRollBrief(
            current,
            updates,
            newManifest,
            userPrompt,
            effectiveSeedIdea,
            hadEmptyBrief);

        if (!current.IsEmpty)
        {
            BriefMarkdown = CustomWidgetStudioSessionStore.FormatBriefMarkdown(
                current,
                preferEnglish: !Localized.IsActiveChinese);
        }
        else if (hadEmptyBrief)
        {
            BriefMarkdown = string.Empty;
        }

        if (RevisionIndex >= 0 && RevisionIndex < Revisions.Count)
        {
            Revisions[RevisionIndex] = Revisions[RevisionIndex] with
            {
                BriefMarkdown = string.IsNullOrWhiteSpace(BriefMarkdown) ? null : BriefMarkdown
            };
        }

        return false;
    }

    internal void PersistActiveDraftIfDirty()
    {
        if (_suppressDraftPersistOnExit ||
            Committed ||
            (UncommittedEditCount <= 0 && !HasDirtySettingOverrides && ChatMessages.Count == 0 && PinnedCapabilityIds.Count == 0))
        {
            return;
        }

        try
        {
            var snapshot = new StudioDraftSnapshot
            {
                UpdatedAtUtcTicks = DateTimeOffset.UtcNow.UtcTicks,
                TargetWidgetId = TargetWidgetId,
                IsExternalImportSession = IsExternalImportSession,
                SelectedEmoji = SelectedEmoji ?? string.Empty,
                StartDraftIdea = StartDraftIdea,
                RefineDraftIdea = RefineDraftIdea,
                SelectedRevisionIndex = Math.Max(0, RevisionIndex),
                BriefMarkdown = BriefMarkdown,
                PreviewSampleDataJson = PreviewSampleDataJson,
                PinnedCapabilityIds = PinnedCapabilityIds.ToList(),
                LoadedCapabilityPackIds = LoadedCapabilityPackIds.OrderBy(static x => x, StringComparer.Ordinal).ToList(),
                Revisions = Revisions.ToList(),
                ChatMessages = ChatMessages.ToList()
            };

            if (IsEditMode && !string.IsNullOrWhiteSpace(TargetWidgetId))
            {
                _sessionStore.SaveWidgetDraft(TargetWidgetId!, snapshot);
            }
            else
            {
                _sessionStore.SaveCreateDraft(snapshot);
            }
        }
        catch (Exception ex)
        {
            _hostBridge.Log($"[CustomWidgetStudio] Persist draft failed: {ex.Message}");
        }
    }

    internal bool RestoreDraftState(StudioDraftSnapshot draft)
    {
        _suppressDraftPersistOnExit = false;
        PendingRecoveryDraft = null;
        WasReconciledExternalOnOpen = false;
        IsExternalImportSession = draft.IsExternalImportSession;
        if (!string.IsNullOrWhiteSpace(draft.SelectedEmoji))
        {
            SelectedEmoji = draft.SelectedEmoji;
        }

        StartDraftIdea = draft.StartDraftIdea;
        RefineDraftIdea = draft.RefineDraftIdea;
        BriefMarkdown = draft.BriefMarkdown;
        CustomWidgetDesignBrief parsedDraftBrief = CustomWidgetStudioSessionStore.ParseBriefMarkdown(BriefMarkdown);
        if (StudioContextCompactor.IsSyntheticRepairOrSystemPrompt(parsedDraftBrief.Purpose))
        {
            CustomWidgetDesignBrief healedBrief = StudioContextCompactor.SanitizeAndRollBrief(
                parsedDraftBrief,
                updates: null,
                newManifest: null,
                rawUserPrompt: null,
                startDraftIdea: StartDraftIdea,
                isInitialOrEmptyBrief: true);
            if (string.IsNullOrWhiteSpace(healedBrief.Purpose) &&
                !string.IsNullOrWhiteSpace(StartDraftIdea) &&
                !StudioContextCompactor.IsSyntheticRepairOrSystemPrompt(StartDraftIdea))
            {
                healedBrief = healedBrief with { Purpose = StartDraftIdea.Trim() };
            }
            BriefMarkdown = healedBrief.IsEmpty
                ? string.Empty
                : CustomWidgetStudioSessionStore.FormatBriefMarkdown(healedBrief, preferEnglish: !Localized.IsActiveChinese);
        }
        PreviewSampleDataJson = draft.PreviewSampleDataJson;

        PinnedCapabilityIds.Clear();
        foreach (string capId in draft.PinnedCapabilityIds)
        {
            if (!string.IsNullOrWhiteSpace(capId) &&
                !PinnedCapabilityIds.Contains(capId, StringComparer.OrdinalIgnoreCase))
            {
                PinnedCapabilityIds.Add(capId);
            }
        }

        LoadedCapabilityPackIds.Clear();
        RecordLoadedCapabilityPacks(draft.LoadedCapabilityPackIds);

        Revisions.Clear();
        Revisions.AddRange(draft.Revisions);
        if (!IsExternalImportSession && Revisions.Count > 0)
        {
            CustomWidgetManifest? firstRevisionManifest = CustomWidgetManifestParser.Parse(Revisions[0].ManifestJson).Manifest;
            if (IsUntrustedExternalOrigin(firstRevisionManifest?.Origin))
            {
                IsExternalImportSession = true;
            }
        }
        RevisionIndex = Revisions.Count > 0
            ? Math.Clamp(draft.SelectedRevisionIndex, 0, Revisions.Count - 1)
            : -1;

        ChatMessages.Clear();
        ChatMessages.AddRange(draft.ChatMessages);
        SyncChatSupersededWithRevisionIndex();

        AiRevisionCount = Math.Max(1, Revisions.Count(static r => string.Equals(r.Source, "ai", StringComparison.OrdinalIgnoreCase)));
        UncommittedEditCount = Math.Max(1, Revisions.Count(static r => !r.IsCommitted));

        return Revisions.Count > 0;
    }

    internal void DismissPendingRecoveryDraft()
    {
        PendingRecoveryDraft = null;
        if (IsEditMode && !string.IsNullOrWhiteSpace(TargetWidgetId))
        {
            _sessionStore.DiscardWidgetDraft(TargetWidgetId!);
        }
        else
        {
            _sessionStore.DiscardCreateDraft();
        }
    }

    internal void DiscardActiveDraftCleanly()
    {
        _suppressDraftPersistOnExit = true;
        PendingRecoveryDraft = null;
        UncommittedEditCount = 0;
        HasDirtySettingOverrides = false;
        HasDirtyChromeModeOverride = false;
        ChatMessages.Clear();
        PinnedCapabilityIds.Clear();
        LoadedCapabilityPackIds.Clear();
        if (IsEditMode && !string.IsNullOrWhiteSpace(TargetWidgetId))
        {
            _sessionStore.DiscardWidgetDraft(TargetWidgetId!);
        }
        else
        {
            _sessionStore.DiscardCreateDraft();
        }
    }

    internal void RecordStudioCommitDiagnostic(
        string commitSource,
        string widgetId,
        bool wasEdit,
        string manifestJson,
        string? logicJs,
        CustomWidgetHealthReport? healthReport)
    {
        try
        {
            string? manifestId = CustomWidgetManifestParser.Parse(manifestJson).Manifest?.Id;
            var healthDiags = new List<string>();
            if (healthReport is { Issues.Count: > 0 })
            {
                foreach (CustomWidgetHealthIssue issue in healthReport.Issues.Take(8))
                {
                    healthDiags.Add($"{(issue.IsWarning ? "WARN" : "INFO")} {issue.Code}: {issue.Message}");
                }
            }

            string lastUserPrompt = ChatMessages
                .LastOrDefault(m => string.Equals(m.Role, "user", StringComparison.OrdinalIgnoreCase))?.Text
                ?? string.Empty;

            StudioAiChannelKind channel = ResolveEffectiveStudioChannel(out string? activeProviderId);
            string providerId = channel == StudioAiChannelKind.DirectApi && !string.IsNullOrWhiteSpace(activeProviderId)
                ? activeProviderId
                : "agent";

            _hostBridge.Log(
                $"[StudioAI] Committed source={commitSource} widgetId={widgetId} manifestId={manifestId ?? "(none)"} wasEdit={wasEdit} aiRevisions={AiRevisionCount} healthIssues={healthDiags.Count} overflowPx={healthReport?.VerticalOverflowPx ?? 0}");

            CustomWidgetDiagnosticArchive.Shared.Record(new StudioDiagnosticArchiveEntry(
                EntryId: string.Empty,
                CreatedAtUtc: string.Empty,
                EventKind: "studio-commit",
                WidgetId: widgetId,
                ManifestId: manifestId,
                ProviderId: providerId,
                Model: string.Empty,
                ThinkingMode: string.Empty,
                Success: true,
                WasAutoRepaired: false,
                LlmCalls: 0,
                ToolRounds: 0,
                RepairRounds: 0,
                ElapsedMs: 0,
                UserPrompt: lastUserPrompt,
                SummaryOrError: $"committed ({commitSource}, wasEdit={wasEdit})",
                Diagnostics: healthDiags,
                ManifestJson: manifestJson,
                LogicJs: logicJs));
        }
        catch (Exception ex)
        {
            _hostBridge.Log($"[StudioAI] Failed to record commit diagnostic: {ex.Message}");
        }
    }

    internal (CustomWidgetParseResult Result, string NormalizedJson, CustomWidgetLocaleCatalog? EffectiveLocales) ValidateCandidate(
        string rawJson,
        string? candidateLogicJs = null,
        CustomWidgetLocaleCatalog? candidateLocales = null)
    {
        string normalized = CustomWidgetAiGeneratorService.ExtractInlineManifestLocalesAndStrip(
            rawJson,
            candidateLocales,
            out CustomWidgetLocaleCatalog? effectiveLocales);
        normalized = CustomWidgetTemplates.SyncManifestScriptPropertyJson(normalized, candidateLogicJs);
        normalized = CustomWidgetI18nEngine.AutoCompileIfLocalized(
            normalized,
            candidateLogicJs,
            effectiveLocales,
            _loc.CurrentCultureName,
            out effectiveLocales);
        if (Revisions.Count == 0 && CustomWidgetTemplates.IsEmojiIcon(SelectedEmoji))
        {
            try
            {
                normalized = CustomWidgetTemplates.WithUpdatedIconJson(normalized, SelectedEmoji!.Trim());
            }
            catch
            {
            }
        }

        return (
            CustomWidgetScriptValidator.ValidateWorkspaceBundle(normalized, candidateLogicJs, effectiveLocales, strictAuthoring: true),
            normalized,
            effectiveLocales);
    }

    internal StudioAiChannelKind ResolveEffectiveStudioChannel(out string? activeDirectProviderId)
    {
        if (string.Equals(_prefs.PreferredStudioChannel, CustomWidgetStudioPreferencesStore.ChannelAgentMcp, StringComparison.OrdinalIgnoreCase))
        {
            activeDirectProviderId = null;
            return StudioAiChannelKind.AgentMcp;
        }

        activeDirectProviderId = CustomWidgetWorkshopFlow.ResolveDirectProvider(
            CustomWidgetAiGeneratorService.Shared.GetConfiguredProviders(),
            _prefs.PreferredAiProviderId);
        return StudioAiChannelKind.DirectApi;
    }

    internal bool IsCapabilityPinned(string id) =>
        !string.IsNullOrWhiteSpace(id) &&
        PinnedCapabilityIds.Any(x => string.Equals(x, id.Trim(), StringComparison.OrdinalIgnoreCase));

    internal bool TogglePinnedCapability(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return false;
        }

        string normalized = id.Trim();
        int idx = PinnedCapabilityIds.FindIndex(x => string.Equals(x, normalized, StringComparison.OrdinalIgnoreCase));
        if (idx >= 0)
        {
            PinnedCapabilityIds.RemoveAt(idx);
            return true;
        }

        if (CustomWidgetCapabilityRegistry.GetById(normalized) is { } spec)
        {
            _suppressDraftPersistOnExit = false;
            PinnedCapabilityIds.Add(spec.Id);
            return true;
        }

        return false;
    }

    internal bool ClearPinnedCapabilities()
    {
        if (PinnedCapabilityIds.Count == 0)
        {
            return false;
        }

        PinnedCapabilityIds.Clear();
        return true;
    }

    internal void RecordLoadedCapabilityPacks(IEnumerable<string>? idsOrPacks)
    {
        if (idsOrPacks is null)
        {
            return;
        }

        foreach (string packId in CustomWidgetCapabilityRegistry.ResolveDomainPackIdsForCapabilities(idsOrPacks))
        {
            LoadedCapabilityPackIds.Add(packId);
        }
    }

    internal IReadOnlyList<string> GetSortedLoadedCapabilityPackIds() =>
        LoadedCapabilityPackIds.Count == 0
            ? []
            : LoadedCapabilityPackIds.OrderBy(static x => x, StringComparer.Ordinal).ToList();

    internal IReadOnlyList<string> ResolveEffectivePinnedCapabilitiesForPrompt(string? promptText)
    {
        var merged = new List<string>(PinnedCapabilityIds);
        var seen = new HashSet<string>(PinnedCapabilityIds, StringComparer.OrdinalIgnoreCase);

        foreach (string id in CustomWidgetCapabilityRegistry.ExtractMentionedCapabilityIds(promptText))
        {
            if (seen.Add(id))
            {
                merged.Add(id);
            }
        }

        return merged;
    }

    internal CustomWidgetI18nSyncPlan? GetOrBuildCurrentI18nSyncPlan()
    {
        string? manifestJson = ActiveI18nWorkingManifestJson ?? CurrentManifestJson;
        if (string.IsNullOrWhiteSpace(manifestJson))
        {
            return null;
        }

        string? logicJs = CurrentLogicJs;
        CustomWidgetLocaleCatalog? catalog = ActiveI18nWorkingCatalog ?? CurrentLocales;
        string cacheKey =
            $"{CustomWidgetStudioSessionStore.ComputeManifestSha256(manifestJson!, logicJs, catalog)}|{_loc.CurrentCultureName}";
        if (CachedI18nSyncPlan is not null &&
            string.Equals(CachedI18nSyncPlanKey, cacheKey, StringComparison.Ordinal))
        {
            return CachedI18nSyncPlan;
        }

        try
        {
            CustomWidgetI18nSyncPlan plan = CustomWidgetI18nEngine.BuildSyncPlan(
                manifestJson!,
                logicJs,
                catalog,
                fallbackCulture: _loc.CurrentCultureName);
            CachedI18nSyncPlan = plan;
            CachedI18nSyncPlanKey = cacheKey;
            return plan;
        }
        catch (Exception ex)
        {
            _hostBridge.Log($"[CustomWidgetStudio] Build i18n sync plan failed: {ex.Message}");
            return null;
        }
    }

    internal void PersistI18nCheckpointDraft(
        string workingManifestJson,
        string? workingLogicJs,
        CustomWidgetLocaleCatalog snapshotCatalog)
    {
        if (_suppressDraftPersistOnExit || Committed)
        {
            return;
        }

        try
        {
            CustomWidgetLocaleCatalog? normalizedCatalog = snapshotCatalog.HasAnyLocale ? snapshotCatalog : null;
            string sha = CustomWidgetStudioSessionStore.ComputeManifestSha256(
                workingManifestJson,
                workingLogicJs,
                normalizedCatalog);
            List<StudioPersistedRevision> baseRevs = RevisionIndex >= 0 && RevisionIndex < Revisions.Count
                ? Revisions.Take(RevisionIndex + 1).ToList()
                : Revisions.ToList();

            var checkpointRev = new StudioPersistedRevision
            {
                RevisionId = $"rev-i18n-ckpt-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture)}",
                CreatedAtUtcTicks = DateTimeOffset.UtcNow.UtcTicks,
                Source = "ai",
                ChangeSummary = _loc.T("CustomWidget.Workshop.I18n.CheckpointSummary"),
                ManifestSha256 = sha,
                IsCommitted = false,
                ManifestJson = workingManifestJson,
                LogicJs = string.IsNullOrWhiteSpace(workingLogicJs) ? null : workingLogicJs,
                Locales = normalizedCatalog
            };

            if (baseRevs.Count > 0 && string.Equals(baseRevs[^1].ManifestSha256, sha, StringComparison.Ordinal))
            {
                baseRevs[^1] = checkpointRev;
            }
            else
            {
                baseRevs.Add(checkpointRev);
            }

            IReadOnlyList<StudioPersistedRevision> trimmed = CustomWidgetStudioSessionStore.TrimRevisions(baseRevs);
            var snapshot = new StudioDraftSnapshot
            {
                UpdatedAtUtcTicks = DateTimeOffset.UtcNow.UtcTicks,
                TargetWidgetId = TargetWidgetId,
                IsExternalImportSession = IsExternalImportSession,
                SelectedEmoji = SelectedEmoji ?? string.Empty,
                StartDraftIdea = StartDraftIdea,
                RefineDraftIdea = RefineDraftIdea,
                SelectedRevisionIndex = Math.Max(0, trimmed.Count - 1),
                BriefMarkdown = BriefMarkdown,
                PreviewSampleDataJson = PreviewSampleDataJson,
                PinnedCapabilityIds = PinnedCapabilityIds.ToList(),
                LoadedCapabilityPackIds = GetSortedLoadedCapabilityPackIds().ToList(),
                Revisions = trimmed,
                ChatMessages = ChatMessages.ToList()
            };

            if (IsEditMode && !string.IsNullOrWhiteSpace(TargetWidgetId))
            {
                _sessionStore.SaveWidgetDraft(TargetWidgetId!, snapshot);
            }
            else
            {
                _sessionStore.SaveCreateDraft(snapshot);
            }
        }
        catch (Exception ex)
        {
            _hostBridge.Log($"[CustomWidgetStudio] Persist i18n checkpoint draft failed: {ex.Message}");
        }
    }

    internal string? TryGetTargetWidgetIdForAgentUpsert(bool isWindowClosed)
    {
        if (isWindowClosed || string.IsNullOrWhiteSpace(TargetWidgetId))
        {
            return null;
        }

        if (Page == StudioPage.Preview && WaitingKind == WaitingKind.AgentMcp)
        {
            return TargetWidgetId;
        }

        return null;
    }

    private static bool IsUntrustedExternalOrigin(string? origin) =>
        string.Equals(origin, CustomWidgetOriginKinds.Community, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(origin, CustomWidgetOriginKinds.Agent, StringComparison.OrdinalIgnoreCase);

    internal string StampManifestProvenanceForCommit(string json)
    {
        CustomWidgetParseResult currentParsed = CustomWidgetManifestParser.Parse(json);
        CustomWidgetManifest? currentManifest = currentParsed.Manifest;

        string? forkedFromId = currentManifest?.ForkedFromId;
        string? forkedFromVersion = currentManifest?.ForkedFromVersion;
        string? createdAtUtc = currentManifest?.CreatedAtUtc;
        bool baseRevisionWasUntrustedOrigin = false;

        if (Revisions.FirstOrDefault() is { } firstRevision)
        {
            CustomWidgetParseResult baseParsed = CustomWidgetManifestParser.Parse(firstRevision.ManifestJson);
            if (baseParsed.Manifest is { } baseManifest)
            {
                baseRevisionWasUntrustedOrigin = IsUntrustedExternalOrigin(baseManifest.Origin);
                if (string.IsNullOrWhiteSpace(createdAtUtc) && !string.IsNullOrWhiteSpace(baseManifest.CreatedAtUtc))
                {
                    createdAtUtc = baseManifest.CreatedAtUtc;
                }

                if (string.IsNullOrWhiteSpace(forkedFromId))
                {
                    if (!string.IsNullOrWhiteSpace(baseManifest.ForkedFromId))
                    {
                        forkedFromId = baseManifest.ForkedFromId;
                        forkedFromVersion = string.IsNullOrWhiteSpace(forkedFromVersion)
                            ? baseManifest.ForkedFromVersion
                            : forkedFromVersion;
                    }
                    else if (baseManifest.Id.StartsWith("deskbox.", StringComparison.OrdinalIgnoreCase))
                    {
                        forkedFromId = baseManifest.Id;
                        forkedFromVersion = baseManifest.Version;
                    }
                }
            }
        }

        bool isExistingUntrustedEdit = IsEditMode &&
            !string.IsNullOrWhiteSpace(TargetWidgetId) &&
            !CustomWidgetTrustStore.Shared.GetProfile(TargetWidgetId, currentManifest?.Origin).TrustedLocalAuthoring;
        if (IsExternalImportSession ||
            isExistingUntrustedEdit ||
            baseRevisionWasUntrustedOrigin ||
            IsUntrustedExternalOrigin(currentManifest?.Origin))
        {
            return CustomWidgetManifestMetadataStamper.StampProvenance(
                json,
                origin: CustomWidgetOriginKinds.Community,
                isCustomized: (currentManifest?.IsCustomized ?? false) || AiRevisionCount > 0 || Revisions.Count > 1,
                forkedFromId: forkedFromId,
                forkedFromVersion: forkedFromVersion,
                createdAtUtc: createdAtUtc);
        }

        if (AiRevisionCount > 0)
        {
            return CustomWidgetManifestMetadataStamper.StampProvenance(
                json,
                origin: CustomWidgetOriginKinds.Ai,
                isCustomized: true,
                forkedFromId: forkedFromId,
                forkedFromVersion: forkedFromVersion,
                createdAtUtc: createdAtUtc);
        }

        if (currentManifest is not null &&
            Revisions.Count <= 1 &&
            string.Equals(currentManifest.Origin, CustomWidgetOriginKinds.Official, StringComparison.OrdinalIgnoreCase) &&
            currentManifest.Id.StartsWith("deskbox.", StringComparison.OrdinalIgnoreCase) &&
            !currentManifest.IsCustomized)
        {
            return CustomWidgetManifestMetadataStamper.StampProvenance(
                json,
                origin: CustomWidgetOriginKinds.Official,
                isCustomized: false,
                createdAtUtc: createdAtUtc);
        }

        string fallbackOrigin =
            currentManifest is not null &&
            !string.Equals(currentManifest.Origin, CustomWidgetOriginKinds.Official, StringComparison.OrdinalIgnoreCase)
                ? currentManifest.Origin
                : CustomWidgetOriginKinds.Local;
        bool isCustomized = (currentManifest?.IsCustomized ?? false) || Revisions.Count > 1;
        return CustomWidgetManifestMetadataStamper.StampProvenance(
            json,
            origin: fallbackOrigin,
            isCustomized: isCustomized,
            forkedFromId: forkedFromId,
            forkedFromVersion: forkedFromVersion,
            createdAtUtc: createdAtUtc);
    }

    internal void ResetState()
    {
        _suppressDraftPersistOnExit = false;
        TargetWidgetId = null;
        WaitingKind = WaitingKind.None;
        Revisions.Clear();
        ChatMessages.Clear();
        PinnedCapabilityIds.Clear();
        LoadedCapabilityPackIds.Clear();
        BriefMarkdown = string.Empty;
        PreviewSampleDataJson = null;
        IsSampleDataMutedInPreview = false;
        PendingRecoveryDraft = null;
        WasReconciledExternalOnOpen = false;
        SelectedTargetNode = null;
        IsInspectorModeEnabled = false;
        IsCapsulePreviewMode = false;
        LatestHealthReport = null;
        RevisionIndex = -1;
        AiRevisionCount = 0;
        UncommittedEditCount = 0;
        HasDirtySettingOverrides = false;
        HasDirtyChromeModeOverride = false;
        StageChromeMode = WidgetChromeMode.System;
        Committed = false;
        EditBaselineStorePayload = null;
        SelectedEmoji = null;
        StartDraftIdea = string.Empty;
        RefineDraftIdea = string.Empty;
        PendingPrompt = string.Empty;
        PendingDisplayPrompt = null;
        PendingChannelSelectionIdea = null;
        PendingAgentStageId = null;
        PendingAgentStageManifest = null;
        IsCommittingPendingAgentStage = false;
        LastBrokenJson = null;
        LastBrokenLogicJs = null;
        LastDiagnostics = [];
        LiveRuntimeScriptError = null;
        PendingBriefResetUpdate = null;
        LiveRuntimeScriptErrorLine = null;
        TargetWidgetName = null;
        IsInPlaceGenerating = false;
        IsI18nQueueRunning = false;
        IsLocaleCenterOpen = false;
        ActivePreviewCultureOverride = null;
        ActiveI18nWorkingManifestJson = null;
        ActiveI18nWorkingCatalog = null;
        InvalidateI18nSyncPlanCache();
        LiveLocaleJobUpdates.Clear();
        LastFailedLocaleErrors.Clear();
        InFlightTargetNodeLabel = null;
        InFlightDetectedWidgetName = string.Empty;
        SessionSettingOverrides.Clear();
        SessionSecretOverrides.Clear();
        IsExternalImportSession = false;
        HasPromptedFirstRunSetupInSession = false;
    }

    internal void HydrateExistingWidgetSettingsFromDisk(string widgetId, CustomWidgetManifest manifest)
    {
        try
        {
            CustomWidgetDataStore store = CustomWidgetDataStore.ForWidget(widgetId);
            if (File.Exists(store.StorePath))
            {
                using JsonDocument doc = JsonDocument.Parse(
                    File.ReadAllText(store.StorePath, Encoding.UTF8));
                if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                    doc.RootElement.TryGetProperty("settings", out JsonElement settingsEl) &&
                    settingsEl.ValueKind == JsonValueKind.Object)
                {
                    foreach (JsonProperty prop in settingsEl.EnumerateObject())
                    {
                        if (prop.Value.ValueKind == JsonValueKind.String &&
                            prop.Value.GetString() is { } strVal)
                        {
                            SessionSettingOverrides[prop.Name] = strVal;
                        }
                    }
                }
            }

            foreach (CustomWidgetSettingDefinition def in manifest.Settings)
            {
                if (string.Equals(def.Type, "secret", StringComparison.OrdinalIgnoreCase))
                {
                    string existingSecret = CustomWidgetSecretStore.Shared.GetSecret(widgetId, def.Key);
                    if (!string.IsNullOrWhiteSpace(existingSecret))
                    {
                        SessionSecretOverrides[def.Key] = existingSecret;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _hostBridge.Log($"[CustomWidgetStudio] Hydrate existing settings failed: {ex.Message}");
        }
    }

    internal (Dictionary<string, string>? Settings, Dictionary<string, string>? Secrets) ExtractValidPreviewSettingOverrides(
        string committedManifestJson)
    {
        if (SessionSettingOverrides.Count == 0 && SessionSecretOverrides.Count == 0)
        {
            return (null, null);
        }

        CustomWidgetManifest? manifest = CustomWidgetManifestParser.Parse(committedManifestJson).Manifest;
        if (manifest is null || manifest.Settings.Count == 0)
        {
            return (null, null);
        }

        var validRegularKeys = new HashSet<string>(
            manifest.Settings
                .Where(static d => !string.Equals(d.Type, "secret", StringComparison.OrdinalIgnoreCase))
                .Select(static d => d.Key),
            StringComparer.Ordinal);
        var validSecretKeys = new HashSet<string>(
            manifest.Settings
                .Where(static d => string.Equals(d.Type, "secret", StringComparison.OrdinalIgnoreCase))
                .Select(static d => d.Key),
            StringComparer.Ordinal);

        Dictionary<string, string>? settings = null;
        foreach ((string key, string val) in SessionSettingOverrides)
        {
            if (validRegularKeys.Contains(key))
            {
                settings ??= new Dictionary<string, string>(StringComparer.Ordinal);
                settings[key] = val;
            }
        }

        Dictionary<string, string>? secrets = null;
        foreach ((string secretKey, string secretVal) in SessionSecretOverrides)
        {
            if (validSecretKeys.Contains(secretKey))
            {
                secrets ??= new Dictionary<string, string>(StringComparer.Ordinal);
                secrets[secretKey] = secretVal;
            }
        }

        return (settings, secrets);
    }

    internal async Task PersistPreviewSettingOverridesToWidgetAsync(
        string widgetId,
        string committedManifestJson,
        object? saveOrigin = null)
    {
        try
        {
            (Dictionary<string, string>? settings, Dictionary<string, string>? secrets) =
                ExtractValidPreviewSettingOverrides(committedManifestJson);
            bool syncChromeMode = HasDirtyChromeModeOverride || StageChromeMode != WidgetChromeMode.System;
            if (settings is null && secrets is null && !syncChromeMode)
            {
                return;
            }

            if (_hostBridge.HasWidgetManager)
            {
                await _hostBridge.ApplyCustomWidgetMutationAsync(
                    new CustomWidgetSettingsMutation(
                        widgetId,
                        Settings: settings,
                        Secrets: secrets,
                        ChromeModeOverride: syncChromeMode ? WidgetChromeModeNames.ToSettingValue(StageChromeMode) : null,
                        UpdateChromeMode: syncChromeMode));
                HasDirtyChromeModeOverride = false;
                return;
            }

            if (secrets is not null)
            {
                foreach ((string secretKey, string secretVal) in secrets)
                {
                    CustomWidgetSecretStore.Shared.SetSecret(widgetId, secretKey, secretVal);
                }
            }

            if (settings is not null)
            {
                CustomWidgetDataStore store = CustomWidgetDataStore.ForWidget(widgetId);
                CustomWidgetStorePayload latest = await store.ReloadFromDiskAsync();
                foreach ((string key, string val) in settings)
                {
                    latest.Settings[key] = val;
                }
                await store.SaveAsync(latest, origin: saveOrigin ?? this);
            }
        }
        catch (Exception ex)
        {
            _hostBridge.Log($"[CustomWidgetStudio] Persist preview settings on commit failed: {ex.Message}");
        }
    }
}
