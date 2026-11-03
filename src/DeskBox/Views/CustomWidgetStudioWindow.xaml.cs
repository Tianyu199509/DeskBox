using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using DeskBox.Controls.WidgetContents;
using DeskBox.FileSafety;
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
    internal const int DesiredWidth = 1400;
    internal const int DesiredHeight = 900;
    private const int MinimumWidth = 760;
    private const int MinimumHeight = 560;
    private const int WorkAreaMargin = 12;
    private const uint WmGetMinMaxInfo = 0x0024;
    private const uint WmNcDestroy = 0x0082;
    private static readonly UIntPtr StudioWindowSubclassId = new(0xD05C0B77);

    private readonly IntPtr _hWnd;
    private readonly AppWindow _appWindow;
    private readonly Win32Helper.SubclassProc _windowSubclassProc;
    private bool _isSubclassInstalled;
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
    private readonly Dictionary<string, string> _sessionSettingOverrides = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _sessionSecretOverrides = new(StringComparer.Ordinal);
    private bool _isExternalImportSession;
    private bool _hasPromptedFirstRunSetupInSession;
    private Action? _refreshActiveComposerCapabilityChrome;
    private Action? _refreshApiDocsToggleButtons;
    private CustomWidgetApiDocsDialogHelper.ApiReferenceViewHandle? _apiDocsDrawerHandle;
    private bool _isApiDocsDrawerOpen;
    private double _preferredApiDrawerWidth = 488;
    private TextBox? _startIdeaTextBox;
    private DropDownButton? _activeChannelSelectorButton;
    private FrameworkElement? _activeAgentEntryTarget;
    private Button? _activeQuickSetupButton;
    private Button? _activeCapabilityPickerButton;
    private Action? _refreshActiveChannelSelector;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _onboardingAnimTimer;
    private int _onboardingTourRunId;
    private enum OnboardingOverlayKind { None, ChannelModal, StepTour }
    private OnboardingOverlayKind _activeOnboardingOverlayKind;
    private StudioAiChannelKind _activeStepTourChannel;
    private int _activeStepTourStep = 1;
    private int _galleryHydrationRunId;
    private Button? _activeComposerSubmitButton;
    private string _briefMarkdown = string.Empty;
    private string? _previewSampleDataJson;
    private StudioDraftSnapshot? _pendingRecoveryDraft;
    private bool _wasReconciledExternalOnOpen;
    private CustomWidgetPickedNode? _selectedTargetNode;
    private bool _isInspectorModeEnabled;
    private StageBackdropMode _stageBackdropMode;
    private readonly bool _hasSystemWallpaperAvailable;
    private bool _isSampleDataMutedInPreview;
    private bool _isCapsulePreviewMode;
    private CustomWidgetHealthReport? _latestHealthReport;

    private int _revisionIndex = -1;
    private int _aiRevisionCount;
    private int _uncommittedEditCount;
    private bool _committed;
    private string _startDraftIdea = string.Empty;
    // P0：首次生成前若尚未显式选择创作方式，暂存用户意图；选择确认后继续生成。
    private string? _pendingChannelSelectionIdea;
    // P2：正在预览的外部 Agent 暂存批次。非空时「放到桌面」必须走 Bridge 的 stage_commit，
    // 且只提交暂存区里那份原始内容，避免工坊另开一条落盘路径。
    private string? _pendingAgentStageId;
    private string? _pendingAgentStageManifest;
    private bool _isCommittingPendingAgentStage;
    private string _refineDraftIdea = string.Empty;
    private string _pendingPrompt = string.Empty;
    private bool _preferWebAiInSession;
    private string? _lastBrokenJson;
    private string? _lastBrokenLogicJs;
    private IReadOnlyList<CustomWidgetValidationDiagnostic> _lastDiagnostics = [];
    private string? _liveRuntimeScriptError;
    private int? _liveRuntimeScriptErrorLine;

    private StudioPage _page = StudioPage.Start;
    private StudioPage _pageBeforeWaiting = StudioPage.Start;
    private WaitingKind _waitingKind = WaitingKind.Web;
    private WebAiTargetPreset? _activeWebPreset;
    private bool _isInPlaceGenerating;
    private bool _isWebAiRepairFlow;
    private string? _openedWebAiTargetIdInSession;
    private bool _isWebAiSameTabFollowUp;
    private string? _lastStandaloneWebAiPrompt;
    private string? _inFlightTargetNodeLabel;
    private string _inFlightDetectedWidgetName = string.Empty;
    private bool _showRawStreamCodeInSession = true;
    private AiGenerationStage _inFlightStage = AiGenerationStage.Connecting;

    private CancellationTokenSource? _generationCts;
    private CancellationTokenSource? _i18nQueueCts;
    private bool _isLocaleCenterOpen;
    private bool _isI18nQueueRunning;
    private string? _activePreviewCultureOverride;
    private string? _activeI18nWorkingManifestJson;
    private CustomWidgetLocaleCatalog? _activeI18nWorkingCatalog;
    private CustomWidgetI18nSyncPlan? _cachedI18nSyncPlan;
    private string _cachedI18nSyncPlanKey = string.Empty;
    private readonly Dictionary<string, LocaleTranslationProgressUpdate> _liveLocaleJobUpdates =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _lastFailedLocaleErrors =
        new(StringComparer.OrdinalIgnoreCase);
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

        WindowsCompatibilityService.ApplySafeBackdrop(this);

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);

        _windowSubclassProc = CallbackDepth.WrapSubclassProc(WindowSubclassProc);
        _hWnd = WindowNative.GetWindowHandle(this);
        WindowId windowId = Win32Interop.GetWindowIdFromWindow(_hWnd);
        _appWindow = AppWindow.GetFromWindowId(windowId);
        AppBranding.ApplyWindowIcon(_appWindow);

        if (_appWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsResizable = true;
            presenter.IsMaximizable = true;
            presenter.IsMinimizable = true;
            // Minimum size is enforced per-monitor DPI via WM_GETMINMAXINFO hook below.

        }

        _hasSystemWallpaperAvailable = DesktopWallpaperReadOps.TryResolveCurrentWallpaperImagePath() is not null;
        _stageBackdropMode = _hasSystemWallpaperAvailable
            ? StageBackdropMode.Wallpaper
            : (Win32Helper.IsSystemDarkMode() ? StageBackdropMode.Dark : StageBackdropMode.Light);

        InstallMinimumSizeHook();
        App.Current.DisplayTopologyChanged += OnDisplayTopologyChanged;
        _appWindow.Closing += AppWindow_Closing;
        AppTitleBar.ActualThemeChanged += AppTitleBar_ActualThemeChanged;
        _loc.LanguageChanged += OnLocalizationLanguageChanged;
        CustomWidgetAgentStageStore.Changed += OnAgentStageStoreChanged;
        Closed += CustomWidgetStudioWindow_Closed;
        RootGrid.FlowDirection = CustomWidgetLocaleCatalog.IsRightToLeftCulture(_loc.CurrentCultureName)
            ? FlowDirection.RightToLeft
            : FlowDirection.LeftToRight;
        RootGrid.AllowDrop = true;
        RootGrid.DragOver += OnStudioRootDragOver;
        RootGrid.Drop += OnStudioRootDrop;
        ResizeAndCenter(windowId);
        ApplyTitleBarColors();
        _ = EnsureWallpaperLoadedAsync();
    }

    public IntPtr WindowHandle => _hWnd;

    /// <summary>True while the user has uncommitted changes in the current Studio session.</summary>
    public bool HasUnsavedWork => !_committed && (_uncommittedEditCount > 0 || _isI18nQueueRunning);

    private bool IsEditMode => !string.IsNullOrWhiteSpace(_targetWidgetId);

    private string? CurrentManifestJson =>
        _revisionIndex >= 0 && _revisionIndex < _revisions.Count
            ? _revisions[_revisionIndex].ManifestJson
            : null;

    private string? CurrentLogicJs =>
        _revisionIndex >= 0 && _revisionIndex < _revisions.Count
            ? _revisions[_revisionIndex].LogicJs
            : null;

    private CustomWidgetLocaleCatalog? CurrentLocales =>
        _revisionIndex >= 0 && _revisionIndex < _revisions.Count
            ? _revisions[_revisionIndex].Locales
            : null;

    private bool IsAnyStudioJobRunning => _isInPlaceGenerating || _isI18nQueueRunning;

    private string EffectivePreviewCultureName =>
        !string.IsNullOrWhiteSpace(_activePreviewCultureOverride)
            ? _activePreviewCultureOverride!
            : _loc.CurrentCultureName;

    /// <summary>
    /// Starts a fresh session. <paramref name="widgetId"/> opens an existing
    /// widget for AI co-creation; <paramref name="initialIdea"/> immediately starts
    /// generating from a sentence typed elsewhere (for example in Settings).
    /// </summary>
    public void Initialize(string? widgetId, string? initialIdea, bool autoStartGeneration = true)
    {
        if (!string.IsNullOrWhiteSpace(widgetId) &&
            widgetId.Trim().StartsWith("fork:", StringComparison.OrdinalIgnoreCase))
        {
            InitializeForkFromWidget(widgetId.Trim()["fork:".Length..].Trim(), initialIdea, autoStartGeneration);
            return;
        }

        ResetSession();
        _targetWidgetId = string.IsNullOrWhiteSpace(widgetId) ? null : widgetId.Trim();

        if (IsEditMode)
        {
            _activeStartGalleryMode = StartGallerySourceMode.MyWidgets;
            string? existingJson = TryReadWidgetManifest(_targetWidgetId!);
            string? existingLogicJs = TryReadWidgetLogicJs(_targetWidgetId!);
            CustomWidgetLocaleCatalog? existingLocales = TryReadWidgetLocales(_targetWidgetId!);
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
                    if (existingLocales is { HasAnyLocale: true })
                    {
                        manifest = manifest.WithLocales(existingLocales);
                    }

                    if (CustomWidgetTemplates.IsEmojiIcon(manifest.Icon))
                    {
                        _selectedEmoji = manifest.Icon.Trim();
                    }

                    if (string.IsNullOrWhiteSpace(_targetWidgetName))
                    {
                        _targetWidgetName = manifest.ResolveLocalizedName(_loc.CurrentCultureName);
                    }

                    HydrateExistingWidgetSettingsForPreview(_targetWidgetId!, manifest);
                }

                StudioLoadedSession loaded = _sessionStore.LoadOrReconcileWidgetSession(
                    _targetWidgetId!,
                    existingJson,
                    _loc.T("CustomWidget.Workshop.CoCreate.ExternalSyncSummary"),
                    currentDiskLogicJs: existingLogicJs,
                    currentDiskLocales: existingLocales);

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
                        markDirty: false,
                        logicJs: existingLogicJs,
                        hasExplicitLogicJs: true,
                        locales: existingLocales,
                        hasExplicitLocales: true);
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
                if (autoStartGeneration)
                {
                    _ = BeginGenerationAsync(initialIdea);
                }
                else if (_refineTextBox is not null)
                {
                    _refineTextBox.Text = initialIdea;
                }
            }

            return;
        }

        ShowStartPage(initialIdea);
        if (autoStartGeneration && !string.IsNullOrWhiteSpace(initialIdea))
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
            if (!_isClosed && _page == StudioPage.Start)
            {
                if (_refreshTemplateGalleryFromRemote is not null)
                {
                    _refreshTemplateGalleryFromRemote();
                }
                else if (!HasUnsavedWork)
                {
                    ShowStartPage(_startDraftIdea);
                }
            }
        });
    }

    /// <summary>
    /// P2：外部 Agent 暂存 / 提交 / 忽略都在后台线程发生，这里只做一次线程切换。
    /// 在首屏只刷新提示条；在预览页等待 Agent 提交时自动将最新暂存包推入预览舞台。
    /// </summary>
    private void OnAgentStageStoreChanged()
    {
        if (_isClosed)
        {
            return;
        }

        DispatcherQueue.TryEnqueue(() =>
        {
            if (_isClosed)
            {
                return;
            }

            if (_page == StudioPage.Start)
            {
                _refreshAgentStageBanner?.Invoke();
                return;
            }

            if (_page == StudioPage.Preview && !_isCommittingPendingAgentStage)
            {
                TryAutoLoadLatestAgentStageIntoPreview();
            }
        });
    }

    private void TryAutoLoadLatestAgentStageIntoPreview()
    {
        IReadOnlyList<AgentStageSummary> pending = App.Current.GetPendingAgentStages();
        if (pending.Count == 0)
        {
            return;
        }

        AgentStageSummary latest = pending[0];
        if (string.Equals(latest.StageId, _pendingAgentStageId, StringComparison.Ordinal))
        {
            return;
        }

        bool isWaitingAgentMcp = _isInPlaceGenerating && _waitingKind == WaitingKind.AgentMcp;
        bool isUpdatingActiveStagedPreview = !string.IsNullOrWhiteSpace(_pendingAgentStageId);
        bool matchesCurrentEditTarget = !string.IsNullOrWhiteSpace(_targetWidgetId) &&
            !string.IsNullOrWhiteSpace(latest.WidgetId) &&
            string.Equals(_targetWidgetId, latest.WidgetId, StringComparison.OrdinalIgnoreCase) &&
            !HasUnsavedWork;

        if (!isWaitingAgentMcp && !isUpdatingActiveStagedPreview && !matchesCurrentEditTarget)
        {
            return;
        }

        if (!App.Current.TryReadAgentStage(latest.StageId, out AgentStageBundle? bundle, out _) || bundle is null)
        {
            return;
        }

        if (isWaitingAgentMcp &&
            !string.IsNullOrWhiteSpace(_targetWidgetId) &&
            !string.IsNullOrWhiteSpace(bundle.WidgetId) &&
            !string.Equals(_targetWidgetId, bundle.WidgetId, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        ApplyAgentStageToCurrentPreviewSession(bundle);
    }

    private void ApplyAgentStageToCurrentPreviewSession(AgentStageBundle bundle)
    {
        StopClipboardPolling();
        StopWaitingElapsedTimer();

        if (string.IsNullOrWhiteSpace(_targetWidgetId) &&
            !string.IsNullOrWhiteSpace(bundle.WidgetId) &&
            App.Current.WidgetManager?.ResolveCustomWidgetConfig(bundle.WidgetId) is { } existingCfg)
        {
            _targetWidgetId = existingCfg.Id;
            _targetWidgetName = existingCfg.Name;
        }

        CustomWidgetLocaleCatalog? locales = bundle.HasLocales
            ? CustomWidgetLocaleCatalog.ParseCatalogJson(bundle.LocalesJson)
            : null;
        string? prevStageId = _pendingAgentStageId;
        _pendingAgentStageId = bundle.StageId;
        string promptForHistory = _pendingPrompt;
        bool applied = HandleManifestCandidate(
            bundle.ManifestJson,
            promptForHistory,
            _loc.T("CustomWidget.Workshop.AgentStage.HistoryLabel"),
            source: "agent-stage",
            fencedLogicJs: bundle.HasLogicJs ? bundle.LogicJs : null,
            externalLocales: bundle.HasLocales ? locales : null,
            hasExplicitExternalFiles: bundle.HasLogicJs || bundle.HasLocales);
        if (!applied)
        {
            _pendingAgentStageId = prevStageId;
            return;
        }
        _pendingAgentStageManifest = CurrentManifestJson;
        UpdateWindowTitle();
        ShowPageMessage(
            InfoBarSeverity.Informational,
            string.Empty,
            _loc.T("CustomWidget.Workshop.AgentStage.ReviewHint"),
            null);
    }

    /// <summary>
    /// P2：打开一份来自外部工具的待确认暂存，复用常规预览舞台。
    /// 预览期间桌面不变；只有「放到桌面」触发 Bridge 的 stage_commit。
    /// </summary>
    public async void OpenAgentStagePreview(string stageId)
    {
        try
        {
            await OpenAgentStagePreviewCoreAsync(stageId);
        }
        catch (Exception ex)
        {
            App.Log($"[CustomWidgetStudio] OpenAgentStagePreview failed: {ex.Message}");
        }
    }

    private async Task OpenAgentStagePreviewCoreAsync(string stageId)
    {
        if (string.IsNullOrWhiteSpace(stageId))
        {
            return;
        }

        if (_page != StudioPage.Start && HasUnsavedWork && !await ConfirmDiscardCleanlyAsync())
        {
            return;
        }

        if (!App.Current.TryReadAgentStage(stageId, out AgentStageBundle? bundle, out string error) || bundle is null)
        {
            ShowPageMessage(InfoBarSeverity.Error, string.Empty, error, null);
            return;
        }

        string preservedIdea = _startDraftIdea;
        ResetSession();
        _startDraftIdea = preservedIdea;

        WidgetConfig? existingTarget = !string.IsNullOrWhiteSpace(bundle.WidgetId)
            ? App.Current.WidgetManager?.ResolveCustomWidgetConfig(bundle.WidgetId)
            : null;
        if (existingTarget is not null)
        {
            _targetWidgetId = existingTarget.Id;
            _targetWidgetName = existingTarget.Name;
            string? existingJson = TryReadWidgetManifest(_targetWidgetId);
            string? existingLogicJs = TryReadWidgetLogicJs(_targetWidgetId);
            CustomWidgetLocaleCatalog? existingLocales = TryReadWidgetLocales(_targetWidgetId);
            if (!string.IsNullOrWhiteSpace(existingJson))
            {
                CustomWidgetParseResult existingParsed = CustomWidgetManifestParser.Parse(existingJson);
                if (existingParsed.Manifest is { } existingManifest)
                {
                    if (existingLocales is { HasAnyLocale: true })
                    {
                        existingManifest = existingManifest.WithLocales(existingLocales);
                    }
                    HydrateExistingWidgetSettingsForPreview(_targetWidgetId, existingManifest);
                }

                StudioLoadedSession loaded = _sessionStore.LoadOrReconcileWidgetSession(
                    _targetWidgetId,
                    existingJson,
                    _loc.T("CustomWidget.Workshop.CoCreate.ExternalSyncSummary"),
                    currentDiskLogicJs: existingLogicJs,
                    currentDiskLocales: existingLocales);
                _briefMarkdown = loaded.BriefMarkdown;
                _revisions.AddRange(loaded.Revisions);
                _revisionIndex = _revisions.Count - 1;
                _chatMessages.AddRange(loaded.ChatMessages);
            }
        }
        else
        {
            _targetWidgetId = null;
        }

        UpdateWindowTitle();

        CustomWidgetParseResult parsed = CustomWidgetManifestParser.Parse(bundle.ManifestJson);
        if (parsed.Manifest is not null && CustomWidgetTemplates.IsEmojiIcon(parsed.Manifest.Icon))
        {
            _selectedEmoji = parsed.Manifest.Icon.Trim();
        }

        string? effectiveLogicJs = bundle.HasLogicJs
            ? bundle.LogicJs
            : (IsEditMode ? CurrentLogicJs : null);
        CustomWidgetLocaleCatalog? locales = bundle.HasLocales
            ? CustomWidgetLocaleCatalog.ParseCatalogJson(bundle.LocalesJson)
            : (IsEditMode ? CurrentLocales : null);
        AddRevision(
            bundle.ManifestJson,
            _loc.T("CustomWidget.Workshop.AgentStage.HistoryLabel"),
            isAiChange: false,
            source: "agent-stage",
            isCommitted: false,
            markDirty: false,
            logicJs: effectiveLogicJs,
            hasExplicitLogicJs: bundle.HasLogicJs || IsEditMode,
            locales: locales,
            hasExplicitLocales: bundle.HasLocales || IsEditMode);

        // ResetSession 会清空暂存态，必须在 AddRevision 后绑定当前预览对应的暂存快照。
        _pendingAgentStageId = bundle.StageId;
        _pendingAgentStageManifest = CurrentManifestJson;

        ShowPreviewPage(justLoaded: true);
        ShowPageMessage(
            InfoBarSeverity.Informational,
            string.Empty,
            _loc.T("CustomWidget.Workshop.AgentStage.ReviewHint"),
            null);
    }

    public void OpenExternalManifestPreview(
        string manifestJson,
        string? sourceLabel = null,
        string? logicJs = null,
        CustomWidgetLocaleCatalog? locales = null,
        string? originKind = null)
    {
        if (string.IsNullOrWhiteSpace(manifestJson))
        {
            return;
        }

        if (_page != StudioPage.Start && HasUnsavedWork)
        {
            _ = OpenExternalManifestPreviewWithDiscardPromptAsync(
                manifestJson,
                sourceLabel,
                logicJs,
                locales,
                originKind);
            return;
        }

        OpenExternalManifestPreviewCore(manifestJson, sourceLabel, logicJs, locales, originKind);
    }

    private async Task OpenExternalManifestPreviewWithDiscardPromptAsync(
        string manifestJson,
        string? sourceLabel,
        string? logicJs,
        CustomWidgetLocaleCatalog? locales,
        string? originKind)
    {
        ShowWindow();
        if (!await ConfirmDiscardCleanlyAsync())
        {
            return;
        }

        OpenExternalManifestPreviewCore(manifestJson, sourceLabel, logicJs, locales, originKind);
    }

    private void OpenExternalManifestPreviewCore(
        string manifestJson,
        string? sourceLabel = null,
        string? logicJs = null,
        CustomWidgetLocaleCatalog? locales = null,
        string? originKind = null)
    {
        ResetSession();
        _targetWidgetId = null;
        UpdateWindowTitle();

        manifestJson = CustomWidgetTemplates.UpgradeLegacyInlineBundle(
            manifestJson,
            logicJs,
            locales,
            out string? effectiveLogicJs,
            out CustomWidgetLocaleCatalog? effectiveLocales);
        if (!string.IsNullOrWhiteSpace(effectiveLogicJs))
        {
            manifestJson = CustomWidgetTemplates.SyncManifestScriptPropertyJson(manifestJson, effectiveLogicJs);
        }

        string effectiveOrigin = string.IsNullOrWhiteSpace(originKind)
            ? CustomWidgetOriginKinds.Community
            : originKind!;
        manifestJson = CustomWidgetManifestMetadataStamper.StampProvenance(
            manifestJson,
            origin: effectiveOrigin,
            isCustomized: false);

        CustomWidgetParseResult parsed = CustomWidgetManifestParser.Parse(manifestJson);
        CustomWidgetManifest? effectiveManifest = parsed.Manifest is not null && effectiveLocales is { HasAnyLocale: true }
            ? parsed.Manifest.WithLocales(effectiveLocales)
            : parsed.Manifest;
        if (effectiveManifest is not null && CustomWidgetTemplates.IsEmojiIcon(effectiveManifest.Icon))
        {
            _selectedEmoji = effectiveManifest.Icon.Trim();
        }

        string label = !string.IsNullOrWhiteSpace(sourceLabel)
            ? sourceLabel!
            : effectiveManifest?.ResolveLocalizedName(_loc.CurrentCultureName) ?? _loc.T("CustomWidget.Title");
        AddRevision(
            manifestJson,
            _loc.Format("CustomWidget.Workshop.History.FromTemplate", label),
            isAiChange: false,
            source: "template",
            isCommitted: false,
            markDirty: false,
            logicJs: effectiveLogicJs,
            hasExplicitLogicJs: true,
            locales: effectiveLocales,
            hasExplicitLocales: true);
        _isExternalImportSession = true;
        ShowPreviewPage(justLoaded: true);
        RefreshExternalImportSetupBanner(effectiveManifest);
    }

    public void InitializeForkFromWidget(string sourceWidgetId, string? initialIdea = null, bool autoStartGeneration = true)
    {
        if (string.IsNullOrWhiteSpace(sourceWidgetId))
        {
            return;
        }

        if (_page != StudioPage.Start && HasUnsavedWork)
        {
            _ = InitializeForkFromWidgetWithDiscardPromptAsync(sourceWidgetId, initialIdea, autoStartGeneration);
            return;
        }

        InitializeForkFromWidgetCore(sourceWidgetId, initialIdea, autoStartGeneration);
    }

    private async Task InitializeForkFromWidgetWithDiscardPromptAsync(
        string sourceWidgetId,
        string? initialIdea,
        bool autoStartGeneration)
    {
        ShowWindow();
        if (!await ConfirmDiscardCleanlyAsync())
        {
            return;
        }

        InitializeForkFromWidgetCore(sourceWidgetId, initialIdea, autoStartGeneration);
    }

    private void InitializeForkFromWidgetCore(
        string sourceWidgetId,
        string? initialIdea = null,
        bool autoStartGeneration = true)
    {
        string trimmedSourceId = sourceWidgetId.Trim();
        string? existingJson = TryReadWidgetManifest(trimmedSourceId);
        if (string.IsNullOrWhiteSpace(existingJson))
        {
            Initialize(null, initialIdea, autoStartGeneration);
            return;
        }

        CustomWidgetManifest? sourceManifest = CustomWidgetManifestParser.Parse(existingJson).Manifest;
        CustomWidgetTrustStore.Shared.InvalidateCache();
        bool isUntrustedSource = !CustomWidgetTrustStore.Shared
            .GetProfile(trimmedSourceId, sourceManifest?.Origin)
            .TrustedLocalAuthoring;

        string? existingLogicJs = TryReadWidgetLogicJs(trimmedSourceId);
        CustomWidgetLocaleCatalog? existingLocales = TryReadWidgetLocales(trimmedSourceId);
        CustomWidgetForkBundleResult forkBundle = CustomWidgetOnboardingHelper.CreateForkBundle(
            existingJson!,
            existingLogicJs,
            existingLocales,
            _loc.CurrentCultureName,
            _loc.T("CustomWidget.Fork.TitleSuffix"),
            preserveUntrustedOrigin: isUntrustedSource);

        ResetSession();
        _targetWidgetId = null;
        _isExternalImportSession = isUntrustedSource;
        UpdateWindowTitle();

        CustomWidgetParseResult parsed = CustomWidgetManifestParser.Parse(forkBundle.ManifestJson);
        if (parsed.Manifest is not null && CustomWidgetTemplates.IsEmojiIcon(parsed.Manifest.Icon))
        {
            _selectedEmoji = parsed.Manifest.Icon.Trim();
        }

        AddRevision(
            forkBundle.ManifestJson,
            _loc.Format("CustomWidget.Workshop.History.FromTemplate", forkBundle.DisplayTitle),
            isAiChange: false,
            source: "fork",
            isCommitted: false,
            markDirty: false,
            logicJs: forkBundle.LogicJs,
            hasExplicitLogicJs: true,
            locales: forkBundle.Locales,
            hasExplicitLocales: true);

        ShowPreviewPage(justLoaded: true);
        if (!string.IsNullOrWhiteSpace(initialIdea))
        {
            if (autoStartGeneration)
            {
                _ = BeginGenerationAsync(initialIdea);
            }
            else if (_refineTextBox is not null)
            {
                _refineTextBox.Text = initialIdea;
            }
        }
    }

    private void RefreshExternalImportSetupBanner(CustomWidgetManifest? manifest)
    {
        if (manifest is null || _page != StudioPage.Preview)
        {
            return;
        }

        CustomWidgetSetupInspection inspection = CustomWidgetOnboardingHelper.InspectFirstRunSetupRequirements(
            manifest,
            EffectivePreviewCultureName,
            _sessionSettingOverrides,
            _sessionSecretOverrides);
        if (!inspection.RequiresAttention)
        {
            return;
        }

        var configureBtn = new Button
        {
            CornerRadius = new CornerRadius(6),
            Content = _loc.T("CustomWidget.Onboarding.ConfigureNowButton")
        };
        configureBtn.Click += async (_, _) => await PromptAndApplyFirstRunSetupInStudioAsync();

        ShowPageMessage(
            InfoBarSeverity.Informational,
            string.Empty,
            _loc.T("CustomWidget.Onboarding.BannerNeedsSetup"),
            configureBtn);
    }

    private async Task PromptAndApplyFirstRunSetupInStudioAsync()
    {
        if (_isDialogOpen || _isClosed || RootGrid.XamlRoot is null || string.IsNullOrWhiteSpace(CurrentManifestJson))
        {
            return;
        }

        CustomWidgetManifest? manifest = CustomWidgetManifestParser.Parse(CurrentManifestJson!).Manifest;
        if (manifest is null)
        {
            return;
        }

        if (CurrentLocales is { HasAnyLocale: true })
        {
            manifest = manifest.WithLocales(CurrentLocales);
        }

        CustomWidgetSetupInspection inspection = CustomWidgetOnboardingHelper.InspectFirstRunSetupRequirements(
            manifest,
            EffectivePreviewCultureName,
            _sessionSettingOverrides,
            _sessionSecretOverrides);
        if (!inspection.HasAnyConfigurableFields)
        {
            return;
        }

        _hasPromptedFirstRunSetupInSession = true;
        _isDialogOpen = true;
        CustomWidgetFirstRunSetupOutcome outcome;
        try
        {
            outcome = await CustomWidgetFirstRunSetupDialogHelper.ShowAsync(
                RootGrid.XamlRoot,
                RootGrid.ActualTheme,
                _loc,
                _hWnd,
                manifest,
                inspection,
                EffectivePreviewCultureName);
        }
        finally
        {
            _isDialogOpen = false;
        }

        if (!outcome.Applied)
        {
            return;
        }

        foreach ((string key, string val) in outcome.Settings)
        {
            _sessionSettingOverrides[key] = val;
            if (_previewVm is not null)
            {
                await _previewVm.SetSettingValueAsync(key, val);
            }
        }

        foreach ((string secKey, string secVal) in outcome.Secrets)
        {
            _sessionSecretOverrides[secKey] = secVal;
            if (_previewVm is not null)
            {
                await _previewVm.SetSecretValueAsync(secKey, secVal);
            }
        }

        if (_pageInfoBar is not null)
        {
            _pageInfoBar.IsOpen = false;
            _pageInfoBar.Visibility = Visibility.Collapsed;
        }

        RefreshExternalImportSetupBanner(manifest);
    }

    private void OnStudioRootDragOver(object sender, DragEventArgs e)
    {
        if (e.Handled || _isDialogOpen || _isClosed || IsAnyStudioJobRunning)
        {
            return;
        }

        if (DeskBoxDragData.HasDroppedFiles(e.DataView))
        {
            DataPackageOperation negotiated = DeskBoxDragData.GetFileAssociationOperation(
                e.DataView,
                e.AllowedOperations);
            if (negotiated != DataPackageOperation.None)
            {
                e.AcceptedOperation = negotiated;
                if (e.DragUIOverride is not null)
                {
                    e.DragUIOverride.IsCaptionVisible = true;
                    e.DragUIOverride.Caption = _loc.T("CustomWidget.Studio.ImportPackageButton");
                }
            }
        }
    }

    private async void OnStudioRootDrop(object sender, DragEventArgs e)
    {
        if (e.Handled || _isDialogOpen || _isClosed || IsAnyStudioJobRunning || !DeskBoxDragData.HasDroppedFiles(e.DataView))
        {
            return;
        }

        DataPackageOperation allowedOperations = e.AllowedOperations;
        DragOperationDeferral deferral = e.GetDeferral();
        try
        {
            using DroppedFileBatch batch = await DeskBoxDragData.TryGetDroppedFilesAsync(e.DataView);
            foreach (DroppedFilePath file in batch.Files)
            {
                if (!string.IsNullOrWhiteSpace(file.Path))
                {
                    string ext = Path.GetExtension(file.Path);
                    if (CustomWidgetPackageService.SupportedImportExtensions.Any(s =>
                            string.Equals(s, ext, StringComparison.OrdinalIgnoreCase)))
                    {
                        e.Handled = true;
                        e.AcceptedOperation = DeskBoxDragData.GetFileAssociationOperation(
                            e.DataView,
                            allowedOperations);
                        await ImportPackageFilePathInStudioAsync(file.Path);
                        return;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            App.Log($"[CustomWidgetStudio] Drag-drop package import failed: {ex.Message}");
        }
        finally
        {
            deferral.Complete();
        }
    }

    private async Task ImportPackageFromDiskInStudioAsync()
    {
        if (_isDialogOpen || _isClosed || _hWnd == IntPtr.Zero)
        {
            return;
        }

        if (_page != StudioPage.Start && HasUnsavedWork && !await ConfirmDiscardCleanlyAsync())
        {
            return;
        }

        try
        {
            string? pickedPath = await FileOpenPickerService.PickSingleFileAsync(
                _hWnd,
                CustomWidgetPackageService.SupportedImportExtensions.ToArray(),
                Microsoft.Windows.Storage.Pickers.PickerLocationId.Desktop);
            if (string.IsNullOrWhiteSpace(pickedPath))
            {
                return;
            }

            await ImportPackageFilePathInStudioAsync(pickedPath, alreadyConfirmedDiscard: true);
        }
        catch (CustomWidgetIncompatibleVersionException verEx)
        {
            ShowPageMessage(InfoBarSeverity.Error, string.Empty, verEx.Message, null);
        }
        catch (Exception ex)
        {
            App.Log($"[CustomWidgetStudio] Import package failed: {ex.Message}");
            ShowPageMessage(
                InfoBarSeverity.Error,
                string.Empty,
                _loc.Format("CustomWidget.Studio.Status.ImportFailed", ex.Message),
                null);
        }
    }

    private async Task ImportPackageFilePathInStudioAsync(string packagePath, bool alreadyConfirmedDiscard = false)
    {
        if (!alreadyConfirmedDiscard && _page != StudioPage.Start && HasUnsavedWork && !await ConfirmDiscardCleanlyAsync())
        {
            return;
        }

        try
        {
            CustomWidgetImportedBundle imported = await CustomWidgetPackageService.ReadPackageBundleAsync(packagePath);
            string displayLabel = Path.GetFileNameWithoutExtension(packagePath);
            OpenExternalManifestPreviewCore(
                imported.RawJson,
                displayLabel,
                imported.LogicJs,
                imported.Locales,
                CustomWidgetOriginKinds.Community);
        }
        catch (CustomWidgetIncompatibleVersionException verEx)
        {
            ShowPageMessage(InfoBarSeverity.Error, string.Empty, verEx.Message, null);
        }
        catch (Exception ex)
        {
            App.Log($"[CustomWidgetStudio] Import package failed: {ex.Message}");
            ShowPageMessage(
                InfoBarSeverity.Error,
                string.Empty,
                _loc.Format("CustomWidget.Studio.Status.ImportFailed", ex.Message),
                null);
        }
    }

    /// <summary>
    /// Re-targets an already open window. Unsaved AI work is never thrown away
    /// silently: the window is just brought to the front instead.
    /// </summary>
    public void RequestTarget(string? widgetId, string? initialIdea, bool autoStartGeneration = true)
    {
        if (!string.IsNullOrWhiteSpace(widgetId) &&
            widgetId.Trim().StartsWith("fork:", StringComparison.OrdinalIgnoreCase))
        {
            InitializeForkFromWidget(widgetId.Trim()["fork:".Length..].Trim(), initialIdea, autoStartGeneration);
            return;
        }

        bool sameTarget = string.Equals(
            string.IsNullOrWhiteSpace(widgetId) ? null : widgetId.Trim(),
            _targetWidgetId,
            StringComparison.Ordinal);
        if (HasUnsavedWork || (sameTarget && string.IsNullOrWhiteSpace(initialIdea) && _page != StudioPage.Start))
        {
            return;
        }

        Initialize(widgetId, initialIdea, autoStartGeneration);
    }

    public void ShowWindow()
    {
        if (_appWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized } minimizedPresenter)
        {
            minimizedPresenter.Restore();
        }
        _appWindow.Show();
        ApplyTitleBarColors();
        App.Current.WidgetManager?.BringAuxiliaryWindowToFront(_hWnd, "custom-widget-studio-shown");
        Activate();
        _ = Win32Helper.SetForegroundWindow(_hWnd);
    }

    internal void WriteStudioContextTo(System.Text.Json.Utf8JsonWriter writer, bool includeManifest = true)
    {
        DeskBoxAgentBridgeServer.WriteCommonStudioContextHeader(writer, hostOnline: true, _loc.CurrentCultureName);
        if (_isClosed)
        {
            writer.WriteBoolean("isOpen", false);
            return;
        }

        string? manifestJson = CurrentManifestJson;
        CustomWidgetManifest? parsedManifest = !string.IsNullOrWhiteSpace(manifestJson)
            ? CustomWidgetManifestParser.Parse(manifestJson).Manifest
            : null;
        string? resolvedId = !string.IsNullOrWhiteSpace(_targetWidgetId)
            ? _targetWidgetId
            : parsedManifest?.Id;
        string? resolvedName = !string.IsNullOrWhiteSpace(_targetWidgetName)
            ? _targetWidgetName
            : parsedManifest?.ResolveLocalizedName(EffectivePreviewCultureName);

        writer.WriteBoolean("isOpen", true);
        writer.WriteString("page", _page.ToString().ToLowerInvariant());
        writer.WriteBoolean("isEditMode", IsEditMode);
        if (!string.IsNullOrWhiteSpace(resolvedId))
        {
            writer.WriteString("widgetId", resolvedId);
        }
        if (!string.IsNullOrWhiteSpace(resolvedName))
        {
            writer.WriteString("widgetName", resolvedName);
        }
        writer.WriteString("activePreviewCulture", EffectivePreviewCultureName);
        writer.WriteBoolean("isCapsuleViewport", _isCapsulePreviewMode);
        writer.WriteBoolean("hasUnsavedChanges", HasUnsavedWork);
        writer.WriteNumber("revisionCount", _revisions.Count);

        writer.WriteStartArray("pinnedCapabilities");
        foreach (string capId in _pinnedCapabilityIds)
        {
            writer.WriteStringValue(capId);
        }
        writer.WriteEndArray();

        if (_selectedTargetNode is { } picked)
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

        if (!string.IsNullOrWhiteSpace(_pendingAgentStageId))
        {
            writer.WriteString("pendingPreviewStageId", _pendingAgentStageId);
        }

        if (!string.IsNullOrWhiteSpace(_briefMarkdown))
        {
            writer.WriteString("briefMarkdown", _briefMarkdown);
        }

        writer.WriteBoolean("hasLogicJs", !string.IsNullOrWhiteSpace(CurrentLogicJs));
        if (includeManifest && !string.IsNullOrWhiteSpace(manifestJson))
        {
            writer.WriteString("manifestJson", manifestJson);
        }
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
        bool markDirty = true,
        string? logicJs = null,
        bool hasExplicitLogicJs = false,
        CustomWidgetLocaleCatalog? locales = null,
        bool hasExplicitLocales = false)
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

        _cachedI18nSyncPlan = null;
        _cachedI18nSyncPlanKey = string.Empty;

        if (_revisionIndex >= 0 && _revisionIndex < _revisions.Count - 1)
        {
            _revisions.RemoveRange(_revisionIndex + 1, _revisions.Count - _revisionIndex - 1);
        }

        string sha = CustomWidgetStudioSessionStore.ComputeManifestSha256(
            manifestJson,
            effectiveLogicJs,
            effectiveLocales);
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
            ManifestJson = manifestJson,
            LogicJs = effectiveLogicJs,
            Locales = effectiveLocales
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

        string? normalizedName = !string.IsNullOrWhiteSpace(newName) ? newName!.Trim() : null;
        string? normalizedIcon = !string.IsNullOrWhiteSpace(newIcon) ? newIcon!.Trim() : null;
        if (!string.IsNullOrWhiteSpace(normalizedIcon) && CustomWidgetTemplates.IsEmojiIcon(normalizedIcon))
        {
            _selectedEmoji = normalizedIcon;
        }

        CustomWidgetLocaleCatalog? prevLocales = CurrentLocales;
        bool hasLocaleCatalog = prevLocales is { HasAnyLocale: true };
        string? rawManifestName = CustomWidgetManifestParser.Parse(current).Manifest?.Name;

        string updated = CustomWidgetManifestCompatibility.WithUpdatedMetadataJson(
            current,
            newName: normalizedName,
            newIcon: normalizedIcon,
            newDefaultSize: newSize,
            activeCulture: EffectivePreviewCultureName,
            preserveI18nNameBinding: hasLocaleCatalog);
        updated = CustomWidgetManifestCompatibility.StampRequiredMinAppVersion(updated);

        CustomWidgetLocaleCatalog? nextLocales = hasLocaleCatalog && normalizedName is not null
            ? CustomWidgetManifestCompatibility.WithUpdatedLocaleName(
                prevLocales,
                rawManifestName,
                normalizedName,
                EffectivePreviewCultureName)
            : prevLocales;

        bool localesChanged = !string.Equals(
            prevLocales?.ComputeSha256() ?? string.Empty,
            nextLocales?.ComputeSha256() ?? string.Empty,
            StringComparison.Ordinal);
        if (string.Equals(updated, current, StringComparison.Ordinal) && !localesChanged)
        {
            return;
        }

        AddRevision(
            updated,
            _loc.T("CustomWidget.Workshop.CoCreate.ManualQuickEdit"),
            isAiChange: false,
            source: "manual",
            isCommitted: false,
            markDirty: true,
            locales: nextLocales,
            hasExplicitLocales: true);
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

        CustomWidgetManifest effectiveManifest = CurrentLocales is { HasAnyLocale: true }
            ? parsed.Manifest.WithLocales(CurrentLocales)
            : parsed.Manifest;

        CustomWidgetStorePayload? storeSnapshot = BuildLiveOrPreviewStoreSnapshot();
        double measuredW = 0;
        double measuredH = 0;
        if (_previewContent is not null)
        {
            var natural = _previewContent.MeasureNaturalContentSize(
                Math.Max(220, effectiveManifest.DefaultSize.Width));
            measuredW = natural.MeasuredWidth;
            measuredH = natural.MeasuredHeight;
        }

        return CustomWidgetHealthAnalyzer.AnalyzeHealth(
            rawManifestJson: manifestJson,
            manifest: effectiveManifest,
            storePayload: storeSnapshot,
            measuredWidth: measuredW,
            measuredHeight: measuredH,
            cultureName: EffectivePreviewCultureName,
            logicJs: CurrentLogicJs);
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

    private (CustomWidgetParseResult Result, string NormalizedJson, CustomWidgetLocaleCatalog? EffectiveLocales) ValidateCandidate(
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

        return (
            CustomWidgetScriptValidator.ValidateWorkspaceBundle(normalized, candidateLogicJs, effectiveLocales),
            normalized,
            effectiveLocales);
    }

    private static string? InspectNodeScriptSummary(string? manifestJson, string? nodePath)
    {
        if (string.IsNullOrWhiteSpace(manifestJson) || string.IsNullOrWhiteSpace(nodePath))
        {
            return null;
        }

        try
        {
            CustomWidgetManifest? manifest = CustomWidgetManifestParser.Parse(manifestJson).Manifest;
            if (manifest is null)
            {
                return null;
            }

            CustomWidgetNodeSpec? found = FindNodeByPath(manifest.View, nodePath);
            if (found is null)
            {
                return null;
            }

            var fns = new List<string>();
            void CollectFns(IReadOnlyList<CustomWidgetActionSpec> actions)
            {
                foreach (CustomWidgetActionSpec a in actions)
                {
                    if (string.Equals(a.Type, "script.run", StringComparison.OrdinalIgnoreCase) &&
                        !string.IsNullOrWhiteSpace(a.Fn) &&
                        !fns.Contains(a.Fn.Trim(), StringComparer.Ordinal))
                    {
                        fns.Add(a.Fn.Trim());
                    }
                    if (a.Then is { Count: > 0 })
                    {
                        CollectFns(a.Then);
                    }
                }
            }

            CollectFns(found.OnClick);
            CollectFns(found.OnSubmit);
            CollectFns(found.OnChange);

            var computedKeys = new List<string>();
            void CollectComputed(string? expr)
            {
                if (string.IsNullOrWhiteSpace(expr))
                {
                    return;
                }
                int idx = expr.IndexOf("$computed.", StringComparison.Ordinal);
                while (idx >= 0)
                {
                    int start = idx + "$computed.".Length;
                    int end = start;
                    while (end < expr.Length && (char.IsLetterOrDigit(expr[end]) || expr[end] == '_'))
                    {
                        end++;
                    }
                    if (end > start)
                    {
                        string key = expr[start..end];
                        if (!computedKeys.Contains(key, StringComparer.Ordinal))
                        {
                            computedKeys.Add(key);
                        }
                    }
                    idx = expr.IndexOf("$computed.", end, StringComparison.Ordinal);
                }
            }

            CollectComputed(found.Text);
            CollectComputed(found.Title);
            CollectComputed(found.Subtitle);
            CollectComputed(found.Value);
            CollectComputed(found.Items);
            CollectComputed(found.Collection);
            CollectComputed(found.Sparkline);

            var parts = new List<string>(2);
            if (fns.Count > 0)
            {
                parts.Add("⚡ " + string.Join(", ", fns));
            }
            if (computedKeys.Count > 0)
            {
                parts.Add("$computed." + string.Join(", $computed.", computedKeys.Take(2)));
            }

            return parts.Count > 0 ? string.Join(" · ", parts) : null;
        }
        catch
        {
            return null;
        }
    }

    private static CustomWidgetNodeSpec? FindNodeByPath(CustomWidgetNodeSpec node, string targetPath)
    {
        if (string.Equals(node.NodePath, targetPath, StringComparison.Ordinal))
        {
            return node;
        }

        if (node.ItemTemplate is not null && FindNodeByPath(node.ItemTemplate, targetPath) is { } fromTpl)
        {
            return fromTpl;
        }

        foreach (CustomWidgetNodeSpec hover in node.HoverActions)
        {
            if (FindNodeByPath(hover, targetPath) is { } fromHover)
            {
                return fromHover;
            }
        }

        foreach (CustomWidgetNodeSpec child in node.Children)
        {
            if (FindNodeByPath(child, targetPath) is { } fromChild)
            {
                return fromChild;
            }
        }

        return null;
    }

    private static string? FormatTargetNodePromptContext(string? manifestJson, string? nodePath)
    {
        if (string.IsNullOrWhiteSpace(nodePath))
        {
            return null;
        }

        string? scriptSummary = InspectNodeScriptSummary(manifestJson, nodePath);
        return string.IsNullOrWhiteSpace(scriptSummary)
            ? nodePath
            : $"{nodePath} (linked script bindings: {scriptSummary})";
    }

    private static string? FormatPickedNodeDisplayLabel(CustomWidgetPickedNode? picked, string? manifestJson = null)
    {
        if (picked is null)
        {
            return null;
        }

        string baseLabel = string.IsNullOrWhiteSpace(picked.DisplayLabel)
            ? picked.NodeType
            : $"{picked.NodeType} · {picked.DisplayLabel}";
        string? scriptSummary = InspectNodeScriptSummary(manifestJson, picked.NodePath);
        return string.IsNullOrWhiteSpace(scriptSummary)
            ? baseLabel
            : $"{baseLabel} ({scriptSummary})";
    }

    /// <summary>
    /// Applies an AI reply (from the clipboard or external code tool).
    /// Supports bare <c>manifest.json</c>, dual-block <c>manifest.json</c> + <c>logic.js</c>, and co-creation envelopes.
    /// </summary>
    private bool HandleManifestCandidate(
        string rawJson,
        string? prompt,
        string? fallbackLabel,
        string source = "ai",
        string? fencedLogicJs = null,
        CustomWidgetLocaleCatalog? externalLocales = null,
        bool hasExplicitExternalFiles = false)
    {
        string? prevJson = CurrentManifestJson;
        string? prevLogicJs = CurrentLogicJs;
        CustomWidgetLocaleCatalog? prevLocales = CurrentLocales;
        string? targetNodePath = _selectedTargetNode?.NodePath;
        string? targetNodeLabel = _inFlightTargetNodeLabel ?? FormatPickedNodeDisplayLabel(_selectedTargetNode, prevJson);

        string combinedInput = !string.IsNullOrWhiteSpace(fencedLogicJs) && !hasExplicitExternalFiles
            ? $"```json\n{rawJson}\n```\n```js\n{fencedLogicJs}\n```"
            : rawJson;

        StudioTurnResult parsedEnvelope = CustomWidgetAiGeneratorService.ParseStudioTurnResponse(
            combinedInput,
            preferredEmoji: _revisions.Count == 0 ? _selectedEmoji : null,
            currentLogicJs: hasExplicitExternalFiles ? fencedLogicJs : prevLogicJs,
            currentLocales: hasExplicitExternalFiles ? externalLocales : prevLocales,
            fallbackCulture: _loc.CurrentCultureName,
            currentManifestJson: hasExplicitExternalFiles ? null : prevJson);

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

        string? effectiveLogicJs = hasExplicitExternalFiles
            ? fencedLogicJs
            : CustomWidgetAiGeneratorService.ResolveEffectiveLogicJs(
                prevLogicJs,
                parsedEnvelope.LogicJsPatchMode,
                parsedEnvelope.LogicJs);
        CustomWidgetLocaleCatalog? effectiveLocales = hasExplicitExternalFiles
            ? CustomWidgetAiGeneratorService.MergeLocaleCatalogs(externalLocales, parsedEnvelope.Locales)
            : CustomWidgetAiGeneratorService.MergeLocaleCatalogs(prevLocales, parsedEnvelope.Locales);

        string candidateJson = !string.IsNullOrWhiteSpace(parsedEnvelope.ManifestJson)
            ? parsedEnvelope.ManifestJson
            : rawJson;

        (CustomWidgetParseResult parsed, string normalized, CustomWidgetLocaleCatalog? validatedLocales) = ValidateCandidate(
            candidateJson,
            effectiveLogicJs,
            effectiveLocales);
        bool hasErrors = parsed.Manifest is null || parsed.Diagnostics.Any(static d => d.IsError);
        if (hasErrors)
        {
            _lastBrokenJson = normalized;
            _lastBrokenLogicJs = effectiveLogicJs;
            _lastDiagnostics = parsed.Diagnostics;
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

            if (_isInPlaceGenerating && _waitingKind == WaitingKind.Web)
            {
                ShowPageMessage(
                    InfoBarSeverity.Error,
                    _loc.T("CustomWidget.Workshop.Diag.ErrorTitle"),
                    _loc.T("CustomWidget.Workshop.Diag.ErrorBodyWaiting"),
                    CreateRepairButton(),
                    BuildTechDetails());
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
                _loc.T("CustomWidget.Workshop.Diag.ErrorBody"),
                CreateRepairButton(),
                BuildTechDetails());
            return false;
        }

        bool cameFromWebAi = _isInPlaceGenerating && _waitingKind == WaitingKind.Web;
        StopClipboardPolling();
        StopWaitingElapsedTimer();
        _isInPlaceGenerating = false;
        _isWebAiRepairFlow = false;
        _isWebAiSameTabFollowUp = false;
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
            cameFromWebAi: cameFromWebAi,
            nextLogicJs: effectiveLogicJs,
            hasExplicitLogicJs: true,
            nextLocales: validatedLocales,
            hasExplicitLocales: true,
            previousLogicJs: prevLogicJs);
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
        bool cameFromWebAi = false,
        string? nextLogicJs = null,
        bool hasExplicitLogicJs = false,
        CustomWidgetLocaleCatalog? nextLocales = null,
        bool hasExplicitLocales = false,
        string? previousLogicJs = null)
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

        string? effectiveLogicJs = hasExplicitLogicJs ? nextLogicJs : CurrentLogicJs;
        CustomWidgetManifest? prevManifest = !string.IsNullOrWhiteSpace(previousManifestJson)
            ? CustomWidgetManifestParser.Parse(previousManifestJson!).Manifest
            : null;
        IReadOnlyList<string> diffPills = parsed.Manifest is not null
            ? CustomWidgetHealthAnalyzer.FormatDiffPills(
                CustomWidgetHealthAnalyzer.ComputeStructuralDiff(
                    prevManifest,
                    parsed.Manifest,
                    previousLogicJs,
                    effectiveLogicJs),
                Localized.ActiveCultureName)
            : [];

        bool isAi = string.Equals(source, "ai", StringComparison.OrdinalIgnoreCase);
        StudioPersistedRevision rev = AddRevision(
            normalizedManifestJson,
            label,
            isAiChange: isAi,
            source: source,
            isCommitted: false,
            markDirty: true,
            logicJs: nextLogicJs,
            hasExplicitLogicJs: hasExplicitLogicJs,
            locales: nextLocales,
            hasExplicitLocales: hasExplicitLocales);

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
            _lastBrokenLogicJs = rev.LogicJs;
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
                _loc.CurrentCultureName,
                brokenLogicJs: _lastBrokenLogicJs));
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

    private StudioAiChannelKind ResolveEffectiveStudioChannel(out string? activeDirectProviderId)
    {
        string prefChannel = _prefs.PreferredStudioChannel;
        if (string.Equals(prefChannel, CustomWidgetStudioPreferencesStore.ChannelAgentMcp, StringComparison.OrdinalIgnoreCase))
        {
            activeDirectProviderId = null;
            return StudioAiChannelKind.AgentMcp;
        }

        if (_preferWebAiInSession ||
            string.Equals(prefChannel, CustomWidgetStudioPreferencesStore.ChannelWeb, StringComparison.OrdinalIgnoreCase))
        {
            activeDirectProviderId = null;
            return StudioAiChannelKind.WebAi;
        }

        activeDirectProviderId = CustomWidgetWorkshopFlow.ResolveDirectProvider(
            CustomWidgetAiGeneratorService.Shared.GetConfiguredProviders(),
            _prefs.PreferredAiProviderId);
        if (string.Equals(prefChannel, CustomWidgetStudioPreferencesStore.ChannelDirect, StringComparison.OrdinalIgnoreCase))
        {
            return StudioAiChannelKind.DirectApi;
        }

        return activeDirectProviderId is not null
            ? StudioAiChannelKind.DirectApi
            : StudioAiChannelKind.WebAi;
    }

    private async Task BeginGenerationAsync(string? idea, bool forceWebAi = false, bool copyOnly = false)
    {
        string trimmed = idea?.Trim() ?? string.Empty;
        if (trimmed.Length == 0)
        {
            ShowPageMessage(InfoBarSeverity.Informational, string.Empty, _loc.T("CustomWidget.Workshop.EmptyIdea"), null);
            return;
        }

        // P0：未显式选择创作方式时，先让用户做选择，禁止静默降级到网页 AI。
        // forceWebAi / copyOnly 是用户从「更多方式」里做出的显式选择，不受此约束。
        if (!forceWebAi && !copyOnly &&
            string.IsNullOrWhiteSpace(_prefs.PreferredStudioChannel))
        {
            // 选择弹窗已经开着时不要再开一个：重开会 ++runId 让旧实例作废，
            // 用户连按两次 Enter 会看到弹窗闪一下重建、动画从头播。
            if (StudioOnboardingHost.Visibility == Visibility.Visible &&
                !string.IsNullOrWhiteSpace(_pendingChannelSelectionIdea))
            {
                _pendingChannelSelectionIdea = trimmed;
                return;
            }

            _pendingChannelSelectionIdea = trimmed;
            ShowAgentMcpOnboardingTour(forceShow: true);
            return;
        }

        if ((_isInPlaceGenerating && _waitingKind == WaitingKind.Direct) || _isI18nQueueRunning)
        {
            return;
        }

        if (_isInPlaceGenerating && (_waitingKind == WaitingKind.Web || _waitingKind == WaitingKind.AgentMcp))
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
        string? contextJson = CurrentManifestJson;
        _inFlightTargetNodeLabel = FormatPickedNodeDisplayLabel(_selectedTargetNode, contextJson);

        if (!forceWebAi && !copyOnly)
        {
            StudioAiChannelKind channel = ResolveEffectiveStudioChannel(out string? directProviderId);
            if (channel == StudioAiChannelKind.AgentMcp)
            {
                await StartAgentMcpFlowAsync(trimmed, contextJson);
                return;
            }

            if (channel == StudioAiChannelKind.DirectApi)
            {
                if (directProviderId is null)
                {
                    await ShowAiConfigDialogInStudioAsync(null, _refreshActiveChannelSelector);
                    directProviderId = CustomWidgetWorkshopFlow.ResolveDirectProvider(
                        CustomWidgetAiGeneratorService.Shared.GetConfiguredProviders(),
                        _prefs.PreferredAiProviderId);
                    if (directProviderId is null)
                    {
                        return;
                    }
                }

                await RunDirectGenerationAsync(directProviderId, trimmed, contextJson);
                return;
            }
        }

        StartWebAiFlow(trimmed, contextJson, copyOnly);
    }

    private async Task RunDirectGenerationAsync(string providerId, string prompt, string? contextJson)
    {
        CancelGeneration();
        var cts = new CancellationTokenSource();
        _generationCts = cts;

        string? currentLogicJs = CurrentLogicJs;
        CustomWidgetLocaleCatalog? currentLocales = CurrentLocales;
        string? targetNodePath = _selectedTargetNode?.NodePath;
        string? targetNodePromptCtx = FormatTargetNodePromptContext(contextJson, targetNodePath);
        string? targetNodeLabel = _inFlightTargetNodeLabel ?? FormatPickedNodeDisplayLabel(_selectedTargetNode, contextJson);
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
        _directThinkingCollapsedByUser = true;
        _chatStickToBottom = true;
        _streamStickToBottom = true;
        _thinkingStickToBottom = true;

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
                targetNodePath: targetNodePromptCtx,
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
                pinnedCapabilityIds: effectivePinnedCapabilities,
                currentLogicJs: currentLogicJs,
                currentLocales: currentLocales,
                isCapsuleViewport: _isCapsulePreviewMode);

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
                    previousLogicJs: currentLogicJs);
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
                    _prefs.SetPreferredStudioChannel(CustomWidgetStudioPreferencesStore.ChannelDirect);
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
                isSideDrawerMode: true,
                requestedTheme: RootGrid.ActualTheme);
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

    private CustomWidgetI18nSyncPlan? GetOrBuildCurrentI18nSyncPlan()
    {
        string? manifestJson = _activeI18nWorkingManifestJson ?? CurrentManifestJson;
        if (string.IsNullOrWhiteSpace(manifestJson))
        {
            return null;
        }

        string? logicJs = CurrentLogicJs;
        CustomWidgetLocaleCatalog? catalog = _activeI18nWorkingCatalog ?? CurrentLocales;
        string cacheKey =
            $"{CustomWidgetStudioSessionStore.ComputeManifestSha256(manifestJson!, logicJs, catalog)}|{_loc.CurrentCultureName}";
        if (_cachedI18nSyncPlan is not null &&
            string.Equals(_cachedI18nSyncPlanKey, cacheKey, StringComparison.Ordinal))
        {
            return _cachedI18nSyncPlan;
        }

        try
        {
            CustomWidgetI18nSyncPlan plan = CustomWidgetI18nEngine.BuildSyncPlan(
                manifestJson!,
                logicJs,
                catalog,
                fallbackCulture: _loc.CurrentCultureName);
            _cachedI18nSyncPlan = plan;
            _cachedI18nSyncPlanKey = cacheKey;
            return plan;
        }
        catch (Exception ex)
        {
            App.Log($"[CustomWidgetStudio] Build i18n sync plan failed: {ex.Message}");
            return null;
        }
    }

    private void OpenLocaleCenter(bool toggleIfAlreadyOpen = false)
    {
        _isLocaleCenterOpen = toggleIfAlreadyOpen ? !_isLocaleCenterOpen : true;
        RefreshLocaleCenterChrome();
    }

    private void SetActivePreviewCultureOverride(string? cultureCode)
    {
        string? normalized = string.IsNullOrWhiteSpace(cultureCode)
            ? null
            : CustomWidgetLocaleCatalog.NormalizeLocaleFileCode(cultureCode);
        if (normalized is not null &&
            (string.Equals(normalized, _loc.CurrentCultureName, StringComparison.OrdinalIgnoreCase) ||
             string.Equals(normalized, _activePreviewCultureOverride, StringComparison.OrdinalIgnoreCase)))
        {
            normalized = null;
        }

        _activePreviewCultureOverride = normalized;
        _previewVm?.SetPreviewCultureOverride(_activePreviewCultureOverride);
        UpdatePreviewTitlesForActiveCulture(
            _activeI18nWorkingManifestJson ?? CurrentManifestJson,
            _activeI18nWorkingCatalog ?? CurrentLocales);
        RefreshCapsulePreviewCardContent();
        RefreshStageToolbarChrome();
        RefreshLocaleCenterChrome();
    }

    private void UpdatePreviewTitlesForActiveCulture(string? manifestJson, CustomWidgetLocaleCatalog? locales)
    {
        if (string.IsNullOrWhiteSpace(manifestJson))
        {
            return;
        }

        CustomWidgetManifest? manifest = CustomWidgetManifestParser.Parse(manifestJson!).Manifest;
        if (manifest is not null && locales is { HasAnyLocale: true })
        {
            manifest = manifest.WithLocales(locales);
        }

        string title = manifest?.ResolveLocalizedName(EffectivePreviewCultureName)
                       ?? _targetWidgetName
                       ?? _loc.T("CustomWidget.Title");
        if (_nameText is not null)
        {
            _nameText.Text = title;
        }

        if (_frameTitleText is not null)
        {
            _frameTitleText.Text = title;
        }
    }

    private void PersistI18nCheckpointDraft(
        string workingManifestJson,
        string? workingLogicJs,
        CustomWidgetLocaleCatalog snapshotCatalog)
    {
        if (_committed)
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
            List<StudioPersistedRevision> baseRevs = _revisionIndex >= 0 && _revisionIndex < _revisions.Count
                ? _revisions.Take(_revisionIndex + 1).ToList()
                : _revisions.ToList();

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
                TargetWidgetId = _targetWidgetId,
                SelectedEmoji = _selectedEmoji ?? string.Empty,
                StartDraftIdea = _startDraftIdea,
                RefineDraftIdea = _refineDraftIdea,
                SelectedRevisionIndex = Math.Max(0, trimmed.Count - 1),
                BriefMarkdown = _briefMarkdown,
                PreviewSampleDataJson = _previewSampleDataJson,
                Revisions = trimmed,
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
            App.Log($"[CustomWidgetStudio] Persist i18n checkpoint draft failed: {ex.Message}");
        }
    }

    private void CancelI18nQueue()
    {
        try
        {
            _i18nQueueCts?.Cancel();
        }
        catch
        {
        }
    }

    private async Task RunIncrementalI18nBatchAsync(IReadOnlyList<string>? onlyCultures = null)
    {
        if (IsAnyStudioJobRunning || string.IsNullOrWhiteSpace(CurrentManifestJson))
        {
            return;
        }

        _isLocaleCenterOpen = true;
        string originalManifestJson = CurrentManifestJson!;
        string? currentLogicJs = CurrentLogicJs;
        CustomWidgetLocaleCatalog? initialCatalog = CurrentLocales;

        CustomWidgetI18nSyncPlan plan;
        try
        {
            plan = CustomWidgetI18nEngine.BuildSyncPlan(
                originalManifestJson,
                currentLogicJs,
                initialCatalog,
                fallbackCulture: _loc.CurrentCultureName);
            _cachedI18nSyncPlan = plan;
            _cachedI18nSyncPlanKey =
                $"{CustomWidgetStudioSessionStore.ComputeManifestSha256(originalManifestJson, currentLogicJs, initialCatalog)}|{_loc.CurrentCultureName}";
        }
        catch (Exception ex)
        {
            RefreshLocaleCenterChrome();
            ShowFailureMessage(
                "CustomWidget.Workshop.Failure.I18nTitle",
                "CustomWidget.Workshop.Failure.I18nBody",
                ex);
            return;
        }

        if (plan.RequiredKeys.Count == 0)
        {
            RefreshLocaleCenterChrome();
            ShowPageMessage(
                InfoBarSeverity.Informational,
                string.Empty,
                _loc.T("CustomWidget.Workshop.I18n.NoLocalizableText"),
                null);
            return;
        }

        HashSet<string>? scopeSet = onlyCultures is { Count: > 0 }
            ? new HashSet<string>(
                onlyCultures.Select(CustomWidgetLocaleCatalog.NormalizeLocaleFileCode),
                StringComparer.OrdinalIgnoreCase)
            : null;
        int scopedPendingCount = plan.LocalePlans.Count(lp =>
            (scopeSet is null || scopeSet.Contains(lp.Culture)) && lp.NeedsTranslation);

        string workingManifestJson = CustomWidgetManifestCompatibility.StampRequiredMinAppVersion(plan.CompiledManifestJson);
        bool seedCatalogChanged = !string.Equals(
            initialCatalog?.ComputeSha256() ?? string.Empty,
            plan.SeededCatalog.ComputeSha256(),
            StringComparison.Ordinal);
        bool manifestChanged = !string.Equals(originalManifestJson, workingManifestJson, StringComparison.Ordinal);

        if (scopedPendingCount == 0)
        {
            if (scopeSet is null)
            {
                _lastFailedLocaleErrors.Clear();
            }
            else
            {
                foreach (string c in scopeSet)
                {
                    _lastFailedLocaleErrors.Remove(c);
                }
            }

            if (manifestChanged || seedCatalogChanged)
            {
                AddRevision(
                    workingManifestJson,
                    _loc.T("CustomWidget.Workshop.I18n.CompiledBaseSummary"),
                    isAiChange: false,
                    source: "manual",
                    isCommitted: false,
                    markDirty: true,
                    logicJs: currentLogicJs,
                    hasExplicitLogicJs: true,
                    locales: plan.SeededCatalog,
                    hasExplicitLocales: true);
                PersistActiveDraftIfDirty();
                if (_page == StudioPage.Preview && _previewVm is not null && _previewContent is not null)
                {
                    ApplyLivePreviewUpdateInPlace();
                }
            }

            RefreshLocaleCenterChrome();
            ShowPageMessage(
                InfoBarSeverity.Success,
                string.Empty,
                _loc.T("CustomWidget.Workshop.I18n.AlreadyUpToDate"),
                null);
            return;
        }

        string? providerId = CustomWidgetWorkshopFlow.ResolveDirectProvider(
            CustomWidgetAiGeneratorService.Shared.GetConfiguredProviders(),
            _prefs.PreferredAiProviderId);
        if (providerId is null)
        {
            RefreshLocaleCenterChrome();
            await ShowAiConfigDialogInStudioAsync(null, null);
            providerId = CustomWidgetWorkshopFlow.ResolveDirectProvider(
                CustomWidgetAiGeneratorService.Shared.GetConfiguredProviders(),
                _prefs.PreferredAiProviderId);
            if (providerId is null)
            {
                RefreshLocaleCenterChrome();
                return;
            }
        }

        CancelI18nQueue();
        var cts = new CancellationTokenSource();
        _i18nQueueCts = cts;
        _isI18nQueueRunning = true;
        _activeI18nWorkingManifestJson = workingManifestJson;
        _activeI18nWorkingCatalog = plan.SeededCatalog;
        _cachedI18nSyncPlan = null;
        _liveLocaleJobUpdates.Clear();

        if (scopeSet is null)
        {
            _lastFailedLocaleErrors.Clear();
        }
        else
        {
            foreach (string c in scopeSet)
            {
                _lastFailedLocaleErrors.Remove(c);
            }
        }

        if (!IsEditMode)
        {
            try
            {
                string draftDir = _sessionStore.GetCreateDraftDirectory();
                Directory.CreateDirectory(draftDir);
                Interlocked.Exchange(ref _lastDraftWriteTick, Environment.TickCount64);
                CustomWidgetStorageFileOps.ReplaceTextFileAtomically(
                    Path.Combine(draftDir, "manifest.json"),
                    workingManifestJson);
                plan.SeededCatalog.WriteToDirectoryAtomically(
                    Path.Combine(draftDir, CustomWidgetDataStore.LocalesDirectoryName),
                    removeStaleFiles: false,
                    preferredKeyOrder: plan.RequiredKeys);
                Interlocked.Exchange(ref _lastDraftWriteTick, Environment.TickCount64);
            }
            catch (Exception seedEx)
            {
                App.Log($"[CustomWidgetStudio] Draft locale seed failed: {seedEx.Message}");
            }
        }

        if (_previewVm is not null)
        {
            _previewVm.ApplyPreviewBundle(
                workingManifestJson,
                currentLogicJs,
                plan.SeededCatalog,
                preserveRuntimeData: true);
            _previewVm.SetPreviewCultureOverride(_activePreviewCultureOverride);
        }

        UpdatePreviewTitlesForActiveCulture(workingManifestJson, plan.SeededCatalog);
        RefreshInPlaceGenerationChrome();
        RefreshLocaleCenterChrome();

        try
        {
            WidgetIncrementalI18nBatchResult batchResult =
                await CustomWidgetAiGeneratorService.Shared.RunIncrementalI18nQueueAsync(
                    providerId,
                    plan,
                    onlyCultures: onlyCultures,
                    onLocaleProgress: update =>
                    {
                        if (_isClosed)
                        {
                            return;
                        }

                        DispatcherQueue.TryEnqueue(() =>
                        {
                            if (_isClosed)
                            {
                                return;
                            }

                            _liveLocaleJobUpdates[update.Culture] = update;
                            if (update.State == LocaleTranslationJobState.Failed &&
                                !string.IsNullOrWhiteSpace(update.ErrorMessage))
                            {
                                _lastFailedLocaleErrors[update.Culture] = update.ErrorMessage!;
                            }
                            else if (update.State == LocaleTranslationJobState.Completed)
                            {
                                _lastFailedLocaleErrors.Remove(update.Culture);
                            }

                            RefreshLocaleCenterChrome();
                        });
                    },
                    onLocaleCompletedAsync: (culture, entries, snapshotCatalog) =>
                    {
                        if (_isClosed)
                        {
                            return Task.CompletedTask;
                        }

                        if (!IsEditMode)
                        {
                            try
                            {
                                string draftDir = _sessionStore.GetCreateDraftDirectory();
                                string localesDir = Path.Combine(draftDir, CustomWidgetDataStore.LocalesDirectoryName);
                                Interlocked.Exchange(ref _lastDraftWriteTick, Environment.TickCount64);
                                CustomWidgetLocaleCatalog.WriteSingleLocaleAtomically(
                                    localesDir,
                                    culture,
                                    entries,
                                    snapshotCatalog.Meta,
                                    plan.RequiredKeys);
                                Interlocked.Exchange(ref _lastDraftWriteTick, Environment.TickCount64);
                            }
                            catch (Exception diskEx)
                            {
                                App.Log($"[CustomWidgetStudio] Draft locale checkpoint write failed ({culture}): {diskEx.Message}");
                            }
                        }

                        DispatcherQueue.TryEnqueue(() =>
                        {
                            if (_isClosed)
                            {
                                return;
                            }

                            _activeI18nWorkingCatalog = snapshotCatalog;
                            _cachedI18nSyncPlan = null;
                            PersistI18nCheckpointDraft(workingManifestJson, currentLogicJs, snapshotCatalog);
                            if (_previewVm is not null)
                            {
                                _previewVm.ApplyPreviewBundle(
                                    workingManifestJson,
                                    currentLogicJs,
                                    snapshotCatalog,
                                    preserveRuntimeData: true);
                                _previewVm.SetPreviewCultureOverride(_activePreviewCultureOverride);
                            }

                            UpdatePreviewTitlesForActiveCulture(workingManifestJson, snapshotCatalog);
                            RefreshLocaleCenterChrome();
                        });
                        return Task.CompletedTask;
                    },
                    cancellationToken: cts.Token);

            if (_isClosed)
            {
                return;
            }

            _isI18nQueueRunning = false;
            _activeI18nWorkingManifestJson = null;
            _activeI18nWorkingCatalog = null;
            _liveLocaleJobUpdates.Clear();
            _cachedI18nSyncPlan = null;

            foreach (string doneCulture in batchResult.CompletedLocales)
            {
                _lastFailedLocaleErrors.Remove(doneCulture);
            }

            foreach ((string failedCulture, string errMsg) in batchResult.FailedLocales)
            {
                _lastFailedLocaleErrors[failedCulture] = errMsg;
            }

            bool anyCompleted = batchResult.CompletedLocales.Count > 0;
            bool finalCatalogChanged = !string.Equals(
                initialCatalog?.ComputeSha256() ?? string.Empty,
                batchResult.UpdatedCatalog.ComputeSha256(),
                StringComparison.Ordinal);

            if (anyCompleted || finalCatalogChanged || manifestChanged)
            {
                CustomWidgetParseResult validated = CustomWidgetScriptValidator.ValidateWorkspaceBundle(
                    workingManifestJson,
                    currentLogicJs,
                    batchResult.UpdatedCatalog);
                string finalJsonToStore = validated.Manifest is not null && !validated.Diagnostics.Any(static d => d.IsError)
                    ? workingManifestJson
                    : originalManifestJson;

                CustomWidgetI18nSyncPlan postPlan = CustomWidgetI18nEngine.BuildSyncPlan(
                    finalJsonToStore,
                    currentLogicJs,
                    batchResult.UpdatedCatalog,
                    fallbackCulture: _loc.CurrentCultureName);

                string summary = batchResult.Cancelled
                    ? _loc.Format("CustomWidget.Workshop.I18n.SummaryCancelled", batchResult.CompletedLocales.Count)
                    : batchResult.FailedLocales.Count > 0
                        ? _loc.Format(
                            "CustomWidget.Workshop.I18n.SummaryPartialFailed",
                            batchResult.CompletedLocales.Count,
                            batchResult.FailedLocales.Count)
                        : _loc.Format(
                            "CustomWidget.Workshop.I18n.SummaryDone",
                            postPlan.ReadyLocaleCount,
                            postPlan.TotalLocaleCount);

                string replyText = batchResult.Cancelled
                    ? _loc.Format("CustomWidget.Workshop.I18n.ReplyCancelled", batchResult.CompletedLocales.Count)
                    : batchResult.FailedLocales.Count > 0
                        ? _loc.Format(
                            "CustomWidget.Workshop.I18n.ReplyPartialFailed",
                            batchResult.CompletedLocales.Count,
                            batchResult.FailedLocales.Count)
                        : _loc.Format(
                            "CustomWidget.Workshop.I18n.ReplyDone",
                            batchResult.CompletedLocales.Count,
                            postPlan.ReadyLocaleCount,
                            postPlan.TotalLocaleCount);

                StudioPersistedRevision rev = AddRevision(
                    finalJsonToStore,
                    summary,
                    isAiChange: anyCompleted,
                    source: anyCompleted ? "ai" : "manual",
                    isCommitted: false,
                    markDirty: true,
                    logicJs: currentLogicJs,
                    hasExplicitLogicJs: true,
                    locales: batchResult.UpdatedCatalog,
                    hasExplicitLocales: true);

                AppendChatMessage(new StudioChatMessage
                {
                    Id = $"msg-i18n-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}",
                    Role = "assistant",
                    Kind = "edit",
                    Text = replyText,
                    LinkedRevisionId = rev.RevisionId,
                    DiffPills = [summary],
                    Suggestions = []
                });

                PersistActiveDraftIfDirty();
                if (_page == StudioPage.Preview && _previewVm is not null && _previewContent is not null)
                {
                    ApplyLivePreviewUpdateInPlace();
                }
                else
                {
                    ShowPreviewPage(justLoaded: true);
                }
            }
            else
            {
                if (_previewVm is not null && !string.IsNullOrWhiteSpace(CurrentManifestJson))
                {
                    _previewVm.ApplyPreviewBundle(
                        CurrentManifestJson!,
                        CurrentLogicJs,
                        CurrentLocales,
                        preserveRuntimeData: true);
                    _previewVm.SetPreviewCultureOverride(_activePreviewCultureOverride);
                }

                UpdatePreviewTitlesForActiveCulture(CurrentManifestJson, CurrentLocales);
                RefreshInPlaceGenerationChrome();
                RefreshLocaleCenterChrome();
            }

            if (!batchResult.Cancelled && batchResult.FailedLocales.Count > 0 && !string.IsNullOrWhiteSpace(batchResult.ErrorMessage))
            {
                ShowPageMessage(InfoBarSeverity.Warning, string.Empty, batchResult.ErrorMessage!, null);
            }
        }
        catch (OperationCanceledException)
        {
            if (!_isClosed)
            {
                _isI18nQueueRunning = false;
                _activeI18nWorkingManifestJson = null;
                _activeI18nWorkingCatalog = null;
                _liveLocaleJobUpdates.Clear();
                _cachedI18nSyncPlan = null;
                RefreshInPlaceGenerationChrome();
                RefreshLocaleCenterChrome();
            }
        }
        catch (Exception ex)
        {
            if (!_isClosed)
            {
                _isI18nQueueRunning = false;
                _activeI18nWorkingManifestJson = null;
                _activeI18nWorkingCatalog = null;
                _liveLocaleJobUpdates.Clear();
                _cachedI18nSyncPlan = null;
                RefreshInPlaceGenerationChrome();
                RefreshLocaleCenterChrome();
                ShowPageMessage(InfoBarSeverity.Error, string.Empty, ex.Message, null);
            }
        }
        finally
        {
            if (ReferenceEquals(_i18nQueueCts, cts))
            {
                _i18nQueueCts = null;
            }

            cts.Dispose();
        }
    }

    private void StartWebAiFlow(string prompt, string? contextJson, bool copyOnly)
    {
        IReadOnlyList<string> effectivePinnedCapabilities = ResolveEffectivePinnedCapabilitiesForPrompt(prompt);
        string? targetNodePromptCtx = FormatTargetNodePromptContext(contextJson, _selectedTargetNode?.NodePath);
        string fullPrompt = CustomWidgetTemplates.BuildChatAiPrompt(
            prompt,
            contextJson,
            _revisions.Count == 0 ? _selectedEmoji : null,
            _loc.CurrentCultureName,
            briefMarkdown: _briefMarkdown,
            targetNodePath: targetNodePromptCtx,
            pinnedCapabilityIds: effectivePinnedCapabilities,
            existingLogicJs: CurrentLogicJs,
            isCapsuleViewport: _isCapsulePreviewMode,
            existingLocales: CurrentLocales);
        _lastStandaloneWebAiPrompt = fullPrompt;

        WebAiTargetPreset preset = copyOnly
            ? CustomWidgetStudioPreferencesStore.ResolveWebAiPreset(CustomWidgetStudioPreferencesStore.TargetCopyOnly)
            : CustomWidgetStudioPreferencesStore.ResolveWebAiPreset(
                _prefs.ResolveEffectiveWebAiTarget(_loc.CurrentCultureName));
        bool reuseOpenTab = !copyOnly &&
                            !string.IsNullOrWhiteSpace(contextJson) &&
                            string.Equals(_openedWebAiTargetIdInSession, preset.Id, StringComparison.OrdinalIgnoreCase);

        _activeWebPreset = preset;
        _waitingKind = WaitingKind.Web;
        _isWebAiRepairFlow = false;
        _isWebAiSameTabFollowUp = reuseOpenTab;
        _isInPlaceGenerating = true;

        if (reuseOpenTab)
        {
            string followUpPrompt = CustomWidgetTemplates.BuildChatAiFollowUpPrompt(
                prompt,
                _loc.CurrentCultureName,
                targetNodePath: targetNodePromptCtx,
                pinnedCapabilityIds: effectivePinnedCapabilities,
                isCapsuleViewport: _isCapsulePreviewMode,
                hasExistingLogicJs: !string.IsNullOrWhiteSpace(CurrentLogicJs));
            SetClipboardText(followUpPrompt);
        }
        else
        {
            SetClipboardText(fullPrompt);
            if (!copyOnly)
            {
                _openedWebAiTargetIdInSession = preset.Id;
                OpenWebAi(preset);
            }
        }

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
        CancelI18nQueue();
        StopOnboardingAnimationTimer();
        StopWaitingElapsedTimer();
        StopClipboardPolling();
        _isInPlaceGenerating = false;
        _isWebAiRepairFlow = false;
        _isWebAiSameTabFollowUp = false;
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
        try
        {
            await ClipboardPollTimerTickCoreAsync();
        }
        catch (Exception ex)
        {
            App.Log($"[CustomWidgetStudio] Clipboard poll failed: {ex.Message}");
        }
    }

    private async Task ClipboardPollTimerTickCoreAsync()
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

            if (CustomWidgetWorkshopFlow.TryExtractWorkspaceReply(
                    text,
                    _lastSelfCopiedText,
                    out string manifestJson,
                    out string? fencedJs))
            {
                BringToFrontForReply();
                HandleManifestCandidate(manifestJson, _pendingPrompt, null, fencedLogicJs: fencedJs);
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
        (string extracted, string? fencedJs) = CustomWidgetTemplates.ExtractWorkspaceBundleFromClipboardText(text);
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

        HandleManifestCandidate(extracted, _pendingPrompt, null, fencedLogicJs: fencedJs);
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
            if (_appWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized } minimizedPresenter)
            {
                minimizedPresenter.Restore();
            }
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
              { "type": "Text", "text": "Ready for external Agent preview.", "style": "bodyStrong" }
            ]
          }
        }
        """;

    private async Task CopyAgentKickoffPromptAsync(string? idea)
    {
        string trimmed = idea?.Trim() ?? string.Empty;
        if (trimmed.Length > 0)
        {
            _preferWebAiInSession = false;
            _prefs.SetPreferredStudioChannel(CustomWidgetStudioPreferencesStore.ChannelAgentMcp);
            await BeginGenerationAsync(trimmed);
            return;
        }

        try
        {
            string prompt = CustomWidgetAgentConnectorService.BuildStudioAgentMcpPrompt(
                userIdea: string.Empty,
                uiLanguage: _loc.CurrentCultureName,
                targetWidgetId: _targetWidgetId,
                targetWidgetName: _targetWidgetName,
                currentManifestJson: CurrentManifestJson,
                briefMarkdown: _briefMarkdown,
                targetNodeContext: FormatTargetNodePromptContext(CurrentManifestJson, _selectedTargetNode?.NodePath),
                pinnedCapabilityIds: ResolveEffectivePinnedCapabilitiesForPrompt(null),
                isCapsuleViewport: _isCapsulePreviewMode);
            SetClipboardText(prompt);
            ShowPageMessage(
                InfoBarSeverity.Success,
                string.Empty,
                _loc.T("CustomWidget.Workshop.AgentTaskCopied"),
                null);
        }
        catch (Exception ex)
        {
            ShowFailureMessage(
                "CustomWidget.Workshop.Failure.ClipboardTitle",
                "CustomWidget.Workshop.Failure.ClipboardBody",
                ex);
        }
    }

    private Task StartAgentMcpFlowAsync(string prompt, string? contextJson)
    {
        CancelGeneration();
        StopWaitingElapsedTimer();
        StopClipboardPolling();
        DisposeCodeToolWatcher();

        string fullPrompt = CustomWidgetAgentConnectorService.BuildStudioAgentMcpPrompt(
            userIdea: prompt,
            uiLanguage: _loc.CurrentCultureName,
            targetWidgetId: _targetWidgetId,
            targetWidgetName: _targetWidgetName,
            currentManifestJson: contextJson,
            manifestFilePath: null,
            briefMarkdown: _briefMarkdown,
            targetNodeContext: FormatTargetNodePromptContext(contextJson, _selectedTargetNode?.NodePath),
            pinnedCapabilityIds: ResolveEffectivePinnedCapabilitiesForPrompt(prompt),
            isCapsuleViewport: _isCapsulePreviewMode);

        SetClipboardText(fullPrompt);

        _waitingKind = WaitingKind.AgentMcp;
        _isWebAiRepairFlow = false;
        _isInPlaceGenerating = true;
        _chatStickToBottom = true;

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

        return Task.CompletedTask;
    }

    internal string? TryGetTargetWidgetIdForAgentUpsert()
    {
        if (_isClosed || string.IsNullOrWhiteSpace(_targetWidgetId))
        {
            return null;
        }

        if (_waitingKind == WaitingKind.AgentMcp ||
            _page == StudioPage.Preview ||
            string.Equals(_prefs.PreferredStudioChannel, CustomWidgetStudioPreferencesStore.ChannelAgentMcp, StringComparison.OrdinalIgnoreCase))
        {
            return _targetWidgetId;
        }

        return null;
    }

    internal void OnExternalAgentWidgetUpserted(
        string widgetId,
        string widgetName,
        string manifestJson,
        string? logicJs,
        bool hasLogicJs,
        CustomWidgetLocaleCatalog? locales,
        bool hasLocales)
    {
        if (_isClosed || string.IsNullOrWhiteSpace(manifestJson))
        {
            return;
        }

        // P2：工坊自己点击「放到桌面」调用 stage_commit 时，Bridge 仍会广播一次「外部已落盘」。
        // 这条通知属于同一次提交，由 CommitPendingAgentStageAsync 自行完成会话落盘，不重复灌回修订历史。
        if (_isCommittingPendingAgentStage)
        {
            return;
        }

        // 若当前预览舞台正打开着这份待确认暂存，而用户在外部 Agent 对话中回复确认触发了 commit=true：
        // 此时无需再叠加一条重复修订，直接把当前修订标记为已提交并持久化共创会话。
        if (!string.IsNullOrWhiteSpace(_pendingAgentStageId))
        {
            _pendingAgentStageId = null;
            _pendingAgentStageManifest = null;

            if (_revisions.Count > 0 &&
                string.Equals(
                    CustomWidgetAgentStageStore.ComputeManifestHash(CurrentManifestJson ?? string.Empty),
                    CustomWidgetAgentStageStore.ComputeManifestHash(manifestJson),
                    StringComparison.Ordinal))
            {
                StopClipboardPolling();
                StopWaitingElapsedTimer();

                bool wasCreate = string.IsNullOrWhiteSpace(_targetWidgetId);
                _targetWidgetId = widgetId;
                if (!string.IsNullOrWhiteSpace(widgetName))
                {
                    _targetWidgetName = widgetName;
                }

                int lastIdx = _revisions.Count - 1;
                _revisions[lastIdx] = _revisions[lastIdx] with { IsCommitted = true };
                if (wasCreate)
                {
                    _sessionStore.PromoteCreateSessionToWidget(
                        widgetId,
                        _briefMarkdown,
                        _revisions,
                        _chatMessages);
                }
                else
                {
                    _sessionStore.SaveCommittedWidgetSession(
                        widgetId,
                        _briefMarkdown,
                        _revisions,
                        _chatMessages);
                }

                _uncommittedEditCount = 0;
                _committed = true;
                DisposeCodeToolWatcher();
                UpdateWindowTitle();
                if (_page == StudioPage.Preview)
                {
                    RefreshInPlaceGenerationChrome();
                }

                ShowPageMessage(
                    InfoBarSeverity.Success,
                    string.Empty,
                    _loc.Format("CustomWidget.Workshop.AgentMcp.SyncedBanner", _targetWidgetName ?? widgetId),
                    null);
                return;
            }
        }

        bool isWaitingMcp = _isInPlaceGenerating && _waitingKind == WaitingKind.AgentMcp;
        bool matchesCurrentTarget = !string.IsNullOrWhiteSpace(_targetWidgetId) &&
            string.Equals(_targetWidgetId, widgetId, StringComparison.OrdinalIgnoreCase);
        bool isAgentChannelCreateDraft = string.IsNullOrWhiteSpace(_targetWidgetId) &&
            string.Equals(_prefs.PreferredStudioChannel, CustomWidgetStudioPreferencesStore.ChannelAgentMcp, StringComparison.OrdinalIgnoreCase);

        if (!isWaitingMcp && !matchesCurrentTarget && !isAgentChannelCreateDraft)
        {
            return;
        }

        StopClipboardPolling();
        StopWaitingElapsedTimer();

        bool wasCreateMode = string.IsNullOrWhiteSpace(_targetWidgetId);
        _targetWidgetId = widgetId;
        if (!string.IsNullOrWhiteSpace(widgetName))
        {
            _targetWidgetName = widgetName;
        }

        string promptForHistory = _pendingPrompt;
        bool applied = HandleManifestCandidate(
            manifestJson,
            promptForHistory,
            _loc.T("CustomWidget.Workshop.History.AgentMcp"),
            source: "ai",
            fencedLogicJs: hasLogicJs ? logicJs : null,
            externalLocales: hasLocales ? locales : null,
            hasExplicitExternalFiles: hasLogicJs || hasLocales);

        if (!applied)
        {
            return;
        }

        if (_revisions.Count > 0)
        {
            int lastIdx = _revisions.Count - 1;
            _revisions[lastIdx] = _revisions[lastIdx] with { IsCommitted = true };
        }

        if (wasCreateMode)
        {
            _sessionStore.PromoteCreateSessionToWidget(
                widgetId,
                _briefMarkdown,
                _revisions,
                _chatMessages);
        }
        else
        {
            _sessionStore.SaveCommittedWidgetSession(
                widgetId,
                _briefMarkdown,
                _revisions,
                _chatMessages);
        }

        _uncommittedEditCount = 0;
        _committed = true;
        DisposeCodeToolWatcher();
        UpdateWindowTitle();

        ShowPageMessage(
            InfoBarSeverity.Success,
            string.Empty,
            _loc.Format("CustomWidget.Workshop.AgentMcp.SyncedBanner", _targetWidgetName ?? widgetId),
            null);
    }

    private async Task OpenInCodeToolAsync(string? idea)
    {
        string workspace = _sessionStore.GetCreateDraftDirectory();
        string seedJson = !string.IsNullOrWhiteSpace(CurrentManifestJson)
            ? CurrentManifestJson!
            : DraftSkeletonManifestJson;

        try
        {
            Directory.CreateDirectory(workspace);
            string manifestPath = Path.Combine(workspace, "manifest.json");
            Interlocked.Exchange(ref _lastDraftWriteTick, Environment.TickCount64);
            CustomWidgetStorageFileOps.ReplaceTextFileAtomically(manifestPath, seedJson);

            string logicPath = Path.Combine(workspace, CustomWidgetDataStore.LogicScriptFileName);
            if (!string.IsNullOrWhiteSpace(CurrentLogicJs))
            {
                CustomWidgetStorageFileOps.ReplaceTextFileAtomically(logicPath, CurrentLogicJs!);
            }
            else
            {
                CustomWidgetStorageFileOps.DeleteFileIfExists(logicPath);
            }

            string localesDir = Path.Combine(workspace, CustomWidgetDataStore.LocalesDirectoryName);
            if (CurrentLocales is { HasAnyLocale: true } draftLocales)
            {
                draftLocales.WriteToDirectoryAtomically(localesDir);
            }
            else if (Directory.Exists(localesDir))
            {
                CustomWidgetStorageFileOps.DeleteDirectoryRecursive(localesDir);
            }
            Interlocked.Exchange(ref _lastDraftWriteTick, Environment.TickCount64);

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
            ShowFailureMessage(
                "CustomWidget.Workshop.Failure.OpenToolTitle",
                "CustomWidget.Workshop.Failure.OpenToolBody",
                ex);
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
            var watcher = new FileSystemWatcher(workspace)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.CreationTime
            };
            static bool IsWatchedWorkspaceFile(string? name)
            {
                if (string.IsNullOrWhiteSpace(name))
                {
                    return false;
                }

                if (string.Equals(name, "manifest.json", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(name, CustomWidgetDataStore.LogicScriptFileName, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(name, CustomWidgetStudioSessionStore.BriefFileName, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                string normalized = name.Replace('\\', '/');
                return normalized.StartsWith("locales/", StringComparison.OrdinalIgnoreCase) &&
                       normalized.EndsWith(".json", StringComparison.OrdinalIgnoreCase) &&
                       !normalized.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase);
            }

            watcher.Changed += (_, e) =>
            {
                if (IsWatchedWorkspaceFile(e.Name)) ScheduleCodeToolReload(workspace);
            };
            watcher.Created += (_, e) =>
            {
                if (IsWatchedWorkspaceFile(e.Name)) ScheduleCodeToolReload(workspace);
            };
            watcher.Deleted += (_, e) =>
            {
                if (IsWatchedWorkspaceFile(e.Name)) ScheduleCodeToolReload(workspace);
            };
            watcher.Renamed += (_, e) =>
            {
                if (IsWatchedWorkspaceFile(e.Name) || IsWatchedWorkspaceFile(e.OldName))
                {
                    ScheduleCodeToolReload(workspace);
                }
            };
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
                string manifestPath = Path.Combine(workspace, "manifest.json");
                if (!File.Exists(manifestPath))
                {
                    return;
                }

                string text = await File.ReadAllTextAsync(manifestPath, Encoding.UTF8, token);
                string logicPath = Path.Combine(workspace, CustomWidgetDataStore.LogicScriptFileName);
                string? diskLogicJs = File.Exists(logicPath)
                    ? await File.ReadAllTextAsync(logicPath, Encoding.UTF8, token)
                    : null;
                if (string.IsNullOrWhiteSpace(diskLogicJs))
                {
                    diskLogicJs = null;
                }

                string localesDir = Path.Combine(workspace, CustomWidgetDataStore.LocalesDirectoryName);
                CustomWidgetLocaleCatalog loadedCatalog = CustomWidgetLocaleCatalog.LoadFromDirectory(localesDir);
                CustomWidgetLocaleCatalog? diskLocales = loadedCatalog.HasAnyLocale ? loadedCatalog : null;

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

                        bool bundleChanged =
                            !string.Equals(text, CurrentManifestJson, StringComparison.Ordinal) ||
                            !string.Equals(diskLogicJs ?? string.Empty, CurrentLogicJs ?? string.Empty, StringComparison.Ordinal) ||
                            !string.Equals(
                                diskLocales?.ComputeSha256() ?? string.Empty,
                                CurrentLocales?.ComputeSha256() ?? string.Empty,
                                StringComparison.Ordinal);
                        if (bundleChanged)
                        {
                            if (_page != StudioPage.Waiting)
                            {
                                _pageBeforeWaiting = _page;
                            }

                            HandleManifestCandidate(
                                text,
                                null,
                                _loc.T("CustomWidget.Workshop.History.CodeTool"),
                                source: "external",
                                fencedLogicJs: diskLogicJs,
                                externalLocales: diskLocales,
                                hasExplicitExternalFiles: true);
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

        // P2：外部 Agent 的待确认暂存必须经 Bridge 的 stage_commit 落盘，
        // 这样校验、stageId 一次性消费与审计都留在同一条写入路径上。
        string? stagedId = _pendingAgentStageId;
        string? abandonedStageId = null;
        if (!string.IsNullOrWhiteSpace(stagedId))
        {
            if (string.Equals(json, _pendingAgentStageManifest, StringComparison.Ordinal))
            {
                await CommitPendingAgentStageAsync(commitButton, stagedId);
                return;
            }

            // 用户在预览里又改了内容 = 已经转成一次本地创作。此时改走常规提交路径，
            // 丢弃旧暂存目录并在落盘后登记 committed 幂等记录，防止用户随后在 Agent 聊天里
            // 再次回复「确认提交」时报错或用未微调版本覆盖桌面。
            abandonedStageId = stagedId;
            try
            {
                await App.Current.DiscardAgentStageAsync(stagedId);
            }
            catch (Exception ex)
            {
                App.Log("[AgentStage] discard after local edit failed: " + ex.Message);
            }

            _pendingAgentStageId = null;
            _pendingAgentStageManifest = null;
        }

        commitButton.IsEnabled = false;
        try
        {
            bool wasEdit = IsEditMode;
            if (!wasEdit && _isExternalImportSession && !_hasPromptedFirstRunSetupInSession)
            {
                CustomWidgetManifest? preCommitManifest = CustomWidgetManifestParser.Parse(json).Manifest;
                if (preCommitManifest is not null)
                {
                    if (CurrentLocales is { HasAnyLocale: true })
                    {
                        preCommitManifest = preCommitManifest.WithLocales(CurrentLocales);
                    }

                    CustomWidgetSetupInspection setupCheck = CustomWidgetOnboardingHelper.InspectFirstRunSetupRequirements(
                        preCommitManifest,
                        EffectivePreviewCultureName,
                        _sessionSettingOverrides,
                        _sessionSecretOverrides);
                    if (setupCheck.RequiresAttention)
                    {
                        await PromptAndApplyFirstRunSetupInStudioAsync();
                    }
                }
            }

            if (wasEdit && !string.IsNullOrWhiteSpace(_targetWidgetId))
            {
                bool proceed = await VerifyOrMigrateStoreDataBeforeCommitAsync(_targetWidgetId!, json);
                if (!proceed)
                {
                    commitButton.IsEnabled = true;
                    return;
                }
            }

            string? logicJsToCommit = CurrentLogicJs;
            CustomWidgetLocaleCatalog? localesToCommit = CurrentLocales;
            string jsonToCommit = CustomWidgetTemplates.SyncManifestScriptPropertyJson(json, logicJsToCommit);
            jsonToCommit = StampManifestProvenanceForCommit(jsonToCommit);
            jsonToCommit = CustomWidgetManifestCompatibility.StampRequiredMinAppVersion(jsonToCommit);

            if (wasEdit && !string.IsNullOrWhiteSpace(_targetWidgetId))
            {
                CustomWidgetDataStore targetStore = CustomWidgetDataStore.ForWidget(_targetWidgetId!);
                string? prevDiskManifestJson = TryReadWidgetManifest(_targetWidgetId!);
                CustomWidgetManifest? prevManifestForMigration = !string.IsNullOrWhiteSpace(prevDiskManifestJson)
                    ? CustomWidgetManifestParser.Parse(prevDiskManifestJson!).Manifest
                    : null;
                CustomWidgetManifest? nextManifestForMigration = CustomWidgetManifestParser.Parse(jsonToCommit).Manifest;
                if (nextManifestForMigration is not null)
                {
                    await CustomWidgetHealthAnalyzer.MigratePromotedStateKeysToSettingsAsync(
                        targetStore,
                        prevManifestForMigration,
                        nextManifestForMigration);
                }

                await targetStore.WriteOrDeleteLogicJsAsync(logicJsToCommit);
                await targetStore.WriteOrDeleteLocalesAsync(localesToCommit);
            }

            (Dictionary<string, string>? initialSettings, Dictionary<string, string>? initialSecrets) =
                ExtractValidPreviewSettingOverrides(jsonToCommit);

            GlanceWidgetInstanceInfo info = wasEdit
                ? await manager.CreateCustomWidgetAsync(
                    $"update:{_targetWidgetId}:{jsonToCommit}",
                    initialSettings: initialSettings,
                    initialSecrets: initialSecrets)
                : await manager.CreateCustomWidgetAsync(
                    $"json:{jsonToCommit}",
                    logicJsOverride: logicJsToCommit,
                    hasLogicJsOverride: true,
                    localesOverride: localesToCommit,
                    hasLocalesOverride: true,
                    initialSettings: initialSettings,
                    initialSecrets: initialSecrets);

            if (!string.IsNullOrWhiteSpace(info.Id))
            {
                if (!string.IsNullOrWhiteSpace(abandonedStageId))
                {
                    CustomWidgetAgentStageStore.RecordCommitted(abandonedStageId, info.Id);
                }

                IReadOnlyList<StudioPersistedRevision> committedRevisions =
                    CustomWidgetStudioSessionStore.MarkCommittedRevision(
                        _revisions.Take(_revisionIndex + 1).ToList(),
                        jsonToCommit,
                        source: _aiRevisionCount > 0 ? "ai" : "manual",
                        committedLogicJs: logicJsToCommit,
                        committedLocales: localesToCommit);
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
            ShowFailureMessage(
                "CustomWidget.Workshop.Failure.SaveTitle",
                "CustomWidget.Workshop.Failure.SaveBody",
                ex);
        }
    }

    /// <summary>解析 Bridge 的 { ok, result, error } 响应，避免把字符串拼接当协议用。</summary>
    private static (bool Ok, bool Committed, string? WidgetId, string Error) ParseAgentStageResponse(string response)
    {
        try
        {
            using System.Text.Json.JsonDocument doc = System.Text.Json.JsonDocument.Parse(response);
            System.Text.Json.JsonElement root = doc.RootElement;
            bool ok = root.TryGetProperty("ok", out System.Text.Json.JsonElement okEl) &&
                      okEl.ValueKind == System.Text.Json.JsonValueKind.True;
            bool committed = false;
            string? widgetId = null;
            if (ok &&
                root.TryGetProperty("result", out System.Text.Json.JsonElement resultEl) &&
                resultEl.ValueKind == System.Text.Json.JsonValueKind.Object)
            {
                committed = resultEl.TryGetProperty("committed", out System.Text.Json.JsonElement committedEl) &&
                            committedEl.ValueKind == System.Text.Json.JsonValueKind.True;
                if (resultEl.TryGetProperty("widgetId", out System.Text.Json.JsonElement widEl) &&
                    widEl.ValueKind == System.Text.Json.JsonValueKind.String)
                {
                    widgetId = widEl.GetString();
                }
            }
            string error = ok
                ? string.Empty
                : (root.TryGetProperty("error", out System.Text.Json.JsonElement errorEl) &&
                   errorEl.ValueKind == System.Text.Json.JsonValueKind.String
                    ? errorEl.GetString() ?? "Unknown error."
                    : "Unknown error.");
            return (ok, committed, widgetId, error);
        }
        catch (Exception ex)
        {
            return (false, false, null, ex.Message);
        }
    }

    /// <summary>
    /// 用户确认一份外部暂存：Bridge 会重新校验、消费 stageId 再写入桌面。
    /// 成功后持久化共创修订历史并回到首屏让下一批待确认内容继续出现。
    /// </summary>
    private async Task CommitPendingAgentStageAsync(Button commitButton, string stageId)
    {
        commitButton.IsEnabled = false;
        try
        {
            bool wasEdit = IsEditMode;
            string? candidateJson = CurrentManifestJson;
            if (wasEdit && !string.IsNullOrWhiteSpace(_targetWidgetId) && !string.IsNullOrWhiteSpace(candidateJson))
            {
                bool proceed = await VerifyOrMigrateStoreDataBeforeCommitAsync(_targetWidgetId!, candidateJson);
                if (!proceed)
                {
                    commitButton.IsEnabled = true;
                    return;
                }
            }

            string response;
            _isCommittingPendingAgentStage = true;
            try
            {
                response = await App.Current.CommitAgentStageAsync(stageId);
            }
            finally
            {
                _isCommittingPendingAgentStage = false;
            }

            (bool ok, bool committed, string? committedWidgetId, string error) = ParseAgentStageResponse(response);
            if (!ok || !committed)
            {
                commitButton.IsEnabled = true;
                ShowPageMessage(
                    InfoBarSeverity.Error,
                    string.Empty,
                    string.IsNullOrWhiteSpace(error)
                        ? _loc.T("CustomWidget.Workshop.AgentStage.CommitFailed")
                        : error,
                    null);
                return;
            }

            string resolvedWidgetId = !string.IsNullOrWhiteSpace(committedWidgetId)
                ? committedWidgetId!
                : (_targetWidgetId ?? string.Empty);
            if (!string.IsNullOrWhiteSpace(resolvedWidgetId) && !string.IsNullOrWhiteSpace(candidateJson))
            {
                await PersistPreviewSettingOverridesToWidgetAsync(resolvedWidgetId, candidateJson);

                IReadOnlyList<StudioPersistedRevision> committedRevisions =
                    CustomWidgetStudioSessionStore.MarkCommittedRevision(
                        _revisions.Take(_revisionIndex + 1).ToList(),
                        candidateJson,
                        source: "ai",
                        committedLogicJs: CurrentLogicJs,
                        committedLocales: CurrentLocales);
                if (wasEdit)
                {
                    _sessionStore.SaveCommittedWidgetSession(resolvedWidgetId, _briefMarkdown, committedRevisions, _chatMessages);
                }
                else
                {
                    _sessionStore.PromoteCreateSessionToWidget(resolvedWidgetId, _briefMarkdown, committedRevisions, _chatMessages);
                }

                if (_prefs.LocateOnDesktopAfterCommit && App.Current.WidgetManager is { } manager)
                {
                    await manager.LocateCustomWidgetAsync(resolvedWidgetId);
                }
            }

            string preservedIdea = _startDraftIdea;
            ResetSession();
            _targetWidgetId = null;
            _startDraftIdea = preservedIdea;
            UpdateWindowTitle();
            ShowStartPage(preservedIdea);

            App.Current.NativeNotificationService?.TryShow(
                _loc.T(wasEdit ? "CustomWidget.Workshop.Saved.Title" : "CustomWidget.Workshop.Placed.Title"),
                _loc.T("CustomWidget.Workshop.Placed.Body"));
            ShowPageMessage(
                InfoBarSeverity.Success,
                string.Empty,
                _loc.T("CustomWidget.Workshop.Placed.Body"),
                null);
        }
        catch (Exception ex)
        {
            commitButton.IsEnabled = true;
            ShowFailureMessage(
                "CustomWidget.Workshop.Failure.SaveTitle",
                "CustomWidget.Workshop.Failure.SaveBody",
                ex);
        }
    }

    private async Task PlaceTemplateDirectlyAsync(
        string manifestJson,
        string? emoji,
        Button triggerButton,
        CustomWidgetLocaleCatalog? locales = null,
        string? logicJs = null)
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
            CustomWidgetManifest? effectiveManifest = parsed.Manifest;
            CustomWidgetLocaleCatalog? normalizedLocales = locales is { HasAnyLocale: true } ? locales : null;
            if (effectiveManifest is not null && normalizedLocales is not null)
            {
                effectiveManifest = effectiveManifest.WithLocales(normalizedLocales);
            }

            string jsonToCommit = manifestJson;
            if (CustomWidgetTemplates.IsEmojiIcon(emoji) &&
                CustomWidgetTemplates.MatchPresetOrNull(effectiveManifest?.Id) is null &&
                (effectiveManifest is null || !CustomWidgetTemplates.IsEmojiIcon(effectiveManifest.Icon)))
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

            GlanceWidgetInstanceInfo info = await manager.CreateCustomWidgetAsync(
                $"json:{jsonToCommit}",
                logicJsOverride: logicJs,
                hasLogicJsOverride: true,
                localesOverride: normalizedLocales,
                hasLocalesOverride: true);
            if (!string.IsNullOrWhiteSpace(info.Id))
            {
                string title = effectiveManifest?.ResolveLocalizedName(_loc.CurrentCultureName) ?? _loc.T("CustomWidget.Title");
                var initialRevisions = new List<StudioPersistedRevision>
                {
                    new()
                    {
                        RevisionId = "rev-1",
                        ManifestJson = jsonToCommit,
                        ManifestSha256 = CustomWidgetStudioSessionStore.ComputeManifestSha256(
                            jsonToCommit,
                            logicJs,
                            normalizedLocales),
                        LogicJs = logicJs,
                        Locales = normalizedLocales,
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
            ShowFailureMessage(
                "CustomWidget.Workshop.Failure.SaveTitle",
                "CustomWidget.Workshop.Failure.SaveBody",
                ex);
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
                RequestedTheme = StudioExplicitTheme,
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
        string? createdAtUtc = currentManifest?.CreatedAtUtc;

        if (_revisions.Count > 0)
        {
            CustomWidgetParseResult baseParsed = CustomWidgetManifestParser.Parse(_revisions[0].ManifestJson);
            if (baseParsed.Manifest is { } baseManifest)
            {
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
                    else if (baseManifest.Id.StartsWith("deskbox.", StringComparison.OrdinalIgnoreCase) ||
                             baseManifest.Id.StartsWith("starter.", StringComparison.OrdinalIgnoreCase))
                    {
                        forkedFromId = baseManifest.Id;
                        forkedFromVersion = baseManifest.Version;
                    }
                }
            }
        }

        bool isExistingUntrustedEdit = IsEditMode &&
            !string.IsNullOrWhiteSpace(_targetWidgetId) &&
            !CustomWidgetTrustStore.Shared.GetProfile(_targetWidgetId, currentManifest?.Origin).TrustedLocalAuthoring;
        if (_isExternalImportSession ||
            isExistingUntrustedEdit ||
            (currentManifest is not null &&
             string.Equals(currentManifest.Origin, CustomWidgetOriginKinds.Community, StringComparison.OrdinalIgnoreCase)))
        {
            return CustomWidgetManifestMetadataStamper.StampProvenance(
                json,
                origin: CustomWidgetOriginKinds.Community,
                isCustomized: (currentManifest?.IsCustomized ?? false) || _aiRevisionCount > 0 || _revisions.Count > 1,
                forkedFromId: forkedFromId,
                forkedFromVersion: forkedFromVersion,
                createdAtUtc: createdAtUtc);
        }

        if (_aiRevisionCount > 0)
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
            _revisions.Count <= 1 &&
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
        bool isCustomized = (currentManifest?.IsCustomized ?? false) || _revisions.Count > 1;
        return CustomWidgetManifestMetadataStamper.StampProvenance(
            json,
            origin: fallbackOrigin,
            isCustomized: isCustomized,
            forkedFromId: forkedFromId,
            forkedFromVersion: forkedFromVersion,
            createdAtUtc: createdAtUtc);
    }

    // ── Session / lifecycle ─────────────────────────────────────────────

    private void ResetSession()
    {
        CancelGeneration();
        CancelI18nQueue();
        StopOnboardingAnimationTimer();
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
        _isCapsulePreviewMode = false;
        _latestHealthReport = null;
        _revisionIndex = -1;
        _aiRevisionCount = 0;
        _uncommittedEditCount = 0;
        _committed = false;
        _selectedEmoji = null;
        _startDraftIdea = string.Empty;
        _refineDraftIdea = string.Empty;
        _pendingPrompt = string.Empty;
        // 引导覆盖层是 XAML 层，不属于会话数据；重置会话时必须一起收掉，
        // 否则它会带着已经作废的待生成需求留在屏幕上，点确认会凭空生成一次。
        _pendingChannelSelectionIdea = null;
        if (StudioOnboardingHost is not null && StudioOnboardingHost.Visibility == Visibility.Visible)
        {
            CloseOnboardingOverlay();
        }
        _pendingAgentStageId = null;
        _pendingAgentStageManifest = null;
        _isCommittingPendingAgentStage = false;
        _preferWebAiInSession = false;
        _lastBrokenJson = null;
        _lastBrokenLogicJs = null;
        _lastDiagnostics = [];
        _liveRuntimeScriptError = null;
        _liveRuntimeScriptErrorLine = null;
        _targetWidgetName = null;
        _isInPlaceGenerating = false;
        _isI18nQueueRunning = false;
        _isLocaleCenterOpen = false;
        _activePreviewCultureOverride = null;
        _activeI18nWorkingManifestJson = null;
        _activeI18nWorkingCatalog = null;
        _cachedI18nSyncPlan = null;
        _cachedI18nSyncPlanKey = string.Empty;
        _liveLocaleJobUpdates.Clear();
        _lastFailedLocaleErrors.Clear();
        _isWebAiRepairFlow = false;
        _openedWebAiTargetIdInSession = null;
        _isWebAiSameTabFollowUp = false;
        _lastStandaloneWebAiPrompt = null;
        _inFlightTargetNodeLabel = null;
        _inFlightDetectedWidgetName = string.Empty;
        _sessionSettingOverrides.Clear();
        _sessionSecretOverrides.Clear();
        _isExternalImportSession = false;
        _hasPromptedFirstRunSetupInSession = false;
    }

    private void HydrateExistingWidgetSettingsForPreview(string widgetId, CustomWidgetManifest manifest)
    {
        try
        {
            CustomWidgetDataStore store = CustomWidgetDataStore.ForWidget(widgetId);
            if (File.Exists(store.StorePath))
            {
                using System.Text.Json.JsonDocument doc = System.Text.Json.JsonDocument.Parse(
                    File.ReadAllText(store.StorePath, Encoding.UTF8));
                if (doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object &&
                    doc.RootElement.TryGetProperty("settings", out System.Text.Json.JsonElement settingsEl) &&
                    settingsEl.ValueKind == System.Text.Json.JsonValueKind.Object)
                {
                    foreach (System.Text.Json.JsonProperty prop in settingsEl.EnumerateObject())
                    {
                        if (prop.Value.ValueKind == System.Text.Json.JsonValueKind.String &&
                            prop.Value.GetString() is { } strVal)
                        {
                            _sessionSettingOverrides[prop.Name] = strVal;
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
                        _sessionSecretOverrides[def.Key] = existingSecret;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            App.Log($"[CustomWidgetStudio] Hydrate existing settings failed: {ex.Message}");
        }
    }

    private (Dictionary<string, string>? Settings, Dictionary<string, string>? Secrets) ExtractValidPreviewSettingOverrides(
        string committedManifestJson)
    {
        if (_sessionSettingOverrides.Count == 0 && _sessionSecretOverrides.Count == 0)
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
        foreach ((string key, string val) in _sessionSettingOverrides)
        {
            if (validRegularKeys.Contains(key))
            {
                settings ??= new Dictionary<string, string>(StringComparer.Ordinal);
                settings[key] = val;
            }
        }

        Dictionary<string, string>? secrets = null;
        foreach ((string secretKey, string secretVal) in _sessionSecretOverrides)
        {
            if (validSecretKeys.Contains(secretKey))
            {
                secrets ??= new Dictionary<string, string>(StringComparer.Ordinal);
                secrets[secretKey] = secretVal;
            }
        }

        return (settings, secrets);
    }

    private async Task PersistPreviewSettingOverridesToWidgetAsync(string widgetId, string committedManifestJson)
    {
        try
        {
            (Dictionary<string, string>? settings, Dictionary<string, string>? secrets) =
                ExtractValidPreviewSettingOverrides(committedManifestJson);
            if (settings is null && secrets is null)
            {
                return;
            }

            if (App.Current.WidgetManager is { } manager)
            {
                await manager.ApplyCustomWidgetMutationAsync(
                    new Contracts.CustomWidgetSettingsMutation(widgetId, Settings: settings, Secrets: secrets));
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
                await store.SaveAsync(latest, origin: this);
            }
        }
        catch (Exception ex)
        {
            App.Log($"[CustomWidgetStudio] Persist preview settings on commit failed: {ex.Message}");
        }
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

    private static string? TryReadWidgetLogicJs(string widgetId)
    {
        try
        {
            CustomWidgetDataStore store = CustomWidgetDataStore.ForWidget(widgetId);
            if (!File.Exists(store.LogicScriptPath))
            {
                return null;
            }

            string text = File.ReadAllText(store.LogicScriptPath, Encoding.UTF8);
            return string.IsNullOrWhiteSpace(text) ? null : text;
        }
        catch (Exception ex)
        {
            App.Log($"[CustomWidgetStudio] Read logic.js failed: {ex.Message}");
            return null;
        }
    }

    private static CustomWidgetLocaleCatalog? TryReadWidgetLocales(string widgetId)
    {
        try
        {
            CustomWidgetDataStore store = CustomWidgetDataStore.ForWidget(widgetId);
            CustomWidgetLocaleCatalog catalog = store.ReadLocaleCatalog();
            return catalog.HasAnyLocale ? catalog : null;
        }
        catch (Exception ex)
        {
            App.Log($"[CustomWidgetStudio] Read locales failed: {ex.Message}");
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
                RequestedTheme = StudioExplicitTheme,
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
            CancelI18nQueue();
            DiscardActiveDraftCleanly();
            return true;
        }

        if (decision == DiscardDecision.SaveDraftAndClose)
        {
            if (_isI18nQueueRunning &&
                !string.IsNullOrWhiteSpace(_activeI18nWorkingManifestJson) &&
                _activeI18nWorkingCatalog is not null)
            {
                string ckptJson = _activeI18nWorkingManifestJson!;
                CustomWidgetLocaleCatalog ckptCat = _activeI18nWorkingCatalog;
                CancelI18nQueue();
                PersistI18nCheckpointDraft(ckptJson, CurrentLogicJs, ckptCat);
            }
            else
            {
                PersistActiveDraftIfDirty();
            }

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
        try
        {
            await HandleAppWindowClosingAfterCancelAsync();
        }
        catch (Exception ex)
        {
            App.Log($"[CustomWidgetStudio] AppWindow_Closing failed: {ex.Message}");
            _allowClose = true;
            Close();
        }
    }

    private async Task HandleAppWindowClosingAfterCancelAsync()
    {
        DiscardDecision decision = await AskDiscardOrSaveDraftAsync();
        if (decision == DiscardDecision.DiscardCleanly)
        {
            CancelI18nQueue();
            DiscardActiveDraftCleanly();
            _allowClose = true;
            Close();
        }
        else if (decision == DiscardDecision.SaveDraftAndClose)
        {
            if (_isI18nQueueRunning &&
                !string.IsNullOrWhiteSpace(_activeI18nWorkingManifestJson) &&
                _activeI18nWorkingCatalog is not null)
            {
                string ckptJson = _activeI18nWorkingManifestJson!;
                CustomWidgetLocaleCatalog ckptCat = _activeI18nWorkingCatalog;
                CancelI18nQueue();
                PersistI18nCheckpointDraft(ckptJson, CurrentLogicJs, ckptCat);
            }
            else
            {
                PersistActiveDraftIfDirty();
            }

            _allowClose = true;
            Close();
        }
    }

    private void RootGrid_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (DeskBox.Helpers.ImeCompositionGuard.IsImeCompositionKeyEvent(e))
        {
            return;
        }

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

        if (_isI18nQueueRunning)
        {
            CancelI18nQueue();
            return;
        }

        if (_isInPlaceGenerating)
        {
            CancelWaiting();
            return;
        }

        if (_isLocaleCenterOpen)
        {
            _isLocaleCenterOpen = false;
            RefreshLocaleCenterChrome();
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

        if (_isInspectorModeEnabled)
        {
            _isInspectorModeEnabled = false;
            if (_previewContent is not null)
            {
                _previewContent.IsNodeSelectionEnabled = false;
            }
            RefreshStageToolbarChrome();
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
        int maxWidth = Math.Max(320, workArea.Width - margin);
        int width = Math.Clamp((int)Math.Round(DesiredWidth * scale), Math.Min(minWidth, maxWidth), maxWidth);
        int maxHeight = Math.Max(280, workArea.Height - margin);
        int height = Math.Clamp((int)Math.Round(DesiredHeight * scale), Math.Min(minHeight, maxHeight), maxHeight);
        if (_appWindow.Presenter is OverlappedPresenter presenter &&
            presenter.State != OverlappedPresenterState.Restored)
        {
            presenter.Restore();
        }
        _appWindow.MoveAndResize(new RectInt32(
            workArea.X + Math.Max(0, (workArea.Width - width) / 2),
            workArea.Y + Math.Max(0, (workArea.Height - height) / 2),
            width,
            height));
    }

    private void AppTitleBar_ActualThemeChanged(FrameworkElement sender, object args)
    {
        ApplyTitleBarColors();
        StudioOnboardingTeachingTip.RequestedTheme = StudioExplicitTheme;
        ApplyStageBackdropVisuals();

        if (_apiDocsDrawerHandle is not null)
        {
            _apiDocsDrawerHandle = null;
            ApiDrawerBorder.Child = null;
            if (_isApiDocsDrawerOpen)
            {
                OpenApiDocsDrawer(null);
            }
        }

        if (!_isClosed && !_isDialogOpen && !IsAnyStudioJobRunning)
        {
            bool wasOnboardingVisible = StudioOnboardingHost.Visibility == Visibility.Visible;
            OnboardingOverlayKind prevOverlayKind = _activeOnboardingOverlayKind;
            StudioAiChannelKind prevTourChannel = _activeStepTourChannel;
            int prevTourStep = _activeStepTourStep;

            if (_page == StudioPage.Start)
            {
                ShowStartPage(_startDraftIdea);
            }
            else if (_page == StudioPage.Preview)
            {
                ShowPreviewPage(justLoaded: false);
            }

            if (wasOnboardingVisible)
            {
                if (prevOverlayKind == OnboardingOverlayKind.ChannelModal)
                {
                    ShowAgentMcpOnboardingTour(forceShow: true);
                }
                else if (prevOverlayKind == OnboardingOverlayKind.StepTour)
                {
                    _ = RunChannelStepTourAsync(++_onboardingTourRunId, prevTourChannel, prevTourStep);
                }
            }
        }
    }

    private void ApplyTitleBarColors()
    {
        bool isDark = StudioExplicitTheme == ElementTheme.Dark;
        AppWindowTitleBar titleBar = _appWindow.TitleBar;
        titleBar.ButtonBackgroundColor = Colors.Transparent;
        titleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
        titleBar.ButtonForegroundColor = isDark ? Colors.White : Colors.Black;
        titleBar.ButtonHoverForegroundColor = isDark ? Colors.White : Colors.Black;
        titleBar.ButtonPressedForegroundColor = isDark ? Colors.White : Colors.Black;
        titleBar.ButtonHoverBackgroundColor = isDark
            ? Windows.UI.Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF)
            : Windows.UI.Color.FromArgb(0x10, 0x00, 0x00, 0x00);
        titleBar.ButtonPressedBackgroundColor = isDark
            ? Windows.UI.Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF)
            : Windows.UI.Color.FromArgb(0x18, 0x00, 0x00, 0x00);
        titleBar.ButtonInactiveForegroundColor = isDark
            ? Windows.UI.Color.FromArgb(0xA0, 0xFF, 0xFF, 0xFF)
            : Windows.UI.Color.FromArgb(0xA0, 0x10, 0x10, 0x10);
    }

    private void OnLocalizationLanguageChanged()
    {
        if (_isClosed)
        {
            return;
        }

        void ApplyOnUi()
        {
            if (_isClosed)
            {
                return;
            }

            RootGrid.FlowDirection = CustomWidgetLocaleCatalog.IsRightToLeftCulture(_loc.CurrentCultureName)
                ? FlowDirection.RightToLeft
                : FlowDirection.LeftToRight;
            _cachedI18nSyncPlan = null;
            _cachedI18nSyncPlanKey = string.Empty;
            if (_apiDocsDrawerHandle is not null)
            {
                ApiDrawerBorder.Child = null;
                _apiDocsDrawerHandle = null;
            }

            UpdateWindowTitle();
            if (!IsAnyStudioJobRunning && !_isDialogOpen)
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

        if (DispatcherQueue.HasThreadAccess)
        {
            ApplyOnUi();
        }
        else
        {
            DispatcherQueue.TryEnqueue(ApplyOnUi);
        }
    }

    private void CustomWidgetStudioWindow_Closed(object sender, WindowEventArgs args)
    {
        _isClosed = true;
        _loc.LanguageChanged -= OnLocalizationLanguageChanged;
        CustomWidgetRemoteCatalogService.Shared.CatalogUpdated -= OnRemoteCatalogUpdated;
        CustomWidgetAgentStageStore.Changed -= OnAgentStageStoreChanged;
        CancelGeneration();
        CancelI18nQueue();
        StopOnboardingAnimationTimer();
        StopWaitingElapsedTimer();
        StopClipboardPolling();
        DisposeCodeToolWatcher();
        DisposePreview();
        DisposeTemplatePreviews();
        RootGrid.DragOver -= OnStudioRootDragOver;
        RootGrid.Drop -= OnStudioRootDrop;
        _appWindow.Closing -= AppWindow_Closing;
        AppTitleBar.ActualThemeChanged -= AppTitleBar_ActualThemeChanged;
        App.Current.DisplayTopologyChanged -= OnDisplayTopologyChanged;
        RemoveMinimumSizeHook();
        WindowsCompatibilityService.ReleaseTitleBarExtensions(this, _appWindow);
        App.Current.WidgetManager?.ReleaseRaisedBandGuest(_hWnd, "custom-widget-studio-closed");
        Closed -= CustomWidgetStudioWindow_Closed;
    }

    private void InstallMinimumSizeHook()
    {
        _isSubclassInstalled = Win32Helper.SetWindowSubclass(
            _hWnd,
            _windowSubclassProc,
            StudioWindowSubclassId,
            UIntPtr.Zero);
    }

    private void RemoveMinimumSizeHook()
    {
        if (!_isSubclassInstalled)
        {
            return;
        }

        Win32Helper.RemoveWindowSubclass(
            _hWnd,
            _windowSubclassProc,
            StudioWindowSubclassId);
        _isSubclassInstalled = false;
    }

    private IntPtr WindowSubclassProc(
        IntPtr hWnd,
        uint message,
        UIntPtr wParam,
        IntPtr lParam,
        UIntPtr subclassId,
        UIntPtr refData)
    {
        if (message == WmGetMinMaxInfo)
        {
            var minMaxInfo = Marshal.PtrToStructure<MinMaxInfo>(lParam);
            double scale = Win32Helper.GetDpiScaleForWindow(_hWnd, RootGrid.XamlRoot);
            int targetMinW = ToPhysicalPixels(MinimumWidth, scale);
            int targetMinH = ToPhysicalPixels(MinimumHeight, scale);

            IntPtr monitor = Win32Helper.MonitorFromWindow(_hWnd, 2 /* MONITOR_DEFAULTTONEAREST */);
            var info = new Win32Helper.MONITORINFO { cbSize = Marshal.SizeOf<Win32Helper.MONITORINFO>() };
            if (Win32Helper.GetMonitorInfo(monitor, ref info))
            {
                int workW = Math.Max(320, info.rcWork.Right - info.rcWork.Left);
                int workH = Math.Max(280, info.rcWork.Bottom - info.rcWork.Top);
                targetMinW = Math.Min(targetMinW, workW);
                targetMinH = Math.Min(targetMinH, workH);
            }

            minMaxInfo.MinTrackSize.X = Math.Max(minMaxInfo.MinTrackSize.X, targetMinW);
            minMaxInfo.MinTrackSize.Y = Math.Max(minMaxInfo.MinTrackSize.Y, targetMinH);
            Marshal.StructureToPtr(minMaxInfo, lParam, false);
            return IntPtr.Zero;
        }

        if (message == WmNcDestroy)
        {
            RemoveMinimumSizeHook();
        }

        return Win32Helper.DefSubclassProc(hWnd, message, wParam, lParam);
    }

    private static int ToPhysicalPixels(int logicalPixels, double scale)
    {
        double normalizedScale = double.IsFinite(scale) && scale > 0 ? scale : 1.0;
        return Math.Max(
            1,
            (int)Math.Round(logicalPixels * normalizedScale, MidpointRounding.AwayFromZero));
    }

    private void OnDisplayTopologyChanged()
    {
        if (_isClosed || _hWnd == IntPtr.Zero)
        {
            return;
        }

        if (DispatcherQueue is { } queue && !queue.HasThreadAccess)
        {
            queue.TryEnqueue(OnDisplayTopologyChanged);
            return;
        }

        try
        {
            if (_appWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Maximized })
            {
                return;
            }

            if (!Win32Helper.GetWindowRect(_hWnd, out Win32Helper.RECT rect) ||
                (rect.Left <= -30000 && rect.Top <= -30000))
            {
                return;
            }

            IntPtr monitor = Win32Helper.MonitorFromWindow(_hWnd, 2 /* MONITOR_DEFAULTTONEAREST */);
            var info = new Win32Helper.MONITORINFO { cbSize = Marshal.SizeOf<Win32Helper.MONITORINFO>() };
            if (!Win32Helper.GetMonitorInfo(monitor, ref info))
            {
                return;
            }

            Win32Helper.RECT work = info.rcWork;
            int workW = work.Right - work.Left;
            int workH = work.Bottom - work.Top;
            if (workW <= 0 || workH <= 0)
            {
                return;
            }

            int x = rect.Left;
            int y = rect.Top;
            int width = Math.Min(rect.Right - rect.Left, workW);
            int height = Math.Min(rect.Bottom - rect.Top, workH);
            x = Math.Clamp(x, work.Left, Math.Max(work.Left, work.Right - width));
            y = Math.Clamp(y, work.Top, Math.Max(work.Top, work.Bottom - height));

            if (x != rect.Left ||
                y != rect.Top ||
                width != rect.Right - rect.Left ||
                height != rect.Bottom - rect.Top)
            {
                _appWindow.MoveAndResize(new RectInt32(x, y, width, height));
            }
        }
        catch (Exception ex)
        {
            App.Log($"[CustomWidgetStudio] Display topology clamp failed: {ex.Message}");
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MinMaxInfo
    {
        public NativePoint Reserved;
        public NativePoint MaxSize;
        public NativePoint MaxPosition;
        public NativePoint MinTrackSize;
        public NativePoint MaxTrackSize;
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
        Direct,
        AgentMcp
    }

    private enum StudioAiChannelKind
    {
        DirectApi,
        AgentMcp,
        WebAi
    }

    private enum StageBackdropMode
    {
        Wallpaper,
        Light,
        Dark
    }
}
