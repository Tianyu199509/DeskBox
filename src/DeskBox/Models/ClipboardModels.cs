using System.Text.Json.Serialization;

namespace DeskBox.Models;

/// <summary>
/// Primary payload family of a captured clipboard history entry.
/// </summary>
public enum ClipboardItemKind
{
    Text,
    Image,
    Files
}

/// <summary>
/// Semantic sub-classification for text clipboard items.
/// </summary>
public enum ClipboardTextSubKind
{
    Plain,
    Url,
    Color,
    Email,
    Path,
    Html,
    Rtf,
    Code
}

/// <summary>
/// Durable metadata record for a single captured clipboard entry.
/// Heavy image payloads and pre-decoded thumbnails are stored by content hash
/// on disk outside the JSON document.
/// </summary>
public sealed class ClipboardHistoryItem
{
    public const int InlineTextMaxChars = 2048;

    private int _charCount;
    private int _lineCount;

    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public ClipboardItemKind Kind { get; set; } = ClipboardItemKind.Text;

    public ClipboardTextSubKind SubKind { get; set; } = ClipboardTextSubKind.Plain;

    public string ContentHash { get; set; } = string.Empty;

    /// <summary>
    /// Truncated single/multi-line preview summary (up to 256 Unicode scalar chars)
    /// used by virtualized list cards so multi-megabyte text entries never stall UI layout.
    /// </summary>
    public string Summary { get; set; } = string.Empty;

    /// <summary>
    /// Full plain-text representation (or newline-joined paths for Files).
    /// </summary>
    public string? TextContent { get; set; }

    /// <summary>
    /// Optional HTML clipboard payload when captured from browsers/rich editors.
    /// </summary>
    public string? HtmlContent { get; set; }

    /// <summary>
    /// Optional RTF clipboard payload when captured from rich documents.
    /// </summary>
    public string? RtfContent { get; set; }

    /// <summary>
    /// Content-addressed UTF-8 text payload file name under clipboard/payloads/ when <see cref="CharCount"/> exceeds <see cref="InlineTextMaxChars"/>.
    /// </summary>
    public string? TextPayloadFileName { get; set; }

    /// <summary>
    /// Content-addressed UTF-8 HTML payload file name under clipboard/payloads/.
    /// </summary>
    public string? HtmlPayloadFileName { get; set; }

    /// <summary>
    /// Content-addressed UTF-8 RTF payload file name under clipboard/payloads/.
    /// </summary>
    public string? RtfPayloadFileName { get; set; }

    /// <summary>
    /// Cached full character count for text payloads (preserved even when <see cref="TextContent"/> is truncated to <see cref="InlineTextMaxChars"/>).
    /// </summary>
    public int CharCount
    {
        get => _charCount > 0 ? _charCount : (TextContent?.Length ?? 0);
        set => _charCount = Math.Max(0, value);
    }

    /// <summary>
    /// Cached full line count for text payloads (preserved even when <see cref="TextContent"/> is truncated to <see cref="InlineTextMaxChars"/>).
    /// </summary>
    public int LineCount
    {
        get => _lineCount > 0 ? _lineCount : CountLines(TextContent);
        set => _lineCount = Math.Max(0, value);
    }

    /// <summary>
    /// Content-addressed PNG file name under clipboard/images/.
    /// </summary>
    public string? ImageFileName { get; set; }

    /// <summary>
    /// Content-addressed 180px thumbnail PNG file name under clipboard/thumbnails/.
    /// </summary>
    public string? ThumbnailFileName { get; set; }

    public int ImageWidth { get; set; }

    public int ImageHeight { get; set; }

    public long ByteSize { get; set; }

    public List<string> FilePaths { get; set; } = [];

    /// <summary>
    /// Normalized #AARRGGBB or #RRGGBB hex string when <see cref="SubKind"/> is <see cref="ClipboardTextSubKind.Color"/>.
    /// </summary>
    public string? ColorHex { get; set; }

    /// <summary>
    /// User-defined remark / search alias attached to this clipboard entry.
    /// </summary>
    public string? Note { get; set; }

    /// <summary>
    /// Optional resolved page or anchor title when <see cref="SubKind"/> is <see cref="ClipboardTextSubKind.Url"/>.
    /// </summary>
    public string? UrlTitle { get; set; }

    /// <summary>
    /// Optional detected language hint (e.g. JSON, SQL, Shell) when <see cref="SubKind"/> is <see cref="ClipboardTextSubKind.Code"/>.
    /// </summary>
    public string? CodeLanguage { get; set; }

    /// <summary>
    /// Cached text extracted offline via <c>Windows.Media.Ocr</c> for image entries so
    /// screenshots become searchable and one-click copyable without re-running OCR.
    /// </summary>
    public string? OcrText { get; set; }

    public bool IsSensitive { get; set; }

    public bool IsPinned { get; set; }

    public int UseCount { get; set; } = 1;

    public string? SourceAppName { get; set; }

    public string? SourceAppPath { get; set; }

    public string? SourceAppIconFileName { get; set; }

    public long CreatedAtUtcTicks { get; set; }

    public long UpdatedAtUtcTicks { get; set; }

    [JsonIgnore]
    public ClipboardTextSubKind TextSubKind
    {
        get => SubKind;
        set => SubKind = value;
    }

    [JsonIgnore]
    public string? PlainText
    {
        get => TextContent;
        set => TextContent = value;
    }

    [JsonIgnore]
    public string SummaryText
    {
        get => Summary;
        set => Summary = value;
    }

    [JsonIgnore]
    public string? ImageRelativePath =>
        string.IsNullOrWhiteSpace(ImageFileName) ? null : $"images/{ImageFileName}";

    [JsonIgnore]
    public string? ThumbnailRelativePath =>
        string.IsNullOrWhiteSpace(ThumbnailFileName) ? null : $"thumbnails/{ThumbnailFileName}";

    [JsonIgnore]
    public DateTime CreatedAtUtc =>
        CreatedAtUtcTicks > 0
            ? new DateTime(CreatedAtUtcTicks, DateTimeKind.Utc)
            : DateTime.UtcNow;

    [JsonIgnore]
    public DateTime LastUsedAtUtc =>
        UpdatedAtUtcTicks > 0
            ? new DateTime(UpdatedAtUtcTicks, DateTimeKind.Utc)
            : CreatedAtUtcTicks > 0
                ? new DateTime(CreatedAtUtcTicks, DateTimeKind.Utc)
                : DateTime.UtcNow;

    [JsonIgnore]
    public bool HasExternalTextPayload => !string.IsNullOrWhiteSpace(TextPayloadFileName);

    [JsonIgnore]
    public bool HasExternalRichPayload =>
        !string.IsNullOrWhiteSpace(HtmlPayloadFileName) ||
        !string.IsNullOrWhiteSpace(RtfPayloadFileName);

    [JsonIgnore]
    public int CharacterCount => CharCount;

    internal static int CountLines(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return 0;
        }

        int lines = 1;
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] == '\n')
            {
                lines++;
            }
        }

        return lines;
    }
}

/// <summary>
/// Recently observed source application for one-click exclusion in Clipboard settings.
/// </summary>
public sealed class ClipboardObservedApp
{
    public string AppPath { get; set; } = string.Empty;

    public string AppName { get; set; } = string.Empty;

    public string? IconFileName { get; set; }

    public long LastSeenUtcTicks { get; set; }
}

/// <summary>
/// Root persisted JSON document at %DataDirectory%/clipboard/clipboard.json.
/// </summary>
public sealed class ClipboardStoreDocument
{
    public int Version { get; set; } = 1;

    public List<ClipboardHistoryItem> Items { get; set; } = [];

    public List<ClipboardObservedApp> ObservedApps { get; set; } = [];
}

/// <summary>
/// Cross-process IPC summary of Clipboard storage and recently observed apps.
/// </summary>
public sealed record ClipboardStorageStats(
    int ActiveItemCount,
    int PinnedItemCount,
    int ImageFileCount,
    long TotalCacheBytes,
    List<ClipboardObservedApp> ObservedApps);
