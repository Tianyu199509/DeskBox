using CommunityToolkit.WinUI.Animations;
using DeskBox.Helpers;
using DeskBox.Models;
using DeskBox.Services;
using Microsoft.UI;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Shapes;
using Windows.UI;
using WinRT.Interop;

namespace DeskBox.Views;

public sealed partial class OnboardingWindow
{
    private void SetupStep3()
    {
        BuildThemeSelector();
        BuildAccentSelector();
        BuildMaterialSelector();
        UpdateAppearancePreview();

        // Capsule mode toggle
        Step3CapsuleToggle.Toggled -= Step3CapsuleToggle_Toggled;
        Step3CapsuleToggle.IsOn = _settingsService.Settings.WidgetCapsuleModeEnabled;
        Step3CapsuleToggle.Toggled += Step3CapsuleToggle_Toggled;
    }

    private void BuildThemeSelector()
    {
        Step3ThemeSelector.Children.Clear();
        string[] themeKeys = { "System", "Light", "Dark" };
        string[] themeLabelKeys = { "Onboarding.Step3.ThemeSystem", "Onboarding.Step3.ThemeLight", "Onboarding.Step3.ThemeDark" };
        string currentTheme = _settingsService.Settings.Theme;

        for (int i = 0; i < themeKeys.Length; i++)
        {
            string key = themeKeys[i];
            var rb = new RadioButton
            {
                Content = _localizationService.T(themeLabelKeys[i]),
                IsChecked = string.Equals(currentTheme, key, StringComparison.OrdinalIgnoreCase),
                MinWidth = 0,
                Padding = new Thickness(10, 4, 10, 4)
            };
            string capturedKey = key;
            rb.Checked += (_, _) =>
            {
                if (App.Current.ThemeService is { } ts)
                {
                    ts.SetTheme(capturedKey);
                }
                else
                {
                    _settingsService.Settings.Theme = capturedKey;
                    _settingsService.SaveDebounced();
                }
                UpdateAppearancePreview();
            };
            Step3ThemeSelector.Children.Add(rb);
        }
    }

    private void BuildAccentSelector()
    {
        Step3AccentSelector.Children.Clear();
        bool useSystemAccent = !string.Equals(_settingsService.Settings.AccentColorMode, ThemeService.AccentModeCustom, StringComparison.OrdinalIgnoreCase);

        var systemAccentRb = new RadioButton
        {
            Content = _localizationService.T("Onboarding.Step3.UseSystemAccent"),
            IsChecked = useSystemAccent,
            MinWidth = 0,
            Padding = new Thickness(10, 4, 10, 4)
        };
        systemAccentRb.Checked += (_, _) =>
        {
            if (App.Current.ThemeService is { } ts)
            {
                ts.SetAccentMode(ThemeService.AccentModeSystem);
            }
            else
            {
                _settingsService.Settings.AccentColorMode = ThemeService.AccentModeSystem;
                _settingsService.SaveDebounced();
            }
            UpdateAppearancePreview();
        };
        Step3AccentSelector.Children.Add(systemAccentRb);

        foreach (string colorHex in PresetAccentColors)
        {
            var color = ColorHelper.FromArgb(0xFF,
                Convert.ToByte(colorHex.Substring(1, 2), 16),
                Convert.ToByte(colorHex.Substring(3, 2), 16),
                Convert.ToByte(colorHex.Substring(5, 2), 16));

            var colorBtn = new Button
            {
                Width = 28,
                Height = 28,
                Padding = new Thickness(0),
                MinWidth = 0,
                MinHeight = 0,
                CornerRadius = new CornerRadius(6),
                Background = new SolidColorBrush(color),
                BorderBrush = new SolidColorBrush(Colors.Transparent),
                Content = null
            };
            string captured = colorHex;
            colorBtn.Click += (_, _) =>
            {
                if (App.Current.ThemeService is { } ts)
                {
                    ts.SetCustomAccentColor(color);
                }
                else
                {
                    _settingsService.Settings.AccentColorMode = ThemeService.AccentModeCustom;
                    _settingsService.Settings.CustomAccentColor = captured;
                    _settingsService.SaveDebounced();
                }
                systemAccentRb.IsChecked = false;
                UpdateAppearancePreview();
            };
            Step3AccentSelector.Children.Add(colorBtn);
        }
    }

    private void BuildMaterialSelector()
    {
        Step3MaterialSelector.Children.Clear();
        string[] materialKeys = { "Mica", "Acrylic", "Solid" };
        string[] materialLabelKeys = { "Onboarding.Step3.MaterialMica", "Onboarding.Step3.MaterialAcrylic", "Onboarding.Step3.MaterialSolid" };
        string currentMaterial = _settingsService.Settings.WidgetMaterialType;

        for (int i = 0; i < materialKeys.Length; i++)
        {
            string key = materialKeys[i];
            var rb = new RadioButton
            {
                Content = _localizationService.T(materialLabelKeys[i]),
                IsChecked = string.Equals(currentMaterial, key, StringComparison.OrdinalIgnoreCase),
                MinWidth = 0,
                Padding = new Thickness(10, 4, 10, 4)
            };
            string capturedKey = key;
            rb.Checked += (_, _) =>
            {
                _settingsService.Settings.WidgetMaterialType = capturedKey;
                _settingsService.SaveDebounced();
                if (App.Current.ThemeService is { } ts)
                {
                    ts.RefreshAppearance();
                }
                UpdateAppearancePreview();
            };
            Step3MaterialSelector.Children.Add(rb);
        }
    }

    private void UpdateAppearancePreview()
    {
        // Update preview background based on material
        string material = _settingsService.Settings.WidgetMaterialType;
        if (Step3PreviewBackground is SolidColorBrush brush)
        {
            brush.Color = material switch
            {
                "Acrylic" => IsDarkTheme()
                    ? ColorHelper.FromArgb(0xCC, 0x2C, 0x2C, 0x2C)
                    : ColorHelper.FromArgb(0xCC, 0xF4, 0xF4, 0xF4),
                "Solid" => IsDarkTheme()
                    ? ColorHelper.FromArgb(0xFF, 0x20, 0x20, 0x20)
                    : ColorHelper.FromArgb(0xFF, 0xF8, 0xF8, 0xF8),
                _ => IsDarkTheme() // Mica
                    ? ColorHelper.FromArgb(0x80, 0x1C, 0x1C, 0x1C)
                    : ColorHelper.FromArgb(0x80, 0xFA, 0xFA, 0xFA)
            };
        }

        // Update accent-colored elements
        var accentColor = App.Current.ThemeService?.GetEffectiveAccentColor()
            ?? AccentColorHelper.DefaultAccentColor;
        if (Step3PreviewIcon?.Background is SolidColorBrush iconBrush)
        {
            iconBrush.Color = accentColor;
        }
        if (Step3PreviewItem1?.Background is SolidColorBrush itemBrush)
        {
            itemBrush.Color = accentColor;
        }
    }

    // ════════════════════════════════════════════════════════════
    //  Step 4: Feature Widgets
    // ════════════════════════════════════════════════════════════
}
