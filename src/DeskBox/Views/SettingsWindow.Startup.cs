using DeskBox.Services;
using Microsoft.UI.Xaml;
using Windows.System;

namespace DeskBox.Views;

public sealed partial class SettingsWindow
{
    private void SettingsWindow_Activated(
        object sender,
        WindowActivatedEventArgs args)
    {
        if (args.WindowActivationState == WindowActivationState.Deactivated)
        {
            return;
        }

        ViewModel.RefreshAutoStartState();
        RefreshSystemAppearanceStates();
    }

    private async void OpenStartupAppsSettingsButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        await OpenStartupAppsSettingsAsync();
    }

    private async Task<bool> OpenStartupAppsSettingsAsync()
    {
        try
        {
            bool launched = await Launcher.LaunchUriAsync(
                new Uri("ms-settings:startupapps"));
            if (!launched)
            {
                App.Log("[Startup] Windows Startup apps settings could not be opened.");
            }

            return launched;
        }
        catch (Exception ex)
        {
            App.Log($"[Startup] Failed to open Windows Startup apps settings: {ex}");
            return false;
        }
    }
}
