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
    private CancellationTokenSource? _resultRefreshCts;
    private CancellationTokenSource? _recommendationCts;
    private long _recommendationGeneration;
    private readonly object _metadataTaskGate = new();
    private readonly Dictionary<SearchResultItem, Task> _metadataTasks =
        new(ReferenceEqualityComparer.Instance);
    private long _searchGeneration;
    private bool _isDisposed;
    private int _nextFileResultOffset;
    private int _loadMoreRunning;
    private const int MaxEnrichedSearchResults = 40;
    private static readonly TimeSpan ProviderRefreshDebounceDelay = TimeSpan.FromSeconds(1);

    private enum SearchRefreshKind
    {
        UserQuery,
        ProviderUpdate,
        LoadMore
    }

    /// <summary>Flat results for the active query, in engine relevance order.</summary>
    private List<SearchResultItem> _allResults = [];

    /// <summary>Empty-state pool: launchable application recommendations.</summary>
    private readonly List<SearchResultItem> _emptyStateItems = [];

    /// <summary>Recommended applications cached between empty-state rebuilds.</summary>
    private readonly List<SearchResultItem> _recentContentItems = [];

    /// <summary>
    /// Wall-clock time of the last successful recommendation load. Used to skip
    /// the 1-second skeleton reload when the popup re-opens within a short window
    /// — the user sees the icons immediately.
    /// </summary>
    private DateTime _lastRecommendationLoadUtc = DateTime.MinValue;

    /// <summary>Cache TTL: if the last load is within this window, reuse the cached data.</summary>
    private static readonly TimeSpan RecommendationCacheTtl = TimeSpan.FromSeconds(60);

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
        _searchEngine.ResultsChanged += OnResultsChanged;

        // Callback to close popup when item is opened.
        HidePopupCallback = () => { };
    }

    public Action? HidePopupCallback;

    public IntPtr OwnerWindowHandle { get; set; }

    [ObservableProperty]
    public partial string Query { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsSearching { get; set; }

    [ObservableProperty]
    public partial bool HasResults { get; set; }

    /// <summary>True while a non-empty query is active; drives the tab strategy.</summary>
    [ObservableProperty]
    public partial bool IsQueryActive { get; set; }

    /// <summary>Whether there are application recommendations to show.</summary>
    [ObservableProperty]
    public partial SearchResultItem? SelectedItem { get; set; }

    [ObservableProperty]
    public partial int SelectedIndex { get; set; } = -1;

    [ObservableProperty]
    public partial string StatusText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial SearchTabItem? SelectedTab { get; set; }

    [ObservableProperty]
    public partial ResultSortColumn SortColumn { get; set; } = ResultSortColumn.Relevance;

    [ObservableProperty]
    public partial bool SortAscending { get; set; } = true;

    [ObservableProperty]
    public partial SearchResultFilter ResultFilter { get; set; } = SearchResultFilter.All;

    [ObservableProperty]
    public partial bool HasCurrentResults { get; set; }

    [ObservableProperty]
    public partial bool HasMoreResults { get; set; }

    [ObservableProperty]
    public partial bool IsLoadingMore { get; set; }

    [ObservableProperty]
    public partial int TotalResultCount { get; set; }

    /// <summary>The dynamic tab bar. Rebuilt on every search / empty-state change.</summary>
    public ObservableCollection<SearchTabItem> Tabs { get; } = [];

    /// <summary>Filtered + sorted view of the active pool for <see cref="SelectedTab"/>.</summary>
    public ObservableCollection<SearchResultItem> CurrentResults { get; } = [];

    public string DisplayMode => _settingsService.Settings.SearchDisplayMode;
    public string HotkeyHint => GetHotkeyHint();

    /// <summary>
    /// True only while an index-driven update publishes its final result set. The
    /// view uses this transient state to avoid replaying the user-search entrance
    /// animation when background file-system activity changes a query's results.
    /// </summary>
    public bool IsApplyingBackgroundResultRefresh { get; private set; }

    /// <summary>Public access to recent queries for UI binding.</summary>
    public IReadOnlyList<string> RecentQueries => _historyService.RecentQueries;

    /// <summary>Public access to favorite queries for UI binding.</summary>
    public IReadOnlyList<string> FavoriteQueries => _historyService.FavoriteQueries;

    /// <summary>True if there's any history or recommendations to display.</summary>
    public bool HasHistoryOrRecommendations => _recentContentItems.Any();

    /// <summary>
    /// Whether search history (recent queries + favorites) is currently being
    /// recorded and shown. Driven by the <see cref="AppSettings.SearchSaveHistory"/> flag.
    /// </summary>
    public bool SaveSearchHistory => _settingsService.Settings.SearchSaveHistory;

    /// <summary>
    /// Enables or disables search-history recording and persists the change.
    /// </summary>
    public void SetSaveSearchHistory(bool enabled)
    {
        if (_settingsService.Settings.SearchSaveHistory == enabled)
        {
            return;
        }

        _settingsService.Settings.SearchSaveHistory = enabled;
        _settingsService.SaveDebounced();
        OnPropertyChanged(nameof(SaveSearchHistory));
    }

    /// <summary>Home mode: a roomier dashboard.</summary>
    public bool IsHomeMode => string.Equals(DisplayMode, "Home", StringComparison.OrdinalIgnoreCase);

    /// <summary>Palette (command) mode: a compact launcher.</summary>
    public bool IsPaletteMode => string.Equals(DisplayMode, "Palette", StringComparison.OrdinalIgnoreCase);

    /// <summary>Spotlight mode: the default balanced search experience.</summary>
    public bool IsSpotlightMode => !IsHomeMode && !IsPaletteMode;

    private IReadOnlyList<SearchResultItem> ActivePool => IsQueryActive ? _allResults : _emptyStateItems;

    partial void OnQueryChanged(string value)
    {
        // A fresh query re-scopes every provider; carrying a stale All-tab
        // type filter across queries silently hides non-matching results.
        ResultFilter = SearchResultFilter.All;
        _ = SearchAsync(value, SearchRefreshKind.UserQuery);
    }

    partial void OnSelectedTabChanged(SearchTabItem? value)
    {
        UpdateTabSelectionState(value);
        RebuildCurrentResults();
    }

    private void UpdateTabSelectionState(SearchTabItem? selectedTab)
    {
        foreach (SearchTabItem tab in Tabs)
        {
            tab.IsSelected = ReferenceEquals(tab, selectedTab);
        }
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

    private void OnResultsChanged()
    {
        void ScheduleRefresh()
        {
            if (_isDisposed || string.IsNullOrWhiteSpace(Query))
            {
                return;
            }

            _resultRefreshCts?.Cancel();
            _resultRefreshCts?.Dispose();
            _resultRefreshCts = new CancellationTokenSource();
            _ = RefreshAfterResultsChangedAsync(Query, _resultRefreshCts.Token);
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

    private async Task RefreshAfterResultsChangedAsync(
        string query,
        CancellationToken cancellationToken)
    {
        try
        {
            // File-system watchers can produce bursts for a single user operation.
            // Wait for a quiet interval, then leave an active user search alone; its
            // own result set is newer than this background invalidation request.
            await Task.Delay(ProviderRefreshDebounceDelay, cancellationToken);
            if (!string.IsNullOrWhiteSpace(query) &&
                string.Equals(query, Query, StringComparison.Ordinal) &&
                !IsSearching)
            {
                await SearchAsync(query, SearchRefreshKind.ProviderUpdate);
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
        _recommendationCts?.Cancel();
        _recommendationCts?.Dispose();
        _recommendationCts = new CancellationTokenSource();
        CancellationToken token = _recommendationCts.Token;
        long generation = Interlocked.Increment(ref _recommendationGeneration);
        bool hadRecommendationCache = HasRecommendationCache;
        var loadedItems = new List<SearchResultItem>();
        bool recommendationsEnabled =
            _settingsService.Settings.SearchShowRecommendations;

        if (recommendationsEnabled)
        {
            try
            {
                var identities = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var engineItems = await _searchEngine.GetRecommendationsAsync(token);
                foreach (var recommendation in engineItems.Where(IsApplicationRecommendation))
                {
                    token.ThrowIfCancellationRequested();
                    var item = ToResultItem(recommendation);
                    if (identities.Add(Services.SearchResultRanker.GetIdentityKey(item)))
                    {
                        loadedItems.Add(item);
                    }
                }

                // Real app launches are useful secondary evidence, but widget shortcuts
                // remain first because the engine intentionally returns them first.
                foreach (var recent in _historyService.RecentResults.Where(IsApplicationRecommendation))
                {
                    token.ThrowIfCancellationRequested();
                    var item = ToResultItem(recent);
                    if (identities.Add(Services.SearchResultRanker.GetIdentityKey(item)))
                    {
                        loadedItems.Add(item);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                App.Log($"[SearchPopup] Failed to load recommendations: {ex.Message}");
            }
        }

        if (token.IsCancellationRequested)
        {
            return;
        }

        if (!recommendationsEnabled)
        {
            ReplaceRecommendationItems([]);
            _lastRecommendationLoadUtc = DateTime.MinValue;
            return;
        }

        // On the first application open, publish the inexpensive recommendation
        // identities immediately. Resolving every shell icon can take noticeably
        // longer on a cold Windows shell cache; keeping the list private until that
        // finishes makes the popup look empty even though the apps are already known.
        if (!hadRecommendationCache)
        {
            ReplaceRecommendationItems(loadedItems);
            if (loadedItems.Count > 0)
            {
                _ = CompletePublishedRecommendationEnrichmentAsync(
                    loadedItems,
                    generation,
                    token);
            }

            return;
        }

        // Do not replace a useful cache with an empty transient refresh result.
        if (loadedItems.Count == 0)
        {
            return;
        }

        // Shortcut and executable recommendations use their real app icons.
        // Enrich the replacement list off-screen so an expired cache stays visible
        // until the refreshed icons are ready.
        await EnrichResultsAsync(
            loadedItems,
            token,
            hideShortcutArrowOverlay: true);

        if (token.IsCancellationRequested ||
            generation != Volatile.Read(ref _recommendationGeneration))
        {
            return;
        }

        ReplaceRecommendationItems(loadedItems);

        // Mark the cache timestamp so subsequent popup opens can skip the load
        // and display icons immediately.
        _lastRecommendationLoadUtc = DateTime.UtcNow;
    }

    private async Task CompletePublishedRecommendationEnrichmentAsync(
        List<SearchResultItem> publishedItems,
        long generation,
        CancellationToken token)
    {
        await EnrichResultsAsync(
            publishedItems,
            token,
            hideShortcutArrowOverlay: true);

        if (!token.IsCancellationRequested &&
            generation == Volatile.Read(ref _recommendationGeneration))
        {
            _lastRecommendationLoadUtc = DateTime.UtcNow;
        }
    }

    private void ReplaceRecommendationItems(
        IReadOnlyCollection<SearchResultItem> items)
    {
        _recentContentItems.Clear();
        _recentContentItems.AddRange(items);
        RebuildEmptyStateItems();
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
    private async Task SearchAsync(string query, SearchRefreshKind refreshKind)
    {
        CancelCurrentSearch();
        long generation = Interlocked.Increment(ref _searchGeneration);

        if (string.IsNullOrWhiteSpace(query))
        {
            _allResults = [];
            _nextFileResultOffset = 0;
            IsQueryActive = false;
            HasResults = false;
            HasMoreResults = false;
            TotalResultCount = 0;
            IsSearching = false;
            StatusText = string.Empty;
            // Refresh history — the previous query may have just been recorded.
            RebuildEmptyStateItems();
            return;
        }

        var searchCts = new CancellationTokenSource();
        _searchCts = searchCts;
        CancellationToken token = searchCts.Token;
        if (refreshKind == SearchRefreshKind.UserQuery)
        {
            _nextFileResultOffset = 0;
        }

        bool preserveVisibleResults = refreshKind != SearchRefreshKind.UserQuery &&
                                       IsQueryActive &&
                                       HasResults;
        if (!preserveVisibleResults)
        {
            _allResults = [];
        }
        IsQueryActive = true;
        if (!preserveVisibleResults)
        {
            HasResults = false;
        }
        IsSearching = refreshKind != SearchRefreshKind.LoadMore;
        if (IsSearching)
        {
            StatusText = _localizationService.T("Search.Status.Searching");
        }
        if (!preserveVisibleResults)
        {
            RebuildTabs();
        }

        try
        {
            int fileResultOffset = refreshKind == SearchRefreshKind.LoadMore
                ? _nextFileResultOffset
                : 0;
            int fileResultPageSize = refreshKind == SearchRefreshKind.ProviderUpdate
                ? Math.Max(
                    Services.SearchEngineService.InitialFileResultPageSize,
                    _nextFileResultOffset)
                : Services.SearchEngineService.FileResultPageSize;
            SearchResponse response = await _searchEngine.SearchPageAsync(
                query,
                fileResultOffset,
                fileResultPageSize,
                token);
            if (token.IsCancellationRequested ||
                generation != Volatile.Read(ref _searchGeneration))
            {
                return;
            }

            ApplySearchResponse(response, token, refreshKind);
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

    public async Task LoadMoreResultsAsync()
    {
        if (_isDisposed ||
            !HasMoreResults ||
            string.IsNullOrWhiteSpace(Query) ||
            Interlocked.Exchange(ref _loadMoreRunning, 1) != 0)
        {
            return;
        }

        IsLoadingMore = true;
        try
        {
            await SearchAsync(Query, SearchRefreshKind.LoadMore);
        }
        finally
        {
            IsLoadingMore = false;
            Volatile.Write(ref _loadMoreRunning, 0);
        }
    }

    private void ApplySearchResponse(
        SearchResponse response,
        CancellationToken token,
        SearchRefreshKind refreshKind)
    {
        // Preserve compatibility with incomplete provider responses without
        // replacing a stable background snapshot.
        if (refreshKind == SearchRefreshKind.ProviderUpdate && !response.IsComplete)
        {
            return;
        }

        List<SearchResultItem> incoming = response.RankedItems.Count > 0
            ? response.RankedItems.ToList()
            : response.Groups.SelectMany(g => g.Items).ToList();

        // Stamp each result with a localized type label once (cheap, no I/O).
        foreach (SearchResultItem item in incoming)
        {
            item.TypeDisplay = GetTypeDisplay(item);
        }

        if (refreshKind == SearchRefreshKind.LoadMore)
        {
            incoming = MergeLoadedPage(_allResults, incoming);
        }

        bool backgroundResultUnchanged = refreshKind == SearchRefreshKind.ProviderUpdate &&
            Services.SearchResultCollectionReconciler.HasSameIdentitySequence(
                _allResults,
                incoming);

        if (backgroundResultUnchanged)
        {
            // No visual update means no row recycling, selection reset, or entrance
            // animation while unrelated files are being written elsewhere. Paging
            // metadata may still change as the resident index grows.
            HasMoreResults = response.HasMoreResults;
            TotalResultCount = response.TotalResultCount;
            _nextFileResultOffset = response.NextFileResultOffset;
            return;
        }

        // Reuse identities across user results, loaded pages, and background
        // refreshes. This keeps logical selection and resolved shell metadata stable.
        _allResults = Services.SearchResultCollectionReconciler.ReuseExistingInstances(
            _allResults,
            incoming);

        IsQueryActive = true;
        IsApplyingBackgroundResultRefresh = refreshKind != SearchRefreshKind.UserQuery;
        try
        {
            HasResults = _allResults.Count > 0;
            HasMoreResults = response.HasMoreResults;
            TotalResultCount = response.TotalResultCount;
            _nextFileResultOffset = response.NextFileResultOffset;
            StatusText = GetSearchStatusText(response);
            RebuildTabs();
            RebuildCurrentResults(preserveSelection: true);
        }
        finally
        {
            IsApplyingBackgroundResultRefresh = false;
        }

        if (response.IsComplete)
        {
            _ = EnrichResultsAsync(
                _allResults.Take(MaxEnrichedSearchResults).ToList(),
                token);
        }
    }

    private static List<SearchResultItem> MergeLoadedPage(
        IReadOnlyList<SearchResultItem> current,
        IReadOnlyList<SearchResultItem> page)
    {
        var byIdentity = new Dictionary<string, SearchResultItem>(
            StringComparer.OrdinalIgnoreCase);
        foreach (SearchResultItem item in current)
        {
            byIdentity[Services.SearchResultRanker.GetIdentityKey(item)] = item;
        }

        foreach (SearchResultItem item in page)
        {
            string identity = Services.SearchResultRanker.GetIdentityKey(item);
            if (!byIdentity.ContainsKey(identity))
            {
                byIdentity[identity] = item;
            }
        }

        return byIdentity.Values
            .OrderByDescending(item => item.RelevanceScore)
            .ThenByDescending(item => item.ModifiedAt)
            .ThenBy(item => item.Title, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    private string GetSearchStatusText(SearchResponse response)
    {
        string? providerStatusKey = response.FileProviderState switch
        {
            EverythingConnectionState.NotConfirmed =>
                "Settings.Search.Everything.Status.NotConfirmed",
            EverythingConnectionState.NotInstalled =>
                "Settings.Search.Everything.Status.NotInstalled",
            EverythingConnectionState.NotRunning =>
                "Settings.Search.Everything.Status.NotRunning",
            EverythingConnectionState.PermissionMismatch =>
                "Settings.Search.Everything.Status.PermissionMismatch",
            EverythingConnectionState.IpcUnavailable =>
                "Settings.Search.Everything.Status.IpcUnavailable",
            EverythingConnectionState.SdkUnavailable =>
                "Settings.Search.Everything.Status.SdkUnavailable",
            EverythingConnectionState.Error =>
                "Search.Status.Error",
            _ => null
        };
        if (providerStatusKey is not null)
        {
            return _localizationService.T(providerStatusKey);
        }

        return string.Format(
            _localizationService.T(response.IsComplete
                ? "Search.Status.Results"
                : "Search.Status.PartialResults"),
            response.TotalResultCount,
            response.Elapsed.TotalMilliseconds);
    }

    private void CancelCurrentSearch()
    {
        CancellationTokenSource? previous = Interlocked.Exchange(ref _searchCts, null);
        if (previous is null)
        {
            return;
        }

        try
        {
            previous.Cancel();
        }
        finally
        {
            previous.Dispose();
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
    /// Resolves metadata for a result only when its row is actually realized.
    /// This keeps large result sets cheap while ensuring rows beyond the initial
    /// enrichment batch never remain as empty icon placeholders.
    /// </summary>
    public Task EnsureResultMetadataAsync(SearchResultItem item)
    {
        if (_isDisposed ||
            item.IconResolved ||
            item.Kind is not (SearchResultKind.File or SearchResultKind.Folder) ||
            string.IsNullOrWhiteSpace(item.DetailPath))
        {
            return Task.CompletedTask;
        }

        lock (_metadataTaskGate)
        {
            if (_metadataTasks.TryGetValue(item, out var existing))
            {
                return existing;
            }

            Task task = EnrichVisibleResultAsync(item);
            _metadataTasks[item] = task;
            _ = task.ContinueWith(
                _ =>
                {
                    lock (_metadataTaskGate)
                    {
                        if (_metadataTasks.TryGetValue(item, out var current) &&
                            ReferenceEquals(current, task))
                        {
                            _metadataTasks.Remove(item);
                        }
                    }
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            return task;
        }
    }

    private async Task EnrichVisibleResultAsync(SearchResultItem item)
    {
        try
        {
            CancellationToken token = _searchCts?.Token ?? CancellationToken.None;
            await _fileMetaService.EnrichAsync([item], token);
        }
        catch (OperationCanceledException)
        {
            // The row belonged to a superseded query.
        }
        catch (ObjectDisposedException)
        {
            // The popup closed while the row metadata was being resolved.
        }
        catch (Exception ex)
        {
            App.Log($"[SearchPopup] Visible metadata enrichment error: {ex.Message}");
        }
    }

    /// <summary>
    /// Rebuilds a stable, intentionally small set of top-level scopes. File media
    /// categories belong in secondary filtering rather than competing with File.
    /// When the tab structure has not changed, only counts are updated in-place to
    /// avoid the visual flicker caused by Clear + re-Add on the ObservableCollection.
    /// </summary>
    private void RebuildTabs()
    {
        string? previousTabId = SelectedTab?.Id;

        // Determine the expected tab IDs for the current state.
        string[] expectedIds = IsQueryActive
            ? ["all", "app", "file", "image", "document", "deskbox"]
            : ["home"];

        // If the structure matches, update counts in-place (no flicker).
        bool structureMatches = Tabs.Count == expectedIds.Length &&
            Tabs.Select((t, i) => t.Id == expectedIds[i]).All(x => x);

        if (structureMatches && Tabs.Count > 0)
        {
            // Only refresh counts; skip Clear/Add to avoid ListView re-render flash.
            foreach (var tab in Tabs)
            {
                tab.Count = ActivePool.Count(tab.Predicate);
            }

            // Ensure selection is still valid.
            if (SelectedTab is null)
            {
                SelectedTab = Tabs.FirstOrDefault();
            }
            else
            {
                // The selected instance did not change, so the generated
                // property callback is not raised. Keep the template's
                // indicator state synchronized explicitly in that case.
                UpdateTabSelectionState(SelectedTab);
            }
            return;
        }

        // Full rebuild (structure changed or first build).
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
            AddTab("image", "Search.Filter.Images", "\uE8B9",
                item => item.Kind == SearchResultKind.File &&
                        FileCategoryHelper.Categorize(item.Title) == FileCategory.Image,
                supportsFileSort: true);
            AddTab("document", "Search.Filter.Documents", "\uE8A5",
                item => item.Kind == SearchResultKind.File &&
                        FileCategoryHelper.Categorize(item.Title) == FileCategory.Document,
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
        UpdateTabSelectionState(SelectedTab);
        if (SelectedTab is null)
        {
            Services.SearchResultCollectionReconciler.Reconcile(CurrentResults, []);
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
        var tab = SelectedTab;
        IReadOnlyList<SearchResultItem> target = [];
        if (tab is not null)
        {
            target = GetSortedTabItems(tab);
        }

        Services.SearchResultCollectionReconciler.Reconcile(CurrentResults, target);

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

        if (SelectedIndex >= CurrentResults.Count - 1 && HasMoreResults)
        {
            _ = LoadMoreAndAdvanceSelectionAsync(SelectedItem);
            return;
        }

        SelectedIndex = Math.Min(CurrentResults.Count - 1, SelectedIndex + 1);
        SelectedItem = CurrentResults[SelectedIndex];
    }

    private async Task LoadMoreAndAdvanceSelectionAsync(SearchResultItem? anchor)
    {
        int previousCount = CurrentResults.Count;
        await LoadMoreResultsAsync();
        if (anchor is null || !ReferenceEquals(SelectedItem, anchor) ||
            CurrentResults.Count <= previousCount)
        {
            return;
        }

        int anchorIndex = CurrentResults.IndexOf(anchor);
        if (anchorIndex >= 0 && anchorIndex + 1 < CurrentResults.Count)
        {
            SelectedIndex = anchorIndex + 1;
            SelectedItem = CurrentResults[SelectedIndex];
        }
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
        if (_settingsService.Settings.SearchSaveHistory)
        {
            _historyService.RecordQuery(Query);
            if (recordResult)
            {
                _historyService.RecordResult(item);
            }
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
        CancelCurrentSearch();
        Query = string.Empty;
        _allResults = [];
        _nextFileResultOffset = 0;
        IsQueryActive = false;
        IsSearching = false;
        HasResults = false;
        HasMoreResults = false;
        TotalResultCount = 0;
        StatusText = string.Empty;
        RebuildEmptyStateItems();
    }

    /// <summary>
    /// Cancels transient popup work while retaining the small recommendation/icon
    /// cache. The whole popup is still disposed by the existing idle cleanup policy.
    /// </summary>
    public void OnPopupHidden()
    {
        CancelCurrentSearch();
        _resultRefreshCts?.Cancel();
        _recommendationCts?.Cancel();
        Query = string.Empty;
        _allResults = [];
        _nextFileResultOffset = 0;
        SelectedItem = null;
        IsQueryActive = false;
        IsSearching = false;
        HasResults = false;
        HasMoreResults = false;
        TotalResultCount = 0;
        StatusText = string.Empty;
        RebuildEmptyStateItems();
    }

    public Task RefreshSearchAsync()
    {
        return string.IsNullOrWhiteSpace(Query)
            ? LoadRecommendationsAsync()
            : SearchAsync(Query, SearchRefreshKind.UserQuery);
    }

    /// <summary>
    /// Called when the popup becomes visible.
    /// </summary>
    public async Task OnPopupOpenedAsync()
    {
        // The type filter picker is only visible on the All tab; a stale
        // filter from an earlier session would silently empty that tab.
        ResultFilter = SearchResultFilter.All;
        ClearSearch();
        // If the recommendations were loaded recently (within the cache TTL),
        // reuse them so the popup shows icons immediately without a skeleton.
        // Otherwise reload in the background.
        if (HasFreshRecommendationCache)
        {
            RebuildEmptyStateItems();
            return;
        }

        if (HasRecommendationCache)
        {
            // Present the last complete set immediately and refresh it atomically.
            _ = LoadRecommendationsAsync();
            return;
        }

        await LoadRecommendationsAsync();
    }

    /// <summary>
    /// True when the recommendation pool was loaded recently enough to reuse
    /// without a network/enrichment pass. The View uses this to decide whether
    /// to show the skeleton screen on popup open.
    /// </summary>
    public bool HasFreshRecommendationCache =>
        _recentContentItems.Count > 0
        && (DateTime.UtcNow - _lastRecommendationLoadUtc) < RecommendationCacheTtl;

    /// <summary>
    /// True when a complete recommendation set is available for immediate display,
    /// even if it is old enough to warrant a background refresh.
    /// </summary>
    public bool HasRecommendationCache => _recentContentItems.Count > 0;

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
        _searchEngine.ResultsChanged -= OnResultsChanged;
        _resultRefreshCts?.Cancel();
        _resultRefreshCts?.Dispose();
        _recommendationCts?.Cancel();
        _recommendationCts?.Dispose();
        CancelCurrentSearch();
        _allResults = [];
        _emptyStateItems.Clear();
        _recentContentItems.Clear();
        CurrentResults.Clear();
        Tabs.Clear();
        lock (_metadataTaskGate)
        {
            _metadataTasks.Clear();
        }
        HidePopupCallback = null;
    }
}
