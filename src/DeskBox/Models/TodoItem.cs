namespace DeskBox.Models;

public sealed class TodoItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string Text { get; set; } = string.Empty;

    public bool IsCompleted { get; set; }

    public int SortOrder { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
