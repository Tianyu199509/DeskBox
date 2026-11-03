using DeskBox.Controls.Todo;
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
    private async void ClearQuickCaptureDataButton_Click(object sender, RoutedEventArgs e)
    {
        if (SettingsRoot.XamlRoot is null)
        {
            return;
        }

        var (recordCount, _) = await _dataActions.GetQuickCaptureCountsAsync();
        var dialog = new ContentDialog
        {
            XamlRoot = SettingsRoot.XamlRoot,
            Title = _localizationService.T("QuickCapture.ClearDataTitle"),
            PrimaryButtonText = _localizationService.T("QuickCapture.ClearData"),
            CloseButtonText = _localizationService.T("Common.Cancel"),
            DefaultButton = ContentDialogButton.Close,
            Content = new TextBlock
            {
                Text = recordCount > 0
                    ? _localizationService.Format(
                        "QuickCapture.ClearDataDescriptionWithCount",
                        recordCount)
                    : _localizationService.T("QuickCapture.ClearDataDescription"),
                TextWrapping = TextWrapping.Wrap
            }
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        await _dataActions.ClearQuickCaptureDataAsync();
        await ViewModel.RefreshQuickCaptureImageCacheInfoAsync();
    }

    private async void CleanupQuickCaptureImageCacheButton_Click(object sender, RoutedEventArgs e)
    {
        if (SettingsRoot.XamlRoot is null)
        {
            return;
        }

        var result = await _dataActions.ClearUnusedQuickCaptureImageCacheAsync();
        await ViewModel.RefreshQuickCaptureImageCacheInfoAsync();

        var dialog = new ContentDialog
        {
            XamlRoot = SettingsRoot.XamlRoot,
            Title = _localizationService.T("Settings.QuickCapture.ImageCacheCleanupTitle"),
            CloseButtonText = _localizationService.T("Common.GotIt"),
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

    private async void QuickCaptureMirrorFolderBrowseButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            string? folderPath = await FolderPickerService.PickFolderAsync(_hWnd);
            if (string.IsNullOrWhiteSpace(folderPath))
            {
                return;
            }

            await _dataActions.SetQuickCaptureMirrorFolderAsync(folderPath);
            _quickCaptureSettingsViewModel.UpdateMirrorFolderPresentation(folderPath);
        }
        catch (Exception ex)
        {
            App.Log($"[QuickCaptureSettings] Failed to pick mirror folder: {ex}");
        }
    }

    private void QuickCaptureMirrorFolderOpenButton_Click(object sender, RoutedEventArgs e)
    {
        string? folderPath = _quickCaptureSettingsViewModel.MirrorFolderPath;
        if (string.IsNullOrWhiteSpace(folderPath))
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(folderPath);
            Win32Helper.ShowInExplorer(folderPath);
        }
        catch (Exception ex)
        {
            App.Log($"[QuickCaptureSettings] Failed to open mirror folder: {ex}");
        }
    }

    private async void QuickCaptureMirrorFolderClearButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await _dataActions.SetQuickCaptureMirrorFolderAsync(null);
            _quickCaptureSettingsViewModel.UpdateMirrorFolderPresentation(null);
        }
        catch (Exception ex)
        {
            App.Log($"[QuickCaptureSettings] Failed to clear mirror folder: {ex}");
        }
    }

    private void ShowOnboardingButton_Click(object sender, RoutedEventArgs e)
    {
        _appActions.ShowOnboarding();
    }

    private async void ShowAboutMeButton_Click(object sender, RoutedEventArgs e)
    {
        if (SettingsRoot.XamlRoot is null)
        {
            return;
        }

        AboutMeDialog.XamlRoot = SettingsRoot.XamlRoot;
        AboutMeDialog.CloseButtonText = _localizationService.T("Settings.Dialog.AboutMeClose");
        await AboutMeDialog.ShowAsync();
    }

    private ContentDialog? _activeOrphanFoldersDialog;

    private async Task<IReadOnlyList<ManagedStorageFolderCleanupCandidate>?> RefreshOrphanFoldersSummaryAsync()
    {
        // Deep review R18 (A-#24): run the recursive candidate scan on a pool thread.
        IReadOnlyList<ManagedStorageFolderCleanupCandidate>? candidates =
            await Task.Run(() => _widgetActions.GetOrphanManagedStorageFolders());

        if (_isClosed ||
            !_settingsSectionElements.TryGetValue("AppearanceDetail", out FrameworkElement? el) ||
            el is not Views.SettingsSections.FileWidgetSettingsSection section)
        {
            return candidates;
        }

        if (candidates is not { } list)
        {
            section.SetOrphanFoldersState(
                0,
                _localizationService.T("Settings.ManagedStorage.SummaryUnavailable"),
                _localizationService.T("Common.Manage"));
            return null;
        }

        string summary = list.Count > 0
            ? _localizationService.Format("Settings.ManagedStorage.Summary", list.Count)
            : _localizationService.T("Settings.ManagedStorage.SummaryEmpty");
        section.SetOrphanFoldersState(
            list.Count,
            summary,
            _localizationService.T("Common.Manage"));
        return list;
    }

    private void ManageOrphanFoldersButton_Click(object? sender, RoutedEventArgs e)
    {
        _ = ShowManageOrphanFoldersDialogAsync();
    }

    private async Task ShowManageOrphanFoldersDialogAsync()
    {
        if (SettingsRoot.XamlRoot is null)
        {
            return;
        }

        IReadOnlyList<ManagedStorageFolderCleanupCandidate>? list = await RefreshOrphanFoldersSummaryAsync();
        if (list is not { Count: > 0 } || SettingsRoot.XamlRoot is null)
        {
            return;
        }

        var listPanel = new StackPanel { Spacing = 8 };
        for (int index = 0; index < list.Count; index++)
        {
            if (index > 0)
            {
                listPanel.Children.Add(CreateSettingDivider());
            }

            listPanel.Children.Add(CreateManagedStorageFolderRow(list[index]));
        }

        var dialog = new ContentDialog
        {
            XamlRoot = SettingsRoot.XamlRoot,
            RequestedTheme = SettingsRoot.ActualTheme,
            Title = _localizationService.T("Settings.ManagedStorage.OrphanRow.Title"),
            CloseButtonText = _localizationService.T("Common.Close"),
            DefaultButton = ContentDialogButton.Close,
            MinWidth = 540,
            Content = new ScrollViewer
            {
                MaxHeight = 420,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Content = listPanel
            }
        };

        _activeOrphanFoldersDialog = dialog;
        try
        {
            await ShowDialogWhenFreeAsync(() => dialog);
        }
        finally
        {
            if (ReferenceEquals(_activeOrphanFoldersDialog, dialog))
            {
                _activeOrphanFoldersDialog = null;
            }
        }
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
        if (!_widgetActions.IsHostAvailable)
        {
            return;
        }

        bool reopenDialog = _activeOrphanFoldersDialog is not null;
        _activeOrphanFoldersDialog?.Hide();
        try
        {
            int restoredCount = await _widgetActions.RestoreOrphanManagedStorageFoldersAsync([folderPath]);
            var remaining = await RefreshOrphanFoldersSummaryAsync();
            await ShowInfoDialogAsync(
                _localizationService.T("Settings.ManagedStorage.RestoreCompleteTitle"),
                _localizationService.Format("Settings.ManagedStorage.RestoreCompleteBody", restoredCount));
            if (reopenDialog && remaining is { Count: > 0 })
            {
                await ShowManageOrphanFoldersDialogAsync();
            }
        }
        catch (Exception ex)
        {
            var remaining = await RefreshOrphanFoldersSummaryAsync();
            await ShowInfoDialogAsync(
                _localizationService.T("Settings.ManagedStorage.ActionFailedTitle"),
                _localizationService.Format("Settings.ManagedStorage.ActionFailedBody", ex.Message));
            if (reopenDialog && remaining is { Count: > 0 })
            {
                await ShowManageOrphanFoldersDialogAsync();
            }
        }
    }

    private async Task OpenManagedStorageFolderAsync(string folderPath)
    {
        if (!Directory.Exists(folderPath))
        {
            bool reopenDialog = _activeOrphanFoldersDialog is not null;
            _activeOrphanFoldersDialog?.Hide();
            var remaining = await RefreshOrphanFoldersSummaryAsync();
            await ShowInfoDialogAsync(
                _localizationService.T("Settings.ManagedStorage.ActionFailedTitle"),
                _localizationService.T("Settings.ManagedStorage.MissingFolder"));
            if (reopenDialog && remaining is { Count: > 0 })
            {
                await ShowManageOrphanFoldersDialogAsync();
            }
            return;
        }

        Win32Helper.OpenFile(folderPath);
    }

    private async Task MoveManagedStorageFolderToDesktopAsync(string folderPath, string folderName)
    {
        if (!_widgetActions.IsHostAvailable)
        {
            return;
        }

        bool reopenDialog = _activeOrphanFoldersDialog is not null;
        _activeOrphanFoldersDialog?.Hide();
        if (!await ConfirmManagedStorageActionAsync(
                _localizationService.T("Settings.ManagedStorage.MoveConfirmTitle"),
                _localizationService.Format("Settings.ManagedStorage.MoveConfirmBody", folderName),
                _localizationService.T("Common.Move")))
        {
            if (reopenDialog)
            {
                await ShowManageOrphanFoldersDialogAsync();
            }
            return;
        }

        try
        {
            await _widgetActions.MoveOrphanManagedStorageFolderContentsToDesktopAsync(folderPath);
            var remaining = await RefreshOrphanFoldersSummaryAsync();
            await ShowInfoDialogAsync(
                _localizationService.T("Settings.ManagedStorage.MoveCompleteTitle"),
                _localizationService.Format("Settings.ManagedStorage.MoveCompleteBody", folderName));
            if (reopenDialog && remaining is { Count: > 0 })
            {
                await ShowManageOrphanFoldersDialogAsync();
            }
        }
        catch (Exception ex)
        {
            var remaining = await RefreshOrphanFoldersSummaryAsync();
            await ShowInfoDialogAsync(
                _localizationService.T("Settings.ManagedStorage.ActionFailedTitle"),
                _localizationService.Format("Settings.ManagedStorage.ActionFailedBody", ex.Message));
            if (reopenDialog && remaining is { Count: > 0 })
            {
                await ShowManageOrphanFoldersDialogAsync();
            }
        }
    }

    private async Task DeleteManagedStorageFolderAsync(string folderPath, string folderName)
    {
        if (!_widgetActions.IsHostAvailable)
        {
            return;
        }

        bool reopenDialog = _activeOrphanFoldersDialog is not null;
        _activeOrphanFoldersDialog?.Hide();
        if (!await ConfirmManagedStorageActionAsync(
                _localizationService.T("Settings.ManagedStorage.DeleteConfirmTitle"),
                _localizationService.Format("Settings.ManagedStorage.DeleteConfirmBody", folderName),
                _localizationService.T("Common.Delete")))
        {
            if (reopenDialog)
            {
                await ShowManageOrphanFoldersDialogAsync();
            }
            return;
        }

        try
        {
            await _widgetActions.DeleteOrphanManagedStorageFolderAsync(
                folderPath,
                _hWnd);
            var remaining = await RefreshOrphanFoldersSummaryAsync();
            await ShowInfoDialogAsync(
                _localizationService.T("Settings.ManagedStorage.DeleteCompleteTitle"),
                _localizationService.Format("Settings.ManagedStorage.DeleteCompleteBody", folderName));
            if (reopenDialog && remaining is { Count: > 0 })
            {
                await ShowManageOrphanFoldersDialogAsync();
            }
        }
        catch (Exception ex)
        {
            var remaining = await RefreshOrphanFoldersSummaryAsync();
            await ShowInfoDialogAsync(
                _localizationService.T("Settings.ManagedStorage.ActionFailedTitle"),
                _localizationService.Format("Settings.ManagedStorage.ActionFailedBody", ex.Message));
            if (reopenDialog && remaining is { Count: > 0 })
            {
                await ShowManageOrphanFoldersDialogAsync();
            }
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
            CloseButtonText = _localizationService.T("Common.GotIt"),
            DefaultButton = ContentDialogButton.Close,
            Content = new TextBlock
            {
                Text = message,
                TextWrapping = TextWrapping.Wrap
            }
        };

        // Coordinator-routed like the update dialogs: this surfaces failure
        // notices whose triggers can land while another dialog is open.
        await ShowDialogWhenFreeAsync(() => dialog);
    }

    private async void ExportTodoDataButton_Click(object sender, RoutedEventArgs e)
    {
        if (SettingsRoot.XamlRoot is null)
        {
            return;
        }

        IReadOnlyList<TodoItem> items;
        try
        {
            items = await TodoExportService.LoadPrimaryTodoItemsAsync(
                DeskBoxDataPathService.Current.DataDirectory);
        }
        catch (Exception ex)
        {
            App.Log($"[TodoExport] Failed to load primary Todo items in Settings: {ex}");
            await ShowInfoDialogAsync(
                _localizationService.T("Todo.Export.DialogTitle"),
                _localizationService.Format("Todo.Export.Status.Failed", ex.Message));
            return;
        }

        if (items.Count == 0)
        {
            await ShowInfoDialogAsync(
                _localizationService.T("Todo.Export.DialogTitle"),
                _localizationService.T("Todo.Export.NoTasksInStore"));
            return;
        }

        var panel = new TodoExportPanel(
            items,
            _localizationService,
            initialScope: TodoExportStatusScope.All,
            isSelectionSubset: false,
            showFlyoutChrome: false);

        var configDialog = new ContentDialog
        {
            XamlRoot = SettingsRoot.XamlRoot,
            Title = _localizationService.T("Todo.Export.DialogTitle"),
            PrimaryButtonText = _localizationService.T("Todo.Export.ConfirmButton"),
            CloseButtonText = _localizationService.T("Common.Cancel"),
            DefaultButton = ContentDialogButton.Primary,
            IsPrimaryButtonEnabled = panel.CanExport,
            Content = panel
        };

        panel.PreviewChanged += (_, _) =>
        {
            configDialog.IsPrimaryButtonEnabled = panel.CanExport;
        };

        if (await configDialog.ShowAsync() != ContentDialogResult.Primary || !panel.CanExport)
        {
            return;
        }

        TodoExportOptions chosenOptions = panel.CurrentOptions;
        TodoItem[] itemsToExport = panel.CurrentPreview.Items.ToArray();

        string? savedPath;
        try
        {
            savedPath = await TodoExportWorkflow.PickAndExportAsync(
                _hWnd,
                itemsToExport,
                chosenOptions,
                _localizationService);
        }
        catch (Exception ex)
        {
            App.Log($"[TodoExport] Export failed in Settings: {ex}");
            await ShowInfoDialogAsync(
                _localizationService.T("Todo.Export.DialogTitle"),
                _localizationService.Format("Todo.Export.Status.Failed", ex.Message));
            return;
        }

        if (string.IsNullOrWhiteSpace(savedPath) || SettingsRoot.XamlRoot is null)
        {
            return;
        }

        var doneContent = new StackPanel { Spacing = 6 };
        doneContent.Children.Add(new TextBlock
        {
            Text = _localizationService.Format("Todo.Export.Status.Exported", itemsToExport.Length),
            TextWrapping = TextWrapping.Wrap
        });
        doneContent.Children.Add(new TextBlock
        {
            Text = savedPath,
            FontSize = 12,
            Opacity = 0.72,
            TextWrapping = TextWrapping.Wrap
        });

        var doneDialog = new ContentDialog
        {
            XamlRoot = SettingsRoot.XamlRoot,
            Title = _localizationService.T("Todo.Export.CompletedDialogTitle"),
            PrimaryButtonText = _localizationService.T("Todo.Export.Action.OpenFile"),
            SecondaryButtonText = _localizationService.T("Todo.Export.Action.RevealInFolder"),
            CloseButtonText = _localizationService.T("Common.GotIt"),
            DefaultButton = ContentDialogButton.Primary,
            Content = doneContent
        };

        ContentDialogResult doneResult = await doneDialog.ShowAsync();
        if (doneResult == ContentDialogResult.Primary)
        {
            TodoExportWorkflow.OpenExportedFile(savedPath);
        }
        else if (doneResult == ContentDialogResult.Secondary)
        {
            TodoExportWorkflow.RevealExportedFile(savedPath);
        }
    }
}
