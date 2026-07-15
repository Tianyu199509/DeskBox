namespace DeskBox.Models;

public sealed class QuickCaptureStoreData
{
    public int Version { get; set; } = 3;

    public QuickCaptureViewMode CurrentView { get; set; } = QuickCaptureViewMode.Records;

    public List<QuickCaptureItem> Items { get; set; } = [];

    public List<QuickCaptureItem> RecentItems { get; set; } = [];
}

public enum QuickCaptureViewMode
{
    Records,
    Pinned,
    Recent
}
