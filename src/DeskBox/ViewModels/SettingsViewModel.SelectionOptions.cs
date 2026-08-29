using DeskBox.Models;
using DeskBox.Services;

namespace DeskBox.ViewModels;

public partial class SettingsViewModel
{
    public IReadOnlyList<SettingsOption> AvailableThemeOptions =>
        CreateSelectionOptions(AvailableThemes, AvailableThemeDisplayNames);

    public IReadOnlyList<SettingsOption> AvailableTrayIconStyleOptions =>
        CreateSelectionOptions(AvailableTrayIconStyles, AvailableTrayIconStyleDisplayNames);

    public IReadOnlyList<SettingsOption> AvailableLanguageOptions =>
        CreateSelectionOptions(AvailableLanguages, AvailableLanguageDisplayNames);

    public IReadOnlyList<SettingsOption> AvailableWidgetCornerPreferenceOptions =>
        CreateSelectionOptions(AvailableWidgetCornerPreferences, AvailableWidgetCornerPreferenceDisplayNames);

    public IReadOnlyList<SettingsOption> AvailableWidgetMaterialTypeOptions =>
        CreateSelectionOptions(AvailableWidgetMaterialTypes, AvailableWidgetMaterialTypeDisplayNames);

    public IReadOnlyList<SettingsOption> AvailableWidgetBorderColorModeOptions =>
        CreateSelectionOptions(AvailableWidgetBorderColorModes, AvailableWidgetBorderColorModeDisplayNames);

    public IReadOnlyList<SettingsOption> AvailableWidgetBorderStyleOptions =>
        CreateSelectionOptions(AvailableWidgetBorderStyles, AvailableWidgetBorderStyleDisplayNames);

    public IReadOnlyList<SettingsOption> AvailableWidgetCollapseBehaviorOptions =>
        CreateSelectionOptions(AvailableWidgetCollapseBehaviors, AvailableWidgetCollapseBehaviorDisplayNames);

    public IReadOnlyList<SettingsOption> AvailableWidgetCompactWidthModeOptions =>
        CreateSelectionOptions(
            AvailableWidgetCompactWidthModes,
            AvailableWidgetCompactWidthModeDisplayNames);

    public IReadOnlyList<SettingsOption> AvailableWidgetCompactExpansionDirectionOptions =>
        CreateSelectionOptions(
            AvailableWidgetCompactExpansionDirections,
            AvailableWidgetCompactExpansionDirectionDisplayNames);

    public IReadOnlyList<SettingsOption> AvailableWidgetCapsuleArrangementOptions =>
        CreateSelectionOptions(
            AvailableWidgetCapsuleArrangementModes,
            AvailableWidgetCapsuleArrangementDisplayNames);

    public IReadOnlyList<SettingsOption> AvailableWidgetCapsuleBarPlacementOptions =>
        CreateSelectionOptions(
            AvailableWidgetCapsuleBarPlacements,
            AvailableWidgetCapsuleBarPlacementDisplayNames);

    public IReadOnlyList<SettingsOption> AvailableWidgetCapsuleBarDirectionOptions =>
        CreateSelectionOptions(
            AvailableWidgetCapsuleBarDirections,
            AvailableWidgetCapsuleBarDirectionDisplayNames);

    public IReadOnlyList<SettingsOption> AvailableWidgetCompactContentModeOptions =>
        CreateSelectionOptions(AvailableWidgetCompactContentModes, AvailableWidgetCompactContentModeDisplayNames);

    public IReadOnlyList<SettingsOption> AvailableWidgetCompactAnimationEffectOptions =>
        CreateSelectionOptions(AvailableWidgetCompactAnimationEffects, AvailableWidgetCompactAnimationEffectDisplayNames);

    public IReadOnlyList<SettingsOption> AvailableWidgetCompactHoverResponseOptions =>
        CreateSelectionOptions(AvailableWidgetCompactHoverResponses, AvailableWidgetCompactHoverResponseDisplayNames);

    public IReadOnlyList<SettingsOption> AvailableWidgetCompactMediaCornerOptions =>
        CreateSelectionOptions(AvailableWidgetCompactMediaCornerModes, AvailableWidgetCompactMediaCornerDisplayNames);

    public IReadOnlyList<SettingsOption> AvailableLayoutDensityOptions =>
        CreateSelectionOptions(AvailableLayoutDensities, AvailableLayoutDensityDisplayNames);

    public IReadOnlyList<SettingsOption> AvailableFileNameLineCountOptions =>
    [
        new(SettingsService.MinFileNameLineCount, _localizationService.T("Settings.FileNameLines.Single")),
        new(SettingsService.MaxFileNameLineCount, _localizationService.T("Settings.FileNameLines.Double"))
    ];

    public IReadOnlyList<SettingsOption> AvailableAnimationPresetOptions =>
        CreateSelectionOptions(AvailableAnimationPresets, AvailableAnimationPresetDisplayNames);

    public IReadOnlyList<SettingsOption> AvailableWidgetAnimationEffectOptions =>
        CreateSelectionOptions(AvailableWidgetAnimationEffects, AvailableWidgetAnimationEffectDisplayNames);

    public IReadOnlyList<SettingsOption> AvailableWidgetAnimationSpeedOptions =>
        CreateSelectionOptions(AvailableWidgetAnimationSpeeds, AvailableWidgetAnimationSpeedDisplayNames);

    public IReadOnlyList<SettingsOption> AvailableWidgetAnimationSlideDirectionOptions =>
        CreateSelectionOptions(AvailableWidgetAnimationSlideDirections, AvailableWidgetAnimationSlideDirectionDisplayNames);

    public IReadOnlyList<SettingsOption> AvailableWidgetAnimationEasingIntensityOptions =>
        CreateSelectionOptions(AvailableWidgetAnimationEasingIntensities, AvailableWidgetAnimationEasingIntensityDisplayNames);

    public IReadOnlyList<SettingsOption> AvailableDisplayWidgetChromeModeOptions =>
        CreateSelectionOptions(AvailableDisplayWidgetChromeModes, AvailableDisplayWidgetChromeModeDisplayNames);

    public IReadOnlyList<SettingsOption> AvailableInteractiveWidgetChromeModeOptions =>
        CreateSelectionOptions(AvailableInteractiveWidgetChromeModes, AvailableInteractiveWidgetChromeModeDisplayNames);

    public IReadOnlyList<SettingsOption> AvailableWidgetTitleIconModeOptions =>
        CreateSelectionOptions(AvailableWidgetTitleIconModes, AvailableWidgetTitleIconModeDisplayNames);

    public IReadOnlyList<SettingsOption> AvailableWidgetLayerModeOptions =>
        CreateSelectionOptions(AvailableWidgetLayerModes, AvailableWidgetLayerModeDisplayNames);

    public IReadOnlyList<SettingsOption> AvailableQuickCaptureDefaultViewOptions =>
        CreateSelectionOptions(AvailableQuickCaptureDefaultViews, AvailableQuickCaptureDefaultViewDisplayNames);

    public IReadOnlyList<SettingsOption> AvailableQuickCaptureTabStyleOptions =>
        CreateSelectionOptions(AvailableWidgetTabStyles, AvailableQuickCaptureTabStyleDisplayNames);

    public IReadOnlyList<SettingsOption> AvailableItemPreviewLineCountOptions =>
        CreateSelectionOptions(AvailableItemPreviewLineCounts, AvailableItemPreviewLineCountDisplayNames);

    public IReadOnlyList<SettingsOption> AvailableEditorEnterBehaviorOptions =>
        CreateSelectionOptions(AvailableEditorEnterBehaviors, AvailableEditorEnterBehaviorDisplayNames);

    public IReadOnlyList<SettingsOption> AvailableQuickCaptureFormatOptions =>
        CreateSelectionOptions(AvailableQuickCaptureFormats, AvailableQuickCaptureFormatDisplayNames);

    public IReadOnlyList<SettingsOption> AvailableQuickCaptureWideLayoutOptions =>
        CreateSelectionOptions(AvailableQuickCaptureWideLayouts, AvailableQuickCaptureWideLayoutDisplayNames);

    public IReadOnlyList<SettingsOption> AvailableQuickCaptureWideOpenModeOptions =>
        CreateSelectionOptions(AvailableQuickCaptureWideOpenModes, AvailableQuickCaptureWideOpenModeDisplayNames);

    public IReadOnlyList<SettingsOption> AvailableTodoNewTaskPositionOptions =>
        CreateSelectionOptions(AvailableTodoNewTaskPositions, AvailableTodoNewTaskPositionDisplayNames);

    public IReadOnlyList<SettingsOption> AvailableTodoLayoutModeOptions =>
        CreateSelectionOptions(AvailableTodoLayoutModes, AvailableTodoLayoutModeDisplayNames);

    public IReadOnlyList<SettingsOption> AvailableAttachmentStorageModeOptions =>
        CreateSelectionOptions(AvailableAttachmentStorageModes, AvailableAttachmentStorageModeDisplayNames);

    public IReadOnlyList<SettingsOption> AvailableManagedDropActionOptions =>
        CreateSelectionOptions(AvailableManagedDropActions, AvailableManagedDropActionDisplayNames);

    public IReadOnlyList<SettingsOption> AvailableTodoDefaultFilterOptions =>
        CreateSelectionOptions(AvailableTodoDefaultFilters, AvailableTodoDefaultFilterDisplayNames);

    public IReadOnlyList<SettingsOption> AvailableTodoTabStyleOptions =>
        CreateSelectionOptions(AvailableWidgetTabStyles, AvailableTodoTabStyleDisplayNames);

    public IReadOnlyList<SettingsOption> AvailableTodoReminderOffsetOptions =>
        CreateSelectionOptions(AvailableTodoReminderOffsetMinutes, AvailableTodoReminderOffsetDisplayNames);

    public IReadOnlyList<SettingsOption> AvailableMusicDisplayModeOptions =>
        CreateSelectionOptions(AvailableMusicDisplayModes, AvailableMusicDisplayModeDisplayNames);

    public IReadOnlyList<SettingsOption> AvailableWeatherTemperatureUnitOptions =>
        CreateSelectionOptions(AvailableWeatherTemperatureUnits, AvailableWeatherTemperatureUnitDisplayNames);

    public IReadOnlyList<SettingsOption> AvailableWeatherWindSpeedUnitOptions =>
        CreateSelectionOptions(AvailableWeatherWindSpeedUnits, AvailableWeatherWindSpeedUnitDisplayNames);

    public IReadOnlyList<SettingsOption> AvailableWeatherDefaultViewOptions =>
        CreateSelectionOptions(AvailableWeatherDefaultViews, AvailableWeatherDefaultViewDisplayNames);

    public IReadOnlyList<SettingsOption> AvailableWeatherSkinOptions =>
        CreateSelectionOptions(AvailableWeatherSkins, AvailableWeatherSkinDisplayNames);

    public IReadOnlyList<SettingsOption> AvailableWeatherDataSourceOptions =>
        CreateSelectionOptions(AvailableWeatherDataSources, AvailableWeatherDataSourceDisplayNames);

    public IReadOnlyList<SettingsOption> AvailableWeatherRefreshIntervalOptions =>
        CreateSelectionOptions(AvailableWeatherRefreshIntervals, AvailableWeatherRefreshIntervalDisplayNames);

    public IReadOnlyList<SettingsOption> AvailableFileStackGroupByOptions =>
        CreateSelectionOptions(AvailableFileStackGroupBys, AvailableFileStackGroupByDisplayNames);

    public IReadOnlyList<SettingsOption> AvailableFileStackThresholdOptions =>
        CreateSelectionOptions(AvailableFileStackThresholds, AvailableFileStackThresholdDisplayNames);

    public IReadOnlyList<SettingsOption> AvailableFileStackOrderByOptions =>
        CreateSelectionOptions(AvailableFileStackOrderBys, AvailableFileStackOrderByDisplayNames);

    public IReadOnlyList<SettingsOption> AvailableFileStackUnmatchedBehaviorOptions =>
        CreateSelectionOptions(AvailableFileStackUnmatchedBehaviors, AvailableFileStackUnmatchedBehaviorDisplayNames);

    internal static IReadOnlyList<SettingsOption> CreateSelectionOptions<T>(
        IReadOnlyList<T> values,
        IReadOnlyList<string> displayNames)
    {
        if (values.Count != displayNames.Count)
        {
            throw new InvalidOperationException("Setting option values and display names must have the same length.");
        }

        var options = new SettingsOption[values.Count];
        for (int index = 0; index < values.Count; index++)
        {
            options[index] = new SettingsOption(values[index]!, displayNames[index]);
        }

        return options;
    }

    private void NotifySelectionOptionsChanged()
    {
        OnPropertyChanged(nameof(AvailableThemeOptions));
        OnPropertyChanged(nameof(AvailableAccentColorSourceOptions));
        OnPropertyChanged(nameof(AvailableFileOpenMethodOptions));
        OnPropertyChanged(nameof(AvailableFileWidgetFolderOpenBehaviorOptions));
        OnPropertyChanged(nameof(AvailableFileWidgetFolderOpenBehaviorOptionItems));
        OnPropertyChanged(nameof(AvailableShowDesktopBehaviorOptions));
        OnPropertyChanged(nameof(AvailableWeatherLocationModeOptions));
        OnPropertyChanged(nameof(AvailableTrayIconStyleOptions));
        OnPropertyChanged(nameof(AvailableLanguageOptions));
        OnPropertyChanged(nameof(AvailableWidgetCornerPreferenceOptions));
        OnPropertyChanged(nameof(AvailableWidgetMaterialTypeOptions));
        OnPropertyChanged(nameof(AvailableWidgetBorderColorModeOptions));
        OnPropertyChanged(nameof(AvailableWidgetBorderStyleOptions));
        OnPropertyChanged(nameof(AvailableWidgetCollapseBehaviorOptions));
        OnPropertyChanged(nameof(AvailableWidgetCompactWidthModeOptions));
        OnPropertyChanged(nameof(AvailableWidgetCompactExpansionDirectionOptions));
        OnPropertyChanged(nameof(AvailableWidgetCapsuleArrangementOptions));
        OnPropertyChanged(nameof(AvailableWidgetCapsuleBarPlacementOptions));
        OnPropertyChanged(nameof(AvailableWidgetCapsuleBarDirectionOptions));
        OnPropertyChanged(nameof(AvailableWidgetCompactContentModeOptions));
        OnPropertyChanged(nameof(AvailableWidgetCompactAnimationEffectOptions));
        OnPropertyChanged(nameof(AvailableWidgetCompactHoverResponseOptions));
        OnPropertyChanged(nameof(AvailableWidgetCompactMediaCornerOptions));
        OnPropertyChanged(nameof(AvailableLayoutDensityOptions));
        OnPropertyChanged(nameof(AvailableFileNameLineCountOptions));
        OnPropertyChanged(nameof(AvailableAnimationPresetOptions));
        OnPropertyChanged(nameof(AvailableWidgetAnimationEffectOptions));
        OnPropertyChanged(nameof(AvailableWidgetAnimationSpeedOptions));
        OnPropertyChanged(nameof(AvailableWidgetAnimationSlideDirectionOptions));
        OnPropertyChanged(nameof(AvailableWidgetAnimationEasingIntensityOptions));
        OnPropertyChanged(nameof(AvailableDisplayWidgetChromeModeOptions));
        OnPropertyChanged(nameof(AvailableInteractiveWidgetChromeModeOptions));
        OnPropertyChanged(nameof(AvailableWidgetTitleIconModeOptions));
        OnPropertyChanged(nameof(AvailableWidgetLayerModeOptions));
        OnPropertyChanged(nameof(AvailableQuickCaptureDefaultViewOptions));
        OnPropertyChanged(nameof(AvailableQuickCaptureTabStyleOptions));
        OnPropertyChanged(nameof(AvailableItemPreviewLineCountOptions));
        OnPropertyChanged(nameof(AvailableEditorEnterBehaviorOptions));
        OnPropertyChanged(nameof(AvailableQuickCaptureFormatOptions));
        OnPropertyChanged(nameof(AvailableQuickCaptureWideLayoutOptions));
        OnPropertyChanged(nameof(AvailableQuickCaptureWideOpenModeOptions));
        OnPropertyChanged(nameof(AvailableTodoNewTaskPositionOptions));
        OnPropertyChanged(nameof(AvailableTodoLayoutModeOptions));
        OnPropertyChanged(nameof(AvailableAttachmentStorageModeOptions));
        OnPropertyChanged(nameof(AvailableManagedDropActionOptions));
        OnPropertyChanged(nameof(AvailableTodoDefaultFilterOptions));
        OnPropertyChanged(nameof(AvailableTodoTabStyleOptions));
        OnPropertyChanged(nameof(AvailableTodoReminderOffsetOptions));
        OnPropertyChanged(nameof(AvailableMusicDisplayModeOptions));
        OnPropertyChanged(nameof(AvailableWeatherTemperatureUnitOptions));
        OnPropertyChanged(nameof(AvailableWeatherWindSpeedUnitOptions));
        OnPropertyChanged(nameof(AvailableWeatherDefaultViewOptions));
        OnPropertyChanged(nameof(AvailableWeatherSkinOptions));
        OnPropertyChanged(nameof(AvailableWeatherDataSourceOptions));
        OnPropertyChanged(nameof(AvailableWeatherRefreshIntervalOptions));
        OnPropertyChanged(nameof(AvailableFileStackGroupByOptions));
        OnPropertyChanged(nameof(AvailableFileStackThresholdOptions));
        OnPropertyChanged(nameof(AvailableFileStackOrderByOptions));
        OnPropertyChanged(nameof(AvailableFileStackUnmatchedBehaviorOptions));
    }
}
