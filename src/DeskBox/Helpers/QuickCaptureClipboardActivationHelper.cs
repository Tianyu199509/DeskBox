using DeskBox.Models;
using DeskBox.Services;
using Microsoft.UI.Xaml;

namespace DeskBox.Helpers;

public static class QuickCaptureClipboardActivationHelper
{
    public static async Task<bool> EnableAsync(XamlRoot? xamlRoot, LocalizationService localizationService)
    {
        await App.Current.QuickCaptureSettings.SetClipboardEnabledAsync(
            enabled: true, captureCurrent: true);
        App.Log("[QuickCaptureClipboard] Enabled");
        return true;
    }
}
