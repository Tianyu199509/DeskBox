using System.Diagnostics;
using System.Numerics;
using DeskBox.Controls;
using DeskBox.Helpers;
using DeskBox.Models;
using DeskBox.Services;
using DeskBox.ViewModels;
using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.System;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Media.Ocr;
using Windows.Graphics.Imaging;
using Microsoft.UI.Xaml.Controls.Primitives;
using WinRT;
using WinRT.Interop;

namespace DeskBox.Views;

public sealed partial class QuickCaptureWidgetWindow
{
    private Task EditItemAsync(QuickCaptureItemViewModel item)
    {
        if (item.Type == QuickCaptureItemType.Image)
        {
            return Task.CompletedTask;
        }

        _editingItem = item;
        QuickCaptureInlineEditor.Title = _localizationService.T("QuickCapture.Edit");
        QuickCaptureInlineEditor.Text = item.Body;
        QuickCaptureInlineEditor.Visibility = Visibility.Visible;
        QuickCaptureInlineEditor.FocusEditor(moveCaretToEnd: true);
        return Task.CompletedTask;
    }

    private async void EditSaveButton_Click(object sender, RoutedEventArgs e)
    {
        await SaveInlineEditAsync();
    }

    private void EditCancelButton_Click(object sender, RoutedEventArgs e)
    {
        CloseInlineEdit();
    }

    private async void EditTextBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Escape)
        {
            CloseInlineEdit();
            e.Handled = true;
            return;
        }

        if (e.Key == Windows.System.VirtualKey.Enter)
        {
            e.Handled = true;
            if (ShouldSubmitQuickCaptureEditor(e))
            {
                await SaveInlineEditAsync();
            }
            else
            {
                TextBoxEditorShortcutHelper.InsertLineBreak(EditTextBox);
            }
        }
    }

    private async Task SaveInlineEditAsync()
    {
        string body = QuickCaptureInlineEditor.Text;
        if (string.IsNullOrWhiteSpace(body))
        {
            ShowStatusToast(_localizationService.T("QuickCapture.EmptyEdit"));
            return;
        }

        if (_isExpandingInput)
        {
            InputTextBox.Text = body;
            await ViewModel.AddInputAsync();
            CloseInlineEdit();
            return;
        }

        if (_editingItem is not { } item)
        {
            CloseInlineEdit();
            return;
        }

        await ViewModel.EditItemAsync(item, body);
        CloseInlineEdit();
    }

    private void CloseInlineEdit(bool restoreInputFocus = true)
    {
        _editingItem = null;
        _isExpandingInput = false;
        QuickCaptureInlineEditor.Visibility = Visibility.Collapsed;
        QuickCaptureInlineEditor.Text = string.Empty;
        QuickCaptureInlineEditor.Title = _localizationService.T("QuickCapture.Edit");
        if (restoreInputFocus)
        {
            InputTextBox.Focus(FocusState.Programmatic);
        }
    }

    private async Task ConfirmClearDataAsync()
    {
        if (_isClearingData || RootGrid.XamlRoot is null)
        {
            return;
        }

        var dialog = new ContentDialog
        {
            XamlRoot = RootGrid.XamlRoot,
            Title = _localizationService.T("QuickCapture.ClearDataTitle"),
            Content = new TextBlock
            {
                Text = _localizationService.Format(
                    "QuickCapture.ClearDataDescriptionWithCount",
                    ViewModel.RecordCount,
                    ViewModel.RecentCount),
                TextWrapping = TextWrapping.Wrap
            },
            PrimaryButtonText = _localizationService.T("QuickCapture.ClearData"),
            CloseButtonText = _localizationService.T("Common.Cancel"),
            DefaultButton = ContentDialogButton.Close
        };
        ApplyQuickCaptureDialogSizing(dialog);

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        _isClearingData = true;
        try
        {
            await ViewModel.ClearAsync();
        }
        finally
        {
            _isClearingData = false;
        }
    }

    private async Task ClearDataAsync()
    {
        if (_isClearingData)
        {
            return;
        }

        _isClearingData = true;
        try
        {
            await ViewModel.ClearAsync();
        }
        finally
        {
            _isClearingData = false;
        }
    }

    private async Task ConfirmClearRecentAsync()
    {
        if (_isClearingData || RootGrid.XamlRoot is null)
        {
            return;
        }

        var dialog = new ContentDialog
        {
            XamlRoot = RootGrid.XamlRoot,
            Title = _localizationService.T("QuickCapture.ClearRecentTitle"),
            Content = new TextBlock
            {
                Text = _localizationService.Format(
                    "QuickCapture.ClearRecentDescriptionWithCount",
                    ViewModel.RecentCount),
                TextWrapping = TextWrapping.Wrap
            },
            PrimaryButtonText = _localizationService.T("QuickCapture.ClearRecent"),
            CloseButtonText = _localizationService.T("Common.Cancel"),
            DefaultButton = ContentDialogButton.Close
        };
        ApplyQuickCaptureDialogSizing(dialog);

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        _isClearingData = true;
        try
        {
            await ViewModel.ClearRecentAsync();
        }
        finally
        {
            _isClearingData = false;
        }
    }

    private async Task ClearRecentAsync()
    {
        if (_isClearingData)
        {
            return;
        }

        _isClearingData = true;
        try
        {
            await ViewModel.ClearRecentAsync();
        }
        finally
        {
            _isClearingData = false;
        }
    }

    private double GetQuickCaptureDialogWidth()
    {
        double rootWidth = RootGrid.ActualWidth;
        if (!double.IsFinite(rootWidth) || rootWidth <= 0)
        {
            rootWidth = ViewModel.Config.Width;
        }

        if (!double.IsFinite(rootWidth) || rootWidth <= 0)
        {
            rootWidth = SettingsService.DefaultWidgetWidth;
        }

        return Math.Clamp(
            rootWidth - QuickCaptureDialogHorizontalMargin,
            QuickCaptureDialogMinWidth,
            QuickCaptureDialogMaxWidth);
    }

    private void ApplyQuickCaptureDialogSizing(ContentDialog dialog, double? dialogWidth = null)
    {
        double compactWidth = dialogWidth ?? GetQuickCaptureDialogWidth();
        double buttonMinWidth = Math.Clamp(
            (compactWidth - 24) / 2,
            QuickCaptureDialogMinButtonWidth,
            QuickCaptureDialogMaxButtonWidth);

        dialog.Resources["ContentDialogMinWidth"] = compactWidth;
        dialog.Resources["ContentDialogMaxWidth"] = compactWidth;
        dialog.Resources["ContentDialogButtonMinWidth"] = buttonMinWidth;
    }

    private MenuFlyoutItem CreateToggleMenuItem(string text, string glyph, bool isChecked, Action<bool> applyValue)
    {
        var item = new ToggleMenuFlyoutItem
        {
            Text = text,
            Icon = new FontIcon { Glyph = glyph },
            IsChecked = isChecked
        };
        item.Click += (_, _) => applyValue(item.IsChecked);
        return item;
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        if (App.Current.WidgetManager is { } widgetManager)
        {
            var localization = App.Current.LocalizationService;
            var flyout = WidgetCompactConfirmationMenuBuilder.CreateDeleteConfirmation(
                new WidgetCompactConfirmationOptions(
                    localization.Format("Widget.FeatureWidget.DisableConfirmTitle", ViewModel.Config.Name),
                    localization.T("Widget.FeatureWidget.Disable"),
                    async () => await widgetManager.SetFeatureWidgetEnabledAsync(WidgetKind.QuickCapture, enabled: false, reveal: false))
                {
                    Message = localization.T("Widget.FeatureWidget.DisableConfirmNote"),
                    MessageGlyph = "\uE946",
                    IsDangerAction = false,
                    CancelText = localization.T("Common.Cancel")
                });
            ShowFlyoutWithElevation(flyout, QuickCaptureShell.CloseActionButton);
        }
    }

    private void QuickCaptureShell_TitleDoubleTapped(object? sender, DoubleTappedRoutedEventArgs e)
    {
        e.Handled = true;
        DispatcherQueue.TryEnqueue(StartTitleRename);
    }

    private void StartTitleRename()
    {
        if (_isDragging ||
            _isResizing ||
            QuickCaptureShell.TitleEditorContent is not null)
        {
            return;
        }

        _isCancellingTitleRename = false;
        BeginInteractionLayer("quick-title-rename-opened");
        var editor = CreateTitleRenameEditor();
        QuickCaptureShell.TitleEditorContent = editor;
        DispatcherQueue.TryEnqueue(() =>
        {
            editor.Focus(FocusState.Programmatic);
            editor.SelectAll();
        });
    }

    private TextBox CreateTitleRenameEditor()
    {
        double titleWidth = TitleText.ActualWidth > 0
            ? TitleText.ActualWidth + 36
            : (ViewModel.DisplayName.Length * 9.5) + 36;

        var editor = new TextBox
        {
            Text = ViewModel.DisplayName,
            PlaceholderText = _localizationService.T("Widget.TitlePlaceholder"),
            Width = Math.Clamp(titleWidth, 120, 220),
            MaxWidth = 220,
            FontSize = Math.Max(TitleText.FontSize - 1, 11),
            Style = GetTextBoxStyleResource("WidgetTitleRenameTextBoxStyle"),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center
        };

        editor.KeyDown += TitleRenameEditor_KeyDown;
        editor.LostFocus += TitleRenameEditor_LostFocus;
        return editor;
    }

    private static Style? GetTextBoxStyleResource(string resourceKey)
    {
        return Application.Current.Resources.TryGetValue(resourceKey, out object? resource) && resource is Style style
            ? style
            : null;
    }

    private async void TitleRenameEditor_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_isCancellingTitleRename)
        {
            _isCancellingTitleRename = false;
            return;
        }

        await CommitTitleRenameAsync();
    }

    private async void TitleRenameEditor_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter)
        {
            await CommitTitleRenameAsync();
            e.Handled = true;
        }
        else if (e.Key == VirtualKey.Escape)
        {
            CancelTitleRename();
            e.Handled = true;
        }
    }

    private async Task CommitTitleRenameAsync()
    {
        if (_isCommittingTitleRename ||
            QuickCaptureShell.TitleEditorContent is not TextBox editor)
        {
            return;
        }

        string newName = editor.Text.Trim();
        _isCommittingTitleRename = true;
        try
        {
            if (!string.IsNullOrEmpty(newName))
            {
                await ViewModel.RenameAsync(newName);
            }

            CompleteTitleRename("quick-title-rename-committed");
        }
        catch (Exception ex)
        {
            ShowStatusToast(ex.Message);
            editor.Focus(FocusState.Programmatic);
            editor.SelectAll();
        }
        finally
        {
            _isCommittingTitleRename = false;
        }
    }

    private void CancelTitleRename()
    {
        _isCancellingTitleRename = true;
        CompleteTitleRename("quick-title-rename-canceled");
    }

    private void CompleteTitleRename(string reason)
    {
        if (QuickCaptureShell.TitleEditorContent is TextBox editor)
        {
            editor.KeyDown -= TitleRenameEditor_KeyDown;
            editor.LostFocus -= TitleRenameEditor_LostFocus;
        }

        QuickCaptureShell.TitleEditorContent = null;
        ReleaseInteractionLayer(reason);
    }

    private void ShowFlyoutWithElevation(MenuFlyout flyout, FrameworkElement target, Windows.Foundation.Point? position = null)
    {
        BeginInteractionLayer("quick-flyout-opened");
        flyout.Closed += (_, _) =>
        {
            ReleaseInteractionLayer("quick-flyout-closed");
        };

        if (position is Windows.Foundation.Point point)
        {
            flyout.ShowAt(target, point);
        }
        else
        {
            flyout.ShowAt(target);
        }
    }

    private bool ShouldOpenTitleBarFlyout(object? originalSource)
    {
        if (originalSource is not DependencyObject source)
        {
            return true;
        }

        return !IsWithin(source, PositionLockButton) &&
               !IsWithin(source, SizeLockButton) &&
               !IsWithin(source, AddButton) &&
               !IsWithin(source, MoreButton) &&
               !IsWithin(source, CloseButton) &&
               !HasAncestorOfType<TextBox>(source);
    }

    private static bool IsWithin(DependencyObject source, DependencyObject target)
    {
        var current = source;
        while (current is not null)
        {
            if (ReferenceEquals(current, target))
            {
                return true;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return false;
    }

    private static bool IsItemActionSource(DependencyObject source)
    {
        var current = source;
        while (current is not null)
        {
            if (current is Button ||
                current is MenuFlyoutItem ||
                (current is FrameworkElement { Name: "ItemActionHost" }))
            {
                return true;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return false;
    }
}
