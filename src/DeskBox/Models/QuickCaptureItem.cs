namespace DeskBox.Models;

public sealed class QuickCaptureItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public QuickCaptureItemType Type { get; set; } = QuickCaptureItemType.Text;

    public string Body { get; set; } = string.Empty;

    public string? Title { get; set; }

    public string? Url { get; set; }

    public string? ImagePath { get; set; }

    public string? ContentHash { get; set; }

    public List<TodoAttachment> Attachments { get; set; } = [];

    public bool IsPinned { get; set; }

    public bool IsRecent { get; set; }

    public bool IsDeleted { get; set; }

    public QuickCaptureAppearancePreset AppearancePreset { get; set; } = QuickCaptureAppearancePreset.Default;

    public QuickCaptureSourceKind SourceKind { get; set; } = QuickCaptureSourceKind.Manual;

    public List<string> Tags { get; set; } = [];

    public DateTimeOffset? ArchivedAt { get; set; }

    public int SortOrder { get; set; }

    public int PinnedSortOrder { get; set; } = -1;

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public enum QuickCaptureItemType
{
    Text,
    Link,
    Image,
    Todo
}

public enum QuickCaptureAppearancePreset
{
    Default,
    Paper,
    StickyYellow,
    Rose,
    Mint,
    MistBlue
}

public enum QuickCaptureSourceKind
{
    Manual,
    Clipboard,
    Image,
    DragDrop
}
