using DeskBox.Controls.WidgetContents;
using DeskBox.Helpers;
using DeskBox.Models;
using DeskBox.Platform;
using DeskBox.Services;
using DeskBox.ViewModels;

namespace DeskBox.Views;

public sealed partial class ContentWidgetWindow
{
    internal bool CanObserveElevatedPointerDrag =>
        !IsClosing &&
        Visible &&
        !IsWidgetCollapsed &&
        !IsCompactTransitionActive;

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

        ObserveNativeGroupTabDragHover(
            screenX,
            screenY,
            hasFileData: true,
            pointerGeneration: 0);

        if (CurrentContent is FileWidgetContentAdapter fileAdapter)
        {
            fileAdapter.SetHostWindowHandle(HWnd);
            if (fileAdapter.Surface is null)
            {
                EnsureCollapsedContentViewAttached();
                ArmCollapsedContentHibernationIfSettled();
            }

            if (fileAdapter.IsImportBusy)
            {
                return string.Empty;
            }

            WidgetItem? hitItem = FindNativeDropDataContext<WidgetItem>(
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

            if (nativeTarget is { IsFolder: true, Path.Length: > 0 } folder)
            {
                if (FileService.IsLexicallyUnsafeDirectoryTransfer(sourcePaths, folder.Path))
                {
                    canDrop = false;
                    return localizationService.T("Widget.CannotMoveToFolder");
                }

                FileDropIntent folderIntent = ResolveElevatedDropIntent(
                    sourcePaths,
                    folder.Path,
                    hasMappedFolder: true);
                canDrop = folderIntent != FileDropIntent.None;
                return FormatElevatedDropCaption(
                    localizationService,
                    folderIntent,
                    folder.Name);
            }

            string destination = fileAdapter.Surface?.ViewModel.CurrentFolderPath ??
                fileAdapter.Surface?.ViewModel.MappedFolderPath ??
                string.Empty;
            bool mapped = !string.IsNullOrWhiteSpace(
                fileAdapter.Surface?.ViewModel.MappedFolderPath);
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
        int screenY)
    {
        if (IsClosing || sourcePaths.Count == 0)
        {
            LeaveElevatedPointerDrag();
            return false;
        }

        if (CurrentContent is FileWidgetContentAdapter fileAdapter)
        {
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

            WidgetItem? capturedTarget = _nativeFileDropItemTarget;
            if (capturedTarget is null)
            {
                fileAdapter.Surface?.CaptureNativeDropInsertion(screenX, screenY);
            }

            LeaveElevatedPointerDrag();
            await ImportNativeFileDropAsync(
                sourcePaths,
                containsTemporaryFiles: false,
                copyWhenMapped: null,
                screenX: screenX,
                screenY: screenY,
                forcedIntent: null,
                allowedEffects: NativeDropEffectPolicy.Copy |
                                NativeDropEffectPolicy.Move |
                                NativeDropEffectPolicy.Link,
                capturedItemTarget: capturedTarget,
                hasCapturedItemTarget: capturedTarget is not null);
            return true;
        }

        LeaveElevatedPointerDrag();
        await ImportNativeFileDropAsync(
            sourcePaths,
            containsTemporaryFiles: false,
            copyWhenMapped: null,
            screenX: screenX,
            screenY: screenY,
            forcedIntent: null,
            allowedEffects: NativeDropEffectPolicy.Copy |
                            NativeDropEffectPolicy.Move |
                            NativeDropEffectPolicy.Link);
        return true;
    }

    private FileDropIntent ResolveElevatedDropIntent(
        IReadOnlyList<string> sourcePaths,
        string destinationFolderPath,
        bool hasMappedFolder)
    {
        bool sameVolume = sourcePaths.Count == 0 ||
            (destinationFolderPath.Length > 0 &&
             FileDropIntentPolicy.AreAllOnSameVolume(sourcePaths, destinationFolderPath));
        string action = SettingsService.Settings.FileWidget.ManagedDropAction;
        bool followWindows = string.Equals(
            action,
            Services.SettingsService.ManagedDropActionFollowWindows,
            StringComparison.Ordinal);
        return FileDropIntentPolicy.ResolveMappedTransfer(
            hasMappedFolder,
            forceCopy: false,
            controlDown: Win32Helper.IsKeyPressed(Windows.System.VirtualKey.Control),
            shiftDown: Win32Helper.IsKeyPressed(Windows.System.VirtualKey.Shift),
            defaultMove: string.Equals(
                action,
                Services.SettingsService.ManagedDropActionMove,
                StringComparison.Ordinal),
            canCopy: true,
            canMove: true,
            altDown: Win32Helper.IsKeyPressed(Windows.System.VirtualKey.Menu),
            followWindows,
            sameVolume,
            canLink: true);
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
