using System.Buffers.Binary;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using DeskBox.Platform;
using DeskBox.Services;
using DeskBox.Views;
using Microsoft.UI.Dispatching;

namespace DeskBox.Tests;

/// <summary>
/// Behavioral and runtime contract tests for the 1.5.9 callback-isolation batch
/// (drag-freeze family): the PostAfterCallback dispatch primitive, the
/// callback-depth radar, virtual drop temp directory cleanup, and P/Invoke
/// entry-point resolution.
/// </summary>
public sealed class CallbackDeferralContractTests : IDisposable
{
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

    [Fact]
    public void CleanupVirtualDropDirectories_DeletesOnlyInsideOwnedTempRoot()
    {
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

    // ── Callback-isolation exemptions (⑥ & ⑦) IL anti-regression contracts ───

    [Fact]
    public void ContentWindowActivated_ReassertStaysSynchronous()
    {
        MethodInfo? handler = typeof(ContentWidgetWindow).GetMethod(
            "ContentWidgetWindow_Activated",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(handler);
        Assert.Null(handler!.GetCustomAttribute<AsyncStateMachineAttribute>());

        List<MethodBase> calls = ReadDirectIlCalls(handler);
        Assert.Contains(calls, static m =>
            m.DeclaringType == typeof(WidgetManager) &&
            m.Name == "ReassertRaisedWidgetGroupAfterDeskBoxActivation");
        Assert.DoesNotContain(calls, static m =>
            m.DeclaringType == typeof(UiDispatch) &&
            m.Name == nameof(UiDispatch.PostAfterCallback));
    }

    [Fact]
    public void HudActionButtonClick_ForegroundClaimStaysSynchronous()
    {
        MethodInfo? handler = typeof(DesktopToastHudWindow).GetMethod(
            "ActionButton_Click",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(handler);

        var asyncAttr = handler!.GetCustomAttribute<AsyncStateMachineAttribute>();
        Assert.NotNull(asyncAttr);

        MethodInfo? moveNext = asyncAttr!.StateMachineType.GetMethod(
            "MoveNext",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(moveNext);

        List<MethodBase> calls = ReadDirectIlCalls(moveNext!);
        int setForegroundIdx = calls.FindIndex(static m =>
            m.DeclaringType == typeof(Win32Helper) &&
            m.Name == nameof(Win32Helper.SetForegroundWindow));
        int allowForegroundIdx = calls.FindIndex(static m =>
            m.DeclaringType == typeof(Win32Helper) &&
            m.Name == nameof(Win32Helper.AllowSetForegroundWindow));
        int firstAwaitIdx = calls.FindIndex(static m =>
            m.Name is "AwaitUnsafeOnCompleted" or "AwaitOnCompleted");
        int postAfterIdx = calls.FindIndex(static m =>
            m.DeclaringType == typeof(UiDispatch) &&
            m.Name == nameof(UiDispatch.PostAfterCallback));

        Assert.True(setForegroundIdx >= 0, "ActionButton_Click must claim foreground via Win32Helper.SetForegroundWindow.");
        Assert.True(allowForegroundIdx >= 0, "ActionButton_Click must grant foreground via Win32Helper.AllowSetForegroundWindow.");
        Assert.True(firstAwaitIdx >= 0, "Expected ActionButton_Click state machine to await the action callback.");
        Assert.True(
            setForegroundIdx < firstAwaitIdx && allowForegroundIdx < firstAwaitIdx,
            "Exemption 7 requires SetForegroundWindow and AllowSetForegroundWindow to run synchronously before awaiting callback.");
        if (postAfterIdx >= 0)
        {
            Assert.True(
                setForegroundIdx < postAfterIdx && allowForegroundIdx < postAfterIdx,
                "Exemption 7 forbids deferring SetForegroundWindow / AllowSetForegroundWindow behind PostAfterCallback.");
        }
    }

    private static readonly (OpCode[] SingleByte, OpCode[] TwoByte) s_opCodeTables = BuildOpCodeTables();

    private static (OpCode[] SingleByte, OpCode[] TwoByte) BuildOpCodeTables()
    {
        var singleByte = new OpCode[256];
        var twoByte = new OpCode[256];
        foreach (FieldInfo field in typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static))
        {
            if (field.FieldType != typeof(OpCode))
            {
                continue;
            }

            var op = (OpCode)field.GetValue(null)!;
            ushort value = unchecked((ushort)op.Value);
            if ((value & 0xFF00) == 0xFE00)
            {
                twoByte[value & 0xFF] = op;
            }
            else if (value < 0x100)
            {
                singleByte[value] = op;
            }
        }

        return (singleByte, twoByte);
    }

    private static List<MethodBase> ReadDirectIlCalls(MethodBase method)
    {
        var calls = new List<MethodBase>();
        MethodBody? body = method.GetMethodBody();
        byte[]? il = body?.GetILAsByteArray();
        if (il is not { Length: > 0 })
        {
            return calls;
        }

        Type? declaringType = method.DeclaringType;
        Type[] genericTypeArgs = declaringType is { IsGenericType: true }
            ? declaringType.GetGenericArguments()
            : Type.EmptyTypes;
        Type[] genericMethodArgs = method is MethodInfo { IsGenericMethod: true } mi
            ? mi.GetGenericArguments()
            : Type.EmptyTypes;

        int offset = 0;
        while (offset < il.Length)
        {
            byte b = il[offset++];
            OpCode op;
            if (b == 0xFE)
            {
                if (offset >= il.Length)
                {
                    break;
                }

                op = s_opCodeTables.TwoByte[il[offset++]];
            }
            else
            {
                op = s_opCodeTables.SingleByte[b];
            }

            switch (op.OperandType)
            {
                case OperandType.InlineNone:
                    break;

                case OperandType.ShortInlineBrTarget:
                case OperandType.ShortInlineI:
                case OperandType.ShortInlineVar:
                    offset += 1;
                    break;

                case OperandType.InlineVar:
                    offset += 2;
                    break;

                case OperandType.InlineMethod:
                    if ((op == OpCodes.Call || op == OpCodes.Callvirt || op == OpCodes.Newobj) &&
                        offset + 4 <= il.Length)
                    {
                        int metadataToken = BinaryPrimitives.ReadInt32LittleEndian(il.AsSpan(offset, 4));
                        try
                        {
                            MethodBase? target = method.Module.ResolveMethod(
                                metadataToken,
                                genericTypeArgs,
                                genericMethodArgs);
                            if (target is not null)
                            {
                                calls.Add(target);
                            }
                        }
                        catch
                        {
                        }
                    }

                    offset += 4;
                    break;

                case OperandType.InlineBrTarget:
                case OperandType.InlineField:
                case OperandType.InlineI:
                case OperandType.InlineSig:
                case OperandType.InlineString:
                case OperandType.InlineTok:
                case OperandType.InlineType:
                case OperandType.ShortInlineR:
                    offset += 4;
                    break;

                case OperandType.InlineI8:
                case OperandType.InlineR:
                    offset += 8;
                    break;

                case OperandType.InlineSwitch:
                    if (offset + 4 > il.Length)
                    {
                        return calls;
                    }

                    int targetCount = BinaryPrimitives.ReadInt32LittleEndian(il.AsSpan(offset, 4));
                    offset += 4 + (targetCount * 4);
                    break;

                default:
                    return calls;
            }
        }

        return calls;
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
