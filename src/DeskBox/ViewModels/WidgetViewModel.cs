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
    private readonly LocalizationService _localizationService;
    private readonly SemaphoreSlim _folderRefreshGate = new(1, 1);

    private string _name = string.Empty;
    private ViewMode _viewMode;
    private bool _isLoading;
    private string? _mappedFolderPath;
    private double _widgetOpacity;
    private string _iconGlyph = string.Empty;
    private Visibility _iconViewVisibility;
    private Visibility _listViewVisibility;
    private Visibility _loadingVisibility;
    private Visibility _topAddButtonVisibility;
    private bool _isIconMode;
    private bool _isListMode;
    private bool _hideShortcutArrowOverlay;
    private bool _showListItemDetails = true;
    private string _modeLabel = string.Empty;
    private string _modeDescription = string.Empty;
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
    private double _iconLabelMaxWidth;
    private double _iconLabelFontSize;
    private Thickness _listItemMargin;
    private Thickness _listItemPadding;
    private double _listIconSize;
    private double _listLabelFontSize;
    private bool _showFileExtensions;
    private bool _hideShortcutExtensionWhenShowingFileExtensions = true;

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

    public bool ShowListItemDetails
    {
        get => _showListItemDetails;
        set => SetProperty(ref _showListItemDetails, value);
    }

    public string ModeLabel
    {
        get => _modeLabel;
        set => SetProperty(ref _modeLabel, value);
    }

    public string ModeDescription
    {
        get => _modeDescription;
        set => SetProperty(ref _modeDescription, value);
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

    public double IconLabelMaxWidth
    {
        get => _iconLabelMaxWidth;
        set => SetProperty(ref _iconLabelMaxWidth, value);
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
        LocalizationService? localizationService,
        DispatcherQueue dispatcherQueue)
    {
        _dispatcherQueue = dispatcherQueue;
        _fileService = fileService;
        _organizerService = organizerService;
        _settingsService = settingsService;
        _localizationService = localizationService ?? new LocalizationService(settingsService);

        Config = config;
        Config.WidgetKind = WidgetKind.File;
        Config.IsDisabled = false;
        EnsureFolderBackedConfig();

        _name = config.Name;
        _viewMode = config.ViewMode;
        _mappedFolderPath = config.MappedFolderPath;
        _widgetOpacity = Math.Clamp(
            _settingsService.Settings.WidgetOpacity,
            SettingsService.MinWidgetOpacity,
            SettingsService.MaxWidgetOpacity);
        _hideShortcutArrowOverlay = _settingsService.Settings.HideShortcutArrowOverlay;
        _showFileExtensions = _settingsService.Settings.ShowFileExtensions;
        _hideShortcutExtensionWhenShowingFileExtensions =
            _settingsService.Settings.HideShortcutExtensionWhenShowingFileExtensions;
        _showListItemDetails = _settingsService.Settings.ShowListItemDetails;
        _isPositionLocked = config.IsPositionLocked;
        _isSizeLocked = config.IsSizeLocked;

        ApplyLayoutSettings();
        UpdateDependentProperties();

        _folderWatcher = new FolderWatcherService(dispatcherQueue);
        _folderWatcher.FolderChanged += OnFolderChanged;

        _publicFolderWatcher = new FolderWatcherService(dispatcherQueue);
        _publicFolderWatcher.FolderChanged += OnFolderChanged;

        _settingsService.SettingsChanged += OnSettingsChanged;
        _localizationService.LanguageChanged += OnLanguageChanged;
    }

    public WidgetViewModel(
        WidgetConfig config,
        FileService fileService,
        OrganizerService organizerService,
        SettingsService settingsService,
        DispatcherQueue dispatcherQueue)
        : this(config, fileService, organizerService, settingsService, null, dispatcherQueue)
    {
    }

    private void UpdateDependentProperties()
    {
        string mappedFolderName = GetMappedFolderDisplayName();
        string managedAction = GetManagedActionText();
        bool isManagedStorage = FollowsDefaultStoragePath;

        IconGlyph = isManagedStorage ? "\uE8B7" : "\uE71B";
        TopAddButtonVisibility = Visibility.Visible;
        IconViewVisibility = ViewMode == ViewMode.Icon ? Visibility.Visible : Visibility.Collapsed;
        ListViewVisibility = ViewMode == ViewMode.List ? Visibility.Visible : Visibility.Collapsed;
        IsIconMode = ViewMode == ViewMode.Icon;
        IsListMode = ViewMode == ViewMode.List;
        LoadingVisibility = IsLoading ? Visibility.Visible : Visibility.Collapsed;
        ModeLabel = isManagedStorage
            ? _localizationService.T("Widget.Mode.Managed")
            : _localizationService.T("Widget.Mode.Mapped");
        ModeDescription = isManagedStorage
            ? _localizationService.T("Widget.Mode.ManagedDescription")
            : _localizationService.T("Widget.Mode.MappedDescription");
        EmptyStateGlyph = IconGlyph;
        EmptyStateTitle = isManagedStorage
            ? _localizationService.T("Widget.Empty.ManagedTitle")
            : _localizationService.T("Widget.Empty.MappedTitle");
        EmptyStateText = isManagedStorage
            ? _localizationService.Format("Widget.Empty.ManagedText", managedAction, mappedFolderName)
            : _localizationService.Format("Widget.Empty.MappedText", mappedFolderName);
    }

    private string GetManagedActionText()
    {
        return string.Equals(_settingsService.Settings.ManagedDropAction, SettingsService.ManagedDropActionCopy, StringComparison.OrdinalIgnoreCase)
            ? _localizationService.T("Common.Copy")
            : _localizationService.T("Common.Move");
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
        double horizontalScale = Math.Clamp(
            settings.HorizontalSpacingScale,
            SettingsService.MinSpacingScale,
            SettingsService.MaxSpacingScale);
        double verticalScale = Math.Clamp(
            settings.VerticalSpacingScale,
            SettingsService.MinSpacingScale,
            SettingsService.MaxSpacingScale);
        double fileNameWidthScale = Math.Clamp(
            settings.FileNameWidthScale,
            SettingsService.MinSpacingScale,
            SettingsService.MaxSpacingScale);

        double horizontalT = NormalizeScale(horizontalScale, SettingsService.MinSpacingScale, SettingsService.MaxSpacingScale);
        double verticalT = NormalizeScale(verticalScale, SettingsService.MinSpacingScale, SettingsService.MaxSpacingScale);
        double nameWidthT = NormalizeScale(fileNameWidthScale, SettingsService.MinSpacingScale, SettingsService.MaxSpacingScale);

        double labelMaxWidth = Math.Max(iconSize, Lerp(iconSize, textSize * 10.5, nameWidthT));
        IconLabelMaxWidth = labelMaxWidth;
        IconTileWidth = Math.Max(iconSize + Lerp(6, 28, horizontalT), labelMaxWidth + Lerp(4, 16, horizontalT));
        IconTileHeight = iconSize + Lerp(24, 70, verticalT);
        IconTileMargin = new Thickness(
            Lerp(0, 2, horizontalT),
            Lerp(0, 2, verticalT),
            Lerp(0, 2, horizontalT),
            Lerp(0, 2, verticalT));
        IconTilePadding = new Thickness(
            Lerp(1, 5, horizontalT),
            Lerp(1, 6, verticalT),
            Lerp(1, 5, horizontalT),
            Lerp(1, 6, verticalT));
        IconContentSpacing = Lerp(1, 7, verticalT);
        IconImageSize = iconSize;
        IconLabelFontSize = textSize;

        const double listScale = 0.8;
        double listItemMarginY = Lerp(0, 2, verticalT);
        ListItemMargin = new Thickness(0, listItemMarginY * listScale, 0, listItemMarginY * listScale);
        ListItemPadding = new Thickness(
            Lerp(4, 12, horizontalT) * listScale,
            Lerp(2, 9, verticalT) * listScale,
            Lerp(4, 12, horizontalT) * listScale,
            Lerp(2, 9, verticalT) * listScale);
        ListIconSize = Math.Clamp(Math.Round(iconSize * 0.72 * listScale), 16, 32);
        ListLabelFontSize = textSize;
    }

    private static double Lerp(double min, double max, double t)
    {
        return min + ((max - min) * t);
    }

    private static double NormalizeScale(double value, double min, double max)
    {
        return Math.Abs(max - min) < 0.0001
            ? 0
            : (value - min) / (max - min);
    }

    private string GetMappedFolderDisplayName()
    {
        if (string.IsNullOrWhiteSpace(MappedFolderPath))
        {
            return _localizationService.T("Common.CurrentLocation");
        }

        var (userDesktop, publicDesktop) = FileService.GetDesktopPaths();
        if (MappedFolderPath.Equals(userDesktop, StringComparison.OrdinalIgnoreCase) ||
            MappedFolderPath.Equals(publicDesktop, StringComparison.OrdinalIgnoreCase))
        {
            return _localizationService.T("Common.Desktop");
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

    private void OnLanguageChanged()
    {
        if (_dispatcherQueue.HasThreadAccess)
        {
            UpdateDependentProperties();
            return;
        }

        _dispatcherQueue.TryEnqueue(UpdateDependentProperties);
    }

    private async Task ApplySettingsChangesAsync()
    {
        WidgetOpacity = Math.Clamp(
            _settingsService.Settings.WidgetOpacity,
            SettingsService.MinWidgetOpacity,
            SettingsService.MaxWidgetOpacity);
        ShowListItemDetails = _settingsService.Settings.ShowListItemDetails;
        ApplyLayoutSettings();
        UpdateDependentProperties();

        bool showFileExtensions = _settingsService.Settings.ShowFileExtensions;
        bool hideShortcutExtensionWhenShowingFileExtensions =
            _settingsService.Settings.HideShortcutExtensionWhenShowingFileExtensions;
        if (_showFileExtensions != showFileExtensions ||
            _hideShortcutExtensionWhenShowingFileExtensions != hideShortcutExtensionWhenShowingFileExtensions)
        {
            _showFileExtensions = showFileExtensions;
            _hideShortcutExtensionWhenShowingFileExtensions = hideShortcutExtensionWhenShowingFileExtensions;
            RefreshItemDisplayNames();
        }

        bool hideShortcutArrowOverlay = _settingsService.Settings.HideShortcutArrowOverlay;
        if (_hideShortcutArrowOverlay == hideShortcutArrowOverlay)
        {
            return;
        }

        _hideShortcutArrowOverlay = hideShortcutArrowOverlay;
        await RefreshShortcutIconsAsync();
    }

    public void ApplyAppearancePreview()
    {
        WidgetOpacity = Math.Clamp(
            _settingsService.Settings.WidgetOpacity,
            SettingsService.MinWidgetOpacity,
            SettingsService.MaxWidgetOpacity);
        ApplyLayoutSettings();
    }

    /// <summary>
    /// Initialize the widget by loading its current content.
    /// </summary>
    public async Task InitializeAsync()
    {
        IsLoading = true;
        try
        {
            EnsureFolderBackedConfig();
            MappedFolderPath = Config.MappedFolderPath;
            await LoadFolderContentsAsync(MappedFolderPath!);
            ConfigureFolderWatchers(MappedFolderPath);
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

    public async Task ImportPathsAsync(
        IEnumerable<string> paths,
        bool? moveWhenMapped = null,
        bool useShellProgress = false)
    {
        var normalizedPaths = paths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (normalizedPaths.Count == 0)
        {
            return;
        }

        EnsureFolderBackedConfig();
        MappedFolderPath = Config.MappedFolderPath;
        bool shouldMove = moveWhenMapped ?? ShouldMoveManagedItems();
        var historyEntry = await _organizerService.OrganizeDropAsync(Config, Name, normalizedPaths, shouldMove, useShellProgress);

        if (shouldMove)
        {
            foreach (var sourcePath in historyEntry.Items.Select(item => item.SourcePath))
            {
                if (Path.GetDirectoryName(sourcePath)?.Equals(MappedFolderPath, StringComparison.OrdinalIgnoreCase) == true)
                {
                    RemoveItemByPath(sourcePath);
                }
            }
        }

        foreach (var destinationPath in historyEntry.Items.Select(item => item.DestinationPath))
        {
            await UpsertFolderItemAsync(destinationPath);
        }
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

    public void OpenItem(WidgetItem item, IntPtr ownerHwnd)
    {
        if (FileService.OpenItem(item, ownerHwnd) == FileService.OpenItemResult.ShortcutDeleted)
        {
            RemoveItemByPath(item.Path);
        }
    }

    /// <summary>
    /// Reveal an item in Explorer.
    /// </summary>
    [RelayCommand]
    public void ShowInExplorer(WidgetItem item)
    {
        FileService.ShowInExplorer(item);
    }

    public async Task<int> MoveItemBackToDesktopAsync(WidgetItem item, bool useShellProgress = false)
    {
        if (string.IsNullOrWhiteSpace(MappedFolderPath))
        {
            return 0;
        }

        var historyEntry = await _organizerService.MoveItemBackToDesktopAsync(Config, Name, item, useShellProgress);
        if (historyEntry.Items.Any(entry => string.Equals(entry.SourcePath, item.Path, StringComparison.OrdinalIgnoreCase)))
        {
            RemoveItemByPath(item.Path);
            return 1;
        }

        return 0;
    }

    public async Task<int> MoveItemsBackToDesktopAsync(IEnumerable<WidgetItem> items, bool useShellProgress = false)
    {
        if (string.IsNullOrWhiteSpace(MappedFolderPath))
        {
            return 0;
        }

        var targets = items
            .Where(item => item is not null)
            .Distinct()
            .ToList();
        if (targets.Count == 0)
        {
            return 0;
        }

        var historyEntry = await _organizerService.MoveItemsBackToDesktopAsync(
            Config,
            Name,
            targets.Select(item => item.Path),
            useShellProgress);

        var movedSourcePaths = historyEntry.Items
            .Select(item => item.SourcePath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var item in targets.Where(item => movedSourcePaths.Contains(item.Path)))
        {
            RemoveItemByPath(item.Path);
        }

        return movedSourcePaths.Count;
    }

    public async Task RefreshFromConfigAsync()
    {
        using var perfScope = PerformanceLogger.Measure(
            "WidgetViewModel.RefreshFromConfig",
            $"id={Config.Id} name={Name}");

        Config.WidgetKind = WidgetKind.File;
        Config.IsDisabled = false;
        EnsureFolderBackedConfig();

        MappedFolderPath = Config.MappedFolderPath;
        OnPropertyChanged(nameof(FollowsDefaultStoragePath));

        await LoadFolderContentsAsync(MappedFolderPath!);
        ConfigureFolderWatchers(MappedFolderPath);
        UpdateDependentProperties();
    }

    public async Task UpdateMappedFolderPathAsync(string folderPath)
    {
        if (string.IsNullOrWhiteSpace(folderPath))
        {
            return;
        }

        string normalizedPath = Path.GetFullPath(folderPath);
        Directory.CreateDirectory(normalizedPath);

        Config.WidgetKind = WidgetKind.File;
        Config.IsDisabled = false;
        Config.FollowsDefaultStoragePath = false;
        Config.ManagedFolderName = null;
        Config.MappedFolderPath = normalizedPath;
        Config.Items.Clear();
        MappedFolderPath = normalizedPath;
        OnPropertyChanged(nameof(FollowsDefaultStoragePath));

        if (App.Current?.WidgetManager is { } widgetManager)
        {
            widgetManager.SyncMappedWidgetShortcut(Config.Id);
        }

        _settingsService.UpdateWidget(Config);
        await LoadFolderContentsAsync(normalizedPath);
        ConfigureFolderWatchers(normalizedPath);
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

        if (string.IsNullOrEmpty(MappedFolderPath))
        {
            return Task.CompletedTask;
        }

        foreach (var path in normalizedPaths)
        {
            RemoveItemByPath(path);
        }

        return Task.CompletedTask;
    }

    public async Task RenameItemAsync(WidgetItem item, string newName)
    {
        ArgumentNullException.ThrowIfNull(item);

        string sanitizedName = FileService.SanitizeFileSystemName(newName);
        if (string.IsNullOrWhiteSpace(sanitizedName))
        {
            throw new InvalidOperationException(_localizationService.T("Widget.Validation.NameRequired"));
        }

        string sourcePath = Path.GetFullPath(item.Path);
        string? parentDirectory = Path.GetDirectoryName(sourcePath);
        if (string.IsNullOrWhiteSpace(parentDirectory))
        {
            throw new InvalidOperationException(_localizationService.T("Widget.Validation.FolderUnknown"));
        }

        string extension = item.IsFolder ? string.Empty : Path.GetExtension(sourcePath);
        string destinationName = item.IsFolder
            ? sanitizedName
            : BuildRenameFileName(sanitizedName, extension);
        string destinationPath = Path.Combine(parentDirectory, destinationName);
        if (string.Equals(sourcePath, destinationPath, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (File.Exists(destinationPath) || Directory.Exists(destinationPath))
        {
            throw new IOException(_localizationService.T("Widget.Validation.TargetExists"));
        }

        await _fileService.RelocateEntryAsync(sourcePath, destinationPath);
        var refreshedItem = await _fileService.CreateWidgetItemAsync(
            destinationPath,
            _hideShortcutArrowOverlay,
            _showFileExtensions,
            _hideShortcutExtensionWhenShowingFileExtensions);
        ApplyRuntimeItemData(item, refreshedItem);

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

        var existingTargets = new List<WidgetItem>();
        foreach (var item in targets)
        {
            if (File.Exists(item.Path) || Directory.Exists(item.Path))
            {
                existingTargets.Add(item);
            }
            else
            {
                Items.Remove(item);
            }
        }

        foreach (var item in existingTargets)
        {
            await _fileService.DeleteEntryAsync(item.Path, recycle: true);
        }

        foreach (var item in existingTargets)
        {
            Items.Remove(item);
        }

        NormalizeSortOrder();
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
    public async Task RenameAsync(string newName)
    {
        Name = newName;
        Config.Name = newName;
        if (App.Current?.WidgetManager is { } widgetManager)
        {
            await widgetManager.RenameWidgetAsync(Config.Id, newName);
            return;
        }

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

    private void EnsureFolderBackedConfig()
    {
        if (!string.IsNullOrWhiteSpace(Config.MappedFolderPath))
        {
            Config.MappedFolderPath = Path.GetFullPath(Config.MappedFolderPath);
            return;
        }

        Config.FollowsDefaultStoragePath = true;
        Config.ManagedFolderName = string.IsNullOrWhiteSpace(Config.ManagedFolderName)
            ? CreateAvailableManagedFolderName(Config.Name, Config.Id)
            : Config.ManagedFolderName;
        Config.MappedFolderPath = Path.Combine(
            SettingsService.NormalizeManagedStorageRootPath(_settingsService.Settings.DefaultManagedStorageRootPath),
            Config.ManagedFolderName);
        Directory.CreateDirectory(Config.MappedFolderPath);
        Config.Items.Clear();
        _settingsService.SaveDebounced();
    }

    private string CreateAvailableManagedFolderName(string displayName, string widgetId)
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

    private async Task LoadFolderContentsAsync(string folderPath)
    {
        using var perfScope = PerformanceLogger.Measure(
            "WidgetViewModel.LoadFolderContents",
            $"id={Config.Id} path={folderPath}");

        Items.Clear();

        List<WidgetItem> items;
        var (userDesktop, publicDesktop) = FileService.GetDesktopPaths();
        if (folderPath.Equals(userDesktop, StringComparison.OrdinalIgnoreCase))
        {
            var userItems = await _fileService.EnumerateDirectoryAsync(
                userDesktop,
                _hideShortcutArrowOverlay,
                _showFileExtensions,
                _hideShortcutExtensionWhenShowingFileExtensions);
            var publicItems = await _fileService.EnumerateDirectoryAsync(
                publicDesktop,
                _hideShortcutArrowOverlay,
                _showFileExtensions,
                _hideShortcutExtensionWhenShowingFileExtensions);

            items = userItems.Concat(publicItems)
                .GroupBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .OrderBy(item => !item.IsFolder)
                .ThenBy(item => item.Name)
                .ToList();
        }
        else
        {
            items = await _fileService.EnumerateDirectoryAsync(
                folderPath,
                _hideShortcutArrowOverlay,
                _showFileExtensions,
                _hideShortcutExtensionWhenShowingFileExtensions);
        }

        foreach (var item in items)
        {
            Items.Add(item);
        }
    }

    private async Task RefreshShortcutIconsAsync()
    {
        int shortcutCount = Items.Count(item => item.IsShortcut);
        using var perfScope = PerformanceLogger.Measure(
            "WidgetViewModel.RefreshShortcutIcons",
            $"id={Config.Id} count={shortcutCount}");

        foreach (var item in Items.Where(item => item.IsShortcut))
        {
            item.Icon = await _fileService.GetIconAsync(item.Path, _hideShortcutArrowOverlay);
        }
    }

    private void RefreshItemDisplayNames()
    {
        foreach (var item in Items)
        {
            item.Name = FileService.GetDisplayName(
                item.Path,
                item.IsFolder,
                _showFileExtensions,
                _hideShortcutExtensionWhenShowingFileExtensions);
        }

        var sortedItems = Items.ToList();
        sortedItems.Sort(CompareItems);
        for (int targetIndex = 0; targetIndex < sortedItems.Count; targetIndex++)
        {
            var item = sortedItems[targetIndex];
            int currentIndex = Items.IndexOf(item);
            if (currentIndex >= 0 && currentIndex != targetIndex)
            {
                Items.Move(currentIndex, targetIndex);
            }
        }

        NormalizeSortOrder();
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

        using var perfScope = PerformanceLogger.Measure(
            "WidgetViewModel.OnFolderChanged",
            $"id={Config.Id} changes={changeBatch.Changes.Count} fullReload={changeBatch.RequiresFullReload}");

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
        var item = await _fileService.TryCreateWidgetItemAsync(
            path,
            _hideShortcutArrowOverlay,
            _showFileExtensions,
            _hideShortcutExtensionWhenShowingFileExtensions);
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
        target.FolderItemCount = source.FolderItemCount;
        target.LastModified = source.LastModified;
        target.IsShortcut = source.IsShortcut;
        target.IsFolder = source.IsFolder;
    }

    private string BuildRenameFileName(string sanitizedName, string extension)
    {
        if (string.IsNullOrWhiteSpace(extension))
        {
            return sanitizedName;
        }

        if (_showFileExtensions)
        {
            return sanitizedName.EndsWith(extension, StringComparison.OrdinalIgnoreCase)
                ? sanitizedName
                : sanitizedName + extension;
        }

        return sanitizedName + extension;
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

    public void Dispose()
    {
        _folderWatcher.Dispose();
        _publicFolderWatcher.Dispose();
        _folderRefreshGate.Dispose();
        _settingsService.SettingsChanged -= OnSettingsChanged;
        _localizationService.LanguageChanged -= OnLanguageChanged;
    }
}
