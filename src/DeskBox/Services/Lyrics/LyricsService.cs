namespace DeskBox.Services.Lyrics;

/// <summary>
/// Orchestrates local <c>.lrc</c> files, two-tier cache, and public LRCLIB lookup without background polling.
/// Identical across Microsoft Store and open-source Direct builds.
/// </summary>
public sealed class LyricsService
{
    public static LyricsService Shared { get; } = new();

    private readonly LyricsCacheStore _cacheStore;
    private readonly LrclibLyricsProvider _lrclibProvider;

    public LyricsService(
        LyricsCacheStore? cacheStore = null,
        LrclibLyricsProvider? lrclibProvider = null)
    {
        _cacheStore = cacheStore ?? new LyricsCacheStore();
        _lrclibProvider = lrclibProvider ?? new LrclibLyricsProvider();
    }

    public async Task<LrcDocument> ResolveLyricsAsync(
        LyricsQuery query,
        string? localLyricsFolder,
        CancellationToken cancellationToken = default)
    {
        if (!query.IsValid)
        {
            return LrcDocument.Empty;
        }

        string cacheKey = LyricsCacheStore.ComputeCacheKey(query.Title, query.Artist);
        int userOffsetMs = await _cacheStore.GetSavedUserOffsetMsAsync(cacheKey, cancellationToken)
            .ConfigureAwait(false);

        // 1. User-configured local .lrc folder always wins so edits to local files apply immediately.
        string? localLrc = await LocalFileLyricsProvider
            .TryReadLocalLrcAsync(query, localLyricsFolder, cancellationToken)
            .ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(localLrc))
        {
            LrcDocument parsedLocal = LrcParser.Parse(localLrc, query.Duration, userOffsetMs);
            if (parsedLocal.HasSyncedLines || parsedLocal.IsInstrumental)
            {
                return parsedLocal;
            }
        }

        // 2. Two-tier cache (in-memory LRU + bounded disk JSON).
        CachedLyricsEntry? cached = await _cacheStore.TryGetValidAsync(cacheKey, cancellationToken)
            .ConfigureAwait(false);
        if (cached is not null)
        {
            bool staleOrUntrustedNegative =
                string.Equals(cached.Status, CachedLyricsStatus.NotFound, StringComparison.Ordinal) &&
                query.Duration.TotalSeconds > 5.0 &&
                (cached.DurationSeconds <= 5.0 ||
                 Math.Abs(cached.DurationSeconds - query.Duration.TotalSeconds) > 5.0);

            if (!staleOrUntrustedNegative)
            {
                TimeSpan? cachedDuration = cached.DurationSeconds > 0
                    ? TimeSpan.FromSeconds(cached.DurationSeconds)
                    : query.Duration;

                return cached.Status switch
                {
                    CachedLyricsStatus.Synced when !string.IsNullOrWhiteSpace(cached.SyncedLyrics) =>
                        LrcParser.Parse(cached.SyncedLyrics, cachedDuration, cached.UserOffsetMs),
                    CachedLyricsStatus.Instrumental =>
                        new LrcDocument([], isInstrumental: true, cachedDuration, embeddedOffsetMs: 0, cached.UserOffsetMs),
                    _ => LrcDocument.Empty
                };
            }
        }

        // 3. Official public LRCLIB lookup.
        (LrclibTrackDto? match, bool fetchSucceeded) = await _lrclibProvider
            .SearchBestMatchAsync(query, cancellationToken)
            .ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        if (!fetchSucceeded)
        {
            return LrcDocument.Empty;
        }

        if (match is null)
        {
            var notFoundEntry = new CachedLyricsEntry
            {
                CacheKey = cacheKey,
                Status = CachedLyricsStatus.NotFound,
                DurationSeconds = query.Duration.TotalSeconds > 0 ? query.Duration.TotalSeconds : 0,
                UserOffsetMs = userOffsetMs
            };

            if (query.Duration <= TimeSpan.Zero)
            {
                _cacheStore.PutMemoryOnly(notFoundEntry);
            }
            else
            {
                await _cacheStore.PutAsync(notFoundEntry, cancellationToken).ConfigureAwait(false);
            }

            return LrcDocument.Empty;
        }

        TimeSpan? resolvedDuration = match.Duration > 0
            ? TimeSpan.FromSeconds(match.Duration)
            : (query.Duration.TotalSeconds > 0 ? query.Duration : null);

        if (!string.IsNullOrWhiteSpace(match.SyncedLyrics))
        {
            LrcDocument parsed = LrcParser.Parse(match.SyncedLyrics, resolvedDuration, userOffsetMs);
            if (parsed.HasSyncedLines || parsed.IsInstrumental)
            {
                await _cacheStore.PutAsync(
                    new CachedLyricsEntry
                    {
                        CacheKey = cacheKey,
                        Status = parsed.IsInstrumental ? CachedLyricsStatus.Instrumental : CachedLyricsStatus.Synced,
                        SyncedLyrics = parsed.IsInstrumental ? null : match.SyncedLyrics,
                        DurationSeconds = resolvedDuration?.TotalSeconds ?? 0,
                        UserOffsetMs = userOffsetMs
                    },
                    cancellationToken).ConfigureAwait(false);
                return parsed;
            }
        }

        if (match.Instrumental)
        {
            await _cacheStore.PutAsync(
                new CachedLyricsEntry
                {
                    CacheKey = cacheKey,
                    Status = CachedLyricsStatus.Instrumental,
                    DurationSeconds = resolvedDuration?.TotalSeconds ?? 0,
                    UserOffsetMs = userOffsetMs
                },
                cancellationToken).ConfigureAwait(false);
            return new LrcDocument([], isInstrumental: true, resolvedDuration, embeddedOffsetMs: 0, userOffsetMs);
        }

        await _cacheStore.PutAsync(
            new CachedLyricsEntry
            {
                CacheKey = cacheKey,
                Status = CachedLyricsStatus.NotFound,
                DurationSeconds = resolvedDuration?.TotalSeconds ?? 0,
                UserOffsetMs = userOffsetMs
            },
            cancellationToken).ConfigureAwait(false);
        return LrcDocument.Empty;
    }

    public void InvalidateNegativeCache(string? title, string? artist)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return;
        }

        string cacheKey = LyricsCacheStore.ComputeCacheKey(title, artist);
        _cacheStore.RemoveNegativeEntryFromMemory(cacheKey);
    }

    public Task<int> AdjustUserOffsetAsync(
        LyricsQuery query,
        int deltaMs,
        bool reset = false,
        CancellationToken cancellationToken = default)
    {
        if (!query.IsValid)
        {
            return Task.FromResult(0);
        }

        string cacheKey = LyricsCacheStore.ComputeCacheKey(query.Title, query.Artist);
        return _cacheStore.UpdateUserOffsetAsync(cacheKey, deltaMs, reset, cancellationToken);
    }
}
