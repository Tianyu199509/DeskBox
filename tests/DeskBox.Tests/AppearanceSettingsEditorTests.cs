using DeskBox.Contracts;
using DeskBox.ViewModels;
using DeskBox.Features.Appearance;
using DeskBox.Models;
using DeskBox.Services;

namespace DeskBox.Tests;

/// <summary>
/// Third copy batch of the settings-shell binding-facade retirement: the
/// appearance family (main appearance section plus the material, density,
/// window and animation subsections) is re-bound to the appearance editor
/// through a section-level DataContext switch. These tests pin the editor's
/// behavior — read snapshot projection with the legacy constructor
/// normalization, write-through, no write-back on external sync, live-preview
/// linkage events, shell-pushed presentations (theme/accent/group-nav/host
/// environment), the density and animation preset state machines, slider
/// normalization reentry, localization refresh — and the migration pattern
/// itself (XAML paths, AOT bridges, window wiring, facade removal).
/// </summary>
public sealed class AppearanceSettingsEditorTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "DeskBox.Tests", Guid.NewGuid().ToString("N"));

    private static readonly Func<string, string> PassthroughLocalize = static key => key;

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private static (SettingsService Settings, AppearanceSettingsViewModel Editor) CreateEditor(
        string root,
        Action<SettingsService>? arrange = null)
    {
        var settings = new SettingsService(root);
        arrange?.Invoke(settings);
        var coordinator = new AppearanceSettingsCoordinator(settings);
        return (settings, new AppearanceSettingsViewModel(coordinator, PassthroughLocalize));
    }

    [Fact]
    public void Constructor_ProjectsPersistedPresentationFromTheReadSnapshot()
    {
        (_, AppearanceSettingsViewModel editor) = CreateEditor(
            _root,
            settings =>
            {
                WidgetShellSettingsSlice shell = settings.Settings.WidgetShell;
                shell.WidgetMaterialType = SettingsService.WidgetMaterialTypeSolid;
                shell.WidgetOpacity = 0.4d;
                shell.WidgetMaterialIntensity = 0.7d;
                shell.WidgetCornerPreference = SettingsService.WidgetCornerPreferenceSquare;
                shell.WidgetBorderColorMode = SettingsService.WidgetBorderColorModeNone;
                shell.WidgetBorderStyle = SettingsService.WidgetBorderStyleThick;
                shell.WidgetForegroundMode = WidgetForegroundSettings.ModeCustom;
                shell.WidgetForegroundColor = "#112233";
                shell.IconSize = 34d;
                shell.TextSize = 13d;
                shell.LayoutDensityScale = 0.6d;
                shell.HorizontalSpacingScale = 0.3d;
                shell.VerticalSpacingScale = 0.7d;
                settings.Settings.FileWidget.FileNameWidthScale = 0.4d;
                settings.Settings.FileWidget.FileNameLineCount = 0;
                shell.DefaultWidgetWidth = 320d;
                shell.DefaultWidgetHeight = 480d;
                shell.DisplayWidgetChromeMode = "Compact";
                shell.InteractiveWidgetChromeMode = "Hidden";
                shell.WidgetTitleIconMode = SettingsService.WidgetTitleIconModeTextLabel;
                shell.WidgetAnimationEffect = SettingsService.WidgetAnimationEffectZoom;
                shell.WidgetAnimationSpeed = SettingsService.WidgetAnimationSpeedFast;
                shell.WidgetAnimationSlideDirection = SettingsService.WidgetAnimationSlideDirectionLeft;
                shell.WidgetAnimationEasingIntensity = SettingsService.WidgetAnimationEasingStrong;
                settings.Settings.Core.TrayIconStyle = "Black";
            });

        Assert.Equal(SettingsService.WidgetMaterialTypeSolid, editor.MaterialType);
        Assert.Equal(0.6d, editor.WidgetTransparency, 3);
        Assert.Equal(0.7d, editor.MaterialIntensity);
        Assert.Equal(SettingsService.WidgetCornerPreferenceSquare, editor.CornerPreference);
        Assert.Equal(SettingsService.WidgetBorderColorModeNone, editor.BorderColorMode);
        Assert.False(editor.IsBorderStyleEnabled);
        Assert.Equal(SettingsService.WidgetBorderStyleThick, editor.BorderStyle);
        Assert.Equal(WidgetForegroundSettings.ModeCustom, editor.ForegroundMode);
        Assert.True(editor.ShowForegroundCustomColor);
        Assert.Equal("#112233", editor.ForegroundColorHex);
        Assert.Equal(34d, editor.IconSize);
        Assert.Equal(13d, editor.TextSize);
        Assert.Equal(0.6d, editor.LayoutDensityScale);
        Assert.Equal(0.3d, editor.HorizontalSpacingScale);
        Assert.Equal(0.7d, editor.VerticalSpacingScale);
        Assert.Equal(0.4d, editor.FileNameWidthScale);
        Assert.Equal(0, editor.FileNameLineCount);
        Assert.Equal(320d, editor.DefaultWidth);
        Assert.Equal(480d, editor.DefaultHeight);
        Assert.Equal("Compact", editor.DisplayChromeMode);
        Assert.Equal("Hidden", editor.InteractiveChromeMode);
        Assert.Equal(SettingsService.WidgetTitleIconModeTextLabel, editor.TitleIconMode);
        Assert.Equal(SettingsService.WidgetAnimationEffectZoom, editor.AnimationEffect);
        Assert.Equal(SettingsService.WidgetAnimationSpeedFast, editor.AnimationSpeed);
        Assert.Equal(SettingsService.WidgetAnimationSlideDirectionLeft, editor.AnimationSlideDirection);
        Assert.Equal(SettingsService.WidgetAnimationEasingStrong, editor.AnimationEasingIntensity);
        Assert.Equal(WidgetAnimationKinds.PresetCustom, editor.AnimationPreset);
        Assert.Equal("Black", editor.TrayIconStyle);
    }

    [Fact]
    public void ReadSnapshot_AppliesTheLegacyConstructorNormalization()
    {
        (_, AppearanceSettingsViewModel editor) = CreateEditor(
            _root,
            settings =>
            {
                WidgetShellSettingsSlice shell = settings.Settings.WidgetShell;
                shell.WidgetBorderColorMode = "Nope";
                shell.WidgetBorderStyle = "Nope";
                shell.WidgetAnimationEffect = "Nope";
                shell.WidgetAnimationSpeed = "Nope";
                shell.WidgetAnimationSlideDirection = "Nope";
                shell.WidgetAnimationEasingIntensity = "Nope";
                shell.WidgetTitleIconMode = "Nope";
                shell.DisplayWidgetChromeMode = "Nope";
                settings.Settings.FileWidget.FileNameLineCount = 7;
                settings.Settings.Core.TrayIconStyle = "Nope";
            });

        Assert.Equal(SettingsService.WidgetBorderColorModeNeutral, editor.BorderColorMode);
        Assert.True(editor.IsBorderStyleEnabled);
        Assert.Equal(SettingsService.WidgetBorderStyleThin, editor.BorderStyle);
        Assert.Equal(SettingsService.WidgetAnimationEffectSlideFade, editor.AnimationEffect);
        Assert.Equal(SettingsService.WidgetAnimationSpeedStandard, editor.AnimationSpeed);
        Assert.Equal(SettingsService.WidgetAnimationSlideDirectionRight, editor.AnimationSlideDirection);
        Assert.Equal(SettingsService.WidgetAnimationEasingStandard, editor.AnimationEasingIntensity);
        Assert.Equal(SettingsService.WidgetTitleIconModeColor, editor.TitleIconMode);
        Assert.Equal(WidgetChromeKinds.Overlay, editor.DisplayChromeMode);
        Assert.Equal(WidgetChromeKinds.Standard, editor.InteractiveChromeMode);
        Assert.Equal(SettingsService.DefaultFileNameLineCount, editor.FileNameLineCount);
        Assert.Equal("System", editor.TrayIconStyle);
        Assert.Equal(WidgetAnimationKinds.PresetStandard, editor.AnimationPreset);
    }

    [Fact]
    public void SliderEdits_WriteThroughTheCoordinatorAndRaiseTheSaveEvent()
    {
        (SettingsService settings, AppearanceSettingsViewModel editor) = CreateEditor(_root);
        int valueCommitted = 0;
        int textCommitted = 0;
        editor.AppearanceValueCommitted += () => valueCommitted++;
        editor.TextSizeCommitted += () => textCommitted++;

        editor.WidgetTransparency = 0.4d; // opacity 0.6 on a 0.02 grid
        Assert.Equal(0.6d, settings.Settings.WidgetShell.WidgetOpacity, 3);
        editor.MaterialIntensity = 0.5d;
        Assert.Equal(0.5d, settings.Settings.WidgetShell.WidgetMaterialIntensity);
        editor.IconSize = 26d;
        Assert.Equal(26d, settings.Settings.WidgetShell.IconSize);
        editor.LayoutDensityScale = 0.42d;
        Assert.Equal(0.42d, settings.Settings.WidgetShell.LayoutDensityScale);
        editor.HorizontalSpacingScale = 0.3d;
        editor.VerticalSpacingScale = 0.2d;
        editor.FileNameWidthScale = 0.44d;
        Assert.Equal(7, valueCommitted);

        editor.TextSize = 12.5d;
        Assert.Equal(12.5d, settings.Settings.WidgetShell.TextSize);
        Assert.Equal(1, textCommitted);
        Assert.Equal(7, valueCommitted);
    }

    [Fact]
    public void SliderEdits_ReenterWithTheNormalizedValueWithoutCommitting()
    {
        (SettingsService settings, AppearanceSettingsViewModel editor) = CreateEditor(_root);
        int valueCommitted = 0;
        editor.AppearanceValueCommitted += () => valueCommitted++;

        editor.IconSize = 25d; // snaps to the step-2 grid
        Assert.Equal(26d, editor.IconSize);
        Assert.Equal(26d, settings.Settings.WidgetShell.IconSize);
        // The normalized reentry commits exactly once.
        Assert.Equal(1, valueCommitted);

        editor.TextSize = 12.3d; // snaps to the 0.5 grid
        Assert.Equal(12.5d, editor.TextSize);
        Assert.Equal(12.5d, settings.Settings.WidgetShell.TextSize);
    }

    [Fact]
    public void SliderEdits_MarkTheDensitySelectionCustom()
    {
        (_, AppearanceSettingsViewModel editor) = CreateEditor(_root);
        Assert.Equal(LayoutDensityKinds.Standard, editor.LayoutDensity);

        editor.IconSize = 40d;
        Assert.Equal(LayoutDensityKinds.Custom, editor.LayoutDensity);
    }

    [Fact]
    public void DensityPreset_WritesSevenFieldsAndRaisesASingleSaveEvent()
    {
        (SettingsService settings, AppearanceSettingsViewModel editor) = CreateEditor(_root);
        int valueCommitted = 0;
        int markedCustom = 0;
        editor.AppearanceValueCommitted += () => valueCommitted++;
        editor.LayoutDensityMarkedCustom += () => markedCustom++;
        Assert.Equal(0, markedCustom);
        editor.IconSize = 40d; // flips the label to custom silently
        Assert.Equal(LayoutDensityKinds.Custom, editor.LayoutDensity);

        editor.LayoutDensity = LayoutDensityKinds.Relaxed;
        WidgetShellSettingsSlice shell = settings.Settings.WidgetShell;
        Assert.Equal(LayoutDensityKinds.Relaxed, shell.LayoutDensity);
        Assert.Equal(36d, shell.IconSize);
        Assert.Equal(13d, shell.TextSize);
        Assert.Equal(0.84d, shell.LayoutDensityScale, 5);
        Assert.Equal(0.68d, shell.HorizontalSpacingScale, 5);
        Assert.Equal(0.82d, shell.VerticalSpacingScale, 5);
        Assert.Equal(0.50d, settings.Settings.FileWidget.FileNameWidthScale);
        Assert.Equal(36d, editor.IconSize);
        Assert.Equal(0.84d, editor.LayoutDensityScale);
        // one event for the slider edit, one for the preset application
        Assert.Equal(2, valueCommitted);
        Assert.Equal(0, markedCustom);

        // Flipping the combo to "custom" explicitly raises the shell's
        // plain-save event.
        editor.LayoutDensity = LayoutDensityKinds.Custom;
        Assert.Equal(1, markedCustom);
    }

    [Fact]
    public void AnimationPreset_WritesFourFieldsDeferredAndRaisesThePresetEvent()
    {
        (SettingsService settings, AppearanceSettingsViewModel editor) = CreateEditor(_root);
        int presetApplied = 0;
        editor.AnimationPresetApplied += () => presetApplied++;

        editor.AnimationPreset = WidgetAnimationKinds.PresetEmphasized;
        WidgetShellSettingsSlice shell = settings.Settings.WidgetShell;
        Assert.Equal(SettingsService.WidgetAnimationEffectScaleFade, shell.WidgetAnimationEffect);
        Assert.Equal(SettingsService.WidgetAnimationSpeedRelaxed, shell.WidgetAnimationSpeed);
        Assert.Equal(SettingsService.WidgetAnimationSlideDirectionNone, shell.WidgetAnimationSlideDirection);
        Assert.Equal(SettingsService.WidgetAnimationEasingStrong, shell.WidgetAnimationEasingIntensity);
        Assert.Equal(1, presetApplied);
        Assert.Equal(WidgetAnimationKinds.PresetEmphasized, editor.AnimationPreset);

        // Individual edits resolve the preset label back to custom.
        editor.AnimationSpeed = SettingsService.WidgetAnimationSpeedFast;
        Assert.Equal(WidgetAnimationKinds.PresetCustom, editor.AnimationPreset);
    }

    [Fact]
    public void SlideFadeEffect_WithNoDirection_NudgesTheDirectionToRight()
    {
        (SettingsService settings, AppearanceSettingsViewModel editor) = CreateEditor(
            _root,
            arrange: settings =>
            {
                settings.Settings.WidgetShell.WidgetAnimationSlideDirection =
                    SettingsService.WidgetAnimationSlideDirectionNone;
                settings.Settings.WidgetShell.WidgetAnimationEffect =
                    SettingsService.WidgetAnimationEffectFade;
            });

        editor.AnimationEffect = SettingsService.WidgetAnimationEffectSlideFade;
        Assert.Equal(
            SettingsService.WidgetAnimationSlideDirectionRight,
            settings.Settings.WidgetShell.WidgetAnimationSlideDirection);
        Assert.False(editor.IsDirectionEnabled is false && editor.IsSpeedEnabled is false);
        Assert.True(editor.IsSpeedEnabled);
        Assert.True(editor.IsEasingEnabled);
        Assert.True(editor.IsDirectionEnabled);
    }

    [Fact]
    public void ExternalSync_ReprojectsWithoutWritingBackOrRaisingEvents()
    {
        (SettingsService settings, AppearanceSettingsViewModel editor) = CreateEditor(_root);
        int notified = 0;
        int valueCommitted = 0;
        settings.SettingsChanged += () => notified++;
        editor.AppearanceValueCommitted += () => valueCommitted++;

        settings.Settings.WidgetShell.IconSize = 44d;
        settings.Settings.WidgetShell.WidgetAnimationEffect = SettingsService.WidgetAnimationEffectFade;
        editor.SyncPresentation();

        Assert.Equal(44d, editor.IconSize);
        Assert.Equal(SettingsService.WidgetAnimationEffectFade, editor.AnimationEffect);
        Assert.Equal(0, notified);
        Assert.Equal(0, valueCommitted);
    }

    [Fact]
    public void PushedPresentations_UpdateSilently_WithoutUserEvents()
    {
        (_, AppearanceSettingsViewModel editor) = CreateEditor(_root);
        string? theme = null;
        bool? sourceUseSystem = null;
        string? accent = null;
        editor.ThemeUserChanged += value => theme = value;
        editor.AccentColorSourceUserChanged += value => sourceUseSystem = value;
        editor.AccentColorUserChanged += value => accent = value;

        editor.UpdateThemeSelection("Dark");
        editor.UpdateAccentPresentation(useSystemAccentColor: false, "#EF6950");

        Assert.Equal("Dark", editor.Theme);
        Assert.Equal("Custom", editor.AccentColorSource);
        Assert.True(editor.CanEditCustomAccent);
        Assert.Equal("#EF6950", editor.SelectedAccentColorHex);
        Assert.Null(theme);
        Assert.Null(sourceUseSystem);
        Assert.Null(accent);
    }

    [Fact]
    public void UserSelections_RaiseTheShellLinkageEvents()
    {
        (_, AppearanceSettingsViewModel editor) = CreateEditor(_root);
        string? theme = null;
        string? tray = null;
        bool? sourceUseSystem = null;
        string? accent = null;
        editor.ThemeUserChanged += value => theme = value;
        editor.TrayIconStyleUserChanged += value => tray = value;
        editor.AccentColorSourceUserChanged += value => sourceUseSystem = value;
        editor.AccentColorUserChanged += value => accent = value;

        editor.UpdateAccentPresentation(useSystemAccentColor: false, "#000000");
        editor.Theme = "Light";
        editor.TrayIconStyle = "White";
        editor.AccentColorSource = "System";
        editor.NotifyAccentPresetPicked("#038387");

        Assert.Equal("Light", theme);
        Assert.Equal("White", tray);
        Assert.True(sourceUseSystem);
        Assert.Equal("#038387", accent);
    }

    [Fact]
    public void HostEnvironmentPush_GatesTheMaterialOptionsAndCorners()
    {
        (_, AppearanceSettingsViewModel editor) = CreateEditor(_root);
        editor.UpdateHostEnvironment(
            windows10Compatibility: false,
            supportsNativeCorners: false,
            supportedMaterialKinds:
            [
                SettingsService.WidgetMaterialTypeAcrylic,
                SettingsService.WidgetMaterialTypeAcrylicBase,
                SettingsService.WidgetMaterialTypeSolid
            ]);

        Assert.False(editor.Windows10Compatibility);
        Assert.False(editor.NativeCornersSupported);
        Assert.Equal(3, editor.AvailableMaterialTypeOptions.Count);

        editor.UpdateHostEnvironment(
            windows10Compatibility: false,
            supportsNativeCorners: true,
            supportedMaterialKinds:
            [
                SettingsService.WidgetMaterialTypeAcrylic,
                SettingsService.WidgetMaterialTypeMica
            ]);
        Assert.Equal(2, editor.AvailableMaterialTypeOptions.Count);

        // The material-kind gates follow the current material type.
        editor.MaterialType = SettingsService.WidgetMaterialTypeMica;
        Assert.False(editor.ShowOpacitySlider);
        Assert.True(editor.ShowMaterialIntensitySlider);
        editor.MaterialType = SettingsService.WidgetMaterialTypeSolid;
        Assert.True(editor.ShowOpacitySlider);
        Assert.False(editor.ShowMaterialIntensitySlider);
    }

    [Fact]
    public void OptionEdits_WriteThroughTheCoordinatorWithTheirLegacySaveSemantics()
    {
        (SettingsService settings, AppearanceSettingsViewModel editor) = CreateEditor(_root);
        int notified = 0;
        int valueCommitted = 0;
        settings.SettingsChanged += () => notified++;
        editor.AppearanceValueCommitted += () => valueCommitted++;

        // Corner, border and chrome writes save inside the coordinator.
        editor.CornerPreference = SettingsService.WidgetCornerPreferenceSmall;
        Assert.Equal(1, notified);
        editor.BorderStyle = SettingsService.WidgetBorderStyleMedium;
        Assert.Equal(SettingsService.WidgetBorderStyleMedium, settings.Settings.WidgetShell.WidgetBorderStyle);
        editor.DisplayChromeMode = WidgetChromeKinds.Hidden;
        Assert.Equal(WidgetChromeKinds.Hidden, settings.Settings.WidgetShell.DisplayWidgetChromeMode);

        // Foreground and title-icon writes defer the save to the shell event.
        editor.ForegroundMode = WidgetForegroundSettings.ModeLight;
        Assert.Equal(WidgetForegroundSettings.ModeLight, settings.Settings.WidgetShell.WidgetForegroundMode);
        editor.ForegroundColorHex = "#FFAA00";
        Assert.Equal("#FFAA00", settings.Settings.WidgetShell.WidgetForegroundColor);
        editor.TitleIconMode = SettingsService.WidgetTitleIconModeHidden;
        Assert.Equal(SettingsService.WidgetTitleIconModeHidden, settings.Settings.WidgetShell.WidgetTitleIconMode);
        editor.FileNameLineCount = SettingsService.MinFileNameLineCount;
        Assert.Equal(SettingsService.MinFileNameLineCount, settings.Settings.FileWidget.FileNameLineCount);
        Assert.Equal(4, valueCommitted);
    }

    [Fact]
    public void DefaultSizeEdits_NormalizeReenterAndLetTheCoordinatorSave()
    {
        (SettingsService settings, AppearanceSettingsViewModel editor) = CreateEditor(_root);
        int valueCommitted = 0;
        editor.AppearanceValueCommitted += () => valueCommitted++;

        editor.DefaultWidth = 305d; // snaps to the step-10 grid
        Assert.Equal(310d, editor.DefaultWidth);
        Assert.Equal(310d, settings.Settings.WidgetShell.DefaultWidgetWidth);
        Assert.Equal(0, valueCommitted);
    }

    [Fact]
    public void RefreshLocalization_DropsNameCachesAndReprojectsTables()
    {
        (_, AppearanceSettingsViewModel editor) = CreateEditor(_root);
        int changes = 0;
        editor.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(AppearanceSettingsViewModel.AvailableThemeOptions) or
                nameof(AppearanceSettingsViewModel.MaterialTypeText) or
                nameof(AppearanceSettingsViewModel.Windows10CompatibilityTitle))
            {
                changes++;
            }
        };

        _ = editor.AvailableThemeOptions;
        _ = editor.MaterialTypeText;
        _ = editor.Windows10CompatibilityTitle;
        editor.RefreshLocalization();
        Assert.Equal(3, changes);

        // Option tables carry the canonical values with localized names.
        Assert.Equal(
            ["System", "Light", "Dark"],
            editor.AvailableThemeOptions.Select(option => option.Value));
        Assert.Equal(
            [LayoutDensityKinds.Compact, LayoutDensityKinds.Standard, LayoutDensityKinds.Relaxed, LayoutDensityKinds.Custom],
            editor.AvailableLayoutDensityOptions.Select(option => option.Value));
        Assert.Equal(
            [0, 1, 2],
            editor.AvailableFileNameLineCountOptions.Select(option => (int)option.Value));
    }

    [Fact]
    public void ShellFacade_AppearanceFamilyIsGoneFromTheSettingsViewModel()
    {
        // The batch's real deletion: none of the migrated binding paths may
        // survive as public shell properties (the dynamic AOT inventory test
        // keeps the bridge honest; this pins the reflection surface).
        System.Reflection.PropertyInfo[] properties = typeof(SettingsViewModel)
            .GetProperties(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public);
        string[] removed =
        [
            "SelectedTheme",
            "SelectedTrayIconStyle",
            "SelectedAccentColorSource",
            "SelectedAccentColor",
            "AvailableThemeOptions",
            "SelectedWidgetMaterialType",
            "SelectedLayoutDensity",
            "SelectedAnimationPreset",
            "SelectedWidgetTitleIconMode",
            "SelectedDisplayWidgetChromeMode",
            "SelectedInteractiveWidgetChromeMode",
            "SelectedWidgetForegroundMode",
            "SelectedWidgetForegroundColor",
            "WidgetTransparency",
            "WidgetMaterialIntensity",
            "IconSize",
            "TextSize",
            "LayoutDensityScale",
            "HorizontalSpacingScale",
            "VerticalSpacingScale",
            "FileNameWidthScale",
            "FileNameLineCount",
            "DefaultWidth",
            "DefaultHeight",
            "WidgetOpacityVisibility",
            "IsWindows10VisualCompatibilityMode",
            "SupportsNativeWidgetCorners",
            "AccentColorDescription",
            "CanEditCustomAccent",
        ];
        foreach (string name in removed)
        {
            Assert.DoesNotContain(properties, property => property.Name == name);
        }

        // Batch 44 recycled the group-navigation push group into the
        // group-navigation editor; the appearance shell surface no longer
        // carries any group-navigation facade.
        Assert.DoesNotContain(properties, property => property.Name == "SelectedWidgetGroupDefaultNavigationStyle");
    }

    [Fact]
    public void XamlAndBridge_WireTheAppearanceFamilyToTheEditor()
    {
        string window = File.ReadAllText(
            Path.Combine(TestPaths.FromRepository("src/DeskBox"), "Views/SettingsWindow.xaml"));
        string section = File.ReadAllText(
            Path.Combine(TestPaths.FromRepository("src/DeskBox"), "Views/SettingsSections/AppearanceSettingsSection.xaml"));
        string deferred = File.ReadAllText(
            Path.Combine(TestPaths.FromRepository("src/DeskBox"), "Views/SettingsWindow.DeferredSections.cs"));
        string bridge = File.ReadAllText(
            Path.Combine(TestPaths.FromRepository("src/DeskBox"), "Features/Appearance/AppearanceSettingsViewModel.AotBindableProperties.cs"));
        string shellBridge = File.ReadAllText(
            Path.Combine(TestPaths.FromRepository("src/DeskBox"), "ViewModels/SettingsViewModel.AotBindableProperties.cs"));

        // Editor binding paths on the section family ({Binding} markers kept).
        Assert.Contains("controls:SettingsComboBox.Value=\"{Binding Theme, Mode=TwoWay}\"", section);
        Assert.Contains("controls:SettingsComboBox.Value=\"{Binding TrayIconStyle, Mode=TwoWay}\"", section);
        Assert.Contains(
            "SelectedColor=\"{Binding SelectedAccentColorHex, Mode=TwoWay, Converter={StaticResource SettingsColorStringConverter}}\"",
            section);
        Assert.Contains("controls:SettingsComboBox.Value=\"{Binding MaterialType, Mode=TwoWay}\"", section);
        Assert.Contains("controls:SettingsComboBox.Value=\"{Binding LayoutDensity, Mode=TwoWay}\"", window);
        Assert.Contains("controls:SettingsComboBox.Value=\"{Binding TitleIconMode, Mode=TwoWay}\"", window);
        Assert.Contains("controls:SettingsComboBox.Value=\"{Binding AnimationPreset, Mode=TwoWay}\"", window);
        Assert.Contains("Value=\"{Binding WidgetTransparency, Mode=TwoWay}\"", window);
        Assert.Contains("Value=\"{Binding TextSize, Mode=TwoWay}\"", window);
        // Conditional appearance sub-settings stay visible and gray out
        // instead of collapsing, so the expanders always have content.
        Assert.Contains("IsEnabled=\"{Binding ShowOpacitySlider}\"", window);
        Assert.Contains("IsEnabled=\"{Binding ShowMaterialIntensitySlider}\"", window);
        Assert.Contains("IsEnabled=\"{Binding ShowForegroundCustomColor}\"", window);
        Assert.Contains("IsOpen=\"{Binding Windows10Compatibility}\"", window);
        Assert.Contains("IsEnabled=\"{Binding NativeCornersSupported}\"", window);

        // The legacy shell paths are gone from the appearance family.
        Assert.DoesNotContain("{Binding SelectedTheme", section + window);
        Assert.DoesNotContain("{Binding SelectedWidgetMaterialType", section + window);
        Assert.DoesNotContain("{Binding SelectedLayoutDensity", section + window);
        Assert.DoesNotContain("{Binding SelectedAnimationPreset", section + window);
        Assert.DoesNotContain("{Binding SelectedWidgetTitleIconMode", section + window);
        Assert.DoesNotContain("{Binding WidgetOpacityVisibility", window);
        Assert.DoesNotContain("{Binding IsWindows10VisualCompatibilityMode", window);

        // Section-level DataContext switch covers the whole family.
        Assert.Contains("\"Appearance\" or", deferred);
        Assert.Contains("\"AppearanceMaterialSettings\"", deferred);
        Assert.Contains("\"AppearanceDensitySettings\"", deferred);
        Assert.Contains("\"AppearanceWindowSettings\"", deferred);
        Assert.Contains("\"AppearanceAnimationSettings\"", deferred);
        Assert.Contains("section.DataContext = _appearanceSettingsViewModel;", deferred);

        // The editor exposes an AOT bridge and the shell bridge dropped the
        // migrated names.
        Assert.Contains("[WinRT.GeneratedBindableCustomProperty([", bridge);
        Assert.Contains("nameof(WidgetTransparency)", bridge);
        Assert.DoesNotContain("nameof(SelectedWidgetMaterialType)", shellBridge);
        Assert.DoesNotContain("nameof(WidgetTransparency)", shellBridge);
        Assert.DoesNotContain("nameof(SelectedTheme)", shellBridge);
    }

    [Fact]
    public void RefreshLocalization_ClearsEveryCachedNameArray()
    {
        // After a language change the option tables must re-localize: every
        // "_cached*Names" array has to be nulled inside RefreshLocalization,
        // otherwise a lazily built cache (??=) keeps serving the old language.
        System.Reflection.FieldInfo[] cachedNameFields = typeof(AppearanceSettingsViewModel)
            .GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            .Where(field => field.FieldType == typeof(string[]) &&
                            field.Name.StartsWith("_cached", StringComparison.Ordinal) &&
                            field.Name.EndsWith("Names", StringComparison.Ordinal))
            .ToArray();
        Assert.NotEmpty(cachedNameFields);

        string source = File.ReadAllText(
            Path.Combine(TestPaths.FromRepository("src/DeskBox"), "Features/Appearance/AppearanceSettingsViewModel.cs"));
        int methodStart = source.IndexOf("public void RefreshLocalization()", StringComparison.Ordinal);
        Assert.True(methodStart >= 0, "AppearanceSettingsViewModel.RefreshLocalization not found.");
        int methodEnd = source.IndexOf("private static string NormalizeHexColor", StringComparison.Ordinal);
        Assert.True(methodEnd > methodStart, "RefreshLocalization method bounds not found.");
        string body = source.Substring(methodStart, methodEnd - methodStart);

        foreach (System.Reflection.FieldInfo field in cachedNameFields)
        {
            Assert.True(
                body.Contains($"{field.Name} = null;", StringComparison.Ordinal),
                $"RefreshLocalization must null the {field.Name} cache, otherwise its " +
                "combo box keeps the previous language's option text.");
        }
    }
}
