using DeskBox.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DeskBox.Views.SettingsSections;

public sealed partial class CapsuleModeSettingsSection : UserControl
{
    public static readonly DependencyProperty ViewModelProperty =
        DependencyProperty.Register(
            nameof(ViewModel),
            typeof(SettingsViewModel),
            typeof(CapsuleModeSettingsSection),
            new PropertyMetadata(null));

    public CapsuleModeSettingsSection()
    {
        InitializeComponent();
    }

    public SettingsViewModel? ViewModel
    {
        get => (SettingsViewModel?)GetValue(ViewModelProperty);
        set => SetValue(ViewModelProperty, value);
    }

    public event EventHandler<SettingsSectionNavigationRequestedEventArgs>? NavigationRequested;

    private void NestedSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        // Keep this guard aligned with the shell's FrameworkElement match so
        // toolkit SettingsCard entries (ButtonBase, not Button) navigate too.
        if (sender is FrameworkElement { Tag: string sectionTag })
        {
            NavigationRequested?.Invoke(this, new SettingsSectionNavigationRequestedEventArgs(sectionTag));
        }
    }
}
