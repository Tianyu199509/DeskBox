using System.Text.Json.Serialization;

namespace DeskBox.Models;

public static class DesktopOrganizationCategoryIds
{
    public const string Shortcuts = "Shortcuts";
    public const string Documents = "Documents";
    public const string Design = "Design";
    public const string Images = "Images";
    public const string Media = "Media";
    public const string Packages = "Packages";
    public const string Other = "Other";

    public static readonly IReadOnlyList<string> DefaultOrder =
    [
        Shortcuts,
        Documents,
        Design,
        Images,
        Media,
        Packages,
        Other
    ];
}

public static class DesktopOrganizationSubtypeIds
{
    public const string Pdf = "Pdf";
    public const string Word = "Word";
    public const string Excel = "Excel";
    public const string PowerPoint = "PowerPoint";
    public const string Text = "Text";
    public const string Audio = "Audio";
    public const string Video = "Video";
}

/// <summary>
/// A stable routing rule. The target is a widget identity, never a user-facing
/// widget name. Joining a group or renaming the widget does not affect routing.
/// </summary>
public sealed class DesktopOrganizationRule
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string TargetWidgetId { get; set; } = string.Empty;

    public bool IsEnabled { get; set; } = true;

    public List<string> CategoryIds { get; set; } = [];

    public List<string> SubtypeIds { get; set; } = [];

    public List<string> Extensions { get; set; } = [];

    public List<string> ExcludedExtensions { get; set; } = [];
}

public enum DesktopOrganizationSourceScope
{
    Personal,
    Public
}

public enum DesktopOrganizationExclusionReason
{
    None,
    Folder,
    HiddenOrSystem,
    ReparsePoint,
    OfflinePlaceholder,
    TemporaryOrDownloading,
    PublicDesktopItem,
    Unavailable,
    SlowItem,
    BatchLimit,
    UserChoice,
    SourceNotSelected
}

public sealed record DesktopOrganizationFileSnapshot(
    string SourcePath,
    string Name,
    string Extension,
    long Size,
    DateTime LastWriteTimeUtc,
    string CategoryId,
    string? SubtypeId,
    DesktopOrganizationExclusionReason ExclusionReason,
    bool IsDirectory = false,
    DesktopOrganizationSourceScope SourceScope = DesktopOrganizationSourceScope.Personal)
{
    public bool IsEligible => ExclusionReason == DesktopOrganizationExclusionReason.None;

    public bool CanOptIn => ExclusionReason is
        DesktopOrganizationExclusionReason.Folder or
        DesktopOrganizationExclusionReason.SlowItem or
        DesktopOrganizationExclusionReason.BatchLimit;
}

public sealed class DesktopOrganizationScanResult
{
    public string DesktopPath { get; init; } = string.Empty;

    public string PublicDesktopPath { get; init; } = string.Empty;

    public bool PublicDesktopUnavailable { get; init; }

    public List<DesktopOrganizationFileSnapshot> Items { get; init; } = [];

    public int TotalCount => Items.Count;

    public int EligibleCount => Items.Count(item => item.IsEligible);

    public long EligibleSize => Items.Where(item => item.IsEligible).Sum(item => item.Size);

    public IReadOnlyDictionary<DesktopOrganizationExclusionReason, int> ExcludedCounts =>
        Items
            .Where(item => !item.IsEligible)
            .GroupBy(item => item.ExclusionReason)
            .ToDictionary(group => group.Key, group => group.Count());
}

public sealed class DesktopOrganizationTargetPlan
{
    /// <summary>
    /// Stable identity for the preview card. It is intentionally independent
    /// from the localized display name so selections survive a re-render.
    /// </summary>
    public string SourceBucketId { get; init; } = string.Empty;

    public string CategoryId { get; init; } = DesktopOrganizationCategoryIds.Other;

    public string TargetWidgetId { get; init; } = string.Empty;

    public string SuggestedDisplayName { get; init; } = string.Empty;

    public string TargetDirectoryPath { get; init; } = string.Empty;

    public bool CreatesWidget { get; init; }

    public List<DesktopOrganizationFileSnapshot> Items { get; init; } = [];

    public DesktopOrganizationRect? PlannedBounds { get; set; }

    public DesktopOrganizationTargetPlan CloneWith(
        string targetWidgetId,
        string displayName,
        string targetDirectoryPath,
        bool createsWidget,
        IEnumerable<DesktopOrganizationFileSnapshot> items) => new()
        {
            SourceBucketId = SourceBucketId,
            CategoryId = CategoryId,
            TargetWidgetId = targetWidgetId,
            SuggestedDisplayName = displayName,
            TargetDirectoryPath = targetDirectoryPath,
            CreatesWidget = createsWidget,
            Items = items.ToList(),
            PlannedBounds = PlannedBounds
        };
}

public enum DesktopOrganizationDestinationMode
{
    Dynamic,
    ExistingWidget
}

public sealed class DesktopOrganizationTargetSelection
{
    public string SourceBucketId { get; init; } = string.Empty;

    public bool IsSelected { get; set; } = true;

    public DesktopOrganizationDestinationMode DestinationMode { get; set; } =
        DesktopOrganizationDestinationMode.Dynamic;

    public string? ExistingWidgetId { get; set; }
}

public sealed record DesktopOrganizationDestinationOption(
    string Id,
    string DisplayName,
    string DirectoryPath,
    bool IsDynamic);

public sealed class DesktopOrganizationPlan
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");

    public string DesktopPath { get; init; } = string.Empty;

    public string PublicDesktopPath { get; init; } = string.Empty;

    public bool PublicDesktopUnavailable { get; init; }

    public bool IncludePersonalDesktop { get; init; } = true;

    public bool IncludePublicDesktop { get; init; }

    public List<DesktopOrganizationFileSnapshot> SourceItems { get; init; } = [];

    public string StorageRootPath { get; init; } = string.Empty;

    public List<DesktopOrganizationTargetPlan> Targets { get; init; } = [];

    public List<DesktopOrganizationFileSnapshot> ExcludedItems { get; init; } = [];

    public int EligibleItemCount => Targets.Sum(target => target.Items.Count);

    public int NewWidgetCount => Targets.Count(target => target.CreatesWidget);
}

public readonly record struct DesktopOrganizationRect(
    double X,
    double Y,
    double Width,
    double Height)
{
    public double Right => X + Width;

    public double Bottom => Y + Height;

    public bool Intersects(DesktopOrganizationRect other) =>
        X < other.Right &&
        Right > other.X &&
        Y < other.Bottom &&
        Bottom > other.Y;
}

public sealed class DesktopOrganizationRecoveryJournal
{
    public bool IsUndo { get; set; }

    public string TransactionId { get; set; } = string.Empty;

    /// <summary>
    /// Durable terminal marker written before an abandon touches settings.
    /// A crash between marking and clearing must never let startup recovery
    /// execute a transaction the user explicitly abandoned, so recovery
    /// refuses to act on a journal carrying this flag.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool IsAbandoned { get; set; }

    public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.UtcNow;

    public List<DesktopOrganizationRecoveryItem> Items { get; set; } = [];

    public List<string> CreatedWidgetIds { get; set; } = [];
}

/// <summary>
/// Durable object identity of the destination object recorded when a
/// physical move completed. Recovery may only move an item back while the
/// object currently at the destination path still carries exactly this
/// identity; a missing identity means no automatic restore authority.
/// </summary>
public sealed class DesktopOrganizationDestinationIdentity
{
    public ulong VolumeSerialNumber { get; set; }

    public ulong FileIdHigh { get; set; }

    public ulong FileIdLow { get; set; }
}

public sealed class DesktopOrganizationRecoveryItem
{
    public string? RestorePath { get; set; }

    public DesktopOrganizationSourceScope SourceScope { get; set; }

    public long? Size { get; set; }

    public DateTime? LastWriteTimeUtc { get; set; }

    public string SourcePath { get; set; } = string.Empty;

    public string DestinationPath { get; set; } = string.Empty;

    public string TargetWidgetId { get; set; } = string.Empty;

    public bool Completed { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool ShortcutRebaseInProgress { get; set; }

    public DesktopOrganizationDestinationIdentity? DestinationIdentity { get; set; }
}

public sealed class DesktopOrganizationExecutionResult
{
    public OrganizationHistoryEntry History { get; init; } = new();

    /// <summary>
    /// This run's completed receipts, captured before the persisted history
    /// entry may be compacted to a summary by the retention policy. The
    /// result page renders these; <see cref="History"/> remains the durable
    /// record (possibly a receipt-less summary for oversized batches).
    /// </summary>
    public List<OrganizationHistoryItem> CompletedItems { get; init; } = [];

    public List<WidgetConfig> CreatedWidgets { get; init; } = [];

    public List<DesktopOrganizationRetainedItem> RetainedItems { get; init; } = [];
}

public enum DesktopOrganizationRetentionReason
{
    SourceChanged,
    InUse,
    AccessDenied,
    Unavailable,
    TransferFailed,
    Canceled
}

public sealed record DesktopOrganizationRetainedItem(
    string SourcePath,
    string Name,
    DesktopOrganizationRetentionReason Reason,
    string Detail,
    DesktopOrganizationSourceScope SourceScope = DesktopOrganizationSourceScope.Personal);

public sealed record DesktopOrganizationProgress(
    int CompletedCount,
    int TotalCount,
    string TargetWidgetId,
    string TargetDisplayName,
    DesktopOrganizationSourceScope SourceScope = DesktopOrganizationSourceScope.Personal);
