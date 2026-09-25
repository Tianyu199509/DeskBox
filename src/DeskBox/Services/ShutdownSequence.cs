namespace DeskBox.Services;

internal sealed record ShutdownStep(string Name, Func<Task> Run)
{
    public static ShutdownStep Sync(string name, Action action) => new(name, () =>
    {
        action();
        return Task.CompletedTask;
    });

    public static ShutdownStep Bounded(string name, Func<Task> run, TimeSpan gracePeriod) =>
        new(name, async () =>
        {
            try
            {
                await run().WaitAsync(gracePeriod);
            }
            catch (TimeoutException ex)
            {
                throw new TimeoutException(
                    $"Step '{name}' exceeded its {gracePeriod.TotalSeconds:F0}s shutdown grace period; its operation may still be running.",
                    ex);
            }
        });
}

/// <summary>Runs teardown once, in dependency order, continuing after individual failures.</summary>
internal sealed class ShutdownSequence(Action<string> log)
{
    private readonly object _gate = new();
    private Task? _completion;

    public Task RunAsync(params ShutdownStep[] steps)
    {
        TaskCompletionSource completion;
        lock (_gate)
        {
            if (_completion is not null) return _completion;
            completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _completion = completion.Task;
        }
        _ = ExecuteAsync();
        return completion.Task;

        async Task ExecuteAsync()
        {
            foreach (ShutdownStep step in steps)
            {
                try { await step.Run(); }
                catch (Exception ex)
                {
                    try { log($"[Shutdown] Step '{step.Name}' failed: {ex}"); } catch { }
                }
            }
            completion.TrySetResult();
        }
    }
}
