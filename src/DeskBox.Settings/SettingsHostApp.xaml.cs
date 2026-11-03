using Microsoft.UI.Xaml;

namespace DeskBox;

public sealed partial class SettingsHostApp : global::DeskBox.App
{
    public SettingsHostApp()
        : base(isExternalSettingsHost: true)
    {
        SettingsControlsAdapter.EnsureRegistered();
        InitializeSettingsHost();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        LaunchSettingsHostAsync();
    }
}
