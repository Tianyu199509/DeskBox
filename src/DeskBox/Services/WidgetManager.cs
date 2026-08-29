﻿using DeskBox.Models;
using DeskBox.Helpers;
using DeskBox.Controls.WidgetContents;
using DeskBox.ViewModels;
using DeskBox.Views;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;

namespace DeskBox.Services;

public sealed record ManagedStorageMigrationResult(
    int AffectedWidgetCount,
    string OldRootPath,
    string NewRootPath);

public sealed record QuickCaptureFileWidgetTarget(
    string WidgetId,
    string Name,
    string FolderPath);

public enum WidgetRemovalAction
{
    RemoveWidgetOnly,
    MoveManagedFolderContentsToDesktop,
    DeleteManagedFolder
}

public sealed record ManagedStorageFolderCleanupCandidate(
    string Name,
    string Path,
    int ItemCount);

internal sealed record FeatureWidgetHandler(
    WidgetKind WidgetKind,
    Func<bool, Task<IDesktopWidgetWindow?>> CreateOrShowAsync,
    Func<bool, bool, Task> SetEnabledAsync,
    Action HideLoaded);

internal sealed record WidgetWindowCreationRequest(
    WidgetConfig Config,
    bool KeepPreparedForAnimation,
    bool RevealAfterCreate,
    bool ShowRaisedWhileInitializing,
    CancellationToken CancellationToken);

internal sealed record WidgetWindowProvider(
    WidgetKind WidgetKind,
    Func<WidgetWindowCreationRequest, Task<IDesktopWidgetWindow>> CreateWindowAsync);

internal interface IDesktopWidgetWindow
{
    WidgetWindowIdentity Identity { get; }
    WidgetConfig Config { get; }
    IntPtr WindowHandle { get; }
    FrameworkElement? WindowContentRoot { get; }
    bool Visible { get; }
    bool IsRaisedAboveDesktopLayer { get; }
    bool IsCompactArrangementActive { get; }
    bool CanParticipateInCoordinatedMove { get; }
    Windows.Graphics.RectInt32 CoordinatedMoveBounds { get; }
    Windows.Foundation.Rect AnimationBounds { get; }
    Windows.Foundation.Rect RestingAnimationBounds { get; }
    void ApplyAppearancePreview();
    void ApplyPerformanceSettings();
    void BeginDisplayTopologyTransition(long generation);
    void EndDisplayTopologyTransition(long generation);
    void RestoreBoundsForCurrentTopology();
    bool TryRestoreBoundsForDisplayTopology();
    void ApplyCompactArrangement(Windows.Graphics.RectInt32 bounds, bool constrainSize);
    void ClearCompactArrangementConstraint();
    void PreviewCompactArrangement(Windows.Graphics.RectInt32 bounds);
    void SetTrayAnimationOffsetOverride(double? offsetX, double? offsetY);
    void CancelTrayAnimationAndRestorePosition();
    void PrepareTrayShowAnimation();
    void ShowPreparedAtDesktopLayer(bool persistVisibility = true);
    void ShowPreparedRaisedFromTray(bool persistVisibility = true);
    void PlayTrayShowAnimation();
    void CompleteTrayShowWithoutAnimation();
    void RevealFromTray(bool autoRestore = true);
    bool PrepareTrayHideAnimation(bool persistVisibility = true);
    void PlayPreparedTrayHideAnimation();
    WidgetTrayBatchAnimationEntry? BeginSharedTrayShowAnimation();
    WidgetTrayBatchAnimationEntry? BeginSharedTrayHideAnimation();
    void ActivateRaisedFromTrayBatch();
    void EnsureRaisedFromTrayTopMost();
    void RaiseTemporarilyFromManager();
    void BeginCoordinatedMoveParticipation(bool isSource);
    void PrepareCoordinatedMoveBounds(Windows.Graphics.RectInt32 bounds);
    void CompleteCoordinatedMoveBoundsPreview();
    void ApplyCoordinatedMoveBoundsFallback(Windows.Graphics.RectInt32 bounds);
    void CompleteCoordinatedMoveParticipation(bool hasMoved, bool isSource);
    void ForceRestoreDesktopLayerFromManager();
    void RestoreDesktopLayerFromManager();
    Task WaitForFirstPresentedFrameAsync(CancellationToken cancellationToken);
    void SetGroupDropPreview(
        bool visible,
        bool ready,
        string? messageKey = null);
    Windows.Graphics.RectInt32? GetGroupMergeTitleScreenBounds();
    void HideWindow();
    void CloseWindow();
}

/// <summary>
/// Manages the lifecycle of all desktop organizer widgets.
/// </summary>
public sealed partial class WidgetManager
{
    private const string ManagedShortcutDescriptionPrefix = "DeskBox mapped widget shortcut:";

    private readonly SettingsService _settingsService;
    private readonly FileService _fileService;
    private readonly OrganizerService _organizerService;
    private readonly ThemeService _themeService;
    private readonly QuickCaptureService _quickCaptureService;
    private readonly LocalizationService _localizationService;
    private readonly Func<string> _desktopPathProvider;
    private readonly bool _recycleManagedFolderDeletes;
    private readonly WidgetRegistry _widgetRegistry;
    private readonly WidgetSessionManager _sessionManager;
    private readonly FileWidgetHostDiagnostics _fileWidgetHostDiagnostics;
    private readonly WidgetTopologyLayoutService _topologyLayoutService = new();
    private readonly Dictionary<string, FileWidgetSession> _fileWidgets = new();
    private readonly Dictionary<string, ContentWidgetWindow> _contentWidgets = new();
    private readonly HashSet<IntPtr> _widgetWindowHandles = new();
    private readonly HashSet<string> _deletedWidgetIds = [];
    private readonly HashSet<string> _suppressClosedVisibilityPersistence = [];
    private readonly SemaphoreSlim _widgetRenameGate = new(1, 1);
    private readonly SemaphoreSlim _trayVisibilityOperationGate = new(1, 1);
    private readonly TrayToggleRequestQueue _trayToggleRequestQueue;
    private EffectivePerformanceSettings _lastPerformanceSettings;

    internal IReadOnlyDictionary<string, ContentWidgetWindow> ContentWidgets => _contentWidgets;

    public bool WidgetsRaisedFromTray => _widgetsRaisedFromTray;
    public WidgetSessionState SessionState => _sessionManager.State;
    public bool IsWidgetInteractionActive => _sessionManager.IsInteractionActive;

    internal bool HasActiveVisualWork =>
        _trayBatchAnimationDriver.IsRunning ||
        WidgetCompactAnimationCoordinator.HasActiveAnimations ||
        GetLoadedDesktopWindows()
            .OfType<WidgetWindowBase>()
            .Any(window => window.HasActiveVisualWork);

    public bool HasVisibleWidgets =>
        GetLoadedDesktopWindows().Any(window => window.Visible);

    public bool HasVisibleFileWidgets =>
        _fileWidgets.Values.Any(session => session.Host.Visible) ||
        _contentWidgets.Values
            .Distinct()
            .Any(window =>
                window.Visible &&
                window.CurrentContent is FileSurfaceContent);

    internal int LoadedWidgetCount => _widgetSurfaces.Count;

    internal int VisibleWidgetCount => GetLoadedDesktopWindows().Count(window => window.Visible);

    public bool IsWidgetWindow(IntPtr hwnd)
    {
        return _widgetWindowHandles.Contains(hwnd);
    }

    /// <summary>
    /// Returns the HWND of every currently-loaded widget window.
    /// Used by the resize guide service to detect alignment targets.
    /// </summary>
    public IReadOnlyList<IntPtr> GetAllWidgetWindowHandles()
    {
        return _widgetWindowHandles.ToList();
    }

    /// <summary>
    /// Finds the root FrameworkElement of a widget window by its HWND.
    /// Used by the resize guide service to show edge highlights on target widgets.
    /// </summary>
    public FrameworkElement? GetWidgetRootElementByHandle(IntPtr hwnd)
    {
        return GetLoadedDesktopWindows()
            .FirstOrDefault(window => window.WindowHandle == hwnd)
            ?.WindowContentRoot;
    }

    private IReadOnlyList<IDesktopWidgetWindow> GetLoadedDesktopWindows()
    {
        return _widgetSurfaces.GetSessions()
            .Select(session => session.Host)
            .GroupBy(host => host.WindowHandle)
            .Select(group => group.First())
            .ToList();
    }

    public void BeginWidgetInteraction(string reason)
    {
        App.NotifyMemoryCleanupActivity();
        _idlePeerOrderGeneration++;
        _sessionManager.BeginInteraction(reason);
    }

    public void EndWidgetInteraction(string reason)
    {
        _sessionManager.EndInteraction(reason);
        if (!_sessionManager.IsInteractionActive)
        {
            RestoreTemporarilyRaisedWidgetsToDesktopLayer(
                $"{reason}-interaction-ended");
            QueueIdleWidgetZOrderNormalization(reason);
        }
    }

    public event Action<string>? WidgetRemoved;
    public event Action<bool>? TrayLayerStateChanged;

    private static bool HasUiThreadAccess()
    {
        var dispatcherQueue = App.UiDispatcherQueue;
        return dispatcherQueue is null || dispatcherQueue.HasThreadAccess;
    }

    private static Task<T> RunOnUiThreadAsync<T>(Func<Task<T>> action)
    {
        var dispatcherQueue = App.UiDispatcherQueue;
        if (dispatcherQueue is null || dispatcherQueue.HasThreadAccess)
        {
            return action();
        }

        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!dispatcherQueue.TryEnqueue(async () =>
        {
            try
            {
                completion.SetResult(await action());
            }
            catch (Exception ex)
            {
                completion.SetException(ex);
            }
        }))
        {
            completion.SetException(new InvalidOperationException("Unable to dispatch widget lifecycle operation to the UI thread."));
        }

        return completion.Task;
    }

    private static Task RunOnUiThreadAsync(Func<Task> action)
    {
        return RunOnUiThreadAsync(async () =>
        {
            await action();
            return true;
        });
    }

    public bool ShouldHideWidgetsForTrayToggle()
    {
        IntPtr foregroundWindow = Win32Helper.GetForegroundWindow();
        bool foregroundLocal = IsDeskBoxForegroundWindow(foregroundWindow) ||
                               IsDesktopShellWindow(foregroundWindow) ||
                               IsTaskbarWindow(foregroundWindow);
        var context = new TrayToggleDecisionContext(
            WidgetLayerService.UsesDesktopPinnedMode(),
            WidgetLayerService.UsesQuickRevealMode(),
            _widgetsRaisedFromTray,
            HasVisibleWidgets,
            foregroundLocal);
        bool shouldHide = TrayToggleDecisionPolicy.ShouldHide(context);
        string reason = context.IsQuickRevealMode
            ? context.HasVisibleWidgets
                ? "quick-reveal-visible"
                : "quick-reveal-hidden"
            : context.IsDesktopPinnedMode
            ? context.HasVisibleWidgets
                ? "desktop-pinned-visible"
                : "desktop-pinned-hidden"
            : context.IsRaisedSession
                ? "raised-session"
                : !context.HasVisibleWidgets
                    ? "no-visible-windows"
                    : context.IsForegroundLocal
                        ? "foreground-local"
                        : "visible-widgets-behind";
        App.LogVerbose(
            $"[TrayBatch] ToggleDecision={(shouldHide ? "hide" : "raise")} " +
            $"reason={reason} hwnd=0x{foregroundWindow.ToInt64():X}");
        return shouldHide;
    }

    /// <summary>
    /// Enqueues one tray-toggle intent. Hotkeys and tray clicks must use this
    /// entry point so they share one serialized state machine.
    /// </summary>
    public Task ToggleWidgetsFromTrayAsync(string source = "tray-toggle")
    {
        return _trayToggleRequestQueue.EnqueueAsync(source);
    }

    private Task ExecuteQueuedTrayToggleAsync(string source)
    {
        return RunOnUiThreadAsync(() => ExecuteTrayVisibilityOperationAsync(
            source,
            async () =>
            {
                if (ShouldHideWidgetsForTrayToggle())
                {
                    await SetAllWidgetsVisibleCoreAsync(false);
                    return;
                }

                await RaiseWidgetsFromTrayCoreAsync(source);
            }));
    }

    private async Task ExecuteTrayVisibilityOperationAsync(
        string source,
        Func<Task> operation)
    {
        await _trayVisibilityOperationGate.WaitAsync();
        App.LogVerbose($"[TrayToggle] operation-start source={source}");
        try
        {
            await operation();
        }
        finally
        {
            App.LogVerbose($"[TrayToggle] operation-end source={source}");
            _trayVisibilityOperationGate.Release();
        }
    }

    public WidgetManager(
        SettingsService settingsService,
        FileService fileService,
        OrganizerService organizerService,
        ThemeService themeService,
        QuickCaptureService quickCaptureService,
        LocalizationService? localizationService = null)
        : this(
            settingsService,
            fileService,
            organizerService,
            themeService,
            quickCaptureService,
            localizationService ?? new LocalizationService(settingsService),
            () => Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            recycleManagedFolderDeletes: true)
    {
    }

    internal WidgetManager(
        SettingsService settingsService,
        FileService fileService,
        OrganizerService organizerService,
        ThemeService themeService,
        QuickCaptureService quickCaptureService,
        Func<string> desktopPathProvider,
        bool recycleManagedFolderDeletes)
        : this(
            settingsService,
            fileService,
            organizerService,
            themeService,
            quickCaptureService,
            null,
            desktopPathProvider,
            recycleManagedFolderDeletes)
    {
    }

    internal WidgetManager(
        SettingsService settingsService,
        FileService fileService,
        OrganizerService organizerService,
        ThemeService themeService,
        QuickCaptureService quickCaptureService,
        LocalizationService? localizationService,
        Func<string> desktopPathProvider,
        bool recycleManagedFolderDeletes)
    {
        _settingsService = settingsService;
        _fileService = fileService;
        _organizerService = organizerService;
        _themeService = themeService;
        _quickCaptureService = quickCaptureService;
        _localizationService = localizationService ?? new LocalizationService(settingsService);
        _desktopPathProvider = desktopPathProvider;
        _recycleManagedFolderDeletes = recycleManagedFolderDeletes;
        _widgetRegistry = WidgetRegistry.Default;
        _sessionManager = new WidgetSessionManager(App.LogVerbose);
        _fileWidgetHostDiagnostics = new FileWidgetHostDiagnostics();
        _trayToggleRequestQueue = new(ExecuteQueuedTrayToggleAsync);
        InitializeCapsuleArrangementState();
        _featureWidgetHandlers = CreateFeatureWidgetHandlers();
        _windowProviders = CreateWindowProviders();
        foreach (var kind in FeatureWidgetSettings.FeatureKinds)
        {
            _lastFeatureWidgetEnabledStates[kind] = FeatureWidgetSettings.IsEnabled(_settingsService.Settings, kind);
        }
        _lastWidgetLayerMode = SettingsService.NormalizeWidgetLayerModeSetting(_settingsService.Settings.WidgetLayerMode);
        _lastPerformanceSettings =
            PerformanceSettingsPolicy.Resolve(_settingsService.Settings);
        IconHelper.ConfigurePerformanceCacheBudget(
            _lastPerformanceSettings.CacheBudget);
        InitializeWidgetGroupPresentationDefaults();
        _settingsService.SettingsChanged += OnSettingsChanged;
        _settingsService.AppearancePreviewChanged += ApplyAppearancePreview;
        _themeService.AppearanceChanged += ApplyAppearancePreview;
        App.Log(
            $"[WidgetManager] Standalone file host strategy=UnifiedContentOnly " +
            $"legacyFallbackAvailable=false fallbackRequests={_fileWidgetHostDiagnostics.FallbackRequestCount} " +
            $"fallbackReason={_fileWidgetHostDiagnostics.LastFallbackReason ?? "none"}");
    }

    private Dictionary<WidgetKind, FeatureWidgetHandler> CreateFeatureWidgetHandlers()
    {
        FeatureWidgetHandler[] handlers =
        [
            new(
                WidgetKind.QuickCapture,
                async reveal => await CreateOrShowQuickCaptureWidgetAsync(reveal),
                SetQuickCaptureEnabledAsync,
                CloseLoadedQuickCaptureWidgets),
            new(
                WidgetKind.Todo,
                async _ => await CreateTodoWidgetAsync(),
                SetTodoEnabledAsync,
                () => HideAndCloseFeatureWidgetAsync(WidgetKind.Todo)),
            new(
                WidgetKind.Music,
                async _ => await CreateSingletonContentFeatureWidgetAsync(WidgetKind.Music),
                SetContentFeatureWidgetEnabledAsync,
                () => HideAndCloseFeatureWidgetAsync(WidgetKind.Music)),
            new(
                WidgetKind.Weather,
                async _ => await CreateSingletonContentFeatureWidgetAsync(WidgetKind.Weather),
                SetWeatherFeatureWidgetEnabledAsync,
                () => HideAndCloseFeatureWidgetAsync(WidgetKind.Weather)),
            new(
                WidgetKind.Search,
                async _ => await CreateSingletonContentFeatureWidgetAsync(WidgetKind.Search),
                SetSearchFeatureWidgetEnabledAsync,
                () => HideAndCloseFeatureWidgetAsync(WidgetKind.Search)),
            new(
                WidgetKind.Glance,
                CreateOrShowGlanceWidgetsAsync,
                SetGlanceFeatureWidgetEnabledAsync,
                () => CloseLoadedFeatureWidgetWindows(WidgetKind.Glance))
        ];

        return handlers.ToDictionary(handler => handler.WidgetKind);
    }

    private Dictionary<WidgetKind, WidgetWindowProvider> CreateWindowProviders()
    {
        WidgetWindowProvider[] providers =
        [
            new(
                WidgetKind.File,
                async request => await CreateContentWidgetFromConfigAsync(
                    request.Config,
                    request.KeepPreparedForAnimation,
                    request.RevealAfterCreate,
                    request.ShowRaisedWhileInitializing,
                    request.CancellationToken)),
            new(
                WidgetKind.QuickCapture,
                async request => await CreateContentWidgetFromConfigAsync(
                    request.Config,
                    request.KeepPreparedForAnimation,
                    request.RevealAfterCreate,
                    request.ShowRaisedWhileInitializing,
                    request.CancellationToken)),
            new(
                WidgetKind.Todo,
                async request => await CreateContentWidgetFromConfigAsync(
                    request.Config,
                    request.KeepPreparedForAnimation,
                    request.RevealAfterCreate,
                    request.ShowRaisedWhileInitializing,
                    request.CancellationToken)),
            new(
                WidgetKind.Music,
                async request => await CreateContentWidgetFromConfigAsync(
                    request.Config,
                    request.KeepPreparedForAnimation,
                    request.RevealAfterCreate,
                    request.ShowRaisedWhileInitializing,
                    request.CancellationToken)),
            new(
                WidgetKind.Weather,
                async request => await CreateContentWidgetFromConfigAsync(
                    request.Config,
                    request.KeepPreparedForAnimation,
                    request.RevealAfterCreate,
                    request.ShowRaisedWhileInitializing,
                    request.CancellationToken)),
            new(
                WidgetKind.Search,
                async request => await CreateContentWidgetFromConfigAsync(
                    request.Config,
                    request.KeepPreparedForAnimation,
                    request.RevealAfterCreate,
                    request.ShowRaisedWhileInitializing,
                    request.CancellationToken)),
            new(
                WidgetKind.Glance,
                async request => await CreateContentWidgetFromConfigAsync(
                    request.Config,
                    request.KeepPreparedForAnimation,
                    request.RevealAfterCreate,
                    request.ShowRaisedWhileInitializing,
                    request.CancellationToken))
        ];

        return providers.ToDictionary(provider => provider.WidgetKind);
    }

    private void OnSettingsChanged()
    {
        if (_settingsService.LastNotifiedChangeKind == SettingsChangeKind.Appearance)
        {
            // The appearance preview channel already pushed visuals to every
            // loaded window; none of the reactions below observe
            // appearance-dimension settings.
            return;
        }

        ApplyWidgetLayerModeIfChanged();
        ApplyCapsuleArrangementIfChanged();

        foreach (var kind in FeatureWidgetSettings.FeatureKinds)
        {
            bool enabled = FeatureWidgetSettings.IsEnabled(_settingsService.Settings, kind);
            if (_lastFeatureWidgetEnabledStates.TryGetValue(kind, out bool lastEnabled) &&
                lastEnabled == enabled)
            {
                continue;
            }

            _lastFeatureWidgetEnabledStates[kind] = enabled;
            ApplyFeatureWidgetEnabledState(kind, enabled);
        }

        RefreshWidgetGroupPresentationDefaultsIfChanged();
        ApplyPerformanceSettingsIfChanged();
    }

    private void ApplyPerformanceSettingsIfChanged()
    {
        if (!HasUiThreadAccess())
        {
            App.UiDispatcherQueue?.TryEnqueue(
                ApplyPerformanceSettingsIfChanged);
            return;
        }

        EffectivePerformanceSettings current =
            PerformanceSettingsPolicy.Resolve(_settingsService.Settings);
        if (current == _lastPerformanceSettings)
        {
            return;
        }

        _lastPerformanceSettings = current;
        IconHelper.ConfigurePerformanceCacheBudget(current.CacheBudget);
        foreach (IDesktopWidgetWindow window in GetLoadedDesktopWindows())
        {
            window.ApplyPerformanceSettings();
        }

        App.NotifyPerformanceSettingsChanged();
    }

    private void ApplyWidgetLayerModeIfChanged()
    {
        string layerMode = SettingsService.NormalizeWidgetLayerModeSetting(_settingsService.Settings.WidgetLayerMode);
        if (string.Equals(layerMode, _lastWidgetLayerMode, StringComparison.Ordinal))
        {
            return;
        }

        string previousMode = _lastWidgetLayerMode;
        _lastWidgetLayerMode = layerMode;
        WidgetLayerService.InvalidateDesktopIconViewCache();
        App.Log($"[WidgetManager] Widget layer mode changed {previousMode}->{layerMode}");
        if (string.Equals(layerMode, SettingsService.WidgetLayerModeQuickReveal, StringComparison.Ordinal))
        {
            _ = SetAllWidgetsVisibleAsync(false);
            return;
        }

        if (string.Equals(previousMode, SettingsService.WidgetLayerModeQuickReveal, StringComparison.Ordinal))
        {
            _ = SetAllWidgetsVisibleAsync(true);
            return;
        }

        RefreshVisibleWidgetDesktopLayers("layer-mode-changed");
    }

    public void RefreshVisibleWidgetDesktopLayers(string reason)
    {
        if (!HasUiThreadAccess())
        {
            App.UiDispatcherQueue.TryEnqueue(() => RefreshVisibleWidgetDesktopLayers(reason));
            return;
        }

        App.Log($"[WidgetManager] Refresh visible widget desktop layers reason={reason}");
        if (WidgetLayerService.UsesQuickRevealMode())
        {
            _ = SetAllWidgetsVisibleAsync(false);
            return;
        }

        ClearTemporaryRaiseLease(reason);
        foreach (var window in GetLoadedDesktopWindows())
        {
            if (!window.Visible)
            {
                continue;
            }

            try
            {
                window.ForceRestoreDesktopLayerFromManager();
            }
            catch (Exception ex)
            {
                App.Log($"[WidgetManager] Failed to refresh widget desktop layer {FormatHostWindow(window)}: {ex}");
            }
        }

        QueueIdleWidgetZOrderNormalization(reason);
    }

    private void ApplyAppearancePreview()
    {
        if (_isApplyingAppearancePreview)
        {
            return;
        }

        _isApplyingAppearancePreview = true;
        try
        {
            foreach (IDesktopWidgetWindow window in GetLoadedDesktopWindows())
            {
                window.ApplyAppearancePreview();
            }
        }
        finally
        {
            _isApplyingAppearancePreview = false;
        }
    }

    /// <summary>
    /// Restore every enabled widget for the new application session.
    /// </summary>
    public async Task RestoreWidgetsAsync()
    {
        RepairLegacyContentFeatureFileShells();

        // Dedup singleton feature widgets. Glance intentionally supports
        // multiple independently configured instances.
        DeduplicateFeatureWidgets();
        NormalizeWidgetGroupsForRuntime();
        if (_topologyLayoutService.ActivateCurrentTopology(_settingsService.Settings))
        {
            _settingsService.SaveDebounced(notifySubscribers: false);
        }

        // A process shutdown closes every HWND. Closed handlers historically
        // persisted that teardown as IsVisible=false, which made the next
        // launch restore nothing. Startup is a new visible session: restore
        // every enabled surface, independently of the previous visibility
        // flag, and synchronize group-level visibility before creating HWNDs.
        IReadOnlyList<WidgetConfig> enabledConfigs =
            WidgetStartupRestorePolicy.SelectEnabledWidgets(
                _settingsService.Settings,
                IsDeleted);

        foreach (var unsupportedConfig in enabledConfigs.Where(widget => !_widgetRegistry.CanCreateWindow(widget.WidgetKind)))
        {
            string reason = _widgetRegistry.IsKnown(unsupportedConfig.WidgetKind)
                ? "not-implemented-yet"
                : "unknown-kind";
            App.Log($"[WidgetManager] Skipping widget restore reason={reason} widget={FormatWidget(unsupportedConfig)}");
        }

        var configs = enabledConfigs.Where(widget =>
                _widgetRegistry.IsAvailableForSession(widget, _settingsService.Settings))
            .ToList();

        if (WidgetStartupRestorePolicy.MarkVisible(
                _settingsService.Settings,
                configs))
        {
            _settingsService.SaveDebounced(notifySubscribers: false);
        }

        using var perfScope = PerformanceLogger.Measure("WidgetManager.RestoreWidgets", $"count={configs.Count}");
        foreach (var config in configs)
        {
            try
            {
                using var widgetPerfScope = PerformanceLogger.Measure(
                    "WidgetManager.RestoreWidget",
                    $"id={config.Id} name={config.Name}");
                await CreateRegisteredWidgetFromConfigAsync(config);
            }
            catch (Exception ex)
            {
                App.Log($"[WidgetManager] Failed to restore widget '{config.Name}' ({config.Id}): {ex}");
            }

            await Task.Yield();
        }

        // A grouped widget owns one persistent content surface.  Restoring
        // only the active member above is normally sufficient, but the host
        // can still be hidden or positioned by WinUI after its content tree
        // finishes loading.  Re-show each persisted visible group once the
        // complete restore pass has settled so a group cannot silently lose
        // its surface during startup.
        await RestoreVisibleWidgetGroupsAsync();

        // Window creation can temporarily apply compact/capsule geometry
        // before the surface host has finished loading. Reconcile every
        // restored host once more from the persisted placement so a grouped
        // surface cannot remain stranded just outside the current work area.
        RestoreLoadedWidgetBoundsAfterStartup();

        QueueDeferredStartupWidgetBoundsReconciliation();

        if (configs.Count > 0 && WidgetLayerService.UsesQuickRevealMode())
        {
            await SetAllWidgetsVisibleCoreAsync(false);
            App.LogVerbose("[WidgetManager] Startup widgets hidden for quick-reveal layer");
        }
        else if (configs.Count > 0)
        {
            RaiseVisibleWidgetsTemporarily("startup-restore");
            _sessionManager.MarkDesktopResting("restore-widgets");
            QueueVisibleGroupedFileIconRecoveryAfterStartup();
        }
    }

    /// <summary>
    /// Create a new widget backed by the default managed storage root.
    /// </summary>
    public Task CreateManagedWidgetAsync(string? name = null)
    {
        return CreateManagedWidgetCoreAsync(name, placeForFirstRun: false);
    }

    internal Task CreateInitialManagedWidgetAsync(string? name = null)
    {
        return CreateManagedWidgetCoreAsync(name, placeForFirstRun: true);
    }

    private async Task CreateManagedWidgetCoreAsync(string? name, bool placeForFirstRun)
    {
        name = string.IsNullOrWhiteSpace(name)
            ? _localizationService.T("Widget.DefaultName")
            : name;
        string managedFolderName = CreateManagedFolderName(name);
        string folderPath = BuildManagedFolderPath(managedFolderName);
        Directory.CreateDirectory(folderPath);

        var config = new WidgetConfig
        {
            Name = name,
            WidgetKind = WidgetKind.File,
            MappedFolderPath = folderPath,
            FollowsDefaultStoragePath = true,
            ManagedFolderName = managedFolderName,
            BoundsCoordinateVersion = WidgetConfig.CurrentBoundsCoordinateVersion,
            Width = _settingsService.Settings.DefaultWidgetWidth,
            Height = _settingsService.Settings.DefaultWidgetHeight
        };

        if (placeForFirstRun)
        {
            Windows.Graphics.PointInt32 pointerPosition = new(0, 0);
            if (Win32Helper.GetCursorPos(out Win32Helper.POINT cursor))
            {
                pointerPosition = new Windows.Graphics.PointInt32(cursor.X, cursor.Y);
            }

            var workArea = DisplayArea.GetFromPoint(
                pointerPosition,
                DisplayAreaFallback.Primary).WorkArea;
            InitialFileWidgetPlacementPolicy.Apply(
                config,
                workArea,
                WidgetPositioningService.GetDpiScale(workArea));
        }

        _settingsService.Settings.Widgets.Add(config);
        await _settingsService.SaveAsync();

        await CreateWidgetFromConfigAsync(config, revealAfterCreate: true);
    }

    public async Task CreateWidgetOfKindAsync(WidgetKind widgetKind)
    {
        if (!_widgetRegistry.CanCreateWindow(widgetKind))
        {
            throw new NotSupportedException($"Widget kind '{widgetKind}' is not registered as creatable.");
        }

        switch (widgetKind)
        {
            case WidgetKind.File:
                await CreateManagedWidgetAsync(_localizationService.T("Widget.DefaultNameShort"));
                break;
            case WidgetKind.Todo:
                await CreateTodoWidgetAsync();
                break;
            case WidgetKind.Music:
                await CreateSingletonContentFeatureWidgetAsync(widgetKind);
                break;
            case WidgetKind.Glance:
                await CreateGlanceWidgetAsync();
                break;
            default:
                if (IsContentFeatureWidgetKind(widgetKind))
                {
                    await CreateSingletonContentFeatureWidgetAsync(widgetKind);
                    break;
                }

                await CreateRegisteredWidgetFromConfigAsync(new WidgetConfig
                {
                    Name = GetDefaultFeatureWidgetTitle(
                        widgetKind,
                        new WidgetContentFactory(_localizationService).GetDescriptor(widgetKind)),
                    WidgetKind = widgetKind,
                    BoundsCoordinateVersion = WidgetConfig.CurrentBoundsCoordinateVersion,
                    Width = _settingsService.Settings.DefaultWidgetWidth,
                    Height = _settingsService.Settings.DefaultWidgetHeight
                }, revealAfterCreate: true);
                break;
        }
    }

    /// <summary>
    /// Create a widget mapped to an arbitrary folder.
    /// </summary>
    public async Task CreateFolderWidgetAsync(string folderPath)
    {
        string normalizedPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folderPath));
        EnsureFileWidgetPathAvailable(normalizedPath);

        string folderName = Path.GetFileName(normalizedPath);
        if (string.IsNullOrWhiteSpace(folderName))
        {
            folderName = normalizedPath;
        }

        var config = new WidgetConfig
        {
            Name = folderName,
            WidgetKind = WidgetKind.File,
            MappedFolderPath = normalizedPath,
            BoundsCoordinateVersion = WidgetConfig.CurrentBoundsCoordinateVersion,
            Width = _settingsService.Settings.DefaultWidgetWidth,
            Height = _settingsService.Settings.DefaultWidgetHeight
        };

        _settingsService.Settings.Widgets.Add(config);
        SyncMappedWidgetShortcut(config);
        await _settingsService.SaveAsync();

        await CreateWidgetFromConfigAsync(config, revealAfterCreate: true);
    }

    public void EnsureFileWidgetPathAvailable(string folderPath, string? excludedWidgetId = null)
    {
        string normalizedPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folderPath));
        WidgetConfig? conflict = _settingsService.Settings.Widgets.FirstOrDefault(widget =>
            widget.WidgetKind == WidgetKind.File &&
            !IsDeleted(widget.Id) &&
            !string.Equals(widget.Id, excludedWidgetId, StringComparison.Ordinal) &&
            !string.IsNullOrWhiteSpace(widget.MappedFolderPath) &&
            FileService.PathsOverlap(normalizedPath, widget.MappedFolderPath));

        if (conflict is null)
        {
            return;
        }

        throw new InvalidOperationException(_localizationService.Format(
            "Widget.Error.FileWidgetPathConflict",
            conflict.Name));
    }

    /// <summary>
    /// Show a specific widget by id.
    /// </summary>
    public async Task<bool> ShowWidgetAsync(string widgetId, bool reveal = true, bool autoRestoreOnReveal = true)
    {
        if (IsDeleted(widgetId))
        {
            return false;
        }

        var config = FindConfig(widgetId);
        if (config is null || config.IsDisabled)
        {
            return false;
        }

        if (!_widgetRegistry.IsAvailableForSession(config, _settingsService.Settings))
        {
            return false;
        }

        config.IsVisible = true;
        _settingsService.SaveDebounced(notifySubscribers: false);

        WidgetGroupConfig? group = WidgetGroupSettings.FindByMember(
            _settingsService.Settings,
            widgetId);
        if (group is not null)
        {
            group.IsVisible = true;
            foreach (string memberId in group.MemberIds)
            {
                if (FindConfig(memberId) is { } memberConfig)
                {
                    memberConfig.IsVisible = true;
                }
            }
            _settingsService.SaveDebounced(notifySubscribers: false);

            if (!string.Equals(group.ActiveMemberId, widgetId, StringComparison.Ordinal))
            {
                return await SwitchWidgetGroupMemberAsync(widgetId);
            }

            ApplyGroupLayoutToMember(group, config);
        }

        if (config.WidgetKind == WidgetKind.QuickCapture)
        {
            ContentWidgetWindow quickCaptureWindow;
            if (_contentWidgets.TryGetValue(widgetId, out var existingQuickCapture))
            {
                quickCaptureWindow = existingQuickCapture;
            }
            else
            {
                quickCaptureWindow = await CreateContentWidgetFromConfigAsync(
                    config,
                    keepPreparedForAnimation: !reveal);
            }

            ShowLoadedWidgetWindow(
                quickCaptureWindow,
                reveal,
                autoRestoreOnReveal);
            return true;
        }

        if (IsContentFeatureWidgetKind(config.WidgetKind))
        {
            return await ShowContentWidgetAsync(config, reveal);
        }

        if (config.WidgetKind != WidgetKind.File)
        {
            App.Log($"[WidgetManager] Show skipped reason=unsupported-kind widget={FormatWidget(config)}");
            return false;
        }

        if (_fileWidgets.TryGetValue(widgetId, out var fileSession))
        {
            ShowLoadedWidgetWindow(
                fileSession.Host,
                reveal,
                autoRestoreOnReveal);

            return true;
        }

        var window = await CreateWidgetFromConfigAsync(config, keepPreparedForAnimation: !reveal);
        ShowLoadedWidgetWindow(window, reveal, autoRestoreOnReveal);

        return true;
    }

    internal bool SetWidgetOnboardingTopMost(
        string widgetId,
        bool isTopMost)
    {
        IDesktopWidgetWindow? window = null;
        if (_fileWidgets.TryGetValue(widgetId, out var fileSession))
        {
            window = fileSession.Host;
        }
        else if (_contentWidgets.TryGetValue(widgetId, out var contentWindow))
        {
            window = contentWindow;
        }
        if (window is null)
        {
            return false;
        }

        if (isTopMost)
        {
            if (WidgetLayerService.UsesDesktopPinnedMode())
            {
                window.RaiseTemporarilyFromManager();
            }
            else
            {
                Win32Helper.SetWindowTopMost(
                    window.WindowHandle,
                    showWindow: false);
            }

            return true;
        }

        window.ForceRestoreDesktopLayerFromManager();
        return true;
    }

    private static void ShowLoadedWidgetWindow(
        IDesktopWidgetWindow window,
        bool reveal,
        bool autoRestoreOnReveal)
    {
        window.RestoreBoundsForCurrentTopology();
        if (reveal)
        {
            window.RevealFromTray(autoRestoreOnReveal);
            return;
        }

        window.PrepareTrayShowAnimation();
        window.ShowPreparedAtDesktopLayer();
        window.CompleteTrayShowWithoutAnimation();
    }

    private async Task<bool> ShowContentWidgetAsync(WidgetConfig config, bool reveal)
    {
        if (_contentWidgets.TryGetValue(config.Id, out var contentWindow))
        {
            contentWindow.RestoreBoundsForCurrentTopology();
            contentWindow.PrepareTrayShowAnimation();
            if (reveal)
            {
                contentWindow.ShowPreparedRaisedFromTray();
                contentWindow.PlayTrayShowAnimation();
            }
            else
            {
                contentWindow.ShowPreparedAtDesktopLayer();
                contentWindow.CompleteTrayShowWithoutAnimation();
            }

            return true;
        }

        var createdWindow = await CreateContentWidgetFromConfigAsync(
            config,
            keepPreparedForAnimation: !reveal,
            revealAfterCreate: reveal);
        createdWindow.RestoreBoundsForCurrentTopology();
        if (!reveal)
        {
            createdWindow.PrepareTrayShowAnimation();
            createdWindow.ShowPreparedAtDesktopLayer();
            createdWindow.CompleteTrayShowWithoutAnimation();
        }

        return true;
    }

    /// <summary>
    /// Show or hide all currently managed widgets.
    /// </summary>
    public Task SetAllWidgetsVisibleAsync(bool visible)
    {
        return RunOnUiThreadAsync(() => ExecuteTrayVisibilityOperationAsync(
            $"set-all:{visible}",
            () => SetAllWidgetsVisibleCoreAsync(visible)));
    }

    private async Task SetAllWidgetsVisibleCoreAsync(bool visible)
    {
        using var perfScope = PerformanceLogger.Measure("WidgetManager.SetAllWidgetsVisible", $"visible={visible}");
        bool holdQuickRevealTopMostDuringHide =
            !visible &&
            _widgetsRaisedFromTray &&
            WidgetLayerService.UsesQuickRevealMode();
        App.LogVerbose(
            $"[TrayBatch] SetAllVisible requested visible={visible} raised={_widgetsRaisedFromTray} " +
            $"loadedFile={_fileWidgets.Count} loadedContent={_contentWidgets.Count}");
        CancelActiveTrayAnimationsAndRestorePositions();
        if (visible)
        {
            App.CancelBackgroundMemoryCleanup();
            var candidates = _settingsService.Settings.Widgets
                .Where(IsSessionCandidate)
                .ToList();
            App.LogVerbose($"[TrayBatch] SetAllVisible candidates={candidates.Count} widgets={FormatWidgetList(candidates)}");

            var windowsToShow = new List<IDesktopWidgetWindow>();
            foreach (var widget in candidates)
            {
                try
                {
                    var window = await PrepareWidgetForBatchShowAsync(widget, showRaisedWhileInitializing: true);
                    if (window is null)
                    {
                        continue;
                    }

                    windowsToShow.Add(window);
                }
                catch (Exception ex)
                {
                    App.Log($"[WidgetManager] Failed to prepare widget for visible state '{widget.Name}' ({widget.Id}): {ex}");
                }
            }

            App.LogVerbose($"[TrayBatch] SetAllVisible preparedShow={windowsToShow.Count}/{candidates.Count}");
            var windowsToAnimate = windowsToShow
                .Where(window => !window.Visible)
                .ToList();
            PrepareTrayShowAnimations(windowsToAnimate);

            var shownWindows = new List<IDesktopWidgetWindow>();
            foreach (var window in windowsToShow)
            {
                try
                {
                    if (window.Visible)
                    {
                        shownWindows.Add(window);
                        continue;
                    }

                    window.ShowPreparedAtDesktopLayer(persistVisibility: false);
                    shownWindows.Add(window);
                }
                catch (Exception ex)
                {
                    App.Log($"[WidgetManager] Failed to show prepared widget at desktop layer {FormatHostWindow(window)}: {ex}");
                }
            }

            PlayPreparedTrayShowAnimations(windowsToAnimate);
            _sessionManager.MarkDesktopResting("set-all-visible");
            NormalizeIdleWidgetZOrder("set-all-visible");
            SaveBatchVisibilityState();
            await _trayBatchAnimationDriver.WaitForIdleAsync();
            App.LogVerbose($"[TrayBatch] SetAllVisible completed visible=true prepared={windowsToShow.Count} shown={shownWindows.Count}");
            return;
        }

        CancelAllWidgetSurfaceSwitches();
        var hideCandidates = GetLoadedDesktopWindows()
            .Where(window => window.Visible)
            .ToList();

        ApplyTrayAnimationGroupOffset(hideCandidates);

        var windowsToHide = new List<IDesktopWidgetWindow>();
        foreach (var window in hideCandidates)
        {
            try
            {
                if (window.PrepareTrayHideAnimation(persistVisibility: false))
                {
                    windowsToHide.Add(window);
                }
            }
            catch (Exception ex)
            {
                App.Log($"[WidgetManager] Failed to prepare widget hide {FormatHostWindow(window)}: {ex}");
            }
        }

        App.LogVerbose($"[TrayBatch] SetAllVisible preparedHide={windowsToHide.Count}");
        if (holdQuickRevealTopMostDuringHide)
        {
            IReadOnlyList<IDesktopWidgetWindow> orderedWindows =
                GetWindowsInIdleHighestFirstOrder(windowsToHide);
            WidgetLayerService.HoldGroupTopMostWithoutActivation(
                orderedWindows.Select(window => window.WindowHandle).ToList());
            App.LogVerbose(
                $"[QuickReveal] Holding topmost through hide animation count={orderedWindows.Count}");
        }

        PlayPreparedTrayHideAnimations(windowsToHide);

        ClearTemporaryRaiseLease("set-all-hidden");
        SetWidgetsRaisedFromTray(false);
        _sessionManager.MarkHidden("set-all-hidden");
        _trayRaiseBatchGeneration++;
        StopTrayLayerRestoreMonitor();
        SaveBatchVisibilityState();
        await _trayBatchAnimationDriver.WaitForIdleAsync();
        App.LogVerbose($"[TrayBatch] SetAllVisible completed visible=false prepared={windowsToHide.Count}");
        App.ScheduleBackgroundMemoryCleanup();

        return;
    }

    /// <summary>
    /// Restores all loaded widget windows to their correct positions for
    /// the current display topology.  Called when displays are added,
    /// removed, or reconfigured (hot-plug, resolution change, DPI change).
    /// </summary>
    public async Task<bool> RestoreWidgetPositionsAsync(long generation, string reasons)
    {
        using var perfScope = PerformanceLogger.Measure("WidgetManager.RestoreWidgetPositions");
        App.Log(
            $"[WidgetManager] Restoring widget positions for current display topology " +
            $"generation={generation} reasons={reasons}");

        if (_sessionManager.IsInteractionActive)
        {
            App.LogVerbose(
                $"[WidgetManager] Deferring topology generation={generation}; widget interaction is active");
            return false;
        }

        bool allRestored = true;
        IReadOnlyList<IDesktopWidgetWindow> windows = GetLoadedDesktopWindows();
        foreach (IDesktopWidgetWindow window in windows)
        {
            window.BeginDisplayTopologyTransition(generation);
        }

        try
        {
            if (_topologyLayoutService.ActivateCurrentTopology(_settingsService.Settings))
            {
                _settingsService.SaveDebounced(notifySubscribers: false);
            }

            foreach (IDesktopWidgetWindow window in windows)
            {
                try
                {
                    allRestored &= window.TryRestoreBoundsForDisplayTopology();
                }
                catch (Exception ex)
                {
                    allRestored = false;
                    App.Log($"[WidgetManager] Failed to restore position for widget '{window.Identity.WidgetId}': {ex.Message}");
                }
            }
        }
        finally
        {
            foreach (IDesktopWidgetWindow window in windows)
            {
                window.EndDisplayTopologyTransition(generation);
            }
        }

        await Task.Yield();
        QueueIdleWidgetZOrderNormalization("display-topology-restored");
        return allRestored;
    }

    internal void CaptureCurrentTopologyLayout(WidgetConfig config)
    {
        if (_topologyLayoutService.CaptureCurrentSurface(_settingsService.Settings, config))
        {
            _settingsService.SaveDebounced(notifySubscribers: false);
        }
    }

    /// <summary>
    /// Remove a widget and close its window.
    /// </summary>
    public async Task RemoveWidgetAsync(string widgetId, WidgetRemovalAction removalAction = WidgetRemovalAction.RemoveWidgetOnly)
    {
        var config = FindConfig(widgetId);
        if (config is not null)
        {
            await RemoveWidgetFromGroupAsync(widgetId, revealStandalone: false);
        }
        _deletedWidgetIds.Add(widgetId);

        if (_fileWidgets.TryGetValue(widgetId, out var fileSession))
        {
            App.Log($"[WidgetManager] Retiring widget window for delete: {widgetId}");
            _fileWidgets.Remove(widgetId);
        }

        if (_contentWidgets.TryGetValue(widgetId, out var contentWindow))
        {
            App.Log($"[WidgetManager] Retiring content widget window for delete: {widgetId}");
            _contentWidgets.Remove(widgetId);
            _widgetWindowHandles.Remove(contentWindow.WindowHandle);
            // Explicitly dispose content (e.g. MusicWidgetViewModel) BEFORE
            // closing the window.  The Closed event handler also calls
            // DisposeContent, but if the event is delayed or fails, the
            // MusicSessionService's event subscriptions on the WinRT
            // singleton would keep the old ViewModel alive indefinitely.
            try
            {
                if (contentWindow.CurrentContent is IDisposable disposableContent)
                {
                    disposableContent.Dispose();
                }
            }
            catch (Exception ex) { App.Log($"[WidgetManager] Content dispose failed during delete: {ex.Message}"); }
            try { contentWindow.HideWindow(); } catch (Exception ex) { App.Log($"[WidgetManager] HideWindow failed during delete: {ex.Message}"); }
            try { contentWindow.Close(); } catch (Exception ex) { App.Log($"[WidgetManager] Close failed during delete: {ex.Message}"); }
        }

        if (config is not null)
        {
            try
            {
                await ApplyWidgetRemovalActionAsync(config, removalAction);
            }
            catch (Exception ex)
            {
                App.Log($"[WidgetManager] Managed folder cleanup failed while deleting widget '{widgetId}'. The widget will be removed and the folder will be kept. {ex}");
            }

            RemoveMappedWidgetShortcut(config);
        }

        _settingsService.RemoveWidgetImmediate(widgetId);
        _topologyLayoutService.RemoveSurface(_settingsService.Settings, widgetId);
        ClearWidgetGroupTransientState(widgetId);
        if (config is not null && FeatureWidgetSettings.IsFeatureWidget(config.WidgetKind))
        {
            if (config.WidgetKind == WidgetKind.Glance)
            {
                await GlanceWidgetStore.DeleteForWidgetAsync(config.Id);
                bool hasRemainingGlanceWidget = _settingsService.Settings.Widgets.Any(widget =>
                    widget.WidgetKind == WidgetKind.Glance &&
                    !IsDeleted(widget.Id));
                if (!hasRemainingGlanceWidget)
                {
                    SetFeatureWidgetEnabledState(WidgetKind.Glance, false);
                }
            }
            else
            {
                SetFeatureWidgetEnabledState(config.WidgetKind, false);
            }
        }
        await _settingsService.SaveAsync();
        _deletedWidgetIds.Remove(widgetId);
        App.Log($"[WidgetManager] Widget delete persisted: {widgetId} kind={config?.WidgetKind} featureEnabled={GetFeatureWidgetEnabledState(config?.WidgetKind)}");
        WidgetRemoved?.Invoke(widgetId);
    }

    public async Task RenameWidgetAsync(string widgetId, string newName)
    {
        newName = newName.Trim();
        if (string.IsNullOrWhiteSpace(newName))
        {
            throw new InvalidOperationException(_localizationService.T("Widget.Validation.NameRequired"));
        }

        await _widgetRenameGate.WaitAsync();
        try
        {
            await RenameWidgetCoreAsync(widgetId, newName);
        }
        finally
        {
            _widgetRenameGate.Release();
        }
    }

    private async Task RenameWidgetCoreAsync(string widgetId, string newName)
    {
        var config = FindConfig(widgetId);
        if (config is null || IsDeleted(widgetId))
        {
            return;
        }

        if (config.FollowsDefaultStoragePath)
        {
            await RenameManagedWidgetFolderAsync(config, newName);
        }
        else
        {
            SyncMappedWidgetShortcut(config, newName);
        }

        config.Name = newName;
        config.IsDefaultTitle = false;
        _settingsService.UpdateWidget(config);
        if (WidgetGroupSettings.FindByMember(_settingsService.Settings, widgetId) is not null)
        {
            RaiseWidgetGroupsChanged();
        }
    }

    private void SyncStorageFolderEntries(string oldRootPath)
    {
        if (!string.IsNullOrWhiteSpace(oldRootPath))
        {
            RemoveAllMappedWidgetShortcuts(oldRootPath);
        }

        SyncStorageFolderEntries();
    }

    public void RemoveWidget(string widgetId)
    {
        _ = RemoveWidgetAsync(widgetId);
    }

    public void ClearSelectionsExcept(string activeWidgetId)
    {
        foreach (var (widgetId, fileSession) in _fileWidgets.ToList())
        {
            if (string.Equals(widgetId, activeWidgetId, StringComparison.Ordinal))
            {
                continue;
            }

            fileSession.ClearItemSelection();
        }

        foreach (ContentWidgetWindow window in _contentWidgets.Values.Distinct())
        {
            if (window.CurrentContent is not FileSurfaceContent fileContent ||
                string.Equals(
                    fileContent.WidgetId,
                    activeWidgetId,
                    StringComparison.Ordinal))
            {
                continue;
            }

            fileContent.ClearItemSelection();
        }
    }

    private void RestoreLoadedWidgetBoundsAfterStartup()
    {
        IReadOnlyList<IDesktopWidgetWindow> windows =
            GetLoadedDesktopWindows();

        foreach (IDesktopWidgetWindow window in windows)
        {
            try
            {
                window.RestoreBoundsForCurrentTopology();
            }
            catch (Exception ex)
            {
                App.Log(
                    $"[WidgetManager] Startup bounds reconciliation failed " +
                    $"widget={window.Config.Id}: {ex}");
            }
        }
    }

    private async Task RestoreVisibleWidgetGroupsAsync()
    {
        foreach (WidgetGroupConfig group in _settingsService.Settings.WidgetGroups.ToList())
        {
            if (!group.IsVisible ||
                FindConfig(group.ActiveMemberId) is not { } config ||
                IsDeleted(config.Id) ||
                config.IsDisabled ||
                !_widgetRegistry.IsAvailableForSession(
                    config,
                    _settingsService.Settings))
            {
                continue;
            }

            // This runs both during initial restoration and during the deferred
            // bounds pass. Re-showing an already-visible group sends its surface
            // through the desktop-layer show path, which would undo the temporary
            // foreground raise applied after startup. Only re-show a group when
            // its native host has actually been hidden or lost.
            IDesktopWidgetWindow? existingWindow = GetLoadedWindow(group.ActiveMemberId);
            if (existingWindow is { Visible: true } &&
                existingWindow.WindowHandle != IntPtr.Zero &&
                Win32Helper.IsWindowVisible(existingWindow.WindowHandle))
            {
                existingWindow.RestoreBoundsForCurrentTopology();
                App.Log(
                    $"[WidgetGroup] Kept visible group surface during restore: " +
                    $"group={group.Id}, active={group.ActiveMemberId}, " +
                    $"hwnd=0x{existingWindow.WindowHandle.ToInt64():X}");
                continue;
            }

            try
            {
                await ShowGroupActiveWindowAsync(group);
            }
            catch (Exception ex)
            {
                App.Log(
                    $"[WidgetGroup] Visible group restore failed " +
                    $"group={group.Id} active={group.ActiveMemberId}: {ex}");
            }
        }
    }

    private void QueueDeferredStartupWidgetBoundsReconciliation()
    {
        App.UiDispatcherQueue?.TryEnqueue(async () =>
        {
            try
            {
                // Let RootElement.Loaded and the first composition/layout pass
                // complete before the final native bounds reconciliation.
                await Task.Yield();
                await Task.Delay(120);
                await RestoreVisibleWidgetGroupsAsync();
                RestoreLoadedWidgetBoundsAfterStartup();
            }
            catch (Exception ex)
            {
                App.Log($"[WidgetManager] Deferred startup bounds reconciliation failed: {ex}");
            }
        });
    }

    private void QueueVisibleGroupedFileIconRecoveryAfterStartup()
    {
        App.UiDispatcherQueue?.TryEnqueue(async () =>
        {
            try
            {
                // A persistent group surface can be shown without activation
                // while the startup layer handoff is still settling. In that
                // window, the initial asynchronous icon hydration can be
                // interrupted and leave every item on its fallback glyph.
                // Match the existing manual refresh once, but only for a
                // visible grouped file surface that still has no loaded icons.
                await Task.Delay(900);

                foreach (WidgetGroupConfig group in _settingsService.Settings.WidgetGroups.ToList())
                {
                    if (!group.IsVisible ||
                        FindConfig(group.ActiveMemberId) is not { WidgetKind: WidgetKind.File } config ||
                        IsDeleted(config.Id) ||
                        config.IsDisabled ||
                        !_widgetRegistry.IsAvailableForSession(
                            config,
                            _settingsService.Settings))
                    {
                        continue;
                    }

                    if (GetLoadedWindow(group.ActiveMemberId) is not ContentWidgetWindow window ||
                        !window.Visible ||
                        window.CurrentContent is not FileSurfaceContent fileSurface ||
                        !string.Equals(
                            fileSurface.WidgetId,
                            group.ActiveMemberId,
                            StringComparison.Ordinal))
                    {
                        continue;
                    }

                    int itemCount = fileSurface.ViewModel.Items.Count;
                    if (itemCount == 0 ||
                        fileSurface.ViewModel.Items.Any(item => item.Icon is not null))
                    {
                        continue;
                    }

                    await fileSurface.RefreshAsync();
                    App.Log(
                        $"[StartupIconRecovery] Refreshed visible grouped file surface " +
                        $"group={group.Id} widget={fileSurface.WidgetId} items={itemCount}");
                }
            }
            catch (Exception ex)
            {
                App.Log($"[WidgetManager] Startup grouped file icon recovery failed: {ex}");
            }
        });
    }

    private bool IsSessionCandidate(WidgetConfig widget)
    {
        return !widget.IsDisabled &&
               !IsDeleted(widget.Id) &&
               WidgetGroupSettings.IsActiveMember(_settingsService.Settings, widget.Id) &&
               _widgetRegistry.IsAvailableForSession(widget, _settingsService.Settings);
    }

    private void QueueTrayRaiseTopMostConfirmation(
        IReadOnlyList<IDesktopWidgetWindow> windows,
        long generation,
        TimeSpan delay)
    {
        App.UiDispatcherQueue.TryEnqueue(async () =>
        {
            await Task.Delay(delay);
            ConfirmTrayRaiseTopMost(windows, generation);
        });
    }

    private static bool CanCreateWidgetWindowOnCurrentThread()
    {
        return App.UiDispatcherQueue is not null;
    }

    /// <summary>
    /// Hide a widget if it is currently loaded.
    /// </summary>
    public bool HideWidget(string widgetId)
    {
        WidgetGroupConfig? group = WidgetGroupSettings.FindByMember(
            _settingsService.Settings,
            widgetId);
        if (group is not null)
        {
            _widgetGroupSwitchRequests.Cancel(group.SurfaceId);
        }

        if (_fileWidgets.TryGetValue(widgetId, out var fileSession))
        {
            fileSession.Host.HideWindow();
            SetWidgetGroupVisibility(fileSession.Host.Config, isVisible: false);
            return true;
        }

        if (_contentWidgets.TryGetValue(widgetId, out var contentWindow))
        {
            contentWindow.HideWindow();
            SetWidgetGroupVisibility(contentWindow.Config, isVisible: false);
            return true;
        }

        return false;
    }

    private void RestoreRaisedWidgetsToDesktopLayer(bool force)
    {
        if (!force &&
            (_isTogglingWidgetsDesktopLayer ||
             DateTime.UtcNow < _suppressTrayLayerRestoreUntilUtc))
        {
            App.LogVerbose($"[TrayBatch] RestoreDesktopLayer skipped force={force} reason=busy-or-suppressed");
            return;
        }

        App.Log(
            $"[TrayBatch] RestoreDesktopLayer force={force} file={_fileWidgets.Count} content={_contentWidgets.Count}");
        ClearTemporaryRaiseLease("raised-session-restored");
        SetWidgetsRaisedFromTray(false);
        _trayRaiseBatchGeneration++;
        StopTrayLayerRestoreMonitor();
        IReadOnlyList<IDesktopWidgetWindow> windows =
            GetWindowsInIdleHighestFirstOrder(
                GetLoadedDesktopWindows().Where(window => window.Visible));
        foreach (IDesktopWidgetWindow window in windows)
        {
            try
            {
                window.ForceRestoreDesktopLayerFromManager();
            }
            catch (Exception ex)
            {
                App.Log($"[WidgetManager] Failed to restore widget desktop layer {FormatHostWindow(window)}: {ex}");
            }
        }

        // Invalidate the defensive delayed normalizations queued by individual
        // hosts, then establish one shared external boundary for the group.
        _idlePeerOrderGeneration++;
        bool applied = WidgetLayerService.RestoreGroupPreservingForeground(
            windows.Select(window => window.WindowHandle).ToList(),
            "raised-session-restored");
        App.LogVerbose(
            $"[TrayBatch] RestoreDesktopLayer group-applied={applied} " +
            $"count={windows.Count} order={FormatIdlePeerOrder(windows)}");
    }

    /// <summary>
    /// Update the persisted position lock state for a widget.
    /// </summary>
    public bool SetWidgetPositionLocked(string widgetId, bool locked)
    {
        if (_fileWidgets.TryGetValue(widgetId, out var loadedEntry))
        {
            loadedEntry.ViewModel.SetPositionLocked(locked);
            SynchronizeGroupLayoutFromMember(loadedEntry.ViewModel.Config);
            return true;
        }

        var config = FindConfig(widgetId);
        if (config is null)
        {
            return false;
        }

        config.IsPositionLocked = locked;
        if (WidgetGroupSettings.FindByMember(_settingsService.Settings, widgetId) is { } group)
        {
            group.IsPositionLocked = locked;
            foreach (string memberId in group.MemberIds)
            {
                if (FindConfig(memberId) is { } member)
                {
                    member.IsPositionLocked = locked;
                }
            }
        }
        _settingsService.UpdateWidget(config);
        return true;
    }

    /// <summary>
    /// Update the persisted size lock state for a widget.
    /// </summary>
    public bool SetWidgetSizeLocked(string widgetId, bool locked)
    {
        if (_fileWidgets.TryGetValue(widgetId, out var loadedEntry))
        {
            loadedEntry.ViewModel.SetSizeLocked(locked);
            SynchronizeGroupLayoutFromMember(loadedEntry.ViewModel.Config);
            return true;
        }

        var config = FindConfig(widgetId);
        if (config is null)
        {
            return false;
        }

        config.IsSizeLocked = locked;
        if (WidgetGroupSettings.FindByMember(_settingsService.Settings, widgetId) is { } group)
        {
            group.IsSizeLocked = locked;
            foreach (string memberId in group.MemberIds)
            {
                if (FindConfig(memberId) is { } member)
                {
                    member.IsSizeLocked = locked;
                }
            }
        }
        _settingsService.UpdateWidget(config);
        return true;
    }

    /// <summary>
    /// Toggle visibility across all file widgets.
    /// </summary>
    public async Task ToggleAllWidgetsAsync()
    {
        bool anyVisible = _settingsService.Settings.Widgets.Any(widget =>
            widget.IsVisible &&
            IsSessionCandidate(widget));

        await SetAllWidgetsVisibleAsync(!anyVisible);
    }

    /// <summary>
    /// Close all widget windows for shutdown.
    /// </summary>
    public void CloseAll()
    {
        CancelAllWidgetSurfaceSwitches();
        StopTrayLayerRestoreMonitor();
        DisposeWidgetDetachPlacementPreview();
        _settingsService.SettingsChanged -= OnSettingsChanged;
        _settingsService.AppearancePreviewChanged -= ApplyAppearancePreview;
        _themeService.AppearanceChanged -= ApplyAppearancePreview;

        // CloseAll is process teardown, not a user visibility command. Suppress
        // every Closed handler so shutdown cannot turn visible widgets into a
        // persisted hidden state just before the final settings flush.
        foreach (WidgetConfig widget in _settingsService.Settings.Widgets)
        {
            _suppressClosedVisibilityPersistence.Add(widget.Id);
        }

        _fileWidgets.Clear();

        foreach (ContentWidgetWindow window in _contentWidgets.Values
                     .DistinctBy(candidate => candidate.WindowHandle)
                     .ToList())
        {
            try
            {
                if (window.CurrentContent is IDisposable disposableContent)
                {
                    disposableContent.Dispose();
                }
            }
            catch
            {
            }
            try
            {
                window.Close();
            }
            catch
            {
            }
        }

        _contentWidgets.Clear();
        _widgetWindowHandles.Clear();
        _widgetSurfaces.Clear();
        _widgetSurfaceSwitchGates.Clear();
        _sessionManager.MarkHidden("close-all");
    }

    public int GetDefaultManagedStorageWidgetCount()
    {
        return _settingsService.Settings.Widgets.Count(widget =>
            widget.WidgetKind == WidgetKind.File &&
            widget.FollowsDefaultStoragePath &&
            !IsDeleted(widget.Id));
    }

    public async Task RefreshFileWidgetAsync(string widgetId)
    {
        if (!HasUiThreadAccess())
        {
            await RunOnUiThreadAsync(() => RefreshFileWidgetAsync(widgetId));
            return;
        }

        if (_fileWidgets.TryGetValue(widgetId, out var fileEntry))
        {
            await fileEntry.ViewModel.RefreshFromConfigAsync();
            return;
        }

        ContentWidgetWindow? contentWindow = _contentWidgets.Values
            .Distinct()
            .FirstOrDefault(window =>
                window.CurrentContent is FileSurfaceContent surface &&
                string.Equals(surface.WidgetId, widgetId, StringComparison.Ordinal));
        if (contentWindow?.CurrentContent is FileSurfaceContent fileSurface)
        {
            await fileSurface.ViewModel.RefreshFromConfigAsync();
        }
    }

    public void SetDesktopOrganizationBusy(
        IEnumerable<string> widgetIds,
        bool isBusy)
    {
        if (!HasUiThreadAccess())
        {
            App.UiDispatcherQueue?.TryEnqueue(() =>
                SetDesktopOrganizationBusy(widgetIds.ToArray(), isBusy));
            return;
        }

        foreach (string widgetId in widgetIds.Distinct(StringComparer.Ordinal))
        {
            if (_fileWidgets.TryGetValue(widgetId, out var fileEntry))
            {
                fileEntry.SetDesktopOrganizationBusy(isBusy);
                continue;
            }

            ContentWidgetWindow? contentWindow = _contentWidgets.Values
                .Distinct()
                .FirstOrDefault(window =>
                    window.CurrentContent is FileSurfaceContent surface &&
                    string.Equals(surface.WidgetId, widgetId, StringComparison.Ordinal));
            if (contentWindow?.CurrentContent is FileSurfaceContent fileSurface)
            {
                fileSurface.SetDesktopOrganizationBusy(isBusy);
            }
        }
    }

    private WidgetConfig? FindConfig(string widgetId)
    {
        return _settingsService.Settings.Widgets.FirstOrDefault(widget => widget.Id == widgetId);
    }

    private void SyncMappedWidgetShortcut(WidgetConfig config, string? displayNameOverride = null)
    {
        if (config.FollowsDefaultStoragePath ||
            string.IsNullOrWhiteSpace(config.MappedFolderPath))
        {
            RemoveMappedWidgetShortcut(config);
            return;
        }

        try
        {
            string rootPath = GetManagedStorageRootPath();
            Directory.CreateDirectory(rootPath);

            string targetPath = Path.GetFullPath(config.MappedFolderPath);
            string shortcutPath = GetExistingMappedWidgetShortcutPath(config, rootPath);
            string desiredShortcutPath = BuildAvailableMappedShortcutPath(
                displayNameOverride ?? config.Name,
                config.Id,
                rootPath,
                shortcutPath);

            if (!string.Equals(shortcutPath, desiredShortcutPath, StringComparison.OrdinalIgnoreCase))
            {
                DeleteMappedWidgetShortcut(shortcutPath, config.Id);
                shortcutPath = desiredShortcutPath;
            }

            ShortcutHelper.CreateOrUpdateFolderShortcut(
                shortcutPath,
                targetPath,
                BuildMappedWidgetShortcutDescription(config.Id));
        }
        catch (Exception ex)
        {
            App.Log($"[MappedShortcut] Failed to sync shortcut for widget '{config.Id}': {ex}");
        }
    }

    private bool IsDeleted(string widgetId)
    {
        return _deletedWidgetIds.Contains(widgetId) ||
               _settingsService.Settings.DeletedWidgetIds.Contains(widgetId);
    }

    private (double Width, double Height) GetDefaultFeatureWidgetSize(WidgetKind kind)
    {
        return kind switch
        {
            WidgetKind.Todo => (
                Math.Max(_settingsService.Settings.DefaultWidgetWidth, 320),
                Math.Max(_settingsService.Settings.DefaultWidgetHeight, 420)),
            WidgetKind.Music => (380, 190),
            WidgetKind.Weather => (200, 200),
            WidgetKind.Glance => (360, 260),
            _ => (
                _settingsService.Settings.DefaultWidgetWidth,
                _settingsService.Settings.DefaultWidgetHeight)
        };
    }

    private Task SetContentFeatureWidgetEnabledAsync(bool enabled, bool reveal)
    {
        return SetContentFeatureWidgetEnabledAsync(WidgetKind.Music, enabled, reveal);
    }

    private Task<IDesktopWidgetWindow> CreateWidgetFromConfigAsync(
        WidgetConfig config,
        bool keepPreparedForAnimation = false,
        bool revealAfterCreate = false,
        bool showRaisedWhileInitializing = false)
    {
        if (config.WidgetKind != WidgetKind.File)
        {
            return Task.FromException<IDesktopWidgetWindow>(
                new InvalidOperationException(
                    $"File widget window creation requires a File config. " +
                    $"Actual kind: {config.WidgetKind}."));
        }

        return CreateRegisteredWidgetFromConfigAsync(
            config,
            keepPreparedForAnimation,
            revealAfterCreate,
            showRaisedWhileInitializing,
            CancellationToken.None);
    }

    private async Task<IDesktopWidgetWindow> CreateRegisteredWidgetFromConfigAsync(
        WidgetConfig config,
        bool keepPreparedForAnimation = false,
        bool revealAfterCreate = false,
        bool showRaisedWhileInitializing = false,
        CancellationToken cancellationToken = default)
    {
        WidgetGroupConfig? group = WidgetGroupSettings.FindByMember(
            _settingsService.Settings,
            config.Id);
        if (group is not null &&
            config.WidgetKind is WidgetKind.File or
                WidgetKind.QuickCapture)
        {
            return await CreateContentWidgetFromConfigAsync(
                config, keepPreparedForAnimation, revealAfterCreate,
                showRaisedWhileInitializing, cancellationToken);
        }

        if (!_windowProviders.TryGetValue(config.WidgetKind, out var provider))
        {
            throw new NotSupportedException($"Widget kind '{config.WidgetKind}' is not registered as creatable.");
        }

        return await provider.CreateWindowAsync(new WidgetWindowCreationRequest(
            config,
            keepPreparedForAnimation,
            revealAfterCreate,
            showRaisedWhileInitializing,
            cancellationToken));
    }

    private async Task<ContentWidgetWindow> CreateContentWidgetFromConfigAsync(
        WidgetConfig config,
        bool keepPreparedForAnimation = false,
        bool revealAfterCreate = false,
        bool showRaisedWhileInitializing = false,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!HasUiThreadAccess())
        {
            return await RunOnUiThreadAsync(() => CreateContentWidgetFromConfigAsync(
                config,
                keepPreparedForAnimation,
                revealAfterCreate,
                showRaisedWhileInitializing,
                cancellationToken));
        }

        if (_contentWidgets.TryGetValue(config.Id, out var existing))
        {
            if (!showRaisedWhileInitializing)
            {
                await existing.ContentReadyTask.WaitAsync(cancellationToken);
                RestoreWidgetGroupTransientState(config.Id);
            }

            RegisterStandaloneUnifiedFileSessionIfNeeded(
                config,
                existing,
                existing.CurrentContent);

            return existing;
        }

        ContentWidgetWindowFactory factory = CreateSurfaceContentWindowFactory();
        if (!factory.CanCreateContentWindow(config.WidgetKind))
        {
            throw new NotSupportedException(
                $"Widget kind '{config.WidgetKind}' does not support content window creation.");
        }

        if (!_widgetRegistry.IsAvailableForSession(config, _settingsService.Settings))
        {
            throw new InvalidOperationException($"Widget kind '{config.WidgetKind}' is disabled for the current session.");
        }

        config.IsDisabled = false;
        NormalizeWidgetBounds(config);

        ContentWidgetWindowPlan plan = factory.CreateContentWindowPlan(config);
        var window = factory.CreateContentWindow(plan);
        _themeService.TrackWindow(window);
        _contentWidgets[config.Id] = window;
        RegisterStandaloneUnifiedFileSessionIfNeeded(
            config,
            window,
            plan.Content);
        RegisterCreatedSurfaceHost(config, window);
        _widgetWindowHandles.Add(window.WindowHandle);
        ApplyCapsuleArrangementIfChanged(force: true);

        window.Closed += (_, _) =>
        {
            List<string> registeredIds = _contentWidgets
                .Where(entry => ReferenceEquals(entry.Value, window))
                .Select(entry => entry.Key)
                .ToList();
            foreach (string registeredId in registeredIds)
            {
                _contentWidgets.Remove(registeredId);
            }

            RemoveFileWidgetSessionsForHost(window);

            UnregisterSurfaceHost(window);
            _widgetWindowHandles.Remove(window.WindowHandle);
            WidgetConfig closedConfig = window.Config;
            if (IsDeleted(closedConfig.Id) || FindConfig(closedConfig.Id) is null)
            {
                return;
            }

            if (_suppressClosedVisibilityPersistence.Contains(closedConfig.Id) ||
                registeredIds.Any(_suppressClosedVisibilityPersistence.Contains))
            {
                return;
            }

            if (_contentWidgets.Values.Any(candidate => ReferenceEquals(candidate, window)))
            {
                return;
            }

            closedConfig.IsVisible = false;
            SetWidgetGroupVisibility(closedConfig, isVisible: false);
            _settingsService.SaveDebounced();
        };

        try
        {
            if (keepPreparedForAnimation && showRaisedWhileInitializing)
            {
                window.PrepareTrayShowAnimation();
                QueueDeferredContentInitialization(config, window);
                return window;
            }

            await window.ContentReadyTask.WaitAsync(cancellationToken);
            RestoreWidgetGroupTransientState(config.Id);
            window.PrepareTrayShowAnimation();
            if (revealAfterCreate)
            {
                // A newly-created window has exactly one first-presentation
                // path. Showing it at the desktop layer first and immediately
                // raising it detached/re-attached the Explorer owner while the
                // HWND was already visible, which could flash the whole desktop.
                window.ShowPreparedRaisedFromTray();
                window.PlayTrayShowAnimation();
            }
            else if (!keepPreparedForAnimation)
            {
                window.ShowPreparedAtDesktopLayer();
                window.CompleteTrayShowWithoutAnimation();
            }
        }
        catch
        {
            _contentWidgets.Remove(config.Id);
            RemoveFileWidgetSessionsForHost(window);
            _widgetWindowHandles.Remove(window.WindowHandle);
            UnregisterSurfaceHost(window);
            CloseFailedCreatedWindow(
                config.Id,
                window,
                preserveVisibility: cancellationToken.CanBeCanceled);
            throw;
        }

        return window;
    }

    private void RegisterStandaloneUnifiedFileSessionIfNeeded(
        WidgetConfig config,
        ContentWidgetWindow window,
        DeskBox.Contracts.IWidgetContent? content)
    {
        if (config.WidgetKind != WidgetKind.File ||
            WidgetGroupSettings.FindByMember(
                _settingsService.Settings,
                config.Id) is not null ||
            content is not FileSurfaceContent fileSurface)
        {
            return;
        }

        if (_fileWidgets.TryGetValue(config.Id, out var existing) &&
            ReferenceEquals(existing.Host, window))
        {
            return;
        }

        var session = new FileWidgetSession(window, fileSurface);
        _fileWidgets[config.Id] = session;
        _fileWidgetHostDiagnostics.RecordUnifiedCreation();
        App.LogVerbose(
            $"[WidgetManager] Registered unified standalone file host " +
            $"widget={config.Id} hwnd=0x{window.WindowHandle.ToInt64():X}");
    }

    private List<string> RemoveFileWidgetSessionsForHost(
        IDesktopWidgetWindow window)
    {
        List<string> registeredIds = _fileWidgets
            .Where(entry => ReferenceEquals(entry.Value.Host, window))
            .Select(entry => entry.Key)
            .ToList();
        foreach (string registeredId in registeredIds)
        {
            _fileWidgets.Remove(registeredId);
        }

        return registeredIds;
    }

    private void CloseFailedCreatedWindow(
        string widgetId,
        IDesktopWidgetWindow window,
        bool preserveVisibility)
    {
        if (preserveVisibility)
        {
            _suppressClosedVisibilityPersistence.Add(widgetId);
        }

        try
        {
            window.CloseWindow();
        }
        catch
        {
        }
        finally
        {
            if (preserveVisibility)
            {
                _suppressClosedVisibilityPersistence.Remove(widgetId);
            }
        }
    }

    private void QueueDeferredContentInitialization(
        WidgetConfig config,
        ContentWidgetWindow window)
    {
        App.UiDispatcherQueue.TryEnqueue(async () =>
        {
            await Task.Yield();
            try
            {
                await window.ContentReadyTask;
                RestoreWidgetGroupTransientState(config.Id);
            }
            catch (Exception ex)
            {
                App.Log(
                    $"[WidgetManager] Failed to initialize content widget " +
                    $"'{config.Name}' ({config.Id}) after show: {ex}");
                if (_contentWidgets.TryGetValue(config.Id, out var currentWindow) &&
                    ReferenceEquals(currentWindow, window))
                {
                    _contentWidgets.Remove(config.Id);
                    RemoveFileWidgetSessionsForHost(window);
                    _widgetWindowHandles.Remove(window.WindowHandle);
                    try
                    {
                        window.Close();
                    }
                    catch
                    {
                    }
                }
            }
        });
    }


    private void NormalizeWidgetBounds(WidgetConfig config)
    {
        int width = (int)Math.Round(Math.Max(SettingsService.MinWidgetWidth, config.Width));
        int height = (int)Math.Round(Math.Max(SettingsService.MinWidgetHeight, config.Height));
        int x = (int)Math.Round(config.X);
        int y = (int)Math.Round(config.Y);
        double previousX = config.X;
        double previousY = config.Y;
        double previousWidth = config.Width;
        double previousHeight = config.Height;
        string? previousAnchor = config.PositionAnchor;
        double previousMarginX = config.PositionMarginX;
        double previousMarginY = config.PositionMarginY;
        string? previousMonitorKey = config.PositionMonitorKey;
        string? previousMonitorDeviceName = config.PositionMonitorDeviceName;
        bool? previousMonitorWasPrimary = config.PositionMonitorWasPrimary;
        int previousBoundsCoordinateVersion = config.BoundsCoordinateVersion;

        var area = DisplayArea.GetFromRect(
            new Windows.Graphics.RectInt32(x, y, width, height),
            DisplayAreaFallback.Nearest);
        var workArea = area.WorkArea;
        WidgetPositioningService.EnsureCurrentBoundsCoordinateVersionForCurrentTopology(config, workArea);

        var safeBounds = WidgetPositioningService.ResolveBoundsForCurrentTopology(config);
        var selectedWorkArea = DisplayArea.GetFromRect(safeBounds, DisplayAreaFallback.Nearest).WorkArea;
        bool shouldCaptureAnchor = string.IsNullOrWhiteSpace(config.PositionAnchor) ||
                                   string.IsNullOrWhiteSpace(config.PositionMonitorKey) ||
                                   string.IsNullOrWhiteSpace(config.PositionMonitorDeviceName) ||
                                   !config.PositionMonitorWasPrimary.HasValue ||
                                   config.PositionMonitorWasPrimary == true ||
                                   string.Equals(
                                       config.PositionMonitorKey,
                                       WidgetPositioningService.CreateMonitorKey(selectedWorkArea),
                                       StringComparison.Ordinal);
        if (shouldCaptureAnchor)
        {
            WidgetPositioningService.CaptureAnchor(config, safeBounds, selectedWorkArea);
        }

        WidgetPositioningService.UpdateConfigFromPhysicalBounds(config, safeBounds, selectedWorkArea);

        bool changed =
            Math.Abs(config.Width - previousWidth) > double.Epsilon ||
            Math.Abs(config.Height - previousHeight) > double.Epsilon ||
            Math.Abs(config.X - previousX) > double.Epsilon ||
            Math.Abs(config.Y - previousY) > double.Epsilon ||
            previousBoundsCoordinateVersion != config.BoundsCoordinateVersion ||
            !string.Equals(config.PositionAnchor, previousAnchor, StringComparison.Ordinal) ||
            Math.Abs(config.PositionMarginX - previousMarginX) > double.Epsilon ||
            Math.Abs(config.PositionMarginY - previousMarginY) > double.Epsilon ||
            !string.Equals(config.PositionMonitorKey, previousMonitorKey, StringComparison.Ordinal) ||
            !string.Equals(config.PositionMonitorDeviceName, previousMonitorDeviceName, StringComparison.OrdinalIgnoreCase) ||
            config.PositionMonitorWasPrimary != previousMonitorWasPrimary;

        if (!changed)
        {
            return;
        }

        _settingsService.UpdateWidget(config, notifySubscribers: false);
    }

}
