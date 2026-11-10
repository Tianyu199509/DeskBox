namespace DeskBox.Models;

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml;
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
    Favorite,
    Clipboard
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
/// Current availability of the external Everything filename provider. DeskBox content
/// and commands can still be returned when the file provider is unavailable.
/// </summary>
public enum EverythingConnectionState
{
    Unknown,
    Checking,
    NotConfirmed,
    NotInstalled,
    NotRunning,
    PermissionMismatch,
    IpcUnavailable,
    SdkUnavailable,
    Connected,
    Error
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
    public string DisplayGlyph => !string.IsNullOrWhiteSpace(Glyph)
        ? Glyph
        : Kind switch
        {
            SearchResultKind.Folder => "\uE8B7",
            SearchResultKind.Clipboard => "\uE77F",
            _ => "\uE8A5"
        };
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
    /// For Clipboard results: the clipboard history item ID.
    /// </summary>
    public string? ClipboardItemId { get; init; }

    /// <summary>
    /// For Action results: the action identifier.
    /// </summary>
    public string? ActionId { get; init; }

    /// <summary>
    /// For History/Favorite results: the query text to re-run when activated.
    /// </summary>
    public string? HistoryQuery { get; init; }

    /// <summary>
    /// Stable canonical identity key used by zero-input application recommendations
    /// for deduplication, manual ordering, folder grouping, and hiding.
    /// </summary>
    public string? RecommendationKey { get; set; }

    /// <summary>Optional multi-line preview text for notes, todos, clipboard items, calculator, or URLs.</summary>
    public string? PreviewText { get; init; }

    /// <summary>Optional local image path for thumbnail preview in the details pane (e.g., clipboard image).</summary>
    public string? PreviewImagePath { get; init; }

    /// <summary>Optional source application display name (e.g., clipboard capture source).</summary>
    public string? PreviewSourceApp { get; init; }

    /// <summary>Optional character count for text notes or clipboard payloads.</summary>
    public int? PreviewCharCount { get; init; }

    /// <summary>Optional line count for text notes or clipboard payloads.</summary>
    public int? PreviewLineCount { get; init; }

    /// <summary>Whether this action item is a low-priority fallback suggestion (such as fallback web search).</summary>
    public bool IsFallback { get; init; }
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
    public int MaterializedFileResultCount { get; init; }
    public int TotalFileResultCount { get; init; }
    public int NextFileResultOffset { get; init; }
    public bool HasMoreResults { get; init; }
    public TimeSpan Elapsed { get; init; }
    public bool IsComplete { get; init; }
    public EverythingConnectionState FileProviderState { get; init; }
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

    /// <summary>
    /// Pre-resolved deduplication identity carried from the engine to avoid re-reading
    /// shortcut metadata on the cold-open path.
    /// </summary>
    internal Helpers.AppRecommendationIdentity.Keys? ResolvedIdentityKeys { get; init; }
}

/// <summary>
/// Display model for one manageable query in the desktop search widget.
/// </summary>
public sealed class SearchHistoryEntry
{
    public required string Query { get; init; }
    public required string DeleteLabel { get; init; }
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

    /// <summary>
    /// Whether this tab is currently selected.  The popup binds the selection
    /// indicator inside the tab's label column to this state so the indicator
    /// follows the text instead of being positioned relative to the whole item.
    /// </summary>
    private bool _isSelected;
    public bool IsSelected
    {
        get => _isSelected;
        internal set
        {
            if (_isSelected == value)
            {
                return;
            }

            _isSelected = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(SelectionIndicatorVisibility));
        }
    }

    public Visibility SelectionIndicatorVisibility =>
        _isSelected ? Visibility.Visible : Visibility.Collapsed;

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

/// <summary>
/// Lightweight folder summary used when building recommendation context menus.
/// </summary>
public sealed record RecommendationFolderSummary(string FolderId, string Name);

/// <summary>
/// Immutable display model for one tile in the zero-input application recommendation grid:
/// either a standalone application card or a Windows 11-style 2x2 preview folder card.
/// </summary>
public sealed class RecommendedGridEntry
{
    private RecommendedGridEntry(
        string entryKey,
        bool isFolder,
        string label,
        SearchResultItem? appItem,
        string? folderId,
        IReadOnlyList<SearchResultItem> folderItems)
    {
        EntryKey = entryKey;
        IsFolder = isFolder;
        Label = label;
        AppItem = appItem;
        FolderId = folderId;
        FolderItems = folderItems;
    }

    public string EntryKey { get; }
    public bool IsFolder { get; }
    public string Label { get; }
    public SearchResultItem? AppItem { get; }
    public string? FolderId { get; }
    public IReadOnlyList<SearchResultItem> FolderItems { get; }

    public Visibility SingleIconVisibility => IsFolder ? Visibility.Collapsed : Visibility.Visible;
    public Visibility FolderPreviewVisibility => IsFolder ? Visibility.Visible : Visibility.Collapsed;

    public static RecommendedGridEntry CreateApp(string appKey, SearchResultItem item) =>
        new(
            entryKey: $"app:{appKey}",
            isFolder: false,
            label: item.AppDisplayName,
            appItem: item,
            folderId: null,
            folderItems: []);

    public static RecommendedGridEntry CreateFolder(
        string folderId,
        string folderName,
        IReadOnlyList<SearchResultItem> items) =>
        new(
            entryKey: $"folder:{folderId}",
            isFolder: true,
            label: folderName,
            appItem: null,
            folderId: folderId,
            folderItems: items);
}
