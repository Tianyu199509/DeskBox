using DeskBox.Models;
using DeskBox.ViewModels;

namespace DeskBox.Tests;

public sealed class PomodoroWidgetViewModelTests
{
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
}
