namespace DeskBox.Models;

public sealed class TodoWidgetData
{
    public int Version { get; set; } = 2;

    public List<TodoItem> Items { get; set; } = [];
}
