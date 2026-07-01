namespace DeskBox.Models;

public sealed class TodoItem
{
    public const string RedColorMarker = "red";
    public const string OrangeColorMarker = "orange";
    public const string YellowColorMarker = "yellow";
    public const string GreenColorMarker = "green";
    public const string BlueColorMarker = "blue";
    public const string PurpleColorMarker = "purple";

    public static readonly string[] SupportedColorMarkers =
    [
        RedColorMarker,
        OrangeColorMarker,
        YellowColorMarker,
        GreenColorMarker,
        BlueColorMarker,
        PurpleColorMarker
    ];

    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string Text { get; set; } = string.Empty;

    public bool IsCompleted { get; set; }

    public bool IsImportant { get; set; }

    public string? ColorMarker { get; set; }

    public DateTimeOffset? DueDate { get; set; }

    public int SortOrder { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

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
            _ => "Todo.Color.None"
        };
    }
}
