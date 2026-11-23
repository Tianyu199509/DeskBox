using DeskBox.Helpers;
using DeskBox.Models;
using DeskBox.Platform;
using DeskBox.Platform.DragDrop;
using DeskBox.Services;
using DeskBox.Services.DragDrop;
using DeskBox.ViewModels;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.Storage;

namespace DeskBox.Controls.WidgetContents;

public sealed partial class FileSurfaceContent
{
    private static bool IsWin10NativeShellDragActive =>
        ActiveDeskBoxDragRegistry.HasActiveNativeShellDrag;

    /// <summary>
    /// Win11 WinRT XAML drags draw their own XAML drag preview, so suppressing
    /// <c>IDropTargetHelper</c> avoids double visuals.
    /// </summary>
    internal static bool ShouldSuppressNativeShellDragVisual(
        bool isWin10NativeShellDragActive,
        bool hasActiveDragSession,
        bool isSurfaceReorderDragActive,
        bool isStackPopoverDragActive,
        bool isDeskBoxFileDragSnapshot,
        bool hasActiveProcessDeskBoxDrag = false) =>
        !isWin10NativeShellDragActive &&
        (hasActiveDragSession ||
         hasActiveProcessDeskBoxDrag ||
         isSurfaceReorderDragActive ||
         isStackPopoverDragActive ||
         isDeskBoxFileDragSnapshot);

    private static bool IsBlockedPointerDragOrigin(PointerRoutedEventArgs e) =>
        e.OriginalSource is DependencyObject source &&
        (FileItemSelectionGeometry.HasAncestor<ScrollBar>(source) ||
         FileItemSelectionGeometry.HasAncestor<ButtonBase>(source) ||
         FileItemSelectionGeometry.HasAncestor<TextBox>(source));

    private bool HandleExternalDragOutObservationAndWin10Retry(
        bool isWindows11OrLater,
        string dragOutAction,
        DataPackageOperation allowedOperations,
        DataPackageOperation dropResult,
        bool handledAsStackMembership,
        bool claimedByOtherDeskBoxWidget,
        bool win10MoveGateBlocked,
        DropReleaseSnapshot releaseSnapshot,
        ExternalDragObservation observation,
        string[] movedPaths)
    {
        bool isWin10MoveNonExplorerDrop = !claimedByOtherDeskBoxWidget &&
            (ShouldMonitorWin10MoveNonExplorerDrop(
                 isWindows11OrLater,
                 dragOutAction,
                 allowedOperations,
                 handledAsStackMembership,
                 releaseSnapshot) ||
             (win10MoveGateBlocked &&
              !isWindows11OrLater &&
              !handledAsStackMembership &&
              !releaseSnapshot.EscapePressed &&
              releaseSnapshot.TargetKind != DropTargetWindowKind.DeskBoxOwnWindow));

        bool win10MoveRejected = dropResult == DataPackageOperation.None || win10MoveGateBlocked;
        bool rejectedByNonExplorer = isWin10MoveNonExplorerDrop && win10MoveRejected;
        if (rejectedByNonExplorer && movedPaths.Length > 0)
        {
            // Arm a 15-second one-shot Copy retry for this exact file batch so
            // re-dragging immediately drops into copy-only targets (browsers,
            // third-party apps, or Explorer-hosted #32770 dialogs) without
            // changing the user's Move preference for Desktop/Explorer.
            Win10MoveCopyFallbackPolicy.Shared.ArmForRejectedPaths(movedPaths);
        }
        else if (dropResult != DataPackageOperation.None)
        {
            Win10MoveCopyFallbackPolicy.Shared.Clear();
        }

        // When our Win10 Move Target Gate already withheld the file payload and
        // the target returned None, the target never received the files: show the
        // retry hint immediately and do NOT spawn a 10-minute removal watcher
        // that would fire a false warning if the user moves the file later.
        if (win10MoveGateBlocked && dropResult == DataPackageOperation.None)
        {
            if (rejectedByNonExplorer)
            {
                ShowWin10MoveRejectedByAppHint();
            }

            return rejectedByNonExplorer;
        }

        if (observation != ExternalDragObservation.None && movedPaths.Length > 0)
        {
            DropTargetWindowKind releaseTargetKind = releaseSnapshot.TargetKind;
            Action? onSourceRemoved = isWin10MoveNonExplorerDrop
                ? () =>
                {
                    Win10MoveCopyFallbackPolicy.Shared.Clear();
                    ShowWin10MoveNonExplorerRemovalWarning();
                }
                : dropResult == DataPackageOperation.None
                    ? () => MaybeShowDragOutResultHint(
                        DataPackageOperation.Move,
                        allowedOperations,
                        observation,
                        handledAsStackMembership: false,
                        releaseTargetKind,
                        win10MoveRejectedByNonExplorer: false)
                    : null;
            Action? onFirstProbeRetained = rejectedByNonExplorer
                ? ShowWin10MoveRejectedByAppHint
                : null;
            bool forceBrief = observation == ExternalDragObservation.Brief || win10MoveGateBlocked;
            _ = ObserveExternalDragOutAsync(
                movedPaths,
                _lifetimeCancellation.Token,
                brief: forceBrief,
                onSourceRemoved: onSourceRemoved,
                onFirstProbeRetained: onFirstProbeRetained);
        }
        else if (rejectedByNonExplorer)
        {
            ShowWin10MoveRejectedByAppHint();
        }

        return rejectedByNonExplorer;
    }

    private static WidgetItem[] ExpandExistingStackMembers(WidgetStackItem stack)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var members = new List<WidgetItem>(stack.Members.Count);
        foreach (WidgetItem member in stack.Members)
        {
            if (member is WidgetStackItem || string.IsNullOrWhiteSpace(member.Path))
            {
                continue;
            }

            if (!DesktopOutboundDropCoordinator.IsFastNetworkCandidatePath(member.Path) &&
                !File.Exists(member.Path) &&
                !Directory.Exists(member.Path))
            {
                continue;
            }

            string fullPath;
            try
            {
                fullPath = Path.GetFullPath(member.Path);
            }
            catch
            {
                continue;
            }

            if (seen.Add(fullPath))
            {
                members.Add(member);
            }
        }

        return [.. members];
    }

    /// <summary>
    /// D6: Prepares a collapsed stack tile drag in WinUI XAML mode so same-widget
    /// reorder keeps <c>StackReorderKeyProperty</c> while cross-widget, folder, and
    /// Explorer/Desktop drops carry all stack member files natively.
    /// </summary>
    private void PrepareCollapsedStackXamlDrag(
        DragStartingEventArgs e,
        WidgetStackItem stack,
        Action<string> cancelDrag)
    {
        WidgetItem[] memberItems = ExpandExistingStackMembers(stack);
        string[] memberPaths = memberItems
            .Select(static item => Path.GetFullPath(item.Path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (memberPaths.Length > 0 && HasActiveTransferSource(memberPaths))
        {
            _activeDragSessionId = null;
            ShowTransferBlockedFeedback(
                _fileService.TransferSessions.GetState(memberPaths[0]));
            cancelDrag("stack-active-transfer-source");
            return;
        }

        _stackPointerDragStarted = true;
        e.Data.Properties[DeskBoxDragData.SourceWidgetIdProperty] = WidgetId;
        e.Data.Properties[DeskBoxDragData.InternalFileDragTokenProperty] =
            DeskBoxDragData.InternalFileDragToken;
        e.Data.Properties[DeskBoxDragData.StackReorderKeyProperty] = stack.StackKey;
        e.Data.Properties.Title = stack.Name;

        FileItemDragPackageResult result = default;
        bool preparedFiles = memberItems.Length > 0 &&
            FileItemDragPackage.TryPrepare(
                e.Data,
                memberItems,
                WidgetId,
                paths => _fileService.GetStorageItems(paths),
                paths => paths.Count == 1 ? Path.GetFileName(paths[0]) : stack.Name,
                out result);

        if (preparedFiles)
        {
            try
            {
                _activeDragSourcePaths = result.SourcePaths.ToArray();
                _activeDragHasStorageItems = result.HasStorageItems;
                bool isProtectedCopyOnly = FileItemDragPackage.IsProtectedCopyOnlySource(result) ||
                    (!WindowsCompatibilityService.IsWindows11OrLater && !result.UsesNativeShellDataObject);
                bool isDefaultCopySpecialMedia = FileItemDragPackage.IsDefaultCopySpecialMediaSource(result);
                string managedDragOutAction = _settingsService.Settings.FileWidget.ManagedDragOutAction;
                bool isWin10MoveMode = !WindowsCompatibilityService.IsWindows11OrLater &&
                    string.Equals(managedDragOutAction, SettingsService.ManagedDragOutActionMove, StringComparison.Ordinal);
                bool win10CtrlCopyOverride = isWin10MoveMode &&
                    Win32Helper.IsKeyPressed(Windows.System.VirtualKey.Control);
                bool win10OneShotCopyFallback = isWin10MoveMode &&
                    !isProtectedCopyOnly &&
                    !isDefaultCopySpecialMedia &&
                    !win10CtrlCopyOverride &&
                    Win10MoveCopyFallbackPolicy.Shared.TryConsumeForPaths(result.SourcePaths);
                e.AllowedOperations = FileItemDragPackage.ResolveDragOutAllowedOperations(
                    managedDragOutAction,
                    sourcePaths: result.SourcePaths,
                    isProtectedCopyOnly: isProtectedCopyOnly,
                    controlPressed: win10CtrlCopyOverride,
                    oneShotCopyFallback: win10OneShotCopyFallback,
                    isDefaultCopySpecialMedia: isDefaultCopySpecialMedia);
                e.Data.RequestedOperation = FileItemDragPackage.ResolveDragOutPreferredOperation(
                    managedDragOutAction,
                    sourcePaths: result.SourcePaths,
                    isProtectedCopyOnly: isProtectedCopyOnly,
                    isDefaultCopySpecialMedia: isDefaultCopySpecialMedia,
                    controlPressed: win10CtrlCopyOverride,
                    oneShotCopyFallback: win10OneShotCopyFallback);
                _activeDragAllowedOperations = e.AllowedOperations;
                _activeDragContext = new DragSessionContext(
                    _activeDragSessionId!,
                    _activeDragSourcePaths,
                    _activeDragHasStorageItems,
                    _activeDragAllowedOperations,
                    FromStackPopover: false);
                _sourceDragSession.Begin(_activeDragSessionId!);
                ArmOutboundModifierTipWatch(e.AllowedOperations, isPlatformDragSession: true);
                ActiveDeskBoxDragRegistry.Begin(
                    _activeDragSessionId!,
                    WidgetId,
                    result.SourcePaths,
                    fromStackPopover: false,
                    physicalPaths: result.PhysicalPaths,
                    stackReorderKey: stack.StackKey);
                e.DragUI.SetContentFromDataPackage();
                FileDragSourceGuardDataObject.ConfigureActiveWin10MoveTargetGate(
                    !WindowsCompatibilityService.IsWindows11OrLater &&
                    e.AllowedOperations == DataPackageOperation.Move);
            }
            catch (Exception ex)
            {
                App.Log($"[DragProtocol] stage=StackPackageSetupFailed widget={WidgetId}: {ex.Message}");
                FileDragSourceGuardDataObject.ClearActiveManagedRoot();
                if (_activeDragSessionId is not null)
                {
                    ActiveDeskBoxDragRegistry.End(_activeDragSessionId);
                }
                e.Cancel = true;
                return;
            }
        }
        else
        {
            e.Data.RequestedOperation = DataPackageOperation.Link;
            e.AllowedOperations = DataPackageOperation.Link;
            _sourceDragSession.Begin(_activeDragSessionId!);
        }

        App.Log(
            $"[DragProtocol] stage=PackagePrepared widget={WidgetId} " +
            $"session={FormatDragSessionId(_activeDragSessionId)} " +
            $"kind=stack members={memberPaths.Length} preparedFiles={preparedFiles} " +
            $"requested={e.Data.RequestedOperation} allowed={e.AllowedOperations}");
    }

    /// <summary>
    /// Handles same-widget drops originating from Win10 native <c>SHDoDragDrop</c>
    /// (where OLE delivers <c>Drop</c> to <see cref="NativeDropTarget"/> rather than
    /// WinUI routed XAML <c>Drop</c>): collapsed stack reorder, stack-popover member
    /// extraction to main surface, and same-widget item reorder.
    /// </summary>
    private bool TryCommitNativeSameWidgetArrangement(
        IReadOnlyList<DroppedFilePath> droppedFiles,
        WidgetItem? targetItem,
        int? preferredManualIndex,
        int? screenX,
        int? screenY,
        ActiveDeskBoxDragRegistry.MatchedDragEntry? capturedSelfDrag = null)
    {
        if (targetItem is not null || droppedFiles.Count == 0)
        {
            return false;
        }

        string[] paths = droppedFiles.Select(static f => f.Path).ToArray();
        ActiveDeskBoxDragRegistry.MatchedDragEntry matched;
        if (capturedSelfDrag is { } preCaptured)
        {
            matched = preCaptured;
        }
        else if (!ActiveDeskBoxDragRegistry.TryMatchDetailed(paths, out matched))
        {
            return false;
        }

        if (!matched.IsNativeShellDrag ||
            !string.Equals(matched.WidgetId, WidgetId, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        _activeDragHandledAsStackMembership = true;
        ListViewBase activeView = GetActiveItemsView();
        Point dropPosition = screenX.HasValue &&
            screenY.HasValue &&
            TryGetScreenPointInElement(activeView, screenX.Value, screenY.Value, out Point pt)
            ? pt
            : default;

        if (matched.FromStackPopover &&
            !string.IsNullOrWhiteSpace(matched.SourceStackKey) &&
            TryGetStackPopoverDragItems(
                matched.SourceStackKey,
                paths,
                out WidgetStackItem? sourceStack,
                out WidgetItem[] detachItems) &&
            detachItems.Length > 0)
        {
            int detachIndex = preferredManualIndex ?? ReorderDropIndexCalculator.Compute(
                activeView,
                dropPosition,
                _externalDropInsertionIndex);
            bool removed = DetachStackItemsToSurfaceIndex(
                sourceStack.StackKey,
                detachItems,
                detachIndex);
            PersistSurfaceReorder();
            ResetExternalDropPreview();
            if (removed)
            {
                CloseStackPopover();
            }

            return removed;
        }

        _surfaceReorderStackKey = matched.StackReorderKey;
        HandleSurfaceFinalReorder(paths, dropPosition);
        PersistSurfaceReorder();
        ResetExternalDropPreview();
        return true;
    }

    /// <summary>
    /// D7: Synchronous native OLE launch drop handler for the Stack Popover's
    /// <see cref="NativeDropTarget"/>, mirroring <c>ContentWidgetWindow.NativeDragDrop</c>
    /// and using 24-hour / next-startup retention instead of a 20-second hard delete.
    /// </summary>
    private ShellDropLaunchResult HandleStackPopoverNativeLaunchDrop(
        NativeDropLaunchRequest request)
    {
        if (_isDisposed ||
            request.RightButtonDrag ||
            _stackPopoverNativeLaunchTarget is not { Path.Length: > 0 } launchTarget)
        {
            return ShellDropLaunchResult.NotAttempted;
        }

        if (ActiveDeskBoxDragRegistry.TryMatchDetailed(
                request.Paths,
                out ActiveDeskBoxDragRegistry.MatchedDragEntry matched))
        {
            // Keep same-popover member drags on reorder and never launch whole
            // collapsed stacks; allow Win10 native-shell non-popover drags onto
            // an executable tile inside the popover.
            if (matched.FromStackPopover ||
                !matched.IsNativeShellDrag ||
                !string.IsNullOrWhiteSpace(matched.StackReorderKey))
            {
                return ShellDropLaunchResult.NotAttempted;
            }

            if (ShortcutLaunchPolicy.EvaluateInternalDrag(
                    isDeskBoxFileDrag: true,
                    isStackPopoverMemberDrag: false,
                    request.Paths,
                    launchTarget.Path,
                    targetHostedInStackPopover: true) != ShortcutLaunchDecision.Launch)
            {
                return ShellDropLaunchResult.NotAttempted;
            }

            ActiveDeskBoxDragRegistry.MarkClaimedByDeskBoxDropTarget(
                matched.SessionId,
                WidgetId);
        }

        if (WasLaunchConsumedRecently())
        {
            _stackPopoverNativeItemTarget = null;
            _stackPopoverNativeLaunchTarget = null;
            return ShellDropLaunchResult.AlreadyResolved;
        }

        string targetPath = launchTarget.Path;
        string appName = launchTarget.Name;
        IReadOnlyList<string> droppedPaths = request.Paths;
        bool containsTemp = request.ContainsTemporaryFiles;
        IntPtr ownerHwnd = ActiveStackPopoverWindowHandle != IntPtr.Zero
            ? ActiveStackPopoverWindowHandle
            : _hostWindowHandle;
        _stackPopoverNativeItemTarget = null;
        _stackPopoverNativeLaunchTarget = null;
        MarkNativeLaunchConsumed();
        bool closeAfterSpringDrop = ConsumeSpringLoadedPopoverDrop();
        ClearDragSessionVisualState();

        UiDispatch.PostAfterCallback(() =>
            _ = LaunchStackPopoverNativeDroppedFilesAsync(
                targetPath,
                appName,
                droppedPaths,
                containsTemp,
                ownerHwnd,
                closeAfterSpringDrop));
        return ShellDropLaunchResult.Launched(NativeDropEffectPolicy.Link, 0);
    }

    private async Task LaunchStackPopoverNativeDroppedFilesAsync(
        string targetPath,
        string applicationName,
        IReadOnlyList<string> droppedPaths,
        bool containsTemporaryFiles,
        IntPtr ownerHwnd,
        bool closeAfterSpringDrop)
    {
        if (_isDisposed)
        {
            return;
        }

        string[] existingPaths = await Task.Run(() => droppedPaths
            .Where(static p =>
                !string.IsNullOrWhiteSpace(p) &&
                (File.Exists(p) || Directory.Exists(p)))
            .ToArray());
        if (existingPaths.Length == 0)
        {
            if (!_isDisposed)
            {
                ShowShortcutLaunchRefusedFeedback(applicationName);
            }

            if (containsTemporaryFiles)
            {
                _ = Task.Run(() => NativeDropMaterializationWorker.CleanupVirtualDropDirectories(droppedPaths));
            }

            if (closeAfterSpringDrop && _stackPopoverPopupOpen)
            {
                HideStackPopoverForReuse(restoreOwnerForeground: false);
            }

            return;
        }

        ShellLaunchOutcome outcome = await ShortcutFileLauncher.LaunchWithFilesAsync(
            targetPath,
            existingPaths,
            ownerHwnd,
            _lifetimeCancellation.Token);
        if (outcome == ShellLaunchOutcome.Launched && containsTemporaryFiles)
        {
            _ = Task.Run(() => DropStagingFileOps.MarkStagingBatchLaunchedRetention(droppedPaths));
        }
        else if (containsTemporaryFiles)
        {
            _ = Task.Run(() => NativeDropMaterializationWorker.CleanupVirtualDropDirectories(droppedPaths));
        }

        if (outcome == ShellLaunchOutcome.Failed && !_isDisposed)
        {
            ShowShortcutLaunchRefusedFeedback(applicationName);
        }

        if (closeAfterSpringDrop && _stackPopoverPopupOpen)
        {
            HideStackPopoverForReuse(restoreOwnerForeground: false);
        }
    }

    private bool TryCommitWin10SamePopoverReorder(
        string stackKey,
        IReadOnlyList<string> paths,
        ActiveDeskBoxDragRegistry.MatchedDragEntry? capturedSelfDrag,
        int? preferredMemberIndex)
    {
        if (capturedSelfDrag is not { IsNativeShellDrag: true } ||
            preferredMemberIndex is not { } reorderTargetIndex ||
            !TryGetStackPopoverDragItems(
                stackKey,
                paths,
                out WidgetStackItem? sameStack,
                out WidgetItem[] reorderItems) ||
            reorderItems.Length == 0)
        {
            return false;
        }

        bool reordered = ReorderStackPopoverMembers(sameStack, reorderItems, reorderTargetIndex);
        if (!reordered)
        {
            QueueStackPopoverReconciliation();
        }

        PersistSurfaceReorder();
        return reordered;
    }

    private readonly List<string> _lastImportDestinationPaths = [];

    private WidgetItem[] SelectImportedStackItems(IReadOnlySet<string> preExistingNormalizedPaths) =>
        StackDropCommitHelper.SelectImportedItemsForStack(
            ViewModel.Items,
            preExistingNormalizedPaths,
            _lastImportDestinationPaths);

    private string SynchronizeResolvedStackKeyAndInsertMembers(
        string targetStackKey,
        WidgetItem[] committedItems,
        int? preferredStackMemberIndex,
        WidgetStackItem? fallbackStack = null)
    {
        string resolvedStackKey = StackDropCommitHelper.ResolveEffectiveStackKey(
            targetStackKey,
            ViewModel.LastResolvedStackKey);
        if (string.Equals(_stackPopoverKey, targetStackKey, StringComparison.Ordinal))
        {
            _stackPopoverKey = resolvedStackKey;
        }

        if (preferredStackMemberIndex is { } stackMemberIndex &&
            (ViewModel.FindStackByKey(resolvedStackKey) ?? fallbackStack) is { } updatedStack)
        {
            ApplyImportedStackMemberInsertion(
                updatedStack,
                committedItems,
                stackMemberIndex);
        }

        return resolvedStackKey;
    }

    private bool DetachStackItemsToSurfaceIndex(
        string sourceStackKey,
        WidgetItem[] detachItems,
        int detachInsertionIndex)
    {
        bool removed = false;
        ApplyStackProjectionChange(() =>
        {
            removed = ViewModel.RemoveItemsFromStack(
                sourceStackKey,
                detachItems);
            if (removed && detachInsertionIndex >= 0)
            {
                for (int i = 0; i < detachItems.Length; i++)
                {
                    ViewModel.MoveVisibleItemForReorder(
                        detachItems[i],
                        detachInsertionIndex + i);
                }
            }
        });
        return removed;
    }

    private void ShowCollapsedStackRecycleBinBlockedFeedback()
    {
        ShowFeedback(new WidgetFeedbackRequest(
            T("Widget.DropBlocked.CollapsedStackRecycleBin"),
            WidgetFeedbackSeverity.Warning,
            "collapsed-stack-recycle-bin-blocked"));
    }
}
