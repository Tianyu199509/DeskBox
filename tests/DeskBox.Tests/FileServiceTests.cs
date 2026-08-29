using System.Diagnostics;
using DeskBox.Helpers;
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
    public void DeleteSourceFileAfterCopy_ClearsReadOnlyBeforeDeleting()
    {
        string sourcePath = Path.Combine(_tempRoot, "read-only-source.txt");
        File.WriteAllText(sourcePath, "content");
        FileAttributes attributes = File.GetAttributes(sourcePath) |
            FileAttributes.ReadOnly;
        File.SetAttributes(sourcePath, attributes);

        FileService.DeleteSourceFileAfterCopy(sourcePath, attributes);

        Assert.False(File.Exists(sourcePath));
    }

    [Fact]
    public async Task ExecuteTransferPlanAsync_MoveCollisionNeverDeletesExistingDestination()
    {
        var service = new FileService();
        string sourcePath = Path.Combine(_tempRoot, "collision-source.txt");
        string destinationPath = Path.Combine(_tempRoot, "collision-destination.txt");
        await File.WriteAllTextAsync(sourcePath, "source");
        await File.WriteAllTextAsync(destinationPath, "existing destination");

        await Assert.ThrowsAsync<IOException>(() =>
            service.ExecuteTransferPlanAsync(
                [new FileService.FileTransferPlan(sourcePath, destinationPath)],
                move: true,
                progress: new InlineProgress<FileService.FileTransferProgress>(
                    _ => { })));

        Assert.Equal("source", await File.ReadAllTextAsync(sourcePath));
        Assert.Equal(
            "existing destination",
            await File.ReadAllTextAsync(destinationPath));
    }

    [Fact]
    public async Task ExecuteTransferPlanAsync_CancelRemovesPartialCopy()
    {
        var service = new FileService();
        string sourcePath = Path.Combine(_tempRoot, "cancel-source.bin");
        string destinationPath = Path.Combine(_tempRoot, "cancel-destination.bin");
        await using (FileStream source = File.Create(sourcePath))
        {
            source.SetLength(16L * 1024 * 1024);
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
        Assert.False(File.Exists(destinationPath));
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
    public async Task ExecuteTransferPlanAsync_CancelAfterFirstItemRollsBackCompletedBatch(
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

        Assert.Equal("first", await File.ReadAllTextAsync(firstSource));
        Assert.Equal("second", await File.ReadAllTextAsync(secondSource));
        Assert.False(File.Exists(firstDestination));
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

        FileService.FileTransferSourceCleanupException exception =
            await Assert.ThrowsAsync<FileService.FileTransferSourceCleanupException>(
                () => service.ExecuteTransferPlanAsync(
                    [new FileService.FileTransferPlan(
                        sourceDirectory,
                        destinationDirectory)],
                    move: true,
                    progress: progress));

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
            "[InternetShortcut]\nURL=steam://rungameid/123\nIconFile=%ProgramFiles%\\Steam\\steam.exe\nIconIndex=0\n");

        var items = await service.EnumerateDirectoryAsync(
            _tempRoot,
            showFileExtensions: true,
            loadIcons: false);

        var item = Assert.Single(items);
        Assert.True(item.IsShortcut);
        Assert.Equal("Steam", item.Name);
        Assert.Equal("steam://rungameid/123", item.TargetPath);
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

    private sealed class InlineProgress<T>(Action<T> callback) : IProgress<T>
    {
        public void Report(T value) => callback(value);
    }

}
