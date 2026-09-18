using DeskBox.Helpers;
using DeskBox.Platform;
using Microsoft.Windows.Storage.Pickers;

namespace DeskBox.Services;

public static class FileOpenPickerService
{
    public static async Task<IReadOnlyList<string>> PickFilesAsync(
        IntPtr ownerHwnd,
        string? suggestedFolder = null)
    {
        ValidateOwnerWindowHandle(ownerHwnd);

        var ownerWindowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(
            ownerHwnd);
        var picker = new FileOpenPicker(ownerWindowId)
        {
            SuggestedStartLocation = PickerLocationId.Desktop
        };
        if (!string.IsNullOrWhiteSpace(suggestedFolder))
        {
            string normalizedFolder = Path.GetFullPath(suggestedFolder);
            if (!Directory.Exists(normalizedFolder))
            {
                throw new DirectoryNotFoundException(
                    $"The suggested file-picker folder does not exist: '{normalizedFolder}'.");
            }

            picker.SuggestedFolder = normalizedFolder;
        }

        IReadOnlyList<PickFileResult> files =
            await picker.PickMultipleFilesAsync();
        return files
            .Select(file => file.Path)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    internal static void ValidateOwnerWindowHandle(IntPtr ownerHwnd)
    {
        if (ownerHwnd == IntPtr.Zero || !Win32Helper.IsWindow(ownerHwnd))
        {
            throw new ArgumentException(
                "FileOpenPicker requires a valid owner window handle.",
                nameof(ownerHwnd));
        }
    }
}
