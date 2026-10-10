using DeskBox.Helpers;
using DeskBox.Models;
using DeskBox.Platform;
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
        await ChangeManagedStoragePathAsync();
    }

    private async Task ChangeManagedStoragePathAsync()
    {
        while (true)
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

            // The desktop organizer sweeps the desktop on every run; a
            // storage root there would re-ingest its own managed folders,
            // so the choice is rejected before any migration is offered.
            if (ManagedStoragePathService.OverlapsDesktop(normalizedPath))
            {
                await ShowManagedStorageDesktopOverlapDialogAsync();
                continue;
            }

            if (App.Current.WidgetManager is not { } manager) return;
            var choice = await ManagedStorageMigrationDialog.ConfirmAsync(SettingsRoot.XamlRoot, _localizationService,
                manager.GetDefaultManagedStorageWidgetCount(), ViewModel.ManagedStorageRootPath, normalizedPath,
                token => manager.PreviewManagedStorageMigrationAsync(normalizedPath, token));
            if (choice is null) return;
            if (choice == StorageMigrationStartChoice.ChooseLocation)
            {
                continue;
            }
            var outcome = await ManagedStorageMigrationDialog.RunAsync(
                SettingsRoot.XamlRoot, SettingsRoot.DispatcherQueue, _localizationService,
                ViewModel.ManagedStorageRootPath, normalizedPath,
                options => manager.UpdateDefaultManagedStorageRootAsync(normalizedPath, options),
                restart: choice == StorageMigrationStartChoice.Restart);
            if (outcome.ChooseLocation)
            {
                continue;
            }
            ManagedStorageMigrationResult? result = outcome.Migration;
            if (result is not null)
            {
                ViewModel.UpdateManagedStorageRootPath(result.NewRootPath);
                try { await App.Current.ManagedStorageDesktopShortcutService.SyncAsync(result.OldRootPath); }
                catch (Exception ex) { App.Log($"[ManagedStorageMigration] Desktop shortcut sync deferred: {ex.Message}"); }
            }
            RefreshManagedStoragePathWarning();
            return;
        }
    }

    private async Task ShowManagedStorageDesktopOverlapDialogAsync()
    {
        if (SettingsRoot.XamlRoot is null)
        {
            return;
        }

        var dialog = new ContentDialog
        {
            XamlRoot = SettingsRoot.XamlRoot,
            Title = _localizationService.T("Settings.ManagedPath.Error.DesktopOverlapTitle"),
            CloseButtonText = _localizationService.T("Common.Close"),
            DefaultButton = ContentDialogButton.Close,
            Content = new TextBlock
            {
                Text = _localizationService.T("Settings.ManagedPath.Error.DesktopOverlapBody"),
                TextWrapping = TextWrapping.Wrap
            }
        };

        await dialog.ShowAsync();
    }

    private void RefreshManagedStoragePathWarning()
    {
        if (ManagedStoragePathWarningText is null)
        {
            return;
        }

        ManagedStoragePathAssessment assessment =
            ManagedStoragePathService.AssessPath(ViewModel.ManagedStorageRootPath);
        var warnings = new List<string>();
        if (ManagedStoragePathService.OverlapsDesktop(ViewModel.ManagedStorageRootPath))
        {
            warnings.Add(_localizationService.T("Settings.ManagedPath.Warning.DesktopOverlap"));
        }
        if (assessment.IsSystemDrive)
        {
            warnings.Add(_localizationService.T(assessment.HasSuitableNonSystemDrive
                ? "Settings.ManagedPath.Warning.SystemDrive"
                : "Settings.ManagedPath.Warning.SystemDriveOnly"));
        }
        if (assessment.IsCloudSynced)
        {
            warnings.Add(_localizationService.T("Settings.ManagedPath.Warning.CloudSync"));
        }
        if (assessment.DriveType == DriveType.Removable || assessment.IsTransientBusDrive)
        {
            warnings.Add(_localizationService.T("Settings.ManagedPath.Warning.Removable"));
        }
        else if (assessment.DriveType == DriveType.Network)
        {
            warnings.Add(_localizationService.T("Settings.ManagedPath.Warning.Network"));
        }

        ManagedStoragePathWarningText.Text = string.Join(Environment.NewLine, warnings);
        ManagedStoragePathWarningBorder.IsOpen = warnings.Count > 0;
    }

    private void RefreshDragOutWin10State()
    {
        // Windows 10 cannot offer a modifier switch to external targets: a
        // brokered drag there advertises a single effect only. The modifier
        // tip therefore never fires and its toggle is disabled; the note
        // under the drag-out combo explains the collapsed semantics.
        bool modifiersReachExternalTargets =
            WindowsCompatibilityService.IsWindows11OrLater;
        if (DragOutWin10Note is { } note)
        {
            note.Visibility = modifiersReachExternalTargets
                ? Visibility.Collapsed
                : Visibility.Visible;
        }
        if (DragOutModifierTipToggle is { } toggle)
        {
            toggle.IsEnabled = modifiersReachExternalTargets;
        }
    }

    private bool _isSynchronizingManagedStorageDesktopShortcutToggle;

    private void RefreshManagedStorageDesktopShortcutState()
    {
        if (ManagedStorageDesktopShortcutToggle is not ToggleSwitch toggle)
        {
            return;
        }

        SetManagedStorageDesktopShortcutToggleState(
            toggle,
            App.Current.ManagedStorageDesktopShortcutService.HasShortcut());
    }

    private async void ManagedStorageDesktopShortcutToggle_Toggled(
        object sender,
        RoutedEventArgs e)
    {
        if (_isSynchronizingManagedStorageDesktopShortcutToggle ||
            sender is not ToggleSwitch toggle)
        {
            return;
        }

        bool requestedState = toggle.IsOn;
        toggle.IsEnabled = false;
        try
        {
            ManagedStorageDesktopShortcutService shortcutService =
                App.Current.ManagedStorageDesktopShortcutService;
            bool succeeded = requestedState
                ? await shortcutService.CreateAsync()
                : await shortcutService.RemoveAsync();
            if (succeeded)
            {
                return;
            }

            SetManagedStorageDesktopShortcutToggleState(toggle, !requestedState);
            await ShowInfoDialogAsync(
                _localizationService.T("Settings.ManagedPath.DesktopShortcut.Title"),
                _localizationService.T("Common.OperationFailedRetry"));
        }
        finally
        {
            toggle.IsEnabled = true;
        }
    }

    private void SetManagedStorageDesktopShortcutToggleState(ToggleSwitch toggle, bool isOn)
    {
        _isSynchronizingManagedStorageDesktopShortcutToggle = true;
        try
        {
            toggle.IsOn = isOn;
        }
        finally
        {
            _isSynchronizingManagedStorageDesktopShortcutToggle = false;
        }
    }

    private void OpenManagedStoragePathButton_Click(object sender, RoutedEventArgs e)
    {
        string path = ViewModel.ManagedStorageRootPath;
        try
        {
            Directory.CreateDirectory(path);
            Win32Helper.OpenFile(path);
        }
        catch (Exception ex)
        {
            App.Log($"[SettingsWindow] Failed to open managed storage root '{path}': {ex.Message}");
            _ = ShowInfoDialogAsync(
                _localizationService.T("Settings.Dialog.OpenStorageFailedTitle"),
                _localizationService.Format("Settings.Dialog.OpenStorageFailedBody", ex.Message));
        }
    }

    private async void PinManagedStorageToQuickAccessButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_managedStorageSettingsViewModel.CanInvokeQuickAccessAction)
        {
            return;
        }

        string path = ViewModel.ManagedStorageRootPath;
        bool shouldUnpin = _managedStorageSettingsViewModel.ShouldUnpinQuickAccessAction;

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
                CloseButtonText = _localizationService.T("Common.Close"),
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
                    CloseButtonText = _localizationService.T("Common.Close"),
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
            // Raised from a download continuation, so it can land while the
            // user already has a dialog open on this window; the coordinator
            // retries briefly instead of crashing on the second ShowAsync.
            ContentDialogResult choice = await ShowDialogWhenFreeAsync(BuildDownloadFailureDialog);
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

    private ContentDialog BuildDownloadFailureDialog()
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

        return dialog;
    }

    private async Task<bool> CreatePreUpdateRecoverySnapshotAsync()
    {
        try
        {
            var result = await _backupCommands.CreateSnapshotNowAsync();
            if (_isClosed || _backupCommands.IsStopping) return false;
            string? snapshotPath = result.ArchivePath;
            if (!string.IsNullOrWhiteSpace(snapshotPath))
            {
                return true;
            }

            await ShowInfoDialogAsync(
                _localizationService.T("Settings.Update.PreUpdateBackupFailedTitle"),
                _localizationService.T("Settings.Update.PreUpdateBackupFailedBody"));
        }
        catch (OperationCanceledException) when (_backupCommands.IsStopping) { }
        catch (Exception ex)
        {
            App.Log($"[Update] Failed to create pre-update recovery snapshot: {ex}");
            if (_isClosed || _backupCommands.IsStopping) return false;
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

        // Queued from the updater process exit path, so it can land while a
        // user dialog is open; route through the same coordinator as the
        // download-failure retry dialog.
        ContentDialogResult choice = await ShowDialogWhenFreeAsync(() => new ContentDialog
        {
            XamlRoot = SettingsRoot.XamlRoot,
            Title = _localizationService.T(titleKey),
            Content = new TextBlock
            {
                Text = _localizationService.T(bodyKey),
                TextWrapping = TextWrapping.Wrap
            },
            PrimaryButtonText = ViewModel.UpdateFallbackActionText,
            CloseButtonText = _localizationService.T("Common.Close"),
            DefaultButton = ContentDialogButton.Close
        });

        if (choice == ContentDialogResult.Primary)
        {
            OpenManualUpdateDownload();
        }
    }

    private void ViewReleaseNotesButton_Click(object sender, RoutedEventArgs e)
    {
        // The button is always visible: when no check has produced a
        // manifest yet the window still opens and resolves its manifest
        // lazily inside itself.
        AppUpdateManifest? manifest = ViewModel.LatestUpdateManifest;
        if (_releaseNotesWindow is null)
        {
            var releaseNotesWindow = new ReleaseNotesWindow(
                manifest,
                ViewModel.AppVersion,
                _themeService,
                _localizationService,
                manifest is null ? ViewModel.FetchLatestManifestForReleaseNotesAsync : null);
            _releaseNotesWindow = releaseNotesWindow;
            releaseNotesWindow.Closed += (_, _) =>
            {
                if (ReferenceEquals(_releaseNotesWindow, releaseNotesWindow))
                {
                    _releaseNotesWindow = null;
                }
            };
        }
        else if (manifest is not null)
        {
            _releaseNotesWindow.UpdateManifest(manifest, ViewModel.AppVersion);
        }
        else
        {
            _releaseNotesWindow.RetryManifestLoad();
        }

        _releaseNotesWindow.ShowWindow(_hWnd);
    }
}
