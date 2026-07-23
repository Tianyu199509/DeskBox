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

        string? folderPath = FolderPickerService.PickFolder(_hWnd);
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
                    Text = shouldUnpin
                        ? _localizationService.Format("Settings.Dialog.UnpinQuickAccessFailedBody", result.Error ?? string.Empty)
                        : _localizationService.Format("Settings.Dialog.PinQuickAccessFailedBody", result.Error ?? string.Empty),
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
                        Text = shouldUnpin
                            ? _localizationService.Format("Settings.Dialog.UnpinQuickAccessFailedBody", ex.Message)
                            : _localizationService.Format("Settings.Dialog.PinQuickAccessFailedBody", ex.Message),
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

    private void OpenRepositoryButton_Click(object sender, RoutedEventArgs e)
    {
        Win32Helper.OpenFile(ViewModel.OpenSourceRepositoryUrl);
    }

    private void OpenWebsiteButton_Click(object sender, RoutedEventArgs e)
    {
        Win32Helper.OpenFile(ViewModel.OfficialWebsiteLink);
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
        await ViewModel.OneClickUpdateActionAsync();
    }

    private void OpenManualUpdateDownloadButton_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(ViewModel.UpdateFallbackUrl))
        {
            Win32Helper.OpenFile(ViewModel.UpdateFallbackUrl);
        }
    }
}
