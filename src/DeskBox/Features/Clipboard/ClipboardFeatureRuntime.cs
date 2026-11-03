using DeskBox.Contracts;

namespace DeskBox.Features.Clipboard;

/// <summary>
/// Formal <see cref="IFeatureRuntime"/> lease over the Clipboard capture daemon:
/// active only while <c>WidgetKind.Clipboard</c> is enabled, zero OS listener
/// overhead when disabled or after shutdown.
/// </summary>
internal sealed class ClipboardFeatureRuntime(Action start, Action stop) : IFeatureRuntime
{
    private readonly Lock _gate = new();
    private bool _started;

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (_started)
            {
                return Task.CompletedTask;
            }
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
            if (!_started)
            {
                return ValueTask.CompletedTask;
            }

            _started = false;
        }

        stop();
        return ValueTask.CompletedTask;
    }
}
