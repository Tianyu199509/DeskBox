namespace DeskBox.Contracts;

/// <summary>
/// Canonical file-stack option values, thresholds, caps and normalizers,
/// owned here so the file-stack section editor can build its option tables
/// and normalize its binding surface without referencing the settings
/// adapter (batch 45 sink; <see cref="Services.SettingsService"/> keeps its
/// historical constants and normalizers as aliases of these).
/// </summary>
public static class FileStackOptionKinds
{
    public const string GroupByKind = "Kind";
    public const string GroupByDateAdded = "DateAdded";
    public const string GroupByDateCreated = "DateCreated";
    public const string GroupByDateModified = "DateModified";
    public const string GroupByCustom = "Custom";

    public const string OrderByWidget = "Widget";
    public const string OrderByName = "Name";
    public const string OrderByDateAdded = "DateAdded";
    public const string OrderByDateModified = "DateModified";

    public const string OpenModeInline = "Inline";
    public const string OpenModePopover = "Popover";

    public const string PopoverLayoutAdaptive = "Adaptive";
    public const string PopoverLayoutGrid3 = "Grid3";
    public const string PopoverLayoutGrid5 = "Grid5";

    public const string PopoverStyleFollowMaterial = "FollowMaterial";
    public const string PopoverStyleNeutral = "Neutral";

    public const string UnmatchedKeepLoose = "KeepLoose";
    public const string UnmatchedOther = "Other";

    public const int DefaultThreshold = 3;

    /// <summary>The auto-stack threshold options offered by the page.</summary>
    public static int[] Thresholds { get; } = [2, 3, 5];

    public const int MaxCustomRules = 32;
    public const int MaxExtensionsPerRule = 64;

    public static string NormalizeGroupBy(string? groupBy)
    {
        if (string.Equals(groupBy, GroupByDateAdded, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(groupBy, GroupByDateCreated, StringComparison.OrdinalIgnoreCase))
        {
            return GroupByDateAdded;
        }

        if (string.Equals(groupBy, GroupByDateModified, StringComparison.OrdinalIgnoreCase))
        {
            return GroupByDateModified;
        }

        return string.Equals(groupBy, GroupByCustom, StringComparison.OrdinalIgnoreCase)
            ? GroupByCustom
            : GroupByKind;
    }

    public static int NormalizeThreshold(int threshold) => threshold switch
    {
        2 or 3 or 5 => threshold,
        _ => DefaultThreshold
    };

    public static string NormalizeOrderBy(string? orderBy)
    {
        if (string.Equals(orderBy, OrderByName, StringComparison.OrdinalIgnoreCase))
        {
            return OrderByName;
        }

        if (string.Equals(orderBy, OrderByDateAdded, StringComparison.OrdinalIgnoreCase))
        {
            return OrderByDateAdded;
        }

        return string.Equals(orderBy, OrderByDateModified, StringComparison.OrdinalIgnoreCase)
            ? OrderByDateModified
            : OrderByWidget;
    }

    public static string NormalizeOpenMode(string? openMode) =>
        string.Equals(openMode, OpenModeInline, StringComparison.OrdinalIgnoreCase)
            ? OpenModeInline
            : OpenModePopover;

    public static string NormalizePopoverLayout(string? layout) =>
        layout switch
        {
            PopoverLayoutGrid3 => PopoverLayoutGrid3,
            PopoverLayoutGrid5 => PopoverLayoutGrid5,
            _ => PopoverLayoutAdaptive
        };

    public static string NormalizePopoverStyle(string? style) =>
        string.Equals(style, PopoverStyleFollowMaterial, StringComparison.OrdinalIgnoreCase)
            ? PopoverStyleFollowMaterial
            : PopoverStyleNeutral;

    public static string NormalizeUnmatchedBehavior(string? behavior) =>
        string.Equals(behavior, UnmatchedOther, StringComparison.OrdinalIgnoreCase)
            ? UnmatchedOther
            : UnmatchedKeepLoose;

    /// <summary>
    /// Normalizes a raw extension list the way the settings adapter always
    /// has: trims, maps <c>*.ext</c>/<c>*ext</c> to <c>.ext</c>, lowercases,
    /// dedupes (case-insensitive) and drops empties.
    /// </summary>
    public static IReadOnlyList<string> NormalizeExtensions(
        IEnumerable<string>? extensions)
    {
        if (extensions is null)
        {
            return [];
        }

        var normalized = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string? value in extensions)
        {
            string extension = (value ?? string.Empty).Trim();
            if (extension.StartsWith("*.", StringComparison.Ordinal))
            {
                extension = extension[1..];
            }
            else if (extension.StartsWith('*'))
            {
                extension = extension[1..];
            }

            if (extension.Length == 0)
            {
                continue;
            }

            if (!extension.StartsWith('.'))
            {
                extension = $".{extension}";
            }

            extension = extension.ToLowerInvariant();
            if (extension.Length > 24 ||
                extension.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
                extension.Contains(Path.DirectorySeparatorChar) ||
                extension.Contains(Path.AltDirectorySeparatorChar) ||
                !seen.Add(extension))
            {
                continue;
            }

            normalized.Add(extension);
        }

        return normalized;
    }
}

/// <summary>
/// Canonical global folder-open behavior values for the file-widget section,
/// owned here so the feature-widgets editor can normalize its binding
/// surface without referencing the settings adapter.
/// <see cref="Services.FileWidgetFolderOpenBehaviorNames"/> keeps its
/// historical constants as aliases of these.
/// </summary>
public static class FileWidgetFolderOpenBehaviors
{
    public const string Explorer = "Explorer";
    public const string Embedded = "Embedded";
    public const string FollowGlobal = "FollowGlobal";

    public static string NormalizeGlobal(string? value) =>
        string.Equals(value, Embedded, StringComparison.Ordinal)
            ? Embedded
            : Explorer;
}

/// <summary>
/// One entry of the file-stack rule preview: a widget file (or a folder
/// discovered under a mapped folder) with the extension an empty-extension
/// directory test left behind. Built by the settings shell (which owns the
/// widget config and the disk scan) and pushed onto the file-stack editor.
/// </summary>
public readonly record struct FileStackPreviewEntry(
    string WidgetName,
    string Path,
    string Extension);
