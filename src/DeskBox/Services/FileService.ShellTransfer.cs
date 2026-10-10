using DeskBox.Helpers;
using DeskBox.Platform;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace DeskBox.Services;

public sealed partial class FileService
{
    private const uint ShellFileOperationNoConfirmMakeDirectory = 0x0200;
    private const uint ClsContextInProcessServer = 0x1;
    private const uint CoInitApartmentThreaded = 0x2;
    private const uint ShellDisplayNameFileSystemPath = 0x80058000;
    private const int ErrorCancelledHResult = unchecked((int)0x800704C7);
    private static readonly Guid s_fileOperationClassId =
        new("3AD05575-8857-4850-9277-11B85BDB8E09");
    private static readonly Guid s_fileOperationInterfaceId =
        new("947AAB5F-0A5C-4C13-B4D6-4BF7836FC9F8");
    private static readonly Guid s_shellItemInterfaceId =
        new("43826D1E-E718-42EE-BC55-A1E261C37BFE");

    // Feedback 455: a wedged IFileOperation used to hang its dedicated STA
    // thread forever — the import card stayed pending and only killing the
    // process recovered. The watchdog below declares the transfer dead only
    // after no Shell progress for the whole inactivity window (every progress
    // sink callback renews it), then abandons the unabortable thread and
    // returns a retryable failure. Bounded admission also stops repeated
    // wedged imports from piling up zombie threads without any backpressure.
    private static readonly BoundedStaOperationRunner s_shellTransferRunner =
        new(
            maxConcurrency: 4,
            maxQueued: 8,
            queueTimeout: TimeSpan.FromSeconds(10),
            threadName: "DeskBox Windows File Operation",
            coInitializationFlags: (int)CoInitApartmentThreaded);

    /// <summary>
    /// Inactivity budget for user-driven shell transfers (drag-in import,
    /// elevated drop). A healthy interactive transfer finishes in ~300ms; a
    /// legitimate large one keeps the sink talking, so prolonged total
    /// silence is the verdict "wedged", not "slow". The budget stays well
    /// above the documented UpdateProgress cadence for an actively copying
    /// item so slow media (single large file on a lagging disk) is never
    /// falsely abandoned — a wrong abandonment risks a duplicate file once
    /// the zombie transfer still completes and the user retries.
    /// </summary>
    private static readonly TimeSpan InteractiveShellTransferInactivityTimeout =
        TimeSpan.FromSeconds(45);

    /// <summary>
    /// Reserved tier for headless bulk transfers: none exist on this engine
    /// today (background batches run the managed engine), the constant keeps
    /// the interactive/headless split explicit if one is added.
    /// </summary>
    private static readonly TimeSpan HeadlessShellTransferInactivityTimeout =
        TimeSpan.FromSeconds(60);

    private async Task<IReadOnlyList<FileTransferResult>>
        ExecuteModernShellTransferPlanAsync(
            IReadOnlyList<TransferOperation> operations,
            bool move,
            IntPtr ownerWindowHandle,
            IProgress<FileTransferProgress>? progress,
            CancellationToken cancellationToken,
            bool keepBoth = false,
            Action<FileTransferResult>? itemCompleted = null)
    {
        if (operations.Count == 0)
        {
            return [];
        }

        await Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            EnsureSafeDirectoryTransfers(operations);
            foreach (TransferOperation operation in operations)
            {
                string? destinationDirectory = Path.GetDirectoryName(
                    operation.DestinationPath);
                if (!string.IsNullOrWhiteSpace(destinationDirectory))
                {
                    Directory.CreateDirectory(destinationDirectory);
                }
            }
        }, cancellationToken);

        progress?.Report(CreateShellProgress(
            FileTransferPhase.DelegatedToShell,
            operations.Count,
            completedItems: 0));

        var stopwatch = Stopwatch.StartNew();
        App.Log(
            $"[FileTransfer] Windows shell transfer start " +
            $"count={operations.Count} move={move} " +
            $"owner=0x{ownerWindowHandle.ToInt64():X}");

        ShellTransferOutcome outcome;
        try
        {
            outcome = await RunShellTransferOnStaThreadAsync(
                operations,
                move,
                ownerWindowHandle,
                cancellationToken, keepBoth, itemCompleted);
        }
        catch (OperationCanceledException)
        {
            progress?.Report(CreateShellProgress(
                FileTransferPhase.Canceled,
                operations.Count,
                completedItems: 0));
            throw;
        }
        catch (StaOperationAbandonedException)
        {
            progress?.Report(CreateShellProgress(
                FileTransferPhase.Failed,
                operations.Count,
                completedItems: 0));
            App.Log(
                $"[FileTransfer] Windows shell transfer abandoned by the " +
                $"watchdog count={operations.Count} move={move} " +
                $"owner=0x{ownerWindowHandle.ToInt64():X} " +
                $"elapsedMs={stopwatch.ElapsedMilliseconds}; reported as a " +
                $"retryable failure so the import cannot stay pending.");
            throw;
        }
        catch
        {
            progress?.Report(CreateShellProgress(
                FileTransferPhase.Failed,
                operations.Count,
                completedItems: 0));
            throw;
        }

        bool shellReportedFailure =
            outcome.PerformHResult < 0 ||
            outcome.FinishHResult < 0 ||
            outcome.FailedItemCount > 0;
        bool shellCanceled =
            outcome.Aborted ||
            outcome.PerformHResult == ErrorCancelledHResult ||
            outcome.FinishHResult == ErrorCancelledHResult ||
            cancellationToken.IsCancellationRequested;
        IReadOnlyList<FileTransferResult> completedResults =
            ReconcileShellTransferResults(
                operations,
                outcome.CompletedResults,
                move,
                allowSuccessfulBatchFallback:
                    !shellCanceled && !shellReportedFailure);

        App.Log(
            $"[FileTransfer] Windows shell transfer returned " +
            $"count={operations.Count} completed={completedResults.Count} " +
            $"move={move} aborted={outcome.Aborted} " +
            $"failedItems={outcome.FailedItemCount} " +
            $"performHr=0x{outcome.PerformHResult:X8} " +
            $"finishHr=0x{outcome.FinishHResult:X8} " +
            $"elapsedMs={stopwatch.ElapsedMilliseconds}");

        if (shellCanceled)
        {
            progress?.Report(CreateShellProgress(
                FileTransferPhase.Canceled,
                operations.Count,
                completedResults.Count));
            throw new FileTransferCanceledException(
                completedResults,
                cancellationToken);
        }

        if (shellReportedFailure || completedResults.Count != operations.Count)
        {
            progress?.Report(CreateShellProgress(
                FileTransferPhase.Failed,
                operations.Count,
                completedResults.Count));
            Exception? innerException = GetShellFailureException(outcome);
            throw new FileTransferPartialFailureException(
                completedResults,
                innerException);
        }

        progress?.Report(CreateShellProgress(
            FileTransferPhase.Completed,
            operations.Count,
            completedResults.Count));
        return completedResults;
    }

    private static FileTransferProgress CreateShellProgress(
        FileTransferPhase phase,
        int totalItems,
        int completedItems)
    {
        return new FileTransferProgress(
            phase,
            CurrentItemName: null,
            completedItems,
            totalItems,
            BytesTransferred: 0,
            TotalBytes: null,
            BytesPerSecond: null,
            EstimatedRemaining: null);
    }

    private static Exception? GetShellFailureException(
        ShellTransferOutcome outcome)
    {
        int hresult = outcome.PerformHResult < 0
            ? outcome.PerformHResult
            : outcome.FinishHResult < 0
                ? outcome.FinishHResult
                : outcome.FirstFailedItemHResult;
        return hresult < 0 && hresult != ErrorCancelledHResult
            ? Marshal.GetExceptionForHR(hresult)
            : null;
    }

    private static IReadOnlyList<FileTransferResult>
        ReconcileShellTransferResults(
            IReadOnlyList<TransferOperation> operations,
            IReadOnlyList<FileTransferResult> reportedResults,
            bool move,
            bool allowSuccessfulBatchFallback)
    {
        var results = new Dictionary<string, FileTransferResult>(
            StringComparer.OrdinalIgnoreCase);
        foreach (FileTransferResult result in reportedResults)
        {
            if (move && !IsCompletedShellMove(result.SourcePath, result.DestinationPath)) continue;
            results[result.SourcePath] = result;
        }

        foreach (TransferOperation operation in operations)
        {
            if (results.ContainsKey(operation.SourcePath))
            {
                continue;
            }

            bool completed = move
                ? IsCompletedShellMove(
                    operation.SourcePath,
                    operation.DestinationPath)
                : allowSuccessfulBatchFallback &&
                  IsCompletedShellCopy(
                      operation.SourcePath,
                      operation.DestinationPath);
            if (completed)
            {
                results[operation.SourcePath] = new FileTransferResult(
                    operation.SourcePath,
                    operation.DestinationPath);
            }
        }

        return operations
            .Where(operation => results.ContainsKey(operation.SourcePath))
            .Select(operation => results[operation.SourcePath])
            .ToArray();
    }

    internal static bool IsCompletedShellCopy(
        string sourcePath,
        string destinationPath)
    {
        if (File.Exists(sourcePath) && File.Exists(destinationPath))
        {
            try
            {
                return new FileInfo(sourcePath).Length ==
                       new FileInfo(destinationPath).Length;
            }
            catch (Exception ex) when (
                ex is IOException or UnauthorizedAccessException)
            {
                return false;
            }
        }

        return Directory.Exists(sourcePath) &&
               Directory.Exists(destinationPath);
    }

    private static async Task<ShellTransferOutcome> RunShellTransferOnStaThreadAsync(
        IReadOnlyList<TransferOperation> operations,
        bool move,
        IntPtr ownerWindowHandle,
        CancellationToken cancellationToken,
        bool keepBoth,
        Action<FileTransferResult>? itemCompleted)
    {
        // Interactive by construction: every caller of the modern Shell
        // engine is a user-driven transfer with an owner window.
        var watchdog = new StaOperationWatchdog(
            InteractiveShellTransferInactivityTimeout);
        StaOperationResult<ShellTransferOutcome> result =
            await s_shellTransferRunner.RunAsync(
                () => ExecuteShellTransferOnCurrentThread(
                    operations,
                    move,
                    ownerWindowHandle,
                    cancellationToken,
                    keepBoth,
                    itemCompleted,
                    watchdog),
                cancellationToken,
                watchdog).ConfigureAwait(false);
        if (!result.Started)
        {
            // Admission or queue timeout: nothing ran, nothing completed.
            // Retryable by the user once the in-flight transfers drain.
            throw new FileTransferPartialFailureException(
                [],
                new IOException(
                    "The Windows file operation engine was busy; the " +
                    "transfer was not started (queueWaitMs=" +
                    $"{(long)result.QueueWait.TotalMilliseconds})."));
        }

        return result.Value!;
    }

    private static ShellTransferOutcome ExecuteShellTransferOnCurrentThread(
        IReadOnlyList<TransferOperation> operations,
        bool move,
        IntPtr ownerWindowHandle,
        CancellationToken cancellationToken,
        bool keepBoth,
        Action<FileTransferResult>? itemCompleted,
        StaOperationWatchdog watchdog)
    {
        cancellationToken.ThrowIfCancellationRequested();
        int initializeResult = FileOperationNativeMethods.CoInitializeEx(
            IntPtr.Zero,
            CoInitApartmentThreaded);
        ThrowForShellHResult(initializeResult, cancellationToken);
        bool uninitialize = initializeResult >= 0;
        IFileOperationNative? fileOperation = null;
        var retainedShellItems = new List<IntPtr>(operations.Count * 2);
        uint adviseCookie = 0;
        var sink = new ShellFileOperationProgressSink(
            operations,
            cancellationToken, itemCompleted, move, watchdog);
        try
        {
            Guid classId = s_fileOperationClassId;
            Guid interfaceId = s_fileOperationInterfaceId;
            ThrowForShellHResult(
                FileOperationNativeMethods.CoCreateInstance(
                    ref classId,
                    IntPtr.Zero,
                    ClsContextInProcessServer,
                    ref interfaceId,
                    out IntPtr operationPointer),
                cancellationToken);

            fileOperation = new IFileOperationNative(operationPointer);
            ThrowForShellHResult(
                fileOperation.Advise(sink, out adviseCookie),
                cancellationToken);
            // Staging is progress too: slow Shell item creation (network
            // paths, offline shells) must keep the watchdog calm.
            watchdog.Pulse();
            ThrowForShellHResult(
                fileOperation.SetOperationFlags(
                    ShellFileOperationNoConfirmMakeDirectory |
                    (keepBoth ? 0x00240008u : 0u)),
                cancellationToken);
            if (ownerWindowHandle != IntPtr.Zero)
            {
                ThrowForShellHResult(
                    fileOperation.SetOwnerWindow(ownerWindowHandle),
                    cancellationToken);
            }

            foreach (TransferOperation operation in operations)
            {
                cancellationToken.ThrowIfCancellationRequested();
                IntPtr sourceItem = CreateShellItem(
                    operation.SourcePath,
                    cancellationToken);
                IntPtr destinationFolderItem = CreateShellItem(
                    Path.GetDirectoryName(operation.DestinationPath)!,
                    cancellationToken);
                retainedShellItems.Add(sourceItem);
                retainedShellItems.Add(destinationFolderItem);
                string destinationName = Path.GetFileName(
                    operation.DestinationPath);
                int queueResult = move
                    ? fileOperation.MoveItem(
                        sourceItem,
                        destinationFolderItem,
                        destinationName,
                        IntPtr.Zero)
                    : fileOperation.CopyItem(
                        sourceItem,
                        destinationFolderItem,
                        destinationName,
                        IntPtr.Zero);
                ThrowForShellHResult(queueResult, cancellationToken);
                watchdog.Pulse();
            }

            watchdog.Pulse();
            int performResult = fileOperation.PerformOperations();
            int abortedResult = fileOperation.GetAnyOperationsAborted(
                out bool aborted);
            if (abortedResult < 0 && performResult >= 0)
            {
                performResult = abortedResult;
            }

            return new ShellTransferOutcome(
                sink.GetCompletedResults(),
                aborted,
                performResult,
                sink.FinishHResult,
                sink.FailedItemCount,
                sink.FirstFailedItemHResult);
        }
        finally
        {
            if (fileOperation is not null && adviseCookie != 0)
            {
                try
                {
                    _ = fileOperation.Unadvise(adviseCookie);
                }
                catch
                {
                }
            }

            foreach (IntPtr shellItem in retainedShellItems)
            {
                Marshal.Release(shellItem);
            }

            if (fileOperation is not null)
            {
                fileOperation.Dispose();
            }

            GC.KeepAlive(sink);
            if (uninitialize)
            {
                FileOperationNativeMethods.CoUninitialize();
            }
        }
    }

    private static IntPtr CreateShellItem(
        string path,
        CancellationToken cancellationToken)
    {
        Guid interfaceId = s_shellItemInterfaceId;
        ThrowForShellHResult(
            FileOperationNativeMethods.SHCreateItemFromParsingName(
                path,
                IntPtr.Zero,
                ref interfaceId,
                out IntPtr shellItem),
            cancellationToken);
        return shellItem;
    }

    private static void ThrowForShellHResult(
        int hresult,
        CancellationToken cancellationToken)
    {
        if (hresult >= 0)
        {
            return;
        }

        if (hresult == ErrorCancelledHResult ||
            cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }

        Marshal.ThrowExceptionForHR(hresult);
    }

    private static string? TryGetShellItemPath(IntPtr item)
    {
        if (item == IntPtr.Zero)
        {
            return null;
        }

        IntPtr pathPointer = IntPtr.Zero;
        try
        {
            int result = IFileOperationNative.GetDisplayName(item,
                ShellDisplayNameFileSystemPath,
                out pathPointer);
            return result >= 0 && pathPointer != IntPtr.Zero
                ? Marshal.PtrToStringUni(pathPointer)
                : null;
        }
        catch
        {
            return null;
        }
        finally
        {
            if (pathPointer != IntPtr.Zero)
            {
                FileOperationNativeMethods.CoTaskMemFree(pathPointer);
            }
        }
    }

    private sealed record ShellTransferOutcome(
        IReadOnlyList<FileTransferResult> CompletedResults,
        bool Aborted,
        int PerformHResult,
        int FinishHResult,
        int FailedItemCount,
        int FirstFailedItemHResult);

    [GeneratedComClass]
    private sealed partial class ShellFileOperationProgressSink :
        IFileOperationProgressSinkNative
    {
        private const int SuccessHResult = 0;
        private readonly IReadOnlyList<TransferOperation> _operations;
        private readonly CancellationToken _cancellationToken;
        private readonly Action<FileTransferResult>? _itemCompleted;
        private readonly StaOperationWatchdog? _watchdog;
        private int _receiptError;
        private readonly bool _move;
        private readonly Dictionary<string, FileTransferResult> _completed =
            new(StringComparer.OrdinalIgnoreCase);

        internal ShellFileOperationProgressSink(
            IReadOnlyList<TransferOperation> operations,
            CancellationToken cancellationToken,
            Action<FileTransferResult>? itemCompleted,
            bool move,
            StaOperationWatchdog? watchdog = null)
        {
            _move = move;
            _itemCompleted = itemCompleted;
            _operations = operations;
            _cancellationToken = cancellationToken;
            _watchdog = watchdog;
        }

        internal int FinishHResult { get; private set; }

        internal int FailedItemCount { get; private set; }

        internal int FirstFailedItemHResult { get; private set; }

        internal IReadOnlyList<FileTransferResult> GetCompletedResults()
        {
            return _operations
                .Where(operation => _completed.ContainsKey(
                    operation.SourcePath))
                .Select(operation => _completed[operation.SourcePath])
                .ToArray();
        }

        // Every Shell callback is observable progress: it renews the
        // watchdog so a legitimate minute-level transfer is never mistaken
        // for the wedged state of feedback 455.
        private void Pulse() => _watchdog?.Pulse();

        public int StartOperations()
        {
            Pulse();
            return CancellationResult();
        }

        public int FinishOperations(int result)
        {
            Pulse();
            FinishHResult = result;
            return SuccessHResult;
        }

        public int PreRenameItem(
            uint flags,
            IntPtr item,
            IntPtr newName)
        {
            Pulse();
            return CancellationResult();
        }

        public int PostRenameItem(
            uint flags,
            IntPtr item,
            IntPtr newName,
            int renameResult,
            IntPtr newlyCreatedItem)
        {
            Pulse();
            return SuccessHResult;
        }

        public int PreMoveItem(
            uint flags,
            IntPtr item,
            IntPtr destinationFolder,
            IntPtr newName)
        {
            Pulse();
            return CancellationResult();
        }

        public int PostMoveItem(
            uint flags,
            IntPtr item,
            IntPtr destinationFolder,
            IntPtr newName,
            int moveResult,
            IntPtr newlyCreatedItem)
        {
            Pulse();
            RecordTransferResult(item, newlyCreatedItem, moveResult);
            return CancellationResult();
        }

        public int PreCopyItem(
            uint flags,
            IntPtr item,
            IntPtr destinationFolder,
            IntPtr newName)
        {
            Pulse();
            return CancellationResult();
        }

        public int PostCopyItem(
            uint flags,
            IntPtr item,
            IntPtr destinationFolder,
            IntPtr newName,
            int copyResult,
            IntPtr newlyCreatedItem)
        {
            Pulse();
            RecordTransferResult(item, newlyCreatedItem, copyResult);
            return CancellationResult();
        }

        public int PreDeleteItem(uint flags, IntPtr item)
        {
            Pulse();
            return CancellationResult();
        }

        public int PostDeleteItem(
            uint flags,
            IntPtr item,
            int deleteResult,
            IntPtr newlyCreatedItem)
        {
            Pulse();
            return SuccessHResult;
        }

        public int PreNewItem(
            uint flags,
            IntPtr destinationFolder,
            IntPtr newName)
        {
            Pulse();
            return CancellationResult();
        }

        public int PostNewItem(
            uint flags,
            IntPtr destinationFolder,
            IntPtr newName,
            IntPtr templateName,
            uint fileAttributes,
            int newItemResult,
            IntPtr newItem)
        {
            Pulse();
            return SuccessHResult;
        }

        public int UpdateProgress(uint totalWork, uint completedWork)
        {
            Pulse();
            return CancellationResult();
        }

        public int ResetTimer()
        {
            Pulse();
            return SuccessHResult;
        }

        public int PauseTimer()
        {
            Pulse();
            return SuccessHResult;
        }

        public int ResumeTimer()
        {
            Pulse();
            return SuccessHResult;
        }

        private int CancellationResult()
        {
            return _receiptError != 0 ? _receiptError : _cancellationToken.IsCancellationRequested
                ? ErrorCancelledHResult
                : SuccessHResult;
        }

        private void RecordTransferResult(
            IntPtr sourceItem,
            IntPtr newlyCreatedItem,
            int operationResult)
        {
            if (operationResult < 0)
            {
                FailedItemCount++;
                if (FirstFailedItemHResult == 0)
                {
                    FirstFailedItemHResult = operationResult;
                }

                return;
            }

            string? sourcePath = TryGetShellItemPath(sourceItem);
            string? destinationPath = TryGetShellItemPath(newlyCreatedItem);
            TransferOperation? operation = sourcePath is null
                ? null
                : _operations.FirstOrDefault(candidate =>
                    string.Equals(
                        candidate.SourcePath,
                        sourcePath,
                        StringComparison.OrdinalIgnoreCase));
            if (operation is null && destinationPath is not null)
            {
                operation = _operations.FirstOrDefault(candidate =>
                    string.Equals(
                        candidate.DestinationPath,
                        destinationPath,
                        StringComparison.OrdinalIgnoreCase));
            }

            if (operation is null || destinationPath is null ||
                (_move && !IsCompletedShellMove(operation.SourcePath, destinationPath)))
            {
                return;
            }

            _completed[operation.SourcePath] = new FileTransferResult(
                operation.SourcePath,
                destinationPath ?? operation.DestinationPath);
            try
            {
                _itemCompleted?.Invoke(_completed[operation.SourcePath]);
            }
            catch (Exception ex)
            {
                // Never let a managed exception escape a COM callback. Stop
                // before another item if the durable receipt cannot be saved.
                _receiptError = ex.HResult < 0 ? ex.HResult : unchecked((int)0x80004005);
                FailedItemCount++;
                FirstFailedItemHResult = _receiptError;
            }
        }
    }

    [GeneratedComInterface(Options = ComInterfaceOptions.ManagedObjectWrapper)]
    [Guid("04B0F1A7-9490-44BC-96E1-4296A31252E2")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal partial interface IFileOperationProgressSinkNative
    {
        [PreserveSig]
        int StartOperations();

        [PreserveSig]
        int FinishOperations(int result);

        [PreserveSig]
        int PreRenameItem(
            uint flags,
            IntPtr item,
            IntPtr newName);

        [PreserveSig]
        int PostRenameItem(
            uint flags,
            IntPtr item,
            IntPtr newName,
            int renameResult,
            IntPtr newlyCreatedItem);

        [PreserveSig]
        int PreMoveItem(
            uint flags,
            IntPtr item,
            IntPtr destinationFolder,
            IntPtr newName);

        [PreserveSig]
        int PostMoveItem(
            uint flags,
            IntPtr item,
            IntPtr destinationFolder,
            IntPtr newName,
            int moveResult,
            IntPtr newlyCreatedItem);

        [PreserveSig]
        int PreCopyItem(
            uint flags,
            IntPtr item,
            IntPtr destinationFolder,
            IntPtr newName);

        [PreserveSig]
        int PostCopyItem(
            uint flags,
            IntPtr item,
            IntPtr destinationFolder,
            IntPtr newName,
            int copyResult,
            IntPtr newlyCreatedItem);

        [PreserveSig]
        int PreDeleteItem(uint flags, IntPtr item);

        [PreserveSig]
        int PostDeleteItem(
            uint flags,
            IntPtr item,
            int deleteResult,
            IntPtr newlyCreatedItem);

        [PreserveSig]
        int PreNewItem(
            uint flags,
            IntPtr destinationFolder,
            IntPtr newName);

        [PreserveSig]
        int PostNewItem(
            uint flags,
            IntPtr destinationFolder,
            IntPtr newName,
            IntPtr templateName,
            uint fileAttributes,
            int newItemResult,
            IntPtr newItem);

        [PreserveSig]
        int UpdateProgress(uint totalWork, uint completedWork);

        [PreserveSig]
        int ResetTimer();

        [PreserveSig]
        int PauseTimer();

        [PreserveSig]
        int ResumeTimer();
    }

    // A small owned native wrapper avoids RCWs and works in Native AOT too.
    private sealed unsafe class IFileOperationNative(IntPtr pointer) : IDisposable
    {
        private IntPtr _sinkPointer;
        private void** Table => *(void***)pointer;
        public int Advise(IFileOperationProgressSinkNative sink, out uint cookie)
        {
            _sinkPointer = (IntPtr)ComInterfaceMarshaller<IFileOperationProgressSinkNative>.ConvertToUnmanaged(sink);
            fixed (uint* value = &cookie)
                return ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint*, int>)Table[3])(pointer, _sinkPointer, value);
        }
        public int Unadvise(uint cookie) => ((delegate* unmanaged[Stdcall]<IntPtr, uint, int>)Table[4])(pointer, cookie);
        public int SetOperationFlags(uint flags) => ((delegate* unmanaged[Stdcall]<IntPtr, uint, int>)Table[5])(pointer, flags);
        public int SetOwnerWindow(IntPtr owner) => ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int>)Table[9])(pointer, owner);
        public int MoveItem(IntPtr source, IntPtr folder, string name, IntPtr sink) => Queue(14, source, folder, name, sink);
        public int CopyItem(IntPtr source, IntPtr folder, string name, IntPtr sink) => Queue(16, source, folder, name, sink);
        private int Queue(int slot, IntPtr source, IntPtr folder, string name, IntPtr sink)
        {
            fixed (char* text = name)
                return ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr, char*, IntPtr, int>)Table[slot])(pointer, source, folder, text, sink);
        }
        public int PerformOperations() => ((delegate* unmanaged[Stdcall]<IntPtr, int>)Table[21])(pointer);
        public int GetAnyOperationsAborted(out bool aborted)
        {
            int value = 0;
            int hr = ((delegate* unmanaged[Stdcall]<IntPtr, int*, int>)Table[22])(pointer, &value);
            aborted = value != 0;
            return hr;
        }
        public static int GetDisplayName(IntPtr item, uint kind, out IntPtr name)
        {
            fixed (IntPtr* value = &name)
                return ((delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr*, int>)(*(void***)item)[5])(item, kind, value);
        }
        public void Dispose()
        {
            Marshal.Release(pointer);
            if (_sinkPointer != IntPtr.Zero)
                ComInterfaceMarshaller<IFileOperationProgressSinkNative>.Free((void*)_sinkPointer);
        }
    }

    // ole32/shell32 COM entry points live in
    // DeskBox.Platform.FileOperationNativeMethods.
}
