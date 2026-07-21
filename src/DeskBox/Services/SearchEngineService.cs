using System.Diagnostics;
using DeskBox.Models;

namespace DeskBox.Services;

/// <summary>
/// Coordinates search across all layers: DeskBox internal data, custom file index,
/// and (future) Windows Search Index.
/// </summary>
public sealed class SearchEngineService : IDisposable
{
    private readonly SettingsService _settingsService;
    private readonly LocalizationService _localizationService;
    private readonly SearchIndexService _indexService;
    private readonly WindowsIndexSearchService _windowsIndexService;
    private readonly UsnJournalIndexService? _usnIndexService;
    private bool _isDisposed;

    public SearchEngineService(
        SettingsService settingsService,
        LocalizationService localizationService,
        SearchIndexService indexService,
        WindowsIndexSearchService windowsIndexService,
        UsnJournalIndexService? usnIndexService = null)
    {
        _settingsService = settingsService;
        _localizationService = localizationService;
        _indexService = indexService;
        _windowsIndexService = windowsIndexService;
        _usnIndexService = usnIndexService;
        _indexService.IndexUpdated += OnIndexUpdated;
        if (_usnIndexService is not null)
        {
            _usnIndexService.IndexUpdated += OnIndexUpdated;
        }
    }

    public SearchIndexService IndexService => _indexService;

    public int IndexedItemCount => _usnIndexService is { IsAvailable: true }
        ? _usnIndexService.EntryCount
        : _indexService.EntryCount;

    public bool IsCustomIndexing => _indexService.IsScanning ||
                                    _usnIndexService is { IsScanning: true };

    public event Action? IndexUpdated;

    private void OnIndexUpdated() => IndexUpdated?.Invoke();

    public void SetCustomIndexingEnabled(bool enabled)
    {
        if (enabled)
        {
            _indexService.StartIndexing();
            _usnIndexService?.StartIndexing();
        }
        else
        {
            _indexService.StopIndexing();
            _usnIndexService?.StopIndexing();
        }
    }

    /// <summary>
    /// Performs a unified search across all enabled layers.
    /// </summary>
    public async Task<SearchResponse> SearchAsync(string query, CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        var settings = _settingsService.Settings;
        int maxResults = Math.Clamp(settings.SearchMaxResults, 10, 200);

        var providerTasks = new List<Task<IReadOnlyList<SearchResultItem>>>();

        // Start every enabled provider together. DeskBox content normally wins the
        // first-result race, while system and full-disk providers complete in parallel.
        if (settings.SearchIncludeDeskBoxContent)
        {
            providerTasks.Add(SearchDeskBoxContentAsync(query, maxResults, cancellationToken));
        }

        providerTasks.Add(Task.FromResult(SearchActions(query)));

        // Layer 2: Windows Search Index (system-indexed locations)
        if (settings.SearchIncludeSystemIndex)
        {
            providerTasks.Add(_windowsIndexService.SearchAsync(query, maxResults, cancellationToken));
        }

        // Layer 3: File index. Prefer the USN journal full-disk index when it is
        // available (elevated); otherwise fall back to the directory-scan index, which
        // now covers every fixed drive so coverage stays broad without admin.
        if (settings.SearchCustomIndexerEnabled)
        {
            providerTasks.Add(_usnIndexService is { IsAvailable: true }
                ? Task.Run(() => _usnIndexService.Search(query, maxResults), cancellationToken)
                : Task.Run(() => _indexService.Search(query, maxResults), cancellationToken));
        }

        IReadOnlyList<SearchResultItem>[] providerResults = await Task.WhenAll(providerTasks);
        cancellationToken.ThrowIfCancellationRequested();

        var rankedItems = SearchResultRanker.MergeAndRank(
            providerResults.SelectMany(items => items),
            query.Trim(),
            maxResults);
        var groups = BuildGroups(rankedItems);
        stopwatch.Stop();

        return new SearchResponse
        {
            Query = query,
            RankedItems = rankedItems,
            Groups = groups,
            TotalResultCount = rankedItems.Count,
            Elapsed = stopwatch.Elapsed,
            IsComplete = true
        };
    }

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
        if (!Directory.Exists(root))
        {
            yield break;
        }

        var pending = new Stack<string>();
        pending.Push(root);

        while (pending.Count > 0 && !cancellationToken.IsCancellationRequested)
        {
            string current = pending.Pop();
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
        string query, int maxResults, CancellationToken cancellationToken)
    {
        var results = new List<SearchResultItem>();

        var todoTask = SearchTodosAsync(query, maxResults / 2, cancellationToken);
        var noteTask = SearchQuickCaptureAsync(query, maxResults / 2, cancellationToken);
        await Task.WhenAll(todoTask, noteTask);
        results.AddRange(await todoTask);
        results.AddRange(await noteTask);

        return results;
    }

    private async Task<IReadOnlyList<SearchResultItem>> SearchTodosAsync(
        string query, int maxResults, CancellationToken cancellationToken)
    {
        var results = new List<SearchResultItem>();
        var settings = _settingsService.Settings;

        var todoWidgets = settings.Widgets
            .Where(w => w.WidgetKind == WidgetKind.Todo && !w.IsDisabled)
            .ToList();

        foreach (var widget in todoWidgets)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            try
            {
                var store = new TodoWidgetStore(widget.Id);
                var data = await store.LoadAsync();

                foreach (var item in data.Items)
                {
                    if (results.Count >= maxResults)
                    {
                        break;
                    }

                    bool matches = item.Text.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                                   (item.Notes?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false);

                    if (!matches)
                    {
                        continue;
                    }

                    double score = ComputeTextRelevance(item.Text, query);
                    results.Add(new SearchResultItem
                    {
                        Kind = SearchResultKind.Todo,
                        Title = item.Text,
                        Subtitle = item.DueDate.HasValue
                            ? $"{_localizationService.T("Search.Todo.Due")}: {item.DueDate.Value:yyyy-MM-dd}"
                            : widget.Name,
                        TodoWidgetId = widget.Id,
                        TodoItemId = item.Id,
                        TodoIsCompleted = item.IsCompleted,
                        Glyph = "\uE9D5",
                        RelevanceScore = score + (item.IsCompleted ? -20 : 10)
                    });
                }
            }
            catch
            {
                // Skip widgets that fail to load
            }
        }

        return results.OrderByDescending(r => r.RelevanceScore).Take(maxResults).ToList();
    }

    private async Task<IReadOnlyList<SearchResultItem>> SearchQuickCaptureAsync(
        string query, int maxResults, CancellationToken cancellationToken)
    {
        var results = new List<SearchResultItem>();

        try
        {
            var store = new QuickCaptureStore();
            var data = await store.LoadAsync();

            foreach (var item in data.Items)
            {
                if (results.Count >= maxResults)
                {
                    break;
                }

                if (item.IsDeleted)
                {
                    continue;
                }

                bool matches = item.Body.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                               (item.Title?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false) ||
                               (item.Url?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false);

                if (!matches)
                {
                    continue;
                }

                string displayTitle = !string.IsNullOrWhiteSpace(item.Title)
                    ? item.Title
                    : TruncateText(item.Body, 60);

                double score = ComputeTextRelevance(displayTitle, query);
                results.Add(new SearchResultItem
                {
                    Kind = SearchResultKind.QuickCapture,
                    Title = displayTitle,
                    Subtitle = item.Type.ToString(),
                    QuickCaptureItemId = item.Id,
                    Glyph = "\uE70F",
                    ModifiedAt = item.UpdatedAt,
                    RelevanceScore = score + (item.IsPinned ? 5 : 0)
                });
            }
        }
        catch
        {
            // Skip if QuickCapture data fails to load
        }

        return results.OrderByDescending(r => r.RelevanceScore).Take(maxResults).ToList();
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

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        _indexService.IndexUpdated -= OnIndexUpdated;
        if (_usnIndexService is not null)
        {
            _usnIndexService.IndexUpdated -= OnIndexUpdated;
        }
        _indexService.Dispose();
    }
}
