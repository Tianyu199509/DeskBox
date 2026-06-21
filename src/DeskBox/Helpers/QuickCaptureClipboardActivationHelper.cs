using DeskBox.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DeskBox.Helpers;

public static class QuickCaptureClipboardActivationHelper
{
    public static async Task<bool> EnableAsync(XamlRoot? xamlRoot, LocalizationService localizationService)
    {
        if (xamlRoot is null)
        {
            return false;
        }

        var settingsService = App.Current.SettingsService;
        if (!settingsService.Settings.HasConfirmedQuickCaptureClipboardNotice)
        {
            var dialog = new ContentDialog
            {
                XamlRoot = xamlRoot,
                Title = localizationService.T("Settings.QuickCapture.ClipboardNoticeTitle"),
                PrimaryButtonText = localizationService.T("Common.Enable"),
                CloseButtonText = localizationService.T("Common.Cancel"),
                DefaultButton = ContentDialogButton.Close,
                Content = new TextBlock
                {
                    Text = localizationService.T("Settings.QuickCapture.ClipboardNoticeBody"),
                    TextWrapping = TextWrapping.Wrap
                }
            };

            if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            {
                App.Log("[QuickCaptureClipboard] Enable canceled");
                return false;
            }

            settingsService.Settings.HasConfirmedQuickCaptureClipboardNotice = true;
        }

        settingsService.Settings.QuickCaptureEnabled = true;
        settingsService.Settings.QuickCaptureClipboardEnabled = true;
        await settingsService.SaveAsync();
        App.Current.QuickCaptureClipboardService?.Refresh();
        App.Current.QuickCaptureClipboardService?.CaptureCurrent();
        App.Log("[QuickCaptureClipboard] Enabled");
        return true;
    }
}
