using DeskBox.Services;

namespace DeskBox.Tests;

public sealed class ShutdownSequenceTests
{
    [Fact]
    public async Task Failure_DoesNotSkipConsumerFlushContainerOrInstanceCleanup()
    {
        var calls = new List<string>();
        var logs = new List<string>();
        var shutdown = new ShutdownSequence(logs.Add);
        await shutdown.RunAsync(
            new("backup", () => Task.FromException(new IOException("failed"))),
            ShutdownStep.Sync("windows", () => calls.Add("windows")),
            new("flush", () => { calls.Add("flush"); return Task.CompletedTask; }),
            ShutdownStep.Sync("container", () => calls.Add("container")),
            ShutdownStep.Sync("mutex", () => calls.Add("mutex")));
        Assert.Equal(new[] { "windows", "flush", "container", "mutex" }, calls);
        Assert.Contains("backup", Assert.Single(logs));
    }

    [Fact]
    public async Task RepeatedShutdown_SharesTheDrainAndDisposesOnlyOnce()
    {
        var drain = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int disposed = 0;
        var shutdown = new ShutdownSequence(_ => { });
        Task first = shutdown.RunAsync(new("drain", () => drain.Task),
            ShutdownStep.Sync("dispose", () => disposed++));
        Task second = shutdown.RunAsync(ShutdownStep.Sync("unexpected", () => disposed += 10));
        Assert.Same(first, second);
        Assert.Equal(0, disposed);
        drain.SetResult();
        await Task.WhenAll(first, second);
        await shutdown.RunAsync();
        Assert.Equal(1, disposed);
    }

    [Fact]
    public async Task BoundedStep_TimesOutAndContinuesWithRemainingCleanup()
    {
        var blocked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = new List<string>();
        var logs = new List<string>();
        var shutdown = new ShutdownSequence(logs.Add);

        await shutdown.RunAsync(
            ShutdownStep.Bounded("quick-capture", () => blocked.Task,
                TimeSpan.FromMilliseconds(20)),
            ShutdownStep.Sync("settings-flush", () => calls.Add("settings-flush")),
            ShutdownStep.Sync("single-instance", () => calls.Add("single-instance")));

        Assert.Equal(["settings-flush", "single-instance"], calls);
        Assert.Contains("quick-capture", Assert.Single(logs));
        blocked.TrySetResult();
    }
}
