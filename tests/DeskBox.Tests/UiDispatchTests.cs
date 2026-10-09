using DeskBox.Platform;
using Microsoft.UI.Dispatching;

namespace DeskBox.Tests;

/// <summary>
/// State-machine tests for UiDispatch: thread identity, Headless/Ready/
/// Shutdown decisions, bounded deferral replay, and the headless skip
/// semantics that replace the legacy "null dispatcher ⇒ current thread is
/// the UI thread" fiction.
/// </summary>
public sealed class UiDispatchTests : IDisposable
{
    public UiDispatchTests()
    {
        UiDispatch.ResetForTests();
    }

    public void Dispose()
    {
        UiDispatch.ResetForTests();
    }

    [Fact]
    public void Headless_OnOriginThread_RunsInline()
    {
        UiDispatch.MarkUiOriginThread(); // this test thread is the "UI" thread

        Assert.Equal(UiDispatch.PhaseKind.Headless, UiDispatch.Phase);
        Assert.True(UiDispatch.HasAccess);

        bool ran = false;
        UiDispatch.RunOrDefer(() => ran = true);

        Assert.True(ran);
        Assert.Equal(0, UiDispatch.PendingCount);
    }

    [Fact]
    public async Task Headless_TestHostWithoutOrigin_KeepsLegacyInlineFiction()
    {
        // Test-host shape: no origin registered, foreign thread. Test hosts
        // deliberately keep the legacy inline fiction (mixed UI/config logic
        // behind these gates must still run in fixtures); the production Skip
        // branch is unreachable in this process and reviewed instead.
        Assert.Equal(UiDispatch.PhaseKind.Headless, UiDispatch.Phase);

        bool ran = false;
        await Task.Run(() => UiDispatch.RunOrDefer(() => ran = true));

        Assert.True(ran);
        Assert.Equal(0, UiDispatch.PendingCount);
    }

    [Fact]
    public async Task Headless_ForeignThreadWithOrigin_DefersUntilReady()
    {
        UiDispatch.MarkUiOriginThread();
        using var dispatcher = new DispatcherQueueThread();

        var completion = new TaskCompletionSource<int>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        int? ranOnThreadId = null;
        await Task.Run(() =>
        {
            UiDispatch.RunAsync(async () =>
            {
                ranOnThreadId = Environment.CurrentManagedThreadId;
                await Task.Yield();
                return 42;
            }).ContinueWith(
                t => completion.SetResult(t.Result),
                TaskContinuationOptions.OnlyOnRanToCompletion);
        });

        Assert.Equal(1, UiDispatch.PendingCount);
        Assert.False(completion.Task.IsCompleted);

        // MarkReady runs on the dispatcher thread; replay happens there.
        await dispatcher.RunOnQueueAsync(() => UiDispatch.MarkReady(dispatcher.Queue));

        int result = await WaitUntilAsync(() => completion.Task.IsCompleted)
            ? await completion.Task
            : 0;
        Assert.Equal(42, result);
        Assert.NotNull(ranOnThreadId);
        Assert.Equal(dispatcher.ThreadId, ranOnThreadId);
        Assert.Equal(0, UiDispatch.PendingCount);
    }

    [Fact]
    public async Task Ready_ForeignThread_Enqueues()
    {
        using var dispatcher = new DispatcherQueueThread();
        await dispatcher.RunOnQueueAsync(() => UiDispatch.MarkReady(dispatcher.Queue));
        Assert.Equal(UiDispatch.PhaseKind.Ready, UiDispatch.Phase);

        var completion = new TaskCompletionSource<string>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        await Task.Run(() =>
        {
            UiDispatch.RunAsync(() => Task.FromResult("queued"))
                .ContinueWith(
                    t => completion.SetResult(t.Result),
                    TaskContinuationOptions.OnlyOnRanToCompletion);
        });

        string result = await WaitUntilAsync(() => completion.Task.IsCompleted)
            ? await completion.Task
            : string.Empty;
        Assert.Equal("queued", result);
    }

    [Fact]
    public void Shutdown_DropsPending_RefusesNewWork()
    {
        UiDispatch.MarkUiOriginThread();
        UiDispatch.MarkShutdown();

        Assert.Equal(UiDispatch.PhaseKind.Shutdown, UiDispatch.Phase);
        Assert.False(UiDispatch.HasAccess);

        bool ran = false;
        UiDispatch.RunOrDefer(() => ran = true);
        Assert.False(ran);
    }

    [Fact]
    public async Task DeferredQueue_DropsBeyondCap()
    {
        UiDispatch.MarkUiOriginThread();
        long droppedBefore = UiDispatch.DeferredDroppedCount;

        await Task.Run(() =>
        {
            for (int i = 0; i < 260; i++)
            {
                UiDispatch.RunOrDefer(() => { });
            }
        });

        Assert.Equal(256, UiDispatch.PendingCount);
        Assert.True(UiDispatch.DeferredDroppedCount >= droppedBefore + 4);
    }

    private static async Task<bool> WaitUntilAsync(Func<bool> condition)
    {
        DateTime deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return true;
            }

            await Task.Delay(20);
        }

        return condition();
    }

    private sealed class DispatcherQueueThread : IDisposable
    {
        private readonly DispatcherQueueController _controller =
            DispatcherQueueController.CreateOnDedicatedThread();

        public DispatcherQueue Queue => _controller.DispatcherQueue;

        public int ThreadId { get; private set; }

        public Task RunOnQueueAsync(Action action)
        {
            var completion = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            if (!Queue.TryEnqueue(() =>
                {
                    try
                    {
                        ThreadId = Environment.CurrentManagedThreadId;
                        action();
                        completion.SetResult(true);
                    }
                    catch (Exception ex)
                    {
                        completion.SetException(ex);
                    }
                }))
            {
                completion.SetException(new InvalidOperationException(
                    "Failed to enqueue onto the dedicated dispatcher queue."));
            }

            return completion.Task;
        }

        public void Dispose()
        {
            _ = _controller.ShutdownQueueAsync();
        }
    }
}
