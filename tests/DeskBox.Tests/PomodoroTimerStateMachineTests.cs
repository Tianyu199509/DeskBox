using DeskBox.Models;
using DeskBox.Services;

namespace DeskBox.Tests;

public sealed class PomodoroTimerStateMachineTests
{
    private static readonly DateTimeOffset FixedNow =
        new(2026, 9, 3, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public void NewTimer_StartsPausedInTwentyFiveMinuteFocusPhase()
    {
        var config = new WidgetConfig();
        var timer = new PomodoroTimerStateMachine(config, FixedNow);

        PomodoroTimerSnapshot snapshot = timer.GetSnapshot(FixedNow);

        Assert.Equal(PomodoroTimerPhase.Focus, snapshot.Phase);
        Assert.False(snapshot.IsRunning);
        Assert.Equal(TimeSpan.FromMinutes(25), snapshot.Remaining);
        Assert.Equal(0, snapshot.CompletedFocusRounds);
        Assert.Null(snapshot.DeadlineUtc);
        Assert.False(timer.ShouldPersistRestore);
    }

    [Fact]
    public void Start_PersistsUtcDeadlineAndRunningState()
    {
        DateTimeOffset localNow = FixedNow.ToOffset(TimeSpan.FromHours(8));
        var config = new WidgetConfig();
        var timer = new PomodoroTimerStateMachine(config, localNow);

        PomodoroTimerUpdate update = timer.Start(localNow);

        Assert.Equal(PomodoroTimerTransition.Started, update.Transition);
        Assert.True(update.ShouldPersist);
        Assert.True(update.Snapshot.IsRunning);
        Assert.Equal(FixedNow.AddMinutes(25), update.Snapshot.DeadlineUtc);
        Assert.Equal("True", config.Metadata["Pomodoro.Running"]);
        Assert.Equal(
            FixedNow.AddMinutes(25).ToString("O"),
            config.Metadata["Pomodoro.DeadlineUtc"]);
        Assert.DoesNotContain("Pomodoro.RemainingTicks", config.Metadata.Keys);
    }

    [Fact]
    public void OrdinaryTick_UsesDeadlineWithoutChangingMetadata()
    {
        var config = new WidgetConfig();
        var timer = new PomodoroTimerStateMachine(config, FixedNow);
        timer.Start(FixedNow);
        Dictionary<string, string> before = new(config.Metadata);

        PomodoroTimerUpdate update = timer.Tick(FixedNow.AddMinutes(7));

        Assert.Equal(PomodoroTimerTransition.None, update.Transition);
        Assert.False(update.ShouldPersist);
        Assert.Equal(TimeSpan.FromMinutes(18), update.Snapshot.Remaining);
        Assert.Equal(before.Count, config.Metadata.Count);
        Assert.All(before, pair => Assert.Equal(pair.Value, config.Metadata[pair.Key]));
    }

    [Fact]
    public void Pause_CapturesRemainingTimeAndCanResumeFromIt()
    {
        var config = new WidgetConfig();
        var timer = new PomodoroTimerStateMachine(config, FixedNow);
        timer.Start(FixedNow);

        PomodoroTimerUpdate paused = timer.Pause(FixedNow.AddMinutes(15));

        Assert.Equal(PomodoroTimerTransition.Paused, paused.Transition);
        Assert.False(paused.Snapshot.IsRunning);
        Assert.Equal(TimeSpan.FromMinutes(10), paused.Snapshot.Remaining);
        Assert.Equal(
            TimeSpan.FromMinutes(10).Ticks,
            long.Parse(config.Metadata["Pomodoro.RemainingTicks"]));

        PomodoroTimerUpdate resumed = timer.Start(FixedNow.AddHours(1));

        Assert.Equal(PomodoroTimerTransition.Started, resumed.Transition);
        Assert.Equal(FixedNow.AddHours(1).AddMinutes(10), resumed.Snapshot.DeadlineUtc);
    }

    [Fact]
    public void Reset_RestoresCurrentPhaseWithoutClearingCompletedRounds()
    {
        var config = new WidgetConfig();
        var timer = new PomodoroTimerStateMachine(config, FixedNow);
        timer.Start(FixedNow);
        timer.Tick(FixedNow.AddMinutes(25));
        timer.Start(FixedNow.AddMinutes(25));

        PomodoroTimerUpdate reset = timer.Reset(FixedNow.AddMinutes(27));

        Assert.Equal(PomodoroTimerTransition.Reset, reset.Transition);
        Assert.Equal(PomodoroTimerPhase.ShortBreak, reset.Snapshot.Phase);
        Assert.False(reset.Snapshot.IsRunning);
        Assert.Equal(TimeSpan.FromMinutes(5), reset.Snapshot.Remaining);
        Assert.Equal(1, reset.Snapshot.CompletedFocusRounds);
    }

    [Fact]
    public void Skip_SwitchesPhasePausedAndAdvancesPastSkippedFocusRound()
    {
        var config = new WidgetConfig();
        var timer = new PomodoroTimerStateMachine(config, FixedNow);
        timer.Start(FixedNow);

        PomodoroTimerUpdate shortBreakUpdate = timer.Skip(FixedNow.AddMinutes(10));
        PomodoroTimerUpdate focusUpdate = timer.Skip(FixedNow);

        Assert.Equal(PomodoroTimerTransition.Skipped, shortBreakUpdate.Transition);
        Assert.Equal(PomodoroTimerPhase.ShortBreak, shortBreakUpdate.Snapshot.Phase);
        Assert.False(shortBreakUpdate.Snapshot.IsRunning);
        Assert.Equal(TimeSpan.FromMinutes(5), shortBreakUpdate.Snapshot.Remaining);
        Assert.Equal(1, shortBreakUpdate.Snapshot.CompletedFocusRounds);
        Assert.Equal(PomodoroTimerPhase.Focus, focusUpdate.Snapshot.Phase);
        Assert.Equal(1, focusUpdate.Snapshot.CompletedFocusRounds);
    }

    [Fact]
    public void NaturalFocusCompletion_IncrementsRoundAndPausesShortBreak()
    {
        var config = new WidgetConfig();
        var timer = new PomodoroTimerStateMachine(config, FixedNow);
        timer.Start(FixedNow);

        PomodoroTimerUpdate update = timer.Tick(FixedNow.AddMinutes(25));

        Assert.Equal(PomodoroTimerTransition.FocusCompleted, update.Transition);
        Assert.True(update.ShouldPersist);
        Assert.Equal(PomodoroTimerPhase.ShortBreak, update.Snapshot.Phase);
        Assert.False(update.Snapshot.IsRunning);
        Assert.Equal(TimeSpan.FromMinutes(5), update.Snapshot.Remaining);
        Assert.Equal(1, update.Snapshot.CompletedFocusRounds);
        Assert.Equal("1", config.Metadata["Pomodoro.CompletedFocusRounds"]);
        Assert.DoesNotContain("Pomodoro.DeadlineUtc", config.Metadata.Keys);

        PomodoroTimerUpdate repeated = timer.Tick(FixedNow.AddHours(1));

        Assert.Equal(PomodoroTimerTransition.None, repeated.Transition);
        Assert.Equal(1, repeated.Snapshot.CompletedFocusRounds);
    }

    [Fact]
    public void NaturalShortBreakCompletion_PausesNextFocusRound()
    {
        var config = new WidgetConfig();
        var timer = new PomodoroTimerStateMachine(config, FixedNow);
        timer.Skip(FixedNow);
        timer.Start(FixedNow);

        PomodoroTimerUpdate update = timer.Tick(FixedNow.AddMinutes(5));

        Assert.Equal(PomodoroTimerTransition.ShortBreakCompleted, update.Transition);
        Assert.Equal(PomodoroTimerPhase.Focus, update.Snapshot.Phase);
        Assert.False(update.Snapshot.IsRunning);
        Assert.Equal(TimeSpan.FromMinutes(25), update.Snapshot.Remaining);
        Assert.Equal(1, update.Snapshot.CompletedFocusRounds);
    }

    [Fact]
    public void FinalFocusCompletion_EntersLongBreakWithConfiguredDuration()
    {
        var config = new WidgetConfig();
        var timer = new PomodoroTimerStateMachine(
            config,
            FixedNow,
            focusMinutes: 10,
            shortBreakMinutes: 3,
            longBreakMinutes: 12,
            roundCount: 2);
        timer.Skip(FixedNow);
        timer.Skip(FixedNow);
        timer.Start(FixedNow);

        PomodoroTimerUpdate update = timer.Tick(FixedNow.AddMinutes(10));

        Assert.Equal(PomodoroTimerTransition.FocusCompleted, update.Transition);
        Assert.Equal(PomodoroTimerPhase.LongBreak, update.Snapshot.Phase);
        Assert.False(update.Snapshot.IsRunning);
        Assert.Equal(TimeSpan.FromMinutes(12), update.Snapshot.Remaining);
        Assert.Equal(2, update.Snapshot.CompletedFocusRounds);
    }

    [Fact]
    public void LongBreakCompletion_ResetsCycleToFirstFocusRound()
    {
        var config = new WidgetConfig();
        var timer = new PomodoroTimerStateMachine(
            config,
            FixedNow,
            focusMinutes: 10,
            shortBreakMinutes: 3,
            longBreakMinutes: 12,
            roundCount: 1);
        timer.Skip(FixedNow);
        timer.Start(FixedNow);

        PomodoroTimerUpdate update = timer.Tick(FixedNow.AddMinutes(12));

        Assert.Equal(
            PomodoroTimerTransition.LongBreakCompleted,
            update.Transition);
        Assert.Equal(PomodoroTimerPhase.Focus, update.Snapshot.Phase);
        Assert.False(update.Snapshot.IsRunning);
        Assert.Equal(TimeSpan.FromMinutes(10), update.Snapshot.Remaining);
        Assert.Equal(0, update.Snapshot.CompletedFocusRounds);
    }

    [Fact]
    public void Skip_FinalFocusAndLongBreak_ResetsCycleWithoutAutoStarting()
    {
        var config = new WidgetConfig();
        var timer = new PomodoroTimerStateMachine(
            config,
            FixedNow,
            roundCount: 1);

        PomodoroTimerUpdate longBreak = timer.Skip(FixedNow);
        PomodoroTimerUpdate firstFocus = timer.Skip(FixedNow);

        Assert.Equal(PomodoroTimerPhase.LongBreak, longBreak.Snapshot.Phase);
        Assert.Equal(1, longBreak.Snapshot.CompletedFocusRounds);
        Assert.Equal(PomodoroTimerPhase.Focus, firstFocus.Snapshot.Phase);
        Assert.False(firstFocus.Snapshot.IsRunning);
        Assert.Equal(0, firstFocus.Snapshot.CompletedFocusRounds);
    }

    [Fact]
    public void UpdateSettings_ReclassifiesCompletedCycleAndUsesLongBreakDuration()
    {
        var config = new WidgetConfig();
        var timer = new PomodoroTimerStateMachine(
            config,
            FixedNow,
            roundCount: 4);
        timer.Skip(FixedNow);

        PomodoroTimerUpdate update = timer.UpdateSettings(
            focusMinutes: 30,
            shortBreakMinutes: 7,
            longBreakMinutes: 20,
            roundCount: 1,
            utcNow: FixedNow);

        Assert.True(update.ShouldPersist);
        Assert.Equal(PomodoroTimerPhase.LongBreak, update.Snapshot.Phase);
        Assert.Equal(TimeSpan.FromMinutes(20), update.Snapshot.Remaining);
        Assert.Equal(TimeSpan.FromMinutes(20), timer.CurrentPhaseDuration);
        Assert.Equal(1, update.Snapshot.CompletedFocusRounds);
        Assert.Equal(1, timer.RoundCount);
    }

    [Fact]
    public void Restore_RunningTimerUsesPersistedUtcDeadline()
    {
        var original = new WidgetConfig();
        var running = new PomodoroTimerStateMachine(original, FixedNow);
        running.Start(FixedNow);
        var restoredConfig = new WidgetConfig
        {
            Metadata = new Dictionary<string, string>(original.Metadata)
        };

        var restored = new PomodoroTimerStateMachine(
            restoredConfig,
            FixedNow.AddMinutes(9));
        PomodoroTimerSnapshot snapshot = restored.GetSnapshot(FixedNow.AddMinutes(9));

        Assert.False(restored.ShouldPersistRestore);
        Assert.True(snapshot.IsRunning);
        Assert.Equal(TimeSpan.FromMinutes(16), snapshot.Remaining);
        Assert.Equal(FixedNow.AddMinutes(25), snapshot.DeadlineUtc);
    }

    [Fact]
    public void Restore_PausedTimerPreservesRemainingTime()
    {
        var original = new WidgetConfig();
        var timer = new PomodoroTimerStateMachine(original, FixedNow);
        timer.Start(FixedNow);
        timer.Pause(FixedNow.AddMinutes(6));
        var restoredConfig = new WidgetConfig
        {
            Metadata = new Dictionary<string, string>(original.Metadata)
        };

        var restored = new PomodoroTimerStateMachine(
            restoredConfig,
            FixedNow.AddDays(1));
        PomodoroTimerSnapshot snapshot = restored.GetSnapshot(FixedNow.AddDays(1));

        Assert.False(restored.ShouldPersistRestore);
        Assert.Equal(PomodoroTimerPhase.Focus, snapshot.Phase);
        Assert.False(snapshot.IsRunning);
        Assert.Equal(TimeSpan.FromMinutes(19), snapshot.Remaining);
    }

    [Theory]
    [InlineData(1, PomodoroTimerPhase.ShortBreak)]
    [InlineData(4, PomodoroTimerPhase.LongBreak)]
    public void Restore_LegacyBreakMigratesToCycleAwareBreakPhase(
        int completedFocusRounds,
        PomodoroTimerPhase expectedPhase)
    {
        var config = new WidgetConfig
        {
            Metadata = new Dictionary<string, string>
            {
                ["Pomodoro.Version"] = "1",
                ["Pomodoro.Phase"] = "Break",
                ["Pomodoro.Running"] = "False",
                ["Pomodoro.RemainingTicks"] = TimeSpan.FromMinutes(5).Ticks.ToString(),
                ["Pomodoro.CompletedFocusRounds"] = completedFocusRounds.ToString()
            }
        };

        var timer = new PomodoroTimerStateMachine(config, FixedNow);
        PomodoroTimerSnapshot snapshot = timer.GetSnapshot(FixedNow);

        Assert.True(timer.WasMetadataMigrated);
        Assert.True(timer.ShouldPersistRestore);
        Assert.Equal(expectedPhase, snapshot.Phase);
        Assert.Equal(TimeSpan.FromMinutes(5), snapshot.Remaining);
        Assert.Equal("2", config.Metadata["Pomodoro.Version"]);
        Assert.Equal(expectedPhase.ToString(), config.Metadata["Pomodoro.Phase"]);
    }

    [Fact]
    public void Restore_ExpiredFocusCompletesExactlyOnceAndStaysPaused()
    {
        var original = new WidgetConfig();
        var running = new PomodoroTimerStateMachine(original, FixedNow);
        running.Start(FixedNow);
        var restoredConfig = new WidgetConfig
        {
            Metadata = new Dictionary<string, string>(original.Metadata)
        };

        var restored = new PomodoroTimerStateMachine(
            restoredConfig,
            FixedNow.AddDays(2));
        PomodoroTimerSnapshot snapshot = restored.GetSnapshot(FixedNow.AddDays(2));

        Assert.True(restored.ShouldPersistRestore);
        Assert.Equal(PomodoroTimerTransition.FocusCompleted, restored.RestoreTransition);
        Assert.Equal(PomodoroTimerPhase.ShortBreak, snapshot.Phase);
        Assert.False(snapshot.IsRunning);
        Assert.Equal(TimeSpan.FromMinutes(5), snapshot.Remaining);
        Assert.Equal(1, snapshot.CompletedFocusRounds);
    }

    [Fact]
    public void Restore_ExpiredShortBreakPreservesCompletedFocusRounds()
    {
        var original = new WidgetConfig();
        var timer = new PomodoroTimerStateMachine(original, FixedNow);
        timer.Skip(FixedNow);
        timer.Start(FixedNow);
        var restoredConfig = new WidgetConfig
        {
            Metadata = new Dictionary<string, string>(original.Metadata)
        };

        var restored = new PomodoroTimerStateMachine(
            restoredConfig,
            FixedNow.AddMinutes(6));
        PomodoroTimerSnapshot snapshot = restored.GetSnapshot(FixedNow.AddMinutes(6));

        Assert.Equal(
            PomodoroTimerTransition.ShortBreakCompleted,
            restored.RestoreTransition);
        Assert.Equal(PomodoroTimerPhase.Focus, snapshot.Phase);
        Assert.False(snapshot.IsRunning);
        Assert.Equal(1, snapshot.CompletedFocusRounds);
    }

    [Fact]
    public void Restore_CorruptMetadataResetsSafelyAndPreservesUnrelatedValues()
    {
        var config = new WidgetConfig
        {
            Metadata = new Dictionary<string, string>
            {
                ["Pomodoro.Version"] = "1",
                ["Pomodoro.Phase"] = "Focus",
                ["Pomodoro.Running"] = "True",
                ["Pomodoro.DeadlineUtc"] = "not-a-date",
                ["Pomodoro.CompletedFocusRounds"] = "broken",
                ["Other.Feature"] = "keep"
            }
        };

        var timer = new PomodoroTimerStateMachine(config, FixedNow);
        PomodoroTimerSnapshot snapshot = timer.GetSnapshot(FixedNow);

        Assert.True(timer.WasMetadataRecovered);
        Assert.True(timer.ShouldPersistRestore);
        Assert.Equal(PomodoroTimerPhase.Focus, snapshot.Phase);
        Assert.False(snapshot.IsRunning);
        Assert.Equal(TimeSpan.FromMinutes(25), snapshot.Remaining);
        Assert.Equal(0, snapshot.CompletedFocusRounds);
        Assert.Equal("keep", config.Metadata["Other.Feature"]);
        Assert.DoesNotContain("Pomodoro.DeadlineUtc", config.Metadata.Keys);
    }

    [Fact]
    public void Restore_ImpossibleFutureDeadlineFallsBackToSafeDefault()
    {
        var config = new WidgetConfig
        {
            Metadata = new Dictionary<string, string>
            {
                ["Pomodoro.Version"] = "1",
                ["Pomodoro.Phase"] = "Focus",
                ["Pomodoro.Running"] = "True",
                ["Pomodoro.DeadlineUtc"] = FixedNow.AddHours(3).ToString("O"),
                ["Pomodoro.CompletedFocusRounds"] = "4"
            }
        };

        var timer = new PomodoroTimerStateMachine(config, FixedNow);
        PomodoroTimerSnapshot snapshot = timer.GetSnapshot(FixedNow);

        Assert.True(timer.WasMetadataRecovered);
        Assert.Equal(PomodoroTimerPhase.Focus, snapshot.Phase);
        Assert.False(snapshot.IsRunning);
        Assert.Equal(TimeSpan.FromMinutes(25), snapshot.Remaining);
        Assert.Equal(0, snapshot.CompletedFocusRounds);
    }
}
