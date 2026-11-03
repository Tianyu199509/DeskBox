using DeskBox.Controls;
using DeskBox.Helpers;
using DeskBox.Models;
using DeskBox.Services;
using DeskBox.ViewModels;
using Microsoft.UI.Xaml;
using Windows.Storage.Streams;
using Windows.UI;

namespace DeskBox.Tests;

public sealed class MusicPlaybackSyncRegressionTests
{
    [Fact]
    public async Task TogglePlayPauseAsync_UpdatesImmediatelyAndHoldsThroughDelayedSmtcTransition()
    {
        var fakeService = new FakeMusicSessionController
        {
            PlaybackState = MusicPlaybackState.Paused,
            CanPlay = true,
            CanPause = true,
            UpdateStateImmediatelyOnCommand = false
        };
        using var viewModel = CreateReadyMusicViewModel(fakeService);
        await viewModel.InitializeAsync();

        Assert.False(viewModel.IsPlaying);
        Assert.Equal(Visibility.Visible, viewModel.PlayIconVisibility);
        Assert.Equal(Visibility.Collapsed, viewModel.PauseIconVisibility);

        Task toggleTask = viewModel.TogglePlayPauseAsync();

        // Audio started in player, SMTC still temporarily reports Paused / Changing (Unknown).
        Assert.Equal(1, fakeService.PlayCallCount);
        Assert.True(viewModel.IsPlaying);
        Assert.Equal(Visibility.Collapsed, viewModel.PlayIconVisibility);
        Assert.Equal(Visibility.Visible, viewModel.PauseIconVisibility);

        fakeService.PlaybackState = MusicPlaybackState.Unknown;
        fakeService.RaisePlaybackInfoChanged();
        Assert.True(viewModel.IsPlaying);

        await Task.Delay(320);
        Assert.True(viewModel.IsPlaying);

        // Player finishes fade-in and reports Playing.
        fakeService.PlaybackState = MusicPlaybackState.Playing;
        fakeService.RaisePlaybackInfoChanged();

        await toggleTask;
        Assert.True(viewModel.IsPlaying);
        Assert.Equal(MusicPlaybackState.Playing, viewModel.PlaybackState);
    }

    [Fact]
    public async Task TogglePlayPauseAsync_ReconcilesBackToPausedWhenPlayerRemainsPausedAfterSettleWindow()
    {
        var fakeService = new FakeMusicSessionController
        {
            PlaybackState = MusicPlaybackState.Paused,
            CanPlay = true,
            CanPause = false,
            UpdateStateImmediatelyOnCommand = false
        };
        using var viewModel = CreateReadyMusicViewModel(fakeService);
        await viewModel.InitializeAsync();

        await viewModel.TogglePlayPauseAsync();

        Assert.Equal(1, fakeService.PlayCallCount);
        Assert.False(viewModel.IsPlaying);
        Assert.Equal(MusicPlaybackState.Paused, viewModel.PlaybackState);
    }

    [Fact]
    public async Task TogglePlayPauseAsync_WorksEvenBeforeWindowRevealCompletion()
    {
        var fakeService = new FakeMusicSessionController
        {
            PlaybackState = MusicPlaybackState.Paused,
            CanPlay = true,
            CanPause = true,
            UpdateStateImmediatelyOnCommand = true
        };
        var config = new WidgetConfig
        {
            Id = "music-pre-reveal",
            WidgetKind = WidgetKind.Music,
            Name = "Music"
        };
        using var viewModel = new MusicWidgetViewModel(
            config,
            fakeService,
            TestServices.CreateLocalizationService());

        await viewModel.InitializeAsync();
        viewModel.OnWindowVisibilityChanged(true);
        // Intentionally do not call OnWindowRevealCompleted() yet (simulates 240ms reveal window).

        await viewModel.TogglePlayPauseAsync();

        Assert.Equal(1, fakeService.PlayCallCount);
        Assert.True(viewModel.IsPlaying);
        Assert.Equal(MusicPlaybackState.Playing, viewModel.PlaybackState);
    }

    [Fact]
    public async Task MonitorOcclusionSuspendAndResume_RestoresSmtcEventSynchronization()
    {
        var fakeService = new FakeMusicSessionController
        {
            PlaybackState = MusicPlaybackState.Paused,
            CanPlay = true,
            CanPause = true
        };
        using var viewModel = CreateReadyMusicViewModel(fakeService);
        await viewModel.InitializeAsync();

        // Simulate monitor occlusion suspend (maximized foreground window on desktop).
        viewModel.OnWindowVisibilityChanged(false);

        // External player starts playing while widget is occluded.
        fakeService.PlaybackState = MusicPlaybackState.Playing;
        fakeService.RaisePlaybackInfoChanged();
        Assert.False(viewModel.IsPlaying);

        // Simulate un-occlusion resume (OnWindowVisibilityChanged(true) + OnWindowRevealCompleted()).
        viewModel.OnWindowVisibilityChanged(true);
        viewModel.OnWindowRevealCompleted();
        await Task.Delay(50);

        Assert.True(viewModel.IsPlaying);

        // Subsequent external player pause/play events continue syncing cleanly.
        fakeService.PlaybackState = MusicPlaybackState.Paused;
        fakeService.RaisePlaybackInfoChanged();
        await Task.Delay(20);
        Assert.False(viewModel.IsPlaying);
    }

    [Fact]
    public async Task TimelineChanged_SyncsConcretePlaybackStateWhenPlaybackInfoReportedChanging()
    {
        var fakeService = new FakeMusicSessionController
        {
            PlaybackState = MusicPlaybackState.Unknown,
            CanPlay = true,
            CanPause = true
        };
        using var viewModel = CreateReadyMusicViewModel(fakeService);
        await viewModel.InitializeAsync();
        Assert.False(viewModel.IsPlaying);

        fakeService.PlaybackState = MusicPlaybackState.Playing;
        fakeService.Position = TimeSpan.FromSeconds(12);
        fakeService.RaiseTimelinePropertiesChanged();
        await Task.Delay(20);

        Assert.True(viewModel.IsPlaying);
        Assert.Equal(MusicPlaybackState.Playing, viewModel.PlaybackState);
    }

    [Fact]
    public void MonitorOcclusionSourceContract_RoutesThroughWindowSuspendAndResumeWithRevealCompletion()
    {
        string wmMemorySource = File.ReadAllText(
            TestPaths.FromRepository("src/DeskBox/Services/WidgetManager.Memory.cs"));
        string windowInteractionSource = File.ReadAllText(
            TestPaths.FromRepository("src/DeskBox/Views/ContentWidgetWindow.WindowInteraction.cs"));

        Assert.Contains("window.SuspendContentForMonitorOcclusion();", wmMemorySource, StringComparison.Ordinal);
        Assert.Contains("window.ResumeContentFromMonitorOcclusion();", wmMemorySource, StringComparison.Ordinal);
        Assert.DoesNotContain("window.CurrentContent?.OnWindowVisibilityChanged(", wmMemorySource, StringComparison.Ordinal);

        int resumeStart = windowInteractionSource.IndexOf(
            "internal void ResumeContentFromMonitorOcclusion()",
            StringComparison.Ordinal);
        Assert.True(resumeStart >= 0);
        string resumeSlice = windowInteractionSource[resumeStart..];
        Assert.Contains("QueueVisibleContentResume();", resumeSlice, StringComparison.Ordinal);
        Assert.Contains("_contentHost.OnWindowRevealCompleted();", resumeSlice, StringComparison.Ordinal);
    }

    [Fact]
    public void SelectSystemSessionCandidateIndex_PrefersPlayingPlayerAndRetainsPausedCurrentSession()
    {
        // Browser tab is system-current & paused, while music player is actively playing -> pick playing player.
        int playingIndex = MusicSessionService.SelectSystemSessionCandidateIndex(
        [
            new SystemSessionCandidate(MusicPlaybackState.Paused, IsClosed: false, IsSystemCurrent: true, IsPreviousCurrent: false),
            new SystemSessionCandidate(MusicPlaybackState.Playing, IsClosed: false, IsSystemCurrent: false, IsPreviousCurrent: false)
        ]);
        Assert.Equal(1, playingIndex);

        // Music player paused and Windows cleared GetCurrentSession() -> retain previous current session instead of jumping to index 0.
        int retainedPausedIndex = MusicSessionService.SelectSystemSessionCandidateIndex(
        [
            new SystemSessionCandidate(MusicPlaybackState.Paused, IsClosed: false, IsSystemCurrent: false, IsPreviousCurrent: false),
            new SystemSessionCandidate(MusicPlaybackState.Paused, IsClosed: false, IsSystemCurrent: false, IsPreviousCurrent: true)
        ]);
        Assert.Equal(1, retainedPausedIndex);
    }

    [Fact]
    public async Task ChromiumBrowserSession_ReplacesInitialAppIconPlaceholderWithoutSecondMediaPropertiesEvent()
    {
        byte[] braveIconBytes = CreateSyntheticImagePayload(seed: 0x11, length: 1024);
        byte[] song1ArtworkBytes = CreateSyntheticImagePayload(seed: 0xA1, length: 4096);
        ulong braveIconFp = MusicWidgetViewModel.ComputeThumbnailFingerprint(braveIconBytes);
        ulong song1ArtworkFp = MusicWidgetViewModel.ComputeThumbnailFingerprint(song1ArtworkBytes);

        var fakeService = new FakeMusicSessionController
        {
            SessionId = MusicSessionService.CreateSessionId("brave.exe", 0),
            SourceApp = "brave.exe",
            Title = "Song One",
            Artist = "Artist One",
            Album = "Album One",
            PlaybackState = MusicPlaybackState.Playing,
            ThumbnailBytes = braveIconBytes
        };
        using var viewModel = CreateReadyMusicViewModel(fakeService);
        await viewModel.InitializeAsync();

        // Initially shows Brave's placeholder icon because Song 1's cover is still downloading.
        Assert.Equal(braveIconFp, viewModel.CurrentCoverFingerprintForTesting);
        Assert.Equal(1, viewModel.DecodedCoverApplyCountForTesting);

        // Chromium finishes fetching YouTube Music artwork and updates SMTC Thumbnail WITHOUT a second MediaPropertiesChanged.
        await Task.Delay(150);
        fakeService.ThumbnailBytes = song1ArtworkBytes;

        // Late cover probe detects the updated stream, applies real artwork, and learns Brave's app icon as the session placeholder.
        await WaitUntilAsync(() => viewModel.CurrentCoverFingerprintForTesting == song1ArtworkFp, timeoutMs: 2200);
        Assert.Equal(song1ArtworkFp, viewModel.CurrentCoverFingerprintForTesting);
        Assert.Equal(braveIconFp, viewModel.SessionPlaceholderFingerprintForTesting);
        Assert.Equal(2, viewModel.DecodedCoverApplyCountForTesting);
    }

    [Fact]
    public async Task ChromiumBrowserSession_SubsequentTrackSkipSuppressesAppIconFlashAndAppliesDownloadedCover()
    {
        byte[] braveIconBytes = CreateSyntheticImagePayload(seed: 0x22, length: 1024);
        byte[] song1ArtworkBytes = CreateSyntheticImagePayload(seed: 0xB1, length: 4096);
        byte[] song2ArtworkBytes = CreateSyntheticImagePayload(seed: 0xB2, length: 4096);
        ulong braveIconFp = MusicWidgetViewModel.ComputeThumbnailFingerprint(braveIconBytes);
        ulong song1ArtworkFp = MusicWidgetViewModel.ComputeThumbnailFingerprint(song1ArtworkBytes);
        ulong song2ArtworkFp = MusicWidgetViewModel.ComputeThumbnailFingerprint(song2ArtworkBytes);

        var fakeService = new FakeMusicSessionController
        {
            SessionId = MusicSessionService.CreateSessionId("brave.exe", 0),
            SourceApp = "brave.exe",
            Title = "First Song",
            Artist = "First Artist",
            Album = "First Album",
            PlaybackState = MusicPlaybackState.Playing,
            ThumbnailBytes = braveIconBytes
        };
        using var viewModel = CreateReadyMusicViewModel(fakeService);
        await viewModel.InitializeAsync();

        fakeService.ThumbnailBytes = song1ArtworkBytes;
        await WaitUntilAsync(() => viewModel.CurrentCoverFingerprintForTesting == song1ArtworkFp, timeoutMs: 2200);
        Assert.Equal(braveIconFp, viewModel.SessionPlaceholderFingerprintForTesting);
        int decodeCountAfterSong1 = viewModel.DecodedCoverApplyCountForTesting;

        // Skip to Song 2: Brave immediately publishes Song 2's metadata with Brave's app icon as temporary thumbnail.
        fakeService.Title = "Second Song";
        fakeService.Artist = "Second Artist";
        fakeService.Album = "Second Album";
        fakeService.ThumbnailBytes = braveIconBytes;
        await viewModel.RefreshAsync();

        // Title & Artist update immediately, while Brave's temporary app icon is held (no browser-logo flash!).
        Assert.Equal("Second Song", viewModel.Title);
        Assert.Equal("Second Artist", viewModel.Artist);
        Assert.Equal(song1ArtworkFp, viewModel.CurrentCoverFingerprintForTesting);
        Assert.False(viewModel.HasAppliedCoverForCurrentTrackForTesting);
        Assert.Equal(decodeCountAfterSong1, viewModel.DecodedCoverApplyCountForTesting);

        // Song 2's real artwork finishes downloading 180ms later (again without needing a second MediaPropertiesChanged).
        await Task.Delay(180);
        fakeService.ThumbnailBytes = song2ArtworkBytes;

        await WaitUntilAsync(() => viewModel.CurrentCoverFingerprintForTesting == song2ArtworkFp, timeoutMs: 2200);
        Assert.True(viewModel.HasAppliedCoverForCurrentTrackForTesting);
        Assert.Equal(decodeCountAfterSong1 + 1, viewModel.DecodedCoverApplyCountForTesting);

        // Skip to Song 3 on the SAME album (initial Brave icon -> same song2ArtworkBytes):
        // neither flashes Brave's icon nor re-decodes identical album bytes.
        fakeService.Title = "Third Song Same Album";
        fakeService.ThumbnailBytes = braveIconBytes;
        await viewModel.RefreshAsync();
        Assert.Equal(song2ArtworkFp, viewModel.CurrentCoverFingerprintForTesting);
        Assert.False(viewModel.HasAppliedCoverForCurrentTrackForTesting);

        fakeService.ThumbnailBytes = song2ArtworkBytes;
        await WaitUntilAsync(() => viewModel.HasAppliedCoverForCurrentTrackForTesting, timeoutMs: 2200);
        Assert.Equal(song2ArtworkFp, viewModel.CurrentCoverFingerprintForTesting);
        Assert.Equal(decodeCountAfterSong1 + 1, viewModel.DecodedCoverApplyCountForTesting);
    }

    [Fact]
    public async Task ChromiumBrowserSession_CoverlessTabFallsBackToAppIconAfterHoldGrace()
    {
        byte[] braveIconBytes = CreateSyntheticImagePayload(seed: 0x33, length: 1024);
        byte[] song1ArtworkBytes = CreateSyntheticImagePayload(seed: 0xC1, length: 4096);
        ulong braveIconFp = MusicWidgetViewModel.ComputeThumbnailFingerprint(braveIconBytes);
        ulong song1ArtworkFp = MusicWidgetViewModel.ComputeThumbnailFingerprint(song1ArtworkBytes);

        var fakeService = new FakeMusicSessionController
        {
            SessionId = MusicSessionService.CreateSessionId("brave.exe", 0),
            SourceApp = "brave.exe",
            Title = "Music Track",
            Artist = "Music Artist",
            PlaybackState = MusicPlaybackState.Playing,
            ThumbnailBytes = braveIconBytes
        };
        using var viewModel = CreateReadyMusicViewModel(fakeService);
        await viewModel.InitializeAsync();

        fakeService.ThumbnailBytes = song1ArtworkBytes;
        await WaitUntilAsync(() => viewModel.CurrentCoverFingerprintForTesting == song1ArtworkFp, timeoutMs: 2200);
        Assert.Equal(braveIconFp, viewModel.SessionPlaceholderFingerprintForTesting);

        // Switch to a coverless browser video that never publishes custom Media Session artwork.
        fakeService.Title = "Coverless Lecture";
        fakeService.Artist = "Channel";
        fakeService.Album = string.Empty;
        fakeService.ThumbnailBytes = braveIconBytes;
        await viewModel.RefreshAsync();

        Assert.False(viewModel.HasAppliedCoverForCurrentTrackForTesting);

        // Once PlaceholderCoverHoldGrace expires, DeskBox falls back cleanly to Brave's icon rather than staying stuck on Song 1's cover.
        await WaitUntilAsync(
            () => viewModel.HasAppliedCoverForCurrentTrackForTesting &&
                  viewModel.CurrentCoverFingerprintForTesting == braveIconFp,
            timeoutMs: 2500);
        Assert.Equal(braveIconFp, viewModel.CurrentCoverFingerprintForTesting);
    }

    [Fact]
    public void ComputeThumbnailFingerprint_DistinguishesFullStreamInteriorPixelDifferences()
    {
        byte[] imageA = new byte[65_536];
        byte[] imageB = new byte[65_536];
        Array.Fill(imageA, (byte)0x42);
        Array.Fill(imageB, (byte)0x42);

        // Mutate a single interior byte outside the first 64B, middle 64B, and trailing 64B slices.
        imageB[1024] = 0x99;

        ulong fpA = MusicWidgetViewModel.ComputeThumbnailFingerprint(imageA);
        ulong fpB = MusicWidgetViewModel.ComputeThumbnailFingerprint(imageB);
        Assert.NotEqual(0UL, fpA);
        Assert.NotEqual(0UL, fpB);
        Assert.NotEqual(fpA, fpB);
    }

    [Fact]
    public async Task MidFlightDebouncedTrackChangeCancelledByWindowHide_MarksDirtyAndRecoversOnReveal()
    {
        byte[] coverA = CreateSyntheticImagePayload(seed: 0x41, length: 2048);
        byte[] coverB = CreateSyntheticImagePayload(seed: 0x52, length: 2048);
        ulong fpA = MusicWidgetViewModel.ComputeThumbnailFingerprint(coverA);
        ulong fpB = MusicWidgetViewModel.ComputeThumbnailFingerprint(coverB);

        var fakeService = new FakeMusicSessionController
        {
            Title = "Song Before Hide",
            Artist = "Artist A",
            Album = "Album A",
            PlaybackState = MusicPlaybackState.Playing,
            ThumbnailBytes = coverA
        };
        using var viewModel = CreateReadyMusicViewModel(fakeService);
        await viewModel.InitializeAsync();
        Assert.Equal("Song Before Hide", viewModel.Title);
        Assert.Equal(fpA, viewModel.CurrentCoverFingerprintForTesting);
        Assert.True(viewModel.IsPlaying);

        // Song switches right before monitor occlusion / hide cancels the 220ms settle window.
        fakeService.Title = "Song During Hide";
        fakeService.Artist = "Artist B";
        fakeService.Album = "Album B";
        fakeService.PlaybackState = MusicPlaybackState.Paused;
        fakeService.ThumbnailBytes = coverB;
        fakeService.RaiseMediaPropertiesChanged();

        await Task.Delay(30);
        viewModel.OnWindowVisibilityChanged(false);
        await Task.Delay(260);

        // Reveal immediately (< 30s threshold): dirty flag must force immediate sync to Song B.
        viewModel.OnWindowVisibilityChanged(true);
        viewModel.OnWindowRevealCompleted();

        await WaitUntilAsync(
            () => viewModel.Title == "Song During Hide" &&
                  viewModel.CurrentCoverFingerprintForTesting == fpB &&
                  viewModel.PlaybackState == MusicPlaybackState.Paused,
            timeoutMs: 1500);
        Assert.Equal("Song During Hide", viewModel.Title);
        Assert.Equal("Artist B", viewModel.Artist);
        Assert.Equal(fpB, viewModel.CurrentCoverFingerprintForTesting);
        Assert.False(viewModel.IsPlaying);
    }

    [Fact]
    public async Task PostMemoryReclaimAndMissedSmtcEvents_ReconcilesStaleTrackCoverAndPlaybackState()
    {
        byte[] coverA = CreateSyntheticImagePayload(seed: 0x61, length: 2048);
        byte[] coverB = CreateSyntheticImagePayload(seed: 0x72, length: 2048);
        ulong fpA = MusicWidgetViewModel.ComputeThumbnailFingerprint(coverA);
        ulong fpB = MusicWidgetViewModel.ComputeThumbnailFingerprint(coverB);

        var fakeService = new FakeMusicSessionController
        {
            Title = "Track One",
            Artist = "Singer One",
            Album = "Record One",
            PlaybackState = MusicPlaybackState.Playing,
            ThumbnailBytes = coverA
        };
        using var viewModel = CreateReadyMusicViewModel(fakeService);
        await viewModel.InitializeAsync();
        Assert.Equal(fpA, viewModel.CurrentCoverFingerprintForTesting);
        Assert.True(viewModel.IsPlaying);

        // Simulate player switching track & pausing without firing SMTC push events during/after memory reclaim.
        fakeService.Title = "Track Two";
        fakeService.Artist = "Singer Two";
        fakeService.Album = "Record Two";
        fakeService.PlaybackState = MusicPlaybackState.Paused;
        fakeService.ThumbnailBytes = coverB;

        MemoryReclaimer.NotifyReclaimCompletedForTesting();

        await WaitUntilAsync(
            () => viewModel.Title == "Track Two" &&
                  viewModel.CurrentCoverFingerprintForTesting == fpB &&
                  viewModel.PlaybackState == MusicPlaybackState.Paused,
            timeoutMs: 1500);
        Assert.Equal("Track Two", viewModel.Title);
        Assert.Equal("Singer Two", viewModel.Artist);
        Assert.Equal(fpB, viewModel.CurrentCoverFingerprintForTesting);
        Assert.False(viewModel.IsPlaying);

        // Subsequent reconcile when state matches live session must be a no-op (no redundant cover decode).
        int decodesBeforeNoOp = viewModel.DecodedCoverApplyCountForTesting;
        await viewModel.ReconcileLiveSessionStateForTestingAsync();
        Assert.Equal(decodesBeforeNoOp, viewModel.DecodedCoverApplyCountForTesting);

        // Recreated session sharing the same SessionId or closed session must never reuse stale cached RCW.
        string sessionId = MusicSessionService.CreateSessionId("QQMusic.exe", 0);
        Assert.False(MusicSessionService.ShouldReuseCachedPreferredSession(
            hasPreviousSession: true,
            previousSessionId: sessionId,
            preferredSessionId: sessionId,
            matchesLiveEntryIdentity: false,
            isPreviousSessionClosed: false));
        Assert.False(MusicSessionService.ShouldReuseCachedPreferredSession(
            hasPreviousSession: true,
            previousSessionId: sessionId,
            preferredSessionId: sessionId,
            matchesLiveEntryIdentity: true,
            isPreviousSessionClosed: true));
        Assert.True(MusicSessionService.ShouldReuseCachedPreferredSession(
            hasPreviousSession: true,
            previousSessionId: sessionId,
            preferredSessionId: sessionId,
            matchesLiveEntryIdentity: true,
            isPreviousSessionClosed: false));
    }

    [Fact]
    public async Task VolumeRefreshAndSnapshot_NeverPushesDefaultHalfVolumeAndCoalescesConcurrentReads()
    {
        var fakeService = new FakeMusicSessionController
        {
            PlaybackState = MusicPlaybackState.Playing
        };
        var fakeVolume = new FakeMusicVolumeController
        {
            SystemVolume = 0.12,
            SessionVolume = 0.85,
            HasSessionVolume = true,
            HasSystemVolume = true,
            ReadDelayMs = 60
        };
        using var viewModel = CreateReadyMusicViewModel(fakeService, fakeVolume);
        await viewModel.InitializeAsync();

        Assert.False(viewModel.HasReadSystemVolume);
        Assert.Equal(0, fakeVolume.SetSystemVolumeCallCount);
        Assert.Equal(0, fakeVolume.SetSessionVolumeCallCount);

        Task<bool> firstRefresh = viewModel.RefreshVolumeAsync();
        Task<bool> secondRefresh = viewModel.RefreshVolumeAsync();
        bool[] results = await Task.WhenAll(firstRefresh, secondRefresh);

        Assert.All(results, Assert.True);
        Assert.True(viewModel.HasReadSystemVolume);
        Assert.Equal(1, fakeVolume.GetVolumeCallCount);
        Assert.Equal(0, fakeVolume.SetSystemVolumeCallCount);
        Assert.Equal(0.12, viewModel.SystemVolume, precision: 3);
        Assert.Equal(0.85, viewModel.SessionVolume, precision: 3);

        // If endpoint snapshot temporarily fails, RefreshVolumeAsync reports false without clobbering SystemVolume or writing 0.5.
        fakeVolume.HasSystemVolume = false;
        fakeVolume.SystemVolume = 0.5;
        bool failedResult = await viewModel.RefreshVolumeAsync();
        Assert.False(failedResult);
        Assert.Equal(0, fakeVolume.SetSystemVolumeCallCount);
        Assert.Equal(0.12, viewModel.SystemVolume, precision: 3);
    }

    [Fact]
    public void NativeMusicVolumeSnapshot_RequiresSucceededSystemHResultBeforeReportingHasSystemVolume()
    {
        var succeeded = new MusicVolumeNativeCallResult(
            MusicVolumeNativeCallFailure.None,
            string.Empty,
            Status: 0,
            OperationHResult: 0,
            AttemptedPhases: 0x1F,
            MatchKind: 0,
            ComHResult: 0,
            CreateHResult: 0,
            DeviceHResult: 0,
            SystemHResult: 0,
            SessionHResult: 0,
            SystemVolume: 0.42,
            SessionVolume: 1.0,
            HasSessionVolume: true);
        var passThroughEndpoint = succeeded with
        {
            SystemHResult = unchecked((int)0x80070490),
            SystemVolume = 0.0
        };
        var failedCall = succeeded with
        {
            Failure = MusicVolumeNativeCallFailure.NativeFailure
        };

        Assert.True(MusicVolumeService.HasNativeSystemVolume(succeeded));
        Assert.False(MusicVolumeService.HasNativeSystemVolume(passThroughEndpoint));
        Assert.False(MusicVolumeService.HasNativeSystemVolume(failedCall));
    }

    [Fact]
    public async Task PlayingTimelineProbe_SkipsFullMediaPropertiesRpcWhenSteadyAndEscalatesOnSilentDivergence()
    {
        byte[] coverA = CreateSyntheticImagePayload(seed: 0x33, length: 2048);
        byte[] coverB = CreateSyntheticImagePayload(seed: 0x44, length: 2048);
        ulong fpB = MusicWidgetViewModel.ComputeThumbnailFingerprint(coverB);

        var fakeService = new FakeMusicSessionController
        {
            Title = "Steady Track",
            Artist = "Artist One",
            Album = "Album One",
            PlaybackState = MusicPlaybackState.Playing,
            Position = TimeSpan.FromSeconds(32),
            Duration = TimeSpan.FromMinutes(3),
            ThumbnailBytes = coverA
        };
        using var viewModel = CreateReadyMusicViewModel(fakeService);
        await viewModel.InitializeAsync();

        int infoCallsAfterInit = fakeService.GetCurrentSessionInfoCallCount;
        int timelineCallsAfterInit = fakeService.GetCurrentTimelineCallCount;

        // Steady playing heartbeat: lightweight timeline probe only, zero TryGetMediaPropertiesAsync / Thumbnail RCW churn.
        await viewModel.ProbeLiveTimelineStateForTestingAsync();
        Assert.Equal(timelineCallsAfterInit + 1, fakeService.GetCurrentTimelineCallCount);
        Assert.Equal(infoCallsAfterInit, fakeService.GetCurrentSessionInfoCallCount);

        // Silent unannounced track restart/skip to a new track of identical duration (position jumps back near 0:00).
        fakeService.Title = "Unannounced Next Song";
        fakeService.Artist = "Artist Two";
        fakeService.Position = TimeSpan.FromSeconds(1);
        fakeService.ThumbnailBytes = coverB;

        await viewModel.ProbeLiveTimelineStateForTestingAsync();
        await WaitUntilAsync(
            () => viewModel.Title == "Unannounced Next Song" &&
                  viewModel.CurrentCoverFingerprintForTesting == fpB,
            timeoutMs: 1500);
        Assert.Equal("Unannounced Next Song", viewModel.Title);
        Assert.Equal(fpB, viewModel.CurrentCoverFingerprintForTesting);
        Assert.True(fakeService.GetCurrentSessionInfoCallCount > infoCallsAfterInit);

        // Direct escalation contract checks (including seek-drag guard).
        var steadySnapshot = new MusicTimelineSnapshot(
            TimeSpan.FromSeconds(40),
            TimeSpan.FromMinutes(3),
            DateTimeOffset.UtcNow,
            PlaybackRate: 1.0,
            MusicPlaybackState.Playing);
        Assert.False(MusicWidgetViewModel.ShouldEscalateTimelineProbeToFullReconcile(
            hasCurrentMediaInfo: true,
            currentPlaybackState: MusicPlaybackState.Playing,
            resolvedLivePlaybackState: MusicPlaybackState.Playing,
            currentEffectiveDuration: TimeSpan.FromMinutes(3),
            extrapolatedPosition: TimeSpan.FromSeconds(41),
            isSeeking: false,
            steadySnapshot));
        Assert.False(MusicWidgetViewModel.ShouldEscalateTimelineProbeToFullReconcile(
            hasCurrentMediaInfo: true,
            currentPlaybackState: MusicPlaybackState.Playing,
            resolvedLivePlaybackState: MusicPlaybackState.Playing,
            currentEffectiveDuration: TimeSpan.FromMinutes(3),
            extrapolatedPosition: TimeSpan.FromSeconds(50),
            isSeeking: true,
            steadySnapshot with { Position = TimeSpan.FromSeconds(2) }));
        Assert.True(MusicWidgetViewModel.ShouldEscalateTimelineProbeToFullReconcile(
            hasCurrentMediaInfo: true,
            currentPlaybackState: MusicPlaybackState.Playing,
            resolvedLivePlaybackState: MusicPlaybackState.Paused,
            currentEffectiveDuration: TimeSpan.FromMinutes(3),
            extrapolatedPosition: TimeSpan.FromSeconds(41),
            isSeeking: false,
            steadySnapshot with { PlaybackState = MusicPlaybackState.Paused }));
        Assert.True(MusicWidgetViewModel.ShouldEscalateTimelineProbeToFullReconcile(
            hasCurrentMediaInfo: true,
            currentPlaybackState: MusicPlaybackState.Playing,
            resolvedLivePlaybackState: MusicPlaybackState.Unknown,
            currentEffectiveDuration: TimeSpan.FromMinutes(3),
            extrapolatedPosition: TimeSpan.FromSeconds(41),
            isSeeking: false,
            liveTimeline: null));
    }

    private static byte[] CreateSyntheticImagePayload(byte seed, int length)
    {
        byte[] bytes = new byte[length];
        for (int i = 0; i < bytes.Length; i++)
        {
            bytes[i] = unchecked((byte)(seed * 31 + i * 17));
        }

        return bytes;
    }

    private static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(35);
        }
    }

    private static MusicWidgetViewModel CreateReadyMusicViewModel(
        FakeMusicSessionController fakeService,
        FakeMusicVolumeController? fakeVolume = null)
    {
        var config = new WidgetConfig
        {
            Id = "music-sync-test",
            WidgetKind = WidgetKind.Music,
            Name = "Music"
        };
        var vm = new MusicWidgetViewModel(
            config,
            fakeService,
            TestServices.CreateLocalizationService(),
            coverDecoder: static bytes =>
            {
                if (bytes is not { Length: > 0 })
                {
                    return Task.FromResult(new DecodedCoverResult(false, null, null));
                }

                Color color = Color.FromArgb(255, bytes[0], bytes[1 % bytes.Length], bytes[2 % bytes.Length]);
                return Task.FromResult(new DecodedCoverResult(true, null, color));
            },
            volumeController: fakeVolume);
        vm.OnWindowVisibilityChanged(true);
        vm.OnWindowRevealCompleted();
        return vm;
    }

    private sealed class FakeMusicVolumeController : IMusicVolumeController
    {
        public double SystemVolume { get; set; } = 0.25;
        public bool HasSystemVolume { get; set; } = true;
        public bool HasSessionVolume { get; set; }
        public double SessionVolume { get; set; } = 1.0;
        public int ReadDelayMs { get; set; }
        public int GetVolumeCallCount { get; private set; }
        public int SetSystemVolumeCallCount { get; private set; }
        public int SetSessionVolumeCallCount { get; private set; }

        public async Task<MusicVolumeSnapshot> GetVolumeAsync(
            string sourceAppUserModelId,
            string sourceDisplayName)
        {
            GetVolumeCallCount++;
            if (ReadDelayMs > 0)
            {
                await Task.Delay(ReadDelayMs);
            }

            return new MusicVolumeSnapshot(
                SystemVolume,
                SessionVolume,
                HasSessionVolume,
                HasSystemVolume);
        }

        public Task<double> GetSystemMasterVolumeAsync() =>
            Task.FromResult(SystemVolume);

        public Task<bool> TrySetSystemMasterVolumeAsync(double volume)
        {
            SetSystemVolumeCallCount++;
            SystemVolume = Math.Clamp(volume, 0.0, 1.0);
            return Task.FromResult(true);
        }

        public Task<bool> TrySetSessionVolumeAsync(
            string sourceAppUserModelId,
            string sourceDisplayName,
            double volume)
        {
            SetSessionVolumeCallCount++;
            SessionVolume = Math.Clamp(volume, 0.0, 1.0);
            return Task.FromResult(true);
        }
    }

    private sealed class FakeMusicSessionController : IMusicSessionController
    {
        public string SessionId { get; set; } = MusicSessionService.CreateSessionId("cloudmusic.exe", 0);
        public string SourceApp { get; set; } = "cloudmusic.exe";
        public string Title { get; set; } = "Test Song";
        public string Artist { get; set; } = "Test Artist";
        public string Album { get; set; } = "Test Album";
        public MusicPlaybackState PlaybackState { get; set; } = MusicPlaybackState.Paused;
        public TimeSpan Position { get; set; } = TimeSpan.FromSeconds(10);
        public TimeSpan Duration { get; set; } = TimeSpan.FromMinutes(3);
        public bool CanPlay { get; set; } = true;
        public bool CanPause { get; set; } = true;
        public bool UpdateStateImmediatelyOnCommand { get; set; } = true;
        public bool AcceptTransportCommands { get; set; } = true;
        public byte[]? ThumbnailBytes { get; set; }
        public int PlayCallCount { get; private set; }
        public int PauseCallCount { get; private set; }
        public int GetCurrentSessionInfoCallCount { get; private set; }
        public int GetCurrentTimelineCallCount { get; private set; }

        public event EventHandler? SessionsChanged;
        public event EventHandler? CurrentSessionChanged;
        public event EventHandler? PlaybackInfoChanged;
        public event EventHandler? MediaPropertiesChanged;
        public event EventHandler? TimelinePropertiesChanged;

        public Task InitializeAsync() => Task.CompletedTask;

        public IReadOnlyList<MusicSessionOption> GetSessionOptions() =>
        [
            new MusicSessionOption(SessionId, SourceApp, "网易云音乐", PlaybackState, IsSystemCurrent: true)
        ];

        public async Task<MusicSessionInfo?> GetCurrentSessionInfoAsync(string? preferredSessionId = null)
        {
            GetCurrentSessionInfoCallCount++;
            return new(
                SessionId,
                SourceApp,
                "网易云音乐",
                Title,
                Artist,
                Album,
                PlaybackState,
                Position,
                Duration,
                DateTimeOffset.UtcNow,
                PlaybackRate: 1.0,
                CanPlay,
                CanPause,
                CanGoPrevious: true,
                CanGoNext: true,
                CanSeek: true,
                CanChangeShuffle: true,
                CanChangeRepeat: true,
                MusicPlaybackMode.Normal,
                Thumbnail: await CreateStreamReferenceAsync(ThumbnailBytes));
        }

        public Task<IRandomAccessStreamReference?> GetCurrentThumbnailAsync(string? preferredSessionId = null) =>
            CreateStreamReferenceAsync(ThumbnailBytes);

        private static async Task<IRandomAccessStreamReference?> CreateStreamReferenceAsync(byte[]? bytes)
        {
            if (bytes is null)
            {
                return null;
            }

            var stream = new InMemoryRandomAccessStream();
            using (var writer = new DataWriter(stream.GetOutputStreamAt(0)))
            {
                writer.WriteBytes(bytes);
                await writer.StoreAsync();
                writer.DetachStream();
            }

            stream.Seek(0);
            return RandomAccessStreamReference.CreateFromStream(stream);
        }

        public Task<MusicTimelineSnapshot?> GetCurrentTimelineAsync(string? preferredSessionId = null)
        {
            GetCurrentTimelineCallCount++;
            return Task.FromResult<MusicTimelineSnapshot?>(new MusicTimelineSnapshot(
                Position,
                Duration,
                DateTimeOffset.UtcNow,
                PlaybackRate: 1.0,
                PlaybackState));
        }

        public Task<MusicPlaybackSnapshot?> GetCurrentPlaybackAsync(string? preferredSessionId = null) =>
            Task.FromResult<MusicPlaybackSnapshot?>(new MusicPlaybackSnapshot(
                PlaybackState,
                PlaybackRate: 1.0,
                CanPlay,
                CanPause,
                CanGoPrevious: true,
                CanGoNext: true,
                CanSeek: true,
                CanChangeShuffle: true,
                CanChangeRepeat: true,
                MusicPlaybackMode.Normal));

        public Task<bool> TrySetPreferredSessionAsync(string sessionId) => Task.FromResult(true);

        public Task<bool> TryPlayAsync(string? sessionId)
        {
            PlayCallCount++;
            if (AcceptTransportCommands && UpdateStateImmediatelyOnCommand)
            {
                PlaybackState = MusicPlaybackState.Playing;
            }

            return Task.FromResult(AcceptTransportCommands);
        }

        public Task<bool> TryPauseAsync(string? sessionId)
        {
            PauseCallCount++;
            if (AcceptTransportCommands && UpdateStateImmediatelyOnCommand)
            {
                PlaybackState = MusicPlaybackState.Paused;
            }

            return Task.FromResult(AcceptTransportCommands);
        }

        public Task<bool> TryPreviousAsync(string? sessionId) => Task.FromResult(AcceptTransportCommands);

        public Task<bool> TryNextAsync(string? sessionId) => Task.FromResult(AcceptTransportCommands);

        public Task<bool> TrySeekAsync(string? sessionId, TimeSpan position)
        {
            if (AcceptTransportCommands)
            {
                Position = position;
            }

            return Task.FromResult(AcceptTransportCommands);
        }

        public Task<bool> TryChangePlaybackModeAsync(string? sessionId, MusicPlaybackMode playbackMode) =>
            Task.FromResult(AcceptTransportCommands);

        public void RaisePlaybackInfoChanged() => PlaybackInfoChanged?.Invoke(this, EventArgs.Empty);

        public void RaiseTimelinePropertiesChanged() => TimelinePropertiesChanged?.Invoke(this, EventArgs.Empty);

        public void RaiseSessionsChanged() => SessionsChanged?.Invoke(this, EventArgs.Empty);

        public void RaiseCurrentSessionChanged() => CurrentSessionChanged?.Invoke(this, EventArgs.Empty);

        public void RaiseMediaPropertiesChanged() => MediaPropertiesChanged?.Invoke(this, EventArgs.Empty);

        public void Dispose()
        {
        }
    }
}
