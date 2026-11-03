using DeskBox.Platform;
using Microsoft.UI.Dispatching;

namespace DeskBox.Tests;

/// <summary>
/// Contract tests for the 1.5.9 callback-isolation batch (drag-freeze
/// family): the PostAfterCallback dispatch primitive, the callback-depth
/// radar, and slice pins that keep every deferral site routed through the
/// primitive with its landing guards. Full invariant text lives in
/// file_drag_stack_contract.md §9.23.
/// </summary>
public sealed class CallbackDeferralContractTests : IDisposable
{
    // Same CI-host limitation as UiDispatchTests: no WinRT classes are
    // registered there, so real-dispatcher probes early-out.
    private static readonly bool s_realDispatcherQueueAvailable = ProbeRealDispatcherQueue();

    public CallbackDeferralContractTests()
    {
        UiDispatch.ResetForTests();
        CallbackDepth.ResetForTests();
    }

    public void Dispose()
    {
        UiDispatch.ResetForTests();
        CallbackDepth.ResetForTests();
    }

    // ── UiDispatch.PostAfterCallback ───────────────────────────────────

    [Fact]
    public void PostAfterCallback_TestHostInline_RunsSynchronously()
    {
        // Test-host shape (no origin, no queue) keeps the legacy inline
        // fiction, same as RunOrDefer.
        bool ran = false;
        UiDispatch.PostAfterCallback(() => ran = true);

        Assert.True(ran);
        Assert.Equal(0, UiDispatch.PostAfterCallbackDroppedCount);
    }

    [Fact]
    public async Task PostAfterCallback_OnDispatcherThread_NeverRunsInline()
    {
        if (!s_realDispatcherQueueAvailable)
        {
            return;
        }

        using var thread = new DispatcherQueueThread();
        bool queuedRan = false;
        await thread.RunOnQueueAsync(() =>
        {
            UiDispatch.MarkReady(thread.Queue);
            UiDispatch.PostAfterCallback(() => queuedRan = true);
            // Still inside the dispatcher callback that posted the item: the
            // core contract is that PostAfterCallback leaves the current
            // frame even when the caller already owns the dispatcher thread.
            Assert.False(queuedRan);
        });

        // The dedicated pump drains the posted item before this second
        // enqueued action observes it.
        await thread.RunOnQueueAsync(() => { });
        Assert.True(queuedRan);
    }

    [Fact]
    public void PostAfterCallback_AfterShutdown_DropsAndCounts()
    {
        UiDispatch.MarkUiOriginThread();
        UiDispatch.MarkShutdown();

        bool ran = false;
        UiDispatch.PostAfterCallback(() => ran = true);

        Assert.False(ran);
        Assert.Equal(1, UiDispatch.PostAfterCallbackDroppedCount);
    }

    // ── CallbackDepth radar ────────────────────────────────────────────

    [Fact]
    public void CallbackDepth_EntersExitsAndClampsAtZero()
    {
        Assert.Equal(0, CallbackDepth.Depth);
        CallbackDepth.Enter();
        CallbackDepth.Enter();
        Assert.Equal(2, CallbackDepth.Depth);
        CallbackDepth.Exit();
        CallbackDepth.Exit();
        // An unbalanced exit must never drive the depth negative and silence
        // the radar for the rest of the session.
        CallbackDepth.Exit();
        Assert.Equal(0, CallbackDepth.Depth);
    }

    [Fact]
    public void CallbackDepth_RecordsWindowOperationsPerCaller()
    {
        CallbackDepth.RecordWindowOperation("op-a");
        CallbackDepth.RecordWindowOperation("op-a");
        CallbackDepth.RecordWindowOperation("op-b");

        IReadOnlyDictionary<string, long> counts =
            CallbackDepth.SnapshotWindowOperationCounts();

        Assert.Equal(2, counts["op-a"]);
        Assert.Equal(1, counts["op-b"]);
    }

    // ── Slice pins for the deferral chain ──────────────────────────────

    [Fact]
    public void TrayMenu_OpensDeferredWithSyncCursorCapture()
    {
        string tray = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/App.Tray.cs"));
        string entry = SliceMethod(
            tray,
            "private void ShowTrayContextMenuFromTray()",
            "private void ShowTrayContextMenuAt");
        // The click anchor is captured in the callback frame; the menu open
        // leaves it.
        Assert.Contains("Win32Helper.GetCursorPos", entry, StringComparison.Ordinal);
        Assert.Contains("UiDispatch.PostAfterCallback", entry, StringComparison.Ordinal);

        string landing = SliceMethod(
            tray,
            "private void ShowTrayContextMenuAt",
            "internal void ShowTrayContextMenuForOnboarding");
        Assert.Contains("trayHost.ShowContextMenu(", landing, StringComparison.Ordinal);
        Assert.DoesNotContain("_trayHost.ShowContextMenu(", landing, StringComparison.Ordinal);

        string toggle = SliceMethod(
            tray,
            "private async Task ToggleTrayWidgetsAsync",
            "private async Task ToggleWidgetsFromDesktopDoubleClickAsync");
        Assert.Contains("await Task.Yield();", toggle, StringComparison.Ordinal);
    }

    [Fact]
    public void NativeLaunchDrop_ClaimsSynchronouslyAndDefersProcessStart()
    {
        string source = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Views/ContentWidgetWindow.NativeDragDrop.cs"));
        string claim = SliceMethod(
            source,
            "private ShellDropLaunchResult HandleNativeLaunchDrop(",
            "private void LaunchDroppedFilesDeferred(");
        // The gesture claim must stay inside the OLE callback frame (the
        // legacy WM_DROPFILES race), and the effect is committed
        // optimistically as Link.
        Assert.Contains("MarkLaunchConsumedForCurrentContent();", claim, StringComparison.Ordinal);
        Assert.Contains("UiDispatch.PostAfterCallback", claim, StringComparison.Ordinal);
        Assert.Contains(
            "ShellDropLaunchResult.Launched(NativeDropEffectPolicy.Link, 0)",
            claim,
            StringComparison.Ordinal);

        string deferred = SliceMethod(
            source,
            "private void LaunchDroppedFilesDeferred(",
            "private void MarkLaunchConsumedForCurrentContent");
        // Only paths that exist on disk reach the command line (synthetic
        // CF_HDROP argument injection, security review 2026-10-14).
        Assert.Contains("File.Exists", deferred, StringComparison.Ordinal);
        Assert.Contains("ShowShortcutLaunchRefusedFeedback", deferred, StringComparison.Ordinal);
        Assert.Contains("CleanupNativeTemporaryDropFiles", deferred, StringComparison.Ordinal);
    }

    [Fact]
    public void AsyncDropCapability_PinsSdkFacts()
    {
        // The IID and the vtable order were verified against Windows SDK
        // 10.0.26100.0 um/shldisp.h:8482-8502 (MIDL declaration order:
        // SetAsyncMode, GetAsyncMode, StartOperation, InOperation,
        // EndOperation). A wrong IID silently disables the whole async
        // protocol (QI E_NOINTERFACE); a wrong StartOperation slot calls
        // InOperation with a null out-pointer. Both facts are pinned here so
        // neither can regress unnoticed.
        string source = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Helpers/NativeDropAsyncCapability.cs"));
        Assert.Contains("3D8B0590-F691-11d2-8EA9-006097DF5BD4", source, StringComparison.Ordinal);
        Assert.Contains("GetAsyncModeVtableSlot = 4", source, StringComparison.Ordinal);
        Assert.Contains("StartOperationVtableSlot = 5", source, StringComparison.Ordinal);
        Assert.Contains("EndOperationVtableSlot = 7", source, StringComparison.Ordinal);
    }

    [Fact]
    public void VirtualDrop_FollowsAsyncProtocolWithWorkerHandoff()
    {
        string source = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Helpers/NativeDropTarget.cs"));
        string dropCore = SliceMethod(
            source,
            "private int OnDropCore(",
            "private int HandleDeferredDropMaterialization(");
        Assert.Contains("HandleDeferredDropMaterialization(", dropCore, StringComparison.Ordinal);
        // All deferred flavors: virtual streams, shell-application links,
        // and materializable Shell namespace objects.
        Assert.Contains(
            "(virtualFileDrop || shellApplicationDrop || shellNamespaceDrop)",
            dropCore,
            StringComparison.Ordinal);

        string handler = SliceMethod(
            source,
            "private int HandleDeferredDropMaterialization(",
            "private static void EndAsyncOperationIfStarted");
        // StartOperation happens before the callback returns — and before the
        // worker enqueue, otherwise the protocol handshake is lost.
        Assert.Contains("IsImportBusy()", handler, StringComparison.Ordinal);
        Assert.Contains("capability.StartOperation()", handler, StringComparison.Ordinal);
        Assert.Contains("CoMarshalInterThreadInterfaceInStream", handler, StringComparison.Ordinal);
        Assert.Contains("NativeDropMaterializationWorker.TryEnqueue", handler, StringComparison.Ordinal);
        Assert.True(
            handler.IndexOf("capability.StartOperation()", StringComparison.Ordinal) <
            handler.IndexOf("NativeDropMaterializationWorker.TryEnqueue", StringComparison.Ordinal),
            "StartOperation must precede the worker enqueue.");
        // A full queue refuses synchronously with a none receipt.
        Assert.Contains("materialization queue ", handler, StringComparison.Ordinal);
        // Shell-object extraction (AppsFolder + namespace items) stays on the
        // UI STA (shell namespace APIs fail on the MTA worker — 2026-10-15
        // field incident): its branch AddRefs the raw pointer and defers one
        // turn instead of marshaling to the worker.
        Assert.Contains("Marshal.AddRef(dataObject)", handler, StringComparison.Ordinal);
        Assert.Contains(
            "TryExtractShellIdListItems(dataObject)",
            handler,
            StringComparison.Ordinal);
        Assert.Contains("Marshal.Release(dataObject)", handler, StringComparison.Ordinal);
        Assert.True(
            handler.IndexOf(
                "if (shellApplicationDrop || shellNamespaceDrop)",
                StringComparison.Ordinal) <
            handler.IndexOf("CoMarshalInterThreadInterfaceInStream", StringComparison.Ordinal),
            "The shell-object branch must run before the virtual marshal path.");

        string completion = SliceMethod(
            source,
            "private void OnDeferredMaterializationCompleted(",
            "private bool GetFollowWindowsSetting");
        Assert.Contains("!shellApplicationDrop", completion, StringComparison.Ordinal);
        Assert.Contains("LaunchDropHandler", completion, StringComparison.Ordinal);
        Assert.Contains("IsImportBusy()", completion, StringComparison.Ordinal);
        // A refusal must not orphan the materialized temp files.
        Assert.Contains("CleanupVirtualDropDirectories", completion, StringComparison.Ordinal);
        Assert.Contains("DropIntentEvent?.Invoke", completion, StringComparison.Ordinal);

        string worker = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Helpers/NativeDropMaterializationWorker.cs"));
        // Single MTA thread owns the whole COM chain: init, unmarshal,
        // receipt, EndOperation — in that order.
        Assert.Contains("CoInitializeEx", worker, StringComparison.Ordinal);
        Assert.Contains("CoGetInterfaceAndReleaseStream", worker, StringComparison.Ordinal);
        Assert.Contains("WritePerformedDropEffect", worker, StringComparison.Ordinal);
        Assert.Contains("EndOperation", worker, StringComparison.Ordinal);
        Assert.Contains("UiDispatch.PostAfterCallback", worker, StringComparison.Ordinal);
        // Bounded queue: a hung source must turn into a synchronous refusal,
        // not an unbounded pile of optimistically receipted drops.
        Assert.Contains("MaxQueuedJobs", worker, StringComparison.Ordinal);
        Assert.Contains("TryAdd", worker, StringComparison.Ordinal);
        Assert.True(
            worker.IndexOf("WritePerformedDropEffect(dataObject, finalEffect)", StringComparison.Ordinal) <
            worker.IndexOf("EndOperation(operationResult, finalEffect)", StringComparison.Ordinal),
            "The receipt must be written before EndOperation notifies the source.");
    }

    [Fact]
    public void SearchExecution_DefersShellWorkOutOfInputCallbacks()
    {
        string source = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/ViewModels/SearchPopupViewModel.cs"));
        string execute = SliceMethod(
            source,
            "public bool ExecuteItem(SearchResultItem item)",
            "public bool OpenSelectedLocation()");
        Assert.Contains("UiDispatch.PostAfterCallback", execute, StringComparison.Ordinal);

        string location = SliceMethod(
            source,
            "public bool OpenSelectedLocation()",
            "private void CommitExecution(");
        Assert.Contains("UiDispatch.PostAfterCallback", location, StringComparison.Ordinal);
        Assert.Contains("explorer.exe", location, StringComparison.Ordinal);
    }

    [Fact]
    public void Radar_TracksWindowMutationsThroughWrappedPInvokes()
    {
        string win32 = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Platform/Win32Helper.cs"));
        foreach (string native in new[]
        {
            "private static partial bool SetWindowPosNative",
            "private static partial bool EndDeferWindowPosNative",
            "private static partial bool ShowWindowNative",
            "private static partial bool SetForegroundWindowNative",
            "private static partial IntPtr SetWindowLongPtrNative",
        })
        {
            Assert.Contains(native, win32, StringComparison.Ordinal);
        }

        foreach (string wrapper in new[]
        {
            "public static bool SetWindowPos(",
            "public static bool EndDeferWindowPos(",
            "public static bool ShowWindow(",
            "public static bool SetForegroundWindow(",
            "public static IntPtr SetWindowLongPtr(",
        })
        {
            Assert.Contains(wrapper, win32, StringComparison.Ordinal);
        }

        Assert.Contains("CallbackDepth.Depth > 0", win32, StringComparison.Ordinal);
        // Removing the record call from a wrapper body would silence the
        // radar while keeping every other pin green — tripwire count.
        Assert.Equal(
            5,
            CountOccurrences(win32, "CallbackDepth.RecordWindowOperation();"));
        // The first violation of each operation surfaces at the default log
        // level so field diagnostics see it without verbose enabled.
        Assert.Contains("App.Log(", win32.Substring(
            win32.IndexOf("CallbackDepth.Depth > 0", StringComparison.Ordinal)),
            StringComparison.Ordinal);

        int wrappedProcs = 0;
        int subclassInstalls = 0;
        foreach (string file in Directory.EnumerateFiles(
                     TestPaths.FromRepository("src/DeskBox"),
                     "*.cs",
                     SearchOption.AllDirectories))
        {
            string content = File.ReadAllText(file);
            wrappedProcs += CountOccurrences(content, "CallbackDepth.WrapSubclassProc(");
            subclassInstalls += CountOccurrences(content, "Win32Helper.SetWindowSubclass(");
        }

        // Equality ratchet, not just a floor: a future subclass install that
        // forgets the wrapper is the most likely regression shape, and a
        // floor count would stay green through it.
        Assert.True(
            wrappedProcs >= subclassInstalls,
            $"Expected every subclass install to route through the radar " +
            $"wrapper: installs={subclassInstalls}, wrapped={wrappedProcs}.");

        // The OLE drop-target methods carry their own depth bracketing.
        string dropTarget = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Helpers/NativeDropTarget.cs"));
        Assert.Contains("CallbackDepth.Enter();", dropTarget, StringComparison.Ordinal);
        Assert.True(
            CountOccurrences(dropTarget, "CallbackDepth.Enter();") >= 4,
            "All four IDropTarget methods must bracket with the radar.");
    }

    [Fact]
    public void Diagnostics_ExposeCallbackRadarSection()
    {
        string snapshot = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/App.DiagnosticsBundle.cs"));
        Assert.Contains(
            "CallbackDepth.SnapshotWindowOperationCounts()",
            snapshot,
            StringComparison.Ordinal);
        Assert.Contains("CallbackRadar: callbackRadar", snapshot, StringComparison.Ordinal);

        string service = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Services/DeskBoxDiagnosticsBundleService.cs"));
        Assert.Contains(
            "record DeskBoxCallbackRadarDiagnostic",
            service,
            StringComparison.Ordinal);
    }

    [Fact]
    public void DoDragDrop_StaysDeadInProductSource()
    {
        // The self-initiated DoDragDrop experiment is closed (contract
        // §8.1.1b). This ratchet is identifier-level on purpose: the
        // historical PlatformInteropExpectedViolations manifest is now an
        // empty hard-zero law that cannot take new entries, and the
        // DeskBox.Platform namespace is exempt from it.
        const string pattern =
            "(LibraryImport|DllImport)[\\s\\S]{0,160}DoDragDrop" +
            "|DoDragDrop[\\s\\S]{0,160}(LibraryImport|DllImport)";
        foreach (string file in Directory.EnumerateFiles(
                     TestPaths.FromRepository("src/DeskBox"),
                     "*.cs",
                     SearchOption.AllDirectories))
        {
            Assert.DoesNotMatch(pattern, File.ReadAllText(file));
        }
    }

    [Fact]
    public void CleanupVirtualDropDirectories_DeletesOnlyInsideOwnedTempRoot()
    {
        // This is the only code in the batch that recursively deletes
        // directories reachable from drop data; the whitelist behavior gets a
        // real test rather than a text pin.
        string ownedRoot = Path.Combine(
            Path.GetTempPath(),
            "DeskBox",
            "VirtualDrops",
            $"test-{Guid.NewGuid():N}");
        string ownedDir = Path.Combine(ownedRoot, "inner");
        Directory.CreateDirectory(ownedDir);
        string ownedFile = Path.Combine(ownedDir, "materialized.txt");
        File.WriteAllText(ownedFile, "x");

        string foreignDir = Path.Combine(
            Path.GetTempPath(),
            $"deskbox-test-foreign-{Guid.NewGuid():N}");
        Directory.CreateDirectory(foreignDir);
        string foreignFile = Path.Combine(foreignDir, "keep.txt");
        File.WriteAllText(foreignFile, "x");

        try
        {
            DeskBox.Helpers.NativeDropMaterializationWorker.CleanupVirtualDropDirectories(
                new[] { ownedFile, foreignFile });

            Assert.False(Directory.Exists(ownedDir));
            Assert.True(File.Exists(foreignFile));
        }
        finally
        {
            if (Directory.Exists(ownedRoot))
            {
                Directory.Delete(ownedRoot, recursive: true);
            }

            if (Directory.Exists(foreignDir))
            {
                Directory.Delete(foreignDir, recursive: true);
            }
        }
    }

    [Fact]
    public void TrackedWindowPInvokes_ResolveRealExports()
    {
        // 2026-10-15 incident: renaming the tracked P/Invokes to *Native
        // without EntryPoint= made every call throw EntryPointNotFoundException
        // at runtime — the whole unit suite stayed green because nothing ever
        // called them. Entry-point resolution happens on first call, so calling
        // each wrapper once with a null handle is the only CI-level proof that
        // the export names are real. All calls below fail gracefully with a
        // null window and have no side effects.
        Assert.False(Win32Helper.SetWindowPos(
            IntPtr.Zero,
            IntPtr.Zero,
            0, 0, 0, 0,
            Win32Helper.SWP_NOMOVE | Win32Helper.SWP_NOSIZE |
            Win32Helper.SWP_NOZORDER | Win32Helper.SWP_NOACTIVATE));
        Assert.False(Win32Helper.ShowWindow(IntPtr.Zero, 0));
        Assert.False(Win32Helper.SetForegroundWindow(IntPtr.Zero));
        Assert.False(Win32Helper.EndDeferWindowPos(IntPtr.Zero));
        _ = Win32Helper.SetWindowLongPtr(IntPtr.Zero, -20, IntPtr.Zero);
        Assert.False(Win32Helper.IsWindowInCurrentProcess(IntPtr.Zero));
        _ = Win32Helper.GetForegroundWindow();
        Assert.False(Win32Helper.TrySetProcessMemoryPriorityCore(IntPtr.Zero, lowPriority: false));
    }

    [Fact]
    public void ContentWindowActivated_ReassertStaysSynchronous()
    {
        // 2026-10-15 incident: deferring this reassert one dispatcher turn
        // turned its SetForegroundWindow(active) from a no-op (the window is
        // still the foreground while the handler runs) into a REAL foreground
        // switch against whichever sibling the raise batch activated next —
        // each switch raised another Activated event and the group entered a
        // 3ms activation ping-pong ring (1805 band-split transactions during
        // one raise). The call must stay synchronous.
        string source = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Views/ContentWidgetWindow.WindowInteraction.cs"));
        string handler = SliceMethod(
            source,
            "private void ContentWidgetWindow_Activated(",
            "private void QueueRestoreDesktopLayerIfForegroundLeavesDeskBox(");
        Assert.Contains(
            "ReassertRaisedWidgetGroupAfterDeskBoxActivation(",
            handler,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "PostAfterCallback",
            handler,
            StringComparison.Ordinal);
    }

    // ── helpers ────────────────────────────────────────────────────────

    private static int CountOccurrences(string source, string value)
    {
        int count = 0;
        int index = 0;
        while ((index = source.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }

        return count;
    }

    private static string SliceMethod(string source, string startMarker, string endMarker)
    {
        int start = source.IndexOf(startMarker, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Start marker not found: {startMarker}");
        int end = source.IndexOf(endMarker, start + startMarker.Length, StringComparison.Ordinal);
        Assert.True(end >= 0, $"End marker not found after start: {endMarker}");
        return source[start..end];
    }

    private static bool ProbeRealDispatcherQueue()
    {
        try
        {
            using var probe = new DispatcherQueueThread();
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private sealed class DispatcherQueueThread : IDisposable
    {
        private readonly DispatcherQueueController _controller =
            DispatcherQueueController.CreateOnDedicatedThread();

        public DispatcherQueue Queue => _controller.DispatcherQueue;

        public Task RunOnQueueAsync(Action action)
        {
            var completion = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            if (!Queue.TryEnqueue(() =>
                {
                    try
                    {
                        action();
                        completion.SetResult(true);
                    }
                    catch (Exception ex)
                    {
                        completion.SetException(ex);
                    }
                }))
            {
                completion.SetException(new InvalidOperationException(
                    "Failed to enqueue onto the dedicated dispatcher queue."));
            }

            return completion.Task;
        }

        public void Dispose()
        {
            _ = _controller.ShutdownQueueAsync();
        }
    }
}
