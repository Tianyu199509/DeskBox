using DeskBox.Services;
using DeskBox.Services.Distribution;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DeskBox.Views;

/// <summary>
/// Save-failure notice surface (deep review R13, R12 debt ①) and dual-channel
/// coexistence banner (Direct vs. Microsoft Store): both render in the top
/// banner flow row above navigation content.
/// </summary>
public sealed partial class SettingsWindow
{
    private bool _channelCoexistenceBannerDismissed;
    private bool _channelCoexistenceProbeStarted;

    internal void ShowSaveFailedBanner()
    {
        SaveFailedInfoBar.Title = Services.Localized.T("Settings.Persistence.SaveFailedTitle");
        SaveFailedInfoBar.Message = Services.Localized.T("Settings.Persistence.SaveFailedBody");
        SaveFailedInfoBar.Visibility = Visibility.Visible;
        SaveFailedInfoBar.IsOpen = true;
    }

    internal void DismissSaveFailedBanner()
    {
        SaveFailedInfoBar.IsOpen = false;
        SaveFailedInfoBar.Visibility = Visibility.Collapsed;
    }

    private void SaveFailedInfoBar_Closed(InfoBar sender, InfoBarClosedEventArgs args)
    {
        SaveFailedInfoBar.Visibility = Visibility.Collapsed;
    }

    private void StartChannelCoexistenceBannerProbe()
    {
        if (_channelCoexistenceProbeStarted ||
            _channelCoexistenceBannerDismissed ||
            DeskBoxDataPathService.Current.IsDevelopmentRoot)
        {
            return;
        }

        _channelCoexistenceProbeStarted = true;
        bool isStoreChannel = AppDistributionService.Current.IsMicrosoftStore;
        _ = Task.Run(() => ChannelCoexistenceService.DetectOtherChannelInstalled(isStoreChannel))
            .ContinueWith(task =>
            {
                if (!task.IsCompletedSuccessfully || !task.Result.OtherChannelInstalled)
                {
                    return;
                }

                DispatcherQueue.TryEnqueue(() =>
                {
                    if (_isClosed || _channelCoexistenceBannerDismissed)
                    {
                        return;
                    }

                    ApplyChannelCoexistenceBannerText();
                    ChannelCoexistenceInfoBar.Visibility = Visibility.Visible;
                    ChannelCoexistenceInfoBar.IsOpen = true;
                });
            }, TaskScheduler.Default);
    }

    private void ApplyChannelCoexistenceBannerText()
    {
        ChannelCoexistenceInfoBar.Title = _localizationService.T("ChannelCoexistence.SettingsBanner.Title");
        ChannelCoexistenceInfoBar.Message = _localizationService.T("ChannelCoexistence.SettingsBanner.Body");
        ChannelCoexistenceActionButton.Content = _localizationService.T("ChannelCoexistence.Action.OpenInstalledApps");
    }

    private void ChannelCoexistenceInfoBar_Closed(InfoBar sender, InfoBarClosedEventArgs args)
    {
        _channelCoexistenceBannerDismissed = true;
        ChannelCoexistenceInfoBar.Visibility = Visibility.Collapsed;
    }

    private void ChannelCoexistenceActionButton_Click(object sender, RoutedEventArgs e)
    {
        ChannelCoexistenceService.TryOpenWindowsInstalledAppsSettings(App.Log);
    }
}
