using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DeskBox.Platform;

namespace DeskBox.Services.Lyrics;

/// <summary>
/// Two-tier (20-entry in-memory LRU + bounded disk JSON) cache for resolved lyrics and per-song timing offsets.
/// </summary>
public sealed class LyricsCacheStore
{
    public const int MaxMemoryEntries = 20;
    public const int MaxDiskFiles = 200;
    public const long MaxDiskBytes = 5L * 1024 * 1024;
    public const string OffsetsFileName = "music-lyrics-offsets.json";

    private static readonly TimeSpan PositiveTtl = TimeSpan.FromDays(30);
    private static readonly TimeSpan NegativeTtl = TimeSpan.FromHours(24);

    private readonly string _cacheDirectory;
    private readonly string _offsetsFilePath;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly Lock _memoryGate = new();
    private readonly SemaphoreSlim _offsetsGate = new(1, 1);
    private readonly Dictionary<string, LinkedListNode<CachedLyricsEntry>> _memoryIndex = new(StringComparer.Ordinal);
    private readonly LinkedList<CachedLyricsEntry> _memoryLru = new();
    private Dictionary<string, int>? _offsetsCache;
    private int _writesSincePrune;

    public LyricsCacheStore(
        string? cacheDirectory = null,
        Func<DateTimeOffset>? utcNow = null,
        string? offsetsFilePath = null)
    {
        bool useDefaultCacheDir = string.IsNullOrWhiteSpace(cacheDirectory);
        _cacheDirectory = useDefaultCacheDir
            ? Path.Combine(DeskBoxDataPathService.Current.RootPath, "cache", "lyrics")
            : cacheDirectory!;
        _offsetsFilePath = !string.IsNullOrWhiteSpace(offsetsFilePath)
            ? offsetsFilePath!
            : useDefaultCacheDir
                ? Path.Combine(DeskBoxDataPathService.Current.DataDirectory, OffsetsFileName)
                : ResolveDefaultOffsetsPathForCustomCacheDir(_cacheDirectory);
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
    }

    internal string OffsetsFilePath => _offsetsFilePath;

    private static string ResolveDefaultOffsetsPathForCustomCacheDir(string customCacheDirectory)
    {
        string normalized = Path.GetFullPath(customCacheDirectory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string leaf = Path.GetFileName(normalized);
        string? parent = Path.GetDirectoryName(normalized);
        if (string.Equals(leaf, "lyrics", StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(parent) &&
            string.Equals(Path.GetFileName(parent), "cache", StringComparison.OrdinalIgnoreCase))
        {
            string? root = Path.GetDirectoryName(parent);
            if (!string.IsNullOrWhiteSpace(root))
            {
                return Path.Combine(root, "data", OffsetsFileName);
            }
        }

        return Path.Combine(parent ?? normalized, OffsetsFileName);
    }

    public static string ComputeCacheKey(string? title, string? artist)
    {
        string identity = LyricTrackMatcher.BuildNormalizedCacheIdentity(title, artist);
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(identity));
        return Convert.ToHexStringLower(hash)[..32];
    }

    public void ClearMemoryCache()
    {
        lock (_memoryGate)
        {
            _memoryLru.Clear();
            _memoryIndex.Clear();
        }

        _offsetsGate.Wait();
        try
        {
            _offsetsCache = null;
        }
        finally
        {
            _offsetsGate.Release();
        }
    }

    public async Task<CachedLyricsEntry?> TryGetValidAsync(
        string cacheKey,
        CancellationToken cancellationToken = default)
    {
        CachedLyricsEntry? raw = await TryGetRawAsync(cacheKey, cancellationToken).ConfigureAwait(false);
        if (raw is null)
        {
            return null;
        }

        DateTimeOffset now = _utcNow();
        DateTimeOffset updated = new(Math.Clamp(raw.UpdatedUtcTicks, DateTimeOffset.MinValue.Ticks, DateTimeOffset.MaxValue.Ticks), TimeSpan.Zero);
        TimeSpan age = now - updated;
        TimeSpan ttl = string.Equals(raw.Status, CachedLyricsStatus.NotFound, StringComparison.Ordinal)
            ? NegativeTtl
            : PositiveTtl;
        if (age > ttl)
        {
            return null;
        }

        int durableOffset = await GetSavedUserOffsetMsAsync(cacheKey, cancellationToken).ConfigureAwait(false);
        if (raw.UserOffsetMs != durableOffset)
        {
            raw = new CachedLyricsEntry
            {
                CacheKey = raw.CacheKey,
                Status = raw.Status,
                SyncedLyrics = raw.SyncedLyrics,
                DurationSeconds = raw.DurationSeconds,
                UserOffsetMs = durableOffset,
                UpdatedUtcTicks = raw.UpdatedUtcTicks
            };
            StoreInMemory(raw);
        }

        return raw;
    }

    public async Task<int> GetSavedUserOffsetMsAsync(
        string cacheKey,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(cacheKey))
        {
            return 0;
        }

        await _offsetsGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Dictionary<string, int> offsets = await EnsureOffsetsLoadedLockedAsync(cancellationToken).ConfigureAwait(false);
            if (offsets.TryGetValue(cacheKey, out int savedOffset))
            {
                return savedOffset;
            }
        }
        finally
        {
            _offsetsGate.Release();
        }

        // Seamless migration fallback from pre-1.6.0 cache entries.
        CachedLyricsEntry? raw = await TryGetRawAsync(cacheKey, cancellationToken).ConfigureAwait(false);
        if (raw is { UserOffsetMs: not 0 })
        {
            await PersistSavedUserOffsetAsync(cacheKey, raw.UserOffsetMs, cancellationToken).ConfigureAwait(false);
            return raw.UserOffsetMs;
        }

        return 0;
    }

    public async Task PutAsync(
        CachedLyricsEntry entry,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(entry.CacheKey))
        {
            return;
        }

        if (entry.UpdatedUtcTicks <= 0)
        {
            entry.UpdatedUtcTicks = _utcNow().UtcTicks;
        }

        if (entry.UserOffsetMs != 0)
        {
            await PersistSavedUserOffsetAsync(entry.CacheKey, entry.UserOffsetMs, cancellationToken)
                .ConfigureAwait(false);
        }

        StoreInMemory(entry);

        try
        {
            Directory.CreateDirectory(_cacheDirectory);
            string filePath = GetEntryFilePath(entry.CacheKey);
            string tempPath = $"{filePath}.{Guid.NewGuid():N}.tmp";
            bool moved = false;
            try
            {
                string json = JsonSerializer.Serialize(entry, LyricsJsonContext.Default.CachedLyricsEntry);
                await File.WriteAllTextAsync(tempPath, json, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
                LyricsCacheFileOps.ReplaceCacheFileAtomically(tempPath, filePath);
                moved = true;
            }
            finally
            {
                if (!moved)
                {
                    TryDeleteTempFile(tempPath);
                }
            }

            if (Interlocked.Increment(ref _writesSincePrune) % 10 == 1)
            {
                PruneDiskCacheIfNeeded();
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // Cache persistence is best-effort; never fail playback UI.
        }
    }

    internal void PutMemoryOnly(CachedLyricsEntry entry)
    {
        if (string.IsNullOrWhiteSpace(entry.CacheKey))
        {
            return;
        }

        if (entry.UpdatedUtcTicks <= 0)
        {
            entry.UpdatedUtcTicks = _utcNow().UtcTicks;
        }

        StoreInMemory(entry);
    }

    internal void RemoveNegativeEntryFromMemory(string cacheKey)
    {
        if (string.IsNullOrWhiteSpace(cacheKey))
        {
            return;
        }

        lock (_memoryGate)
        {
            if (_memoryIndex.TryGetValue(cacheKey, out LinkedListNode<CachedLyricsEntry>? node) &&
                string.Equals(node.Value.Status, CachedLyricsStatus.NotFound, StringComparison.Ordinal))
            {
                _memoryLru.Remove(node);
                _memoryIndex.Remove(cacheKey);
            }
        }
    }

    private static void TryDeleteTempFile(string tempPath)
    {
        try
        {
            var tempFile = new FileInfo(tempPath);
            if (tempFile.Exists)
            {
                tempFile.Delete();
            }
        }
        catch
        {
            // Best-effort cleanup of abandoned temp file.
        }
    }

    public async Task<int> UpdateUserOffsetAsync(
        string cacheKey,
        int deltaMs,
        bool reset = false,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(cacheKey))
        {
            return 0;
        }

        int currentOffset = await GetSavedUserOffsetMsAsync(cacheKey, cancellationToken).ConfigureAwait(false);
        int nextOffset = reset
            ? 0
            : Math.Clamp(currentOffset + deltaMs, -30_000, 30_000);

        await PersistSavedUserOffsetAsync(cacheKey, nextOffset, cancellationToken).ConfigureAwait(false);

        CachedLyricsEntry? existing = await TryGetRawAsync(cacheKey, cancellationToken).ConfigureAwait(false);
        CachedLyricsEntry updated = existing is not null
            ? new CachedLyricsEntry
            {
                CacheKey = cacheKey,
                Status = existing.Status,
                SyncedLyrics = existing.SyncedLyrics,
                DurationSeconds = existing.DurationSeconds,
                UserOffsetMs = nextOffset,
                UpdatedUtcTicks = existing.UpdatedUtcTicks
            }
            : new CachedLyricsEntry
            {
                CacheKey = cacheKey,
                Status = CachedLyricsStatus.NotFound,
                UserOffsetMs = nextOffset,
                UpdatedUtcTicks = _utcNow().UtcTicks
            };

        await PutAsync(updated, cancellationToken).ConfigureAwait(false);
        return nextOffset;
    }

    private async Task<Dictionary<string, int>> EnsureOffsetsLoadedLockedAsync(
        CancellationToken cancellationToken)
    {
        if (_offsetsCache is not null)
        {
            return _offsetsCache;
        }

        var map = new Dictionary<string, int>(StringComparer.Ordinal);
        if (File.Exists(_offsetsFilePath))
        {
            try
            {
                string json = await File.ReadAllTextAsync(_offsetsFilePath, Encoding.UTF8, cancellationToken)
                    .ConfigureAwait(false);
                Dictionary<string, int>? parsed = JsonSerializer.Deserialize(
                    json,
                    LyricsJsonContext.Default.DictionaryStringInt32);
                if (parsed is not null)
                {
                    foreach (KeyValuePair<string, int> kv in parsed)
                    {
                        if (!string.IsNullOrWhiteSpace(kv.Key) && kv.Value != 0)
                        {
                            map[kv.Key] = Math.Clamp(kv.Value, -30_000, 30_000);
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                // Corrupt or unreadable offsets file falls back to empty dictionary.
            }
        }

        _offsetsCache = map;
        return map;
    }

    private async Task PersistSavedUserOffsetAsync(
        string cacheKey,
        int offsetMs,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(cacheKey))
        {
            return;
        }

        await _offsetsGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Dictionary<string, int> offsets = await EnsureOffsetsLoadedLockedAsync(cancellationToken)
                .ConfigureAwait(false);
            bool changed;
            if (offsetMs == 0)
            {
                changed = offsets.Remove(cacheKey);
            }
            else
            {
                int clamped = Math.Clamp(offsetMs, -30_000, 30_000);
                changed = !offsets.TryGetValue(cacheKey, out int prev) || prev != clamped;
                offsets[cacheKey] = clamped;
            }

            if (!changed)
            {
                return;
            }

            try
            {
                if (offsets.Count == 0)
                {
                    if (File.Exists(_offsetsFilePath))
                    {
                        LyricsCacheFileOps.DeleteCacheFile(_offsetsFilePath);
                    }

                    return;
                }

                string? dir = Path.GetDirectoryName(_offsetsFilePath);
                if (!string.IsNullOrWhiteSpace(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                string tempPath = $"{_offsetsFilePath}.{Guid.NewGuid():N}.tmp";
                bool moved = false;
                try
                {
                    string json = JsonSerializer.Serialize(
                        offsets,
                        LyricsJsonContext.Default.DictionaryStringInt32);
                    await File.WriteAllTextAsync(tempPath, json, Encoding.UTF8, cancellationToken)
                        .ConfigureAwait(false);
                    LyricsCacheFileOps.ReplaceCacheFileAtomically(tempPath, _offsetsFilePath);
                    moved = true;
                }
                finally
                {
                    if (!moved)
                    {
                        TryDeleteTempFile(tempPath);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                // Durable offset write is best-effort; in-memory _offsetsCache still holds it for this session.
            }
        }
        finally
        {
            _offsetsGate.Release();
        }
    }

    public void PruneDiskCacheIfNeeded()
    {
        try
        {
            if (!Directory.Exists(_cacheDirectory))
            {
                return;
            }

            FileInfo[] files = new DirectoryInfo(_cacheDirectory).GetFiles("*.json");
            long totalBytes = 0;
            for (int i = 0; i < files.Length; i++)
            {
                totalBytes += files[i].Length;
            }

            if (files.Length <= MaxDiskFiles && totalBytes <= MaxDiskBytes)
            {
                return;
            }

            Array.Sort(files, static (a, b) => a.LastWriteTimeUtc.CompareTo(b.LastWriteTimeUtc));
            int remainingFiles = files.Length;

            for (int i = 0; i < files.Length && (remainingFiles > MaxDiskFiles || totalBytes > MaxDiskBytes); i++)
            {
                try
                {
                    long len = files[i].Length;
                    LyricsCacheFileOps.DeleteCacheFile(files[i].FullName);
                    totalBytes -= len;
                    remainingFiles--;
                }
                catch
                {
                    // Ignore locked file during opportunistic eviction.
                }
            }
        }
        catch
        {
            // Ignore directory enumeration failures.
        }
    }

    private async Task<CachedLyricsEntry?> TryGetRawAsync(
        string cacheKey,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(cacheKey))
        {
            return null;
        }

        lock (_memoryGate)
        {
            if (_memoryIndex.TryGetValue(cacheKey, out LinkedListNode<CachedLyricsEntry>? node))
            {
                _memoryLru.Remove(node);
                _memoryLru.AddFirst(node);
                return node.Value;
            }
        }

        string filePath = GetEntryFilePath(cacheKey);
        if (!File.Exists(filePath))
        {
            return null;
        }

        try
        {
            string json = await File.ReadAllTextAsync(filePath, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
            CachedLyricsEntry? entry = JsonSerializer.Deserialize(json, LyricsJsonContext.Default.CachedLyricsEntry);
            if (entry is not null)
            {
                StoreInMemory(entry);
            }

            return entry;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    private void StoreInMemory(CachedLyricsEntry entry)
    {
        lock (_memoryGate)
        {
            if (_memoryIndex.TryGetValue(entry.CacheKey, out LinkedListNode<CachedLyricsEntry>? existing))
            {
                existing.Value = entry;
                _memoryLru.Remove(existing);
                _memoryLru.AddFirst(existing);
                return;
            }

            var node = new LinkedListNode<CachedLyricsEntry>(entry);
            _memoryLru.AddFirst(node);
            _memoryIndex[entry.CacheKey] = node;

            while (_memoryLru.Count > MaxMemoryEntries && _memoryLru.Last is { } tail)
            {
                _memoryIndex.Remove(tail.Value.CacheKey);
                _memoryLru.RemoveLast();
            }
        }
    }

    private string GetEntryFilePath(string cacheKey) =>
        Path.Combine(_cacheDirectory, cacheKey + ".json");
}
