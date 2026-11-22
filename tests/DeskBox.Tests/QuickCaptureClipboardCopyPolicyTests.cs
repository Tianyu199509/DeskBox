using DeskBox.Models;
using DeskBox.Services;
using DeskBox.ViewModels;

namespace DeskBox.Tests;

public sealed class QuickCaptureClipboardCopyPolicyTests
{
    [Fact]
    public void ShouldCopyBitmap_ForAnyImageQuickCaptureItem()
    {
        Assert.True(QuickCaptureClipboardCopyPolicy.ShouldCopyBitmap(CreateItem(
            QuickCaptureItemType.Image,
            isRecent: true)));
        Assert.False(QuickCaptureClipboardCopyPolicy.ShouldCopyBitmap(CreateItem(
            QuickCaptureItemType.Text,
            isRecent: true)));
        Assert.True(QuickCaptureClipboardCopyPolicy.ShouldCopyBitmap(CreateItem(
            QuickCaptureItemType.Image,
            isRecent: false)));
    }

    private static QuickCaptureItemViewModel CreateItem(
        QuickCaptureItemType type,
        bool isRecent)
    {
        return new QuickCaptureItemViewModel(
            new QuickCaptureItem
            {
                Body = type == QuickCaptureItemType.Image ? "Image" : "Text",
                Type = type,
                IsRecent = isRecent,
                SourceKind = QuickCaptureSourceKind.Clipboard
            },
            TestServices.CreateLocalizationService(),
            textSize: 14,
            iconSize: 14,
            searchText: null);
    }
}
