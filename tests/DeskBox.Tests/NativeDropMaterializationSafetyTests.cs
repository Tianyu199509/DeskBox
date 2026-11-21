using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using DeskBox.Controls;
using DeskBox.Helpers;
using DeskBox.Platform;
using DeskBox.Services;
using DeskBox.Services.DragDrop;
using Microsoft.UI.Dispatching;

namespace DeskBox.Tests;

public sealed class NativeDropMaterializationSafetyTests : IDisposable
{
    private readonly string _sandbox;

    public NativeDropMaterializationSafetyTests()
    {
        UiDispatch.ResetForTests();
        _sandbox = Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), "DeskBox.Tests", "MatSafety_" + Guid.NewGuid().ToString("N"))).FullName;
    }

    public void Dispose()
    {
        UiDispatch.ResetForTests();
        try
        {
            if (Directory.Exists(_sandbox))
            {
                Directory.Delete(_sandbox, recursive: true);
            }
        }
        catch
        {
        }
    }

    [Fact]
    public async Task ConfirmAcceptanceBeforeReceipt_LateUiCallbackAfterTimeoutDoesNotCrash()
    {
        DispatcherQueueController controller = DispatcherQueueController.CreateOnDedicatedThread();
        var uiEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var releaseUi = new ManualResetEventSlim(false);
        var uiFinishedWithoutException = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        try
        {
            var ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            Assert.True(controller.DispatcherQueue.TryEnqueue(() =>
            {
                UiDispatch.MarkReady(controller.DispatcherQueue);
                ready.TrySetResult(true);
            }));
            await ready.Task.WaitAsync(TimeSpan.FromSeconds(5));

            // Run ConfirmAcceptanceBeforeReceipt on a background worker thread (just like the MTA worker)
            // with a short timeout while the UI callback is still blocked; the late UI callback must
            // complete cleanly without throwing ObjectDisposedException.
            bool accepted = await Task.Run(() =>
                NativeDropMaterializationWorker.ConfirmAcceptanceBeforeReceipt(
                    () =>
                    {
                        uiEntered.TrySetResult(true);
                        releaseUi.Wait(TimeSpan.FromSeconds(5));
                        uiFinishedWithoutException.TrySetResult(true);
                        return true;
                    },
                    timeoutMs: 30));

            Assert.False(accepted);
            await uiEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            releaseUi.Set();
            Assert.True(await uiFinishedWithoutException.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        }
        finally
        {
            releaseUi.Set();
            UiDispatch.ResetForTests();
            await controller.ShutdownQueueAsync();
        }
    }

    [Fact]
    public void StreamReader_ThrowsTypedBudgetExceededEvenWhenBudgetIsNotBufferAligned()
    {
        // 100_000 is intentionally not a multiple of NativeComStreamReader's 81_920 buffer size
        // (mirroring the historical 256MB % 80KB == 65,536 modulo bug).
        const long nonAlignedBudget = 100_000;
        byte[] payload = new byte[120_000];
        Random.Shared.NextBytes(payload);
        int offset = 0;

        using var fake = new FakeComStream(slotCount: 4);
        ReadCallback readCb = (nint _, nint buffer, uint bufferSize, nint bytesRead) =>
        {
            int count = (int)Math.Min(bufferSize, payload.Length - offset);
            if (count > 0)
            {
                Marshal.Copy(payload, offset, buffer, count);
                offset += count;
            }

            Marshal.WriteInt32(bytesRead, count);
            return count == 0 ? 1 : 0;
        };
        fake.SetSlot(3, readCb);

        using var destination = new MemoryStream();
        VirtualDropBudgetExceededException ex = Assert.Throws<VirtualDropBudgetExceededException>(() =>
            NativeComStreamReader.CopyTo(fake.Pointer, destination, nonAlignedBudget));
        Assert.Equal(nonAlignedBudget, ex.MaxBytes);
    }

    [Fact]
    public void StreamReader_RewindsSeekableIStreamAndDetectsDeclaredSizeTruncation()
    {
        byte[] payload = "0123456789ABCDEF"u8.ToArray();
        int offset = 7; // Simulate a source that handed out a stream positioned mid-stream!
        int seekCalls = 0;
        Guid iidIStream = new("0000000c-0000-0000-c000-000000000046");

        using var fake = new FakeComStream(slotCount: 6);
        QueryInterfaceCallback qiCb = (nint self, ref Guid riid, out nint ppv) =>
        {
            if (riid == iidIStream)
            {
                ppv = self;
                return 0;
            }

            ppv = 0;
            return unchecked((int)0x80004002);
        };
        ReleaseCallback releaseCb = _ => 1u;
        ReadCallback readCb = (nint _, nint buffer, uint bufferSize, nint bytesRead) =>
        {
            // Return 4-byte chunks with S_FALSE on non-final chunks to test chunked S_FALSE handling.
            int count = Math.Min(4, payload.Length - offset);
            if (count > 0)
            {
                Marshal.Copy(payload, offset, buffer, count);
                offset += count;
            }

            Marshal.WriteInt32(bytesRead, count);
            return 1; // S_FALSE even before EOF
        };
        SeekCallback seekCb = (nint _, long move, uint origin, out ulong newPos) =>
        {
            seekCalls++;
            if (origin == 0 && move == 0)
            {
                offset = 0;
            }

            newPos = (ulong)offset;
            return 0;
        };
        fake.SetSlot(0, qiCb);
        fake.SetSlot(2, releaseCb);
        fake.SetSlot(3, readCb);
        fake.SetSlot(5, seekCb);

        using var completeDestination = new MemoryStream();
        NativeComStreamReader.CopyTo(fake.Pointer, completeDestination, maxBytes: 1024, declaredFileSize: payload.Length);
        Assert.Equal(1, seekCalls);
        Assert.Equal(payload, completeDestination.ToArray());

        // When declaredFileSize exceeds actual stream bytes, CopyTo throws IOException (no silent truncation).
        using var truncatedDestination = new MemoryStream();
        IOException truncated = Assert.Throws<IOException>(() =>
            NativeComStreamReader.CopyTo(fake.Pointer, truncatedDestination, maxBytes: 1024, declaredFileSize: payload.Length + 10));
        Assert.Contains("prematurely", truncated.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DropStagingBudgetPolicy_ComputesSpaceAwareLimitsAndHonorsExplicitOverrides()
    {
        const long gb = 1024L * 1024 * 1024;
        const long mb = 1024L * 1024;

        // Spacious drive: reserves 512 MB, allowing multi-GB drops well above old 256MB/512MB caps.
        Assert.Equal(
            (10L * gb) - DropStagingBudgetPolicy.SafetyReserveBytes,
            DropStagingBudgetPolicy.ResolveDynamicByteBudget(_sandbox, availableFreeSpaceOverride: 10L * gb));

        // Low-space drive (<= 512 MB): keeps 64 MB critical reserve.
        Assert.Equal(
            (200L * mb) - DropStagingBudgetPolicy.CriticalReserveBytes,
            DropStagingBudgetPolicy.ResolveDynamicByteBudget(_sandbox, availableFreeSpaceOverride: 200L * mb));

        // Critically full drive (<= 64 MB): refuses staging cleanly (0 bytes).
        Assert.Equal(
            0L,
            DropStagingBudgetPolicy.ResolveDynamicByteBudget(_sandbox, availableFreeSpaceOverride: 32L * mb));

        // Explicit limit override wins over free space.
        Assert.Equal(
            4096L,
            DropStagingBudgetPolicy.ResolveDynamicByteBudget(_sandbox, availableFreeSpaceOverride: 10L * gb, explicitLimitBytes: 4096L));
    }

    [Fact]
    public void StageExternalTempDropPathsWithOrigins_ReportsAccurateSkippedCountOnPartialAndTotalFailure()
    {
        string tempArchiveDir = Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), "7zO_" + Guid.NewGuid().ToString("N")[..8])).FullName;
        IReadOnlyList<string> stagedPartial = [];
        try
        {
            string ok1 = Path.Combine(tempArchiveDir, "a.txt");
            string oversized = Path.Combine(tempArchiveDir, "huge.bin");
            string ok2 = Path.Combine(tempArchiveDir, "b.txt");
            File.WriteAllBytes(ok1, new byte[128]);
            File.WriteAllBytes(oversized, new byte[4096]);
            File.WriteAllBytes(ok2, new byte[128]);

            stagedPartial = DropStagingFileOps.StageExternalTempDropPathsWithOrigins(
                [ok1, oversized, ok2],
                hasShellIdList: false,
                out _,
                out bool stagedAny,
                out int skippedPartial,
                maxTotalBytes: 1024);
            Assert.True(stagedAny);
            Assert.Equal(2, stagedPartial.Count);
            Assert.Equal(1, skippedPartial);

            IReadOnlyList<string> stagedAllFailed = DropStagingFileOps.StageExternalTempDropPathsWithOrigins(
                [oversized],
                hasShellIdList: false,
                out _,
                out bool stagedAnyAllFailed,
                out int skippedTotal,
                maxTotalBytes: 512);
            Assert.False(stagedAnyAllFailed);
            Assert.Empty(stagedAllFailed);
            Assert.Equal(1, skippedTotal);
        }
        finally
        {
            DropStagingFileOps.CleanupConsumedStagedPaths(stagedPartial);
            if (Directory.Exists(tempArchiveDir))
            {
                Directory.Delete(tempArchiveDir, recursive: true);
            }
        }
    }

    [Fact]
    public void ResolveShellIdListPostExtractionIntent_NeverUpgradesPromisedLinkOrCopyToMove()
    {
        // Hover & OLE receipt promised DROPEFFECT_LINK on a Shell IDList-only real filesystem item:
        // must execute Shortcut (never silently Move and delete the original!).
        (bool linkCopy, bool linkShortcut) = NativeDropTarget.ResolveShellIdListPostExtractionIntent(
            shellApplicationDrop: false,
            hasShellTemporaryLinks: false,
            hasShellTemporaryStreams: false,
            rightButtonDrag: false,
            feedbackEffect: NativeDropEffectPolicy.Link,
            defaultCopyRequested: false,
            keyState: 0,
            defaultMove: true,
            followWindows: true,
            sameVolume: true,
            sourceCanCopy: true,
            sourceCanMove: true,
            protectDefaultMoveAsCopy: false);
        Assert.True(linkCopy);
        Assert.True(linkShortcut);

        // Hover promised DROPEFFECT_COPY: must stay Copy (never silently Move).
        (bool copyOnly, bool copyShortcut) = NativeDropTarget.ResolveShellIdListPostExtractionIntent(
            shellApplicationDrop: false,
            hasShellTemporaryLinks: false,
            hasShellTemporaryStreams: false,
            rightButtonDrag: false,
            feedbackEffect: NativeDropEffectPolicy.Copy,
            defaultCopyRequested: false,
            keyState: 0,
            defaultMove: true,
            followWindows: true,
            sameVolume: true,
            sourceCanCopy: true,
            sourceCanMove: true,
            protectDefaultMoveAsCopy: false);
        Assert.True(copyOnly);
        Assert.False(copyShortcut);
    }

    [Fact]
    public void ExtractVirtualFiles_DoesNotEmitDirectoryWhenAllChildrenFail_AndReportsPartialSkippedCount()
    {
        ushort descriptorFormatId = Win32Helper.GetRegisteredClipboardFormat("FileGroupDescriptorW");
        ushort contentsFormatId = Win32Helper.GetRegisteredClipboardFormat("FileContents");
        const uint FdAttributes = 0x00000004;
        const uint FdFileSize = 0x00000040;
        const uint FileAttrDir = 0x10;

        // Case 1: Directory descriptor + single child whose stream fails -> 0 paths, skipped >= 1.
        var dirWithFailedChild = new NativeDropTarget.FILEDESCRIPTORW[]
        {
            NativeDropTarget.FILEDESCRIPTORW.Create("BrokenFolder", dwFlags: FdAttributes, dwFileAttributes: FileAttrDir),
            NativeDropTarget.FILEDESCRIPTORW.Create(@"BrokenFolder\missing.txt", dwFlags: FdFileSize, nFileSizeLow: 10),
        };
        using (var fake = CreateVirtualDataObject(
            descriptorFormatId,
            contentsFormatId,
            dirWithFailedChild,
            fileContentsByIndex: new Dictionary<int, byte[]>()))
        {
            IReadOnlyList<string> paths = NativeDropTarget.ExtractVirtualFiles(
                new NativeOleDataObject(fake.Pointer),
                out int skippedCount,
                out bool budgetExceeded);
            Assert.Empty(paths);
            Assert.False(budgetExceeded);
            Assert.Equal(1, skippedCount);
        }

        // Case 2: 2 files where index 0 succeeds and index 1 fails -> 1 path kept + skipped == 1.
        var partialFiles = new NativeDropTarget.FILEDESCRIPTORW[]
        {
            NativeDropTarget.FILEDESCRIPTORW.Create("ok.txt", dwFlags: FdFileSize, nFileSizeLow: 5),
            NativeDropTarget.FILEDESCRIPTORW.Create("broken.txt", dwFlags: FdFileSize, nFileSizeLow: 5),
        };
        IReadOnlyList<string> extractedPartial = [];
        try
        {
            using var fake = CreateVirtualDataObject(
                descriptorFormatId,
                contentsFormatId,
                partialFiles,
                fileContentsByIndex: new Dictionary<int, byte[]> { [0] = "hello"u8.ToArray() });
            extractedPartial = NativeDropTarget.ExtractVirtualFiles(
                new NativeOleDataObject(fake.Pointer),
                out int skippedCount,
                out bool budgetExceeded);
            string okPath = Assert.Single(extractedPartial);
            Assert.False(budgetExceeded);
            Assert.Equal(1, skippedCount);
            Assert.Equal("hello", File.ReadAllText(okPath));
        }
        finally
        {
            DropStagingFileOps.CleanupConsumedStagedPaths(extractedPartial);
        }

        // Case 3: Genuine empty directory (no failed children) is still accepted.
        var emptyDirOnly = new NativeDropTarget.FILEDESCRIPTORW[]
        {
            NativeDropTarget.FILEDESCRIPTORW.Create("EmptyFolder", dwFlags: FdAttributes, dwFileAttributes: FileAttrDir),
        };
        IReadOnlyList<string> extractedEmptyDir = [];
        try
        {
            using var fake = CreateVirtualDataObject(
                descriptorFormatId,
                contentsFormatId,
                emptyDirOnly,
                fileContentsByIndex: new Dictionary<int, byte[]>());
            extractedEmptyDir = NativeDropTarget.ExtractVirtualFiles(
                new NativeOleDataObject(fake.Pointer),
                out int skippedCount,
                out bool budgetExceeded);
            string dirPath = Assert.Single(extractedEmptyDir);
            Assert.False(budgetExceeded);
            Assert.Equal(0, skippedCount);
            Assert.True(Directory.Exists(dirPath));
        }
        finally
        {
            DropStagingFileOps.CleanupConsumedStagedPaths(extractedEmptyDir);
        }
    }

    private static FakeComStream CreateVirtualDataObject(
        ushort descriptorFormatId,
        ushort contentsFormatId,
        NativeDropTarget.FILEDESCRIPTORW[] descriptors,
        IReadOnlyDictionary<int, byte[]> fileContentsByIndex)
    {
        var fake = new FakeComStream(slotCount: 6);
        GetDataCallback getData = (nint _, ref NativeFormatEtc format, out NativeStorageMedium medium) =>
        {
            medium = default;
            if (format.ClipboardFormat == descriptorFormatId && (format.MediumType & 1u) != 0)
            {
                int descriptorSize = Unsafe.SizeOf<NativeDropTarget.FILEDESCRIPTORW>();
                byte[] bytes = new byte[sizeof(uint) + (descriptors.Length * descriptorSize)];
                BitConverter.TryWriteBytes(bytes, descriptors.Length);
                MemoryMarshal.AsBytes(descriptors.AsSpan()).CopyTo(bytes.AsSpan(sizeof(uint)));

                if (!Win32Helper.TryCreateGlobalMemory(bytes, out nint hGlobal))
                {
                    return unchecked((int)0x8007000E);
                }

                medium = new NativeStorageMedium { MediumType = 1u, Content = hGlobal, ReleaseUnknown = 0 };
                return 0;
            }

            if (format.ClipboardFormat == contentsFormatId &&
                (format.MediumType & 1u) != 0 &&
                fileContentsByIndex.TryGetValue(format.Index, out byte[]? payload))
            {
                if (!Win32Helper.TryCreateGlobalMemory(payload, out nint hGlobal))
                {
                    return unchecked((int)0x8007000E);
                }

                medium = new NativeStorageMedium { MediumType = 1u, Content = hGlobal, ReleaseUnknown = 0 };
                return 0;
            }

            return unchecked((int)0x80040064); // DV_E_FORMATETC
        };
        fake.SetSlot(3, getData);
        return fake;
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetDataCallback(
        nint self,
        ref NativeFormatEtc format,
        out NativeStorageMedium medium);

    [Fact]
    public async Task FileDragSourceGuardDataObject_HandlesNullPointersUnsetPreferredAndStaFinalizerRelease()
    {
        const int E_POINTER = unchecked((int)0x80004003);
        const int DV_E_FORMATETC = unchecked((int)0x80040064);

        string filePath = Path.Combine(_sandbox, "guard.txt");
        File.WriteAllText(filePath, "hello");
        nint inner = NativeShellFileDragProvider.CreateShellDataObject([filePath]);
        nint guard = FileDragSourceGuardDataObject.CreateInterfacePointer(inner);
        try
        {
            var direct = new FileDragSourceGuardDataObject(inner, ownerDispatcher: null);
            Assert.Equal(E_POINTER, direct.GetData(0, 0));
            Assert.Equal(E_POINTER, direct.GetDataHere(0, 0));
            Assert.Equal(E_POINTER, direct.QueryGetData(0));

            var preferredFormat = new NativeFormatEtc
            {
                ClipboardFormat = Win32Helper.GetRegisteredClipboardFormat("Preferred DropEffect"),
                Aspect = 1,
                Index = -1,
                MediumType = 1,
            };
            var receiver = new NativeOleDataObject(guard);
            Assert.Equal(DV_E_FORMATETC, receiver.QueryGetData(ref preferredFormat));
            Assert.Equal(DV_E_FORMATETC, receiver.GetData(ref preferredFormat, out _));
        }
        finally
        {
            FileDragSourceGuardDataObject.ReleaseInterfacePointer(guard);
            NativeShellFileDragProvider.ReleaseInterface(inner);
        }

        // Verify finalizer marshals inner COM Release onto the owner STA DispatcherQueue thread!
        DispatcherQueueController controller = DispatcherQueueController.CreateOnDedicatedThread();
        // Root the CsWinRT DispatcherQueue RCW (mirroring App.UiDispatcherQueue in production)
        // so GC.Collect() cannot finalize its IObjectReference ahead of ~FileDragSourceGuardDataObject().
        DispatcherQueue ownerQueue = controller.DispatcherQueue;
        int staThreadId = 0;
        var releaseThreadId = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var fakeInner = new FakeComStream(slotCount: 4);
        AddRefCallback addRefCb = _ => 2u;
        ReleaseCallback releaseCb = _ =>
        {
            releaseThreadId.TrySetResult(Environment.CurrentManagedThreadId);
            return 1u;
        };
        fakeInner.SetSlot(1, addRefCb);
        fakeInner.SetSlot(2, releaseCb);

        try
        {
            var created = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            Assert.True(ownerQueue.TryEnqueue(() =>
            {
                staThreadId = Environment.CurrentManagedThreadId;
                _ = new FileDragSourceGuardDataObject(fakeInner.Pointer, ownerQueue);
                created.TrySetResult(true);
            }));
            await created.Task.WaitAsync(TimeSpan.FromSeconds(5));

            for (int attempt = 0; attempt < 10 && !releaseThreadId.Task.IsCompleted; attempt++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                await Task.Delay(25);
            }

            int actualReleaseThread = await releaseThreadId.Task.WaitAsync(TimeSpan.FromSeconds(5));
            GC.KeepAlive(ownerQueue);
            Assert.Equal(staThreadId, actualReleaseThread);
        }
        finally
        {
            await controller.ShutdownQueueAsync();
        }
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int QueryInterfaceCallback(nint self, ref Guid riid, out nint ppv);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate uint AddRefCallback(nint self);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate uint ReleaseCallback(nint self);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int ReadCallback(nint self, nint buffer, uint bufferSize, nint bytesRead);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int SeekCallback(nint self, long dlibMove, uint dwOrigin, out ulong plibNewPosition);

    private sealed class FakeComStream : IDisposable
    {
        private readonly List<Delegate> _callbacks = [];
        private readonly nint _vtable;

        public FakeComStream(int slotCount)
        {
            _vtable = Marshal.AllocHGlobal(checked(slotCount * IntPtr.Size));
            Pointer = Marshal.AllocHGlobal(IntPtr.Size);
            for (int i = 0; i < slotCount; i++)
            {
                Marshal.WriteIntPtr(_vtable, i * IntPtr.Size, IntPtr.Zero);
            }

            Marshal.WriteIntPtr(Pointer, _vtable);
        }

        public nint Pointer { get; }

        public void SetSlot<TDelegate>(int slot, TDelegate callback) where TDelegate : Delegate
        {
            _callbacks.Add(callback);
            Marshal.WriteIntPtr(_vtable, checked(slot * IntPtr.Size), Marshal.GetFunctionPointerForDelegate(callback));
        }

        public void Dispose()
        {
            Marshal.FreeHGlobal(Pointer);
            Marshal.FreeHGlobal(_vtable);
            GC.KeepAlive(_callbacks);
        }
    }
}
