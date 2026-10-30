using DeskBox.Contracts;
using DeskBox.Features.Appearance;
using DeskBox.Models;
using DeskBox.Services;

namespace DeskBox.Tests;

public sealed class AppearanceSettingsCoordinatorTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "DeskBox.Tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public async Task SliderUpdates_NormalizeWithStepRounding_AndPersistRawValues()
    {
        var settings = new SettingsService(_root);
        var coordinator = new AppearanceSettingsCoordinator(settings);

        AppearanceValueUpdate icon = coordinator.UpdateIconSize(25);
        Assert.False(icon.Committed);
        Assert.Equal(26, icon.Value);

        Assert.True(coordinator.UpdateIconSize(26).Committed);
        Assert.Equal(26, settings.Settings.WidgetShell.IconSize);

        AppearanceValueUpdate halfPoint = coordinator.UpdateTextSize(12.3);
        Assert.False(halfPoint.Committed);
        Assert.Equal(12.5, halfPoint.Value);
        Assert.True(coordinator.UpdateTextSize(12.5).Committed);
        Assert.Equal(12.5, settings.Settings.WidgetShell.TextSize);

        AppearanceValueUpdate spacing = coordinator.UpdateHorizontalSpacingScale(0.413);
        Assert.False(spacing.Committed);
        Assert.Equal(0.42, spacing.Value);
        Assert.True(coordinator.UpdateHorizontalSpacingScale(0.42).Committed);
        Assert.Equal(0.42, settings.Settings.WidgetShell.HorizontalSpacingScale);

        AppearanceValueUpdate clamped = coordinator.UpdateLayoutDensityScale(4);
        Assert.False(clamped.Committed);
        Assert.Equal(1.0, clamped.Value);

        await settings.SaveAsync();
        var reloaded = new SettingsService(_root);
        await reloaded.LoadAsync();
        Assert.Equal(26, reloaded.Settings.WidgetShell.IconSize);
        Assert.Equal(12.5, reloaded.Settings.WidgetShell.TextSize);
        Assert.Equal(0.42, reloaded.Settings.WidgetShell.HorizontalSpacingScale);
    }

    [Fact]
    public void ContentPaddingUpdate_ClampsToTheSupportedRange_AndRejectsNonFiniteInput()
    {
        var settings = new SettingsService(_root);
        var coordinator = new AppearanceSettingsCoordinator(settings);
        WidgetShellSettingsSlice shell = settings.Settings.WidgetShell;
        Assert.Equal(SettingsService.DefaultContentPadding, shell.ContentPadding);

        AppearanceValueUpdate clampedHigh = coordinator.UpdateContentPadding(50);
        Assert.False(clampedHigh.Committed);
        Assert.Equal(SettingsService.MaxContentPadding, clampedHigh.Value);
        Assert.Equal(SettingsService.DefaultContentPadding, shell.ContentPadding);

        AppearanceValueUpdate clampedLow = coordinator.UpdateContentPadding(-4);
        Assert.False(clampedLow.Committed);
        Assert.Equal(SettingsService.MinContentPadding, clampedLow.Value);

        Assert.True(coordinator.UpdateContentPadding(0).Committed);
        Assert.Equal(0, shell.ContentPadding);
        Assert.True(coordinator.UpdateContentPadding(24).Committed);
        Assert.Equal(24, shell.ContentPadding);

        AppearanceValueUpdate nan = coordinator.UpdateContentPadding(double.NaN);
        Assert.False(nan.Committed);
        Assert.Equal(24, nan.Value);
        Assert.Equal(24, shell.ContentPadding);

        // A corrupt settings file cannot smuggle a non-finite value past the
        // normalizer: infinities and NaN fall back to the default padding.
        Assert.Equal(
            SettingsService.DefaultContentPadding,
            SettingsService.NormalizeContentPadding(double.PositiveInfinity));
        Assert.Equal(
            SettingsService.DefaultContentPadding,
            SettingsService.NormalizeContentPadding(double.NaN));
        Assert.Equal(
            SettingsService.MaxContentPadding,
            SettingsService.NormalizeContentPadding(100));
    }

    [Fact]
    public void SliderUpdates_RejectNonFiniteInput_AndRestoreStoredValue()
    {
        var settings = new SettingsService(_root);
        settings.Settings.WidgetShell.WidgetOpacity = 0.6;
        settings.Settings.FileWidget.FileNameWidthScale = 0.5;
        var editor = new AppearanceSettingsCoordinator(settings);

        AppearanceValueUpdate opacity = editor.UpdateWidgetOpacity(double.NaN);
        Assert.False(opacity.Committed);
        Assert.Equal(0.6, opacity.Value);

        AppearanceValueUpdate intensity = editor.UpdateWidgetMaterialIntensity(
            double.PositiveInfinity);
        Assert.False(intensity.Committed);
        Assert.Equal(
            SettingsService.DefaultWidgetMaterialIntensity,
            intensity.Value);

        AppearanceValueUpdate width = editor.UpdateFileNameWidthScale(double.NaN);
        Assert.False(width.Committed);
        Assert.Equal(0.5, width.Value);

        Assert.Equal(0.6, settings.Settings.WidgetShell.WidgetOpacity);
        Assert.Equal(0.5, settings.Settings.FileWidget.FileNameWidthScale);
    }

    [Fact]
    public void SliderWrites_OwnNoSaveOrNotification_LeavingPreviewTimingToTheShell()
    {
        var settings = new SettingsService(_root);
        var coordinator = new AppearanceSettingsCoordinator(settings);
        int notified = 0;
        settings.SettingsChanged += () => notified++;

        Assert.True(coordinator.UpdateWidgetOpacity(0.5).Committed);
        Assert.True(coordinator.UpdateIconSize(30).Committed);
        Assert.True(coordinator.UpdateTextSize(12).Committed);
        Assert.True(coordinator.UpdateLayoutDensityScale(0.7).Committed);
        Assert.True(coordinator.UpdateHorizontalSpacingScale(0.3).Committed);
        Assert.True(coordinator.UpdateVerticalSpacingScale(0.5).Committed);
        Assert.True(coordinator.UpdateFileNameWidthScale(0.4).Committed);
        Assert.True(coordinator.UpdateWidgetMaterialIntensity(0.4).Committed);
        Assert.Equal(0, notified);

        // Option edits keep their previous immediate-save semantics.
        coordinator.SetWidgetCornerPreference(
            SettingsService.WidgetCornerPreferenceSquare);
        Assert.Equal(1, notified);
        Assert.Equal(
            SettingsService.WidgetCornerPreferenceSquare,
            settings.Settings.WidgetShell.WidgetCornerPreference);
    }

    [Fact]
    public void OptionSetters_NormalizeInvalidValues_SkipNoOpWrites()
    {
        var settings = new SettingsService(_root);
        var coordinator = new AppearanceSettingsCoordinator(settings);
        int notified = 0;
        settings.SettingsChanged += () => notified++;

        coordinator.SetWidgetMaterialType("NotAMaterial");
        Assert.Equal(
            SettingsService.WidgetMaterialTypeAcrylic,
            settings.Settings.WidgetShell.WidgetMaterialType);

        coordinator.SetWidgetBorderColorMode("Nope");
        Assert.Equal(
            SettingsService.WidgetBorderColorModeNeutral,
            settings.Settings.WidgetShell.WidgetBorderColorMode);

        coordinator.SetTrayIconStyle("Colorful");
        Assert.Equal("Colorful", settings.Settings.Core.TrayIconStyle);
        coordinator.SetTrayIconStyle(null);
        Assert.Equal("System", settings.Settings.Core.TrayIconStyle);

        // Unchanged option writes must not notify or save again.
        coordinator.SetWidgetCornerPreference(
            SettingsService.WidgetCornerPreferenceSquare);
        int notifiedAfterCorner = notified;
        coordinator.SetWidgetCornerPreference(
            SettingsService.WidgetCornerPreferenceSquare);
        Assert.Equal(notifiedAfterCorner, notified);
    }

    [Fact]
    public void TrayIconStyle_HiddenRoundTrips_UnknownValuesStillNormalize()
    {
        var settings = new SettingsService(_root);
        var coordinator = new AppearanceSettingsCoordinator(settings);

        coordinator.SetTrayIconStyle("Hidden");
        Assert.Equal("Hidden", settings.Settings.Core.TrayIconStyle);
        Assert.Equal("Hidden", coordinator.ReadTrayIconStyle());

        // A stored hidden style must survive the read projection: the shell
        // keys its "skip tray icon creation" decision off this value.
        settings.Settings.Core.TrayIconStyle = "Hidden";
        Assert.Equal("Hidden", coordinator.ReadTrayIconStyle());

        coordinator.SetTrayIconStyle("Nope");
        Assert.Equal("System", settings.Settings.Core.TrayIconStyle);
    }

    [Fact]
    public void LayoutDensityWrites_PresetAppliesSevenFieldsWithoutSchedulingASave()
    {
        var settings = new SettingsService(_root);
        var coordinator = new AppearanceSettingsCoordinator(settings);
        int notified = 0;
        settings.SettingsChanged += () => notified++;

        coordinator.ApplyLayoutDensityPreset(SettingsService.LayoutDensityRelaxed);
        Assert.Equal(SettingsService.LayoutDensityRelaxed, settings.Settings.WidgetShell.LayoutDensity);
        Assert.Equal(36, settings.Settings.WidgetShell.IconSize);
        Assert.Equal(13, settings.Settings.WidgetShell.TextSize);
        Assert.Equal(0.84, settings.Settings.WidgetShell.LayoutDensityScale);
        Assert.Equal(0.68, settings.Settings.WidgetShell.HorizontalSpacingScale);
        Assert.Equal(0.82, settings.Settings.WidgetShell.VerticalSpacingScale);
        Assert.Equal(0.50, settings.Settings.FileWidget.FileNameWidthScale);
        Assert.Equal(0, notified);

        coordinator.MarkLayoutDensityCustom();
        Assert.Equal(
            SettingsService.LayoutDensityCustom,
            settings.Settings.WidgetShell.LayoutDensity);
        Assert.Equal(0, notified);
    }

    [Fact]
    public void AnimationWrites_DefersSaveWhileApplyingPreset()
    {
        var settings = new SettingsService(_root);
        var coordinator = new AppearanceSettingsCoordinator(settings);
        int notified = 0;
        settings.SettingsChanged += () => notified++;

        coordinator.SetAnimationEffect(SettingsService.WidgetAnimationEffectZoom, scheduleSave: false);
        coordinator.SetAnimationSpeed(SettingsService.WidgetAnimationSpeedFast, scheduleSave: false);
        coordinator.SetAnimationSlideDirection(
            SettingsService.WidgetAnimationSlideDirectionLeft, scheduleSave: false);
        coordinator.SetAnimationEasingIntensity(
            SettingsService.WidgetAnimationEasingStrong, scheduleSave: false);
        Assert.Equal(0, notified);

        coordinator.SetAnimationEasingIntensity(SettingsService.WidgetAnimationEasingLight);
        Assert.Equal(1, notified);

        AppearanceAnimationSettings snapshot = coordinator.ReadAnimation();
        Assert.Equal(SettingsService.WidgetAnimationEffectZoom, snapshot.Effect);
        Assert.Equal(SettingsService.WidgetAnimationSpeedFast, snapshot.Speed);
        Assert.Equal(SettingsService.WidgetAnimationSlideDirectionLeft, snapshot.SlideDirection);
        Assert.Equal(SettingsService.WidgetAnimationEasingLight, snapshot.EasingIntensity);
    }

    [Fact]
    public void ForegroundAndWindowChromeWrites_DeferPreviewToTheShell()
    {
        var settings = new SettingsService(_root);
        var coordinator = new AppearanceSettingsCoordinator(settings);
        int notified = 0;
        settings.SettingsChanged += () => notified++;

        coordinator.SetWidgetForegroundMode(WidgetForegroundSettings.ModeCustom);
        coordinator.SetWidgetForegroundColor("#112233");
        coordinator.SetWidgetTitleIconMode(SettingsService.WidgetTitleIconModeFilledMono);
        coordinator.SetFileNameLineCount(SettingsService.MinFileNameLineCount);
        Assert.Equal(0, notified);
        Assert.Equal(WidgetForegroundSettings.ModeCustom, settings.Settings.WidgetShell.WidgetForegroundMode);
        Assert.Equal("#112233", settings.Settings.WidgetShell.WidgetForegroundColor);
        Assert.Equal(SettingsService.WidgetTitleIconModeFilledMono, settings.Settings.WidgetShell.WidgetTitleIconMode);
        Assert.Equal(SettingsService.MinFileNameLineCount, settings.Settings.FileWidget.FileNameLineCount);

        // Window chrome and default size keep their immediate-save semantics.
        coordinator.SetDisplayWidgetChromeMode("Compact");
        Assert.Equal(1, notified);
        Assert.True(coordinator.UpdateDefaultWidgetWidth(300).Committed);
        Assert.Equal(300, settings.Settings.WidgetShell.DefaultWidgetWidth);
        AppearanceValueUpdate snapped = coordinator.UpdateDefaultWidgetHeight(455);
        Assert.False(snapped.Committed);
        Assert.Equal(460, snapped.Value);
    }

    [Fact]
    public async Task StoppedCoordinator_RejectsFurtherWrites()
    {
        var settings = new SettingsService(_root);
        var coordinator = new AppearanceSettingsCoordinator(settings);
        coordinator.Stop();

        Assert.Throws<ObjectDisposedException>(
            () => coordinator.UpdateWidgetOpacity(0.5));
        Assert.Throws<ObjectDisposedException>(
            () => coordinator.SetWidgetMaterialType(SettingsService.WidgetMaterialTypeSolid));
        Assert.Throws<ObjectDisposedException>(
            () => coordinator.MarkLayoutDensityCustom());

        await settings.SaveAsync();
        Assert.Equal(
            SettingsService.DefaultWidgetOpacity,
            settings.Settings.WidgetShell.WidgetOpacity);
    }

    [Fact]
    public async Task ClearPerWidgetBackgrounds_WithExternalChannel_PredictsCountAndSkipsLocalMutation()
    {
        var settings = new SettingsService(_root);
        await settings.LoadAsync();
        var widget = new WidgetConfig { Id = "w-clear-external", WidgetKind = WidgetKind.File };
        WidgetBackgroundCustomization.SetColorOverride(
            widget,
            Windows.UI.Color.FromArgb(0xFF, 0xAA, 0x11, 0x22));
        settings.Settings.Widgets.Add(widget);

        int externalCalls = 0;
        var coordinator = new AppearanceSettingsCoordinator(
            settings,
            clearPerWidgetBackgroundsExternal: () =>
            {
                externalCalls++;
                return Task.CompletedTask;
            });

        int cleared = coordinator.ClearPerWidgetBackgrounds();

        Assert.Equal(1, cleared);
        Assert.Equal(1, externalCalls);
        // The local mirror stays untouched — the MAIN process owns the
        // clear (layout domain), and its response slice re-merges this
        // mirror. Deleting asset files host-side would strand a
        // half-applied state (batch R8).
        Assert.NotNull(WidgetBackgroundCustomization.GetColorOverride(widget));
    }

    [Fact]
    public async Task ClearPerWidgetBackgrounds_WithoutExternalChannel_ClearsLocally()
    {
        var settings = new SettingsService(_root);
        await settings.LoadAsync();
        var widget = new WidgetConfig { Id = "w-clear-local", WidgetKind = WidgetKind.File };
        WidgetBackgroundCustomization.SetColorOverride(
            widget,
            Windows.UI.Color.FromArgb(0xFF, 0xAA, 0x11, 0x22));
        settings.Settings.Widgets.Add(widget);

        var coordinator = new AppearanceSettingsCoordinator(settings);
        int cleared = coordinator.ClearPerWidgetBackgrounds();

        Assert.Equal(1, cleared);
        Assert.Null(WidgetBackgroundCustomization.GetColorOverride(widget));
    }
}
