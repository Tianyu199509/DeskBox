using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

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
            CancellationToken cancellationToken)
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

            return completedOperations
                .Select(operation => new FileTransferResult(
                    operation.SourcePath,
                    operation.DestinationPath))
                .ToList();
        }
        catch (OperationCanceledException)
        {
            App.Log(
                $"[FileTransfer] Managed canceling count={operations.Count} " +
                $"move={move} bytes={reporter.BytesTransferred} " +
                $"elapsedMs={reporter.ElapsedMilliseconds}");
            reporter.Report(FileTransferPhase.Canceling, force: true);
            await RollbackTransfersAsync(completedOperations, move);
            reporter.Report(FileTransferPhase.Canceled, force: true);
            App.Log(
                $"[FileTransfer] Managed canceled count={operations.Count} " +
                $"move={move} rollbackCount={completedOperations.Count} " +
                $"elapsedMs={reporter.ElapsedMilliseconds}");
            throw;
        }
        catch
        {
            reporter.Report(FileTransferPhase.Failed, force: true);
            await RollbackTransfersAsync(completedOperations, move);
            throw;
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

    private static async Task CopyFileWithProgressAsync(
        string sourceFilePath,
        string destinationFilePath,
        TransferProgressReporter reporter,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destinationFilePath)!);
        var sourceInfo = new FileInfo(sourceFilePath);
        reporter.SetCurrentItem(sourceInfo.Name);

        bool destinationCreated = false;
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
            await using var destination = new FileStream(
                destinationFilePath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            destinationCreated = true;

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
        }
        catch
        {
            // FileMode.CreateNew can fail because another process created the
            // destination after planning. Never delete that pre-existing file;
            // only clean up a destination stream this operation opened.
            if (destinationCreated)
            {
                TryDeletePartialFile(destinationFilePath);
            }

            throw;
        }

        CopyFileMetadata(sourceInfo, destinationFilePath);
        reporter.Report(FileTransferPhase.Transferring, force: false);
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

        await CopyFileWithProgressAsync(
            sourceFilePath,
            destinationFilePath,
            reporter,
            cancellationToken);
        try
        {
            FileAttributes sourceAttributes = sourceInfo.Attributes;
            await Task.Run(
                () => DeleteSourceFileAfterCopy(
                    sourceFilePath,
                    sourceAttributes),
                cancellationToken);
        }
        catch
        {
            TryDeletePartialFile(destinationFilePath);
            throw;
        }
    }

    internal static void DeleteSourceFileAfterCopy(
        string sourceFilePath,
        FileAttributes originalAttributes)
    {
        bool clearedReadOnly = originalAttributes.HasFlag(
            FileAttributes.ReadOnly);
        if (clearedReadOnly)
        {
            File.SetAttributes(
                sourceFilePath,
                originalAttributes & ~FileAttributes.ReadOnly);
        }

        try
        {
            File.Delete(sourceFilePath);
        }
        catch
        {
            if (clearedReadOnly && File.Exists(sourceFilePath))
            {
                File.SetAttributes(sourceFilePath, originalAttributes);
            }

            throw;
        }
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
            if (GetVolumePathName(
                    candidatePath,
                    volumePath,
                    (uint)volumePath.Capacity))
            {
                return volumePath.ToString();
            }
        }

        return Path.GetPathRoot(fullPath);
    }

    [DllImport(
        "kernel32.dll",
        EntryPoint = "GetVolumePathNameW",
        CharSet = CharSet.Unicode,
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumePathName(
        string fileName,
        StringBuilder volumePathName,
        uint bufferLength);

    private static async Task CopyDirectoryWithProgressAsync(
        string sourceDirectory,
        string destinationDirectory,
        TransferProgressReporter reporter,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(destinationDirectory);
        var completedChildOperations = new List<TransferOperation>();
        try
        {
            foreach (string filePath in Directory.EnumerateFiles(sourceDirectory))
            {
                cancellationToken.ThrowIfCancellationRequested();
                string destinationFilePath = GetAvailableDestinationPath(
                    destinationDirectory,
                    Path.GetFileName(filePath));
                await CopyFileWithProgressAsync(
                    filePath,
                    destinationFilePath,
                    reporter,
                    cancellationToken);
                completedChildOperations.Add(
                    new TransferOperation(filePath, destinationFilePath));
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
                    cancellationToken);
                completedChildOperations.Add(
                    new TransferOperation(subDirectory, destinationSubDirectory));
            }
        }
        catch
        {
            await RollbackTransfersAsync(completedChildOperations, move: false);
            if (Directory.Exists(destinationDirectory) &&
                !Directory.EnumerateFileSystemEntries(destinationDirectory).Any())
            {
                Directory.Delete(destinationDirectory, recursive: false);
            }

            throw;
        }
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
        await CopyDirectoryWithProgressAsync(
            sourceDirectory,
            destinationDirectory,
            reporter,
            cancellationToken);
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
                () => Directory.Delete(sourceDirectory, recursive: true),
                CancellationToken.None);
        }
        catch (Exception ex) when (
            ex is UnauthorizedAccessException or IOException)
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

    private static void TryDeletePartialFile(string destinationFilePath)
    {
        try
        {
            if (File.Exists(destinationFilePath))
            {
                File.SetAttributes(destinationFilePath, FileAttributes.Normal);
                File.Delete(destinationFilePath);
            }
        }
        catch (Exception ex)
        {
            App.Log(
                $"[FileTransfer] Failed to remove partial file " +
                $"'{destinationFilePath}': {ex.Message}");
        }
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
