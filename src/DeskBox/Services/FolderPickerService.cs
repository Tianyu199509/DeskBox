using DeskBox.Helpers;
using DeskBox.Platform;
using Microsoft.Windows.Storage.Pickers;

namespace DeskBox.Services;

public static class FolderPickerService
{
    public static async Task<string?> PickFolderAsync(IntPtr ownerHwnd)
    {
        ValidateOwnerWindowHandle(ownerHwnd);

        try
        {
            var ownerWindowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(ownerHwnd);
            var picker = new FolderPicker(ownerWindowId);
            PickFolderResult? result = await picker.PickSingleFolderAsync();
            return string.IsNullOrWhiteSpace(result?.Path) ? null : result.Path;
        }
        catch (Exception ex)
        {
            App.Log($"[FolderPicker] Failed to pick folder: {ex}");
            return null;
        }
    }

    internal static void ValidateOwnerWindowHandle(IntPtr ownerHwnd)
    {
        if (ownerHwnd == IntPtr.Zero || !Win32Helper.IsWindow(ownerHwnd))
        {
            throw new ArgumentException(
                "FolderPicker requires a valid owner window handle.",
                nameof(ownerHwnd));
        }
    }
}
