using DeskBox.Models;

namespace DeskBox.Services;

internal static class WidgetTopologyAppearanceService
{
    internal static WidgetTopologyAppearance Capture(AppSettings settings) => Normalize(new()
    {
        IconSize = settings.WidgetShell.IconSize,
        TextSize = settings.WidgetShell.TextSize,
        HorizontalSpacingScale = settings.WidgetShell.HorizontalSpacingScale,
        VerticalSpacingScale = settings.WidgetShell.VerticalSpacingScale,
        FileNameWidthScale = settings.FileWidget.FileNameWidthScale
    });

    internal static WidgetTopologyAppearance Normalize(WidgetTopologyAppearance value) => value with
    {
        IconSize = SettingsService.NormalizeIconSize(value.IconSize),
        TextSize = SettingsService.NormalizeTextSize(value.TextSize),
        HorizontalSpacingScale = Spacing(value.HorizontalSpacingScale, 0.1),
        VerticalSpacingScale = Spacing(value.VerticalSpacingScale, 0.3),
        FileNameWidthScale = Spacing(value.FileNameWidthScale, 0.25)
    };

    private static double Spacing(double value, double fallback) => double.IsFinite(value)
        ? Math.Clamp(value, SettingsService.MinSpacingScale, SettingsService.MaxSpacingScale)
        : fallback;

    internal static bool CaptureActive(AppSettings settings, string currentTopologyKey)
    {
        if (settings.WidgetLayout.WidgetTopologyAppearanceDefaults is null ||
            !string.Equals(settings.WidgetLayout.ActiveWidgetTopologyKey, currentTopologyKey, StringComparison.Ordinal) ||
            !settings.WidgetLayout.WidgetTopologyLayouts.TryGetValue(currentTopologyKey, out var profile)) return false;
        var appearance = Capture(settings);
        if (profile.Appearance == appearance) return false;
        profile.Appearance = appearance;
        return true;
    }

    internal static bool Apply(AppSettings settings, WidgetTopologyLayoutProfile profile)
    {
        // Missing profiles must use the opt-in baseline, never inherit the last
        // monitor's large dimensions. With no baseline, legacy behavior is unchanged.
        if (settings.WidgetLayout.WidgetTopologyAppearanceDefaults is null) return false;
        var appearance = Normalize(profile.Appearance ?? settings.WidgetLayout.WidgetTopologyAppearanceDefaults);
        bool changed = Capture(settings) != appearance || profile.Appearance != appearance;
        profile.Appearance = appearance;
        settings.WidgetShell.IconSize = appearance.IconSize;
        settings.WidgetShell.TextSize = appearance.TextSize;
        settings.WidgetShell.HorizontalSpacingScale = appearance.HorizontalSpacingScale;
        settings.WidgetShell.VerticalSpacingScale = appearance.VerticalSpacingScale;
        settings.FileWidget.FileNameWidthScale = appearance.FileNameWidthScale;
        return changed;
    }
}
