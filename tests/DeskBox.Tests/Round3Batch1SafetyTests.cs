using DeskBox.Controls;
using DeskBox.Controls.WidgetContents;
using DeskBox.Helpers;
using DeskBox.Models;
using DeskBox.Platform;
using DeskBox.Services;
using DeskBox.ViewModels;
using Windows.ApplicationModel.DataTransfer;

namespace DeskBox.Tests;

public sealed class Round3Batch1SafetyTests
{
    [Fact]
    public void BundledAndInstalledExecutables_DefaultToSmartShortcut_WhileCtrlCopiesAndSetupExeMoves()
    {
        string tempRoot = CreateTempTestDir();
        string nonTempSandbox = Directory.CreateDirectory(
            Path.Combine(AppContext.BaseDirectory, "_r3b1_portable_" + Guid.NewGuid().ToString("N"))).FullName;
        try
        {
            PathOriginClassifier.ResetCachesForTests();
            string portableDir = Directory.CreateDirectory(Path.Combine(nonTempSandbox, "PixPin")).FullName;
            string portableExe = Path.Combine(portableDir, "PixPin.exe");
            string companionDll = Path.Combine(portableDir, "PixPinCore.dll");
            File.WriteAllBytes(portableExe, [0x4D, 0x5A]);
            File.WriteAllBytes(companionDll, [0x4D, 0x5A]);

            // Ephemeral %TEMP% archive extraction must NOT default to .lnk (which would break when archiver closes)
            string tempArchiveDir = Directory.CreateDirectory(Path.Combine(tempRoot, "Rar$EXa1234")).FullName;
            string tempArchiveExe = Path.Combine(tempArchiveDir, "PixPin.exe");
            File.WriteAllBytes(tempArchiveExe, [0x4D, 0x5A]);
            File.WriteAllBytes(Path.Combine(tempArchiveDir, "PixPinCore.dll"), [0x4D, 0x5A]);

            string downloadsLikeDir = Directory.CreateDirectory(Path.Combine(nonTempSandbox, "LooseFiles")).FullName;
            string installerExe = Path.Combine(downloadsLikeDir, "DeskBox_Setup_x64.exe");
            string singleToolExe = Path.Combine(downloadsLikeDir, "rufus.exe");
            File.WriteAllBytes(installerExe, [0x4D, 0x5A]);
            File.WriteAllBytes(singleToolExe, [0x4D, 0x5A]);

            string localProgramsExe = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Programs",
                "Antigravity",
                "Antigravity.exe");

            Assert.True(DriveMediaInterop.IsInstalledOrBundledExecutablePath(localProgramsExe, _ => false));
            Assert.True(DriveMediaInterop.IsInstalledOrBundledExecutablePath(@"D:\Program Files\Demo\Demo.exe", _ => false));
            Assert.True(DriveMediaInterop.IsInstalledOrBundledExecutablePath(portableExe, _ => false));
            Assert.False(DriveMediaInterop.IsInstalledOrBundledExecutablePath(tempArchiveExe, _ => false));
            Assert.False(DriveMediaInterop.IsInstalledOrBundledExecutablePath(installerExe, _ => false));
            Assert.False(DriveMediaInterop.IsInstalledOrBundledExecutablePath(singleToolExe, _ => false));

            SourceOriginSnapshot origin = PathOriginClassifier.ClassifySources([portableExe]);
            Assert.True(origin.PrefersShortcutByDefault);
            Assert.False(origin.IsMoveProtectedSystemOrSharedSource);

            SourceOriginSnapshot installedOrigin = PathOriginClassifier.ClassifySources([localProgramsExe]);
            Assert.True(installedOrigin.PrefersShortcutByDefault);
            Assert.True(installedOrigin.IsMoveProtectedSystemOrSharedSource);

            // Unmodified external drop -> Smart Shortcut (Link)
            TransferIntentDecision unmodified = TransferIntentEngine.Evaluate(new TransferIntentRequest(
                Domain: TransferDomain.ExternalInbound,
                TargetKind: TransferTargetKind.DirectorySurface,
                HasFileData: true,
                HasVirtualFileData: false,
                HasShellApplicationOrNamespaceData: false,
                ForceCopy: false,
                HasMappedFolder: true,
                ControlDown: false,
                ShiftDown: false,
                AltDown: false,
                AllowedEffects: NativeDropEffectPolicy.Copy | NativeDropEffectPolicy.Move | NativeDropEffectPolicy.Link,
                NoOperationMetadata: false,
                DefaultMove: true,
                FollowWindows: true,
                SameVolume: true,
                CanLink: true,
                SourceOrigin: origin));
            Assert.Equal(FileDropIntent.Shortcut, unmodified.Intent);
            Assert.Equal(NativeDropEffectPolicy.Link, unmodified.FeedbackEffect);

            // Explicit Ctrl -> Copy
            TransferIntentDecision ctrlCopy = TransferIntentEngine.Evaluate(new TransferIntentRequest(
                Domain: TransferDomain.ExternalInbound,
                TargetKind: TransferTargetKind.DirectorySurface,
                HasFileData: true,
                HasVirtualFileData: false,
                HasShellApplicationOrNamespaceData: false,
                ForceCopy: false,
                HasMappedFolder: true,
                ControlDown: true,
                ShiftDown: false,
                AltDown: false,
                AllowedEffects: NativeDropEffectPolicy.Copy | NativeDropEffectPolicy.Move | NativeDropEffectPolicy.Link,
                NoOperationMetadata: false,
                DefaultMove: true,
                FollowWindows: true,
                SameVolume: true,
                CanLink: true,
                SourceOrigin: origin));
            Assert.Equal(FileDropIntent.Copy, ctrlCopy.Intent);

            // Explicit Shift on user-folder portable .exe -> Move allowed, while installed system/program .exe -> Blocked (None)
            TransferIntentDecision shiftPortableMove = TransferIntentEngine.Evaluate(new TransferIntentRequest(
                Domain: TransferDomain.ExternalInbound,
                TargetKind: TransferTargetKind.DirectorySurface,
                HasFileData: true,
                HasVirtualFileData: false,
                HasShellApplicationOrNamespaceData: false,
                ForceCopy: false,
                HasMappedFolder: true,
                ControlDown: false,
                ShiftDown: true,
                AltDown: false,
                AllowedEffects: NativeDropEffectPolicy.Copy | NativeDropEffectPolicy.Move | NativeDropEffectPolicy.Link,
                NoOperationMetadata: false,
                DefaultMove: true,
                FollowWindows: true,
                SameVolume: true,
                CanLink: true,
                SourceOrigin: origin));
            Assert.Equal(FileDropIntent.Move, shiftPortableMove.Intent);

            TransferIntentDecision shiftInstalledBlocked = TransferIntentEngine.Evaluate(new TransferIntentRequest(
                Domain: TransferDomain.ExternalInbound,
                TargetKind: TransferTargetKind.DirectorySurface,
                HasFileData: true,
                HasVirtualFileData: false,
                HasShellApplicationOrNamespaceData: false,
                ForceCopy: false,
                HasMappedFolder: true,
                ControlDown: false,
                ShiftDown: true,
                AltDown: false,
                AllowedEffects: NativeDropEffectPolicy.Copy | NativeDropEffectPolicy.Move | NativeDropEffectPolicy.Link,
                NoOperationMetadata: false,
                DefaultMove: true,
                FollowWindows: true,
                SameVolume: true,
                CanLink: true,
                SourceOrigin: installedOrigin));
            Assert.Equal(FileDropIntent.None, shiftInstalledBlocked.Intent);
        }
        finally
        {
            PathOriginClassifier.ResetCachesForTests();
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }

            if (Directory.Exists(nonTempSandbox))
            {
                Directory.Delete(nonTempSandbox, recursive: true);
            }
        }
    }

    [Fact]
    public void UnsupportedAndAssociationSurfaces_NeverReportExternalMoveEffect()
    {
        TransferIntentDecision weatherDrop = TransferIntentEngine.Evaluate(new TransferIntentRequest(
            Domain: TransferDomain.ExternalInbound,
            TargetKind: TransferTargetKind.UnsupportedSurface,
            HasFileData: true,
            HasVirtualFileData: false,
            HasShellApplicationOrNamespaceData: false,
            ForceCopy: false,
            HasMappedFolder: false,
            ControlDown: false,
            ShiftDown: true,
            AltDown: false,
            AllowedEffects: NativeDropEffectPolicy.Copy | NativeDropEffectPolicy.Move | NativeDropEffectPolicy.Link,
            NoOperationMetadata: false,
            DefaultMove: true,
            FollowWindows: true,
            SameVolume: true));
        Assert.Equal(FileDropIntent.None, weatherDrop.Intent);
        Assert.Equal(NativeDropEffectPolicy.None, weatherDrop.FeedbackEffect);

        TransferIntentDecision todoShiftDrop = TransferIntentEngine.Evaluate(new TransferIntentRequest(
            Domain: TransferDomain.ExternalInbound,
            TargetKind: TransferTargetKind.AssociationOnlySurface,
            HasFileData: true,
            HasVirtualFileData: false,
            HasShellApplicationOrNamespaceData: false,
            ForceCopy: false,
            HasMappedFolder: false,
            ControlDown: false,
            ShiftDown: true,
            AltDown: false,
            AllowedEffects: NativeDropEffectPolicy.Copy | NativeDropEffectPolicy.Move,
            NoOperationMetadata: false,
            DefaultMove: true,
            FollowWindows: true,
            SameVolume: true));
        Assert.Equal(FileDropIntent.Copy, todoShiftDrop.Intent);
        Assert.Equal(NativeDropEffectPolicy.Copy, todoShiftDrop.FeedbackEffect);
    }

    [Fact]
    public async Task EmptySourceDirectory_CollidingWithDestinationRegularFile_ThrowsAndPreservesSourceDirectory()
    {
        string tempRoot = CreateTempTestDir();
        try
        {
            string srcEmptyDir = Directory.CreateDirectory(Path.Combine(tempRoot, "Src", "Foo")).FullName;
            string destDir = Directory.CreateDirectory(Path.Combine(tempRoot, "Dest")).FullName;
            string collidingDestFile = Path.Combine(destDir, "Foo");
            File.WriteAllText(collidingDestFile, "existing-file");

            var fileService = new FileService();
            var plans = new[] { new FileService.FileTransferPlan(srcEmptyDir, collidingDestFile) };
            var progress = new Progress<FileService.FileTransferProgress>(_ => { });

            await Assert.ThrowsAnyAsync<IOException>(() =>
                fileService.ExecuteTransferPlanAsync(
                    plans,
                    move: true,
                    useShellProgress: false,
                    progress: progress));

            Assert.True(Directory.Exists(srcEmptyDir));
            Assert.Equal("existing-file", File.ReadAllText(collidingDestFile));
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void FolderMergeUndoSnapshot_ExpandsOnlyNewlyAddedEntries_PreservingPreExistingFiles()
    {
        string tempRoot = CreateTempTestDir();
        try
        {
            string srcProject = Directory.CreateDirectory(Path.Combine(tempRoot, "Desktop", "ProjectA")).FullName;
            string destProject = Directory.CreateDirectory(Path.Combine(tempRoot, "Box", "ProjectA")).FullName;
            string destSub = Directory.CreateDirectory(Path.Combine(destProject, "Sub")).FullName;

            string preExistingRootFile = Path.Combine(destProject, "existing_work.docx");
            string preExistingSubFile = Path.Combine(destSub, "old_note.txt");
            File.WriteAllText(preExistingRootFile, "keep-me");
            File.WriteAllText(preExistingSubFile, "keep-sub");

            var plans = new[] { new FileService.FileTransferPlan(srcProject, destProject) };
            Dictionary<string, HashSet<string>?> snapshots =
                OrganizerService.SnapshotPreExistingMergeDestinations(plans);

            // Simulate merged arrivals into Dest\ProjectA
            string addedRootFile = Path.Combine(destProject, "new_spec.pdf");
            string addedSubFile = Path.Combine(destSub, "added_note.txt");
            string addedWholeFolder = Directory.CreateDirectory(Path.Combine(destProject, "Assets")).FullName;
            File.WriteAllText(addedRootFile, "new");
            File.WriteAllText(addedSubFile, "new-sub");
            File.WriteAllText(Path.Combine(addedWholeFolder, "logo.png"), "png");

            var rawResults = new[] { new FileService.FileTransferResult(srcProject, destProject) };
            (IReadOnlyList<FileService.FileTransferResult> undoable, bool ok) =
                OrganizerService.ExpandResultsForSafeFolderMergeUndo(rawResults, snapshots);

            Assert.True(ok);
            string[] undoDestinations = undoable.Select(static r => Path.GetFullPath(r.DestinationPath)).ToArray();
            Assert.Equal(3, undoDestinations.Length);
            Assert.Contains(Path.GetFullPath(addedRootFile), undoDestinations, StringComparer.OrdinalIgnoreCase);
            Assert.Contains(Path.GetFullPath(addedSubFile), undoDestinations, StringComparer.OrdinalIgnoreCase);
            Assert.Contains(Path.GetFullPath(addedWholeFolder), undoDestinations, StringComparer.OrdinalIgnoreCase);
            Assert.DoesNotContain(Path.GetFullPath(destProject), undoDestinations, StringComparer.OrdinalIgnoreCase);
            Assert.DoesNotContain(Path.GetFullPath(preExistingRootFile), undoDestinations, StringComparer.OrdinalIgnoreCase);
            Assert.DoesNotContain(Path.GetFullPath(preExistingSubFile), undoDestinations, StringComparer.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void Win11ProtectedSources_AllowCopyAndLinkForTaskbarPinning_WhileInternalFileReorderPrefersCopy()
    {
        string localProgramsExe = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Programs",
            "Cursor",
            "Cursor.exe");

        Assert.Equal(
            DataPackageOperation.Copy | DataPackageOperation.Link,
            FileItemDragPackage.ResolveDragOutAllowedOperations(
                SettingsService.ManagedDragOutActionFollowWindows,
                isWindows11OrLater: true,
                sourcePaths: [localProgramsExe]));
        Assert.Equal(
            DataPackageOperation.Copy,
            FileItemDragPackage.ResolveDragOutAllowedOperations(
                SettingsService.ManagedDragOutActionFollowWindows,
                isWindows11OrLater: false,
                sourcePaths: [localProgramsExe]));
        Assert.Equal(
            DataPackageOperation.Copy,
            FileItemDragPackage.ResolveDragOutAllowedOperations(
                SettingsService.ManagedDragOutActionCopy,
                isWindows11OrLater: true,
                sourcePaths: [localProgramsExe]));

        Assert.Equal(
            DataPackageOperation.Copy,
            FileSurfaceContent.ResolveInternalArrangementFeedbackOperation(
                DataPackageOperation.Copy | DataPackageOperation.Move | DataPackageOperation.Link,
                DataPackageOperation.None));
        Assert.Equal(
            DataPackageOperation.Link,
            FileSurfaceContent.ResolveInternalArrangementFeedbackOperation(
                DataPackageOperation.Copy | DataPackageOperation.Move | DataPackageOperation.Link,
                DataPackageOperation.Link));
    }

    [Fact]
    public async Task QuickCaptureDragPackage_HandlesHiddenStoreAppDataAncestorGracefully()
    {
        string tempRoot = CreateTempTestDir();
        try
        {
            string hiddenAppData = Directory.CreateDirectory(Path.Combine(tempRoot, ".appdata")).FullName;
            File.SetAttributes(hiddenAppData, FileAttributes.Directory | FileAttributes.Hidden);
            string attachDir = Directory.CreateDirectory(Path.Combine(hiddenAppData, "data", "quick-capture", "attachments")).FullName;
            string pngPath = Path.Combine(attachDir, "hidden-store.png");
            byte[] validPng = Convert.FromBase64String(
                "iVBORw0KGgoAAAANSUhEUgAAAAIAAAACCAYAAABytg0kAAAAFElEQVR4nGNkYPj/nwEJMDGgAQAAMQIEA3mLB4MAAAAASUVORK5CYII=");
            await File.WriteAllBytesAsync(pngPath, validPng);

            var model = new QuickCaptureItem
            {
                Id = "store-img-1",
                Type = QuickCaptureItemType.Image,
                Body = "Image",
                ImagePath = pngPath,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            };
            var vm = new QuickCaptureItemViewModel(
                model,
                TestServices.CreateLocalizationService(),
                textSize: 14,
                iconSize: 16,
                searchText: null);

            var pkg = new DataPackage();
            Assert.True(QuickCaptureDragPackage.TryPrepare(pkg, [vm], TestServices.CreateLocalizationService()));
            DataPackageView view = pkg.GetView();
            var items = await view.GetStorageItemsAsync();
            Assert.Single(items);
            var bitmap = await view.GetBitmapAsync();
            using var stream = await bitmap.OpenReadAsync();
            Assert.True(stream.Size > 0);
        }
        finally
        {
            foreach (string dir in Directory.GetDirectories(tempRoot, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(dir, FileAttributes.Directory);
            }

            Directory.Delete(tempRoot, recursive: true);
        }
    }

    private static string CreateTempTestDir() =>
        Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), "DeskBox.Tests", Guid.NewGuid().ToString("N"))).FullName;
}
