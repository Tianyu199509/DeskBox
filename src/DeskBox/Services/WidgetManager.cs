using DeskBox.Models;
using DeskBox.ViewModels;
using DeskBox.Views;
using Microsoft.UI.Windowing;

namespace DeskBox.Services;

public sealed record ManagedStorageMigrationResult(
    int AffectedWidgetCount,
    string OldRootPath,
    string NewRootPath);

/// <summary>
/// Manages the lifecycle of all desktop organizer widgets.
/// </summary>
public sealed class WidgetManager
{
    private readonly SettingsService _settingsService;
    private readonly FileService _fileService;
    private readonly OrganizerService _organizerService;
    private readonly ThemeService _themeService;
    private readonly Dictionary<string, (WidgetWindow Window, WidgetViewModel ViewModel)> _widgets = new();
    private readonly HashSet<string> _deletedWidgetIds = [];
    private readonly List<WidgetWindow> _retiredWindows = [];

    public IReadOnlyDictionary<string, (WidgetWindow Window, WidgetViewModel ViewModel)> Widgets => _widgets;

    public event Action<WidgetWindow>? WidgetCreated;
    public event Action<string>? WidgetRemoved;

    public WidgetManager(SettingsService settingsService, FileService fileService, OrganizerService organizerService, ThemeService themeService)
    {
        _settingsService = settingsService;
        _fileService = fileService;
        _organizerService = organizerService;
        _themeService = themeService;
    }

    /// <summary>
    /// Restore all visible file widgets from saved configuration.
    /// </summary>
    public async Task RestoreWidgetsAsync()
    {
        foreach (var config in _settingsService.Settings.Widgets.Where(widget =>
                     widget.WidgetKind == WidgetKind.File &&
                     widget.IsVisible &&
                     !widget.IsDisabled &&
                     !IsDeleted(widget.Id)).ToList())
        {
            try
            {
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
    /// Create a new empty reference widget.
    /// </summary>
    public async Task<WidgetWindow> CreateNewWidgetAsync(string name = "新建组件")
    {
        var config = new WidgetConfig
        {
            Name = name,
            WidgetKind = WidgetKind.File,
            Width = _settingsService.Settings.DefaultWidgetWidth,
            Height = _settingsService.Settings.DefaultWidgetHeight
        };

        _settingsService.Settings.Widgets.Add(config);
        await _settingsService.SaveAsync();

        return await CreateWidgetFromConfigAsync(config);
    }

    /// <summary>
    /// Create a new widget backed by the default managed storage root.
    /// </summary>
    public async Task<WidgetWindow> CreateManagedWidgetAsync(string name = "新建收纳组件")
    {
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

        return await CreateWidgetFromConfigAsync(config);
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

        return await CreateWidgetFromConfigAsync(config);
    }

    /// <summary>
    /// Show a specific widget by id.
    /// </summary>
    public async Task<bool> ShowWidgetAsync(string widgetId, bool reveal = true)
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
                entry.Window.RevealFromTray();
            }
            else
            {
                entry.Window.Activate();
                entry.Window.PushToBottom();
            }

            return true;
        }

        var window = await CreateWidgetFromConfigAsync(config);
        if (reveal)
        {
            window.RevealFromTray();
        }

        return true;
    }

    /// <summary>
    /// Show or hide all currently managed widgets.
    /// </summary>
    public async Task SetAllWidgetsVisibleAsync(bool visible)
    {
        if (visible)
        {
            foreach (var widget in _settingsService.Settings.Widgets
                         .Where(widget => widget.WidgetKind == WidgetKind.File && !widget.IsDisabled && !IsDeleted(widget.Id))
                         .ToList())
            {
                await ShowWidgetAsync(widget.Id, reveal: false);
            }

            return;
        }

        foreach (var (_, (window, _)) in _widgets.ToList())
        {
            window.HideWindow();
        }
    }

    /// <summary>
    /// Remove a widget and close its window.
    /// </summary>
    public async Task RemoveWidgetAsync(string widgetId)
    {
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

    public async Task<bool> EnableManagedStorageAsync(string widgetId)
    {
        var config = FindConfig(widgetId);
        if (config is null || config.WidgetKind != WidgetKind.File || IsDeleted(widgetId))
        {
            return false;
        }

        if (_widgets.TryGetValue(widgetId, out var entry))
        {
            string managedFolderName = string.IsNullOrWhiteSpace(config.ManagedFolderName)
                ? CreateManagedFolderName(config.Name, widgetId)
                : config.ManagedFolderName;
            string folderPath = BuildManagedFolderPath(managedFolderName);
            Directory.CreateDirectory(folderPath);

            await entry.ViewModel.EnableManagedStorageAsync(folderPath, managedFolderName);
            return true;
        }

        string hiddenFolderName = string.IsNullOrWhiteSpace(config.ManagedFolderName)
            ? CreateManagedFolderName(config.Name, widgetId)
            : config.ManagedFolderName;
        string hiddenFolderPath = BuildManagedFolderPath(hiddenFolderName);
        Directory.CreateDirectory(hiddenFolderPath);

        var importPaths = string.IsNullOrEmpty(config.MappedFolderPath)
            ? config.Items
                .Select(item => item.Path)
                .Where(path => !string.IsNullOrWhiteSpace(path) && (File.Exists(path) || Directory.Exists(path)))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList()
            : [];

        if (importPaths.Count > 0)
        {
            await _fileService.TransferItemsAsync(importPaths, hiddenFolderPath, ShouldMoveManagedItems());
        }

        config.MappedFolderPath = hiddenFolderPath;
        config.FollowsDefaultStoragePath = true;
        config.ManagedFolderName = hiddenFolderName;
        config.Items.Clear();
        await _settingsService.SaveAsync();
        return true;
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
            baseFolderName = "收纳组件";
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

    private async Task<WidgetWindow> CreateWidgetFromConfigAsync(WidgetConfig config)
    {
        if (_widgets.TryGetValue(config.Id, out var existing))
        {
            return existing.Window;
        }

        config.WidgetKind = WidgetKind.File;
        config.IsDisabled = false;
        NormalizeWidgetBounds(config);

        var dispatcherQueue = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        var viewModel = new WidgetViewModel(config, _fileService, _organizerService, _settingsService, dispatcherQueue);
        var window = new WidgetWindow(viewModel, _settingsService);

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
            window.Activate();
            window.PushToBottom();
            await viewModel.InitializeAsync();
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
