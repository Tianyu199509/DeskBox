using DeskBox.Services;
using Microsoft.UI.Dispatching;

namespace DeskBox.Tests;

public sealed class DiagnosticsBundleRegressionTests
{
    [Fact]
    public async Task DisplayTopologyTransitionCoordinator_ResumesOnlyWhenAllGateReasonsOpen()
    {
        DispatcherQueueController controller = DispatcherQueueController.CreateOnDedicatedThread();

        try
        {
            int restorePassCount = 0;
            var restoredAfterFullOpen = new TaskCompletionSource<string>(
                TaskCreationOptions.RunContinuationsAsynchronously);

            using var coordinator = new DisplayTopologyTransitionCoordinator(
                controller.DispatcherQueue,
                static () => "sig-1440x900",
                (_, reasons) =>
                {
                    int pass = Interlocked.Increment(ref restorePassCount);
                    if (pass >= 2)
                    {
                        restoredAfterFullOpen.TrySetResult(reasons);
                    }

                    return Task.FromResult(true);
                });

            await EnqueueAsync(controller.DispatcherQueue, () =>
            {
                coordinator.Gate.Close(DisplayTopologyGateReason.SessionLocked);
                coordinator.Gate.Close(DisplayTopologyGateReason.UserInteraction);
                coordinator.RequestRestore("display-area-watcher");

                // Opening UserInteraction while SessionLocked is still closed must
                // NOT unpark or run any restore pass.
                coordinator.Gate.Open(DisplayTopologyGateReason.UserInteraction);
            });

            await Task.Delay(450);
            Assert.Equal(0, Volatile.Read(ref restorePassCount));

            await EnqueueAsync(controller.DispatcherQueue, () =>
            {
                coordinator.Gate.Open(DisplayTopologyGateReason.SessionLocked);
            });

            string completedReasons = await restoredAfterFullOpen.Task.WaitAsync(TimeSpan.FromSeconds(4));
            Assert.Contains("display-area-watcher", completedReasons, StringComparison.Ordinal);
            Assert.Contains("gate-open", completedReasons, StringComparison.Ordinal);
            Assert.True(Volatile.Read(ref restorePassCount) >= 2);
        }
        finally
        {
            await controller.ShutdownQueueAsync();
        }
    }

    [Fact]
    public void PruneStaleInstallerDirectories_ReturnsCleanlyWhenUpdatesRootDoesNotExist()
    {
        string nonExistentRoot = Path.Combine(
            Path.GetTempPath(),
            "DeskBox-MissingUpdatesRoot-" + Guid.NewGuid().ToString("N"));
        Assert.False(Directory.Exists(nonExistentRoot));

        var service = new AppUpdateService(updateRootPath: nonExistentRoot);
        Exception? exception = Record.Exception(service.PruneStaleInstallerDirectories);

        Assert.Null(exception);
        Assert.False(Directory.Exists(nonExistentRoot));
    }

    private static Task EnqueueAsync(DispatcherQueue queue, Action action)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!queue.TryEnqueue(() =>
            {
                try
                {
                    action();
                    tcs.SetResult();
                }
                catch (Exception ex)
                {
                    tcs.SetException(ex);
                }
            }))
        {
            tcs.SetException(new InvalidOperationException("DispatcherQueue rejected work item."));
        }

        return tcs.Task;
    }
}
