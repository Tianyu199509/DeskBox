using DeskBox.Models;
using DeskBox.Services;
using DeskBox.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System.Globalization;

namespace DeskBox.Views;

/// <summary>
/// Cloud-backup settings page (roadmap §10 PR-3): provider selection,
/// WebDAV credential entry, connection test, manual backup, remote
/// snapshot inventory and per-domain restore. The password never touches
/// bindings or settings.json — it goes straight from the PasswordBox to
/// <see cref="ICredentialStore"/>.
/// </summary>
public sealed partial class SettingsWindow
{
    private async Task InitializeCloudBackupSectionAsync()
    {
        try
        {
            ViewModel.CloudBackupCredentialSaved =
                await App.Current.CloudBackupService.HasCredentialAsync();
            ViewModel.RefreshCloudBackupStatus();
        }
        catch (Exception ex)
        {
            App.Log($"[CloudBackup] Section init failed: {ex}");
        }
    }

    private async void CloudBackupSavePasswordButton_Click(object sender, RoutedEventArgs e)
    {
        string password = CloudBackupPasswordBox.Password;
        if (string.IsNullOrWhiteSpace(password))
        {
            await ShowInfoDialogAsync(
                _localizationService.T("Settings.CloudBackup.Password.EmptyTitle"),
                _localizationService.T("Settings.CloudBackup.Password.EmptyBody"));
            return;
        }

        ViewModel.CloudBackupBusy = true;
        try
        {
            // Flush settings first so the credential key reflects the
            // provider/host/username the user just typed.
            await _settingsService.SaveAsync();
            await App.Current.CloudBackupService.SaveCredentialAsync(password);
            CloudBackupPasswordBox.Password = string.Empty;
            ViewModel.CloudBackupCredentialSaved = true;
            ViewModel.CloudBackupConnectionStatusText =
                _localizationService.T("Settings.CloudBackup.Password.Saved");
        }
        catch (Exception ex)
        {
            App.Log($"[CloudBackup] Saving credential failed: {ex}");
            ViewModel.CloudBackupConnectionStatusText = ex.Message;
        }
        finally
        {
            ViewModel.CloudBackupBusy = false;
        }
    }

    private async void CloudBackupTestConnectionButton_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.CloudBackupBusy = true;
        ViewModel.CloudBackupConnectionStatusText = string.Empty;
        try
        {
            await _settingsService.SaveAsync();
            await App.Current.CloudBackupService.ProbeConnectionAsync();
            ViewModel.CloudBackupConnectionStatusText =
                _localizationService.T("Settings.CloudBackup.TestConnection.Success");
        }
        catch (Exception ex)
        {
            App.Log($"[CloudBackup] Connection test failed: {ex}");
            ViewModel.CloudBackupConnectionStatusText = _localizationService.Format(
                "Settings.CloudBackup.TestConnection.Failed",
                ex.Message);
        }
        finally
        {
            ViewModel.CloudBackupBusy = false;
        }
    }

    private async void CloudBackupNowButton_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.CloudBackupBusy = true;
        try
        {
            await _settingsService.SaveAsync();
            CloudBackupRunResult result = await App.Current.CloudBackupService.RunBackupNowAsync();
            ViewModel.CloudBackupConnectionStatusText = result.Uploaded
                ? _localizationService.Format(
                    "Settings.CloudBackup.BackupNow.Success",
                    result.RemoteFilePath ?? string.Empty)
                : _localizationService.T("Settings.CloudBackup.NotConfigured");
            ViewModel.RefreshCloudBackupStatus();
        }
        catch (Exception ex)
        {
            App.Log($"[CloudBackup] Manual backup failed: {ex}");
            ViewModel.CloudBackupConnectionStatusText = _localizationService.Format(
                "Settings.CloudBackup.BackupNow.Failed",
                ex.Message);
        }
        finally
        {
            ViewModel.CloudBackupBusy = false;
        }
    }

    private async void CloudBackupRefreshSnapshotsButton_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.CloudBackupBusy = true;
        try
        {
            await _settingsService.SaveAsync();
            IReadOnlyList<CloudBackupRemoteEntry> snapshots =
                await App.Current.CloudBackupService.ListRemoteSnapshotsAsync();

            ViewModel.CloudBackupRemoteSnapshots.Clear();
            foreach (CloudBackupRemoteEntry entry in snapshots)
            {
                ViewModel.CloudBackupRemoteSnapshots.Add(
                    FormatRemoteSnapshot(entry));
            }
        }
        catch (Exception ex)
        {
            App.Log($"[CloudBackup] Listing remote snapshots failed: {ex}");
            ViewModel.CloudBackupConnectionStatusText = _localizationService.Format(
                "Settings.CloudBackup.RefreshSnapshots.Failed",
                ex.Message);
        }
        finally
        {
            ViewModel.CloudBackupBusy = false;
        }
    }

    private CloudBackupRemoteSnapshotItem FormatRemoteSnapshot(CloudBackupRemoteEntry entry)
    {
        // DeskBox-CloudBackup-20260918-210000-abcdef12.zip → local time + device.
        string title = entry.Name;
        string details = entry.Name;
        string stem = entry.Name;
        if (stem.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            stem = stem[..^4];
        }

        string[] parts = stem.Split('-');
        if (parts.Length >= 5 &&
            DateTimeOffset.TryParseExact(
                $"{parts[^3]}-{parts[^2]}",
                "yyyyMMdd-HHmmss",
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal,
                out DateTimeOffset createdUtc))
        {
            title = createdUtc.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);
            string device = parts[^1];
            string size = entry.Length is { } length ? $" · {ViewModel.FormatBytes(length)}" : string.Empty;
            details = _localizationService.Format(
                "Settings.CloudBackup.SnapshotDetails",
                device,
                size);
        }

        return new CloudBackupRemoteSnapshotItem(entry.Name, title, details);
    }

    private async void CloudBackupRestoreSnapshotButton_Click(object sender, RoutedEventArgs e)
    {
        if (SettingsRoot.XamlRoot is null ||
            sender is not FrameworkElement { DataContext: CloudBackupRemoteSnapshotItem snapshot })
        {
            return;
        }

        // Step 1: pick the domains to restore (all on by default).
        var todoBox = new CheckBox
        {
            IsChecked = true,
            Content = _localizationService.T("Settings.CloudBackup.TodoData.Title")
        };
        var quickCaptureBox = new CheckBox
        {
            IsChecked = true,
            Content = _localizationService.T("Settings.CloudBackup.QuickCaptureData.Title")
        };
        var widgetStyleBox = new CheckBox
        {
            IsChecked = true,
            Content = _localizationService.T("Settings.CloudBackup.WidgetStyle.Title")
        };
        var domainDialog = new ContentDialog
        {
            XamlRoot = SettingsRoot.XamlRoot,
            Title = _localizationService.T("Settings.CloudBackup.RestoreDomains.Title"),
            PrimaryButtonText = _localizationService.T("Settings.CloudBackup.RestoreDomains.Continue"),
            CloseButtonText = _localizationService.T("Common.Cancel"),
            DefaultButton = ContentDialogButton.Close,
            Content = new StackPanel
            {
                Spacing = 8,
                Children =
                {
                    new TextBlock
                    {
                        Text = _localizationService.Format(
                            "Settings.CloudBackup.RestoreDomains.Body",
                            snapshot.Title),
                        TextWrapping = TextWrapping.Wrap
                    },
                    todoBox,
                    quickCaptureBox,
                    widgetStyleBox
                }
            }
        };

        if (await domainDialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        CloudBackupDomain scope = CloudBackupDomain.None;
        if (todoBox.IsChecked == true)
        {
            scope |= CloudBackupDomain.TodoData;
        }

        if (quickCaptureBox.IsChecked == true)
        {
            scope |= CloudBackupDomain.QuickCaptureData;
        }

        if (widgetStyleBox.IsChecked == true)
        {
            scope |= CloudBackupDomain.WidgetStyle;
        }

        if (scope == CloudBackupDomain.None)
        {
            return;
        }

        // Step 2: download → prepare scoped restore → confirm → relaunch.
        ViewModel.CloudBackupBusy = true;
        bool restartScheduled = false;
        try
        {
            string downloadDirectory = Path.Combine(
                Path.GetTempPath(),
                $"deskbox-cloud-restore-{Guid.NewGuid():N}");
            string archivePath = await App.Current.CloudBackupService.DownloadSnapshotAsync(
                snapshot.Name,
                downloadDirectory);

            DeskBoxRestorePreparation preparation =
                await App.Current.DataBackupService.PrepareScopedRestoreAsync(archivePath, scope);

            string domainList = preparation.Domains is { Count: > 0 } domains
                ? string.Join(", ", domains)
                : _localizationService.T("Settings.CloudBackup.RestoreDomains.None");
            var confirmDialog = new ContentDialog
            {
                XamlRoot = SettingsRoot.XamlRoot,
                Title = _localizationService.T("Settings.CloudBackup.RestoreConfirm.Title"),
                PrimaryButtonText = _localizationService.T("Settings.CloudBackup.RestoreConfirm.Button"),
                CloseButtonText = _localizationService.T("Common.Cancel"),
                DefaultButton = ContentDialogButton.Close,
                Content = new TextBlock
                {
                    Text = _localizationService.Format(
                        "Settings.CloudBackup.RestoreConfirm.Body",
                        preparation.BackupCreatedAtUtc.ToLocalTime().ToString("g"),
                        preparation.AppVersion,
                        preparation.FileCount,
                        ViewModel.FormatBytes(preparation.TotalUncompressedBytes),
                        domainList),
                    TextWrapping = TextWrapping.Wrap
                }
            };

            if (await confirmDialog.ShowAsync() != ContentDialogResult.Primary)
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
            App.Log($"[CloudBackup] Remote restore failed: {ex}");
            if (!restartScheduled)
            {
                try
                {
                    await App.Current.DataBackupService.CancelPendingRestoreAsync();
                }
                catch (Exception cancelEx)
                {
                    App.Log($"[CloudBackup] Cancelling pending restore failed: {cancelEx}");
                }
            }

            await ShowInfoDialogAsync(
                _localizationService.T("Settings.CloudBackup.RestoreFailed.Title"),
                _localizationService.Format("Settings.CloudBackup.RestoreFailed.Body", ex.Message));
        }
        finally
        {
            ViewModel.CloudBackupBusy = false;
        }
    }
}
