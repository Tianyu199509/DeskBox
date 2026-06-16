using DeskBox.Models;
using DeskBox.ViewModels;
using DeskBox.Views;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;

namespace DeskBox.Services;

public sealed record ManagedStorageMigrationResult(
    int AffectedWidgetCount,
    string OldRootPath,
    string NewRootPath);

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

/// <summary>
/// Manages the lifecycle of all desktop organizer widgets.
/// </summary>
public sealed class WidgetManager
{
    private readonly SettingsService _settingsService;
    private readonly FileService _fileService;
    private readonly OrganizerService _organizerService;
    private readonly ThemeService _themeService;
    private readonly LocalizationService _localizationService;
    private readonly Func<string> _desktopPathProvider;
    private readonly bool _recycleManagedFolderDeletes;
    private readonly Dictionary<string, (WidgetWindow Window, WidgetViewModel ViewModel)> _widgets = new();
    private readonly HashSet<string> _deletedWidgetIds = [];
    private readonly List<WidgetWindow> _retiredWindows = [];
    private bool _widgetsRaisedFromTray;
    private bool _isTogglingWidgetsDesktopLayer;
    private bool _isApplyingAppearancePreview;
    private DateTime _lastTrayLayerToggleUtc = DateTime.MinValue;
    private DateTime _suppressTrayLayerRestoreUntilUtc = DateTime.MinValue;

    public IReadOnlyDictionary<string, (WidgetWindow Window, WidgetViewModel ViewModel)> Widgets => _widgets;

    public bool WidgetsRaisedFromTray => _widgetsRaisedFromTray;

    public event Action<WidgetWindow>? WidgetCreated;
    public event Action<string>? WidgetRemoved;
    public event Action<bool>? TrayLayerStateChanged;

    public WidgetManager(
        SettingsService settingsService,
        FileService fileService,
        OrganizerService organizerService,
        ThemeService themeService,
        LocalizationService? localizationService = null)
        : this(
            settingsService,
            fileService,
            organizerService,
            themeService,
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
        Func<string> desktopPathProvider,
        bool recycleManagedFolderDeletes)
        : this(
            settingsService,
            fileService,
            organizerService,
            themeService,
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
        LocalizationService? localizationService,
        Func<string> desktopPathProvider,
        bool recycleManagedFolderDeletes)
    {
        _settingsService = settingsService;
        _fileService = fileService;
        _organizerService = organizerService;
        _themeService = themeService;
        _localizationService = localizationService ?? new LocalizationService(settingsService);
        _desktopPathProvider = desktopPathProvider;
        _recycleManagedFolderDeletes = recycleManagedFolderDeletes;
        _settingsService.AppearancePreviewChanged += ApplyAppearancePreview;
        _themeService.AppearanceChanged += ApplyAppearancePreview;
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
                widget.WidgetKind == WidgetKind.File &&
                widget.IsVisible &&
                !widget.IsDisabled &&
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
                await CreateWidgetFromConfigAsync(config);
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
        if (config is null || config.WidgetKind != WidgetKind.File || config.IsDisabled)
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
        if (_isTogglingWidgetsDesktopLayer || now - _lastTrayLayerToggleUtc < TimeSpan.FromMilliseconds(320))
        {
            return null;
        }

        _isTogglingWidgetsDesktopLayer = true;
        _lastTrayLayerToggleUtc = now;
        try
        {
            var windowsToRaise = new List<WidgetWindow>();
            foreach (var widget in _settingsService.Settings.Widgets
                         .Where(widget => widget.WidgetKind == WidgetKind.File && !widget.IsDisabled && !IsDeleted(widget.Id))
                         .ToList())
            {
                var window = await PrepareWidgetForBatchShowAsync(widget);
                if (window is null)
                {
                    continue;
                }

                windowsToRaise.Add(window);
            }

            foreach (var window in windowsToRaise)
            {
                window.ShowPreparedRaisedFromTray();
            }

            _suppressTrayLayerRestoreUntilUtc = DateTime.UtcNow.AddMilliseconds(500);
            PlayPreparedTrayShowAnimations(windowsToRaise);
            windowsToRaise.LastOrDefault()?.ActivateRaisedFromTrayBatch();
            SetWidgetsRaisedFromTray(windowsToRaise.Count > 0);
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
        if (visible)
        {
            var windowsToShow = new List<WidgetWindow>();
            foreach (var widget in _settingsService.Settings.Widgets
                         .Where(widget => widget.WidgetKind == WidgetKind.File && !widget.IsDisabled && !IsDeleted(widget.Id))
                         .ToList())
            {
                var window = await PrepareWidgetForBatchShowAsync(widget);
                if (window is null)
                {
                    continue;
                }

                windowsToShow.Add(window);
            }

            foreach (var window in windowsToShow)
            {
                window.ShowPreparedAtDesktopLayer();
            }

            PlayPreparedTrayShowAnimations(windowsToShow);
            return;
        }

        var windowsToHide = _widgets.Values
            .Select(entry => entry.Window)
            .Where(window => window.PrepareTrayHideAnimation())
            .ToList();

        PlayPreparedTrayHideAnimations(windowsToHide);

        SetWidgetsRaisedFromTray(false);
    }

    /// <summary>
    /// Remove a widget and close its window.
    /// </summary>
    public async Task RemoveWidgetAsync(string widgetId, WidgetRemovalAction removalAction = WidgetRemovalAction.RemoveWidgetOnly)
    {
        var config = FindConfig(widgetId);
        if (config is not null)
        {
            await ApplyWidgetRemovalActionAsync(config, removalAction);
        }

        _deletedWidgetIds.Add(widgetId);

        if (_widgets.TryGetValue(widgetId, out var entry))
        {
            App.Log($"[WidgetManager] Retiring widget window for delete: {widgetId}");
            entry.Window.HideWindow();
            entry.ViewModel.Dispose();
            _widgets.Remove(widgetId);
            _retiredWindows.Add(entry.Window);
        }

        _settingsService.RemoveWidget(widgetId);
        await _settingsService.SaveAsync();
        App.Log($"[WidgetManager] Widget delete persisted: {widgetId}");
        WidgetRemoved?.Invoke(widgetId);
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

    private async Task<WidgetWindow?> PrepareWidgetForBatchShowAsync(WidgetConfig config)
    {
        if (IsDeleted(config.Id) ||
            config.WidgetKind != WidgetKind.File ||
            config.IsDisabled)
        {
            return null;
        }

        if (_widgets.TryGetValue(config.Id, out var existing))
        {
            existing.Window.PrepareTrayShowAnimation();
            return existing.Window;
        }

        var window = await CreateWidgetFromConfigAsync(config, keepPreparedForAnimation: true);
        window.PrepareTrayShowAnimation();
        return window;
    }

    private void PlayPreparedTrayShowAnimations(IReadOnlyList<WidgetWindow> windows)
    {
        foreach (var window in windows)
        {
            window.PlayTrayShowAnimation();
        }
    }

    private void PlayPreparedTrayHideAnimations(IReadOnlyList<WidgetWindow> windows)
    {
        foreach (var window in windows)
        {
            window.PlayPreparedTrayHideAnimation();
        }
    }

    public bool CanCleanupManagedStorageForWidget(string widgetId)
    {
        var config = FindConfig(widgetId);
        return config is not null &&
               IsDefaultManagedStorageFolder(config.MappedFolderPath) &&
               config.FollowsDefaultStoragePath;
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
            .OrderBy(candidate => candidate.Name, StringComparer.CurrentCultureIgnoreCase)
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

    public void RestoreRaisedWidgetsToDesktopLayer()
    {
        if (_isTogglingWidgetsDesktopLayer ||
            DateTime.UtcNow < _suppressTrayLayerRestoreUntilUtc)
        {
            return;
        }

        foreach (var (_, (window, _)) in _widgets.ToList())
        {
            window.RestoreDesktopLayerFromManager();
        }

        SetWidgetsRaisedFromTray(false);
    }

    private void SetWidgetsRaisedFromTray(bool raised)
    {
        if (_widgetsRaisedFromTray == raised)
        {
            return;
        }

        _widgetsRaisedFromTray = raised;
        TrayLayerStateChanged?.Invoke(raised);
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
            widget.WidgetKind == WidgetKind.File &&
            widget.IsVisible &&
            !widget.IsDisabled &&
            !IsDeleted(widget.Id));

        await SetAllWidgetsVisibleAsync(!anyVisible);
    }

    /// <summary>
    /// Close all widget windows for shutdown.
    /// </summary>
    public void CloseAll()
    {
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
        try
        {
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
        }
        catch
        {
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

        foreach (var widgetPlan in affectedWidgets)
        {
            if (_widgets.TryGetValue(widgetPlan.Widget.Id, out var entry))
            {
                await entry.ViewModel.RefreshFromConfigAsync();
            }
        }

        return new ManagedStorageMigrationResult(affectedWidgets.Count, oldRootPath, normalizedNewRootPath);
    }

    private WidgetConfig? FindConfig(string widgetId)
    {
        return _settingsService.Settings.Widgets.FirstOrDefault(widget => widget.Id == widgetId);
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
            SettingsService.NormalizeManagedStorageRootPath(_settingsService.Settings.DefaultManagedStorageRootPath),
            managedFolderName);
    }

    private string CreateManagedFolderName(string displayName, string? widgetId = null)
    {
        string baseFolderName = FileService.SanitizeFileSystemName(displayName);
        if (string.IsNullOrWhiteSpace(baseFolderName))
        {
            baseFolderName = _localizationService.T("Widget.ManagedFolderBaseName");
        }

        string rootPath = SettingsService.NormalizeManagedStorageRootPath(_settingsService.Settings.DefaultManagedStorageRootPath);
        var usedNames = _settingsService.Settings.Widgets
            .Where(widget => widget.WidgetKind == WidgetKind.File &&
                             widget.FollowsDefaultStoragePath &&
                             !string.IsNullOrWhiteSpace(widget.ManagedFolderName) &&
                             !string.Equals(widget.Id, widgetId, StringComparison.Ordinal))
            .Select(widget => widget.ManagedFolderName!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        string candidate = baseFolderName;
        int suffix = 2;
        while (usedNames.Contains(candidate) || Directory.Exists(Path.Combine(rootPath, candidate)))
        {
            candidate = $"{baseFolderName} ({suffix++})";
        }

        return candidate;
    }

    private bool ShouldMoveManagedItems()
    {
        return !string.Equals(_settingsService.Settings.ManagedDropAction, SettingsService.ManagedDropActionCopy, StringComparison.OrdinalIgnoreCase);
    }

    private async Task<WidgetWindow> CreateWidgetFromConfigAsync(
        WidgetConfig config,
        bool keepPreparedForAnimation = false,
        bool revealAfterCreate = false)
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
