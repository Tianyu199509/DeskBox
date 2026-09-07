using Microsoft.UI.Xaml.Controls;

namespace DeskBox.Controls.WidgetContents;

// Extracted verbatim from the SettingsWindow.xaml inline MusicSettingsSection
// template (pluginization roadmap stage 2, first of the four inline-template
// extractions; Music is the per-kind store pilot). Bindings deliberately stay
// {Binding} against the inherited SettingsViewModel DataContext - switching to
// x:Bind would change the frozen bindable-property inventory. The namespace is
// DeskBox.Controls.WidgetContents per the roadmap 16.4 decision (feature
// namespace, not a host Views namespace); the file stays in
// Views/SettingsSections so the search-catalog generator keeps finding it.
public sealed partial class MusicSettingsSection : UserControl
{
    public MusicSettingsSection()
    {
        InitializeComponent();
    }
}
