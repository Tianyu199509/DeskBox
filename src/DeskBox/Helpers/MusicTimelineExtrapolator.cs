namespace DeskBox.Services;

/// <summary>
/// Extrapolates media playback position from SMTC timeline snapshots while
/// guarding against stale timestamps, pause-before-timeline-update races,
/// uninitialized WinRT FILETIME values, and timeline-less players (such as
/// unpatched NetEase Cloud Music 3.x).
/// </summary>
internal sealed class MusicTimelineExtrapolator
{
    internal const double DriftSnapThresholdSeconds = 1.5;
    internal static readonly TimeSpan MaxUnknownBufferingWindow = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan MaxValidTimestampAge = TimeSpan.FromHours(24);
    private static readonly TimeSpan MaxClockSkewAhead = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan StalePlayingExtrapolationSlack = TimeSpan.FromSeconds(5);

    private string _trackKey = string.Empty;
    private TimeSpan _anchorPosition;
    private DateTimeOffset _anchorUtc;
    private TimeSpan _smtcDuration;
    private TimeSpan _fallbackDuration;
    private double _playbackRate = 1.0;
    private MusicPlaybackState _playbackState = MusicPlaybackState.Unknown;
    private bool _usesLocalFallbackClock;
    private TimeSpan? _lastSmtcRawPosition;
    private DateTimeOffset? _lastSmtcUpdatedUtc;
    private DateTimeOffset? _unknownStateSinceUtc;

    public bool UsesLocalFallbackClock => _usesLocalFallbackClock;

    public double PlaybackRate => _playbackRate;

    public TimeSpan EffectiveDuration =>
        _smtcDuration > TimeSpan.Zero ? _smtcDuration : _fallbackDuration;

    public bool HasNativeDuration => _smtcDuration > TimeSpan.Zero;

    public bool IsUnknownBufferingExpired(DateTimeOffset nowUtc) =>
        _playbackState == MusicPlaybackState.Unknown &&
        _unknownStateSinceUtc.HasValue &&
        nowUtc - _unknownStateSinceUtc.Value >= MaxUnknownBufferingWindow;

    public void Reset()
    {
        _trackKey = string.Empty;
        _anchorPosition = TimeSpan.Zero;
        _anchorUtc = default;
        _smtcDuration = TimeSpan.Zero;
        _fallbackDuration = TimeSpan.Zero;
        _playbackRate = 1.0;
        _playbackState = MusicPlaybackState.Unknown;
        _usesLocalFallbackClock = false;
        _lastSmtcRawPosition = null;
        _lastSmtcUpdatedUtc = null;
        _unknownStateSinceUtc = null;
    }

    public (TimeSpan Position, TimeSpan Duration) ApplyFullSnapshot(
        string sourceAppUserModelId,
        string title,
        string artist,
        MusicPlaybackState playbackState,
        TimeSpan rawPosition,
        TimeSpan smtcDuration,
        DateTimeOffset smtcLastUpdatedUtc,
        double playbackRate,
        DateTimeOffset nowUtc)
    {
        string nextTrackKey = BuildTrackKey(sourceAppUserModelId, title, artist);
        bool trackChanged = !string.Equals(_trackKey, nextTrackKey, StringComparison.Ordinal);
        if (trackChanged)
        {
            _trackKey = nextTrackKey;
            _fallbackDuration = TimeSpan.Zero;
            _lastSmtcRawPosition = null;
            _lastSmtcUpdatedUtc = null;
        }

        return ApplyCore(
            hasTrackIdentity: !string.IsNullOrWhiteSpace(title),
            trackChanged,
            playbackState,
            rawPosition,
            smtcDuration,
            smtcLastUpdatedUtc,
            playbackRate,
            nowUtc);
    }

    public (TimeSpan Position, TimeSpan Duration) ApplyTimelineSnapshot(
        MusicPlaybackState playbackState,
        TimeSpan rawPosition,
        TimeSpan smtcDuration,
        DateTimeOffset smtcLastUpdatedUtc,
        double playbackRate,
        DateTimeOffset nowUtc)
    {
        return ApplyCore(
            hasTrackIdentity: !string.IsNullOrEmpty(_trackKey),
            trackChanged: false,
            playbackState,
            rawPosition,
            smtcDuration,
            smtcLastUpdatedUtc,
            playbackRate,
            nowUtc);
    }

    public TimeSpan ApplyPlaybackStateChange(
        MusicPlaybackState playbackState,
        double playbackRate,
        DateTimeOffset nowUtc)
    {
        TimeSpan current = GetPosition(nowUtc);
        _anchorPosition = current;
        _anchorUtc = nowUtc;
        _playbackRate = NormalizePlaybackRate(playbackRate);
        _playbackState = playbackState;
        if (playbackState == MusicPlaybackState.Unknown)
        {
            _unknownStateSinceUtc ??= nowUtc;
        }
        else
        {
            _unknownStateSinceUtc = null;
        }

        return current;
    }

    public void ApplyManualSeek(TimeSpan targetPosition, DateTimeOffset nowUtc)
    {
        _anchorPosition = ClampToDuration(
            targetPosition < TimeSpan.Zero ? TimeSpan.Zero : targetPosition,
            EffectiveDuration);
        _anchorUtc = nowUtc;
    }

    public (TimeSpan Position, TimeSpan Duration) SetFallbackDuration(
        TimeSpan fallbackDuration,
        DateTimeOffset nowUtc)
    {
        _fallbackDuration = fallbackDuration > TimeSpan.Zero ? fallbackDuration : TimeSpan.Zero;
        TimeSpan pos = GetPosition(nowUtc);
        if (EffectiveDuration > TimeSpan.Zero && pos > EffectiveDuration)
        {
            _anchorPosition = EffectiveDuration;
            _anchorUtc = nowUtc;
            pos = EffectiveDuration;
        }

        return (pos, EffectiveDuration);
    }

    public TimeSpan GetPosition(DateTimeOffset nowUtc)
    {
        if (_anchorUtc == default)
        {
            return TimeSpan.Zero;
        }

        TimeSpan position = _anchorPosition;
        if (IsAdvancing(_playbackState))
        {
            DateTimeOffset effectiveNow = nowUtc;
            if (_playbackState == MusicPlaybackState.Unknown && _unknownStateSinceUtc.HasValue)
            {
                DateTimeOffset unknownCap = _unknownStateSinceUtc.Value + MaxUnknownBufferingWindow;
                if (effectiveNow > unknownCap)
                {
                    effectiveNow = unknownCap;
                }
            }

            double elapsedSeconds = (effectiveNow - _anchorUtc).TotalSeconds;
            if (elapsedSeconds > 0)
            {
                position += TimeSpan.FromSeconds(elapsedSeconds * _playbackRate);
            }
        }

        return ClampToDuration(position, EffectiveDuration);
    }

    internal static double NormalizePlaybackRate(double playbackRate) =>
        double.IsFinite(playbackRate) && playbackRate is >= 0.05 and <= 4.0
            ? playbackRate
            : 1.0;

    internal static bool IsTimestampUsable(DateTimeOffset timestampUtc, DateTimeOffset nowUtc)
    {
        // Uninitialized WinRT DateTime (UniversalTime == 0) marshals as 1601-01-01Z,
        // and default C# DateTimeOffset is 0001-01-01Z. Reject both as well as
        // implausibly old or future-skewed timestamps.
        if (timestampUtc <= DateTimeOffset.UnixEpoch)
        {
            return false;
        }

        TimeSpan age = nowUtc - timestampUtc;
        return age <= MaxValidTimestampAge && age >= -MaxClockSkewAhead;
    }

    private (TimeSpan Position, TimeSpan Duration) ApplyCore(
        bool hasTrackIdentity,
        bool trackChanged,
        MusicPlaybackState playbackState,
        TimeSpan rawPosition,
        TimeSpan smtcDuration,
        DateTimeOffset smtcLastUpdatedUtc,
        double playbackRate,
        DateTimeOffset nowUtc)
    {
        TimeSpan normalizedRaw = rawPosition < TimeSpan.Zero ? TimeSpan.Zero : rawPosition;
        TimeSpan normalizedDuration = smtcDuration < TimeSpan.Zero ? TimeSpan.Zero : smtcDuration;
        double normalizedRate = NormalizePlaybackRate(playbackRate);
        bool hasSmtcTimeline = normalizedDuration > TimeSpan.Zero || normalizedRaw > TimeSpan.Zero;

        if (!hasSmtcTimeline)
        {
            _smtcDuration = TimeSpan.Zero;
            if (!hasTrackIdentity)
            {
                _usesLocalFallbackClock = false;
                _anchorPosition = TimeSpan.Zero;
                _anchorUtc = nowUtc;
                _playbackRate = normalizedRate;
                _playbackState = playbackState;
                _unknownStateSinceUtc = playbackState == MusicPlaybackState.Unknown
                    ? (_unknownStateSinceUtc ?? nowUtc)
                    : null;
                return (TimeSpan.Zero, EffectiveDuration);
            }

            // Timeline-less player (e.g. unpatched NetEase Cloud Music 3.x):
            // maintain a monotonic local clock per track so elapsed time and
            // matched lyrics still advance smoothly from track start.
            if (trackChanged || !_usesLocalFallbackClock || _anchorUtc == default)
            {
                _usesLocalFallbackClock = true;
                _anchorPosition = TimeSpan.Zero;
                _anchorUtc = nowUtc;
            }
            else
            {
                _anchorPosition = GetPosition(nowUtc);
                _anchorUtc = nowUtc;
            }

            _playbackRate = normalizedRate;
            _playbackState = playbackState;
            UpdateUnknownStateWindow(playbackState, resetWindow: trackChanged, nowUtc);
            return (GetPosition(nowUtc), EffectiveDuration);
        }

        bool switchedFromLocalClock = _usesLocalFallbackClock;
        _usesLocalFallbackClock = false;
        bool hadNoNativeDuration = _smtcDuration <= TimeSpan.Zero && normalizedDuration > TimeSpan.Zero;
        _smtcDuration = normalizedDuration;

        TimeSpan currentBeforeUpdate = trackChanged || _anchorUtc == default
            ? TimeSpan.Zero
            : GetPosition(nowUtc);

        bool sameRawTupleAsLastRead =
            !trackChanged &&
            _lastSmtcRawPosition.HasValue &&
            _lastSmtcRawPosition.Value == normalizedRaw &&
            _lastSmtcUpdatedUtc.HasValue &&
            _lastSmtcUpdatedUtc.Value == smtcLastUpdatedUtc;

        bool wasAdvancing = IsAdvancing(_playbackState);
        bool isNowAdvancing = IsAdvancing(playbackState);
        bool validTimestamp = IsTimestampUsable(smtcLastUpdatedUtc, nowUtc);

        TimeSpan reportedNow;
        if (!isNowAdvancing)
        {
            // When pausing, many Chromium/Spotify sessions fire PlaybackInfoChanged
            // before rewriting TimelineProperties; keep the extrapolated freeze point
            // if the timeline tuple itself has not changed since playing.
            reportedNow = wasAdvancing && sameRawTupleAsLastRead
                ? currentBeforeUpdate
                : ClampToDuration(normalizedRaw, EffectiveDuration);
        }
        else if (!wasAdvancing && sameRawTupleAsLastRead && !trackChanged && _anchorUtc != default)
        {
            // Resumed from a known pause before the player emitted a fresh timeline
            // timestamp: continue from the frozen pause position rather than adding
            // the entire pause duration.
            reportedNow = currentBeforeUpdate;
        }
        else if (validTimestamp)
        {
            double elapsedSeconds = Math.Max(0, (nowUtc - smtcLastUpdatedUtc).TotalSeconds);
            TimeSpan extrapolated = normalizedRaw + TimeSpan.FromSeconds(elapsedSeconds * normalizedRate);
            if (normalizedDuration > TimeSpan.Zero &&
                extrapolated > normalizedDuration + StalePlayingExtrapolationSlack)
            {
                reportedNow = ClampToDuration(normalizedRaw, EffectiveDuration);
            }
            else
            {
                reportedNow = ClampToDuration(extrapolated, EffectiveDuration);
            }
        }
        else
        {
            reportedNow = ClampToDuration(normalizedRaw, EffectiveDuration);
        }

        _lastSmtcRawPosition = normalizedRaw;
        _lastSmtcUpdatedUtc = smtcLastUpdatedUtc;
        _playbackRate = normalizedRate;
        _playbackState = playbackState;
        UpdateUnknownStateWindow(playbackState, resetWindow: trackChanged || !sameRawTupleAsLastRead, nowUtc);

        double driftSeconds = Math.Abs((reportedNow - currentBeforeUpdate).TotalSeconds);
        if (trackChanged ||
            switchedFromLocalClock ||
            hadNoNativeDuration ||
            _anchorUtc == default ||
            !isNowAdvancing ||
            driftSeconds > DriftSnapThresholdSeconds)
        {
            _anchorPosition = reportedNow;
            _anchorUtc = nowUtc;
        }
        else
        {
            _anchorPosition = currentBeforeUpdate;
            _anchorUtc = nowUtc;
        }

        return (GetPosition(nowUtc), EffectiveDuration);
    }

    private void UpdateUnknownStateWindow(
        MusicPlaybackState playbackState,
        bool resetWindow,
        DateTimeOffset nowUtc)
    {
        if (playbackState != MusicPlaybackState.Unknown)
        {
            _unknownStateSinceUtc = null;
            return;
        }

        if (resetWindow || !_unknownStateSinceUtc.HasValue)
        {
            _unknownStateSinceUtc = nowUtc;
        }
    }

    private static bool IsAdvancing(MusicPlaybackState state) =>
        state is (MusicPlaybackState.Playing or MusicPlaybackState.Unknown);

    private static TimeSpan ClampToDuration(TimeSpan position, TimeSpan duration)
    {
        if (position < TimeSpan.Zero)
        {
            return TimeSpan.Zero;
        }

        return duration > TimeSpan.Zero && position > duration
            ? duration
            : position;
    }

    private static string BuildTrackKey(string sourceApp, string title, string artist) =>
        string.Concat(
            (sourceApp ?? string.Empty).Trim(),
            "\u001F",
            (title ?? string.Empty).Trim(),
            "\u001F",
            (artist ?? string.Empty).Trim());
}
