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
    private const int ClipboardCannotOpenHResult = unchecked((int)0x800401D0);
    private static readonly int[] s_clipboardRetryDelaysMs = [40, 90, 160, 260];

    private readonly QuickCaptureService _quickCaptureService;
    private readonly SettingsService _settingsService;
    private readonly LocalizationService _localizationService;
    private readonly DispatcherQueue _dispatcherQueue;
    private readonly DispatcherQueueTimer _searchRefreshTimer;
    private readonly DispatcherQueueTimer _currentViewSaveTimer;

    private string _inputText = string.Empty;
    private string _searchText = string.Empty;
    private bool _isSearchExpanded;
    private QuickCaptureViewMode _selectedView = QuickCaptureViewMode.Records;
    private double _widgetOpacity;
    private double _textSize;
    private double _iconSize;
    private Visibility _emptyStateVisibility = Visibility.Collapsed;
    private Visibility _listVisibility = Visibility.Visible;
    private int _recordCount;
    private int _pinnedCount;
    private int _recentCount;
    private string _emptyStateTitle = string.Empty;
    private string _emptyStateText = string.Empty;
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
        _widgetOpacity = settingsService.Settings.WidgetOpacity;
        _textSize = SettingsService.NormalizeTextSize(settingsService.Settings.TextSize);
        _iconSize = SettingsService.NormalizeIconSize(settingsService.Settings.IconSize);
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
            }
        }
    }

    public Visibility SearchBoxVisibility => IsSearchExpanded ? Visibility.Visible : Visibility.Collapsed;

    public Visibility SearchButtonVisibility => IsSearchExpanded ? Visibility.Collapsed : Visibility.Visible;

    public string SearchPlaceholderText => _localizationService.T("QuickCapture.SearchPlaceholder");

    public string InputPlaceholderText => _localizationService.T("QuickCapture.InputPlaceholder");

    public string ExpandInputTooltipText => _localizationService.T("QuickCapture.ExpandInput");

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
            RefreshVisibleItemsFromCacheOrService();
        }
    }

    public bool IsRecordsView => SelectedView == QuickCaptureViewMode.Records;

    public bool IsPinnedView => SelectedView == QuickCaptureViewMode.Pinned;

    public bool IsRecentView => SelectedView == QuickCaptureViewMode.Recent;

    public double WidgetOpacity
    {
        get => _widgetOpacity;
        set => SetProperty(ref _widgetOpacity, value);
    }

    public double TextSize
    {
        get => _textSize;
        private set => SetProperty(ref _textSize, value);
    }

    public double TitleTextSize => Math.Min(SettingsService.MaxTextSize + 2, TextSize + 3);

    public double SecondaryTextSize => Math.Max(SettingsService.MinTextSize - 1, TextSize - 2);

    public double CaptionTextSize => Math.Max(SettingsService.MinTextSize, TextSize);

    public double SegmentTextSize => Math.Max(SettingsService.MinTextSize, TextSize);

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

    public async Task InitializeAsync()
    {
        if (_isDisposed)
        {
            return;
        }

        var data = await _quickCaptureService.GetDataAsync();
        if (_isDisposed)
        {
            return;
        }

        _cachedData = data;
        _selectedView = MapDefaultView(_settingsService.Settings.QuickCaptureDefaultView);
        OnPropertyChanged(nameof(SelectedView));
        OnPropertyChanged(nameof(IsRecordsView));
        OnPropertyChanged(nameof(IsPinnedView));
        OnPropertyChanged(nameof(IsRecentView));
        OnPropertyChanged(nameof(InputAreaVisibility));
        OnPropertyChanged(nameof(RecentCaptureStatusVisibility));
        OnPropertyChanged(nameof(RecentCaptureActionVisibility));
        await RefreshFromDataAsync(data);
    }

    public void RefreshAfterViewReady()
    {
        if (_isDisposed)
        {
            return;
        }

        RefreshVisibleItemsFromCacheOrService();
    }

    public async Task AddInputAsync()
    {
        string body = InputText;
        await AddTextAsync(body);
        InputText = string.Empty;
    }

    public async Task AddTextAsync(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return;
        }

        await _quickCaptureService.AddItemAsync(body);
        if (SelectedView is QuickCaptureViewMode.Pinned or QuickCaptureViewMode.Recent)
        {
            SelectedView = QuickCaptureViewMode.Records;
        }
    }

    public Task<QuickCaptureItem?> AddImageFileAsync(string imagePath)
    {
        return _quickCaptureService.AddImageFileItemAsync(imagePath);
    }

    public Task<string?> CreateImageExportFileAsync(QuickCaptureItemViewModel item, string fileNamePrefix)
    {
        return _quickCaptureService.CreateImageExportFileAsync(item.ToModel(), fileNamePrefix);
    }

    public async Task CopyItemAsync(QuickCaptureItemViewModel item)
    {
        await WriteItemToClipboardWithRetryAsync(item);
        if (item.Type != QuickCaptureItemType.Image)
        {
            _quickCaptureService.MarkClipboardTextWrittenByDeskBox(item.Body);
        }
    }

    private async Task WriteItemToClipboardWithRetryAsync(QuickCaptureItemViewModel item)
    {
        Exception? lastException = null;
        for (int attempt = 0; attempt <= s_clipboardRetryDelaysMs.Length; attempt++)
        {
            try
            {
                await WriteItemToClipboardOnceAsync(item);
                return;
            }
            catch (COMException ex) when (IsRetryableClipboardException(ex) && attempt < s_clipboardRetryDelaysMs.Length)
            {
                lastException = ex;
                await Task.Delay(s_clipboardRetryDelaysMs[attempt]);
            }
        }

        if (lastException is not null)
        {
            throw lastException;
        }
    }

    private static async Task WriteItemToClipboardOnceAsync(QuickCaptureItemViewModel item)
    {
        var dataPackage = new DataPackage();
        if (item.Type == QuickCaptureItemType.Image &&
            !string.IsNullOrWhiteSpace(item.ImagePath) &&
            File.Exists(item.ImagePath))
        {
            var file = await Windows.Storage.StorageFile.GetFileFromPathAsync(item.ImagePath);
            dataPackage.SetBitmap(Windows.Storage.Streams.RandomAccessStreamReference.CreateFromFile(file));
            DeskBoxClipboardWriteScope.MarkWrite(hasImage: true, paths: [item.ImagePath]);
        }
        else
        {
            dataPackage.SetText(item.Body);
            DeskBoxClipboardWriteScope.MarkWrite(text: item.Body);
        }

        Clipboard.SetContent(dataPackage);
        Clipboard.Flush();
    }

    private static bool IsRetryableClipboardException(COMException ex)
    {
        return ex.HResult == ClipboardCannotOpenHResult;
    }

    private static QuickCaptureViewMode MapDefaultView(string? defaultView)
    {
        return defaultView switch
        {
            SettingsService.QuickCaptureDefaultViewPinned => QuickCaptureViewMode.Pinned,
            SettingsService.QuickCaptureDefaultViewRecent => QuickCaptureViewMode.Recent,
            _ => QuickCaptureViewMode.Records
        };
    }

    public async Task EditItemAsync(QuickCaptureItemViewModel item, string body)
    {
        await _quickCaptureService.UpdateItemAsync(item.Id, body);
    }

    public async Task TogglePinnedAsync(QuickCaptureItemViewModel item)
    {
        await _quickCaptureService.SetPinnedAsync(item.Id, !item.IsPinned);
    }

    public async Task MovePinnedItemAsync(QuickCaptureItemViewModel item, int direction)
    {
        if (SelectedView != QuickCaptureViewMode.Pinned || HasSearchText)
        {
            return;
        }

        await _quickCaptureService.MovePinnedItemAsync(item.Id, direction);
    }

    public async Task<QuickCaptureDeletedItemSnapshot?> DeleteItemAsync(QuickCaptureItemViewModel item)
    {
        if (item.IsRecent)
        {
            return await _quickCaptureService.DeleteRecentItemAsync(item.Id);
        }

        return await _quickCaptureService.DeleteItemAsync(item.Id);
    }

    public Task<bool> RestoreDeletedItemAsync(QuickCaptureDeletedItemSnapshot? snapshot)
    {
        return _quickCaptureService.RestoreDeletedItemAsync(snapshot);
    }

    public Task CleanupUnusedImageCacheAsync()
    {
        return _quickCaptureService.CleanupUnusedImageCacheAsync();
    }

    public async Task SaveRecentItemAsync(QuickCaptureItemViewModel item)
    {
        if (!item.IsRecent)
        {
            return;
        }

        await _quickCaptureService.SaveRecentItemToRecordsAsync(item.Id, pin: false);
    }

    public async Task PinRecentItemAsync(QuickCaptureItemViewModel item)
    {
        if (!item.IsRecent)
        {
            return;
        }

        await _quickCaptureService.SaveRecentItemToRecordsAsync(item.Id, pin: true);
    }

    public async Task ClearAsync()
    {
        await _quickCaptureService.ClearAsync();
    }

    public async Task ClearRecentAsync()
    {
        await _quickCaptureService.ClearRecentAsync();
    }

    public async Task RenameAsync(string newName)
    {
        if (App.Current?.WidgetManager is { } widgetManager)
        {
            await widgetManager.RenameWidgetAsync(Config.Id, newName);
            Name = Config.Name;
            return;
        }

        newName = newName.Trim();
        if (string.IsNullOrWhiteSpace(newName))
        {
            throw new InvalidOperationException(_localizationService.T("Widget.Validation.NameRequired"));
        }

        Config.Name = newName;
        Config.IsDefaultTitle = false;
        _settingsService.UpdateWidget(Config);
        OnPropertyChanged(nameof(DisplayName));
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
        WidgetOpacity = _settingsService.Settings.WidgetOpacity;
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
        _quickCaptureService.Changed -= OnQuickCaptureChanged;
        _settingsService.SettingsChanged -= OnSettingsChanged;
        _localizationService.LanguageChanged -= OnLanguageChanged;
    }

    private void ScheduleCurrentViewSave()
    {
        if (_isDisposed)
        {
            return;
        }

        _currentViewSaveTimer.Stop();
        _currentViewSaveTimer.Start();
    }

    private void CurrentViewSaveTimer_Tick(DispatcherQueueTimer sender, object args)
    {
        sender.Stop();
        if (_isDisposed)
        {
            return;
        }

        var view = SelectedView;
        _ = Task.Run(async () =>
        {
            try
            {
                await _quickCaptureService.SetCurrentViewAsync(view);
            }
            catch (Exception ex)
            {
                App.Log($"[QuickCapture] Failed to save current view: {ex}");
            }
        });
    }

    private void OnQuickCaptureChanged()
    {
        if (_isDisposed)
        {
            return;
        }

        if (!_dispatcherQueue.HasThreadAccess)
        {
            _dispatcherQueue.TryEnqueue(RefreshVisibleItemsImmediately);
            return;
        }

        RefreshVisibleItemsImmediately();
    }

    private void OnLanguageChanged()
    {
        if (_isDisposed)
        {
            return;
        }

        UpdateEmptyStateText();
        OnPropertyChanged(nameof(DisplayName));
        OnPropertyChanged(nameof(RecordsTabText));
        OnPropertyChanged(nameof(PinnedTabText));
        OnPropertyChanged(nameof(RecentTabText));
        OnPropertyChanged(nameof(EnableRecentCaptureText));
        OnPropertyChanged(nameof(SearchPlaceholderText));
        OnPropertyChanged(nameof(SearchScopeText));
        OnPropertyChanged(nameof(SearchScopeVisibility));
        OnPropertyChanged(nameof(RecentCaptureStatusText));
        OnPropertyChanged(nameof(RecentCaptureStatusVisibility));
        OnPropertyChanged(nameof(RecentCaptureActionVisibility));
        foreach (var item in Items)
        {
            item.Update(item.ToModel());
            item.UpdateSearchText(SearchText);
        }
    }

    private void OnSettingsChanged()
    {
        if (_isDisposed)
        {
            return;
        }

        if (!_dispatcherQueue.HasThreadAccess)
        {
            _dispatcherQueue.TryEnqueue(OnSettingsChanged);
            return;
        }

        UpdateEmptyStateText();
        RefreshAppearanceFromSettings();
        OnPropertyChanged(nameof(RecentCaptureStatusText));
        OnPropertyChanged(nameof(RecentCaptureStatusVisibility));
        OnPropertyChanged(nameof(RecentCaptureActionVisibility));
    }

    private void RefreshAppearanceFromSettings()
    {
        var settings = _settingsService.Settings;
        WidgetOpacity = settings.WidgetOpacity;
        TextSize = SettingsService.NormalizeTextSize(settings.TextSize);
        IconSize = SettingsService.NormalizeIconSize(settings.IconSize);
        OnPropertyChanged(nameof(TitleTextSize));
        OnPropertyChanged(nameof(SecondaryTextSize));
        OnPropertyChanged(nameof(CaptionTextSize));
        OnPropertyChanged(nameof(SegmentTextSize));
        OnPropertyChanged(nameof(TitleIconSize));
        OnPropertyChanged(nameof(ActionIconSize));
        OnPropertyChanged(nameof(EmptyIconSize));

        foreach (var item in Items)
        {
            item.UpdateAppearance(TextSize, IconSize);
            item.UpdateSearchText(SearchText);
        }
    }

    private async Task RefreshVisibleItemsAsync()
    {
        if (_isDisposed)
        {
            return;
        }

        int generation = System.Threading.Interlocked.Increment(ref _visibleItemsRefreshGeneration);
        var data = await _quickCaptureService.GetDataAsync();
        if (_isDisposed || generation != System.Threading.Volatile.Read(ref _visibleItemsRefreshGeneration))
        {
            return;
        }

        _cachedData = data;
        await RefreshFromDataAsync(data);
    }

    private void RefreshVisibleItemsFromCacheOrService()
    {
        if (_isDisposed)
        {
            return;
        }

        if (!_dispatcherQueue.HasThreadAccess)
        {
            _dispatcherQueue.TryEnqueue(RefreshVisibleItemsFromCacheOrService);
            return;
        }

        _searchRefreshTimer.Stop();
        if (_cachedData is { } data)
        {
            _ = RefreshFromDataAsync(data);
            return;
        }

        RefreshVisibleItemsAsync().LogQuickCaptureFailure();
    }

    private void ScheduleVisibleItemsRefresh()
    {
        if (_isDisposed)
        {
            return;
        }

        if (!_dispatcherQueue.HasThreadAccess)
        {
            _dispatcherQueue.TryEnqueue(ScheduleVisibleItemsRefresh);
            return;
        }

        _searchRefreshTimer.Stop();
        _searchRefreshTimer.Start();
    }

    private void RefreshVisibleItemsImmediately()
    {
        if (_isDisposed)
        {
            return;
        }

        if (!_dispatcherQueue.HasThreadAccess)
        {
            _dispatcherQueue.TryEnqueue(RefreshVisibleItemsImmediately);
            return;
        }

        _searchRefreshTimer.Stop();
        RefreshVisibleItemsAsync().LogQuickCaptureFailure();
    }

    private void SearchRefreshTimer_Tick(DispatcherQueueTimer sender, object args)
    {
        if (_isDisposed)
        {
            return;
        }

        sender.Stop();
        RefreshVisibleItemsAsync().LogQuickCaptureFailure();
    }

    private Task RefreshFromDataAsync(QuickCaptureStoreData data)
    {
        if (_isDisposed)
        {
            return Task.CompletedTask;
        }

        var activeItems = data.Items
            .Where(item => !item.IsDeleted)
            .OrderBy(item => item.SortOrder)
            .ThenByDescending(item => item.UpdatedAt)
            .ToList();
        var recentItems = data.RecentItems
            .Where(item => !item.IsDeleted)
            .OrderBy(item => item.SortOrder)
            .ThenByDescending(item => item.UpdatedAt)
            .ToList();

        RecordCount = activeItems.Count;
        PinnedCount = activeItems.Count(item => item.IsPinned);
        RecentCount = recentItems.Count;
        OnPropertyChanged(nameof(RecordsTabText));
        OnPropertyChanged(nameof(PinnedTabText));
        OnPropertyChanged(nameof(RecentTabText));

        bool canShowPinnedSortControls = SelectedView == QuickCaptureViewMode.Pinned && !HasSearchText;
        var visibleItems = SelectedView switch
        {
            QuickCaptureViewMode.Pinned => activeItems
                .Where(item => item.IsPinned)
                .OrderBy(item => item.PinnedSortOrder < 0 ? int.MaxValue : item.PinnedSortOrder)
                .ThenBy(item => item.SortOrder)
                .ThenByDescending(item => item.UpdatedAt)
                .ToList(),
            QuickCaptureViewMode.Recent => recentItems,
            _ => activeItems
        };

        if (HasSearchText)
        {
            visibleItems = activeItems
                .Concat(recentItems)
                .Where(item => MatchesSearch(item, SearchText))
                .OrderByDescending(item => item.UpdatedAt)
                .ThenBy(item => item.IsRecent ? 1 : 0)
                .ThenBy(item => item.SortOrder)
                .ToList();
        }

        SyncVisibleItems(visibleItems, canShowPinnedSortControls);

        UpdateEmptyStateText();
        bool hasItems = Items.Count > 0;
        EmptyStateVisibility = hasItems ? Visibility.Collapsed : Visibility.Visible;
        ListVisibility = hasItems ? Visibility.Visible : Visibility.Collapsed;
        OnPropertyChanged(nameof(RecentCaptureStatusText));
        OnPropertyChanged(nameof(RecentCaptureStatusVisibility));
        OnPropertyChanged(nameof(RecentCaptureActionVisibility));
        return Task.CompletedTask;
    }

    private void SyncVisibleItems(IReadOnlyList<QuickCaptureItem> visibleItems, bool canShowPinnedSortControls)
    {
        var existingById = Items
            .GroupBy(item => item.Id, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        var visibleIds = visibleItems
            .Select(item => item.Id)
            .ToHashSet(StringComparer.Ordinal);

        PruneItemViewModelCache(visibleItems);

        for (int index = Items.Count - 1; index >= 0; index--)
        {
            if (!visibleIds.Contains(Items[index].Id))
            {
                Items.RemoveAt(index);
            }
        }

        var currentIndexById = new Dictionary<string, int>(Items.Count, StringComparer.Ordinal);
        for (int i = 0; i < Items.Count; i++)
        {
            currentIndexById[Items[i].Id] = i;
        }

        for (int targetIndex = 0; targetIndex < visibleItems.Count; targetIndex++)
        {
            var model = visibleItems[targetIndex];
            bool canMoveUp = canShowPinnedSortControls && targetIndex > 0;
            bool canMoveDown = canShowPinnedSortControls && targetIndex < visibleItems.Count - 1;

            if (!existingById.TryGetValue(model.Id, out var viewModel) &&
                !_itemViewModelCache.TryGetValue(model.Id, out viewModel))
            {
                viewModel = new QuickCaptureItemViewModel(
                    model,
                    _localizationService,
                    TextSize,
                    IconSize,
                    SearchText,
                    showPinnedSortControls: canShowPinnedSortControls,
                    canMovePinnedUp: canMoveUp,
                    canMovePinnedDown: canMoveDown);
                _itemViewModelCache[model.Id] = viewModel;
            }

            viewModel.Update(model);
            viewModel.UpdateAppearance(TextSize, IconSize);
            viewModel.UpdateSearchText(SearchText);
            viewModel.UpdatePinnedSortState(canShowPinnedSortControls, canMoveUp, canMoveDown);

            if (!currentIndexById.TryGetValue(viewModel.Id, out int currentIndex))
            {
                Items.Insert(targetIndex, viewModel);
                foreach (var key in currentIndexById.Keys)
                {
                    if (currentIndexById[key] >= targetIndex)
                    {
                        currentIndexById[key]++;
                    }
                }
            }
            else if (currentIndex != targetIndex)
            {
                Items.Move(currentIndex, targetIndex);
                int lo = Math.Min(currentIndex, targetIndex);
                int hi = Math.Max(currentIndex, targetIndex);
                for (int i = lo; i <= hi; i++)
                {
                    currentIndexById[Items[i].Id] = i;
                }
            }
        }
    }

    private void PruneItemViewModelCache(IReadOnlyList<QuickCaptureItem> visibleItems)
    {
        if (_cachedData is null && _itemViewModelCache.Count == 0)
        {
            return;
        }

        HashSet<string> retainedIds = _cachedData is { } data
            ? data.Items
                .Concat(data.RecentItems)
                .Where(item => !item.IsDeleted)
                .Select(item => item.Id)
                .ToHashSet(StringComparer.Ordinal)
            : visibleItems
                .Select(item => item.Id)
                .ToHashSet(StringComparer.Ordinal);

        foreach (string id in _itemViewModelCache.Keys.Where(id => !retainedIds.Contains(id)).ToList())
        {
            _itemViewModelCache.Remove(id);
        }
    }

    private void UpdateEmptyStateText()
    {
        if (HasSearchText)
        {
            EmptyStateTitle = _localizationService.T("QuickCapture.Empty.SearchTitle");
            EmptyStateText = _localizationService.T("QuickCapture.Empty.SearchText");
            return;
        }

        if (SelectedView == QuickCaptureViewMode.Pinned)
        {
            EmptyStateTitle = _localizationService.T("QuickCapture.Empty.PinnedTitle");
            EmptyStateText = _localizationService.T("QuickCapture.Empty.PinnedText");
            return;
        }

        if (SelectedView == QuickCaptureViewMode.Recent)
        {
            if (!IsRecentCaptureEnabled())
            {
                EmptyStateTitle = _localizationService.T("QuickCapture.Empty.RecentDisabledTitle");
                EmptyStateText = _localizationService.T("QuickCapture.Empty.RecentDisabledText");
                return;
            }

            EmptyStateTitle = _localizationService.T("QuickCapture.Empty.RecentTitle");
            EmptyStateText = _settingsService.Settings.QuickCaptureImageClipboardEnabled
                ? _localizationService.T("QuickCapture.Empty.RecentTextWithImages")
                : _localizationService.T("QuickCapture.Empty.RecentText");
            return;
        }

        EmptyStateTitle = _localizationService.T("QuickCapture.Empty.RecordsTitle");
        EmptyStateText = _localizationService.T("QuickCapture.Empty.RecordsText");
    }

    private bool IsRecentCaptureEnabled()
    {
        return _settingsService.Settings.QuickCaptureClipboardEnabled;
    }

    private bool MatchesSearch(QuickCaptureItem item, string searchText)
    {
        if (string.IsNullOrWhiteSpace(searchText))
        {
            return true;
        }

        string keyword = searchText.Trim();
        if (string.IsNullOrEmpty(keyword))
        {
            return true;
        }

        if (item.Type == QuickCaptureItemType.Image)
        {
            return "Image".Contains(keyword, StringComparison.CurrentCultureIgnoreCase) ||
                   _localizationService.T("QuickCapture.ImageItem").Contains(keyword, StringComparison.CurrentCultureIgnoreCase);
        }

        return item.Body.Contains(keyword, StringComparison.CurrentCultureIgnoreCase) ||
               (item.Url?.Contains(keyword, StringComparison.CurrentCultureIgnoreCase) ?? false);
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
