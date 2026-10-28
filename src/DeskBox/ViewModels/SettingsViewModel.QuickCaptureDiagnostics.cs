using DeskBox.Services;

namespace DeskBox.ViewModels;

public partial class SettingsViewModel
{
    /// <summary>
    /// Forwarder for the code-behind paths that refresh the clipboard
    /// diagnostics line; the line itself lives on the Quick Capture editor.
    /// </summary>
    public void RefreshQuickCaptureClipboardDiagnostics() =>
        _quickCaptureSettingsEditor.RefreshClipboardDiagnostics();

    public async Task RefreshQuickCaptureImageCacheInfoAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken = cancellationToken.CanBeCanceled ? cancellationToken : _lifetimeCts.Token;
        if (await _dataActions.GetQuickCaptureImageCacheInfoAsync() is not { } info)
        {
            _quickCaptureSettingsEditor.UpdateImageCachePresentation(
                _localizationService.T("Settings.QuickCapture.ImageCacheUnavailable"),
                canClear: false);
            return;
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (info.TotalFileCount == 0)
            {
                _quickCaptureSettingsEditor.UpdateImageCachePresentation(
                    _localizationService.T("Settings.QuickCapture.ImageCacheEmpty"),
                    canClear: false);
                return;
            }

            _quickCaptureSettingsEditor.UpdateImageCachePresentation(
                _localizationService.Format(
                    "Settings.QuickCapture.ImageCacheValue",
                    info.TotalFileCount,
                    FormatBytes(info.TotalBytes),
                    info.UnusedFileCount,
                    FormatBytes(info.UnusedBytes)),
                canClear: info.UnusedFileCount > 0);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            App.Log($"[QuickCapture] Failed to refresh image cache info: {ex}");
            _quickCaptureSettingsEditor.UpdateImageCachePresentation(
                _localizationService.T("Settings.QuickCapture.ImageCacheUnavailable"),
                canClear: false);
        }
    }

    private void OnQuickCaptureClipboardDiagnosticsChanged()
    {
        if (_isDisposed) return;
        if (App.UiDispatcherQueue is { } dispatcherQueue)
        {
            dispatcherQueue.TryEnqueue(RefreshQuickCaptureClipboardDiagnostics);
            return;
        }

        RefreshQuickCaptureClipboardDiagnostics();
    }
}
