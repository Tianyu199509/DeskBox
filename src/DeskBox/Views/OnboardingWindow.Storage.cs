using CommunityToolkit.WinUI.Animations;
using DeskBox.Helpers;
using DeskBox.Models;
using DeskBox.Services;
using Microsoft.UI;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Shapes;
using Windows.UI;
using WinRT.Interop;

namespace DeskBox.Views;

public sealed partial class OnboardingWindow
{
    private string? _step4SuggestedStoragePath;

    private void SetupStep4Storage()
    {
        string path = SettingsService.NormalizeManagedStorageRootPath(_settingsService.Settings.DefaultManagedStorageRootPath);
        Step4PathText.Text = path;
        RefreshStep4StorageAssessment();

        var pinState = ExplorerQuickAccessHelper.GetQuickAccessPinState(path, out _);
        bool isPinned = pinState == QuickAccessPinState.Pinned;
        Step4PinToggle.Toggled -= Step4PinToggle_Toggled;
        Step4PinToggle.IsOn = isPinned;
        Step4PinToggle.Toggled += Step4PinToggle_Toggled;
    }

    private void RefreshStep4StorageAssessment()
    {
        string path = SettingsService.NormalizeManagedStorageRootPath(_settingsService.Settings.DefaultManagedStorageRootPath);
        ManagedStoragePathAssessment assessment = ManagedStoragePathService.AssessPath(path);

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
        if (assessment.DriveType == DriveType.Removable || assessment.IsTransientBusDrive)
        {
            warnings.Add(_localizationService.T("Onboarding.Task.Step2.Warning.Removable"));
        }
        else if (assessment.DriveType == DriveType.Network)
        {
            warnings.Add(_localizationService.T("Onboarding.Task.Step2.Warning.Network"));
        }

        Step4StorageWarningText.Text = string.Join(Environment.NewLine, warnings);
        Step4StorageWarningBorder.Visibility = warnings.Count > 0
            ? Visibility.Visible
            : Visibility.Collapsed;

        _step4SuggestedStoragePath = assessment.IsSystemDrive &&
                                     !string.IsNullOrWhiteSpace(assessment.SuitableNonSystemDrivePath)
            ? assessment.SuitableNonSystemDrivePath
            : null;
        Step4SuggestedDriveButton.Visibility = _step4SuggestedStoragePath is null
            ? Visibility.Collapsed
            : Visibility.Visible;
        if (_step4SuggestedStoragePath is not null)
        {
            Step4SuggestedDriveButton.Content = _localizationService.Format(
                "Onboarding.Step4.MoveToSuggestedDriveButton",
                _step4SuggestedStoragePath);
        }
    }

    private void Step4ChangePath_Click(object sender, RoutedEventArgs e)
    {
        _ = ChangeStoragePathAsync();
    }

    private void Step4SuggestedDrive_Click(object sender, RoutedEventArgs e)
    {
        if (_step4SuggestedStoragePath is null)
        {
            return;
        }

        _ = ChangeStoragePathToAsync(_step4SuggestedStoragePath);
    }

    private async Task<bool> ChangeStoragePathAsync()
    {
        string? folderPath = await FolderPickerService.PickFolderAsync(_hWnd);
        if (string.IsNullOrWhiteSpace(folderPath))
        {
            return false;
        }

        return await ChangeStoragePathToAsync(folderPath);
    }

    private async Task<bool> ChangeStoragePathToAsync(string folderPath)
    {
        string normalizedPath = SettingsService.NormalizeManagedStorageRootPath(folderPath);
        string currentPath = SettingsService.NormalizeManagedStorageRootPath(_settingsService.Settings.DefaultManagedStorageRootPath);
        if (string.Equals(normalizedPath, currentPath, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        int affectedCount = App.Current.WidgetManager?.GetDefaultManagedStorageWidgetCount() ?? 0;
        if (affectedCount > 0 && RootGrid.XamlRoot is not null)
        {
            var dialog = new ContentDialog
            {
                XamlRoot = RootGrid.XamlRoot,
                Title = _localizationService.T("Settings.Dialog.MigrateTitle"),
                PrimaryButtonText = _localizationService.T("Settings.Dialog.MigrateButton"),
                CloseButtonText = _localizationService.T("Common.Cancel"),
                DefaultButton = ContentDialogButton.Primary,
                Content = new TextBlock
                {
                    Text = _localizationService.Format(
                        "Settings.Dialog.MigrateBody",
                        affectedCount,
                        currentPath,
                        normalizedPath),
                    TextWrapping = TextWrapping.Wrap
                }
            };

            if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            {
                return false;
            }
        }

        if (App.Current.WidgetManager is not null)
        {
            try
            {
                ManagedStorageMigrationResult? result;
                if (RootGrid.XamlRoot is not null)
                {
                    result = await ManagedStorageMigrationDialog.RunAsync(
                        RootGrid.XamlRoot,
                        RootGrid.DispatcherQueue,
                        _localizationService,
                        currentPath,
                        normalizedPath,
                        options => App.Current.WidgetManager
                            .UpdateDefaultManagedStorageRootAsync(normalizedPath, options),
                        (items, options) => App.Current.WidgetManager
                            .RetrySkippedMigrationItemsAsync(items, options));
                    if (result is null)
                    {
                        // Canceled or failed — the dialog already showed it.
                        return false;
                    }
                }
                else
                {
                    result = await App.Current.WidgetManager
                        .UpdateDefaultManagedStorageRootAsync(normalizedPath);
                }

                if (result.Residues.Count > 0 && RootGrid.XamlRoot is not null)
                {
                    // The migration finished; only the old-root cleanup left
                    // folders behind. Surface them with an explicit
                    // recycle-or-keep choice instead of staying silent.
                    await ManagedStorageMigrationResidueDialog.ShowMigrationResidueAsync(
                        RootGrid.XamlRoot,
                        _localizationService,
                        folders => App.Current.WidgetManager.DeleteMigrationResidueFoldersAsync(folders),
                        result);
                }
            }
            catch (ManagedStorageDestinationResidueException ex)
            {
                if (RootGrid.XamlRoot is not null)
                {
                    ContentDialogResult choice = await ManagedStorageMigrationResidueDialog
                        .ShowStaleDestinationAsync(
                            RootGrid.XamlRoot,
                            _localizationService,
                            ex.StaleDestinationFolders);
                    if (choice == ContentDialogResult.Primary)
                    {
                        await App.Current.WidgetManager.DeleteMigrationResidueFoldersAsync(
                            ex.StaleDestinationFolders);
                        return await ChangeStoragePathToAsync(normalizedPath);
                    }
                }
                return false;
            }
            catch (ManagedStorageRollbackFailureException ex)
            {
                // The rollback left folders in both roots: list them and offer
                // a conservative retry instead of a bare failure message (#112).
                if (RootGrid.XamlRoot is not null)
                {
                    await ManagedStorageMigrationResidueDialog.ShowRollbackFailureAsync(
                        RootGrid.XamlRoot,
                        _localizationService,
                        failures => App.Current.WidgetManager.RetryMigrationRollbackAsync(failures),
                        ex);
                }
                return false;
            }
            catch (Exception ex)
            {
                if (RootGrid.XamlRoot is not null)
                {
                    // The outer message only counts completed items; the inner
                    // exception names the file that failed (e.g. locked by an app).
                    string detail = ex.InnerException is { } inner
                        ? $"{ex.Message} {inner.Message}"
                        : ex.Message;
                    var errorDialog = new ContentDialog
                    {
                        XamlRoot = RootGrid.XamlRoot,
                        Title = _localizationService.T("Settings.Dialog.MigrateFailedTitle"),
                        CloseButtonText = _localizationService.T("Common.Ok"),
                        DefaultButton = ContentDialogButton.Close,
                        Content = new TextBlock
                        {
                            Text = _localizationService.Format("Settings.Dialog.MigrateFailedBody", detail),
                            TextWrapping = TextWrapping.Wrap
                        }
                    };
                    await errorDialog.ShowAsync();
                }
                return false;
            }
        }

        _settingsService.Settings.DefaultManagedStorageRootPath = normalizedPath;
        _settingsService.SaveDebounced();
        Step4PathText.Text = normalizedPath;
        RefreshStep4StorageAssessment();
        InvalidateDesktopOrganizationPlan();
        return true;
    }

    private async void Step4PinToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleSwitch toggle)
        {
            return;
        }

        string storagePath = SettingsService.NormalizeManagedStorageRootPath(_settingsService.Settings.DefaultManagedStorageRootPath);

        if (toggle.IsOn)
        {
            var result = await ExplorerQuickAccessHelper.TryPinFolderToQuickAccessAsync(storagePath);
            if (!result.Succeeded && RootGrid.XamlRoot is not null)
            {
                var dialog = new ContentDialog
                {
                    XamlRoot = RootGrid.XamlRoot,
                    Title = _localizationService.T("Onboarding.Step4.PinTitle"),
                    CloseButtonText = _localizationService.T("Common.Ok"),
                    DefaultButton = ContentDialogButton.Close,
                    Content = new TextBlock
                    {
                        Text = _localizationService.T("Onboarding.Step4.PinFailedBody"),
                        TextWrapping = TextWrapping.Wrap
                    }
                };
                await dialog.ShowAsync();
            }
        }
        else
        {
            await ExplorerQuickAccessHelper.TryUnpinFolderFromQuickAccessAsync(storagePath);
        }
    }

    // ════════════════════════════════════════════════════════════
    //  Step 4: Daily Use (continued)
    // ════════════════════════════════════════════════════════════
}
