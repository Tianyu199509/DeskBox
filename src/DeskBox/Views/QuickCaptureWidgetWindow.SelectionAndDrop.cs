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
    private void ItemsListView_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not ListViewBase listView)
        {
            return;
        }

        var properties = e.GetCurrentPoint(listView).Properties;
        if (!properties.IsLeftButtonPressed ||
            Win32Helper.IsKeyPressed(Windows.System.VirtualKey.Shift) ||
            !CanStartQuickCaptureBoxSelection(e.OriginalSource))
        {
            return;
        }

        RootGrid.Focus(FocusState.Programmatic);
        _selectionPointerPressed = true;
        _isBoxSelecting = false;
        _selectionStartPoint = e.GetCurrentPoint(SelectionOverlay).Position;
        _selectionCurrentPoint = _selectionStartPoint;
        _selectionSnapshot = Win32Helper.IsKeyPressed(Windows.System.VirtualKey.Control)
            ? GetSelectedQuickCaptureItemsInVisibleOrder().ToList()
            : [];
        _selectionPreviewItems = new HashSet<QuickCaptureItemViewModel>(_selectionSnapshot);
        _selectionHitTestItems = [];

        if (!Win32Helper.IsKeyPressed(Windows.System.VirtualKey.Control))
        {
            ClearQuickCaptureCopySelection();
            ItemsListView.SelectedItem = null;
        }
    }

    private void ItemsListView_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not ListViewBase listView || !_selectionPointerPressed)
        {
            return;
        }

        _selectionCurrentPoint = e.GetCurrentPoint(SelectionOverlay).Position;
        if (!_isBoxSelecting && GetSelectionDragDistance(_selectionStartPoint, _selectionCurrentPoint) < 6.0)
        {
            return;
        }

        if (!_isBoxSelecting)
        {
            _isBoxSelecting = true;
            listView.CapturePointer(e.Pointer);
            if (_selectionHitTestItems.Count == 0)
            {
                CacheQuickCaptureSelectionHitTestItems(listView);
            }
        }

        UpdateQuickCaptureSelectionRectangleVisual();
        ApplyQuickCaptureSelectionRectanglePreview();
        e.Handled = true;
    }

    private void ItemsListView_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not ListViewBase listView)
        {
            return;
        }

        bool wasBoxSelecting = _isBoxSelecting;
        FinishQuickCaptureSelectionRectangle(listView);
        if (wasBoxSelecting)
        {
            listView.ReleasePointerCapture(e.Pointer);
            e.Handled = true;
        }
    }

    private void ItemsListView_PointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        if (sender is ListViewBase listView)
        {
            FinishQuickCaptureSelectionRectangle(listView);
        }
    }

    private bool CanStartQuickCaptureBoxSelection(object? originalSource)
    {
        if (originalSource is not DependencyObject source)
        {
            return false;
        }

        return FindQuickCaptureItemContainer(source) is null &&
               !HasAncestorOfType<ScrollBar>(source) &&
               !HasAncestorOfType<ButtonBase>(source) &&
               !HasAncestorOfType<TextBox>(source);
    }

    private void UpdateQuickCaptureSelectionRectangleVisual()
    {
        var selectionRect = GetSelectionRect(_selectionStartPoint, _selectionCurrentPoint);
        Canvas.SetLeft(SelectionRectangle, selectionRect.X);
        Canvas.SetTop(SelectionRectangle, selectionRect.Y);
        SelectionRectangle.Width = Math.Max(0, selectionRect.Width);
        SelectionRectangle.Height = Math.Max(0, selectionRect.Height);
        SelectionRectangle.Visibility = selectionRect.Width > 0 && selectionRect.Height > 0
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void CacheQuickCaptureSelectionHitTestItems(ListViewBase listView)
    {
        _selectionHitTestItems = [];

        foreach (var item in ViewModel.Items)
        {
            if (listView.ContainerFromItem(item) is not SelectorItem container ||
                container.Visibility != Visibility.Visible ||
                container.ActualWidth <= 0 ||
                container.ActualHeight <= 0)
            {
                continue;
            }

            var target = FindVisualChild<Grid>(container, "QuickCaptureItemRoot") ?? container as FrameworkElement;
            if (target is null || target.ActualWidth <= 0 || target.ActualHeight <= 0)
            {
                continue;
            }

            var topLeft = target.TransformToVisual(SelectionOverlay)
                .TransformPoint(new Windows.Foundation.Point(0, 0));
            var bounds = new Windows.Foundation.Rect(
                topLeft.X,
                topLeft.Y,
                target.ActualWidth,
                target.ActualHeight);
            _selectionHitTestItems.Add(new QuickCaptureSelectionHitTestItem(item, bounds));
        }
    }

    private void ApplyQuickCaptureSelectionRectanglePreview()
    {
        var selectionRect = GetSelectionRect(_selectionStartPoint, _selectionCurrentPoint);
        var selectedItems = new HashSet<QuickCaptureItemViewModel>(_selectionSnapshot);

        foreach (var hitTestItem in _selectionHitTestItems)
        {
            if (RectsIntersect(selectionRect, hitTestItem.Bounds))
            {
                selectedItems.Add(hitTestItem.Item);
            }
        }

        ApplyQuickCaptureSelectionPreview(selectedItems);
    }

    private void FinishQuickCaptureSelectionRectangle(ListViewBase listView)
    {
        bool shouldHandle = _selectionPointerPressed || _isBoxSelecting;
        _selectionPointerPressed = false;

        if (_isBoxSelecting)
        {
            ApplyQuickCaptureSelectionRectanglePreview();
            _copySelectionAnchorId = ViewModel.Items.FirstOrDefault(item => item.IsCopySelected)?.Id;
        }

        _isBoxSelecting = false;
        _selectionSnapshot = [];
        _selectionPreviewItems = [];
        _selectionHitTestItems = [];
        SelectionRectangle.Visibility = Visibility.Collapsed;
        SelectionRectangle.Width = 0;
        SelectionRectangle.Height = 0;

        if (shouldHandle)
        {
            listView.Focus(FocusState.Programmatic);
        }
    }

    private void ApplyQuickCaptureSelectionPreview(HashSet<QuickCaptureItemViewModel> selectedItems)
    {
        _selectionPreviewItems = selectedItems;
        foreach (var item in ViewModel.Items)
        {
            item.IsCopySelected = selectedItems.Contains(item);
        }
    }

    private void ToggleQuickCaptureSelection(QuickCaptureItemViewModel item)
    {
        item.IsCopySelected = !item.IsCopySelected;
        _copySelectionAnchorId = item.Id;
    }

    private void SelectQuickCaptureRange(QuickCaptureItemViewModel item)
    {
        if (ViewModel.Items.Count == 0)
        {
            return;
        }

        int endIndex = ViewModel.Items.IndexOf(item);
        if (endIndex < 0)
        {
            return;
        }

        int startIndex = endIndex;
        if (!string.IsNullOrWhiteSpace(_copySelectionAnchorId))
        {
            for (int index = 0; index < ViewModel.Items.Count; index++)
            {
                if (string.Equals(ViewModel.Items[index].Id, _copySelectionAnchorId, StringComparison.Ordinal))
                {
                    startIndex = index;
                    break;
                }
            }
        }

        int first = Math.Min(startIndex, endIndex);
        int last = Math.Max(startIndex, endIndex);
        ClearQuickCaptureCopySelection();
        for (int index = first; index <= last; index++)
        {
            ViewModel.Items[index].IsCopySelected = true;
        }

        _copySelectionAnchorId = ViewModel.Items[startIndex].Id;
    }

    private void SelectAllVisibleQuickCaptureItems()
    {
        foreach (var item in ViewModel.Items)
        {
            item.IsCopySelected = true;
        }

        _copySelectionAnchorId = ViewModel.Items.FirstOrDefault()?.Id;
    }

    private void ClearQuickCaptureCopySelection()
    {
        foreach (var item in ViewModel.Items)
        {
            item.IsCopySelected = false;
        }

        _copySelectionAnchorId = null;
    }

    private bool HasQuickCaptureCopySelection()
    {
        return ViewModel.Items.Any(item => item.IsCopySelected);
    }

    private IReadOnlyList<QuickCaptureItemViewModel> GetSelectedQuickCaptureItemsInVisibleOrder()
    {
        return ViewModel.Items
            .Where(item => item.IsCopySelected)
            .ToList();
    }

    private async Task CopySelectedQuickCaptureItemsAsync()
    {
        await CopySelectedQuickCaptureItemsAsync(GetSelectedQuickCaptureItemsInVisibleOrder());
    }

    private async Task CopySelectedQuickCaptureItemsAsync(IReadOnlyList<QuickCaptureItemViewModel> selectedItems)
    {
        if (selectedItems.Count == 0)
        {
            return;
        }

        if (selectedItems.Count == 1)
        {
            await CopyItemWithFeedbackAsync(selectedItems[0]);
            return;
        }

        string text = FormatQuickCaptureBatch(selectedItems);
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        try
        {
            SetClipboardText(text);
            ShowStatusToast(
                _localizationService.Format("QuickCapture.CopiedCount", selectedItems.Count),
                durationMs: CopyToastMs);
        }
        catch (Exception ex)
        {
            App.Log($"[QuickCapture] Failed to copy {selectedItems.Count} selected items: {ex}");
            ShowStatusToast(_localizationService.T("QuickCapture.CopyFailed"));
        }
    }

    private string FormatQuickCaptureBatch(IReadOnlyList<QuickCaptureItemViewModel> selectedItems)
    {
        return QuickCaptureClipboardFormatter.FormatBatch(selectedItems, _localizationService);
    }

    private static void SetClipboardText(string text)
    {
        var dataPackage = new DataPackage
        {
            RequestedOperation = DataPackageOperation.Copy
        };
        dataPackage.SetText(text);
        Clipboard.SetContent(dataPackage);
        Clipboard.Flush();
    }

    private static FrameworkElement? FindQuickCaptureItemContainer(DependencyObject source)
    {
        DependencyObject? current = source;
        while (current is not null)
        {
            if (current is Grid { Name: "QuickCaptureItemRoot", DataContext: QuickCaptureItemViewModel })
            {
                return (FrameworkElement)current;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return null;
    }

    private static Windows.Foundation.Rect GetSelectionRect(
        Windows.Foundation.Point startPoint,
        Windows.Foundation.Point endPoint)
    {
        double x = Math.Min(startPoint.X, endPoint.X);
        double y = Math.Min(startPoint.Y, endPoint.Y);
        double width = Math.Abs(endPoint.X - startPoint.X);
        double height = Math.Abs(endPoint.Y - startPoint.Y);
        return new Windows.Foundation.Rect(x, y, width, height);
    }

    private static double GetSelectionDragDistance(
        Windows.Foundation.Point startPoint,
        Windows.Foundation.Point endPoint)
    {
        double deltaX = endPoint.X - startPoint.X;
        double deltaY = endPoint.Y - startPoint.Y;
        return Math.Sqrt((deltaX * deltaX) + (deltaY * deltaY));
    }

    private static bool RectsIntersect(Windows.Foundation.Rect first, Windows.Foundation.Rect second)
    {
        return first.X < second.X + second.Width &&
               first.X + first.Width > second.X &&
               first.Y < second.Y + second.Height &&
               first.Y + first.Height > second.Y;
    }

    private static bool HasAncestorOfType<T>(DependencyObject source) where T : DependencyObject
    {
        var current = source;
        while (current is not null)
        {
            if (current is T)
            {
                return true;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return false;
    }

    private async void RootGrid_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (TryHandleCompactActivation(e))
        {
            return;
        }

        if (e.Key == Windows.System.VirtualKey.Escape)
        {
            if (DetailPage.Visibility == Visibility.Visible)
            {
                await SaveAndCloseDetailAsync();
                e.Handled = true;
                return;
            }

            bool isFromTextBox = e.OriginalSource is DependencyObject source &&
                HasAncestorOfType<TextBox>(source);
            if (!isFromTextBox && HasQuickCaptureCopySelection())
            {
                ClearQuickCaptureCopySelection();
                RootGrid.Focus(FocusState.Programmatic);
                e.Handled = true;
                return;
            }

            if (ViewModel.IsSearchExpanded)
            {
                ViewModel.CollapseSearch();
                RootGrid.Focus(FocusState.Programmatic);
                e.Handled = true;
                return;
            }

            if (!string.IsNullOrEmpty(InputTextBox.Text))
            {
                InputTextBox.Text = string.Empty;
                RootGrid.Focus(FocusState.Programmatic);
                e.Handled = true;
                return;
            }

            if (TryHandleCompactEscape())
            {
                RootGrid.Focus(FocusState.Programmatic);
                e.Handled = true;
            }
        }
    }

    private void RootGrid_DragOver(object sender, DragEventArgs e)
    {
        if (_isInternalQuickCaptureDrag)
        {
            return;
        }

        if (DeskBoxDragData.HasDroppedFiles(e.DataView))
        {
            e.AcceptedOperation = DataPackageOperation.Copy;
            ApplyCompactQuickCaptureDropCaption(e);
            return;
        }

        if (e.DataView.Contains(StandardDataFormats.Text) ||
            e.DataView.Contains(StandardDataFormats.WebLink))
        {
            e.AcceptedOperation = DataPackageOperation.Copy;
            ApplyCompactQuickCaptureDropCaption(e);
        }
        else
        {
            e.AcceptedOperation = DataPackageOperation.None;
        }
    }

    private void ApplyCompactQuickCaptureDropCaption(DragEventArgs e)
    {
        if (!IsCompactBoundsStateActive)
        {
            return;
        }

        e.DragUIOverride.IsGlyphVisible = false;
        e.DragUIOverride.Caption = _localizationService.T("Widget.Compact.QuickCaptureDropHint");
    }

    private async void RootGrid_Drop(object sender, DragEventArgs e)
    {
        if (_isInternalQuickCaptureDrag)
        {
            return;
        }

        var deferral = e.GetDeferral();
        try
        {
            if (DeskBoxDragData.HasDroppedFiles(e.DataView))
            {
                using DroppedFileBatch batch = await DeskBoxDragData.TryGetDroppedFilesAsync(e.DataView);
                if (batch.Files.Count == 0)
                {
                    string? fallbackText = await TryReadDroppedTextAsync(e.DataView);
                    if (!string.IsNullOrWhiteSpace(fallbackText))
                    {
                        await ViewModel.AddTextAsync(fallbackText);
                        e.AcceptedOperation = DataPackageOperation.Copy;
                        ShowStatusToast(_localizationService.T("QuickCapture.Dropped"));
                        return;
                    }

                    if (batch.SkippedCount > 0)
                    {
                        ShowStatusToast(_localizationService.T("QuickCapture.DroppedAllSkipped"));
                    }
                    e.AcceptedOperation = DataPackageOperation.None;
                    return;
                }

                QuickCaptureItemViewModel? imported;
                if (DetailPage.Visibility == Visibility.Visible && _detailItem is not null)
                {
                    imported = await ViewModel.AddAttachmentsAsync(_detailItem, batch.Files);
                    if (imported is not null)
                    {
                        _detailItem = imported;
                        RefreshDetailAttachmentList();
                    }
                }
                else
                {
                    imported = await ViewModel.AddItemWithAttachmentsAsync(batch.Files);
                    if (imported is not null &&
                        DetailPage.Visibility == Visibility.Visible &&
                        _isCreatingDetail)
                    {
                        _detailItem = imported;
                        _isCreatingDetail = false;
                        _pendingDetailAttachments = [];
                        RefreshDetailAttachmentList();
                    }
                }

                e.AcceptedOperation = imported is null
                    ? DataPackageOperation.None
                    : DataPackageOperation.Copy;
                if (imported is not null)
                {
                    ShowStatusToast(batch.SkippedCount > 0
                        ? _localizationService.T("QuickCapture.DroppedWithSkipped")
                        : _localizationService.T("QuickCapture.Dropped"));
                }
                return;
            }

            string? text = await TryReadDroppedTextAsync(e.DataView);
            if (string.IsNullOrWhiteSpace(text))
            {
                e.AcceptedOperation = DataPackageOperation.None;
                return;
            }

            await ViewModel.AddTextAsync(text);
            ViewModel.RefreshAfterViewReady();
            e.AcceptedOperation = DataPackageOperation.Copy;
            ShowStatusToast(_localizationService.T("QuickCapture.Dropped"));
        }
        catch (Exception ex)
        {
            App.Log($"[QuickCapture] RootGrid_Drop failed: {ex}");
        }
        finally
        {
            deferral.Complete();
        }
    }

    private void ShowCopyToast()
    {
        ShowStatusToast(_localizationService.T("QuickCapture.Copied"), durationMs: CopyToastMs);
    }

    private void ShowStatusToast(string text, string? actionText = null, int durationMs = StatusToastDefaultMs)
    {
        if (!DispatcherQueue.HasThreadAccess)
        {
            DispatcherQueue.TryEnqueue(() => ShowStatusToast(text, actionText, durationMs));
            return;
        }

        long generation = ++_statusToastGeneration;
        StatusToast.MaxWidth = Math.Max(120, _appWindow.Size.Width - 24);
        StatusToastText.Text = text;
        StatusToastActionButton.Content = actionText;
        StatusToastActionButton.Visibility = string.IsNullOrWhiteSpace(actionText)
            ? Visibility.Collapsed
            : Visibility.Visible;
        StatusToast.IsHitTestVisible = !string.IsNullOrWhiteSpace(actionText);
        StatusToast.Opacity = 1;
        _ = HideStatusToastAfterDelayAsync(generation, durationMs);
    }

    private async Task HideStatusToastAfterDelayAsync(long generation, int durationMs)
    {
        await Task.Delay(durationMs);
        if (generation != _statusToastGeneration)
        {
            return;
        }

        if (!DispatcherQueue.HasThreadAccess)
        {
            DispatcherQueue.TryEnqueue(() => HideStatusToastIfCurrent(generation));
            return;
        }

        HideStatusToastIfCurrent(generation);
    }

    private void HideStatusToastIfCurrent(long generation)
    {
        if (generation == _statusToastGeneration)
        {
            StatusToast.Opacity = 0;
            StatusToast.IsHitTestVisible = false;
            StatusToastActionButton.Visibility = Visibility.Collapsed;
            _pendingDeletedItemSnapshot = null;
        }
    }

    private async void StatusToastActionButton_Click(object sender, RoutedEventArgs e)
    {
        var snapshot = _pendingDeletedItemSnapshot;
        if (snapshot is null)
        {
            return;
        }

        _pendingDeletedItemSnapshot = null;
        if (await ViewModel.RestoreDeletedItemAsync(snapshot))
        {
            ShowStatusToast(_localizationService.T("Common.Undone"));
        }
    }

    private static async Task<string?> TryReadDroppedTextAsync(DataPackageView dataView)
    {
        string? internalText = await DeskBoxDragData.TryGetInternalTextAsync(dataView);
        if (!string.IsNullOrWhiteSpace(internalText))
        {
            return internalText.Length <= QuickCaptureClipboardService.MaxClipboardTextCharacters
                ? internalText
                : null;
        }

        if (dataView.Contains(StandardDataFormats.WebLink))
        {
            var link = await dataView.GetWebLinkAsync();
            if (!string.IsNullOrWhiteSpace(link?.AbsoluteUri))
            {
                return link.AbsoluteUri;
            }
        }

        if (dataView.Contains(StandardDataFormats.Text))
        {
            string text = await dataView.GetTextAsync();
            if (!string.IsNullOrWhiteSpace(text))
            {
                return text.Length <= QuickCaptureClipboardService.MaxClipboardTextCharacters
                    ? text
                    : null;
            }
        }

        return null;
    }
}
