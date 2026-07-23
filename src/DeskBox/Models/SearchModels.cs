namespace DeskBox.Models;

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml.Media;

/// <summary>
/// Categorizes the type of a search result.
/// </summary>
public enum SearchResultKind
{
    File,
    Todo,
    QuickCapture,
    Action,
    Folder,
    History,
    Favorite
}

/// <summary>
/// Semantic file category derived from the extension, used to build the dynamic
/// extension-semantic tabs (e.g. .exe/.lnk results surface an "Apps" tab).
/// </summary>
public enum FileCategory
{
    App,
    Document,
    Image,
    Video,
    Music,
    Archive,
    Other
}

/// <summary>
/// Sortable columns for the file-style result list.
/// </summary>
public enum ResultSortColumn
{
    Relevance,
    Name,
    Size,
    Date,
    Type
}

/// <summary>
/// Secondary result filter used by the All tab.
/// </summary>
public enum SearchResultFilter
{
    All,
    FilesAndFolders,
    Apps,
    Images,
    Documents,
    DeskBox
}

/// <summary>
/// Maps a file name to its semantic category based on extension.
/// </summary>
public static class FileCategoryHelper
{
    private static readonly HashSet<string> s_appExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".lnk", ".msi", ".appx", ".msix", ".bat", ".cmd", ".com", ".scr", ".ps1", ".url"
    };

    private static readonly HashSet<string> s_documentExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".doc", ".docx", ".pdf", ".txt", ".md", ".xls", ".xlsx", ".ppt", ".pptx",
        ".csv", ".rtf", ".odt", ".json", ".xml", ".html", ".htm", ".ini", ".log"
    };

    private static readonly HashSet<string> s_imageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".svg", ".webp", ".ico", ".tif", ".tiff", ".heic", ".raw"
    };

    private static readonly HashSet<string> s_videoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".avi", ".mkv", ".mov", ".wmv", ".flv", ".webm", ".m4v", ".mpg", ".mpeg", ".3gp"
    };

    private static readonly HashSet<string> s_musicExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp3", ".wav", ".flac", ".aac", ".ogg", ".wma", ".m4a", ".opus", ".mid", ".midi"
    };

    private static readonly HashSet<string> s_archiveExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".zip", ".rar", ".7z", ".tar", ".gz", ".bz2", ".xz", ".iso", ".cab"
    };

    public static FileCategory Categorize(string fileName)
    {
        string extension = Path.GetExtension(fileName);
        if (string.IsNullOrEmpty(extension))
        {
            return FileCategory.Other;
        }

        if (s_appExtensions.Contains(extension)) return FileCategory.App;
        if (s_documentExtensions.Contains(extension)) return FileCategory.Document;
        if (s_imageExtensions.Contains(extension)) return FileCategory.Image;
        if (s_videoExtensions.Contains(extension)) return FileCategory.Video;
        if (s_musicExtensions.Contains(extension)) return FileCategory.Music;
        if (s_archiveExtensions.Contains(extension)) return FileCategory.Archive;
        return FileCategory.Other;
    }
}

/// <summary>
/// Represents a single search result from any search layer.
/// </summary>
public sealed class SearchResultItem
{
    public required SearchResultKind Kind { get; init; }
    public required string Title { get; init; }
    public string? Subtitle { get; init; }
    public string? DetailPath { get; init; }
    public string? Glyph { get; init; }
    public DateTimeOffset? ModifiedAt { get; set; }
    public double RelevanceScore { get; set; }

    /// <summary>File size in bytes (files only). Populated lazily by FileMetaService.</summary>
    public long? FileSize { get; set; }

    /// <summary>Creation time. Populated lazily by FileMetaService.</summary>
    public DateTimeOffset? CreatedAt { get; set; }

    /// <summary>Real shell icon for file results. Populated lazily by FileMetaService.</summary>
    public ImageSource? Icon { get; set; }

    /// <summary>Whether <see cref="Icon"/> has been resolved (to distinguish pending vs. none).</summary>
    public bool IconResolved { get; set; }

    /// <summary>Application name without the shortcut/executable extension.</summary>
    public string AppDisplayName => Path.GetFileNameWithoutExtension(Title);

    /// <summary>Human-readable file size for display.</summary>
    public string? SizeDisplay { get; set; }

    /// <summary>Formatted creation date for display.</summary>
    public string? DateDisplay { get; set; }

    /// <summary>Localized type label for display and sort (e.g., "App", "File", "Folder").</summary>
    public string? TypeDisplay { get; set; }

    /// <summary>
    /// For Todo results: the widget ID and item ID for direct actions.
    /// </summary>
    public string? TodoWidgetId { get; init; }
    public string? TodoItemId { get; init; }
    public bool TodoIsCompleted { get; init; }

    /// <summary>
    /// For QuickCapture results: the item ID.
    /// </summary>
    public string? QuickCaptureItemId { get; init; }

    /// <summary>
    /// For Action results: the action identifier.
    /// </summary>
    public string? ActionId { get; init; }

    /// <summary>
    /// For History/Favorite results: the query text to re-run when activated.
    /// </summary>
    public string? HistoryQuery { get; init; }
}

/// <summary>
/// Represents a grouped set of search results.
/// </summary>
public sealed class SearchResultGroup
{
    public required SearchResultKind Kind { get; init; }
    public required string DisplayName { get; init; }
    public required IReadOnlyList<SearchResultItem> Items { get; init; }
    public int TotalCount { get; init; }
}

/// <summary>
/// Aggregated search response from all layers.
/// </summary>
public sealed class SearchResponse
{
    public required string Query { get; init; }
    public IReadOnlyList<SearchResultItem> RankedItems { get; init; } = [];
    public required IReadOnlyList<SearchResultGroup> Groups { get; init; }
    public int TotalResultCount { get; init; }
    public TimeSpan Elapsed { get; init; }
    public bool IsComplete { get; init; }
}

/// <summary>
/// Represents a recommendation item shown when no query is entered.
/// </summary>
public sealed class SearchRecommendationItem
{
    public required SearchResultKind Kind { get; init; }
    public required string Title { get; init; }
    public string? Subtitle { get; init; }
    public string? Glyph { get; init; }
    public string? DetailPath { get; init; }
    public string? ActionId { get; init; }
    public string? TodoWidgetId { get; init; }
    public string? TodoItemId { get; init; }
    public string? QuickCaptureItemId { get; init; }

    /// <summary>
    /// For History/Favorite recommendations: the query text to re-run when activated.
    /// </summary>
    public string? HistoryQuery { get; init; }
}

/// <summary>
/// A dynamic tab in the search popup. Tabs are generated from the current result set:
/// extension-semantic tabs (Apps/Documents/...) when a query is active, Kind-semantic
/// tabs (Todo/Note/File/Folder) plus recent-content tabs in the empty state.
/// </summary>
public sealed class SearchTabItem : INotifyPropertyChanged
{
    public required string Id { get; init; }
    public required string DisplayName { get; init; }
    public string? Glyph { get; init; }

    /// <summary>Predicate that decides whether a result belongs to this tab.</summary>
    public required Func<SearchResultItem, bool> Predicate { get; init; }

    /// <summary>Whether this tab shows the sortable file columns (size/date).</summary>
    public bool SupportsFileSort { get; init; }

    private int _count;
    /// <summary>Number of results currently in this tab (shown as a badge).</summary>
    public int Count
    {
        get => _count;
        set { if (_count != value) { _count = value; OnPropertyChanged(); } }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
