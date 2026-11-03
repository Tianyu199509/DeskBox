using DeskBox.Services;

namespace DeskBox.ViewModels;

public partial class SettingsViewModel
{
    public async Task RefreshQuickCaptureImageCacheInfoAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken = cancellationToken.CanBeCanceled ? cancellationToken : _lifetimeCts.Token;
        await RefreshQuickCaptureMirrorFolderAsync(cancellationToken);
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

    public async Task RefreshQuickCaptureMirrorFolderAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            string? mirrorPath = await _dataActions.GetQuickCaptureMirrorFolderAsync();
            _quickCaptureSettingsEditor.UpdateMirrorFolderPresentation(mirrorPath);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            App.Log($"[QuickCapture] Failed to refresh mirror folder path: {ex}");
            _quickCaptureSettingsEditor.UpdateMirrorFolderPresentation(null);
        }
    }
}
