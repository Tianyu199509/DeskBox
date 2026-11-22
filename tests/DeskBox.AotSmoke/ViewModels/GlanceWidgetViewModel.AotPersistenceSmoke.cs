#if DESKBOX_NATIVE_AOT && DESKBOX_AOT_SMOKE_HARNESS
using DeskBox.Models;

namespace DeskBox.ViewModels;

public sealed partial class GlanceWidgetViewModel
{
    internal AotGlanceViewModelSnapshot CaptureAotGlanceSnapshot()
    {
        return new AotGlanceViewModelSnapshot(
            _settings.BackgroundSource.ToString(),
            _settings.LocalImagePaths.ToArray(),
            _settings.ShowTime,
            _settings.ShowDate,
            _settings.ShowYear,
            _settings.ShowWeekday,
            _settings.ShowCalendar,
            _settings.Layout.ToString(),
            _settings.RotationIntervalMinutes,
            _settings.RandomOrder,
            _settings.Transition.ToString(),
            _settings.TransitionSpeed.ToString(),
            _settings.Readability.ToString(),
            BackgroundImageTransparency,
            BackgroundImageOpacity,
            _settings.ShowPhotoControls,
            ImageCount,
            CurrentImagePath,
            HasCurrentImage,
            IsCenteredLayout,
            IsEditorialLayout,
            ReadabilityOpacity,
            ShowPhotoControls);
    }
}

internal sealed record AotGlanceViewModelSnapshot(
    string BackgroundSource,
    IReadOnlyList<string> LocalImagePaths,
    bool ShowTime,
    bool ShowDate,
    bool ShowYear,
    bool ShowWeekday,
    bool ShowCalendar,
    string Layout,
    double RotationIntervalMinutes,
    bool RandomOrder,
    string Transition,
    string TransitionSpeed,
    string Readability,
    double BackgroundImageTransparency,
    double BackgroundImageOpacity,
    bool ShowPhotoControlsSetting,
    int ImageCount,
    string? CurrentImagePath,
    bool HasCurrentImage,
    bool IsCenteredLayout,
    bool IsEditorialLayout,
    double ReadabilityOpacity,
    bool ShowPhotoControls);
#endif
