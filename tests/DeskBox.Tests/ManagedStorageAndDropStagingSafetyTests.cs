using System.Diagnostics;
using System.Text;
using DeskBox.Models;
using DeskBox.Platform;
using DeskBox.Services;

namespace DeskBox.Tests;

public sealed class ManagedStorageAndDropStagingSafetyTests
{
    [Theory]
    [InlineData("7zO48A1B2C3", true)]
    [InlineData("Rar$DRa12048.39102", true)]
    [InlineData("BNZ.65a1f02c", true)]
    [InlineData("Temp1_release-bundle.zip", true)]
    [InlineData("Temp12_archive.7z", true)]
    [InlineData("scp49281", true)]
    [InlineData("Content.Outlook", true)]
    [InlineData("Documents", false)]
    [InlineData("MyProject_v1", false)]
    public void DropStaging_RecognizesKnownTransientArchiverAndClientSegments(
        string segment,
        bool expected)
    {
        Assert.Equal(
            expected,
            DropStagingFileOps.IsKnownTransientTempDirectorySegment(segment));
    }

    [Fact]
    public void DropStaging_StagesExternal7ZipTempPayloadBeforeSourceDeletesIt()
    {
        string transientSourceDir = Path.Combine(
            Path.GetTempPath(),
            $"7zO_{Guid.NewGuid():N}");
        string? stagedBatchDir = null;

        try
        {
            Directory.CreateDirectory(transientSourceDir);
            string topFile = Path.Combine(transientSourceDir, "readme.txt");
            File.WriteAllText(topFile, "hello-from-7zip-temp", Encoding.UTF8);

            string nestedDir = Path.Combine(transientSourceDir, "sub", "inner");
            Directory.CreateDirectory(nestedDir);
            string nestedFile = Path.Combine(nestedDir, "payload.bin");
            File.WriteAllText(nestedFile, "nested-archive-payload", Encoding.UTF8);

            IReadOnlyList<string> staged = DropStagingFileOps.StageExternalTempDropPaths(
                [topFile, Path.Combine(transientSourceDir, "sub")],
                hasShellIdList: false,
                out bool stagedAny);

            Assert.True(stagedAny);
            Assert.Equal(2, staged.Count);
            stagedBatchDir = Path.GetDirectoryName(staged[0]);

            // Simulate 7-Zip / WinRAR deleting its %TEMP%\7zO... directory immediately after Drop returns S_OK.
            Directory.Delete(transientSourceDir, recursive: true);
            Assert.False(Directory.Exists(transientSourceDir));

            Assert.True(File.Exists(staged[0]));
            Assert.Equal("hello-from-7zip-temp", File.ReadAllText(staged[0], Encoding.UTF8));

            string stagedNestedFile = Path.Combine(staged[1], "inner", "payload.bin");
            Assert.True(File.Exists(stagedNestedFile));
            Assert.Equal("nested-archive-payload", File.ReadAllText(stagedNestedFile, Encoding.UTF8));
        }
        finally
        {
            TryDeleteDirectory(transientSourceDir);
            if (!string.IsNullOrWhiteSpace(stagedBatchDir))
            {
                TryDeleteDirectory(stagedBatchDir);
            }
        }
    }

    [Fact]
    public void ManagedStorageRootValidation_RejectsDriveRootsDesktopAncestorsAndStoreAppData()
    {
        string sandbox = Path.Combine(Path.GetTempPath(), $"DeskBox-P0RootVal-{Guid.NewGuid():N}");
        try
        {
            string fakeProfile = Path.Combine(sandbox, "Users", "alice");
            string fakeOneDrive = Path.Combine(fakeProfile, "OneDrive");
            string fakeDesktop = Path.Combine(fakeOneDrive, "Desktop");
            string fakePublicDesktop = Path.Combine(sandbox, "Users", "Public", "Desktop");
            string fakeLocalAppData = Path.Combine(fakeProfile, "AppData", "Local");
            string fakeRoamingAppData = Path.Combine(fakeProfile, "AppData", "Roaming");

            // Bidirectional overlap: OneDrive is an ancestor of Desktop and must be rejected.
            Assert.True(ManagedStoragePathService.OverlapsDesktop(
                fakeOneDrive,
                fakeDesktop,
                fakePublicDesktop));
            Assert.Equal(
                ManagedStorageRootValidationProblem.DesktopOverlap,
                ManagedStoragePathService.ValidateStorageRootCandidate(
                    fakeOneDrive,
                    isStoreChannel: false,
                    fakeDesktop,
                    fakePublicDesktop,
                    fakeProfile,
                    fakeLocalAppData,
                    fakeRoamingAppData).Problem);

            // Drive root (e.g. C:\ or D:\) is rejected.
            string systemDriveRoot = Path.GetPathRoot(sandbox)!;
            Assert.Equal(
                ManagedStorageRootValidationProblem.DriveOrShareRoot,
                ManagedStoragePathService.ValidateStorageRootCandidate(
                    systemDriveRoot,
                    isStoreChannel: false,
                    fakeDesktop,
                    fakePublicDesktop,
                    fakeProfile,
                    fakeLocalAppData,
                    fakeRoamingAppData).Problem);

            // Store channel rejects virtualized LocalAppData subdirectories.
            string insideLocalAppData = Path.Combine(fakeLocalAppData, "CustomBoxes");
            Assert.Equal(
                ManagedStorageRootValidationProblem.StoreVirtualizedAppData,
                ManagedStoragePathService.ValidateStorageRootCandidate(
                    insideLocalAppData,
                    isStoreChannel: true,
                    fakeDesktop,
                    fakePublicDesktop,
                    fakeProfile,
                    fakeLocalAppData,
                    fakeRoamingAppData).Problem);

            // Internal Store durable .appdata directory cannot be used as file-widget root,
            // while %USERPROFILE%\DeskBox itself remains valid.
            Assert.Equal(
                ManagedStorageRootValidationProblem.SystemOrProfileRoot,
                ManagedStoragePathService.ValidateStorageRootCandidate(
                    Path.Combine(fakeProfile, "DeskBox", ".appdata"),
                    isStoreChannel: true,
                    fakeDesktop,
                    fakePublicDesktop,
                    fakeProfile,
                    fakeLocalAppData,
                    fakeRoamingAppData).Problem);
            Assert.True(ManagedStoragePathService.ValidateStorageRootCandidate(
                Path.Combine(fakeProfile, "DeskBox"),
                isStoreChannel: true,
                fakeDesktop,
                fakePublicDesktop,
                fakeProfile,
                fakeLocalAppData,
                fakeRoamingAppData).IsValid);
        }
        finally
        {
            TryDeleteDirectory(sandbox);
        }
    }

    [Fact]
    public void ManagedStorageOwnership_ScopesNonEmptyPickedFolderAndProtectsForeignDirectories()
    {
        string sandbox = Path.Combine(Path.GetTempPath(), $"DeskBox-P0Own-{Guid.NewGuid():N}");
        try
        {
            string workDir = Path.Combine(sandbox, "Work");
            Directory.CreateDirectory(Path.Combine(workDir, "ClientRepo"));

            // Picking a non-empty foreign folder scopes to \DeskBox automatically.
            Assert.Equal(
                Path.Combine(workDir, "DeskBox"),
                ManagedStoragePathService.ResolvePickedStorageRootPath(workDir));

            // Empty folder is accepted as-is without appending \DeskBox.
            string emptyDir = Path.Combine(sandbox, "EmptyStorage");
            Directory.CreateDirectory(emptyDir);
            Assert.Equal(
                emptyDir,
                ManagedStoragePathService.ResolvePickedStorageRootPath(emptyDir));

            // Pre-migration ownership snapshot records existing foreign folders and protects them from orphan cleanup.
            ManagedStorageOwnershipFileOps.SnapshotMigrationDestinationOwnership(
                workDir,
                migratingManagedFolderNames: ["ProjectFiles"],
                temporaryMigrationFolderName: ".deskbox-migration-temp");
            Directory.CreateDirectory(Path.Combine(workDir, "ProjectFiles"));
            Directory.CreateDirectory(Path.Combine(workDir, ".appdata"));

            ManagedStorageOwnershipSnapshot snapshot = ManagedStorageOwnershipFileOps.ReadSnapshot(workDir);
            Assert.True(snapshot.HasManifest);
            Assert.Contains("ProjectFiles", snapshot.ManagedFolders);
            Assert.Contains("ClientRepo", snapshot.ForeignFolders);
            Assert.True(ManagedStorageOwnershipFileOps.IsExcludedOrSystemDirectory(
                Path.Combine(workDir, ".appdata"),
                ".deskbox-migration-temp"));
        }
        finally
        {
            TryDeleteDirectory(sandbox);
        }
    }

    [Fact]
    public async Task ShellRecycleGuard_BlocksUncHeadlessRecycleAndRecyclesLocalFixedFileViaIFileOperation()
    {
        Assert.Equal(IntPtr.Size == 8 ? 24 : 20, FileOperationNativeMethods.ShQueryRbInfoNativeSize);
        Assert.False(FileOperationNativeMethods.CanHeadlessRecyclePath(@"\\server\share\document.txt"));

        string sandbox = Path.Combine(Path.GetTempPath(), $"DeskBox-P0Recycle-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(sandbox);
            string tempFile = Path.Combine(sandbox, $"DeskBoxRecycleProbe-{Guid.NewGuid():N}.txt");
            await File.WriteAllTextAsync(tempFile, "recycle-probe", Encoding.UTF8);

            bool canRecycle = FileOperationNativeMethods.CanHeadlessRecyclePath(tempFile);
            if (!canRecycle)
            {
                // Only valid when the volume is removable/RAM-disk or Recycle Bin NukeOnDelete=1 is explicitly set.
                string? root = Path.GetPathRoot(tempFile);
                var driveInfo = string.IsNullOrWhiteSpace(root) ? null : new DriveInfo(root);
                Assert.True(
                    driveInfo is null || driveInfo.DriveType != DriveType.Fixed || IsVolumeRecycleBinDisabledByUserPolicy(root!),
                    $"Expected CanHeadlessRecyclePath('{tempFile}') to succeed on a fixed drive with Recycle Bin enabled (ShQueryRbInfo size={FileOperationNativeMethods.ShQueryRbInfoNativeSize}).");
                return;
            }

            var service = new FileService();
            bool deleted = await service.DeleteEntryAsync(tempFile, recycle: true, IntPtr.Zero);
            Assert.True(deleted);
            Assert.False(File.Exists(tempFile));
        }
        finally
        {
            TryDeleteDirectory(sandbox);
        }
    }

    private static bool IsVolumeRecycleBinDisabledByUserPolicy(string volumeRoot)
    {
        try
        {
            using var bitBucketKey = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Explorer\BitBucket");
            if (bitBucketKey?.GetValue("NukeOnDelete") is int globalNuke && globalNuke == 1)
            {
                return true;
            }

            using var volumeRootKey = bitBucketKey?.OpenSubKey("Volume");
            if (volumeRootKey is null)
            {
                return false;
            }

            foreach (string subKeyName in volumeRootKey.GetSubKeyNames())
            {
                using var volKey = volumeRootKey.OpenSubKey(subKeyName);
                if (volKey?.GetValue("NukeOnDelete") is int volNuke && volNuke == 1)
                {
                    return true;
                }
            }
        }
        catch
        {
        }

        return false;
    }

    [Fact]
    public void StoreDurableDataRoot_MigratesLegacyLocalAppDataAndRebasesEmbeddedAttachmentPaths()
    {
        string sandbox = Path.Combine(Path.GetTempPath(), $"DeskBox-P0StoreRoot-{Guid.NewGuid():N}");
        try
        {
            string fakeLocalAppData = Path.Combine(sandbox, "AppData", "Local");
            string fakeUserProfile = Path.Combine(sandbox, "Profile");
            string legacyDataDir = Path.Combine(fakeLocalAppData, "DeskBox", "data");
            string legacyWidgetDir = Path.Combine(legacyDataDir, "widgets", "todo-1");
            string legacyAttachDir = Path.Combine(legacyWidgetDir, "attachments");
            string legacyQcDir = Path.Combine(legacyDataDir, "quick-capture");
            string legacyRecoveryAuto = Path.Combine(fakeLocalAppData, "DeskBox-Recovery", "automatic");

            Directory.CreateDirectory(legacyAttachDir);
            Directory.CreateDirectory(legacyQcDir);
            Directory.CreateDirectory(legacyRecoveryAuto);

            File.WriteAllText(Path.Combine(legacyDataDir, "settings.json"), "{\"schemaVersion\":1}", Encoding.UTF8);
            string legacyAttachmentFile = Path.Combine(legacyAttachDir, "spec.pdf");
            File.WriteAllText(legacyAttachmentFile, "pdf-bytes", Encoding.UTF8);

            string escapedLegacyPath = legacyAttachmentFile.Replace("\\", "\\\\", StringComparison.Ordinal);
            File.WriteAllText(
                Path.Combine(legacyWidgetDir, "todo.json"),
                $"{{\"items\":[{{\"id\":\"1\",\"attachments\":[{{\"filePath\":\"{escapedLegacyPath}\",\"storageMode\":\"managed\"}}]}}]}}",
                Encoding.UTF8);

            string legacySnapshotZip = Path.Combine(legacyRecoveryAuto, "snapshot-01.zip");
            File.WriteAllText(legacySnapshotZip, "zip-placeholder", Encoding.UTF8);

            var storePaths = new DeskBoxDataPathService(
                rootPath: null,
                isMicrosoftStore: true,
                localAppDataPath: fakeLocalAppData,
                userProfilePath: fakeUserProfile);

            string expectedRoot = Path.Combine(fakeUserProfile, "DeskBox", ".appdata");
            string expectedRecovery = Path.Combine(fakeUserProfile, "DeskBox", ".appdata-recovery");

            Assert.False(storePaths.IsDevelopmentRoot);
            Assert.Equal("DeskBox_SingleInstance_Mutex_7F3A9B2E", storePaths.SingleInstanceMutexName);
            Assert.Equal(expectedRoot, storePaths.RootPath);
            Assert.Equal(expectedRecovery, storePaths.RecoveryDirectory);

            string migratedAttachment = Path.Combine(
                storePaths.DataDirectory,
                "widgets",
                "todo-1",
                "attachments",
                "spec.pdf");
            Assert.True(File.Exists(migratedAttachment));
            string migratedTodoJson = File.ReadAllText(
                Path.Combine(storePaths.DataDirectory, "widgets", "todo-1", "todo.json"),
                Encoding.UTF8);
            Assert.Contains(
                migratedAttachment.Replace("\\", "\\\\", StringComparison.Ordinal),
                migratedTodoJson,
                StringComparison.OrdinalIgnoreCase);

            Assert.True(File.Exists(Path.Combine(expectedRecovery, "automatic", "snapshot-01.zip")));

            Assert.Equal(
                fakeUserProfile,
                RestoreMachineLocalPathRebase.TryDeriveSourceProfileRoot(storePaths.DataDirectory));
        }
        finally
        {
            TryDeleteDirectory(sandbox);
        }
    }

    [Fact]
    public void ExplorerPhysicalTargetResolution_CollapsesDirectoryJunctionsAndFallsBackToExistingParent()
    {
        string sandbox = Path.Combine(Path.GetTempPath(), $"DeskBox-P0Junction-{Guid.NewGuid():N}");
        try
        {
            string physicalDir = Path.Combine(sandbox, "LocalCache", "Local", "DeskBox");
            Directory.CreateDirectory(physicalDir);
            string physicalFile = Path.Combine(physicalDir, "DeskBox.log");
            File.WriteAllText(physicalFile, "log-entry", Encoding.UTF8);

            string virtualParent = Path.Combine(sandbox, "Virtualized");
            Directory.CreateDirectory(virtualParent);
            string junctionDir = Path.Combine(virtualParent, "DeskBox");

            using (Process? mklink = Process.Start(new ProcessStartInfo
                   {
                       FileName = "cmd.exe",
                       Arguments = $"/c mklink /J \"{junctionDir}\" \"{physicalDir}\"",
                       CreateNoWindow = true,
                       UseShellExecute = false,
                       RedirectStandardOutput = true,
                       RedirectStandardError = true,
                   }))
            {
                mklink?.WaitForExit(5000);
                if (mklink?.ExitCode != 0 || !Directory.Exists(junctionDir))
                {
                    return;
                }
            }

            string virtualFile = Path.Combine(junctionDir, "DeskBox.log");
            (string resolvedFile, bool selectFile) = Win32Helper.ResolveExplorerTarget(virtualFile);
            Assert.True(selectFile);
            Assert.Equal(Path.GetFullPath(physicalFile), Path.GetFullPath(resolvedFile), ignoreCase: true);

            // Missing leaf falls back to opening the resolved physical parent folder without /select.
            string missingSnapshot = Path.Combine(junctionDir, "uncreated-folder", "missing.zip");
            (string resolvedFallback, bool selectFallback) = Win32Helper.ResolveExplorerTarget(missingSnapshot);
            Assert.False(selectFallback);
            Assert.Equal(Path.GetFullPath(physicalDir), Path.GetFullPath(resolvedFallback), ignoreCase: true);
        }
        finally
        {
            TryDeleteDirectory(sandbox);
        }
    }

    [Fact]
    public void StageExternalTempDropPathsWithOrigins_EnforcesByteCapWithPerItemFaultIsolation()
    {
        string transientBatch = Path.Combine(
            Path.GetTempPath(),
            "7zO" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(transientBatch);
        string small1 = Path.Combine(transientBatch, "small1.txt");
        string oversized = Path.Combine(transientBatch, "oversized.bin");
        string small2 = Path.Combine(transientBatch, "small2.txt");

        IReadOnlyList<string> result = [];
        try
        {
            File.WriteAllBytes(small1, new byte[100]);
            File.WriteAllBytes(oversized, new byte[4096]);
            File.WriteAllBytes(small2, new byte[100]);

            result = DropStagingFileOps.StageExternalTempDropPathsWithOrigins(
                [small1, oversized, small2],
                hasShellIdList: false,
                out IReadOnlyList<DroppedItemOriginKind> origins,
                out bool stagedAny,
                maxTotalBytes: 1024);

            Assert.True(stagedAny);
            Assert.Equal(2, result.Count);
            Assert.Equal(2, origins.Count);
            Assert.Equal("small1.txt", Path.GetFileName(result[0]));
            Assert.Equal("small2.txt", Path.GetFileName(result[1]));
            Assert.All(result, path => Assert.True(DropStagingFileOps.IsUnderVirtualDropsDirectory(path)));
            Assert.All(origins, origin => Assert.Equal(DroppedItemOriginKind.TransientArchiveOrCache, origin));

            int cleaned = DropStagingFileOps.CleanupConsumedStagedPaths(result);
            Assert.True(cleaned >= 1);
            Assert.False(File.Exists(result[0]));
            Assert.False(File.Exists(result[1]));
        }
        finally
        {
            DropStagingFileOps.CleanupConsumedStagedPaths(result);
            TryDeleteDirectory(transientBatch);
        }
    }

    [Fact]
    public void CleanupStaleDropStagingDirectories_RemovesOnlyExpiredBatches()
    {
        string sandboxTempRoot = Path.Combine(
            Path.GetTempPath(),
            "DeskBox.Tests",
            "StaleReaper-" + Guid.NewGuid().ToString("N"));
        string virtualDrops = Path.Combine(sandboxTempRoot, "VirtualDrops");
        string oldBatch = Path.Combine(virtualDrops, "old-batch");
        string freshBatch = Path.Combine(virtualDrops, "fresh-batch");
        Directory.CreateDirectory(oldBatch);
        Directory.CreateDirectory(freshBatch);

        try
        {
            File.WriteAllText(Path.Combine(oldBatch, "stale.txt"), "stale");
            File.WriteAllText(Path.Combine(freshBatch, "fresh.txt"), "fresh");
            DateTime expiredUtc = DateTime.UtcNow.AddHours(-2);
            Directory.SetCreationTimeUtc(oldBatch, expiredUtc);
            Directory.SetLastWriteTimeUtc(oldBatch, expiredUtc);

            int removed = DropStagingFileOps.CleanupStaleDropStagingDirectories(
                sandboxTempRoot,
                TimeSpan.FromMinutes(30),
                DateTimeOffset.UtcNow);

            Assert.Equal(1, removed);
            Assert.False(Directory.Exists(oldBatch));
            Assert.True(Directory.Exists(freshBatch));
        }
        finally
        {
            TryDeleteDirectory(sandboxTempRoot);
        }
    }

    [Fact]
    public void DropStagingFileOps_RecognizesMsixLocalCacheAndUnvirtualizedTemp_WhileExcludingOwnDroppedFiles()
    {
        Assert.True(DropStagingFileOps.IsMsixPackageTransientDropPath(
            @"C:\Users\tester\AppData\Local\Packages\App_123\LocalCache\Local\Temp\7zO42\item.txt"));
        Assert.True(DropStagingFileOps.IsMsixPackageTransientDropPath(
            @"C:\Users\tester\AppData\Local\Packages\App_123\LocalCache\Local\Microsoft\Windows\INetCache\Content.Outlook\ABC\mail.msg"));

        string tempRoot = @"C:\Virtualized\Temp\";
        string virtualDropsRoot = @"C:\Virtualized\Temp\DeskBox\VirtualDrops\";
        string unvirtualizedTemp = @"C:\Users\tester\AppData\Local\Temp\";
        string devRoot = @"C:\Users\tester\AppData\Local\Programs\";
        string inetCache = @"C:\Users\tester\AppData\Local\Microsoft\Windows\INetCache\";

        Assert.True(DropStagingFileOps.IsExternalTransientTempDropPath(
            @"C:\Users\tester\AppData\Local\Temp\7zO123\payload.txt",
            hasShellIdList: false,
            tempRoot,
            virtualDropsRoot,
            devRoot,
            inetCache,
            unvirtualizedTemp));

        Assert.False(DropStagingFileOps.IsExternalTransientTempDropPath(
            @"C:\Users\tester\AppData\Local\Temp\DeskBox\DroppedFiles\batch1\note.txt",
            hasShellIdList: false,
            tempRoot,
            virtualDropsRoot,
            devRoot,
            inetCache,
            unvirtualizedTemp));
    }

    [Fact]
    public void DropStagingFileOps_RejectsTopLevelJunctionDirectory_AndCleansDualStagingBatches()
    {
        string sandbox = Path.Combine(
            Path.GetTempPath(),
            "DeskBox.Tests",
            "StagingJunction-" + Guid.NewGuid().ToString("N"));
        string targetRealDir = Path.Combine(sandbox, "real-target");
        string junctionDir = Path.Combine(sandbox, "junction-dir");
        string regularFile = Path.Combine(sandbox, "regular.txt");
        string stagingOut = Path.Combine(sandbox, "staged-out");
        string uniqueBatchName = "TestBatch-" + Guid.NewGuid().ToString("N");
        string realDroppedBatch = Path.Combine(Path.GetTempPath(), "DeskBox", "DroppedFiles", uniqueBatchName);
        string realVirtualBatch = Path.Combine(Path.GetTempPath(), "DeskBox", "VirtualDrops", uniqueBatchName);

        Directory.CreateDirectory(targetRealDir);
        File.WriteAllText(Path.Combine(targetRealDir, "secret.txt"), "secret");
        File.WriteAllText(regularFile, "safe");
        Directory.CreateDirectory(stagingOut);
        Directory.CreateDirectory(realDroppedBatch);
        Directory.CreateDirectory(realVirtualBatch);

        try
        {
            using (Process? mklink = Process.Start(new ProcessStartInfo
                   {
                       FileName = "cmd.exe",
                       Arguments = $"/c mklink /J \"{junctionDir}\" \"{targetRealDir}\"",
                       CreateNoWindow = true,
                       UseShellExecute = false,
                       RedirectStandardOutput = true,
                       RedirectStandardError = true,
                   }))
            {
                mklink?.WaitForExit(5000);
                if (mklink?.ExitCode != 0 || !Directory.Exists(junctionDir))
                {
                    return;
                }
            }

            long remaining = 1024 * 1024;
            long junctionBytes = 0;
            Assert.Throws<IOException>(() =>
                DropStagingFileOps.StagePathIntoDirectory(
                    junctionDir,
                    stagingOut,
                    ref junctionBytes,
                    remaining));
            Assert.Empty(Directory.GetFileSystemEntries(stagingOut));

            long fileBytes = 0;
            string? stagedFile = DropStagingFileOps.StagePathIntoDirectory(
                regularFile,
                stagingOut,
                ref fileBytes,
                remaining);
            Assert.NotNull(stagedFile);
            Assert.True(File.Exists(stagedFile));

            string droppedFile = Path.Combine(realDroppedBatch, "d.txt");
            string virtualFile = Path.Combine(realVirtualBatch, "v.txt");
            File.WriteAllText(droppedFile, "d");
            File.WriteAllText(virtualFile, "v");

            int cleaned = DropStagingFileOps.CleanupConsumedStagedPaths([droppedFile, virtualFile]);
            Assert.Equal(2, cleaned);
            Assert.False(File.Exists(droppedFile));
            Assert.False(File.Exists(virtualFile));
        }
        finally
        {
            TryDeleteDirectory(sandbox);
            TryDeleteDirectory(realDroppedBatch);
            TryDeleteDirectory(realVirtualBatch);
        }
    }

    [Fact]
    public void DriveMediaInterop_ProtectsDeskBoxAppDataAndRecoveryDirectories()
    {
        Assert.True(DriveMediaInterop.IsDeskBoxInternalDataOrRecoveryPath(@"C:\Users\alice\DeskBox\.appdata"));
        Assert.True(DriveMediaInterop.IsDeskBoxInternalDataOrRecoveryPath(@"C:\Users\alice\DeskBox\.appdata\settings.json"));
        Assert.True(DriveMediaInterop.IsDeskBoxInternalDataOrRecoveryPath(@"C:\Users\alice\DeskBox\.appdata-recovery\backup.json"));
        Assert.False(DriveMediaInterop.IsDeskBoxInternalDataOrRecoveryPath(@"C:\Users\alice\DeskBox\Notes\todo.txt"));
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                foreach (string subDir in Directory.EnumerateDirectories(path, "*", SearchOption.AllDirectories))
                {
                    FileAttributes attrs = File.GetAttributes(subDir);
                    if ((attrs & FileAttributes.ReparsePoint) != 0)
                    {
                        Directory.Delete(subDir, recursive: false);
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
