using DeskBox.Contracts;

namespace DeskBox.Features.Clipboard;

/// <summary>
/// Formal <see cref="IFeatureRuntime"/> lease over the Clipboard capture daemon:
/// active only while <c>WidgetKind.Clipboard</c> is enabled, zero OS listener
/// overhead when disabled or after shutdown.
/// </summary>
internal sealed class ClipboardFeatureRuntime : IFeatureRuntime
{
    private readonly Action _start;
    private readonly Func<ValueTask> _stopAsync;
    private readonly SemaphoreSlim _lifecycleLock = new(1, 1);
    private bool _started;

    public ClipboardFeatureRuntime(Action start, Action stop)
        : this(start, () =>
        {
            stop();
            return ValueTask.CompletedTask;
        })
    {
    }

    public ClipboardFeatureRuntime(Action start, Func<ValueTask> stopAsync)
    {
        _start = start;
        _stopAsync = stopAsync;
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycleLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_started)
            {
                return;
            }

            cancellationToken.ThrowIfCancellationRequested();
            _start();
            _started = true;
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _lifecycleLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!_started)
            {
                return;
            }

            _started = false;
            await _stopAsync().ConfigureAwait(false);
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }
}
