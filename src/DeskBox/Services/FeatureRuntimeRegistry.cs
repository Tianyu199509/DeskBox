using DeskBox.Contracts;

namespace DeskBox.Services;

/// <summary>
/// App-level ownership ledger for feature runtimes (roadmap §2): the
/// registry — not individual callers — owns the <see cref="IFeatureRuntime"/>
/// instances. Assembly registers each runtime exactly once; code that needs a
/// runtime borrows it through <see cref="TryGet"/> instead of caching a
/// private reference. Disposal sweeps run in reverse registration order
/// (matching the contract's reverse-acquisition reclamation) and isolate
/// faults: a runtime that cannot be disposed is reported and quarantined for
/// leak isolation so it cannot block the host's shutdown.
/// </summary>
public sealed class FeatureRuntimeRegistry
{
    private readonly object _gate = new();
    private readonly List<KeyValuePair<string, IFeatureRuntime>> _runtimes = [];
    private readonly Action<string> _log;

    public FeatureRuntimeRegistry(Action<string>? log = null)
    {
        _log = log ?? (_ => { });
    }

    /// <summary>Registers the runtime that owns <paramref name="featureId"/> and returns it for wiring.</summary>
    public T Register<T>(string featureId, T runtime) where T : IFeatureRuntime
    {
        ArgumentNullException.ThrowIfNull(featureId);
        ArgumentNullException.ThrowIfNull(runtime);
        lock (_gate)
        {
            if (_runtimes.Any(pair => string.Equals(pair.Key, featureId, StringComparison.Ordinal)))
            {
                throw new ArgumentException(
                    $"A runtime is already registered for '{featureId}'.", nameof(featureId));
            }
            _runtimes.Add(new(featureId, runtime));
        }
        return runtime;
    }

    /// <summary>Borrows the runtime registered for <paramref name="featureId"/>, if any.</summary>
    public IFeatureRuntime? TryGet(string featureId)
    {
        lock (_gate)
        {
            return _runtimes
                .Where(pair => string.Equals(pair.Key, featureId, StringComparison.Ordinal))
                .Select(pair => pair.Value)
                .FirstOrDefault();
        }
    }

    public IReadOnlyList<string> RegisteredFeatures
    {
        get
        {
            lock (_gate) return _runtimes.Select(pair => pair.Key).ToArray();
        }
    }

    /// <summary>
    /// Releases the runtime registered for <paramref name="featureId"/>. The
    /// runtime's own idempotent disposal makes repeat calls no-ops.
    /// </summary>
    public async Task DisposeAsync(string featureId)
    {
        IFeatureRuntime? runtime = TryGet(featureId);
        if (runtime is null) return;
        try
        {
            await runtime.DisposeAsync().AsTask();
        }
        catch (Exception ex)
        {
            _log($"[FeatureRuntimes] '{featureId}' could not be disposed and is quarantined for leak isolation: {ex}");
        }
    }

    /// <summary>
    /// Shutdown sweep: disposes every registered runtime in reverse
    /// registration order. A runtime that already ran its own disposal step
    /// is an idempotent no-op here; one that throws is reported and skipped
    /// so the remaining runtimes still release.
    /// </summary>
    public async Task DisposeAllAsync()
    {
        KeyValuePair<string, IFeatureRuntime>[] snapshot;
        lock (_gate)
        {
            snapshot = _runtimes
                .Select((pair, index) => (pair, index))
                .OrderByDescending(entry => entry.index)
                .Select(entry => entry.pair)
                .ToArray();
        }
        foreach (KeyValuePair<string, IFeatureRuntime> entry in snapshot)
        {
            try
            {
                await entry.Value.DisposeAsync().AsTask();
            }
            catch (Exception ex)
            {
                _log($"[FeatureRuntimes] '{entry.Key}' could not be disposed and is quarantined for leak isolation: {ex}");
            }
        }
    }
}
