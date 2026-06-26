using DeskBox.Models;
using DeskBox.Helpers;
using DeskBox.ViewModels;
using DeskBox.Views;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;

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

internal interface IDesktopWidgetWindow
{
    IntPtr WindowHandle { get; }
    bool Visible { get; }
    Windows.Foundation.Rect AnimationBounds { get; }
    void ApplyAppearancePreview();
    void SetTrayAnimationOffsetOverride(double? offsetX, double? offsetY);
    void PrepareTrayShowAnimation();
    void ShowPreparedAtDesktopLayer(bool persistVisibility = true);
    void ShowPreparedRaisedFromTray(bool persistVisibility = true);
    void PlayTrayShowAnimation();
    bool PrepareTrayHideAnimation(bool persistVisibility = true);
    void PlayPreparedTrayHideAnimation();
    void ActivateRaisedFromTrayBatch();
    void EnsureRaisedFromTrayTopMost();
    void ForceRestoreDesktopLayerFromManager();
    void RestoreDesktopLayerFromManager();
    void HideWindow();
}

/// <summary>
/// Manages the lifecycle of all desktop organizer widgets.
/// </summary>
public sealed class WidgetManager
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
    private readonly Dictionary<string, (WidgetWindow Window, WidgetViewModel ViewModel)> _widgets = new();
    private readonly Dictionary<string, (QuickCaptureWidgetWindow Window, QuickCaptureWidgetViewModel ViewModel)> _quickCaptureWidgets = new();
    private readonly HashSet<string> _deletedWidgetIds = [];
    private readonly List<WidgetWindow> _retiredWindows = [];
    private readonly SemaphoreSlim _widgetRenameGate = new(1, 1);
    private const double OffscreenAnimationPadding = 16.0;
    private DispatcherQueueTimer? _trayLayerRestoreTimer;
    private readonly Win32Helper.LowLevelMouseProc _mouseHookProc;
    private IntPtr _mouseHookHandle;
    private bool _widgetsRaisedFromTray;
    private bool _isTogglingWidgetsDesktopLayer;
    private bool _isApplyingAppearancePreview;
    private bool _lastQuickCaptureEnabled;
    private DateTime _lastTrayLayerToggleUtc = DateTime.MinValue;
    private DateTime _suppressTrayLayerRestoreUntilUtc = DateTime.MinValue;
    private long _trayRaiseBatchGeneration;

    public IReadOnlyDictionary<string, (WidgetWindow Window, WidgetViewModel ViewModel)> Widgets => _widgets;
    public IReadOnlyDictionary<string, (QuickCaptureWidgetWindow Window, QuickCaptureWidgetViewModel ViewModel)> QuickCaptureWidgets => _quickCaptureWidgets;

    public bool WidgetsRaisedFromTray => _widgetsRaisedFromTray;
    public bool HasVisibleWidgets => _widgets.Values.Any(entry => entry.Window.Visible) ||
                                     _quickCaptureWidgets.Values.Any(entry => entry.Window.Visible);

    public bool IsWidgetWindow(IntPtr hwnd)
    {
        return _widgets.Values.Any(w => w.Window.WindowHandle == hwnd) ||
               _quickCaptureWidgets.Values.Any(w => w.Window.WindowHandle == hwnd);
    }

    public event Action<WidgetWindow>? WidgetCreated;
    public event Action<string>? WidgetRemoved;
    public event Action<bool>? TrayLayerStateChanged;

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
            $"[TrayBatch] ToggleDecision=raise reason=visible-covered-by-external hwnd=0x{foregroundWindow.ToInt64():X}");
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
        _mouseHookProc = TrayLayerMouseHookProc;
        _desktopPathProvider = desktopPathProvider;
        _recycleManagedFolderDeletes = recycleManagedFolderDeletes;
        _lastQuickCaptureEnabled = _settingsService.Settings.QuickCaptureEnabled;
        _settingsService.SettingsChanged += OnSettingsChanged;
        _settingsService.AppearancePreviewChanged += ApplyAppearancePreview;
        _themeService.AppearanceChanged += ApplyAppearancePreview;
    }

    private void OnSettingsChanged()
    {
        bool quickCaptureEnabled = _settingsService.Settings.QuickCaptureEnabled;
        if (quickCaptureEnabled == _lastQuickCaptureEnabled)
        {
            return;
        }

        _lastQuickCaptureEnabled = quickCaptureEnabled;
        if (!quickCaptureEnabled)
        {
            ApplyQuickCaptureEnabledState(enabled: false);
            return;
        }

        ApplyQuickCaptureEnabledState(enabled: true);
    }

    private void ApplyQuickCaptureEnabledState(bool enabled)
    {
        if (App.UiDispatcherQueue is { } dispatcherQueue && !dispatcherQueue.HasThreadAccess)
        {
            dispatcherQueue.TryEnqueue(() => ApplyQuickCaptureEnabledState(enabled));
            return;
        }

        if (!enabled)
        {
            HideLoadedQuickCaptureWidgets();
            return;
        }

        CreateOrShowQuickCaptureWidgetAsync(reveal: true).ContinueWith(
            task =>
            {
                if (task.Exception is not null)
                {
                    App.Log($"[WidgetManager] Failed to show Quick Capture after enabling: {task.Exception}");
                }
            },
            TaskContinuationOptions.OnlyOnFaulted);
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
        var configs = _settingsService.Settings.Widgets.Where(widget =>
                widget.WidgetKind is WidgetKind.File or WidgetKind.QuickCapture &&
                widget.IsVisible &&
                !widget.IsDisabled &&
                (widget.WidgetKind == WidgetKind.File || _settingsService.Settings.QuickCaptureEnabled) &&
                !IsDeleted(widget.Id))
            .ToList();

        using var perfScope = PerformanceLogger.Measure("WidgetManager.RestoreWidgets", $"count={configs.Count}");
        foreach (var config in configs)
        {
            try
            {
                using var widgetPerfScope = PerformanceLogger.Measure(
                    "WidgetManager.RestoreWidget",
                    $"id={config.Id} name={config.Name}");
                if (config.WidgetKind == WidgetKind.QuickCapture)
                {
                    await CreateQuickCaptureWidgetFromConfigAsync(config);
                }
                else
                {
                    await CreateWidgetFromConfigAsync(config);
                }
            }
            catch (Exception ex)
            {
                App.Log($"[WidgetManager] Failed to restore widget '{config.Name}' ({config.Id}): {ex}");
            }

            await Task.Yield();
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
            Width = _settingsService.Settings.DefaultWidgetWidth,
            Height = _settingsService.Settings.DefaultWidgetHeight
        };

        _settingsService.Settings.Widgets.Add(config);
        await _settingsService.SaveAsync();

        return await CreateWidgetFromConfigAsync(config, revealAfterCreate: true);
    }

    public async Task<QuickCaptureWidgetWindow> CreateOrShowQuickCaptureWidgetAsync(bool reveal = true)
    {
        _settingsService.Settings.QuickCaptureEnabled = true;
        _lastQuickCaptureEnabled = true;
        RestoreDeletedQuickCaptureConfigs();

        var config = _settingsService.Settings.Widgets.FirstOrDefault(widget =>
            widget.WidgetKind == WidgetKind.QuickCapture);

        if (config is null)
        {
            config = new WidgetConfig
            {
                Name = _localizationService.T("QuickCapture.Name"),
                WidgetKind = WidgetKind.QuickCapture,
                Width = _settingsService.Settings.DefaultWidgetWidth,
                Height = _settingsService.Settings.DefaultWidgetHeight
            };
            _settingsService.Settings.Widgets.Add(config);
        }

        config.IsDisabled = false;
        config.IsVisible = true;
        await _settingsService.SaveAsync();

        var window = await CreateQuickCaptureWidgetFromConfigAsync(config);
        if (reveal)
        {
            window.RevealFromTray(autoRestore: false);
        }

        return window;
    }

    private void RestoreDeletedQuickCaptureConfigs()
    {
        var quickCaptureIds = _settingsService.Settings.Widgets
            .Where(widget => widget.WidgetKind == WidgetKind.QuickCapture)
            .Select(widget => widget.Id)
            .ToHashSet(StringComparer.Ordinal);

        if (quickCaptureIds.Count == 0)
        {
            return;
        }

        _deletedWidgetIds.RemoveWhere(quickCaptureIds.Contains);
        _settingsService.Settings.DeletedWidgetIds.RemoveAll(quickCaptureIds.Contains);
    }

    public async Task SetQuickCaptureEnabledAsync(bool enabled, bool reveal = true)
    {
        _settingsService.Settings.QuickCaptureEnabled = enabled;
        _lastQuickCaptureEnabled = enabled;

        if (enabled)
        {
            await CreateOrShowQuickCaptureWidgetAsync(reveal);
            return;
        }

        foreach (var config in _settingsService.Settings.Widgets.Where(widget =>
                     widget.WidgetKind == WidgetKind.QuickCapture &&
                     !IsDeleted(widget.Id)))
        {
            config.IsVisible = false;
            config.IsDisabled = false;
        }

        HideLoadedQuickCaptureWidgets();
        await _settingsService.SaveAsync();
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

        if (config.WidgetKind == WidgetKind.QuickCapture && !_settingsService.Settings.QuickCaptureEnabled)
        {
            return false;
        }

        if (config.WidgetKind == WidgetKind.QuickCapture)
        {
            if (_quickCaptureWidgets.TryGetValue(widgetId, out var quickCaptureEntry))
            {
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

            var quickCaptureWindow = await CreateQuickCaptureWidgetFromConfigAsync(config, keepPreparedForAnimation: !reveal);
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

        if (config.WidgetKind != WidgetKind.File)
        {
            return false;
        }

        if (_widgets.TryGetValue(widgetId, out var entry))
        {
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

    /// <summary>
    /// Bring desktop widgets to the front of the normal Z-order from the tray.
    /// </summary>
    public async Task<bool?> RaiseWidgetsFromTrayAsync()
    {
        using var perfScope = PerformanceLogger.Measure("WidgetManager.RaiseWidgetsFromTray");
        var now = DateTime.UtcNow;
        double sinceLastToggleMs = (now - _lastTrayLayerToggleUtc).TotalMilliseconds;
        App.LogVerbose(
            $"[TrayBatch] Raise requested raised={_widgetsRaisedFromTray} toggling={_isTogglingWidgetsDesktopLayer} " +
            $"sinceLastMs={sinceLastToggleMs:F0} loadedFile={_widgets.Count} loadedQuick={_quickCaptureWidgets.Count}");
        if (_isTogglingWidgetsDesktopLayer || now - _lastTrayLayerToggleUtc < TimeSpan.FromMilliseconds(320))
        {
            App.LogVerbose("[TrayBatch] Raise ignored reason=busy-or-throttled");
            return null;
        }

        _isTogglingWidgetsDesktopLayer = true;
        _lastTrayLayerToggleUtc = now;
        try
        {
            var candidates = _settingsService.Settings.Widgets
                .Where(widget => widget.WidgetKind is WidgetKind.File or WidgetKind.QuickCapture &&
                                 !widget.IsDisabled &&
                                 !IsDeleted(widget.Id) &&
                                 (widget.WidgetKind == WidgetKind.File || _settingsService.Settings.QuickCaptureEnabled))
                .ToList();
            App.LogVerbose($"[TrayBatch] Raise candidates={candidates.Count} widgets={FormatWidgetList(candidates)}");

            var windowsToRaise = new List<IDesktopWidgetWindow>();
            foreach (var widget in candidates)
            {
                try
                {
                    var window = await PrepareWidgetForBatchShowAsync(widget, showRaisedWhileInitializing: true);
                    if (window is null)
                    {
                        continue;
                    }

                    windowsToRaise.Add(window);
                }
                catch (Exception ex)
                {
                    App.Log($"[WidgetManager] Failed to prepare widget for tray raise '{widget.Name}' ({widget.Id}): {ex}");
                }
            }

            App.LogVerbose($"[TrayBatch] Raise prepared={windowsToRaise.Count}/{candidates.Count}");
            var windowsToAnimate = windowsToRaise
                .Where(window => !window.Visible)
                .ToList();
            PrepareTrayShowAnimations(windowsToAnimate);

            _widgetsRaisedFromTray = windowsToRaise.Count > 0;
            var shownWindows = new List<IDesktopWidgetWindow>();
            foreach (var window in windowsToRaise)
            {
                try
                {
                    if (window.Visible)
                    {
                        window.EnsureRaisedFromTrayTopMost();
                    }
                    else
                    {
                        window.ShowPreparedRaisedFromTray(persistVisibility: false);
                    }

                    shownWindows.Add(window);
                }
                catch (Exception ex)
                {
                    App.Log($"[WidgetManager] Failed to show prepared widget from tray hwnd=0x{window.WindowHandle.ToInt64():X}: {ex}");
                }
            }

            _ = Win32Helper.HasMouseButtonActivity();
            _suppressTrayLayerRestoreUntilUtc = DateTime.UtcNow.AddMilliseconds(160);
            PlayPreparedTrayShowAnimations(windowsToAnimate);
            SetWidgetsRaisedFromTray(shownWindows.Count > 0);
            QueueTrayRaiseTopMostConfirmation(shownWindows);
            StartTrayLayerRestoreMonitor(shownWindows.Count > 0);
            SaveBatchVisibilityState();
            App.LogVerbose($"[TrayBatch] Raise completed raised={_widgetsRaisedFromTray} prepared={windowsToRaise.Count} shown={shownWindows.Count}");
            return _widgetsRaisedFromTray;
        }
        finally
        {
            _isTogglingWidgetsDesktopLayer = false;
        }
    }

    /// <summary>
    /// Show or hide all currently managed widgets.
    /// </summary>
    public async Task SetAllWidgetsVisibleAsync(bool visible)
    {
        using var perfScope = PerformanceLogger.Measure("WidgetManager.SetAllWidgetsVisible", $"visible={visible}");
        App.LogVerbose(
            $"[TrayBatch] SetAllVisible requested visible={visible} raised={_widgetsRaisedFromTray} " +
            $"loadedFile={_widgets.Count} loadedQuick={_quickCaptureWidgets.Count}");
        if (visible)
        {
            var candidates = _settingsService.Settings.Widgets
                .Where(widget => widget.WidgetKind is WidgetKind.File or WidgetKind.QuickCapture &&
                                 !widget.IsDisabled &&
                                 !IsDeleted(widget.Id) &&
                                 (widget.WidgetKind == WidgetKind.File || _settingsService.Settings.QuickCaptureEnabled))
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
                    App.Log($"[WidgetManager] Failed to show prepared widget at desktop layer hwnd=0x{window.WindowHandle.ToInt64():X}: {ex}");
                }
            }

            PlayPreparedTrayShowAnimations(windowsToAnimate);
            SaveBatchVisibilityState();
            App.LogVerbose($"[TrayBatch] SetAllVisible completed visible=true prepared={windowsToShow.Count} shown={shownWindows.Count}");
            return;
        }

        var hideCandidates = _widgets.Values
            .Select(entry => entry.Window)
            .Cast<IDesktopWidgetWindow>()
            .Concat(_quickCaptureWidgets.Values.Select(entry => (IDesktopWidgetWindow)entry.Window))
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
                App.Log($"[WidgetManager] Failed to prepare widget hide hwnd=0x{window.WindowHandle.ToInt64():X}: {ex}");
            }
        }

        App.LogVerbose($"[TrayBatch] SetAllVisible preparedHide={windowsToHide.Count}");
        PlayPreparedTrayHideAnimations(windowsToHide);

        SetWidgetsRaisedFromTray(false);
        _trayRaiseBatchGeneration++;
        StopTrayLayerRestoreMonitor();
        SaveBatchVisibilityState();
        App.LogVerbose($"[TrayBatch] SetAllVisible completed visible=false prepared={windowsToHide.Count}");
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
            entry.Window.HideWindow();
            _retiredWindows.Add(entry.Window);
        }

        if (_quickCaptureWidgets.TryGetValue(widgetId, out var quickCaptureEntry))
        {
            App.Log($"[WidgetManager] Retiring quick capture widget window for delete: {widgetId}");
            quickCaptureEntry.ViewModel.Dispose();
            _quickCaptureWidgets.Remove(widgetId);
            quickCaptureEntry.Window.HideWindow();
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

        _settingsService.RemoveWidget(widgetId);
        await _settingsService.SaveAsync();
        App.Log($"[WidgetManager] Widget delete persisted: {widgetId}");
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
        _settingsService.UpdateWidget(config);
    }

    public void SyncMappedWidgetShortcut(string widgetId)
    {
        var config = FindConfig(widgetId);
        if (config is null || IsDeleted(widgetId))
        {
            return;
        }

        SyncMappedWidgetShortcut(config);
    }

    public void SyncStorageFolderEntries()
    {
        string rootPath = GetManagedStorageRootPath();
        Directory.CreateDirectory(rootPath);

        var activeWidgetIds = _settingsService.Settings.Widgets
            .Where(widget => widget.WidgetKind == WidgetKind.File && !IsDeleted(widget.Id))
            .Select(widget => widget.Id)
            .ToHashSet(StringComparer.Ordinal);

        RemoveStaleMappedWidgetShortcuts(rootPath, activeWidgetIds);

        foreach (var config in _settingsService.Settings.Widgets
                     .Where(widget => widget.WidgetKind == WidgetKind.File && !IsDeleted(widget.Id))
                     .ToList())
        {
            if (config.FollowsDefaultStoragePath)
            {
                continue;
            }

            SyncMappedWidgetShortcut(config);
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
        foreach (var (widgetId, (window, _)) in _widgets.ToList())
        {
            if (string.Equals(widgetId, activeWidgetId, StringComparison.Ordinal))
            {
                continue;
            }

            window.ClearItemSelection();
        }
    }

    private async Task<IDesktopWidgetWindow?> PrepareWidgetForBatchShowAsync(
        WidgetConfig config,
        bool showRaisedWhileInitializing = false)
    {
        if (IsDeleted(config.Id))
        {
            App.LogVerbose($"[TrayBatch] Prepare skipped reason=deleted widget={FormatWidget(config)}");
            return null;
        }

        if (config.IsDisabled)
        {
            App.LogVerbose($"[TrayBatch] Prepare skipped reason=disabled widget={FormatWidget(config)}");
            return null;
        }

        if (config.WidgetKind == WidgetKind.QuickCapture)
        {
            if (!_settingsService.Settings.QuickCaptureEnabled)
            {
                App.LogVerbose($"[TrayBatch] Prepare skipped reason=quick-capture-disabled widget={FormatWidget(config)}");
                return null;
            }

            if (_quickCaptureWidgets.TryGetValue(config.Id, out var existingQuickCapture))
            {
                App.LogVerbose($"[TrayBatch] Prepare useLoaded widget={FormatWidget(config)} hwnd=0x{existingQuickCapture.Window.WindowHandle.ToInt64():X}");
                if (!existingQuickCapture.Window.Visible)
                {
                    existingQuickCapture.Window.PrepareTrayShowAnimation();
                }
                return existingQuickCapture.Window;
            }

            App.LogVerbose($"[TrayBatch] Prepare createQuick widget={FormatWidget(config)} raisedInit={showRaisedWhileInitializing}");
            var quickCaptureWindow = await CreateQuickCaptureWidgetFromConfigAsync(
                config,
                keepPreparedForAnimation: true,
                showRaisedWhileInitializing: showRaisedWhileInitializing);
            return quickCaptureWindow;
        }

        if (config.WidgetKind != WidgetKind.File)
        {
            App.LogVerbose($"[TrayBatch] Prepare skipped reason=unsupported-kind widget={FormatWidget(config)}");
            return null;
        }

        if (_widgets.TryGetValue(config.Id, out var existing))
        {
            App.LogVerbose($"[TrayBatch] Prepare useLoaded widget={FormatWidget(config)} hwnd=0x{existing.Window.WindowHandle.ToInt64():X}");
            if (!existing.Window.Visible)
            {
                existing.Window.PrepareTrayShowAnimation();
            }
            return existing.Window;
        }

        App.LogVerbose($"[TrayBatch] Prepare createFile widget={FormatWidget(config)} raisedInit={showRaisedWhileInitializing}");
        var window = await CreateWidgetFromConfigAsync(
            config,
            keepPreparedForAnimation: true,
            showRaisedWhileInitializing: showRaisedWhileInitializing);
        return window;
    }

    private void PlayPreparedTrayShowAnimations(IReadOnlyList<IDesktopWidgetWindow> windows)
    {
        ApplyTrayAnimationGroupOffset(windows);
        foreach (var window in windows)
        {
            try
            {
                window.PlayTrayShowAnimation();
            }
            catch (Exception ex)
            {
                App.Log($"[WidgetManager] Failed to play widget show animation hwnd=0x{window.WindowHandle.ToInt64():X}: {ex}");
            }
        }
    }

    private void PrepareTrayShowAnimations(IReadOnlyList<IDesktopWidgetWindow> windows)
    {
        ApplyTrayAnimationGroupOffset(windows);
        foreach (var window in windows)
        {
            try
            {
                window.PrepareTrayShowAnimation();
            }
            catch (Exception ex)
            {
                App.Log($"[WidgetManager] Failed to prepare widget show animation hwnd=0x{window.WindowHandle.ToInt64():X}: {ex}");
            }
        }
    }

    private void PlayPreparedTrayHideAnimations(IReadOnlyList<IDesktopWidgetWindow> windows)
    {
        ApplyTrayAnimationGroupOffset(windows);
        foreach (var window in windows)
        {
            try
            {
                window.PlayPreparedTrayHideAnimation();
            }
            catch (Exception ex)
            {
                App.Log($"[WidgetManager] Failed to play widget hide animation hwnd=0x{window.WindowHandle.ToInt64():X}: {ex}");
            }
        }
    }

    private void ApplyTrayAnimationGroupOffset(IReadOnlyList<IDesktopWidgetWindow> windows)
    {
        if (windows.Count == 0)
        {
            return;
        }

        foreach (var window in windows)
        {
            window.SetTrayAnimationOffsetOverride(null, null);
        }

        string effect = _settingsService.Settings.WidgetAnimationEffect;
        if (string.Equals(effect, SettingsService.WidgetAnimationEffectNone, StringComparison.Ordinal) ||
            string.Equals(effect, SettingsService.WidgetAnimationEffectFade, StringComparison.Ordinal) ||
            string.Equals(effect, SettingsService.WidgetAnimationEffectScaleFade, StringComparison.Ordinal) ||
            string.Equals(effect, SettingsService.WidgetAnimationEffectZoom, StringComparison.Ordinal))
        {
            return;
        }

        foreach (var group in windows.GroupBy(GetAnimationWorkAreaKey))
        {
            var groupWindows = group.ToList();
            if (groupWindows.Count == 0)
            {
                continue;
            }

            var workArea = GetAnimationWorkArea(groupWindows[0]);
            double groupLeft = groupWindows.Min(window => window.AnimationBounds.Left);
            double groupTop = groupWindows.Min(window => window.AnimationBounds.Top);
            double groupRight = groupWindows.Max(window => window.AnimationBounds.Right);
            double groupBottom = groupWindows.Max(window => window.AnimationBounds.Bottom);

            double offsetX = 0;
            double offsetY = 0;
            switch (effect)
            {
                case SettingsService.WidgetAnimationEffectSlideLeft:
                case SettingsService.WidgetAnimationEffectSlideLeftFade:
                    offsetX = -(groupRight - workArea.X + OffscreenAnimationPadding);
                    break;

                case SettingsService.WidgetAnimationEffectSlideUp:
                case SettingsService.WidgetAnimationEffectSlideUpFade:
                    offsetY = -(groupBottom - workArea.Y + OffscreenAnimationPadding);
                    break;

                case SettingsService.WidgetAnimationEffectSlideDown:
                case SettingsService.WidgetAnimationEffectSlideDownFade:
                    offsetY = workArea.Y + workArea.Height - groupTop + OffscreenAnimationPadding;
                    break;

                case SettingsService.WidgetAnimationEffectSlideRight:
                case SettingsService.WidgetAnimationEffectSlideFade:
                case SettingsService.WidgetAnimationEffectSlideRightFade:
                case SettingsService.WidgetAnimationEffectScaleSlide:
                default:
                    offsetX = workArea.X + workArea.Width - groupLeft + OffscreenAnimationPadding;
                    break;
            }

            foreach (var window in groupWindows)
            {
                window.SetTrayAnimationOffsetOverride(offsetX, offsetY);
            }
        }
    }

    private static string GetAnimationWorkAreaKey(IDesktopWidgetWindow window)
    {
        var workArea = GetAnimationWorkArea(window);
        return $"{workArea.X}:{workArea.Y}:{workArea.Width}:{workArea.Height}";
    }

    private static Windows.Graphics.RectInt32 GetAnimationWorkArea(IDesktopWidgetWindow window)
    {
        var point = new Windows.Graphics.PointInt32(
            (int)Math.Round(window.AnimationBounds.Left),
            (int)Math.Round(window.AnimationBounds.Top));
        var displayArea = DisplayArea.GetFromPoint(point, DisplayAreaFallback.Primary);
        return displayArea.WorkArea;
    }

    private static void ActivateLastRaisedWindow(IReadOnlyList<IDesktopWidgetWindow> windows)
    {
        if (windows.LastOrDefault() is not { } window)
        {
            return;
        }

        try
        {
            window.ActivateRaisedFromTrayBatch();
        }
        catch (Exception ex)
        {
            App.Log($"[WidgetManager] Failed to activate raised widget hwnd=0x{window.WindowHandle.ToInt64():X}: {ex}");
        }
    }

    private void QueueTrayRaiseTopMostConfirmation(IReadOnlyList<IDesktopWidgetWindow> windows)
    {
        if (windows.Count == 0)
        {
            return;
        }

        long generation = ++_trayRaiseBatchGeneration;
        ConfirmTrayRaiseTopMost(windows, generation);
        QueueTrayRaiseTopMostConfirmation(windows, generation, TimeSpan.FromMilliseconds(40));
        QueueTrayRaiseTopMostConfirmation(windows, generation, TimeSpan.FromMilliseconds(140));
        QueueTrayRaiseTopMostConfirmation(windows, generation, TimeSpan.FromMilliseconds(320));
        QueueTrayRaiseTopMostConfirmation(windows, generation, TimeSpan.FromMilliseconds(640));
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

    private void ConfirmTrayRaiseTopMost(IReadOnlyList<IDesktopWidgetWindow> windows, long generation)
    {
        if (generation != _trayRaiseBatchGeneration || !_widgetsRaisedFromTray)
        {
            return;
        }

        foreach (var window in windows)
        {
            try
            {
                window.EnsureRaisedFromTrayTopMost();
            }
            catch (Exception ex)
            {
                App.Log($"[WidgetManager] Failed to confirm raised widget topmost hwnd=0x{window.WindowHandle.ToInt64():X}: {ex}");
            }
        }
    }

    private void SaveBatchVisibilityState()
    {
        _settingsService.SaveDebounced(notifySubscribers: false);
    }

    public bool CanCleanupManagedStorageForWidget(string widgetId)
    {
        var config = FindConfig(widgetId);
        return config is not null &&
               IsDefaultManagedStorageFolder(config.MappedFolderPath) &&
               config.FollowsDefaultStoragePath &&
               Directory.Exists(config.MappedFolderPath);
    }

    public IReadOnlyList<ManagedStorageFolderCleanupCandidate> GetOrphanManagedStorageFolders()
    {
        string rootPath = SettingsService.NormalizeManagedStorageRootPath(_settingsService.Settings.DefaultManagedStorageRootPath);
        if (!Directory.Exists(rootPath))
        {
            return [];
        }

        var activePaths = _settingsService.Settings.Widgets
            .Where(widget => widget.WidgetKind == WidgetKind.File &&
                             widget.FollowsDefaultStoragePath &&
                             !IsDeleted(widget.Id))
            .SelectMany(widget => GetPossibleManagedStoragePaths(widget, rootPath))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return Directory.EnumerateDirectories(rootPath)
            .Select(Path.GetFullPath)
            .Where(path => !activePaths.Contains(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)))
            .Select(path => new ManagedStorageFolderCleanupCandidate(
                Path.GetFileName(path),
                path,
                CountDirectoryEntries(path)))
            .OrderBy(candidate => candidate.Name, NaturalStringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    public async Task MoveOrphanManagedStorageFolderContentsToDesktopAsync(string folderPath)
    {
        string normalizedPath = ValidateOrphanManagedStorageFolderPath(folderPath);
        await MoveManagedFolderContentsToDesktopAsync(normalizedPath);
    }

    public async Task DeleteOrphanManagedStorageFolderAsync(string folderPath)
    {
        string normalizedPath = ValidateOrphanManagedStorageFolderPath(folderPath);
        await _fileService.DeleteEntryAsync(normalizedPath, recycle: _recycleManagedFolderDeletes);
    }

    public async Task<int> RestoreOrphanManagedStorageFoldersAsync(IEnumerable<string> folderPaths)
    {
        int restoredCount = 0;
        bool canCreateWindow = CanCreateWidgetWindowOnCurrentThread();
        foreach (var folderPath in folderPaths)
        {
            string normalizedPath = ValidateOrphanManagedStorageFolderPath(folderPath);
            if (!Directory.Exists(normalizedPath))
            {
                continue;
            }

            string folderName = Path.GetFileName(normalizedPath);
            var config = new WidgetConfig
            {
                Name = string.IsNullOrWhiteSpace(folderName)
                    ? _localizationService.T("Widget.DefaultNameShort")
                    : folderName,
                WidgetKind = WidgetKind.File,
                MappedFolderPath = normalizedPath,
                FollowsDefaultStoragePath = true,
                ManagedFolderName = folderName,
                Width = _settingsService.Settings.DefaultWidgetWidth,
                Height = _settingsService.Settings.DefaultWidgetHeight,
                IsVisible = true,
                IsDisabled = false
            };

            _settingsService.Settings.Widgets.Add(config);
            if (canCreateWindow)
            {
                await CreateWidgetFromConfigAsync(config, revealAfterCreate: true);
            }
            restoredCount++;
            App.Log($"[WidgetManager] Restored orphan managed storage folder as widget: {normalizedPath} -> {config.Id}");
        }

        if (restoredCount > 0)
        {
            await _settingsService.SaveAsync();
        }

        return restoredCount;
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
        if (!_widgets.TryGetValue(widgetId, out var entry))
        {
            return false;
        }

        entry.Window.HideWindow();
        return true;
    }

    private void HideLoadedQuickCaptureWidgets()
    {
        foreach (var (_, (window, _)) in _quickCaptureWidgets.ToList())
        {
            window.HideWindow();
        }
    }

    public void RestoreRaisedWidgetsToDesktopLayer()
    {
        RestoreRaisedWidgetsToDesktopLayer(force: false);
    }

    public void ForceRestoreRaisedWidgetsToDesktopLayer()
    {
        RestoreRaisedWidgetsToDesktopLayer(force: true);
    }

    public bool RequestRestoreRaisedWidgetsToDesktopLayer(string reason = "interaction-ended")
    {
        if (!_widgetsRaisedFromTray)
        {
            App.LogVerbose($"[TrayBatch] RestoreRequest ignored reason={reason} state=not-raised");
            return false;
        }

        if (App.UiDispatcherQueue is { } dispatcherQueue && !dispatcherQueue.HasThreadAccess)
        {
            dispatcherQueue.TryEnqueue(() => RequestRestoreRaisedWidgetsToDesktopLayer(reason));
            return true;
        }

        StartTrayLayerRestoreMonitor(hasRaisedWidgets: true);
        App.LogVerbose($"[TrayBatch] RestoreRequest queued reason={reason}");
        return true;
    }

    private void QueueRequestedLayerRestoreCheck(string reason, TimeSpan delay)
    {
        long generation = _trayRaiseBatchGeneration;
        App.UiDispatcherQueue.TryEnqueue(async () =>
        {
            await Task.Delay(delay);
            TryRestoreRaisedWidgetsAfterInteraction(reason, generation);
        });
    }

    private void TryRestoreRaisedWidgetsAfterInteraction(string reason, long generation)
    {
        if (!_widgetsRaisedFromTray ||
            generation != _trayRaiseBatchGeneration ||
            _isTogglingWidgetsDesktopLayer ||
            DateTime.UtcNow < _suppressTrayLayerRestoreUntilUtc)
        {
            return;
        }

        Win32Helper.POINT? cursor = TryGetCursorPosition();
        IntPtr foreground = Win32Helper.GetForegroundWindow();
        bool foregroundIsWidget = IsWidgetWindow(foreground);
        bool pointerOverWidget = cursor.HasValue && IsWidgetWindow(Win32Helper.WindowFromPoint(cursor.Value));
        bool pointerOverTaskbar = IsPointerOverTaskbar(cursor);

        if (foregroundIsWidget || pointerOverWidget || pointerOverTaskbar)
        {
            App.LogVerbose($"[TrayBatch] RestoreRequest kept reason={reason} fgWidget={foregroundIsWidget} ptrWidget={pointerOverWidget} ptrTaskbar={pointerOverTaskbar}");
            return;
        }

        App.LogVerbose($"[TrayBatch] RestoreRequest restoring reason={reason} cursor={FormatPoint(cursor)}");
        RestoreRaisedWidgetsToDesktopLayer();
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

        App.LogVerbose(
            $"[TrayBatch] RestoreDesktopLayer force={force} file={_widgets.Count} quick={_quickCaptureWidgets.Count}");
        foreach (var (_, (window, _)) in _widgets.ToList())
        {
            try
            {
                if (force)
                {
                    window.ForceRestoreDesktopLayerFromManager();
                }
                else
                {
                    window.RestoreDesktopLayerFromManager();
                }
            }
            catch (Exception ex)
            {
                App.Log($"[WidgetManager] Failed to restore file widget desktop layer hwnd=0x{window.WindowHandle.ToInt64():X}: {ex}");
            }
        }

        foreach (var (_, (window, _)) in _quickCaptureWidgets.ToList())
        {
            try
            {
                if (force)
                {
                    window.ForceRestoreDesktopLayerFromManager();
                }
                else
                {
                    window.RestoreDesktopLayerFromManager();
                }
            }
            catch (Exception ex)
            {
                App.Log($"[WidgetManager] Failed to restore quick capture widget desktop layer hwnd=0x{window.WindowHandle.ToInt64():X}: {ex}");
            }
        }

        SetWidgetsRaisedFromTray(false);
        _trayRaiseBatchGeneration++;
        StopTrayLayerRestoreMonitor();
    }

    private void StartTrayLayerRestoreMonitor(bool hasRaisedWidgets)
    {
        if (!hasRaisedWidgets)
        {
            App.LogVerbose("[TrayBatch] RestoreMonitor not-started reason=no-raised-windows");
            StopTrayLayerRestoreMonitor();
            return;
        }

        _ = Win32Helper.HasMouseButtonActivity();

        _trayLayerRestoreTimer ??= App.UiDispatcherQueue.CreateTimer();
        _trayLayerRestoreTimer.Stop();
        _trayLayerRestoreTimer.Interval = TimeSpan.FromMilliseconds(40);
        _trayLayerRestoreTimer.Tick -= TrayLayerRestoreTimer_Tick;
        _trayLayerRestoreTimer.Tick += TrayLayerRestoreTimer_Tick;
        _trayLayerRestoreTimer.Start();
        InstallTrayLayerMouseHook();
        App.LogVerbose("[TrayBatch] RestoreMonitor started intervalMs=40");
    }

    private void StopTrayLayerRestoreMonitor()
    {
        if (_trayLayerRestoreTimer is null)
        {
            return;
        }

        _trayLayerRestoreTimer.Stop();
        _trayLayerRestoreTimer.Tick -= TrayLayerRestoreTimer_Tick;
        UninstallTrayLayerMouseHook();
        App.LogVerbose("[TrayBatch] RestoreMonitor stopped");
    }

    private void InstallTrayLayerMouseHook()
    {
        if (_mouseHookHandle != IntPtr.Zero)
        {
            return;
        }

        _mouseHookHandle = Win32Helper.SetWindowsMouseHookEx(
            Win32Helper.WH_MOUSE_LL,
            _mouseHookProc,
            Win32Helper.GetModuleHandle(null),
            0);
        if (_mouseHookHandle == IntPtr.Zero)
        {
            App.Log($"[TrayBatch] RestoreMouseHook install failed error={System.Runtime.InteropServices.Marshal.GetLastWin32Error()}");
            return;
        }

        App.LogVerbose("[TrayBatch] RestoreMouseHook installed");
    }

    private void UninstallTrayLayerMouseHook()
    {
        if (_mouseHookHandle == IntPtr.Zero)
        {
            return;
        }

        Win32Helper.UnhookWindowsHookEx(_mouseHookHandle);
        _mouseHookHandle = IntPtr.Zero;
        App.LogVerbose("[TrayBatch] RestoreMouseHook removed");
    }

    private IntPtr TrayLayerMouseHookProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && IsMouseDownMessage(wParam))
        {
            var data = System.Runtime.InteropServices.Marshal.PtrToStructure<Win32Helper.MSLLHOOKSTRUCT>(lParam);
            App.UiDispatcherQueue.TryEnqueue(() => RestoreRaisedWidgetsForExternalMouseDown(data.pt));
        }

        return Win32Helper.CallNextHookEx(_mouseHookHandle, nCode, wParam, lParam);
    }

    private static bool IsMouseDownMessage(IntPtr message)
    {
        int value = message.ToInt32();
        return value is Win32Helper.WM_LBUTTONDOWN or
               Win32Helper.WM_RBUTTONDOWN or
               Win32Helper.WM_MBUTTONDOWN or
               Win32Helper.WM_XBUTTONDOWN;
    }

    private void RestoreRaisedWidgetsForExternalMouseDown(Win32Helper.POINT cursor)
    {
        if (!_widgetsRaisedFromTray || _isTogglingWidgetsDesktopLayer)
        {
            return;
        }

        bool overTaskbar = IsPointerOverTaskbar(cursor);
        if (overTaskbar)
        {
            App.LogVerbose($"[TrayBatch] RestoreMouseHook kept taskbar");
            return;
        }

        bool overWidget = IsCursorOverAnyWidget(cursor);
        if (overWidget)
        {
            App.LogVerbose($"[TrayBatch] RestoreMouseHook kept-all over-widget cursor={cursor.X},{cursor.Y}");
            return;
        }

        IntPtr targetWindow = Win32Helper.WindowFromPoint(cursor);
        App.LogVerbose($"[TrayBatch] RestoreMouseHook restoring-all cursor={cursor.X},{cursor.Y} hwnd=0x{targetWindow.ToInt64():X}");
        RestoreRaisedWidgetsToDesktopLayer(force: true);
    }

    private bool IsCursorOverAnyWidget(Win32Helper.POINT cursor)
    {
        foreach (var (_, (window, _)) in _widgets)
        {
            if (IsCursorOverWindow(window, cursor))
            {
                return true;
            }
        }

        foreach (var (_, (window, _)) in _quickCaptureWidgets)
        {
            if (IsCursorOverWindow(window, cursor))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsCursorOverWindow(IDesktopWidgetWindow window, Win32Helper.POINT cursor)
    {
        if (!window.Visible)
        {
            return false;
        }

        var bounds = window.AnimationBounds;
        return cursor.X >= bounds.X &&
               cursor.X < bounds.X + (int)bounds.Width &&
               cursor.Y >= bounds.Y &&
               cursor.Y < bounds.Y + (int)bounds.Height;
    }

    private void RestoreRaisedWidgetsToDesktopLayerExcept(IntPtr skipHwnd)
    {
        if (!_widgetsRaisedFromTray || _isTogglingWidgetsDesktopLayer)
        {
            return;
        }

        foreach (var (_, (window, _)) in _widgets.ToList())
        {
            if (window.WindowHandle == skipHwnd) continue;
            try { window.ForceRestoreDesktopLayerFromManager(); }
            catch (Exception ex) { App.Log($"[WidgetManager] Failed to restore file widget: {ex}"); }
        }

        foreach (var (_, (window, _)) in _quickCaptureWidgets.ToList())
        {
            if (window.WindowHandle == skipHwnd) continue;
            try { window.ForceRestoreDesktopLayerFromManager(); }
            catch (Exception ex) { App.Log($"[WidgetManager] Failed to restore quick capture: {ex}"); }
        }
    }

    private void TrayLayerRestoreTimer_Tick(DispatcherQueueTimer sender, object args)
    {
        if (!_widgetsRaisedFromTray)
        {
            StopTrayLayerRestoreMonitor();
            return;
        }

        if (_isTogglingWidgetsDesktopLayer ||
            DateTime.UtcNow < _suppressTrayLayerRestoreUntilUtc)
        {
            return;
        }

        InstallTrayLayerMouseHook();
    }

    private static bool IsPointerOverDeskBoxWindow(Win32Helper.POINT? cursor)
    {
        if (!cursor.HasValue)
        {
            return false;
        }

        IntPtr pointerWindow = Win32Helper.WindowFromPoint(cursor.Value);
        return App.Current.IsDeskBoxWindow(pointerWindow);
    }

    private static bool IsForegroundDeskBoxWindow()
    {
        IntPtr foregroundWindow = Win32Helper.GetForegroundWindow();
        return IsDeskBoxForegroundWindow(foregroundWindow);
    }

    private static bool IsDeskBoxForegroundWindow(IntPtr foregroundWindow)
    {
        return foregroundWindow != IntPtr.Zero &&
               App.Current.IsDeskBoxWindow(foregroundWindow);
    }

    private static bool IsPointerOverTaskbar(Win32Helper.POINT? cursor)
    {
        if (!cursor.HasValue)
        {
            return false;
        }

        IntPtr pointerWindow = Win32Helper.WindowFromPoint(cursor.Value);
        if (pointerWindow == IntPtr.Zero)
        {
            return false;
        }

        IntPtr currentWindow = pointerWindow;
        while (currentWindow != IntPtr.Zero)
        {
            if (IsTaskbarWindow(currentWindow))
            {
                return true;
            }

            currentWindow = Win32Helper.GetParent(currentWindow);
        }

        IntPtr rootWindow = Win32Helper.GetAncestor(pointerWindow, Win32Helper.GA_ROOT);
        return IsTaskbarWindow(rootWindow);
    }

    private static bool IsTaskbarWindow(IntPtr hWnd)
    {
        return WindowOrAncestorHasClass(
            hWnd,
            value => string.Equals(value, "Shell_TrayWnd", StringComparison.Ordinal) ||
                     string.Equals(value, "Shell_SecondaryTrayWnd", StringComparison.Ordinal) ||
                     string.Equals(value, "NotifyIconOverflowWindow", StringComparison.Ordinal));
    }

    private static bool IsDesktopShellWindow(IntPtr hWnd)
    {
        return WindowOrAncestorHasClass(
            hWnd,
            value => string.Equals(value, "Progman", StringComparison.Ordinal) ||
                     string.Equals(value, "WorkerW", StringComparison.Ordinal));
    }

    private static bool WindowOrAncestorHasClass(IntPtr hWnd, Func<string, bool> predicate)
    {
        if (hWnd == IntPtr.Zero)
        {
            return false;
        }

        IntPtr currentWindow = hWnd;
        while (currentWindow != IntPtr.Zero)
        {
            if (WindowHasClass(currentWindow, predicate))
            {
                return true;
            }

            currentWindow = Win32Helper.GetParent(currentWindow);
        }

        IntPtr rootWindow = Win32Helper.GetAncestor(hWnd, Win32Helper.GA_ROOT);
        return WindowHasClass(rootWindow, predicate);
    }

    private static bool WindowHasClass(IntPtr hWnd, Func<string, bool> predicate)
    {
        if (hWnd == IntPtr.Zero)
        {
            return false;
        }

        var className = new System.Text.StringBuilder(256);
        int length = Win32Helper.GetClassName(hWnd, className, className.Capacity);
        return length > 0 && predicate(className.ToString());
    }

    private static Win32Helper.POINT? TryGetCursorPosition()
    {
        return Win32Helper.GetCursorPos(out var cursor) ? cursor : null;
    }

    private void SetWidgetsRaisedFromTray(bool raised)
    {
        if (_widgetsRaisedFromTray == raised)
        {
            return;
        }

        App.LogVerbose($"[TrayBatch] RaisedState changed { _widgetsRaisedFromTray } -> {raised}");
        _widgetsRaisedFromTray = raised;
        TrayLayerStateChanged?.Invoke(raised);
    }

    private static string FormatWidgetList(IReadOnlyList<WidgetConfig> widgets)
    {
        return widgets.Count == 0
            ? "[]"
            : "[" + string.Join(", ", widgets.Select(FormatWidget)) + "]";
    }

    private static string FormatWidget(WidgetConfig widget)
    {
        return $"{widget.Name}#{ShortId(widget.Id)} kind={widget.WidgetKind} visible={widget.IsVisible} disabled={widget.IsDisabled}";
    }

    private static string ShortId(string id)
    {
        return string.IsNullOrWhiteSpace(id)
            ? "none"
            : id.Length <= 8 ? id : id[..8];
    }

    private static string FormatPoint(Win32Helper.POINT? point)
    {
        return point.HasValue ? $"{point.Value.X},{point.Value.Y}" : "unknown";
    }

    public async Task NotifyItemsMovedOutAsync(string widgetId, IEnumerable<string> sourcePaths)
    {
        if (!_widgets.TryGetValue(widgetId, out var entry) || IsDeleted(widgetId))
        {
            return;
        }

        await entry.ViewModel.HandleItemsMovedOutAsync(sourcePaths);
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
            widget.WidgetKind is WidgetKind.File or WidgetKind.QuickCapture &&
            widget.IsVisible &&
            !widget.IsDisabled &&
            (widget.WidgetKind == WidgetKind.File || _settingsService.Settings.QuickCaptureEnabled) &&
            !IsDeleted(widget.Id));

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

        foreach (var window in _retiredWindows)
        {
            try
            {
                window.Close();
            }
            catch
            {
            }
        }

        _retiredWindows.Clear();
    }

    public int GetDefaultManagedStorageWidgetCount()
    {
        return _settingsService.Settings.Widgets.Count(widget =>
            widget.WidgetKind == WidgetKind.File &&
            widget.FollowsDefaultStoragePath &&
            !IsDeleted(widget.Id));
    }

    public IReadOnlyList<QuickCaptureFileWidgetTarget> GetQuickCaptureFileWidgetTargets()
    {
        return _settingsService.Settings.Widgets
            .Where(widget => widget.WidgetKind == WidgetKind.File &&
                             !widget.IsDisabled &&
                             !IsDeleted(widget.Id) &&
                             TryGetFileWidgetFolderPath(widget, out _))
            .Select(widget =>
            {
                TryGetFileWidgetFolderPath(widget, out string folderPath);
                return new QuickCaptureFileWidgetTarget(widget.Id, widget.Name, folderPath);
            })
            .ToList();
    }

    public QuickCaptureFileWidgetTarget? GetLastQuickCaptureFileWidgetTarget()
    {
        string lastTargetId = _settingsService.Settings.LastQuickCaptureFileWidgetId;
        if (string.IsNullOrWhiteSpace(lastTargetId))
        {
            return null;
        }

        return GetQuickCaptureFileWidgetTargets()
            .FirstOrDefault(target => string.Equals(target.WidgetId, lastTargetId, StringComparison.Ordinal));
    }

    public async Task<string?> SaveQuickCaptureItemToFileWidgetAsync(
        QuickCaptureItem item,
        string targetWidgetId,
        string? imageFileNamePrefix = null)
    {
        if (item.IsDeleted ||
            string.IsNullOrWhiteSpace(targetWidgetId) ||
            FindConfig(targetWidgetId) is not { } targetConfig ||
            targetConfig.WidgetKind != WidgetKind.File ||
            targetConfig.IsDisabled ||
            IsDeleted(targetWidgetId) ||
            !TryGetFileWidgetFolderPath(targetConfig, out string targetFolderPath))
        {
            return null;
        }

        Directory.CreateDirectory(targetFolderPath);
        string? destinationPath = item.Type switch
        {
            QuickCaptureItemType.Image => await SaveQuickCaptureImageToFolderAsync(item, targetFolderPath, imageFileNamePrefix),
            QuickCaptureItemType.Link => await SaveQuickCaptureLinkToFolderAsync(item, targetFolderPath),
            _ => await SaveQuickCaptureTextToFolderAsync(item, targetFolderPath)
        };

        if (!string.IsNullOrWhiteSpace(destinationPath))
        {
            RememberLastQuickCaptureFileWidgetTarget(targetWidgetId);
            if (_widgets.TryGetValue(targetWidgetId, out var targetEntry))
            {
                await targetEntry.ViewModel.RefreshFromConfigAsync();
                targetEntry.Window.RevealSavedItem(destinationPath);
            }
        }

        return destinationPath;
    }

    public async Task<ManagedStorageMigrationResult> UpdateDefaultManagedStorageRootAsync(string newRootPath)
    {
        string oldRootPath = SettingsService.NormalizeManagedStorageRootPath(_settingsService.Settings.DefaultManagedStorageRootPath);
        string normalizedNewRootPath = SettingsService.NormalizeManagedStorageRootPath(newRootPath);

        if (string.Equals(oldRootPath, normalizedNewRootPath, StringComparison.OrdinalIgnoreCase))
        {
            _settingsService.Settings.DefaultManagedStorageRootPath = normalizedNewRootPath;
            await _settingsService.SaveAsync();
            return new ManagedStorageMigrationResult(0, oldRootPath, normalizedNewRootPath);
        }

        Directory.CreateDirectory(normalizedNewRootPath);

        var affectedWidgets = _settingsService.Settings.Widgets
            .Where(widget => widget.WidgetKind == WidgetKind.File && widget.FollowsDefaultStoragePath && !IsDeleted(widget.Id))
            .Select(widget =>
            {
                string managedFolderName = string.IsNullOrWhiteSpace(widget.ManagedFolderName)
                    ? CreateManagedFolderName(widget.Name, widget.Id)
                    : widget.ManagedFolderName;
                string sourceFolder = string.IsNullOrWhiteSpace(widget.MappedFolderPath)
                    ? Path.Combine(oldRootPath, managedFolderName)
                    : widget.MappedFolderPath;
                string destinationFolder = Path.Combine(normalizedNewRootPath, managedFolderName);

                return new
                {
                    Widget = widget,
                    ManagedFolderName = managedFolderName,
                    SourceFolder = sourceFolder,
                    DestinationFolder = destinationFolder
                };
            })
            .ToList();

        var completedMoves = new List<(string SourceFolder, string DestinationFolder)>(affectedWidgets.Count);
        var originalWidgetStorage = affectedWidgets.ToDictionary(
            widget => widget.Widget.Id,
            widget => (widget.Widget.ManagedFolderName, widget.Widget.MappedFolderPath),
            StringComparer.Ordinal);

        SetManagedStorageMigrationBusy(affectedWidgets.Select(widget => widget.Widget.Id), isBusy: true);
        try
        {
            if (affectedWidgets.Count > 0)
            {
                await Task.Delay(100);
            }

            foreach (var widgetPlan in affectedWidgets)
            {
                await _fileService.RelocateDirectoryAsync(widgetPlan.SourceFolder, widgetPlan.DestinationFolder);
                completedMoves.Add((widgetPlan.SourceFolder, widgetPlan.DestinationFolder));
            }

            _settingsService.Settings.DefaultManagedStorageRootPath = normalizedNewRootPath;
            foreach (var widgetPlan in affectedWidgets)
            {
                widgetPlan.Widget.ManagedFolderName = widgetPlan.ManagedFolderName;
                widgetPlan.Widget.MappedFolderPath = widgetPlan.DestinationFolder;
            }

            await _settingsService.SaveAsync();
            SyncStorageFolderEntries(oldRootPath);

            foreach (var widgetPlan in affectedWidgets)
            {
                if (!_widgets.TryGetValue(widgetPlan.Widget.Id, out var entry))
                {
                    continue;
                }

                try
                {
                    await entry.ViewModel.RefreshFromConfigAsync();
                }
                catch (Exception ex)
                {
                    App.Log($"[ManagedStorageMigration] Refresh failed for widget '{widgetPlan.Widget.Id}': {ex}");
                }
            }
        }
        catch
        {
            _settingsService.Settings.DefaultManagedStorageRootPath = oldRootPath;
            foreach (var widgetPlan in affectedWidgets)
            {
                if (!originalWidgetStorage.TryGetValue(widgetPlan.Widget.Id, out var originalStorage))
                {
                    continue;
                }

                widgetPlan.Widget.ManagedFolderName = originalStorage.ManagedFolderName;
                widgetPlan.Widget.MappedFolderPath = originalStorage.MappedFolderPath;
            }

            foreach (var move in completedMoves.AsEnumerable().Reverse())
            {
                try
                {
                    await _fileService.RelocateDirectoryAsync(move.DestinationFolder, move.SourceFolder);
                }
                catch (Exception ex)
                {
                    App.Log($"[ManagedStorageMigration] Rollback failed for '{move.DestinationFolder}' -> '{move.SourceFolder}': {ex}");
                }
            }

            throw;
        }
        finally
        {
            SetManagedStorageMigrationBusy(affectedWidgets.Select(widget => widget.Widget.Id), isBusy: false);
        }

        return new ManagedStorageMigrationResult(affectedWidgets.Count, oldRootPath, normalizedNewRootPath);
    }

    private void SetManagedStorageMigrationBusy(IEnumerable<string> widgetIds, bool isBusy)
    {
        foreach (string widgetId in widgetIds.Distinct(StringComparer.Ordinal))
        {
            if (_widgets.TryGetValue(widgetId, out var entry))
            {
                entry.Window.SetMigrationBusy(isBusy);
            }
        }
    }

    private WidgetConfig? FindConfig(string widgetId)
    {
        return _settingsService.Settings.Widgets.FirstOrDefault(widget => widget.Id == widgetId);
    }

    private void RememberLastQuickCaptureFileWidgetTarget(string widgetId)
    {
        if (string.Equals(_settingsService.Settings.LastQuickCaptureFileWidgetId, widgetId, StringComparison.Ordinal))
        {
            return;
        }

        _settingsService.Settings.LastQuickCaptureFileWidgetId = widgetId;
        _settingsService.SaveDebounced(notifySubscribers: false);
    }

    private async Task<string?> SaveQuickCaptureImageToFolderAsync(
        QuickCaptureItem item,
        string targetFolderPath,
        string? imageFileNamePrefix)
    {
        if (string.IsNullOrWhiteSpace(item.ImagePath) || !File.Exists(item.ImagePath))
        {
            return null;
        }

        string fileName = QuickCaptureService.BuildImageExportFileName(
            imageFileNamePrefix,
            item.UpdatedAt == default ? item.CreatedAt : item.UpdatedAt,
            item.ImagePath);
        string destinationPath = FileService.GetAvailablePath(Path.Combine(targetFolderPath, fileName));
        await Task.Run(() => File.Copy(item.ImagePath, destinationPath));
        return destinationPath;
    }

    private async Task<string?> SaveQuickCaptureTextToFolderAsync(QuickCaptureItem item, string targetFolderPath)
    {
        string body = item.Body?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        string fileName = BuildQuickCaptureContentFileName(
            body,
            _localizationService.T("QuickCapture.TextFileNamePrefix"),
            ".txt");
        string destinationPath = FileService.GetAvailablePath(Path.Combine(targetFolderPath, fileName));
        await File.WriteAllTextAsync(destinationPath, body);
        return destinationPath;
    }

    private async Task<string?> SaveQuickCaptureLinkToFolderAsync(QuickCaptureItem item, string targetFolderPath)
    {
        string url = string.IsNullOrWhiteSpace(item.Url) ? item.Body?.Trim() ?? string.Empty : item.Url.Trim();
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return await SaveQuickCaptureTextToFolderAsync(item, targetFolderPath);
        }

        string baseText = string.IsNullOrWhiteSpace(uri.Host) ? uri.AbsoluteUri : uri.Host;
        string fileName = BuildQuickCaptureContentFileName(
            baseText,
            _localizationService.T("QuickCapture.LinkFileNamePrefix"),
            ".url");
        string destinationPath = FileService.GetAvailablePath(Path.Combine(targetFolderPath, fileName));
        await File.WriteAllTextAsync(destinationPath, $"[InternetShortcut]{Environment.NewLine}URL={uri.AbsoluteUri}{Environment.NewLine}");
        return destinationPath;
    }

    private static string BuildQuickCaptureContentFileName(string? body, string fallbackName, string extension)
    {
        string firstLine = body?
            .Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault() ?? string.Empty;
        string baseName = FileService.SanitizeFileSystemName(firstLine);
        if (baseName.Length > 36)
        {
            baseName = baseName[..36].Trim().TrimEnd('.');
        }

        if (string.IsNullOrWhiteSpace(baseName))
        {
            baseName = FileService.SanitizeFileSystemName(fallbackName);
        }

        if (string.IsNullOrWhiteSpace(baseName))
        {
            baseName = "Quick Capture";
        }

        return baseName.EndsWith(extension, StringComparison.OrdinalIgnoreCase)
            ? baseName
            : baseName + extension;
    }

    private bool TryGetFileWidgetFolderPath(WidgetConfig widget, out string folderPath)
    {
        folderPath = string.Empty;
        if (widget.WidgetKind != WidgetKind.File)
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(widget.MappedFolderPath))
        {
            folderPath = Path.GetFullPath(widget.MappedFolderPath);
            return true;
        }

        if (!widget.FollowsDefaultStoragePath || string.IsNullOrWhiteSpace(widget.ManagedFolderName))
        {
            return false;
        }

        folderPath = Path.Combine(GetManagedStorageRootPath(), widget.ManagedFolderName);
        return true;
    }

    private async Task RenameManagedWidgetFolderAsync(WidgetConfig config, string newName)
    {
        if (!config.FollowsDefaultStoragePath)
        {
            return;
        }

        string rootPath = GetManagedStorageRootPath();
        string currentFolderPath = string.IsNullOrWhiteSpace(config.MappedFolderPath)
            ? Path.Combine(rootPath, config.ManagedFolderName ?? string.Empty)
            : Path.GetFullPath(config.MappedFolderPath);
        string currentFolderName = string.IsNullOrWhiteSpace(config.ManagedFolderName)
            ? Path.GetFileName(currentFolderPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
            : config.ManagedFolderName;

        if (!Directory.Exists(currentFolderPath))
        {
            throw new DirectoryNotFoundException(currentFolderPath);
        }

        string desiredFolderName = FileService.SanitizeFileSystemName(newName);
        if (string.IsNullOrWhiteSpace(desiredFolderName))
        {
            desiredFolderName = _localizationService.T("Widget.ManagedFolderBaseName");
        }

        if (string.Equals(currentFolderName, desiredFolderName, StringComparison.OrdinalIgnoreCase))
        {
            config.ManagedFolderName = currentFolderName;
            config.MappedFolderPath = currentFolderPath;
            return;
        }

        string destinationFolderPath = Path.Combine(rootPath, desiredFolderName);
        if (IsManagedWidgetNameInUse(newName, desiredFolderName, config.Id) ||
            IsUnavailableManagedFolderPath(destinationFolderPath, currentFolderPath))
        {
            throw new InvalidOperationException(_localizationService.T("Widget.Error.ManagedFolderNameExists"));
        }

        if (!string.Equals(currentFolderPath, destinationFolderPath, StringComparison.OrdinalIgnoreCase))
        {
            await Task.Run(() => Directory.Move(currentFolderPath, destinationFolderPath));
        }

        config.ManagedFolderName = desiredFolderName;
        config.MappedFolderPath = destinationFolderPath;

        if (_widgets.TryGetValue(config.Id, out var entry))
        {
            await entry.ViewModel.RefreshFromConfigAsync();
        }
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

    private void RemoveMappedWidgetShortcut(WidgetConfig config)
    {
        DeleteMappedWidgetShortcut(GetExistingMappedWidgetShortcutPath(config, GetManagedStorageRootPath()), config.Id);
    }

    private void DeleteMappedWidgetShortcut(string shortcutPath, string widgetId)
    {
        if (string.IsNullOrWhiteSpace(shortcutPath) ||
            !File.Exists(shortcutPath) ||
            !IsDeskBoxMappedWidgetShortcut(shortcutPath, widgetId))
        {
            return;
        }

        try
        {
            File.Delete(shortcutPath);
        }
        catch (Exception ex)
        {
            App.Log($"[MappedShortcut] Failed to delete shortcut '{shortcutPath}': {ex}");
        }
    }

    private void RemoveStaleMappedWidgetShortcuts(string rootPath, ISet<string> activeWidgetIds)
    {
        if (!Directory.Exists(rootPath))
        {
            return;
        }

        foreach (string shortcutPath in Directory.EnumerateFiles(rootPath, "*.lnk", SearchOption.TopDirectoryOnly))
        {
            string? widgetId = GetDeskBoxMappedWidgetShortcutId(shortcutPath);
            if (string.IsNullOrWhiteSpace(widgetId) || activeWidgetIds.Contains(widgetId))
            {
                continue;
            }

            DeleteMappedWidgetShortcut(shortcutPath, widgetId);
        }
    }

    private void RemoveAllMappedWidgetShortcuts(string rootPath)
    {
        if (!Directory.Exists(rootPath))
        {
            return;
        }

        foreach (string shortcutPath in Directory.EnumerateFiles(rootPath, "*.lnk", SearchOption.TopDirectoryOnly))
        {
            string? widgetId = GetDeskBoxMappedWidgetShortcutId(shortcutPath);
            if (string.IsNullOrWhiteSpace(widgetId))
            {
                continue;
            }

            DeleteMappedWidgetShortcut(shortcutPath, widgetId);
        }
    }

    private string GetExistingMappedWidgetShortcutPath(WidgetConfig config, string rootPath)
    {
        if (!Directory.Exists(rootPath))
        {
            return string.Empty;
        }

        return Directory.EnumerateFiles(rootPath, "*.lnk", SearchOption.TopDirectoryOnly)
            .FirstOrDefault(path => IsDeskBoxMappedWidgetShortcut(path, config.Id)) ?? string.Empty;
    }

    private string BuildAvailableMappedShortcutPath(
        string displayName,
        string widgetId,
        string rootPath,
        string currentShortcutPath)
    {
        string shortcutName = FileService.SanitizeFileSystemName(displayName);
        if (string.IsNullOrWhiteSpace(shortcutName))
        {
            shortcutName = _localizationService.T("Widget.MappedShortcutBaseName");
        }

        string desiredPath = Path.Combine(rootPath, $"{shortcutName}.lnk");
        if (!string.IsNullOrWhiteSpace(currentShortcutPath) &&
            string.Equals(Path.GetFullPath(currentShortcutPath), desiredPath, StringComparison.OrdinalIgnoreCase))
        {
            return currentShortcutPath;
        }

        if (CanUseMappedShortcutPath(desiredPath, widgetId))
        {
            return desiredPath;
        }

        int suffix = 2;
        while (true)
        {
            string candidatePath = Path.Combine(rootPath, $"{shortcutName} ({suffix++}).lnk");
            if (CanUseMappedShortcutPath(candidatePath, widgetId))
            {
                return candidatePath;
            }
        }
    }

    private bool CanUseMappedShortcutPath(string shortcutPath, string widgetId)
    {
        if (!File.Exists(shortcutPath) && !Directory.Exists(shortcutPath))
        {
            return true;
        }

        return File.Exists(shortcutPath) && IsDeskBoxMappedWidgetShortcut(shortcutPath, widgetId);
    }

    private static bool IsDeskBoxMappedWidgetShortcut(string shortcutPath, string widgetId)
    {
        return string.Equals(
            GetDeskBoxMappedWidgetShortcutId(shortcutPath),
            widgetId,
            StringComparison.Ordinal);
    }

    private static string? GetDeskBoxMappedWidgetShortcutId(string shortcutPath)
    {
        var shortcut = ShortcutHelper.Resolve(shortcutPath);
        if (shortcut?.Description.StartsWith(ManagedShortcutDescriptionPrefix, StringComparison.Ordinal) != true)
        {
            return null;
        }

        return shortcut.Description[ManagedShortcutDescriptionPrefix.Length..];
    }

    private static string BuildMappedWidgetShortcutDescription(string widgetId)
    {
        return $"{ManagedShortcutDescriptionPrefix}{widgetId}";
    }

    private async Task ApplyWidgetRemovalActionAsync(WidgetConfig config, WidgetRemovalAction removalAction)
    {
        if (removalAction == WidgetRemovalAction.RemoveWidgetOnly)
        {
            return;
        }

        if (!config.FollowsDefaultStoragePath || !IsDefaultManagedStorageFolder(config.MappedFolderPath))
        {
            throw new InvalidOperationException(_localizationService.T("Widget.Error.ManagedFolderActionOnlyDefault"));
        }

        string folderPath = Path.GetFullPath(config.MappedFolderPath!);
        if (!Directory.Exists(folderPath))
        {
            return;
        }

        if (removalAction == WidgetRemovalAction.MoveManagedFolderContentsToDesktop)
        {
            await MoveManagedFolderContentsToDesktopAsync(folderPath);
            return;
        }

        if (removalAction == WidgetRemovalAction.DeleteManagedFolder)
        {
            await _fileService.DeleteEntryAsync(folderPath, recycle: _recycleManagedFolderDeletes);
        }
    }

    private async Task MoveManagedFolderContentsToDesktopAsync(string folderPath)
    {
        if (!Directory.Exists(folderPath))
        {
            return;
        }

        string desktopPath = _desktopPathProvider();
        var reservedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var plans = Directory.EnumerateFileSystemEntries(folderPath)
            .Select(path => new FileService.FileTransferPlan(
                path,
                FileService.GetAvailablePath(Path.Combine(desktopPath, Path.GetFileName(path)), reservedPaths)))
            .ToList();

        if (plans.Count > 0)
        {
            await _fileService.ExecuteTransferPlanAsync(plans, move: true);
        }

        if (Directory.Exists(folderPath) && !Directory.EnumerateFileSystemEntries(folderPath).Any())
        {
            Directory.Delete(folderPath, recursive: false);
        }
    }

    private string ValidateOrphanManagedStorageFolderPath(string folderPath)
    {
        string normalizedPath = Path.GetFullPath(folderPath);
        if (!IsDefaultManagedStorageFolder(normalizedPath))
        {
            throw new InvalidOperationException(_localizationService.T("Widget.Error.ManagedFolderCleanupOnlyDefault"));
        }

        if (!Directory.Exists(normalizedPath))
        {
            return normalizedPath;
        }

        var activePaths = _settingsService.Settings.Widgets
            .Where(widget => widget.WidgetKind == WidgetKind.File &&
                             widget.FollowsDefaultStoragePath &&
                             !IsDeleted(widget.Id))
            .SelectMany(widget => GetPossibleManagedStoragePaths(
                widget,
                SettingsService.NormalizeManagedStorageRootPath(_settingsService.Settings.DefaultManagedStorageRootPath)))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (activePaths.Contains(normalizedPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)))
        {
            throw new InvalidOperationException(_localizationService.T("Widget.Error.ManagedFolderStillActive"));
        }

        return normalizedPath;
    }

    private bool IsDefaultManagedStorageFolder(string? folderPath)
    {
        if (string.IsNullOrWhiteSpace(folderPath))
        {
            return false;
        }

        string rootPath = SettingsService.NormalizeManagedStorageRootPath(_settingsService.Settings.DefaultManagedStorageRootPath)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string normalizedPath = Path.GetFullPath(folderPath)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        if (string.Equals(rootPath, normalizedPath, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string? parentPath = Path.GetDirectoryName(normalizedPath);
        return parentPath is not null &&
               string.Equals(
                   parentPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                   rootPath,
                   StringComparison.OrdinalIgnoreCase);
    }

    private static IEnumerable<string> GetPossibleManagedStoragePaths(WidgetConfig widget, string rootPath)
    {
        if (!string.IsNullOrWhiteSpace(widget.MappedFolderPath))
        {
            yield return Path.GetFullPath(widget.MappedFolderPath)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }

        if (!string.IsNullOrWhiteSpace(widget.ManagedFolderName))
        {
            yield return Path.GetFullPath(Path.Combine(rootPath, widget.ManagedFolderName))
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
    }

    private static int CountDirectoryEntries(string folderPath)
    {
        try
        {
            return Directory.EnumerateFileSystemEntries(folderPath).Count();
        }
        catch
        {
            return 0;
        }
    }

    private bool IsDeleted(string widgetId)
    {
        return _deletedWidgetIds.Contains(widgetId) ||
               _settingsService.Settings.DeletedWidgetIds.Contains(widgetId);
    }

    private string BuildManagedFolderPath(string managedFolderName)
    {
        return Path.Combine(
            GetManagedStorageRootPath(),
            managedFolderName);
    }

    private string GetManagedStorageRootPath()
    {
        return SettingsService.NormalizeManagedStorageRootPath(_settingsService.Settings.DefaultManagedStorageRootPath);
    }

    private string CreateManagedFolderName(
        string displayName,
        string? widgetId = null,
        string? reusableFolderPath = null)
    {
        string baseFolderName = FileService.SanitizeFileSystemName(displayName);
        if (string.IsNullOrWhiteSpace(baseFolderName))
        {
            baseFolderName = _localizationService.T("Widget.ManagedFolderBaseName");
        }

        string rootPath = GetManagedStorageRootPath();
        var usedNames = _settingsService.Settings.Widgets
            .Where(widget => widget.WidgetKind == WidgetKind.File &&
                             widget.FollowsDefaultStoragePath &&
                             !string.IsNullOrWhiteSpace(widget.ManagedFolderName) &&
                             !string.Equals(widget.Id, widgetId, StringComparison.Ordinal))
            .Select(widget => widget.ManagedFolderName!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        string? reusablePath = string.IsNullOrWhiteSpace(reusableFolderPath)
            ? null
            : Path.GetFullPath(reusableFolderPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string candidate = baseFolderName;
        int suffix = 2;
        while (usedNames.Contains(candidate) ||
               IsUnavailableManagedFolderPath(Path.Combine(rootPath, candidate), reusablePath))
        {
            candidate = $"{baseFolderName} ({suffix++})";
        }

        return candidate;
    }

    private bool IsManagedWidgetNameInUse(string displayName, string managedFolderName, string widgetId)
    {
        return _settingsService.Settings.Widgets.Any(widget =>
            widget.WidgetKind == WidgetKind.File &&
            widget.FollowsDefaultStoragePath &&
            !IsDeleted(widget.Id) &&
            !string.Equals(widget.Id, widgetId, StringComparison.Ordinal) &&
            (string.Equals(widget.Name.Trim(), displayName.Trim(), StringComparison.OrdinalIgnoreCase) ||
             string.Equals(widget.ManagedFolderName, managedFolderName, StringComparison.OrdinalIgnoreCase)));
    }

    private static bool IsUnavailableManagedFolderPath(string folderPath, string? reusableFolderPath)
    {
        string normalizedPath = Path.GetFullPath(folderPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (reusableFolderPath is not null &&
            string.Equals(normalizedPath, reusableFolderPath, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return Directory.Exists(normalizedPath) || File.Exists(normalizedPath);
    }

    private bool ShouldMoveManagedItems()
    {
        return !string.Equals(_settingsService.Settings.ManagedDropAction, SettingsService.ManagedDropActionCopy, StringComparison.OrdinalIgnoreCase);
    }

    private async Task<WidgetWindow> CreateWidgetFromConfigAsync(
        WidgetConfig config,
        bool keepPreparedForAnimation = false,
        bool revealAfterCreate = false,
        bool showRaisedWhileInitializing = false)
    {
        if (_widgets.TryGetValue(config.Id, out var existing))
        {
            return existing.Window;
        }

        config.WidgetKind = WidgetKind.File;
        config.IsDisabled = false;
        NormalizeWidgetBounds(config);

        var dispatcherQueue = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        var viewModel = new WidgetViewModel(config, _fileService, _organizerService, _settingsService, _localizationService, dispatcherQueue);
        var window = new WidgetWindow(viewModel, _settingsService, _localizationService);

        _themeService.TrackWindow(window);
        _widgets[config.Id] = (window, viewModel);

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

    private async Task<QuickCaptureWidgetWindow> CreateQuickCaptureWidgetFromConfigAsync(
        WidgetConfig config,
        bool keepPreparedForAnimation = false,
        bool revealAfterCreate = false,
        bool showRaisedWhileInitializing = false)
    {
        if (_quickCaptureWidgets.TryGetValue(config.Id, out var existing))
        {
            return existing.Window;
        }

        config.WidgetKind = WidgetKind.QuickCapture;
        config.Name = string.IsNullOrWhiteSpace(config.Name)
            ? _localizationService.T("QuickCapture.Name")
            : config.Name;
        config.IsDisabled = false;
        NormalizeWidgetBounds(config);

        var dispatcherQueue = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        var viewModel = new QuickCaptureWidgetViewModel(
            config,
            _quickCaptureService,
            _settingsService,
            _localizationService,
            dispatcherQueue);
        var window = new QuickCaptureWidgetWindow(viewModel, _settingsService, _localizationService);

        _themeService.TrackWindow(window);
        _quickCaptureWidgets[config.Id] = (window, viewModel);

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
                QueueDeferredQuickCaptureInitialization(config, window, viewModel);
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
            _quickCaptureWidgets.Remove(config.Id);
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

        return window;
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

    private void QueueDeferredQuickCaptureInitialization(
        WidgetConfig config,
        QuickCaptureWidgetWindow window,
        QuickCaptureWidgetViewModel viewModel)
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
                App.Log($"[WidgetManager] Failed to initialize quick capture widget '{config.Name}' ({config.Id}) after show: {ex}");
                if (_quickCaptureWidgets.TryGetValue(config.Id, out var entry) &&
                    ReferenceEquals(entry.Window, window))
                {
                    _quickCaptureWidgets.Remove(config.Id);
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

        var area = DisplayArea.GetFromRect(
            new Windows.Graphics.RectInt32(x, y, width, height),
            DisplayAreaFallback.Nearest);
        var workArea = area.WorkArea;

        int safeX = x;
        int safeY = y;
        bool isWildlyOffscreen =
            safeX + width < workArea.X + 48 ||
            safeY + height < workArea.Y + 48 ||
            safeX > workArea.X + workArea.Width - 48 ||
            safeY > workArea.Y + workArea.Height - 48;

        if (isWildlyOffscreen)
        {
            safeX = workArea.X + 32;
            safeY = workArea.Y + 32;
        }
        else
        {
            int maxX = Math.Max(workArea.X, workArea.X + workArea.Width - width);
            int maxY = Math.Max(workArea.Y, workArea.Y + workArea.Height - height);
            safeX = Math.Clamp(safeX, workArea.X, maxX);
            safeY = Math.Clamp(safeY, workArea.Y, maxY);
        }

        bool changed =
            Math.Abs(config.Width - width) > double.Epsilon ||
            Math.Abs(config.Height - height) > double.Epsilon ||
            Math.Abs(config.X - safeX) > double.Epsilon ||
            Math.Abs(config.Y - safeY) > double.Epsilon;

        if (!changed)
        {
            return;
        }

        config.Width = width;
        config.Height = height;
        config.X = safeX;
        config.Y = safeY;
        _settingsService.UpdateWidget(config, notifySubscribers: false);
    }
}
