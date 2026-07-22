using DeskBox.Helpers;
using DeskBox.Services;
using DeskBox.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace DeskBox.Views;

public sealed partial class SettingsWindow
{
    private void RefreshDragDropPermissionButton_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.RefreshDragDropPermissionDiagnostic();
    }

    private async void RepairDragDropPermissionButton_Click(object sender, RoutedEventArgs e)
    {
        if (SettingsRoot.XamlRoot is null)
        {
            return;
        }

        var result = ViewModel.RepairDragDropPermission();
        if (result.NeedsRelaunch)
        {
            var dialog = new ContentDialog
            {
                XamlRoot = SettingsRoot.XamlRoot,
                Title = _localizationService.T("Settings.DragDropPermission.RelaunchTitle"),
                PrimaryButtonText = _localizationService.T("Settings.DragDropPermission.RelaunchButton"),
                CloseButtonText = _localizationService.T("Common.Cancel"),
                DefaultButton = ContentDialogButton.Primary,
                Content = new TextBlock
                {
                    Text = _localizationService.T("Settings.DragDropPermission.RelaunchBody"),
                    TextWrapping = TextWrapping.Wrap
                }
            };

            if (await dialog.ShowAsync() == ContentDialogResult.Primary)
            {
                if (DragDropPermissionService.TryRelaunchAsExplorerUser())
                {
                    App.Current.Exit();
                }
                else
                {
                    await ShowInfoDialogAsync(
                        _localizationService.T("Settings.DragDropPermission.RelaunchFailedTitle"),
                        _localizationService.T("Settings.DragDropPermission.RelaunchFailedBody"));
                }
            }

            return;
        }

        await ShowInfoDialogAsync(
            _localizationService.T(result.Success
                ? "Settings.DragDropPermission.RepairCompleteTitle"
                : "Settings.DragDropPermission.RepairFailedTitle"),
            result.Success
                ? _localizationService.Format("Settings.DragDropPermission.RepairCompleteBody", result.RepairedCount)
                : result.FailureMessage);
    }

    private async void OpenUacSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        await Task.Run(() => Win32Helper.OpenFile("UserAccountControlSettings.exe"));
    }

    private async void ExportDataBackupButton_Click(object sender, RoutedEventArgs e)
    {
        if (SettingsRoot.XamlRoot is null)
        {
            return;
        }

        string? folderPath = FolderPickerService.PickFolder(_hWnd);
        if (string.IsNullOrWhiteSpace(folderPath))
        {
            return;
        }

        ExportDataBackupButton.IsEnabled = false;
        try
        {
            await App.Current.SettingsService.SaveAsync(notifySubscribers: false);
            string backupPath = await App.Current.DataBackupService.ExportBackupAsync(folderPath);
            await ShowInfoDialogAsync(
                _localizationService.T("Settings.DataBackup.SuccessTitle"),
                _localizationService.Format("Settings.DataBackup.SuccessBody", backupPath));
            Win32Helper.ShowInExplorer(backupPath);
        }
        catch (Exception ex)
        {
            App.Log($"[DataBackup] Manual export failed: {ex}");
            await ShowInfoDialogAsync(
                _localizationService.T("Settings.DataBackup.FailedTitle"),
                _localizationService.Format("Settings.DataBackup.FailedBody", ex.Message));
        }
        finally
        {
            ExportDataBackupButton.IsEnabled = true;
        }
    }

    private async void RestoreDataBackupButton_Click(object sender, RoutedEventArgs e)
    {
        if (SettingsRoot.XamlRoot is null)
        {
            return;
        }

        Windows.Storage.StorageFile? backupFile;
        try
        {
            var picker = new FileOpenPicker
            {
                SuggestedStartLocation = PickerLocationId.DocumentsLibrary
            };
            picker.FileTypeFilter.Add(".zip");
            InitializeWithWindow.Initialize(picker, _hWnd);
            backupFile = await picker.PickSingleFileAsync();
        }
        catch (Exception ex)
        {
            App.Log($"[DataBackup] Restore picker failed: {ex}");
            await ShowInfoDialogAsync(
                _localizationService.T("Settings.DataBackup.RestoreFailedTitle"),
                _localizationService.Format("Settings.DataBackup.RestoreFailedBody", ex.Message));
            return;
        }

        if (backupFile is null || string.IsNullOrWhiteSpace(backupFile.Path))
        {
            return;
        }

        await RestoreDataBackupFromPathAsync(backupFile.Path);
    }

    private async Task RestoreDataBackupFromPathAsync(string archivePath)
    {
        if (SettingsRoot.XamlRoot is null)
        {
            return;
        }

        RestoreDataBackupButton.IsEnabled = false;
        ExportDataBackupButton.IsEnabled = false;
        bool restartScheduled = false;
        try
        {
            DeskBoxRestorePreparation preparation = await App.Current.DataBackupService.PrepareRestoreAsync(
                archivePath);
            string integrityWarning = preparation.HasIntegrityManifest
                ? string.Empty
                : $"\n\n{_localizationService.T("Settings.DataBackup.LegacyIntegrityWarning")}";
            var dialog = new ContentDialog
            {
                XamlRoot = SettingsRoot.XamlRoot,
                Title = _localizationService.T("Settings.DataBackup.RestoreConfirmTitle"),
                PrimaryButtonText = _localizationService.T("Settings.DataBackup.RestoreConfirmButton"),
                CloseButtonText = _localizationService.T("Common.Cancel"),
                DefaultButton = ContentDialogButton.Close,
                Content = new TextBlock
                {
                    Text = _localizationService.Format(
                        "Settings.DataBackup.RestoreConfirmBody",
                        preparation.BackupCreatedAtUtc.ToLocalTime().ToString("g"),
                        preparation.AppVersion,
                        preparation.FileCount,
                        ViewModel.FormatBytes(preparation.TotalUncompressedBytes),
                        integrityWarning),
                    TextWrapping = TextWrapping.Wrap
                }
            };

            if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            {
                await App.Current.DataBackupService.CancelPendingRestoreAsync();
                return;
            }

            AppRelaunchScheduleResult relaunch = AppRelaunchService.ScheduleAfterCurrentProcessExit();
            if (!relaunch.Started)
            {
                await App.Current.DataBackupService.CancelPendingRestoreAsync();
                await ShowInfoDialogAsync(
                    _localizationService.T("Settings.DataBackup.RestartFailedTitle"),
                    _localizationService.Format(
                        "Settings.DataBackup.RestartFailedBody",
                        relaunch.ErrorMessage ?? string.Empty));
                return;
            }

            restartScheduled = true;
            await App.Current.ShutdownForRestartAsync();
        }
        catch (Exception ex)
        {
            App.Log($"[DataBackup] Restore preparation failed: {ex}");
            if (!restartScheduled)
            {
                await App.Current.DataBackupService.CancelPendingRestoreAsync();
            }
            await ShowInfoDialogAsync(
                _localizationService.T("Settings.DataBackup.RestoreFailedTitle"),
                _localizationService.Format("Settings.DataBackup.RestoreFailedBody", ex.Message));
        }
        finally
        {
            if (!restartScheduled && !_isClosed)
            {
                RestoreDataBackupButton.IsEnabled = true;
                ExportDataBackupButton.IsEnabled = true;
            }
        }
    }

    private async void CreateBackupSnapshotButton_Click(object sender, RoutedEventArgs e)
    {
        CreateBackupSnapshotButton.IsEnabled = false;
        RefreshBackupSnapshotsButton.IsEnabled = false;
        try
        {
            await App.Current.SettingsService.SaveAsync(notifySubscribers: false);
            string? snapshotPath = await App.Current.DataBackupService.CreateAutomaticSnapshotNowAsync();
            if (snapshotPath is null)
            {
                await ShowInfoDialogAsync(
                    _localizationService.T("Settings.DataBackup.FailedTitle"),
                    _localizationService.Format(
                        "Settings.DataBackup.FailedBody",
                        _localizationService.T("Settings.DataBackup.Snapshots.Empty")));
                return;
            }

            await RefreshBackupSnapshotInventoryAsync();
            Win32Helper.ShowInExplorer(snapshotPath);
        }
        catch (Exception ex)
        {
            App.Log($"[DataBackup] Immediate snapshot failed: {ex}");
            await ShowInfoDialogAsync(
                _localizationService.T("Settings.DataBackup.FailedTitle"),
                _localizationService.Format("Settings.DataBackup.FailedBody", ex.Message));
        }
        finally
        {
            CreateBackupSnapshotButton.IsEnabled = true;
            RefreshBackupSnapshotsButton.IsEnabled = true;
        }
    }

    private void OpenBackupFolderButton_Click(object sender, RoutedEventArgs e)
    {
        string directory = Path.GetDirectoryName(App.Current.DataBackupService.AutomaticSnapshotDirectory)
                           ?? App.Current.DataBackupService.AutomaticSnapshotDirectory;
        Directory.CreateDirectory(directory);
        Win32Helper.ShowInExplorer(directory);
    }

    private async void RefreshBackupSnapshotsButton_Click(object sender, RoutedEventArgs e)
    {
        await RefreshBackupSnapshotInventoryAsync();
    }

    private async Task RefreshBackupSnapshotInventoryAsync()
    {
        if (_isRefreshingBackupSnapshots || BackupSnapshotsList is null)
        {
            return;
        }

        _isRefreshingBackupSnapshots = true;
        RefreshBackupSnapshotsButton.IsEnabled = false;
        try
        {
            IReadOnlyList<DeskBoxBackupSnapshotInfo> snapshots =
                await App.Current.DataBackupService.GetSnapshotInventoryAsync();
            var rows = snapshots.Select(snapshot =>
            {
                string kind = snapshot.Kind == "pre-restore"
                    ? _localizationService.T("Settings.DataBackup.Snapshots.PreRestore")
                    : _localizationService.T("Settings.DataBackup.Snapshots.Automatic");
                string status = snapshot.IsReadable
                    ? _localizationService.T("Settings.DataBackup.Snapshots.Readable")
                    : _localizationService.T("Settings.DataBackup.Snapshots.Unreadable");
                string title = $"{kind} · {snapshot.CreatedAtUtc.ToLocalTime():g}";
                string details = $"{ViewModel.FormatBytes(snapshot.SizeBytes)} · {status}\n{Path.GetFileName(snapshot.Path)}";
                return new BackupSnapshotListItem(snapshot.Path, title, details, snapshot.IsReadable);
            }).ToArray();

            BackupSnapshotsList.ItemsSource = rows;
            BackupSnapshotSummaryText.Text = rows.Length == 0
                ? _localizationService.T("Settings.DataBackup.Snapshots.Empty")
                : _localizationService.Format(
                    "Settings.DataBackup.Snapshots.Summary",
                    rows.Length,
                    ViewModel.FormatBytes(snapshots.Sum(snapshot => snapshot.SizeBytes)),
                    snapshots[0].CreatedAtUtc.ToLocalTime().ToString("g"));
        }
        catch (Exception ex)
        {
            App.Log($"[DataBackup] Snapshot inventory failed: {ex}");
            BackupSnapshotsList.ItemsSource = Array.Empty<BackupSnapshotListItem>();
            BackupSnapshotSummaryText.Text = _localizationService.Format(
                "Settings.DataBackup.FailedBody",
                ex.Message);
        }
        finally
        {
            RefreshBackupSnapshotsButton.IsEnabled = true;
            _isRefreshingBackupSnapshots = false;
        }
    }

    private async void RestoreSnapshotButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: BackupSnapshotListItem item } && item.CanRestore)
        {
            await RestoreDataBackupFromPathAsync(item.Path);
        }
    }

    private async void DeleteSnapshotButton_Click(object sender, RoutedEventArgs e)
    {
        if (SettingsRoot.XamlRoot is null || sender is not Button { DataContext: BackupSnapshotListItem item })
        {
            return;
        }

        var dialog = new ContentDialog
        {
            XamlRoot = SettingsRoot.XamlRoot,
            Title = _localizationService.T("Settings.DataBackup.Snapshots.DeleteConfirmTitle"),
            PrimaryButtonText = _localizationService.T("Settings.DataBackup.Snapshots.Delete"),
            CloseButtonText = _localizationService.T("Common.Cancel"),
            DefaultButton = ContentDialogButton.Close,
            Content = new TextBlock
            {
                Text = _localizationService.Format(
                    "Settings.DataBackup.Snapshots.DeleteConfirmBody",
                    item.Title),
                TextWrapping = TextWrapping.Wrap
            }
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        try
        {
            await App.Current.DataBackupService.DeleteSnapshotAsync(item.Path);
            await RefreshBackupSnapshotInventoryAsync();
        }
        catch (Exception ex)
        {
            App.Log($"[DataBackup] Snapshot deletion failed: {ex}");
            await ShowInfoDialogAsync(
                _localizationService.T("Settings.DataBackup.FailedTitle"),
                _localizationService.Format("Settings.DataBackup.FailedBody", ex.Message));
        }
    }

    private async void CheckAttachmentHealthButton_Click(object sender, RoutedEventArgs e)
    {
        CheckAttachmentHealthButton.IsEnabled = false;
        AttachmentHealthSummaryText.Text = _localizationService.T("Settings.AttachmentHealth.Checking");
        try
        {
            DeskBoxAttachmentHealthReport report = await App.Current.AttachmentHealthService.ScanAsync();
            string key = report.UnreadableStoreCount > 0
                ? "Settings.AttachmentHealth.Partial"
                : report.IsHealthy
                    ? "Settings.AttachmentHealth.Healthy"
                    : "Settings.AttachmentHealth.Issues";
            AttachmentHealthSummaryText.Text = _localizationService.Format(
                key,
                report.ReferencedFileCount,
                report.MissingLinkedFiles.Count,
                report.MissingManagedFiles.Count,
                report.OrphanManagedFiles.Count,
                report.UnreadableStoreCount);
        }
        catch (Exception ex)
        {
            App.Log($"[AttachmentHealth] Scan failed: {ex}");
            AttachmentHealthSummaryText.Text = _localizationService.Format(
                "Settings.AttachmentHealth.Failed",
                ex.Message);
        }
        finally
        {
            CheckAttachmentHealthButton.IsEnabled = true;
        }
    }

    private async void RestoreDefaultSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        if (SettingsRoot.XamlRoot is null)
        {
            return;
        }

        var dialog = new ContentDialog
        {
            XamlRoot = SettingsRoot.XamlRoot,
            Title = _localizationService.T("Settings.Dialog.RestoreTitle"),
            PrimaryButtonText = _localizationService.T("Common.Restore"),
            CloseButtonText = _localizationService.T("Common.Cancel"),
            DefaultButton = ContentDialogButton.Primary,
            Content = new TextBlock
            {
                Text = _localizationService.T("Settings.Dialog.RestoreBody"),
                TextWrapping = TextWrapping.Wrap
            }
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        await ViewModel.RestoreDefaultPreferencesAsync();
    }

}
