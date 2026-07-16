// Copyright (c) DeskBox. All rights reserved.

using DeskBox.Helpers;
using DeskBox.Models;
using DeskBox.Services;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using WinRT.Interop;

namespace DeskBox.Views;

public sealed partial class WidgetWindow
{
    private void WidgetItemSurface_DragOver(object sender, DragEventArgs e)
    {
        if (_isMigrationBusy)
        {
            e.Handled = true;
            e.AcceptedOperation = DataPackageOperation.None;
            e.DragUIOverride.IsGlyphVisible = false;
            ClearFolderDropTarget();
            return;
        }

        if (!TryGetFolderDropTarget(sender, out var border, out var targetFolder))
        {
            return;
        }

        e.Handled = true;

        if (!HasPathDropData(e.DataView))
        {
            e.AcceptedOperation = DataPackageOperation.None;
            e.DragUIOverride.IsGlyphVisible = false;
            ClearFolderDropTarget();
            LogDropDiagnostic("FolderDragOverNoPathData", e.DataView, e.AcceptedOperation, movesIntoFolder: true);
            return;
        }

        var sourcePaths = TryGetPackageStringArray(e.DataView.Properties, "DeskBoxSourcePaths");
        if (IsInvalidFolderDrop(sourcePaths, targetFolder.Path))
        {
            e.AcceptedOperation = DataPackageOperation.None;
            e.DragUIOverride.IsGlyphVisible = false;
            e.DragUIOverride.Caption = _localizationService.T("Widget.CannotMoveToFolder");
            ClearFolderDropTarget();
            return;
        }

        e.AcceptedOperation = NormalizePathDropOperation(e.DataView.RequestedOperation, movesIntoFolder: true);
        LogDropDiagnostic("FolderDragOver", e.DataView, e.AcceptedOperation, movesIntoFolder: true);
        if (e.AcceptedOperation == DataPackageOperation.None)
        {
            e.DragUIOverride.IsGlyphVisible = false;
            ClearFolderDropTarget();
            return;
        }

        SetFolderDropTarget(border);
        e.DragUIOverride.IsGlyphVisible = true;
        e.DragUIOverride.Caption = _localizationService.Format(
            ShouldMoveForAcceptedOperation(e.AcceptedOperation)
                ? "Widget.MoveToFolder"
                : "Widget.CopyToFolder",
            targetFolder.Name);
    }

    private void WidgetItemSurface_DragLeave(object sender, DragEventArgs e)
    {
        if (ReferenceEquals(sender, _folderDropTarget))
        {
            ClearFolderDropTarget();
        }

        _lastFolderDragDiagnosticSignature = null;
    }

    private async void WidgetItemSurface_Drop(object sender, DragEventArgs e)
    {
        if (_isMigrationBusy)
        {
            e.Handled = true;
            ClearFolderDropTarget();
            return;
        }

        StopDragHighlight();

        if (!TryGetFolderDropTarget(sender, out _, out var targetFolder))
        {
            return;
        }

        e.Handled = true;
        ClearFolderDropTarget();

        if (!HasPathDropData(e.DataView))
        {
            LogDropDiagnostic("FolderDropNoPathData", e.DataView, e.AcceptedOperation, movesIntoFolder: true);
            return;
        }

        var deferral = e.GetDeferral();
        try
        {
            LogDropDiagnostic("FolderDrop", e.DataView, e.AcceptedOperation, movesIntoFolder: true);

            var sourcePaths = await GetDropPathsAsync(e.DataView);
            if (sourcePaths.Length == 0)
            {
                App.Log(
                    $"[DropDiagnostic] widget='{ViewModel.Name}' id={ViewModel.Config.Id} stage=FolderDropNoPaths " +
                    $"requested={e.DataView.RequestedOperation} accepted={e.AcceptedOperation} " +
                    $"target='{targetFolder.Path}' formats={FormatDataPackageFormats(e.DataView.AvailableFormats)}");
                return;
            }

            if (IsInvalidFolderDrop(sourcePaths, targetFolder.Path))
            {
                ShowStatusToast(_localizationService.T("Widget.CannotMoveToFolder"));
                return;
            }

            var acceptedOperation = e.AcceptedOperation == DataPackageOperation.None
                ? NormalizePathDropOperation(e.DataView.RequestedOperation, movesIntoFolder: true)
                : e.AcceptedOperation;
            e.AcceptedOperation = acceptedOperation;
            if (acceptedOperation == DataPackageOperation.None)
            {
                LogDropDiagnostic("FolderDropRejectedOperation", e.DataView, acceptedOperation, movesIntoFolder: true);
                return;
            }

            bool move = ShouldMoveForAcceptedOperation(acceptedOperation);

            // Extract all needed data from the DataPackageView before completing
            // the deferral — the DataView becomes invalid after Complete().
            string? syncSourceWidgetId = TryGetPackageString(e.DataView.Properties, "DeskBoxSourceWidgetId");
            var syncSourcePaths = TryGetPackageStringArray(e.DataView.Properties, "DeskBoxSourcePaths");

            // Complete the deferral early so the drag glyph disappears immediately.
            deferral.Complete();
            deferral = null;

            // Only show the import overlay for large transfers to avoid
            // flashing for small files.
            bool showOverlay = ShouldShowImportOverlay(sourcePaths);
            if (showOverlay)
            {
                SetImportBusy(true);
            }
            try
            {
                var results = await App.Current.FileService.TransferItemsWithResultAsync(sourcePaths, targetFolder.Path, move);
                if (results.Count == 0)
                {
                    return;
                }

                if (!string.IsNullOrWhiteSpace(ViewModel.MappedFolderPath))
                {
                    await ViewModel.RefreshFromConfigAsync();
                }

                if (move)
                {
                    await SyncMoveSourceAsync(syncSourceWidgetId, syncSourcePaths);
                    ClearRemovedCutPaths();
                }

                ShowStatusToast(_localizationService.Format(
                    move ? "Widget.MovedToFolder" : "Widget.CopiedToFolder",
                    targetFolder.Name,
                    results.Count));
            }
            catch (Exception ex)
            {
                await ShowErrorDialogAsync(_localizationService.T("Widget.MoveToFolderFailed"), ex.Message);
            }
            finally
            {
                if (showOverlay)
                {
                    SetImportBusy(false);
                }
            }
        }
        catch (Exception ex)
        {
            await ShowErrorDialogAsync(_localizationService.T("Widget.MoveToFolderFailed"), ex.Message);
        }
        finally
        {
            deferral?.Complete();
        }
    }

    private static bool TryGetFolderDropTarget(object sender, out Border border, out WidgetItem folder)
    {
        if (sender is Border targetBorder &&
            targetBorder.DataContext is WidgetItem item &&
            item.IsFolder &&
            Directory.Exists(item.Path))
        {
            border = targetBorder;
            folder = item;
            return true;
        }

        border = null!;
        folder = null!;
        return false;
    }

    private void SetFolderDropTarget(Border border)
    {
        if (ReferenceEquals(_folderDropTarget, border))
        {
            ApplyWidgetItemSurfaceState(border, ItemSurfaceState.DropTarget);
            return;
        }

        ClearFolderDropTarget();
        _folderDropTarget = border;
        ApplyWidgetItemSurfaceState(border, ItemSurfaceState.DropTarget);
    }

    private void ClearFolderDropTarget()
    {
        if (_folderDropTarget is null)
        {
            return;
        }

        var border = _folderDropTarget;
        _folderDropTarget = null;
        ApplyWidgetItemSurfaceState(border, ItemSurfaceState.Normal);
    }

    private bool IsPointerOverFolderDropTarget()
    {
        if (!Win32Helper.GetCursorPos(out var cursor))
        {
            return false;
        }

        foreach (var border in _interactiveSurfaces.ToArray())
        {
            if (border.DataContext is not WidgetItem { IsFolder: true } folder ||
                !Directory.Exists(folder.Path) ||
                border.XamlRoot is null ||
                border.ActualWidth <= 0 ||
                border.ActualHeight <= 0)
            {
                continue;
            }

            var topLeft = border.TransformToVisual(null)
                .TransformPoint(new Windows.Foundation.Point(0, 0));
            double scale = RootGrid.XamlRoot?.RasterizationScale ?? 1.0;
            var left = _appWindow.Position.X + (topLeft.X * scale);
            var top = _appWindow.Position.Y + (topLeft.Y * scale);
            var right = left + (border.ActualWidth * scale);
            var bottom = top + (border.ActualHeight * scale);

            if (cursor.X >= left &&
                cursor.X <= right &&
                cursor.Y >= top &&
                cursor.Y <= bottom)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Determines whether the import overlay should be shown based on the
    /// total size of the files being transferred.  Small files complete
    /// almost instantly, so showing the overlay would cause an annoying flash.
    /// </summary>
}
