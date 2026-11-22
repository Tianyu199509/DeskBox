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
    private bool _isStorageMigrationFlowOpen;
    private bool _isStorageMigrationRunning;

    private async Task ChangeManagedStoragePathAsync()
    {
        if (_isStorageMigrationFlowOpen)
        {
            return;
        }

        _isStorageMigrationFlowOpen = true;
        try
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

                string scopedPath = ManagedStoragePathService.ResolvePickedStorageRootPath(
                    folderPath,
                    ManagedStorageMigrationService.TemporaryFolderName);
                string normalizedPath = SettingsService.NormalizeManagedStorageRootPath(scopedPath);
                if (string.Equals(normalizedPath, ViewModel.ManagedStorageRootPath, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                // The desktop organizer sweeps the desktop on every run; a
                // storage root there (or containing Desktop) would re-ingest its
                // own managed folders, so the choice is rejected before any
                // migration is offered.
                if (ManagedStoragePathService.OverlapsDesktop(normalizedPath))
                {
                    await ShowManagedStorageDesktopOverlapDialogAsync();
                    continue;
                }

                if (!_widgetActions.IsHostAvailable) return;

                // Fast path: 0 files and no kept orphan folders → instant switch + concise notice.
                try
                {
                    ManagedStorageMigrationResult? emptySwitch =
                        await _widgetActions.TrySwitchEmptyManagedStorageRootAsync(normalizedPath, CancellationToken.None);
                    if (emptySwitch is not null)
                    {
                        ViewModel.UpdateManagedStorageRootPath(emptySwitch.NewRootPath);
                        RefreshManagedStoragePathWarning();
                        _ = RefreshQuickAccessAndOrphansAfterMigrationAsync();
                        if (SettingsRoot.XamlRoot is not null)
                        {
                            await ManagedStorageMigrationDialog.ShowEmptySwitchSuccessAsync(
                                SettingsRoot.XamlRoot, _localizationService, emptySwitch.NewRootPath);
                        }
                        return;
                    }
                }
                catch (Exception ex)
                {
                    // Let ConfirmAsync render the structured preview error card & exact blocking path.
                    App.Log($"[ManagedStorageMigration] Empty-switch precheck fell through to confirm dialog: {ex.Message}");
                }

                var confirm = await ManagedStorageMigrationDialog.ConfirmAsync(
                    SettingsRoot.XamlRoot,
                    _localizationService,
                    _widgetActions.GetDefaultManagedStorageWidgetCount(),
                    ViewModel.ManagedStorageRootPath,
                    normalizedPath,
                    token => _widgetActions.PreviewManagedStorageMigrationAsync(normalizedPath, token),
                    token => _widgetActions.CleanupInterruptedMigrationCopiesAsync(normalizedPath, token));
                if (confirm is null) return;
                if (confirm.Choice == StorageMigrationStartChoice.ChooseLocation)
                {
                    continue;
                }

                var outcome = await ManagedStorageMigrationDialog.RunAsync(
                    SettingsRoot.XamlRoot,
                    SettingsRoot.DispatcherQueue,
                    _localizationService,
                    ViewModel.ManagedStorageRootPath,
                    normalizedPath,
                    options => _widgetActions.UpdateDefaultManagedStorageRootAsync(normalizedPath, options),
                    restart: confirm.Choice == StorageMigrationStartChoice.Restart,
                    retainOriginals: confirm.RetainOriginals,
                    includeOrphanFolders: confirm.IncludeOrphanFolders,
                    retrySourceCleanup: token => _widgetActions.RetryManagedStorageSourceCleanupAsync(normalizedPath, token),
                    cleanupInterruptedCopies: token => _widgetActions.CleanupInterruptedMigrationCopiesAsync(normalizedPath, token),
                    onRunningStateChanged: running => _isStorageMigrationRunning = running);
                if (outcome.ChooseLocation)
                {
                    continue;
                }

                ManagedStorageMigrationResult? result = outcome.Migration;
                if (result is not null)
                {
                    ViewModel.UpdateManagedStorageRootPath(result.NewRootPath);
                }

                RefreshManagedStoragePathWarning();
                _ = RefreshQuickAccessAndOrphansAfterMigrationAsync();
                return;
            }
        }
        finally
        {
            _isStorageMigrationRunning = false;
            _isStorageMigrationFlowOpen = false;
        }
    }

    private async Task RefreshQuickAccessAndOrphansAfterMigrationAsync()
    {
        try
        {
            await ViewModel.RefreshQuickAccessStateAsync();
        }
        catch (Exception ex)
        {
            App.Log($"[ManagedStorageMigration] Quick Access state refresh deferred: {ex.Message}");
        }

        SyncManagedStorageQuickAccessToggleState();
        RefreshManagedStorageDesktopShortcutState();
        await RefreshOrphanFoldersSummaryAsync();
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
            RequestedTheme = NeutralInteractionBrush.ResolveEffectiveElementTheme(SettingsRoot),
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

    private void ChangeManagedStoragePathButton_Click(object? sender, RoutedEventArgs e)
    {
        _ = ChangeManagedStoragePathAsync();
    }

    private void RefreshManagedStoragePathWarning()
    {
        if (!_settingsSectionElements.TryGetValue("AppearanceDetail", out FrameworkElement? el) ||
            el is not Views.SettingsSections.FileWidgetSettingsSection section)
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

        section.SetStoragePathWarning(
            warnings.Count > 0,
            InfoBarSeverity.Warning,
            null,
            string.Join(Environment.NewLine, warnings));
    }

    private void RefreshManagedStorageDesktopShortcutState()
    {
        if (!_settingsSectionElements.TryGetValue("AppearanceDetail", out FrameworkElement? el) ||
            el is not Views.SettingsSections.FileWidgetSettingsSection section)
        {
            return;
        }

        section.SetDesktopShortcutState(_appActions.HasManagedStorageDesktopShortcut());
    }

    private async void AppearanceDetailSection_DesktopShortcutToggled(
        object? sender,
        bool requestedState)
    {
        if (!_settingsSectionElements.TryGetValue("AppearanceDetail", out FrameworkElement? el) ||
            el is not Views.SettingsSections.FileWidgetSettingsSection section)
        {
            return;
        }

        section.SetDesktopShortcutState(requestedState, isEnabled: false);
        try
        {
            bool succeeded = requestedState
                ? await _appActions.CreateManagedStorageDesktopShortcutAsync()
                : await _appActions.RemoveManagedStorageDesktopShortcutAsync();
            if (succeeded)
            {
                section.SetDesktopShortcutState(requestedState, isEnabled: true);
                return;
            }

            section.SetDesktopShortcutState(!requestedState, isEnabled: true);
            await ShowInfoDialogAsync(
                _localizationService.T("Settings.ManagedPath.DesktopShortcut.Title"),
                _localizationService.T("Common.OperationFailedRetry"));
        }
        catch
        {
            section.SetDesktopShortcutState(!requestedState, isEnabled: true);
            throw;
        }
    }

    private void ManagedStorageSettingsViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (string.IsNullOrEmpty(e.PropertyName) ||
            e.PropertyName == nameof(DeskBox.Features.ManagedStorage.ManagedStorageSettingsViewModel.CanInvokeQuickAccessAction) ||
            e.PropertyName == nameof(DeskBox.Features.ManagedStorage.ManagedStorageSettingsViewModel.ShouldUnpinQuickAccessAction))
        {
            if (!DispatcherQueue.HasThreadAccess)
            {
                DispatcherQueue.TryEnqueue(SyncManagedStorageQuickAccessToggleState);
                return;
            }

            SyncManagedStorageQuickAccessToggleState();
        }
    }

    private void SyncManagedStorageQuickAccessToggleState()
    {
        if (!_settingsSectionElements.TryGetValue("AppearanceDetail", out FrameworkElement? el) ||
            el is not Views.SettingsSections.FileWidgetSettingsSection section)
        {
            return;
        }

        section.SetQuickAccessState(
            _managedStorageSettingsViewModel.ShouldUnpinQuickAccessAction,
            _managedStorageSettingsViewModel.CanInvokeQuickAccessAction);
    }

    private void OpenManagedStoragePathButton_Click(object? sender, RoutedEventArgs e)
    {
        string path = ViewModel.ManagedStorageRootPath;
        try
        {
            Directory.CreateDirectory(path);
            Services.ShellLaunchService.FireAndForgetOpenPath(path);
        }
        catch (Exception ex)
        {
            App.Log($"[SettingsWindow] Failed to open managed storage root '{path}': {ex.Message}");
            _ = ShowInfoDialogAsync(
                _localizationService.T("Settings.Dialog.OpenStorageFailedTitle"),
                _localizationService.Format("Settings.Dialog.OpenStorageFailedBody", ex.Message));
        }
    }

    private async void AppearanceDetailSection_QuickAccessToggled(object? sender, bool requestedPinned)
    {
        if (!_managedStorageSettingsViewModel.CanInvokeQuickAccessAction)
        {
            SyncManagedStorageQuickAccessToggleState();
            return;
        }

        string path = ViewModel.ManagedStorageRootPath;
        bool shouldUnpin = !requestedPinned;

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
            await ShowInfoDialogAsync(
                shouldUnpin
                    ? _localizationService.T("Settings.Dialog.UnpinQuickAccessFailedTitle")
                    : _localizationService.T("Settings.Dialog.PinQuickAccessFailedTitle"),
                _localizationService.T("Settings.Dialog.QuickAccessOperationFailedBody"));
        }
        catch (Exception ex)
        {
            App.Log($"[SettingsWindow] Failed to update Quick Access pin state: {ex}");
            await ShowInfoDialogAsync(
                shouldUnpin
                    ? _localizationService.T("Settings.Dialog.UnpinQuickAccessFailedTitle")
                    : _localizationService.T("Settings.Dialog.PinQuickAccessFailedTitle"),
                _localizationService.T("Settings.Dialog.QuickAccessOperationFailedBody"));
        }
        finally
        {
            ViewModel.SetQuickAccessBusy(false);
            SyncManagedStorageQuickAccessToggleState();
        }
    }

    private void OpenWebsiteButton_Click(object sender, RoutedEventArgs e)
    {
        Services.ShellLaunchService.FireAndForgetOpenPath(ViewModel.OfficialWebsiteLink);
    }

    private void OpenPrivacyPolicyButton_Click(object sender, RoutedEventArgs e)
    {
        Services.ShellLaunchService.FireAndForgetOpenPath("https://deskbox.fun/privacy/");
    }

    private async void ShowStoreSupportDialogButton_Click(object sender, RoutedEventArgs e)
    {
        if (SettingsRoot.XamlRoot is null ||
            ViewModel.StoreSupportCardVisibility != Visibility.Visible)
        {
            return;
        }

#if !DESKBOX_STORE
        if (WechatSupportQrContainer.Child is Image wechatQrImage && wechatQrImage.Source is null)
        {
            wechatQrImage.Source = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(
                new Uri("ms-appx:///Assets/Support/support-wechat.png"));
        }

        if (AlipaySupportQrContainer.Child is Image alipayQrImage && alipayQrImage.Source is null)
        {
            alipayQrImage.Source = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(
                new Uri("ms-appx:///Assets/Support/support-alipay.png"));
        }
#endif

        SupportDeskBoxDialog.XamlRoot = SettingsRoot.XamlRoot;
        SupportDeskBoxDialog.RequestedTheme = NeutralInteractionBrush.ResolveEffectiveElementTheme(SettingsRoot);
        SupportDeskBoxDialog.Title = _localizationService.T("Settings.About.StoreSupportTitle");
        SupportDeskBoxDialog.CloseButtonText = _localizationService.T("Settings.Dialog.SupportClose");
        try
        {
            await SupportDeskBoxDialog.ShowAsync();
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
        }
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

        Services.ShellLaunchService.FireAndForgetOpenPath(ViewModel.MicrosoftStoreLink);
    }

    private async void OneClickUpdateButton_Click(object sender, RoutedEventArgs e)
    {
        await RunOneClickUpdateFlowAsync();
    }

    /// <summary>
    /// The one-click update flow, shared by the About-page button and
    /// external entry points (update-toast activation). Runs check →
    /// download, or the install confirmation when a download already
    /// finished; failures surface through the same retry/offline dialogs.
    /// The caller is responsible for having the window visible and on the
    /// About section before invoking, so progress stays in view.
    /// </summary>
    public async Task RunOneClickUpdateFlowAsync()
    {
        // If update is already downloaded, show install confirmation dialog.
        if (ViewModel.IsUpdateDownloaded)
        {
            await PromptAndInstallDownloadedUpdateAsync();
            return;
        }

        // Otherwise, trigger one-click check → download → install prompt flow.
        AppUpdateDownloadResult? downloadResult = await ViewModel.OneClickUpdateActionAsync();
        if (downloadResult is { Success: true } && ViewModel.IsUpdateDownloaded)
        {
            await PromptAndInstallDownloadedUpdateAsync();
            return;
        }

        if (downloadResult is { Success: false, FailureKind: not AppUpdateDownloadFailureKind.Cancelled })
        {
            await ShowDownloadFailureDialogAsync(downloadResult);
        }
    }

    private async Task PromptAndInstallDownloadedUpdateAsync()
    {
        if (SettingsRoot.XamlRoot is null || !ViewModel.IsUpdateDownloaded)
        {
            return;
        }

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

        // Routed through the coordinator: a toast "update now" click can
        // reach this flow while another dialog is already open, and a
        // raw ShowAsync would throw and be swallowed as a silent no-op.
        // Being displaced here reads as Cancel — the install simply does
        // not start.
        ContentDialogResult confirmation = await ShowDialogWhenFreeAsync(() => dialog);
        if (confirmation != ContentDialogResult.Primary)
        {
            return;
        }

        if (!await CreatePreUpdateRecoverySnapshotAsync())
        {
            return;
        }

        var result = ViewModel.StartDownloadedUpdateInstall();
        if (!result.Success)
        {
            await ShowInstallStartFailedDialogAsync(
                result.ErrorMessage ?? _localizationService.T("Settings.Update.InstallStartFailedBody"));
            return;
        }

        await _appActions.ShutdownForUpdateAsync();
    }

    private async Task ShowInstallStartFailedDialogAsync(string message)
    {
        if (SettingsRoot.XamlRoot is null)
        {
            return;
        }

        ContentDialogResult choice = await ShowDialogWhenFreeAsync(() =>
        {
            var dialog = new ContentDialog
            {
                XamlRoot = SettingsRoot.XamlRoot,
                Title = _localizationService.T("Settings.Update.InstallStartFailedTitle"),
                Content = new TextBlock
                {
                    Text = message,
                    TextWrapping = TextWrapping.Wrap
                },
                CloseButtonText = _localizationService.T("Common.Close"),
                DefaultButton = ContentDialogButton.Close
            };

            if (ViewModel.CanOpenManualUpdateDownload)
            {
                dialog.PrimaryButtonText = ViewModel.UpdateFallbackActionText;
                dialog.DefaultButton = ContentDialogButton.Primary;
            }

            return dialog;
        });

        if (choice == ContentDialogResult.Primary && ViewModel.CanOpenManualUpdateDownload)
        {
            OpenManualUpdateDownload();
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
            if (result is { Success: true } && ViewModel.IsUpdateDownloaded)
            {
                await PromptAndInstallDownloadedUpdateAsync();
                return;
            }
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
            Services.ShellLaunchService.FireAndForgetOpenPath(url);
        }
    }

    private void DismissUpdateActivity_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.DismissUpdateActivity();
    }

    /// <summary>
    /// Scrolls the update status card into view. Called after the tray and
    /// toast paths land on the About section so the update entry point is on
    /// screen without hunting for it.
    /// </summary>
    public void BringUpdateStatusIntoView()
    {
        if (_isClosed)
        {
            return;
        }

        // Section navigation may still be mid-layout; a queued pass runs
        // after the frame has composed the target panel. The About panel is
        // instantiated lazily with its section template: on a cold
        // navigation (update-state push arriving before the section was
        // ever opened) the named field is still null. Dereferencing it
        // inside the DispatcherQueue callback threw a NullReferenceException
        // that XAML stowed and the whole host process died with it
        // (0xc000027b, batch R10) — null-guard and retry one layout tick.
        _ = DispatcherQueue.TryEnqueue(() =>
        {
            if (_isClosed)
            {
                return;
            }

            if (UpdateCheckActionsPanel is { } panel)
            {
                panel.StartBringIntoView();
                return;
            }

            _ = DispatcherQueue.TryEnqueue(() =>
            {
                if (!_isClosed)
                {
                    UpdateCheckActionsPanel?.StartBringIntoView();
                }
            });
        });
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
        // lazily inside itself. Routed through App's single flight so the
        // settings button and the automatic/toast openers can never show
        // two release-notes windows at once.
        AppUpdateManifest? manifest = ViewModel.LatestUpdateManifest;
        // No cross-process ownership: the release-notes window lives in the
        // main process while this HWND dies with the settings host — an
        // owner here made Windows destroy the child window too (batch R6).
        _appActions.OpenReleaseNotesWindow(
            manifest,
            ViewModel.AppVersion,
            manifest is null ? ViewModel.FetchLatestManifestForReleaseNotesAsync : null,
            0);
    }
}
