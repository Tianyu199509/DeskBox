using System.Text.Json.Serialization;

namespace DeskBox.Models;

public enum GlanceBackgroundSource
{
    Online,
    LocalFiles,
    LocalFolder,
    Bing
}

public enum GlanceOnlineImageProvider
{
    Wikimedia,
    Bing
}

public enum GlanceOnlineImageCategory
{
    Featured,
    Landscapes,
    Cities,
    Architecture,
    Animals,
    Plants,
    Astronomy,
    People
}

public enum GlanceLayoutMode
{
    Immersive,
    Centered,
    Editorial,
    Calendar
}

public enum GlanceDisplayElement
{
    Time,
    Date,
    Year,
    Weekday,
    Calendar
}

public enum GlanceTransitionMode
{
    None,
    CrossFade,
    SlideFade,
    ZoomFade
}

public enum GlanceTransitionSpeed
{
    Fast,
    Standard,
    Relaxed
}

public enum GlanceReadabilityMode
{
    None,
    Soft,
    Strong
}

public enum GlanceCalendarMaterialMode
{
    FollowSystem,
    FollowImage
}

public enum GlanceTraditionalCalendarMode
{
    None,
    Auto,
    ChineseLunar,
    UmAlQura,
    Hijri,
    IndianSaka,
    JapaneseEra,
    Bangla,
    Julian,
    Hebrew,
    Persian,
    ThaiBuddhist
}

public enum GlanceImageFitMode
{
    Fill,
    Fit
}

public enum GlanceImageFocus
{
    Center,
    Top,
    Bottom,
    Left,
    Right
}

/// <summary>
/// Versioned, portable preferences for one Glance widget. Downloaded images
/// and their catalog deliberately live outside this document.
/// </summary>
public sealed class GlanceWidgetData
{
    public const int CurrentVersion = 9;

    public int Version { get; set; } = CurrentVersion;
    public bool ShowTime { get; set; } = true;
    public bool ShowDate { get; set; } = true;
    public bool ShowYear { get; set; }
    public bool ShowWeekday { get; set; } = true;
    public bool ShowCalendar { get; set; }
    public GlanceLayoutMode Layout { get; set; } = GlanceLayoutMode.Centered;
    public GlanceBackgroundSource BackgroundSource { get; set; } = GlanceBackgroundSource.Bing;
    public GlanceOnlineImageCategory OnlineImageCategory { get; set; } =
        GlanceOnlineImageCategory.Featured;
    public List<string> LocalImagePaths { get; set; } = [];
    public string? LocalFolderPath { get; set; }
    public double RotationIntervalMinutes { get; set; } = 30;
    public bool RandomOrder { get; set; } = true;
    public GlanceTransitionMode Transition { get; set; } = GlanceTransitionMode.CrossFade;
    public GlanceTransitionSpeed TransitionSpeed { get; set; } = GlanceTransitionSpeed.Standard;
    public GlanceReadabilityMode Readability { get; set; } = GlanceReadabilityMode.Soft;
    /// <summary>
    /// Transparency applied only to the background image. Zero keeps the
    /// existing fully opaque presentation; one makes the image invisible.
    /// </summary>
    public double BackgroundImageTransparency { get; set; }
    public bool ShowPhotoControls { get; set; } = true;
    public GlanceCalendarMaterialMode CalendarMaterialMode { get; set; } =
        GlanceCalendarMaterialMode.FollowSystem;
    public double CalendarImageMaterialTransparency { get; set; } = 0.32;
    public GlanceTraditionalCalendarMode TraditionalCalendarMode { get; set; } =
        GlanceTraditionalCalendarMode.None;
    public bool ShowChineseFestivals { get; set; } = true;
    public GlanceImageFitMode ImageFit { get; set; } = GlanceImageFitMode.Fill;
    public GlanceImageFocus ImageFocus { get; set; } = GlanceImageFocus.Center;
    public string? TimeFontFamily { get; set; }
    public double TimeScale { get; set; } = 1;
}

public sealed class GlanceImageInfo
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string LocalPath { get; set; } = string.Empty;
    public string? Title { get; set; }
    public string? Author { get; set; }
    public string? License { get; set; }
    public string? LicenseUrl { get; set; }
    public string? SourcePageUrl { get; set; }
    public string? RemoteImageUrl { get; set; }
    public int PixelWidth { get; set; }
    public int PixelHeight { get; set; }
    public DateTimeOffset CachedAtUtc { get; set; }
    public GlanceOnlineImageCategory OnlineCategory { get; set; } =
        GlanceOnlineImageCategory.Featured;
    public GlanceOnlineImageProvider OnlineProvider { get; set; } =
        GlanceOnlineImageProvider.Wikimedia;

    [JsonIgnore]
    public bool IsOnline => !string.IsNullOrWhiteSpace(SourcePageUrl);
}

public sealed record GlanceCalendarDay(
    DateOnly Date,
    string DayText,
    bool IsCurrentMonth,
    bool IsToday,
    string TraditionalText = "",
    string FestivalText = "")
{
    public bool HasTraditionalText => !string.IsNullOrWhiteSpace(TraditionalText);
    public bool HasFestival => !string.IsNullOrWhiteSpace(FestivalText);
    public bool HasTraditionalTextOnly => HasTraditionalText && !HasFestival;
    public bool HasSecondaryText => HasFestival || HasTraditionalText;
}

// Bound from the calendar day-item template via {Binding Tag.*}; Native AOT
// resolves those bindings through this generated provider, reflection cannot.
[WinRT.GeneratedBindableCustomProperty]
public sealed partial record GlanceCalendarDayDecoration(
    string DayText,
    string SecondaryText,
    bool HasSecondaryText,
    bool IsToday,
    bool IsFestival,
    double PrimaryOpacity,
    double SecondaryOpacity);

public sealed record GlanceCalendarMonth(
    DateOnly Month,
    IReadOnlyList<string> WeekdayHeaders,
    IReadOnlyList<GlanceCalendarDay> Days,
    string TraditionalTitle = "")
{
    public bool HasTraditionalTitle => !string.IsNullOrWhiteSpace(TraditionalTitle);
}

public sealed record GlanceCalendarEvent(
    string Id,
    string Title,
    DateTimeOffset StartsAt,
    DateTimeOffset EndsAt,
    bool IsAllDay,
    string? CalendarColor = null);
