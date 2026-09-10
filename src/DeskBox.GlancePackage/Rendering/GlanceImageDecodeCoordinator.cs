using DeskBox.GlancePackage.Services;
using DeskBox.Services;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage;

namespace DeskBox.GlancePackage.Rendering;

/// <summary>
/// Explicitly decodes a local stream before offering an image for display; the
/// caller retains ownership of its current layers. No live visual tree is needed.
/// Request/Cancel/Dispose may originate off that dispatcher and invalidate queued
/// work immediately. All BitmapImage access and callback delivery stay on it.
/// </summary>
internal sealed class GlanceImageDecodeCoordinator(
    DispatcherQueue dispatcher,
    Action<string, ImageBrush> ready,
    Action<string> failed) : IDisposable
{
    private readonly object _gate = new();
    private long _version;
    private bool _disposed;
    private CancellationTokenSource? _active;
    // Accessed only on the supplied dispatcher, under _gate.
    private bool _watchingShutdown;

    internal void Request(string path, Stretch stretch, AlignmentX alignmentX, AlignmentY alignmentY,
        int decodePixelWidth = 0)
    {
        long version;
        CancellationTokenSource cts;
        lock (_gate)
        {
            if (_disposed) return;
            version = ++_version;
            CancelActive();
            _active = cts = new CancellationTokenSource();
        }
        _ = DecodeAsync(version, path, stretch, alignmentX, alignmentY, decodePixelWidth, cts);
    }

    internal void Cancel()
    {
        lock (_gate)
        {
            if (_disposed) return;
            ++_version;
            CancelActive();
        }
    }

    private async Task DecodeAsync(long version, string path, Stretch stretch,
        AlignmentX alignmentX, AlignmentY alignmentY, int decodePixelWidth, CancellationTokenSource cts)
    {
        CancellationToken token = cts.Token;
        try
        {
            BitmapImage bitmap = await OnDispatcherAsync(version, token, () =>
            {
                if (!_watchingShutdown)
                {
                    dispatcher.ShutdownStarting += OnShutdownStarting;
                    _watchingShutdown = true;
                }
                return new BitmapImage
                {
                    DecodePixelType = DecodePixelType.Physical,
                    DecodePixelWidth = decodePixelWidth <= 0
                        ? GlanceImageDecodeSizeCalculator.Calculate(0, 0, 1)
                        : Math.Clamp(decodePixelWidth,
                            GlanceImageDecodeSizeCalculator.MinimumDecodePixelWidth,
                            GlanceImageDecodeSizeCalculator.MaximumDecodePixelWidth),
                };
            }).ConfigureAwait(false);

            StorageFile file = await StorageFile.GetFileFromPathAsync(path).AsTask(token).ConfigureAwait(false);
            using (var stream = await file.OpenReadAsync().AsTask(token).ConfigureAwait(false))
            {
                // Initiate the visual object's operation on its dispatcher, then
                // await it without borrowing the host's SynchronizationContext.
                Task decoding = await OnDispatcherAsync(version, token,
                    () => bitmap.SetSourceAsync(stream).AsTask(token)).ConfigureAwait(false);
                await decoding.ConfigureAwait(false);
            }

            // SetSourceAsync's completion is the explicit decode result, so no
            // ImageOpened subscription or attachment to a visual tree is needed.
            await OnDispatcherAsync(version, token, () =>
            {
                if (bitmap.PixelWidth <= 0 || bitmap.PixelHeight <= 0)
                    throw new InvalidDataException("Image decoding produced no pixels.");
                var brush = new ImageBrush
                {
                    ImageSource = bitmap,
                    Stretch = stretch,
                    AlignmentX = alignmentX,
                    AlignmentY = alignmentY,
                };
                CompleteRequest();
                try { ready(path, brush); }
                catch (Exception error) { Log(error); }
                return true;
            }, alwaysEnqueue: true).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception error)
        {
            Log(error);
            try
            {
                await OnDispatcherAsync(version, token, () =>
                {
                    CompleteRequest();
                    try { failed(path); }
                    catch (Exception callbackError) { Log(callbackError); }
                    return true;
                }, alwaysEnqueue: true).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { }
            catch (Exception callbackError) { Log(callbackError); }
        }
        finally
        {
            lock (_gate)
            {
                if (ReferenceEquals(_active, cts)) _active = null;
                cts.Dispose();
            }
        }
    }

    private async Task<T> OnDispatcherAsync<T>(long version, CancellationToken token,
        Func<T> action, bool alwaysEnqueue = false)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        // Release waiting stream owners even if shutdown drops an accepted action.
        using var registration = token.Register(() => completion.TrySetCanceled(token));
        if (!Dispatch(() =>
            {
                lock (_gate)
                {
                    if (_disposed || version != _version || token.IsCancellationRequested)
                    {
                        completion.TrySetCanceled();
                        return;
                    }
                    // Cancellation and delivery serialize through this gate.
                    // Callbacks may reenter Request/Cancel/Dispose on this thread.
                    try { completion.TrySetResult(action()); }
                    catch (Exception error) { completion.TrySetException(error); }
                }
            }, alwaysEnqueue)) completion.TrySetCanceled();
        return await completion.Task.ConfigureAwait(false);
    }

    // Called under _gate on the dispatcher before invoking the terminal callback.
    private void CompleteRequest()
    {
        _active = null;
        ++_version;
    }

    private bool Dispatch(Action action, bool alwaysEnqueue = false)
    {
        try
        {
            if (!alwaysEnqueue && dispatcher.HasThreadAccess)
            {
                action();
                return true;
            }
            if (dispatcher.TryEnqueue(() =>
                {
                    try { action(); }
                    catch (Exception error) { Log(error); }
                })) return true;
        }
        catch (Exception error) { Log(error); }

        // No callback fallback on a worker thread when the UI queue closes.
        // ShutdownStarting cancels any live request on the owning thread;
        // if this call is already on it, cleanup can still happen immediately.
        lock (_gate)
        {
            _disposed = true;
            ++_version;
            CancelActive();
        }
        try
        {
            if (dispatcher.HasThreadAccess) CleanupOnDispatcher();
        }
        catch (Exception error) { Log(error); }
        return false;
    }

    // The async request owns disposal once all of its continuations unwind.
    private void CancelActive()
    {
        CancellationTokenSource? active = _active;
        _active = null;
        try { active?.Cancel(); }
        catch (Exception error) { Log(error); }
    }

    private void OnShutdownStarting(DispatcherQueue sender, DispatcherQueueShutdownStartingEventArgs args)
    {
        lock (_gate)
        {
            _disposed = true;
            ++_version;
            CancelActive();
        }
        CleanupOnDispatcher();
    }

    private void CleanupOnDispatcher()
    {
        lock (_gate)
        {
            if (!_watchingShutdown) return;
            _watchingShutdown = false;
            try { dispatcher.ShutdownStarting -= OnShutdownStarting; }
            catch (Exception error) { Log(error); }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            ++_version;
            CancelActive();
        }
        Dispatch(CleanupOnDispatcher);
    }

    private static void Log(Exception error)
    {
        // The optional host log sink must not defeat exception containment.
        try { PackageLogger.LogVerbose($"[GlancePackage] image decode: {error.Message}"); }
        catch { }
    }
}
