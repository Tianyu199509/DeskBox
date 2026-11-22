using System.Text.Json;
using DeskBox.Services;
using DeskBox.Services.Media;

namespace DeskBox.Tests;

public sealed class MediaSessionIsolationContractTests
{
    [Fact]
    public void MainProcessSource_NeverActivatesWindowsMediaControlInProcess()
    {
        string sourceRoot = TestPaths.FromRepository("src/DeskBox");
        var offenders = new List<string>();

        foreach (string filePath in Directory.EnumerateFiles(sourceRoot, "*.cs", SearchOption.AllDirectories))
        {
            string normalized = filePath.Replace('\\', '/');
            if (normalized.Contains("/obj/", StringComparison.OrdinalIgnoreCase) ||
                normalized.Contains("/bin/", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string content = File.ReadAllText(filePath);
            if (content.Contains("Windows.Media.Control", StringComparison.Ordinal) ||
                content.Contains("GlobalSystemMediaTransportControls", StringComparison.Ordinal))
            {
                string relative = Path.GetRelativePath(TestPaths.FromRepository(string.Empty), filePath).Replace('\\', '/');
                offenders.Add(relative);
            }
        }

        Assert.True(
            offenders.Count == 0,
            "Windows.Media.Control / GlobalSystemMediaTransportControls must remain strictly isolated out-of-process in deskbox-thumbnail-proxy.exe --media-session-server. Offending files: " +
            string.Join(", ", offenders));
    }

    [Fact]
    public void CircuitBreaker_AppliesExponentialBackoffOpensAtFiveCrashesAndResetsAfterHealthyUptime()
    {
        var breaker = new MediaSessionCircuitBreaker();
        DateTimeOffset t0 = new(2026, 6, 1, 10, 0, 0, TimeSpan.Zero);

        Assert.True(breaker.CanStartNow(t0, out TimeSpan initialDelay));
        Assert.Equal(TimeSpan.Zero, initialDelay);

        // Crashes 1..4 apply 1s -> 3s -> 10s -> 30s backoff and remain Reconnecting.
        TimeSpan[] expectedDelays =
        [
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(3),
            TimeSpan.FromSeconds(10),
            TimeSpan.FromSeconds(30)
        ];

        DateTimeOffset now = t0;
        for (int i = 0; i < expectedDelays.Length; i++)
        {
            (MediaSessionConnectionState state, TimeSpan? delay, int count) = breaker.RecordUnexpectedExit(now);
            Assert.Equal(MediaSessionConnectionState.Reconnecting, state);
            Assert.Equal(expectedDelays[i], delay);
            Assert.Equal(i + 1, count);

            Assert.False(breaker.CanStartNow(now + TimeSpan.FromMilliseconds(500), out TimeSpan remaining));
            Assert.True(remaining > TimeSpan.Zero);

            now += expectedDelays[i];
            Assert.True(breaker.CanStartNow(now, out TimeSpan zeroRemaining));
            Assert.Equal(TimeSpan.Zero, zeroRemaining);
            breaker.RecordStarted(now);
            now += TimeSpan.FromSeconds(5);
        }

        // Crash 5 within 5 minutes opens circuit -> Unavailable with no automatic retry delay.
        (MediaSessionConnectionState openState, TimeSpan? openDelay, int openCount) = breaker.RecordUnexpectedExit(now);
        Assert.Equal(MediaSessionConnectionState.Unavailable, openState);
        Assert.Null(openDelay);
        Assert.Equal(5, openCount);
        Assert.False(breaker.CanStartNow(now + TimeSpan.FromHours(1), out TimeSpan infiniteDelay));
        Assert.Equal(Timeout.InfiniteTimeSpan, infiniteDelay);

        // Manual retry resets breaker back to Healthy and allows immediate restart.
        breaker.ResetForManualRetry();
        Assert.Equal(MediaSessionConnectionState.Healthy, breaker.State);
        Assert.True(breaker.CanStartNow(now, out _));

        // 2 minutes of healthy uptime clears prior crash history back to 1s backoff.
        breaker.RecordStarted(now);
        breaker.RecordUnexpectedExit(now + TimeSpan.FromSeconds(10));
        breaker.RecordStarted(now + TimeSpan.FromSeconds(11));
        (MediaSessionConnectionState postHealthyState, TimeSpan? postHealthyDelay, int postHealthyCount) =
            breaker.RecordUnexpectedExit(now + TimeSpan.FromMinutes(3));
        Assert.Equal(MediaSessionConnectionState.Reconnecting, postHealthyState);
        Assert.Equal(TimeSpan.FromSeconds(1), postHealthyDelay);
        Assert.Equal(1, postHealthyCount);
    }

    [Fact]
    public void ProxyProtocol_RoundTripsCommandsResponsesAndHelpers()
    {
        string serialized = MediaSessionProxyProtocol.SerializeCommand(new MediaSessionProxyCommand
        {
            Id = 42,
            Op = "get_info",
            PreferredSessionId = "spotify.exe\u001F0",
            KnownCoverFingerprint = "12345678901234567890"
        });
        Assert.Contains("\"id\":42", serialized, StringComparison.Ordinal);
        Assert.Contains("\"op\":\"get_info\"", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("\"action\"", serialized, StringComparison.Ordinal);

        const string sampleReply =
            """{"type":"response","id":42,"ok":true,"info":{"sessionId":"spotify.exe\u001F0","sourceAppUserModelId":"Spotify.exe","title":"Song","artist":"Artist","album":"Album","playbackState":"playing","positionTicks":10000000,"durationTicks":1800000000,"positionUpdatedFileTimeTicks":133932168000000000,"playbackRate":1.0,"canPlay":true,"canPause":true,"canGoPrevious":true,"canGoNext":true,"canSeek":true,"canChangeShuffle":true,"canChangeRepeat":true,"playbackMode":"repeat_one","hasThumbnail":true,"thumbnailFingerprint":"987654321","thumbnailUnchanged":false,"thumbnailContentType":"image/png","thumbnailBase64":"AQID"}}""";
        MediaSessionProxyMessage? decoded = MediaSessionProxyProtocol.DeserializeMessage(sampleReply);
        Assert.NotNull(decoded);
        Assert.Equal("response", decoded!.Type);
        Assert.Equal(42, decoded.Id);
        Assert.True(decoded.Ok);
        Assert.NotNull(decoded.Info);
        Assert.Equal(MusicPlaybackState.Playing, MediaSessionProxyProtocol.ParsePlaybackState(decoded.Info!.PlaybackState));
        Assert.Equal(MusicPlaybackMode.RepeatOne, MediaSessionProxyProtocol.ParsePlaybackMode(decoded.Info.PlaybackMode));
        Assert.Equal("repeat_one", MediaSessionProxyProtocol.FormatPlaybackMode(MusicPlaybackMode.RepeatOne));
        Assert.Equal(987654321UL, MediaSessionProxyProtocol.ParseFingerprint(decoded.Info.ThumbnailFingerprint));
        Assert.Equal(new byte[] { 1, 2, 3 }, MediaSessionProxyProtocol.TryDecodeBase64(decoded.Info.ThumbnailBase64));
        Assert.True(MediaSessionProxyProtocol.ParseFileTimeUtc(decoded.Info.PositionUpdatedFileTimeTicks) > DateTimeOffset.UnixEpoch);
    }

    [Fact]
    public void AllLocales_DefineMediaServiceDegradedAndRetryStrings()
    {
        string stringsDir = TestPaths.FromRepository("src/DeskBox/Strings");
        string[] localeFiles = Directory.GetFiles(stringsDir, "*.json", SearchOption.TopDirectoryOnly);
        Assert.Equal(13, localeFiles.Length);

        foreach (string file in localeFiles)
        {
            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(file));
            Assert.True(
                doc.RootElement.TryGetProperty("Music.Status.ServiceUnavailable", out JsonElement statusEl) &&
                !string.IsNullOrWhiteSpace(statusEl.GetString()),
                $"Missing Music.Status.ServiceUnavailable in {Path.GetFileName(file)}");
            Assert.True(
                doc.RootElement.TryGetProperty("Music.Action.RetryConnection", out JsonElement retryEl) &&
                !string.IsNullOrWhiteSpace(retryEl.GetString()),
                $"Missing Music.Action.RetryConnection in {Path.GetFileName(file)}");
        }
    }
}
