using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DeskBox.Models;
using DeskBox.Services;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace DeskBox.ViewModels;

/// <summary>
/// ViewModel for a single desktop organizer widget.
/// </summary>
public partial class WidgetViewModel : ObservableObject, IDisposable
{
    private const int IncrementalRefreshBatchThreshold = 24;

    private readonly DispatcherQueue _dispatcherQueue;
    private readonly FileService _fileService;
    private readonly OrganizerService _organizerService;
    private readonly FolderWatcherService _folderWatcher;
    private readonly FolderWatcherService _publicFolderWatcher;
    private readonly SettingsService _settingsService;
    private readonly SemaphoreSlim _folderRefreshGate = new(1, 1);

    private string _name = string.Empty;
    private ViewMode _viewMode;
    private bool _isLoading;
    private string? _mappedFolderPath;
    private double _widgetOpacity;
    private string _iconGlyph = string.Empty;
    private Visibility _manualModeVisibility;
    private Visibility _iconViewVisibility;
    private Visibility _listViewVisibility;
    private Visibility _loadingVisibility;
    private Visibility _topAddButtonVisibility;
    private bool _isIconMode;
    private bool _isListMode;
    private bool _hideShortcutArrowOverlay;
    private string _modeLabel = string.Empty;
    private string _emptyStateTitle = string.Empty;
    private string _emptyStateText = string.Empty;
    private string _emptyStateGlyph = string.Empty;
    private bool _isPositionLocked;
    private bool _isSizeLocked;
    private double _iconTileWidth;
    private double _iconTileHeight;
    private Thickness _iconTileMargin;
    private Thickness _iconTilePadding;
    private double _iconContentSpacing;
    private double _iconImageSize;
    private double _iconLabelFontSize;
    private Thickness _listItemMargin;
    private Thickness _listItemPadding;
    private double _listIconSize;
    private double _listLabelFontSize;

    public string Name
    {
        get => _name;
        set => SetProperty(ref _name, value);
    }

    public ViewMode ViewMode
    {
        get => _viewMode;
        set
        {
            if (SetProperty(ref _viewMode, value))
            {
                UpdateDependentProperties();
            }
        }
    }

    public bool IsLoading
    {
        get => _isLoading;
        set
        {
            if (SetProperty(ref _isLoading, value))
            {
                UpdateDependentProperties();
            }
        }
    }

    public string? MappedFolderPath
    {
        get => _mappedFolderPath;
        set
        {
            if (SetProperty(ref _mappedFolderPath, value))
            {
                UpdateDependentProperties();
            }
        }
    }

    public double WidgetOpacity
    {
        get => _widgetOpacity;
        set => SetProperty(ref _widgetOpacity, value);
    }

    public string IconGlyph
    {
        get => _iconGlyph;
        set => SetProperty(ref _iconGlyph, value);
    }

    public Visibility ManualModeVisibility
    {
        get => _manualModeVisibility;
        set => SetProperty(ref _manualModeVisibility, value);
    }

    public Visibility IconViewVisibility
    {
        get => _iconViewVisibility;
        set => SetProperty(ref _iconViewVisibility, value);
    }

    public Visibility ListViewVisibility
    {
        get => _listViewVisibility;
        set => SetProperty(ref _listViewVisibility, value);
    }

    public Visibility LoadingVisibility
    {
        get => _loadingVisibility;
        set => SetProperty(ref _loadingVisibility, value);
    }

    public Visibility TopAddButtonVisibility
    {
        get => _topAddButtonVisibility;
        set => SetProperty(ref _topAddButtonVisibility, value);
    }

    public bool IsIconMode
    {
        get => _isIconMode;
        set => SetProperty(ref _isIconMode, value);
    }

    public bool IsListMode
    {
        get => _isListMode;
        set => SetProperty(ref _isListMode, value);
    }

    public string ModeLabel
    {
        get => _modeLabel;
        set => SetProperty(ref _modeLabel, value);
    }

    public string EmptyStateTitle
    {
        get => _emptyStateTitle;
        set => SetProperty(ref _emptyStateTitle, value);
    }

    public string EmptyStateText
    {
        get => _emptyStateText;
        set => SetProperty(ref _emptyStateText, value);
    }

    public string EmptyStateGlyph
    {
        get => _emptyStateGlyph;
        set => SetProperty(ref _emptyStateGlyph, value);
    }

    public bool IsPositionLocked
    {
        get => _isPositionLocked;
        set => SetProperty(ref _isPositionLocked, value);
    }

    public bool IsSizeLocked
    {
        get => _isSizeLocked;
        set => SetProperty(ref _isSizeLocked, value);
    }

    public bool FollowsDefaultStoragePath => Config.FollowsDefaultStoragePath;

    public double IconTileWidth
    {
        get => _iconTileWidth;
        set => SetProperty(ref _iconTileWidth, value);
    }

    public double IconTileHeight
    {
        get => _iconTileHeight;
        set => SetProperty(ref _iconTileHeight, value);
    }

    public Thickness IconTileMargin
    {
        get => _iconTileMargin;
        set => SetProperty(ref _iconTileMargin, value);
    }

    public Thickness IconTilePadding
    {
        get => _iconTilePadding;
        set => SetProperty(ref _iconTilePadding, value);
    }

    public double IconContentSpacing
    {
        get => _iconContentSpacing;
        set => SetProperty(ref _iconContentSpacing, value);
    }

    public double IconImageSize
    {
        get => _iconImageSize;
        set => SetProperty(ref _iconImageSize, value);
    }

    public double IconLabelFontSize
    {
        get => _iconLabelFontSize;
        set => SetProperty(ref _iconLabelFontSize, value);
    }

    public Thickness ListItemMargin
    {
        get => _listItemMargin;
        set => SetProperty(ref _listItemMargin, value);
    }

    public Thickness ListItemPadding
    {
        get => _listItemPadding;
        set => SetProperty(ref _listItemPadding, value);
    }

    public double ListIconSize
    {
        get => _listIconSize;
        set => SetProperty(ref _listIconSize, value);
    }

    public double ListLabelFontSize
    {
        get => _listLabelFontSize;
        set => SetProperty(ref _listLabelFontSize, value);
    }

    public ObservableCollection<WidgetItem> Items { get; } = [];

    public WidgetConfig Config { get; }

    public WidgetViewModel(
        WidgetConfig config,
        FileService fileService,
        OrganizerService organizerService,
        SettingsService settingsService,
        DispatcherQueue dispatcherQueue)
    {
        _dispatcherQueue = dispatcherQueue;
        _fileService = fileService;
        _organizerService = organizerService;
        _settingsService = settingsService;

        Config = config;
        Config.WidgetKind = WidgetKind.File;
        Config.IsDisabled = false;

        _name = config.Name;
        _viewMode = config.ViewMode;
        _mappedFolderPath = config.MappedFolderPath;
        _widgetOpacity = Math.Clamp(
            _settingsService.Settings.WidgetOpacity,
            SettingsService.MinWidgetOpacity,
            SettingsService.MaxWidgetOpacity);
        _hideShortcutArrowOverlay = _settingsService.Settings.HideShortcutArrowOverlay;
        _isPositionLocked = config.IsPositionLocked;
        _isSizeLocked = config.IsSizeLocked;

        ApplyLayoutSettings();
        UpdateDependentProperties();

        _folderWatcher = new FolderWatcherService(dispatcherQueue);
        _folderWatcher.FolderChanged += OnFolderChanged;

        _publicFolderWatcher = new FolderWatcherService(dispatcherQueue);
        _publicFolderWatcher.FolderChanged += OnFolderChanged;

        _settingsService.SettingsChanged += OnSettingsChanged;
    }

    private void UpdateDependentProperties()
    {
        bool isMappedFolderMode = !string.IsNullOrEmpty(MappedFolderPath);
        string mappedFolderName = GetMappedFolderDisplayName();
        string managedAction = GetManagedActionText();

        IconGlyph = isMappedFolderMode ? "\uE8B7" : "\uE71D";
        ManualModeVisibility = isMappedFolderMode ? Visibility.Collapsed : Visibility.Visible;
        TopAddButtonVisibility = isMappedFolderMode ? Visibility.Collapsed : Visibility.Visible;
        IconViewVisibility = ViewMode == ViewMode.Icon ? Visibility.Visible : Visibility.Collapsed;
        ListViewVisibility = ViewMode == ViewMode.List ? Visibility.Visible : Visibility.Collapsed;
        IsIconMode = ViewMode == ViewMode.Icon;
        IsListMode = ViewMode == ViewMode.List;
        LoadingVisibility = IsLoading ? Visibility.Visible : Visibility.Collapsed;
        ModeLabel = !isMappedFolderMode
            ? "\u5f15\u7528"
            : FollowsDefaultStoragePath
                ? "\u6536\u7eb3"
                : "\u6620\u5c04";
        EmptyStateGlyph = isMappedFolderMode ? "\uE8B7" : "\uE71D";
        EmptyStateTitle = isMappedFolderMode ? "\u8fd9\u4e2a\u6587\u4ef6\u5939\u8fd8\u662f\u7a7a\u7684" : "\u6dfb\u52a0\u7b2c\u4e00\u4e2a\u9879\u76ee";
        EmptyStateText = isMappedFolderMode
            ? $"\u628a\u684c\u9762\u6587\u4ef6\u62d6\u5230\u8fd9\u91cc\uff0c\u4f1a\u81ea\u52a8{managedAction}\u5230 {mappedFolderName}\u3002"
            : "\u628a\u6587\u4ef6\u62d6\u5230\u8fd9\u91cc\uff0c\u6216\u4f7f\u7528\u53f3\u4e0a\u89d2\u7684\u6dfb\u52a0\u6309\u94ae\u3002";
    }

    private string GetManagedActionText()
    {
        return string.Equals(_settingsService.Settings.ManagedDropAction, SettingsService.ManagedDropActionCopy, StringComparison.OrdinalIgnoreCase)
            ? "\u590d\u5236"
            : "\u79fb\u52a8";
    }

    private bool ShouldMoveManagedItems()
    {
        return !string.Equals(_settingsService.Settings.ManagedDropAction, SettingsService.ManagedDropActionCopy, StringComparison.OrdinalIgnoreCase);
    }

    private void ApplyLayoutSettings()
    {
        var settings = _settingsService.Settings;
        double iconSize = Math.Clamp(settings.IconSize, SettingsService.MinIconSize, SettingsService.MaxIconSize);
        double textSize = Math.Clamp(settings.TextSize, SettingsService.MinTextSize, SettingsService.MaxTextSize);
        double densityScale = Math.Clamp(
            settings.LayoutDensityScale,
            SettingsService.MinLayoutDensityScale,
            SettingsService.MaxLayoutDensityScale);
        double t = (densityScale - SettingsService.MinLayoutDensityScale) /
                   (SettingsService.MaxLayoutDensityScale - SettingsService.MinLayoutDensityScale);

        IconTileWidth = Math.Max(iconSize + Lerp(18, 34, t), (textSize * 5.8) + 10);
        IconTileHeight = iconSize + Lerp(40, 72, t);
        IconTileMargin = new Thickness(Lerp(0, 2, t));
        IconTilePadding = new Thickness(
            Lerp(3, 6, t),
            Lerp(3, 6, t),
            Lerp(3, 6, t),
            Lerp(2, 6, t));
        IconContentSpacing = Lerp(3, 7, t);
        IconImageSize = iconSize;
        IconLabelFontSize = textSize;

        double listItemMarginY = Lerp(0, 2, t);
        ListItemMargin = new Thickness(0, listItemMarginY, 0, listItemMarginY);
        ListItemPadding = new Thickness(
            Lerp(6, 12, t),
            Lerp(4, 9, t),
            Lerp(6, 12, t),
            Lerp(4, 9, t));
        ListIconSize = Math.Clamp(Math.Round(iconSize * 0.72), 16, 40);
        ListLabelFontSize = textSize;
    }

    private static double Lerp(double min, double max, double t)
    {
        return min + ((max - min) * t);
    }

    private string GetMappedFolderDisplayName()
    {
        if (string.IsNullOrWhiteSpace(MappedFolderPath))
        {
            return "\u5f53\u524d\u4f4d\u7f6e";
        }

        var (userDesktop, publicDesktop) = FileService.GetDesktopPaths();
        if (MappedFolderPath.Equals(userDesktop, StringComparison.OrdinalIgnoreCase) ||
            MappedFolderPath.Equals(publicDesktop, StringComparison.OrdinalIgnoreCase))
        {
            return "\u684c\u9762";
        }

        string folderName = Path.GetFileName(MappedFolderPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        return string.IsNullOrWhiteSpace(folderName) ? MappedFolderPath : folderName;
    }

    private void OnSettingsChanged()
    {
        if (_dispatcherQueue.HasThreadAccess)
        {
            _ = ApplySettingsChangesAsync();
            return;
        }

        _dispatcherQueue.TryEnqueue(async () => await ApplySettingsChangesAsync());
    }

    private async Task ApplySettingsChangesAsync()
    {
        WidgetOpacity = Math.Clamp(
            _settingsService.Settings.WidgetOpacity,
            SettingsService.MinWidgetOpacity,
            SettingsService.MaxWidgetOpacity);
        ApplyLayoutSettings();
        UpdateDependentProperties();

        bool hideShortcutArrowOverlay = _settingsService.Settings.HideShortcutArrowOverlay;
        if (_hideShortcutArrowOverlay == hideShortcutArrowOverlay)
        {
            return;
        }

        _hideShortcutArrowOverlay = hideShortcutArrowOverlay;
        await RefreshShortcutIconsAsync();
    }

    /// <summary>
    /// Initialize the widget by loading its current content.
    /// </summary>
    public async Task InitializeAsync()
    {
        IsLoading = true;
        try
        {
            if (!string.IsNullOrEmpty(MappedFolderPath))
            {
                await LoadFolderContentsAsync(MappedFolderPath);
                ConfigureFolderWatchers(MappedFolderPath);
            }
            else
            {
                await LoadManualItemsAsync();
                ConfigureFolderWatchers(null);
            }
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>
    /// Add file or folder items to the widget, or move/copy them into a mapped folder.
    /// </summary>
    [RelayCommand]
    public async Task AddItemsAsync(IEnumerable<string> paths)
    {
        await ImportPathsAsync(paths);
    }

    public async Task ImportPathsAsync(IEnumerable<string> paths, bool? moveWhenMapped = null)
    {
        var normalizedPaths = paths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (normalizedPaths.Count == 0)
        {
            return;
        }

        if (!string.IsNullOrEmpty(MappedFolderPath))
        {
            bool shouldMove = moveWhenMapped ?? ShouldMoveManagedItems();
            var historyEntry = await _organizerService.OrganizeDropAsync(Config, Name, normalizedPaths, shouldMove);

            foreach (var sourcePath in historyEntry.Items.Select(item => item.SourcePath))
            {
                if (Path.GetDirectoryName(sourcePath)?.Equals(MappedFolderPath, StringComparison.OrdinalIgnoreCase) == true)
                {
                    RemoveItemByPath(sourcePath);
                }
            }

            foreach (var destinationPath in historyEntry.Items.Select(item => item.DestinationPath))
            {
                await UpsertFolderItemAsync(destinationPath);
            }

            return;
        }

        int nextOrder = Items.Count > 0 ? Items.Max(item => item.SortOrder) + 1 : 0;
        foreach (var path in normalizedPaths)
        {
            if (Items.Any(item => item.Path.Equals(path, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            var item = await _fileService.CreateWidgetItemAsync(path, _hideShortcutArrowOverlay);
            item.SortOrder = nextOrder++;
            Items.Add(item);
        }

        SyncConfigItems();
    }

    /// <summary>
    /// Remove an item from a manual widget.
    /// </summary>
    [RelayCommand]
    public void RemoveItem(WidgetItem item)
    {
        if (!string.IsNullOrEmpty(MappedFolderPath))
        {
            return;
        }

        Items.Remove(item);
        SyncConfigItems();
    }

    /// <summary>
    /// Toggle between icon and list views.
    /// </summary>
    [RelayCommand]
    public void ToggleViewMode()
    {
        ViewMode = ViewMode == ViewMode.Icon ? ViewMode.List : ViewMode.Icon;
        Config.ViewMode = ViewMode;
        _settingsService.SaveDebounced();
    }

    /// <summary>
    /// Open an item using the default shell handler.
    /// </summary>
    [RelayCommand]
    public void OpenItem(WidgetItem item)
    {
        FileService.OpenItem(item);
    }

    /// <summary>
    /// Reveal an item in Explorer.
    /// </summary>
    [RelayCommand]
    public void ShowInExplorer(WidgetItem item)
    {
        FileService.ShowInExplorer(item);
    }

    /// <summary>
    /// Map this widget to a folder.
    /// </summary>
    public async Task MapToFolderAsync(string folderPath)
    {
        await ApplyMappedFolderConfigurationAsync(folderPath, followsDefaultStoragePath: false, managedFolderName: null);
    }

    public async Task MoveItemBackToDesktopAsync(WidgetItem item)
    {
        if (string.IsNullOrWhiteSpace(MappedFolderPath))
        {
            return;
        }

        await _organizerService.MoveItemBackToDesktopAsync(Config, Name, item);
        RemoveItemByPath(item.Path);
    }

    public async Task EnableManagedStorageAsync(string folderPath, string managedFolderName)
    {
        var importPaths = string.IsNullOrEmpty(MappedFolderPath)
            ? Items.Select(item => item.Path)
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList()
            : [];

        if (importPaths.Count > 0)
        {
            await _fileService.TransferItemsAsync(importPaths, folderPath, ShouldMoveManagedItems());
        }

        await ApplyMappedFolderConfigurationAsync(folderPath, followsDefaultStoragePath: true, managedFolderName);
    }

    public async Task RefreshFromConfigAsync()
    {
        Config.WidgetKind = WidgetKind.File;
        Config.IsDisabled = false;

        MappedFolderPath = Config.MappedFolderPath;
        OnPropertyChanged(nameof(FollowsDefaultStoragePath));

        if (!string.IsNullOrEmpty(MappedFolderPath))
        {
            await LoadFolderContentsAsync(MappedFolderPath);
        }
        else
        {
            await LoadManualItemsAsync();
        }

        ConfigureFolderWatchers(MappedFolderPath);
        UpdateDependentProperties();
    }

    public Task HandleItemsMovedOutAsync(IEnumerable<string> sourcePaths)
    {
        var normalizedPaths = sourcePaths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (normalizedPaths.Count == 0)
        {
            return Task.CompletedTask;
        }

        if (!string.IsNullOrEmpty(MappedFolderPath))
        {
            foreach (var path in normalizedPaths)
            {
                RemoveItemByPath(path);
            }

            return Task.CompletedTask;
        }

        var removedItems = Items
            .Where(item => normalizedPaths.Contains(item.Path))
            .ToList();

        if (removedItems.Count == 0)
        {
            return Task.CompletedTask;
        }

        foreach (var item in removedItems)
        {
            Items.Remove(item);
        }

        SyncConfigItems();
        return Task.CompletedTask;
    }

    public async Task RenameItemAsync(WidgetItem item, string newName)
    {
        ArgumentNullException.ThrowIfNull(item);

        string sanitizedName = FileService.SanitizeFileSystemName(newName);
        if (string.IsNullOrWhiteSpace(sanitizedName))
        {
            throw new InvalidOperationException("名称不能为空。");
        }

        string sourcePath = Path.GetFullPath(item.Path);
        string? parentDirectory = Path.GetDirectoryName(sourcePath);
        if (string.IsNullOrWhiteSpace(parentDirectory))
        {
            throw new InvalidOperationException("无法确定当前项目所在的文件夹。");
        }

        string extension = item.IsFolder ? string.Empty : Path.GetExtension(sourcePath);
        string destinationPath = Path.Combine(parentDirectory, sanitizedName + extension);
        if (string.Equals(sourcePath, destinationPath, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (File.Exists(destinationPath) || Directory.Exists(destinationPath))
        {
            throw new IOException("目标名称已存在。");
        }

        await _fileService.RelocateEntryAsync(sourcePath, destinationPath);
        var refreshedItem = await _fileService.CreateWidgetItemAsync(destinationPath, _hideShortcutArrowOverlay);
        ApplyRuntimeItemData(item, refreshedItem);

        if (string.IsNullOrEmpty(MappedFolderPath))
        {
            SyncConfigItems();
            return;
        }

        int originalIndex = Items.IndexOf(item);
        if (originalIndex >= 0)
        {
            Items.RemoveAt(originalIndex);
            Items.Insert(GetSortedInsertIndex(item), item);
            NormalizeSortOrder();
        }
    }

    public async Task DeleteItemsAsync(IEnumerable<WidgetItem> items)
    {
        var targets = items
            .Where(item => item is not null)
            .Distinct()
            .ToList();

        if (targets.Count == 0)
        {
            return;
        }

        foreach (var item in targets)
        {
            await _fileService.DeleteEntryAsync(item.Path, recycle: true);
        }

        foreach (var item in targets)
        {
            Items.Remove(item);
        }

        NormalizeSortOrder();

        if (string.IsNullOrEmpty(MappedFolderPath))
        {
            SyncConfigItems();
        }
    }

    public void UpdateBounds(double x, double y, double width, double height, bool persist)
    {
        Config.X = x;
        Config.Y = y;
        Config.Width = width;
        Config.Height = height;

        if (persist)
        {
            _settingsService.UpdateWidget(Config, notifySubscribers: false);
        }
    }

    /// <summary>
    /// Rename the widget.
    /// </summary>
    public void Rename(string newName)
    {
        Name = newName;
        Config.Name = newName;
        _settingsService.UpdateWidget(Config);
    }

    public void SetPositionLocked(bool value)
    {
        if (IsPositionLocked == value)
        {
            return;
        }

        IsPositionLocked = value;
        Config.IsPositionLocked = value;
        _settingsService.UpdateWidget(Config);
    }

    public void SetSizeLocked(bool value)
    {
        if (IsSizeLocked == value)
        {
            return;
        }

        IsSizeLocked = value;
        Config.IsSizeLocked = value;
        _settingsService.UpdateWidget(Config);
    }

    [RelayCommand]
    public void TogglePositionLock()
    {
        SetPositionLocked(!IsPositionLocked);
    }

    [RelayCommand]
    public void ToggleSizeLock()
    {
        SetSizeLocked(!IsSizeLocked);
    }

    private async Task LoadManualItemsAsync()
    {
        Items.Clear();

        foreach (var itemConfig in Config.Items.OrderBy(item => item.SortOrder))
        {
            if (!File.Exists(itemConfig.Path) && !Directory.Exists(itemConfig.Path))
            {
                continue;
            }

            var item = await _fileService.CreateWidgetItemAsync(itemConfig.Path, _hideShortcutArrowOverlay);
            item.SortOrder = itemConfig.SortOrder;
            Items.Add(item);
        }
    }

    private async Task LoadFolderContentsAsync(string folderPath)
    {
        Items.Clear();

        List<WidgetItem> items;
        var (userDesktop, publicDesktop) = FileService.GetDesktopPaths();
        if (folderPath.Equals(userDesktop, StringComparison.OrdinalIgnoreCase))
        {
            var userItems = await _fileService.EnumerateDirectoryAsync(userDesktop, _hideShortcutArrowOverlay);
            var publicItems = await _fileService.EnumerateDirectoryAsync(publicDesktop, _hideShortcutArrowOverlay);

            items = userItems.Concat(publicItems)
                .GroupBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .OrderBy(item => !item.IsFolder)
                .ThenBy(item => item.Name)
                .ToList();
        }
        else
        {
            items = await _fileService.EnumerateDirectoryAsync(folderPath, _hideShortcutArrowOverlay);
        }

        foreach (var item in items)
        {
            Items.Add(item);
        }
    }

    private async Task RefreshShortcutIconsAsync()
    {
        foreach (var item in Items.Where(item => item.IsShortcut))
        {
            item.Icon = await _fileService.GetIconAsync(item.Path, _hideShortcutArrowOverlay);
        }
    }

    private async Task ApplyMappedFolderConfigurationAsync(string folderPath, bool followsDefaultStoragePath, string? managedFolderName)
    {
        MappedFolderPath = folderPath;
        Config.MappedFolderPath = folderPath;
        Config.FollowsDefaultStoragePath = followsDefaultStoragePath;
        Config.ManagedFolderName = followsDefaultStoragePath ? managedFolderName : null;
        Config.WidgetKind = WidgetKind.File;
        Config.Items.Clear();

        OnPropertyChanged(nameof(FollowsDefaultStoragePath));
        UpdateDependentProperties();

        await LoadFolderContentsAsync(folderPath);
        ConfigureFolderWatchers(folderPath);
        _settingsService.SaveDebounced();
    }

    private void ConfigureFolderWatchers(string? folderPath)
    {
        _folderWatcher.Stop();
        _publicFolderWatcher.Stop();

        if (string.IsNullOrEmpty(folderPath))
        {
            return;
        }

        _folderWatcher.Start(folderPath);

        var (userDesktop, publicDesktop) = FileService.GetDesktopPaths();
        if (folderPath.Equals(userDesktop, StringComparison.OrdinalIgnoreCase))
        {
            _publicFolderWatcher.Start(publicDesktop);
        }
    }

    private async void OnFolderChanged(FolderChangeBatch changeBatch)
    {
        if (string.IsNullOrEmpty(MappedFolderPath))
        {
            return;
        }

        await _folderRefreshGate.WaitAsync();
        try
        {
            if (string.IsNullOrEmpty(MappedFolderPath))
            {
                return;
            }

            if (ShouldUseFullReload(changeBatch, MappedFolderPath))
            {
                await LoadFolderContentsAsync(MappedFolderPath);
                return;
            }

            foreach (var change in changeBatch.Changes)
            {
                await ApplyFolderChangeAsync(change);
            }
        }
        catch (Exception ex)
        {
            App.Log($"[FolderRefresh] Incremental refresh failed for '{MappedFolderPath}': {ex}");
            if (!string.IsNullOrEmpty(MappedFolderPath))
            {
                await LoadFolderContentsAsync(MappedFolderPath);
            }
        }
        finally
        {
            _folderRefreshGate.Release();
        }
    }

    private bool ShouldUseFullReload(FolderChangeBatch changeBatch, string mappedFolderPath)
    {
        if (changeBatch.RequiresFullReload || changeBatch.Changes.Count == 0 || changeBatch.Changes.Count > IncrementalRefreshBatchThreshold)
        {
            return true;
        }

        var (userDesktop, publicDesktop) = FileService.GetDesktopPaths();
        if (mappedFolderPath.Equals(userDesktop, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!changeBatch.WatchedPath.Equals(mappedFolderPath, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return mappedFolderPath.Equals(publicDesktop, StringComparison.OrdinalIgnoreCase) &&
               changeBatch.Changes.Any(change => !FileService.IsPathUnderDirectory(change.FullPath, mappedFolderPath));
    }

    private async Task ApplyFolderChangeAsync(FolderChange change)
    {
        if (change.ChangeType == WatcherChangeTypes.Renamed && !string.IsNullOrWhiteSpace(change.OldFullPath))
        {
            RemoveItemByPath(change.OldFullPath);
            await UpsertFolderItemAsync(change.FullPath);
            return;
        }

        if (change.ChangeType == WatcherChangeTypes.Deleted)
        {
            RemoveItemByPath(change.FullPath);
            return;
        }

        await UpsertFolderItemAsync(change.FullPath);
    }

    private async Task UpsertFolderItemAsync(string path)
    {
        var item = await _fileService.TryCreateWidgetItemAsync(path, _hideShortcutArrowOverlay);
        if (item is null)
        {
            RemoveItemByPath(path);
            return;
        }

        int existingIndex = FindItemIndexByPath(path);
        if (existingIndex >= 0)
        {
            Items.RemoveAt(existingIndex);
        }

        item.SortOrder = GetSortedInsertIndex(item);
        Items.Insert(GetSortedInsertIndex(item), item);
        NormalizeSortOrder();
    }

    private void RemoveItemByPath(string path)
    {
        int index = FindItemIndexByPath(path);
        if (index < 0)
        {
            return;
        }

        Items.RemoveAt(index);
        NormalizeSortOrder();
    }

    private int FindItemIndexByPath(string path)
    {
        for (int index = 0; index < Items.Count; index++)
        {
            if (string.Equals(Items[index].Path, path, StringComparison.OrdinalIgnoreCase))
            {
                return index;
            }
        }

        return -1;
    }

    private int GetSortedInsertIndex(WidgetItem candidate)
    {
        for (int index = 0; index < Items.Count; index++)
        {
            if (CompareItems(candidate, Items[index]) < 0)
            {
                return index;
            }
        }

        return Items.Count;
    }

    private void NormalizeSortOrder()
    {
        for (int index = 0; index < Items.Count; index++)
        {
            Items[index].SortOrder = index;
        }
    }

    private static void ApplyRuntimeItemData(WidgetItem target, WidgetItem source)
    {
        target.Name = source.Name;
        target.Path = source.Path;
        target.TargetPath = source.TargetPath;
        target.Icon = source.Icon;
        target.FileSize = source.FileSize;
        target.LastModified = source.LastModified;
        target.IsShortcut = source.IsShortcut;
        target.IsFolder = source.IsFolder;
    }

    private static int CompareItems(WidgetItem left, WidgetItem right)
    {
        if (left.IsFolder != right.IsFolder)
        {
            return left.IsFolder ? -1 : 1;
        }

        int nameComparison = StringComparer.CurrentCultureIgnoreCase.Compare(left.Name, right.Name);
        if (nameComparison != 0)
        {
            return nameComparison;
        }

        return StringComparer.OrdinalIgnoreCase.Compare(left.Path, right.Path);
    }

    /// <summary>
    /// Sync the in-memory items back to persisted widget config.
    /// </summary>
    private void SyncConfigItems()
    {
        Config.Items = Items.Select((item, index) => new WidgetItemConfig
        {
            Path = item.Path,
            SortOrder = index
        }).ToList();

        _settingsService.UpdateWidget(Config);
    }

    public void Dispose()
    {
        _folderWatcher.Dispose();
        _publicFolderWatcher.Dispose();
        _folderRefreshGate.Dispose();
        _settingsService.SettingsChanged -= OnSettingsChanged;
    }
}
