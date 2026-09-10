using DeskBox.Models;

namespace DeskBox.GlancePackage.Rendering;

/// <summary>
/// Pure normalization rules ported VERBATIM from the built-in
/// GlanceWidgetStore.Normalize (audit round 21 R2): the package must
/// produce the same effective settings from the same input as the built-in
/// widget. Applied to the typed model after loading from the migrated
/// file; the raw JSON is preserved separately for lossless round-trip.
/// </summary>
internal static class GlanceSettingsNormalizer
{
    private static readonly double[] SupportedRotationIntervals =
    [
        0,
        10d / 60d,
        30d / 60d,
        1,
        2,
        5,
        10,
        30,
        60,
        360,
        1440
    ];

    public static void Normalize(GlanceWidgetData data)
    {
        data.LocalImagePaths ??= [];
        data.LocalImagePaths = data.LocalImagePaths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => path.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        data.LocalFolderPath = string.IsNullOrWhiteSpace(data.LocalFolderPath)
            ? null
            : data.LocalFolderPath.Trim();
        if (!data.ShowDate)
        {
            data.ShowYear = false;
        }

        // Snap to the supported rotation interval set; unsupported values
        // fall back to 30 minutes (built-in parity).
        double normalizedRotation = SupportedRotationIntervals.FirstOrDefault(
            interval => Math.Abs(interval - data.RotationIntervalMinutes) < 0.0001,
            double.NaN);
        data.RotationIntervalMinutes = double.IsNaN(normalizedRotation)
            ? 30
            : normalizedRotation;

        data.TimeScale = Math.Clamp(data.TimeScale, 0.75, 1.35);
        data.TimeFontFamily = string.IsNullOrWhiteSpace(data.TimeFontFamily)
            ? null
            : data.TimeFontFamily.Trim();
        data.TimeFormat = Enum.IsDefined(data.TimeFormat)
            ? data.TimeFormat
            : GlanceTimeFormatMode.FollowSystem;
        data.Layout = Enum.IsDefined(data.Layout) ? data.Layout : GlanceLayoutMode.Centered;
        data.BackgroundSource = Enum.IsDefined(data.BackgroundSource)
            ? data.BackgroundSource
            : GlanceBackgroundSource.Bing;
        data.Transition = Enum.IsDefined(data.Transition) ? data.Transition : GlanceTransitionMode.CrossFade;
        data.TransitionSpeed = Enum.IsDefined(data.TransitionSpeed) ? data.TransitionSpeed : GlanceTransitionSpeed.Standard;
        data.Readability = Enum.IsDefined(data.Readability) ? data.Readability : GlanceReadabilityMode.Soft;
        data.BackgroundImageTransparency = double.IsFinite(data.BackgroundImageTransparency)
            ? Math.Clamp(data.BackgroundImageTransparency, 0.0, 1.0)
            : 0.0;
        data.TraditionalCalendarMode = Enum.IsDefined(data.TraditionalCalendarMode)
            ? data.TraditionalCalendarMode
            : GlanceTraditionalCalendarMode.None;
        data.ImageFit = Enum.IsDefined(data.ImageFit) ? data.ImageFit : GlanceImageFitMode.Fill;
        data.ImageFocus = Enum.IsDefined(data.ImageFocus) ? data.ImageFocus : GlanceImageFocus.Center;
    }
}
