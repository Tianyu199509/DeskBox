using DeskBox.Helpers;
using DeskBox.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using Windows.UI.ViewManagement;

namespace DeskBox.Views;

public abstract partial class WidgetWindowBase
{
    private AccessibilitySettings? _foregroundAccessibilitySettings;

    protected void ApplyWidgetForegroundAppearance()
    {
        EnsureForegroundAccessibilityWatcher();
        bool highContrast = _foregroundAccessibilitySettings?.HighContrast == true;
        string mode = WidgetForegroundSettings.ResolveMode(
            Config,
            SettingsService.Settings);
        Color customColor = WidgetForegroundSettings.ResolveCustomColor(
            Config,
            SettingsService.Settings);
        WidgetForegroundPalette palette = ResolveForegroundPalette(
            mode,
            customColor,
            RootElement.ActualTheme,
            highContrast);

        ApplyForegroundBrushes(palette);
        WidgetShellControl.SetGroupTitleForegroundColors(
            palette.Primary, palette.Secondary, palette.Disabled, highContrast);
    }

    protected void SetWidgetForegroundModeOverride(string? mode)
    {
        WidgetForegroundSettings.SetModeOverride(Config, mode);
        SettingsService.UpdateWidget(Config);
        ApplyWidgetForegroundAppearance();
    }

    protected async Task ShowWidgetForegroundColorPickerAsync()
    {
        if (RootElement.XamlRoot is null)
        {
            return;
        }

        var picker = new ColorPicker
        {
            Color = WidgetForegroundSettings.ResolveCustomColor(
                Config,
                SettingsService.Settings),
            IsAlphaEnabled = false,
            MinWidth = 340
        };
        var localization = App.Current.LocalizationService;
        var dialog = new ContentDialog
        {
            XamlRoot = RootElement.XamlRoot,
            Title = localization.T("Widget.Foreground.CustomColor"),
            Content = picker,
            PrimaryButtonText = localization.T("Common.Save"),
            CloseButtonText = localization.T("Common.Cancel"),
            DefaultButton = ContentDialogButton.Primary
        };

        try
        {
            if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            {
                return;
            }

            WidgetForegroundSettings.SetCustomColorOverride(Config, picker.Color);
            WidgetForegroundSettings.SetModeOverride(
                Config,
                WidgetForegroundSettings.ModeCustom);
            SettingsService.UpdateWidget(Config);
            ApplyWidgetForegroundAppearance();
        }
        catch (Exception ex)
        {
            App.Log($"[WidgetForeground] Color picker failed: {ex.Message}");
        }
    }

    private void EnsureForegroundAccessibilityWatcher()
    {
        if (_foregroundAccessibilitySettings is not null)
        {
            return;
        }

        try
        {
            _foregroundAccessibilitySettings = new AccessibilitySettings();
            _foregroundAccessibilitySettings.HighContrastChanged +=
                ForegroundAccessibilitySettings_HighContrastChanged;
        }
        catch (Exception ex)
        {
            App.LogVerbose($"[WidgetForeground] Accessibility watcher unavailable: {ex.Message}");
        }
    }

    private void ForegroundAccessibilitySettings_HighContrastChanged(
        AccessibilitySettings sender,
        object args)
    {
        if (!DispatcherQueue.TryEnqueue(ApplyWidgetForegroundAppearance))
        {
            App.LogVerbose("[WidgetForeground] Could not queue high-contrast refresh.");
        }
    }

    private void CleanupWidgetForegroundAppearance()
    {
        if (_foregroundAccessibilitySettings is not null)
        {
            _foregroundAccessibilitySettings.HighContrastChanged -=
                ForegroundAccessibilitySettings_HighContrastChanged;
            _foregroundAccessibilitySettings = null;
        }
    }

    private void ApplyForegroundBrushes(WidgetForegroundPalette palette)
    {
        SetBrushColor(palette.Primary,
            "TextFillColorPrimaryBrush",
            "ControlStrongFillColorDefaultBrush",
            "ButtonForeground",
            "ButtonForegroundPointerOver",
            "SubtleButtonForeground",
            "SubtleButtonForegroundPointerOver");
        SetBrushColor(palette.Secondary,
            "TextFillColorSecondaryBrush",
            "ControlStrongStrokeColorDefaultBrush",
            "ButtonForegroundPressed",
            "SubtleButtonForegroundPressed");
        SetBrushColor(palette.Tertiary,
            "TextFillColorTertiaryBrush",
            "WidgetDragHandleBrush");
        SetBrushColor(palette.Disabled,
            "TextFillColorDisabledBrush",
            "ControlStrongFillColorDisabledBrush",
            "ControlStrongStrokeColorDisabledBrush",
            "ButtonForegroundDisabled",
            "SubtleButtonForegroundDisabled");
        SetBrushColor(palette.Divider, "DividerStrokeColorDefaultBrush");
    }

    private void SetBrushColor(Color color, params string[] keys)
    {
        foreach (string key in keys)
        {
            if (RootElement.Resources.TryGetValue(key, out object? value) &&
                value is SolidColorBrush brush)
            {
                brush.Color = color;
            }
        }
    }

    private static WidgetForegroundPalette ResolveForegroundPalette(
        string mode,
        Color customColor,
        ElementTheme actualTheme,
        bool highContrast)
    {
        if (highContrast)
        {
            try
            {
                var uiSettings = new UISettings();
                Color foreground = uiSettings.GetColorValue(UIColorType.Foreground);
                return new WidgetForegroundPalette(
                    foreground,
                    foreground,
                    foreground,
                    foreground,
                    foreground);
            }
            catch
            {
                // Continue with the selected palette if WinRT accessibility
                // colors are temporarily unavailable.
            }
        }

        Color primary = mode switch
        {
            WidgetForegroundSettings.ModeLight =>
                Color.FromArgb(0xFF, 0xF7, 0xF7, 0xF7),
            WidgetForegroundSettings.ModeDark =>
                Color.FromArgb(0xFF, 0x1A, 0x1A, 0x1A),
            WidgetForegroundSettings.ModeCustom =>
                Color.FromArgb(0xFF, customColor.R, customColor.G, customColor.B),
            _ when actualTheme == ElementTheme.Light =>
                Color.FromArgb(0xFF, 0x1A, 0x1A, 0x1A),
            _ => Color.FromArgb(0xFF, 0xF7, 0xF7, 0xF7)
        };

        return new WidgetForegroundPalette(
            primary,
            WithAlpha(primary, 0xE8),
            WithAlpha(primary, 0xCC),
            WithAlpha(primary, 0x8F),
            WithAlpha(primary, 0x66));
    }

    private static Color WithAlpha(Color color, byte alpha) =>
        Color.FromArgb(alpha, color.R, color.G, color.B);

    private readonly record struct WidgetForegroundPalette(
        Color Primary,
        Color Secondary,
        Color Tertiary,
        Color Disabled,
        Color Divider);
}
