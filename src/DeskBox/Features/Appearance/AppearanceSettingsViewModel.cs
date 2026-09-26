using DeskBox.Contracts;

namespace DeskBox.Features.Appearance;

/// <summary>
/// Appearance section editor. The legacy settings shell keeps every XAML/AOT
/// binding (sliders live on the shell so the drag-to-preview timing is
/// untouched) and forwards its writes here; this editor is the feature seam
/// over <see cref="IAppearanceSettings"/> and owns no duplicated state. All
/// normalization and persistence rules live in the coordinator.
/// </summary>
public sealed class AppearanceSettingsViewModel
{
    private readonly IAppearanceSettings _settings;

    public AppearanceSettingsViewModel(IAppearanceSettings settings)
    {
        _settings = settings;
    }

    public AppearanceMaterialSettings ReadMaterial() => _settings.ReadMaterial();

    public AppearanceDensitySettings ReadDensity() => _settings.ReadDensity();

    public AppearanceWindowChromeSettings ReadWindowChrome() => _settings.ReadWindowChrome();

    public AppearanceAnimationSettings ReadAnimation() => _settings.ReadAnimation();

    public AppearanceForegroundSettings ReadForeground() => _settings.ReadForeground();

    public void SetTrayIconStyle(string? style) => _settings.SetTrayIconStyle(style);

    public AppearanceValueUpdate UpdateWidgetOpacity(double value) =>
        _settings.UpdateWidgetOpacity(value);

    public AppearanceValueUpdate UpdateWidgetMaterialIntensity(double value) =>
        _settings.UpdateWidgetMaterialIntensity(value);

    public AppearanceValueUpdate UpdateIconSize(double value) =>
        _settings.UpdateIconSize(value);

    public AppearanceValueUpdate UpdateTextSize(double value) =>
        _settings.UpdateTextSize(value);

    public AppearanceValueUpdate UpdateLayoutDensityScale(double value) =>
        _settings.UpdateLayoutDensityScale(value);

    public AppearanceValueUpdate UpdateHorizontalSpacingScale(double value) =>
        _settings.UpdateHorizontalSpacingScale(value);

    public AppearanceValueUpdate UpdateVerticalSpacingScale(double value) =>
        _settings.UpdateVerticalSpacingScale(value);

    public AppearanceValueUpdate UpdateFileNameWidthScale(double value) =>
        _settings.UpdateFileNameWidthScale(value);

    public void SetWidgetMaterialType(string? materialType) =>
        _settings.SetWidgetMaterialType(materialType);

    public void SetWidgetCornerPreference(string? preference) =>
        _settings.SetWidgetCornerPreference(preference);

    public void SetWidgetBorderColorMode(string? mode) =>
        _settings.SetWidgetBorderColorMode(mode);

    public void SetWidgetBorderStyle(string? style) =>
        _settings.SetWidgetBorderStyle(style);

    public void SetFileNameLineCount(int lineCount) =>
        _settings.SetFileNameLineCount(lineCount);

    public void MarkLayoutDensityCustom() => _settings.MarkLayoutDensityCustom();

    public void ApplyLayoutDensityPreset(string preset) =>
        _settings.ApplyLayoutDensityPreset(preset);

    public AppearanceValueUpdate UpdateDefaultWidgetWidth(double value) =>
        _settings.UpdateDefaultWidgetWidth(value);

    public AppearanceValueUpdate UpdateDefaultWidgetHeight(double value) =>
        _settings.UpdateDefaultWidgetHeight(value);

    public void SetDisplayWidgetChromeMode(string? mode) =>
        _settings.SetDisplayWidgetChromeMode(mode);

    public void SetInteractiveWidgetChromeMode(string? mode) =>
        _settings.SetInteractiveWidgetChromeMode(mode);

    public void SetWidgetTitleIconMode(string? mode) =>
        _settings.SetWidgetTitleIconMode(mode);

    public void SetAnimationEffect(string? effect, bool scheduleSave = true) =>
        _settings.SetAnimationEffect(effect, scheduleSave);

    public void SetAnimationSpeed(string? speed, bool scheduleSave = true) =>
        _settings.SetAnimationSpeed(speed, scheduleSave);

    public void SetAnimationSlideDirection(string? direction, bool scheduleSave = true) =>
        _settings.SetAnimationSlideDirection(direction, scheduleSave);

    public void SetAnimationEasingIntensity(string? intensity, bool scheduleSave = true) =>
        _settings.SetAnimationEasingIntensity(intensity, scheduleSave);

    public void SetWidgetForegroundMode(string? mode) =>
        _settings.SetWidgetForegroundMode(mode);

    public void SetWidgetForegroundColor(string colorHex) =>
        _settings.SetWidgetForegroundColor(colorHex);
}
