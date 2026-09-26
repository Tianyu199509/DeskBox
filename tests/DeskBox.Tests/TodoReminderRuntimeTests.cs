using DeskBox.Contracts;
using DeskBox.Features.Todo;
using DeskBox.Services;

namespace DeskBox.Tests;

public sealed class TodoReminderRuntimeTests
{
    private static readonly TodoReminderSettings Active = new(true, true, 5);

    [Fact]
    public async Task RepeatedReconciliationAndDisable_OwnExactlyOneSession()
    {
        var sessions = new List<FakeSession>();
        await using var runtime = new TodoReminderRuntime(() =>
        {
            var session = new FakeSession();
            sessions.Add(session);
            return session;
        }, () => Active);

        for (int i = 0; i < 5; i++) runtime.Reconcile(Active);
        Assert.Single(sessions);
        Assert.Equal(1, sessions[0].Starts);
        Assert.Equal(4, sessions[0].Refreshes);

        runtime.Reconcile(Active with { RemindersEnabled = false });
        runtime.Reconcile(Active with { Enabled = false });
        Assert.Null(runtime.Current);
        Assert.Equal(1, sessions[0].Disposals);

        runtime.Reconcile(Active);
        Assert.Equal(2, sessions.Count);
        Assert.Same(sessions[1], runtime.Current);
    }

    [Fact]
    public async Task FailedStart_RetiresPartialSessionAndAllowsRetry()
    {
        var failed = new FakeSession { FailStart = true };
        var healthy = new FakeSession();
        int attempts = 0;
        await using var runtime = new TodoReminderRuntime(() => ++attempts == 1 ? failed : healthy, () => Active);

        Assert.Throws<InvalidOperationException>(() => runtime.Reconcile(Active));
        Assert.Null(runtime.Current);
        Assert.Equal(1, failed.Disposals);

        runtime.Reconcile(Active);
        Assert.Same(healthy, runtime.Current);
        Assert.Equal(1, healthy.Starts);
    }

    [Fact]
    public async Task Stop_DrainsRetiredWorkAndCannotRestart()
    {
        var drain = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var session = new FakeSession { Drain = drain.Task };
        var runtime = new TodoReminderRuntime(() => session, () => Active);
        runtime.Reconcile(Active);
        runtime.Reconcile(Active with { Enabled = false });

        Task stop = runtime.DisposeAsync().AsTask();
        Assert.False(stop.IsCompleted);
        Assert.Equal(1, session.Disposals);
        runtime.Reconcile(Active);
        Assert.Null(runtime.Current);

        drain.SetResult();
        await stop;
        await runtime.DisposeAsync();
        Assert.Equal(1, session.Starts);
        Assert.Equal(1, session.Disposals);
    }

    [Fact]
    public async Task HungReminderDrain_StopsSessionButKeepsNotificationsAliveUntilExit()
    {
        var drain = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var session = new FakeSession { Drain = drain.Task };
        var runtime = new TodoReminderRuntime(() => session, () => Active);
        runtime.Reconcile(Active);
        bool notificationsDisposed = false;
        var shutdown = new ShutdownSequence(_ => { });
        Task? reminderStop = null;

        bool completed = await shutdown.RunAsync(
            ShutdownStep.Bounded("todo-reminders",
                () => reminderStop = runtime.DisposeAsync().AsTask(),
                TimeSpan.FromMilliseconds(30), abortFollowingStepsOnTimeout: true),
            ShutdownStep.Sync("notifications", () => notificationsDisposed = true));

        Assert.False(completed);
        Assert.Equal(1, session.Disposals);
        Assert.False(notificationsDisposed);
        runtime.Reconcile(Active);
        Assert.Null(runtime.Current);
        drain.SetResult();
        await reminderStop!;
        await runtime.DisposeAsync();
        Assert.False(notificationsDisposed);
    }

    [Fact]
    public async Task AuditOverride_DoesNotChangeDisabledProductPreferences()
    {
        var session = new FakeSession();
        await using var runtime = new TodoReminderRuntime(() => session, () => Active);
        var disabled = Active with { RemindersEnabled = false };
        runtime.Reconcile(disabled);
        Assert.Null(runtime.Current);
        runtime.Reconcile(disabled, forceActive: true);
        Assert.Same(session, runtime.Current);
        runtime.Reconcile(disabled);
        Assert.Null(runtime.Current);
    }

    private sealed class FakeSession : ITodoReminderSession
    {
        public bool FailStart { get; init; }
        public int Starts { get; private set; }
        public int Refreshes { get; private set; }
        public int Disposals { get; private set; }
        public Task Drain { get; init; } = Task.CompletedTask;
        public void Start()
        {
            Starts++;
            if (FailStart) throw new InvalidOperationException("timer registration failed");
        }
        public void Refresh() => Refreshes++;
        public Task<int> CheckNowAsync(DateTimeOffset now) => Task.FromResult(0);
        public ValueTask DisposeAsync()
        {
            Disposals++;
            return new ValueTask(Drain);
        }
    }
}
