using System.Collections.ObjectModel;
using System.Runtime.InteropServices;
using CommunityToolkit.Mvvm.ComponentModel;
using DeskBox.Models;
using DeskBox.Services;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Windows.ApplicationModel.DataTransfer;

namespace DeskBox.ViewModels;

public sealed partial class QuickCaptureWidgetViewModel : ObservableObject, IDisposable
{
    private const int SearchRefreshDebounceMs = 150;
    private const int ViewSwitchRefreshDelayMs = 60;
    private const int ClipboardCannotOpenHResult = unchecked((int)0x800401D0);
    private static readonly int[] s_clipboardRetryDelaysMs = [40, 90, 160, 260];

    private readonly QuickCaptureService _quickCaptureService;
    private readonly SettingsService _settingsService;
    private readonly LocalizationService _localizationService;
    private readonly DispatcherQueue _dispatcherQueue;
    private readonly DispatcherQueueTimer _searchRefreshTimer;
    private readonly DispatcherQueueTimer _currentViewSaveTimer;
    private readonly DispatcherQueueTimer _viewSwitchRefreshTimer;

    private string _inputText = string.Empty;
    private string _searchText = string.Empty;
    private bool _isSearchExpanded;
    private QuickCaptureViewMode _selectedView = QuickCaptureViewMode.Records;
    private string _tabStyle = SettingsService.WidgetTabStyleButton;
    private bool _showTabBar = true;
    private bool _showRecordsTab = true;
    private bool _showPinnedTab = true;
    private bool _showRecentTab = true;
    private double _widgetOpacity;
    private double _textSize;
    private double _iconSize;
    private double _layoutDensityScale = SettingsService.DefaultLayoutDensityScale;
    private Visibility _emptyStateVisibility = Visibility.Collapsed;
    private Visibility _listVisibility = Visibility.Visible;
    private int _recordCount;
    private int _pinnedCount;
    private int _recentCount;
    private string _emptyStateTitle = string.Empty;
    private string _emptyStateText = string.Empty;
    private bool _isSwitchingView;
    private int _itemsViewTransitionToken;
    private bool _isDisposed;
    private int _visibleItemsRefreshGeneration;
    private QuickCaptureStoreData? _cachedData;
    private readonly Dictionary<string, QuickCaptureItemViewModel> _itemViewModelCache = new(StringComparer.Ordinal);

    public ObservableCollection<QuickCaptureItemViewModel> Items { get; } = [];

    public WidgetConfig Config { get; }

    public QuickCaptureWidgetViewModel(
        WidgetConfig config,
        QuickCaptureService quickCaptureService,
        SettingsService settingsService,
        LocalizationService localizationService,
        DispatcherQueue dispatcherQueue)
    {
        Config = config;
        _quickCaptureService = quickCaptureService;
        _settingsService = settingsService;
        _localizationService = localizationService;
        _dispatcherQueue = dispatcherQueue;
        _searchRefreshTimer = dispatcherQueue.CreateTimer();
        _searchRefreshTimer.Interval = TimeSpan.FromMilliseconds(SearchRefreshDebounceMs);
        _searchRefreshTimer.IsRepeating = false;
        _searchRefreshTimer.Tick += SearchRefreshTimer_Tick;
        _currentViewSaveTimer = dispatcherQueue.CreateTimer();
        _currentViewSaveTimer.Interval = TimeSpan.FromMilliseconds(350);
        _currentViewSaveTimer.IsRepeating = false;
        _currentViewSaveTimer.Tick += CurrentViewSaveTimer_Tick;
        _viewSwitchRefreshTimer = dispatcherQueue.CreateTimer();
        _viewSwitchRefreshTimer.Interval = TimeSpan.FromMilliseconds(ViewSwitchRefreshDelayMs);
        _viewSwitchRefreshTimer.IsRepeating = false;
        _viewSwitchRefreshTimer.Tick += ViewSwitchRefreshTimer_Tick;
        _widgetOpacity = settingsService.Settings.WidgetOpacity;
        _tabStyle = SettingsService.NormalizeWidgetTabStyle(settingsService.Settings.QuickCaptureTabStyle);
        _textSize = SettingsService.NormalizeTextSize(settingsService.Settings.TextSize);
        _iconSize = SettingsService.NormalizeIconSize(settingsService.Settings.IconSize);
        _layoutDensityScale = NormalizeDensity(settingsService.Settings.LayoutDensityScale);
        Name = config.Name;
        _quickCaptureService.Changed += OnQuickCaptureChanged;
        _settingsService.SettingsChanged += OnSettingsChanged;
        _localizationService.LanguageChanged += OnLanguageChanged;
        UpdateEmptyStateText();
    }

    public string Name
    {
        get => Config.Name;
        private set
        {
            Config.Name = value;
            OnPropertyChanged(nameof(DisplayName));
        }
    }

    public string DisplayName => Config.IsDefaultTitle || string.IsNullOrWhiteSpace(Config.Name)
        ? _localizationService.T("QuickCapture.Name")
        : Config.Name;

    public string InputText
    {
        get => _inputText;
        set
        {
            if (SetProperty(ref _inputText, value))
            {
                OnPropertyChanged(nameof(CanAddInput));
            }
        }
    }

    public bool CanAddInput => !string.IsNullOrWhiteSpace(InputText);

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (SetProperty(ref _searchText, value))
            {
                OnPropertyChanged(nameof(HasSearchText));
                OnPropertyChanged(nameof(SearchBoxVisibility));
                OnPropertyChanged(nameof(SearchButtonVisibility));
                OnPropertyChanged(nameof(SearchScopeVisibility));
                OnPropertyChanged(nameof(RecentCaptureStatusVisibility));
                OnPropertyChanged(nameof(RecentCaptureActionVisibility));
                ScheduleVisibleItemsRefresh();
            }
        }
    }

    public bool HasSearchText => !string.IsNullOrWhiteSpace(SearchText);

    public bool IsSearchExpanded
    {
        get => _isSearchExpanded;
        private set
        {
            if (SetProperty(ref _isSearchExpanded, value))
            {
                OnPropertyChanged(nameof(SearchBoxVisibility));
                OnPropertyChanged(nameof(SearchButtonVisibility));
                OnPropertyChanged(nameof(InputAreaVisibility));
                OnPropertyChanged(nameof(TabBarVisibility));
            }
        }
    }

    public Visibility SearchBoxVisibility => IsSearchExpanded ? Visibility.Visible : Visibility.Collapsed;

    public Visibility SearchButtonVisibility => IsSearchExpanded ? Visibility.Collapsed : Visibility.Visible;

    public string SearchPlaceholderText => _localizationService.T("QuickCapture.SearchPlaceholder");

    public string InputPlaceholderText => _localizationService.T("QuickCapture.InputPlaceholder");

    public string AddNoteText => _localizationService.T("QuickCapture.AddNote");

    public string AddFileText => _localizationService.T("QuickCapture.AddFile");

    public string DetailRemoveAttachmentText => _localizationService.T("QuickCapture.Detail.RemoveAttachment");

    public string ExpandInputTooltipText => _localizationService.T("QuickCapture.ExpandInput");

    public string DetailBackText => _localizationService.T("QuickCapture.Detail.Back");

    public string DetailTitlePlaceholderText => _localizationService.T("QuickCapture.Detail.TitlePlaceholder");

    public string DetailBodyPlaceholderText => _localizationService.T("QuickCapture.Detail.BodyPlaceholder");

    public string DetailAppearanceText => _localizationService.T("QuickCapture.Detail.Appearance");

    public string DetailCopyText => _localizationService.T("QuickCapture.Detail.Copy");

    public string DetailCopyImageText => _localizationService.T("QuickCapture.Detail.CopyImage");

    public string DetailReplaceImageText => _localizationService.T("QuickCapture.Detail.ReplaceImage");

    public string DetailDeleteText => _localizationService.T("QuickCapture.Detail.Delete");

    public string MaterialDefaultText => _localizationService.T("QuickCapture.Material.Default");

    public string MaterialPaperText => _localizationService.T("QuickCapture.Material.Paper");

    public string MaterialYellowText => _localizationService.T("QuickCapture.Material.Yellow");

    public string MaterialRoseText => _localizationService.T("QuickCapture.Material.Rose");

    public string MaterialMintText => _localizationService.T("QuickCapture.Material.Mint");

    public string MaterialBlueText => _localizationService.T("QuickCapture.Material.Blue");

    public Visibility InputAreaVisibility => IsRecordsView && !IsSearchExpanded ? Visibility.Visible : Visibility.Collapsed;

    public string SearchScopeText => _localizationService.T("QuickCapture.SearchScope");

    public Visibility SearchScopeVisibility => HasSearchText ? Visibility.Visible : Visibility.Collapsed;

    public QuickCaptureViewMode SelectedView
    {
        get => _selectedView;
        set
        {
            if (_isDisposed)
            {
                return;
            }

            if (!SetProperty(ref _selectedView, value))
            {
                return;
            }

            ScheduleCurrentViewSave();
            OnPropertyChanged(nameof(IsRecordsView));
            OnPropertyChanged(nameof(IsPinnedView));
            OnPropertyChanged(nameof(IsRecentView));
            OnPropertyChanged(nameof(InputAreaVisibility));
            OnPropertyChanged(nameof(RecentCaptureStatusVisibility));
            OnPropertyChanged(nameof(RecentCaptureActionVisibility));
            ScheduleViewSwitchRefresh();
        }
    }

    public bool IsRecordsView => SelectedView == QuickCaptureViewMode.Records;

    public bool IsPinnedView => SelectedView == QuickCaptureViewMode.Pinned;

    public bool IsRecentView => SelectedView == QuickCaptureViewMode.Recent;

    public Visibility TabBarVisibility => _showTabBar && !IsSearchExpanded
        ? Visibility.Visible
        : Visibility.Collapsed;

    public Visibility RecordsTabVisibility => _showRecordsTab ? Visibility.Visible : Visibility.Collapsed;

    public Visibility PinnedTabVisibility => _showPinnedTab ? Visibility.Visible : Visibility.Collapsed;

    public Visibility RecentTabVisibility => _showRecentTab ? Visibility.Visible : Visibility.Collapsed;

    public int VisibleTabCount => (_showRecordsTab ? 1 : 0) + (_showPinnedTab ? 1 : 0) + (_showRecentTab ? 1 : 0);

    public string TabStyle
    {
        get => _tabStyle;
        private set => SetProperty(ref _tabStyle, SettingsService.NormalizeWidgetTabStyle(value));
    }

    public double WidgetOpacity
    {
        get => _widgetOpacity;
        set => SetProperty(ref _widgetOpacity, value);
    }

    public double TextSize
    {
        get => _textSize;
        private set
        {
            if (SetProperty(ref _textSize, value))
            {
                OnPropertyChanged(nameof(TitleTextSize));
                OnPropertyChanged(nameof(SecondaryTextSize));
                OnPropertyChanged(nameof(DetailHeaderTextSize));
                OnPropertyChanged(nameof(CaptionTextSize));
                OnPropertyChanged(nameof(SegmentTextSize));
                OnPropertyChanged(nameof(SegmentHeight));
                OnPropertyChanged(nameof(SegmentPadding));
                OnPropertyChanged(nameof(InputHeight));
                OnPropertyChanged(nameof(InputButtonSize));
                OnPropertyChanged(nameof(InputActionIconSize));
                OnPropertyChanged(nameof(PrimaryIconSize));
                OnPropertyChanged(nameof(InputPadding));
                OnPropertyChanged(nameof(ItemTextLineHeight));
                OnPropertyChanged(nameof(ItemMetaLineHeight));
            }
        }
    }

    public double LayoutDensityScale
    {
        get => _layoutDensityScale;
        private set
        {
            if (SetProperty(ref _layoutDensityScale, NormalizeDensity(value)))
            {
                OnPropertyChanged(nameof(RootPadding));
                OnPropertyChanged(nameof(RootRowSpacing));
                OnPropertyChanged(nameof(ItemPadding));
                OnPropertyChanged(nameof(ItemMargin));
                OnPropertyChanged(nameof(ItemChromeMargin));
                OnPropertyChanged(nameof(AddCardMinHeight));
                OnPropertyChanged(nameof(AddCardMargin));
                OnPropertyChanged(nameof(AddCardPadding));
                OnPropertyChanged(nameof(ItemTextLineHeight));
                OnPropertyChanged(nameof(ItemMetaLineHeight));
            }
        }
    }

    public double TitleTextSize => Math.Min(SettingsService.MaxTextSize + 2, TextSize + 3);

    public double SecondaryTextSize => Math.Max(SettingsService.MinTextSize, TextSize - 1.5);

    public double DetailHeaderTextSize => Math.Max(SettingsService.MinTextSize, TextSize - 1);

    public double CaptionTextSize => Math.Max(SettingsService.MinTextSize, TextSize);

    public double SegmentTextSize => WidgetSegmentedMetrics.Create(TextSize).TextSize;

    public double SegmentHeight => WidgetSegmentedMetrics.Create(TextSize).Height;

    public Thickness SegmentPadding => WidgetSegmentedMetrics.Create(TextSize).Padding;

    public double InputHeight => WidgetInputMetrics.Create(TextSize).Height;

    public double InputButtonSize => WidgetInputMetrics.Create(TextSize).ButtonSize;

    public double InputActionIconSize => WidgetInputMetrics.Create(TextSize).ActionIconSize;

    public double PrimaryIconSize => Math.Round(Math.Clamp(TextSize + 5, 16, 20));

    public Thickness InputPadding => WidgetInputMetrics.Create(TextSize).Padding;

    public Thickness RootPadding => new(
        DensityMetric(8, 10, 14),
        DensityMetric(4, 6, 9),
        DensityMetric(4, 6, 9),
        DensityMetric(6, 8, 12));

    public double RootRowSpacing => DensityMetric(3, 4, 7);

    public Thickness ItemPadding => new(
        DensityMetric(8, 10, 14),
        DensityMetric(4, 6, 9),
        DensityMetric(4, 6, 9),
        DensityMetric(4, 6, 9));

    public Thickness ItemMargin => new(0, 0, 0, DensityMetric(3, 5, 8));

    public Thickness ItemChromeMargin => new(
        -ItemPadding.Left,
        -ItemPadding.Top,
        -ItemPadding.Right,
        -ItemPadding.Bottom);

    public double AddCardMinHeight => DensityMetric(42, 48, 56);

    public Thickness AddCardMargin => new(0, 0, 0, DensityMetric(4, 6, 9));

    public Thickness AddCardPadding => new(
        DensityMetric(6, 8, 12),
        DensityMetric(4, 5, 8),
        DensityMetric(6, 8, 12),
        DensityMetric(4, 5, 8));

    public double ItemTextLineHeight => Math.Round(TextSize * DensityMetric(1.18, 1.24, 1.34));

    public double ItemMetaLineHeight => Math.Round(SecondaryTextSize * DensityMetric(1.12, 1.16, 1.24));

    public int ItemPreviewLineCount => SettingsService.NormalizeItemPreviewLineCount(
        _settingsService.Settings.QuickCaptureItemPreviewLineCount);

    public Visibility CreatedTimeVisibility => _settingsService.Settings.QuickCaptureShowCreatedTime
        ? Visibility.Visible
        : Visibility.Collapsed;

    public double IconSize
    {
        get => _iconSize;
        private set => SetProperty(ref _iconSize, value);
    }

    public double TitleIconSize => Math.Clamp(Math.Round(IconSize * 0.72 * 0.56 * 0.54), 11, 18);

    public double ActionIconSize => Math.Max(10, Math.Round(IconSize * 0.42));

    public double EmptyIconSize => Math.Max(18, Math.Round(IconSize * 0.74));

    public Visibility EmptyStateVisibility
    {
        get => _emptyStateVisibility;
        private set => SetProperty(ref _emptyStateVisibility, value);
    }

    public Visibility ListVisibility
    {
        get => _listVisibility;
        private set => SetProperty(ref _listVisibility, value);
    }

    public Visibility ViewSwitchLoadingVisibility => _isSwitchingView ? Visibility.Visible : Visibility.Collapsed;

    public bool IsSwitchingView => _isSwitchingView;

    public int ItemsViewTransitionToken
    {
        get => _itemsViewTransitionToken;
        private set => SetProperty(ref _itemsViewTransitionToken, value);
    }

    public int RecordCount
    {
        get => _recordCount;
        private set => SetProperty(ref _recordCount, value);
    }

    public int PinnedCount
    {
        get => _pinnedCount;
        private set => SetProperty(ref _pinnedCount, value);
    }

    public int RecentCount
    {
        get => _recentCount;
        private set => SetProperty(ref _recentCount, value);
    }

    public string RecordsTabText => _localizationService.Format("QuickCapture.Tab.Records", RecordCount);

    public string PinnedTabText => _localizationService.Format("QuickCapture.Tab.Pinned", PinnedCount);

    public string RecentTabText => _localizationService.Format("QuickCapture.Tab.Recent", RecentCount);

    public string EnableRecentCaptureText => _localizationService.T("QuickCapture.EnableRecentCapture");

    public string RecentCaptureStatusText
    {
        get
        {
            var settings = _settingsService.Settings;
            if (!settings.QuickCaptureEnabled)
            {
                return _localizationService.T("QuickCapture.RecentStatus.FeatureOff");
            }

            if (!settings.QuickCaptureClipboardEnabled)
            {
                return _localizationService.T("QuickCapture.RecentStatus.Off");
            }

            return settings.QuickCaptureImageClipboardEnabled
                ? _localizationService.T("QuickCapture.RecentStatus.OnWithImages")
                : _localizationService.T("QuickCapture.RecentStatus.On");
        }
    }

    public Visibility RecentCaptureStatusVisibility => Visibility.Collapsed;

    public Visibility RecentCaptureActionVisibility =>
        SelectedView == QuickCaptureViewMode.Recent &&
        !HasSearchText &&
        Items.Count == 0 &&
        !IsRecentCaptureEnabled()
            ? Visibility.Visible
            : Visibility.Collapsed;

    public string EmptyStateTitle
    {
        get => _emptyStateTitle;
        private set => SetProperty(ref _emptyStateTitle, value);
    }

    public string EmptyStateText
    {
        get => _emptyStateText;
        private set => SetProperty(ref _emptyStateText, value);
    }

    public void UpdateBounds(int x, int y, int width, int height, bool persist)
    {
        Config.X = x;
        Config.Y = y;
        Config.Width = width;
        Config.Height = height;
        if (persist)
        {
            _settingsService.UpdateWidget(Config);
        }
    }

    public void ApplyAppearancePreview()
    {
        RefreshAppearanceFromSettings();
    }

    public void SetPositionLocked(bool locked)
    {
        Config.IsPositionLocked = locked;
        _settingsService.UpdateWidget(Config);
    }

    public void SetSizeLocked(bool locked)
    {
        Config.IsSizeLocked = locked;
        _settingsService.UpdateWidget(Config);
    }

    public void ExpandSearch()
    {
        IsSearchExpanded = true;
    }

    public void CollapseSearch()
    {
        SearchText = string.Empty;
        IsSearchExpanded = false;
        RefreshVisibleItemsImmediately();
    }

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        System.Threading.Interlocked.Increment(ref _visibleItemsRefreshGeneration);
        _searchRefreshTimer.Stop();
        _searchRefreshTimer.Tick -= SearchRefreshTimer_Tick;
        _currentViewSaveTimer.Stop();
        _currentViewSaveTimer.Tick -= CurrentViewSaveTimer_Tick;
        _viewSwitchRefreshTimer.Stop();
        _viewSwitchRefreshTimer.Tick -= ViewSwitchRefreshTimer_Tick;
        _quickCaptureService.Changed -= OnQuickCaptureChanged;
        _settingsService.SettingsChanged -= OnSettingsChanged;
        _localizationService.LanguageChanged -= OnLanguageChanged;
    }

}

internal static class QuickCaptureTaskExtensions
{

    public static void LogQuickCaptureFailure(this Task task)
    {
        _ = task.ContinueWith(
            completed =>
            {
                if (completed.Exception is not null)
                {
                    App.Log($"[TaskExtensions] Unhandled task failure: {completed.Exception}");
                }
            },
            TaskContinuationOptions.OnlyOnFaulted);
    }
}
