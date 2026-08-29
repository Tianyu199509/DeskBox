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
    private const int DeskBoxContentRefreshIntervalMilliseconds = 1000;

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
    }

    public EverythingSearchService EverythingProvider => _everythingSearchService;

    public event Action? ResultsChanged;

    private void OnQuickCaptureChanged()
    {
        if (!_isDisposed && _settingsService.Settings.SearchIncludeDeskBoxContent)
        {
            _ = EnsureDeskBoxContentSnapshotAsync(
                forceRefresh: true,
                _deskBoxContentLifetimeCts.Token);
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
            _ = EnsureDeskBoxContentSnapshotAsync(
                forceRefresh: true,
                _deskBoxContentLifetimeCts.Token);
            return;
        }

        Volatile.Write(ref _deskBoxContentSnapshot, []);
        Volatile.Write(ref _deskBoxContentInitialized, 0);
        Interlocked.Exchange(ref _deskBoxContentLastRefreshMs, long.MinValue);
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

    public async IAsyncEnumerable<SearchResponse> SearchStagedAsync(
        string query,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        yield return await SearchAsync(query, cancellationToken).ConfigureAwait(false);
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

    private IReadOnlyList<SearchRecommendationItem> BuildApplicationRecommendations(
        CancellationToken cancellationToken)
    {
        var recommendations = new List<SearchRecommendationItem>();
        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void AddShortcut(string path, string subtitle)
        {
            if (cancellationToken.IsCancellationRequested ||
                !path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) ||
                !File.Exists(path))
            {
                return;
            }

            string fullPath;
            try
            {
                fullPath = Path.GetFullPath(path);
            }
            catch
            {
                return;
            }

            if (!seenPaths.Add(fullPath))
            {
                return;
            }

            recommendations.Add(new SearchRecommendationItem
            {
                Kind = SearchResultKind.File,
                Title = Path.GetFileName(fullPath),
                Subtitle = subtitle,
                DetailPath = fullPath
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
        string[] startMenuRoots =
        [
            Environment.GetFolderPath(Environment.SpecialFolder.Programs),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms),
            Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu)
        ];

        const int MaxStartMenuApps = 40;
        int startMenuCount = 0;
        foreach (string root in startMenuRoots
                     .Where(path => !string.IsNullOrWhiteSpace(path))
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            foreach (string shortcut in EnumerateShortcutFilesSafely(
                         root, recursive: true, cancellationToken)
                     .OrderBy(Path.GetFileName, StringComparer.CurrentCultureIgnoreCase))
            {
                int before = recommendations.Count;
                AddShortcut(shortcut, startMenuLabel);
                if (recommendations.Count > before && ++startMenuCount >= MaxStartMenuApps)
                {
                    return recommendations;
                }
            }
        }

        return recommendations;
    }

    private static IEnumerable<string> EnumerateShortcutFilesSafely(
        string root,
        bool recursive,
        CancellationToken cancellationToken)
    {
        if (!FileService.TryResolveExistingPathForTraversal(
                root,
                out string resolvedRoot))
        {
            yield break;
        }

        var pending = new Stack<string>();
        pending.Push(resolvedRoot);
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

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

    private async Task<IReadOnlyList<SearchRecommendationItem>> GetRecentNotesAsync(
        CancellationToken cancellationToken)
    {
        var recommendations = new List<SearchRecommendationItem>();

        try
        {
            var store = new QuickCaptureStore();
            var data = await store.LoadAsync();

            var recent = data.Items
                .Where(i => !i.IsDeleted)
                .OrderByDescending(i => i.UpdatedAt)
                .Take(3);

            foreach (var item in recent)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                recommendations.Add(new SearchRecommendationItem
                {
                    Kind = SearchResultKind.QuickCapture,
                    Title = !string.IsNullOrWhiteSpace(item.Title)
                        ? item.Title
                        : TruncateText(item.Body, 60),
                    Subtitle = item.Type.ToString(),
                    Glyph = "\uE70F",
                    QuickCaptureItemId = item.Id
                });
            }
        }
        catch
        {
            // Skip if QuickCapture data fails to load
        }

        return recommendations;
    }

    private async Task<IReadOnlyList<SearchResultItem>> SearchDeskBoxContentAsync(
        string query,
        CancellationToken cancellationToken)
    {
        bool initialized = Volatile.Read(ref _deskBoxContentInitialized) != 0;
        if (!initialized)
        {
            await EnsureDeskBoxContentSnapshotAsync(
                    forceRefresh: true,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        else if (Environment.TickCount64 -
                 Interlocked.Read(ref _deskBoxContentLastRefreshMs) >=
                 DeskBoxContentRefreshIntervalMilliseconds)
        {
            // Return the current immutable snapshot immediately. The refresh runs
            // outside the query hot path and raises ResultsChanged only if content
            // actually changed, which refreshes an already-visible query in place.
            _ = EnsureDeskBoxContentSnapshotAsync(
                forceRefresh: false,
                _deskBoxContentLifetimeCts.Token);
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
        try
        {
            DeskBoxSearchDocument[] next = await BuildDeskBoxContentSnapshotAsync(
                    cancellationToken)
                .ConfigureAwait(false);
            if (_isDisposed ||
                cancellationToken.IsCancellationRequested ||
                !_settingsService.Settings.SearchIncludeDeskBoxContent)
            {
                return;
            }

            DeskBoxSearchDocument[] previous = Volatile.Read(ref _deskBoxContentSnapshot);
            bool wasInitialized = Volatile.Read(ref _deskBoxContentInitialized) != 0;
            bool changed = !previous.SequenceEqual(next);
            Volatile.Write(ref _deskBoxContentSnapshot, next);
            Volatile.Write(ref _deskBoxContentInitialized, 1);
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
        QuickCaptureStoreData quickCapture = await _quickCaptureService
            .GetDataAsync()
            .WaitAsync(cancellationToken)
            .ConfigureAwait(false);
        foreach (QuickCaptureItem item in quickCapture.Items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (item.IsDeleted)
            {
                continue;
            }

            string displayTitle = !string.IsNullOrWhiteSpace(item.Title)
                ? item.Title
                : TruncateText(item.Body, 60);
            documents.Add(new DeskBoxSearchDocument(
                SearchResultKind.QuickCapture,
                displayTitle,
                item.Body,
                item.Url,
                item.Type.ToString(),
                TodoWidgetId: null,
                TodoItemId: null,
                TodoIsCompleted: false,
                QuickCaptureItemId: item.Id,
                IsPinned: item.IsPinned,
                item.UpdatedAt));
        }

        List<WidgetConfig> todoWidgets = _settingsService.Settings.Widgets
            .Where(widget => widget.WidgetKind == WidgetKind.Todo && !widget.IsDisabled)
            .ToList();
        Task<(WidgetConfig Widget, TodoWidgetData? Data)>[] todoLoads = todoWidgets
            .Select(async widget =>
            {
                try
                {
                    TodoWidgetData data = await new TodoWidgetStore(widget.Id)
                        .LoadAsync()
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
                documents.Add(new DeskBoxSearchDocument(
                    SearchResultKind.Todo,
                    item.Text,
                    item.Notes,
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

    private IReadOnlyList<SearchResultItem> SearchActions(string query)
    {
        var actions = new (string Id, string NameKey, string Glyph)[]
        {
            ("new-todo", "Search.Action.NewTodo", "\uE9D5"),
            ("new-note", "Search.Action.NewNote", "\uE70F"),
            ("open-settings", "Search.Action.OpenSettings", "\uE713"),
            ("toggle-widgets", "Search.Action.ToggleWidgets", "\uE8A5"),
            ("toggle-theme", "Search.Action.ToggleTheme", "\uE793")
        };

        var results = new List<SearchResultItem>();
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

    private async Task<IReadOnlyList<SearchRecommendationItem>> GetUpcomingTodosAsync(
        CancellationToken cancellationToken)
    {
        var recommendations = new List<SearchRecommendationItem>();
        var settings = _settingsService.Settings;

        var todoWidgets = settings.Widgets
            .Where(w => w.WidgetKind == WidgetKind.Todo && !w.IsDisabled)
            .ToList();

        foreach (var widget in todoWidgets)
        {
            if (cancellationToken.IsCancellationRequested || recommendations.Count >= 3)
            {
                break;
            }

            try
            {
                var store = new TodoWidgetStore(widget.Id);
                var data = await store.LoadAsync();

                var upcoming = data.Items
                    .Where(i => !i.IsCompleted && i.DueDate.HasValue &&
                                i.DueDate.Value >= DateTimeOffset.Now &&
                                i.DueDate.Value <= DateTimeOffset.Now.AddDays(7))
                    .OrderBy(i => i.DueDate)
                    .Take(3 - recommendations.Count);

                foreach (var item in upcoming)
                {
                    recommendations.Add(new SearchRecommendationItem
                    {
                        Kind = SearchResultKind.Todo,
                        Title = item.Text,
                        Subtitle = $"{_localizationService.T("Search.Todo.Due")}: {item.DueDate!.Value:MM-dd}",
                        Glyph = "\uE9D5",
                        TodoWidgetId = widget.Id,
                        TodoItemId = item.Id
                    });
                }
            }
            catch
            {
                // Skip
            }
        }

        return recommendations;
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
        _quickCaptureService.Changed -= OnQuickCaptureChanged;
        _deskBoxContentLifetimeCts.Cancel();
        _deskBoxContentLifetimeCts.Dispose();
        Volatile.Write(ref _deskBoxContentSnapshot, []);
        Volatile.Write(ref _deskBoxContentInitialized, 0);
        _everythingSearchService.Dispose();
    }
}
