using System.Text.Json;
using DeskBox.Models;

namespace DeskBox.Services;

/// <summary>
/// Persists recent search queries (history) and user-pinned queries (favorites).
/// History is recorded automatically as the user searches; favorites are toggled
/// explicitly and always surface ahead of history in the empty-state view.
/// </summary>
public sealed class SearchHistoryService
{
    private const int MaxHistoryEntries = 20;
    private const int MaxRecentResultEntries = 12;

    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly string _storePath;
    private readonly object _gate = new();
    private PersistedData _data = new();

    public SearchHistoryService()
        : this(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DeskBox",
            "data",
            "search-history.json"))
    {
    }

    internal SearchHistoryService(string storePath)
    {
        _storePath = storePath;
        Load();
    }

    /// <summary>Raised whenever the recent-query list changes (record/clear).</summary>
    public event Action? RecentQueriesChanged;

    /// <summary>Most recent queries, newest first.</summary>
    public IReadOnlyList<string> RecentQueries
    {
        get { lock (_gate) { return [.. _data.Recent]; } }
    }

    /// <summary>User-pinned queries, in pin order.</summary>
    public IReadOnlyList<string> FavoriteQueries
    {
        get { lock (_gate) { return [.. _data.Favorites]; } }
    }

    /// <summary>Results the user actually opened, newest first.</summary>
    public IReadOnlyList<SearchRecommendationItem> RecentResults
    {
        get
        {
            lock (_gate)
            {
                return _data.RecentResults.Select(item => item.ToRecommendation()).ToList();
            }
        }
    }

    /// <summary>
    /// Records a query into history (deduplicated, newest first, capped).
    /// </summary>
    public void RecordQuery(string? query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return;
        }

        string normalized = query.Trim();
        if (normalized.Length == 0)
        {
            return;
        }

        bool changed;
        lock (_gate)
        {
            _data.Recent.RemoveAll(q => string.Equals(q, normalized, StringComparison.OrdinalIgnoreCase));
            _data.Recent.Insert(0, normalized);
            if (_data.Recent.Count > MaxHistoryEntries)
            {
                _data.Recent.RemoveRange(MaxHistoryEntries, _data.Recent.Count - MaxHistoryEntries);
            }

            changed = true;
        }

        if (changed)
        {
            Save();
            RecentQueriesChanged?.Invoke();
        }
    }

    /// <summary>
    /// Records a result only after the user executes it. File modification time is not
    /// treated as usage; this keeps the empty state free of downloads and cache churn.
    /// </summary>
    public void RecordResult(SearchResultItem? item)
    {
        if (item is null || item.Kind is SearchResultKind.Action or SearchResultKind.History or SearchResultKind.Favorite)
        {
            return;
        }

        var stored = PersistedResult.From(item);
        lock (_gate)
        {
            _data.RecentResults.RemoveAll(existing =>
                string.Equals(existing.Identity, stored.Identity, StringComparison.OrdinalIgnoreCase));
            _data.RecentResults.Insert(0, stored);
            if (_data.RecentResults.Count > MaxRecentResultEntries)
            {
                _data.RecentResults.RemoveRange(
                    MaxRecentResultEntries,
                    _data.RecentResults.Count - MaxRecentResultEntries);
            }
        }

        Save();
    }

    /// <summary>
    /// Toggles a query in favorites. Returns true if the query is now a favorite.
    /// </summary>
    public bool ToggleFavorite(string? query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return false;
        }

        string normalized = query.Trim();
        bool isFavorite;
        lock (_gate)
        {
            int existing = _data.Favorites.FindIndex(
                q => string.Equals(q, normalized, StringComparison.OrdinalIgnoreCase));
            if (existing >= 0)
            {
                _data.Favorites.RemoveAt(existing);
                isFavorite = false;
            }
            else
            {
                _data.Favorites.Insert(0, normalized);
                isFavorite = true;
            }
        }

        Save();
        return isFavorite;
    }

    public bool IsFavorite(string? query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return false;
        }

        string normalized = query.Trim();
        lock (_gate)
        {
            return _data.Favorites.Any(
                q => string.Equals(q, normalized, StringComparison.OrdinalIgnoreCase));
        }
    }

    /// <summary>
    /// Clears all recent search history (pinned favorites are preserved) and persists.
    /// </summary>
    public void ClearRecentHistory()
    {
        lock (_gate)
        {
            if (_data.Recent.Count == 0)
            {
                return;
            }

            _data.Recent.Clear();
        }

        Save();
        RecentQueriesChanged?.Invoke();
    }

    public void ClearAllHistory()
    {
        lock (_gate)
        {
            _data.Recent.Clear();
            _data.Favorites.Clear();
            _data.RecentResults.Clear();
        }

        Save();
        RecentQueriesChanged?.Invoke();
    }

    /// <summary>
    /// Clears recent search queries AND cached recent-result cards (the items shown
    /// in the search widget body), while preserving user-pinned favorites. Used by
    /// the search widget's clear button so the user can wipe auto-recorded data
    /// without losing deliberately pinned queries.
    /// </summary>
    public void ClearHistoryAndResults()
    {
        lock (_gate)
        {
            _data.Recent.Clear();
            _data.RecentResults.Clear();
        }

        Save();
        RecentQueriesChanged?.Invoke();
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_storePath))
            {
                return;
            }

            string json = File.ReadAllText(_storePath);
            var data = JsonSerializer.Deserialize<PersistedData>(json, s_jsonOptions);
            if (data is not null)
            {
                data.Recent ??= [];
                data.Favorites ??= [];
                data.RecentResults ??= [];
                _data = data;
            }
        }
        catch (Exception ex)
        {
            App.Log($"[SearchHistory] Failed to load history: {ex.Message}");
        }
    }

    private void Save()
    {
        try
        {
            PersistedData snapshot;
            lock (_gate)
            {
                snapshot = new PersistedData
                {
                    Recent = [.. _data.Recent],
                    Favorites = [.. _data.Favorites],
                    RecentResults = [.. _data.RecentResults]
                };
            }

            string? directory = Path.GetDirectoryName(_storePath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            string json = JsonSerializer.Serialize(snapshot, s_jsonOptions);
            File.WriteAllText(_storePath, json);
        }
        catch (Exception ex)
        {
            App.Log($"[SearchHistory] Failed to save history: {ex.Message}");
        }
    }

    private sealed class PersistedData
    {
        public List<string> Recent { get; set; } = [];
        public List<string> Favorites { get; set; } = [];
        public List<PersistedResult> RecentResults { get; set; } = [];
    }

    private sealed class PersistedResult
    {
        public string Identity { get; set; } = string.Empty;
        public SearchResultKind Kind { get; set; }
        public string Title { get; set; } = string.Empty;
        public string? Subtitle { get; set; }
        public string? DetailPath { get; set; }
        public string? Glyph { get; set; }
        public string? TodoWidgetId { get; set; }
        public string? TodoItemId { get; set; }
        public string? QuickCaptureItemId { get; set; }

        public static PersistedResult From(SearchResultItem item) => new()
        {
            Identity = SearchResultRanker.GetIdentityKey(item),
            Kind = item.Kind,
            Title = item.Title,
            Subtitle = item.Subtitle,
            DetailPath = item.DetailPath,
            Glyph = item.Glyph,
            TodoWidgetId = item.TodoWidgetId,
            TodoItemId = item.TodoItemId,
            QuickCaptureItemId = item.QuickCaptureItemId
        };

        public SearchRecommendationItem ToRecommendation() => new()
        {
            Kind = Kind,
            Title = Title,
            Subtitle = Subtitle,
            DetailPath = DetailPath,
            Glyph = Glyph,
            TodoWidgetId = TodoWidgetId,
            TodoItemId = TodoItemId,
            QuickCaptureItemId = QuickCaptureItemId
        };
    }
}
