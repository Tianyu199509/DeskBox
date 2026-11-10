using DeskBox.Models;

namespace DeskBox.Services;

internal static class GlanceWidgetSettingsPolicy
{
    /// <summary>
    /// Display elements exposed in the multi-select "显示内容" menu.
    /// Calendar is intentionally excluded so toggling display items never
    /// secretly overwrites the Layout dropdown (Calendar mode is entered
    /// exclusively via Layout = Calendar).
    /// </summary>
    public static IReadOnlyList<GlanceDisplayElement> SelectableDisplayElements { get; } =
    [
        GlanceDisplayElement.Time,
        GlanceDisplayElement.Seconds,
        GlanceDisplayElement.Date,
        GlanceDisplayElement.Year,
        GlanceDisplayElement.Weekday
    ];

    public static void SetLocalImageFiles(
        GlanceWidgetData settings,
        IEnumerable<string> imagePaths)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(imagePaths);

        settings.BackgroundSource = GlanceBackgroundSource.LocalFiles;
        settings.LocalImagePaths = imagePaths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => path.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static void ClearLocalSource(GlanceWidgetData settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (settings.BackgroundSource == GlanceBackgroundSource.LocalFolder)
        {
            settings.LocalFolderPath = null;
        }
        else
        {
            settings.LocalImagePaths.Clear();
        }
    }

    public static bool IsDisplayElementVisible(
        GlanceWidgetData settings,
        GlanceDisplayElement element)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return element switch
        {
            GlanceDisplayElement.Time => settings.ShowTime,
            GlanceDisplayElement.Seconds => settings.ShowTime && settings.ShowSeconds,
            GlanceDisplayElement.Date => settings.ShowDate,
            GlanceDisplayElement.Year => settings.ShowYear,
            GlanceDisplayElement.Weekday => settings.ShowWeekday,
            GlanceDisplayElement.Calendar => settings.ShowCalendar,
            _ => false
        };
    }

    public static void SetDisplayElement(
        GlanceWidgetData settings,
        GlanceDisplayElement element,
        bool isVisible)
    {
        ArgumentNullException.ThrowIfNull(settings);
        switch (element)
        {
            case GlanceDisplayElement.Time:
                settings.ShowTime = isVisible;
                if (!isVisible)
                {
                    settings.ShowSeconds = false;
                }
                break;
            case GlanceDisplayElement.Seconds:
                settings.ShowSeconds = isVisible;
                if (isVisible)
                {
                    settings.ShowTime = true;
                }
                break;
            case GlanceDisplayElement.Date:
                settings.ShowDate = isVisible;
                if (!isVisible)
                {
                    settings.ShowYear = false;
                }
                break;
            case GlanceDisplayElement.Year:
                settings.ShowYear = isVisible;
                if (isVisible)
                {
                    settings.ShowDate = true;
                }
                break;
            case GlanceDisplayElement.Weekday:
                settings.ShowWeekday = isVisible;
                break;
            case GlanceDisplayElement.Calendar:
                if (isVisible)
                {
                    SetStyle(settings, GlanceStyle.MonthCalendar);
                }
                else if (settings.Style == GlanceStyle.MonthCalendar)
                {
                    SetStyle(settings, GlanceStyle.PhotoClock);
                }
                break;
        }
    }

    /// <summary>
    /// Legacy entry point (context menus, smoke tests, older callers): maps a
    /// v10 layout onto the authoritative style.
    /// </summary>
    public static void SetLayout(
        GlanceWidgetData settings,
        GlanceLayoutMode layout)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ApplyLegacyLayout(settings, layout);
        SyncLegacyMirrors(settings);
    }

    public static void SetStyle(
        GlanceWidgetData settings,
        GlanceStyle style)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings.Style = Enum.IsDefined(style) ? style : GlanceStyle.PhotoClock;
        SyncLegacyMirrors(settings);
    }

    public static void SetPhotoClockAlignment(
        GlanceWidgetData settings,
        GlancePhotoClockAlignment alignment)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings.PhotoClockAlignment = Enum.IsDefined(alignment)
            ? alignment
            : GlancePhotoClockAlignment.Center;
        SyncLegacyMirrors(settings);
    }

    /// <summary>
    /// True when <see cref="GlanceWidgetData.Layout"/> must be treated as the
    /// authority: documents written before v11, and documents (or in-memory
    /// objects) that only set the legacy layout while the style is still at
    /// its default. New code always writes coherent mirrors, so a default
    /// style with a disagreeing layout can only come from a legacy writer.
    /// </summary>
    public static bool ShouldAdoptLegacyLayout(GlanceWidgetData settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (settings.Version < GlanceWidgetData.StyleVersion)
        {
            return true;
        }

        bool styleUntouched = settings.Style == GlanceStyle.PhotoClock &&
            settings.PhotoClockAlignment == GlancePhotoClockAlignment.Center;
        return styleUntouched && settings.Layout != GlanceLayoutMode.Centered;
    }

    /// <summary>
    /// Migrates pre-v11 documents onto <see cref="GlanceWidgetData.Style"/>.
    /// Documents written before v10 expressed calendar mode via
    /// <see cref="GlanceWidgetData.ShowCalendar"/> independently of
    /// <see cref="GlanceWidgetData.Layout"/>.
    /// </summary>
    public static void MigrateLegacyStyle(GlanceWidgetData settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (settings.Version < 10 && settings.ShowCalendar)
        {
            settings.Style = GlanceStyle.MonthCalendar;
            return;
        }

        ApplyLegacyLayout(settings, settings.Layout);
    }

    /// <summary>Derives <see cref="GlanceWidgetData.Style"/> from a v10 layout.</summary>
    public static void ApplyLegacyLayout(
        GlanceWidgetData settings,
        GlanceLayoutMode layout)
    {
        ArgumentNullException.ThrowIfNull(settings);
        switch (layout)
        {
            case GlanceLayoutMode.Immersive:
                settings.Style = GlanceStyle.PhotoClock;
                settings.PhotoClockAlignment = GlancePhotoClockAlignment.BottomLeft;
                break;
            case GlanceLayoutMode.Editorial:
                settings.Style = GlanceStyle.Editorial;
                break;
            case GlanceLayoutMode.Calendar:
                settings.Style = GlanceStyle.MonthCalendar;
                break;
            default:
                settings.Style = GlanceStyle.PhotoClock;
                settings.PhotoClockAlignment = GlancePhotoClockAlignment.Center;
                break;
        }
    }

    /// <summary>
    /// Writes the legacy <see cref="GlanceWidgetData.Layout"/> and
    /// <see cref="GlanceWidgetData.ShowCalendar"/> mirrors from the style so
    /// older builds reading a v11 document still render something sensible.
    /// </summary>
    public static void SyncLegacyMirrors(GlanceWidgetData settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings.Layout = settings.Style switch
        {
            GlanceStyle.PhotoClock when settings.PhotoClockAlignment == GlancePhotoClockAlignment.BottomLeft =>
                GlanceLayoutMode.Immersive,
            GlanceStyle.Editorial => GlanceLayoutMode.Editorial,
            GlanceStyle.MonthCalendar => GlanceLayoutMode.Calendar,
            _ => GlanceLayoutMode.Centered
        };
        settings.ShowCalendar = settings.Style == GlanceStyle.MonthCalendar;
    }

    public static void SetPhotoPlayback(
        GlanceWidgetData settings,
        double rotationIntervalMinutes,
        bool randomOrder,
        GlanceTransitionMode transition,
        GlanceTransitionSpeed transitionSpeed,
        GlanceReadabilityMode readability,
        bool showPhotoControls)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings.RotationIntervalMinutes = rotationIntervalMinutes;
        settings.RandomOrder = randomOrder;
        settings.Transition = transition;
        settings.TransitionSpeed = transitionSpeed;
        settings.Readability = readability;
        settings.ShowPhotoControls = showPhotoControls;
    }
}
