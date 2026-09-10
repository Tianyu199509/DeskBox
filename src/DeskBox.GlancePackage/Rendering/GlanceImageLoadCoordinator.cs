using DeskBox.GlancePackage.Services;
using DeskBox.Models;

namespace DeskBox.GlancePackage.Rendering;

/// <summary>Loads cache first, then online results; only the current request may update the UI.</summary>
internal sealed class GlanceImageLoadCoordinator(
    Func<GlanceWidgetData, CancellationToken, Task<IReadOnlyList<GlanceImageInfo>>> loadCached,
    Func<GlanceWidgetData, CancellationToken, Task<IReadOnlyList<GlanceImageInfo>>> refreshOnline,
    Func<Action, bool> enqueue,
    Action<IReadOnlyList<GlanceImageInfo>> apply) : IDisposable
{
    private readonly object _gate = new();
    private CancellationTokenSource? _active;
    private long _version;
    private bool _disposed;

    internal Task RequestAsync(GlanceWidgetData settings, bool refresh = true)
    {
        // Never let a worker observe a mutable instance settings object.
        var snapshot = new GlanceWidgetData
        {
            BackgroundSource = settings.BackgroundSource,
            OnlineImageCategory = settings.OnlineImageCategory,
            LocalImagePaths = [.. settings.LocalImagePaths],
            LocalFolderPath = settings.LocalFolderPath,
            RandomOrder = settings.RandomOrder,
        };
        CancellationTokenSource cts;
        long version;
        lock (_gate)
        {
            if (_disposed) return Task.CompletedTask;
            _active?.Cancel();
            _active = cts = new CancellationTokenSource();
            version = ++_version;
        }
        return RunAsync(snapshot, refresh, cts, version);
    }

    private async Task RunAsync(GlanceWidgetData snapshot, bool refresh, CancellationTokenSource cts, long version)
    {
        try
        {
            var cached = await loadCached(snapshot, cts.Token).ConfigureAwait(false);
            cts.Token.ThrowIfCancellationRequested();
            Post(cached, version);
            if (refresh && snapshot.BackgroundSource is GlanceBackgroundSource.Online or GlanceBackgroundSource.Bing)
            {
                var fresh = await refreshOnline(snapshot, cts.Token).ConfigureAwait(false);
                cts.Token.ThrowIfCancellationRequested();
                // A failed refresh must not erase already usable cached photos.
                if (fresh.Count > 0) Post(fresh, version);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { PackageLogger.LogVerbose($"[GlancePackage] image load failed: {error.Message}"); }
        finally
        {
            lock (_gate)
            {
                if (ReferenceEquals(_active, cts)) _active = null;
                cts.Dispose();
            }
        }
    }

    private void Post(IReadOnlyList<GlanceImageInfo> images, long version)
    {
        enqueue(() =>
        {
            lock (_gate)
            {
                if (_disposed || _version != version) return;
                // Cancellation and delivery share this gate, so cancellation
                // cannot pass validation and then race the UI mutation.
                try { apply(images); }
                catch (Exception error) { PackageLogger.LogVerbose($"[GlancePackage] image display failed: {error.Message}"); }
            }
        });
    }

    internal void Cancel()
    {
        lock (_gate)
        {
            ++_version; // invalidates work that already reached the dispatcher
            _active?.Cancel();
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            ++_version;
            _active?.Cancel();
        }
    }
}
