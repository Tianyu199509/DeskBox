using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using DeskBox.Models;

namespace DeskBox.ViewModels;

/// <summary>
/// ViewModel for the search popup window.
/// Owns a flat result pool, a dynamic tab bar (extension-semantic tabs while a query
/// is active, Kind/recent-content tabs in the empty state) and per-tab sorting.
/// </summary>
public sealed partial class SearchPopupViewModel : ObservableObject, IDisposable
{
    private readonly Services.SearchEngineService _searchEngine;
    private readonly Services.SettingsService _settingsService;
    private readonly Services.LocalizationService _localizationService;
    private readonly Services.SearchHistoryService _historyService;
    private readonly Services.FileMetaService _fileMetaService;
    private readonly SynchronizationContext? _uiContext;
    private CancellationTokenSource? _searchCts;
    private CancellationTokenSource? _indexRefreshCts;
    private long _searchGeneration;
    private bool _isDisposed;

    /// <summary>Flat results for the active query, in engine relevance order.</summary>
    private List<SearchResultItem> _allResults = [];

    /// <summary>Empty-state pool: launchable application recommendations.</summary>
    private readonly List<SearchResultItem> _emptyStateItems = [];

    /// <summary>Recommended applications cached between empty-state rebuilds.</summary>
    private readonly List<SearchResultItem> _recentContentItems = [];

    public SearchPopupViewModel(
        Services.SearchEngineService searchEngine,
        Services.SettingsService settingsService,
        Services.LocalizationService localizationService,
        Services.SearchHistoryService historyService,
        Services.FileMetaService fileMetaService)
    {
        _searchEngine = searchEngine;
        _settingsService = settingsService;
        _localizationService = localizationService;
        _historyService = historyService;
        _fileMetaService = fileMetaService;
        _uiContext = SynchronizationContext.Current;
        _searchEngine.IndexUpdated += OnIndexUpdated;

        // Callback to close popup when item is opened.
        HidePopupCallback = () => { };
    }

    public Action? HidePopupCallback;

    public IntPtr OwnerWindowHandle { get; set; }

    [ObservableProperty]
    private string _query = string.Empty;

    [ObservableProperty]
    private bool _isSearching;

    [ObservableProperty]
    private bool _hasResults;

    /// <summary>True while a non-empty query is active; drives the tab strategy.</summary>
    [ObservableProperty]
    private bool _isQueryActive;

    /// <summary>Whether there are application recommendations to show.</summary>
    [ObservableProperty]
    private SearchResultItem? _selectedItem;

    [ObservableProperty]
    private int _selectedIndex = -1;

    [ObservableProperty]
    private string _statusText = string.Empty;

    [ObservableProperty]
    private SearchTabItem? _selectedTab;

    [ObservableProperty]
    private ResultSortColumn _sortColumn = ResultSortColumn.Relevance;

    [ObservableProperty]
    private bool _sortAscending = true;

    [ObservableProperty]
    private SearchResultFilter _resultFilter = SearchResultFilter.All;

    [ObservableProperty]
    private bool _hasCurrentResults;

    /// <summary>The dynamic tab bar. Rebuilt on every search / empty-state change.</summary>
    public ObservableCollection<SearchTabItem> Tabs { get; } = [];

    /// <summary>Filtered + sorted view of the active pool for <see cref="SelectedTab"/>.</summary>
    public ObservableCollection<SearchResultItem> CurrentResults { get; } = [];

    public string DisplayMode => _settingsService.Settings.SearchDisplayMode;
    public string HotkeyHint => GetHotkeyHint();

    /// <summary>Public access to recent queries for UI binding.</summary>
    public IReadOnlyList<string> RecentQueries => _historyService.RecentQueries;

    /// <summary>Public access to favorite queries for UI binding.</summary>
    public IReadOnlyList<string> FavoriteQueries => _historyService.FavoriteQueries;

    /// <summary>True if there's any history or recommendations to display.</summary>
    public bool HasHistoryOrRecommendations => _recentContentItems.Any();

    /// <summary>Home mode: a roomier dashboard.</summary>
    public bool IsHomeMode => string.Equals(DisplayMode, "Home", StringComparison.OrdinalIgnoreCase);

    /// <summary>Palette (command) mode: a compact launcher.</summary>
    public bool IsPaletteMode => string.Equals(DisplayMode, "Palette", StringComparison.OrdinalIgnoreCase);

    /// <summary>Spotlight mode: the default balanced search experience.</summary>
    public bool IsSpotlightMode => !IsHomeMode && !IsPaletteMode;

    private IReadOnlyList<SearchResultItem> ActivePool => IsQueryActive ? _allResults : _emptyStateItems;

    partial void OnQueryChanged(string value)
    {
        _ = SearchAsync(value);
    }

    partial void OnSelectedTabChanged(SearchTabItem? value)
    {
        RebuildCurrentResults();
    }

    partial void OnSortColumnChanged(ResultSortColumn value)
    {
        RebuildCurrentResults(preserveSelection: true);
    }

    partial void OnSortAscendingChanged(bool value)
    {
        RebuildCurrentResults(preserveSelection: true);
    }

    partial void OnResultFilterChanged(SearchResultFilter value)
    {
        RebuildCurrentResults(preserveSelection: true);
    }

    private void OnIndexUpdated()
    {
        void ScheduleRefresh()
        {
            if (_isDisposed || string.IsNullOrWhiteSpace(Query))
            {
                return;
            }

            _indexRefreshCts?.Cancel();
            _indexRefreshCts?.Dispose();
            _indexRefreshCts = new CancellationTokenSource();
            _ = RefreshAfterIndexUpdateAsync(_indexRefreshCts.Token);
        }

        if (_uiContext is not null)
        {
            _uiContext.Post(_ => ScheduleRefresh(), null);
        }
        else
        {
            ScheduleRefresh();
        }
    }

    private async Task RefreshAfterIndexUpdateAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(300, cancellationToken);
            if (!string.IsNullOrWhiteSpace(Query))
            {
                await SearchAsync(Query);
            }
        }
        catch (OperationCanceledException)
        {
            // Coalesce bursts from file-system watchers into one refresh.
        }
    }

    /// <summary>
    /// Loads launchable applications for the empty state and rebuilds its result pool.
    /// </summary>
    public async Task LoadRecommendationsAsync()
    {
        _recentContentItems.Clear();

        if (_settingsService.Settings.SearchShowRecommendations)
        {
            try
            {
                var identities = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var engineItems = await _searchEngine.GetRecommendationsAsync();
                foreach (var recommendation in engineItems.Where(IsApplicationRecommendation))
                {
                    var item = ToResultItem(recommendation);
                    if (identities.Add(Services.SearchResultRanker.GetIdentityKey(item)))
                    {
                        _recentContentItems.Add(item);
                    }
                }

                // Real app launches are useful secondary evidence, but widget shortcuts
                // remain first because the engine intentionally returns them first.
                foreach (var recent in _historyService.RecentResults.Where(IsApplicationRecommendation))
                {
                    var item = ToResultItem(recent);
                    if (identities.Add(Services.SearchResultRanker.GetIdentityKey(item)))
                    {
                        _recentContentItems.Add(item);
                    }
                }
            }
            catch (Exception ex)
            {
                App.Log($"[SearchPopup] Failed to load recommendations: {ex.Message}");
            }
        }

        RebuildEmptyStateItems();

        // Shortcut and executable recommendations use their real app icons.
        if (_recentContentItems.Count > 0)
        {
            _ = EnrichResultsAsync(
                _recentContentItems,
                CancellationToken.None,
                hideShortcutArrowOverlay: true);
        }
    }

    /// <summary>
    /// Rebuilds the application recommendation pool and, when no query is active,
    /// the tab bar.
    /// </summary>
    private void RebuildEmptyStateItems()
    {
        _emptyStateItems.Clear();

        _emptyStateItems.AddRange(_recentContentItems);

        if (!IsQueryActive)
        {
            RebuildTabs();
        }
    }

    private static SearchResultItem ToResultItem(SearchRecommendationItem rec) => new()
    {
        Kind = rec.Kind,
        Title = rec.Title,
        Subtitle = rec.Subtitle,
        DetailPath = rec.DetailPath,
        Glyph = rec.Glyph,
        ActionId = rec.ActionId,
        TodoWidgetId = rec.TodoWidgetId,
        TodoItemId = rec.TodoItemId,
        QuickCaptureItemId = rec.QuickCaptureItemId,
        HistoryQuery = rec.HistoryQuery
    };

    private static bool IsApplicationRecommendation(SearchRecommendationItem item) =>
        item.Kind == SearchResultKind.File &&
        FileCategoryHelper.Categorize(item.Title) == FileCategory.App;

    private static bool IsApplicationRecommendation(SearchResultItem item) =>
        item.Kind == SearchResultKind.File &&
        FileCategoryHelper.Categorize(item.Title) == FileCategory.App;

    /// <summary>
    /// Performs search with debouncing, then rebuilds the tab bar from the flat
    /// result pool and kicks off lazy metadata enrichment.
    /// </summary>
    private async Task SearchAsync(string query)
    {
        _searchCts?.Cancel();
        long generation = Interlocked.Increment(ref _searchGeneration);

        if (string.IsNullOrWhiteSpace(query))
        {
            _allResults = [];
            IsQueryActive = false;
            HasResults = false;
            IsSearching = false;
            StatusText = string.Empty;
            // Refresh history — the previous query may have just been recorded.
            RebuildEmptyStateItems();
            return;
        }

        _searchCts = new CancellationTokenSource();
        var token = _searchCts.Token;
        _allResults = [];
        IsQueryActive = true;
        HasResults = false;
        IsSearching = true;
        StatusText = _localizationService.T("Search.Status.Searching");
        RebuildTabs();

        try
        {
            await Task.Delay(80, token);
            var response = await _searchEngine.SearchAsync(query, token);
            if (token.IsCancellationRequested)
            {
                return;
            }

            _allResults = response.RankedItems.Count > 0
                ? response.RankedItems.ToList()
                : response.Groups.SelectMany(g => g.Items).ToList();

            // Stamp each result with a localized type label once (cheap, no I/O).
            foreach (var item in _allResults)
            {
                item.TypeDisplay = GetTypeDisplay(item);
            }
            IsQueryActive = true;
            HasResults = _allResults.Count > 0;
            StatusText = string.Format(
                _localizationService.T("Search.Status.Results"),
                response.TotalResultCount,
                response.Elapsed.TotalMilliseconds);
            RebuildTabs();

            _ = EnrichResultsAsync(_allResults, token);
        }
        catch (OperationCanceledException)
        {
            // Search was cancelled by new query
        }
        catch (Exception ex)
        {
            App.Log($"[SearchPopup] Search error: {ex.Message}");
            StatusText = _localizationService.T("Search.Status.Error");
        }
        finally
        {
            if (generation == Volatile.Read(ref _searchGeneration))
            {
                IsSearching = false;
            }
        }
    }

    /// <summary>
    /// Fills in icons/sizes/dates for a result batch in the background, then
    /// re-renders the current tab (preserving the selection) so the new
    /// metadata becomes visible.
    /// </summary>
    private async Task EnrichResultsAsync(
        List<SearchResultItem> items,
        CancellationToken token,
        bool hideShortcutArrowOverlay = false)
    {
        try
        {
            await _fileMetaService.EnrichAsync(items, token, hideShortcutArrowOverlay);
            if (token.IsCancellationRequested)
            {
                return;
            }

            RebuildCurrentResults(preserveSelection: true);
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer query — ignore.
        }
        catch (Exception ex)
        {
            App.Log($"[SearchPopup] Metadata enrichment error: {ex.Message}");
        }
    }

    /// <summary>
    /// Rebuilds a stable, intentionally small set of top-level scopes. File media
    /// categories belong in secondary filtering rather than competing with File.
    /// </summary>
    private void RebuildTabs()
    {
        string? previousTabId = SelectedTab?.Id;
        Tabs.Clear();

        if (IsQueryActive)
        {
            AddTab("all", "Search.Tab.All", "\uE71D", _ => true, supportsFileSort: true);
            AddTab("app", "Search.Tab.App", "\uE7AC",
                item => item.Kind == SearchResultKind.File &&
                        FileCategoryHelper.Categorize(item.Title) == FileCategory.App,
                supportsFileSort: false);
            AddTab("file", "Search.Tab.File", "\uE8E5",
                item => item.Kind is SearchResultKind.File or SearchResultKind.Folder,
                supportsFileSort: true);
            AddTab("deskbox", "Search.Tab.DeskBox", "\uE80F",
                item => item.Kind is SearchResultKind.Todo or SearchResultKind.QuickCapture or SearchResultKind.Action,
                supportsFileSort: false);
        }
        else
        {
            AddTab("home", "Search.Tab.App", "\uE7AC", _ => true, supportsFileSort: false);
        }

        string preferredTabId = IsQueryActive && previousTabId is null or "home"
            ? NormalizeDefaultTab(_settingsService.Settings.SearchDefaultTab)
            : previousTabId ?? string.Empty;
        SelectedTab = Tabs.FirstOrDefault(t => t.Id == preferredTabId) ?? Tabs.FirstOrDefault();
        if (SelectedTab is null)
        {
            CurrentResults.Clear();
            HasCurrentResults = false;
            SelectedIndex = -1;
            SelectedItem = null;
        }
    }

    /// <summary>Public entry point for language-change refresh.</summary>
    public void RebuildTabsPublic() => RebuildTabs();

    /// <summary>Cycles the selected tab forward (or backward when <paramref name="backward"/> is true).</summary>
    public void CycleTab(bool backward)
    {
        if (Tabs.Count == 0)
        {
            return;
        }

        int idx = SelectedTab is null ? 0 : Tabs.IndexOf(SelectedTab);
        idx = backward
            ? (idx - 1 + Tabs.Count) % Tabs.Count
            : (idx + 1) % Tabs.Count;
        SelectedTab = Tabs[idx];
    }

    /// <summary>Returns a localized type label for a search result.</summary>
    private string GetTypeDisplay(SearchResultItem item) => item.Kind switch
    {
        SearchResultKind.Folder => _localizationService.T("Search.Type.Folder"),
        SearchResultKind.Todo => _localizationService.T("Search.Type.Todo"),
        SearchResultKind.QuickCapture => _localizationService.T("Search.Type.Note"),
        SearchResultKind.Action => _localizationService.T("Search.Type.Action"),
        SearchResultKind.File => FileCategoryHelper.Categorize(item.Title) switch
        {
            FileCategory.App => _localizationService.T("Search.Type.App"),
            FileCategory.Image => _localizationService.T("Search.Type.Image"),
            FileCategory.Document => _localizationService.T("Search.Type.Document"),
            FileCategory.Video => _localizationService.T("Search.Type.Video"),
            FileCategory.Music => _localizationService.T("Search.Type.Music"),
            FileCategory.Archive => _localizationService.T("Search.Type.Archive"),
            _ => _localizationService.T("Search.Type.File"),
        },
        _ => string.Empty,
    };

    private void AddTab(
        string id,
        string nameKey,
        string glyph,
        Func<SearchResultItem, bool> predicate,
        bool supportsFileSort,
        bool onlyIfNonEmpty = false)
    {
        int count = ActivePool.Count(predicate);
        if (onlyIfNonEmpty && count == 0)
        {
            return;
        }

        Tabs.Add(new SearchTabItem
        {
            Id = id,
            DisplayName = _localizationService.T(nameKey),
            Glyph = glyph,
            Predicate = predicate,
            SupportsFileSort = supportsFileSort,
            Count = count
        });
    }

    /// <summary>
    /// Re-filters and re-sorts <see cref="CurrentResults"/> for the selected tab.
    /// Sorting is scoped to the current tab only.
    /// </summary>
    private void RebuildCurrentResults(bool preserveSelection = false)
    {
        var previous = SelectedItem;
        CurrentResults.Clear();

        var tab = SelectedTab;
        if (tab is not null)
        {
            foreach (var item in GetSortedTabItems(tab))
            {
                CurrentResults.Add(item);
            }
        }

        HasCurrentResults = CurrentResults.Count > 0;

        if (preserveSelection && previous is not null)
        {
            int index = CurrentResults.IndexOf(previous);
            if (index >= 0)
            {
                SelectedIndex = index;
                SelectedItem = previous;
                return;
            }
        }

        SelectedIndex = CurrentResults.Count > 0 ? 0 : -1;
        SelectedItem = SelectedIndex >= 0 ? CurrentResults[SelectedIndex] : null;
    }

    private List<SearchResultItem> GetSortedTabItems(SearchTabItem tab)
    {
        IEnumerable<SearchResultItem> items = ActivePool.Where(tab.Predicate);

        if (tab.Id == "all")
        {
            items = items.Where(MatchesResultFilter);
        }

        // Relevance (engine order) is the default; name/size/date sorting only
        // applies to file-style tabs.
        if (!tab.SupportsFileSort || SortColumn == ResultSortColumn.Relevance)
        {
            return items.ToList();
        }

        return SortColumn switch
        {
            ResultSortColumn.Name => SortAscending
                ? items.OrderBy(i => i.Title, StringComparer.OrdinalIgnoreCase).ToList()
                : items.OrderByDescending(i => i.Title, StringComparer.OrdinalIgnoreCase).ToList(),
            ResultSortColumn.Size => SortAscending
                ? items.OrderBy(i => i.FileSize ?? long.MaxValue).ToList()
                : items.OrderByDescending(i => i.FileSize ?? long.MinValue).ToList(),
            ResultSortColumn.Date => SortAscending
                ? items.OrderBy(i => i.CreatedAt ?? DateTimeOffset.MaxValue).ToList()
                : items.OrderByDescending(i => i.CreatedAt ?? DateTimeOffset.MinValue).ToList(),
            ResultSortColumn.Type => SortAscending
                ? items.OrderBy(i => i.TypeDisplay ?? string.Empty, StringComparer.OrdinalIgnoreCase).ToList()
                : items.OrderByDescending(i => i.TypeDisplay ?? string.Empty, StringComparer.OrdinalIgnoreCase).ToList(),
            _ => items.ToList()
        };
    }

    private bool MatchesResultFilter(SearchResultItem item) => ResultFilter switch
    {
        SearchResultFilter.FilesAndFolders => item.Kind is SearchResultKind.File or SearchResultKind.Folder,
        SearchResultFilter.Apps => item.Kind == SearchResultKind.File &&
                                   FileCategoryHelper.Categorize(item.Title) == FileCategory.App,
        SearchResultFilter.Images => item.Kind == SearchResultKind.File &&
                                     FileCategoryHelper.Categorize(item.Title) == FileCategory.Image,
        SearchResultFilter.Documents => item.Kind == SearchResultKind.File &&
                                        FileCategoryHelper.Categorize(item.Title) == FileCategory.Document,
        SearchResultFilter.DeskBox => item.Kind is SearchResultKind.Todo or
                                      SearchResultKind.QuickCapture or
                                      SearchResultKind.Action,
        _ => true
    };

    private static string NormalizeDefaultTab(string? value)
    {
        string normalized = value?.Trim().ToLowerInvariant() ?? "all";
        return normalized is "all" or "app" or "file" or "deskbox" ? normalized : "all";
    }

    /// <summary>
    /// Switches the sort column (or toggles direction when re-clicking the same
    /// column). Sensible defaults: name ascending, size/date descending.
    /// </summary>
    public void ToggleSort(ResultSortColumn column)
    {
        if (SortColumn == column)
        {
            SortAscending = !SortAscending;
            return;
        }

        SortColumn = column;
        SortAscending = column switch
        {
            ResultSortColumn.Name => true,
            ResultSortColumn.Size => false,
            ResultSortColumn.Date => false,
            ResultSortColumn.Type => true,
            _ => true
        };
    }

    /// <summary>
    /// Moves selection up in the current tab's result list.
    /// </summary>
    public void MoveSelectionUp()
    {
        if (CurrentResults.Count == 0)
        {
            return;
        }

        SelectedIndex = Math.Max(0, SelectedIndex - 1);
        SelectedItem = CurrentResults[SelectedIndex];
    }

    /// <summary>
    /// Moves selection down in the current tab's result list.
    /// </summary>
    public void MoveSelectionDown()
    {
        if (CurrentResults.Count == 0)
        {
            return;
        }

        SelectedIndex = Math.Min(CurrentResults.Count - 1, SelectedIndex + 1);
        SelectedItem = CurrentResults[SelectedIndex];
    }

    /// <summary>
    /// Executes the default action for the selected item.
    /// Returns true if an action was executed.
    /// </summary>
    public bool ExecuteSelectedItem()
    {
        var item = SelectedItem;
        if (item is null)
        {
            return false;
        }

        return ExecuteItem(item);
    }

    /// <summary>
    /// Executes the default action for a specific item.
    /// </summary>
    public bool ExecuteItem(SearchResultItem item)
    {
        switch (item.Kind)
        {
            case SearchResultKind.File:
                if (!string.IsNullOrWhiteSpace(item.DetailPath))
                {
                    if (DeskBox.Helpers.Win32Helper.OpenFileOrChooseApp(OwnerWindowHandle, item.DetailPath))
                    {
                        CommitExecution(item);
                        HidePopupCallback?.Invoke();
                        return true;
                    }
                }
                break;

            case SearchResultKind.Folder:
                // Folders always open in Explorer.
                if (!string.IsNullOrWhiteSpace(item.DetailPath))
                {
                    OpenPath(item.DetailPath);
                    CommitExecution(item);
                    HidePopupCallback?.Invoke();
                    return true;
                }
                break;

            case SearchResultKind.Action:
                CommitExecution(item, recordResult: false);
                ExecuteAction(item.ActionId);
                return true;

            case SearchResultKind.Todo:
                CommitExecution(item);
                ContentRequested?.Invoke(this, item);
                return true;

            case SearchResultKind.QuickCapture:
                CommitExecution(item);
                ContentRequested?.Invoke(this, item);
                return true;

            case SearchResultKind.History:
            case SearchResultKind.Favorite:
                if (!string.IsNullOrWhiteSpace(item.HistoryQuery))
                {
                    ApplyQuery(item.HistoryQuery);
                    return true;
                }
                break;
        }

        return false;
    }

    public bool OpenSelectedLocation()
    {
        var item = SelectedItem;
        if (item is null || string.IsNullOrWhiteSpace(item.DetailPath) ||
            item.Kind is not (SearchResultKind.File or SearchResultKind.Folder))
        {
            return false;
        }

        try
        {
            string path = item.DetailPath;
            if (item.Kind == SearchResultKind.Folder)
            {
                string? parent = Directory.GetParent(path)?.FullName;
                if (!string.IsNullOrWhiteSpace(parent))
                {
                    System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{path}\"");
                }
                else
                {
                    OpenPath(path);
                }
            }
            else
            {
                System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{path}\"");
            }

            CommitExecution(item);
            HidePopupCallback?.Invoke();
            return true;
        }
        catch (Exception ex)
        {
            App.Log($"[SearchPopup] Failed to open result location: {ex.Message}");
            return false;
        }
    }

    private void CommitExecution(SearchResultItem item, bool recordResult = true)
    {
        _historyService.RecordQuery(Query);
        if (recordResult)
        {
            _historyService.RecordResult(item);
        }

        OnPropertyChanged(nameof(IsCurrentQueryFavorite));
    }

    /// <summary>
    /// Invokes a top-level action (used by the horizontal quick-action buttons).
    /// </summary>
    public void InvokeAction(string actionId)
    {
        ExecuteAction(actionId);
    }

    /// <summary>
    /// Sets the search box query (used by history/favorite activation) and re-runs search.
    /// </summary>
    public void ApplyQuery(string query)
    {
        Query = query;
        QueryApplied?.Invoke(this, query);
    }

    /// <summary>
    /// Whether the current query is pinned as a favorite.
    /// </summary>
    public bool IsCurrentQueryFavorite => _historyService.IsFavorite(Query);

    /// <summary>
    /// Toggles the current query in favorites and returns the new state.
    /// </summary>
    public bool ToggleFavoriteForCurrentQuery()
    {
        bool isFavorite = _historyService.ToggleFavorite(Query);
        OnPropertyChanged(nameof(IsCurrentQueryFavorite));
        return isFavorite;
    }

    /// <summary>
    /// Clears all recent search history (one-click cleanup) and refreshes the
    /// empty-state tabs so the recent-searches tab collapses.
    /// </summary>
    public void ClearRecentSearches()
    {
        _historyService.ClearRecentHistory();
        RebuildEmptyStateItems();
        OnPropertyChanged(nameof(HasHistoryOrRecommendations));
    }

    /// <summary>
    /// Clears both favorites and recent searches completely.
    /// </summary>
    public void ClearAllHistory()
    {
        _historyService.ClearAllHistory();
        RebuildEmptyStateItems();
        OnPropertyChanged(nameof(HasHistoryOrRecommendations));
    }

    /// <summary>
    /// Clears the current query and results.
    /// </summary>
    public void ClearSearch()
    {
        _searchCts?.Cancel();
        Query = string.Empty;
        _allResults = [];
        IsQueryActive = false;
        IsSearching = false;
        HasResults = false;
        StatusText = string.Empty;
        RebuildEmptyStateItems();
    }

    public Task RefreshSearchAsync()
    {
        return string.IsNullOrWhiteSpace(Query)
            ? LoadRecommendationsAsync()
            : SearchAsync(Query);
    }

    /// <summary>
    /// Called when the popup becomes visible.
    /// </summary>
    public async Task OnPopupOpenedAsync()
    {
        ClearSearch();
        await LoadRecommendationsAsync();
    }

    private void ExecuteAction(string? actionId)
    {
        switch (actionId)
        {
            case "new-todo":
                ActionRequested?.Invoke(this, "new-todo");
                break;
            case "new-note":
                ActionRequested?.Invoke(this, "new-note");
                break;
            case "open-settings":
                ActionRequested?.Invoke(this, "open-settings");
                break;
            case "toggle-widgets":
                ActionRequested?.Invoke(this, "toggle-widgets");
                break;
            case "toggle-theme":
                ActionRequested?.Invoke(this, "toggle-theme");
                break;
            case "open-todo":
                ActionRequested?.Invoke(this, "open-todo");
                break;
            case "open-quickcapture":
                ActionRequested?.Invoke(this, "open-quickcapture");
                break;
        }
    }

    private static void OpenPath(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                System.Diagnostics.Process.Start("explorer.exe", $"\"{path}\"");
            }
            else if (File.Exists(path))
            {
                System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{path}\"");
            }
        }
        catch (Exception ex)
        {
            App.Log($"[SearchPopup] Failed to open path '{path}': {ex.Message}");
        }
    }

    private string GetHotkeyHint()
    {
        var settings = _settingsService.Settings;
        if (!settings.SearchHotkeyEnabled)
        {
            return string.Empty;
        }

        var parts = new List<string>();
        var modifiers = (HotkeyModifierKeys)settings.SearchHotkeyModifiers;
        if (modifiers.HasFlag(HotkeyModifierKeys.Control))
        {
            parts.Add("Ctrl");
        }

        if (modifiers.HasFlag(HotkeyModifierKeys.Alt))
        {
            parts.Add("Alt");
        }

        if (modifiers.HasFlag(HotkeyModifierKeys.Shift))
        {
            parts.Add("Shift");
        }

        // Map virtual key to display name
        string keyName = settings.SearchHotkeyKey switch
        {
            0x20 => "Space",
            >= 0x41 and <= 0x5A => ((char)settings.SearchHotkeyKey).ToString(),
            >= 0x30 and <= 0x39 => ((char)settings.SearchHotkeyKey).ToString(),
            _ => $"VK:{settings.SearchHotkeyKey:X2}"
        };
        parts.Add(keyName);

        return string.Join("+", parts);
    }

    /// <summary>
    /// Raised when an action requires external handling (e.g., open settings, create todo).
    /// </summary>
    public event EventHandler<string>? ActionRequested;

    /// <summary>Raised when a DeskBox result should open its exact source item.</summary>
    public event EventHandler<SearchResultItem>? ContentRequested;

    /// <summary>
    /// Raised when a history/favorite query is applied and the search box should update.
    /// </summary>
    public event EventHandler<string>? QueryApplied;

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        _searchEngine.IndexUpdated -= OnIndexUpdated;
        _indexRefreshCts?.Cancel();
        _indexRefreshCts?.Dispose();
        _searchCts?.Cancel();
        _searchCts?.Dispose();
    }
}
