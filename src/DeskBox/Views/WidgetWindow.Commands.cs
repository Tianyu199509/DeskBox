using System.ComponentModel;
using DeskBox.Helpers;
using DeskBox.Models;
using DeskBox.Services;
using DeskBox.ViewModels;
using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage;
using Windows.Storage.Pickers;
using WinRT;
using WinRT.Interop;

namespace DeskBox.Views;

public sealed partial class WidgetWindow
{
    private void RootGrid_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (_isMigrationBusy)
        {
            e.Handled = true;
            return;
        }

        if (FileWidgetShell.ChromeMode is not (WidgetChromeMode.Overlay or WidgetChromeMode.Hidden) &&
            e.OriginalSource is DependencyObject source &&
            IsWithin(source, TitleBarGrid))
        {
            return;
        }

        ShowFlyoutWithElevation(CreateContentAreaFlyout(), RootGrid, e.GetPosition(RootGrid));
        e.Handled = true;
    }

    private void TitleBarGrid_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (_isMigrationBusy)
        {
            e.Handled = true;
            return;
        }

        if (!ShouldOpenTitleBarFlyout(e.OriginalSource))
        {
            return;
        }

        var position = e.GetPosition(TitleBarGrid);
        TrackMoreFlyoutAnchor(TitleBarGrid, position);
        ShowFlyoutWithElevation(CreateMoreFlyout(), TitleBarGrid, position);
        e.Handled = true;
    }

    private void TitleText_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (!CanStartRenameFromTitleArea(e.OriginalSource))
        {
            return;
        }

        e.Handled = true;
        DispatcherQueue.TryEnqueue(() =>
        {
            if (_isDragging || _isResizing || TitleEditBox.Visibility == Visibility.Visible)
            {
                return;
            }

            StartRename();
        });
    }

    private void AddFileButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isMigrationBusy)
        {
            return;
        }

        var target = sender as FrameworkElement ?? AddButton;
        ShowFlyoutWithElevation(CreateNewWidgetFlyout(), target);
    }

    private void TitleBarGrid_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_isMigrationBusy)
        {
            e.Handled = true;
            return;
        }

        if (!ShouldStartTitleDrag(e.OriginalSource))
        {
            return;
        }

        var properties = e.GetCurrentPoint(TitleBarGrid).Properties;
        if (!properties.IsLeftButtonPressed)
        {
            return;
        }

        if (TitleEditBox.Visibility == Visibility.Visible &&
            ShouldOpenTitleBarFlyout(e.OriginalSource))
        {
            _ = CommitRenameAsync();
            e.Handled = true;
            return;
        }

        App.Current.WidgetManager?.ActivateAllVisibleWidgetsFromTitle(_hWnd);
        if (ViewModel.IsPositionLocked)
        {
            return;
        }

        Win32Helper.GetCursorPos(out var cursorPt);
        if (CanStartRenameFromTitleArea(e.OriginalSource) && IsTitleBarDoubleClick(cursorPt))
        {
            _hasPendingTitleBarClick = false;
            StartRename();
            e.Handled = true;
            return;
        }

        TrackTitleBarClick(e.OriginalSource, cursorPt);
        BeginWindowDrag(e, TitleBarGrid, cursorPt);
    }

    private void DragHandle_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_isMigrationBusy)
        {
            e.Handled = true;
            return;
        }

        if (ViewModel.IsPositionLocked)
        {
            return;
        }

        var captureElement = FileWidgetShell.DragHandleElement;
        var properties = e.GetCurrentPoint(captureElement).Properties;
        if (!properties.IsLeftButtonPressed)
        {
            return;
        }

        Win32Helper.GetCursorPos(out var cursorPt);
        _hasPendingTitleBarClick = false;
        BeginWindowDrag(e, captureElement, cursorPt);
    }

    private void BeginWindowDrag(PointerRoutedEventArgs e, FrameworkElement captureElement, Win32Helper.POINT cursorPt)
    {
        BeginWidgetBoundsInteraction();
        _isDragging = true;
        _hasMovedTitleBarDrag = false;
        _displayChangeWatcher?.SuppressRestore();
        ElevateForInteraction();
        _initialCursorPt = cursorPt;
        var initialBounds = GetActualWindowBounds();
        _initialWindowPos = new Windows.Graphics.PointInt32(initialBounds.X, initialBounds.Y);
        _initialWindowSize = new Windows.Graphics.SizeInt32(initialBounds.Width, initialBounds.Height);
        bool movesCapsuleBar = BeginCompactArrangementDrag();
        _dragCaptureElement = captureElement;
        captureElement.CapturePointer(e.Pointer);
        e.Handled = true;

        if (!movesCapsuleBar)
        {
            App.Current?.ResizeGuideOverlay.BeginDrag(_hWnd, RootGrid);
        }
    }

    private void TitleBarGrid_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        ContinueWindowDrag(e);
    }

    private void DragHandle_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        ContinueWindowDrag(e);
    }

    private void ContinueWindowDrag(PointerRoutedEventArgs e)
    {
        if (!_isDragging)
        {
            return;
        }

        Win32Helper.GetCursorPos(out var currentPt);
        int deltaX = currentPt.X - _initialCursorPt.X;
        int deltaY = currentPt.Y - _initialCursorPt.Y;
        int dragDistanceSquared = (deltaX * deltaX) + (deltaY * deltaY);

        if (_hasPendingTitleBarClick && dragDistanceSquared > 25)
        {
            _hasPendingTitleBarClick = false;
        }

        if (!_hasMovedTitleBarDrag)
        {
            if (dragDistanceSquared < 16)
            {
                e.Handled = true;
                return;
            }

            _hasMovedTitleBarDrag = true;
            FileWidgetShell.NotifyCompactDragMoved();
        }

        int newX = _initialWindowPos.X + deltaX;
        int newY = _initialWindowPos.Y + deltaY;

        var proposedBounds = new Windows.Graphics.RectInt32(
            newX, newY, _initialWindowSize.Width, _initialWindowSize.Height);
        if (!TryMoveCompactArrangement(proposedBounds, out _))
        {
            var snappedBounds = App.Current?.ResizeGuideOverlay.UpdateGuidesAndSnapForDrag(proposedBounds)
                ?? proposedBounds;
            ApplyWindowBounds(
                snappedBounds.X,
                snappedBounds.Y,
                snappedBounds.Width,
                snappedBounds.Height,
                persist: false);
        }
        e.Handled = true;
    }

    private void TitleBarGrid_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        EndWindowDrag(e);
    }

    private void DragHandle_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        EndWindowDrag(e);
    }

    private void TitleBarGrid_PointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        if (!_isDragging)
        {
            return;
        }

        _isDragging = false;
        bool hasMoved = _hasMovedTitleBarDrag;
        _dragCaptureElement = null;
        App.Current?.ResizeGuideOverlay.EndDrag();
        CompleteCompactArrangementDrag();
        if (hasMoved)
        {
            var finalBounds = GetActualWindowBounds();
            finalBounds = CompleteExpandedWidgetDrag(finalBounds);
            CapturePositionAnchor(finalBounds.X, finalBounds.Y, finalBounds.Width, finalBounds.Height);
            UpdateConfigBoundsFromPhysical(finalBounds.X, finalBounds.Y, finalBounds.Width, finalBounds.Height, persist: true);
        }
        EndWidgetBoundsInteraction();
        RestoreAfterFileDrag("file-drag-capture-lost");
        _displayChangeWatcher?.ResumeRestore();
        _hasMovedTitleBarDrag = false;
        QueueBackdropRefresh();
        e.Handled = true;
    }

    private void EndWindowDrag(PointerRoutedEventArgs e)
    {
        if (!_isDragging)
        {
            return;
        }

        _isDragging = false;
        _dragCaptureElement?.ReleasePointerCapture(e.Pointer);
        _dragCaptureElement = null;

        // End drag-move snap session
        App.Current?.ResizeGuideOverlay.EndDrag();

        CompleteCompactArrangementDrag();
        if (_hasMovedTitleBarDrag)
        {
            var finalBounds = GetActualWindowBounds();
            finalBounds = CompleteExpandedWidgetDrag(finalBounds);
            CapturePositionAnchor(finalBounds.X, finalBounds.Y, finalBounds.Width, finalBounds.Height);
            UpdateConfigBoundsFromPhysical(finalBounds.X, finalBounds.Y, finalBounds.Width, finalBounds.Height, persist: true);
            RestoreAfterFileDrag("file-drag-ended");
        }

        EndWidgetBoundsInteraction();
        _displayChangeWatcher?.ResumeRestore();
        _hasMovedTitleBarDrag = false;
        e.Handled = true;
    }

    private void RestoreAfterFileDrag(string reason)
    {
        if (App.Current.WidgetManager?.RequestRestoreRaisedWidgetsToDesktopLayer(reason) == true)
        {
            return;
        }

        RestoreDesktopLayer();
    }

    private async void MapFolderButton_Click(object sender, RoutedEventArgs e)
    {
        if (App.Current is not App app || app.WidgetManager is null)
        {
            return;
        }

        BeginInteractionLayer("file-map-folder-picker-opened");

        try
        {
            string? folderPath = FolderPickerService.PickFolder(_hWnd);
            if (!string.IsNullOrWhiteSpace(folderPath))
            {
                await app.WidgetManager.CreateFolderWidgetAsync(folderPath);
            }
        }
        finally
        {
            ReleaseInteractionLayer("file-picker-closed");
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        var target = sender as FrameworkElement ?? CloseButton;
        TrackMoreFlyoutAnchor(target, null);
        ShowDeleteWidgetFlyout(target);
    }

    private void MoreButton_Click(object sender, RoutedEventArgs e)
    {
        var target = sender as FrameworkElement ?? MoreButton;
        TrackMoreFlyoutAnchor(target, null);
        ShowFlyoutWithElevation(CreateMoreFlyout(), target);
    }

    private void SetChromeModeOverride(WidgetChromeMode mode)
    {
        WidgetChromeModeNames.SetOverrideMode(ViewModel.Config, mode);
        _settingsService.UpdateWidget(ViewModel.Config);
        ApplyTitleBarLayout();
    }

    private async Task PickAndImportFilesAsync()
    {
        BeginInteractionLayer("file-import-picker-opened");

        try
        {
            var picker = new FileOpenPicker
            {
                SuggestedStartLocation = PickerLocationId.Desktop
            };
            picker.FileTypeFilter.Add("*");
            InitializeWithWindow.Initialize(picker, _hWnd);

            var files = await picker.PickMultipleFilesAsync();
            var paths = files
                .Select(file => file.Path)
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .ToArray();
            if (paths.Length == 0)
            {
                return;
            }

            bool shouldMove = GetManagedDropOperation() != DataPackageOperation.Copy;
            await ViewModel.ImportPathsAsync(paths, shouldMove, useShellProgress: shouldMove);
            ShowStatusToast(paths.Length == 1
                ? _localizationService.T("Widget.AddedFile")
                : _localizationService.Format("Widget.AddedFiles", paths.Length));
        }
        catch (Exception ex)
        {
            await ShowErrorDialogAsync(_localizationService.T("Widget.AddFileFailed"), ex.Message);
        }
        finally
        {
            ReleaseInteractionLayer("folder-picker-closed");
        }
    }

    private async Task PickAndApplyMappedFolderAsync()
    {
        if (ViewModel.FollowsDefaultStoragePath)
        {
            return;
        }

        BeginInteractionLayer("file-mapped-folder-picker-opened");

        try
        {
            string? folderPath = FolderPickerService.PickFolder(_hWnd);
            if (string.IsNullOrWhiteSpace(folderPath))
            {
                return;
            }

            await ViewModel.UpdateMappedFolderPathAsync(folderPath);
            ShowStatusToast(_localizationService.T("Widget.MappedPathUpdated"));
        }
        catch (Exception ex)
        {
            await ShowErrorDialogAsync(_localizationService.T("Widget.ChangeMappedPathFailed"), ex.Message);
        }
        finally
        {
            ReleaseInteractionLayer("folder-picker-closed");
        }
    }

    private void SetIconView_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.ViewMode != ViewMode.Icon)
        {
            ViewModel.ToggleViewModeCommand.Execute(null);
        }
    }

    private void SetListView_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.ViewMode != ViewMode.List)
        {
            ViewModel.ToggleViewModeCommand.Execute(null);
        }
    }

    private void Rename_Click(object sender, RoutedEventArgs e)
    {
        StartRename();
    }

    private async Task CreateFolderInMappedLocationAsync()
    {
        if (string.IsNullOrWhiteSpace(ViewModel.MappedFolderPath))
        {
            return;
        }

        try
        {
            string folderPath = FileService.GetAvailablePath(
                Path.Combine(ViewModel.MappedFolderPath, _localizationService.T("Widget.NewFolderName")));
            Directory.CreateDirectory(folderPath);
            await ViewModel.RefreshFromConfigAsync();
            if (ViewModel.Items.FirstOrDefault(existingItem =>
                    string.Equals(existingItem.Path, folderPath, StringComparison.OrdinalIgnoreCase)) is { } newFolderItem)
            {
                SelectSingleItem(newFolderItem);
                await StartItemRenameAsync(newFolderItem);
            }
        }
        catch (Exception ex)
        {
            await ShowErrorDialogAsync(_localizationService.T("Widget.CreateFolderFailed"), ex.Message);
        }
    }

    private async Task DeleteSelectedItemsAsync()
    {
        var selectedItems = GetSelectedItems();
        if (selectedItems.Count == 0 && GetPrimarySelectedItem() is { } primaryItem)
        {
            selectedItems = [primaryItem];
        }

        if (selectedItems.Count == 0)
        {
            return;
        }

        if (RequiresDeleteConfirmation(selectedItems))
        {
            ShowDeleteItemsConfirmFlyout(selectedItems);
            return;
        }

        await DeleteItemsWithoutConfirmAsync(selectedItems);
    }

    private static bool RequiresDeleteConfirmation(IReadOnlyList<WidgetItem> selectedItems)
    {
        return selectedItems.Any(item => item.IsFolder);
    }

    private async Task DeleteItemsWithoutConfirmAsync(IReadOnlyList<WidgetItem> selectedItems)
    {
        try
        {
            int deletedCount = selectedItems.Count;
            await ViewModel.DeleteItemsAsync(selectedItems);
            ClearRemovedCutPaths();
            ShowStatusToast(_localizationService.Format("Widget.MovedToRecycleBin", deletedCount));
        }
        catch (Exception ex)
        {
            await ShowErrorDialogAsync(_localizationService.T("Widget.DeleteFailed"), ex.Message);
        }
    }

    private async Task MoveSelectedItemsBackToDesktopAsync()
    {
        var selectedItems = GetSelectedItems();
        if (selectedItems.Count == 0)
        {
            return;
        }

        try
        {
            int movedCount = await ViewModel.MoveItemsBackToDesktopAsync(selectedItems, useShellProgress: true);

            ClearRemovedCutPaths();
            ShowStatusToast(movedCount > 0
                ? _localizationService.Format("Widget.MovedBackToDesktop", movedCount)
                : _localizationService.T("Widget.NoItemsMoved"));
        }
        catch (Exception ex)
        {
            await ShowErrorDialogAsync(_localizationService.T("Widget.MoveBackToDesktopFailed"), ex.Message);
        }
    }

    private void TogglePositionLock_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.TogglePositionLockCommand.Execute(null);
        ApplyLockActionIconState();
    }

    private void ToggleSizeLock_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.ToggleSizeLockCommand.Execute(null);
        ApplyLockActionIconState();
    }

    private void DeleteWidget_Click(object sender, RoutedEventArgs e)
    {
        ShowDeleteWidgetFlyout(_lastMoreFlyoutTarget ?? MoreButton, _lastMoreFlyoutPosition);
    }

    private async Task ConfirmAndDeleteWidgetAsync(WidgetRemovalAction removalAction)
    {
        if (App.Current is not App app || app.WidgetManager is null)
        {
            _deletePending = false;
            return;
        }

        try
        {
            App.Log($"[WidgetDelete] Begin delete widget '{ViewModel.Name}' ({ViewModel.Config.Id})");
            await app.WidgetManager.RemoveWidgetAsync(ViewModel.Config.Id, removalAction);
        }
        catch (Exception ex)
        {
            App.Log($"[WidgetDelete] Delete failed for '{ViewModel.Name}' ({ViewModel.Config.Id}): {ex}");
            _deletePending = false;
            RootGrid.IsHitTestVisible = true;
            await ShowErrorDialogAsync(_localizationService.T("Widget.DeleteWidgetFailed"), ex.Message);
            ReleaseInteractionLayer("file-delete-widget-failed");
        }
    }

    private void QueueDeleteWidget(WidgetRemovalAction removalAction)
    {
        if (_deletePending)
        {
            return;
        }

        _deletePending = true;
        RootGrid.IsHitTestVisible = false;

        DispatcherQueue.TryEnqueue(async () =>
        {
            await Task.Delay(16);
            await ConfirmAndDeleteWidgetAsync(removalAction);
        });
    }
}
