using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DeskBox.Helpers;
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
    private const int IconHydrationBatchSize = 8;
    private const int IconHydrationRetryCount = 3;
    private static readonly TimeSpan[] s_iconHydrationRetryDelays =
    [
        TimeSpan.FromMilliseconds(450),
        TimeSpan.FromMilliseconds(1200),
        TimeSpan.FromMilliseconds(2600)
    ];
    private const int FolderCountHydrationBatchSize = 8;
    private const int ShortcutTargetHydrationBatchSize = 4;
    private const int ShellKindHydrationBatchSize = 8;
    private const int FolderCountHydrationYieldMs = 24;

    private readonly DispatcherQueue _dispatcherQueue;
    private readonly FileService _fileService;
    private readonly OrganizerService _organizerService;
    private readonly FolderWatcherService _folderWatcher;
    private readonly FolderWatcherService _publicFolderWatcher;
    private readonly SettingsService _settingsService;
    private readonly LocalizationService _localizationService;
    private readonly SemaphoreSlim _folderRefreshGate = new(1, 1);
    private readonly WidgetSurfaceActivityTracker _surfaceActivity = new();
    private int _itemHydrationGeneration;
    private CancellationTokenSource? _itemHydrationCancellation;
    private int _iconDecodePixelWidth;
    private bool _isDisposed;
    private bool _surfaceBackgroundResourcesReleased;

    private string _name = string.Empty;
    private ViewMode _viewMode;
    private bool _isLoading;
    private string? _mappedFolderPath;
    private double _widgetOpacity;
    private string _iconGlyph = string.Empty;
    private string _titleIconKind = WidgetTitleIconKindNames.ManagedStorage;
    private Visibility _iconViewVisibility;
    private Visibility _listViewVisibility;
    private Visibility _loadingVisibility;
    private Visibility _topAddButtonVisibility;
    private bool _isIconMode;
    private bool _isListMode;
    private bool _hideShortcutArrowOverlay;
    private bool _showImageFilesAsIcons;
    private bool _showListItemDetails = true;
    private bool _showFileItemPathTooltips = true;
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
    private int _iconLabelMaxLines = SettingsService.DefaultFileNameLineCount;
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

    public bool IsInitialized { get; private set; }

    public FolderWatcherHealthSnapshot FolderWatcherHealth => _folderWatcher.Health;

    public FolderWatcherHealthSnapshot PublicFolderWatcherHealth => _publicFolderWatcher.Health;

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

    public string TitleIconKind
    {
        get => _titleIconKind;
        set => SetProperty(ref _titleIconKind, value);
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
        set
        {
            if (SetProperty(ref _showListItemDetails, value))
            {
                OnPropertyChanged(nameof(ListItemDetailVisibility));
            }
        }
    }

    public Visibility ListItemDetailVisibility =>
        ShowListItemDetails ? Visibility.Visible : Visibility.Collapsed;

    public bool ShowFileItemPathTooltips
    {
        get => _showFileItemPathTooltips;
        set => SetProperty(ref _showFileItemPathTooltips, value);
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
        set
        {
            if (SetProperty(ref _listLabelFontSize, value))
            {
                OnPropertyChanged(nameof(ListItemDetailFontSize));
            }
        }
    }

    public int IconLabelMaxLines
    {
        get => _iconLabelMaxLines;
        set => SetProperty(ref _iconLabelMaxLines, value);
    }

    public double ListItemDetailFontSize =>
        Math.Max(ListLabelFontSize - 2, 9);

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
        _currentFolderPath = config.MappedFolderPath;
        _widgetOpacity = Math.Clamp(
            _settingsService.Settings.WidgetOpacity,
            SettingsService.MinWidgetOpacity,
            SettingsService.MaxWidgetOpacity);
        _hideShortcutArrowOverlay = _settingsService.Settings.HideShortcutArrowOverlay;
        _showImageFilesAsIcons = _settingsService.Settings.ShowImageFilesAsIcons;
        _showFileExtensions = _settingsService.Settings.ShowFileExtensions;
        _hideShortcutExtensionWhenShowingFileExtensions =
            _settingsService.Settings.HideShortcutExtensionWhenShowingFileExtensions;
        _showListItemDetails = _settingsService.Settings.ShowListItemDetails;
        _showFileItemPathTooltips = _settingsService.Settings.ShowFileItemPathTooltips;
        _isPositionLocked = config.IsPositionLocked;
        _isSizeLocked = config.IsSizeLocked;

        ApplyLayoutSettings();
        UpdateDependentProperties();

        _folderWatcher = new FolderWatcherService(dispatcherQueue);
        _folderWatcher.FolderChanged += OnFolderChanged;
        _folderWatcher.FolderIconChanged += OnFolderIconChanged;

        _publicFolderWatcher = new FolderWatcherService(dispatcherQueue);
        _publicFolderWatcher.FolderChanged += OnFolderChanged;
        _publicFolderWatcher.FolderIconChanged += OnFolderIconChanged;

        _settingsService.SettingsChanged += OnSettingsChanged;
        _localizationService.LanguageChanged += OnLanguageChanged;
        InitializeStacks();
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

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        Interlocked.Exchange(
            ref _folderNavigationCancellation,
            null)?.Cancel();
        Interlocked.Increment(ref _itemHydrationGeneration);
        CancelItemHydration(
            Interlocked.Exchange(
                ref _itemHydrationCancellation,
                null));
        CleanupStacks();
        _folderWatcher.FolderChanged -= OnFolderChanged;
        _folderWatcher.FolderIconChanged -= OnFolderIconChanged;
        _publicFolderWatcher.FolderChanged -= OnFolderChanged;
        _publicFolderWatcher.FolderIconChanged -= OnFolderIconChanged;
        _folderWatcher.Dispose();
        _publicFolderWatcher.Dispose();
        // A debounced file-system callback may already be waiting on this gate.
        // Do not dispose it underneath that continuation; the view-model is
        // short-lived after this point and the waiter exits via _isDisposed.
        _settingsService.SettingsChanged -= OnSettingsChanged;
        _localizationService.LanguageChanged -= OnLanguageChanged;
    }

    public void SuspendBackgroundActivity()
    {
        if (_isDisposed)
        {
            return;
        }

        // A tray hide is usually brief. Keep the native/WinRT folder watchers
        // alive and coalesce any hidden changes instead of disposing and
        // recreating every watcher on each reveal.
        _surfaceActivity.Suspend();
    }

    public void ReleaseBackgroundActivityForLongHide()
    {
        if (_isDisposed || _surfaceBackgroundResourcesReleased)
        {
            return;
        }

        _surfaceActivity.Suspend();
        _surfaceBackgroundResourcesReleased = true;
        Interlocked.Increment(ref _itemHydrationGeneration);
        CancelItemHydration(
            Interlocked.Exchange(
                ref _itemHydrationCancellation,
                null));
        _folderWatcher.Stop();
        _publicFolderWatcher.Stop();
    }

    public bool ResumeBackgroundActivity()
    {
        if (_isDisposed || !_surfaceActivity.IsSuspended)
        {
            return false;
        }

        bool requiresReconciliation = _surfaceActivity.Resume();
        if (!_surfaceBackgroundResourcesReleased)
        {
            return requiresReconciliation;
        }

        _surfaceBackgroundResourcesReleased = false;
        if (!string.IsNullOrWhiteSpace(CurrentFolderPath))
        {
            string folderPath = CurrentFolderPath;
            _ = ResumeBackgroundActivityAsync(folderPath);
            requiresReconciliation = true;
        }

        return requiresReconciliation;
    }

    private async Task ResumeBackgroundActivityAsync(string folderPath)
    {
        try
        {
            await ConfigureFolderWatchersAsync(folderPath);
        }
        catch (Exception ex)
        {
            App.Log(
                $"[FolderRefresh] Failed to resume file widget background activity " +
                $"id={Config.Id}: {ex.Message}");
        }
    }
}
