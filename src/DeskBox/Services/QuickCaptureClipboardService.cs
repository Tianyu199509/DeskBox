using DeskBox.Models;

namespace DeskBox.Services;

public sealed class QuickCaptureClipboardService : IDisposable
{
    public const int MaxClipboardTextCharacters = 20000;
    public const int MaxClipboardImageBytes = 12 * 1024 * 1024;

    private readonly SettingsService _settingsService;
    private readonly QuickCaptureService _quickCaptureService;
    private readonly IQuickCaptureClipboardReader _clipboardReader;
    private bool _isStarted;
    private bool _isProcessing;
    private bool _hasPendingCapture;
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
        _settingsService.SettingsChanged += OnSettingsChanged;
    }

    public void Refresh()
    {
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
        if (App.UiDispatcherQueue is { } dispatcherQueue)
        {
            dispatcherQueue.TryEnqueue(() => _ = CaptureCurrentClipboardAsync());
            return;
        }

        _ = CaptureCurrentClipboardAsync();
    }

    internal Task CaptureCurrentForTestingAsync()
    {
        return CaptureCurrentClipboardAsync();
    }

    public void Dispose()
    {
        _settingsService.SettingsChanged -= OnSettingsChanged;
        Stop();
    }

    private bool ShouldCaptureClipboard()
    {
        var settings = _settingsService.Settings;
        return settings.QuickCaptureEnabled &&
               settings.QuickCaptureClipboardEnabled &&
               settings.HasConfirmedQuickCaptureClipboardNotice;
    }

    private string BuildDisabledReason()
    {
        var settings = _settingsService.Settings;
        if (!settings.QuickCaptureEnabled)
        {
            return "disabled:quick-capture-off";
        }

        if (!settings.QuickCaptureClipboardEnabled)
        {
            return "disabled:clipboard-off";
        }

        if (!settings.HasConfirmedQuickCaptureClipboardNotice)
        {
            return "disabled:notice-unconfirmed";
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

    private void OnSettingsChanged()
    {
        App.UiDispatcherQueue?.TryEnqueue(Refresh);
    }

    private void Start()
    {
        if (_isStarted)
        {
            return;
        }

        _clipboardReader.ContentChanged += Clipboard_ContentChanged;
        _isStarted = true;
        App.Log("[QuickCaptureClipboard] Started");
        _ = CaptureCurrentClipboardAsync();
    }

    private void Stop()
    {
        if (!_isStarted)
        {
            return;
        }

        _clipboardReader.ContentChanged -= Clipboard_ContentChanged;
        _isStarted = false;
        App.Log("[QuickCaptureClipboard] Stopped");
    }

    private void Clipboard_ContentChanged(object? sender, object e)
    {
        App.LogVerbose("[QuickCaptureClipboard] ContentChanged");
        if (App.UiDispatcherQueue is { } dispatcherQueue)
        {
            dispatcherQueue.TryEnqueue(() => _ = CaptureCurrentClipboardAsync());
            return;
        }

        _ = CaptureCurrentClipboardAsync();
    }

    private async Task CaptureCurrentClipboardAsync()
    {
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

                QuickCaptureClipboardContent? content = await _clipboardReader.ReadContentAsync();
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
        catch (Exception ex)
        {
            SetReason("failed:read-or-save");
            App.Log($"[QuickCaptureClipboardService] Failed to capture clipboard text: {ex}");
        }
        finally
        {
            _isProcessing = false;
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
