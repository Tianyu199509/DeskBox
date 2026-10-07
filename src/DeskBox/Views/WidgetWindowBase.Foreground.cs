using DeskBox.Helpers;
using DeskBox.Services;
using Microsoft.UI.Text;
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

    /// <summary>
    /// Builds the custom-foreground-color picker as an anchored flyout. The
    /// window decides where to show it; a ContentDialog inside a small widget
    /// window gets clipped by the window bounds, while a flyout follows the
    /// same escape-the-window placement the widget context menus use.
    /// </summary>
    protected Flyout BuildWidgetForegroundColorPickerFlyout()
    {
        var picker = new ColorPicker
        {
            Color = WidgetForegroundSettings.ResolveCustomColor(
                Config,
                SettingsService.Settings),
            IsAlphaEnabled = false,
            MinWidth = 256
        };
        var localization = App.Current.LocalizationService;
        var saveButton = new Button
        {
            Content = localization.T("Common.Save"),
            MinWidth = 96
        };
        var cancelButton = new Button
        {
            Content = localization.T("Common.Cancel"),
            MinWidth = 96
        };
        var flyout = new Flyout
        {
            ShouldConstrainToRootBounds = false,
            Content = new StackPanel
            {
                Spacing = 12,
                Children =
                {
                    new TextBlock
                    {
                        Text = localization.T("Widget.Foreground.CustomColor"),
                        FontWeight = FontWeights.SemiBold
                    },
                    picker,
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        HorizontalAlignment = HorizontalAlignment.Right,
                        Spacing = 8,
                        Children = { cancelButton, saveButton }
                    }
                }
            }
        };
        cancelButton.Click += (_, _) => flyout.Hide();
        saveButton.Click += (_, _) =>
        {
            try
            {
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
            finally
            {
                flyout.Hide();
            }
        };
        return flyout;
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
            // Reuse the shared cached UISettings instance instead of a new
            // projection per palette resolve; TryGetUiColor already carries
            // the unavailability fallback contract.
            Color? foreground = WindowsCompatibilityService.TryGetUiColor(
                UIColorType.Foreground);
            if (foreground is { } highContrastForeground)
            {
                return new WidgetForegroundPalette(
                    highContrastForeground,
                    highContrastForeground,
                    highContrastForeground,
                    highContrastForeground,
                    highContrastForeground);
            }

            // Continue with the selected palette if WinRT accessibility
            // colors are temporarily unavailable.
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
