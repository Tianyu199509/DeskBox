using System.ComponentModel;
using System.Diagnostics;
using DeskBox.Controls;
using DeskBox.Contracts;
using DeskBox.Helpers;
using DeskBox.Models;
using DeskBox.Platform;
using DeskBox.Services;
using DeskBox.ViewModels;
using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.System;

namespace DeskBox.Controls.WidgetContents;

public sealed partial class FileSurfaceContent
{
    private readonly HashSet<Border> _itemSurfaces = [];
    private readonly HashSet<Border> _stackSurfaces = [];
    private readonly Dictionary<Border, (WidgetStackItem Stack, PropertyChangedEventHandler Handler)>
        _stackSurfacePropertyChangedHandlers = [];
    private readonly FileItemSurfaceStyleCache _itemSurfaceStyleCache = new();
    private bool _folderDropVisualActive;
    private SolidColorBrush? _stackTransparentBrush;
    private bool _stackMemberDropVisualActive;
    private string? _stackDropItemsTargetKey;
    private DataPackageView? _stackDropItemsDataView;
    private int _stackDropItemsTargetMemberCount = -1;
    private WidgetItem[] _stackDropItemsCache = [];

    private void ApplySelectionRectangleAppearance()
    {
        ApplySelectionRectangleAppearance(SelectionRectangle);
        if (_stackPopoverSelectionRectangle is { } popoverRectangle)
        {
            ApplySelectionRectangleAppearance(popoverRectangle);
        }
    }

    private void ApplySelectionRectangleAppearance(Border rectangle)
    {
        // Marquee selection reports what the pointer is sweeping over, so it
        // draws the neutral interaction palette rather than the accent.
        rectangle.Background = SharedBrushCache.GetOrCreate(
            NeutralInteractionBrush.Fill(rectangle));
        rectangle.BorderBrush = SharedBrushCache.GetOrCreate(
            NeutralInteractionBrush.Line(rectangle));
        rectangle.BorderThickness = new Thickness(1);
        rectangle.CornerRadius = new CornerRadius(0);
        rectangle.Opacity = 1;
    }

    private void ItemSurface_Loaded(
        object sender,
        RoutedEventArgs e)
    {
        if (sender is FileItemSurface surface)
        {
            // Popover templates are created lazily and avoid runtime bindings
            // to the parent ItemsControl so they add no Native AOT trim paths.
            // Main-surface templates already supply this value through XAML;
            // assigning the same context here is a safe fallback for both.
            surface.LayoutContext ??= ViewModel;
            // State and data-context callbacks are wired once by the template.
            // They must already work during first realization and remain
            // connected when WinUI recycles an item without another Loaded.
            ApplyOpeningStateToSurface(surface);
        }

        if (FileItemSurface.TryGetInteractiveBorder(sender) is { } border)
        {
            RestoreStackAnimationElement(border);
            _itemSurfaces.Add(border);
            ApplyItemSurfaceVisual(
                border,
                FileItemSurface.FindOwner(border)?.VisualState ??
                    FileItemSurfaceVisualState.Normal);
        }
    }

    private void ItemSurface_Unloaded(
        object sender,
        RoutedEventArgs e)
    {
        if (FileItemSurface.TryGetInteractiveBorder(sender) is { } border)
        {
            RestoreStackAnimationElement(border);
            if (ReferenceEquals(border, _folderDropTarget))
            {
                _folderDropTarget = null;
                _folderDropVisualActive = false;
            }

            _itemSurfaces.Remove(border);
        }
    }

    private void ItemSurface_VisualStateChanged(
        object? sender,
        FileItemSurfaceVisualStateChangedEventArgs e)
    {
        if (_isDisposed)
        {
            return;
        }

        if (FileItemSurface.TryGetInteractiveBorder(sender) is { } border)
        {
            ApplyItemSurfaceVisual(border, e.State);
        }
    }

    private void ItemSurface_PointerPressed(
        object sender,
        PointerRoutedEventArgs e)
    {
        if (FileItemSurface.TryGetInteractiveBorder(sender) is not { } border ||
            border.DataContext is not WidgetItem item)
        {
            return;
        }

        var pointerPoint = e.GetCurrentPoint(border);
        bool isPrimaryContact =
            pointerPoint.Properties.IsLeftButtonPressed ||
            pointerPoint.PointerDeviceType is
                PointerDeviceType.Touch or PointerDeviceType.Pen &&
            pointerPoint.IsInContact;
        if (!isPrimaryContact)
        {
            return;
        }

        ListViewBase listView = GetActiveItemsView();
        _pendingPointerDragItems = listView.SelectedItems.Contains(item)
            ? listView.SelectedItems
                .OfType<WidgetItem>()
                .Where(selected => selected is not WidgetStackItem)
                .Distinct()
                .ToArray()
            : [];
        // Keep pointer-down selection read-only. The native selector commits
        // its final state after ItemClick; changing SelectedItems here makes
        // the same click toggle twice and can leave a stale custom highlight.
        // The snapshot above only preserves an existing multi-selection for a
        // drag that starts on one of its selected anchors.
    }

    private void ItemSurface_PointerGestureEnded(
        object sender,
        PointerRoutedEventArgs e)
    {
        // The press that staged the drag snapshot ended without a drag. The
        // snapshot must not outlive its gesture: an item-container release or
        // capture loss never reaches the view-level PointerReleased, and the
        // stale snapshot then keeps the deactivation selection clear guarded
        // (it reads as an active drag) after an opened file's app takes the
        // foreground.
        _pendingPointerDragItems = [];
    }

    private void ItemSurface_DragOver(
        object sender,
        DragEventArgs e)
    {
        if (!TryGetFolderDropTarget(sender, out Border border, out WidgetItem targetFolder))
        {
            TryHandleLaunchTargetDragOver(sender, e);
            return;
        }

        e.Handled = true;
        // A folder item is an explicit filesystem destination. Cancel any
        // insertion preview that the root produced before the pointer entered
        // the folder so DragItemsCompleted cannot commit a stale reorder.
        if (_isSurfaceReorderDragActive ||
            _surfaceReorderInsertionIndex >= 0)
        {
            PersistSurfaceReorder();
        }
        ClearExternalDropPreviewPlacement();

        DragPayloadSnapshot payload = GetDragPayload(e.DataView);
        if (!payload.IsDeskBoxFileDrag && payload.HasSurfacePathData)
        {
            SuppressExternalDragOperationBadge(e);
        }

        if (_isImportBusy ||
            !payload.HasSurfacePathData ||
            HasTransferConflict(payload.Paths, targetFolder.Path))
        {
            e.AcceptedOperation = DataPackageOperation.None;
            ClearFolderDropTarget();
            return;
        }

        if (IsUnsafeFolderDrop(payload.Paths, targetFolder.Path))
        {
            e.AcceptedOperation = DataPackageOperation.None;
            if (payload.IsDeskBoxFileDrag)
            {
                ApplyDeskBoxFileDragFeedback(
                    e,
                    DataPackageOperation.None,
                    T("Widget.CannotMoveToFolder"));
            }
            ClearFolderDropTarget();
            return;
        }

        if (AreAllSourcesAlreadyInDestinationLexically(
                payload.Paths,
                targetFolder.Path))
        {
            e.AcceptedOperation = DataPackageOperation.None;
            if (payload.IsDeskBoxFileDrag)
            {
                ApplyDeskBoxFileDragFeedback(
                    e,
                    DataPackageOperation.None,
                    T("Widget.DragCaption.CurrentWidget"));
            }
            ClearFolderDropTarget();
            return;
        }

        FileDropIntent resolvedIntent = ResolveSurfaceDropIntent(
            payload.DataView,
            e.AllowedOperations,
            destinationFolderPath: targetFolder.Path);
        DataPackageOperation operation =
            ToDataPackageOperation(resolvedIntent);
        e.AcceptedOperation = operation;
        if (operation == DataPackageOperation.None)
        {
            ClearFolderDropTarget();
            return;
        }

        SetFolderDropTarget(border);
        if (payload.IsDeskBoxFileDrag)
        {
            ApplyDeskBoxFileDragFeedback(
                e,
                operation,
                FormatDropCaption(resolvedIntent, targetFolder.Name));
        }
    }

    private void ItemSurface_DragLeave(
        object sender,
        DragEventArgs e)
    {
        if (!TryGetFolderDropTarget(sender, out Border border, out _))
        {
            // A shortcut tile stays the recorded release target while the
            // pointer is still over it: refreshing a ListView container during
            // the real-time reorder preview can raise DragLeave even though the
            // pointer never moved, which would otherwise discard the record.
            if (TryGetLaunchDropTarget(sender, out Border launchBorder, out _) &&
                !IsPointerInsideDropElement(launchBorder, e))
            {
                ClearInternalLaunchHover();
                // The tile really lost the pointer. Later root DragOver calls
                // only clear the folder and stack targets, so the launch hover
                // would otherwise stay painted until the gesture ends.
                ClearLaunchDropTarget();
            }

            return;
        }

        e.Handled = true;
        if (IsPointerInsideDropElement(border, e))
        {
            return;
        }

        if (ReferenceEquals(border, _folderDropTarget))
        {
            ClearFolderDropTarget();
        }
    }

    private async void ItemSurface_Drop(
        object sender,
        DragEventArgs e)
    {
        if (!TryGetFolderDropTarget(sender, out _, out WidgetItem targetFolder))
        {
            await TryHandleLaunchTargetDropAsync(sender, e);
            return;
        }

        e.Handled = true;
        // DragOver may have advertised Move. Do not complete the drag with that
        // result until the destination transfer has actually succeeded.
        e.AcceptedOperation = DataPackageOperation.None;
        ClearFolderDropTarget();
        PersistSurfaceReorder();
        ApplyDropVisual(FileDropVisualState.None);

        DragPayloadSnapshot payload = GetDragPayload(e.DataView);
        TraceTargetDropEntered("folder", payload, e);
        if (_isImportBusy ||
            !payload.HasSurfacePathData ||
            HasTransferConflict(payload.Paths, targetFolder.Path))
        {
            e.AcceptedOperation = DataPackageOperation.None;
            if (HasTransferConflict(payload.Paths, targetFolder.Path))
            {
                FileTransferPathState targetState =
                    GetTransferState(targetFolder);
                ShowTransferBlockedFeedback(
                    targetState.IsActive
                        ? targetState
                        : _fileService.TransferSessions.GetState(
                            payload.Paths.FirstOrDefault()));
            }
            ResetDragPayloadCache();
            return;
        }

        var deferral = e.GetDeferral();
        // Surface-level and folder-target drops share the same acquisition
        // phase. Show preparation feedback before resolving StorageItems so a
        // large external payload never leaves the widget looking frozen.
        BeginTrackedImport();
        try
        {
            using DroppedFileBatch batch = await GetSurfaceDropFilesAsync(e.DataView);
            DroppedFilePath[] droppedFiles = batch.Files
                // GetSurfaceDropFilesAsync has already normalized and validated
                // filesystem paths (or materialized a temporary virtual file).
                // Repeating synchronous existence checks here blocks the UI
                // thread during a folder-target drop.
                .Where(file => !string.IsNullOrWhiteSpace(file.Path))
                .GroupBy(file => file.Path, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .ToArray();
            string[] sourcePaths = droppedFiles
                .Select(file => file.Path)
                .ToArray();
            bool transferConflict = HasTransferConflict(
                sourcePaths,
                targetFolder.Path);
            bool unsafeFolderDrop = IsUnsafeFolderDrop(
                sourcePaths,
                targetFolder.Path);
            bool sameDirectoryDrop = sourcePaths.Length > 0 &&
                await AreAllSourcesAlreadyInDestinationResolvedAsync(
                    sourcePaths,
                    targetFolder.Path);
            if (sourcePaths.Length == 0 ||
                transferConflict ||
                unsafeFolderDrop ||
                sameDirectoryDrop)
            {
                e.AcceptedOperation = DataPackageOperation.None;
                if (transferConflict)
                {
                    FileTransferPathState targetState =
                        GetTransferState(targetFolder);
                    ShowTransferBlockedFeedback(
                        targetState.IsActive
                            ? targetState
                            : _fileService.TransferSessions.GetState(
                                sourcePaths.FirstOrDefault()));
                }
                if (sameDirectoryDrop)
                {
                    ShowSameDirectoryDropFeedback();
                }
                else if (sourcePaths.Length > 0 && !transferConflict)
                {
                    ShowFeedback(new(
                        T("Widget.CannotMoveToFolder"),
                        WidgetFeedbackSeverity.Warning,
                        "folder-drop-unsafe"));
                }
                return;
            }

            // Re-read Ctrl/Shift at Drop so releasing or pressing a modifier
            // during the drag changes the actual transfer, not only its glyph.
            FileDropIntent resolvedIntent = ResolveSurfaceDropIntent(
                e.DataView,
                e.AllowedOperations,
                forceCopy: droppedFiles.Any(file => file.ForceManagedCopy),
                destinationFolderPath: targetFolder.Path,
                sourcePathsOverride: droppedFiles.Select(file => file.Path));
            DataPackageOperation operation =
                ToDataPackageOperation(resolvedIntent);
            if (operation == DataPackageOperation.None)
            {
                return;
            }

            bool move = operation == DataPackageOperation.Move;
            bool createShortcuts = resolvedIntent == FileDropIntent.Shortcut;
            string? sourceWidgetId = TryGetString(
                e.DataView.Properties,
                DeskBoxDragData.SourceWidgetIdProperty);

            EnsureTrackedImportStarted();
            IProgress<FileService.FileTransferProgress> progress =
                new CallbackProgress<FileService.FileTransferProgress>(
                    ReportImportProgress);
            try
            {
                var results = new List<FileService.FileTransferResult>();
                string[] regularPaths = droppedFiles
                    .Where(file => !file.ForceManagedCopy)
                    .Select(file => file.Path)
                    .ToArray();
                if (!createShortcuts)
                {
                    regularPaths = await RemoveUndisplayableFolderDropPathsAsync(
                        regularPaths);
                }

                if (regularPaths.Length > 0)
                {
                    if (createShortcuts)
                    {
                        IReadOnlyList<string> created =
                            await CreateShortcutFilesAsync(
                                droppedFiles.Where(file => !file.ForceManagedCopy)
                                    .ToArray(),
                                targetFolder.Path,
                                ActiveImportCancellationToken);
                        results.AddRange(created.Select(path =>
                            new FileService.FileTransferResult(path, path)));
                    }
                    else
                    {
                        results.AddRange(await _fileService.TransferItemsWithResultAsync(
                            regularPaths,
                            targetFolder.Path,
                            move,
                            progress,
                            ActiveImportCancellationToken,
                            useShellProgress: true,
                            ownerWindowHandle: _hostWindowHandle));
                    }
                }

                string[] forcedCopyPaths = droppedFiles
                    .Where(file => file.ForceManagedCopy)
                    .Select(file => file.Path)
                    .ToArray();
                forcedCopyPaths = await RemoveUndisplayableFolderDropPathsAsync(
                    forcedCopyPaths);
                if (forcedCopyPaths.Length > 0)
                {
                    results.AddRange(await _fileService.TransferItemsWithResultAsync(
                        forcedCopyPaths,
                        targetFolder.Path,
                        move: false,
                        progress: progress,
                        cancellationToken: ActiveImportCancellationToken));
                }

                if (!string.IsNullOrWhiteSpace(ViewModel.MappedFolderPath))
                {
                    await ViewModel.RefreshFromConfigAsync();
                }

                string[] movedSourcePaths = move
                    ? results
                        .Where(result => regularPaths.Contains(
                            result.SourcePath,
                            StringComparer.OrdinalIgnoreCase))
                        .Select(result => result.SourcePath)
                        .ToArray()
                    : [];
                if (movedSourcePaths.Length > 0 &&
                    sourceWidgetId is { Length: > 0 } &&
                    App.Current?.WidgetManager is { } manager)
                {
                    await manager.NotifyItemsMovedOutAsync(
                        sourceWidgetId,
                        movedSourcePaths);
                }

                e.AcceptedOperation = ResolveSafeDropCompletionOperation(
                    operation,
                    payload.IsDeskBoxFileDrag,
                    regularPaths.Length,
                    movedSourcePaths.Length);

                if (move)
                {
                    _cutClipboardPaths = [];
                    ApplyCutState();
                }

                if (results.Count > 0)
                {
                    ShowFeedback(new(
                        _localizationService.Format(
                            move
                                ? "Widget.MovedToFolder"
                                : "Widget.CopiedToFolder",
                            targetFolder.Name,
                            results.Count),
                        WidgetFeedbackSeverity.Success,
                        move ? "folder-drop-move" : "folder-drop-copy"));
                }

                await CompleteTrackedImportAsync(
                    ImportCompletionState.Completed);
            }
            catch (OperationCanceledException)
            {
                await CompleteTrackedImportAsync(
                    ImportCompletionState.Canceled);
                throw;
            }
            catch
            {
                await CompleteTrackedImportAsync(
                    ImportCompletionState.Failed);
                throw;
            }
        }
        catch (OperationCanceledException)
        {
            e.AcceptedOperation = DataPackageOperation.None;
            App.Log($"[WidgetSurface] Folder drop canceled id={WidgetId}");
            await RefreshAfterInterruptedFolderImportAsync();
            if (_activeImportCancellation is not null)
            {
                await CompleteTrackedImportAsync(
                    ImportCompletionState.Canceled);
            }
        }
        catch (Exception ex)
        {
            e.AcceptedOperation = DataPackageOperation.None;
            App.Log($"[WidgetSurface] Folder drop failed id={WidgetId}: {ex}");
            await RefreshAfterInterruptedFolderImportAsync();
            ShowFeedback(new(
                $"{T("Widget.MoveToFolderFailed")}: {ex.Message}",
                WidgetFeedbackSeverity.Error,
                "folder-drop-error"));
            if (_activeImportCancellation is not null)
            {
                await CompleteTrackedImportAsync(
                    ImportCompletionState.Failed);
            }
        }
        finally
        {
            if (_activeImportCancellation is not null)
            {
                CancelAndResetTrackedImport();
            }
            ResetDragPayloadCache();
            deferral.Complete();
        }
    }

    /// <summary>
    /// Removes entries the widget item list can never display from a
    /// folder-tile drop. Folder-tile transfers bypass the ViewModel import
    /// gate, so the filter is applied here; refused entries stay at their
    /// source instead of landing invisible inside the folder.
    /// </summary>
    private async Task<string[]> RemoveUndisplayableFolderDropPathsAsync(
        IReadOnlyList<string> paths)
    {
        string[] materialized = paths.ToArray();
        if (materialized.Length == 0)
        {
            return materialized;
        }

        string[] displayable = await Task.Run(() => materialized
            .Where(path => !FileService.IsFilteredFromWidgetDisplay(path))
            .ToArray());
        int skippedCount = materialized.Length - displayable.Length;
        if (skippedCount > 0)
        {
            App.Log(
                $"[Import] Refused undisplayable folder drop widget={WidgetId} " +
                $"skipped={skippedCount} requested={materialized.Length}");
            ShowFeedback(new(
                _localizationService.Format(
                    "Widget.ImportSkippedUndisplayable",
                    skippedCount),
                WidgetFeedbackSeverity.Warning,
                "file-import-skipped-undisplayable"));
        }

        return displayable;
    }

    private async Task<bool> ImportNativeDroppedFilesIntoFolderAsync(
        IReadOnlyList<DroppedFilePath> droppedFiles,
        WidgetItem targetFolder,
        bool move,
        FileDropIntent? intentOverride = null)
    {
        string[] sourcePaths = droppedFiles
            .Select(file => file.Path)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (HasTransferConflict(sourcePaths, targetFolder.Path))
        {
            FileTransferPathState targetState = GetTransferState(targetFolder);
            ShowTransferBlockedFeedback(
                targetState.IsActive
                    ? targetState
                    : _fileService.TransferSessions.GetState(
                        sourcePaths.FirstOrDefault()));
            return false;
        }

        if (sourcePaths.Length == 0 ||
            IsUnsafeFolderDrop(sourcePaths, targetFolder.Path))
        {
            if (sourcePaths.Length > 0)
            {
                ShowFeedback(new(
                    T("Widget.CannotMoveToFolder"),
                    WidgetFeedbackSeverity.Warning,
                    "native-folder-drop-unsafe"));
            }

            return false;
        }

        BeginTrackedImport();
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        App.Log(
            $"[Import] Native folder import start widget={WidgetId} " +
            $"target='{targetFolder.Path}' count={sourcePaths.Length} " +
            $"move={move}");
        try
        {
            EnsureTrackedImportStarted();
            IProgress<FileService.FileTransferProgress> progress =
                new CallbackProgress<FileService.FileTransferProgress>(
                    ReportImportProgress);
            var results = new List<FileService.FileTransferResult>();
            bool createShortcuts = intentOverride == FileDropIntent.Shortcut;
            string[] regularPaths = droppedFiles
                .Where(file => !file.ForceManagedCopy)
                .Select(file => file.Path)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (!createShortcuts)
            {
                regularPaths = await RemoveUndisplayableFolderDropPathsAsync(
                    regularPaths);
            }

            if (regularPaths.Length > 0)
            {
                if (createShortcuts)
                {
                    IReadOnlyList<string> created =
                        await CreateShortcutFilesAsync(
                            droppedFiles.Where(file => !file.ForceManagedCopy)
                                .ToArray(),
                            targetFolder.Path,
                            ActiveImportCancellationToken);
                    results.AddRange(created.Select(path =>
                        new FileService.FileTransferResult(path, path)));
                }
                else
                {
                    results.AddRange(await _fileService.TransferItemsWithResultAsync(
                        regularPaths,
                        targetFolder.Path,
                        move,
                        progress,
                        ActiveImportCancellationToken,
                        useShellProgress: true,
                        ownerWindowHandle: _hostWindowHandle));
                }
            }

            string[] forcedCopyPaths = droppedFiles
                .Where(file => file.ForceManagedCopy)
                .Select(file => file.Path)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            forcedCopyPaths = await RemoveUndisplayableFolderDropPathsAsync(
                forcedCopyPaths);
            if (forcedCopyPaths.Length > 0)
            {
                results.AddRange(await _fileService.TransferItemsWithResultAsync(
                    forcedCopyPaths,
                    targetFolder.Path,
                    move: false,
                    progress: progress,
                    cancellationToken: ActiveImportCancellationToken));
            }

            if (!string.IsNullOrWhiteSpace(ViewModel.MappedFolderPath))
            {
                await ViewModel.RefreshFromConfigAsync();
            }

            if (move)
            {
                _cutClipboardPaths = [];
                ApplyCutState();
            }

            if (results.Count > 0)
            {
                ShowFeedback(new(
                    _localizationService.Format(
                        move
                            ? "Widget.MovedToFolder"
                            : "Widget.CopiedToFolder",
                        targetFolder.Name,
                        results.Count),
                    WidgetFeedbackSeverity.Success,
                    move
                        ? "native-folder-drop-move"
                        : "native-folder-drop-copy"));
            }

            await CompleteTrackedImportAsync(
                ImportCompletionState.Completed);
            App.Log(
                $"[Import] Native folder import completed widget={WidgetId} " +
                $"target='{targetFolder.Path}' count={results.Count} " +
                $"elapsedMs={stopwatch.ElapsedMilliseconds}");
            return results.Count > 0;
        }
        catch (OperationCanceledException)
        {
            await RefreshAfterInterruptedFolderImportAsync();
            if (_activeImportCancellation is not null)
            {
                await CompleteTrackedImportAsync(
                    ImportCompletionState.Canceled);
            }

            App.Log(
                $"[Import] Native folder import canceled widget={WidgetId} " +
                $"target='{targetFolder.Path}' " +
                $"elapsedMs={stopwatch.ElapsedMilliseconds}");
            return false;
        }
        catch (Exception ex)
        {
            await RefreshAfterInterruptedFolderImportAsync();
            if (_activeImportCancellation is not null)
            {
                await CompleteTrackedImportAsync(
                    ImportCompletionState.Failed);
            }

            App.Log(
                $"[WidgetSurface] Native folder drop failed id={WidgetId} " +
                $"target='{targetFolder.Path}': {ex}");
            ShowFeedback(new(
                $"{T("Widget.MoveToFolderFailed")}: {ex.Message}",
                WidgetFeedbackSeverity.Error,
                "native-folder-drop-error"));
            return false;
        }
        finally
        {
            if (_activeImportCancellation is not null)
            {
                CancelAndResetTrackedImport();
            }
        }
    }

    private async Task RefreshAfterInterruptedFolderImportAsync()
    {
        if (string.IsNullOrWhiteSpace(ViewModel.MappedFolderPath))
        {
            return;
        }

        try
        {
            await ViewModel.RefreshFromConfigAsync();
        }
        catch (Exception ex)
        {
            App.Log(
                $"[WidgetSurface] Refresh after interrupted folder import " +
                $"failed id={WidgetId}: {ex}");
        }
    }

    private async Task<bool> ImportNativeDroppedFilesIntoStackAsync(
        IReadOnlyList<DroppedFilePath> droppedFiles,
        WidgetStackItem stack,
        bool? moveWhenMapped,
        FileDropIntent? intentOverride = null)
    {
        string[] sourcePaths = droppedFiles
            .Select(file => file.Path)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (HasTransferConflict(sourcePaths, ViewModel.CurrentFolderPath))
        {
            ShowTransferBlockedFeedback(
                _fileService.TransferSessions.GetState(
                    sourcePaths.FirstOrDefault()) is { IsActive: true } sourceState
                    ? sourceState
                    : _fileService.TransferSessions.GetState(
                        ViewModel.CurrentFolderPath));
            return false;
        }

        string targetStackKey = stack.StackKey;
        string[] targetStackMemberAnchors = stack.Members
            .Select(member => member.Path)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .ToArray();
        HashSet<string> existingPaths = ViewModel.Items
            .Select(item => Path.GetFullPath(item.Path))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        BeginTrackedImport();
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        App.Log(
            $"[Import] Native stack import start widget={WidgetId} " +
            $"stack='{targetStackKey}' count={droppedFiles.Count} " +
            $"move={moveWhenMapped == true}");
        try
        {
            await ImportDroppedFilesAsync(
                droppedFiles,
                moveWhenMapped,
                intentOverride);
            WidgetItem[] importedItems = ViewModel.Items
                .Where(item => !existingPaths.Contains(
                    Path.GetFullPath(item.Path)))
                .ToArray();
            bool added = importedItems.Length > 0 &&
                ViewModel.AddItemsToStack(targetStackKey, importedItems);
            if (added)
            {
                ClearSelection();
                QueueStackPopoverReconciliation(
                    targetStackKey,
                    targetStackMemberAnchors);
            }

            App.Log(
                $"[Import] Native stack import completed widget={WidgetId} " +
                $"stack='{targetStackKey}' imported={importedItems.Length} " +
                $"added={added} elapsedMs={stopwatch.ElapsedMilliseconds}");
            return added;
        }
        catch (OperationCanceledException)
        {
            App.Log(
                $"[Import] Native stack import canceled widget={WidgetId} " +
                $"stack='{targetStackKey}' " +
                $"elapsedMs={stopwatch.ElapsedMilliseconds}");
            return false;
        }
        catch (Exception ex)
        {
            App.Log(
                $"[WidgetSurface] Native stack drop failed id={WidgetId} " +
                $"stack='{targetStackKey}': {ex}");
            ShowFeedback(new(
                ex.Message,
                WidgetFeedbackSeverity.Error,
                "native-stack-drop-error"));
            return false;
        }
        finally
        {
            if (_activeImportCancellation is not null)
            {
                CancelAndResetTrackedImport();
            }
        }
    }

    private void ApplyImportedStackMemberInsertion(
        WidgetStackItem originalStack,
        IReadOnlyList<WidgetItem> importedItems,
        int memberInsertionIndex)
    {
        if (importedItems.Count == 0)
        {
            return;
        }

        // Importing a new member can convert an automatic group into a
        // manual stack and rebuild the projection under a new stack key. Make
        // that projection current before resolving the stack that owns the
        // imported objects, then reuse the same member reorder primitive as
        // the in-popover drag path.
        ViewModel.StabilizeStackDisplay();
        WidgetStackItem? currentStack = ViewModel.VisibleItems
            .OfType<WidgetStackItem>()
            .FirstOrDefault(candidate => importedItems.Any(imported =>
                candidate.Members.Any(member =>
                    string.Equals(
                        member.Path,
                        imported.Path,
                        StringComparison.OrdinalIgnoreCase))));
        currentStack ??= ViewModel.FindStackByKey(originalStack.StackKey);
        if (currentStack is null)
        {
            return;
        }

        ViewModel.MoveStackMembersForReorder(
            currentStack.StackKey,
            importedItems,
            memberInsertionIndex);
    }

    private static bool TryGetFolderDropTarget(
        object sender,
        out Border border,
        out WidgetItem folder)
    {
        if (sender is FileItemSurface surface &&
            surface.DataContext is WidgetItem item &&
            ItemDropBehaviorPolicy.Resolve(item) == ItemDropBehavior.FolderImport)
        {
            border = surface.InteractiveBorder;
            folder = item;
            return true;
        }

        border = null!;
        folder = null!;
        return false;
    }

    private static bool TryGetLaunchDropTarget(
        object sender,
        out Border border,
        out WidgetItem launchTarget)
    {
        if (sender is FileItemSurface surface &&
            surface.DataContext is WidgetItem item &&
            ItemDropBehaviorPolicy.Resolve(item) == ItemDropBehavior.Launch)
        {
            border = surface.InteractiveBorder;
            launchTarget = item;
            return true;
        }

        border = null!;
        launchTarget = null!;
        return false;
    }

    /// <summary>
    /// DragOver on an application-shortcut tile: highlight the tile and
    /// advertise a link-style accept so the drop lands here instead of the
    /// root import. DeskBox-sourced drags arm the launch only over the tile's
    /// icon; the label and padding around it keep their reorder and import
    /// semantics, so sorting toward a position a shortcut happens to occupy
    /// never opens it. The "open with" wording itself is carried by the Shell
    /// drop description, so Explorer's compact drag image stays.
    /// </summary>
    private void TryHandleLaunchTargetDragOver(
        object sender,
        DragEventArgs e)
    {
        if (!TryGetLaunchDropTarget(sender, out Border launchBorder, out WidgetItem launchTile))
        {
            return;
        }

        DragPayloadSnapshot payload = GetDragPayload(e.DataView);
        bool internalLaunch =
            ShortcutLaunchPolicy.EvaluateInternalDrag(
                payload.IsDeskBoxFileDrag,
                payload.IsStackPopoverMemberDrag,
                payload.Paths,
                launchTile.Path) == ShortcutLaunchDecision.Launch &&
            IsPointerOverLaunchIcon(sender, e);
        if (payload.IsStackPopoverMemberDrag)
        {
            return;
        }

        if (!internalLaunch && payload.IsDeskBoxFileDrag)
        {
            // A DeskBox-sourced drag outside the icon keeps its own semantics:
            // a same-widget drag hands the gesture back to the surface reorder,
            // a cross-widget drop falls through to the import, and dragging a
            // shortcut onto itself lands here too - putting a tile back where
            // it was never opens it. Leaving the icon after arming must also
            // drop the launch hover: the root feedback path only clears the
            // folder and stack targets, nothing else clears it.
            DisarmLaunchHover(launchTile);
            return;
        }

        App.LogVerbose(
            "[DragProtocol] launch-target dragover " +
            $"widget={WidgetId} target='{launchTile.Path}' internalLaunch={internalLaunch} " +
            $"deskBoxDrag={payload.IsDeskBoxFileDrag} " +
            $"internalReorder={payload.IsInternalReorder} paths={payload.Paths.Length}");

        e.Handled = true;
        // Same child-target contract as folder tiles: an explicit destination
        // cancels any root insertion preview so a stale reorder cannot commit.
        if (_isSurfaceReorderDragActive ||
            _surfaceReorderInsertionIndex >= 0)
        {
            PersistSurfaceReorder();
        }

        ClearExternalDropPreviewPlacement();
        SuppressExternalDragOperationBadge(e);
        // A launch is not a file transfer, so an external drag advertises a
        // link. An internal drag only ever allows Move - the source is a
        // ListView reorder - and the pointer being over the icon is what
        // claims the gesture: the arming DragOver above already discarded the
        // reorder state, and the completion resolves the release as a launch,
        // so no reorder ever commits.
        e.AcceptedOperation = internalLaunch
            ? e.AllowedOperations.HasFlag(DataPackageOperation.Move)
                ? DataPackageOperation.Move
                : e.AllowedOperations
            : e.AllowedOperations.HasFlag(DataPackageOperation.Link)
                ? DataPackageOperation.Link
                : e.AllowedOperations.HasFlag(DataPackageOperation.Copy)
                    ? DataPackageOperation.Copy
                    : DataPackageOperation.None;
        if (e.AcceptedOperation == DataPackageOperation.None)
        {
            ClearFolderDropTarget();
            ClearLaunchDropTarget();
            return;
        }

        SetLaunchDropTarget(launchBorder);
        if (internalLaunch)
        {
            // An internal drag carries no Shell drop description, so the
            // "open with" hint is the XAML caption instead - the same hint the
            // external path gets from the Shell.
            ApplyDeskBoxFileDragFeedback(
                e,
                e.AcceptedOperation,
                _localizationService.Format(
                    "Widget.DropOnShortcutOpenWith",
                    launchTile.Name));
            // WinUI will not deliver this gesture's Drop to the tile, so the
            // release is resolved from DragItemsCompleted: remember what this
            // DragOver accepted and which container it armed, so the release
            // can re-read the live icon geometry.
            _internalLaunchHoverItem = launchTile;
            _internalLaunchHoverBorder = launchBorder;
        }
    }

    // Launch hit-testing slack around the icon glyph host: enough to absorb
    // pointer jitter between DragOver samples, far smaller than the tile, so
    // aiming a reorder at the label or padding beside a shortcut never opens.
    private const double LaunchIconSlackPixels = 6;

    private void ClearInternalLaunchHover()
    {
        _internalLaunchHoverItem = null;
        _internalLaunchHoverBorder = null;
    }

    /// <summary>
    /// Drops a launch hover armed for this tile: the pointer left the icon, so
    /// the gesture is back to reorder/import semantics and neither the hover
    /// record nor its visual may claim the release.
    /// </summary>
    private void DisarmLaunchHover(WidgetItem launchTile)
    {
        if (!ReferenceEquals(_internalLaunchHoverItem, launchTile))
        {
            return;
        }

        ClearInternalLaunchHover();
        ClearLaunchDropTarget();
    }

    /// <summary>
    /// The routed drag pointer is over the tile's icon glyph host (with the
    /// launch slack). The icon is the only launch territory for
    /// DeskBox-sourced drags; the label and padding around it keep their
    /// reorder and import semantics.
    /// </summary>
    private static bool IsPointerOverLaunchIcon(
        object sender,
        DragEventArgs e)
    {
        if (sender is not FileItemSurface surface ||
            surface.IconHitTestElement is not { } iconHost)
        {
            return false;
        }

        try
        {
            Windows.Foundation.Point point = e.GetPosition(iconHost);
            return ShortcutLaunchPolicy.IsPointInsideRectWithSlack(
                0,
                0,
                iconHost.ActualWidth,
                iconHost.ActualHeight,
                LaunchIconSlackPixels,
                point.X,
                point.Y);
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>
    /// True when this release was accepted by a shortcut tile's icon and the
    /// surface never took the gesture. Popover member drags keep their
    /// membership semantics, and an accepted internal drop reports its own
    /// operation rather than None. The pointer is checked against the live
    /// icon geometry, so a release that slid off the icon first does not open
    /// the files.
    /// </summary>
    private bool ShouldLaunchFromCompletedInternalDrag(
        DragItemsCompletedEventArgs e,
        bool fromStackPopover)
    {
        bool hovered = _internalLaunchHoverItem is not null;
        bool atPoint = IsCursorInsideLaunchIcon();
        if (hovered && !atPoint)
        {
            // The corner to watch: armed by DragOver but the release geometry
            // failed (container recycled, pointer slid off between samples).
            App.LogVerbose(
                "[ShortcutLaunch] completion geometry rejected the release " +
                $"widget={WidgetId} target='{_internalLaunchHoverItem?.Path}'");
        }

        return !fromStackPopover &&
            ShortcutLaunchPolicy.ShouldLaunchFromCompletedInternalDrag(
                e.DropResult,
                hovered,
                atPoint);
    }

    /// <summary>
    /// The physical pointer is still inside the icon of the shortcut tile the
    /// last DragOver armed. The completion is the only release signal an
    /// internal drag gets, so the decision reads the live geometry instead of
    /// a point recorded during DragOver: the real-time reorder preview can
    /// recycle containers and move tiles under a stationary pointer.
    /// </summary>
    private bool IsCursorInsideLaunchIcon()
    {
        if (_internalLaunchHoverItem is not { } hoverItem ||
            !Win32Helper.GetCursorPos(out Win32Helper.POINT cursor))
        {
            return false;
        }

        if (FindLaunchHoverIconHost(hoverItem) is not { } iconHost ||
            !TryGetScreenPointInElement(
                iconHost,
                cursor.X,
                cursor.Y,
                out Windows.Foundation.Point local))
        {
            return false;
        }

        return ShortcutLaunchPolicy.IsPointInsideRectWithSlack(
            0,
            0,
            iconHost.ActualWidth,
            iconHost.ActualHeight,
            LaunchIconSlackPixels,
            local.X,
            local.Y);
    }

    /// <summary>
    /// The icon host of the armed shortcut tile, or null when the recorded
    /// container no longer shows that item - a recycled or rebuilt container
    /// makes the live geometry unknowable, and an unknowable release must not
    /// launch.
    /// </summary>
    private FrameworkElement? FindLaunchHoverIconHost(WidgetItem hoverItem)
    {
        Border? border = _internalLaunchHoverBorder;
        if (border?.XamlRoot is null ||
            border.DataContext is not WidgetItem current ||
            !string.Equals(
                current.Path,
                hoverItem.Path,
                StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return FileItemSurface.FindOwner(border)?.IconHitTestElement;
    }

    /// <summary>
    /// Opens the dragged tiles with the application the shortcut points at,
    /// which is what the release meant. The gesture belongs to the launch, so
    /// the pending reorder preview is discarded rather than committed.
    /// </summary>
    private bool TryLaunchInternalDragOnShortcut(string[] movedPaths)
    {
        WidgetItem? launchTarget = _internalLaunchHoverItem;
        ClearInternalLaunchHover();
        if (launchTarget is not { Path.Length: > 0 } shortcut ||
            movedPaths.Length == 0)
        {
            return false;
        }

        PersistSurfaceReorder();
        ResetExternalDropPreview();
        ApplyDropVisual(FileDropVisualState.None);
        ClearFolderDropTarget();
        // The completion does not raise the tile's DragLeave, so the launch
        // hover would otherwise stay painted on the shortcut tile.
        ClearLaunchDropTarget();
        bool launched = ShortcutFileLauncher.TryLaunchWithFiles(
            shortcut.Path,
            movedPaths);
        MarkNativeLaunchConsumed();
        if (!launched)
        {
            ShowShortcutLaunchRefusedFeedback(shortcut.Name);
        }

        App.Log(
            $"[ShortcutLaunch] Internal drag resolved on completion " +
            $"widget={WidgetId} target='{shortcut.Path}' " +
            $"paths={movedPaths.Length} launched={launched}");
        return true;
    }

    /// <summary>
    /// Drop on an application-shortcut tile: rebuild a CF_HDROP data object
    /// from the (possibly virtual, materialized) paths and delegate the drop
    /// to the shortcut's Shell drop target. The method stays unhandled on any
    /// refusal, so the routed root import takes over with today's behavior and
    /// feedback - no transfer session is ever created for the launch attempt.
    /// </summary>
    private async Task TryHandleLaunchTargetDropAsync(
        object sender,
        DragEventArgs e)
    {
        if (!TryGetLaunchDropTarget(sender, out _, out WidgetItem launchItem))
        {
            return;
        }

        DragPayloadSnapshot payload = GetDragPayload(e.DataView);
        bool internalLaunch =
            ShortcutLaunchPolicy.EvaluateInternalDrag(
                payload.IsDeskBoxFileDrag,
                payload.IsStackPopoverMemberDrag,
                payload.Paths,
                launchItem.Path) == ShortcutLaunchDecision.Launch &&
            IsPointerOverLaunchIcon(sender, e);
        if (payload.IsStackPopoverMemberDrag)
        {
            return;
        }

        if (!internalLaunch && payload.IsDeskBoxFileDrag)
        {
            // Outside the icon a DeskBox-sourced drop keeps its own semantics:
            // a same-widget drag is resolved by the surface reorder, a
            // cross-widget drop falls through to the import, and the launch
            // hover armed over the icon must not survive the release.
            DisarmLaunchHover(launchItem);
            return;
        }

        if (WasLaunchConsumedByNativeDrop())
        {
            // The native OLE path delegated this same physical drop while the
            // real IDataObject was alive; delegating again would launch the
            // target application a second time.
            App.LogVerbose(
                "[DragProtocol] launch double-consumption suppressed " +
                $"widget={WidgetId} path='{launchItem.Path}'");
            e.Handled = true;
            return;
        }

        ClearFolderDropTarget();
        // A consumed drop does not raise the tile's DragLeave, so the launch
        // hover must be cleared here instead of waiting for it.
        ClearLaunchDropTarget();
        ApplyDropVisual(FileDropVisualState.None);
        // Reading StorageItems yields past the synchronous handler body; the
        // deferral keeps the drag transaction (and the DataView) alive.
        var deferral = e.GetDeferral();
        try
        {
            string[] paths;
            if (internalLaunch)
            {
                // An internal drag carries its already-resolved source paths, so
                // the opened files are exactly the tiles that were dragged.
                paths = payload.Paths
                    .Where(path => !string.IsNullOrWhiteSpace(path))
                    .GroupBy(path => path, StringComparer.OrdinalIgnoreCase)
                    .Select(group => group.First())
                    .ToArray();
            }
            else
            {
                using DroppedFileBatch batch =
                    await GetSurfaceDropFilesAsync(e.DataView);
                paths = batch.Files
                    .Where(file => !string.IsNullOrWhiteSpace(file.Path))
                    .GroupBy(file => file.Path, StringComparer.OrdinalIgnoreCase)
                    .Select(group => group.First().Path)
                    .ToArray();
            }

            if (paths.Length == 0)
            {
                return;
            }

            // The application may accept the launch asynchronously; the routed
            // root import must not run for this gesture either way, because the
            // user asked to open the files with this shortcut's application.
            MarkNativeLaunchConsumed();
            e.Handled = true;
            if (ShortcutFileLauncher.TryLaunchWithFiles(launchItem.Path, paths))
            {
                e.AcceptedOperation = DataPackageOperation.Link;
            }
            else
            {
                e.AcceptedOperation = DataPackageOperation.None;
                ShowShortcutLaunchRefusedFeedback(launchItem.Name);
            }
        }
        finally
        {
            deferral.Complete();
        }
    }

    private bool WasLaunchConsumedByNativeDrop() => WasLaunchConsumedRecently();

    /// <summary>
    /// True while another drop path already resolved this physical gesture as an
    /// application-shortcut launch (opened, refused, or failed). The routed XAML
    /// Drop and the legacy WM_DROPFILES message must then stand down instead of
    /// delegating a second time or importing the files.
    /// </summary>
    internal bool WasLaunchConsumedRecently()
    {
        long consumedTicks = Interlocked.Read(ref _lastLaunchConsumedTicks);
        return consumedTicks != 0 &&
            Stopwatch.GetTimestamp() - consumedTicks < LaunchConsumptionWindowTicks;
    }

    /// <summary>
    /// Claims this physical drop for the launch path. Callable from the OLE
    /// thread (before the UI thread would process a legacy duplicate) as well as
    /// from the UI thread, so the OLE refusal is visible to WM_DROPFILES even
    /// when the queued visual work has not run yet.
    /// </summary>
    internal void MarkNativeLaunchConsumed()
    {
        Interlocked.Exchange(ref _lastLaunchConsumedTicks, Stopwatch.GetTimestamp());
    }

    private Border? FindItemSurfaceBorder(WidgetItem item)
    {
        foreach (Border border in _itemSurfaces)
        {
            WidgetItem? candidate =
                FileItemSurface.FindOwner(border)?.DataContext as WidgetItem ??
                border.DataContext as WidgetItem;
            if (ReferenceEquals(candidate, item))
            {
                return border;
            }
        }

        return null;
    }

    private void ApplyNativeFolderDropTarget(WidgetItem folder)
    {
        if (FindItemSurfaceBorder(folder) is { } border)
        {
            SetFolderDropTarget(border);
        }
    }

    private void ApplyNativeStackDropTarget(WidgetStackItem stack)
    {
        if (FindStackSurface(stack.StackKey) is { } border)
        {
            SetStackMemberDropTarget(border);
        }
    }

    private DataPackageOperation ResolveFolderDropOperation(
        DataPackageView dataView,
        DataPackageOperation allowedOperations,
        bool forceCopy = false,
        string? destinationFolderPath = null) =>
        ToDataPackageOperation(
            ResolveSurfaceDropIntent(
                dataView,
                allowedOperations,
                forceCopy,
                destinationFolderPath));

    private void SetFolderDropTarget(Border border)
    {
        ClearStackMemberDropTarget();
        ClearLaunchDropTarget();
        if (ReferenceEquals(_folderDropTarget, border) &&
            _folderDropVisualActive)
        {
            return;
        }

        if (!ReferenceEquals(_folderDropTarget, border))
        {
            ClearFolderDropTarget();
            _folderDropTarget = border;
        }

        ApplyItemSurfaceVisual(border, FileItemSurfaceVisualState.DropTarget);
        _folderDropVisualActive = true;
    }

    private void ClearFolderDropTarget()
    {
        Border? previous = _folderDropTarget;
        _folderDropTarget = null;
        _folderDropVisualActive = false;
        if (previous?.XamlRoot is not null)
        {
            ApplyItemSurfaceVisual(previous, FileItemSurfaceVisualState.Normal);
        }
    }

    /// <summary>
    /// Highlights a shortcut tile that the pointer is hovering with files to
    /// open. Like every other drop target - a folder to import into, a stack to
    /// add to - the launch hover is the neutral hover surface with no border,
    /// so the shared style cache draws all three the same way.
    /// </summary>
    private void SetLaunchDropTarget(Border border)
    {
        ClearStackMemberDropTarget();
        if (ReferenceEquals(_launchDropTarget, border) &&
            _launchDropVisualActive)
        {
            return;
        }

        if (!ReferenceEquals(_launchDropTarget, border))
        {
            ClearLaunchDropTarget();
            _launchDropTarget = border;
        }

        ApplyItemSurfaceVisual(border, FileItemSurfaceVisualState.Hover);
        _launchDropVisualActive = true;
    }

    private void ClearLaunchDropTarget()
    {
        Border? previous = _launchDropTarget;
        _launchDropTarget = null;
        _launchDropVisualActive = false;
        if (previous?.XamlRoot is not null)
        {
            ApplyItemSurfaceVisual(previous, FileItemSurfaceVisualState.Normal);
        }
    }

    private static bool IsPointerInsideDropElement(
        FrameworkElement element,
        DragEventArgs e)
    {
        if (element.ActualWidth <= 0 || element.ActualHeight <= 0)
        {
            return false;
        }

        try
        {
            Windows.Foundation.Point point = e.GetPosition(element);
            return point.X >= 0 &&
                   point.Y >= 0 &&
                   point.X <= element.ActualWidth &&
                   point.Y <= element.ActualHeight;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private void StackSurface_Loaded(
        object sender,
        RoutedEventArgs e)
    {
        if (sender is Border border)
        {
            RestoreStackAnimationElement(border);
            _stackSurfaces.Add(border);
            border.DataContextChanged -= StackSurface_DataContextChanged;
            border.DataContextChanged += StackSurface_DataContextChanged;
            SubscribeStackSurfacePropertyChanges(border);
            ApplyStackFolderPreviewMode(border);
            ApplyStackSurfaceVisual(border, hovered: false);
        }
    }

    private void StackSurface_Unloaded(
        object sender,
        RoutedEventArgs e)
    {
        if (sender is Border border)
        {
            RestoreStackAnimationElement(border);
            border.DataContextChanged -= StackSurface_DataContextChanged;
            UnsubscribeStackSurfacePropertyChanges(border);
            if (ReferenceEquals(border, _stackMemberDropTarget))
            {
                _stackMemberDropTarget = null;
                _stackMemberDropVisualActive = false;
            }
            _stackSurfaces.Remove(border);
        }
    }

    private void StackSurface_DataContextChanged(
        FrameworkElement sender,
        DataContextChangedEventArgs args)
    {
        if (sender is not Border border)
        {
            return;
        }

        SubscribeStackSurfacePropertyChanges(border);
        ApplyStackFolderPreviewMode(border);
    }

    private void SubscribeStackSurfacePropertyChanges(Border border)
    {
        UnsubscribeStackSurfacePropertyChanges(border);
        if (border.DataContext is not WidgetStackItem stack)
        {
            return;
        }

        PropertyChangedEventHandler handler = (_, e) =>
        {
            // The folder-style preview sets the fourth miniature's Visibility
            // directly so it can switch between the inline and popover
            // compositions. That local value does not get replaced by a
            // binding notification when a stack grows. Reapply the preview
            // layout as soon as the stack publishes its new member list.
            if (e.PropertyName != nameof(WidgetStackItem.Members) ||
                border.XamlRoot is null)
            {
                return;
            }

            ApplyStackFolderPreviewMode(border);
        };

        stack.PropertyChanged += handler;
        _stackSurfacePropertyChangedHandlers[border] = (stack, handler);
    }

    private void UnsubscribeStackSurfacePropertyChanges(Border border)
    {
        if (_stackSurfacePropertyChangedHandlers.Remove(
                border,
                out (WidgetStackItem Stack, PropertyChangedEventHandler Handler) subscription))
        {
            subscription.Stack.PropertyChanged -= subscription.Handler;
        }
    }

    private void DisposeStackSurfacePropertyChanges()
    {
        foreach (Border border in _stackSurfacePropertyChangedHandlers.Keys.ToArray())
        {
            border.DataContextChanged -= StackSurface_DataContextChanged;
            UnsubscribeStackSurfacePropertyChanges(border);
        }
    }

    private void StackSurface_PointerEntered(
        object sender,
        PointerRoutedEventArgs e)
    {
        if (sender is Border border)
        {
            ApplyStackSurfaceVisual(border, hovered: true);
        }
    }

    private void StackSurface_PointerExited(
        object sender,
        PointerRoutedEventArgs e)
    {
        if (sender is Border border)
        {
            ApplyStackSurfaceVisual(border, hovered: false);
        }
    }
    private void StackSurface_PointerPressed(
        object sender,
        PointerRoutedEventArgs e)
    {
        if (sender is not Border border ||
            border.DataContext is not WidgetStackItem { IsExpanded: false } stack ||
            !e.GetCurrentPoint(border).Properties.IsLeftButtonPressed)
        {
            _pressedStack = null;
            _stackInputActivation.CancelPointer();
            return;
        }

        _pressedStack = stack;
        _stackPointerDragStarted = false;
        _stackInputActivation.BeginPointer(stack.StackKey);
        App.LogVerbose(
            $"[FileStack] Pointer pressed widget={WidgetId} " +
            $"stack={stack.StackKey}");
        border.Background =
            ResolveBrush("SubtleFillColorTertiaryBrush");
    }

    private void StackSurface_PointerReleased(
        object sender,
        PointerRoutedEventArgs e)
    {
        if (sender is not Border border)
        {
            _pressedStack = null;
            _stackPointerDragStarted = false;
            _stackInputActivation.CancelPointer();
            return;
        }

        Windows.Foundation.Point point =
            e.GetCurrentPoint(border).Position;
        bool inside =
            point.X >= 0 &&
            point.Y >= 0 &&
            point.X <= border.ActualWidth &&
            point.Y <= border.ActualHeight;
        WidgetStackItem? releasedStack =
            border.DataContext as WidgetStackItem;
        bool isValidRelease =
            inside &&
            !_stackPointerDragStarted &&
            releasedStack is { IsExpanded: false } &&
            ReferenceEquals(_pressedStack, releasedStack);
        bool shouldToggle =
            releasedStack is not null &&
            _stackInputActivation.ShouldActivateFromPointerRelease(
                releasedStack.StackKey,
                isValidRelease);
        _pressedStack = null;
        _stackPointerDragStarted = false;
        _stackInputActivation.EndPointer();
        ApplyStackSurfaceVisual(
            border,
            hovered: inside);

        if (shouldToggle && releasedStack is not null)
        {
            e.Handled = true;
            ToggleStackFromInput(releasedStack);
        }
    }

    private void StackSurface_DragOver(
        object sender,
        DragEventArgs e)
    {
        e.Handled = true;
        ClearExternalDropPreviewPlacement();
        if (sender is not Border
            {
                DataContext: WidgetStackItem stack
            } border)
        {
            e.AcceptedOperation = DataPackageOperation.None;
            ClearStackMemberDropTarget();
            return;
        }

        DragPayloadSnapshot payload = GetDragPayload(e.DataView);
        if (!payload.IsDeskBoxFileDrag && payload.HasSurfacePathData)
        {
            SuppressExternalDragOperationBadge(e);
        }

        if (payload.IsStackPopoverMemberDrag &&
            string.Equals(
                payload.SourceStackKey,
                stack.StackKey,
                StringComparison.Ordinal))
        {
            PersistSurfaceReorder();
            e.AcceptedOperation = DataPackageOperation.None;
            e.DragUIOverride.IsGlyphVisible = false;
            e.DragUIOverride.IsCaptionVisible = true;
            e.DragUIOverride.Caption = T("Widget.DragCaption.CurrentWidget");
            ClearStackMemberDropTarget();
            return;
        }

        if (TryGetStackDropItems(
                payload,
                stack,
                out _))
        {
            SetStackMemberDropTarget(border);
            DataPackageOperation internalOperation =
                ResolveInternalArrangementFeedbackOperation(
                    payload.IsDeskBoxFileDrag,
                    e.AllowedOperations,
                    e.DataView.RequestedOperation);
            e.AcceptedOperation = internalOperation;
            TraceInternalDragDecision("stack-membership", payload, e);
            if (payload.IsDeskBoxFileDrag)
            {
                ApplyDeskBoxFileDragFeedback(
                    e,
                    internalOperation,
                    _localizationService.Format(
                        "Widget.Stack.DragCaption.Add",
                        stack.Name));
            }
            return;
        }

        if (!payload.HasSurfacePathData ||
            _isImportBusy ||
            HasTransferConflict(payload.Paths, ViewModel.CurrentFolderPath))
        {
            e.AcceptedOperation = DataPackageOperation.None;
            ClearStackMemberDropTarget();
            return;
        }

        if (IsUnsafeFolderDrop(
                payload.Paths,
                ViewModel.CurrentFolderPath))
        {
            e.AcceptedOperation = DataPackageOperation.None;
            if (payload.IsDeskBoxFileDrag)
            {
                ApplyDeskBoxFileDragFeedback(
                    e,
                    DataPackageOperation.None,
                    T("Widget.Error.UnsafeFolderTransfer"));
            }
            ClearStackMemberDropTarget();
            return;
        }

        if (AreAllSourcesAlreadyInDestinationLexically(
                payload.Paths,
                ViewModel.CurrentFolderPath))
        {
            e.AcceptedOperation = DataPackageOperation.None;
            if (payload.IsDeskBoxFileDrag)
            {
                ApplyDeskBoxFileDragFeedback(
                    e,
                    DataPackageOperation.None,
                    T("Widget.DragCaption.CurrentWidget"));
            }
            ClearStackMemberDropTarget();
            return;
        }

        SetStackMemberDropTarget(border);
        FileDropIntent resolvedIntent = ResolveSurfaceDropIntent(
            payload.DataView,
            e.AllowedOperations,
            destinationFolderPath: ViewModel.CurrentFolderPath);
        e.AcceptedOperation = ToDataPackageOperation(resolvedIntent);
        if (payload.IsDeskBoxFileDrag)
        {
            ApplyDeskBoxFileDragFeedback(
                e,
                e.AcceptedOperation,
                resolvedIntent == FileDropIntent.Shortcut
                    ? FormatDropCaption(resolvedIntent, stack.Name)
                    : _localizationService.Format(
                        "Widget.Stack.DragCaption.Add",
                        stack.Name));
        }
    }

    private void StackSurface_DragLeave(
        object sender,
        DragEventArgs e)
    {
        e.Handled = true;
        if (sender is Border border &&
            IsPointerInsideDropElement(border, e))
        {
            return;
        }

        if (ReferenceEquals(
                sender,
                _stackMemberDropTarget))
        {
            ClearStackMemberDropTarget();
        }
    }

    private async void StackSurface_Drop(
        object sender,
        DragEventArgs e)
    {
        e.Handled = true;
        e.AcceptedOperation = DataPackageOperation.None;
        int? preferredStackMemberIndex = null;
        if (ReferenceEquals(sender, _stackPopoverSurface) &&
            _stackPopoverItemsView is { } popoverView &&
            _stackPopoverReorderInsertionIndex >= 0 &&
            _stackPopoverReorderInsertionIndex < popoverView.Items.Count)
        {
            preferredStackMemberIndex =
                ResolveStackPopoverMemberInsertionIndex(
                    popoverView,
                    e.GetPosition(popoverView));
        }
        HideStackPopoverReorderIndicator();
        if (sender is not Border
            {
                DataContext: WidgetStackItem stack
            })
        {
            e.AcceptedOperation = DataPackageOperation.None;
            ClearStackMemberDropTarget();
            ResetDragPayloadCache();
            return;
        }

        string targetStackKey = stack.StackKey;
        string[] targetStackMemberAnchors = stack.Members
            .Select(member => member.Path)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .ToArray();

        DragPayloadSnapshot payload = GetDragPayload(e.DataView);
        TraceTargetDropEntered("stack-membership", payload, e);

        if (HasTransferConflict(payload.Paths, ViewModel.CurrentFolderPath))
        {
            e.AcceptedOperation = DataPackageOperation.None;
            ShowTransferBlockedFeedback(
                _fileService.TransferSessions.GetState(
                    payload.Paths.FirstOrDefault()) is { IsActive: true } sourceState
                    ? sourceState
                    : _fileService.TransferSessions.GetState(
                        ViewModel.CurrentFolderPath));
            ClearStackMemberDropTarget();
            ResetDragPayloadCache();
            return;
        }

        if (payload.IsStackPopoverMemberDrag &&
            string.Equals(
                payload.SourceStackKey,
                stack.StackKey,
                StringComparison.Ordinal))
        {
            _activeDragHandledAsStackMembership = true;
            e.AcceptedOperation = DataPackageOperation.None;
            ClearStackMemberDropTarget();
            PersistSurfaceReorder();
            ResetDragPayloadCache();
            return;
        }

        if (!TryGetStackDropItems(
                payload,
                stack,
                out WidgetItem[] items))
        {
            if (!payload.HasSurfacePathData || _isImportBusy)
            {
                e.AcceptedOperation = DataPackageOperation.None;
                ClearStackMemberDropTarget();
                ResetDragPayloadCache();
                return;
            }

            ClearStackMemberDropTarget();
            var deferral = e.GetDeferral();
            HashSet<string> existingPaths = ViewModel.Items
                .Select(item => Path.GetFullPath(item.Path))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            BeginTrackedImport();
            try
            {
                using DroppedFileBatch batch =
                    await GetSurfaceDropFilesAsync(e.DataView);
                if (await AreAllSourcesAlreadyInDestinationResolvedAsync(
                        batch.Files.Select(file => file.Path),
                        ViewModel.CurrentFolderPath))
                {
                    e.AcceptedOperation = DataPackageOperation.None;
                    ShowSameDirectoryDropFeedback();
                    CancelAndResetTrackedImport();
                    return;
                }
                FileDropIntent resolvedIntent = ResolveSurfaceDropIntent(
                    payload.DataView,
                    e.AllowedOperations,
                    forceCopy: batch.Files.Any(file => file.ForceManagedCopy),
                    destinationFolderPath: ViewModel.CurrentFolderPath,
                    sourcePathsOverride: batch.Files.Select(file => file.Path));
                DataPackageOperation accepted =
                    ToDataPackageOperation(resolvedIntent);
                if (accepted == DataPackageOperation.None)
                {
                    e.AcceptedOperation = DataPackageOperation.None;
                    CancelAndResetTrackedImport();
                    return;
                }

                bool mapped = !string.IsNullOrWhiteSpace(
                    ViewModel.MappedFolderPath);
                bool? moveWhenMapped = mapped
                    ? accepted == DataPackageOperation.Move
                    : null;
                string? sourceWidgetId = TryGetString(
                    e.DataView.Properties,
                    "DeskBoxSourceWidgetId");
                IReadOnlyList<string> completedSourcePaths =
                    await ImportDroppedFilesAsync(
                        batch.Files,
                        moveWhenMapped,
                        intentOverride: resolvedIntent == FileDropIntent.Shortcut
                            ? FileDropIntent.Shortcut
                            : null);
                WidgetItem[] importedItems = ViewModel.Items
                    .Where(item => !existingPaths.Contains(
                        Path.GetFullPath(item.Path)))
                    .ToArray();
                bool importedIntoStack = importedItems.Length > 0 &&
                    ViewModel.AddItemsToStack(
                        stack.StackKey,
                        importedItems);
                if (importedIntoStack &&
                    preferredStackMemberIndex is { } stackMemberIndex)
                {
                    ApplyImportedStackMemberInsertion(
                        stack,
                        importedItems,
                        stackMemberIndex);
                }
                if (moveWhenMapped == true &&
                    sourceWidgetId is { Length: > 0 } &&
                    App.Current?.WidgetManager is { } manager)
                {
                    await manager.NotifyItemsMovedOutAsync(
                        sourceWidgetId,
                        completedSourcePaths);
                }

                int requestedMoveCount = batch.Files.Count(file =>
                    !file.ForceManagedCopy);
                e.AcceptedOperation = importedIntoStack
                    ? ResolveSafeDropCompletionOperation(
                        accepted,
                        payload.IsDeskBoxFileDrag,
                        requestedMoveCount,
                        completedSourcePaths.Count)
                    : DataPackageOperation.None;
                if (importedIntoStack)
                {
                    ClearSelection();
                    QueueStackPopoverReconciliation(
                        targetStackKey,
                        targetStackMemberAnchors);
                }
            }
            catch (OperationCanceledException)
            {
                e.AcceptedOperation = DataPackageOperation.None;
                if (_activeImportCancellation is not null)
                {
                    await CompleteTrackedImportAsync(
                        ImportCompletionState.Canceled);
                }
            }
            catch (Exception ex)
            {
                e.AcceptedOperation = DataPackageOperation.None;
                if (_activeImportCancellation is not null)
                {
                    await CompleteTrackedImportAsync(
                        ImportCompletionState.Failed);
                }
                ShowFeedback(new(
                    ex.Message,
                    WidgetFeedbackSeverity.Error,
                    "stack-file-drop-error"));
            }
            finally
            {
                ResetDragPayloadCache();
                deferral.Complete();
            }
            return;
        }

        ClearStackMemberDropTarget();
        try
        {
            _activeDragHandledAsStackMembership = true;
            bool added = false;
            if (payload.IsStackPopoverMemberDrag)
            {
                ApplyStackProjectionChange(() =>
                    added = ViewModel.AddItemsToStack(
                        stack.StackKey,
                        items));
            }
            else
            {
                added = ViewModel.AddItemsToStack(
                    stack.StackKey,
                    items);
            }
            e.AcceptedOperation = added
                ? ResolveInternalArrangementCompletionOperation(
                    e.AllowedOperations,
                    e.DataView.RequestedOperation)
                : Windows.ApplicationModel.DataTransfer
                    .DataPackageOperation.None;
            // This is a stack-membership drop, not an ordering drop. Clear the
            // complete reorder session, including the cached insertion position.
            PersistSurfaceReorder();
            if (added)
            {
                ClearSelection();
                QueueStackPopoverReconciliation(
                    targetStackKey,
                    targetStackMemberAnchors);
                if (payload.IsStackPopoverMemberDrag)
                {
                    CloseStackPopover();
                }
            }
        }
        finally
        {
            ResetDragPayloadCache();
        }
    }

    private bool TryGetStackDropItems(
        DragPayloadSnapshot payload,
        WidgetStackItem targetStack,
        out WidgetItem[] items)
    {
        items = [];
        if (!payload.IsInternalReorder ||
            !string.IsNullOrWhiteSpace(payload.StackReorderKey))
        {
            return false;
        }

        if (ReferenceEquals(_stackDropItemsDataView, payload.DataView) &&
            string.Equals(
                _stackDropItemsTargetKey,
                targetStack.StackKey,
                StringComparison.Ordinal) &&
            _stackDropItemsTargetMemberCount == targetStack.Members.Count)
        {
            items = _stackDropItemsCache;
            return items.Length > 0;
        }

        HashSet<string> targetPaths = targetStack.Members
            .Select(item => Path.GetFullPath(item.Path))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        HashSet<string> sourcePaths = payload.Paths
            .Select(Path.GetFullPath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        items = ViewModel.Items
            .Where(item =>
                sourcePaths.Contains(
                    Path.GetFullPath(item.Path)) &&
                !targetPaths.Contains(
                    Path.GetFullPath(item.Path)))
            .ToArray();
        _stackDropItemsDataView = payload.DataView;
        _stackDropItemsTargetKey = targetStack.StackKey;
        _stackDropItemsTargetMemberCount = targetStack.Members.Count;
        _stackDropItemsCache = items;
        return items.Length > 0;
    }

    private void SetStackMemberDropTarget(
        Border border)
    {
        ClearFolderDropTarget();
        ClearLaunchDropTarget();
        if (!ReferenceEquals(
                _stackMemberDropTarget,
                border))
        {
            ClearStackMemberDropTarget();
            _stackMemberDropTarget = border;
        }

        if (_stackMemberDropVisualActive &&
            ReferenceEquals(_stackMemberDropTarget, border))
        {
            return;
        }

        ApplyStackSurfaceDropVisual(border);
        _stackMemberDropVisualActive = true;
    }

    private void ClearStackMemberDropTarget()
    {
        Border? previous = _stackMemberDropTarget;
        _stackMemberDropTarget = null;
        _stackMemberDropVisualActive = false;
        if (previous?.XamlRoot is not null)
        {
            if (ReferenceEquals(previous, _stackPopoverSurface))
            {
                UpdateStackPopoverAppearance();
            }
            else
            {
                ApplyStackSurfaceVisual(
                    previous,
                    hovered: false);
            }
        }
    }

    private void ApplyStackSurfaceDropVisual(
        Border border)
    {
        // A stack the pointer is hovering with files to add reads as the same
        // neutral hover surface as every other drop target. The accent color no
        // longer enters the drop visual, so there is no per-accent brush state
        // to track here either.
        border.Background = ResolveBrush("SubtleFillColorSecondaryBrush");
        border.BorderBrush = GetStackTransparentBrush();
        border.BorderThickness = new Thickness(0);
    }


    private void StackCollapseButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (sender is FrameworkElement
            {
                DataContext: WidgetStackItem stack
            })
        {
            RequestStackState(stack, expanded: false);
        }
    }

    private void ResetStackInteractionVisuals()
    {
        _stackTransitionGeneration++;
        CancelStackTransition(
            Interlocked.Exchange(
                ref _stackTransitionCancellation,
                null));
        _pendingStackTransitionKey = null;
        _pendingStackExpanded = null;
        StopAndRestoreStackAnimations();
        _pressedStack = null;
        _stackPointerDragStarted = false;
        _stackInputActivation.CancelPointer();
        ClearStackMemberDropTarget();
        foreach (Border surface in _stackSurfaces.ToArray())
        {
            if (surface.XamlRoot is null)
            {
                _stackSurfaces.Remove(surface);
                continue;
            }

            ApplyStackSurfaceVisual(surface, hovered: false);
        }
    }

    private void StackToggleButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (sender is FrameworkElement
            {
                DataContext: WidgetStackItem stack
            })
        {
            ToggleStackFromInput(stack);
        }
    }

    private void RefreshItemSelectionVisuals() =>
        UpdateItemSurfaceVisuals();

    private void ClearOtherWidgetSelections()
    {
        App.Current.WidgetManager?.ClearSelectionsExcept(WidgetId);
    }

    private void UpdateItemSurfaceVisuals()
    {
        ListViewBase activeView = GetActiveItemsView();
        foreach (WidgetItem item in activeView.Items
                     .OfType<WidgetItem>()
                     .Where(item => item is not WidgetStackItem))
        {
            if (activeView.ContainerFromItem(item) is not SelectorItem container ||
                FindDescendantByTag(container, "InteractiveSurface") is not Border border)
            {
                continue;
            }

            // Collection projection can realize an expanded stack member
            // without delivering the template Loaded event to this host. Find
            // every realized surface from the native item containers so a
            // later SelectionChanged always refreshes previously selected
            // stack children as well.
            _itemSurfaces.Add(border);
            FileItemSurfaceVisualState state =
                FileItemSurface.FindOwner(border)?.VisualState ??
                FileItemSurfaceVisualState.Normal;
            ApplyItemSurfaceVisual(border, state);
        }

        foreach (Border border in _itemSurfaces.ToArray())
        {
            if (border.XamlRoot is null)
            {
                _itemSurfaces.Remove(border);
            }
        }
    }

    private void ApplyItemSurfaceVisual(
        Border border,
        FileItemSurfaceVisualState state)
    {
        bool isDropTarget = ReferenceEquals(border, _folderDropTarget) ||
            ReferenceEquals(border, _launchDropTarget);
        if (isDropTarget && state != FileItemSurfaceVisualState.DropTarget)
        {
            // Keep the state describing the drop while the shared cache renders
            // every drop target with the neutral hover surface.
            state = FileItemSurfaceVisualState.DropTarget;
        }

        WidgetItem? item =
            FileItemSurface.FindOwner(border)?.DataContext as WidgetItem ??
            border.DataContext as WidgetItem;
        FileItemSurface? surface = FileItemSurface.FindOwner(border);
        FileTransferPathState transferState = GetTransferState(item);
        surface?.SetTransferState(
            transferState,
            GetTransferStatusText(transferState));
        bool isSelected = item is not null &&
                          item is not WidgetStackItem &&
                          GetActiveItemsView().SelectedItems.Contains(item);
        _itemSurfaceStyleCache.Apply(
            border,
            state,
            Root.ActualTheme,
            isSelected,
            item?.IsCut == true,
            isDropTarget: state == FileItemSurfaceVisualState.DropTarget);
    }

    private void ApplyStackSurfaceVisual(
        Border border,
        bool hovered)
    {
        if (_stackMemberDropVisualActive &&
            ReferenceEquals(border, _stackMemberDropTarget))
        {
            return;
        }

        border.Background = hovered
            ? ResolveBrush("SubtleFillColorSecondaryBrush")
            : GetStackTransparentBrush();
        border.BorderBrush = GetStackTransparentBrush();
        border.BorderThickness = new Thickness(0);
    }

    private SolidColorBrush GetStackTransparentBrush() =>
        _stackTransparentBrush ??= new SolidColorBrush(Colors.Transparent);

    private static Windows.UI.Color WithAlpha(
        Windows.UI.Color color,
        byte alpha)
    {
        return ColorHelper.FromArgb(
            alpha,
            color.R,
            color.G,
            color.B);
    }
}
