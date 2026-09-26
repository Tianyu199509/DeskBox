using DeskBox.Contracts;

namespace DeskBox.Features.Todo;

/// <summary>
/// Owns the active reminder session. Lifecycle calls are made on the owning
/// UI thread; an inactive or failed session is retired before another starts.
/// The thread is the serialization boundary of the
/// <see cref="IFeatureRuntime"/> state machine: <see cref="StartAsync"/> and
/// <see cref="DisposeAsync"/> cannot interleave.
/// </summary>
internal sealed class TodoReminderRuntime(
    Func<ITodoReminderSession> createSession,
    Func<TodoReminderSettings> readSettings) : IFeatureRuntime
{
    private readonly List<Task> _retiringSessions = [];
    private bool _stopped;

    internal ITodoReminderSession? Current { get; private set; }

    internal void Reconcile(TodoReminderSettings settings, bool forceActive = false)
    {
        if (_stopped)
        {
            return;
        }

        if (!settings.ShouldRunReminders && !forceActive)
        {
            RetireCurrent();
            return;
        }

        if (Current is { } current)
        {
            try
            {
                current.Refresh();
            }
            catch
            {
                RetireCurrent();
                throw;
            }
            return;
        }

        ITodoReminderSession candidate = createSession();
        try
        {
            candidate.Start();
            Current = candidate;
        }
        catch
        {
            Retire(candidate);
            throw;
        }
    }

    /// <summary>
    /// <see cref="IFeatureRuntime"/> lease entry: reconciles to the reminder
    /// settings that are current right now — the same path the settings
    /// coordinator drives. Idempotency comes from <see cref="Reconcile"/>
    /// (an existing session is only refreshed), rollback from the failed-
    /// candidate retirement below.
    /// </summary>
    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Reconcile(readSettings());
        return Task.CompletedTask;
    }

    /// <summary>
    /// <see cref="IFeatureRuntime"/> release: stops accepting reconciliation,
    /// retires the current session, and drains retired work. Terminal — a
    /// start after this is a no-op. Repeat calls drain the same (possibly
    /// still running) retirement work instead of releasing twice.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        _stopped = true;
        RetireCurrent();
        await Task.WhenAll(_retiringSessions);
        _retiringSessions.Clear();
    }

    /// <summary>
    /// Feature scan-now trigger: runs one reminder pass on the current
    /// session, if one is active. Owned here so the host only routes the
    /// user intent, not the feature's own scan behavior.
    /// </summary>
    internal Task CheckCurrentAsync(DateTimeOffset now) =>
        Current?.CheckNowAsync(now) ?? Task.FromResult(0);

    private void RetireCurrent()
    {
        ITodoReminderSession? session = Current;
        Current = null;
        if (session is not null)
        {
            Retire(session);
        }
    }

    private void Retire(ITodoReminderSession session)
    {
        // DisposeAsync stops timers synchronously, then drains any existing IO.
        // Keep faults for shutdown to observe, while pruning successful drains.
        _retiringSessions.RemoveAll(task => task.IsCompletedSuccessfully);
        try
        {
            _retiringSessions.Add(session.DisposeAsync().AsTask());
        }
        catch (Exception ex)
        {
            _retiringSessions.Add(Task.FromException(ex));
        }
    }
}
