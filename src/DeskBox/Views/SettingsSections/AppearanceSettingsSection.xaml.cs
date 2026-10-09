using DeskBox.Helpers;
using DeskBox.Services;
using DeskBox.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DeskBox.Views.SettingsSections;

public sealed partial class AppearanceSettingsSection : UserControl
{
    private LocalizationService Localization => App.Current.LocalizationService;

    public AppearanceSettingsSection()
    {
        InitializeComponent();
    }

    public event EventHandler<SettingsSectionNavigationRequestedEventArgs>? NavigationRequested;

    private void NestedSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        // Drill-down rows are Buttons, but the 格子组-style entry cards are
        // toolkit SettingsCards (ButtonBase, not Button); match the shell's
        // FrameworkElement guard so both navigate.
        if (sender is FrameworkElement { Tag: string sectionTag })
        {
            NavigationRequested?.Invoke(this, new SettingsSectionNavigationRequestedEventArgs(sectionTag));
        }
    }

    private void AccentPresetButton_Click(object sender, RoutedEventArgs e)
    {
        // The section's DataContext is the appearance editor; the custom
        // accent write stays on the settings shell (theme service state), so
        // the preset pick surfaces as an editor event the shell handles.
        if (DataContext is not DeskBox.Features.Appearance.AppearanceSettingsViewModel editor ||
            sender is not Button { Tag: string hex } ||
            !AccentColorHelper.TryParseHex(hex, out var color))
        {
            return;
        }

        editor.NotifyAccentPresetPicked(AccentColorHelper.ToHex(color));
    }
}
