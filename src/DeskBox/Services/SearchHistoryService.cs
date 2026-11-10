using System.Text.Json;
using System.Text.Json.Serialization;
using DeskBox.Models;

namespace DeskBox.Services;

/// <summary>
/// Persists recent search queries (history) and user-pinned queries (favorites).
/// History is recorded automatically as the user searches; favorites are toggled
/// explicitly and always surface ahead of history in the empty-state view.
/// </summary>
public sealed partial class SearchHistoryService
{
    private const int MaxHistoryEntries = 20;
    private const int MaxRecentResultEntries = 12;
    private const int MaxFrecencyPoolEntries = 48;

    private readonly string _storePath;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private long _saveVersion;
    private long _flushedVersion;
    private PersistedData _data = new();

    public SearchHistoryService()
        : this(Path.Combine(
            DeskBoxDataPathService.Current.DataDirectory,
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
                return _data.RecentResults
                    .Take(MaxRecentResultEntries)
                    .Select(item => item.ToRecommendation())
                    .ToList();
            }
        }
    }

    /// <summary>
    /// Computes a lightweight Frecency (frequency × recency) score boost (0..32)
    /// for a candidate result identity based on user launches in this workspace.
    /// </summary>
    public double GetFrecencyBoost(string? identityKey) =>
        GetFrecencyBoost(identityKey, DateTimeOffset.UtcNow);

    internal double GetFrecencyBoost(string? identityKey, DateTimeOffset nowUtc)
    {
        if (string.IsNullOrWhiteSpace(identityKey))
        {
            return 0;
        }

        int useCount;
        long lastUsedUtcTicks;
        int rankIndex;
        lock (_gate)
        {
            rankIndex = _data.RecentResults.FindIndex(existing =>
                string.Equals(existing.Identity, identityKey, StringComparison.OrdinalIgnoreCase));
            if (rankIndex < 0)
            {
                return 0;
            }

            PersistedResult matched = _data.RecentResults[rankIndex];
            useCount = Math.Max(1, matched.UseCount);
            lastUsedUtcTicks = matched.LastUsedUtcTicks;
        }

        double frequencyFactor = Math.Min(16.0, 5.0 + (Math.Log2(useCount) * 4.5));
        double recencyMultiplier;
        if (lastUsedUtcTicks > 0)
        {
            double ageHours = Math.Max(0, (nowUtc.UtcTicks - lastUsedUtcTicks) / (double)TimeSpan.TicksPerHour);
            recencyMultiplier = ageHours switch
            {
                <= 4.0 => 2.0,
                <= 24.0 => 1.65,
                <= 72.0 => 1.35,
                <= 168.0 => 1.1,
                <= 720.0 => 0.8,
                _ => 0.5
            };
        }
        else
        {
            // Legacy records without timestamps fall back to ordinal position decay.
            recencyMultiplier = Math.Max(0.6, 1.5 - (rankIndex * 0.08));
        }

        return Math.Clamp(frequencyFactor * recencyMultiplier, 0.0, 32.0);
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
    /// Clipboard items are excluded so copied secrets or transient text never leak into search-history.json.
    /// </summary>
    public void RecordResult(SearchResultItem? item)
    {
        if (item is null ||
            item.Kind is SearchResultKind.Action or
                         SearchResultKind.History or
                         SearchResultKind.Favorite or
                         SearchResultKind.Clipboard)
        {
            return;
        }

        var stored = PersistedResult.From(item);
        long nowTicks = DateTimeOffset.UtcNow.UtcTicks;
        lock (_gate)
        {
            int existingIndex = _data.RecentResults.FindIndex(existing =>
                string.Equals(existing.Identity, stored.Identity, StringComparison.OrdinalIgnoreCase));
            int nextUseCount = 1;
            if (existingIndex >= 0)
            {
                nextUseCount = Math.Min(10_000, Math.Max(1, _data.RecentResults[existingIndex].UseCount) + 1);
                _data.RecentResults.RemoveAt(existingIndex);
            }

            stored.UseCount = nextUseCount;
            stored.LastUsedUtcTicks = nowTicks;
            _data.RecentResults.Insert(0, stored);
            if (_data.RecentResults.Count > MaxFrecencyPoolEntries)
            {
                _data.RecentResults.RemoveRange(
                    MaxFrecencyPoolEntries,
                    _data.RecentResults.Count - MaxFrecencyPoolEntries);
            }
        }

        Save();
    }

    /// <summary>
    /// Removes one query from recent history while preserving pinned favorites and
    /// recently-opened result cards.
    /// </summary>
    public bool RemoveRecentQuery(string? query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return false;
        }

        bool changed;
        lock (_gate)
        {
            changed = _data.Recent.RemoveAll(
                existing => string.Equals(existing, query.Trim(), StringComparison.OrdinalIgnoreCase)) > 0;
        }

        if (!changed)
        {
            return false;
        }

        Save();
        RecentQueriesChanged?.Invoke();
        return true;
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

    private void Load()
    {
        try
        {
            if (!File.Exists(_storePath))
            {
                return;
            }

            using var stream = new FileStream(
                _storePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            PersistedData? data = JsonSerializer.Deserialize(
                stream,
                SearchHistoryJsonContext.Default.PersistedData);
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
        long version = Interlocked.Increment(ref _saveVersion);
        if (TryShouldOffloadSaveFromUiThread())
        {
            _ = Task.Run(() => FlushPendingSaveAsync(version));
            return;
        }

        _writeGate.Wait();
        try
        {
            if (version < Volatile.Read(ref _flushedVersion))
            {
                return;
            }

            long latestVersion = Volatile.Read(ref _saveVersion);
            PersistHistoryToDisk(CaptureSnapshot());
            Volatile.Write(ref _flushedVersion, latestVersion);
        }
        catch (Exception ex)
        {
            App.Log($"[SearchHistory] Failed to save history: {ex.Message}");
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private static bool TryShouldOffloadSaveFromUiThread()
    {
        try
        {
            return Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread() is not null;
        }
        catch
        {
            return false;
        }
    }

    private async Task FlushPendingSaveAsync(long requestedVersion)
    {
        await _writeGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (requestedVersion <= Volatile.Read(ref _flushedVersion))
            {
                return;
            }

            long latestVersion = Volatile.Read(ref _saveVersion);
            PersistHistoryToDisk(CaptureSnapshot());
            Volatile.Write(ref _flushedVersion, latestVersion);
        }
        catch (Exception ex)
        {
            App.Log($"[SearchHistory] Failed to save history: {ex.Message}");
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private PersistedData CaptureSnapshot()
    {
        lock (_gate)
        {
            return new PersistedData
            {
                Recent = [.. _data.Recent],
                Favorites = [.. _data.Favorites],
                RecentResults = [.. _data.RecentResults]
            };
        }
    }

    private void PersistHistoryToDisk(PersistedData snapshot)
    {
        string? directory = Path.GetDirectoryName(_storePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        string tempPath = $"{_storePath}.{Guid.NewGuid():N}.tmp";
        try
        {
            using (var stream = new FileStream(
                       tempPath,
                       FileMode.Create,
                       FileAccess.Write,
                       FileShare.None,
                       bufferSize: 4096,
                       FileOptions.SequentialScan))
            {
                JsonSerializer.Serialize(
                    stream,
                    snapshot,
                    SearchHistoryJsonContext.Default.PersistedData);
            }

            DeskBox.Platform.SearchHistoryFileOps.ReplaceStoreFileAtomically(tempPath, _storePath);
        }
        finally
        {
            try
            {
                if (File.Exists(tempPath))
                {
                    DeskBox.Platform.SearchHistoryFileOps.TryDeleteTempFile(tempPath);
                }
            }
            catch
            {
            }
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
        public int UseCount { get; set; } = 1;
        public long LastUsedUtcTicks { get; set; }

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

    [JsonSourceGenerationOptions(
        GenerationMode = JsonSourceGenerationMode.Metadata,
        PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
        WriteIndented = true)]
    [JsonSerializable(typeof(PersistedData), TypeInfoPropertyName = "PersistedData")]
    private sealed partial class SearchHistoryJsonContext : JsonSerializerContext
    {
    }
}
