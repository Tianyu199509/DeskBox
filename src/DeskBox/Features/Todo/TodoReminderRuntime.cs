using DeskBox.Contracts;

namespace DeskBox.Features.Todo;

/// <summary>
/// Owns the active reminder session. Lifecycle calls are made on the owning
/// UI thread; an inactive or failed session is retired before another starts.
/// </summary>
internal sealed class TodoReminderRuntime(Func<ITodoReminderSession> createSession) : IAsyncDisposable
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

    public async ValueTask DisposeAsync()
    {
        _stopped = true;
        RetireCurrent();
        await Task.WhenAll(_retiringSessions);
        _retiringSessions.Clear();
    }

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
