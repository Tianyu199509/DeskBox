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

    private static readonly TimeSpan PositiveTtl = TimeSpan.FromDays(30);
    private static readonly TimeSpan NegativeTtl = TimeSpan.FromHours(24);

    private readonly string _cacheDirectory;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly Lock _memoryGate = new();
    private readonly Dictionary<string, LinkedListNode<CachedLyricsEntry>> _memoryIndex = new(StringComparer.Ordinal);
    private readonly LinkedList<CachedLyricsEntry> _memoryLru = new();
    private int _writesSincePrune;

    public LyricsCacheStore(string? cacheDirectory = null, Func<DateTimeOffset>? utcNow = null)
    {
        _cacheDirectory = string.IsNullOrWhiteSpace(cacheDirectory)
            ? Path.Combine(DeskBoxDataPathService.Current.RootPath, "cache", "lyrics")
            : cacheDirectory;
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
    }

    public static string ComputeCacheKey(string? title, string? artist)
    {
        string identity = LyricTrackMatcher.BuildNormalizedCacheIdentity(title, artist);
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(identity));
        return Convert.ToHexStringLower(hash)[..32];
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

        return age <= ttl ? raw : null;
    }

    public async Task<int> GetSavedUserOffsetMsAsync(
        string cacheKey,
        CancellationToken cancellationToken = default)
    {
        CachedLyricsEntry? raw = await TryGetRawAsync(cacheKey, cancellationToken).ConfigureAwait(false);
        return raw?.UserOffsetMs ?? 0;
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
        CachedLyricsEntry? existing = await TryGetRawAsync(cacheKey, cancellationToken).ConfigureAwait(false);
        int nextOffset = reset
            ? 0
            : Math.Clamp((existing?.UserOffsetMs ?? 0) + deltaMs, -30_000, 30_000);

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
