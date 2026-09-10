using DeskBox.Contracts;

namespace DeskBox.Services.Plugins;

/// <summary>
/// Keeps a live native instance's settings snapshot in step with its host
/// authority. Store notifications may arrive off-thread; snapshot writes
/// and ABI refreshes run together on the owning UI dispatcher. No feature
/// types, file names, or package ids are part of this mechanism.
/// </summary>
internal sealed class NativeInstanceSettingsSubscription : IDisposable
{
    private readonly object _gate = new();
    private readonly Func<Action, bool> _enqueue;
    private readonly Func<bool> _sync;
    private readonly Action _refresh;
    private readonly IDisposable? _subscription;
    private bool _queued;
    private bool _disposed;

    internal NativeInstanceSettingsSubscription(
        ILegacyInstanceMigration migration,
        string instanceId,
        Func<Action, bool> enqueue,
        Func<bool> sync,
        Action refresh)
    {
        _enqueue = enqueue;
        _sync = sync;
        _refresh = refresh;
        _subscription = migration.SubscribeChanges(instanceId, RequestRefresh);
        // Close the gap between create-time migration and subscribing.
        // Only live adapters participate; InitializeAsync remains a no-op.
        if (_subscription is not null) RequestRefresh();
    }

    internal void RequestRefresh()
    {
        lock (_gate)
        {
            if (_disposed || _queued) return;
            _queued = true;
        }
        try
        {
            if (_enqueue(Drain)) return;
        }
        catch (Exception error)
        {
            App.LogVerbose($"[NativePackage] could not queue settings refresh: {error.Message}");
        }
        lock (_gate) _queued = false;
    }

    private void Drain()
    {
        lock (_gate)
        {
            _queued = false;
            if (_disposed) return;
        }
        try
        {
            // Never tell a package to reload a stale or partially written
            // snapshot. A later change or explicit refresh can retry.
            if (!_sync()) return;
            lock (_gate)
            {
                if (_disposed) return;
            }
            _refresh();
        }
        catch (Exception error)
        {
            App.LogVerbose($"[NativePackage] live settings refresh failed: {error.Message}");
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
        }
        _subscription?.Dispose();
    }
}
