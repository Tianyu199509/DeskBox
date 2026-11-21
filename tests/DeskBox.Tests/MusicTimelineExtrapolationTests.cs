using DeskBox.Services;

namespace DeskBox.Tests;

public sealed class MusicTimelineExtrapolationTests
{
    private static readonly DateTimeOffset BaseUtc = new(2026, 10, 30, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ApplyFullSnapshot_ExtrapolatesFromSmtcLastUpdatedUtcAndPlaybackRate()
    {
        var extrapolator = new MusicTimelineExtrapolator();
        DateTimeOffset smtcUpdated = BaseUtc.AddSeconds(-8);

        (TimeSpan pos, TimeSpan dur) = extrapolator.ApplyFullSnapshot(
            "Spotify.exe",
            "Track A",
            "Artist A",
            MusicPlaybackState.Playing,
            rawPosition: TimeSpan.FromSeconds(20),
            smtcDuration: TimeSpan.FromSeconds(200),
            smtcLastUpdatedUtc: smtcUpdated,
            playbackRate: 1.5,
            nowUtc: BaseUtc);

        // 20s + (8s * 1.5x) = 32s
        Assert.Equal(32.0, pos.TotalSeconds, precision: 3);
        Assert.Equal(TimeSpan.FromSeconds(200), dur);
        Assert.False(extrapolator.UsesLocalFallbackClock);

        // Local progression 4s later at 1.5x -> 38s
        TimeSpan laterPos = extrapolator.GetPosition(BaseUtc.AddSeconds(4));
        Assert.Equal(38.0, laterPos.TotalSeconds, precision: 3);
    }

    [Fact]
    public void IsTimestampUsable_RejectsUninitializedWinRtAndDefaultTimestamps()
    {
        DateTimeOffset winRtZero = new(1601, 1, 1, 0, 0, 0, TimeSpan.Zero);
        Assert.False(MusicTimelineExtrapolator.IsTimestampUsable(default, BaseUtc));
        Assert.False(MusicTimelineExtrapolator.IsTimestampUsable(winRtZero, BaseUtc));
        Assert.False(MusicTimelineExtrapolator.IsTimestampUsable(BaseUtc.AddHours(-30), BaseUtc));
        Assert.False(MusicTimelineExtrapolator.IsTimestampUsable(BaseUtc.AddSeconds(15), BaseUtc));
        Assert.True(MusicTimelineExtrapolator.IsTimestampUsable(BaseUtc.AddSeconds(-30), BaseUtc));
    }

    [Fact]
    public void PauseBeforeTimelineUpdate_FreezesExtrapolatedPositionWithoutJumpingBack()
    {
        var extrapolator = new MusicTimelineExtrapolator();
        DateTimeOffset t0 = BaseUtc;

        extrapolator.ApplyFullSnapshot(
            "msedge.exe",
            "Song",
            "Singer",
            MusicPlaybackState.Playing,
            rawPosition: TimeSpan.FromSeconds(10),
            smtcDuration: TimeSpan.FromSeconds(180),
            smtcLastUpdatedUtc: t0,
            playbackRate: 1.0,
            nowUtc: t0);

        // 25 seconds pass without any SMTC timeline write; user pauses at t0 + 25s.
        // Player emits Paused while TimelineProperties still holds rawPosition=10s, updated=t0.
        DateTimeOffset pauseAt = t0.AddSeconds(25);
        (TimeSpan pausedPos, _) = extrapolator.ApplyTimelineSnapshot(
            MusicPlaybackState.Paused,
            rawPosition: TimeSpan.FromSeconds(10),
            smtcDuration: TimeSpan.FromSeconds(180),
            smtcLastUpdatedUtc: t0,
            playbackRate: 1.0,
            nowUtc: pauseAt);

        Assert.Equal(35.0, pausedPos.TotalSeconds, precision: 3);
        Assert.Equal(35.0, extrapolator.GetPosition(pauseAt.AddSeconds(60)).TotalSeconds, precision: 3);

        // User resumes 60s later before player updates timeline tuple: must resume from 35s, NOT 95s.
        DateTimeOffset resumeAt = pauseAt.AddSeconds(60);
        (TimeSpan resumedPos, _) = extrapolator.ApplyTimelineSnapshot(
            MusicPlaybackState.Playing,
            rawPosition: TimeSpan.FromSeconds(10),
            smtcDuration: TimeSpan.FromSeconds(180),
            smtcLastUpdatedUtc: t0,
            playbackRate: 1.0,
            nowUtc: resumeAt);

        Assert.Equal(35.0, resumedPos.TotalSeconds, precision: 3);
        Assert.Equal(40.0, extrapolator.GetPosition(resumeAt.AddSeconds(5)).TotalSeconds, precision: 3);
    }

    [Fact]
    public void SmallJitter_DoesNotSnapAnchor_LargeSeekDoesSnap()
    {
        var extrapolator = new MusicTimelineExtrapolator();
        extrapolator.ApplyFullSnapshot(
            "App",
            "Song",
            "Artist",
            MusicPlaybackState.Playing,
            rawPosition: TimeSpan.FromSeconds(50),
            smtcDuration: TimeSpan.FromSeconds(240),
            smtcLastUpdatedUtc: BaseUtc,
            playbackRate: 1.0,
            nowUtc: BaseUtc);

        // At BaseUtc + 5s, local position is 55s. SMTC reports 54.2s (-0.8s jitter <= 1.5s threshold).
        DateTimeOffset t5 = BaseUtc.AddSeconds(5);
        (TimeSpan jitterPos, _) = extrapolator.ApplyTimelineSnapshot(
            MusicPlaybackState.Playing,
            rawPosition: TimeSpan.FromSeconds(54.2),
            smtcDuration: TimeSpan.FromSeconds(240),
            smtcLastUpdatedUtc: t5,
            playbackRate: 1.0,
            nowUtc: t5);

        Assert.Equal(55.0, jitterPos.TotalSeconds, precision: 3);

        // User seeks in external player to 120s (> 1.5s drift) -> snaps immediately.
        (TimeSpan seekPos, _) = extrapolator.ApplyTimelineSnapshot(
            MusicPlaybackState.Playing,
            rawPosition: TimeSpan.FromSeconds(120),
            smtcDuration: TimeSpan.FromSeconds(240),
            smtcLastUpdatedUtc: t5,
            playbackRate: 1.0,
            nowUtc: t5);

        Assert.Equal(120.0, seekPos.TotalSeconds, precision: 3);
    }

    [Fact]
    public void TimelineLessSession_AdvancesMonotonicLocalClockAndAcceptsLyricFallbackDuration()
    {
        var extrapolator = new MusicTimelineExtrapolator();

        (TimeSpan startPos, TimeSpan startDur) = extrapolator.ApplyFullSnapshot(
            "cloudmusic.exe",
            "晴天",
            "周杰伦",
            MusicPlaybackState.Playing,
            rawPosition: TimeSpan.Zero,
            smtcDuration: TimeSpan.Zero,
            smtcLastUpdatedUtc: default,
            playbackRate: 1.0,
            nowUtc: BaseUtc);

        Assert.True(extrapolator.UsesLocalFallbackClock);
        Assert.False(extrapolator.HasNativeDuration);
        Assert.Equal(TimeSpan.Zero, startPos);
        Assert.Equal(TimeSpan.Zero, startDur);

        // Matched lyrics supply 269s fallback duration at +12s into track
        DateTimeOffset t12 = BaseUtc.AddSeconds(12);
        (TimeSpan posAt12, TimeSpan effectiveDur) = extrapolator.SetFallbackDuration(
            TimeSpan.FromSeconds(269),
            t12);

        Assert.Equal(12.0, posAt12.TotalSeconds, precision: 3);
        Assert.Equal(TimeSpan.FromSeconds(269), effectiveDur);

        // Subsequent redundant full refresh on the same timeline-less track does NOT reset elapsed clock to 00:00
        DateTimeOffset t20 = BaseUtc.AddSeconds(20);
        (TimeSpan posAt20, _) = extrapolator.ApplyFullSnapshot(
            "cloudmusic.exe",
            "晴天",
            "周杰伦",
            MusicPlaybackState.Playing,
            rawPosition: TimeSpan.Zero,
            smtcDuration: TimeSpan.Zero,
            smtcLastUpdatedUtc: default,
            playbackRate: 1.0,
            nowUtc: t20);

        Assert.Equal(20.0, posAt20.TotalSeconds, precision: 3);

        // Switching to a new track resets the monotonic clock and clears old fallback duration
        (TimeSpan nextTrackPos, TimeSpan nextTrackDur) = extrapolator.ApplyFullSnapshot(
            "cloudmusic.exe",
            "七里香",
            "周杰伦",
            MusicPlaybackState.Playing,
            rawPosition: TimeSpan.Zero,
            smtcDuration: TimeSpan.Zero,
            smtcLastUpdatedUtc: default,
            playbackRate: 1.0,
            nowUtc: t20.AddSeconds(5));

        Assert.Equal(TimeSpan.Zero, nextTrackPos);
        Assert.Equal(TimeSpan.Zero, nextTrackDur);

        // Single-track repeat on a timeline-less player: once elapsed position
        // passes fallback duration + 1.5s while still playing the same track,
        // the fallback clock wraps instead of staying frozen at the end.
        extrapolator.SetFallbackDuration(TimeSpan.FromSeconds(100), t20.AddSeconds(5));
        TimeSpan loopedPos = extrapolator.GetPosition(t20.AddSeconds(5 + 103));
        Assert.Equal(3.0, loopedPos.TotalSeconds, precision: 3);
    }
}
