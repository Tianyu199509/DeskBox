using DeskBox.Models;
using DeskBox.ViewModels;

namespace DeskBox.Services;

public static class QuickCaptureClipboardFormatter
{
    public static string FormatSingle(
        QuickCaptureItemViewModel item,
        LocalizationService localizationService)
    {
        return FormatContent(
            item.CopyText,
            item.Attachments.Select(attachment => attachment.Attachment),
            localizationService,
            includeContentLabel: item.Attachments.Count > 0);
    }

    public static string FormatContent(
        string? content,
        IEnumerable<TodoAttachment> attachments,
        LocalizationService localizationService,
        bool includeContentLabel = true)
    {
        string normalizedContent = (content ?? string.Empty).Trim();
        TodoAttachment[] attachmentList = attachments
            .Where(attachment => attachment is not null && !string.IsNullOrWhiteSpace(attachment.FilePath))
            .ToArray();
        if (attachmentList.Length == 0)
        {
            return normalizedContent;
        }

        var sections = new List<string>();
        if (!string.IsNullOrWhiteSpace(normalizedContent))
        {
            sections.Add(includeContentLabel
                ? $"{localizationService.T("Clipboard.ContentLabel")}{Environment.NewLine}{normalizedContent}"
                : normalizedContent);
        }

        sections.Add(FormatAttachments(attachmentList, localizationService));
        return string.Join(Environment.NewLine + Environment.NewLine, sections);
    }

    public static string FormatBatch(
        IReadOnlyList<QuickCaptureItemViewModel> items,
        LocalizationService localizationService)
    {
        return string.Join(
            $"{Environment.NewLine}{Environment.NewLine}---{Environment.NewLine}{Environment.NewLine}",
            items.Select((item, index) => string.Join(
                Environment.NewLine,
                localizationService.Format("QuickCapture.Copy.ItemHeader", index + 1),
                FormatContent(
                    item.CopyText,
                    item.Attachments.Select(attachment => attachment.Attachment),
                    localizationService))));
    }

    private static string FormatAttachments(
        IReadOnlyList<TodoAttachment> attachments,
        LocalizationService localizationService)
    {
        var lines = new List<string>
        {
            localizationService.Format("Clipboard.Attachments", attachments.Count)
        };
        foreach (TodoAttachment attachment in attachments)
        {
            string displayName = string.IsNullOrWhiteSpace(attachment.DisplayName)
                ? Path.GetFileName(attachment.FilePath)
                : attachment.DisplayName.Trim();
            lines.Add($"- {displayName}");
            lines.Add($"  {localizationService.Format("Clipboard.Path", attachment.FilePath)}");
        }

        return string.Join(Environment.NewLine, lines);
    }
}
