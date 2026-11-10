using System.Diagnostics;
using System.Text;
using DeskBox.Models;
using DeskBox.Platform;
using DeskBox.Services;

namespace DeskBox.Tests;

public sealed class P0Batch1RegressionTests
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
        Assert.False(FileOperationNativeMethods.CanHeadlessRecyclePath(@"\\server\share\document.txt"));

        string sandbox = Path.Combine(Path.GetTempPath(), $"DeskBox-P0Recycle-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(sandbox);
            string tempFile = Path.Combine(sandbox, $"DeskBoxRecycleProbe-{Guid.NewGuid():N}.txt");
            await File.WriteAllTextAsync(tempFile, "recycle-probe", Encoding.UTF8);

            if (!FileOperationNativeMethods.CanHeadlessRecyclePath(tempFile))
            {
                // Skip live Recycle Bin mutation if the developer machine disabled Recycle Bin on TEMP's volume.
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
