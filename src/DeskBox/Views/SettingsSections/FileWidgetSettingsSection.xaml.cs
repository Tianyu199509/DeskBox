using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DeskBox.Views.SettingsSections;

public sealed partial class FileWidgetSettingsSection : UserControl
{
    public FileWidgetSettingsSection()
    {
        InitializeComponent();
    }

    public event EventHandler<SettingsSectionNavigationRequestedEventArgs>? NavigationRequested;

    public event RoutedEventHandler? OpenManagedStorageRequested;

    private void NestedSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string sectionTag })
        {
            NavigationRequested?.Invoke(this, new SettingsSectionNavigationRequestedEventArgs(sectionTag));
        }
    }

    private void OpenManagedStoragePathButton_Click(object sender, RoutedEventArgs e)
    {
        OpenManagedStorageRequested?.Invoke(this, e);
    }
}
