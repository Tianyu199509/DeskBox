using DeskBox.Models;
using DeskBox.Services;
using DeskBox.ViewModels;

namespace DeskBox.Tests;

public sealed class PomodoroWidgetViewModelTests
{
    private const string CompletionAlertMetadataKey =
        "Pomodoro.CompletionAlertActive";

    [Fact]
    public async Task Constructor_AllowsWorkerThreadWithoutDispatcherQueue()
    {
        Task worker = Task.Run(async () =>
        {
            var config = new WidgetConfig
            {
                Id = "pomodoro-worker-thread",
                Name = "Pomodoro",
                WidgetKind = WidgetKind.Pomodoro
            };
            var localizationService = TestServices.CreateLocalizationService();

            using var viewModel = new PomodoroWidgetViewModel(
                config,
                localizationService);

            await viewModel.InitializeAsync();
            viewModel.StartPause();
            viewModel.Reset();
        });

        await worker.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task ShortBreak_KeepsCompletedRoundUntilBreakEnds()
    {
        DateTimeOffset now = new(2026, 9, 4, 8, 0, 0, TimeSpan.Zero);
        var config = new WidgetConfig
        {
            Id = "pomodoro-short-break",
            Name = "Pomodoro",
            WidgetKind = WidgetKind.Pomodoro
        };
        using var viewModel = new PomodoroWidgetViewModel(
            config,
            TestServices.CreateLocalizationService(),
            utcNowProvider: () => now);
        await viewModel.InitializeAsync();

        viewModel.Skip();

        Assert.Equal(PomodoroTimerPhase.ShortBreak, viewModel.Phase);
        Assert.False(viewModel.IsFocusPhase);
        Assert.True(viewModel.IsShortBreakPhase);
        Assert.False(viewModel.IsLongBreakPhase);
        Assert.True(viewModel.IsBreakPhase);
        Assert.Equal(1, viewModel.RoundNumber);
        Assert.Equal(4, viewModel.RoundCount);
        Assert.Equal(TimeSpan.FromMinutes(5), viewModel.Remaining);
        Assert.Equal(TimeSpan.FromMinutes(5), viewModel.PhaseDuration);
        Assert.Equal(0d, viewModel.Progress);

        viewModel.Skip();

        Assert.Equal(PomodoroTimerPhase.Focus, viewModel.Phase);
        Assert.Equal(2, viewModel.RoundNumber);
    }

    [Fact]
    public async Task LongBreakCompletion_ReturnsViewModelToFirstRound()
    {
        DateTimeOffset now = new(2026, 9, 4, 8, 0, 0, TimeSpan.Zero);
        var config = new WidgetConfig
        {
            Id = "pomodoro-long-break",
            Name = "Pomodoro",
            WidgetKind = WidgetKind.Pomodoro
        };
        using var viewModel = new PomodoroWidgetViewModel(
            config,
            TestServices.CreateLocalizationService(),
            utcNowProvider: () => now);
        await viewModel.InitializeAsync();

        for (int round = 1; round < viewModel.RoundCount; round++)
        {
            viewModel.Skip();
            viewModel.Skip();
        }

        viewModel.Skip();
        Assert.Equal(PomodoroTimerPhase.LongBreak, viewModel.Phase);
        Assert.True(viewModel.IsLongBreakPhase);
        Assert.Equal(viewModel.RoundCount, viewModel.RoundNumber);

        viewModel.StartPause();
        now = now.AddMinutes(PomodoroSettingsPolicy.DefaultLongBreakMinutes);
        await viewModel.RefreshAsync();

        Assert.Equal(PomodoroTimerPhase.Focus, viewModel.Phase);
        Assert.Equal(1, viewModel.RoundNumber);
        Assert.Equal(0, viewModel.CompletedFocusRounds);
        Assert.False(viewModel.IsRunning);
    }

    [Fact]
    public async Task NaturalCompletion_ActivatesPersistentAlertAndRaisesEventOnce()
    {
        DateTimeOffset now = new(2026, 9, 4, 8, 0, 0, TimeSpan.Zero);
        WidgetConfig config = CreateConfig("pomodoro-completion-alert");
        int completionCount = 0;

        using (var viewModel = new PomodoroWidgetViewModel(
                   config,
                   TestServices.CreateLocalizationService(),
                   utcNowProvider: () => now))
        {
            viewModel.CompletionOccurred += (_, _) => completionCount++;
            await viewModel.InitializeAsync();

            viewModel.StartPause();
            now = now.AddMinutes(PomodoroSettingsPolicy.DefaultFocusMinutes);
            await viewModel.RefreshAsync();
            await viewModel.RefreshAsync();

            Assert.True(viewModel.IsCompletionAlertActive);
            Assert.Equal(1, completionCount);
            Assert.Equal(
                bool.TrueString,
                config.Metadata[CompletionAlertMetadataKey]);
        }

        using var restoredViewModel = new PomodoroWidgetViewModel(
            config,
            TestServices.CreateLocalizationService(),
            utcNowProvider: () => now);
        await restoredViewModel.InitializeAsync();

        Assert.True(restoredViewModel.IsCompletionAlertActive);
    }

    [Fact]
    public async Task PauseDeactivationAndCollapse_DoNotClearCompletionAlert()
    {
        DateTimeOffset now = new(2026, 9, 4, 8, 0, 0, TimeSpan.Zero);
        WidgetConfig config = CreateConfig("pomodoro-alert-retention");

        using (var seedViewModel = new PomodoroWidgetViewModel(
                   config,
                   TestServices.CreateLocalizationService(),
                   utcNowProvider: () => now))
        {
            await seedViewModel.InitializeAsync();
            seedViewModel.StartPause();
        }

        config.Metadata[CompletionAlertMetadataKey] = bool.TrueString;
        using var viewModel = new PomodoroWidgetViewModel(
            config,
            TestServices.CreateLocalizationService(),
            utcNowProvider: () => now);
        await viewModel.InitializeAsync();

        Assert.True(viewModel.IsRunning);
        Assert.True(viewModel.IsCompletionAlertActive);

        viewModel.StartPause();
        viewModel.OnDeactivated();
        viewModel.OnWindowVisibilityChanged(false);

        Assert.False(viewModel.IsRunning);
        Assert.True(viewModel.IsCompletionAlertActive);
        Assert.Equal(
            bool.TrueString,
            config.Metadata[CompletionAlertMetadataKey]);
    }

    [Fact]
    public async Task HiddenRunningTimer_CompletesAndRaisesEventOnlyOnce()
    {
        DateTimeOffset now = new(2026, 9, 4, 8, 0, 0, TimeSpan.Zero);
        WidgetConfig config = CreateConfig("pomodoro-hidden-completion");
        using var viewModel = new PomodoroWidgetViewModel(
            config,
            TestServices.CreateLocalizationService(),
            utcNowProvider: () => now);
        int completionCount = 0;
        viewModel.CompletionOccurred += (_, _) => completionCount++;
        await viewModel.InitializeAsync();

        viewModel.StartPause();
        viewModel.OnWindowVisibilityChanged(false);
        now = now.AddMinutes(PomodoroSettingsPolicy.DefaultFocusMinutes);
        await viewModel.RefreshAsync();
        await viewModel.RefreshAsync();

        Assert.False(viewModel.IsRunning);
        Assert.True(viewModel.IsCompletionAlertActive);
        Assert.Equal(1, completionCount);
    }

    [Fact]
    public async Task SettingsChange_DoesNotClearCompletionAlert()
    {
        string tempRoot = Path.Combine(
            Path.GetTempPath(),
            "DeskBox.Tests",
            Guid.NewGuid().ToString("N"));
        var settingsService = new SettingsService(tempRoot);

        try
        {
            WidgetConfig config = CreateConfig(
                "pomodoro-alert-settings",
                completionAlertActive: true);
            using var viewModel = new PomodoroWidgetViewModel(
                config,
                TestServices.CreateLocalizationService(),
                settingsService,
                utcNowProvider: () => new DateTimeOffset(
                    2026,
                    9,
                    4,
                    8,
                    0,
                    0,
                    TimeSpan.Zero));
            await viewModel.InitializeAsync();

            settingsService.Settings.PomodoroRoundCount++;
            settingsService.SaveDebounced();

            Assert.True(viewModel.IsCompletionAlertActive);
            Assert.Equal(
                bool.TrueString,
                config.Metadata[CompletionAlertMetadataKey]);
        }
        finally
        {
            await settingsService.FlushPendingSaveAsync();
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }

    [Fact]
    public async Task StartSkipAndReset_ClearCompletionAlert()
    {
        WidgetConfig startConfig = CreateConfig(
            "pomodoro-alert-start",
            completionAlertActive: true);
        using (var viewModel = new PomodoroWidgetViewModel(
                   startConfig,
                   TestServices.CreateLocalizationService()))
        {
            await viewModel.InitializeAsync();
            viewModel.StartPause();

            Assert.False(viewModel.IsCompletionAlertActive);
            Assert.False(startConfig.Metadata.ContainsKey(
                CompletionAlertMetadataKey));
        }

        WidgetConfig skipConfig = CreateConfig(
            "pomodoro-alert-skip",
            completionAlertActive: true);
        using (var viewModel = new PomodoroWidgetViewModel(
                   skipConfig,
                   TestServices.CreateLocalizationService()))
        {
            await viewModel.InitializeAsync();
            viewModel.Skip();

            Assert.False(viewModel.IsCompletionAlertActive);
            Assert.False(skipConfig.Metadata.ContainsKey(
                CompletionAlertMetadataKey));
        }

        WidgetConfig resetConfig = CreateConfig(
            "pomodoro-alert-reset",
            completionAlertActive: true);
        using var resetViewModel = new PomodoroWidgetViewModel(
            resetConfig,
            TestServices.CreateLocalizationService());
        await resetViewModel.InitializeAsync();
        resetViewModel.Reset();

        Assert.False(resetViewModel.IsCompletionAlertActive);
        Assert.False(resetConfig.Metadata.ContainsKey(
            CompletionAlertMetadataKey));
    }

    private static WidgetConfig CreateConfig(
        string id,
        bool completionAlertActive = false)
    {
        var config = new WidgetConfig
        {
            Id = id,
            Name = "Pomodoro",
            WidgetKind = WidgetKind.Pomodoro
        };
        if (completionAlertActive)
        {
            config.Metadata[CompletionAlertMetadataKey] = bool.TrueString;
        }

        return config;
    }
}
