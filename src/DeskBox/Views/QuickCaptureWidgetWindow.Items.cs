using System.Diagnostics;
using System.Numerics;
using DeskBox.Controls;
using DeskBox.Helpers;
using DeskBox.Models;
using DeskBox.Platform;
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
    private async void ItemsListView_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if ((e.OriginalSource as FrameworkElement)?.DataContext is not QuickCaptureItemViewModel item)
        {
            return;
        }

        await OpenItemInDefaultAppAsync(item);
    }

    private async Task OpenItemInDefaultAppAsync(QuickCaptureItemViewModel item)
    {
        if (item.Type == QuickCaptureItemType.Image)
        {
            await OpenImageInDefaultViewerAsync(item);
            return;
        }

        if (item.Type == QuickCaptureItemType.Link &&
            Uri.TryCreate(item.Url ?? item.Body, UriKind.Absolute, out var uri))
        {
            if (!await Launcher.LaunchUriAsync(uri))
            {
                ShowStatusToast(_localizationService.T("QuickCapture.OpenItemFailed"));
            }

            return;
        }

        if (item.IsRecent)
        {
            await OpenTextInDefaultEditorAsync(item);
            return;
        }

        await EditItemAsync(item);
    }

    private async Task OpenImageInDefaultViewerAsync(QuickCaptureItemViewModel item)
    {
        if (item.Type != QuickCaptureItemType.Image ||
            string.IsNullOrWhiteSpace(item.ImagePath) ||
            !File.Exists(item.ImagePath))
        {
            ShowStatusToast(_localizationService.T("QuickCapture.OpenImageFailed"));
            return;
        }

        try
        {
            var file = await StorageFile.GetFileFromPathAsync(item.ImagePath);
            if (!await Launcher.LaunchFileAsync(file))
            {
                ShowStatusToast(_localizationService.T("QuickCapture.OpenImageFailed"));
            }
        }
        catch (Exception ex)
        {
            App.Log($"[QuickCaptureWidget] Failed to open image preview: {ex}");
            ShowStatusToast(_localizationService.T("QuickCapture.OpenImageFailed"));
        }
    }

    private async Task OpenTextInNotepadAsync(QuickCaptureItemViewModel item)
    {
        if (item.IsRecent)
        {
            return;
        }

        string? previewPath = await TryCreateTextPreviewFileAsync(item);
        if (string.IsNullOrWhiteSpace(previewPath))
        {
            return;
        }

        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "notepad.exe",
                Arguments = $"\"{previewPath}\"",
                UseShellExecute = true
            });

            if (process is null)
            {
                ShowStatusToast(_localizationService.T("QuickCapture.OpenItemFailed"));
                return;
            }

            await process.WaitForExitAsync();
            await TryApplyTextPreviewEditAsync(item, previewPath);
        }
        catch (Exception ex)
        {
            App.Log($"[QuickCaptureWidget] Failed to open text in notepad: {ex}");
            ShowStatusToast(_localizationService.T("QuickCapture.OpenItemFailed"));
        }
    }

    private async Task OpenTextInDefaultEditorAsync(QuickCaptureItemViewModel item)
    {
        string? previewPath = await TryCreateTextPreviewFileAsync(item);
        if (string.IsNullOrWhiteSpace(previewPath))
        {
            return;
        }

        try
        {
            var file = await StorageFile.GetFileFromPathAsync(previewPath);
            if (!await Launcher.LaunchFileAsync(file))
            {
                ShowStatusToast(_localizationService.T("QuickCapture.OpenItemFailed"));
            }
        }
        catch (Exception ex)
        {
            App.Log($"[QuickCaptureWidget] Failed to open text preview: {ex}");
            ShowStatusToast(_localizationService.T("QuickCapture.OpenItemFailed"));
        }
    }

    private async Task TryApplyTextPreviewEditAsync(QuickCaptureItemViewModel item, string previewPath)
    {
        if (item.IsRecent)
        {
            return;
        }

        try
        {
            string editedBody = await File.ReadAllTextAsync(previewPath);
            if (string.Equals(editedBody, item.Body, StringComparison.Ordinal))
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(editedBody))
            {
                ShowStatusToast(_localizationService.T("QuickCapture.EmptyEdit"));
                return;
            }

            await ViewModel.EditItemAsync(item, editedBody);
            ShowStatusToast(_localizationService.T("QuickCapture.Edited"));
        }
        catch (Exception ex)
        {
            App.Log($"[QuickCaptureWidget] Failed to apply notepad edit: {ex}");
            ShowStatusToast(_localizationService.T("QuickCapture.OpenItemFailed"));
        }
    }

    private async Task<string?> TryCreateTextPreviewFileAsync(QuickCaptureItemViewModel item)
    {
        if (string.IsNullOrWhiteSpace(item.Body))
        {
            ShowStatusToast(_localizationService.T("QuickCapture.OpenItemFailed"));
            return null;
        }

        try
        {
            Directory.CreateDirectory(QuickCaptureTextPreviewDirectory);
            string fileName = BuildQuickCapturePreviewFileName(item);
            string previewPath = Path.Combine(QuickCaptureTextPreviewDirectory, fileName);
            await File.WriteAllTextAsync(previewPath, item.Body);
            return previewPath;
        }
        catch (Exception ex)
        {
            App.Log($"[QuickCaptureWidget] Failed to create text preview: {ex}");
            ShowStatusToast(_localizationService.T("QuickCapture.OpenItemFailed"));
            return null;
        }
    }

    private static string BuildQuickCapturePreviewFileName(QuickCaptureItemViewModel item)
    {
        string stem = item.Body
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault() ?? "Quick Capture";
        stem = SanitizeQuickCapturePreviewFileName(stem);
        if (string.IsNullOrWhiteSpace(stem))
        {
            stem = "Quick Capture";
        }

        if (stem.Length > 42)
        {
            stem = stem[..42].Trim();
        }

        return $"{stem}-{item.Id[..Math.Min(8, item.Id.Length)]}.txt";
    }

    private static string SanitizeQuickCapturePreviewFileName(string fileName)
    {
        foreach (char invalidChar in Path.GetInvalidFileNameChars())
        {
            fileName = fileName.Replace(invalidChar, ' ');
        }

        return string.Join(" ", fileName.Split(' ', StringSplitOptions.RemoveEmptyEntries)).Trim();
    }

    private async void ItemsListView_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        bool isCtrlPressed = Win32Helper.IsKeyPressed(Windows.System.VirtualKey.Control);
        if (isCtrlPressed && e.Key == Windows.System.VirtualKey.A)
        {
            SelectAllVisibleQuickCaptureItems();
            e.Handled = true;
            return;
        }

        if (isCtrlPressed && e.Key == Windows.System.VirtualKey.C)
        {
            await CopySelectedQuickCaptureItemsAsync();
            e.Handled = true;
            return;
        }

        if (e.Key == Windows.System.VirtualKey.Escape && HasQuickCaptureCopySelection())
        {
            ClearQuickCaptureCopySelection();
            e.Handled = true;
            return;
        }

        if (e.Key == Windows.System.VirtualKey.Delete)
        {
            IReadOnlyList<QuickCaptureItemViewModel> selectedItems =
                GetSelectedQuickCaptureItemsInVisibleOrder();
            if (selectedItems.Count > 1)
            {
                e.Handled = true;
                await DeleteSelectedQuickCaptureItemsAsync(
                    selectedItems.Select(item => item.Id).ToArray(),
                    selectedItems.All(item => item.IsRecent));
                return;
            }

            QuickCaptureItemViewModel? deleteItem = selectedItems.SingleOrDefault() ??
                ItemsListView.SelectedItem as QuickCaptureItemViewModel;
            if (deleteItem is not null)
            {
                e.Handled = true;
                await DeleteItemWithUndoAsync(deleteItem);
                return;
            }
        }

        if (ItemsListView.SelectedItem is not QuickCaptureItemViewModel item)
        {
            return;
        }

        if (e.Key == Windows.System.VirtualKey.F2 && !item.IsRecent)
        {
            e.Handled = true;
            await OpenDetailAfterSavingAsync(item);
            EnterDetailEditMode();
            return;
        }

        if (e.Key is Windows.System.VirtualKey.Enter or Windows.System.VirtualKey.Space)
        {
            e.Handled = true;
            if (item.IsRecent && !_isDualPane)
            {
                await CopyItemWithFeedbackAsync(item);
            }
            else
            {
                await OpenDetailAfterSavingAsync(item);
            }
        }
    }

    private void ItemsListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        foreach (var item in e.RemovedItems)
        {
            SetItemActionButtonsVisibleForItem(item, false);
        }

        foreach (var item in e.AddedItems)
        {
            SetItemActionButtonsVisibleForItem(item, true);
        }
    }

    private void QuickCaptureItem_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not QuickCaptureItemViewModel item)
        {
            return;
        }

        ItemsListView.Focus(FocusState.Programmatic);
        if (!item.IsCopySelected)
        {
            ClearQuickCaptureCopySelection();
            item.IsCopySelected = true;
            _copySelectionAnchorId = item.Id;
        }

        var anchor = (FrameworkElement)sender;
        var selectedItems = GetSelectedQuickCaptureItemsInVisibleOrder();
        var flyout = selectedItems.Count > 1 && item.IsCopySelected
            ? CreateMultiItemFlyout(selectedItems)
            : CreateItemFlyout(item);
        ShowFlyoutWithElevation(flyout, anchor, e.GetPosition(anchor));
        e.Handled = true;
    }

    private async void QuickCaptureItem_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not QuickCaptureItemViewModel item ||
            e.OriginalSource is not DependencyObject source ||
            IsItemActionSource(source))
        {
            return;
        }

        ItemsListView.Focus(FocusState.Programmatic);
        bool isCtrlPressed = Win32Helper.IsKeyPressed(Windows.System.VirtualKey.Control);
        bool isShiftPressed = Win32Helper.IsKeyPressed(Windows.System.VirtualKey.Shift);
        if (isCtrlPressed || isShiftPressed)
        {
            if (isShiftPressed)
            {
                SelectQuickCaptureRange(item);
            }
            else
            {
                ToggleQuickCaptureSelection(item);
            }

            e.Handled = true;
            return;
        }

        if (HasQuickCaptureCopySelection())
        {
            ClearQuickCaptureCopySelection();
        }

        e.Handled = true;
        if (item.IsRecent && !_isDualPane)
        {
            await CopyItemWithFeedbackAsync(item);
        }
        else
        {
            await OpenDetailAfterSavingAsync(item);
        }
    }

    private async void QuickCaptureItem_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not QuickCaptureItemViewModel item ||
            item.IsRecent ||
            e.OriginalSource is not DependencyObject source ||
            IsItemActionSource(source))
        {
            return;
        }

        await OpenDetailAfterSavingAsync(item);
        EnterDetailEditMode();
        e.Handled = true;
    }

    private async void CopyItemButton_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is QuickCaptureItemViewModel item)
        {
            await CopyItemWithFeedbackAsync(item);
        }
    }

    private async Task CopyImageWithFeedbackAsync(QuickCaptureItemViewModel item)
    {
        try
        {
            await ViewModel.CopyImageAsync(item);
            ShowCopyToast();
        }
        catch (Exception ex)
        {
            App.Log($"[QuickCapture] Failed to copy image {item.Id}: {ex}");
            ShowStatusToast(_localizationService.T("QuickCapture.CopyFailed"));
        }
    }

    private async Task CopyItemWithFeedbackAsync(QuickCaptureItemViewModel item)
    {
        try
        {
            await ViewModel.CopyItemAsync(item);
            ShowCopyToast();
        }
        catch (Exception ex)
        {
            App.Log($"[QuickCapture] Failed to copy item {item.Id}: {ex}");
            ShowStatusToast(_localizationService.T("QuickCapture.CopyFailed"));
        }
    }

    private async Task CopyImageTextAsync(QuickCaptureItemViewModel item)
    {
        if (string.IsNullOrWhiteSpace(item.ImagePath) || !File.Exists(item.ImagePath))
        {
            ShowStatusToast(_localizationService.T("QuickCapture.CopyFailed"));
            return;
        }

        try
        {
            var file = await StorageFile.GetFileFromPathAsync(item.ImagePath);
            using var stream = await file.OpenAsync(FileAccessMode.Read);
            var decoder = await BitmapDecoder.CreateAsync(stream);
            var softwareBitmap = await decoder.GetSoftwareBitmapAsync();

            var ocrEngine = OcrEngine.TryCreateFromUserProfileLanguages();
            if (ocrEngine is null)
            {
                ShowStatusToast(_localizationService.T("QuickCapture.CopyFailed"));
                return;
            }

            var ocrResult = await ocrEngine.RecognizeAsync(softwareBitmap);
            if (string.IsNullOrWhiteSpace(ocrResult.Text))
            {
                ShowStatusToast(_localizationService.T("QuickCapture.CopyFailed"));
                return;
            }

            var dataPackage = new DataPackage();
            dataPackage.SetText(ocrResult.Text);
            Clipboard.SetContent(dataPackage);
            ShowStatusToast(_localizationService.T("QuickCapture.CopyText") + " ✓");
        }
        catch (Exception ex)
        {
            App.Log($"[QuickCapture] Failed to OCR image {item.Id}: {ex}");
            ShowStatusToast(_localizationService.T("QuickCapture.CopyFailed"));
        }
    }

    private void QuickCaptureItem_DataContextChanged(FrameworkElement sender, DataContextChangedEventArgs args)
    {
        if (sender.DataContext is not QuickCaptureItemViewModel item)
        {
            return;
        }

        SetItemActionButtonsVisible(sender, false);
        ApplyItemSearchHighlight(sender, item);
        ApplyItemMaterialSurface(sender, item);

        if (FindVisualChild<Button>(sender, "CopyItemButton") is { } copyButton)
        {
            string copyText = _localizationService.T("Common.Copy");
            ToolTipService.SetToolTip(copyButton, copyText);
            AutomationProperties.SetName(copyButton, copyText);
        }

        if (FindVisualChild<Button>(sender, "SaveRecentItemButton") is { } saveButton)
        {
            string saveToRecordsText = _localizationService.T("QuickCapture.SaveToRecords");
            saveButton.Visibility = item.IsRecent ? Visibility.Visible : Visibility.Collapsed;
            ToolTipService.SetToolTip(saveButton, saveToRecordsText);
            AutomationProperties.SetName(saveButton, saveToRecordsText);
        }

        if (FindPinnedButton(sender) is { } pinButton)
        {
            string pinText = item.IsRecent
                ? _localizationService.T("QuickCapture.PinToRecords")
                : item.PinTooltip;
            ToolTipService.SetToolTip(pinButton, pinText);
            AutomationProperties.SetName(pinButton, pinText);
        }

        if (FindVisualChild<Button>(sender, "MovePinnedItemUpButton") is { } moveUpButton)
        {
            string moveUpText = _localizationService.T("QuickCapture.MoveUp");
            ToolTipService.SetToolTip(moveUpButton, moveUpText);
            AutomationProperties.SetName(moveUpButton, moveUpText);
        }

        if (FindVisualChild<Button>(sender, "MovePinnedItemDownButton") is { } moveDownButton)
        {
            string moveDownText = _localizationService.T("QuickCapture.MoveDown");
            ToolTipService.SetToolTip(moveDownButton, moveDownText);
            AutomationProperties.SetName(moveDownButton, moveDownText);
        }

        if (FindVisualChild<Button>(sender, "DeleteItemButton") is { } deleteButton)
        {
            string deleteText = _localizationService.T("Common.Delete");
            ToolTipService.SetToolTip(deleteButton, deleteText);
            AutomationProperties.SetName(deleteButton, deleteText);
        }

    }

    private static Uri? TryCreateImageUri(string? path)
    {
        return !string.IsNullOrWhiteSpace(path) && File.Exists(path) && Uri.TryCreate(path, UriKind.Absolute, out var uri)
            ? uri
            : null;
    }

    private void ApplyItemSearchHighlight(DependencyObject itemRoot, QuickCaptureItemViewModel item)
    {
        if (FindVisualChild<TextBlock>(itemRoot, "ItemTextBlock") is not { } textBlock)
        {
            return;
        }

        textBlock.TextHighlighters.Clear();
        if (item.HighlightStartIndex < 0 || item.HighlightLength <= 0)
        {
            return;
        }

        // A query match is an input-driven highlight, so it follows the
        // inline-rename precedent and washes with a neutral tone instead of
        // the accent.
        bool isDark = textBlock.ActualTheme == ElementTheme.Dark;
        var highlighter = new TextHighlighter
        {
            Background = new SolidColorBrush(WithAlpha(
                isDark ? Colors.White : Colors.Black,
                isDark ? (byte)0x33 : (byte)0x1E)),
            Foreground = GetBrushResourceOrFallback(
                "TextFillColorPrimaryBrush",
                isDark ? Colors.White : Colors.Black)
        };
        highlighter.Ranges.Add(new TextRange
        {
            StartIndex = item.HighlightStartIndex,
            Length = item.HighlightLength
        });
        textBlock.TextHighlighters.Add(highlighter);
    }

    private Brush GetBrushResourceOrFallback(string resourceKey, Windows.UI.Color fallbackColor)
    {
        if (RootGrid.Resources.TryGetValue(resourceKey, out object? scopedResource))
        {
            return scopedResource switch
            {
                Brush brush => brush,
                Windows.UI.Color color => new SolidColorBrush(color),
                _ => new SolidColorBrush(fallbackColor)
            };
        }

        if (Application.Current.Resources.TryGetValue(resourceKey, out object? resource))
        {
            return resource switch
            {
                Brush brush => brush,
                Windows.UI.Color color => new SolidColorBrush(color),
                _ => new SolidColorBrush(fallbackColor)
            };
        }

        return new SolidColorBrush(fallbackColor);
    }
}
