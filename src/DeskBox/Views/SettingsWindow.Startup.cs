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
        bool isActive = args.WindowActivationState != WindowActivationState.Deactivated;
        global::DeskBox.Views.Dialogs.HotkeyRecorderDialog.NotifyHostWindowActivationChanged(isActive);
        if (!isActive)
        {
            return;
        }

        ViewModel.RefreshAutoStartState();
        RefreshSystemAppearanceStates();
        CustomWidgetAiCredentialStore.Shared.CheckForExternalChangesAndNotify();
        RefreshAiSettingsEntrySummary();
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
