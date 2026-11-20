using DeskBox.Controls.WidgetContents;
using DeskBox.Helpers;
using DeskBox.Models;
using DeskBox.Platform;
using DeskBox.Services;
using DeskBox.ViewModels;

namespace DeskBox.Views;

public sealed partial class ContentWidgetWindow
{
    protected override bool SupportsCompactDropExpansion =>
        CurrentContent is FileWidgetContentAdapter or
            TodoWidgetContentAdapter or
            QuickCaptureWidgetContentAdapter or
            ClipboardWidgetContentAdapter ||
        (CurrentContent is CustomWidgetContentAdapter custom &&
         custom.ViewModel.AcceptsDrops) ||
        AnyGroupedMemberAcceptsFileDrops() ||
        base.SupportsCompactDropExpansion;

    private bool AnyGroupedMemberAcceptsFileDrops()
    {
        if (App.Current?.WidgetManager?.GetWidgetGroupPresentation(_config.Id) is not
            { Members.Count: > 1 } presentation)
        {
            return false;
        }

        foreach (var member in presentation.Members)
        {
            if (DoesGroupMemberAcceptFileDrop(member.WidgetId))
            {
                return true;
            }
        }

        return false;
    }

    internal bool CanObserveElevatedPointerDrag =>
        !IsClosing &&
        Visible &&
        ((!IsWidgetCollapsed && !IsCompactTransitionActive) ||
         SupportsCompactDropExpansion);

    internal string ObserveElevatedPointerDrag(
        int screenX,
        int screenY,
        IReadOnlyList<string> sourcePaths,
        LocalizationService localizationService,
        out bool canDrop,
        bool forceCopyUnlessExplicit = false)
    {
        canDrop = false;
        if (!CanObserveElevatedPointerDrag || sourcePaths.Count == 0)
        {
            return string.Empty;
        }

        if (IsWidgetCollapsed || IsCompactTransitionActive)
        {
            NotifyCompactDragEnteredFromHost();
        }

        ObserveNativeGroupTabDragHover(
            screenX,
            screenY,
            hasFileData: true,
            pointerGeneration: 0);

        if (HitTestInactiveGroupTitleMemberId(screenX, screenY) is { } inactiveMemberId)
        {
            if (!DoesGroupMemberAcceptFileDrop(inactiveMemberId))
            {
                canDrop = false;
                return string.Empty;
            }

            WidgetConfig? memberConfig = SettingsService.Settings.WidgetLayout.Widgets
                .FirstOrDefault(w => string.Equals(w.Id, inactiveMemberId, StringComparison.Ordinal));
            canDrop = true;
            return memberConfig?.Name ?? _config.Name;
        }

        if (CurrentContent is FileWidgetContentAdapter fileAdapter)
        {
            fileAdapter.SetHostWindowHandle(HWnd);
            fileAdapter.HostHasActiveShellDragWindowProbe = () => HasActiveNativeShellDragWindow;
            if (fileAdapter.Surface is null && !IsCompactBoundsStateActive)
            {
                EnsureCollapsedContentViewAttached();
                ArmCollapsedContentHibernationIfSettled();
            }

            if (fileAdapter.IsImportBusy)
            {
                return string.Empty;
            }

            WidgetItem? navFolderItem =
                fileAdapter.TryResolveFolderNavigationDropTarget(screenX, screenY);
            WidgetItem? hitItem = navFolderItem ??
                FindNativeDropDataContext<WidgetItem>(
                    screenX,
                    screenY);
            ItemDropBehavior hitBehavior = hitItem is not null
                ? ItemDropBehaviorPolicy.Resolve(hitItem)
                : ItemDropBehavior.None;

            if (hitItem is not null &&
                hitBehavior == ItemDropBehavior.Launch &&
                ShortcutLaunchPolicy.EvaluateInternalDrag(
                    isDeskBoxFileDrag: true,
                    isStackPopoverMemberDrag: false,
                    sourcePaths,
                    hitItem.Path) == ShortcutLaunchDecision.Launch &&
                IsNativeDropPointOverItemIcon(screenX, screenY, hitItem))
            {
                _nativeFileDropItemTarget = null;
                _nativeFileDropLaunchTarget = hitItem;
                fileAdapter.ObserveNativeDragPointer(
                    screenX,
                    screenY,
                    hasFileData: true,
                    pathHints: sourcePaths,
                    nativeTarget: null,
                    launchTarget: hitItem);
                canDrop = true;
                return localizationService.Format(
                    "Widget.DropOnShortcutOpenWith",
                    hitItem.Name);
            }

            WidgetItem? nativeTarget = hitBehavior is
                ItemDropBehavior.FolderImport or
                ItemDropBehavior.StackImport or
                ItemDropBehavior.RecycleBinDelete
                    ? hitItem
                    : null;
            _nativeFileDropLaunchTarget = null;
            _nativeFileDropItemTarget = nativeTarget;
            fileAdapter.ObserveNativeDragPointer(
                screenX,
                screenY,
                hasFileData: true,
                pathHints: sourcePaths,
                nativeTarget: nativeTarget,
                launchTarget: null);

            if (nativeTarget is WidgetStackItem targetStack)
            {
                string stackDest = fileAdapter.Surface?.ViewModel.CurrentFolderPath ??
                    fileAdapter.Surface?.ViewModel.MappedFolderPath ??
                    string.Empty;
                if (!string.IsNullOrWhiteSpace(stackDest) &&
                    FileService.IsLexicallyUnsafeDirectoryTransfer(sourcePaths, stackDest))
                {
                    canDrop = false;
                    return localizationService.T("Widget.Error.UnsafeFolderTransfer");
                }

                HashSet<string> stackMemberPaths = targetStack.Members
                    .Where(static m => !string.IsNullOrWhiteSpace(m.Path))
                    .Select(static m => Path.GetFullPath(m.Path))
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                if (!string.IsNullOrWhiteSpace(stackDest) &&
                    FileSurfaceContent.AreAllSourcesAlreadyInDestinationLexically(sourcePaths, stackDest) &&
                    sourcePaths.All(path => stackMemberPaths.Contains(Path.GetFullPath(path))))
                {
                    canDrop = false;
                    return localizationService.T("Widget.DragCaption.CurrentWidget");
                }

                FileDropIntent stackIntent = ResolveElevatedDropIntent(
                    sourcePaths,
                    stackDest,
                    hasMappedFolder: !string.IsNullOrWhiteSpace(stackDest),
                    forceCopyUnlessExplicit: forceCopyUnlessExplicit);
                if (stackIntent == FileDropIntent.None)
                {
                    canDrop = false;
                    return string.Empty;
                }

                canDrop = true;
                return stackIntent == FileDropIntent.Shortcut
                    ? FormatElevatedDropCaption(
                        localizationService,
                        FileDropIntent.Shortcut,
                        targetStack.Name)
                    : localizationService.Format(
                        "Widget.Stack.DragCaption.Add",
                        targetStack.Name);
            }

            if (nativeTarget is { } binTile &&
                hitBehavior == ItemDropBehavior.RecycleBinDelete)
            {
                canDrop = !sourcePaths.Contains(
                    binTile.Path,
                    StringComparer.OrdinalIgnoreCase);
                return canDrop
                    ? localizationService.T("Widget.DropOnRecycleBinHint")
                    : string.Empty;
            }

            if (nativeTarget is { } folder &&
                ItemDropBehaviorPolicy.TryGetFolderDropDestinationPath(
                    folder,
                    out string folderDestination))
            {
                if (FileService.IsLexicallyUnsafeDirectoryTransfer(sourcePaths, folderDestination))
                {
                    canDrop = false;
                    return localizationService.T("Widget.CannotMoveToFolder");
                }

                if (FileSurfaceContent.AreAllSourcesAlreadyInDestinationLexically(
                        sourcePaths,
                        folderDestination))
                {
                    canDrop = false;
                    return ReferenceEquals(folder, navFolderItem)
                        ? localizationService.T("Widget.CannotMoveToFolder")
                        : localizationService.T("Widget.DragCaption.CurrentWidget");
                }

                FileDropIntent folderIntent = ResolveElevatedDropIntent(
                    sourcePaths,
                    folderDestination,
                    hasMappedFolder: true,
                    forceCopyUnlessExplicit: forceCopyUnlessExplicit);
                canDrop = folderIntent != FileDropIntent.None;
                return FormatElevatedDropCaption(
                    localizationService,
                    folderIntent,
                    folder.Name);
            }

            string destination = TransferIntentEngine.ResolveProspectiveManagedDestination(
                fileAdapter.CurrentFolderPath,
                fileAdapter.ViewModel.MappedFolderPath ?? _config.MappedFolderPath,
                SettingsService.Settings.FileWidget.DefaultManagedStorageRootPath);
            bool mapped = !string.IsNullOrWhiteSpace(destination);
            if (!string.IsNullOrWhiteSpace(destination) &&
                FileService.IsLexicallyUnsafeDirectoryTransfer(sourcePaths, destination))
            {
                canDrop = false;
                return localizationService.T("Widget.Error.UnsafeFolderTransfer");
            }

            if (!string.IsNullOrWhiteSpace(destination) &&
                FileSurfaceContent.AreAllSourcesAlreadyInDestinationLexically(sourcePaths, destination))
            {
                canDrop = false;
                return localizationService.T("Widget.DragCaption.CurrentWidget");
            }

            FileDropIntent surfaceIntent = ResolveElevatedDropIntent(
                sourcePaths,
                destination,
                hasMappedFolder: mapped,
                forceCopyUnlessExplicit: forceCopyUnlessExplicit);
            canDrop = surfaceIntent != FileDropIntent.None;
            if (!mapped && surfaceIntent == FileDropIntent.Reference)
            {
                return localizationService.T("Widget.DragCaption.Reference");
            }

            string widgetTitle = !string.IsNullOrWhiteSpace(_config.Name)
                ? _config.Name
                : fileAdapter.Surface?.ViewModel.CurrentFolderDisplayName ?? string.Empty;
            return FormatElevatedDropCaption(
                localizationService,
                surfaceIntent,
                widgetTitle);
        }

        if (CurrentContent is QuickCaptureWidgetContentAdapter or
            TodoWidgetContentAdapter or
            ClipboardWidgetContentAdapter)
        {
            canDrop = true;
            return _config.Name;
        }

        if (CurrentContent is CustomWidgetContentAdapter customAdapter &&
            customAdapter.ViewModel.AcceptsFileDrops)
        {
            canDrop = true;
            return _config.Name;
        }

        return string.Empty;
    }

    internal void LeaveElevatedPointerDrag()
    {
        NativeFileDropTarget_DragLeaveEvent();
    }

    internal async Task<bool> AcceptElevatedPointerDropAsync(
        IReadOnlyList<string> sourcePaths,
        int screenX,
        int screenY,
        bool? controlDown = null,
        bool? shiftDown = null,
        bool? altDown = null,
        bool isSecondaryButtonDrag = false)
    {
        if (IsClosing || sourcePaths.Count == 0)
        {
            LeaveElevatedPointerDrag();
            return false;
        }

        // Decision 3A: if the pointer released directly on an inactive group
        // tab pill before hover-dwell completed, switch to that tab first so
        // the drop targets the tab under the cursor (never the old active tab).
        if (!await TryEnsureGroupTabDropTargetAsync(screenX, screenY))
        {
            LeaveElevatedPointerDrag();
            return false;
        }

        bool canSourceMove = CanElevatedDragSourceMove(
            sourcePaths,
            PathOriginClassifier.DefaultDropProbeBudget);
        uint allowedEffects = canSourceMove
            ? NativeDropEffectPolicy.Copy |
              NativeDropEffectPolicy.Move |
              NativeDropEffectPolicy.Link
            : NativeDropEffectPolicy.Copy |
              NativeDropEffectPolicy.Link;

        if (CurrentContent is FileWidgetContentAdapter fileAdapter)
        {
            fileAdapter.SetHostWindowHandle(HWnd);
            fileAdapter.HostHasActiveShellDragWindowProbe = () => HasActiveNativeShellDragWindow;
            if (fileAdapter.Surface is null && !IsCompactBoundsStateActive)
            {
                EnsureCollapsedContentViewAttached();
                ArmCollapsedContentHibernationIfSettled();
            }

            WidgetItem? launchItem = _nativeFileDropLaunchTarget is { Path.Length: > 0 } activeLaunch &&
                IsNativeDropPointOverItemIcon(screenX, screenY, activeLaunch)
                ? activeLaunch
                : null;
            WidgetItem? capturedTarget =
                fileAdapter.TryResolveFolderNavigationDropTarget(screenX, screenY) ??
                _nativeFileDropItemTarget;

            if (isSecondaryButtonDrag)
            {
                var rightArgs = new NativeDropIntentEventArgs(
                    Paths: sourcePaths,
                    ScreenX: screenX,
                    ScreenY: screenY,
                    ContainsTemporaryFiles: false,
                    CopyRequested: false,
                    ShortcutRequested: false,
                    RightButtonDrag: true,
                    FeedbackEffect: allowedEffects,
                    KeyState: 0,
                    AllowedEffects: allowedEffects);
                LeaveElevatedPointerDrag();
                NativeRightDropChoice choice = await ShowNativeRightButtonDropChoiceAsync(
                    rightArgs,
                    launchItem,
                    capturedTarget);
                if (choice == NativeRightDropChoice.Cancel)
                {
                    NotifyCompactDropCompletedFromHost();
                    return false;
                }

                if (choice == NativeRightDropChoice.OpenWithApp && launchItem is not null)
                {
                    NotifyCompactDropCompletedFromHost();
                    return TryLaunchElevatedShortcutDrop(
                        fileAdapter,
                        launchItem,
                        sourcePaths);
                }

                WidgetItem? effectiveTarget = launchItem is not null &&
                    ReferenceEquals(capturedTarget, launchItem)
                    ? null
                    : capturedTarget;
                if (effectiveTarget is null)
                {
                    fileAdapter.Surface?.CaptureNativeDropInsertion(screenX, screenY);
                }

                FileDropIntent? forcedChoiceIntent = choice switch
                {
                    NativeRightDropChoice.Copy => FileDropIntent.Copy,
                    NativeRightDropChoice.Move => FileDropIntent.Move,
                    NativeRightDropChoice.Shortcut => FileDropIntent.Shortcut,
                    _ => null,
                };
                NotifyCompactDropCompletedFromHost();
                await ImportNativeFileDropAsync(
                    sourcePaths,
                    containsTemporaryFiles: false,
                    copyWhenMapped: choice != NativeRightDropChoice.Move,
                    screenX: screenX,
                    screenY: screenY,
                    forcedIntent: forcedChoiceIntent,
                    allowedEffects: allowedEffects,
                    capturedItemTarget: effectiveTarget,
                    hasCapturedItemTarget: effectiveTarget is not null);
                return true;
            }

            if (launchItem is not null)
            {
                LeaveElevatedPointerDrag();
                return TryLaunchElevatedShortcutDrop(
                    fileAdapter,
                    launchItem,
                    sourcePaths);
            }

            if (capturedTarget is null)
            {
                fileAdapter.Surface?.CaptureNativeDropInsertion(screenX, screenY);
            }

            string destination = capturedTarget is { } targetFolder &&
                ItemDropBehaviorPolicy.TryGetFolderDropDestinationPath(
                    targetFolder,
                    out string targetFolderDestination)
                ? targetFolderDestination
                : TransferIntentEngine.ResolveProspectiveManagedDestination(
                    fileAdapter.CurrentFolderPath,
                    fileAdapter.ViewModel.MappedFolderPath ?? _config.MappedFolderPath,
                    SettingsService.Settings.FileWidget.DefaultManagedStorageRootPath);
            FileDropIntent snapshotIntent = ResolveElevatedDropIntent(
                sourcePaths,
                destination,
                hasMappedFolder: !string.IsNullOrWhiteSpace(destination),
                controlDown: controlDown,
                shiftDown: shiftDown,
                altDown: altDown,
                probeBudget: PathOriginClassifier.DefaultDropProbeBudget);
            if (snapshotIntent == FileDropIntent.None)
            {
                LeaveElevatedPointerDrag();
                return false;
            }

            FileDropIntent? forcedIntent = snapshotIntent == FileDropIntent.Reference
                ? null
                : snapshotIntent;
            NotifyCompactDropCompletedFromHost();
            LeaveElevatedPointerDrag();
            await ImportNativeFileDropAsync(
                sourcePaths,
                containsTemporaryFiles: false,
                copyWhenMapped: null,
                screenX: screenX,
                screenY: screenY,
                forcedIntent: forcedIntent,
                allowedEffects: allowedEffects,
                capturedItemTarget: capturedTarget,
                hasCapturedItemTarget: capturedTarget is not null);
            return true;
        }

        if (!CurrentContentAcceptsFileDrops())
        {
            LeaveElevatedPointerDrag();
            return false;
        }

        if (isSecondaryButtonDrag)
        {
            var rightArgs = new NativeDropIntentEventArgs(
                Paths: sourcePaths,
                ScreenX: screenX,
                ScreenY: screenY,
                ContainsTemporaryFiles: false,
                CopyRequested: false,
                ShortcutRequested: false,
                RightButtonDrag: true,
                FeedbackEffect: allowedEffects,
                KeyState: 0,
                AllowedEffects: allowedEffects);
            LeaveElevatedPointerDrag();
            NativeRightDropChoice choice = await ShowNativeRightButtonDropChoiceAsync(
                rightArgs,
                launchTarget: null,
                itemTarget: null);
            NotifyCompactDropCompletedFromHost();
            if (choice == NativeRightDropChoice.Cancel)
            {
                return false;
            }

            await ImportNativeFileDropAsync(
                sourcePaths,
                containsTemporaryFiles: false,
                copyWhenMapped: choice != NativeRightDropChoice.Move,
                screenX: screenX,
                screenY: screenY,
                forcedIntent: choice switch
                {
                    NativeRightDropChoice.Copy => FileDropIntent.Copy,
                    NativeRightDropChoice.Move => FileDropIntent.Move,
                    NativeRightDropChoice.Shortcut => FileDropIntent.Shortcut,
                    _ => null,
                },
                allowedEffects: allowedEffects);
            return true;
        }

        NotifyCompactDropCompletedFromHost();
        LeaveElevatedPointerDrag();
        await ImportNativeFileDropAsync(
            sourcePaths,
            containsTemporaryFiles: false,
            copyWhenMapped: null,
            screenX: screenX,
            screenY: screenY,
            forcedIntent: null,
            allowedEffects: allowedEffects);
        return true;
    }

    private bool TryLaunchElevatedShortcutDrop(
        FileWidgetContentAdapter fileAdapter,
        WidgetItem launchItem,
        IReadOnlyList<string> sourcePaths)
    {
        fileAdapter.Surface?.MarkNativeLaunchConsumed();
        if (!ShortcutFileLauncher.CanLaunchWithFiles(launchItem.Path, sourcePaths))
        {
            fileAdapter.Surface?.ShowShortcutLaunchRefusedFeedback(
                launchItem.Name);
            return false;
        }

        string shortcutPath = launchItem.Path;
        string shortcutName = launchItem.Name;
        string[] capturedPaths = sourcePaths.ToArray();
        IntPtr ownerHwnd = HWnd;
        UiDispatch.PostAfterCallback(async () =>
        {
            if (IsClosing)
            {
                return;
            }

            ShellLaunchOutcome launchOutcome = await ShortcutFileLauncher.LaunchWithFilesAsync(
                shortcutPath,
                capturedPaths,
                ownerHwnd);
            if (launchOutcome == ShellLaunchOutcome.Failed && !IsClosing)
            {
                fileAdapter.Surface?.ShowShortcutLaunchRefusedFeedback(
                    shortcutName);
            }
        });
        return true;
    }

    private bool CanElevatedDragSourceMove(
        IReadOnlyList<string> sourcePaths,
        TimeSpan? probeBudget = null)
    {
        return !DriveMediaInterop.IsProtectedDragOutSource(sourcePaths) &&
            !PathOriginClassifier
                .ClassifySources(
                    sourcePaths,
                    probeBudget ?? PathOriginClassifier.DefaultHoverProbeBudget)
                .IsMoveProtectedSystemOrSharedSource;
    }

    private FileDropIntent ResolveElevatedDropIntent(
        IReadOnlyList<string> sourcePaths,
        string destinationFolderPath,
        bool hasMappedFolder,
        bool? controlDown = null,
        bool? shiftDown = null,
        bool? altDown = null,
        TimeSpan? probeBudget = null,
        bool forceCopyUnlessExplicit = false)
    {
        TimeSpan budget = probeBudget ?? PathOriginClassifier.DefaultHoverProbeBudget;
        bool sameVolume = sourcePaths.Count == 0 ||
            (destinationFolderPath.Length > 0 &&
             PathOriginClassifier.AreAllOnSameVolume(
                 sourcePaths,
                 destinationFolderPath,
                 budget));
        string action = SettingsService.Settings.FileWidget.ManagedDropAction;
        bool followWindows = !forceCopyUnlessExplicit && string.Equals(
            action,
            Services.SettingsService.ManagedDropActionFollowWindows,
            StringComparison.Ordinal);
        SourceOriginSnapshot origin = PathOriginClassifier.ClassifySources(
            sourcePaths,
            budget);
        bool canSourceMove = !DriveMediaInterop.IsProtectedDragOutSource(sourcePaths) &&
            !origin.IsMoveProtectedSystemOrSharedSource;
        uint allowedEffects = canSourceMove
            ? NativeDropEffectPolicy.Copy | NativeDropEffectPolicy.Move | NativeDropEffectPolicy.Link
            : NativeDropEffectPolicy.Copy | NativeDropEffectPolicy.Link;
        return TransferIntentEngine.Evaluate(new TransferIntentRequest(
            Domain: TransferDomain.InternalDeskBox,
            TargetKind: TransferTargetKind.DirectorySurface,
            HasFileData: true,
            HasVirtualFileData: false,
            HasShellApplicationOrNamespaceData: false,
            ForceCopy: false,
            HasMappedFolder: hasMappedFolder,
            ControlDown: controlDown ?? Win32Helper.IsAsyncKeyPressed(Windows.System.VirtualKey.Control),
            ShiftDown: shiftDown ?? Win32Helper.IsAsyncKeyPressed(Windows.System.VirtualKey.Shift),
            AltDown: altDown ?? Win32Helper.IsAsyncKeyPressed(Windows.System.VirtualKey.Menu),
            AllowedEffects: allowedEffects,
            NoOperationMetadata: false,
            DefaultMove: !forceCopyUnlessExplicit && string.Equals(
                action,
                Services.SettingsService.ManagedDropActionMove,
                StringComparison.Ordinal),
            FollowWindows: followWindows,
            SameVolume: sameVolume,
            CanLink: true,
            SourceOrigin: origin)).Intent;
    }

    private static string FormatElevatedDropCaption(
        LocalizationService localizationService,
        FileDropIntent intent,
        string targetName)
    {
        return intent switch
        {
            FileDropIntent.Shortcut =>
                $"{localizationService.T("Widget.CreateShortcut")} \"{targetName}\"",
            FileDropIntent.Copy => localizationService.Format(
                "Widget.CopyToFolder",
                targetName),
            FileDropIntent.Move => localizationService.Format(
                "Widget.MoveToFolder",
                targetName),
            _ => string.Empty
        };
    }
}
