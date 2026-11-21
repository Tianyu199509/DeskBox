using DeskBox.ViewModels;

namespace DeskBox.Controls.Todo;

/// <summary>
/// Pure mapping and drag-drop target helper for the Todo filter header bar.
/// </summary>
internal static class TodoFilterBarController
{
    public static TodoFilter GetFilterFromSegmentIndex(int? selectedIndex) =>
        selectedIndex switch
        {
            1 => TodoFilter.Active,
            2 => TodoFilter.Today,
            3 => TodoFilter.Overdue,
            4 => TodoFilter.ThisWeek,
            5 => TodoFilter.ThisMonth,
            6 => TodoFilter.Important,
            7 => TodoFilter.Completed,
            _ => TodoFilter.All
        };

    public static int GetSegmentIndex(TodoFilter filter) =>
        filter switch
        {
            TodoFilter.Active => 1,
            TodoFilter.Today => 2,
            TodoFilter.Overdue => 3,
            TodoFilter.ThisWeek => 4,
            TodoFilter.ThisMonth => 5,
            TodoFilter.Important => 6,
            TodoFilter.Completed => 7,
            _ => 0
        };

    public static bool TryGetTabDropTarget(string? tag, out TodoFilter target)
    {
        target = tag switch
        {
            "Active" => TodoFilter.Active,
            "Today" => TodoFilter.Today,
            "Important" => TodoFilter.Important,
            "Completed" => TodoFilter.Completed,
            _ => TodoFilter.All
        };
        return target is TodoFilter.Active or TodoFilter.Today or TodoFilter.Important or TodoFilter.Completed;
    }

    public static string GetTabDropCaptionKey(TodoFilter target) =>
        target switch
        {
            TodoFilter.Active => "Todo.DropTab.Active",
            TodoFilter.Today => "Todo.DropTab.Today",
            TodoFilter.Important => "Todo.DropTab.Important",
            _ => "Todo.DropTab.Completed"
        };
}
