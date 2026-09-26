using DeskBox.Contracts;

namespace DeskBox.Features.Search;

/// <summary>
/// Formal <see cref="IFeatureRuntime"/> lease over the App-owned search
/// start/stop chain (batch 6): start ensures the search services, dispose
/// releases the engine, Everything provider, hotkey, popup, history, and icon
/// caches back to zero. The delegates are the App chain itself; the chain
/// owns its real idempotency (early return while the engine lives) and its
/// partial-failure rollback, and it only ever runs on the host UI thread,
/// which serializes the two transitions. The flag here keeps registry-level
/// repeat calls and shutdown sweeps no-ops instead of re-running the chain.
/// </summary>
internal sealed class SearchFeatureRuntime(Action start, Action dispose) : IFeatureRuntime
{
    private readonly object _gate = new();
    private bool _started;

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (_started) return Task.CompletedTask;
        }
        cancellationToken.ThrowIfCancellationRequested();
        start();
        lock (_gate)
        {
            _started = true;
        }
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (!_started) return ValueTask.CompletedTask;
            _started = false;
        }
        dispose();
        return ValueTask.CompletedTask;
    }
}
