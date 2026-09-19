using System.Collections.Concurrent;
using DeskBox.Models;
using DeskBox.Services;

namespace DeskBox.Tests;

/// <summary>
/// Safety contracts for the storage-migration source cleanup: nothing that
/// was not verifiably copied may ever be deleted, read-only files must not
/// fail a move, and a cleanup failure must complete the migration with a
/// visible residue instead of rolling back onto a gutted source folder.
/// </summary>
public sealed class ManagedStorageMigrationSafetyTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly string _storageRoot;
    private readonly string _newStorageRoot;
    private readonly SettingsService _settingsService;
    private readonly FileService _fileService;
    private readonly WidgetManager _widgetManager;

    public ManagedStorageMigrationSafetyTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "DeskBox.Tests", Guid.NewGuid().ToString("N"));
        _storageRoot = Directory.CreateDirectory(Path.Combine(_tempRoot, "storage")).FullName;
        _newStorageRoot = Path.Combine(_tempRoot, "storage-new");

        _settingsService = new SettingsService(Path.Combine(_tempRoot, "settings"));
        _settingsService.Settings.DefaultManagedStorageRootPath = _storageRoot;

        _fileService = new FileService();
        _widgetManager = new WidgetManager(
            _settingsService,
            _fileService,
            new OrganizerService(_settingsService, _fileService),
            new ThemeService(_settingsService),
            new QuickCaptureService(new QuickCaptureStore(Path.Combine(_tempRoot, "quick-capture"))),
            () => Path.Combine(_tempRoot, "desktop"),
            recycleManagedFolderDeletes: false);
    }

    public void Dispose()
    {
        // Tests intentionally leave read-only files behind (copied
        // attributes), which block a plain recursive delete.
        foreach (string filePath in Directory.EnumerateFiles(
                     _tempRoot,
                     "*",
                     SearchOption.AllDirectories))
        {
            File.SetAttributes(filePath, FileAttributes.Normal);
        }

        Directory.Delete(_tempRoot, recursive: true);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecuteTransferPlanAsync_DirectoryMoveWithReadOnlyNestedFileSucceeds(
        bool reportProgress)
    {
        string sourceDirectory = Directory.CreateDirectory(
            Path.Combine(_tempRoot, $"ro-source-{reportProgress}")).FullName;
        string nestedDirectory = Directory.CreateDirectory(
            Path.Combine(sourceDirectory, "documents")).FullName;
        string readOnlyFile = Path.Combine(nestedDirectory, "report.docx");
        File.WriteAllText(readOnlyFile, "attachment content");
        File.SetAttributes(readOnlyFile, FileAttributes.ReadOnly);
        string destinationDirectory = Directory.CreateDirectory(
            Path.Combine(_tempRoot, $"ro-destination-{reportProgress}")).FullName;

        IProgress<FileService.FileTransferProgress>? progress = reportProgress
            ? new InlineProgress<FileService.FileTransferProgress>(_ => { })
            : null;

        await _fileService.ExecuteTransferPlanAsync(
            [new FileService.FileTransferPlan(sourceDirectory, destinationDirectory)],
            move: true,
            progress: progress);

        Assert.False(Directory.Exists(sourceDirectory));
        string copiedFile = Path.Combine(destinationDirectory, "documents", "report.docx");
        Assert.Equal("attachment content", File.ReadAllText(copiedFile));
        Assert.True(
            File.GetAttributes(copiedFile).HasFlag(FileAttributes.ReadOnly),
            "The copy must preserve the source read-only attribute.");
    }

    [Fact]
    public void DeleteSourceTreeByManifest_ThrowsAndDeletesNothingWhenFileAppearedAfterCopy()
    {
        string sourceDirectory = Directory.CreateDirectory(
            Path.Combine(_tempRoot, "toctou-source")).FullName;
        string copiedFile = Path.Combine(sourceDirectory, "copied.txt");
        string lateFile = Path.Combine(sourceDirectory, "late.txt");
        File.WriteAllText(copiedFile, "copied before the delete");
        File.WriteAllText(lateFile, "written after the copy enumeration");

        var manifest = new List<FileService.CopiedSourceFileRecord>
        {
            new(copiedFile, new FileInfo(copiedFile).Length, new FileInfo(copiedFile).LastWriteTimeUtc, null)
        };

        Assert.Throws<FileService.FileTransferSourceChangedException>(
            () => FileService.DeleteSourceTreeByManifest(
                sourceDirectory,
                Path.Combine(_tempRoot, "toctou-destination"),
                manifest));

        Assert.True(File.Exists(lateFile), "A file that appeared after the copy must survive.");
        Assert.True(File.Exists(copiedFile), "Verification failure must not delete anything.");
        Assert.True(Directory.Exists(sourceDirectory));
    }

    [Fact]
    public void DeleteSourceTreeByManifest_ThrowsWhenCopiedFileChangedAfterCopy()
    {
        string sourceDirectory = Directory.CreateDirectory(
            Path.Combine(_tempRoot, "changed-source")).FullName;
        string changedFile = Path.Combine(sourceDirectory, "changed.txt");
        File.WriteAllText(changedFile, "stale copy captured");

        var manifest = new List<FileService.CopiedSourceFileRecord>
        {
            new(changedFile, 1, DateTime.UtcNow - TimeSpan.FromHours(1), null)
        };

        Assert.Throws<FileService.FileTransferSourceChangedException>(
            () => FileService.DeleteSourceTreeByManifest(
                sourceDirectory,
                Path.Combine(_tempRoot, "changed-destination"),
                manifest));

        Assert.True(File.Exists(changedFile), "A modified source file must survive.");
    }

    [Fact]
    public void DeleteSourceTreeByManifest_ClearsReadOnlyAndDeletesWholeTree()
    {
        string sourceDirectory = Directory.CreateDirectory(
            Path.Combine(_tempRoot, "manifest-source")).FullName;
        string nestedDirectory = Directory.CreateDirectory(
            Path.Combine(sourceDirectory, "nested", "deeper")).FullName;
        string readOnlyTop = Path.Combine(sourceDirectory, "ro-top.txt");
        string readOnlyNested = Path.Combine(nestedDirectory, "ro-nested.txt");
        string plainFile = Path.Combine(sourceDirectory, "plain.txt");
        File.WriteAllText(readOnlyTop, "a");
        File.WriteAllText(readOnlyNested, "b");
        File.WriteAllText(plainFile, "c");
        File.SetAttributes(readOnlyTop, FileAttributes.ReadOnly);
        File.SetAttributes(readOnlyNested, FileAttributes.ReadOnly);

        FileService.DeleteSourceTreeByManifest(
            sourceDirectory,
            Path.Combine(_tempRoot, "manifest-destination"),
            FileService.CollectCurrentFileManifest(sourceDirectory));

        Assert.False(Directory.Exists(sourceDirectory), "The fully verified tree must be removed.");
    }

    [Fact]
    public async Task DeleteSourceTreeByManifest_LockedReadOnlyFileKeepsFileAndAttribute()
    {
        string sourceDirectory = Directory.CreateDirectory(
            Path.Combine(_tempRoot, "locked-source")).FullName;
        string lockedFile = Path.Combine(sourceDirectory, "locked.txt");
        File.WriteAllText(lockedFile, "locked content");
        File.SetAttributes(lockedFile, FileAttributes.ReadOnly);

        await using var lockHandle = new FileStream(
            lockedFile,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read);

        // A file locked by another process cannot be deleted through a
        // verified handle: fail closed as a cleanup failure (both copies
        // stay) instead of a raw delete error.
        Assert.Throws<FileService.FileTransferSourceCleanupException>(
            () => FileService.DeleteSourceTreeByManifest(
                sourceDirectory,
                Path.Combine(_tempRoot, "locked-destination"),
                FileService.CollectCurrentFileManifest(sourceDirectory)));

        Assert.True(File.Exists(lockedFile));
        Assert.True(
            File.GetAttributes(lockedFile).HasFlag(FileAttributes.ReadOnly),
            "A failed delete must restore the original read-only attribute.");
    }

    [Fact]
    public async Task RestoreMigratedDirectoryPreservingExisting_MovesOnlyMissingChildren()
    {
        string copiedDirectory = Directory.CreateDirectory(
            Path.Combine(_tempRoot, "merge-copy")).FullName;
        string originalDirectory = Directory.CreateDirectory(
            Path.Combine(_tempRoot, "merge-original")).FullName;
        string twinFile = Path.Combine(copiedDirectory, "twin.txt");
        string twinOriginal = Path.Combine(originalDirectory, "twin.txt");
        string uniqueFile = Path.Combine(copiedDirectory, "unique.txt");
        string matchingFile = Path.Combine(copiedDirectory, "matching.txt");
        string matchingOriginal = Path.Combine(originalDirectory, "matching.txt");
        File.WriteAllText(twinFile, "stale copy");
        File.WriteAllText(twinOriginal, "possibly newer original");
        File.WriteAllText(uniqueFile, "only in the copy");
        File.WriteAllText(matchingFile, "identical twin");
        File.WriteAllText(matchingOriginal, "identical twin");
        DateTime stamp = DateTime.UtcNow.AddDays(-1);
        File.SetLastWriteTimeUtc(matchingFile, stamp);
        File.SetLastWriteTimeUtc(matchingOriginal, stamp);

        await FileService.RestoreMigratedDirectoryPreservingExistingAsync(
            copiedDirectory,
            originalDirectory);

        Assert.Equal(
            "possibly newer original",
            File.ReadAllText(twinOriginal));
        Assert.Equal(
            "only in the copy",
            File.ReadAllText(Path.Combine(originalDirectory, "unique.txt")));
        Assert.True(
            File.Exists(twinFile),
            "A diverged duplicate is kept: either side may hold the user's " +
            "latest edit, and ownership cannot be proven without a match.");
        Assert.True(
            File.Exists(matchingFile),
            "Duplicates are never auto-deleted: content equality from size " +
            "and timestamp cannot authorize a delete in a rollback.");
        Assert.True(Directory.Exists(copiedDirectory), "The copy stays for its duplicate content.");
    }

    [Fact]
    public async Task RestoreMigratedDirectory_NestedSameNamedDirectoriesMergeConservatively()
    {
        // The failed source cleanup already deleted some files from the
        // original subtree; the copied subtree holds the ONLY remaining copy
        // of those files. A same-named nested directory must merge
        // child-by-child, never delete the copied subtree wholesale.
        string copiedDirectory = Directory.CreateDirectory(
            Path.Combine(_tempRoot, "nested-copy")).FullName;
        string originalDirectory = Directory.CreateDirectory(
            Path.Combine(_tempRoot, "nested-original")).FullName;
        string copiedSub = Directory.CreateDirectory(
            Path.Combine(copiedDirectory, "documents")).FullName;
        string originalSub = Directory.CreateDirectory(
            Path.Combine(originalDirectory, "documents")).FullName;
        // Original side after partial cleanup: only b.txt remains.
        File.WriteAllText(Path.Combine(originalSub, "b.txt"), "b");
        // Copied side: the complete tree (a.txt deleted from original).
        File.WriteAllText(Path.Combine(copiedSub, "a.txt"), "a");
        File.WriteAllText(Path.Combine(copiedSub, "b.txt"), "b");

        await FileService.RestoreMigratedDirectoryPreservingExistingAsync(
            copiedDirectory,
            originalDirectory);

        Assert.True(
            File.Exists(Path.Combine(originalSub, "a.txt")),
            "a.txt must move back: the copied subtree held its last copy");
        Assert.True(
            File.Exists(Path.Combine(originalSub, "b.txt")),
            "b.txt survives on the original side");
        Assert.True(
            File.Exists(Path.Combine(copiedSub, "b.txt")),
            "the duplicate b.txt stays: duplicates are never auto-deleted");
    }

    [Fact]
    public async Task UpdateDefaultManagedStorageRootAsync_CompletesWithResidueWhenCleanupFails()
    {
        var widgetA = CreateManagedWidget("A", Path.Combine(_storageRoot, "A"));
        var widgetB = CreateManagedWidget("B", Path.Combine(_storageRoot, "B"));
        string folderA = Directory.CreateDirectory(widgetA.MappedFolderPath!).FullName;
        string folderB = Directory.CreateDirectory(widgetB.MappedFolderPath!).FullName;
        File.WriteAllText(Path.Combine(folderB, "b.txt"), "b");
        _settingsService.Settings.Widgets.Add(widgetA);
        _settingsService.Settings.Widgets.Add(widgetB);

        _widgetManager.RelocateDirectoryForMigrationOverride = (source, destination) =>
        {
            if (source == folderA)
            {
                throw new FileService.FileTransferSourceCleanupException(
                    source,
                    destination,
                    new UnauthorizedAccessException("simulated read-only blocker"));
            }

            return _fileService.RelocateDirectoryAsync(source, destination);
        };

        ManagedStorageMigrationResult result =
            await _widgetManager.UpdateDefaultManagedStorageRootAsync(_newStorageRoot);

        Assert.Equal(_newStoragePath(), _settingsService.Settings.DefaultManagedStorageRootPath);
        ManagedStorageMigrationResidue residue = Assert.Single(result.Residues);
        Assert.Equal(widgetA.Id, residue.WidgetId);
        Assert.Equal(folderA, residue.SourceFolder, ignoreCase: true);
        Assert.True(Directory.Exists(folderA),
            "The blocked source folder must be kept for the residue report.");
        Assert.Equal(
            Path.Combine(_newStorageRoot, "B"),
            widgetB.MappedFolderPath,
            ignoreCase: true);
        Assert.False(Directory.Exists(folderB), "The healthy widget must fully move.");
        Assert.True(File.Exists(Path.Combine(_newStorageRoot, "B", "b.txt")));
    }

    [Fact]
    public async Task UpdateDefaultManagedStorageRootAsync_RollsBackCompletedMovesOnHardFailure()
    {
        var widgetA = CreateManagedWidget("A", Path.Combine(_storageRoot, "A"));
        var widgetB = CreateManagedWidget("B", Path.Combine(_storageRoot, "B"));
        string folderA = Directory.CreateDirectory(widgetA.MappedFolderPath!).FullName;
        string folderB = widgetB.MappedFolderPath!;
        File.WriteAllText(Path.Combine(folderA, "a.txt"), "a");
        _settingsService.Settings.Widgets.Add(widgetA);
        _settingsService.Settings.Widgets.Add(widgetB);

        _widgetManager.RelocateDirectoryForMigrationOverride = (source, destination) =>
        {
            if (source == folderB)
            {
                throw new IOException("simulated hard failure");
            }

            return _fileService.RelocateDirectoryAsync(source, destination);
        };

        await Assert.ThrowsAsync<IOException>(
            () => _widgetManager.UpdateDefaultManagedStorageRootAsync(_newStorageRoot));

        Assert.Equal(
            _storageRoot,
            _settingsService.Settings.DefaultManagedStorageRootPath,
            ignoreCase: true);
        Assert.Equal(widgetA.MappedFolderPath, Path.Combine(_storageRoot, "A"), ignoreCase: true);
        Assert.True(File.Exists(Path.Combine(folderA, "a.txt")),
            "The rolled back widget folder must be restored at its source.");
        Assert.False(Directory.Exists(Path.Combine(_newStorageRoot, "A")),
            "The rolled back destination folder must be gone.");
    }

    [Fact]
    public async Task UpdateDefaultManagedStorageRootAsync_ReturnsPartiallyMovedItemsOnFailure()
    {
        var widget = CreateManagedWidget("A", Path.Combine(_storageRoot, "A"));
        string folderA = Directory.CreateDirectory(widget.MappedFolderPath!).FullName;
        string movedFile = Path.Combine(folderA, "a.txt");
        File.WriteAllText(movedFile, "a");
        File.WriteAllText(Path.Combine(folderA, "locked.txt"), "locked");
        _settingsService.Settings.Widgets.Add(widget);

        // Simulate a mid-folder failure: a.txt already landed at the
        // destination while the locked file (e.g. open in another app) still
        // blocks the move. The stranded item must return to the source or
        // every retry trips the stale-destination guard.
        string destinationFolder = Path.Combine(_newStorageRoot, "A");
        string strandedFile = Path.Combine(destinationFolder, "a.txt");
        _widgetManager.RelocateDirectoryForMigrationOverride = (source, destination) =>
        {
            Directory.CreateDirectory(destination);
            File.Move(movedFile, strandedFile);
            throw new FileService.FileTransferPartialFailureException(
                [new FileService.FileTransferResult(movedFile, strandedFile)],
                new IOException("simulated sharing violation"));
        };

        await Assert.ThrowsAsync<FileService.FileTransferPartialFailureException>(
            () => _widgetManager.UpdateDefaultManagedStorageRootAsync(_newStorageRoot));

        Assert.Equal(
            _storageRoot,
            _settingsService.Settings.DefaultManagedStorageRootPath,
            ignoreCase: true);
        Assert.True(
            File.Exists(movedFile),
            "The partially moved item must be returned to its source folder.");
        Assert.False(
            File.Exists(strandedFile),
            "No partially moved residue may remain at the destination.");
        Assert.True(File.Exists(Path.Combine(folderA, "locked.txt")),
            "The blocked file stays at the source.");
    }

    [Fact]
    public async Task UpdateDefaultManagedStorageRootAsync_ReportsStrandedItemsWhenPartialRestoreFails()
    {
        var widget = CreateManagedWidget("A", Path.Combine(_storageRoot, "A"));
        string folderA = Directory.CreateDirectory(widget.MappedFolderPath!).FullName;
        string movedFile = Path.Combine(folderA, "a.txt");
        File.WriteAllText(movedFile, "a");
        File.WriteAllText(Path.Combine(folderA, "locked.txt"), "locked");
        _settingsService.Settings.Widgets.Add(widget);

        string destinationFolder = Path.Combine(_newStorageRoot, "A");
        string strandedFile = Path.Combine(destinationFolder, "a.txt");
        // Pinning the moved file makes the restore-back fail the way a
        // sharing violation does. The widget never reached completedMoves,
        // so without a receipt the stranded file would be lost to every
        // recovery channel.
        FileStream? lockStream = null;
        _widgetManager.RelocateDirectoryForMigrationOverride = (source, destination) =>
        {
            Directory.CreateDirectory(destination);
            File.Move(movedFile, strandedFile);
            lockStream = new FileStream(
                strandedFile,
                FileMode.Open,
                FileAccess.Read,
                FileShare.None);
            throw new FileService.FileTransferPartialFailureException(
                [new FileService.FileTransferResult(movedFile, strandedFile)],
                new IOException("simulated sharing violation"));
        };

        ManagedStorageRollbackFailureException failure;
        try
        {
            failure = await Assert.ThrowsAsync<ManagedStorageRollbackFailureException>(
                () => _widgetManager.UpdateDefaultManagedStorageRootAsync(_newStorageRoot));
        }
        finally
        {
            lockStream?.Dispose();
        }

        ManagedStorageRollbackFailure receipt = Assert.Single(failure.Failures);
        Assert.Equal(widget.Id, receipt.WidgetId);
        Assert.Equal("A", receipt.WidgetName);
        Assert.Equal(destinationFolder, receipt.DestinationFolder, ignoreCase: true);
        Assert.Equal(folderA, receipt.SourceFolder, ignoreCase: true);
        Assert.True(receipt.PreserveExisting);
        Assert.IsType<FileService.FileTransferPartialFailureException>(failure.OriginalFailure);
        Assert.Equal(
            _storageRoot,
            _settingsService.Settings.DefaultManagedStorageRootPath,
            ignoreCase: true);
        Assert.Equal(folderA, widget.MappedFolderPath, ignoreCase: true);
        Assert.True(File.Exists(strandedFile),
            "The stranded file stays at the destination until a retry returns it.");
        Assert.True(File.Exists(Path.Combine(folderA, "locked.txt")));

        IReadOnlyList<ManagedStorageRollbackFailure> remaining =
            await _widgetManager.RetryMigrationRollbackAsync(failure.Failures);

        Assert.Empty(remaining);
        Assert.True(File.Exists(movedFile),
            "The rollback retry must return the stranded file to its source.");
        Assert.False(File.Exists(strandedFile));
    }

    [Fact]
    public async Task RetrySkippedMigrationItemsAsync_CancelUndoesMovedItems()
    {
        var widget = CreateManagedWidget("A", Path.Combine(_storageRoot, "A"));
        string folderA = Directory.CreateDirectory(widget.MappedFolderPath!).FullName;
        string fileA = Path.Combine(folderA, "a.txt");
        string fileB = Path.Combine(folderA, "b.txt");
        File.WriteAllText(fileA, "a");
        File.WriteAllText(fileB, "b");
        _settingsService.Settings.Widgets.Add(widget);

        string destinationFolder = Path.Combine(_newStorageRoot, "A");
        string movedFileA = Path.Combine(destinationFolder, "a.txt");
        var skipped = new List<ManagedStorageSkippedItem>
        {
            new(widget.Id, widget.Name, fileA, movedFileA,
                FileService.FileTransferItemErrorKind.InUse, "skipped earlier"),
            new(widget.Id, widget.Name, fileB,
                Path.Combine(destinationFolder, "b.txt"),
                FileService.FileTransferItemErrorKind.InUse, "skipped earlier"),
        };

        // A cancel mid-retry must behave like a cancelled migration: the
        // item that already moved goes back, so the skipped list stays
        // truthful and no file is stranded without a receipt.
        _widgetManager.RelocateDirectoryForMigrationOverrideEx =
            (source, destination, progress, cancellationToken, onItemError) =>
            {
                Directory.CreateDirectory(destination);
                File.Move(fileA, movedFileA);
                throw new FileService.FileTransferCanceledException(
                    [new FileService.FileTransferResult(fileA, movedFileA)],
                    cancellationToken);
            };

        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => _widgetManager.RetrySkippedMigrationItemsAsync(skipped));
        }
        finally
        {
            _widgetManager.RelocateDirectoryForMigrationOverrideEx = null;
        }

        Assert.True(File.Exists(fileA),
            "The item moved before the cancel must be returned to the source.");
        Assert.False(File.Exists(movedFileA),
            "No half-retried item may remain at the destination.");
        Assert.False(Directory.Exists(destinationFolder),
            "The emptied destination shell goes with the undo.");
        Assert.True(File.Exists(fileB));
        Assert.Equal(folderA, widget.MappedFolderPath, ignoreCase: true);
    }

    [Fact]
    public async Task RetrySkippedMigrationItemsAsync_ReportsStrandedItemsWhenUndoFails()
    {
        var widget = CreateManagedWidget("A", Path.Combine(_storageRoot, "A"));
        string folderA = Directory.CreateDirectory(widget.MappedFolderPath!).FullName;
        string fileA = Path.Combine(folderA, "a.txt");
        File.WriteAllText(fileA, "a");
        _settingsService.Settings.Widgets.Add(widget);

        string destinationFolder = Path.Combine(_newStorageRoot, "A");
        string movedFileA = Path.Combine(destinationFolder, "a.txt");
        var skipped = new List<ManagedStorageSkippedItem>
        {
            new(widget.Id, widget.Name, fileA, movedFileA,
                FileService.FileTransferItemErrorKind.InUse, "skipped earlier"),
        };

        // The undo itself is blocked by a sharing violation: the stranded
        // item must surface as a rollback-failure receipt instead of a bare
        // cancel whose only copy of the receipt dies with the dialog.
        FileStream? lockStream = null;
        _widgetManager.RelocateDirectoryForMigrationOverrideEx =
            (source, destination, progress, cancellationToken, onItemError) =>
            {
                Directory.CreateDirectory(destination);
                File.Move(fileA, movedFileA);
                lockStream = new FileStream(
                    movedFileA,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.None);
                throw new FileService.FileTransferCanceledException(
                    [new FileService.FileTransferResult(fileA, movedFileA)],
                    cancellationToken);
            };

        ManagedStorageRollbackFailureException failure;
        try
        {
            failure = await Assert.ThrowsAsync<ManagedStorageRollbackFailureException>(
                () => _widgetManager.RetrySkippedMigrationItemsAsync(skipped));
        }
        finally
        {
            lockStream?.Dispose();
            _widgetManager.RelocateDirectoryForMigrationOverrideEx = null;
        }

        ManagedStorageRollbackFailure receipt = Assert.Single(failure.Failures);
        Assert.Equal(widget.Id, receipt.WidgetId);
        Assert.Equal(destinationFolder, receipt.DestinationFolder, ignoreCase: true);
        Assert.Equal(folderA, receipt.SourceFolder, ignoreCase: true);
        Assert.True(File.Exists(movedFileA));
        Assert.Equal(folderA, widget.MappedFolderPath, ignoreCase: true);

        IReadOnlyList<ManagedStorageRollbackFailure> remaining =
            await _widgetManager.RetryMigrationRollbackAsync(failure.Failures);

        Assert.Empty(remaining);
        Assert.True(File.Exists(fileA),
            "Once unblocked, the rollback retry must return the file.");
        Assert.False(File.Exists(movedFileA));
    }

    [Fact]
    public async Task UpdateDefaultManagedStorageRootAsync_RejectsNonEmptyDestinationFolders()
    {
        var widget = CreateManagedWidget("A", Path.Combine(_storageRoot, "A"));
        string folderA = Directory.CreateDirectory(widget.MappedFolderPath!).FullName;
        File.WriteAllText(Path.Combine(folderA, "a.txt"), "a");
        _settingsService.Settings.Widgets.Add(widget);

        string staleDestination = Directory.CreateDirectory(
            Path.Combine(_newStorageRoot, "A")).FullName;
        File.WriteAllText(Path.Combine(staleDestination, "stale.txt"), "previous attempt");

        ManagedStorageDestinationResidueException exception =
            await Assert.ThrowsAsync<ManagedStorageDestinationResidueException>(
                () => _widgetManager.UpdateDefaultManagedStorageRootAsync(_newStorageRoot));

        string staleFolder = Assert.Single(exception.StaleDestinationFolders);
        Assert.Equal(staleDestination, staleFolder, ignoreCase: true);
        Assert.True(File.Exists(Path.Combine(folderA, "a.txt")),
            "The source must be untouched when the destination is rejected.");
        Assert.Equal(
            _storageRoot,
            _settingsService.Settings.DefaultManagedStorageRootPath,
            ignoreCase: true);
    }

    [Fact]
    public async Task UpdateDefaultManagedStorageRootAsync_ReportsFoldersLeftBehindWhenRollbackFails()
    {
        var widgetA = CreateManagedWidget("A", Path.Combine(_storageRoot, "A"));
        var widgetB = CreateManagedWidget("B", Path.Combine(_storageRoot, "B"));
        string folderA = Directory.CreateDirectory(widgetA.MappedFolderPath!).FullName;
        string folderB = widgetB.MappedFolderPath!;
        File.WriteAllText(Path.Combine(folderA, "a.txt"), "a");
        _settingsService.Settings.Widgets.Add(widgetA);
        _settingsService.Settings.Widgets.Add(widgetB);

        string movedFolderA = Path.Combine(_newStorageRoot, "A");
        // The lock keeps A's already-moved file pinned so the rollback of the
        // completed move fails exactly the way a sharing violation does. It can
        // only open after the forward move lands, so the override arms it.
        FileStream? lockStream = null;
        _widgetManager.RelocateDirectoryForMigrationOverride = async (source, destination) =>
        {
            if (source == folderB)
            {
                throw new IOException("simulated hard failure");
            }

            await _fileService.RelocateDirectoryAsync(source, destination);
            if (source == folderA)
            {
                lockStream = new FileStream(
                    Path.Combine(destination, "a.txt"),
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.None);
            }
        };

        ManagedStorageRollbackFailureException failure;
        try
        {
            failure = await Assert.ThrowsAsync<ManagedStorageRollbackFailureException>(
                () => _widgetManager.UpdateDefaultManagedStorageRootAsync(_newStorageRoot));
        }
        finally
        {
            lockStream?.Dispose();
        }

        ManagedStorageRollbackFailure rollbackFailure = Assert.Single(failure.Failures);
        Assert.Equal(widgetA.Id, rollbackFailure.WidgetId);
        Assert.Equal("A", rollbackFailure.WidgetName);
        Assert.Equal(movedFolderA, rollbackFailure.DestinationFolder, ignoreCase: true);
        Assert.Equal(folderA, rollbackFailure.SourceFolder, ignoreCase: true);
        Assert.False(rollbackFailure.PreserveExisting);
        Assert.IsType<IOException>(failure.OriginalFailure);

        // Settings and widget configs still rolled back to the old root.
        Assert.Equal(_storageRoot, _settingsService.Settings.DefaultManagedStorageRootPath, ignoreCase: true);
        Assert.Equal(folderA, widgetA.MappedFolderPath, ignoreCase: true);
        // The pinned copy stayed in the new root: this is the split the user
        // must be told about, instead of a bare "migration failed".
        Assert.True(File.Exists(Path.Combine(movedFolderA, "a.txt")));
        Assert.True(Directory.Exists(movedFolderA));

        IReadOnlyList<ManagedStorageRollbackFailure> remaining =
            await _widgetManager.RetryMigrationRollbackAsync(failure.Failures);

        Assert.Empty(remaining);
        Assert.True(File.Exists(Path.Combine(folderA, "a.txt")),
            "The retry must return the folder to its original location.");
        Assert.False(Directory.Exists(movedFolderA),
            "The retry must not leave the split copy behind.");
    }

    [Fact]
    public async Task RetryMigrationRollbackAsync_KeepsFailureWhenRetryStillBlocked()
    {
        var widgetA = CreateManagedWidget("A", Path.Combine(_storageRoot, "A"));
        string folderA = Directory.CreateDirectory(widgetA.MappedFolderPath!).FullName;
        File.WriteAllText(Path.Combine(folderA, "a.txt"), "a");
        _settingsService.Settings.Widgets.Add(widgetA);

        string movedFolderA = Path.Combine(_newStorageRoot, "A");
        Directory.CreateDirectory(movedFolderA);
        File.WriteAllText(Path.Combine(movedFolderA, "a.txt"), "a");

        var failureList = new List<ManagedStorageRollbackFailure>
        {
            new(widgetA.Id, "A", movedFolderA, folderA, PreserveExisting: false, Reason: "blocked"),
        };

        IReadOnlyList<ManagedStorageRollbackFailure> remaining;
        using (var lockStream = new FileStream(
                   Path.Combine(movedFolderA, "a.txt"),
                   FileMode.Open,
                   FileAccess.Read,
                   FileShare.None))
        {
            remaining = await _widgetManager.RetryMigrationRollbackAsync(failureList);
        }

        ManagedStorageRollbackFailure stillBlocked = Assert.Single(remaining);
        Assert.Equal(widgetA.Id, stillBlocked.WidgetId);
        Assert.True(Directory.Exists(movedFolderA), "The blocked copy must survive the failed retry.");
    }


    [Fact]
    public async Task UpdateDefaultManagedStorageRootAsync_SkipsLockedItemAndMigratesTheRest()
    {
        var widget = CreateManagedWidget("A", Path.Combine(_storageRoot, "A"));
        string folderA = Directory.CreateDirectory(widget.MappedFolderPath!).FullName;
        File.WriteAllText(Path.Combine(folderA, "free.txt"), "free");
        string lockedFile = Path.Combine(folderA, "locked.txt");
        File.WriteAllText(lockedFile, "locked");
        _settingsService.Settings.Widgets.Add(widget);
        // A pre-existing destination folder forces the per-item move path
        // (the same-volume whole-folder rename would carry the lock along).
        Directory.CreateDirectory(Path.Combine(_newStorageRoot, "A"));

        var reportedErrors = new List<FileService.FileTransferItemError>();
        var options = new ManagedStorageMigrationOptions(
            OnItemError: error =>
            {
                reportedErrors.Add(error);
                return Task.FromResult(FileService.FileTransferItemAction.Skip);
            });

        ManagedStorageMigrationResult result;
        await using (var lockStream = new FileStream(
                         lockedFile, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            result = await _widgetManager.UpdateDefaultManagedStorageRootAsync(
                _newStorageRoot, options);
        }

        FileService.FileTransferItemError itemError = Assert.Single(reportedErrors);
        Assert.Equal(lockedFile, itemError.SourcePath, ignoreCase: true);
        ManagedStorageSkippedItem skipped = Assert.Single(result.SkippedItems);
        Assert.Equal(lockedFile, skipped.SourcePath, ignoreCase: true);
        Assert.Equal(
            FileService.FileTransferItemErrorKind.InUse,
            skipped.ErrorKind);
        Assert.Equal(1, result.MovedItemCount);
        Assert.Equal(1, result.AffectedWidgetCount);
        Assert.True(
            File.Exists(Path.Combine(_newStorageRoot, "A", "free.txt")),
            "The healthy item must move normally.");
        Assert.True(File.Exists(lockedFile),
            "A skipped file must stay at the source, never deleted.");
        Assert.True(Directory.Exists(folderA),
            "The source folder stays while skipped items remain inside.");
        Assert.Equal(
            Path.Combine(_newStorageRoot, "A"),
            widget.MappedFolderPath,
            ignoreCase: true);
    }

    [Fact]
    public async Task UpdateDefaultManagedStorageRootAsync_RetriedItemRecoversAfterUnlock()
    {
        var widget = CreateManagedWidget("A", Path.Combine(_storageRoot, "A"));
        string folderA = Directory.CreateDirectory(widget.MappedFolderPath!).FullName;
        string lockedFile = Path.Combine(folderA, "busy.txt");
        File.WriteAllText(lockedFile, "busy");
        _settingsService.Settings.Widgets.Add(widget);
        Directory.CreateDirectory(Path.Combine(_newStorageRoot, "A"));

        var lockStream = new FileStream(
            lockedFile, FileMode.Open, FileAccess.Read, FileShare.None);
        var options = new ManagedStorageMigrationOptions(
            OnItemError: _ =>
            {
                // The user closed the program holding the file, then chose
                // Retry: the same item must complete on the second attempt.
                lockStream.Dispose();
                return Task.FromResult(FileService.FileTransferItemAction.Retry);
            });

        ManagedStorageMigrationResult result =
            await _widgetManager.UpdateDefaultManagedStorageRootAsync(
                _newStorageRoot, options);

        Assert.Empty(result.SkippedItems);
        Assert.Equal(1, result.AffectedWidgetCount);
        Assert.True(
            File.Exists(Path.Combine(_newStorageRoot, "A", "busy.txt")),
            "The retried item must land at the destination.");
        Assert.False(Directory.Exists(folderA),
            "A fully emptied source folder is removed.");
    }

    [Fact]
    public async Task UpdateDefaultManagedStorageRootAsync_AllItemsSkipped_KeepsWidgetOnOldFolder()
    {
        var widget = CreateManagedWidget("A", Path.Combine(_storageRoot, "A"));
        string folderA = Directory.CreateDirectory(widget.MappedFolderPath!).FullName;
        string lockedFile = Path.Combine(folderA, "only.txt");
        File.WriteAllText(lockedFile, "only");
        _settingsService.Settings.Widgets.Add(widget);
        Directory.CreateDirectory(Path.Combine(_newStorageRoot, "A"));

        var options = new ManagedStorageMigrationOptions(
            OnItemError: _ => Task.FromResult(FileService.FileTransferItemAction.Skip));

        ManagedStorageMigrationResult result;
        await using (var lockStream = new FileStream(
                         lockedFile, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            result = await _widgetManager.UpdateDefaultManagedStorageRootAsync(
                _newStorageRoot, options);
        }

        Assert.Equal(0, result.AffectedWidgetCount);
        Assert.Single(result.SkippedItems);
        // A widget whose whole folder was skipped must keep pointing at the
        // old location instead of an empty new folder.
        Assert.Equal(folderA, widget.MappedFolderPath, ignoreCase: true);
        // The root change itself still commits; only this widget lags behind.
        Assert.Equal(
            _newStoragePath(),
            _settingsService.Settings.DefaultManagedStorageRootPath);
    }

    [Fact]
    public async Task UpdateDefaultManagedStorageRootAsync_CancelAfterFirstWidget_RollsBack()
    {
        var widgetA = CreateManagedWidget("A", Path.Combine(_storageRoot, "A"));
        var widgetB = CreateManagedWidget("B", Path.Combine(_storageRoot, "B"));
        string folderA = Directory.CreateDirectory(widgetA.MappedFolderPath!).FullName;
        string folderB = Directory.CreateDirectory(widgetB.MappedFolderPath!).FullName;
        File.WriteAllText(Path.Combine(folderA, "a.txt"), "a");
        File.WriteAllText(Path.Combine(folderB, "b.txt"), "b");
        _settingsService.Settings.Widgets.Add(widgetA);
        _settingsService.Settings.Widgets.Add(widgetB);

        var cts = new CancellationTokenSource();
        // The synchronous InlineProgress makes the cancel deterministic: the
        // widget-completed report for A fires before B's turn begins.
        var options = new ManagedStorageMigrationOptions(
            Progress: new InlineProgress<ManagedStorageMigrationProgress>(progress =>
            {
                if (progress.CompletedWidgets >= 1)
                {
                    cts.Cancel();
                }
            }),
            CancellationToken: cts.Token);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => _widgetManager.UpdateDefaultManagedStorageRootAsync(
                _newStorageRoot, options));

        Assert.Equal(
            _storageRoot,
            _settingsService.Settings.DefaultManagedStorageRootPath,
            ignoreCase: true);
        Assert.Equal(folderA, widgetA.MappedFolderPath, ignoreCase: true);
        Assert.Equal(folderB, widgetB.MappedFolderPath, ignoreCase: true);
        Assert.True(
            File.Exists(Path.Combine(folderA, "a.txt")),
            "The completed widget move must roll back on cancel.");
        Assert.False(
            Directory.Exists(Path.Combine(_newStorageRoot, "A")),
            "The rolled back destination must be gone.");
        Assert.False(
            Directory.Exists(_newStorageRoot),
            "A canceled migration must not leave the new root's empty shell " +
            "behind when the migration itself created it.");
        Assert.True(
            File.Exists(Path.Combine(folderB, "b.txt")),
            "The not-yet-started widget stays untouched.");
    }

    [Fact]
    public async Task UpdateDefaultManagedStorageRootAsync_AbortItemDecisionRollsBack()
    {
        var widget = CreateManagedWidget("A", Path.Combine(_storageRoot, "A"));
        string folderA = Directory.CreateDirectory(widget.MappedFolderPath!).FullName;
        File.WriteAllText(Path.Combine(folderA, "free.txt"), "free");
        string lockedFile = Path.Combine(folderA, "locked.txt");
        File.WriteAllText(lockedFile, "locked");
        _settingsService.Settings.Widgets.Add(widget);
        Directory.CreateDirectory(Path.Combine(_newStorageRoot, "A"));

        var options = new ManagedStorageMigrationOptions(
            OnItemError: _ => Task.FromResult(FileService.FileTransferItemAction.Abort));

        await using (var lockStream = new FileStream(
                         lockedFile, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => _widgetManager.UpdateDefaultManagedStorageRootAsync(
                    _newStorageRoot, options));
        }

        Assert.Equal(
            _storageRoot,
            _settingsService.Settings.DefaultManagedStorageRootPath,
            ignoreCase: true);
        Assert.Equal(folderA, widget.MappedFolderPath, ignoreCase: true);
        Assert.True(File.Exists(Path.Combine(folderA, "free.txt")),
            "Items moved before the abort must return to the source.");
        Assert.True(File.Exists(lockedFile));
        Assert.False(
            File.Exists(Path.Combine(_newStorageRoot, "A", "free.txt")),
            "No half-moved item may remain at the destination.");
        Assert.False(
            Directory.Exists(Path.Combine(_newStorageRoot, "A")),
            "Once the moved items are restored, the emptied destination " +
            "folder shell must go too.");
    }

    [Fact]
    public async Task RetrySkippedMigrationItemsAsync_MovesRemainingAndRepointsWidget()
    {
        var widget = CreateManagedWidget("A", Path.Combine(_storageRoot, "A"));
        string folderA = Directory.CreateDirectory(widget.MappedFolderPath!).FullName;
        string lockedFile = Path.Combine(folderA, "only.txt");
        File.WriteAllText(lockedFile, "only");
        _settingsService.Settings.Widgets.Add(widget);
        Directory.CreateDirectory(Path.Combine(_newStorageRoot, "A"));

        var skipOptions = new ManagedStorageMigrationOptions(
            OnItemError: _ => Task.FromResult(FileService.FileTransferItemAction.Skip));
        var lockStream = new FileStream(
            lockedFile, FileMode.Open, FileAccess.Read, FileShare.None);
        ManagedStorageMigrationResult result =
            await _widgetManager.UpdateDefaultManagedStorageRootAsync(
                _newStorageRoot, skipOptions);
        lockStream.Dispose();

        Assert.Single(result.SkippedItems);
        Assert.Equal(folderA, widget.MappedFolderPath, ignoreCase: true);

        IReadOnlyList<ManagedStorageSkippedItem> remaining =
            await _widgetManager.RetrySkippedMigrationItemsAsync(result.SkippedItems);

        Assert.Empty(remaining);
        Assert.True(
            File.Exists(Path.Combine(_newStorageRoot, "A", "only.txt")),
            "The retried item must move into the new root.");
        // Once the old folder emptied, the widget must follow it.
        Assert.Equal(
            Path.Combine(_newStorageRoot, "A"),
            widget.MappedFolderPath,
            ignoreCase: true);
    }

    [Fact]
    public async Task UpdateDefaultManagedStorageRootAsync_ReportsProgressAcrossWidgets()
    {
        var widgetA = CreateManagedWidget("A", Path.Combine(_storageRoot, "A"));
        var widgetB = CreateManagedWidget("B", Path.Combine(_storageRoot, "B"));
        string folderA = Directory.CreateDirectory(widgetA.MappedFolderPath!).FullName;
        string folderB = Directory.CreateDirectory(widgetB.MappedFolderPath!).FullName;
        File.WriteAllText(Path.Combine(folderA, "a.txt"), "a");
        File.WriteAllText(Path.Combine(folderB, "b.txt"), "b");
        _settingsService.Settings.Widgets.Add(widgetA);
        _settingsService.Settings.Widgets.Add(widgetB);

        // The per-folder inner progress flows through Progress<T>, which
        // posts to the thread pool without a SynchronizationContext: the
        // collection must tolerate concurrent writers.
        var reports = new ConcurrentQueue<ManagedStorageMigrationProgress>();
        var options = new ManagedStorageMigrationOptions(
            Progress: new InlineProgress<ManagedStorageMigrationProgress>(
                reports.Enqueue));

        await _widgetManager.UpdateDefaultManagedStorageRootAsync(
            _newStorageRoot, options);

        Assert.False(reports.IsEmpty);
        Assert.Equal(2, reports.Max(report => report.TotalWidgets));
        Assert.Equal(2, reports.Max(report => report.CompletedWidgets));
        Assert.Equal(2, reports.Max(report => report.TotalItems));
        Assert.Contains(reports, report => report.CurrentWidgetName == "A");
        Assert.Contains(reports, report => report.CurrentWidgetName == "B");
    }

    [Fact]
    public void ClassifyTransferError_MapsKnownFailuresToKinds()
    {
        Assert.Equal(
            FileService.FileTransferItemErrorKind.InUse,
            FileService.ClassifyTransferError(
                new IOException("locked") { HResult = unchecked((int)0x80070020) }));
        // new IOException(msg, rawWin32Code) must classify like
        // HRESULT_FROM_WIN32(0x20).
        Assert.Equal(
            FileService.FileTransferItemErrorKind.InUse,
            FileService.ClassifyTransferError(
                new IOException("locked", 0x20)));
        Assert.Equal(
            FileService.FileTransferItemErrorKind.AccessDenied,
            FileService.ClassifyTransferError(new UnauthorizedAccessException()));
        Assert.Equal(
            FileService.FileTransferItemErrorKind.NotFound,
            FileService.ClassifyTransferError(new FileNotFoundException()));
        Assert.Equal(
            FileService.FileTransferItemErrorKind.PathTooLong,
            FileService.ClassifyTransferError(new PathTooLongException()));
        Assert.Equal(
            FileService.FileTransferItemErrorKind.DiskFull,
            FileService.ClassifyTransferError(
                new IOException("full") { HResult = unchecked((int)0x80070070) }));
        // The partial-failure wrapper must be unwrapped before classifying.
        Assert.Equal(
            FileService.FileTransferItemErrorKind.InUse,
            FileService.ClassifyTransferError(
                new FileService.FileTransferPartialFailureException(
                    [],
                    new IOException("locked")
                    {
                        HResult = unchecked((int)0x80070020)
                    })));
        Assert.Equal(
            FileService.FileTransferItemErrorKind.Unknown,
            FileService.ClassifyTransferError(new InvalidOperationException("odd")));
    }

    private string _newStoragePath()
    {
        return SettingsService.NormalizeManagedStorageRootPath(_newStorageRoot);
    }

    private static WidgetConfig CreateManagedWidget(string name, string folderPath)
    {
        return new WidgetConfig
        {
            Name = name,
            WidgetKind = WidgetKind.File,
            MappedFolderPath = folderPath,
            FollowsDefaultStoragePath = true,
            ManagedFolderName = Path.GetFileName(folderPath)
        };
    }

    private sealed class InlineProgress<T>(Action<T> callback) : IProgress<T>
    {
        public void Report(T value) => callback(value);
    }
}
