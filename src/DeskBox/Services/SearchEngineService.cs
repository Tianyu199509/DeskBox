using System.Diagnostics;
using System.Runtime.CompilerServices;
using DeskBox.Models;

namespace DeskBox.Services;

/// <summary>
/// Coordinates Everything filename results with a small DeskBox-content snapshot.
/// DeskBox owns no filename index, scanner, filesystem watcher, or persisted catalog.
/// </summary>
public sealed class SearchEngineService : IDisposable
{
    public const int InitialFileResultPageSize = 200;
    public const int FileResultPageSize = 200;
    internal const int MaxIndexedBodyCharacters = 2048;
    private const int DeskBoxContentRefreshIntervalMilliseconds = 1000;

    private static readonly object s_activeInstancesLock = new();
    private static readonly List<WeakReference<SearchEngineService>> s_activeInstances = [];

    private readonly SettingsService _settingsService;
    private readonly LocalizationService _localizationService;
    private readonly EverythingSearchService _everythingSearchService;
    private readonly QuickCaptureService _quickCaptureService;
    private readonly object _deskBoxContentRefreshLock = new();
    private readonly CancellationTokenSource _deskBoxContentLifetimeCts = new();
    private DeskBoxSearchDocument[] _deskBoxContentSnapshot = [];
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
        QuickCaptureService quickCaptureService)
    {
        _settingsService = settingsService;
        _localizationService = localizationService;
        _everythingSearchService = everythingSearchService;
        _quickCaptureService = quickCaptureService;
        _quickCaptureService.Changed += OnQuickCaptureChanged;
        TodoWidgetStore.StoreChanged += OnTodoStoreChanged;
        RegisterActiveInstance(this);
    }

    public EverythingSearchService EverythingProvider => _everythingSearchService;

    public event Action? ResultsChanged;

    private void OnQuickCaptureChanged() => Interlocked.Exchange(ref _deskBoxContentDirty, 1);

    private void OnTodoStoreChanged(string storePath, string widgetId) =>
        Interlocked.Exchange(ref _deskBoxContentDirty, 1);

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

        Task<MeasuredProviderResult<SearchFileQueryPage>> fileTask = MeasureProviderAsync(
            "everything",
            () => _everythingSearchService.SearchPageAsync(
                query,
                normalizedOffset,
                normalizedPageSize,
                cancellationToken));
        Task<MeasuredProviderResult<IReadOnlyList<SearchResultItem>>> deskBoxTask =
            _settingsService.Settings.SearchIncludeDeskBoxContent
                ? MeasureProviderAsync(
                    "deskbox-content",
                    () => SearchDeskBoxContentAsync(query, cancellationToken))
                : Task.FromResult(new MeasuredProviderResult<IReadOnlyList<SearchResultItem>>(
                    [],
                    0,
                    null));

        IReadOnlyList<SearchResultItem> actions = SearchActions(query);
        await Task.WhenAll(fileTask, deskBoxTask).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        MeasuredProviderResult<SearchFileQueryPage> measuredFile = await fileTask.ConfigureAwait(false);
        MeasuredProviderResult<IReadOnlyList<SearchResultItem>> measuredDeskBox =
            await deskBoxTask.ConfigureAwait(false);
        if (measuredFile.Failure is not null)
        {
            LogProviderFailure(measuredFile.Provider, measuredFile.Failure);
        }
        if (measuredDeskBox.Failure is not null)
        {
            LogProviderFailure(measuredDeskBox.Provider, measuredDeskBox.Failure);
        }

        SearchFileQueryPage filePage = measuredFile.Failure is null
            ? measuredFile.Value
            : SearchFileQueryPage.Empty;
        IReadOnlyList<SearchResultItem> deskBoxResults = measuredDeskBox.Failure is null
            ? measuredDeskBox.Value
            : [];
        stopwatch.Stop();
        SearchResponse response = BuildSearchResponse(
            query,
            filePage,
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
        SearchFileQueryPage filePage,
        IReadOnlyList<SearchResultItem> deskBoxResults,
        IReadOnlyList<SearchResultItem> actions,
        TimeSpan elapsed)
    {
        IReadOnlyList<SearchResultItem> rankedItems = SearchResultRanker.MergeAndRank(
            filePage.Items.Concat(deskBoxResults).Concat(actions),
            query.Trim(),
            int.MaxValue);
        IReadOnlyList<SearchResultGroup> groups = BuildGroups(rankedItems);
        int materializedFileResults = rankedItems.Count(item =>
            item.Kind is SearchResultKind.File or SearchResultKind.Folder);
        int nonFileResults = rankedItems.Count - materializedFileResults;

        return new SearchResponse
        {
            Query = query,
            RankedItems = rankedItems,
            Groups = groups,
            TotalResultCount = (int)Math.Min(
                int.MaxValue,
                (long)filePage.TotalMatchedCount + nonFileResults),
            MaterializedFileResultCount = materializedFileResults,
            TotalFileResultCount = filePage.TotalMatchedCount,
            NextFileResultOffset = filePage.NextOffset,
            HasMoreResults = filePage.NextOffset < filePage.TotalMatchedCount,
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
        foreach (var widget in _settingsService.Settings.Widgets
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

    // GetRecentNotesAsync / GetUpcomingTodosAsync removed (deep review
    // R15, F6 audit): zero callers; the notes path also bypassed the
    // QuickCapture service by newing a raw store, inconsistent with
    // every other path in this file.

    private async Task<IReadOnlyList<SearchResultItem>> SearchDeskBoxContentAsync(
        string query,
        CancellationToken cancellationToken)
    {
        bool initialized = Volatile.Read(ref _deskBoxContentInitialized) != 0;
        if (!initialized || Volatile.Read(ref _deskBoxContentDirty) != 0)
        {
            await EnsureDeskBoxContentSnapshotAsync(
                    forceRefresh: true,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        DeskBoxSearchDocument[] snapshot = Volatile.Read(ref _deskBoxContentSnapshot);
        return await Task.Run(
                () => SearchDeskBoxContentSnapshot(snapshot, query, cancellationToken),
                cancellationToken)
            .ConfigureAwait(false);
    }

    private Task EnsureDeskBoxContentSnapshotAsync(
        bool forceRefresh,
        CancellationToken cancellationToken)
    {
        if (_isDisposed || !_settingsService.Settings.SearchIncludeDeskBoxContent)
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
                        _deskBoxContentLifetimeCts.Token),
                    _deskBoxContentLifetimeCts.Token);
                _deskBoxContentRefreshTask = refreshTask;
            }
        }

        return cancellationToken.CanBeCanceled
            ? refreshTask.WaitAsync(cancellationToken)
            : refreshTask;
    }

    private async Task RefreshDeskBoxContentSnapshotAsync(
        CancellationToken cancellationToken)
    {
        int generation = Volatile.Read(ref _deskBoxContentCacheGeneration);
        try
        {
            DeskBoxSearchDocument[] next = await BuildDeskBoxContentSnapshotAsync(
                    cancellationToken)
                .ConfigureAwait(false);
            if (_isDisposed ||
                cancellationToken.IsCancellationRequested ||
                !_settingsService.Settings.SearchIncludeDeskBoxContent ||
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
        foreach (QuickCaptureSearchProjection item in quickCaptureProjections)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string displayTitle = !string.IsNullOrWhiteSpace(item.Title)
                ? item.Title
                : TruncateText(item.Text, 60);
            documents.Add(new DeskBoxSearchDocument(
                SearchResultKind.QuickCapture,
                displayTitle,
                TruncateIndexedBody(item.Text),
                item.Url,
                item.Type.ToString(),
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

            foreach (TodoItem item in data.Items)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (item.IsDeleted)
                {
                    continue;
                }

                documents.Add(new DeskBoxSearchDocument(
                    SearchResultKind.Todo,
                    item.Text,
                    TruncateIndexedBody(item.Notes),
                    AuxiliaryText: null,
                    item.DueDate.HasValue
                        ? $"{_localizationService.T("Search.Todo.Due")}: {item.DueDate.Value:yyyy-MM-dd}"
                        : widget.Name,
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
        string query,
        CancellationToken cancellationToken)
    {
        var results = new List<SearchResultItem>();
        foreach (DeskBoxSearchDocument document in snapshot)
        {
            cancellationToken.ThrowIfCancellationRequested();
            bool titleMatches = document.Title.Contains(
                query,
                StringComparison.OrdinalIgnoreCase);
            bool bodyMatches = document.BodyText?.Contains(
                query,
                StringComparison.OrdinalIgnoreCase) == true;
            bool auxiliaryMatches = document.AuxiliaryText?.Contains(
                query,
                StringComparison.OrdinalIgnoreCase) == true;
            if (!titleMatches && !bodyMatches && !auxiliaryMatches)
            {
                continue;
            }

            double score = 1;
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

            score += document.Kind == SearchResultKind.Todo
                ? document.TodoIsCompleted ? -20 : 10
                : document.IsPinned ? 5 : 0;
            results.Add(new SearchResultItem
            {
                Kind = document.Kind,
                Title = document.Title,
                Subtitle = document.Subtitle,
                TodoWidgetId = document.TodoWidgetId,
                TodoItemId = document.TodoItemId,
                TodoIsCompleted = document.TodoIsCompleted,
                QuickCaptureItemId = document.QuickCaptureItemId,
                Glyph = document.Kind == SearchResultKind.Todo ? "\uE9D5" : "\uE70F",
                ModifiedAt = document.ModifiedAt,
                RelevanceScore = Math.Max(1, score)
            });
        }

        return results;
    }

    internal static bool TryParseQuickNoteCommand(string? query, out string noteText)
    {
        noteText = string.Empty;
        if (string.IsNullOrWhiteSpace(query))
        {
            return false;
        }

        ReadOnlySpan<char> span = query.AsSpan().Trim();
        ReadOnlySpan<string> prefixes = ["note", "jot", "n", "随记", "速记", "记"];
        foreach (string prefix in prefixes)
        {
            if (!span.StartsWith(prefix.AsSpan(), StringComparison.OrdinalIgnoreCase) ||
                span.Length <= prefix.Length)
            {
                continue;
            }

            char delimiter = span[prefix.Length];
            if (!char.IsWhiteSpace(delimiter) && delimiter is not (':' or '：'))
            {
                continue;
            }

            ReadOnlySpan<char> body = span[(prefix.Length + 1)..].Trim();
            if (!body.IsEmpty)
            {
                noteText = body.ToString();
                return true;
            }
        }

        return false;
    }

    private IReadOnlyList<SearchResultItem> SearchActions(string query)
    {
        var actions = new (string Id, string NameKey, string Glyph)[]
        {
            ("new-todo", "Search.Action.NewTodo", "\uE9D5"),
            ("new-note", "Search.Action.NewNote", "\uE70F"),
            ("quick-jot", "QuickCapture.QuickJotTitle", "\uE70F"),
            ("open-settings", "Search.Action.OpenSettings", "\uE713"),
            ("toggle-widgets", "Search.Action.ToggleWidgets", "\uE8A5"),
            ("toggle-theme", "Search.Action.ToggleTheme", "\uE793")
        };

        var results = new List<SearchResultItem>();
        if (TryParseQuickNoteCommand(query, out string quickNoteText))
        {
            string template = _localizationService.T("QuickCapture.SearchQuickSaveFormat");
            string title = string.IsNullOrWhiteSpace(template) ||
                           string.Equals(template, "QuickCapture.SearchQuickSaveFormat", StringComparison.Ordinal)
                ? $"Save to Quick Capture: {quickNoteText}"
                : string.Format(template, quickNoteText);
            results.Add(new SearchResultItem
            {
                Kind = SearchResultKind.Action,
                Title = title,
                Subtitle = _localizationService.T("QuickCapture.Name"),
                ActionId = $"quick-note:{quickNoteText}",
                Glyph = "\uE70F",
                RelevanceScore = 10000
            });
        }

        foreach (var (id, nameKey, glyph) in actions)
        {
            string name = _localizationService.T(nameKey);
            if (name.Contains(query, StringComparison.OrdinalIgnoreCase))
            {
                results.Add(new SearchResultItem
                {
                    Kind = SearchResultKind.Action,
                    Title = name,
                    ActionId = id,
                    Glyph = glyph,
                    RelevanceScore = ComputeTextRelevance(name, query) + 5
                });
            }
        }

        return results;
    }

    private IReadOnlyList<SearchResultGroup> BuildGroups(
        IReadOnlyList<SearchResultItem> rankedResults)
    {
        var groups = new List<SearchResultGroup>();

        var groupOrder = new[]
        {
            (SearchResultKind.Action, _localizationService.T("Search.Group.Actions")),
            (SearchResultKind.Todo, _localizationService.T("Search.Group.Todos")),
            (SearchResultKind.QuickCapture, _localizationService.T("Search.Group.Notes")),
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
        TodoWidgetStore.StoreChanged -= OnTodoStoreChanged;
        _deskBoxContentLifetimeCts.Cancel();
        _deskBoxContentLifetimeCts.Dispose();
        Volatile.Write(ref _deskBoxContentSnapshot, []);
        Volatile.Write(ref _deskBoxContentInitialized, 0);
        _everythingSearchService.Dispose();
    }
}
