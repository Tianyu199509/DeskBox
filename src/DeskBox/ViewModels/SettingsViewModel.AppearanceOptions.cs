using CommunityToolkit.Mvvm.Input;
using DeskBox.Helpers;
using DeskBox.Models;
using DeskBox.Services;

namespace DeskBox.ViewModels;

public partial class SettingsViewModel
{
    public bool CanToggleHoverActionLockPosition => CanToggleHoverButtonAction(ShowHoverActionLockPosition);
    public bool CanToggleHoverActionLockSize => CanToggleHoverButtonAction(ShowHoverActionLockSize);
    public bool CanToggleHoverActionAdd => CanToggleHoverButtonAction(ShowHoverActionAdd);
    public bool CanToggleHoverActionMore => CanToggleHoverButtonAction(ShowHoverActionMore);
    public bool CanToggleHoverActionDelete => CanToggleHoverButtonAction(ShowHoverActionDelete);
    // The hover-button action summary is owned by this shell's flyout
    // selection state machine; the interaction editor binds the section's
    // DropDownButton to a pushed projection of it.
    internal string BuildHoverButtonActionsSummary() => !ShowHoverButtons
        ? _localizationService.T("Settings.HoverButtonActions.None")
        : string.Join(
            _localizationService.IsChinese ? "、" : ", ",
            AvailableWidgetHoverButtonActions
                .Where(IsHoverButtonActionSelected)
                .Select(GetHoverButtonActionDisplayName));

    public string[] AvailableWidgetHoverButtonActions { get; } =
    [
        SettingsService.WidgetHoverActionLockPosition,
        SettingsService.WidgetHoverActionLockSize,
        SettingsService.WidgetHoverActionAdd,
        SettingsService.WidgetHoverActionMore,
        SettingsService.WidgetHoverActionDelete
    ];

    // Host linkage for the interaction editor: the editor persists the layer
    // mode through the coordinator; the desktop-layer refresh stays on the
    // shell because it reaches the host's widget manager.
    private void OnInteractionLayerModeUserChanged()
    {
        App.Current?.WidgetManager?.RefreshVisibleWidgetDesktopLayers("settings-layer-mode");
    }

    public string SelectedLanguage
    {
        get => _selectedLanguage;
        set
        {
            string normalizedValue = LocalizationService.NormalizeLanguageSetting(value);
            if (!SetProperty(ref _selectedLanguage, normalizedValue))
            {
                return;
            }

            if (_isRestoringDefaults)
            {
                return;
            }

            _localizationService.SetLanguage(normalizedValue);
        }
    }

    public string SelectedLanguageText => _localizationService.GetLanguageDisplayName(SelectedLanguage);

    // --- Appearance-section host linkage ---
    //
    // The appearance editor owns the section family's binding surface and
    // persisted writes; these handlers run the host-side effects that used to
    // surround the legacy facade writes. The live-preview orchestration
    // (SaveAppearanceChange / CommitAppearanceChanges / the slider drag
    // flags) stays on this shell and is driven by the editor's value events.
    // Batch 44 recycled the group-navigation push group into the
    // group-navigation editor (the WidgetGroups section owns those fields).

    private void OnAppearanceThemeUserChanged(string value)
    {
        _themeService.SetTheme(value is ThemeLight or ThemeDark ? value : ThemeSystem);
    }

    private void OnAppearanceTrayIconStyleUserChanged(string value)
    {
        // The editor already persisted the style through the appearance
        // coordinator; the tray icon refresh reaches the host.
        App.Current.UpdateTrayIcon();
    }

    private void OnAppearanceAccentColorSourceUserChanged(bool useSystem)
    {
        // Keep the shell's accent-mode flag in step with the editor write:
        // RefreshAccentPreview pushes this flag back into the editor, so a
        // stale value here would bounce the combo selection right back.
        UseSystemAccentColor = useSystem;
        _themeService.SetAccentMode(
            useSystem ? ThemeService.AccentModeSystem : ThemeService.AccentModeCustom);
        RefreshAccentPreview();
    }

    private void OnAppearanceAccentColorUserChanged(string colorHex)
    {
        if (AccentColorHelper.TryParseHex(colorHex, out Windows.UI.Color color))
        {
            SetCustomAccentColor(color);
        }
    }

    private void OnAppearanceValueCommitted()
    {
        SaveAppearanceChange();
    }

    private void OnAppearanceTextSizeCommitted()
    {
        SaveAppearanceChange();
        // Global text size also drives the effective inherited font sizes of
        // the Todo and Quick Capture sections; keep them in sync immediately
        // (batch 22 regression) while their raw override values stay as-is.
        _todoSettings.Refresh();
        _quickCaptureSettings.RefreshFromSettings();
    }

    private void OnAppearanceLayoutDensityMarkedCustom()
    {
        _settingsService.SaveDebounced();
    }

    private void OnAppearanceAnimationPresetApplied()
    {
        _settingsService.SaveDebounced();
    }

    // --- Appearance-section push surface ---
    //
    // Selections whose state machines stay on this shell (theme service,
    // accent mode/effective color, OS capability probes) are pushed onto the
    // editor; the editor never writes them back except through the
    // user-changed events above. The group-navigation push moved to the
    // group-navigation editor in batch 44.

    private void PushAppearanceThemeSelection()
    {
        string theme = _settingsService.Settings.Core.Theme;
        _appearanceSettings.UpdateThemeSelection(
            theme is ThemeLight or ThemeDark ? theme : ThemeSystem);
    }

    private void PushAppearanceAccentPresentation()
    {
        _appearanceSettings.UpdateAccentPresentation(
            UseSystemAccentColor,
            AccentColorHelper.ToHex(_currentAccentColor));
    }

    private void PushAppearanceHostEnvironment()
    {
        _appearanceSettings.UpdateHostEnvironment(
            !WindowsCompatibilityService.IsWindows11OrLater,
            WindowsCompatibilityService.SupportsNativeWindowCorners,
            ResolveSupportedMaterialKinds(WindowsCompatibilityService.IsWindows11OrLater));
    }

    /// <summary>
    /// The OS filter behind the material picker: the Mica kinds are never
    /// offered below Windows 11 — their effective value degrades to acrylic
    /// there anyway (<see cref="WindowsCompatibilityService.ResolveWidgetMaterialTypeForBuild"/>).
    /// </summary>
    internal static string[] ResolveSupportedMaterialKinds(bool isWindows11OrLater) =>
        isWindows11OrLater
            ?
            [
                SettingsService.WidgetMaterialTypeAcrylic,
                SettingsService.WidgetMaterialTypeAcrylicBase,
                SettingsService.WidgetMaterialTypeMica,
                SettingsService.WidgetMaterialTypeMicaAlt,
                SettingsService.WidgetMaterialTypeSolid
            ]
            :
            [
                SettingsService.WidgetMaterialTypeAcrylic,
                SettingsService.WidgetMaterialTypeAcrylicBase,
                SettingsService.WidgetMaterialTypeSolid
            ];

    [RelayCommand]
    public void ResetDisplayWidgetChromeOverrides()
    {
        ResetWidgetChromeOverrides(WidgetChromeCategory.Display);
    }

    [RelayCommand]
    public void ResetInteractiveWidgetChromeOverrides()
    {
        ResetWidgetChromeOverrides(WidgetChromeCategory.Interactive);
    }

    internal int ResetWidgetChromeOverrides(WidgetChromeCategory category)
    {
        int changed = ResetWidgetChromeOverrides(
            _settingsService.Settings,
            _widgetContentFactory,
            category);

        if (changed > 0)
        {
            _settingsService.SaveDebounced();
        }

        return changed;
    }

    internal static int ResetWidgetChromeOverrides(
        AppSettings settings,
        WidgetContentFactory widgetContentFactory,
        WidgetChromeCategory category,
        string? mode = null)
    {
        int changed = 0;

        foreach (var widget in settings.Widgets)
        {
            WidgetContentDescriptor descriptor;
            try
            {
                descriptor = widgetContentFactory.GetDescriptor(widget.WidgetKind);
            }
            catch (NotSupportedException)
            {
                continue;
            }

            if (descriptor.ChromeCategory != category)
            {
                continue;
            }

            if (widget.Metadata is null ||
                !widget.Metadata.ContainsKey(WidgetChromeModeNames.MetadataKey))
            {
                continue;
            }

            WidgetChromeModeNames.SetOverrideMode(widget, WidgetChromeMode.System);
            changed++;
        }

        return changed;
    }
}
