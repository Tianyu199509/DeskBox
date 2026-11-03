namespace DeskBox.Services.Lyrics;

/// <summary>
/// A single synchronized lyric line, optionally paired with a translated or secondary line at the same timestamp.
/// </summary>
public sealed record LrcLine(
    TimeSpan Timestamp,
    string PrimaryText,
    string? SecondaryText = null)
{
    public bool HasSecondaryText => !string.IsNullOrEmpty(SecondaryText);
}

/// <summary>
/// Immutable parsed representation of synchronized or instrumental lyrics for a track.
/// </summary>
public sealed class LrcDocument
{
    private static readonly TimeSpan MinTimerDelay = TimeSpan.FromMilliseconds(40);
    private static readonly TimeSpan MaxTimerDelay = TimeSpan.FromSeconds(30);

    public static LrcDocument Empty { get; } = new([], isInstrumental: false, trackDuration: null, embeddedOffsetMs: 0, userOffsetMs: 0);

    public LrcDocument(
        IReadOnlyList<LrcLine> lines,
        bool isInstrumental = false,
        TimeSpan? trackDuration = null,
        int embeddedOffsetMs = 0,
        int userOffsetMs = 0)
    {
        Lines = lines ?? [];
        IsInstrumental = isInstrumental;
        TrackDuration = trackDuration is { TotalSeconds: > 0 } ? trackDuration : null;
        EmbeddedOffsetMs = embeddedOffsetMs;
        UserOffsetMs = userOffsetMs;
    }

    public IReadOnlyList<LrcLine> Lines { get; }

    public bool IsInstrumental { get; }

    public TimeSpan? TrackDuration { get; }

    /// <summary>
    /// Offset in milliseconds declared inside an LRC <c>[offset:+/-ms]</c> tag.
    /// Positive values delay lyric timestamps; negative values advance them earlier.
    /// </summary>
    public int EmbeddedOffsetMs { get; }

    /// <summary>
    /// Per-track timing adjustment chosen by the user in milliseconds.
    /// Negative values make lyrics appear earlier; positive values make them appear later.
    /// </summary>
    public int UserOffsetMs { get; }

    public int TotalOffsetMs => EmbeddedOffsetMs + UserOffsetMs;

    public bool HasSyncedLines => Lines.Count > 0;

    public LrcDocument WithUserOffset(int userOffsetMs)
    {
        if (userOffsetMs == UserOffsetMs)
        {
            return this;
        }

        return new LrcDocument(
            Lines,
            IsInstrumental,
            TrackDuration,
            EmbeddedOffsetMs,
            userOffsetMs);
    }

    public LrcDocument WithTrackDurationIfMissing(TimeSpan? fallbackDuration)
    {
        if (TrackDuration.HasValue || fallbackDuration is not { TotalSeconds: > 0 })
        {
            return this;
        }

        return new LrcDocument(
            Lines,
            IsInstrumental,
            fallbackDuration,
            EmbeddedOffsetMs,
            UserOffsetMs);
    }

    /// <summary>
    /// Binary-searches the active lyric line index for <paramref name="playbackPosition"/>.
    /// Returns <c>-1</c> when no line has been reached yet or the document has no synced lines.
    /// </summary>
    public int FindActiveLineIndex(TimeSpan playbackPosition)
    {
        if (Lines.Count == 0)
        {
            return -1;
        }

        TimeSpan effectivePosition = playbackPosition - TimeSpan.FromMilliseconds(TotalOffsetMs);
        if (effectivePosition < Lines[0].Timestamp)
        {
            return -1;
        }

        int low = 0;
        int high = Lines.Count - 1;
        int result = 0;

        while (low <= high)
        {
            int mid = low + ((high - low) >> 1);
            if (Lines[mid].Timestamp <= effectivePosition)
            {
                result = mid;
                low = mid + 1;
            }
            else
            {
                high = mid - 1;
            }
        }

        return result;
    }

    /// <summary>
    /// Computes the delay until the next lyric line becomes active at <paramref name="playbackRate"/>,
    /// clamped to <c>[40ms, 30s]</c>. Returns <see langword="null"/> when already on the final line.
    /// </summary>
    public TimeSpan? GetNextTransitionDelay(TimeSpan playbackPosition, double playbackRate)
    {
        if (Lines.Count == 0)
        {
            return null;
        }

        int activeIndex = FindActiveLineIndex(playbackPosition);
        int nextIndex = activeIndex + 1;
        if (nextIndex < 0 || nextIndex >= Lines.Count)
        {
            return null;
        }

        double effectiveRate = double.IsFinite(playbackRate) && playbackRate > 0.05
            ? Math.Clamp(playbackRate, 0.25, 4.0)
            : 1.0;

        TimeSpan nextTriggerPlaybackPos = Lines[nextIndex].Timestamp + TimeSpan.FromMilliseconds(TotalOffsetMs);
        double remainingMs = (nextTriggerPlaybackPos - playbackPosition).TotalMilliseconds / effectiveRate;
        if (!double.IsFinite(remainingMs) || remainingMs <= MinTimerDelay.TotalMilliseconds)
        {
            return MinTimerDelay;
        }

        if (remainingMs >= MaxTimerDelay.TotalMilliseconds)
        {
            return MaxTimerDelay;
        }

        return TimeSpan.FromMilliseconds(remainingMs);
    }
}
