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
    bool ShowRaisedWhileInitializing);

internal sealed record WidgetWindowProvider(
    WidgetKind WidgetKind,
    Func<WidgetWindowCreationRequest, Task<IDesktopWidgetWindow>> CreateWindowAsync);

internal interface IDesktopWidgetWindow
{
    WidgetWindowIdentity Identity { get; }
    WidgetConfig Config { get; }
    IntPtr WindowHandle { get; }
    bool Visible { get; }
    bool IsCompactArrangementActive { get; }
    Windows.Foundation.Rect AnimationBounds { get; }
    Windows.Foundation.Rect RestingAnimationBounds { get; }
    void ApplyAppearancePreview();
    void RestoreBoundsForCurrentTopology();
    void ApplyCompactArrangement(Windows.Graphics.RectInt32 bounds, bool constrainSize);
    void PreviewCompactArrangement(Windows.Graphics.RectInt32 bounds);
    void SetTrayAnimationOffsetOverride(double? offsetX, double? offsetY);
    void PrepareTrayShowAnimation();
    void ShowPreparedAtDesktopLayer(bool persistVisibility = true);
    void ShowPreparedRaisedFromTray(bool persistVisibility = true);
    void PlayTrayShowAnimation();
    void CompleteTrayShowWithoutAnimation();
    bool PrepareTrayHideAnimation(bool persistVisibility = true);
    void PlayPreparedTrayHideAnimation();
    WidgetTrayBatchAnimationEntry? BeginSharedTrayShowAnimation();
    WidgetTrayBatchAnimationEntry? BeginSharedTrayHideAnimation();
    void ActivateRaisedFromTrayBatch();
    void EnsureRaisedFromTrayTopMost();
    void ForceRestoreDesktopLayerFromManager();
    void RestoreDesktopLayerFromManager();
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
    private readonly Dictionary<string, (WidgetWindow Window, WidgetViewModel ViewModel)> _widgets = new();
    private readonly Dictionary<string, (QuickCaptureWidgetWindow Window, QuickCaptureWidgetViewModel ViewModel)> _quickCaptureWidgets = new();
    private readonly Dictionary<string, ContentWidgetWindow> _contentWidgets = new();
    private readonly HashSet<IntPtr> _widgetWindowHandles = new();
    private readonly HashSet<string> _deletedWidgetIds = [];
    private readonly HashSet<string> _suppressClosedVisibilityPersistence = [];
    private readonly SemaphoreSlim _widgetRenameGate = new(1, 1);

    public IReadOnlyDictionary<string, (WidgetWindow Window, WidgetViewModel ViewModel)> Widgets => _widgets;
    public IReadOnlyDictionary<string, (QuickCaptureWidgetWindow Window, QuickCaptureWidgetViewModel ViewModel)> QuickCaptureWidgets => _quickCaptureWidgets;
    internal IReadOnlyDictionary<string, ContentWidgetWindow> ContentWidgets => _contentWidgets;

    public bool WidgetsRaisedFromTray => _widgetsRaisedFromTray;
    public WidgetSessionState SessionState => _sessionManager.State;
    public bool IsWidgetInteractionActive => _sessionManager.IsInteractionActive;

    public bool HasVisibleWidgets => _widgets.Values.Any(entry => entry.Window.Visible) ||
                                     _quickCaptureWidgets.Values.Any(entry => entry.Window.Visible) ||
                                     _contentWidgets.Values.Any(window => window.Visible);

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
        foreach (var entry in _widgets.Values)
        {
            if (entry.Window.WindowHandle == hwnd)
            {
                return entry.Window.Content as FrameworkElement;
            }
        }

        foreach (var entry in _quickCaptureWidgets.Values)
        {
            if (entry.Window.WindowHandle == hwnd)
            {
                return entry.Window.Content as FrameworkElement;
            }
        }

        foreach (var window in _contentWidgets.Values)
        {
            if (window.WindowHandle == hwnd)
            {
                return window.Content as FrameworkElement;
            }
        }

        return null;
    }

    private IReadOnlyList<IDesktopWidgetWindow> GetLoadedDesktopWindows()
    {
        var windows = new List<IDesktopWidgetWindow>(
            _widgets.Count + _quickCaptureWidgets.Count + _contentWidgets.Count);
        windows.AddRange(_widgets.Values.Select(entry => (IDesktopWidgetWindow)entry.Window));
        windows.AddRange(_quickCaptureWidgets.Values.Select(entry => (IDesktopWidgetWindow)entry.Window));
        windows.AddRange(_contentWidgets.Values.Select(window => (IDesktopWidgetWindow)window));
        return windows;
    }

    public void BeginWidgetInteraction(string reason)
    {
        _sessionManager.BeginInteraction(reason);
    }

    public void EndWidgetInteraction(string reason)
    {
        _sessionManager.EndInteraction(reason);
    }

    public event Action<WidgetWindow>? WidgetCreated;
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
        if (_widgetsRaisedFromTray)
        {
            App.LogVerbose("[TrayBatch] ToggleDecision=hide reason=raised-session");
            return true;
        }

        if (!HasVisibleWidgets)
        {
            App.LogVerbose("[TrayBatch] ToggleDecision=raise reason=no-visible-windows");
            return false;
        }

        IntPtr foregroundWindow = Win32Helper.GetForegroundWindow();
        if (IsDeskBoxForegroundWindow(foregroundWindow) ||
            IsDesktopShellWindow(foregroundWindow) ||
            IsTaskbarWindow(foregroundWindow))
        {
            App.LogVerbose(
                $"[TrayBatch] ToggleDecision=hide reason=foreground-local hwnd=0x{foregroundWindow.ToInt64():X}");
            return true;
        }

        App.LogVerbose(
            $"[TrayBatch] ToggleDecision=raise reason=visible-widgets-behind hwnd=0x{foregroundWindow.ToInt64():X}");
        return false;
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
        InitializeCapsuleArrangementState();
        _featureWidgetHandlers = CreateFeatureWidgetHandlers();
        _windowProviders = CreateWindowProviders();
        foreach (var kind in FeatureWidgetSettings.FeatureKinds)
        {
            _lastFeatureWidgetEnabledStates[kind] = FeatureWidgetSettings.IsEnabled(_settingsService.Settings, kind);
        }
        _lastWidgetLayerMode = SettingsService.NormalizeWidgetLayerModeSetting(_settingsService.Settings.WidgetLayerMode);
        _settingsService.SettingsChanged += OnSettingsChanged;
        _settingsService.AppearancePreviewChanged += ApplyAppearancePreview;
        _themeService.AppearanceChanged += ApplyAppearancePreview;
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
                () => HideAndCloseFeatureWidgetAsync(WidgetKind.Search))
        ];

        return handlers.ToDictionary(handler => handler.WidgetKind);
    }

    private Dictionary<WidgetKind, WidgetWindowProvider> CreateWindowProviders()
    {
        WidgetWindowProvider[] providers =
        [
            new(
                WidgetKind.File,
                async request => await CreateWidgetFromConfigAsync(
                    request.Config,
                    request.KeepPreparedForAnimation,
                    request.RevealAfterCreate,
                    request.ShowRaisedWhileInitializing)),
            new(
                WidgetKind.QuickCapture,
                async request => await CreateQuickCaptureWidgetFromConfigAsync(
                    request.Config,
                    request.KeepPreparedForAnimation,
                    request.RevealAfterCreate,
                    request.ShowRaisedWhileInitializing)),
            new(
                WidgetKind.Todo,
                async request => await CreateContentWidgetFromConfigAsync(
                    request.Config,
                    request.KeepPreparedForAnimation,
                    request.RevealAfterCreate,
                    request.ShowRaisedWhileInitializing)),
            new(
                WidgetKind.Music,
                async request => await CreateContentWidgetFromConfigAsync(
                    request.Config,
                    request.KeepPreparedForAnimation,
                    request.RevealAfterCreate,
                    request.ShowRaisedWhileInitializing)),
            new(
                WidgetKind.Weather,
                async request => await CreateContentWidgetFromConfigAsync(
                    request.Config,
                    request.KeepPreparedForAnimation,
                    request.RevealAfterCreate,
                    request.ShowRaisedWhileInitializing)),
            new(
                WidgetKind.Search,
                async request => await CreateContentWidgetFromConfigAsync(
                    request.Config,
                    request.KeepPreparedForAnimation,
                    request.RevealAfterCreate,
                    request.ShowRaisedWhileInitializing))
        ];

        return providers.ToDictionary(provider => provider.WidgetKind);
    }

    private void OnSettingsChanged()
    {
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
            foreach (var (_, (window, _)) in _widgets.ToList())
            {
                window.ApplyAppearancePreview();
            }

            foreach (var (_, (window, _)) in _quickCaptureWidgets.ToList())
            {
                window.ApplyAppearancePreview();
            }

            foreach (var (_, window) in _contentWidgets.ToList())
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
    /// Restore all visible file widgets from saved configuration.
    /// </summary>
    public async Task RestoreWidgetsAsync()
    {
        RepairLegacyContentFeatureFileShells();

        // Dedup feature widgets: each kind should only have one config
        DeduplicateFeatureWidgets();

        var visibleConfigs = _settingsService.Settings.Widgets.Where(widget =>
                widget.IsVisible &&
                !widget.IsDisabled &&
                !IsDeleted(widget.Id))
            .ToList();

        foreach (var unsupportedConfig in visibleConfigs.Where(widget => !_widgetRegistry.CanCreateWindow(widget.WidgetKind)))
        {
            string reason = _widgetRegistry.IsKnown(unsupportedConfig.WidgetKind)
                ? "not-implemented-yet"
                : "unknown-kind";
            App.Log($"[WidgetManager] Skipping widget restore reason={reason} widget={FormatWidget(unsupportedConfig)}");
        }

        var configs = visibleConfigs.Where(widget =>
                _widgetRegistry.IsAvailableForSession(widget, _settingsService.Settings))
            .ToList();

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

        if (configs.Count > 0)
        {
            _sessionManager.MarkDesktopResting("restore-widgets");
        }
    }

    /// <summary>
    /// Create a new widget backed by the default managed storage root.
    /// </summary>
    public async Task<WidgetWindow> CreateManagedWidgetAsync(string? name = null)
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

        _settingsService.Settings.Widgets.Add(config);
        await _settingsService.SaveAsync();

        return await CreateWidgetFromConfigAsync(config, revealAfterCreate: true);
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
    public async Task<WidgetWindow> CreateFolderWidgetAsync(string folderPath)
    {
        var folderName = Path.GetFileName(folderPath);
        var config = new WidgetConfig
        {
            Name = folderName,
            WidgetKind = WidgetKind.File,
            MappedFolderPath = folderPath,
            BoundsCoordinateVersion = WidgetConfig.CurrentBoundsCoordinateVersion,
            Width = _settingsService.Settings.DefaultWidgetWidth,
            Height = _settingsService.Settings.DefaultWidgetHeight
        };

        _settingsService.Settings.Widgets.Add(config);
        SyncMappedWidgetShortcut(config);
        await _settingsService.SaveAsync();

        return await CreateWidgetFromConfigAsync(config, revealAfterCreate: true);
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

        if (config.WidgetKind == WidgetKind.QuickCapture)
        {
            if (_quickCaptureWidgets.TryGetValue(widgetId, out var quickCaptureEntry))
            {
                quickCaptureEntry.Window.RestoreBoundsForCurrentTopology();
                if (reveal)
                {
                    quickCaptureEntry.Window.RevealFromTray(autoRestoreOnReveal);
                }
                else
                {
                    quickCaptureEntry.Window.PrepareTrayShowAnimation();
                    quickCaptureEntry.Window.ShowPreparedAtDesktopLayer();
                    quickCaptureEntry.Window.CompleteTrayShowWithoutAnimation();
                }

                return true;
            }

            var quickCaptureWindow = (QuickCaptureWidgetWindow)await CreateRegisteredWidgetFromConfigAsync(
                config,
                keepPreparedForAnimation: !reveal);
            quickCaptureWindow.RestoreBoundsForCurrentTopology();
            if (reveal)
            {
                quickCaptureWindow.RevealFromTray(autoRestoreOnReveal);
            }
            else
            {
                quickCaptureWindow.PrepareTrayShowAnimation();
                quickCaptureWindow.ShowPreparedAtDesktopLayer();
                quickCaptureWindow.CompleteTrayShowWithoutAnimation();
            }

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

        if (_widgets.TryGetValue(widgetId, out var entry))
        {
            entry.Window.RestoreBoundsForCurrentTopology();
            if (reveal)
            {
                entry.Window.RevealFromTray(autoRestoreOnReveal);
            }
            else
            {
                entry.Window.PrepareTrayShowAnimation();
                entry.Window.ShowPreparedAtDesktopLayer();
                entry.Window.CompleteTrayShowWithoutAnimation();
            }

            return true;
        }

        var window = await CreateWidgetFromConfigAsync(config, keepPreparedForAnimation: !reveal);
        window.RestoreBoundsForCurrentTopology();
        if (reveal)
        {
            window.RevealFromTray(autoRestoreOnReveal);
        }
        else
        {
            window.PrepareTrayShowAnimation();
            window.ShowPreparedAtDesktopLayer();
            window.CompleteTrayShowWithoutAnimation();
        }

        return true;
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
    public async Task SetAllWidgetsVisibleAsync(bool visible)
    {
        using var perfScope = PerformanceLogger.Measure("WidgetManager.SetAllWidgetsVisible", $"visible={visible}");
        App.LogVerbose(
            $"[TrayBatch] SetAllVisible requested visible={visible} raised={_widgetsRaisedFromTray} " +
            $"loadedFile={_widgets.Count} loadedQuick={_quickCaptureWidgets.Count} loadedContent={_contentWidgets.Count}");
        _trayBatchAnimationDriver.Cancel();
        if (visible)
        {
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
            SaveBatchVisibilityState();
            App.LogVerbose($"[TrayBatch] SetAllVisible completed visible=true prepared={windowsToShow.Count} shown={shownWindows.Count}");
            return;
        }

        var hideCandidates = GetLoadedDesktopWindows()
            .Where(window => window.Visible)
            .ToList();
        
        // ⭐ 终极优化：将整个 Prepare + Play 流程都放在 TryEnqueue 中
        var dispatcher = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        dispatcher.TryEnqueue(() =>
        {
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
            PlayPreparedTrayHideAnimations(windowsToHide);

            SetWidgetsRaisedFromTray(false);
            _sessionManager.MarkHidden("set-all-hidden");
            _trayRaiseBatchGeneration++;
            StopTrayLayerRestoreMonitor();
            SaveBatchVisibilityState();
            App.LogVerbose($"[TrayBatch] SetAllVisible completed visible=false prepared={windowsToHide.Count}");
        });

        return;
    }

    /// <summary>
    /// Restores all loaded widget windows to their correct positions for
    /// the current display topology.  Called when displays are added,
    /// removed, or reconfigured (hot-plug, resolution change, DPI change).
    /// </summary>
    public async Task RestoreWidgetPositionsAsync()
    {
        using var perfScope = PerformanceLogger.Measure("WidgetManager.RestoreWidgetPositions");
        App.Log("[WidgetManager] Restoring widget positions for current display topology");

        foreach (var entry in _widgets.Values.ToList())
        {
            try
            {
                entry.Window.RestoreBoundsForCurrentTopology();
            }
            catch (Exception ex)
            {
                App.Log($"[WidgetManager] Failed to restore position for widget '{entry.Window.Identity.WidgetId}': {ex.Message}");
            }
        }

        foreach (var window in _contentWidgets.Values.ToList())
        {
            try
            {
                window.RestoreBoundsForCurrentTopology();
            }
            catch (Exception ex)
            {
                App.Log($"[WidgetManager] Failed to restore position for content widget: {ex.Message}");
            }
        }

        foreach (var entry in _quickCaptureWidgets.Values.ToList())
        {
            try
            {
                entry.Window.RestoreBoundsForCurrentTopology();
            }
            catch (Exception ex)
            {
                App.Log($"[WidgetManager] Failed to restore position for quick capture widget: {ex.Message}");
            }
        }

        await Task.Yield();
    }

    /// <summary>
    /// Remove a widget and close its window.
    /// </summary>
    public async Task RemoveWidgetAsync(string widgetId, WidgetRemovalAction removalAction = WidgetRemovalAction.RemoveWidgetOnly)
    {
        var config = FindConfig(widgetId);
        _deletedWidgetIds.Add(widgetId);

        if (_widgets.TryGetValue(widgetId, out var entry))
        {
            App.Log($"[WidgetManager] Retiring widget window for delete: {widgetId}");
            entry.ViewModel.Dispose();
            _widgets.Remove(widgetId);
            _widgetWindowHandles.Remove(entry.Window.WindowHandle);
            try { entry.Window.HideWindow(); } catch (Exception ex) { App.Log($"[WidgetManager] HideWindow failed during delete: {ex.Message}"); }
            try { entry.Window.Close(); } catch (Exception ex) { App.Log($"[WidgetManager] Close failed during delete: {ex.Message}"); }
        }

        if (_quickCaptureWidgets.TryGetValue(widgetId, out var quickCaptureEntry))
        {
            App.Log($"[WidgetManager] Retiring quick capture widget window for delete: {widgetId}");
            quickCaptureEntry.ViewModel.Dispose();
            _quickCaptureWidgets.Remove(widgetId);
            _widgetWindowHandles.Remove(quickCaptureEntry.Window.WindowHandle);
            try { quickCaptureEntry.Window.HideWindow(); } catch (Exception ex) { App.Log($"[WidgetManager] HideWindow failed during delete: {ex.Message}"); }
            try { quickCaptureEntry.Window.Close(); } catch (Exception ex) { App.Log($"[WidgetManager] Close failed during delete: {ex.Message}"); }
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
        if (config is not null && FeatureWidgetSettings.IsFeatureWidget(config.WidgetKind))
        {
            SetFeatureWidgetEnabledState(config.WidgetKind, false);
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
        foreach (var (widgetId, (window, _)) in _widgets.ToList())
        {
            if (string.Equals(widgetId, activeWidgetId, StringComparison.Ordinal))
            {
                continue;
            }

            window.ClearItemSelection();
        }
    }

    private bool IsSessionCandidate(WidgetConfig widget)
    {
        return !widget.IsDisabled &&
               !IsDeleted(widget.Id) &&
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
        if (_widgets.TryGetValue(widgetId, out var entry))
        {
            entry.Window.HideWindow();
            return true;
        }

        if (_quickCaptureWidgets.TryGetValue(widgetId, out var quickCaptureEntry))
        {
            quickCaptureEntry.Window.HideWindow();
            return true;
        }

        if (_contentWidgets.TryGetValue(widgetId, out var contentWindow))
        {
            contentWindow.HideWindow();
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
            $"[TrayBatch] RestoreDesktopLayer force={force} file={_widgets.Count} quick={_quickCaptureWidgets.Count} content={_contentWidgets.Count}");
        foreach (var window in GetLoadedDesktopWindows())
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

        SetWidgetsRaisedFromTray(false);
        _trayRaiseBatchGeneration++;
        StopTrayLayerRestoreMonitor();
    }

    /// <summary>
    /// Update the persisted position lock state for a widget.
    /// </summary>
    public bool SetWidgetPositionLocked(string widgetId, bool locked)
    {
        if (_widgets.TryGetValue(widgetId, out var loadedEntry))
        {
            loadedEntry.ViewModel.SetPositionLocked(locked);
            return true;
        }

        var config = FindConfig(widgetId);
        if (config is null)
        {
            return false;
        }

        config.IsPositionLocked = locked;
        _settingsService.UpdateWidget(config);
        return true;
    }

    /// <summary>
    /// Update the persisted size lock state for a widget.
    /// </summary>
    public bool SetWidgetSizeLocked(string widgetId, bool locked)
    {
        if (_widgets.TryGetValue(widgetId, out var loadedEntry))
        {
            loadedEntry.ViewModel.SetSizeLocked(locked);
            return true;
        }

        var config = FindConfig(widgetId);
        if (config is null)
        {
            return false;
        }

        config.IsSizeLocked = locked;
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
        StopTrayLayerRestoreMonitor();
        _settingsService.SettingsChanged -= OnSettingsChanged;
        _settingsService.AppearancePreviewChanged -= ApplyAppearancePreview;
        _themeService.AppearanceChanged -= ApplyAppearancePreview;

        foreach (var (_, (window, viewModel)) in _widgets)
        {
            viewModel.Dispose();
            try
            {
                window.Close();
            }
            catch
            {
            }
        }

        _widgets.Clear();

        foreach (var (_, (window, viewModel)) in _quickCaptureWidgets)
        {
            viewModel.Dispose();
            try
            {
                window.Close();
            }
            catch
            {
            }
        }

        _quickCaptureWidgets.Clear();

        foreach (var (_, window) in _contentWidgets.ToList())
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
        _sessionManager.MarkHidden("close-all");
    }

    public int GetDefaultManagedStorageWidgetCount()
    {
        return _settingsService.Settings.Widgets.Count(widget =>
            widget.WidgetKind == WidgetKind.File &&
            widget.FollowsDefaultStoragePath &&
            !IsDeleted(widget.Id));
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
            _ => (
                _settingsService.Settings.DefaultWidgetWidth,
                _settingsService.Settings.DefaultWidgetHeight)
        };
    }

    private Task SetContentFeatureWidgetEnabledAsync(bool enabled, bool reveal)
    {
        return SetContentFeatureWidgetEnabledAsync(WidgetKind.Music, enabled, reveal);
    }

    private async Task<WidgetWindow> CreateWidgetFromConfigAsync(
        WidgetConfig config,
        bool keepPreparedForAnimation = false,
        bool revealAfterCreate = false,
        bool showRaisedWhileInitializing = false)
    {
        if (config.WidgetKind != WidgetKind.File)
        {
            throw new InvalidOperationException(
                $"File widget window creation requires a File config. Actual kind: {config.WidgetKind}.");
        }

        if (_widgets.TryGetValue(config.Id, out var existing))
        {
            return existing.Window;
        }

        config.IsDisabled = false;
        NormalizeWidgetBounds(config);

        var dispatcherQueue = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        var viewModel = new WidgetViewModel(config, _fileService, _organizerService, _settingsService, _localizationService, dispatcherQueue);
        var window = new WidgetWindow(viewModel, _settingsService, _localizationService);

        _themeService.TrackWindow(window);
        _widgets[config.Id] = (window, viewModel);
        _widgetWindowHandles.Add(window.WindowHandle);
        ApplyCapsuleArrangementIfChanged(force: true);

        window.Closed += (_, _) =>
        {
            if (IsDeleted(config.Id) || FindConfig(config.Id) is null)
            {
                return;
            }

            config.IsVisible = false;
            _settingsService.SaveDebounced();
        };

        try
        {
            window.PrepareTrayShowAnimation();
            if (!keepPreparedForAnimation)
            {
                window.Activate();
                window.PushToBottom();
            }
            else if (showRaisedWhileInitializing)
            {
                viewModel.IsLoading = true;
                QueueDeferredWidgetInitialization(config, window, viewModel);
                WidgetCreated?.Invoke(window);
                return window;
            }

            await viewModel.InitializeAsync();
            if (!keepPreparedForAnimation)
            {
                window.CompleteTrayShowWithoutAnimation();
                if (revealAfterCreate)
                {
                    window.RevealFromTray(autoRestore: false);
                }
            }
        }
        catch
        {
            _widgets.Remove(config.Id);
            viewModel.Dispose();

            try
            {
                window.Close();
            }
            catch
            {
            }

            throw;
        }

        WidgetCreated?.Invoke(window);
        return window;
    }

    private async Task<IDesktopWidgetWindow> CreateRegisteredWidgetFromConfigAsync(
        WidgetConfig config,
        bool keepPreparedForAnimation = false,
        bool revealAfterCreate = false,
        bool showRaisedWhileInitializing = false)
    {
        if (!_windowProviders.TryGetValue(config.WidgetKind, out var provider))
        {
            throw new NotSupportedException($"Widget kind '{config.WidgetKind}' is not registered as creatable.");
        }

        return await provider.CreateWindowAsync(new WidgetWindowCreationRequest(
            config,
            keepPreparedForAnimation,
            revealAfterCreate,
            showRaisedWhileInitializing));
    }

    private Task<ContentWidgetWindow> CreateContentWidgetFromConfigAsync(
        WidgetConfig config,
        bool keepPreparedForAnimation = false,
        bool revealAfterCreate = false,
        bool showRaisedWhileInitializing = false)
    {
        if (!HasUiThreadAccess())
        {
            return RunOnUiThreadAsync(() => CreateContentWidgetFromConfigAsync(
                config,
                keepPreparedForAnimation,
                revealAfterCreate,
                showRaisedWhileInitializing));
        }

        if (_contentWidgets.TryGetValue(config.Id, out var existing))
        {
            return Task.FromResult(existing);
        }

        var factory = new ContentWidgetWindowFactory(new WidgetContentFactory(_localizationService), _settingsService);
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

        var window = factory.CreateContentWindow(config);
        _themeService.TrackWindow(window);
        _contentWidgets[config.Id] = window;
        _widgetWindowHandles.Add(window.WindowHandle);
        ApplyCapsuleArrangementIfChanged(force: true);

        window.Closed += (_, _) =>
        {
            if (_contentWidgets.TryGetValue(config.Id, out var currentWindow) &&
                ReferenceEquals(currentWindow, window))
            {
                _contentWidgets.Remove(config.Id);
            }

            _widgetWindowHandles.Remove(window.WindowHandle);
            if (IsDeleted(config.Id) || FindConfig(config.Id) is null)
            {
                return;
            }

            if (_suppressClosedVisibilityPersistence.Contains(config.Id))
            {
                return;
            }

            if (_contentWidgets.ContainsKey(config.Id))
            {
                return;
            }

            config.IsVisible = false;
            _settingsService.SaveDebounced();
        };

        try
        {
            window.PrepareTrayShowAnimation();
            if (!keepPreparedForAnimation)
            {
                window.ShowPreparedAtDesktopLayer();
                window.CompleteTrayShowWithoutAnimation();
            }
            else if (showRaisedWhileInitializing)
            {
                window.ShowPreparedRaisedFromTray();
                return Task.FromResult(window);
            }

            if (revealAfterCreate)
            {
                window.ShowPreparedRaisedFromTray();
                window.PlayTrayShowAnimation();
            }
        }
        catch
        {
            _contentWidgets.Remove(config.Id);
            _widgetWindowHandles.Remove(window.WindowHandle);

            try
            {
                window.Close();
            }
            catch
            {
            }

            throw;
        }

        return Task.FromResult(window);
    }

    private void QueueDeferredWidgetInitialization(
        WidgetConfig config,
        WidgetWindow window,
        WidgetViewModel viewModel)
    {
        App.UiDispatcherQueue.TryEnqueue(async () =>
        {
            await Task.Yield();
            try
            {
                await viewModel.InitializeAsync();
            }
            catch (Exception ex)
            {
                App.Log($"[WidgetManager] Failed to initialize widget '{config.Name}' ({config.Id}) after show: {ex}");
                if (_widgets.TryGetValue(config.Id, out var entry) &&
                    ReferenceEquals(entry.Window, window))
                {
                    _widgets.Remove(config.Id);
                    viewModel.Dispose();
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
