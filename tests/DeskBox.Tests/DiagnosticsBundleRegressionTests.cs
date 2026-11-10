using System.Runtime.InteropServices;
using DeskBox.Services;
using Microsoft.UI.Dispatching;

namespace DeskBox.Tests;

public sealed class DiagnosticsBundleRegressionTests
{
    [Fact]
    public void AppLifecycleRecoveryWatcher_RoutesGateEventsIndependentlyFromRecoveryReasons()
    {
        string watcher = File.ReadAllText(
            TestPaths.FromRepository("src/DeskBox/Services/AppLifecycleRecoveryWatcher.cs"));

        int resolveGate = watcher.IndexOf(
            "AppLifecycleRecoverySignalClassifier.ResolveGateEvent(",
            StringComparison.Ordinal);
        int raiseGate = watcher.IndexOf(
            "RaiseGateEvent(gateEvent);",
            StringComparison.Ordinal);
        int queueRecovery = watcher.IndexOf(
            "QueueRecovery(recoveryReason);",
            StringComparison.Ordinal);

        Assert.True(resolveGate >= 0, "Expected ResolveGateEvent call in AppLifecycleRecoveryWatcher.");
        Assert.True(raiseGate > resolveGate, "Expected gate event dispatch before recovery handling.");
        Assert.True(queueRecovery > raiseGate, "Expected recovery queueing after gate dispatch.");
        Assert.DoesNotContain(
            "else if (message == AppLifecycleRecoverySignalClassifier.WmWtsSessionChange)",
            watcher,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task DisplayTopologyTransitionCoordinator_ResumesOnlyWhenAllGateReasonsOpen()
    {
        DispatcherQueueController? controller;
        try
        {
            controller = DispatcherQueueController.CreateOnDedicatedThread();
        }
        catch (COMException)
        {
            return;
        }

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
    public void ElementBackdropTargetBridge_ConfigurationChangedDoesNotResetBackdropTarget()
    {
        string backdropFile = File.ReadAllText(
            TestPaths.FromRepository("src/DeskBox/Services/WidgetMaterialSystemBackdrop.cs"));

        int bridgeStart = backdropFile.IndexOf(
            "internal sealed partial class ElementBackdropTargetBridge",
            StringComparison.Ordinal);
        int configStart = backdropFile.IndexOf(
            "protected override void OnDefaultSystemBackdropConfigurationChanged(",
            bridgeStart,
            StringComparison.Ordinal);
        int disconnectStart = backdropFile.IndexOf(
            "protected override void OnTargetDisconnected(",
            configStart,
            StringComparison.Ordinal);

        Assert.True(bridgeStart >= 0 && configStart > bridgeStart && disconnectStart > configStart);
        string configMethod = backdropFile[configStart..disconnectStart];
        Assert.DoesNotContain("_onTargetChanged?.Invoke", configMethod, StringComparison.Ordinal);
        Assert.DoesNotContain("_onTargetChanged(", configMethod, StringComparison.Ordinal);
    }

    [Fact]
    public void RenameManagedWidgetFolderAsync_SuspendsWatchersBeforeFilesystemMutation()
    {
        string storage = File.ReadAllText(
            TestPaths.FromRepository("src/DeskBox/Services/WidgetManager.Storage.cs"));

        int methodStart = storage.IndexOf(
            "private async Task RenameManagedWidgetFolderAsync(",
            StringComparison.Ordinal);
        int helperStart = storage.IndexOf(
            "private async Task SuspendFileWidgetWatchersAsync(",
            methodStart,
            StringComparison.Ordinal);
        Assert.True(methodStart >= 0 && helperStart > methodStart);

        string renameMethod = storage[methodStart..helperStart];
        int firstSuspend = renameMethod.IndexOf("SuspendFileWidgetWatchersAsync(", StringComparison.Ordinal);
        int deleteCall = renameMethod.IndexOf("Directory.Delete(currentFolderPath);", StringComparison.Ordinal);
        int secondSuspend = renameMethod.LastIndexOf("SuspendFileWidgetWatchersAsync(", StringComparison.Ordinal);
        int moveCall = renameMethod.IndexOf("MoveManagedFolderWithRetry(", StringComparison.Ordinal);

        Assert.True(firstSuspend >= 0 && deleteCall > firstSuspend);
        Assert.True(secondSuspend > deleteCall && moveCall > secondSuspend);
        Assert.Contains("ResumeFileWidgetWatchersIfStoppedAsync(", renameMethod, StringComparison.Ordinal);
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

    [Fact]
    public void WidgetVisibilityAndTrayStartupLogging_FollowExpectedOrderAndGuards()
    {
        string interaction = File.ReadAllText(
            TestPaths.FromRepository("src/DeskBox/Views/ContentWidgetWindow.WindowInteraction.cs"));
        int showMethod = interaction.IndexOf(
            "private void ShowWithoutActivation(bool persistVisibility)",
            StringComparison.Ordinal);
        Assert.True(showMethod >= 0);
        string showSlice = interaction.Substring(showMethod, Math.Min(360, interaction.Length - showMethod));
        int visibleTrue = showSlice.IndexOf("Visible = true;", StringComparison.Ordinal);
        int appWindowShow = showSlice.IndexOf("AppWindow.Show();", StringComparison.Ordinal);
        Assert.True(visibleTrue >= 0 && appWindowShow > visibleTrue);

        string manager = File.ReadAllText(
            TestPaths.FromRepository("src/DeskBox/Services/WidgetManager.cs"));
        Assert.Contains("!App.IsBackgroundMemoryCleanupArmed", manager, StringComparison.Ordinal);

        string tray = File.ReadAllText(
            TestPaths.FromRepository("src/DeskBox/App.Tray.cs"));
        Assert.Contains("if (GlobalHotkeyService is { } globalHotkeyService)", tray, StringComparison.Ordinal);
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
