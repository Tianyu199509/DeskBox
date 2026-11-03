using DeskBox.Contracts;
using DeskBox.Controls;
using DeskBox.Controls.WidgetContents;
using DeskBox.Helpers;
using DeskBox.Models;
using DeskBox.Platform;
using DeskBox.Services;
using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics;
using WinRT;
using WinRT.Interop;

namespace DeskBox.Views;

public sealed partial class ContentWidgetWindow
{
    private static readonly TimeSpan GroupKeyboardSwitchCooldown =
        TimeSpan.FromMilliseconds(450);
    private bool _groupKeyboardTabGestureActive;
    private DateTimeOffset _lastGroupKeyboardSwitchAt;
    private readonly Dictionary<string, (DateTime LastWriteUtc, bool Accepts)>
        _customMemberFileDropCache = new(StringComparer.Ordinal);

    private void RootGrid_DragOver(object sender, DragEventArgs e)
    {
        Windows.Foundation.Point rootPoint = e.GetPosition(RootGrid);
        if (WidgetShellControl.IsPointOverGroupTitleBar(RootGrid, rootPoint))
        {
            string? tabId = WidgetShellControl.HitTestGroupTitleDropTargetMemberId(
                RootGrid,
                rootPoint);
            if (string.IsNullOrWhiteSpace(tabId) ||
                !DoesGroupMemberAcceptDrag(tabId, e.DataView))
            {
                e.AcceptedOperation = DataPackageOperation.None;
                e.Handled = true;
                return;
            }

            if (!string.Equals(CurrentContent?.WidgetId, tabId, StringComparison.Ordinal))
            {
                e.AcceptedOperation = ResolvePendingGroupTabDragOperation(
                    tabId,
                    e.DataView,
                    e.AllowedOperations);
                e.Handled = true;
                return;
            }
        }

        if (CurrentContent is FileWidgetContentAdapter file)
        {
            // The transparent resize grid sits above the content along every
            // window edge. Reuse the file surface's normal feedback there so
            // the cursor does not briefly report a forbidden drop even though
            // the native window drop target can accept it.
            file.ApplyHostEdgeDragOverFeedback(e);
            return;
        }

        if (!IsCompactBoundsStateActive)
        {
            return;
        }

        if (CurrentContent is TodoWidgetContentAdapter todo)
        {
            e.AcceptedOperation = todo.CanImportExternalDrop(e.DataView)
                ? DeskBoxDragData.HasDroppedFiles(e.DataView)
                    ? DeskBoxDragData.GetFileAssociationOperation(
                        e.DataView,
                        e.AllowedOperations)
                    : DataPackageOperation.Copy
                : DataPackageOperation.None;
            if (DeskBoxDragData.HasDroppedFiles(e.DataView))
            {
                bool isInternalFileDrag =
                    DeskBoxDragData.IsInternalFileDrag(e.DataView);
                e.DragUIOverride.IsContentVisible = isInternalFileDrag;
                e.DragUIOverride.IsGlyphVisible = isInternalFileDrag;
                e.DragUIOverride.IsCaptionVisible = isInternalFileDrag;
                e.DragUIOverride.Caption = isInternalFileDrag
                    ? App.Current.LocalizationService.T(
                        "Widget.Compact.TodoDropHint")
                    : string.Empty;
            }
            else
            {
                e.DragUIOverride.IsGlyphVisible = false;
                e.DragUIOverride.Caption =
                    e.AcceptedOperation == DataPackageOperation.None
                        ? string.Empty
                        : App.Current.LocalizationService.T(
                            "Widget.Compact.TodoDropHint");
            }
            e.Handled = true;
            return;
        }

        if (CurrentContent is CustomWidgetContentAdapter custom)
        {
            e.AcceptedOperation = custom.CanImportExternalDrop(e.DataView)
                ? DeskBoxDragData.GetFileAssociationOperation(e.DataView, e.AllowedOperations)
                : DataPackageOperation.None;
            e.Handled = true;
            return;
        }

        if (CurrentContent is ClipboardWidgetContentAdapter clipboard)
        {
            e.AcceptedOperation = clipboard.CanImportExternalDrop(e.DataView)
                ? DeskBoxDragData.HasDroppedFiles(e.DataView)
                    ? DeskBoxDragData.GetFileAssociationOperation(e.DataView, e.AllowedOperations)
                    : DataPackageOperation.Copy
                : DataPackageOperation.None;
            e.Handled = true;
            return;
        }

        if (CurrentContent is QuickCaptureWidgetContentAdapter quickCapture)
        {
            e.AcceptedOperation = quickCapture.CanImportExternalDrop(e.DataView)
                ? DeskBoxDragData.HasDroppedFiles(e.DataView)
                    ? DeskBoxDragData.GetFileAssociationOperation(e.DataView, e.AllowedOperations)
                    : DataPackageOperation.Copy
                : DataPackageOperation.None;
            e.Handled = true;
        }
    }

    private async void RootGrid_Drop(object sender, DragEventArgs e)
    {
        Windows.Foundation.Point rootPoint = e.GetPosition(RootGrid);
        if (WidgetShellControl.IsPointOverGroupTitleBar(RootGrid, rootPoint))
        {
            string? tabId = WidgetShellControl.HitTestGroupTitleDropTargetMemberId(
                    RootGrid,
                    rootPoint) ??
                WidgetShellControl.ConsumePendingGroupTabDropTargetId();
            if (string.IsNullOrWhiteSpace(tabId) ||
                !DoesGroupMemberAcceptDrag(tabId, e.DataView))
            {
                e.AcceptedOperation = DataPackageOperation.None;
                e.Handled = true;
                return;
            }

            if (!string.Equals(CurrentContent?.WidgetId, tabId, StringComparison.Ordinal))
            {
                await HandleInactiveGroupTabRoutedDropAsync(tabId, e);
                return;
            }
        }

        if (CurrentContent is FileWidgetContentAdapter file)
        {
            file.HandleHostEdgeDrop(e);
            if (e.Handled)
            {
                MarkXamlDropClaimed(e.DataView);
            }
            return;
        }

        if (!IsCompactBoundsStateActive)
        {
            return;
        }

        DataPackageOperation allowedOperations = e.AllowedOperations;
        var deferral = e.GetDeferral();
        try
        {
            if (CurrentContent is TodoWidgetContentAdapter todo)
            {
                e.Handled = true;
                MarkXamlDropClaimed(e.DataView);
                e.AcceptedOperation = await todo.ImportExternalDropAsync(e.DataView)
                    ? DeskBoxDragData.HasDroppedFiles(e.DataView)
                        ? DeskBoxDragData.GetFileAssociationOperation(e.DataView, allowedOperations)
                        : DataPackageOperation.Copy
                    : DataPackageOperation.None;
            }
            else if (CurrentContent is CustomWidgetContentAdapter custom)
            {
                e.Handled = true;
                MarkXamlDropClaimed(e.DataView);
                e.AcceptedOperation = await custom.ImportExternalDropAsync(e.DataView)
                    ? DeskBoxDragData.GetFileAssociationOperation(e.DataView, allowedOperations)
                    : DataPackageOperation.None;
            }
            else if (CurrentContent is ClipboardWidgetContentAdapter clipboard)
            {
                e.Handled = true;
                MarkXamlDropClaimed(e.DataView);
                e.AcceptedOperation = await clipboard.ImportExternalDropAsync(e.DataView)
                    ? DeskBoxDragData.HasDroppedFiles(e.DataView)
                        ? DeskBoxDragData.GetFileAssociationOperation(e.DataView, allowedOperations)
                        : DataPackageOperation.Copy
                    : DataPackageOperation.None;
            }
            else if (CurrentContent is QuickCaptureWidgetContentAdapter quickCapture)
            {
                e.Handled = true;
                MarkXamlDropClaimed(e.DataView);
                e.AcceptedOperation = await quickCapture.ImportExternalDropAsync(e.DataView)
                    ? DeskBoxDragData.HasDroppedFiles(e.DataView)
                        ? DeskBoxDragData.GetFileAssociationOperation(e.DataView, allowedOperations)
                        : DataPackageOperation.Copy
                    : DataPackageOperation.None;
            }
        }
        finally
        {
            deferral.Complete();
        }
    }

    private bool DoesGroupMemberAcceptDrag(string memberWidgetId, DataPackageView dataView)
    {
        WidgetConfig? memberConfig = SettingsService.Settings.WidgetLayout.Widgets
            .FirstOrDefault(w => string.Equals(w.Id, memberWidgetId, StringComparison.Ordinal));
        if (memberConfig is null)
        {
            return false;
        }

        return memberConfig.WidgetKind switch
        {
            WidgetKind.File => DeskBoxDragData.HasDroppedFiles(dataView),
            WidgetKind.QuickCapture => QuickCaptureSurfaceContent.CanImportExternalDrop(dataView),
            WidgetKind.Todo => DeskBoxDragData.HasDroppedFiles(dataView) ||
                DeskBoxDragData.TryGetQuickCaptureDragItems(dataView) is { Count: > 0 } ||
                dataView.Contains(StandardDataFormats.WebLink) ||
                dataView.Contains(StandardDataFormats.Text),
            WidgetKind.Clipboard => DeskBoxDragData.HasDroppedFiles(dataView) ||
                dataView.Contains(StandardDataFormats.WebLink) ||
                dataView.Contains(StandardDataFormats.Text),
            WidgetKind.Custom => DeskBoxDragData.HasDroppedFiles(dataView) &&
                DoesCustomGroupMemberAcceptFileDrops(memberWidgetId),
            _ => false,
        };
    }

    internal bool DoesGroupMemberAcceptFileDrop(string memberWidgetId)
    {
        WidgetConfig? memberConfig = SettingsService.Settings.WidgetLayout.Widgets
            .FirstOrDefault(w => string.Equals(w.Id, memberWidgetId, StringComparison.Ordinal));
        if (memberConfig is null)
        {
            return false;
        }

        return memberConfig.WidgetKind switch
        {
            WidgetKind.File or
            WidgetKind.QuickCapture or
            WidgetKind.Todo or
            WidgetKind.Clipboard => true,
            WidgetKind.Custom => DoesCustomGroupMemberAcceptFileDrops(memberWidgetId),
            _ => false,
        };
    }

    private bool DoesCustomGroupMemberAcceptFileDrops(string memberWidgetId)
    {
        if (CurrentContent is CustomWidgetContentAdapter activeCustom &&
            string.Equals(activeCustom.WidgetId, memberWidgetId, StringComparison.Ordinal))
        {
            return activeCustom.ViewModel.AcceptsFileDrops;
        }

        if (_cachedGroupContents.TryGetValue(memberWidgetId, out Contracts.IWidgetContent? cached) &&
            cached is CustomWidgetContentAdapter cachedCustom)
        {
            return cachedCustom.ViewModel.AcceptsFileDrops;
        }

        try
        {
            var store = new CustomWidgetDataStore(memberWidgetId, createDirectory: false);
            string manifestPath = store.ManifestPath;
            if (!File.Exists(manifestPath))
            {
                return false;
            }

            DateTime writeUtc = File.GetLastWriteTimeUtc(manifestPath);
            if (_customMemberFileDropCache.TryGetValue(memberWidgetId, out var cachedEntry) &&
                cachedEntry.LastWriteUtc == writeUtc)
            {
                return cachedEntry.Accepts;
            }

            string rawJson = File.ReadAllText(manifestPath);
            bool accepts = ViewModels.CustomWidgetViewModel.DoesManifestJsonAcceptFileDrops(rawJson);
            _customMemberFileDropCache[memberWidgetId] = (writeUtc, accepts);
            return accepts;
        }
        catch
        {
            return false;
        }
    }

    internal string? HitTestInactiveGroupTitleMemberId(int screenX, int screenY)
    {
        if (IsClosing || RootGrid.XamlRoot is null)
        {
            return null;
        }

        var clientPoint = new Win32Helper.POINT { X = screenX, Y = screenY };
        if (!Win32Helper.ScreenToClient(HWnd, ref clientPoint))
        {
            return null;
        }

        double scale = RootGrid.XamlRoot.RasterizationScale;
        if (scale <= 0)
        {
            scale = 1;
        }

        var point = new Windows.Foundation.Point(
            clientPoint.X / scale,
            clientPoint.Y / scale);
        if (!WidgetShellControl.IsPointOverGroupTitleBar(RootGrid, point))
        {
            return null;
        }

        string? memberId = WidgetShellControl.HitTestGroupTitleDropTargetMemberId(
            RootGrid,
            point);
        if (string.IsNullOrWhiteSpace(memberId) ||
            string.Equals(CurrentContent?.WidgetId, memberId, StringComparison.Ordinal))
        {
            return null;
        }

        return memberId;
    }

    private DataPackageOperation ResolvePendingGroupTabDragOperation(
        string memberWidgetId,
        DataPackageView dataView,
        DataPackageOperation allowedOperations)
    {
        if (!DeskBoxDragData.HasDroppedFiles(dataView))
        {
            return allowedOperations.HasFlag(DataPackageOperation.Copy)
                ? DataPackageOperation.Copy
                : DataPackageOperation.None;
        }

        WidgetConfig? memberConfig = SettingsService.Settings.WidgetLayout.Widgets
            .FirstOrDefault(w => string.Equals(w.Id, memberWidgetId, StringComparison.Ordinal));
        if (memberConfig?.WidgetKind == WidgetKind.File)
        {
            DataPackageOperation supported = DeskBoxDragData.GetFileTransferOperations(
                dataView,
                allowedOperations);
            bool noOperationMetadata = supported == DataPackageOperation.None;
            bool isInternal = DeskBoxDragData.IsInternalFileDrag(dataView);
            string[] sourcePaths = (isInternal
                    ? DeskBoxDragData.GetRawInternalSourcePaths(dataView)
                    : GetNativeFileDropPathHints())
                .Where(static path => !string.IsNullOrWhiteSpace(path))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            string destination = TransferIntentEngine.ResolveProspectiveManagedDestination(
                currentFolderPath: null,
                memberConfig.MappedFolderPath,
                SettingsService.Settings.FileWidget.DefaultManagedStorageRootPath);
            bool sameVolume = sourcePaths.Length == 0 ||
                (destination.Length > 0 &&
                 PathOriginClassifier.AreAllOnSameVolume(
                     sourcePaths,
                     destination,
                     TimeSpan.FromMilliseconds(80)));
            string action = SettingsService.Settings.FileWidget.ManagedDropAction;
            bool followWindows = string.Equals(
                action,
                Services.SettingsService.ManagedDropActionFollowWindows,
                StringComparison.Ordinal);
            SourceOriginSnapshot sourceOrigin = PathOriginClassifier.ClassifySources(
                sourcePaths,
                TimeSpan.FromMilliseconds(80));
            bool hasMapped = !string.IsNullOrWhiteSpace(destination);

            FileDropIntent intent = TransferIntentEngine.Evaluate(new TransferIntentRequest(
                Domain: isInternal ? TransferDomain.InternalDeskBox : TransferDomain.ExternalInbound,
                TargetKind: TransferTargetKind.DirectorySurface,
                HasFileData: true,
                HasVirtualFileData: false,
                HasShellApplicationOrNamespaceData: false,
                ForceCopy: false,
                HasMappedFolder: hasMapped,
                ControlDown: Win32Helper.IsKeyPressed(Windows.System.VirtualKey.Control),
                ShiftDown: Win32Helper.IsKeyPressed(Windows.System.VirtualKey.Shift),
                AltDown: Win32Helper.IsKeyPressed(Windows.System.VirtualKey.Menu),
                AllowedEffects: ToNativeAllowedEffects(supported),
                NoOperationMetadata: noOperationMetadata,
                DefaultMove: string.Equals(
                    action,
                    Services.SettingsService.ManagedDropActionMove,
                    StringComparison.Ordinal),
                FollowWindows: followWindows,
                SameVolume: sameVolume,
                CanLink: true,
                SourceOrigin: sourceOrigin)).Intent;

            DataPackageOperation resolvedOperation = intent switch
            {
                FileDropIntent.Copy => DataPackageOperation.Copy,
                FileDropIntent.Move => DataPackageOperation.Move,
                FileDropIntent.Shortcut or FileDropIntent.Reference =>
                    supported.HasFlag(DataPackageOperation.Link) || noOperationMetadata
                        ? DataPackageOperation.Link
                        : supported.HasFlag(DataPackageOperation.Copy)
                            ? DataPackageOperation.Copy
                            : supported.HasFlag(DataPackageOperation.Move)
                                ? DataPackageOperation.Move
                                : DataPackageOperation.None,
                _ => DataPackageOperation.None,
            };
            return DeskBoxDragData.ResolveFileDragFeedbackOperation(
                dataView,
                resolvedOperation,
                allowedOperations);
        }

        return DeskBoxDragData.GetFileAssociationOperation(dataView, allowedOperations);
    }

    private async Task HandleInactiveGroupTabRoutedDropAsync(string targetMemberId, DragEventArgs e)
    {
        DataPackageOperation allowedOperations = e.AllowedOperations;
        bool ctrlDown = Win32Helper.IsKeyPressed(Windows.System.VirtualKey.Control);
        bool shiftDown = Win32Helper.IsKeyPressed(Windows.System.VirtualKey.Shift);
        bool altDown = Win32Helper.IsKeyPressed(Windows.System.VirtualKey.Menu);
        e.Handled = true;
        MarkXamlDropClaimed(e.DataView);
        var deferral = e.GetDeferral();
        try
        {
            if (App.Current?.WidgetManager is not { } manager ||
                !await manager.SwitchWidgetGroupMemberAsync(
                    targetMemberId,
                    WidgetGroupSwitchOrigin.DragHover))
            {
                e.AcceptedOperation = DataPackageOperation.None;
                return;
            }

            if (CurrentContent is FileWidgetContentAdapter file)
            {
                using DroppedFileBatch batch = await DeskBoxDragData.TryGetDroppedFilesAsync(e.DataView);
                if (batch.Files.Count == 0)
                {
                    e.AcceptedOperation = DataPackageOperation.None;
                    return;
                }

                file.SetHostWindowHandle(HWnd);
                file.HostHasActiveShellDragWindowProbe = () => HasActiveNativeShellDragWindow;
                if (file.Surface is null && !IsCompactBoundsStateActive)
                {
                    EnsureCollapsedContentViewAttached();
                    ArmCollapsedContentHibernationIfSettled();
                }

                DataPackageOperation supported = DeskBoxDragData.GetFileTransferOperations(
                    e.DataView,
                    allowedOperations);
                uint effects = ToNativeAllowedEffects(supported);
                bool allTemp = batch.Files.All(static f => f.IsProviderTemporary);
                bool anyTemp = batch.Files.Any(static f => f.IsProviderTemporary);
                string[] paths = batch.Files.Select(static f => f.Path).ToArray();
                DroppedItemOriginKind[] origins = batch.Files.Select(static f => f.OriginKind).ToArray();
                FileDropIntent? forcedIntent = ResolveSnapshotForcedIntent(
                    ctrlDown,
                    shiftDown,
                    altDown,
                    effects,
                    allTemp,
                    paths);
                bool imported = await file.ImportNativeDroppedFilesAsync(
                    paths,
                    anyTemp,
                    copyWhenMapped: null,
                    targetItem: null,
                    forcedIntent: forcedIntent,
                    allowedEffects: effects,
                    itemOrigins: origins);
                bool isInternalDrag = DeskBoxDragData.IsInternalFileDrag(e.DataView);
                int nonTempCount = batch.Files.Count(static f => !f.IsProviderTemporary);
                DataPackageOperation pendingOp = imported
                    ? ResolvePendingGroupTabDragOperation(targetMemberId, e.DataView, allowedOperations)
                    : DataPackageOperation.None;
                e.AcceptedOperation = imported
                    ? FileSurfaceContent.ResolveSafeDropCompletionOperation(
                        pendingOp,
                        isInternalDrag,
                        requestedMoveCount: nonTempCount,
                        completedMoveCount: nonTempCount,
                        canReportCopy: allowedOperations.HasFlag(DataPackageOperation.Copy))
                    : DataPackageOperation.None;
                e.AcceptedOperation = DeskBoxDragData.ResolveFileDragFeedbackOperation(
                    e.DataView,
                    e.AcceptedOperation);
                return;
            }

            if (CurrentContent is TodoWidgetContentAdapter todo)
            {
                e.AcceptedOperation = await todo.ImportExternalDropAsync(e.DataView)
                    ? DeskBoxDragData.HasDroppedFiles(e.DataView)
                        ? DeskBoxDragData.GetFileAssociationOperation(e.DataView, allowedOperations)
                        : DataPackageOperation.Copy
                    : DataPackageOperation.None;
                return;
            }

            if (CurrentContent is CustomWidgetContentAdapter custom)
            {
                e.AcceptedOperation = await custom.ImportExternalDropAsync(e.DataView)
                    ? DeskBoxDragData.GetFileAssociationOperation(e.DataView, allowedOperations)
                    : DataPackageOperation.None;
                return;
            }

            if (CurrentContent is ClipboardWidgetContentAdapter clipboard)
            {
                e.AcceptedOperation = await clipboard.ImportExternalDropAsync(e.DataView)
                    ? DeskBoxDragData.HasDroppedFiles(e.DataView)
                        ? DeskBoxDragData.GetFileAssociationOperation(e.DataView, allowedOperations)
                        : DataPackageOperation.Copy
                    : DataPackageOperation.None;
                return;
            }

            if (CurrentContent is QuickCaptureWidgetContentAdapter quickCapture)
            {
                e.AcceptedOperation = await quickCapture.ImportExternalDropAsync(e.DataView)
                    ? DeskBoxDragData.HasDroppedFiles(e.DataView)
                        ? DeskBoxDragData.GetFileAssociationOperation(e.DataView, allowedOperations)
                        : DataPackageOperation.Copy
                    : DataPackageOperation.None;
                return;
            }

            e.AcceptedOperation = DataPackageOperation.None;
        }
        finally
        {
            deferral.Complete();
        }
    }

    private async void RootGrid_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (CurrentContent is FileWidgetContentAdapter fileSurface &&
            await fileSurface.TryHandleClipboardShortcutAsync(e))
        {
            return;
        }

        if (e.Key != Windows.System.VirtualKey.Tab ||
            !Win32Helper.IsKeyPressed(Windows.System.VirtualKey.Control))
        {
            return;
        }

        if (_groupKeyboardTabGestureActive ||
            DateTimeOffset.UtcNow - _lastGroupKeyboardSwitchAt <
            GroupKeyboardSwitchCooldown)
        {
            e.Handled = true;
            return;
        }

        if (ContentWidgetShell.TryHandleGroupKeyboardNavigation(e))
        {
            _groupKeyboardTabGestureActive = true;
            _lastGroupKeyboardSwitchAt = DateTimeOffset.UtcNow;
            e.Handled = true;
        }
    }

    private void RootGrid_PreviewKeyUp(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Tab)
        {
            _groupKeyboardTabGestureActive = false;
        }
    }

    private void RootGrid_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Handled || e.OriginalSource is DependencyObject source && HasAncestorOfType<TextBox>(source))
        {
            return;
        }

        if (TryHandleCompactActivation(e))
        {
            return;
        }

        if (e.Key == Windows.System.VirtualKey.Escape && TryHandleCompactEscape())
        {
            e.Handled = true;
        }
    }

    private void TitleBarGrid_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (IsGroupNavigationInput(e.OriginalSource))
        {
            return;
        }
        var properties = e.GetCurrentPoint(ContentWidgetShell.TitleBar).Properties;
        if (!properties.IsLeftButtonPressed) return;
        if (ContentWidgetShell.TitleEditorContent is TextBox &&
            ShouldOpenTitleBarFlyout(e.OriginalSource))
        {
            _ = CommitTitleRenameAsync();
            e.Handled = true;
            return;
        }

        if (ShouldOpenTitleBarFlyout(e.OriginalSource) &&
            !Win32Helper.IsKeyPressed(Windows.System.VirtualKey.Control))
        {
            // Raising the whole group (band ordering + foreground) is window
            // surgery and must not run on the pointer-pressed callback frame
            // (drag-freeze family). The deferral sits at this call site only:
            // the manager's raise sequence itself stays atomic so the reassert
            // path never splits across turns. The foreground snapshot guard
            // drops the raise if the user moved focus elsewhere during the
            // deferred turn; the drag guard skips it when the press turned
            // into a drag before the unit landed.
            IntPtr foregroundAtEnqueue = Win32Helper.GetForegroundWindow();
            IntPtr windowHandle = HWnd;
            UiDispatch.PostAfterCallback(() =>
            {
                // The drag guard gates on real movement: IsDragging is set
                // synchronously by BeginWindowDragCore later in THIS press
                // dispatch, so a bare check would drop the raise forever.
                if (!Win32Helper.IsWindowInCurrentProcess(windowHandle) ||
                    (IsDragging && HasMovedTitleBarDrag))
                {
                    return;
                }

                IntPtr foregroundNow = Win32Helper.GetForegroundWindow();
                if (foregroundNow != windowHandle &&
                    foregroundNow != foregroundAtEnqueue)
                {
                    App.LogVerbose(
                        "[ZOrder] Deferred title-bar group raise dropped: " +
                        "foreground moved on");
                    return;
                }

                if (WidgetLayerService.UsesDynamicGroupClickRaiseMode())
                {
                    App.Current.WidgetManager?.ActivateAllVisibleWidgetsFromTitle(
                        windowHandle);
                }
                else if (!WidgetLayerService.UsesDesktopPinnedMode() &&
                         !WidgetLayerService.UsesQuickRevealMode())
                {
                    App.Current.WidgetManager?.RebedDynamicPassiveSiblingsAfterSingleActivation(
                        windowHandle,
                        "title-single-press");
                }
            });
        }
        if (_config.IsPositionLocked || IsCompactTransitionActive) return;
        BeginWindowDragCore(e, ContentWidgetShell.TitleBar);
    }

    private bool ShouldOpenTitleBarFlyout(object? originalSource)
    {
        if (originalSource is not DependencyObject source)
        {
            return true;
        }

        return !IsGroupNavigationInput(source) &&
               !IsWithin(source, ContentWidgetShell.PositionLockActionButton) &&
               !IsWithin(source, ContentWidgetShell.SizeLockActionButton) &&
               !IsWithin(source, ContentWidgetShell.AddActionButton) &&
               !IsWithin(source, ContentWidgetShell.MoreActionButton) &&
               !IsWithin(source, ContentWidgetShell.CloseActionButton) &&
               !HasAncestorOfType<TextBox>(source);
    }

    private bool IsGroupNavigationInput(object? source) =>
        source is DependencyObject element &&
        ContentWidgetShell.IsGroupNavigationInteractiveSource(element);

    private static bool IsWithin(DependencyObject source, DependencyObject target)
    {
        for (DependencyObject? current = source; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (ReferenceEquals(current, target))
            {
                return true;
            }
        }

        return false;
    }

    private static bool HasAncestorOfType<T>(DependencyObject source) where T : DependencyObject
    {
        for (DependencyObject? current = source; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (current is T)
            {
                return true;
            }
        }

        return false;
    }

    private void TitleBarGrid_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        ContinueWindowDragCore(e);
    }

    private void TitleBarGrid_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        EndWindowDragCore(e);
        IntPtr foreground = Win32Helper.GetForegroundWindow();
        if (foreground != IntPtr.Zero && !App.Current.IsDeskBoxWindow(foreground))
        {
            App.Current.WidgetManager?.RestoreTemporarilyRaisedWidgetsToDesktopLayer(
                "content-title-released");
        }
    }

    private void DragHandle_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_config.IsPositionLocked) return;
        var properties = e.GetCurrentPoint(ContentWidgetShell.DragHandleElement).Properties;
        if (!properties.IsLeftButtonPressed) return;
        BeginWindowDragCore(e, ContentWidgetShell.DragHandleElement);
    }

    private void DragHandle_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        ContinueWindowDragCore(e);
    }

    private void DragHandle_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        EndWindowDragCore(e);
    }

    private void TitleBarGrid_PointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        DragPointerCaptureLostCore(sender, e);
    }

    private void DragHandle_PointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        DragPointerCaptureLostCore(sender, e);
    }

    protected override void OnDragEnd(bool hasMoved)
    {
        if (RestoreDesktopLayerWhenIdle)
        {
            RestoreDesktopLayer();
        }
    }

    // ── Resize handlers (delegate to base) ─────────────────────

    private void ResizeBorder_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        ResizeBorder_PointerPressedCore(sender, e);
    }

    private void ResizeBorder_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        ResizeBorder_PointerMovedCore(sender, e);
    }

    private void ResizeBorder_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        ResizeBorder_PointerReleasedCore(sender, e);
    }

    private void ResizeBorder_PointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        ResizeBorder_PointerCaptureLostCore(sender, e);
    }

    protected override void OnResizeEnd()
    {
        UpdateResizeChromeForCurrentState();
        if (CurrentContent is IWidgetInteractiveResizeContent resizeContent)
        {
            double titleHeight = Math.Max(0, ContentWidgetShell.ActualTitleBarHeight);
            resizeContent.CompleteInteractiveResize(
                Math.Max(1, RootGrid.ActualWidth),
                Math.Max(1, RootGrid.ActualHeight - titleHeight));
        }

        if (RestoreDesktopLayerWhenIdle)
        {
            RestoreDesktopLayer();
        }
    }

    private void ResizeBorder_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not UIElement element)
        {
            return;
        }

        var shape = element is FrameworkElement frameworkElement
            ? GetResizeCursorShapeForCurrentState(frameworkElement.Tag as string)
            : InputSystemCursorShape.Arrow;

        UiElementCursorHelper.SetCursor(element, shape);
    }

    // ── Activation ─────────────────────────────────────────────

    private void ContentWidgetWindow_Activated(object sender, WindowActivatedEventArgs args)
    {
        if (args.WindowActivationState == WindowActivationState.Deactivated)
        {
            _groupKeyboardTabGestureActive = false;
            _contentHost.OnDeactivated();
            if (Visible && !IsAtDesktopLayer &&
                !IsRaisedFromManager &&
                App.Current.WidgetManager is not { WidgetsRaisedFromTray: true } &&
                (DateTime.UtcNow - LastElevateForInteractionUtc).TotalMilliseconds > 300)
            {
                App.Log($"[ZOrder] Content Deactivated→QueueRestore hwnd=0x{HWnd.ToInt64():X}");
                QueueRestoreDesktopLayerIfForegroundLeavesDeskBox();
            }
            return;
        }

        _contentHost.OnActivated();
        // A long-hidden release dropped the decoded custom background; the
        // surface is cheap to rebuild from disk on the way back in.
        RecoverCustomBackgroundIfReleased();
        // Native activation (a body click on a resting widget) lifts the
        // window without going through any raise path, so IsAtDesktopLayer
        // never flipped and the Deactivated funnel above stayed blind — the
        // window then floats above foreign apps with no owner for its
        // restore. Every explicit raise path has already flipped the flag
        // by now; the desktop-pinned band keeps its own activation funnels.
        bool activatedFromRestingDynamicLayer =
            IsAtDesktopLayer &&
            !WidgetLayerService.UsesDesktopPinnedMode() &&
            !WidgetLayerService.UsesQuickRevealMode();
        if (IsAtDesktopLayer && !WidgetLayerService.UsesDesktopPinnedMode())
        {
            IsAtDesktopLayer = false;
        }
        // Deliberately synchronous (callback-isolation exemption): deferring
        // this reassert one dispatcher turn turns its SetForegroundWindow
        // (active) from a no-op — the window is still the foreground when the
        // handler runs — into a REAL foreground switch against whichever
        // sibling the raise batch activated next. Each switch raises another
        // Activated event, whose deferred reassert switches again: a 3ms
        // activation ping-pong ring across the whole group (2026-10-15
        // incident, 1805 band-split transactions during one raise). The
        // manager's own guards (session flag, foreground ownership, the
        // 400ms reassert suppression) bound the synchronous form.
        App.Current.WidgetManager?.ReassertRaisedWidgetGroupAfterDeskBoxActivation(
            HWnd,
            "content-window-activated");
        if ((activatedFromRestingDynamicLayer ||
                WidgetLayerService.UsesDynamicGroupClickRaiseMode()) &&
            App.Current.WidgetManager is not { WidgetsRaisedFromTray: true })
        {
            DeferDynamicRestingClickRaiseScope();
        }
    }

    private void QueueRestoreDesktopLayerIfForegroundLeavesDeskBox()
    {
        DispatcherQueue.TryEnqueue(async () =>
        {
            await Task.Delay(80);
            if (!Visible ||
                IsAtDesktopLayer ||
                IsRaisedFromManager ||
                ShouldDeferDesktopLayerRestore() ||
                (DateTime.UtcNow - LastElevateForInteractionUtc).TotalMilliseconds <= 300)
            {
                return;
            }

            IntPtr foregroundWindow = Win32Helper.GetForegroundWindow();
            if (foregroundWindow == IntPtr.Zero ||
                App.Current.IsDeskBoxOrTextInputExperienceWindow(foregroundWindow))
            {
                // Zero means activation is mid-transition (owned popover
                // hand-off); do not read the gap as a DeskBox leave.
                RestoreDesktopLayerWhenIdle = false;
                return;
            }

            RestoreDesktopLayerWhenIdle = true;
            // Raised-session holds are decided inline: while the tray session
            // is alive its restore monitor owns the layer; otherwise this
            // window restores itself.
            if (App.Current.WidgetManager is not { WidgetsRaisedFromTray: true })
            {
                if (App.Current.WidgetManager is { HasActiveTemporaryRaiseLease: true } manager &&
                    manager.RestoreTemporarilyRaisedWidgetsToDesktopLayer("deskbox-leave"))
                {
                    return;
                }

                RestoreDesktopLayer(force: true);
            }
        });
    }

    // ── Tray animation ─────────────────────────────────────────

    private void ShowWithoutActivation(bool persistVisibility)
    {
        Visible = true;
        AppWindow.Show();
        Win32Helper.ShowWindow(HWnd, Win32Helper.SW_SHOWNOACTIVATE);
        UpdatePersistedVisibility(isVisible: true, persistVisibility);

        ApplyBackdropPreference();
        NotifyCompactHostVisibilityChanged(true);
        QueueVisibleContentResume();
    }

    private void QueueVisibleContentResume()
    {
        int generation = ++_contentVisibilityGeneration;
        if (!Visible || generation != _contentVisibilityGeneration || IsClosing)
        {
            return;
        }

        _contentHost.OnWindowVisibilityChanged(true);
        PerformanceLogger.Mark(
            "WidgetVisibleContentFirstFrameReady",
            $"kind={_config.WidgetKind} id={_config.Id}");
    }

    private void NotifyVisibleContentRevealCompleted()
    {
        if (!Visible || IsClosing)
        {
            return;
        }

        int generation = _contentVisibilityGeneration;
        if (_queuedContentRevealGeneration == generation)
        {
            return;
        }

        _queuedContentRevealGeneration = generation;
        if (!DispatcherQueue.TryEnqueue(async () =>
            {
                await Task.Delay(RevealCompletedBackgroundDelayMs);
                if (!Visible ||
                    IsClosing ||
                    generation != _contentVisibilityGeneration)
                {
                    return;
                }

                _contentHost.OnWindowRevealCompleted();
                PerformanceLogger.Mark(
                    "WidgetVisibleContentResumed",
                    $"kind={_config.WidgetKind} id={_config.Id}");
            }))
        {
            _queuedContentRevealGeneration = -1;
        }
    }

    private void NotifyVisibleContentSuspended()
    {
        if (HWnd != IntPtr.Zero)
        {
            WidgetEditorPopoverWindow.ClosePopoversForOwner(HWnd);
        }

        _contentVisibilityGeneration++;
        _contentHost.OnWindowVisibilityChanged(false);
    }

    internal void SuspendContentForMonitorOcclusion()
    {
        NotifyVisibleContentSuspended();
    }

    internal void ResumeContentFromMonitorOcclusion()
    {
        if (!Visible || IsClosing)
        {
            return;
        }

        QueueVisibleContentResume();
        _contentHost.OnWindowRevealCompleted();
    }

    private void UpdatePersistedVisibility(bool isVisible, bool persistVisibility)
    {
        _config.IsVisible = isVisible;
        bool groupVisibilityHandled =
            App.Current?.WidgetManager?.SetWidgetGroupVisibility(
                _config,
                isVisible,
                persistVisibility) == true;
        if (persistVisibility && !groupVisibilityHandled)
        {
            SettingsService.SaveDebounced();
        }
    }

    private void DeferDynamicRestingClickRaiseScope()
    {
        IntPtr activatedHandle = HWnd;
        UiDispatch.PostAfterCallback(() =>
        {
            if (!Win32Helper.IsWindowInCurrentProcess(activatedHandle) ||
                (IsDragging && HasMovedTitleBarDrag) ||
                App.Current.WidgetManager is { WidgetsRaisedFromTray: true })
            {
                return;
            }

            IntPtr foregroundNow = Win32Helper.GetForegroundWindow();
            if (!WidgetLayerService.IsWindowOrOwnedByWidget(foregroundNow, activatedHandle) &&
                !OwnsTransientForegroundWindow(foregroundNow))
            {
                return;
            }

            if (WidgetLayerService.UsesDynamicGroupClickRaiseMode())
            {
                App.Current.WidgetManager?.PromotePeerWidgetsAfterDynamicBodyActivation(
                    activatedHandle);
            }
            else
            {
                App.Current.WidgetManager?.RebedDynamicPassiveSiblingsAfterSingleActivation(
                    activatedHandle,
                    "content-body-single-activated");
            }
        });
    }

    private void PushToBottom(bool showWindow = true)
    {
        IsRaisedFromManager = false;
        IsAtDesktopLayer = true;
        WidgetLayerService.MoveToDesktopBottom(HWnd, showWindow);
        App.LogVerbose($"[ZOrder] Content PushToBottom hwnd=0x{HWnd.ToInt64():X}");
        App.Current.WidgetManager?.QueueIdleWidgetZOrderNormalization(
            "content-pushed-to-desktop");
    }

    internal void NotifyTransientPopupDismissedToExternalForeground()
    {
        if (Visible &&
            !IsAtDesktopLayer &&
            !IsRaisedFromManager &&
            App.Current.WidgetManager is not { WidgetsRaisedFromTray: true })
        {
            App.Log($"[ZOrder] Content PopupDismissed→QueueRestore hwnd=0x{HWnd.ToInt64():X}");
            QueueRestoreDesktopLayerIfForegroundLeavesDeskBox();
        }
    }
}
