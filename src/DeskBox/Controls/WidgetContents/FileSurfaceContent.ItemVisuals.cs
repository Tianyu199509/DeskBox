using DeskBox.Controls;
using DeskBox.Contracts;
using DeskBox.Helpers;
using DeskBox.Models;
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

namespace DeskBox.Controls.WidgetContents;

public sealed partial class FileSurfaceContent
{
    private readonly HashSet<Border> _itemSurfaces = [];
    private readonly HashSet<Border> _stackSurfaces = [];
    private readonly FileItemSurfaceStyleCache _itemSurfaceStyleCache = new();
    private bool _folderDropVisualActive;
    private SolidColorBrush? _stackDropBackgroundBrush;
    private SolidColorBrush? _stackDropBorderBrush;
    private SolidColorBrush? _stackTransparentBrush;
    private Windows.UI.Color _stackDropBrushAccent;
    private ElementTheme _stackDropBrushTheme;
    private bool _stackDropBrushesInitialized;
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
        bool isDark = Root.ActualTheme == ElementTheme.Dark;
        Windows.UI.Color accent =
            App.Current.ThemeService?.GetEffectiveAccentColor() ??
            AccentColorHelper.DefaultAccentColor;
        rectangle.Background = new SolidColorBrush(
            WithAlpha(accent, isDark ? (byte)0x2D : (byte)0x24));
        rectangle.BorderBrush = new SolidColorBrush(
            WithAlpha(accent, isDark ? (byte)0xD8 : (byte)0xCC));
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
            // Loaded can be raised again when a virtualized popup container is
            // reattached without a matching unload on some WinUI versions.
            // Make the host subscription idempotent so each item surface keeps
            // exactly one callback to this content instance.
            surface.VisualStateChanged -= ItemSurface_VisualStateChanged;
            surface.VisualStateChanged += ItemSurface_VisualStateChanged;
        }

        if (FileItemSurface.TryGetInteractiveBorder(sender) is { } border)
        {
            RestoreStackAnimationElement(border);
            _itemSurfaces.Add(border);
            ApplyItemSurfaceVisual(border, FileItemSurfaceVisualState.Normal);
        }
    }

    private void ItemSurface_Unloaded(
        object sender,
        RoutedEventArgs e)
    {
        if (sender is FileItemSurface surface)
        {
            surface.VisualStateChanged -= ItemSurface_VisualStateChanged;
        }

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

    private void ItemSurface_DragOver(
        object sender,
        DragEventArgs e)
    {
        if (!TryGetFolderDropTarget(sender, out Border border, out WidgetItem targetFolder))
        {
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

        FileDropIntent resolvedIntent = ResolveSurfaceDropIntent(
            payload.DataView,
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
            if (sourcePaths.Length == 0 ||
                HasTransferConflict(sourcePaths, targetFolder.Path) ||
                IsUnsafeFolderDrop(sourcePaths, targetFolder.Path))
            {
                e.AcceptedOperation = DataPackageOperation.None;
                if (HasTransferConflict(sourcePaths, targetFolder.Path))
                {
                    FileTransferPathState targetState =
                        GetTransferState(targetFolder);
                    ShowTransferBlockedFeedback(
                        targetState.IsActive
                            ? targetState
                            : _fileService.TransferSessions.GetState(
                                sourcePaths.FirstOrDefault()));
                }
                if (sourcePaths.Length > 0)
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

    private static bool TryGetFolderDropTarget(
        object sender,
        out Border border,
        out WidgetItem folder)
    {
        if (sender is FileItemSurface surface &&
            surface.DataContext is WidgetItem
            {
                IsFolder: true,
                Path.Length: > 0
            } item)
        {
            border = surface.InteractiveBorder;
            folder = item;
            return true;
        }

        border = null!;
        folder = null!;
        return false;
    }

    private DataPackageOperation ResolveFolderDropOperation(
        DataPackageView dataView,
        bool forceCopy = false,
        string? destinationFolderPath = null) =>
        ToDataPackageOperation(
            ResolveSurfaceDropIntent(
                dataView,
                forceCopy,
                destinationFolderPath));

    private void SetFolderDropTarget(Border border)
    {
        ClearStackMemberDropTarget();
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
            if (ReferenceEquals(border, _stackMemberDropTarget))
            {
                _stackMemberDropTarget = null;
                _stackMemberDropVisualActive = false;
            }
            _stackSurfaces.Remove(border);
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
            e.AcceptedOperation = DataPackageOperation.Link;
            if (payload.IsDeskBoxFileDrag)
            {
                ApplyDeskBoxFileDragFeedback(
                    e,
                    DataPackageOperation.Link,
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

        SetStackMemberDropTarget(border);
        FileDropIntent resolvedIntent = ResolveSurfaceDropIntent(
            payload.DataView,
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
                FileDropIntent resolvedIntent = ResolveSurfaceDropIntent(
                    payload.DataView,
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
                if (moveWhenMapped == true &&
                    sourceWidgetId is { Length: > 0 } &&
                    App.Current?.WidgetManager is { } manager)
                {
                    await manager.NotifyItemsMovedOutAsync(
                        sourceWidgetId,
                        completedSourcePaths);
                }

                e.AcceptedOperation = importedIntoStack
                    ? DataPackageOperation.Link
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
            if (payload.IsStackPopoverMemberDrag)
            {
                _activeDragHandledAsStackMembership = true;
            }
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
                ? Windows.ApplicationModel.DataTransfer
                    .DataPackageOperation.Link
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
        Windows.UI.Color accent =
            App.Current.ThemeService?.GetEffectiveAccentColor() ??
            AccentColorHelper.DefaultAccentColor;
        ElementTheme theme = Root.ActualTheme;
        if (!_stackDropBrushesInitialized ||
            !_stackDropBrushAccent.Equals(accent) ||
            _stackDropBrushTheme != theme)
        {
            _stackDropBrushAccent = accent;
            _stackDropBrushTheme = theme;
            _stackDropBackgroundBrush = new SolidColorBrush(
                WithAlpha(
                    accent,
                    theme == ElementTheme.Dark
                        ? (byte)0x38
                        : (byte)0x28));
            _stackDropBorderBrush = new SolidColorBrush(
                WithAlpha(accent, 0xD8));
            _stackDropBrushesInitialized = true;
        }

        border.Background = _stackDropBackgroundBrush;
        border.BorderBrush = _stackDropBorderBrush;
        border.BorderThickness = new Thickness(1);
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
        if (ReferenceEquals(border, _folderDropTarget) &&
            state != FileItemSurfaceVisualState.DropTarget)
        {
            state = FileItemSurfaceVisualState.DropTarget;
        }

        Windows.UI.Color accent =
            App.Current.ThemeService?.GetEffectiveAccentColor() ??
            AccentColorHelper.DefaultAccentColor;
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
            accent,
            isSelected,
            item?.IsCut == true);
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
