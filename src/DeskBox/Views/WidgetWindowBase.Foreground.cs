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

    protected virtual void OnWidgetForegroundChanged()
    {
    }

    protected void SetWidgetForegroundModeOverride(string? mode)
    {
        WidgetForegroundSettings.SetModeOverride(Config, mode);
        SettingsService.UpdateWidget(Config);
        ApplyWidgetForegroundAppearance();
        OnWidgetForegroundChanged();
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
                    new TextBlock
                    {
                        Text = localization.T("Widget.Foreground.ContrastHint"),
                        FontSize = 11,
                        Opacity = 0.7,
                        TextWrapping = TextWrapping.Wrap
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
                OnWidgetForegroundChanged();
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
        if (flyout.Content is StackPanel rootPanel &&
            rootPanel.Children.Count > 0 &&
            rootPanel.Children[0] is TextBlock titleText)
        {
            rootPanel.Children.RemoveAt(0);
            titleText.VerticalAlignment = VerticalAlignment.Center;
            var headerGrid = new Grid { ColumnSpacing = 8 };
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            headerGrid.Children.Add(titleText);
            Button closeButton = WidgetEditorPopoverWindow.CreateHeaderCloseButton(
                localization.T("Common.Close"),
                () => flyout.Hide(),
                RootElement);
            Grid.SetColumn(closeButton, 1);
            headerGrid.Children.Add(closeButton);
            rootPanel.Children.Insert(0, headerGrid);
            rootPanel.GotFocus += (_, e) =>
                FlyoutTextInputActivation.ActivateVisibleBridges("WidgetForegroundColorPicker", e.OriginalSource);
        }

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
        if (!DispatcherQueue.TryEnqueue(() =>
            {
                ApplyWidgetForegroundAppearance();
                OnWidgetForegroundChanged();
            }))
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
        // The drag handle renders over widget content without a plate behind
        // it, so a translucent tone shows the content through the bar. It
        // follows the full-opacity primary tone instead of the tertiary one.
        SetBrushColor(palette.Primary,
            "TextFillColorPrimaryBrush",
            "ControlStrongFillColorDefaultBrush",
            "ButtonForeground",
            "ButtonForegroundPointerOver",
            "SubtleButtonForeground",
            "SubtleButtonForegroundPointerOver",
            "WidgetDragHandleBrush");
        SetBrushColor(palette.Secondary,
            "TextFillColorSecondaryBrush",
            "ControlStrongStrokeColorDefaultBrush",
            "ButtonForegroundPressed",
            "SubtleButtonForegroundPressed");
        SetBrushColor(palette.Tertiary,
            "TextFillColorTertiaryBrush");
        SetBrushColor(palette.Disabled,
            "TextFillColorDisabledBrush",
            "ControlStrongFillColorDisabledBrush",
            "ControlStrongStrokeColorDisabledBrush",
            "ButtonForegroundDisabled",
            "SubtleButtonForegroundDisabled");
        SetBrushColor(palette.Divider, "DividerStrokeColorDefaultBrush");
        SetBrushColor(palette.OverlaySurface, "WidgetOverlaySurfaceBrush");
        SetBrushColor(palette.OverlayInputSurface, "WidgetOverlayInputSurfaceBrush");
        SetBrushColor(palette.OverlayStroke, "WidgetOverlayStrokeBrush");
        SetBrushColor(palette.SubtleButtonPointerOver, "WidgetSubtleButtonBackgroundPointerOver");
        SetBrushColor(palette.SubtleButtonPressed, "WidgetSubtleButtonBackgroundPressed");
        SetBrushColor(palette.ButtonBackground, "ButtonBackground");
        SetBrushColor(palette.ButtonBackgroundPointerOver, "ButtonBackgroundPointerOver");
        SetBrushColor(palette.ButtonBackgroundPressed, "ButtonBackgroundPressed");
        SetBrushColor(palette.ButtonBackgroundDisabled, "ButtonBackgroundDisabled");
        SetBrushColor(palette.ButtonBorder, "ButtonBorderBrush");
        SetBrushColor(palette.ButtonBorderPointerOver, "ButtonBorderBrushPointerOver");
        SetBrushColor(palette.ButtonBorderPressed, "ButtonBorderBrushPressed");
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

    internal static readonly Color DarkOverlaySurfaceColor = Color.FromArgb(0xFF, 0x2C, 0x2C, 0x2C);
    internal static readonly Color LightOverlaySurfaceColor = Color.FromArgb(0xFF, 0xF9, 0xF9, 0xF9);

    internal static double ComputeRelativeLuminance(Color color)
    {
        static double Linearize(byte channel)
        {
            double c = channel / 255.0;
            return c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        }

        return (0.2126 * Linearize(color.R)) +
               (0.7152 * Linearize(color.G)) +
               (0.0722 * Linearize(color.B));
    }

    internal static double ComputeContrastRatio(Color a, Color b)
    {
        double la = ComputeRelativeLuminance(a);
        double lb = ComputeRelativeLuminance(b);
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    internal static bool ShouldUseDarkOverlaySurface(Color foreground) =>
        ComputeContrastRatio(foreground, DarkOverlaySurfaceColor) >=
        ComputeContrastRatio(foreground, LightOverlaySurfaceColor);

    internal static WidgetForegroundPalette ResolveForegroundPalette(
        string mode,
        Color customColor,
        ElementTheme actualTheme,
        bool highContrast,
        Color? highContrastForeground = null,
        Color? highContrastBackground = null)
    {
        if (highContrast)
        {
            // Reuse the shared cached UISettings instance instead of a new
            // projection per palette resolve; TryGetUiColor already carries
            // the unavailability fallback contract.
            Color? foreground = highContrastForeground ??
                WindowsCompatibilityService.TryGetUiColor(UIColorType.Foreground);
            if (foreground is { } hcForeground)
            {
                Color hcBackground = highContrastBackground ??
                    WindowsCompatibilityService.TryGetUiColor(UIColorType.Background) ??
                    (actualTheme == ElementTheme.Light
                        ? Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF)
                        : Color.FromArgb(0xFF, 0x00, 0x00, 0x00));
                Color transparent = Color.FromArgb(0x00, 0x00, 0x00, 0x00);

                return new WidgetForegroundPalette(
                    Primary: hcForeground,
                    Secondary: hcForeground,
                    Tertiary: hcForeground,
                    Disabled: hcForeground,
                    Divider: hcForeground,
                    UseDarkOverlay: ComputeRelativeLuminance(hcBackground) < 0.5,
                    OverlaySurface: hcBackground,
                    OverlayInputSurface: hcBackground,
                    OverlayStroke: hcForeground,
                    SubtleButtonPointerOver: transparent,
                    SubtleButtonPressed: transparent,
                    ButtonBackground: hcBackground,
                    ButtonBackgroundPointerOver: hcBackground,
                    ButtonBackgroundPressed: hcBackground,
                    ButtonBackgroundDisabled: hcBackground,
                    ButtonBorder: hcForeground,
                    ButtonBorderPointerOver: hcForeground,
                    ButtonBorderPressed: hcForeground);
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

        bool useDarkOverlay = ShouldUseDarkOverlaySurface(primary);

        return useDarkOverlay
            ? new WidgetForegroundPalette(
                Primary: primary,
                Secondary: WithAlpha(primary, 0xE8),
                Tertiary: WithAlpha(primary, 0xCC),
                Disabled: WithAlpha(primary, 0x8F),
                Divider: WithAlpha(primary, 0x66),
                UseDarkOverlay: true,
                OverlaySurface: DarkOverlaySurfaceColor,
                OverlayInputSurface: Color.FromArgb(0xFF, 0x22, 0x28, 0x30),
                OverlayStroke: Color.FromArgb(0x38, 0xFF, 0xFF, 0xFF),
                SubtleButtonPointerOver: Color.FromArgb(0x18, 0xFF, 0xFF, 0xFF),
                SubtleButtonPressed: Color.FromArgb(0x10, 0xFF, 0xFF, 0xFF),
                ButtonBackground: Color.FromArgb(0x0F, 0xFF, 0xFF, 0xFF),
                ButtonBackgroundPointerOver: Color.FromArgb(0x18, 0xFF, 0xFF, 0xFF),
                ButtonBackgroundPressed: Color.FromArgb(0x10, 0xFF, 0xFF, 0xFF),
                ButtonBackgroundDisabled: Color.FromArgb(0x0B, 0xFF, 0xFF, 0xFF),
                ButtonBorder: Color.FromArgb(0x18, 0xFF, 0xFF, 0xFF),
                ButtonBorderPointerOver: Color.FromArgb(0x24, 0xFF, 0xFF, 0xFF),
                ButtonBorderPressed: Color.FromArgb(0x12, 0xFF, 0xFF, 0xFF))
            : new WidgetForegroundPalette(
                Primary: primary,
                Secondary: WithAlpha(primary, 0xE8),
                Tertiary: WithAlpha(primary, 0xCC),
                Disabled: WithAlpha(primary, 0x8F),
                Divider: WithAlpha(primary, 0x66),
                UseDarkOverlay: false,
                OverlaySurface: LightOverlaySurfaceColor,
                OverlayInputSurface: Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF),
                OverlayStroke: Color.FromArgb(0x24, 0x00, 0x00, 0x00),
                SubtleButtonPointerOver: Color.FromArgb(0x0D, 0x00, 0x00, 0x00),
                SubtleButtonPressed: Color.FromArgb(0x16, 0x00, 0x00, 0x00),
                ButtonBackground: Color.FromArgb(0xB3, 0xFF, 0xFF, 0xFF),
                ButtonBackgroundPointerOver: Color.FromArgb(0x80, 0xF9, 0xF9, 0xF9),
                ButtonBackgroundPressed: Color.FromArgb(0x4D, 0xF9, 0xF9, 0xF9),
                ButtonBackgroundDisabled: Color.FromArgb(0x4D, 0xF9, 0xF9, 0xF9),
                ButtonBorder: Color.FromArgb(0x1F, 0x00, 0x00, 0x00),
                ButtonBorderPointerOver: Color.FromArgb(0x29, 0x00, 0x00, 0x00),
                ButtonBorderPressed: Color.FromArgb(0x14, 0x00, 0x00, 0x00));
    }

    private static Color WithAlpha(Color color, byte alpha) =>
        Color.FromArgb(alpha, color.R, color.G, color.B);

    internal readonly record struct WidgetForegroundPalette(
        Color Primary,
        Color Secondary,
        Color Tertiary,
        Color Disabled,
        Color Divider,
        bool UseDarkOverlay,
        Color OverlaySurface,
        Color OverlayInputSurface,
        Color OverlayStroke,
        Color SubtleButtonPointerOver,
        Color SubtleButtonPressed,
        Color ButtonBackground,
        Color ButtonBackgroundPointerOver,
        Color ButtonBackgroundPressed,
        Color ButtonBackgroundDisabled,
        Color ButtonBorder,
        Color ButtonBorderPointerOver,
        Color ButtonBorderPressed);
}
