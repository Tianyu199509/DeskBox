using DeskBox.Models;
using DeskBox.Contracts;

namespace DeskBox.Services;

public sealed class QuickCaptureClipboardService : IQuickCaptureClipboardSession
{
    public const int MaxClipboardTextCharacters = 20000;
    public const int MaxClipboardImageBytes = 12 * 1024 * 1024;

    private readonly SettingsService _settingsService;
    private readonly QuickCaptureService _quickCaptureService;
    private readonly IQuickCaptureClipboardReader _clipboardReader;
    private bool _isStarted;
    private volatile bool _isProcessing;
    private volatile bool _hasPendingCapture;
    private Task _activeCapture = Task.CompletedTask;
    private CancellationTokenSource _readCancellation = new();
    private int _captureGeneration;
    private bool _stopping;
    private bool _disposed;
    private string? _lastStateLog;
    private DateTimeOffset? _lastCapturedAt;
    private string _lastReason = "disabled:initial";
    private DateTimeOffset? _lastReasonAt;

    public event Action? DiagnosticsChanged;

    public QuickCaptureClipboardService(
        SettingsService settingsService,
        QuickCaptureService quickCaptureService,
        IQuickCaptureClipboardReader? clipboardReader = null)
    {
        _settingsService = settingsService;
        _quickCaptureService = quickCaptureService;
        _clipboardReader = clipboardReader ?? new WindowsQuickCaptureClipboardReader();
    }

    public void Refresh()
    {
        if (_stopping || _disposed) return;
        if (App.UiDispatcherQueue is { } dispatcherQueue &&
            !dispatcherQueue.HasThreadAccess)
        {
            dispatcherQueue.TryEnqueue(Refresh);
            return;
        }

        if (ShouldCaptureClipboard())
        {
            SetReason("enabled");
            Start();
        }
        else
        {
            SetReason(BuildDisabledReason());
            Stop();
        }
    }

    public QuickCaptureClipboardDiagnostics GetDiagnostics()
    {
        return new QuickCaptureClipboardDiagnostics(
            IsRecording: ShouldCaptureClipboard(),
            IsListening: _isStarted,
            LastCapturedAt: _lastCapturedAt,
            LastReason: _lastReason,
            LastReasonAt: _lastReasonAt);
    }

    public void CaptureCurrent()
    {
        if (_stopping || _disposed) return;
        if (App.UiDispatcherQueue is { } dispatcherQueue)
        {
            dispatcherQueue.TryEnqueue(() => _ = BeginCaptureAsync());
            return;
        }

        _ = BeginCaptureAsync();
    }

    internal Task CaptureCurrentForTestingAsync()
    {
        return BeginCaptureAsync();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
        _readCancellation.Dispose();
    }

    public async Task StopAsync()
    {
        if (_disposed)
        {
            await _activeCapture;
            return;
        }
        _stopping = true;
        Stop();
        await _activeCapture;
    }

    private bool ShouldCaptureClipboard()
    {
        return !_stopping && !_disposed &&
               _settingsService.Settings.QuickCaptureEnabled &&
               _settingsService.Settings.QuickCaptureClipboardEnabled;
    }

    private string BuildDisabledReason()
    {
        if (!_settingsService.Settings.QuickCaptureEnabled)
        {
            return "disabled:quick-capture-off";
        }

        if (!_settingsService.Settings.QuickCaptureClipboardEnabled)
        {
            return "disabled:clipboard-off";
        }

        return "disabled:unknown";
    }

    private void LogState(string state)
    {
        if (string.Equals(_lastStateLog, state, StringComparison.Ordinal))
        {
            return;
        }

        _lastStateLog = state;
        App.Log($"[QuickCaptureClipboard] State {state}");
    }

    private void Start()
    {
        if (_isStarted || _stopping || _disposed)
        {
            return;
        }

        if (_readCancellation.IsCancellationRequested)
        {
            _readCancellation.Dispose();
            _readCancellation = new();
        }

        _clipboardReader.ContentChanged += Clipboard_ContentChanged;
        _isStarted = true;
        App.Log($"[QuickCaptureClipboard] Started uiThread={App.UiDispatcherQueue?.HasThreadAccess.ToString() ?? "unknown"}");
        _ = BeginCaptureAsync();
    }

    private void Stop()
    {
        ++_captureGeneration;
        _hasPendingCapture = false;
        if (!_readCancellation.IsCancellationRequested) _readCancellation.Cancel();
        if (_isStarted)
        {
            _clipboardReader.ContentChanged -= Clipboard_ContentChanged;
            _isStarted = false;
            App.Log("[QuickCaptureClipboard] Stopped");
        }
    }

    private void Clipboard_ContentChanged(object? sender, object e)
    {
        App.LogVerbose("[QuickCaptureClipboard] ContentChanged");
        if (App.UiDispatcherQueue is { } dispatcherQueue)
        {
            dispatcherQueue.TryEnqueue(() => _ = BeginCaptureAsync());
            return;
        }

        _ = BeginCaptureAsync();
    }

    private Task BeginCaptureAsync()
    {
        if (_stopping || _disposed) return Task.CompletedTask;
        if (_isProcessing)
        {
            _hasPendingCapture = true;
            return _activeCapture;
        }

        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _activeCapture = completion.Task;
        _ = CompleteCaptureAsync(completion);
        return completion.Task;
    }

    private async Task CompleteCaptureAsync(TaskCompletionSource completion)
    {
        try { await CaptureCurrentClipboardAsync(); }
        catch (Exception ex) { App.Log($"[QuickCaptureClipboardService] Capture callback failed: {ex}"); }
        finally { completion.TrySetResult(); }
    }

    private async Task CaptureCurrentClipboardAsync()
    {
        int generation = _captureGeneration;
        CancellationToken readToken = _readCancellation.Token;
        if (!ShouldCaptureClipboard())
        {
            SetReason(BuildDisabledReason());
            return;
        }

        if (_isProcessing)
        {
            _hasPendingCapture = true;
            return;
        }

        _isProcessing = true;
        try
        {
            do
            {
                _hasPendingCapture = false;
                if (!ShouldCaptureClipboard())
                {
                    SetReason(BuildDisabledReason());
                    return;
                }

                QuickCaptureClipboardContent? content =
                    await _clipboardReader.ReadContentAsync().WaitAsync(readToken);
                // A read that started before disable/retirement must never
                // turn into a new note after that session has stopped.
                if (generation != _captureGeneration || !ShouldCaptureClipboard())
                {
                    SetReason(BuildDisabledReason());
                    return;
                }
                if (content is null || (!content.HasImage && string.IsNullOrWhiteSpace(content.Text)))
                {
                    SetReason("ignored:empty-or-unsupported");
                    continue;
                }

                if (DeskBoxClipboardWriteScope.ShouldIgnore(content))
                {
                    SetReason("ignored:deskbox-write");
                    continue;
                }

                int maxItems = QuickCaptureService.NormalizeRecentLimit(_settingsService.Settings.QuickCaptureRecentLimit);
                QuickCaptureItem? item;
                if (content.HasImage)
                {
                    if (!_settingsService.Settings.QuickCaptureImageClipboardEnabled)
                    {
                        SetReason("ignored:image-recording-off");
                        continue;
                    }

                    if (content.ImagePngBytes!.Length > MaxClipboardImageBytes)
                    {
                        SetReason("ignored:image-too-large");
                        continue;
                    }

                    item = await _quickCaptureService.AddRecentClipboardImageAsync(content.ImagePngBytes!, maxItems);
                }
                else
                {
                    string text = content.Text!;
                    if (text.Length > MaxClipboardTextCharacters)
                    {
                        SetReason("ignored:text-too-large");
                        continue;
                    }

                    item = await _quickCaptureService.AddRecentClipboardItemAsync(text, maxItems);
                }
                if (generation != _captureGeneration || !ShouldCaptureClipboard()) return;
                if (item is null)
                {
                    SetReason("ignored:duplicate-or-app-write");
                }
                else
                {
                    _lastCapturedAt = DateTimeOffset.Now;
                    SetReason($"captured:{item.Type}");
                }
            } while (_hasPendingCapture);
        }
        catch (OperationCanceledException) when (readToken.IsCancellationRequested)
        {
            // A clipboard provider may still finish later, but this session
            // will never observe that result or start a new data write.
        }
        catch (Exception ex)
        {
            SetReason("failed:read-or-save");
            App.Log($"[QuickCaptureClipboardService] Failed to capture clipboard text: {ex}");
        }
        finally
        {
            _isProcessing = false;
            if (_hasPendingCapture && ShouldCaptureClipboard()) _ = BeginCaptureAsync();
        }
    }

    private void SetReason(string reason)
    {
        _lastReason = reason;
        _lastReasonAt = DateTimeOffset.Now;
        LogState(reason);
        DiagnosticsChanged?.Invoke();
    }

}
