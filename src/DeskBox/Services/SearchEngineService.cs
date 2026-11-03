using System.Diagnostics;
using DeskBox.Models;

namespace DeskBox.Services;

/// <summary>
/// Coordinates Everything filename results with DeskBox content (QuickCapture, Todo,
/// Clipboard history), Start Menu / File Widget applications (with Pinyin/initials matching),
/// Settings deep-links, and inline calculator / quick-create commands.
/// DeskBox owns no filename index, scanner, filesystem watcher, or persisted catalog.
/// </summary>
public sealed class SearchEngineService : IDisposable
{
    public const int InitialFileResultPageSize = 200;
    public const int FileResultPageSize = 200;
    internal const int MaxIndexedBodyCharacters = 2048;
    private const int DeskBoxContentRefreshIntervalMilliseconds = 1000;
    private const string SettingsPrefix = "Settings.";

    private static readonly object s_activeInstancesLock = new();
    private static readonly List<WeakReference<SearchEngineService>> s_activeInstances = [];

    private readonly SettingsService _settingsService;
    private readonly LocalizationService _localizationService;
    private readonly EverythingSearchService _everythingSearchService;
    private readonly QuickCaptureService _quickCaptureService;
    private readonly Func<CancellationToken, Task<IReadOnlyList<ClipboardHistoryItem>>>? _clipboardItemsProvider;
    private readonly Func<string, double>? _frecencyBoostProvider;
    private readonly Func<ClipboardHistoryItem, string?>? _clipboardPreviewImageResolver;
    private readonly object _deskBoxContentRefreshLock = new();
    private readonly object _settingsCatalogLock = new();
    private readonly CancellationTokenSource _deskBoxContentLifetimeCts = new();
    private DeskBoxSearchDocument[] _deskBoxContentSnapshot = [];
    private SettingsCatalogItem[]? _cachedSettingsCatalog;
    private string? _cachedSettingsCatalogCulture;
    private Task? _deskBoxContentRefreshTask;
    private long _deskBoxContentLastRefreshMs = long.MinValue;
    private int _deskBoxContentInitialized;
    private int _deskBoxContentDirty = 1;
    private int _deskBoxContentCacheGeneration;
    private bool _isDisposed;

    public SearchEngineService(
        SettingsService settingsService,
        LocalizationService localizationService,
        EverythingSearchService everythingSearchService,
        QuickCaptureService quickCaptureService,
        Func<CancellationToken, Task<IReadOnlyList<ClipboardHistoryItem>>>? clipboardItemsProvider = null,
        Func<string, double>? frecencyBoostProvider = null,
        Func<ClipboardHistoryItem, string?>? clipboardPreviewImageResolver = null)
    {
        _settingsService = settingsService;
        _localizationService = localizationService;
        _everythingSearchService = everythingSearchService;
        _quickCaptureService = quickCaptureService;
        _clipboardItemsProvider = clipboardItemsProvider;
        _frecencyBoostProvider = frecencyBoostProvider;
        _clipboardPreviewImageResolver = clipboardPreviewImageResolver;
        _quickCaptureService.Changed += OnQuickCaptureChanged;
        _localizationService.LanguageChanged += OnLanguageChanged;
        TodoWidgetStore.StoreChanged += OnTodoStoreChanged;
        RegisterActiveInstance(this);
    }

    public EverythingSearchService EverythingProvider => _everythingSearchService;

    public event Action? ResultsChanged;

    private void OnQuickCaptureChanged() => Interlocked.Exchange(ref _deskBoxContentDirty, 1);

    private void OnTodoStoreChanged(string storePath, string widgetId) =>
        Interlocked.Exchange(ref _deskBoxContentDirty, 1);

    private void OnLanguageChanged()
    {
        lock (_settingsCatalogLock)
        {
            _cachedSettingsCatalog = null;
            _cachedSettingsCatalogCulture = null;
        }
        Interlocked.Exchange(ref _deskBoxContentDirty, 1);
    }

    public void InvalidateDeskBoxContentSnapshot()
    {
        if (!_isDisposed)
        {
            Interlocked.Exchange(ref _deskBoxContentDirty, 1);
        }
    }

    public void SetDeskBoxContentSearchEnabled(bool enabled)
    {
        if (_isDisposed)
        {
            return;
        }

        if (enabled)
        {
            Interlocked.Exchange(ref _deskBoxContentDirty, 1);
            return;
        }

        ReleaseColdHiddenCaches();
    }

    public void ReleaseColdHiddenCaches()
    {
        if (_isDisposed)
        {
            return;
        }

        Interlocked.Increment(ref _deskBoxContentCacheGeneration);
        Volatile.Write(ref _deskBoxContentSnapshot, []);
        Volatile.Write(ref _deskBoxContentInitialized, 0);
        Volatile.Write(ref _deskBoxContentDirty, 1);
        Interlocked.Exchange(ref _deskBoxContentLastRefreshMs, long.MinValue);
        lock (_settingsCatalogLock)
        {
            _cachedSettingsCatalog = null;
            _cachedSettingsCatalogCulture = null;
        }
    }

    internal static void ReleaseActiveColdHiddenCaches()
    {
        List<SearchEngineService>? targets = null;
        lock (s_activeInstancesLock)
        {
            for (int i = s_activeInstances.Count - 1; i >= 0; i--)
            {
                if (s_activeInstances[i].TryGetTarget(out SearchEngineService? instance) &&
                    !instance._isDisposed)
                {
                    (targets ??= []).Add(instance);
                }
                else
                {
                    s_activeInstances.RemoveAt(i);
                }
            }
        }

        if (targets is null)
        {
            return;
        }

        foreach (SearchEngineService instance in targets)
        {
            instance.ReleaseColdHiddenCaches();
        }
    }

    private static void RegisterActiveInstance(SearchEngineService instance)
    {
        lock (s_activeInstancesLock)
        {
            for (int i = s_activeInstances.Count - 1; i >= 0; i--)
            {
                if (!s_activeInstances[i].TryGetTarget(out SearchEngineService? existing) ||
                    existing._isDisposed)
                {
                    s_activeInstances.RemoveAt(i);
                }
            }

            s_activeInstances.Add(new WeakReference<SearchEngineService>(instance));
        }
    }

    private static void UnregisterActiveInstance(SearchEngineService instance)
    {
        lock (s_activeInstancesLock)
        {
            for (int i = s_activeInstances.Count - 1; i >= 0; i--)
            {
                if (!s_activeInstances[i].TryGetTarget(out SearchEngineService? existing) ||
                    ReferenceEquals(existing, instance) ||
                    existing._isDisposed)
                {
                    s_activeInstances.RemoveAt(i);
                }
            }
        }
    }

    /// <summary>
    /// Performs a unified search across all enabled layers.
    /// </summary>
    public async Task<SearchResponse> SearchAsync(string query, CancellationToken cancellationToken = default)
        => await SearchPageAsync(
            query,
            0,
            InitialFileResultPageSize,
            cancellationToken).ConfigureAwait(false);

    public async Task<SearchResponse> SearchPageAsync(
        string query,
        int fileResultOffset,
        int fileResultPageSize,
        CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        int normalizedOffset = Math.Max(0, fileResultOffset);
        int normalizedPageSize = Math.Max(1, fileResultPageSize);
        ParsedSearchQuery parsed = SearchQuerySyntaxParser.Parse(query);
        string effectiveQuery = parsed.EffectiveQuery;

        bool queryEverything = SearchQuerySyntaxParser.NeedsFileProvider(parsed.Scope) &&
                               !string.IsNullOrWhiteSpace(effectiveQuery);
        Task<MeasuredProviderResult<SearchFileQueryPage>> fileTask = queryEverything
            ? MeasureProviderAsync(
                "everything",
                () => _everythingSearchService.SearchPageAsync(
                    effectiveQuery,
                    normalizedOffset,
                    normalizedPageSize,
                    cancellationToken))
            : Task.FromResult(new MeasuredProviderResult<SearchFileQueryPage>(
                SearchFileQueryPage.Empty,
                0,
                null,
                "everything"));

        var searchSlice = _settingsService.Settings.Search;
        bool includeDeskBox = normalizedOffset == 0 &&
                              SearchQuerySyntaxParser.NeedsDeskBoxContentProvider(parsed.Scope) &&
                              (searchSlice.SearchIncludeDeskBoxContent ||
                               searchSlice.SearchIncludeClipboard ||
                               parsed.HasScopeFilter);
        Task<MeasuredProviderResult<IReadOnlyList<SearchResultItem>>> deskBoxTask = includeDeskBox
            ? MeasureProviderAsync(
                "deskbox-content",
                () => SearchDeskBoxAndClipboardAsync(parsed, cancellationToken))
            : Task.FromResult(new MeasuredProviderResult<IReadOnlyList<SearchResultItem>>(
                [],
                0,
                null));

        Task<MeasuredProviderResult<IReadOnlyList<SearchResultItem>>> localAppsTask =
            normalizedOffset == 0 && parsed.Scope is SearchInlineScope.None or SearchInlineScope.Apps
                ? MeasureProviderAsync(
                    "local-apps",
                    () => Task.Run(() => SearchLocalApps(parsed, cancellationToken), cancellationToken))
                : Task.FromResult(new MeasuredProviderResult<IReadOnlyList<SearchResultItem>>(
                    [],
                    0,
                    null,
                    "local-apps"));

        IReadOnlyList<SearchResultItem> actions = normalizedOffset == 0
            ? SearchActionsAndSettings(parsed)
            : [];

        await Task.WhenAll(fileTask, deskBoxTask, localAppsTask).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        MeasuredProviderResult<SearchFileQueryPage> measuredFile = await fileTask.ConfigureAwait(false);
        MeasuredProviderResult<IReadOnlyList<SearchResultItem>> measuredDeskBox =
            await deskBoxTask.ConfigureAwait(false);
        MeasuredProviderResult<IReadOnlyList<SearchResultItem>> measuredLocalApps =
            await localAppsTask.ConfigureAwait(false);

        if (measuredFile.Failure is not null)
        {
            LogProviderFailure(measuredFile.Provider, measuredFile.Failure);
        }
        if (measuredDeskBox.Failure is not null)
        {
            LogProviderFailure(measuredDeskBox.Provider, measuredDeskBox.Failure);
        }
        if (measuredLocalApps.Failure is not null)
        {
            LogProviderFailure(measuredLocalApps.Provider, measuredLocalApps.Failure);
        }

        SearchFileQueryPage filePage = measuredFile.Failure is null
            ? measuredFile.Value
            : SearchFileQueryPage.Empty;
        IReadOnlyList<SearchResultItem> deskBoxResults = measuredDeskBox.Failure is null
            ? measuredDeskBox.Value
            : [];
        IReadOnlyList<SearchResultItem> localAppResults = measuredLocalApps.Failure is null
            ? measuredLocalApps.Value
            : [];

        stopwatch.Stop();
        SearchResponse response = BuildSearchResponse(
            query,
            parsed,
            filePage,
            localAppResults,
            deskBoxResults,
            actions,
            stopwatch.Elapsed);
        App.Log(
            $"[Search] Query completed chars={query.Trim().Length} " +
            $"everythingMs={measuredFile.ElapsedMilliseconds} " +
            $"deskboxMs={measuredDeskBox.ElapsedMilliseconds} " +
            $"totalMs={stopwatch.ElapsedMilliseconds} " +
            $"fileOffset={normalizedOffset}->{response.NextFileResultOffset} " +
            $"files={response.MaterializedFileResultCount}/{response.TotalFileResultCount} " +
            $"visible={response.RankedItems.Count}");
        return response;
    }

    private SearchResponse BuildSearchResponse(
        string query,
        ParsedSearchQuery parsed,
        SearchFileQueryPage filePage,
        IReadOnlyList<SearchResultItem> localAppResults,
        IReadOnlyList<SearchResultItem> deskBoxResults,
        IReadOnlyList<SearchResultItem> actions,
        TimeSpan elapsed)
    {
        IEnumerable<SearchResultItem> combined = filePage.Items
            .Concat(localAppResults)
            .Concat(deskBoxResults)
            .Concat(actions);

        if (parsed.HasScopeFilter)
        {
            combined = combined.Where(item => SearchQuerySyntaxParser.MatchesScope(item, parsed.Scope));
        }

        string rankQuery = !string.IsNullOrWhiteSpace(parsed.EffectiveQuery)
            ? parsed.EffectiveQuery
            : query.Trim();

        IReadOnlyList<SearchResultItem> rankedItems = SearchResultRanker.MergeAndRank(
            combined,
            rankQuery,
            int.MaxValue,
            _frecencyBoostProvider);

        bool hasHighPriorityTopHit = rankedItems.Any(item => item.RelevanceScore >= 10000);
        int localMatchCount = rankedItems.Count;

        if (parsed.Scope == SearchInlineScope.None &&
            string.IsNullOrEmpty(parsed.QuickTodoText) &&
            string.IsNullOrEmpty(parsed.QuickNoteText) &&
            !parsed.IsQuickTodoIntent &&
            !parsed.IsQuickNoteIntent &&
            !string.IsNullOrWhiteSpace(parsed.EffectiveQuery) &&
            !SearchQuerySyntaxParser.LooksLikeFileSystemPath(parsed.EffectiveQuery) &&
            !hasHighPriorityTopHit)
        {
            rankedItems = AppendInlineQuickCreateItems(rankedItems, parsed.EffectiveQuery, localMatchCount);
        }

        if (_settingsService.Settings.Search.SearchIncludeWebFallback &&
            parsed.Scope == SearchInlineScope.None &&
            SearchWebAndUrlEvaluator.ShouldOfferFallbackWebSearch(parsed.EffectiveQuery) &&
            localMatchCount <= 6 &&
            !hasHighPriorityTopHit)
        {
            SearchResultItem fallbackWebItem = CreateFallbackWebSearchItem(parsed.EffectiveQuery);
            var expanded = new List<SearchResultItem>(rankedItems.Count + 1);
            expanded.AddRange(rankedItems);
            expanded.Add(fallbackWebItem);
            rankedItems = expanded;
        }

        IReadOnlyList<SearchResultGroup> groups = BuildGroups(rankedItems);
        int materializedFileResults = rankedItems.Count(item =>
            item.Kind is SearchResultKind.File or SearchResultKind.Folder);
        int nonFileResults = rankedItems.Count - materializedFileResults;
        int effectiveTotalFiles = parsed.HasScopeFilter &&
                                  parsed.Scope is not (SearchInlineScope.None or SearchInlineScope.Files)
            ? materializedFileResults
            : Math.Max(materializedFileResults, filePage.TotalMatchedCount);

        return new SearchResponse
        {
            Query = query,
            RankedItems = rankedItems,
            Groups = groups,
            TotalResultCount = (int)Math.Min(
                int.MaxValue,
                (long)effectiveTotalFiles + nonFileResults),
            MaterializedFileResultCount = materializedFileResults,
            TotalFileResultCount = effectiveTotalFiles,
            NextFileResultOffset = filePage.NextOffset,
            HasMoreResults = SearchQuerySyntaxParser.NeedsFileProvider(parsed.Scope) &&
                             filePage.NextOffset < filePage.TotalMatchedCount,
            Elapsed = elapsed,
            IsComplete = true,
            FileProviderState = _everythingSearchService.CurrentSnapshot.State
        };
    }

    private static async Task<MeasuredProviderResult<T>> MeasureProviderAsync<T>(
        string provider,
        Func<Task<T>> operation)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            T value = await operation().ConfigureAwait(false);
            stopwatch.Stop();
            return new MeasuredProviderResult<T>(
                value,
                stopwatch.ElapsedMilliseconds,
                null,
                provider);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            return new MeasuredProviderResult<T>(
                default!,
                stopwatch.ElapsedMilliseconds,
                ex,
                provider);
        }
    }

    private static void LogProviderFailure(string provider, Exception ex)
    {
        App.Log($"[Search] Provider '{provider}' failed; returning partial results: {ex}");
    }

    private readonly record struct MeasuredProviderResult<T>(
        T Value,
        long ElapsedMilliseconds,
        Exception? Failure,
        string Provider = "deskbox-content");

    /// <summary>
    /// Gets recommendations for the empty-state view.
    /// </summary>
    public async Task<IReadOnlyList<SearchRecommendationItem>> GetRecommendationsAsync(
        CancellationToken cancellationToken = default)
    {
        return await Task.Run(
            () => BuildApplicationRecommendations(cancellationToken),
            cancellationToken);
    }

    private const long StartMenuShortcutCacheTtlMilliseconds = 180_000;
    private static readonly object s_startMenuShortcutCacheLock = new();
    private static long s_startMenuShortcutCacheTickMs = long.MinValue;
    private static (string FullPath, Helpers.AppRecommendationIdentity.Keys Keys)[] s_startMenuShortcutCache = [];

    private IReadOnlyList<SearchResultItem> SearchLocalApps(
        ParsedSearchQuery parsed,
        CancellationToken cancellationToken)
    {
        bool listAllInAppScope = parsed.Scope == SearchInlineScope.Apps &&
                                 string.IsNullOrWhiteSpace(parsed.EffectiveQuery);
        if (!listAllInAppScope && string.IsNullOrWhiteSpace(parsed.EffectiveQuery))
        {
            return [];
        }

        IReadOnlyList<SearchRecommendationItem> candidates = BuildApplicationRecommendations(cancellationToken);
        var results = new List<SearchResultItem>();
        int rankOrdinal = 0;

        foreach (SearchRecommendationItem candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(candidate.DetailPath))
            {
                continue;
            }

            string appName = Path.GetFileNameWithoutExtension(candidate.Title);
            double relevance;
            if (listAllInAppScope)
            {
                relevance = Math.Max(30, 88 - rankOrdinal);
                rankOrdinal++;
            }
            else
            {
                int matchScore = SettingsSearchMatcher.GetScore(
                    parsed.EffectiveQuery,
                    appName,
                    candidate.Subtitle ?? string.Empty,
                    candidate.Title);
                if (matchScore == SettingsSearchMatcher.NoMatch)
                {
                    continue;
                }

                relevance = SettingsSearchMatcher.ConvertMatchScoreToSearchRelevance(matchScore) + 4;
            }

            results.Add(new SearchResultItem
            {
                Kind = SearchResultKind.File,
                Title = candidate.Title,
                Subtitle = candidate.Subtitle ?? Path.GetDirectoryName(candidate.DetailPath),
                DetailPath = candidate.DetailPath,
                RelevanceScore = relevance
            });
        }

        return results;
    }

    private IReadOnlyList<SearchRecommendationItem> BuildApplicationRecommendations(
        CancellationToken cancellationToken)
    {
        var recommendations = new List<SearchRecommendationItem>();
        var deduplicator = new Helpers.AppRecommendationDeduplicator();

        void AddShortcut(
            string path,
            string subtitle,
            Helpers.AppRecommendationIdentity.Keys? precomputedKeys = null,
            bool skipExistsCheck = false)
        {
            if (cancellationToken.IsCancellationRequested ||
                !path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            if (!skipExistsCheck && !File.Exists(path))
            {
                return;
            }

            string fullPath;
            if (skipExistsCheck)
            {
                fullPath = path;
            }
            else
            {
                try
                {
                    fullPath = Path.GetFullPath(path);
                }
                catch
                {
                    return;
                }
            }

            string fileName = Path.GetFileName(fullPath);
            if (!deduplicator.TryAcceptWithKeys(
                    fullPath,
                    fileName,
                    precomputedKeys,
                    out Helpers.AppRecommendationIdentity.Keys resolvedKeys,
                    out _))
            {
                return;
            }

            recommendations.Add(new SearchRecommendationItem
            {
                Kind = SearchResultKind.File,
                Title = fileName,
                Subtitle = subtitle,
                DetailPath = fullPath,
                ResolvedIdentityKeys = resolvedKeys
            });
        }

        // The user's widgets are an explicit curation signal, so every shortcut shown
        // by an enabled file widget comes before generic Start menu applications.
        foreach (var widget in _settingsService.Settings.WidgetLayout.Widgets
                     .Where(widget => widget.WidgetKind == WidgetKind.File && !widget.IsDisabled))
        {
            cancellationToken.ThrowIfCancellationRequested();

            foreach (var item in widget.Items.OrderBy(item => item.SortOrder))
            {
                AddShortcut(item.Path, widget.Name);
            }

            if (!string.IsNullOrWhiteSpace(widget.MappedFolderPath))
            {
                foreach (string shortcut in EnumerateShortcutFilesSafely(
                             widget.MappedFolderPath, recursive: false, cancellationToken))
                {
                    AddShortcut(shortcut, widget.Name);
                }
            }
        }

        string startMenuLabel = _localizationService.T("Search.Recommend.StartMenu");
        const int MaxStartMenuApps = 40;
        int startMenuCount = 0;
        foreach (var entry in GetOrScanStartMenuShortcuts(cancellationToken))
        {
            int before = recommendations.Count;
            AddShortcut(
                entry.FullPath,
                startMenuLabel,
                precomputedKeys: entry.Keys,
                skipExistsCheck: true);
            if (recommendations.Count > before && ++startMenuCount >= MaxStartMenuApps)
            {
                return recommendations;
            }
        }

        return recommendations;
    }

    private static (string FullPath, Helpers.AppRecommendationIdentity.Keys Keys)[] GetOrScanStartMenuShortcuts(
        CancellationToken cancellationToken)
    {
        long nowTick = Environment.TickCount64;
        lock (s_startMenuShortcutCacheLock)
        {
            if (s_startMenuShortcutCache.Length > 0 &&
                nowTick - s_startMenuShortcutCacheTickMs < StartMenuShortcutCacheTtlMilliseconds)
            {
                return s_startMenuShortcutCache;
            }
        }

        string[] startMenuRoots =
        [
            Environment.GetFolderPath(Environment.SpecialFolder.Programs),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms),
            Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu)
        ];

        var visitedDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var deduplicator = new Helpers.AppRecommendationDeduplicator();
        var scanned = new List<(string FullPath, Helpers.AppRecommendationIdentity.Keys Keys)>(64);
        const int MaxCachedStartMenuCandidates = 72;

        foreach (string root in startMenuRoots
                     .Where(path => !string.IsNullOrWhiteSpace(path))
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            foreach (string shortcut in EnumerateShortcutFilesSafely(
                         root,
                         recursive: true,
                         cancellationToken,
                         visitedDirectories)
                     .OrderBy(Path.GetFileName, StringComparer.CurrentCultureIgnoreCase))
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    return scanned.ToArray();
                }

                string fullPath;
                try
                {
                    fullPath = Path.GetFullPath(shortcut);
                }
                catch
                {
                    continue;
                }

                string fileName = Path.GetFileName(fullPath);
                if (!deduplicator.TryAcceptWithKeys(
                        fullPath,
                        fileName,
                        precomputedKeys: null,
                        out Helpers.AppRecommendationIdentity.Keys keys,
                        out _))
                {
                    continue;
                }

                scanned.Add((fullPath, keys));
                if (scanned.Count >= MaxCachedStartMenuCandidates)
                {
                    break;
                }
            }

            if (scanned.Count >= MaxCachedStartMenuCandidates)
            {
                break;
            }
        }

        var snapshot = scanned.ToArray();
        if (!cancellationToken.IsCancellationRequested && snapshot.Length > 0)
        {
            lock (s_startMenuShortcutCacheLock)
            {
                s_startMenuShortcutCache = snapshot;
                s_startMenuShortcutCacheTickMs = Environment.TickCount64;
            }
        }

        return snapshot;
    }

    private static IEnumerable<string> EnumerateShortcutFilesSafely(
        string root,
        bool recursive,
        CancellationToken cancellationToken,
        HashSet<string>? sharedVisitedDirectories = null)
    {
        if (!FileService.TryResolveExistingPathForTraversal(
                root,
                out string resolvedRoot))
        {
            yield break;
        }

        var pending = new Stack<string>();
        pending.Push(resolvedRoot);
        var visited = sharedVisitedDirectories ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        while (pending.Count > 0 && !cancellationToken.IsCancellationRequested)
        {
            string current = pending.Pop();
            if (!FileService.TryResolveExistingPathForTraversal(
                    current,
                    out string resolvedCurrent) ||
                !visited.Add(resolvedCurrent))
            {
                continue;
            }

            current = resolvedCurrent;
            string[] files;
            try
            {
                files = Directory.GetFiles(current, "*.lnk", SearchOption.TopDirectoryOnly);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                continue;
            }

            foreach (string file in files)
            {
                yield return file;
            }

            if (!recursive)
            {
                continue;
            }

            try
            {
                foreach (string directory in Directory.GetDirectories(current))
                {
                    pending.Push(directory);
                }
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                // Keep results already found in accessible Start menu folders.
            }
        }
    }

    private async Task<IReadOnlyList<SearchResultItem>> SearchDeskBoxAndClipboardAsync(
        ParsedSearchQuery parsed,
        CancellationToken cancellationToken)
    {
        var combined = new List<SearchResultItem>();

        var searchSlice = _settingsService.Settings.Search;
        if ((searchSlice.SearchIncludeDeskBoxContent || parsed.HasScopeFilter) &&
            parsed.Scope is SearchInlineScope.None or SearchInlineScope.Todos or SearchInlineScope.Notes)
        {
            bool initialized = Volatile.Read(ref _deskBoxContentInitialized) != 0;
            if (!initialized || Volatile.Read(ref _deskBoxContentDirty) != 0)
            {
                await EnsureDeskBoxContentSnapshotAsync(
                        forceRefresh: true,
                        cancellationToken,
                        ignoreMasterToggle: parsed.HasScopeFilter)
                    .ConfigureAwait(false);
            }

            DeskBoxSearchDocument[] snapshot = Volatile.Read(ref _deskBoxContentSnapshot);
            IReadOnlyList<SearchResultItem> docHits = await Task.Run(
                    () => SearchDeskBoxContentSnapshot(snapshot, parsed, cancellationToken),
                    cancellationToken)
                .ConfigureAwait(false);
            combined.AddRange(docHits);
        }

        if (_clipboardItemsProvider is not null &&
            (parsed.Scope == SearchInlineScope.Clipboard ||
             (parsed.Scope == SearchInlineScope.None && searchSlice.SearchIncludeClipboard)))
        {
            IReadOnlyList<ClipboardHistoryItem> clipItems =
                await _clipboardItemsProvider(cancellationToken).ConfigureAwait(false);
            if (clipItems.Count > 0)
            {
                combined.AddRange(SearchClipboardItems(clipItems, parsed, cancellationToken));
            }
        }

        return combined;
    }

    private IReadOnlyList<SearchResultItem> SearchClipboardItems(
        IReadOnlyList<ClipboardHistoryItem> clipItems,
        ParsedSearchQuery parsed,
        CancellationToken cancellationToken)
    {
        string effectiveQuery = parsed.EffectiveQuery;
        bool browseMode = parsed.Scope == SearchInlineScope.Clipboard &&
                          string.IsNullOrWhiteSpace(effectiveQuery);
        if (!browseMode && string.IsNullOrWhiteSpace(effectiveQuery))
        {
            return [];
        }

        string clipboardLabel = _localizationService.T("Search.Type.Clipboard");
        string imageTypeLabel = _localizationService.T("Search.Type.Image");
        var results = new List<SearchResultItem>();
        const int MaxClipboardHits = 24;

        foreach (ClipboardHistoryItem clip in clipItems)
        {
            cancellationToken.ThrowIfCancellationRequested();

            (string displayTitle, string glyph) = FormatClipboardDisplayTitleAndGlyph(
                clip,
                imageTypeLabel);
            if (string.IsNullOrWhiteSpace(displayTitle))
            {
                continue;
            }

            string cleanAppName = ClipboardClassifier.NormalizeFriendlyAppDisplayName(clip.SourceAppName);
            string subtitle = !string.IsNullOrWhiteSpace(cleanAppName)
                ? $"{clipboardLabel} · {cleanAppName}"
                : clipboardLabel;
            if (!string.IsNullOrWhiteSpace(clip.Note))
            {
                subtitle = $"{subtitle} · {clip.Note}";
            }

            double score;
            if (browseMode)
            {
                score = (clip.IsPinned ? 88 : 72) - Math.Min(30, results.Count);
            }
            else
            {
                string searchableBody = clip.IsSensitive
                    ? (clip.Note ?? string.Empty)
                    : string.Concat(clip.TextContent ?? clip.Summary ?? string.Empty, " ", clip.UrlTitle ?? string.Empty, " ", clip.Note ?? string.Empty);

                int matchScore = SettingsSearchMatcher.GetScore(
                    effectiveQuery,
                    displayTitle,
                    subtitle,
                    clip.IsSensitive ? (clip.Note ?? string.Empty) : string.Concat(clip.Summary ?? string.Empty, " ", clip.UrlTitle ?? string.Empty));

                if (matchScore != SettingsSearchMatcher.NoMatch)
                {
                    score = SettingsSearchMatcher.ConvertMatchScoreToSearchRelevance(matchScore);
                }
                else if (!clip.IsSensitive &&
                         searchableBody.Contains(effectiveQuery, StringComparison.OrdinalIgnoreCase))
                {
                    score = 44;
                }
                else
                {
                    continue;
                }

                if (clip.IsPinned)
                {
                    score += 6;
                }
            }

            (string? clipPreviewText, string? clipPreviewImage, int? clipChars, int? clipLines, string? clipDetailPath) =
                BuildClipboardPreviewMetadata(clip);

            results.Add(new SearchResultItem
            {
                Kind = SearchResultKind.Clipboard,
                Title = displayTitle,
                Subtitle = subtitle,
                DetailPath = clipDetailPath,
                ClipboardItemId = clip.Id,
                ActionId = $"clipboard-copy:{clip.Id}",
                Glyph = glyph,
                ModifiedAt = new DateTimeOffset(clip.LastUsedAtUtc, TimeSpan.Zero),
                RelevanceScore = Math.Max(1, score),
                PreviewText = clipPreviewText,
                PreviewImagePath = clipPreviewImage,
                PreviewSourceApp = cleanAppName,
                PreviewCharCount = clipChars,
                PreviewLineCount = clipLines
            });

            if (results.Count >= MaxClipboardHits)
            {
                break;
            }
        }

        return results;
    }

    private static (string Title, string Glyph) FormatClipboardDisplayTitleAndGlyph(
        ClipboardHistoryItem clip,
        string imageTypeLabel)
    {
        switch (clip.Kind)
        {
            case ClipboardItemKind.Image:
            {
                string dims = clip.ImageWidth > 0 && clip.ImageHeight > 0
                    ? $" ({clip.ImageWidth}×{clip.ImageHeight})"
                    : string.Empty;
                return ($"{imageTypeLabel}{dims}", "\uE8B9");
            }

            case ClipboardItemKind.Files:
            {
                if (clip.FilePaths.Count == 0)
                {
                    return (TruncateText(clip.Summary, 80), "\uE8E5");
                }

                string firstName = Path.GetFileName(clip.FilePaths[0].TrimEnd('\\', '/'));
                if (string.IsNullOrWhiteSpace(firstName))
                {
                    firstName = clip.FilePaths[0];
                }

                string suffix = clip.FilePaths.Count > 1 ? $" (+{clip.FilePaths.Count - 1})" : string.Empty;
                return ($"{firstName}{suffix}", "\uE8E5");
            }

            default:
            {
                string raw = !string.IsNullOrWhiteSpace(clip.Summary)
                    ? clip.Summary
                    : (clip.TextContent ?? string.Empty);
                bool mustMask = clip.IsSensitive || ClipboardClassifier.IsHighConfidenceSecret(raw);
                string safeText = mustMask
                    ? ClipboardClassifier.MaskSecretForDisplay(raw)
                    : raw;
                string glyph = clip.SubKind switch
                {
                    ClipboardTextSubKind.Url => "\uE71B",
                    ClipboardTextSubKind.Color => "\uE790",
                    ClipboardTextSubKind.Path => "\uE8B7",
                    ClipboardTextSubKind.Code => "\uE943",
                    _ => "\uE77F"
                };
                string preferredTitle = !mustMask && clip.SubKind == ClipboardTextSubKind.Url && !string.IsNullOrWhiteSpace(clip.UrlTitle)
                    ? clip.UrlTitle
                    : safeText;
                return (TruncateText(preferredTitle, 80), glyph);
            }
        }
    }

    private (string? PreviewText, string? PreviewImagePath, int? CharCount, int? LineCount, string? DetailPath)
        BuildClipboardPreviewMetadata(ClipboardHistoryItem clip)
    {
        switch (clip.Kind)
        {
            case ClipboardItemKind.Image:
            {
                string? dims = clip.ImageWidth > 0 && clip.ImageHeight > 0
                    ? $"{clip.ImageWidth} × {clip.ImageHeight} px"
                    : null;
                string? thumbPath = _clipboardPreviewImageResolver?.Invoke(clip);
                return (dims, thumbPath, null, null, thumbPath);
            }

            case ClipboardItemKind.Files:
            {
                string? joined = clip.FilePaths.Count > 0
                    ? string.Join(Environment.NewLine, clip.FilePaths.Take(10))
                    : clip.Summary;
                return (joined, null, null, clip.FilePaths.Count > 0 ? clip.FilePaths.Count : null, clip.FilePaths.FirstOrDefault());
            }

            default:
            {
                string body = !string.IsNullOrWhiteSpace(clip.TextContent)
                    ? clip.TextContent
                    : (clip.Summary ?? string.Empty);
                bool mustMask = clip.IsSensitive || ClipboardClassifier.IsHighConfidenceSecret(body);
                string safePreview = mustMask
                    ? ClipboardClassifier.MaskSecretForDisplay(body)
                    : (body.Length > 1200 ? string.Concat(body.AsSpan(0, 1200), "…") : body);
                int? chars = clip.CharCount > 0 ? clip.CharCount : (body.Length > 0 ? body.Length : null);
                int? lines = clip.LineCount > 0 ? clip.LineCount : (body.Length > 0 ? CountTextLines(body) : null);
                string? target = !mustMask && (clip.SubKind is ClipboardTextSubKind.Url or ClipboardTextSubKind.Path)
                    ? clip.Summary
                    : null;
                return (safePreview, null, chars, lines, target);
            }
        }
    }

    private static int CountTextLines(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return 0;
        }

        int count = 1;
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] == '\n')
            {
                count++;
            }
        }

        return count;
    }

    private IReadOnlyList<SearchResultItem> AppendInlineQuickCreateItems(
        IReadOnlyList<SearchResultItem> rankedItems,
        string effectiveQuery,
        int localMatchCount)
    {
        var searchSettings = _settingsService.Settings.Search;
        string displayMode = (searchSettings.SearchQuickCreateDisplayMode ?? "always").Trim().ToLowerInvariant();
        if (displayMode == "prefix" || (displayMode == "fallback" && localMatchCount > 0))
        {
            return rankedItems;
        }

        bool canTodo = CanOfferTodoQuickCreate(isExplicitPrefix: false);
        bool canNote = CanOfferNoteQuickCreate(isExplicitPrefix: false);
        if (!canTodo && !canNote)
        {
            return rankedItems;
        }

        string trimmedText = effectiveQuery.Trim();
        if (trimmedText.Length == 0)
        {
            return rankedItems;
        }

        bool noteFirst = string.Equals(
            searchSettings.SearchQuickCreateDefaultTarget,
            "note",
            StringComparison.OrdinalIgnoreCase);

        var expanded = new List<SearchResultItem>(rankedItems.Count + 2);
        expanded.AddRange(rankedItems);

        if (noteFirst)
        {
            if (canNote)
            {
                expanded.Add(CreateQuickNoteActionItem(trimmedText, relevanceScore: 0.45, isFallback: false));
            }
            if (canTodo)
            {
                expanded.Add(CreateQuickTodoActionItem(trimmedText, relevanceScore: 0.40, isFallback: false));
            }
        }
        else
        {
            if (canTodo)
            {
                expanded.Add(CreateQuickTodoActionItem(trimmedText, relevanceScore: 0.45, isFallback: false));
            }
            if (canNote)
            {
                expanded.Add(CreateQuickNoteActionItem(trimmedText, relevanceScore: 0.40, isFallback: false));
            }
        }

        return expanded;
    }

    private bool CanOfferTodoQuickCreate(bool isExplicitPrefix)
    {
        AppSettings settings = _settingsService.Settings;
        if (!settings.Search.SearchQuickCreateTodoEnabled)
        {
            return false;
        }

        if (FeatureWidgetSettings.IsEnabled(settings, WidgetKind.Todo))
        {
            return true;
        }

        return isExplicitPrefix &&
               !settings.FeatureWidgetEnabledStates.ContainsKey(nameof(WidgetKind.Todo));
    }

    private bool CanOfferNoteQuickCreate(bool isExplicitPrefix)
    {
        AppSettings settings = _settingsService.Settings;
        if (!settings.Search.SearchQuickCreateNoteEnabled)
        {
            return false;
        }

        if (FeatureWidgetSettings.IsEnabled(settings, WidgetKind.QuickCapture))
        {
            return true;
        }

        return isExplicitPrefix &&
               !settings.FeatureWidgetEnabledStates.ContainsKey(nameof(WidgetKind.QuickCapture));
    }

    private SearchResultItem CreateQuickTodoActionItem(
        string todoText,
        double relevanceScore,
        bool isFallback)
    {
        string displayTodo = TruncateText(todoText, 80);
        string template = _localizationService.T("Search.QuickTodo.Format");
        string title = string.IsNullOrWhiteSpace(template) ||
                       string.Equals(template, "Search.QuickTodo.Format", StringComparison.Ordinal)
            ? $"Add to-do: {displayTodo}"
            : string.Format(template, displayTodo);

        string typeLabel = _localizationService.T("Search.Type.Todo");
        string hint = _localizationService.T("Search.QuickCreate.TodoSubtitleHint");
        string subtitle = !string.IsNullOrWhiteSpace(hint) &&
                          !string.Equals(hint, "Search.QuickCreate.TodoSubtitleHint", StringComparison.Ordinal)
            ? $"{typeLabel} · {hint}"
            : typeLabel;

        return new SearchResultItem
        {
            Kind = SearchResultKind.Action,
            Title = title,
            Subtitle = subtitle,
            ActionId = $"quick-todo:{todoText}",
            Glyph = "\uE9D5",
            RelevanceScore = relevanceScore,
            IsFallback = isFallback,
            PreviewText = todoText,
            PreviewCharCount = todoText.Length,
            PreviewLineCount = CountTextLines(todoText)
        };
    }

    private SearchResultItem CreateQuickNoteActionItem(
        string noteText,
        double relevanceScore,
        bool isFallback)
    {
        string displayNote = TruncateText(noteText, 80);
        string template = _localizationService.T("QuickCapture.SearchQuickSaveFormat");
        string title = string.IsNullOrWhiteSpace(template) ||
                       string.Equals(template, "QuickCapture.SearchQuickSaveFormat", StringComparison.Ordinal)
            ? $"Save to Quick Capture: {displayNote}"
            : string.Format(template, displayNote);

        string typeLabel = _localizationService.T("QuickCapture.Name");
        string hint = _localizationService.T("Search.QuickCreate.NoteSubtitleHint");
        string subtitle = !string.IsNullOrWhiteSpace(hint) &&
                          !string.Equals(hint, "Search.QuickCreate.NoteSubtitleHint", StringComparison.Ordinal)
            ? $"{typeLabel} · {hint}"
            : typeLabel;

        return new SearchResultItem
        {
            Kind = SearchResultKind.Action,
            Title = title,
            Subtitle = subtitle,
            ActionId = $"quick-note:{noteText}",
            Glyph = "\uE70F",
            RelevanceScore = relevanceScore,
            IsFallback = isFallback,
            PreviewText = noteText,
            PreviewCharCount = noteText.Length,
            PreviewLineCount = CountTextLines(noteText)
        };
    }

    private SearchResultItem CreateFallbackWebSearchItem(string query)
    {
        string trimmed = query.Trim();
        string displayQuery = TruncateText(trimmed, 60);
        var searchSettings = _settingsService.Settings.Search;
        string fallbackUri = SearchWebAndUrlEvaluator.BuildFallbackWebSearchUri(
            trimmed,
            searchSettings.SearchDefaultWebEngine,
            searchSettings.SearchCustomWebUrl);
        string engineName = SearchWebAndUrlEvaluator.ResolveEngineDisplayName(
            searchSettings.SearchDefaultWebEngine,
            searchSettings.SearchCustomWebUrl);
        string format = _localizationService.T("Search.Web.FallbackFormat");
        string title = string.IsNullOrWhiteSpace(format) ||
                       string.Equals(format, "Search.Web.FallbackFormat", StringComparison.Ordinal)
            ? $"Search the web for \"{displayQuery}\""
            : string.Format(format, displayQuery);
        string engineSubtitleFormat = _localizationService.T("Search.Web.EngineSubtitleFormat");
        string subtitle = !string.IsNullOrWhiteSpace(engineSubtitleFormat) &&
                          !string.Equals(engineSubtitleFormat, "Search.Web.EngineSubtitleFormat", StringComparison.Ordinal)
            ? string.Format(engineSubtitleFormat, engineName)
            : _localizationService.T("Search.Web.FallbackSubtitle");
        if (string.IsNullOrWhiteSpace(subtitle) ||
            string.Equals(subtitle, "Search.Web.FallbackSubtitle", StringComparison.Ordinal))
        {
            subtitle = $"Search the web with {engineName}";
        }

        return new SearchResultItem
        {
            Kind = SearchResultKind.Action,
            Title = title,
            Subtitle = subtitle,
            DetailPath = fallbackUri,
            ActionId = $"{SearchWebAndUrlEvaluator.WebSearchActionPrefix}{fallbackUri}",
            Glyph = "\uE774",
            RelevanceScore = 0.25,
            IsFallback = true
        };
    }

    private Task EnsureDeskBoxContentSnapshotAsync(
        bool forceRefresh,
        CancellationToken cancellationToken,
        bool ignoreMasterToggle = false)
    {
        if (_isDisposed ||
            (!ignoreMasterToggle && !_settingsService.Settings.Search.SearchIncludeDeskBoxContent))
        {
            return Task.CompletedTask;
        }

        Task refreshTask;
        lock (_deskBoxContentRefreshLock)
        {
            if (_deskBoxContentRefreshTask is { IsCompleted: false } activeRefresh)
            {
                refreshTask = activeRefresh;
            }
            else
            {
                long elapsed = Environment.TickCount64 -
                               Interlocked.Read(ref _deskBoxContentLastRefreshMs);
                if (!forceRefresh &&
                    Volatile.Read(ref _deskBoxContentInitialized) != 0 &&
                    elapsed < DeskBoxContentRefreshIntervalMilliseconds)
                {
                    return Task.CompletedTask;
                }

                refreshTask = Task.Run(
                    () => RefreshDeskBoxContentSnapshotAsync(
                        _deskBoxContentLifetimeCts.Token,
                        ignoreMasterToggle),
                    _deskBoxContentLifetimeCts.Token);
                _deskBoxContentRefreshTask = refreshTask;
            }
        }

        return cancellationToken.CanBeCanceled
            ? refreshTask.WaitAsync(cancellationToken)
            : refreshTask;
    }

    private async Task RefreshDeskBoxContentSnapshotAsync(
        CancellationToken cancellationToken,
        bool ignoreMasterToggle = false)
    {
        int generation = Volatile.Read(ref _deskBoxContentCacheGeneration);
        try
        {
            DeskBoxSearchDocument[] next = await BuildDeskBoxContentSnapshotAsync(
                    cancellationToken)
                .ConfigureAwait(false);
            if (_isDisposed ||
                cancellationToken.IsCancellationRequested ||
                (!ignoreMasterToggle && !_settingsService.Settings.Search.SearchIncludeDeskBoxContent) ||
                Volatile.Read(ref _deskBoxContentCacheGeneration) != generation)
            {
                return;
            }

            DeskBoxSearchDocument[] previous = Volatile.Read(ref _deskBoxContentSnapshot);
            bool wasInitialized = Volatile.Read(ref _deskBoxContentInitialized) != 0;
            bool changed = !previous.SequenceEqual(next);
            Volatile.Write(ref _deskBoxContentSnapshot, next);
            Volatile.Write(ref _deskBoxContentInitialized, 1);
            Volatile.Write(ref _deskBoxContentDirty, 0);
            Interlocked.Exchange(
                ref _deskBoxContentLastRefreshMs,
                Environment.TickCount64);

            if (wasInitialized && changed)
            {
                ResultsChanged?.Invoke();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Normal feature shutdown.
        }
        catch (Exception ex)
        {
            Interlocked.Exchange(
                ref _deskBoxContentLastRefreshMs,
                Environment.TickCount64);
            App.Log($"[Search] DeskBox content snapshot refresh failed: {ex.Message}");
        }
        finally
        {
            lock (_deskBoxContentRefreshLock)
            {
                _deskBoxContentRefreshTask = null;
            }
        }
    }

    private async Task<DeskBoxSearchDocument[]> BuildDeskBoxContentSnapshotAsync(
        CancellationToken cancellationToken)
    {
        var documents = new List<DeskBoxSearchDocument>();
        IReadOnlyList<QuickCaptureSearchProjection> quickCaptureProjections = await _quickCaptureService
            .GetSearchIndexProjectionsAsync(MaxIndexedBodyCharacters, cancellationToken)
            .ConfigureAwait(false);
        string noteTypeLabel = _localizationService.T("Search.Type.Note");
        string todoTypeLabel = _localizationService.T("Search.Type.Todo");
        foreach (QuickCaptureSearchProjection item in quickCaptureProjections)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string displayTitle = !string.IsNullOrWhiteSpace(item.Title)
                ? item.Title
                : TruncateText(item.Text, 60);
            string bodyPreview = TruncateText(item.Text, 48);
            string noteSubtitle = !string.IsNullOrWhiteSpace(item.Title) &&
                                  !string.IsNullOrWhiteSpace(bodyPreview) &&
                                  !string.Equals(bodyPreview, displayTitle, StringComparison.Ordinal)
                ? $"{noteTypeLabel} · {bodyPreview}"
                : !string.IsNullOrWhiteSpace(item.Url)
                    ? $"{noteTypeLabel} · {TruncateText(item.Url, 48)}"
                    : noteTypeLabel;
            documents.Add(new DeskBoxSearchDocument(
                SearchResultKind.QuickCapture,
                displayTitle,
                TruncateIndexedBody(item.Text),
                item.Url,
                noteSubtitle,
                TodoWidgetId: null,
                TodoItemId: null,
                TodoIsCompleted: false,
                QuickCaptureItemId: item.Id,
                IsPinned: item.IsPinned,
                item.UpdatedAt));
        }

        List<WidgetConfig> todoWidgets = _settingsService.Settings.WidgetLayout.Widgets
            .Where(widget => widget.WidgetKind == WidgetKind.Todo && !widget.IsDisabled)
            .ToList();
        Task<(WidgetConfig Widget, TodoWidgetData? Data)>[] todoLoads = todoWidgets
            .Select(async widget =>
            {
                try
                {
                    TodoWidgetData data = await new TodoWidgetStore(widget.Id)
                        .LoadReadOnlySnapshotAsync()
                        .WaitAsync(cancellationToken)
                        .ConfigureAwait(false);
                    return (Widget: widget, Data: (TodoWidgetData?)data);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    App.Log(
                        $"[Search] Todo snapshot skipped widget={widget.Id}: {ex.Message}");
                    return (Widget: widget, Data: (TodoWidgetData?)null);
                }
            })
            .ToArray();

        foreach ((WidgetConfig widget, TodoWidgetData? data) in
                 await Task.WhenAll(todoLoads).ConfigureAwait(false))
        {
            if (data is null)
            {
                continue;
            }

            DateTime today = DateTime.Today;
            foreach (TodoItem item in data.Items)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (item.IsDeleted || TodoRecurrenceService.IsDeferredOccurrence(item, today))
                {
                    continue;
                }

                string noteSnippet = TruncateText(item.Notes ?? string.Empty, 44);
                string todoSubtitle = item.DueDate.HasValue
                    ? $"{todoTypeLabel} · {_localizationService.T("Search.Todo.Due")}: {item.DueDate.Value:yyyy-MM-dd}"
                    : !string.IsNullOrWhiteSpace(noteSnippet)
                        ? $"{todoTypeLabel} · {noteSnippet}"
                        : !string.IsNullOrWhiteSpace(widget.Name) &&
                          !string.Equals(widget.Name, todoTypeLabel, StringComparison.OrdinalIgnoreCase)
                            ? $"{todoTypeLabel} · {widget.Name}"
                            : todoTypeLabel;
                documents.Add(new DeskBoxSearchDocument(
                    SearchResultKind.Todo,
                    item.Text,
                    TruncateIndexedBody(item.Notes),
                    AuxiliaryText: null,
                    todoSubtitle,
                    TodoWidgetId: widget.Id,
                    TodoItemId: item.Id,
                    TodoIsCompleted: item.IsCompleted,
                    QuickCaptureItemId: null,
                    IsPinned: false,
                    item.UpdatedAt));
            }
        }

        return documents.ToArray();
    }

    private static IReadOnlyList<SearchResultItem> SearchDeskBoxContentSnapshot(
        IReadOnlyList<DeskBoxSearchDocument> snapshot,
        ParsedSearchQuery parsed,
        CancellationToken cancellationToken)
    {
        string query = parsed.EffectiveQuery;
        bool browseTodos = parsed.Scope == SearchInlineScope.Todos && string.IsNullOrWhiteSpace(query);
        bool browseNotes = parsed.Scope == SearchInlineScope.Notes && string.IsNullOrWhiteSpace(query);
        if (!browseTodos && !browseNotes && string.IsNullOrWhiteSpace(query))
        {
            return [];
        }

        var results = new List<SearchResultItem>();
        foreach (DeskBoxSearchDocument document in snapshot)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (parsed.Scope == SearchInlineScope.Todos && document.Kind != SearchResultKind.Todo)
            {
                continue;
            }
            if (parsed.Scope == SearchInlineScope.Notes && document.Kind != SearchResultKind.QuickCapture)
            {
                continue;
            }

            double score;
            if (browseTodos || browseNotes)
            {
                score = 72;
            }
            else
            {
                bool titleMatches = document.Title.Contains(query, StringComparison.OrdinalIgnoreCase);
                bool bodyMatches = document.BodyText?.Contains(query, StringComparison.OrdinalIgnoreCase) == true;
                bool auxiliaryMatches = document.AuxiliaryText?.Contains(query, StringComparison.OrdinalIgnoreCase) == true;

                int pinyinOrSynonymScore = SettingsSearchMatcher.GetScore(
                    query,
                    document.Title,
                    document.Subtitle,
                    document.BodyText ?? document.AuxiliaryText ?? string.Empty);

                if (!titleMatches && !bodyMatches && !auxiliaryMatches &&
                    pinyinOrSynonymScore == SettingsSearchMatcher.NoMatch)
                {
                    continue;
                }

                score = 1;
                if (titleMatches)
                {
                    score = Math.Max(score, ComputeTextRelevance(document.Title, query));
                }
                if (bodyMatches)
                {
                    score = Math.Max(score, ComputeTextRelevance(document.BodyText!, query) - 5);
                }
                if (auxiliaryMatches)
                {
                    score = Math.Max(score, ComputeTextRelevance(document.AuxiliaryText!, query) - 10);
                }
                if (pinyinOrSynonymScore != SettingsSearchMatcher.NoMatch)
                {
                    score = Math.Max(
                        score,
                        SettingsSearchMatcher.ConvertMatchScoreToSearchRelevance(pinyinOrSynonymScore));
                }
            }

            score += document.Kind == SearchResultKind.Todo
                ? document.TodoIsCompleted ? -20 : 10
                : document.IsPinned ? 5 : 0;
            string? docBody = !string.IsNullOrWhiteSpace(document.BodyText)
                ? document.BodyText
                : null;
            results.Add(new SearchResultItem
            {
                Kind = document.Kind,
                Title = document.Title,
                Subtitle = document.Subtitle,
                DetailPath = document.AuxiliaryText,
                TodoWidgetId = document.TodoWidgetId,
                TodoItemId = document.TodoItemId,
                TodoIsCompleted = document.TodoIsCompleted,
                QuickCaptureItemId = document.QuickCaptureItemId,
                Glyph = document.Kind == SearchResultKind.Todo ? "\uE9D5" : "\uE70F",
                ModifiedAt = document.ModifiedAt,
                RelevanceScore = Math.Max(1, score),
                PreviewText = docBody ?? document.AuxiliaryText,
                PreviewCharCount = docBody?.Length,
                PreviewLineCount = docBody is not null ? CountTextLines(docBody) : null
            });
        }

        return results;
    }

    internal static bool TryParseQuickNoteCommand(string? query, out string noteText) =>
        SearchQuerySyntaxParser.TryParseQuickNoteCommand(query, out noteText);

    internal static bool TryParseQuickTodoCommand(string? query, out string todoText) =>
        SearchQuerySyntaxParser.TryParseQuickTodoCommand(query, out todoText);

    private IReadOnlyList<SearchResultItem> SearchActionsAndSettings(ParsedSearchQuery parsed)
    {
        var results = new List<SearchResultItem>();
        string effectiveQuery = parsed.EffectiveQuery;

        // 1. Inline Calculator (top-hit card when user types e.g. "=128*3.5" or "(1920-660)/2").
        if (parsed.Scope == SearchInlineScope.None &&
            _settingsService.Settings.Search.SearchIncludeCalculator &&
            SearchCalculatorEvaluator.TryEvaluate(parsed.RawQuery, out SearchCalculationResult calc))
        {
            string copyHint = _localizationService.T("Search.Calculator.CopyHint");
            if (string.Equals(copyHint, "Search.Calculator.CopyHint", StringComparison.Ordinal))
            {
                copyHint = "Press Enter to copy result";
            }

            results.Add(new SearchResultItem
            {
                Kind = SearchResultKind.Action,
                Title = $"= {calc.FormattedValue}",
                Subtitle = $"{calc.NormalizedExpression} · {copyHint}",
                ActionId = $"copy-calc:{calc.FormattedValue}",
                Glyph = "\uE8EF",
                RelevanceScore = 12000,
                PreviewText = $"{calc.NormalizedExpression}\n= {calc.FormattedValue}"
            });
        }

        // 1b. Explicit Web Search ("? keyword", "g: keyword", "bd: keyword", "gh: keyword") and Direct URL ("github.com", "https://...").
        if (parsed.Scope == SearchInlineScope.None)
        {
            var searchSettings = _settingsService.Settings.Search;
            if (SearchWebAndUrlEvaluator.TryEvaluateExplicitWebSearch(
                    parsed.RawQuery,
                    out ExplicitWebSearchMatch webMatch,
                    searchSettings.SearchDefaultWebEngine,
                    searchSettings.SearchCustomWebUrl))
            {
                string displayQuery = TruncateText(webMatch.Query, 60);
                string format = _localizationService.T("Search.Web.FallbackFormat");
                string title = string.IsNullOrWhiteSpace(format) ||
                               string.Equals(format, "Search.Web.FallbackFormat", StringComparison.Ordinal)
                    ? $"Search the web for \"{displayQuery}\""
                    : string.Format(format, displayQuery);
                string engineFormat = _localizationService.T("Search.Web.EngineSubtitleFormat");
                string subtitle = string.IsNullOrWhiteSpace(engineFormat) ||
                                  string.Equals(engineFormat, "Search.Web.EngineSubtitleFormat", StringComparison.Ordinal)
                    ? $"Search the web with {webMatch.EngineName}"
                    : string.Format(engineFormat, webMatch.EngineName);

                results.Add(new SearchResultItem
                {
                    Kind = SearchResultKind.Action,
                    Title = title,
                    Subtitle = subtitle,
                    DetailPath = webMatch.TargetUri,
                    ActionId = $"{SearchWebAndUrlEvaluator.WebSearchActionPrefix}{webMatch.TargetUri}",
                    Glyph = "\uE774",
                    RelevanceScore = 11200
                });
            }
            else if (SearchWebAndUrlEvaluator.TryEvaluateDirectUrl(parsed.RawQuery, out DirectUrlMatch urlMatch))
            {
                string displayUrl = TruncateText(urlMatch.DisplayText, 64);
                string openFormat = _localizationService.T("Search.Url.OpenFormat");
                string title = string.IsNullOrWhiteSpace(openFormat) ||
                               string.Equals(openFormat, "Search.Url.OpenFormat", StringComparison.Ordinal)
                    ? $"Open URL {displayUrl}"
                    : string.Format(openFormat, displayUrl);
                string subtitle = _localizationService.T("Search.Url.Subtitle");
                if (string.IsNullOrWhiteSpace(subtitle) ||
                    string.Equals(subtitle, "Search.Url.Subtitle", StringComparison.Ordinal))
                {
                    subtitle = urlMatch.NormalizedUri;
                }

                results.Add(new SearchResultItem
                {
                    Kind = SearchResultKind.Action,
                    Title = title,
                    Subtitle = subtitle,
                    DetailPath = urlMatch.NormalizedUri,
                    ActionId = $"{SearchWebAndUrlEvaluator.OpenUrlActionPrefix}{urlMatch.NormalizedUri}",
                    Glyph = "\uE774",
                    RelevanceScore = 11500
                });
            }
        }

        // 2. Quick-create Todo ("+Buy milk" / "todo Buy milk" / "待办 买牛奶").
        if (!string.IsNullOrWhiteSpace(parsed.QuickTodoText) &&
            parsed.Scope is SearchInlineScope.None or SearchInlineScope.Todos or SearchInlineScope.Commands &&
            CanOfferTodoQuickCreate(isExplicitPrefix: true))
        {
            results.Add(CreateQuickTodoActionItem(parsed.QuickTodoText, relevanceScore: 10500, isFallback: false));
        }

        // 3. Quick-create Note (";Idea" / "note Idea" / "随记 想法").
        if (!string.IsNullOrWhiteSpace(parsed.QuickNoteText) &&
            parsed.Scope is SearchInlineScope.None or SearchInlineScope.Notes or SearchInlineScope.Commands &&
            CanOfferNoteQuickCreate(isExplicitPrefix: true))
        {
            results.Add(CreateQuickNoteActionItem(parsed.QuickNoteText, relevanceScore: 10000, isFallback: false));
        }

        // 4. Built-in DeskBox actions (matched by literal, synonym, or Pinyin).
        bool allowTodoActions = CanOfferTodoQuickCreate(isExplicitPrefix: true);
        bool allowNoteActions = CanOfferNoteQuickCreate(isExplicitPrefix: true);
        var actions = new List<(string Id, string NameKey, string Glyph)>(6);
        if (allowTodoActions)
        {
            actions.Add(("new-todo", "Search.Action.NewTodo", "\uE9D5"));
        }
        if (allowNoteActions)
        {
            actions.Add(("new-note", "Search.Action.NewNote", "\uE70F"));
            actions.Add(("quick-jot", "QuickCapture.QuickJotTitle", "\uE70F"));
        }
        actions.Add(("open-settings", "Search.Action.OpenSettings", "\uE713"));
        actions.Add(("toggle-widgets", "Search.Action.ToggleWidgets", "\uE8A5"));
        actions.Add(("toggle-theme", "Search.Action.ToggleTheme", "\uE793"));

        bool listAllCommands = parsed.Scope == SearchInlineScope.Commands &&
                               string.IsNullOrWhiteSpace(effectiveQuery);
        foreach (var (id, nameKey, glyph) in actions)
        {
            string name = _localizationService.T(nameKey);
            if (listAllCommands)
            {
                results.Add(new SearchResultItem
                {
                    Kind = SearchResultKind.Action,
                    Title = name,
                    ActionId = id,
                    Glyph = glyph,
                    RelevanceScore = 85
                });
                continue;
            }

            if (string.IsNullOrWhiteSpace(effectiveQuery))
            {
                continue;
            }

            int matchScore = SettingsSearchMatcher.GetScore(effectiveQuery, name, string.Empty, id);
            if (name.Contains(effectiveQuery, StringComparison.OrdinalIgnoreCase) ||
                matchScore != SettingsSearchMatcher.NoMatch)
            {
                double baseScore = name.Contains(effectiveQuery, StringComparison.OrdinalIgnoreCase)
                    ? ComputeTextRelevance(name, effectiveQuery) + 8
                    : SettingsSearchMatcher.ConvertMatchScoreToSearchRelevance(matchScore) + 4;
                results.Add(new SearchResultItem
                {
                    Kind = SearchResultKind.Action,
                    Title = name,
                    ActionId = id,
                    Glyph = glyph,
                    RelevanceScore = baseScore
                });
            }
        }

        // 5. DeskBox Settings deep-link search ("透明度", "tmd", "mica", "set: ...").
        if (parsed.Scope is SearchInlineScope.None or SearchInlineScope.Settings or SearchInlineScope.Commands)
        {
            bool listDefaultSettings = parsed.Scope == SearchInlineScope.Settings &&
                                       string.IsNullOrWhiteSpace(effectiveQuery);
            bool allowSettingsMatch = listDefaultSettings ||
                                      (!string.IsNullOrWhiteSpace(effectiveQuery) &&
                                       (parsed.Scope != SearchInlineScope.None || effectiveQuery.Length >= 2));
            if (allowSettingsMatch)
            {
                SettingsCatalogItem[] catalog = GetOrBuildSettingsCatalog();
                if (listDefaultSettings)
                {
                    int rank = 0;
                    foreach (SettingsCatalogItem entry in catalog.Take(20))
                    {
                        results.Add(new SearchResultItem
                        {
                            Kind = SearchResultKind.Action,
                            Title = entry.Title,
                            Subtitle = entry.Breadcrumb,
                            ActionId = $"open-settings:{entry.SectionTag}@{entry.HeaderKey}",
                            Glyph = "\uE713",
                            RelevanceScore = 80 - rank++
                        });
                    }
                }
                else
                {
                    int limit = parsed.Scope == SearchInlineScope.Settings ? 24 : 8;
                    var matchedSettings = catalog
                        .Select(entry => (
                            Entry: entry,
                            Score: SettingsSearchMatcher.GetScore(
                                effectiveQuery,
                                entry.Title,
                                entry.Breadcrumb,
                                entry.Description)))
                        .Where(x => x.Score != SettingsSearchMatcher.NoMatch)
                        .OrderBy(x => x.Score)
                        .ThenBy(x => x.Entry.Title.Length)
                        .Take(limit);

                    foreach (var (entry, matchScore) in matchedSettings)
                    {
                        double relevance = Math.Max(
                            24,
                            SettingsSearchMatcher.ConvertMatchScoreToSearchRelevance(matchScore) - 4);
                        results.Add(new SearchResultItem
                        {
                            Kind = SearchResultKind.Action,
                            Title = entry.Title,
                            Subtitle = entry.Breadcrumb,
                            ActionId = $"open-settings:{entry.SectionTag}@{entry.HeaderKey}",
                            Glyph = "\uE713",
                            RelevanceScore = relevance
                        });
                    }
                }
            }
        }

        return results;
    }

    private SettingsCatalogItem[] GetOrBuildSettingsCatalog()
    {
        string culture = _localizationService.CurrentCultureName;
        lock (_settingsCatalogLock)
        {
            if (_cachedSettingsCatalog is not null &&
                string.Equals(_cachedSettingsCatalogCulture, culture, StringComparison.OrdinalIgnoreCase))
            {
                return _cachedSettingsCatalog;
            }
        }

        Dictionary<string, string> strings = _localizationService.LoadUnfilteredLocaleStringsForCurrentCulture();
        bool isStoreBuild = AppDistributionService.Current.IsMicrosoftStore;
        var built = new List<SettingsCatalogItem>(SettingsSearchCatalog.Entries.Count);

        string Lookup(string? key) =>
            !string.IsNullOrWhiteSpace(key) && strings.TryGetValue(key, out string? val) && !string.IsNullOrWhiteSpace(val)
                ? val
                : string.Empty;

        string settingsRootLabel = _localizationService.T("Search.Action.OpenSettings");
        foreach (SettingsSearchCatalogEntry entry in SettingsSearchCatalog.Entries)
        {
            if (isStoreBuild && SettingsSearchChannelPolicy.IsHiddenInStore(entry.HeaderKey))
            {
                continue;
            }

            string title = Lookup(entry.HeaderKey);
            if (string.IsNullOrWhiteSpace(title))
            {
                continue;
            }

            string sectionTitleKey = ResolveSectionTitleKey(entry.SectionTag);
            string sectionTitle = Lookup(sectionTitleKey);
            string breadcrumb = !string.IsNullOrWhiteSpace(sectionTitle)
                ? $"{settingsRootLabel} · {sectionTitle}"
                : settingsRootLabel;
            string description = Lookup(entry.DescriptionKey);

            built.Add(new SettingsCatalogItem(
                entry.SectionTag,
                entry.HeaderKey,
                title,
                breadcrumb,
                description));
        }

        SettingsCatalogItem[] array = built.ToArray();
        lock (_settingsCatalogLock)
        {
            _cachedSettingsCatalog = array;
            _cachedSettingsCatalogCulture = culture;
        }

        return array;
    }

    private static string ResolveSectionTitleKey(string sectionTag) => sectionTag switch
    {
        "General" or "PerformanceSettings" => SettingsPrefix + "Section.General",
        "Appearance" or "AppearanceMaterialSettings" or "AppearanceDensitySettings" or "AppearanceWindowSettings" or "AppearanceAnimationSettings" =>
            SettingsPrefix + "Section.Appearance",
        "Interaction" or "InteractionWindowSettings" or "CapsuleMode" or "CapsuleOverridesSettings" =>
            SettingsPrefix + "Section.Interaction",
        "WidgetGroups" => SettingsPrefix + "Section.WidgetGroups",
        "Displays" => SettingsPrefix + "Displays.Title",
        "AppearanceDetail" or "DesktopOrganizationSettings" or "FileStorageSettings" or "FileDisplaySettings" or "ManagedStorage" =>
            SettingsPrefix + "Appearance.DetailTitle",
        "FileStackSettings" => SettingsPrefix + "FileStacks.PageTitle",
        "FeatureWidgets" => SettingsPrefix + "Section.FeatureWidgets",
        "QuickCaptureSettings" => SettingsPrefix + "QuickCapture.Title",
        "TodoSettings" => SettingsPrefix + "Todo.Title",
        "MusicSettings" => SettingsPrefix + "Music.Title",
        "WeatherSettings" => SettingsPrefix + "Weather.Title",
        "GlanceSettings" => "Glance.Settings.Title",
        "ClipboardSettings" => "Clipboard.Settings.Title",
        "CustomWidgetSettings" => "CustomWidget.Settings.Title",
        "SearchSettings" => SettingsPrefix + "Search.Title",
        "AiSettings" => SettingsPrefix + "Nav.AiSettings",
        "Maintenance" or "BackupRestoreSettings" or "CloudBackupSettings" or "ResetSettings" =>
            SettingsPrefix + "Section.Maintenance",
        "About" or "DataHealthSettings" or "CompatibilityDiagnosticsSettings" =>
            SettingsPrefix + "Nav.About",
        _ => SettingsPrefix + "Section.General"
    };

    private IReadOnlyList<SearchResultGroup> BuildGroups(
        IReadOnlyList<SearchResultItem> rankedResults)
    {
        var groups = new List<SearchResultGroup>();

        var groupOrder = new[]
        {
            (SearchResultKind.Action, _localizationService.T("Search.Group.Actions")),
            (SearchResultKind.Todo, _localizationService.T("Search.Group.Todos")),
            (SearchResultKind.QuickCapture, _localizationService.T("Search.Group.Notes")),
            (SearchResultKind.Clipboard, _localizationService.T("Search.Type.Clipboard")),
            (SearchResultKind.File, _localizationService.T("Search.Group.Files")),
            (SearchResultKind.Folder, _localizationService.T("Search.Group.Folders"))
        };

        foreach (var (kind, displayName) in groupOrder)
        {
            var items = rankedResults
                .Where(r => r.Kind == kind)
                .ToList();

            if (items.Count > 0)
            {
                groups.Add(new SearchResultGroup
                {
                    Kind = kind,
                    DisplayName = displayName,
                    Items = items,
                    TotalCount = items.Count
                });
            }
        }

        return groups;
    }

    private static double ComputeTextRelevance(string text, string query)
    {
        if (text.Equals(query, StringComparison.OrdinalIgnoreCase))
        {
            return 100;
        }

        if (text.StartsWith(query, StringComparison.OrdinalIgnoreCase))
        {
            return 80;
        }

        if (text.Contains(query, StringComparison.OrdinalIgnoreCase))
        {
            return 50;
        }

        return 30;
    }

    internal static string? TruncateIndexedBody(string? text)
    {
        if (string.IsNullOrEmpty(text) || text.Length <= MaxIndexedBodyCharacters)
        {
            return text;
        }

        int length = MaxIndexedBodyCharacters;
        if (char.IsHighSurrogate(text[length - 1]))
        {
            length--;
        }

        return text[..length];
    }

    private static string TruncateText(string text, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        string singleLine = text.ReplaceLineEndings(" ").Trim();
        return singleLine.Length <= maxLength
            ? singleLine
            : singleLine[..maxLength] + "...";
    }

    private sealed record SettingsCatalogItem(
        string SectionTag,
        string HeaderKey,
        string Title,
        string Breadcrumb,
        string Description);

    private sealed record DeskBoxSearchDocument(
        SearchResultKind Kind,
        string Title,
        string? BodyText,
        string? AuxiliaryText,
        string Subtitle,
        string? TodoWidgetId,
        string? TodoItemId,
        bool TodoIsCompleted,
        string? QuickCaptureItemId,
        bool IsPinned,
        DateTimeOffset ModifiedAt);

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        UnregisterActiveInstance(this);
        Interlocked.Increment(ref _deskBoxContentCacheGeneration);
        _quickCaptureService.Changed -= OnQuickCaptureChanged;
        _localizationService.LanguageChanged -= OnLanguageChanged;
        TodoWidgetStore.StoreChanged -= OnTodoStoreChanged;
        _deskBoxContentLifetimeCts.Cancel();
        _deskBoxContentLifetimeCts.Dispose();
        Volatile.Write(ref _deskBoxContentSnapshot, []);
        Volatile.Write(ref _deskBoxContentInitialized, 0);
        lock (_settingsCatalogLock)
        {
            _cachedSettingsCatalog = null;
            _cachedSettingsCatalogCulture = null;
        }
        _everythingSearchService.Dispose();
    }
}
