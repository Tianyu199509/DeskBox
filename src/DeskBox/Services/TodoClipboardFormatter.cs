using DeskBox.ViewModels;

namespace DeskBox.Services;

public static class TodoClipboardFormatter
{
    public static string FormatSingleText(TodoItemViewModel item)
    {
        return FormatSingleText(item.Text);
    }

    public static string FormatSingle(
        TodoItemViewModel item,
        LocalizationService localizationService)
    {
        if (item.Attachments.Count == 0)
        {
            return FormatSingleText(item);
        }

        return string.Join(
            Environment.NewLine + Environment.NewLine,
            $"{localizationService.T("Clipboard.ContentLabel")}{Environment.NewLine}{FormatSingleText(item)}",
            FormatAttachments(item, localizationService));
    }

    public static string FormatSingleText(string? text)
    {
        return (text ?? string.Empty).Trim();
    }

    public static string FormatBatch(
        IReadOnlyList<TodoItemViewModel> items,
        LocalizationService localizationService)
    {
        return string.Join(
            $"{Environment.NewLine}{Environment.NewLine}---{Environment.NewLine}{Environment.NewLine}",
            items.Select((item, index) => FormatBatchItem(item, index, localizationService)));
    }

    private static string FormatBatchItem(
        TodoItemViewModel item,
        int index,
        LocalizationService localizationService)
    {
        string state = item.IsCompleted ? "- [x] \u2705" : "- [ ]";
        string marker = GetColorMarkerEmoji(item.ColorMarker);
        string text = NormalizeLineText(item.Text);
        string title = string.IsNullOrWhiteSpace(marker)
            ? text
            : $"{marker} {text}";
        string metadata = item.IsCompleted
            ? localizationService.Format(
                "Todo.Copy.Completed",
                FormatDateTime(item.CompletedAt ?? item.UpdatedAt, localizationService))
            : item.DueDate is { }
                ? localizationService.Format("Todo.Copy.Due", item.DueStatusText)
                : localizationService.T("Todo.Copy.NoDue");

        var sections = new List<string>
        {
            localizationService.Format("Todo.Copy.ItemHeader", index + 1),
            $"{state} {title}",
            metadata
        };
        if (item.Attachments.Count > 0)
        {
            sections.Add(FormatAttachments(item, localizationService));
        }

        return string.Join(Environment.NewLine, sections);
    }

    private static string FormatAttachments(
        TodoItemViewModel item,
        LocalizationService localizationService)
    {
        var lines = new List<string>
        {
            localizationService.Format("Clipboard.Attachments", item.Attachments.Count)
        };
        foreach (TodoAttachmentViewModel attachment in item.Attachments)
        {
            lines.Add($"- {attachment.DisplayName}");
            lines.Add($"  {localizationService.Format("Clipboard.Path", attachment.FilePath)}");
        }

        return string.Join(Environment.NewLine, lines);
    }

    private static string NormalizeLineText(string? text)
    {
        string normalized = string.Join(
            " ",
            (text ?? string.Empty)
                .Split(['\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries)
                .Select(part => part.Trim())
                .Where(part => !string.IsNullOrWhiteSpace(part)));

        return string.IsNullOrWhiteSpace(normalized) ? "Task" : normalized;
    }

    private static string FormatDateTime(DateTimeOffset value, LocalizationService localizationService)
    {
        DateTimeOffset localValue = value.ToLocalTime();
        var today = DateTimeOffset.Now.Date;
        string time = localValue.Second == 0
            ? localValue.ToString("HH:mm")
            : localValue.ToString("HH:mm:ss");

        if (localValue.Date == today)
        {
            return localizationService.Format("Todo.Due.TodayAt", time);
        }

        if (localValue.Date == today.AddDays(1))
        {
            return localizationService.Format("Todo.Due.TomorrowAt", time);
        }

        return localValue.Second == 0
            ? localValue.ToString("yyyy/M/d HH:mm")
            : localValue.ToString("yyyy/M/d HH:mm:ss");
    }

    private static string GetColorMarkerEmoji(string? colorMarker)
    {
        return colorMarker switch
        {
            "red" => "\U0001F534",
            "orange" => "\U0001F7E0",
            "yellow" => "\U0001F7E1",
            "green" => "\U0001F7E2",
            "blue" => "\U0001F535",
            "purple" => "\U0001F7E3",
            _ => string.Empty
        };
    }
}
