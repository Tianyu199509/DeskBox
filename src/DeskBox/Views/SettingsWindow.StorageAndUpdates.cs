using DeskBox.Helpers;
using DeskBox.Models;
using DeskBox.Services;
using DeskBox.ViewModels;
using System.ComponentModel;
using Microsoft.UI;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Shapes;
using System.Runtime.InteropServices;
using Windows.System;
using WinRT.Interop;

namespace DeskBox.Views;

public sealed partial class SettingsWindow
{
    private async void ChangeManagedStoragePathButton_Click(object sender, RoutedEventArgs e)
    {
        if (SettingsRoot.XamlRoot is null)
        {
            return;
        }

        string? folderPath = await FolderPickerService.PickFolderAsync(_hWnd);
        if (string.IsNullOrWhiteSpace(folderPath))
        {
            return;
        }

        string normalizedPath = SettingsService.NormalizeManagedStorageRootPath(folderPath);
        if (string.Equals(normalizedPath, ViewModel.ManagedStorageRootPath, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        int affectedCount = App.Current.WidgetManager?.GetDefaultManagedStorageWidgetCount() ?? 0;
        if (affectedCount > 0)
        {
            var dialog = new ContentDialog
            {
                XamlRoot = SettingsRoot.XamlRoot,
                Title = _localizationService.T("Settings.Dialog.MigrateTitle"),
                PrimaryButtonText = _localizationService.T("Settings.Dialog.MigrateButton"),
                CloseButtonText = _localizationService.T("Common.Cancel"),
                DefaultButton = ContentDialogButton.Primary,
                Content = new TextBlock
                {
                    Text = _localizationService.Format(
                        "Settings.Dialog.MigrateBody",
                        affectedCount,
                        ViewModel.ManagedStorageRootPath,
                        normalizedPath),
                    TextWrapping = TextWrapping.Wrap
                }
            };

            if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            {
                return;
            }
        }

        if (App.Current.WidgetManager is not null)
        {
            try
            {
                var result = await App.Current.WidgetManager.UpdateDefaultManagedStorageRootAsync(normalizedPath);
                await ShowInfoDialogAsync(
                    _localizationService.T("Settings.Dialog.MigrateCompleteTitle"),
                    _localizationService.Format(
                        "Settings.Dialog.MigrateCompleteBody",
                        result.AffectedWidgetCount,
                        result.OldRootPath,
                        result.NewRootPath));
            }
            catch (Exception ex)
            {
                var errorDialog = new ContentDialog
                {
                    XamlRoot = SettingsRoot.XamlRoot,
                    Title = _localizationService.T("Settings.Dialog.MigrateFailedTitle"),
                    CloseButtonText = _localizationService.T("Common.Ok"),
                    DefaultButton = ContentDialogButton.Close,
                    Content = new TextBlock
                    {
                        Text = _localizationService.Format("Settings.Dialog.MigrateFailedBody", ex.Message),
                        TextWrapping = TextWrapping.Wrap
                    }
                };

                await errorDialog.ShowAsync();
                return;
            }
        }

        ViewModel.UpdateManagedStorageRootPath(normalizedPath);
        RefreshManagedStoragePathWarning();
    }

    private void RefreshManagedStoragePathWarning()
    {
        ManagedStoragePathAssessment assessment =
            ManagedStoragePathService.AssessPath(ViewModel.ManagedStorageRootPath);
        var warnings = new List<string>();
        if (assessment.IsSystemDrive)
        {
            warnings.Add(_localizationService.T(assessment.HasSuitableNonSystemDrive
                ? "Onboarding.Task.Step2.Warning.SystemDrive"
                : "Onboarding.Task.Step2.Warning.SystemDriveOnly"));
        }
        if (assessment.IsCloudSynced)
        {
            warnings.Add(_localizationService.T("Onboarding.Task.Step2.Warning.CloudSync"));
        }
        if (assessment.DriveType == DriveType.Removable)
        {
            warnings.Add(_localizationService.T("Onboarding.Task.Step2.Warning.Removable"));
        }
        else if (assessment.DriveType == DriveType.Network)
        {
            warnings.Add(_localizationService.T("Onboarding.Task.Step2.Warning.Network"));
        }

        ManagedStoragePathWarningText.Text = string.Join(Environment.NewLine, warnings);
        ManagedStoragePathWarningBorder.Visibility = warnings.Count > 0
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void OpenManagedStoragePathButton_Click(object sender, RoutedEventArgs e)
    {
        string path = ViewModel.ManagedStorageRootPath;
        Directory.CreateDirectory(path);
        Win32Helper.OpenFile(path);
    }

    private async void PinManagedStorageToQuickAccessButton_Click(object sender, RoutedEventArgs e)
    {
        if (!ViewModel.CanInvokeQuickAccessAction)
        {
            return;
        }

        string path = ViewModel.ManagedStorageRootPath;
        bool shouldUnpin = ViewModel.ShouldUnpinManagedStorageFromQuickAccess;

        ViewModel.SetQuickAccessBusy(true);
        try
        {
            QuickAccessOperationResult result = shouldUnpin
                ? await ExplorerQuickAccessHelper.TryUnpinFolderFromQuickAccessAsync(path)
                : await ExplorerQuickAccessHelper.TryPinFolderToQuickAccessAsync(path);

            if (result.Succeeded)
            {
                ViewModel.SetQuickAccessPinState(shouldUnpin ? QuickAccessPinState.NotPinned : QuickAccessPinState.Pinned);
                await Task.Delay(500);
                await ViewModel.RefreshQuickAccessStateAsync();
                return;
            }

            App.Log($"[SettingsWindow] Quick Access operation failed: {result.Error ?? "unknown error"}");
            if (SettingsRoot.XamlRoot is null)
            {
                return;
            }

            var dialog = new ContentDialog
            {
                XamlRoot = SettingsRoot.XamlRoot,
                Title = shouldUnpin
                    ? _localizationService.T("Settings.Dialog.UnpinQuickAccessFailedTitle")
                    : _localizationService.T("Settings.Dialog.PinQuickAccessFailedTitle"),
                CloseButtonText = _localizationService.T("Common.Ok"),
                DefaultButton = ContentDialogButton.Close,
                Content = new TextBlock
                {
                    Text = _localizationService.T("Settings.Dialog.QuickAccessOperationFailedBody"),
                    TextWrapping = TextWrapping.Wrap
                }
            };

            await dialog.ShowAsync();
        }
        catch (Exception ex)
        {
            App.Log($"[SettingsWindow] Failed to update Quick Access pin state: {ex}");
            if (SettingsRoot.XamlRoot is not null)
            {
                var dialog = new ContentDialog
                {
                    XamlRoot = SettingsRoot.XamlRoot,
                    Title = shouldUnpin
                        ? _localizationService.T("Settings.Dialog.UnpinQuickAccessFailedTitle")
                        : _localizationService.T("Settings.Dialog.PinQuickAccessFailedTitle"),
                    CloseButtonText = _localizationService.T("Common.Ok"),
                    DefaultButton = ContentDialogButton.Close,
                    Content = new TextBlock
                    {
                        Text = _localizationService.T("Settings.Dialog.QuickAccessOperationFailedBody"),
                        TextWrapping = TextWrapping.Wrap
                    }
                };

                await dialog.ShowAsync();
            }
        }
        finally
        {
            ViewModel.SetQuickAccessBusy(false);
        }
    }

    private void OpenWebsiteButton_Click(object sender, RoutedEventArgs e)
    {
        Win32Helper.OpenFile(ViewModel.OfficialWebsiteLink);
    }

    private async void ShowStoreSupportDialogButton_Click(object sender, RoutedEventArgs e)
    {
        if (SettingsRoot.XamlRoot is null ||
            ViewModel.StoreSupportCardVisibility != Visibility.Visible)
        {
            return;
        }

        SupportDeskBoxDialog.XamlRoot = SettingsRoot.XamlRoot;
        SupportDeskBoxDialog.Title = _localizationService.T("Settings.About.StoreSupportTitle");
        SupportDeskBoxDialog.CloseButtonText = _localizationService.T("Settings.Dialog.SupportClose");
        await SupportDeskBoxDialog.ShowAsync();
    }

    private async void OpenMicrosoftStoreButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            bool launched = await Launcher.LaunchUriAsync(new Uri(ViewModel.MicrosoftStoreAppLink));
            if (launched)
            {
                return;
            }
        }
        catch (Exception ex)
        {
            App.Log($"[SettingsWindow] Failed to launch Microsoft Store app: {ex}");
        }

        Win32Helper.OpenFile(ViewModel.MicrosoftStoreLink);
    }

    private async void OpenFeedbackEmailButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            bool launched = await Launcher.LaunchUriAsync(new Uri(ViewModel.FeedbackEmailLink));
            if (!launched)
            {
                App.Log($"[SettingsWindow] No email handler accepted '{ViewModel.FeedbackEmailLink}'.");
            }
        }
        catch (Exception ex)
        {
            App.Log($"[SettingsWindow] Failed to open feedback email link: {ex.Message}");
        }
    }

    private async void OneClickUpdateButton_Click(object sender, RoutedEventArgs e)
    {
        // If update is already downloaded, show install confirmation dialog.
        if (ViewModel.IsUpdateDownloaded)
        {
            if (SettingsRoot.XamlRoot is null) return;

            var dialog = new ContentDialog
            {
                XamlRoot = SettingsRoot.XamlRoot,
                Title = _localizationService.T("Settings.Update.InstallConfirmTitle"),
                Content = new TextBlock
                {
                    Text = _localizationService.T("Settings.Update.InstallConfirmBody"),
                    TextWrapping = TextWrapping.Wrap
                },
                PrimaryButtonText = _localizationService.T("Settings.Update.OneClick.Install"),
                CloseButtonText = _localizationService.T("Common.Cancel"),
                DefaultButton = ContentDialogButton.Primary
            };

            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

            if (!await CreatePreUpdateRecoverySnapshotAsync())
            {
                return;
            }

            var result = ViewModel.StartDownloadedUpdateInstall();
            if (!result.Success)
            {
                await ShowInfoDialogAsync(
                    _localizationService.T("Settings.Update.InstallStartFailedTitle"),
                    result.ErrorMessage ?? _localizationService.T("Settings.Update.InstallStartFailedBody"));
                return;
            }

            await App.Current.ShutdownForUpdateAsync();
            return;
        }

        // Otherwise, trigger one-click check → download flow.
        AppUpdateDownloadResult? downloadResult = await ViewModel.OneClickUpdateActionAsync();
        if (downloadResult is { Success: false, FailureKind: not AppUpdateDownloadFailureKind.Cancelled })
        {
            await ShowDownloadFailureDialogAsync(downloadResult);
        }
    }

    private async Task ShowDownloadFailureDialogAsync(AppUpdateDownloadResult initialResult)
    {
        AppUpdateDownloadResult? result = initialResult;
        while (result is { Success: false, FailureKind: not AppUpdateDownloadFailureKind.Cancelled } &&
               SettingsRoot.XamlRoot is not null)
        {
            var dialog = new ContentDialog
            {
                XamlRoot = SettingsRoot.XamlRoot,
                Title = _localizationService.T("Settings.Update.DownloadFailedTitle"),
                Content = new TextBlock
                {
                    Text = _localizationService.Format(
                        "Settings.Update.DownloadFailedBody",
                        ViewModel.UpdateDetailText),
                    TextWrapping = TextWrapping.Wrap
                },
                PrimaryButtonText = _localizationService.T("Settings.Update.OneClick.Retry"),
                CloseButtonText = _localizationService.T("Common.Cancel"),
                DefaultButton = ContentDialogButton.Primary
            };

            if (ViewModel.CanOpenManualUpdateDownload)
            {
                dialog.SecondaryButtonText = ViewModel.UpdateFallbackActionText;
            }

            ContentDialogResult choice = await dialog.ShowAsync();
            if (choice == ContentDialogResult.Secondary)
            {
                OpenManualUpdateDownload();
                return;
            }

            if (choice != ContentDialogResult.Primary)
            {
                return;
            }

            result = await ViewModel.DownloadAvailableUpdateAsync();
        }
    }

    private async Task<bool> CreatePreUpdateRecoverySnapshotAsync()
    {
        try
        {
            await App.Current.SettingsService.SaveAsync(notifySubscribers: false);
            string? snapshotPath = await App.Current.DataBackupService.CreateAutomaticSnapshotNowAsync();
            if (!string.IsNullOrWhiteSpace(snapshotPath))
            {
                return true;
            }

            await ShowInfoDialogAsync(
                _localizationService.T("Settings.Update.PreUpdateBackupFailedTitle"),
                _localizationService.T("Settings.Update.PreUpdateBackupFailedBody"));
        }
        catch (Exception ex)
        {
            App.Log($"[Update] Failed to create pre-update recovery snapshot: {ex}");
            await ShowInfoDialogAsync(
                _localizationService.T("Settings.Update.PreUpdateBackupFailedTitle"),
                _localizationService.Format("Settings.Update.PreUpdateBackupFailedBodyWithError", ex.Message));
        }

        return false;
    }

    private void OpenManualUpdateDownloadButton_Click(object sender, RoutedEventArgs e)
    {
        OpenManualUpdateDownload();
    }

    private void OpenManualUpdateDownload()
    {
        string url = ViewModel.ManualUpdateDownloadUrl;
        if (AppUpdateManifest.IsSafeWebUrl(url))
        {
            Win32Helper.OpenFile(url);
        }
    }

    public void QueueUpdateInstallResultDialog(string outcome)
    {
        if (SettingsRoot.XamlRoot is not null)
        {
            _ = ShowUpdateInstallResultDialogAsync(outcome);
            return;
        }

        RoutedEventHandler? loadedHandler = null;
        loadedHandler = (_, _) =>
        {
            SettingsRoot.Loaded -= loadedHandler;
            _ = ShowUpdateInstallResultDialogAsync(outcome);
        };
        SettingsRoot.Loaded += loadedHandler;
    }

    private async Task ShowUpdateInstallResultDialogAsync(string outcome)
    {
        if (SettingsRoot.XamlRoot is null)
        {
            return;
        }

        bool wasCancelled = string.Equals(outcome, "cancelled", StringComparison.OrdinalIgnoreCase);
        bool pathMismatch = string.Equals(outcome, "path-mismatch", StringComparison.OrdinalIgnoreCase);
        string titleKey = wasCancelled
            ? "Settings.Update.InstallCancelledTitle"
            : "Settings.Update.InstallFailedTitle";
        string bodyKey = wasCancelled
            ? "Settings.Update.InstallCancelledBody"
            : pathMismatch
                ? "Settings.Update.InstallPathMismatchBody"
                : "Settings.Update.InstallFailedBody";

        var dialog = new ContentDialog
        {
            XamlRoot = SettingsRoot.XamlRoot,
            Title = _localizationService.T(titleKey),
            Content = new TextBlock
            {
                Text = _localizationService.T(bodyKey),
                TextWrapping = TextWrapping.Wrap
            },
            PrimaryButtonText = ViewModel.UpdateFallbackActionText,
            CloseButtonText = _localizationService.T("Common.Ok"),
            DefaultButton = ContentDialogButton.Close
        };

        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            OpenManualUpdateDownload();
        }
    }

    private void ViewReleaseNotesButton_Click(object sender, RoutedEventArgs e)
    {
        if (!ViewModel.CanViewReleaseNotes || ViewModel.LatestUpdateManifest is not { } manifest)
        {
            return;
        }

        if (_releaseNotesWindow is null)
        {
            var releaseNotesWindow = new ReleaseNotesWindow(
                manifest,
                ViewModel.AppVersion,
                _themeService,
                _localizationService);
            _releaseNotesWindow = releaseNotesWindow;
            releaseNotesWindow.Closed += (_, _) =>
            {
                if (ReferenceEquals(_releaseNotesWindow, releaseNotesWindow))
                {
                    _releaseNotesWindow = null;
                }
            };
        }
        else
        {
            _releaseNotesWindow.UpdateManifest(manifest, ViewModel.AppVersion);
        }

        _releaseNotesWindow.ShowWindow(_hWnd);
    }
}
