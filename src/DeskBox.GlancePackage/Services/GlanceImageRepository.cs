using DeskBox.Models;
using DeskBox.Services;

namespace DeskBox.GlancePackage.Services;

/// <summary>One repository per package session, shared by every Glance instance.</summary>
internal sealed class GlanceImageRepository : IDisposable
{
    private readonly GlanceImageService _service;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly CancellationToken _token;
    private readonly Lazy<Task> _prepareCache;
    private int _disposed;

    internal GlanceImageRepository(string packageDataRoot)
        : this(new GlanceImageService(Path.Combine(packageDataRoot, "cache", "glance")),
            ct => LegacyGlanceImageCacheImporter.ImportAsync(packageDataRoot, ct)) { }

    internal GlanceImageRepository(GlanceImageService service, Func<CancellationToken, Task> prepareCache)
    {
        _service = service;
        _token = _lifetime.Token;
        _prepareCache = new Lazy<Task>(() => prepareCache(_token));
    }

    internal async Task<IReadOnlyList<GlanceImageInfo>> GetAvailableAsync(GlanceWidgetData settings, CancellationToken ct)
    {
        using var request = CancellationTokenSource.CreateLinkedTokenSource(ct, _token);
        CancellationToken token = request.Token;
        if (settings.BackgroundSource is GlanceBackgroundSource.Online or GlanceBackgroundSource.Bing)
            await PrepareAsync(token).ConfigureAwait(false);
        return await _service.GetAvailableImagesAsync(settings, token).ConfigureAwait(false);
    }

    internal async Task<IReadOnlyList<GlanceImageInfo>> RefreshOnlineAsync(GlanceWidgetData settings, CancellationToken ct)
    {
        using var request = CancellationTokenSource.CreateLinkedTokenSource(ct, _token);
        CancellationToken token = request.Token;
        await PrepareAsync(token).ConfigureAwait(false);
        return await _service.RefreshOnlineImagesAsync(settings, token).ConfigureAwait(false);
    }

    private Task PrepareAsync(CancellationToken ct)
    {
        _token.ThrowIfCancellationRequested();
        // Cancelling one widget's wait must not cancel another widget's import.
        // All cache writers await the same preparation before doing any work.
        return _prepareCache.Value.WaitAsync(ct);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try { _lifetime.Cancel(); }
        finally { _lifetime.Dispose(); }
    }
}
