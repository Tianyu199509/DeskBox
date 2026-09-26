using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using DeskBox.Helpers;
using DeskBox.Platform;
using Microsoft.Win32.SafeHandles;

namespace DeskBox.Services;

public sealed partial class FileService
{
    public enum FileTransferPhase
    {
        Preparing,
        DelegatedToShell,
        Transferring,
        Finalizing,
        Canceling,
        Completed,
        Canceled,
        Failed
    }

    public sealed record FileTransferProgress(
        FileTransferPhase Phase,
        string? CurrentItemName,
        int CompletedItems,
        int TotalItems,
        long BytesTransferred,
        long? TotalBytes,
        double? BytesPerSecond,
        TimeSpan? EstimatedRemaining)
    {
        public double? Percentage
        {
            get
            {
                if (TotalBytes is > 0)
                {
                    return Math.Clamp(
                        BytesTransferred * 100d / TotalBytes.Value,
                        0d,
                        100d);
                }

                if (TotalItems > 0 && CompletedItems > 0)
                {
                    return Math.Clamp(
                        CompletedItems * 100d / TotalItems,
                        0d,
                        100d);
                }

                return null;
            }
        }
    }

    private sealed record TransferWorkEstimate(long Bytes, bool IsExact);

    private async Task<IReadOnlyList<FileTransferResult>>
        ExecuteManagedTransferPlanWithProgressAsync(
            IReadOnlyList<TransferOperation> operations,
            bool move,
            IProgress<FileTransferProgress>? progress,
            CancellationToken cancellationToken,
            Func<FileTransferItemError, Task<FileTransferItemAction>>? onItemError = null,
            ICollection<FileTransferSkippedItem>? skippedItems = null)
    {
        var reporter = new TransferProgressReporter(progress, operations.Count);
        var completedOperations = new List<TransferOperation>(operations.Count);
        try
        {
            reporter.Report(FileTransferPhase.Preparing, force: true);
            Dictionary<string, TransferWorkEstimate> estimates =
                await Task.Run(
                    () => EstimateTransferWork(operations, cancellationToken),
                    cancellationToken);
            reporter.SetTotalBytes(
                estimates.Values.All(estimate => estimate.IsExact)
                    ? estimates.Values.Sum(estimate => estimate.Bytes)
                    : null);
            App.Log(
                $"[FileTransfer] Managed start count={operations.Count} " +
                $"move={move} totalBytes={reporter.TotalBytes?.ToString() ?? "unknown"}");
            reporter.Report(FileTransferPhase.Transferring, force: true);

            foreach (TransferOperation operation in operations)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await Task.Run(
                    () => EnsureSafeDirectoryTransfers([operation]),
                    cancellationToken);

                estimates.TryGetValue(operation.SourcePath, out TransferWorkEstimate? estimate);
                while (true)
                {
                    try
                    {
                        if (move)
                        {
                            await MoveEntryWithProgressAsync(
                                operation.SourcePath,
                                operation.DestinationPath,
                                estimate,
                                reporter,
                                cancellationToken);
                        }
                        else
                        {
                            await CopyEntryWithProgressAsync(
                                operation.SourcePath,
                                operation.DestinationPath,
                                reporter,
                                cancellationToken);
                        }

                        completedOperations.Add(operation);
                        break;
                    }
                    catch (Exception itemException) when (
                        onItemError is not null &&
                        itemException is not (
                            OperationCanceledException or
                            FileTransferSourceCleanupException or
                            FileTransferSourceChangedException or
                            FileTransferDestinationCleanupException))
                    {
                        // The destination of a failed copy was already removed
                        // through its own open handle and the source is
                        // untouched, so asking the caller what to do with this
                        // item is safe. Cleanup/changed-source exceptions are
                        // excluded: their destination is a complete copy and
                        // must keep propagating to the batch-level handlers.
                        // A destination-cleanup failure is excluded too: its
                        // destination is an unremovable half-copy, and a
                        // "skip" answer would drop that residue untracked.
                        FileTransferItemAction action = await onItemError(
                            new FileTransferItemError(
                                operation.SourcePath,
                                operation.DestinationPath,
                                itemException));
                        if (action == FileTransferItemAction.Retry)
                        {
                            reporter.SetCurrentItem(
                                Path.GetFileName(operation.SourcePath));
                            continue;
                        }

                        if (action == FileTransferItemAction.Skip)
                        {
                            skippedItems?.Add(new FileTransferSkippedItem(
                                operation.SourcePath,
                                operation.DestinationPath,
                                ClassifyTransferError(itemException),
                                itemException.Message));
                            App.Log(
                                $"[FileTransfer] Item skipped " +
                                $"source='{operation.SourcePath}' " +
                                $"destination='{operation.DestinationPath}': " +
                                $"{itemException.Message}");
                            break;
                        }

                        // Abort surfaces as cancellation so the batch-level
                        // path restores/logs exactly like a user cancel.
                        throw new OperationCanceledException(cancellationToken);
                    }
                }

                reporter.CompleteItem(Path.GetFileName(operation.SourcePath));
                // A progress consumer can request cancellation from the final
                // byte/item callback. Check once more after registering the
                // completed operation so rollback can restore/remove it.
                cancellationToken.ThrowIfCancellationRequested();
            }

            reporter.Report(FileTransferPhase.Finalizing, force: true);
            reporter.Report(FileTransferPhase.Completed, force: true);
            App.Log(
                $"[FileTransfer] Managed completed count={operations.Count} " +
                $"move={move} bytes={reporter.BytesTransferred} " +
                $"elapsedMs={reporter.ElapsedMilliseconds}");
            NotifyShellDirectoriesUpdated(completedOperations, move);

            return completedOperations
                .Select(operation => new FileTransferResult(
                    operation.SourcePath,
                    operation.DestinationPath))
                .ToList();
        }
        catch (OperationCanceledException)
        {
            // Explorer semantics: completed items stay, the in-flight item's
            // partial destination was already removed through its own open
            // handle, and pending items never run. Rolling back completed
            // work would require deleting verified-owned objects while their
            // source may have vanished meanwhile — the exact class of
            // operation this engine refuses to perform.
            App.Log(
                $"[FileTransfer] Managed canceling count={operations.Count} " +
                $"move={move} bytes={reporter.BytesTransferred} " +
                $"elapsedMs={reporter.ElapsedMilliseconds}");
            reporter.Report(FileTransferPhase.Canceling, force: true);
            reporter.Report(FileTransferPhase.Canceled, force: true);
            App.Log(
                $"[FileTransfer] Managed canceled count={operations.Count} " +
                $"move={move} keptCompleted={completedOperations.Count} " +
                $"elapsedMs={reporter.ElapsedMilliseconds}");
            // The completed results ride the exception so callers (history,
            // journals, undo) learn what physically moved even though the
            // batch stopped early.
            throw new FileTransferCanceledException(
                completedOperations
                    .Select(operation => new FileTransferResult(
                        operation.SourcePath,
                        operation.DestinationPath))
                    .ToList(),
                cancellationToken);
        }
        catch (Exception exception)
        {
            // Partial completion: completed items stay (same reasoning as
            // cancellation); the failure itself propagates with the
            // completed results attached. An item-level exception that
            // carries its own receipts (e.g. a move whose source cleanup
            // failed after the copy completed) keeps them in the wrapper:
            // the data did physically move.
            reporter.Report(FileTransferPhase.Failed, force: true);
            var completedSnapshot = completedOperations
                .Select(operation => new FileTransferResult(
                    operation.SourcePath,
                    operation.DestinationPath))
                .ToList();
            if (exception is IFileTransferWithCompletedResults itemLevelResults)
            {
                completedSnapshot.AddRange(itemLevelResults.CompletedResults);
            }

            throw new FileTransferPartialFailureException(completedSnapshot, exception);
        }
    }

    private static void NotifyShellDirectoriesUpdated(
        IReadOnlyList<TransferOperation> operations,
        bool move)
    {
        // Raw file APIs plus per-item rename notifications still leave
        // OneDrive-backed folder views (typically redirected desktops)
        // showing stale entries. Forcing one re-enumeration per affected
        // directory is the programmatic equivalent of the user pressing F5.
        var directories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (TransferOperation operation in operations)
        {
            if (move)
            {
                AddParentDirectory(directories, operation.SourcePath);
            }

            AddParentDirectory(directories, operation.DestinationPath);
        }

        foreach (string directory in directories)
        {
            Win32Helper.NotifyShellDirectoryUpdated(directory);
        }

        static void AddParentDirectory(ISet<string> target, string path)
        {
            try
            {
                string? parent = Path.GetDirectoryName(path);
                if (!string.IsNullOrWhiteSpace(parent))
                {
                    target.Add(parent);
                }
            }
            catch (ArgumentException)
            {
            }
        }
    }

    private static Dictionary<string, TransferWorkEstimate> EstimateTransferWork(
        IReadOnlyList<TransferOperation> operations,
        CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, TransferWorkEstimate>(
            StringComparer.OrdinalIgnoreCase);
        foreach (TransferOperation operation in operations)
        {
            cancellationToken.ThrowIfCancellationRequested();
            result[operation.SourcePath] = EstimateTransferWork(
                operation.SourcePath,
                cancellationToken);
        }

        return result;
    }

    private static TransferWorkEstimate EstimateTransferWork(
        string sourcePath,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            if (File.Exists(sourcePath))
            {
                return new TransferWorkEstimate(
                    Math.Max(0, new FileInfo(sourcePath).Length),
                    IsExact: true);
            }

            if (!Directory.Exists(sourcePath))
            {
                return new TransferWorkEstimate(0, IsExact: false);
            }

            // Never recursively enumerate a directory before starting its
            // transfer. A deep folder, an offline cloud provider or a slow
            // network share can otherwise hold the UI at "Preparing 0/1" for
            // minutes before the first byte is copied. Directory progress is
            // item-based until the transfer itself discovers its contents.
            return new TransferWorkEstimate(0, IsExact: false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (
            ex is UnauthorizedAccessException or IOException or OverflowException)
        {
            return new TransferWorkEstimate(0, IsExact: false);
        }
    }

    private static async Task CopyEntryWithProgressAsync(
        string sourcePath,
        string destinationPath,
        TransferProgressReporter reporter,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (File.Exists(sourcePath))
        {
            await CopyFileWithProgressAsync(
                sourcePath,
                destinationPath,
                reporter,
                cancellationToken);
            return;
        }

        if (Directory.Exists(sourcePath))
        {
            await CopyDirectoryWithProgressAsync(
                sourcePath,
                destinationPath,
                reporter,
                cancellationToken);
        }
    }

    private static async Task MoveEntryWithProgressAsync(
        string sourcePath,
        string destinationPath,
        TransferWorkEstimate? estimate,
        TransferProgressReporter reporter,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (File.Exists(sourcePath))
        {
            await MoveFileWithProgressAsync(
                sourcePath,
                destinationPath,
                reporter,
                cancellationToken);
            return;
        }

        if (Directory.Exists(sourcePath))
        {
            await MoveDirectoryWithProgressAsync(
                sourcePath,
                destinationPath,
                estimate,
                reporter,
                cancellationToken);
        }
    }

    /// <summary>
    /// Copies one file and returns both object identities, each read from
    /// the very handle that performed the work while it was still open —
    /// the directory-move source cleanup verifies each source deletion
    /// against the first, and the partial-destination cleanup verifies
    /// each created destination file against the second.
    /// </summary>
    private static async Task<(FileTransferSourceIdentity? Source, FileTransferSourceIdentity? Destination)>
        CopyFileWithProgressAsync(
            string sourceFilePath,
            string destinationFilePath,
            TransferProgressReporter reporter,
            CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destinationFilePath)!);
        var sourceInfo = new FileInfo(sourceFilePath);
        reporter.SetCurrentItem(sourceInfo.Name);

        FileStream? destination = null;
        FileTransferSourceIdentity? sourceIdentity;
        FileTransferSourceIdentity? destinationIdentity;
        try
        {
            const int bufferSize = 256 * 1024;
            await using var source = new FileStream(
                sourceFilePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            sourceIdentity = IdentityFromHandle(source.SafeFileHandle);
            (destination, destinationIdentity) = await CopyFileCoreAsync(
                source,
                sourceInfo,
                destinationFilePath,
                reporter,
                cancellationToken);
        }
        finally
        {
            // Plain copy: no commit follows, so the destination simply closes.
            TryDisposeQuietly(destination);
        }

        reporter.Report(FileTransferPhase.Transferring, force: false);
        return (sourceIdentity, destinationIdentity);
    }

    /// <summary>
    /// Copies from an open source stream into a CreateNew destination
    /// handle, flushes, and applies metadata — then returns with the
    /// destination STILL OPEN and carrying its object identity. The caller
    /// owns the commit decision (a move keeps it open through the source
    /// disposition; a copy closes it). A failure deletes the partial
    /// destination through that same handle.
    /// </summary>
    private static async Task<(FileStream Stream, FileTransferSourceIdentity? DestinationIdentity)> CopyFileCoreAsync(
        FileStream source,
        FileInfo sourceInfo,
        string destinationFilePath,
        TransferProgressReporter reporter,
        CancellationToken cancellationToken)
    {
        const int bufferSize = 256 * 1024;
        FileStream destination = CreateTransferDestinationStream(destinationFilePath);
        try
        {
            byte[] buffer = new byte[bufferSize];
            while (true)
            {
                int bytesRead = await source.ReadAsync(
                    buffer.AsMemory(0, buffer.Length),
                    cancellationToken);
                if (bytesRead == 0)
                {
                    break;
                }

                await destination.WriteAsync(
                    buffer.AsMemory(0, bytesRead),
                    cancellationToken);
                reporter.AddBytes(bytesRead, sourceInfo.Name);
            }

            await destination.FlushAsync(cancellationToken);
            // Still exclusively held (ShareNone), so the path cannot point
            // anywhere else while metadata is applied.
            CopyFileMetadata(sourceInfo, destinationFilePath);
            return (destination, IdentityFromHandle(destination.SafeFileHandle));
        }
        catch
        {
            // Delete the partial file through the still-open handle bound to
            // the object this copy created — never by path, and never a file
            // someone else placed at the path.
            TryDisposeWithHandleDeletion(destination);
            throw;
        }
    }

    /// <summary>
    /// Closes a destination stream without deleting the file — used when the
    /// destination must survive (copy completion, kept-both-copies outcomes).
    /// </summary>
    private static void TryDisposeQuietly(FileStream? destination)
    {
        if (destination is null)
        {
            return;
        }

        try
        {
            destination.Dispose();
        }
        catch (Exception ex)
        {
            App.Log(
                $"[FileTransfer] Failed to close the destination stream for " +
                $"'{destination.Name}': {ex.Message}");
        }
    }

    /// <summary>
    /// Disposes a destination stream whose transfer failed, deleting the
    /// partial file through that same handle (POSIX-style disposition) so
    /// only the object this transfer created can ever be removed.
    /// </summary>
    internal static void TryDisposeWithHandleDeletion(FileStream? destination)
    {
        if (destination is null)
        {
            return;
        }

        try
        {
            if (!destination.SafeFileHandle.IsClosed)
            {
                var disposition = new FileTransferNativeMethods.FileDispositionInfo { Delete = true };
                bool deleted = FileTransferNativeMethods.SetFileInformationByHandle(
                    destination.SafeFileHandle,
                    FileDispositionInfoClass,
                    ref disposition,
                    Marshal.SizeOf<FileTransferNativeMethods.FileDispositionInfo>());
                App.Log(
                    $"[FileTransfer] Partial cleanup disposition applied={deleted} " +
                    $"for '{destination.Name}'");
            }
            else
            {
                App.Log(
                    $"[FileTransfer] Partial cleanup skipped: the handle for " +
                    $"'{destination.Name}' was already closed before disposition.");
            }
        }
        catch (Exception ex)
        {
            App.Log(
                $"[FileTransfer] Handle-bound partial deletion failed for " +
                $"'{destination.Name}': {ex.Message}");
        }
        finally
        {
            try
            {
                destination.Dispose();
            }
            catch (Exception ex)
            {
                App.Log(
                    $"[FileTransfer] Failed to dispose the partial stream for " +
                    $"'{destination.Name}': {ex.Message}");
            }
        }
    }

    private static async Task MoveFileWithProgressAsync(
        string sourceFilePath,
        string destinationFilePath,
        TransferProgressReporter reporter,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destinationFilePath)!);
        var sourceInfo = new FileInfo(sourceFilePath);
        reporter.SetCurrentItem(sourceInfo.Name);
        cancellationToken.ThrowIfCancellationRequested();
        // Capture all source metadata before File.Move. Accessing FileInfo.Length
        // after a successful rename reopens the now-missing source path and was
        // incorrectly treated as a cross-volume move failure.
        long sourceLength = sourceInfo.Length;

        bool canUseAtomicMove = CanUseAtomicMove(
            sourceFilePath,
            destinationFilePath);
        App.Log(
            $"[FileTransfer] File move mode=" +
            $"{(canUseAtomicMove ? "atomic" : "chunked-cross-volume")} " +
            $"name='{sourceInfo.Name}' bytes={sourceLength} " +
            $"sourceRoot='{Path.GetPathRoot(sourceFilePath)}' " +
            $"destinationRoot='{Path.GetPathRoot(destinationFilePath)}'");

        try
        {
            if (canUseAtomicMove)
            {
                await Task.Run(
                    () => File.Move(sourceFilePath, destinationFilePath),
                    cancellationToken);
                reporter.AddBytes(sourceLength, sourceInfo.Name, force: true);
                Win32Helper.NotifyShellItemMoved(sourceFilePath, destinationFilePath);
                return;
            }
        }
        catch (IOException) when (
            File.Exists(sourceFilePath) &&
            !File.Exists(destinationFilePath))
        {
            // Cross-volume moves cannot be renamed atomically. Copy in chunks
            // so cancellation and real byte progress remain available.
        }

        // Cross-volume fallback: ONE source handle (READ|DELETE, shared for
        // reading only) spans the entire copy-to-commit sequence. Nobody can
        // write, rename, or delete the source mid-move, and the final
        // disposition deletes the exact object that was copied — no gap
        // remains between validation and deletion.
        SafeFileHandle sourceHandle = FileTransferNativeMethods.CreateFileW(
            sourceFilePath,
            GenericReadAccess | DeleteAccess | FileWriteAttributesAccess,
            ShareRead,
            IntPtr.Zero,
            OpenExisting,
            FileFlagOverlapped,
            IntPtr.Zero);
        if (sourceHandle.IsInvalid)
        {
            int openError = Marshal.GetLastWin32Error();
            // ERROR_SHARING_VIOLATION means another process is writing the
            // source: refuse the move rather than copy a moving target.
            // ERROR_ACCESS_DENIED (readable-but-not-deletable sources such as
            // Public Desktop shortcuts under an unelevated host) is a
            // permission failure and must not be reported as "in use".
            string refusal = openError == ErrorSharingViolation
                ? "is in use"
                : $"is not accessible to this process (win32={openError})";
            throw new IOException(
                $"The source '{sourceFilePath}' {refusal} and cannot be " +
                "moved safely.",
                openError);
        }

        FileStream? destination = null;
        try
        {
            await using var source = new FileStream(
                sourceHandle,
                FileAccess.Read,
                256 * 1024,
                isAsync: true);
            if (IdentityFromHandle(sourceHandle) is null)
            {
                // Nothing has been copied yet, so this is an ordinary move
                // failure — it must not surface completed results or read as
                // "copied but source retained" to the history/journal layers.
                throw new IOException(
                    $"The source '{sourceFilePath}' cannot be moved safely: its " +
                    "object identity could not be read on this file system.");
            }

            (destination, _) = await CopyFileCoreAsync(
                source,
                sourceInfo,
                destinationFilePath,
                reporter,
                cancellationToken);

            // The copy was read from this very handle; the disposition
            // therefore deletes exactly the object that was copied.
            if (!FileTransferNativeMethods.GetFileInformationByHandle(sourceHandle, out FileTransferNativeMethods.ByHandleFileInformation sourceInformation))
            {
                throw new FileTransferSourceCleanupException(
                    sourceFilePath,
                    destinationFilePath,
                    new InvalidOperationException(
                        "The source file state could not be read before deletion."));
            }

            if (!TrySetDispositionByHandle(sourceHandle, in sourceInformation, sourceFilePath))
            {
                // Copy complete, source retained: both copies stay.
                throw new FileTransferSourceCleanupException(
                    sourceFilePath,
                    destinationFilePath,
                    new IOException(
                        $"The source '{sourceFilePath}' could not be deleted after the copy."));
            }

            // The source deletion completes when its handle closes: finish
            // the source transaction before releasing the destination's
            // exclusive hold, so at least one protected copy exists at every
            // instant of the commit.
            await source.DisposeAsync();
            TryDisposeQuietly(destination);
            destination = null;
        }
        catch (FileTransferSourceChangedException)
        {
            // Both copies stay; the destination holds the complete original
            // content, so the destination cleanup must not run.
            TryDisposeQuietly(destination);
            throw;
        }
        catch (FileTransferSourceCleanupException)
        {
            // Copy complete, source retained by design: the destination holds
            // the full content and must not be cleaned up.
            TryDisposeQuietly(destination);
            throw;
        }
        catch
        {
            // The copy itself failed: remove the destination through its own
            // still-open handle — never by path, never a replacement.
            TryDisposeWithHandleDeletion(destination);
            throw;
        }

        Win32Helper.NotifyShellItemMoved(sourceFilePath, destinationFilePath);
    }

    internal static bool CanUseAtomicMove(
        string sourcePath,
        string destinationPath)
    {
        try
        {
            string? sourceRoot = GetComparableVolumeRoot(
                sourcePath,
                useParentPath: false);
            string? destinationRoot = GetComparableVolumeRoot(
                destinationPath,
                useParentPath: true);
            return !string.IsNullOrWhiteSpace(sourceRoot) &&
                   !string.IsNullOrWhiteSpace(destinationRoot) &&
                   string.Equals(
                       Path.TrimEndingDirectorySeparator(sourceRoot),
                       Path.TrimEndingDirectorySeparator(destinationRoot),
                       StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (
            ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            // An uncertain path identity must use the observable chunked path.
            return false;
        }
    }

    internal static bool CanUseLegacyShellMove(
        IEnumerable<FileTransferPlan> plans)
    {
        ArgumentNullException.ThrowIfNull(plans);

        bool hasPlan = false;
        foreach (FileTransferPlan plan in plans)
        {
            hasPlan = true;
            if (!CanUseAtomicMove(plan.SourcePath, plan.DestinationPath))
            {
                return false;
            }
        }

        return hasPlan;
    }

    private static string? GetComparableVolumeRoot(
        string path,
        bool useParentPath)
    {
        string fullPath = Path.GetFullPath(path);
        string candidatePath = useParentPath
            ? Path.GetDirectoryName(fullPath) ?? fullPath
            : fullPath;

        // UNC share roots already identify the effective volume and should not
        // trigger a network probe just to compare two paths.
        if (OperatingSystem.IsWindows() &&
            !candidatePath.StartsWith(@"\\", StringComparison.Ordinal))
        {
            var volumePath = new StringBuilder(512);
            if (Kernel32NativeMethods.GetVolumePathName(
                    candidatePath,
                    volumePath,
                    (uint)volumePath.Capacity))
            {
                return volumePath.ToString();
            }
        }

        return Path.GetPathRoot(fullPath);
    }

    private static Task CopyDirectoryWithProgressAsync(
        string sourceDirectory,
        string destinationDirectory,
        TransferProgressReporter reporter,
        CancellationToken cancellationToken)
    {
        return CopyDirectoryWithProgressAsync(
            sourceDirectory,
            destinationDirectory,
            reporter,
            cancellationToken,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            new List<CopiedSourceFileRecord>());
    }

    /// <summary>
    /// Copies one directory tree. Completed children are never rolled back
    /// (Explorer semantics); the manifest of copied source files is recorded
    /// for the directory-move source cleanup, which only runs after the
    /// entire tree has copied and been verified. When
    /// <paramref name="copiedDestinationFiles"/> is supplied each created
    /// destination file is recorded with its object identity too, so a
    /// failed move can remove exactly the objects it created.
    /// </summary>
    private static async Task CopyDirectoryWithProgressAsync(
        string sourceDirectory,
        string destinationDirectory,
        TransferProgressReporter reporter,
        CancellationToken cancellationToken,
        ISet<string> visitedSourceDirectories,
        List<CopiedSourceFileRecord> copiedSourceFiles,
        List<CopiedDestinationFileRecord>? copiedDestinationFiles = null,
        List<string>? createdDestinationDirectories = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureSafeRecursiveDirectoryCopy(
            sourceDirectory,
            destinationDirectory,
            visitedSourceDirectories);
        CreateDestinationDirectory(destinationDirectory, createdDestinationDirectories);
        foreach (string filePath in Directory.EnumerateFiles(sourceDirectory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string destinationFilePath = GetAvailableDestinationPath(
                destinationDirectory,
                Path.GetFileName(filePath));
            // Capture the pre-copy state: a mismatch during cleanup means
            // the file changed while it was being copied. FileInfo stats
            // lazily on first property access, so read both values now.
            var sourceInfo = new FileInfo(filePath);
            long sourceLength = sourceInfo.Length;
            DateTime sourceLastWriteUtc = sourceInfo.LastWriteTimeUtc;
            // The source identity comes from the copy's own handle: it
            // describes the object that was actually read, never whatever
            // may have appeared at the path after the copy finished. The
            // destination identity is captured the same way — from the
            // still-open CreateNew handle, not from the path.
            (FileTransferSourceIdentity? sourceIdentity,
                FileTransferSourceIdentity? destinationIdentity) =
                await CopyFileWithProgressAsync(
                    filePath,
                    destinationFilePath,
                    reporter,
                    cancellationToken);
            copiedSourceFiles.Add(new CopiedSourceFileRecord(
                filePath,
                sourceLength,
                sourceLastWriteUtc,
                sourceIdentity));
            copiedDestinationFiles?.Add(new CopiedDestinationFileRecord(
                destinationFilePath,
                destinationIdentity));
        }

        foreach (string subDirectory in Directory.EnumerateDirectories(sourceDirectory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string destinationSubDirectory = GetAvailableDestinationPath(
                destinationDirectory,
                Path.GetFileName(subDirectory));
            await CopyDirectoryWithProgressAsync(
                subDirectory,
                destinationSubDirectory,
                reporter,
                cancellationToken,
                visitedSourceDirectories,
                copiedSourceFiles,
                copiedDestinationFiles,
                createdDestinationDirectories);
        }
    }

    /// <summary>
    /// Creates one destination directory level through CreateDirectoryW so
    /// the return value atomically proves THIS operation created it — an
    /// Exists-check before a managed CreateDirectory could still record a
    /// directory a foreign actor won in between. Directories that already
    /// exist never reach the manifest: cleanup may only delete objects this
    /// operation provably created.
    /// </summary>
    private static void CreateDestinationDirectory(
        string path,
        List<string>? createdDestinationDirectories)
    {
        if (createdDestinationDirectories is null)
        {
            Directory.CreateDirectory(path);
            return;
        }

        if (TryCreateOwnedDirectory(path))
        {
            createdDestinationDirectories.Add(path);
        }
    }

    /// <summary>
    /// Creates exactly one directory level through CreateDirectoryW and
    /// reports whether THIS call created it — the atomic ownership proof an
    /// Exists-check before a managed CreateDirectory cannot give (a foreign
    /// actor can win the gap in between). False means the path already
    /// existed: not ours, whoever made it keeps it.
    /// </summary>
    private static bool TryCreateOwnedDirectory(string path)
    {
        if (Kernel32NativeMethods.CreateDirectory(path, IntPtr.Zero))
        {
            return true;
        }

        int error = Marshal.GetLastWin32Error();
        if (error == ErrorAlreadyExists)
        {
            return false;
        }

        throw new IOException(
            $"Failed to create destination directory '{path}' (win32={error}).",
            new System.ComponentModel.Win32Exception(error));
    }

    private static async Task MoveDirectoryWithProgressAsync(
        string sourceDirectory,
        string destinationDirectory,
        TransferWorkEstimate? estimate,
        TransferProgressReporter reporter,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destinationDirectory)!);
        cancellationToken.ThrowIfCancellationRequested();
        bool canUseAtomicMove = CanUseAtomicMove(
            sourceDirectory,
            destinationDirectory);
        try
        {
            if (canUseAtomicMove && !Directory.Exists(destinationDirectory))
            {
                TransferWorkEstimate work = estimate ?? EstimateTransferWork(
                    sourceDirectory,
                    cancellationToken);
                await Task.Run(
                    () => Directory.Move(sourceDirectory, destinationDirectory),
                    cancellationToken);
                reporter.AddBytes(
                    work.Bytes,
                    Path.GetFileName(sourceDirectory),
                    force: true);
                Win32Helper.NotifyShellItemMoved(sourceDirectory, destinationDirectory);
                return;
            }
        }
        catch (IOException)
        {
            // Cross-volume directory move. Fall through to controlled moves.
        }
        catch (UnauthorizedAccessException)
        {
            // Preserve the existing fallback for paths requiring per-entry work.
        }

        // A directory fallback must preserve a complete copy before changing
        // the source tree. Moving children one by one makes a late failure
        // while deleting an empty source directory split the tree between the
        // source and destination. Copy-first guarantees that every source
        // byte still exists in at least one complete tree.
        //
        // A copy phase that stops early (cancel, item abort, hard failure)
        // leaves a partial destination tree that nothing tracks: the
        // completed-results lists only carry finished operations, so a retry
        // would meet its own half-copy — and re-copy its entries under "(2)"
        // names. When the destination held nothing before this operation,
        // the recorded manifest proves which entries are ours and they go;
        // a destination that already had content is a merge we cannot
        // untangle, so it stays.
        bool destinationExistedBefore = true;
        bool destinationPreHeldContent = true;
        try
        {
            destinationExistedBefore = Directory.Exists(destinationDirectory);
            destinationPreHeldContent =
                destinationExistedBefore &&
                Directory.EnumerateFileSystemEntries(destinationDirectory).Any();
        }
        catch
        {
            // Cannot inspect the destination: assume pre-existing content and
            // never delete.
        }

        var copiedSourceFiles = new List<CopiedSourceFileRecord>();
        var copiedDestinationFiles = new List<CopiedDestinationFileRecord>();
        var createdDestinationDirectories = new List<string>();
        try
        {
            await CopyDirectoryWithProgressAsync(
                sourceDirectory,
                destinationDirectory,
                reporter,
                cancellationToken,
                new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                copiedSourceFiles,
                copiedDestinationFiles,
                createdDestinationDirectories);
        }
        catch (Exception copyFailure)
        {
            if (!destinationPreHeldContent)
            {
                // Manifest-scoped cleanup only: each recorded destination
                // file is deleted through a handle verified against the
                // identity captured at its CreateNew call, and only
                // directories this operation provably created leave — while
                // empty. A foreign file or directory dropped into the tree
                // mid-copy is never touched.
                int stranded = CleanupCopiedDestinationTree(
                    copiedDestinationFiles,
                    createdDestinationDirectories);
                if (stranded > 0)
                {
                    // Some of OUR objects could not be removed (locked,
                    // swapped, identity lost). The half-copy must not be
                    // answered by a per-item "skip": it is residue the
                    // batch-level recovery flow has to see.
                    throw new FileTransferDestinationCleanupException(
                        sourceDirectory,
                        destinationDirectory,
                        copyFailure,
                        stranded);
                }
            }

            throw;
        }
        if (cancellationToken.IsCancellationRequested)
        {
            // The destination is already complete while the source is still
            // untouched. Preserve and report that exact partial outcome
            // instead of losing track of the copied tree during cancellation.
            throw new FileTransferCanceledException(
                [new FileTransferResult(
                    sourceDirectory,
                    destinationDirectory)],
                cancellationToken);
        }
        try
        {
            await Task.Run(
                () => DeleteSourceTreeByManifest(
                    sourceDirectory,
                    destinationDirectory,
                    copiedSourceFiles),
                CancellationToken.None);
        }
        catch (Exception ex) when (
            ex is UnauthorizedAccessException or
                (IOException and not FileTransferSourceChangedException))
        {
            App.Log(
                $"[FileTransfer] Directory copy completed but source cleanup " +
                $"failed source='{sourceDirectory}' " +
                $"destination='{destinationDirectory}': {ex}");
            throw new FileTransferSourceCleanupException(
                sourceDirectory,
                destinationDirectory,
                ex);
        }

        Win32Helper.NotifyShellItemMoved(sourceDirectory, destinationDirectory);
    }

    /// <summary>
    /// Removes only the destination objects this operation created, each
    /// deleted through a handle verified against the identity captured at
    /// its own CreateNew call — never a recursive path delete, which could
    /// take foreign files dropped into the tree mid-copy (or a directory
    /// that merely existed before we ran) down with it. Directories leave
    /// deepest-first and only while empty, so unmanifested content can
    /// survive but never disappear. Returns the number of manifest objects
    /// that could not be removed.
    /// </summary>
    private static int CleanupCopiedDestinationTree(
        IReadOnlyList<CopiedDestinationFileRecord> copiedDestinationFiles,
        IReadOnlyList<string> createdDestinationDirectories)
    {
        int stranded = 0;
        for (int index = copiedDestinationFiles.Count - 1; index >= 0; index--)
        {
            CopiedDestinationFileRecord record = copiedDestinationFiles[index];
            // Without a recorded identity there is no deletion authority:
            // keep the file and count it stranded rather than delete an
            // unverified path.
            if (record.Identity is not { } identity ||
                !TryDeleteFileByIdentity(record.DestinationFilePath, identity))
            {
                stranded++;
            }
        }

        // Only directories this operation provably created may leave, and
        // only while still empty — never a tree re-enumeration, which would
        // take foreign directories dropped in mid-copy down with ours.
        // Creation order puts parents before children, so reversing walks
        // deepest-first without a re-sort.
        for (int index = createdDestinationDirectories.Count - 1; index >= 0; index--)
        {
            string directory = createdDestinationDirectories[index];
            try
            {
                if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
                {
                    // Our directory was swapped for a junction/symlink: the
                    // object at the path is foreign now — fail closed.
                    continue;
                }

                Directory.Delete(directory, recursive: false);
            }
            catch (Exception)
            {
                // Non-empty (foreign content or a stranded file), already
                // gone, or locked — either way it stays.
            }
        }

        return stranded;
    }

    private static void CopyFileMetadata(
        FileInfo sourceInfo,
        string destinationFilePath)
    {
        try
        {
            File.SetCreationTimeUtc(destinationFilePath, sourceInfo.CreationTimeUtc);
            File.SetLastWriteTimeUtc(destinationFilePath, sourceInfo.LastWriteTimeUtc);
            File.SetAttributes(destinationFilePath, sourceInfo.Attributes);
        }
        catch (Exception ex) when (
            ex is UnauthorizedAccessException or IOException)
        {
            App.Log(
                $"[FileTransfer] Could not preserve metadata for " +
                $"'{destinationFilePath}': {ex.Message}");
        }
    }

    /// <summary>
    /// Removes a destination file this transfer created. When
    /// <paramref name="expectedLength"/> is known (a completed copy), a file
    /// that no longer carries that length is kept: it may have been replaced
    /// by another process, and neither its attributes nor its content are
    /// ours to touch.
    /// </summary>
    /// <summary>
    /// Creates the transfer destination stream: an overlapped CreateNew
    /// handle with write and delete access, shared with nobody. Returned
    /// still open — the caller owns the commit decision and the
    /// handle-bound cleanup on failure.
    /// </summary>
    internal static FileStream CreateTransferDestinationStream(string destinationFilePath)
    {
        SafeFileHandle destinationHandle = FileTransferNativeMethods.CreateFileW(
            destinationFilePath,
            GenericWriteAccess | DeleteAccess,
            ShareNone,
            IntPtr.Zero,
            CreateNewDisposition,
            FileFlagOverlapped,
            IntPtr.Zero);
        if (destinationHandle.IsInvalid)
        {
            // CreateNew semantics: a competing file at the planned path
            // fails the copy untouched.
            throw new IOException(
                $"The destination '{destinationFilePath}' could not be created.",
                Marshal.GetLastWin32Error());
        }

        return new FileStream(
            destinationHandle,
            FileAccess.Write,
            256 * 1024,
            isAsync: true);
    }

    private sealed class TransferProgressReporter(
        IProgress<FileTransferProgress>? progress,
        int totalItems)
    {
        private static readonly TimeSpan MinimumReportInterval =
            TimeSpan.FromMilliseconds(90);
        private readonly Stopwatch _stopwatch = Stopwatch.StartNew();
        private TimeSpan _lastReportAt = TimeSpan.MinValue;
        private long _bytesTransferred;
        private long? _totalBytes;
        private int _completedItems;
        private string? _currentItemName;

        public void SetTotalBytes(long? totalBytes)
        {
            _totalBytes = totalBytes is >= 0 ? totalBytes : null;
        }

        public long BytesTransferred => _bytesTransferred;

        public long? TotalBytes => _totalBytes;

        public long ElapsedMilliseconds => _stopwatch.ElapsedMilliseconds;

        public void SetCurrentItem(string? itemName)
        {
            _currentItemName = itemName;
            Report(FileTransferPhase.Transferring, force: false);
        }

        public void AddBytes(long bytes, string? itemName, bool force = false)
        {
            if (bytes > 0)
            {
                _bytesTransferred = checked(_bytesTransferred + bytes);
            }

            _currentItemName = itemName;
            Report(FileTransferPhase.Transferring, force);
        }

        public void CompleteItem(string? itemName)
        {
            _completedItems = Math.Min(totalItems, _completedItems + 1);
            _currentItemName = itemName;
            Report(FileTransferPhase.Transferring, force: true);
        }

        public void Report(FileTransferPhase phase, bool force)
        {
            if (progress is null)
            {
                return;
            }

            TimeSpan elapsed = _stopwatch.Elapsed;
            if (!force && elapsed - _lastReportAt < MinimumReportInterval)
            {
                return;
            }

            _lastReportAt = elapsed;
            double? bytesPerSecond = elapsed.TotalSeconds >= 0.2 &&
                                     _bytesTransferred > 0
                ? _bytesTransferred / elapsed.TotalSeconds
                : null;
            TimeSpan? remaining = _totalBytes is { } total &&
                                  bytesPerSecond is > 0
                ? TimeSpan.FromSeconds(Math.Max(
                    0,
                    (total - _bytesTransferred) / bytesPerSecond.Value))
                : null;

            progress.Report(new FileTransferProgress(
                phase,
                _currentItemName,
                _completedItems,
                totalItems,
                _bytesTransferred,
                _totalBytes,
                bytesPerSecond,
                remaining));
        }
    }
}
