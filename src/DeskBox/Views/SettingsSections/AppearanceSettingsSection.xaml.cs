using DeskBox.Helpers;
using DeskBox.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DeskBox.Views.SettingsSections;

public sealed partial class AppearanceSettingsSection : UserControl
{
    public AppearanceSettingsSection()
    {
        InitializeComponent();
    }

    public event EventHandler<SettingsSectionNavigationRequestedEventArgs>? NavigationRequested;

    private void NestedSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string sectionTag })
        {
            NavigationRequested?.Invoke(this, new SettingsSectionNavigationRequestedEventArgs(sectionTag));
        }
    }

    private void AccentPresetButton_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not SettingsViewModel viewModel ||
            sender is not Button { Tag: string hex } ||
            !AccentColorHelper.TryParseHex(hex, out var color))
        {
            return;
        }

        viewModel.SetCustomAccentColor(color);
    }
}
