using System.Globalization;

namespace DeskBox.ViewModels;

public partial class SettingsViewModel
{
    public string QuickCaptureStatusText => QuickCaptureEnabled
        ? _localizationService.T("Settings.QuickCapture.Status.Enabled")
        : _localizationService.T("Settings.QuickCapture.Status.Disabled");
    public string QuickCaptureDependencyStatusText => QuickCaptureEnabled
        ? _localizationService.T("Settings.QuickCapture.Dependency.Enabled")
        : _localizationService.T("Settings.QuickCapture.Dependency.Disabled");
    public string QuickCaptureRecentLimitText => _localizationService.Format("Settings.QuickCapture.RecentLimitValue", QuickCaptureRecentLimit);
    public string QuickCaptureClipboardDiagnosticsText
    {
        get => _quickCaptureClipboardDiagnosticsText;
        private set => SetProperty(ref _quickCaptureClipboardDiagnosticsText, value);
    }

    public string QuickCaptureRecentLimitInput
    {
        get => QuickCaptureRecentLimit.ToString(CultureInfo.CurrentCulture);
        set => ApplyQuickCaptureRecentLimitInput(value);
    }
    public string QuickCaptureImageCacheText
    {
        get => _quickCaptureImageCacheText;
        private set => SetProperty(ref _quickCaptureImageCacheText, value);
    }

    public bool CanClearQuickCaptureImageCache
    {
        get => _canClearQuickCaptureImageCache;
        private set => SetProperty(ref _canClearQuickCaptureImageCache, value);
    }

    public async Task RefreshQuickCaptureImageCacheInfoAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken = cancellationToken.CanBeCanceled ? cancellationToken : _lifetimeCts.Token;
        if (App.Current?.QuickCaptureService is not { } quickCaptureService)
        {
            QuickCaptureImageCacheText = _localizationService.T("Settings.QuickCapture.ImageCacheUnavailable");
            CanClearQuickCaptureImageCache = false;
            return;
        }

        try
        {
            var info = await quickCaptureService.GetImageCacheInfoAsync().WaitAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (info.TotalFileCount == 0)
            {
                QuickCaptureImageCacheText = _localizationService.T("Settings.QuickCapture.ImageCacheEmpty");
                CanClearQuickCaptureImageCache = false;
                return;
            }

            QuickCaptureImageCacheText = _localizationService.Format(
                "Settings.QuickCapture.ImageCacheValue",
                info.TotalFileCount,
                FormatBytes(info.TotalBytes),
                info.UnusedFileCount,
                FormatBytes(info.UnusedBytes));
            CanClearQuickCaptureImageCache = info.UnusedFileCount > 0;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            App.Log($"[QuickCapture] Failed to refresh image cache info: {ex}");
            QuickCaptureImageCacheText = _localizationService.T("Settings.QuickCapture.ImageCacheUnavailable");
            CanClearQuickCaptureImageCache = false;
        }
    }

    public void RefreshQuickCaptureClipboardDiagnostics()
    {
        if (App.Current?.QuickCaptureClipboardService is not { } clipboardService)
        {
            QuickCaptureClipboardDiagnosticsText = _localizationService.T("Settings.QuickCapture.ClipboardDiagnosticsUnavailable");
            return;
        }

        var diagnostics = clipboardService.GetDiagnostics();
        string reasonText = GetQuickCaptureClipboardReasonText(diagnostics.LastReason);
        if (diagnostics.LastCapturedAt is { } capturedAt)
        {
            QuickCaptureClipboardDiagnosticsText = _localizationService.Format(
                diagnostics.IsRecording && diagnostics.IsListening
                    ? "Settings.QuickCapture.ClipboardDiagnosticsRecording"
                    : "Settings.QuickCapture.ClipboardDiagnosticsNotRecording",
                capturedAt.ToLocalTime().ToString("HH:mm", CultureInfo.CurrentCulture),
                reasonText);
            return;
        }

        QuickCaptureClipboardDiagnosticsText = _localizationService.Format(
            diagnostics.IsRecording && diagnostics.IsListening
                ? "Settings.QuickCapture.ClipboardDiagnosticsNoCapture"
                : "Settings.QuickCapture.ClipboardDiagnosticsNotRecordingNoCapture",
            reasonText);
    }

    private string GetQuickCaptureClipboardReasonText(string reason)
    {
        string key = reason switch
        {
            "enabled" => "Settings.QuickCapture.ClipboardReason.Enabled",
            "disabled:quick-capture-off" => "Settings.QuickCapture.ClipboardReason.QuickCaptureOff",
            "disabled:clipboard-off" => "Settings.QuickCapture.ClipboardReason.ClipboardOff",
            "disabled:notice-unconfirmed" => "Settings.QuickCapture.ClipboardReason.NoticeUnconfirmed",
            "ignored:empty-or-unsupported" => "Settings.QuickCapture.ClipboardReason.EmptyOrUnsupported",
            "ignored:deskbox-write" => "Settings.QuickCapture.ClipboardReason.DeskBoxWrite",
            "ignored:image-recording-off" => "Settings.QuickCapture.ClipboardReason.ImageOff",
            "ignored:image-too-large" => "Settings.QuickCapture.ClipboardReason.ImageTooLarge",
            "ignored:text-too-large" => "Settings.QuickCapture.ClipboardReason.TextTooLarge",
            "ignored:duplicate-or-app-write" => "Settings.QuickCapture.ClipboardReason.Duplicate",
            "failed:read-or-save" => "Settings.QuickCapture.ClipboardReason.Failed",
            _ when reason.StartsWith("captured:", StringComparison.Ordinal) => "Settings.QuickCapture.ClipboardReason.Captured",
            _ => "Settings.QuickCapture.ClipboardReason.Unknown"
        };

        return _localizationService.T(key);
    }

    private void OnQuickCaptureClipboardDiagnosticsChanged()
    {
        if (App.UiDispatcherQueue is { } dispatcherQueue)
        {
            dispatcherQueue.TryEnqueue(RefreshQuickCaptureClipboardDiagnostics);
            return;
        }

        RefreshQuickCaptureClipboardDiagnostics();
    }
}
