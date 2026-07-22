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
    private void OpenQuickCaptureSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        NavigateToSettingsSection("QuickCaptureSettings");
    }

    private void OpenTodoSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        NavigateToSettingsSection("TodoSettings");
    }

    private void OpenAppearanceDetailButton_Click(object sender, RoutedEventArgs e)
    {
        NavigateToSettingsSection("AppearanceDetail");
    }

    private async void ClearQuickCaptureDataButton_Click(object sender, RoutedEventArgs e)
    {
        if (SettingsRoot.XamlRoot is null)
        {
            return;
        }

        var data = await App.Current.QuickCaptureService.GetDataAsync();
        int recordCount = data.Items.Count(item => !item.IsDeleted);
        int recentCount = data.RecentItems.Count(item => !item.IsDeleted);
        var dialog = new ContentDialog
        {
            XamlRoot = SettingsRoot.XamlRoot,
            Title = _localizationService.T("QuickCapture.ClearDataTitle"),
            PrimaryButtonText = _localizationService.T("QuickCapture.ClearData"),
            CloseButtonText = _localizationService.T("Common.Cancel"),
            DefaultButton = ContentDialogButton.Close,
            Content = new TextBlock
            {
                Text = _localizationService.Format(
                    "QuickCapture.ClearDataDescriptionWithCount",
                    recordCount,
                    recentCount),
                TextWrapping = TextWrapping.Wrap
            }
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        await App.Current.QuickCaptureService.ClearAsync();
        await ViewModel.RefreshQuickCaptureImageCacheInfoAsync();
    }

    private async void ClearQuickCaptureRecentButton_Click(object sender, RoutedEventArgs e)
    {
        if (SettingsRoot.XamlRoot is null)
        {
            return;
        }

        var data = await App.Current.QuickCaptureService.GetDataAsync();
        int recentCount = data.RecentItems.Count(item => !item.IsDeleted);
        var dialog = new ContentDialog
        {
            XamlRoot = SettingsRoot.XamlRoot,
            Title = _localizationService.T("QuickCapture.ClearRecentTitle"),
            PrimaryButtonText = _localizationService.T("QuickCapture.ClearRecent"),
            CloseButtonText = _localizationService.T("Common.Cancel"),
            DefaultButton = ContentDialogButton.Close,
            Content = new TextBlock
            {
                Text = _localizationService.Format(
                    "QuickCapture.ClearRecentDescriptionWithCount",
                    recentCount),
                TextWrapping = TextWrapping.Wrap
            }
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        await App.Current.QuickCaptureService.ClearRecentAsync();
        await ViewModel.RefreshQuickCaptureImageCacheInfoAsync();
    }

    private async void CleanupQuickCaptureImageCacheButton_Click(object sender, RoutedEventArgs e)
    {
        if (SettingsRoot.XamlRoot is null)
        {
            return;
        }

        var result = await App.Current.QuickCaptureService.CleanupUnusedImageCacheAsync();
        await ViewModel.RefreshQuickCaptureImageCacheInfoAsync();

        var dialog = new ContentDialog
        {
            XamlRoot = SettingsRoot.XamlRoot,
            Title = _localizationService.T("Settings.QuickCapture.ImageCacheCleanupTitle"),
            CloseButtonText = _localizationService.T("Common.Ok"),
            DefaultButton = ContentDialogButton.Close,
            Content = new TextBlock
            {
                Text = _localizationService.Format(
                    "Settings.QuickCapture.ImageCacheCleanupDescription",
                    result.DeletedFileCount,
                    ViewModel.FormatBytes(result.DeletedBytes)),
                TextWrapping = TextWrapping.Wrap
            }
        };

        await dialog.ShowAsync();
    }

    private void ShowOnboardingButton_Click(object sender, RoutedEventArgs e)
    {
        App.Current.ShowOnboarding();
    }

    private async void ShowProductReasonButton_Click(object sender, RoutedEventArgs e)
    {
        if (SettingsRoot.XamlRoot is null)
        {
            return;
        }

        var content = new StackPanel
        {
            MaxWidth = 560,
            Spacing = 16
        };

        for (int index = 1; index <= 5; index++)
        {
            content.Children.Add(CreateDialogParagraph(
                _localizationService.T($"Settings.Dialog.ProductReasonP{index}")));
        }

        var dialog = new ContentDialog
        {
            XamlRoot = SettingsRoot.XamlRoot,
            Title = _localizationService.T("Settings.About.ReasonTitle"),
            CloseButtonText = _localizationService.T("Settings.Dialog.ProductReasonClose"),
            DefaultButton = ContentDialogButton.Close,
            Content = content
        };

        await dialog.ShowAsync();
    }

    private void CleanupManagedStorageButton_Click(object sender, RoutedEventArgs e)
    {
        NavigateToSettingsSection("ManagedStorage");
    }

    private void RefreshManagedStorageButton_Click(object sender, RoutedEventArgs e)
    {
        RefreshManagedStorageFolderList();
    }

    private void RefreshManagedStorageFolderList()
    {
        ManagedStorageFolderList.Children.Clear();

        if (App.Current.WidgetManager is not { } widgetManager)
        {
            ManagedStorageEmptyState.Visibility = Visibility.Visible;
            ManagedStorageFolderList.Visibility = Visibility.Collapsed;
            ManagedStorageSummaryText.Text = _localizationService.T("Settings.ManagedStorage.SummaryUnavailable");
            return;
        }

        var candidates = widgetManager.GetOrphanManagedStorageFolders();
        bool hasCandidates = candidates.Count > 0;
        ManagedStorageEmptyState.Visibility = hasCandidates ? Visibility.Collapsed : Visibility.Visible;
        ManagedStorageFolderList.Visibility = hasCandidates ? Visibility.Visible : Visibility.Collapsed;
        ManagedStorageSummaryText.Text = hasCandidates
            ? _localizationService.Format("Settings.ManagedStorage.Summary", candidates.Count)
            : _localizationService.T("Settings.ManagedStorage.SummaryEmpty");

        for (int index = 0; index < candidates.Count; index++)
        {
            if (index > 0)
            {
                ManagedStorageFolderList.Children.Add(CreateSettingDivider());
            }

            ManagedStorageFolderList.Children.Add(CreateManagedStorageFolderRow(candidates[index]));
        }

        DispatcherQueue.TryEnqueue(() =>
        {
            CollectResponsiveRows(SettingsRoot);
            UpdateResponsiveLayout(GetWindowWidth());
        });
    }

    private Grid CreateManagedStorageFolderRow(ManagedStorageFolderCleanupCandidate candidate)
    {
        var row = new Grid
        {
            Style = (Style)SettingsRoot.Resources["SettingRowStyle"]
        };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var textPanel = new StackPanel
        {
            Style = (Style)SettingsRoot.Resources["SettingTextPanelStyle"]
        };
        textPanel.Children.Add(new TextBlock
        {
            Text = candidate.Name,
            Style = (Style)SettingsRoot.Resources["SettingTitleTextStyle"]
        });
        textPanel.Children.Add(new TextBlock
        {
            Text = _localizationService.Format("Settings.ManagedStorage.ItemCount", candidate.ItemCount),
            Style = (Style)SettingsRoot.Resources["SettingDescriptionTextStyle"]
        });
        textPanel.Children.Add(new TextBlock
        {
            Text = candidate.Path,
            MaxLines = 1,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Style = (Style)SettingsRoot.Resources["SettingDescriptionTextStyle"]
        });

        var actionsPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            Spacing = 8
        };

        string folderPath = candidate.Path;
        string folderName = candidate.Name;
        actionsPanel.Children.Add(CreateManagedStorageActionButton(
            "Settings.ManagedStorage.RestoreAction",
            "Settings.ManagedStorage.RestoreTooltip",
            async () => await RestoreManagedStorageFolderAsync(folderPath)));
        actionsPanel.Children.Add(CreateManagedStorageActionButton(
            "Settings.ManagedStorage.OpenAction",
            "Settings.ManagedStorage.OpenTooltip",
            async () => await OpenManagedStorageFolderAsync(folderPath)));
        actionsPanel.Children.Add(CreateManagedStorageActionButton(
            "Settings.ManagedStorage.MoveAction",
            "Settings.ManagedStorage.MoveTooltip",
            async () => await MoveManagedStorageFolderToDesktopAsync(folderPath, folderName)));
        actionsPanel.Children.Add(CreateManagedStorageActionButton(
            "Settings.ManagedStorage.DeleteAction",
            "Settings.ManagedStorage.DeleteTooltip",
            async () => await DeleteManagedStorageFolderAsync(folderPath, folderName)));

        row.Children.Add(textPanel);
        row.Children.Add(actionsPanel);
        Grid.SetColumn(actionsPanel, 1);

        return row;
    }

    private Button CreateManagedStorageActionButton(string textKey, string tooltipKey, Func<Task> action)
    {
        var button = new Button
        {
            Style = (Style)SettingsRoot.Resources["CompactTextActionButtonStyle"],
            Content = _localizationService.T(textKey)
        };
        ToolTipService.SetToolTip(button, _localizationService.T(tooltipKey));
        button.Click += async (_, _) => await action();
        return button;
    }

    private Border CreateSettingDivider()
    {
        return new Border
        {
            Style = (Style)SettingsRoot.Resources["SettingDividerStyle"]
        };
    }

    private async Task RestoreManagedStorageFolderAsync(string folderPath)
    {
        if (App.Current.WidgetManager is null)
        {
            return;
        }

        try
        {
            int restoredCount = await App.Current.WidgetManager.RestoreOrphanManagedStorageFoldersAsync([folderPath]);
            RefreshManagedStorageFolderList();
            await ShowInfoDialogAsync(
                _localizationService.T("Settings.ManagedStorage.RestoreCompleteTitle"),
                _localizationService.Format("Settings.ManagedStorage.RestoreCompleteBody", restoredCount));
        }
        catch (Exception ex)
        {
            RefreshManagedStorageFolderList();
            await ShowInfoDialogAsync(
                _localizationService.T("Settings.ManagedStorage.ActionFailedTitle"),
                _localizationService.Format("Settings.ManagedStorage.ActionFailedBody", ex.Message));
        }
    }

    private async Task OpenManagedStorageFolderAsync(string folderPath)
    {
        if (!Directory.Exists(folderPath))
        {
            RefreshManagedStorageFolderList();
            await ShowInfoDialogAsync(
                _localizationService.T("Settings.ManagedStorage.ActionFailedTitle"),
                _localizationService.T("Settings.ManagedStorage.MissingFolder"));
            return;
        }

        Win32Helper.OpenFile(folderPath);
    }

    private async Task MoveManagedStorageFolderToDesktopAsync(string folderPath, string folderName)
    {
        if (App.Current.WidgetManager is null ||
            !await ConfirmManagedStorageActionAsync(
                _localizationService.T("Settings.ManagedStorage.MoveConfirmTitle"),
                _localizationService.Format("Settings.ManagedStorage.MoveConfirmBody", folderName),
                _localizationService.T("Common.Move")))
        {
            return;
        }

        try
        {
            await App.Current.WidgetManager.MoveOrphanManagedStorageFolderContentsToDesktopAsync(folderPath);
            RefreshManagedStorageFolderList();
            await ShowInfoDialogAsync(
                _localizationService.T("Settings.ManagedStorage.MoveCompleteTitle"),
                _localizationService.Format("Settings.ManagedStorage.MoveCompleteBody", folderName));
        }
        catch (Exception ex)
        {
            RefreshManagedStorageFolderList();
            await ShowInfoDialogAsync(
                _localizationService.T("Settings.ManagedStorage.ActionFailedTitle"),
                _localizationService.Format("Settings.ManagedStorage.ActionFailedBody", ex.Message));
        }
    }

    private async Task DeleteManagedStorageFolderAsync(string folderPath, string folderName)
    {
        if (App.Current.WidgetManager is null ||
            !await ConfirmManagedStorageActionAsync(
                _localizationService.T("Settings.ManagedStorage.DeleteConfirmTitle"),
                _localizationService.Format("Settings.ManagedStorage.DeleteConfirmBody", folderName),
                _localizationService.T("Common.Delete")))
        {
            return;
        }

        try
        {
            await App.Current.WidgetManager.DeleteOrphanManagedStorageFolderAsync(folderPath);
            RefreshManagedStorageFolderList();
            await ShowInfoDialogAsync(
                _localizationService.T("Settings.ManagedStorage.DeleteCompleteTitle"),
                _localizationService.Format("Settings.ManagedStorage.DeleteCompleteBody", folderName));
        }
        catch (Exception ex)
        {
            RefreshManagedStorageFolderList();
            await ShowInfoDialogAsync(
                _localizationService.T("Settings.ManagedStorage.ActionFailedTitle"),
                _localizationService.Format("Settings.ManagedStorage.ActionFailedBody", ex.Message));
        }
    }

    private async Task<bool> ConfirmManagedStorageActionAsync(string title, string message, string primaryButtonText)
    {
        if (SettingsRoot.XamlRoot is null)
        {
            return false;
        }

        var dialog = new ContentDialog
        {
            XamlRoot = SettingsRoot.XamlRoot,
            Title = title,
            PrimaryButtonText = primaryButtonText,
            CloseButtonText = _localizationService.T("Common.Cancel"),
            DefaultButton = ContentDialogButton.Close,
            Content = new TextBlock
            {
                Text = message,
                TextWrapping = TextWrapping.Wrap
            }
        };

        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    private async Task ShowInfoDialogAsync(string title, string message)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = SettingsRoot.XamlRoot,
            Title = title,
            CloseButtonText = _localizationService.T("Common.Ok"),
            DefaultButton = ContentDialogButton.Close,
            Content = new TextBlock
            {
                Text = message,
                TextWrapping = TextWrapping.Wrap
            }
        };

        await dialog.ShowAsync();
    }
}
