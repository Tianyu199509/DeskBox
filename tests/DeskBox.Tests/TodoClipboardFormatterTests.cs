using DeskBox.Models;
using DeskBox.Services;
using DeskBox.ViewModels;
using Microsoft.UI.Xaml;

namespace DeskBox.Tests;

public sealed class TodoClipboardFormatterTests
{
    [Fact]
    public void FormatSingleText_TrimsText()
    {
        var item = new TodoItemViewModel(new TodoItem
        {
            Text = "  review release notes  "
        });

        string formatted = TodoClipboardFormatter.FormatSingleText(item);

        Assert.Equal("review release notes", formatted);
    }

    [Fact]
    public void FormatBatch_UsesMarkdownFriendlyLines()
    {
        var localization = TestServices.CreateLocalizationService();
        var active = new TodoItemViewModel(new TodoItem
        {
            Text = "review release notes",
            ColorMarker = TodoItem.RedColorMarker,
            DueDate = DateTimeOffset.Now.AddHours(1)
        }, localization);
        var noDue = new TodoItemViewModel(new TodoItem
        {
            Text = "clean inbox"
        }, localization);
        var completed = new TodoItemViewModel(new TodoItem
        {
            Text = "send build",
            IsCompleted = true,
            CompletedAt = DateTimeOffset.Now.AddMinutes(-10)
        }, localization);

        string formatted = TodoClipboardFormatter.FormatBatch([active, noDue, completed], localization);

        Assert.Contains("Todo 1", formatted, StringComparison.Ordinal);
        Assert.Contains("- [ ] \U0001F534 review release notes", formatted, StringComparison.Ordinal);
        Assert.Contains("Due:", formatted, StringComparison.Ordinal);
        Assert.Contains("Todo 2", formatted, StringComparison.Ordinal);
        Assert.Contains("- [ ] clean inbox", formatted, StringComparison.Ordinal);
        Assert.Contains("No due date", formatted, StringComparison.Ordinal);
        Assert.Contains("Todo 3", formatted, StringComparison.Ordinal);
        Assert.Contains("- [x] \u2705 send build", formatted, StringComparison.Ordinal);
        Assert.Contains("Completed:", formatted, StringComparison.Ordinal);
        Assert.Contains("---", formatted, StringComparison.Ordinal);
    }

    [Fact]
    public void FormatSingle_WithAttachments_IncludesContentNamesAndPaths()
    {
        var localization = TestServices.CreateLocalizationService();
        var item = new TodoItemViewModel(new TodoItem
        {
            Text = "review contract",
            Attachments =
            [
                new TodoAttachment
                {
                    FilePath = @"C:\docs\contract.pdf",
                    DisplayName = "contract.pdf"
                },
                new TodoAttachment
                {
                    FilePath = @"D:\images\markup.png",
                    DisplayName = "markup.png",
                    Type = "image"
                }
            ]
        }, localization);

        string formatted = TodoClipboardFormatter.FormatSingle(item, localization);

        Assert.Contains("Content:", formatted, StringComparison.Ordinal);
        Assert.Contains("review contract", formatted, StringComparison.Ordinal);
        Assert.Contains("Attachments (2):", formatted, StringComparison.Ordinal);
        Assert.Contains("- contract.pdf", formatted, StringComparison.Ordinal);
        Assert.Contains(@"Path: C:\docs\contract.pdf", formatted, StringComparison.Ordinal);
        Assert.Contains(@"Path: D:\images\markup.png", formatted, StringComparison.Ordinal);
    }

    [Fact]
    public void DueStatusVisibility_SeparatesOverdueFromNormalDueDate()
    {
        var overdue = new TodoItemViewModel(new TodoItem
        {
            Text = "overdue",
            DueDate = DateTimeOffset.Now.AddMinutes(-5)
        });
        var upcoming = new TodoItemViewModel(new TodoItem
        {
            Text = "upcoming",
            DueDate = DateTimeOffset.Now.AddMinutes(5)
        });

        Assert.Equal(Visibility.Collapsed, overdue.DueStatusNormalVisibility);
        Assert.Equal(Visibility.Visible, overdue.DueStatusOverdueVisibility);
        Assert.Equal(Visibility.Visible, upcoming.DueStatusNormalVisibility);
        Assert.Equal(Visibility.Collapsed, upcoming.DueStatusOverdueVisibility);
    }

    [Fact]
    public void DueStatusText_AppendsOverdueSuffixAfterTime()
    {
        var localization = TestServices.CreateLocalizationService();
        var item = new TodoItemViewModel(new TodoItem
        {
            Text = "overdue",
            DueDate = DateTimeOffset.Now.AddMinutes(-5)
        }, localization);

        Assert.Contains(localization.T("Todo.Due.OverdueSuffix"), item.DueStatusText, StringComparison.Ordinal);
        Assert.Contains("\u00B7", item.DueStatusText, StringComparison.Ordinal);
    }
}
