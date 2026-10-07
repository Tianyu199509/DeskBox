using DeskBox.Services;

namespace DeskBox.Tests;

public sealed class RuntimeActivityPolicyTests
{
    [Fact]
    public void VisibleIdleMemoryTracker_TriggersAfterThirtySecondsAndRespectsCooldown()
    {
        var tracker = new VisibleIdleMemoryTracker(
            TimeSpan.FromSeconds(30),
            TimeSpan.FromSeconds(60));
        DateTimeOffset start = new(2026, 8, 11, 10, 0, 0, TimeSpan.Zero);

        Assert.False(tracker.Observe(start, isEligible: true));
        Assert.False(tracker.Observe(start.AddSeconds(29), isEligible: true));
        Assert.True(tracker.Observe(start.AddSeconds(30), isEligible: true));
        tracker.CommitMaintenance(start.AddSeconds(30));
        Assert.False(tracker.Observe(start.AddSeconds(89), isEligible: true));
        Assert.True(tracker.Observe(start.AddSeconds(90), isEligible: true));
    }

    [Fact]
    public void VisibleIdleMemoryTracker_DueObservationDoesNotConsumeCooldown()
    {
        var tracker = new VisibleIdleMemoryTracker(
            TimeSpan.FromSeconds(30),
            TimeSpan.FromSeconds(60));
        DateTimeOffset start = new(2026, 8, 11, 10, 0, 0, TimeSpan.Zero);

        Assert.False(tracker.Observe(start, isEligible: true));
        Assert.True(tracker.Observe(start.AddSeconds(30), isEligible: true));

        // The caller found no useful maintenance work. The next five-second
        // timer tick must be allowed to retry instead of waiting a full cooldown.
        Assert.True(tracker.Observe(start.AddSeconds(35), isEligible: true));

        tracker.CommitMaintenance(start.AddSeconds(35));
        Assert.False(tracker.Observe(start.AddSeconds(94), isEligible: true));
        Assert.True(tracker.Observe(start.AddSeconds(95), isEligible: true));
    }

    [Fact]
    public void VisibleIdleMemoryTracker_RestartsIdleWindowAfterActivity()
    {
        var tracker = new VisibleIdleMemoryTracker(
            TimeSpan.FromSeconds(30),
            TimeSpan.FromSeconds(60));
        DateTimeOffset start = new(2026, 8, 11, 10, 0, 0, TimeSpan.Zero);

        Assert.False(tracker.Observe(start, isEligible: true));
        Assert.False(tracker.Observe(start.AddSeconds(20), isEligible: false));
        Assert.False(tracker.Observe(start.AddSeconds(21), isEligible: true));
        Assert.False(tracker.Observe(start.AddSeconds(50), isEligible: true));
        Assert.True(tracker.Observe(start.AddSeconds(51), isEligible: true));
    }

    [Fact]
    public void VisibleIdleMemoryTracker_ReconfigureRestartsTheIdleWindow()
    {
        var tracker = new VisibleIdleMemoryTracker(
            TimeSpan.FromSeconds(30),
            TimeSpan.FromSeconds(60));
        DateTimeOffset start = new(2026, 8, 11, 10, 0, 0, TimeSpan.Zero);

        Assert.False(tracker.Observe(start, isEligible: true));
        tracker.Configure(
            TimeSpan.FromMinutes(10),
            TimeSpan.FromMinutes(10));
        Assert.False(tracker.Observe(start.AddMinutes(10), isEligible: true));
        Assert.False(tracker.Observe(
            start.AddMinutes(19).AddSeconds(59),
            isEligible: true));
        Assert.True(tracker.Observe(start.AddMinutes(20), isEligible: true));
    }

    [Fact]
    public void BackgroundMemorySchedule_AddsDeepFinalizerAfterBoundedCacheStage()
    {
        TimeSpan hiddenDuration = TimeSpan.FromSeconds(30);
        BackgroundMemoryCleanupStage due =
            MemoryCleanupPolicy.GetDueBackgroundStages(
                hiddenDuration,
                softDelaySeconds: 30,
                deepDelaySeconds: 300,
                completedStages: BackgroundMemoryCleanupStage.None);

        Assert.Equal(
            BackgroundMemoryCleanupStage.SoftCache |
            BackgroundMemoryCleanupStage.DeepFinalizerCollection,
            due);

        BackgroundMemoryCleanupStage noStageStillDue =
            MemoryCleanupPolicy.GetDueBackgroundStages(
                hiddenDuration,
                softDelaySeconds: 30,
                deepDelaySeconds: 300,
            completedStages: BackgroundMemoryCleanupStage.SoftCache);
        Assert.Equal(
            BackgroundMemoryCleanupStage.DeepFinalizerCollection,
            noStageStillDue);
    }

    [Fact]
    public void BackgroundMemorySchedule_WaitsForNearestOutstandingStage()
    {
        TimeSpan? firstDelay =
            MemoryCleanupPolicy.GetDelayUntilNextBackgroundStage(
                hiddenDuration: TimeSpan.FromSeconds(29),
                softDelaySeconds: 30,
                deepDelaySeconds: 300,
                completedStages: BackgroundMemoryCleanupStage.None);
        Assert.Equal(TimeSpan.FromSeconds(1), firstDelay);

        TimeSpan? finalizerDelay =
            MemoryCleanupPolicy.GetDelayUntilNextBackgroundStage(
                hiddenDuration: TimeSpan.FromSeconds(30),
                softDelaySeconds: 30,
                deepDelaySeconds: 300,
                completedStages: BackgroundMemoryCleanupStage.SoftCache);
        Assert.Equal(TimeSpan.Zero, finalizerDelay);

        TimeSpan? deepDelay =
            MemoryCleanupPolicy.GetDelayUntilNextBackgroundStage(
                hiddenDuration: TimeSpan.FromSeconds(30),
                softDelaySeconds: 30,
                deepDelaySeconds: 300,
                completedStages: BackgroundMemoryCleanupStage.SoftCache |
                    BackgroundMemoryCleanupStage.DeepFinalizerCollection);
        Assert.Equal(TimeSpan.FromSeconds(270), deepDelay);
    }

    [Theory]
    [InlineData(1, 5)]
    [InlineData(2, 10)]
    [InlineData(3, 20)]
    [InlineData(4, 30)]
    [InlineData(12, 30)]
    public void BackgroundMemoryRetryDelay_UsesCappedExponentialBackoff(
        int attemptNumber,
        int expectedSeconds)
    {
        Assert.Equal(
            TimeSpan.FromSeconds(expectedSeconds),
            MemoryCleanupPolicy.GetCappedBackgroundRetryDelay(
                attemptNumber,
                initialDelaySeconds: 5,
                maximumDelaySeconds: 30));
    }

    [Fact]
    public void WidgetMemoryVisibilitySnapshot_UsesNativeVisibilityForCleanup()
    {
        var nativeHidden = new WidgetMemoryVisibilitySnapshot(
            LoadedWindowCount: 1,
            LogicalVisibleCount: 1,
            NativeVisibleCount: 0);
        var nativeVisible = new WidgetMemoryVisibilitySnapshot(
            LoadedWindowCount: 1,
            LogicalVisibleCount: 0,
            NativeVisibleCount: 1);

        Assert.False(nativeHidden.HasNativeVisibleWidgets);
        Assert.True(nativeVisible.HasNativeVisibleWidgets);
    }

    [Fact]
    public void WidgetCompactWarmupPolicy_AllowsReadyIdleCollapsedWindow()
    {
        Assert.True(WidgetCompactWarmupPolicy.CanRun(CreateWarmupSnapshot()));
    }

    [Fact]
    public void MemoryCleanupPolicy_RequiresVisibleInactiveUiForThirtySecondTracker()
    {
        var snapshot = new MemoryCleanupActivitySnapshot(
            HasVisibleWidgets: true,
            IsWidgetInteractionActive: false,
            IsSettingsOpen: false,
            IsOnboardingOpen: false,
            IsSearchPopupVisible: false,
            IsDeskBoxForeground: false,
            IsPointerOverDeskBox: false);

        Assert.True(MemoryCleanupPolicy.IsVisibleIdleCandidate(snapshot));
        Assert.False(MemoryCleanupPolicy.IsVisibleIdleCandidate(
            snapshot with { IsPointerOverDeskBox = true }));
        Assert.False(MemoryCleanupPolicy.IsVisibleIdleCandidate(
            snapshot with { IsDeskBoxForeground = true }));
        Assert.False(MemoryCleanupPolicy.IsVisibleIdleCandidate(
            snapshot with { HasVisibleWidgets = false }));
    }

    [Fact]
    public void MemoryCleanupPolicy_AllowsDeepCollectionOnlyWhenTransientUiIsIdle()
    {
        var snapshot = new MemoryCleanupActivitySnapshot(
            HasVisibleWidgets: true,
            IsWidgetInteractionActive: false,
            IsSettingsOpen: false,
            IsOnboardingOpen: false,
            IsSearchPopupVisible: false,
            IsDeskBoxForeground: false,
            IsPointerOverDeskBox: false);

        Assert.True(MemoryCleanupPolicy.IsDeepCleanupCandidate(snapshot));
        Assert.False(MemoryCleanupPolicy.IsDeepCleanupCandidate(
            snapshot with { IsSettingsOpen = true }));
        Assert.False(MemoryCleanupPolicy.IsDeepCleanupCandidate(
            snapshot with { IsWidgetInteractionActive = true }));
        Assert.False(MemoryCleanupPolicy.IsDeepCleanupCandidate(
            snapshot with { IsPointerOverDeskBox = true }));
    }

    [Theory]
    [InlineData(true, 4, 4, true)]
    [InlineData(false, 4, 4, false)]
    [InlineData(true, 3, 4, false)]
    [InlineData(true, -1, 0, false)]
    public void WidgetCompactWarmupPolicy_RejectsReadinessFromAnOlderMemoryEpoch(
        bool isWarmed,
        long warmedEpoch,
        long memoryCleanupEpoch,
        bool expected)
    {
        Assert.Equal(
            expected,
            WidgetCompactWarmupPolicy.IsExpansionReady(
                isWarmed,
                warmedEpoch,
                memoryCleanupEpoch));
    }

    [Theory]
    [InlineData(nameof(WidgetCompactWarmupSnapshot.IsCollapseInitialized))]
    [InlineData(nameof(WidgetCompactWarmupSnapshot.IsCollapsed))]
    [InlineData(nameof(WidgetCompactWarmupSnapshot.IsExpansionWarmed))]
    [InlineData(nameof(WidgetCompactWarmupSnapshot.IsClosing))]
    [InlineData(nameof(WidgetCompactWarmupSnapshot.IsAnimationActive))]
    [InlineData(nameof(WidgetCompactWarmupSnapshot.IsPointerOverWidget))]
    [InlineData(nameof(WidgetCompactWarmupSnapshot.HasActiveInteraction))]
    [InlineData(nameof(WidgetCompactWarmupSnapshot.IsWindowVisible))]
    [InlineData(nameof(WidgetCompactWarmupSnapshot.IsContentReady))]
    [InlineData(nameof(WidgetCompactWarmupSnapshot.IsApplicationIdle))]
    public void WidgetCompactWarmupPolicy_BlocksEveryUnsafeState(string propertyName)
    {
        WidgetCompactWarmupSnapshot snapshot = CreateWarmupSnapshot();
        snapshot = propertyName switch
        {
            nameof(WidgetCompactWarmupSnapshot.IsCollapseInitialized) =>
                snapshot with { IsCollapseInitialized = false },
            nameof(WidgetCompactWarmupSnapshot.IsCollapsed) =>
                snapshot with { IsCollapsed = false },
            nameof(WidgetCompactWarmupSnapshot.IsExpansionWarmed) =>
                snapshot with { IsExpansionWarmed = true },
            nameof(WidgetCompactWarmupSnapshot.IsClosing) =>
                snapshot with { IsClosing = true },
            nameof(WidgetCompactWarmupSnapshot.IsAnimationActive) =>
                snapshot with { IsAnimationActive = true },
            nameof(WidgetCompactWarmupSnapshot.IsPointerOverWidget) =>
                snapshot with { IsPointerOverWidget = true },
            nameof(WidgetCompactWarmupSnapshot.HasActiveInteraction) =>
                snapshot with { HasActiveInteraction = true },
            nameof(WidgetCompactWarmupSnapshot.IsWindowVisible) =>
                snapshot with { IsWindowVisible = false },
            nameof(WidgetCompactWarmupSnapshot.IsContentReady) =>
                snapshot with { IsContentReady = false },
            nameof(WidgetCompactWarmupSnapshot.IsApplicationIdle) =>
                snapshot with { IsApplicationIdle = false },
            _ => throw new ArgumentOutOfRangeException(nameof(propertyName))
        };

        Assert.False(WidgetCompactWarmupPolicy.CanRun(snapshot));
    }

    [Fact]
    public void WidgetCompactWarmupPolicy_OnlyWaitsOutTransientBlockers()
    {
        // A hidden window cannot warm and stays unrunnable until the show
        // path re-arms an urgent run, so a deferred run must stop instead
        // of polling.
        Assert.False(WidgetCompactWarmupPolicy.ShouldKeepWaiting(
            CreateWarmupSnapshot() with { IsWindowVisible = false }));

        // Every other gate clears on its own (idle arrives, the pointer
        // leaves, content finishes loading), so a visible window keeps
        // waiting.
        Assert.True(WidgetCompactWarmupPolicy.ShouldKeepWaiting(
            CreateWarmupSnapshot() with { IsApplicationIdle = false }));
        Assert.True(WidgetCompactWarmupPolicy.ShouldKeepWaiting(
            CreateWarmupSnapshot() with { IsPointerOverWidget = true }));
        Assert.True(WidgetCompactWarmupPolicy.ShouldKeepWaiting(
            CreateWarmupSnapshot() with { IsContentReady = false }));
    }

    private static WidgetCompactWarmupSnapshot CreateWarmupSnapshot()
    {
        return new WidgetCompactWarmupSnapshot(
            IsCollapseInitialized: true,
            IsCollapsed: true,
            IsExpansionWarmed: false,
            IsClosing: false,
            IsAnimationActive: false,
            IsPointerOverWidget: false,
            HasActiveInteraction: false,
            IsWindowVisible: true,
            IsContentReady: true,
            IsApplicationIdle: true);
    }
}
