﻿﻿﻿// Copyright (c) DeskBox. All rights reserved.

using DeskBox.Helpers;
using DeskBox.Models;
using DeskBox.Services;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
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

    // ── Real-time reorder state ──
    private bool _isReorderDragActive;
    private string[] _reorderDragPaths = [];
    private DataPackageView? _pendingDropDataView;

    // ── Drop poll (compensates for WinUI 3 Drop not firing) ──
    // WinUI 3's managed Drop event sometimes fails to fire for non-Chromium
    // OLE drag sources (e.g., WeChat chat files). DragOver only fires on
    // mouse movement, and DispatcherQueueTimer doesn't fire during OLE's
    // modal message loop, so neither can detect release when the mouse is
    // stationary. A dedicated background thread polls GetAsyncKeyState
    // (VK_LBUTTON) independently of the message pump and marshals the result
    // back to the UI thread.
    //
    // Key insight: the DataPackageView becomes invalid AFTER the OLE drag
    // ends. So the poll thread can't read it. Instead, we pre-cache paths
    // during DragOver (when data is still valid) and the poll thread uses
    // the cached paths.
    private CancellationTokenSource? _dropPollCts;
    private bool _isManualDropInProgress;
    private string[]? _cachedDropPaths;
    private bool _isCachingDropPaths;

    private async void RootGrid_DragOver(object sender, DragEventArgs e)
    {
        e.Handled = true;
        ClearFolderDropTarget();

        // WORKAROUND: WinUI 3's Drop event sometimes fails to fire for non-Chromium
        // OLE drag sources (e.g., WeChat chat files). Detect mouse-button release
        // inside DragOver and process the drop manually when that happens.
        // The poll timer (_dropPollTimer) also monitors for release in case
        // DragOver doesn't fire again after the button is released.
        bool isLeftButtonReleased = !Win32Helper.IsKeyDown(0x01);
        if (isLeftButtonReleased && _pendingDropDataView is not null && !_isManualDropInProgress)
        {
            var capturedView = _pendingDropDataView;
            _pendingDropDataView = null;
            _isManualDropInProgress = true;
            StopDropPoll();
            StopDragHighlight();

            App.Log("[DropDiagnostic] Detected mouse release in DragOver — processing drop manually");
            try
            {
                var paths = await GetDropPathsAsync(capturedView);
                App.Log($"[DropDiagnostic] Manual drop pathCount={paths.Length} paths=[{string.Join("|", paths)}]");
                if (paths.Length > 0)
                {
                    await ProcessManualDropAsync(paths);
                }
            }
            catch (Exception ex)
            {
                App.Log($"[DropDiagnostic] Manual drop failed: {ex}");
            }
            finally
            {
                _isManualDropInProgress = false;
            }
            return;
        }

        _pendingDropDataView = e.DataView;
        EnsureDropPoll();

        // Pre-cache drop paths while the DataPackageView is still valid
        // (during DragOver, inside the OLE modal loop). The poll thread
        // will use these cached paths when it detects mouse release,
        // because by then the DataPackageView will be invalid.
        TryCacheDropPaths(e.DataView);

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

        // Same-widget internal drag: real-time reordering.
        // All sort modes allow drag reorder — dragging switches to Manual mode
        // and items move in real-time to show where the drop will land.
        if (HasDeskBoxInternalDragData(e.DataView.Properties))
        {
            string? sourceWidgetId = TryGetPackageString(e.DataView.Properties, "DeskBoxSourceWidgetId");
            if (string.Equals(sourceWidgetId, ViewModel.Config.Id, StringComparison.Ordinal))
            {
                e.AcceptedOperation = DataPackageOperation.Link;
                e.DragUIOverride.IsGlyphVisible = false;
                e.DragUIOverride.Caption = _localizationService.T("Widget.DragCaption.Reorder");

                // Perform real-time reordering for visual feedback.
                HandleRealTimeReorder(e.DataView.Properties, e.GetPosition(GetDropTargetControl()));
                return;
            }
        }

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
_cachedDropPaths = null;
_isCachingDropPaths = false;
_pendingDropDataView = e.DataView;
EnsureDropPoll();

// Pre-cache drop paths while data is still valid.
TryCacheDropPaths(e.DataView);
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
    App.Log("[DropDiagnostic] RootGrid_DragLeave fired");
    // DON'T clear _pendingDropDataView or stop the poll here.
    // When the OLE drag ends (button released), WinUI 3 may fire DragLeave
    // instead of Drop (the bug). If we clear the data now, the background
    // poll thread can't process the drop. Instead, let the poll thread detect
    // the release and process it. The poll thread will clean up itself.
    ClearFolderDropTarget();
    StopDragHighlight();
    _lastRootDragDiagnosticSignature = null;

    // Persist any real-time reordering that was done during DragOver.
    if (_isReorderDragActive)
    {
        _isReorderDragActive = false;
        _reorderDragPaths = [];
        ViewModel.PersistManualOrder();
    }
}

private async void RootGrid_Drop(object sender, DragEventArgs e)
{
    e.Handled = true;
    _pendingDropDataView = null;
    _cachedDropPaths = null;
    _isCachingDropPaths = false;
    StopDropPoll();
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

            // ── Same-widget internal drag: persist real-time reorder ──
            if (HasDeskBoxInternalDragData(e.DataView.Properties) &&
                string.Equals(sourceWidgetId, ViewModel.Config.Id, StringComparison.Ordinal))
            {
                // Real-time reordering was done during DragOver.  If the mode
                // wasn't Manual yet, switch now (HandleRealTimeReorder already
                // did this, but this covers edge cases).
                if (ViewModel.Config.SortMode != WidgetSortMode.Manual)
                {
                    ViewModel.SetSortMode(WidgetSortMode.Manual);
                }

                // Do a final reorder to the exact drop position, then persist.
                var dragPaths = TryGetPackageStringArray(e.DataView.Properties, "DeskBoxSourcePaths");
                HandleFinalReorder(dragPaths, e.GetPosition(GetDropTargetControl()));
                ViewModel.PersistManualOrder();

                _isReorderDragActive = false;
                _reorderDragPaths = [];

                // Same-widget drop: no file transfer needed regardless of mode.
                return;
            }

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

    // ── Real-time reorder helpers ────────────────────────────────

    /// <summary>
    /// Returns the active items control (GridView or ListView) that
    /// should be used for hit-testing the drop position.
    /// </summary>
    private UIElement GetDropTargetControl()
    {
        return ItemsGridView.Visibility == Visibility.Visible
            ? ItemsGridView
            : ItemsListView;
    }

    /// <summary>
    /// Performs real-time reordering during DragOver.  Moves the dragged
    /// item to the insertion index so other items shift to make room.
    /// Switches to Manual mode on first call.
    /// </summary>
    private void HandleRealTimeReorder(
        DataPackagePropertySetView properties,
        Windows.Foundation.Point position)
    {
        // Skip when file stacks are enabled — VisibleItems != Items.
        if (ViewModel.FileStacksEnabled)
        {
            return;
        }

        var dragPaths = TryGetPackageStringArray(properties, "DeskBoxSourcePaths");
        if (dragPaths.Count == 0)
        {
            return;
        }

        // Switch to Manual mode if needed (only once per drag).
        if (!_isReorderDragActive)
        {
            if (ViewModel.Config.SortMode != WidgetSortMode.Manual)
            {
                ViewModel.SetSortMode(WidgetSortMode.Manual);
            }
            _isReorderDragActive = true;
            _reorderDragPaths = dragPaths.ToArray();
        }

        // Find the dragged item (single-item drag is most common).
        var pathSet = _reorderDragPaths
            .Select(Path.GetFullPath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var draggedItem = ViewModel.Items
            .FirstOrDefault(item => pathSet.Contains(Path.GetFullPath(item.Path)));

        if (draggedItem is null)
        {
            return;
        }

        int currentIndex = ViewModel.Items.IndexOf(draggedItem);
        if (currentIndex < 0)
        {
            return;
        }

        int targetIndex = ComputeDropInsertionIndex(GetDropTargetControl(), position);

        // Adjust for Move semantics: Move(oldIndex, newIndex) puts the item
        // AT newIndex.  If target > current, we need target-1 so the item
        // ends up visually where the insertion indicator shows.
        if (targetIndex > currentIndex)
        {
            targetIndex--;
        }

        // Skip if no meaningful move.
        if (targetIndex == currentIndex || targetIndex < 0)
        {
            return;
        }

        ViewModel.MoveItemForReorder(draggedItem, targetIndex);
    }

    /// <summary>
    /// Final reorder on drop — moves the item to the exact drop position.
    /// </summary>
    private void HandleFinalReorder(
        IReadOnlyList<string> dragPaths,
        Windows.Foundation.Point dropPosition)
    {
        if (dragPaths.Count == 0 || ViewModel.FileStacksEnabled)
        {
            return;
        }

        var pathSet = dragPaths
            .Select(Path.GetFullPath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var draggedItem = ViewModel.Items
            .FirstOrDefault(item => pathSet.Contains(Path.GetFullPath(item.Path)));

        if (draggedItem is null)
        {
            return;
        }

        int currentIndex = ViewModel.Items.IndexOf(draggedItem);
        if (currentIndex < 0)
        {
            return;
        }

        int targetIndex = ComputeDropInsertionIndex(GetDropTargetControl(), dropPosition);

        if (targetIndex > currentIndex)
        {
            targetIndex--;
        }

        if (targetIndex == currentIndex || targetIndex < 0)
        {
            return;
        }

        ViewModel.MoveItemForReorder(draggedItem, targetIndex);
    }

    // ── Drop poll (background thread) ─────────────────────────

    /// <summary>
    /// Starts a background thread that polls GetAsyncKeyState(VK_LBUTTON)
    /// to detect mouse-button release during an OLE drag. The DispatcherQueue
    /// timer approach doesn't work because OLE's modal message loop prevents
    /// it from firing. A background thread is independent of the message pump.
    /// </summary>
    private void EnsureDropPoll()
    {
        if (_dropPollCts is not null)
        {
            return; // Already polling
        }

        _dropPollCts = new CancellationTokenSource();
        var token = _dropPollCts.Token;
        App.Log("[DropDiagnostic] Drop poll thread starting (v2-background)");

        Task.Run(async () =>
        {
            try
            {
                // Safety timeout: stop after 5 seconds to avoid orphan threads
                // and minimize interference with Z-order's GetAsyncKeyState usage.
                var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(token);
                timeoutCts.CancelAfter(TimeSpan.FromSeconds(5));

                while (!timeoutCts.Token.IsCancellationRequested)
                {
                    await Task.Delay(30, timeoutCts.Token);

                    // Button still held — keep polling.
                    if (Win32Helper.IsKeyDown(0x01))
                    {
                        continue;
                    }

                    // Button released! Marshal to UI thread.
                    App.Log("[DropDiagnostic] Poll thread detected button release, marshaling to UI");
                    DispatcherQueue.TryEnqueue(OnDropPollDetectedRelease);
                    return;
                }

                // Timed out — clean up silently.
                if (!token.IsCancellationRequested)
                {
                    App.Log("[DropDiagnostic] Drop poll thread timed out after 30s");
                    DispatcherQueue.TryEnqueue(() =>
                    {
                        _pendingDropDataView = null;
                        StopDropPoll();
                    });
                }
            }
            catch (TaskCanceledException)
            {
                // Normal — drag left / completed normally.
            }
            catch (Exception ex)
            {
                App.Log($"[DropDiagnostic] Drop poll thread crashed: {ex.Message}");
            }
        }, token);
    }

    /// <summary>
    /// Stops the background drop poll if running.
    /// </summary>
    private void StopDropPoll()
    {
        if (_dropPollCts is null)
        {
            return;
        }

        App.Log("[DropDiagnostic] StopDropPoll called");
        try
        {
            _dropPollCts.Cancel();
        }
        catch
        {
        }
        _dropPollCts = null;
    }

    /// <summary>
    /// Called on the UI thread when the background poll detects that the
    /// left mouse button was released during an active drag-over.
    /// Uses pre-cached paths (extracted during DragOver when data was valid)
    /// because the DataPackageView is already invalid by this point.
    /// </summary>
    private async void OnDropPollDetectedRelease()
    {
        // Grace period: let the normal Drop event fire first.
        // For sources where WinUI 3's Drop works (browser, Explorer), the
        // Drop handler will clear _pendingDropDataView within this window.
        await Task.Delay(80);

        if (_pendingDropDataView is null || _isManualDropInProgress)
        {
            // Normal Drop event already handled it.
            return;
        }

        _pendingDropDataView = null;
        _isManualDropInProgress = true;
        StopDropPoll();
        StopDragHighlight();
        ClearFolderDropTarget();
        _lastRootDragDiagnosticSignature = null;

        if (_isMigrationBusy)
        {
            App.Log("[DropDiagnostic] Mouse release detected via poll but migration busy — ignoring");
            _isManualDropInProgress = false;
            return;
        }

        // Use cached paths — the DataPackageView is invalid by now.
        var paths = _cachedDropPaths ?? [];
        _cachedDropPaths = null;
        App.Log($"[DropDiagnostic] Detected mouse release via poll thread — using cached paths count={paths.Length}");
        if (paths.Length > 0)
        {
            try
            {
                await ProcessManualDropAsync(paths);
            }
            catch (Exception ex)
            {
                App.Log($"[DropDiagnostic] Poll-based manual drop failed: {ex}");
            }
        }
        else
        {
            App.Log("[DropDiagnostic] Poll-based manual drop: no cached paths available");
        }
        _isManualDropInProgress = false;
    }

    /// <summary>
    /// Pre-extracts file paths from the DataPackageView during DragOver,
    /// while the data is still valid (inside the OLE modal loop).
    /// The result is stored in _cachedDropPaths for later use by the
    /// poll thread when it detects mouse release.
    /// </summary>
    private void TryCacheDropPaths(DataPackageView dataView)
    {
        if (_cachedDropPaths is not null || _isCachingDropPaths)
        {
            return;
        }

        if (!HasPathDropData(dataView))
        {
            return;
        }

        _isCachingDropPaths = true;
        // Capture the view and extract paths asynchronously.
        var capturedView = dataView;
        Task.Run(async () =>
        {
            string[]? result = null;
            try
            {
                result = await GetDropPathsAsync(capturedView);
                App.Log($"[DropDiagnostic] Pre-cache extracted paths count={result.Length}");
            }
            catch (Exception ex)
            {
                App.Log($"[DropDiagnostic] Pre-cache failed: {ex.Message}");
            }

            _cachedDropPaths = result ?? [];
            _isCachingDropPaths = false;
        });
    }

    /// <summary>
    /// Processes a manual drop (from the DragOver workaround or the poll timer).
    /// If the cursor is over a folder item, transfers files into that folder;
    /// otherwise imports into the widget root.
    /// </summary>
    private async Task ProcessManualDropAsync(IReadOnlyList<string> paths)
    {
        // Check if the cursor is over a folder item — if so, transfer into it
        // (mirrors OnNativeDrop's folder-drop logic).
        if (Win32Helper.GetCursorPos(out var cursor) &&
            TryGetFolderItemAtScreenPoint(cursor.X, cursor.Y, out _, out var folderItem))
        {
            App.Log($"[DropDiagnostic] Manual drop → folder='{folderItem.Name}' path='{folderItem.Path}'");
            bool showOverlay = ShouldShowImportOverlay(paths);
            if (showOverlay)
            {
                SetImportBusy(true);
            }
            try
            {
                bool move = ShouldMoveForAcceptedOperation(
                    NormalizePathDropOperation(DataPackageOperation.Copy | DataPackageOperation.Move, movesIntoFolder: true));
                App.Log($"[DropDiagnostic] Manual folder drop move={move} sourceCount={paths.Count}");
                var results = await App.Current.FileService.TransferItemsWithResultAsync(
                    paths, folderItem.Path, move);
                App.Log($"[DropDiagnostic] Manual folder drop results.Count={results.Count}");

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
                App.Log($"[DropDiagnostic] Manual folder drop failed: {ex}");
            }
            finally
            {
                if (showOverlay)
                {
                    SetImportBusy(false);
                }
            }
            return;
        }

        // Not over a folder — import into the widget root.
        App.Log($"[DropDiagnostic] Manual drop → root import, mapped='{ViewModel.MappedFolderPath}'");
        await ImportNativeDropPathsAsync(paths);
    }

    /// <summary>
    /// Computes the insertion index at the given position within the
    /// GridView or ListView.  Handles gaps between items correctly.
    /// For GridView: considers both row (Y) and column (X) position.
    /// For ListView: considers row (Y) position only.
    /// </summary>
    private int ComputeDropInsertionIndex(UIElement control, Windows.Foundation.Point position)
    {
        if (control is not ListViewBase listControl || listControl.Items.Count == 0)
        {
            return 0;
        }

        bool isGridView = control is GridView;

        for (int i = 0; i < listControl.Items.Count; i++)
        {
            var container = listControl.ContainerFromIndex(i) as FrameworkElement;
            if (container is null || container.ActualHeight <= 0)
            {
                continue;
            }

            var transform = container.TransformToVisual(control);
            var rect = transform.TransformBounds(new Windows.Foundation.Rect(
                0, 0, container.ActualWidth, container.ActualHeight));

            if (isGridView)
            {
                // Determine if the pointer is "before" this item.
                // "Before" = above the item's row, or in the same row and
                // to the left of the item's horizontal center.
                bool aboveRow = position.Y < rect.Top;
                bool sameRow = position.Y >= rect.Top && position.Y < rect.Bottom;
                bool leftOfCenter = position.X < (rect.X + rect.Width / 2);

                if (aboveRow || (sameRow && leftOfCenter))
                {
                    return i;
                }
            }
            else
            {
                // ListView: check if pointer is above the vertical midpoint.
                if (position.Y < (rect.Top + rect.Height / 2))
                {
                    return i;
                }
            }
        }

        return listControl.Items.Count;
    }

}
