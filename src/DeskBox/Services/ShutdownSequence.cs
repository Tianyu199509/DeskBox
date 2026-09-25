namespace DeskBox.Services;

internal sealed record ShutdownStep(
    string Name, Func<Task> Run, bool AbortFollowingStepsOnTimeout = false)
{
    public static ShutdownStep Sync(string name, Action action) => new(name, () =>
    {
        action();
        return Task.CompletedTask;
    });

    public static ShutdownStep Bounded(
        string name, Func<Task> run, TimeSpan gracePeriod,
        bool abortFollowingStepsOnTimeout = false) =>
        new(name, async () =>
        {
            Task operation = run();
            using var deadlineCancellation = new CancellationTokenSource();
            Task deadline = Task.Delay(gracePeriod, deadlineCancellation.Token);
            if (await Task.WhenAny(operation, deadline) != operation)
            {
                throw new ShutdownStepDeadlineExceededException(name, gracePeriod);
            }
            deadlineCancellation.Cancel();
            await operation;
        }, abortFollowingStepsOnTimeout);
}

internal sealed class ShutdownStepDeadlineExceededException(
    string name, TimeSpan gracePeriod) : TimeoutException(
    $"Step '{name}' exceeded its {gracePeriod.TotalSeconds:F0}s shutdown grace period; its operation may still be running.");

/// <summary>
/// Runs teardown once in dependency order. Ordinary failures continue; an
/// ownership deadline leaves dependent resources alive for process exit.
/// </summary>
internal sealed class ShutdownSequence(Action<string> log)
{
    private readonly object _gate = new();
    private Task<bool>? _completion;

    public Task<bool> RunAsync(params ShutdownStep[] steps)
    {
        TaskCompletionSource<bool> completion;
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
                catch (ShutdownStepDeadlineExceededException ex)
                    when (step.AbortFollowingStepsOnTimeout)
                {
                    try { log($"[Shutdown] Step '{step.Name}' failed: {ex}"); } catch { }
                    completion.TrySetResult(false);
                    return;
                }
                catch (Exception ex)
                {
                    try { log($"[Shutdown] Step '{step.Name}' failed: {ex}"); } catch { }
                }
            }
            completion.TrySetResult(true);
        }
    }
}
