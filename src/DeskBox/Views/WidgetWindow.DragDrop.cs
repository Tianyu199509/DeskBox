﻿// Copyright (c) DeskBox. All rights reserved.

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

/// <summary>
/// Partial class containing drag-and-drop, file drop subclass,
/// folder drop target, and item drag package logic for WidgetWindow.
/// </summary>
public sealed partial class WidgetWindow
{

    private const string DeskBoxInternalDragToken = "DeskBox.WidgetItemDrag.v2";
    private static readonly UIntPtr FileDropSubclassId = new(0xDDB0);
    private readonly Win32Helper.SubclassProc _fileDropSubclassProc;
    private string[] _activeDragSourcePaths = [];
    private bool _activeDragHasStorageItems;
    private string? _lastRootDragDiagnosticSignature;
    private string? _lastFolderDragDiagnosticSignature;
    private Border? _folderDropTarget;
    private bool _surfaceDragCompletionHandled;
    private bool _isFileDropSubclassInstalled;

    // ── Native IDropTarget (OLE drag-drop) ──
    private NativeDropTarget? _nativeDropTarget;
    private bool _isNativeDragActive;
    private Border? _nativeDragHighlightBorder;

    private void InstallFileDropSubclass()
    {
        if (_isFileDropSubclassInstalled)
        {
            return;
        }

        _isFileDropSubclassInstalled = Win32Helper.SetWindowSubclass(
            _hWnd,
            _fileDropSubclassProc,
            FileDropSubclassId,
            UIntPtr.Zero);
        App.LogVerbose(
            $"[DropDiagnostic] widget='{ViewModel.Name}' id={ViewModel.Config.Id} stage=NativeSubclassInstall " +
            $"hwnd=0x{_hWnd.ToInt64():X} installed={_isFileDropSubclassInstalled}");

        // Register IDropTarget for richer drag-over feedback.
        // This supersedes WM_DROPFILES for OLE-aware drag sources (Explorer).
        // WM_DROPFILES is kept as fallback for legacy sources.
        try
        {
            _nativeDropTarget = new NativeDropTarget(_hWnd);
            _nativeDropTarget.DragEnterEvent += OnNativeDragEnter;
            _nativeDropTarget.DragOverEvent += OnNativeDragOver;
            _nativeDropTarget.DragLeaveEvent += OnNativeDragLeave;
            _nativeDropTarget.DropEvent += OnNativeDrop;
            _nativeDropTarget.Register();
        }
        catch (Exception ex)
        {
            App.Log($"[DropTarget] Failed to register IDropTarget: {ex.Message}");
            _nativeDropTarget = null;
        }
    }

    private void RemoveFileDropSubclass()
    {
        if (!_isFileDropSubclassInstalled)
        {
            return;
        }

        Win32Helper.RemoveWindowSubclass(_hWnd, _fileDropSubclassProc, FileDropSubclassId);
        _isFileDropSubclassInstalled = false;

        if (_nativeDropTarget is not null)
        {
            _nativeDropTarget.DragEnterEvent -= OnNativeDragEnter;
            _nativeDropTarget.DragOverEvent -= OnNativeDragOver;
            _nativeDropTarget.DragLeaveEvent -= OnNativeDragLeave;
            _nativeDropTarget.DropEvent -= OnNativeDrop;
            _nativeDropTarget.Dispose();
            _nativeDropTarget = null;
        }

        ClearNativeDragHighlight();
    }

    private IntPtr FileDropSubclassProc(
        IntPtr hWnd,
        uint message,
        UIntPtr wParam,
        IntPtr lParam,
        UIntPtr uIdSubclass,
        UIntPtr dwRefData)
    {
        if (message == Win32Helper.WM_DROPFILES)
        {
            var paths = Win32Helper.GetDroppedFilePaths((IntPtr)wParam);
            App.Log(
                $"[DropDiagnostic] widget='{ViewModel.Name}' id={ViewModel.Config.Id} stage=NativeDropFiles " +
                $"count={paths.Count} mapped={!string.IsNullOrWhiteSpace(ViewModel.MappedFolderPath)} " +
                $"managed={ViewModel.FollowsDefaultStoragePath}");
            if (paths.Count > 0)
            {
                DispatcherQueue.TryEnqueue(async () => await ImportNativeDropPathsAsync(paths));
            }

            return IntPtr.Zero;
        }

        return Win32Helper.DefSubclassProc(hWnd, message, wParam, lParam);
    }

    private async Task ImportNativeDropPathsAsync(
        IReadOnlyList<string> paths,
        bool cleanupTemporaryFiles = false)
    {
        if (_isMigrationBusy || paths.Count == 0)
        {
            return;
        }

        bool? moveWhenMapped = null;
        try
        {
            bool movesIntoFolder = !string.IsNullOrEmpty(ViewModel.MappedFolderPath);
            var acceptedOperation = NormalizePathDropOperation(DataPackageOperation.Copy | DataPackageOperation.Move, movesIntoFolder);
            if (acceptedOperation == DataPackageOperation.None)
            {
                App.Log(
                    $"[DropDiagnostic] widget='{ViewModel.Name}' id={ViewModel.Config.Id} stage=NativeDropRejectedOperation " +
                    $"count={paths.Count} mapped={movesIntoFolder}");
                return;
            }

            moveWhenMapped = movesIntoFolder
                ? ShouldMoveForAcceptedOperation(acceptedOperation)
                : null;
        }
        catch (Exception ex)
        {
            App.Log($"[DropDiagnostic] NativeDropPrecheck failed widget='{ViewModel.Name}' id={ViewModel.Config.Id}: {ex}");
            return;
        }

        // Show the import overlay for large transfers, same as the WinUI drop path.
        bool showOverlay = ShouldShowImportOverlay(paths);
        if (showOverlay)
        {
            SetImportBusy(true);
        }
        try
        {
            await ViewModel.ImportPathsAsync(paths, moveWhenMapped, useShellProgress: moveWhenMapped == true);
            ClearCutState();
        }
        catch (Exception ex)
        {
            App.Log($"[DropDiagnostic] NativeDropFailed widget='{ViewModel.Name}' id={ViewModel.Config.Id}: {ex}");
        }
        finally
        {
            if (showOverlay)
            {
                SetImportBusy(false);
            }
            if (cleanupTemporaryFiles)
            {
                CleanupNativeTemporaryDropFiles(paths);
            }
        }
    }

    // ── IDropTarget event handlers (native OLE drag-drop) ──

    private void OnNativeDragEnter(int screenX, int screenY, bool hasFileData)
    {
        if (!hasFileData || _isMigrationBusy)
        {
            return;
        }

        _isNativeDragActive = true;
        DispatcherQueue.TryEnqueue(() =>
        {
            StartDragHighlight();
            ShowNativeDragHighlight(screenX, screenY);
        });
    }

    private void OnNativeDragOver(int screenX, int screenY)
    {
        if (!_isNativeDragActive)
        {
            return;
        }

        DispatcherQueue.TryEnqueue(() =>
        {
            ShowNativeDragHighlight(screenX, screenY);
        });
    }

    private void OnNativeDragLeave()
    {
        _isNativeDragActive = false;
        DispatcherQueue.TryEnqueue(() =>
        {
            ClearNativeDragHighlight();
            StopDragHighlight();
        });
    }

    private void OnNativeDrop(
        IReadOnlyList<string> paths,
        int screenX,
        int screenY,
        bool containsTemporaryFiles)
    {
        _isNativeDragActive = false;
        DispatcherQueue.TryEnqueue(() =>
        {
            ClearNativeDragHighlight();
            StopDragHighlight();
        });

        if (paths.Count == 0)
        {
            return;
        }

        App.Log(
            $"[DropDiagnostic] widget='{ViewModel.Name}' id={ViewModel.Config.Id} stage=NativeIDropTargetDrop " +
            $"count={paths.Count} mapped={!string.IsNullOrWhiteSpace(ViewModel.MappedFolderPath)} " +
            $"managed={ViewModel.FollowsDefaultStoragePath}");

        // Check if the drop is over a folder item
        if (TryGetFolderItemAtScreenPoint(screenX, screenY, out var folderBorder, out var folderItem))
        {
            DispatcherQueue.TryEnqueue(async () =>
            {
                bool showOverlay = ShouldShowImportOverlay(paths);
                if (showOverlay)
                {
                    SetImportBusy(true);
                }
                try
                {
                    bool move = ShouldMoveForAcceptedOperation(
                        NormalizePathDropOperation(DataPackageOperation.Copy | DataPackageOperation.Move, movesIntoFolder: true));
                    var results = await App.Current.FileService.TransferItemsWithResultAsync(
                        paths, folderItem.Path, move);

                    if (!string.IsNullOrWhiteSpace(ViewModel.MappedFolderPath))
                    {
                        await ViewModel.RefreshFromConfigAsync();
                    }

                    ShowStatusToast(_localizationService.Format(
                        move ? "Widget.MovedToFolder" : "Widget.CopiedToFolder",
                        folderItem.Name,
                        results.Count));
                }
                catch (Exception ex)
                {
                    App.Log($"[DropTarget] Folder drop failed: {ex.Message}");
                }
                finally
                {
                    if (showOverlay)
                    {
                        SetImportBusy(false);
                    }
                    if (containsTemporaryFiles)
                    {
                        CleanupNativeTemporaryDropFiles(paths);
                    }
                }
            });
            return;
        }

        DispatcherQueue.TryEnqueue(async () =>
            await ImportNativeDropPathsAsync(paths, containsTemporaryFiles));
    }

    private static void CleanupNativeTemporaryDropFiles(IReadOnlyList<string> paths)
    {
        string temporaryRoot = Path.GetFullPath(Path.Combine(
            Path.GetTempPath(),
            "DeskBox",
            "VirtualDrops"))
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        foreach (string directory in paths
                     .Select(Path.GetDirectoryName)
                     .Where(directory => !string.IsNullOrWhiteSpace(directory))
                     .Select(directory => Path.GetFullPath(directory!))
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            string normalizedDirectory = directory
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!normalizedDirectory.StartsWith(temporaryRoot, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            try
            {
                if (Directory.Exists(directory))
                {
                    Directory.Delete(directory, recursive: true);
                }
            }
            catch (Exception ex)
            {
                App.Log($"[DropTarget] Failed to clean virtual drop directory: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Shows visual highlight during native drag-over.
    /// Highlights the folder item under the cursor, or the root grid as fallback.
    /// </summary>
    private void ShowNativeDragHighlight(int screenX, int screenY)
    {
        if (TryGetFolderItemAtScreenPoint(screenX, screenY, out var border, out _))
        {
            if (!ReferenceEquals(_nativeDragHighlightBorder, border))
            {
                ClearNativeDragHighlight();
                _nativeDragHighlightBorder = border;
                ApplyWidgetItemSurfaceState(border, ItemSurfaceState.DropTarget);
            }
            return;
        }

        // No folder under cursor — clear folder highlight, show root-level feedback
        ClearNativeDragHighlight();
        // Root-level feedback is handled by the existing _isNativeDragActive flag
        // which could be used to show a subtle border glow on the window.
    }

    private void ClearNativeDragHighlight()
    {
        if (_nativeDragHighlightBorder is not null)
        {
            var border = _nativeDragHighlightBorder;
            _nativeDragHighlightBorder = null;
            ApplyWidgetItemSurfaceState(border, ItemSurfaceState.Normal);
        }

        // Also clear the shared folder drop target if set
        ClearFolderDropTarget();
    }

    /// <summary>
    /// Hit-tests a screen coordinate against interactive folder surfaces.
    /// Returns the border and WidgetItem if the point is over a folder item.
    /// </summary>
    private bool TryGetFolderItemAtScreenPoint(int screenX, int screenY, out Border border, out WidgetItem folder)
    {
        foreach (var surface in _interactiveSurfaces.ToArray())
        {
            if (surface.DataContext is not WidgetItem { IsFolder: true } item ||
                !Directory.Exists(item.Path) ||
                surface.XamlRoot is null ||
                surface.ActualWidth <= 0 ||
                surface.ActualHeight <= 0)
            {
                continue;
            }

            var topLeft = surface.TransformToVisual(null)
                .TransformPoint(new Windows.Foundation.Point(0, 0));
            double scale = RootGrid.XamlRoot?.RasterizationScale ?? 1.0;
            var left = _appWindow.Position.X + (topLeft.X * scale);
            var top = _appWindow.Position.Y + (topLeft.Y * scale);
            var right = left + (surface.ActualWidth * scale);
            var bottom = top + (surface.ActualHeight * scale);

            if (screenX >= left && screenX <= right &&
                screenY >= top && screenY <= bottom)
            {
                border = surface;
                folder = item;
                return true;
            }
        }

        border = null!;
        folder = null!;
        return false;
    }

    private void RootGrid_DragOver(object sender, DragEventArgs e)
    {
        e.Handled = true;
        ClearFolderDropTarget();

        if (_isMigrationBusy)
        {
            e.AcceptedOperation = DataPackageOperation.None;
            e.DragUIOverride.IsGlyphVisible = false;
            LogDropDiagnostic("RootDragOverBusy", e.DataView, e.AcceptedOperation, movesIntoFolder: false);
            return;
        }

        if (!HasPathDropData(e.DataView))
        {
            e.AcceptedOperation = DataPackageOperation.None;
            LogDropDiagnostic("RootDragOverNoPathData", e.DataView, e.AcceptedOperation, movesIntoFolder: false);
            return;
        }

        bool movesIntoFolder = !string.IsNullOrEmpty(ViewModel.MappedFolderPath);
        e.AcceptedOperation = NormalizePathDropOperation(e.DataView.RequestedOperation, movesIntoFolder);
        LogDropDiagnostic("RootDragOver", e.DataView, e.AcceptedOperation, movesIntoFolder);
        if (e.AcceptedOperation == DataPackageOperation.None)
        {
            e.DragUIOverride.IsGlyphVisible = false;
            return;
        }

        e.DragUIOverride.IsGlyphVisible = true;
        e.DragUIOverride.Caption = movesIntoFolder
            ? _localizationService.Format(
                GetRootFolderDropCaptionKey(),
                GetAcceptedOperationCaption(e.AcceptedOperation))
            : _localizationService.T("Widget.DragCaption.Reference");
    }

private void RootGrid_DragEnter(object sender, DragEventArgs e)
{
LogDropDiagnostic("RootDragEnter", e.DataView, e.AcceptedOperation, !string.IsNullOrEmpty(ViewModel.MappedFolderPath));
StartDragHighlight();
}

    private string GetRootFolderDropCaptionKey()
    {
        return ViewModel.FollowsDefaultStoragePath
            ? "Widget.DragCaption.Managed"
            : "Widget.DragCaption.Mapped";
    }

    private DataPackageOperation NormalizePathDropOperation(DataPackageOperation requestedOperation, bool movesIntoFolder)
    {
        if (requestedOperation == DataPackageOperation.None)
        {
            return movesIntoFolder
                ? GetManagedDropOperation()
                : DataPackageOperation.Link;
        }

        var operation = GetAcceptedDropOperation(requestedOperation, movesIntoFolder);
        if (operation != DataPackageOperation.None ||
            !movesIntoFolder ||
            !SupportsOperation(requestedOperation, DataPackageOperation.Link))
        {
            return operation;
        }

        return DataPackageOperation.Link;
    }

    private string GetAcceptedOperationCaption(DataPackageOperation acceptedOperation)
    {
        return ShouldMoveForAcceptedOperation(acceptedOperation)
            ? _localizationService.T("Common.Move")
            : _localizationService.T("Common.Copy");
    }

    private bool ShouldMoveForAcceptedOperation(DataPackageOperation acceptedOperation)
    {
        return acceptedOperation switch
        {
            DataPackageOperation.Copy => false,
            DataPackageOperation.Move => true,
            DataPackageOperation.Link => true,
            _ => true
        };
    }

private void RootGrid_DragLeave(object sender, DragEventArgs e)
{
e.Handled = true;
ClearFolderDropTarget();
StopDragHighlight();
_lastRootDragDiagnosticSignature = null;
}

private async void RootGrid_Drop(object sender, DragEventArgs e)
{
e.Handled = true;
ClearFolderDropTarget();
StopDragHighlight();
        _lastRootDragDiagnosticSignature = null;

        var deferral = e.GetDeferral();
        try
        {
            if (_isMigrationBusy)
            {
                return;
            }

            if (!HasPathDropData(e.DataView))
            {
                LogDropDiagnostic("RootDropNoPathData", e.DataView, e.AcceptedOperation, movesIntoFolder: false);
                return;
            }

            bool movesIntoFolder = !string.IsNullOrEmpty(ViewModel.MappedFolderPath);
            LogDropDiagnostic("RootDrop", e.DataView, e.AcceptedOperation, movesIntoFolder);

            var paths = await GetDropPathsAsync(e.DataView);
            if (paths.Length == 0)
            {
                App.Log(
                    $"[DropDiagnostic] widget='{ViewModel.Name}' id={ViewModel.Config.Id} stage=RootDropNoPaths " +
                    $"mapped={movesIntoFolder} requested={e.DataView.RequestedOperation} accepted={e.AcceptedOperation} " +
                    $"formats={FormatDataPackageFormats(e.DataView.AvailableFormats)}");
                return;
            }

            var acceptedOperation = e.AcceptedOperation == DataPackageOperation.None
                ? NormalizePathDropOperation(e.DataView.RequestedOperation, movesIntoFolder)
                : e.AcceptedOperation;
            e.AcceptedOperation = acceptedOperation;
            if (acceptedOperation == DataPackageOperation.None)
            {
                LogDropDiagnostic("RootDropRejectedOperation", e.DataView, acceptedOperation, movesIntoFolder);
                return;
            }

            bool? moveWhenMapped = movesIntoFolder
                ? ShouldMoveForAcceptedOperation(acceptedOperation)
                : null;

            string? sourceWidgetId = TryGetPackageString(e.DataView.Properties, "DeskBoxSourceWidgetId");
            if (movesIntoFolder &&
                HasDeskBoxInternalDragData(e.DataView.Properties) &&
                string.Equals(sourceWidgetId, ViewModel.Config.Id, StringComparison.Ordinal) &&
                moveWhenMapped == true)
            {
                return;
            }

            // Extract all needed data from the DataPackageView before completing
            // the deferral — the DataView becomes invalid after Complete().
            string? syncSourceWidgetId = TryGetPackageString(e.DataView.Properties, "DeskBoxSourceWidgetId");
            var syncSourcePaths = TryGetPackageStringArray(e.DataView.Properties, "DeskBoxSourcePaths");

            // Complete the deferral early so the drag glyph disappears immediately.
            // The actual file transfer continues in the background with a visual overlay.
            deferral.Complete();
            deferral = null;

            // Only show the import overlay for large transfers to avoid
            // flashing for small files.
            bool showOverlay = ShouldShowImportOverlay(paths);
            if (showOverlay)
            {
                SetImportBusy(true);
            }
            try
            {
                await ViewModel.ImportPathsAsync(paths, moveWhenMapped, useShellProgress: moveWhenMapped == true);

                if (moveWhenMapped == true)
                {
                    await SyncMoveSourceAsync(syncSourceWidgetId, syncSourcePaths);
                }

                ClearCutState();
            }
            catch (Exception ex)
            {
                App.Log($"[Widget] RootGrid_Drop failed: {ex}");
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
            App.Log($"[Widget] RootGrid_Drop failed: {ex}");
        }
        finally
        {
            deferral?.Complete();
        }
    }

    /// <summary>
    /// Shows the native Windows Explorer context menu for a single file item.
    /// Handles Z-order elevation, coordinate conversion, and foreground window management.
    /// </summary>
    /// <returns>True if the native menu was shown (regardless of whether a command was invoked); false if it failed and the caller should fall back.</returns>
    private void ItemsView_DragItemsStarting(object sender, DragItemsStartingEventArgs e)
    {
        e.Cancel = !TryPrepareItemDragPackage(
            e.Data,
            GetDragItems(e.Items.OfType<WidgetItem>().FirstOrDefault()));
    }

    private async void ItemsView_DragItemsCompleted(ListViewBase sender, DragItemsCompletedEventArgs args)
    {
        try
        {
            await HandleItemDragCompletedAsync(args.DropResult);
        }
        catch (Exception ex)
        {
            App.Log($"[Widget] ItemsView_DragItemsCompleted failed: {ex}");
        }
    }

    private async Task HandleItemDragCompletedAsync(DataPackageOperation dropResult)
    {
        var draggedPaths = _activeDragSourcePaths;
        bool hasStorageItems = _activeDragHasStorageItems;
        _activeDragSourcePaths = [];
        _activeDragHasStorageItems = false;

        if (draggedPaths.Length == 0)
        {
            return;
        }

        bool cursorOnDesktop = IsCursorOnDesktop();
        bool cursorOverWindow = IsCursorOverThisWindow();

        App.Log(
            $"[DragComplete] widget='{ViewModel.Name}' followsDefault={ViewModel.FollowsDefaultStoragePath} " +
            $"mapped='{ViewModel.MappedFolderPath}' dropResult={dropResult} hasStorageItems={hasStorageItems} " +
            $"cursorOnDesktop={cursorOnDesktop} cursorOverWindow={cursorOverWindow} paths={draggedPaths.Length}");

        if (ViewModel.FollowsDefaultStoragePath &&
            cursorOnDesktop &&
            !cursorOverWindow)
        {
            if (hasStorageItems || dropResult == DataPackageOperation.Move)
            {
                await RefreshAfterDragOutAsync(draggedPaths);
                return;
            }

            await MoveDraggedPathsBackToDesktopAsync(draggedPaths, useShellProgress: true);
            return;
        }

        // For mapped folder widgets, when items are dragged out to desktop
        // (or any location outside this widget), the shell handles the
        // move directly via OLE.  WinUI reports dropResult=None because
        // the drop happened outside any WinUI drop target.  We need to
        // refresh to remove the moved items.
        if (!ViewModel.FollowsDefaultStoragePath &&
            !string.IsNullOrEmpty(ViewModel.MappedFolderPath) &&
            !cursorOverWindow &&
            (hasStorageItems || dropResult == DataPackageOperation.Move))
        {
            await RefreshAfterDragOutAsync(draggedPaths);
            return;
        }

        if (dropResult != DataPackageOperation.Move)
        {
            return;
        }

        ClearRemovedCutPaths();
    }

    /// <summary>
    /// Handles the post-drag-out refresh for items dragged from this widget
    /// to an external target (Explorer, desktop, etc.).
    ///
    /// The Shell OLE move may take anywhere from milliseconds (small files)
    /// to many minutes (large files/folders).  We must NOT:
    /// - Do optimistic removal (we don't know if it's a copy or a move)
    /// - Call RefreshFromConfigAsync (it restarts the folder watcher,
    ///   clearing pending events and potentially missing the deletion)
    /// - Poll continuously at a fixed interval (wastes resources for long transfers)
    ///
    /// Instead we use exponential backoff: start at 300ms and double each
    /// iteration, capped at 5 minutes.  This gives:
    ///   300ms → 600ms → 1.2s → 2.4s → 4.8s → 9.6s → 19s → 38s → 76s → 152s → 300s
    /// Small files are caught within ~300-600ms; large files are caught
    /// whenever the transfer finishes.  Each check is a single File.Exists
    /// probe — essentially zero cost.
    /// </summary>
    private async Task RefreshAfterDragOutAsync(string[] draggedPaths)
    {
        ClearRemovedCutPaths();
        UpdateEmptyState();

        int delayMs = 300;
        while (delayMs <= 300_000)
        {
            await Task.Delay(delayMs);

            // Window may have been closed while we were waiting.
            if (IsClosing)
            {
                return;
            }

            bool anyExists = false;
            foreach (var path in draggedPaths)
            {
                if (File.Exists(path) || Directory.Exists(path))
                {
                    anyExists = true;
                    break;
                }
            }

            if (!anyExists)
            {
                App.Log($"[DragComplete] Files gone after ~{delayMs}ms, refreshing.");
                await ViewModel.RefreshFolderContentsAsync();
                ClearRemovedCutPaths();
                UpdateEmptyState();
                return;
            }

            delayMs = (int)Math.Min(delayMs * 2, 300_000);
        }

        App.Log("[DragComplete] Files still exist after 10min, giving up.");
    }

    private IReadOnlyList<WidgetItem> GetDragItems(WidgetItem? fallbackItem)
    {
        var selectedItems = GetSelectedItems();
        if (fallbackItem is null)
        {
            return selectedItems;
        }

        if (selectedItems.Any(item => ReferenceEquals(item, fallbackItem)))
        {
            return selectedItems;
        }

        return [fallbackItem];
    }

    private bool TryPrepareItemDragPackage(DataPackage dataPackage, IReadOnlyList<WidgetItem> draggedItems)
    {
        if (draggedItems.Count == 0)
        {
            _activeDragSourcePaths = [];
            _activeDragHasStorageItems = false;
            return false;
        }

        var sourcePaths = draggedItems
            .Select(item => item.Path)
            .Where(path => !string.IsNullOrWhiteSpace(path) && (File.Exists(path) || Directory.Exists(path)))
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (sourcePaths.Length == 0)
        {
            _activeDragSourcePaths = [];
            _activeDragHasStorageItems = false;
            return false;
        }

        dataPackage.RequestedOperation = DataPackageOperation.Copy | DataPackageOperation.Move;

        var storageItems = App.Current.FileService.GetStorageItems(sourcePaths);
        if (storageItems.Count > 0)
        {
            dataPackage.SetStorageItems(storageItems, false);
        }
        else
        {
            App.Log($"[DragStart] No StorageItems for selected paths; using path fallback. paths={sourcePaths.Length}");
        }

        dataPackage.Properties["DeskBoxSourceWidgetId"] = ViewModel.Config.Id;
        dataPackage.Properties["DeskBoxSourcePaths"] = sourcePaths;
        dataPackage.Properties["DeskBoxInternalDragToken"] = DeskBoxInternalDragToken;
        dataPackage.Properties.Title = sourcePaths.Length == 1
            ? Path.GetFileName(sourcePaths[0])
            : _localizationService.Format("Widget.ItemCount", sourcePaths.Length);
        dataPackage.SetText(string.Join(Environment.NewLine, sourcePaths));
        _activeDragSourcePaths = sourcePaths;
        _activeDragHasStorageItems = storageItems.Count > 0;
        return true;
    }

    private async Task SyncMoveSourceAsync(string? sourceWidgetId, IReadOnlyList<string> sourcePaths)
    {
        if (string.IsNullOrWhiteSpace(sourceWidgetId) || sourcePaths.Count == 0)
        {
            return;
        }

        if (string.Equals(sourceWidgetId, ViewModel.Config.Id, StringComparison.Ordinal))
        {
            if (!string.IsNullOrEmpty(ViewModel.MappedFolderPath))
            {
                await ViewModel.RefreshFromConfigAsync();
            }

            return;
        }

        if (App.Current.WidgetManager is { } widgetManager)
        {
            await widgetManager.NotifyItemsMovedOutAsync(sourceWidgetId, sourcePaths);
        }
    }

    private static bool HasDeskBoxInternalDragData(DataPackagePropertySetView properties)
    {
        return string.Equals(
                   TryGetPackageString(properties, "DeskBoxInternalDragToken"),
                   DeskBoxInternalDragToken,
                   StringComparison.Ordinal) &&
               TryGetPackageStringArray(properties, "DeskBoxSourcePaths").Count > 0;
    }

    private static bool HasPathDropData(DataPackageView dataView)
    {
        return TryGetPackageStringArray(dataView.Properties, "DeskBoxSourcePaths").Count > 0 ||
               dataView.Contains(StandardDataFormats.StorageItems) ||
               dataView.Contains(StandardDataFormats.Text) ||
               HasFallbackFileFormats(dataView);
    }

    private static async Task<string[]> GetDropPathsAsync(DataPackageView dataView)
    {
        var sourcePaths = TryGetPackageStringArray(dataView.Properties, "DeskBoxSourcePaths")
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (sourcePaths.Length > 0)
        {
            return sourcePaths;
        }

        if (dataView.Contains(StandardDataFormats.StorageItems) ||
            HasFallbackFileFormats(dataView))
        {
            try
            {
                var items = await dataView.GetStorageItemsAsync();
                sourcePaths = items
                    .Select(item => item.Path)
                    .Where(path => !string.IsNullOrWhiteSpace(path))
                    .Select(Path.GetFullPath)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                App.Log(
                    $"[DropDiagnostic] GetStorageItemsAsync count={items.Count} pathCount={sourcePaths.Length} " +
                    $"formats={FormatDataPackageFormats(dataView.AvailableFormats)}");
                if (sourcePaths.Length > 0)
                {
                    return sourcePaths;
                }
            }
            catch (Exception ex)
            {
                App.Log($"[DropDiagnostic] GetStorageItemsAsync failed: {ex.Message}");
            }

            sourcePaths = await TryGetLegacyFormatPathsAsync(dataView);
            if (sourcePaths.Length > 0)
            {
                return sourcePaths;
            }
        }

        if (!dataView.Contains(StandardDataFormats.Text))
        {
            return [];
        }

        string text = await dataView.GetTextAsync();
        return text
            .Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(candidate => TryNormalizeDroppedPath(candidate, out string normalizedPath) ? normalizedPath : null)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => path!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static bool TryNormalizeDroppedPath(string candidate, out string normalizedPath)
    {
        normalizedPath = string.Empty;

        try
        {
            string fullPath = Path.GetFullPath(candidate.Trim().Trim('"'));
            if (File.Exists(fullPath) || Directory.Exists(fullPath))
            {
                normalizedPath = fullPath;
                return true;
            }
        }
        catch
        {
        }

        return false;
    }

    private void LogDropDiagnostic(
        string stage,
        DataPackageView dataView,
        DataPackageOperation acceptedOperation,
        bool movesIntoFolder)
    {
        string signature = $"{stage}|{dataView.RequestedOperation}|{acceptedOperation}|{movesIntoFolder}|{FormatDataPackageFormats(dataView.AvailableFormats)}";
        if (stage.StartsWith("Folder", StringComparison.Ordinal))
        {
            if (string.Equals(_lastFolderDragDiagnosticSignature, signature, StringComparison.Ordinal))
            {
                return;
            }

            _lastFolderDragDiagnosticSignature = signature;
        }
        else
        {
            if (string.Equals(_lastRootDragDiagnosticSignature, signature, StringComparison.Ordinal))
            {
                return;
            }

            _lastRootDragDiagnosticSignature = signature;
        }

        App.Log(
            $"[DropDiagnostic] widget='{ViewModel.Name}' id={ViewModel.Config.Id} stage={stage} " +
            $"mapped={!string.IsNullOrWhiteSpace(ViewModel.MappedFolderPath)} managed={ViewModel.FollowsDefaultStoragePath} " +
            $"requested={dataView.RequestedOperation} accepted={acceptedOperation} " +
            $"containsStorage={dataView.Contains(StandardDataFormats.StorageItems)} containsText={dataView.Contains(StandardDataFormats.Text)} " +
            $"fallback={HasFallbackFileFormats(dataView)} " +
            $"formats={FormatDataPackageFormats(dataView.AvailableFormats)}");
    }

    private static string FormatDataPackageFormats(IReadOnlyList<string> formats)
    {
        return formats.Count == 0
            ? "<none>"
            : string.Join(",", formats.OrderBy(format => format, StringComparer.Ordinal));
    }

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
    private static bool ShouldShowImportOverlay(IReadOnlyList<string> paths)
    {
        const long ThresholdBytes = 10 * 1024 * 1024; // 10 MB

        long totalSize = 0;
        foreach (string path in paths)
        {
            try
            {
                if (File.Exists(path))
                {
                    totalSize += new FileInfo(path).Length;
                }
                else if (Directory.Exists(path))
                {
                    // For directories, enumerate is too expensive — assume
                    // large and show the overlay.
                    return true;
                }
            }
            catch
            {
                // If we can't stat the file, err on the side of showing the overlay.
                return true;
            }

            if (totalSize >= ThresholdBytes)
            {
                return true;
            }
        }

        return totalSize >= ThresholdBytes;
    }

    private static bool IsInvalidFolderDrop(IReadOnlyList<string> sourcePaths, string destinationFolder)
    {
        if (string.IsNullOrWhiteSpace(destinationFolder) || sourcePaths.Count == 0)
        {
            return false;
        }

        string normalizedDestination = Path.GetFullPath(destinationFolder);
        foreach (string sourcePath in sourcePaths)
        {
            if (string.IsNullOrWhiteSpace(sourcePath))
            {
                continue;
            }

            string normalizedSource = Path.GetFullPath(sourcePath);
            if (string.Equals(normalizedSource, normalizedDestination, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (Directory.Exists(normalizedSource) &&
                FileService.IsPathUnderDirectory(normalizedDestination, normalizedSource))
            {
                return true;
            }
        }

        return false;
    }

    private async Task MoveDraggedPathsBackToDesktopAsync(IReadOnlyList<string> sourcePaths, bool useShellProgress)
    {
        var pathSet = sourcePaths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(Path.GetFullPath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (pathSet.Count == 0)
        {
            return;
        }

        var draggedItems = ViewModel.Items
            .Where(item =>
                pathSet.Contains(Path.GetFullPath(item.Path)) &&
                (File.Exists(item.Path) || Directory.Exists(item.Path)))
            .ToList();
        if (draggedItems.Count == 0)
        {
            await ViewModel.RefreshFromConfigAsync();
            ClearRemovedCutPaths();
            UpdateEmptyState();
            return;
        }

        try
        {
            int movedCount = await ViewModel.MoveItemsBackToDesktopAsync(draggedItems, useShellProgress);

            ClearRemovedCutPaths();
            ShowStatusToast(movedCount > 0
                ? _localizationService.Format("Widget.MovedToDesktop", movedCount)
                : _localizationService.T("Widget.NoItemsMoved"));
        }
        catch (Exception ex)
        {
            await ShowErrorDialogAsync(_localizationService.T("Widget.MoveToDesktopFailed"), ex.Message);
            await ViewModel.RefreshFromConfigAsync();
            UpdateEmptyState();
        }
    }

    private DataPackageOperation GetManagedDropOperation()
    {
        return DataPackageOperation.Move;
    }

    private DataPackageOperation GetAcceptedDropOperation(DataPackageOperation requestedOperation, bool movesIntoFolder)
    {
        if (!movesIntoFolder)
        {
            if (SupportsOperation(requestedOperation, DataPackageOperation.Link))
            {
                return DataPackageOperation.Link;
            }

            return SupportsOperation(requestedOperation, DataPackageOperation.Copy) || requestedOperation == DataPackageOperation.None
                ? DataPackageOperation.Copy
                : DataPackageOperation.None;
        }

        bool ctrlPressed = Win32Helper.IsKeyPressed(Windows.System.VirtualKey.Control);
        if (ctrlPressed)
        {
            return DataPackageOperation.Copy;
        }

        var preferredOperation = GetManagedDropOperation();
        if (CanUseRequestedOperation(requestedOperation, preferredOperation))
        {
            return preferredOperation;
        }

        var fallbackOperation = preferredOperation == DataPackageOperation.Move
            ? DataPackageOperation.Copy
            : DataPackageOperation.Move;
        return CanUseRequestedOperation(requestedOperation, fallbackOperation)
            ? fallbackOperation
            : DataPackageOperation.None;
    }

    private static bool SupportsOperation(DataPackageOperation requestedOperation, DataPackageOperation operation)
    {
        return (requestedOperation & operation) == operation;
    }

    private bool CanMoveItemsBackToDesktop()
    {
        return !string.IsNullOrWhiteSpace(ViewModel.MappedFolderPath);
    }

}
