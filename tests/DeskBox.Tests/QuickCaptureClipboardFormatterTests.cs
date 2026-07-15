using DeskBox.Models;
using DeskBox.Services;
using DeskBox.ViewModels;

namespace DeskBox.Tests;

public sealed class QuickCaptureClipboardFormatterTests
{
    [Fact]
    public void FormatSingle_WithoutAttachments_PreservesSimpleText()
    {
        var localization = TestServices.CreateLocalizationService();
        var item = CreateViewModel(new QuickCaptureItem { Body = "simple note" }, localization);

        string formatted = QuickCaptureClipboardFormatter.FormatSingle(item, localization);

        Assert.Equal("simple note", formatted);
    }

    [Fact]
    public void FormatSingle_WithAttachments_IncludesTextAndAllPaths()
    {
        var localization = TestServices.CreateLocalizationService();
        var item = CreateViewModel(new QuickCaptureItem
        {
            Body = "meeting notes",
            Attachments =
            [
                new TodoAttachment
                {
                    FilePath = @"C:\captures\one.png",
                    DisplayName = "one.png",
                    Type = "image"
                },
                new TodoAttachment
                {
                    FilePath = @"C:\captures\brief.pdf",
                    DisplayName = "brief.pdf",
                    Type = "pdf"
                }
            ]
        }, localization);

        string formatted = QuickCaptureClipboardFormatter.FormatSingle(item, localization);

        Assert.Contains("Content:", formatted, StringComparison.Ordinal);
        Assert.Contains("meeting notes", formatted, StringComparison.Ordinal);
        Assert.Contains("Attachments (2):", formatted, StringComparison.Ordinal);
        Assert.Contains(@"Path: C:\captures\one.png", formatted, StringComparison.Ordinal);
        Assert.Contains(@"Path: C:\captures\brief.pdf", formatted, StringComparison.Ordinal);
    }

    [Fact]
    public void FormatBatch_NumbersAndSeparatesRecords()
    {
        var localization = TestServices.CreateLocalizationService();
        QuickCaptureItemViewModel first = CreateViewModel(
            new QuickCaptureItem { Body = "first" },
            localization);
        QuickCaptureItemViewModel second = CreateViewModel(new QuickCaptureItem
        {
            Body = "second",
            Attachments =
            [
                new TodoAttachment
                {
                    FilePath = @"D:\second.txt",
                    DisplayName = "second.txt"
                }
            ]
        }, localization);

        string formatted = QuickCaptureClipboardFormatter.FormatBatch([first, second], localization);

        Assert.Contains("Note 1", formatted, StringComparison.Ordinal);
        Assert.Contains("Note 2", formatted, StringComparison.Ordinal);
        Assert.Contains("---", formatted, StringComparison.Ordinal);
        Assert.Contains(@"Path: D:\second.txt", formatted, StringComparison.Ordinal);
    }

    [Fact]
    public void FormatSingle_UsesChineseLabelsWhenConfigured()
    {
        var localization = TestServices.CreateLocalizationService(SettingsService.LanguageChinese);
        var item = CreateViewModel(new QuickCaptureItem
        {
            Body = "会议记录",
            Attachments =
            [
                new TodoAttachment
                {
                    FilePath = @"C:\资料\会议.pdf",
                    DisplayName = "会议.pdf"
                }
            ]
        }, localization);

        string formatted = QuickCaptureClipboardFormatter.FormatSingle(item, localization);

        Assert.Contains("内容：", formatted, StringComparison.Ordinal);
        Assert.Contains("附件（1）：", formatted, StringComparison.Ordinal);
        Assert.Contains(@"路径：C:\资料\会议.pdf", formatted, StringComparison.Ordinal);
    }

    private static QuickCaptureItemViewModel CreateViewModel(
        QuickCaptureItem item,
        LocalizationService localization)
    {
        return new QuickCaptureItemViewModel(
            item,
            localization,
            textSize: 12,
            iconSize: 28,
            searchText: null);
    }
}
