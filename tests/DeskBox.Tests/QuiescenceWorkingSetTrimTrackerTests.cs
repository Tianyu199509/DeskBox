using DeskBox.Services;

namespace DeskBox.Tests;

public sealed class QuiescenceWorkingSetTrimTrackerTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private const long MB = 1024 * 1024;

    private static readonly QuiescenceTrimActivitySnapshot VisibleBackground = new(
        HasVisibleWidgets: true,
        IsWidgetInteractionActive: false,
        HasActiveVisualWork: false,
        IsTransientUiOpen: false,
        IsDeskBoxForeground: false,
        IsPointerOverDeskBox: false);

    private static readonly QuiescenceTrimActivitySnapshot Hidden =
        VisibleBackground with { HasVisibleWidgets = false };

    private static readonly QuiescenceTrimActivitySnapshot VisibleForeground =
        VisibleBackground with { IsDeskBoxForeground = true };

    private static readonly QuiescenceTrimActivitySnapshot VisibleBackgroundAmbient =
        VisibleBackground with { HasAmbientVisualWork = true };

    private static QuiescenceTrimDecision Observe(
        QuiescenceWorkingSetTrimTracker tracker,
        DateTimeOffset now,
        QuiescenceTrimActivitySnapshot activity,
        long workingSet = 180 * MB) =>
        tracker.Observe(now, activity, () => workingSet);

    [Fact]
    public void VisibleBackground_TrimsOnceAfterFiveSecondQuiet_ThenUsesGrowthBackstop()
    {
        var tracker = new QuiescenceWorkingSetTrimTracker();

        var initial = Observe(tracker, T0, VisibleBackground, workingSet: 180 * MB);
        Assert.False(initial.ShouldTrim);
        Assert.Equal("waiting-for-quiet", initial.Reason);
        Assert.Equal(QuiescenceTrimTier.VisibleBackground, initial.Tier);
        Assert.Equal(
            QuiescenceWorkingSetTrimTracker.VisibleBackgroundQuietDuration,
            tracker.GetNextEvaluationDelay(T0, VisibleBackground));

        DateTimeOffset dueAt = T0 + QuiescenceWorkingSetTrimTracker.VisibleBackgroundQuietDuration;
        var episodeDue = Observe(tracker, dueAt, VisibleBackground, workingSet: 180 * MB);
        Assert.True(episodeDue.ShouldTrim);
        Assert.True(episodeDue.EpisodeTrim);
        Assert.Equal(QuiescenceTrimTier.VisibleBackground, episodeDue.Tier);
        Assert.Equal("episode-idle", episodeDue.Reason);

        tracker.CommitTrim(dueAt);
        Assert.False(tracker.IsHiddenSessionSettled);

        // Within the same unbroken idle episode, no churn after 30 s.
        var cooldownHold = Observe(
            tracker,
            dueAt + TimeSpan.FromSeconds(30),
            VisibleBackground,
            workingSet: 180 * MB);
        Assert.False(cooldownHold.ShouldTrim);
        Assert.Equal("trim-cooldown", cooldownHold.Reason);

        // Sample settled baseline (80 MB) after 90 s.
        Observe(
            tracker,
            dueAt + QuiescenceWorkingSetTrimTracker.BaselineSettleDelay,
            VisibleBackground,
            workingSet: 80 * MB);

        // After 5-minute steady-state interval, +20 MB (< 32 MB floor) does not re-trim.
        DateTimeOffset afterSteadyCooldown =
            dueAt + QuiescenceWorkingSetTrimTracker.StaticBackgroundMinimumTrimInterval + TimeSpan.FromSeconds(10);
        var underGrowth = Observe(
            tracker,
            afterSteadyCooldown,
            VisibleBackground,
            workingSet: 100 * MB);
        Assert.False(underGrowth.ShouldTrim);
        Assert.Equal("insufficient-growth", underGrowth.Reason);

        // Material growth (>= 32 MB over 80 MB baseline) triggers backstop trim.
        var grown = Observe(
            tracker,
            afterSteadyCooldown + TimeSpan.FromSeconds(10),
            VisibleBackground,
            workingSet: 80 * MB + QuiescenceWorkingSetTrimTracker.MinimumWorkingSetGrowthBytes);
        Assert.True(grown.ShouldTrim);
        Assert.False(grown.EpisodeTrim);
        Assert.Equal("growth-backstop", grown.Reason);
    }

    [Fact]
    public void VisibleForegroundAndAmbient_WaitFifteenSeconds_AndLeavingForegroundPreservesCountdown()
    {
        foreach (var snapshot in new[] { VisibleForeground, VisibleBackgroundAmbient })
        {
            var tracker = new QuiescenceWorkingSetTrimTracker();
            Observe(tracker, T0, snapshot);

            var atFiveSeconds = Observe(tracker, T0 + TimeSpan.FromSeconds(5), snapshot);
            Assert.False(atFiveSeconds.ShouldTrim);
            Assert.Equal("waiting-for-quiet", atFiveSeconds.Reason);

            var atFifteenSeconds = Observe(
                tracker,
                T0 + QuiescenceWorkingSetTrimTracker.VisibleForegroundQuietDuration,
                snapshot);
            Assert.True(atFifteenSeconds.ShouldTrim);
            Assert.True(atFifteenSeconds.EpisodeTrim);
        }

        // User clicks DeskBox at T0 (VisibleForeground), then switches to another window before T+5s:
        // VisibleBackground trims right at T+5s without resetting the quiet timer.
        var transitionTracker = new QuiescenceWorkingSetTrimTracker();
        Observe(transitionTracker, T0, VisibleForeground);
        Assert.Equal(
            QuiescenceWorkingSetTrimTracker.VisibleBackgroundQuietDuration,
            transitionTracker.GetNextEvaluationDelay(T0, VisibleForeground));

        var switchedToBackground = Observe(
            transitionTracker,
            T0 + QuiescenceWorkingSetTrimTracker.VisibleBackgroundQuietDuration,
            VisibleBackground);
        Assert.True(switchedToBackground.ShouldTrim);
        Assert.Equal(QuiescenceTrimTier.VisibleBackground, switchedToBackground.Tier);
    }

    [Fact]
    public void HiddenOrOccludedSession_TrimsOnceAfterQuietDuration_AndDisarmsTimerOnceSettled()
    {
        var tracker = new QuiescenceWorkingSetTrimTracker();

        var early = Observe(tracker, T0, Hidden);
        Assert.False(early.ShouldTrim);
        Assert.Equal("waiting-for-quiet", early.Reason);
        Assert.Equal(QuiescenceTrimTier.Hidden, early.Tier);
        Assert.Equal(
            QuiescenceWorkingSetTrimTracker.HiddenQuietDuration,
            tracker.GetNextEvaluationDelay(T0, Hidden));

        DateTimeOffset dueAt = T0 + QuiescenceWorkingSetTrimTracker.HiddenQuietDuration;
        var due = Observe(tracker, dueAt, Hidden);
        Assert.True(due.ShouldTrim);
        Assert.True(due.EpisodeTrim);
        Assert.Equal(QuiescenceTrimTier.Hidden, due.Tier);
        Assert.Equal("episode-idle", due.Reason);

        tracker.CommitTrim(dueAt);
        Assert.True(tracker.IsHiddenSessionSettled);
        Assert.Null(tracker.GetNextEvaluationDelay(dueAt, Hidden));

        var settled = Observe(tracker, dueAt + TimeSpan.FromMinutes(10), Hidden);
        Assert.False(settled.ShouldTrim);
        Assert.Equal("hidden-session-settled", settled.Reason);
        Assert.Null(tracker.GetNextEvaluationDelay(dueAt + TimeSpan.FromMinutes(10), Hidden));
    }

    [Fact]
    public void BelowEpisodeFloor_SkipsTrimWithoutChurn_AndSettlesHiddenSession()
    {
        var hiddenTracker = new QuiescenceWorkingSetTrimTracker();
        Observe(hiddenTracker, T0, Hidden, workingSet: 55 * MB);
        var hiddenBelow = Observe(
            hiddenTracker,
            T0 + QuiescenceWorkingSetTrimTracker.HiddenQuietDuration,
            Hidden,
            workingSet: 55 * MB);
        Assert.False(hiddenBelow.ShouldTrim);
        Assert.Equal("below-episode-floor", hiddenBelow.Reason);
        Assert.True(hiddenTracker.IsHiddenSessionSettled);
        Assert.Null(hiddenTracker.GetNextEvaluationDelay(
            T0 + QuiescenceWorkingSetTrimTracker.HiddenQuietDuration,
            Hidden));

        var visibleTracker = new QuiescenceWorkingSetTrimTracker();
        Observe(visibleTracker, T0, VisibleBackground, workingSet: 55 * MB);
        var visibleBelow = Observe(
            visibleTracker,
            T0 + QuiescenceWorkingSetTrimTracker.VisibleBackgroundQuietDuration,
            VisibleBackground,
            workingSet: 55 * MB);
        Assert.False(visibleBelow.ShouldTrim);
        Assert.Equal("below-episode-floor", visibleBelow.Reason);
        Assert.Equal(
            QuiescenceWorkingSetTrimTracker.BelowEpisodeFloorRetryInterval,
            visibleTracker.GetNextEvaluationDelay(
                T0 + QuiescenceWorkingSetTrimTracker.VisibleBackgroundQuietDuration,
                VisibleBackground));
    }

    [Theory]
    [InlineData(true, false, false, "blocked:interaction")]
    [InlineData(false, true, false, "blocked:visual-work")]
    [InlineData(false, false, true, "blocked:transient-ui")]
    public void Blockers_ResetQuietPeriod_AndScheduleBoundedRetry(
        bool interaction,
        bool visualWork,
        bool transientUi,
        string expectedReason)
    {
        var tracker = new QuiescenceWorkingSetTrimTracker();
        Observe(tracker, T0, VisibleBackground);

        var blockedSnapshot = VisibleBackground with
        {
            IsWidgetInteractionActive = interaction,
            HasActiveVisualWork = visualWork,
            IsTransientUiOpen = transientUi
        };

        var blocked = Observe(tracker, T0 + TimeSpan.FromSeconds(2), blockedSnapshot);
        Assert.False(blocked.ShouldTrim);
        Assert.Equal(expectedReason, blocked.Reason);
        Assert.Equal(
            QuiescenceWorkingSetTrimTracker.BlockedRetryInterval,
            tracker.GetNextEvaluationDelay(T0 + TimeSpan.FromSeconds(2), blockedSnapshot));

        // Quiet restarts from after the blocker clears.
        var restarted = Observe(tracker, T0 + TimeSpan.FromSeconds(3), VisibleBackground);
        Assert.False(restarted.ShouldTrim);
        Assert.Equal("waiting-for-quiet", restarted.Reason);

        var due = Observe(
            tracker,
            T0 + TimeSpan.FromSeconds(3) + QuiescenceWorkingSetTrimTracker.VisibleBackgroundQuietDuration,
            VisibleBackground);
        Assert.True(due.ShouldTrim);
    }

    [Fact]
    public void NoteActivity_RearmsEpisodeTrim_AndRespectsTwentySecondMinimumTrimInterval()
    {
        var tracker = new QuiescenceWorkingSetTrimTracker();
        tracker.CommitTrim(T0);
        Assert.True(tracker.IsHiddenSessionSettled);

        tracker.NoteActivity();
        Assert.False(tracker.IsHiddenSessionSettled);

        Observe(tracker, T0 + TimeSpan.FromSeconds(1), Hidden, workingSet: 200 * MB);

        var heldByCooldown = Observe(
            tracker,
            T0 + TimeSpan.FromSeconds(10),
            Hidden,
            workingSet: 200 * MB);
        Assert.False(heldByCooldown.ShouldTrim);
        Assert.Equal("trim-cooldown", heldByCooldown.Reason);

        TimeSpan? remainingDelay = tracker.GetNextEvaluationDelay(
            T0 + TimeSpan.FromSeconds(10),
            Hidden);
        Assert.Equal(TimeSpan.FromSeconds(10), remainingDelay);

        var dueAfterCooldown = Observe(
            tracker,
            T0 + QuiescenceWorkingSetTrimTracker.MinimumTrimInterval + TimeSpan.FromSeconds(1),
            Hidden,
            workingSet: 200 * MB);
        Assert.True(dueAfterCooldown.ShouldTrim);
    }

    [Fact]
    public void LastTrimWithin_ReflectsRecentCommitWindow()
    {
        var tracker = new QuiescenceWorkingSetTrimTracker();
        Assert.False(tracker.LastTrimWithin(T0, TimeSpan.FromSeconds(20)));

        tracker.CommitTrim(T0);
        Assert.True(tracker.LastTrimWithin(T0 + TimeSpan.FromSeconds(19), TimeSpan.FromSeconds(20)));
        Assert.False(tracker.LastTrimWithin(T0 + TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(20)));
    }

    [Fact]
    public void SingleMasterSwitch_AndUnfocusedSearchPopup_DoNotDeadlockIdleCompression()
    {
        string settingsXaml = File.ReadAllText(
            TestPaths.FromRepository("src/DeskBox/Views/SettingsWindow.xaml"));
        string quiescenceSource = File.ReadAllText(
            TestPaths.FromRepository("src/DeskBox/App.QuiescenceWorkingSetTrim.cs"));
        string immediateSource = File.ReadAllText(
            TestPaths.FromRepository("src/DeskBox/App.ImmediateHiddenWorkingSetTrim.cs"));
        string appSource = File.ReadAllText(
            TestPaths.FromRepository("src/DeskBox/App.xaml.cs"));
        string searchPopupSource = File.ReadAllText(
            TestPaths.FromRepository("src/DeskBox/Views/SearchPopupWindow.xaml.cs"));

        Assert.Contains("AutomationProperties.AutomationId=\"IdleWorkingSetTrimCard\"", settingsXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("IdleWorkingSetTrimExpander", settingsXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("ImmediateHiddenWorkingSetTrimToggle", settingsXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("QuiescenceWorkingSetTrimToggle", settingsXaml, StringComparison.Ordinal);

        Assert.DoesNotContain(".QuiescenceWorkingSetTrimEnabled", quiescenceSource, StringComparison.Ordinal);
        Assert.DoesNotContain("QuiescenceWorkingSetTrimEnabled:", quiescenceSource, StringComparison.Ordinal);
        Assert.DoesNotContain(".ImmediateHiddenWorkingSetTrimEnabled", immediateSource, StringComparison.Ordinal);
        Assert.DoesNotContain("HasUnoccludedVisibleWidgets == true)\n        {\n            _quiescenceWorkingSetTrimTimer?.Stop();", quiescenceSource, StringComparison.Ordinal);

        Assert.Contains("internal bool IsBlockingMemoryCleanup =>", searchPopupSource, StringComparison.Ordinal);
        Assert.Contains("_unfocusedAutoHideTimer", searchPopupSource, StringComparison.Ordinal);
        Assert.Contains("IsSearchPopupVisible: _searchPopupWindow?.IsBlockingMemoryCleanup == true", appSource, StringComparison.Ordinal);
        Assert.Contains("if (_searchPopupWindow?.IsBlockingMemoryCleanup == true)", appSource, StringComparison.Ordinal);
    }
}
