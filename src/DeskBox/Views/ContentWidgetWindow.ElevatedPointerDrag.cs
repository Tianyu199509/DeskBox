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
         custom.ViewModel.AcceptsFileDrops) ||
        base.SupportsCompactDropExpansion;

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
        out bool canDrop)
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

                canDrop = true;
                return localizationService.Format(
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

                FileDropIntent folderIntent = ResolveElevatedDropIntent(
                    sourcePaths,
                    folderDestination,
                    hasMappedFolder: true);
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

            FileDropIntent surfaceIntent = ResolveElevatedDropIntent(
                sourcePaths,
                destination,
                hasMappedFolder: mapped);
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
        bool? altDown = null)
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

        bool canSourceMove = CanElevatedDragSourceMove(sourcePaths);
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

            if (_nativeFileDropLaunchTarget is { Path.Length: > 0 } launchItem &&
                IsNativeDropPointOverItemIcon(screenX, screenY, launchItem))
            {
                LeaveElevatedPointerDrag();
                fileAdapter.Surface?.MarkNativeLaunchConsumed();
                bool launched = ShortcutFileLauncher.TryLaunchWithFiles(
                    launchItem.Path,
                    sourcePaths);
                if (!launched)
                {
                    fileAdapter.Surface?.ShowShortcutLaunchRefusedFeedback(
                        launchItem.Name);
                }

                return launched;
            }

            WidgetItem? capturedTarget =
                fileAdapter.TryResolveFolderNavigationDropTarget(screenX, screenY) ??
                _nativeFileDropItemTarget;
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
                altDown: altDown);
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

    private bool CanElevatedDragSourceMove(IReadOnlyList<string> sourcePaths)
    {
        return !PathOriginClassifier
            .ClassifySources(sourcePaths, TimeSpan.FromMilliseconds(80))
            .IsMoveProtectedSystemOrSharedSource;
    }

    private FileDropIntent ResolveElevatedDropIntent(
        IReadOnlyList<string> sourcePaths,
        string destinationFolderPath,
        bool hasMappedFolder,
        bool? controlDown = null,
        bool? shiftDown = null,
        bool? altDown = null)
    {
        bool sameVolume = sourcePaths.Count == 0 ||
            (destinationFolderPath.Length > 0 &&
             PathOriginClassifier.AreAllOnSameVolume(
                 sourcePaths,
                 destinationFolderPath,
                 TimeSpan.FromMilliseconds(80)));
        string action = SettingsService.Settings.FileWidget.ManagedDropAction;
        bool followWindows = string.Equals(
            action,
            Services.SettingsService.ManagedDropActionFollowWindows,
            StringComparison.Ordinal);
        SourceOriginSnapshot origin = PathOriginClassifier.ClassifySources(
            sourcePaths,
            TimeSpan.FromMilliseconds(80));
        return TransferIntentEngine.Evaluate(new TransferIntentRequest(
            Domain: TransferDomain.InternalDeskBox,
            TargetKind: TransferTargetKind.DirectorySurface,
            HasFileData: true,
            HasVirtualFileData: false,
            HasShellApplicationOrNamespaceData: false,
            ForceCopy: false,
            HasMappedFolder: hasMappedFolder,
            ControlDown: controlDown ?? Win32Helper.IsKeyPressed(Windows.System.VirtualKey.Control),
            ShiftDown: shiftDown ?? Win32Helper.IsKeyPressed(Windows.System.VirtualKey.Shift),
            AltDown: altDown ?? Win32Helper.IsKeyPressed(Windows.System.VirtualKey.Menu),
            AllowedEffects: NativeDropEffectPolicy.Copy | NativeDropEffectPolicy.Move | NativeDropEffectPolicy.Link,
            NoOperationMetadata: false,
            DefaultMove: string.Equals(
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
