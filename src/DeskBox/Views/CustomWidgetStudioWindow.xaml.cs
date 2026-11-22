using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DeskBox.Controls.WidgetContents;
using DeskBox.FileSafety;
using DeskBox.Helpers;
using DeskBox.Models;
using DeskBox.Platform;
using DeskBox.Services;
using DeskBox.Services.Studio;
using DeskBox.ViewModels;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
using Windows.System;
using WinRT.Interop;
using DeskBox.Controls.Studio;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace DeskBox.Views;

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
    private readonly IStudioHostBridge _hostBridge;
    private readonly CustomWidgetStudioPreferencesStore _prefs = CustomWidgetStudioPreferencesStore.Shared;
    private readonly CustomWidgetStudioSessionStore _sessionStore = CustomWidgetStudioSessionStore.Shared;
    private readonly StudioSessionController _session;
    private bool _allowClose;
    private bool _isClosed;
    private CustomWidgetApiDocsDialogHelper.ApiReferenceViewHandle? _apiDocsDrawerHandle;
    private bool _isApiDocsDrawerOpen;
    private double _preferredApiDrawerWidth = 488;
    private StudioPage? _lastRenderedPage;
    private bool _isDialogOpen;
    private readonly bool _usesSolidWindowBackdrop;
    public IntPtr WindowHandle => _hWnd;
    private InfoBar? _pageInfoBar;
    private readonly StudioChannelOnboardingController _channelOnboarding;
    private readonly StudioLocaleCenterPanel _localeCenter;
    private readonly StudioComposerCardBuilder _composerBuilder;
    private readonly StudioStartGalleryView _startGallery;
    private readonly StudioPreviewFrameThemeRenderer _previewTheme;
    private readonly StudioStageInspectorBar _stageInspector;
    private readonly StudioStageDialogsAndFlyouts _stageDialogs;
    private readonly StudioStageViewportController _stageViewport;
    private readonly StudioCoCreationChatPanel _chatPanel;
    private readonly StudioGenerationCoordinator _generation;
    private readonly StudioCommitAndWorkspaceCoordinator _commitAndWorkspace;

    public bool HasUnsavedWork => _session.HasUnsavedWork;
    private bool IsEditMode => _session.IsEditMode;
    private string? CurrentManifestJson => _session.CurrentManifestJson;
    private string? CurrentLogicJs => _session.CurrentLogicJs;
    private CustomWidgetLocaleCatalog? CurrentLocales => _session.CurrentLocales;
    private bool IsAnyStudioJobRunning => _session.IsAnyStudioJobRunning;
    private string EffectivePreviewCultureName => _session.EffectivePreviewCultureName;

    public CustomWidgetStudioWindow(LocalizationService localization)
        : this(localization, null)
    {
    }

    internal CustomWidgetStudioWindow(LocalizationService localization, IStudioHostBridge? hostBridge)
    {
        _loc = localization;
        _hostBridge = hostBridge ?? NullStudioHostBridge.Instance;
        InitializeComponent();

        _usesSolidWindowBackdrop = WindowsCompatibilityService.ApplySafeBackdrop(this) == "Solid";

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

        bool hasSystemWallpaper = DesktopWallpaperReadOps.TryResolveCurrentWallpaperImagePath() is not null;
        StageBackdropMode initialBackdrop = hasSystemWallpaper
            ? StageBackdropMode.Wallpaper
            : (Win32Helper.IsSystemDarkMode() ? StageBackdropMode.Dark : StageBackdropMode.Light);
        _session = new StudioSessionController(
            _loc,
            _hostBridge,
            _prefs,
            _sessionStore,
            hasSystemWallpaper,
            initialBackdrop);

        _channelOnboarding = new StudioChannelOnboardingController(this);
        _localeCenter = new StudioLocaleCenterPanel(this);
        _composerBuilder = new StudioComposerCardBuilder(this);
        _startGallery = new StudioStartGalleryView(this);
        _previewTheme = new StudioPreviewFrameThemeRenderer(this);
        _stageInspector = new StudioStageInspectorBar(this);
        _stageDialogs = new StudioStageDialogsAndFlyouts(this);
        _stageViewport = new StudioStageViewportController(this);
        _chatPanel = new StudioCoCreationChatPanel(this);
        _generation = new StudioGenerationCoordinator(this);
        _commitAndWorkspace = new StudioCommitAndWorkspaceCoordinator(this);
        _session.StagePreviewCapturer = CaptureStagePreviewPngAsync;

        InstallMinimumSizeHook();
        _hostBridge.DisplayTopologyChanged += OnDisplayTopologyChanged;
        _hostBridge.AppearanceChanged += _previewTheme.OnHostAppearanceChanged;
        _appWindow.Closing += AppWindow_Closing;
        _appWindow.Changed += AppWindow_Changed;
        AppTitleBar.ActualThemeChanged += AppTitleBar_ActualThemeChanged;
        _loc.LanguageChanged += OnLocalizationLanguageChanged;
        CustomWidgetAgentStageStore.Changed += OnAgentStageStoreChanged;
        DeskBoxAgentBridgeServer.McpClientConnected += OnMcpClientConnected;
        DeskBoxAgentBridgeServer.McpActivityRecorded += OnMcpActivityRecorded;
        CustomWidgetAiCredentialStore.Shared.CredentialsChanged += OnAiCredentialsChanged;
        Activated += CustomWidgetStudioWindow_Activated;
        Closed += CustomWidgetStudioWindow_Closed;
        RootGrid.FlowDirection = CustomWidgetLocaleCatalog.IsRightToLeftCulture(_loc.CurrentCultureName)
            ? FlowDirection.RightToLeft
            : FlowDirection.LeftToRight;
        RootGrid.AllowDrop = true;
        RootGrid.DragOver += _commitAndWorkspace.OnStudioRootDragOver;
        RootGrid.Drop += _commitAndWorkspace.OnStudioRootDrop;
        ResizeAndCenter(windowId);
        ApplyTitleBarColors();
        _ = _previewTheme.EnsureWallpaperLoadedAsync();
    }

    internal static IReadOnlyList<WorkshopCardSpec> BuildWorkshopGallerySpecs(
        CustomWidgetRemoteCatalogService catalogService,
        LocalizationService loc) =>
        StudioStartGalleryView.BuildWorkshopGallerySpecs(catalogService, loc);

    internal void OnExternalAgentWidgetUpserted(
        string widgetId,
        string widgetName,
        string manifestJson,
        string? logicJs,
        bool hasLogicJs,
        CustomWidgetLocaleCatalog? locales,
        bool hasLocales) =>
        _commitAndWorkspace.OnExternalAgentWidgetUpserted(
            widgetId,
            widgetName,
            manifestJson,
            logicJs,
            hasLogicJs,
            locales,
            hasLocales);

    internal LocalizationService Localization => _loc;
    internal IStudioHostBridge HostBridge => _hostBridge;
    internal CustomWidgetStudioPreferencesStore Preferences => _prefs;
    internal CustomWidgetStudioSessionStore SessionStore => _sessionStore;
    internal StudioSessionController Session => _session;

    internal bool AllowClose { get => _allowClose; set => _allowClose = value; }
    internal bool IsClosed => _isClosed;
    internal bool IsDialogOpen { get => _isDialogOpen; set => _isDialogOpen = value; }
    internal bool IsApiDocsDrawerOpen { get => _isApiDocsDrawerOpen; set => _isApiDocsDrawerOpen = value; }
    internal InfoBar? PageInfoBar { get => _pageInfoBar; set => _pageInfoBar = value; }
    internal Grid ShellRootGrid => RootGrid;

    internal StudioChannelOnboardingController ChannelOnboarding => _channelOnboarding;
    internal StudioLocaleCenterPanel LocaleCenter => _localeCenter;
    internal StudioComposerCardBuilder ComposerBuilder => _composerBuilder;
    internal StudioStartGalleryView StartGallery => _startGallery;
    internal StudioPreviewFrameThemeRenderer PreviewTheme => _previewTheme;
    internal StudioStageInspectorBar StageInspector => _stageInspector;
    internal StudioStageDialogsAndFlyouts StageDialogs => _stageDialogs;
    internal StudioStageViewportController StageViewport => _stageViewport;
    internal StudioCoCreationChatPanel ChatPanel => _chatPanel;
    internal StudioGenerationCoordinator Generation => _generation;
    internal StudioCommitAndWorkspaceCoordinator CommitAndWorkspace => _commitAndWorkspace;

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
        _session.TargetWidgetId = string.IsNullOrWhiteSpace(widgetId) ? null : widgetId.Trim();

        if (_session.IsEditMode)
        {
            _startGallery.ActiveStartGalleryMode = StartGallerySourceMode.MyWidgets;
            string? existingJson = StudioManifestInspector.TryReadWidgetManifest(_session.TargetWidgetId!, _hostBridge.Log);
            string? existingLogicJs = StudioManifestInspector.TryReadWidgetLogicJs(_session.TargetWidgetId!, _hostBridge.Log);
            CustomWidgetLocaleCatalog? existingLocales = StudioManifestInspector.TryReadWidgetLocales(_session.TargetWidgetId!, _hostBridge.Log);
            _session.TargetWidgetName = _hostBridge.GetCustomWidgetInstances()
                .FirstOrDefault(i => string.Equals(i.Id, _session.TargetWidgetId, StringComparison.Ordinal))?.Name;
            if (_hostBridge.ResolveCustomWidgetConfig(_session.TargetWidgetId!) is { } existingWidgetCfg)
            {
                _session.StageChromeMode = WidgetChromeModeNames.GetOverrideMode(existingWidgetCfg);
            }
            if (string.IsNullOrWhiteSpace(existingJson))
            {
                _session.TargetWidgetId = null;
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
                        _session.SelectedEmoji = manifest.Icon.Trim();
                    }

                    if (string.IsNullOrWhiteSpace(_session.TargetWidgetName))
                    {
                        _session.TargetWidgetName = manifest.ResolveLocalizedName(_loc.CurrentCultureName);
                    }

                    HydrateExistingWidgetSettingsForPreview(_session.TargetWidgetId!, manifest);
                }

                StudioLoadedSession loaded = _sessionStore.LoadOrReconcileWidgetSession(
                    _session.TargetWidgetId!,
                    existingJson,
                    _loc.T("CustomWidget.Workshop.CoCreate.ExternalSyncSummary"),
                    currentDiskLogicJs: existingLogicJs,
                    currentDiskLocales: existingLocales);

                _session.BriefMarkdown = loaded.BriefMarkdown;
                _session.Revisions.AddRange(loaded.Revisions);
                if (_session.Revisions.Count == 0)
                {
                    _session.AddRevision(
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
                    _session.RevisionIndex = _session.Revisions.Count - 1;
                }

                _session.ChatMessages.AddRange(loaded.ChatMessages);
                _session.PendingRecoveryDraft = loaded.PendingDraft;
                _session.WasReconciledExternalOnOpen = loaded.WasReconciledWithExternalEdit;
            }
        }
        else if (string.IsNullOrWhiteSpace(initialIdea))
        {
            _session.PendingRecoveryDraft = _sessionStore.TryLoadCreateDraft();
        }

        UpdateWindowTitle();
        CustomWidgetRemoteCatalogService.Shared.CatalogUpdated -= OnRemoteCatalogUpdated;
        CustomWidgetRemoteCatalogService.Shared.CatalogUpdated += OnRemoteCatalogUpdated;
        _ = CustomWidgetRemoteCatalogService.Shared.RefreshInBackgroundAsync();
        _ = CustomWidgetRemotePromptService.Shared.RefreshInBackgroundAsync();

        if (_session.IsEditMode)
        {
            _stageViewport.ShowPreviewPage(justLoaded: false);
            if (!string.IsNullOrWhiteSpace(initialIdea))
            {
                if (autoStartGeneration)
                {
                    _ = _generation.BeginGenerationAsync(initialIdea);
                }
                else if (_chatPanel.RefineTextBox is not null)
                {
                    _chatPanel.RefineTextBox.Text = initialIdea;
                }
            }

            return;
        }

        _startGallery.ShowStartPage(initialIdea);
        if (autoStartGeneration && !string.IsNullOrWhiteSpace(initialIdea))
        {
            _ = _generation.BeginGenerationAsync(initialIdea);
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
            if (!_isClosed && _session.Page == StudioPage.Start)
            {
                if (_startGallery.RefreshTemplateGalleryFromRemote is not null)
                {
                    _startGallery.RefreshTemplateGalleryFromRemote();
                }
                else if (!_session.HasUnsavedWork)
                {
                    _startGallery.ShowStartPage(_session.StartDraftIdea);
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

            if (_session.Page == StudioPage.Start)
            {
                _startGallery.RefreshAgentStageBanner?.Invoke();
                return;
            }

            if (_session.Page == StudioPage.Preview && !_session.IsCommittingPendingAgentStage)
            {
                TryAutoLoadLatestAgentStageIntoPreview();
            }
        });
    }

    private void TryAutoLoadLatestAgentStageIntoPreview()
    {
        IReadOnlyList<AgentStageSummary> pending = _hostBridge.GetPendingAgentStages();
        if (pending.Count == 0)
        {
            _stageViewport.HideIncomingAgentStageBanner();
            return;
        }

        AgentStageSummary latest = pending[0];
        if (string.Equals(latest.StageId, _session.PendingAgentStageId, StringComparison.Ordinal))
        {
            _stageViewport.HideIncomingAgentStageBanner();
            return;
        }

        if (!_session.HasUnsavedWork)
        {
            _stageViewport.HideIncomingAgentStageBanner();
            LoadAgentStageIntoPreview(latest, forceLoadOverUnsaved: false);
            return;
        }

        _stageViewport.ShowIncomingAgentStageBanner(latest, pending.Count);
    }

    internal void LoadAgentStageIntoPreview(AgentStageSummary stage, bool forceLoadOverUnsaved = false)
    {
        if (string.IsNullOrWhiteSpace(stage.StageId))
        {
            return;
        }

        if (_session.HasUnsavedWork && !forceLoadOverUnsaved)
        {
            _stageViewport.ShowIncomingAgentStageBanner(stage, Math.Max(1, _hostBridge.GetPendingAgentStages().Count));
            return;
        }

        if (!_hostBridge.TryReadAgentStage(stage.StageId, out AgentStageBundle? bundle, out string error) || bundle is null)
        {
            _stageViewport.HideIncomingAgentStageBanner();
            if (!string.IsNullOrWhiteSpace(error))
            {
                ShowPageMessage(InfoBarSeverity.Error, string.Empty, error, null);
            }
            return;
        }

        bool isWaitingAgentMcp = _session.IsInPlaceGenerating && _session.WaitingKind == WaitingKind.AgentMcp;
        if (isWaitingAgentMcp &&
            !forceLoadOverUnsaved &&
            !string.IsNullOrWhiteSpace(_session.TargetWidgetId) &&
            !string.IsNullOrWhiteSpace(bundle.WidgetId) &&
            !string.Equals(_session.TargetWidgetId, bundle.WidgetId, StringComparison.OrdinalIgnoreCase))
        {
            _stageViewport.ShowIncomingAgentStageBanner(stage, Math.Max(1, _hostBridge.GetPendingAgentStages().Count));
            return;
        }

        _stageViewport.HideIncomingAgentStageBanner();
        ApplyAgentStageToCurrentPreviewSession(bundle);
    }

    private void ApplyAgentStageToCurrentPreviewSession(AgentStageBundle bundle)
    {
        _generation.StopWaitingElapsedTimer();

        if (!string.IsNullOrWhiteSpace(bundle.WidgetId) &&
            _hostBridge.ResolveCustomWidgetConfig(bundle.WidgetId) is { } existingCfg)
        {
            if (!string.Equals(_session.TargetWidgetId, existingCfg.Id, StringComparison.OrdinalIgnoreCase))
            {
                _session.TargetWidgetId = existingCfg.Id;
                _session.TargetWidgetName = existingCfg.Name;
                _session.StageChromeMode = WidgetChromeModeNames.GetOverrideMode(existingCfg);
                CustomWidgetParseResult? parsedTarget = CustomWidgetManifestParser.Parse(bundle.ManifestJson);
                if (parsedTarget.Manifest is { } targetManifest)
                {
                    HydrateExistingWidgetSettingsForPreview(existingCfg.Id, targetManifest);
                }
            }
        }
        else if (string.IsNullOrWhiteSpace(bundle.WidgetId) && !string.IsNullOrWhiteSpace(_session.TargetWidgetId))
        {
            _session.TargetWidgetId = null;
            _session.TargetWidgetName = string.Empty;
            _session.StageChromeMode = WidgetChromeMode.System;
        }

        CustomWidgetLocaleCatalog? locales = bundle.HasLocales
            ? CustomWidgetLocaleCatalog.ParseCatalogJson(bundle.LocalesJson)
            : null;
        string? prevStageId = _session.PendingAgentStageId;
        _session.PendingAgentStageId = bundle.StageId;
        string promptForHistory = _session.PendingPrompt;
        bool applied = _generation.HandleManifestCandidate(
            bundle.ManifestJson,
            promptForHistory,
            _loc.T("CustomWidget.Workshop.AgentStage.HistoryLabel"),
            source: "agent-stage",
            fencedLogicJs: bundle.HasLogicJs ? bundle.LogicJs : null,
            externalLocales: bundle.HasLocales ? locales : null,
            hasExplicitExternalFiles: bundle.HasLogicJs && bundle.HasLocales,
            hasExplicitLogicJsOverride: bundle.HasLogicJs,
            hasExplicitLocalesOverride: bundle.HasLocales);
        if (!applied)
        {
            _session.PendingAgentStageId = prevStageId;
            return;
        }
        _session.PendingAgentStageManifest = _session.CurrentManifestJson;
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
            _hostBridge.Log($"[CustomWidgetStudio] OpenAgentStagePreview failed: {ex.Message}");
        }
    }

    private async Task OpenAgentStagePreviewCoreAsync(string stageId)
    {
        if (string.IsNullOrWhiteSpace(stageId))
        {
            return;
        }

        if (_session.Page != StudioPage.Start && _session.HasUnsavedWork && !await ConfirmDiscardCleanlyAsync())
        {
            return;
        }

        if (!_hostBridge.TryReadAgentStage(stageId, out AgentStageBundle? bundle, out string error) || bundle is null)
        {
            ShowPageMessage(InfoBarSeverity.Error, string.Empty, error, null);
            return;
        }

        string preservedIdea = _session.StartDraftIdea;
        ResetSession();
        _session.StartDraftIdea = preservedIdea;

        WidgetConfig? existingTarget = !string.IsNullOrWhiteSpace(bundle.WidgetId)
            ? _hostBridge.ResolveCustomWidgetConfig(bundle.WidgetId)
            : null;
        if (existingTarget is not null)
        {
            _session.TargetWidgetId = existingTarget.Id;
            _session.TargetWidgetName = existingTarget.Name;
            _session.StageChromeMode = WidgetChromeModeNames.GetOverrideMode(existingTarget);
            string? existingJson = StudioManifestInspector.TryReadWidgetManifest(_session.TargetWidgetId, _hostBridge.Log);
            string? existingLogicJs = StudioManifestInspector.TryReadWidgetLogicJs(_session.TargetWidgetId, _hostBridge.Log);
            CustomWidgetLocaleCatalog? existingLocales = StudioManifestInspector.TryReadWidgetLocales(_session.TargetWidgetId, _hostBridge.Log);
            if (!string.IsNullOrWhiteSpace(existingJson))
            {
                CustomWidgetParseResult existingParsed = CustomWidgetManifestParser.Parse(existingJson);
                if (existingParsed.Manifest is { } existingManifest)
                {
                    if (existingLocales is { HasAnyLocale: true })
                    {
                        existingManifest = existingManifest.WithLocales(existingLocales);
                    }
                    HydrateExistingWidgetSettingsForPreview(_session.TargetWidgetId, existingManifest);
                }

                StudioLoadedSession loaded = _sessionStore.LoadOrReconcileWidgetSession(
                    _session.TargetWidgetId,
                    existingJson,
                    _loc.T("CustomWidget.Workshop.CoCreate.ExternalSyncSummary"),
                    currentDiskLogicJs: existingLogicJs,
                    currentDiskLocales: existingLocales);
                _session.BriefMarkdown = loaded.BriefMarkdown;
                _session.Revisions.AddRange(loaded.Revisions);
                _session.RevisionIndex = _session.Revisions.Count - 1;
                _session.ChatMessages.AddRange(loaded.ChatMessages);
            }
        }
        else
        {
            _session.TargetWidgetId = null;
        }

        UpdateWindowTitle();

        CustomWidgetParseResult parsed = CustomWidgetManifestParser.Parse(bundle.ManifestJson);
        if (parsed.Manifest is not null && CustomWidgetTemplates.IsEmojiIcon(parsed.Manifest.Icon))
        {
            _session.SelectedEmoji = parsed.Manifest.Icon.Trim();
        }

        string? effectiveLogicJs = bundle.HasLogicJs
            ? bundle.LogicJs
            : (_session.IsEditMode ? _session.CurrentLogicJs : null);
        CustomWidgetLocaleCatalog? locales = bundle.HasLocales
            ? CustomWidgetLocaleCatalog.ParseCatalogJson(bundle.LocalesJson)
            : (_session.IsEditMode ? _session.CurrentLocales : null);
        _session.AddRevision(
            bundle.ManifestJson,
            _loc.T("CustomWidget.Workshop.AgentStage.HistoryLabel"),
            isAiChange: false,
            source: "agent-stage",
            isCommitted: false,
            markDirty: false,
            logicJs: effectiveLogicJs,
            hasExplicitLogicJs: bundle.HasLogicJs || _session.IsEditMode,
            locales: locales,
            hasExplicitLocales: bundle.HasLocales || _session.IsEditMode);

        // ResetSession 会清空暂存态，必须在 AddRevision 后绑定当前预览对应的暂存快照。
        _session.PendingAgentStageId = bundle.StageId;
        _session.PendingAgentStageManifest = _session.CurrentManifestJson;

        _stageViewport.ShowPreviewPage(justLoaded: true);
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

        if (_session.Page != StudioPage.Start && _session.HasUnsavedWork)
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

    internal void OpenExternalManifestPreviewCore(
        string manifestJson,
        string? sourceLabel = null,
        string? logicJs = null,
        CustomWidgetLocaleCatalog? locales = null,
        string? originKind = null)
    {
        ResetSession();
        _session.TargetWidgetId = null;
        UpdateWindowTitle();

        manifestJson = CustomWidgetAiGeneratorService.ExtractInlineManifestLocalesAndStrip(
            manifestJson,
            locales,
            out CustomWidgetLocaleCatalog? effectiveLocales);
        string? effectiveLogicJs = logicJs;
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
            _session.SelectedEmoji = effectiveManifest.Icon.Trim();
        }

        string label = !string.IsNullOrWhiteSpace(sourceLabel)
            ? sourceLabel!
            : effectiveManifest?.ResolveLocalizedName(_loc.CurrentCultureName) ?? _loc.T("CustomWidget.Title");
        _session.AddRevision(
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
        _session.IsExternalImportSession = true;
        _stageViewport.ShowPreviewPage(justLoaded: true);
        RefreshExternalImportSetupBanner(effectiveManifest);
    }

    public void InitializeForkFromWidget(string sourceWidgetId, string? initialIdea = null, bool autoStartGeneration = true)
    {
        if (string.IsNullOrWhiteSpace(sourceWidgetId))
        {
            return;
        }

        if (_session.Page != StudioPage.Start && _session.HasUnsavedWork)
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
        string? existingJson = StudioManifestInspector.TryReadWidgetManifest(trimmedSourceId, _hostBridge.Log);
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

        string? existingLogicJs = StudioManifestInspector.TryReadWidgetLogicJs(trimmedSourceId, _hostBridge.Log);
        CustomWidgetLocaleCatalog? existingLocales = StudioManifestInspector.TryReadWidgetLocales(trimmedSourceId, _hostBridge.Log);
        CustomWidgetForkBundleResult forkBundle = CustomWidgetOnboardingHelper.CreateForkBundle(
            existingJson!,
            existingLogicJs,
            existingLocales,
            _loc.CurrentCultureName,
            _loc.T("CustomWidget.Fork.TitleSuffix"),
            preserveUntrustedOrigin: isUntrustedSource);

        ResetSession();
        _session.TargetWidgetId = null;
        _session.IsExternalImportSession = isUntrustedSource;
        UpdateWindowTitle();

        CustomWidgetParseResult parsed = CustomWidgetManifestParser.Parse(forkBundle.ManifestJson);
        if (parsed.Manifest is not null && CustomWidgetTemplates.IsEmojiIcon(parsed.Manifest.Icon))
        {
            _session.SelectedEmoji = parsed.Manifest.Icon.Trim();
        }

        _session.AddRevision(
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

        _stageViewport.ShowPreviewPage(justLoaded: true);
        if (!string.IsNullOrWhiteSpace(initialIdea))
        {
            if (autoStartGeneration)
            {
                _ = _generation.BeginGenerationAsync(initialIdea);
            }
            else if (_chatPanel.RefineTextBox is not null)
            {
                _chatPanel.RefineTextBox.Text = initialIdea;
            }
        }
    }

    private void RefreshExternalImportSetupBanner(CustomWidgetManifest? manifest)
    {
        if (manifest is null || _session.Page != StudioPage.Preview)
        {
            return;
        }

        CustomWidgetSetupInspection inspection = CustomWidgetOnboardingHelper.InspectFirstRunSetupRequirements(
            manifest,
            _session.EffectivePreviewCultureName,
            _session.SessionSettingOverrides,
            _session.SessionSecretOverrides);
        if (!inspection.RequiresAttention)
        {
            return;
        }

        var configureBtn = new Button
        {
            CornerRadius = new CornerRadius(6),
            Content = _loc.T("CustomWidget.Onboarding.ConfigureNowButton")
        };
        configureBtn.Click += async (_, _) =>
            await StudioUiPrimitives.SafeInvokeAsync(this, "FirstRunSetup", PromptAndApplyFirstRunSetupInStudioAsync);

        ShowPageMessage(
            InfoBarSeverity.Informational,
            string.Empty,
            _loc.T("CustomWidget.Onboarding.BannerNeedsSetup"),
            configureBtn);
    }

    internal async Task<CustomWidgetFirstRunSetupOutcome?> PromptFirstRunSetupForManifestAsync(
        CustomWidgetManifest manifest,
        string? cultureName = null)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        string effectiveCulture = !string.IsNullOrWhiteSpace(cultureName)
            ? cultureName
            : _session.EffectivePreviewCultureName;
        CustomWidgetSetupInspection inspection = CustomWidgetOnboardingHelper.InspectFirstRunSetupRequirements(
            manifest,
            effectiveCulture);
        if (!inspection.RequiresAttention)
        {
            return null;
        }

        return await ShowFirstRunSetupDialogCoreAsync(manifest, inspection, effectiveCulture);
    }

    private async Task<CustomWidgetFirstRunSetupOutcome?> ShowFirstRunSetupDialogCoreAsync(
        CustomWidgetManifest manifest,
        CustomWidgetSetupInspection inspection,
        string effectiveCulture)
    {
        if (_isDialogOpen || _isClosed || RootGrid.XamlRoot is null || !inspection.HasAnyConfigurableFields)
        {
            return null;
        }

        _isDialogOpen = true;
        try
        {
            return await CustomWidgetFirstRunSetupDialogHelper.ShowAsync(
                RootGrid.XamlRoot,
                RootGrid.ActualTheme,
                _loc,
                _hWnd,
                manifest,
                inspection,
                effectiveCulture);
        }
        finally
        {
            _isDialogOpen = false;
        }
    }

    internal async Task PromptAndApplyFirstRunSetupInStudioAsync()
    {
        if (_isDialogOpen || _isClosed || RootGrid.XamlRoot is null || string.IsNullOrWhiteSpace(_session.CurrentManifestJson))
        {
            return;
        }

        CustomWidgetManifest? manifest = CustomWidgetManifestParser.Parse(_session.CurrentManifestJson!).Manifest;
        if (manifest is null)
        {
            return;
        }

        if (_session.CurrentLocales is { HasAnyLocale: true })
        {
            manifest = manifest.WithLocales(_session.CurrentLocales);
        }

        CustomWidgetSetupInspection inspection = CustomWidgetOnboardingHelper.InspectFirstRunSetupRequirements(
            manifest,
            _session.EffectivePreviewCultureName,
            _session.SessionSettingOverrides,
            _session.SessionSecretOverrides);
        if (!inspection.HasAnyConfigurableFields)
        {
            return;
        }

        _session.HasPromptedFirstRunSetupInSession = true;
        CustomWidgetFirstRunSetupOutcome? outcome = await ShowFirstRunSetupDialogCoreAsync(
            manifest,
            inspection,
            _session.EffectivePreviewCultureName);

        if (outcome is not { Applied: true })
        {
            return;
        }

        foreach ((string key, string val) in outcome.Settings)
        {
            _session.SessionSettingOverrides[key] = val;
            if (_stageViewport.PreviewVm is not null)
            {
                await _stageViewport.PreviewVm.SetSettingValueAsync(key, val);
            }
        }

        foreach ((string secKey, string secVal) in outcome.Secrets)
        {
            _session.SessionSecretOverrides[secKey] = secVal;
            if (_stageViewport.PreviewVm is not null)
            {
                await _stageViewport.PreviewVm.SetSecretValueAsync(secKey, secVal);
            }
        }

        if (_pageInfoBar is not null)
        {
            _pageInfoBar.IsOpen = false;
            _pageInfoBar.Visibility = Visibility.Collapsed;
        }

        RefreshExternalImportSetupBanner(manifest);
    }

    private void OnMcpClientConnected(ConnectedMcpClientInfo _)
    {
        if (_isClosed)
        {
            return;
        }

        DispatcherQueue.TryEnqueue(() =>
        {
            if (!_isClosed)
            {
                _channelOnboarding.NotifyChannelSelectorsRefresh();
                if (_session.IsInPlaceGenerating && _session.WaitingKind == WaitingKind.AgentMcp)
                {
                    _chatPanel.RefreshCoCreationStream();
                }
            }
        });
    }

    private void OnMcpActivityRecorded(string op, string detail)
    {
        if (_isClosed)
        {
            return;
        }

        DispatcherQueue.TryEnqueue(() =>
        {
            if (!_isClosed)
            {
                _chatPanel.RecordMcpActivity(op, detail);
            }
        });
    }

    private void OnAiCredentialsChanged(object? sender, EventArgs e)
    {
        if (_isClosed)
        {
            return;
        }

        DispatcherQueue.TryEnqueue(() =>
        {
            if (!_isClosed)
            {
                _channelOnboarding.NotifyChannelSelectorsRefresh();
            }
        });
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
            _session.TargetWidgetId,
            StringComparison.Ordinal);
        if (_session.HasUnsavedWork || (sameTarget && string.IsNullOrWhiteSpace(initialIdea) && _session.Page != StudioPage.Start))
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
        _hostBridge.BringAuxiliaryWindowToFront(_hWnd, "custom-widget-studio-shown");
        Activate();
        _ = Win32Helper.SetForegroundWindow(_hWnd);
    }

    internal void WriteStudioContextTo(System.Text.Json.Utf8JsonWriter writer, bool includeManifest = true) =>
        _session.WriteStudioContextTo(writer, includeManifest, _isClosed);

    public void CloseForShutdown()
    {
        _session.PersistActiveDraftIfDirty();
        _allowClose = true;
        Close();
    }

    internal void PersistDraftOnExit() => _session.PersistActiveDraftIfDirty();

    internal void RestoreFromDraftSnapshot(StudioDraftSnapshot draft)
    {
        bool hasRevisions = _session.RestoreDraftState(draft);
        _apiDocsDrawerHandle?.RefreshPinnedState();
        _composerBuilder.RefreshActiveComposerCapabilityChrome();
        if (hasRevisions)
        {
            _stageViewport.ShowPreviewPage(justLoaded: true);
        }
        else
        {
            _startGallery.ShowStartPage(_session.StartDraftIdea);
        }
    }

    internal void ApplyZeroTokenMetadataEdit(
        string? newName = null,
        string? newIcon = null,
        CustomWidgetSizeSpec? newSize = null)
    {
        string? current = _session.CurrentManifestJson;
        if (string.IsNullOrWhiteSpace(current))
        {
            return;
        }

        string? normalizedName = !string.IsNullOrWhiteSpace(newName) ? newName!.Trim() : null;
        string? normalizedIcon = !string.IsNullOrWhiteSpace(newIcon) ? newIcon!.Trim() : null;
        if (!string.IsNullOrWhiteSpace(normalizedIcon) && CustomWidgetTemplates.IsEmojiIcon(normalizedIcon))
        {
            _session.SelectedEmoji = normalizedIcon;
        }

        CustomWidgetLocaleCatalog? prevLocales = _session.CurrentLocales;
        bool hasLocaleCatalog = prevLocales is { HasAnyLocale: true };
        string? rawManifestName = CustomWidgetManifestParser.Parse(current).Manifest?.Name;

        string updated = CustomWidgetManifestCompatibility.WithUpdatedMetadataJson(
            current,
            newName: normalizedName,
            newIcon: normalizedIcon,
            newDefaultSize: newSize,
            activeCulture: _session.EffectivePreviewCultureName,
            preserveI18nNameBinding: hasLocaleCatalog);

        CustomWidgetLocaleCatalog? nextLocales = hasLocaleCatalog && normalizedName is not null
            ? CustomWidgetManifestCompatibility.WithUpdatedLocaleName(
                prevLocales,
                rawManifestName,
                normalizedName,
                _session.EffectivePreviewCultureName)
            : prevLocales;

        bool localesChanged = !string.Equals(
            prevLocales?.ComputeSha256() ?? string.Empty,
            nextLocales?.ComputeSha256() ?? string.Empty,
            StringComparison.Ordinal);
        if (string.Equals(updated, current, StringComparison.Ordinal) && !localesChanged)
        {
            return;
        }

        string manualLabel = newSize is { Width: > 0, Height: > 0 } && string.IsNullOrWhiteSpace(normalizedName)
            ? _loc.Format("CustomWidget.Workshop.CoCreate.ManualResizeFormat", newSize.Width, newSize.Height)
            : !string.IsNullOrWhiteSpace(normalizedName) && newSize is null
                ? _loc.Format("CustomWidget.Workshop.CoCreate.ManualRenameFormat", normalizedName)
                : _loc.T("CustomWidget.Workshop.CoCreate.ManualQuickEdit");

        _session.AddRevision(
            updated,
            manualLabel,
            isAiChange: false,
            source: "manual",
            isCommitted: false,
            markDirty: true,
            locales: nextLocales,
            hasExplicitLocales: true,
            coalesceGroupKey: newSize is not null ? "meta:size" : "meta:identity");
        _session.PersistActiveDraftIfDirty();
        if (_session.Page == StudioPage.Preview && _stageViewport.CanMountOrUpdatePreviewInPlace)
        {
            _stageViewport.ApplyLivePreviewUpdateInPlace();
        }
        else
        {
            _stageViewport.ShowPreviewPage(justLoaded: false);
        }
    }

    internal void ApplyZeroTokenNodeEditInPlace(
        string nodePath,
        string? jsonProperty,
        JsonNode? nextPropertyValue,
        string? i18nKeyToUpdate = null,
        string? i18nValue = null,
        string? coalesceGroupKey = null,
        string? changeSummary = null)
    {
        string? current = _session.CurrentManifestJson;
        if (string.IsNullOrWhiteSpace(current) || string.IsNullOrWhiteSpace(nodePath))
        {
            return;
        }

        CustomWidgetLocaleCatalog? prevLocales = _session.CurrentLocales;
        CustomWidgetLocaleCatalog? nextLocales = prevLocales;
        string updatedJson = current;

        if (!string.IsNullOrWhiteSpace(i18nKeyToUpdate) && i18nValue is not null && prevLocales is { HasAnyLocale: true })
        {
            nextLocales = CustomWidgetManifestCompatibility.WithUpdatedLocaleKey(
                prevLocales,
                i18nKeyToUpdate!,
                i18nValue,
                _session.EffectivePreviewCultureName);
        }

        if (!string.IsNullOrWhiteSpace(jsonProperty))
        {
            try
            {
                if (StudioWorkspaceEngine.ParsePermissiveJsonNode(current) is not JsonObject rootObj ||
                    !StudioManifestInspector.TryResolveJsonNodeByPath(rootObj, nodePath, out JsonObject? targetObj) ||
                    targetObj is null)
                {
                    return;
                }

                if (nextPropertyValue is null)
                {
                    targetObj.Remove(jsonProperty);
                }
                else
                {
                    targetObj[jsonProperty] = nextPropertyValue;
                }

                using var ms = new MemoryStream();
                using (var writer = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true }))
                {
                    rootObj.WriteTo(writer);
                }
                updatedJson = Encoding.UTF8.GetString(ms.ToArray());
            }
            catch (Exception ex)
            {
                _hostBridge.Log($"[CustomWidgetStudio] ApplyZeroTokenNodeEditInPlace failed: {ex.Message}");
                return;
            }
        }

        bool localesChanged = !string.Equals(
            prevLocales?.ComputeSha256() ?? string.Empty,
            nextLocales?.ComputeSha256() ?? string.Empty,
            StringComparison.Ordinal);
        if (string.Equals(updatedJson, current, StringComparison.Ordinal) && !localesChanged)
        {
            return;
        }

        _session.AddRevision(
            updatedJson,
            changeSummary ?? _loc.T("CustomWidget.Workshop.CoCreate.ManualQuickEdit"),
            isAiChange: false,
            source: "manual",
            isCommitted: false,
            markDirty: true,
            locales: nextLocales,
            hasExplicitLocales: true,
            coalesceGroupKey: coalesceGroupKey);
        _session.PersistActiveDraftIfDirty();
        _stageViewport.ApplyLivePreviewUpdateInPlace();
    }

    internal void RecordStudioCommitDiagnostic(
        string commitSource,
        string widgetId,
        bool wasEdit,
        string manifestJson,
        string? logicJs)
    {
        CustomWidgetHealthReport? healthReport = _session.LatestHealthReport ?? BuildCurrentHealthReport(manifestJson);
        _session.RecordStudioCommitDiagnostic(commitSource, widgetId, wasEdit, manifestJson, logicJs, healthReport);
    }

    internal CustomWidgetHealthReport? BuildCurrentHealthReport(string? manifestJson)
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

        CustomWidgetManifest effectiveManifest = _session.CurrentLocales is { HasAnyLocale: true }
            ? parsed.Manifest.WithLocales(_session.CurrentLocales)
            : parsed.Manifest;

        CustomWidgetStorePayload? storeSnapshot = BuildLiveOrPreviewStoreSnapshot();
        double measuredW = 0;
        double measuredH = 0;
        if (_stageViewport.PreviewContent is not null)
        {
            var natural = _stageViewport.PreviewContent.MeasureNaturalContentSize(
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
            cultureName: _session.EffectivePreviewCultureName,
            logicJs: _session.CurrentLogicJs);
    }

    internal CustomWidgetStorePayload? BuildLiveOrPreviewStoreSnapshot()
    {
        if (_stageViewport.PreviewVm is null)
        {
            return null;
        }

        return new CustomWidgetStorePayload
        {
            Settings = new Dictionary<string, string>(_stageViewport.PreviewVm.Settings, StringComparer.Ordinal),
            State = new Dictionary<string, string>(_stageViewport.PreviewVm.State, StringComparer.Ordinal),
            Collections = _stageViewport.PreviewVm.Collections.ToDictionary(
                static kvp => kvp.Key,
                static kvp => kvp.Value.ToList(),
                StringComparer.Ordinal)
        };
    }

    internal Task ShowApiDocsDialogInStudioAsync(string? initialCapabilityId = null)
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
                _session.IsCapabilityPinned,
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

        _composerBuilder.RefreshApiDocsToggleButtons();
    }

    private async void CloseApiDocsDrawer()
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
        ApiDrawerOverlayScrim.Visibility = Visibility.Collapsed;
        _composerBuilder.RefreshApiDocsToggleButtons();

        if (ApiDrawerBorder.Visibility == Visibility.Visible)
        {
            await FluentMotionHelper.PlayExitSlideAndFadeAsync(ApiDrawerBorder, offsetX: 14f, offsetY: 0f, durationMs: 130);
        }

        if (!_isApiDocsDrawerOpen && !_isClosed)
        {
            ApplyApiDrawerLayout(WorkspaceShellGrid.ActualWidth);
        }
    }

    private void ApiDrawerOverlayScrim_Tapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
    {
        e.Handled = true;
        CloseApiDocsDrawer();
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
            ApiDrawerOverlayScrim.Visibility = Visibility.Collapsed;
            ApiDrawerBorder.Visibility = Visibility.Collapsed;
            ApiDrawerSplitter.Visibility = Visibility.Collapsed;
            ApiDrawerColumn.MinWidth = 0;
            ApiDrawerColumn.MaxWidth = double.PositiveInfinity;
            ApiDrawerColumn.Width = new GridLength(0);
            return;
        }

        ApiDrawerBorder.Visibility = Visibility.Visible;
        double effectiveWidth = shellWidth > 0 ? shellWidth : DesiredWidth;
        double dockThreshold = _session.Page == StudioPage.Preview ? 1140 : 980;

        // Wide workspace: side-by-side resizable pane; Narrow workspace: right-aligned overlay drawer with scrim
        if (effectiveWidth >= dockThreshold)
        {
            ApiDrawerOverlayScrim.Visibility = Visibility.Collapsed;
            Grid.SetColumn(ApiDrawerBorder, 2);
            Grid.SetColumnSpan(ApiDrawerBorder, 1);
            ApiDrawerBorder.HorizontalAlignment = HorizontalAlignment.Stretch;
            ApiDrawerBorder.Width = double.NaN;

            double maxAllowed = Math.Clamp(effectiveWidth - (_session.Page == StudioPage.Preview ? 680 : 500), 380, 640);
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

            ApiDrawerOverlayScrim.Visibility = Visibility.Visible;
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

        TextBox? box = _session.Page == StudioPage.Preview ? _chatPanel.RefineTextBox : _composerBuilder.StartIdeaTextBox;
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

    internal void TogglePinnedCapability(string id)
    {
        if (_session.TogglePinnedCapability(id))
        {
            _composerBuilder.RefreshActiveComposerCapabilityChrome();
            _apiDocsDrawerHandle?.RefreshPinnedState();
        }
    }

    internal void ClearPinnedCapabilities()
    {
        if (_session.ClearPinnedCapabilities())
        {
            _composerBuilder.RefreshActiveComposerCapabilityChrome();
            _apiDocsDrawerHandle?.RefreshPinnedState();
        }
    }

    internal void CancelWaiting()
    {
        _generation.CancelGeneration();
        _localeCenter.CancelI18nQueue();
        _channelOnboarding.StopOnboardingAnimationTimer();
        _generation.StopWaitingElapsedTimer();
        _session.IsInPlaceGenerating = false;
        _generation.RestoreAfterCancelledInPlaceWait();
    }

    internal void SetClipboardText(string text)
    {
        _commitAndWorkspace.LastSelfCopiedText = text;
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
                _hostBridge.Log($"[CustomWidgetStudio] Clipboard content was set but flush failed: {flushEx.Message}");
            }
        }
        catch (Exception ex)
        {
            _hostBridge.Log($"[CustomWidgetStudio] Clipboard copy failed: {ex.Message}");
        }
    }

    internal void BringToFrontForReply()
    {
        try
        {
            if (_appWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized } minimizedPresenter)
            {
                minimizedPresenter.Restore();
            }
            _appWindow.Show();
            _hostBridge.BringAuxiliaryWindowToFront(_hWnd, "custom-widget-studio-reply");
            Activate();
            _ = Win32Helper.SetForegroundWindow(_hWnd);
        }
        catch
        {
        }
    }

    internal string? TryGetTargetWidgetIdForAgentUpsert() =>
        !_isClosed &&
        _session.IsInPlaceGenerating &&
        _session.WaitingKind == WaitingKind.AgentMcp &&
        !string.IsNullOrWhiteSpace(_session.TargetWidgetId)
            ? _session.TargetWidgetId
            : null;

    internal void ResetSession()
    {
        _generation.CancelGeneration();
        _localeCenter.CancelI18nQueue();
        _channelOnboarding.StopOnboardingAnimationTimer();
        _generation.StopWaitingElapsedTimer();
        _commitAndWorkspace.DisposeCodeToolWatcher();
        _stageViewport.DisposePreview();
        _startGallery.DisposeTemplatePreviews();
        _apiDocsDrawerHandle?.RefreshPinnedState();
        if (_channelOnboarding.IsOnboardingActive)
        {
            _channelOnboarding.CloseOnboardingOverlay();
        }
        _channelOnboarding.DismissActiveOnboardingOverlay = null;
        _channelOnboarding.ResetUiDelegates();
        _chatPanel.LastRenderedChatMessageCount = 0;
        _composerBuilder.ResetUiDelegates();
        _stageInspector.ClearPreviewElementRefs();
        _chatPanel.ClearPreviewElementRefs();
        _chatPanel.ResetRenderState();
        _session.ResetState();
    }

    internal void BeginHydrateEditBaselineStorePayload(string widgetId)
    {
        if (string.IsNullOrWhiteSpace(widgetId))
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                CustomWidgetStorePayload loaded = await CustomWidgetDataStore.ForWidget(widgetId).LoadAsync();
                DispatcherQueue.TryEnqueue(() =>
                {
                    if (_isClosed ||
                        !_session.IsEditMode ||
                        !string.Equals(_session.TargetWidgetId, widgetId, StringComparison.Ordinal))
                    {
                        return;
                    }

                    _session.EditBaselineStorePayload = loaded;
                    if (_stageViewport.PreviewVm is not null && !_stageViewport.PreviewVm.HasUserRuntimeMutations)
                    {
                        _stageViewport.PreviewVm.ApplyPreviewSeedPayload(loaded);
                        _stageViewport.ReapplySessionSettingOverridesToPreviewVm();
                        _stageViewport.SyncRuntimeScriptErrorFromPreviewVm();
                        _previewTheme.RefreshCapsulePreviewCardContent();
                    }
                });
            }
            catch (Exception ex)
            {
                _hostBridge.Log($"[CustomWidgetStudio] Load edit baseline store payload failed: {ex.Message}");
            }
        });
    }

    internal void HydrateExistingWidgetSettingsForPreview(string widgetId, CustomWidgetManifest manifest)
    {
        BeginHydrateEditBaselineStorePayload(widgetId);
        _session.HydrateExistingWidgetSettingsFromDisk(widgetId, manifest);
    }

    internal void UpdateWindowTitle()
    {
        string title = _session.IsEditMode
            ? _loc.Format(
                "CustomWidget.Workshop.EditTitle",
                string.IsNullOrWhiteSpace(_session.TargetWidgetName) ? _loc.T("CustomWidget.Title") : _session.TargetWidgetName)
            : _loc.T("CustomWidget.Workshop.Title");
        Title = title;
        TitleTextBlock.Text = title;
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
                RequestedTheme = StudioUiPrimitives.GetStudioExplicitTheme(this),
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
            _hostBridge.Log($"[CustomWidgetStudio] Discard dialog failed: {ex.Message}");
            return DiscardDecision.KeepEditing;
        }
        finally
        {
            _isDialogOpen = false;
        }
    }

    internal async Task<bool> ConfirmDiscardCleanlyAsync()
    {
        DiscardDecision decision = await AskDiscardOrSaveDraftAsync();
        if (decision == DiscardDecision.DiscardCleanly)
        {
            _localeCenter.CancelI18nQueue();
            _session.DiscardActiveDraftCleanly();
            return true;
        }

        if (decision == DiscardDecision.SaveDraftAndClose)
        {
            if (_session.IsI18nQueueRunning &&
                !string.IsNullOrWhiteSpace(_session.ActiveI18nWorkingManifestJson) &&
                _session.ActiveI18nWorkingCatalog is not null)
            {
                string ckptJson = _session.ActiveI18nWorkingManifestJson!;
                CustomWidgetLocaleCatalog ckptCat = _session.ActiveI18nWorkingCatalog;
                _localeCenter.CancelI18nQueue();
                _session.PersistI18nCheckpointDraft(ckptJson, _session.CurrentLogicJs, ckptCat);
            }
            else
            {
                _session.PersistActiveDraftIfDirty();
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

        if (!_session.HasUnsavedWork)
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
            _hostBridge.Log($"[CustomWidgetStudio] AppWindow_Closing failed: {ex.Message}");
            _allowClose = true;
            Close();
        }
    }

    private async Task HandleAppWindowClosingAfterCancelAsync()
    {
        DiscardDecision decision = await AskDiscardOrSaveDraftAsync();
        if (decision == DiscardDecision.DiscardCleanly)
        {
            _localeCenter.CancelI18nQueue();
            _session.DiscardActiveDraftCleanly();
            _allowClose = true;
            Close();
        }
        else if (decision == DiscardDecision.SaveDraftAndClose)
        {
            if (_session.IsI18nQueueRunning &&
                !string.IsNullOrWhiteSpace(_session.ActiveI18nWorkingManifestJson) &&
                _session.ActiveI18nWorkingCatalog is not null)
            {
                string ckptJson = _session.ActiveI18nWorkingManifestJson!;
                CustomWidgetLocaleCatalog ckptCat = _session.ActiveI18nWorkingCatalog;
                _localeCenter.CancelI18nQueue();
                _session.PersistI18nCheckpointDraft(ckptJson, _session.CurrentLogicJs, ckptCat);
            }
            else
            {
                _session.PersistActiveDraftIfDirty();
            }

            _allowClose = true;
            Close();
        }
    }

    private void RootGrid_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (DeskBox.Helpers.ImeCompositionGuard.IsImeCompositionKeyEvent(e) || _isDialogOpen)
        {
            return;
        }

        if (_session.Page == StudioPage.Preview &&
            !_channelOnboarding.IsOnboardingActive &&
            !_session.IsAnyStudioJobRunning &&
            (Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control) & Windows.UI.Core.CoreVirtualKeyStates.Down) != 0)
        {
            object? focused = RootGrid.XamlRoot is not null
                ? Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(RootGrid.XamlRoot)
                : null;
            bool inTextInput = focused is TextBox or PasswordBox or RichEditBox or AutoSuggestBox or NumberBox or ComboBox;
            if (focused is TextBox refineTb &&
                ReferenceEquals(refineTb, _chatPanel.RefineTextBox) &&
                string.IsNullOrEmpty(refineTb.Text) &&
                !refineTb.CanUndo &&
                !refineTb.CanRedo)
            {
                inTextInput = false;
            }
            if (!inTextInput)
            {
                bool shiftDown = (Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift) & Windows.UI.Core.CoreVirtualKeyStates.Down) != 0;
                if (e.Key == VirtualKey.Z)
                {
                    e.Handled = true;
                    _chatPanel.StepRevision(shiftDown ? 1 : -1);
                    return;
                }

                if (e.Key == VirtualKey.Y && !shiftDown)
                {
                    e.Handled = true;
                    _chatPanel.StepRevision(1);
                    return;
                }
            }
        }

        if (e.Key != VirtualKey.Escape)
        {
            return;
        }

        if (_channelOnboarding.IsOnboardingActive)
        {
            e.Handled = true;
            if (_channelOnboarding.DismissActiveOnboardingOverlay is not null)
            {
                _channelOnboarding.DismissActiveOnboardingOverlay();
            }
            else
            {
                _channelOnboarding.CloseOnboardingOverlay();
            }
            return;
        }

        if (_isApiDocsDrawerOpen)
        {
            e.Handled = true;
            CloseApiDocsDrawer();
            return;
        }

        if (_session.IsI18nQueueRunning)
        {
            e.Handled = true;
            _localeCenter.CancelI18nQueue();
            return;
        }

        if (_session.IsInPlaceGenerating)
        {
            e.Handled = true;
            CancelWaiting();
            return;
        }

        if (_session.IsLocaleCenterOpen)
        {
            e.Handled = true;
            _session.IsLocaleCenterOpen = false;
            _localeCenter.RefreshLocaleCenterChrome();
            return;
        }

        if (_session.SelectedTargetNode is not null)
        {
            e.Handled = true;
            _session.SelectedTargetNode = null;
            if (_stageViewport.PreviewContent is not null)
            {
                _stageViewport.PreviewContent.SelectedNodePath = null;
            }
            _chatPanel.RefreshTargetNodePillChrome();
            return;
        }

        if (_session.IsInspectorModeEnabled)
        {
            e.Handled = true;
            _session.IsInspectorModeEnabled = false;
            if (_stageViewport.PreviewContent is not null)
            {
                _stageViewport.PreviewContent.IsNodeSelectionEnabled = false;
            }
            _stageViewport.RefreshStageToolbarChrome();
        }
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
        _previewTheme.ApplyStageBackdropVisuals();

        if (_apiDocsDrawerHandle is not null)
        {
            _apiDocsDrawerHandle = null;
            ApiDrawerBorder.Child = null;
            if (_isApiDocsDrawerOpen)
            {
                OpenApiDocsDrawer(null);
            }
        }

        if (!_isClosed && !_isDialogOpen && !_session.IsAnyStudioJobRunning)
        {
            bool wasTourActive = _channelOnboarding.ActiveOnboardingOverlayKind == OnboardingOverlayKind.StepTour;
            StudioAiChannelKind prevTourChannel = _channelOnboarding.ActiveStepTourChannel;
            int prevTourStep = _channelOnboarding.ActiveStepTourStep;

            if (_session.Page == StudioPage.Start)
            {
                _startGallery.ShowStartPage(_session.StartDraftIdea);
            }
            else if (_session.Page == StudioPage.Preview)
            {
                _stageViewport.ShowPreviewPage(justLoaded: false);
            }

            if (wasTourActive)
            {
                _ = _channelOnboarding.RunChannelStepTourAsync(++_channelOnboarding.OnboardingTourRunId, prevTourChannel, prevTourStep);
            }
        }
    }

    private void ApplyTitleBarColors()
    {
        bool isDark = StudioUiPrimitives.GetStudioExplicitTheme(this) == ElementTheme.Dark;
        bool isHighContrast = WindowsCompatibilityService.IsHighContrast;
        Win32Helper.SetWindowTheme(_hWnd, isDark);
        if (_usesSolidWindowBackdrop)
        {
            RootGrid.Background = new SolidColorBrush(isHighContrast
                ? WindowsCompatibilityService.ResolveHighContrastBackgroundColor(isDark)
                : isDark
                    ? Windows.UI.Color.FromArgb(0xFF, 0x20, 0x20, 0x20)
                    : Windows.UI.Color.FromArgb(0xFF, 0xF3, 0xF3, 0xF3));
        }

        WindowShellState.TryApplyExtendedTitleBarButtonColors(_appWindow, isDark);
        UpdateTitleBarInsets();
    }

    private void UpdateTitleBarInsets()
    {
        if (_isClosed || AppTitleBar is null)
        {
            return;
        }

        try
        {
            if (!AppWindowTitleBar.IsCustomizationSupported())
            {
                AppTitleBar.Padding = new Thickness(14, 0, 148, 0);
                return;
            }

            double scale = Win32Helper.GetDpiScaleForWindow(_hWnd, RootGrid.XamlRoot);
            if (scale <= 0)
            {
                scale = 1.0;
            }

            int leftInsetPx = _appWindow.TitleBar.LeftInset;
            int rightInsetPx = _appWindow.TitleBar.RightInset;
            if (leftInsetPx <= 0 && rightInsetPx <= 0)
            {
                AppTitleBar.Padding = new Thickness(14, 0, 148, 0);
                return;
            }

            double leftPad = Math.Max(14, leftInsetPx / scale);
            double rightPad = Math.Max(14, rightInsetPx / scale);
            AppTitleBar.Padding = new Thickness(leftPad, 0, rightPad, 0);
        }
        catch
        {
        }
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
            _session.CachedI18nSyncPlan = null;
            _session.CachedI18nSyncPlanKey = string.Empty;
            if (_apiDocsDrawerHandle is not null)
            {
                ApiDrawerBorder.Child = null;
                _apiDocsDrawerHandle = null;
            }

            UpdateWindowTitle();
            if (!_session.IsAnyStudioJobRunning && !_isDialogOpen)
            {
                if (_session.Page == StudioPage.Start)
                {
                    _startGallery.ShowStartPage(_session.StartDraftIdea);
                }
                else if (_session.Page == StudioPage.Preview)
                {
                    _stageViewport.ShowPreviewPage(justLoaded: false);
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

    private void AppWindow_Changed(AppWindow sender, AppWindowChangedEventArgs args)
    {
        if (_isClosed)
        {
            return;
        }

        if (args.DidSizeChange || args.DidPositionChange)
        {
            DispatcherQueue.TryEnqueue(UpdateTitleBarInsets);
        }

        if (!args.DidPresenterChange && !args.DidVisibilityChange)
        {
            return;
        }

        bool isMinimized = sender.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized };
        bool active = sender.IsVisible && !isMinimized;
        DispatcherQueue.TryEnqueue(() =>
        {
            if (!_isClosed)
            {
                _stageViewport.SetStudioWindowActive(active);
            }
        });
    }

    internal void SyncHeaderCommitActionsForDesktopState()
    {
        if (!_isClosed && _session.Page == StudioPage.Preview)
        {
            _stageViewport.SyncHeaderCommitActionsForCurrentMode();
        }
    }

    private void CustomWidgetStudioWindow_Activated(object sender, WindowActivatedEventArgs args)
    {
        if (_isClosed ||
            args.WindowActivationState == WindowActivationState.Deactivated)
        {
            return;
        }

        CustomWidgetAiCredentialStore.Shared.CheckForExternalChangesAndNotify();
        _channelOnboarding.NotifyChannelSelectorsRefresh();
        if (_session.Page == StudioPage.Preview)
        {
            _stageViewport.SyncHeaderCommitActionsForCurrentMode();
            _chatPanel.RefreshInPlaceGenerationChrome(refreshStream: false);
        }
    }

    private void CustomWidgetStudioWindow_Closed(object sender, WindowEventArgs args)
    {
        _isClosed = true;
        Activated -= CustomWidgetStudioWindow_Activated;
        _loc.LanguageChanged -= OnLocalizationLanguageChanged;
        CustomWidgetRemoteCatalogService.Shared.CatalogUpdated -= OnRemoteCatalogUpdated;
        CustomWidgetAgentStageStore.Changed -= OnAgentStageStoreChanged;
        DeskBoxAgentBridgeServer.McpClientConnected -= OnMcpClientConnected;
        DeskBoxAgentBridgeServer.McpActivityRecorded -= OnMcpActivityRecorded;
        CustomWidgetAiCredentialStore.Shared.CredentialsChanged -= OnAiCredentialsChanged;
        _generation.CancelGeneration();
        _localeCenter.CancelI18nQueue();
        _channelOnboarding.StopOnboardingAnimationTimer();
        _generation.StopWaitingElapsedTimer();
        _commitAndWorkspace.DisposeCodeToolWatcher();
        _stageViewport.DisposePreview();
        _startGallery.DisposeTemplatePreviews();
        RootGrid.DragOver -= _commitAndWorkspace.OnStudioRootDragOver;
        RootGrid.Drop -= _commitAndWorkspace.OnStudioRootDrop;
        _appWindow.Closing -= AppWindow_Closing;
        _appWindow.Changed -= AppWindow_Changed;
        AppTitleBar.ActualThemeChanged -= AppTitleBar_ActualThemeChanged;
        _hostBridge.AppearanceChanged -= _previewTheme.OnHostAppearanceChanged;
        _hostBridge.DisplayTopologyChanged -= OnDisplayTopologyChanged;
        _channelOnboarding.Dispose();
        _localeCenter.Dispose();
        _composerBuilder.ResetUiDelegates();
        _startGallery.Dispose();
        _previewTheme.Dispose();
        _stageInspector.ClearPreviewElementRefs();
        _stageViewport.Dispose();
        _chatPanel.Dispose();
        _generation.Dispose();
        _commitAndWorkspace.Dispose();
        RemoveMinimumSizeHook();
        WindowsCompatibilityService.ReleaseTitleBarExtensions(this, _appWindow);
        _apiDocsDrawerHandle = null;
        ApiDrawerBorder.Child = null;
        PageHost.Children.Clear();
        SystemBackdrop = null;
        _hostBridge.ReleaseRaisedBandGuest(_hWnd, "custom-widget-studio-closed");
        Closed -= CustomWidgetStudioWindow_Closed;
        CustomWidgetCapabilityRegistry.ReleaseIdleCache();
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
        if (message == WmGetMinMaxInfo && lParam != IntPtr.Zero)
        {
            return WindowShellState.ApplyMinTrackSizeWithWorkAreaClamp(
                hWnd,
                message,
                wParam,
                lParam,
                Win32Helper.GetDpiScaleForWindow(_hWnd, RootGrid.XamlRoot),
                MinimumWidth,
                MinimumHeight);
        }

        if (message == WmNcDestroy)
        {
            RemoveMinimumSizeHook();
        }

        return Win32Helper.DefSubclassProc(hWnd, message, wParam, lParam);
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
            _hostBridge.Log($"[CustomWidgetStudio] Display topology clamp failed: {ex.Message}");
        }
    }

    internal void SetPage(UIElement element)
    {
        StudioPage? previousPage = _lastRenderedPage;
        _lastRenderedPage = _session.Page;

        PageHost.Children.Clear();
        PageHost.Children.Add(element);

        if (previousPage.HasValue && previousPage.Value != _session.Page)
        {
            float dirX = _session.Page == StudioPage.Preview ? 18f : -18f;
            FluentMotionHelper.PlayEntranceSlideAndFade(element, offsetX: dirX, offsetY: 0f, durationMs: 185);
        }
        else
        {
            FluentMotionHelper.PlayEntranceSlideAndFade(element, offsetY: 10f, durationMs: 180);
        }
    }

    internal InfoBar CreatePageInfoBar()
    {
        var bar = new InfoBar
        {
            IsClosable = true,
            IsOpen = false,
            Visibility = Visibility.Collapsed
        };
        bar.Closed += (_, _) =>
        {
            bar.Visibility = Visibility.Collapsed;
            _stageViewport.RefreshScriptErrorBannerChrome();
            _chatPanel.RefreshSuggestionChips();
        };
        return bar;
    }

    internal void ShowPageMessage(
        InfoBarSeverity severity,
        string title,
        string message,
        ButtonBase? action,
        UIElement? extraContent = null)
    {
        if (_pageInfoBar is null)
        {
            return;
        }

        _pageInfoBar.Severity = severity;
        _pageInfoBar.Title = title;
        _pageInfoBar.Message = message;
        _pageInfoBar.ActionButton = action;
        _pageInfoBar.Content = extraContent;
        _pageInfoBar.Visibility = Visibility.Visible;
        _pageInfoBar.IsOpen = true;
        _stageViewport.RefreshScriptErrorBannerChrome();
        _chatPanel.RefreshSuggestionChips();
    }

    /// <summary>
    /// 统一的失败提示：一句人能看懂的标题 + 一句「下一步该做什么」 + 折叠的技术详情。
    /// </summary>
    internal void ShowFailureMessage(
        string titleKey,
        string bodyKey,
        Exception? ex,
        ButtonBase? action = null)
    {
        _hostBridge.Log($"[CustomWidgetStudio] failure {titleKey}: {ex}");

        ShowPageMessage(
            InfoBarSeverity.Error,
            _loc.T(titleKey),
            _loc.T(bodyKey),
            action,
            ex is null ? null : StudioUiPrimitives.BuildFailureTechDetails(this, ex.ToString()));
    }

    internal void AddMenuItem(MenuFlyout flyout, string glyph, string textKey, Func<Task> action)
    {
        var item = new MenuFlyoutItem
        {
            Text = _loc.T(textKey),
            Icon = new FontIcon { Glyph = glyph }
        };
        item.Click += async (_, _) =>
        {
            try
            {
                await action();
            }
            catch (Exception ex)
            {
                _hostBridge.Log($"[CustomWidgetStudio] Menu action '{textKey}' failed: {ex}");
            }
        };
        flyout.Items.Add(item);
    }

    private async Task<(byte[] PngBytes, int Width, int Height, int OverflowPx)?> CaptureStagePreviewPngAsync(bool capsuleMode)
    {
        if (_isClosed || _session.Page != StudioPage.Preview)
        {
            return null;
        }

        Border? targetFrame = capsuleMode ? _previewTheme.CapsulePreviewFrame : _stageViewport.WidgetFrame;
        if (targetFrame is null || !targetFrame.IsLoaded || targetFrame.ActualWidth < 8 || targetFrame.ActualHeight < 8)
        {
            return null;
        }

        var rtb = new RenderTargetBitmap();
        await rtb.RenderAsync(targetFrame);
        if (rtb.PixelWidth <= 0 || rtb.PixelHeight <= 0)
        {
            return null;
        }

        IBuffer pixelBuffer = await rtb.GetPixelsAsync();
        byte[] bgraPixels = pixelBuffer.ToArray();
        using var outStream = new InMemoryRandomAccessStream();
        BitmapEncoder encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, outStream);
        double dpi = 96.0 * (targetFrame.XamlRoot?.RasterizationScale ?? 1.0);
        encoder.SetPixelData(
            BitmapPixelFormat.Bgra8,
            BitmapAlphaMode.Premultiplied,
            (uint)rtb.PixelWidth,
            (uint)rtb.PixelHeight,
            dpi,
            dpi,
            bgraPixels);
        await encoder.FlushAsync();

        byte[] pngBytes = new byte[(int)outStream.Size];
        outStream.Seek(0);
        using Stream reader = outStream.AsStreamForRead();
        int offset = 0;
        while (offset < pngBytes.Length)
        {
            int read = await reader.ReadAsync(pngBytes.AsMemory(offset, pngBytes.Length - offset));
            if (read <= 0)
            {
                break;
            }
            offset += read;
        }

        int overflowPx = !capsuleMode && _session.LatestHealthReport is { VerticalOverflowPx: > 0 } hr
            ? hr.VerticalOverflowPx
            : 0;
        return (pngBytes, rtb.PixelWidth, rtb.PixelHeight, overflowPx);
    }
}
