namespace DeskBox.Contracts;

/// <summary>
/// Outcome of a numeric appearance edit. The settings shell re-enters its own
/// binding with <see cref="Value"/> unless the update committed, which keeps
/// the slider normalization feedback loop inside the page.
/// </summary>
public readonly record struct AppearanceValueUpdate(double Value, bool Committed)
{
    public static AppearanceValueUpdate Rejected(double storedValue) =>
        new(storedValue, Committed: false);

    public static AppearanceValueUpdate NeedsNormalization(double normalizedValue) =>
        new(normalizedValue, Committed: false);

    public static AppearanceValueUpdate CommittedValue(double storedValue) =>
        new(storedValue, Committed: true);
}

public readonly record struct AppearanceMaterialSettings(
    string MaterialType,
    double Opacity,
    double MaterialIntensity,
    string CornerPreference,
    string BorderColorMode,
    string BorderStyle);

public readonly record struct AppearanceDensitySettings(
    string LayoutDensity,
    double IconSize,
    double TextSize,
    double LayoutDensityScale,
    double HorizontalSpacingScale,
    double VerticalSpacingScale,
    double FileNameWidthScale,
    int FileNameLineCount);

public readonly record struct AppearanceWindowChromeSettings(
    double DefaultWidgetWidth,
    double DefaultWidgetHeight,
    string DisplayWidgetChromeMode,
    string InteractiveWidgetChromeMode,
    string WidgetTitleIconMode);

public readonly record struct AppearanceAnimationSettings(
    string Effect,
    string Speed,
    string SlideDirection,
    string EasingIntensity);

public readonly record struct AppearanceForegroundSettings(
    string ForegroundMode,
    string ForegroundColor);

/// <summary>
/// Settings-page writes for the appearance section: material, density,
/// typography, default widget size, window chrome, animation, foreground and
/// tray icon style. The settings shell keeps the XAML/AOT binding surface and
/// the live-preview timing; this port owns the raw persisted values only.
/// </summary>
public interface IAppearanceSettings
{
    AppearanceMaterialSettings ReadMaterial();
    AppearanceDensitySettings ReadDensity();
    AppearanceWindowChromeSettings ReadWindowChrome();
    AppearanceAnimationSettings ReadAnimation();
    AppearanceForegroundSettings ReadForeground();

    void SetTrayIconStyle(string? style);

    // Slider-driven values: normalize, write the raw field, and leave the
    // preview/debounce dance to the shell's SaveAppearanceChange path.
    AppearanceValueUpdate UpdateWidgetOpacity(double value);
    AppearanceValueUpdate UpdateWidgetMaterialIntensity(double value);
    AppearanceValueUpdate UpdateIconSize(double value);
    AppearanceValueUpdate UpdateTextSize(double value);
    AppearanceValueUpdate UpdateLayoutDensityScale(double value);
    AppearanceValueUpdate UpdateHorizontalSpacingScale(double value);
    AppearanceValueUpdate UpdateVerticalSpacingScale(double value);
    AppearanceValueUpdate UpdateFileNameWidthScale(double value);

    void SetWidgetMaterialType(string? materialType);
    void SetWidgetCornerPreference(string? preference);
    void SetWidgetBorderColorMode(string? mode);
    void SetWidgetBorderStyle(string? style);

    void SetFileNameLineCount(int lineCount);
    void MarkLayoutDensityCustom();
    void ApplyLayoutDensityPreset(string preset);

    AppearanceValueUpdate UpdateDefaultWidgetWidth(double value);
    AppearanceValueUpdate UpdateDefaultWidgetHeight(double value);
    void SetDisplayWidgetChromeMode(string? mode);
    void SetInteractiveWidgetChromeMode(string? mode);
    void SetWidgetTitleIconMode(string? mode);

    // Animation preset application writes all four fields before a single
    // save; individual edits pass the default and save immediately.
    void SetAnimationEffect(string? effect, bool scheduleSave = true);
    void SetAnimationSpeed(string? speed, bool scheduleSave = true);
    void SetAnimationSlideDirection(string? direction, bool scheduleSave = true);
    void SetAnimationEasingIntensity(string? intensity, bool scheduleSave = true);

    void SetWidgetForegroundMode(string? mode);
    void SetWidgetForegroundColor(string colorHex);
}
