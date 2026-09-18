namespace DeskBox.Models;

public sealed class TodoItem
{
    public const string RedColorMarker = "red";
    public const string OrangeColorMarker = "orange";
    public const string YellowColorMarker = "yellow";
    public const string GreenColorMarker = "green";
    public const string BlueColorMarker = "blue";
    public const string PurpleColorMarker = "purple";
    public const string TealColorMarker = "teal";
    public const string PinkColorMarker = "pink";

    public static readonly string[] SupportedColorMarkers =
    [
        RedColorMarker,
        OrangeColorMarker,
        YellowColorMarker,
        GreenColorMarker,
        BlueColorMarker,
        PurpleColorMarker,
        TealColorMarker,
        PinkColorMarker
    ];

    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string Text { get; set; } = string.Empty;

    public bool IsCompleted { get; set; }

    public bool IsImportant { get; set; }

    public string? ColorMarker { get; set; }

    public DateTimeOffset? DueDate { get; set; }

    public TodoRecurrence? Recurrence { get; set; }

    public List<TodoStep> Steps { get; set; } = [];

    public string? Notes { get; set; }

    public List<TodoAttachment> Attachments { get; set; } = [];

    public DateTimeOffset? CompletedAt { get; set; }

    public DateTimeOffset? ReminderLastNotifiedAt { get; set; }

    public DateTimeOffset? ReminderDismissedForDueDate { get; set; }

    public int? ReminderOffsetMinutes { get; set; }

    public DateTimeOffset? SnoozedUntil { get; set; }

    public DateTimeOffset? SnoozeLastNotifiedAt { get; set; }

    public string? RecurrenceSeriesId { get; set; }

    public string? GeneratedNextItemId { get; set; }

    public int SortOrder { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Sync-layer field: ID of the device that last wrote this record.
    /// Backfilled by the store's Normalize; used by the future sync projection.
    /// </summary>
    public string? DeviceId { get; set; }

    /// <summary>
    /// Sync-layer field: deletion tombstone slot. Soft-delete wiring arrives
    /// with the sync projection; for now records are still hard-deleted and
    /// this flag simply rides along.
    /// </summary>
    public bool IsDeleted { get; set; }

    public static string? NormalizeColorMarker(string? colorMarker)
    {
        if (string.IsNullOrWhiteSpace(colorMarker))
        {
            return null;
        }

        string normalized = colorMarker.Trim().ToLowerInvariant();
        return SupportedColorMarkers.Contains(normalized, StringComparer.Ordinal)
            ? normalized
            : null;
    }

    public static string GetColorMarkerHex(string? colorMarker)
    {
        return NormalizeColorMarker(colorMarker) switch
        {
            RedColorMarker => "#E34D4D",
            OrangeColorMarker => "#F08A3C",
            YellowColorMarker => "#F2C94C",
            GreenColorMarker => "#4CAF6D",
            BlueColorMarker => "#4D8FE3",
            PurpleColorMarker => "#9B6BE8",
            TealColorMarker => "#2DB7A3",
            PinkColorMarker => "#E66AA2",
            _ => "#8A8F98"
        };
    }

    public static string GetColorMarkerLocalizationKey(string? colorMarker)
    {
        return NormalizeColorMarker(colorMarker) switch
        {
            RedColorMarker => "Todo.Color.Red",
            OrangeColorMarker => "Todo.Color.Orange",
            YellowColorMarker => "Todo.Color.Yellow",
            GreenColorMarker => "Todo.Color.Green",
            BlueColorMarker => "Todo.Color.Blue",
            PurpleColorMarker => "Todo.Color.Purple",
            TealColorMarker => "Todo.Color.Teal",
            PinkColorMarker => "Todo.Color.Pink",
            _ => "Todo.Color.None"
        };
    }
}
