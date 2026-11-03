using System.Diagnostics;
using System.Globalization;
using System.Text;
using DeskBox.Controls.WidgetContents;
using DeskBox.Helpers;
using DeskBox.Models;
using DeskBox.Platform;
using DeskBox.Services;
using DeskBox.ViewModels;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics;
using Windows.System;
using WinRT.Interop;

namespace DeskBox.Views;

/// <summary>
/// Standalone "Widget Workshop" window that owns the whole AI co-creation flow
/// (pick a starting point → generate → multi-turn preview &amp; refine → put on desktop).
/// Persists per-widget <c>brief.md</c>, <c>revisions.json</c>, <c>studio-chat.json</c>, and
/// uncommitted recovery drafts via <see cref="CustomWidgetStudioSessionStore"/>.
/// </summary>
public sealed partial class CustomWidgetStudioWindow : Window
{
    private const int DesiredWidth = 1200;
    private const int DesiredHeight = 820;
    private const int MinimumWidth = 760;
    private const int MinimumHeight = 560;
    private const int WorkAreaMargin = 64;

    private readonly IntPtr _hWnd;
    private readonly AppWindow _appWindow;
    private readonly LocalizationService _loc;
    private readonly CustomWidgetStudioPreferencesStore _prefs = CustomWidgetStudioPreferencesStore.Shared;
    private readonly CustomWidgetStudioSessionStore _sessionStore = CustomWidgetStudioSessionStore.Shared;

    private bool _allowClose;
    private bool _isClosed;

    // ── Session state ───────────────────────────────────────────────────
    private string? _targetWidgetId;
    private string? _targetWidgetName;
    private string? _selectedEmoji;
    private readonly List<StudioPersistedRevision> _revisions = [];
    private readonly List<StudioChatMessage> _chatMessages = [];
    private readonly List<string> _pinnedCapabilityIds = [];
    private Action? _refreshActiveComposerCapabilityChrome;
    private Action? _refreshApiDocsToggleButtons;
    private CustomWidgetApiDocsDialogHelper.ApiReferenceViewHandle? _apiDocsDrawerHandle;
    private bool _isApiDocsDrawerOpen;
    private double _preferredApiDrawerWidth = 488;
    private TextBox? _startIdeaTextBox;
    private string _briefMarkdown = string.Empty;
    private string? _previewSampleDataJson;
    private StudioDraftSnapshot? _pendingRecoveryDraft;
    private bool _wasReconciledExternalOnOpen;
    private CustomWidgetPickedNode? _selectedTargetNode;
    private bool _isInspectorModeEnabled;
    private StageBackdropMode _stageBackdropMode;
    private readonly bool _hasSystemWallpaperAvailable;
    private bool _isSampleDataMutedInPreview;
    private CustomWidgetHealthReport? _latestHealthReport;

    private int _revisionIndex = -1;
    private int _aiRevisionCount;
    private int _uncommittedEditCount;
    private bool _committed;
    private string _startDraftIdea = string.Empty;
    private string _refineDraftIdea = string.Empty;
    private string _pendingPrompt = string.Empty;
    private bool _preferWebAiInSession;
    private string? _lastBrokenJson;
    private IReadOnlyList<CustomWidgetValidationDiagnostic> _lastDiagnostics = [];

    private StudioPage _page = StudioPage.Start;
    private StudioPage _pageBeforeWaiting = StudioPage.Start;
    private WaitingKind _waitingKind = WaitingKind.Web;
    private WebAiTargetPreset? _activeWebPreset;
    private bool _isInPlaceGenerating;
    private bool _isWebAiRepairFlow;
    private string? _inFlightTargetNodeLabel;
    private string _inFlightDetectedWidgetName = string.Empty;
    private bool _showRawStreamCodeInSession;
    private AiGenerationStage _inFlightStage = AiGenerationStage.Connecting;

    private CancellationTokenSource? _generationCts;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _clipboardPollTimer;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _waitingElapsedTimer;
    private uint _clipboardSequence;
    private bool _clipboardReadInFlight;
    private bool _isDialogOpen;
    private string? _lastSelfCopiedText;

    private FileSystemWatcher? _codeToolWatcher;
    private CancellationTokenSource? _codeToolDebounceCts;
    private long _lastDraftWriteTick;

    public CustomWidgetStudioWindow(LocalizationService localization)
    {
        _loc = localization;
        InitializeComponent();

        if (WindowsCompatibilityService.ApplySafeBackdrop(this) != "Solid")
        {
            RootGrid.Background = null;
        }

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);

        _hWnd = WindowNative.GetWindowHandle(this);
        WindowId windowId = Win32Interop.GetWindowIdFromWindow(_hWnd);
        _appWindow = AppWindow.GetFromWindowId(windowId);
        AppBranding.ApplyWindowIcon(_appWindow);

        if (_appWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsResizable = true;
            presenter.IsMaximizable = true;
            presenter.IsMinimizable = true;
            presenter.PreferredMinimumWidth = MinimumWidth;
            presenter.PreferredMinimumHeight = MinimumHeight;
        }

        _hasSystemWallpaperAvailable = DesktopWallpaperReadOps.TryResolveCurrentWallpaperImagePath() is not null;
        _stageBackdropMode = _hasSystemWallpaperAvailable
            ? StageBackdropMode.Wallpaper
            : (Win32Helper.IsSystemDarkMode() ? StageBackdropMode.Dark : StageBackdropMode.Light);

        _appWindow.Closing += AppWindow_Closing;
        AppTitleBar.ActualThemeChanged += AppTitleBar_ActualThemeChanged;
        Closed += CustomWidgetStudioWindow_Closed;
        ResizeAndCenter(windowId);
        ApplyTitleBarColors();
        _ = EnsureWallpaperLoadedAsync();
    }

    public IntPtr WindowHandle => _hWnd;

    /// <summary>True while the user has uncommitted changes in the current Studio session.</summary>
    public bool HasUnsavedWork => !_committed && _uncommittedEditCount > 0;

    private bool IsEditMode => !string.IsNullOrWhiteSpace(_targetWidgetId);

    private string? CurrentManifestJson =>
        _revisionIndex >= 0 && _revisionIndex < _revisions.Count
            ? _revisions[_revisionIndex].ManifestJson
            : null;

    /// <summary>
    /// Starts a fresh session. <paramref name="widgetId"/> opens an existing
    /// widget for AI co-creation; <paramref name="initialIdea"/> immediately starts
    /// generating from a sentence typed elsewhere (for example in Settings).
    /// </summary>
    public void Initialize(string? widgetId, string? initialIdea)
    {
        ResetSession();
        _targetWidgetId = string.IsNullOrWhiteSpace(widgetId) ? null : widgetId.Trim();

        if (IsEditMode)
        {
            _activeStartGalleryMode = StartGallerySourceMode.MyWidgets;
            string? existingJson = TryReadWidgetManifest(_targetWidgetId!);
            _targetWidgetName = App.Current.WidgetManager?.GetCustomWidgetInstances()
                .FirstOrDefault(i => string.Equals(i.Id, _targetWidgetId, StringComparison.Ordinal))?.Name;
            if (string.IsNullOrWhiteSpace(existingJson))
            {
                _targetWidgetId = null;
            }
            else
            {
                CustomWidgetParseResult parsed = CustomWidgetManifestParser.Parse(existingJson);
                if (parsed.Manifest is { } manifest)
                {
                    if (CustomWidgetTemplates.IsEmojiIcon(manifest.Icon))
                    {
                        _selectedEmoji = manifest.Icon.Trim();
                    }

                    if (string.IsNullOrWhiteSpace(_targetWidgetName))
                    {
                        _targetWidgetName = manifest.ResolveLocalizedName(_loc.CurrentCultureName);
                    }
                }

                StudioLoadedSession loaded = _sessionStore.LoadOrReconcileWidgetSession(
                    _targetWidgetId!,
                    existingJson,
                    _loc.T("CustomWidget.Workshop.CoCreate.ExternalSyncSummary"));

                _briefMarkdown = loaded.BriefMarkdown;
                _revisions.AddRange(loaded.Revisions);
                if (_revisions.Count == 0)
                {
                    AddRevision(
                        existingJson,
                        _loc.T("CustomWidget.Workshop.History.Current"),
                        isAiChange: false,
                        source: "template",
                        isCommitted: true,
                        markDirty: false);
                }
                else
                {
                    _revisionIndex = _revisions.Count - 1;
                }

                _chatMessages.AddRange(loaded.ChatMessages);
                _pendingRecoveryDraft = loaded.PendingDraft;
                _wasReconciledExternalOnOpen = loaded.WasReconciledWithExternalEdit;
            }
        }
        else if (string.IsNullOrWhiteSpace(initialIdea))
        {
            _pendingRecoveryDraft = _sessionStore.TryLoadCreateDraft();
        }

        UpdateWindowTitle();
        CustomWidgetRemoteCatalogService.Shared.CatalogUpdated -= OnRemoteCatalogUpdated;
        CustomWidgetRemoteCatalogService.Shared.CatalogUpdated += OnRemoteCatalogUpdated;
        _ = CustomWidgetRemoteCatalogService.Shared.RefreshInBackgroundAsync();
        _ = CustomWidgetRemotePromptService.Shared.RefreshInBackgroundAsync();

        if (IsEditMode)
        {
            ShowPreviewPage(justLoaded: false);
            if (!string.IsNullOrWhiteSpace(initialIdea))
            {
                _ = BeginGenerationAsync(initialIdea);
            }

            return;
        }

        ShowStartPage(initialIdea);
        if (!string.IsNullOrWhiteSpace(initialIdea))
        {
            _ = BeginGenerationAsync(initialIdea);
        }
    }

    private void OnRemoteCatalogUpdated(object? sender, EventArgs e)
    {
        if (_isClosed)
        {
            return;
        }

        DispatcherQueue.TryEnqueue(() =>
        {
            if (!_isClosed && _page == StudioPage.Start && !HasUnsavedWork)
            {
                ShowStartPage(_startDraftIdea);
            }
        });
    }

    public void OpenExternalManifestPreview(string manifestJson, string? sourceLabel = null)
    {
        if (string.IsNullOrWhiteSpace(manifestJson) || HasUnsavedWork)
        {
            return;
        }

        ResetSession();
        _targetWidgetId = null;
        UpdateWindowTitle();

        manifestJson = CustomWidgetManifestMetadataStamper.StampProvenance(
            manifestJson,
            origin: CustomWidgetOriginKinds.Community,
            isCustomized: false);

        CustomWidgetParseResult parsed = CustomWidgetManifestParser.Parse(manifestJson);
        if (parsed.Manifest is not null && CustomWidgetTemplates.IsEmojiIcon(parsed.Manifest.Icon))
        {
            _selectedEmoji = parsed.Manifest.Icon.Trim();
        }

        string label = !string.IsNullOrWhiteSpace(sourceLabel)
            ? sourceLabel!
            : parsed.Manifest?.ResolveLocalizedName(_loc.CurrentCultureName) ?? _loc.T("CustomWidget.Title");
        AddRevision(
            manifestJson,
            _loc.Format("CustomWidget.Workshop.History.FromTemplate", label),
            isAiChange: false,
            source: "template",
            isCommitted: false,
            markDirty: false);
        ShowPreviewPage(justLoaded: true);
    }

    /// <summary>
    /// Re-targets an already open window. Unsaved AI work is never thrown away
    /// silently: the window is just brought to the front instead.
    /// </summary>
    public void RequestTarget(string? widgetId, string? initialIdea)
    {
        bool sameTarget = string.Equals(
            string.IsNullOrWhiteSpace(widgetId) ? null : widgetId.Trim(),
            _targetWidgetId,
            StringComparison.Ordinal);
        if (HasUnsavedWork || (sameTarget && string.IsNullOrWhiteSpace(initialIdea) && _page != StudioPage.Start))
        {
            return;
        }

        Initialize(widgetId, initialIdea);
    }

    public void ShowWindow()
    {
        _appWindow.Show();
        App.Current.WidgetManager?.BringAuxiliaryWindowToFront(_hWnd, "custom-widget-studio-shown");
        Activate();
        _ = Win32Helper.SetForegroundWindow(_hWnd);
    }

    public void CloseForShutdown()
    {
        PersistActiveDraftIfDirty();
        _allowClose = true;
        Close();
    }

    // ── Revisions & Co-Creation Session Persistence ──────────────────────

    private StudioPersistedRevision AddRevision(
        string manifestJson,
        string label,
        bool isAiChange,
        string source = "ai",
        bool isCommitted = false,
        bool markDirty = true)
    {
        if (_revisionIndex >= 0 && _revisionIndex < _revisions.Count - 1)
        {
            _revisions.RemoveRange(_revisionIndex + 1, _revisions.Count - _revisionIndex - 1);
        }

        string sha = CustomWidgetStudioSessionStore.ComputeManifestSha256(manifestJson);
        if (_revisions.Count > 0 &&
            string.Equals(_revisions[^1].ManifestSha256, sha, StringComparison.Ordinal))
        {
            _revisionIndex = _revisions.Count - 1;
            return _revisions[^1];
        }

        var rev = new StudioPersistedRevision
        {
            RevisionId = $"rev-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture)}-{_revisions.Count + 1}",
            CreatedAtUtcTicks = DateTimeOffset.UtcNow.UtcTicks,
            Source = source,
            ChangeSummary = string.IsNullOrWhiteSpace(label)
                ? _loc.T("CustomWidget.Workshop.History.AiChange")
                : label,
            ManifestSha256 = sha,
            IsCommitted = isCommitted,
            ManifestJson = manifestJson
        };
        _revisions.Add(rev);

        IReadOnlyList<StudioPersistedRevision> trimmed = CustomWidgetStudioSessionStore.TrimRevisions(_revisions);
        if (trimmed.Count != _revisions.Count)
        {
            _revisions.Clear();
            _revisions.AddRange(trimmed);
        }

        _revisionIndex = _revisions.Count - 1;
        if (isAiChange)
        {
            _aiRevisionCount++;
        }

        if (markDirty && !isCommitted)
        {
            _uncommittedEditCount++;
        }

        return rev;
    }

    private void AppendChatMessage(StudioChatMessage message)
    {
        _chatMessages.Add(message);
        IReadOnlyList<StudioChatMessage> trimmed = CustomWidgetStudioSessionStore.TrimChatMessages(_chatMessages);
        if (trimmed.Count != _chatMessages.Count)
        {
            _chatMessages.Clear();
            _chatMessages.AddRange(trimmed);
        }
    }

    private void MergeTurnBriefUpdates(StudioBriefUpdateSpec? updates, CustomWidgetManifest? newManifest, string? userPrompt)
    {
        CustomWidgetDesignBrief current = CustomWidgetStudioSessionStore.ParseBriefMarkdown(_briefMarkdown);
        bool hadEmptyBrief = current.IsEmpty;

        if (updates is { HasAnyChange: true })
        {
            current = CustomWidgetStudioSessionStore.ApplyBriefUpdates(current, updates);
        }
        else if (hadEmptyBrief && newManifest is not null)
        {
            string seedPurpose = !string.IsNullOrWhiteSpace(newManifest.Description)
                ? newManifest.Description
                : (!string.IsNullOrWhiteSpace(userPrompt) ? userPrompt! : newManifest.Name);
            current = current with { Purpose = seedPurpose };
        }

        if (!current.IsEmpty)
        {
            _briefMarkdown = CustomWidgetStudioSessionStore.FormatBriefMarkdown(
                current,
                preferEnglish: !Localized.IsActiveChinese);
        }
    }

    private void PersistActiveDraftIfDirty()
    {
        if (_committed || (_uncommittedEditCount <= 0 && _chatMessages.Count == 0))
        {
            return;
        }

        try
        {
            var snapshot = new StudioDraftSnapshot
            {
                UpdatedAtUtcTicks = DateTimeOffset.UtcNow.UtcTicks,
                TargetWidgetId = _targetWidgetId,
                SelectedEmoji = _selectedEmoji ?? string.Empty,
                StartDraftIdea = _startDraftIdea,
                RefineDraftIdea = _refineDraftIdea,
                SelectedRevisionIndex = Math.Max(0, _revisionIndex),
                BriefMarkdown = _briefMarkdown,
                PreviewSampleDataJson = _previewSampleDataJson,
                Revisions = _revisions.ToList(),
                ChatMessages = _chatMessages.ToList()
            };

            if (IsEditMode && !string.IsNullOrWhiteSpace(_targetWidgetId))
            {
                _sessionStore.SaveWidgetDraft(_targetWidgetId!, snapshot);
            }
            else
            {
                _sessionStore.SaveCreateDraft(snapshot);
            }
        }
        catch (Exception ex)
        {
            App.Log($"[CustomWidgetStudio] Persist draft failed: {ex.Message}");
        }
    }

    private void RestoreFromDraftSnapshot(StudioDraftSnapshot draft)
    {
        _pendingRecoveryDraft = null;
        _wasReconciledExternalOnOpen = false;
        if (!string.IsNullOrWhiteSpace(draft.SelectedEmoji))
        {
            _selectedEmoji = draft.SelectedEmoji;
        }

        _startDraftIdea = draft.StartDraftIdea;
        _refineDraftIdea = draft.RefineDraftIdea;
        _briefMarkdown = draft.BriefMarkdown;
        _previewSampleDataJson = draft.PreviewSampleDataJson;

        _revisions.Clear();
        _revisions.AddRange(draft.Revisions);
        _revisionIndex = _revisions.Count > 0
            ? Math.Clamp(draft.SelectedRevisionIndex, 0, _revisions.Count - 1)
            : -1;

        _chatMessages.Clear();
        _chatMessages.AddRange(draft.ChatMessages);

        _aiRevisionCount = Math.Max(1, _revisions.Count(static r => string.Equals(r.Source, "ai", StringComparison.OrdinalIgnoreCase)));
        _uncommittedEditCount = Math.Max(1, _revisions.Count(static r => !r.IsCommitted));

        if (_revisions.Count > 0)
        {
            ShowPreviewPage(justLoaded: true);
        }
        else
        {
            ShowStartPage(_startDraftIdea);
        }
    }

    private void DiscardActiveDraftCleanly()
    {
        _pendingRecoveryDraft = null;
        if (IsEditMode && !string.IsNullOrWhiteSpace(_targetWidgetId))
        {
            _sessionStore.DiscardWidgetDraft(_targetWidgetId!);
        }
        else
        {
            _sessionStore.DiscardCreateDraft();
        }
    }

    private void ApplyZeroTokenMetadataEdit(
        string? newName = null,
        string? newIcon = null,
        CustomWidgetSizeSpec? newSize = null)
    {
        string? current = CurrentManifestJson;
        if (string.IsNullOrWhiteSpace(current))
        {
            return;
        }

        string? normalizedIcon = !string.IsNullOrWhiteSpace(newIcon) ? newIcon!.Trim() : null;
        if (!string.IsNullOrWhiteSpace(normalizedIcon) && CustomWidgetTemplates.IsEmojiIcon(normalizedIcon))
        {
            _selectedEmoji = normalizedIcon;
        }

        string updated = CustomWidgetManifestCompatibility.WithUpdatedMetadataJson(
            current,
            newName: !string.IsNullOrWhiteSpace(newName) ? newName!.Trim() : null,
            newIcon: normalizedIcon,
            newDefaultSize: newSize,
            activeCulture: _loc.CurrentCultureName);
        updated = CustomWidgetManifestCompatibility.StampRequiredMinAppVersion(updated);

        if (string.Equals(updated, current, StringComparison.Ordinal))
        {
            return;
        }

        AddRevision(
            updated,
            _loc.T("CustomWidget.Workshop.CoCreate.ManualQuickEdit"),
            isAiChange: false,
            source: "manual",
            isCommitted: false,
            markDirty: true);
        PersistActiveDraftIfDirty();
        ShowPreviewPage(justLoaded: false);
    }

    private CustomWidgetHealthReport? BuildCurrentHealthReport(string? manifestJson)
    {
        if (string.IsNullOrWhiteSpace(manifestJson))
        {
            return null;
        }

        CustomWidgetParseResult parsed = CustomWidgetManifestParser.Parse(manifestJson);
        if (parsed.Manifest is null)
        {
            return null;
        }

        CustomWidgetStorePayload? storeSnapshot = BuildLiveOrPreviewStoreSnapshot();
        double measuredW = 0;
        double measuredH = 0;
        if (_previewContent is not null)
        {
            var natural = _previewContent.MeasureNaturalContentSize(
                Math.Max(220, parsed.Manifest.DefaultSize.Width));
            measuredW = natural.MeasuredWidth;
            measuredH = natural.MeasuredHeight;
        }

        return CustomWidgetHealthAnalyzer.AnalyzeHealth(
            rawManifestJson: manifestJson,
            manifest: parsed.Manifest,
            storePayload: storeSnapshot,
            measuredWidth: measuredW,
            measuredHeight: measuredH,
            isChinese: Localized.IsActiveChinese);
    }

    private CustomWidgetStorePayload? BuildLiveOrPreviewStoreSnapshot()
    {
        if (_previewVm is null)
        {
            return null;
        }

        return new CustomWidgetStorePayload
        {
            Settings = new Dictionary<string, string>(_previewVm.Settings, StringComparer.Ordinal),
            State = new Dictionary<string, string>(_previewVm.State, StringComparer.Ordinal),
            Collections = _previewVm.Collections.ToDictionary(
                static kvp => kvp.Key,
                static kvp => kvp.Value.ToList(),
                StringComparer.Ordinal)
        };
    }

    private (CustomWidgetParseResult Result, string NormalizedJson) ValidateCandidate(string rawJson)
    {
        string normalized = rawJson;
        if (_revisions.Count == 0 && CustomWidgetTemplates.IsEmojiIcon(_selectedEmoji))
        {
            try
            {
                normalized = CustomWidgetTemplates.WithUpdatedIconJson(normalized, _selectedEmoji!.Trim());
            }
            catch
            {
            }
        }

        try
        {
            normalized = CustomWidgetManifestCompatibility.StampRequiredMinAppVersion(normalized);
        }
        catch
        {
        }

        return (CustomWidgetManifestParser.ValidateWithRuntimeHints(normalized), normalized);
    }

    private static string? FormatPickedNodeDisplayLabel(CustomWidgetPickedNode? picked)
    {
        if (picked is null)
        {
            return null;
        }

        return string.IsNullOrWhiteSpace(picked.DisplayLabel)
            ? picked.NodeType
            : $"{picked.NodeType} · {picked.DisplayLabel}";
    }

    /// <summary>
    /// Applies an AI reply (from the clipboard or external code tool).
    /// Supports both bare <c>manifest.json</c> and structured co-creation envelopes.
    /// </summary>
    private bool HandleManifestCandidate(string rawJson, string? prompt, string? fallbackLabel, string source = "ai")
    {
        string? prevJson = CurrentManifestJson;
        string? targetNodePath = _selectedTargetNode?.NodePath;
        string? targetNodeLabel = _inFlightTargetNodeLabel ?? FormatPickedNodeDisplayLabel(_selectedTargetNode);

        StudioTurnResult parsedEnvelope = CustomWidgetAiGeneratorService.ParseStudioTurnResponse(
            rawJson,
            preferredEmoji: _revisions.Count == 0 ? _selectedEmoji : null);

        if (parsedEnvelope.Success && !string.Equals(parsedEnvelope.Kind, "edit", StringComparison.OrdinalIgnoreCase))
        {
            StopClipboardPolling();
            StopWaitingElapsedTimer();
            _isInPlaceGenerating = false;
            _isWebAiRepairFlow = false;
            _inFlightTargetNodeLabel = null;
            _inFlightDetectedWidgetName = string.Empty;

            if (!string.IsNullOrWhiteSpace(prompt))
            {
                AppendChatMessage(new StudioChatMessage
                {
                    Id = $"msg-u-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}",
                    Role = "user",
                    Kind = "prompt",
                    Text = prompt!,
                    TargetNodePath = targetNodePath,
                    TargetNodeLabel = targetNodeLabel
                });
            }

            MergeTurnBriefUpdates(parsedEnvelope.BriefUpdates, null, prompt);
            AppendChatMessage(new StudioChatMessage
            {
                Id = $"msg-a-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}",
                Role = "assistant",
                Kind = parsedEnvelope.Kind,
                Text = parsedEnvelope.Reply,
                Suggestions = parsedEnvelope.Suggestions
            });
            _selectedTargetNode = null;
            if (_previewContent is not null)
            {
                _previewContent.SelectedNodePath = null;
            }
            _refineDraftIdea = string.Empty;
            PersistActiveDraftIfDirty();

            if (_page == StudioPage.Preview)
            {
                RefreshInPlaceGenerationChrome();
                RefreshBriefButtonChrome();
                RefreshTargetNodePillChrome();
                RefreshSuggestionChips();
            }
            else
            {
                ShowPreviewPage(justLoaded: false);
            }

            return true;
        }

        string candidateJson = !string.IsNullOrWhiteSpace(parsedEnvelope.ManifestJson)
            ? parsedEnvelope.ManifestJson
            : rawJson;

        (CustomWidgetParseResult parsed, string normalized) = ValidateCandidate(candidateJson);
        bool hasErrors = parsed.Manifest is null || parsed.Diagnostics.Any(static d => d.IsError);
        if (hasErrors)
        {
            _lastBrokenJson = normalized;
            _lastDiagnostics = parsed.Diagnostics;
            CustomWidgetValidationDiagnostic? first = parsed.Diagnostics.FirstOrDefault(static d => d.IsError);
            string techLine = _loc.Format(
                "CustomWidget.Studio.Diag.TechReasonLine",
                parsed.Diagnostics.Count(static d => d.IsError),
                first?.Message ?? "manifest.json");

            if (_isInPlaceGenerating && _waitingKind == WaitingKind.Web)
            {
                ShowPageMessage(
                    InfoBarSeverity.Error,
                    _loc.T("CustomWidget.Workshop.Diag.ErrorTitle"),
                    _loc.T("CustomWidget.Workshop.Diag.ErrorBodyWaiting") + "\n" + techLine,
                    CreateRepairButton());
                return false;
            }

            StopWaitingElapsedTimer();
            _isInPlaceGenerating = false;
            _isWebAiRepairFlow = false;
            if (_page == StudioPage.Preview)
            {
                RefreshInPlaceGenerationChrome();
            }
            ShowPageMessage(
                InfoBarSeverity.Error,
                _loc.T("CustomWidget.Workshop.Diag.ErrorTitle"),
                _loc.T("CustomWidget.Workshop.Diag.ErrorBody") + "\n" + techLine,
                CreateRepairButton());
            return false;
        }

        bool cameFromWebAi = _isInPlaceGenerating && _waitingKind == WaitingKind.Web;
        StopClipboardPolling();
        StopWaitingElapsedTimer();
        _isInPlaceGenerating = false;
        _isWebAiRepairFlow = false;
        _inFlightTargetNodeLabel = null;
        _inFlightDetectedWidgetName = string.Empty;

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
            cameFromWebAi: cameFromWebAi);
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
        bool cameFromWebAi = false)
    {
        if (!string.IsNullOrWhiteSpace(prompt))
        {
            AppendChatMessage(new StudioChatMessage
            {
                Id = $"msg-u-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}",
                Role = "user",
                Kind = "prompt",
                Text = prompt!,
                TargetNodePath = targetNodePath,
                TargetNodeLabel = targetNodeLabel
            });
        }

        string label = !string.IsNullOrWhiteSpace(changeSummary)
            ? changeSummary!
            : (!string.IsNullOrWhiteSpace(prompt)
                ? CustomWidgetWorkshopFlow.SummarizePrompt(prompt, maxLength: 64)
                : fallbackLabel ?? _loc.T("CustomWidget.Workshop.History.AiChange"));

        CustomWidgetManifest? prevManifest = !string.IsNullOrWhiteSpace(previousManifestJson)
            ? CustomWidgetManifestParser.Parse(previousManifestJson!).Manifest
            : null;
        IReadOnlyList<string> diffPills = parsed.Manifest is not null
            ? CustomWidgetHealthAnalyzer.FormatDiffPills(
                CustomWidgetHealthAnalyzer.ComputeStructuralDiff(prevManifest, parsed.Manifest),
                Localized.IsActiveChinese)
            : [];

        bool isAi = string.Equals(source, "ai", StringComparison.OrdinalIgnoreCase);
        StudioPersistedRevision rev = AddRevision(
            normalizedManifestJson,
            label,
            isAiChange: isAi,
            source: source,
            isCommitted: false,
            markDirty: true);

        if (!IsEditMode && !string.IsNullOrWhiteSpace(sampleDataJson))
        {
            _previewSampleDataJson = sampleDataJson;
            _isSampleDataMutedInPreview = false;
        }

        MergeTurnBriefUpdates(briefUpdates, parsed.Manifest, prompt);
        _selectedTargetNode = null;
        if (_previewContent is not null)
        {
            _previewContent.SelectedNodePath = null;
        }
        _refineDraftIdea = string.Empty;
        _pendingPrompt = string.Empty;

        string assistantText = !string.IsNullOrWhiteSpace(replyText)
            ? replyText!
            : label;
        AppendChatMessage(new StudioChatMessage
        {
            Id = $"msg-a-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}",
            Role = "assistant",
            Kind = "edit",
            Text = assistantText,
            LinkedRevisionId = rev.RevisionId,
            DiffPills = diffPills,
            Suggestions = suggestions ?? []
        });

        PersistActiveDraftIfDirty();

        bool hasWarnings = parsed.Diagnostics.Any(static d => !d.IsError);
        if (hasWarnings)
        {
            _lastBrokenJson = normalizedManifestJson;
            _lastDiagnostics = parsed.Diagnostics;
        }

        if (_page == StudioPage.Preview && _previewVm is not null && _previewContent is not null)
        {
            ApplyLivePreviewUpdateInPlace();
        }
        else
        {
            ShowPreviewPage(justLoaded: true);
        }

        if (hasWarnings)
        {
            ShowPageMessage(
                InfoBarSeverity.Warning,
                string.Empty,
                _loc.T("CustomWidget.Workshop.Diag.WarningBody"),
                CreateRepairButton());
        }
        else if (cameFromWebAi && !_prefs.HasSeenKeyUpsell &&
                 !CustomWidgetAiGeneratorService.Shared.HasAnyConfiguredProvider())
        {
            _prefs.MarkKeyUpsellSeen();
            var settingsButton = new Button { Content = _loc.T("CustomWidget.Workshop.Upsell.Button") };
            settingsButton.Click += async (_, _) => await ShowAiConfigDialogInStudioAsync(null, null);
            ShowPageMessage(
                InfoBarSeverity.Informational,
                string.Empty,
                _loc.T("CustomWidget.Workshop.Upsell.Message"),
                settingsButton);
        }

        return true;
    }

    private Button CreateRepairButton()
    {
        string label = _loc.T("CustomWidget.AiWizard.Step3.CopyRepairButton");
        var button = new Button { Content = label };
        button.Click += async (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(_lastBrokenJson) || _lastDiagnostics.Count == 0)
            {
                return;
            }

            SetClipboardText(CustomWidgetTemplates.BuildChatAiRepairPrompt(
                _lastBrokenJson,
                _lastDiagnostics,
                _loc.CurrentCultureName));
            button.Content = "✓ " + _loc.T("CustomWidget.Native.Dialog.CopiedButton");

            _waitingKind = WaitingKind.Web;
            _isWebAiRepairFlow = true;
            _isInPlaceGenerating = true;
            _activeWebPreset ??= CustomWidgetStudioPreferencesStore.ResolveWebAiPreset(
                CustomWidgetStudioPreferencesStore.TargetCopyOnly);
            if (_page != StudioPage.Preview)
            {
                ShowPreviewPage(justLoaded: false);
            }
            else
            {
                RefreshInPlaceGenerationChrome();
            }
            StartClipboardPolling();

            await Task.Delay(1800);
            button.Content = label;
        };
        return button;
    }

    // ── Generation routing ──────────────────────────────────────────────

    private async Task BeginGenerationAsync(string? idea, bool forceWebAi = false, bool copyOnly = false)
    {
        string trimmed = idea?.Trim() ?? string.Empty;
        if (trimmed.Length == 0)
        {
            ShowPageMessage(InfoBarSeverity.Informational, string.Empty, _loc.T("CustomWidget.Workshop.EmptyIdea"), null);
            return;
        }

        if (_isInPlaceGenerating && _waitingKind == WaitingKind.Direct)
        {
            return;
        }

        if (_isInPlaceGenerating && _waitingKind == WaitingKind.Web)
        {
            StopClipboardPolling();
            _isInPlaceGenerating = false;
            _isWebAiRepairFlow = false;
        }

        if (_page == StudioPage.Start)
        {
            _startDraftIdea = trimmed;
        }

        _pendingPrompt = trimmed;
        _pageBeforeWaiting = _page;
        _inFlightTargetNodeLabel = FormatPickedNodeDisplayLabel(_selectedTargetNode);
        string? contextJson = CurrentManifestJson;

        string? providerId = forceWebAi || copyOnly || _preferWebAiInSession
            ? null
            : CustomWidgetWorkshopFlow.ResolveDirectProvider(
                CustomWidgetAiGeneratorService.Shared.GetConfiguredProviders(),
                _prefs.PreferredAiProviderId);

        if (providerId is not null)
        {
            await RunDirectGenerationAsync(providerId, trimmed, contextJson);
            return;
        }

        StartWebAiFlow(trimmed, contextJson, copyOnly);
    }

    private async Task RunDirectGenerationAsync(string providerId, string prompt, string? contextJson)
    {
        CancelGeneration();
        var cts = new CancellationTokenSource();
        _generationCts = cts;

        string? targetNodePath = _selectedTargetNode?.NodePath;
        string? targetNodeLabel = _inFlightTargetNodeLabel ?? FormatPickedNodeDisplayLabel(_selectedTargetNode);
        bool allowSampleData = !IsEditMode && string.IsNullOrWhiteSpace(contextJson);
        CustomWidgetHealthReport? healthReport = BuildCurrentHealthReport(contextJson);
        List<StudioChatMessage> recentContext = _chatMessages.TakeLast(6).ToList();
        IReadOnlyList<string> effectivePinnedCapabilities = ResolveEffectivePinnedCapabilitiesForPrompt(prompt);

        ResolvedAiInvocation resolved = CustomWidgetAiGeneratorService.Shared.ResolveEffectiveSettings(providerId);
        _waitingKind = WaitingKind.Direct;
        _isWebAiRepairFlow = false;
        _isInPlaceGenerating = true;
        _inFlightStage = AiGenerationStage.Connecting;
        _inFlightDetectedWidgetName = string.Empty;
        _directActiveProviderName = resolved.Preset.DisplayName;
        _directActiveModelName = resolved.Model;
        _directStartTickMs = Environment.TickCount64;
        _directLastCharTotal = 0;
        _directThinkingCollapsedByUser = false;

        if (_page != StudioPage.Preview)
        {
            ShowPreviewPage(justLoaded: false);
        }
        else
        {
            if (_refineTextBox is not null)
            {
                _refineTextBox.Text = string.Empty;
                _refineDraftIdea = string.Empty;
            }
            if (_pageInfoBar is not null)
            {
                _pageInfoBar.IsOpen = false;
                _pageInfoBar.Visibility = Visibility.Collapsed;
            }
            RefreshInPlaceGenerationChrome();
        }

        StartWaitingElapsedTimer(() =>
        {
            if (_directStatsBadgeText is not null)
            {
                TimeSpan elapsed = TimeSpan.FromMilliseconds(Math.Max(0, Environment.TickCount64 - _directStartTickMs));
                _directStatsBadgeText.Text = FormatDirectStatsText(_directActiveModelName, elapsed, _directLastCharTotal);
            }
        });

        try
        {
            StudioTurnResult result = await CustomWidgetAiGeneratorService.Shared.RunStudioTurnAsync(
                providerId: providerId,
                userMessage: prompt,
                currentManifestJson: contextJson,
                briefMarkdown: _briefMarkdown,
                recentMessages: recentContext,
                healthReport: healthReport,
                targetNodePath: targetNodePath,
                preferredEmoji: _revisions.Count == 0 ? _selectedEmoji : null,
                allowSampleData: allowSampleData,
                cancellationToken: cts.Token,
                onProgress: progress =>
                {
                    if (cts.IsCancellationRequested || _isClosed)
                    {
                        return;
                    }

                    DispatcherQueue.TryEnqueue(() =>
                    {
                        if (!cts.IsCancellationRequested && !_isClosed && _isInPlaceGenerating)
                        {
                            UpdateDirectStreamProgress(progress);
                        }
                    });
                },
                uiLanguage: _loc.CurrentCultureName,
                pinnedCapabilityIds: effectivePinnedCapabilities);

            if (cts.IsCancellationRequested || _isClosed)
            {
                return;
            }

            StopWaitingElapsedTimer();
            _isInPlaceGenerating = false;
            _inFlightTargetNodeLabel = null;
            _inFlightDetectedWidgetName = string.Empty;

            if (result.Success && !string.Equals(result.Kind, "edit", StringComparison.OrdinalIgnoreCase))
            {
                AppendChatMessage(new StudioChatMessage
                {
                    Id = $"msg-u-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}",
                    Role = "user",
                    Kind = "prompt",
                    Text = prompt,
                    TargetNodePath = targetNodePath,
                    TargetNodeLabel = targetNodeLabel
                });
                MergeTurnBriefUpdates(result.BriefUpdates, null, prompt);
                AppendChatMessage(new StudioChatMessage
                {
                    Id = $"msg-a-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}",
                    Role = "assistant",
                    Kind = result.Kind,
                    Text = result.Reply,
                    Suggestions = result.Suggestions
                });
                _selectedTargetNode = null;
                if (_previewContent is not null)
                {
                    _previewContent.SelectedNodePath = null;
                }
                _refineDraftIdea = string.Empty;
                _pendingPrompt = string.Empty;
                PersistActiveDraftIfDirty();

                if (_page == StudioPage.Preview)
                {
                    RefreshInPlaceGenerationChrome();
                    RefreshBriefButtonChrome();
                    RefreshTargetNodePillChrome();
                    RefreshSuggestionChips();
                }
                else
                {
                    ShowPreviewPage(justLoaded: false);
                }
                return;
            }

            if (result.Success &&
                !string.IsNullOrWhiteSpace(result.ManifestJson) &&
                result.ParseResult is { Manifest: not null })
            {
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
                    source: "ai");
                return;
            }

            RestorePromptToComposerAfterFailure(prompt);
            RefreshInPlaceGenerationChrome();
            ShowDirectFailureMessage(
                providerId,
                prompt,
                result.ErrorMessage ?? _loc.T("CustomWidget.Studio.Direct.Failed"));
        }
        catch (OperationCanceledException)
        {
            if (!_isClosed)
            {
                _isInPlaceGenerating = false;
                RestoreAfterCancelledInPlaceWait();
            }
        }
        catch (Exception ex)
        {
            if (_isClosed)
            {
                return;
            }

            _isInPlaceGenerating = false;
            RestorePromptToComposerAfterFailure(prompt);
            RefreshInPlaceGenerationChrome();
            ShowDirectFailureMessage(providerId, prompt, ex.Message);
        }
        finally
        {
            StopWaitingElapsedTimer();
            if (ReferenceEquals(_generationCts, cts))
            {
                _generationCts = null;
            }

            cts.Dispose();
        }
    }

    private void RestorePromptToComposerAfterFailure(string prompt)
    {
        if (_refineTextBox is not null && string.IsNullOrWhiteSpace(_refineTextBox.Text))
        {
            _refineTextBox.Text = prompt;
            _refineTextBox.SelectionStart = _refineTextBox.Text.Length;
        }
    }

    private void RestoreAfterCancelledInPlaceWait()
    {
        string restoredPrompt = !string.IsNullOrWhiteSpace(_pendingPrompt) ? _pendingPrompt : _startDraftIdea;
        _inFlightTargetNodeLabel = null;
        _inFlightDetectedWidgetName = string.Empty;

        if (_revisions.Count == 0 && _chatMessages.Count == 0 && !IsEditMode)
        {
            _startDraftIdea = restoredPrompt;
            ShowStartPage(restoredPrompt);
            return;
        }

        if (_refineTextBox is not null && string.IsNullOrWhiteSpace(_refineTextBox.Text) && !string.IsNullOrWhiteSpace(_pendingPrompt))
        {
            _refineTextBox.Text = _pendingPrompt;
            _refineTextBox.SelectionStart = _refineTextBox.Text.Length;
        }
        RefreshInPlaceGenerationChrome();
    }

    private void ShowDirectFailureMessage(string providerId, string prompt, string errorDetail)
    {
        var actionsRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Margin = new Thickness(0, 0, 0, 10)
        };

        var retryBtn = new Button
        {
            Content = _loc.T("CustomWidget.Workshop.Direct.Retry"),
            CornerRadius = new CornerRadius(6)
        };
        retryBtn.Click += async (_, _) => await BeginGenerationAsync(prompt);
        actionsRow.Children.Add(retryBtn);

        var configBtn = new Button
        {
            Content = _loc.T("CustomWidget.Workshop.Composer.ConfigKey"),
            CornerRadius = new CornerRadius(6)
        };
        configBtn.Click += async (_, _) => await ShowAiConfigDialogInStudioAsync(providerId, null);
        actionsRow.Children.Add(configBtn);

        var useWebBtn = new Button
        {
            Content = _loc.T("CustomWidget.Workshop.Direct.SwitchToWebAi"),
            CornerRadius = new CornerRadius(6)
        };
        useWebBtn.Click += async (_, _) => await BeginGenerationAsync(prompt, forceWebAi: true);
        actionsRow.Children.Add(useWebBtn);

        ShowPageMessage(
            InfoBarSeverity.Error,
            string.Empty,
            _loc.Format("CustomWidget.Workshop.Direct.Failed", errorDetail),
            action: null,
            extraContent: actionsRow);
    }

    private async Task ShowAiConfigDialogInStudioAsync(string? providerId, Action? onSaved)
    {
        if (_isDialogOpen || _isClosed || RootGrid.XamlRoot is null)
        {
            return;
        }

        _isDialogOpen = true;
        try
        {
            string? targetId = string.IsNullOrWhiteSpace(providerId)
                ? (string.IsNullOrWhiteSpace(_prefs.PreferredAiProviderId) ? null : _prefs.PreferredAiProviderId)
                : providerId;
            string? savedProvider = await CustomWidgetAiConfigDialogHelper.ShowAsync(
                RootGrid.XamlRoot,
                RootGrid.ActualTheme,
                _loc,
                targetId);
            if (!string.IsNullOrWhiteSpace(savedProvider))
            {
                CustomWidgetAiProviderPreset savedPreset = DeskBoxAiService.FindProviderPreset(savedProvider);
                AiProviderCredentialEntry? entry = CustomWidgetAiCredentialStore.Shared.Get(savedProvider);
                if (DeskBoxAiService.IsProviderConfigured(savedPreset, entry))
                {
                    _preferWebAiInSession = false;
                    _prefs.SetPreferredAiProviderId(savedProvider);
                }
            }

            onSaved?.Invoke();
            if (_page == StudioPage.Start)
            {
                ShowStartPage(_startDraftIdea);
            }
            else if (_page == StudioPage.Preview)
            {
                ShowPreviewPage(justLoaded: false);
            }
        }
        finally
        {
            _isDialogOpen = false;
        }
    }

    private void SwitchWaitingToWebAi()
    {
        string prompt = _pendingPrompt;
        string? contextJson = CurrentManifestJson;
        CancelGeneration();
        StopWaitingElapsedTimer();
        _isInPlaceGenerating = false;
        if (!string.IsNullOrWhiteSpace(prompt))
        {
            StartWebAiFlow(prompt, contextJson, copyOnly: false);
        }
        else
        {
            RestoreAfterCancelledInPlaceWait();
        }
    }

    private Task ShowApiDocsDialogInStudioAsync(string? initialCapabilityId = null)
    {
        if (_isClosed)
        {
            return Task.CompletedTask;
        }

        if (!string.IsNullOrWhiteSpace(initialCapabilityId))
        {
            OpenApiDocsDrawer(initialCapabilityId);
        }
        else if (_isApiDocsDrawerOpen)
        {
            CloseApiDocsDrawer();
        }
        else
        {
            OpenApiDocsDrawer(null);
        }

        return Task.CompletedTask;
    }

    private void OpenApiDocsDrawer(string? initialCapabilityId = null)
    {
        if (_isClosed)
        {
            return;
        }

        bool wasOpen = _isApiDocsDrawerOpen;
        _isApiDocsDrawerOpen = true;

        if (_apiDocsDrawerHandle is null)
        {
            _apiDocsDrawerHandle = CustomWidgetApiDocsDialogHelper.CreateReferenceView(
                _loc,
                initialCapabilityId,
                IsCapabilityPinned,
                TogglePinnedCapability,
                InsertTextIntoActiveComposer,
                CloseApiDocsDrawer,
                isSideDrawerMode: true);
            ApiDrawerBorder.Child = _apiDocsDrawerHandle.RootElement;
        }
        else if (!string.IsNullOrWhiteSpace(initialCapabilityId))
        {
            _apiDocsDrawerHandle.SelectCapability(initialCapabilityId);
        }
        else
        {
            _apiDocsDrawerHandle.RefreshPinnedState();
        }

        ApplyApiDrawerLayout(WorkspaceShellGrid.ActualWidth);
        if (!wasOpen)
        {
            FluentMotionHelper.PlayEntranceSlideAndFade(ApiDrawerBorder, offsetX: 16f, offsetY: 0f, durationMs: 170);
        }

        _refreshApiDocsToggleButtons?.Invoke();
    }

    private void CloseApiDocsDrawer()
    {
        if (!_isApiDocsDrawerOpen)
        {
            return;
        }

        if (ApiDrawerColumn.ActualWidth >= 360)
        {
            _preferredApiDrawerWidth = ApiDrawerColumn.ActualWidth;
        }

        _isApiDocsDrawerOpen = false;
        ApplyApiDrawerLayout(WorkspaceShellGrid.ActualWidth);
        _refreshApiDocsToggleButtons?.Invoke();
    }

    private void WorkspaceShellGrid_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_isApiDocsDrawerOpen && e.NewSize.Width > 0)
        {
            ApplyApiDrawerLayout(e.NewSize.Width);
        }
    }

    private void ApplyApiDrawerLayout(double shellWidth)
    {
        if (!_isApiDocsDrawerOpen)
        {
            ApiDrawerBorder.Visibility = Visibility.Collapsed;
            ApiDrawerSplitter.Visibility = Visibility.Collapsed;
            ApiDrawerColumn.MinWidth = 0;
            ApiDrawerColumn.MaxWidth = double.PositiveInfinity;
            ApiDrawerColumn.Width = new GridLength(0);
            return;
        }

        ApiDrawerBorder.Visibility = Visibility.Visible;
        double effectiveWidth = shellWidth > 0 ? shellWidth : DesiredWidth;

        // Wide workspace: side-by-side resizable pane; Narrow workspace: right-aligned overlay drawer
        if (effectiveWidth >= 980)
        {
            Grid.SetColumn(ApiDrawerBorder, 2);
            Grid.SetColumnSpan(ApiDrawerBorder, 1);
            ApiDrawerBorder.HorizontalAlignment = HorizontalAlignment.Stretch;
            ApiDrawerBorder.Width = double.NaN;

            double maxAllowed = Math.Clamp(effectiveWidth - 500, 380, 640);
            double targetWidth = Math.Clamp(_preferredApiDrawerWidth, 380, maxAllowed);
            ApiDrawerColumn.MinWidth = 360;
            ApiDrawerColumn.MaxWidth = maxAllowed;
            if (ApiDrawerSplitter.Visibility != Visibility.Visible || ApiDrawerColumn.Width.Value < 300)
            {
                ApiDrawerColumn.Width = new GridLength(targetWidth);
            }
            ApiDrawerSplitter.Visibility = Visibility.Visible;
        }
        else
        {
            if (ApiDrawerColumn.ActualWidth >= 360)
            {
                _preferredApiDrawerWidth = ApiDrawerColumn.ActualWidth;
            }

            ApiDrawerSplitter.Visibility = Visibility.Collapsed;
            ApiDrawerColumn.MinWidth = 0;
            ApiDrawerColumn.MaxWidth = double.PositiveInfinity;
            ApiDrawerColumn.Width = new GridLength(0);

            Grid.SetColumn(ApiDrawerBorder, 0);
            Grid.SetColumnSpan(ApiDrawerBorder, 3);
            ApiDrawerBorder.HorizontalAlignment = HorizontalAlignment.Right;
            ApiDrawerBorder.Width = Math.Clamp(effectiveWidth - 56, 340, 488);
        }
    }

    private void InsertTextIntoActiveComposer(string token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return;
        }

        TextBox? box = _page == StudioPage.Preview ? _refineTextBox : _startIdeaTextBox;
        if (box is null || !box.IsEnabled)
        {
            return;
        }

        string current = box.Text ?? string.Empty;
        int caret = Math.Clamp(box.SelectionStart, 0, current.Length);
        int selLen = Math.Clamp(box.SelectionLength, 0, current.Length - caret);

        string before = current[..caret];
        string after = current[(caret + selLen)..];
        string prefixSpace = before.Length > 0 && !char.IsWhiteSpace(before[^1]) ? " " : string.Empty;
        string suffixSpace = after.Length == 0 || !char.IsWhiteSpace(after[0]) ? " " : string.Empty;
        string inserted = prefixSpace + token.Trim() + suffixSpace;

        box.Text = before + inserted + after;
        box.SelectionStart = before.Length + inserted.Length;
        box.SelectionLength = 0;
        box.Focus(FocusState.Programmatic);
    }

    private bool IsCapabilityPinned(string id) =>
        !string.IsNullOrWhiteSpace(id) &&
        _pinnedCapabilityIds.Any(x => string.Equals(x, id.Trim(), StringComparison.OrdinalIgnoreCase));

    private void TogglePinnedCapability(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return;
        }

        string normalized = id.Trim();
        int idx = _pinnedCapabilityIds.FindIndex(x => string.Equals(x, normalized, StringComparison.OrdinalIgnoreCase));
        if (idx >= 0)
        {
            _pinnedCapabilityIds.RemoveAt(idx);
        }
        else if (CustomWidgetCapabilityRegistry.GetById(normalized) is { } spec)
        {
            _pinnedCapabilityIds.Add(spec.Id);
        }

        _refreshActiveComposerCapabilityChrome?.Invoke();
        _apiDocsDrawerHandle?.RefreshPinnedState();
    }

    private void ClearPinnedCapabilities()
    {
        if (_pinnedCapabilityIds.Count == 0)
        {
            return;
        }

        _pinnedCapabilityIds.Clear();
        _refreshActiveComposerCapabilityChrome?.Invoke();
        _apiDocsDrawerHandle?.RefreshPinnedState();
    }

    private IReadOnlyList<string> ResolveEffectivePinnedCapabilitiesForPrompt(string? promptText)
    {
        var merged = new List<string>(_pinnedCapabilityIds);
        var seen = new HashSet<string>(_pinnedCapabilityIds, StringComparer.OrdinalIgnoreCase);

        foreach (string id in CustomWidgetCapabilityRegistry.ExtractMentionedCapabilityIds(promptText))
        {
            if (seen.Add(id))
            {
                merged.Add(id);
            }
        }

        return merged;
    }

    private void StartWebAiFlow(string prompt, string? contextJson, bool copyOnly)
    {
        IReadOnlyList<string> effectivePinnedCapabilities = ResolveEffectivePinnedCapabilitiesForPrompt(prompt);
        string fullPrompt = CustomWidgetTemplates.BuildChatAiPrompt(
            prompt,
            contextJson,
            _revisions.Count == 0 ? _selectedEmoji : null,
            _loc.CurrentCultureName,
            briefMarkdown: _briefMarkdown,
            targetNodePath: _selectedTargetNode?.NodePath,
            pinnedCapabilityIds: effectivePinnedCapabilities);
        SetClipboardText(fullPrompt);

        WebAiTargetPreset preset = copyOnly
            ? CustomWidgetStudioPreferencesStore.ResolveWebAiPreset(CustomWidgetStudioPreferencesStore.TargetCopyOnly)
            : CustomWidgetStudioPreferencesStore.ResolveWebAiPreset(
                _prefs.ResolveEffectiveWebAiTarget(_loc.CurrentCultureName));
        _activeWebPreset = preset;
        _waitingKind = WaitingKind.Web;
        _isWebAiRepairFlow = false;
        _isInPlaceGenerating = true;
        OpenWebAi(preset);

        if (_page != StudioPage.Preview)
        {
            ShowPreviewPage(justLoaded: false);
        }
        else
        {
            if (_refineTextBox is not null)
            {
                _refineTextBox.Text = string.Empty;
                _refineDraftIdea = string.Empty;
            }
            if (_pageInfoBar is not null)
            {
                _pageInfoBar.IsOpen = false;
                _pageInfoBar.Visibility = Visibility.Collapsed;
            }
            RefreshInPlaceGenerationChrome();
        }

        StartClipboardPolling();
    }

    private static void OpenWebAi(WebAiTargetPreset preset)
    {
        if (preset.IsCopyOnly || string.IsNullOrWhiteSpace(preset.LaunchUrl))
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(preset.LaunchUrl) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            App.Log($"[CustomWidgetStudio] Open web AI failed: {ex.Message}");
        }
    }

    private void CancelWaiting()
    {
        bool hadActiveGeneration = _generationCts is not null;
        CancelGeneration();
        StopWaitingElapsedTimer();
        StopClipboardPolling();
        _isInPlaceGenerating = false;
        _isWebAiRepairFlow = false;
        if (!hadActiveGeneration)
        {
            RestoreAfterCancelledInPlaceWait();
        }
    }

    private void StartWaitingElapsedTimer(Action onSecondTick)
    {
        StopWaitingElapsedTimer();
        Microsoft.UI.Dispatching.DispatcherQueueTimer timer = DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromSeconds(1);
        timer.IsRepeating = true;
        timer.Tick += (_, _) =>
        {
            if (_isClosed || !_isInPlaceGenerating)
            {
                StopWaitingElapsedTimer();
                return;
            }

            onSecondTick();
        };
        timer.Start();
        _waitingElapsedTimer = timer;
    }

    private void StopWaitingElapsedTimer()
    {
        if (_waitingElapsedTimer is { } timer)
        {
            timer.Stop();
            _waitingElapsedTimer = null;
        }
    }

    private void CancelGeneration()
    {
        try
        {
            _generationCts?.Cancel();
        }
        catch
        {
        }
    }

    // ── Clipboard ───────────────────────────────────────────────────────

    private void StartClipboardPolling()
    {
        StopClipboardPolling();
        _clipboardSequence = ClipboardPlatformInterop.GetClipboardSequenceNumber();
        Microsoft.UI.Dispatching.DispatcherQueueTimer timer = DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(450);
        timer.IsRepeating = true;
        timer.Tick += ClipboardPollTimer_Tick;
        timer.Start();
        _clipboardPollTimer = timer;
    }

    private void StopClipboardPolling()
    {
        if (_clipboardPollTimer is { } timer)
        {
            timer.Stop();
            timer.Tick -= ClipboardPollTimer_Tick;
            _clipboardPollTimer = null;
        }
    }

    private async void ClipboardPollTimer_Tick(Microsoft.UI.Dispatching.DispatcherQueueTimer sender, object args)
    {
        if (_clipboardReadInFlight || _isClosed)
        {
            return;
        }

        uint sequence = ClipboardPlatformInterop.GetClipboardSequenceNumber();
        if (sequence == _clipboardSequence)
        {
            return;
        }

        _clipboardSequence = sequence;
        _clipboardReadInFlight = true;
        try
        {
            string text = await ReadClipboardTextAsync();
            if (_clipboardPollTimer is null || _isClosed)
            {
                return;
            }

            if (CustomWidgetWorkshopFlow.TryExtractManifestReply(text, _lastSelfCopiedText, out string manifestJson))
            {
                BringToFrontForReply();
                HandleManifestCandidate(manifestJson, _pendingPrompt, null);
            }
        }
        finally
        {
            _clipboardReadInFlight = false;
        }
    }

    private async Task PasteReplyManuallyAsync()
    {
        string text = await ReadClipboardTextAsync();
        string extracted = CustomWidgetTemplates.ExtractJsonFromClipboardText(text);
        if (string.IsNullOrWhiteSpace(extracted) ||
            string.Equals(text.Trim(), _lastSelfCopiedText?.Trim(), StringComparison.Ordinal))
        {
            ShowPageMessage(InfoBarSeverity.Warning, string.Empty, _loc.T("CustomWidget.Workshop.Web.NotRecognized"), null);
            return;
        }

        if (_page != StudioPage.Waiting)
        {
            _pageBeforeWaiting = _page;
        }

        HandleManifestCandidate(extracted, _pendingPrompt, null);
    }

    private static async Task<string> ReadClipboardTextAsync()
    {
        try
        {
            DataPackageView view = Clipboard.GetContent();
            if (view.Contains(StandardDataFormats.Text))
            {
                return await view.GetTextAsync() ?? string.Empty;
            }
        }
        catch
        {
        }

        return string.Empty;
    }

    private void SetClipboardText(string text)
    {
        _lastSelfCopiedText = text;
        try
        {
            var package = new DataPackage();
            package.SetText(text);
            DeskBoxClipboardWriteScope.MarkText(text);
            Clipboard.SetContent(package);
            try
            {
                Clipboard.Flush();
            }
            catch (Exception flushEx)
            {
                App.Log($"[CustomWidgetStudio] Clipboard content was set but flush failed: {flushEx.Message}");
            }
        }
        catch (Exception ex)
        {
            App.Log($"[CustomWidgetStudio] Clipboard copy failed: {ex.Message}");
        }

        _clipboardSequence = ClipboardPlatformInterop.GetClipboardSequenceNumber();
    }

    private void BringToFrontForReply()
    {
        try
        {
            _appWindow.Show();
            App.Current.WidgetManager?.BringAuxiliaryWindowToFront(_hWnd, "custom-widget-studio-reply");
            Activate();
            _ = Win32Helper.SetForegroundWindow(_hWnd);
        }
        catch
        {
        }
    }

    // ── Code tool & external Agent workflow ─────────────────────────────

    private const string DraftSkeletonManifestJson = """
        {
          "schemaVersion": 1,
          "id": "custom.draft",
          "name": "Draft Widget",
          "version": "1.0.0",
          "author": "DeskBox",
          "description": "External Agent draft workspace",
          "icon": "✨",
          "origin": "agent",
          "minSize": { "width": 260, "height": 200 },
          "defaultSize": { "width": 320, "height": 260 },
          "state": {},
          "collections": {},
          "view": {
            "type": "Stack",
            "spacing": 8,
            "children": [
              { "type": "Text", "text": "Ready for external Agent live hot-reload.", "style": "bodyStrong" }
            ]
          }
        }
        """;

    private async Task CopyAgentKickoffPromptAsync(string? idea)
    {
        try
        {
            string prompt;
            if (IsEditMode && !string.IsNullOrWhiteSpace(_targetWidgetId))
            {
                CustomWidgetDataStore store = CustomWidgetDataStore.ForWidget(_targetWidgetId);
                await SyncWorkspaceBriefAsync(store.WidgetDirectory);
                await CustomWidgetTemplates.EnsureAgentWorkspacePreparedAsync(store.WidgetDirectory, _targetWidgetId, idea);
                prompt = CustomWidgetAgentConnectorService.BuildKickoffPrompt(
                    AgentPlaybookKind.ModifyExistingWidget,
                    _loc.CurrentCultureName,
                    _targetWidgetId,
                    _targetWidgetName,
                    idea);
            }
            else if (!string.IsNullOrWhiteSpace(CurrentManifestJson))
            {
                string workspace = _sessionStore.GetCreateDraftDirectory();
                Directory.CreateDirectory(workspace);
                string manifestPath = Path.Combine(workspace, "manifest.json");
                Interlocked.Exchange(ref _lastDraftWriteTick, Environment.TickCount64);
                CustomWidgetStorageFileOps.ReplaceTextFileAtomically(manifestPath, CurrentManifestJson);
                Interlocked.Exchange(ref _lastDraftWriteTick, Environment.TickCount64);
                await SyncWorkspaceBriefAsync(workspace);
                await CustomWidgetTemplates.EnsureAgentWorkspacePreparedAsync(workspace, initialIdea: idea);
                prompt = CustomWidgetTemplates.BuildLocalAgentPrompt(
                    idea ?? string.Empty,
                    Path.Combine(workspace, "SKILL.md"),
                    manifestPath,
                    workspace,
                    _selectedEmoji,
                    _loc.CurrentCultureName,
                    ResolveEffectivePinnedCapabilitiesForPrompt(idea));
                StartCodeToolWatcher(workspace);
            }
            else
            {
                await CustomWidgetTemplates.EnsureAiAgentWorkspaceDocsAsync(
                    CustomWidgetDataStore.GetWidgetsRootDirectory());
                prompt = CustomWidgetAgentConnectorService.BuildKickoffPrompt(
                    AgentPlaybookKind.CreateNewWidget,
                    _loc.CurrentCultureName,
                    userIdea: idea);
            }

            SetClipboardText(prompt);
            ShowPageMessage(
                InfoBarSeverity.Success,
                string.Empty,
                _loc.T("CustomWidget.Workshop.AgentTaskCopied"),
                null);
        }
        catch (Exception ex)
        {
            App.Log($"[CustomWidgetStudio] Copy external agent prompt failed: {ex.Message}");
            ShowPageMessage(InfoBarSeverity.Error, string.Empty, ex.Message, null);
        }
    }

    private async Task OpenInCodeToolAsync(string? idea)
    {
        string workspace = IsEditMode
            ? CustomWidgetDataStore.ForWidget(_targetWidgetId!).WidgetDirectory
            : _sessionStore.GetCreateDraftDirectory();
        string seedJson = !string.IsNullOrWhiteSpace(CurrentManifestJson)
            ? CurrentManifestJson!
            : DraftSkeletonManifestJson;

        try
        {
            Directory.CreateDirectory(workspace);
            string manifestPath = Path.Combine(workspace, "manifest.json");
            if (!IsEditMode || !File.Exists(manifestPath))
            {
                Interlocked.Exchange(ref _lastDraftWriteTick, Environment.TickCount64);
                CustomWidgetStorageFileOps.ReplaceTextFileAtomically(manifestPath, seedJson);
                Interlocked.Exchange(ref _lastDraftWriteTick, Environment.TickCount64);
            }

            await SyncWorkspaceBriefAsync(workspace);
            await CustomWidgetTemplates.EnsureAgentWorkspacePreparedAsync(
                workspace,
                IsEditMode ? _targetWidgetId : null,
                idea);
            SetClipboardText(CustomWidgetTemplates.BuildLocalAgentPrompt(
                idea ?? string.Empty,
                Path.Combine(workspace, "SKILL.md"),
                manifestPath,
                workspace,
                _selectedEmoji,
                _loc.CurrentCultureName,
                ResolveEffectivePinnedCapabilitiesForPrompt(idea)));
            CustomWidgetAgentConnectorService.TryOpenWorkspaceInEditor(workspace, "auto");
            StartCodeToolWatcher(workspace);
            ShowPageMessage(InfoBarSeverity.Informational, string.Empty, _loc.T("CustomWidget.Workshop.CodeTool.Hint"), null);
        }
        catch (Exception ex)
        {
            App.Log($"[CustomWidgetStudio] Open code tool failed: {ex.Message}");
            ShowPageMessage(InfoBarSeverity.Error, string.Empty, ex.Message, null);
        }
    }

    private async Task SyncWorkspaceBriefAsync(string workspaceDirectory)
    {
        if (string.IsNullOrWhiteSpace(_briefMarkdown))
        {
            return;
        }

        string briefPath = Path.Combine(workspaceDirectory, CustomWidgetStudioSessionStore.BriefFileName);
        await File.WriteAllTextAsync(briefPath, _briefMarkdown.Trim() + Environment.NewLine, Encoding.UTF8);
    }

    private void StartCodeToolWatcher(string workspace)
    {
        DisposeCodeToolWatcher();
        try
        {
            var watcher = new FileSystemWatcher(workspace, "manifest.json")
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.CreationTime
            };
            watcher.Changed += (_, _) => ScheduleCodeToolReload(workspace);
            watcher.Created += (_, _) => ScheduleCodeToolReload(workspace);
            watcher.Renamed += (_, _) => ScheduleCodeToolReload(workspace);
            watcher.EnableRaisingEvents = true;
            _codeToolWatcher = watcher;
        }
        catch (Exception ex)
        {
            App.Log($"[CustomWidgetStudio] Code tool watcher failed: {ex.Message}");
        }
    }

    private void ScheduleCodeToolReload(string workspace)
    {
        long elapsed = Environment.TickCount64 - Interlocked.Read(ref _lastDraftWriteTick);
        if (elapsed is >= 0 and < 450)
        {
            return;
        }

        CancellationTokenSource cts;
        lock (_revisions)
        {
            _codeToolDebounceCts?.Cancel();
            _codeToolDebounceCts?.Dispose();
            cts = new CancellationTokenSource();
            _codeToolDebounceCts = cts;
        }

        CancellationToken token = cts.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(220, token);
                string text = await File.ReadAllTextAsync(Path.Combine(workspace, "manifest.json"), Encoding.UTF8, token);
                string briefPath = Path.Combine(workspace, CustomWidgetStudioSessionStore.BriefFileName);
                string? diskBrief = File.Exists(briefPath)
                    ? await File.ReadAllTextAsync(briefPath, Encoding.UTF8, token)
                    : null;
                if (string.IsNullOrWhiteSpace(text))
                {
                    return;
                }

                DispatcherQueue.TryEnqueue(() =>
                {
                    if (!_isClosed && !token.IsCancellationRequested)
                    {
                        if (!string.IsNullOrWhiteSpace(diskBrief))
                        {
                            _briefMarkdown = diskBrief!.Trim();
                        }

                        if (!string.Equals(text, CurrentManifestJson, StringComparison.Ordinal))
                        {
                            if (_page != StudioPage.Waiting)
                            {
                                _pageBeforeWaiting = _page;
                            }

                            HandleManifestCandidate(
                                text,
                                null,
                                _loc.T("CustomWidget.Workshop.History.CodeTool"),
                                source: "external");
                        }
                    }
                });
            }
            catch
            {
            }
        }, token);
    }

    private void DisposeCodeToolWatcher()
    {
        lock (_revisions)
        {
            try
            {
                _codeToolDebounceCts?.Cancel();
                _codeToolDebounceCts?.Dispose();
            }
            catch
            {
            }

            _codeToolDebounceCts = null;
        }

        if (_codeToolWatcher is not null)
        {
            try
            {
                _codeToolWatcher.EnableRaisingEvents = false;
                _codeToolWatcher.Dispose();
            }
            catch
            {
            }

            _codeToolWatcher = null;
        }
    }

    // ── Commit + P0 User Data Safety Gate ───────────────────────────────

    private async Task CommitAsync(Button commitButton)
    {
        string? json = CurrentManifestJson;
        WidgetManager? manager = App.Current.WidgetManager;
        if (string.IsNullOrWhiteSpace(json) || manager is null)
        {
            return;
        }

        commitButton.IsEnabled = false;
        try
        {
            bool wasEdit = IsEditMode;
            if (wasEdit && !string.IsNullOrWhiteSpace(_targetWidgetId))
            {
                bool proceed = await VerifyOrMigrateStoreDataBeforeCommitAsync(_targetWidgetId!, json);
                if (!proceed)
                {
                    commitButton.IsEnabled = true;
                    return;
                }
            }

            string jsonToCommit = StampManifestProvenanceForCommit(json);
            jsonToCommit = CustomWidgetManifestCompatibility.StampRequiredMinAppVersion(jsonToCommit);

            GlanceWidgetInstanceInfo info = wasEdit
                ? await manager.CreateCustomWidgetAsync($"update:{_targetWidgetId}:{jsonToCommit}")
                : await manager.CreateCustomWidgetAsync($"json:{jsonToCommit}");

            if (!string.IsNullOrWhiteSpace(info.Id))
            {
                IReadOnlyList<StudioPersistedRevision> committedRevisions =
                    CustomWidgetStudioSessionStore.MarkCommittedRevision(
                        _revisions.Take(_revisionIndex + 1).ToList(),
                        jsonToCommit,
                        source: _aiRevisionCount > 0 ? "ai" : "manual");
                if (wasEdit)
                {
                    _sessionStore.SaveCommittedWidgetSession(info.Id, _briefMarkdown, committedRevisions, _chatMessages);
                }
                else
                {
                    _sessionStore.PromoteCreateSessionToWidget(info.Id, _briefMarkdown, committedRevisions, _chatMessages);
                }
            }

            if (_prefs.LocateOnDesktopAfterCommit && !string.IsNullOrWhiteSpace(info.Id))
            {
                await manager.LocateCustomWidgetAsync(info.Id);
            }

            _committed = true;
            _uncommittedEditCount = 0;
            App.Current.NativeNotificationService?.TryShow(
                _loc.T(wasEdit ? "CustomWidget.Workshop.Saved.Title" : "CustomWidget.Workshop.Placed.Title"),
                _loc.T("CustomWidget.Workshop.Placed.Body"));
            _allowClose = true;
            Close();
        }
        catch (Exception ex)
        {
            commitButton.IsEnabled = true;
            ShowPageMessage(InfoBarSeverity.Error, string.Empty, ex.Message, null);
        }
    }

    private async Task PlaceTemplateDirectlyAsync(string manifestJson, string? emoji, Button triggerButton)
    {
        WidgetManager? manager = App.Current.WidgetManager;
        if (string.IsNullOrWhiteSpace(manifestJson) || manager is null)
        {
            return;
        }

        triggerButton.IsEnabled = false;
        try
        {
            CustomWidgetParseResult parsed = CustomWidgetManifestParser.Parse(manifestJson);
            string jsonToCommit = manifestJson;
            if (CustomWidgetTemplates.IsEmojiIcon(emoji) &&
                (parsed.Manifest is null || !CustomWidgetTemplates.IsEmojiIcon(parsed.Manifest.Icon)))
            {
                try
                {
                    jsonToCommit = CustomWidgetTemplates.WithUpdatedIconJson(jsonToCommit, emoji!.Trim());
                }
                catch
                {
                }
            }

            jsonToCommit = StampManifestProvenanceForCommit(jsonToCommit);
            jsonToCommit = CustomWidgetManifestCompatibility.StampRequiredMinAppVersion(jsonToCommit);

            GlanceWidgetInstanceInfo info = await manager.CreateCustomWidgetAsync($"json:{jsonToCommit}");
            if (!string.IsNullOrWhiteSpace(info.Id))
            {
                string title = parsed.Manifest?.ResolveLocalizedName(_loc.CurrentCultureName) ?? _loc.T("CustomWidget.Title");
                var initialRevisions = new List<StudioPersistedRevision>
                {
                    new()
                    {
                        RevisionId = "rev-1",
                        ManifestJson = jsonToCommit,
                        ManifestSha256 = CustomWidgetStudioSessionStore.ComputeManifestSha256(jsonToCommit),
                        ChangeSummary = _loc.Format("CustomWidget.Workshop.History.FromTemplate", title),
                        Source = "template",
                        IsCommitted = true,
                        CreatedAtUtcTicks = DateTimeOffset.UtcNow.UtcTicks
                    }
                };
                _sessionStore.SaveCommittedWidgetSession(info.Id, string.Empty, initialRevisions, []);
            }

            if (_prefs.LocateOnDesktopAfterCommit && !string.IsNullOrWhiteSpace(info.Id))
            {
                await manager.LocateCustomWidgetAsync(info.Id);
            }

            _committed = true;
            _uncommittedEditCount = 0;
            App.Current.NativeNotificationService?.TryShow(
                _loc.T("CustomWidget.Workshop.Placed.Title"),
                _loc.T("CustomWidget.Workshop.Placed.Body"));
            _allowClose = true;
            Close();
        }
        catch (Exception ex)
        {
            triggerButton.IsEnabled = true;
            ShowPageMessage(InfoBarSeverity.Error, string.Empty, ex.Message, null);
        }
    }

    private async Task<bool> VerifyOrMigrateStoreDataBeforeCommitAsync(string widgetId, string candidateJson)
    {
        if (_isDialogOpen || RootGrid.XamlRoot is null)
        {
            return true;
        }

        CustomWidgetParseResult candidateParsed = CustomWidgetManifestParser.Parse(candidateJson);
        if (candidateParsed.Manifest is null)
        {
            return true;
        }

        CustomWidgetDataStore store = CustomWidgetDataStore.ForWidget(widgetId);
        CustomWidgetStorePayload livePayload = await store.LoadAsync();
        string? diskManifestJson = TryReadWidgetManifest(widgetId);
        CustomWidgetManifest? prevManifest = !string.IsNullOrWhiteSpace(diskManifestJson)
            ? CustomWidgetManifestParser.Parse(diskManifestJson!).Manifest
            : null;

        CustomWidgetDataSafetyReport safety = CustomWidgetHealthAnalyzer.EvaluateStoreDataSafety(
            prevManifest,
            livePayload,
            candidateParsed.Manifest);
        if (!safety.HasDataLossRisk)
        {
            return true;
        }

        var bodySb = new StringBuilder();
        var missingNames = new List<string>();
        bool canAutoMigrateAll = safety.DroppedCollections.Count > 0 &&
                                 safety.DroppedNonEmptyStateKeys.Count == 0 &&
                                 safety.DroppedCollections.All(static c => !string.IsNullOrWhiteSpace(c.SuggestedNewCollectionKey));

        foreach (CustomWidgetDroppedCollectionRisk col in safety.DroppedCollections)
        {
            missingNames.Add(col.OldCollectionKey);
            bodySb.Append(_loc.Format("CustomWidget.Workshop.CoCreate.DataSafetyCollectionLine", col.OldCollectionKey, col.LiveRecordCount));
            if (!string.IsNullOrWhiteSpace(col.SuggestedNewCollectionKey))
            {
                bodySb.Append(_loc.Format("CustomWidget.Workshop.CoCreate.DataSafetyRenameHint", col.SuggestedNewCollectionKey));
            }
            bodySb.AppendLine();
        }

        if (safety.DroppedNonEmptyStateKeys.Count > 0)
        {
            missingNames.AddRange(safety.DroppedNonEmptyStateKeys);
            bodySb.AppendLine(_loc.Format(
                "CustomWidget.Workshop.CoCreate.DataSafetyStateLine",
                string.Join(", ", safety.DroppedNonEmptyStateKeys)));
        }

        _isDialogOpen = true;
        ContentDialogResult choice;
        try
        {
            var dialog = new ContentDialog
            {
                XamlRoot = RootGrid.XamlRoot,
                RequestedTheme = RootGrid.ActualTheme,
                Title = _loc.T("CustomWidget.Workshop.CoCreate.DataSafetyTitle"),
                Content = new TextBlock
                {
                    Text = bodySb.ToString().Trim(),
                    TextWrapping = TextWrapping.Wrap
                },
                PrimaryButtonText = canAutoMigrateAll
                    ? _loc.T("CustomWidget.Workshop.CoCreate.DataSafetyMigrateAndSave")
                    : _loc.T("CustomWidget.Workshop.CoCreate.DataSafetyAskAiKeep"),
                SecondaryButtonText = canAutoMigrateAll
                    ? _loc.T("CustomWidget.Workshop.CoCreate.DataSafetyAskAiKeep")
                    : _loc.T("CustomWidget.Workshop.CoCreate.DataSafetySaveAnyway"),
                CloseButtonText = _loc.T("Common.Cancel"),
                DefaultButton = ContentDialogButton.Primary
            };
            if (Application.Current.Resources.TryGetValue("DefaultContentDialogStyle", out object? style) &&
                style is Style dialogStyle)
            {
                dialog.Style = dialogStyle;
            }

            choice = await dialog.ShowAsync();
        }
        finally
        {
            _isDialogOpen = false;
        }

        if (choice == ContentDialogResult.None)
        {
            return false;
        }

        if (canAutoMigrateAll)
        {
            if (choice == ContentDialogResult.Primary)
            {
                var renames = safety.DroppedCollections
                    .Select(static c => (c.OldCollectionKey, c.SuggestedNewCollectionKey!))
                    .ToList();
                await CustomWidgetHealthAnalyzer.MigrateRenamedCollectionsAsync(store, renames);
                return true;
            }

            // Secondary = Ask AI to preserve original keys
            string preservePrompt = _loc.Format(
                "CustomWidget.Workshop.CoCreate.DataSafetyRepairPrompt",
                string.Join(", ", missingNames));
            _ = BeginGenerationAsync(preservePrompt);
            return false;
        }

        if (choice == ContentDialogResult.Primary)
        {
            string preservePrompt = _loc.Format(
                "CustomWidget.Workshop.CoCreate.DataSafetyRepairPrompt",
                string.Join(", ", missingNames));
            _ = BeginGenerationAsync(preservePrompt);
            return false;
        }

        // Secondary = Save Anyway
        return true;
    }

    private string StampManifestProvenanceForCommit(string json)
    {
        CustomWidgetParseResult currentParsed = CustomWidgetManifestParser.Parse(json);
        CustomWidgetManifest? currentManifest = currentParsed.Manifest;

        string? forkedFromId = currentManifest?.ForkedFromId;
        string? forkedFromVersion = currentManifest?.ForkedFromVersion;

        if (_revisions.Count > 0 && string.IsNullOrWhiteSpace(forkedFromId))
        {
            CustomWidgetParseResult baseParsed = CustomWidgetManifestParser.Parse(_revisions[0].ManifestJson);
            if (baseParsed.Manifest is { } baseManifest &&
                baseManifest.Id.StartsWith("deskbox.", StringComparison.OrdinalIgnoreCase))
            {
                forkedFromId = baseManifest.Id;
                forkedFromVersion = baseManifest.Version;
            }
        }

        if (_aiRevisionCount > 0)
        {
            return CustomWidgetManifestMetadataStamper.StampProvenance(
                json,
                origin: CustomWidgetOriginKinds.Ai,
                isCustomized: true,
                forkedFromId: forkedFromId,
                forkedFromVersion: forkedFromVersion);
        }

        if (currentManifest is not null &&
            string.Equals(currentManifest.Origin, CustomWidgetOriginKinds.Community, StringComparison.OrdinalIgnoreCase))
        {
            return CustomWidgetManifestMetadataStamper.StampProvenance(
                json,
                origin: CustomWidgetOriginKinds.Community,
                isCustomized: currentManifest.IsCustomized,
                forkedFromId: forkedFromId,
                forkedFromVersion: forkedFromVersion);
        }

        if (currentManifest is not null &&
            _revisions.Count <= 1 &&
            string.Equals(currentManifest.Origin, CustomWidgetOriginKinds.Official, StringComparison.OrdinalIgnoreCase) &&
            currentManifest.Id.StartsWith("deskbox.", StringComparison.OrdinalIgnoreCase) &&
            !currentManifest.IsCustomized)
        {
            return CustomWidgetManifestMetadataStamper.StampProvenance(
                json,
                origin: CustomWidgetOriginKinds.Official,
                isCustomized: false);
        }

        string fallbackOrigin =
            currentManifest is not null &&
            !string.Equals(currentManifest.Origin, CustomWidgetOriginKinds.Official, StringComparison.OrdinalIgnoreCase)
                ? currentManifest.Origin
                : CustomWidgetOriginKinds.Local;
        bool isCustomized = (currentManifest?.IsCustomized ?? false) || _revisions.Count > 1;
        return CustomWidgetManifestMetadataStamper.StampProvenance(
            json,
            origin: fallbackOrigin,
            isCustomized: isCustomized,
            forkedFromId: forkedFromId,
            forkedFromVersion: forkedFromVersion);
    }

    // ── Session / lifecycle ─────────────────────────────────────────────

    private void ResetSession()
    {
        CancelGeneration();
        StopWaitingElapsedTimer();
        StopClipboardPolling();
        DisposeCodeToolWatcher();
        DisposePreview();
        DisposeTemplatePreviews();
        _revisions.Clear();
        _chatMessages.Clear();
        _pinnedCapabilityIds.Clear();
        _apiDocsDrawerHandle?.RefreshPinnedState();
        _refreshActiveComposerCapabilityChrome = null;
        _refreshApiDocsToggleButtons = null;
        _startIdeaTextBox = null;
        _briefMarkdown = string.Empty;
        _previewSampleDataJson = null;
        _isSampleDataMutedInPreview = false;
        _pendingRecoveryDraft = null;
        _wasReconciledExternalOnOpen = false;
        _selectedTargetNode = null;
        _isInspectorModeEnabled = false;
        _latestHealthReport = null;
        _revisionIndex = -1;
        _aiRevisionCount = 0;
        _uncommittedEditCount = 0;
        _committed = false;
        _selectedEmoji = null;
        _startDraftIdea = string.Empty;
        _refineDraftIdea = string.Empty;
        _pendingPrompt = string.Empty;
        _preferWebAiInSession = false;
        _lastBrokenJson = null;
        _lastDiagnostics = [];
        _targetWidgetName = null;
        _isInPlaceGenerating = false;
        _isWebAiRepairFlow = false;
        _inFlightTargetNodeLabel = null;
        _inFlightDetectedWidgetName = string.Empty;
    }

    private static string? TryReadWidgetManifest(string widgetId)
    {
        try
        {
            CustomWidgetDataStore store = CustomWidgetDataStore.ForWidget(widgetId);
            return File.Exists(store.ManifestPath)
                ? File.ReadAllText(store.ManifestPath, Encoding.UTF8)
                : null;
        }
        catch (Exception ex)
        {
            App.Log($"[CustomWidgetStudio] Read manifest failed: {ex.Message}");
            return null;
        }
    }

    private void UpdateWindowTitle()
    {
        string title = IsEditMode
            ? _loc.Format(
                "CustomWidget.Workshop.EditTitle",
                string.IsNullOrWhiteSpace(_targetWidgetName) ? _loc.T("CustomWidget.Title") : _targetWidgetName)
            : _loc.T("CustomWidget.Workshop.Title");
        Title = title;
        TitleTextBlock.Text = title;
        RefreshTitleBarContextBadge();
    }

    private void RefreshTitleBarContextBadge()
    {
        // Keep the 38px window caption bar calm and unadorned; widget title and
        // version selector live inside the workspace command bar below.
        TitleBarContextBadge.Visibility = Visibility.Collapsed;
    }

    private async Task<DiscardDecision> AskDiscardOrSaveDraftAsync()
    {
        if (_isDialogOpen || RootGrid.XamlRoot is null)
        {
            return DiscardDecision.KeepEditing;
        }

        _isDialogOpen = true;
        try
        {
            var dialog = new ContentDialog
            {
                XamlRoot = RootGrid.XamlRoot,
                RequestedTheme = RootGrid.ActualTheme,
                Title = _loc.T("CustomWidget.Workshop.Discard.Title"),
                Content = _loc.T("CustomWidget.Workshop.Discard.Body"),
                PrimaryButtonText = _loc.T("CustomWidget.Workshop.Discard.Confirm"),
                SecondaryButtonText = _loc.T("CustomWidget.Workshop.CoCreate.SaveDraftAndClose"),
                CloseButtonText = _loc.T("CustomWidget.Workshop.Discard.Keep"),
                DefaultButton = ContentDialogButton.Close
            };
            if (Application.Current.Resources.TryGetValue("DefaultContentDialogStyle", out object? style) &&
                style is Style dialogStyle)
            {
                dialog.Style = dialogStyle;
            }

            ContentDialogResult result = await dialog.ShowAsync();
            return result switch
            {
                ContentDialogResult.Primary => DiscardDecision.DiscardCleanly,
                ContentDialogResult.Secondary => DiscardDecision.SaveDraftAndClose,
                _ => DiscardDecision.KeepEditing
            };
        }
        catch (Exception ex)
        {
            App.Log($"[CustomWidgetStudio] Discard dialog failed: {ex.Message}");
            return DiscardDecision.KeepEditing;
        }
        finally
        {
            _isDialogOpen = false;
        }
    }

    private async Task<bool> ConfirmDiscardCleanlyAsync()
    {
        DiscardDecision decision = await AskDiscardOrSaveDraftAsync();
        if (decision == DiscardDecision.DiscardCleanly)
        {
            DiscardActiveDraftCleanly();
            return true;
        }

        if (decision == DiscardDecision.SaveDraftAndClose)
        {
            PersistActiveDraftIfDirty();
            return true;
        }

        return false;
    }

    private async void AppWindow_Closing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_allowClose)
        {
            return;
        }

        if (!HasUnsavedWork)
        {
            _allowClose = true;
            return;
        }

        args.Cancel = true;
        DiscardDecision decision = await AskDiscardOrSaveDraftAsync();
        if (decision == DiscardDecision.DiscardCleanly)
        {
            DiscardActiveDraftCleanly();
            _allowClose = true;
            Close();
        }
        else if (decision == DiscardDecision.SaveDraftAndClose)
        {
            PersistActiveDraftIfDirty();
            _allowClose = true;
            Close();
        }
    }

    private void RootGrid_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Escape || _isDialogOpen)
        {
            return;
        }

        e.Handled = true;
        if (_isApiDocsDrawerOpen)
        {
            CloseApiDocsDrawer();
            return;
        }

        if (_isInPlaceGenerating)
        {
            CancelWaiting();
            return;
        }

        if (_selectedTargetNode is not null)
        {
            _selectedTargetNode = null;
            if (_previewContent is not null)
            {
                _previewContent.SelectedNodePath = null;
            }
            RefreshTargetNodePillChrome();
            return;
        }

        Close();
    }

    private void ResizeAndCenter(WindowId displayWindowId)
    {
        DisplayArea displayArea = DisplayArea.GetFromWindowId(displayWindowId, DisplayAreaFallback.Primary);
        RectInt32 workArea = displayArea.WorkArea;
        double scale = Win32Helper.GetDpiScaleForWindow(_hWnd, RootGrid.XamlRoot);
        int margin = (int)Math.Round(WorkAreaMargin * scale);
        int minWidth = (int)Math.Round(MinimumWidth * scale);
        int minHeight = (int)Math.Round(MinimumHeight * scale);
        int width = Math.Clamp((int)Math.Round(DesiredWidth * scale), minWidth, Math.Max(minWidth, workArea.Width - margin));
        int height = Math.Clamp((int)Math.Round(DesiredHeight * scale), minHeight, Math.Max(minHeight, workArea.Height - margin));
        _appWindow.MoveAndResize(new RectInt32(
            workArea.X + Math.Max(0, (workArea.Width - width) / 2),
            workArea.Y + Math.Max(0, (workArea.Height - height) / 2),
            width,
            height));
    }

    private void AppTitleBar_ActualThemeChanged(FrameworkElement sender, object args)
    {
        ApplyTitleBarColors();
        ApplyStageBackdropVisuals();
        if (!_isClosed && !_isDialogOpen && !_isInPlaceGenerating)
        {
            if (_page == StudioPage.Start)
            {
                ShowStartPage(_startDraftIdea);
            }
            else if (_page == StudioPage.Preview)
            {
                ShowPreviewPage(justLoaded: false);
            }
        }
    }

    private void ApplyTitleBarColors()
    {
        bool isDark = RootGrid.ActualTheme switch
        {
            ElementTheme.Dark => true,
            ElementTheme.Light => false,
            _ => Win32Helper.IsSystemDarkMode()
        };
        AppWindowTitleBar titleBar = _appWindow.TitleBar;
        titleBar.ButtonBackgroundColor = Colors.Transparent;
        titleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
        titleBar.ButtonForegroundColor = isDark ? Colors.White : Colors.Black;
        titleBar.ButtonInactiveForegroundColor = isDark
            ? Windows.UI.Color.FromArgb(0xA0, 0xFF, 0xFF, 0xFF)
            : Windows.UI.Color.FromArgb(0xA0, 0x10, 0x10, 0x10);
    }

    private void CustomWidgetStudioWindow_Closed(object sender, WindowEventArgs args)
    {
        _isClosed = true;
        CustomWidgetRemoteCatalogService.Shared.CatalogUpdated -= OnRemoteCatalogUpdated;
        CancelGeneration();
        StopWaitingElapsedTimer();
        StopClipboardPolling();
        DisposeCodeToolWatcher();
        DisposePreview();
        DisposeTemplatePreviews();
        _appWindow.Closing -= AppWindow_Closing;
        AppTitleBar.ActualThemeChanged -= AppTitleBar_ActualThemeChanged;
        WindowsCompatibilityService.ReleaseTitleBarExtensions(this, _appWindow);
        App.Current.WidgetManager?.ReleaseRaisedBandGuest(_hWnd, "custom-widget-studio-closed");
        Closed -= CustomWidgetStudioWindow_Closed;
    }

    private enum DiscardDecision
    {
        KeepEditing,
        SaveDraftAndClose,
        DiscardCleanly
    }

    private enum StudioPage
    {
        Start,
        Waiting,
        Preview
    }

    private enum WaitingKind
    {
        Web,
        Direct
    }

    private enum StageBackdropMode
    {
        Wallpaper,
        Light,
        Dark
    }
}
