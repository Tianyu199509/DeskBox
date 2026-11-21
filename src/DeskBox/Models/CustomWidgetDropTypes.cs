using System.Globalization;

namespace DeskBox.Services;

public sealed class CustomWidgetDropFileContext
{
    public static readonly CustomWidgetDropFileContext Empty = new();

    public string Name { get; init; } = string.Empty;
    public string Path { get; init; } = string.Empty;
    public string Extension { get; init; } = string.Empty;
    public long SizeBytes { get; init; }
    public string SizeText { get; init; } = string.Empty;
    public string ModifiedIso { get; init; } = string.Empty;
    public string Mode { get; init; } = "link";
    public string Text { get; init; } = string.Empty;

    public string Resolve(string field) => field switch
    {
        "name" or "fileName" => Name,
        "path" or "filePath" => Path,
        "ext" or "extension" => Extension,
        "sizeBytes" or "size" => SizeBytes.ToString(CultureInfo.InvariantCulture),
        "sizeText" => SizeText,
        "modifiedIso" or "modifiedAt" => ModifiedIso,
        "mode" or "storageMode" => Mode,
        "text" or "content" => Text,
        _ => string.Empty
    };
}

public sealed record CustomWidgetDropItemRecord(
    string Id,
    string Title,
    string Text,
    string? Body = null,
    string? Url = null,
    bool IsCompleted = false,
    string Source = "");

public sealed record CustomWidgetStructuredDropPayload(
    string Kind,
    string Text = "",
    IReadOnlyList<CustomWidgetDropItemRecord>? Items = null,
    IReadOnlyList<string>? FilePaths = null,
    bool ForceCopyFiles = false);

public static class CustomWidgetDropAcceptHelper
{
    public static void ParseAcceptModes(
        string? rawAccept,
        out bool allowsFiles,
        out bool allowsText,
        out bool allowsItems,
        out HashSet<string>? allowedFileExtensions)
    {
        allowedFileExtensions = null;
        if (string.IsNullOrWhiteSpace(rawAccept))
        {
            allowsFiles = true;
            allowsText = false;
            allowsItems = false;
            return;
        }

        allowsFiles = false;
        allowsText = false;
        allowsItems = false;
        bool sawToken = false;

        string[] parts = rawAccept.Split([',', ';', '|'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (string part in parts)
        {
            if (part.Length == 0)
            {
                continue;
            }

            sawToken = true;
            if (string.Equals(part, "*", StringComparison.Ordinal) ||
                string.Equals(part, "*/*", StringComparison.Ordinal) ||
                string.Equals(part, "any", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(part, "all", StringComparison.OrdinalIgnoreCase))
            {
                allowsFiles = true;
                allowsText = true;
                allowsItems = true;
                allowedFileExtensions = null;
                return;
            }

            if (string.Equals(part, "files", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(part, "file", StringComparison.OrdinalIgnoreCase))
            {
                allowsFiles = true;
            }
            else if (string.Equals(part, "text", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(part, "url", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(part, "links", StringComparison.OrdinalIgnoreCase))
            {
                allowsText = true;
            }
            else if (string.Equals(part, "items", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(part, "todo", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(part, "todos", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(part, "notes", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(part, "quickcapture", StringComparison.OrdinalIgnoreCase))
            {
                allowsItems = true;
            }
            else if (part.StartsWith('.'))
            {
                allowsFiles = true;
                allowedFileExtensions ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                allowedFileExtensions.Add(part);
            }
            else if (part.Contains('/'))
            {
                allowsFiles = true;
            }
        }

        if (!sawToken || (!allowsFiles && !allowsText && !allowsItems))
        {
            allowsFiles = true;
        }
    }

    public static bool AllowsFiles(string? rawAccept)
    {
        ParseAcceptModes(rawAccept, out bool allowsFiles, out _, out _, out _);
        return allowsFiles;
    }

    public static bool MatchesFileExtensionFilter(string? filePath, HashSet<string>? allowedExtensions)
    {
        if (allowedExtensions is null || allowedExtensions.Count == 0)
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(filePath))
        {
            return false;
        }

        string ext = Path.GetExtension(filePath);
        return !string.IsNullOrEmpty(ext) && allowedExtensions.Contains(ext);
    }
}
