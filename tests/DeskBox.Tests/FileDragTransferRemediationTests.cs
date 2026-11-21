using System.Buffers.Binary;
using System.Reflection;
using System.Reflection.Emit;
using System.Text;
using System.Text.Json;
using DeskBox.Controls;
using DeskBox.Controls.WidgetContents;
using DeskBox.Helpers;
using DeskBox.Models;
using DeskBox.Platform;
using DeskBox.Services;
using DeskBox.ViewModels;
using DeskBox.Views;
using Windows.ApplicationModel.DataTransfer;

namespace DeskBox.Tests;

public sealed class FileDragTransferRemediationTests
{
    [Theory]
    [InlineData(0x00000400u, 0xA000000Cu, true)]  // IO_REPARSE_TAG_SYMLINK
    [InlineData(0x00000410u, 0xA0000003u, true)]  // IO_REPARSE_TAG_MOUNT_POINT (junction / volume mount)
    [InlineData(0x00000400u, 0xA000001Du, true)]  // IO_REPARSE_TAG_LX_SYMLINK
    [InlineData(0x00000400u, 0x8000001Bu, true)]  // IO_REPARSE_TAG_APPEXECLINK
    [InlineData(0x00000400u, 0x9000001Au, false)] // IO_REPARSE_TAG_CLOUD (OneDrive)
    [InlineData(0x00000400u, 0x80000017u, false)] // IO_REPARSE_TAG_WOF (CompactOS)
    [InlineData(0x00000080u, 0xA000000Cu, false)] // Non-reparse normal file
    public void IsDisallowedLinkReparseTag_ClassifiesLinksAndMountPointsAccurately(
        uint fileAttributes,
        uint reparseTag,
        bool expected)
    {
        Assert.Equal(
            expected,
            StorageMigrationNativeMethods.IsDisallowedLinkReparseTag(fileAttributes, reparseTag));
    }

    [Fact]
    public void StageExternalTempDropPaths_CopiesIntoIndependentInode_PreventingSourceMutation()
    {
        string tempRoot = Path.GetFullPath(Path.GetTempPath());
        string transientDir = Path.Combine(tempRoot, $"7zO{Guid.NewGuid():N}"[..11]);
        string? stagedDir = null;
        try
        {
            Directory.CreateDirectory(transientDir);
            string sourceFile = Path.Combine(transientDir, "report.txt");
            File.WriteAllText(sourceFile, "original-immutable-payload", Encoding.UTF8);

            IReadOnlyList<string> staged = DropStagingFileOps.StageExternalTempDropPaths(
                [sourceFile],
                hasShellIdList: false,
                out bool stagedAny);

            Assert.True(stagedAny);
            Assert.Single(staged);
            stagedDir = Path.GetDirectoryName(staged[0]);

            // Mutate the original source file in-place (which would corrupt a hardlinked staging file).
            File.WriteAllText(sourceFile, "mutated-by-archive-app", Encoding.UTF8);

            Assert.Equal("original-immutable-payload", File.ReadAllText(staged[0], Encoding.UTF8));
        }
        finally
        {
            TryDeleteDirectory(transientDir);
            if (!string.IsNullOrWhiteSpace(stagedDir))
            {
                TryDeleteDirectory(stagedDir);
            }
        }
    }

    [Fact]
    public void FileSymbolicLink_IsDetectedAndNeverDereferencedByIdentityDelete()
    {
        string root = Path.Combine(Path.GetTempPath(), $"DeskBoxSymlinkAudit-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            string externalTarget = Path.Combine(root, "important-target.txt");
            File.WriteAllText(externalTarget, "do-not-delete-me", Encoding.UTF8);
            string linkPath = Path.Combine(root, "shortcut-link.txt");

            try
            {
                File.CreateSymbolicLink(linkPath, externalTarget);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Non-Developer-Mode environments forbid unprivileged file symlink creation;
                // verify directory junction detection instead (always unprivileged on Windows).
                string junctionDir = Path.Combine(root, "junction-dir");
                string targetDir = Path.Combine(root, "target-dir");
                Directory.CreateDirectory(targetDir);
                using var proc = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = $"/c mklink /J \"{junctionDir}\" \"{targetDir}\"",
                    CreateNoWindow = true,
                    UseShellExecute = false
                })!;
                proc.WaitForExit(5000);
                Assert.True(FileService.IsFileSystemLink(junctionDir));
                return;
            }

            Assert.True(FileService.IsFileSystemLink(linkPath));

            // Capture real target identity as if a swapped symlink tried to impersonate it.
            using var targetStream = new FileStream(externalTarget, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var targetIdentity = FileService.IdentityFromHandle(targetStream.SafeFileHandle);
            targetStream.Close();
            Assert.NotNull(targetIdentity);

            bool deleted = FileService.TryDeleteFileByIdentity(linkPath, targetIdentity.Value);
            Assert.False(deleted);
            Assert.True(File.Exists(externalTarget));
            Assert.Equal("do-not-delete-me", File.ReadAllText(externalTarget, Encoding.UTF8));
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Fact]
    public async Task NavigateIntoSubfolder_PreservesRootEntriesAndPrunesStaleSubfolderEntries()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), $"DeskBoxAddedAt-{Guid.NewGuid():N}");
        string settingsRoot = Path.Combine(tempRoot, "settings");
        string rootDir = Path.Combine(tempRoot, "box");
        string subDir = Path.Combine(rootDir, "Subfolder");
        Directory.CreateDirectory(subDir);

        string rootLiveFile = Path.Combine(rootDir, "root-keep.txt");
        string subLiveFile = Path.Combine(subDir, "sub-keep.txt");
        string subDeletedFile = Path.Combine(subDir, "sub-deleted.txt");
        await File.WriteAllTextAsync(rootLiveFile, "root");
        await File.WriteAllTextAsync(subLiveFile, "sub");

        try
        {
            var settingsService = new SettingsService(settingsRoot);
            await settingsService.LoadAsync();
            settingsService.Settings.FileWidget.FileWidgetFolderOpenBehavior =
                FileWidgetFolderOpenBehaviorNames.Embedded;
            var fileService = new FileService();
            var organizer = TestOrganizerServices.Create(settingsService, fileService);

            DateTimeOffset rootTimestamp = new(2025, 5, 1, 10, 0, 0, TimeSpan.Zero);
            DateTimeOffset subTimestamp = new(2025, 5, 2, 10, 0, 0, TimeSpan.Zero);
            var config = new WidgetConfig
            {
                Id = "added-at-test",
                MappedFolderPath = rootDir,
                FollowsDefaultStoragePath = false,
                FileAddedAtTrackingInitialized = true
            };
            config.FileAddedAtByPath[rootLiveFile] = rootTimestamp;
            config.FileAddedAtByPath[subLiveFile] = subTimestamp;
            config.FileAddedAtByPath[subDeletedFile] = subTimestamp;
            settingsService.Settings.Widgets.Add(config);

            using var vm = new WidgetViewModel(
                config,
                fileService,
                organizer,
                settingsService,
                dispatcherQueue: null);
            await vm.InitializeAsync();

            WidgetItem subFolderItem = vm.Items.Single(item => item.IsFolder);
            Assert.True(await vm.NavigateIntoFolderAsync(subFolderItem));

            // Root entry MUST survive navigating into Subfolder, while deleted Subfolder entry is pruned.
            Assert.True(config.FileAddedAtByPath.ContainsKey(rootLiveFile));
            Assert.Equal(rootTimestamp, config.FileAddedAtByPath[rootLiveFile]);
            Assert.True(config.FileAddedAtByPath.ContainsKey(subLiveFile));
            Assert.False(config.FileAddedAtByPath.ContainsKey(subDeletedFile));
        }
        finally
        {
            TryDeleteDirectory(tempRoot);
        }
    }

    [Fact]
    public void ClassifyInboundDropSources_DetectsUsbFlashUsbFixedSsdUncAndProtectedFolders()
    {
        // 1. USB flash drive (DriveType.Removable) -> ExternalRemovableOrNetwork
        Assert.Equal(
            InboundSourceProtectionKind.ExternalRemovableOrNetwork,
            DriveMediaInterop.ClassifyInboundDropSources(
                [@"F:\Photos\IMG_0001.jpg"],
                driveTypeProbe: static _ => DriveType.Removable,
                busProbe: static _ => (StorageBusKind.Usb, true)));

        // 2. External USB SSD reporting DriveType.Fixed -> ExternalRemovableOrNetwork via USB bus
        Assert.Equal(
            InboundSourceProtectionKind.ExternalRemovableOrNetwork,
            DriveMediaInterop.ClassifyInboundDropSources(
                [@"G:\Backups\archive.zip"],
                driveTypeProbe: static _ => DriveType.Fixed,
                busProbe: static _ => (StorageBusKind.Usb, false)));

        // 3. Internal NVMe drive reporting DriveType.Fixed -> None (allows default Move across C: -> D:)
        Assert.Equal(
            InboundSourceProtectionKind.None,
            DriveMediaInterop.ClassifyInboundDropSources(
                [@"\\?\D:\Projects\spec.pdf"],
                driveTypeProbe: static _ => DriveType.Fixed,
                busProbe: static _ => (StorageBusKind.Nvme, false)));

        // 4. Extended UNC share -> ExternalRemovableOrNetwork (must not misparse 'U' in UNC as drive letter)
        Assert.Equal(
            InboundSourceProtectionKind.ExternalRemovableOrNetwork,
            DriveMediaInterop.ClassifyInboundDropSources(
                [@"\\?\UNC\nas\team\roadmap.xlsx"],
                driveTypeProbe: static _ => DriveType.Fixed,
                busProbe: static _ => (StorageBusKind.Nvme, false)));

        // 5. Common Start Menu -> SystemOrSharedProtected (overrides ExternalRemovableOrNetwork)
        string publicDesktop = Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu);
        if (!string.IsNullOrWhiteSpace(publicDesktop))
        {
            Assert.Equal(
                InboundSourceProtectionKind.SystemOrSharedProtected,
                DriveMediaInterop.ClassifyInboundDropSources(
                    [@"F:\USB\note.txt", Path.Combine(publicDesktop, "SharedApp.lnk")],
                    driveTypeProbe: static _ => DriveType.Removable,
                    busProbe: static _ => (StorageBusKind.Usb, true)));
        }
    }

    [Fact]
    public void ResolveMappedTransfer_ProtectsExternalMediaAsCopyUnlessShiftHeld_AndNeverDegradesExplicitCopyToMove()
    {
        // External USB/UNC in default Move mode -> auto-protected to Copy when unmodified
        Assert.Equal(
            FileDropIntent.Copy,
            FileDropIntentPolicy.ResolveMappedTransfer(
                hasMappedFolder: true,
                forceCopy: false,
                controlDown: false,
                shiftDown: false,
                defaultMove: true,
                canCopy: true,
                canMove: true,
                protectDefaultMoveAsCopy: true));

        // Explicit Shift overrides auto-protection on writable external media -> Move
        Assert.Equal(
            FileDropIntent.Move,
            FileDropIntentPolicy.ResolveMappedTransfer(
                hasMappedFolder: true,
                forceCopy: false,
                controlDown: false,
                shiftDown: true,
                defaultMove: true,
                canCopy: true,
                canMove: true,
                protectDefaultMoveAsCopy: true));

        // Explicit Copy mode (!defaultMove && !followWindows) MUST return None (never Move!) if source omits Copy
        Assert.Equal(
            FileDropIntent.None,
            FileDropIntentPolicy.ResolveMappedTransfer(
                hasMappedFolder: true,
                forceCopy: false,
                controlDown: false,
                shiftDown: false,
                defaultMove: false,
                canCopy: false,
                canMove: true,
                followWindows: false));
    }

    [Fact]
    public void InterBoxTransfer_CopyPriorityPreventsSourceDeletionWhenSourceOrProtectedPathForbidsMove()
    {
        var package = new DataPackage();
        string normalSource = @"D:\BoxA\notes.md";
        package.Properties[DeskBoxDragData.InternalFileDragTokenProperty] = DeskBoxDragData.InternalFileDragToken;
        package.Properties[DeskBoxDragData.SourcePathsProperty] = new[] { normalSource };
        DataPackageView view = package.GetView();

        // Domain B decoupling: internal DeskBox drags stay eligible for Copy | Move | Link
        // between boxes even when outbound ManagedDragOutAction = Copy folded wire allowedOperations to Copy.
        DataPackageOperation internalOps = DeskBoxDragData.GetFileTransferOperations(
            view,
            DataPackageOperation.Copy);
        Assert.True(internalOps.HasFlag(DataPackageOperation.Copy));
        Assert.True(internalOps.HasFlag(DataPackageOperation.Move));

        // External non-internal drag with single-bit Copy still forbids Move so a copy-only source is never deleted.
        var externalCopyOnlyPkg = new DataPackage();
        DataPackageOperation externalCopyOnlyOps = DeskBoxDragData.GetFileTransferOperations(
            externalCopyOnlyPkg.GetView(),
            DataPackageOperation.Copy);
        Assert.True(externalCopyOnlyOps.HasFlag(DataPackageOperation.Copy));
        Assert.False(externalCopyOnlyOps.HasFlag(DataPackageOperation.Move));
        Assert.Equal(
            FileDropIntent.Copy,
            FileDropIntentPolicy.ResolveMappedTransfer(
                hasMappedFolder: true,
                forceCopy: false,
                controlDown: false,
                shiftDown: false,
                defaultMove: true,
                canCopy: externalCopyOnlyOps.HasFlag(DataPackageOperation.Copy),
                canMove: externalCopyOnlyOps.HasFlag(DataPackageOperation.Move)));

        // Protected Start Menu item in an internal drag also strips Move even if allowedOperations had Move
        string publicDesktop = Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu);
        if (!string.IsNullOrWhiteSpace(publicDesktop))
        {
            var protectedPkg = new DataPackage();
            protectedPkg.Properties[DeskBoxDragData.InternalFileDragTokenProperty] = DeskBoxDragData.InternalFileDragToken;
            protectedPkg.Properties[DeskBoxDragData.SourcePathsProperty] =
                new[] { Path.Combine(publicDesktop, "Browser.lnk") };
            DataPackageOperation protectedOps = DeskBoxDragData.GetFileTransferOperations(
                protectedPkg.GetView(),
                DataPackageOperation.Copy | DataPackageOperation.Move);
            Assert.False(protectedOps.HasFlag(DataPackageOperation.Move));
            Assert.Equal(
                DataPackageOperation.Copy,
                FileItemDragPackage.ResolveDragOutPreferredOperation(
                    SettingsService.ManagedDragOutActionMove,
                    [Path.Combine(publicDesktop, "Browser.lnk")]));
        }
    }

    [Fact]
    public void DesktopToastHudService_PersistsOnceHints_EnforcesPriority_AndNeverSilencesSafetyWarnings()
    {
        string tempDataDir = Path.Combine(Path.GetTempPath(), $"DeskBoxHudTest-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDataDir);
        DesktopToastHudService.Shared.ResetForTests();
        try
        {
            // 1. First-time contextual guide shows and persists to dismissed-drag-hints.txt
            bool shownFirst = DesktopToastHudService.Shared.TryShow(
                new DesktopToastRequest(
                    HintId: DesktopToastHudService.HintIdWin10ExplorerMenu,
                    Message: "Win10 Explorer menu guide",
                    Priority: DesktopToastPriority.ContextualGuide,
                    PersistOnceOnShow: true),
                dataDirectoryOverride: tempDataDir);
            Assert.True(shownFirst);
            Assert.True(File.Exists(Path.Combine(tempDataDir, DesktopToastHudService.DismissedHintsFileName)));

            // 2. While ContextualGuide is active, Routine receipt cannot overwrite it
            bool routineBlocked = DesktopToastHudService.Shared.TryShow(
                new DesktopToastRequest(
                    HintId: "drag-out-result-tip",
                    Message: "Copied",
                    Priority: DesktopToastPriority.Routine),
                dataDirectoryOverride: tempDataDir);
            Assert.False(routineBlocked);

            // 3. Second occurrence of PersistOnceOnShow hint is suppressed even after window hides
            DesktopToastHudService.Shared.NotifyWindowHidden();
            bool shownSecond = DesktopToastHudService.Shared.TryShow(
                new DesktopToastRequest(
                    HintId: DesktopToastHudService.HintIdWin10ExplorerMenu,
                    Message: "Win10 Explorer menu guide",
                    Priority: DesktopToastPriority.ContextualGuide,
                    PersistOnceOnShow: true),
                dataDirectoryOverride: tempDataDir);
            Assert.False(shownSecond);

            // 4. SafetyWarning preempts ContextualGuide and is NEVER suppressed by dismissed file
            DesktopToastHudService.Shared.RecordHintDismissed(
                DesktopToastHudService.HintIdWin10MoveRemovedWarning,
                tempDataDir);
            bool safetyShown = DesktopToastHudService.Shared.TryShow(
                new DesktopToastRequest(
                    HintId: DesktopToastHudService.HintIdWin10MoveRemovedWarning,
                    Message: "Source file moved by app",
                    Severity: DesktopToastSeverity.Warning,
                    Priority: DesktopToastPriority.SafetyWarning),
                dataDirectoryOverride: tempDataDir);
            Assert.True(safetyShown);

            // 5. Clearing dismissed hints (used by RestoreDefaultPreferencesAsync) restores onboarding hints
            DesktopToastHudService.ClearDismissedHints(tempDataDir);
            DesktopToastHudService.Shared.NotifyWindowHidden();
            Assert.False(DesktopToastHudService.Shared.IsHintDismissed(
                DesktopToastHudService.HintIdWin10ExplorerMenu,
                tempDataDir));
        }
        finally
        {
            DesktopToastHudService.Shared.ResetForTests();
            TryDeleteDirectory(tempDataDir);
        }
    }

    [Fact]
    public void ContextualDragHudRules_MatchAllFiveScenariosWithoutFalsePositives()
    {
        var desktopDrop = new DropReleaseSnapshot(
            DropTargetWindowKind.ExplorerFileView,
            EscapePressed: false,
            default,
            IsDesktopShellView: true);
        var explorerFolderDrop = new DropReleaseSnapshot(
            DropTargetWindowKind.ExplorerFileView,
            EscapePressed: false,
            default,
            IsDesktopShellView: false);
        var explorerEsc = new DropReleaseSnapshot(
            DropTargetWindowKind.ExplorerFileView,
            EscapePressed: true,
            default,
            IsDesktopShellView: true);
        var wechatDrop = new DropReleaseSnapshot(
            DropTargetWindowKind.NonExplorerWindow,
            EscapePressed: false,
            default,
            IsDesktopShellView: false);

        // Taskbar is excluded while Desktop/Explorer folder & #32768 menu are included;
        // Desktop root window classes distinguish Desktop from CabinetWClass Explorer folders.
        Assert.True(DesktopToastHudInterop.IsExplorerTaskbarOrTrayClass("Shell_TrayWnd"));
        Assert.True(DesktopToastHudInterop.IsExplorerFileViewOrMenuClass("SHELLDLL_DefView"));
        Assert.True(DesktopToastHudInterop.IsExplorerFileViewOrMenuClass("#32768"));
        Assert.True(DesktopToastHudInterop.IsDesktopRootWindowClass("Progman"));
        Assert.True(DesktopToastHudInterop.IsDesktopRootWindowClass("WorkerW"));
        Assert.False(DesktopToastHudInterop.IsDesktopRootWindowClass("CabinetWClass"));

        // D3: Win10 no longer shows the one-click Default Move guide HUD.
        Assert.Null(FileSurfaceContent.EvaluateContextualDragOutGuideId(
            DataPackageOperation.Copy,
            SettingsService.ManagedDragOutActionFollowWindows,
            isWindows11OrLater: false,
            explorerFolderDrop,
            handledAsStackMembership: false,
            hasStorageItems: true,
            controlHeld: false,
            crossVolumeFromDesktop: false));
        Assert.Null(FileSurfaceContent.EvaluateContextualDragOutGuideId(
            DataPackageOperation.None,
            SettingsService.ManagedDragOutActionFollowWindows,
            isWindows11OrLater: false,
            explorerFolderDrop,
            handledAsStackMembership: false,
            hasStorageItems: true,
            controlHeld: false,
            crossVolumeFromDesktop: false));
        Assert.Null(FileSurfaceContent.EvaluateContextualDragOutGuideId(
            DataPackageOperation.Copy,
            SettingsService.ManagedDragOutActionFollowWindows,
            isWindows11OrLater: false,
            wechatDrop,
            handledAsStackMembership: false,
            hasStorageItems: true,
            controlHeld: false,
            crossVolumeFromDesktop: false));
        Assert.Null(FileSurfaceContent.EvaluateContextualDragOutGuideId(
            DataPackageOperation.Copy,
            SettingsService.ManagedDragOutActionFollowWindows,
            isWindows11OrLater: false,
            explorerEsc,
            handledAsStackMembership: false,
            hasStorageItems: true,
            controlHeld: false,
            crossVolumeFromDesktop: false));

        // Hint 3: Win11 FollowWindows cross-volume Copy onto Desktop ONLY (not CabinetWClass folder windows or Ctrl-drag)
        Assert.Equal(
            DesktopToastHudService.HintIdWin11CrossVolumeCopy,
            FileSurfaceContent.EvaluateContextualDragOutGuideId(
                DataPackageOperation.Copy,
                SettingsService.ManagedDragOutActionFollowWindows,
                isWindows11OrLater: true,
                desktopDrop,
                handledAsStackMembership: false,
                hasStorageItems: true,
                controlHeld: false,
                crossVolumeFromDesktop: true));
        Assert.Null(FileSurfaceContent.EvaluateContextualDragOutGuideId(
            DataPackageOperation.Copy,
            SettingsService.ManagedDragOutActionFollowWindows,
            isWindows11OrLater: true,
            explorerFolderDrop,
            handledAsStackMembership: false,
            hasStorageItems: true,
            controlHeld: false,
            crossVolumeFromDesktop: true));
        Assert.Null(FileSurfaceContent.EvaluateContextualDragOutGuideId(
            DataPackageOperation.Copy,
            SettingsService.ManagedDragOutActionFollowWindows,
            isWindows11OrLater: true,
            desktopDrop,
            handledAsStackMembership: false,
            hasStorageItems: true,
            controlHeld: true,
            crossVolumeFromDesktop: true));

        // Hint 4 & 5 monitor: Win10 Move onto non-Explorer window (not on Explorer or Esc)
        Assert.True(FileSurfaceContent.ShouldMonitorWin10MoveNonExplorerDrop(
            isWindows11OrLater: false,
            SettingsService.ManagedDragOutActionMove,
            DataPackageOperation.Move,
            handledAsStackMembership: false,
            wechatDrop));
        Assert.False(FileSurfaceContent.ShouldMonitorWin10MoveNonExplorerDrop(
            isWindows11OrLater: false,
            SettingsService.ManagedDragOutActionMove,
            DataPackageOperation.Move,
            handledAsStackMembership: false,
            desktopDrop));
    }

    [Fact]
    public void OrganizationHistoryRebase_UpdatesTargetAndReceiptPaths_AndPreservesUntamperedUndoAuthority()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), $"DeskBoxHistRebase-{Guid.NewGuid():N}");
        string oldDir = Path.Combine(tempRoot, "old-root", "Docs");
        string newDir = Path.Combine(tempRoot, "new-root", "Docs");
        Directory.CreateDirectory(oldDir);
        Directory.CreateDirectory(newDir);
        try
        {
            string oldUntouched = Path.Combine(oldDir, "report.txt");
            string newCopied = Path.Combine(newDir, "report.txt");
            File.WriteAllText(oldUntouched, "verified content");
            File.WriteAllText(newCopied, "verified content");

            string oldReplaced = Path.Combine(oldDir, "tampered.txt");
            string newReplacedCopy = Path.Combine(newDir, "tampered.txt");
            File.WriteAllText(oldReplaced, "original");
            var originalReplacedIdentity = FileService.CaptureUndoReceiptIdentity(oldReplaced);
            Assert.NotNull(originalReplacedIdentity);
            // Replace inode at oldReplaced before migration so original receipt no longer matches
            string replacementTmp = Path.Combine(oldDir, "tampered.tmp");
            File.WriteAllText(replacementTmp, "different inode");
            File.Delete(oldReplaced);
            File.Move(replacementTmp, oldReplaced);
            File.WriteAllText(newReplacedCopy, "different inode");
            Assert.False(FileService.UndoReceiptStillMatches(oldReplaced, originalReplacedIdentity));

            var untouchedIdentity = FileService.CaptureUndoReceiptIdentity(oldUntouched);
            Assert.NotNull(untouchedIdentity);

            var store = new DeskBox.FileSafety.DesktopOrganizationHistoryStore(
                Path.Combine(tempRoot, "history.json"));
            store.Entries.Add(new OrganizationHistoryEntry
            {
                WidgetId = "w-docs",
                ActionType = OrganizationActionType.DesktopOrganization,
                CanUndo = true,
                Targets =
                [
                    new OrganizationHistoryTarget
                    {
                        WidgetId = "w-docs",
                        WidgetName = "Docs",
                        DirectoryPath = oldDir,
                        WasCreated = true,
                    }
                ],
                Items =
                [
                    new OrganizationHistoryItem
                    {
                        Name = "report.txt",
                        SourcePath = Path.Combine(tempRoot, "Desktop", "report.txt"),
                        DestinationPath = oldUntouched,
                        TargetWidgetId = "w-docs",
                        DestinationIdentity = untouchedIdentity,
                    },
                    new OrganizationHistoryItem
                    {
                        Name = "tampered.txt",
                        SourcePath = Path.Combine(tempRoot, "Desktop", "tampered.txt"),
                        DestinationPath = oldReplaced,
                        TargetWidgetId = "w-docs",
                        DestinationIdentity = originalReplacedIdentity,
                    }
                ],
            });

            bool changed = store.RebaseWidgetDirectory(
                "w-docs",
                oldDir,
                newDir,
                refreshCopiedDestinationIdentities: true);
            Assert.True(changed);
            Assert.Equal(newDir, store.Entries[0].Targets[0].DirectoryPath);
            Assert.Equal(newCopied, store.Entries[0].Items[0].DestinationPath);
            Assert.True(FileService.UndoReceiptStillMatches(
                newCopied,
                store.Entries[0].Items[0].DestinationIdentity));

            // Tampered source never regains undo authority after migration
            Assert.Equal(newReplacedCopy, store.Entries[0].Items[1].DestinationPath);
            Assert.False(FileService.UndoReceiptStillMatches(
                newReplacedCopy,
                store.Entries[0].Items[1].DestinationIdentity));
        }
        finally
        {
            TryDeleteDirectory(tempRoot);
        }
    }

    [Fact]
    public async Task DesktopOrganizationScanner_ExcludesFolderContainingNestedJunction()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), $"DeskBoxScanJunction-{Guid.NewGuid():N}");
        string desktop = Path.Combine(tempRoot, "Desktop");
        string externalDir = Path.Combine(tempRoot, "ExternalSecret");
        string desktopFolder = Path.Combine(desktop, "Project");
        Directory.CreateDirectory(desktopFolder);
        Directory.CreateDirectory(externalDir);
        try
        {
            File.WriteAllText(Path.Combine(desktopFolder, "readme.txt"), "hello");
            File.WriteAllText(Path.Combine(externalDir, "secret.txt"), "do not move");
            string nestedJunction = Path.Combine(desktopFolder, "linked");
            Assert.True(TestJunction.TryCreate(nestedJunction, externalDir));

            var scanner = new DesktopOrganizationScanner(
                new DesktopOrganizationClassifier(),
                () => desktop,
                () => string.Empty);
            var scan = await scanner.ScanAsync(includeSlowItems: true);
            var folderSnapshot = Assert.Single(scan.Items, item => item.Name == "Project");
            Assert.Equal(DesktopOrganizationExclusionReason.ReparsePoint, folderSnapshot.ExclusionReason);
        }
        finally
        {
            TryDeleteDirectory(tempRoot);
        }
    }

    [Fact]
    public async Task ManagedTransferEngine_CopiesSharedWriteLockedFile_AndMovesReadOnlyFileChunked()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), $"DeskBoxLockAndReadOnly-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        try
        {
            // 1. Copy while another writer holds FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE (Office/Chrome/VSCode pattern).
            string lockedSrc = Path.Combine(tempRoot, "open-workbook.xlsx");
            string lockedCopyDst = Path.Combine(tempRoot, "copied-workbook.xlsx");
            await File.WriteAllBytesAsync(lockedSrc, Encoding.UTF8.GetBytes("live-shared-workbook-data"));

            await using (var writerLock = new FileStream(
                lockedSrc,
                FileMode.Open,
                FileAccess.ReadWrite,
                FileShare.ReadWrite | FileShare.Delete))
            {
                using var cts = new CancellationTokenSource();
                var fileService = new FileService();
                var results = await fileService.ExecuteTransferPlanAsync(
                    [new FileService.FileTransferPlan(lockedSrc, lockedCopyDst)],
                    move: false,
                    cancellationToken: cts.Token);
                Assert.Single(results);
                Assert.True(File.Exists(lockedCopyDst));
                Assert.Equal("live-shared-workbook-data", await File.ReadAllTextAsync(lockedCopyDst, Encoding.UTF8));
            }

            // 2. Chunked cross-volume move path clears FILE_ATTRIBUTE_READONLY on source cleanup while preserving destination attributes.
            string readOnlySrc = Path.Combine(tempRoot, "readonly-spec.txt");
            string readOnlyDst = Path.Combine(tempRoot, "moved-readonly-spec.txt");
            await File.WriteAllTextAsync(readOnlySrc, "immutable-content", Encoding.UTF8);
            File.SetAttributes(readOnlySrc, File.GetAttributes(readOnlySrc) | FileAttributes.ReadOnly);

            await FileService.MoveFileChunkedForTestAsync(readOnlySrc, readOnlyDst);

            Assert.False(File.Exists(readOnlySrc));
            Assert.True(File.Exists(readOnlyDst));
            Assert.Equal("immutable-content", await File.ReadAllTextAsync(readOnlyDst, Encoding.UTF8));
            Assert.True((File.GetAttributes(readOnlyDst) & FileAttributes.ReadOnly) != 0);
        }
        finally
        {
            TryDeleteDirectory(tempRoot);
        }
    }

    [Fact]
    public void PersistTemporaryDroppedFiles_EvacuatesVirtualDropsBeforeScopeCleanup()
    {
        string virtualBatchDir = Path.Combine(
            Path.GetTempPath(),
            "DeskBox",
            "VirtualDrops",
            $"test-{Guid.NewGuid():N}");
        string tempRoot = Path.Combine(Path.GetTempPath(), $"DeskBoxPersistDrops-{Guid.NewGuid():N}");
        string normalDir = Path.Combine(tempRoot, "normal");
        string durableDir = Path.Combine(tempRoot, "durable");
        Directory.CreateDirectory(virtualBatchDir);
        Directory.CreateDirectory(normalDir);
        Directory.CreateDirectory(durableDir);
        try
        {
            string virtualFile = Path.Combine(virtualBatchDir, "archive-extract.txt");
            string normalFile = Path.Combine(normalDir, "local-doc.txt");
            File.WriteAllText(virtualFile, "from-7zip-staging", Encoding.UTF8);
            File.WriteAllText(normalFile, "permanent-local", Encoding.UTF8);

            IReadOnlyList<string> persisted = DropStagingFileOps.PersistTemporaryDroppedFiles(
                [
                    new DroppedFilePath(virtualFile, "archive-extract.txt", ForceManagedCopy: true),
                    new DroppedFilePath(normalFile, "local-doc.txt", ForceManagedCopy: false),
                ],
                durableDir);

            // Simulate DroppedFileBatch.Dispose() wiping VirtualDrops immediately after drop handler returns.
            TryDeleteDirectory(virtualBatchDir);

            Assert.Equal(2, persisted.Count);
            Assert.NotEqual(Path.GetFullPath(virtualFile), Path.GetFullPath(persisted[0]), StringComparer.OrdinalIgnoreCase);
            Assert.StartsWith(Path.GetFullPath(durableDir), persisted[0], StringComparison.OrdinalIgnoreCase);
            Assert.True(File.Exists(persisted[0]));
            Assert.Equal("from-7zip-staging", File.ReadAllText(persisted[0], Encoding.UTF8));

            Assert.Equal(Path.GetFullPath(normalFile), Path.GetFullPath(persisted[1]), StringComparer.OrdinalIgnoreCase);
            Assert.True(File.Exists(persisted[1]));
        }
        finally
        {
            TryDeleteDirectory(virtualBatchDir);
            TryDeleteDirectory(tempRoot);
        }
    }

    [Fact]
    public void NativeShellFileDragProvider_SupportsCrossDirectorySelectionWithSourceGuard()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), $"DeskBoxMultiParentShellDrag-{Guid.NewGuid():N}");
        string dirA = Path.Combine(tempRoot, "FolderA");
        string dirB = Path.Combine(tempRoot, "FolderB");
        Directory.CreateDirectory(dirA);
        Directory.CreateDirectory(dirB);
        nint shellObj = 0;
        nint guardedObj = 0;
        try
        {
            string fileA = Path.Combine(dirA, "alpha.txt");
            string fileB = Path.Combine(dirB, "beta.txt");
            File.WriteAllText(fileA, "alpha", Encoding.UTF8);
            File.WriteAllText(fileB, "beta", Encoding.UTF8);

            string[] crossFolderSelection = [fileA, fileB];
            Assert.True(NativeShellFileDragProvider.CanAttachPaths(crossFolderSelection));

            shellObj = NativeShellFileDragProvider.CreateShellDataObject(crossFolderSelection);
            Assert.NotEqual(0, shellObj);
            Assert.True(NativeShellFileDragProvider.HasFileDropFormat(shellObj));

            guardedObj = FileDragSourceGuardDataObject.CreateInterfacePointer(shellObj);
            Assert.NotEqual(0, guardedObj);
            Assert.True(NativeShellFileDragProvider.HasFileDropFormat(guardedObj));
        }
        finally
        {
            FileDragSourceGuardDataObject.ReleaseInterfacePointer(guardedObj);
            NativeShellFileDragProvider.ReleaseInterface(shellObj);
            TryDeleteDirectory(tempRoot);
        }
    }

    [Fact]
    public void DesktopToastHudService_InvalidatesCacheOnExternalFileDelete_AndStripsCapturedClosures()
    {
        string tempDataDir = Path.Combine(Path.GetTempPath(), $"DeskBoxHudStamp-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDataDir);
        try
        {
            DesktopToastHudService.Shared.ResetForTests();
            DesktopToastHudService.Shared.RecordHintDismissed("test-hint-1", tempDataDir);
            Assert.True(DesktopToastHudService.Shared.IsHintDismissed("test-hint-1", tempDataDir));

            // Simulate out-of-process Settings host deleting dismissed-drag-hints.txt ("Restore Defaults").
            string dismissedFilePath = Path.Combine(tempDataDir, DesktopToastHudService.DismissedHintsFileName);
            Assert.True(File.Exists(dismissedFilePath));
            File.Delete(dismissedFilePath);

            Assert.False(DesktopToastHudService.Shared.IsHintDismissed("test-hint-1", tempDataDir));

            object heavyCapturedTarget = new byte[1024];
            bool accepted = DesktopToastHudService.Shared.TryShow(
                new DesktopToastRequest(
                    HintId: "closure-strip-check",
                    Message: "Drag safety notification",
                    Priority: DesktopToastPriority.ContextualGuide,
                    ActionButtonText: "Open",
                    OnActionAsync: () =>
                    {
                        GC.KeepAlive(heavyCapturedTarget);
                        return Task.CompletedTask;
                    },
                    OnDismissAsync: () =>
                    {
                        GC.KeepAlive(heavyCapturedTarget);
                        return Task.CompletedTask;
                    }),
                dataDirectoryOverride: tempDataDir);

            Assert.True(accepted);
            DesktopToastRequest? snapshot = DesktopToastHudService.Shared.LastEvaluatedRequestForTests;
            Assert.NotNull(snapshot);
            Assert.Null(snapshot.OnActionAsync);
            Assert.Null(snapshot.OnDismissAsync);
        }
        finally
        {
            DesktopToastHudService.Shared.ResetForTests();
            TryDeleteDirectory(tempDataDir);
        }
    }

    [Fact]
    public void VolumeFormatSupport_AllowsRefs_AndStoreMigrationRetriesWhenFileTemporarilyLocked()
    {
        Assert.True(ManagedStorageMigrationService.IsSupportedVolumeFormat("NTFS"));
        Assert.True(ManagedStorageMigrationService.IsSupportedVolumeFormat("ReFS"));
        Assert.True(ManagedStorageMigrationService.IsSupportedVolumeFormat("refs"));
        Assert.False(ManagedStorageMigrationService.IsSupportedVolumeFormat("exFAT"));
        Assert.False(ManagedStorageMigrationService.IsSupportedVolumeFormat("FAT32"));

        string tempRoot = Path.Combine(Path.GetTempPath(), $"DeskBoxStoreMigrateRetry-{Guid.NewGuid():N}");
        string legacyRoot = Path.Combine(tempRoot, "legacy");
        string durableRoot = Path.Combine(tempRoot, "durable");
        string legacyRecovery = Path.Combine(tempRoot, "legacy-recovery");
        string durableRecovery = Path.Combine(tempRoot, "durable-recovery");
        string legacyData = Path.Combine(legacyRoot, "data");
        string legacySubDir = Path.Combine(legacyData, "quick-capture");
        Directory.CreateDirectory(legacySubDir);
        Directory.CreateDirectory(legacyRecovery);
        try
        {
            string settingsJson = Path.Combine(legacyData, "settings.json");
            string lockedAsset = Path.Combine(legacySubDir, "locked-image.png");
            File.WriteAllText(settingsJson, "{}", Encoding.UTF8);
            File.WriteAllBytes(lockedAsset, [1, 2, 3, 4, 5]);

            string sentinelPath = Path.Combine(durableRoot, StoreUserAssetMigrationFileOps.MigrationSentinelFileName);
            string durableLockedAsset = Path.Combine(durableRoot, "data", "quick-capture", "locked-image.png");

            using (var exclusiveLock = new FileStream(lockedAsset, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                StoreUserAssetMigrationFileOps.EnsureStoreDurableRootInitialized(
                    legacyRoot,
                    durableRoot,
                    legacyRecovery,
                    durableRecovery);

                Assert.True(File.Exists(Path.Combine(durableRoot, "data", "settings.json")));
                Assert.False(File.Exists(durableLockedAsset));
                Assert.False(File.Exists(sentinelPath));
            }

            // Next launch after lock is released completes the remaining file and stamps sentinel.
            StoreUserAssetMigrationFileOps.EnsureStoreDurableRootInitialized(
                legacyRoot,
                durableRoot,
                legacyRecovery,
                durableRecovery);

            Assert.True(File.Exists(durableLockedAsset));
            Assert.True(File.Exists(sentinelPath));
        }
        finally
        {
            TryDeleteDirectory(tempRoot);
        }
    }

    [Fact]
    public void DesktopToastHudWindow_MatchesWindows11NativeHardwareIndicatorSpec()
    {
        Assert.Equal(8.0, DesktopToastHudWindow.CardCornerRadiusDip);
        Assert.Equal(4.0, DesktopToastHudWindow.ControlCornerRadiusDip);
        Assert.Equal(48.0, DesktopToastHudWindow.MinCardHeightDip);
        Assert.Equal(14.0, DesktopToastHudWindow.MessageFontSizeDip);
        Assert.Equal(20.0, DesktopToastHudWindow.MessageLineHeightDip);
        Assert.Equal(16.0, DesktopToastHudWindow.StatusIconFontSizeDip);
        Assert.Equal(16.0, DesktopToastHudWindow.HorizontalPaddingDip);
        Assert.Equal(10.0, DesktopToastHudWindow.VerticalPaddingDip);
        Assert.Equal(12.0, DesktopToastHudWindow.IconToMessageSpacingDip);

        // Linear slide-up/slide-down show & hide motion contract.
        Assert.Equal(16.0, DesktopToastHudWindow.ShowHideSlideDistanceDip);
        Assert.Equal(180, DesktopToastHudWindow.ShowAnimationDurationMs);
        Assert.Equal(150, DesktopToastHudWindow.HideAnimationDurationMs);

        // Fixed frosted-acrylic recipe (Thin at the stack popover's fixed-mode
        // strength): tint plus translucent opacities, never fully opaque.
        var darkRecipe = DesktopToastHudWindow.BuildFixedAcrylicRecipe(isDark: true);
        Assert.InRange(darkRecipe.TintOpacity, 0.0, 0.6);
        Assert.InRange(darkRecipe.LuminosityOpacity, 0.0, 0.7);
        var lightRecipe = DesktopToastHudWindow.BuildFixedAcrylicRecipe(isDark: false);
        Assert.InRange(lightRecipe.TintOpacity, 0.0, 0.6);
        Assert.InRange(lightRecipe.LuminosityOpacity, 0.0, 0.7);
        Assert.NotEqual(darkRecipe.TintColor, lightRecipe.TintColor);

        var darkInfo = DesktopToastHudWindow.ResolveFluentPalette(
            DesktopToastSeverity.Info,
            light: false,
            highContrast: false);
        Assert.Equal(0x2C, darkInfo.FallbackBackground.R);
        Assert.Equal(0x2C, darkInfo.FallbackBackground.G);
        Assert.Equal(0x2C, darkInfo.FallbackBackground.B);
        Assert.Equal(0xFF, darkInfo.PrimaryText.R);
        Assert.Equal(0xFF, darkInfo.PrimaryText.G);
        Assert.Equal(0xFF, darkInfo.PrimaryText.B);
        Assert.Equal(darkInfo.PrimaryText, darkInfo.StatusIcon);

        var darkSuccess = DesktopToastHudWindow.ResolveFluentPalette(
            DesktopToastSeverity.Success,
            light: false,
            highContrast: false);
        Assert.Equal(darkSuccess.PrimaryText, darkSuccess.StatusIcon);

        var darkWarning = DesktopToastHudWindow.ResolveFluentPalette(
            DesktopToastSeverity.Warning,
            light: false,
            highContrast: false);
        Assert.NotEqual(darkWarning.PrimaryText, darkWarning.StatusIcon);

        var lightInfo = DesktopToastHudWindow.ResolveFluentPalette(
            DesktopToastSeverity.Info,
            light: true,
            highContrast: false);
        Assert.Equal(0xF9, lightInfo.FallbackBackground.R);
        Assert.Equal(0xF9, lightInfo.FallbackBackground.G);
        Assert.Equal(0xF9, lightInfo.FallbackBackground.B);
        Assert.Equal(lightInfo.PrimaryText, lightInfo.StatusIcon);
    }

    [Fact]
    public void DesktopToastHudWindow_ResolvesFixedAcrylicSurface_WithSolidFallbacks()
    {        Assert.Equal(
            DesktopToastHudWindow.HudSurfaceKind.Acrylic,
            DesktopToastHudWindow.ResolveHudSurfaceKind(
                acrylicSupported: true,
                highContrast: false));
        Assert.Equal(
            DesktopToastHudWindow.HudSurfaceKind.Solid,
            DesktopToastHudWindow.ResolveHudSurfaceKind(
                acrylicSupported: false,
                highContrast: false));
        Assert.Equal(
            DesktopToastHudWindow.HudSurfaceKind.Solid,
            DesktopToastHudWindow.ResolveHudSurfaceKind(
                acrylicSupported: true,
                highContrast: true));
    }

    [Fact]
    public void DesktopToastHudService_AllToastsShowUniformFiveSeconds()
    {
        Assert.Equal(5000, DesktopToastHudService.StandardHintDurationMs);
        Assert.Equal(
            5000,
            DesktopToastHudService.ResolveEffectiveDurationMs(
                new DesktopToastRequest(HintId: "plain", Message: "m", DurationMs: 3600)));
        Assert.Equal(
            5000,
            DesktopToastHudService.ResolveEffectiveDurationMs(
                new DesktopToastRequest(
                    HintId: "action",
                    Message: "m",
                    ActionButtonText: "switch",
                    OnActionAsync: () => Task.CompletedTask,
                    DurationMs: 6500)));
    }

    [Fact]
    public void DesktopToastHudWindow_HideGuards_ProtectPendingFinalizeAndBlockStaleFinalize()
    {
        // Exit tick: only the pending session's finalize may run — a redundant
        // HideImmediate no longer bumps the session, so the armed session still
        // matches; a replaced toast (ShowToast bump) or destroy must not run.
        Assert.True(DesktopToastHudWindow.ExitTickShouldFinalize(6, 6, closed: false));
        Assert.False(DesktopToastHudWindow.ExitTickShouldFinalize(7, 6, closed: false));
        Assert.False(DesktopToastHudWindow.ExitTickShouldFinalize(6, 6, closed: true));

        Assert.True(DesktopToastHudWindow.FinalizeHideShouldRun(6, 6, isShown: false, closed: false));
        Assert.False(DesktopToastHudWindow.FinalizeHideShouldRun(7, 6, isShown: false, closed: false));
        Assert.False(DesktopToastHudWindow.FinalizeHideShouldRun(6, 6, isShown: true, closed: false));
        Assert.False(DesktopToastHudWindow.FinalizeHideShouldRun(6, 6, isShown: false, closed: true));
    }

    [Fact]
    public void DesktopToastHudWindow_ShowLogLine_RecordsLengthInsteadOfMessageContent()
    {
        string line = DesktopToastHudWindow.FormatShowLogLine(
            "quick-create-note",
            messageLength: 28,
            desiredWidth: 217.4,
            desiredHeight: 48.2,
            textWidth: 69.1,
            textHeight: 20.0);
        Assert.Contains("msgLen=28", line);
        Assert.DoesNotContain("msg='", line);
    }

    [Fact]
    public void DesktopToastHudWindow_RepaintsSolidOnlyWhenAttachFailedAfterPaintingAcrylic()
    {
        Assert.True(DesktopToastHudWindow.ShouldRepaintAfterAttachAttempt(
            attachFailedNow: true,
            paintedAcrylic: true));
        Assert.False(DesktopToastHudWindow.ShouldRepaintAfterAttachAttempt(
            attachFailedNow: true,
            paintedAcrylic: false));
        Assert.False(DesktopToastHudWindow.ShouldRepaintAfterAttachAttempt(
            attachFailedNow: false,
            paintedAcrylic: true));
        Assert.False(DesktopToastHudWindow.ShouldRepaintAfterAttachAttempt(
            attachFailedNow: false,
            paintedAcrylic: false));
    }

    [Fact]
    public void DesktopToastHudWindow_HighContrastRestoresStroke_RegularThemesStayBorderless()
    {
        Assert.Equal(1.0, DesktopToastHudWindow.ResolveCardBorderThicknessDip(highContrast: true));
        Assert.Equal(0.0, DesktopToastHudWindow.ResolveCardBorderThicknessDip(highContrast: false));

        var hc = DesktopToastHudWindow.ResolveFluentPalette(
            DesktopToastSeverity.Warning, light: false, highContrast: true);
        Assert.Equal(hc.PrimaryText, hc.Border);

        var dark = DesktopToastHudWindow.ResolveFluentPalette(
            DesktopToastSeverity.Info, light: false, highContrast: false);
        var lightPalette = DesktopToastHudWindow.ResolveFluentPalette(
            DesktopToastSeverity.Info, light: true, highContrast: false);
        Assert.Equal(Windows.UI.Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF), dark.Border);
        Assert.Equal(Windows.UI.Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF), lightPalette.Border);
    }

    [Fact]
    public void DragHudMessageLengths_StayWithinThreeLinePillBudget()
    {
        string stringsDir = TestPaths.FromRepository("src/DeskBox/Strings");
        string[] localeFiles = Directory.GetFiles(stringsDir, "*.json");
        Assert.Equal(13, localeFiles.Length);

        // MessageMaxLines=3 capacity, minus a safety margin. Scripts with
        // wider average glyphs get tighter caps; the default covers the rest.
        var caps = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["fr-FR"] = 150,
            ["es-ES"] = 155,
            ["de-DE"] = 155,
            ["ru-RU"] = 146,
            ["hi-IN"] = 150,
        };
        string[] messageKeys =
        [
            "Widget.DragHud.InboundCrossVolumeDesktopCopy",
            "Widget.DragHud.Win10MoveAppRemovedWarning",
            "Widget.DragHud.Win10DefaultCopy",
            "Widget.DragHud.Win11CrossVolumeCopy",
            "Widget.DragHud.Win10MoveRejected",
            "Widget.DragHud.ExternalCopyProtected",
        ];

        foreach (string file in localeFiles)
        {
            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(file));
            int cap = caps.TryGetValue(Path.GetFileNameWithoutExtension(file), out int tight)
                ? tight
                : 210;
            foreach (string key in messageKeys)
            {
                Assert.True(
                    doc.RootElement.TryGetProperty(key, out JsonElement el) &&
                    !string.IsNullOrWhiteSpace(el.GetString()) &&
                    el.GetString()!.Length <= cap,
                    $"{Path.GetFileName(file)} {key} exceeds the {cap}-char three-line budget");
            }
        }
    }

    [Fact]
    public void Win32Helper_ShellCloakBit_IgnoresAppCloakAndCatchesShellCloak()
    {
        // The HUD cloaks itself for hide animation/monitor seeding (APP bit);
        // only the SHELL bit means "stranded on another virtual desktop".
        Assert.False(Win32Helper.IsShellCloakBit(0));
        Assert.False(Win32Helper.IsShellCloakBit(1)); // DWM_CLOAKED_APP only
        Assert.True(Win32Helper.IsShellCloakBit(2)); // DWM_CLOAKED_SHELL
        Assert.True(Win32Helper.IsShellCloakBit(3)); // both
    }

    [Fact]
    public void DesktopToastHudWindow_AutomationNotification_MapsSeverity()
    {
        var warning = DesktopToastHudWindow.ResolveAutomationNotification(DesktopToastSeverity.Warning);
        Assert.Equal(
            Microsoft.UI.Xaml.Automation.Peers.AutomationNotificationProcessing.ImportantMostRecent,
            warning.Processing);

        var success = DesktopToastHudWindow.ResolveAutomationNotification(DesktopToastSeverity.Success);
        Assert.Equal(
            Microsoft.UI.Xaml.Automation.Peers.AutomationNotificationKind.ActionCompleted,
            success.Kind);

        var info = DesktopToastHudWindow.ResolveAutomationNotification(DesktopToastSeverity.Info);
        Assert.Equal(
            Microsoft.UI.Xaml.Automation.Peers.AutomationNotificationProcessing.MostRecent,
            info.Processing);
    }

    [Fact]
    public void ToastLifetimeMath_FreezesIdempotently_GrantsAtLeastGraceOnHoverExit()
    {
        long t0 = 100_000;
        ToastLifetimeState state = ToastLifetimeState.Start(t0, budgetMs: 5000, hardCapMs: 60_000);

        // Budget burns on the wall clock.
        Assert.Equal(4000, ToastLifetimeMath.RemainingMs(state, t0 + 1000));

        // Drag freeze: repeated pauses never double-count; remaining holds.
        state = ToastLifetimeMath.PauseForDrag(state, t0 + 1000);
        state = ToastLifetimeMath.PauseForDrag(state, t0 + 1040);
        Assert.Equal(4000, ToastLifetimeMath.RemainingMs(state, t0 + 4000));
        Assert.False(ToastLifetimeMath.IsHardCapped(state, t0 + 30_000));

        // Resume accumulates the frozen span exactly once; repeats are no-ops.
        state = ToastLifetimeMath.ResumeFromDrag(state, t0 + 3000);
        state = ToastLifetimeMath.ResumeFromDrag(state, t0 + 3100);
        Assert.Equal(4000, ToastLifetimeMath.RemainingMs(state, t0 + 3000));
        Assert.Equal(3000, ToastLifetimeMath.RemainingMs(state, t0 + 4000));

        // Hover exit early: never shorter than the remaining budget.
        Assert.Equal(3000, ToastLifetimeMath.HoverExitRemountMs(state, t0 + 4000, graceMs: 2200));
        // Hover exit after the budget burned: the fixed departure grace.
        Assert.Equal(2200, ToastLifetimeMath.HoverExitRemountMs(state, t0 + 9000, graceMs: 2200));

        // Lease: alive while budget (+pad) remains; hover overrides expiry.
        Assert.True(ToastLifetimeMath.IsLeaseAlive(state, t0 + 6000, windowHovered: false, leasePadMs: 250));
        Assert.False(ToastLifetimeMath.IsLeaseAlive(state, t0 + 7300, windowHovered: false, leasePadMs: 250));
        Assert.True(ToastLifetimeMath.IsLeaseAlive(state, t0 + 7300, windowHovered: true, leasePadMs: 250));

        // Hard cap counts total wall clock including freezes.
        Assert.True(ToastLifetimeMath.IsHardCapped(state, t0 + 60_000));

        // ResumeFromHover rebases the active lifetime to the departure grace
        // window so the priority lease remains alive across the 2200ms tail.
        ToastLifetimeState postHover = ToastLifetimeMath.ResumeFromHover(
            state,
            t0 + 9000,
            graceMs: 2200,
            out int remountMs);
        Assert.Equal(2200, remountMs);
        Assert.True(ToastLifetimeMath.IsLeaseAlive(postHover, t0 + 11_000, windowHovered: false, leasePadMs: 250));
        Assert.False(ToastLifetimeMath.IsLeaseAlive(postHover, t0 + 11_500, windowHovered: false, leasePadMs: 250));
    }

    [Fact]
    public void FormatHudSnippet_StripsExoticLineBreaksAndNeverSplitsSurrogatePairs()
    {
        // Exotic separators become spaces (same single-line semantics as CRLF),
        // never TextBlock line breaks.
        Assert.Equal(
            "a b c d",
            DeskBox.App.FormatHudSnippet("a\u2028b\u2029c\u0085d"));

        // 27 ASCII + a 2-code-unit emoji + trailing text: the pair must be
        // excluded whole, never split into an unpaired high surrogate.
        string withEmoji = new string('x', 27) + "\U0001F600" + "tail";
        string snippet = DeskBox.App.FormatHudSnippet(withEmoji);
        Assert.Equal(27, snippet.Length - 1); // 27 x's + ellipsis
        Assert.Equal('…', snippet[^1]);
        Assert.All(snippet, static ch => Assert.False(char.IsSurrogate(ch)));

        // Exactly 28 code units made of whole pairs stay intact.
        string paired = "\U0001F600" + new string('y', 26);
        Assert.Equal(paired, DeskBox.App.FormatHudSnippet(paired));

        // 29 plain ASCII truncates at 28 + ellipsis.
        Assert.Equal(new string('z', 28) + "…", DeskBox.App.FormatHudSnippet(new string('z', 29)));
        Assert.Equal(string.Empty, DeskBox.App.FormatHudSnippet("  \r\n  "));
    }

    [Fact]
    public void ShouldProbeHDropHintsOnHover_AllowsExplorerShellIdListWhileBlockingDelayedArchiveAndVirtualSources()
    {
        // 1. Standard Windows 10/11 Explorer & Desktop drag (CIDLData / SHCreateDataObject):
        //    always carries in-memory CF_HDROP + Shell IDList Array alongside
        //    FileGroupDescriptorW and IDataObjectAsyncCapability (isAsyncCapableSource=true).
        //    Must probe CF_HDROP on hover so cross-volume, insertion preview, and
        //    protected-source feedback match Drop!
        Assert.True(NativeDropTarget.ShouldProbeHDropHintsOnHover(
            hasFileData: true,
            hasVirtualFileData: false,
            hasVirtualDescriptorData: true,
            isAsyncCapableSource: true,
            hasShellIdListFormat: true,
            hasActiveDeskBoxDrag: false,
            isDevelopmentRoot: false));

        // 2. Active DeskBox-initiated drag (including Win10 SHCreateDataObject with
        //    FileGroupDescriptorW) -> always safe to probe in-memory CF_HDROP.
        Assert.True(NativeDropTarget.ShouldProbeHDropHintsOnHover(
            hasFileData: true,
            hasVirtualFileData: false,
            hasVirtualDescriptorData: true,
            isAsyncCapableSource: true,
            hasShellIdListFormat: false,
            hasActiveDeskBoxDrag: true,
            isDevelopmentRoot: false));

        // 3. Delayed-rendering archive/FTP drag (7-Zip, WinRAR, Bandizip, PeaZip, WinSCP):
        //    omits Shell IDList Array; must NEVER call GetData(CF_HDROP) on hover in retail builds.
        Assert.False(NativeDropTarget.ShouldProbeHDropHintsOnHover(
            hasFileData: true,
            hasVirtualFileData: false,
            hasVirtualDescriptorData: false,
            isAsyncCapableSource: false,
            hasShellIdListFormat: false,
            hasActiveDeskBoxDrag: false,
            isDevelopmentRoot: false));

        // 4. Non-Shell hybrid/virtual drag (New Outlook, WebView2, browsers):
        //    omits Shell IDList Array while advertising FileGroupDescriptorW or async capability.
        Assert.False(NativeDropTarget.ShouldProbeHDropHintsOnHover(
            hasFileData: true,
            hasVirtualFileData: false,
            hasVirtualDescriptorData: true,
            isAsyncCapableSource: false,
            hasShellIdListFormat: false,
            hasActiveDeskBoxDrag: false,
            isDevelopmentRoot: true));
        Assert.False(NativeDropTarget.ShouldProbeHDropHintsOnHover(
            hasFileData: true,
            hasVirtualFileData: false,
            hasVirtualDescriptorData: false,
            isAsyncCapableSource: true,
            hasShellIdListFormat: false,
            hasActiveDeskBoxDrag: false,
            isDevelopmentRoot: true));

        // 5. Pure virtual or non-file payloads -> never probe CF_HDROP.
        Assert.False(NativeDropTarget.ShouldProbeHDropHintsOnHover(
            hasFileData: true,
            hasVirtualFileData: true,
            hasVirtualDescriptorData: true,
            isAsyncCapableSource: false,
            hasShellIdListFormat: true,
            hasActiveDeskBoxDrag: false,
            isDevelopmentRoot: true));
        Assert.False(NativeDropTarget.ShouldProbeHDropHintsOnHover(
            hasFileData: false,
            hasVirtualFileData: false,
            hasVirtualDescriptorData: false,
            isAsyncCapableSource: false,
            hasShellIdListFormat: true,
            hasActiveDeskBoxDrag: true,
            isDevelopmentRoot: true));
    }

    [Fact]
    public void ResolveFeedbackEffect_MatchesDropExecutionAcrossCrossVolumeProtectedAndUnmappedCases()
    {
        const uint allEffects =
            NativeDropEffectPolicy.Copy |
            NativeDropEffectPolicy.Move |
            NativeDropEffectPolicy.Link;

        // 1. FollowWindows cross-volume (C:\Desktop -> E:\box): hover MUST report Copy (1), matching Drop!
        Assert.Equal(
            NativeDropEffectPolicy.Copy,
            NativeDropEffectPolicy.ResolveFeedbackEffect(
                hasFileData: true,
                hasVirtualFileData: false,
                keyState: 0,
                allowedEffects: allEffects,
                defaultMove: false,
                followWindows: true,
                sameVolume: false));
        Assert.True(NativeDropEffectPolicy.ShouldCopyMappedTransfer(
            containsTemporaryFiles: false,
            keyState: 0,
            defaultMove: false,
            followWindows: true,
            sameVolume: false));

        // 2. FollowWindows same-volume (C:\Desktop -> C:\box): hover MUST report Move (2), matching Drop!
        Assert.Equal(
            NativeDropEffectPolicy.Move,
            NativeDropEffectPolicy.ResolveFeedbackEffect(
                hasFileData: true,
                hasVirtualFileData: false,
                keyState: 0,
                allowedEffects: allEffects,
                defaultMove: false,
                followWindows: true,
                sameVolume: true));
        Assert.False(NativeDropEffectPolicy.ShouldCopyMappedTransfer(
            containsTemporaryFiles: false,
            keyState: 0,
            defaultMove: false,
            followWindows: true,
            sameVolume: true));

        // 3. FollowWindows cross-volume + Shift: explicit Shift overrides to Move (2).
        Assert.Equal(
            NativeDropEffectPolicy.Move,
            NativeDropEffectPolicy.ResolveFeedbackEffect(
                hasFileData: true,
                hasVirtualFileData: false,
                keyState: NativeDropEffectPolicy.ShiftKeyState,
                allowedEffects: allEffects,
                defaultMove: false,
                followWindows: true,
                sameVolume: false));

        // 4. Move-protected external source (Start Menu) masks off Move:
        //    unmodified -> Copy; Shift -> None (cannot move shared system shortcut).
        uint moveMaskedEffects = allEffects & ~NativeDropEffectPolicy.Move;
        Assert.Equal(
            NativeDropEffectPolicy.Copy,
            NativeDropEffectPolicy.ResolveFeedbackEffect(
                hasFileData: true,
                hasVirtualFileData: false,
                keyState: 0,
                allowedEffects: moveMaskedEffects,
                defaultMove: true,
                followWindows: false,
                sameVolume: true,
                protectDefaultMoveAsCopy: true));
        Assert.Equal(
            NativeDropEffectPolicy.None,
            NativeDropEffectPolicy.ResolveFeedbackEffect(
                hasFileData: true,
                hasVirtualFileData: false,
                keyState: NativeDropEffectPolicy.ShiftKeyState,
                allowedEffects: moveMaskedEffects,
                defaultMove: true,
                followWindows: false,
                sameVolume: true,
                protectDefaultMoveAsCopy: true));

        // 5. Unmapped File Box (hasMappedFolder: false): unmodified reports Link (or Copy fallback), never None!
        Assert.Equal(
            NativeDropEffectPolicy.Link,
            NativeDropEffectPolicy.ResolveFeedbackEffect(
                hasFileData: true,
                hasVirtualFileData: false,
                keyState: 0,
                allowedEffects: allEffects,
                defaultMove: true,
                hasMappedFolder: false));
        Assert.Equal(
            NativeDropEffectPolicy.Copy,
            NativeDropEffectPolicy.ResolveFeedbackEffect(
                hasFileData: true,
                hasVirtualFileData: false,
                keyState: 0,
                allowedEffects: NativeDropEffectPolicy.Copy | NativeDropEffectPolicy.Move,
                defaultMove: true,
                hasMappedFolder: false));

        // 6. ResolveSnapshotForcedIntent never forces Move when source is Start Menu.
        string publicDesktop = Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu);
        if (!string.IsNullOrWhiteSpace(publicDesktop))
        {
            Assert.Equal(
                FileDropIntent.None,
                ContentWidgetWindow.ResolveSnapshotForcedIntent(
                    ctrlDown: false,
                    shiftDown: true,
                    altDown: false,
                    allowedEffects: allEffects,
                    containsTemporaryFiles: false,
                    sourcePaths: [Path.Combine(publicDesktop, "SharedBrowser.lnk")]));
        }
    }

    [Fact]
    public async Task InboundCrossVolumeDesktopCopyGuide_FiresOnlyOnUnmodifiedFollowWindowsCrossDriveUserDesktopDrop_AndSwitchesDefaultMove()
    {
        string systemRoot = Path.GetPathRoot(Environment.SystemDirectory) ?? @"C:\";
        char sysDriveLetter = char.ToUpperInvariant(systemRoot[0]);
        char otherDriveLetter = sysDriveLetter == 'Z' ? 'Y' : 'Z';
        string fakeUserDesktop = Path.Combine(systemRoot, "DeskBoxFakeUser", "Desktop");
        string desktopVideo = Path.Combine(fakeUserDesktop, "videoplayback.mp4");
        string crossVolumeBox = $"{otherDriveLetter}:\\box\\综合";
        string sameVolumeBox = Path.Combine(systemRoot, "box", "综合");

        // 1. Unmodified FollowWindows drop from User Desktop (C:) to cross-volume Box (Z:) -> Copy triggers B1 guide!
        Assert.True(FileSurfaceContent.ShouldShowInboundCrossVolumeDesktopCopyGuide(
            sourcePaths: [desktopVideo],
            isDeskBoxOwnedSource: false,
            containsTemporaryFiles: false,
            resolvedIntent: FileDropIntent.Copy,
            managedDropAction: SettingsService.ManagedDropActionFollowWindows,
            destinationFolderPath: crossVolumeBox,
            controlHeld: false,
            shiftHeld: false,
            altHeld: false,
            userDesktopPathOverride: fakeUserDesktop));

        // 2. Same-volume User Desktop (C:) -> Box (C:) -> does NOT trigger.
        Assert.False(FileSurfaceContent.ShouldShowInboundCrossVolumeDesktopCopyGuide(
            sourcePaths: [desktopVideo],
            isDeskBoxOwnedSource: false,
            containsTemporaryFiles: false,
            resolvedIntent: FileDropIntent.Copy,
            managedDropAction: SettingsService.ManagedDropActionFollowWindows,
            destinationFolderPath: sameVolumeBox,
            controlHeld: false,
            shiftHeld: false,
            altHeld: false,
            userDesktopPathOverride: fakeUserDesktop));

        // 3. Explicit Ctrl / right-button choice -> does NOT trigger.
        Assert.False(FileSurfaceContent.ShouldShowInboundCrossVolumeDesktopCopyGuide(
            sourcePaths: [desktopVideo],
            isDeskBoxOwnedSource: false,
            containsTemporaryFiles: false,
            resolvedIntent: FileDropIntent.Copy,
            managedDropAction: SettingsService.ManagedDropActionFollowWindows,
            destinationFolderPath: crossVolumeBox,
            controlHeld: true,
            shiftHeld: false,
            altHeld: false,
            userDesktopPathOverride: fakeUserDesktop));

        // 4. ManagedDropAction == Move or Copy -> does NOT trigger.
        Assert.False(FileSurfaceContent.ShouldShowInboundCrossVolumeDesktopCopyGuide(
            sourcePaths: [desktopVideo],
            isDeskBoxOwnedSource: false,
            containsTemporaryFiles: false,
            resolvedIntent: FileDropIntent.Copy,
            managedDropAction: SettingsService.ManagedDropActionMove,
            destinationFolderPath: crossVolumeBox,
            controlHeld: false,
            shiftHeld: false,
            altHeld: false,
            userDesktopPathOverride: fakeUserDesktop));

        // 5. DeskBox self-drag or non-Desktop folder (Downloads) -> does NOT trigger; an eligible desktop path does.
        Assert.False(FileSurfaceContent.ShouldShowInboundCrossVolumeDesktopCopyGuide(
            sourcePaths: [desktopVideo],
            isDeskBoxOwnedSource: true,
            containsTemporaryFiles: false,
            resolvedIntent: FileDropIntent.Copy,
            managedDropAction: SettingsService.ManagedDropActionFollowWindows,
            destinationFolderPath: crossVolumeBox,
            controlHeld: false,
            shiftHeld: false,
            altHeld: false,
            userDesktopPathOverride: fakeUserDesktop));
        Assert.False(FileSurfaceContent.ShouldShowInboundCrossVolumeDesktopCopyGuide(
            sourcePaths: [Path.Combine(systemRoot, "DeskBoxFakeUser", "Downloads", "videoplayback.mp4")],
            isDeskBoxOwnedSource: false,
            containsTemporaryFiles: false,
            resolvedIntent: FileDropIntent.Copy,
            managedDropAction: SettingsService.ManagedDropActionFollowWindows,
            destinationFolderPath: crossVolumeBox,
            controlHeld: false,
            shiftHeld: false,
            altHeld: false,
            userDesktopPathOverride: fakeUserDesktop));

        string publicDesktop = Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory);
        if (!string.IsNullOrWhiteSpace(publicDesktop))
        {
            Assert.True(FileSurfaceContent.ShouldShowInboundCrossVolumeDesktopCopyGuide(
                sourcePaths: [Path.Combine(publicDesktop, "SharedApp.lnk")],
                isDeskBoxOwnedSource: false,
                containsTemporaryFiles: false,
                resolvedIntent: FileDropIntent.Copy,
                managedDropAction: SettingsService.ManagedDropActionFollowWindows,
                destinationFolderPath: crossVolumeBox,
                controlHeld: false,
                shiftHeld: false,
                altHeld: false,
                userDesktopPathOverride: publicDesktop));
        }

        // 6. One-time HUD persistence + SwitchDropActionAsync persists ManagedDropActionMove.
        string tempSettingsRoot = Path.Combine(Path.GetTempPath(), $"DeskBoxB1Hud-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempSettingsRoot);
        try
        {
            DesktopToastHudService.Shared.ResetForTests();
            var settingsService = new SettingsService(tempSettingsRoot);
            await settingsService.LoadAsync();
            settingsService.Settings.FileWidget.ManagedDropAction =
                SettingsService.ManagedDropActionFollowWindows;
            await settingsService.SaveAsync();

            bool firstShown = DesktopToastHudService.Shared.TryShow(
                new DesktopToastRequest(
                    HintId: DesktopToastHudService.HintIdInboundCrossVolumeDesktopCopy,
                    Message: "Cross-volume desktop copy guide",
                    Severity: DesktopToastSeverity.Info,
                    Priority: DesktopToastPriority.ContextualGuide,
                    PersistOnceOnShow: true),
                settingsService,
                dataDirectoryOverride: tempSettingsRoot);
            Assert.True(firstShown);

            // Second trigger is suppressed by PersistOnceOnShow.
            DesktopToastHudService.Shared.NotifyWindowHidden();
            bool secondShown = DesktopToastHudService.Shared.TryShow(
                new DesktopToastRequest(
                    HintId: DesktopToastHudService.HintIdInboundCrossVolumeDesktopCopy,
                    Message: "Cross-volume desktop copy guide",
                    Severity: DesktopToastSeverity.Info,
                    Priority: DesktopToastPriority.ContextualGuide,
                    PersistOnceOnShow: true),
                settingsService,
                dataDirectoryOverride: tempSettingsRoot);
            Assert.False(secondShown);

            await DesktopToastHudService.SwitchDropActionAsync(
                settingsService,
                SettingsService.ManagedDropActionMove);
            Assert.Equal(
                SettingsService.ManagedDropActionMove,
                settingsService.Settings.FileWidget.ManagedDropAction);
            Assert.Equal(
                SettingsService.ManagedDropActionMove,
                settingsService.Settings.FileWidget.ManagedDragOutAction);

            var reloaded = new SettingsService(tempSettingsRoot);
            await reloaded.LoadAsync();
            Assert.Equal(
                SettingsService.ManagedDropActionMove,
                reloaded.Settings.FileWidget.ManagedDropAction);
            Assert.Equal(
                SettingsService.ManagedDropActionMove,
                reloaded.Settings.FileWidget.ManagedDragOutAction);
        }
        finally
        {
            DesktopToastHudService.Shared.ResetForTests();
            TryDeleteDirectory(tempSettingsRoot);
        }
    }

    [Fact]
    public void DragOutModifierTip_IncludesOpenSettingsActionButtonAndDeepLinksToDragBehaviorSettings()
    {
        DesktopToastRequest request = FileSurfaceContent.CreateDragOutModifierTipRequest(
            "拖动时按住 Shift 可移动文件，按住 Ctrl 可复制文件",
            "更改设置");

        Assert.Equal("drag-out-modifier-tip", request.HintId);
        Assert.Equal("拖动时按住 Shift 可移动文件，按住 Ctrl 可复制文件", request.Message);
        Assert.Equal("更改设置", request.ActionButtonText);
        Assert.Equal(
            (Func<Task>)DesktopToastHudService.OpenDragBehaviorSettingsAsync,
            request.OnActionAsync);
        Assert.Equal(4500, request.DurationMs);
        Assert.Equal(
            "AppearanceDetail@Settings.DragBehavior.Group.Title",
            DesktopToastHudService.DragBehaviorSettingsDeepLink);
        Assert.Equal(
            "AppearanceDetail@Settings.DragBehavior.Group.Title",
            DesktopToastHudService.DropBehaviorSettingsDeepLink);
    }

    [Fact]
    public async Task ExactHistoryIdUndo_UnwindsBoundMoveBatchEvenAfterNewerMove()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), $"DeskBoxExactUndo-{Guid.NewGuid():N}");
        string sourceDir = Path.Combine(tempRoot, "Source");
        string boxADir = Path.Combine(tempRoot, "BoxA");
        string boxBDir = Path.Combine(tempRoot, "BoxB");
        Directory.CreateDirectory(sourceDir);
        Directory.CreateDirectory(boxADir);
        Directory.CreateDirectory(boxBDir);

        try
        {
            string fileA = Path.Combine(sourceDir, "first.txt");
            string fileB = Path.Combine(sourceDir, "second.txt");
            await File.WriteAllTextAsync(fileA, "first-batch");
            await File.WriteAllTextAsync(fileB, "second-batch");

            var settingsService = new SettingsService(tempRoot);
            await settingsService.LoadAsync();
            var fileService = new FileService();
            var organizer = TestOrganizerServices.Create(
                settingsService,
                fileService,
                desktopPathProvider: () => sourceDir,
                recoveryJournalPath: Path.Combine(tempRoot, "recovery.json"));

            var widgetA = new WidgetConfig
            {
                Id = "widget-a",
                Name = "Box A",
                MappedFolderPath = boxADir,
            };
            var widgetB = new WidgetConfig
            {
                Id = "widget-b",
                Name = "Box B",
                MappedFolderPath = boxBDir,
            };

            OrganizerOperationResult firstMove = await organizer.OrganizeDropAsync(
                widgetA,
                widgetA.Name,
                [fileA],
                move: true,
                useShellProgress: false);
            string? firstHistoryId = firstMove.History is { CanUndo: true } firstHistory
                ? firstHistory.Id
                : null;
            Assert.False(string.IsNullOrWhiteSpace(firstHistoryId));

            // Subsequent move into another widget creates a newer undoable entry.
            OrganizerOperationResult secondMove = await organizer.OrganizeDropAsync(
                widgetB,
                widgetB.Name,
                [fileB],
                move: true,
                useShellProgress: false);
            Assert.NotNull(secondMove.History);
            Assert.NotEqual(firstHistoryId, secondMove.History!.Id);

            // Binding the HUD undo action to firstHistoryId must undo ONLY the
            // first batch and leave the newer second batch untouched.
            await organizer.UndoAsync(firstHistoryId!);
            Assert.True(File.Exists(fileA));
            Assert.False(File.Exists(Path.Combine(boxADir, "first.txt")));
            Assert.True(File.Exists(Path.Combine(boxBDir, "second.txt")));

            // Reflection contract checks for consolidated HUD helpers & partial move return type.
            var surfaceType = typeof(FileSurfaceContent);
            var nonPublicInstance = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
            Assert.Single(
                surfaceType.GetMethods(nonPublicInstance),
                m => m.Name == "TryShowInboundDropHudReceipt");
            Assert.Single(
                surfaceType.GetMethods(nonPublicInstance),
                m => m.Name == "TryShowContextualDragOutGuide");
            var partialHelper = surfaceType.GetMethod(
                "HandlePartialSubfolderMoveCompletionAsync",
                nonPublicInstance);
            Assert.NotNull(partialHelper);
            Assert.Equal(typeof(Task<string?>), partialHelper!.ReturnType);
            Assert.Null(typeof(DesktopToastHudService).GetField(
                "_activeExpiresTick64",
                nonPublicInstance));
        }
        finally
        {
            TryDeleteDirectory(tempRoot);
        }
    }

    [Fact]
    public void SubfolderDropHudReceipt_AllFourEntryPointsRouteThroughSingleHelper()
    {
        var surfaceType = typeof(FileSurfaceContent);
        const BindingFlags DeclaredMembers =
            BindingFlags.Public | BindingFlags.NonPublic |
            BindingFlags.Instance | BindingFlags.Static |
            BindingFlags.DeclaredOnly;

        MethodInfo? receiptHelper = surfaceType.GetMethod(
            "TryShowSubfolderDropHudReceipt",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(receiptHelper);

        var typesToScan = new Stack<Type>();
        typesToScan.Push(surfaceType);
        var callSiteOwnerNames = new List<string>();
        while (typesToScan.Count > 0)
        {
            Type currentType = typesToScan.Pop();
            foreach (Type nested in currentType.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic))
            {
                typesToScan.Push(nested);
            }

            IEnumerable<MethodBase> members = currentType.GetMethods(DeclaredMembers)
                .Cast<MethodBase>()
                .Concat(currentType.GetConstructors(DeclaredMembers));
            foreach (MethodBase member in members)
            {
                foreach (MethodBase called in ReadDirectIlCalls(member))
                {
                    if (called.DeclaringType == surfaceType &&
                        called.MetadataToken == receiptHelper!.MetadataToken)
                    {
                        callSiteOwnerNames.Add($"{currentType.Name}::{member.Name}");
                    }
                }
            }
        }

        Assert.Equal(5, callSiteOwnerNames.Count);
        Assert.Equal(2, callSiteOwnerNames.Count(static s => s.Contains("<ItemSurface_Drop>", StringComparison.Ordinal)));
        Assert.Equal(1, callSiteOwnerNames.Count(static s => s.Contains("<ImportNativeDroppedFilesAsync>", StringComparison.Ordinal)));
        Assert.Equal(1, callSiteOwnerNames.Count(static s => s.Contains("<HandleFolderNavigationButtonDropAsync>", StringComparison.Ordinal)));
        Assert.Equal(1, callSiteOwnerNames.Count(static s => s.Contains("<ExecuteElevatedAsyncDropCommitAsync>", StringComparison.Ordinal)));
    }

    private static readonly IReadOnlyDictionary<short, OpCode> IlOpCodes = typeof(OpCodes)
        .GetFields(BindingFlags.Public | BindingFlags.Static)
        .Where(static field => field.FieldType == typeof(OpCode))
        .Select(static field => (OpCode)field.GetValue(null)!)
        .ToDictionary(static code => code.Value);

    private static IEnumerable<MethodBase> ReadDirectIlCalls(MethodBase method)
    {
        MethodBody? body;
        try
        {
            body = method.GetMethodBody();
        }
        catch
        {
            yield break;
        }

        byte[]? il = body?.GetILAsByteArray();
        if (il is not { Length: > 0 })
        {
            yield break;
        }

        Type? declaringType = method.DeclaringType;
        Type[] genericTypeArgs = declaringType is { IsGenericType: true }
            ? declaringType.GetGenericArguments()
            : Type.EmptyTypes;
        Type[] genericMethodArgs = method is MethodInfo { IsGenericMethod: true } mi
            ? mi.GetGenericArguments()
            : Type.EmptyTypes;

        int offset = 0;
        while (offset < il.Length)
        {
            short code = il[offset++];
            if (code == 0xFE)
            {
                if (offset >= il.Length)
                {
                    yield break;
                }

                code = unchecked((short)(0xFE00 | il[offset++]));
            }

            if (!IlOpCodes.TryGetValue(code, out OpCode op))
            {
                yield break;
            }

            if (op.OperandType == OperandType.InlineMethod &&
                (op == OpCodes.Call || op == OpCodes.Callvirt || op == OpCodes.Newobj) &&
                offset + 4 <= il.Length)
            {
                int token = BinaryPrimitives.ReadInt32LittleEndian(il.AsSpan(offset, 4));
                MethodBase? resolved = null;
                try
                {
                    resolved = method.Module.ResolveMethod(token, genericTypeArgs, genericMethodArgs);
                }
                catch
                {
                }

                if (resolved is not null)
                {
                    yield return resolved;
                }
            }

            offset += op.OperandType switch
            {
                OperandType.InlineNone => 0,
                OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
                OperandType.InlineVar => 2,
                OperandType.InlineI8 or OperandType.InlineR => 8,
                OperandType.InlineSwitch => offset + 4 <= il.Length
                    ? 4 + (4 * BinaryPrimitives.ReadInt32LittleEndian(il.AsSpan(offset, 4)))
                    : il.Length - offset,
                _ => 4
            };
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                foreach (string file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
                {
                    FileAttributes attrs = File.GetAttributes(file);
                    if ((attrs & FileAttributes.ReadOnly) != 0)
                    {
                        File.SetAttributes(file, attrs & ~FileAttributes.ReadOnly);
                    }
                }

                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
        }
    }
}
