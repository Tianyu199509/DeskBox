using DeskBox.Models;
using DeskBox.ViewModels;

namespace DeskBox.Services;

internal static class QuickCaptureClipboardCopyPolicy
{
    public static bool ShouldCopyBitmap(QuickCaptureItemViewModel item) =>
        item.Type == QuickCaptureItemType.Image;
}
