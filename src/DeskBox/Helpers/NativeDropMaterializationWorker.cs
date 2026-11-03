using System.Collections.Concurrent;
using DeskBox.Platform;
using DeskBox.Services;

namespace DeskBox.Helpers;

/// <summary>
/// The dedicated MTA thread that materializes deferred drops after the OLE
/// drop callback has returned. One long-lived thread, one job at a time:
/// every COM call for a job (unmarshaling, extraction — virtual FILECONTENTS
/// streams or shell-application PIDLs — the performed-effect receipt,
/// EndOperation) happens on this single thread, so apartment affinity of the
/// stream proxies can never be violated (the classic
/// GetData-on-one-thread / Read-on-another deadlock). Marshal-to-worker is
/// the official Shell asynchronous-drop protocol (IDataObjectAsyncCapability);
/// the plain AddRef variant covers sources without the capability — the
/// remote stub manager keeps the source object alive while the marshaled
/// reference exists (MS-DCOM).
/// The queue is bounded: a hung drag source can pin this thread inside one
/// IStream read, and an unbounded queue would let every further drop be
/// optimistically receipted and then silently pile up behind it. When the
/// queue is full the drop is refused synchronously instead.
/// </summary>
internal static class NativeDropMaterializationWorker
{
    private const int MaxQueuedJobs = 8;

    private sealed class Job
    {
        public required nint MarshalStream { get; init; }

        /// <summary>True when StartOperation was called on the UI thread
        /// before OnDrop returned; the matching EndOperation then runs here
        /// after extraction.</summary>
        public required bool AsyncOperationStarted { get; init; }

        /// <summary>The completion effect decided optimistically at drop
        /// time; it is committed here (performed-effect receipt +
        /// EndOperation) once extraction actually succeeded, or downgraded
        /// to none when it did not.</summary>
        public required uint CompletionEffect { get; init; }

        public required uint LogicalEffect { get; init; }

        public required Action<IReadOnlyList<string>, IReadOnlyList<DroppedItemOriginKind>> OnCompleted { get; init; }
    }

    private static readonly BlockingCollection<Job> PendingJobs =
        new(new ConcurrentQueue<Job>(), MaxQueuedJobs);

    private static Thread? _worker;
    private static readonly object StartupGate = new();

    /// <summary>Checked before the marshal packet is created: a job that can
    /// never run must never pin the source object with an unconsumed marshal
    /// stream. Only the UI thread enqueues, so this check is exact.</summary>
    public static bool CanAcceptJob => PendingJobs.Count < MaxQueuedJobs;

    /// <summary>False when the queue is full: the caller must refuse the
    /// drop synchronously (none receipt, EndOperation if negotiated).
    /// Virtual-file jobs only — shell-application extraction stays on the
    /// UI STA because its shell namespace APIs fail on the MTA worker
    /// (2026-10-15 field incident).</summary>
    public static bool TryEnqueue(
        nint marshalStream,
        bool asyncOperationStarted,
        uint completionEffect,
        uint logicalEffect,
        Action<IReadOnlyList<string>, IReadOnlyList<DroppedItemOriginKind>> onCompleted)
    {
        var job = new Job
        {
            MarshalStream = marshalStream,
            AsyncOperationStarted = asyncOperationStarted,
            CompletionEffect = completionEffect,
            LogicalEffect = logicalEffect,
            OnCompleted = onCompleted,
        };

        lock (StartupGate)
        {
            _worker ??= CreateWorkerThread();
        }

        return PendingJobs.TryAdd(job);
    }

    /// <summary>
    /// Deletes materialized drop directories, keeping only paths under the
    /// DeskBox VirtualDrops temp root. Shared by the launch path and by the
    /// refusal branches that must not orphan materialized files.
    /// </summary>
    internal static void CleanupVirtualDropDirectories(IReadOnlyList<string> paths)
    {
        string temporaryRoot = Path.GetFullPath(Path.Combine(
                Path.GetTempPath(),
                "DeskBox",
                "VirtualDrops"))
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
            Path.DirectorySeparatorChar;

        foreach (string directory in paths
                     .Select(Path.GetDirectoryName)
                     .Where(directory => !string.IsNullOrWhiteSpace(directory))
                     .Select(directory => Path.GetFullPath(directory!))
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            string normalizedDirectory = directory
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
                Path.DirectorySeparatorChar;
            if (!normalizedDirectory.StartsWith(
                    temporaryRoot,
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            try
            {
                if (Directory.Exists(directory))
                {
                    Directory.Delete(directory, recursive: true);
                }
            }
            catch (Exception ex)
            {
                App.Log(
                    $"[DropTarget] Failed to clean grouped virtual drop directory: {ex.Message}");
            }
        }
    }

    private static Thread CreateWorkerThread()
    {
        var thread = new Thread(RunWorkerLoop)
        {
            Name = "DeskBox.NativeDropMaterialization",
            IsBackground = true,
        };
        thread.Start();
        return thread;
    }

    private static void RunWorkerLoop()
    {
        // One MTA initialization for the lifetime of the thread: outbound
        // calls from the MTA need no message pump. CoUninitialize is only
        // paired when initialization actually succeeded.
        int initHr = Win32Helper.CoInitializeEx(
            IntPtr.Zero,
            Win32Helper.CoinitMultithreaded);
        try
        {
            foreach (Job job in PendingJobs.GetConsumingEnumerable())
            {
                RunJob(job);
            }
        }
        catch
        {
            // The consuming enumerable only ends if the collection is marked
            // complete, which never happens before process exit; any job
            // exception is caught inside RunJob.
        }
        finally
        {
            if (initHr >= 0)
            {
                Win32Helper.CoUninitialize();
            }
        }
    }

    private static void RunJob(Job job)
    {
        IReadOnlyList<string> extractedPaths = [];
        IReadOnlyList<DroppedItemOriginKind> itemOrigins = [];
        uint finalEffect = NativeDropEffectPolicy.None;
        int operationResult = unchecked((int)0x80004005); // E_UNEXPECTED

        nint dataObject = 0;
        try
        {
            int hr = Win32Helper.CoGetInterfaceAndReleaseStream(
                job.MarshalStream,
                in Win32Helper.IDataObjectComInterfaceId,
                out dataObject);
            if (hr < 0 || dataObject == 0)
            {
                App.Log(
                    "[DropTarget] Deferred-drop worker could not unmarshal " +
                    $"the data object hr=0x{hr:X8}");
                if (job.AsyncOperationStarted)
                {
                    // The marshal stream is consumed, so the object is
                    // unreachable and EndOperation can never be sent: an
                    // async source will wait this out against its own
                    // timeout. Loud on purpose — this is the only silent
                    // dead-end left in the protocol chain.
                    App.Log(
                        "[DropTarget] Async operation was started but the " +
                        "marshal stream is dead; EndOperation cannot be " +
                        "delivered.");
                }
            }
            else
            {
                extractedPaths = NativeDropTarget.ExtractDeferredWorkerFilesWithOrigins(
                    new NativeOleDataObject(dataObject),
                    out itemOrigins);
                if (extractedPaths.Count > 0)
                {
                    finalEffect = job.CompletionEffect;
                    operationResult = 0; // S_OK
                }
                else
                {
                    App.Log(
                        "[DropTarget] Deferred-drop worker extracted no files; " +
                        "the import will not run.");
                }

                // The receipt belongs on this thread (the proxy's apartment)
                // and, per the async protocol, after the operation finished —
                // never before OnDrop returned. Order matters: the source
                // reads the receipt after EndOperation.
                NativeDropTarget.WritePerformedDropEffect(dataObject, finalEffect);
                NativeDropTarget.WriteLogicalPerformedDropEffect(
                    dataObject,
                    finalEffect != NativeDropEffectPolicy.None
                        ? job.LogicalEffect
                        : NativeDropEffectPolicy.None);
            }
        }
        catch (Exception ex)
        {
            App.Log($"[DropTarget] Deferred-drop worker failed: {ex.Message}");
            // The failure must propagate to BOTH channels the source reads:
            // the receipt below and EndOperation in the finally. Leaving the
            // success values in place would tell a move-semantic source
            // S_OK+effect while the receipt says none.
            operationResult = unchecked((int)0x80004005); // E_UNEXPECTED
            finalEffect = NativeDropEffectPolicy.None;
            try
            {
                if (dataObject != 0)
                {
                    NativeDropTarget.WritePerformedDropEffect(
                        dataObject,
                        NativeDropEffectPolicy.None);
                    NativeDropTarget.WriteLogicalPerformedDropEffect(
                        dataObject,
                        NativeDropEffectPolicy.None);
                }
            }
            catch
            {
                // Best effort; the source will keep its files when no receipt
                // arrives at all.
            }
        }
        finally
        {
            if (dataObject != 0)
            {
                if (job.AsyncOperationStarted &&
                    NativeDropAsyncCapability.TryQuery(dataObject, out NativeDropAsyncCapability capability))
                {
                    try
                    {
                        _ = capability.EndOperation(operationResult, finalEffect);
                    }
                    catch
                    {
                        // The source may already be gone; nothing to recover.
                    }

                    _ = capability.Release();
                }

                // CoGetInterfaceAndReleaseStream returned an add-ref'd
                // pointer; release exactly that reference.
                _ = ReleaseDataObject(dataObject);
            }
        }

        UiDispatch.PostAfterCallback(() => job.OnCompleted(extractedPaths, itemOrigins));
    }

    private static unsafe uint ReleaseDataObject(nint pointer)
    {
        nint* vtable = *(nint**)pointer;
        var release = (delegate* unmanaged[Stdcall]<nint, uint>)vtable[2];
        return release(pointer);
    }
}
