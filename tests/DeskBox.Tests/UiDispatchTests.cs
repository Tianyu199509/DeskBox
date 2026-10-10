using System.Runtime.InteropServices;
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
    // The CI unit-test host has no Windows App SDK WinRT classes registered —
    // DispatcherQueueController activation throws REGDB_E_CLASSNOTREG (the
    // same host limitation behind the empty-restore Application.Current
    // guard). The two tests that exercise the Ready phase against a REAL
    // dispatcher queue early-out where the runtime is unavailable; every
    // interactive environment (dev box, real machines) runs them in full.
    private static readonly bool s_realDispatcherQueueAvailable = ProbeRealDispatcherQueue();

    public UiDispatchTests()
    {
        UiDispatch.ResetForTests();
    }

    public void Dispose()
    {
        UiDispatch.ResetForTests();
    }

    private static bool ProbeRealDispatcherQueue()
    {
        try
        {
            using var probe = new DispatcherQueueThread();
            return true;
        }
        catch (COMException)
        {
            return false;
        }
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
        if (!s_realDispatcherQueueAvailable)
        {
            return;
        }

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
        if (!s_realDispatcherQueueAvailable)
        {
            return;
        }

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

    [Fact]
    public async Task Shutdown_CancelsPendingDeferredActions()
    {
        UiDispatch.MarkUiOriginThread();
        long droppedBefore = UiDispatch.DeferredDroppedCount;

        Task deferred = null!;
        // Braces matter: the lambda must be an Action, otherwise the
        // assignment expression makes it Func<Task> and Task.Run unwraps —
        // the await would then block on the deferred task itself.
        await Task.Run(() => { deferred = UiDispatch.RunAsync(() => Task.FromResult(1)); });
        Assert.Equal(1, UiDispatch.PendingCount);
        Assert.False(deferred.IsCompleted);

        UiDispatch.MarkShutdown();

        await Assert.ThrowsAsync<TaskCanceledException>(() => deferred);
        Assert.Equal(0, UiDispatch.PendingCount);
        Assert.Equal(droppedBefore + 1, UiDispatch.DeferredDroppedCount);
    }

    [Fact]
    public async Task Shutdown_AfterRunOrDeferDeferral_StaysSilent()
    {
        UiDispatch.MarkUiOriginThread();
        long droppedBefore = UiDispatch.DeferredDroppedCount;

        bool ran = false;
        await Task.Run(() => UiDispatch.RunOrDefer(() => ran = true));
        Assert.Equal(1, UiDispatch.PendingCount);

        // The fire-and-forget deferral path must observe cancellation
        // silently: no thrown exception here, and a canceled task never
        // surfaces as an unobserved exception later.
        UiDispatch.MarkShutdown();

        bool ranAfterShutdown = false;
        UiDispatch.RunOrDefer(() => ranAfterShutdown = true);

        Assert.False(ran);
        Assert.False(ranAfterShutdown);
        Assert.Equal(0, UiDispatch.PendingCount);
        Assert.Equal(droppedBefore + 1, UiDispatch.DeferredDroppedCount);
    }

    [Fact]
    public async Task RepeatedShutdown_DoesNotRecountDroppedActions()
    {
        UiDispatch.MarkUiOriginThread();
        long droppedBefore = UiDispatch.DeferredDroppedCount;

        await Task.Run(() =>
        {
            for (int i = 0; i < 3; i++)
            {
                UiDispatch.RunOrDefer(() => { });
            }
        });
        Assert.Equal(3, UiDispatch.PendingCount);

        UiDispatch.MarkShutdown();
        long droppedAfterFirst = UiDispatch.DeferredDroppedCount;

        UiDispatch.MarkShutdown();

        Assert.Equal(droppedBefore + 3, droppedAfterFirst);
        Assert.Equal(droppedAfterFirst, UiDispatch.DeferredDroppedCount);
        Assert.Equal(0, UiDispatch.PendingCount);
    }

    [Fact]
    public async Task ResetForTests_CancelsLeftoverDeferredActions()
    {
        UiDispatch.MarkUiOriginThread();

        Task deferred = null!;
        await Task.Run(() => { deferred = UiDispatch.RunAsync(() => Task.FromResult(1)); });
        Assert.Equal(1, UiDispatch.PendingCount);

        UiDispatch.ResetForTests();

        await Assert.ThrowsAsync<TaskCanceledException>(() => deferred);
        Assert.Equal(0, UiDispatch.PendingCount);
    }
}
