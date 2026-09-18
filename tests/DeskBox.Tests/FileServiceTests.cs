using System.Diagnostics;
using DeskBox.Helpers;
using DeskBox.Models;
using DeskBox.Services;

namespace DeskBox.Tests;

public sealed class FileServiceTests : IDisposable
{
    private readonly string _tempRoot;

    public FileServiceTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "DeskBox.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
    }

    [Theory]
    [InlineData("  name  ", "name")]
    [InlineData("trailing.", "trailing")]
    [InlineData("", "")]
    [InlineData("   ", "")]
    public void SanitizeFileSystemName_NormalizesBasicInput(string input, string expected)
    {
        Assert.Equal(expected, FileService.SanitizeFileSystemName(input));
    }

    [Fact]
    public void SanitizeFileSystemName_ReplacesInvalidFileNameChars()
    {
        char invalidChar = Path.GetInvalidFileNameChars().First();

        string result = FileService.SanitizeFileSystemName($"left{invalidChar}right");

        Assert.Equal("left-right", result);
    }

    [Fact]
    public void GetAvailablePath_ReturnsDesiredPathWhenUnused()
    {
        string desiredPath = Path.Combine(_tempRoot, "item.txt");

        string result = FileService.GetAvailablePath(desiredPath);

        Assert.Equal(Path.GetFullPath(desiredPath), result);
    }

    [Fact]
    public void GetAvailablePath_AppendsIndexWhenPathExistsOrReserved()
    {
        string desiredPath = Path.Combine(_tempRoot, "item.txt");
        File.WriteAllText(desiredPath, "existing");
        var reservedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            Path.Combine(_tempRoot, "item (2).txt")
        };

        string result = FileService.GetAvailablePath(desiredPath, reservedPaths);

        Assert.Equal(Path.Combine(_tempRoot, "item (3).txt"), result);
        Assert.Contains(result, reservedPaths);
    }

    [Fact]
    public void IsPathUnderDirectory_MatchesSelfAndChildrenOnly()
    {
        string root = Path.Combine(_tempRoot, "root");
        string child = Path.Combine(root, "child", "file.txt");
        string sibling = Path.Combine(_tempRoot, "root-other", "file.txt");

        Assert.True(FileService.IsPathUnderDirectory(root, root));
        Assert.True(FileService.IsPathUnderDirectory(child, root));
        Assert.False(FileService.IsPathUnderDirectory(sibling, root));
    }

    [Fact]
    public void IsUnsafeDirectoryTransfer_RejectsDestinationInsideSource()
    {
        string source = Directory.CreateDirectory(
            Path.Combine(_tempRoot, "source-root")).FullName;
        string destination = Directory.CreateDirectory(
            Path.Combine(source, "deskbox")).FullName;

        Assert.True(FileService.IsUnsafeDirectoryTransfer([source], destination));
        Assert.True(FileService.IsUnsafeDirectoryTransfer([destination], destination));
    }

    [Fact]
    public void IsUnsafeDirectoryTransfer_AllowsSiblingDestinationAndFiles()
    {
        string source = Directory.CreateDirectory(
            Path.Combine(_tempRoot, "source-sibling")).FullName;
        string destination = Directory.CreateDirectory(
            Path.Combine(_tempRoot, "destination-sibling")).FullName;
        string file = Path.Combine(source, "note.txt");
        File.WriteAllText(file, "content");

        Assert.False(FileService.IsUnsafeDirectoryTransfer([source], destination));
        Assert.False(FileService.IsUnsafeDirectoryTransfer([file], destination));
    }

    [Fact]
    public void PathsOverlap_MatchesEqualAndAncestorPathsButNotSiblings()
    {
        string root = Path.Combine(_tempRoot, "root");
        string child = Path.Combine(root, "child");
        string sibling = Path.Combine(_tempRoot, "root-other");

        Assert.True(FileService.PathsOverlap(root, root));
        Assert.True(FileService.PathsOverlap(root, child));
        Assert.True(FileService.PathsOverlap(child, root));
        Assert.False(FileService.PathsOverlap(root, sibling));
    }

    [Fact]
    public async Task RenameEntryAsync_RenamesFileWhenOnlyCasingChanges()
    {
        var service = new FileService();
        string sourcePath = Path.Combine(_tempRoot, "report.txt");
        string destinationPath = Path.Combine(_tempRoot, "REPORT.txt");
        await File.WriteAllTextAsync(sourcePath, "preserved");

        await service.RenameEntryAsync(sourcePath, destinationPath);

        string actualPath = Assert.Single(
            Directory.EnumerateFiles(_tempRoot));
        Assert.Equal("REPORT.txt", Path.GetFileName(actualPath));
        Assert.Equal("preserved", await File.ReadAllTextAsync(actualPath));
        Assert.DoesNotContain(
            Directory.EnumerateFileSystemEntries(_tempRoot),
            path => Path.GetFileName(path).StartsWith(
                ".deskbox-case-rename-",
                StringComparison.Ordinal));
    }

    [Fact]
    public async Task RenameEntryAsync_RenamesFolderWhenOnlyCasingChanges()
    {
        var service = new FileService();
        string sourcePath = Directory.CreateDirectory(
            Path.Combine(_tempRoot, "designs")).FullName;
        string destinationPath = Path.Combine(_tempRoot, "DESIGNS");
        await File.WriteAllTextAsync(
            Path.Combine(sourcePath, "content.txt"),
            "preserved");

        await service.RenameEntryAsync(sourcePath, destinationPath);

        string actualPath = Assert.Single(
            Directory.EnumerateDirectories(_tempRoot));
        Assert.Equal("DESIGNS", Path.GetFileName(actualPath));
        Assert.Equal(
            "preserved",
            await File.ReadAllTextAsync(
                Path.Combine(actualPath, "content.txt")));
        Assert.DoesNotContain(
            Directory.EnumerateFileSystemEntries(_tempRoot),
            path => Path.GetFileName(path).StartsWith(
                ".deskbox-case-rename-",
                StringComparison.Ordinal));
    }

    [Fact]
    public async Task RenameEntryAsync_LockedChildFailsWithoutCreatingDestinationOrMovingSiblings()
    {
        var service = new FileService();
        string sourcePath = Directory.CreateDirectory(
            Path.Combine(_tempRoot, "source-folder")).FullName;
        string destinationPath = Path.Combine(_tempRoot, "renamed-folder");
        string siblingPath = Path.Combine(sourcePath, "sibling.txt");
        string lockedPath = Path.Combine(sourcePath, "open.docx");
        await File.WriteAllTextAsync(siblingPath, "preserved sibling");
        await File.WriteAllTextAsync(lockedPath, "open document");

        Exception? renameError;
        using (FileStream lockedStream = File.Open(
                   lockedPath,
                   FileMode.Open,
                   FileAccess.ReadWrite,
                   FileShare.Read))
        {
            renameError = await Record.ExceptionAsync(() =>
                service.RenameEntryAsync(sourcePath, destinationPath));

            Assert.True(
                renameError is IOException or UnauthorizedAccessException,
                $"Expected a locked-entry rename failure, got: {renameError}");
            Assert.True(Directory.Exists(sourcePath));
            Assert.False(Directory.Exists(destinationPath));
            Assert.Equal("preserved sibling", await File.ReadAllTextAsync(siblingPath));
            Assert.True(File.Exists(lockedPath));
            Assert.Equal("open document".Length, lockedStream.Length);
            Assert.Equal(2, Directory.EnumerateFileSystemEntries(sourcePath).Count());
        }

        await service.RenameEntryAsync(sourcePath, destinationPath);

        Assert.False(Directory.Exists(sourcePath));
        Assert.True(Directory.Exists(destinationPath));
        Assert.Equal(
            "preserved sibling",
            await File.ReadAllTextAsync(Path.Combine(destinationPath, "sibling.txt")));
        Assert.Equal(
            "open document",
            await File.ReadAllTextAsync(Path.Combine(destinationPath, "open.docx")));
    }

    [Fact]
    public void TryIsPathUnderDirectoryResolved_VerifiesExistingChild()
    {
        string root = Directory.CreateDirectory(
            Path.Combine(_tempRoot, "resolved-root")).FullName;
        string child = Directory.CreateDirectory(
            Path.Combine(root, "child")).FullName;
        string sibling = Directory.CreateDirectory(
            Path.Combine(_tempRoot, "resolved-sibling")).FullName;

        Assert.True(FileService.TryIsPathUnderDirectoryResolved(
            child,
            root,
            out bool childIsUnderRoot));
        Assert.True(childIsUnderRoot);
        Assert.True(FileService.TryIsPathUnderDirectoryResolved(
            sibling,
            root,
            out bool siblingIsUnderRoot));
        Assert.False(siblingIsUnderRoot);
    }

    [Fact]
    public async Task DirectoryJunction_IsResolvedForTraversalAndFolderMetadata()
    {
        string target = Directory.CreateDirectory(
            Path.Combine(_tempRoot, "ditto", "current-version")).FullName;
        string childPath = Path.Combine(target, "readme.txt");
        await File.WriteAllTextAsync(childPath, "junction target");
        string junction = Path.Combine(_tempRoot, "current");

        Assert.True(
            TryCreateDirectoryJunction(junction, target),
            "The Windows test host must support creating a directory junction.");
        try
        {
            Assert.True(FileService.IsFileSystemLink(junction));
            Assert.True(FileService.TryResolveExistingPathForTraversal(
                junction,
                out string resolvedPath));
            Assert.Equal(
                Path.GetFullPath(target),
                resolvedPath,
                ignoreCase: true);

            FolderPathSnapshot snapshot =
                await FileService.CaptureDirectChildSnapshotAsync(junction);
            Assert.Equal(FolderSnapshotStatus.SuccessWithItems, snapshot.Status);
            Assert.Contains(
                snapshot.Paths,
                path => string.Equals(
                    path,
                    childPath,
                    StringComparison.OrdinalIgnoreCase));

            var service = new FileService();
            List<WidgetItem> items = await service.EnumerateDirectoryAsync(
                junction,
                loadIcons: false,
                loadFolderItemCounts: false);
            WidgetItem item = Assert.Single(items);
            Assert.Equal("readme", item.Name);
            Assert.Equal(
                childPath,
                item.Path,
                ignoreCase: true);
            Assert.Equal(1, await service.CountVisibleChildrenAsync(junction));

            string nestedTarget = Directory.CreateDirectory(
                Path.Combine(target, "nested")).FullName;
            string nestedLinkPath = Path.Combine(junction, "nested");
            Assert.True(FileService.TryResolveExistingPathForTraversal(
                nestedLinkPath,
                out string resolvedNestedPath));
            Assert.Equal(
                nestedTarget,
                resolvedNestedPath,
                ignoreCase: true);
        }
        finally
        {
            TryDeleteDirectoryJunction(junction);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecuteTransferPlanAsync_RejectsDirectoryDestinationInsideSource(bool move)
    {
        var service = new FileService();
        string sourceDirectory = Directory.CreateDirectory(Path.Combine(_tempRoot, "source-folder")).FullName;
        File.WriteAllText(Path.Combine(sourceDirectory, "file.txt"), "content");
        string destinationDirectory = Path.Combine(sourceDirectory, "nested", "source-folder");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.ExecuteTransferPlanAsync(
                [new FileService.FileTransferPlan(sourceDirectory, destinationDirectory)],
                move));

        Assert.Contains("itself", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(Directory.Exists(Path.Combine(sourceDirectory, "nested")));
        Assert.True(File.Exists(Path.Combine(sourceDirectory, "file.txt")));
    }

    [Fact]
    public async Task TransferItemsWithResultAsync_RejectsNestedDestinationBeforeCreatingIt()
    {
        var service = new FileService();
        string sourceDirectory = Directory.CreateDirectory(Path.Combine(_tempRoot, "source-folder")).FullName;
        string destinationRoot = Path.Combine(sourceDirectory, "mapped-widget");

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.TransferItemsWithResultAsync([sourceDirectory], destinationRoot, move: true));

        Assert.False(Directory.Exists(destinationRoot));
        Assert.True(Directory.Exists(sourceDirectory));
    }

    [Fact]
    public async Task RelocateDirectoryAsync_RejectsDirectoryDestinationInsideSource()
    {
        var service = new FileService();
        string sourceDirectory = Directory.CreateDirectory(Path.Combine(_tempRoot, "source-folder")).FullName;
        string destinationDirectory = Path.Combine(sourceDirectory, "nested");

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.RelocateDirectoryAsync(sourceDirectory, destinationDirectory));

        Assert.False(Directory.Exists(destinationDirectory));
        Assert.True(Directory.Exists(sourceDirectory));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TransferItemsWithResultAsync_RejectsDestinationInsideSourceThroughJunction(
        bool move)
    {
        var service = new FileService();
        string sourceDirectory = Directory.CreateDirectory(
            Path.Combine(_tempRoot, "physical-source")).FullName;
        File.WriteAllText(Path.Combine(sourceDirectory, "file.txt"), "content");
        string sourceAlias = Path.Combine(_tempRoot, "source-alias");

        Assert.True(
            TryCreateDirectoryJunction(sourceAlias, sourceDirectory),
            "The Windows test host must support creating a directory junction.");
        try
        {
            string destinationRoot = Path.Combine(sourceAlias, "nested");
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                service.TransferItemsWithResultAsync(
                    [sourceDirectory],
                    destinationRoot,
                    move));

            Assert.False(Directory.Exists(
                Path.Combine(sourceDirectory, "nested")));
            Assert.True(File.Exists(
                Path.Combine(sourceDirectory, "file.txt")));
        }
        finally
        {
            TryDeleteDirectoryJunction(sourceAlias);
        }
    }

    [Fact]
    public void IsEntryDirectlyInDirectoryResolved_RecognizesJunctionAlias()
    {
        string targetDirectory = Directory.CreateDirectory(
            Path.Combine(_tempRoot, "physical-target")).FullName;
        string filePath = Path.Combine(targetDirectory, "note.txt");
        File.WriteAllText(filePath, "content");
        string junction = Path.Combine(_tempRoot, "target-alias");

        Assert.True(
            TryCreateDirectoryJunction(junction, targetDirectory),
            "The Windows test host must support creating a directory junction.");
        try
        {
            Assert.True(FileService.IsEntryDirectlyInDirectoryResolved(
                Path.Combine(junction, "note.txt"),
                targetDirectory));
            Assert.True(FileService.IsEntryDirectlyInDirectoryResolved(
                filePath,
                junction));
        }
        finally
        {
            TryDeleteDirectoryJunction(junction);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TransferItemsWithResultAsync_MixedBatchSkipsOnlyEntriesAlreadyInDestination(
        bool move)
    {
        var service = new FileService();
        string destinationDirectory = Directory.CreateDirectory(
            Path.Combine(_tempRoot, "mixed-target")).FullName;
        string existingPath = Path.Combine(destinationDirectory, "existing.txt");
        File.WriteAllText(existingPath, "existing");
        string externalDirectory = Directory.CreateDirectory(
            Path.Combine(_tempRoot, "mixed-source")).FullName;
        string externalPath = Path.Combine(externalDirectory, "external.txt");
        File.WriteAllText(externalPath, "external");

        IReadOnlyList<FileService.FileTransferResult> results =
            await service.TransferItemsWithResultAsync(
                [existingPath, externalPath],
                destinationDirectory,
                move);

        FileService.FileTransferResult result = Assert.Single(results);
        Assert.Equal(externalPath, result.SourcePath);
        Assert.Equal(
            Path.Combine(destinationDirectory, "external.txt"),
            result.DestinationPath);
        Assert.True(File.Exists(existingPath));
        Assert.False(File.Exists(
            Path.Combine(destinationDirectory, "existing (2).txt")));
        Assert.Equal(!move, File.Exists(externalPath));
        Assert.Equal(
            "external",
            File.ReadAllText(result.DestinationPath));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TransferItemsWithResultAsync_SameDirectoryJunctionIsNoOp(
        bool move)
    {
        var service = new FileService();
        string destinationDirectory = Directory.CreateDirectory(
            Path.Combine(_tempRoot, "junction-no-op-target")).FullName;
        string physicalDirectory = Directory.CreateDirectory(
            Path.Combine(_tempRoot, "junction-no-op-source")).FullName;
        string junctionPath = Path.Combine(destinationDirectory, "linked-folder");

        Assert.True(
            TryCreateDirectoryJunction(junctionPath, physicalDirectory),
            "The Windows test host must support creating a directory junction.");
        try
        {
            IReadOnlyList<FileService.FileTransferResult> results =
                await service.TransferItemsWithResultAsync(
                    [junctionPath],
                    destinationDirectory,
                    move);

            Assert.Empty(results);
            Assert.True(Directory.Exists(junctionPath));
            Assert.False(Directory.Exists(
                Path.Combine(destinationDirectory, "linked-folder (2)")));
        }
        finally
        {
            TryDeleteDirectoryJunction(junctionPath);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecuteTransferPlanAsync_RejectsNestedJunctionBeforeRecursiveCopy(
        bool reportProgress)
    {
        var service = new FileService();
        string sourceDirectory = Directory.CreateDirectory(
            Path.Combine(_tempRoot, "source-with-loop")).FullName;
        File.WriteAllText(Path.Combine(sourceDirectory, "file.txt"), "content");
        string loopJunction = Path.Combine(sourceDirectory, "loop");
        string destinationDirectory = Path.Combine(_tempRoot, "copied-loop");

        Assert.True(
            TryCreateDirectoryJunction(loopJunction, sourceDirectory),
            "The Windows test host must support creating a directory junction.");
        try
        {
            IProgress<FileService.FileTransferProgress>? progress = reportProgress
                ? new Progress<FileService.FileTransferProgress>(_ => { })
                : null;
            FileService.FileTransferPartialFailureException junctionFailure =
                await Assert.ThrowsAsync<FileService.FileTransferPartialFailureException>(() =>
                    service.ExecuteTransferPlanAsync(
                        [new FileService.FileTransferPlan(
                            sourceDirectory,
                            destinationDirectory)],
                        move: false,
                        progress: progress));
            Assert.IsType<InvalidOperationException>(junctionFailure.InnerException);

            Assert.False(Directory.Exists(
                Path.Combine(destinationDirectory, "loop")));
            Assert.True(File.Exists(Path.Combine(sourceDirectory, "file.txt")));
        }
        finally
        {
            TryDeleteDirectoryJunction(loopJunction);
        }
    }

    [Fact]
    public async Task TransferItemsWithResultAsync_MovesFilesToAvailableNames()
    {
        var service = new FileService();
        string sourceDirectory = Directory.CreateDirectory(Path.Combine(_tempRoot, "source")).FullName;
        string destinationDirectory = Directory.CreateDirectory(Path.Combine(_tempRoot, "destination")).FullName;
        string sourcePath = Path.Combine(sourceDirectory, "note.txt");
        string existingDestinationPath = Path.Combine(destinationDirectory, "note.txt");
        File.WriteAllText(sourcePath, "source");
        File.WriteAllText(existingDestinationPath, "existing");

        var results = await service.TransferItemsWithResultAsync([sourcePath], destinationDirectory, move: true);

        var result = Assert.Single(results);
        Assert.Equal(sourcePath, result.SourcePath);
        Assert.Equal(Path.Combine(destinationDirectory, "note (2).txt"), result.DestinationPath);
        Assert.False(File.Exists(sourcePath));
        Assert.Equal("source", File.ReadAllText(result.DestinationPath));
        Assert.Equal("existing", File.ReadAllText(existingDestinationPath));
    }

    [Fact]
    public async Task ExecuteTransferPlanAsync_CopiesDirectoryRecursively()
    {
        var service = new FileService();
        string sourceDirectory = Directory.CreateDirectory(Path.Combine(_tempRoot, "source-folder")).FullName;
        string nestedDirectory = Directory.CreateDirectory(Path.Combine(sourceDirectory, "nested")).FullName;
        string sourceFile = Path.Combine(nestedDirectory, "file.txt");
        File.WriteAllText(sourceFile, "content");

        string destinationDirectory = Path.Combine(_tempRoot, "destination-folder");
        var results = await service.ExecuteTransferPlanAsync(
            [new FileService.FileTransferPlan(sourceDirectory, destinationDirectory)],
            move: false);

        var result = Assert.Single(results);
        Assert.Equal(sourceDirectory, result.SourcePath);
        Assert.Equal(destinationDirectory, result.DestinationPath);
        Assert.True(File.Exists(sourceFile));
        Assert.Equal("content", File.ReadAllText(Path.Combine(destinationDirectory, "nested", "file.txt")));
    }

    [Fact]
    public async Task ExecuteTransferPlanAsync_ReportsRealByteProgress()
    {
        var service = new FileService();
        string sourcePath = Path.Combine(_tempRoot, "progress-source.bin");
        string destinationPath = Path.Combine(_tempRoot, "progress-destination.bin");
        byte[] content = Enumerable.Range(0, 1024 * 1024 + 17)
            .Select(index => (byte)(index % 251))
            .ToArray();
        await File.WriteAllBytesAsync(sourcePath, content);
        var updates = new List<FileService.FileTransferProgress>();

        var results = await service.ExecuteTransferPlanAsync(
            [new FileService.FileTransferPlan(sourcePath, destinationPath)],
            move: false,
            progress: new InlineProgress<FileService.FileTransferProgress>(
                updates.Add));

        Assert.Single(results);
        FileService.FileTransferProgress completed = Assert.Single(
            updates.Where(update =>
                update.Phase == FileService.FileTransferPhase.Completed));
        Assert.Equal(content.LongLength, completed.TotalBytes);
        Assert.Equal(content.LongLength, completed.BytesTransferred);
        Assert.Equal(1, completed.CompletedItems);
        Assert.Equal(100d, completed.Percentage);
        Assert.Equal(content, await File.ReadAllBytesAsync(destinationPath));
    }

    [Fact]
    public async Task ExecuteTransferPlanAsync_MovesFileWithProgressWithoutDeletingDestination()
    {
        var service = new FileService();
        string sourcePath = Path.Combine(_tempRoot, "move-progress-source.txt");
        string destinationPath = Path.Combine(_tempRoot, "move-progress-destination.txt");
        const string content = "move progress must preserve the destination";
        await File.WriteAllTextAsync(sourcePath, content);
        long expectedBytes = new FileInfo(sourcePath).Length;
        var updates = new List<FileService.FileTransferProgress>();

        var results = await service.ExecuteTransferPlanAsync(
            [new FileService.FileTransferPlan(sourcePath, destinationPath)],
            move: true,
            progress: new InlineProgress<FileService.FileTransferProgress>(
                updates.Add));

        Assert.Single(results);
        Assert.False(File.Exists(sourcePath));
        Assert.True(File.Exists(destinationPath));
        Assert.Equal(content, await File.ReadAllTextAsync(destinationPath));
        FileService.FileTransferProgress completed = Assert.Single(
            updates.Where(update =>
                update.Phase == FileService.FileTransferPhase.Completed));
        Assert.Equal(expectedBytes, completed.BytesTransferred);
        Assert.Equal(100d, completed.Percentage);
    }

    [Theory]
    [InlineData(@"E:\source.bin", @"E:\folder\destination.bin", true)]
    [InlineData(@"F:\source.bin", @"E:\folder\destination.bin", false)]
    [InlineData(@"\\server\share\source.bin", @"\\server\share\folder\destination.bin", true)]
    [InlineData(@"\\server\share-a\source.bin", @"\\server\share-b\destination.bin", false)]
    public void CanUseAtomicMove_RequiresMatchingFileSystemRoot(
        string sourcePath,
        string destinationPath,
        bool expected)
    {
        Assert.Equal(
            expected,
            FileService.CanUseAtomicMove(sourcePath, destinationPath));
    }

    private static bool TryCreateDirectoryJunction(string junction, string target)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/d /c mklink /J \"{junction}\" \"{target}\"",
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            });
            process?.WaitForExit();
            return process?.ExitCode == 0 && Directory.Exists(junction);
        }
        catch (Exception ex) when (
            ex is IOException or UnauthorizedAccessException or
            System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    private static void TryDeleteDirectoryJunction(string junction)
    {
        try
        {
            Directory.Delete(junction, recursive: false);
        }
        catch
        {
        }
    }

    [Fact]
    public void CanUseLegacyShellMove_RejectsCrossVolumeAndMixedBatches()
    {
        Assert.True(FileService.CanUseLegacyShellMove(
        [
            new FileService.FileTransferPlan(
                @"E:\source-a.bin",
                @"E:\folder\source-a.bin"),
            new FileService.FileTransferPlan(
                @"E:\source-b.bin",
                @"E:\folder\source-b.bin")
        ]));
        Assert.False(FileService.CanUseLegacyShellMove(
        [
            new FileService.FileTransferPlan(
                @"E:\source.bin",
                @"F:\folder\source.bin")
        ]));
        Assert.False(FileService.CanUseLegacyShellMove(
        [
            new FileService.FileTransferPlan(
                @"E:\source-a.bin",
                @"E:\folder\source-a.bin"),
            new FileService.FileTransferPlan(
                @"E:\source-b.bin",
                @"F:\folder\source-b.bin")
        ]));
        Assert.False(FileService.CanUseLegacyShellMove([]));
    }

    [Fact]
    public void FileTransferProgress_UnknownTotalBeforeFirstCompletionIsIndeterminate()
    {
        var progress = new FileService.FileTransferProgress(
            FileService.FileTransferPhase.Transferring,
            "folder",
            CompletedItems: 0,
            TotalItems: 1,
            BytesTransferred: 1024,
            TotalBytes: null,
            BytesPerSecond: 512,
            EstimatedRemaining: null);

        Assert.Null(progress.Percentage);
    }

    [Fact]
    public void TryDeleteFileByIdentity_ClearsReadOnlyBeforeDeleting()
    {
        // The handle-bound delete clears read-only through the same handle,
        // so read-only sources still move cleanly.
        string sourcePath = Path.Combine(_tempRoot, "read-only-source.txt");
        File.WriteAllText(sourcePath, "content");
        File.SetAttributes(sourcePath, File.GetAttributes(sourcePath) | FileAttributes.ReadOnly);
        FileService.FileTransferSourceIdentity? identity =
            FileService.TryCaptureSourceIdentity(sourcePath);
        Assert.NotNull(identity);

        Assert.True(FileService.TryDeleteFileByIdentity(sourcePath, identity!.Value));
        Assert.False(File.Exists(sourcePath));
    }

    [Fact]
    public void ManagedMoveEngine_PostsShellRenameNotificationsForRawMoves()
    {
        string transferSource = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Services/FileService.TransferProgress.cs"));
        string win32Source = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Platform/Win32Helper.cs"));

        // Raw File.Move/Directory.Move/File.Delete leave Explorer views
        // (including the desktop) with stale icons because they post no
        // shell change notifications. Every managed move completion must
        // report the rename: file atomic, file chunked, directory atomic,
        // directory chunked.
        Assert.Equal(
            4,
            transferSource.Split(
                "NotifyShellItemMoved(",
                StringSplitOptions.None).Length - 1);
        Assert.Contains(
            "Win32Helper.NotifyShellItemMoved(sourceFilePath, destinationFilePath)",
            transferSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "Win32Helper.NotifyShellItemMoved(sourceDirectory, destinationDirectory)",
            transferSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "NotifyShellDirectoriesUpdated(completedOperations, move);",
            transferSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "Win32Helper.NotifyShellDirectoryUpdated(directory)",
            transferSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "internal static void NotifyShellItemMoved",
            win32Source,
            StringComparison.Ordinal);
        Assert.Contains(
            "internal static void NotifyShellDirectoryUpdated",
            win32Source,
            StringComparison.Ordinal);
        Assert.Contains(
            "ShcneRenameItem",
            win32Source,
            StringComparison.Ordinal);
        Assert.Contains(
            "ShcneUpdateDir",
            win32Source,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExecuteTransferPlanAsync_MoveCollisionNeverDeletesExistingDestination()
    {
        var service = new FileService();
        string sourcePath = Path.Combine(_tempRoot, "collision-source.txt");
        string destinationPath = Path.Combine(_tempRoot, "collision-destination.txt");
        await File.WriteAllTextAsync(sourcePath, "source");
        await File.WriteAllTextAsync(destinationPath, "existing destination");

        FileService.FileTransferPartialFailureException collisionFailure =
            await Assert.ThrowsAsync<FileService.FileTransferPartialFailureException>(() =>
            service.ExecuteTransferPlanAsync(
                [new FileService.FileTransferPlan(sourcePath, destinationPath)],
                move: true,
                progress: new InlineProgress<FileService.FileTransferProgress>(
                    _ => { })));
        Assert.IsAssignableFrom<IOException>(collisionFailure.InnerException);

        Assert.Equal("source", await File.ReadAllTextAsync(sourcePath));
        Assert.Equal(
            "existing destination",
            await File.ReadAllTextAsync(destinationPath));
    }

    [Fact]
    public async Task ExecuteTransferPlanAsync_CancelLeavesNoTruncatedFile()
    {
        // On a fast disk the 64 MB copy can finish inside the progress
        // throttle window, so the cancel lands either mid-copy (the partial
        // is removed through its own open handle) or after completion (the
        // full copy stays, Explorer semantics). Either way the user never
        // sees a truncated file at the destination.
        var service = new FileService();
        string sourcePath = Path.Combine(_tempRoot, "cancel-source.bin");
        string destinationPath = Path.Combine(_tempRoot, "cancel-destination.bin");
        await using (FileStream source = File.Create(sourcePath))
        {
            source.SetLength(64L * 1024 * 1024);
        }

        using var cancellation = new CancellationTokenSource();
        var updates = new List<FileService.FileTransferProgress>();
        var progress = new InlineProgress<FileService.FileTransferProgress>(update =>
        {
            updates.Add(update);
            if (update.BytesTransferred > 0)
            {
                cancellation.Cancel();
            }
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.ExecuteTransferPlanAsync(
                [new FileService.FileTransferPlan(sourcePath, destinationPath)],
                move: false,
                progress: progress,
                cancellationToken: cancellation.Token));

        Assert.True(File.Exists(sourcePath));
        if (File.Exists(destinationPath))
        {
            Assert.True(
                new FileInfo(destinationPath).Length == 64L * 1024 * 1024,
                "a surviving destination must be the complete copy");
        }

        int cancelingIndex = updates.FindIndex(update =>
            update.Phase == FileService.FileTransferPhase.Canceling);
        int canceledIndex = updates.FindIndex(update =>
            update.Phase == FileService.FileTransferPhase.Canceled);
        Assert.True(cancelingIndex >= 0);
        Assert.True(canceledIndex > cancelingIndex);
    }

    [Fact]
    public async Task ExecuteTransferPlanAsync_CancelDuringPreparationReportsTerminalCancellation()
    {
        var service = new FileService();
        string sourcePath = Path.Combine(_tempRoot, "prepare-cancel-source.bin");
        string destinationPath = Path.Combine(_tempRoot, "prepare-cancel-destination.bin");
        await File.WriteAllTextAsync(sourcePath, "content");

        using var cancellation = new CancellationTokenSource();
        var updates = new List<FileService.FileTransferProgress>();
        var progress = new InlineProgress<FileService.FileTransferProgress>(update =>
        {
            updates.Add(update);
            if (update.Phase == FileService.FileTransferPhase.Preparing)
            {
                cancellation.Cancel();
            }
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.ExecuteTransferPlanAsync(
                [new FileService.FileTransferPlan(sourcePath, destinationPath)],
                move: false,
                progress: progress,
                cancellationToken: cancellation.Token));

        Assert.True(File.Exists(sourcePath));
        Assert.False(File.Exists(destinationPath));
        Assert.Equal(
            [
                FileService.FileTransferPhase.Preparing,
                FileService.FileTransferPhase.Canceling,
                FileService.FileTransferPhase.Canceled
            ],
            updates.Select(update => update.Phase).ToArray());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecuteTransferPlanAsync_CancelAfterFirstItemKeepsCompletedItems(
        bool move)
    {
        var service = new FileService();
        string sourceDirectory = Directory.CreateDirectory(
            Path.Combine(_tempRoot, move ? "move-source" : "copy-source")).FullName;
        string destinationDirectory = Directory.CreateDirectory(
            Path.Combine(_tempRoot, move ? "move-destination" : "copy-destination")).FullName;
        string firstSource = Path.Combine(sourceDirectory, "first.txt");
        string secondSource = Path.Combine(sourceDirectory, "second.txt");
        string firstDestination = Path.Combine(destinationDirectory, "first.txt");
        string secondDestination = Path.Combine(destinationDirectory, "second.txt");
        await File.WriteAllTextAsync(firstSource, "first");
        await File.WriteAllTextAsync(secondSource, "second");

        using var cancellation = new CancellationTokenSource();
        var progress = new InlineProgress<FileService.FileTransferProgress>(update =>
        {
            if (update.CompletedItems == 1)
            {
                cancellation.Cancel();
            }
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.ExecuteTransferPlanAsync(
                [
                    new FileService.FileTransferPlan(firstSource, firstDestination),
                    new FileService.FileTransferPlan(secondSource, secondDestination)
                ],
                move,
                progress: progress,
                cancellationToken: cancellation.Token));

        // Explorer semantics: the completed item stays at its destination and,
        // for a move, its source is gone; the pending item never ran.
        Assert.Equal("first", await File.ReadAllTextAsync(firstDestination));
        Assert.Equal(
            move,
            File.Exists(firstSource) is false);
        Assert.Equal("second", await File.ReadAllTextAsync(secondSource));
        Assert.False(File.Exists(secondDestination));
    }

    [Fact]
    public async Task ExecuteTransferPlanAsync_ManySmallFilesThrottlesProgressCallbacks()
    {
        var service = new FileService();
        string sourceDirectory = Directory.CreateDirectory(
            Path.Combine(_tempRoot, "many-small-source")).FullName;
        string destinationDirectory = Path.Combine(
            _tempRoot,
            "many-small-destination");
        const int fileCount = 120;
        for (int index = 0; index < fileCount; index++)
        {
            await File.WriteAllTextAsync(
                Path.Combine(sourceDirectory, $"item-{index:D3}.txt"),
                index.ToString());
        }

        var updates = new List<FileService.FileTransferProgress>();
        await service.ExecuteTransferPlanAsync(
            [new FileService.FileTransferPlan(sourceDirectory, destinationDirectory)],
            move: false,
            progress: new InlineProgress<FileService.FileTransferProgress>(
                updates.Add));

        Assert.Equal(
            fileCount,
            Directory.EnumerateFiles(destinationDirectory).Count());
        Assert.True(
            updates.Count < fileCount,
            $"Expected throttled progress, received {updates.Count} updates.");
    }

    [Fact]
    public async Task ExecuteTransferPlanAsync_DirectoryStartsWithoutExactRecursivePreScan()
    {
        var service = new FileService();
        string sourceDirectory = Directory.CreateDirectory(
            Path.Combine(_tempRoot, "unknown-directory-total-source")).FullName;
        string nestedDirectory = Directory.CreateDirectory(
            Path.Combine(sourceDirectory, "nested")).FullName;
        await File.WriteAllTextAsync(
            Path.Combine(nestedDirectory, "archive.zip"),
            "content");
        string destinationDirectory = Path.Combine(
            _tempRoot,
            "unknown-directory-total-destination");
        var updates = new List<FileService.FileTransferProgress>();

        await service.ExecuteTransferPlanAsync(
            [new FileService.FileTransferPlan(
                sourceDirectory,
                destinationDirectory)],
            move: false,
            progress: new InlineProgress<FileService.FileTransferProgress>(
                updates.Add));

        FileService.FileTransferProgress transferring = updates.First(update =>
            update.Phase == FileService.FileTransferPhase.Transferring);
        FileService.FileTransferProgress completed = updates.Last(update =>
            update.Phase == FileService.FileTransferPhase.Completed);
        Assert.Null(transferring.TotalBytes);
        Assert.Null(completed.TotalBytes);
        Assert.Equal(1, completed.CompletedItems);
        Assert.Equal(100d, completed.Percentage);
        Assert.True(File.Exists(Path.Combine(
            destinationDirectory,
            "nested",
            "archive.zip")));
    }

    [Fact]
    [Trait("Category", "Hardware")]
    public async Task ExecuteTransferPlanAsync_RealCrossVolumeMoveReportsProgressAndCancelsPromptly()
    {
        string? sourceVolume = Environment.GetEnvironmentVariable(
            "DESKBOX_TEST_SOURCE_VOLUME");
        string? destinationVolume = Environment.GetEnvironmentVariable(
            "DESKBOX_TEST_DESTINATION_VOLUME");
        if (string.IsNullOrWhiteSpace(sourceVolume) ||
            string.IsNullOrWhiteSpace(destinationVolume))
        {
            return;
        }

        string runId = Guid.NewGuid().ToString("N");
        string sourceTestRoot = Path.Combine(
            Path.GetFullPath(sourceVolume),
            "DeskBox-TransferTests");
        string destinationTestRoot = Path.Combine(
            Path.GetFullPath(destinationVolume),
            "DeskBox-TransferTests");
        string sourceDirectory = Path.Combine(sourceTestRoot, runId);
        string destinationDirectory = Path.Combine(
            destinationTestRoot,
            runId);
        Directory.CreateDirectory(sourceDirectory);
        Directory.CreateDirectory(destinationDirectory);

        try
        {
            var service = new FileService();
            const long fileLength = 128L * 1024 * 1024;
            string successSource = Path.Combine(sourceDirectory, "success.bin");
            string successDestination = Path.Combine(
                destinationDirectory,
                "success.bin");
            await using (FileStream file = File.Create(successSource))
            {
                file.SetLength(fileLength);
            }

            Assert.False(FileService.CanUseAtomicMove(
                successSource,
                successDestination));
            var successUpdates = new List<FileService.FileTransferProgress>();
            IReadOnlyList<FileService.FileTransferResult> results =
                await service.ExecuteTransferPlanAsync(
                    [new FileService.FileTransferPlan(
                        successSource,
                        successDestination)],
                    move: true,
                    progress: new InlineProgress<FileService.FileTransferProgress>(
                        successUpdates.Add));

            Assert.Single(results);
            Assert.False(File.Exists(successSource));
            Assert.Equal(fileLength, new FileInfo(successDestination).Length);
            Assert.Contains(successUpdates, update =>
                update.Phase == FileService.FileTransferPhase.Transferring &&
                update.BytesTransferred > 0 &&
                update.BytesTransferred < fileLength);

            string cancelSource = Path.Combine(sourceDirectory, "cancel.bin");
            string cancelDestination = Path.Combine(
                destinationDirectory,
                "cancel.bin");
            await using (FileStream file = File.Create(cancelSource))
            {
                file.SetLength(fileLength);
            }

            using var cancellation = new CancellationTokenSource();
            var cancelUpdates = new List<FileService.FileTransferProgress>();
            var stopwatch = Stopwatch.StartNew();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                service.ExecuteTransferPlanAsync(
                    [new FileService.FileTransferPlan(
                        cancelSource,
                        cancelDestination)],
                    move: true,
                    progress: new InlineProgress<FileService.FileTransferProgress>(
                        update =>
                        {
                            cancelUpdates.Add(update);
                            if (update.BytesTransferred > 0)
                            {
                                cancellation.Cancel();
                            }
                        }),
                    cancellationToken: cancellation.Token));
            stopwatch.Stop();

            Assert.True(File.Exists(cancelSource));
            Assert.False(File.Exists(cancelDestination));
            Assert.Contains(cancelUpdates, update =>
                update.Phase == FileService.FileTransferPhase.Canceled);
            Assert.True(
                stopwatch.Elapsed < TimeSpan.FromSeconds(10),
                $"Cross-volume cancellation took {stopwatch.Elapsed}.");

            string batchFirstSource = Path.Combine(
                sourceDirectory,
                "batch-first.bin");
            string batchSecondSource = Path.Combine(
                sourceDirectory,
                "batch-second.bin");
            string batchFirstDestination = Path.Combine(
                destinationDirectory,
                "batch-first.bin");
            string batchSecondDestination = Path.Combine(
                destinationDirectory,
                "batch-second.bin");
            await using (FileStream file = File.Create(batchFirstSource))
            {
                file.SetLength(16L * 1024 * 1024);
            }
            await using (FileStream file = File.Create(batchSecondSource))
            {
                file.SetLength(fileLength);
            }

            using var batchCancellation = new CancellationTokenSource();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                service.ExecuteTransferPlanAsync(
                    [
                        new FileService.FileTransferPlan(
                            batchFirstSource,
                            batchFirstDestination),
                        new FileService.FileTransferPlan(
                            batchSecondSource,
                            batchSecondDestination)
                    ],
                    move: true,
                    progress: new InlineProgress<FileService.FileTransferProgress>(
                        update =>
                        {
                            if (update.CompletedItems == 1)
                            {
                                batchCancellation.Cancel();
                            }
                        }),
                    cancellationToken: batchCancellation.Token));

            Assert.Equal(
                16L * 1024 * 1024,
                new FileInfo(batchFirstSource).Length);
            Assert.Equal(fileLength, new FileInfo(batchSecondSource).Length);
            Assert.False(File.Exists(batchFirstDestination));
            Assert.False(File.Exists(batchSecondDestination));
        }
        finally
        {
            if (Directory.Exists(sourceDirectory))
            {
                Directory.Delete(sourceDirectory, recursive: true);
            }

            if (Directory.Exists(destinationDirectory))
            {
                Directory.Delete(destinationDirectory, recursive: true);
            }

            if (Directory.Exists(sourceTestRoot) &&
                !Directory.EnumerateFileSystemEntries(sourceTestRoot).Any())
            {
                Directory.Delete(sourceTestRoot, recursive: false);
            }

            if (Directory.Exists(destinationTestRoot) &&
                !Directory.EnumerateFileSystemEntries(destinationTestRoot).Any())
            {
                Directory.Delete(destinationTestRoot, recursive: false);
            }
        }
    }

    [Fact]
    public async Task ExecuteTransferPlanAsync_MovesDeepDirectoryWithoutMissingOrDuplicatingFiles()
    {
        var service = new FileService();
        string sourceDirectory = Directory.CreateDirectory(Path.Combine(_tempRoot, "source-folder")).FullName;
        string level1 = Directory.CreateDirectory(Path.Combine(sourceDirectory, "level1")).FullName;
        string level2 = Directory.CreateDirectory(Path.Combine(level1, "level2")).FullName;
        string level3 = Directory.CreateDirectory(Path.Combine(level2, "level3")).FullName;
        File.WriteAllText(Path.Combine(sourceDirectory, "root.txt"), "root");
        File.WriteAllText(Path.Combine(level1, "one.txt"), "one");
        File.WriteAllText(Path.Combine(level2, "two.txt"), "two");
        File.WriteAllText(Path.Combine(level3, "three.txt"), "three");

        string destinationDirectory = Path.Combine(_tempRoot, "destination-folder");
        var results = await service.ExecuteTransferPlanAsync(
            [new FileService.FileTransferPlan(sourceDirectory, destinationDirectory)],
            move: true);

        var result = Assert.Single(results);
        Assert.Equal(sourceDirectory, result.SourcePath);
        Assert.Equal(destinationDirectory, result.DestinationPath);
        Assert.False(Directory.Exists(sourceDirectory));
        Assert.Equal(4, Directory.EnumerateFiles(destinationDirectory, "*", SearchOption.AllDirectories).Count());
        Assert.Equal("root", File.ReadAllText(Path.Combine(destinationDirectory, "root.txt")));
        Assert.Equal("one", File.ReadAllText(Path.Combine(destinationDirectory, "level1", "one.txt")));
        Assert.Equal("two", File.ReadAllText(Path.Combine(destinationDirectory, "level1", "level2", "two.txt")));
        Assert.Equal("three", File.ReadAllText(Path.Combine(destinationDirectory, "level1", "level2", "level3", "three.txt")));
    }

    [Fact]
    public async Task ExecuteTransferPlanAsync_ShellMoveCompletesAndReportsResult()
    {
        var service = new FileService();
        string sourceDirectory = Directory.CreateDirectory(
            Path.Combine(_tempRoot, "shell-source")).FullName;
        string destinationDirectory = Directory.CreateDirectory(
            Path.Combine(_tempRoot, "shell-destination")).FullName;
        string sourcePath = Path.Combine(sourceDirectory, "note.txt");
        string destinationPath = Path.Combine(destinationDirectory, "note.txt");
        File.WriteAllText(sourcePath, "content");

        var results = await service.ExecuteTransferPlanAsync(
            [new FileService.FileTransferPlan(sourcePath, destinationPath)],
            move: true,
            useShellProgress: true,
            ownerWindowHandle: IntPtr.Zero);

        var result = Assert.Single(results);
        Assert.Equal(sourcePath, result.SourcePath);
        Assert.Equal(destinationPath, result.DestinationPath);
        Assert.False(File.Exists(sourcePath));
        Assert.Equal("content", File.ReadAllText(destinationPath));
    }

    [Fact]
    public async Task ExecuteTransferPlanAsync_ShellCopyCompletesAndDelegatesProgress()
    {
        var service = new FileService();
        string sourceDirectory = Directory.CreateDirectory(
            Path.Combine(_tempRoot, "shell-copy-source")).FullName;
        string destinationDirectory = Directory.CreateDirectory(
            Path.Combine(_tempRoot, "shell-copy-destination")).FullName;
        string sourcePath = Path.Combine(sourceDirectory, "manual.zip");
        string destinationPath = Path.Combine(
            destinationDirectory,
            "manual.zip");
        File.WriteAllText(sourcePath, "copy through Windows Shell");
        var updates = new List<FileService.FileTransferProgress>();

        var results = await service.ExecuteTransferPlanAsync(
            [new FileService.FileTransferPlan(sourcePath, destinationPath)],
            move: false,
            useShellProgress: true,
            ownerWindowHandle: IntPtr.Zero,
            progress: new InlineProgress<FileService.FileTransferProgress>(
                updates.Add));

        var result = Assert.Single(results);
        Assert.Equal(sourcePath, result.SourcePath);
        Assert.Equal(destinationPath, result.DestinationPath);
        Assert.True(File.Exists(sourcePath));
        Assert.Equal(
            "copy through Windows Shell",
            File.ReadAllText(destinationPath));
        Assert.Contains(updates, update =>
            update.Phase == FileService.FileTransferPhase.DelegatedToShell);
        Assert.Contains(updates, update =>
            update.Phase == FileService.FileTransferPhase.Completed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecuteTransferPlanAsync_SourceCleanupFailureKeepsCompleteDestination(
        bool reportProgress)
    {
        var service = new FileService();
        string sourceDirectory = Directory.CreateDirectory(
            Path.Combine(_tempRoot, $"cleanup-source-{reportProgress}")).FullName;
        string nestedDirectory = Directory.CreateDirectory(
            Path.Combine(sourceDirectory, "documents")).FullName;
        string sourceFile = Path.Combine(nestedDirectory, "report.pdf");
        File.WriteAllText(sourceFile, "complete destination content");
        string destinationDirectory = Path.Combine(
            _tempRoot,
            $"cleanup-destination-{reportProgress}");
        Directory.CreateDirectory(destinationDirectory);

        await using var sourceFileLock = new FileStream(
            sourceFile,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read);
        IProgress<FileService.FileTransferProgress>? progress = reportProgress
            ? new InlineProgress<FileService.FileTransferProgress>(_ => { })
            : null;

        FileService.FileTransferPartialFailureException exception =
            await Assert.ThrowsAsync<FileService.FileTransferPartialFailureException>(
                () => service.ExecuteTransferPlanAsync(
                    [new FileService.FileTransferPlan(
                        sourceDirectory,
                        destinationDirectory)],
                    move: true,
                    progress: progress));
        // The directory-move source cleanup failure rides as the inner
        // exception; the wrapper carries what physically completed.
        Assert.IsType<FileService.FileTransferSourceCleanupException>(exception.InnerException);

        FileService.FileTransferResult completed = Assert.Single(
            exception.CompletedResults);
        Assert.Equal(sourceDirectory, completed.SourcePath);
        Assert.Equal(destinationDirectory, completed.DestinationPath);
        Assert.Equal(
            "complete destination content",
            File.ReadAllText(Path.Combine(
                destinationDirectory,
                "documents",
                "report.pdf")));
        Assert.True(Directory.Exists(sourceDirectory));
        Assert.Equal(
            "complete destination content",
            File.ReadAllText(sourceFile));
    }

    [Fact]
    public async Task TransferItemsWithResultAsync_MovesDeepDirectoryToAvailableNameWhenDestinationExists()
    {
        var service = new FileService();
        string sourceRoot = Directory.CreateDirectory(Path.Combine(_tempRoot, "source")).FullName;
        string destinationRoot = Directory.CreateDirectory(Path.Combine(_tempRoot, "destination")).FullName;
        string sourceDirectory = Directory.CreateDirectory(Path.Combine(sourceRoot, "project")).FullName;
        string nestedDirectory = Directory.CreateDirectory(Path.Combine(sourceDirectory, "nested", "child")).FullName;
        File.WriteAllText(Path.Combine(nestedDirectory, "file.txt"), "content");
        Directory.CreateDirectory(Path.Combine(destinationRoot, "project"));

        var results = await service.TransferItemsWithResultAsync([sourceDirectory], destinationRoot, move: true);

        var result = Assert.Single(results);
        string expectedDestination = Path.Combine(destinationRoot, "project (2)");
        Assert.Equal(expectedDestination, result.DestinationPath);
        Assert.False(Directory.Exists(sourceDirectory));
        Assert.True(Directory.Exists(Path.Combine(destinationRoot, "project")));
        Assert.Equal("content", File.ReadAllText(Path.Combine(expectedDestination, "nested", "child", "file.txt")));
        Assert.Single(Directory.EnumerateFiles(expectedDestination, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task EnumerateDirectoryAsync_SkipsHiddenAndDesktopIniEntries()
    {
        var service = new FileService();
        string visibleFile = Path.Combine(_tempRoot, "visible.txt");
        string desktopIni = Path.Combine(_tempRoot, "desktop.ini");
        string hiddenFile = Path.Combine(_tempRoot, "hidden.txt");
        File.WriteAllText(visibleFile, "visible");
        File.WriteAllText(desktopIni, "desktop");
        File.WriteAllText(hiddenFile, "hidden");
        File.SetAttributes(hiddenFile, File.GetAttributes(hiddenFile) | FileAttributes.Hidden);

        var items = await service.EnumerateDirectoryAsync(_tempRoot);

        var item = Assert.Single(items);
        Assert.Equal("visible", item.Name);
        Assert.Equal(visibleFile, item.Path);
    }

    [Fact]
    public async Task EnumerateDirectoryAsync_CanShowFileExtensions()
    {
        var service = new FileService();
        string visibleFile = Path.Combine(_tempRoot, "visible.txt");
        File.WriteAllText(visibleFile, "visible");

        var items = await service.EnumerateDirectoryAsync(_tempRoot, showFileExtensions: true);

        var item = Assert.Single(items);
        Assert.Equal("visible.txt", item.Name);
    }

    [Fact]
    public async Task EnumerateDirectoryAsync_ExcludesShortcutExtensionByDefaultWhenShowingFileExtensions()
    {
        var service = new FileService();
        string shortcutFile = Path.Combine(_tempRoot, "app.lnk");
        File.WriteAllText(shortcutFile, "shortcut");

        var items = await service.EnumerateDirectoryAsync(_tempRoot, showFileExtensions: true);

        var item = Assert.Single(items);
        Assert.Equal("app", item.Name);
    }

    [Fact]
    public async Task EnumerateDirectoryAsync_CanShowShortcutExtension()
    {
        var service = new FileService();
        string shortcutFile = Path.Combine(_tempRoot, "app.lnk");
        File.WriteAllText(shortcutFile, "shortcut");

        var items = await service.EnumerateDirectoryAsync(
            _tempRoot,
            showFileExtensions: true,
            hideShortcutExtensionWhenShowingFileExtensions: false);

        var item = Assert.Single(items);
        Assert.Equal("app.lnk", item.Name);
    }

    [Fact]
    public async Task EnumerateDirectoryAsync_RecognizesInternetShortcutAndHidesExtension()
    {
        var service = new FileService();
        string shortcutFile = Path.Combine(_tempRoot, "Steam.url");
        await File.WriteAllTextAsync(
            shortcutFile,
            "[InternetShortcut]\nURL=https://example.invalid/game\nIconFile=%SystemRoot%\\System32\\shell32.dll\nIconIndex=0\n");

        var items = await service.EnumerateDirectoryAsync(
            _tempRoot,
            showFileExtensions: true,
            loadIcons: false);

        var item = Assert.Single(items);
        Assert.True(item.IsShortcut);
        Assert.Equal("Steam", item.Name);
        Assert.Equal("https://example.invalid/game", item.TargetPath);
    }

    [Fact]
    public void SteamInstallState_OnlyReportsNotInstalledFromCompleteEvidence()
    {
        Assert.Equal(
            SteamGameInstallState.Installed,
            FileService.EvaluateSteamGameInstallState(
                snapshotIsAuthoritative: true,
                [
                    (IsAvailable: false, HasManifest: false),
                    (IsAvailable: true, HasManifest: true)
                ]));
        Assert.Equal(
            SteamGameInstallState.Unknown,
            FileService.EvaluateSteamGameInstallState(
                snapshotIsAuthoritative: true,
                [(IsAvailable: false, HasManifest: false)]));
        Assert.Equal(
            SteamGameInstallState.Unknown,
            FileService.EvaluateSteamGameInstallState(
                snapshotIsAuthoritative: false,
                [(IsAvailable: true, HasManifest: false)]));
        Assert.Equal(
            SteamGameInstallState.Unknown,
            FileService.EvaluateSteamGameInstallState(
                snapshotIsAuthoritative: true,
                []));
        Assert.Equal(
            SteamGameInstallState.NotInstalled,
            FileService.EvaluateSteamGameInstallState(
                snapshotIsAuthoritative: true,
                [
                    (IsAvailable: true, HasManifest: false),
                    (IsAvailable: true, HasManifest: false)
                ]));
    }

    [Fact]
    public async Task TryCreateWidgetItemAsync_ReturnsNullForHiddenEntry()
    {
        var service = new FileService();
        string hiddenFile = Path.Combine(_tempRoot, "hidden.lnk");
        File.WriteAllText(hiddenFile, "hidden");
        File.SetAttributes(hiddenFile, File.GetAttributes(hiddenFile) | FileAttributes.Hidden);

        // The import insert path must agree with folder enumeration: a hidden
        // entry would otherwise be moved into managed storage while no tile
        // can ever display it.
        Assert.Null(await service.TryCreateWidgetItemAsync(hiddenFile));
    }

    [Fact]
    public async Task TryCreateWidgetItemAsync_ReturnsItemForUrlShortcutsIncludingStaleSteamGames()
    {
        var service = new FileService();
        string websiteUrl = Path.Combine(_tempRoot, "Website.url");
        await File.WriteAllTextAsync(
            websiteUrl,
            "[InternetShortcut]\nURL=https://example.invalid/\n");
        // A Steam game shortcut whose game is uninstalled must still display:
        // opening it launches Steam's install flow, so it is never "dead"
        // from the user's perspective. The app id is fake; the assertion
        // holds regardless of this machine's Steam library state.
        string steamGameUrl = Path.Combine(_tempRoot, "It Takes Two.url");
        await File.WriteAllTextAsync(
            steamGameUrl,
            "[InternetShortcut]\nURL=steam://rungameid/999999999\n");

        var websiteItem = await service.TryCreateWidgetItemAsync(
            websiteUrl,
            loadIcon: false,
            loadFolderItemCount: false,
            loadShortcutTarget: false);
        var steamGameItem = await service.TryCreateWidgetItemAsync(
            steamGameUrl,
            loadIcon: false,
            loadFolderItemCount: false,
            loadShortcutTarget: false);

        Assert.NotNull(websiteItem);
        Assert.Equal(websiteUrl, websiteItem.Path);
        Assert.NotNull(steamGameItem);
        Assert.Equal(steamGameUrl, steamGameItem.Path);
    }

    [Fact]
    public void IsFilteredFromWidgetDisplay_MatchesEnumerationFilters()
    {
        string visibleFile = Path.Combine(_tempRoot, "visible.txt");
        string hiddenFile = Path.Combine(_tempRoot, "hidden.txt");
        string desktopIni = Path.Combine(_tempRoot, "desktop.ini");
        string missingFile = Path.Combine(_tempRoot, "missing.txt");
        string websiteUrl = Path.Combine(_tempRoot, "Website.url");
        string steamGameUrl = Path.Combine(_tempRoot, "Game.url");
        File.WriteAllText(visibleFile, "visible");
        File.WriteAllText(hiddenFile, "hidden");
        File.WriteAllText(desktopIni, "ini");
        File.SetAttributes(hiddenFile, File.GetAttributes(hiddenFile) | FileAttributes.Hidden);
        File.WriteAllText(websiteUrl, "[InternetShortcut]\nURL=https://example.invalid/\n");
        File.WriteAllText(
            steamGameUrl,
            "[InternetShortcut]\nURL=steam://rungameid/999999999\n");

        Assert.False(FileService.IsFilteredFromWidgetDisplay(visibleFile));
        Assert.True(FileService.IsFilteredFromWidgetDisplay(hiddenFile));
        Assert.True(FileService.IsFilteredFromWidgetDisplay(desktopIni));
        Assert.True(FileService.IsFilteredFromWidgetDisplay(missingFile));
        Assert.False(FileService.IsFilteredFromWidgetDisplay(websiteUrl));
        Assert.False(FileService.IsFilteredFromWidgetDisplay(steamGameUrl));
        Assert.False(FileService.IsDeadSteamShortcutUrl(websiteUrl));
    }

    [Fact]
    public async Task CreateWidgetItemAsync_CanDeferBrokenShortcutTargetHydration()
    {
        var service = new FileService();
        string shortcutPath = Path.Combine(_tempRoot, "missing-target.lnk");
        string missingTargetPath = Path.Combine(_tempRoot, "missing", "app.exe");
        ShortcutHelper.CreateOrUpdateFolderShortcut(
            shortcutPath,
            missingTargetPath,
            "test shortcut");

        var item = await service.CreateWidgetItemAsync(
            shortcutPath,
            loadIcon: false,
            loadFolderItemCount: false,
            loadShortcutTarget: false);

        Assert.True(item.IsShortcut);
        Assert.Equal(string.Empty, item.TargetPath);
        Assert.Equal(
            Path.GetFullPath(missingTargetPath),
            await service.GetStoredShortcutTargetAsync(shortcutPath));
    }

    [Fact]
    public void ReadStoredMetadata_InvalidatesCacheWhenShortcutIsUpdated()
    {
        string shortcutPath = Path.Combine(_tempRoot, "cached.lnk");
        string firstTargetPath = Path.Combine(_tempRoot, "first", "app.exe");
        string secondTargetPath = Path.Combine(_tempRoot, "second", "app.exe");
        ShortcutHelper.CreateOrUpdateFolderShortcut(
            shortcutPath,
            firstTargetPath,
            "first");

        ShortcutInfo? first = ShortcutHelper.ReadStoredMetadata(shortcutPath);
        ShortcutHelper.CreateOrUpdateFolderShortcut(
            shortcutPath,
            secondTargetPath,
            "second");
        ShortcutInfo? second = ShortcutHelper.ReadStoredMetadata(shortcutPath);

        Assert.Equal(Path.GetFullPath(firstTargetPath), first?.TargetPath);
        Assert.Equal(Path.GetFullPath(secondTargetPath), second?.TargetPath);
        Assert.Equal("second", second?.Description);
    }

    [Fact]
    public async Task CreateWidgetItemAsync_FolderSecondaryInfoShowsVisibleItemCountOnly()
    {
        var service = new FileService();
        string folder = Directory.CreateDirectory(Path.Combine(_tempRoot, "folder")).FullName;
        File.WriteAllText(Path.Combine(folder, "first.txt"), "first");
        File.WriteAllText(Path.Combine(folder, "desktop.ini"), "desktop");
        string hiddenFile = Path.Combine(folder, "hidden.txt");
        File.WriteAllText(hiddenFile, "hidden");
        File.SetAttributes(hiddenFile, File.GetAttributes(hiddenFile) | FileAttributes.Hidden);

        var item = await service.CreateWidgetItemAsync(folder);

        Assert.True(item.IsFolder);
        Assert.Equal(1, item.FolderItemCount);
        Assert.Equal("1 项", item.SecondaryInfo);
    }

    [Fact]
    public void ShellKindCache_RemainsBoundedDuringLongRunningPathChurn()
    {
        FileService.ClearShellKindCache();
        try
        {
            for (int index = 0;
                 index < FileService.MaxShellKindCacheEntries + 512;
                 index++)
            {
                FileService.CacheShellKind(
                    $@"C:\synthetic\entry-{index}.bin",
                    "document");
            }

            Assert.Equal(
                FileService.MaxShellKindCacheEntries,
                FileService.ShellKindCacheEntryCount);
        }
        finally
        {
            FileService.ClearShellKindCache();
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempRoot))
        {
            File.SetAttributes(_tempRoot, FileAttributes.Normal);
            foreach (string path in Directory.EnumerateFileSystemEntries(_tempRoot, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(path, FileAttributes.Normal);
            }

            Directory.Delete(_tempRoot, recursive: true);
        }
    }

    [Fact]
    public void SourceIdentity_MatchesUnchangedFile()
    {
        string path = Path.Combine(_tempRoot, "identity-stable.txt");
        File.WriteAllText(path, "unchanged content");

        FileService.FileTransferSourceIdentity? identity =
            FileService.TryCaptureSourceIdentity(path);

        Assert.NotNull(identity);
        Assert.True(FileService.SourceFileMatchesIdentity(path, identity!.Value));
    }

    [Fact]
    public void SourceIdentity_DetectsReplacementWithSameLengthAndTimestamp()
    {
        // Same path, same length, same timestamp — only the NTFS file key can
        // tell this replacement apart from the file that was captured.
        string path = Path.Combine(_tempRoot, "identity-replaced.txt");
        File.WriteAllText(path, "AAAAAAAA");
        FileService.FileTransferSourceIdentity? identity =
            FileService.TryCaptureSourceIdentity(path);
        Assert.NotNull(identity);

        DateTime lastWriteUtc = File.GetLastWriteTimeUtc(path);
        File.Delete(path);
        File.WriteAllText(path, "BBBBBBBB");
        File.SetLastWriteTimeUtc(path, lastWriteUtc);

        Assert.False(FileService.SourceFileMatchesIdentity(path, identity!.Value));
    }

    [Fact]
    public void SourceIdentity_DetectsInPlaceEditDespiteSameFileId()
    {
        // An in-place rewrite keeps the NTFS file key, so the key alone must
        // never authorize deleting the source: the copied content may be
        // stale by the time the delete runs (Word/OneDrive in-place saves).
        string path = Path.Combine(_tempRoot, "identity-inplace.txt");
        File.WriteAllText(path, "AAAAAAAA");
        FileService.FileTransferSourceIdentity? identity =
            FileService.TryCaptureSourceIdentity(path);
        Assert.NotNull(identity);
        Assert.NotNull(identity!.Value.FileId);

        // Same object, same length, newer write time (a sub-timestamp-granularity
        // rewrite stays undetectable without a content hash — accepted).
        File.WriteAllText(path, "BBBBBBBB");
        File.SetLastWriteTimeUtc(path, identity.Value.LastWriteTimeUtc.AddSeconds(2));

        Assert.False(FileService.SourceFileMatchesIdentity(path, identity.Value));
    }

    [Fact]
    public async Task ExecuteTransferPlanAsync_CancelKeepsCompletedDirectoryCopyAndForeignFiles()
    {
        // Explorer semantics: canceling after a directory copy completed
        // keeps that copy; foreign files added to the destination also stay;
        // only the in-flight partial is removed (through its own handle).
        var service = new FileService();
        string sourceDirectory = Path.Combine(_tempRoot, "cancel-dir-src");
        Directory.CreateDirectory(sourceDirectory);
        File.WriteAllText(
            Path.Combine(sourceDirectory, "copied.txt"),
            "copied content");
        string bigSource = Path.Combine(_tempRoot, "cancel-big.bin");
        await using (FileStream big = File.Create(bigSource))
        {
            big.SetLength(64L * 1024 * 1024);
        }

        string destinationDirectory = Path.Combine(_tempRoot, "cancel-dir-dest");
        string bigDestination = Path.Combine(_tempRoot, "cancel-big-dest.bin");
        string foreignFile = Path.Combine(destinationDirectory, "foreign.txt");

        using var cancellation = new CancellationTokenSource();
        var progress = new InlineProgress<FileService.FileTransferProgress>(update =>
        {
            if (update.BytesTransferred > 1024 * 1024)
            {
                // Past the first, already-completed small directory entry: a
                // foreign file lands in the copied directory before the cancel.
                if (!File.Exists(foreignFile))
                {
                    Directory.CreateDirectory(destinationDirectory);
                    File.WriteAllText(foreignFile, "not deskbox data");
                }

                cancellation.Cancel();
            }
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.ExecuteTransferPlanAsync(
                [
                    new FileService.FileTransferPlan(sourceDirectory, destinationDirectory),
                    new FileService.FileTransferPlan(bigSource, bigDestination),
                ],
                move: false,
                progress: progress,
                cancellationToken: cancellation.Token));

        Assert.True(
            File.Exists(Path.Combine(destinationDirectory, "copied.txt")),
            "the completed copy stays (Explorer semantics)");
        Assert.True(File.Exists(foreignFile), "foreign files stay untouched");
        Assert.True(File.Exists(bigSource));
        if (File.Exists(bigDestination))
        {
            Assert.True(
                new FileInfo(bigDestination).Length == 64L * 1024 * 1024,
                "a surviving in-flight item must be the complete copy, never truncated");
        }
    }

    [Fact]
    public async Task ExecuteTransferPlanAsync_CancelKeepsCompletedCopyEvenWhenSourceChanged()
    {
        // The source was rewritten after the copy completed; the completed
        // destination stays (Explorer semantics) and the rewritten source is
        // untouched. The user's newest content lives in both places now,
        // and nothing deletes either.
        var service = new FileService();
        string sourcePath = Path.Combine(_tempRoot, "changed-source.txt");
        string destinationPath = Path.Combine(_tempRoot, "changed-dest.txt");
        File.WriteAllText(sourcePath, "first version");
        string bigSource = Path.Combine(_tempRoot, "changed-big.bin");
        await using (FileStream big = File.Create(bigSource))
        {
            big.SetLength(64L * 1024 * 1024);
        }
        string bigDestination = Path.Combine(_tempRoot, "changed-big-dest.bin");

        using var cancellation = new CancellationTokenSource();
        var progress = new InlineProgress<FileService.FileTransferProgress>(update =>
        {
            if (update.BytesTransferred > 1024 * 1024)
            {
                File.WriteAllText(sourcePath, "second versio");
                cancellation.Cancel();
            }
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.ExecuteTransferPlanAsync(
                [
                    new FileService.FileTransferPlan(sourcePath, destinationPath),
                    new FileService.FileTransferPlan(bigSource, bigDestination),
                ],
                move: false,
                progress: progress,
                cancellationToken: cancellation.Token));

        Assert.True(
            string.Equals(
                await File.ReadAllTextAsync(destinationPath),
                "first version",
                StringComparison.Ordinal),
            "the completed copy stays");
        Assert.Equal("second versio", await File.ReadAllTextAsync(sourcePath));
    }

    [Fact]
    public void MovePaths_HoldTheSourceHandleAcrossTheWholeTransaction()
    {
        string progressSource = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Services/FileService.TransferProgress.cs"));
        string managedMove = Slice(
            progressSource,
            "private static async Task MoveFileWithProgressAsync",
            "internal static bool CanUseAtomicMove");
        // One source handle (read+delete, shared for reading only) spans the
        // copy and the disposition: no gap between validation and deletion,
        // and concurrent writers are refused instead of raced.
        Assert.Contains("ShareRead,", managedMove, StringComparison.Ordinal);
        Assert.DoesNotContain("ShareWrite,", managedMove, StringComparison.Ordinal);
        Assert.Contains(
            "TrySetDispositionByHandle(sourceHandle",
            managedMove,
            StringComparison.Ordinal);
        // An uncapturable identity must never authorize the delete either:
        // fail closed and keep both copies.
        Assert.Contains(
            "FileTransferSourceCleanupException",
            managedMove,
            StringComparison.Ordinal);
        // A failed copy removes its destination through the still-open
        // handle, never by path.
        Assert.Contains(
            "TryDisposeWithHandleDeletion(destination)",
            managedMove,
            StringComparison.Ordinal);

        string service = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Services/FileService.cs"));
        string fallbackMove = Slice(
            service,
            "private static Task MoveFileAsync",
            "private static async Task MoveDirectoryAsync");
        // The headless fallback delegates to the same transaction instead of
        // reimplementing a second copy-and-delete sequence.
        Assert.Contains(
            "MoveFileWithProgressAsync(",
            fallbackMove,
            StringComparison.Ordinal);
    }

    [Fact]
    public void SourceIdentity_MatchingRequiresFileIdAndLengthAndTimestampTogether()
    {
        // A matching key proves the same object, not unchanged content: the
        // conjunction must stay in the source so a future edit cannot regress
        // to key-only matching.
        string service = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Services/FileService.cs"));
        string matches = Slice(
            service,
            "internal static bool SourceFileMatchesIdentity",
            "// ─── Steam dead-shortcut detection");

        Assert.Contains(
            "current.Length == expected.Length",
            matches,
            StringComparison.Ordinal);
        Assert.Contains(
            "current.LastWriteTimeUtc == expected.LastWriteTimeUtc",
            matches,
            StringComparison.Ordinal);
        Assert.Contains(
            "VolumeSerialNumber != current.VolumeSerialNumber",
            matches,
            StringComparison.Ordinal);
        // The key-only short circuit must never come back.
        Assert.DoesNotContain(
            "return expectedKey == currentKey;",
            matches,
            StringComparison.Ordinal);
    }

    [Fact]
    public void CompletedTransfers_AreNeverRolledBack()
    {
        // Explorer semantics: a cancel or failure never deletes completed
        // work. The rollback machinery this pins against was removed
        // wholesale; these guards keep it from creeping back.
        string service = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Services/FileService.cs"));
        string progress = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Services/FileService.TransferProgress.cs"));
        foreach (string source in new[] { service, progress })
        {
            Assert.DoesNotContain("RollbackTransfersAsync", source, StringComparison.Ordinal);
            Assert.DoesNotContain("RollbackCopiedEntry", source, StringComparison.Ordinal);
            Assert.DoesNotContain("RollbackCopiedDirectory", source, StringComparison.Ordinal);
            Assert.DoesNotContain("TryDeleteEmptyDestinationTree", source, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task CopyEntryAsync_NeverDeletesACompetingDestinationFile()
    {
        // A file that appears at the planned destination after planning is
        // not ours: the copy must fail and leave it untouched. The old
        // File.Copy catch deleted whatever sat at the destination.
        var service = new FileService();
        string sourcePath = Path.Combine(_tempRoot, "competing-source.txt");
        string destinationPath = Path.Combine(_tempRoot, "competing-dest.txt");
        File.WriteAllText(sourcePath, "deskbox payload");
        File.WriteAllText(destinationPath, "someone else's file");

        await Assert.ThrowsAnyAsync<IOException>(() =>
            service.ExecuteTransferPlanAsync(
                [new FileService.FileTransferPlan(sourcePath, destinationPath)],
                move: false,
                progress: null));

        Assert.Equal(
            "someone else's file",
            await File.ReadAllTextAsync(destinationPath));
        Assert.True(File.Exists(sourcePath));
    }

    [Fact]
    public async Task RestoreMigratedDirectory_KeepsAllDuplicates()
    {
        // Rollback merge never auto-deletes: size+timestamp equality is not
        // an ownership proof, and the copied side may hold the last copy of
        // files already deleted from a partially-cleaned original.
        string copiedDirectory = Path.Combine(_tempRoot, "merge-copied");
        string originalDirectory = Path.Combine(_tempRoot, "merge-original");
        Directory.CreateDirectory(copiedDirectory);
        Directory.CreateDirectory(originalDirectory);
        DateTime stamp = DateTime.UtcNow.AddDays(-1);

        string matchingCopied = Path.Combine(copiedDirectory, "matching.txt");
        string matchingOriginal = Path.Combine(originalDirectory, "matching.txt");
        File.WriteAllText(matchingCopied, "same content");
        File.WriteAllText(matchingOriginal, "same content");
        File.SetLastWriteTimeUtc(matchingCopied, stamp);
        File.SetLastWriteTimeUtc(matchingOriginal, stamp);

        string divergedCopied = Path.Combine(copiedDirectory, "diverged.txt");
        string divergedOriginal = Path.Combine(originalDirectory, "diverged.txt");
        File.WriteAllText(divergedCopied, "short");
        File.WriteAllText(divergedOriginal, "longer original content");

        await FileService.RestoreMigratedDirectoryPreservingExistingAsync(
            copiedDirectory,
            originalDirectory);

        Assert.True(
            File.Exists(matchingCopied),
            "even a matching duplicate stays: never auto-deleted in rollback");
        Assert.True(File.Exists(matchingOriginal));
        Assert.True(
            File.Exists(divergedCopied),
            "a diverged duplicate must be kept instead of guessed at");
        Assert.Equal(
            "longer original content",
            await File.ReadAllTextAsync(divergedOriginal));
    }

    [Fact]
    public void StripBlockingAttributes_RestoresTheOriginalState()
    {
        string path = Path.Combine(_tempRoot, "stripped.txt");
        File.WriteAllText(path, "payload");
        File.SetAttributes(path, FileAttributes.Hidden | FileAttributes.Archive);

        System.IO.FileAttributes? original = FileService.StripBlockingAttributes(path);
        Assert.NotNull(original);
        Assert.Equal(
            FileAttributes.None,
            File.GetAttributes(path) & FileAttributes.Hidden);

        FileService.RestoreAttributes(path, original);
        Assert.Equal(
            FileAttributes.Hidden,
            File.GetAttributes(path) & FileAttributes.Hidden);

        // A file with no blocking attributes reports nothing to restore,
        // and restoring null is a no-op.
        File.SetAttributes(path, FileAttributes.Archive);
        Assert.Null(FileService.StripBlockingAttributes(path));
        FileService.RestoreAttributes(path, null);
        Assert.Equal(
            FileAttributes.Archive,
            File.GetAttributes(path) & ~FileAttributes.System);
    }

    [Fact]
    public void AttributeStrip_IsSerializedAgainstLostUpdateRaces()
    {
        string service = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Services/FileService.cs"));
        string strip = Slice(
            service,
            "internal static System.IO.FileAttributes? StripBlockingAttributes",
            "internal static void RestoreAttributes");
        string restore = Slice(
            service,
            "internal static void RestoreAttributes",
            "private static async Task<StorageFile?> TryGetStorageFileAsync");

        // Without the gate a second stripper can read the already-stripped
        // state as "original" and restore it over the first restore.
        Assert.Contains("lock (s_attributeStripGate)", strip, StringComparison.Ordinal);
        Assert.Contains("lock (s_attributeStripGate)", restore, StringComparison.Ordinal);
        Assert.Contains(
            "Failed to restore attributes",
            restore,
            StringComparison.Ordinal);
    }

    [Fact]
    public void SafeCopyCore_IsTheOnlyFileCopyPath()
    {
        string service = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Services/FileService.cs"));
        string copyEntry = Slice(
            service,
            "private static async Task CopyEntryAsync",
            "private static async Task MoveEntryAsync");
        Assert.Contains(
            "CopyFileWithProgressAsync(",
            copyEntry,
            StringComparison.Ordinal);
        // The P0 pattern: a blind catch deleting whatever sits at the
        // destination must never come back.
        Assert.DoesNotContain("File.Delete(destinationPath)", copyEntry, StringComparison.Ordinal);

        string progressSource = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Services/FileService.TransferProgress.cs"));
        string copyCore = Slice(
            progressSource,
            "private static async Task<(FileStream Stream, FileTransferSourceIdentity? Identity)> CopyFileCoreAsync",
            "private static void TryDisposeQuietly");
        // The destination is created once with delete access and shared with
        // nobody; the caller owns the commit, and failures delete through
        // the still-open handle.
        Assert.Contains(
            "CreateTransferDestinationStream(",
            copyCore,
            StringComparison.Ordinal);
        Assert.Contains(
            "TryDisposeWithHandleDeletion(destination)",
            copyCore,
            StringComparison.Ordinal);
    }

    [Fact]
    public void SourceIdentity_RejectsAMissingFileIdOnEitherSide()
    {
        string path = Path.Combine(_tempRoot, "identity-asymmetric.txt");
        File.WriteAllText(path, "payload");
        FileService.FileTransferSourceIdentity? identity =
            FileService.TryCaptureSourceIdentity(path);
        Assert.NotNull(identity);
        Assert.NotNull(identity!.Value.FileId);

        // One stat saw a key, the other did not: the object at the path is
        // no longer the one that was captured.
        var withoutKey = identity.Value with { FileId = null };
        Assert.False(FileService.SourceIdentityMatches(withoutKey, identity.Value));
        Assert.False(FileService.SourceIdentityMatches(identity.Value, withoutKey));
    }

    [Fact]
    public void SourceIdentity_CapturesDirectoriesToo()
    {
        // Recovery receipts identify folder items as objects, not by their
        // mtime: the native handle opens directories via
        // FILE_FLAG_BACKUP_SEMANTICS.
        string directory = Directory.CreateDirectory(
            Path.Combine(_tempRoot, "identity-dir")).FullName;
        File.WriteAllText(Path.Combine(directory, "child.txt"), "content");

        FileService.FileTransferSourceIdentity? identity =
            FileService.TryCaptureSourceIdentity(directory);
        Assert.NotNull(identity);
        Assert.NotNull(identity!.Value.FileId);

        // Same directory restated matches; a replaced directory does not.
        Assert.True(FileService.SourceFileMatchesIdentity(directory, identity.Value));
        string replacement = Path.Combine(_tempRoot, "identity-dir-2");
        Directory.CreateDirectory(replacement);
        FileService.FileTransferSourceIdentity? otherIdentity =
            FileService.TryCaptureSourceIdentity(replacement);
        Assert.NotNull(otherIdentity);
        Assert.False(FileService.SourceFileMatchesIdentity(replacement, identity.Value));
    }

    [Fact]
    public void SourceIdentity_UsesThe128BitFileIdPath()
    {
        // The FileIdInfo query must actually succeed: the native struct's
        // volume serial is ULONGLONG (24-byte layout), and an undersized
        // layout made every call fail silently onto the legacy 64-bit view.
        string path = Path.Combine(_tempRoot, "identity-128.txt");
        File.WriteAllText(path, "payload");
        FileService.FileTransferSourceIdentity? identity =
            FileService.TryCaptureSourceIdentity(path);
        Assert.NotNull(identity);
        Assert.NotNull(identity!.Value.FileId);
        // NTFS file ids are currently allocated in a low range, so the high
        // 64 bits stay zero there; the discriminator is that the identity
        // came from the 128-bit query, which fails closed on capture errors
        // (no legacy fallback anymore).
        Assert.True(
            FileService.SourceFileMatchesIdentity(path, identity.Value));
    }

    [Fact]
    public void TryDeleteFileByIdentity_RemovesTheVerifiedObjectAndKeepsAReplacement()
    {
        string path = Path.Combine(_tempRoot, "handle-delete.txt");
        File.WriteAllText(path, "original");
        FileService.FileTransferSourceIdentity? identity =
            FileService.TryCaptureSourceIdentity(path);
        Assert.NotNull(identity);

        // Unchanged object: the handle-bound delete removes exactly it.
        Assert.True(FileService.TryDeleteFileByIdentity(path, identity!.Value));
        Assert.False(File.Exists(path));

        // A replacement at the same path with the same length and timestamp
        // must survive: the delete refuses to act on the path alone.
        File.WriteAllText(path, "original");
        DateTime stamp = File.GetLastWriteTimeUtc(path);
        File.Delete(path);
        File.WriteAllText(path, "REPLACED!");
        File.SetLastWriteTimeUtc(path, stamp);
        FileService.FileTransferSourceIdentity? replacedIdentity =
            FileService.TryCaptureSourceIdentity(path);
        Assert.NotEqual(identity.Value.FileId, replacedIdentity!.Value.FileId);

        Assert.False(FileService.TryDeleteFileByIdentity(path, identity.Value));
        Assert.True(File.Exists(path));
        Assert.Equal("REPLACED!", File.ReadAllText(path));
    }

    [Fact]
    public void PartialDestination_IsDeletedThroughItsOwnOpenHandle()
    {
        // The catch-time cleanup deletes through the still-open handle
        // bound to the object this transfer created — never by path.
        string path = Path.Combine(_tempRoot, "partial-handle-delete.bin");
        using FileStream destination = FileService.CreateTransferDestinationStream(path);
        destination.Write([1, 2, 3, 4], 0, 4);
        destination.Flush();
        Assert.True(File.Exists(path));

        FileService.TryDisposeWithHandleDeletion(destination);

        Assert.False(File.Exists(path), "the partial file must disappear when its handle closes");
    }

    [Fact]
    public async Task CopyFailure_WithANameConflict_KeepsBothTheForeignFileAndTheCreatedCopy()
    {
        // A foreign file already occupies the planned name, so the copy
        // creates "a (2).txt"; a later item fails. Explorer semantics: the
        // completed copy stays next to the foreign file, untouched.
        var service = new FileService();
        string sourceDirectory = Path.Combine(_tempRoot, "conflict-src");
        Directory.CreateDirectory(sourceDirectory);
        string sourceFile = Path.Combine(sourceDirectory, "a.txt");
        File.WriteAllText(sourceFile, "shared payload");

        string destinationDirectory = Path.Combine(_tempRoot, "conflict-dest");
        Directory.CreateDirectory(destinationDirectory);
        string foreignFile = Path.Combine(destinationDirectory, "a.txt");
        File.WriteAllText(foreignFile, "shared payload");
        DateTime stamp = DateTime.UtcNow.AddDays(-1);
        File.SetLastWriteTimeUtc(sourceFile, stamp);
        File.SetLastWriteTimeUtc(foreignFile, stamp);

        string lockedSource = Path.Combine(_tempRoot, "conflict-locked.txt");
        File.WriteAllText(lockedSource, "will fail");
        await using var lockHandle = new FileStream(
            lockedSource,
            FileMode.Open,
            FileAccess.Read,
            FileShare.None);

        await Assert.ThrowsAnyAsync<Exception>(() =>
            service.ExecuteTransferPlanAsync(
                [
                    new FileService.FileTransferPlan(sourceDirectory, destinationDirectory),
                    new FileService.FileTransferPlan(lockedSource, Path.Combine(_tempRoot, "conflict-locked-dest.txt")),
                ],
                move: false,
                progress: null));

        Assert.True(
            File.Exists(foreignFile),
            "the pre-existing foreign file must survive");
        Assert.Equal("shared payload", await File.ReadAllTextAsync(foreignFile));
        Assert.True(
            File.Exists(Path.Combine(destinationDirectory, "a (2).txt")),
            "the completed copy stays (Explorer semantics)");
        Assert.True(File.Exists(sourceFile));
    }

    [Fact]
    public async Task Copy_PreservesSourceTimestampsOnTheDestination()
    {
        var service = new FileService();
        string sourcePath = Path.Combine(_tempRoot, "metadata-source.txt");
        string destinationPath = Path.Combine(_tempRoot, "metadata-dest.txt");
        await File.WriteAllTextAsync(sourcePath, "metadata probe");
        DateTime writeStamp = DateTime.UtcNow.AddDays(-3);
        DateTime createStamp = DateTime.UtcNow.AddDays(-4);
        File.SetLastWriteTimeUtc(sourcePath, writeStamp);
        File.SetCreationTimeUtc(sourcePath, createStamp);

        await service.ExecuteTransferPlanAsync(
            [new FileService.FileTransferPlan(sourcePath, destinationPath)],
            move: false,
            progress: null);

        var destination = new FileInfo(destinationPath);
        Assert.True(File.Exists(destinationPath));
        Assert.Equal(
            writeStamp,
            destination.LastWriteTimeUtc,
            TimeSpan.FromSeconds(2));
        Assert.Equal(
            createStamp,
            destination.CreationTimeUtc,
            TimeSpan.FromSeconds(2));
    }

    [Fact]
    public void SourceIdentity_RejectsMatchingWithoutAnyFileId()
    {
        // File systems without stable object ids (some network/cloud
        // providers) cannot authorize a destructive delete: metadata alone
        // is not an ownership proof.
        string path = Path.Combine(_tempRoot, "identity-no-key.txt");
        File.WriteAllText(path, "payload");
        FileService.FileTransferSourceIdentity? identity =
            FileService.TryCaptureSourceIdentity(path);
        Assert.NotNull(identity);

        var withoutKeys = identity!.Value with { FileId = null };
        Assert.False(FileService.SourceIdentityMatches(withoutKeys, withoutKeys));
    }

    [Fact]
    public async Task ExecuteTransferPlanAsync_CancelSurfacesCompletedResults()
    {
        // Explorer semantics: completed items stay AND ride the exception so
        // journals/history record what physically finished.
        var service = new FileService();
        string sourceDirectory = Directory.CreateDirectory(
            Path.Combine(_tempRoot, "cr-src")).FullName;
        string destinationDirectory = Directory.CreateDirectory(
            Path.Combine(_tempRoot, "cr-dest")).FullName;
        string firstSource = Path.Combine(sourceDirectory, "first.txt");
        string secondSource = Path.Combine(sourceDirectory, "second.txt");
        File.WriteAllText(firstSource, "first");
        File.WriteAllText(secondSource, "second");

        using var cancellation = new CancellationTokenSource();
        var progress = new InlineProgress<FileService.FileTransferProgress>(update =>
        {
            if (update.CompletedItems == 1)
            {
                cancellation.Cancel();
            }
        });

        FileService.FileTransferCanceledException exception =
            await Assert.ThrowsAsync<FileService.FileTransferCanceledException>(() =>
                service.ExecuteTransferPlanAsync(
                    [
                        new FileService.FileTransferPlan(
                            firstSource,
                            Path.Combine(destinationDirectory, "first.txt")),
                        new FileService.FileTransferPlan(
                            secondSource,
                            Path.Combine(destinationDirectory, "second.txt")),
                    ],
                    move: false,
                    progress: progress,
                    cancellationToken: cancellation.Token));

        FileService.FileTransferResult completed = Assert.Single(exception.CompletedResults);
        Assert.Equal(firstSource, completed.SourcePath);
    }

    [Fact]
    public async Task MoveBatchCancel_LeavesACompletedMoveAndItsReplacementAlone()
    {
        // A completed move stays moved (Explorer semantics); whatever later
        // occupies the destination path is never swapped back.
        var service = new FileService();
        string sourceDirectory = Directory.CreateDirectory(
            Path.Combine(_tempRoot, "moveback-src")).FullName;
        string firstSource = Path.Combine(sourceDirectory, "first.txt");
        string secondSource = Path.Combine(sourceDirectory, "second.txt");
        string destinationDirectory = Directory.CreateDirectory(
            Path.Combine(_tempRoot, "moveback-dest")).FullName;
        string firstDestination = Path.Combine(destinationDirectory, "first.txt");
        File.WriteAllText(firstSource, "moved content");
        File.WriteAllText(secondSource, "second");

        using var cancellation = new CancellationTokenSource();
        var progress = new InlineProgress<FileService.FileTransferProgress>(update =>
        {
            if (update.CompletedItems == 1 && File.Exists(firstDestination))
            {
                // Replace the moved object at the destination path.
                File.Delete(firstDestination);
                File.WriteAllText(firstDestination, "someone else's file");
                cancellation.Cancel();
            }
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.ExecuteTransferPlanAsync(
                [
                    new FileService.FileTransferPlan(firstSource, firstDestination),
                    new FileService.FileTransferPlan(secondSource, Path.Combine(destinationDirectory, "second.txt")),
                ],
                move: true,
                progress: progress,
                cancellationToken: cancellation.Token));

        Assert.Equal(
            "someone else's file",
            await File.ReadAllTextAsync(firstDestination));
        Assert.False(
            File.Exists(firstSource),
            "the completed move stays moved; nothing is swapped back");
    }

    private static string Slice(string source, string startMarker, string endMarker)
    {
        int start = source.IndexOf(startMarker, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Start marker not found: {startMarker}");
        int end = source.IndexOf(endMarker, start + startMarker.Length, StringComparison.Ordinal);
        Assert.True(end > start, $"End marker not found: {endMarker}");
        return source[start..end];
    }

    private sealed class InlineProgress<T>(Action<T> callback) : IProgress<T>
    {
        public void Report(T value) => callback(value);
    }

}
