using System.Reflection;
using System.Text;
using DeskBox.Controls;
using DeskBox.Controls.WidgetContents;
using DeskBox.Models;
using DeskBox.Platform;
using DeskBox.Services;
using Microsoft.UI.Dispatching;

namespace DeskBox.Tests;

public sealed class P2Batch3RegressionTests : IDisposable
{
    private readonly string _tempRoot =
        Path.Combine(Path.GetTempPath(), $"DeskBox-P2Batch3-{Guid.NewGuid():N}");

    public P2Batch3RegressionTests()
    {
        Directory.CreateDirectory(_tempRoot);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempRoot))
            {
                foreach (string file in Directory.EnumerateFiles(_tempRoot, "*", SearchOption.AllDirectories))
                {
                    FileAttributes attrs = File.GetAttributes(file);
                    if ((attrs & FileAttributes.ReadOnly) != 0)
                    {
                        File.SetAttributes(file, attrs & ~FileAttributes.ReadOnly);
                    }
                }

                Directory.Delete(_tempRoot, recursive: true);
            }
        }
        catch
        {
            // Best-effort temp cleanup.
        }
    }

    [Fact]
    public void CopyStorageFileVerified_SinglePassCopiesAlternateStreamsPreservesReadOnlyAndMatchesHash()
    {
        string sourceDir = Directory.CreateDirectory(Path.Combine(_tempRoot, "Src")).FullName;
        string destDir = Directory.CreateDirectory(Path.Combine(_tempRoot, "Dst")).FullName;
        string sourceFile = Path.Combine(sourceDir, "archive.bin");
        string destFile = Path.Combine(destDir, "archive.bin.tmp");

        byte[] payload = Encoding.UTF8.GetBytes("Single-pass verified migration payload " + new string('X', 8192));
        File.WriteAllBytes(sourceFile, payload);
        byte[] zoneBytes = Encoding.ASCII.GetBytes("[ZoneTransfer]\r\nZoneId=3\r\n");
        File.WriteAllBytes(sourceFile + ":Zone.Identifier", zoneBytes);
        DateTime fixedWriteUtc = new(2026, 5, 10, 12, 30, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(sourceFile, fixedWriteUtc);
        File.SetAttributes(sourceFile, File.GetAttributes(sourceFile) | FileAttributes.ReadOnly);

        long copiedBytes = 0;
        string singlePassHash = FileService.CopyStorageFileVerified(
            sourceFile,
            destFile,
            CancellationToken.None,
            bytes => copiedBytes = bytes,
            verifyDestinationImmediately: false);

        string expectedSourceHash = FileService.HashStorageFile(sourceFile, CancellationToken.None);
        string destHash = FileService.HashStorageFile(destFile, CancellationToken.None);

        Assert.Equal(expectedSourceHash, singlePassHash);
        Assert.Equal(expectedSourceHash, destHash);
        Assert.Equal(payload.Length + zoneBytes.Length, copiedBytes);
        Assert.Equal(zoneBytes, File.ReadAllBytes(destFile + ":Zone.Identifier"));
        Assert.True((File.GetAttributes(destFile) & FileAttributes.ReadOnly) != 0);
        Assert.Equal(fixedWriteUtc, File.GetLastWriteTimeUtc(destFile));
    }

    [Fact]
    public async Task ManagedStorageMigration_UsnAcceleratedCopyCommitsAndDetectsSameSizeSourceMutation()
    {
        string oldRoot = Directory.CreateDirectory(Path.Combine(_tempRoot, "OldManaged")).FullName;
        string newRoot = Directory.CreateDirectory(Path.Combine(_tempRoot, "NewManaged")).FullName;
        string journalDir = Directory.CreateDirectory(Path.Combine(_tempRoot, "Journals")).FullName;
        string srcWidgetDir = Directory.CreateDirectory(Path.Combine(oldRoot, "Box1")).FullName;
        string dstWidgetDir = Path.Combine(newRoot, "Box1");

        string filePath = Path.Combine(srcWidgetDir, "notes.txt");
        File.WriteAllText(filePath, "AAAA-BBBB-CCCC-DDDD", Encoding.UTF8);

        var service = new ManagedStorageMigrationService(journalDir);
        var folders = new[]
        {
            new ManagedStorageMigrationFolder("box-1", "Box 1", "Box1", srcWidgetDir, dstWidgetDir, "Box1", srcWidgetDir)
        };

        var (journal, _) = await service.CopyAsync(
            oldRoot,
            newRoot,
            folders,
            new ManagedStorageMigrationOptions(
                RestartIfCopiesRemoved: true,
                CancellationToken: CancellationToken.None));

        journal.State = "Committed";
        await service.SaveAsync(journal);
        StorageMigrationNativeMethods.TryPruneEmptyMigrationWorkspace(journal.TemporaryDirectory);

        Assert.Equal("AAAA-BBBB-CCCC-DDDD", await File.ReadAllTextAsync(Path.Combine(dstWidgetDir, "notes.txt")));
        Assert.False(Directory.Exists(journal.TemporaryDirectory));

        // Now verify same-length + restored-timestamp tamper during Verifying is still caught via USN/hash check.
        string newRootTamper = Directory.CreateDirectory(Path.Combine(_tempRoot, "NewManagedTamper")).FullName;
        string dstTamperDir = Path.Combine(newRootTamper, "Box1");
        var tamperService = new ManagedStorageMigrationService(journalDir);
        DateTime originalWriteUtc = File.GetLastWriteTimeUtc(filePath);
        bool mutatedDuringVerify = false;
        tamperService.PhaseObserver = phase =>
        {
            if (phase == ManagedStorageMigrationPhase.Verifying && !mutatedDuringVerify)
            {
                mutatedDuringVerify = true;
                File.WriteAllText(filePath, "ZZZZ-BBBB-CCCC-DDDD", Encoding.UTF8);
                File.SetLastWriteTimeUtc(filePath, originalWriteUtc);
            }
        };

        var ex = await Assert.ThrowsAsync<StorageMigrationException>(() =>
            tamperService.CopyAsync(
                oldRoot,
                newRootTamper,
                [new ManagedStorageMigrationFolder("box-1", "Box 1", "Box1", srcWidgetDir, dstTamperDir, "Box1", srcWidgetDir)],
                new ManagedStorageMigrationOptions(
                    RestartIfCopiesRemoved: true,
                    CancellationToken: CancellationToken.None)));
        Assert.Equal(StorageMigrationProblem.SourceChanged, ex.Problem);
    }

    [Fact]
    public async Task FolderWatcherService_CapsPendingBufferAndPreservesRenamedOldFullPathUnderChangedBurst()
    {
        DispatcherQueueController? controller;
        try
        {
            controller = DispatcherQueueController.CreateOnDedicatedThread();
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            return;
        }

        try
        {
            using var watcher = new FolderWatcherService(controller.DispatcherQueue);
            PropertyInfo watchedPathProp = typeof(FolderWatcherService)
                .GetProperty(nameof(FolderWatcherService.WatchedPath))!;
            watchedPathProp.SetValue(watcher, _tempRoot);
            FieldInfo watchGenField = typeof(FolderWatcherService)
                .GetField("_watchGeneration", BindingFlags.Instance | BindingFlags.NonPublic)!;
            FieldInfo pendingField = typeof(FolderWatcherService)
                .GetField("_pendingChanges", BindingFlags.Instance | BindingFlags.NonPublic)!;
            FieldInfo fullReloadField = typeof(FolderWatcherService)
                .GetField("_requiresFullReload", BindingFlags.Instance | BindingFlags.NonPublic)!;
            MethodInfo queueChangeMethod = typeof(FolderWatcherService)
                .GetMethod("QueueChange", BindingFlags.Instance | BindingFlags.NonPublic)!;

            int generation = (int)watchGenField.GetValue(watcher)!;
            string oldPath = Path.Combine(_tempRoot, "before.txt");
            string newPath = Path.Combine(_tempRoot, "after.txt");

            // 1. Renamed followed by multiple Changed events on newPath must coalesce Changed without losing Renamed.
            queueChangeMethod.Invoke(
                watcher,
                [new FolderChange(newPath, WatcherChangeTypes.Renamed, oldPath), generation]);
            for (int i = 0; i < 10; i++)
            {
                queueChangeMethod.Invoke(
                    watcher,
                    [new FolderChange(newPath, WatcherChangeTypes.Changed), generation]);
            }

            var pending = (List<FolderChange>)pendingField.GetValue(watcher)!;
            Assert.Single(pending);
            Assert.Equal(WatcherChangeTypes.Renamed, pending[0].ChangeType);
            Assert.Equal(oldPath, pending[0].OldFullPath);
            Assert.False((bool)fullReloadField.GetValue(watcher)!);

            // 2. Flooding 500 distinct Created events must escalate to full reload and strictly cap _pendingChanges.
            for (int i = 0; i < 500; i++)
            {
                queueChangeMethod.Invoke(
                    watcher,
                    [new FolderChange(Path.Combine(_tempRoot, $"flood-{i}.txt"), WatcherChangeTypes.Created), generation]);
            }

            Assert.True((bool)fullReloadField.GetValue(watcher)!);
            Assert.Equal(FolderWatcherService.MaxBufferedChangesBeforeReload + 1, pending.Count);
        }
        finally
        {
            await controller.ShutdownQueueAsync();
        }
    }

    [Fact]
    public void WidgetStackGroupingService_OrdersMembersByNaturalExplorerSort()
    {
        WidgetItem[] items =
        [
            new WidgetItem { Name = "file10.txt", Path = @"C:\Docs\file10.txt" },
            new WidgetItem { Name = "file2.txt", Path = @"C:\Docs\file2.txt" },
            new WidgetItem { Name = "file1.txt", Path = @"C:\Docs\file1.txt" },
        ];

        IReadOnlyList<WidgetStackGroup> groups = WidgetStackGroupingService.Group(
            items,
            SettingsService.FileStackGroupByKind,
            orderBy: SettingsService.FileStackOrderByName);

        Assert.Single(groups);
        Assert.Equal(
            ["file1.txt", "file2.txt", "file10.txt"],
            groups[0].Items.Select(item => item.Name).ToArray());
    }

    [Fact]
    public void NativeShellFileDragProvider_BypassesStorageBrokerWhenSelectionMixesShortcutAndFile()
    {
        string[] mixed = [@"C:\Folder\App.lnk", @"C:\Folder\Readme.txt"];

        Assert.False(NativeShellFileDragProvider.AreExistingShortcuts(mixed, _ => true));
        Assert.True(NativeShellFileDragProvider.RequiresStorageBrokerBypass(mixed, _ => true));
        Assert.False(NativeShellFileDragProvider.RequiresStorageBrokerBypass([@"C:\Folder\Readme.txt"], _ => true));
    }

    [Fact]
    public void GetShortcutDisplayName_PreservesFolderDotNamesWhileStrippingFileExtensions()
    {
        Assert.Equal(
            "v1.2.3 - Shortcut.lnk",
            FileSurfaceContent.GetShortcutDisplayName(@"C:\Releases\v1.2.3", isDirectory: true));
        Assert.Equal(
            "archive.tar - Shortcut.lnk",
            FileSurfaceContent.GetShortcutDisplayName(@"C:\Releases\archive.tar.gz", isDirectory: false));
    }

    [Fact]
    public void CleanupStaleDropStagingDirectories_RemovesExpiredDropBucketsAndPreservesFreshAndSiblingDirs()
    {
        string fakeDeskBoxTemp = Directory.CreateDirectory(Path.Combine(_tempRoot, "DeskBoxTemp")).FullName;
        DateTimeOffset now = new(2026, 10, 31, 12, 0, 0, TimeSpan.Zero);

        string expiredVirtual = Directory.CreateDirectory(
            Path.Combine(fakeDeskBoxTemp, "VirtualDrops", "expired-1")).FullName;
        File.WriteAllText(Path.Combine(expiredVirtual, "a.txt"), "old");
        Directory.SetCreationTimeUtc(expiredVirtual, now.AddHours(-5).UtcDateTime);
        Directory.SetLastWriteTimeUtc(expiredVirtual, now.AddHours(-5).UtcDateTime);

        string freshStaged = Directory.CreateDirectory(
            Path.Combine(fakeDeskBoxTemp, "StagedDrops", "fresh-1")).FullName;
        File.WriteAllText(Path.Combine(freshStaged, "b.txt"), "fresh");
        Directory.SetCreationTimeUtc(freshStaged, now.AddMinutes(-15).UtcDateTime);
        Directory.SetLastWriteTimeUtc(freshStaged, now.AddMinutes(-15).UtcDateTime);

        string siblingClipboardExport = Directory.CreateDirectory(
            Path.Combine(fakeDeskBoxTemp, "ClipboardExports", "keep-export")).FullName;
        File.WriteAllText(Path.Combine(siblingClipboardExport, "clip.png"), "keep");
        Directory.SetCreationTimeUtc(siblingClipboardExport, now.AddHours(-10).UtcDateTime);
        Directory.SetLastWriteTimeUtc(siblingClipboardExport, now.AddHours(-10).UtcDateTime);

        int removed = DropStagingFileOps.CleanupStaleDropStagingDirectories(
            tempRootOverride: fakeDeskBoxTemp,
            maxAge: TimeSpan.FromHours(2),
            nowUtc: now);

        Assert.Equal(1, removed);
        Assert.False(Directory.Exists(expiredVirtual));
        Assert.True(Directory.Exists(freshStaged));
        Assert.True(Directory.Exists(siblingClipboardExport));
    }

    [Fact]
    public async Task RenameEntryAsync_PerformsCaseOnlyFileAndDirectoryRenamesCleanly()
    {
        var fileService = new FileService();
        string lowerFile = Path.Combine(_tempRoot, "readme.md");
        string upperFile = Path.Combine(_tempRoot, "README.md");
        await File.WriteAllTextAsync(lowerFile, "case rename test");

        await fileService.RenameEntryAsync(lowerFile, upperFile);
        string[] entriesAfterFile = Directory.GetFileSystemEntries(_tempRoot)
            .Select(Path.GetFileName)
            .ToArray()!;
        Assert.Contains("README.md", entriesAfterFile, StringComparer.Ordinal);
        Assert.DoesNotContain("readme.md", entriesAfterFile, StringComparer.Ordinal);

        string lowerDir = Directory.CreateDirectory(Path.Combine(_tempRoot, "assets")).FullName;
        string upperDir = Path.Combine(_tempRoot, "Assets");
        await File.WriteAllTextAsync(Path.Combine(lowerDir, "logo.svg"), "<svg/>");

        await fileService.RenameEntryAsync(lowerDir, upperDir);
        string[] entriesAfterDir = Directory.GetFileSystemEntries(_tempRoot)
            .Select(Path.GetFileName)
            .ToArray()!;
        Assert.Contains("Assets", entriesAfterDir, StringComparer.Ordinal);
        Assert.DoesNotContain("assets", entriesAfterDir, StringComparer.Ordinal);
        Assert.True(File.Exists(Path.Combine(upperDir, "logo.svg")));
    }

    [Fact]
    public void AppManifest_OptsIntoLongPathAwarenessAndUpdaterSharesManifest()
    {
        string manifest = File.ReadAllText(TestPaths.FromRepository("src/DeskBox/app.manifest"));
        string updaterProj = File.ReadAllText(TestPaths.FromRepository("src/DeskBox.Updater/DeskBox.Updater.csproj"));

        Assert.Contains(
            "<longPathAware xmlns=\"http://schemas.microsoft.com/SMI/2016/WindowsSettings\">true</longPathAware>",
            manifest,
            StringComparison.Ordinal);
        Assert.Contains(
            "<ApplicationManifest>..\\DeskBox\\app.manifest</ApplicationManifest>",
            updaterProj,
            StringComparison.Ordinal);
    }
}
