using DeskBox.Models;
using DeskBox.Services;
using DeskBox.Contracts;
using DeskBox.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System.Collections.Specialized;

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
    private bool _cloudBackupSnapshotSyncHooked;
    private NotifyCollectionChangedEventHandler? _cloudBackupCollectionChanged;

    internal Task StopCloudBackupActionsAsync()
    {
        _backupSettingsViewModel.Deactivate();
        return _backupRestoreActions.StopAsync();
    }

    private Task InitializeCloudBackupSectionAsync()
    {
        if (!_cloudBackupSnapshotSyncHooked)
        {
            _cloudBackupSnapshotSyncHooked = true;
            // The native ListView uses an object[] snapshot for AOT-safe projection.
            _cloudBackupCollectionChanged = (_, _) => SyncCloudBackupSnapshotList();
            ViewModel.CloudBackupRemoteSnapshots.CollectionChanged += _cloudBackupCollectionChanged;
        }

        SyncCloudBackupSnapshotList();
        return Task.CompletedTask;
    }

    private bool CurrentCloudBackupVisit(long endpointGeneration, int visitGeneration) =>
        !_isClosed && !_backupCommands.IsStopping && _backupSettingsViewModel.IsActive &&
        _backupSettingsViewModel.VisitGeneration == visitGeneration &&
        ViewModel.CloudBackupEndpointGeneration == endpointGeneration;

    private async void CloudBackupSavePasswordButton_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.CloudBackupBusy || !_backupSettingsViewModel.IsActive) return;
        string password = CloudBackupPasswordBox.Password;
        if (string.IsNullOrWhiteSpace(password))
        {
            await ShowInfoDialogAsync(
                _localizationService.T("Settings.CloudBackup.Password.EmptyTitle"),
                _localizationService.T("Settings.CloudBackup.Password.EmptyBody"));
            return;
        }

        if (await _backupSettingsViewModel.SaveCredentialAsync(password))
            CloudBackupPasswordBox.Password = string.Empty;
    }

    private async void CloudBackupTestConnectionButton_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.CloudBackupBusy || !_backupSettingsViewModel.IsActive) return;
        string typedPassword = CloudBackupPasswordBox.Password;
        await _backupSettingsViewModel.ProbeAsync(
            string.IsNullOrEmpty(typedPassword) ? null : typedPassword);
    }

    private async void CloudBackupNowButton_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.CloudBackupBusy || _backupCommands.IsStopping) return;
        ViewModel.CloudBackupBusy = true;
        long generation = ViewModel.CloudBackupEndpointGeneration;
        int visit = _backupSettingsViewModel.VisitGeneration;
        try
        {
            CloudBackupRunResult result = await _backupCommands.UploadNowAsync();
            if (!CurrentCloudBackupVisit(generation, visit)) return;
            string prunedSuffix = result.PrunedCount > 0
                ? " " + _localizationService.Format(
                    "Settings.CloudBackup.BackupNow.PrunedSuffix", result.PrunedCount)
                : string.Empty;
            string unverifiedSuffix = result.UploadUnverified
                ? " " + _localizationService.T("Settings.CloudBackup.BackupNow.UnverifiedSuffix")
                : string.Empty;
            ViewModel.CloudBackupConnectionStatusText = result.Uploaded
                ? _localizationService.Format(
                    "Settings.CloudBackup.BackupNow.Success",
                    result.RemoteFilePath ?? string.Empty) + prunedSuffix + unverifiedSuffix
                : result.AlreadyInProgress
                    ? _localizationService.T("Settings.CloudBackup.BackupNow.InProgress")
                    : result.RestorePending
                        ? _localizationService.T("Settings.CloudBackup.RestorePending")
                        : result.NoCredential
                            ? _localizationService.T("Settings.CloudBackup.Password.NotSaved")
                            : result.NoScopeSelected
                                ? _localizationService.T("Settings.CloudBackup.NoScope")
                                : _localizationService.T("Settings.CloudBackup.NotConfigured");
            ViewModel.RefreshCloudBackupStatus();
        }
        catch (OperationCanceledException) when (_backupCommands.IsStopping) { }
        catch (Exception ex)
        {
            App.Log($"[CloudBackup] Manual backup failed: {ex}");
            if (CurrentCloudBackupVisit(generation, visit))
                ViewModel.CloudBackupConnectionStatusText = _localizationService.Format(
                    "Settings.CloudBackup.BackupNow.Failed", ex.Message);
        }
        finally
        {
            if (!_isClosed) ViewModel.CloudBackupBusy = false;
        }
    }

    private async void CloudBackupRefreshSnapshotsButton_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.CloudBackupBusy || !_backupSettingsViewModel.IsActive) return;
        await _backupSettingsViewModel.RefreshSnapshotsAsync();
    }

    // ItemsSource is assigned in code, not bound: pushing an
    // ObservableCollection<T> through {Binding} needs the WinRT
    // IObservableVector marshaling path, which silently yields an empty
    // list under Native AOT. A plain object[] snapshot needs no CCW —
    // same pattern as BackupSnapshotsList.
    private void SyncCloudBackupSnapshotList()
    {
        // Generic lookup — the untyped overload + `is` check would silently
        // skip the assignment if AOT returned a base-class RCW, recreating
        // the blank-list bug this code exists to fix.
        if (FindCreatedSectionElement<global::Microsoft.UI.Xaml.Controls.ListView>(
                "CloudBackupSettings", "CloudBackupSnapshotsList") is { } list)
        {
            list.ItemsSource = ViewModel.CloudBackupRemoteSnapshots.Count == 0
                ? null
                : ViewModel.CloudBackupRemoteSnapshots.Cast<object>().ToArray();
        }

        // Empty-state hint: an unexplained blank area reads as "still
        // loading" on first visit — say there is simply nothing remote yet.
        if (FindCreatedSectionElement<TextBlock>(
                "CloudBackupSettings", "CloudBackupSnapshotsEmptyHint") is { } emptyHint)
        {
            emptyHint.Visibility = ViewModel.CloudBackupRemoteSnapshots.Count == 0
                ? Visibility.Visible
                : Visibility.Collapsed;
        }
    }

    /// <summary>
    /// Maps service-layer backup errors to localized text. Exception
    /// messages from <see cref="DeskBoxDataBackupService"/> are English
    /// log strings, not UI copy.
    /// </summary>
    private string FormatBackupError(Exception ex)
    {
        if (ex.Data.Contains(DeskBoxDataBackupService.BackupSchemaVersionDataKey))
        {
            return _localizationService.Format(
                "Settings.DataBackup.RestoreFailedSchemaVersion",
                ex.Data[DeskBoxDataBackupService.BackupSchemaVersionDataKey] ?? string.Empty);
        }

        if (ex.Data.Contains(DeskBoxDataBackupService.BackupManifestInvalidDataKey))
        {
            return _localizationService.T("Settings.DataBackup.RestoreFailedInvalidManifest");
        }

        return ex.Message;
    }

    /// <summary>
    /// Localized label for one restored domain in the confirm dialog —
    /// "Todo data (12)". A manifest domain can legitimately hold zero live
    /// items (backup taken before data existed); the count makes that
    /// explicit instead of letting an empty domain silently wipe local data.
    /// WidgetStyle is a settings projection and shows no count.
    /// </summary>
    private string FormatRestoreDomainLabel(
        string manifestName,
        IReadOnlyList<DeskBoxDomainItemCount>? itemCounts)
    {
        string titleKey = manifestName switch
        {
            "todo-data" => "Settings.CloudBackup.TodoData.Title",
            "quick-capture-data" => "Settings.CloudBackup.QuickCaptureData.Title",
            "widget-style" => "Settings.CloudBackup.WidgetStyle.Title",
            _ => manifestName
        };
        string title = titleKey.StartsWith("Settings.", StringComparison.Ordinal)
            ? _localizationService.T(titleKey)
            : titleKey;
        int? items = itemCounts?
            .FirstOrDefault(c => string.Equals(c.Domain, manifestName, StringComparison.Ordinal))
            ?.Items;
        return items is { } count
            ? _localizationService.Format("Settings.CloudBackup.RestoreConfirm.DomainItems", title, count)
            : title;
    }

    private async void CloudBackupDeleteSnapshotButton_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.CloudBackupBusy ||
            SettingsRoot.XamlRoot is null ||
            sender is not FrameworkElement { DataContext: CloudBackupRemoteSnapshotItem snapshot } ||
            !_backupSettingsViewModel.IsActive ||
            !_backupRestoreActions.IsCurrentEndpoint(snapshot.Endpoint))
        {
            return;
        }

        long generation = ViewModel.CloudBackupEndpointGeneration;
        int visit = _backupSettingsViewModel.VisitGeneration;
        // Remote deletes are irreversible — confirm first, in the same
        // style as every other destructive dialog in settings.
        var confirmDialog = new ContentDialog
        {
            XamlRoot = SettingsRoot.XamlRoot,
            Title = _localizationService.T("Settings.CloudBackup.DeleteSnapshot.Title"),
            PrimaryButtonText = _localizationService.T("Common.Delete"),
            CloseButtonText = _localizationService.T("Common.Cancel"),
            DefaultButton = ContentDialogButton.Close,
            Content = new TextBlock
            {
                Text = _localizationService.Format(
                    "Settings.CloudBackup.DeleteSnapshot.Body",
                    snapshot.Title),
                TextWrapping = TextWrapping.Wrap
            }
        };

        if (await confirmDialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }
        if (!CurrentCloudBackupVisit(generation, visit) ||
            !_backupRestoreActions.IsCurrentEndpoint(snapshot.Endpoint)) return;

        ViewModel.CloudBackupBusy = true;
        try
        {
            if (!_backupRestoreActions.IsCurrentEndpoint(snapshot.Endpoint) ||
                !CurrentCloudBackupVisit(generation, visit)) return;
            await _settingsService.SaveAsync();
            await _backupRestoreActions.DeleteSnapshotAsync(snapshot.Endpoint, snapshot.Name);
            if (CurrentCloudBackupVisit(generation, visit))
            {
                // Optimistic removal — the row vanishes immediately; the
                // delayed refresh corrects if the server listing lagged.
                _backupSettingsViewModel.RemoveSnapshot(snapshot.Name);
            }
        }
        catch (OperationCanceledException) when (!CurrentCloudBackupVisit(generation, visit) ||
            !_backupRestoreActions.IsCurrentEndpoint(snapshot.Endpoint)) { }
        catch (Exception ex)
        {
            App.Log($"[CloudBackup] Deleting remote snapshot failed: {ex}");
            if (CurrentCloudBackupVisit(generation, visit))
            {
                ViewModel.CloudBackupConnectionStatusText = _localizationService.Format(
                    "Settings.CloudBackup.DeleteSnapshot.Failed",
                    ex.Message);
            }
        }
        finally
        {
            if (!_isClosed) ViewModel.CloudBackupBusy = false;
        }
    }

    private async void CloudBackupRestoreSnapshotButton_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.CloudBackupBusy ||
            SettingsRoot.XamlRoot is null ||
            sender is not FrameworkElement { DataContext: CloudBackupRemoteSnapshotItem snapshot } ||
            !_backupSettingsViewModel.IsActive ||
            !_backupRestoreActions.IsCurrentEndpoint(snapshot.Endpoint))
        {
            return;
        }

        // The busy flag covers the WHOLE flow, domain picker included. A
        // second restore starting while the first is mid-download would
        // run PrepareScopedRestoreAsync, which deletes the shared pending
        // marker — the first dialog's confirm would then apply the SECOND
        // snapshot. Confirmed must equal applied.
        ViewModel.CloudBackupBusy = true;
        bool restartScheduled = false;
        bool prepareStarted = false;
        string? downloadDirectory = null;
        long generation = ViewModel.CloudBackupEndpointGeneration;
        int visit = _backupSettingsViewModel.VisitGeneration;
        try
        {
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
            if (!CurrentCloudBackupVisit(generation, visit) ||
                !_backupRestoreActions.IsCurrentEndpoint(snapshot.Endpoint)) return;

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
            downloadDirectory = Path.Combine(
                Path.GetTempPath(),
                $"deskbox-cloud-restore-{Guid.NewGuid():N}");
            string archivePath = await _backupRestoreActions.DownloadSnapshotAsync(
                snapshot.Endpoint,
                snapshot.Name,
                downloadDirectory);

            prepareStarted = true;
            DeskBoxRestorePreparation preparation =
                await _backupRestoreActions.PrepareScopedRestoreAsync(archivePath, scope);

            string domainList = preparation.Domains is { Count: > 0 } domains
                ? string.Join(", ", domains.Select(domain =>
                    FormatRestoreDomainLabel(domain, preparation.DomainItemCounts)))
                : _localizationService.T("Settings.CloudBackup.RestoreDomains.None");
            var bodyText = new System.Text.StringBuilder(_localizationService.Format(
                "Settings.CloudBackup.RestoreConfirm.Body",
                preparation.BackupCreatedAtUtc.ToLocalTime().ToString("g"),
                preparation.AppVersion,
                preparation.FileCount,
                ViewModel.FormatBytes(preparation.TotalUncompressedBytes),
                domainList));
            if (!string.IsNullOrEmpty(preparation.SourceDeviceId))
            {
                bodyText.Append(' ').Append(_localizationService.Format(
                    "Settings.CloudBackup.RestoreConfirm.SourceDevice",
                    preparation.SourceDeviceId));
            }

            if (preparation.TodoWidgetRemaps is { Count: > 0 } remaps)
            {
                // Count target widgets, not source stores — several source
                // stores can merge into the same live widget.
                bodyText.Append(' ').Append(_localizationService.Format(
                    "Settings.CloudBackup.RestoreConfirm.Remapped",
                    remaps.Select(r => r.TargetWidgetId)
                        .Distinct(StringComparer.Ordinal)
                        .Count()));
            }

            if (preparation.UnmappedTodoWidgetIds is { Count: > 0 } unmapped)
            {
                bodyText.Append(' ').Append(_localizationService.Format(
                    "Settings.CloudBackup.RestoreConfirm.Unmapped",
                    unmapped.Count));
            }

            // Item-restore mode is a real choice with destructive potential
            // on one side, so it gets its own labelled radio group — merge
            // is the safe default, overwrite warns explicitly on selection.
            var mergeModeRadio = new RadioButton
            {
                IsChecked = true,
                GroupName = "CloudBackupRestoreMode",
                Content = _localizationService.T("Settings.CloudBackup.RestoreConfirm.Mode.Merge")
            };
            var overwriteModeRadio = new RadioButton
            {
                GroupName = "CloudBackupRestoreMode",
                Content = _localizationService.T("Settings.CloudBackup.RestoreConfirm.Mode.Overwrite")
            };
            var overwriteWarning = new InfoBar
            {
                Severity = InfoBarSeverity.Warning,
                IsClosable = false,
                IsOpen = false,
                Message = _localizationService.T("Settings.CloudBackup.RestoreConfirm.OverwriteWarning")
            };
            mergeModeRadio.Checked += (_, _) => overwriteWarning.IsOpen = false;
            overwriteModeRadio.Checked += (_, _) => overwriteWarning.IsOpen = true;

            var confirmContent = new StackPanel { Spacing = 10 };
            confirmContent.Children.Add(new TextBlock
            {
                Text = bodyText.ToString(),
                TextWrapping = TextWrapping.Wrap
            });
            if (preparation.AttachmentReferenceCount > 0)
            {
                confirmContent.Children.Add(new InfoBar
                {
                    Severity = InfoBarSeverity.Warning,
                    IsClosable = false,
                    IsOpen = true,
                    Message = _localizationService.Format(
                        "Settings.CloudBackup.RestoreConfirm.AttachmentRefs",
                        preparation.AttachmentReferenceCount)
                });
            }

            if (preparation.IsFromNewerAppVersion)
            {
                confirmContent.Children.Add(new InfoBar
                {
                    Severity = InfoBarSeverity.Warning,
                    IsClosable = false,
                    IsOpen = true,
                    Message = _localizationService.Format(
                        "Settings.CloudBackup.RestoreConfirm.NewerVersion",
                        preparation.AppVersion)
                });
            }

            confirmContent.Children.Add(new TextBlock
            {
                Text = _localizationService.T("Settings.CloudBackup.RestoreConfirm.Mode.Title"),
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
            });
            confirmContent.Children.Add(mergeModeRadio);
            confirmContent.Children.Add(overwriteModeRadio);
            confirmContent.Children.Add(overwriteWarning);

            var confirmDialog = new ContentDialog
            {
                XamlRoot = SettingsRoot.XamlRoot,
                Title = _localizationService.T("Settings.CloudBackup.RestoreConfirm.Title"),
                PrimaryButtonText = _localizationService.T("Settings.CloudBackup.RestoreConfirm.Button"),
                CloseButtonText = _localizationService.T("Common.Cancel"),
                DefaultButton = ContentDialogButton.Close,
                Content = confirmContent
            };

            if (await confirmDialog.ShowAsync() != ContentDialogResult.Primary)
            {
                await _backupRestoreActions.CancelPendingRestoreAsync();
                return;
            }

            // Persist the chosen mode onto the pending marker — apply runs
            // after the restart and only has the marker to consult.
            if (!await _backupRestoreActions.SetPendingRestoreItemReplaceModeAsync(
                    overwriteModeRadio.IsChecked == true))
            {
                await _backupRestoreActions.CancelPendingRestoreAsync();
                throw new InvalidOperationException("The pending restore is no longer available.");
            }

            AppRelaunchScheduleResult relaunch = _backupRestoreActions.ScheduleRelaunch();
            if (!relaunch.Started)
            {
                await _backupRestoreActions.CancelPendingRestoreAsync();
                await ShowInfoDialogAsync(
                    _localizationService.T("Settings.DataBackup.RestartFailedTitle"),
                    _localizationService.Format(
                        "Settings.DataBackup.RestartFailedBody",
                        relaunch.ErrorMessage ?? string.Empty));
                return;
            }

            restartScheduled = true;
            await _backupRestoreActions.ShutdownForRestartAsync();
        }
        catch (OperationCanceledException) when (!prepareStarted ||
            !CurrentCloudBackupVisit(generation, visit)) { }
        catch (Exception ex)
        {
            App.Log($"[CloudBackup] Remote restore failed: {ex}");
            if (!restartScheduled && prepareStarted)
            {
                try
                {
                    await _backupRestoreActions.CancelPendingRestoreAsync();
                }
                catch (Exception cancelEx)
                {
                    App.Log($"[CloudBackup] Cancelling pending restore failed: {cancelEx}");
                }
            }

            await ShowInfoDialogAsync(
                _localizationService.T("Settings.CloudBackup.RestoreFailed.Title"),
                _localizationService.Format("Settings.CloudBackup.RestoreFailed.Body", FormatBackupError(ex)));
        }
        finally
        {
            // The staged archive was already extracted into the app's own
            // restore staging — the temp download must not linger either
            // way (it can be hundreds of MB).
            _backupRestoreActions.DeleteTemporaryDownload(downloadDirectory);

            if (!_isClosed) ViewModel.CloudBackupBusy = false;
        }
    }
}
